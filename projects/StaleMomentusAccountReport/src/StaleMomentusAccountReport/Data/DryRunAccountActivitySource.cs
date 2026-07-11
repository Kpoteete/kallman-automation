using StaleMomentusAccountReport.Models;

namespace StaleMomentusAccountReport.Data;

public sealed class DryRunAccountActivitySource : IAccountActivitySource
{
    public Task<IReadOnlyList<AccountSnapshot>> GetAccountSnapshotsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AccountSnapshot> accounts =
        [
            new()
            {
                AccountCode = "PROS-001",
                AccountName = "Sample Old Account With No Exhibitors",
                EnteredOn = new DateOnly(2012, 1, 10),
                StatusCode = "PROSPECT",
                TypeCode = "COMPANY",
                ClassCode = "C",
                ActiveContactCount = 3,
                Activity = new ActivitySummary
                {
                    TotalServiceOrderCount = 0,
                    TotalExhibitorCount = 0
                }
            },
            new()
            {
                AccountCode = "ORG-OLD",
                AccountName = "Sample Active Account With Old Service Orders",
                EnteredOn = new DateOnly(2024, 1, 10),
                StatusCode = "ACTIVE",
                TypeCode = "ORG",
                ClassCode = "O",
                ActiveContactCount = 8,
                Activity = new ActivitySummary
                {
                    TotalServiceOrderCount = 2,
                    LatestServiceOrderDate = new DateOnly(2018, 5, 1),
                    TotalExhibitorCount = 0
                }
            },
            new()
            {
                AccountCode = "COMP-NOEX",
                AccountName = "Sample Old Account With Old Exhibitor Entered On",
                EnteredOn = new DateOnly(2011, 2, 20),
                StatusCode = "ACTIVE",
                TypeCode = "COMPANY",
                ClassCode = "C",
                ActiveContactCount = 11,
                Activity = new ActivitySummary
                {
                    TotalServiceOrderCount = 6,
                    LatestServiceOrderDate = DateOnly.FromDateTime(DateTime.Today.AddMonths(-3)),
                    TotalExhibitorCount = 1,
                    LatestExhibitorEnteredOnDate = new DateOnly(2017, 10, 15)
                }
            },
            new()
            {
                AccountCode = "COMP-GOOD",
                AccountName = "Sample Active Company With Recent Activity",
                EnteredOn = new DateOnly(2010, 3, 20),
                StatusCode = "ACTIVE",
                TypeCode = "COMPANY",
                ClassCode = "C",
                ActiveContactCount = 6,
                Activity = new ActivitySummary
                {
                    TotalServiceOrderCount = 3,
                    LatestServiceOrderDate = DateOnly.FromDateTime(DateTime.Today.AddMonths(-2)),
                    TotalExhibitorCount = 2,
                    LatestExhibitorEnteredOnDate = DateOnly.FromDateTime(DateTime.Today.AddMonths(-1))
                }
            }
        ];

        return Task.FromResult(accounts);
    }
}
