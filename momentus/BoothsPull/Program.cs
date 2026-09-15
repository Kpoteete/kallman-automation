using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Kallman.Automation.Core.Configuration;
using Kallman.Automation.Core.Files;
using Kallman.Automation.Core.Operations;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

class Program
{
    private const string OrgCode = "10";
    private const string UngerboeckUri = "https://kallman.ungerboeck.com/prod";

    private static readonly string OutputFolder =
        Environment.GetEnvironmentVariable("BOOTHS_OUTPUT_FOLDER")?.Trim()
        ?? Environment.GetEnvironmentVariable("KALLMAN_DATA_WAREHOUSE")?.Trim()
        ?? @"C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents";

    private static BoothPullSettings Settings = new();
    private static List<string> DesiredColumns = new();
    private static string? DetectedKeyField;
    private static string? DetectedChangeField;

    private static string OutputFilePath = string.Empty;
    private static string RunStatePath = string.Empty;

    private static readonly string[] KeyFieldCandidates =
    {
        "SequenceNumber",
        "Sequence",
        "BoothSequenceNumber",
        "SequenceNbr"
    };

    private static readonly string[] ChangeFieldCandidates =
    {
        "LastChangedDateTime",
        "ChangedOn",
        "LastChangedOn",
        "UpdateStamp",
        "LastChanged",
        "ModifiedOn",
        "LastModifiedDateTime",
        "LastModifiedOn"
    };

    static int Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Settings = LoadSettings();
        OutputFilePath = Path.Combine(OutputFolder, Settings.OutputFileName);
        RunStatePath = Path.Combine(OutputFolder, Settings.RunStateFileName);

        DesiredColumns = BuildDesiredColumns();
        DetectedKeyField = ResolveModelField(Settings.KeyField, KeyFieldCandidates);
        DetectedChangeField = ResolveModelField(Settings.ChangeField, ChangeFieldCandidates);

        string requestedMode = ResolveRunMode();
        var summary = new AutomationRunSummary { Automation = "BoothsPull" };
        string summaryPath = Path.Combine(OutputFolder, "logs", $"booths-{summary.RunId}.json");

