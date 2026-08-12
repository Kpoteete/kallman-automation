namespace MarketSegmentApplication.Models;

public sealed record SegmentMapping(
    string InterestCode,
    string MarketSegmentMajor,
    string MarketSegmentMinor,
    string MarketSegmentCombined,
    bool Skip);

public sealed record MomentusAccountSnapshot(
    string AccountCode,
    string Name,
    string Class,
    string AccountStatus,
    string EventSalesStatus,
    string MarketSegmentMajor,
    string MarketSegmentMinor,
    string Country,
    string Website,
    string Email,
    string Phone);

public sealed record MomentusContactSnapshot(
    string AccountCode,
    string FirstName,
    string LastName,
    string Email,
    string PrimaryAccount,
    string Company);

public sealed record MomentusAuditAccountSnapshot(
    string AccountCode,
    string Name,
    string Class,
    string AccountStatus,
    string EventSalesStatus,
    string MarketSegmentMajor,
    string MarketSegmentMinor,
    string Country,
    string Website,
    string Email,
    string Phone,
    string PrimaryAccount,
    string Company,
    string FirstName,
    string LastName);

public sealed record ContactSegmentDecision(
    MomentusContactSnapshot Contact,
    IReadOnlyList<string> InterestCodes,
    IReadOnlyList<SegmentMapping> ValidSegments,
    IReadOnlyList<string> SkippedInterestCodes,
    IReadOnlyList<string> UnmappedInterestCodes);

public sealed record AccountSegmentationPlan(
    MomentusAccountSnapshot ParentAccount,
    IReadOnlyList<string> ParentInterestCodes,
    IReadOnlyList<ContactSegmentDecision> Contacts,
    IReadOnlyList<SegmentMapping> RequiredSegments,
    IReadOnlyList<string> ReviewReasons)
{
    public bool NeedsReview => ReviewReasons.Count > 0 || Contacts.Any(c => c.UnmappedInterestCodes.Count > 0);
    public bool HasClassifiedSegments => RequiredSegments.Count > 0;
    public bool NeedsMultipleMappedSegments => RequiredSegments.Count > 1;
    public bool NeedsSingleSegmentUpdate => RequiredSegments.Count == 1;
}

public sealed record PlanActionRow(
    string Action,
    string Status,
    string ParentAccountCode,
    string ParentAccountName,
    string ContactAccountCode,
    string ContactName,
    string ContactEmail,
    string InterestCodes,
    string MarketSegmentMajor,
    string MarketSegmentMinor,
    string MarketSegmentCombined,
    string TargetSegmentAccountCode,
    string Message);
