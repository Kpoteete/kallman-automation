using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Options;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace ActivitiesPull;

internal static class Program
{
    public static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(CliOptions.HelpText);
                return 0;
            }

            var paths = OutputPaths.Create(options);
            Directory.CreateDirectory(paths.OutputFolder);
            using var runLock = options.Mode is RunMode.Full or RunMode.Incremental
                ? RunLock.Acquire(paths.OutputFolder)
                : null;
            var credentials = MomentusCredentials.FromEnvironment();
            var source = new MomentusActivitySource(credentials, options);
            var puller = new AdaptiveActivityPuller(source, options);

            return options.Mode switch
            {
                RunMode.Full => FullBuild.Run(options, paths, puller),
                RunMode.Incremental => IncrementalBuild.Run(options, paths, puller),
                RunMode.Probe => Probe.Run(options, puller),
                _ => throw new InvalidOperationException("A run mode is required.")
            };
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine("Run with --help for usage.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: no warehouse checkpoint was advanced.");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}

internal enum RunMode { None, Full, Incremental, Probe }

internal sealed record CliOptions(
    RunMode Mode,
    string OrganizationCode,
    string BaseUrl,
    string OutputFolder,
    DateTime? Start,
    DateTime? End,
    int MaxRowsPerWindow,
    int DenseWindowMaxRows,
    int PageSize,
    int RequestDelayMs,
    int MaxAttempts,
    int OverlapHours,
    bool RestartFull,
    bool ShowHelp)
{
    public static readonly string HelpText = """
ActivitiesPull - safe historical and daily Momentus activity extraction

Usage:
  dotnet run -c Release -- full [options]
  dotnet run -c Release -- incremental [options]
  dotnet run -c Release -- probe --start YYYY-MM-DD --end YYYY-MM-DD [options]

Modes:
  full          One-time historical build. Resumes from durable chunk checkpoints.
  incremental   Daily overlap pull and key-based upsert into Activities_Pull.csv.
  probe         Read-only count check. Does not write activity data.

Options:
  --start VALUE             Full/probe start (default full: 1900-01-01).
  --end VALUE               Exclusive end (default: run start plus one minute).
  --output-folder PATH      Default: KALLMAN_DATA_WAREHOUSE, then the standard warehouse.
  --org CODE                Default: 10.
  --base-url URL            Default: https://kallman.ungerboeck.com/prod.
  --max-rows N              Split windows above this count (default: 2000).
  --dense-window-max N      Ceiling for a dense one-minute batch (default: 100000).
  --page-size N             SDK page size (default: 250; max: 1000).
  --request-delay-ms N      Pause after every API request (default: 250).
  --max-attempts N          Retry count for transient failures (default: 6).
  --overlap-hours N         Incremental overlap (default: 48).
  --restart-full            Archive incomplete full-build staging and start over.
  --help                    Show this help.

Required environment variables:
  MOMENTUS_APIUSER, MOMENTUS_SECRET, MOMENTUS_KEY

The tool is read-only against Momentus. It never creates, edits, or deletes activities.
""";

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase)))
            return Defaults(RunMode.None) with { ShowHelp = true };

        var mode = args[0].ToLowerInvariant() switch
        {
            "full" => RunMode.Full,
            "incremental" => RunMode.Incremental,
            "probe" => RunMode.Probe,
            _ => throw new CliException($"Unknown mode '{args[0]}'.")
        };

        var result = Defaults(mode);
        for (var i = 1; i < args.Length; i++)
        {
            string NextValue()
            {
                if (++i >= args.Length)
                    throw new CliException($"Missing value after {args[i - 1]}.");
                return args[i];
            }

            result = args[i].ToLowerInvariant() switch
            {
                "--start" => result with { Start = ParseDate(NextValue(), "--start") },
                "--end" => result with { End = ParseDate(NextValue(), "--end") },
                "--output-folder" => result with { OutputFolder = NextValue() },
                "--org" => result with { OrganizationCode = NextValue() },
                "--base-url" => result with { BaseUrl = NextValue() },
                "--max-rows" => result with { MaxRowsPerWindow = ParseInt(NextValue(), "--max-rows", 1, 100_000) },
                "--dense-window-max" => result with { DenseWindowMaxRows = ParseInt(NextValue(), "--dense-window-max", 1, 1_000_000) },
                "--page-size" => result with { PageSize = ParseInt(NextValue(), "--page-size", 1, 1000) },
                "--request-delay-ms" => result with { RequestDelayMs = ParseInt(NextValue(), "--request-delay-ms", 0, 60_000) },
                "--max-attempts" => result with { MaxAttempts = ParseInt(NextValue(), "--max-attempts", 1, 20) },
                "--overlap-hours" => result with { OverlapHours = ParseInt(NextValue(), "--overlap-hours", 1, 24 * 30) },
                "--restart-full" => result with { RestartFull = true },
                _ => throw new CliException($"Unknown option '{args[i]}'.")
            };
        }

        if (result.Start.HasValue && result.End.HasValue && result.Start >= result.End)
            throw new CliException("--start must be earlier than --end.");
        if (mode == RunMode.Probe && (!result.Start.HasValue || !result.End.HasValue))
            throw new CliException("probe requires both --start and --end.");
        return result;
    }

    private static CliOptions Defaults(RunMode mode) => new(
        mode,
        "10",
        "https://kallman.ungerboeck.com/prod",
        Environment.GetEnvironmentVariable("KALLMAN_DATA_WAREHOUSE")?.Trim()
            ?? @"C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents",
        null,
        null,
        2000,
        100_000,
        250,
        250,
        6,
        48,
        false,
        false);

    private static DateTime ParseDate(string value, string option) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified)
            : throw new CliException($"{option} is not a valid date/time: '{value}'.");

    private static int ParseInt(string value, string option, int min, int max) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= min && parsed <= max
            ? parsed
            : throw new CliException($"{option} must be an integer from {min} through {max}.");
}