        try
        {
            Directory.CreateDirectory(OutputFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(summaryPath)!);

            Console.WriteLine("-> Building Momentus client...");
            var client = BuildClient();

            PrintStartupDiagnostics(requestedMode);

            // A first incremental run automatically becomes a full rebuild so the
            // warehouse starts with a complete historical booth file.
            string effectiveMode = requestedMode;
            if (requestedMode.Equals("Incremental", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(OutputFilePath))
            {
                Console.WriteLine("-> No existing Booths_Pull.csv found. First run will be a FULL rebuild.");
                effectiveMode = "Full";
            }

            int exitCode = effectiveMode.Equals("Full", StringComparison.OrdinalIgnoreCase)
                ? RunFullRebuild(client, summary, summaryPath)
                : RunIncremental(client, summary, summaryPath);

            return exitCode;
        }
        catch (Exception ex)
        {
            summary.Errors++;
            summary.Complete("Failed", ex.Message);
            summary.WriteJson(summaryPath);
            Console.Error.WriteLine("FATAL:");
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static int RunFullRebuild(ApiClient client, AutomationRunSummary summary, string summaryPath)
    {
        DateTime now = DateTime.Now;

        Console.WriteLine("-> FULL REBUILD: pulling every booth available in Organization 10...");
        var searchBooths = SearchAllBooths(client);
        Console.WriteLine($"-> Booth records returned by search: {searchBooths.Count:N0}");

        var finalByKey = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        int processed = 0;
        int fullGets = 0;
        int getFallbacks = 0;

        foreach (BoothsModel booth in searchBooths)
        {
            processed++;
            BoothsModel exportBooth = TryGetFullBooth(
                client, booth, out bool usedFullGet, out bool usedSearchFallback);

            if (usedFullGet)
                fullGets++;
            if (usedSearchFallback)
                getFallbacks++;

            var row = CreateRow(exportBooth);
            string key = GetRowKey(row);

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException(
                    $"Could not create a unique key for booth #{processed}. " +
                    "Expected a sequence key or Event + Function + Booth values. " +
                    "The existing output and checkpoint were not changed.");
            }

            finalByKey[key] = row;

            if (processed % 100 == 0 || processed == searchBooths.Count)
            {
                double pct = searchBooths.Count == 0
                    ? 100.0
                    : processed * 100.0 / searchBooths.Count;

                Console.WriteLine(
                    $"-> Full booth details: {processed:N0} / {searchBooths.Count:N0} ({pct:0.0}%) " +
                    $"| full GETs {fullGets:N0} | search fallbacks {getFallbacks:N0}");
            }

            ThrottleIfNeeded(processed);
        }

        var finalRows = SortRows(finalByKey.Values);
        summary.RecordsRead = searchBooths.Count;
        summary.RecordsWritten = finalRows.Count;

        PublishOutput(finalRows);
        WriteRunState(RunStatePath, now);

        summary.CheckpointAdvanced = true;
        summary.Complete("Succeeded");
        summary.WriteJson(summaryPath);

        Console.WriteLine($"-> Full GET lookups used: {fullGets:N0}");
        Console.WriteLine($"-> Search-record fallbacks used: {getFallbacks:N0}");
        Console.WriteLine($"-> Unique booths written: {finalRows.Count:N0}");
        Console.WriteLine($"-> CSV saved: {OutputFilePath}");
        Console.WriteLine($"-> Backup: {OutputFilePath}.bak");
        Console.WriteLine($"-> Run state saved: {RunStatePath}");
        return 0;
    }

    private static int RunIncremental(ApiClient client, AutomationRunSummary summary, string summaryPath)
    {
        if (string.IsNullOrWhiteSpace(DetectedChangeField))
        {
            if (!Settings.IncrementalFallbackToFullScan)
            {
                throw new InvalidOperationException(
                    "No supported booth change-date field was found on BoothsModel. " +
                    "Set ChangeField in appsettings.json if you know the correct field, " +
                    "or enable IncrementalFallbackToFullScan.");
            }

            Console.WriteLine("WARNING: No booth change-date field was detected.");
            Console.WriteLine("WARNING: Incremental mode is falling back to a complete booth rebuild.");
            return RunFullRebuild(client, summary, summaryPath);
        }

        DateTime now = DateTime.Now;
        DateTime effectiveSince = GetEffectiveSince();

        Console.WriteLine($"-> Existing file: {OutputFilePath}");
        Console.WriteLine($"-> Last run checkpoint with overlap: {effectiveSince:yyyy-MM-dd HH:mm:ss}");

        var existingRows = LoadExistingRows(OutputFilePath);
        Console.WriteLine($"-> Existing CSV rows loaded: {existingRows.Count:N0}");

        var changedBooths = PullChangedBooths(client, effectiveSince);
        summary.RecordsRead = changedBooths.Count;
        Console.WriteLine($"-> Changed/new booths returned: {changedBooths.Count:N0}");

        int inserted = 0;
        int updated = 0;

        foreach (BoothsModel booth in changedBooths)
        {
            var row = CreateRow(booth);
            string key = GetRowKey(row);

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException(
                    "A changed booth did not contain a usable unique key. " +
                    "The existing output and checkpoint were not changed.");
            }

            if (existingRows.ContainsKey(key))
            {
                existingRows[key] = row;
                updated++;
            }
            else
            {
                existingRows[key] = row;
                inserted++;
            }
        }

        var finalRows = SortRows(existingRows.Values);
        summary.RecordsWritten = finalRows.Count;

        PublishOutput(finalRows);
        WriteRunState(RunStatePath, now);

        summary.CheckpointAdvanced = true;
        summary.Complete("Succeeded");
        summary.WriteJson(summaryPath);

        Console.WriteLine($"-> Inserted rows: {inserted:N0}");
        Console.WriteLine($"-> Updated rows: {updated:N0}");
        Console.WriteLine($"-> Final CSV rows: {finalRows.Count:N0}");
        Console.WriteLine($"-> CSV saved: {OutputFilePath}");
        Console.WriteLine($"-> Backup: {OutputFilePath}.bak");
        Console.WriteLine($"-> Run state saved: {RunStatePath}");
        return 0;
    }

    private static ApiClient BuildClient()
    {
        MomentusCredentials credentials = MomentusCredentials.FromEnvironment();
        var auth = new Jwt
        {
            APIUserID = credentials.ApiUserId,
            Secret = credentials.Secret,
            Key = credentials.Key,
            UngerboeckURI = UngerboeckUri,
            AutoRefresh = new AutoRefresh()
        };

        return new ApiClient(auth);
    }

    private static BoothPullSettings LoadSettings()
    {
        string[] candidates =
        {
            Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "appsettings.json")
        };

        string? path = candidates.FirstOrDefault(File.Exists);
        if (path == null)
            throw new FileNotFoundException("Could not find appsettings.json.");

