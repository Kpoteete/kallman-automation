using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsvHelper;
using CsvHelper.Configuration;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Options;
using Ungerboeck.Api.Sdk;

namespace GLDataMillPull;

internal static class Program
{
    public static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        try
        {
            CliOptions options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(CliOptions.HelpText);
                return 0;
            }

            MomentusCredentials credentials = MomentusCredentials.FromEnvironment();
            using var client = MomentusClientFactory.Create(credentials, options.BaseUrl);
            var reader = new MomentusEndpointReader(client, options);
            IReadOnlyList<DatasetSpec> datasets = DatasetCatalog.Select(options.DatasetNames);

            if (options.Mode == RunMode.Probe)
                return ProbeRunner.Run(reader, datasets);

            string outputFolder = OutputPaths.ResolveFolder(options.OutputFolder);
            Directory.CreateDirectory(outputFolder);
            using var runLock = RunLock.Acquire(outputFolder);
            return options.Mode switch
            {
                RunMode.Full => FullSnapshotRunner.Run(reader, datasets, outputFolder, options),
                RunMode.Incremental => IncrementalRunner.Run(reader, datasets, outputFolder, options),
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
            Console.Error.WriteLine("FATAL: existing warehouse exports were left in place.");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}

internal enum RunMode { None, Probe, Full, Incremental }

internal sealed record CliOptions(
    RunMode Mode,
    string OrganizationCode,
    string BaseUrl,
    string OutputFolder,
    IReadOnlyList<string> DatasetNames,
    int PageSize,
    int MaxResults,
    int RequestDelayMs,
    int MaxAttempts,
    int OverlapHours,
    bool ShowHelp)
{
    public static readonly string HelpText = """
GLDataMillPull - read-only Momentus general-ledger export for Datamill

Usage:
  dotnet run -c Release -- probe [options]
  dotnet run -c Release -- full [options]
  dotnet run -c Release -- incremental [options]

Modes:
  probe   Read counts from Momentus. Writes no warehouse data.
  full    Build and validate every selected CSV before publishing any of them.
  incremental
          Pull ChangedOn since each durable checkpoint, apply key-based upserts,
          and advance checkpoints only after successful publication.

Options:
  --dataset NAME[,NAME]     Limit the run to named datasets. May be repeated.
  --output-folder PATH      Default: KALLMAN_DATA_WAREHOUSE\Momentus GL.
  --org CODE                Default: 10.
  --base-url URL            Default: https://kallman.ungerboeck.com/prod.
  --page-size N             API page size (default: 250; maximum: 1000).
  --max-results N           Guarded API result ceiling (default: 1000000).
  --request-delay-ms N      Delay after each API request (default: 250).
  --max-attempts N          Transient read attempts (default: 6).
  --overlap-hours N         ChangedOn safety overlap (default: 48).
  --help                    Show this help.

Required environment variables:
  MOMENTUS_APIUSER, MOMENTUS_SECRET, MOMENTUS_KEY

The program only calls Momentus Search/NavigateSearchList methods. It contains no
Momentus add, update, post, or delete operation. Existing Datamill files are not
replaced until the entire selected snapshot has been written and reconciled.
""";

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase)))
            return Defaults(RunMode.None) with { ShowHelp = true };

        RunMode mode = args[0].ToLowerInvariant() switch
        {
            "probe" => RunMode.Probe,
            "full" => RunMode.Full,
            "incremental" or "daily" => RunMode.Incremental,
            _ => throw new CliException($"Unknown mode '{args[0]}'.")
        };

        CliOptions result = Defaults(mode);
        var datasets = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            string NextValue()
            {
                if (++i >= args.Length)
                    throw new CliException($"Missing value after {args[i - 1]}.");
                return args[i];
            }

            switch (args[i].ToLowerInvariant())
            {
                case "--dataset":
                    datasets.AddRange(NextValue().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--output-folder":
                    result = result with { OutputFolder = NextValue() };
                    break;
                case "--org":
                    result = result with { OrganizationCode = NextValue() };
                    break;
                case "--base-url":
                    result = result with { BaseUrl = NextValue().TrimEnd('/') };
                    break;
                case "--page-size":
                    result = result with { PageSize = ParseInt(NextValue(), "--page-size", 1, 1000) };
                    break;
                case "--max-results":
                    result = result with { MaxResults = ParseInt(NextValue(), "--max-results", 1, 10_000_000) };
                    break;
                case "--request-delay-ms":
                    result = result with { RequestDelayMs = ParseInt(NextValue(), "--request-delay-ms", 0, 60_000) };
                    break;
                case "--max-attempts":
                    result = result with { MaxAttempts = ParseInt(NextValue(), "--max-attempts", 1, 20) };
                    break;
                case "--overlap-hours":
                    result = result with { OverlapHours = ParseInt(NextValue(), "--overlap-hours", 1, 24 * 30) };
                    break;
                default:
                    throw new CliException($"Unknown option '{args[i]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(result.OrganizationCode))
            throw new CliException("--org cannot be blank.");
        if (!Uri.TryCreate(result.BaseUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new CliException("--base-url must be an absolute HTTPS URL.");

        return result with { DatasetNames = datasets };
    }

    private static CliOptions Defaults(RunMode mode)
    {
        string warehouseRoot = Environment.GetEnvironmentVariable("KALLMAN_DATA_WAREHOUSE")?.Trim()
            ?? @"C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents";

        return new(
            mode,
            "10",
            "https://kallman.ungerboeck.com/prod",
            Path.Combine(warehouseRoot, "Momentus GL"),
            [],
            250,
            1_000_000,
            250,
            6,
            48,
            false);
    }

    private static int ParseInt(string value, string option, int min, int max) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
        parsed >= min && parsed <= max
            ? parsed
            : throw new CliException($"{option} must be an integer from {min:N0} through {max:N0}.");
}

internal sealed class CliException(string message) : Exception(message);

internal sealed record MomentusCredentials(string ApiUserId, string Secret, string Key)
{
    public static MomentusCredentials FromEnvironment()
    {
        var credentials = new MomentusCredentials(
            Environment.GetEnvironmentVariable("MOMENTUS_APIUSER")?.Trim() ?? "",
            Environment.GetEnvironmentVariable("MOMENTUS_SECRET")?.Trim() ?? "",
            Environment.GetEnvironmentVariable("MOMENTUS_KEY")?.Trim() ?? "");

        var missing = new List<string>();
        if (credentials.ApiUserId.Length == 0) missing.Add("MOMENTUS_APIUSER");
        if (credentials.Secret.Length == 0) missing.Add("MOMENTUS_SECRET");
        if (credentials.Key.Length == 0) missing.Add("MOMENTUS_KEY");
        if (missing.Count > 0)
            throw new InvalidOperationException("Missing required environment variable(s): " + string.Join(", ", missing));

        return credentials;
    }
}

internal static class MomentusClientFactory
{
    public static ApiClient Create(MomentusCredentials credentials, string baseUrl)
    {
        var authorization = new Jwt
        {
            APIUserID = credentials.ApiUserId,
            Secret = credentials.Secret,
            Key = credentials.Key,
            UngerboeckURI = baseUrl,
            AutoRefresh = new AutoRefresh()
        };

        return new ApiClient(authorization);
    }
}

internal sealed record DatasetSpec(
    string Name,
    string EndpointProperty,
    string FileName,
    bool RequiresOrganization,
    IReadOnlyList<string> KeyProperties,
    string? ChangedOnProperty,
    string Purpose);

internal static class DatasetCatalog
{
    public static readonly IReadOnlyList<DatasetSpec> All =
    [
        new("GLAccountAnalysisCodes", "GLAccountAnalysisCodes", "GL_Account_Analysis_Codes.csv", true, ["OrganizationCode", "Code"], null, "Account analysis dimension definitions"),
        new("GLAccounts", "GLAccounts", "GL_Accounts.csv", true, ["Organization", "GLAccount", "SubAccount"], "ChangedOn", "Complete account and subaccount combinations"),
        new("GLDeferralRevenueDetails", "GLDeferralRevenueDetails", "GL_Deferral_Revenue_Details.csv", false, ["HdrSequence", "Sequence"], "ChangedOn", "Deferred-revenue recognition schedule lines"),
        new("GLDeferralRevenueHeaders", "GLDeferralRevenueHeaders", "GL_Deferral_Revenue_Headers.csv", false, ["Sequence"], "ChangedOn", "Deferred-revenue source records"),
        new("GLDistributions", "GLDistributions", "GL_Distributions.csv", true, ["Organization", "RecordType", "SequenceNumber"], "ChangedOn", "Revenue and cost distribution rules"),
        new("GLMainAccounts", "GLMainAccounts", "GL_Main_Accounts.csv", true, ["Organization", "GLMainAccount"], "ChangedOn", "Main chart of accounts"),
        new("GLSources", "GLSources", "GL_Sources.csv", true, ["Organization", "Source"], "ChangedOn", "Journal source definitions"),
        new("GLSpaceMajor", "GLSpaceMajor", "GL_Space_Major.csv", true, ["OrganizationCode", "Code"], "ChangedOn", "Major space accounting dimensions"),
        new("GLSpaceMinor", "GLSpaceMinor", "GL_Space_Minor.csv", true, ["OrganizationCode", "Code"], "ChangedOn", "Minor space accounting dimensions"),
        new("CoreDimensions", "CoreDimensions", "GL_Core_Dimensions.csv", true, ["OrganizationCode", "Code"], null, "Core financial dimension definitions"),
        new("FiscalYears", "FiscalYears", "GL_Fiscal_Years.csv", true, ["Organization", "Year"], "ChangedOn", "Fiscal-year controls"),
        new("FiscalPeriods", "FiscalPeriods", "GL_Fiscal_Periods.csv", true, ["OrganizationCode", "Module", "Year", "Period"], "ChangedOn", "Fiscal-period date and status definitions"),
        new("JournalEntries", "JournalEntries", "GL_Journal_Entries.csv", true, ["Organization", "Year", "Period", "Source", "EntryNumber"], "ChangedOn", "Journal headers"),
        new("JournalEntryDetails", "JournalEntryDetails", "GL_Journal_Entry_Details.csv", true, ["Organization", "Year", "Period", "Source", "EntryNumber", "Line"], "ChangedOn", "Posted and unposted GL lines used for financial reporting"),
        new("DailyRevenueAndCostAnalysis", "DailyRevenueAndCostAnalysis", "GL_Daily_Revenue_And_Cost.csv", true, ["Organization", "RevenueSequence"], "ChangedOn", "Event/order revenue, direct cost, and margin facts")
    ];

    public static IReadOnlyList<DatasetSpec> Select(IReadOnlyList<string> requested)
    {
        if (requested.Count == 0)
            return All;

        var selected = new List<DatasetSpec>();
        foreach (string name in requested)
        {
            DatasetSpec? match = All.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new CliException(
                    $"Unknown dataset '{name}'. Valid names: {string.Join(", ", All.Select(d => d.Name))}.");
            if (!selected.Contains(match))
                selected.Add(match);
        }

        return selected;
    }
}

internal sealed record SearchPage(
    Type ModelType,
    IReadOnlyList<object> Rows,
    long? ResultsTotal,
    string? NextUrl);

internal sealed class MomentusEndpointReader
{
    private readonly ApiClient client;
    private readonly CliOptions options;
    private readonly Search searchOptions;

    public MomentusEndpointReader(ApiClient client, CliOptions options)
    {
        this.client = client;
        this.options = options;
        searchOptions = new Search { PageSize = options.PageSize, MaxResults = options.MaxResults };
    }

    public SearchPage ReadFirst(DatasetSpec spec, string searchFilter = "All")
    {
        object endpoint = GetEndpoint(spec.EndpointProperty);
        object?[] arguments = spec.RequiresOrganization
            ? [options.OrganizationCode, searchFilter, searchOptions]
            : [searchFilter, searchOptions];
        object response = InvokeWithRetry(endpoint, "Search", arguments);
        return ParsePage(response);
    }

    public SearchPage ReadNext(DatasetSpec spec, string nextUrl)
    {
        object endpoint = GetEndpoint(spec.EndpointProperty);
        object response = InvokeWithRetry(endpoint, "NavigateSearchList", [nextUrl, searchOptions]);
        return ParsePage(response);
    }

    private object GetEndpoint(string propertyName)
    {
        PropertyInfo property = client.Endpoints.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?? throw new MissingMemberException(client.Endpoints.GetType().FullName, propertyName);

        return property.GetValue(client.Endpoints)
            ?? throw new InvalidOperationException($"Momentus endpoint '{propertyName}' returned null.");
    }

    private object InvokeWithRetry(object target, string methodName, object?[] preferredArguments)
    {
        MethodInfo method = FindCompatibleMethod(target.GetType(), methodName, preferredArguments)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);

        object?[] arguments = preferredArguments.Take(method.GetParameters().Length).ToArray();
        Exception? last = null;

        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            try
            {
                object? result = method.Invoke(target, arguments);
                if (result is null)
                    throw new InvalidOperationException($"{target.GetType().Name}.{methodName} returned null.");
                Delay();
                return result;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                last = ex.InnerException;
                if (attempt >= options.MaxAttempts || !TransientErrors.IsTransient(last))
                    ExceptionDispatchInfo.Capture(last).Throw();
                RetryDelay(attempt);
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt >= options.MaxAttempts || !TransientErrors.IsTransient(ex))
                    throw;
                RetryDelay(attempt);
            }
        }

        throw last ?? new InvalidOperationException("Momentus request failed without an exception.");
    }

    private static MethodInfo? FindCompatibleMethod(Type type, string name, object?[] arguments) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name.Equals(name, StringComparison.Ordinal))
            .Where(m => !m.IsGenericMethodDefinition)
            .Where(m => Compatible(m.GetParameters(), arguments))
            .OrderByDescending(m => m.GetParameters().Length)
            .FirstOrDefault();

    private static bool Compatible(ParameterInfo[] parameters, object?[] arguments)
    {
        if (parameters.Length > arguments.Length)
            return false;

        for (var i = 0; i < parameters.Length; i++)
        {
            object? argument = arguments[i];
            if (argument is null)
                continue;
            if (!parameters[i].ParameterType.IsAssignableFrom(argument.GetType()))
                return false;
        }

        return true;
    }

    internal static SearchPage ParsePage(object response)
    {
        PropertyInfo resultsProperty = response.GetType().GetProperty("Results")
            ?? throw new InvalidDataException("Momentus search response has no Results property.");
        Type modelType = resultsProperty.PropertyType.IsGenericType
            ? resultsProperty.PropertyType.GetGenericArguments()[0]
            : typeof(object);

        var rows = new List<object>();
        if (resultsProperty.GetValue(response) is IEnumerable enumerable)
        {
            foreach (object? row in enumerable)
            {
                if (row is not null)
                {
                    modelType = row.GetType();
                    rows.Add(row);
                }
            }
        }

        object? metadata = response.GetType().GetProperty("SearchMetadata")?.GetValue(response);
        long? total = ReflectionValue.Long(metadata, "ResultsTotal");
        object? links = ReflectionValue.Get(metadata, "Links");
        string? next = ReflectionValue.String(links, "Next");
        return new SearchPage(modelType, rows, total, string.IsNullOrWhiteSpace(next) ? null : next);
    }

    private void Delay()
    {
        if (options.RequestDelayMs > 0)
            Thread.Sleep(options.RequestDelayMs);
    }

    private static void RetryDelay(int attempt)
    {
        double jitter = 1 + Random.Shared.NextDouble() * 0.25;
        int milliseconds = (int)Math.Min(30_000, 1000 * Math.Pow(2, attempt - 1) * jitter);
        Thread.Sleep(milliseconds);
    }
}

