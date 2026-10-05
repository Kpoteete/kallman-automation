using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServiceOrderEntry;

internal enum StageStatus { Planned, Dispatching, Unknown, Failed, Succeeded, Verified }

internal sealed record OrderIdentity(string Endpoint, string Organization, int Event, int Exhibitor, int Order)
{
    public static OrderIdentity From(CliOptions options, RunRow row) => new(
        new Uri(options.BaseUrl).AbsoluteUri.TrimEnd('/'), options.OrganizationCode, row.EventId, row.ExhibitorId, row.OrderNumber);
    [JsonIgnore]
    public string Key => Hash(JsonSerializer.Serialize(this));
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal sealed class StageEvidence
{
    public string Key { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Target { get; set; } = "";
    public string Intent { get; set; } = "";
    public string Result { get; set; } = "";
    public Dictionary<string, string> Source { get; set; } = [];
    public StageStatus Status { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
    public string RunId { get; set; } = "";
    public int Attempts { get; set; }
    public string Error { get; set; } = "";
    public string Verification { get; set; } = "";
    public DateTimeOffset? VerifiedAt { get; set; }
    public bool Reconciled { get; set; }
}

internal sealed class OrderEvidence
{
    public int Version { get; set; } = 1;
    public OrderIdentity Identity { get; set; } = new("", "", 0, 0, 0);
    public string ProcessingId { get; set; } = Guid.NewGuid().ToString("N");
    public string CreatedRunId { get; set; } = "";
    public RunRow Plan { get; set; } = new();
    public bool SendEmail { get; set; }
    public bool ActivateOrder { get; set; }
    public bool ActivateExhibitor { get; set; }
    public bool Complete { get; set; }
    public List<StageEvidence> Stages { get; set; } = [];
    public List<string> SearchFailures { get; set; } = [];
}

internal sealed class JournalStorageException(string message, Exception cause) : IOException(message, cause);
internal sealed class RecoveryReviewException(string message) : Exception(message);

internal interface IJournalStore
{
    IReadOnlyList<OrderEvidence> Load();
    void Save(OrderEvidence evidence);
}

internal sealed class FileJournalStore : IJournalStore
{
    private readonly string folder;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public FileJournalStore(string stateFolder)
    {
        folder = Path.Combine(stateFolder, "orders");
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $"probe-{Guid.NewGuid():N}.tmp");
            DurableWrite(probe, "{}");
            File.Delete(probe);
        }
        catch (Exception ex) { throw new JournalStorageException("Required journal storage is not writable; no mutations are allowed.", ex); }
    }
    public IReadOnlyList<OrderEvidence> Load()
    {
        try
        {
            var result = new List<OrderEvidence>();
            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
            {
                var item = JsonSerializer.Deserialize<OrderEvidence>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Empty journal.");
                if (item.Version != 1 || Path.GetFileNameWithoutExtension(path) != item.Identity.Key ||
                    item.Identity.Order <= 0 || item.Identity.Event <= 0 || item.Identity.Exhibitor <= 0 ||
                    !Uri.IsWellFormedUriString(item.Identity.Endpoint, UriKind.Absolute) || string.IsNullOrWhiteSpace(item.Identity.Organization) ||
                    item.Plan.OrderNumber != item.Identity.Order || item.Plan.EventId != item.Identity.Event || item.Plan.ExhibitorId != item.Identity.Exhibitor ||
                    item.Stages.Select(x => x.Key).Distinct().Count() != item.Stages.Count ||
                    item.Stages.Any(x => !Enum.IsDefined(x.Status) || string.IsNullOrEmpty(x.Intent) || string.IsNullOrEmpty(x.Key) ||
                        x.Key != OrderIdentity.Hash(x.Operation + "\n" + x.Target) || x.Status == StageStatus.Verified && (string.IsNullOrEmpty(x.Result) || x.VerifiedAt is null)) ||
                    item.Complete && item.Stages.Any(x => x.Status != StageStatus.Verified))
                    throw new InvalidDataException($"Invalid journal: {path}");
                result.Add(item);
            }
            return result;
        }
        catch (Exception ex) { throw new JournalStorageException("Existing recovery evidence could not be loaded; no mutations are allowed.", ex); }
    }
    public void Save(OrderEvidence evidence)
    {
        var path = Path.Combine(folder, evidence.Identity.Key + ".json");
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            DurableWrite(temp, JsonSerializer.Serialize(evidence, JsonOptions));
            File.Move(temp, path, true);
        }
        catch (Exception ex) { throw new JournalStorageException("Journal persistence failed; stop all subsequent mutations. Recover from the last durable stage before continuing.", ex); }
    }
    internal static void DurableWrite(string path, string contents)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        var bytes = Encoding.UTF8.GetBytes(contents);
        stream.Write(bytes);
        stream.Flush(true);
    }
}

internal sealed class ProcessingJournal(IJournalStore store, OrderEvidence evidence, string runId)
{
    public OrderEvidence Evidence { get; } = evidence;
    public void Save() => store.Save(Evidence);
    public StageEvidence Prepare(string operation, string target, string intent, Dictionary<string, string>? source)
    {
        var key = OrderIdentity.Hash(operation + "\n" + target);
        var stage = Evidence.Stages.SingleOrDefault(x => x.Key == key);
        if (stage is not null)
        {
            if (stage.Intent != intent) throw new RecoveryReviewException($"REVIEW: intended values changed for journaled stage {operation} ({target}).");
            return stage;
        }
        stage = new StageEvidence { Key = key, Operation = operation, Target = target, Intent = intent, Source = source ?? [], Status = StageStatus.Planned };
        Evidence.Stages.Add(stage);
        Save();
        return stage;
    }
    public void Dispatching(StageEvidence stage)
    {
        if (stage.Status is not (StageStatus.Planned or StageStatus.Failed))
            throw new RecoveryReviewException($"REVIEW: {stage.Operation} is {stage.Status}; redispatch is prohibited.");
        stage.Status = StageStatus.Dispatching;
        stage.AttemptedAt = DateTimeOffset.UtcNow;
        stage.RunId = runId;
        stage.Attempts++;
        stage.Error = "";
        Save();
    }
    public void Result(StageEvidence stage, string result) { stage.Result = result; stage.Status = StageStatus.Succeeded; Save(); }
    public void Outcome(StageEvidence stage, StageStatus status, string error) { stage.Status = status; stage.Error = error; Save(); }
    public void Verified(StageEvidence stage, string verification, bool reconciled = false)
    {
        stage.Status = StageStatus.Verified; stage.Verification = verification; stage.VerifiedAt = DateTimeOffset.UtcNow;
        stage.Reconciled = reconciled; Save();
    }
    public string? CreatedAccount(string operation)
    {
        var stage = Evidence.Stages.SingleOrDefault(x => x.Operation == operation && x.Status == StageStatus.Verified);
        return stage is null ? null : Newtonsoft.Json.JsonConvert.DeserializeObject<Ungerboeck.Api.Models.Subjects.AllAccountsModel>(stage.Result)?.AccountCode;
    }
}

internal static class CanonicalState
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Kallman", "ServiceOrderEntry", "state");
    public static void Validate(CliOptions options)
    {
        if (options.Apply && !string.Equals(Path.GetFullPath(options.StateFolder).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new CliException($"Live runs must use the canonical state folder: {Folder}. Alternate --state-folder values are allowed only for preview.");
    }
}
