using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Options;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace ServiceOrderBoothUpdater;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(CliOptions.HelpText);
                return 0;
            }

            using var runLock = RunLock.Acquire(options.StateFolder);
            var credentials = MomentusCredentials.FromEnvironment();
            var client = MomentusClient.Create(credentials, options);
            var runner = new BoothUpdateRunner(client, options);
            return runner.Run();
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine(CliOptions.HelpText);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }
}

internal sealed record CliOptions(
    bool Apply,
    bool ConfirmUpdate,
    bool ShowHelp,
    string OrganizationCode,
    string BaseUrl,
    string StateFolder,
    int InitialLookbackHours,
    int OverlapMinutes,
    int PageSize,
    int MaxResults,
    int RequestDelayMs,
    int? ExhibitorId,
    int? EventId)
{
    public const string HelpText = """
ServiceOrderBoothUpdater

Usage:
  ServiceOrderBoothUpdater.exe preview [options]
  ServiceOrderBoothUpdater.exe apply --confirm-update-booth-number [options]

The default mode is preview. Apply mode writes BoothNumber only when the current
service-order value is blank. Existing different values are reported as conflicts.

Options:
  --org CODE                 Momentus organization (default: 10).
  --base-url URL             Default: https://kallman.ungerboeck.com/prod.
  --state-folder PATH        Logs/checkpoint folder (default: ./state).
  --initial-lookback-hours N First apply/each preview lookback (default: 336 / 14 days).
  --overlap-minutes N        Checkpoint overlap (default: 120).
  --page-size N              API page size (default: 1000).
  --max-results N            Safety ceiling per API search (default: 100000).
  --request-delay-ms N       Delay after each API request (default: 100).
  --exhibitor ID             Reconcile one exhibitor (requires --event).
  --event ID                 Reconcile one event (requires --exhibitor).
  --help                     Show help.

Credentials are read only from MOMENTUS_APIUSER, MOMENTUS_SECRET, and MOMENTUS_KEY.
""";

    public static CliOptions Parse(string[] args)
    {
        var result = new CliOptions(
            Apply: false,
            ConfirmUpdate: false,
            ShowHelp: false,
            OrganizationCode: "10",
            BaseUrl: "https://kallman.ungerboeck.com/prod",
            StateFolder: Path.Combine(AppContext.BaseDirectory, "state"),
            InitialLookbackHours: 336,
            OverlapMinutes: 120,
            PageSize: 1000,
            MaxResults: 100_000,
            RequestDelayMs: 100,
            ExhibitorId: null,
            EventId: null);

        var i = 0;
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            result = args[0].ToLowerInvariant() switch
            {
                "preview" => result,
                "apply" => result with { Apply = true },
                _ => throw new CliException($"Unknown mode '{args[0]}'.")
            };
            i++;
        }

        for (; i < args.Length; i++)
        {
            string NextValue()
            {
                if (++i >= args.Length)
                    throw new CliException($"Missing value after {args[i - 1]}.");
                return args[i];
            }

            result = args[i].ToLowerInvariant() switch
            {
                "--confirm-update-booth-number" => result with { ConfirmUpdate = true },
                "--org" => result with { OrganizationCode = NextValue().Trim() },
                "--base-url" => result with { BaseUrl = NextValue().Trim().TrimEnd('/') },
                "--state-folder" => result with { StateFolder = Path.GetFullPath(NextValue()) },
                "--initial-lookback-hours" => result with { InitialLookbackHours = ParseInt(NextValue(), 1, 24 * 365) },
                "--overlap-minutes" => result with { OverlapMinutes = ParseInt(NextValue(), 0, 24 * 60) },
                "--page-size" => result with { PageSize = ParseInt(NextValue(), 1, 10_000) },
                "--max-results" => result with { MaxResults = ParseInt(NextValue(), 1, 1_000_000) },
                "--request-delay-ms" => result with { RequestDelayMs = ParseInt(NextValue(), 0, 60_000) },
                "--exhibitor" => result with { ExhibitorId = ParseInt(NextValue(), 1, int.MaxValue) },
                "--event" => result with { EventId = ParseInt(NextValue(), 1, int.MaxValue) },
                "--help" or "-h" or "/?" => result with { ShowHelp = true },
                _ => throw new CliException($"Unknown option '{args[i]}'.")
            };
        }

        if (result.Apply && !result.ConfirmUpdate)
            throw new CliException("Apply mode also requires --confirm-update-booth-number.");
        if (!result.Apply && result.ConfirmUpdate)
            throw new CliException("--confirm-update-booth-number is valid only with apply mode.");
        if (string.IsNullOrWhiteSpace(result.OrganizationCode))
            throw new CliException("Organization code cannot be blank.");
        if (!Uri.TryCreate(result.BaseUrl, UriKind.Absolute, out _))
            throw new CliException("Base URL must be an absolute URL.");
        if (result.ExhibitorId.HasValue != result.EventId.HasValue)
            throw new CliException("--exhibitor and --event must be supplied together.");

        return result;
    }

    private static int ParseInt(string raw, int min, int max) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new CliException($"Expected a whole number from {min} through {max}, but got '{raw}'.");
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
        if (string.IsNullOrWhiteSpace(credentials.ApiUserId)) missing.Add("MOMENTUS_APIUSER");
        if (string.IsNullOrWhiteSpace(credentials.Secret)) missing.Add("MOMENTUS_SECRET");
        if (string.IsNullOrWhiteSpace(credentials.Key)) missing.Add("MOMENTUS_KEY");
        if (missing.Count > 0)
            throw new InvalidOperationException($"Missing environment variables: {string.Join(", ", missing)}.");
        return credentials;
    }
}

