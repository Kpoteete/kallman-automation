using System.Net;
using System.Text;
using Newtonsoft.Json;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class ProjectedAccountSearchTests
{
    [Fact]
    public void OrganizationProjectionKeepsMatchingFieldsAndSelectionOnEveryPage()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var requests = 0;
        s.Transport.SearchOverride = request =>
        {
            requests++;
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Assert.Contains("select=", query, StringComparison.OrdinalIgnoreCase);
            foreach (var field in MomentusGateway.AccountMatchFields) Assert.Contains(field, query);
            Assert.DoesNotContain("AccountUserFieldSets", query);
            var later = query.Contains("page=2", StringComparison.Ordinal);
            var rows = new[] { new { Organization = "10", AccountCode = later ? "MATCH" : "OTHER", Class = "O",
                Name = later ? "Acme LLC" : "Acme Components", Address1 = "1 Test Street", City = "Test City",
                State = "MT", PostalCode = "12345", Country = "***", EventSalesStatus = "A" } };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonConvert.SerializeObject(rows), Encoding.UTF8, "application/json") };
            response.Headers.Add("X-SearchMetadata", JsonConvert.SerializeObject(new { Page = later ? 2 : 1, Page_Size = 1,
                PageTotal = 2, ResultsTotal = 2, Links = new { Next = later ? null : "?page=2&search=projected" } }));
            return response;
        };
        var match = Assert.Single(s.Gateway.FindOrganizationCandidates(s.Row.RequestedBilling with { CompanyName = "Acme, Inc." }));
        Assert.Equal(2, requests);
        Assert.Equal("MATCH", match.AccountCode); Assert.Equal("Acme LLC", match.Company);
        Assert.Equal("O", match.AccountClass); Assert.Equal("1 Test Street", match.Address);
        Assert.Equal("Test City", match.City); Assert.Equal("MT", match.State);
        Assert.Equal("12345", match.PostalCode); Assert.Equal("***", match.Country); Assert.Equal("A", match.EventSalesStatus);
    }

    [Fact]
    public void PageSizeKeepsExistingDefaultAndAnExplicitOverrideStillWins()
    {
        Assert.Equal(1000, CliOptions.Parse(["preview"]).PageSize);
        Assert.Equal(5000, CliOptions.Parse(["preview", "--page-size", "5000"]).PageSize);
    }
}
