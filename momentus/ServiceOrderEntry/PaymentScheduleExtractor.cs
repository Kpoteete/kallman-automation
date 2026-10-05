using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ServiceOrderEntry;

internal static partial class PaymentScheduleExtractor
{
    public static string FromPdf(byte[] data)
    {
        using var document = PdfDocument.Open(data);
        foreach (var page in document.GetPages())
        {
            var result = FromText(ContentOrderTextExtractor.GetText(page));
            if (result.Length > 0) return result;
        }
        return "";
    }

    public static string FromText(string? text)
    {
        var normalized = NormalizeLigatures(text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var match = SchedulePattern().Match(normalized);
        if (!match.Success) return "";
        var lines = match.Value.Split('\n')
            .Select(x => MultiSpace().Replace(x.Trim(), " "))
            .Select(x => x.Length > 0 && x[0] == '?' ? x[1..].TrimStart() : x)
            .Where(x => x.Length > 0);
        return string.Join(Environment.NewLine, lines);
    }

    public static string NormalizeForComparison(string? value) => MultiSpace().Replace((value ?? "").Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim(), " ");

    private static string NormalizeLigatures(string value) => value
        .Replace("\uFB00", "ff", StringComparison.Ordinal)
        .Replace("\uFB01", "fi", StringComparison.Ordinal)
        .Replace("\uFB02", "fl", StringComparison.Ordinal)
        .Replace("\uFB03", "ffi", StringComparison.Ordinal)
        .Replace("\uFB04", "ffl", StringComparison.Ordinal)
        .Replace("\uFB05", "st", StringComparison.Ordinal)
        .Replace("\uFB06", "st", StringComparison.Ordinal);

    [GeneratedRegex(@"Payment Schedule\s+.*?(?:Note:\s*)?Please refer.*?due dates\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SchedulePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpace();
}
