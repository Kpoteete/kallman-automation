using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class ApprovalEmailOnlyTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void NewlyAddedApprovalCategoryRefreshesEmailWithoutHoldingActivation(bool afterOrderActivation)
    {
        using var s = new RetryBoundaryTests.Scenario();
        if (!afterOrderActivation) s.Transport.Exhibitor.ExhibitorCategory = "102";
        else s.Transport.BeforeRequest = _ =>
        {
            if (s.Gateway.Journal?.Evidence.Stages.Any(x => x.Operation == "Activate order" && x.Status == StageStatus.Verified) == true)
                s.Transport.Exhibitor.ExhibitorCategory = "1,102";
        };
        s.Apply();
        Assert.True(s.Row.Outcome == "SUCCESS", s.Row.UpdateMessage);
        Assert.Equal("A", s.Transport.Order.OrderStatus);
        Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus);
        Assert.StartsWith("WAIT FOR SALES APPROVAL", Assert.Single(s.Transport.Emails).EmailSubject);
        Assert.True(s.Store.Load().Single().Handoff!.ApprovalNeeded);
    }

    [Fact]
    public void ApprovalNeededActivatesBothStatusesAndSendsWarningLast()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Exhibitor.ExhibitorCategory = "102";
        s.Row.ApprovalNeeded = true;
        s.Row.ExistingExhibitorCategories = "102";
        s.Row.FinalExhibitorCategories = "1,102";
        s.Row.DecisionInputs = s.Row.DecisionInputs! with { ApprovalNeeded = true };
        s.Apply();
        Assert.True(s.Row.Outcome == "SUCCESS", s.Row.UpdateMessage);
        Assert.Equal("A", s.Transport.Order.OrderStatus);
        Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus);
        Assert.Contains("102", s.Transport.Exhibitor.ExhibitorCategory);
        var email = Assert.Single(s.Transport.Emails);
        Assert.StartsWith("WAIT FOR SALES APPROVAL", email.EmailSubject);
        Assert.Contains("Do not continue until you receive notice from Sales", email.HtmlText);
        Assert.Equal(new[] { "Activate order", "Activate exhibitor", "Send ready email" }, s.Transport.Mutations.TakeLast(3));
        var count = s.Transport.Mutations.Count;
        Assert.Equal("SUCCESS", s.Apply(restart: true).Outcome);
        Assert.Equal(count, s.Transport.Mutations.Count);
        Assert.Single(s.Transport.Emails);
    }

    [Fact]
    public void ApprovalCannotSubstituteForVerifiedOrderActivation()
    {
        var evidence = new OrderEvidence { ActivateOrder = true, Plan = new RunRow { ApprovalNeeded = true } };
        Assert.Throws<RecoveryReviewException>(() => HandoffRules.RequireOrderStages(evidence, includeActivation: true));
    }

    [Fact]
    public void HoldStillStopsOrderEntryEvenWithApprovalNeeded()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Exhibitor.ExhibitorCategory = "102,103";
        s.Row.ApprovalNeeded = true;
        s.Apply();
        Assert.Equal("REVIEW", s.Row.Outcome);
        Assert.Empty(s.Transport.Mutations);
        Assert.Empty(s.Transport.Emails);
    }
}
