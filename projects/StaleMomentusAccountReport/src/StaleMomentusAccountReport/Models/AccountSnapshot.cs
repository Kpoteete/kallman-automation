namespace StaleMomentusAccountReport.Models;

public sealed record AccountSnapshot
{
    public required string AccountCode { get; init; }
    public required string AccountName { get; init; }
    public DateOnly? EnteredOn { get; init; }
    public string? StatusCode { get; init; }
    public string? TypeCode { get; init; }
    public string? ClassCode { get; init; }
    public string? CompanyAccountCode { get; init; }
    public int ActiveContactCount { get; init; }
    public ActivitySummary Activity { get; init; } = new();
}
