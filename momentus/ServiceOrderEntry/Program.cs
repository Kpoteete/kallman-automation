using System.Globalization;
using Ungerboeck.Api.Models.Subjects;

namespace ServiceOrderEntry;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = CliOptions.Parse(args);
            if (options.Help) { Console.WriteLine(CliOptions.HelpText); return 0; }
            return new Runner(options).Run();
        }
        catch (CliException ex) { Console.Error.WriteLine($"ERROR: {ex.Message}\n{CliOptions.HelpText}"); return 2; }
        catch (Exception ex) { Console.Error.WriteLine($"ERROR: {ex.Message}"); return 1; }
    }
}

internal sealed record CliOptions(
    bool Apply, bool Confirm, bool Help, bool ActivateOrder, bool ActivateExhibitor, bool SendReadyEmail, bool All, bool MaxUpdatesSpecified,
    string OrganizationCode, string BaseUrl, string StateFolder, string RunFolder,
    string SalesRepLookupPath, string CategoryLookupPath, int PageSize, int MaxResults, int MaxUpdates, int RequestDelayMs,
    int? ExhibitorId, int? EventId)
{
    public bool Probe { get; init; }
    public int? OrderNumber { get; init; }
    public BillingConfiguration Billing { get; init; } = new();
    public string BillingConfigurationPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "billing-config.json");
    public const string HelpText = """
ServiceOrderEntry

Usage:
  ServiceOrderEntry.exe probe
  ServiceOrderEntry.exe preview
  ServiceOrderEntry.exe preview --exhibitor ID [--event ID]
  ServiceOrderEntry.exe apply --confirm-service-order-entry --exhibitor ID [options]
  ServiceOrderEntry.exe apply --confirm-service-order-entry --all [--max-updates 1-10]

Preview is the default and never writes to Momentus. Initial live testing must be
scoped to exactly one exhibitor. Eligible records require exhibitor status 35
(Online Booth Order), main-exhibitor type ME, and service-order status PC.
Bulk runs search all events in the selected organization.

Options:
  --exhibitor ID                 Scope the run to one exhibitor (required for apply).
  --order ID                     Scope an explicit exhibitor run to one order.
  --all                          Apply to all eligible records, capped at 10 attempts.
  --event ID                     Optionally scope that exhibitor to one event.
  --activate-order               Enabled by default in apply: change verified order PC to A.
  --activate-exhibitor           Enabled by default in apply: change completed exhibitor 35 to 2.
  --skip-order-activation        Keep order PC (exceptional run / matching old recovery options).
  --skip-exhibitor-activation    Keep exhibitor 35 (exceptional run / matching old recovery options).
  --skip-ready-email             Do not send the normally enabled ready-for-invoicing email.
  --confirm-service-order-entry  Required with apply mode.
  --max-updates N                Maximum orders updated in one apply run (default: 1).
  --state-folder PATH            Preview only; live state is under ProgramData/Kallman/ServiceOrderEntry/state.
  --run-folder PATH              CSV run-file folder (default: ./runs).
  --sales-rep-lookup PATH        Default: ./SalesRepLookup.xlsx.
  --category-lookup PATH         Default: ./OrderCategoryLookup.xlsx.
  --billing-config PATH          Explicit billing UDF identity and tenant Not Applicable status (default: ./billing-config.json).
  --org CODE                     Momentus organization (default: 10).
  --base-url URL                 Default: https://kallman.ungerboeck.com/prod.
  --page-size N                  API page size (default: 1000).
  --max-results N                Search safety ceiling (default: 100000).
  --request-delay-ms N           Delay after API calls (default: 100).
  --help                         Show help.

Credentials are read only from MOMENTUS_APIUSER, MOMENTUS_SECRET, and MOMENTUS_KEY.
""";

    public static CliOptions Parse(string[] args)
    {
        var root = AppContext.BaseDirectory;
        var o = new CliOptions(false, false, false, false, false, true, false, false, "10", "https://kallman.ungerboeck.com/prod",
            CanonicalState.Folder, Path.Combine(root, "runs"), Path.Combine(root, "SalesRepLookup.xlsx"), Path.Combine(root, "OrderCategoryLookup.xlsx"),
            1000, 100000, 1, 100, null, null);
        var i = 0;
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            o = args[0].ToLowerInvariant() switch { "probe" => o with { Probe = true }, "preview" => o, "apply" => o with { Apply = true, ActivateOrder = true, ActivateExhibitor = true }, _ => throw new CliException($"Unknown mode '{args[0]}'.") };
            i++;
        }
        string Next() { if (++i >= args.Length) throw new CliException($"Missing value after {args[i - 1]}."); return args[i]; }
        for (; i < args.Length; i++) o = args[i].ToLowerInvariant() switch
        {
            "--confirm-service-order-entry" => o with { Confirm = true }, "--activate-order" => o with { ActivateOrder = true },
            "--activate-exhibitor" => o with { ActivateExhibitor = true }, "--help" or "-h" or "/?" => o with { Help = true },
            "--skip-order-activation" => o with { ActivateOrder = false },
            "--skip-exhibitor-activation" => o with { ActivateExhibitor = false },
            "--skip-ready-email" => o with { SendReadyEmail = false },
            "--order" => o with { OrderNumber = Int(Next(), 1, int.MaxValue) },
            "--all" => o with { All = true },
            "--org" => o with { OrganizationCode = Next().Trim() }, "--base-url" => o with { BaseUrl = Next().Trim().TrimEnd('/') },
            "--state-folder" => o with { StateFolder = Path.GetFullPath(Next()) }, "--run-folder" => o with { RunFolder = Path.GetFullPath(Next()) },
            "--sales-rep-lookup" => o with { SalesRepLookupPath = Path.GetFullPath(Next()) }, "--category-lookup" => o with { CategoryLookupPath = Path.GetFullPath(Next()) },
            "--enabled-events" => throw new CliException("--enabled-events has been removed. Omit it to search all events."),
            "--billing-config" => o with { BillingConfigurationPath = Path.GetFullPath(Next()) },
            "--page-size" => o with { PageSize = Int(Next(), 1, 10000) }, "--max-results" => o with { MaxResults = Int(Next(), 1, 1000000) },
            "--max-updates" => o with { MaxUpdates = Int(Next(), 1, 1000), MaxUpdatesSpecified = true }, "--request-delay-ms" => o with { RequestDelayMs = Int(Next(), 0, 60000) },
            "--exhibitor" => o with { ExhibitorId = Int(Next(), 1, int.MaxValue) }, "--event" => o with { EventId = Int(Next(), 1, int.MaxValue) },
            _ => throw new CliException($"Unknown option '{args[i]}'.")
        };
        if (o.Apply && !o.Confirm) throw new CliException("Apply mode requires --confirm-service-order-entry.");
        if (!o.Apply && o.Confirm) throw new CliException("The confirmation switch is valid only in apply mode.");
        if (o.Apply && !o.ExhibitorId.HasValue && !o.All) throw new CliException("Apply mode requires either --exhibitor or --all.");
        if (o.ExhibitorId.HasValue && o.All) throw new CliException("Use either --exhibitor or --all, not both.");
        if (o.All && o.EventId.HasValue) throw new CliException("--event cannot be combined with --all.");
        if (o.All && o.MaxUpdates > 10) throw new CliException("Bulk apply is capped at 10 write attempts.");
        if (o.All && !o.MaxUpdatesSpecified) o = o with { MaxUpdates = 10 };
        if (o.EventId.HasValue && !o.ExhibitorId.HasValue) throw new CliException("--event requires --exhibitor.");
        if (o.OrderNumber.HasValue && !o.ExhibitorId.HasValue) throw new CliException("--order requires --exhibitor.");
        if (!o.Apply && (o.ActivateOrder || o.ActivateExhibitor)) throw new CliException("Status-change switches are valid only in apply mode.");
        if (!o.Apply && args.Any(x => x.Equals("--skip-order-activation", StringComparison.OrdinalIgnoreCase) || x.Equals("--skip-exhibitor-activation", StringComparison.OrdinalIgnoreCase)))
            throw new CliException("Status-change switches are valid only in apply mode.");
        if (o.Help) return o;
        if (args.Contains("--billing-config", StringComparer.OrdinalIgnoreCase) && !File.Exists(o.BillingConfigurationPath))
            throw new CliException($"Billing configuration not found: {o.BillingConfigurationPath}");
        o = o with { Billing = BillingConfiguration.Load(o.BillingConfigurationPath) };
        CanonicalState.Validate(o);
        if (!File.Exists(o.SalesRepLookupPath)) throw new CliException($"Sales-rep lookup not found: {o.SalesRepLookupPath}");
        if (!File.Exists(o.CategoryLookupPath)) throw new CliException($"Category lookup not found: {o.CategoryLookupPath}");
        return o;
    }
    private static int Int(string raw, int min, int max) => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? n : throw new CliException($"Invalid whole number '{raw}'.");
}

