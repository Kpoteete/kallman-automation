using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class JournalRecoveryTests
{
    public static IEnumerable<object[]> Stages()
    {
        foreach (var stage in new[] { "Create organization account", "Add BTO relationship", "Create contact",
            "Update service order", "Copy contract document", "Add payment schedule note", "Update payment schedule note", "Update exhibitor categories",
            "Send ready email", "Activate order", "Activate exhibitor" }) yield return [stage];
    }
    private static bool IsStage(StageEvidence stage, string name) => stage.Operation == name ||
        stage.Operation == "Add relationship" && stage.Target.Contains($"type={(name == "Add BTO relationship" ? "BTO" : "CTA")}") && name.Contains("relationship");

    [Theory]
    [MemberData(nameof(Stages))]
    public void CrashAfterReturnedResultRestartsWithoutRepeatingAcceptedWrite(string stage)
    {
        using var scenario = new RetryBoundaryTests.Scenario(stage);
        scenario.Transport.FaultEnabled = false;
        var durable = scenario.Store;
        scenario.Store = new FaultStore(durable, evidence => evidence.Stages.Any(x => IsStage(x, stage) && x.Status == StageStatus.Succeeded), afterSave: true);

        Assert.Throws<JournalStorageException>(() => scenario.Apply());
        Assert.Equal(stage, scenario.Transport.Mutations.Last());
        Assert.Equal(StageStatus.Succeeded, durable.Load().Single().Stages.Single(x => IsStage(x, stage)).Status);

        scenario.Store = new FileJournalStore(scenario.Options.StateFolder); // New process: only files and remote state survive.
        var row = scenario.Apply(restart: true);

        Assert.True(row.ServiceOrderUpdateStatus == "COMPLETED", row.UpdateMessage);
        Assert.Single(scenario.Transport.Mutations, x => x == stage);
        var evidence = scenario.Store.Load().Single();
        Assert.True(evidence.Complete);
        Assert.True(evidence.Stages.Single(x => IsStage(x, stage)).Reconciled);
        Assert.All(evidence.Stages, x => Assert.Equal(StageStatus.Verified, x.Status));
        if (stage == "Create organization account") Assert.Equal("NEWACCOUNT", row.FinalBillToAccount);
        if (stage == "Create contact") Assert.Equal("NEWCONTACT", row.FinalBillToContact);
        if (stage == "Copy contract document") Assert.Contains("22", evidence.Stages.Single(x => IsStage(x, stage)).Result);
    }

    [Theory]
    [MemberData(nameof(Stages))]
    public void CrashAfterPersistedVerificationReusesStage(string stage)
    {
        using var scenario = new RetryBoundaryTests.Scenario(stage);
        scenario.Transport.FaultEnabled = false;
        var durable = scenario.Store;
        scenario.Store = new FaultStore(durable, e => e.Stages.Any(x => IsStage(x, stage) && x.Status == StageStatus.Verified), afterSave: true);
        Assert.Throws<JournalStorageException>(() => scenario.Apply());
        Assert.Equal(stage, scenario.Transport.Mutations.Last());
        scenario.Store = new FileJournalStore(scenario.Options.StateFolder);
        var row = scenario.Apply(restart: true);
        Assert.True(row.ServiceOrderUpdateStatus == "COMPLETED", row.UpdateMessage);
        Assert.Single(scenario.Transport.Mutations, x => x == stage);
    }

    [Theory]
    [MemberData(nameof(Stages))]
    public void CrashBeforeResultPersistsLeavesDispatchUnknownAndNeverBlindlyRepeats(string stage)
    {
        using var scenario = new RetryBoundaryTests.Scenario(stage);
        scenario.Transport.FaultEnabled = false;
        var durable = scenario.Store;
        scenario.Store = new FaultStore(durable, e => e.Stages.Any(x => IsStage(x, stage) && x.Status == StageStatus.Succeeded), afterSave: false);
        Assert.Throws<JournalStorageException>(() => scenario.Apply());
        Assert.Equal(StageStatus.Dispatching, durable.Load().Single().Stages.Single(x => IsStage(x, stage)).Status);
        scenario.Store = new FileJournalStore(scenario.Options.StateFolder);
        var reads = scenario.Transport.Requests.Count;
        var row = scenario.Apply(restart: true);
        Assert.Single(scenario.Transport.Mutations, x => x == stage);
        Assert.True(scenario.Transport.Requests.Count > reads); // Current external state was consulted.
        if (stage is "Create organization account" or "Create contact" or "Send ready email")
        {
            Assert.Equal("RECOVERY REVIEW", row.ServiceOrderUpdateStatus);
            Assert.Equal(stage, scenario.Transport.Mutations.Last());
            Assert.Equal(StageStatus.Unknown, scenario.Store.Load().Single().Stages.Single(x => IsStage(x, stage)).Status);
        }
        else Assert.True(row.ServiceOrderUpdateStatus == "COMPLETED", row.UpdateMessage);
    }

    [Theory]
    [MemberData(nameof(Stages))]
    public void PersistedTimeoutIsReconciledOrReviewedWithoutRedispatch(string stage)
    {
        using var scenario = new RetryBoundaryTests.Scenario(stage);
        scenario.Apply();
        Assert.Equal("UNKNOWN WRITE OUTCOME", scenario.Row.ServiceOrderUpdateStatus);
        Assert.Equal(StageStatus.Unknown, scenario.Store.Load().Single().Stages.Single(x => IsStage(x, stage)).Status);
        scenario.Transport.FaultEnabled = false;
        var row = scenario.Apply(restart: true);
        Assert.Single(scenario.Transport.Mutations, x => x == stage);
        Assert.True(row.ServiceOrderUpdateStatus is "COMPLETED" or "RECOVERY REVIEW", row.UpdateMessage);
        if (row.ServiceOrderUpdateStatus == "RECOVERY REVIEW") Assert.Equal(stage, scenario.Transport.Mutations.Last());
    }

    [Theory]
    [MemberData(nameof(Stages))]
    public void JournalFailureBeforeDispatchPreventsThatMutation(string stage)
    {
        using var scenario = new RetryBoundaryTests.Scenario(stage);
        scenario.Transport.FaultEnabled = false;
        scenario.Store = new FaultStore(scenario.Store, e => e.Stages.Any(x => IsStage(x, stage) && x.Status == StageStatus.Dispatching), afterSave: false);
        Assert.Throws<JournalStorageException>(() => scenario.Apply());
        Assert.DoesNotContain(stage, scenario.Transport.Mutations);
        Assert.Equal("FAILED", scenario.Row.ServiceOrderUpdateStatus);
        Assert.Contains("persistence", scenario.Row.UpdateMessage);
        scenario.Store = new FileJournalStore(scenario.Options.StateFolder);
        Assert.Equal(StageStatus.Planned, scenario.Store.Load().Single().Stages.Single(x => IsStage(x, stage)).Status);
        var row = scenario.Apply(restart: true);
        Assert.True(row.ServiceOrderUpdateStatus == "COMPLETED", row.UpdateMessage);
        Assert.Single(scenario.Transport.Mutations, x => x == stage);
    }

    [Fact]
    public void UnresolvedTimeoutRemainsUnknownAcrossMultipleRestarts()
    {
        using var scenario = new RetryBoundaryTests.Scenario("Update service order");
        scenario.Transport.ApplyEffect = false;
        scenario.Apply();
        scenario.Transport.FaultEnabled = false;
        for (var i = 0; i < 2; i++)
        {
            scenario.Store = new FileJournalStore(scenario.Options.StateFolder);
            var row = scenario.Apply(restart: true);
            Assert.Equal("RECOVERY REVIEW", row.ServiceOrderUpdateStatus);
            Assert.Equal("REVIEW", row.ValidationStatus);
            Assert.NotEqual(0, Runner.ApplyExitCode([row]));
            Assert.Equal(StageStatus.Unknown, scenario.Store.Load().Single().Stages.Single().Status);
        }
        Assert.Equal(new[] { "Update service order" }, scenario.Transport.Mutations);
    }

    [Fact]
    public void ConfirmedRejectionCanProceedAfterCurrentValidationOnRestart()
    {
        using var scenario = new RetryBoundaryTests.Scenario("Update service order");
        scenario.Transport.WriteFailureStatus = 400;
        scenario.Apply();
        Assert.Equal(StageStatus.Failed, scenario.Store.Load().Single().Stages.Single().Status);
        scenario.Transport.WriteFailureStatus = null;
        scenario.Transport.FaultEnabled = false;
        var row = scenario.Apply(restart: true);
        Assert.True(row.ServiceOrderUpdateStatus == "COMPLETED", row.UpdateMessage);
        Assert.Equal(2, scenario.Store.Load().Single().Stages.Single(x => x.Operation == "Update service order").Attempts);
    }

    [Fact]
    public void ChangedEligibilityStillDiscoveredByExactJournalIds()
    {
        using var scenario = new RetryBoundaryTests.Scenario();
        var durable = scenario.Store;
        scenario.Store = new FaultStore(durable, e => e.Stages.Any(x => x.Operation == "Activate exhibitor" && x.Status == StageStatus.Succeeded), afterSave: true);
        Assert.Throws<JournalStorageException>(() => scenario.Apply());
        Assert.Equal("A", scenario.Transport.Order.OrderStatus);
        Assert.Equal(2, scenario.Transport.Exhibitor.ExhibitorStatus);
        Assert.Empty(scenario.Gateway.FindCandidates(new HashSet<int> { 1 }));
        var work = Runner.DiscoverWork(scenario.Gateway, durable.Load(), scenario.Options, new HashSet<int> { 1 });
        Assert.Single(work);
        Assert.Equal(3, work[0].Order.OrderNumber);
        scenario.Store = durable;
        Assert.Equal("COMPLETED", scenario.Apply(restart: true).ServiceOrderUpdateStatus);
    }

    [Fact]
    public void RecoveryHonorsEndpointOrganizationEventAndExplicitExhibitorScope()
    {
        using var scenario = new RetryBoundaryTests.Scenario("Update service order");
        scenario.Apply();
        var records = scenario.Store.Load();
        Assert.Single(Runner.RecoveryWork(records, scenario.Options, new HashSet<int> { 1 }));
        Assert.Empty(Runner.RecoveryWork(records, scenario.Options, new HashSet<int> { 99 }));
        Assert.Empty(Runner.RecoveryWork(records, scenario.Options with { BaseUrl = "https://other.invalid/prod" }, new HashSet<int> { 1 }));
        Assert.Empty(Runner.RecoveryWork(records, scenario.Options with { OrganizationCode = "99" }, new HashSet<int> { 1 }));
        Assert.Empty(Runner.RecoveryWork(records, scenario.Options with { ExhibitorId = 99 }, new HashSet<int> { 1 }));
        Assert.Single(Runner.RecoveryWork(records, scenario.Options with { ExhibitorId = 2 }, new HashSet<int>()));
    }

    [Fact]
    public void CorruptOrUnwritableRequiredStatePreventsAnyMutation()
    {
        using var scenario = new RetryBoundaryTests.Scenario();
        File.WriteAllText(Path.Combine(scenario.Options.StateFolder, "orders", "broken.json"), "not JSON");
        Assert.Throws<JournalStorageException>(() => scenario.Apply());
        Assert.Empty(scenario.Transport.Mutations);
        var blocked = Path.Combine(scenario.Options.StateFolder, "blocked");
        File.WriteAllText(blocked, "file blocks directory creation");
        Assert.Throws<JournalStorageException>(() => new FileJournalStore(blocked));
    }

    [Fact]
    public void AWriteWithoutOrderJournalCannotDispatch()
    {
        using var scenario = new RetryBoundaryTests.Scenario();
        scenario.Gateway.Journal = null;
        Assert.Throws<InvalidOperationException>(() => scenario.Gateway.UpdateOrder(scenario.Transport.Order));
        Assert.Empty(scenario.Transport.Mutations);
    }

    [Fact]
    public void WriteBoundaryRejectsADifferentStageWhilePriorOutcomeIsUnknown()
    {
        using var scenario = new RetryBoundaryTests.Scenario("Update service order");
        Assert.Throws<UnknownWriteOutcomeException>(() => scenario.Gateway.UpdateOrder(scenario.Transport.Order));
        Assert.Throws<RecoveryReviewException>(() => scenario.Gateway.UpdateExhibitor(scenario.Transport.Exhibitor));
        Assert.Equal(new[] { "Update service order" }, scenario.Transport.Mutations);
        Assert.Equal(StageStatus.Unknown, scenario.Store.Load().Single().Stages.Single().Status);
    }

    [Fact]
    public void SameSecondCsvFilesPreserveBothReports()
    {
        using var scenario = new RetryBoundaryTests.Scenario();
        var started = new DateTime(2026, 10, 4, 12, 0, 0);
        var first = CsvRunWriter.Write(scenario.Options.RunFolder, started, [scenario.Row], true);
        var original = File.ReadAllText(first);
        var second = CsvRunWriter.Write(scenario.Options.RunFolder, started, [], true);
        Assert.NotEqual(first, second);
        Assert.Equal(original, File.ReadAllText(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void CanonicalLiveStateRejectsAlternateLocationsAndLockCannotBeAcquiredTwice()
    {
        using var scenario = new RetryBoundaryTests.Scenario();
        Assert.Throws<CliException>(() => CanonicalState.Validate(scenario.Options));
        Assert.Throws<CliException>(() => CliOptions.Parse(["apply", "--confirm-service-order-entry", "--exhibitor", "2", "--state-folder", scenario.Options.StateFolder]));
        CanonicalState.Validate(scenario.Options with { StateFolder = CanonicalState.Folder });
        using (RunLock.Acquire(scenario.Options.StateFolder))
            Assert.Throws<InvalidOperationException>(() => RunLock.Acquire(scenario.Options.StateFolder));
        using var afterRestart = RunLock.Acquire(scenario.Options.StateFolder);
    }

    [Fact]
    public void LockExcludesAnotherProcess()
    {
        using var scenario = new RetryBoundaryTests.Scenario();
        using var runLock = RunLock.Acquire(scenario.Options.StateFolder);
        var lockPath = Path.Combine(scenario.Options.StateFolder, "ServiceOrderEntry.lock").Replace("'", "''");
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"try {{ $s = [IO.FileStream]::new('{lockPath}', [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $s.Dispose(); exit 0 }} catch {{ exit 9 }}");
        using var process = System.Diagnostics.Process.Start(start)!;
        Assert.True(process.WaitForExit(20000));
        Assert.Equal(9, process.ExitCode);
    }

    [Fact]
    public void ChangedRecoveryOptionsRequireReviewWithoutAnyAdditionalWrite()
    {
        using var scenario = new RetryBoundaryTests.Scenario("Update service order");
        scenario.Apply();
        var count = scenario.Transport.Mutations.Count;
        var runner = new Runner(scenario.Options with { ActivateExhibitor = false });
        runner.UseJournalStore(scenario.Store);
        var row = scenario.Store.Load().Single().Plan;
        runner.Apply(scenario.Gateway, new Candidate(scenario.Transport.Order, scenario.Transport.Exhibitor), row);
        Assert.Equal("RECOVERY REVIEW", row.ServiceOrderUpdateStatus);
        Assert.Equal(count, scenario.Transport.Mutations.Count);
    }

    [Theory]
    [InlineData("Activate order")]
    [InlineData("Activate exhibitor")]
    public void ConflictingCurrentActivationStatusRequiresReviewWithoutReactivation(string stage)
    {
        using var scenario = new RetryBoundaryTests.Scenario();
        var durable = scenario.Store;
        scenario.Store = new FaultStore(durable, e => e.Stages.Any(x => x.Operation == stage && x.Status == StageStatus.Verified), afterSave: true);
        Assert.Throws<JournalStorageException>(() => scenario.Apply());
        if (stage == "Activate order") scenario.Transport.Order.OrderStatus = "PC";
        else scenario.Transport.Exhibitor.ExhibitorStatus = 35;
        var count = scenario.Transport.Mutations.Count;
        scenario.Store = durable;
        var row = scenario.Apply(restart: true);
        Assert.Equal("RECOVERY REVIEW", row.ServiceOrderUpdateStatus);
        Assert.Equal(count, scenario.Transport.Mutations.Count);
        Assert.False(scenario.Store.Load().Single().Complete);
    }

    private sealed class FaultStore(IJournalStore inner, Func<OrderEvidence, bool> trigger, bool afterSave) : IJournalStore
    {
        public IReadOnlyList<OrderEvidence> Load() => inner.Load();
        public void Save(OrderEvidence evidence)
        {
            var fail = trigger(evidence);
            if (!fail || afterSave) inner.Save(evidence);
            if (fail) throw new JournalStorageException("Simulated interruption / journal persistence failure.", new IOException("Offline storage failure."));
        }
    }
}
