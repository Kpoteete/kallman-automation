using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;

class Program
{
    // ============================================================
    // CONFIG
    // ============================================================

    private static readonly string DefaultDataWarehouseFolder =
        @"C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents";

    private static string DataWarehouseFolder =>
        FirstNonBlank(Environment.GetEnvironmentVariable("REGISTRATION_DATA_WAREHOUSE_FOLDER") ?? "", DefaultDataWarehouseFolder);

    private static readonly string EventsFileName = "Events_Pull.csv";
    private static readonly string ExhibitorsFileName = "Exhibitor_Pull.csv";
    private static readonly string AccountsFileName = "Accounts_Pull.xlsx";
    private static readonly string ExhibitorCategoriesFileName = "CategoriesExhibitors.csv";

    private static readonly string OutputRootFolderName = "Registration Lists";
    private const string GlobalRunLockFileName = "RegistrationListAutomation.lock";

    private const int MonthsAhead = 13;
    private static readonly bool FlagMissingKeyData = true;

    private const string DashboardSheetName = "Date and Time Picker";
    private const string CurrentRegistrationSheetName = "Current Registration List";
    private const string PreviousRegistrationSheetName = "Last Registration List";
    private const string PullDataSheetName = "data";
    private const string NewSinceSelectedDateSheetName = "New Since Date";
    private const string ChangedSinceSelectedDateSheetName = "Changes Since Date";
    private const string RunInfoSheetName = "About This File";
    private const string DataQualitySheetName = "Data Quality";

    private const string CurrentDataSheetName = "_CurrentData";
    private const string ExhibitorHistorySheetName = "_ExhibitorHistory";
    private const string ChangeHistorySheetName = "_ChangeHistory";
    private const string FieldMapSheetName = "_FieldMap";

    private const string CurrentDataTableName = "tblCurrentData";
    private const string CurrentRegistrationTableName = "tblCurrentRegistration";
    private const string PullDataTableName = "tblData";
    private const string ExhibitorHistoryTableName = "tblExhibitorHistory";
    private const string ChangeHistoryTableName = "tblChangeHistory";
    private const string FieldMapTableName = "tblFieldMap";
    private const string PullDateTimeColumnName = "PullDateTime";

    private const int DashboardCompareDateRow = 7;
    private const int DashboardCompareDateColumn = 2;
    private static readonly string CompareDateCellReference = $"'{DashboardSheetName}'!$B${DashboardCompareDateRow}";

