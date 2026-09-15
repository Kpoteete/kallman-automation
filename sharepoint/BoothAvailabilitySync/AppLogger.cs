namespace BoothAvailabilitySync;

public sealed class AppLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public string LogPath { get; }

    public AppLogger(string stateFolder)
    {
        var logFolder = Path.Combine(stateFolder, "logs");
        Directory.CreateDirectory(logFolder);
        LogPath = Path.Combine(logFolder, $"sync_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        _writer = new StreamWriter(LogPath, append: true) { AutoFlush = true };
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [{level}] {message}";
        lock (_gate)
        {
            Console.WriteLine(line);
            _writer.WriteLine(line);
        }
    }

    public void Dispose() => _writer.Dispose();
}