internal static class MomentusClient
{
    public static ApiClient Create(MomentusCredentials credentials, CliOptions options) =>
        new(new Jwt
        {
            UngerboeckURI = options.BaseUrl,
            APIUserID = credentials.ApiUserId,
            Secret = credentials.Secret,
            Key = credentials.Key,
            AutoRefresh = new AutoRefresh()
        });
}

internal readonly record struct ExhibitorEventKey(int ExhibitorId, int EventId);

internal sealed record BoothCandidate(
    string OrganizationCode,
    string Account,
    int ActivitySequence,
    int ExhibitorId,
    int EventId,
    string BoothNumber,
    DateTime ActivityTime,
    string ActivityText);

internal enum DecisionKind { WouldUpdate, Updated, AlreadyMatches, Conflict, Skipped, Error }

internal sealed record AuditRow(
    DateTime RunStarted,
    string Mode,
    DecisionKind Decision,
    int OrderNumber,
    int ExhibitorId,
    int EventId,
    string Account,
    string OrderStatus,
    string ExistingBooth,
    string ProposedBooth,
    int ActivitySequence,
    DateTime ActivityTime,
    string Message);

internal sealed class BoothUpdateRunner(ApiClient client, CliOptions options)
{
    private readonly DateTime runStarted = DateTime.Now;
    private readonly List<AuditRow> audit = [];
    private int requestCount;

    public int Run()
    {
        Directory.CreateDirectory(options.StateFolder);
        var since = GetSince();
        Console.WriteLine(options.Apply
            ? "LIVE MODE: blank service-order BoothNumber values can be updated."
            : "PREVIEW MODE: no Momentus record will be changed.");
        Console.WriteLine($"Scanning changes from {since:yyyy-MM-dd HH:mm:ss} through {runStarted:yyyy-MM-dd HH:mm:ss}.");

        try
        {
            List<ExhibitorEventKey> keys;
            if (options.ExhibitorId.HasValue && options.EventId.HasValue)
            {
                keys = [new ExhibitorEventKey(options.ExhibitorId.Value, options.EventId.Value)];
                Console.WriteLine($"Scoped reconciliation: exhibitor {options.ExhibitorId}, event {options.EventId}.");
            }
            else
            {
                var recentActivities = SearchRecentBoothActivities(since, runStarted);
                var recentOrders = SearchRecentOrders(since, runStarted);
                Console.WriteLine($"Found {recentActivities.Count:N0} recent BP activities and {recentOrders.Count:N0} recent active/pending orders.");
                keys = recentActivities
                    .Select(ActivityKey)
                    .Concat(recentOrders.Select(OrderKey))
                    .Where(k => k.ExhibitorId > 0 && k.EventId > 0)
                    .Distinct()
                    .OrderBy(k => k.EventId)
                    .ThenBy(k => k.ExhibitorId)
                    .ToList();
            }

            Console.WriteLine($"Reconciling {keys.Count:N0} changed exhibitor/event combinations.");
            foreach (var key in keys)
                Reconcile(key);

            var logPath = WriteAudit();
            var errors = audit.Count(x => x.Decision == DecisionKind.Error);
            Console.WriteLine($"API requests: {requestCount:N0}");
            Console.WriteLine($"Would update: {audit.Count(x => x.Decision == DecisionKind.WouldUpdate):N0}");
            Console.WriteLine($"Updated: {audit.Count(x => x.Decision == DecisionKind.Updated):N0}");
            Console.WriteLine($"Already matched: {audit.Count(x => x.Decision == DecisionKind.AlreadyMatches):N0}");
            Console.WriteLine($"Conflicts skipped: {audit.Count(x => x.Decision == DecisionKind.Conflict):N0}");
            Console.WriteLine($"Errors: {errors:N0}");
            Console.WriteLine($"Audit log: {logPath}");

            if (options.Apply && errors == 0)
                File.WriteAllText(CheckpointPath(), runStarted.ToString("O", CultureInfo.InvariantCulture));
            return errors == 0 ? 0 : 1;
        }
        catch
        {
            WriteAudit();
            throw;
        }
    }

