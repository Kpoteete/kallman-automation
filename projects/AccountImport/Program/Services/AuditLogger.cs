using AccountImport.Models;

namespace AccountImport.Services;

public sealed class AuditLogger
{
    private readonly string _auditPath;
    private readonly object _lock = new();
    private readonly List<AuditRecord> _records = new();
    private bool _headerWritten;

    public AuditLogger(string auditPath)
    {
        _auditPath = auditPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_auditPath)!);
        if (File.Exists(_auditPath)) File.Delete(_auditPath);
    }

    public string AuditPath => _auditPath;
    public IReadOnlyList<AuditRecord> Records
    {
        get
        {
            lock (_lock) return _records.ToList();
        }
    }

    public void Log(AuditRecord record)
    {
        lock (_lock)
        {
            _records.Add(record);
            using var writer = new StreamWriter(_auditPath, append: true);
            if (!_headerWritten)
            {
                writer.WriteLine(string.Join(",", new[]
                {
                    "Timestamp",
                    "Phase",
                    "DryRun",
                    "SourceFileName",
                    "RowNumber",
                    "CompanyName",
                    "ContactEmail",
                    "ActionAttempted",
                    "Result",
                    "AccountCode",
                    "MomentusResponseMessage",
                    "ErrorMessage"
                }.Select(CsvEscape)));
                _headerWritten = true;
            }

            writer.WriteLine(string.Join(",", new[]
            {
                record.Timestamp.ToString("O"),
                record.Phase,
                record.DryRun ? "TRUE" : "FALSE",
                record.SourceFileName,
                record.RowNumber.ToString(),
                record.CompanyName,
                record.ContactEmail,
                record.ActionAttempted,
                record.Result,
                record.AccountCode,
                record.MomentusResponseMessage,
                record.ErrorMessage
            }.Select(CsvEscape)));
        }
    }

    private static string CsvEscape(string? value)
    {
        string text = value ?? string.Empty;
        if (text.Contains('"')) text = text.Replace("\"", "\"\"");
        if (text.Contains(',') || text.Contains('\n') || text.Contains('\r') || text.Contains('"'))
            return $"\"{text}\"";
        return text;
    }
}
