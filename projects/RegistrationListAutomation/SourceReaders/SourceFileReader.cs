internal static class SourceFileReader
{
    public static string CopyStableFile(string sourcePath, string tempRunFolder)
    {
        string destinationPath = Path.Combine(tempRunFolder, Path.GetFileName(sourcePath));
        const int maxAttempts = 10;
        const int delayMilliseconds = 3000;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                FileInfo before = new(sourcePath);
                long lengthBefore = before.Length;
                DateTime changedBefore = before.LastWriteTimeUtc;

                using (FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (FileStream destination = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    source.CopyTo(destination);

                FileInfo after = new(sourcePath);
                FileInfo copied = new(destinationPath);
                if (lengthBefore != after.Length || changedBefore != after.LastWriteTimeUtc || copied.Length != lengthBefore)
                    throw new IOException($"Source file changed while being copied: {sourcePath}");

                Console.WriteLine($"Copied stable source file for reading: {Path.GetFileName(sourcePath)}");
                return destinationPath;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Console.WriteLine($"File busy or changing: {Path.GetFileName(sourcePath)}. Attempt {attempt} of {maxAttempts}.");
                Thread.Sleep(delayMilliseconds);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"Could not copy a stable file after {maxAttempts} attempts: {sourcePath}", ex);
            }
        }

        throw new IOException($"Could not copy file: {sourcePath}");
    }
}
