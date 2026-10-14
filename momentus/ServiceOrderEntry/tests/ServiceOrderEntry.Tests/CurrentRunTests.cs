using System.Reflection;
using Ungerboeck.Api.Sdk;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class CurrentRunTests
{
    [Fact]
    public void OldMalformedHistoryAndOtherRunRecordsAreNotRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "service-order-fresh-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "orders"));
            var old = Path.Combine(root, "orders", "old.json");
            File.WriteAllText(old, "old test data is not valid journal JSON");
            var first = CurrentRunState.CreateFolder(root, Guid.NewGuid().ToString("N"));
            File.WriteAllText(Path.Combine(first, "old-receipt.json"), "old receipt");
            var second = CurrentRunState.CreateFolder(root, Guid.NewGuid().ToString("N"));
            Assert.NotEqual(first, second);
            Assert.Empty(new FileJournalStore(second).Load());
            Assert.Equal("old test data is not valid journal JSON", File.ReadAllText(old));
            Assert.Equal("old receipt", File.ReadAllText(Path.Combine(first, "old-receipt.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ARunFolderCannotBeReusedAsAFreshRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "service-order-fresh-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = Guid.NewGuid().ToString("N");
            CurrentRunState.CreateFolder(root, id);
            Assert.Throws<InvalidOperationException>(() => CurrentRunState.CreateFolder(root, id));
            Assert.Throws<InvalidOperationException>(() => CurrentRunState.CreateFolder(root, "../old"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void FreshRunUsesResetCurrentOrderAndIgnoresOldOptionsUnknownStagesAndEmails()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Apply();
        var old = s.Store.Load().Single();
        old.ActivateOrder = false; old.ActivateExhibitor = false; old.Complete = false;
        old.Stages[0].Status = StageStatus.Unknown;
        s.Store.Save(old);
        var oldPath = Path.Combine(s.Options.StateFolder, "orders", old.Identity.Key + ".json");
        var oldContents = File.ReadAllText(oldPath);
        s.Transport.Order.OrderStatus = "PC"; s.Transport.Order.OrderAccountRep = "RESET";
        s.Transport.Order.Category = 0; s.Transport.Order.BoothNumber = "";
        s.Transport.Exhibitor.ExhibitorStatus = 35; s.Transport.Exhibitor.ExhibitorCategory = "102";
        s.Transport.BillingInstructions = s.Row.RequestedBilling with { UseRequestedAddress = "ECA" };
        var currentFolder = CurrentRunState.CreateFolder(s.Options.StateFolder, Guid.NewGuid().ToString("N"));
        var currentStore = new FileJournalStore(currentFolder);
        var client = (ApiClient)typeof(MomentusGateway).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Gateway)!;
        var gateway = new MomentusGateway(s.Options with { StateFolder = currentFolder }, client, _ => { });
        var runner = new Runner(s.Options with { ExhibitorId = 2, EventId = 1, OrderNumber = 3 });
        runner.UseJournalStore(currentStore);
        Assert.Equal(0, runner.ProcessCandidates(gateway, [new(s.Transport.Order, s.Transport.Exhibitor)], [], [new("Turnkey", 28, ["Turnkey"])]));
        Assert.Equal("A", s.Transport.Order.OrderStatus);
        Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus);
        Assert.Equal("REP", s.Transport.Order.OrderAccountRep);
        Assert.Equal(28, s.Transport.Order.Category);
        Assert.Equal("101", s.Transport.Order.BoothNumber);
        Assert.StartsWith("WAIT FOR SALES APPROVAL", s.Transport.Emails.Last().EmailSubject);
        Assert.Equal(2, s.Transport.Emails.Count); // One final notification per actual fresh workflow.
        Assert.Single(s.Transport.Copies); Assert.Single(s.Transport.Notes);
        Assert.Equal(oldContents, File.ReadAllText(oldPath));
        Assert.True(currentStore.Load().Single().Complete);
    }

    [Fact]
    public void MatchingLegacyPaymentTermsAreReusedFromCurrentDataWithoutOldOwnershipProof()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Notes.Add(new() { SequenceNumber = 99, Type = "OH", Class = "SON", OrderNumber = 3,
            Title = "Payment Schedule", PlainText = s.Row.PaymentScheduleText });
        s.Row.ManagedNoteSequence = 99;
        s.Row.ManagedNoteContentHash = ManagedNoteRules.ContentHash(s.Row.PaymentScheduleText);
        s.Apply();
        Assert.True(s.Row.Outcome == "SUCCESS", s.Row.UpdateMessage);
        Assert.Single(s.Transport.Notes);
        Assert.DoesNotContain("Add payment schedule note", s.Transport.Mutations);
        Assert.DoesNotContain("Update payment schedule note", s.Transport.Mutations);
        Assert.Equal("Payment Schedule", s.Transport.Notes.Single().Title);
    }

    [Fact]
    public void ExplicitOrderScopeOnlyIncludesThatOrder()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.OtherOrders.Add(new() { OrganizationCode = "10", OrderNumber = 4, Event = 1, Exhibitor = 2, OrderStatus = "PC" });
        var work = Runner.DiscoverWork(s.Gateway, [], s.Options with { ExhibitorId = 2, EventId = 1, OrderNumber = 3 });
        Assert.Equal(3, Assert.Single(work.Candidates).Order.OrderNumber);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void ExplicitOrderForADifferentExhibitorIsRejected()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Order.Exhibitor = 999;
        Assert.Throws<RecoveryReviewException>(() => Runner.DiscoverWork(s.Gateway, [], s.Options with { ExhibitorId = 2, EventId = 1, OrderNumber = 3 }));
        Assert.Empty(s.Transport.Mutations);
    }
}