    private void Reconcile(ExhibitorEventKey key)
    {
        try
        {
            var activities = SearchActivitiesForKey(key)
                .Select(BoothTextParser.TryCreateCandidate)
                .Where(c => c is not null)
                .Cast<BoothCandidate>()
                .OrderByDescending(c => c.ActivityTime)
                .ThenByDescending(c => c.ActivitySequence)
                .ToList();
            var orders = SearchOrdersForKey(key);

            if (orders.Count == 0 || activities.Count == 0)
                return;

            var latest = activities[0];
            var conflictingLatest = activities
                .Where(x => x.ActivityTime == latest.ActivityTime)
                .Select(x => BoothTextParser.Normalize(x.BoothNumber))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1;

            foreach (var order in orders)
            {
                if (conflictingLatest)
                {
                    AddAudit(DecisionKind.Conflict, order, latest, "Multiple different booth values share the latest activity time.");
                    continue;
                }
                ProcessOrder(order, latest);
            }
        }
        catch (Exception ex)
        {
            audit.Add(new AuditRow(runStarted, Mode(), DecisionKind.Error, 0, key.ExhibitorId, key.EventId, "", "", "", "", 0, DateTime.MinValue, ex.Message));
        }
    }

    private void ProcessOrder(ServiceOrdersModel order, BoothCandidate candidate)
    {
        if (!OrderRules.IsEligible(order, candidate))
        {
            AddAudit(DecisionKind.Skipped, order, candidate, "Order is not eligible for this activity.");
            return;
        }

        var existing = order.BoothNumber?.Trim() ?? "";
        if (BoothTextParser.Equivalent(existing, candidate.BoothNumber))
        {
            AddAudit(DecisionKind.AlreadyMatches, order, candidate, "Order already has the accepted booth.");
            return;
        }
        if (!string.IsNullOrWhiteSpace(existing))
        {
            AddAudit(DecisionKind.Conflict, order, candidate, "Existing BoothNumber differs; no overwrite was attempted.");
            return;
        }
        if (!options.Apply)
        {
            AddAudit(DecisionKind.WouldUpdate, order, candidate, "Preview only; no write attempted.");
            return;
        }

        try
        {
            var current = client.Endpoints.ServiceOrders.Get(options.OrganizationCode, IntValue(order.OrderNumber));
            AfterRequest();
            if (!OrderRules.IsEligible(current, candidate))
            {
                AddAudit(DecisionKind.Skipped, current, candidate, "Order changed after search and is no longer eligible.");
                return;
            }
            if (!string.IsNullOrWhiteSpace(current.BoothNumber))
            {
                var kind = BoothTextParser.Equivalent(current.BoothNumber, candidate.BoothNumber)
                    ? DecisionKind.AlreadyMatches
                    : DecisionKind.Conflict;
                AddAudit(kind, current, candidate, "BoothNumber was populated after search; no overwrite was attempted.");
                return;
            }

            current.BoothNumber = candidate.BoothNumber;
            var updated = client.Endpoints.ServiceOrders.Update(current);
            AfterRequest();
            AddAudit(DecisionKind.Updated, updated, candidate, "Blank BoothNumber updated from latest accepted BP activity.");
        }
        catch (Exception ex)
        {
            AddAudit(DecisionKind.Error, order, candidate, ex.Message);
        }
    }

