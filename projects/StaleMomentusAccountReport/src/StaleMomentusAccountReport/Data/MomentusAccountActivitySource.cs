using StaleMomentusAccountReport.Configuration;
using StaleMomentusAccountReport.Models;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Options;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace StaleMomentusAccountReport.Data;

public sealed class MomentusAccountActivitySource : IAccountActivitySource
{
    private readonly ReportConfig config;

    public MomentusAccountActivitySource(ReportConfig config)
    {
        this.config = config;
    }

    public Task<IReadOnlyList<AccountSnapshot>> GetAccountSnapshotsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var client = CreateClient();
        var accountModels = SearchAccounts(client);
        var snapshots = new List<AccountSnapshot>(accountModels.Count);

        foreach (var account in accountModels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(account.AccountCode))
            {
                continue;
            }

            var accountCode = account.AccountCode.Trim();
            var activeContacts = CountActiveContacts(client, accountCode);
            var serviceOrders = SearchServiceOrders(client, accountCode);
            var exhibitors = SearchExhibitors(client, accountCode);

            snapshots.Add(new AccountSnapshot
            {
                AccountCode = accountCode,
                AccountName = FirstNonBlank(account.Name, account.Search, account.LegalName, account.AccountCode),
                EnteredOn = ToDateOnly(account.EnteredOn),
                StatusCode = account.Status,
                TypeCode = account.Type,
                ClassCode = account.Class,
                CompanyAccountCode = account.Company,
                ActiveContactCount = activeContacts,
                Activity = new ActivitySummary
                {
                    TotalServiceOrderCount = serviceOrders.Count,
                    LatestServiceOrderDate = MaxDate(serviceOrders.Select(order => order.OrderDate)),
                    TotalExhibitorCount = exhibitors.Count,
                    LatestExhibitorEnteredOnDate = MaxDate(exhibitors.Select(exhibitor => exhibitor.EnteredOn))
                }
            });
        }

        return Task.FromResult<IReadOnlyList<AccountSnapshot>>(snapshots);
    }

    private ApiClient CreateClient()
    {
        var jwt = new Jwt
        {
            UngerboeckURI = config.Momentus.BaseUrl,
            APIUserID = config.Momentus.ApiUserId,
            Key = config.Momentus.ApiKey,
            Secret = config.Momentus.ApiSecret,
            ProxiedUserID = config.Momentus.ProxiedUserId ?? ""
        };

        return new ApiClient(jwt, new Initialization());
    }

    private List<AllAccountsModel> SearchAccounts(ApiClient client)
    {
        var accountAgeCutoff = DateTime.Today.AddDays(-decimal.ToDouble(config.Report.AccountAgeYears * 365.25m));
        var oldAccountFilter = $"{nameof(AllAccountsModel.EnteredOn)} le {FormatMomentusDate(accountAgeCutoff)}";
        var activeFilter = AnyEquals(nameof(AllAccountsModel.Status), config.AccountMappings.ActiveStatusCodes);
        var filter = Or(oldAccountFilter, activeFilter);
        return Search(() => client.Endpoints.Accounts.Search(config.Momentus.OrganizationCode, filter, SearchOptions()));
    }

    private int CountActiveContacts(ApiClient client, string companyAccountCode)
    {
        var filter = And(
            $"{nameof(AllAccountsModel.Company)} eq '{Escape(companyAccountCode)}'",
            AnyEquals(nameof(AllAccountsModel.Status), config.ContactMappings.ActiveStatusCodes));

        return Search(() => client.Endpoints.Accounts.Search(config.Momentus.OrganizationCode, filter, SearchOptions())).Count;
    }

    private List<ServiceOrdersModel> SearchServiceOrders(ApiClient client, string accountCode)
    {
        var filter = $"{nameof(ServiceOrdersModel.Account)} eq '{Escape(accountCode)}'";
        return Search(() => client.Endpoints.ServiceOrders.Search(config.Momentus.OrganizationCode, filter, SearchOptions()));
    }

    private List<ExhibitorsModel> SearchExhibitors(ApiClient client, string accountCode)
    {
        var filter = $"{nameof(ExhibitorsModel.AccountCode)} eq '{Escape(accountCode)}'";
        return Search(() => client.Endpoints.Exhibitors.Search(config.Momentus.OrganizationCode, filter, SearchOptions()));
    }

    private Search SearchOptions() =>
        new()
        {
            PageSize = config.Momentus.SearchPageSize,
            MaxResults = config.Momentus.SearchMaxResults
        };

    private static List<T> Search<T>(Func<SearchResponse<T>> search)
        where T : UngerboeckModel
    {
        var response = search();
        return response.Results?.ToList() ?? [];
    }

    private static DateOnly? MaxDate(IEnumerable<DateTime?> dates) =>
        MaxDate(dates.ToArray());

    private static DateOnly? MaxDate(params DateTime?[] dates)
    {
        var latest = dates
            .Where(date => date.HasValue)
            .Select(date => DateOnly.FromDateTime(date!.Value))
            .OrderDescending()
            .FirstOrDefault();

        return latest == default ? null : latest;
    }

    private static string AnyEquals(string fieldName, IEnumerable<string> values)
    {
        var clauses = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => $"{fieldName} eq '{Escape(value)}'")
            .ToList();

        return clauses.Count == 0 ? "" : "(" + string.Join(" or ", clauses) + ")";
    }

    private static string And(params string[] clauses)
    {
        var nonBlank = clauses.Where(clause => !string.IsNullOrWhiteSpace(clause)).ToList();
        return string.Join(" and ", nonBlank);
    }

    private static string Or(params string[] clauses)
    {
        var nonBlank = clauses.Where(clause => !string.IsNullOrWhiteSpace(clause)).ToList();
        return nonBlank.Count == 0 ? "" : string.Join(" or ", nonBlank.Select(clause => $"({clause})"));
    }

    private static string FormatMomentusDate(DateTime date) =>
        date.ToString("yyyy-MM-ddTHH:mm:ss");

    private static DateOnly? ToDateOnly(DateTime? date) =>
        date.HasValue ? DateOnly.FromDateTime(date.Value) : null;

    private static string Escape(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}
