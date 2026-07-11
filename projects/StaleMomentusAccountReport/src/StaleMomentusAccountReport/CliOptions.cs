using StaleMomentusAccountReport.Configuration;

namespace StaleMomentusAccountReport;

public sealed class CliOptions
{
    public bool DryRun { get; private set; }
    public bool ShowHelp { get; private set; }
    public string? OrganizationCode { get; private set; }
    public string? OutputPath { get; private set; }
    public decimal? AccountAgeYears { get; private set; }
    public decimal? StaleActivityYears { get; private set; }

    public static string HelpText =>
        """
        Stale Momentus Account Report

        Usage:
          dotnet run --project src/StaleMomentusAccountReport -- [options]

        Options:
          --dry-run             Create a sample workbook without querying Momentus.
          --org <code>          Override the Momentus organization code.
          --output <path>       Override the workbook output path.
          --account-age-years <n>       Override the account age threshold. Default is 5.
          --stale-activity-years <n>    Override the activity stale threshold. Default is 6.1.
          --cutoff-years <n>            Back-compatible alias for --stale-activity-years.
          --help                Show this help text.

        Environment variables use the STALE_ prefix and double underscores for nesting.
        Example: STALE_Momentus__ApiUserId
        """;

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;
                case "--org":
                    options.OrganizationCode = ReadValue(args, ref i, arg);
                    break;
                case "--output":
                    options.OutputPath = ReadValue(args, ref i, arg);
                    break;
                case "--cutoff-years":
                case "--stale-activity-years":
                    if (!decimal.TryParse(ReadValue(args, ref i, arg), out var staleYears) || staleYears <= 0)
                    {
                        throw new ArgumentException($"{arg} must be a positive number.");
                    }

                    options.StaleActivityYears = staleYears;
                    break;
                case "--account-age-years":
                    if (!decimal.TryParse(ReadValue(args, ref i, arg), out var accountAgeYears) || accountAgeYears <= 0)
                    {
                        throw new ArgumentException("--account-age-years must be a positive number.");
                    }

                    options.AccountAgeYears = accountAgeYears;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{arg}'. Use --help for usage.");
            }
        }

        return options;
    }

    public void ApplyTo(ReportConfig config)
    {
        if (!string.IsNullOrWhiteSpace(OrganizationCode))
        {
            config.Momentus.OrganizationCode = OrganizationCode;
        }

        if (AccountAgeYears.HasValue)
        {
            config.Report.AccountAgeYears = AccountAgeYears.Value;
        }

        if (StaleActivityYears.HasValue)
        {
            config.Report.StaleActivityYears = StaleActivityYears.Value;
        }
    }

    public string ResolveOutputPath(string outputFolder, DateOnly today)
    {
        if (!string.IsNullOrWhiteSpace(OutputPath))
        {
            return Path.GetFullPath(OutputPath);
        }

        var folder = string.IsNullOrWhiteSpace(outputFolder) ? "outputs" : outputFolder;
        Directory.CreateDirectory(folder);
        return Path.GetFullPath(Path.Combine(folder, $"stale-accounts-{today:yyyyMMdd}.xlsx"));
    }

    private static string ReadValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{optionName} requires a value.");
        }

        index++;
        return args[index];
    }
}
