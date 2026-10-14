using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class RecoveryDiscoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableRecoveryTargetIsReportedWhileOtherOrdersContinue(bool missingExhibitor)
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Store.Save(s.Gateway.Journal!.Evidence);
        var unavailable = new OrderEvidence
        {
            Identity = new(s.Options.BaseUrl, "10", 1, missingExhibitor ? 99 : 2, 4),
            Plan = new() { EventId = 1, ExhibitorId = missingExhibitor ? 99 : 2, OrderNumber = 4 }
        };
        s.Store.Save(unavailable);
        if (missingExhibitor) s.Transport.OtherOrders.Add(new ServiceOrdersModel { OrganizationCode = "10", OrderNumber = 4, Event = 1, Exhibitor = 99 });
        var failedPath = missingExhibitor ? "/Exhibitors/10/99" : "/ServiceOrders/10/4";
        s.Transport.BeforeRequest = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(failedPath))
                throw new AggregateException(new InvalidOperationException("Recovery entry not found."));
        };
        var before = Directory.GetFiles(Path.Combine(s.Options.StateFolder, "orders"), "*.json").ToDictionary(x => x, File.ReadAllText);
        var work = Runner.DiscoverWork(s.Gateway, s.Store.Load(), s.Options);
        Assert.Equal(3, Assert.Single(work.Candidates).Order.OrderNumber);
        var failure = Assert.Single(work.Failures);
        Assert.Equal(4, failure.OrderNumber);
        Assert.Equal(unavailable.Identity.Exhibitor, failure.ExhibitorId);
        Assert.Equal("REVIEW", failure.Outcome);
        Assert.Equal("RECOVERY REVIEW", failure.ServiceOrderUpdateStatus);
        Assert.Contains("Recovery entry not found", failure.UpdateMessage);

        var preview = s.Options with { Apply = false, Confirm = false, ActivateOrder = false, ActivateExhibitor = false };
        var runner = new Runner(preview); runner.UseJournalStore(s.Store);
        Assert.Equal(2, runner.ProcessDiscovery(s.Gateway, work, [new("REP", "REP", "REP")], [new("Turnkey", 28, ["Turnkey"])]));
        var csv = File.ReadAllText(Assert.Single(Directory.GetFiles(preview.RunFolder, "*.csv")));
        Assert.Contains("Recovery entry not found", csv);
        Assert.Contains("RECOVERY REVIEW", csv);
        Assert.Contains("WOULD UPDATE", csv); // The valid order was evaluated as well.
        Assert.Empty(s.Transport.Mutations);
        Assert.All(before, entry => Assert.Equal(entry.Value, File.ReadAllText(entry.Key)));
    }

    [Fact]
    public void MissingPeerStillBlocksSharedExhibitorActivationDuringApply()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.FaultEnabled = false;
        s.Store.Save(s.Gateway.Journal!.Evidence);
        var missing = new OrderEvidence
        {
            Identity = new(s.Options.BaseUrl, "10", 1, 2, 4),
            Plan = new() { EventId = 1, ExhibitorId = 2, OrderNumber = 4 }
        };
        s.Store.Save(missing);
        var missingPath = Path.Combine(s.Options.StateFolder, "orders", missing.Identity.Key + ".json");
        var before = File.ReadAllText(missingPath);
        s.Transport.FailReadPath = "ServiceOrders/10/4";
        var runner = new Runner(s.Options); runner.UseJournalStore(s.Store);
        Assert.Equal(2, runner.ProcessDiscovery(s.Gateway, Runner.DiscoverWork(s.Gateway, s.Store.Load(), s.Options),
            [new("REP", "REP", "REP")], [new("Turnkey", 28, ["Turnkey"])]));
        Assert.Equal("A", s.Transport.Order.OrderStatus);
        Assert.Equal(35, s.Transport.Exhibitor.ExhibitorStatus);
        Assert.DoesNotContain("Activate exhibitor", s.Transport.Mutations);
        Assert.True(s.Store.Load().Single(x => x.Identity.Order == 3).ExhibitorActivationPending);
        Assert.Equal(before, File.ReadAllText(missingPath));
    }

    [Fact]
    public void UnavailableOrderWithUncertainWriteKeepsUnknownOutcomeAndJournal()
    {
        using var s = new RetryBoundaryTests.Scenario("Update service order");
        s.Apply();
        Assert.Equal(StageStatus.Unknown, Assert.Single(s.Store.Load().Single().Stages).Status);
        var mutationCount = s.Transport.Mutations.Count;
        s.Transport.FailReadPath = "ServiceOrders/10/3";
        var work = Runner.DiscoverWork(s.Gateway, s.Store.Load(), s.Options);
        Assert.Empty(work.Candidates);
        Assert.Equal("UNKNOWN", Assert.Single(work.Failures).Outcome);
        var runner = new Runner(s.Options); runner.UseJournalStore(s.Store);
        Assert.Equal(3, runner.ProcessDiscovery(s.Gateway, work, [], []));
        Assert.Contains("RECOVERY REVIEW", File.ReadAllText(Assert.Single(Directory.GetFiles(s.Options.RunFolder, "*.csv"))));
        Assert.Equal(mutationCount, s.Transport.Mutations.Count);
        Assert.Equal(StageStatus.Unknown, Assert.Single(s.Store.Load().Single().Stages).Status);
    }
}
