using DuplicateMerging.Models;

namespace DuplicateMerging.Services;

public interface IMomentusApiService : IDisposable
{
    Task EnsureConnectionAsync(CancellationToken cancellationToken);
    Task<ContactLookupResult> FindContactByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);
    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken);
    Task<IReadOnlyList<MomentusContactSnapshot>> SearchActiveContactsByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetCompanyRelationshipCodesForContactAsync(string contactAccountCode, CancellationToken cancellationToken);
    Task<AccountLookupResult> FindOrganizationAccountAsync(string companyName, string marketSegmentMajor, string country, string websiteRootDomain, CancellationToken cancellationToken);
    Task<ApiWriteResult> CreateOrganizationAccountAsync(string companyName, string marketSegmentMajor, string country, IReadOnlyDictionary<string, string> accountFields, CancellationToken cancellationToken);
    Task<ApiWriteResult> UpdateBlankOrganizationAccountFieldsAsync(string accountCode, IReadOnlyDictionary<string, string> accountFields, CancellationToken cancellationToken);
    Task<ApiWriteResult> CreateContactAsync(string parentAccountCode, string parentCompanyName, IReadOnlyDictionary<string, string> contactFields, CancellationToken cancellationToken);
    Task<ApiWriteResult> CreateRelationshipAsync(string companyAccountCode, string contactAccountCode, CancellationToken cancellationToken);
    Task<ApiWriteResult> AddAccountAffiliationAsync(string accountCode, string affiliationCode, CancellationToken cancellationToken);
    Task<ApiWriteResult> ApplyRunTagToAccountAsync(string accountCode, bool isOrganizationAccount, CancellationToken cancellationToken);
    Task<ApiWriteResult> ApplyImportIdToAccountAsync(string accountCode, CancellationToken cancellationToken);
    Task<IReadOnlyList<MomentusAccountSnapshot>> SearchActiveParentAccountsAsync(int maxResults, CancellationToken cancellationToken);
    Task<IReadOnlyList<MomentusAccountSnapshot>> SearchActiveParentAccountsByAccountCodeRangeAsync(string startAccountCode, string endAccountCode, int maxResults, CancellationToken cancellationToken);
    Task<IReadOnlyList<MomentusContactSnapshot>> GetContactsForParentAccountAsync(string parentAccountCode, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetAccountAffiliationCodesAsync(string accountCode, CancellationToken cancellationToken);
    Task<AccountLookupResult> FindSegmentAccountAsync(string companyName, string marketSegmentMajor, string marketSegmentMinor, string country, CancellationToken cancellationToken);
    Task<ApiWriteResult> UpdateAccountMarketSegmentAsync(string accountCode, string marketSegmentMajor, string marketSegmentMinor, CancellationToken cancellationToken);
    Task<ApiWriteResult> CreateSegmentAccountCopyAsync(string sourceAccountCode, string marketSegmentMajor, string marketSegmentMinor, CancellationToken cancellationToken);
    Task<bool> RelationshipExistsAsync(string companyAccountCode, string contactAccountCode, string relationshipType, CancellationToken cancellationToken);
    Task<ApiWriteResult> UpdateContactPrimaryAccountAsync(string contactAccountCode, string survivorAccountCode, string survivorCompanyName, CancellationToken cancellationToken);
    Task<ApiWriteResult> CopyBlankAccountFieldsAsync(string sourceAccountCode, string targetAccountCode, CancellationToken cancellationToken);
    Task<ApiWriteResult> UpdateAccountEventSalesStatusAsync(string accountCode, string eventSalesStatus, CancellationToken cancellationToken);
    Task<ApiWriteResult> DeleteRelationshipAsync(string companyAccountCode, string contactAccountCode, string relationshipType, CancellationToken cancellationToken);
    Task<ApiWriteResult> InactivateRelationshipAsync(string companyAccountCode, string contactAccountCode, string relationshipType, CancellationToken cancellationToken);
}
