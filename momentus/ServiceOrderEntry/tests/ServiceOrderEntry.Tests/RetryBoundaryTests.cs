using System.Net;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Options;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class RetryBoundaryTests
{
    internal static BillingConfiguration TestBillingConfiguration() => new()
    { Header = "OrgAccountUDF", Class = "B", Type = "BL", EventSalesNotApplicableCode = "N" }; // Synthetic offline tenant values.
    internal static BillingRequest TestRequest() => new("Offline Test Company", "", "Test", "Contact", "test@example.invalid", "BA", "1 Test Street", "Test City", "", "12345", "USA");
    internal static AccountInfo TestAccount() => new("ACCOUNT", "Offline Test Company", "", "", "", "1 Test Street", "Test City", "", "12345", "***", "")
    { AccountClass = "O", EventSalesStatus = "A" };
    internal static AccountInfo TestContact() => new("CONTACT", "Offline Test Company", "Test", "Contact", "test@example.invalid", "", "", "", "", "", "ACCOUNT")
    { AccountClass = "P" };
    internal static UserFields TestFields(BillingRequest request) => new()
    {
        Header = "OrgAccountUDF", Class = "B", Type = "BL", UserText03 = request.CompanyName, UserText13 = request.AttentionOf,
        UserText05 = request.FirstName, UserText09 = request.LastName, UserText10 = request.Email, UserText11 = request.UseRequestedAddress,
        UserText04 = request.Address, UserText15 = request.City, UserText06 = request.State, UserText07 = request.PostalCode, UserText08 = request.Country
    };

    [Fact]
    public void LegacyRelationshipPrimitiveStillDispatchesOnlyOnceOnUnknownOutcome()
    {
        using var scenario = new Scenario("Add CTA relationship");
        Assert.Throws<UnknownWriteOutcomeException>(() => scenario.Gateway.EnsureRelationship("ACCOUNT", "CONTACT", "CTA"));
        Assert.Single(scenario.Transport.Mutations, x => x == "Add CTA relationship");
        Assert.Equal(StageStatus.Unknown, scenario.Store.Load().Single().Stages.Single().Status);
        Assert.Throws<RecoveryReviewException>(() => scenario.Gateway.EnsureRelationship("ACCOUNT", "CONTACT", "CTA"));
        Assert.Single(scenario.Transport.Mutations);
    }
    [Theory]
    [InlineData("Create organization account")]
    [InlineData("Add BTO relationship")]
    [InlineData("Create contact")]
    [InlineData("Update service order")]
    [InlineData("Copy contract document")]
    [InlineData("Add payment schedule note")]
    [InlineData("Update payment schedule note")]
    [InlineData("Update exhibitor categories")]
    [InlineData("Send ready email")]
    [InlineData("Activate order")]
    [InlineData("Activate exhibitor")]
    public void AcceptedWriteThenTimeoutStopsAllSubsequentStages(string stage)
    {
        using var scenario = new Scenario(stage);
        scenario.Apply();

        Assert.True(scenario.Transport.Mutations.Count(x => x == stage) == 1, scenario.Row.UpdateMessage);
        Assert.Equal(stage, scenario.Transport.Mutations.Last());
        Assert.Equal("UNKNOWN WRITE OUTCOME", scenario.Row.ServiceOrderUpdateStatus);
        Assert.Contains("dispatched once", scenario.Row.UpdateMessage);
        Assert.Contains("org=10", scenario.Row.UpdateMessage);
        Assert.Contains("HttpCallFailed", scenario.Row.UpdateMessage);
        Assert.Equal(1, Runner.ApplyExitCode([scenario.Row]));
        // Earlier confirmed effects are retained, but no stage after uncertainty runs.
        if (stage is not ("Send ready email" or "Activate order" or "Activate exhibitor"))
            Assert.DoesNotContain("Send ready email", scenario.Transport.Mutations);
        if (stage != "Activate exhibitor") Assert.DoesNotContain("Activate exhibitor", scenario.Transport.Mutations);
        if (stage is not ("Activate order" or "Activate exhibitor")) Assert.DoesNotContain("Activate order", scenario.Transport.Mutations);
        if (stage == "Send ready email")
        {
            Assert.Equal("UNKNOWN WRITE OUTCOME", scenario.Row.ReadyEmailStatus);
            Assert.True(Directory.Exists(scenario.Options.StateFolder));
            Assert.Contains(new FileJournalStore(scenario.Options.StateFolder).Load().Single().Stages, x => x.Operation == "Send ready email" && x.Status == StageStatus.Unknown);
        }
    }

    [Fact]
    public void TransientReadRetriesAndSucceeds()
    {
        using var scenario = new Scenario();
        scenario.Transport.ReadFailures = 1;

        var order = scenario.Gateway.GetOrder(3);

        Assert.Equal(3, order.OrderNumber);
        Assert.Equal(2, scenario.Transport.Requests.Count);
        Assert.Equal(2, scenario.Gateway.RequestCount);
        Assert.Equal([TimeSpan.FromSeconds(2)], scenario.Delays);
        Assert.Empty(scenario.Transport.Mutations);
    }

    [Fact]
    public void ReadRetriesAreBounded()
    {
        using var scenario = new Scenario();
        scenario.Transport.ReadFailures = 10;

        Assert.ThrowsAny<Exception>(() => scenario.Gateway.GetOrder(3));

        Assert.Equal(3, scenario.Transport.Requests.Count);
        Assert.Equal(3, scenario.Gateway.RequestCount);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, scenario.Delays);
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    public void ReadRetriesOnlyTransientResponseStatuses(int status, bool retries)
    {
        using var scenario = new Scenario();
        scenario.Transport.ReadFailures = 1;
        scenario.Transport.ReadFailureStatus = status;
        if (retries) Assert.Equal(3, scenario.Gateway.GetOrder(3).OrderNumber);
        else Assert.ThrowsAny<Exception>(() => scenario.Gateway.GetOrder(3));
        Assert.Equal(retries ? 2 : 1, scenario.Transport.Requests.Count);
    }

    [Fact]
    public void ConfirmedWriteRejectionIsFailedAndNeverSuccessful()
    {
        using var scenario = new Scenario("Update service order");
        scenario.Transport.WriteFailureStatus = 400;
        scenario.Apply();

        Assert.Equal("FAILED", scenario.Row.ServiceOrderUpdateStatus);
        Assert.Contains("WRITE REJECTED", scenario.Row.UpdateMessage);
        Assert.DoesNotContain("UNKNOWN WRITE OUTCOME", scenario.Row.UpdateMessage);
        Assert.Empty(scenario.Transport.Mutations);
        Assert.Single(scenario.Transport.Requests, x => x.StartsWith("PUT "));
        Assert.Equal("NOT ATTEMPTED", scenario.Row.ReadyEmailStatus);
        Assert.Equal(1, Runner.ApplyExitCode([scenario.Row]));
    }

    [Theory]
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void UncertainWriteResponseIsUnknownAndNotRetried(int status)
    {
        using var scenario = new Scenario("Update service order");
        scenario.Transport.WriteFailureStatus = status;
        scenario.Apply();

        Assert.Equal("UNKNOWN WRITE OUTCOME", scenario.Row.ServiceOrderUpdateStatus);
        Assert.Single(scenario.Transport.Requests, x => x.StartsWith("PUT "));
        Assert.Equal("NOT ATTEMPTED", scenario.Row.ReadyEmailStatus);
        Assert.Empty(scenario.Delays);
    }

    [Fact]
    public void AcceptedWriteThenConnectionLossIsUnknownAndNotRetried()
    {
        using var scenario = new Scenario("Update service order");
        scenario.Transport.WriteFailure = new HttpRequestException("Connection interrupted after acceptance.");
        scenario.Apply();

        Assert.Equal("UNKNOWN WRITE OUTCOME", scenario.Row.ServiceOrderUpdateStatus);
        Assert.Equal(new[] { "Update service order" }, scenario.Transport.Mutations);
        Assert.Equal("NOT ATTEMPTED", scenario.Row.ReadyEmailStatus);
    }

    [Fact]
    public void GatewayThrowsTypedUnknownOutcomeWithOperationTargetAndCause()
    {
        using var scenario = new Scenario("Update service order");

        var error = Assert.Throws<UnknownWriteOutcomeException>(() => scenario.Gateway.UpdateOrder(scenario.Transport.Order));

        Assert.Equal("Update service order", error.Operation);
        Assert.Contains("order=3", error.Target);
        Assert.NotNull(error.InnerException);
        Assert.Contains("HttpCallFailed", error.Message);
        Assert.Equal(2, scenario.Gateway.RequestCount); // Original-state read plus one dispatch.
        Assert.Single(scenario.Transport.Requests, x => x.StartsWith("PUT "));
        Assert.Single(scenario.Transport.Mutations);
        Assert.Empty(scenario.Delays);
    }

    [Fact]
    public void GatewayThrowsTypedConfirmedRejection()
    {
        using var scenario = new Scenario("Update service order");
        scenario.Transport.WriteFailureStatus = 400;

        Assert.Throws<WriteRejectedException>(() => scenario.Gateway.UpdateOrder(scenario.Transport.Order));

        Assert.Equal(2, scenario.Gateway.RequestCount); // Original-state read plus one dispatch.
        Assert.Single(scenario.Transport.Requests, x => x.StartsWith("PUT "));
        Assert.Empty(scenario.Transport.Mutations);
    }

    [Fact]
    public void SuccessfulWriteResponseWithoutCreatedIdentityIsUnknown()
    {
        using var scenario = new Scenario("Create organization account");
        scenario.Transport.OmitCreatedIdentity = true;
        scenario.Apply();

        Assert.Equal("UNKNOWN WRITE OUTCOME", scenario.Row.ServiceOrderUpdateStatus);
        Assert.Equal(new[] { "Create organization account" }, scenario.Transport.Mutations);
        Assert.Equal("NOT ATTEMPTED", scenario.Row.ReadyEmailStatus);
    }

    [Fact]
    public void EmptyMutationResponseIsUnknownAndStopsTheRecord()
    {
        using var scenario = new Scenario("Update service order");
        scenario.Transport.EmptyMutationResponse = true;
        scenario.Apply();

        Assert.Equal("UNKNOWN WRITE OUTCOME", scenario.Row.ServiceOrderUpdateStatus);
        Assert.Contains("no mutation response", scenario.Row.UpdateMessage);
        Assert.Equal(new[] { "Update service order" }, scenario.Transport.Mutations);
        Assert.Equal("NOT ATTEMPTED", scenario.Row.ReadyEmailStatus);
    }

    [Fact]
    public void UnknownOutcomeEvidenceIsIncludedInRunCsv()
    {
        using var scenario = new Scenario("Update service order");
        scenario.Apply();

        var path = CsvRunWriter.Write(scenario.Options.RunFolder, DateTime.Now, [scenario.Row], true);
        var csv = File.ReadAllText(path);

        Assert.Contains("UNKNOWN WRITE OUTCOME", csv);
        Assert.Contains("Update service order", csv);
        Assert.Contains("order=3", csv);
        Assert.Contains("HttpCallFailed", csv);
    }

    [Fact]
    public void SuccessfulApplyStillCompletesEmailAndRequestedActivation()
    {
        using var scenario = new Scenario();
        scenario.Apply();

        Assert.True(scenario.Row.ServiceOrderUpdateStatus == "COMPLETED", scenario.Row.UpdateMessage);
        Assert.Equal("SENT", scenario.Row.ReadyEmailStatus);
        Assert.Equal(new[] { "Update service order", "Copy contract document", "Add payment schedule note",
            "Update exhibitor categories", "Send ready email", "Activate order", "Activate exhibitor" }, scenario.Transport.Mutations);
        Assert.Equal(0, Runner.ApplyExitCode([scenario.Row]));
    }

    // Real SDK endpoints use an in-memory HTTP handler. No environment credentials
    // are loaded, and every unexpected URL fails the test without network access.
    internal sealed class Scenario : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "service-order-entry-tests", Guid.NewGuid().ToString("N"));
        private readonly ApiClient client;
        public CliOptions Options { get; }
        public FakeTransport Transport { get; }
        public MomentusGateway Gateway { get; }
        public List<TimeSpan> Delays { get; } = [];
        public IJournalStore Store { get; set; }
        public RunRow Row { get; } = new()
        {
            EventId = 1, ExhibitorId = 2, OrderNumber = 3, ProposedOrderAccountRep = "REP", ProposedCategory = 28,
            ProposedBoothNumber = "101", FinalBillToAccount = "ACCOUNT", FinalBillToContact = "CONTACT",
            BillToAddressAction = "KEEP EXISTING", ContactAction = "KEEP EXISTING", FinalExhibitorCategories = "1",
            PaymentScheduleText = "Payment Schedule\n50% Deposit", ReadyEmailRecipient = "test@example.invalid",
            ExistingBillToAccount = RunRow.EmptyAccount with { AccountCode = "ACCOUNT" },
            RequestedBilling = RunRow.EmptyBilling with
            {
                CompanyName = "Offline Test Company", FirstName = "Test", LastName = "Contact", Email = "test@example.invalid",
                Address = "1 Test Street", City = "Test City", PostalCode = "12345", Country = "USA"
            }
        };

        public Scenario(string? failingStage = null, BillingConfiguration? billing = null)
        {
            Options = CliOptions.Parse(["preview"]) with
            {
                Apply = true, Confirm = true, ActivateOrder = true, ActivateExhibitor = true,
                Billing = billing ?? TestBillingConfiguration(), BaseUrl = "https://offline.invalid/prod", StateFolder = Path.Combine(folder, "state"),
                RunFolder = Path.Combine(folder, "runs"), RequestDelayMs = 0
            };
            if (failingStage is "Create organization account" or "Add BTO relationship") Row.BillToAddressAction = "CREATE RELATED BILL-TO ACCOUNT";
            if (failingStage == "Create contact") Row.ContactAction = "CREATE CONTACT";
            Transport = new FakeTransport(failingStage);
            Row.BillingVersion = 1;
            Row.BillingSourceAccount = "ACCOUNT";
            Row.BillingConfiguration = Options.Billing;
            Row.ValidCategoryIds = [28];
            Row.RequestedBilling = Row.RequestedBilling with { UseRequestedAddress = "BA" };
            Row.ExistingBillToAccount = TestAccount();
            Row.ExistingBillToContact = TestContact();
            if (failingStage is "Create organization account" or "Add BTO relationship") Row.ContactAction = "CREATE CONTACT";
            BillingState.Set(Row, new(
                Row.BillToAddressAction == "CREATE RELATED BILL-TO ACCOUNT" ? BillingRules.RequestedAccount(Row.RequestedBilling) : Row.ExistingBillToAccount,
                Row.ContactAction == "CREATE CONTACT" ? BillingRules.RequestedContact(Row.RequestedBilling) : Row.ExistingBillToContact,
                "Existing/new separate account", "Existing/new separate contact", false,
                Row.BillToAddressAction == "CREATE RELATED BILL-TO ACCOUNT", Row.ContactAction == "CREATE CONTACT"));
            client = new ApiClient(new Jwt
            {
                UngerboeckURI = Options.BaseUrl, APIUserID = "offline-test",
                Secret = Guid.NewGuid().ToString(), Key = Guid.NewGuid().ToString(), AutoRefresh = new AutoRefresh()
            });
            // This SDK has no public HTTP-handler injection point. Replace only the
            // test client's instance field; the production client is unchanged.
            var field = typeof(ApiClient).GetField("HttpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((HttpClient)field.GetValue(client)!).Dispose();
            field.SetValue(client, new HttpClient(Transport) { BaseAddress = new Uri(Options.BaseUrl) });
            Gateway = new MomentusGateway(Options, client, Delays.Add);
            Store = new FileJournalStore(Options.StateFolder);
            var evidence = new OrderEvidence { Identity = OrderIdentity.From(Options, Row), Plan = Row, SendEmail = Options.SendReadyEmail,
                ActivateOrder = Options.ActivateOrder, ActivateExhibitor = Options.ActivateExhibitor };
            Gateway.Journal = new ProcessingJournal(Store, evidence, Guid.NewGuid().ToString("N"));
        }

        public RunRow Apply(bool restart = false)
        {
            var row = restart ? Store.Load().Single().Plan : Row;
            var runner = new Runner(Options);
            runner.UseJournalStore(Store);
            runner.Apply(Gateway, new Candidate(Transport.Order, Transport.Exhibitor), row);
            return row;
        }
        public void Dispose()
        {
            client.Dispose();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    internal sealed class FakeTransport(string? failingStage) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string> Mutations { get; } = [];
        public int ReadFailures { get; set; }
        public int? ReadFailureStatus { get; set; }
        public int? WriteFailureStatus { get; set; }
        public Exception WriteFailure { get; set; } = new TimeoutException("Response timed out after the mutation was accepted.");
        public bool OmitCreatedIdentity { get; set; }
        public bool EmptyMutationResponse { get; set; }
        public bool FaultEnabled { get; set; } = true;
        public bool ApplyEffect { get; set; } = true;
        public string? FailReadPath { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage?>? SearchOverride { get; set; }
        public Func<AllAccountsModel, AllAccountsModel>? AccountReadOverride { get; set; }
        public ServiceOrdersModel Order { get; private set; } = new()
        {
            OrganizationCode = "10", OrderNumber = 3, Event = 1, Function = 4, Account = "ACCOUNT", BillToAccount = "ACCOUNT", BillToContact = "CONTACT", OrderStatus = "PC"
        };
        public ExhibitorsModel Exhibitor { get; private set; } = new() { OrganizationCode = "10", ExhibitorID = 2, Event = 1, ExhibitorStatus = 35, ExhibitorCategory = "" };
        private readonly List<DocumentsModel> copies = [];
        private readonly List<DocumentsModel> savedEmails = [];
        public readonly Dictionary<string, AllAccountsModel> Accounts = [];
        public BillingRequest BillingInstructions { get; set; } = TestRequest();
        private readonly Dictionary<string, RelationshipsModel> relationships = [];
        private readonly List<NotesModel> notes = failingStage == "Update payment schedule note"
            ? [new NotesModel { SequenceNumber = 12, Class = "SON", Type = "OH", PlainText = "Old terms" }] : [];
        private readonly DocumentsModel contract = new()
        {
            Type = "C", SequenceNumber = 11, DocumentID = "contract.pdf", Description = "Contract", Category = "CON"
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Split("/api/v1/")[1];
            Requests.Add($"{request.Method} {path}");
            if (request.Method == HttpMethod.Get)
            {
                if (FailReadPath == path) throw new InvalidOperationException("Offline reconciliation read unavailable.");
                if (ReadFailures-- > 0)
                {
                    if (ReadFailureStatus.HasValue) return Error(ReadFailureStatus.Value);
                    throw new TimeoutException("Transient read timeout.");
                }
                if (path == "ServiceOrders/10/3") return Json(Order);
                if (path == "Exhibitors/10/2") return Json(Exhibitor);
                if (path.StartsWith("Relationships/10/")) return Json(relationships[path]);
                if (path.StartsWith("Accounts/10/"))
                {
                    var code = path.Split('/').Last();
                    if (Accounts.TryGetValue(code, out var saved)) return Json(AccountReadOverride?.Invoke(saved) ?? saved);
                    var account = code == "CONTACT" ? TestContact() : TestAccount() with { AccountCode = code };
                    return Json(new AllAccountsModel
                    {
                        Organization = "10", AccountCode = account.AccountCode, Class = account.AccountClass, Name = account.Company,
                        FirstName = account.FirstName, LastName = account.LastName, Email = account.Email, PrimaryAccount = account.PrimaryAccount,
                        Address1 = account.Address, City = account.City, State = account.State, PostalCode = account.PostalCode, Country = account.Country,
                        EventSalesStatus = account.EventSalesStatus, AccountUserFieldSets = [TestFields(BillingInstructions)]
                    });
                }
                if (path == "Documents/10/C/11/Download") return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Convert.ToBase64String(Encoding.UTF8.GetBytes("Offline attachment")))
                };
                if (path.EndsWith("/Download")) return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(copies.Single(x => x.SequenceNumber == int.Parse(path.Split('/')[3])).NewDocumentData)
                };
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                if (SearchOverride?.Invoke(request) is { } overridden) return overridden;
                if (path == "Documents/10") return Search(query.Contains("Type eq 'M'") ? savedEmails : query.Contains("Exhibitor eq") ? [contract] : copies);
                if (path == "Notes/10") return Search(notes);
                if (path is "Exhibitors/10" or "ServiceOrders/10") return Search(Array.Empty<ExhibitorsModel>());
                if (path == "Accounts/10")
                {
                    var pool = Accounts.Values.Where(x => !string.IsNullOrWhiteSpace(x.AccountCode));
                    if (query.Contains("Class eq 'O'")) pool = pool.Where(x => x.Class == "O");
                    if (query.Contains("Class eq 'P'")) pool = pool.Where(x => x.Class == "P");
                    return Search(pool.ToArray());
                }
                if (path == "Relationships/10") return Search(Array.Empty<AllAccountsModel>());
                throw new InvalidOperationException($"Unexpected offline GET: {path}");
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            object result;
            string stage;
            if (path == "Accounts" && request.Method == HttpMethod.Post)
            {
                var model = JsonConvert.DeserializeObject<AllAccountsModel>(body)!;
                stage = model.Class == "O" ? "Create organization account" : "Create contact";
                model.AccountCode = OmitCreatedIdentity ? "" : model.Class == "O" ? "NEWACCOUNT" : "NEWCONTACT";
                if (ApplyEffect) Accounts[model.AccountCode] = model;
                result = model;
            }
            else if (path.StartsWith("Accounts/10/") && request.Method == HttpMethod.Put)
            {
                stage = "Update account address";
                result = JsonConvert.DeserializeObject<AllAccountsModel>(body)!;
                if (ApplyEffect) Accounts[((AllAccountsModel)result).AccountCode] = (AllAccountsModel)result;
            }
            else if (path == "Relationships")
            {
                var model = JsonConvert.DeserializeObject<RelationshipsModel>(body)!;
                stage = $"Add {model.RelationshipType} relationship";
                if (ApplyEffect) relationships[$"Relationships/10/{model.MasterAccountCode}/{model.SubordinateAccountCode}/{model.RelationshipType}"] = model;
                result = model;
            }
            else if (path == "ServiceOrders/10/3")
            {
                var model = JsonConvert.DeserializeObject<ServiceOrdersModel>(body)!;
                stage = model.OrderStatus == "A" ? "Activate order" : "Update service order";
                if (ApplyEffect && WriteFailureStatus is not (>= 400 and < 500)) Order = model;
                result = model;
            }
            else if (path.StartsWith("Documents"))
            {
                stage = "Copy contract document";
                var model = JsonConvert.DeserializeObject<DocumentsModel>(body)!;
                model.SequenceNumber = 22;
                if (ApplyEffect) copies.Add(model);
                result = model;
            }
            else if (path.StartsWith("Notes"))
            {
                stage = request.Method == HttpMethod.Post ? "Add payment schedule note" : "Update payment schedule note";
                var model = JsonConvert.DeserializeObject<NotesModel>(body)!;
                model.SequenceNumber = 12;
                model.PlainText = model.Text;
                if (ApplyEffect) { notes.Clear(); notes.Add(model); }
                result = model;
            }
            else if (path == "Exhibitors/10/2")
            {
                var model = JsonConvert.DeserializeObject<ExhibitorsModel>(body)!;
                stage = model.ExhibitorStatus == 2 ? "Activate exhibitor" : "Update exhibitor categories";
                if (ApplyEffect) Exhibitor = model;
                result = model;
            }
            else if (path == "Emails/Send")
            {
                stage = "Send ready email";
                result = JsonConvert.DeserializeObject<EmailsModel>(body)!;
                if (ApplyEffect) savedEmails.Add(new DocumentsModel { Type = "M", SequenceNumber = 33, Description = ((EmailsModel)result).EmailSubject, Order = 3 });
            }
            else throw new InvalidOperationException($"Unexpected offline mutation: {path}");

            if (FaultEnabled && stage == failingStage && WriteFailureStatus.HasValue) return Error(WriteFailureStatus.Value);
            Mutations.Add(stage); // The fake server accepts the mutation before losing its response.
            if (FaultEnabled && stage == failingStage && EmptyMutationResponse) return Json(null);
            if (FaultEnabled && stage == failingStage && !OmitCreatedIdentity) throw WriteFailure;
            return Json(result);
        }

        private static HttpResponseMessage Json(object? value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonConvert.SerializeObject(value), Encoding.UTF8, "application/json")
        };
        private static HttpResponseMessage Search(object rows)
        {
            var response = Json(rows);
            var count = ((System.Collections.IEnumerable)rows).Cast<object>().Count();
            response.Headers.Add("X-SearchMetadata", JsonConvert.SerializeObject(new
            {
                Page = 1, Page_Size = 1000, PageTotal = 1, ResultsTotal = count
            }));
            return response;
        }
        private static HttpResponseMessage Error(int status) => new((HttpStatusCode)status)
        {
            Content = new StringContent(JsonConvert.SerializeObject(new
            {
                Status = status,
                ErrorList = new[] { new { ErrorCode = "OfflineError", Message = "Offline response failure", Source = "Api" } }
            }), Encoding.UTF8, "application/json")
        };
    }
}
