using ClosedXML.Excel;
using StaleMomentusAccountReport.Models;
using StaleMomentusAccountReport.Reports;

namespace StaleMomentusAccountReport.Tests;

public sealed class StaleAccountWorkbookWriterTests
{
    [Fact]
    public void WorkbookContainsReviewColumnsDropdownsSummaryAndReadme()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stale-report-{Guid.NewGuid():N}.xlsx");
        var results = new List<StaleAccountResult>
        {
            Result(StaleBucket.Phase1OldAccountsExhibitors, "P1"),
            Result(StaleBucket.Phase2ActiveAccountsServiceOrders, "O1")
        };

        try
        {
            new StaleAccountWorkbookWriter().Write(
                path,
                results,
                new WorkbookRunInfo(new DateOnly(2026, 6, 29), new DateOnly(2021, 6, 29), new DateOnly(2020, 5, 23), true, "10"));

            using var workbook = new XLWorkbook(path);
            Assert.Contains("README", workbook.Worksheets.Select(sheet => sheet.Name));
            Assert.Contains("Summary", workbook.Worksheets.Select(sheet => sheet.Name));

            var sheet = workbook.Worksheet("Phase 1 Exhibitors");
            var headers = sheet.Row(1).Cells(1, 21).Select(cell => cell.GetString()).ToArray();
            Assert.Contains("Account Entered On", headers);
            Assert.Contains("Latest Exhibitor Entered On", headers);
            Assert.Contains("Review Status", headers);
            Assert.Contains("Recommended Action", headers);
            Assert.Contains("Assigned To", headers);
            Assert.Contains("Reviewed Date", headers);
            Assert.Contains("Correction Notes", headers);
            Assert.Contains("Target Merge Account", headers);
            Assert.Contains("New Status", headers);
            Assert.Contains("Momentus Updated?", headers);

            Assert.Equal("Needs Review", sheet.Cell("N2").GetString());
            Assert.Equal("No", sheet.Cell("U2").GetString());
            Assert.True(sheet.Tables.Any());
            Assert.Contains(sheet.DataValidations, validation => validation.Ranges.Any(range => range.RangeAddress.ToStringRelative().Contains("N2")));
            Assert.Contains(sheet.DataValidations, validation => validation.Ranges.Any(range => range.RangeAddress.ToStringRelative().Contains("U2")));

            var summary = workbook.Worksheet("Summary");
            Assert.Equal("Stale Account Summary", summary.Cell("A1").GetString());
            Assert.Contains("COUNTIF", summary.Cell("C8").FormulaA1);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static StaleAccountResult Result(StaleBucket bucket, string code) =>
        new()
        {
            Bucket = bucket,
            Account = new AccountSnapshot
            {
                AccountCode = code,
                AccountName = $"Account {code}",
            StatusCode = "ACTIVE",
            TypeCode = "COMPANY",
            ClassCode = "C",
            EnteredOn = new DateOnly(2018, 1, 1),
            ActiveContactCount = 1,
                Activity = new ActivitySummary
                {
                    TotalServiceOrderCount = 0,
                    TotalExhibitorCount = 0
                }
            },
            StaleReason = "Test stale reason",
            RecommendedAction = "Test recommended action",
            Priority = "High"
        };
}
