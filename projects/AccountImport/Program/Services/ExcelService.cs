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
        ("Bio", "Bio"),
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
        ("Bio", "Bio"),
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

        var recordsByRow = auditRecords
            .Where(r => r.RowNumber >= ColumnMap.FirstDataRow)
            .GroupBy(r => r.RowNumber)
            .ToDictionary(g => g.Key, g => g.ToList());

        var summarySheet = workbook.Worksheets.Add("Summary");
        var statusSheet = workbook.Worksheets.Add("Line Status");
        AddLineStatusSheet(statusSheet, session, recordsByRow);
        AddLineStatusSummarySheet(summarySheet, session, statusSheet);
        AddAuditSheet(workbook, "Raw Audit", auditRecords);

        workbook.SaveAs(outputWorkbookPath);
        return outputWorkbookPath;
    }

    private static void AddLineStatusSheet(
        IXLWorksheet statusSheet,
        SessionContext session,
        IReadOnlyDictionary<int, List<AuditRecord>> recordsByRow)
    {
        using var sourceWorkbook = new XLWorkbook(session.Phase0SourceFile);
        var sourceSheet = sourceWorkbook.Worksheets.First();

        int lastSourceRow = sourceSheet.LastRowUsed()?.RowNumber() ?? ColumnMap.HeaderFriendlyRow;
        int lastSourceColumn = sourceSheet.LastColumnUsed()?.ColumnNumber() ?? ColumnMap.ContactEmail;

        string[] statusHeaders =
        {
            "Source Row",
            "Status",
            "What happened",
            "Manual action needed"
        };

        for (int c = 0; c < statusHeaders.Length; c++)
        {
            statusSheet.Cell(1, c + 1).SetValue(statusHeaders[c]);
        }

        for (int sourceCol = 1; sourceCol <= lastSourceColumn; sourceCol++)
        {
            string header = GetString(sourceSheet, ColumnMap.HeaderFriendlyRow, sourceCol);
            if (string.IsNullOrWhiteSpace(header))
            {
                header = GetString(sourceSheet, ColumnMap.HeaderApiRow, sourceCol);
            }

            if (string.IsNullOrWhiteSpace(header))
            {
                header = $"Column {ColumnLetter(sourceCol)}";
            }

            statusSheet.Cell(1, statusHeaders.Length + sourceCol).SetValue(header);
        }

        int outputRow = 2;
        for (int sourceRow = ColumnMap.FirstDataRow; sourceRow <= lastSourceRow; sourceRow++)
        {
            if (IsBlankDataRow(sourceSheet, sourceRow)) continue;

            recordsByRow.TryGetValue(sourceRow, out List<AuditRecord>? rowRecords);
            LineStatus lineStatus = BuildLineStatus(rowRecords ?? new List<AuditRecord>());

            statusSheet.Cell(outputRow, 1).SetValue(sourceRow);
            statusSheet.Cell(outputRow, 2).SetValue(lineStatus.Status);
            statusSheet.Cell(outputRow, 3).SetValue(lineStatus.WhatHappened);
            statusSheet.Cell(outputRow, 4).SetValue(lineStatus.ManualAction);

            for (int sourceCol = 1; sourceCol <= lastSourceColumn; sourceCol++)
            {
                statusSheet.Cell(outputRow, statusHeaders.Length + sourceCol).Value = sourceSheet.Cell(sourceRow, sourceCol).Value;
            }

            outputRow++;
        }

        FormatLineStatusSheet(statusSheet);
    }

    private static LineStatus BuildLineStatus(IReadOnlyList<AuditRecord> records)
    {
        if (records.Count == 0)
        {
            return new LineStatus(
                "Skipped / no action",
                "No audit action was logged for this source row.",
                "Review manually if this row was expected to import.");
        }

        var seriousFailures = records
            .Where(r => IsResult(r, "Failed") && !IsAlreadyAddedAffiliation(r))
            .ToList();
        var skipped = records.Where(r => IsResult(r, "Skipped")).ToList();
        var seriousSkipped = skipped.Where(r => !IsBenignSkipped(r)).ToList();
        var successes = records.Where(r => IsResult(r, "Success")).ToList();

        string whatHappened = string.Join(Environment.NewLine, records
            .Select(DescribeAuditAction)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        if (seriousFailures.Count > 0)
        {
            string errorMessages = JoinMessages(seriousFailures.Select(r => r.ErrorMessage));
            string manualAction = seriousFailures.Any(r => r.ErrorMessage.Contains("invalid country", StringComparison.OrdinalIgnoreCase))
                ? "Fix the Momentus country/state/city mapping for this row, then rerun or update manually."
                : "Review the failure message and update manually if the row did not import.";

            if (!string.IsNullOrWhiteSpace(errorMessages))
            {
                manualAction += Environment.NewLine + Environment.NewLine + "System message: " + errorMessages;
            }

            return new LineStatus("Needs manual update", whatHappened, manualAction);
        }

        if (seriousSkipped.Count > 0)
        {
            string messages = JoinMessages(seriousSkipped.Select(r => r.ErrorMessage).Concat(seriousSkipped.Select(r => r.MomentusResponseMessage)));
            string manualAction = string.IsNullOrWhiteSpace(messages)
                ? "Review this row before rerun/manual update."
                : "Review this row before rerun/manual update. " + messages;
            return new LineStatus("Skipped / no action", whatHappened, manualAction);
        }

        if (records.Any(r =>
                IsSuccessAction(r, "CreateOrganizationAccount") ||
                IsSuccessAction(r, "CreateContact")))
        {
            return new LineStatus("Imported / created", whatHappened, "No manual update needed based on this run.");
        }

        if (records.Any(r =>
                IsSuccessAction(r, "FillBlankOrganizationFields") ||
                IsSuccessAction(r, "ApplyImportIdKeyword") ||
                IsSuccessAction(r, "AddAffiliation") ||
                IsSuccessAction(r, "SearchContactByEmail") ||
                IsSuccessAction(r, "SearchOrganizationAccount") ||
                IsAlreadyAddedAffiliation(r)))
        {
            return new LineStatus("Already existed / updated", whatHappened, "No manual update needed based on this run.");
        }

        return new LineStatus("Skipped / no action", whatHappened, "Review manually if this row was expected to import.");
    }

    private static string DescribeAuditAction(AuditRecord record)
    {
        if (IsSuccessAction(record, "SearchContactByEmail")) return "Contact already existed or was found by email.";
        if (IsSuccessAction(record, "SearchOrganizationAccount")) return "Company account already existed or was found.";
        if (IsSuccessAction(record, "CreateOrganizationAccount")) return "Company account was created.";
        if (IsResult(record, "Skipped") && record.ActionAttempted.Equals("CreateOrganizationAccount", StringComparison.OrdinalIgnoreCase))
            return "Company account creation was skipped because the run reused an account from another line.";
        if (IsSuccessAction(record, "CreateContact")) return "Contact was created.";
        if (IsSuccessAction(record, "FillBlankOrganizationFields")) return "Blank existing company fields were updated.";
        if (IsSuccessAction(record, "ApplyImportIdKeyword")) return "Import ID tag was applied.";
        if (IsSuccessAction(record, "AddAffiliation")) return "Affiliation/interest was added.";
        if (IsAlreadyAddedAffiliation(record)) return "Affiliation/interest was already on the record.";
        if (IsResult(record, "Skipped")) return "Skipped during " + record.ActionAttempted + ".";
        if (IsResult(record, "Failed")) return "Failed during " + record.ActionAttempted + ".";
        return record.ActionAttempted + ": " + record.Result;
    }

    private static void AddLineStatusSummarySheet(IXLWorksheet summarySheet, SessionContext session, IXLWorksheet statusSheet)
    {
        summarySheet.Cell(1, 1).SetValue("Import Line Status Handoff");
        summarySheet.Cell(2, 1).SetValue("Source workbook");
        summarySheet.Cell(2, 2).SetValue(session.Phase0SourceFile);
        summarySheet.Cell(3, 1).SetValue("Audit file");
        summarySheet.Cell(3, 2).SetValue(session.Phase4AuditLogFile);
        summarySheet.Cell(5, 1).SetValue("Status");
        summarySheet.Cell(5, 2).SetValue("Count");

        var counts = statusSheet.RowsUsed()
            .Skip(1)
            .Select(row => row.Cell(2).GetString())
            .Where(status => !string.IsNullOrWhiteSpace(status))
            .GroupBy(status => status, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        string[] statuses =
        {
            "Imported / created",
            "Already existed / updated",
            "Needs manual update",
            "Skipped / no action"
        };

        for (int i = 0; i < statuses.Length; i++)
        {
            summarySheet.Cell(6 + i, 1).SetValue(statuses[i]);
            summarySheet.Cell(6 + i, 2).SetValue(counts.TryGetValue(statuses[i], out int count) ? count : 0);
        }

        summarySheet.Cell(12, 1).SetValue("How to use this");
        summarySheet.Cell(13, 1).SetValue("Send the Line Status tab back to the requester. Rows marked Needs manual update or Skipped / no action need human review before rerun/manual entry.");

        summarySheet.Range("A1:B1").Merge();
        summarySheet.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(16).Font.SetFontColor(XLColor.FromHtml("#17365D"));
        summarySheet.Range("A5:B5").Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#17365D"));
        summarySheet.Range("A5:B5").Style.Font.SetFontColor(XLColor.White);
        summarySheet.Range("A13:B13").Merge();
        summarySheet.Columns().AdjustToContents();
        summarySheet.Column(2).Width = Math.Min(summarySheet.Column(2).Width, 90);
        summarySheet.Rows().Style.Alignment.WrapText = true;
    }

    private static void FormatLineStatusSheet(IXLWorksheet worksheet)
    {
        worksheet.SheetView.FreezeRows(1);
        worksheet.SheetView.FreezeColumns(4);
        worksheet.RangeUsed()?.SetAutoFilter();

        var used = worksheet.RangeUsed();
        if (used is null) return;

        used.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        used.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        used.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        used.Style.Alignment.WrapText = true;

        var header = worksheet.Row(1);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#17365D");
        header.Style.Font.FontColor = XLColor.White;
        header.Height = 30;

        worksheet.Column(1).Width = 11;
        worksheet.Column(2).Width = 24;
        worksheet.Column(3).Width = 52;
        worksheet.Column(4).Width = 68;

        int lastColumn = worksheet.LastColumnUsed()?.ColumnNumber() ?? 4;
        for (int col = 5; col <= lastColumn; col++)
        {
            worksheet.Column(col).AdjustToContents();
            worksheet.Column(col).Width = Math.Min(Math.Max(worksheet.Column(col).Width, 12), 34);
        }

        int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (int row = 2; row <= lastRow; row++)
        {
            string status = worksheet.Cell(row, 2).GetString();
            XLColor fill = status switch
            {
                "Imported / created" => XLColor.FromHtml("#E2F0D9"),
                "Already existed / updated" => XLColor.FromHtml("#DDEBF7"),
                "Needs manual update" => XLColor.FromHtml("#FCE4D6"),
                "Skipped / no action" => XLColor.FromHtml("#FFF2CC"),
                _ => XLColor.White
            };

            worksheet.Row(row).Style.Fill.BackgroundColor = fill;
            worksheet.Row(row).Height = 72;
        }
    }

    private static bool IsSuccessAction(AuditRecord record, string action) =>
        IsResult(record, "Success") &&
        record.ActionAttempted.Equals(action, StringComparison.OrdinalIgnoreCase);

    private static bool IsResult(AuditRecord record, string result) =>
        record.Result.Equals(result, StringComparison.OrdinalIgnoreCase);

    private static bool IsAlreadyAddedAffiliation(AuditRecord record) =>
        record.ActionAttempted.Equals("AddAffiliation", StringComparison.OrdinalIgnoreCase) &&
        (record.ErrorMessage.Contains("affiliation has already been added", StringComparison.OrdinalIgnoreCase) ||
         record.MomentusResponseMessage.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
         record.MomentusResponseMessage.Contains("add skipped", StringComparison.OrdinalIgnoreCase));

    private static bool IsBenignSkipped(AuditRecord record) =>
        IsAlreadyAddedAffiliation(record) ||
        (record.ActionAttempted.Equals("CreateOrganizationAccount", StringComparison.OrdinalIgnoreCase) &&
            record.MomentusResponseMessage.Contains("reused", StringComparison.OrdinalIgnoreCase)) ||
        record.MomentusResponseMessage.Contains("DRY_RUN", StringComparison.OrdinalIgnoreCase);

    private static string JoinMessages(IEnumerable<string> messages) =>
        string.Join(" | ", messages
            .Select(TextUtil.Clean)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private sealed record LineStatus(string Status, string WhatHappened, string ManualAction);

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
