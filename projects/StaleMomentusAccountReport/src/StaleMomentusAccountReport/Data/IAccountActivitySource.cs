using StaleMomentusAccountReport.Models;

namespace StaleMomentusAccountReport.Data;

public interface IAccountActivitySource
{
    Task<IReadOnlyList<AccountSnapshot>> GetAccountSnapshotsAsync(CancellationToken cancellationToken);
}
