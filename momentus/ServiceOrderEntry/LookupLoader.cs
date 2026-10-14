using ClosedXML.Excel;

namespace ServiceOrderEntry;

internal static class LookupLoader
{
    public static IReadOnlyList<SalesRepLookup> LoadSalesReps(string path)
    {
        using var workbook = new XLWorkbook(path);
        var rows = workbook.Worksheet(1).RangeUsed()?.RowsUsed().Skip(1) ?? [];
        var result = rows.Select(r => new SalesRepLookup(r.Cell(1).GetString().Trim(), r.Cell(2).GetString().Trim(), r.Cell(3).GetString().Trim()))
            .Where(x => x.DisplayName.Length > 0 || x.UdfCode.Length > 0).ToList();
        if (result.Count == 0) throw new InvalidDataException($"No sales-rep mappings found in {path}.");
        if (result.Any(x => !BillingRules.ValidCode(x.AccountCode))) throw new InvalidDataException("Sales-rep lookup contains a missing/invalid AccountCode.");
        var ambiguous = result.SelectMany(x => new[] { (Key: x.DisplayName, x.AccountCode), (Key: x.UdfCode, x.AccountCode) }).Where(x => x.Key.Length > 0)
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(x => x.AccountCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1).Select(g => g.Key).ToList();
        if (ambiguous.Count > 0) throw new InvalidDataException($"Duplicate sales-rep lookup keys: {string.Join(", ", ambiguous)}.");
        return result;
    }

    public static IReadOnlyList<CategoryLookup> LoadCategories(string path)
    {
        using var workbook = new XLWorkbook(path);
        var result = new List<CategoryLookup>();
        foreach (var row in workbook.Worksheet(1).RangeUsed()?.RowsUsed().Skip(1) ?? [])
        {
            if (row.Cells(1, 3).All(x => x.IsEmpty())) continue;
            if (!int.TryParse(row.Cell(2).GetString(), out var sequence) || sequence <= 0)
                throw new InvalidDataException($"Invalid category identifier in {path}, row {row.RowNumber()}.");
            var identifiers = row.Cell(3).GetString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (identifiers.Length > 0) result.Add(new CategoryLookup(row.Cell(1).GetString().Trim(), sequence, identifiers));
        }
        if (result.Count == 0) throw new InvalidDataException($"No category rows with matching identifiers found in {path}.");
        if (result.GroupBy(x => x.Sequence).Any(x => x.Count() > 1)) throw new InvalidDataException("Category lookup contains duplicate identifiers.");
        return result;
    }
}
