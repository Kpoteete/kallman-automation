using Newtonsoft.Json;
using System.Net;
using System.Text;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class CrossAccountContactTests
{
    private static void Prepare(RetryBoundaryTests.Scenario s, bool newCompany = false, bool unlinked = false, bool differentAddress = false)
    {
        s.Transport.Accounts["CONTACT"] = new AllAccountsModel { Organization = "10", Class = "P", AccountCode = "CONTACT", PrimaryAccount = "ACCOUNT",
            FirstName = "Original", LastName = "Contact", Email = "original@example.invalid" };
        s.Transport.Accounts["OWNER"] = new AllAccountsModel { Organization = "10", Class = "O", AccountCode = "OWNER", Name = "Other Primary Company",
            Address1 = "2 Other Street", City = "Other City", PostalCode = "56789", Country = "***" };
        s.Transport.Accounts["CROSS"] = new AllAccountsModel { Organization = "10", Class = "P", AccountCode = "CROSS", PrimaryAccount = unlinked ? "" : "OWNER",
            FirstName = "Test", LastName = "Contact", Email = " Test@Example.Invalid ",
            Address1 = differentAddress ? "Old Contact Street" : "1 Test Street", City = "Test City", PostalCode = "12345", Country = "***" };
        if (newCompany)
        {
            s.Row.RequestedBilling = s.Row.RequestedBilling with { CompanyName = "New Billing Company" };
            s.Transport.BillingInstructions = s.Row.RequestedBilling;
        }
        else s.Transport.SearchOverride = request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/Accounts/10") || !Uri.UnescapeDataString(request.RequestUri.Query).Contains("Class eq 'O'")) return null;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(new[] {
                new AllAccountsModel { Organization = "10", Class = "O", AccountCode = "ACCOUNT", Name = "Offline Test Company",
                    Address1 = "1 Test Street", City = "Test City", PostalCode = "12345", Country = "***", EventSalesStatus = "A" }
            }), Encoding.UTF8, "application/json") };
            response.Headers.Add("X-SearchMetadata", JsonConvert.SerializeObject(new { Page = 1, Page_Size = 1000, PageTotal = 1, ResultsTotal = 1,
                Links = new { Next = (string?)null } }));
            return response;
        };
        Runner.DecideAccountAndAddress(s.Gateway, s.Row);
        Runner.DecideContact(s.Gateway, s.Row);
        (s.Row.ValidationStatus, s.Row.ValidationMessage) = ValidationRules.Validate(s.Row);
    }

    private static RelationshipsModel Link(string company = "ACCOUNT", string designation = USISDKConstants.RelationshipDesignationStatus.Secondary) => new()
        { MasterOrganizationCode = "10", MasterAccountCode = company, SubordinateOrganizationCode = "10", SubordinateAccountCode = "CROSS",
            RelationshipType = "CTA", EventSalesDesignation = designation };

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public void ReusedCrossAccountContactGetsBillingAddressBeforeAffiliation(bool newCompany, bool unlinked)
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.FaultEnabled = false;
        Prepare(s, newCompany, unlinked, differentAddress: true);
        Assert.Equal("UPDATE EXISTING CONTACT", s.Row.ContactAction);
        s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        var contact = s.Transport.Accounts["CROSS"];
        Assert.Equal("1 Test Street", contact.Address1);
        Assert.Equal(unlinked ? "" : "OWNER", contact.PrimaryAccount);
        Assert.Equal("CROSS", s.Transport.Order.BillToContact);
        Assert.Single(s.Transport.Mutations, x => x == "Update billing contact");
        Assert.DoesNotContain("Create contact", s.Transport.Mutations);
        Assert.True(s.Transport.Mutations.IndexOf("Update billing contact") < s.Transport.Mutations.IndexOf("Add CTA relationship"));
        var count = s.Transport.Mutations.Count;
        Assert.Equal("COMPLETED", s.Apply(restart: true).ServiceOrderUpdateStatus);
        Assert.Equal(count, s.Transport.Mutations.Count);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void CrossAccountAddressUpdateReconcilesLostResponseWithoutRepeatingIt(bool accepted)
    {
        using var s = new RetryBoundaryTests.Scenario("Update billing contact");
        Prepare(s, newCompany: true, differentAddress: true);
        // Account creation precedes the uncertain contact update and must itself be accepted.
        s.Transport.BeforeRequest = request =>
        {
            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath.EndsWith("/Accounts/10/CROSS"))
                s.Transport.ApplyEffect = accepted;
        };
        s.Apply();
        Assert.Equal("UNKNOWN WRITE OUTCOME", s.Row.ServiceOrderUpdateStatus);
        Assert.DoesNotContain("Update service order", s.Transport.Mutations);
        s.Transport.BeforeRequest = null;
        s.Transport.FaultEnabled = false;
        s.Transport.ApplyEffect = true;
        var resumed = s.Apply(restart: true);
        Assert.True(resumed.ServiceOrderUpdateStatus == (accepted ? "COMPLETED" : "RECOVERY REVIEW"), resumed.UpdateMessage);
        Assert.Single(s.Transport.Mutations, x => x == "Create organization account");
        Assert.Single(s.Transport.Mutations, x => x == "Update billing contact");
        Assert.DoesNotContain("Create contact", s.Transport.Mutations);
        Assert.Equal("OWNER", s.Transport.Accounts["CROSS"].PrimaryAccount);
    }

    [Fact]
    public void AddressUpdateThenVerifiedBlankPrimaryAffiliationCompletesAndResumes()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.FaultEnabled = false;
        Prepare(s, newCompany: true, unlinked: true, differentAddress: true);
        s.Transport.ResponseOverride = result =>
        {
            if (result is RelationshipsModel { RelationshipType: "CTA" } link)
                s.Transport.Accounts["CROSS"].PrimaryAccount = link.MasterAccountCode;
            return result;
        };
        s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.Equal("NEWACCOUNT", s.Transport.Accounts["CROSS"].PrimaryAccount);
        Assert.Equal("1 Test Street", s.Transport.Accounts["CROSS"].Address1);
        Assert.Equal("COMPLETED", s.Apply(restart: true).ServiceOrderUpdateStatus);
        Assert.Single(s.Transport.Mutations, x => x == "Update billing contact");
        Assert.Single(s.Transport.Mutations, x => x == "Add CTA relationship");
    }

    [Fact]
    public void UniqueVerifiedContactGetsSecondaryAffiliationWithoutMovingOrDuplicatingIt()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.FaultEnabled = false;
        Prepare(s);
        Assert.Equal("PREPARED", s.Row.ValidationStatus);
        Assert.Equal("CROSS", s.Row.FinalBillToContact);
        Assert.True(s.Row.EffectiveBilling!.ContactRelationshipPending);
        var original = JsonConvert.SerializeObject(s.Transport.Accounts["CROSS"]);
        s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.Equal("CROSS", s.Transport.Order.BillToContact);
        Assert.Equal("ACCOUNT", s.Transport.Order.BillToAccount);
        Assert.Equal(original, JsonConvert.SerializeObject(s.Transport.Accounts["CROSS"]));
        Assert.Contains("Add CTA relationship", s.Transport.Mutations);
        Assert.DoesNotContain("Create contact", s.Transport.Mutations);
        Assert.DoesNotContain("Update billing contact", s.Transport.Mutations);
        Assert.Equal(USISDKConstants.RelationshipDesignationStatus.Secondary, s.Transport.relationships["Relationships/10/ACCOUNT/CROSS/CTA"].EventSalesDesignation);
        Assert.False(s.Row.EffectiveBilling.ContactRelationshipPending);
    }

    [Fact]
    public void UniqueUnlinkedContactIsAffiliatedWithoutCreatingOrEditingAnotherContact()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.FaultEnabled = false;
        Prepare(s, unlinked: true);
        Assert.Equal("PREPARED", s.Row.ValidationStatus);
        var original = JsonConvert.SerializeObject(s.Transport.Accounts["CROSS"]);
        s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.Equal("CROSS", s.Transport.Order.BillToContact);
        Assert.Equal(original, JsonConvert.SerializeObject(s.Transport.Accounts["CROSS"]));
        Assert.Single(s.Transport.Mutations, x => x == "Add CTA relationship");
        Assert.DoesNotContain("Create contact", s.Transport.Mutations);
        Assert.DoesNotContain("Update billing contact", s.Transport.Mutations);
        Assert.False(s.Row.EffectiveBilling!.ContactRelationshipPending);
    }

    [Fact]
    public void UnlinkedContactWithAnExistingSecondaryAffiliationNeedsNoDuplicateRelationship()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.FaultEnabled = false;
        s.Transport.relationships["Relationships/10/ACCOUNT/CROSS/CTA"] = Link();
        Prepare(s, unlinked: true);
        Assert.Equal("READY", s.Row.ValidationStatus);
        s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.DoesNotContain("Add CTA relationship", s.Transport.Mutations);
        Assert.Equal("", s.Transport.Accounts["CROSS"].PrimaryAccount);
    }

    [Fact]
    public void APrimaryCompanyAddedAfterAnUnlinkedContactWasSelectedRequiresReview()
    {
        using var s = new RetryBoundaryTests.Scenario();
        Prepare(s, unlinked: true);
        s.Transport.Accounts["CROSS"].PrimaryAccount = "ACCOUNT";
        s.Apply();
        Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void DuplicateUnlinkedContactsCannotAuthorizeReuseOrAnotherCreation()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Accounts["DUPLICATE"] = new AllAccountsModel { Class = "P", AccountCode = "DUPLICATE", PrimaryAccount = "",
            FirstName = "Test", LastName = "Contact", Email = "test@example.invalid" };
        Prepare(s, unlinked: true);
        Assert.Equal("REVIEW", s.Row.ValidationStatus);
        s.Apply();
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void UnknownUnlinkedContactAffiliationStopsAssignmentAndEmail()
    {
        using var s = new RetryBoundaryTests.Scenario("Add CTA relationship");
        Prepare(s, unlinked: true);
        s.Apply();
        Assert.Equal("UNKNOWN WRITE OUTCOME", s.Row.ServiceOrderUpdateStatus);
        Assert.Equal(new[] { "Add CTA relationship" }, s.Transport.Mutations);
        Assert.Equal("CONTACT", s.Transport.Order.BillToContact);
        Assert.Empty(s.Transport.SavedEmails);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ACCOUNT")]
    public void NewlyCreatedContactRequiresVerifiedAffiliationEvenWhenPrimaryAccountIsIgnored(string primary)
    {
        using var s = new RetryBoundaryTests.Scenario("Create contact");
        s.Transport.FaultEnabled = false;
        s.Transport.ResponseOverride = result =>
        {
            if (result is AllAccountsModel { Class: "P", AccountCode: "NEWCONTACT" } contact) contact.PrimaryAccount = primary;
            return result;
        };
        s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.Equal(primary, s.Transport.Accounts["NEWCONTACT"].PrimaryAccount);
        Assert.Equal("NEWCONTACT", s.Transport.Order.BillToContact);
        Assert.Single(s.Transport.Mutations, x => x == "Create contact");
        Assert.Single(s.Transport.Mutations, x => x == "Add CTA relationship");
        Assert.True(s.Transport.Mutations.IndexOf("Add CTA relationship") < s.Transport.Mutations.IndexOf("Update service order"));
        Assert.Equal(USISDKConstants.RelationshipDesignationStatus.Secondary,
            s.Transport.relationships["Relationships/10/ACCOUNT/NEWCONTACT/CTA"].EventSalesDesignation);
        var create = s.Store.Load().Single().Stages.Single(x => x.Operation == "Create contact");
        Assert.Equal(StageStatus.Verified, create.Status);
        Assert.Equal("NEWCONTACT", JsonConvert.DeserializeObject<AllAccountsModel>(create.Result)!.AccountCode);
        Assert.Equal("COMPLETED", s.Apply(restart: true).ServiceOrderUpdateStatus);
        Assert.Single(s.Transport.Mutations, x => x == "Create contact");
        Assert.Single(s.Transport.Mutations, x => x == "Add CTA relationship");
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("name")]
    [InlineData("email")]
    [InlineData("company")]
    public void IncorrectCreatedContactIdentityStillStopsAllLaterWrites(string changed)
    {
        using var s = new RetryBoundaryTests.Scenario("Create contact");
        s.Transport.FaultEnabled = false;
        s.Transport.ResponseOverride = result =>
        {
            if (result is AllAccountsModel { Class: "P", AccountCode: "NEWCONTACT" } contact)
            {
                contact.PrimaryAccount = changed == "parent" ? "WRONG" : "";
                if (changed == "name") contact.LastName = "Wrong";
                if (changed == "email") contact.Email = "wrong@example.invalid";
                if (changed == "company") contact.Company = "Wrong Company";
            }
            return result;
        };
        s.Apply();
        Assert.Equal("UNKNOWN WRITE OUTCOME", s.Row.ServiceOrderUpdateStatus);
        Assert.Equal(new[] { "Create contact" }, s.Transport.Mutations);
        Assert.Equal("CONTACT", s.Transport.Order.BillToContact);
        Assert.Empty(s.Transport.SavedEmails);
    }

    [Fact]
    public void AcceptedUnlinkedContactCreationIsNeverRepeatedAfterUnknownAffiliation()
    {
        using var s = new RetryBoundaryTests.Scenario("Add CTA relationship");
        s.Row.ContactAction = "CREATE CONTACT";
        BillingState.Set(s.Row, s.Row.EffectiveBilling! with { Contact = BillingRules.RequestedContact(s.Row.RequestedBilling), ContactCreationPending = true });
        s.Transport.ResponseOverride = result =>
        {
            if (result is AllAccountsModel { Class: "P", AccountCode: "NEWCONTACT" } contact) contact.PrimaryAccount = "";
            return result;
        };
        s.Apply();
        Assert.Equal("UNKNOWN WRITE OUTCOME", s.Row.ServiceOrderUpdateStatus);
        Assert.Equal(new[] { "Create contact", "Add CTA relationship" }, s.Transport.Mutations);
        Assert.Equal("CONTACT", s.Transport.Order.BillToContact);
        Assert.Empty(s.Transport.SavedEmails);
        s.Transport.FaultEnabled = false;
        var resumed = s.Apply(restart: true);
        Assert.True(resumed.ServiceOrderUpdateStatus == "COMPLETED", resumed.UpdateMessage);
        Assert.Single(s.Transport.Mutations, x => x == "Create contact");
        Assert.Single(s.Transport.Mutations, x => x == "Add CTA relationship");
    }

    [Fact]
    public void NewlyCreatedUnlinkedContactCanAcquireIntendedPrimaryCompanyDuringVerifiedAffiliation()
    {
        using var s = new RetryBoundaryTests.Scenario("Create contact");
        s.Transport.FaultEnabled = false;
        s.Transport.ResponseOverride = result =>
        {
            if (result is AllAccountsModel { Class: "P", AccountCode: "NEWCONTACT" } contact) contact.PrimaryAccount = "";
            if (result is RelationshipsModel { RelationshipType: "CTA" }) s.Transport.Accounts["NEWCONTACT"].PrimaryAccount = "ACCOUNT";
            return result;
        };
        s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.Equal("NEWCONTACT", s.Transport.Order.BillToContact);
        Assert.Equal("ACCOUNT", s.Store.Load().Single().Plan.EffectiveBilling!.Contact.PrimaryAccount);
        var resumed = s.Apply(restart: true);
        Assert.True(resumed.ServiceOrderUpdateStatus == "COMPLETED", resumed.UpdateMessage);
        Assert.Single(s.Transport.Mutations, x => x == "Create contact");
        Assert.Single(s.Transport.Mutations, x => x == "Add CTA relationship");
    }

    [Fact]
    public void PreviouslyUnlinkedContactCanAcquireOnlyTheVerifiedAffiliationCompany()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.FaultEnabled = false;
        Prepare(s, unlinked: true);
        s.Transport.ResponseOverride = result =>
        {
            if (result is RelationshipsModel { RelationshipType: "CTA" }) s.Transport.Accounts["CROSS"].PrimaryAccount = "ACCOUNT";
            return result;
        };
        s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.Equal("CROSS", s.Transport.Order.BillToContact);
        var saved = s.Store.Load().Single();
        Assert.Equal("ACCOUNT", saved.Plan.EffectiveBilling!.Contact.PrimaryAccount);
        var affiliation = saved.Stages.Single(x => x.Operation == "Add relationship");
        Assert.Equal("", JsonConvert.DeserializeObject<AccountInfo>(affiliation.Source["ContactBeforeAffiliation"])!.PrimaryAccount);
        Assert.Equal(StageStatus.Verified, affiliation.Status);
        Assert.False(saved.Plan.EffectiveBilling.ContactRelationshipPending);
        Assert.True(s.Apply(restart: true).ServiceOrderUpdateStatus == "COMPLETED");
        Assert.Single(s.Transport.Mutations, x => x == "Add CTA relationship");
        Assert.DoesNotContain("Create contact", s.Transport.Mutations);
        Assert.DoesNotContain("Update billing contact", s.Transport.Mutations);
        var before = s.Transport.Mutations.Count;
        s.Transport.Accounts["CROSS"].PrimaryAccount = "OWNER";
        Assert.Equal("RECOVERY REVIEW", s.Apply(restart: true).ServiceOrderUpdateStatus);
        Assert.Equal(before, s.Transport.Mutations.Count);
    }

    [Theory]
    [InlineData("primary")] [InlineData("name")] [InlineData("email")] [InlineData("address")]
    [InlineData("company")] [InlineData("status")]
    public void InitialAffiliationDoesNotPermitOtherContactChanges(string changed)
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.FaultEnabled = false;
        Prepare(s, unlinked: true);
        s.Transport.ResponseOverride = result =>
        {
            if (result is RelationshipsModel { RelationshipType: "CTA" })
            {
                var contact = s.Transport.Accounts["CROSS"];
                contact.PrimaryAccount = changed == "primary" ? "OWNER" : "ACCOUNT";
                if (changed == "name") contact.LastName = "Changed";
                if (changed == "email") contact.Email = "changed@example.invalid";
                if (changed == "address") contact.Address1 = "Changed Street";
                if (changed == "company") contact.Name = "Changed Display Name";
                if (changed == "status") contact.EventSalesStatus = "I";
            }
            return result;
        };
        s.Apply();
        Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus);
        Assert.Equal(new[] { "Add CTA relationship" }, s.Transport.Mutations);
        Assert.Equal("CONTACT", s.Transport.Order.BillToContact);
        Assert.Empty(s.Transport.SavedEmails);
    }

    [Fact]
    public void UnknownAffiliationMustReconcileBeforeAcceptingItsInitialPrimaryCompany()
    {
        using var s = new RetryBoundaryTests.Scenario("Add CTA relationship");
        Prepare(s, unlinked: true);
        s.Transport.AccountReadOverride = model =>
        {
            if (model.AccountCode == "CROSS" && s.Transport.relationships.ContainsKey("Relationships/10/ACCOUNT/CROSS/CTA"))
                model.PrimaryAccount = "ACCOUNT";
            return model;
        };
        s.Apply();
        Assert.Equal("UNKNOWN WRITE OUTCOME", s.Row.ServiceOrderUpdateStatus);
        Assert.Equal(new[] { "Add CTA relationship" }, s.Transport.Mutations);
        Assert.Equal("CONTACT", s.Transport.Order.BillToContact);
        Assert.Empty(s.Transport.SavedEmails);
        s.Transport.FaultEnabled = false;
        var resumed = s.Apply(restart: true);
        Assert.True(resumed.ServiceOrderUpdateStatus == "COMPLETED", resumed.UpdateMessage);
        Assert.Equal("ACCOUNT", resumed.EffectiveBilling!.Contact.PrimaryAccount);
        Assert.Single(s.Transport.Mutations, x => x == "Add CTA relationship");
    }

    [Fact]
    public void ExistingAffiliationWithoutDispatchedOriginalSnapshotCannotAuthorizePrimaryChange()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.FaultEnabled = false;
        Prepare(s, unlinked: true);
        s.Gateway.EnsureRelationship("ACCOUNT", "CROSS", "CTA");
        var saved = s.Store.Load().Single();
        saved.Stages.Single().Source.Clear(); // A legacy stage proves the link, not the contact's pre-dispatch state.
        s.Store.Save(saved);
        s.Transport.Accounts["CROSS"].PrimaryAccount = "ACCOUNT";
        Assert.Equal("RECOVERY REVIEW", s.Apply(restart: true).ServiceOrderUpdateStatus);
        Assert.Equal(new[] { "Add CTA relationship" }, s.Transport.Mutations);
        Assert.Empty(s.Transport.SavedEmails);
    }

    [Fact]
    public void InitialPrimaryCompanyRequiresTheAffiliationToStillBeSecondary()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.FaultEnabled = false;
        Prepare(s, unlinked: true);
        s.Transport.AccountReadOverride = model =>
        {
            if (model.AccountCode == "CROSS" && s.Transport.relationships.TryGetValue("Relationships/10/ACCOUNT/CROSS/CTA", out var link))
            {
                model.PrimaryAccount = "ACCOUNT";
                link.EventSalesDesignation = USISDKConstants.RelationshipDesignationStatus.Primary;
            }
            return model;
        };
        s.Apply();
        Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus);
        Assert.Equal(new[] { "Add CTA relationship" }, s.Transport.Mutations);
        Assert.Equal("CONTACT", s.Transport.Order.BillToContact);
        Assert.Empty(s.Transport.SavedEmails);
    }

    [Fact]
    public void ANewBillingCompanyCanUseTheExistingContactThroughTheNewAffiliation()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.FaultEnabled = false;
        Prepare(s, newCompany: true);
        s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.Equal("NEWACCOUNT", s.Transport.Order.BillToAccount);
        Assert.Equal("CROSS", s.Transport.Order.BillToContact);
        Assert.Equal("OWNER", s.Transport.Accounts["CROSS"].PrimaryAccount);
        Assert.Contains("Add CTA relationship", s.Transport.Mutations);
        Assert.DoesNotContain("Create contact", s.Transport.Mutations);
        Assert.Equal(USISDKConstants.RelationshipDesignationStatus.Secondary, s.Transport.relationships["Relationships/10/NEWACCOUNT/CROSS/CTA"].EventSalesDesignation);
    }

    [Fact]
    public void ExistingAffiliationIsVerifiedAndNeverAddedAgain()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.FaultEnabled = false;
        s.Transport.relationships["Relationships/10/ACCOUNT/CROSS/CTA"] = Link();
        Prepare(s);
        Assert.Equal("READY", s.Row.ValidationStatus);
        s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.DoesNotContain("Add CTA relationship", s.Transport.Mutations);
    }

    [Theory]
    [InlineData("name")] [InlineData("email")] [InlineData("parent")]
    public void ChangedContactIdentityStopsBeforeAnyWrites(string changed)
    {
        using var s = new RetryBoundaryTests.Scenario();
        Prepare(s);
        if (changed == "name") s.Transport.Accounts["CROSS"].FirstName = "Changed";
        if (changed == "email") s.Transport.Accounts["CROSS"].Email = "changed@example.invalid";
        if (changed == "parent") s.Transport.Accounts["CROSS"].PrimaryAccount = "ACCOUNT";
        s.Apply();
        Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void ANewGlobalDuplicateStopsTheAffiliationBeforeWrites()
    {
        using var s = new RetryBoundaryTests.Scenario();
        Prepare(s);
        s.Transport.Accounts["DUPLICATE"] = new AllAccountsModel { Class = "P", AccountCode = "DUPLICATE", PrimaryAccount = "OWNER",
            FirstName = "Test", LastName = "Contact", Email = "test@example.invalid" };
        s.Apply();
        Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void MultipleCrossAccountContactsRemainReview()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Accounts["DUPLICATE"] = new AllAccountsModel { Class = "P", AccountCode = "DUPLICATE", PrimaryAccount = "OWNER",
            FirstName = "Test", LastName = "Contact", Email = "test@example.invalid" };
        Prepare(s);
        Assert.Equal("REVIEW", s.Row.ValidationStatus);
        s.Apply();
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void UnknownAffiliationOutcomeNeverDispatchesOrderUpdateOrEmailOrRetries()
    {
        using var s = new RetryBoundaryTests.Scenario("Add CTA relationship");
        Prepare(s);
        s.Apply();
        Assert.Equal("UNKNOWN WRITE OUTCOME", s.Row.ServiceOrderUpdateStatus);
        Assert.Equal(new[] { "Add CTA relationship" }, s.Transport.Mutations);
        Assert.Equal("CONTACT", s.Transport.Order.BillToContact);
        Assert.Equal("OWNER", s.Transport.Accounts["CROSS"].PrimaryAccount);
        Assert.Empty(s.Transport.SavedEmails);
    }

    [Fact]
    public void InactiveAffiliationRequiresReviewWithoutReactivation()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.relationships["Relationships/10/ACCOUNT/CROSS/CTA"] = Link(designation: USISDKConstants.RelationshipDesignationStatus.Inactive);
        Prepare(s);
        s.Apply();
        Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void AnUnexpectedPrimaryCompanyChangeAfterAffiliationStopsOrderAssignment()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.FaultEnabled = false;
        Prepare(s);
        s.Transport.ResponseOverride = result =>
        {
            if (result is RelationshipsModel { RelationshipType: "CTA" }) s.Transport.Accounts["CROSS"].PrimaryAccount = "ACCOUNT";
            return result;
        };
        s.Apply();
        Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus);
        Assert.Equal(new[] { "Add CTA relationship" }, s.Transport.Mutations);
        Assert.Equal("CONTACT", s.Transport.Order.BillToContact);
        Assert.Empty(s.Transport.SavedEmails);
    }

    [Fact]
    public void APrimaryDesignationInsteadOfSecondaryFailsReadbackAndStopsLaterWrites()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.FaultEnabled = false;
        Prepare(s);
        s.Transport.ResponseOverride = result =>
        {
            if (result is RelationshipsModel { RelationshipType: "CTA" } relationship)
                relationship.EventSalesDesignation = USISDKConstants.RelationshipDesignationStatus.Primary;
            return result;
        };
        s.Apply();
        Assert.Equal("UNKNOWN WRITE OUTCOME", s.Row.ServiceOrderUpdateStatus);
        Assert.Equal(new[] { "Add CTA relationship" }, s.Transport.Mutations);
        Assert.Equal("CONTACT", s.Transport.Order.BillToContact);
        Assert.Empty(s.Transport.SavedEmails);
    }

    [Fact]
    public void MissingAffiliationAfterCompletionBlocksRetainedHandoff()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.FaultEnabled = false;
        Prepare(s);
        s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        s.Transport.relationships.Clear();
        var before = s.Transport.Mutations.Count;
        var resumed = s.Apply(restart: true);
        Assert.Equal("RECOVERY REVIEW", resumed.ServiceOrderUpdateStatus);
        Assert.Equal(before, s.Transport.Mutations.Count);
    }
}
