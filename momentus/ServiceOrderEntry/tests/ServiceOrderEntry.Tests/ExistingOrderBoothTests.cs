using Newtonsoft.Json;
using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class ExistingOrderBoothTests
{
    static RunRow Evaluate(RetryBoundaryTests.Scenario s)
    {
        ConfigureBilling(s);
        return Runner.Evaluate(s.Gateway, new(s.Transport.Order, s.Transport.Exhibitor), [], [new("Turnkey", 28, ["Turnkey"])]);
    }

    static void ConfigureBilling(RetryBoundaryTests.Scenario s)
    {
        s.Transport.Accounts["CONTACT"] = s.Gateway.GetAccountModel("CONTACT");
        // Booth-only scenarios start with a contact already using the selected billing address.
        var account = s.Gateway.GetAccountModel("ACCOUNT");
        var contact = s.Transport.Accounts["CONTACT"];
        contact.Address1 = account.Address1; contact.City = account.City; contact.State = account.State;
        contact.PostalCode = account.PostalCode; contact.Country = account.Country;
        s.Transport.BillingInstructions = s.Transport.BillingInstructions with { UseRequestedAddress = "ECA" };
    }

    static void Apply(RetryBoundaryTests.Scenario s, RunRow row)
    {
        var runner = new Runner(s.Options);
        runner.UseJournalStore(s.Store);
        runner.Apply(s.Gateway, new(s.Transport.Order, s.Transport.Exhibitor), row);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("different")]
    [InlineData("unavailable")]
    public void ExistingOrderBoothCompletesWithoutActivityLookupAndSurvivesRecovery(string activity)
    {
        using var s = new RetryBoundaryTests.Scenario();
        const string booth = " S11.D76, S11.D77 ";
        s.Transport.Order.BoothNumber = booth;
        if (activity == "missing") s.Transport.Activities.Clear();
        if (activity == "unavailable") s.Transport.FailReadPath = "Activities/10";
        var row = Evaluate(s);
        Assert.True(row.ValidationStatus == "READY", row.ValidationMessage);
        Assert.Equal(booth, row.ProposedBoothNumber);
        Assert.True(row.DecisionInputs!.Booth.UsesExistingOrderBooth);
        Apply(s, row);
        Assert.True(row.Outcome == "SUCCESS", row.UpdateMessage);
        Assert.Equal(booth, s.Transport.Order.BoothNumber);
        Assert.Equal("A", s.Transport.Order.OrderStatus);
        Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus);
        Assert.Single(s.Transport.Emails);
        var evidence = Assert.Single(s.Store.Load());
        Assert.True(evidence.Plan.DecisionInputs!.Booth.UsesExistingOrderBooth);
        Assert.Equal(booth, JsonConvert.DeserializeObject<ServiceOrdersModel>(
            evidence.Stages.Single(x => x.Operation == "Update service order").Intent)!.BoothNumber);
        Assert.Equal(booth, evidence.Handoff!.Order.Booth);
        var mutations = s.Transport.Mutations.Count;
        Apply(s, evidence.Plan);
        Assert.Equal("SUCCESS", evidence.Plan.Outcome);
        Assert.Equal(mutations, s.Transport.Mutations.Count);
        Assert.DoesNotContain("GET Activities/10", s.Transport.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOrderBoothStillUsesActivityAndIsApplied(string? existing)
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Order.BoothNumber = existing;
        var row = Evaluate(s);
        Assert.True(row.ValidationStatus == "READY", row.ValidationMessage);
        Assert.Equal("101", row.ProposedBoothNumber);
        Assert.False(row.DecisionInputs!.Booth.UsesExistingOrderBooth);
        Apply(s, row);
        Assert.True(row.Outcome == "SUCCESS", row.UpdateMessage);
        Assert.Equal("101", s.Transport.Order.BoothNumber);
        Assert.Contains("GET Activities/10", s.Transport.Requests);
    }

    [Fact]
    public void MissingOrderBoothAndMissingActivityRetainPendingStatusesAndFillOtherFields()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Activities.Clear();
        var runner = new Runner(s.Options);
        runner.UseJournalStore(s.Store);
        ConfigureBilling(s);
        Assert.Equal(2, runner.ProcessCandidates(s.Gateway, [new(s.Transport.Order, s.Transport.Exhibitor)], [], [new("Turnkey", 28, ["Turnkey"])]));
        Assert.Contains(Assert.Single(s.Store.Load()).Plan.Issues, x => x.Component == "Booth");
        Assert.Equal("PC", s.Transport.Order.OrderStatus);
        Assert.Equal(35, s.Transport.Exhibitor.ExhibitorStatus);
        Assert.Equal("REP", s.Transport.Order.OrderAccountRep);
        Assert.Equal(28, s.Transport.Order.Category);
        Assert.DoesNotContain("Activate order", s.Transport.Mutations);
        Assert.DoesNotContain("Activate exhibitor", s.Transport.Mutations);
        Assert.Equal("kylep@kallman.com", Assert.Single(Assert.Single(s.Transport.Emails).SendToAddresses).EmailAddress);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("OTHER")]
    public void ExistingOrderBoothChangedBeforeApplyStopsEveryWrite(string? changed)
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Order.BoothNumber = "S11.D76";
        var row = Evaluate(s);
        s.Transport.Order.BoothNumber = changed;
        Apply(s, row);
        Assert.Equal("REVIEW", row.Outcome);
        Assert.Contains("existing order booth", row.UpdateMessage);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void ExistingOrderBoothChangedBetweenStagesStopsFurtherWrites()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Order.BoothNumber = "S11.D76";
        var row = Evaluate(s);
        var changed = false;
        s.Transport.BeforeRequest = _ =>
        {
            if (!changed && s.Gateway.Journal?.Evidence.Stages.Any(x => x.Operation == "Update service order" && x.Status == StageStatus.Verified) == true)
            { changed = true; s.Transport.Order.BoothNumber = "OTHER"; }
        };
        Apply(s, row);
        Assert.True(changed);
        Assert.Equal("REVIEW", row.Outcome);
        Assert.Single(s.Transport.Mutations);
        Assert.Empty(s.Transport.Emails);
        Assert.Equal("OTHER", s.Transport.Order.BoothNumber);
    }

    [Fact]
    public void CompletedOrderWithChangedBoothRequiresReviewWithoutMoreWrites()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Order.BoothNumber = "S11.D76";
        var row = Evaluate(s);
        Apply(s, row);
        Assert.Equal("SUCCESS", row.Outcome);
        var mutations = s.Transport.Mutations.Count;
        s.Transport.Order.BoothNumber = "OTHER";
        Apply(s, Assert.Single(s.Store.Load()).Plan);
        Assert.Equal("REVIEW", Assert.Single(s.Store.Load()).Plan.Outcome);
        Assert.Equal(mutations, s.Transport.Mutations.Count);
        Assert.Single(s.Transport.Emails);
    }

    [Fact]
    public void LegacyBoothEvidenceRetainsActivityValidation()
    {
        var booth = JsonConvert.DeserializeObject<BoothEvidence>("{\"Booth\":\"101\",\"Hash\":\"legacy\"}")!;
        Assert.False(booth.UsesExistingOrderBooth);
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Activities.Clear();
        s.Apply();
        Assert.Equal("REVIEW", s.Row.Outcome);
        Assert.Empty(s.Transport.Mutations);
    }
}
