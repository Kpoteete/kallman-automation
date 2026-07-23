namespace StaleMomentusAccountReport.Models;

public sealed record ActivitySummary
{
    public int TotalServiceOrderCount { get; init; }
    public DateOnly? LatestServiceOrderDate { get; init; }
    public int TotalExhibitorCount { get; init; }
    public DateOnly? LatestExhibitorEnteredOnDate { get; init; }
}
