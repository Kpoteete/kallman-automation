using AccountImport.Models;
using ClosedXML.Excel;

namespace AccountImport.Services;

public sealed class ExcelService
{
    private static readonly (string Friendly, string Api)[] AccountFieldMappings =
    {
        ("Company Name", "Company"),
        ("Company Website", "Website"),
        ("Account email", "Email"),
        ("Account phone", "Phone"),
        ("Account Address", "Address1"),
        ("Account Postal Code", "PostalCode"),
        ("Company Event Sales Status (P unless account in momentus is A)", "EventSalesStatus"),
        ("Type Code", "Type"),
        ("Company Market Segment major Code", "MarketSegmentMajor"),
        ("Company Market Segment minor Code", "MarketSegmentMinor"),
        ("Company Market Segment Major Code", "MarketSegmentMajor"),
        ("Company Market Segment Minor Code", "MarketSegmentMinor"),
        ("Market Segment Major Code", "MarketSegmentMajor"),
        ("Market Segment Minor Code", "MarketSegmentMinor"),
        ("Account rep Code", "AccountRep"),
        ("Country Code", "Country"),
        ("State Code", "State"),
        ("City Code", "City")
    };

    private static readonly (string Friendly, string Api)[] ContactFieldMappings =
    {
        // Deliberately doubled from the company/account section.
        // The template has one Type Code column, but Momentus also accepts Type on individual/contact accounts.
        // This lets the same import Type populate both the organization account and the contact account.
        ("Type Code", "Type"),
        // Deliberately doubled from the company/account section.
        // Market Segment Major/Minor should be applied to both organization accounts and contact accounts.
        ("Company Market Segment major Code", "MarketSegmentMajor"),
        ("Company Market Segment minor Code", "MarketSegmentMinor"),
        ("Company Market Segment Major Code", "MarketSegmentMajor"),
        ("Company Market Segment Minor Code", "MarketSegmentMinor"),
        ("Market Segment Major Code", "MarketSegmentMajor"),
        ("Market Segment Minor Code", "MarketSegmentMinor"),
        ("Contact First Name", "FirstName"),
        ("Contact Last Name", "LastName"),
        ("Contact event Sales", "EventSalesStatus"),
        ("Contact Title", "Title"),
        ("Contact Phone", "Phone"),
        ("Contact Mobile Phone", "Mobile"),
        ("Contact Email", "Email"),
        ("Contact Address", "Address1"),
        ("Contact Postal Code", "PostalCode"),
        ("Contact Country Code", "Country"),
        ("Contact State Code", "State"),
        ("Contact City Code", "City")
    };

    public string GetSinglePhase0File(string phase0Folder)
    {
        if (!Directory.Exists(phase0Folder))
            throw new DirectoryNotFoundException($"Phase 0 folder not found: {phase0Folder}");

        var files = Directory.GetFiles(phase0Folder, "*.xlsx", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0)
            throw new InvalidOperationException($"No .xlsx file was found in Phase 0: {phase0Folder}");

        if (files.Count > 1)
            throw new InvalidOperationException(
                "Phase 0 must contain exactly one Excel import file. Found: " +
                string.Join(", ", files.Select(Path.GetFileName)));

        return files[0];
    }

    public void ValidateWorkbook(string workbookPath)
    {
        using var workbook = new XLWorkbook(workbookPath);
        var worksheet = workbook.Worksheets.FirstOrDefault()
            ?? throw new InvalidOperationException("Workbook has no worksheets.");

        if (worksheet.LastRowUsed()?.RowNumber() < ColumnMap.HeaderFriendlyRow)
            throw new InvalidOperationException("Workbook must have two header rows. Row 1 = API field names; Row 2 = friendly names.");

        RequireFriendlyHeader(worksheet, "Company Name");
        RequireFriendlyHeader(worksheet, "Company Website");
        RequireFriendlyHeader(worksheet, "Contact Email");
        RequireFriendlyHeader(worksheet, "Account Code (if account already exists)");
        RequireFriendlyHeader(worksheet, "Company Market Segment major Code");
        RequireFriendlyHeader(worksheet, "Country Code");
    }