        string json = File.ReadAllText(path);
        var root = JsonSerializer.Deserialize<AppSettingsRoot>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return root?.BoothsPull
            ?? throw new InvalidOperationException("appsettings.json is missing the BoothsPull section.");
    }

    private static string ResolveRunMode()
    {
        string mode = Environment.GetEnvironmentVariable("BOOTHS_PULL_MODE")?.Trim()
            ?? Settings.DefaultMode.Trim();

        if (!mode.Equals("Incremental", StringComparison.OrdinalIgnoreCase)
            && !mode.Equals("Full", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported BoothsPull mode '{mode}'. Use Incremental or Full.");
        }

        return mode;
    }

    private static void PrintStartupDiagnostics(string mode)
    {
        Console.WriteLine($"-> Requested mode: {mode}");
        Console.WriteLine($"-> Organization: {OrgCode}");
        Console.WriteLine($"-> Output: {OutputFilePath}");
        Console.WriteLine($"-> Standard BoothsModel fields exported: {DesiredColumns.Count - 1:N0}");
        Console.WriteLine($"-> Detected key field: {DetectedKeyField ?? "(none - using Event + Function + Booth)"}");
        Console.WriteLine($"-> Detected change field: {DetectedChangeField ?? "(none)"}");
        Console.WriteLine($"-> User-field properties: excluded");
    }

    private static List<string> BuildDesiredColumns()
    {
        var columns = new List<string> { "PullRunOn" };

        var properties = typeof(BoothsModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Where(p => !IsUserFieldProperty(p.Name))
            .OrderBy(p => p.MetadataToken)
            .Select(p => p.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        columns.AddRange(properties);
        return columns;
    }

    private static bool IsUserFieldProperty(string name)
    {
        return name.Contains("UserField", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveModelField(string configuredValue, IEnumerable<string> candidates)
    {
        var propertyNames = typeof(BoothsModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        if (!string.IsNullOrWhiteSpace(configuredValue)
            && !configuredValue.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return propertyNames.FirstOrDefault(p =>
                p.Equals(configuredValue, StringComparison.OrdinalIgnoreCase));
        }

        foreach (string candidate in candidates)
        {
            string? match = propertyNames.FirstOrDefault(p =>
                p.Equals(candidate, StringComparison.OrdinalIgnoreCase));

            if (match != null)
                return match;
        }

        return null;
    }

    private static List<BoothsModel> SearchAllBooths(ApiClient client)
    {
        string configuredFilter = Settings.FullSearchFilter?.Trim() ?? string.Empty;
        string filter = string.IsNullOrWhiteSpace(configuredFilter) ? "All" : configuredFilter;

        Console.WriteLine($"-> Booth full-search filter: {filter}");
        return SearchBoothsPaged(client, filter);
    }

    private static List<BoothsModel> SearchBoothsPaged(ApiClient client, string odata)
    {
        int maxResults = Math.Max(1, Settings.SearchMaxResults);
        int pageSize = Math.Max(1, Settings.SearchPageSize);

        var options = new Ungerboeck.Api.Models.Options.Search
        {
            MaxResults = maxResults,
            PageSize = pageSize
        };

        Console.WriteLine($"-> Search MaxResults: {maxResults:N0}; PageSize: {pageSize:N0}");

        var response = client.Endpoints.Booths.Search(OrgCode, odata, options);
        var allResults = new List<BoothsModel>();
        int pageNumber = 0;

        while (true)
        {
            pageNumber++;
            var pageResults = response.Results?.ToList() ?? new List<BoothsModel>();
            allResults.AddRange(pageResults);

            Console.WriteLine(
                $"-> Search page {pageNumber:N0}: {pageResults.Count:N0} booths; " +
                $"running total {allResults.Count:N0}");

            string? nextLink = response.SearchMetadata.Links.Next;
            if (string.IsNullOrWhiteSpace(nextLink))
                break;

            response = client.Endpoints.Booths.NavigateSearchList(nextLink);
        }

        return allResults;
    }

    private static List<BoothsModel> PullChangedBooths(ApiClient client, DateTime since)
    {
        if (string.IsNullOrWhiteSpace(DetectedChangeField))
            throw new InvalidOperationException("Incremental booth pull requires a change-date field.");

        var rows = new List<BoothsModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        DateTime cursor = since.Date;
        DateTime endDate = DateTime.Today.AddDays(1);
        int processed = 0;

        while (cursor < endDate)
        {
            DateTime next = cursor.AddDays(1);
            string startText = cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string endText = next.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string odata =
                $"{DetectedChangeField} ge datetime'{startText}' and {DetectedChangeField} lt datetime'{endText}'";

            Console.WriteLine($"-> OData: {odata}");

            var results = SearchBoothsPaged(client, odata);

            foreach (BoothsModel searchBooth in results)
            {
                processed++;

                DateTime changedOn = GetDateTimeProperty(searchBooth, DetectedChangeField);
                if (changedOn != DateTime.MinValue && changedOn <= since)
                    continue;

                BoothsModel exportBooth = TryGetFullBooth(
                    client, searchBooth, out _, out _);
                var row = CreateRow(exportBooth);
                string key = GetRowKey(row);

                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                    continue;

                rows.Add(exportBooth);
                ThrottleIfNeeded(processed);
            }

            cursor = next;
        }

        return rows;
    }

    private static BoothsModel TryGetFullBooth(
        ApiClient client,
        BoothsModel searchBooth,
        out bool usedFullGet,
        out bool usedSearchFallback)
    {
        usedFullGet = false;
        usedSearchFallback = false;

        if (string.IsNullOrWhiteSpace(DetectedKeyField))
            return searchBooth;

        object? rawKey = GetPropertyValue(searchBooth, DetectedKeyField);
        if (rawKey == null || !int.TryParse(rawKey.ToString(), out int sequenceNumber))
            return searchBooth;

        try
        {
            BoothsModel fullBooth = client.Endpoints.Booths.Get(OrgCode, sequenceNumber);
            usedFullGet = true;
            return fullBooth;
        }
        catch (Exception ex)
        {
            usedSearchFallback = true;
            Console.WriteLine(
                $"WARNING: Full booth GET failed for {DetectedKeyField}={sequenceNumber}. " +
                $"Using the booth record returned by Search instead. Reason: {GetInnermostMessage(ex)}");
            return searchBooth;
        }
    }

    private static string GetInnermostMessage(Exception ex)
    {
        Exception current = ex;
        while (current.InnerException != null)
            current = current.InnerException;

        return current.Message;
    }

    private static Dictionary<string, string> CreateRow(BoothsModel booth)
    {
        var row = CreateBlankRow();
        row["PullRunOn"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        foreach (string column in DesiredColumns)
        {
            if (column.Equals("PullRunOn", StringComparison.OrdinalIgnoreCase))
                continue;

            object? value = GetPropertyValue(booth, column);
            row[column] = ToStringSafe(value);
        }

        return row;
    }

    private static object? GetPropertyValue(object source, string propertyName)
    {
        PropertyInfo? property = source.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        return property?.GetValue(source);
    }

    private static DateTime GetDateTimeProperty(object source, string propertyName)
    {
        object? value = GetPropertyValue(source, propertyName);

        if (value == null)
            return DateTime.MinValue;

        if (value is DateTime dt)
            return dt;

        if (value is DateTimeOffset dto)
            return dto.LocalDateTime;

        return DateTime.TryParse(value.ToString(), out DateTime parsed)
            ? parsed
            : DateTime.MinValue;
    }

    private static DateTime GetEffectiveSince()
    {
        DateTime since;

        if (File.Exists(RunStatePath))
        {
            string raw = File.ReadAllText(RunStatePath).Trim();
            if (!DateTime.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out since))
            {
                throw new InvalidOperationException(
                    $"Could not parse run-state timestamp in {RunStatePath}.");
            }
        }
        else if (File.Exists(OutputFilePath))
        {
            since = File.GetLastWriteTime(OutputFilePath);
        }
        else
        {
            // Main() should have converted this situation to a full rebuild.
            since = DateTime.MinValue;
        }

        return since - TimeSpan.FromMinutes(Math.Max(0, Settings.OverlapMinutes));
    }

    private static Dictionary<string, Dictionary<string, string>> LoadExistingRows(string path)
    {
        var rows = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
            return rows;

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null
        };

        using var reader = new StreamReader(path, Encoding.UTF8);
        using var csv = new CsvReader(reader, config);

        if (!csv.Read() || !csv.ReadHeader())
            return rows;

        string[] headers = csv.HeaderRecord ?? Array.Empty<string>();

        while (csv.Read())
        {
            var row = CreateBlankRow();

            foreach (string header in headers)
            {
                if (row.ContainsKey(header))
                    row[header] = csv.GetField(header) ?? string.Empty;
            }

            string key = GetRowKey(row);
            if (!string.IsNullOrWhiteSpace(key))
                rows[key] = row;
        }

        return rows;
    }

    private static Dictionary<string, string> CreateBlankRow()
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string column in DesiredColumns)
            row[column] = string.Empty;
        return row;
    }

    private static string GetRowKey(Dictionary<string, string> row)
    {
        if (!string.IsNullOrWhiteSpace(DetectedKeyField))
        {
            string keyValue = GetValue(row, DetectedKeyField);
            if (!string.IsNullOrWhiteSpace(keyValue))
                return $"SEQ:{keyValue.Trim()}";
        }

        string eventId = GetValue(row, "Event").Trim();
        string functionId = GetValue(row, "Function").Trim();
        string booth = GetValue(row, "Booth").Trim();

        if (string.IsNullOrWhiteSpace(eventId)
            || string.IsNullOrWhiteSpace(functionId)
            || string.IsNullOrWhiteSpace(booth))
        {
            return string.Empty;
        }

        return $"BOOTH:{eventId}|{functionId}|{booth}";
    }

    private static List<Dictionary<string, string>> SortRows(
        IEnumerable<Dictionary<string, string>> rows)
    {
        return rows
            .OrderBy(r => ToLong(GetValue(r, "Event")))
            .ThenBy(r => ToLong(GetValue(r, "Function")))
            .ThenBy(r => GetValue(r, "Booth"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => string.IsNullOrWhiteSpace(DetectedKeyField)
                ? string.Empty
                : GetValue(r, DetectedKeyField), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetValue(Dictionary<string, string> row, string columnName)
    {
        return row.TryGetValue(columnName, out string? value)
            ? value ?? string.Empty
            : string.Empty;
    }

    private static long ToLong(string value)
    {
        return long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out long number)
            ? number
            : 0L;
    }

    private static void PublishOutput(List<Dictionary<string, string>> rows)
    {
        string tempOutputPath = AtomicFilePublisher.CreateTemporaryPath(OutputFilePath);
        WriteCsv(tempOutputPath, rows, DesiredColumns);
        AtomicFilePublisher.Publish(tempOutputPath, OutputFilePath, OutputFilePath + ".bak");
    }

    private static void WriteRunState(string path, DateTime value)
    {
        File.WriteAllText(
            path,
            value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    private static void ThrottleIfNeeded(int processed)
    {
        if (Settings.ThrottleEveryRecords <= 0 || Settings.ThrottleDelayMilliseconds <= 0)
            return;

        if (processed % Settings.ThrottleEveryRecords == 0)
            System.Threading.Thread.Sleep(Settings.ThrottleDelayMilliseconds);
    }

    private static string ToStringSafe(object? value)
    {
        if (value == null)
            return string.Empty;

        return value switch
        {
            string s => s,
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            byte[] bytes => Convert.ToBase64String(bytes),
            Enum e => e.ToString(),
            IEnumerable enumerable when value is not string => SerializeComplexValue(enumerable),
            _ when IsSimpleValue(value.GetType()) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => SerializeComplexValue(value)
        };
    }

    private static bool IsSimpleValue(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive
            || underlying.IsEnum
            || underlying == typeof(string)
            || underlying == typeof(decimal)
            || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset)
            || underlying == typeof(Guid)
            || underlying == typeof(TimeSpan);
    }

    private static string SerializeComplexValue(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value, value.GetType());
        }
        catch
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static void WriteCsv(
        string path,
        List<Dictionary<string, string>> rows,
        List<string> columns)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            Encoding = new UTF8Encoding(true)
        };

        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        using var csv = new CsvWriter(writer, config);

        foreach (string column in columns)
            csv.WriteField(column);
        csv.NextRecord();

        foreach (var row in rows)
        {
            foreach (string column in columns)
            {
                row.TryGetValue(column, out string? value);
                csv.WriteField(value ?? string.Empty);
            }
            csv.NextRecord();
        }
    }
}

sealed class AppSettingsRoot
{
    public BoothPullSettings BoothsPull { get; set; } = new();
}

sealed class BoothPullSettings
{
    public string DefaultMode { get; set; } = "Incremental";
    public string OutputFileName { get; set; } = "Booths_Pull.csv";
    public string RunStateFileName { get; set; } = "Booths_Pull.last_run.txt";
    public int OverlapMinutes { get; set; } = 10;
    public string ChangeField { get; set; } = "Auto";
    public string KeyField { get; set; } = "Auto";
    public bool IncrementalFallbackToFullScan { get; set; } = true;
    public string FullSearchFilter { get; set; } = "All";
    public int SearchMaxResults { get; set; } = 500000;
    public int SearchPageSize { get; set; } = 500;
    public int ThrottleEveryRecords { get; set; } = 100;
    public int ThrottleDelayMilliseconds { get; set; } = 500;
}
