using System.Text;

namespace ServiceOrderEntry;

internal static class CsvRunWriter
{
    public static readonly string[] Headers =
    [
        "EventID", "ExhibitorID", "Order Number", "Exhibitor Name", "Order Date", "Sales Rep from Exhibitor Record", "OrderAccountRep value to enter on Service Order",
        "Service Order Item Identifier", "Service Order Category to Apply", "Service Order Category Name",
        "Existing Exhibitor Categories", "Exhibitor Categories to Add", "Exhibitor Categories to Remove", "Final Exhibitor Categories", "Approval Needed Blocks Activation",
        "Contract PDFs on Exhibitor", "Contract PDF Copies Needed", "Contract PDF Copy Status",
        "Payment Schedule Text", "SON Note Action", "SON Note Status", "Ready Email Recipient", "Ready Email Status",
        "Order Status Action", "Exhibitor Status Action",
        "Existing BillToAccount Code / ID", "Existing BillToAccount Company Name",
        "Existing BillToAccount Address", "Existing BillToAccount City", "Existing BillToAccount State", "Existing BillToAccount Postal Code", "Existing BillToAccount Country",
        "Existing BillToContact Code / ID", "Existing BillToContact First Name", "Existing BillToContact Last Name", "Existing BillToContact Email",
        "Requested Bill-To Company Name", "Invoice Attention Of", "Requested Bill-To Contact First Name", "Requested Bill-To Contact Last Name",
        "Requested Bill-To Contact Email", "Send Invoice to Following Address", "Requested Bill-To Address", "Requested Bill-To City", "Requested Bill-To State",
        "Requested Bill-To Postal Code", "Requested Bill-To Country", "Bill-To Address Action", "Bill-To Contact Match Result", "Matching Contact Code / ID",
        "Contact Action", "Final BillToAccount Code / ID", "Final BillToContact Code / ID", "Final Bill-To Address", "Final Bill-To City",
        "Final Bill-To State", "Final Bill-To Postal Code", "Final Bill-To Country", "Booth Number to Apply", "Validation Status", "Validation Message",
        "Service Order Update Status", "Update/Error Message", "Run ID", "Processing ID", "Journal Stages",
        "Effective Bill-To Company", "Effective Contact First Name", "Effective Contact Last Name", "Effective Contact Email",
        "Effective Account Source", "Effective Contact Source", "Above Address Retained", "Account Match Kind"
    ];

    public static string Write(string folder, DateTime started, IEnumerable<RunRow> rows, bool apply, string? runId = null)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"service-order-entry-{started:yyyyMMdd-HHmmss}-{runId ?? Guid.NewGuid().ToString("N")}-{(apply ? "apply" : "preview")}.csv");
        var temp = path + ".tmp";
        using (var writer = new StreamWriter(temp, false, new UTF8Encoding(true)))
        {
            WriteLine(writer, Headers);
            foreach (var row in rows) WriteLine(writer, row.ToCsvFields());
        }
        File.Move(temp, path, false);
        return path;
    }

    private static void WriteLine(TextWriter writer, IEnumerable<string> fields) =>
        writer.WriteLine(string.Join(",", fields.Select(Escape)));
    private static string Escape(string? value)
    {
        var text = value ?? "";
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }
}