    private static readonly HashSet<string> ActiveExhibitorStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "2",
        "10",
        "22"
    };

    private static readonly List<string> CleanOutputColumns = new()
    {
        "BoothNumber",
        "CompanyName",
        "CompanyBannerName",
        "ExhibitorStatus",
        "OrderedArea",
        "ExhibitorCategory",
        "ExhibitorType",
        "MainExhibitorCompany",
        "OrderedLength",
        "OrderedWidth",

        "CompanyAddress1",
        "CompanyCity",
        "CompanyState",
        "CompanyPostalCode",

        "CatalogAddress",
        "CatalogCity",
        "CatalogState",
        "CatalogPostalCode",
        "CatalogCountryName",
        "ExhibitorWebsite",

        "MainContactFirstName",
        "MainContactLastName",
        "MainContactEmail",
        "MainContactPhone",
        "MainContactMobile",
        "MainContactTitle",

        "BoothContactFirstName",
        "BoothContactLastName",
        "BoothContactEmail",
        "BoothContactPhone",
        "BoothContactMobile",
        "BoothContactTitle",

        "PressContactFirstName",
        "PressContactLastName",
        "PressContactEmail",
        "PressContactPhone",
        "PressContactMobile",
        "PressContactTitle"
    };

    private static readonly HashSet<string> MissingDataWatchColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "BoothNumber",
        "CompanyName",
        "CompanyBannerName",
        "MainContactFirstName",
        "MainContactLastName",
        "MainContactEmail"
    };

    // Only user-facing registration-list fields are compared for business-visible change tracking.
    // This prevents noisy false positives from internal helper/audit fields such as account ChangedOn values.
    private static readonly HashSet<string> ComparableSnapshotColumns =
        CleanOutputColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> DisplayColumnNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BoothNumber"] = "Booth Number",
        ["CompanyName"] = "Company Name",
        ["CompanyBannerName"] = "Exhibiting As Name",
        ["ExhibitorStatus"] = "Status",
        ["OrderedArea"] = "Area",
        ["ExhibitorCategory"] = "Category",
        ["ExhibitorType"] = "Type",
        ["MainExhibitorCompany"] = "Main Exhibitor",
        ["OrderedLength"] = "Booth Length",
        ["OrderedWidth"] = "Booth Width",

        ["CompanyAddress1"] = "Company Address 1",
        ["CompanyCity"] = "Company City",
        ["CompanyState"] = "Company State",
        ["CompanyPostalCode"] = "Company Postal Code",

        ["CatalogAddress"] = "Catalog Address",
        ["CatalogCity"] = "Catalog City",
        ["CatalogState"] = "Catalog State",
        ["CatalogPostalCode"] = "Catalog Postal Code",
        ["CatalogCountryName"] = "Catalog Country",
        ["ExhibitorWebsite"] = "Exhibitor Website",

        ["MainContactFirstName"] = "Main Contact First Name",
        ["MainContactLastName"] = "Main Contact Last Name",
        ["MainContactEmail"] = "Main Contact Email",
        ["MainContactPhone"] = "Main Contact Phone",
        ["MainContactMobile"] = "Main Contact Mobile",
        ["MainContactTitle"] = "Main Contact Title",

        ["BoothContactFirstName"] = "Booth Contact First Name",
        ["BoothContactLastName"] = "Booth Contact Last Name",
        ["BoothContactEmail"] = "Booth Contact Email",
        ["BoothContactPhone"] = "Booth Contact Phone",
        ["BoothContactMobile"] = "Booth Contact Mobile",
        ["BoothContactTitle"] = "Booth Contact Title",

        ["PressContactFirstName"] = "Press Contact First Name",
        ["PressContactLastName"] = "Press Contact Last Name",
        ["PressContactEmail"] = "Press Contact Email",
        ["PressContactPhone"] = "Press Contact Phone",
        ["PressContactMobile"] = "Press Contact Mobile",
        ["PressContactTitle"] = "Press Contact Title",

        ["RowKey"] = "Row Key",
        ["ChangeKey"] = "Change Key",
        ["FirstSeenDate"] = "First Seen Date",
        ["LastSeenDate"] = "Last Seen Date",
        ["PullDate"] = "Pull date",
        ["PullTime"] = "Pull Time",
        ["PullDateTime"] = "Date and Time",
        ["ExhibitingAsName"] = "Exhibiting As Name",
        ["Status"] = "Status",
        ["RunDate"] = "Run Date",
        ["RunDateDate"] = "Run Date Only",
        ["ChangeDate"] = "Change Date",
        ["FieldChanged"] = "Field Changed",
        ["FieldDisplayName"] = "Field Changed",
        ["OldValue"] = "Old Value",
        ["NewValue"] = "New Value",
        ["ChangeDetectedOn"] = "Change Detected On",
        ["ChangeDetectedOnDate"] = "Change Detected On Date",
        ["QualifiesForSelectedDate"] = "Qualifies For Selected Date",
        ["MissingField"] = "Missing Field",
        ["MissingFieldInternal"] = "Missing Field Internal",
        ["Notes"] = "Notes"
    };

    private static readonly List<string> ChangeLogColumns = new()
    {
        "EventID",
        "EventName",
        "ExhibitorID",
        "AccountCode",
        "CompanyName",
        "FieldChanged",
        "OldValue",
        "NewValue",
        "ChangeDetectedOn"
    };

    private static readonly List<string> ExhibitorHistoryColumns = new()
    {
        "RowKey",
        "EventID",
        "EventName",
        "ExhibitorID",
        "AccountCode",
        "FirstSeenDate",
        "LastSeenDate",
        "CompanyName",
        "ExhibitingAsName",
        "Status",
        "BoothNumber",
        "MainExhibitorCompany"
    };

    private static readonly List<string> ChangeHistoryColumns = new()
    {
        "ChangeKey",
        "RowKey",
        "PriorSnapshotVersion",
        "RunDate",
        "EventID",
        "EventName",
        "ExhibitorID",
        "AccountCode",
        "CompanyName",
        "FieldChanged",
        "FieldDisplayName",
        "OldValue",
        "NewValue",
        "ChangeDetectedOn"
    };

    private static readonly List<string> CurrentDataWorkbookColumns = new List<string>()
    {
        "RowKey",
        "EventID",
        "ExhibitorID",
        "AccountCode",
        "FirstSeenDate"
    }.Concat(CleanOutputColumns).ToList();

    private static readonly List<string> PullDataWorkbookColumns = new List<string>()
    {
        "RowKey"
    }.Concat(CleanOutputColumns)
     .Concat(new[] { "PullDate", "PullTime", PullDateTimeColumnName })
     .ToList();

    private static readonly List<string> ChangeHistoryWorkbookColumns = new()
    {
        "RowKey",
        "ChangeKey",
        "PriorSnapshotVersion",

        // Clean contiguous display range for the dynamic Changed Since Selected Date sheet.
        "ChangeDate",
        "RunDate",
        "EventName",
        "ExhibitorID",
        "AccountCode",
        "CompanyName",
        "FieldDisplayName",
        "OldValue",
        "NewValue",

        // Internal helper/audit columns.
        "RunDateDate",
        "ChangeDetectedOn",
        "ChangeDetectedOnDate",
        "EventID",
        "FieldChanged",
        "QualifiesForSelectedDate"
    };

    // ============================================================
    // MAIN
    // ============================================================

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        List<Dictionary<string, string>> automationErrors = new();

        Console.WriteLine("Starting Registration List Automation...");
        Console.WriteLine($"Data warehouse folder: {DataWarehouseFolder}");

        DateTime runStartedAt = DateTime.Now;

        string sourceEventsPath = Path.Combine(DataWarehouseFolder, EventsFileName);
        string sourceExhibitorsPath = Path.Combine(DataWarehouseFolder, ExhibitorsFileName);
        string sourceAccountsPath = Path.Combine(DataWarehouseFolder, AccountsFileName);
        string sourceExhibitorCategoriesPath = Path.Combine(DataWarehouseFolder, ExhibitorCategoriesFileName);

        string outputRoot = Path.Combine(DataWarehouseFolder, OutputRootFolderName);

        Directory.CreateDirectory(outputRoot);

        using FileStream runLock = AcquireRunLock(outputRoot);
        Console.WriteLine("Acquired run lock. No overlapping automation instance is active.");

        ValidateFileExists(sourceEventsPath);
        ValidateFileExists(sourceExhibitorsPath);
        ValidateFileExists(sourceAccountsPath);
        ValidateFileExists(sourceExhibitorCategoriesPath);

        string tempRunFolder = Path.Combine(
            Path.GetTempPath(),
            "RegistrationListAutomation",
            $"{runStartedAt:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"
        );

        Directory.CreateDirectory(tempRunFolder);

        Console.WriteLine($"Temp run folder: {tempRunFolder}");

        string eventsPath = CopyFileForReading(sourceEventsPath, tempRunFolder);
        string exhibitorsPath = CopyFileForReading(sourceExhibitorsPath, tempRunFolder);
        string accountsPath = CopyFileForReading(sourceAccountsPath, tempRunFolder);
        string exhibitorCategoriesPath = CopyFileForReading(sourceExhibitorCategoriesPath, tempRunFolder);

        DateTime today = runStartedAt.Date;
        DateTime maxDate = today.AddMonths(MonthsAhead);

        Console.WriteLine($"Date filter: {today:yyyy-MM-dd} through {maxDate:yyyy-MM-dd}");

        List<Dictionary<string, string>> eventRows = ReadCsvAsRows(eventsPath);
        List<Dictionary<string, string>> exhibitorRows = ReadCsvAsRows(exhibitorsPath);
        Dictionary<string, Dictionary<string, string>> accountsByCode = ReadAccountsWorkbook(accountsPath);
        Dictionary<string, string> exhibitorCategoriesByCode = ReadExhibitorCategories(exhibitorCategoriesPath);

        Console.WriteLine($"Events loaded: {eventRows.Count:N0}");
        Console.WriteLine($"Exhibitors loaded: {exhibitorRows.Count:N0}");
        Console.WriteLine($"Accounts loaded: {accountsByCode.Count:N0}");
        Console.WriteLine($"Exhibitor categories loaded: {exhibitorCategoriesByCode.Count:N0}");

        List<EventInfo> upcomingEvents = GetUpcomingEvents(eventRows, today, maxDate);

        Console.WriteLine($"Upcoming events found: {upcomingEvents.Count:N0}");

        if (upcomingEvents.Count == 0)
        {
            Console.WriteLine("No upcoming events found. Program complete.");
            return;
        }

        Dictionary<string, List<Dictionary<string, string>>> exhibitorsByEventId =
            exhibitorRows
                .Select(row => new
                {
                    Row = row,
                    EventId = NormalizeId(GetValue(row, new[] { "EventID", "Event ID", "Event" }, "D")),
                    Status = NormalizeId(GetValue(row, new[] { "ExhibitorStatus", "Exhibitor Status", "Status" }, "G"))
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.EventId))
                .Where(x => ActiveExhibitorStatuses.Contains(x.Status))
                .GroupBy(x => x.EventId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.Row).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );

        int eventNumber = 0;
        int successfulEvents = 0;
        int failedEvents = 0;

        foreach (EventInfo eventInfo in upcomingEvents)
        {
            eventNumber++;

            try
            {
                Console.WriteLine();
                Console.WriteLine($"[{eventNumber}/{upcomingEvents.Count}] Processing event:");
                Console.WriteLine($"EventID: {eventInfo.EventId}");
                Console.WriteLine($"EventName: {eventInfo.EventName}");
                Console.WriteLine($"StartDate: {eventInfo.StartDate:yyyy-MM-dd}");

                if (!exhibitorsByEventId.TryGetValue(eventInfo.EventId, out List<Dictionary<string, string>>? eventExhibitors))
                {
                    eventExhibitors = new List<Dictionary<string, string>>();
                }

                Console.WriteLine($"Active exhibitors found: {eventExhibitors.Count:N0}");

                List<Dictionary<string, string>> currentFullRows =
                    BuildCurrentRegistrationRows(
                        eventInfo,
                        eventExhibitors,
                        accountsByCode,
                        exhibitorCategoriesByCode
                    );

                string safeEventName = MakeSafeFileName(eventInfo.EventName);
                if (string.IsNullOrWhiteSpace(safeEventName))
                    safeEventName = $"Event {MakeSafeFileName(eventInfo.EventId)}";

                string outputFileName = $"{safeEventName} Registration List.xlsx";
                string outputPath = Path.Combine(outputRoot, outputFileName);

                List<Dictionary<string, string>> existingPullRows = ReadExistingPullRowsFromWorkbook(outputPath);
                List<Dictionary<string, string>> currentPullRows = BuildPullRows(currentFullRows, runStartedAt);
                List<Dictionary<string, string>> allPullRows = existingPullRows
                    .Concat(currentPullRows)
                    .ToList();

                Console.WriteLine($"Existing data pull rows: {existingPullRows.Count:N0}");
                Console.WriteLine($"Rows appended to data: {currentPullRows.Count:N0}");
                Console.WriteLine($"Total data pull rows after append: {allPullRows.Count:N0}");

                Stopwatch workbookStopwatch = Stopwatch.StartNew();

                CreateRegistrationListWorkbook(outputPath, currentPullRows, allPullRows, runStartedAt);

                workbookStopwatch.Stop();
                Console.WriteLine($"Workbook render/save time: {workbookStopwatch.Elapsed:mm\\:ss}");

                Console.WriteLine($"Created workbook: {outputPath}");

                successfulEvents++;
            }
            catch (Exception ex)
            {
                failedEvents++;

                Console.WriteLine("ERROR processing event. Continuing to next event.");
                Console.WriteLine($"EventID: {eventInfo.EventId}");
                Console.WriteLine($"EventName: {eventInfo.EventName}");
                Console.WriteLine(ex.Message);

                automationErrors.Add(new Dictionary<string, string>
                {
                    ["RunDate"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["EventNumber"] = eventNumber.ToString(CultureInfo.InvariantCulture),
                    ["EventID"] = eventInfo.EventId,
                    ["EventName"] = eventInfo.EventName,
                    ["EventStartDate"] = eventInfo.StartDate.ToString("yyyy-MM-dd"),
                    ["ErrorType"] = ex.GetType().Name,
                    ["ErrorMessage"] = ex.Message,
                    ["StackTrace"] = ex.StackTrace ?? ""
                });
            }
        }

        if (automationErrors.Count > 0)
        {
            string errorPath = Path.Combine(
                outputRoot,
                $"Registration List Automation Errors - {DateTime.Now:yyyy-MM-dd HHmm}.xlsx"
            );

            CreateErrorWorkbook(errorPath, automationErrors);

            Console.WriteLine();
            Console.WriteLine($"Errors were logged here: {errorPath}");
        }

        Console.WriteLine();
        Console.WriteLine("Registration List Automation complete.");
        Console.WriteLine($"Successful events: {successfulEvents:N0}");
        Console.WriteLine($"Failed events: {failedEvents:N0}");
    }

    // ============================================================
    // EVENT FILTERING
    // ============================================================

    private static List<EventInfo> GetUpcomingEvents(
        List<Dictionary<string, string>> eventRows,
        DateTime today,
        DateTime maxDate)
    {
        List<EventInfo> results = new();

        foreach (Dictionary<string, string> row in eventRows)
        {
            string eventId = NormalizeId(GetValue(row, new[] { "EventID", "Event ID" }, "D"));
            string startDateText = GetValue(row, new[] { "StartDate", "Start Date" }, "E");
            string description = GetValue(row, new[] { "Description", "EventName", "Event Name" }, "G");

            if (string.IsNullOrWhiteSpace(eventId))
                continue;

            if (!TryParseDate(startDateText, out DateTime startDate))
                continue;

            if (startDate.Date < today.Date)
                continue;

            if (startDate.Date > maxDate.Date)
                continue;

            if (string.IsNullOrWhiteSpace(description))
                description = $"Event {eventId}";

            results.Add(new EventInfo
            {
                EventId = eventId,
                EventName = description.Trim(),
                StartDate = startDate.Date
            });
        }

        return results
            .OrderBy(e => e.StartDate)
            .ThenBy(e => e.EventName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ============================================================
    // BUILD CURRENT REGISTRATION LIST
    // ============================================================

    private static List<Dictionary<string, string>> BuildCurrentRegistrationRows(
        EventInfo eventInfo,
        List<Dictionary<string, string>> eventExhibitors,
        Dictionary<string, Dictionary<string, string>> accountsByCode,
        Dictionary<string, string> exhibitorCategoriesByCode)
    {
        List<Dictionary<string, string>> outputRows = new();

        foreach (Dictionary<string, string> exhibitor in eventExhibitors)
        {
            string exhibitorId = NormalizeId(GetValue(exhibitor, new[] { "ExhibitorID", "Exhibitor ID" }, "C"));
            string exhibitorEventId = NormalizeId(GetValue(exhibitor, new[] { "EventID", "Event ID", "Event" }, "D"));

            if (!StringEqualsNormalized(exhibitorEventId, eventInfo.EventId))
                continue;

            string exhibitorStatus = NormalizeId(GetValue(exhibitor, new[] { "ExhibitorStatus", "Exhibitor Status", "Status" }, "G"));

            if (!ActiveExhibitorStatuses.Contains(exhibitorStatus))
                continue;

            string accountCode = NormalizeAccountCode(GetValue(exhibitor, new[] { "AccountCode", "Account Code" }, ""));
            string mainContactCode = NormalizeAccountCode(GetValue(exhibitor, new[] { "MainContact", "Main Contact" }, "AJ"));
            string boothContactCode = NormalizeAccountCode(GetValue(exhibitor, new[] { "BoothContact", "Booth Contact" }, "AL"));
            string pressContactCode = NormalizeAccountCode(GetValue(exhibitor, new[] { "PressContact", "Press Contact" }, "AN"));
            string mainExhibitorAccountCode = NormalizeAccountCode(GetValue(exhibitor, new[] { "MainExhibitor", "Main Exhibitor", "MainExhibitorAccount" }, "DE"));

            string rawExhibitorType = GetValue(exhibitor, new[] { "ExhibitorType", "Exhibitor Type" }, "F");
            string rawExhibitorCategory = GetValue(exhibitor, new[] { "ExhibitorCategory", "Exhibitor Category" }, "DX");

            Dictionary<string, string>? companyAccount = GetAccount(accountsByCode, accountCode);
            Dictionary<string, string>? mainContactAccount = GetAccount(accountsByCode, mainContactCode);
            Dictionary<string, string>? boothContactAccount = GetAccount(accountsByCode, boothContactCode);
            Dictionary<string, string>? pressContactAccount = GetAccount(accountsByCode, pressContactCode);
            Dictionary<string, string>? mainExhibitorAccount = GetAccount(accountsByCode, mainExhibitorAccountCode);

            string rowKey = BuildRowKey(eventInfo.EventId, exhibitorId);

            Dictionary<string, string> row = new(StringComparer.OrdinalIgnoreCase)
            {
                ["RowKey"] = rowKey,
                ["EventID"] = eventInfo.EventId,
                ["EventName"] = eventInfo.EventName,
                ["EventStartDate"] = eventInfo.StartDate.ToString("yyyy-MM-dd"),

                ["ExhibitorID"] = exhibitorId,
                ["ExhibitorEventID"] = exhibitorEventId,
                ["AccountCode"] = accountCode,
                ["ExhibitorStatus"] = FormatExhibitorStatus(exhibitorStatus),

                ["CompanyName"] = GetBestCompanyName(companyAccount),
                ["CompanyAddress1"] = GetValue(companyAccount ?? new Dictionary<string, string>(), new[] { "Address1", "Address 1" }, "H"),
                ["CompanyCity"] = GetValue(companyAccount ?? new Dictionary<string, string>(), new[] { "City" }, "U"),
                ["CompanyState"] = GetValue(companyAccount ?? new Dictionary<string, string>(), new[] { "State" }, "CU"),
                ["CompanyPostalCode"] = GetValue(companyAccount ?? new Dictionary<string, string>(), new[] { "PostalCode", "Postal Code" }, "CE"),

                ["ExhibitorType"] = FormatExhibitorType(rawExhibitorType),
                ["CompanyBannerName"] = GetValue(exhibitor, new[] { "CompanyBannerName", "Company Banner Name" }, "W"),

                ["OrderedArea"] = GetValue(exhibitor, new[] { "OrderedArea", "Ordered Area" }, "U"),
                ["OrderedLength"] = GetValue(exhibitor, new[] { "OrderedLength", "Ordered Length", "OrderfedLegnth" }, "S"),
                ["OrderedWidth"] = GetValue(exhibitor, new[] { "OrderedWidth", "Ordered Width" }, "T"),
                ["BoothNumber"] = GetValue(exhibitor, new[] { "BoothNumber", "Booth Number" }, "DZ"),
                ["ExhibitorCategory"] = MapExhibitorCategories(rawExhibitorCategory, exhibitorCategoriesByCode),
                ["MainExhibitorAccountCode"] = mainExhibitorAccountCode,
                ["MainExhibitorCompany"] = GetBestCompanyName(mainExhibitorAccount),

                ["MainContactAccountCode"] = mainContactCode,
                ["BoothContactAccountCode"] = boothContactCode,
                ["PressContactAccountCode"] = pressContactCode,

                ["ExhibitorCompanyName"] = GetValue(exhibitor, new[] { "CompanyName", "Company Name" }, "BH"),
                ["CatalogAddress"] = GetValue(exhibitor, new[] { "CatalogAddress", "Catalog Address" }, "AR"),
                ["CatalogCity"] = GetValue(exhibitor, new[] { "CatalogCity", "Catalog City" }, "AX"),
                ["CatalogState"] = GetValue(exhibitor, new[] { "CatalogState", "Catalog State" }, "AY"),
                ["CatalogPostalCode"] = GetValue(exhibitor, new[] { "CatalogPostalCode", "Catalog Postal Code" }, "AZ"),
                ["CatalogCountry"] = GetValue(exhibitor, new[] { "CatalogCountry", "Catalog Country" }, "BA"),
                ["CatalogCountryName"] = GetValue(exhibitor, new[] { "CatalogCountryName", "Catalog Country Name", "Cataloged Country Name" }, "DU"),
                ["ExhibitorWebsite"] = GetValue(exhibitor, new[] { "Website" }, "CB")
            };

            AddAccountFields(row, "CompanyAccount", companyAccount);
            AddAccountFields(row, "MainContact", mainContactAccount);
            AddAccountFields(row, "BoothContact", boothContactAccount);
            AddAccountFields(row, "PressContact", pressContactAccount);

            outputRows.Add(row);
        }

        return outputRows
            .OrderBy(r => GetDictionaryValue(r, "CompanyName"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => GetDictionaryValue(r, "CompanyBannerName"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => GetDictionaryValue(r, "ExhibitorID"), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, string>? GetAccount(
        Dictionary<string, Dictionary<string, string>> accountsByCode,
        string accountCode)
    {
        if (string.IsNullOrWhiteSpace(accountCode))
            return null;

        return accountsByCode.TryGetValue(accountCode, out Dictionary<string, string>? account)
            ? account
            : null;
    }

    private static void AddAccountFields(
        Dictionary<string, string> outputRow,
        string prefix,
        Dictionary<string, string>? account)
    {
        outputRow[$"{prefix}AccountCode"] = GetAccountValue(account, new[] { "AccountCode", "Account Code" }, "B");
        outputRow[$"{prefix}Address1"] = GetAccountValue(account, new[] { "Address1", "Address 1" }, "H");
        outputRow[$"{prefix}ChangedOn"] = FormatPossibleDate(GetAccountValue(account, new[] { "ChangedOn", "Changed On" }, "T"));
        outputRow[$"{prefix}Company"] = GetAccountValue(account, new[] { "Company" }, "W");
        outputRow[$"{prefix}City"] = GetAccountValue(account, new[] { "City" }, "U");
        outputRow[$"{prefix}Country"] = GetAccountValue(account, new[] { "Country" }, "X");
        outputRow[$"{prefix}Email"] = GetAccountValue(account, new[] { "Email" }, "AC");
        outputRow[$"{prefix}FirstName"] = GetAccountValue(account, new[] { "FirstName", "First Name" }, "AJ");
        outputRow[$"{prefix}LastName"] = GetAccountValue(account, new[] { "LastName", "Last Name" }, "AV");
        outputRow[$"{prefix}LegalName"] = GetAccountValue(account, new[] { "LegalName", "Legal Name" }, "AY");
        outputRow[$"{prefix}Mobile"] = GetAccountValue(account, new[] { "Mobile", "Mobile Number" }, "BR");
        outputRow[$"{prefix}Phone"] = GetAccountValue(account, new[] { "Phone" }, "CC");
        outputRow[$"{prefix}PostalCode"] = GetAccountValue(account, new[] { "PostalCode", "Postal Code" }, "CE");
        outputRow[$"{prefix}PrimaryAccount"] = GetAccountValue(account, new[] { "PrimaryAccount", "Primary Account" }, "CH");
        outputRow[$"{prefix}State"] = GetAccountValue(account, new[] { "State" }, "CU");
        outputRow[$"{prefix}Title"] = GetAccountValue(account, new[] { "Title" }, "DO");
        outputRow[$"{prefix}Website"] = GetAccountValue(account, new[] { "Website" }, "DJ");
    }

    private static string GetAccountValue(
        Dictionary<string, string>? account,
        string[] possibleHeaders,
        string fallbackColumnLetter)
    {
        if (account == null)
            return "";

        return GetValue(account, possibleHeaders, fallbackColumnLetter);
    }

    // ============================================================
    // COMPARISON + HISTORY
    // ============================================================

    private static ComparisonResult CompareToPriorSnapshot(
        List<Dictionary<string, string>> priorRows,
        List<Dictionary<string, string>> currentRows,
        string priorSnapshotVersion,
        DateTime changeDetectedOnDate)
    {
        ComparisonResult result = new();

        Dictionary<string, Dictionary<string, string>> priorByKey = priorRows
            .Where(r => !string.IsNullOrWhiteSpace(GetRowKey(r)))
            .GroupBy(GetRowKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (Dictionary<string, string> currentRow in currentRows)
        {
            string key = GetRowKey(currentRow);

            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!priorByKey.TryGetValue(key, out Dictionary<string, string>? priorRow))
            {
                result.NewRows.Add(new Dictionary<string, string>(currentRow, StringComparer.OrdinalIgnoreCase));
                continue;
            }

            List<string> allColumns = currentRow.Keys
                .Union(priorRow.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Where(c => !IsComparisonExcludedColumn(c))
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string column in allColumns)
            {
                string oldValue = GetDictionaryValue(priorRow, column);
                string newValue = GetDictionaryValue(currentRow, column);

                if (NormalizeForCompare(oldValue) == NormalizeForCompare(newValue))
                    continue;

                Dictionary<string, string> changeRow = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["EventID"] = GetDictionaryValue(currentRow, "EventID"),
                    ["EventName"] = GetDictionaryValue(currentRow, "EventName"),
                    ["ExhibitorID"] = GetDictionaryValue(currentRow, "ExhibitorID"),
                    ["AccountCode"] = GetDictionaryValue(currentRow, "AccountCode"),
                    ["PriorSnapshotVersion"] = priorSnapshotVersion,
                    ["CompanyName"] = FirstNonBlank(
                        GetDictionaryValue(currentRow, "CompanyName"),
                        GetDictionaryValue(currentRow, "ExhibitorCompanyName"),
                        GetDictionaryValue(currentRow, "CompanyAccountCompany"),
                        GetDictionaryValue(currentRow, "CompanyAccountLegalName")
                    ),
                    ["FieldChanged"] = column,
                    ["FieldDisplayName"] = GetDisplayColumnName(column),
                    ["OldValue"] = oldValue,
                    ["NewValue"] = newValue,
                    ["ChangeDetectedOn"] = changeDetectedOnDate.ToString("yyyy-MM-dd")
                };

                changeRow["RowKey"] = GetRowKey(changeRow);
                changeRow["ChangeKey"] = BuildChangeKey(changeRow);

                result.ChangedRows.Add(changeRow);
            }
        }

        return result;
    }

    private static List<Dictionary<string, string>> BuildUpdatedExhibitorHistory(
        string historyPath,
        List<Dictionary<string, string>> currentFullRows,
        DateTime runDate)
    {
        List<Dictionary<string, string>> existingHistory =
            File.Exists(historyPath)
                ? ReadCsvAsRows(historyPath)
                : new List<Dictionary<string, string>>();

        foreach (Dictionary<string, string> row in existingHistory)
        {
            row["RowKey"] = GetRowKey(row);
        }

        Dictionary<string, Dictionary<string, string>> historyByKey = existingHistory
            .Where(r => !string.IsNullOrWhiteSpace(GetRowKey(r)))
            .GroupBy(GetRowKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        string runDateText = runDate.ToString("yyyy-MM-dd");

        foreach (Dictionary<string, string> currentRow in currentFullRows)
        {
            string key = GetRowKey(currentRow);

            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!historyByKey.TryGetValue(key, out Dictionary<string, string>? historyRow))
            {
                historyRow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["RowKey"] = key,
                    ["EventID"] = GetDictionaryValue(currentRow, "EventID"),
                    ["EventName"] = GetDictionaryValue(currentRow, "EventName"),
                    ["ExhibitorID"] = GetDictionaryValue(currentRow, "ExhibitorID"),
                    ["AccountCode"] = GetDictionaryValue(currentRow, "AccountCode"),
                    ["FirstSeenDate"] = runDateText
                };

                historyByKey[key] = historyRow;
            }

            historyRow["RowKey"] = key;
            historyRow["LastSeenDate"] = runDateText;
            historyRow["CompanyName"] = GetDictionaryValue(currentRow, "CompanyName");
            historyRow["ExhibitingAsName"] = GetDictionaryValue(currentRow, "CompanyBannerName");
            historyRow["Status"] = GetDictionaryValue(currentRow, "ExhibitorStatus");
            historyRow["BoothNumber"] = GetDictionaryValue(currentRow, "BoothNumber");
            historyRow["MainExhibitorCompany"] = GetDictionaryValue(currentRow, "MainExhibitorCompany");
        }

        List<Dictionary<string, string>> outputRows = historyByKey.Values
            .OrderBy(r => GetDictionaryValue(r, "FirstSeenDate"))
            .ThenBy(r => GetDictionaryValue(r, "CompanyName"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => GetDictionaryValue(r, "ExhibitorID"), StringComparer.OrdinalIgnoreCase)
            .ToList();

        return outputRows;
    }

    private static List<Dictionary<string, string>> BuildUpdatedChangeHistory(
        string historyPath,
        List<Dictionary<string, string>> changedRows,
        DateTime runDate)
    {
        List<Dictionary<string, string>> existingHistory =
            File.Exists(historyPath)
                ? ReadCsvAsRows(historyPath)
                : new List<Dictionary<string, string>>();

        foreach (Dictionary<string, string> row in existingHistory)
        {
            NormalizeChangeHistoryRow(row);
        }

        HashSet<string> existingChangeKeys = existingHistory
            .Select(BuildChangeKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string runDateText = runDate.ToString("yyyy-MM-dd HH:mm:ss");

        foreach (Dictionary<string, string> changeRow in changedRows)
        {
            Dictionary<string, string> historyRow = new(StringComparer.OrdinalIgnoreCase)
            {
                ["RunDate"] = runDateText,
                ["EventID"] = GetDictionaryValue(changeRow, "EventID"),
                ["EventName"] = GetDictionaryValue(changeRow, "EventName"),
                ["ExhibitorID"] = GetDictionaryValue(changeRow, "ExhibitorID"),
                ["AccountCode"] = GetDictionaryValue(changeRow, "AccountCode"),
                ["PriorSnapshotVersion"] = GetDictionaryValue(changeRow, "PriorSnapshotVersion"),
                ["CompanyName"] = GetDictionaryValue(changeRow, "CompanyName"),
                ["FieldChanged"] = GetDictionaryValue(changeRow, "FieldChanged"),
                ["FieldDisplayName"] = FirstNonBlank(
                    GetDictionaryValue(changeRow, "FieldDisplayName"),
                    GetDisplayColumnName(GetDictionaryValue(changeRow, "FieldChanged"))
                ),
                ["OldValue"] = GetDictionaryValue(changeRow, "OldValue"),
                ["NewValue"] = GetDictionaryValue(changeRow, "NewValue"),
                ["ChangeDetectedOn"] = GetDictionaryValue(changeRow, "ChangeDetectedOn")
            };

            NormalizeChangeHistoryRow(historyRow);

            string changeKey = GetDictionaryValue(historyRow, "ChangeKey");

            if (string.IsNullOrWhiteSpace(changeKey))
                continue;

            if (existingChangeKeys.Contains(changeKey))
                continue;

            existingHistory.Add(historyRow);
            existingChangeKeys.Add(changeKey);
        }

        return existingHistory
            .OrderByDescending(r => ParseDateOrMin(GetDictionaryValue(r, "RunDate")))
            .ThenBy(r => GetDictionaryValue(r, "CompanyName"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => GetDictionaryValue(r, "FieldChanged"), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void NormalizeChangeHistoryRow(Dictionary<string, string> row)
    {
        row["RowKey"] = GetRowKey(row);

        string fieldChanged = GetDictionaryValue(row, "FieldChanged");
        row["FieldDisplayName"] = FirstNonBlank(GetDictionaryValue(row, "FieldDisplayName"), GetDisplayColumnName(fieldChanged));
        row["ChangeKey"] = BuildChangeKey(row);
    }

    private static List<Dictionary<string, string>> BuildCurrentWorkbookRows(
        List<Dictionary<string, string>> currentFullRows,
        List<Dictionary<string, string>> exhibitorHistoryRows,
        DateTime fallbackRunDate)
    {
        Dictionary<string, Dictionary<string, string>> historyByKey = exhibitorHistoryRows
            .Where(r => !string.IsNullOrWhiteSpace(GetRowKey(r)))
            .GroupBy(GetRowKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        List<Dictionary<string, string>> output = new();

        foreach (Dictionary<string, string> sourceRow in currentFullRows)
        {
            string rowKey = GetRowKey(sourceRow);

            Dictionary<string, string> row = new(StringComparer.OrdinalIgnoreCase)
            {
                ["RowKey"] = rowKey,
                ["EventID"] = GetDictionaryValue(sourceRow, "EventID"),
                ["ExhibitorID"] = GetDictionaryValue(sourceRow, "ExhibitorID"),
                ["AccountCode"] = GetDictionaryValue(sourceRow, "AccountCode")
            };

            string firstSeenDate = fallbackRunDate.ToString("yyyy-MM-dd");

            if (historyByKey.TryGetValue(rowKey, out Dictionary<string, string>? historyRow))
            {
                firstSeenDate = FirstNonBlank(GetDictionaryValue(historyRow, "FirstSeenDate"), firstSeenDate);
            }

            row["FirstSeenDate"] = firstSeenDate;

            foreach (string column in CleanOutputColumns)
            {
                row[column] = GetDictionaryValue(sourceRow, column);
            }

            output.Add(row);
        }

        return output;
    }

    private static List<Dictionary<string, string>> BuildChangeWorkbookRows(List<Dictionary<string, string>> changeHistoryRows)
    {
        List<Dictionary<string, string>> output = new();

        foreach (Dictionary<string, string> sourceRow in changeHistoryRows)
        {
            string runDateText = GetDictionaryValue(sourceRow, "RunDate");
            string changeDetectedOnText = GetDictionaryValue(sourceRow, "ChangeDetectedOn");

            DateTime runDate = ParseDateOrMin(runDateText);
            DateTime changeDetectedOn = ParseDateOrMin(changeDetectedOnText);
            DateTime changeDate = MaxDate(runDate.Date, changeDetectedOn.Date);

            Dictionary<string, string> row = new(StringComparer.OrdinalIgnoreCase)
            {
                ["RowKey"] = GetRowKey(sourceRow),
                ["ChangeKey"] = FirstNonBlank(GetDictionaryValue(sourceRow, "ChangeKey"), BuildChangeKey(sourceRow)),
                ["PriorSnapshotVersion"] = GetDictionaryValue(sourceRow, "PriorSnapshotVersion"),
                ["ChangeDate"] = changeDate == DateTime.MinValue ? "" : changeDate.ToString("yyyy-MM-dd"),
                ["RunDate"] = runDate == DateTime.MinValue ? runDateText : runDate.ToString("yyyy-MM-dd HH:mm:ss"),
                ["EventName"] = GetDictionaryValue(sourceRow, "EventName"),
                ["ExhibitorID"] = GetDictionaryValue(sourceRow, "ExhibitorID"),
                ["AccountCode"] = GetDictionaryValue(sourceRow, "AccountCode"),
                ["CompanyName"] = GetDictionaryValue(sourceRow, "CompanyName"),
                ["FieldDisplayName"] = FirstNonBlank(
                    GetDictionaryValue(sourceRow, "FieldDisplayName"),
                    GetDisplayColumnName(GetDictionaryValue(sourceRow, "FieldChanged"))
                ),
                ["OldValue"] = GetDictionaryValue(sourceRow, "OldValue"),
                ["NewValue"] = GetDictionaryValue(sourceRow, "NewValue"),
                ["RunDateDate"] = runDate == DateTime.MinValue ? "" : runDate.ToString("yyyy-MM-dd"),
                ["ChangeDetectedOn"] = changeDetectedOn == DateTime.MinValue ? changeDetectedOnText : changeDetectedOn.ToString("yyyy-MM-dd"),
                ["ChangeDetectedOnDate"] = changeDetectedOn == DateTime.MinValue ? "" : changeDetectedOn.ToString("yyyy-MM-dd"),
                ["EventID"] = GetDictionaryValue(sourceRow, "EventID"),
                ["FieldChanged"] = GetDictionaryValue(sourceRow, "FieldChanged"),
                ["QualifiesForSelectedDate"] = ""
            };

            output.Add(row);
        }

        return output;
    }

    private static bool IsComparisonExcludedColumn(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
            return true;

        if (IsHelperColumn(columnName))
            return true;

        // Production workbook change tracking should be business-visible by default.
        // Comparing every internal snapshot field creates noise and false positives.
        return !ComparableSnapshotColumns.Contains(columnName);
    }

    private static bool IsHelperColumn(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
            return true;

        return Regex.IsMatch(columnName.Trim(), @"^Column[A-Z]+$", RegexOptions.IgnoreCase);
    }

    private static string GetRowKey(Dictionary<string, string> row)
    {
        string existingRowKey = GetDictionaryValue(row, "RowKey").Trim();

        if (!string.IsNullOrWhiteSpace(existingRowKey))
        {
            string[] parts = existingRowKey.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 2)
                return BuildRowKey(parts[0], parts[1]);
        }

        string eventId = NormalizeId(GetDictionaryValue(row, "EventID"));
        string exhibitorId = NormalizeId(GetDictionaryValue(row, "ExhibitorID"));

        return BuildRowKey(eventId, exhibitorId);
    }

    private static string BuildRowKey(string eventId, string exhibitorId)
    {
        string normalizedEventId = NormalizeId(eventId);
        string normalizedExhibitorId = NormalizeId(exhibitorId);

        if (string.IsNullOrWhiteSpace(normalizedEventId) || string.IsNullOrWhiteSpace(normalizedExhibitorId))
            return "";

        return $"{normalizedEventId}|{normalizedExhibitorId}";
    }

    private static string BuildChangeKey(Dictionary<string, string> row)
    {
        string rowKey = GetRowKey(row);
        string fieldChanged = NormalizeForCompare(GetDictionaryValue(row, "FieldChanged"));
        string oldValueHash = StableHash(NormalizeForCompare(GetDictionaryValue(row, "OldValue")));
        string newValueHash = StableHash(NormalizeForCompare(GetDictionaryValue(row, "NewValue")));
        string priorSnapshotVersion = FirstNonBlank(
            GetDictionaryValue(row, "PriorSnapshotVersion"),
            "UNKNOWN_PRIOR_SNAPSHOT"
        );

        if (string.IsNullOrWhiteSpace(rowKey) || string.IsNullOrWhiteSpace(fieldChanged))
            return "";

        return $"{rowKey}|{fieldChanged}|{oldValueHash}|{newValueHash}|{priorSnapshotVersion}";
    }

    private static string StableHash(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexString(bytes).Substring(0, 16);
    }

    // ============================================================
    // WORKBOOK-OWNED PULL HISTORY
    // ============================================================

    private static List<Dictionary<string, string>> BuildPullRows(
        List<Dictionary<string, string>> currentFullRows,
        DateTime pullDateTime)
    {
        List<Dictionary<string, string>> output = new();
        string pullDate = pullDateTime.ToString("yyyy-MM-dd");
        string pullTime = pullDateTime.ToString("HH:mm:ss");
        string pullDateTimeText = pullDateTime.ToString("yyyy-MM-dd HH:mm:ss");

        foreach (Dictionary<string, string> sourceRow in currentFullRows)
        {
            Dictionary<string, string> row = new(StringComparer.OrdinalIgnoreCase)
            {
                ["RowKey"] = GetRowKey(sourceRow),
                ["PullDate"] = pullDate,
                ["PullTime"] = pullTime,
                [PullDateTimeColumnName] = pullDateTimeText
            };

            foreach (string column in CleanOutputColumns)
            {
                row[column] = GetDictionaryValue(sourceRow, column);
            }

            output.Add(row);
        }

        return output;
    }

    private static List<Dictionary<string, string>> ReadExistingPullRowsFromWorkbook(string workbookPath)
    {
        List<Dictionary<string, string>> rows = new();

        if (!File.Exists(workbookPath))
            return rows;

        using FileStream fileStream = new(
            workbookPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );

        using XLWorkbook workbook = new(fileStream);

        IXLWorksheet? ws = workbook.Worksheets
            .FirstOrDefault(sheet => sheet.Name.Equals(PullDataSheetName, StringComparison.OrdinalIgnoreCase));

        if (ws == null)
            return rows;

        IXLRange? usedRange = ws.RangeUsed();

        if (usedRange == null)
            return rows;

        int firstRow = usedRange.RangeAddress.FirstAddress.RowNumber;
        int lastRow = usedRange.RangeAddress.LastAddress.RowNumber;
        int firstCol = usedRange.RangeAddress.FirstAddress.ColumnNumber;
        int lastCol = usedRange.RangeAddress.LastAddress.ColumnNumber;

        Dictionary<int, string> headersByColumn = new();

        for (int col = firstCol; col <= lastCol; col++)
        {
            string internalHeader = MapPullDataHeader(ws.Cell(firstRow, col).GetFormattedString());

            if (!string.IsNullOrWhiteSpace(internalHeader))
                headersByColumn[col] = internalHeader;
        }

        if (!headersByColumn.Values.Contains("RowKey", StringComparer.OrdinalIgnoreCase)
            || !headersByColumn.Values.Contains("PullDate", StringComparer.OrdinalIgnoreCase)
            || !headersByColumn.Values.Contains("PullTime", StringComparer.OrdinalIgnoreCase))
        {
            return rows;
        }

        for (int rowNumber = firstRow + 1; rowNumber <= lastRow; rowNumber++)
        {
            Dictionary<string, string> row = new(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<int, string> pair in headersByColumn)
            {
                row[pair.Value] = GetWorkbookCellText(ws.Cell(rowNumber, pair.Key), pair.Value);
            }

            NormalizePullRow(row);

            if (string.IsNullOrWhiteSpace(GetDictionaryValue(row, "RowKey")))
                continue;

            if (!TryGetPullDateTime(row, out _))
                continue;

            rows.Add(row);
        }

        return rows;
    }

    private static string MapPullDataHeader(string rawHeader)
    {
        string cleanedHeader = CleanHeader(rawHeader);

        if (string.IsNullOrWhiteSpace(cleanedHeader))
            return "";

        foreach (string column in PullDataWorkbookColumns)
        {
            if (cleanedHeader.Equals(CleanHeader(column), StringComparison.OrdinalIgnoreCase)
                || cleanedHeader.Equals(CleanHeader(GetDisplayColumnName(column)), StringComparison.OrdinalIgnoreCase))
            {
                return column;
            }
        }

        return "";
    }

    private static string GetWorkbookCellText(IXLCell cell, string internalHeader)
    {
        string value = cell.GetFormattedString().Trim();

        if (internalHeader.Equals("PullDate", StringComparison.OrdinalIgnoreCase)
            && TryParseDate(value, out DateTime date))
        {
            return date.ToString("yyyy-MM-dd");
        }

        if (internalHeader.Equals("PullTime", StringComparison.OrdinalIgnoreCase)
            && TryParseTime(value, out TimeSpan time))
        {
            return time.ToString(@"hh\:mm\:ss");
        }

        if (internalHeader.Equals(PullDateTimeColumnName, StringComparison.OrdinalIgnoreCase)
            && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime pullDateTime))
        {
            return pullDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        }

        return value;
    }

    private static void NormalizePullRow(Dictionary<string, string> row)
    {
        foreach (string column in PullDataWorkbookColumns)
        {
            if (!row.ContainsKey(column))
                row[column] = "";
        }

        if (TryParseDate(GetDictionaryValue(row, "PullDate"), out DateTime pullDate))
            row["PullDate"] = pullDate.ToString("yyyy-MM-dd");

        if (TryParseTime(GetDictionaryValue(row, "PullTime"), out TimeSpan pullTime))
            row["PullTime"] = pullTime.ToString(@"hh\:mm\:ss");

        if (TryGetPullDateTime(row, out DateTime pullDateTime))
            row[PullDateTimeColumnName] = pullDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static bool TryGetPullDateTime(Dictionary<string, string> row, out DateTime pullDateTime)
    {
        pullDateTime = default;

        if (!TryParseDate(GetDictionaryValue(row, "PullDate"), out DateTime pullDate))
            return false;

        TimeSpan pullTime = TimeSpan.Zero;
        TryParseTime(GetDictionaryValue(row, "PullTime"), out pullTime);

        pullDateTime = pullDate.Date.Add(pullTime);
        return true;
    }

    private static bool TryParseTime(string value, out TimeSpan time)
    {
        time = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        string cleaned = value.Trim();

        if (TimeSpan.TryParse(cleaned, CultureInfo.InvariantCulture, out time))
            return true;

        string[] formats =
        {
            "H:mm",
            "HH:mm",
            "H:mm:ss",
            "HH:mm:ss",
            "h:mm tt",
            "hh:mm tt",
            "h:mm:ss tt",
            "hh:mm:ss tt"
        };

        if (DateTime.TryParseExact(
                cleaned,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTime parsedDateTime))
        {
            time = parsedDateTime.TimeOfDay;
            return true;
        }

        if (double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out double serialTime)
            && serialTime >= 0
            && serialTime < 1)
        {
            time = TimeSpan.FromDays(serialTime);
            return true;
        }

        if (DateTime.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsedDateTime))
        {
            time = parsedDateTime.TimeOfDay;
            return true;
        }

        return false;
    }

    private static List<PullRunInfo> BuildPullRunList(
        List<Dictionary<string, string>> pullRows,
        DateTime fallbackRunDateTime)
    {
        List<PullRunInfo> runs = pullRows
            .Select(row => TryGetPullDateTime(row, out DateTime pullDateTime)
                ? new { PullDateTime = pullDateTime, Row = row }
                : null)
            .Where(item => item != null)
            .GroupBy(item => item!.PullDateTime)
            .Select(group => new PullRunInfo
            {
                PullDateTime = group.Key,
                RowCount = group.Count()
            })
            .OrderByDescending(run => run.PullDateTime)
            .ToList();

        if (runs.Count == 0)
        {
            runs.Add(new PullRunInfo
            {
                PullDateTime = fallbackRunDateTime,
                RowCount = 0
            });
        }

        return runs;
    }

    private static DateTime GetDefaultPreviousPullDateTime(List<PullRunInfo> pullRuns)
    {
        if (pullRuns.Count >= 2)
            return pullRuns[1].PullDateTime;

        return pullRuns[0].PullDateTime;
    }

    private static void CreateRegistrationListWorkbook(
        string outputPath,
        List<Dictionary<string, string>> currentPullRows,
        List<Dictionary<string, string>> allPullRows,
        DateTime runStartedAt)
    {
        using XLWorkbook workbook = new();
        workbook.CalculateMode = XLCalculateMode.Auto;

        AddPullDataWorksheet(workbook, allPullRows);
        AddFormulaRegistrationListWorksheet(
            workbook,
            Math.Max(currentPullRows.Count, 1),
            GetDefaultPreviousPullDateTime(BuildPullRunList(allPullRows, runStartedAt))
        );

        workbook.Worksheet(CurrentRegistrationSheetName).Position = 1;

        SaveWorkbookWithRetry(workbook, outputPath);
    }

    private static void AddFormulaRegistrationListWorksheet(
        XLWorkbook workbook,
        int currentRowCount,
        DateTime defaultComparePullDateTime)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(CurrentRegistrationSheetName);

        int headerRow = 1;
        int firstDataRow = 2;
        int firstSourceCol = 4;
        int sourceColCount = PullDataWorkbookColumns.Count;
        int lastSourceCol = firstSourceCol + sourceColCount - 1;
        int changeStatusCol = lastSourceCol + 1;
        int changeDetailsCol = lastSourceCol + 2;
        int priorExistsCol = lastSourceCol + 3;
        int changedCountCol = lastSourceCol + 4;
        int priorValueFirstCol = lastSourceCol + 5;
        int priorValueLastCol = priorValueFirstCol + CleanOutputColumns.Count - 1;
        int lastDataRow = firstDataRow + Math.Max(currentRowCount, 1) - 1;

        string rowKeyDisplay = GetDisplayColumnName("RowKey");
        string pullDateTimeDisplay = GetDisplayColumnName(PullDateTimeColumnName);
        string firstDataRowKeyRef = $"${IndexToColumnLetter(firstSourceCol)}{firstDataRow}";
        string priorExistsFirstRef = $"${IndexToColumnLetter(priorExistsCol)}{firstDataRow}";
        string priorValueFirstRef = $"${IndexToColumnLetter(priorValueFirstCol)}{firstDataRow}";
        string priorValueLastRef = $"${IndexToColumnLetter(priorValueLastCol)}{firstDataRow}";
        string firstComparableCellRef = $"{IndexToColumnLetter(firstSourceCol + 1)}{firstDataRow}";

        ws.Cell(2, 1).Value = "Last registration pull:";
        ws.Cell(2, 2).Value = defaultComparePullDateTime;
        ws.Cell(2, 2).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
        ws.Cell(3, 2).Value = "< Input Date and Time of last reg pull";

        ws.Range(2, 1, 2, 2).Style.Font.Bold = true;
        ws.Cell(2, 2).Style.Fill.BackgroundColor = XLColor.FromArgb(255, 242, 204);
        ws.Cell(2, 2).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Cell(3, 2).Style.Font.FontColor = XLColor.FromArgb(117, 117, 117);
        ws.Cell(3, 2).Style.Font.Italic = true;

        for (int c = 0; c < PullDataWorkbookColumns.Count; c++)
        {
            string internalHeader = PullDataWorkbookColumns[c];
            ws.Cell(headerRow, firstSourceCol + c).Value = GetDisplayColumnName(internalHeader);
        }

        ws.Cell(headerRow, changeStatusCol).Value = "Change Status";
        ws.Cell(headerRow, changeDetailsCol).Value = "Change Details";
        ws.Cell(headerRow, priorExistsCol).Value = "Helper Prior Exists";
        ws.Cell(headerRow, changedCountCol).Value = "Helper Changed Count";

        for (int i = 0; i < CleanOutputColumns.Count; i++)
        {
            ws.Cell(headerRow, priorValueFirstCol + i).Value = $"Helper Prior {GetDisplayColumnName(CleanOutputColumns[i])}";
        }

        ws.Cell(firstDataRow, firstSourceCol).FormulaA1 =
            $"FILTER({PullDataTableName},{PullDataTableName}[{pullDateTimeDisplay}]=MAX({PullDataTableName}[{pullDateTimeDisplay}]),\"No data\")";

        for (int r = firstDataRow; r <= lastDataRow; r++)
        {
            string rowKeyRef = $"${IndexToColumnLetter(firstSourceCol)}{r}";
            string compareDateTimeRef = "$B$2";
            string priorExistsRef = $"{IndexToColumnLetter(priorExistsCol)}{r}";
            string changedCountRef = $"{IndexToColumnLetter(changedCountCol)}{r}";

            ws.Cell(r, priorExistsCol).FormulaA1 =
                $"IF({rowKeyRef}=\"\",FALSE,COUNTIFS({PullDataTableName}[{rowKeyDisplay}],{rowKeyRef},{PullDataTableName}[{pullDateTimeDisplay}],{compareDateTimeRef})>0)";

            for (int i = 0; i < CleanOutputColumns.Count; i++)
            {
                string displayName = GetDisplayColumnName(CleanOutputColumns[i]);
                ws.Cell(r, priorValueFirstCol + i).FormulaA1 =
                    $"IF({priorExistsRef},IFERROR(INDEX(FILTER({PullDataTableName}[{displayName}],({PullDataTableName}[{rowKeyDisplay}]={rowKeyRef})*({PullDataTableName}[{pullDateTimeDisplay}]={compareDateTimeRef})),1),\"\"),\"\")";
            }

            ws.Cell(r, changedCountCol).FormulaA1 = BuildChangeCountFormula(r, firstSourceCol + 1, priorValueFirstCol, priorExistsRef);
            ws.Cell(r, changeStatusCol).FormulaA1 =
                $"IF({rowKeyRef}=\"\",\"\",IF(NOT({priorExistsRef}),\"New\",IF({changedCountRef}>0,\"Changed\",\"\")))";
            ws.Cell(r, changeDetailsCol).FormulaA1 =
                BuildChangeDetailsFormula(r, firstSourceCol + 1, priorValueFirstCol, priorExistsRef, changedCountRef);
        }

        ws.Range(headerRow, firstSourceCol, headerRow, changeDetailsCol).Style.Font.Bold = true;
        ws.Range(headerRow, firstSourceCol, headerRow, changeDetailsCol).Style.Fill.BackgroundColor = XLColor.FromArgb(217, 217, 217);
        ws.Range(headerRow, firstSourceCol, headerRow, changeDetailsCol).Style.Alignment.WrapText = true;
        ws.Range(headerRow, priorExistsCol, headerRow, priorValueLastCol).Style.Font.Bold = true;
        ws.Range(headerRow, priorExistsCol, headerRow, priorValueLastCol).Style.Fill.BackgroundColor = XLColor.FromArgb(217, 217, 217);

        ws.Range(firstDataRow, firstSourceCol, lastDataRow, changeDetailsCol).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        ws.Range(firstDataRow, changeDetailsCol, lastDataRow, changeDetailsCol).Style.Alignment.WrapText = true;
        ws.Range(firstDataRow, firstSourceCol, lastDataRow, lastSourceCol).Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        ws.Range(firstDataRow, firstSourceCol, lastDataRow, changeDetailsCol).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

        ws.Range(firstDataRow, firstSourceCol, lastDataRow, changeDetailsCol)
            .AddConditionalFormat()
            .WhenIsTrue($"AND({firstDataRowKeyRef}<>\"\",{priorExistsFirstRef}=FALSE)")
            .Fill.SetBackgroundColor(XLColor.FromArgb(226, 239, 218));

        ws.Range(firstDataRow, firstSourceCol + 1, lastDataRow, firstSourceCol + CleanOutputColumns.Count)
            .AddConditionalFormat()
            .WhenIsTrue($"AND({firstDataRowKeyRef}<>\"\",{priorExistsFirstRef}=TRUE,{firstComparableCellRef}<>INDEX({priorValueFirstRef}:{priorValueLastRef},1,COLUMN()-COLUMN(${IndexToColumnLetter(firstSourceCol + 1)}${firstDataRow})+1))")
            .Fill.SetBackgroundColor(XLColor.FromArgb(255, 242, 204));

        ws.Columns(priorExistsCol, priorValueLastCol).Hide();
        ws.SheetView.FreezeRows(1);
        ws.SheetView.FreezeColumns(firstSourceCol + 2);

        ws.Column(1).Width = 22;
        ws.Column(2).Width = 28;
        ws.Column(3).Width = 4;
        ApplyRegistrationListColumnWidths(ws, firstSourceCol, changeStatusCol, changeDetailsCol);
    }

    private static string BuildChangeCountFormula(
        int row,
        int firstCurrentValueCol,
        int firstPriorValueCol,
        string priorExistsRef)
    {
        List<string> comparisons = new();

        for (int i = 0; i < CleanOutputColumns.Count; i++)
        {
            string currentCell = $"{IndexToColumnLetter(firstCurrentValueCol + i)}{row}";
            string priorCell = $"{IndexToColumnLetter(firstPriorValueCol + i)}{row}";
            comparisons.Add($"N({currentCell}<>{priorCell})");
        }

        return $"IF({priorExistsRef},SUM({string.Join(",", comparisons)}),0)";
    }

    private static string BuildChangeDetailsFormula(
        int row,
        int firstCurrentValueCol,
        int firstPriorValueCol,
        string priorExistsRef,
        string changedCountRef)
    {
        List<string> detailParts = new();

        for (int i = 0; i < CleanOutputColumns.Count; i++)
        {
            string displayName = EscapeExcelString(GetDisplayColumnName(CleanOutputColumns[i]));
            string currentCell = $"{IndexToColumnLetter(firstCurrentValueCol + i)}{row}";
            string priorCell = $"{IndexToColumnLetter(firstPriorValueCol + i)}{row}";

            detailParts.Add(
                $"IF({currentCell}<>{priorCell},\"{displayName}: \"&IF({priorCell}=\"\",\"(blank)\",{priorCell})&\" -> \"&IF({currentCell}=\"\",\"(blank)\",{currentCell}),\"\")"
            );
        }

        return $"IF($D{row}=\"\",\"\",IF(NOT({priorExistsRef}),\"New exhibitor\",IF({changedCountRef}=0,\"\",TEXTJOIN(\"; \",TRUE,{string.Join(",", detailParts)}))))";
    }

    private static void ApplyRegistrationListColumnWidths(
        IXLWorksheet ws,
        int firstSourceCol,
        int changeStatusCol,
        int changeDetailsCol)
    {
        for (int i = 0; i < PullDataWorkbookColumns.Count; i++)
        {
            string header = PullDataWorkbookColumns[i];
            double width = header switch
            {
                "RowKey" => 14,
                "PullDate" => 12,
                "PullTime" => 12,
                "PullDateTime" => 18,
                "CompanyName" => 24,
                "CompanyBannerName" => 24,
                "ExhibitorCategory" => 28,
                "CompanyAddress1" => 28,
                "CatalogAddress" => 28,
                _ when header.EndsWith("Email", StringComparison.OrdinalIgnoreCase) => 28,
                _ when header.EndsWith("Title", StringComparison.OrdinalIgnoreCase) => 20,
                _ when header.EndsWith("FirstName", StringComparison.OrdinalIgnoreCase) => 18,
                _ when header.EndsWith("LastName", StringComparison.OrdinalIgnoreCase) => 18,
                _ => 14
            };

            ws.Column(firstSourceCol + i).Width = width;
        }

        ws.Column(changeStatusCol).Width = 16;
        ws.Column(changeDetailsCol).Width = 60;
    }

    private static void CreateShowWorkbook(
        string outputPath,
        EventInfo eventInfo,
        List<Dictionary<string, string>> currentPullRows,
        List<Dictionary<string, string>> allPullRows,
        DateTime runStartedAt,
        string eventsPath,
        string exhibitorsPath,
        string accountsPath,
        string exhibitorCategoriesPath)
    {
        using XLWorkbook workbook = new();
        workbook.CalculateMode = XLCalculateMode.Auto;

        List<PullRunInfo> pullRuns = BuildPullRunList(allPullRows, runStartedAt);
        DateTime defaultPreviousPullDateTime = GetDefaultPreviousPullDateTime(pullRuns);

        AddDateTimePickerWorksheet(workbook, eventInfo, pullRuns, defaultPreviousPullDateTime, runStartedAt);
        AddCurrentRegistrationListWorksheet(workbook, currentPullRows);
        AddPreviousRegistrationListWorksheet(workbook);
        AddPullDataWorksheet(workbook, allPullRows);
        ApplyCurrentVsPreviousConditionalFormatting(workbook.Worksheet(CurrentRegistrationSheetName), currentPullRows.Count);

        SaveWorkbookWithRetry(workbook, outputPath);
    }

    private static void AddDateTimePickerWorksheet(
        XLWorkbook workbook,
        EventInfo eventInfo,
        List<PullRunInfo> pullRuns,
        DateTime selectedPullDateTime,
        DateTime currentPullDateTime)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(DashboardSheetName);

        XLColor navy = XLColor.FromArgb(31, 78, 121);
        XLColor inputFill = XLColor.FromArgb(221, 235, 247);
        XLColor headerFill = XLColor.FromArgb(91, 155, 213);

        ws.Cell(1, 1).Value = $"{eventInfo.EventName} Registration List";
        ws.Range(1, 1, 1, 7).Merge();
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 16;
        ws.Cell(1, 1).Style.Font.FontColor = XLColor.White;
        ws.Cell(1, 1).Style.Fill.BackgroundColor = navy;
        ws.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Cell(2, 1).Value = "Date";
        ws.Cell(3, 1).Value = "Time";
        ws.Cell(2, 2).Value = selectedPullDateTime.Date;
        ws.Cell(3, 2).Value = selectedPullDateTime.TimeOfDay.TotalDays;
        ws.Cell(2, 2).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Cell(3, 2).Style.NumberFormat.Format = "hh:mm:ss";
        ws.Range(2, 2, 3, 2).Style.Fill.BackgroundColor = inputFill;
        ws.Range(2, 2, 3, 2).Style.Font.Bold = true;
        ws.Range(2, 2, 3, 2).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

        ws.Cell(5, 1).Value = "Event";
        ws.Cell(5, 2).Value = eventInfo.EventName;
        ws.Cell(6, 1).Value = "Event ID";
        ws.Cell(6, 2).Value = eventInfo.EventId;
        ws.Cell(7, 1).Value = "Event Start Date";
        ws.Cell(7, 2).Value = eventInfo.StartDate;
        ws.Cell(7, 2).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Cell(8, 1).Value = "Current Pull";
        ws.Cell(8, 2).Value = currentPullDateTime;
        ws.Cell(8, 2).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";

        ws.Range(2, 1, 8, 1).Style.Font.Bold = true;

        ws.Cell(2, 4).Value = "Available Pulls";
        ws.Range(2, 4, 2, 7).Merge();
        ws.Cell(2, 4).Style.Font.Bold = true;
        ws.Cell(2, 4).Style.Font.FontColor = XLColor.White;
        ws.Cell(2, 4).Style.Fill.BackgroundColor = headerFill;

        ws.Cell(3, 4).Value = "Pull date";
        ws.Cell(3, 5).Value = "Pull Time";
        ws.Cell(3, 6).Value = "Rows";
        ws.Cell(3, 7).Value = "Pull Date/Time";
        ws.Range(3, 4, 3, 7).Style.Font.Bold = true;
        ws.Range(3, 4, 3, 7).Style.Fill.BackgroundColor = XLColor.LightGray;

        for (int i = 0; i < pullRuns.Count; i++)
        {
            int row = 4 + i;
            PullRunInfo pullRun = pullRuns[i];

            ws.Cell(row, 4).Value = pullRun.PullDateTime.Date;
            ws.Cell(row, 5).Value = pullRun.PullDateTime.TimeOfDay.TotalDays;
            ws.Cell(row, 6).Value = pullRun.RowCount;
            ws.Cell(row, 7).Value = pullRun.PullDateTime;

            ws.Cell(row, 4).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Cell(row, 5).Style.NumberFormat.Format = "hh:mm:ss";
            ws.Cell(row, 7).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
        }

        ws.Column(1).Width = 18;
        ws.Column(2).Width = 34;
        ws.Column(4).Width = 14;
        ws.Column(5).Width = 12;
        ws.Column(6).Width = 10;
        ws.Column(7).Width = 22;
        ws.SheetView.FreezeRows(1);
    }

    private static void AddCurrentRegistrationListWorksheet(
        XLWorkbook workbook,
        List<Dictionary<string, string>> currentRows)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(CurrentRegistrationSheetName);

        int internalHeaderRow = 1;
        int groupHeaderRow = 2;
        int headerRow = 3;
        int firstDataRow = 4;
        int rowKeyCol = 1;
        int firstVisibleCol = 2;
        int visibleColumnCount = CleanOutputColumns.Count;
        int firstHelperCol = firstVisibleCol + visibleColumnCount;

        List<string> helperColumns = BuildCurrentVsPreviousHelperColumns();
        Dictionary<string, int> helperColumnNumbers = new(StringComparer.OrdinalIgnoreCase);

        int lastCol = firstHelperCol + helperColumns.Count - 1;
        int dataRowCount = Math.Max(currentRows.Count, 1);
        int lastDataRow = firstDataRow + dataRowCount - 1;

        WriteRegistrationListHeaders(ws, internalHeaderRow, groupHeaderRow, headerRow, rowKeyCol, firstVisibleCol);

        for (int i = 0; i < helperColumns.Count; i++)
        {
            int col = firstHelperCol + i;
            string helperName = helperColumns[i];

            helperColumnNumbers[helperName] = col;
            ws.Cell(internalHeaderRow, col).Value = helperName;
            ws.Cell(headerRow, col).Value = helperName;
        }

        for (int r = 0; r < currentRows.Count; r++)
        {
            int excelRow = firstDataRow + r;
            Dictionary<string, string> sourceRow = currentRows[r];

            ws.Cell(excelRow, rowKeyCol).Value = GetDictionaryValue(sourceRow, "RowKey");

            for (int c = 0; c < CleanOutputColumns.Count; c++)
            {
                string internalName = CleanOutputColumns[c];
                SetWorksheetCellValue(ws.Cell(excelRow, firstVisibleCol + c), internalName, GetDictionaryValue(sourceRow, internalName));
            }

            string rowKeyRef = $"$A{excelRow}";
            string isNewCell = $"{IndexToColumnLetter(helperColumnNumbers["Helper_IsNewFromPrevious"])}{excelRow}";
            string changedCountCell = $"{IndexToColumnLetter(helperColumnNumbers["Helper_ChangedFieldCount"])}{excelRow}";

            ws.Cell(excelRow, helperColumnNumbers["Helper_IsNewFromPrevious"]).FormulaA1 =
                $"AND({rowKeyRef}<>\"\",COUNTIF('{PreviousRegistrationSheetName}'!$A:$A,{rowKeyRef})=0)";

            ws.Cell(excelRow, helperColumnNumbers["Helper_ChangedFieldCount"]).FormulaA1 =
                BuildChangedCellCountFormula(excelRow, firstVisibleCol);

            ws.Cell(excelRow, helperColumnNumbers["Helper_IsAffectedFromPrevious"]).FormulaA1 =
                $"OR({isNewCell}=TRUE,{changedCountCell}>0)";

            ws.Cell(excelRow, helperColumnNumbers["Helper_MissingRequiredCount"]).FormulaA1 =
                BuildMissingCountFormula(excelRow, firstVisibleCol);
        }

        if (currentRows.Count == 0)
        {
            ws.Cell(firstDataRow, firstVisibleCol).Value = "No active exhibitors found.";
        }

        IXLRange tableRange = ws.Range(headerRow, rowKeyCol, lastDataRow, lastCol);
        tableRange.CreateTable(CurrentRegistrationTableName);

        FormatRegistrationListSheet(ws, internalHeaderRow, groupHeaderRow, headerRow, rowKeyCol, firstVisibleCol, lastCol, firstHelperCol);
    }

    private static void AddPreviousRegistrationListWorksheet(XLWorkbook workbook)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(PreviousRegistrationSheetName);

        int internalHeaderRow = 1;
        int groupHeaderRow = 2;
        int headerRow = 3;
        int rowKeyCol = 1;
        int firstVisibleCol = 2;
        int lastCol = firstVisibleCol + CleanOutputColumns.Count - 1;

        WriteRegistrationListHeaders(ws, internalHeaderRow, groupHeaderRow, headerRow, rowKeyCol, firstVisibleCol);

        ws.Cell(4, 1).FormulaA1 = BuildPreviousRegistrationListFormula();

        FormatRegistrationListSheet(ws, internalHeaderRow, groupHeaderRow, headerRow, rowKeyCol, firstVisibleCol, lastCol, firstHelperCol: lastCol + 1);
    }

    private static void AddPullDataWorksheet(
        XLWorkbook workbook,
        List<Dictionary<string, string>> pullRows)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(PullDataSheetName);

        for (int c = 0; c < PullDataWorkbookColumns.Count; c++)
        {
            string internalHeader = PullDataWorkbookColumns[c];
            ws.Cell(1, c + 1).Value = GetDisplayColumnName(internalHeader);
            ws.Cell(1, c + 1).Style.Font.Bold = true;
            ws.Cell(1, c + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        int dataRowCount = Math.Max(pullRows.Count, 1);

        for (int r = 0; r < dataRowCount; r++)
        {
            Dictionary<string, string>? row = r < pullRows.Count ? pullRows[r] : null;

            for (int c = 0; c < PullDataWorkbookColumns.Count; c++)
            {
                string internalHeader = PullDataWorkbookColumns[c];
                string value = row == null ? "" : GetDictionaryValue(row, internalHeader);
                SetPullDataCellValue(ws.Cell(r + 2, c + 1), internalHeader, value);
            }
        }

        IXLRange tableRange = ws.Range(1, 1, dataRowCount + 1, PullDataWorkbookColumns.Count);
        tableRange.CreateTable(PullDataTableName);

        ws.SheetView.FreezeRows(1);

        for (int i = 0; i < PullDataWorkbookColumns.Count; i++)
        {
            string header = PullDataWorkbookColumns[i];
            double width = header switch
            {
                "RowKey" => 18,
                "PullDate" => 14,
                "PullTime" => 12,
                "PullDateTime" => 22,
                "CompanyName" => 28,
                "CompanyBannerName" => 28,
                _ when header.EndsWith("Email", StringComparison.OrdinalIgnoreCase) => 28,
                _ when header.Contains("Address", StringComparison.OrdinalIgnoreCase) => 28,
                _ => 16
            };

            ws.Column(i + 1).Width = width;
        }
    }

    private static void WriteRegistrationListHeaders(
        IXLWorksheet ws,
        int internalHeaderRow,
        int groupHeaderRow,
        int headerRow,
        int rowKeyCol,
        int firstVisibleCol)
    {
        ws.Cell(internalHeaderRow, rowKeyCol).Value = "RowKey";
        ws.Cell(headerRow, rowKeyCol).Value = "RowKey";

        for (int i = 0; i < CleanOutputColumns.Count; i++)
        {
            int col = firstVisibleCol + i;
            string internalName = CleanOutputColumns[i];

            ws.Cell(internalHeaderRow, col).Value = internalName;
            ws.Cell(headerRow, col).Value = GetDisplayColumnName(internalName);
        }

        AddCurrentListGroupHeaders(ws, groupHeaderRow, firstVisibleCol);
    }

    private static void FormatRegistrationListSheet(
        IXLWorksheet ws,
        int internalHeaderRow,
        int groupHeaderRow,
        int headerRow,
        int rowKeyCol,
        int firstVisibleCol,
        int lastCol,
        int firstHelperCol)
    {
        XLColor navy = XLColor.FromArgb(31, 78, 121);
        XLColor sectionFill = XLColor.FromArgb(91, 155, 213);

        ws.Range(headerRow, rowKeyCol, headerRow, lastCol).Style.Font.Bold = true;
        ws.Range(headerRow, rowKeyCol, headerRow, lastCol).Style.Fill.BackgroundColor = navy;
        ws.Range(headerRow, rowKeyCol, headerRow, lastCol).Style.Font.FontColor = XLColor.White;
        ws.Range(headerRow, rowKeyCol, headerRow, lastCol).Style.Alignment.WrapText = true;

        ws.Range(groupHeaderRow, firstVisibleCol, groupHeaderRow, firstVisibleCol + CleanOutputColumns.Count - 1).Style.Fill.BackgroundColor = sectionFill;
        ws.Range(groupHeaderRow, firstVisibleCol, groupHeaderRow, firstVisibleCol + CleanOutputColumns.Count - 1).Style.Font.FontColor = XLColor.White;
        ws.Range(groupHeaderRow, firstVisibleCol, groupHeaderRow, firstVisibleCol + CleanOutputColumns.Count - 1).Style.Font.Bold = true;
        ws.Range(groupHeaderRow, firstVisibleCol, groupHeaderRow, firstVisibleCol + CleanOutputColumns.Count - 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Row(internalHeaderRow).Hide();
        ws.Column(rowKeyCol).Hide();

        for (int col = firstHelperCol; col <= lastCol; col++)
        {
            ws.Column(col).Hide();
        }

        ws.SheetView.FreezeRows(headerRow);
        ws.SheetView.FreezeColumns(4);
        ApplyCurrentListColumnWidths(ws, firstVisibleCol);
    }

    private static List<string> BuildCurrentVsPreviousHelperColumns()
    {
        return new List<string>
        {
            "Helper_IsNewFromPrevious",
            "Helper_ChangedFieldCount",
            "Helper_IsAffectedFromPrevious",
            "Helper_MissingRequiredCount"
        };
    }

    private static string BuildPreviousRegistrationListFormula()
    {
        string pickerSheet = FormulaSheetName(DashboardSheetName);
        string lastDisplayColumn = GetDisplayColumnName(CleanOutputColumns.Last());

        return $"LET(selected,{pickerSheet}!$B$2+{pickerSheet}!$B$3,pullStamp,{PullDataTableName}[Pull date]+{PullDataTableName}[Pull Time],latest,IFERROR(MAX(FILTER(pullStamp,pullStamp<=selected)),\"\"),IF(latest=\"\",\"No pull found for selected date/time\",FILTER({PullDataTableName}[[RowKey]:[{lastDisplayColumn}]],pullStamp=latest,\"No pull found for selected date/time\")))";
    }

    private static string BuildChangedCellCountFormula(int excelRow, int firstVisibleCol)
    {
        List<string> parts = new();
        string previousSheet = FormulaSheetName(PreviousRegistrationSheetName);
        int lastVisibleCol = firstVisibleCol + CleanOutputColumns.Count - 1;
        string previousRange = $"{previousSheet}!${IndexToColumnLetter(firstVisibleCol)}:${IndexToColumnLetter(lastVisibleCol)}";
        string previousKeys = $"{previousSheet}!$A:$A";

        for (int i = 0; i < CleanOutputColumns.Count; i++)
        {
            string colLetter = IndexToColumnLetter(firstVisibleCol + i);
            parts.Add($"N(AND(COUNTIF({previousKeys},$A{excelRow})>0,{colLetter}{excelRow}<>IFERROR(INDEX({previousRange},MATCH($A{excelRow},{previousKeys},0),{i + 1}),\"\")))");
        }

        return string.Join("+", parts);
    }

    private static void ApplyCurrentVsPreviousConditionalFormatting(IXLWorksheet ws, int actualRowCount)
    {
        if (actualRowCount <= 0)
            return;

        int firstDataRow = 4;
        int lastDataRow = firstDataRow + actualRowCount - 1;
        int firstVisibleCol = 2;
        int lastVisibleCol = firstVisibleCol + CleanOutputColumns.Count - 1;
        int firstHelperCol = firstVisibleCol + CleanOutputColumns.Count;

        string previousSheet = FormulaSheetName(PreviousRegistrationSheetName);
        string rowKeyReference = $"$A{firstDataRow}";
        string topLeftAddress = $"{IndexToColumnLetter(firstVisibleCol)}{firstDataRow}";
        string helperIsNewReference = $"${IndexToColumnLetter(firstHelperCol)}{firstDataRow}";
        string previousKeys = $"{previousSheet}!$A:$A";
        string previousRange = $"{previousSheet}!${IndexToColumnLetter(firstVisibleCol)}:${IndexToColumnLetter(lastVisibleCol)}";

        IXLRange visibleDataRange = ws.Range(firstDataRow, firstVisibleCol, lastDataRow, lastVisibleCol);

        visibleDataRange
            .AddConditionalFormat()
            .WhenIsTrue($"AND({rowKeyReference}<>\"\",{helperIsNewReference}=TRUE)")
            .Fill.SetBackgroundColor(XLColor.FromArgb(226, 239, 218));

        visibleDataRange
            .AddConditionalFormat()
            .WhenIsTrue($"AND({rowKeyReference}<>\"\",COUNTIF({previousKeys},{rowKeyReference})>0,{topLeftAddress}<>IFERROR(INDEX({previousRange},MATCH({rowKeyReference},{previousKeys},0),COLUMN()-{firstVisibleCol - 1}),\"\"))")
            .Fill.SetBackgroundColor(XLColor.FromArgb(255, 242, 204));
    }

    private static string FormulaSheetName(string sheetName)
    {
        return $"'{sheetName.Replace("'", "''")}'";
    }

    private static void SetPullDataCellValue(IXLCell cell, string header, string value)
    {
        if (header.Equals("PullDate", StringComparison.OrdinalIgnoreCase)
            && TryParseDate(value, out DateTime pullDate))
        {
            cell.Value = pullDate.Date;
            cell.Style.DateFormat.Format = "yyyy-mm-dd";
            return;
        }

        if (header.Equals("PullTime", StringComparison.OrdinalIgnoreCase)
            && TryParseTime(value, out TimeSpan pullTime))
        {
            cell.Value = pullTime.TotalDays;
            cell.Style.NumberFormat.Format = "hh:mm:ss";
            return;
        }

        if (header.Equals(PullDateTimeColumnName, StringComparison.OrdinalIgnoreCase))
        {
            int row = cell.Address.RowNumber;
            int pullDateCol = PullDataWorkbookColumns.FindIndex(column => column.Equals("PullDate", StringComparison.OrdinalIgnoreCase)) + 1;
            int pullTimeCol = PullDataWorkbookColumns.FindIndex(column => column.Equals("PullTime", StringComparison.OrdinalIgnoreCase)) + 1;
            string pullDateCell = $"{IndexToColumnLetter(pullDateCol)}{row}";
            string pullTimeCell = $"{IndexToColumnLetter(pullTimeCol)}{row}";

            cell.FormulaA1 = $"IF(OR({pullDateCell}=\"\",{pullTimeCell}=\"\"),\"\",{pullDateCell}+{pullTimeCell})";
            cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
            return;
        }

        SetWorksheetCellValue(cell, header, value);
    }

    // ============================================================
    // DYNAMIC WORKBOOK CREATION
    // ============================================================

    private static void CreateDynamicWorkbook(
        string outputPath,
        EventInfo eventInfo,
        List<Dictionary<string, string>> currentWorkbookRows,
        List<Dictionary<string, string>> exhibitorHistoryRows,
        List<Dictionary<string, string>> changeHistoryRows,
        ComparisonResult sinceLastRun,
        ComparisonResult sinceDailyBaseline,
        string eventsPath,
        string exhibitorsPath,
        string accountsPath,
        string exhibitorCategoriesPath,
        string latestSnapshotPath,
        string dailyBaselineSnapshotPath,
        string exhibitorHistoryPath,
        string changeHistoryPath)
    {
        using XLWorkbook workbook = new();
        workbook.CalculateMode = XLCalculateMode.Auto;

        IXLWorksheet dashboardSheet = AddDashboardWorksheet(workbook, eventInfo);

        List<Dictionary<string, string>> changeWorkbookRows = BuildChangeWorkbookRows(changeHistoryRows);
        List<Dictionary<string, string>> fieldMapRows = BuildFieldMapRows();

        // Hidden source tables power the dynamic workbook. Do not auto-fit these sheets:
        // auto-fitting large hidden tables is one of the biggest causes of long run times.
        AddWorksheetTable(
            workbook,
            CurrentDataSheetName,
            CurrentDataTableName,
            currentWorkbookRows,
            CurrentDataWorkbookColumns,
            hideSheet: true
        );

        AddWorksheetTable(
            workbook,
            ExhibitorHistorySheetName,
            ExhibitorHistoryTableName,
            exhibitorHistoryRows,
            ExhibitorHistoryColumns,
            hideSheet: true
        );

        AddWorksheetTable(
            workbook,
            ChangeHistorySheetName,
            ChangeHistoryTableName,
            changeWorkbookRows,
            ChangeHistoryWorkbookColumns,
            hideSheet: true
        );

        AddWorksheetTable(
            workbook,
            FieldMapSheetName,
            FieldMapTableName,
            fieldMapRows,
            new List<string>
            {
                "InternalFieldName",
                "DisplayName",
                "OutputOrder",
                "VisibleInCurrentList",
                "Required",
                "HighlightChanged"
            },
            hideSheet: true
        );

        AddCurrentRegistrationWorksheet(workbook, currentWorkbookRows);
        AddNewSinceSelectedDateWorksheet(workbook);
        AddChangedSinceSelectedDateWorksheet(workbook);
        AddDataQualityWorksheet(workbook, currentWorkbookRows);
        AddRunInfoWorksheet(
            workbook,
            eventInfo,
            currentWorkbookRows,
            exhibitorHistoryRows,
            changeHistoryRows,
            sinceLastRun,
            sinceDailyBaseline,
            eventsPath,
            exhibitorsPath,
            accountsPath,
            exhibitorCategoriesPath,
            latestSnapshotPath,
            dailyBaselineSnapshotPath,
            exhibitorHistoryPath,
            changeHistoryPath
        );

        AddDashboardSummaryFormulas(dashboardSheet);
        SaveWorkbookWithRetry(workbook, outputPath);
    }

    private static IXLWorksheet AddDashboardWorksheet(XLWorkbook workbook, EventInfo eventInfo)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(DashboardSheetName);

        XLColor navy = XLColor.FromArgb(31, 78, 121);
        XLColor mediumBlue = XLColor.FromArgb(91, 155, 213);
        XLColor cardFill = XLColor.FromArgb(243, 246, 250);
        XLColor inputFill = XLColor.FromArgb(221, 235, 247);
        XLColor borderColor = XLColor.FromArgb(217, 226, 243);

        ws.Cell(1, 1).Value = $"{eventInfo.EventName} Registration List";
        ws.Range(1, 1, 1, 6).Merge();
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 18;
        ws.Cell(1, 1).Style.Font.FontColor = XLColor.White;
        ws.Cell(1, 1).Style.Fill.BackgroundColor = navy;
        ws.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Cell(3, 1).Value = "Event";
        ws.Cell(3, 2).Value = eventInfo.EventName;
        ws.Cell(4, 1).Value = "Event ID";
        ws.Cell(4, 2).Value = eventInfo.EventId;
        ws.Cell(5, 1).Value = "Event Start Date";
        ws.Cell(5, 2).Value = eventInfo.StartDate;
        ws.Cell(5, 2).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Cell(6, 1).Value = "Workbook Generated";
        ws.Cell(6, 2).Value = DateTime.Now;
        ws.Cell(6, 2).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";

        ws.Cell(DashboardCompareDateRow, 1).Value = "Show Activity Since";
        ws.Cell(DashboardCompareDateRow, DashboardCompareDateColumn).Value = DateTime.Today;
        ws.Cell(DashboardCompareDateRow, DashboardCompareDateColumn).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Cell(DashboardCompareDateRow, DashboardCompareDateColumn).Style.Fill.BackgroundColor = inputFill;
        ws.Cell(DashboardCompareDateRow, DashboardCompareDateColumn).Style.Font.Bold = true;
        ws.Cell(DashboardCompareDateRow, DashboardCompareDateColumn).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Cell(DashboardCompareDateRow, DashboardCompareDateColumn).Style.Border.OutsideBorderColor = navy;

        ws.Range(3, 1, DashboardCompareDateRow, 1).Style.Font.Bold = true;
        ws.Range(3, 1, DashboardCompareDateRow, 2).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(3, 1, DashboardCompareDateRow, 2).Style.Border.OutsideBorderColor = borderColor;

        ws.Cell(9, 1).Value = "Summary";
        ws.Range(9, 1, 9, 6).Merge();
        ws.Cell(9, 1).Style.Fill.BackgroundColor = mediumBlue;
        ws.Cell(9, 1).Style.Font.FontColor = XLColor.White;
        ws.Cell(9, 1).Style.Font.Bold = true;

        AddDashboardMetricCard(ws, 11, 1, "Current Exhibitors");
        AddDashboardMetricCard(ws, 11, 3, "New Since Date");
        AddDashboardMetricCard(ws, 11, 5, "Exhibitors with Changes");
        AddDashboardMetricCard(ws, 14, 1, "Changed Fields");
        AddDashboardMetricCard(ws, 14, 3, "Exhibitors to Review");
        AddDashboardMetricCard(ws, 14, 5, "Missing Required Fields");

        ws.Range(11, 1, 16, 6).Style.Fill.BackgroundColor = cardFill;
        ws.Range(11, 1, 16, 6).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(11, 1, 16, 6).Style.Border.OutsideBorderColor = borderColor;

        ws.Cell(18, 1).Value = "Color Guide";
        ws.Range(18, 1, 18, 6).Merge();
        ws.Cell(18, 1).Style.Fill.BackgroundColor = mediumBlue;
        ws.Cell(18, 1).Style.Font.FontColor = XLColor.White;
        ws.Cell(18, 1).Style.Font.Bold = true;

        ws.Cell(20, 1).Style.Fill.BackgroundColor = XLColor.FromArgb(226, 239, 218);
        ws.Cell(20, 2).Value = "New exhibitor since selected date";
        ws.Cell(21, 1).Style.Fill.BackgroundColor = XLColor.FromArgb(255, 242, 204);
        ws.Cell(21, 2).Value = "Field changed since selected date";
        ws.Cell(22, 1).Style.Fill.BackgroundColor = XLColor.FromArgb(252, 228, 214);
        ws.Cell(22, 2).Value = "Required information is missing";
        ws.Range(20, 1, 22, 2).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(20, 1, 22, 2).Style.Border.OutsideBorderColor = borderColor;

        ws.Cell(24, 1).Value = "How to use this workbook";
        ws.Range(24, 1, 24, 6).Merge();
        ws.Cell(24, 1).Style.Fill.BackgroundColor = mediumBlue;
        ws.Cell(24, 1).Style.Font.FontColor = XLColor.White;
        ws.Cell(24, 1).Style.Font.Bold = true;

        ws.Cell(26, 1).Value = "1. Choose a date in Show Activity Since.";
        ws.Cell(27, 1).Value = "2. Review the summary boxes.";
        ws.Cell(28, 1).Value = "3. Use Current List for the full registration list.";
        ws.Cell(29, 1).Value = "4. Use New Since Date and Changes Since Date for details.";
        ws.Cell(30, 1).Value = "5. Green means new, yellow means changed, and red means required information is missing.";
        ws.Cell(32, 1).Value = "Note: New means first seen by this automation history. It may not mean the exhibitor signed up on that date.";

        foreach (int instructionRow in new[] { 26, 27, 28, 29, 30, 32 })
        {
            ws.Range(instructionRow, 1, instructionRow, 6).Merge();
        }

        ws.Range(26, 1, 32, 6).Style.Alignment.WrapText = true;

        ws.Column(1).Width = 25;
        ws.Column(2).Width = 28;
        ws.Column(3).Width = 22;
        ws.Column(4).Width = 22;
        ws.Column(5).Width = 24;
        ws.Column(6).Width = 20;
        ws.Rows(1, 32).Height = 20;
        ws.Row(1).Height = 28;
        ws.SheetView.FreezeRows(1);

        return ws;
    }

    private static void AddDashboardMetricCard(IXLWorksheet ws, int row, int column, string label)
    {
        ws.Range(row, column, row, column + 1).Merge();
        ws.Cell(row, column).Value = label;
        ws.Cell(row, column).Style.Font.Bold = true;
        ws.Cell(row, column).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Range(row + 1, column, row + 1, column + 1).Merge();
        ws.Cell(row + 1, column).Style.Font.Bold = true;
        ws.Cell(row + 1, column).Style.Font.FontSize = 16;
        ws.Cell(row + 1, column).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(row + 1, column).Style.NumberFormat.Format = "#,##0";
    }

    private static void AddDashboardSummaryFormulas(IXLWorksheet ws)
    {
        ws.Cell(12, 1).FormulaA1 = "COUNTIF(tblCurrentData[RowKey],\"<>\")";
        ws.Cell(12, 3).FormulaA1 = $"COUNTIFS(tblCurrentData[RowKey],\"<>\",tblCurrentData[FirstSeenDate],\">=\"&{CompareDateCellReference})";
        ws.Cell(12, 5).FormulaA1 = "COUNTIFS(tblCurrentRegistration[Helper_ChangedFieldCountSinceSelectedDate],\">0\",tblCurrentRegistration[RowKey],\"<>\")";
        ws.Cell(15, 1).FormulaA1 = $"COUNTIFS(tblChangeHistory[RowKey],\"<>\",tblChangeHistory[ChangeDate],\">=\"&{CompareDateCellReference})";
        ws.Cell(15, 3).FormulaA1 = "COUNTIFS(tblCurrentRegistration[Helper_IsAffectedSinceSelectedDate],TRUE,tblCurrentRegistration[RowKey],\"<>\")";
        ws.Cell(15, 5).FormulaA1 = "SUM(tblCurrentRegistration[Helper_MissingRequiredCount])";

        ws.Range(12, 1, 12, 6).Style.NumberFormat.Format = "#,##0";
        ws.Range(15, 1, 15, 6).Style.NumberFormat.Format = "#,##0";
    }

    private static void AddCurrentRegistrationWorksheet(
        XLWorkbook workbook,
        List<Dictionary<string, string>> currentRows)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(CurrentRegistrationSheetName);

        int internalHeaderRow = 1;
        int groupHeaderRow = 2;
        int headerRow = 3;
        int firstDataRow = 4;
        int rowKeyCol = 1;
        int firstVisibleCol = 2;
        int visibleColumnCount = CleanOutputColumns.Count;
        int firstHelperCol = firstVisibleCol + visibleColumnCount;

        List<string> helperColumns = BuildCurrentHelperColumns();
        Dictionary<string, int> helperColumnNumbers = new(StringComparer.OrdinalIgnoreCase);

        int lastCol = firstHelperCol + helperColumns.Count - 1;
        int dataRowCount = Math.Max(currentRows.Count, 1);
        int lastDataRow = firstDataRow + dataRowCount - 1;

        ws.Cell(internalHeaderRow, rowKeyCol).Value = "RowKey";
        ws.Cell(headerRow, rowKeyCol).Value = "RowKey";

        for (int i = 0; i < CleanOutputColumns.Count; i++)
        {
            int col = firstVisibleCol + i;
            string internalName = CleanOutputColumns[i];

            ws.Cell(internalHeaderRow, col).Value = internalName;
            ws.Cell(headerRow, col).Value = GetDisplayColumnName(internalName);
        }

        for (int i = 0; i < helperColumns.Count; i++)
        {
            int col = firstHelperCol + i;
            string helperName = helperColumns[i];

            helperColumnNumbers[helperName] = col;
            ws.Cell(internalHeaderRow, col).Value = helperName;
            ws.Cell(headerRow, col).Value = helperName;
        }

        AddCurrentListGroupHeaders(ws, groupHeaderRow, firstVisibleCol);

        for (int r = 0; r < currentRows.Count; r++)
        {
            int excelRow = firstDataRow + r;
            Dictionary<string, string> sourceRow = currentRows[r];

            ws.Cell(excelRow, rowKeyCol).Value = GetDictionaryValue(sourceRow, "RowKey");

            for (int c = 0; c < CleanOutputColumns.Count; c++)
            {
                string internalName = CleanOutputColumns[c];
                IXLCell cell = ws.Cell(excelRow, firstVisibleCol + c);
                SetWorksheetCellValue(cell, internalName, GetDictionaryValue(sourceRow, internalName));
            }

            int firstSeenCol = helperColumnNumbers["Helper_FirstSeenDate"];
            SetWorksheetCellValue(ws.Cell(excelRow, firstSeenCol), "FirstSeenDate", GetDictionaryValue(sourceRow, "FirstSeenDate"));

            int isNewCol = helperColumnNumbers["Helper_IsNewSinceSelectedDate"];
            int changedCountCol = helperColumnNumbers["Helper_ChangedFieldCountSinceSelectedDate"];
            int isAffectedCol = helperColumnNumbers["Helper_IsAffectedSinceSelectedDate"];
            int missingCountCol = helperColumnNumbers["Helper_MissingRequiredCount"];

            string rowKeyRef = $"$A{excelRow}";
            string firstSeenRef = $"{IndexToColumnLetter(firstSeenCol)}{excelRow}";
            string changedCountRef = $"{IndexToColumnLetter(changedCountCol)}{excelRow}";
            string isNewRef = $"{IndexToColumnLetter(isNewCol)}{excelRow}";

            ws.Cell(excelRow, isNewCol).FormulaA1 = $"AND({rowKeyRef}<>\"\",{firstSeenRef}>={CompareDateCellReference})";
            ws.Cell(excelRow, changedCountCol).FormulaA1 = $"COUNTIFS(tblChangeHistory[RowKey],{rowKeyRef},tblChangeHistory[ChangeDate],\">=\"&{CompareDateCellReference})";
            ws.Cell(excelRow, isAffectedCol).FormulaA1 = $"OR({isNewRef}=TRUE,{changedCountRef}>0)";
            ws.Cell(excelRow, missingCountCol).FormulaA1 = BuildMissingCountFormula(excelRow, firstVisibleCol);
        }

        if (currentRows.Count == 0)
        {
            ws.Cell(firstDataRow, firstVisibleCol).Value = "No active exhibitors found.";
        }

        IXLRange tableRange = ws.Range(headerRow, rowKeyCol, lastDataRow, lastCol);
        tableRange.CreateTable(CurrentRegistrationTableName);

        XLColor navy = XLColor.FromArgb(31, 78, 121);
        XLColor sectionFill = XLColor.FromArgb(91, 155, 213);

        ws.Range(headerRow, rowKeyCol, headerRow, lastCol).Style.Font.Bold = true;
        ws.Range(headerRow, rowKeyCol, headerRow, lastCol).Style.Fill.BackgroundColor = navy;
        ws.Range(headerRow, rowKeyCol, headerRow, lastCol).Style.Font.FontColor = XLColor.White;
        ws.Range(headerRow, rowKeyCol, headerRow, lastCol).Style.Alignment.WrapText = true;

        ws.Range(groupHeaderRow, firstVisibleCol, groupHeaderRow, firstVisibleCol + visibleColumnCount - 1).Style.Fill.BackgroundColor = sectionFill;
        ws.Range(groupHeaderRow, firstVisibleCol, groupHeaderRow, firstVisibleCol + visibleColumnCount - 1).Style.Font.FontColor = XLColor.White;
        ws.Range(groupHeaderRow, firstVisibleCol, groupHeaderRow, firstVisibleCol + visibleColumnCount - 1).Style.Font.Bold = true;
        ws.Range(groupHeaderRow, firstVisibleCol, groupHeaderRow, firstVisibleCol + visibleColumnCount - 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Row(internalHeaderRow).Hide();
        ws.Column(rowKeyCol).Hide();

        for (int col = firstHelperCol; col <= lastCol; col++)
        {
            ws.Column(col).Hide();
        }

        if (currentRows.Count > 0)
        {
            ApplyCurrentRegistrationConditionalFormatting(
                ws,
                firstDataRow,
                firstDataRow + currentRows.Count - 1,
                firstVisibleCol,
                visibleColumnCount,
                helperColumnNumbers
            );
        }

        ws.SheetView.FreezeRows(headerRow);
        ws.SheetView.FreezeColumns(4);
        ApplyCurrentListColumnWidths(ws, firstVisibleCol);
    }

    private static void AddCurrentListGroupHeaders(IXLWorksheet ws, int row, int firstVisibleCol)
    {
        AddMergedGroupHeader(ws, row, firstVisibleCol + 0, firstVisibleCol + 9, "Booth / Company");
        AddMergedGroupHeader(ws, row, firstVisibleCol + 10, firstVisibleCol + 13, "Company Address");
        AddMergedGroupHeader(ws, row, firstVisibleCol + 14, firstVisibleCol + 19, "Catalog / Web");
        AddMergedGroupHeader(ws, row, firstVisibleCol + 20, firstVisibleCol + 25, "Main Contact");
        AddMergedGroupHeader(ws, row, firstVisibleCol + 26, firstVisibleCol + 31, "Booth Contact");
        AddMergedGroupHeader(ws, row, firstVisibleCol + 32, firstVisibleCol + 37, "Press Contact");
    }

    private static void AddMergedGroupHeader(IXLWorksheet ws, int row, int firstCol, int lastCol, string label)
    {
        ws.Range(row, firstCol, row, lastCol).Merge();
        ws.Cell(row, firstCol).Value = label;
    }

    private static void ApplyCurrentListColumnWidths(IXLWorksheet ws, int firstVisibleCol)
    {
        Dictionary<string, double> widths = new(StringComparer.OrdinalIgnoreCase)
        {
            ["BoothNumber"] = 14,
            ["CompanyName"] = 28,
            ["CompanyBannerName"] = 28,
            ["ExhibitorStatus"] = 18,
            ["OrderedArea"] = 12,
            ["ExhibitorCategory"] = 24,
            ["ExhibitorType"] = 16,
            ["MainExhibitorCompany"] = 24,
            ["OrderedLength"] = 13,
            ["OrderedWidth"] = 13,
            ["CompanyAddress1"] = 28,
            ["CompanyCity"] = 18,
            ["CompanyState"] = 14,
            ["CompanyPostalCode"] = 14,
            ["CatalogAddress"] = 28,
            ["CatalogCity"] = 18,
            ["CatalogState"] = 14,
            ["CatalogPostalCode"] = 14,
            ["CatalogCountryName"] = 18,
            ["ExhibitorWebsite"] = 28,
            ["MainContactFirstName"] = 16,
            ["MainContactLastName"] = 16,
            ["MainContactEmail"] = 28,
            ["MainContactPhone"] = 18,
            ["MainContactMobile"] = 18,
            ["MainContactTitle"] = 22,
            ["BoothContactFirstName"] = 16,
            ["BoothContactLastName"] = 16,
            ["BoothContactEmail"] = 28,
            ["BoothContactPhone"] = 18,
            ["BoothContactMobile"] = 18,
            ["BoothContactTitle"] = 22,
            ["PressContactFirstName"] = 16,
            ["PressContactLastName"] = 16,
            ["PressContactEmail"] = 28,
            ["PressContactPhone"] = 18,
            ["PressContactMobile"] = 18,
            ["PressContactTitle"] = 22
        };

        for (int i = 0; i < CleanOutputColumns.Count; i++)
        {
            string column = CleanOutputColumns[i];
            ws.Column(firstVisibleCol + i).Width = widths.TryGetValue(column, out double width) ? width : 18;
            ws.Column(firstVisibleCol + i).Style.Alignment.WrapText = false;
        }
    }

    private static List<string> BuildCurrentHelperColumns()
    {
        return new List<string>
        {
            "Helper_FirstSeenDate",
            "Helper_IsNewSinceSelectedDate",
            "Helper_ChangedFieldCountSinceSelectedDate",
            "Helper_IsAffectedSinceSelectedDate",
            "Helper_MissingRequiredCount"
        };
    }

    private static void ApplyCurrentRegistrationConditionalFormatting(
        IXLWorksheet ws,
        int firstDataRow,
        int lastDataRow,
        int firstVisibleCol,
        int visibleColumnCount,
        Dictionary<string, int> helperColumnNumbers)
    {
        int lastVisibleCol = firstVisibleCol + visibleColumnCount - 1;
        IXLRange visibleDataRange = ws.Range(firstDataRow, firstVisibleCol, lastDataRow, lastVisibleCol);

        string firstTopLeftAddress = $"{IndexToColumnLetter(firstVisibleCol)}{firstDataRow}";
        string rowKeyReference = $"$A{firstDataRow}";
        string firstSeenColLetter = IndexToColumnLetter(helperColumnNumbers["Helper_FirstSeenDate"]);
        string firstSeenReference = $"${firstSeenColLetter}{firstDataRow}";
        string internalHeaderReference = $"{IndexToColumnLetter(firstVisibleCol)}$1";

        // Priority is controlled by formula exclusivity: missing > changed > new.
        visibleDataRange
            .AddConditionalFormat()
            .WhenIsTrue($"AND({rowKeyReference}<>\"\",{firstSeenReference}>={CompareDateCellReference},COUNTIFS(tblChangeHistory[RowKey],{rowKeyReference},tblChangeHistory[FieldChanged],{internalHeaderReference},tblChangeHistory[ChangeDate],\">=\"&{CompareDateCellReference})=0,NOT(AND(COUNTIFS(tblFieldMap[InternalFieldName],{internalHeaderReference},tblFieldMap[Required],TRUE)>0,LEN(TRIM({firstTopLeftAddress}&\"\"))=0)))")
            .Fill.SetBackgroundColor(XLColor.FromArgb(226, 239, 218));

        visibleDataRange
            .AddConditionalFormat()
            .WhenIsTrue($"AND(COUNTIFS(tblChangeHistory[RowKey],{rowKeyReference},tblChangeHistory[FieldChanged],{internalHeaderReference},tblChangeHistory[ChangeDate],\">=\"&{CompareDateCellReference})>0,NOT(AND(COUNTIFS(tblFieldMap[InternalFieldName],{internalHeaderReference},tblFieldMap[Required],TRUE)>0,LEN(TRIM({firstTopLeftAddress}&\"\"))=0)))")
            .Fill.SetBackgroundColor(XLColor.FromArgb(255, 242, 204));

        if (FlagMissingKeyData)
        {
            visibleDataRange
                .AddConditionalFormat()
                .WhenIsTrue($"AND(COUNTIFS(tblFieldMap[InternalFieldName],{internalHeaderReference},tblFieldMap[Required],TRUE)>0,LEN(TRIM({firstTopLeftAddress}&\"\"))=0)")
                .Fill.SetBackgroundColor(XLColor.FromArgb(252, 228, 214));
        }
    }

    private static string BuildMissingCountFormula(int excelRow, int firstVisibleCol)
    {
        if (!FlagMissingKeyData)
            return "0";

        List<string> parts = new();

        for (int i = 0; i < CleanOutputColumns.Count; i++)
        {
            string internalName = CleanOutputColumns[i];

            if (!MissingDataWatchColumns.Contains(internalName))
                continue;

            string colLetter = IndexToColumnLetter(firstVisibleCol + i);
            parts.Add($"N(LEN(TRIM({colLetter}{excelRow}&\"\"))=0)");
        }

        if (parts.Count == 0)
            return "0";

        return string.Join("+", parts);
    }

    private static void AddNewSinceSelectedDateWorksheet(XLWorkbook workbook)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(NewSinceSelectedDateSheetName);

        List<string> headers = new() { "FirstSeenDate" };
        headers.AddRange(CleanOutputColumns);

        AddSimpleHeaderRow(ws, headers);

        string lastCleanColumn = CleanOutputColumns.Last();
        string formula =
            $"FILTER(tblCurrentData[[FirstSeenDate]:[{lastCleanColumn}]],(tblCurrentData[RowKey]<>\"\")*(tblCurrentData[FirstSeenDate]>={CompareDateCellReference}),\"No new exhibitors since selected date.\")";

        ws.Cell(2, 1).FormulaA1 = formula;
        ws.Column(1).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.SheetView.FreezeRows(1);
        ApplyDynamicListColumnWidths(ws, headers);
    }

    private static void AddChangedSinceSelectedDateWorksheet(XLWorkbook workbook)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(ChangedSinceSelectedDateSheetName);

        List<string> headers = new()
        {
            "ChangeDate",
            "RunDate",
            "EventName",
            "ExhibitorID",
            "AccountCode",
            "CompanyName",
            "FieldDisplayName",
            "OldValue",
            "NewValue"
        };

        AddSimpleHeaderRow(ws, headers);

        string formula =
            $"FILTER(tblChangeHistory[[ChangeDate]:[NewValue]],(tblChangeHistory[RowKey]<>\"\")*(tblChangeHistory[ChangeDate]>={CompareDateCellReference}),\"No changed fields since selected date.\")";

        ws.Cell(2, 1).FormulaA1 = formula;
        ws.Column(1).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Column(2).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
        ws.SheetView.FreezeRows(1);
        ApplyChangedListColumnWidths(ws);
    }

    private static void AddDataQualityWorksheet(
        XLWorkbook workbook,
        List<Dictionary<string, string>> currentRows)
    {
        List<Dictionary<string, string>> rows = BuildDataQualityRows(currentRows);

        AddWorksheetFromRows(
            workbook,
            DataQualitySheetName,
            rows,
            new List<string>
            {
                "CompanyName",
                "BoothNumber",
                "MissingField",
                "MainContactEmail",
                "ExhibitorID",
                "AccountCode",
                "Notes"
            }
        );

        IXLWorksheet ws = workbook.Worksheet(DataQualitySheetName);
        ws.Column(1).Width = 30;
        ws.Column(2).Width = 14;
        ws.Column(3).Width = 26;
        ws.Column(4).Width = 30;
        ws.Column(5).Width = 14;
        ws.Column(6).Width = 14;
        ws.Column(7).Width = 40;
    }

    private static List<Dictionary<string, string>> BuildDataQualityRows(List<Dictionary<string, string>> currentRows)
    {
        List<Dictionary<string, string>> rows = new();

        foreach (Dictionary<string, string> currentRow in currentRows)
        {
            foreach (string column in MissingDataWatchColumns)
            {
                if (!CleanOutputColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                    continue;

                string value = GetDictionaryValue(currentRow, column);

                if (!string.IsNullOrWhiteSpace(value))
                    continue;

                rows.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CompanyName"] = GetDictionaryValue(currentRow, "CompanyName"),
                    ["BoothNumber"] = GetDictionaryValue(currentRow, "BoothNumber"),
                    ["MissingField"] = GetDisplayColumnName(column),
                    ["MissingFieldInternal"] = column,
                    ["MainContactEmail"] = GetDictionaryValue(currentRow, "MainContactEmail"),
                    ["ExhibitorID"] = GetDictionaryValue(currentRow, "ExhibitorID"),
                    ["AccountCode"] = GetDictionaryValue(currentRow, "AccountCode"),
                    ["Notes"] = "Required field is blank on the current registration list."
                });
            }
        }

        return rows
            .OrderBy(r => GetDictionaryValue(r, "CompanyName"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => GetDictionaryValue(r, "MissingField"), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddSimpleHeaderRow(IXLWorksheet ws, List<string> headers)
    {
        XLColor navy = XLColor.FromArgb(31, 78, 121);

        for (int c = 0; c < headers.Count; c++)
        {
            ws.Cell(1, c + 1).Value = GetDisplayColumnName(headers[c]);
            ws.Cell(1, c + 1).Style.Font.Bold = true;
            ws.Cell(1, c + 1).Style.Font.FontColor = XLColor.White;
            ws.Cell(1, c + 1).Style.Fill.BackgroundColor = navy;
        }
    }

    private static void ApplyDynamicListColumnWidths(IXLWorksheet ws, List<string> headers)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            string header = headers[i];
            double width = header switch
            {
                "FirstSeenDate" => 14,
                "CompanyName" => 28,
                "CompanyBannerName" => 28,
                "ExhibitorCategory" => 24,
                "ExhibitorWebsite" => 28,
                _ when header.EndsWith("Email", StringComparison.OrdinalIgnoreCase) => 28,
                _ when header.EndsWith("Title", StringComparison.OrdinalIgnoreCase) => 22,
                _ when header.Contains("Address", StringComparison.OrdinalIgnoreCase) => 28,
                _ => 16
            };

            ws.Column(i + 1).Width = width;
        }
    }

    private static void ApplyChangedListColumnWidths(IXLWorksheet ws)
    {
        ws.Column(1).Width = 14;
        ws.Column(2).Width = 21;
        ws.Column(3).Width = 28;
        ws.Column(4).Width = 14;
        ws.Column(5).Width = 14;
        ws.Column(6).Width = 30;
        ws.Column(7).Width = 24;
        ws.Column(8).Width = 35;
        ws.Column(9).Width = 35;
    }

    private static void AddRunInfoWorksheet(
        XLWorkbook workbook,
        EventInfo eventInfo,
        List<Dictionary<string, string>> currentRows,
        List<Dictionary<string, string>> exhibitorHistoryRows,
        List<Dictionary<string, string>> changeHistoryRows,
        ComparisonResult sinceLastRun,
        ComparisonResult sinceDailyBaseline,
        string eventsPath,
        string exhibitorsPath,
        string accountsPath,
        string exhibitorCategoriesPath,
        string latestSnapshotPath,
        string dailyBaselineSnapshotPath,
        string exhibitorHistoryPath,
        string changeHistoryPath)
    {
        List<Dictionary<string, string>> runInfoRows = new()
        {
            new Dictionary<string, string> { ["Field"] = "RunDate", ["Value"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
            new Dictionary<string, string> { ["Field"] = "EventID", ["Value"] = eventInfo.EventId },
            new Dictionary<string, string> { ["Field"] = "EventName", ["Value"] = eventInfo.EventName },
            new Dictionary<string, string> { ["Field"] = "EventStartDate", ["Value"] = eventInfo.StartDate.ToString("yyyy-MM-dd") },
            new Dictionary<string, string> { ["Field"] = "CurrentRows", ["Value"] = currentRows.Count.ToString("N0") },
            new Dictionary<string, string> { ["Field"] = "NewSinceLastRun", ["Value"] = sinceLastRun.NewRows.Count.ToString("N0") },
            new Dictionary<string, string> { ["Field"] = "ChangedFieldsSinceLastRun", ["Value"] = sinceLastRun.ChangedRows.Count.ToString("N0") },
            new Dictionary<string, string> { ["Field"] = "NewToday", ["Value"] = sinceDailyBaseline.NewRows.Count.ToString("N0") },
            new Dictionary<string, string> { ["Field"] = "ChangedFieldsToday", ["Value"] = sinceDailyBaseline.ChangedRows.Count.ToString("N0") },
            new Dictionary<string, string> { ["Field"] = "ExhibitorHistoryRows", ["Value"] = exhibitorHistoryRows.Count.ToString("N0") },
            new Dictionary<string, string> { ["Field"] = "ChangeHistoryRows", ["Value"] = changeHistoryRows.Count.ToString("N0") },
            new Dictionary<string, string> { ["Field"] = "EventsSourceFile", ["Value"] = Path.GetFileName(eventsPath) },
            new Dictionary<string, string> { ["Field"] = "ExhibitorsSourceFile", ["Value"] = Path.GetFileName(exhibitorsPath) },
            new Dictionary<string, string> { ["Field"] = "AccountsSourceFile", ["Value"] = Path.GetFileName(accountsPath) },
            new Dictionary<string, string> { ["Field"] = "ExhibitorCategoriesSourceFile", ["Value"] = Path.GetFileName(exhibitorCategoriesPath) },
            new Dictionary<string, string> { ["Field"] = "LatestSnapshotFile", ["Value"] = Path.GetFileName(latestSnapshotPath) },
            new Dictionary<string, string> { ["Field"] = "DailyBaselineSnapshotFile", ["Value"] = Path.GetFileName(dailyBaselineSnapshotPath) },
            new Dictionary<string, string> { ["Field"] = "ExhibitorHistoryFile", ["Value"] = Path.GetFileName(exhibitorHistoryPath) },
            new Dictionary<string, string> { ["Field"] = "ChangeHistoryFile", ["Value"] = Path.GetFileName(changeHistoryPath) },
            new Dictionary<string, string> { ["Field"] = "UniqueKey", ["Value"] = "EventID + ExhibitorID" },
            new Dictionary<string, string> { ["Field"] = "ActiveExhibitorStatuses", ["Value"] = "2, 10, 22" },
            new Dictionary<string, string> { ["Field"] = "WorkbookDesign", ["Value"] = "Macro-free .xlsx with hidden source tables, dynamic FILTER formulas, and lightweight conditional formatting" },
            new Dictionary<string, string> { ["Field"] = "ComparisonDateCell", ["Value"] = $"{DashboardSheetName}!B{DashboardCompareDateRow}" },
            new Dictionary<string, string> { ["Field"] = "NewRule", ["Value"] = "FirstSeenDate >= Show Activity Since" },
            new Dictionary<string, string> { ["Field"] = "ChangeRule", ["Value"] = "ChangeDate >= Show Activity Since. ChangeDate is the later of RunDate and ChangeDetectedOn." },
            new Dictionary<string, string> { ["Field"] = "ExternalHistory", ["Value"] = "Full history remains in Registration Lists\\_History" }
        };

        AddWorksheetFromRows(workbook, RunInfoSheetName, runInfoRows, new List<string> { "Field", "Value" });
    }

    private static IXLWorksheet AddWorksheetTable(
        XLWorkbook workbook,
        string worksheetName,
        string tableName,
        List<Dictionary<string, string>> rows,
        List<string> preferredHeaders,
        bool hideSheet)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(worksheetName);

        List<string> headers = preferredHeaders
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int c = 0; c < headers.Count; c++)
        {
            ws.Cell(1, c + 1).Value = headers[c];
            ws.Cell(1, c + 1).Style.Font.Bold = true;
            ws.Cell(1, c + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        int dataRowCount = Math.Max(rows.Count, 1);

        for (int r = 0; r < dataRowCount; r++)
        {
            Dictionary<string, string>? row = r < rows.Count ? rows[r] : null;

            for (int c = 0; c < headers.Count; c++)
            {
                string header = headers[c];
                string value = row == null ? "" : GetDictionaryValue(row, header);
                SetWorksheetCellValue(ws.Cell(r + 2, c + 1), header, value);
            }
        }

        IXLRange range = ws.Range(1, 1, dataRowCount + 1, headers.Count);
        range.CreateTable(tableName);

        ws.SheetView.FreezeRows(1);

        if (hideSheet)
            ws.Hide();

        return ws;
    }

    private static void AddChangeHistoryQualificationFormulas(IXLWorksheet changeHistorySheet, int actualRowCount)
    {
        int formulaColumn = ChangeHistoryWorkbookColumns.IndexOf("QualifiesForSelectedDate") + 1;
        int dataRowCount = Math.Max(actualRowCount, 1);

        for (int r = 0; r < dataRowCount; r++)
        {
            int excelRow = r + 2;
            changeHistorySheet.Cell(excelRow, formulaColumn).FormulaA1 =
                $"AND([@RowKey]<>\"\",OR([@RunDateDate]>={CompareDateCellReference},[@ChangeDetectedOnDate]>={CompareDateCellReference}))";
        }
    }

    private static List<Dictionary<string, string>> BuildFieldMapRows()
    {
        List<Dictionary<string, string>> rows = new();

        for (int i = 0; i < CleanOutputColumns.Count; i++)
        {
            string internalName = CleanOutputColumns[i];

            rows.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["InternalFieldName"] = internalName,
                ["DisplayName"] = GetDisplayColumnName(internalName),
                ["OutputOrder"] = (i + 1).ToString(CultureInfo.InvariantCulture),
                ["VisibleInCurrentList"] = "TRUE",
                ["Required"] = MissingDataWatchColumns.Contains(internalName) ? "TRUE" : "FALSE",
                ["HighlightChanged"] = "TRUE"
            });
        }

        return rows;
    }

    private static void SetWorksheetCellValue(IXLCell cell, string header, string value)
    {
        if (IsDateColumn(header) && TryParseDate(value, out DateTime date))
        {
            cell.Value = ShouldPreserveTime(header) ? date : date.Date;
            cell.Style.DateFormat.Format = ShouldPreserveTime(header) ? "yyyy-mm-dd hh:mm:ss" : "yyyy-mm-dd";
            return;
        }

        cell.Value = value ?? "";
    }

    private static bool IsDateColumn(string header)
    {
        return header.Equals("FirstSeenDate", StringComparison.OrdinalIgnoreCase)
            || header.Equals("LastSeenDate", StringComparison.OrdinalIgnoreCase)
            || header.Equals("RunDate", StringComparison.OrdinalIgnoreCase)
            || header.Equals("RunDateDate", StringComparison.OrdinalIgnoreCase)
            || header.Equals("ChangeDate", StringComparison.OrdinalIgnoreCase)
            || header.Equals("ChangeDetectedOn", StringComparison.OrdinalIgnoreCase)
            || header.Equals("ChangeDetectedOnDate", StringComparison.OrdinalIgnoreCase)
            || header.Equals("EventStartDate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldPreserveTime(string header)
    {
        return header.Equals("RunDate", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeExcelString(string value)
    {
        return (value ?? "").Replace("\"", "\"\"");
    }

    // ============================================================
    // BASIC WORKBOOK TABLE HELPERS
    // ============================================================

    private static void AddWorksheetFromRows(
        XLWorkbook workbook,
        string worksheetName,
        List<Dictionary<string, string>> rows,
        List<string> preferredHeaders)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(worksheetName);

        List<string> headers;

        if (preferredHeaders.Count > 0)
        {
            headers = preferredHeaders.ToList();
        }
        else
        {
            headers = rows
                .SelectMany(r => r.Keys)
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (headers.Count == 0)
        {
            ws.Cell(1, 1).Value = "No records found.";
            ws.Cell(1, 1).Style.Font.Bold = true;
            return;
        }

        for (int c = 0; c < headers.Count; c++)
        {
            string internalHeader = headers[c];
            string displayHeader = GetDisplayColumnName(internalHeader);

            ws.Cell(1, c + 1).Value = displayHeader;
            ws.Cell(1, c + 1).Style.Font.Bold = true;
            ws.Cell(1, c + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        if (rows.Count == 0)
        {
            ws.Cell(2, 1).Value = "No records found.";
            return;
        }

        for (int r = 0; r < rows.Count; r++)
        {
            Dictionary<string, string> row = rows[r];

            for (int c = 0; c < headers.Count; c++)
            {
                string internalHeader = headers[c];
                string value = GetDictionaryValue(row, internalHeader);
                SetWorksheetCellValue(ws.Cell(r + 2, c + 1), internalHeader, value);
            }
        }

        IXLRange range = ws.Range(1, 1, rows.Count + 1, headers.Count);
        range.CreateTable();

        ws.SheetView.FreezeRows(1);
        ApplyGenericColumnWidths(ws, headers);
    }

    private static void ApplyGenericColumnWidths(IXLWorksheet ws, List<string> headers)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            string header = headers[i];
            double width = header switch
            {
                "Field" => 28,
                "Value" => 60,
                "CompanyName" => 30,
                "EventName" => 30,
                "ErrorMessage" => 60,
                "StackTrace" => 80,
                "Notes" => 40,
                "OldValue" => 35,
                "NewValue" => 35,
                _ when header.EndsWith("Email", StringComparison.OrdinalIgnoreCase) => 30,
                _ => 18
            };

            ws.Column(i + 1).Width = width;
        }
    }

    private static void CreateErrorWorkbook(
        string outputPath,
        List<Dictionary<string, string>> errorRows)
    {
        using XLWorkbook workbook = new();

        AddWorksheetFromRows(
            workbook,
            "Automation Errors",
            errorRows,
            new List<string>
            {
                "RunDate",
                "EventNumber",
                "EventID",
                "EventName",
                "EventStartDate",
                "ErrorType",
                "ErrorMessage",
                "StackTrace"
            }
        );

        SaveWorkbookWithRetry(workbook, outputPath);
    }

    // ============================================================
    // CSV READING / WRITING
    // ============================================================

    private static List<Dictionary<string, string>> ReadCsvAsRows(string path)
    {
        CsvConfiguration config = new(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            BadDataFound = null,
            MissingFieldFound = null,
            HeaderValidated = null,
            TrimOptions = TrimOptions.Trim,
            DetectDelimiter = true
        };

        using FileStream fileStream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );

        using StreamReader reader = new(fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using CsvReader csv = new(reader, config);

        if (!csv.Read())
        {
            return new List<Dictionary<string, string>>();
        }

        try
        {
            csv.ReadHeader();
        }
        catch (CsvHelper.ReaderException)
        {
            return new List<Dictionary<string, string>>();
        }

        string[] headers = csv.HeaderRecord ?? Array.Empty<string>();

        if (headers.Length == 0)
        {
            return new List<Dictionary<string, string>>();
        }

        Dictionary<int, string> indexToHeader = new();

        for (int i = 0; i < headers.Length; i++)
        {
            string cleanHeader = CleanHeader(headers[i]);

            if (string.IsNullOrWhiteSpace(cleanHeader))
                cleanHeader = $"Column{IndexToColumnLetter(i + 1)}";

            if (indexToHeader.Values.Contains(cleanHeader, StringComparer.OrdinalIgnoreCase))
                cleanHeader = $"{cleanHeader}_{i + 1}";

            indexToHeader[i] = cleanHeader;
        }

        List<Dictionary<string, string>> rows = new();

        while (csv.Read())
        {
            Dictionary<string, string> row = new(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < headers.Length; i++)
            {
                string header = indexToHeader[i];
                string value = csv.GetField(i) ?? "";

                row[header] = value.Trim();

                string letterHeader = IndexToColumnLetter(i + 1);
                row[$"Column{letterHeader}"] = value.Trim();
            }

            rows.Add(row);
        }

        return rows;
    }

    private static void WriteCsv(
        string path,
        List<Dictionary<string, string>> rows,
        List<string>? preferredHeaders = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        List<string> headers;

        if (preferredHeaders != null && preferredHeaders.Count > 0)
        {
            headers = preferredHeaders
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            headers = rows
                .SelectMany(r => r.Keys)
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Where(h => !IsHelperColumn(h))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (headers.Count == 0)
        {
            headers = GetFullSnapshotHeaders();
        }

        string directory = Path.GetDirectoryName(path)!;
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (StreamWriter writer = new(tempPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            using (CsvWriter csv = new(writer, CultureInfo.InvariantCulture))
            {
                foreach (string header in headers)
                    csv.WriteField(header);

                csv.NextRecord();

                foreach (Dictionary<string, string> row in rows)
                {
                    foreach (string header in headers)
                        csv.WriteField(GetDictionaryValue(row, header));

                    csv.NextRecord();
                }
            }

            ReplaceFileWithRetry(tempPath, path);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }


    // ============================================================
    // EXCEL ACCOUNT READING
    // ============================================================

    private static Dictionary<string, Dictionary<string, string>> ReadAccountsWorkbook(string path)
    {
        Dictionary<string, Dictionary<string, string>> accountsByCode = new(StringComparer.OrdinalIgnoreCase);

        using FileStream fileStream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );

        using XLWorkbook workbook = new(fileStream);
        IXLWorksheet ws = workbook.Worksheets.First();

        IXLRange? usedRange = ws.RangeUsed();

        if (usedRange == null)
            return accountsByCode;

        int firstRow = usedRange.RangeAddress.FirstAddress.RowNumber;
        int lastRow = usedRange.RangeAddress.LastAddress.RowNumber;
        int firstCol = usedRange.RangeAddress.FirstAddress.ColumnNumber;
        int lastCol = usedRange.RangeAddress.LastAddress.ColumnNumber;

        Dictionary<int, string> headersByColumn = new();

        for (int col = firstCol; col <= lastCol; col++)
        {
            string rawHeader = ws.Cell(firstRow, col).GetFormattedString();
            string cleanHeader = CleanHeader(rawHeader);

            if (string.IsNullOrWhiteSpace(cleanHeader))
                cleanHeader = $"Column{IndexToColumnLetter(col)}";

            if (headersByColumn.Values.Contains(cleanHeader, StringComparer.OrdinalIgnoreCase))
                cleanHeader = $"{cleanHeader}_{col}";

            headersByColumn[col] = cleanHeader;
        }

        for (int rowNumber = firstRow + 1; rowNumber <= lastRow; rowNumber++)
        {
            Dictionary<string, string> row = new(StringComparer.OrdinalIgnoreCase);

            for (int col = firstCol; col <= lastCol; col++)
            {
                string header = headersByColumn[col];
                string value = ws.Cell(rowNumber, col).GetFormattedString().Trim();

                row[header] = value;

                string letterHeader = IndexToColumnLetter(col);
                row[$"Column{letterHeader}"] = value;
            }

            string accountCode = NormalizeAccountCode(GetValue(row, new[] { "AccountCode", "Account Code" }, "B"));

            if (string.IsNullOrWhiteSpace(accountCode))
                continue;

            row["AccountCode"] = accountCode;

            if (!accountsByCode.ContainsKey(accountCode))
            {
                accountsByCode.Add(accountCode, row);
            }
        }

        return accountsByCode;
    }

    // ============================================================
    // CATEGORY READING / MAPPING
    // ============================================================

    private static Dictionary<string, string> ReadExhibitorCategories(string path)
    {
        Dictionary<string, string> categories = new(StringComparer.OrdinalIgnoreCase);

        List<Dictionary<string, string>> rows = ReadCsvAsRows(path);

        foreach (Dictionary<string, string> row in rows)
        {
            string categoryName = GetValue(row, new[] { "Description", "Name", "Value", "Category" }, "A").Trim();
            string categoryCode = NormalizeId(GetValue(row, new[] { "Code", "ID", "CategoryID", "Category ID" }, "B"));

            if (string.IsNullOrWhiteSpace(categoryCode))
                continue;

            if (string.IsNullOrWhiteSpace(categoryName))
                categoryName = categoryCode;

            if (!categories.ContainsKey(categoryCode))
                categories.Add(categoryCode, categoryName);
        }

        return categories;
    }

    private static string MapExhibitorCategories(
        string rawCategoryValue,
        Dictionary<string, string> exhibitorCategoriesByCode)
    {
        if (string.IsNullOrWhiteSpace(rawCategoryValue))
            return "";

        string cleaned = rawCategoryValue.Trim();

        string[] parts = Regex
            .Split(cleaned, @"[,;|/ ]+")
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();

        if (parts.Length == 0)
            return cleaned;

        List<string> mappedValues = new();

        foreach (string part in parts)
        {
            string code = NormalizeId(part);

            if (exhibitorCategoriesByCode.TryGetValue(code, out string? categoryName))
                mappedValues.Add(categoryName);
            else
                mappedValues.Add(code);
        }

        return string.Join(", ", mappedValues.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    // ============================================================
    // VALUE HELPERS
    // ============================================================

    private static string GetValue(
        Dictionary<string, string> row,
        string[] possibleHeaders,
        string fallbackColumnLetter)
    {
        foreach (string header in possibleHeaders)
        {
            string cleanHeader = CleanHeader(header);

            if (row.TryGetValue(cleanHeader, out string? value))
                return value ?? "";

            string spaced = header.Trim();

            if (row.TryGetValue(spaced, out value))
                return value ?? "";
        }

        if (!string.IsNullOrWhiteSpace(fallbackColumnLetter))
        {
            string fallbackKey = $"Column{fallbackColumnLetter.ToUpperInvariant()}";

            if (row.TryGetValue(fallbackKey, out string? fallbackValue))
                return fallbackValue ?? "";
        }

        return "";
    }

    private static string GetDictionaryValue(Dictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out string? value) ? value ?? "" : "";
    }

    private static string FirstNonBlank(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private static string CleanHeader(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string cleaned = value.Trim();

        cleaned = cleaned.Replace(" ", "");
        cleaned = cleaned.Replace("_", "");
        cleaned = cleaned.Replace("-", "");
        cleaned = cleaned.Replace(".", "");
        cleaned = cleaned.Replace("/", "");
        cleaned = cleaned.Replace("\\", "");
        cleaned = cleaned.Replace("(", "");
        cleaned = cleaned.Replace(")", "");

        return cleaned;
    }

    private static string GetDisplayColumnName(string internalColumnName)
    {
        if (DisplayColumnNames.TryGetValue(internalColumnName, out string? displayName))
            return displayName;

        return internalColumnName;
    }

    private static string GetBestCompanyName(Dictionary<string, string>? account)
    {
        if (account == null)
            return "";

        return FirstNonBlank(
            GetValue(account, new[] { "Company" }, "W"),
            GetValue(account, new[] { "LegalName", "Legal Name" }, "AY"),
            GetValue(account, new[] { "AccountName", "Account Name", "Name" }, "")
        );
    }

    private static string FormatExhibitorStatus(string rawStatus)
    {
        string status = NormalizeId(rawStatus);

        return status switch
        {
            "2" => "Active",
            "22" => "Active Paid in Full",
            _ => status
        };
    }

    private static string FormatExhibitorType(string rawType)
    {
        string type = (rawType ?? "").Trim().ToUpperInvariant();

        return type switch
        {
            "ME" => "Main Exhibitor",
            "CO" => "CO-Exhibitor",
            _ => rawType?.Trim() ?? ""
        };
    }

    private static List<string> GetFullSnapshotHeaders()
    {
        return new List<string>
        {
            "RowKey",
            "EventID",
            "EventName",
            "EventStartDate",

            "ExhibitorID",
            "ExhibitorEventID",
            "AccountCode",
            "ExhibitorStatus",

            "CompanyName",
            "CompanyAddress1",
            "CompanyCity",
            "CompanyState",
            "CompanyPostalCode",

            "ExhibitorType",
            "CompanyBannerName",

            "OrderedArea",
            "OrderedLength",
            "OrderedWidth",
            "BoothNumber",
            "ExhibitorCategory",
            "MainExhibitorAccountCode",
            "MainExhibitorCompany",

            "MainContactAccountCode",
            "BoothContactAccountCode",
            "PressContactAccountCode",

            "ExhibitorCompanyName",
            "CatalogAddress",
            "CatalogCity",
            "CatalogState",
            "CatalogPostalCode",
            "CatalogCountry",
            "CatalogCountryName",
            "ExhibitorWebsite",

            "CompanyAccountAccountCode",
            "CompanyAccountAddress1",
            "CompanyAccountChangedOn",
            "CompanyAccountCompany",
            "CompanyAccountCity",
            "CompanyAccountCountry",
            "CompanyAccountEmail",
            "CompanyAccountFirstName",
            "CompanyAccountLastName",
            "CompanyAccountLegalName",
            "CompanyAccountMobile",
            "CompanyAccountPhone",
            "CompanyAccountPostalCode",
            "CompanyAccountPrimaryAccount",
            "CompanyAccountState",
            "CompanyAccountTitle",
            "CompanyAccountWebsite",

            "MainContactAccountCode",
            "MainContactAddress1",
            "MainContactChangedOn",
            "MainContactCompany",
            "MainContactCity",
            "MainContactCountry",
            "MainContactEmail",
            "MainContactFirstName",
            "MainContactLastName",
            "MainContactLegalName",
            "MainContactMobile",
            "MainContactPhone",
            "MainContactPostalCode",
            "MainContactPrimaryAccount",
            "MainContactState",
            "MainContactTitle",
            "MainContactWebsite",

            "BoothContactAccountCode",
            "BoothContactAddress1",
            "BoothContactChangedOn",
            "BoothContactCompany",
            "BoothContactCity",
            "BoothContactCountry",
            "BoothContactEmail",
            "BoothContactFirstName",
            "BoothContactLastName",
            "BoothContactLegalName",
            "BoothContactMobile",
            "BoothContactPhone",
            "BoothContactPostalCode",
            "BoothContactPrimaryAccount",
            "BoothContactState",
            "BoothContactTitle",
            "BoothContactWebsite",

            "PressContactAccountCode",
            "PressContactAddress1",
            "PressContactChangedOn",
            "PressContactCompany",
            "PressContactCity",
            "PressContactCountry",
            "PressContactEmail",
            "PressContactFirstName",
            "PressContactLastName",
            "PressContactLegalName",
            "PressContactMobile",
            "PressContactPhone",
            "PressContactPostalCode",
            "PressContactPrimaryAccount",
            "PressContactState",
            "PressContactTitle",
            "PressContactWebsite"
        }
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    // ============================================================
    // NORMALIZATION
    // ============================================================

    private static string NormalizeForCompare(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string cleaned = value.Trim();

        if (TryParseDate(cleaned, out DateTime date))
            return date.ToString("yyyy-MM-dd");

        cleaned = Regex.Replace(cleaned, @"\s+", " ");

        return cleaned.ToUpperInvariant();
    }

    private static string NormalizeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string cleaned = value.Trim();

        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal numeric))
        {
            if (numeric == Math.Truncate(numeric))
                return numeric.ToString("0", CultureInfo.InvariantCulture);
        }

        return cleaned;
    }

    private static string NormalizeAccountCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string cleaned = value.Trim();

        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal numeric))
        {
            if (numeric == Math.Truncate(numeric))
                cleaned = numeric.ToString("0", CultureInfo.InvariantCulture);
        }

        if (Regex.IsMatch(cleaned, @"^\d+$") && cleaned.Length < 8)
        {
            cleaned = cleaned.PadLeft(8, '0');
        }

        return cleaned;
    }

    private static bool StringEqualsNormalized(string a, string b)
    {
        return NormalizeForCompare(a) == NormalizeForCompare(b);
    }

    private static string FormatPossibleDate(string value)
    {
        if (TryParseDate(value, out DateTime date))
            return date.ToString("yyyy-MM-dd");

        return value;
    }

    private static bool TryParseDate(string value, out DateTime date)
    {
        date = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        string cleaned = value.Trim();

        string[] formats =
        {
            "M/d/yyyy",
            "MM/dd/yyyy",
            "M/d/yy",
            "MM/dd/yy",
            "yyyy-MM-dd",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd H:mm:ss",
            "M/d/yyyy h:mm:ss tt",
            "M/d/yyyy h:mm tt",
            "MM/dd/yyyy h:mm:ss tt",
            "MM/dd/yyyy h:mm tt"
        };

        if (DateTime.TryParseExact(
                cleaned,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out date))
        {
            return true;
        }

        return DateTime.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out date);
    }

    private static DateTime ParseDateOrMin(string value)
    {
        return TryParseDate(value, out DateTime date) ? date : DateTime.MinValue;
    }

    private static DateTime MaxDate(DateTime a, DateTime b)
    {
        if (a == DateTime.MinValue)
            return b;

        if (b == DateTime.MinValue)
            return a;

        return a >= b ? a : b;
    }

    // ============================================================
    // UTILITY
    // ============================================================

    private static void SaveWorkbookWithRetry(XLWorkbook workbook, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        string directory = Path.GetDirectoryName(outputPath)!;
        string tempPath = Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.xlsx"
        );

        try
        {
            workbook.SaveAs(tempPath);
            ReplaceFileWithRetry(tempPath, outputPath);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static FileStream AcquireRunLock(string outputRoot)
    {
        string lockPath = Path.Combine(outputRoot, GlobalRunLockFileName);

        try
        {
            FileStream stream = new(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None
            );

            byte[] content = Encoding.UTF8.GetBytes(
                $"Locked by process {Environment.ProcessId} on {Environment.MachineName} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n"
            );

            stream.SetLength(0);
            stream.Write(content, 0, content.Length);
            stream.Flush(true);
            return stream;
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"Another Registration List Automation run appears to be active. Lock file: {lockPath}",
                ex
            );
        }
    }

    private static string GetSnapshotVersion(string snapshotPath)
    {
        if (!File.Exists(snapshotPath))
            return "NO_PRIOR_SNAPSHOT";

        FileInfo info = new(snapshotPath);
        return $"{info.Length}:{info.LastWriteTimeUtc:O}";
    }

    private static void ReplaceFileWithRetry(string tempPath, string destinationPath)
    {
        const int maxAttempts = 10;
        const int delayMilliseconds = 1000;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Move(tempPath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Console.WriteLine($"Destination file busy: {Path.GetFileName(destinationPath)}. Attempt {attempt} of {maxAttempts}.");
                Thread.Sleep(delayMilliseconds);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Console.WriteLine($"Destination file not writable: {Path.GetFileName(destinationPath)}. Attempt {attempt} of {maxAttempts}.");
                Thread.Sleep(delayMilliseconds);
            }
        }

        // Last attempt with original exception surfaced to the event-level error handler.
        File.Move(tempPath, destinationPath, overwrite: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static void ValidateFileExists(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required file not found: {path}");
    }

    private static string CopyFileForReading(string sourcePath, string tempRunFolder)
    {
        string destinationPath = Path.Combine(tempRunFolder, Path.GetFileName(sourcePath));

        const int maxAttempts = 10;
        const int delayMilliseconds = 3000;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                FileInfo before = new(sourcePath);
                long sourceLengthBefore = before.Length;
                DateTime sourceLastWriteBefore = before.LastWriteTimeUtc;

                using (FileStream sourceStream = new(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (FileStream destinationStream = new(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    sourceStream.CopyTo(destinationStream);
                }

                FileInfo after = new(sourcePath);
                FileInfo copied = new(destinationPath);

                bool stable = sourceLengthBefore == after.Length
                    && sourceLastWriteBefore == after.LastWriteTimeUtc
                    && copied.Length == sourceLengthBefore;

                if (!stable)
                {
                    throw new IOException($"Source file changed while being copied: {sourcePath}");
                }

                Console.WriteLine($"Copied stable source file for reading: {Path.GetFileName(sourcePath)}");

                return destinationPath;
            }
            catch (IOException ex)
            {
                Console.WriteLine($"File busy or changing: {Path.GetFileName(sourcePath)}. Attempt {attempt} of {maxAttempts}.");

                if (attempt == maxAttempts)
                {
                    throw new IOException(
                        $"Could not copy a stable file after {maxAttempts} attempts. File may be locked or actively changing: {sourcePath}",
                        ex
                    );
                }

                Thread.Sleep(delayMilliseconds);
            }
        }

        throw new IOException($"Could not copy file: {sourcePath}");
    }


    private static string MakeSafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unnamed";

        string safe = value.Trim();

        foreach (char c in Path.GetInvalidFileNameChars())
            safe = safe.Replace(c, '_');

        safe = Regex.Replace(safe, @"\s+", " ");
        safe = safe.Trim();

        if (safe.Length > 120)
            safe = safe[..120].Trim();

        return safe;
    }

    private static string IndexToColumnLetter(int columnNumber)
    {
        string columnName = "";

        while (columnNumber > 0)
        {
            int modulo = (columnNumber - 1) % 26;
            columnName = Convert.ToChar('A' + modulo) + columnName;
            columnNumber = (columnNumber - modulo) / 26;
        }

        return columnName;
    }
}

// ============================================================
// MODELS
// ============================================================

class EventInfo
{
    public string EventId { get; set; } = "";
    public string EventName { get; set; } = "";
    public DateTime StartDate { get; set; }
}

class ComparisonResult
{
    public List<Dictionary<string, string>> NewRows { get; set; } = new();
    public List<Dictionary<string, string>> ChangedRows { get; set; } = new();
}

class PullRunInfo
{
    public DateTime PullDateTime { get; set; }
    public int RowCount { get; set; }
}
