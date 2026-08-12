using ClosedXML.Excel;
using StaleMomentusAccountReport.Models;

namespace StaleMomentusAccountReport.Reports;

public sealed class StaleAccountWorkbookWriter
{
    private static readonly string[] Headers =
    [
        "Account Code",
        "Account Name",
        "Account Entered On",
        "Status Code",
        "Type Code",
        "Class Code",
        "Active Contact Count",
        "Total Service Orders",
        "Latest Service Order Date",
        "Total Exhibitors",
        "Latest Exhibitor Entered On",
        "Priority",
        "Stale Reason",
        "Review Status",
        "Recommended Action",
        "Assigned To",
        "Reviewed Date",
        "Correction Notes",
        "Target Merge Account",
        "New Status",
        "Momentus Updated?"
    ];

    public void Write(string outputPath, IReadOnlyList<StaleAccountResult> results, WorkbookRunInfo runInfo)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var workbook = new XLWorkbook();
        var listsSheet = AddListsSheet(workbook);

        AddReadmeSheet(workbook, runInfo);
        AddSummarySheet(workbook, results, runInfo);

        foreach (var bucket in Enum.GetValues<StaleBucket>())
        {
            AddStaleBucketSheet(workbook, bucket, results.Where(result => result.Bucket == bucket).ToList(), listsSheet);
        }

