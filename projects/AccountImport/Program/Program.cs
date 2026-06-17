using System.Text.Json;
using AccountImport.Models;
using AccountImport.Services;

namespace AccountImport;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            AppConfig config = LoadConfig(args);
            config.EnsureFolders();

            MomentusCredentials credentials = MomentusCredentials.FromEnvironment();

            string auditPath = ExcelService.TimestampedPath(
                config.Phase4Folder,
                "momentus_account_contact_import_audit",
                DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"),
                ".csv");

            using var api = new MomentusSdkApiService(config, credentials);
            var excel = new ExcelService();
            var audit = new AuditLogger(auditPath);
            var runner = new PhaseRunner(config, excel, api, audit);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
                Console.WriteLine("Cancellation requested. The current safe checkpoint or row will finish, then the run will stop.");
            };

            await runner.RunAsync(cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException ex)
        {
            Console.Error.WriteLine("Stopped safely: " + ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("STOPPED: " + ex.Message);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static AppConfig LoadConfig(string[] args)
    {
        string configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(configPath))
        {
            string localPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            if (File.Exists(localPath)) configPath = localPath;
        }

        AppConfig config = AppConfig.CreateDefault();
        if (File.Exists(configPath))
        {
            string json = File.ReadAllText(configPath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            if (loaded is not null) config = loaded;
        }

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--live", StringComparison.OrdinalIgnoreCase))
            {
                config.DryRun = false;
            }
            else if (string.Equals(arg, "--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                config.DryRun = true;
            }
            else if (string.Equals(arg, "--root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                config.RootPath = args[++i];
            }
        }

        if (string.IsNullOrWhiteSpace(config.RootPath))
            throw new InvalidOperationException("RootPath is blank in configuration.");
        if (string.IsNullOrWhiteSpace(config.MomentusUri))
            throw new InvalidOperationException("MomentusUri is blank in configuration.");
        if (string.IsNullOrWhiteSpace(config.OrgCode))
            throw new InvalidOperationException("OrgCode is blank in configuration.");

        return config;
    }
}
