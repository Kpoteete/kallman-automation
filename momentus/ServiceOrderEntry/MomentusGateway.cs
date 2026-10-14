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
    private IReadOnlyList<CountriesModel>? billingCountries;
    private readonly Dictionary<string, IReadOnlyList<StatesModel>> billingStates = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<(string Type, int Sequence), byte[]>? documentReadSnapshot;
    private Dictionary<string, string>? handoffAccountSnapshot;
    private readonly Dictionary<string, string> parsedPaymentSchedules = new(StringComparer.Ordinal);
    public int RequestCount => requests;
    internal RunProgress? Progress { get; set; }
    internal ProcessingJournal? Journal { get; set; }
    internal Func<string, object, object>? MutationGuard { get; set; }
    internal Action? BeforeWriteDispatch { get; set; }
    internal BillingConfiguration BillingConfiguration => options.Billing;
    internal bool SendReadyEmail => options.SendReadyEmail;

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

    public IReadOnlyList<Candidate> FindCandidates()
    {
        var exhibitorFilter = $"ExhibitorStatus eq 35 and ExhibitorType eq 'ME'";
        if (options.ExhibitorId.HasValue) exhibitorFilter += $" and ExhibitorID eq {options.ExhibitorId.Value}";
        if (options.EventId.HasValue) exhibitorFilter += $" and Event eq {options.EventId.Value}";
        var found = SearchExhibitors(exhibitorFilter);
        if (found.Any(x => x.OrganizationCode != options.OrganizationCode || Value(x.ExhibitorID) <= 0 || Value(x.Event) <= 0 ||
            x.ExhibitorStatus != 35 || x.ExhibitorType != "ME" ||
            options.ExhibitorId.HasValue && x.ExhibitorID != options.ExhibitorId || options.EventId.HasValue && x.Event != options.EventId))
            throw new DecisionSearchException(SearchFailureKind.MalformedResponse, $"Exhibitors: {exhibitorFilter}", 0,
                "Eligibility search returned a missing or conflicting organization, exhibitor, event, type or status.");
        var exhibitors = found
            .Where(x => !ExhibitorCategoryRules.HasCode(x.ExhibitorCategory, ExhibitorCategoryRules.Hold))
            .ToList();
        var result = new List<Candidate>();
        var checkedExhibitors = 0;
        Progress?.Message($"Found {exhibitors.Count:N0} eligible exhibitors without Hold; checking their pending orders.");
        // The tenant can accept an OR of exhibitor/event pairs but silently return
        // no rows. Query each exact pair so that an empty grouped response cannot
        // hide eligible orders. Every search still requires complete pagination.
        foreach (var exhibitor in exhibitors)
        {
            var filter = $"OrderStatus eq 'PC' and Exhibitor eq {Value(exhibitor.ExhibitorID)} and Event eq {Value(exhibitor.Event)}";
            var orders = SearchOrders(filter);
            if (orders.Any(x => x.OrganizationCode != options.OrganizationCode || Value(x.OrderNumber) <= 0 ||
                x.OrderStatus != "PC" || x.Exhibitor != exhibitor.ExhibitorID || x.Event != exhibitor.Event))
                throw new DecisionSearchException(SearchFailureKind.MalformedResponse, $"ServiceOrders: {filter}", 0,
                    "Pending-order search returned a missing or conflicting organization, order, exhibitor/event pair or status.");
            result.AddRange(orders.Select(order => new Candidate(order, exhibitor)));
            checkedExhibitors++;
            Progress?.Message($"Pending-order discovery: checked {checkedExhibitors:N0}/{exhibitors.Count:N0} exhibitors; found {result.Count:N0} orders.");
        }
        return result.OrderBy(x => Value(x.Order.Event)).ThenBy(x => Value(x.Order.OrderNumber)).ToList();
    }

    internal IReadOnlyList<ServiceOrdersModel> GetExhibitorOrders(int exhibitor, int eventId)
    {
        var result = SearchOrders($"Exhibitor eq {exhibitor} and Event eq {eventId}");
        if (result.Any(x => x.OrganizationCode != options.OrganizationCode || x.Exhibitor != exhibitor || x.Event != eventId || x.OrderNumber <= 0 || string.IsNullOrWhiteSpace(x.OrderStatus)))
            throw new RecoveryReviewException("REVIEW: activation order set has missing/conflicting exhibitor, event, identity, or status.");
        return result;
    }

    internal byte[] VerifiedDocumentData(DocumentInfo document)
    {
        if (document.ContentHash.Length == 0) throw new RecoveryReviewException("REVIEW: attachment lacks content identity.");
        var bytes = DownloadDocument(document);
        if (BytesHash(bytes) != document.ContentHash) throw new RecoveryReviewException("REVIEW: attachment content changed after validation.");
        _ = ParsedPaymentSchedule(bytes); // A previously parsed identical hash is still the same PDF.
        return bytes;
    }

    public ExhibitorsModel GetExhibitor(int id) => CallRead(() => client.Endpoints.Exhibitors.Get(options.OrganizationCode, id), "Read exhibitor");
    public ServiceOrdersModel GetOrder(int number) => CallRead(() => client.Endpoints.ServiceOrders.Get(options.OrganizationCode, number), "Read service order");
    public AccountInfo GetAccount(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return RunRow.EmptyAccount;
        return ToAccount(GetAccountModel(code));
    }

    public AllAccountsModel GetAccountModel(string code)
    {
        code = code.Trim();
        if (handoffAccountSnapshot?.TryGetValue(code, out var saved) == true)
            return JsonConvert.DeserializeObject<AllAccountsModel>(saved)!;
        var model = CallRead(() => client.Endpoints.Accounts.Get(options.OrganizationCode, code), "Read account/contact");
        // Save a serialized value, never a mutable SDK payload shared with callers.
        if (model is not null && handoffAccountSnapshot is not null)
            handoffAccountSnapshot.Add(code, JsonConvert.SerializeObject(model));
        return model!;
    }

    internal string GetOrderAccountRepEmail(string code)
    {
        if (!BillingRules.ValidCode(code)) throw new RecoveryReviewException("REVIEW: order account rep is unresolved; employee email cannot be selected.");
        var account = GetAccountModel(code);
        if (!TextRules.Same(account.Organization, options.OrganizationCode) || !TextRules.Same(account.AccountCode, code))
            throw new RecoveryReviewException("REVIEW: employee email lookup returned a different account or organization.");
        var email = account.Email?.Trim() ?? "";
        if (!BillingRules.ValidEmail(email))
            throw new RecoveryReviewException($"REVIEW: order account rep {code} has a missing or invalid email address.");
        return email;
    }

    internal void VerifyOrderAccountRepIdentity(string code)
    {
        var account = GetAccountModel(code);
        if (!BillingRules.ValidCode(code) || !TextRules.Same(account.Organization, options.OrganizationCode) || !TextRules.Same(account.AccountCode, code))
            throw new RecoveryReviewException($"REVIEW: salesperson account '{code}' could not be verified in organization {options.OrganizationCode}.");
    }

    public IReadOnlyList<OrderItemInfo> GetOrderItems(int orderNumber)
    {
        var filter = $"OrderNumber eq {orderNumber}";
        var rows = SearchAll(search => client.Endpoints.ServiceOrderItems.Search(options.OrganizationCode, filter, search),
            client.Endpoints.ServiceOrderItems.NavigateSearchList, "ServiceOrderItems", filter,
            x => NumericIdentity(x.OrderLineNumber), nameof(ServiceOrderItemsModel.OrderLineNumber));
        return rows.Select(ItemEvidenceRules.Capture).ToList();
    }

    public IReadOnlyList<DocumentInfo> GetExhibitorContractPdfs(int exhibitorId)
    {
        var documents = SearchDocuments($"Exhibitor eq {exhibitorId} and Category eq 'CON'");
        ValidateDocumentScope(documents, null, exhibitorId, contracts: true);
        return documents.Select(ToDocument).ToList();
    }

    public IReadOnlyList<DocumentInfo> GetOrderContractPdfs(int orderNumber)
    {
        var documents = SearchDocuments($"Order eq {orderNumber} and Category eq 'CON'");
        ValidateDocumentScope(documents, orderNumber, null, contracts: true);
        return documents.Select(ToDocument).ToList();
    }

    public IReadOnlyList<DocumentInfo> GetNearbyExhibitorPdfs(int exhibitorId, DateTime? orderDate)
    {
        if (!orderDate.HasValue) return [];
        var documents = SearchDocuments($"Exhibitor eq {exhibitorId}");
        ValidateDocumentScope(documents, null, exhibitorId, contracts: false);
        return documents
            .Select(ToDocument)
            .Where(x => IsPdf(x) && x.EnteredOn.HasValue && Math.Abs((x.EnteredOn.Value.Date - orderDate.Value.Date).TotalDays) <= 1)
            .OrderBy(x => Math.Abs((x.EnteredOn!.Value - orderDate.Value).TotalMinutes))
            .ThenByDescending(x => x.SequenceNumber)
            .ToList();
    }

    private void ValidateDocumentScope(IReadOnlyList<DocumentsModel> documents, int? order, int? exhibitor, bool contracts)
    {
        if (documents.Any(x => !string.IsNullOrWhiteSpace(x.Organization) && x.Organization != options.OrganizationCode ||
            order.HasValue && x.Order != order || exhibitor.HasValue && x.Exhibitor != exhibitor || contracts && x.Category != "CON"))
            throw new RecoveryReviewException("REVIEW: contract search returned missing/conflicting owner, organization or category identity.");
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
        if (source.ContentHash.Length > 0 && source.ContentHash != BytesHash(Convert.FromBase64String(data)))
            throw new RecoveryReviewException("REVIEW: source contract content changed; stale copy stopped.");
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
        var added = CallWrite("Copy contract document", $"org={options.OrganizationCode}; source={source.Type}/{source.SequenceNumber}; hash={BytesHash(Convert.FromBase64String(data))}; order={orderNumber}", model, write => client.Endpoints.Documents.Add(write),
            new() { ["sourceType"] = source.Type, ["sourceSequence"] = source.SequenceNumber.ToString(CultureInfo.InvariantCulture),
                ["sourceId"] = source.DocumentId, ["sourceOrg"] = options.OrganizationCode, ["contentHash"] = BytesHash(Convert.FromBase64String(data)),
                ["baseline"] = string.Join(",", GetOrderContractPdfs(orderNumber).Select(x => $"{x.Type}/{x.SequenceNumber}")) });
        if (Value(added.SequenceNumber) == 0) throw new UnknownWriteOutcomeException("Copy contract document", $"org={options.OrganizationCode}; source={source.Type}/{source.SequenceNumber}; order={orderNumber}", new InvalidDataException("Momentus did not return the copied document sequence number."));
    }

    internal ContractSnapshot ReadContract(DocumentInfo document, string owner, int ownerId)
    {
        var bytes = DownloadDocument(document);
        var hash = BytesHash(bytes);
        if (document.ContentHash.Length > 0 && document.ContentHash != hash)
            throw new RecoveryReviewException("REVIEW: contract content changed after selection.");
        string schedule;
        try { schedule = ParsedPaymentSchedule(bytes, hash); }
        catch (Exception ex) when (ex is not JournalStorageException)
        {
            throw new InvalidDataException($"Could not read Contract PDF '{document.DocumentId}' ({owner} {ownerId}, type={document.Type}, sequence={document.SequenceNumber}): {ex.Message}", ex);
        }
        return new(options.OrganizationCode, owner, ownerId, document with { ContentHash = hash }, hash, schedule);
    }

    internal ContractSelection ResolveContracts(int exhibitor, int order, DateTime? orderDate)
    {
        var source = GetExhibitorContractPdfs(exhibitor).Select(x => ReadContract(x, "Exhibitor", exhibitor)).ToList();
        var destination = GetOrderContractPdfs(order).Select(x => ReadContract(x, "Order", order)).ToList();
        var fallback = source.Count == 0 && destination.Count == 0;
        if (fallback) source = GetNearbyExhibitorPdfs(exhibitor, orderDate).Select(x => ReadContract(x, "Nearby", exhibitor))
            .Where(x => x.PaymentSchedule.Length > 0).ToList();
        return ContractRules.Select(source, destination, fallback);
    }

    internal int EnsureContractCopies(ServiceOrdersModel order, RunRow row)
    {
        var journal = Journal ?? throw new InvalidOperationException("Contract copies require a journal.");
        var plan = row.Contracts ?? throw new RecoveryReviewException("REVIEW: missing contract identity plan.");
        if (plan.ReviewMessage.Length > 0) throw new RecoveryReviewException(plan.ReviewMessage);
        var copied = 0;
        foreach (var source in ContractRules.Ordered(plan.Sources).GroupBy(x => x.ContentHash).Select(x => x.First()))
        {
            _ = ReadContract(source.Document, source.Owner, source.OwnerId); // Reject revised bytes before copy/reuse.
            var destination = GetOrderContractPdfs(Value(order.OrderNumber)).Select(x => ReadContract(x, "Order", Value(order.OrderNumber)))
                .Where(x => x.ContentHash == source.ContentHash).ToList();
            if (destination.Count > 1) throw new RecoveryReviewException("REVIEW: multiple indistinguishable destination contracts; no copy attempted.");
            var known = row.ContractCopies.SingleOrDefault(x => x.Source.Organization == source.Organization &&
                x.Source.Document.Type == source.Document.Type && x.Source.Document.SequenceNumber == source.Document.SequenceNumber);
            if (known is not null && (known.Source.ContentHash != source.ContentHash || destination.Count != 1 ||
                destination[0].Document.Type != known.Destination.Type || destination[0].Document.SequenceNumber != known.Destination.SequenceNumber))
                throw new RecoveryReviewException("REVIEW: journaled contract destination is missing or changed; no replacement copy attempted.");
            if (destination.Count == 0)
            {
                CopyContractPdfToOrder(source.Document, order);
                copied++;
                destination = GetOrderContractPdfs(Value(order.OrderNumber)).Select(x => ReadContract(x, "Order", Value(order.OrderNumber)))
                    .Where(x => x.ContentHash == source.ContentHash).ToList();
            }
            if (destination.Count != 1) throw new RecoveryReviewException("REVIEW: contract copy readback did not identify one exact content match.");
            var identity = new ContractCopyIdentity(source, destination[0].Document, destination[0].ContentHash);
            row.ContractCopies.RemoveAll(x => x.Source.Document.Type == source.Document.Type && x.Source.Document.SequenceNumber == source.Document.SequenceNumber);
            row.ContractCopies.Add(identity);
            journal.Save();
        }
        return copied;
    }

    public string ExtractPaymentSchedule(IEnumerable<DocumentInfo> documents)
    {
        var selection = ContractRules.Select(documents.Select(x => ReadContract(x, "Candidate", 0)), []);
        if (selection.ReviewMessage.Length > 0 && selection.Sources.Any(x => x.PaymentSchedule.Length > 0))
            throw new RecoveryReviewException(selection.ReviewMessage);
        return selection.PaymentSchedule;
    }

    public IReadOnlyList<NotesModel> GetOrderSonNotes(int orderNumber)
    {
        var filter = $"Type eq 'OH' and OrderNumber eq {orderNumber} and Class eq 'SON'";
        var notes = SearchAll(search => client.Endpoints.Notes.Search(options.OrganizationCode, filter, search),
            client.Endpoints.Notes.NavigateSearchList, "Notes", filter,
            x => NumericIdentity(x.SequenceNumber), nameof(NotesModel.SequenceNumber));
        if (notes.Any(x => x.OrderNumber != orderNumber || x.Type != "OH" || x.Class != "SON"))
            throw new RecoveryReviewException("REVIEW: managed-note search returned conflicting/missing order-header identity.");
        return notes;
    }

    public string SavePaymentScheduleNote(int orderNumber, string text)
    {
        var journal = Journal ?? throw new InvalidOperationException("Managed note writes require a journal.");
        var notes = GetOrderSonNotes(orderNumber);
        var decision = ManagedNoteRules.Resolve(notes, journal.Evidence.Stages, text);
        if (decision.Action == "REVIEW") throw new RecoveryReviewException(decision.Message);
        var plan = journal.Evidence.Plan;
        var existing = decision.Note;
        if (plan.ManagedNoteSequence.HasValue && (existing is null || Value(existing.SequenceNumber) != plan.ManagedNoteSequence))
            throw new RecoveryReviewException("REVIEW: managed payment-note identity changed after evaluation.");
        if (existing is not null && plan.ManagedNoteContentHash.Length > 0 &&
            ManagedNoteRules.ContentHash(ManagedNoteRules.Text(existing)) != plan.ManagedNoteContentHash &&
            ManagedNoteRules.ContentHash(ManagedNoteRules.Text(existing)) != ManagedNoteRules.ContentHash(text))
            throw new RecoveryReviewException("REVIEW: managed payment-note content changed after evaluation.");
        var action = "ADDED";
        if (existing is null)
        {
            var model = new NotesModel
            {
                OrganizationCode = options.OrganizationCode, Type = USISDKConstants.NoteType.OrderNote,
                OrderNumber = orderNumber, Class = "SON", Title = ManagedNoteRules.Title, Text = text
            };
            CallWrite("Add payment schedule note", $"org={options.OrganizationCode}; order={orderNumber}; type=OH; class=SON", model,
                write => client.Endpoints.Notes.Add(write), new() { ["baseline"] = string.Join(",", notes.Select(x => x.SequenceNumber)), ["noteHash"] = ManagedNoteRules.ContentHash(text) });
        }
        else if (decision.Action == "CURRENT MATCH" || ManagedNoteRules.ContentHash(ManagedNoteRules.Text(existing)) == ManagedNoteRules.ContentHash(text) && existing.Title == ManagedNoteRules.Title)
            action = "ALREADY MATCHED";
        else
        {
            var original = JsonConvert.SerializeObject(existing);
            existing.Class = "SON"; existing.Title = ManagedNoteRules.Title; existing.Text = text;
            CallWrite("Update payment schedule note", $"org={options.OrganizationCode}; order={orderNumber}; note={existing.SequenceNumber}", existing,
                write => client.Endpoints.Notes.Update(write), new() { ["original"] = original, ["noteHash"] = ManagedNoteRules.ContentHash(text) });
            action = "UPDATED";
        }
        var verified = ManagedNoteRules.Resolve(GetOrderSonNotes(orderNumber), journal.Evidence.Stages, text);
        if (verified.Action == "REVIEW" || verified.Note is null || ManagedNoteRules.ContentHash(ManagedNoteRules.Text(verified.Note)) != ManagedNoteRules.ContentHash(text))
            throw new RecoveryReviewException("REVIEW: managed SON Payment Schedule readback failed; unrelated SON notes are preserved.");
        plan.ManagedNoteSequence = Value(verified.Note.SequenceNumber);
        plan.ManagedNoteContentHash = ManagedNoteRules.ContentHash(text);
        journal.Save();
        return action;
    }

    public bool ReadyEmailWasSent(int orderNumber)
    {
        var verified = Journal?.Evidence.Identity.Order == orderNumber && Journal.Evidence.Stages.Any(x => x.Operation == "Send ready email" && x.Status == StageStatus.Verified);
        if (verified) return true;
        // Each invocation is a new current-data workflow. Earlier emails and
        // receipts do not suppress or block this run's final notification.
        return false;
    }

    public string SendReadyForInvoicingEmail(RunRow row, IEnumerable<DocumentInfo> documents)
    {
        if (ReadyEmailWasSent(row.OrderNumber)) return "ALREADY SENT";
        var handoff = Journal?.Evidence.Handoff ?? throw new RecoveryReviewException("REVIEW: send requires verified final handoff state.");
        var supplied = documents.ToList();
        if (!supplied.Select(x => x.ContentHash).Order().SequenceEqual(handoff.Attachments.Select(x => x.ContentHash).Order()))
            throw new RecoveryReviewException("REVIEW: incomplete or conflicting contract attachment set; email not sent.");
        var emailRow = handoff.EmailRow();
        var subject = ReadyEmailBuilder.Subject(emailRow);
        var model = new EmailsModel { Organization = options.OrganizationCode, EmailSubject = subject,
            HtmlText = ReadyEmailBuilder.Build(emailRow, handoff.Attachments), SaveAsUngerboeckDocument = new DocumentsModel { Order = handoff.OrderNumber } };
        var recipients = ReadyEmailBuilder.Recipients(row);
        foreach (var recipient in recipients.To) model.SendToAddresses.Add(new EmailAccountModel { EmailAddress = recipient });
        foreach (var recipient in recipients.Cc) model.CCAddresses.Add(new EmailAccountModel { EmailAddress = recipient });
        foreach (var document in handoff.Attachments)
        {
            model.Attachments.Add(new AttachmentModel { Description = SafeAttachmentName(document.Description, document.SequenceNumber),
                FileName = SafeAttachmentName(document.DocumentId, document.SequenceNumber) + ".pdf", FileData = Convert.ToBase64String(VerifiedDocumentData(document)) });
        }
        CallWrite("Send ready email", $"org={options.OrganizationCode}; order={row.OrderNumber}; recipient={row.ReadyEmailRecipient}; cc={row.ReadyEmailCcRecipient}; subject={subject}", model, write => client.Endpoints.Emails.Send(write),
            new() { ["order"] = row.OrderNumber.ToString(CultureInfo.InvariantCulture), ["baseline"] = string.Join(",", GetSavedEmails(row.OrderNumber).Select(x => x.SequenceNumber)),
                ["bodyHash"] = OrderIdentity.Hash(model.HtmlText), ["attachmentHashes"] = string.Join(",", model.Attachments.Select(x => BytesHash(Convert.FromBase64String(x.FileData)))) });
        WriteReadyEmailReceipt(row.OrderNumber, row.ReadyEmailRecipient, row.ReadyEmailCcRecipient, subject);
        return "SENT";
    }

    private static string SafeAttachmentName(string description, int sequenceNumber)
    {
        var value = string.IsNullOrWhiteSpace(description) ? $"Contract-{sequenceNumber}" : description.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '-');
        return value.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }

    internal void SendExceptionSummary(RunRow anchor, string subject, string html, string runId)
    {
        if (!options.Apply || !options.Confirm) throw new InvalidOperationException("Exception email requires confirmed apply mode.");
        var previousJournal = Journal;
        var previousGuard = MutationGuard;
        var previousDispatch = BeforeWriteDispatch;
        var store = new FileJournalStore(Path.Combine(options.StateFolder, "notifications", runId));
        var identity = OrderIdentity.From(options, anchor);
        var evidence = store.Load().SingleOrDefault(x => x.Identity == identity) ?? new OrderEvidence
            { Identity = identity, CreatedRunId = runId, Plan = anchor };
        try
        {
            Journal = new ProcessingJournal(store, evidence, runId);
            MutationGuard = (operation, intent) =>
            {
                if (operation != "Send exception summary") throw new RecoveryReviewException("REVIEW: exception notification permits only its summary email.");
                var order = GetOrder(anchor.OrderNumber);
                if (!TextRules.Same(order.OrganizationCode, options.OrganizationCode) || order.OrderNumber != anchor.OrderNumber ||
                    order.Event != anchor.EventId || order.Exhibitor != anchor.ExhibitorId)
                    throw new RecoveryReviewException("REVIEW: exception-summary saved-order identity cannot be verified.");
                return intent;
            };
            BeforeWriteDispatch = null; // A run-level notification does not consume an order allowance.
            Journal.Save();
            var model = new EmailsModel { Organization = options.OrganizationCode, EmailSubject = subject, HtmlText = html,
                SaveAsUngerboeckDocument = new DocumentsModel { Order = anchor.OrderNumber } };
            model.SendToAddresses.Add(new EmailAccountModel { EmailAddress = ExceptionSummary.Recipient });
            CallWrite("Send exception summary", $"org={options.OrganizationCode}; run={runId}; recipient={ExceptionSummary.Recipient}", model,
                write => client.Endpoints.Emails.Send(write), new()
                {
                    ["order"] = anchor.OrderNumber.ToString(CultureInfo.InvariantCulture),
                    ["baseline"] = string.Join(",", GetSavedEmails(anchor.OrderNumber).Select(x => x.SequenceNumber)),
                    ["bodyHash"] = OrderIdentity.Hash(html), ["attachmentHashes"] = ""
                });
            evidence.Complete = true;
            Journal.Save();
        }
        finally { Journal = previousJournal; MutationGuard = previousGuard; BeforeWriteDispatch = previousDispatch; }
    }

    private string ReadyEmailReceiptPath(int orderNumber) => Path.Combine(options.StateFolder, $"ready-email-{OrderIdentity.Hash(new Uri(options.BaseUrl).AbsoluteUri.TrimEnd('/') + "|" + options.OrganizationCode)}-order-{orderNumber}.sent.json");
    private void WriteReadyEmailReceipt(int orderNumber, string recipient, string ccRecipient, string subject)
    {
        try
        {
            Directory.CreateDirectory(options.StateFolder);
            var path = ReadyEmailReceiptPath(orderNumber);
            var temp = path + $".{Guid.NewGuid():N}.tmp";
            FileJournalStore.DurableWrite(temp, System.Text.Json.JsonSerializer.Serialize(new { SentOn = DateTimeOffset.Now, OrderNumber = orderNumber, Recipient = recipient, CcRecipient = ccRecipient, Subject = subject }));
            File.Move(temp, path, true);
        }
        catch (Exception ex) { throw new JournalStorageException("Email was accepted and journaled, but receipt persistence failed; stop subsequent mutations.", ex); }
    }

    private byte[] DownloadDocument(DocumentInfo source)
    {
        var identity = (source.Type, source.SequenceNumber);
        if (documentReadSnapshot?.TryGetValue(identity, out var saved) == true) return saved;
        using var downloadProgress = Progress?.Begin($"Downloading contract PDF {source.SequenceNumber}", announce: false);
        var data = CallRead(() => client.Endpoints.Documents.Download(options.OrganizationCode, source.Type, source.SequenceNumber), "Download contract PDF");
        if (string.IsNullOrWhiteSpace(data)) throw new InvalidOperationException($"Contract PDF {source.SequenceNumber} downloaded without file data.");
        try
        {
            var bytes = Convert.FromBase64String(data.Trim().Trim('"'));
            documentReadSnapshot?.Add(identity, bytes);
            return bytes;
        }
        catch (FormatException ex) { throw new InvalidDataException($"Contract PDF {source.SequenceNumber} download was not valid base64 data.", ex); }
    }

    // Only one read-only handoff verification may reuse its freshly downloaded bytes.
    // The next verification/dispatch guard starts fresh, even if document metadata is unchanged.
    internal IDisposable BeginDocumentReadSnapshot()
    {
        var previous = documentReadSnapshot;
        documentReadSnapshot = [];
        return new DocumentReadScope(() => documentReadSnapshot = previous);
    }

    // Reuse exact account reads only inside one read-only final verification.
    // Order/exhibitor payloads, searches and every subsequent dispatch guard remain fresh.
    internal IDisposable BeginHandoffReadSnapshot()
    {
        var documents = BeginDocumentReadSnapshot();
        var previous = handoffAccountSnapshot;
        handoffAccountSnapshot = new(StringComparer.Ordinal);
        return new DocumentReadScope(() =>
        {
            handoffAccountSnapshot = previous;
            documents.Dispose();
        });
    }

    private sealed class DocumentReadScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private string ParsedPaymentSchedule(byte[] bytes, string? hash = null)
    {
        hash ??= BytesHash(bytes);
        if (parsedPaymentSchedules.TryGetValue(hash, out var schedule)) return schedule;
        schedule = PaymentScheduleExtractor.FromPdf(bytes);
        parsedPaymentSchedules.Add(hash, schedule);
        return schedule;
    }

    public string GetBoothNumber(int exhibitorId, int eventId) => GetBoothEvidence(exhibitorId, eventId).Booth;

    internal BoothEvidence GetBoothEvidence(int exhibitorId, int eventId)
    {
        var filter = $"(Type eq 'BP' or Type eq 'DC') and ExhibitorID eq {exhibitorId} and Event eq {eventId}";
        var rows = SearchAll(search => client.Endpoints.Activities.Search(options.OrganizationCode, filter, search),
            client.Endpoints.Activities.NavigateSearchList, "Activities", filter,
            x => NumericIdentity(x.SequenceNumber), nameof(ActivitiesModel.EnteredOn), nameof(ActivitiesModel.SequenceNumber));
        var matches = rows.Select(x => new { Booth = BoothRules.Parse(x.PlainText), Entered = DateValue(x.EnteredOn), Sequence = Value(x.SequenceNumber) })
            .Where(x => x.Booth.Length > 0).OrderByDescending(x => x.Entered).ThenByDescending(x => x.Sequence).ToList();
        var hash = OrderIdentity.Hash(JsonConvert.SerializeObject(rows.OrderBy(x => x.SequenceNumber)));
        if (matches.Count == 0) return new("", hash);
        var latest = matches[0];
        return new(matches.Where(x => x.Entered == latest.Entered).Select(x => x.Booth).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? latest.Booth : "", hash);
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

    internal bool HasGlobalContactEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new DecisionSearchException(SearchFailureKind.IdentityUnavailable, "Contact matching", 0, "No email identity is available for the required search.");
        using (Progress?.Begin("Checking existing contacts by exact email first"))
        {
            var filter = $"Class eq 'P' and Email eq '{TextRules.OData(TextRules.NormalizeEmail(email))}'";
            var search = SearchOptions(nameof(AllAccountsModel.AccountCode));
            search.MaxResults = Math.Min(search.MaxResults, 1000);
            try
            {
                var matches = CompleteSearch.Read(
                    page => CallRead(() => client.Endpoints.Accounts.Search(options.OrganizationCode, filter, page), "Search contacts by email"),
                    (url, page) => CallRead(() => client.Endpoints.Accounts.NavigateSearchList(url, page), "Search contacts by email"),
                    search, "Targeted contact email evidence", new Uri(options.BaseUrl.TrimEnd('/') + $"/api/v1/Accounts/{Uri.EscapeDataString(options.OrganizationCode)}"),
                    x => Clean(x.AccountCode));
                if (matches.Any(x => x.Class == "P" && TextRules.Same(x.Email, email)))
                {
                    Progress?.Message("An existing contact uses the billing email; the full global contact scan is unnecessary for this review decision.");
                    return true;
                }
                Progress?.Message("The exact-email query found no matching contact. Checking a complete covered email-fragment search before allowing creation, including case/space variants.");
            }
            catch (DecisionSearchException)
            {
                // The optional shortcut proves presence only. No failed or empty targeted query authorizes creation.
                Progress?.Message("The exact-email query could not establish a match. Checking complete email-fragment results, with the full contact index as fallback.");
            }
        }
        return FindGlobalContactCandidates(email).Count > 0;
    }

    internal IReadOnlyList<AccountInfo> FindGlobalContactCandidates(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new DecisionSearchException(SearchFailureKind.IdentityUnavailable, "Contact matching", 0, "No email identity is available for the required search.");
        var targeted = CompanySearch.ForEmail(email);
        if (targeted is not null)
        {
            using var progress = Progress?.Begin("Checking all contacts matching a covered billing-email fragment");
            try
            {
                var rows = SearchAccounts(targeted.Filter, CompanySearch.ResultCeiling, optional: true);
                if (rows.All(x => x.Class == "P" && targeted.Includes(x.Email)))
                {
                    var matches = rows.Where(x => TextRules.NormalizeEmail(x.Email) == TextRules.NormalizeEmail(email)).Select(ToAccount).OrderBy(x => x.AccountCode).ToList();
                    Progress?.Message($"Targeted email search checked {rows.Count:N0} contacts; {matches.Count:N0} use the requested billing email.");
                    return matches;
                }
                Progress?.Message("The contact response did not satisfy its email-fragment filter. Checking the complete contact index.");
            }
            catch (DecisionSearchException ex)
            {
                Progress?.Message($"The complete targeted email search is unavailable ({ex.Kind}). Checking the complete contact index.");
            }
        }
        return FindContacts(null, email);
    }

    public IReadOnlyList<AccountInfo> FindOrganizationCandidates(BillingRequest request)
    {
        var company = TextRules.Clean(request.CompanyName);
        if (TextRules.NormalizeCompany(company).Length == 0)
            throw new DecisionSearchException(SearchFailureKind.IdentityUnavailable, "Organization identity", 0,
                "Company comparison has an empty identity; account matching is unavailable.");
        var targeted = CompanySearch.For(company);
        if (targeted is not null)
        {
            using var targetedProgress = Progress?.Begin("Checking billing-company name variants with a targeted name-fragment search");
            try
            {
                var rows = SearchAccounts(targeted.Filter, CompanySearch.ResultCeiling, optional: true);
                if (rows.All(x => x.Class == "O" && targeted.Includes(x.Name)))
                {
                    var matches = rows.Select(ToAccount).Where(x => TextRules.CompanyMatches(x.Company, company)).OrderBy(x => x.AccountCode).ToList();
                    Progress?.Message($"Targeted company search checked {rows.Count:N0} accounts; {matches.Count:N0} match the complete company name. No full organization scan is needed.");
                    return matches;
                }
                Progress?.Message("The targeted company response did not satisfy its name filter. Checking the complete organization index.");
            }
            catch (DecisionSearchException ex)
            {
                Progress?.Message($"The targeted company search could not complete ({ex.Kind}). Checking the complete organization index before choosing or creating an account.");
            }
        }
        else Progress?.Message("This company name has no supported search fragment. Checking the complete organization index to cover its name variants.");
        using var companyProgress = Progress?.Begin("Checking billing-company name variants across the organization index (matching fields only)");
        return SearchAccounts("Class eq 'O'").Select(ToAccount)
            .Where(x => TextRules.CompanyMatches(x.Company, company)).OrderBy(x => x.AccountCode).ToList();
    }

    internal BillingRequest ResolveBillingGeography(BillingRequest request)
    {
        billingCountries ??= SearchAll(search => client.Endpoints.Countries.Search(options.OrganizationCode, "All", search),
            client.Endpoints.Countries.NavigateSearchList, "Countries", "All", x => Clean(x.Code), nameof(CountriesModel.Code));
        var country = BillingGeography.Country(request.Country, billingCountries);
        if (string.IsNullOrWhiteSpace(request.State)) return request with { Country = country, State = "" };
        return BillingGeography.Address(country, request, GetBillingStates(country));
    }

    private IReadOnlyList<StatesModel> GetBillingStates(string country)
    {
        if (!billingStates.TryGetValue(country, out var states))
        {
            var filter = $"CountryCode eq '{TextRules.OData(country)}'";
            states = CompleteSearch.Read(search => CallRead(() => client.Endpoints.States.Search(filter, search)),
                (url, search) => CallRead(() => client.Endpoints.States.NavigateSearchList(url, search)),
                SearchOptions(nameof(StatesModel.Code)), $"States: {filter}", new Uri(options.BaseUrl.TrimEnd('/') + "/api/v1/States"),
                x => string.IsNullOrWhiteSpace(x.CountryCode) || string.IsNullOrWhiteSpace(x.Code) ? "" : $"{x.CountryCode}/{x.Code}");
            billingStates.Add(country, states);
        }
        return states;
    }

    internal void SetBillingAddressLabels(RunRow row)
    {
        var state = row.EffectiveBilling!;
        var labels = TextRules.Same(state.Account.Country, "KOR") && !string.IsNullOrWhiteSpace(state.Account.State)
            ? BillingGeography.Labels(state.Account, GetBillingStates("KOR")) : null;
        BillingState.Set(row, state with { KoreanAddressLabels = labels });
    }

    internal bool BillingAddressesMatch(AccountInfo account, BillingRequest request)
    {
        var submitted = ResolveBillingGeography(request);
        var existing = ResolveBillingGeography(request with { Address = account.Address, City = account.City, Country = account.Country, State = account.State });
        return TextRules.Same(existing.Address, submitted.Address) && TextRules.Same(existing.City, submitted.City) &&
            TextRules.Same(account.PostalCode, request.PostalCode) && TextRules.Same(submitted.Country, existing.Country) &&
            TextRules.Same(submitted.State, existing.State);
    }

    public string CreateOrganizationAccount(BillingRequest request)
    {
        options.Billing.Validate();
        if (options.Billing.Selector(request) != "SEPARATE" || BillingRules.RequestedErrors(request, options.Billing).Count != 0 ||
            string.IsNullOrWhiteSpace(options.Billing.EventSalesNotApplicableCode))
            throw new InvalidOperationException("Bill-To creation requires valid explicit separate billing and configured Not Applicable Event Sales status.");
        var resolved = ResolveBillingGeography(request);
        var model = new AllAccountsModel
        {
            Organization = options.OrganizationCode,
            Class = "O",
            Name = request.CompanyName,
            EventSalesStatus = options.Billing.EventSalesNotApplicableCode,
            Receivables = "A",
            MemberStatus = "A",
            Address1 = resolved.Address,
            City = request.City,
            State = resolved.State,
            PostalCode = request.PostalCode,
            Country = resolved.Country
        };
        var added = CallWrite("Create organization account", $"org={options.OrganizationCode}; order={Journal?.Evidence.Identity.Order}", model, write => client.Endpoints.Accounts.Add(write));
        if (string.IsNullOrWhiteSpace(added.AccountCode)) throw new UnknownWriteOutcomeException("Create organization account", $"org={options.OrganizationCode}; company={request.CompanyName}", new InvalidDataException("Momentus did not return the created AccountCode."));
        return added.AccountCode.Trim();
    }

    public AccountInfo UpdateAccountAddress(string accountCode, BillingRequest request)
    {
        throw new RecoveryReviewException("REVIEW: shared account address-update authority is not established; existing account is preserved.");
    }

    private IReadOnlyList<RelationshipsModel> FindRelationships(string masterAccount, string subordinateAccount, string relationshipType)
    {
        var filter = $"MasterAccountCode eq '{TextRules.OData(masterAccount)}' and SubordinateAccountCode eq '{TextRules.OData(subordinateAccount)}' and RelationshipType eq '{TextRules.OData(relationshipType)}'";
        filter += $" and SubordinateOrganizationCode eq '{TextRules.OData(options.OrganizationCode)}'";
        var existing = SearchAll(search => client.Endpoints.Relationships.Search(options.OrganizationCode, filter, search),
            client.Endpoints.Relationships.NavigateSearchList, "Relationships", filter,
            x => new[] { x.MasterOrganizationCode, x.MasterAccountCode, x.SubordinateOrganizationCode, x.SubordinateAccountCode, x.RelationshipType }
                .All(v => !string.IsNullOrWhiteSpace(v))
                ? JsonConvert.SerializeObject(new[] { x.MasterOrganizationCode, x.MasterAccountCode, x.SubordinateOrganizationCode, x.SubordinateAccountCode, x.RelationshipType }) : "",
            nameof(RelationshipsModel.MasterAccountCode));
        if (existing.Any(x => !TextRules.Same(x.MasterOrganizationCode, options.OrganizationCode) ||
            !TextRules.Same(x.MasterAccountCode, masterAccount) || !TextRules.Same(x.SubordinateOrganizationCode, options.OrganizationCode) ||
            !TextRules.Same(x.SubordinateAccountCode, subordinateAccount) || !TextRules.Same(x.RelationshipType, relationshipType)))
            throw new DecisionSearchException(SearchFailureKind.MalformedResponse, filter, 0, "Exact relationship query returned a different composite identity.");
        return existing;
    }

    internal bool HasActiveContactRelationship(string company, string contact) => FindRelationships(company, contact, "CTA")
        .Any(x => x.EventSalesDesignation is Ungerboeck.Api.Models.USISDKConstants.RelationshipDesignationStatus.Primary
            or Ungerboeck.Api.Models.USISDKConstants.RelationshipDesignationStatus.Secondary);

    internal RelationshipsModel? GetContactRelationship(string company, string contact) =>
        CallRead(() => client.Endpoints.Relationships.Get(options.OrganizationCode, company, contact, "CTA"), "Read contact affiliation");

    public void EnsureRelationship(string masterAccount, string subordinateAccount, string relationshipType)
    {
        if (Journal?.Evidence.Stages.Any(x => x.Status is StageStatus.Dispatching or StageStatus.Unknown or StageStatus.Succeeded) == true)
            throw new RecoveryReviewException("REVIEW: a dispatched stage is unresolved; reconcile it before establishing another relationship.");
        if (TextRules.Same(masterAccount, subordinateAccount)) return;
        var existing = FindRelationships(masterAccount, subordinateAccount, relationshipType);
        if (relationshipType == "CTA" && existing.Count > 0 && !existing.Any(x => x.EventSalesDesignation is
            Ungerboeck.Api.Models.USISDKConstants.RelationshipDesignationStatus.Primary or Ungerboeck.Api.Models.USISDKConstants.RelationshipDesignationStatus.Secondary))
            throw new RecoveryReviewException("REVIEW: the existing contact affiliation is inactive; automatic reactivation is not authorized.");
        if (existing.Any(x => TextRules.Same(x.MasterOrganizationCode, options.OrganizationCode) &&
            TextRules.Same(x.MasterAccountCode, masterAccount) && TextRules.Same(x.SubordinateOrganizationCode, options.OrganizationCode) &&
            TextRules.Same(x.SubordinateAccountCode, subordinateAccount) && TextRules.Same(x.RelationshipType, relationshipType))) return;
        var relationship = new RelationshipsModel
        {
            MasterOrganizationCode = options.OrganizationCode,
            MasterAccountCode = masterAccount,
            SubordinateOrganizationCode = options.OrganizationCode,
            SubordinateAccountCode = subordinateAccount,
            RelationshipType = relationshipType,
            // Bill-To links do not grant Event Sales participation to billing-only accounts.
            EventSalesDesignation = relationshipType == "BTO" ? null : relationshipType == "CTA"
                ? Ungerboeck.Api.Models.USISDKConstants.RelationshipDesignationStatus.Secondary : "P"
        };
        CallWrite("Add relationship", $"org={options.OrganizationCode}; master={masterAccount}; subordinate={subordinateAccount}; type={relationshipType}", relationship, write => client.Endpoints.Relationships.Add(write));
        CallRead(() => client.Endpoints.Relationships.Get(options.OrganizationCode, masterAccount, subordinateAccount, relationshipType));
    }

    public string CreateContact(string parentAccount, string parentCompany, BillingRequest request)
    {
        if (BillingRules.RequestedErrors(request, options.Billing).Count != 0 || !BillingRules.ValidCode(parentAccount))
            throw new InvalidOperationException("Contact creation requires valid Bill-To instructions and parent identity.");
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
        var plan = Journal?.Evidence.Plan ?? throw new InvalidOperationException("Contact creation requires a journaled billing plan.");
        BillingGuard.VerifyReusedAccount(this, plan);
        BillingState.SetContactAddress(model, plan);
        var added = CallWrite("Create contact", $"org={options.OrganizationCode}; parent={parentAccount}; order={Journal?.Evidence.Identity.Order}", model, write => client.Endpoints.Accounts.Add(write));
        if (string.IsNullOrWhiteSpace(added.AccountCode)) throw new UnknownWriteOutcomeException("Create contact", $"org={options.OrganizationCode}; parent={parentAccount}; email={request.Email}", new InvalidDataException("Momentus did not return the created contact AccountCode."));
        return added.AccountCode.Trim();
    }

    public void UpdateBillingContact(RunRow row)
    {
        BillingGuard.Instructions(this, row);
        BillingGuard.VerifyReusedContact(this, row);
        var model = GetAccountModel(row.FinalBillToContact);
        if (model.Class != "P" || !TextRules.Same(model.AccountCode, row.FinalBillToContact) ||
            !TextRules.Same(model.PrimaryAccount, row.EffectiveBilling!.Contact.PrimaryAccount) ||
            TextRules.NormalizeEmail(model.Email) != TextRules.NormalizeEmail(row.RequestedBilling.Email))
            throw new RecoveryReviewException("REVIEW: billing contact update requires an exact contact/email on the intended account.");
        if (row.EffectiveBilling.RequiresContactRelationship) BillingGuard.VerifyCrossAccountContact(this, row, checkUniqueness: true);
        BillingGuard.VerifyReusedAccount(this, row);
        var original = SerializeIntent("Update billing contact", model);
        model.FirstName = row.RequestedBilling.FirstName;
        model.LastName = row.RequestedBilling.LastName;
        if (row.EffectiveBilling.ContactAddressRequired) BillingState.SetContactAddress(model, row);
        CallWrite("Update billing contact", $"org={options.OrganizationCode}; contact={row.FinalBillToContact}; parent={row.FinalBillToAccount}",
            model, write => client.Endpoints.Accounts.Update(write), new() { ["original"] = original });
    }

    public ServiceOrdersModel UpdateOrder(ServiceOrdersModel order)
    {
        var operation = TextRules.Same(order.OrderStatus, "A") ? "Activate order" : "Update service order";
        var original = SerializeIntent(operation, GetOrder(Value(order.OrderNumber)));
        return CallWrite(operation, $"org={options.OrganizationCode}; order={order.OrderNumber}", order, write => client.Endpoints.ServiceOrders.Update(write), new() { ["original"] = original });
    }
    public ExhibitorsModel UpdateExhibitor(ExhibitorsModel exhibitor)
    {
        var operation = Value(exhibitor.ExhibitorStatus) == 2 ? "Activate exhibitor" : "Update exhibitor categories";
        var original = SerializeIntent(operation, GetExhibitor(Value(exhibitor.ExhibitorID)));
        return CallWrite(operation, $"org={options.OrganizationCode}; exhibitor={exhibitor.ExhibitorID}", exhibitor, write => client.Endpoints.Exhibitors.Update(write), new() { ["original"] = original });
    }
    internal ServiceOrdersModel UpdatePartialOrder(ServiceOrdersModel order, bool billing)
    {
        if (!TextRules.Same(order.OrderStatus, "PC")) throw new RecoveryReviewException("REVIEW: partial order must remain PC.");
        var operation = billing ? "Assign partial billing" : "Update partial order";
        var original = SerializeIntent(operation, GetOrder(Value(order.OrderNumber)));
        return CallWrite(operation, $"org={options.OrganizationCode}; order={order.OrderNumber}", order,
            write => client.Endpoints.ServiceOrders.Update(write), new() { ["original"] = original });
    }
    internal ExhibitorsModel UpdatePartialExhibitor(ExhibitorsModel exhibitor)
    {
        if (Value(exhibitor.ExhibitorStatus) != 35) throw new RecoveryReviewException("REVIEW: partial exhibitor must remain at status 35.");
        const string operation = "Update partial exhibitor categories";
        var original = SerializeIntent(operation, GetExhibitor(Value(exhibitor.ExhibitorID)));
        return CallWrite(operation, $"org={options.OrganizationCode}; exhibitor={exhibitor.ExhibitorID}", exhibitor,
            write => client.Endpoints.Exhibitors.Update(write), new() { ["original"] = original });
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

    internal static readonly string[] AccountMatchFields =
    [
        nameof(AllAccountsModel.Organization), nameof(AllAccountsModel.AccountCode), nameof(AllAccountsModel.Class),
        nameof(AllAccountsModel.Name), nameof(AllAccountsModel.Company), nameof(AllAccountsModel.FirstName),
        nameof(AllAccountsModel.LastName), nameof(AllAccountsModel.Email), nameof(AllAccountsModel.Address1),
        nameof(AllAccountsModel.City), nameof(AllAccountsModel.State), nameof(AllAccountsModel.PostalCode),
        nameof(AllAccountsModel.Country), nameof(AllAccountsModel.PrimaryAccount), nameof(AllAccountsModel.EventSalesStatus)
    ];

    private IReadOnlyList<AllAccountsModel> SearchAccounts(string filter, int? maximumResults = null, bool optional = false)
    {
        var search = SearchOptions(nameof(AllAccountsModel.AccountCode));
        // Full pagination and every matching/identity/address field remain required.
        // Account UDF collections and unrelated fields are fetched only by exact account GET when needed.
        search.Select = AccountMatchFields.ToList();
        if (maximumResults.HasValue) search.MaxResults = Math.Min(search.MaxResults, maximumResults.Value);
        return SearchAllCore(
            page => client.Endpoints.Accounts.Search(options.OrganizationCode, filter, page),
            client.Endpoints.Accounts.NavigateSearchList, "Accounts", filter, x => Clean(x.AccountCode), search, recordFailure: !optional);
    }

    private IReadOnlyList<DocumentsModel> SearchDocuments(string filter) => SearchAll(
        search => client.Endpoints.Documents.Search(options.OrganizationCode, filter, search),
        client.Endpoints.Documents.NavigateSearchList, "Documents", filter,
        x => !string.IsNullOrWhiteSpace(x.Type) && Value(x.SequenceNumber) > 0 ? $"{Clean(x.Type)}/{Value(x.SequenceNumber)}" : "",
        nameof(DocumentsModel.SequenceNumber), nameof(DocumentsModel.Type));

    private static string NumericIdentity(object? value) => Value(value) > 0 ? Clean(value) : "";

    private IReadOnlyList<T> SearchAll<T>(Func<Search, SearchResponse<T>> firstPage, Func<string, Search, SearchResponse<T>> nextPage,
        string endpoint, string filter, Func<T, string> identity, params string[] orderBy) where T : UngerboeckModel
        => SearchAllCore(firstPage, nextPage, endpoint, filter, identity, SearchOptions(orderBy));

    private IReadOnlyList<T> SearchAllCore<T>(Func<Search, SearchResponse<T>> firstPage, Func<string, Search, SearchResponse<T>> nextPage,
        string endpoint, string filter, Func<T, string> identity, Search searchOptions, bool recordFailure = true) where T : UngerboeckModel
    {
        var description = endpoint == "Accounts" && filter.StartsWith("Class eq 'O'", StringComparison.Ordinal)
            ? filter.Contains("substringof(", StringComparison.Ordinal)
                ? "Reading all pages of the targeted billing-company name search"
                : "Reading the complete billing-company organization search"
            : endpoint == "Accounts" && filter.StartsWith("Class eq 'P'", StringComparison.Ordinal)
                ? filter.Contains("PrimaryAccount eq", StringComparison.Ordinal)
                ? "Reading contacts for the selected Bill-To account"
                    : filter.Contains("substringof(", StringComparison.Ordinal) ? "Reading all pages of the targeted billing-email fragment search"
                    : "Reading the complete global contact-email fallback search"
                : $"Reading the complete {endpoint} search";
        using var searchProgress = Progress?.Begin(description, announce: false);
        SearchResponse<T> ReportPage(SearchResponse<T> response)
        {
            if (response?.SearchMetadata is { } metadata && metadata.PageTotal > 1 &&
                (metadata.Page == 1 || metadata.Page % 10 == 0 || metadata.Page == metadata.PageTotal))
                Progress?.Message($"{endpoint} search: received page {metadata.Page:N0} of {metadata.PageTotal:N0} ({metadata.ResultsTotal:N0} records reported). Checking all pages before using matches.");
            return response!; // CompleteSearch retains responsibility for rejecting malformed/null responses.
        }
        try
        {
            return CompleteSearch.Read(search => ReportPage(CallRead(() => firstPage(search), $"Search {endpoint}")),
                (url, search) => ReportPage(CallRead(() => nextPage(url, search), $"Search {endpoint}")), searchOptions, $"{endpoint}: {filter}",
                new Uri(options.BaseUrl.TrimEnd('/') + $"/api/v1/{endpoint}/{Uri.EscapeDataString(options.OrganizationCode)}"), identity);
        }
        catch (DecisionSearchException ex)
        {
            if (recordFailure && Journal is { } journal)
            {
                journal.Evidence.SearchFailures.Add($"{DateTimeOffset.UtcNow:O}: {ex.Message}");
                journal.Save();
            }
            throw;
        }
    }

    private Search SearchOptions(params string[] orderBy) => new() { PageSize = options.PageSize, MaxResults = options.MaxResults, OrderBy = orderBy.ToList() };
    private T TimedRequest<T>(Func<T> action, string operation)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try { return action(); }
        finally { Progress?.RecordRequest(operation, elapsed.Elapsed); }
    }

    private T CallRead<T>(Func<T> action, string operation = "Read Momentus record")
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            T result;
            requests++;
            client.LastResponseError = null;
            try
            {
                result = TimedRequest(action, operation);
                ThrowSdkError();
            }
            catch (Exception ex) when (attempt < 3 && IsTransientRead(ex))
            {
                Progress?.Message($"A read request failed temporarily; waiting {1 << attempt}s before read retry {attempt + 1}/3.");
                delay(TimeSpan.FromSeconds(1 << attempt));
                continue;
            }
            PauseAfterRequest();
            return result;
        }
        throw new InvalidOperationException("Unreachable read retry state.");
    }

    private T CallWrite<T>(string operation, string target, T intent, Func<T, T> action, Dictionary<string, string>? source = null)
    {
        if (documentReadSnapshot is not null || handoffAccountSnapshot is not null)
            throw new InvalidOperationException("A read-only verification snapshot cannot authorize a mutation; end verification before dispatch.");
        using var writeProgress = Progress?.Begin($"Checking current data before: {operation}");
        var journal = Journal ?? throw new InvalidOperationException("External mutations require a durable per-order journal.");
        if (operation == "Create organization account" ||
            operation == "Add relationship" && intent is RelationshipsModel relationship && relationship.RelationshipType == "BTO")
        {
            // Run this after the mutation's absence/baseline searches, at the dispatch boundary.
            BillingGuard.Instructions(this, journal.Evidence.Plan);
            if (options.Billing.Selector(journal.Evidence.Plan.RequestedBilling) != "SEPARATE")
                throw new RecoveryReviewException("REVIEW: Use Above Address cannot authorize separate Bill-To mutations.");
        }
        if (operation is "Create contact" or "Update billing contact")
        {
            BillingGuard.Instructions(this, journal.Evidence.Plan);
            var request = journal.Evidence.Plan.RequestedBilling;
            if (intent is not AllAccountsModel contact || contact.Class != "P" ||
                !TextRules.Same(contact.PrimaryAccount, operation == "Create contact" ? journal.Evidence.Plan.FinalBillToAccount :
                    journal.Evidence.Plan.EffectiveBilling!.Contact.PrimaryAccount) ||
                operation == "Update billing contact" && !TextRules.Same(contact.AccountCode, journal.Evidence.Plan.FinalBillToContact) ||
                contact.FirstName != request.FirstName || contact.LastName != request.LastName ||
                TextRules.NormalizeEmail(contact.Email) != TextRules.NormalizeEmail(request.Email) ||
                BillingRules.RequestedContactErrors(request).Count != 0)
                throw new RecoveryReviewException("REVIEW: billing contact write requires the intended account and requested contact.");
            if (journal.Evidence.Plan.EffectiveBilling!.ContactAddressRequired &&
                !TextRules.AddressMatches(ToAccount(contact), BillingState.ContactRequest(journal.Evidence.Plan)))
                throw new RecoveryReviewException("REVIEW: billing contact write requires the selected contract billing address.");
        }
        if (operation == "Add relationship" && intent is RelationshipsModel contactLink && contactLink.RelationshipType == "CTA" &&
            journal.Evidence.Plan.EffectiveBilling?.RequiresContactRelationship == true)
        {
            var plan = journal.Evidence.Plan;
            BillingGuard.Instructions(this, plan);
            if (!TextRules.Same(contactLink.MasterAccountCode, plan.FinalBillToAccount) ||
                !TextRules.Same(contactLink.SubordinateAccountCode, plan.FinalBillToContact) ||
                !TextRules.Same(contactLink.MasterOrganizationCode, options.OrganizationCode) ||
                !TextRules.Same(contactLink.SubordinateOrganizationCode, options.OrganizationCode) ||
                contactLink.EventSalesDesignation != Ungerboeck.Api.Models.USISDKConstants.RelationshipDesignationStatus.Secondary)
                throw new RecoveryReviewException("REVIEW: contact relationship must preserve the intended company/contact identity and secondary designation.");
            BillingGuard.VerifyReusedAccount(this, plan);
            BillingGuard.VerifyCrossAccountContact(this, plan, checkUniqueness: true);
        }
        if (journal.Evidence.Stages.Any(x => x.Status is StageStatus.Dispatching or StageStatus.Unknown or StageStatus.Succeeded))
            throw new RecoveryReviewException("REVIEW: a prior dispatched stage is unresolved; no new mutation can be dispatched before reconciliation and durable verification.");
        // Persist only intended mutable values; full SDK responses can change on readback.
        if (operation == "Activate order")
        {
            if (journal.Evidence.Handoff is null) throw new RecoveryReviewException("REVIEW: activation requires a verified final handoff.");
            HandoffRules.RequireOrderStages(journal.Evidence, includeActivation: false, includeEmail: false);
        }
        if (operation == "Send ready email")
        {
            if (!journal.Evidence.OrderComplete || journal.Evidence.Handoff is null)
                throw new RecoveryReviewException("REVIEW: final email requires verified order completion and activation checks.");
            HandoffRules.RequireOrderStages(journal.Evidence, includeActivation: true, includeEmail: false);
        }
        if (MutationGuard is not null) intent = (T)MutationGuard(operation, intent!);
        if (operation == "Add relationship" && intent is RelationshipsModel { RelationshipType: "CTA" } &&
            journal.Evidence.Plan.EffectiveBilling?.RequiresContactRelationship == true)
        {
            source = new Dictionary<string, string>(source ?? [])
            {
                ["ContactBeforeAffiliation"] = JsonConvert.SerializeObject(journal.Evidence.Plan.EffectiveBilling.Contact)
            };
        }
        var stage = journal.Prepare(operation, target, SerializeIntent(operation, intent), source);
        if (stage.Status == StageStatus.Verified) return JsonConvert.DeserializeObject<T>(stage.Result)!;
        BeforeWriteDispatch?.Invoke(); // Reserve this order's allowance only when a write will be dispatched.
        journal.Dispatching(stage); // Must reach durable storage before dispatch.
        using var dispatchProgress = Progress?.Begin($"Saving change: {operation}");
        T result;
        requests++;
        client.LastResponseError = null;
        try
        {
            // No retry: losing a response never proves that the mutation failed.
            result = TimedRequest(() => action(intent), operation);
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
        using var verifyProgress = Progress?.Begin($"Momentus accepted {operation}; reading it back to verify");
        if (!VerifyStage(stage))
        {
            var unknown = new UnknownWriteOutcomeException(operation, target, new InvalidDataException("Returned identity or exact-target readback could not establish the intended effect."));
            journal.Outcome(stage, StageStatus.Unknown, unknown.Message);
            throw unknown;
        }
        journal.Verified(stage, stage.Verification);
        Progress?.Message($"Verified: {operation}.");
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
            "Create organization account" when value is AllAccountsModel a => new { a.Organization, a.Class, a.Name, a.EventSalesStatus, a.Receivables, a.MemberStatus, a.Address1, a.City, a.State, a.PostalCode, a.Country },
            "Create contact" when value is AllAccountsModel a => new { a.Organization, a.Class, a.PrimaryAccount, a.Company, a.FirstName, a.LastName, a.Email, a.Address1, a.City, a.State, a.PostalCode, a.Country },
            "Update billing contact" when value is AllAccountsModel a => new { a.AccountCode, a.Class, a.PrimaryAccount, a.FirstName, a.LastName, a.Email, a.Address1, a.City, a.State, a.PostalCode, a.Country },
            "Update account address" when value is AllAccountsModel a => new { a.AccountCode, a.Address1, a.City, a.State, a.PostalCode, a.Country },
            "Add relationship" when value is RelationshipsModel r && r.RelationshipType == "CTA" => new
                { r.MasterOrganizationCode, r.MasterAccountCode, r.SubordinateOrganizationCode, r.SubordinateAccountCode, r.RelationshipType, r.EventSalesDesignation },
            "Add relationship" when value is RelationshipsModel r => new { r.MasterOrganizationCode, r.MasterAccountCode, r.SubordinateOrganizationCode, r.SubordinateAccountCode, r.RelationshipType },
            "Update service order" when value is ServiceOrdersModel o => new { o.OrderNumber, o.OrderAccountRep, o.Category, o.BoothNumber, o.BillToAccount, o.BillToContact },
            "Update partial order" or "Assign partial billing" when value is ServiceOrdersModel o => new { o.OrderNumber, o.OrderStatus, o.OrderAccountRep, o.Category, o.BoothNumber, o.BillToAccount, o.BillToContact },
            "Activate order" when value is ServiceOrdersModel o => new { o.OrderNumber, o.OrderStatus },
            "Update exhibitor categories" when value is ExhibitorsModel e => new { e.ExhibitorID, e.ExhibitorCategory },
            "Update partial exhibitor categories" when value is ExhibitorsModel e => new { e.ExhibitorID, e.ExhibitorStatus, e.ExhibitorCategory },
            "Activate exhibitor" when value is ExhibitorsModel e => new { e.ExhibitorID, e.ExhibitorStatus },
            "Copy contract document" when value is DocumentsModel d => new { d.Type, d.NewFileName, d.Description, d.Category, d.Order, d.Event, d.Function, d.Account },
            "Add payment schedule note" or "Update payment schedule note" when value is NotesModel n => new { n.OrganizationCode, n.Type, n.OrderNumber, n.SequenceNumber, n.Class, n.Title, n.Text },
            "Send ready email" or "Send exception summary" when value is EmailsModel e => new { e.Organization, e.EmailSubject, e.HtmlText, e.SendToAddresses, SaveOrder = e.SaveAsUngerboeckDocument?.Order,
                AttachmentManifest = e.Attachments.Select(a => new { a.FileName, a.Description, Hash = BytesHash(Convert.FromBase64String(a.FileData)) }).ToArray() },
            _ => throw new InvalidOperationException($"No durable intent defined for {operation}.")
        };
        var serialized = Newtonsoft.Json.Linq.JObject.FromObject(fields);
        // Retain the exact legacy intent shape when no copies were requested. Copies are part of
        // dispatch identity and accepted-response verification whenever they are present.
        if (value is EmailsModel email)
        {
            if (email.CCAddresses.Count > 0) serialized["CCAddresses"] = Newtonsoft.Json.Linq.JArray.FromObject(email.CCAddresses);
            if (email.BCCAddresses.Count > 0) serialized["BCCAddresses"] = Newtonsoft.Json.Linq.JArray.FromObject(email.BCCAddresses);
        }
        return serialized.ToString(Newtonsoft.Json.Formatting.None);
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

    private static string ContactIntentForStage(StageEvidence stage, AllAccountsModel current)
    {
        var result = Newtonsoft.Json.Linq.JObject.Parse(SerializeIntent(stage.Operation, current));
        var original = Newtonsoft.Json.Linq.JObject.Parse(stage.Intent);
        // Historical contact writes did not include addresses. Reconcile only their recorded fields.
        foreach (var field in new[] { "Address1", "City", "State", "PostalCode", "Country" })
            if (original.Property(field) is null) result.Remove(field);
        return result.ToString(Newtonsoft.Json.Formatting.None);
    }

    private bool VerifyStage(StageEvidence stage)
    {
        object? verified = null;
        switch (stage.Operation)
        {
            case "Update billing contact":
            {
                var intended = JsonConvert.DeserializeObject<AllAccountsModel>(stage.Intent)!;
                if (!BillingRules.ValidCode(intended.AccountCode)) return false;
                var current = GetAccountModel(intended.AccountCode);
                if (ContactIntentForStage(stage, current) != stage.Intent) return false;
                verified = current;
                break;
            }
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
                var currentIntent = stage.Operation == "Create contact" ? ContactIntentForStage(stage, current) : SerializeIntent(stage.Operation, current);
                if (stage.Operation == "Create organization account")
                {
                    // Historical stages verify only the status fields included in their original creation intent.
                    var legacy = Newtonsoft.Json.Linq.JObject.Parse(currentIntent);
                    var original = Newtonsoft.Json.Linq.JObject.Parse(stage.Intent);
                    foreach (var field in new[] { "EventSalesStatus", "Receivables", "MemberStatus" })
                        if (original.Property(field) is null) legacy.Remove(field);
                    currentIntent = legacy.ToString(Newtonsoft.Json.Formatting.None);
                }
                var intendedIntent = stage.Intent;
                if (stage.Operation == "Create contact" && string.IsNullOrWhiteSpace(current.PrimaryAccount))
                {
                    // Some tenants ignore PrimaryAccount on contact creation. Verify the returned stable contact ID
                    // and every other intended field here; the separately verified CTA must precede order assignment.
                    var identity = Newtonsoft.Json.Linq.JObject.Parse(currentIntent);
                    var intendedIdentity = Newtonsoft.Json.Linq.JObject.Parse(intendedIntent);
                    identity.Remove("PrimaryAccount");
                    intendedIdentity.Remove("PrimaryAccount");
                    currentIntent = identity.ToString(Newtonsoft.Json.Formatting.None);
                    intendedIntent = intendedIdentity.ToString(Newtonsoft.Json.Formatting.None);
                }
                if (!TextRules.Same(current.AccountCode, returned.AccountCode) || currentIntent != intendedIntent) return false;
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
            case "Update partial order":
            case "Assign partial billing":
            case "Activate order":
            {
                var intended = JsonConvert.DeserializeObject<ServiceOrdersModel>(stage.Intent)!;
                var current = GetOrder(Value(intended.OrderNumber));
                if (SerializeIntent(stage.Operation, current) != stage.Intent) return false;
                verified = current;
                break;
            }
            case "Update exhibitor categories":
            case "Update partial exhibitor categories":
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
                    !baseline.Contains($"{x.Type}/{x.SequenceNumber}") && !baseline.Contains(x.SequenceNumber.ToString(CultureInfo.InvariantCulture)) && TextRules.Same(x.Category, intended.Category))
                    .Where(x => BytesHash(DownloadDocument(x)) == stage.Source["contentHash"]).ToList();
                if (matches.Count != 1) return false;
                var match = matches[0];
                stage.Source["destinationType"] = match.Type;
                stage.Source["destinationSequence"] = match.SequenceNumber.ToString(CultureInfo.InvariantCulture);
                stage.Source["destinationHash"] = stage.Source["contentHash"];
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
                    (sequence == 0 ? !stage.Source.GetValueOrDefault("baseline", "").Split(',').Contains(Clean(x.SequenceNumber)) : Value(x.SequenceNumber) == sequence) && TextRules.Same(x.Title, intended.Title) &&
                    PaymentScheduleExtractor.NormalizeForComparison(First(x.PlainText ?? "", x.Text ?? "")) == PaymentScheduleExtractor.NormalizeForComparison(intended.Text)).ToList();
                if (matches.Count != 1 || Value(matches[0].SequenceNumber) == 0) return false;
                stage.Source["managedNoteSequence"] = Clean(matches[0].SequenceNumber);
                stage.Source["noteHash"] = ManagedNoteRules.ContentHash(ManagedNoteRules.Text(matches[0]));
                verified = matches[0];
                break;
            }
            case "Send ready email":
            case "Send exception summary":
            {
                // The saved email is corroboration of an accepted response, not proof of delivery.
                // Without that response, description alone cannot prove recipient/body/attachments.
                var intended = JsonConvert.DeserializeObject<EmailsModel>(stage.Intent)!;
                var baseline = stage.Source["baseline"].Split(',').ToHashSet();
                var matches = GetSavedEmails(int.Parse(stage.Source["order"], CultureInfo.InvariantCulture))
                    .Where(x => !baseline.Contains(Clean(x.SequenceNumber)) && TextRules.Same(x.Description, intended.EmailSubject)).ToList();
                var accepted = JsonConvert.DeserializeObject<EmailsModel>(stage.Result);
                if (accepted is null || matches.Count != 1 || (Newtonsoft.Json.Linq.JObject.Parse(stage.Intent)["AttachmentManifest"] is not null
                        ? SerializeIntent(stage.Operation, accepted) != stage.Intent
                        : accepted.CCAddresses.Count > 0 || accepted.BCCAddresses.Count > 0 ||
                          JsonConvert.SerializeObject(new { accepted.Organization, accepted.EmailSubject, accepted.HtmlText, accepted.SendToAddresses, accepted.SaveAsUngerboeckDocument }) != stage.Intent) ||
                    matches[0].Order != int.Parse(stage.Source["order"], CultureInfo.InvariantCulture) ||
                    OrderIdentity.Hash(accepted.HtmlText ?? "") != stage.Source["bodyHash"] ||
                    string.Join(",", accepted.Attachments.Select(a => BytesHash(Convert.FromBase64String(a.FileData)))) != stage.Source["attachmentHashes"]) return false;
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
    internal static AccountInfo ToAccount(AllAccountsModel x) => new(Clean(x.AccountCode), x.Class == "O" ? x.Name ?? "" : First(Clean(x.Name), Clean(x.Company)), Clean(x.FirstName), Clean(x.LastName), Clean(x.Email), Clean(x.Address1), Clean(x.City), Clean(x.State), Clean(x.PostalCode), Clean(x.Country), Clean(x.PrimaryAccount))
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
