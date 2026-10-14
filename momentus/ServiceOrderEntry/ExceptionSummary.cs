using System.Net;
using System.Text;

namespace ServiceOrderEntry;

internal static class ExceptionSummary
{
    public const string Recipient = "kylep@kallman.com";
    internal static bool NeedsAttention(RunRow row) => row.Outcome is "REVIEW" or "FAILED" or "UNKNOWN" ||
        row.ValidationStatus is "REVIEW" or "FAILED" || row.ExhibitorActivationPending || row.ManualCorrections.Count > 0;

    internal static string Build(IEnumerable<RunRow> rows, string runId, string report)
    {
        static string H(string value) => WebUtility.HtmlEncode(value);
        var body = new StringBuilder("<html><body style=\"font-family:Calibri,Arial,sans-serif;font-size:12pt;color:#000;background:#fff\"><p>Hi Kyle,</p><p>These service orders need attention. Verified work has been retained. Manual corrections listed below do not block order processing. Orders with blocking problems remain incomplete and their Finance completion emails are withheld; the result and status actions for each order are shown below.</p>");
        foreach (var row in rows.Where(NeedsAttention))
        {
            body.Append($"<p><strong>Order {row.OrderNumber} — {H(row.ExhibitorName)} (event {row.EventId}, exhibitor {row.ExhibitorId})</strong><br>");
            if (row.PartialWork.Count > 0) body.Append($"Saved/verified: {H(string.Join(", ", row.PartialWork))}.<br>");
            foreach (var issue in row.Issues.Distinct()) body.Append(H(issue.Description)).Append("<br>");
            foreach (var correction in row.ManualCorrections.Distinct())
                body.Append("Manual correction (order processing continues): ").Append(H(correction.Description)).Append("<br>");
            if (row.Issues.Count == 0) body.Append(H(FirstMessage(row))).Append("<br>");
            else if (row.UpdateMessage.Length > 0 && !row.UpdateMessage.StartsWith("Incomplete order retained", StringComparison.Ordinal) &&
                !row.Issues.Any(x => x.Reason.Contains(row.UpdateMessage, StringComparison.Ordinal)))
                body.Append(H(row.UpdateMessage)).Append("<br>");
            body.Append($"Result: {H(row.Outcome)}; order work: {H(row.ServiceOrderUpdateStatus)}; order status action: {H(row.OrderStatusAction)}; exhibitor status action: {H(row.ExhibitorStatusAction)}.</p>");
        }
        body.Append($"<p>Run: {H(runId)}<br>Full report: {H(report)}</p></body></html>");
        return body.ToString();
    }

    private static string FirstMessage(RunRow row) => string.Join(" ", new[]
    {
        row.ValidationMessage, row.UpdateMessage,
        row.ExhibitorActivationPending ? "Exhibitor activation is waiting for other pending orders or unresolved work." : ""
    }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
}

internal sealed partial class Runner
{
    private int SendExceptionSummary(MomentusGateway gateway, string report)
    {
        if (!rows.Any(ExceptionSummary.NeedsAttention)) return 0;
        var body = ExceptionSummary.Build(rows, runId, report);
        var draft = report + ".exceptions.html";
        FileJournalStore.DurableWrite(draft, body); // Preserve the exact report before any send.
        if (!options.Apply)
        {
            Console.WriteLine($"PREVIEW: exception summary for {ExceptionSummary.Recipient}: {draft}; no email sent.");
            return 0;
        }
        RunRow? anchor = null;
        foreach (var candidate in rows.Where(x => x.OrderNumber > 0 && x.EventId > 0 && x.ExhibitorId > 0))
        {
            try
            {
                var current = gateway.GetOrder(candidate.OrderNumber);
                if (TextRules.Same(current.OrganizationCode, options.OrganizationCode) && current.OrderNumber == candidate.OrderNumber &&
                    current.Event == candidate.EventId && current.Exhibitor == candidate.ExhibitorId)
                { anchor = candidate; break; }
            }
            catch (JournalStorageException) { throw; }
            catch (Exception ex) { Console.Error.WriteLine($"Exception-summary attachment skipped unavailable order {candidate.OrderNumber}: {ex.Message}"); }
        }
        if (anchor is null)
        {
            Console.Error.WriteLine($"Exception email could not be sent: no verified order identity is available. Summary: {draft}");
            return 1;
        }
        try
        {
            gateway.SendExceptionSummary(anchor, $"Service Order Entry — attention needed ({rows.Count(ExceptionSummary.NeedsAttention)} orders) — {runId}", body, runId);
            Console.WriteLine($"Exception summary accepted by Momentus for {ExceptionSummary.Recipient}. Summary: {draft}");
            return 0;
        }
        catch (JournalStorageException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Exception email { (ex is UnknownWriteOutcomeException ? "UNKNOWN (not retried)" : "FAILED") }: {ex.Message}. Summary retained: {draft}");
            return ex is UnknownWriteOutcomeException ? 3 : 1;
        }
    }
}
