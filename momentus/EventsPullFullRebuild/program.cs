using System.Diagnostics;

class Program
{
    private const string OutputFolder =
        @"C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents";

    private static readonly string OutputFilePath =
        Path.Combine(OutputFolder, "Events_Pull.csv");

    private static readonly string RunStatePath =
        Path.Combine(OutputFolder, "Events_Pull.last_run.txt");

    private static readonly string BackupFilePath =
        Path.Combine(OutputFolder, "Events_Pull.before_full_rebuild.csv");

    private static readonly string BackupRunStatePath =
        Path.Combine(OutputFolder, "Events_Pull.last_run.before_full_rebuild.txt");

    static int Main()
    {
        try
        {
            Directory.CreateDirectory(OutputFolder);

            string projectPath = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "EventsPull", "EventsPull.csproj"));

            if (!File.Exists(projectPath))
                throw new FileNotFoundException("Could not find the normal EventsPull project.", projectPath);

            Console.WriteLine("-> Starting Events Pull full rebuild...");
            Console.WriteLine($"-> Normal project: {projectPath}");
            Console.WriteLine($"-> Rebuilt file will replace: {OutputFilePath}");

            PrepareBackups();

            int exitCode = RunNormalEventsPull(projectPath);

            if (exitCode != 0 || !File.Exists(OutputFilePath))
            {
                Console.WriteLine("-> Full rebuild failed. Restoring the previous CSV and checkpoint...");
                RestoreBackups();
                return exitCode == 0 ? 1 : exitCode;
            }

            DeleteIfExists(BackupFilePath);
            DeleteIfExists(BackupRunStatePath);

            Console.WriteLine("-> Full rebuild completed successfully.");
            Console.WriteLine($"-> CSV replaced: {OutputFilePath}");
            Console.WriteLine($"-> Checkpoint reset: {RunStatePath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL:");
            Console.WriteLine(ex);

            try
            {
                RestoreBackups();
            }
            catch (Exception restoreEx)
            {
                Console.WriteLine("RESTORE FAILED:");
                Console.WriteLine(restoreEx);
            }

            return 1;
        }
    }

    private static void PrepareBackups()
    {
        DeleteIfExists(BackupFilePath);
        DeleteIfExists(BackupRunStatePath);

        if (File.Exists(OutputFilePath))
        {
            File.Move(OutputFilePath, BackupFilePath);
            Console.WriteLine($"-> Existing CSV backed up: {BackupFilePath}");
        }

        if (File.Exists(RunStatePath))
        {
            File.Move(RunStatePath, BackupRunStatePath);
            Console.WriteLine($"-> Existing checkpoint backed up: {BackupRunStatePath}");
        }
    }

    private static int RunNormalEventsPull(string projectPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start dotnet.");

        process.WaitForExit();
        return process.ExitCode;
    }

    private static void RestoreBackups()
    {
        DeleteIfExists(OutputFilePath);
        DeleteIfExists(RunStatePath);

        if (File.Exists(BackupFilePath))
            File.Move(BackupFilePath, OutputFilePath);

        if (File.Exists(BackupRunStatePath))
            File.Move(BackupRunStatePath, RunStatePath);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
