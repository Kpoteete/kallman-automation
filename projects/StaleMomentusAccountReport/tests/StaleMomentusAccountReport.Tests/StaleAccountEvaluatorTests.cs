using StaleMomentusAccountReport.Configuration;
using StaleMomentusAccountReport.Models;
using StaleMomentusAccountReport.Rules;

namespace StaleMomentusAccountReport.Tests;

public sealed class StaleAccountEvaluatorTests
{
    private static readonly DateOnly AccountAgeCutoff = new(2021, 6, 29);
    private static readonly DateOnly StaleActivityCutoff = new(2020, 5, 23);

    [Fact]
    public void Phase1IncludesOldAccountWithNoExhibitorEnteredOn()
    {
        var result = Evaluate(Account(
            "A1",
            enteredOn: AccountAgeCutoff,
            status: "INACTIVE"));

        Assert.Contains(result, row => row.Bucket == StaleBucket.Phase1OldAccountsExhibitors);
    }

    [Fact]
    public void Phase1IncludesOldAccountWithOldLatestExhibitorEnteredOn()
    {
        var result = Evaluate(Account(
            "A1",
            enteredOn: AccountAgeCutoff.AddDays(-1),
            status: "ACTIVE",
            exhibitors: 2,
            latestExhibitorEnteredOn: StaleActivityCutoff.AddDays(-1)));

        Assert.Contains(result, row => row.Bucket == StaleBucket.Phase1OldAccountsExhibitors);
    }

    [Fact]
    public void Phase1ExcludesOldAccountWithExhibitorEnteredOnAtCutoff()
    {
        var result = Evaluate(Account(
            "A1",
            enteredOn: AccountAgeCutoff.AddDays(-1),
            status: "ACTIVE",
            exhibitors: 1,
            latestExhibitorEnteredOn: StaleActivityCutoff));

        Assert.DoesNotContain(result, row => row.Bucket == StaleBucket.Phase1OldAccountsExhibitors);
    }

    [Fact]
    public void Phase2IncludesActiveAccountWithOldLatestServiceOrder()
    {
        var result = Evaluate(Account(
            "A1",
            enteredOn: AccountAgeCutoff.AddDays(10),
            status: "ACTIVE",
            serviceOrders: 1,
            latestServiceOrderDate: StaleActivityCutoff.AddDays(-1)));

        Assert.Contains(result, row => row.Bucket == StaleBucket.Phase2ActiveAccountsServiceOrders);
    }

    [Fact]
    public void Phase2ExcludesActiveAccountWithServiceOrderAtCutoff()
    {
        var result = Evaluate(Account(
            "A1",
            enteredOn: AccountAgeCutoff.AddDays(10),
            status: "ACTIVE",
            serviceOrders: 1,
            latestServiceOrderDate: StaleActivityCutoff));

        Assert.DoesNotContain(result, row => row.Bucket == StaleBucket.Phase2ActiveAccountsServiceOrders);
    }

    [Fact]
    public void Phase2DoesNotIncludeInactiveAccounts()
    {
        var result = Evaluate(Account(
            "A1",
            enteredOn: AccountAgeCutoff.AddDays(10),
            status: "INACTIVE",
            serviceOrders: 0));

        Assert.DoesNotContain(result, row => row.Bucket == StaleBucket.Phase2ActiveAccountsServiceOrders);
    }

    [Fact]
    public void Phase2ExcludesAccountsAlreadyFlaggedInPhase1()
    {
        var result = Evaluate(Account(
            "A1",
            enteredOn: AccountAgeCutoff.AddDays(-1),
            status: "ACTIVE",
            serviceOrders: 0,
            exhibitors: 0));

        Assert.Single(result);
        Assert.Equal(StaleBucket.Phase1OldAccountsExhibitors, result[0].Bucket);
    }

    private static IReadOnlyList<StaleAccountResult> Evaluate(AccountSnapshot account) =>
        new StaleAccountEvaluator(Config()).Evaluate([account], AccountAgeCutoff, StaleActivityCutoff).ToList();

    private static AccountSnapshot Account(
        string code,
        DateOnly enteredOn,
        string status,
        int serviceOrders = 0,
        DateOnly? latestServiceOrderDate = null,
        int exhibitors = 0,
        DateOnly? latestExhibitorEnteredOn = null) =>
        new()
        {
            AccountCode = code,
            AccountName = code,
            EnteredOn = enteredOn,
            StatusCode = status,
            TypeCode = "COMPANY",
            ActiveContactCount = 0,
            Activity = new ActivitySummary
            {
                TotalServiceOrderCount = serviceOrders,
                LatestServiceOrderDate = latestServiceOrderDate,
                TotalExhibitorCount = exhibitors,
                LatestExhibitorEnteredOnDate = latestExhibitorEnteredOn
            }
        };

    private static ReportConfig Config() =>
        new()
        {
            AccountMappings = new AccountMappings
            {
                ActiveStatusCodes = ["ACTIVE"]
            }
        };
}
