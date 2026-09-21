using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kallman.Automation.Core.Files;

namespace Kallman.WarehousePublisher;

public sealed class WarehousePublisherRunner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv",
        ".xlsx",
        ".xls"
    };

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "runs",
        "history",
        "raw",
        "logs"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly PublisherOptions options;

    public WarehousePublisherRunner(PublisherOptions options)
    {
        this.options = options;
    }

    public PublisherRunResult Run()
    {
        ValidateRoots();

        Directory.CreateDirectory(Path.GetDirectoryName(options.StatePath)
            ?? throw new InvalidOperationException("State path has no parent directory."));

        using FileStream? runLock = TryAcquireRunLock();
        if (runLock is null)
        {
            Console.WriteLine("Another WarehousePublisher run is already active. No action taken.");
            return PublisherRunResult.Overlap();
        }

        PublisherState state = LoadState();

        if (!Directory.Exists(options.SourceRoot))
            throw new DirectoryNotFoundException($"Source warehouse does not exist: {options.SourceRoot}");

        if (!options.Preview)
            Directory.CreateDirectory(options.DestinationRoot);

        var result = new PublisherRunResult();
        var seenRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<string> sourceFiles = Directory
            .EnumerateFiles(options.SourceRoot, "*", SearchOption.AllDirectories)
            .Where(IsPublishableSourceFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine(options.Preview ? "WAREHOUSE PUBLISHER PREVIEW" : "WAREHOUSE PUBLISHER");
        Console.WriteLine($"Source files found: {sourceFiles.Count:N0}");

        foreach (string sourcePath in sourceFiles)
        {
            string relativePath = Path.GetRelativePath(options.SourceRoot, sourcePath);
            seenRelativePaths.Add(relativePath);
            result.FilesSeen++;

            try
            {
                PublishOne(sourcePath, relativePath, state, result);
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.ErrorDetails.Add(new PublisherError(relativePath, ex.Message));
                Console.Error.WriteLine($"ERROR {relativePath}: {ex.Message}");
            }
        }

        result.RetainedMissingSourceFiles = state.Files.Keys.Count(path => !seenRelativePaths.Contains(path));

        if (!options.Preview)
        {
            state.SourceRoot = options.SourceRoot;
            state.DestinationRoot = options.DestinationRoot;
            state.LastCompletedUtc = DateTimeOffset.UtcNow;
            SaveState(state);
            WritePublishedStatus(result);
        }

        PrintSummary(result);
        return result;
    }

    private void PublishOne(
        string sourcePath,
        string relativePath,
        PublisherState state,
        PublisherRunResult result)
    {
        string destinationPath = Path.Combine(options.DestinationRoot, relativePath);
        FileSnapshot initial = CaptureSnapshot(sourcePath);

        if (initial.Length == 0)
            throw new InvalidDataException("Source file is empty; existing published copy was retained.");

        state.Files.TryGetValue(relativePath, out PublishedFileState? previous);

        if (previous is not null &&
            previous.SourceLength == initial.Length &&
            previous.SourceLastWriteUtcTicks == initial.LastWriteUtcTicks &&
            File.Exists(destinationPath) &&
            new FileInfo(destinationPath).Length == initial.Length)
        {
            result.Unchanged++;
            return;
        }

        StableHash? stableSource = TryHashStableFile(sourcePath);
        if (stableSource is null)
        {
            result.Deferred++;
            Console.WriteLine($"DEFER {relativePath} (source changed while being read)");
            return;
        }

        string sourceHash = stableSource.Sha256;
        initial = stableSource.Snapshot;

        if (previous is not null &&
            string.Equals(previous.Sha256, sourceHash, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(destinationPath) &&
            new FileInfo(destinationPath).Length == initial.Length)
        {
            previous.SourceLength = initial.Length;
            previous.SourceLastWriteUtcTicks = initial.LastWriteUtcTicks;
            state.Files[relativePath] = previous;
            result.Unchanged++;
            return;
        }

        if (previous is null && File.Exists(destinationPath))
        {
            FileInfo destinationInfo = new(destinationPath);
            if (destinationInfo.Length == initial.Length &&
                string.Equals(ComputeSha256(destinationPath), sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                state.Files[relativePath] = NewPublishedState(initial, sourceHash, DateTimeOffset.UtcNow);
                result.Unchanged++;
                return;
            }
        }

        if (options.Preview)
        {
            if (File.Exists(destinationPath))
            {
                result.WouldUpdate++;
                Console.WriteLine($"UPDATE {relativePath}");
            }
            else
            {
                result.WouldAdd++;
                Console.WriteLine($"ADD    {relativePath}");
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException($"Destination path has no parent: {destinationPath}"));

        string temporaryPath = AtomicFilePublisher.CreateTemporaryPath(destinationPath);

        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: true);

            FileSnapshot afterCopy = CaptureSnapshot(sourcePath);
            if (afterCopy != initial)
            {
                result.Deferred++;
                Console.WriteLine($"DEFER {relativePath} (source changed during copy)");
                return;
            }

            string copiedHash = ComputeSha256(temporaryPath);
            if (!string.Equals(sourceHash, copiedHash, StringComparison.OrdinalIgnoreCase))
            {
                result.Deferred++;
                Console.WriteLine($"DEFER {relativePath} (copy verification did not match source)");
                return;
            }

            bool existed = File.Exists(destinationPath);
            AtomicFilePublisher.Publish(temporaryPath, destinationPath);
            File.SetLastWriteTimeUtc(destinationPath, new DateTime(initial.LastWriteUtcTicks, DateTimeKind.Utc));

            state.Files[relativePath] = NewPublishedState(initial, sourceHash, DateTimeOffset.UtcNow);

            if (existed)
            {
                result.Updated++;
                Console.WriteLine($"UPDATE {relativePath}");
            }
            else
            {
                result.Added++;
                Console.WriteLine($"ADD    {relativePath}");
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private bool IsPublishableSourceFile(string fullPath)
    {
        string relativePath = Path.GetRelativePath(options.SourceRoot, fullPath);
        string fileName = Path.GetFileName(relativePath);

        if (fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith(".", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!SupportedExtensions.Contains(Path.GetExtension(fileName)))
            return false;

        string? directory = Path.GetDirectoryName(relativePath);
        if (string.IsNullOrWhiteSpace(directory))
            return true;

        string[] segments = directory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return !segments.Any(segment => ExcludedDirectoryNames.Contains(segment));
    }

    private StableHash? TryHashStableFile(string path)
    {
        FileSnapshot before = CaptureSnapshot(path);
        string hash = ComputeSha256(path);
        FileSnapshot after = CaptureSnapshot(path);

        return before == after ? new StableHash(after, hash) : null;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static FileSnapshot CaptureSnapshot(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();

        if (!info.Exists)
            throw new FileNotFoundException("Source file disappeared during publication.", path);

        return new FileSnapshot(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private PublisherState LoadState()
    {
        if (options.ResetState || !File.Exists(options.StatePath))
            return NewState();

        string json = File.ReadAllText(options.StatePath, Encoding.UTF8);
        PublisherState? state = JsonSerializer.Deserialize<PublisherState>(json, JsonOptions);
        if (state is null)
            throw new InvalidDataException($"State file could not be read: {options.StatePath}");

        if (!SamePath(state.SourceRoot, options.SourceRoot) ||
            !SamePath(state.DestinationRoot, options.DestinationRoot))
        {
            throw new InvalidOperationException(
                "The publisher state belongs to different source/destination roots. " +
                "Use the correct state file or run once with --reset-state.");
        }

        state.Files = new Dictionary<string, PublishedFileState>(
            state.Files ?? new Dictionary<string, PublishedFileState>(),
            StringComparer.OrdinalIgnoreCase);

        return state;
    }

    private PublisherState NewState() => new()
    {
        Version = 1,
        SourceRoot = options.SourceRoot,
        DestinationRoot = options.DestinationRoot,
        Files = new Dictionary<string, PublishedFileState>(StringComparer.OrdinalIgnoreCase)
    };

    private void SaveState(PublisherState state)
    {
        string temporaryPath = AtomicFilePublisher.CreateTemporaryPath(options.StatePath);
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(state, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            AtomicFilePublisher.Publish(temporaryPath, options.StatePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private void WritePublishedStatus(PublisherRunResult result)
    {
        string statusPath = Path.Combine(options.DestinationRoot, "_warehouse_status.json");
        string temporaryPath = AtomicFilePublisher.CreateTemporaryPath(statusPath);

        var status = new
        {
            completed_utc = DateTimeOffset.UtcNow,
            status = result.Errors == 0 ? "Succeeded" : "PartialFailure",
            files_seen = result.FilesSeen,
            files_added = result.Added,
            files_updated = result.Updated,
            files_unchanged = result.Unchanged,
            files_deferred = result.Deferred,
            errors = result.Errors,
            retained_missing_source_files = result.RetainedMissingSourceFiles,
            error_details = result.ErrorDetails
        };

        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(status, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            AtomicFilePublisher.Publish(temporaryPath, statusPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private FileStream? TryAcquireRunLock()
    {
        string stateDirectory = Path.GetDirectoryName(options.StatePath)
            ?? throw new InvalidOperationException("State path has no parent directory.");
        string lockPath = Path.Combine(stateDirectory, "WarehousePublisher.lock");

        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void ValidateRoots()
    {
        string source = Path.TrimEndingDirectorySeparator(options.SourceRoot);
        string destination = Path.TrimEndingDirectorySeparator(options.DestinationRoot);

        if (SamePath(source, destination))
            throw new InvalidOperationException("Source and destination must be different folders.");

        if (IsInside(source, destination) || IsInside(destination, source))
            throw new InvalidOperationException("Source and destination may not be nested inside each other.");
    }

    private static bool IsInside(string parent, string child)
    {
        string prefix = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static PublishedFileState NewPublishedState(
        FileSnapshot snapshot,
        string sha256,
        DateTimeOffset publishedUtc) => new()
        {
            SourceLength = snapshot.Length,
            SourceLastWriteUtcTicks = snapshot.LastWriteUtcTicks,
            Sha256 = sha256,
            LastPublishedUtc = publishedUtc
        };

    private static void PrintSummary(PublisherRunResult result)
    {
        Console.WriteLine();
        Console.WriteLine("Summary");
        Console.WriteLine($"  Files seen:                    {result.FilesSeen:N0}");

        if (result.WouldAdd > 0 || result.WouldUpdate > 0)
        {
            Console.WriteLine($"  Would add:                     {result.WouldAdd:N0}");
            Console.WriteLine($"  Would update:                  {result.WouldUpdate:N0}");
        }
        else
        {
            Console.WriteLine($"  Added:                         {result.Added:N0}");
            Console.WriteLine($"  Updated:                       {result.Updated:N0}");
        }

        Console.WriteLine($"  Unchanged:                     {result.Unchanged:N0}");
        Console.WriteLine($"  Deferred active files:         {result.Deferred:N0}");
        Console.WriteLine($"  Missing source files retained: {result.RetainedMissingSourceFiles:N0}");
        Console.WriteLine($"  Errors:                        {result.Errors:N0}");
    }

    private sealed record StableHash(FileSnapshot Snapshot, string Sha256);
    private readonly record struct FileSnapshot(long Length, long LastWriteUtcTicks);
}

public sealed class PublisherState
{
    public int Version { get; set; } = 1;
    public string SourceRoot { get; set; } = "";
    public string DestinationRoot { get; set; } = "";
    public DateTimeOffset? LastCompletedUtc { get; set; }
    public Dictionary<string, PublishedFileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PublishedFileState
{
    public long SourceLength { get; set; }
    public long SourceLastWriteUtcTicks { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset LastPublishedUtc { get; set; }
}

public sealed class PublisherRunResult
{
    public int FilesSeen { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Deferred { get; set; }
    public int WouldAdd { get; set; }
    public int WouldUpdate { get; set; }
    public int RetainedMissingSourceFiles { get; set; }
    public int Errors { get; set; }
    public bool SkippedBecauseAnotherRunIsActive { get; set; }
    public List<PublisherError> ErrorDetails { get; } = [];

    public static PublisherRunResult Overlap() => new()
    {
        SkippedBecauseAnotherRunIsActive = true
    };
}

public sealed record PublisherError(string RelativePath, string Message);
