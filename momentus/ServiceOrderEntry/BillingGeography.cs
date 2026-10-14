using Ungerboeck.Api.Models.Subjects;
using System.Text.RegularExpressions;

namespace ServiceOrderEntry;

internal static class BillingGeography
{
    private static readonly string[] JungGuCities = ["Seoul", "Busan", "Daegu", "Daejeon", "Ulsan"];

    public static BillingRequest Address(string country, BillingRequest request, IReadOnlyList<StatesModel> states)
    {
        if (TextRules.Same(country, "KOR") && TextRules.Same(request.State, "Jung-gu"))
        {
            // Jung-gu is a district shared by several cities. The submitted city,
            // rather than the district name, must identify a unique top-level region.
            if (!JungGuCities.Any(city => TextRules.Same(city, request.City)))
                throw new RecoveryReviewException("REVIEW: Jung-gu needs a supported Korean city to identify its top-level region.");
            var region = State(country, request.City, states);
            var address = Regex.IsMatch(request.Address, @"(?<![\p{L}\p{N}-])Jung-gu(?![\p{L}\p{N}-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                ? request.Address : request.Address + ", " + request.State.Trim();
            return request with { Country = country, State = region, Address = address };
        }
        return request with { Country = country, State = State(country, request.State, states) };
    }

    public static KoreanAddressLabels Labels(AccountInfo account, IReadOnlyList<StatesModel> states)
    {
        var code = State("KOR", account.State, states);
        var region = states.Single(x => TextRules.Same(x.Code, code));
        if (string.IsNullOrWhiteSpace(region.Name))
            throw new RecoveryReviewException("REVIEW: Korean address region has no display name.");
        var district = Regex.Match(account.Address, @",\s*(?<district>[\p{L}][\p{L}-]*-gu)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return new(account.Address, account.City, account.State, region.Name.Trim(),
            district.Success ? district.Groups["district"].Value : "",
            district.Success ? account.Address[..district.Index] : account.Address);
    }

    public static string Country(string value, IReadOnlyList<CountriesModel> countries)
    {
        // ISO identifiers take precedence over legacy tenant codes. Kallman has
        // a deprecated tenant code USA while the actual US entry is ***/US/USA.
        var iso = countries.Where(x => new[] { x.ISOCode2, x.ISOCode3 }
            .Any(alias => !string.IsNullOrWhiteSpace(alias) && TextRules.Same(alias, value))).ToList();
        var matches = iso.Count > 0 ? iso : countries.Where(x => new[] { x.Code, x.Description, x.ISOCode4,
            x.AltDescription1, x.AltDescription2, x.AltDescription3, x.AltDescription4, x.AltDescription5 }
            .Any(alias => !string.IsNullOrWhiteSpace(alias) && TextRules.Same(alias, value))).ToList();
        // Approved submitted-country alias: always select the tenant's KOR entry.
        if (TextRules.Same(value, "South Korea"))
            matches = countries.Where(x => TextRules.Same(x.Code, "KOR")).ToList();
        if (matches.Count != 1 || string.IsNullOrWhiteSpace(matches[0].Code))
            throw new RecoveryReviewException("REVIEW: billing country is unknown or ambiguous in the Momentus country list.");
        return matches[0].Code.Trim();
    }

    public static string State(string country, string value, IReadOnlyList<StatesModel> states)
    {
        if (string.IsNullOrWhiteSpace(value)) return ""; // State remains optional under existing billing policy.
        if (states.Any(x => !TextRules.Same(x.CountryCode, country) || string.IsNullOrWhiteSpace(x.Code)))
            throw new RecoveryReviewException("REVIEW: Momentus state lookup contains missing or conflicting country/state identities.");
        var matches = states.Where(x => new[] { x.Code, x.Name, x.AltDescription1, x.AltDescription2,
            x.AltDescription3, x.AltDescription4, x.AltDescription5 }
            .Any(alias => !string.IsNullOrWhiteSpace(alias) && TextRules.Same(alias, value))).ToList();
        if (matches.Count != 1)
            throw new RecoveryReviewException("REVIEW: billing state is unknown or ambiguous within its Momentus country.");
        return matches[0].Code.Trim();
    }
}

internal sealed record KoreanAddressLabels(string SavedAddress, string City, string RegionCode, string RegionName,
    string District, string StreetAddress)
{
    public string CityRegion => string.Join(" / ", new[] { City, RegionName }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase));

    public void Verify(AccountInfo account)
    {
        if (!TextRules.Same(account.Country, "KOR") || !TextRules.Same(account.Address, SavedAddress) ||
            !TextRules.Same(account.City, City) || !TextRules.Same(account.State, RegionCode))
            throw new RecoveryReviewException("REVIEW: Korean address labels differ from the verified billing address.");
    }
}
