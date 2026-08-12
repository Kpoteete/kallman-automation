namespace StaleMomentusAccountReport.Reports;

public sealed record WorkbookRunInfo(
    DateOnly RunDate,
    DateOnly AccountAgeCutoffDate,
    DateOnly StaleActivityCutoffDate,
    bool IsDryRun,
    string OrganizationCode);
