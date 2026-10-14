using System.Text.RegularExpressions;

namespace ServiceOrderEntry;

internal sealed record CompanySearch(string Filter, IReadOnlyList<string> Fragments)
{
    // Normalized equality preserves tokens. An ASCII fragment inside one of those
    // tokens must occur in every matching raw name, allowing for letter case.
    // Exclude k: NFC also maps the Kelvin sign to K. Other scripts and short names
    // without a useful covered fragment retain the complete-index fallback.
    internal const string CoveredCharacters = "abcdefghijlmnopqrstuvwxyz0123456789";
    internal const int ResultCeiling = 5000;

    internal static CompanySearch? For(string company)
        => Create(TextRules.NormalizeCompany(company), "O", "Name");

    internal static CompanySearch? ForEmail(string email)
        => Create(TextRules.NormalizeEmail(email), "P", "Email");

    private static CompanySearch? Create(string identity, string accountClass, string field)
    {
        var digits = Regex.Matches(identity, "[0-9]{2,}").Select(x => x.Value)
            .OrderByDescending(x => x.Length).FirstOrDefault();
        var letters = Regex.Matches(identity, "[a-jl-z0-9]{3,}").Select(x => x.Value)
            .OrderByDescending(x => x.Length).FirstOrDefault();
        var fragment = digits is not null ? digits[..Math.Min(8, digits.Length)]
            : letters is not null ? letters[..Math.Min(4, letters.Length)] : null;
        if (fragment is null) return null;

        var variants = new List<string> { "" };
        foreach (var character in fragment)
        {
            var forms = char.IsAsciiLetter(character) ? new[] { character, char.ToUpperInvariant(character) } : new[] { character };
            variants = variants.SelectMany(prefix => forms.Select(form => prefix + form)).ToList();
        }
        var filter = $"Class eq '{accountClass}' and (" + string.Join(" or ", variants.Select(x => $"substringof('{x}', {field})")) + ")";
        return new(filter, variants);
    }

    internal bool Includes(string? name) => name is not null && Fragments.Any(fragment => name.Contains(fragment, StringComparison.Ordinal));
}