internal sealed class CliException(string message) : Exception(message);

internal sealed record OutputPaths(
    string OutputFolder,
    string OutputCsv,
    string LastRun,
    string FullState,
    string StagingFolder,
    string BackupFolder,
    string PreviousCsv)
{
    public static OutputPaths Create(CliOptions options)
    {
        var folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.OutputFolder.Trim()));
        return new(
            folder,
            Path.Combine(folder, "Activities_Pull.csv"),
            Path.Combine(folder, "Activities_Pull.last_run.txt"),
            Path.Combine(folder, "Activities_Pull.full_state.json"),
            Path.Combine(folder, "_ActivitiesPull_staging"),
            Path.Combine(folder, "_ActivitiesPull_backups"),
            Path.Combine(folder, "Activities_Pull.previous.csv"));
    }
}

internal sealed record MomentusCredentials(string ApiUserId, string Secret, string Key)
{
    public static MomentusCredentials FromEnvironment()
    {
        var credentials = new MomentusCredentials(
            Environment.GetEnvironmentVariable("MOMENTUS_APIUSER")?.Trim() ?? "",
            Environment.GetEnvironmentVariable("MOMENTUS_SECRET")?.Trim() ?? "",
            Environment.GetEnvironmentVariable("MOMENTUS_KEY")?.Trim() ?? "");

        if (string.IsNullOrWhiteSpace(credentials.ApiUserId) ||
            string.IsNullOrWhiteSpace(credentials.Secret) ||
            string.IsNullOrWhiteSpace(credentials.Key))
        {
            throw new InvalidOperationException(
                "Set MOMENTUS_APIUSER, MOMENTUS_SECRET, and MOMENTUS_KEY. Credentials are never read from files.");
        }

        return credentials;
    }
}

internal sealed class RunLock : IDisposable
{
    private readonly string path;
    private readonly FileStream stream;

    private RunLock(string path, FileStream stream)
    {
        this.path = path;
        this.stream = stream;
    }

    public static RunLock Acquire(string outputFolder)
    {
        var path = Path.Combine(outputFolder, "Activities_Pull.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true))
            {
                writer.WriteLine($"PID={Environment.ProcessId}");
                writer.WriteLine($"Started={DateTime.Now:O}");
                writer.Flush();
            }
            stream.Flush(true);
            return new RunLock(path, stream);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Another Activities Pull appears to be running. Lock file: {path}", ex);
        }
    }

