using System.Text.Json;

namespace Kallman.Automation.Core.Operations;

public sealed class AutomationRunSummary
{
    public string RunId { get; init; } = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
    public required string Automation { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "Running";
    public long RecordsRead { get; set; }
    public long RecordsWritten { get; set; }
    public int Warnings { get; set; }
    public int Errors { get; set; }
    public bool CheckpointAdvanced { get; set; }
    public string? Message { get; set; }

    public void Complete(string status, string? message = null)
    {
        Status = status;
        Message = message;
        FinishedAt = DateTimeOffset.Now;
    }

    public void WriteJson(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
