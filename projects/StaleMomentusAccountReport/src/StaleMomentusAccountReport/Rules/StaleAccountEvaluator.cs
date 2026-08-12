using StaleMomentusAccountReport.Configuration;
using StaleMomentusAccountReport.Models;

namespace StaleMomentusAccountReport.Rules;

public sealed class StaleAccountEvaluator
{
    private readonly ReportConfig config;

    public StaleAccountEvaluator(ReportConfig config)
    {
        this.config = config;
    }

    public IEnumerable<StaleAccountResult> Evaluate(
        IEnumerable<AccountSnapshot> accounts,
        DateOnly accountAgeCutoffDate,
        DateOnly staleActivityCutoffDate)
    {
        var accountList = accounts.ToList();
        var phase1StaleAccountCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in accountList)
        {
            if (IsOlderThanOrEqual(account.EnteredOn, accountAgeCutoffDate) &&
                !IsRecent(account.Activity.LatestExhibitorEnteredOnDate, staleActivityCutoffDate))
            {
                phase1StaleAccountCodes.Add(account.AccountCode);
                yield return new StaleAccountResult
                {
                    Bucket = StaleBucket.Phase1OldAccountsExhibitors,
                    Account = account,
                    StaleReason = $"Account entered on {FormatDate(account.EnteredOn)} is older than the account-age cutoff {accountAgeCutoffDate:yyyy-MM-dd}, and latest exhibitor Entered On is not on or after {staleActivityCutoffDate:yyyy-MM-dd}.",
                    RecommendedAction = "Review exhibitor history, then keep active, merge, deactivate, or convert status.",
                    Priority = account.Activity.TotalExhibitorCount == 0 ? "High" : "Medium"
                };
            }
        }

        foreach (var account in accountList)
        {
            var isActive = ContainsCode(config.AccountMappings.ActiveStatusCodes, account.StatusCode);

            if (isActive &&
                !phase1StaleAccountCodes.Contains(account.AccountCode) &&
                !IsRecent(account.Activity.LatestServiceOrderDate, staleActivityCutoffDate))
            {
                yield return new StaleAccountResult
                {
                    Bucket = StaleBucket.Phase2ActiveAccountsServiceOrders,
                    Account = account,
                    StaleReason = $"Active account was not flagged in phase 1, and latest service order is not on or after {staleActivityCutoffDate:yyyy-MM-dd}.",
                    RecommendedAction = "Review service-order history, then keep active, merge, deactivate, or convert status.",
                    Priority = account.Activity.TotalServiceOrderCount == 0 ? "High" : "Medium"
                };
            }
        }
    }

    private static bool IsRecent(DateOnly? activityDate, DateOnly cutoffDate) =>
        activityDate.HasValue && activityDate.Value >= cutoffDate;

    private static bool IsOlderThanOrEqual(DateOnly? enteredOn, DateOnly cutoffDate) =>
        enteredOn.HasValue && enteredOn.Value <= cutoffDate;

    private static string FormatDate(DateOnly? date) =>
        date.HasValue ? date.Value.ToString("yyyy-MM-dd") : "unknown";

    private static bool ContainsCode(IEnumerable<string> codes, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return codes.Any(code => string.Equals(code.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
