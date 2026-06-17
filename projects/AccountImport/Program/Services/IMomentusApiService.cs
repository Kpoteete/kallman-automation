using AccountImport.Models;

namespace AccountImport.Services;

public interface IMomentusApiService : IDisposable
{
    Task EnsureConnectionAsync(CancellationToken cancellationToken);
    Task<ContactLookupResult> FindContactByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);
    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken);
    Task<AccountLookupResult> FindOrganizationAccountAsync(string companyName, string marketSegmentMajor, string country, string websiteRootDomain, CancellationToken cancellationToken);
    Task<ApiWriteResult> CreateOrganizationAccountAsync(string companyName, string marketSegmentMajor, string country, IReadOnlyDictionary<string, string> accountFields, CancellationToken cancellationToken);
    Task<ApiWriteResult> UpdateBlankOrganizationAccountFieldsAsync(string accountCode, IReadOnlyDictionary<string, string> accountFields, CancellationToken cancellationToken);
    Task<ApiWriteResult> CreateContactAsync(string parentAccountCode, string parentCompanyName, IReadOnlyDictionary<string, string> contactFields, CancellationToken cancellationToken);
    Task<ApiWriteResult> CreateRelationshipAsync(string companyAccountCode, string contactAccountCode, CancellationToken cancellationToken);
    Task<ApiWriteResult> AddAccountAffiliationAsync(string accountCode, string affiliationCode, CancellationToken cancellationToken);
    Task<ApiWriteResult> ApplyRunTagToAccountAsync(string accountCode, bool isOrganizationAccount, CancellationToken cancellationToken);
    Task<ApiWriteResult> ApplyImportIdToAccountAsync(string accountCode, CancellationToken cancellationToken);
}