        listsSheet.Hide();
        workbook.SaveAs(outputPath);
    }

    private static IXLWorksheet AddListsSheet(XLWorkbook workbook)
    {
        var sheet = workbook.Worksheets.Add("Lists");
        for (var i = 0; i < ReviewActionOptions.ReviewStatuses.Length; i++)
        {
            sheet.Cell(i + 1, 1).Value = ReviewActionOptions.ReviewStatuses[i];
        }

        for (var i = 0; i < ReviewActionOptions.MomentusUpdatedOptions.Length; i++)
        {
            sheet.Cell(i + 1, 2).Value = ReviewActionOptions.MomentusUpdatedOptions[i];
        }

        return sheet;
    }

    private static void AddReadmeSheet(XLWorkbook workbook, WorkbookRunInfo runInfo)
    {
        var sheet = workbook.Worksheets.Add("README");
        sheet.Cell("A1").Value = "Stale Momentus Account Report";
        sheet.Cell("A2").Value = "Generated";
        sheet.Cell("B2").Value = runInfo.RunDate.ToDateTime(TimeOnly.MinValue);
        sheet.Cell("B2").Style.DateFormat.Format = "yyyy-mm-dd";
        sheet.Cell("A3").Value = "Stale Activity Cutoff";
        sheet.Cell("B3").Value = runInfo.StaleActivityCutoffDate.ToDateTime(TimeOnly.MinValue);
        sheet.Cell("B3").Style.DateFormat.Format = "yyyy-mm-dd";
        sheet.Cell("A4").Value = "Account Age Cutoff";
        sheet.Cell("B4").Value = runInfo.AccountAgeCutoffDate.ToDateTime(TimeOnly.MinValue);
        sheet.Cell("B4").Style.DateFormat.Format = "yyyy-mm-dd";
        sheet.Cell("A5").Value = "Org Code";
        sheet.Cell("B5").Value = string.IsNullOrWhiteSpace(runInfo.OrganizationCode) ? "(dry run)" : runInfo.OrganizationCode;
        sheet.Cell("A6").Value = "Mode";
        sheet.Cell("B6").Value = runInfo.IsDryRun ? "Dry run sample data" : "Momentus API";

        sheet.Cell("A7").Value = "Tabs";
        sheet.Cell("A8").Value = "Phase 1 Exhibitors";
        sheet.Cell("B8").Value = "Accounts older than the account-age cutoff whose latest exhibitor Entered On date is older than the stale activity cutoff or missing.";
        sheet.Cell("A9").Value = "Phase 2 Service Orders";
        sheet.Cell("B9").Value = "Active accounts not already listed in phase 1 whose latest service order date is older than the stale activity cutoff or missing.";

        sheet.Cell("A12").Value = "Editable Cleanup Columns";
        sheet.Cell("A13").Value = "Review Status";
        sheet.Cell("B13").Value = "Use the dropdown to choose the cleanup decision.";
        sheet.Cell("A14").Value = "Assigned To";
        sheet.Cell("B14").Value = "Person responsible for researching or correcting this account.";
        sheet.Cell("A15").Value = "Reviewed Date";
        sheet.Cell("B15").Value = "Date the account was reviewed.";
        sheet.Cell("A16").Value = "Correction Notes";
        sheet.Cell("B16").Value = "Free-form notes for later account cleanup.";
        sheet.Cell("A17").Value = "Target Merge Account";
        sheet.Cell("B17").Value = "Account code to merge into when Review Status is Merge.";
        sheet.Cell("A18").Value = "New Status";
        sheet.Cell("B18").Value = "Replacement status when Review Status is Convert Status.";
        sheet.Cell("A19").Value = "Momentus Updated?";
        sheet.Cell("B19").Value = "Use after the account correction has been made in Momentus.";

        sheet.Range("A1:B1").Merge();
        sheet.Cell("A1").Style.Font.Bold = true;
        sheet.Cell("A1").Style.Font.FontSize = 16;
        sheet.Range("A7:B7").Style.Font.Bold = true;
        sheet.Range("A12:B12").Style.Font.Bold = true;
        sheet.Columns().AdjustToContents(12, 70);
        sheet.SheetView.FreezeRows(1);
    }

    private static void AddSummarySheet(XLWorkbook workbook, IReadOnlyList<StaleAccountResult> results, WorkbookRunInfo runInfo)
    {
        var sheet = workbook.Worksheets.Add("Summary");
        sheet.Cell("A1").Value = "Stale Account Summary";
        sheet.Range("A1:D1").Merge();
        sheet.Cell("A1").Style.Font.Bold = true;
        sheet.Cell("A1").Style.Font.FontSize = 16;

        sheet.Cell("A3").Value = "Generated";
        sheet.Cell("B3").Value = runInfo.RunDate.ToDateTime(TimeOnly.MinValue);
        sheet.Cell("B3").Style.DateFormat.Format = "yyyy-mm-dd";
        sheet.Cell("A4").Value = "Stale Activity Cutoff";
        sheet.Cell("B4").Value = runInfo.StaleActivityCutoffDate.ToDateTime(TimeOnly.MinValue);
        sheet.Cell("B4").Style.DateFormat.Format = "yyyy-mm-dd";
        sheet.Cell("A5").Value = "Account Age Cutoff";
        sheet.Cell("B5").Value = runInfo.AccountAgeCutoffDate.ToDateTime(TimeOnly.MinValue);
        sheet.Cell("B5").Style.DateFormat.Format = "yyyy-mm-dd";
        sheet.Cell("A6").Value = "Total Stale Rows";
        sheet.Cell("B6").Value = results.Count;

        sheet.Cell("A7").Value = "Bucket";
        sheet.Cell("B7").Value = "Stale Rows";
        sheet.Cell("C7").Value = "Needs Review";
        sheet.Cell("D7").Value = "Marked Updated";

        var row = 8;
        foreach (var bucket in Enum.GetValues<StaleBucket>())
        {
            var sheetName = bucket.SheetName();
            sheet.Cell(row, 1).Value = sheetName;
            sheet.Cell(row, 2).Value = results.Count(result => result.Bucket == bucket);
            sheet.Cell(row, 3).FormulaA1 = $"=COUNTIF('{sheetName}'!N:N,\"Needs Review\")";
            sheet.Cell(row, 4).FormulaA1 = $"=COUNTIF('{sheetName}'!U:U,\"Yes\")";
            row++;
        }

        sheet.Cell(row + 1, 1).Value = "Review Status";
        sheet.Cell(row + 1, 2).Value = "Rows";
        var statusRow = row + 2;
        foreach (var status in ReviewActionOptions.ReviewStatuses)
        {
            sheet.Cell(statusRow, 1).Value = status;
            sheet.Cell(statusRow, 2).FormulaA1 = string.Join("+", Enum.GetValues<StaleBucket>()
                .Select(bucket => $"COUNTIF('{bucket.SheetName()}'!N:N,A{statusRow})"));
            statusRow++;
        }

        StyleHeader(sheet.Range("A7:D7"));
        StyleHeader(sheet.Range(row + 1, 1, row + 1, 2));
        sheet.Columns().AdjustToContents(12, 45);
        sheet.SheetView.FreezeRows(7);
    }

    private static void AddStaleBucketSheet(
        XLWorkbook workbook,
        StaleBucket bucket,
        IReadOnlyList<StaleAccountResult> results,
        IXLWorksheet listsSheet)
    {
        var sheet = workbook.Worksheets.Add(bucket.SheetName());
        for (var col = 0; col < Headers.Length; col++)
        {
            sheet.Cell(1, col + 1).Value = Headers[col];
        }

        var row = 2;
        foreach (var result in results)
        {
            var account = result.Account;
            sheet.Cell(row, 1).Value = account.AccountCode;
            sheet.Cell(row, 2).Value = account.AccountName;
            SetDate(sheet.Cell(row, 3), account.EnteredOn);
            sheet.Cell(row, 4).Value = account.StatusCode;
            sheet.Cell(row, 5).Value = account.TypeCode;
            sheet.Cell(row, 6).Value = account.ClassCode;
            sheet.Cell(row, 7).Value = account.ActiveContactCount;
            sheet.Cell(row, 8).Value = account.Activity.TotalServiceOrderCount;
            SetDate(sheet.Cell(row, 9), account.Activity.LatestServiceOrderDate);
            sheet.Cell(row, 10).Value = account.Activity.TotalExhibitorCount;
            SetDate(sheet.Cell(row, 11), account.Activity.LatestExhibitorEnteredOnDate);
            sheet.Cell(row, 12).Value = result.Priority;
            sheet.Cell(row, 13).Value = result.StaleReason;
            sheet.Cell(row, 14).Value = "Needs Review";
            sheet.Cell(row, 15).Value = result.RecommendedAction;
            sheet.Cell(row, 21).Value = "No";
            row++;
        }

        var lastRow = Math.Max(2, row - 1);
        var lastCol = Headers.Length;
        var tableRange = sheet.Range(1, 1, lastRow, lastCol);
        var table = tableRange.CreateTable(SanitizeTableName(bucket.SheetName()));
        table.Theme = XLTableTheme.TableStyleMedium2;
        table.ShowAutoFilter = true;

        StyleHeader(sheet.Range(1, 1, 1, lastCol));
        sheet.SheetView.FreezeRows(1);
        sheet.Range(2, 3, Math.Max(lastRow, 201), 3).Style.DateFormat.Format = "yyyy-mm-dd";
        sheet.Range(2, 9, Math.Max(lastRow, 201), 9).Style.DateFormat.Format = "yyyy-mm-dd";
        sheet.Range(2, 11, Math.Max(lastRow, 201), 11).Style.DateFormat.Format = "yyyy-mm-dd";
        sheet.Range(2, 17, Math.Max(lastRow, 201), 17).Style.DateFormat.Format = "yyyy-mm-dd";

        var validationLastRow = Math.Max(lastRow + 200, 201);
        var reviewValidation = sheet.Range(2, 14, validationLastRow, 14).CreateDataValidation();
        reviewValidation.List(listsSheet.Range(1, 1, ReviewActionOptions.ReviewStatuses.Length, 1));
        var updatedValidation = sheet.Range(2, 21, validationLastRow, 21).CreateDataValidation();
        updatedValidation.List(listsSheet.Range(1, 2, ReviewActionOptions.MomentusUpdatedOptions.Length, 2));

        sheet.Range(2, 12, validationLastRow, 12)
            .AddConditionalFormat()
            .WhenEquals("High")
            .Fill.SetBackgroundColor(XLColor.FromHtml("#F8CBAD"));

        sheet.Range(2, 14, validationLastRow, 14)
            .AddConditionalFormat()
            .WhenEquals("Needs Review")
            .Fill.SetBackgroundColor(XLColor.FromHtml("#FFF2CC"));

        sheet.Columns(1, lastCol).AdjustToContents(12, 55);
        sheet.Column(13).Width = 48;
        sheet.Column(15).Width = 42;
        sheet.Column(18).Width = 48;
        sheet.Columns(13, 18).Style.Alignment.WrapText = true;
    }

    private static void SetDate(IXLCell cell, DateOnly? date)
    {
        if (!date.HasValue)
        {
            cell.Value = "";
            return;
        }

        cell.Value = date.Value.ToDateTime(TimeOnly.MinValue);
        cell.Style.DateFormat.Format = "yyyy-mm-dd";
    }

    private static void StyleHeader(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E78");
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    private static string SanitizeTableName(string name)
    {
        var valid = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(valid) ? "StaleAccounts" : valid;
    }
}
