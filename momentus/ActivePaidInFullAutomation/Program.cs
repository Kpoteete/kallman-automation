using System.Globalization;
using System.Text.Json;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Options;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace ActivePaidInFullAutomation;

internal static class Program
{
    public static int Main(string[] args)
    {
        try { var o = CliOptions.Parse(args); if (o.Help) { Console.WriteLine(CliOptions.HelpText); return 0; } using var l = RunLock.Acquire(o.StateFolder); return new Runner(BuildClient(o), o).Run(); }
        catch (CliException ex) { Console.Error.WriteLine($"ERROR: {ex.Message}\n{CliOptions.HelpText}"); return 2; }
        catch (Exception ex) { Console.Error.WriteLine($"ERROR: {ex.Message}"); return 1; }
    }

    private static ApiClient BuildClient(CliOptions o)
    {
        string Get(string n) => Environment.GetEnvironmentVariable(n)?.Trim() ?? "";
        var user = Get("MOMENTUS_APIUSER"); var secret = Get("MOMENTUS_SECRET"); var key = Get("MOMENTUS_KEY");
        var missing = new[] { ("MOMENTUS_APIUSER", user), ("MOMENTUS_SECRET", secret), ("MOMENTUS_KEY", key) }.Where(x => x.Item2.Length == 0).Select(x => x.Item1).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"Missing environment variables: {string.Join(", ", missing)}.");
        return new ApiClient(new Jwt { UngerboeckURI = o.BaseUrl, APIUserID = user, Secret = secret, Key = key, AutoRefresh = new AutoRefresh() });
    }
}

internal sealed record CliOptions(bool Apply, bool Confirm, bool Help, string Org, string BaseUrl, string StateFolder, string ReportFolder, int PageSize, int MaxResults, int MaxUpdates, int EventLookbackDays, int DelayMs, int? ExhibitorId, int? EventId)
{
    public const string HelpText = """
ActivePaidInFullAutomation

Usage:
  ActivePaidInFullAutomation.exe preview [options]
  ActivePaidInFullAutomation.exe apply --confirm-active-paid-in-full [options]

Preview is the default. Apply changes only exhibitor status 2 to 22 after a
fresh read confirms an active order in the approved category list has NetDue <= 0.

Options:
  --org CODE              Momentus organization (default: 10)
  --base-url URL          Default: https://kallman.ungerboeck.com/prod
  --state-folder PATH     Audit/state folder (default: ./state)
  --report-folder PATH    Weekly Excel folder (default: ./reports)
  --exhibitor ID          Scope to one exhibitor (requires --event)
  --event ID              Scope to one event (requires --exhibitor)
  --page-size N           API page size (default: 1000)
  --max-results N         Safety ceiling (default: 100000)
  --max-updates N         Maximum live updates per run (default: 25; preview is uncapped)
  --event-lookback-days N Include events ended within N days or later (default: 31)
  --request-delay-ms N    Delay after API requests (default: 100)
""";
    public static CliOptions Parse(string[] a)
    {
        var o = new CliOptions(false, false, false, "10", "https://kallman.ungerboeck.com/prod", Path.Combine(AppContext.BaseDirectory, "state"), Path.Combine(AppContext.BaseDirectory, "reports"), 1000, 100000, 25, 31, 100, null, null);
        var i = 0; if (a.Length > 0 && !a[0].StartsWith("--")) { o = a[0].ToLowerInvariant() switch { "preview" => o, "apply" => o with { Apply = true }, _ => throw new CliException($"Unknown mode '{a[0]}'.") }; i++; }
        string Next() { if (++i >= a.Length) throw new CliException($"Missing value after {a[i - 1]}."); return a[i]; }
        for (; i < a.Length; i++) o = a[i].ToLowerInvariant() switch
        {
            "--confirm-active-paid-in-full" => o with { Confirm = true }, "--help" or "-h" or "/?" => o with { Help = true },
            "--org" => o with { Org = Next().Trim() }, "--base-url" => o with { BaseUrl = Next().Trim().TrimEnd('/') },
            "--state-folder" => o with { StateFolder = Path.GetFullPath(Next()) }, "--report-folder" => o with { ReportFolder = Path.GetFullPath(Next()) },
            "--page-size" => o with { PageSize = Int(Next(), 1, 10000) }, "--max-results" => o with { MaxResults = Int(Next(), 1, 1000000) },
            "--max-updates" => o with { MaxUpdates = Int(Next(), 1, 100000) },
            "--event-lookback-days" => o with { EventLookbackDays = Int(Next(), 0, 3650) },
            "--request-delay-ms" => o with { DelayMs = Int(Next(), 0, 60000) }, "--exhibitor" => o with { ExhibitorId = Int(Next(), 1, int.MaxValue) },
            "--event" => o with { EventId = Int(Next(), 1, int.MaxValue) }, _ => throw new CliException($"Unknown option '{a[i]}'.")
        };
        if (o.Apply && !o.Confirm) throw new CliException("Apply mode requires --confirm-active-paid-in-full.");
        if (!o.Apply && o.Confirm) throw new CliException("The confirmation switch is valid only in apply mode.");
        if (o.ExhibitorId.HasValue != o.EventId.HasValue) throw new CliException("--exhibitor and --event must be supplied together.");
        return o;
    }
    private static int Int(string s, int min, int max) => int.TryParse(s, out var n) && n >= min && n <= max ? n : throw new CliException($"Invalid number '{s}'.");
}
internal sealed class CliException(string message) : Exception(message);

