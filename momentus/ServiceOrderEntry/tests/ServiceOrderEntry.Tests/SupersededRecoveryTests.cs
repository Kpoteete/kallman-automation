using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class SupersededRecoveryTests
{
    static OrderEvidence OldVave() => new() { Identity = new("https://kallman.ungerboeck.com/prod", "10", 6246, 185082, 40414),
        Plan = new() { EventId = 6246, ExhibitorId = 185082, OrderNumber = 40414, Outcome = "REVIEW" } };
    static ServiceOrdersModel NewVave() => new() { OrganizationCode = "10", OrderNumber = 40415, Event = 6246, Exhibitor = 185082, OrderStatus = "A" };

    [Fact] public void ExplicitVaveExceptionRetainsMissingUndispatchedHistory()
    {
        var record = OldVave();
        var before = Newtonsoft.Json.JsonConvert.SerializeObject(record);
        Assert.True(SupersededRecoveryRules.Excludes(record, [NewVave()]));
        Assert.Equal(before, Newtonsoft.Json.JsonConvert.SerializeObject(record));
        Assert.Equal(0, FailureRules.ExitCode([new() { Outcome = "SKIPPED", ValidationStatus = "SKIPPED" }]));
    }

    [Fact] public void NewVaveOrderFinishesBothActivationsWhileOldPlaceholderIsPreserved()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var old = OldVave(); s.Store.Save(old);
        s.Transport.Order.OrderNumber = 40415; s.Transport.Order.Event = 6246; s.Transport.Order.Exhibitor = 185082;
        s.Transport.Exhibitor.ExhibitorID = 185082; s.Transport.Exhibitor.Event = 6246;
        s.Transport.Contracts[0].Exhibitor = 185082;
        var options = s.Options with { BaseUrl = old.Identity.Endpoint, ExhibitorId = 185082, EventId = 6246 };
        s.Store.Save(new() { Identity = old.Identity with { Order = 40415 }, Plan = new() { EventId = 6246, ExhibitorId = 185082, OrderNumber = 40415 } });
        var oldPath = Path.Combine(options.StateFolder, "orders", old.Identity.Key + ".json");
        var original = File.ReadAllText(oldPath);
        var work = Runner.DiscoverWork(s.Gateway, s.Store.Load(), options);
        Assert.Equal("SKIPPED", Assert.Single(work.Failures).Outcome);
        Assert.Equal(40415, Assert.Single(work.Candidates).Order.OrderNumber);
        var runner = new Runner(options); runner.UseJournalStore(s.Store);
        var exit = runner.ProcessDiscovery(s.Gateway, work, [new("REP", "REP", "REP")], [new("Turnkey", 28, ["Turnkey"])]);
        Assert.True(exit == 0, s.Store.Load().Single(x => x.Identity.Order == 40415).Plan.UpdateMessage);
        Assert.Equal("A", s.Transport.Order.OrderStatus); Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus);
        Assert.Single(s.Transport.Emails); Assert.Equal("Send ready email", s.Transport.Mutations.Last());
        Assert.Equal(original, File.ReadAllText(oldPath));
        Assert.DoesNotContain("GET ServiceOrders/10/40414", s.Transport.Requests);
    }

    [Theory] [InlineData("endpoint")] [InlineData("organization")] [InlineData("exhibitor")]
    [InlineData("event")] [InlineData("order")] [InlineData("dispatched")]
    [InlineData("old still exists")] [InlineData("replacement missing")] [InlineData("replacement wrong scope")]
    public void ExceptionCannotBypassOtherHistoryOrUncertainWrites(string change)
    {
        var old = OldVave(); var current = NewVave(); var orders = new List<ServiceOrdersModel> { current };
        switch (change)
        {
            case "endpoint": old.Identity = old.Identity with { Endpoint = "https://other.invalid/prod" }; break;
            case "organization": old.Identity = old.Identity with { Organization = "20" }; break;
            case "exhibitor": old.Identity = old.Identity with { Exhibitor = 1 }; break;
            case "event": old.Identity = old.Identity with { Event = 1 }; break;
            case "order": old.Identity = old.Identity with { Order = 40413 }; break;
            case "dispatched": old.Stages.Add(new() { Status = StageStatus.Unknown }); break;
            case "old still exists": orders.Add(new() { OrderNumber = 40414 }); break;
            case "replacement missing": orders.Clear(); break;
            case "replacement wrong scope": current.Exhibitor = 1; break;
        }
        Assert.False(SupersededRecoveryRules.Excludes(old, orders));
    }
}
