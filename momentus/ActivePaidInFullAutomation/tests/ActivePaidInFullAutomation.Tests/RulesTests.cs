using ActivePaidInFullAutomation;
using ClosedXML.Excel;
using Xunit;

namespace ActivePaidInFullAutomation.Tests;

public class RulesTests
{
    public static IEnumerable<object[]> EligibleCategorySequences() => QualificationRules.EligibleCategories.Keys.Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(EligibleCategorySequences))]
    public void Every_approved_category_qualifies_at_zero_net_due(int category) =>
        Assert.True(QualificationRules.Evaluate(2, "ME", "A", category, 100m, 0m).Qualifies);

    [Fact] public void Negative_net_due_qualifies() => Assert.True(QualificationRules.Evaluate(2, "ME", "A", 27, 100m, -500m).Qualifies);

    [Theory]
    [InlineData(22, "A", 27, 100, 0)]
    [InlineData(2, "C", 27, 100, 0)]
    [InlineData(2, "A", 999, 100, 0)]
    [InlineData(2, "A", 27, 100, .01)]
    [InlineData(2, "A", 27, 0, 0)]
    [InlineData(2, "A", 27, -1, 0)]
    public void Does_not_qualify_outside_exact_gates(int status, string orderStatus, int category, decimal orderedTotal, decimal netDue) =>
        Assert.False(QualificationRules.Evaluate(status, "ME", orderStatus, category, orderedTotal, netDue).Qualifies);

    [Fact] public void Co_exhibitor_never_qualifies() => Assert.False(QualificationRules.Evaluate(2, "CO", "A", 21, 100m, 0m).Qualifies);
    [Fact] public void Payment_amount_does_not_participate_in_rule() => Assert.True(QualificationRules.Evaluate(2, "ME", "A", 19, 100m, 0m).Qualifies);
    [Fact] public void Apply_limit_defaults_to_25() => Assert.Equal(25, CliOptions.Parse(["apply", "--confirm-active-paid-in-full"]).MaxUpdates);
    [Fact] public void Apply_limit_can_be_overridden() => Assert.Equal(100, CliOptions.Parse(["apply", "--confirm-active-paid-in-full", "--max-updates", "100"]).MaxUpdates);
    [Fact] public void Category_filter_contains_all_19_sequences() { var filter = QualificationRules.ODataCategoryFilter(); Assert.Equal(19, QualificationRules.EligibleCategories.Count); Assert.All(QualificationRules.EligibleCategories.Keys, x => Assert.Contains($"Category eq {x}", filter)); }
    [Fact] public void Saturday_starts_new_reporting_week() { var x = ReportingWeek.For(new DateTime(2026, 10, 3)); Assert.Equal(new DateOnly(2026, 10, 3), x.Start); Assert.Equal(new DateOnly(2026, 10, 9), x.End); }
    [Fact] public void Friday_belongs_to_prior_Saturday() { var x = ReportingWeek.For(new DateTime(2026, 10, 2)); Assert.Equal(new DateOnly(2026, 9, 26), x.Start); Assert.Equal(new DateOnly(2026, 10, 2), x.End); }
    [Fact] public void Paid_booth_order_wins_when_other_order_is_unpaid() { OrderPriorityInput[] orders = [new(1, "A", "Y", 27, 100m, 0m), new(2, "A", "N", 22, 100m, 50m)]; Assert.Equal(1, OrderPriorityRules.SelectQualifyingOrder(orders)); }
    [Fact] public void Unpaid_booth_order_blocks_paid_other_order() { OrderPriorityInput[] orders = [new(1, "A", "Y", 27, 100m, 50m), new(2, "A", "N", 22, 100m, 0m)]; Assert.Null(OrderPriorityRules.SelectQualifyingOrder(orders)); }
    [Fact] public void Paid_other_order_qualifies_when_no_booth_order_exists() { OrderPriorityInput[] orders = [new(2, "A", "N", 22, 100m, 0m)]; Assert.Equal(2, OrderPriorityRules.SelectQualifyingOrder(orders)); }
    [Fact] public void Cancelled_booth_order_does_not_block_active_paid_other_order() { OrderPriorityInput[] orders = [new(1, "C", "Y", 27, 100m, 50m), new(2, "A", "N", 22, 100m, 0m)]; Assert.Equal(2, OrderPriorityRules.SelectQualifyingOrder(orders)); }

    [Fact]
    public void Weekly_report_contains_category_and_financial_fields()
    {
        var root = Path.Combine(Path.GetTempPath(), "paid-in-full-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WeeklyReportStore(Path.Combine(root, "reports"), Path.Combine(root, "state"));
            var row = new ReportRow(new DateTime(2026, 10, 2, 15, 30, 0), "Apply", "Updated", 6208, "Test Event", 123,
                "Example Exhibitor", "A100", "B-12", 40001, "27", "Space Only", 100000m, 50000m, 0m, "2", "22", "Verified.");
            var path = store.Record(row);
            Assert.NotNull(path); using var workbook = new XLWorkbook(path); var sheet = workbook.Worksheet("Ready to Register");
            Assert.Single(workbook.Worksheets); Assert.Equal("Example Exhibitor", sheet.Cell("E6").GetString());
            Assert.Equal("27", sheet.Cell("K6").GetString()); Assert.Equal("Space Only", sheet.Cell("L6").GetString()); Assert.Equal(0m, sheet.Cell("O6").GetValue<decimal>());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
