namespace ServiceOrderEntry;

internal static class CurrentRunState
{
    public static string CreateFolder(string stateRoot, string runId)
    {
        if (!Guid.TryParseExact(runId, "N", out _)) throw new InvalidOperationException("Invalid run identity.");
        var folder = Path.Combine(Path.GetFullPath(stateRoot), "runs", runId);
        if (Directory.Exists(folder)) throw new InvalidOperationException("A fresh run cannot reuse an earlier run folder.");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
