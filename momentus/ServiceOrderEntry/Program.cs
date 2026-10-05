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
    string SalesRepLookupPath, string CategoryLookupPath, string EnabledEventsPath, int PageSize, int MaxResults, int MaxUpdates, int RequestDelayMs,
    int? ExhibitorId, int? EventId)
{
    public BillingConfiguration Billing { get; init; } = new();
    public string BillingConfigurationPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "billing-config.json");
    public const string HelpText = """
ServiceOrderEntry

Usage:
  ServiceOrderEntry.exe preview --exhibitor ID [--event ID]
  ServiceOrderEntry.exe apply --confirm-service-order-entry --exhibitor ID [options]
  ServiceOrderEntry.exe apply --confirm-service-order-entry --all [--max-updates 1-10]

Preview is the default and never writes to Momentus. Initial live testing must be
scoped to exactly one exhibitor. Eligible records require exhibitor status 35
(Online Booth Order), main-exhibitor type ME, and service-order status PC.

Options:
  --exhibitor ID                 Scope the run to one exhibitor (required for apply).
  --all                          Apply to all eligible records, capped at 10 attempts.
  --event ID                     Optionally scope that exhibitor to one event.
  --activate-order               After a successful field update, change order PC to A.
  --activate-exhibitor           After a successful field update, change exhibitor 35 to 2.
  --skip-ready-email             Do not send the normally enabled ready-for-invoicing email.
  --confirm-service-order-entry  Required with apply mode.
  --max-updates N                Maximum orders updated in one apply run (default: 1).
  --state-folder PATH            Preview only; live state is under ProgramData/Kallman/ServiceOrderEntry/state.
  --run-folder PATH              CSV run-file folder (default: ./runs).
  --sales-rep-lookup PATH        Default: ./SalesRepLookup.xlsx.
  --category-lookup PATH         Default: ./OrderCategoryLookup.xlsx.
  --billing-config PATH          Explicit billing UDF identity and tenant Not Applicable status (default: ./billing-config.json).
  --enabled-events PATH         Only these Event IDs may be processed (default: ./enabled-events.txt).
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
            Path.Combine(root, "enabled-events.txt"), 1000, 100000, 1, 100, null, null);
        var i = 0;
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            o = args[0].ToLowerInvariant() switch { "preview" => o, "apply" => o with { Apply = true }, _ => throw new CliException($"Unknown mode '{args[0]}'.") };
            i++;
        }
        string Next() { if (++i >= args.Length) throw new CliException($"Missing value after {args[i - 1]}."); return args[i]; }
        for (; i < args.Length; i++) o = args[i].ToLowerInvariant() switch
        {
            "--confirm-service-order-entry" => o with { Confirm = true }, "--activate-order" => o with { ActivateOrder = true },
            "--activate-exhibitor" => o with { ActivateExhibitor = true }, "--help" or "-h" or "/?" => o with { Help = true },
            "--skip-ready-email" => o with { SendReadyEmail = false },
            "--all" => o with { All = true },
            "--org" => o with { OrganizationCode = Next().Trim() }, "--base-url" => o with { BaseUrl = Next().Trim().TrimEnd('/') },
            "--state-folder" => o with { StateFolder = Path.GetFullPath(Next()) }, "--run-folder" => o with { RunFolder = Path.GetFullPath(Next()) },
            "--sales-rep-lookup" => o with { SalesRepLookupPath = Path.GetFullPath(Next()) }, "--category-lookup" => o with { CategoryLookupPath = Path.GetFullPath(Next()) },
            "--enabled-events" => o with { EnabledEventsPath = Path.GetFullPath(Next()) },
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
        if (!o.Apply && (o.ActivateOrder || o.ActivateExhibitor)) throw new CliException("Status-change switches are valid only in apply mode.");
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

internal static class EnabledEventList
{
    public static IReadOnlySet<int> Load(string path)
    {
        if (!File.Exists(path)) return new HashSet<int>();
        var result = new HashSet<int>();
        var lines = File.ReadAllLines(path);
        for (var index = 0; index < lines.Length; index++)
        {
            var value = lines[index].Split('#', 2)[0].Trim();
            if (value.Length == 0) continue;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId) || eventId <= 0)
                throw new CliException($"Invalid Event ID '{value}' in {path} at line {index + 1}.");
            result.Add(eventId);
        }
        return result;
    }
}

internal sealed class Runner(CliOptions options)
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
        journalStore = new FileJournalStore(options.StateFolder);
        existing = journalStore.Load();
        CsvRunWriter.Preflight(options.RunFolder); // Fail closed before any API writes or discovery.
        Console.WriteLine(options.Apply ? "LIVE MODE: eligible READY orders may be updated." : "PREVIEW MODE: no Momentus record will be changed.");
        Console.WriteLine($"Order activation: {(options.ActivateOrder ? "ON" : "OFF")}; Exhibitor activation: {(options.ActivateExhibitor ? "ON" : "OFF")}.");
        Console.WriteLine($"Ready-for-invoicing email to kylep@kallman.com: {(options.Apply && options.SendReadyEmail ? "ON" : options.Apply ? "OFF" : "PREVIEW ONLY")}.");
        var enabledEvents = EnabledEventList.Load(options.EnabledEventsPath);
        Console.WriteLine(enabledEvents.Count == 0
            ? "Enabled events: NONE (no orders can be processed)."
            : $"Enabled events: {string.Join(", ", enabledEvents.Order())}.");
        if (options.ExhibitorId.HasValue)
            Console.WriteLine($"INDIVIDUAL TEST BYPASS: exhibitor {options.ExhibitorId.Value} may be evaluated outside the enabled-event list.");
        var reps = LookupLoader.LoadSalesReps(options.SalesRepLookupPath);
        var categories = LookupLoader.LoadCategories(options.CategoryLookupPath);
        var gateway = new MomentusGateway(options);
        var candidates = DiscoverWork(gateway, existing, options, enabledEvents);
        Console.WriteLine($"Eligible Online Booth Order / Pending Completion records found: {candidates.Count:N0}.");
        return ProcessCandidates(gateway, candidates, reps, categories);
    }

    internal int ProcessCandidates(MomentusGateway gateway, IReadOnlyList<Candidate> candidates, IReadOnlyList<SalesRepLookup> reps, IReadOnlyList<CategoryLookup> categories)
    {
        CsvRunWriter.Preflight(options.RunFolder);
        if (options.Apply)
        {
            // Retain every intended order, including those deferred by the attempt cap or later made active externally.
            foreach (var candidate in candidates)
            {
                var identity = new OrderIdentity(new Uri(options.BaseUrl).AbsoluteUri.TrimEnd('/'), options.OrganizationCode,
                    Value(candidate.Exhibitor.Event), Value(candidate.Exhibitor.ExhibitorID), Value(candidate.Order.OrderNumber));
                if (journalStore!.Load().Any(x => x.Identity == identity)) continue;
                journalStore.Save(new OrderEvidence { Identity = identity, CreatedRunId = runId, SendEmail = options.SendReadyEmail,
                    ActivateOrder = options.ActivateOrder, ActivateExhibitor = options.ActivateExhibitor,
                    Plan = new RunRow { EventId = identity.Event, ExhibitorId = identity.Exhibitor, OrderNumber = identity.Order, Outcome = "PENDING", ValidationStatus = "PREPARED" } });
            }
        }
        var updated = 0;
        var attempted = 0;
        foreach (var candidate in candidates)
        {
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
            row.OrderStatusAction = options.ActivateOrder ? (row.ApprovalNeeded ? "BLOCKED BY APPROVAL NEEDED" : "CHANGE TO ACTIVE (A)") : "NO CHANGE (REMAINS PC)";
            row.ExhibitorStatusAction = options.ActivateExhibitor ? (row.ApprovalNeeded ? "BLOCKED BY APPROVAL NEEDED" : "CHANGE TO ACTIVE (2)") : "NO CHANGE (REMAINS 35)";
            row.RunId = runId;
            rows.Add(row);
            if (options.Apply && row.ValidationStatus is "READY" or "PREPARED" && attempted < options.MaxUpdates)
            {
                attempted++;
                try { Apply(gateway, candidate, row); }
                catch (JournalStorageException)
                {
                    try { Console.Error.WriteLine($"Run halted for journal storage failure. Run file: {CsvRunWriter.Write(options.RunFolder, started, rows, options.Apply, runId)}"); }
                    catch (Exception reportError) { Console.Error.WriteLine($"Run report could not be saved: {reportError.Message}"); }
                    throw;
                }
                if (row.ServiceOrderUpdateStatus == "COMPLETED") updated++;
            }
            else if (!options.Apply && row.ProcessingId.Length == 0) row.ServiceOrderUpdateStatus = row.ValidationStatus is "READY" or "PREPARED" ? "WOULD UPDATE" : "NOT ATTEMPTED";
            else if (attempted >= options.MaxUpdates)
            {
                row.UpdateMessage = $"Apply limit of {options.MaxUpdates} attempts reached; no write attempted.";
                if (options.Apply && row.ValidationStatus is "READY" or "PREPARED")
                { row.Outcome = "REVIEW"; row.ServiceOrderUpdateStatus = "DEFERRED (ATTEMPT CAP)"; }
            }
        }
        var path = CsvRunWriter.Write(options.RunFolder, started, rows, options.Apply, runId);
        Console.WriteLine($"READY: {rows.Count(x => x.ValidationStatus == "READY"):N0}; PREPARED: {rows.Count(x => x.ValidationStatus == "PREPARED"):N0}; REVIEW: {rows.Count(x => x.ValidationStatus == "REVIEW"):N0}; ATTEMPTED: {attempted:N0}; COMPLETED: {updated:N0}.");
        Console.WriteLine($"Outcomes: SUCCESS {rows.Count(x => x.Outcome == "SUCCESS"):N0}; REVIEW {rows.Count(x => x.Outcome == "REVIEW"):N0}; FAILED {rows.Count(x => x.Outcome == "FAILED"):N0}; UNKNOWN {rows.Count(x => x.Outcome == "UNKNOWN"):N0}; shared activation pending {rows.Count(x => x.ExhibitorActivationPending):N0}.");
        Console.WriteLine($"API requests: {gateway.RequestCount:N0}. Run file: {path}");
        return ApplyExitCode(rows);
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

    internal static IEnumerable<OrderEvidence> RecoveryWork(IEnumerable<OrderEvidence> records, CliOptions options, IReadOnlySet<int> enabledEvents) => records.Where(x =>
        (!x.Complete || x.ExhibitorActivationPending) && x.Identity.Endpoint == new Uri(options.BaseUrl).AbsoluteUri.TrimEnd('/') && x.Identity.Organization == options.OrganizationCode &&
        (!options.ExhibitorId.HasValue || x.Identity.Exhibitor == options.ExhibitorId.Value) && (!options.EventId.HasValue || x.Identity.Event == options.EventId.Value) &&
        EventScopeRules.IsAllowed(x.Identity.Event, enabledEvents, options.ExhibitorId.HasValue));

    internal static IReadOnlyList<Candidate> DiscoverWork(MomentusGateway gateway, IEnumerable<OrderEvidence> records, CliOptions options, IReadOnlySet<int> enabledEvents)
    {
        var candidates = gateway.FindCandidates(enabledEvents).ToList();
        foreach (var evidence in RecoveryWork(records, options, enabledEvents))
        {
            if (candidates.Any(x => Value(x.Order.OrderNumber) == evidence.Identity.Order)) continue;
            candidates.Add(new Candidate(gateway.GetOrder(evidence.Identity.Order), gateway.GetExhibitor(evidence.Identity.Exhibitor)));
        }
        return candidates;
    }

    // Injection is restricted to the offline orchestration seam; production Run always uses canonical storage and the lock.
    internal void UseJournalStore(IJournalStore store) { journalStore = store; existing = store.Load(); }

    internal static RunRow Evaluate(MomentusGateway gateway, Candidate candidate, IReadOnlyList<SalesRepLookup> reps, IReadOnlyList<CategoryLookup> categories)
    {
        var order = candidate.Order;
        var exhibitor = candidate.Exhibitor;
        var orderNumber = Value(order.OrderNumber);
        var exhibitorId = Value(exhibitor.ExhibitorID);
        var eventId = Value(exhibitor.Event);
        var sourceAccount = gateway.GetAccountModel(order.Account ?? "");
        var request = MomentusGateway.BillingFrom(sourceAccount, gateway.BillingConfiguration);
        var billAccount = gateway.GetAccount(order.BillToAccount ?? "");
        var billContact = gateway.GetAccount(order.BillToContact ?? "");
        var udfRep = MomentusGateway.ExhibitorSalesRepUdf(exhibitor);
        var items = gateway.GetOrderItems(orderNumber);
        var category = CategoryRules.Resolve(items, categories);
        var exhibitorCategories = ExhibitorCategoryRules.Resolve(exhibitor.ExhibitorCategory, category.Description, items, MomentusGateway.ExhibitorStatePavilionUdf(exhibitor));
        var contracts = gateway.ResolveContracts(exhibitorId, orderNumber, DateValue(order.OrderDate));
        var exhibitorDocuments = contracts.Sources.Select(x => x.Document).ToList();
        var orderDocuments = contracts.OrderDocuments.Select(x => x.Document).ToList();
        var copiesNeeded = exhibitorDocuments.GroupBy(x => x.ContentHash).Count(g => !HasMatchingDocument(orderDocuments, g.First()));
        var paymentSchedule = contracts.PaymentSchedule;
        var noteDecision = ManagedNoteRules.Resolve(gateway.GetOrderSonNotes(orderNumber));
        var sonAction = noteDecision.Action == "OWNED" ?
            (ManagedNoteRules.ContentHash(ManagedNoteRules.Text(noteDecision.Note!)) == ManagedNoteRules.ContentHash(paymentSchedule) ? "KEEP EXISTING" : "UPDATE") : noteDecision.Action;
        var booth = gateway.GetBoothEvidence(exhibitorId, eventId);
        var row = new RunRow
        {
            BillingVersion = 1, BillingSourceAccount = order.Account ?? "", BillingConfiguration = gateway.BillingConfiguration,
            ValidCategoryIds = categories.Select(x => x.Sequence).ToList(),
            EventId = eventId, ExhibitorId = exhibitorId, OrderNumber = orderNumber,
            ExhibitorName = First(exhibitor.CompanyBannerName, exhibitor.CompanyName, exhibitor.AccountCode), OrderDate = DateValue(order.OrderDate),
            ExhibitorSalesRep = string.IsNullOrWhiteSpace(udfRep) ? exhibitor.Salesperson?.Trim() ?? "" : udfRep,
            ProposedOrderAccountRep = SalesRepRules.Resolve(udfRep, exhibitor.Salesperson, reps),
            ServiceOrderItemIdentifier = category.ItemIdentifier, ProposedCategory = category.Sequence, ProposedCategoryName = category.Description,
            ExistingExhibitorCategories = ExhibitorCategoryRules.Format(exhibitorCategories.Existing),
            ExhibitorCategoriesToAdd = ExhibitorCategoryRules.Format(exhibitorCategories.Add),
            ExhibitorCategoriesToRemove = ExhibitorCategoryRules.Format(exhibitorCategories.Remove),
            FinalExhibitorCategories = ExhibitorCategoryRules.Format(exhibitorCategories.Final), ApprovalNeeded = exhibitorCategories.ApprovalNeeded,
            ContractPdfCount = exhibitorDocuments.Count, ContractPdfCopiesNeeded = copiesNeeded,
            ContractPdfCopyStatus = contracts.NearbyFallback && exhibitorDocuments.Count > 0
                ? $"WOULD COPY {copiesNeeded} FALLBACK PDF(S)"
                : copiesNeeded == 0 ? "ALREADY COPIED OR NONE FOUND" : "WOULD COPY",
            Contracts = contracts, ManagedNoteSequence = noteDecision.Note is null ? null : Value(noteDecision.Note.SequenceNumber),
            ManagedNoteContentHash = noteDecision.Note is null ? "" : ManagedNoteRules.ContentHash(ManagedNoteRules.Text(noteDecision.Note)),
            PaymentScheduleText = paymentSchedule, PaymentScheduleNoteAction = sonAction,
            PaymentScheduleDecisionMessage = string.Join(" ", new[] { contracts.ReviewMessage, noteDecision.Message }.Where(x => x.Length > 0)),
            ReadyEmailRecipient = "kylep@kallman.com",
            ExistingBillToAccount = billAccount, ExistingBillToContact = billContact, RequestedBilling = request,
            FinalBillToAccount = billAccount.AccountCode, ProposedBoothNumber = booth.Booth,
            DecisionInputs = PlanGuard.Capture(order, exhibitor, items, booth, categories)
        };
        row.ReadyEmailStatus = gateway.ReadyEmailWasSent(orderNumber) ? "ALREADY SENT" : "WOULD SEND";
        DecideAccountAndAddress(gateway, row);
        DecideContact(gateway, row);
        var validation = ValidationRules.Validate(row);
        row.ValidationStatus = validation.Status;
        row.ValidationMessage = string.Join(" ", new[] { category.Message, validation.Message }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return row;
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
        BillingState.Set(row, new(row.ExistingBillToAccount, row.ExistingBillToContact, "Existing order", "Existing order", false));
        var selector = config.Selector(row.RequestedBilling);
        if (selector == "ABOVE")
        {
            row.BillingRequestBypassed = true;
            row.BillToAddressAction = "KEEP EXISTING ABOVE ADDRESS";
            row.ContactAction = "KEEP EXISTING";
            row.BillToContactMatchResult = "ABOVE ADDRESS - KEEP EXISTING";
            BillingState.Set(row, row.EffectiveBilling! with { AboveAddress = true });
            if (!TextRules.Same(row.BillingSourceAccount, row.ExistingBillToAccount.AccountCode))
                row.AccountDecisionMessage = "Use Above Address conflicts with the order's Bill-To account; REVIEW without reassignment.";
            if (!TextRules.Same(row.ExistingBillToContact.PrimaryAccount, row.ExistingBillToAccount.AccountCode))
                row.ContactDecisionMessage = "Existing above-address contact parent is conflicting or unavailable; REVIEW.";
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
        try { match = AccountIdentityRules.Resolve(gateway.FindOrganizationCandidates(row.RequestedBilling), row.RequestedBilling); }
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
                BillingState.Set(row, row.EffectiveBilling! with { Account = BillingRules.RequestedAccount(row.RequestedBilling),
                    AccountSource = "New separate account", AccountCreationPending = true });
                break;
            default:
                row.BillToAddressAction = "REVIEW";
                break;
        }
    }

    internal static void DecideContact(MomentusGateway gateway, RunRow row)
    {
        if (gateway.BillingConfiguration.Selector(row.RequestedBilling) == "ABOVE")
        {
            row.BillToContactMatchResult = "ABOVE ADDRESS - KEEP EXISTING";
            row.ContactAction = "KEEP EXISTING";
            row.MatchingContactCode = row.ExistingBillToContact.AccountCode;
            row.FinalBillToContact = row.ExistingBillToContact.AccountCode;
            return;
        }
        if (row.EffectiveBilling is null || row.AccountDecisionMessage.Length > 0 ||
            BillingRules.RequestedErrors(row.RequestedBilling, gateway.BillingConfiguration).Count > 0) return;
        var requested = TextRules.NormalizeEmail(row.RequestedBilling.Email);
        var scoped = string.IsNullOrWhiteSpace(row.FinalBillToAccount) ? [] : gateway.FindContacts(row.FinalBillToAccount, requested);
        // Current stable ID wins only if it is a valid contact on the intended account and present in the complete result.
        var selected = SelectContact(scoped, row.ExistingBillToContact.AccountCode);
        if (selected is not null)
        {
            row.BillToContactMatchResult = "ACCOUNT CONTACT MATCH";
            row.ContactAction = "USE EXISTING CONTACT";
            row.MatchingContactCode = selected.AccountCode;
            BillingState.Set(row, row.EffectiveBilling with { Contact = selected, ContactSource = "Reused separate contact", ContactCreationPending = false });
            return;
        }
        if (scoped.Count > 0)
        {
            row.ContactAction = "REVIEW";
            row.ContactDecisionMessage = "Multiple or invalid contacts use the billing email on the intended account; REVIEW without creating another contact.";
            return;
        }
        var global = gateway.FindContacts(null, requested);
        if (global.Count > 0)
        {
            row.ContactAction = "REVIEW";
            row.ContactDecisionMessage = "Billing email exists on another account; cross-account contact reuse/movement is not authorized. REVIEW.";
            return;
        }
        row.BillToContactMatchResult = "NO MATCH";
        row.ContactAction = "CREATE CONTACT";
        BillingState.Set(row, row.EffectiveBilling with { Contact = BillingRules.RequestedContact(row.RequestedBilling),
            ContactSource = "New separate contact", ContactCreationPending = true });
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
                HandoffRules.RequireOrderStages(evidence, includeActivation: true);
                HandoffRules.VerifyRetained(gateway, evidence);
                var retainedExhibitor = gateway.GetExhibitor(row.ExhibitorId);
                if (retainedExhibitor.OrganizationCode != evidence.Identity.Organization || retainedExhibitor.ExhibitorID != evidence.Identity.Exhibitor ||
                    retainedExhibitor.Event != evidence.Identity.Event || ExhibitorCategoryRules.HasCode(retainedExhibitor.ExhibitorCategory, ExhibitorCategoryRules.Hold))
                    throw new RecoveryReviewException("REVIEW: completed order's current exhibitor identity/Hold blocks continued processing.");
                row.Outcome = "SUCCESS";
                evidence.Plan = row;
                evidence.Complete = evidence.Stages.All(x => x.Status == StageStatus.Verified);
                journal.Save();
                FinishExhibitorActivation(gateway, journal, row);
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
                Value(currentExhibitor.ExhibitorStatus) != (Verified("Activate exhibitor") ? 2 : 35))
            {
                if (previous is not null) throw new RecoveryReviewException("REVIEW: current statuses conflict with journaled eligibility/activation evidence.");
                throw new RecoveryReviewException("REVIEW: record status changed after evaluation (expected order PC and exhibitor 35).");
            }

            if (row.Contracts is null || row.Contracts.ReviewMessage.Length > 0 || row.Contracts.ScheduleSource is null ||
                ManagedNoteRules.ContentHash(row.Contracts.PaymentSchedule) != ManagedNoteRules.ContentHash(row.PaymentScheduleText))
                throw new RecoveryReviewException("REVIEW: contract/schedule identity is unresolved or conflicts with the saved plan.");
            BillingGuard.Preflight(gateway, row, currentOrder, journal);
            gateway.MutationGuard = (operation, intent) =>
            {
                if (operation == "Activate exhibitor") return ActivationRules.BeforeWrite(gateway, evidence, journalStore.Load());
                var guarded = PlanGuard.BeforeWrite(gateway, row, operation, intent);
                if (operation == "Send ready email")
                {
                    var prepared = Newtonsoft.Json.JsonConvert.SerializeObject(evidence.Handoff);
                    var current = HandoffRules.Verify(gateway, row, journal);
                    if (prepared != Newtonsoft.Json.JsonConvert.SerializeObject(current))
                        throw new RecoveryReviewException("REVIEW: verified handoff changed while preparing the email; stale send stopped.");
                }
                return guarded;
            };
            _ = PlanGuard.BeforeWrite(gateway, row, "Preflight", new object());
            _ = HandoffRules.PreflightAttachments(gateway, row);
            journal.Save();
            var originalBillToAccount = row.ExistingBillToAccount.AccountCode;
            if (row.BillToAddressAction == "CREATE RELATED BILL-TO ACCOUNT")
            {
                var proven = journal.CreatedAccount("Create organization account");
                var recheck = proven is null ? gateway.FindOrganizationCandidates(row.RequestedBilling) : [];
                if (recheck.Count > 0) throw new RecoveryReviewException("REVIEW: account creation stopped because one or multiple duplicate candidates were found; re-evaluate billing.");
                BillingGuard.Instructions(gateway, row);
                row.FinalBillToAccount = proven ?? gateway.CreateOrganizationAccount(row.RequestedBilling);
                evidence.Plan.FinalBillToAccount = row.FinalBillToAccount;
                journal.Save();
                BillingGuard.VerifyCreatedAccount(gateway, row, row.FinalBillToAccount);
                journal.Save();
                gateway.VerifyUndispatched("Create organization account");
                BillingGuard.Instructions(gateway, row);
                gateway.EnsureRelationship(originalBillToAccount, row.FinalBillToAccount, "BTO");
            }
            else if (row.BillToAddressAction == "USE MATCHED BILL-TO ACCOUNT")
            {
                BillingGuard.Instructions(gateway, row);
                BillingGuard.VerifyReusedAccount(gateway, row);
                gateway.EnsureRelationship(originalBillToAccount, row.FinalBillToAccount, "BTO");
            }

            if (row.ContactAction == "CREATE CONTACT")
            {
                if (string.IsNullOrWhiteSpace(row.RequestedBilling.FirstName) || string.IsNullOrWhiteSpace(row.RequestedBilling.LastName))
                    throw new InvalidOperationException("Contact creation requires requested first and last name.");
                var proven = journal.CreatedAccount("Create contact");
                var recheck = proven is null ? gateway.FindContacts(null, row.RequestedBilling.Email) : [];
                if (recheck.Count > 0) throw new RecoveryReviewException("REVIEW: contact creation stopped because existing contacts use the requested email; re-evaluate identity.");
                var finalCompany = gateway.GetAccount(row.FinalBillToAccount).Company;
                BillingGuard.Instructions(gateway, row);
                BillingGuard.VerifyReusedAccount(gateway, row);
                row.FinalBillToContact = proven ?? gateway.CreateContact(row.FinalBillToAccount, finalCompany, row.RequestedBilling);
                row.MatchingContactCode = row.FinalBillToContact;
                evidence.Plan.FinalBillToContact = row.FinalBillToContact;
                journal.Save();
                BillingGuard.VerifyCreatedContact(gateway, row, row.FinalBillToContact);
                journal.Save();
                gateway.VerifyUndispatched("Create contact");
            }
            else if (row.ContactAction == "USE EXISTING CONTACT")
            {
                var chosen = gateway.GetAccount(row.FinalBillToContact);
                if (!TextRules.Same(chosen.PrimaryAccount, row.FinalBillToAccount))
                    throw new RecoveryReviewException("REVIEW: cross-account contact reuse is not authorized.");
            }
            gateway.VerifyUndispatched("Add relationship");
            BillingGuard.VerifyFinal(gateway, row);
            row.ValidationStatus = "READY";
            journal.Save();
            currentOrder.OrderAccountRep = row.ProposedOrderAccountRep;
            currentOrder.Category = row.ProposedCategory;
            currentOrder.BoothNumber = row.ProposedBoothNumber;
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

            var handoff = HandoffRules.Verify(gateway, row, journal);
            row.ReadyEmailStatus = Verified("Send ready email") ? "ALREADY SENT (JOURNAL VERIFIED)" : options.SendReadyEmail
                ? gateway.SendReadyForInvoicingEmail(row, handoff.Attachments)
                : "SKIPPED BY OPTION";
            gateway.VerifyUndispatched("Send ready email");

            var activationBlocked = row.ApprovalNeeded && (options.ActivateOrder || options.ActivateExhibitor);
            if (!activationBlocked && options.ActivateOrder && !Verified("Activate order"))
            {
                HandoffRules.RequireOrderStages(evidence, includeActivation: false);
                _ = HandoffRules.Verify(gateway, row, journal);
                verifiedOrder.OrderStatus = "A";
                gateway.UpdateOrder(verifiedOrder);
                if (!TextRules.Same(gateway.GetOrder(row.OrderNumber).OrderStatus, "A")) throw new InvalidOperationException("Order status readback did not confirm Active (A).");
            }
            HandoffRules.RequireOrderStages(evidence, includeActivation: true);
            evidence.OrderComplete = true;
            evidence.Complete = true;
            row.Outcome = "SUCCESS";
            evidence.ExhibitorActivationPending = options.ActivateExhibitor;
            row.ExhibitorActivationPending = evidence.ExhibitorActivationPending;
            journal.Save();
            FinishExhibitorActivation(gateway, journal, row);
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
        evidence.Complete = evidence.Stages.All(x => x.Status == StageStatus.Verified);
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
