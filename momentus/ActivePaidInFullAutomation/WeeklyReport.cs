using ClosedXML.Excel;
using System.Globalization;
using System.Text.Json;

namespace ActivePaidInFullAutomation;

internal sealed record ReportRow(DateTime ChangedOn, string Mode, string Result, int EventId, string EventName, int ExhibitorId,
    string ExhibitorName, string AccountCode, string BoothNumber, int? OrderNumber, string CategorySequence, string CategoryName,
    decimal? OrderedTotal, decimal? Payments, decimal? NetDue, string PriorStatus, string NewStatus, string Detail,
    string ChangeType = "Order qualification", string MainExhibitorAccount = "");

internal sealed class WeeklyReportStore(string reportFolder, string stateFolder)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string? Record(ReportRow row, bool rebuild = true)
    {
        Directory.CreateDirectory(reportFolder); Directory.CreateDirectory(Path.Combine(stateFolder, "records"));
        var key = $"{row.ChangedOn:yyyyMMddHHmmssfff}-{row.EventId}-{row.ExhibitorId}-{row.OrderNumber}-{Safe(row.Result)}.json";
        AtomicWrite(Path.Combine(stateFolder, "records", key), JsonSerializer.Serialize(row, JsonOptions)); return rebuild ? Rebuild(row.ChangedOn) : null;
    }

    public string Rebuild(DateTime asOf)
    {
        var week = ReportingWeek.For(asOf);
        var rows = Directory.Exists(Path.Combine(stateFolder, "records")) ? Directory.EnumerateFiles(Path.Combine(stateFolder, "records"), "*.json")
            .Select(ReadRow).Where(x => x is not null).Cast<ReportRow>()
            .Where(x => DateOnly.FromDateTime(x.ChangedOn) >= week.Start && DateOnly.FromDateTime(x.ChangedOn) <= week.End && x.Result == "Updated")
            .GroupBy(x => $"{x.EventId}|{x.ExhibitorId}", StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(x => x.ChangedOn).First())
            .OrderBy(x => x.EventName).ThenBy(x => x.ExhibitorName).ToList() : [];
        var destination = Path.Combine(reportFolder, $"Ready-to-Register_{week.Start:yyyy-MM-dd}_to_{week.End:yyyy-MM-dd}.xlsx");
        var temp = Path.Combine(reportFolder, $".{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.tmp.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var ws = workbook.Worksheets.Add("Ready to Register");
            ws.Cell("A1").Value = "Ready to register"; ws.Cell("A2").Value = $"Reporting week: {week.Start:MMMM d, yyyy} through {week.End:MMMM d, yyyy}";
            ws.Cell("A3").Value = "Confirmed exhibitor status changes from Active (2) to Active Paid in Full (22).";
            ws.Range("A1:T1").Merge().Style.Font.SetBold().Font.SetFontSize(16).Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(XLColor.FromHtml("#17365D"));
            ws.Range("A2:T2").Merge().Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#D9EAF7")); ws.Range("A3:T3").Merge().Style.Font.SetItalic();
            string[] headers = ["Changed on", "Event ID", "Event", "Exhibitor ID", "Exhibitor", "Account", "Booth", "Change type", "Main exhibitor account", "Order", "Category sequence", "Order category", "Ordered total", "Payments", "Ordered net due", "Prior status", "New status", "Result", "Mode", "Detail"];
            for (var c = 0; c < headers.Length; c++) ws.Cell(5, c + 1).Value = headers[c];
            ws.Range(5, 1, 5, headers.Length).Style.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(XLColor.FromHtml("#4472C4"));
            for (var i = 0; i < rows.Count; i++)
            {
                var r = i + 6; var x = rows[i];
                ws.Cell(r, 1).Value = x.ChangedOn; ws.Cell(r, 2).Value = x.EventId; ws.Cell(r, 3).Value = x.EventName; ws.Cell(r, 4).Value = x.ExhibitorId;
                ws.Cell(r, 5).Value = x.ExhibitorName; ws.Cell(r, 6).Value = x.AccountCode; ws.Cell(r, 7).Value = x.BoothNumber; ws.Cell(r, 8).Value = x.ChangeType; ws.Cell(r, 9).Value = x.MainExhibitorAccount;
                if (x.OrderNumber.HasValue) ws.Cell(r, 10).Value = x.OrderNumber.Value; ws.Cell(r, 11).Value = x.CategorySequence; ws.Cell(r, 12).Value = x.CategoryName;
                if (x.OrderedTotal.HasValue) ws.Cell(r, 13).Value = x.OrderedTotal.Value; if (x.Payments.HasValue) ws.Cell(r, 14).Value = x.Payments.Value; if (x.NetDue.HasValue) ws.Cell(r, 15).Value = x.NetDue.Value;
                ws.Cell(r, 16).Value = x.PriorStatus; ws.Cell(r, 17).Value = x.NewStatus; ws.Cell(r, 18).Value = x.Result; ws.Cell(r, 19).Value = x.Mode; ws.Cell(r, 20).Value = x.Detail;
            }
            var last = Math.Max(6, rows.Count + 5); var range = ws.Range(5, 1, last, headers.Length); var table = range.CreateTable(); table.Theme = XLTableTheme.TableStyleMedium2;
            ws.Column(1).Style.DateFormat.Format = "mm/dd/yy h:mm AM/PM"; foreach (var c in new[] { 13, 14, 15 }) ws.Column(c).Style.NumberFormat.Format = "\"$\"#,##0.00;[Red]-\"$\"#,##0.00";
            ws.SheetView.FreezeRows(5); ws.Columns().AdjustToContents(); ws.Column(3).Width = Math.Min(ws.Column(3).Width, 30); ws.Column(5).Width = Math.Min(ws.Column(5).Width, 34);
            ws.Column(12).Width = Math.Min(ws.Column(12).Width, 36); ws.Column(20).Width = 55; ws.Column(20).Style.Alignment.WrapText = true;
            ws.PageSetup.PageOrientation = XLPageOrientation.Landscape; ws.PageSetup.FitToPages(1, 0); ws.PageSetup.PrintAreas.Add(range.RangeAddress.ToString()); ws.PageSetup.SetRowsToRepeatAtTop(1, 5);
            workbook.SaveAs(temp);
        }
        File.Move(temp, destination, true); return destination;
    }
    private static ReportRow? ReadRow(string path) { try { return JsonSerializer.Deserialize<ReportRow>(File.ReadAllText(path)); } catch { return null; } }
    private static string Safe(string value) => string.Concat(value.Where(char.IsLetterOrDigit));
    private static void AtomicWrite(string path, string content) { var temp = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp"; File.WriteAllText(temp, content); File.Move(temp, path, true); }
}
