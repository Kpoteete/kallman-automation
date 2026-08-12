internal sealed class ComparisonResult
{
    public List<Dictionary<string, string>> NewRows { get; set; } = new();
    public List<Dictionary<string, string>> ChangedRows { get; set; } = new();
}
