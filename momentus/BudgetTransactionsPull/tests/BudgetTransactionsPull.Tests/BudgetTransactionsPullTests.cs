using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace BudgetTransactionsPull.Tests;

public sealed class BudgetTransactionsPullTests
{
    [Fact]
    public void CliDefaultsToSafeFullSettings()
    {
        var options = CliOptions.Parse(["full"]);

        Assert.Equal(RunMode.Full, options.Mode);
        Assert.Equal(2000, options.MaxRowsPerWindow);
        Assert.Equal(100_000, options.DenseWindowMaxRows);
        Assert.Equal(250, options.PageSize);
        Assert.Equal(48, options.OverlapHours);
    }

    [Fact]
    public void CliRejectsReverseRange()
    {
        Assert.Throws<CliException>(() => CliOptions.Parse(
            ["probe", "--start", "2026-07-22", "--end", "2026-07-21"]));
    }

    [Fact]
    public void KeyUsesSupportedGetIdentity()
    {
        var row = new string[BudgetTransactionSchema.Columns.Length];
        Array.Fill(row, "");
        row[BudgetTransactionSchema.IndexOf("OrganizationCode")] = "10";
        row[BudgetTransactionSchema.IndexOf("Batch")] = "000000008";
        row[BudgetTransactionSchema.IndexOf("TransactionNum")] = "1";

        Assert.Equal("10|000000008|1", BudgetTransactionSchema.Key(row));
    }

    [Fact]
    public void SchemaContainsEveryDocumentedBudgetTransactionField()
    {
        string[] expected =
        [
            "OrganizationCode", "Batch", "TransactionNum", "Year", "Period", "EntryStatus",
            "TransactionDescription", "GLAccount", "GLSubAccount", "EventJob", "Space", "GLAccountMasked",
            "BudgetAmountTo", "BudgetQuantityTo", "EnteredOn", "EnteredBy", "ChangedOn", "ChangedBy",
            "BatchDetailType", "BudgetAmountFrom", "BudgetAmtChg", "BudgetQuantityFrom", "BudgetUnitsChg",
            "RevisedAmountFrom", "RevisedAmountTo", "RevisedAmountDifference", "RevisedQuantityFrom",
            "RevisedQuantityTo", "RevisedQuantityDifference", "YearPeriod", "BatchDetailStatusWeight"
        ];

        Assert.All(expected, column => Assert.Contains(column, BudgetTransactionSchema.Columns));
    }

    [Fact]
    public void AdaptivePullerSplitsLargeWindowsAndPreservesAllRows()
    {
        var source = new FakeSource();
        var options = CliOptions.Parse(["full", "--max-rows", "2", "--request-delay-ms", "0"]);
        var puller = new AdaptiveBudgetTransactionPuller(source, options);
        var accepted = new List<int>();

        puller.Pull(BudgetTransactionDateField.EnteredOn, new DateTime(2026, 1, 1), new DateTime(2026, 1, 5),
            (_, _, rows) => accepted.Add(rows.Count));

        Assert.Equal(4, accepted.Sum());
        Assert.All(accepted, count => Assert.InRange(count, 0, 2));
        Assert.True(source.Calls > 2);
    }

    private sealed class FakeSource : IBudgetTransactionSource
    {
        public int Calls { get; private set; }

        public BudgetTransactionSearchResult Search(
            BudgetTransactionDateField field,
            DateTime start,
            DateTime end,
            int maxResults)
        {
            Calls++;
            var total = (int)Math.Round((end - start).TotalDays, MidpointRounding.AwayFromZero);
            if (total > maxResults)
                throw new WindowTooLargeException(start, end, maxResults);
            var rows = Enumerable.Range(0, total).Select(i => new BudgetTransactionsModel
            {
                OrganizationCode = "10",
                Batch = "000000008",
                TransactionNum = i + 1
            }).ToList();
            return new BudgetTransactionSearchResult(rows, total);
        }
    }
}

