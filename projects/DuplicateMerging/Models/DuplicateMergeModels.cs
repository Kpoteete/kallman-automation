namespace DuplicateMerging.Models;

public sealed record DuplicateAccountCandidate(
    MomentusAccountSnapshot Account,
    IReadOnlyList<MomentusContactSnapshot> Contacts,
    IReadOnlyList<string> InterestCodes)
{
    public int ContactCount => Contacts.Count;
    public int FieldCompletenessScore =>
        CountNonBlank(Account.Name, Account.Website, Account.Country, Account.Email, Account.Phone, Account.MarketSegmentMajor, Account.MarketSegmentMinor);
    public int ActiveStatusScore => string.Equals(Account.EventSalesStatus, "A", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    private static int CountNonBlank(params string[] values) => values.Count(value => !string.IsNullOrWhiteSpace(value));
}

public sealed record DuplicateAccountGroup(
    string MatchKey,
    string MatchRule,
    string NormalizedName,
    string WebsiteRootDomain,
    string Country,
    DuplicateAccountCandidate Survivor,
    IReadOnlyList<DuplicateAccountCandidate> Duplicates);

public sealed record DuplicateMergeActionRow(
    string Action,
    string Status,
    string MatchKey,
    string SurvivorAccountCode,
    string SurvivorName,
    string DuplicateAccountCode,
    string DuplicateName,
    string ContactAccountCode,
    string ContactName,
    string ContactEmail,
    string Message);
