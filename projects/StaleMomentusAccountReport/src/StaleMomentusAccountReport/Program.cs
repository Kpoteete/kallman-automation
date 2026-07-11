using Microsoft.Extensions.Configuration;
using StaleMomentusAccountReport.Configuration;
using StaleMomentusAccountReport.Data;
using StaleMomentusAccountReport.Reports;
using StaleMomentusAccountReport.Rules;

namespace StaleMomentusAccountReport;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var cli = CliOptions.Parse(args);
            if (cli.ShowHelp)
            {
                Console.WriteLine(CliOptions.HelpText);
                return 0;
            }

            var config = LoadConfig();
            cli.ApplyTo(config);
            if (cli.DryRun)
            {
                config.ApplyDryRunDefaults();
            }

            config.Validate(cli.DryRun);

            var today = DateOnly.FromDateTime(DateTime.Today);
            var accountAgeCutoffDate = SubtractYears(today, config.Report.AccountAgeYears);
            var staleActivityCutoffDate = SubtractYears(today, config.Report.StaleActivityYears);
            var outputPath = cli.ResolveOutputPath(config.Report.OutputFolder, today);

            IAccountActivitySource source = cli.DryRun
                ? new DryRunAccountActivitySource()
                : new MomentusAccountActivitySource(config);

            Console.WriteLine(cli.DryRun
                ? "Running dry-run with sample stale account data."
                : $"Querying Momentus org {config.Momentus.OrganizationCode}.");

            var snapshots = await source.GetAccountSnapshotsAsync(CancellationToken.None);
            var evaluator = new StaleAccountEvaluator(config);
            var results = evaluator.Evaluate(snapshots, accountAgeCutoffDate, staleActivityCutoffDate).ToList();

            var writer = new StaleAccountWorkbookWriter();
            writer.Write(outputPath, results, new WorkbookRunInfo(today, accountAgeCutoffDate, staleActivityCutoffDate, cli.DryRun, config.Momentus.OrganizationCode));

            Console.WriteLine($"Wrote {results.Count} stale account rows to {outputPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static ReportConfig LoadConfig()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables("STALE_")
            .Build();

        var config = new ReportConfig();
        configuration.Bind(config);
        return config;
    }

    private static DateOnly SubtractYears(DateOnly date, decimal years)
    {
        var days = decimal.ToDouble(years * 365.25m);
        return DateOnly.FromDateTime(date.ToDateTime(TimeOnly.MinValue).AddDays(-days));
    }
}
