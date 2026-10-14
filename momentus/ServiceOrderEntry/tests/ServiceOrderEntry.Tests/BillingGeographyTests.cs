using System.Net;
using System.Text;
using Newtonsoft.Json;
using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class BillingGeographyTests
{
    private static readonly CountriesModel Usa = new() { Code = "***", Description = "United States", ISOCode2 = "US", ISOCode3 = "USA" };
    private static readonly StatesModel Montana = new() { CountryCode = "***", Code = "MT", Name = "Montana" };
    private static readonly CountriesModel SouthKorea = new() { Code = "KOR", Description = "Korea, Republic of", ISOCode2 = "KR", ISOCode3 = "KOR" };
    private static readonly StatesModel[] KoreanRegions =
    [
        new() { CountryCode = "KOR", Code = "11", Name = "Seoul" },
        new() { CountryCode = "KOR", Code = "26", Name = "Busan" },
        new() { CountryCode = "KOR", Code = "27", Name = "Daegu" },
        new() { CountryCode = "KOR", Code = "30", Name = "Daejeon" },
        new() { CountryCode = "KOR", Code = "31", Name = "Ulsan" }
    ];

    [Theory]
    [InlineData("Seoul", "11")]
    [InlineData("Busan", "26")]
    [InlineData("Daegu", "27")]
    [InlineData("Daejeon", "30")]
    [InlineData("Ulsan", "31")]
    public void JungGuDistrictUsesItsSubmittedCityForTheTopLevelRegion(string city, string code)
    {
        var request = RetryBoundaryTests.TestRequest() with { City = city, State = "Jung-gu", Country = "South Korea" };
        var resolved = BillingGeography.Address("KOR", request, KoreanRegions);
        Assert.Equal(code, resolved.State);
        Assert.Equal("KOR", resolved.Country);
        Assert.Equal("1 Test Street, Jung-gu", resolved.Address);
        Assert.Equal("Jung-gu", request.State);
        Assert.Equal("1 Test Street", request.Address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("11")]
    public void DistrictAloneCannotSelectSeoul(string city) => Assert.Throws<RecoveryReviewException>(() =>
        BillingGeography.Address("KOR", RetryBoundaryTests.TestRequest() with { City = city, State = "Jung-gu" }, KoreanRegions));

    [Fact]
    public void DistrictResolutionStillRejectsMissingAmbiguousOrForeignReferenceData()
    {
        var request = RetryBoundaryTests.TestRequest() with { City = "Seoul", State = "Jung-gu" };
        Assert.Throws<RecoveryReviewException>(() => BillingGeography.Address("KOR", request, []));
        Assert.Throws<RecoveryReviewException>(() => BillingGeography.Address("KOR", request, [KoreanRegions[0], new() { CountryCode = "KOR", Code = "OTHER", Name = "Seoul" }]));
        Assert.Throws<RecoveryReviewException>(() => BillingGeography.Address("KOR", request, [Montana]));
        Assert.Throws<RecoveryReviewException>(() => BillingGeography.Address("***", request, [Montana]));
        Assert.Throws<RecoveryReviewException>(() => BillingGeography.Address("KOR", request with { State = "Unknown" }, KoreanRegions));
    }

    [Fact]
    public void DistrictAlreadyInAddressIsPreservedWithoutDuplication()
    {
        var request = RetryBoundaryTests.TestRequest() with { City = "Seoul", State = "jung-gu", Address = "1 Test Street, Jung-gu" };
        Assert.Equal(request.Address, BillingGeography.Address("KOR", request, KoreanRegions).Address);
    }

    [Fact]
    public void DistrictAddressMatchesResolvedAccountOnlyWhenDistrictAndCityArePreserved()
    {
        using var s = new RetryBoundaryTests.Scenario(); KoreanReferences(s);
        var request = s.Row.RequestedBilling with { City = "Seoul", State = "Jung-gu", Country = "South Korea" };
        var account = s.Row.ExistingBillToAccount with { Address = "1 Test Street, Jung-gu", City = "Seoul", State = "11", Country = "KOR" };
        Assert.True(s.Gateway.BillingAddressesMatch(account, request));
        Assert.False(s.Gateway.BillingAddressesMatch(account with { Address = "1 Test Street" }, request));
        Assert.False(s.Gateway.BillingAddressesMatch(account with { City = "Busan", State = "26" }, request));
    }

    [Fact]
    public void KoreanAccountAndContactAndVerifiedEmailPreserveDistrictWithCorrectLabels()
    {
        using var s = new RetryBoundaryTests.Scenario(); KoreanReferences(s);
        s.Row.RequestedBilling = s.Row.RequestedBilling with { CompanyName = "New Korean Billing Company", Email = "koreanbilling@example.invalid",
            City = "Seoul", State = "Jung-gu", Country = "South Korea" };
        s.Transport.BillingInstructions = s.Row.RequestedBilling;
        Runner.DecideAccountAndAddress(s.Gateway, s.Row); Runner.DecideContact(s.Gateway, s.Row);
        s.Apply();
        Assert.True(s.Row.Outcome == "SUCCESS", s.Row.UpdateMessage);
        foreach (var code in new[] { s.Row.FinalBillToAccount, s.Row.FinalBillToContact })
        {
            var saved = s.Transport.Accounts[code];
            Assert.Equal("1 Test Street, Jung-gu", saved.Address1);
            Assert.Equal("Seoul", saved.City); Assert.Equal("11", saved.State); Assert.Equal("KOR", saved.Country);
        }
        var journal = s.Store.Load().Single();
        Assert.Equal("Jung-gu", journal.Plan.RequestedBilling.State);
        Assert.Equal("Jung-gu", journal.Handoff!.Billing.KoreanAddressLabels!.District);
        var email = Assert.Single(s.Transport.Emails).HtmlText;
        Assert.Contains(">District</td><td style=\"padding:3px 0;vertical-align:top\">Jung-gu</td>", email);
        Assert.Contains(">City / Region</td><td style=\"padding:3px 0;vertical-align:top\">Seoul</td>", email);
        Assert.Contains(">Country</td><td style=\"padding:3px 0;vertical-align:top\">South Korea</td>", email);
        Assert.DoesNotContain("City / State / Postal", email);
        Assert.DoesNotContain(">11</td>", email); Assert.DoesNotContain(">KOR</td>", email);
        Assert.Equal(email, ReadyEmailBuilder.Build(journal.Handoff.EmailRow(), journal.Handoff.Attachments));
    }

    [Fact]
    public void AboveAddressLabelsUseSavedAddressAndCannotBorrowUncommittedSubmittedDistrict()
    {
        using var s = new RetryBoundaryTests.Scenario(); KoreanReferences(s);
        s.Row.ExistingBillToAccount = s.Row.ExistingBillToAccount with { Address = "Existing Street", City = "Busan", State = "26", Country = "KOR" };
        s.Row.RequestedBilling = s.Row.RequestedBilling with { UseRequestedAddress = "ECA", City = "Seoul", State = "Jung-gu", Country = "South Korea" };
        Runner.DecideAccountAndAddress(s.Gateway, s.Row);
        var email = ReadyEmailBuilder.Build(s.Row, []);
        Assert.Contains("Busan", email); Assert.DoesNotContain("Seoul", email); Assert.DoesNotContain(">District</td>", email);
        var labels = s.Row.EffectiveBilling!.KoreanAddressLabels!;
        Assert.Throws<RecoveryReviewException>(() => labels.Verify(s.Row.EffectiveBilling.Account with { Address = "Changed Street" }));
    }

    private static void KoreanReferences(RetryBoundaryTests.Scenario s) => s.Transport.SearchOverride = request =>
        request.RequestUri!.AbsolutePath.EndsWith("/Countries/10") ? SinglePage([Usa, SouthKorea]) :
        request.RequestUri.AbsolutePath.EndsWith("/States") ? SinglePage(KoreanRegions) : null;

    [Theory]
    [InlineData("South Korea")]
    [InlineData("south korea")]
    [InlineData(" South Korea ")]
    public void SouthKoreaUsesApprovedKorCode(string value) => Assert.Equal("KOR", BillingGeography.Country(value,
        [SouthKorea, new() { Code = "PRK", Description = "Korea (DPR)", ISOCode2 = "KP", ISOCode3 = "PRK" }]));

    [Fact]
    public void SouthKoreaAliasRequiresUniqueKorEntryAndOverridesDescriptionCollision()
    {
        Assert.Throws<RecoveryReviewException>(() => BillingGeography.Country("South Korea", [Usa]));
        Assert.Throws<RecoveryReviewException>(() => BillingGeography.Country("South Korea", [SouthKorea, SouthKorea]));
        Assert.Equal("KOR", BillingGeography.Country("South Korea", [SouthKorea, new() { Code = "OLD", Description = "South Korea" }]));
        Assert.Throws<RecoveryReviewException>(() => BillingGeography.Country("Korea", [SouthKorea]));
    }

    [Fact]
    public void NewAccountAndContactWriteKorWhilePreservingSubmittedCountry()
    {
        using var s = new RetryBoundaryTests.Scenario("Create organization account"); s.Transport.FaultEnabled = false;
        s.Row.RequestedBilling = s.Row.RequestedBilling with { Country = "South Korea", State = "" };
        s.Transport.BillingInstructions = s.Row.RequestedBilling;
        s.Transport.SearchOverride = request => request.RequestUri!.AbsolutePath.EndsWith("/Countries/10")
            ? SinglePage(new[] { SouthKorea }) : null;
        s.Apply();
        Assert.Equal("SUCCESS", s.Row.Outcome);
        Assert.Equal("KOR", s.Transport.Accounts["NEWACCOUNT"].Country);
        Assert.Equal("KOR", s.Transport.Accounts[s.Row.FinalBillToContact].Country);
        Assert.Equal("South Korea", s.Store.Load().Single().Plan.RequestedBilling.Country);
    }

    [Theory]
    [InlineData("USA")]
    [InlineData("US")]
    [InlineData("United States")]
    [InlineData("***")]
    public void CountryNamesAndIsoCodesResolveToTenantCode(string value) => Assert.Equal("***", BillingGeography.Country(value, [Usa]));

    [Theory]
    [InlineData("MT")]
    [InlineData(" Montana ")]
    [InlineData("montana")]
    public void StateNamesAndCodesResolveWithinCountry(string value) => Assert.Equal("MT", BillingGeography.State("***", value, [Montana]));

    [Fact]
    public void UnknownOrAmbiguousCountriesAndStatesRequireReview()
    {
        Assert.Throws<RecoveryReviewException>(() => BillingGeography.Country("Unknown", [Usa]));
        Assert.Throws<RecoveryReviewException>(() => BillingGeography.Country("USA", [Usa, new() { Code = "OTHER", ISOCode3 = "USA" }]));
        Assert.Throws<RecoveryReviewException>(() => BillingGeography.State("***", "Unknown", [Montana]));
        Assert.Throws<RecoveryReviewException>(() => BillingGeography.State("***", "Montana", [Montana, new() { CountryCode = "***", Code = "XX", Name = "Montana" }]));
        Assert.Throws<RecoveryReviewException>(() => BillingGeography.State("CAN", "Montana", [Montana]));
        Assert.Equal("", BillingGeography.State("***", "", []));
    }

    [Fact]
    public void IsoMappingWinsOverDeprecatedTenantCodeCollision() =>
        Assert.Equal("***", BillingGeography.Country("USA", [Usa, new() { Code = "USA", Description = "do not use" }]));

    [Fact]
    public void MatchingReusesExistingAccountWithoutChangingItsAddress()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Row.RequestedBilling = s.Row.RequestedBilling with { State = "Montana" };
        s.Transport.Accounts["ACCOUNT"] = new() { Organization = "10", AccountCode = "ACCOUNT", Class = "O", Name = s.Row.RequestedBilling.CompanyName,
            Address1 = "1 Test Street", City = "Test City", State = "MT", PostalCode = "12345", Country = "***", EventSalesStatus = "A" };
        Runner.DecideAccountAndAddress(s.Gateway, s.Row);
        Assert.Equal("USE MATCHED BILL-TO ACCOUNT", s.Row.BillToAddressAction);
        Assert.Equal("ACCOUNT", s.Row.FinalBillToAccount);
        Assert.Equal("MT", s.Row.FinalState);
        Assert.Equal("Montana", s.Row.RequestedBilling.State);
        Assert.Equal("A", s.Transport.Accounts["ACCOUNT"].EventSalesStatus);
        Assert.Empty(s.Transport.Mutations);
        Assert.False(s.Gateway.BillingAddressesMatch(s.Row.EffectiveBilling!.Account, s.Row.RequestedBilling with { Address = "Different street" }));
    }

    [Fact]
    public void ReferencePaginationIsCompleteAndStateSearchIsCountryScoped()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.SearchOverride = request =>
        {
            var later = request.RequestUri!.Query.Contains("page=2");
            if (request.RequestUri.AbsolutePath.EndsWith("/Countries/10"))
                return Page(later ? new[] { Usa } : new[] { new CountriesModel { Code = "CAN", Description = "Canada", ISOCode2 = "CA" } }, later,
                    "https://offline.invalid/prod/api/v1/Countries/10?page=2");
            if (!request.RequestUri.AbsolutePath.EndsWith("/States")) return null;
            Assert.Contains("CountryCode eq '***'", Uri.UnescapeDataString(request.RequestUri.Query));
            return Page(later ? new[] { Montana } : new[] { new StatesModel { CountryCode = "***", Code = "ND", Name = "North Dakota" } }, later,
                "https://offline.invalid/prod/api/v1/States?page=2&search=" + Uri.EscapeDataString("CountryCode eq '***'"));
        };
        var resolved = s.Gateway.ResolveBillingGeography(s.Row.RequestedBilling with { State = "Montana" });
        Assert.Equal("***", resolved.Country); Assert.Equal("MT", resolved.State);
        Assert.Equal(4, s.Gateway.RequestCount);
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void IncompleteReferenceListCannotPermitMatchingOrCreation()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.SearchOverride = request => request.RequestUri!.AbsolutePath.EndsWith("/Countries/10") ? Page(new[] { Usa }, false, null) : null;
        Assert.Throws<DecisionSearchException>(() => Runner.DecideAccountAndAddress(s.Gateway, s.Row));
        Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void NewAccountUsesResolvedStateAndCountryCodes()
    {
        using var s = new RetryBoundaryTests.Scenario("Create organization account"); s.Transport.FaultEnabled = false;
        s.Row.RequestedBilling = s.Row.RequestedBilling with { State = "Montana" };
        s.Transport.BillingInstructions = s.Row.RequestedBilling;
        s.Apply();
        Assert.Equal("SUCCESS", s.Row.Outcome);
        Assert.Equal("MT", s.Transport.Accounts["NEWACCOUNT"].State);
        Assert.Equal("***", s.Transport.Accounts["NEWACCOUNT"].Country);
        Assert.Equal("Montana", s.Store.Load().Single().Plan.RequestedBilling.State);
    }

    private static HttpResponseMessage Page(object rows, bool later, string? next)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(rows), Encoding.UTF8, "application/json") };
        response.Headers.Add("X-SearchMetadata", JsonConvert.SerializeObject(new { Page = later ? 2 : 1, Page_Size = 1, PageTotal = 2, ResultsTotal = 2,
            Links = new { Next = later ? null : next } }));
        return response;
    }

    private static HttpResponseMessage SinglePage(object[] rows)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(rows), Encoding.UTF8, "application/json") };
        response.Headers.Add("X-SearchMetadata", JsonConvert.SerializeObject(new { Page = 1, Page_Size = 1000, PageTotal = 1, ResultsTotal = rows.Length, Links = new { Next = (string?)null } }));
        return response;
    }
}