internal sealed class CliException(string message) : Exception(message);
internal sealed class ApplyLimitException(int limit) : Exception($"Apply limit of {limit} order write attempts reached; no further write dispatched.");
internal sealed record WorkDiscovery(IReadOnlyList<Candidate> Candidates, IReadOnlyList<RunRow> Failures);

internal sealed partial class Runner(CliOptions options)
{
    private readonly DateTime started = DateTime.Now;
    private readonly List<RunRow> rows = [];
    private readonly string runId = Guid.NewGuid().ToString("N");
    private IJournalStore? journalStore;
    private IReadOnlyList<OrderEvidence> existing = [];

    public int Run()
    {
        options.Billing.Validate();
        CanonicalState.Validate(options);
        using var runLock = RunLock.Acquire(options.StateFolder);
        var runStateFolder = CurrentRunState.CreateFolder(options.StateFolder, runId);
        journalStore = new FileJournalStore(runStateFolder);
        existing = [];
        CsvRunWriter.Preflight(options.RunFolder); // Fail closed before any API writes or discovery.
        Console.WriteLine(options.Probe ? "PROBE MODE: local configuration and storage checks only; no Momentus requests." : options.Apply ? "LIVE MODE: verified fields may be saved; incomplete orders remain pending." : "PREVIEW MODE: no Momentus record will be changed.");
        Console.WriteLine("Plan: CURRENT MOMENTUS DATA ONLY; prior runs are audit history, not inputs.");
        Console.WriteLine($"Order activation: {(options.ActivateOrder ? "ON" : "OFF")}; Exhibitor activation: {(options.ActivateExhibitor ? "ON" : "OFF")}.");
        Console.WriteLine($"Ready-for-invoicing email to {ReadyEmailBuilder.FinanceRecipients}; CC {ReadyEmailBuilder.FinanceCcRecipient} and the assigned order account rep: {(options.Apply && options.SendReadyEmail ? "ON" : options.Apply ? "OFF" : "PREVIEW ONLY")}.");
        Console.WriteLine($"Unfinished-work summary to {ExceptionSummary.Recipient}: {(options.Apply ? "ON (one email per run with exceptions)" : "PREVIEW ONLY; no email sent")}.");
        Console.WriteLine(options.EventId.HasValue ? $"Event scope: {options.EventId.Value}." : "Event scope: ALL EVENTS.");
        if (options.ExhibitorId.HasValue)
            Console.WriteLine($"Exhibitor scope: {options.ExhibitorId.Value}.");
        var reps = LookupLoader.LoadSalesReps(options.SalesRepLookupPath);
        var categories = LookupLoader.LoadCategories(options.CategoryLookupPath);
        if (options.Probe)
        {
            var missing = new[] { "MOMENTUS_APIUSER", "MOMENTUS_SECRET", "MOMENTUS_KEY" }
                .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))).ToList();
            if (missing.Count > 0) throw new InvalidOperationException($"Missing environment variables: {string.Join(", ", missing)}.");
            Console.WriteLine($"PROBE PASS: billing configuration, {reps.Count} sales reps, {categories.Count} categories, {existing.Count} existing journals, credentials present and durable state/report storage verified.");
            Console.WriteLine($"State folder: {options.StateFolder}; report folder: {options.RunFolder}.");
            return 0;
        }
        var gateway = new MomentusGateway(options with { StateFolder = runStateFolder });
        using var progress = new RunProgress(() => gateway.RequestCount);
        gateway.Progress = progress;
        progress.Message($"Run started. Live order attempt limit: {options.MaxUpdates}. State folder: {runStateFolder}.");
        try
        {
            WorkDiscovery discovery;
            using (progress.Begin("Finding eligible exhibitors and pending orders across the selected event scope"))
                discovery = DiscoverWork(gateway, existing, options);
            return ProcessDiscovery(gateway, discovery, reps, categories);
        }
        finally { progress.ReportTimings(); }
    }

    internal int ProcessDiscovery(MomentusGateway gateway, WorkDiscovery discovery, IReadOnlyList<SalesRepLookup> reps, IReadOnlyList<CategoryLookup> categories)
    {
        Console.WriteLine($"Eligible/recovery records found: {discovery.Candidates.Count:N0}; unavailable recovery records: {discovery.Failures.Count(x => x.Outcome != "SKIPPED"):N0}; superseded recovery records: {discovery.Failures.Count(x => x.Outcome == "SKIPPED"):N0}.");
        foreach (var row in discovery.Failures)
        {
            row.RunId = runId;
            rows.Add(row);
            Console.Error.WriteLine($"{(row.Outcome == "SKIPPED" ? "RECOVERY SKIPPED" : "RECOVERY REVIEW")}: order {row.OrderNumber}, event {row.EventId}, exhibitor {row.ExhibitorId}: {row.UpdateMessage}");
        }
        return ProcessCandidates(gateway, discovery.Candidates, reps, categories);
    }

    internal int ProcessCandidates(MomentusGateway gateway, IReadOnlyList<Candidate> candidates, IReadOnlyList<SalesRepLookup> reps, IReadOnlyList<CategoryLookup> categories)
    {
        CsvRunWriter.Preflight(options.RunFolder);
        if (options.Apply)
        {
            PrepareCandidateJournals(candidates);
        }
        var updated = 0;
        var attempted = 0;
        var processed = 0;
        foreach (var candidate in candidates)
        {
            processed++;
            if (gateway.Progress is { } progress)
                progress.Context = $"Order {Value(candidate.Order.OrderNumber)} ({processed}/{candidates.Count}), event {Value(candidate.Exhibitor.Event)}";
            using var orderProgress = gateway.Progress?.Begin("Checking billing, contacts, order items, contracts and booth information");
            RunRow row;
            try
            {
                var previous = existing.SingleOrDefault(x => x.Identity == new OrderIdentity(new Uri(options.BaseUrl).AbsoluteUri.TrimEnd('/'), options.OrganizationCode,
                    Value(candidate.Exhibitor.Event), Value(candidate.Exhibitor.ExhibitorID), Value(candidate.Order.OrderNumber)));
                row = previous is not null && previous.Stages.Count > 0 ? previous.Plan : Evaluate(gateway, new Candidate(gateway.GetOrder(Value(candidate.Order.OrderNumber)), gateway.GetExhibitor(Value(candidate.Exhibitor.ExhibitorID))), reps, categories);
                if (previous is not null && previous.Stages.Count > 0)
                {
                    // Recovery admission is not billing READY. Apply reconciles stages and validates current billing before further writes.
                    row.ValidationStatus = previous.Complete ? ValidationRules.Validate(row).Status : "PREPARED";
                    if (previous.Complete && row.ValidationStatus == "REVIEW")
                        row.ValidationMessage = "Completed journal retained; legacy/missing effective billing state requires REVIEW before any new processing.";
                    row.ServiceOrderUpdateStatus = "NOT ATTEMPTED";
                    row.ProcessingId = previous.ProcessingId;
                    row.JournalStages = StageSummary(previous);
                    if (!options.Apply)
                    {
                        row.ServiceOrderUpdateStatus = previous.Complete ? "COMPLETED (JOURNAL)" : "RECOVERY PENDING";
                        if (previous.Stages.Any(x => x.Status is StageStatus.Dispatching or StageStatus.Unknown or StageStatus.Succeeded))
                        {
                            row.ValidationStatus = "REVIEW";
                            row.ServiceOrderUpdateStatus = "RECOVERY REVIEW";
                            row.UpdateMessage = "Unfinished dispatched stages require reconciliation; preview performs no recovery mutations.";
                        }
                    }
                }
            }
            catch (JournalStorageException) { throw; }
            catch (Exception ex) { row = FailureRules.Evaluation(candidate, ex); }
            if (row.Outcome.Length == 0) row.Outcome = row.ValidationStatus == "REVIEW" ? "REVIEW" : "SUCCESS";
            if (options.Apply && row.ValidationStatus is "REVIEW" or "FAILED") PersistEvaluation(row);
            row.OrderStatusAction = options.ActivateOrder ? "CHANGE TO ACTIVE (A)" : "NO CHANGE (REMAINS PC)";
            row.ExhibitorStatusAction = options.ActivateExhibitor ? "CHANGE TO ACTIVE (2)" : "NO CHANGE (REMAINS 35)";
            row.RunId = runId;
            rows.Add(row);
            gateway.Progress?.Message($"{row.ExhibitorName}: {row.ValidationStatus}. {row.ValidationMessage}");
            if (options.Apply && (row.ValidationStatus is "READY" or "PREPARED" || row.ComponentEvaluation))
            {
                using var applyProgress = gateway.Progress?.Begin("Rechecking current data and completing the order; every saved change is verified");
                var reserved = false;
                gateway.BeforeWriteDispatch = () =>
                {
                    if (reserved) return;
                    if (attempted >= options.MaxUpdates) throw new ApplyLimitException(options.MaxUpdates);
                    attempted++;
                    reserved = true;
                };
                try
                {
                    if (row.ValidationStatus is "READY" or "PREPARED") Apply(gateway, candidate, row);
                    else ApplyPartial(gateway, row);
                    if (row.ComponentEvaluation && row.Outcome == "REVIEW" && row.ServiceOrderUpdateStatus == "RECOVERY REVIEW" &&
                        CanContinuePartial(journalStore!.Load().Single(x => x.Identity == OrderIdentity.From(options, row))))
                    {
                        // A known business review can still leave independent work available.
                        // Never enter this path with an unresolved dispatch, a rejected write or activation.
                        var message = row.UpdateMessage;
                        var component = message.Contains("contact", StringComparison.OrdinalIgnoreCase) || message.Contains("Bill-To", StringComparison.OrdinalIgnoreCase) || message.Contains("billing", StringComparison.OrdinalIgnoreCase)
                            ? "Bill-To" : message.Contains("contract", StringComparison.OrdinalIgnoreCase) ? "Contracts"
                            : message.Contains("note", StringComparison.OrdinalIgnoreCase) ? "Payment note" : "Current data";
                        if (component == "Bill-To") row.BillingResolved = false;
                        if (component == "Contracts") { row.ContractsResolved = false; row.NoteResolved = false; }
                        if (component == "Payment note") row.NoteResolved = false;
                        row.Issues.Add(new(component, message, IssueValues(row, component)));
                        ApplyPartial(gateway, row);
                    }
                }
                catch (JournalStorageException)
                {
                    try { Console.Error.WriteLine($"Run halted for journal storage failure. Run file: {CsvRunWriter.Write(options.RunFolder, started, rows, options.Apply, runId)}"); }
                    catch (Exception reportError) { Console.Error.WriteLine($"Run report could not be saved: {reportError.Message}"); }
                    throw;
                }
                finally { gateway.BeforeWriteDispatch = null; }
                if (row.ServiceOrderUpdateStatus == "COMPLETED") updated++;
            }
            else if (!options.Apply && row.ProcessingId.Length == 0)
            {
                row.ServiceOrderUpdateStatus = row.ValidationStatus is "READY" or "PREPARED" ? "WOULD UPDATE" : row.ComponentEvaluation ? "WOULD FILL VERIFIED FIELDS (REMAINS PC)" : "NOT ATTEMPTED";
                if (row.ValidationStatus == "REVIEW")
                {
                    row.OrderStatusAction = "NO CHANGE (REMAINS PC)";
                    row.ExhibitorStatusAction = "NO CHANGE (REMAINS 35)";
                    row.ReadyEmailStatus = "WITHHELD (INCOMPLETE ORDER)";
                }
            }
            gateway.Progress?.Message($"Result: {row.Outcome}; order work: {row.ServiceOrderUpdateStatus}; email: {row.ReadyEmailStatus}; exhibitor activation: {row.ExhibitorStatusAction}. {row.UpdateMessage}");
        }
        if (gateway.Progress is { } finalProgress) finalProgress.Context = "";
        using var reportProgress = gateway.Progress?.Begin("Saving the final run report");
        var path = CsvRunWriter.Write(options.RunFolder, started, rows, options.Apply, runId);
        var notificationExitCode = SendExceptionSummary(gateway, path);
        Console.WriteLine($"READY: {rows.Count(x => x.ValidationStatus == "READY"):N0}; PREPARED: {rows.Count(x => x.ValidationStatus == "PREPARED"):N0}; REVIEW: {rows.Count(x => x.ValidationStatus == "REVIEW"):N0}; ATTEMPTED: {attempted:N0}; COMPLETED: {updated:N0}.");
        Console.WriteLine($"Outcomes: SUCCESS {rows.Count(x => x.Outcome == "SUCCESS"):N0}; REVIEW {rows.Count(x => x.Outcome == "REVIEW"):N0}; FAILED {rows.Count(x => x.Outcome == "FAILED"):N0}; UNKNOWN {rows.Count(x => x.Outcome == "UNKNOWN"):N0}; shared activation pending {rows.Count(x => x.ExhibitorActivationPending):N0}.");
        Console.WriteLine($"API requests: {gateway.RequestCount:N0}. Run file: {path}");
        var orderExitCode = ApplyExitCode(rows);
        return orderExitCode == 3 || notificationExitCode == 3 ? 3
            : orderExitCode == 1 || notificationExitCode == 1 ? 1 : orderExitCode;
    }

    internal static int ApplyExitCode(IEnumerable<RunRow> rows) => FailureRules.ExitCode(rows);

    private void PersistEvaluation(RunRow row)
    {
        var identity = OrderIdentity.From(options, row);
        var previous = journalStore!.Load().SingleOrDefault(x => x.Identity == identity);
        var evidence = previous ?? new OrderEvidence { Identity = identity, CreatedRunId = runId, Plan = row,
            SendEmail = options.SendReadyEmail, ActivateOrder = options.ActivateOrder, ActivateExhibitor = options.ActivateExhibitor };
        if (evidence.Stages.Count == 0) evidence.Plan = row;
        evidence.Plan.Outcome = row.Outcome;
        evidence.Complete = false;
        journalStore.Save(evidence);
    }

    private static string StageSummary(OrderEvidence evidence) => string.Join("\n", evidence.Stages.Select(x => $"{x.Operation} ({x.Target}): {x.Status}"));

    internal static IEnumerable<OrderEvidence> RecoveryWork(IEnumerable<OrderEvidence> records, CliOptions options) => records.Where(x =>
        (!x.Complete || x.ExhibitorActivationPending) && x.Identity.Endpoint == new Uri(options.BaseUrl).AbsoluteUri.TrimEnd('/') && x.Identity.Organization == options.OrganizationCode &&
        (!options.ExhibitorId.HasValue || x.Identity.Exhibitor == options.ExhibitorId.Value) && (!options.EventId.HasValue || x.Identity.Event == options.EventId.Value) &&
        (!options.OrderNumber.HasValue || x.Identity.Order == options.OrderNumber.Value));

    internal static WorkDiscovery DiscoverWork(MomentusGateway gateway, IEnumerable<OrderEvidence> records, CliOptions options)
    {
        List<Candidate> candidates;
        if (options.OrderNumber.HasValue)
        {
            var order = gateway.GetOrder(options.OrderNumber.Value);
            var exhibitor = gateway.GetExhibitor(options.ExhibitorId!.Value);
            if (order.OrganizationCode != options.OrganizationCode || exhibitor.OrganizationCode != options.OrganizationCode ||
                order.OrderNumber != options.OrderNumber || order.Exhibitor != options.ExhibitorId || exhibitor.ExhibitorID != options.ExhibitorId ||
                order.Event != exhibitor.Event || options.EventId.HasValue && order.Event != options.EventId)
                throw new RecoveryReviewException("REVIEW: explicit order/exhibitor/event identity does not match; no processing allowed.");
            candidates = [new(order, exhibitor)];
        }
        else candidates = gateway.FindCandidates().ToList();
        var failures = new List<RunRow>();
        foreach (var evidence in RecoveryWork(records, options))
        {
            if (candidates.Any(x => Value(x.Order.OrderNumber) == evidence.Identity.Order)) continue;
            try
            {
                if (SupersededRecoveryRules.IsVavePlaceholder(evidence) && SupersededRecoveryRules.Excludes(evidence,
                    gateway.GetExhibitorOrders(evidence.Identity.Exhibitor, evidence.Identity.Event)))
                {
                    failures.Add(new RunRow { EventId = evidence.Identity.Event, ExhibitorId = evidence.Identity.Exhibitor,
                        OrderNumber = evidence.Identity.Order, ProcessingId = evidence.ProcessingId, Outcome = "SKIPPED",
                        ValidationStatus = "SKIPPED", ServiceOrderUpdateStatus = "SUPERSEDED (HISTORY RETAINED)",
                        ValidationMessage = SupersededRecoveryRules.Message, UpdateMessage = SupersededRecoveryRules.Message });
                    continue;
                }
                candidates.Add(new Candidate(gateway.GetOrder(evidence.Identity.Order), gateway.GetExhibitor(evidence.Identity.Exhibitor)));
            }
            catch (JournalStorageException) { throw; }
            catch (Exception ex)
            {
                // Keep durable evidence unchanged. An unavailable recovery target cannot
                // authorize writes or completion, but must not hide other orders' reports.
                var message = $"Saved recovery target could not be read: {ex.GetBaseException().Message}. Recovery history retained; no writes attempted for this record.";
                failures.Add(new RunRow
                {
                    EventId = evidence.Identity.Event, ExhibitorId = evidence.Identity.Exhibitor, OrderNumber = evidence.Identity.Order,
                    ProcessingId = evidence.ProcessingId, JournalStages = StageSummary(evidence),
                    Outcome = evidence.Stages.Any(x => x.Status is StageStatus.Dispatching or StageStatus.Unknown or StageStatus.Succeeded) ? "UNKNOWN" : "REVIEW",
                    ValidationStatus = "REVIEW", ValidationMessage = message, UpdateMessage = message,
                    ServiceOrderUpdateStatus = "RECOVERY REVIEW", ExhibitorActivationPending = evidence.ExhibitorActivationPending
                });
            }
        }
        return new(candidates, failures);
    }

    // Injection is restricted to the offline orchestration seam; production Run always uses canonical storage and the lock.
    internal void UseJournalStore(IJournalStore store) { journalStore = store; existing = store.Load(); }

    internal void PrepareCandidateJournals(IReadOnlyList<Candidate> candidates)
    {
        if (candidates.Count == 0) return;
        // No Momentus mutations occur during this preparation, and the run lock excludes
        // another writer. Load once; subsequent write/recovery boundaries still reload.
        var known = journalStore!.Load().Select(x => x.Identity).ToHashSet();
        var endpoint = new Uri(options.BaseUrl).AbsoluteUri.TrimEnd('/');
        foreach (var candidate in candidates)
        {
            var identity = new OrderIdentity(endpoint, options.OrganizationCode,
                Value(candidate.Exhibitor.Event), Value(candidate.Exhibitor.ExhibitorID), Value(candidate.Order.OrderNumber));
            if (known.Contains(identity)) continue;
            // Retain every intended order, including those deferred by the attempt cap
            // or later made active externally. Only remember a successful durable save.
            journalStore.Save(new OrderEvidence { Identity = identity, CreatedRunId = runId, SendEmail = options.SendReadyEmail,
                ActivateOrder = options.ActivateOrder, ActivateExhibitor = options.ActivateExhibitor,
                Plan = new RunRow { EventId = identity.Event, ExhibitorId = identity.Exhibitor, OrderNumber = identity.Order,
                    Outcome = "PENDING", ValidationStatus = "PREPARED" } });
            known.Add(identity);
        }
    }

    internal static RunRow Evaluate(MomentusGateway gateway, Candidate candidate, IReadOnlyList<SalesRepLookup> reps, IReadOnlyList<CategoryLookup> categories)
    {
        return EvaluateComponents(gateway, candidate, reps, categories);
    }

    internal static bool HasMatchingDocument(IEnumerable<DocumentInfo> orderDocuments, DocumentInfo source) => orderDocuments.Any(x => ContractRules.SameContent(x, source));

    internal static void DecideAccountAndAddress(MomentusGateway gateway, RunRow row)
    {
        var config = gateway.BillingConfiguration;
        config.Validate();
        row.BillingVersion = 1;
        row.BillingConfiguration = config;
        row.AccountDecisionMessage = "";
        row.ContactDecisionMessage = "";
        row.ContactAction = "";
        BillingState.Set(row, new(row.ExistingBillToAccount, row.ExistingBillToContact, "Existing order", "Existing order", false)
            { ContactAddressRequired = true });
        var selector = config.Selector(row.RequestedBilling);
        if (selector == "ABOVE")
        {
            row.BillingRequestBypassed = true;
            row.BillToAddressAction = "KEEP EXISTING ABOVE ADDRESS";
            BillingState.Set(row, row.EffectiveBilling! with { AboveAddress = true });
            if (!TextRules.Same(row.BillingSourceAccount, row.ExistingBillToAccount.AccountCode))
                row.AccountDecisionMessage = "Use Above Address conflicts with the order's Bill-To account; REVIEW without reassignment.";
            gateway.SetBillingAddressLabels(row);
            return;
        }
        row.BillingRequestBypassed = false;
        var errors = BillingRules.RequestedErrors(row.RequestedBilling, config);
        if (errors.Count > 0)
        {
            row.BillToAddressAction = "REVIEW";
            row.AccountDecisionMessage = string.Join(" ", errors);
            return;
        }
        AccountMatch match;
        try
        {
            _ = gateway.ResolveBillingGeography(row.RequestedBilling);
            match = AccountIdentityRules.Resolve(gateway.FindOrganizationCandidates(row.RequestedBilling), row.RequestedBilling, gateway.BillingAddressesMatch);
        }
        catch (DecisionSearchException)
        {
            row.AccountMatchKind = AccountMatchKind.SearchFailure;
            throw;
        }
        row.AccountMatchKind = match.Kind;
        row.AccountDecisionMessage = match.Message;
        switch (match.Kind)
        {
            case AccountMatchKind.Confirmed:
                row.BillToAddressAction = "USE MATCHED BILL-TO ACCOUNT";
                BillingState.Set(row, row.EffectiveBilling! with { Account = match.Account!, AccountSource = "Reused separate account" });
                break;
            case AccountMatchKind.None:
                if (string.IsNullOrWhiteSpace(config.EventSalesNotApplicableCode))
                {
                    row.BillToAddressAction = "REVIEW";
                    row.AccountDecisionMessage = "New Bill-To creation requires the tenant's confirmed Not Applicable Event Sales status code in billing configuration.";
                    break;
                }
                row.BillToAddressAction = "CREATE RELATED BILL-TO ACCOUNT";
                BillingState.Set(row, row.EffectiveBilling! with { Account = BillingRules.RequestedAccount(gateway.ResolveBillingGeography(row.RequestedBilling)),
                    AccountSource = "New separate account", AccountCreationPending = true });
                break;
            default:
                row.BillToAddressAction = "REVIEW";
                break;
        }
        if (row.BillToAddressAction != "REVIEW") gateway.SetBillingAddressLabels(row);
    }

    internal static void DecideContact(MomentusGateway gateway, RunRow row)
    {
        if (row.EffectiveBilling is null || row.AccountDecisionMessage.Length > 0 ||
            BillingRules.RequestedErrors(row.RequestedBilling, gateway.BillingConfiguration).Count > 0) return;
        var requested = TextRules.NormalizeEmail(row.RequestedBilling.Email);
        var scoped = string.IsNullOrWhiteSpace(row.FinalBillToAccount) ? [] : gateway.FindContacts(row.FinalBillToAccount, requested);
        // Current stable ID wins only if it is a valid contact on the intended account and present in the complete result.
        var selected = SelectContact(scoped, row.ExistingBillToContact.AccountCode);
        if (selected is not null)
        {
            row.BillToContactMatchResult = "ACCOUNT CONTACT MATCH";
            row.ContactAction = BillingState.ContactNeedsUpdate(row, selected) ? "UPDATE EXISTING CONTACT" : "USE EXISTING CONTACT";
            row.MatchingContactCode = selected.AccountCode;
            BillingState.Set(row, row.EffectiveBilling with { Contact = selected, ContactSource = "Reused billing contact", ContactCreationPending = false });
            return;
        }
        if (scoped.Count > 0)
        {
            row.ContactAction = "REVIEW";
            row.ContactDecisionMessage = "Multiple or invalid contacts use the billing email on the intended account; REVIEW without creating another contact.";
            return;
        }
        var global = gateway.FindGlobalContactCandidates(requested);
        if (global.Count > 0)
        {
            selected = SelectContact(global, "");
            if (selected is null || (!string.IsNullOrWhiteSpace(selected.PrimaryAccount) && !BillingRules.ValidCode(selected.PrimaryAccount)) ||
                !TextRules.Same(selected.FirstName, row.RequestedBilling.FirstName) || !TextRules.Same(selected.LastName, row.RequestedBilling.LastName))
            {
                row.ContactAction = "REVIEW";
                row.ContactDecisionMessage = "Billing email exists, but a single existing contact with the requested name and valid original company state cannot be verified. REVIEW.";
                return;
            }
            if (!string.IsNullOrWhiteSpace(selected.PrimaryAccount))
            {
                var owner = gateway.GetAccount(selected.PrimaryAccount);
                if (owner.AccountClass != "O" || !TextRules.Same(owner.AccountCode, selected.PrimaryAccount))
                {
                    row.ContactAction = "REVIEW";
                    row.ContactDecisionMessage = "Existing contact's primary company cannot be verified. REVIEW.";
                    return;
                }
            }
            row.BillToContactMatchResult = "CROSS-ACCOUNT CONTACT MATCH";
            row.ContactAction = BillingState.ContactNeedsUpdate(row, selected) ? "UPDATE EXISTING CONTACT" : "USE EXISTING CONTACT";
            row.MatchingContactCode = selected.AccountCode;
            var needsRelationship = row.EffectiveBilling.AccountCreationPending || !TextRules.Same(selected.PrimaryAccount, row.FinalBillToAccount);
            var relationshipPending = needsRelationship && (string.IsNullOrWhiteSpace(row.FinalBillToAccount) ||
                !gateway.HasActiveContactRelationship(row.FinalBillToAccount, selected.AccountCode));
            BillingState.Set(row, row.EffectiveBilling with { Contact = selected, ContactSource = "Reused contact through billing-company CTA relationship",
                ContactCreationPending = false, RequiresContactRelationship = needsRelationship, ContactRelationshipPending = relationshipPending });
            return;
        }
        row.BillToContactMatchResult = "NO MATCH";
        row.ContactAction = "CREATE CONTACT";
        BillingState.Set(row, row.EffectiveBilling with { Contact = BillingRules.RequestedContact(BillingState.ContactRequest(row)),
            ContactSource = "New billing contact", ContactCreationPending = true });
    }

    private static AccountInfo? SelectContact(IReadOnlyList<AccountInfo> contacts, string knownCode)
    {
        var valid = contacts.Where(x => x.AccountClass == "P" && BillingRules.ValidCode(x.AccountCode) && BillingRules.ValidEmail(x.Email)).ToList();
        var exact = valid.Where(x => BillingRules.ValidCode(knownCode) && TextRules.Same(x.AccountCode, knownCode)).ToList();
        if (exact.Count == 1) return exact[0];
        return contacts.Count == 1 && valid.Count == 1 ? valid[0] : null;
    }

    internal void Apply(MomentusGateway gateway, Candidate candidate, RunRow row)
    {
        ProcessingJournal? journal = null;
        var storageFailed = false;
        try
        {
            journalStore ??= new FileJournalStore(options.StateFolder);
            var identity = OrderIdentity.From(options, row);
            var previous = journalStore.Load().SingleOrDefault(x => x.Identity == identity);
            var evidence = previous ?? new OrderEvidence { Identity = identity, Plan = row, CreatedRunId = runId,
                SendEmail = options.SendReadyEmail, ActivateOrder = options.ActivateOrder, ActivateExhibitor = options.ActivateExhibitor };
            // A placeholder with no mutation stages has no dispatched plan to recover. The fresh
            // evaluation can use the current policy; any recorded stage retains its saved options.
            if (evidence.Stages.Count == 0)
            {
                evidence.SendEmail = options.SendReadyEmail;
                evidence.ActivateOrder = options.ActivateOrder;
                evidence.ActivateExhibitor = options.ActivateExhibitor;
            }
            if (evidence.SendEmail != options.SendReadyEmail || evidence.ActivateOrder != options.ActivateOrder || evidence.ActivateExhibitor != options.ActivateExhibitor)
                throw new RecoveryReviewException("REVIEW: recovery options differ from the saved processing plan.");
            journal = new ProcessingJournal(journalStore, evidence, runId);
            row.RunId = runId;
            row.ProcessingId = evidence.ProcessingId;
            gateway.Journal = journal;
            journal.Save();
            gateway.ReconcileIncomplete();
            if (evidence.OrderComplete && evidence.Handoff is not null)
            {
                HandoffRules.RequireOrderStages(evidence, includeActivation: true, includeEmail: false);
                HandoffRules.VerifyRetained(gateway, evidence, includeEmail: false);
                var retainedExhibitor = gateway.GetExhibitor(row.ExhibitorId);
                if (retainedExhibitor.OrganizationCode != evidence.Identity.Organization || retainedExhibitor.ExhibitorID != evidence.Identity.Exhibitor ||
                    retainedExhibitor.Event != evidence.Identity.Event || ExhibitorCategoryRules.HasCode(retainedExhibitor.ExhibitorCategory, ExhibitorCategoryRules.Hold))
                    throw new RecoveryReviewException("REVIEW: completed order's current exhibitor identity/Hold blocks continued processing.");
                row.Outcome = "SUCCESS";
                evidence.Plan = row;
                evidence.Complete = evidence.Stages.All(x => x.Status == StageStatus.Verified) &&
                    (!evidence.SendEmail || evidence.Stages.Any(x => x.Operation == "Send ready email" && x.Status == StageStatus.Verified));
                journal.Save();
                FinishExhibitorActivation(gateway, journal, row);
                FinishReadyEmail(gateway, journal, row);
                return;
            }
            if (evidence.Complete) throw new RecoveryReviewException("REVIEW: legacy completed journal lacks verified final handoff evidence.");
            evidence.Plan = row;
            var currentOrder = gateway.GetOrder(row.OrderNumber);
            var currentExhibitor = gateway.GetExhibitor(row.ExhibitorId);
            bool Verified(string operation) => evidence.Stages.Any(x => x.Operation == operation && x.Status == StageStatus.Verified);
            if (!TextRules.Same(currentOrder.OrganizationCode, options.OrganizationCode) || !TextRules.Same(currentExhibitor.OrganizationCode, options.OrganizationCode) ||
                Value(currentOrder.Event) != row.EventId || Value(currentExhibitor.Event) != row.EventId ||
                Value(currentOrder.OrderNumber) != row.OrderNumber || Value(currentExhibitor.ExhibitorID) != row.ExhibitorId ||
                (Value(currentOrder.Exhibitor) > 0 && Value(currentOrder.Exhibitor) != row.ExhibitorId))
                throw new RecoveryReviewException("REVIEW: current order/exhibitor identity conflicts with the durable plan.");
            if (ExhibitorCategoryRules.HasCode(currentExhibitor.ExhibitorCategory, ExhibitorCategoryRules.Hold))
                throw new RecoveryReviewException("REVIEW: current exhibitor is on Hold; recovery stopped.");
            if (!TextRules.Same(currentOrder.OrderStatus, Verified("Activate order") ? "A" : "PC") ||
                !ActivationRules.CompatibleExhibitorStatus(Value(currentExhibitor.ExhibitorStatus), evidence))
            {
                if (previous is not null) throw new RecoveryReviewException("REVIEW: current statuses conflict with journaled eligibility/activation evidence.");
                throw new RecoveryReviewException("REVIEW: record status changed after evaluation (expected order PC and exhibitor 35).");
            }

            if (row.Contracts is null || !row.Contracts.HasResolvedSchedule ||
                ManagedNoteRules.ContentHash(row.Contracts.PaymentSchedule) != ManagedNoteRules.ContentHash(row.PaymentScheduleText))
                throw new RecoveryReviewException("REVIEW: contract/schedule identity is unresolved or conflicts with the saved plan.");
            BillingGuard.Preflight(gateway, row, currentOrder, journal);
            gateway.MutationGuard = (operation, intent) =>
            {
                if (operation == "Activate exhibitor") return ActivationRules.BeforeWrite(gateway, evidence, journalStore.Load());
                return PlanGuard.BeforeWrite(gateway, row, operation, intent);
            };
            _ = PlanGuard.BeforeWrite(gateway, row, "Preflight", new object());
            _ = HandoffRules.PreflightAttachments(gateway, row);
            journal.Save();
                CompleteBilling(gateway, row, journal);
            row.ValidationStatus = "READY";
            journal.Save();
            currentOrder.OrderAccountRep = row.ProposedOrderAccountRep;
            currentOrder.Category = row.ProposedCategory;
            if (row.DecisionInputs?.Booth.UsesExistingOrderBooth != true) currentOrder.BoothNumber = row.ProposedBoothNumber;
            currentOrder.BillToAccount = row.FinalBillToAccount;
            currentOrder.BillToContact = row.FinalBillToContact;
            if (!Verified("Update service order")) gateway.UpdateOrder(currentOrder);
            var verifiedOrder = gateway.GetOrder(row.OrderNumber);
            VerifyOrder(verifiedOrder, row, false);
            BillingGuard.VerifyFinal(gateway, row);
            journal.Save();

            var copied = gateway.EnsureContractCopies(verifiedOrder, row);
            row.ContractPdfCopyStatus = copied == 0 ? "ALREADY COPIED OR NONE FOUND" : $"COPIED {copied}";
            gateway.VerifyUndispatched("Copy contract document");

            row.PaymentScheduleNoteStatus = gateway.SavePaymentScheduleNote(row.OrderNumber, row.PaymentScheduleText);
            gateway.VerifyUndispatched("Add payment schedule note", "Update payment schedule note");

            currentExhibitor = gateway.GetExhibitor(row.ExhibitorId);
            _ = PlanGuard.BeforeWrite(gateway, row, "Category preflight", new object());
            var needsCategoryUpdate = !TextRules.Same(currentExhibitor.ExhibitorCategory, row.FinalExhibitorCategories);
            if (needsCategoryUpdate)
            {
                currentExhibitor.ExhibitorCategory = row.FinalExhibitorCategories;
                gateway.UpdateExhibitor(currentExhibitor);
                currentExhibitor = gateway.GetExhibitor(row.ExhibitorId);
                if (!ExhibitorCategoryRules.Parse(currentExhibitor.ExhibitorCategory).Where(ExhibitorCategoryRules.ManagedCodes.Contains).SequenceEqual(ExhibitorCategoryRules.Parse(row.FinalExhibitorCategories).Where(ExhibitorCategoryRules.ManagedCodes.Contains)))
                    throw new InvalidOperationException("Exhibitor-category readback did not match the requested values.");
            }
            gateway.VerifyUndispatched("Update exhibitor categories");

            _ = HandoffRules.Verify(gateway, row, journal);

            if (options.ActivateOrder && !Verified("Activate order"))
            {
                HandoffRules.RequireOrderStages(evidence, includeActivation: false, includeEmail: false);
                // Handoff was verified immediately above. UpdateOrder still performs its fresh dispatch guard.
                verifiedOrder.OrderStatus = "A";
                gateway.UpdateOrder(verifiedOrder);
                if (!TextRules.Same(gateway.GetOrder(row.OrderNumber).OrderStatus, "A")) throw new InvalidOperationException("Order status readback did not confirm Active (A).");
            }
            HandoffRules.RequireOrderStages(evidence, includeActivation: true, includeEmail: false);
            evidence.OrderComplete = true;
            evidence.Complete = false;
            row.Outcome = "SUCCESS";
            evidence.ExhibitorActivationPending = options.ActivateExhibitor;
            row.ExhibitorActivationPending = evidence.ExhibitorActivationPending;
            journal.Save();
            FinishExhibitorActivation(gateway, journal, row);
            FinishReadyEmail(gateway, journal, row);
        }

        catch (ApplyLimitException ex)
        {
            row.Outcome = "REVIEW"; row.ServiceOrderUpdateStatus = "DEFERRED (ATTEMPT CAP)";
            row.UpdateMessage = ex.Message;
        }
        catch (UnknownWriteOutcomeException ex)
        {
            row.Outcome = "UNKNOWN"; row.ServiceOrderUpdateStatus = "UNKNOWN WRITE OUTCOME";
            row.UpdateMessage = $"{ex.Message} Subsequent mutation stages for this record were stopped.";
            if (ex.Operation == "Send ready email") row.ReadyEmailStatus = "UNKNOWN WRITE OUTCOME";
            if (ex.Operation == "Copy contract document") row.ContractPdfCopyStatus = "UNKNOWN WRITE OUTCOME";
            if (ex.Operation is "Add payment schedule note" or "Update payment schedule note") row.PaymentScheduleNoteStatus = "UNKNOWN WRITE OUTCOME";
        }
        catch (JournalStorageException ex) { storageFailed = true; row.Outcome = "FAILED"; row.ServiceOrderUpdateStatus = "FAILED"; row.UpdateMessage = ex.Message; throw; }
        catch (RecoveryReviewException ex) { row.Outcome = journal?.Evidence.Stages.Any(x => x.Status is StageStatus.Unknown or StageStatus.Dispatching or StageStatus.Succeeded) == true ? "UNKNOWN" : "REVIEW"; row.ValidationStatus = "REVIEW"; row.ServiceOrderUpdateStatus = "RECOVERY REVIEW"; row.UpdateMessage = ex.Message; }
        catch (Exception ex)
        {
            var unresolved = journal?.Evidence.Stages.LastOrDefault(x => x.Status is StageStatus.Dispatching or StageStatus.Unknown or StageStatus.Succeeded);
            row.Outcome = unresolved is null ? "FAILED" : "UNKNOWN";
            row.ServiceOrderUpdateStatus = unresolved is null ? "FAILED" : "UNKNOWN WRITE OUTCOME";
            row.UpdateMessage = unresolved is null ? ex.Message : $"UNKNOWN WRITE OUTCOME: {unresolved.Operation} ({unresolved.Target}) lacks verified readback. Subsequent stages stopped; reconcile before any resubmission. {ex.Message}";
            if (unresolved?.Operation == "Send ready email") row.ReadyEmailStatus = "UNKNOWN WRITE OUTCOME";
        }
        finally
        {
            if (journal is not null && !storageFailed)
            {
                journal.Evidence.Plan = row;
                if (row.Outcome is "REVIEW" or "FAILED" or "UNKNOWN") journal.Evidence.Complete = false;
                journal.Save();
            }
            if (journal is not null) row.JournalStages = storageFailed ? "Persistence failed; consult last durable journal, not in-memory state." : StageSummary(journal.Evidence);
            gateway.MutationGuard = null;
            gateway.Journal = null;
        }
    }

    private void FinishReadyEmail(MomentusGateway gateway, ProcessingJournal journal, RunRow row)
    {
        var evidence = journal.Evidence;
        var sent = evidence.Stages.Any(x => x.Operation == "Send ready email" && x.Status == StageStatus.Verified);
        if (!sent && evidence.SendEmail)
        {
            evidence.Complete = false;
            journal.Save();
            HandoffRules.RequireOrderStages(evidence, includeActivation: true, includeEmail: false);
            var handoff = HandoffRules.Verify(gateway, row, journal);
            gateway.MutationGuard = (operation, intent) =>
            {
                if (operation != "Send ready email") throw new RecoveryReviewException("REVIEW: final handoff permits only the ready email.");
                var prepared = Newtonsoft.Json.JsonConvert.SerializeObject(evidence.Handoff);
                // Verify includes the fresh PlanGuard check; repeating it separately downloads the contracts twice.
                var current = HandoffRules.Verify(gateway, row, journal);
                if (prepared != Newtonsoft.Json.JsonConvert.SerializeObject(current))
                    throw new RecoveryReviewException("REVIEW: verified handoff changed while preparing the email; stale send stopped.");
                return intent;
            };
            row.ReadyEmailStatus = gateway.SendReadyForInvoicingEmail(row, handoff.Attachments);
        }
        else row.ReadyEmailStatus = sent ? "ALREADY SENT (JOURNAL VERIFIED)" : "SKIPPED BY OPTION";
        gateway.VerifyUndispatched("Send ready email");
        HandoffRules.RequireOrderStages(evidence, includeActivation: true);
        evidence.Complete = evidence.Stages.All(x => x.Status == StageStatus.Verified);
        row.Outcome = "SUCCESS";
        journal.Save();
    }

    private void FinishExhibitorActivation(MomentusGateway gateway, ProcessingJournal journal, RunRow row)
    {
        var evidence = journal.Evidence;
        row.ServiceOrderUpdateStatus = "COMPLETED";
        row.ValidationStatus = "READY";
        row.UpdateMessage = "Required order-level stages are Verified.";
        if (!evidence.ActivateExhibitor) { evidence.ExhibitorActivationPending = false; row.ExhibitorActivationPending = false; journal.Save(); return; }
        evidence.ExhibitorActivationPending = true;
        row.ExhibitorActivationPending = true;
        journal.Save();
        var decision = ActivationRules.Check(gateway, evidence, journalStore!.Load());
        if (decision.Allowed)
        {
            evidence.Complete = false; // The upcoming shared activation stage must be journaled before dispatch.
            journal.Save();
            gateway.MutationGuard = (operation, intent) => operation == "Activate exhibitor"
                ? ActivationRules.BeforeWrite(gateway, evidence, journalStore.Load())
                : throw new RecoveryReviewException("REVIEW: completed order recovery permits only the pending shared exhibitor activation.");
            decision.Exhibitor.ExhibitorStatus = 2;
            gateway.UpdateExhibitor(decision.Exhibitor);
            decision = ActivationRules.Check(gateway, evidence, journalStore.Load());
            if (!decision.AlreadyVerified) throw new RecoveryReviewException("REVIEW: group activation readback is not Verified.");
        }
        evidence.Complete = evidence.Stages.All(x => x.Status == StageStatus.Verified) &&
            (!evidence.SendEmail || evidence.Stages.Any(x => x.Operation == "Send ready email" && x.Status == StageStatus.Verified));
        evidence.ExhibitorActivationPending = !decision.AlreadyVerified;
        row.ExhibitorActivationPending = evidence.ExhibitorActivationPending;
        row.ExhibitorStatusAction = decision.AlreadyVerified ? "ACTIVE (GROUP VERIFIED)" : "DEFERRED: " + decision.Message;
        row.UpdateMessage += " " + decision.Message;
        if (decision.AlreadyVerified)
        {
            foreach (var peer in ActivationRules.Group(journalStore.Load(), evidence.Identity).Where(x => x.Identity != evidence.Identity && x.ExhibitorActivationPending))
            {
                peer.ExhibitorActivationPending = false;
                peer.Plan.ExhibitorActivationPending = false;
                peer.Plan.ExhibitorStatusAction = "ACTIVE (GROUP VERIFIED)";
                journalStore.Save(peer);
                foreach (var processed in rows.Where(x => x.EventId == peer.Identity.Event && x.ExhibitorId == peer.Identity.Exhibitor && x.OrderNumber == peer.Identity.Order))
                { processed.ExhibitorActivationPending = false; processed.ExhibitorStatusAction = "ACTIVE (GROUP VERIFIED)"; }
            }
        }
        journal.Save();
    }

    private static void VerifyOrder(ServiceOrdersModel order, RunRow row, bool activated)
    {
        if (!TextRules.Same(order.OrderAccountRep, row.ProposedOrderAccountRep) || Value(order.Category) != row.ProposedCategory ||
            !TextRules.Same(order.BoothNumber, row.ProposedBoothNumber) || !TextRules.Same(order.BillToAccount, row.FinalBillToAccount) ||
            !TextRules.Same(order.BillToContact, row.FinalBillToContact) || activated && !TextRules.Same(order.OrderStatus, "A"))
            throw new RecoveryReviewException("REVIEW: current service-order values differ from verified intended values; subsequent mutations stopped.");
    }
    private static int Value(object? x) => x is null ? 0 : Convert.ToInt32(x, CultureInfo.InvariantCulture);
    private static DateTime? DateValue(object? x) => x is null ? null : Convert.ToDateTime(x, CultureInfo.InvariantCulture);
    private static string First(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";
}

internal sealed class RunLock : IDisposable
{
    private readonly FileStream stream; private readonly string path;
    private RunLock(FileStream stream, string path) { this.stream = stream; this.path = path; }
    public static RunLock Acquire(string folder)
    {
        Directory.CreateDirectory(folder); var path = Path.Combine(folder, "ServiceOrderEntry.lock");
        try { return new RunLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), path); }
        catch (IOException ex) { throw new InvalidOperationException("Another ServiceOrderEntry run is active.", ex); }
    }
    // Keep the lock file: deleting after close can race a new owner and unlink its lock.
    public void Dispose() => stream.Dispose();
}
