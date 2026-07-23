namespace StaleMomentusAccountReport.Configuration;

public sealed class ReportConfig
{
    public MomentusConfig Momentus { get; set; } = new();
    public AccountMappings AccountMappings { get; set; } = new();
    public ContactMappings ContactMappings { get; set; } = new();
    public ReportDefaults Report { get; set; } = new();

    public void ApplyDryRunDefaults()
    {
        if (AccountMappings.ActiveStatusCodes.Count == 0)
        {
            AccountMappings.ActiveStatusCodes = ["ACTIVE"];
        }

        if (AccountMappings.ProspectStatusCodes.Count == 0)
        {
            AccountMappings.ProspectStatusCodes = ["PROSPECT"];
        }

        if (AccountMappings.CompanyTypeCodes.Count == 0)
        {
            AccountMappings.CompanyTypeCodes = ["COMPANY"];
        }

        if (AccountMappings.OrganizationTypeCodes.Count == 0)
        {
            AccountMappings.OrganizationTypeCodes = ["ORG"];
        }

        if (ContactMappings.ActiveStatusCodes.Count == 0)
        {
            ContactMappings.ActiveStatusCodes = ["ACTIVE"];
        }
    }

    public void Validate(bool dryRun)
    {
        Report.AccountAgeYears = Report.AccountAgeYears <= 0 ? 5 : Report.AccountAgeYears;
        Report.StaleActivityYears = Report.StaleActivityYears <= 0 ? 6.1m : Report.StaleActivityYears;
        Report.OutputFolder = string.IsNullOrWhiteSpace(Report.OutputFolder) ? "outputs" : Report.OutputFolder;

        if (dryRun)
        {
            return;
        }

        var missing = new List<string>();
        AddIfMissing(missing, Momentus.BaseUrl, "Momentus:BaseUrl");
        AddIfMissing(missing, Momentus.OrganizationCode, "Momentus:OrganizationCode");
        AddIfMissing(missing, Momentus.ApiUserId, "Momentus:ApiUserId");
        AddIfMissing(missing, Momentus.ApiKey, "Momentus:ApiKey");
        AddIfMissing(missing, Momentus.ApiSecret, "Momentus:ApiSecret");

        if (AccountMappings.ActiveStatusCodes.Count == 0)
        {
            missing.Add("AccountMappings:ActiveStatusCodes");
        }

        if (ContactMappings.ActiveStatusCodes.Count == 0)
        {
            missing.Add("ContactMappings:ActiveStatusCodes");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Missing required configuration: " + string.Join(", ", missing) +
                ". Copy appsettings.example.json to appsettings.json or set STALE_ environment variables.");
        }
    }

    private static void AddIfMissing(List<string> missing, string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            missing.Add(name);
        }
    }
}

public sealed class MomentusConfig
{
    public string BaseUrl { get; set; } = "";
    public string OrganizationCode { get; set; } = "";
    public string ApiUserId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string? ProxiedUserId { get; set; }
    public int SearchMaxResults { get; set; } = 50_000;
    public int SearchPageSize { get; set; } = 1_000;
}

public sealed class AccountMappings
{
    public List<string> ActiveStatusCodes { get; set; } = [];
    public List<string> ProspectStatusCodes { get; set; } = [];
    public List<string> CompanyTypeCodes { get; set; } = [];
    public List<string> OrganizationTypeCodes { get; set; } = [];
}

public sealed class ContactMappings
{
    public List<string> ActiveStatusCodes { get; set; } = [];
}

public sealed class ReportDefaults
{
    public decimal AccountAgeYears { get; set; } = 5m;
    public decimal StaleActivityYears { get; set; } = 6.1m;
    public string OutputFolder { get; set; } = "outputs";
}