    private List<ActivitiesModel> SearchRecentBoothActivities(DateTime start, DateTime end)
    {
        var rows = new Dictionary<string, ActivitiesModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in new[] { nameof(ActivitiesModel.EnteredOn), nameof(ActivitiesModel.ChangedOn) })
        {
            var filter = $"Type eq 'BP' and {field} ge datetime'{DateText(start)}' and {field} lt datetime'{DateText(end)}'";
            foreach (var row in SearchActivities(filter, [field, nameof(ActivitiesModel.Account), nameof(ActivitiesModel.SequenceNumber)]))
                rows[$"{row.OrganizationCode}|{row.Account}|{row.SequenceNumber}"] = row;
        }
        return rows.Values.ToList();
    }

    private List<ServiceOrdersModel> SearchRecentOrders(DateTime start, DateTime end)
    {
        var filter = $"LastChangedDateTime ge datetime'{DateText(start)}' and LastChangedDateTime lt datetime'{DateText(end)}'";
        return SearchOrders(filter)
            .Where(o => OrderRules.ActiveStatuses.Contains(o.OrderStatus ?? "") && IntValue(o.Exhibitor) > 0)
            .ToList();
    }

    private List<ActivitiesModel> SearchActivitiesForKey(ExhibitorEventKey key)
    {
        var filter = $"Type eq 'BP' and ExhibitorID eq {key.ExhibitorId} and Event eq {key.EventId}";
        return SearchActivities(filter, [nameof(ActivitiesModel.EnteredOn), nameof(ActivitiesModel.SequenceNumber)]);
    }

    private List<ServiceOrdersModel> SearchOrdersForKey(ExhibitorEventKey key)
    {
        var filter = $"Exhibitor eq {key.ExhibitorId} and Event eq {key.EventId}";
        return SearchOrders(filter).Where(o => OrderRules.ActiveStatuses.Contains(o.OrderStatus ?? "")).ToList();
    }

    private List<ActivitiesModel> SearchActivities(string filter, string[] orderBy)
    {
        var search = SearchOptions(orderBy);
        var response = client.Endpoints.Activities.Search(options.OrganizationCode, filter, search);
        AfterRequest();
        var rows = response.Results?.ToList() ?? [];
        var next = response.SearchMetadata?.Links?.Next;
        while (!string.IsNullOrWhiteSpace(next))
        {
            response = client.Endpoints.Activities.NavigateSearchList(next, search);
            AfterRequest();
            rows.AddRange(response.Results ?? []);
            next = response.SearchMetadata?.Links?.Next;
            GuardCount(rows.Count, filter);
        }
        GuardCount(rows.Count, filter);
        return rows;
    }

    private List<ServiceOrdersModel> SearchOrders(string filter)
    {
        var search = SearchOptions([nameof(ServiceOrdersModel.OrderNumber)]);
        var response = client.Endpoints.ServiceOrders.Search(options.OrganizationCode, filter, search);
        AfterRequest();
        var rows = response.Results?.ToList() ?? [];
        var next = response.SearchMetadata?.Links?.Next;
        while (!string.IsNullOrWhiteSpace(next))
        {
            response = client.Endpoints.ServiceOrders.NavigateSearchList(next, search);
            AfterRequest();
            rows.AddRange(response.Results ?? []);
            next = response.SearchMetadata?.Links?.Next;
            GuardCount(rows.Count, filter);
        }
        GuardCount(rows.Count, filter);
        return rows;
    }

    private Search SearchOptions(string[] orderBy) => new()
    {
        PageSize = options.PageSize,
        MaxResults = options.MaxResults,
        OrderBy = orderBy.ToList()
    };

    private void GuardCount(int count, string filter)
    {
        if (count > options.MaxResults)
            throw new InvalidDataException($"Search exceeded the {options.MaxResults:N0}-row ceiling: {filter}");
    }

    private void AfterRequest()
    {
        requestCount++;
        if (options.RequestDelayMs > 0)
            Thread.Sleep(options.RequestDelayMs);
    }

    private DateTime GetSince()
    {
        if (options.Apply && File.Exists(CheckpointPath()))
        {
            var raw = File.ReadAllText(CheckpointPath()).Trim();
            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var checkpoint))
                throw new InvalidDataException($"Invalid checkpoint: {CheckpointPath()}");
            return checkpoint.AddMinutes(-options.OverlapMinutes);
        }
        return runStarted.AddHours(-options.InitialLookbackHours);
    }

    private string WriteAudit()
    {
        var logs = Path.Combine(options.StateFolder, "logs");
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, $"booth-update-{runStarted:yyyyMMdd-HHmmss}-{Mode().ToLowerInvariant()}.json");
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(path, JsonSerializer.Serialize(audit, jsonOptions));
        return path;
    }

    private string CheckpointPath() => Path.Combine(options.StateFolder, "last-successful-apply.txt");
    private string Mode() => options.Apply ? "Apply" : "Preview";
    private static string DateText(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    private static int IntValue(object? value) => value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static ExhibitorEventKey ActivityKey(ActivitiesModel row) => new(IntValue(row.ExhibitorID), IntValue(row.Event));
    private static ExhibitorEventKey OrderKey(ServiceOrdersModel row) => new(IntValue(row.Exhibitor), IntValue(row.Event));

    private void AddAudit(DecisionKind kind, ServiceOrdersModel order, BoothCandidate candidate, string message) =>
        audit.Add(new AuditRow(
            runStarted, Mode(), kind, IntValue(order.OrderNumber), candidate.ExhibitorId, candidate.EventId,
            order.Account ?? "", order.OrderStatus ?? "", order.BoothNumber ?? "", candidate.BoothNumber,
            candidate.ActivitySequence, candidate.ActivityTime, message));
}

