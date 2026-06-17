namespace AccountImport.Models;

public sealed record AccountLookupResult(
    bool Found,
    string AccountCode,
    string CompanyName,
    string MarketSegmentMajor,
    string Country,
    string WebsiteRootDomain,
    string Message)
{
    public static AccountLookupResult NotFound(string message = "No exact organization account match found.") =>
        new(false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, message);
}


public sealed record ContactLookupResult(
    bool Found,
    string AccountCode,
    string Email,
    string FirstName,
    string LastName,
    string Message)
{
    public static ContactLookupResult NotFound(string message = "No existing Momentus contact found by email.") =>
        new(false, string.Empty, string.Empty, string.Empty, string.Empty, message);
}

public sealed record ApiWriteResult(
    bool Success,
    string AccountCode,
    string Message,
    string? ErrorMessage = null)
{
    public static ApiWriteResult Succeeded(string accountCode, string message) => new(true, accountCode, message);
    public static ApiWriteResult Failed(string errorMessage, string? message = null) => new(false, string.Empty, message ?? string.Empty, errorMessage);
}

public sealed class ImportSummary
{
    public int DuplicateContactsFound { get; set; }
    public int NonDuplicatesProcessed { get; set; }
    public int ExistingAccountsMatched { get; set; }
    public int NewAccountsCreated { get; set; }
    public int ExistingAccountBlankFieldUpdates { get; set; }
    public int ExistingAccountBlankFieldUpdateFailures { get; set; }
    public int ContactsCreated { get; set; }
    public int RelationshipsCreated { get; set; }
    public int RelationshipFailures { get; set; }
    public int AffiliationsAdded { get; set; }
    public int AffiliationFailures { get; set; }
    public int RunTagsApplied { get; set; }
    public int RunTagFailures { get; set; }
    public int Failures { get; set; }
    public int RowsSkipped { get; set; }
    public int DryRunAccountsPrepared { get; set; }
    public int DryRunContactsPrepared { get; set; }
}