internal sealed class Runner(ApiClient client, CliOptions o)
{
    private readonly DateTime started = DateTime.Now; private readonly List<object> audit = []; private readonly Dictionary<int, string> eventNames = []; private int requests;
    public int Run()
    {
        Directory.CreateDirectory(o.StateFolder); Directory.CreateDirectory(o.ReportFolder);
        Console.WriteLine(o.Apply ? "LIVE MODE: eligible exhibitor statuses can change from 2 to 22." : "PREVIEW MODE: no Momentus record will be changed.");
        var store = new WeeklyReportStore(o.ReportFolder, o.StateFolder);
        if (o.Apply) store.Rebuild(started); // Recovers a report if a prior run saved its durable row before workbook replacement failed.
        var includedEvents = SearchIncludedEvents(); Console.WriteLine($"Included recent/future events: {includedEvents.Count:N0}");
        foreach (var item in includedEvents) eventNames[item.Key] = item.Value;
        var orders = SearchOrders(includedEvents.Keys); Console.WriteLine($"Active orders in approved categories found: {orders.Count:N0}");
        var activeExhibitors = SearchActiveExhibitors(includedEvents.Keys);
        Console.WriteLine($"Active exhibitors loaded: {activeExhibitors.Count:N0}");
        var errors = 0; var updated = 0; var would = 0;
        var groups = orders.Where(x => Value(x.Exhibitor) > 0 && Value(x.Event) > 0)
            .GroupBy(x => (Exhibitor: Value(x.Exhibitor), Event: Value(x.Event)))
            .OrderBy(x => x.Key.Event).ThenBy(x => x.Key.Exhibitor);
        foreach (var group in groups)
        {
            if (o.Apply && updated >= o.MaxUpdates) { audit.Add(new { Result = "ApplyLimitReached", Limit = o.MaxUpdates }); break; }
            try
            {
                var exhibitorId = group.Key.Exhibitor;
                if (!activeExhibitors.TryGetValue(exhibitorId, out var exhibitor)) continue;
                var candidate = SelectCandidate(group, exhibitor);
                if (candidate.Order is null) continue;
                var eventName = GetEventName(group.Key.Event);
                if (!o.Apply) { would++; AddAudit("WouldUpdate", candidate.Order, exhibitor, candidate.Decision!.Reason); continue; }
                var currentExhibitor = client.Endpoints.Exhibitors.Get(o.Org, exhibitorId); AfterRequest();
                var currentOrders = SearchOrdersFor(group.Key.Exhibitor, group.Key.Event);
                var freshCandidate = SelectCandidate(currentOrders, currentExhibitor);
                if (freshCandidate.Order is null) { AddAudit("SkippedAfterRefresh", candidate.Order, currentExhibitor, "No approved active order still has ordered net due of zero or less."); continue; }
                currentExhibitor.ExhibitorStatus = QualificationRules.PaidInFullExhibitorStatus;
                client.Endpoints.Exhibitors.Update(currentExhibitor); AfterRequest();
                var verified = client.Endpoints.Exhibitors.Get(o.Org, exhibitorId); AfterRequest();
                if (verified.ExhibitorStatus != QualificationRules.PaidInFullExhibitorStatus) throw new InvalidOperationException("Post-update readback did not return exhibitor status 22.");
                updated++;
                store.Record(Row("Updated", QualificationRules.PaidInFullExhibitorStatus.ToString(CultureInfo.InvariantCulture), freshCandidate.Order, verified, eventName, freshCandidate.Decision!), rebuild: false);
                AddAudit("Updated", freshCandidate.Order, verified, "Status changed from 2 to 22 and verified by readback.");
            }
            catch (Exception ex) { errors++; audit.Add(new { Result = "Error", Message = ex.Message }); }
        }
        var auditPath = WriteAudit(); Console.WriteLine($"Would update: {would:N0}; Updated: {updated:N0}; Errors: {errors:N0}; API requests: {requests:N0}"); Console.WriteLine($"Audit: {auditPath}");
        if (o.Apply && updated > 0) Console.WriteLine($"Weekly report: {store.Rebuild(started)}");
        return errors == 0 ? 0 : 1;
    }
    private Dictionary<int, string> SearchIncludedEvents()
    {
        if (o.EventId.HasValue)
        {
            var item = client.Endpoints.Events.Get(o.Org, o.EventId.Value); AfterRequest();
            return new Dictionary<int, string> { [o.EventId.Value] = First(item.Description, $"Event {o.EventId.Value}") };
        }
        var cutoff = DateTime.Today.AddDays(-o.EventLookbackDays);
        var filter = $"EndDate ge datetime'{cutoff:yyyy-MM-ddTHH:mm:ss}'";
        var search = new Search { PageSize = o.PageSize, MaxResults = o.MaxResults, OrderBy = [nameof(EventsModel.EventID)] };
        var response = WithReadRetry(() => client.Endpoints.Events.Search(o.Org, filter, search), "recent/future event search");
        var rows = response.Results?.ToList() ?? []; var total = response.SearchMetadata?.ResultsTotal; var next = response.SearchMetadata?.Links?.Next;
        while (!string.IsNullOrWhiteSpace(next))
        {
            var pageUrl = next; response = WithReadRetry(() => client.Endpoints.Events.NavigateSearchList(pageUrl, search), "recent/future event page");
            rows.AddRange(response.Results ?? []); next = response.SearchMetadata?.Links?.Next;
            if (rows.Count > o.MaxResults) throw new InvalidOperationException($"Event search exceeded the {o.MaxResults:N0} safety ceiling.");
        }
        if (total.HasValue && total.Value != rows.Count) throw new InvalidDataException($"Momentus reported {total.Value:N0} included events but returned {rows.Count:N0}.");
        return rows.Where(x => x.EventID.HasValue).GroupBy(x => x.EventID!.Value).ToDictionary(x => x.Key, x => First(x.First().Description, $"Event {x.Key}"));
    }
    private List<ServiceOrdersModel> SearchOrders(IEnumerable<int> eventIds)
    {
        var rows = new Dictionary<int, ServiceOrdersModel>();
        foreach (var batch in Batches(eventIds, 20))
        {
            var scope = o.ExhibitorId.HasValue ? $"Exhibitor eq {o.ExhibitorId} and " : "";
            var filter = $"{scope}{EventFilter(batch)} and OrderStatus eq 'A' and OrderedTotal gt 0 and {QualificationRules.ODataCategoryFilter()}";
            foreach (var row in SearchOrdersPaged(filter)) rows[Value(row.OrderNumber)] = row;
        }
        return rows.Values.ToList();
    }
    private List<ServiceOrdersModel> SearchOrdersFor(int exhibitorId, int eventId)
    {
        var filter = $"Exhibitor eq {exhibitorId} and Event eq {eventId} and OrderStatus eq 'A' and OrderedTotal gt 0 and {QualificationRules.ODataCategoryFilter()}";
        return SearchOrdersPaged(filter);
    }
    private Dictionary<int, ExhibitorsModel> SearchActiveExhibitors(IEnumerable<int> eventIds)
    {
        if (o.ExhibitorId.HasValue)
        {
            var exhibitor = client.Endpoints.Exhibitors.Get(o.Org, o.ExhibitorId.Value); AfterRequest();
            return exhibitor.ExhibitorStatus == QualificationRules.ActiveExhibitorStatus &&
                   string.Equals(exhibitor.ExhibitorType?.Trim(), "ME", StringComparison.OrdinalIgnoreCase)
                ? new Dictionary<int, ExhibitorsModel> { [o.ExhibitorId.Value] = exhibitor }
                : [];
        }
        var rows = new List<ExhibitorsModel>();
        foreach (var batch in Batches(eventIds, 20)) rows.AddRange(SearchExhibitorsByStatus(QualificationRules.ActiveExhibitorStatus, batch));
        return rows.Where(x => x.ExhibitorID.HasValue).GroupBy(x => x.ExhibitorID!.Value).ToDictionary(x => x.Key, x => x.First());
    }
    private List<ExhibitorsModel> SearchExhibitorsByStatus(int status, IReadOnlyCollection<int> eventIds)
    {
        var search = new Search { PageSize = o.PageSize, MaxResults = o.MaxResults, OrderBy = [nameof(ExhibitorsModel.ExhibitorID)] };
        var filter = $"{EventFilter(eventIds)} and ExhibitorStatus eq {status} and ExhibitorType eq 'ME'";
        var response = WithReadRetry(() => client.Endpoints.Exhibitors.Search(o.Org, filter, search), $"exhibitor status {status} search");
        var rows = response.Results?.ToList() ?? []; var reportedTotal = response.SearchMetadata?.ResultsTotal; var next = response.SearchMetadata?.Links?.Next;
        while (!string.IsNullOrWhiteSpace(next))
        {
            var pageUrl = next;
            response = WithReadRetry(() => client.Endpoints.Exhibitors.NavigateSearchList(pageUrl, search), $"exhibitor status {status} page");
            rows.AddRange(response.Results ?? []); next = response.SearchMetadata?.Links?.Next;
            if (rows.Count > o.MaxResults) throw new InvalidOperationException($"Exhibitor status {status} search exceeded the {o.MaxResults:N0} safety ceiling.");
        }
        if (reportedTotal.HasValue && reportedTotal.Value != rows.Count) throw new InvalidDataException($"Momentus reported {reportedTotal.Value:N0} exhibitors in status {status} but returned {rows.Count:N0} across all pages.");
        return rows;
    }
    private static string EventFilter(IEnumerable<int> eventIds) => "(" + string.Join(" or ", eventIds.Select(x => $"Event eq {x}")) + ")";
    private static IEnumerable<List<int>> Batches(IEnumerable<int> values, int size)
    {
        var batch = new List<int>(size);
        foreach (var value in values.Distinct().Order()) { batch.Add(value); if (batch.Count == size) { yield return batch; batch = new List<int>(size); } }
        if (batch.Count > 0) yield return batch;
    }
    private List<ServiceOrdersModel> SearchOrdersPaged(string filter)
    {
        var search = new Search { PageSize = o.PageSize, MaxResults = o.MaxResults, OrderBy = [nameof(ServiceOrdersModel.OrderNumber)] };
        var response = WithReadRetry(() => client.Endpoints.ServiceOrders.Search(o.Org, filter, search), "service order search");
        var rows = response.Results?.ToList() ?? [];
        var reportedTotal = response.SearchMetadata?.ResultsTotal;
        var next = response.SearchMetadata?.Links?.Next;
        while (!string.IsNullOrWhiteSpace(next))
        {
            var pageUrl = next;
            response = WithReadRetry(() => client.Endpoints.ServiceOrders.NavigateSearchList(pageUrl, search), "service order page");
            rows.AddRange(response.Results ?? []); next = response.SearchMetadata?.Links?.Next;
            if (rows.Count > o.MaxResults) throw new InvalidOperationException($"Service order search exceeded the {o.MaxResults:N0} safety ceiling.");
        }
        if (reportedTotal.HasValue && reportedTotal.Value != rows.Count) throw new InvalidDataException($"Momentus reported {reportedTotal.Value:N0} service orders but returned {rows.Count:N0} across all pages.");
        return rows;
    }
    private QualificationDecision Evaluate(ServiceOrdersModel x, ExhibitorsModel e) => QualificationRules.Evaluate(e.ExhibitorStatus, e.ExhibitorType, x.OrderStatus, x.Category, Money(x.OrderedTotal), Money(x.NetDue));
    private static (ServiceOrdersModel? Order, QualificationDecision? Decision) SelectCandidate(IEnumerable<ServiceOrdersModel> orders, ExhibitorsModel exhibitor)
    {
        var list = orders.ToList();
        var selectedOrderNumber = OrderPriorityRules.SelectQualifyingOrder(list.Select(x => new OrderPriorityInput(Value(x.OrderNumber), x.OrderStatus, x.BoothOrder, x.Category, Money(x.OrderedTotal), Money(x.NetDue))));
        if (!selectedOrderNumber.HasValue) return (null, null);
        var order = list.First(x => Value(x.OrderNumber) == selectedOrderNumber.Value);
        return (order, QualificationRules.Evaluate(exhibitor.ExhibitorStatus, exhibitor.ExhibitorType, order.OrderStatus, order.Category, Money(order.OrderedTotal), Money(order.NetDue)));
    }
    private string GetEventName(int id)
    {
        if (eventNames.TryGetValue(id, out var cached)) return cached;
        try { var x = client.Endpoints.Events.Get(o.Org, id); AfterRequest(); return eventNames[id] = First(x.Description, $"Event {id}"); }
        catch { return eventNames[id] = $"Event {id}"; }
    }
    private ReportRow Row(string result, string newStatus, ServiceOrdersModel x, ExhibitorsModel e, string eventName, QualificationDecision d) => new(started, o.Apply ? "Apply" : "Preview", result, Value(x.Event), eventName, Value(e.ExhibitorID), First(e.CompanyBannerName, e.CompanyName, e.AccountCode), e.AccountCode ?? "", First(x.BoothNumber, e.BoothNumber), Value(x.OrderNumber), d.CategorySequence, d.CategoryName, Money(x.OrderedTotal), Money(x.Payments), Money(x.NetDue), QualificationRules.ActiveExhibitorStatus.ToString(CultureInfo.InvariantCulture), newStatus, d.Reason);
    private void AddAudit(string result, ServiceOrdersModel x, ExhibitorsModel e, string detail) => audit.Add(new { Time = DateTime.Now, Result = result, Event = x.Event, Exhibitor = e.ExhibitorID, Order = x.OrderNumber, Category = x.Category, CategoryName = QualificationRules.CategoryName(x.Category), OrderedTotal = x.OrderedTotal, Payments = x.Payments, NetDue = x.NetDue, Detail = detail });
    private string WriteAudit() { var dir = Path.Combine(o.StateFolder, "logs"); Directory.CreateDirectory(dir); var p = Path.Combine(dir, $"paid-in-full-{started:yyyyMMdd-HHmmss}-{(o.Apply ? "apply" : "preview")}.json"); File.WriteAllText(p, JsonSerializer.Serialize(new { Started = started, Mode = o.Apply ? "Apply" : "Preview", Rows = audit }, new JsonSerializerOptions { WriteIndented = true })); return p; }
    private T WithReadRetry<T>(Func<T> action, string description)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try { var result = action(); AfterRequest(); return result; }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex))
            {
                last = ex; var delay = TimeSpan.FromSeconds(1 << attempt);
                Console.Error.WriteLine($"Momentus {description} attempt {attempt}/3 timed out; retrying in {delay.TotalSeconds:N0}s.");
                Thread.Sleep(delay);
            }
        }
        throw new InvalidOperationException($"Momentus {description} failed after 3 attempts.", last);
    }
    private static bool IsTransient(Exception ex)
    {
        if (ex is TimeoutException or TaskCanceledException or HttpRequestException) return true;
        var text = ex.ToString();
        return text.Contains("timed out", StringComparison.OrdinalIgnoreCase) || text.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("HttpClient called failed", StringComparison.OrdinalIgnoreCase) || text.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("502", StringComparison.OrdinalIgnoreCase) || text.Contains("503", StringComparison.OrdinalIgnoreCase) || text.Contains("504", StringComparison.OrdinalIgnoreCase);
    }
    private void AfterRequest() { requests++; if (o.DelayMs > 0) Thread.Sleep(o.DelayMs); }
    private static int Value(int? x) => x ?? 0; private static decimal Money(decimal? x) => x ?? 0m;
    private static string First(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";
}

internal sealed class RunLock : IDisposable
{
    private readonly FileStream stream; private RunLock(FileStream stream) => this.stream = stream;
    public static RunLock Acquire(string folder) { Directory.CreateDirectory(folder); try { return new RunLock(new FileStream(Path.Combine(folder, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); } catch (IOException) { throw new InvalidOperationException("Another run is already active."); } }
    public void Dispose() => stream.Dispose();
}