    public void Dispose()
    {
        stream.Dispose();
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover unlocked file is harmless; the next run can reuse it.
        }
    }
}

internal enum ActivityDateField { EnteredOn, ChangedOn }

internal sealed record ActivitySearchResult(IReadOnlyList<ActivitiesModel> Rows, int TotalResults);

internal interface IActivitySource
{
    ActivitySearchResult Search(ActivityDateField field, DateTime start, DateTime end, int maxResults);
}

internal sealed class MomentusActivitySource : IActivitySource
{
    private readonly ApiClient client;
    private readonly CliOptions options;

    public MomentusActivitySource(MomentusCredentials credentials, CliOptions options)
    {
        this.options = options;
        var jwt = new Jwt
        {
            UngerboeckURI = options.BaseUrl,
            APIUserID = credentials.ApiUserId,
            Secret = credentials.Secret,
            Key = credentials.Key,
            AutoRefresh = new AutoRefresh()
        };
        client = new ApiClient(jwt);
    }

    public ActivitySearchResult Search(ActivityDateField field, DateTime start, DateTime end, int maxResults)
    {
        var fieldName = field == ActivityDateField.EnteredOn
            ? nameof(ActivitiesModel.EnteredOn)
            : nameof(ActivitiesModel.ChangedOn);
        var filter = $"{fieldName} ge datetime'{FormatDate(start)}' and {fieldName} lt datetime'{FormatDate(end)}'";
        var search = new Search
        {
            PageSize = options.PageSize,
            MaxResults = maxResults,
            OrderBy = [fieldName, nameof(ActivitiesModel.Account), nameof(ActivitiesModel.SequenceNumber)]
        };

        try
        {
            var response = client.Endpoints.Activities.Search(options.OrganizationCode, filter, search);
            PauseAfterRequest();
            var rows = response.Results?.ToList() ?? [];
            var total = response.SearchMetadata?.ResultsTotal ?? rows.Count;
            var next = response.SearchMetadata?.Links?.Next;

            while (!string.IsNullOrWhiteSpace(next))
            {
                response = NavigateWithRetry(next, search, start, end);
                rows.AddRange(response.Results ?? []);
                next = response.SearchMetadata?.Links?.Next;
                if (rows.Count > maxResults)
                    throw new WindowTooLargeException(start, end, maxResults);
            }

            if (total != rows.Count)
                throw new InvalidDataException($"Momentus reported {total:N0} results but returned {rows.Count:N0}.");
            return new ActivitySearchResult(rows, total);
        }
        catch (Exception ex) when (IsResultLimitError(ex))
        {
            PauseAfterRequest();
            throw new WindowTooLargeException(start, end, maxResults, ex);
        }
    }

    private static string FormatDate(DateTime value) =>
        value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private static bool IsResultLimitError(Exception ex) =>
        ex.ToString().Contains("exceeds the allowed max results", StringComparison.OrdinalIgnoreCase);

    private void PauseAfterRequest()
    {
        if (options.RequestDelayMs > 0)
            Thread.Sleep(options.RequestDelayMs);
    }

    private SearchResponse<ActivitiesModel> NavigateWithRetry(
        string next,
        Search search,
        DateTime start,
        DateTime end)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            try
            {
                var response = client.Endpoints.Activities.NavigateSearchList(next, search);
                PauseAfterRequest();
                return response;
            }
            catch (Exception ex) when (attempt < options.MaxAttempts && TransientErrors.IsTransient(ex))
            {
                last = ex;
                var delaySeconds = TransientErrors.IsRateLimited(ex) ? 60 : Math.Min(30, 1 << attempt);
                Console.Error.WriteLine(
                    $"  page attempt {attempt}/{options.MaxAttempts} failed for {start:O}..{end:O} " +
                    $"({ex.GetType().Name}); waiting {delaySeconds}s");
                Thread.Sleep(TimeSpan.FromSeconds(delaySeconds));
            }
        }

        throw new InvalidOperationException(
            $"Momentus pagination failed after {options.MaxAttempts} attempts for {start:O} through {end:O}.", last);
    }
}

internal sealed class WindowTooLargeException(DateTime start, DateTime end, int limit, Exception? inner = null)
    : Exception($"The window {start:O} through {end:O} exceeds {limit:N0} activities.", inner);

