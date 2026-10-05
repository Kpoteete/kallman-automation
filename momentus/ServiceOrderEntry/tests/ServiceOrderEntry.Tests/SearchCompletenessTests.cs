using System.Net;
using System.Text;
using Newtonsoft.Json;
using Ungerboeck.Api.Models.Options;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class SearchCompletenessTests
{
    private const string Email = "test@example.invalid";
    private static AllAccountsModel Account(string code, string name = "Acme") => new() { AccountCode = code, Name = name, Class = "O" };
    private static AllAccountsModel Contact(string code) => new()
    {
        AccountCode = code, Class = "P", PrimaryAccount = "ACCOUNT", Email = Email, FirstName = "Test", LastName = "Contact"
    };
    private static DocumentsModel Document(int id, string description = "Contract", string type = "C") => new()
    {
        SequenceNumber = id, Type = type, DocumentID = $"{id}.pdf", Category = "CON", Description = description, Order = 3, Exhibitor = 2
    };
    private static NotesModel Note(int id) => new() { SequenceNumber = id, Type = "OH", Class = "SON", OrderNumber = 3, Title = ManagedNoteRules.Title, PlainText = RetryBoundaryTests.TestSchedule };
    private static ActivitiesModel Activity(int id, string booth, DateTime entered) => new()
    {
        SequenceNumber = id, EnteredOn = entered, PlainText = $"Accepted booth {booth}, and had these comments: ok"
    };
    private static SearchResponse<AllAccountsModel> Page(int page, AllAccountsModel[] rows, string? next = null, int total = 2, int pages = 2) => new()
    {
        Results = rows, SearchMetadata = new() { Page = page, Page_Size = 1, PageTotal = pages, ResultsTotal = total, Links = new() { Next = next } }
    };
    private static IReadOnlyList<AllAccountsModel> Read(SearchResponse<AllAccountsModel> first,
        Func<string, Search, SearchResponse<AllAccountsModel>> next, int ceiling = 100) => CompleteSearch.Read(
            _ => first, next, new Search { PageSize = 1, MaxResults = ceiling, OrderBy = ["AccountCode"] },
            "offline accounts", new Uri("https://offline.invalid/prod/api/v1/Accounts/10"), x => x.AccountCode);

    // Real SDK serialization/navigation is exercised with a transport that never opens a network connection.
    private static void Pages(RetryBoundaryTests.Scenario scenario, string endpoint, object[] first, object[] second,
        bool failLater = false, bool loop = false)
    {
        scenario.Transport.SearchOverride = request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith($"/{endpoint}/10")) return null;
            var later = request.RequestUri.Query.Contains("page=2");
            if (later && failLater) return Error();
            return SearchResponse(later ? second : first, later ? 2 : 1, first.Length + second.Length,
                !later || loop ? $"https://offline.invalid/prod/api/v1/{endpoint}/10?page=2&search=offline" : null,
                first.Length);
        };
    }
    private static HttpResponseMessage SearchResponse(object rows, int page, int total, string? next, int size = 1)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonConvert.SerializeObject(rows), Encoding.UTF8, "application/json")
        };
        response.Headers.Add("X-SearchMetadata", JsonConvert.SerializeObject(new
        {
            Page = page, Page_Size = size, PageTotal = Math.Max(1, (int)Math.Ceiling((double)total / size)), ResultsTotal = total,
            Links = new { Next = next }
        }));
        return response;
    }
    private static HttpResponseMessage Error() => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent("{\"Status\":400,\"ErrorList\":[{\"ErrorCode\":\"OfflineFailure\",\"Message\":\"Required search unavailable\",\"Source\":\"Api\"}]}", Encoding.UTF8, "application/json")
    };

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void CompleteEmptyUniqueAndMultipleResultsRemainDistinct(int count)
    {
        var rows = Enumerable.Range(1, count).Select(x => Account(x.ToString())).ToArray();
        var page = Page(1, rows, total: count, pages: 1);
        page.SearchMetadata.Page_Size = 10;
        Assert.Equal(count, Read(page, (_, _) => throw new Exception("No next page expected")).Count);
    }

    [Fact]
    public void ExactSearchOptionsAreRetainedOnEveryPage()
    {
        var search = new Search { PageSize = 1, MaxResults = 20, OrderBy = ["AccountCode desc"], Select = ["AccountCode", "Name"] };
        var first = Page(1, [Account("A")], "?page=2&filter=original");
        var rows = CompleteSearch.Read(s => { Assert.Same(search, s); return first; },
            (url, s) => { Assert.Same(search, s); Assert.Contains("filter=original", url); return Page(2, [Account("B")]); },
            search, "original filter", new Uri("https://offline.invalid/prod/api/v1/Accounts/10"), x => x.AccountCode);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "AccountCode desc" }, search.OrderBy);
        Assert.Equal(new[] { "AccountCode", "Name" }, search.Select);
    }

    [Fact]
    public void IdenticalStableIdsAreDeduplicatedInFirstSeenOrder()
    {
        var rows = Read(Page(1, [Account("A")], "?page=2"), (_, _) => Page(2, [Account("A")]));
        Assert.Equal("A", Assert.Single(rows).AccountCode);
    }

    [Fact]
    public void ConflictingStableIdsBlockTheDecision()
    {
        var error = Assert.Throws<DecisionSearchException>(() => Read(Page(1, [Account("A")], "?page=2"),
            (_, _) => Page(2, [Account("A", "Different")] )));
        Assert.Equal(SearchFailureKind.ConflictingDuplicateId, error.Kind);
    }

    [Fact]
    public void FirstPageFailureIsDistinctFromCompleteEmpty()
    {
        var error = Assert.Throws<DecisionSearchException>(() => CompleteSearch.Read<AllAccountsModel>(
            _ => throw new IOException("offline"), (_, _) => throw new Exception(), new Search(), "Accounts",
            new Uri("https://offline.invalid/prod/api/v1/Accounts/10"), x => x.AccountCode));
        Assert.Equal(SearchFailureKind.RequestFailed, error.Kind);
        Assert.Equal(1, error.Page);
    }

    [Fact]
    public void LaterPageFailureNeverReturnsPartialResults()
    {
        var error = Assert.Throws<DecisionSearchException>(() => Read(Page(1, [Account("A")], "?page=2"),
            (_, _) => throw new IOException("offline")));
        Assert.Equal(SearchFailureKind.IncompletePagination, error.Kind);
        Assert.Equal(2, error.Page);
    }

    [Fact]
    public void RepeatedContinuationFailsWithFiniteRequests()
    {
        var calls = 0;
        var error = Assert.Throws<DecisionSearchException>(() => Read(Page(1, [Account("A")], "?page=2", 3, 3),
            (_, _) => { calls++; return Page(2, [Account("B")], "?page=2", 3, 3); }));
        Assert.Equal(SearchFailureKind.PaginationLoop, error.Kind);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(" ")] [InlineData("https://other.invalid/prod/api/v1/Accounts/10?page=2")]
    [InlineData("/prod/api/v1/Documents/10?page=2")] [InlineData("?page=2#fragment")]
    [InlineData("not-a-search-link")] [InlineData("https://[invalid")]
    public void MalformedContinuationsFailClosed(string link)
    {
        Assert.Equal(SearchFailureKind.MalformedResponse, Assert.Throws<DecisionSearchException>(() =>
            Read(Page(1, [Account("A")], link), (_, _) => throw new Exception("Must not navigate"))).Kind);
    }

    [Fact]
    public void MissingLaterEvidenceIsIncompleteEvenWithoutNextLink()
    {
        Assert.Equal(SearchFailureKind.IncompletePagination, Assert.Throws<DecisionSearchException>(() =>
            Read(Page(1, [Account("A")]), (_, _) => throw new Exception())).Kind);
    }

    [Theory]
    [InlineData("metadata")] [InlineData("results")] [InlineData("identity")]
    [InlineData("page")] [InlineData("size")] [InlineData("pages")] [InlineData("negative-total")]
    public void MalformedResponsesCannotEstablishAbsence(string defect)
    {
        var page = Page(1, [Account("A")], "?page=2");
        switch (defect)
        {
            case "metadata": page.SearchMetadata = null; break;
            case "results": page.Results = null; break;
            case "identity": page.Results = [Account("")]; break;
            case "page": page.SearchMetadata.Page = 2; break;
            case "size": page.SearchMetadata.Page_Size = 0; break;
            case "pages": page.SearchMetadata.PageTotal = 3; break;
            case "negative-total": page.SearchMetadata.ResultsTotal = -1; break;
        }
        Assert.Equal(SearchFailureKind.MalformedResponse, Assert.Throws<DecisionSearchException>(() => Read(page, (_, _) => throw new Exception())).Kind);
    }

    [Fact]
    public void CeilingOnFirstPageBlocksBeforeNavigating()
    {
        Assert.Equal(SearchFailureKind.ResultCeiling, Assert.Throws<DecisionSearchException>(() =>
            Read(Page(1, [Account("A")], "?page=2"), (_, _) => throw new Exception("Must not navigate"), 1)).Kind);
    }

    [Fact]
    public void ChangedSearchTotalsBlockDecision()
    {
        Assert.Equal(SearchFailureKind.IncompletePagination, Assert.Throws<DecisionSearchException>(() =>
            Read(Page(1, [Account("A")], "?page=2"), (_, _) => Page(2, [Account("B")], total: 3, pages: 3))).Kind);
    }

    [Theory]
    [InlineData("Acme Corporation", "Acme")] [InlineData("Acme, Inc.", "Acme LLC")]
    public void OrganizationMatchOnPageTwoCoversAcceptedNameVariations(string requested, string existing)
    {
        using var s = new RetryBoundaryTests.Scenario();
        Pages(s, "Accounts", [Account("OTHER", "Other company")], [Account("MATCH", existing)]);
        var match = Assert.Single(s.Gateway.FindOrganizationCandidates(s.Row.RequestedBilling with { CompanyName = requested }));
        Assert.Equal("MATCH", match.AccountCode);
        Assert.Equal(2, s.Gateway.RequestCount);
    }

    [Fact]
    public void OrganizationConflictOnPageTwoBlocksCreateAndAllLaterStages()
    {
        using var s = new RetryBoundaryTests.Scenario("Create organization account");
        Pages(s, "Accounts", [Account("ONE", "Offline Test Company")], [Account("TWO", "Offline Test Company, Inc.")]);
        s.Apply();
        Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus);
        Assert.Contains("multiple duplicate candidates", s.Row.UpdateMessage);
        Assert.Empty(s.Transport.Mutations);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RequiredOrganizationSearchFailureBlocksCreation(bool later)
    {
        using var s = new RetryBoundaryTests.Scenario("Create organization account");
        if (later) Pages(s, "Accounts", [Account("OTHER", "Other")], [Account("MATCH")], failLater: true);
        else s.Transport.SearchOverride = r => r.RequestUri!.AbsolutePath.EndsWith("/Accounts/10") ? Error() : null;
        s.Apply();
        Assert.Equal("FAILED", s.Row.ServiceOrderUpdateStatus);
        Assert.Contains(later ? "IncompletePagination" : "RequestFailed", s.Row.UpdateMessage);
        Assert.Empty(s.Transport.Mutations);
        Assert.Single(s.Store.Load().Single().SearchFailures);
    }

    [Theory]
    [InlineData("!!!")] [InlineData("LLC")]
    public void EmptyNormalizedOrganizationIdentityRequiresReview(string name)
    {
        using var s = new RetryBoundaryTests.Scenario();
        Assert.Throws<DecisionSearchException>(() => s.Gateway.FindOrganizationCandidates(s.Row.RequestedBilling with { CompanyName = name }));
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void ContactMatchOnPageTwoIsReused()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var unrelated = Contact("OTHER"); unrelated.Email = "different@example.invalid";
        Pages(s, "Accounts", [unrelated], [Contact("MATCH")]);
        Runner.DecideContact(s.Gateway, s.Row);
        Assert.Equal("MATCH", s.Row.FinalBillToContact);
        Assert.Equal("USE EXISTING CONTACT", s.Row.ContactAction);
    }

    [Fact]
    public void ContactLaterPageAmbiguityBlocksCreation()
    {
        using var s = new RetryBoundaryTests.Scenario("Create contact");
        Pages(s, "Accounts", [Contact("ONE")], [Contact("TWO")]);
        Runner.DecideContact(s.Gateway, s.Row);
        Assert.Equal("REVIEW", s.Row.ContactAction);
        s.Row.ContactAction = "CREATE CONTACT"; // Recheck also protects an older saved plan.
        s.Apply();
        Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void ContactLaterPageFailureBlocksCreation()
    {
        using var s = new RetryBoundaryTests.Scenario("Create contact");
        Pages(s, "Accounts", [Contact("ONE")], [Contact("TWO")], failLater: true);
        s.Apply();
        Assert.Equal("FAILED", s.Row.ServiceOrderUpdateStatus);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void ItemConflictOnPageTwoParticipatesInCategoryDecision()
    {
        using var s = new RetryBoundaryTests.Scenario();
        Pages(s, "ServiceOrderItems", [new ServiceOrderItemsModel { OrderLineNumber = 1, Description = "Turnkey Package" }],
            [new ServiceOrderItemsModel { OrderLineNumber = 2, Description = "Space Only Package" }]);
        var decision = CategoryRules.Resolve(s.Gateway.GetOrderItems(3),
            [new("Turnkey", 28, ["Turnkey"]), new("Space Only", 27, ["Space Only"])]);
        Assert.Null(decision.Sequence);
        Assert.Contains("Multiple", decision.Message);
        s.Row.ProposedCategory = decision.Sequence;
        Assert.Equal("REVIEW", ValidationRules.Validate(s.Row).Status);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void ItemLaterFailureStopsEvaluationBeforeMutation()
    {
        using var s = new RetryBoundaryTests.Scenario();
        Pages(s, "ServiceOrderItems", [new ServiceOrderItemsModel { OrderLineNumber = 1 }], [new ServiceOrderItemsModel { OrderLineNumber = 2 }], failLater: true);
        Assert.Throws<DecisionSearchException>(() => Runner.Evaluate(s.Gateway, new(s.Transport.Order, s.Transport.Exhibitor), [], []));
        Assert.Empty(s.Transport.Mutations);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LaterContractDocumentAffectsSelectionAndDeduplication(bool exhibitor)
    {
        using var s = new RetryBoundaryTests.Scenario();
        Pages(s, "Documents", [Document(10, "Other contract")], [Document(11)]);
        var docs = exhibitor ? s.Gateway.GetExhibitorContractPdfs(2) : s.Gateway.GetOrderContractPdfs(3);
        Assert.Equal(2, docs.Count);
        Assert.False(Runner.HasMatchingDocument(docs, new("C", 11, "11.pdf", "Contract", "CON"))); // Description/metadata without content evidence cannot establish equality.
    }

    [Fact]
    public void NearbyPdfOnLaterPageIsConsideredBeforeOrdering()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var day = new DateTime(2026, 10, 4);
        Pages(s, "Documents", [new { Type = "C", SequenceNumber = 10, DocumentID = "10.pdf", EnteredOn = day.AddDays(-10), Exhibitor = 2 }],
            [new { Type = "C", SequenceNumber = 11, DocumentID = "11.pdf", EnteredOn = day, Exhibitor = 2 }]);
        Assert.Equal(11, Assert.Single(s.Gateway.GetNearbyExhibitorPdfs(2, day)).SequenceNumber);
    }

    [Fact]
    public void NotesOnLaterPageAreRetrievedAndConflictBlocksNoteMutation()
    {
        using var s = new RetryBoundaryTests.Scenario();
        Pages(s, "Notes", [Note(1)], [Note(2)]);
        Assert.Equal(2, s.Gateway.GetOrderSonNotes(3).Count);
        Assert.Throws<RecoveryReviewException>(() => s.Gateway.SavePaymentScheduleNote(3, "new terms"));
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void NewerBoothActivityOnPageTwoWins()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var day = new DateTime(2026, 10, 4);
        Pages(s, "Activities", [Activity(1, "OLD", day)], [Activity(2, "NEW", day.AddHours(1))]);
        Assert.Equal("NEW", s.Gateway.GetBoothNumber(2, 1));
    }

    [Fact]
    public void SameTimeBoothConflictOnPageTwoIsNotIgnored()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var day = new DateTime(2026, 10, 4);
        Pages(s, "Activities", [Activity(1, "ONE", day)], [Activity(2, "TWO", day)]);
        Assert.Equal("", s.Gateway.GetBoothNumber(2, 1));
    }

    [Fact]
    public void SavedEmailOnPageTwoPreventsResend()
    {
        using var s = new RetryBoundaryTests.Scenario();
        Pages(s, "Documents", [Document(1, "Other email", "M")], [Document(2, "Ready for invoicing - Service Order 3", "M")]);
        Assert.Throws<RecoveryReviewException>(() => s.Gateway.ReadyEmailWasSent(3));
        Assert.Throws<RecoveryReviewException>(() => s.Gateway.SendReadyForInvoicingEmail(s.Row, []));
        Assert.Empty(s.Transport.Mutations);
    }

    [Theory]
    [InlineData("notes")] [InlineData("email")] [InlineData("copy")] [InlineData("relationship")]
    public void LaterPageFailureBlocksTheRelatedMutation(string stage)
    {
        using var s = new RetryBoundaryTests.Scenario();
        switch (stage)
        {
            case "notes":
                Pages(s, "Notes", [Note(1)], [Note(2)], failLater: true);
                Assert.Throws<DecisionSearchException>(() => s.Gateway.SavePaymentScheduleNote(3, "new terms")); break;
            case "email":
                Pages(s, "Documents", [Document(1, "Other email", "M")], [Document(2, type: "M")], failLater: true);
                Assert.Throws<DecisionSearchException>(() => s.Gateway.SendReadyForInvoicingEmail(s.Row, [])); break;
            case "copy":
                Pages(s, "Documents", [Document(10)], [Document(12)], failLater: true);
                Assert.Throws<DecisionSearchException>(() => s.Gateway.CopyContractPdfToOrder(new("C", 11, "contract.pdf", "Contract", "CON"), s.Transport.Order)); break;
            case "relationship":
                Pages(s, "Relationships", [Relationship()], [Relationship()], failLater: true);
                Assert.Throws<DecisionSearchException>(() => s.Gateway.EnsureRelationship("ACCOUNT", "OTHER", "BTO")); break;
        }
        Assert.Empty(s.Transport.Mutations);
        Assert.Single(s.Store.Load().Single().SearchFailures);
    }

    private static RelationshipsModel Relationship() => new()
    {
        MasterOrganizationCode = "10", MasterAccountCode = "ACCOUNT", SubordinateOrganizationCode = "10", SubordinateAccountCode = "OTHER", RelationshipType = "BTO"
    };

    [Fact]
    public void ExactRelationshipCheckUsesCompositeKeysAndReusesExisting()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.SearchOverride = r => r.RequestUri!.AbsolutePath.EndsWith("/Relationships/10") ? SearchResponse(new[] { Relationship() }, 1, 1, null) : null;
        s.Gateway.EnsureRelationship("ACCOUNT", "OTHER", "BTO");
        Assert.Empty(s.Transport.Mutations);
        Assert.Equal(1, s.Gateway.RequestCount);
    }

    [Fact]
    public void EligibilityTraversesBothExhibitorAndOrderPagesAndDeduplicates()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.SearchOverride = r =>
        {
            var later = r.RequestUri!.Query.Contains("page=2");
            var endpoint = r.RequestUri.AbsolutePath.EndsWith("/Exhibitors/10") ? "Exhibitors" : "ServiceOrders";
            return SearchResponse(endpoint == "Exhibitors" ? new object[] { s.Transport.Exhibitor } : new object[] { s.Transport.Order },
                later ? 2 : 1, 2, later ? null : $"https://offline.invalid/prod/api/v1/{endpoint}/10?page=2");
        };
        Assert.Single(s.Gateway.FindCandidates(new HashSet<int> { 1 }));
        Assert.Equal(4, s.Gateway.RequestCount);
    }

    [Fact]
    public void PaginationLoopCannotAuthorizeAccountCreation()
    {
        using var s = new RetryBoundaryTests.Scenario("Create organization account");
        Pages(s, "Accounts", [Account("OTHER", "Other")], [Account("MATCH", "Offline Test Company")], loop: true);
        s.Apply();
        Assert.Equal("FAILED", s.Row.ServiceOrderUpdateStatus);
        Assert.Contains("PaginationLoop", s.Row.UpdateMessage);
        Assert.Empty(s.Transport.Mutations);
        Assert.Equal(6, s.Gateway.RequestCount); // Exact eligibility/billing/identity reads plus two search pages.
    }

    [Fact]
    public void ConflictingDuplicateIdCannotAuthorizeAccountCreation()
    {
        using var s = new RetryBoundaryTests.Scenario("Create organization account");
        Pages(s, "Accounts", [Account("SAME", "Other")], [Account("SAME", "Offline Test Company")]);
        s.Apply();
        Assert.Equal("FAILED", s.Row.ServiceOrderUpdateStatus);
        Assert.Contains("ConflictingDuplicateId", s.Row.UpdateMessage);
        Assert.Empty(s.Transport.Mutations);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SavedEmailRecoveryUsesPageTwoEvidenceAndRetainsUncertaintyOnFailure(bool fail)
    {
        using var s = new RetryBoundaryTests.Scenario();
        var intent = new EmailsModel { EmailSubject = "Ready for invoicing - Service Order 3", HtmlText = "offline body", SaveAsUngerboeckDocument = new() { Order = 3 } };
        var stage = s.Gateway.Journal!.Prepare("Send ready email", "offline accepted email", JsonConvert.SerializeObject(new { intent.Organization, intent.EmailSubject, intent.HtmlText, intent.SendToAddresses, intent.SaveAsUngerboeckDocument }),
            new() { ["order"] = "3", ["baseline"] = "", ["bodyHash"] = OrderIdentity.Hash(intent.HtmlText), ["attachmentHashes"] = "" });
        s.Gateway.Journal.Dispatching(stage);
        s.Gateway.Journal.Result(stage, JsonConvert.SerializeObject(intent));
        Pages(s, "Documents", [Document(1, "Other email", "M")], [Document(2, intent.EmailSubject, "M")], failLater: fail);
        if (fail)
        {
            Assert.Throws<RecoveryReviewException>(() => s.Gateway.ReconcileIncomplete());
            Assert.Equal(StageStatus.Unknown, s.Store.Load().Single().Stages.Single().Status);
            Assert.Single(s.Store.Load().Single().SearchFailures);
        }
        else
        {
            s.Gateway.ReconcileIncomplete();
            var recovered = s.Store.Load().Single().Stages.Single();
            Assert.Equal(StageStatus.Verified, recovered.Status);
            Assert.Equal("2", recovered.Source["savedEmailSequence"]);
            Assert.True(recovered.Reconciled);
        }
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void LaterPageNoteMatchIsAvailableToReadback()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var first = Note(1); first.PlainText = "Other instructions";
        Pages(s, "Notes", [first], [Note(2)]);
        Assert.Contains(s.Gateway.GetOrderSonNotes(3), n => n.SequenceNumber == 2 && ManagedNoteRules.ContentHash(n.PlainText) == ManagedNoteRules.ContentHash(s.Row.PaymentScheduleText));
    }
}
