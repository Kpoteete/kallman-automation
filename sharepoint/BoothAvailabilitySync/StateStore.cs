using System.Text.Json;

namespace BoothAvailabilitySync;

public sealed class StateStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public StateStore(string stateFolder)
    {
        Directory.CreateDirectory(stateFolder);
        _path = Path.Combine(stateFolder, "last-success.json");
    }

    public SnapshotSummary? Load()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SnapshotSummary>(File.ReadAllText(_path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(SnapshotSummary summary)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(summary, JsonOptions));
    }
}
