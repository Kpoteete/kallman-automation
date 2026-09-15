using System.Text.Json;
using BoothAvailabilitySync;

var mode = args.Length == 0 ? "preview" : args[0].Trim().ToLowerInvariant();
if (mode is not ("preview" or "sync"))
{
    Console.Error.WriteLine("Usage: BoothAvailabilitySync.exe [preview|sync]");
    return 64;
}

var configPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Configuration file not found: {configPath}");
    return 2;
}

AppSettings settings;
try
{
    settings = JsonSerializer.Deserialize<AppSettings>(
        File.ReadAllText(configPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("appsettings.json could not be parsed.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not load appsettings.json: {ex.Message}");
    return 2;
}

var stateFolder = Path.GetFullPath(settings.StateFolder, Directory.GetCurrentDirectory());
using var log = new AppLogger(stateFolder);
var cancellationToken = CancellationToken.None;

try
{
    log.Info("============================================================");
    log.Info($"KWI Booth Availability Sync - {mode.ToUpperInvariant()}");
    log.Info("============================================================");
    log.Info($"Input CSV: {settings.InputFile}");

    var reader = new CsvBoothReader();
    var snapshot = reader.Read(settings.InputFile, log);
    var stateStore = new StateStore(stateFolder);
    var previous = stateStore.Load();

    PrintCsvSummary(snapshot, log);

    var safetyMessages = SafetyValidator.Validate(snapshot, previous, settings.Safety);
    foreach (var message in safetyMessages)
    {
        if (message.Fatal)
            log.Error("SAFETY: " + message.Message);
        else
            log.Warn("SAFETY: " + message.Message);
    }

    if (safetyMessages.Any(x => x.Fatal) && mode == "sync")
    {
        log.Error("Sync stopped by safety validation. No SharePoint changes were made.");
        return 3;
    }

    if (safetyMessages.Any(x => x.Fatal) && mode == "preview")
        log.Warn("Preview will continue despite safety failures; SYNC mode would stop here.");

    var credential = AuthFactory.Create(settings.Auth);
    using var graph = new GraphSharePointClient(credential, log);

    log.Info("Connecting to Microsoft Graph / SharePoint...");
    var siteId = await graph.ResolveSiteIdAsync(
        settings.SharePoint.Hostname,
        settings.SharePoint.SitePath,
        cancellationToken);

    var list = await graph.ResolveListAsync(
        siteId,
        settings.SharePoint.ListName,
        cancellationToken);

    var columns = await graph.GetColumnsAsync(siteId, list.Id, cancellationToken);
    var resolvedColumns = GraphSharePointClient.ResolveRequiredColumns(columns, settings.SharePoint);
    var items = await graph.GetItemsAsync(siteId, list.Id, cancellationToken);

    log.Info($"SharePoint site: {settings.SharePoint.SitePath}");
    log.Info($"SharePoint list: {list.DisplayName}");
    log.Info($"SharePoint items read: {items.Count:N0}");

    var syncTimestamp = DateTimeOffset.UtcNow;
    var plan = SyncPlanner.Build(
        snapshot.MetricsByEvent,
        items,
        resolvedColumns,
        settings.SharePoint,
        syncTimestamp);

    PrintPlanSummary(plan, log);
    PrintMetricChanges(plan, log);
    PrintMismatchSamples(plan, log);

    if (plan.MatchedEvents == 0)
    {
        log.Error("No Event IDs matched between the CSV and SharePoint. Live sync is blocked to prevent an accidental bad run.");
        if (mode == "sync")
            return 3;
    }

    if (mode == "preview")
    {
        log.Info("PREVIEW COMPLETE. No SharePoint values were changed.");
        log.Info($"Log: {log.LogPath}");
        return plan.DuplicateSharePointEventIds.Count > 0 ? 4 : 0;
    }

    if (plan.DuplicateSharePointEventIds.Count > 0)
        log.Error("Duplicate Event IDs exist in SharePoint. Those events will be skipped and the run will be marked partial.");

    var failures = new List<string>();
    var succeeded = 0;

    foreach (var update in plan.Updates)
    {
        try
        {
            await graph.UpdateItemFieldsAsync(
                siteId,
                list.Id,
                update.SharePointItemId,
                update.FieldsToWrite,
                cancellationToken);
            succeeded++;
        }
        catch (Exception ex)
        {
            failures.Add(update.EventId);
            log.Error($"Event {update.EventId} failed: {ex.Message}");
        }
    }

    log.Info($"SharePoint items updated successfully: {succeeded:N0}");
    if (failures.Count > 0)
        log.Error($"SharePoint update failures: {failures.Count:N0} ({string.Join(", ", failures.Take(20))}{(failures.Count > 20 ? ", ..." : "")})");

    var partial = failures.Count > 0 || plan.DuplicateSharePointEventIds.Count > 0;
    if (!partial)
    {
        stateStore.Save(snapshot.Summary);
        log.Info("Saved successful-run safety baseline to state\\last-success.json.");
        log.Info("SYNC COMPLETE.");
        log.Info($"Log: {log.LogPath}");
        return 0;
    }

    log.Error("SYNC COMPLETED WITH ERRORS. Safety baseline was not advanced.");
    log.Info($"Log: {log.LogPath}");
    return 4;
}
catch (Exception ex)
{
    log.Error(ex.ToString());
    log.Error("RUN FAILED. No successful-run safety baseline was written.");
    log.Info($"Log: {log.LogPath}");
    return 1;
}

static void PrintCsvSummary(CsvSnapshot snapshot, AppLogger log)
{
    var s = snapshot.Summary;
    log.Info($"CSV raw rows: {s.RawRows:N0}");
    log.Info($"Unique Event + Booth records after newest-ChangedOn dedupe: {s.UniqueBooths:N0}");
    log.Info($"Events represented after dedupe: {s.EventCount:N0}");
    log.Info($"Available Booths (AV): {s.AvailableBooths:N0}");
    log.Info($"Available Area (AV): {s.AvailableArea:N2}");
    log.Info($"Sold Booths (RE/30): {s.SoldBooths:N0}");
    log.Info($"Area Sold (RE/30): {s.AreaSold:N2}");
    log.Info($"Booths on Hold (20): {s.BoothsOnHold:N0}");
    log.Info($"Statuses seen: {string.Join(", ", s.StatusesSeen.Select(x => string.IsNullOrWhiteSpace(x) ? "<blank>" : x))}");
}

static void PrintPlanSummary(SyncPlan plan, AppLogger log)
{
    log.Info("-------------------- PLAN SUMMARY --------------------");
    log.Info($"Events matched between CSV and SharePoint: {plan.MatchedEvents:N0}");
    log.Info($"Events with metric changes: {plan.EventsWithMetricChanges:N0}");
    log.Info($"Matched events with no metric changes: {plan.EventsWithNoMetricChanges:N0}");
    log.Info($"CSV events not found in SharePoint: {plan.CsvEventsMissingInSharePoint.Count:N0}");
    log.Info($"SharePoint events not found in CSV: {plan.SharePointEventsMissingInCsv.Count:N0}");
    log.Info($"Duplicate Event IDs in SharePoint: {plan.DuplicateSharePointEventIds.Count:N0}");
    log.Info($"SharePoint items that would be written (includes timestamp refreshes): {plan.Updates.Count:N0}");
}

static void PrintMetricChanges(SyncPlan plan, AppLogger log)
{
    var changed = plan.Updates.Where(x => x.MetricChanges.Count > 0).ToList();
    if (changed.Count == 0)
    {
        log.Info("No booth metric values need to change.");
        return;
    }

    log.Info("-------------------- METRIC CHANGES --------------------");
    foreach (var update in changed)
    {
        log.Info($"Event {update.EventId}");
        foreach (var change in update.MetricChanges)
        {
            var oldText = change.OldValue.HasValue
                ? FormatValue(change.OldValue.Value, change.IsInteger)
                : "<blank>";
            var newText = FormatValue(change.NewValue, change.IsInteger);
            log.Info($"  {change.Label}: {oldText} -> {newText}");
        }
    }
}

static void PrintMismatchSamples(SyncPlan plan, AppLogger log)
{
    if (plan.CsvEventsMissingInSharePoint.Count > 0)
        log.Warn($"CSV Event IDs missing from SharePoint (first 20): {string.Join(", ", plan.CsvEventsMissingInSharePoint.Take(20))}");

    if (plan.SharePointEventsMissingInCsv.Count > 0)
        log.Warn($"SharePoint Event IDs missing from CSV (first 20): {string.Join(", ", plan.SharePointEventsMissingInCsv.Take(20))}");

    if (plan.DuplicateSharePointEventIds.Count > 0)
        log.Error($"Duplicate SharePoint Event IDs (first 20): {string.Join(", ", plan.DuplicateSharePointEventIds.Take(20))}");
}

static string FormatValue(decimal value, bool integer) =>
    integer ? decimal.Truncate(value).ToString("N0") : value.ToString("N2");
