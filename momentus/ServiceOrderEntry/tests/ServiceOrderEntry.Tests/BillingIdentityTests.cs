using System.Net;
using System.Text;
using ClosedXML.Excel;
using Newtonsoft.Json;
using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class BillingIdentityTests
{
    private static HttpResponseMessage Results(IEnumerable<AllAccountsModel> rows)
    {
        var array = rows.ToArray();
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(array), Encoding.UTF8, "application/json") };
        response.Headers.Add("X-SearchMetadata", JsonConvert.SerializeObject(new { Page = 1, Page_Size = 1000, PageTotal = 1, ResultsTotal = array.Length, Links = new { Next = (string?)null } }));
        return response;
    }

    private static AllAccountsModel Organization(string code, string company = "Offline Test Company", string street = "1 Test Street") => new()
    { Organization = "10", AccountCode = code, Class = "O", Name = company, Address1 = street, City = "Test City", PostalCode = "12345", Country = "***", EventSalesStatus = "A" };
    private static AllAccountsModel Contact(string code, string parent = "ACCOUNT", string email = "test@example.invalid") => new()
    { Organization = "10", AccountCode = code, Class = "P", PrimaryAccount = parent, FirstName = "Test", LastName = "Contact", Email = email };
    private static void Search(RetryBoundaryTests.Scenario s, params AllAccountsModel[] accounts)
    {
        s.Transport.SearchOverride = request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/Accounts/10")) return null;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            return Results(accounts.Where(x => query.Contains($"Class eq '{x.Class}'") &&
                (!query.Contains("PrimaryAccount eq") || query.Contains($"PrimaryAccount eq '{x.PrimaryAccount}'"))));
        };
    }
    private static void Decide(RetryBoundaryTests.Scenario s)
    {
        Runner.DecideAccountAndAddress(s.Gateway, s.Row);
        Runner.DecideContact(s.Gateway, s.Row);
        (s.Row.ValidationStatus, s.Row.ValidationMessage) = ValidationRules.Validate(s.Row);
    }
    private static void SetRequest(RetryBoundaryTests.Scenario s, BillingRequest request)
    {
        s.Row.RequestedBilling = MomentusGateway.BillingFrom(new AllAccountsModel { AccountUserFieldSets = [RetryBoundaryTests.TestFields(request)] }, s.Options.Billing);
        s.Transport.BillingInstructions = request;
    }

    [Theory]
    [InlineData("Use Above Address")] [InlineData("ECA")] [InlineData("")] [InlineData("No")]
    public void AboveAddressRetainsIdentityAndPerformsNoBillingSearchOrMutation(string selector)
    {
        using var s = new RetryBoundaryTests.Scenario();
        SetRequest(s, RunRow.EmptyBilling with { UseRequestedAddress = selector });
        Decide(s);
        Assert.Equal("READY", s.Row.ValidationStatus);
        Assert.Equal("ACCOUNT", s.Row.FinalBillToAccount);
        Assert.Equal("CONTACT", s.Row.FinalBillToContact);
        Assert.Equal(0, s.Gateway.RequestCount);
        s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.DoesNotContain(s.Transport.Mutations, x => x.Contains("account", StringComparison.OrdinalIgnoreCase) || x.Contains("contact", StringComparison.OrdinalIgnoreCase) || x.Contains("relationship"));
        Assert.Equal("ACCOUNT", s.Transport.Order.BillToAccount);
        Assert.Equal("CONTACT", s.Transport.Order.BillToContact);
    }

    [Fact]
    public void PopulatedSeparateFieldsCannotOverrideAboveAddress()
    {
        using var s = new RetryBoundaryTests.Scenario();
        SetRequest(s, s.Row.RequestedBilling with { UseRequestedAddress = "Use Above Address", CompanyName = "Different Co.", Email = "malformed", Address = "Other address" });
        Decide(s);
        Assert.Equal("READY", s.Row.ValidationStatus);
        Assert.Equal("Offline Test Company", s.Row.EffectiveBilling!.Account.Company);
        Assert.Equal("test@example.invalid", s.Row.EffectiveBilling.Contact.Email);
        Assert.Equal("Different Co.", s.Row.RequestedBilling.CompanyName);
        s.Apply();
        Assert.Equal("COMPLETED", s.Row.ServiceOrderUpdateStatus);
        Assert.DoesNotContain("Create organization account", s.Transport.Mutations);
        Assert.DoesNotContain("Create contact", s.Transport.Mutations);
        var html = ReadyEmailBuilder.Build(s.Row, []);
        Assert.DoesNotContain("Different Co.", html);
        Assert.DoesNotContain("malformed", html);
    }

    [Fact]
    public void ConflictingAboveOrderAccountRequiresReviewWithoutReassignment()
    {
        using var s = new RetryBoundaryTests.Scenario();
        SetRequest(s, RunRow.EmptyBilling with { UseRequestedAddress = "ECA" });
        s.Row.ExistingBillToAccount = s.Row.ExistingBillToAccount with { AccountCode = "OTHER" };
        Decide(s);
        Assert.Equal("REVIEW", s.Row.ValidationStatus);
        Assert.Contains("conflicts", s.Row.AccountDecisionMessage);
        s.Apply();
        Assert.Empty(s.Transport.Mutations);
    }

    [Theory]
    [InlineData("above")] [InlineData("values")] [InlineData("separate")]
    public void ChangedBillingInstructionsStopStalePlanBeforeAnyMutation(string change)
    {
        using var s = new RetryBoundaryTests.Scenario("Create organization account");
        if (change == "separate")
        {
            SetRequest(s, RunRow.EmptyBilling with { UseRequestedAddress = "ECA" });
            Decide(s);
            s.Transport.BillingInstructions = RetryBoundaryTests.TestRequest();
        }
        else s.Transport.BillingInstructions = s.Row.RequestedBilling with
        { UseRequestedAddress = change == "above" ? "ECA" : "BA", Address = change == "values" ? "Changed street" : s.Row.RequestedBilling.Address };
        s.Apply();
        Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus);
        Assert.Contains("changed", s.Row.UpdateMessage);
        Assert.Empty(s.Transport.Mutations);
    }

    [Theory]
    [InlineData("Acme Corporation")] [InlineData("  Acme, CORPORATION  ")] [InlineData("東京株式会社")]
    public void NewAccountPreservesExactContractNameAndVerifiesNotApplicableStatus(string name)
    {
        using var s = new RetryBoundaryTests.Scenario("Create organization account");
        s.Transport.FaultEnabled = false;
        SetRequest(s, s.Row.RequestedBilling with { CompanyName = name });
        Decide(s);
        Assert.Equal("PREPARED", s.Row.ValidationStatus);
        s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.Equal(name, s.Transport.Accounts["NEWACCOUNT"].Name);
        Assert.Equal("N", s.Transport.Accounts["NEWACCOUNT"].EventSalesStatus);
        Assert.Equal(name, s.Row.EffectiveBilling!.Account.Company);
        Assert.Equal("NEWACCOUNT", s.Transport.Order.BillToAccount);
        Assert.Equal("NEWACCOUNT", s.Transport.Accounts["NEWCONTACT"].PrimaryAccount);
        Assert.All(s.Store.Load().Single().Stages, x => Assert.Equal(StageStatus.Verified, x.Status));
        Assert.All(s.Store.Load().Single().Stages.Where(x => x.Operation == "Add relationship"),
            x => Assert.Null(JsonConvert.DeserializeObject<RelationshipsModel>(x.Result)!.EventSalesDesignation));
        var second = s.Apply(restart: true);
        Assert.Equal("COMPLETED", second.ServiceOrderUpdateStatus);
        Assert.Single(s.Transport.Mutations, x => x == "Create organization account");
        Assert.Single(s.Transport.Mutations, x => x == "Create contact");
    }

    [Fact]
    public void CreatedAccountWrongStatusFailsReadbackAndStopsFollowingMutations()
    {
        using var s = new RetryBoundaryTests.Scenario("Create organization account");
        s.Transport.FaultEnabled = false;
        s.Transport.AccountReadOverride = model => { if (model.AccountCode == "NEWACCOUNT") model.EventSalesStatus = "A"; return model; };
        s.Apply();
        Assert.Equal("UNKNOWN WRITE OUTCOME", s.Row.ServiceOrderUpdateStatus);
        Assert.Equal(new[] { "Create organization account" }, s.Transport.Mutations);
    }

    [Theory]
    [InlineData("東京", "大阪")] [InlineData("LLC", "Inc.")] [InlineData("!!!", "???")] [InlineData("AB", "A B")]
    public void UnsafeNamesNeverCompareEqual(string a, string b) => Assert.False(TextRules.CompanyMatches(a, b));

    [Fact]
    public void UnicodeNamesRemainNonemptyAndDistinct()
    {
        Assert.NotEmpty(TextRules.NormalizeCompany("東京"));
        Assert.NotEmpty(TextRules.NormalizeCompany("大阪"));
        Assert.NotEqual(TextRules.NormalizeCompany("東京"), TextRules.NormalizeCompany("大阪"));
        Assert.True(TextRules.CompanyMatches("Acme Corporation", "Acme Corp"));
        Assert.True(TextRules.CompanyMatches("Café", "Cafe\u0301"));
        Assert.NotEmpty(TextRules.NormalizeCompany("𐐀"));
    }

    [Theory]
    [InlineData("different street", (int)AccountMatchKind.Candidate)] [InlineData("1 Test Street", (int)AccountMatchKind.Confirmed)]
    public void SuffixCandidatesRequireAddressEvidenceAndExistingNamesAndStatusRemainUnchanged(string street, int expectedValue)
    {
        var expected = (AccountMatchKind)expectedValue;
        using var s = new RetryBoundaryTests.Scenario();
        var account = Organization("REUSED", "Acme Corporation", street);
        s.Transport.Accounts["REUSED"] = account;
        var contact = Contact("REUSEDCONTACT", "REUSED");
        s.Transport.Accounts["REUSEDCONTACT"] = contact;
        SetRequest(s, s.Row.RequestedBilling with { CompanyName = "Acme Corp" });
        Search(s, account, contact);
        Decide(s);
        Assert.Equal(expected, s.Row.AccountMatchKind);
        if (expected == AccountMatchKind.Candidate)
        {
            Assert.Equal("REVIEW", s.Row.ValidationStatus);
            s.Apply();
            Assert.Empty(s.Transport.Mutations);
        }
        else
        {
            Assert.Equal("READY", s.Row.ValidationStatus);
            s.Apply();
            Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
            Assert.Equal("Acme Corporation", s.Row.EffectiveBilling!.Account.Company);
            Assert.Equal("A", account.EventSalesStatus);
            Assert.DoesNotContain("Create organization account", s.Transport.Mutations);
            Assert.DoesNotContain("Update account address", s.Transport.Mutations);
        }
        Assert.Equal(street, account.Address1);
    }

    [Fact]
    public void AmbiguousAccountsRequireReviewAndNeverCreateToEscape()
    {
        using var s = new RetryBoundaryTests.Scenario();
        Search(s, Organization("ONE"), Organization("TWO"));
        Decide(s);
        Assert.Equal(AccountMatchKind.Ambiguous, s.Row.AccountMatchKind);
        Assert.Equal("REVIEW", s.Row.ValidationStatus);
        s.Apply();
        Assert.Empty(s.Transport.Mutations);
    }

    [Theory]
    [InlineData("test@example.invalid")] [InlineData(" Test@Example.Invalid ")]
    public void ExistingContactEmailWinsRegardlessOfSubmittedNameCaseOrWhitespace(string email)
    {
        using var s = new RetryBoundaryTests.Scenario();
        SetRequest(s, s.Row.RequestedBilling with { Email = email, FirstName = "Different", LastName = "Spelling" });
        var contact = Contact("REUSED"); s.Transport.Accounts["REUSED"] = contact;
        Search(s, Organization("ACCOUNT"), contact);
        Decide(s);
        Assert.Equal("READY", s.Row.ValidationStatus);
        Assert.Equal("REUSED", s.Row.FinalBillToContact);
        Assert.Equal("Test", s.Row.EffectiveBilling!.Contact.FirstName);
        s.Apply();
        Assert.Equal("COMPLETED", s.Row.ServiceOrderUpdateStatus);
        Assert.DoesNotContain("Create contact", s.Transport.Mutations);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DuplicateContactsAreAmbiguousUnlessKnownStableContactIdResolvesThem(bool known)
    {
        using var s = new RetryBoundaryTests.Scenario();
        Search(s, Organization("ACCOUNT"), Contact(known ? "CONTACT" : "ONE"), Contact("TWO"));
        Decide(s);
        Assert.Equal(known ? "READY" : "REVIEW", s.Row.ValidationStatus);
        Assert.NotEqual("CREATE CONTACT", s.Row.ContactAction);
        if (known) Assert.Equal("CONTACT", s.Row.FinalBillToContact);
        else { s.Apply(); Assert.Empty(s.Transport.Mutations); }
    }

    [Fact]
    public void CrossAccountEmailRequiresReviewWithoutRelationshipOrCreation()
    {
        using var s = new RetryBoundaryTests.Scenario();
        Search(s, Organization("ACCOUNT"), Contact("CROSS", "ANOTHER"));
        Decide(s);
        Assert.Equal("REVIEW", s.Row.ValidationStatus);
        Assert.Contains("another account", s.Row.ContactDecisionMessage);
        s.Apply();
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void NoExistingContactCreatesOneWithVerifiedParentAndEmail()
    {
        using var s = new RetryBoundaryTests.Scenario("Create contact");
        s.Transport.FaultEnabled = false;
        Search(s, Organization("ACCOUNT"));
        Decide(s);
        Assert.Equal("PREPARED", s.Row.ValidationStatus);
        s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.Equal("ACCOUNT", s.Transport.Accounts["NEWCONTACT"].PrimaryAccount);
        Assert.Equal("test@example.invalid", s.Transport.Accounts["NEWCONTACT"].Email);
        Assert.Equal("READY", s.Row.ValidationStatus);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BillingUdfSelectionUsesConfiguredIdentityInAnyCollectionOrder(bool reverse)
    {
        var intended = RetryBoundaryTests.TestFields(RetryBoundaryTests.TestRequest() with { CompanyName = "  Acme Corporation  " });
        var unrelated = new UserFields { Header = "OrgAccountUDF", Class = "X", Type = "XX", UserText03 = "Wrong", UserText11 = "ECA" };
        var model = new AllAccountsModel { AccountUserFieldSets = reverse ? [intended, unrelated] : [unrelated, intended] };
        Assert.Equal("  Acme Corporation  ", MomentusGateway.BillingFrom(model, RetryBoundaryTests.TestBillingConfiguration()).CompanyName);
    }

    [Theory]
    [InlineData(0)] [InlineData(2)]
    public void MissingOrAmbiguousBillingSetFailsBeforeMutation(int count)
    {
        using var s = new RetryBoundaryTests.Scenario();
        var source = Organization("ACCOUNT");
        source.AccountUserFieldSets = Enumerable.Range(0, count).Select(_ => RetryBoundaryTests.TestFields(s.Row.RequestedBilling)).ToList();
        s.Transport.Accounts["ACCOUNT"] = source;
        s.Apply();
        Assert.Equal("FAILED", s.Row.ServiceOrderUpdateStatus);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void MissingOrInvalidConfigurationFailsClosed()
    {
        Assert.Throws<InvalidDataException>(() => new BillingConfiguration().Validate());
        Assert.Throws<InvalidDataException>(() => (RetryBoundaryTests.TestBillingConfiguration() with { SeparateSelectors = [""] }).Validate());
        Assert.Throws<InvalidDataException>(() => (RetryBoundaryTests.TestBillingConfiguration() with { AboveSelectors = ["CUSTOM ABOVE"], SeparateSelectors = ["ECA"] }).Validate());
        Assert.Throws<InvalidDataException>(() => (RetryBoundaryTests.TestBillingConfiguration() with { EventSalesNotApplicableCode = "NA" }).Validate());
        using var s = new RetryBoundaryTests.Scenario();
        s.Row.BillingConfiguration = s.Row.BillingConfiguration! with { Type = "XX" };
        s.Apply();
        Assert.Empty(s.Transport.Mutations);
    }

    [Theory]
    [InlineData("bad")] [InlineData("a@@example.com")] [InlineData("Jane <jane@example.com>")] [InlineData("a@localhost")]
    public void InvalidSeparateEmailBlocksReadyAndAllMutations(string email)
    {
        using var s = new RetryBoundaryTests.Scenario();
        SetRequest(s, s.Row.RequestedBilling with { Email = email });
        Decide(s);
        Assert.Equal("REVIEW", s.Row.ValidationStatus);
        s.Apply();
        Assert.Empty(s.Transport.Mutations);
    }

    [Theory]
    [InlineData("unknown")] [InlineData(" ", false)]
    public void UnknownSelectorCannotInferSeparateBilling(string selector, bool unknown = true)
    {
        using var s = new RetryBoundaryTests.Scenario();
        SetRequest(s, s.Row.RequestedBilling with { UseRequestedAddress = selector });
        Decide(s);
        Assert.Equal(unknown ? "REVIEW" : "READY", s.Row.ValidationStatus);
        Assert.Equal(0, s.Gateway.RequestCount);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(999)]
    public void InvalidCategoryIdentifierCannotAuthorizeReady(int id)
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Row.ProposedCategory = id;
        Assert.Equal("REVIEW", ValidationRules.Validate(s.Row).Status);
        s.Apply();
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void InvalidCategoryWorkbookFailsAtStartup()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("Categories");
                sheet.Cell(1, 1).Value = "Category"; sheet.Cell(1, 2).Value = "ID";
                sheet.Cell(2, 1).Value = "Turnkey"; sheet.Cell(2, 2).Value = -1; sheet.Cell(2, 3).Value = "Turnkey";
                workbook.SaveAs(path);
            }
            Assert.Throws<InvalidDataException>(() => LookupLoader.LoadCategories(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ShippedLookupFilesHaveValidIdentifiers()
    {
        var options = CliOptions.Parse(["preview"]);
        Assert.NotEmpty(LookupLoader.LoadCategories(options.CategoryLookupPath));
        Assert.NotEmpty(LookupLoader.LoadSalesReps(options.SalesRepLookupPath));
    }

    [Fact]
    public void MissingNotApplicableCodePreventsNewAccountCreation()
    {
        using var s = new RetryBoundaryTests.Scenario("Create organization account", RetryBoundaryTests.TestBillingConfiguration() with { EventSalesNotApplicableCode = "" });
        Decide(s);
        Assert.Equal("REVIEW", s.Row.ValidationStatus);
        Assert.Contains("confirmed Not Applicable", s.Row.AccountDecisionMessage);
        Assert.Throws<InvalidOperationException>(() => s.Gateway.CreateOrganizationAccount(s.Row.RequestedBilling));
        s.Apply();
        Assert.Empty(s.Transport.Mutations);
        Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus);
        Assert.Throws<InvalidDataException>(() => (s.Options.Billing with { Header = "" }).Validate());
    }

    [Theory]
    [InlineData("account")] [InlineData("contact")] [InlineData("order")]
    public void ChangedEffectiveTargetStopsBeforeBillingWrites(string target)
    {
        using var s = new RetryBoundaryTests.Scenario();
        switch (target)
        {
            case "account": s.Transport.Accounts["ACCOUNT"] = Organization("ACCOUNT", street: "Changed");
                s.Transport.Accounts["ACCOUNT"].AccountUserFieldSets = [RetryBoundaryTests.TestFields(s.Row.RequestedBilling)]; break;
            case "contact": s.Transport.Accounts["CONTACT"] = Contact("CONTACT", email: "changed@example.invalid"); break;
            case "order": s.Transport.Order.BillToAccount = "OTHER"; break;
        }
        s.Apply();
        Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void ExistingAccountAddressMutationIsBlockedEvenForOldPlans()
    {
        using var s = new RetryBoundaryTests.Scenario();
        Assert.Throws<RecoveryReviewException>(() => s.Gateway.UpdateAccountAddress("ACCOUNT", s.Row.RequestedBilling));
        s.Row.BillToAddressAction = "UPDATE EXISTING BILL-TO ACCOUNT";
        s.Apply();
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void LegacyPlanRequiresReviewAndPreservesVerifiedStageIdentity()
    {
        using var s = new RetryBoundaryTests.Scenario("Create contact");
        s.Transport.FaultEnabled = false;
        var id = s.Gateway.CreateContact("ACCOUNT", "Offline Test Company", s.Row.RequestedBilling);
        s.Row.BillingVersion = 0;
        s.Apply();
        Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus);
        Assert.Equal("NEWCONTACT", id);
        Assert.Single(s.Transport.Mutations);
        Assert.Equal(StageStatus.Verified, s.Store.Load().Single().Stages.Single().Status);
    }

    [Theory]
    [InlineData((int)StageStatus.Dispatching)] [InlineData((int)StageStatus.Unknown)] [InlineData((int)StageStatus.Succeeded)] [InlineData((int)StageStatus.Verified)]
    public void HistoricalAddressStageReconcilesReadOnlyAndNeverAuthorizesAnotherUpdate(int status)
    {
        using var s = new RetryBoundaryTests.Scenario();
        var model = Organization("ACCOUNT");
        var journal = s.Gateway.Journal!;
        var stage = journal.Prepare("Update account address", "legacy address stage", JsonConvert.SerializeObject(new
        { model.AccountCode, model.Address1, model.City, model.State, model.PostalCode, model.Country }), null);
        stage.Status = (StageStatus)status;
        stage.Result = JsonConvert.SerializeObject(model);
        if (stage.Status == StageStatus.Verified) stage.VerifiedAt = DateTimeOffset.UtcNow;
        s.Row.BillingVersion = 0;
        // SerializeIntent represents blank fields as null for this historical fixture.
        model.State = "";
        stage.Intent = JsonConvert.SerializeObject(new { model.AccountCode, model.Address1, model.City, model.State, model.PostalCode, model.Country });
        journal.Save();
        var row = s.Apply(restart: true);
        Assert.Empty(s.Transport.Mutations);
        Assert.Equal(StageStatus.Verified, s.Store.Load().Single().Stages.Single().Status);
        Assert.Equal("RECOVERY REVIEW", row.ServiceOrderUpdateStatus);
    }

    [Fact]
    public void HistoricalCreatedAccountWithoutSalesStatusIntentRetainsStepTwoReconciliation()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var model = Organization("LEGACY"); model.State = "";
        s.Transport.Accounts["LEGACY"] = model;
        var journal = s.Gateway.Journal!;
        var stage = journal.Prepare("Create organization account", "legacy create stage", JsonConvert.SerializeObject(new
        { model.Organization, model.Class, model.Name, model.Address1, model.City, model.State, model.PostalCode, model.Country }), null);
        journal.Dispatching(stage);
        journal.Result(stage, JsonConvert.SerializeObject(model));
        s.Gateway.ReconcileIncomplete();
        Assert.Equal("LEGACY", journal.CreatedAccount("Create organization account"));
        Assert.Equal(StageStatus.Verified, stage.Status);
        Assert.Empty(s.Transport.Mutations);
    }
}