internal sealed class AdaptiveActivityPuller(IActivitySource source, CliOptions options)
{
    private static readonly TimeSpan MinimumWindow = TimeSpan.FromMinutes(1);
    private int requests;

    public int RequestCount => requests;

    public long Count(ActivityDateField field, DateTime start, DateTime end)
    {
        long count = 0;
        Pull(field, start, end, (_, _, rows) => count += rows.Count);
        return count;
    }

    public void Pull(
        ActivityDateField field,
        DateTime start,
        DateTime end,
        Action<DateTime, DateTime, IReadOnlyList<ActivitiesModel>> acceptLeaf)
    {
        if (start >= end)
            return;

        ActivitySearchResult result;
        try
        {
            result = ExecuteWithRetry(
                () => source.Search(field, start, end, options.MaxRowsPerWindow),
                field,
                start,
                end);
        }
        catch (WindowTooLargeException) when (end - start > MinimumWindow)
        {
            var duration = end - start;
            var halfSeconds = Math.Max(1, (long)Math.Floor(duration.TotalSeconds / 2));
            var midpoint = start.AddSeconds(halfSeconds);
            if (midpoint <= start || midpoint >= end)
                throw new InvalidOperationException($"Unable to split {start:O} through {end:O} safely.");

            Console.WriteLine($"  split {start:yyyy-MM-dd HH:mm:ss} -> {end:yyyy-MM-dd HH:mm:ss} (more than {options.MaxRowsPerWindow:N0} rows)");
            Pull(field, start, midpoint, acceptLeaf);
            Pull(field, midpoint, end, acceptLeaf);
            return;
        }
        catch (WindowTooLargeException)
        {
            Console.WriteLine(
                $"  dense one-minute batch {start:yyyy-MM-dd HH:mm:ss}: " +
                $"raising the guarded ceiling to {options.DenseWindowMaxRows:N0}");
            try
            {
                result = ExecuteWithRetry(
                    () => source.Search(field, start, end, options.DenseWindowMaxRows),
                    field,
                    start,
                    end);
            }
            catch (WindowTooLargeException denseEx)
            {
                throw new InvalidOperationException(
                    $"More than {options.DenseWindowMaxRows:N0} activities fall in one minute. " +
                    "Increase --dense-window-max deliberately and resume the build.", denseEx);
            }
        }

        acceptLeaf(start, end, result.Rows);
    }

    private ActivitySearchResult ExecuteWithRetry(
        Func<ActivitySearchResult> operation,
        ActivityDateField field,
        DateTime start,
        DateTime end)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            try
            {
                requests++;
                var result = operation();
                return result;
            }
            catch (WindowTooLargeException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < options.MaxAttempts && TransientErrors.IsTransient(ex))
            {
                last = ex;
                var delaySeconds = TransientErrors.IsRateLimited(ex) ? 60 : Math.Min(30, 1 << attempt);
                Console.Error.WriteLine(
                    $"  API attempt {attempt}/{options.MaxAttempts} failed for {field} " +
                    $"{start:O}..{end:O} ({ex.GetType().Name}). Waiting {delaySeconds}s.");
                Thread.Sleep(TimeSpan.FromSeconds(delaySeconds));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Non-transient Momentus search failure for {field} {start:O} through {end:O}.", ex);
            }
        }

        throw new InvalidOperationException(
            $"Momentus search failed after {options.MaxAttempts} attempts for {field} {start:O} through {end:O}.", last);
    }

}

