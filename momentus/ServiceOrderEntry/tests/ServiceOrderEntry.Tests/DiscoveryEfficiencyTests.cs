using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Ungerboeck.Api.Models.Subjects;
using Xunit;
using Xunit.Abstractions;

namespace ServiceOrderEntry.Tests;

public sealed class DiscoveryEfficiencyTests(ITestOutputHelper output)
{
    private static ExhibitorsModel Exhibitor(int id, int eventId = 1) => new()
    { OrganizationCode = "10", ExhibitorID = id, Event = eventId, ExhibitorStatus = 35, ExhibitorType = "ME" };
    private static ServiceOrdersModel Order(int number, int exhibitor, int eventId = 1) => new()
    { OrganizationCode = "10", OrderNumber = number, Exhibitor = exhibitor, Event = eventId, OrderStatus = "PC" };
    private static HttpResponseMessage Page(object[] rows, int page = 1, int? total = null, int size = 1000, string? next = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonConvert.SerializeObject(rows), Encoding.UTF8, "application/json") };
        response.Headers.Add("X-SearchMetadata", JsonConvert.SerializeObject(new
        { Page = page, Page_Size = size, PageTotal = Math.Max(1, (int)Math.Ceiling((double)(total ?? rows.Length) / size)),
            ResultsTotal = total ?? rows.Length, Links = new { Next = next } }));
        return response;
    }
    private static bool IsExhibitorSearch(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("/Exhibitors/10");
    private static string Filter(HttpRequestMessage request) => Uri.UnescapeDataString(request.RequestUri!.Query);
    private static HashSet<(int Exhibitor, int Event)> Scopes(HttpRequestMessage request) =>
        Regex.Matches(Filter(request), @"Exhibitor eq (\d+) and Event eq (\d+)")
            .Select(x => (int.Parse(x.Groups[1].Value), int.Parse(x.Groups[2].Value))).ToHashSet();

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(10, 11)]
    [InlineData(11, 12)]
    [InlineData(100, 101)]
    public void BulkDiscoveryUsesExactPairQueriesAndKeepsEveryMatchingOrder(int count, int expectedRequests)
    {
        using var s = new RetryBoundaryTests.Scenario();
        var exhibitors = Enumerable.Range(1, count).Select(x => Exhibitor(x, x % 3 + 1)).ToArray();
        var orders = exhibitors.SelectMany(x => new[] { Order(x.ExhibitorID!.Value * 10, x.ExhibitorID.Value, x.Event!.Value),
            Order(x.ExhibitorID.Value * 10 + 1, x.ExhibitorID.Value, x.Event.Value) }).Reverse().ToArray();
        var queried = new List<(int, int)>();
        s.Transport.SearchOverride = request =>
        {
            if (IsExhibitorSearch(request)) return Page(exhibitors.Cast<object>().ToArray());
            Assert.Contains("OrderStatus eq 'PC' and Exhibitor eq ", Filter(request));
            Assert.DoesNotContain(" or ", Filter(request));
            var scopes = Scopes(request);
            Assert.Single(scopes);
            queried.AddRange(scopes);
            return Page(orders.Where(x => scopes.Contains((x.Exhibitor!.Value, x.Event!.Value))).Cast<object>().ToArray());
        };
        var candidates = s.Gateway.FindCandidates();
        Assert.Equal(orders.OrderBy(x => x.Event).ThenBy(x => x.OrderNumber).Select(x => x.OrderNumber), candidates.Select(x => x.Order.OrderNumber));
        Assert.All(candidates, x => { Assert.Equal(x.Order.Exhibitor, x.Exhibitor.ExhibitorID); Assert.Equal(x.Order.Event, x.Exhibitor.Event); });
        Assert.Equal(count, queried.Distinct().Count());
        Assert.Equal(count, queried.Count);
        Assert.Equal(expectedRequests, s.Gateway.RequestCount);
        output.WriteLine($"{count} eligible exhibitors: {s.Gateway.RequestCount} exact-pair discovery requests; {candidates.Count} orders.");
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void TenantReturningEmptyForGroupedQueriesCannotHideFourPendingOrders()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var exhibitors = new[] { Exhibitor(175397, 6193), Exhibitor(175410, 6193),
            Exhibitor(183021, 6220), Exhibitor(195739, 6193) };
        var orders = new[] { Order(40140, 175397, 6193), Order(39604, 175410, 6193),
            Order(40460, 183021, 6220), Order(40025, 195739, 6193) };
        s.Transport.SearchOverride = request =>
        {
            if (IsExhibitorSearch(request)) return Page(exhibitors.Cast<object>().ToArray());
            // The real tenant accepts the grouped filter and reports complete zero results.
            if (Filter(request).Contains(" or ", StringComparison.Ordinal)) return Page([]);
            var scopes = Scopes(request);
            return Page(orders.Where(x => scopes.Contains((x.Exhibitor!.Value, x.Event!.Value))).Cast<object>().ToArray());
        };
        Assert.Equal(new int?[] { 39604, 40025, 40140, 40460 },
            s.Gateway.FindCandidates().Select(x => x.Order.OrderNumber));
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void ExhibitorsWithoutPendingOrdersAreStillCoveredAndHoldIsNeverQueried()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var held = Exhibitor(9); held.ExhibitorCategory = "103";
        s.Transport.SearchOverride = request =>
        {
            if (IsExhibitorSearch(request)) return Page([Exhibitor(2), Exhibitor(3, 8), held]);
            Assert.Single(Scopes(request));
            Assert.DoesNotContain((9, 1), Scopes(request));
            return Page(Scopes(request).Contains((3, 8)) ? [Order(50, 3, 8)] : []);
        };
        Assert.Equal(50, Assert.Single(s.Gateway.FindCandidates()).Order.OrderNumber);
        Assert.Equal(3, s.Gateway.RequestCount);
    }

    [Fact]
    public void LaterOrderPagesRetainTheOriginalPairAndAllCandidates()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.SearchOverride = request =>
        {
            if (IsExhibitorSearch(request)) return Page([Exhibitor(2)]);
            var later = request.RequestUri!.Query.Contains("page=2");
            if (!later) Assert.Single(Scopes(request));
            return Page([later ? Order(4, 2) : Order(3, 2)], later ? 2 : 1, 2, 1,
                later ? null : "https://offline.invalid/prod/api/v1/ServiceOrders/10?page=2&search=original-batch");
        };
        Assert.Equal(new int?[] { 3, 4 }, s.Gateway.FindCandidates().Select(x => x.Order.OrderNumber));
        Assert.Equal(3, s.Gateway.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureAfterCollectedCandidatesNeverReturnsPartialDiscovery(bool laterPage)
    {
        using var s = new RetryBoundaryTests.Scenario();
        var batch = 0;
        s.Transport.SearchOverride = request =>
        {
            if (IsExhibitorSearch(request)) return Page(Enumerable.Range(1, 26).Select(x => (object)Exhibitor(x)).ToArray());
            if (request.RequestUri!.Query.Contains("page=2") || ++batch == 2)
                return new(HttpStatusCode.BadRequest) { Content = new StringContent("{}") };
            return Page([Order(3, 1)], total: laterPage ? 2 : 1, size: 1,
                next: laterPage ? "https://offline.invalid/prod/api/v1/ServiceOrders/10?page=2&search=original-batch" : null);
        };
        var error = Assert.Throws<DecisionSearchException>(() => s.Gateway.FindCandidates());
        Assert.Equal(laterPage ? SearchFailureKind.IncompletePagination : SearchFailureKind.RequestFailed, error.Kind);
        Assert.Empty(s.Transport.Mutations);
        var audit = Assert.Single(s.Store.Load());
        Assert.Empty(audit.Stages);
        Assert.Single(audit.SearchFailures);
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("event")]
    [InlineData("exhibitor")]
    [InlineData("status")]
    [InlineData("number")]
    public void OrderOutsideTheExactPairScopeBlocksDiscovery(string defect)
    {
        using var s = new RetryBoundaryTests.Scenario();
        var order = Order(3, 2);
        switch (defect)
        {
            case "organization": order.OrganizationCode = "20"; break;
            case "event": order.Event = 7; break;
            case "exhibitor": order.Exhibitor = 9; break;
            case "status": order.OrderStatus = "A"; break;
            case "number": order.OrderNumber = 0; break;
        }
        s.Transport.SearchOverride = request => IsExhibitorSearch(request) ? Page([Exhibitor(2), Exhibitor(9, 7)]) : Page([order]);
        Assert.Equal(SearchFailureKind.MalformedResponse, Assert.Throws<DecisionSearchException>(() => s.Gateway.FindCandidates()).Kind);
        Assert.Empty(s.Transport.Mutations);
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("event")]
    [InlineData("exhibitor")]
    [InlineData("status")]
    [InlineData("type")]
    public void InvalidEligibilityResponseCannotStartOrderSearches(string defect)
    {
        using var s = new RetryBoundaryTests.Scenario();
        var exhibitor = Exhibitor(2);
        switch (defect)
        {
            case "organization": exhibitor.OrganizationCode = "20"; break;
            case "event": exhibitor.Event = 0; break;
            case "exhibitor": exhibitor.ExhibitorID = 0; break;
            case "status": exhibitor.ExhibitorStatus = 2; break;
            case "type": exhibitor.ExhibitorType = "CO"; break;
        }
        s.Transport.SearchOverride = _ => Page([exhibitor]);
        Assert.Equal(SearchFailureKind.MalformedResponse, Assert.Throws<DecisionSearchException>(() => s.Gateway.FindCandidates()).Kind);
        Assert.Single(s.Transport.Requests);
    }
}
