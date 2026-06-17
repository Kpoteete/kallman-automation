namespace AccountImport.Models;

public sealed class AuditRecord
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Phase { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public string SourceFileName { get; set; } = string.Empty;
    public int RowNumber { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string ActionAttempted { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty; // Success, Skipped, Failed
    public string AccountCode { get; set; } = string.Empty;
    public string MomentusResponseMessage { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}
