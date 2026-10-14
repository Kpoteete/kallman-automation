using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class CompletionOrderTests
{
    [Fact] public void FinanceEmailDispatchSeesBothStatusesActiveAndIsLastMutation()
    {
        using var s = new RetryBoundaryTests.Scenario(); var checkedEmail = false;
        s.Transport.BeforeRequest = request =>
        {
            if (request.Method == HttpMethod.Get || !request.RequestUri!.AbsolutePath.EndsWith("/Emails/Send")) return;
            checkedEmail = true;
            Assert.Equal("A", s.Transport.Order.OrderStatus); Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus);
        };
        s.Apply(); Assert.True(s.Row.Outcome == "SUCCESS", s.Row.UpdateMessage); Assert.True(checkedEmail);
        Assert.Equal(new[] { "Activate order", "Activate exhibitor", "Send ready email" }, s.Transport.Mutations.TakeLast(3));
    }

    [Fact] public void CrashAtCompletedOrderCheckpointRecoversActivationThenEmail()
    {
        using var s = new RetryBoundaryTests.Scenario(); var durable = s.Store;
        s.Store = new CompletionCheckpointFault(durable);
        Assert.Throws<JournalStorageException>(() => s.Apply());
        var saved = durable.Load().Single(); Assert.True(saved.OrderComplete); Assert.False(saved.Complete);
        Assert.Empty(s.Transport.Emails); Assert.Equal("A", s.Transport.Order.OrderStatus);
        s.Store = durable;
        Assert.Equal("SUCCESS", s.Apply(restart: true).Outcome);
        Assert.Equal(new[] { "Activate order", "Activate exhibitor", "Send ready email" }, s.Transport.Mutations.TakeLast(3));
        Assert.Single(s.Transport.Mutations, x => x == "Activate order"); Assert.True(durable.Load().Single().Complete);
    }

    [Fact] public void PreviouslySentEmailIsNotRepeatedWhenPendingExhibitorActivationFinishes()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.OtherOrders.Add(new ServiceOrdersModel { OrganizationCode = "10", OrderNumber = 4, Event = 1, Exhibitor = 2, OrderStatus = "PC" });
        s.Apply(); Assert.Single(s.Transport.Emails); Assert.True(s.Store.Load().Single().ExhibitorActivationPending);
        s.Transport.OtherOrders.Clear(); var count = s.Transport.Mutations.Count;
        Assert.Equal("SUCCESS", s.Apply(restart: true).Outcome);
        Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus); Assert.Single(s.Transport.Emails);
        Assert.Equal(count + 1, s.Transport.Mutations.Count); Assert.Equal("Activate exhibitor", s.Transport.Mutations.Last());
    }

    [Fact] public void MomentusAutomaticExhibitorActivationIsVerifiedWithoutRedundantWrite()
    {
        using var s = new RetryBoundaryTests.Scenario(); var autoActivated = false;
        s.Transport.BeforeRequest = _ =>
        {
            if (autoActivated || s.Gateway.Journal?.Evidence.Stages.Any(x => x.Operation == "Activate order" && x.Status == StageStatus.Verified) != true) return;
            autoActivated = true; s.Transport.Exhibitor.ExhibitorStatus = 2;
        };
        s.Apply(); Assert.True(autoActivated); Assert.True(s.Row.Outcome == "SUCCESS", s.Row.UpdateMessage);
        Assert.False(s.Row.ExhibitorActivationPending); Assert.True(s.Store.Load().Single().Complete);
        Assert.DoesNotContain("Activate exhibitor", s.Transport.Mutations); Assert.Single(s.Transport.Emails);
        Assert.Equal(new[] { "Activate order", "Send ready email" }, s.Transport.Mutations.TakeLast(2));
        var count = s.Transport.Mutations.Count;
        Assert.Equal("SUCCESS", s.Apply(restart: true).Outcome); Assert.Equal(count, s.Transport.Mutations.Count);
    }

    [Fact] public void LostOrderActivationResponseRecoversItsAutomaticExhibitorActivation()
    {
        using var s = new RetryBoundaryTests.Scenario("Activate order");
        s.Apply(); Assert.Equal("UNKNOWN", s.Row.Outcome); Assert.Empty(s.Transport.Emails);
        s.Transport.Exhibitor.ExhibitorStatus = 2; // Momentus side effect of the accepted header activation.
        Assert.Equal("SUCCESS", s.Apply(restart: true).Outcome);
        Assert.False(s.Store.Load().Single().ExhibitorActivationPending);
        Assert.Single(s.Transport.Mutations, x => x == "Activate order");
        Assert.DoesNotContain("Activate exhibitor", s.Transport.Mutations); Assert.Single(s.Transport.Emails);
    }

    [Fact] public void AlreadyActiveExhibitorCannotBypassAnotherPendingOrder()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.OtherOrders.Add(new() { OrganizationCode = "10", OrderNumber = 4, Event = 1, Exhibitor = 2, OrderStatus = "PC" });
        s.Transport.BeforeRequest = _ =>
        {
            if (s.Gateway.Journal?.Evidence.Stages.Any(x => x.Operation == "Activate order" && x.Status == StageStatus.Verified) == true)
                s.Transport.Exhibitor.ExhibitorStatus = 2;
        };
        s.Apply(); Assert.True(s.Row.ExhibitorActivationPending);
        Assert.DoesNotContain("Activate exhibitor", s.Transport.Mutations); Assert.Equal(2, Runner.ApplyExitCode([s.Row]));
    }

    sealed class CompletionCheckpointFault(IJournalStore inner) : IJournalStore
    {
        bool fired;
        public IReadOnlyList<OrderEvidence> Load() => inner.Load();
        public void Save(OrderEvidence evidence)
        {
            inner.Save(evidence);
            if (fired || !evidence.OrderComplete || evidence.Stages.Any(x => x.Operation == "Send ready email")) return;
            fired = true; throw new JournalStorageException("Simulated crash before final activation/email", new IOException("offline fault"));
        }
    }
}
