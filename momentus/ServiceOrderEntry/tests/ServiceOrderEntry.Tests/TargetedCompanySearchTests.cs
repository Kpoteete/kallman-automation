using System.Net;
using System.Text;
using Newtonsoft.Json;
using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class TargetedCompanySearchTests
{
    private static AllAccountsModel Account(string code, string name) => new() { AccountCode = code, Class = "O", Name = name };
    private static HttpResponseMessage Page(AllAccountsModel[] rows, int page = 1, int? total = null, string? next = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(rows), Encoding.UTF8, "application/json") };
        response.Headers.Add("X-SearchMetadata", JsonConvert.SerializeObject(new { Page = page, Page_Size = next is not null || page > 1 ? 1 : 1000,
            PageTotal = next is not null || page > 1 ? 2 : 1, ResultsTotal = total ?? rows.Length, Links = new { Next = next } }));
        return response;
    }
    private static bool Targeted(HttpRequestMessage request) => Uri.UnescapeDataString(request.RequestUri!.Query).Contains("substringof(");

    [Theory]
    [InlineData("Acme, Inc.", "aCmE LLC")]
    [InlineData("49North Inc.", "49NORTH Limited")]
    [InlineData("Alpha / Beta Corporation", "ALPHA—BETA LLP")]
    [InlineData("Café Aerospace Inc", "Cafe\u0301 AEROSPACE LLC")]
    [InlineData("Kallman Worldwide", "\u212Aallman Worldwide Ltd")]
    public void EveryAcceptedVariantContainsACoveredFragment(string requested, string existing)
    {
        Assert.True(TextRules.CompanyMatches(requested, existing));
        var query = Assert.IsType<CompanySearch>(CompanySearch.For(requested));
        Assert.True(query.Includes(existing));
        Assert.InRange(query.Fragments.Count, 1, 16);
    }

    [Fact]
    public void CoveredAsciiCharactersCannotBeCreatedByUnicodeNormalizationOrCasing()
    {
        foreach (var character in CompanySearch.CoveredCharacters)
            Assert.Equal(character.ToString(), character.ToString().Normalize(NormalizationForm.FormD));
        for (var value = 128; value <= 0x10FFFF; value++)
        {
            if (!Rune.IsValid(value)) continue;
            string normalized;
            try { normalized = new Rune(value).ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant(); }
            catch (ArgumentException) { continue; } // These values also cannot enter the existing company normalizer.
            Assert.False(normalized.Length == 1 && CompanySearch.CoveredCharacters.Contains(normalized[0]), $"Unexpected ASCII inverse U+{value:X}.");
        }
    }

    [Theory]
    [InlineData("\u4e2d\u6587\u516c\u53f8")]
    [InlineData("AB")]
    [InlineData("KKK LLC")]
    public void NamesWithoutACoveredFragmentUseTheFullIndex(string requested)
    {
        using var s = new RetryBoundaryTests.Scenario();
        Assert.Null(CompanySearch.For(requested));
        s.Transport.SearchOverride = request => { Assert.False(Targeted(request)); return Page([Account("MATCH", requested)]); };
        Assert.Single(s.Gateway.FindOrganizationCandidates(s.Row.RequestedBilling with { CompanyName = requested }));
        Assert.Equal(1, s.Gateway.RequestCount);
    }

    [Fact]
    public void TargetedPaginationRetainsLaterDuplicateCandidatesAndTheOriginalMatchingRules()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.SearchOverride = request =>
        {
            Assert.True(Targeted(request) || request.RequestUri!.Query.Contains("page=2"));
            return request.RequestUri!.Query.Contains("page=2") ? Page([Account("TWO", "aCmE LLC")], 2, 2)
                : Page([Account("ONE", "Acme, Inc.")], 1, 2, "?page=2&search=targeted");
        };
        var request = s.Row.RequestedBilling with { CompanyName = "Acme" };
        var candidates = s.Gateway.FindOrganizationCandidates(request);
        Assert.Equal(2, candidates.Count);
        Assert.Equal(AccountMatchKind.Ambiguous, AccountIdentityRules.Resolve(candidates, request).Kind);
        Assert.Equal(2, s.Gateway.RequestCount);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void CompleteTargetedEmptySearchProvesAbsenceWithoutTheFullIndex()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.SearchOverride = request => { Assert.True(Targeted(request)); return Page([]); };
        Assert.Empty(s.Gateway.FindOrganizationCandidates(s.Row.RequestedBilling with { CompanyName = "Acme, Inc." }));
        Assert.Equal(1, s.Gateway.RequestCount);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void UnsupportedQueryFallsBackAndFindsVariants()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.SearchOverride = request => Targeted(request) ? new(HttpStatusCode.BadRequest) { Content = new StringContent("{}") }
            : Page([Account("MATCH", "aCmE LLC")]);
        Assert.Single(s.Gateway.FindOrganizationCandidates(s.Row.RequestedBilling with { CompanyName = "Acme" }));
        Assert.Equal(2, s.Gateway.RequestCount);
    }

    [Fact]
    public void AResponseThatIgnoresTheNameFilterTriggersTheFullIndex()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.SearchOverride = request => Page(Targeted(request) ? [Account("OTHER", "Other Company")]
            : [Account("MATCH", "Acme LLC")]);
        Assert.Single(s.Gateway.FindOrganizationCandidates(s.Row.RequestedBilling with { CompanyName = "Acme" }));
        Assert.Equal(2, s.Gateway.RequestCount);
    }

    [Fact]
    public void FailedTargetedLaterPageNeverBecomesAbsenceAndFallbackMustAlsoComplete()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var first = true;
        s.Transport.SearchOverride = request =>
        {
            if (first) { first = false; return Page([Account("MATCH", "Acme LLC")], 1, 2, "?page=2&search=targeted"); }
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("{}") };
        };
        Assert.Throws<DecisionSearchException>(() => s.Gateway.FindOrganizationCandidates(s.Row.RequestedBilling with { CompanyName = "Acme" }));
        Assert.Equal(3, s.Gateway.RequestCount);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void FailedTargetedLaterPageUsesOnlyCompleteFallbackCandidates()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.SearchOverride = request => request.RequestUri!.Query.Contains("page=2")
            ? new(HttpStatusCode.BadRequest) { Content = new StringContent("{}") }
            : Targeted(request) ? Page([Account("PARTIAL", "Acme LLC")], 1, 2, "?page=2&search=targeted")
            : Page([Account("CURRENT", "Acme, Inc.")]);
        Assert.Equal("CURRENT", Assert.Single(s.Gateway.FindOrganizationCandidates(s.Row.RequestedBilling with { CompanyName = "Acme" })).AccountCode);
        Assert.Equal(3, s.Gateway.RequestCount);
    }

    [Fact]
    public void FreshTargetedCreationGuardStopsForANewlyAppearedDuplicate()
    {
        using var s = new RetryBoundaryTests.Scenario("Create organization account");
        var searches = 0;
        s.Transport.SearchOverride = request =>
        {
            Assert.True(Targeted(request));
            return ++searches == 1 ? Page([]) : Page([Account("NEW_DUPLICATE", s.Row.RequestedBilling.CompanyName)]);
        };
        Assert.Empty(s.Gateway.FindOrganizationCandidates(s.Row.RequestedBilling));
        s.Apply();
        Assert.Equal(2, searches);
        Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus);
        Assert.Contains("duplicate candidates", s.Row.UpdateMessage);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void OversizedTargetedSearchFallsBackWithoutUsingPartialCandidates()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.SearchOverride = request => Targeted(request) ? Page([Account("PARTIAL", "Acme")], total: 6000)
            : Page([Account("MATCH", "Acme LLC")]);
        Assert.Equal("MATCH", Assert.Single(s.Gateway.FindOrganizationCandidates(s.Row.RequestedBilling with { CompanyName = "Acme" })).AccountCode);
        Assert.Equal(2, s.Gateway.RequestCount);
    }
}