internal static class ReflectionValue
{
    public static object? Get(object? target, string propertyName) =>
        target?.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(target);

    public static string? String(object? target, string propertyName) =>
        Convert.ToString(Get(target, propertyName), CultureInfo.InvariantCulture);

    public static long? Long(object? target, string propertyName)
    {
        object? value = Get(target, propertyName);
        if (value is null)
            return null;
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}

internal static class TransientErrors
{
    public static bool IsTransient(Exception exception)
    {
        if (exception is HttpRequestException http &&
            (http.StatusCode is null ||
             http.StatusCode == HttpStatusCode.RequestTimeout ||
             http.StatusCode == HttpStatusCode.TooManyRequests ||
             (int)http.StatusCode >= 500))
            return true;

        if (exception is TimeoutException or TaskCanceledException)
            return true;

        string message = exception.ToString();
        return message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("502", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("503", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("504", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class ProbeRunner
{
    public static int Run(MomentusEndpointReader reader, IReadOnlyList<DatasetSpec> datasets)
    {
        Console.WriteLine("Read-only Momentus GL probe");
        Console.WriteLine("Dataset\tReportedRows\tFirstPageRows");
        foreach (DatasetSpec spec in datasets)
        {
            SearchPage page = reader.ReadFirst(spec);
            Console.WriteLine($"{spec.Name}\t{page.ResultsTotal?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown"}\t{page.Rows.Count:N0}");
        }

        Console.WriteLine("Probe complete. No warehouse data was written.");
        return 0;
    }
}

internal sealed record DatasetResult(
    string Dataset,
    string FileName,
    long Rows,
    long? ReportedRows,
    string Purpose,
    DateTimeOffset CompletedAtUtc);

internal static class FullSnapshotRunner
{
    public static int Run(
        MomentusEndpointReader reader,
        IReadOnlyList<DatasetSpec> datasets,
        string outputFolder,
        CliOptions options)
    {
        DateTimeOffset checkpointUtc = DateTimeOffset.UtcNow;
        string runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string stagingFolder = Path.Combine(outputFolder, "_GLDataMillPull_staging", runId);
        Directory.CreateDirectory(stagingFolder);

        var staged = new List<(DatasetSpec Spec, DatasetResult Result, string Path)>();
        Console.WriteLine($"Building {datasets.Count} GL dataset(s) in staging. Existing Datamill files remain untouched.");

        foreach (DatasetSpec spec in datasets)
        {
            string stagedPath = Path.Combine(stagingFolder, spec.FileName);
            DatasetResult result = CsvDatasetWriter.Write(reader, spec, stagedPath);
            staged.Add((spec, result, stagedPath));
            Console.WriteLine($"{spec.Name}: {result.Rows:N0} rows verified.");
        }

        string backupFolder = Path.Combine(outputFolder, "_GLDataMillPull_backups");
        Directory.CreateDirectory(backupFolder);
        foreach ((DatasetSpec spec, _, string stagedPath) in staged)
        {
            string destination = Path.Combine(outputFolder, spec.FileName);
            if (File.Exists(destination))
            {
                string backup = Path.Combine(
                    backupFolder,
                    $"{Path.GetFileNameWithoutExtension(spec.FileName)}.{runId}.before_full.csv");
                File.Copy(destination, backup, overwrite: false);
            }

            File.Move(stagedPath, destination, overwrite: true);
        }

        var manifest = new
        {
            RunId = runId,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Mode = "full",
            OrganizationCode = options.OrganizationCode,
            BaseUrl = options.BaseUrl,
            ReadOnlyMomentus = true,
            Datasets = staged.Select(s => s.Result).ToArray()
        };
        ManifestPublisher.Publish(outputFolder, manifest, runId);
        CheckpointStore.PublishAll(outputFolder, datasets, checkpointUtc, "full", runId);

        Console.WriteLine($"Published {staged.Count} CSV file(s) to {outputFolder}");
        Console.WriteLine("Momentus was read only; no production API records were changed.");
        return 0;
    }
}

internal static class IncrementalRunner
{
    public static int Run(
        MomentusEndpointReader reader,
        IReadOnlyList<DatasetSpec> datasets,
        string outputFolder,
        CliOptions options)
    {
        DateTimeOffset runStartedUtc = DateTimeOffset.UtcNow;
        string runId = runStartedUtc.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string stagingFolder = Path.Combine(outputFolder, "_GLDataMillPull_staging", runId);
        Directory.CreateDirectory(stagingFolder);

        var staged = new List<(DatasetSpec Spec, DatasetResult Result, string Path)>();
        var unchanged = new List<DatasetSpec>();
        Console.WriteLine($"Building incremental GL snapshot with a {options.OverlapHours}-hour ChangedOn overlap.");

        foreach (DatasetSpec spec in datasets)
        {
            string existingPath = Path.Combine(outputFolder, spec.FileName);
            if (!File.Exists(existingPath))
                throw new InvalidOperationException(
                    $"{spec.Name} has no existing CSV at '{existingPath}'. Run full before incremental.");

            string stagedPath = Path.Combine(stagingFolder, spec.FileName);
            if (spec.ChangedOnProperty is null)
            {
                DatasetResult refreshed = CsvDatasetWriter.Write(reader, spec, stagedPath);
                staged.Add((spec, refreshed, stagedPath));
                Console.WriteLine($"{spec.Name}: small reference snapshot refreshed ({refreshed.Rows:N0} rows).");
                continue;
            }

            DatasetCheckpoint checkpoint = CheckpointStore.Read(outputFolder, spec.Name);
            DateTimeOffset changedSince = checkpoint.CheckpointUtc.AddHours(-options.OverlapHours);
            string filter =
                $"{spec.ChangedOnProperty} ge DateTime'{changedSince.UtcDateTime:yyyy-MM-ddTHH:mm:ss}'";
            IReadOnlyList<object> changes = CsvIncrementalWriter.ReadAll(reader, spec, filter);

            if (changes.Count == 0)
            {
                unchanged.Add(spec);
                Console.WriteLine($"{spec.Name}: no rows changed since {changedSince:O}.");
                continue;
            }

            DatasetResult result = CsvIncrementalWriter.Upsert(
                spec,
                existingPath,
                stagedPath,
                changes,
                runStartedUtc);
            staged.Add((spec, result, stagedPath));
            Console.WriteLine($"{spec.Name}: applied {changes.Count:N0} changed row(s); {result.Rows:N0} total rows.");
        }

        string previousFolder = Path.Combine(outputFolder, "_GLDataMillPull_previous");
        Directory.CreateDirectory(previousFolder);
        foreach ((DatasetSpec spec, _, string stagedPath) in staged)
        {
            string destination = Path.Combine(outputFolder, spec.FileName);
            string previous = Path.Combine(
                previousFolder,
                $"{Path.GetFileNameWithoutExtension(spec.FileName)}.previous.csv");
            File.Copy(destination, previous, overwrite: true);
            File.Move(stagedPath, destination, overwrite: true);
        }

        var manifest = new
        {
            RunId = runId,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Mode = "incremental",
            OrganizationCode = options.OrganizationCode,
            BaseUrl = options.BaseUrl,
            ReadOnlyMomentus = true,
            ChangedOnOverlapHours = options.OverlapHours,
            UpdatedDatasets = staged.Select(s => s.Result).ToArray(),
            UnchangedDatasets = unchanged.Select(s => s.Name).ToArray()
        };
        ManifestPublisher.Publish(outputFolder, manifest, runId);
        CheckpointStore.PublishAll(outputFolder, datasets, runStartedUtc, "incremental", runId);

        Console.WriteLine(
            $"Incremental publication complete: {staged.Count} file(s) updated, {unchanged.Count} unchanged.");
        Console.WriteLine("Momentus was read only; no production API records were changed.");
        return 0;
    }
}

internal static class CsvDatasetWriter
{
    private static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture)
    {
        Encoding = new UTF8Encoding(true),
        HasHeaderRecord = true,
        NewLine = "\r\n"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        WriteIndented = false
    };

    public static DatasetResult Write(
        MomentusEndpointReader reader,
        DatasetSpec spec,
        string outputPath,
        string searchFilter = "All")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("Staged CSV has no parent directory."));

        SearchPage page = reader.ReadFirst(spec, searchFilter);
        PropertyInfo[] properties = ExportProperties(page.ModelType);
        long rowsWritten = 0;
        long? reportedRows = page.ResultsTotal;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, CsvConfig.Encoding))
        using (var csv = new CsvWriter(writer, CsvConfig))
        {
            foreach (PropertyInfo property in properties)
                csv.WriteField(property.Name);
            csv.WriteField("_ExtractedAtUtc");
            csv.WriteField("_SourceEndpoint");
            csv.NextRecord();

            DateTimeOffset extractedAt = DateTimeOffset.UtcNow;
            while (true)
            {
                foreach (object row in page.Rows)
                {
                    string key = DatasetKey.FromObject(spec, row);
                    if (!keys.Add(key))
                        throw new InvalidDataException($"{spec.Name} returned duplicate key '{key}'.");
                    foreach (PropertyInfo property in properties)
                        csv.WriteField(FormatValue(property.GetValue(row)));
                    csv.WriteField(extractedAt.ToString("O", CultureInfo.InvariantCulture));
                    csv.WriteField(spec.EndpointProperty);
                    csv.NextRecord();
                    rowsWritten++;
                }

                if (string.IsNullOrWhiteSpace(page.NextUrl))
                    break;
                if (!visited.Add(page.NextUrl))
                    throw new InvalidDataException($"{spec.Name} returned a repeated Next link.");

                page = reader.ReadNext(spec, page.NextUrl);
                if (reportedRows.HasValue && page.ResultsTotal.HasValue && reportedRows != page.ResultsTotal)
                    throw new InvalidDataException(
                        $"{spec.Name} changed during extraction: total moved from {reportedRows:N0} to {page.ResultsTotal:N0}.");
            }

            writer.Flush();
            stream.Flush(true);
        }

        if (reportedRows.HasValue && rowsWritten != reportedRows.Value)
            throw new InvalidDataException(
                $"{spec.Name} reconciliation failed: Momentus reported {reportedRows:N0}, but {rowsWritten:N0} rows were written.");

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new InvalidDataException($"{spec.Name} produced an empty CSV file.");

        return new DatasetResult(
            spec.Name,
            spec.FileName,
            rowsWritten,
            reportedRows,
            spec.Purpose,
            DateTimeOffset.UtcNow);
    }

