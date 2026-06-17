namespace AccountImport.Models;

public sealed class RowAnnotation
{
    public string? DuplicateFound { get; set; }
    public string? AccountMatchFound { get; set; }
    public string? AccountCodeUsed { get; set; }
    public string? ImportStatus { get; set; }
    public string? ImportMessage { get; set; }
    public int? SourceRowNumber { get; set; }
    public string? SourceFileName { get; set; }
    public string? AccountCodeToWrite { get; set; }
}
