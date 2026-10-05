using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ServiceOrderEntry;

internal static partial class PaymentScheduleExtractor
{
    public static string FromPdf(byte[] data)
    {
        using var document = PdfDocument.Open(data);
        return FromText(string.Join("\n", document.GetPages().Select(page => ContentOrderTextExtractor.GetText(page))));
    }

    public static string FromText(string? text)
    {
        var normalized = NormalizeLigatures(text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var schedules = SchedulePattern().Matches(normalized).Select(match => Format(match.Value)).ToList();
        if (schedules.Select(NormalizeForComparison).Distinct(StringComparer.Ordinal).Count() > 1)
            throw new RecoveryReviewException("REVIEW: one contract contains conflicting complete Payment Schedules.");
        return schedules.FirstOrDefault() ?? "";
    }

    private static string Format(string value)
    {
        var lines = value.Split('\n')
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
