using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using ClosedXML.Excel;
using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using SearchOptions = Ungerboeck.Api.Models.Options.Search;

class Program
{
    // ====== MOMENTUS / SDK CONFIG ======
    private const string UngerboeckUri = "https://kallman.ungerboeck.com/prod";
    private const string OrgCode = "10";

    private static readonly string ApiUserId =
        Environment.GetEnvironmentVariable("MOMENTUS_APIUSER") ?? "";

    private static readonly string Secret =
        Environment.GetEnvironmentVariable("MOMENTUS_SECRET") ?? "";

    private static readonly string Key =
        Environment.GetEnvironmentVariable("MOMENTUS_KEY") ?? "";

    private const string OutputFolder =
        @"C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents";

    private static readonly string OutputPath =
        Path.Combine(OutputFolder, "Accounts_Pull.xlsx");

    private const int BucketSize = 10000;
    private const int PageSize = 10000;
    private const int MaxResults = 10000;

    private const int AccountCodeWidth = 8;
    private const long StartingAccountCodeNumber = 1;

    private const string DateOnlyFormat = "MM/dd/yyyy";

    static int Main()
    {
        try
        {
            Console.WriteLine("Initializing Momentus API Client...");
            Console.WriteLine("Accounts Pull Version: FULL REBUILD - NO LOWEST-CODE SEARCH");
            Console.WriteLine();

            if (string.IsNullOrWhiteSpace(Secret) || string.IsNullOrWhiteSpace(Key))
            {
                Console.WriteLine("ERROR: API Secret/Key are missing.");
                Console.WriteLine("Set MOMENTUS_SECRET and MOMENTUS_KEY environment variables before running.");
                return 1;
            }

            Directory.CreateDirectory(OutputFolder);

            var client = BuildClient();
            string pullDate = DateTime.Now.ToString(DateOnlyFormat);

            Console.WriteLine("This run will REPLACE the existing Accounts_Pull.xlsx file.");
            Console.WriteLine($"Output file: {OutputPath}");
            Console.WriteLine($"Pull date:   {pullDate}");
            Console.WriteLine();

            long maxAccountCode = FindMaxAccountCodeNumberFromNewestEnteredAccount(client);

            if (maxAccountCode <= 0)
                throw new Exception("Could not determine the max account code from the newest entered account.");

            Console.WriteLine();
            Console.WriteLine("Account-code range selected:");
            Console.WriteLine($"Starting account code: {FormatAccountCode(StartingAccountCodeNumber)}");
            Console.WriteLine($"Max account code:      {FormatAccountCode(maxAccountCode)}");
            Console.WriteLine($"Bucket size:           {BucketSize:N0}");
            Console.WriteLine();

            var allAccounts = PullAccountsByAccountCodeBuckets(
                client,
                StartingAccountCodeNumber,
                maxAccountCode
            );

            Console.WriteLine();
            Console.WriteLine($"Total records pulled before de-dupe: {allAccounts.Count:N0}");

            allAccounts = DeDupeByAccountCode(allAccounts);

            Console.WriteLine($"Total records after de-dupe:        {allAccounts.Count:N0}");
            Console.WriteLine($"Writing replacement Excel file:     {OutputPath}");

            WriteReplacementAccountsExcel(OutputPath, allAccounts, pullDate);

            Console.WriteLine();
            Console.WriteLine("================================");
            Console.WriteLine("PROCESS COMPLETE");
            Console.WriteLine($"Rows written: {allAccounts.Count:N0}");
            Console.WriteLine($"Excel file:   {OutputPath}");
            Console.WriteLine("================================");

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR");
            Console.WriteLine(ex);
            return 1;
        }
    }

