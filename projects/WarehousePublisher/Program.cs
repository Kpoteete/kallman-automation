namespace Kallman.WarehousePublisher;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = PublisherOptions.Parse(args);
            if (options.ShowHelp)
            {
                PublisherOptions.PrintHelp();
                return 0;
            }

            var publisher = new WarehousePublisherRunner(options);
            PublisherRunResult result = publisher.Run();

            if (result.SkippedBecauseAnotherRunIsActive)
                return 0;

            return result.Errors == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }
}

public sealed record PublisherOptions(
    string SourceRoot,
    string DestinationRoot,
    string StatePath,
    bool Preview,
    bool ResetState,
    bool ShowHelp)
{
    public static PublisherOptions Parse(string[] args)
    {
        string? source = Environment.GetEnvironmentVariable("KALLMAN_DATA_WAREHOUSE");
        string? destination = Environment.GetEnvironmentVariable("KALLMAN_PUBLISHED_WAREHOUSE");
        string? statePath = Environment.GetEnvironmentVariable("KALLMAN_WAREHOUSE_PUBLISHER_STATE");
        bool preview = false;
        bool resetState = false;
        bool showHelp = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "publish":
                    preview = false;
                    break;
                case "preview":
                case "--preview":
                    preview = true;
                    break;
                case "--source":
                    source = RequireValue(args, ref i);
                    break;
                case "--destination":
                    destination = RequireValue(args, ref i);
                    break;
                case "--state":
                    statePath = RequireValue(args, ref i);
                    break;
                case "--reset-state":
                    resetState = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (showHelp)
            return new("", "", "", preview, resetState, true);

        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("KALLMAN_DATA_WAREHOUSE is not set. Set it to the private warehouse root or pass --source.");

        if (string.IsNullOrWhiteSpace(destination))
            throw new InvalidOperationException("KALLMAN_PUBLISHED_WAREHOUSE is not set. Set it to the read-only published warehouse root or pass --destination.");

        source = Path.GetFullPath(source.Trim());
        destination = Path.GetFullPath(destination.Trim());

        if (string.IsNullOrWhiteSpace(statePath))
        {
            statePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Kallman",
                "WarehousePublisher",
                "state.json");
        }

        statePath = Path.GetFullPath(statePath.Trim());

        return new(source, destination, statePath, preview, resetState, false);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
WarehousePublisher

Publishes supported warehouse files from the private data warehouse to a separate
consumer-facing copy. Unchanged files are not recopied.

Usage:
  WarehousePublisher.exe publish [options]
  WarehousePublisher.exe preview [options]

Environment variables:
  KALLMAN_DATA_WAREHOUSE              Private source warehouse root
  KALLMAN_PUBLISHED_WAREHOUSE         Published read-only warehouse root
  KALLMAN_WAREHOUSE_PUBLISHER_STATE   Optional state JSON path

Options:
  --source PATH        Override source root
  --destination PATH   Override destination root
  --state PATH         Override state file location
  --preview            Show what would change without writing
  --reset-state        Ignore and replace the existing comparison state
  --help               Show help

Supported data files:
  .csv, .xlsx, .xls

Publication scope:
  - Supported files in the warehouse root
  - Supported files under Asana\current
  - Other warehouse subfolders are not scanned
  - *.previous.csv files are skipped

Safety:
  - Source and destination may not be the same folder or nested inside each other.
  - Published files are replaced atomically.
  - A source file that changes during publication is deferred until the next run.
  - Missing source files are retained in the published warehouse; nothing is auto-deleted.
""");
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (++index >= args.Length)
            throw new ArgumentException($"Missing value for {args[index - 1]}.");

        return args[index];
    }
}
