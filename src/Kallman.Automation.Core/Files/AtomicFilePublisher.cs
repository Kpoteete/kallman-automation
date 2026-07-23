namespace Kallman.Automation.Core.Files;

public static class AtomicFilePublisher
{
    public static string CreateTemporaryPath(string destinationPath)
    {
        string fullPath = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Destination has no parent directory.");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
    }

    public static void Publish(string temporaryPath, string destinationPath, string? backupPath = null)
    {
        if (!File.Exists(temporaryPath))
            throw new FileNotFoundException("Temporary output does not exist.", temporaryPath);
        if (new FileInfo(temporaryPath).Length == 0)
            throw new InvalidDataException("Temporary output is empty; publication was refused.");

        string destination = Path.GetFullPath(destinationPath);
        if (backupPath is not null && File.Exists(destination))
            File.Copy(destination, backupPath, overwrite: true);

        File.Move(temporaryPath, destination, overwrite: true);
    }
}
