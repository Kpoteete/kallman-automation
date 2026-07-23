using System.Text.RegularExpressions;

namespace MarketSegmentApplication.Models;

public static class TextUtil
{
    public static string Clean(string? value)
    {
        return Normalize(value);
    }

    public static string CleanKey(string? value)
    {
        return CleanKeyField(value);
    }

    public static string CleanKeyField(string? value)
    {
        var text = Normalize(value);

        if (text == "'" || text == "\"" || text == "''" || text == "\"\"")
        {
            return string.Empty;
        }

        return text;
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Replace('\u00A0', ' ').Trim();
        text = Regex.Replace(text, @"\s+", " ");
        return text.Trim();
    }

    public static string NormalizeEmail(string? value)
    {
        return Normalize(value).ToLowerInvariant();
    }

    public static bool IsValidEmailForLookup(string? value)
    {
        var email = NormalizeEmail(value);
        if (string.IsNullOrWhiteSpace(email)) return false;

        return email.Contains('@') &&
               email.Contains('.') &&
               !email.Contains(' ') &&
               email.Length <= 254;
    }

    public static bool EqualsKey(string? left, string? right)
    {
        return string.Equals(CleanKeyField(left), CleanKeyField(right), StringComparison.OrdinalIgnoreCase);
    }

    public static bool EqualsTrimmedIgnoreCase(string? left, string? right)
    {
        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }


    public static string RootDomain(string? value)
    {
        string text = Normalize(value).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // If a formula or template stores multiple values, use the first URL-like token.
        text = text.Split(new[] { ' ', ';', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        if (!text.Contains("://")) text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
        {
            text = text.Replace("https://", string.Empty).Replace("http://", string.Empty);
            text = text.Split('/')[0];
            text = text.Split(':')[0];
            if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) text = text[4..];
            return text.Trim('.');
        }

        string host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        return host.Trim('.');
    }



    public static string CompanyNameWithoutParentheses(string? value)
    {
        string text = Normalize(value);
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        text = Regex.Replace(text, @"\([^)]*\)", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }

    public static string CanonicalCompanyName(string? value)
    {
        string text = Normalize(value).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Remove parenthetical acronyms/notes, e.g. "American Institute in Taiwan (AIT)".
        text = Regex.Replace(text, @"\([^)]*\)", " ");

        // Normalize common symbols before stripping punctuation.
        text = text.Replace("&", " and ");

        // Remove punctuation and collapse to words.
        text = Regex.Replace(text, @"[^a-z0-9\s]", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var legalSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "inc", "incorporated", "llc", "ltd", "limited", "corp", "corporation",
            "co", "company", "plc", "gmbh", "sa", "sarl", "bv", "ag", "kg",
            "pte", "pty", "lp", "llp"
        };

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && legalSuffixes.Contains(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Join(" ", words);
    }

    public static bool RelaxedCompanyNameEquals(string? left, string? right)
    {
        string l = CanonicalCompanyName(left);
        string r = CanonicalCompanyName(right);
        return !string.IsNullOrWhiteSpace(l) &&
               !string.IsNullOrWhiteSpace(r) &&
               string.Equals(l, r, StringComparison.OrdinalIgnoreCase);
    }

    public static string EscapeODataString(string? value)
    {
        return CleanKeyField(value).Replace("'", "''");
    }

    public static bool IsBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(CleanKeyField(value));
    }

    public static string SafeMessage(string? value)
    {
        return Normalize(value);
    }

    public static string Shorten(string? value, int maxLength = 500)
    {
        var text = Normalize(value);
        if (text.Length <= maxLength) return text;
        if (maxLength <= 0) return string.Empty;
        if (maxLength <= 3) return text[..maxLength];
        return text[..(maxLength - 3)] + "...";
    }
}
