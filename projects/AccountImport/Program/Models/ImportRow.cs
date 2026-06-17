namespace AccountImport.Models;

public sealed record ImportRow(
    int WorksheetRowNumber,
    int SourceRowNumber,
    string SourceFileName,
    string CompanyName,
    string AccountCode,
    string MarketSegmentMajor,
    string Country,
    string WebsiteRootDomain,
    string ContactEmail)
{
    public string NormalizedEmail => TextUtil.NormalizeEmail(ContactEmail);

    public bool HasRequiredAccountKeyFields(AppConfig config) =>
        !string.IsNullOrWhiteSpace(TextUtil.CleanKeyField(CompanyName)) &&
        !string.IsNullOrWhiteSpace(TextUtil.CleanKeyField(Country)) &&
        (!config.DuplicateCheck.RequireMarketSegmentForAccountMatch ||
         !string.IsNullOrWhiteSpace(TextUtil.CleanKeyField(MarketSegmentMajor)));
}
