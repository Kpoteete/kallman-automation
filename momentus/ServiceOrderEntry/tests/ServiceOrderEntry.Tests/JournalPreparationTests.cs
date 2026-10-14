using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class JournalPreparationTests
{
    private sealed class CountingStore(IJournalStore inner) : IJournalStore
    {
        public int Loads { get; set; }
        public int Saves { get; private set; }
        public int? FailSave { get; set; }
        public IReadOnlyList<OrderEvidence> Load() { Loads++; return inner.Load(); }
        public void Save(OrderEvidence evidence)
        {
            Saves++;
            if (Saves == FailSave) throw new JournalStorageException("Offline storage fault", new IOException());
            inner.Save(evidence);
        }
    }
    private static Candidate Candidate(int number) => new(new ServiceOrdersModel { OrderNumber = number },
        new ExhibitorsModel { ExhibitorID = 2, Event = 1 });

    [Fact]
    public void PreparationLoadsOnceAndDurablyRetainsAllOrdersBeyondTheAttemptCap()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var store = new CountingStore(s.Store);
        var runner = new Runner(s.Options with { MaxUpdates = 1 }); runner.UseJournalStore(store);
        store.Loads = 0;
        var candidates = Enumerable.Range(3, 100).Select(Candidate).ToArray();
        runner.PrepareCandidateJournals(candidates.Concat(candidates).ToArray());
        Assert.Equal(1, store.Loads);
        Assert.Equal(100, store.Saves);
        var retained = s.Store.Load();
        Assert.Equal(100, retained.Count);
        Assert.All(retained, x => { Assert.Equal("PENDING", x.Plan.Outcome); Assert.Empty(x.Stages); Assert.True(x.ActivateOrder); Assert.True(x.SendEmail); });
        Assert.Empty(s.Transport.Requests);
    }

    [Fact]
    public void ExistingUncertainEvidenceIsPreservedExactly()
    {
        using var s = new RetryBoundaryTests.Scenario("Update service order");
        s.Apply();
        var evidence = s.Store.Load().Single();
        Assert.Equal(StageStatus.Unknown, Assert.Single(evidence.Stages).Status);
        var path = Path.Combine(s.Options.StateFolder, "orders", evidence.Identity.Key + ".json");
        var before = File.ReadAllText(path);
        var store = new CountingStore(s.Store);
        var runner = new Runner(s.Options); runner.UseJournalStore(store);
        store.Loads = 0;
        runner.PrepareCandidateJournals([Candidate(3), Candidate(4), Candidate(3)]);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(1, store.Loads);
        Assert.Equal(1, store.Saves);
        Assert.Equal(2, s.Store.Load().Count);
    }

    [Fact]
    public void FailedSaveStopsPreparationAndNextAttemptReadsDurableEvidence()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var store = new CountingStore(s.Store) { FailSave = 2 };
        var runner = new Runner(s.Options); runner.UseJournalStore(store);
        Assert.Throws<JournalStorageException>(() => runner.PrepareCandidateJournals([Candidate(3), Candidate(4), Candidate(5)]));
        Assert.Equal(3, Assert.Single(s.Store.Load()).Identity.Order);
        store.FailSave = null;
        runner.PrepareCandidateJournals([Candidate(3), Candidate(4), Candidate(5)]);
        Assert.Equal(new[] { 3, 4, 5 }, s.Store.Load().Select(x => x.Identity.Order).Order());
        Assert.Equal(4, store.Saves); // One successful save, one failed save, then only the two missing records.
        Assert.Empty(s.Transport.Requests);
    }
}
