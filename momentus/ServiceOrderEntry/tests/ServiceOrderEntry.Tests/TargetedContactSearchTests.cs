using System.Net;
using System.Text;
using Newtonsoft.Json;
using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class TargetedContactSearchTests
{
    private static AllAccountsModel Contact(string email = "test@example.invalid") => new()
        { Class = "P", AccountCode = "EXISTING", PrimaryAccount = "OTHER", Email = email };
    private static HttpResponseMessage Page(params AllAccountsModel[] rows)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonConvert.SerializeObject(rows), Encoding.UTF8, "application/json") };
        response.Headers.Add("X-SearchMetadata", JsonConvert.SerializeObject(new
            { Page = 1, Page_Size = 1000, PageTotal = 1, ResultsTotal = rows.Length, Links = new { Next = (string?)null } }));
        return response;
    }

    [Fact]
    public void PositiveEmailEvidenceSkipsTheGlobalIndexWithoutAuthorizingWrites()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var filters = new List<string>();
        s.Transport.SearchOverride = request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            filters.Add(query);
            Assert.Contains("Class eq 'P' and Email eq 'test@example.invalid'", query);
            return Page(Contact());
        };
        Assert.True(s.Gateway.HasGlobalContactEmail(" Test@Example.Invalid "));
        Assert.Single(filters);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void EmptyExactSearchStillFindsCaseAndSpaceVariantsInCoveredFragmentResults()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var filtered = 0;
        var global = 0;
        s.Transport.SearchOverride = request =>
        {
            if (Uri.UnescapeDataString(request.RequestUri!.Query).Contains("Email eq")) { filtered++; return Page(); }
            global++; return Page(Contact(" Test@Example.Invalid "));
        };
        Assert.True(s.Gateway.HasGlobalContactEmail("test@example.invalid"));
        Assert.Equal(1, filtered); Assert.Equal(1, global);
    }

    [Fact]
    public void EmptyExactQueryAndFailedFullIndexNeverProveAbsence()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.SearchOverride = request => Uri.UnescapeDataString(request.RequestUri!.Query).Contains("Email eq")
            ? Page() : new(HttpStatusCode.BadRequest) { Content = new StringContent("{}") };
        Assert.Throws<DecisionSearchException>(() => s.Gateway.HasGlobalContactEmail("test@example.invalid"));
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void UnsupportedTargetedFilterFallsBackToACompleteSearch()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var requests = 0;
        s.Transport.SearchOverride = request =>
        {
            requests++;
            return Uri.UnescapeDataString(request.RequestUri!.Query).Contains("Email eq")
                ? new(HttpStatusCode.BadRequest) { Content = new StringContent("{}") } : Page(Contact());
        };
        Assert.True(s.Gateway.HasGlobalContactEmail("test@example.invalid"));
        Assert.Equal(2, requests);
    }

    [Fact]
    public void QueryReturnedUnrelatedRecordsDoNotCountAsEmailEvidence()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var requests = 0;
        s.Transport.SearchOverride = _ => { requests++; return requests == 1 ? Page(Contact("other@example.invalid")) : Page(); };
        Assert.False(s.Gateway.HasGlobalContactEmail("test@example.invalid"));
        Assert.Equal(2, requests);
    }

    [Fact]
    public void EmailWithAnApostropheIsEscapedInTheFilter()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.SearchOverride = request =>
        {
            Assert.Contains("Email eq 'o''neil@example.invalid'", Uri.UnescapeDataString(request.RequestUri!.Query));
            return Page(Contact("o'neil@example.invalid"));
        };
        Assert.True(s.Gateway.HasGlobalContactEmail("o'neil@example.invalid"));
    }

    [Fact]
    public void CompleteCoveredEmptyFragmentResultsProveAbsenceWithoutReadingTheFullIndex()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var filters = new List<string>();
        s.Transport.SearchOverride = request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            filters.Add(query);
            Assert.True(query.Contains("Email eq") || query.Contains("substringof"), query);
            return Page();
        };
        Assert.False(s.Gateway.HasGlobalContactEmail("js@livprocess.com"));
        Assert.Equal(2, filters.Count);
        Assert.Contains("substringof", filters[1]);
        Assert.Empty(s.Transport.Mutations);
    }
}
