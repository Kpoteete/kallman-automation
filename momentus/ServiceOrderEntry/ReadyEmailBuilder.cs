using System.Globalization;
using System.Net;
using System.Text;

namespace ServiceOrderEntry;

internal static class ReadyEmailBuilder
{
    public static string Subject(RunRow row) => row.ApprovalNeeded
        ? $"WAIT FOR SALES APPROVAL - Service Order {row.OrderNumber}"
        : $"Ready for invoicing - Service Order {row.OrderNumber}";

    public static string Build(RunRow row, IEnumerable<DocumentInfo> documents)
    {
        var pdfs = documents.ToList();
        var account = row.EffectiveBilling?.Account ?? row.ExistingBillToAccount;
        var contact = row.EffectiveBilling?.Contact ?? row.ExistingBillToContact;
        var billToCompany = account.Company;
        var contactName = string.Join(" ", new[] { contact.FirstName, contact.LastName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var contactEmail = contact.Email;
        var html = new StringBuilder();
        html.Append("<p>Hi Kyle,</p>");
        if (row.ApprovalNeeded)
            html.Append("<p style=\"font-size:16px;font-weight:bold;color:#b42318\">WAIT: Do not continue until you receive notice from Sales. This order is pending final approval.</p>");
        html.Append($"<p>Service order {row.OrderNumber} for {E(row.ExhibitorName)} is ready for invoicing.</p>");
        html.Append("<table style=\"border-collapse:collapse\">");
        AddRow(html, "Event ID", row.EventId.ToString(CultureInfo.InvariantCulture));
        AddRow(html, "Exhibitor", $"{row.ExhibitorName} ({row.ExhibitorId})");
        AddRow(html, "Service order", row.OrderNumber.ToString(CultureInfo.InvariantCulture));
        AddRow(html, "Order date", row.OrderDate?.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture) ?? "");
        AddRow(html, "Order account rep", row.ProposedOrderAccountRep);
        AddRow(html, "Order category", $"{row.ProposedCategoryName} ({row.ProposedCategory?.ToString(CultureInfo.InvariantCulture) ?? ""})");
        AddRow(html, "Booth", row.ProposedBoothNumber);
        html.Append("</table>");

        html.Append("<p><strong>Bill-To Information</strong></p><table style=\"border-collapse:collapse\">");
        AddRow(html, "Bill-To account", $"{billToCompany} ({row.FinalBillToAccount})");
        AddRow(html, "Bill-To contact", $"{contactName} ({row.FinalBillToContact})");
        AddRow(html, "Email", contactEmail);
        AddRow(html, "Address", row.FinalAddress);
        AddRow(html, "City / State / Postal", string.Join(", ", new[] { row.FinalCity, row.FinalState, row.FinalPostalCode }.Where(x => !string.IsNullOrWhiteSpace(x))));
        AddRow(html, "Country", row.FinalCountry);
        html.Append("</table>");

        html.Append("<p><strong>Payment Schedule</strong><br>");
        html.Append(E(row.PaymentScheduleText).Replace("\r\n", "<br>", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal));
        html.Append("</p>");
        html.Append($"<p><strong>Attachments ({pdfs.Count})</strong></p><ul>");
        foreach (var pdf in pdfs) html.Append($"<li>{E(First(pdf.Description, pdf.DocumentId, $"Contract PDF {pdf.SequenceNumber}"))}</li>");
        html.Append("</ul><p>Best,<br>Kyle</p>");
        return html.ToString();
    }

    private static void AddRow(StringBuilder html, string label, string value) => html.Append(
        $"<tr><td style=\"padding:3px 12px 3px 0;font-weight:bold;vertical-align:top\">{E(label)}</td><td style=\"padding:3px 0;vertical-align:top\">{E(value)}</td></tr>");
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string First(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";
}