    public List<ImportRow> LoadRows(string workbookPath)
    {
        using var workbook = new XLWorkbook(workbookPath);
        var worksheet = workbook.Worksheets.First();
        EnsureOutputHeaders(worksheet);

        string sourceFileName = Path.GetFileName(workbookPath);
        int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? ColumnMap.HeaderFriendlyRow;

        int companyCol = FindColumnByFriendlyHeader(worksheet, "Company Name");
        int accountCodeCol = FindColumnByFriendlyHeader(worksheet, "Account Code (if account already exists)");
        int segmentCol = FindColumnByFriendlyHeader(worksheet, "Company Market Segment major Code", "Company Market Segment");
        int countryCol = FindColumnByFriendlyHeader(worksheet, "Country Code", "Account Country");
        int websiteCol = FindColumnByFriendlyHeader(worksheet, "Company Website");
        int contactEmailCol = FindColumnByFriendlyHeader(worksheet, "Contact Email");

        if (companyCol == 0 || accountCodeCol == 0 || segmentCol == 0 || countryCol == 0 || contactEmailCol == 0)
        {
            throw new InvalidOperationException("The workbook is missing one or more required friendly headers in Row 2. Required: Company Name, Contact Email, Account Code (if account already exists), Company Market Segment major Code, Country Code.");
        }

        var rows = new List<ImportRow>();
        for (int rowNumber = ColumnMap.FirstDataRow; rowNumber <= lastRow; rowNumber++)
        {
            if (IsBlankDataRow(worksheet, rowNumber)) continue;

            int sourceRowNumber = TryGetInt(worksheet, rowNumber, ColumnMap.SourceRowNumber) ?? rowNumber;
            string sourceFile = GetString(worksheet, rowNumber, ColumnMap.SourceFileName);
            if (string.IsNullOrWhiteSpace(sourceFile)) sourceFile = sourceFileName;

            rows.Add(new ImportRow(
                WorksheetRowNumber: rowNumber,
                SourceRowNumber: sourceRowNumber,
                SourceFileName: sourceFile,
                CompanyName: GetString(worksheet, rowNumber, companyCol),
                AccountCode: GetString(worksheet, rowNumber, accountCodeCol),
                MarketSegmentMajor: GetString(worksheet, rowNumber, segmentCol),
                Country: GetString(worksheet, rowNumber, countryCol),
                WebsiteRootDomain: websiteCol > 0 ? TextUtil.RootDomain(GetString(worksheet, rowNumber, websiteCol)) : string.Empty,
                ContactEmail: GetString(worksheet, rowNumber, contactEmailCol)));
        }

        return rows;
    }

    public IReadOnlyDictionary<string, string> ReadAccountFields(string workbookPath, int worksheetRowNumber)
    {
        using var workbook = new XLWorkbook(workbookPath);
        var worksheet = workbook.Worksheets.First();
        return ReadMappedFields(worksheet, worksheetRowNumber, AccountFieldMappings);
    }

    public IReadOnlyDictionary<string, string> ReadAccountFields(IXLWorksheet worksheet, int worksheetRowNumber)
    {
        return ReadMappedFields(worksheet, worksheetRowNumber, AccountFieldMappings);
    }

    public IReadOnlyDictionary<string, string> ReadContactFields(string workbookPath, int worksheetRowNumber)
    {
        using var workbook = new XLWorkbook(workbookPath);
        var worksheet = workbook.Worksheets.First();
        return ReadMappedFields(worksheet, worksheetRowNumber, ContactFieldMappings);
    }

    public IReadOnlyDictionary<string, string> ReadContactFields(IXLWorksheet worksheet, int worksheetRowNumber)
    {
        return ReadMappedFields(worksheet, worksheetRowNumber, ContactFieldMappings);
    }

