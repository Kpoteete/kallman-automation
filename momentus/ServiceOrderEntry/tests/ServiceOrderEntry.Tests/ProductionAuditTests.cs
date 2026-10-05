using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class ProductionAuditTests
{
    [Fact] public void ConfirmedTenantZeroStatusCodeIsWrittenAndVerifiedForNewBillingAccount()
    {
        using var s = new RetryBoundaryTests.Scenario("Create organization account", RetryBoundaryTests.TestBillingConfiguration() with { EventSalesNotApplicableCode = "0" });
        s.Transport.FaultEnabled = false; s.Apply();
        Assert.Equal("SUCCESS", s.Row.Outcome); Assert.Equal("0", s.Transport.Accounts["NEWACCOUNT"].EventSalesStatus);
        Assert.Equal("0", s.Store.Load().Single().Handoff!.Billing.Account.EventSalesStatus);
        Assert.Equal(s.Row.RequestedBilling.CompanyName, s.Transport.Accounts["NEWACCOUNT"].Name);
    }
    [Theory] [InlineData("Send ready email", "Documents/10")] [InlineData("Update service order", "ServiceOrders/10/3")]
    public void AcceptedWriteWithUnavailableReadbackIsUnknownAndRecoversWithoutRedispatch(string operation, string path)
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.BeforeRequest = _ => { if (s.Gateway.Journal?.Evidence.Stages.Any(x => x.Operation == operation && x.Status == StageStatus.Succeeded) == true) s.Transport.FailReadPath = path; };
        s.Apply(); Assert.Equal("UNKNOWN", s.Row.Outcome); Assert.Equal(3, Runner.ApplyExitCode([s.Row]));
        Assert.Equal(StageStatus.Succeeded, s.Store.Load().Single().Stages.Single(x => x.Operation == operation).Status);
        s.Transport.BeforeRequest = null; s.Transport.FailReadPath = null;
        var row = s.Apply(restart: true); Assert.Equal("SUCCESS", row.Outcome);
        Assert.Single(s.Transport.Mutations, x => x == operation);
    }
    [Theory] [InlineData("Order")] [InlineData("Exhibitor")] [InlineData("Nearby")]
    public void ForeignOwnerSearchResultCannotSupplyBillingContractAttachment(string owner)
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.SearchOverride = request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/Documents/10")) return null;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(
                Newtonsoft.Json.JsonConvert.SerializeObject(new[] { new DocumentsModel { Organization = "10", Type = "C", SequenceNumber = 11,
                    DocumentID = "contract.pdf", Category = "CON", Order = 999, Exhibitor = 999 } }), System.Text.Encoding.UTF8, "application/json") };
            response.Headers.Add("X-SearchMetadata", "{\"Page\":1,\"Page_Size\":1,\"PageTotal\":1,\"ResultsTotal\":1}");
            return response;
        };
        Assert.Throws<RecoveryReviewException>(() =>
        {
            if (owner == "Order") s.Gateway.GetOrderContractPdfs(3);
            else if (owner == "Exhibitor") s.Gateway.GetExhibitorContractPdfs(2);
            else s.Gateway.GetNearbyExhibitorPdfs(2, DateTime.Today);
        });
        Assert.Empty(s.Transport.Mutations);
    }
    [Theory] [InlineData("Sponsor", "TK", "")] [InlineData("Turnkey", "Sponsor", "")]
    [InlineData("Turnkey", "TK", "Sponsor package")]
    public void ExplicitItemNegationCannotBeOverriddenByCategoryOrOtherItemLabels(string category, string resource, string alternate) =>
        Assert.DoesNotContain(5, ExhibitorCategoryRules.Resolve("", category, [new(1, resource, "no sponsorship", alternate)], "N").Final);

    [Fact] public void DistinctAffirmativeSponsorshipItemStillAddsSponsor() => Assert.Contains(5,
        ExhibitorCategoryRules.Resolve("", "Turnkey", [new(1, "TK", "no sponsorship", ""), new(2, "SP", "Gold Sponsorship", "")], "N").Final);

    static ServiceOrdersModel Other() => new() { OrganizationCode = "10", OrderNumber = 4, Event = 1, Exhibitor = 2,
        Function = 4, Account = "ACCOUNT", BillToAccount = "ACCOUNT", BillToContact = "CONTACT", OrderStatus = "PC" };

    [Theory] [InlineData("103")] [InlineData("102")] [InlineData("77")] [InlineData("PC")]
    public void LastGroupReadPreservesNewCategoriesAndRespectsNewBlockers(string added)
    {
        using var s = new RetryBoundaryTests.Scenario(); var groupSearches = 0; var changed = false;
        s.Transport.BeforeRequest = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/ServiceOrders/10")) groupSearches++;
            if (!changed && groupSearches >= 2 && request.RequestUri.AbsolutePath.EndsWith("/Notes/10"))
            { changed = true; if (added == "PC") s.Transport.OtherOrders.Add(Other()); else s.Transport.Exhibitor.ExhibitorCategory = "1," + added; }
        };
        s.Apply(); Assert.True(changed); if (added != "PC") Assert.Contains(added, s.Transport.Exhibitor.ExhibitorCategory);
        if (added != "77") { Assert.Equal(35, s.Transport.Exhibitor.ExhibitorStatus); Assert.DoesNotContain("Activate exhibitor", s.Transport.Mutations); }
        else Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus);
    }

    [Fact] public void AttemptCapCannotReportUnprocessedApplyOrdersAsSuccess()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.OtherOrders.Add(Other());
        var runner = new Runner(s.Options with { ActivateExhibitor = false }); runner.UseJournalStore(s.Store);
        Assert.Equal(2, runner.ProcessCandidates(s.Gateway, [new(s.Transport.Order, s.Transport.Exhibitor), new(s.Transport.OtherOrders[0], s.Transport.Exhibitor)],
            [], [new("Turnkey", 28, ["Turnkey"])]));
        Assert.Equal("PC", s.Transport.OtherOrders[0].OrderStatus); Assert.False(s.Store.Load().Single(x => x.Identity.Order == 4).OrderComplete);
    }

    [Theory] [InlineData("organization")] [InlineData("billing")] [InlineData("note")] [InlineData("attachment")]
    [InlineData("items")] [InlineData("selector")] [InlineData("exhibitor type")] [InlineData("salesrep")]
    public void CompletedOrderRecoveryRechecksFinalEvidenceBeforeSharedActivation(string field)
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.OtherOrders.Add(Other()); s.Apply();
        Assert.True(s.Store.Load().Single().OrderComplete); Assert.Equal(35, s.Transport.Exhibitor.ExhibitorStatus);
        s.Transport.OtherOrders[0].OrderStatus = "A";
        if (field == "organization") s.Transport.Exhibitor.OrganizationCode = "99";
        if (field == "billing") s.Transport.Order.BillToAccount = "OTHER";
        if (field == "note") s.Transport.Notes.Single().PlainText = "changed payment terms";
        if (field == "attachment") s.Transport.Copies.Clear();
        if (field == "items") s.Transport.Items[0].Units = 7;
        if (field == "selector") s.Transport.BillingInstructions = s.Row.RequestedBilling with { UseRequestedAddress = "ECA" };
        if (field == "exhibitor type") s.Transport.Exhibitor.ExhibitorType = "SE";
        if (field == "salesrep") s.Transport.Exhibitor.Salesperson = "OTHER";
        var count = s.Transport.Mutations.Count; var row = s.Apply(restart: true);
        Assert.Equal("REVIEW", row.Outcome); Assert.Equal(count, s.Transport.Mutations.Count);
        Assert.Equal(35, s.Transport.Exhibitor.ExhibitorStatus);
    }

    [Fact] public void CompletedOrderWithoutSharedActivationCannotReportStaleBillingReady()
    {
        using var s = new RetryBoundaryTests.Scenario(); var options = s.Options with { ActivateExhibitor = false };
        var runner = new Runner(options); runner.UseJournalStore(s.Store);
        runner.Apply(s.Gateway, new(s.Transport.Order, s.Transport.Exhibitor), s.Row);
        Assert.Equal("SUCCESS", s.Row.Outcome); var count = s.Transport.Mutations.Count;
        s.Transport.Order.BillToContact = "OTHER";
        var row = s.Store.Load().Single().Plan;
        runner.Apply(s.Gateway, new(s.Transport.Order, s.Transport.Exhibitor), row);
        Assert.Equal("REVIEW", row.Outcome); Assert.Equal("REVIEW", row.ValidationStatus); Assert.Equal(count, s.Transport.Mutations.Count);
    }
}
