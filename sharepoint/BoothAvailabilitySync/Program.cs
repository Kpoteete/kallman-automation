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
    log.Info($"KWI Event Portal SharePoint Sync - {mode.ToUpperInvariant()}");
    log.Info("============================================================");
    log.Info($"Booths CSV: {settings.InputFile}");
    log.Info($"Events CSV: {settings.EventsInputFile}");

    var boothReader = new CsvBoothReader();
    var boothSnapshot = boothReader.Read(settings.InputFile, log);

    var eventReader = new CsvEventReader();
    var eventSnapshot = eventReader.Read(settings.EventsInputFile, log);

    var stateStore = new StateStore(stateFolder);
    var previous = stateStore.Load();

    PrintBoothCsvSummary(boothSnapshot, log);
    PrintEventCsvSummary(eventSnapshot, log);

    var safetyMessages = new List<SafetyMessage>();
    safetyMessages.AddRange(SafetyValidator.ValidateBooths(boothSnapshot, previous, settings.Safety));
    safetyMessages.AddRange(SafetyValidator.ValidateEvents(eventSnapshot, settings.Safety));

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
        boothSnapshot.MetricsByEvent,
        eventSnapshot.DetailsByEvent,
        items,
        resolvedColumns,
        settings.SharePoint,
        syncTimestamp);

    PrintPlanSummary(plan, log);
    PrintChanges(plan, log);
    PrintMismatchSamples(plan, log);

    if (plan.BoothMatchedEvents == 0)
    {
        log.Error("No Event IDs matched between Booths_Pull.csv and SharePoint. Live sync is blocked to prevent an accidental bad run.");
        if (mode == "sync")
            return 3;
    }

    if (plan.EventsMatchedEvents == 0)
    {
        log.Error("No Event IDs matched between Events_Pull.csv and SharePoint. Live sync is blocked to prevent an accidental bad run.");
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
        stateStore.Save(boothSnapshot.Summary);
        log.Info("Saved successful-run booth safety baseline to state\\last-success.json.");
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

static void PrintBoothCsvSummary(CsvSnapshot snapshot, AppLogger log)
{
    var s = snapshot.Summary;
    log.Info("-------------------- BOOTH SOURCE --------------------");
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

static void PrintEventCsvSummary(EventCsvSnapshot snapshot, AppLogger log)
{
    var s = snapshot.Summary;
    log.Info("-------------------- EVENT SOURCE --------------------");
    log.Info($"Events CSV raw rows: {s.RawRows:N0}");
    log.Info($"Unique Event IDs: {s.UniqueEvents:N0}");
    log.Info($"Duplicate EventID rows resolved: {s.DuplicateRows:N0}");
    log.Info($"Events with Start Date: {s.EventsWithStartDate:N0}");
    log.Info($"Events with End Date: {s.EventsWithEndDate:N0}");
    log.Info($"Events with City: {s.EventsWithCity:N0}");
    log.Info($"Events with Country: {s.EventsWithCountry:N0}");
    log.Info($"Events with Subclass: {s.EventsWithSubclass:N0}");
}

static void PrintPlanSummary(SyncPlan plan, AppLogger log)
{
    log.Info("-------------------- PLAN SUMMARY --------------------");
    log.Info($"SharePoint events matched to Booths_Pull.csv: {plan.BoothMatchedEvents:N0}");
    log.Info($"SharePoint events matched to Events_Pull.csv: {plan.EventsMatchedEvents:N0}");
    log.Info($"Events with booth metric changes: {plan.EventsWithMetricChanges:N0}");
    log.Info($"Events with event detail changes: {plan.EventsWithDetailChanges:N0}");
    log.Info($"Matched events with no field changes: {plan.EventsWithNoChanges:N0}");
    log.Info($"Duplicate Event IDs in SharePoint: {plan.DuplicateSharePointEventIds.Count:N0}");
    log.Info($"SharePoint items that would be written (includes booth timestamp refreshes): {plan.Updates.Count:N0}");
}

static void PrintChanges(SyncPlan plan, AppLogger log)
{
    var changed = plan.Updates
        .Where(x => x.MetricChanges.Count > 0 || x.DetailChanges.Count > 0)
        .ToList();

    if (changed.Count == 0)
    {
        log.Info("No SharePoint field values need to change.");
        return;
    }

    log.Info("-------------------- FIELD CHANGES --------------------");
    foreach (var update in changed)
    {
        log.Info($"Event {update.EventId}");

        foreach (var change in update.DetailChanges)
        {
            var oldText = string.IsNullOrWhiteSpace(change.OldDisplayValue) ? "<blank>" : change.OldDisplayValue;
            var newText = string.IsNullOrWhiteSpace(change.NewDisplayValue) ? "<blank>" : change.NewDisplayValue;
            log.Info($"  {change.Label}: {oldText} -> {newText}");
        }

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
    if (plan.BoothCsvEventsMissingInSharePoint.Count > 0)
        log.Warn($"Booths CSV Event IDs missing from SharePoint (first 20): {string.Join(", ", plan.BoothCsvEventsMissingInSharePoint.Take(20))}");

    if (plan.EventsCsvEventsMissingInSharePoint.Count > 0)
        log.Warn($"Events CSV Event IDs missing from SharePoint (first 20): {string.Join(", ", plan.EventsCsvEventsMissingInSharePoint.Take(20))}");

    if (plan.SharePointEventsMissingInBoothCsv.Count > 0)
        log.Warn($"SharePoint Event IDs missing from Booths CSV (first 20): {string.Join(", ", plan.SharePointEventsMissingInBoothCsv.Take(20))}");

    if (plan.SharePointEventsMissingInEventsCsv.Count > 0)
        log.Warn($"SharePoint Event IDs missing from Events CSV (first 20): {string.Join(", ", plan.SharePointEventsMissingInEventsCsv.Take(20))}");

    if (plan.DuplicateSharePointEventIds.Count > 0)
        log.Error($"Duplicate SharePoint Event IDs (first 20): {string.Join(", ", plan.DuplicateSharePointEventIds.Take(20))}");
}

static string FormatValue(decimal value, bool integer) =>
    integer ? decimal.Truncate(value).ToString("N0") : value.ToString("N2");
