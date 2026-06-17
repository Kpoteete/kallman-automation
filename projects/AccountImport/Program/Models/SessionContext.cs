namespace AccountImport.Models;

public sealed class SessionContext
{
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _accountCodesTouched = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _contactCodesCreated = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _duplicateContactCodesFound = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _createdAccountCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _matchedAccountCodes = new(StringComparer.OrdinalIgnoreCase);

    public DateTime StartedAt { get; } = DateTime.Now;
    public string Timestamp => StartedAt.ToString("yyyy-MM-dd_HH-mm-ss");
    public ImportSummary Summary { get; } = new();
    public string RunTag { get; set; } = string.Empty;

    public string Phase0SourceFile { get; set; } = string.Empty;
    public string Phase1DuplicateFile { get; set; } = string.Empty;
    public string Phase1NonDuplicateFile { get; set; } = string.Empty;
    public string Phase1ReviewRequiredFile { get; set; } = string.Empty;
    public string Phase2ExistingAccountsFile { get; set; } = string.Empty;
    public string Phase2NewAccountsFile { get; set; } = string.Empty;
    public string Phase2ReviewRequiredFile { get; set; } = string.Empty;
    public string Phase4AuditLogFile { get; set; } = string.Empty;
    public string Phase4ExistingAccountsFinalFile { get; set; } = string.Empty;
    public string Phase4NewAccountsFinalFile { get; set; } = string.Empty;
    public string TeamUpdateWorkbookFile { get; set; } = string.Empty;
    public string ArchiveFolder { get; set; } = string.Empty;

    public IReadOnlyCollection<string> FilesUsedOrCreated => _files;
    public IReadOnlyCollection<string> AccountCodesTouched => _accountCodesTouched;
    public IReadOnlyCollection<string> ContactCodesCreated => _contactCodesCreated;
    public IReadOnlyCollection<string> DuplicateContactCodesFound => _duplicateContactCodesFound;
    public IReadOnlyCollection<string> CreatedAccountCodes => _createdAccountCodes;
    public IReadOnlyCollection<string> MatchedAccountCodes => _matchedAccountCodes;

    public void TrackFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            _files.Add(Path.GetFullPath(path));
        }
    }

    public void TrackTouchedAccount(string? accountCode, bool created)
    {
        string clean = TextUtil.CleanKeyField(accountCode);
        if (string.IsNullOrWhiteSpace(clean)) return;
        _accountCodesTouched.Add(clean);
        if (created) _createdAccountCodes.Add(clean);
        else _matchedAccountCodes.Add(clean);
    }

    public void TrackCreatedContact(string? accountCode)
    {
        string clean = TextUtil.CleanKeyField(accountCode);
        if (!string.IsNullOrWhiteSpace(clean)) _contactCodesCreated.Add(clean);
    }

    public void TrackDuplicateContact(string? accountCode)
    {
        string clean = TextUtil.CleanKeyField(accountCode);
        if (!string.IsNullOrWhiteSpace(clean)) _duplicateContactCodesFound.Add(clean);
    }
}
