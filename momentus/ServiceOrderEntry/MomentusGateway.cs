using System.Globalization;
using Newtonsoft.Json;
using Ungerboeck.Api.Models.Errors;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Options;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Subjects.Emails;
using Ungerboeck.Api.Sdk;

namespace ServiceOrderEntry;

internal sealed record Candidate(ServiceOrdersModel Order, ExhibitorsModel Exhibitor);

internal sealed class MomentusGateway
{
    private readonly ApiClient client;
    private readonly CliOptions options;
    private readonly Action<TimeSpan> delay = Thread.Sleep;
    private int requests;
    public int RequestCount => requests;
    internal ProcessingJournal? Journal { get; set; }
    internal BillingConfiguration BillingConfiguration => options.Billing;

    // Offline tests supply a client whose HTTP transport cannot reach Momentus.
    internal MomentusGateway(CliOptions options, ApiClient client, Action<TimeSpan> delay)
    {
        this.options = options;
        this.client = client;
        this.delay = delay;
    }

    public MomentusGateway(CliOptions options)
    {
        this.options = options;
        var user = Environment.GetEnvironmentVariable("MOMENTUS_APIUSER")?.Trim() ?? "";
        var secret = Environment.GetEnvironmentVariable("MOMENTUS_SECRET")?.Trim() ?? "";
        var key = Environment.GetEnvironmentVariable("MOMENTUS_KEY")?.Trim() ?? "";
        var missing = new[] { ("MOMENTUS_APIUSER", user), ("MOMENTUS_SECRET", secret), ("MOMENTUS_KEY", key) }
            .Where(x => x.Item2.Length == 0).Select(x => x.Item1).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"Missing environment variables: {string.Join(", ", missing)}.");
        client = new ApiClient(new Jwt { UngerboeckURI = options.BaseUrl, APIUserID = user, Secret = secret, Key = key, AutoRefresh = new AutoRefresh() });
    }

    public IReadOnlyList<Candidate> FindCandidates(IReadOnlySet<int> enabledEvents)
    {
        var exhibitorFilter = $"ExhibitorStatus eq 35 and ExhibitorType eq 'ME'";
        if (options.ExhibitorId.HasValue) exhibitorFilter += $" and ExhibitorID eq {options.ExhibitorId.Value}";
        if (options.EventId.HasValue) exhibitorFilter += $" and Event eq {options.EventId.Value}";
        var exhibitors = SearchExhibitors(exhibitorFilter)
            .Where(x => Value(x.ExhibitorID) > 0 && Value(x.Event) > 0)
            .Where(x => EventScopeRules.IsAllowed(Value(x.Event), enabledEvents, options.ExhibitorId.HasValue))
            .Where(x => !ExhibitorCategoryRules.HasCode(x.ExhibitorCategory, ExhibitorCategoryRules.Hold))
            .ToList();
        var result = new List<Candidate>();
        foreach (var exhibitor in exhibitors)
        {
            var filter = $"Exhibitor eq {Value(exhibitor.ExhibitorID)} and Event eq {Value(exhibitor.Event)} and OrderStatus eq 'PC'";
            result.AddRange(SearchOrders(filter).Select(order => new Candidate(order, exhibitor)));
        }
        return result.OrderBy(x => Value(x.Order.Event)).ThenBy(x => Value(x.Order.OrderNumber)).ToList();
    }

    public ExhibitorsModel GetExhibitor(int id) => CallRead(() => client.Endpoints.Exhibitors.Get(options.OrganizationCode, id));
    public ServiceOrdersModel GetOrder(int number) => CallRead(() => client.Endpoints.ServiceOrders.Get(options.OrganizationCode, number));
    public AccountInfo GetAccount(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return RunRow.EmptyAccount;
        var model = CallRead(() => client.Endpoints.Accounts.Get(options.OrganizationCode, code.Trim()));
        return ToAccount(model);
    }

    public AllAccountsModel GetAccountModel(string code) => CallRead(() => client.Endpoints.Accounts.Get(options.OrganizationCode, code.Trim()));

    public IReadOnlyList<OrderItemInfo> GetOrderItems(int orderNumber)
    {
        var filter = $"OrderNumber eq {orderNumber}";
        var rows = SearchAll(search => client.Endpoints.ServiceOrderItems.Search(options.OrganizationCode, filter, search),
            client.Endpoints.ServiceOrderItems.NavigateSearchList, "ServiceOrderItems", filter,
            x => NumericIdentity(x.OrderLineNumber), nameof(ServiceOrderItemsModel.OrderLineNumber));
        return rows.Select(x => new OrderItemInfo(Value(x.OrderLineNumber), x.ResourceCode ?? "", x.Description ?? "", x.AltDesc ?? "")).ToList();
    }

    public IReadOnlyList<DocumentInfo> GetExhibitorContractPdfs(int exhibitorId)
    {
        return SearchDocuments($"Exhibitor eq {exhibitorId} and Category eq 'CON'").Select(ToDocument).ToList();
    }

    public IReadOnlyList<DocumentInfo> GetOrderContractPdfs(int orderNumber)
    {
        return SearchDocuments($"Order eq {orderNumber} and Category eq 'CON'").Select(ToDocument).ToList();
    }

    public IReadOnlyList<DocumentInfo> GetNearbyExhibitorPdfs(int exhibitorId, DateTime? orderDate)
    {
        if (!orderDate.HasValue) return [];
        return SearchDocuments($"Exhibitor eq {exhibitorId}")
            .Select(ToDocument)
            .Where(x => IsPdf(x) && x.EnteredOn.HasValue && Math.Abs((x.EnteredOn.Value.Date - orderDate.Value.Date).TotalDays) <= 1)
            .OrderBy(x => Math.Abs((x.EnteredOn!.Value - orderDate.Value).TotalMinutes))
            .ThenByDescending(x => x.SequenceNumber)
            .ToList();
    }

    public void CopyContractPdfToOrder(DocumentInfo source, ServiceOrdersModel order)
    {
        var orderNumber = Value(order.OrderNumber);
        var eventId = Value(order.Event);
        var functionId = Value(order.Function);
        var account = Clean(order.Account);
        if (orderNumber == 0 || eventId == 0 || functionId == 0 || account.Length == 0)
            throw new InvalidOperationException("Contract PDF copy requires order number, event, function, and account.");
        var data = Convert.ToBase64String(DownloadDocument(source));
        var model = new DocumentsModel
        {
            Organization = options.OrganizationCode,
            Type = source.Type,
            NewFileName = source.DocumentId.Length > 0 ? source.DocumentId : $"Contract-{source.SequenceNumber}.pdf",
            NewDocumentData = data,
            Description = source.Description,
            Category = "CON",
            Order = orderNumber,
            Event = eventId,
            Function = functionId,
            Account = account
        };
        var added = CallWrite("Copy contract document", $"org={options.OrganizationCode}; source={source.Type}/{source.SequenceNumber}; order={orderNumber}", model, () => client.Endpoints.Documents.Add(model),
            new() { ["sourceType"] = source.Type, ["sourceSequence"] = source.SequenceNumber.ToString(CultureInfo.InvariantCulture),
                ["sourceId"] = source.DocumentId, ["contentHash"] = BytesHash(Convert.FromBase64String(data)),
                ["baseline"] = string.Join(",", GetOrderContractPdfs(orderNumber).Select(x => x.SequenceNumber)) });
        if (Value(added.SequenceNumber) == 0) throw new UnknownWriteOutcomeException("Copy contract document", $"org={options.OrganizationCode}; source={source.Type}/{source.SequenceNumber}; order={orderNumber}", new InvalidDataException("Momentus did not return the copied document sequence number."));
    }

    public string ExtractPaymentSchedule(IEnumerable<DocumentInfo> documents)
    {
        foreach (var document in documents)
        {
            var result = PaymentScheduleExtractor.FromPdf(DownloadDocument(document));
            if (result.Length > 0) return result;
        }
        return "";
    }

    public IReadOnlyList<NotesModel> GetOrderSonNotes(int orderNumber)
    {
        var filter = $"Type eq 'OH' and OrderNumber eq {orderNumber} and Class eq 'SON'";
        return SearchAll(search => client.Endpoints.Notes.Search(options.OrganizationCode, filter, search),
            client.Endpoints.Notes.NavigateSearchList, "Notes", filter,
            x => NumericIdentity(x.SequenceNumber), nameof(NotesModel.SequenceNumber));
    }

    public string SavePaymentScheduleNote(int orderNumber, string text)
    {
        var notes = GetOrderSonNotes(orderNumber);
        if (notes.Count > 1) throw new InvalidOperationException("Multiple SON order notes exist; no note was changed.");
        var action = "ADDED";
        if (notes.Count == 0)
        {
            var model = new NotesModel
            {
                OrganizationCode = options.OrganizationCode,
                Type = USISDKConstants.NoteType.OrderNote,
                OrderNumber = orderNumber,
                Class = "SON",
                Title = "Payment Schedule",
                Text = text
            };
            CallWrite("Add payment schedule note", $"org={options.OrganizationCode}; order={orderNumber}; type=OH; class=SON", model, () => client.Endpoints.Notes.Add(model), new() { ["baseline"] = "" });
        }
        else if (PaymentScheduleExtractor.NormalizeForComparison(notes[0].PlainText) == PaymentScheduleExtractor.NormalizeForComparison(text))
        {
            return "ALREADY MATCHED";
        }
        else
        {
            var original = JsonConvert.SerializeObject(notes[0]);
            notes[0].Class = "SON";
            notes[0].Title = "Payment Schedule";
            notes[0].Text = text;
            CallWrite("Update payment schedule note", $"org={options.OrganizationCode}; order={orderNumber}; note={notes[0].SequenceNumber}", notes[0], () => client.Endpoints.Notes.Update(notes[0]), new() { ["original"] = original });
            action = "UPDATED";
        }

        var verified = GetOrderSonNotes(orderNumber);
        if (verified.Count != 1 || PaymentScheduleExtractor.NormalizeForComparison(verified[0].PlainText) != PaymentScheduleExtractor.NormalizeForComparison(text))
            throw new InvalidOperationException("SON Payment Schedule note readback did not match the Contract PDF text.");
        return action;
    }

    public bool ReadyEmailWasSent(int orderNumber)
    {
        if (File.Exists(ReadyEmailReceiptPath(orderNumber))) return true;
        return GetSavedEmails(orderNumber).Any(x =>
            TextRules.Same(x.Description, $"Ready for invoicing - Service Order {orderNumber}") ||
            TextRules.Same(x.Description, $"WAIT FOR SALES APPROVAL - Service Order {orderNumber}"));
    }

    public string SendReadyForInvoicingEmail(RunRow row, IEnumerable<DocumentInfo> documents)
    {
        if (ReadyEmailWasSent(row.OrderNumber)) return "ALREADY SENT";
        var subject = ReadyEmailBuilder.Subject(row);
        var model = new EmailsModel
        {
            Organization = options.OrganizationCode,
            EmailSubject = subject,
            HtmlText = ReadyEmailBuilder.Build(row, documents),
            SaveAsUngerboeckDocument = new DocumentsModel { Order = row.OrderNumber }
        };
        model.SendToAddresses.Add(new EmailAccountModel { EmailAddress = row.ReadyEmailRecipient });
        foreach (var document in documents)
        {
            var attachmentName = SafeAttachmentName(document.Description, document.SequenceNumber);
            model.Attachments.Add(new AttachmentModel
            {
                Description = attachmentName,
                FileName = document.DocumentId.Length > 0 ? document.DocumentId : $"Contract-{document.SequenceNumber}.pdf",
                FileData = Convert.ToBase64String(DownloadDocument(document))
            });
        }
        if (model.Attachments.Count == 0) throw new InvalidOperationException("Ready-for-invoicing email was not sent because no Contract PDFs were available.");
        CallWrite("Send ready email", $"org={options.OrganizationCode}; order={row.OrderNumber}; recipient={row.ReadyEmailRecipient}; subject={subject}", model, () => client.Endpoints.Emails.Send(model),
            new() { ["order"] = row.OrderNumber.ToString(CultureInfo.InvariantCulture), ["baseline"] = string.Join(",", GetSavedEmails(row.OrderNumber).Select(x => x.SequenceNumber)),
                ["bodyHash"] = OrderIdentity.Hash(model.HtmlText), ["attachmentHashes"] = string.Join(",", model.Attachments.Select(x => BytesHash(Convert.FromBase64String(x.FileData)))) });
        WriteReadyEmailReceipt(row.OrderNumber, row.ReadyEmailRecipient, subject);
        return "SENT";
    }

    private static string SafeAttachmentName(string description, int sequenceNumber)
    {
        var value = string.IsNullOrWhiteSpace(description) ? $"Contract-{sequenceNumber}" : description.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '-');
        return value.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }

    private string ReadyEmailReceiptPath(int orderNumber) => Path.Combine(options.StateFolder, $"ready-email-{OrderIdentity.Hash(new Uri(options.BaseUrl).AbsoluteUri.TrimEnd('/') + "|" + options.OrganizationCode)}-order-{orderNumber}.sent.json");
    private void WriteReadyEmailReceipt(int orderNumber, string recipient, string subject)
    {
        try
        {
            Directory.CreateDirectory(options.StateFolder);
            var path = ReadyEmailReceiptPath(orderNumber);
            var temp = path + $".{Guid.NewGuid():N}.tmp";
            FileJournalStore.DurableWrite(temp, System.Text.Json.JsonSerializer.Serialize(new { SentOn = DateTimeOffset.Now, OrderNumber = orderNumber, Recipient = recipient, Subject = subject }));
            File.Move(temp, path, true);
        }
        catch (Exception ex) { throw new JournalStorageException("Email was accepted and journaled, but receipt persistence failed; stop subsequent mutations.", ex); }
    }

    private byte[] DownloadDocument(DocumentInfo source)
    {
        var data = CallRead(() => client.Endpoints.Documents.Download(options.OrganizationCode, source.Type, source.SequenceNumber));
        if (string.IsNullOrWhiteSpace(data)) throw new InvalidOperationException($"Contract PDF {source.SequenceNumber} downloaded without file data.");
        try { return Convert.FromBase64String(data.Trim().Trim('"')); }
        catch (FormatException ex) { throw new InvalidDataException($"Contract PDF {source.SequenceNumber} download was not valid base64 data.", ex); }
    }

    public string GetBoothNumber(int exhibitorId, int eventId)
    {
        var filter = $"(Type eq 'BP' or Type eq 'DC') and ExhibitorID eq {exhibitorId} and Event eq {eventId}";
        var rows = SearchAll(search => client.Endpoints.Activities.Search(options.OrganizationCode, filter, search),
            client.Endpoints.Activities.NavigateSearchList, "Activities", filter,
            x => NumericIdentity(x.SequenceNumber), nameof(ActivitiesModel.EnteredOn), nameof(ActivitiesModel.SequenceNumber));
        var matches = rows.Select(x => new { Booth = BoothRules.Parse(x.PlainText), Entered = DateValue(x.EnteredOn), Sequence = Value(x.SequenceNumber) })
            .Where(x => x.Booth.Length > 0).OrderByDescending(x => x.Entered).ThenByDescending(x => x.Sequence).ToList();
        if (matches.Count == 0) return "";
        var latest = matches[0];
        return matches.Where(x => x.Entered == latest.Entered).Select(x => x.Booth).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? latest.Booth : "";
    }

    public IReadOnlyList<AccountInfo> FindContacts(string? parentAccount, string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new DecisionSearchException(SearchFailureKind.IdentityUnavailable, "Contact matching", 0, "No email identity is available for the required search.");
        var parentFilter = string.IsNullOrWhiteSpace(parentAccount) ? "" : $" and PrimaryAccount eq '{TextRules.OData(parentAccount.Trim())}'";
        // Complete contact index avoids relying on tenant OData email case/whitespace semantics.
        var filter = $"Class eq 'P'{parentFilter}";
        return SearchAccounts(filter).Where(x => TextRules.Same(x.Email, email) && (string.IsNullOrWhiteSpace(parentAccount) || TextRules.Same(x.PrimaryAccount, parentAccount)))
            .Select(ToAccount).OrderBy(x => x.AccountCode).ToList();
    }

    public IReadOnlyList<AccountInfo> FindOrganizationCandidates(BillingRequest request)
    {
        var company = TextRules.Clean(request.CompanyName);
        if (TextRules.NormalizeCompany(company).Length == 0)
            throw new DecisionSearchException(SearchFailureKind.IdentityUnavailable, "Organization identity", 0,
                "Company comparison has an empty identity; account matching is unavailable.");
        // Existing comparison ignores suffixes, spacing and punctuation. No selective
        // name filter covers all accepted variants. Require the bounded complete index.
        return SearchAccounts("Class eq 'O'").Select(ToAccount)
            .Where(x => TextRules.CompanyMatches(x.Company, company)).OrderBy(x => x.AccountCode).ToList();
    }

    public string CreateOrganizationAccount(BillingRequest request)
    {
        options.Billing.Validate();
        if (options.Billing.Selector(request) != "SEPARATE" || BillingRules.RequestedErrors(request, options.Billing).Count != 0 ||
            string.IsNullOrWhiteSpace(options.Billing.EventSalesNotApplicableCode))
            throw new InvalidOperationException("Bill-To creation requires valid explicit separate billing and configured Not Applicable Event Sales status.");
        var model = new AllAccountsModel
        {
            Organization = options.OrganizationCode,
            Class = "O",
            Name = request.CompanyName,
            EventSalesStatus = options.Billing.EventSalesNotApplicableCode,
            Address1 = request.Address,
            City = request.City,
            State = request.State,
            PostalCode = request.PostalCode,
            Country = TextRules.MomentusCountry(request.Country)
        };
        var added = CallWrite("Create organization account", $"org={options.OrganizationCode}; order={Journal?.Evidence.Identity.Order}", model, () => client.Endpoints.Accounts.Add(model));
        if (string.IsNullOrWhiteSpace(added.AccountCode)) throw new UnknownWriteOutcomeException("Create organization account", $"org={options.OrganizationCode}; company={request.CompanyName}", new InvalidDataException("Momentus did not return the created AccountCode."));
        return added.AccountCode.Trim();
    }

    public AccountInfo UpdateAccountAddress(string accountCode, BillingRequest request)
    {
        throw new RecoveryReviewException("REVIEW: shared account address-update authority is not established; existing account is preserved.");
    }

    public void EnsureRelationship(string masterAccount, string subordinateAccount, string relationshipType)
    {
        if (TextRules.Same(masterAccount, subordinateAccount)) return;
        var filter = $"MasterAccountCode eq '{TextRules.OData(masterAccount)}' and SubordinateAccountCode eq '{TextRules.OData(subordinateAccount)}' and RelationshipType eq '{TextRules.OData(relationshipType)}'";
        filter += $" and SubordinateOrganizationCode eq '{TextRules.OData(options.OrganizationCode)}'";
        var existing = SearchAll(search => client.Endpoints.Relationships.Search(options.OrganizationCode, filter, search),
            client.Endpoints.Relationships.NavigateSearchList, "Relationships", filter,
            x => new[] { x.MasterOrganizationCode, x.MasterAccountCode, x.SubordinateOrganizationCode, x.SubordinateAccountCode, x.RelationshipType }
                .All(v => !string.IsNullOrWhiteSpace(v))
                ? JsonConvert.SerializeObject(new[] { x.MasterOrganizationCode, x.MasterAccountCode, x.SubordinateOrganizationCode, x.SubordinateAccountCode, x.RelationshipType }) : "",
            nameof(RelationshipsModel.MasterAccountCode));
        if (existing.Any(x => TextRules.Same(x.MasterOrganizationCode, options.OrganizationCode) &&
            TextRules.Same(x.MasterAccountCode, masterAccount) && TextRules.Same(x.SubordinateOrganizationCode, options.OrganizationCode) &&
            TextRules.Same(x.SubordinateAccountCode, subordinateAccount) && TextRules.Same(x.RelationshipType, relationshipType))) return;
        if (existing.Count != 0) throw new DecisionSearchException(SearchFailureKind.MalformedResponse, filter, 0, "Exact relationship query returned a different composite identity.");
        var relationship = new RelationshipsModel
        {
            MasterOrganizationCode = options.OrganizationCode,
            MasterAccountCode = masterAccount,
            SubordinateOrganizationCode = options.OrganizationCode,
            SubordinateAccountCode = subordinateAccount,
            RelationshipType = relationshipType,
            // Bill-To links do not grant Event Sales participation to billing-only accounts.
            EventSalesDesignation = relationshipType == "BTO" ? null : "P"
        };
        CallWrite("Add relationship", $"org={options.OrganizationCode}; master={masterAccount}; subordinate={subordinateAccount}; type={relationshipType}", relationship, () => client.Endpoints.Relationships.Add(relationship));
        CallRead(() => client.Endpoints.Relationships.Get(options.OrganizationCode, masterAccount, subordinateAccount, relationshipType));
    }

    public string CreateContact(string parentAccount, string parentCompany, BillingRequest request)
    {
        if (options.Billing.Selector(request) != "SEPARATE" || BillingRules.RequestedErrors(request, options.Billing).Count != 0 || !BillingRules.ValidCode(parentAccount))
            throw new InvalidOperationException("Contact creation requires valid separate Bill-To instructions and parent identity.");
        var model = new AllAccountsModel
        {
            Organization = options.OrganizationCode,
            Class = "P",
            PrimaryAccount = parentAccount,
            Company = parentCompany,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = TextRules.NormalizeEmail(request.Email)
        };
        var added = CallWrite("Create contact", $"org={options.OrganizationCode}; parent={parentAccount}; order={Journal?.Evidence.Identity.Order}", model, () => client.Endpoints.Accounts.Add(model));
        if (string.IsNullOrWhiteSpace(added.AccountCode)) throw new UnknownWriteOutcomeException("Create contact", $"org={options.OrganizationCode}; parent={parentAccount}; email={request.Email}", new InvalidDataException("Momentus did not return the created contact AccountCode."));
        return added.AccountCode.Trim();
    }

    public ServiceOrdersModel UpdateOrder(ServiceOrdersModel order)
    {
        var operation = TextRules.Same(order.OrderStatus, "A") ? "Activate order" : "Update service order";
        var original = SerializeIntent(operation, GetOrder(Value(order.OrderNumber)));
        return CallWrite(operation, $"org={options.OrganizationCode}; order={order.OrderNumber}", order, () => client.Endpoints.ServiceOrders.Update(order), new() { ["original"] = original });
    }
    public ExhibitorsModel UpdateExhibitor(ExhibitorsModel exhibitor)
    {
        var operation = Value(exhibitor.ExhibitorStatus) == 2 ? "Activate exhibitor" : "Update exhibitor categories";
        var original = SerializeIntent(operation, GetExhibitor(Value(exhibitor.ExhibitorID)));
        return CallWrite(operation, $"org={options.OrganizationCode}; exhibitor={exhibitor.ExhibitorID}", exhibitor, () => client.Endpoints.Exhibitors.Update(exhibitor), new() { ["original"] = original });
    }

    public static BillingRequest BillingFrom(AllAccountsModel model, BillingConfiguration configuration) => BillingState.Read(model, configuration);

    public static string ExhibitorSalesRepUdf(ExhibitorsModel exhibitor) => Clean(exhibitor.ExhibitorUserFields?.UserText02);
    public static string ExhibitorStatePavilionUdf(ExhibitorsModel exhibitor) => Clean(exhibitor.ExhibitorUserFields?.UserText08);

    private IReadOnlyList<ExhibitorsModel> SearchExhibitors(string filter) => SearchAll(
        search => client.Endpoints.Exhibitors.Search(options.OrganizationCode, filter, search),
        client.Endpoints.Exhibitors.NavigateSearchList, "Exhibitors", filter,
        x => NumericIdentity(x.ExhibitorID), nameof(ExhibitorsModel.ExhibitorID));

    private IReadOnlyList<ServiceOrdersModel> SearchOrders(string filter) => SearchAll(
        search => client.Endpoints.ServiceOrders.Search(options.OrganizationCode, filter, search),
        client.Endpoints.ServiceOrders.NavigateSearchList, "ServiceOrders", filter,
        x => NumericIdentity(x.OrderNumber), nameof(ServiceOrdersModel.OrderNumber));

    private IReadOnlyList<AllAccountsModel> SearchAccounts(string filter) => SearchAll(
        search => client.Endpoints.Accounts.Search(options.OrganizationCode, filter, search),
        client.Endpoints.Accounts.NavigateSearchList, "Accounts", filter,
        x => Clean(x.AccountCode), nameof(AllAccountsModel.AccountCode));

    private IReadOnlyList<DocumentsModel> SearchDocuments(string filter) => SearchAll(
        search => client.Endpoints.Documents.Search(options.OrganizationCode, filter, search),
        client.Endpoints.Documents.NavigateSearchList, "Documents", filter,
        x => !string.IsNullOrWhiteSpace(x.Type) && Value(x.SequenceNumber) > 0 ? $"{Clean(x.Type)}/{Value(x.SequenceNumber)}" : "",
        nameof(DocumentsModel.SequenceNumber), nameof(DocumentsModel.Type));

    private static string NumericIdentity(object? value) => Value(value) > 0 ? Clean(value) : "";

    private IReadOnlyList<T> SearchAll<T>(Func<Search, SearchResponse<T>> firstPage, Func<string, Search, SearchResponse<T>> nextPage,
        string endpoint, string filter, Func<T, string> identity, params string[] orderBy) where T : UngerboeckModel
    {
        try
        {
            return CompleteSearch.Read(search => CallRead(() => firstPage(search)),
                (url, search) => CallRead(() => nextPage(url, search)), SearchOptions(orderBy), $"{endpoint}: {filter}",
                new Uri(options.BaseUrl.TrimEnd('/') + $"/api/v1/{endpoint}/{Uri.EscapeDataString(options.OrganizationCode)}"), identity);
        }
        catch (DecisionSearchException ex)
        {
            if (Journal is { } journal)
            {
                journal.Evidence.SearchFailures.Add($"{DateTimeOffset.UtcNow:O}: {ex.Message}");
                journal.Save();
            }
            throw;
        }
    }

    private Search SearchOptions(params string[] orderBy) => new() { PageSize = options.PageSize, MaxResults = options.MaxResults, OrderBy = orderBy.ToList() };
    private T CallRead<T>(Func<T> action)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            T result;
            requests++;
            client.LastResponseError = null;
            try
            {
                result = action();
                ThrowSdkError();
            }
            catch (Exception ex) when (attempt < 3 && IsTransientRead(ex))
            {
                delay(TimeSpan.FromSeconds(1 << attempt));
                continue;
            }
            PauseAfterRequest();
            return result;
        }
        throw new InvalidOperationException("Unreachable read retry state.");
    }

    private T CallWrite<T>(string operation, string target, T intent, Func<T> action, Dictionary<string, string>? source = null)
    {
        var journal = Journal ?? throw new InvalidOperationException("External mutations require a durable per-order journal.");
        if (operation is "Create organization account" or "Create contact" ||
            operation == "Add relationship" && intent is RelationshipsModel relationship && relationship.RelationshipType == "BTO")
        {
            // Run this after the mutation's absence/baseline searches, at the dispatch boundary.
            BillingGuard.Instructions(this, journal.Evidence.Plan);
            if (options.Billing.Selector(journal.Evidence.Plan.RequestedBilling) != "SEPARATE")
                throw new RecoveryReviewException("REVIEW: Use Above Address cannot authorize separate Bill-To mutations.");
        }
        if (journal.Evidence.Stages.Any(x => x.Status is StageStatus.Dispatching or StageStatus.Unknown or StageStatus.Succeeded))
            throw new RecoveryReviewException("REVIEW: a prior dispatched stage is unresolved; no new mutation can be dispatched before reconciliation and durable verification.");
        // Persist only intended mutable values; full SDK responses can change on readback.
        var stage = journal.Prepare(operation, target, SerializeIntent(operation, intent), source);
        if (stage.Status == StageStatus.Verified) return JsonConvert.DeserializeObject<T>(stage.Result)!;
        journal.Dispatching(stage); // Must reach durable storage before dispatch.
        T result;
        requests++;
        client.LastResponseError = null;
        try
        {
            // No retry: losing a response never proves that the mutation failed.
            result = action();
            ThrowSdkError();
            if (result is null) throw new InvalidDataException("Momentus returned no mutation response.");
        }
        catch (Exception ex)
        {
            var evidence = client.LastResponseError is { } error
                ? new InvalidOperationException($"Momentus status {error.Status}; codes={string.Join(",", error.ErrorList?.Select(x => x.ErrorCode) ?? [])}. {ex.Message}", ex)
                : ex;
            if (IsConfirmedRejection(ex))
            {
                var rejected = new WriteRejectedException(operation, target, evidence);
                journal.Outcome(stage, StageStatus.Failed, rejected.Message);
                throw rejected;
            }
            var unknown = new UnknownWriteOutcomeException(operation, target, evidence);
            journal.Outcome(stage, StageStatus.Unknown, unknown.Message);
            throw unknown;
        }
        journal.Result(stage, JsonConvert.SerializeObject(result)); // Returned IDs precede readback or any later stage.
        if (!VerifyStage(stage))
        {
            var unknown = new UnknownWriteOutcomeException(operation, target, new InvalidDataException("Returned identity or exact-target readback could not establish the intended effect."));
            journal.Outcome(stage, StageStatus.Unknown, unknown.Message);
            throw unknown;
        }
        journal.Verified(stage, stage.Verification);
        PauseAfterRequest();
        return result;
    }

    private void PauseAfterRequest()
    {
        if (options.RequestDelayMs > 0) delay(TimeSpan.FromMilliseconds(options.RequestDelayMs));
    }

    private static string BytesHash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    private static string SerializeIntent<T>(string operation, T value)
    {
        object fields = operation switch
        {
            "Create organization account" when value is AllAccountsModel a => new { a.Organization, a.Class, a.Name, a.EventSalesStatus, a.Address1, a.City, a.State, a.PostalCode, a.Country },
            "Create contact" when value is AllAccountsModel a => new { a.Organization, a.Class, a.PrimaryAccount, a.Company, a.FirstName, a.LastName, a.Email },
            "Update account address" when value is AllAccountsModel a => new { a.AccountCode, a.Address1, a.City, a.State, a.PostalCode, a.Country },
            "Add relationship" when value is RelationshipsModel r => new { r.MasterOrganizationCode, r.MasterAccountCode, r.SubordinateOrganizationCode, r.SubordinateAccountCode, r.RelationshipType },
            "Update service order" when value is ServiceOrdersModel o => new { o.OrderNumber, o.OrderAccountRep, o.Category, o.BoothNumber, o.BillToAccount, o.BillToContact },
            "Activate order" when value is ServiceOrdersModel o => new { o.OrderNumber, o.OrderStatus },
            "Update exhibitor categories" when value is ExhibitorsModel e => new { e.ExhibitorID, e.ExhibitorCategory },
            "Activate exhibitor" when value is ExhibitorsModel e => new { e.ExhibitorID, e.ExhibitorStatus },
            "Copy contract document" when value is DocumentsModel d => new { d.Type, d.NewFileName, d.Description, d.Category, d.Order, d.Event, d.Function, d.Account },
            "Add payment schedule note" or "Update payment schedule note" when value is NotesModel n => new { n.OrganizationCode, n.Type, n.OrderNumber, n.SequenceNumber, n.Class, n.Title, n.Text },
            "Send ready email" when value is EmailsModel e => new { e.Organization, e.EmailSubject, e.HtmlText, e.SendToAddresses, e.SaveAsUngerboeckDocument },
            _ => throw new InvalidOperationException($"No durable intent defined for {operation}.")
        };
        return JsonConvert.SerializeObject(fields);
    }

    internal void ReconcileIncomplete()
    {
        var journal = Journal ?? throw new InvalidOperationException("Recovery requires a journal.");
        foreach (var stage in journal.Evidence.Stages.Where(x => x.Status is StageStatus.Dispatching or StageStatus.Unknown or StageStatus.Succeeded))
        {
            // A terminated dispatch is unknown even if the exception was never saved.
            if (stage.Status == StageStatus.Dispatching) journal.Outcome(stage, StageStatus.Unknown, "Process interrupted during dispatch; effect unknown.");
            bool verified;
            try { verified = VerifyStage(stage); }
            catch (JournalStorageException) { throw; }
            catch (Exception ex)
            {
                journal.Outcome(stage, StageStatus.Unknown, $"Reconciliation read failed: {ex.Message}");
                throw new RecoveryReviewException($"REVIEW: unresolved {stage.Operation} ({stage.Target}); no redispatch or later effects are allowed. {ex.Message}");
            }
            if (!verified)
            {
                journal.Outcome(stage, StageStatus.Unknown, "Reconciliation could not positively establish success; automatic redispatch prohibited.");
                throw new RecoveryReviewException($"REVIEW: unresolved {stage.Operation} ({stage.Target}); no redispatch or later effects are allowed.");
            }
            journal.Verified(stage, stage.Verification, true);
        }
    }

    internal void VerifyUndispatched(params string[] operations)
    {
        var journal = Journal!;
        foreach (var stage in journal.Evidence.Stages.Where(x => operations.Contains(x.Operation) && x.Status is StageStatus.Planned or StageStatus.Failed))
        {
            // A current validation may reuse an existing record instead of issuing the planned create.
            if (stage.Operation is "Create organization account" or "Create contact")
            {
                var code = stage.Operation == "Create contact" ? journal.Evidence.Plan.FinalBillToContact : journal.Evidence.Plan.FinalBillToAccount;
                stage.Result = JsonConvert.SerializeObject(GetAccountModel(code));
            }
            if (!VerifyStage(stage)) throw new RecoveryReviewException($"REVIEW: skipped journaled stage {stage.Operation} still lacks verified completion evidence.");
            journal.Verified(stage, "Current validation resolved an undispatched/rejected stage. " + stage.Verification, true);
        }
    }

    private IReadOnlyList<DocumentsModel> GetSavedEmails(int orderNumber)
    {
        return SearchDocuments($"Order eq {orderNumber} and Type eq 'M'");
    }

    private bool VerifyStage(StageEvidence stage)
    {
        object? verified = null;
        switch (stage.Operation)
        {
            case "Create organization account":
            case "Create contact":
            {
                var returned = JsonConvert.DeserializeObject<AllAccountsModel>(stage.Result);
                if (string.IsNullOrWhiteSpace(returned?.AccountCode))
                {
                    // Read for reconciliation, but mutable matching inputs cannot prove creation identity.
                    if (stage.Operation == "Create contact")
                    {
                        var intended = JsonConvert.DeserializeObject<AllAccountsModel>(stage.Intent)!;
                        _ = FindContacts(intended.PrimaryAccount, intended.Email ?? "");
                    }
                    else _ = FindOrganizationCandidates(Journal!.Evidence.Plan.RequestedBilling);
                    return false;
                }
                var current = GetAccountModel(returned.AccountCode);
                var currentIntent = SerializeIntent(stage.Operation, current);
                if (stage.Operation == "Create organization account" && Newtonsoft.Json.Linq.JObject.Parse(stage.Intent)["EventSalesStatus"] is null)
                {
                    // Preserve Step 2 reconciliation for pre-Step 4 journals; legacy plans still require billing re-evaluation before new writes.
                    var legacy = Newtonsoft.Json.Linq.JObject.Parse(currentIntent);
                    legacy.Remove("EventSalesStatus");
                    currentIntent = legacy.ToString(Newtonsoft.Json.Formatting.None);
                }
                if (!TextRules.Same(current.AccountCode, returned.AccountCode) || currentIntent != stage.Intent) return false;
                verified = current;
                break;
            }
            case "Update account address":
            {
                var intended = JsonConvert.DeserializeObject<AllAccountsModel>(stage.Intent)!;
                var current = GetAccountModel(intended.AccountCode);
                if (SerializeIntent(stage.Operation, current) != stage.Intent) return false;
                verified = current;
                break;
            }
            case "Add relationship":
            {
                var intended = JsonConvert.DeserializeObject<RelationshipsModel>(stage.Intent)!;
                var current = CallRead(() => client.Endpoints.Relationships.Get(options.OrganizationCode, intended.MasterAccountCode, intended.SubordinateAccountCode, intended.RelationshipType));
                if (current is null || SerializeIntent(stage.Operation, current) != stage.Intent) return false;
                verified = current;
                break;
            }
            case "Update service order":
            case "Activate order":
            {
                var intended = JsonConvert.DeserializeObject<ServiceOrdersModel>(stage.Intent)!;
                var current = GetOrder(Value(intended.OrderNumber));
                if (SerializeIntent(stage.Operation, current) != stage.Intent) return false;
                verified = current;
                break;
            }
            case "Update exhibitor categories":
            case "Activate exhibitor":
            {
                var intended = JsonConvert.DeserializeObject<ExhibitorsModel>(stage.Intent)!;
                var current = GetExhibitor(Value(intended.ExhibitorID));
                if (SerializeIntent(stage.Operation, current) != stage.Intent) return false;
                verified = current;
                break;
            }
            case "Copy contract document":
            {
                var intended = JsonConvert.DeserializeObject<DocumentsModel>(stage.Intent)!;
                var returned = JsonConvert.DeserializeObject<DocumentsModel>(stage.Result);
                var baseline = stage.Source["baseline"].Split(',').ToHashSet();
                var matches = GetOrderContractPdfs(Value(intended.Order)).Where(x =>
                    Value(returned?.SequenceNumber) > 0 ? x.SequenceNumber == Value(returned!.SequenceNumber) && x.Type == returned.Type :
                    !baseline.Contains(x.SequenceNumber.ToString(CultureInfo.InvariantCulture)) && TextRules.Same(x.Description, intended.Description) && TextRules.Same(x.Category, intended.Category))
                    .Where(x => BytesHash(DownloadDocument(x)) == stage.Source["contentHash"]).ToList();
                if (matches.Count != 1) return false;
                var match = matches[0];
                verified = new DocumentsModel { Type = match.Type, SequenceNumber = match.SequenceNumber, DocumentID = match.DocumentId, Order = intended.Order };
                break;
            }
            case "Add payment schedule note":
            case "Update payment schedule note":
            {
                var intended = JsonConvert.DeserializeObject<NotesModel>(stage.Intent)!;
                var returned = JsonConvert.DeserializeObject<NotesModel>(stage.Result);
                var sequence = Value(returned?.SequenceNumber) > 0 ? Value(returned!.SequenceNumber) : Value(intended.SequenceNumber);
                var matches = GetOrderSonNotes(Value(intended.OrderNumber)).Where(x =>
                    (sequence == 0 || Value(x.SequenceNumber) == sequence) && TextRules.Same(x.Title, intended.Title) &&
                    PaymentScheduleExtractor.NormalizeForComparison(First(x.PlainText ?? "", x.Text ?? "")) == PaymentScheduleExtractor.NormalizeForComparison(intended.Text)).ToList();
                if (matches.Count != 1 || Value(matches[0].SequenceNumber) == 0) return false;
                verified = matches[0];
                break;
            }
            case "Send ready email":
            {
                // The saved email is corroboration of an accepted response, not proof of delivery.
                // Without that response, description alone cannot prove recipient/body/attachments.
                var intended = JsonConvert.DeserializeObject<EmailsModel>(stage.Intent)!;
                var baseline = stage.Source["baseline"].Split(',').ToHashSet();
                var matches = GetSavedEmails(int.Parse(stage.Source["order"], CultureInfo.InvariantCulture))
                    .Where(x => !baseline.Contains(Clean(x.SequenceNumber)) && TextRules.Same(x.Description, intended.EmailSubject)).ToList();
                if (stage.Result.Length == 0 || matches.Count != 1) return false;
                stage.Source["savedEmailSequence"] = Clean(matches[0].SequenceNumber);
                verified = JsonConvert.DeserializeObject<EmailsModel>(stage.Result);
                break;
            }
            default: throw new InvalidDataException($"Unknown journaled operation: {stage.Operation}");
        }
        stage.Result = JsonConvert.SerializeObject(verified);
        stage.Verification = $"Exact target readback at {DateTimeOffset.UtcNow:O}: {stage.Result}";
        return true;
    }

    private void ThrowSdkError()
    {
        if (client.LastResponseError is { } error)
            throw new InvalidOperationException($"Momentus status {error.Status}: {string.Join("; ", error.ErrorList?.Select(x => $"{x.ErrorCode}: {x.Message}") ?? [])}");
    }

    private static bool IsSdkTransportFailure(ApiErrors error) =>
        error.ErrorList?.Any(x => x.ErrorCode == "HttpCallFailed" && x.Source == "Sdk") == true;

    private bool IsTransientRead(Exception ex)
    {
        // The SDK replaces transport exception types with HttpCallFailed/status 400.
        if (client.LastResponseError is { } error)
            return IsSdkTransportFailure(error) || IsTransientStatus(error.Status);
        if (ex is AggregateException aggregate) return aggregate.Flatten().InnerExceptions.All(IsTransientRead);
        if (ex is HttpRequestException http) return !http.StatusCode.HasValue || IsTransientStatus((int)http.StatusCode.Value);
        return ex is TimeoutException or OperationCanceledException or IOException ||
            ex.InnerException is not null && IsTransientRead(ex.InnerException);
    }

    private bool IsConfirmedRejection(Exception ex)
    {
        if (client.LastResponseError is { } error)
            return !IsSdkTransportFailure(error) && IsRejectionStatus(error.Status);
        if (ex is AggregateException aggregate) return aggregate.Flatten().InnerExceptions.All(IsConfirmedRejection);
        return ex is HttpRequestException { StatusCode: { } status } && IsRejectionStatus((int)status);
    }

    private static bool IsTransientStatus(int status) => status is 408 or 429 or 500 or 502 or 503 or 504;
    private static bool IsRejectionStatus(int status) => status is >= 400 and < 500 && status != 408;
    private static AccountInfo ToAccount(AllAccountsModel x) => new(Clean(x.AccountCode), x.Class == "O" ? x.Name ?? "" : First(Clean(x.Name), Clean(x.Company)), Clean(x.FirstName), Clean(x.LastName), Clean(x.Email), Clean(x.Address1), Clean(x.City), Clean(x.State), Clean(x.PostalCode), Clean(x.Country), Clean(x.PrimaryAccount))
    { AccountClass = Clean(x.Class), EventSalesStatus = Clean(x.EventSalesStatus) };
    private static DocumentInfo ToDocument(DocumentsModel x) => new(Clean(x.Type), Value(x.SequenceNumber), Clean(x.DocumentID), Clean(x.Description), Clean(x.Category), DocumentEnteredOn(x));
    private static DateTime? DocumentEnteredOn(DocumentsModel x)
    {
        foreach (var name in new[] { "EnteredOn", "CreatedOn", "AddedOn", "DateAdded", "DocumentDate" })
        {
            var value = x.GetType().GetProperty(name)?.GetValue(x);
            if (value is not null && DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)) return date;
        }
        return null;
    }
    private static bool IsPdf(DocumentInfo x) => x.DocumentId.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
        x.Description.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || x.Type.Contains("PDF", StringComparison.OrdinalIgnoreCase);
    private static string Clean(object? x) => Convert.ToString(x, CultureInfo.InvariantCulture)?.Trim() ?? "";
    private static int Value(object? x) => x is null ? 0 : Convert.ToInt32(x, CultureInfo.InvariantCulture);
    private static DateTime DateValue(object? x) => x is null ? DateTime.MinValue : Convert.ToDateTime(x, CultureInfo.InvariantCulture);
    private static string First(params string[] values) => values.FirstOrDefault(x => x.Length > 0) ?? "";
}