    private static IReadOnlyDictionary<string, string> ReadMappedFields(IXLWorksheet worksheet, int worksheetRowNumber, IReadOnlyList<(string Friendly, string Api)> mappings)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in mappings)
        {
            int col = FindColumnByFriendlyHeader(worksheet, mapping.Friendly);
            if (col == 0) continue;

            string value = GetString(worksheet, worksheetRowNumber, col);
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (IsPlaceholderValue(value)) continue;

            fields[mapping.Api] = value;
        }

        return fields;
    }

    private static bool IsPlaceholderValue(string value)
    {
        string clean = TextUtil.CleanKeyField(value);
        return TextUtil.EqualsTrimmedIgnoreCase(clean, "NA") ||
               TextUtil.EqualsTrimmedIgnoreCase(clean, "N/A");
    }

    public string CreateFilteredCopy(
        string sourceWorkbookPath,
        string outputWorkbookPath,
        IReadOnlyCollection<int> worksheetRowsToKeep,
        IReadOnlyDictionary<int, RowAnnotation> annotations)
    {
        using var workbook = new XLWorkbook(sourceWorkbookPath);
        var worksheet = workbook.Worksheets.First();
        EnsureOutputHeaders(worksheet);

        int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? ColumnMap.HeaderFriendlyRow;
        var keep = new HashSet<int>(worksheetRowsToKeep);

        for (int rowNumber = ColumnMap.FirstDataRow; rowNumber <= lastRow; rowNumber++)
        {
            if (annotations.TryGetValue(rowNumber, out RowAnnotation? annotation))
            {
                ApplyAnnotation(worksheet, rowNumber, annotation);
            }
        }

        for (int rowNumber = lastRow; rowNumber >= ColumnMap.FirstDataRow; rowNumber--)
        {
            if (!keep.Contains(rowNumber))
            {
                worksheet.Row(rowNumber).Delete();
            }
        }

        RemoveWorksheetTables(worksheet);

        Directory.CreateDirectory(Path.GetDirectoryName(outputWorkbookPath)!);
        workbook.SaveAs(outputWorkbookPath);
        return outputWorkbookPath;
    }

    public string CreateAnnotatedCopy(
        string sourceWorkbookPath,
        string outputWorkbookPath,
        IReadOnlyDictionary<int, RowAnnotation> annotations)
    {
        using var workbook = new XLWorkbook(sourceWorkbookPath);
        var worksheet = workbook.Worksheets.First();
        EnsureOutputHeaders(worksheet);

        foreach (var kvp in annotations)
        {
            ApplyAnnotation(worksheet, kvp.Key, kvp.Value);
        }

        RemoveWorksheetTables(worksheet);

        Directory.CreateDirectory(Path.GetDirectoryName(outputWorkbookPath)!);
        workbook.SaveAs(outputWorkbookPath);
        return outputWorkbookPath;
    }

    private static void RemoveWorksheetTables(IXLWorksheet worksheet)
    {
        var tableNames = worksheet.Tables.Select(table => table.Name).ToList();
        foreach (string tableName in tableNames)
        {
            var table = worksheet.Table(tableName);
            var unlistMethod = table.GetType().GetMethod("Unlist", Type.EmptyTypes);
            if (unlistMethod != null)
            {
                unlistMethod.Invoke(table, null);
                continue;
            }

            table.Delete(XLShiftDeletedCells.ShiftCellsUp);
        }
    }

    public string CreateTeamUpdateWorkbook(string outputWorkbookPath, SessionContext session, IReadOnlyList<AuditRecord> auditRecords)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputWorkbookPath)!);
        using var workbook = new XLWorkbook();

        var createdAccountCodes = new HashSet<string>(session.CreatedAccountCodes, StringComparer.OrdinalIgnoreCase);
        var matchedAccountCodes = new HashSet<string>(session.MatchedAccountCodes, StringComparer.OrdinalIgnoreCase);
        var createdContactCodes = new HashSet<string>(session.ContactCodesCreated, StringComparer.OrdinalIgnoreCase);
        var duplicateContactCodes = new HashSet<string>(session.DuplicateContactCodesFound, StringComparer.OrdinalIgnoreCase);

        bool IsCreatedAccount(AuditRecord r) => !string.IsNullOrWhiteSpace(r.AccountCode) && createdAccountCodes.Contains(r.AccountCode);
        bool IsMatchedAccount(AuditRecord r) => !string.IsNullOrWhiteSpace(r.AccountCode) && matchedAccountCodes.Contains(r.AccountCode);
        bool IsCreatedContact(AuditRecord r) => !string.IsNullOrWhiteSpace(r.AccountCode) && createdContactCodes.Contains(r.AccountCode);
        bool IsExistingContact(AuditRecord r) => !string.IsNullOrWhiteSpace(r.AccountCode) && duplicateContactCodes.Contains(r.AccountCode);

        var accountsImported = auditRecords
            .Where(r => r.ActionAttempted == "CreateOrganizationAccount" &&
                        r.Result.Equals("Success", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var accountsChanged = auditRecords
            .Where(r => r.Result.Equals("Success", StringComparison.OrdinalIgnoreCase))
            .Where(r =>
                r.ActionAttempted == "FillBlankOrganizationFields" ||
                ((r.ActionAttempted == "ApplyImportIdKeyword" || r.ActionAttempted == "AddAffiliation") &&
                    IsMatchedAccount(r) && !IsCreatedAccount(r)))
            .ToList();

        var accountsChangedCodes = new HashSet<string>(accountsChanged
            .Select(r => r.AccountCode)
            .Where(code => !string.IsNullOrWhiteSpace(code)), StringComparer.OrdinalIgnoreCase);

        var accountsNotImported = auditRecords
            .Where(r =>
                (r.ActionAttempted == "SearchOrganizationAccount" &&
                    (
                        r.Result.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                        r.Result.Equals("Skipped", StringComparison.OrdinalIgnoreCase) ||
                        (r.Result.Equals("Success", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(r.AccountCode) &&
                            !accountsChangedCodes.Contains(r.AccountCode))
                    )) ||
                (r.ActionAttempted == "CreateOrganizationAccount" &&
                    !r.Result.Equals("Success", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var contactsImported = auditRecords
            .Where(r => r.ActionAttempted == "CreateContact" &&
                        r.Result.Equals("Success", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var contactsChanged = auditRecords
            .Where(r => r.Result.Equals("Success", StringComparison.OrdinalIgnoreCase))
            .Where(r =>
                (r.ActionAttempted == "ApplyImportIdKeyword" || r.ActionAttempted == "AddAffiliation") &&
                IsExistingContact(r) && !IsCreatedContact(r))
            .ToList();

        var contactsChangedCodes = new HashSet<string>(contactsChanged
            .Select(r => r.AccountCode)
            .Where(code => !string.IsNullOrWhiteSpace(code)), StringComparer.OrdinalIgnoreCase);

        var contactsNotImported = auditRecords
            .Where(r =>
                (r.ActionAttempted == "SearchContactByEmail" &&
                    (
                        r.Result.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                        r.Result.Equals("Skipped", StringComparison.OrdinalIgnoreCase) ||
                        (r.Result.Equals("Success", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(r.AccountCode) &&
                            !contactsChangedCodes.Contains(r.AccountCode))
                    )) ||
                (r.ActionAttempted == "CreateContact" &&
                    !r.Result.Equals("Success", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        AddAuditSheet(workbook, "Accounts Imported", accountsImported);
        AddAuditSheet(workbook, "Accounts Changed", accountsChanged);
        AddAuditSheet(workbook, "Accounts Not Imported", accountsNotImported);
        AddAuditSheet(workbook, "Contacts Imported", contactsImported);
        AddAuditSheet(workbook, "Contacts Changed", contactsChanged);
        AddAuditSheet(workbook, "Contacts Not Imported", contactsNotImported);

        workbook.SaveAs(outputWorkbookPath);
        return outputWorkbookPath;
    }

    private static void AddAuditSheet(XLWorkbook workbook, string sheetName, IReadOnlyList<AuditRecord> records)
    {
        var ws = workbook.Worksheets.Add(sheetName);
        string[] headers =
        {
            "Timestamp", "Phase", "Result", "Action", "AccountCode", "CompanyName", "ContactEmail",
            "RowNumber", "Message", "Error"
        };

        for (int c = 0; c < headers.Length; c++)
        {
            ws.Cell(1, c + 1).SetValue(headers[c]);
            ws.Cell(1, c + 1).Style.Font.Bold = true;
        }

        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            int row = i + 2;
            ws.Cell(row, 1).SetValue(r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
            ws.Cell(row, 2).SetValue(r.Phase);
            ws.Cell(row, 3).SetValue(r.Result);
            ws.Cell(row, 4).SetValue(r.ActionAttempted);
            ws.Cell(row, 5).SetValue(r.AccountCode);
            ws.Cell(row, 6).SetValue(r.CompanyName);
            ws.Cell(row, 7).SetValue(r.ContactEmail);
            ws.Cell(row, 8).SetValue(r.RowNumber);
            ws.Cell(row, 9).SetValue(TextUtil.Shorten(r.MomentusResponseMessage, 1000));
            ws.Cell(row, 10).SetValue(TextUtil.Shorten(r.ErrorMessage, 1000));
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
    }

    public static string TimestampedPath(string folder, string prefix, string timestamp, string extension = ".xlsx")
    {
        Directory.CreateDirectory(folder);
        string fileName = $"{prefix}_{timestamp}{extension}";
        return Path.Combine(folder, fileName);
    }

    public void EnsureOutputHeaders(string workbookPath)
    {
        using var workbook = new XLWorkbook(workbookPath);
        var worksheet = workbook.Worksheets.First();
        EnsureOutputHeaders(worksheet);
        workbook.SaveAs(workbookPath);
    }

    public static string ParseApiFieldHeader(string? rawHeader)
    {
        string text = TextUtil.Clean(rawHeader);
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        text = text.Trim().Trim(',').Trim();
        if (string.Equals(text, "na", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "n/a", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "NA", StringComparison.OrdinalIgnoreCase))
        {
            return "na";
        }

        int colonIndex = text.IndexOf(':');
        if (colonIndex >= 0)
        {
            text = text.Substring(0, colonIndex);
        }

        text = text.Trim().Trim('"').Trim('\'').Trim();

        // Handle malformed template header like Type": "string", where the leading quote is missing.
        text = text.Trim().Trim('"').Trim('\'').Trim();

        return text;
    }

    private static void EnsureOutputHeaders(IXLWorksheet worksheet)
    {
        EnsureHeader(worksheet, ColumnMap.DuplicateFlag, "DuplicateFound", "Duplicate Found");
        EnsureHeader(worksheet, ColumnMap.AccountMatchFound, "AccountMatchFound", "Account Match Found");
        EnsureHeader(worksheet, ColumnMap.AccountCodeUsed, "AccountCodeUsed", "Account Code Used");
        EnsureHeader(worksheet, ColumnMap.ImportStatus, "ImportStatus", "Import Status");
        EnsureHeader(worksheet, ColumnMap.ImportMessage, "ImportMessage", "Import Message");
        EnsureHeader(worksheet, ColumnMap.SourceRowNumber, "SourceRowNumber", "Source Row Number");
        EnsureHeader(worksheet, ColumnMap.SourceFileName, "SourceFileName", "Source File Name");
    }

    private static void EnsureHeader(IXLWorksheet worksheet, int col, string apiHeader, string friendlyHeader)
    {
        if (string.IsNullOrWhiteSpace(GetString(worksheet, ColumnMap.HeaderApiRow, col)))
            worksheet.Cell(ColumnMap.HeaderApiRow, col).SetValue(apiHeader);

        if (string.IsNullOrWhiteSpace(GetString(worksheet, ColumnMap.HeaderFriendlyRow, col)))
            worksheet.Cell(ColumnMap.HeaderFriendlyRow, col).SetValue(friendlyHeader);
    }

    private static void ApplyAnnotation(IXLWorksheet worksheet, int rowNumber, RowAnnotation annotation)
    {
        if (annotation.AccountCodeToWrite is not null)
            worksheet.Cell(rowNumber, ColumnMap.AccountCode).SetValue(annotation.AccountCodeToWrite);

        if (annotation.DuplicateFound is not null)
            worksheet.Cell(rowNumber, ColumnMap.DuplicateFlag).SetValue(annotation.DuplicateFound);

        if (annotation.AccountMatchFound is not null)
            worksheet.Cell(rowNumber, ColumnMap.AccountMatchFound).SetValue(annotation.AccountMatchFound);

        if (annotation.AccountCodeUsed is not null)
            worksheet.Cell(rowNumber, ColumnMap.AccountCodeUsed).SetValue(annotation.AccountCodeUsed);

        if (annotation.ImportStatus is not null)
            worksheet.Cell(rowNumber, ColumnMap.ImportStatus).SetValue(annotation.ImportStatus);

        if (annotation.ImportMessage is not null)
            worksheet.Cell(rowNumber, ColumnMap.ImportMessage).SetValue(TextUtil.Shorten(annotation.ImportMessage, 500));

        if (annotation.SourceRowNumber is not null)
            worksheet.Cell(rowNumber, ColumnMap.SourceRowNumber).SetValue(annotation.SourceRowNumber.Value);

        if (annotation.SourceFileName is not null)
            worksheet.Cell(rowNumber, ColumnMap.SourceFileName).SetValue(annotation.SourceFileName);
    }


    private static int FindColumnByFriendlyHeader(IXLWorksheet worksheet, params string[] possibleFriendlyHeaders)
    {
        int lastColumn = worksheet.LastColumnUsed()?.ColumnNumber() ?? ColumnMap.SourceFileName;
        var possible = possibleFriendlyHeaders
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(NormalizeHeader)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int col = 1; col <= lastColumn; col++)
        {
            string friendlyHeader = NormalizeHeader(GetString(worksheet, ColumnMap.HeaderFriendlyRow, col));
            if (possible.Contains(friendlyHeader)) return col;
        }

        return 0;
    }

    private static void RequireFriendlyHeader(IXLWorksheet worksheet, string friendlyHeader)
    {
        if (FindColumnByFriendlyHeader(worksheet, friendlyHeader) == 0)
        {
            throw new InvalidOperationException($"Required friendly header '{friendlyHeader}' was not found in Row 2.");
        }
    }

    private static bool IsBlankDataRow(IXLWorksheet worksheet, int rowNumber)
    {
        int lastColumn = Math.Max(worksheet.LastColumnUsed()?.ColumnNumber() ?? ColumnMap.SourceFileName, ColumnMap.SourceFileName);
        for (int col = 1; col <= lastColumn; col++)
        {
            // Ignore output/status columns when deciding whether the source data row is blank.
            if (col >= ColumnMap.AccountMatchFound && col <= ColumnMap.SourceFileName) continue;
            if (!string.IsNullOrWhiteSpace(GetString(worksheet, rowNumber, col))) return false;
        }
        return true;
    }

    private static string NormalizeHeader(string? value)
    {
        string text = TextUtil.Clean(value).ToLowerInvariant();
        text = text.Replace("\u00a0", " ");
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ");
        return text.Trim();
    }

    private static string GetString(IXLWorksheet worksheet, int row, int col)
    {
        return TextUtil.Clean(worksheet.Cell(row, col).GetString());
    }

    private static int? TryGetInt(IXLWorksheet worksheet, int row, int col)
    {
        var cell = worksheet.Cell(row, col);
        if (cell.IsEmpty()) return null;
        if (cell.TryGetValue<int>(out int value)) return value;
        return int.TryParse(cell.GetString(), out int parsed) ? parsed : null;
    }

    private static string ColumnLetter(int columnNumber)
    {
        string columnName = string.Empty;
        while (columnNumber > 0)
        {
            int modulo = (columnNumber - 1) % 26;
            columnName = Convert.ToChar('A' + modulo) + columnName;
            columnNumber = (columnNumber - modulo) / 26;
        }
        return columnName;
    }
}
