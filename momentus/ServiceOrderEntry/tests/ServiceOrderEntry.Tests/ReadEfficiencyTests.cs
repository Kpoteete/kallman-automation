using Xunit;
using Xunit.Abstractions;

namespace ServiceOrderEntry.Tests;

public sealed class ReadEfficiencyTests(ITestOutputHelper output)
{
    [Fact]
    public void CompletedOrderAvoidsRedundantDownloadsButStillFinishesEveryStage()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Apply();
        Assert.True(s.Store.Load().Single().Complete, s.Row.UpdateMessage);
        var downloads = s.Transport.Requests.Count(x => x.EndsWith("/Download"));
        output.WriteLine($"API requests: {s.Transport.Requests.Count}; PDF downloads: {downloads}");
        Assert.True(downloads < 41, "The original single-contract workflow downloaded PDFs 41 times.");
        Assert.True(s.Transport.Requests.Count < 258, "Before account read reuse the single-contract workflow made 258 API requests.");
        Assert.Single(s.Transport.Emails);
        Assert.All(s.Store.Load().Single().Stages, stage => Assert.Equal(StageStatus.Verified, stage.Status));
    }

    [Fact]
    public void EachHandoffPassDownloadsFreshBytesAndRejectsChangesWithoutMetadataChanges()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Apply();
        var evidence = s.Store.Load().Single();
        s.Gateway.Journal = new ProcessingJournal(s.Store, evidence, Guid.NewGuid().ToString("N"));
        s.Transport.Requests.Clear();
        _ = HandoffRules.Verify(s.Gateway, evidence.Plan, s.Gateway.Journal);
        Assert.Equal(2, s.Transport.Requests.Count(x => x.EndsWith("/Download")));
        s.Transport.DocumentData["C/11"] = RetryBoundaryTests.MakePdf(RetryBoundaryTests.TestSchedule + "\nChanged content");
        Assert.Throws<RecoveryReviewException>(() => HandoffRules.Verify(s.Gateway, evidence.Plan, s.Gateway.Journal));
        Assert.Equal(3, s.Transport.Requests.Count(x => x.EndsWith("/Download")));
        // Exceptional verification must also dispose its read snapshot.
        Assert.Throws<RecoveryReviewException>(() => s.Gateway.VerifiedDocumentData(evidence.Plan.Contracts!.Sources[0].Document));
        Assert.Equal(4, s.Transport.Requests.Count(x => x.EndsWith("/Download")));
    }

    [Fact]
    public void ReadSnapshotCannotBeUsedToDispatchAWrite()
    {
        using var s = new RetryBoundaryTests.Scenario();
        using var snapshot = s.Gateway.BeginDocumentReadSnapshot();
        Assert.Throws<InvalidOperationException>(() => s.Gateway.UpdateOrder(s.Transport.Order));
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void ContractChangedAfterActivationStillBlocksTheFinalEmail()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var changed = false;
        s.Transport.BeforeRequest = _ =>
        {
            if (changed || s.Gateway.Journal?.Evidence.Stages.Any(x => x.Operation == "Activate order" && x.Status == StageStatus.Verified) != true) return;
            changed = true;
            s.Transport.DocumentData["C/11"] = RetryBoundaryTests.MakePdf(RetryBoundaryTests.TestSchedule + "\nChanged after activation");
        };
        s.Apply();
        Assert.True(changed);
        Assert.Equal("A", s.Transport.Order.OrderStatus);
        Assert.Equal("REVIEW", s.Row.Outcome);
        Assert.Empty(s.Transport.Emails);
        Assert.False(s.Store.Load().Single().Complete);
    }

    [Fact]
    public void HandoffReadsEachAccountOnceAndNextPassRejectsChangedBilling()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Apply();
        var evidence = s.Store.Load().Single();
        s.Gateway.Journal = new ProcessingJournal(s.Store, evidence, "read-efficiency-test");
        s.Transport.Requests.Clear();
        _ = HandoffRules.Verify(s.Gateway, evidence.Plan, s.Gateway.Journal);
        var accountReads = s.Transport.Requests.Where(x => x.StartsWith("GET Accounts/10/")).ToList();
        Assert.Equal(3, accountReads.Count);
        Assert.Equal(3, accountReads.Distinct().Count());
        s.Transport.AccountReadOverride = account =>
        {
            if (account.AccountCode == "REP") account.Email = "changed@example.invalid";
            return account;
        };
        Assert.Throws<RecoveryReviewException>(() => HandoffRules.Verify(s.Gateway, evidence.Plan, s.Gateway.Journal));
        s.Transport.AccountReadOverride = null;
        // Exceptional disposal must not leave an old account value available outside the pass.
        Assert.Equal("changed@example.invalid", s.Gateway.GetAccount("REP").Email);
    }

    [Fact]
    public void HandoffAccountValuesCannotBeMutatedByCallersOrUsedForWrites()
    {
        using var s = new RetryBoundaryTests.Scenario();
        using (s.Gateway.BeginHandoffReadSnapshot())
        {
            var first = s.Gateway.GetAccountModel("CONTACT");
            first.Email = "local-change@example.invalid";
            Assert.Equal("test@example.invalid", s.Gateway.GetAccountModel(" CONTACT ").Email);
            Assert.Single(s.Transport.Requests);
            Assert.Throws<InvalidOperationException>(() => s.Gateway.UpdateOrder(s.Transport.Order));
        }
        Assert.Empty(s.Transport.Mutations);
        _ = s.Gateway.GetAccount("CONTACT");
        Assert.Equal(2, s.Transport.Requests.Count(x => x == "GET Accounts/10/CONTACT"));
    }

    [Fact]
    public void BillingChangedAfterHandoffIsRereadBeforeActivation()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var changed = false;
        s.Transport.BeforeRequest = request =>
        {
            if (changed || s.Gateway.Journal?.Evidence.Handoff is null ||
                s.Gateway.Journal.Evidence.Stages.Any(x => x.Operation == "Activate order") ||
                !request.RequestUri!.AbsolutePath.EndsWith("/Accounts/10/ACCOUNT")) return;
            changed = true;
            s.Transport.BillingInstructions = s.Transport.BillingInstructions with { CompanyName = "Changed after handoff" };
        };
        s.Apply();
        Assert.True(changed);
        Assert.Equal("REVIEW", s.Row.Outcome);
        Assert.DoesNotContain("Activate order", s.Transport.Mutations);
        Assert.Empty(s.Transport.Emails);
    }
}