    private static long FindMaxAccountCodeNumberFromNewestEnteredAccount(ApiClient client)
    {
        Console.WriteLine("Finding the newest entered account from Momentus...");
        Console.WriteLine("This version does NOT search all accounts at once.");
        Console.WriteLine("It checks EnteredOn one day at a time to avoid the max-results API error.");
        Console.WriteLine();

        DateTime tomorrowUtc = DateTime.UtcNow.Date.AddDays(1);

        // Looks back up to 10 years.
        // If needed, increase this number.
        int daysToLookBack = 3650;

        for (int dayOffset = 0; dayOffset < daysToLookBack; dayOffset++)
        {
            DateTime windowEnd = tomorrowUtc.AddDays(-dayOffset);
            DateTime windowStart = windowEnd.AddDays(-1);

            string startText = windowStart.ToString("yyyy-MM-ddTHH:mm:ss");
            string endText = windowEnd.ToString("yyyy-MM-ddTHH:mm:ss");

            string searchOData =
                $"EnteredOn ge datetime'{startText}' and EnteredOn lt datetime'{endText}'";

            var options = new SearchOptions
            {
                PageSize = PageSize,
                MaxResults = MaxResults
            };

            Console.WriteLine($"Checking EnteredOn window: {windowStart:yyyy-MM-dd} through {windowEnd:yyyy-MM-dd}");

            SearchResponse<AllAccountsModel> response =
                client.Endpoints.Accounts.Search(OrgCode, searchOData, options);

            var rows = response?.Results?.ToList() ?? new List<AllAccountsModel>();

            if (rows.Count == 0)
                continue;

            Console.WriteLine($"Accounts found in this window: {rows.Count:N0}");

            if (rows.Count >= MaxResults)
            {
                Console.WriteLine();
                Console.WriteLine("WARNING:");
                Console.WriteLine($"This EnteredOn day returned {MaxResults:N0} records.");
                Console.WriteLine("The API may have capped the result.");
                Console.WriteLine("If this happens, this day needs to be split into smaller time windows.");
                Console.WriteLine();
            }

            AllAccountsModel newestAccount = rows
                .OrderByDescending(a => ReadDateTimeProperty(a, "EnteredOn"))
                .First();

            string newestAccountCodeText = ReadStringProperty(newestAccount, "AccountCode");
            string newestEnteredOnText = ReadStringProperty(newestAccount, "EnteredOn");

            if (!TryParseAccountCode(newestAccountCodeText, out long newestAccountCode))
            {
                throw new Exception(
                    $"Newest entered account did not have a readable AccountCode. Returned AccountCode: '{newestAccountCodeText}'"
                );
            }

            Console.WriteLine();
            Console.WriteLine("Newest entered account found:");
            Console.WriteLine($"AccountCode: {FormatAccountCode(newestAccountCode)}");
            Console.WriteLine($"EnteredOn:   {newestEnteredOnText}");
            Console.WriteLine();

            return newestAccountCode;
        }

        throw new Exception("Could not find any accounts by EnteredOn in the lookback window.");
    }

    private static List<AllAccountsModel> PullAccountsByAccountCodeBuckets(
        ApiClient client,
        long startAccountCode,
        long maxAccountCode
    )
    {
        var allRows = new List<AllAccountsModel>();

        long bucketStart = startAccountCode;
        int bucketNumber = 0;

        while (bucketStart <= maxAccountCode)
        {
            bucketNumber++;

            long bucketEnd = Math.Min(bucketStart + BucketSize - 1, maxAccountCode);

            string startCode = FormatAccountCode(bucketStart);
            string endCode = FormatAccountCode(bucketEnd);

            string searchOData =
                $"AccountCode ge '{startCode}' and AccountCode le '{endCode}'";

            var options = new SearchOptions
            {
                PageSize = PageSize,
                MaxResults = MaxResults
            };

            Console.WriteLine();
            Console.WriteLine($"Bucket {bucketNumber:N0}");
            Console.WriteLine($"Range: {startCode} through {endCode}");
            Console.WriteLine($"Search OData: {searchOData}");

            SearchResponse<AllAccountsModel> response =
                client.Endpoints.Accounts.Search(OrgCode, searchOData, options);

            var rows = response?.Results?.ToList() ?? new List<AllAccountsModel>();

            Console.WriteLine($"Rows returned: {rows.Count:N0}");

            if (rows.Count >= MaxResults)
            {
                Console.WriteLine();
                Console.WriteLine("WARNING:");
                Console.WriteLine($"Bucket returned {MaxResults:N0} records.");
                Console.WriteLine("This is okay only if the bucket truly has 10,000 or fewer possible account codes.");
                Console.WriteLine("If Momentus errors or caps this, reduce BucketSize from 10000 to 5000.");
                Console.WriteLine();
            }

            allRows.AddRange(rows);

            bucketStart = bucketEnd + 1;
        }

        return allRows;
    }

    private static List<AllAccountsModel> DeDupeByAccountCode(List<AllAccountsModel> accounts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<AllAccountsModel>();

        foreach (var account in accounts)
        {
            string accountCode = ReadStringProperty(account, "AccountCode");

            if (string.IsNullOrWhiteSpace(accountCode))
            {
                deduped.Add(account);
                continue;
            }

            if (seen.Add(accountCode))
                deduped.Add(account);
        }

        return deduped;
    }

    private static void WriteReplacementAccountsExcel(
        string outputPath,
        List<AllAccountsModel> accounts,
        string pullDate
    )
    {
        var headers = BuildHeaders();

        string tempPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? OutputFolder,
            $"Accounts_Pull_TEMP_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        );

        using (var workbook = new XLWorkbook())
        {
            var worksheet = workbook.Worksheets.Add("Accounts");

            WriteHeaderRow(worksheet, headers);

            int rowNumber = 2;
            int written = 0;

            foreach (var account in accounts)
            {
                for (int col = 0; col < headers.Count; col++)
                {
                    string header = headers[col];

                    string value = header == "PullDate"
                        ? pullDate
                        : GetExcelValue(account, header);

                    worksheet.Cell(rowNumber, col + 1).Value = value;
                }

                rowNumber++;
                written++;

                if (written % 1000 == 0)
                    Console.WriteLine($"Prepared {written:N0} records for Excel...");
            }

            worksheet.SheetView.FreezeRows(1);

            // Full AdjustToContents can be slow on very large files.
            // This adjusts based on the header plus first 2,000 rows.
            int lastRowToSize = Math.Min(rowNumber - 1, 2001);

            if (lastRowToSize >= 1)
                worksheet.Columns(1, headers.Count).AdjustToContents(1, lastRowToSize);

            workbook.SaveAs(tempPath);
        }

        if (File.Exists(outputPath))
            File.Delete(outputPath);

        File.Move(tempPath, outputPath);

        Console.WriteLine($"Replacement Excel workbook saved: {outputPath}");
    }

