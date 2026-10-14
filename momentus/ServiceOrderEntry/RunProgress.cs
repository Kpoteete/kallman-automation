using System.Diagnostics;

namespace ServiceOrderEntry;

// Progress is observational only. A closed console must never interrupt a journaled write.
internal sealed class RunProgress : IDisposable
{
    private readonly object sync = new();
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly Func<int> requestCount;
    private readonly Timer timer;
    private string activity = "Starting";
    private long activityStarted;
    private bool disposed;
    private readonly Dictionary<string, (int Count, TimeSpan Elapsed)> requestTimes = [];
    public string Context { get; set; } = "";

    public RunProgress(Func<int> requestCount)
    {
        this.requestCount = requestCount;
        timer = new Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public IDisposable Begin(string message, bool announce = true)
    {
        lock (sync)
        {
            var previous = activity;
            var previousStarted = activityStarted;
            activity = message;
            activityStarted = elapsed.ElapsedMilliseconds;
            if (announce) Write(WithContext(message));
            return new ActivityScope(() =>
            {
                lock (sync) { activity = previous; activityStarted = previousStarted; }
            });
        }
    }

    public void Message(string message) => Write(WithContext(message));
    public void RecordRequest(string operation, TimeSpan duration)
    {
        lock (sync)
        {
            var prior = requestTimes.GetValueOrDefault(operation);
            requestTimes[operation] = (prior.Count + 1, prior.Elapsed + duration);
            if (duration.TotalSeconds >= 5)
                Write(WithContext($"{operation} took {duration.TotalSeconds:N1}s."));
        }
    }

    public void ReportTimings()
    {
        lock (sync)
        {
            Write($"Run elapsed time: {elapsed.Elapsed.TotalMinutes:N1} minutes. Largest API time totals:");
            foreach (var entry in requestTimes.OrderByDescending(x => x.Value.Elapsed).Take(5))
                Write($"{entry.Key}: {entry.Value.Count:N0} requests, {entry.Value.Elapsed.TotalSeconds:N1}s total.");
        }
    }
    private string WithContext(string message) => Context.Length == 0 ? message : $"{Context}: {message}";
    private void Heartbeat()
    {
        lock (sync)
        {
            if (disposed) return;
            var seconds = (elapsed.ElapsedMilliseconds - activityStarted) / 1000;
            Write(WithContext($"Still working: {activity} ({seconds:N0}s in this step; {requestCount():N0} API requests so far)."));
        }
    }

    internal static void Write(string message)
    {
        try { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message.Replace('\r', ' ').Replace('\n', ' ')}"); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (sync) { disposed = true; }
        timer.Dispose();
    }

    private sealed class ActivityScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