internal static class TransientErrors
{
    public static bool IsTransient(Exception ex)
    {
        if (ex is TimeoutException or TaskCanceledException or HttpRequestException)
            return true;

        var text = ex.ToString();
        return IsRateLimited(ex) ||
               text.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("error occurred while sending the request", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("HttpClient called failed", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("502", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("503", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("504", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRateLimited(Exception ex)
    {
        var text = ex.ToString();
        return text.Contains("429", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class ActivitySchema
{
    public static readonly string[] Columns =
    [
        "PullRunOn", "OrganizationCode", "Account", "SequenceNumber", "Priority", "Due", "PlainText",
        "Opportunity", "Recipient", "DueTime", "Type", "EnteredByCode", "Privileged", "EnteredOn",
        "ChangedByCode", "ChangedOn", "Event", "Function", "Checklist", "Contact", "EntryDesignation",
        "NotificationFlag", "NoteType", "NoteCode", "NoteSequenceNbr", "NotifiedDate", "EventOpportunity",
        "Subject", "Locked", "OrderNumber", "EmailSent", "EmailSentOn", "ProjectID", "ActualStartDate",
        "ActualEndDate", "ProjectDesignation", "EmailSentUserID", "Text", "ContractSequenceNbr",
        "MultiOrgAccountCode", "BlockCode", "FiscalYearPeriod", "InventoryItemCode", "ReminderDate", "Status",
        "Quote", "ExhibitorID"
    ];

    public static string[] FromModel(ActivitiesModel model, DateTime pulledOn)
    {
        var values = new string[Columns.Length];
        for (var i = 0; i < Columns.Length; i++)
        {
            values[i] = Columns[i] == "PullRunOn"
                ? FormatValue(pulledOn)
                : FormatValue(GetProperty(model, Columns[i]));
        }
        return values;
    }

    public static string Key(IReadOnlyList<string> values)
    {
        var org = values[IndexOf("OrganizationCode")].Trim();
        var account = values[IndexOf("Account")].Trim();
        var sequence = values[IndexOf("SequenceNumber")].Trim();
        return string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(sequence)
            ? ""
            : $"{org}|{account}|{sequence}";
    }

    public static int IndexOf(string column)
    {
        var index = Array.FindIndex(Columns, c => c.Equals(column, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : throw new InvalidOperationException($"Unknown activity column '{column}'.");
    }

    private static object? GetProperty(object source, string propertyName) =>
        source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?.GetValue(source);

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };
}

internal static class ActivityCsv
{
    private static CsvConfiguration Config => new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        MissingFieldFound = null,
        HeaderValidated = null,
        BadDataFound = context => throw new InvalidDataException($"Bad CSV data near row {context.Context?.Parser?.Row}.")
    };

    public static long WriteModels(string path, IReadOnlyList<ActivitiesModel> models, DateTime pulledOn)
    {
        var temp = path + ".tmp";
        using (var writer = new StreamWriter(temp, false, new UTF8Encoding(true)))
        using (var csv = new CsvWriter(writer, Config))
        {
            WriteHeader(csv);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in models)
            {
                var values = ActivitySchema.FromModel(model, pulledOn);
                var key = ActivitySchema.Key(values);
                if (string.IsNullOrWhiteSpace(key))
                    throw new InvalidDataException("Momentus returned an activity without OrganizationCode, Account, or SequenceNumber.");
                if (!keys.Add(key))
                    throw new InvalidDataException($"Momentus returned duplicate activity key {key} in one window.");
                WriteRow(csv, values);
            }
        }
        File.Move(temp, path, true);
        return models.Count;
    }

    public static long AssembleChunks(string chunkFolder, string destination)
    {
        var chunkPaths = Directory.GetFiles(chunkFolder, "*.csv").OrderBy(p => p, StringComparer.Ordinal).ToList();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long rows = 0;

        using var writer = new StreamWriter(destination, false, new UTF8Encoding(true));
        using var output = new CsvWriter(writer, Config);
        WriteHeader(output);

        foreach (var chunk in chunkPaths)
        {
            foreach (var values in ReadRows(chunk))
            {
                var key = ActivitySchema.Key(values);
                if (string.IsNullOrWhiteSpace(key))
                    throw new InvalidDataException($"Blank activity key in staging chunk {Path.GetFileName(chunk)}.");
                if (!keys.Add(key))
                    throw new InvalidDataException($"Duplicate activity key {key} across historical chunks.");
                WriteRow(output, values);
                rows++;
            }
        }
        return rows;
    }

    public static (long Rows, long Inserted, long Updated) MergeIncremental(
        string existingPath,
        string tempPath,
        IDictionary<string, string[]> changes)
    {
        long rows = 0;
        long updated = 0;
        var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var writer = new StreamWriter(tempPath, false, new UTF8Encoding(true));
        using var output = new CsvWriter(writer, Config);
        WriteHeader(output);

        foreach (var existing in ReadRows(existingPath))
        {
            var key = ActivitySchema.Key(existing);
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidDataException("The existing warehouse file contains a blank activity key. Complete a full build first.");
            if (!existingKeys.Add(key))
                throw new InvalidDataException($"The existing warehouse file contains duplicate activity key {key}.");

            if (changes.Remove(key, out var replacement))
            {
                WriteRow(output, replacement);
                updated++;
            }
            else
            {
                WriteRow(output, existing);
            }
            rows++;
        }

        var inserted = changes.Count;
        foreach (var addition in changes.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            WriteRow(output, addition.Value);
            rows++;
        }
        return (rows, inserted, updated);
    }

    public static long Validate(string path)
    {
        long rows = 0;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var values in ReadRows(path))
        {
            var key = ActivitySchema.Key(values);
            if (string.IsNullOrWhiteSpace(key) || !keys.Add(key))
                throw new InvalidDataException($"Validation failed at activity key '{key}'.");
            rows++;
        }
        return rows;
    }

    public static IEnumerable<string[]> ReadRows(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        using var csv = new CsvReader(reader, Config);
        if (!csv.Read() || !csv.ReadHeader())
            throw new InvalidDataException($"CSV has no header: {path}");

        var actual = csv.HeaderRecord ?? [];
        if (!actual.SequenceEqual(ActivitySchema.Columns, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"CSV schema does not match Activities_Pull: {path}");

        while (csv.Read())
        {
            var values = new string[ActivitySchema.Columns.Length];
            for (var i = 0; i < values.Length; i++)
                values[i] = csv.GetField(i) ?? "";
            yield return values;
        }
    }

    private static void WriteHeader(CsvWriter csv)
    {
        foreach (var column in ActivitySchema.Columns)
            csv.WriteField(column);
        csv.NextRecord();
    }

    private static void WriteRow(CsvWriter csv, IReadOnlyList<string> values)
    {
        foreach (var value in values)
            csv.WriteField(value);
        csv.NextRecord();
    }
}

internal sealed record FullBuildState(
    DateTime Start,
    DateTime TargetEnd,
    DateTime Cursor,
    long RowsStaged,
    int LeafWindows,
    bool Complete,
    DateTime UpdatedOn);

internal static class FullBuild
{
    public static int Run(CliOptions options, OutputPaths paths, AdaptiveActivityPuller puller)
    {
        if (options.RestartFull)
            ArchiveStaging(paths);

        var state = LoadOrCreateState(options, paths);
        Directory.CreateDirectory(paths.StagingFolder);
        ReconcileUncommittedChunks(paths.StagingFolder, state.Cursor);
        Console.WriteLine($"FULL BUILD: {state.Cursor:yyyy-MM-dd HH:mm:ss} through {state.TargetEnd:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Staging: {paths.StagingFolder}");

        while (state.Cursor < state.TargetEnd)
        {
            var windowEnd = Min(state.Cursor.AddYears(1), state.TargetEnd);
            var currentState = state;
            puller.Pull(ActivityDateField.EnteredOn, state.Cursor, windowEnd, (leafStart, leafEnd, rows) =>
            {
                if (rows.Count > 0)
                {
                    var chunkPath = Path.Combine(paths.StagingFolder, ChunkName(leafStart, leafEnd));
                    ActivityCsv.WriteModels(chunkPath, rows, DateTime.Now);
                }

                currentState = currentState with
                {
                    Cursor = leafEnd,
                    RowsStaged = currentState.RowsStaged + rows.Count,
                    LeafWindows = currentState.LeafWindows + 1,
                    UpdatedOn = DateTime.Now
                };
                AtomicText.WriteJson(paths.FullState, currentState);
                Console.WriteLine($"  saved through {leafEnd:yyyy-MM-dd HH:mm:ss}: +{rows.Count:N0}, staged {currentState.RowsStaged:N0}");
            });
            state = currentState;
        }

        var building = paths.OutputCsv + ".building";
        Console.WriteLine("Assembling and validating historical chunks...");
        var rowsAssembled = ActivityCsv.AssembleChunks(paths.StagingFolder, building);
        if (rowsAssembled != state.RowsStaged)
            throw new InvalidDataException($"Staged row count {state.RowsStaged:N0} does not match assembled count {rowsAssembled:N0}.");

        PublishFullBuild(building, paths);
        AtomicText.WriteAllText(paths.LastRun, state.TargetEnd.ToString("O", CultureInfo.InvariantCulture));
        state = state with { Complete = true, UpdatedOn = DateTime.Now };
        AtomicText.WriteJson(paths.FullState, state);

        Console.WriteLine($"Published {rowsAssembled:N0} activities to {paths.OutputCsv}");
        Console.WriteLine($"Search operations: {puller.RequestCount:N0} (paginated requests are additional)");
        return 0;
    }

    private static FullBuildState LoadOrCreateState(CliOptions options, OutputPaths paths)
    {
        if (File.Exists(paths.FullState))
        {
            var existing = JsonSerializer.Deserialize<FullBuildState>(File.ReadAllText(paths.FullState))
                ?? throw new InvalidDataException("Full-build state file is invalid.");
            if (!existing.Complete)
            {
                if (options.Start.HasValue && options.Start.Value != existing.Start)
                    throw new CliException("An incomplete full build has a different start. Use --restart-full to archive it.");
                Console.WriteLine($"Resuming the incomplete build last updated {existing.UpdatedOn:O}.");
                var normalized = existing with
                {
                    Start = TimeBoundary.FloorSecond(existing.Start),
                    TargetEnd = TimeBoundary.FloorSecond(existing.TargetEnd),
                    Cursor = TimeBoundary.FloorSecond(existing.Cursor)
                };
                if (normalized != existing)
                    AtomicText.WriteJson(paths.FullState, normalized);
                return normalized;
            }

            throw new CliException("A full build is already complete. Use incremental, or --restart-full for a new reconciliation build.");
        }

        var start = TimeBoundary.FloorSecond(options.Start ?? new DateTime(1900, 1, 1));
        var end = TimeBoundary.FloorSecond(options.End ?? DateTime.Now.AddMinutes(1));
        if (start >= end)
            throw new CliException("The full-build start must be earlier than its end.");
        var created = new FullBuildState(start, end, start, 0, 0, false, DateTime.Now);
        AtomicText.WriteJson(paths.FullState, created);
        return created;
    }

    private static void ArchiveStaging(OutputPaths paths)
    {
        if (!File.Exists(paths.FullState) && !Directory.Exists(paths.StagingFolder))
            return;

        var archive = Path.Combine(paths.OutputFolder, $"_ActivitiesPull_staging_archive_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(archive);
        if (Directory.Exists(paths.StagingFolder))
            Directory.Move(paths.StagingFolder, Path.Combine(archive, "chunks"));
        if (File.Exists(paths.FullState))
            File.Move(paths.FullState, Path.Combine(archive, Path.GetFileName(paths.FullState)));
        Console.WriteLine($"Archived prior full-build staging to {archive}");
    }

    private static void ReconcileUncommittedChunks(string stagingFolder, DateTime committedCursor)
    {
        var uncommitted = Directory.GetFiles(stagingFolder, "*.csv")
            .Where(path => ParseChunkStart(path) >= committedCursor)
            .ToList();
        if (uncommitted.Count == 0)
            return;

        var archive = Path.Combine(stagingFolder, $"_uncommitted_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(archive);
        foreach (var path in uncommitted)
            File.Move(path, Path.Combine(archive, Path.GetFileName(path)));
        Console.WriteLine($"Archived {uncommitted.Count:N0} uncommitted chunk(s) to {archive}");
    }

    private static DateTime ParseChunkStart(string path)
    {
        var prefix = Path.GetFileNameWithoutExtension(path).Split('_', 2)[0];
        return DateTime.TryParseExact(
            prefix,
            "yyyyMMdd'T'HHmmssfffffff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : throw new InvalidDataException($"Unrecognized historical chunk name: {Path.GetFileName(path)}");
    }

    private static void PublishFullBuild(string building, OutputPaths paths)
    {
        if (File.Exists(paths.OutputCsv))
        {
            Directory.CreateDirectory(paths.BackupFolder);
            var backup = Path.Combine(
                paths.BackupFolder,
                $"Activities_Pull.before_full_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            File.Replace(building, paths.OutputCsv, backup, true);
        }
        else
        {
            File.Move(building, paths.OutputCsv);
        }
    }

    private static DateTime Min(DateTime left, DateTime right) => left < right ? left : right;
    private static string ChunkName(DateTime start, DateTime end) =>
        $"{start:yyyyMMddTHHmmssfffffff}_{end:yyyyMMddTHHmmssfffffff}.csv";
}

internal static class IncrementalBuild
{
    public static int Run(CliOptions options, OutputPaths paths, AdaptiveActivityPuller puller)
    {
        if (!File.Exists(paths.OutputCsv) || !File.Exists(paths.LastRun))
            throw new InvalidOperationException("A completed full build is required before incremental mode.");
        if (!DateTime.TryParse(File.ReadAllText(paths.LastRun).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var checkpoint))
            throw new InvalidDataException("Activities_Pull.last_run.txt is not a valid timestamp.");

        var start = TimeBoundary.FloorSecond(options.Start ?? checkpoint.Subtract(TimeSpan.FromHours(options.OverlapHours)));
        var end = TimeBoundary.FloorSecond(options.End ?? DateTime.Now.AddMinutes(1));
        if (start >= end)
            throw new CliException("The incremental start must be earlier than its end.");

        Console.WriteLine($"INCREMENTAL: {start:yyyy-MM-dd HH:mm:ss} through {end:yyyy-MM-dd HH:mm:ss}");
        var changes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var pulledOn = DateTime.Now;

        void Accept(DateTime _leafStart, DateTime _leafEnd, IReadOnlyList<ActivitiesModel> rows)
        {
            foreach (var model in rows)
            {
                var values = ActivitySchema.FromModel(model, pulledOn);
                var key = ActivitySchema.Key(values);
                if (string.IsNullOrWhiteSpace(key))
                    throw new InvalidDataException("Momentus returned an activity without its complete key.");
                changes[key] = values;
            }
        }

        PullInDailyWindows(puller, ActivityDateField.ChangedOn, start, end, Accept);
        PullInDailyWindows(puller, ActivityDateField.EnteredOn, start, end, Accept);
        Console.WriteLine($"Unique new/changed activities: {changes.Count:N0}");

        var temp = paths.OutputCsv + ".incremental.tmp";
        var merge = ActivityCsv.MergeIncremental(paths.OutputCsv, temp, changes);
        var validatedRows = ActivityCsv.Validate(temp);
        if (validatedRows != merge.Rows)
            throw new InvalidDataException("Incremental row-count validation failed.");

        if (File.Exists(paths.PreviousCsv))
            File.Delete(paths.PreviousCsv);
        File.Replace(temp, paths.OutputCsv, paths.PreviousCsv, true);
        AtomicText.WriteAllText(paths.LastRun, end.ToString("O", CultureInfo.InvariantCulture));

        Console.WriteLine($"Published {merge.Rows:N0} rows: {merge.Inserted:N0} inserted, {merge.Updated:N0} updated.");
        Console.WriteLine($"Search operations: {puller.RequestCount:N0} (paginated requests are additional)");
        return 0;
    }

    private static void PullInDailyWindows(
        AdaptiveActivityPuller puller,
        ActivityDateField field,
        DateTime start,
        DateTime end,
        Action<DateTime, DateTime, IReadOnlyList<ActivitiesModel>> accept)
    {
        var cursor = start;
        while (cursor < end)
        {
            var next = cursor.AddDays(1) < end ? cursor.AddDays(1) : end;
            puller.Pull(field, cursor, next, accept);
            cursor = next;
        }
    }
}

internal static class Probe
{
    public static int Run(CliOptions options, AdaptiveActivityPuller puller)
    {
        var start = options.Start!.Value;
        var end = options.End!.Value;
        var entered = puller.Count(ActivityDateField.EnteredOn, start, end);
        var changed = puller.Count(ActivityDateField.ChangedOn, start, end);
        Console.WriteLine($"EnteredOn count: {entered:N0}");
        Console.WriteLine($"ChangedOn count: {changed:N0}");
        Console.WriteLine("Probe completed without writing activity data.");
        return 0;
    }
}

internal static class AtomicText
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void WriteJson<T>(string path, T value) =>
        WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));

    public static void WriteAllText(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}

internal static class TimeBoundary
{
    public static DateTime FloorSecond(DateTime value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));
}
