namespace StaleMomentusAccountReport.Models;

public sealed record StaleAccountResult
{
    public required StaleBucket Bucket { get; init; }
    public required AccountSnapshot Account { get; init; }
    public required string StaleReason { get; init; }
    public required string RecommendedAction { get; init; }
    public required string Priority { get; init; }
}

public enum StaleBucket
{
    Phase1OldAccountsExhibitors,
    Phase2ActiveAccountsServiceOrders
}

public static class StaleBucketExtensions
{
    public static string SheetName(this StaleBucket bucket) =>
        bucket switch
        {
            StaleBucket.Phase1OldAccountsExhibitors => "Phase 1 Exhibitors",
            StaleBucket.Phase2ActiveAccountsServiceOrders => "Phase 2 Service Orders",
            _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null)
        };

    public static string DisplayName(this StaleBucket bucket) =>
        bucket switch
        {
            StaleBucket.Phase1OldAccountsExhibitors => "Accounts older than 5 years with no exhibitor Entered On date inside the stale activity window",
            StaleBucket.Phase2ActiveAccountsServiceOrders => "Active accounts not already flagged in phase 1 with no service order inside the stale activity window",
            _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null)
        };
}