    private static void WriteHeaderRow(IXLWorksheet worksheet, List<string> headers)
    {
        for (int col = 0; col < headers.Count; col++)
        {
            worksheet.Cell(1, col + 1).Value = headers[col];
            worksheet.Cell(1, col + 1).Style.Font.Bold = true;
        }
    }

    private static List<string> BuildHeaders()
    {
        var headers = new List<string>();

        // Column A.
        headers.Add("PullDate");

        /*
            Pull every simple/scalar public property available in your installed
            AllAccountsModel SDK class. This catches fields available in your SDK version.
        */
        var scalarProps = typeof(AllAccountsModel)
            .GetProperties()
            .Where(p => p.CanRead)
            .Where(p => p.Name != "AccountUserFieldSets")
            .Where(p => IsSimpleType(p.PropertyType))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Name)
            .ToList();

        headers.AddRange(scalarProps);

        /*
            Flatten AccountUserFieldSets based on your API help sample.
            If there are multiple user field set rows, this exports the first one.
        */
        headers.Add("AccountUserFieldSets_Header");
        headers.Add("AccountUserFieldSets_Class");
        headers.Add("AccountUserFieldSets_Type");

        for (int i = 1; i <= 30; i++)
            headers.Add($"AccountUserFieldSets_UserNumber{i:00}");

        for (int i = 1; i <= 20; i++)
            headers.Add($"AccountUserFieldSets_UserDateTime{i:00}");

        for (int i = 1; i <= 50; i++)
            headers.Add($"AccountUserFieldSets_UserText{i:00}");

        return headers;
    }

    private static bool IsSimpleType(Type type)
    {
        Type realType = Nullable.GetUnderlyingType(type) ?? type;

        return realType.IsPrimitive ||
               realType.IsEnum ||
               realType == typeof(string) ||
               realType == typeof(decimal) ||
               realType == typeof(DateTime) ||
               realType == typeof(DateTimeOffset) ||
               realType == typeof(Guid);
    }

    private static string GetExcelValue(AllAccountsModel account, string header)
    {
        if (header.StartsWith("AccountUserFieldSets_", StringComparison.OrdinalIgnoreCase))
        {
            string userFieldName = header.Replace("AccountUserFieldSets_", "");
            return GetAccountUserFieldSetValue(account, userFieldName);
        }

        return ReadStringProperty(account, header);
    }

    private static string GetAccountUserFieldSetValue(AllAccountsModel account, string userFieldName)
    {
        try
        {
            PropertyInfo? prop = account.GetType().GetProperty("AccountUserFieldSets");

            if (prop == null)
                return "";

            object? rawValue = prop.GetValue(account);

            if (rawValue == null)
                return "";

            if (rawValue is not IEnumerable enumerable)
                return "";

            object? firstSet = null;

            foreach (object item in enumerable)
            {
                firstSet = item;
                break;
            }

            if (firstSet == null)
                return "";

            return ReadStringProperty(firstSet, userFieldName);
        }
        catch
        {
            return "";
        }
    }

    private static string ReadStringProperty(object obj, string propertyName)
    {
        try
        {
            if (obj == null)
                return "";

            PropertyInfo? prop = obj.GetType().GetProperty(propertyName);

            if (prop == null)
                return "";

            object? value = prop.GetValue(obj);

            if (value == null)
                return "";

            if (value is DateTime dt)
                return dt.ToString(DateOnlyFormat);

            if (value is DateTimeOffset dto)
                return dto.ToString(DateOnlyFormat);

            return value.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static DateTime ReadDateTimeProperty(object obj, string propertyName)
    {
        try
        {
            if (obj == null)
                return DateTime.MinValue;

            PropertyInfo? prop = obj.GetType().GetProperty(propertyName);

            if (prop == null)
                return DateTime.MinValue;

            object? value = prop.GetValue(obj);

            if (value == null)
                return DateTime.MinValue;

            if (value is DateTime dt)
                return dt;

            if (value is DateTimeOffset dto)
                return dto.DateTime;

            if (DateTime.TryParse(value.ToString(), out DateTime parsed))
                return parsed;

            return DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static bool TryParseAccountCode(string accountCode, out long value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(accountCode))
            return false;

        string cleaned = accountCode.Trim();

        return long.TryParse(cleaned, out value);
    }

    private static string FormatAccountCode(long accountCodeNumber)
    {
        return accountCodeNumber.ToString().PadLeft(AccountCodeWidth, '0');
    }

    private static ApiClient BuildClient()
    {
        var auth = new Jwt
        {
            APIUserID = ApiUserId,
            Secret = Secret,
            Key = Key,
            UngerboeckURI = UngerboeckUri,
            AutoRefresh = new AutoRefresh()
        };

        return new ApiClient(auth);
    }
}
