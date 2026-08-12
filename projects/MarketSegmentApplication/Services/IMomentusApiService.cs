using MarketSegmentApplication.Models;

namespace MarketSegmentApplication.Services;

public interface IMomentusApiService : IDisposable
{
    Task<IReadOnlyList<MomentusAccountSnapshot>> SearchActiveParentAccountsAsync(int maxResults, CancellationToken cancellationToken);
    Task<IReadOnlyList<MomentusAuditAccountSnapshot>> SearchAuditAccountsByClassAsync(string classValue, int maxResults, CancellationToken cancellationToken);
    Task<MomentusAccountSnapshot?> GetAccountByCodeAsync(string accountCode, CancellationToken cancellationToken);
    Task<IReadOnlyList<MomentusContactSnapshot>> GetContactsForParentAccountAsync(string parentAccountCode, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetAccountAffiliationCodesAsync(string accountCode, CancellationToken cancellationToken);
    Task<ApiWriteResult> AddAccountAffiliationAsync(string accountCode, string affiliationCode, CancellationToken cancellationToken);
    Task<ApiWriteResult> UpdateAccountMarketSegmentAsync(string accountCode, string marketSegmentMajor, string marketSegmentMinor, CancellationToken cancellationToken);
}