    internal static PropertyInfo[] ExportProperties(Type modelType) =>
        modelType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.MetadataToken)
            .ToArray();

    internal static string FormatValue(object? value)
    {
        if (value is null)
            return "";
        return value switch
        {
            string text => text,
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            byte[] bytes => Convert.ToBase64String(bytes),
            IFormattable formattable when IsScalar(value.GetType()) =>
                formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
            _ => JsonSerializer.Serialize(value, value.GetType(), JsonOptions)
        };
    }

    private static bool IsScalar(Type type)
    {
        Type actual = Nullable.GetUnderlyingType(type) ?? type;
        return actual.IsPrimitive ||
               actual.IsEnum ||
               actual == typeof(decimal) ||
               actual == typeof(Guid) ||
               actual == typeof(TimeSpan);
    }
}

internal static class CsvIncrementalWriter
{
    private static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture)
    {
        Encoding = new UTF8Encoding(true),
        HasHeaderRecord = true,
        NewLine = "\r\n",
        MissingFieldFound = null
    };

    public static IReadOnlyList<object> ReadAll(
        MomentusEndpointReader reader,
        DatasetSpec spec,
        string searchFilter)
    {
        SearchPage page = reader.ReadFirst(spec, searchFilter);
        long? reported = page.ResultsTotal;
        var rows = new List<object>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            foreach (object row in page.Rows)
            {
                string key = DatasetKey.FromObject(spec, row);
                if (!keys.Add(key))
                    throw new InvalidDataException($"{spec.Name} incremental query returned duplicate key '{key}'.");
                rows.Add(row);
            }

            if (string.IsNullOrWhiteSpace(page.NextUrl))
                break;
            if (!visited.Add(page.NextUrl))
                throw new InvalidDataException($"{spec.Name} incremental query returned a repeated Next link.");

            page = reader.ReadNext(spec, page.NextUrl);
            if (reported.HasValue && page.ResultsTotal.HasValue && reported != page.ResultsTotal)
                throw new InvalidDataException(
                    $"{spec.Name} changed during incremental extraction: total moved from {reported:N0} to {page.ResultsTotal:N0}.");
        }

        if (reported.HasValue && rows.Count != reported.Value)
            throw new InvalidDataException(
                $"{spec.Name} incremental reconciliation failed: Momentus reported {reported:N0}, but {rows.Count:N0} rows were read.");
        return rows;
    }

    public static DatasetResult Upsert(
        DatasetSpec spec,
        string existingPath,
        string outputPath,
        IReadOnlyList<object> changes,
        DateTimeOffset extractedAtUtc)
    {
        var changesByKey = changes.ToDictionary(
            row => DatasetKey.FromObject(spec, row),
            row => row,
            StringComparer.Ordinal);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var outputKeys = new HashSet<string>(StringComparer.Ordinal);

        using var inputStream = new FileStream(existingPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var inputReader = new StreamReader(inputStream, detectEncodingFromByteOrderMarks: true);
        using var inputCsv = new CsvReader(inputReader, CsvConfig);
        if (!inputCsv.Read() || !inputCsv.ReadHeader() || inputCsv.HeaderRecord is null)
            throw new InvalidDataException($"{spec.Name} existing CSV has no header.");
        string[] headers = inputCsv.HeaderRecord;

        foreach (string keyProperty in spec.KeyProperties)
        {
            if (!headers.Contains(keyProperty, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"{spec.Name} existing CSV is missing key column '{keyProperty}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("Staged incremental CSV has no parent directory."));
        long rowsWritten = 0;
        using (var outputStream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var outputWriter = new StreamWriter(outputStream, CsvConfig.Encoding))
        using (var outputCsv = new CsvWriter(outputWriter, CsvConfig))
        {
            foreach (string header in headers)
                outputCsv.WriteField(header);
            outputCsv.NextRecord();

            while (inputCsv.Read())
            {
                string existingKey = DatasetKey.FromCsv(spec, inputCsv);
                if (!outputKeys.Add(existingKey))
                    throw new InvalidDataException($"{spec.Name} existing CSV contains duplicate key '{existingKey}'.");

                if (changesByKey.TryGetValue(existingKey, out object? changed))
                {
                    WriteObjectRow(outputCsv, headers, changed, spec, extractedAtUtc);
                    matched.Add(existingKey);
                }
                else
                {
                    foreach (string header in headers)
                        outputCsv.WriteField(inputCsv.GetField(header));
                    outputCsv.NextRecord();
                }
                rowsWritten++;
            }

            foreach ((string key, object changed) in changesByKey)
            {
                if (matched.Contains(key))
                    continue;
                if (!outputKeys.Add(key))
                    throw new InvalidDataException($"{spec.Name} attempted to append duplicate key '{key}'.");
                WriteObjectRow(outputCsv, headers, changed, spec, extractedAtUtc);
                rowsWritten++;
            }

            outputWriter.Flush();
            outputStream.Flush(true);
        }

        return new DatasetResult(
            spec.Name,
            spec.FileName,
            rowsWritten,
            changes.Count,
            spec.Purpose,
            DateTimeOffset.UtcNow);
    }

    private static void WriteObjectRow(
        CsvWriter csv,
        IReadOnlyList<string> headers,
        object row,
        DatasetSpec spec,
        DateTimeOffset extractedAtUtc)
    {
        Type type = row.GetType();
        foreach (string header in headers)
        {
            if (header.Equals("_ExtractedAtUtc", StringComparison.OrdinalIgnoreCase))
            {
                csv.WriteField(extractedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                continue;
            }
            if (header.Equals("_SourceEndpoint", StringComparison.OrdinalIgnoreCase))
            {
                csv.WriteField(spec.EndpointProperty);
                continue;
            }

            PropertyInfo property = type.GetProperty(
                header,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                ?? throw new InvalidDataException(
                    $"{spec.Name} API model no longer has existing CSV column '{header}'.");
            csv.WriteField(CsvDatasetWriter.FormatValue(property.GetValue(row)));
        }
        csv.NextRecord();
    }
}

internal static class DatasetKey
{
    public static string FromObject(DatasetSpec spec, object row)
    {
        var values = new string[spec.KeyProperties.Count];
        for (var i = 0; i < spec.KeyProperties.Count; i++)
        {
            string propertyName = spec.KeyProperties[i];
            PropertyInfo property = row.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                ?? throw new InvalidDataException(
                    $"{spec.Name} model is missing configured key property '{propertyName}'.");
            values[i] = Normalize(property.GetValue(row));
        }
        if (values.All(value => value.Length == 0))
            throw new InvalidDataException($"{spec.Name} returned a completely blank composite key.");
        return string.Join('\u001F', values);
    }

    public static string FromCsv(DatasetSpec spec, CsvReader csv)
    {
        string[] values = spec.KeyProperties
            .Select(property => Normalize(csv.GetField(property)))
            .ToArray();
        if (values.All(value => value.Length == 0))
            throw new InvalidDataException($"{spec.Name} existing CSV has a completely blank composite key.");
        return string.Join('\u001F', values);
    }

    private static string Normalize(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
}

internal sealed record DatasetCheckpoint(
    string Dataset,
    DateTimeOffset CheckpointUtc,
    string SourceMode,
    string RunId);

internal static class CheckpointStore
{
    public static DatasetCheckpoint Read(string outputFolder, string dataset)
    {
        string path = Path.Combine(outputFolder, "_GLDataMillPull_checkpoints", dataset + ".json");
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"{dataset} has no durable checkpoint at '{path}'. Run full before incremental.");
        return JsonSerializer.Deserialize<DatasetCheckpoint>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"{dataset} checkpoint is invalid.");
    }

    public static void PublishAll(
        string outputFolder,
        IReadOnlyList<DatasetSpec> datasets,
        DateTimeOffset checkpointUtc,
        string sourceMode,
        string runId)
    {
        string folder = Path.Combine(outputFolder, "_GLDataMillPull_checkpoints");
        Directory.CreateDirectory(folder);
        foreach (DatasetSpec spec in datasets)
        {
            string destination = Path.Combine(folder, spec.Name + ".json");
            string temporary = Path.Combine(folder, $".{spec.Name}.{Guid.NewGuid():N}.tmp");
            var checkpoint = new DatasetCheckpoint(spec.Name, checkpointUtc, sourceMode, runId);
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.Move(temporary, destination, overwrite: true);
        }
    }
}

internal static class ManifestPublisher
{
    public static void Publish(string outputFolder, object manifest, string runId)
    {
        string destination = Path.Combine(outputFolder, "GL_DataMill_manifest.json");
        string temporary = Path.Combine(outputFolder, $".GL_DataMill_manifest.{Guid.NewGuid():N}.tmp");
        string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporary, json, new UTF8Encoding(false));

        if (File.Exists(destination))
        {
            string backupFolder = Path.Combine(outputFolder, "_GLDataMillPull_backups");
            Directory.CreateDirectory(backupFolder);
            File.Copy(
                destination,
                Path.Combine(backupFolder, $"GL_DataMill_manifest.{runId}.before_full.json"),
                overwrite: false);
        }

        File.Move(temporary, destination, overwrite: true);
    }
}

internal static class OutputPaths
{
    public static string ResolveFolder(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new CliException("--output-folder cannot be blank.");
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
    }
}

internal sealed class RunLock : IDisposable
{
    private readonly FileStream stream;

    private RunLock(FileStream stream) => this.stream = stream;

    public static RunLock Acquire(string outputFolder)
    {
        string path = Path.Combine(outputFolder, "GLDataMillPull.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
            writer.WriteLine($"PID={Environment.ProcessId}");
            writer.WriteLine($"Started={DateTimeOffset.Now:O}");
            writer.Flush();
            stream.Flush(true);
            return new RunLock(stream);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Another GLDataMillPull process appears to be using '{path}'.", ex);
        }
    }

    public void Dispose() => stream.Dispose();
}