internal static partial class BoothTextParser
{
    [GeneratedRegex(@"\baccepted\s+booth\s+(?<booth>.+?)\s*,\s*and\s+had\s+these\s+comments\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex AcceptedBoothPattern();

    public static BoothCandidate? TryCreateCandidate(ActivitiesModel activity)
    {
        if (!string.Equals(activity.Type, "BP", StringComparison.OrdinalIgnoreCase))
            return null;
        var text = activity.PlainText ?? "";
        var match = AcceptedBoothPattern().Match(text);
        if (!match.Success)
            return null;
        var booth = Normalize(match.Groups["booth"].Value);
        if (!IsSafeBooth(booth))
            return null;

        return new BoothCandidate(
            activity.OrganizationCode ?? "", activity.Account ?? "", IntValue(activity.SequenceNumber),
            IntValue(activity.ExhibitorID), IntValue(activity.Event), booth,
            DateValue(activity.EnteredOn), text);
    }

    public static string Normalize(string value)
    {
        var collapsed = Regex.Replace(value.Trim(), @"\s+", " ");
        return string.Join(",", collapsed.Split(',', StringSplitOptions.TrimEntries));
    }
    public static bool Equivalent(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeBooth(string value) =>
        value.Length is > 0 and <= 100 &&
        value.Split(',').All(part => part.Length > 0) &&
        value.All(c => char.IsLetterOrDigit(c) || c is '-' or '/' or '.' or ' ' or ',' or '&' or '+');

    private static int IntValue(object? value) => value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static DateTime DateValue(object? value) =>
        value is DateTime date ? date : Convert.ToDateTime(value, CultureInfo.InvariantCulture);
}

internal static class OrderRules
{
    public static readonly HashSet<string> ActiveStatuses = new(StringComparer.OrdinalIgnoreCase) { "A", "PC" };

    public static bool IsEligible(ServiceOrdersModel order, BoothCandidate candidate) =>
        ActiveStatuses.Contains(order.OrderStatus ?? "") &&
        IntValue(order.Exhibitor) == candidate.ExhibitorId &&
        IntValue(order.Event) == candidate.EventId &&
        AccountsCompatible(order.Account, candidate.Account);

    private static bool AccountsCompatible(string? orderAccount, string? activityAccount) =>
        string.IsNullOrWhiteSpace(orderAccount) || string.IsNullOrWhiteSpace(activityAccount) ||
        string.Equals(orderAccount.Trim(), activityAccount.Trim(), StringComparison.OrdinalIgnoreCase);

    private static int IntValue(object? value) => value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
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

    public static RunLock Acquire(string stateFolder)
    {
        Directory.CreateDirectory(stateFolder);
        var path = Path.Combine(stateFolder, "ServiceOrderBoothUpdater.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new RunLock(path, stream);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Another ServiceOrderBoothUpdater run appears to be active.", ex);
        }
    }

    public void Dispose()
    {
        stream.Dispose();
        try { File.Delete(path); } catch (IOException) { }
    }
}
