using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

class Program
{
    private const string OrgCode = "10";
    private const string UngerboeckUri = "https://kallman.ungerboeck.com/prod";

    private static readonly string ApiUserId =
        Environment.GetEnvironmentVariable("MOMENTUS_APIUSER")?.Trim() ?? "";
    private static readonly string Secret =
        Environment.GetEnvironmentVariable("MOMENTUS_SECRET")?.Trim() ?? "";
    private static readonly string Key =
        Environment.GetEnvironmentVariable("MOMENTUS_KEY")?.Trim() ?? "";

    private static readonly string OutputFolder =
        Environment.GetEnvironmentVariable("KALLMAN_DATA_WAREHOUSE")?.Trim()
        ?? @"C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents";

    private static readonly string OutputFilePath = Path.Combine(OutputFolder, "Notes_Pull.csv");
    private static readonly string RunStatePath = Path.Combine(OutputFolder, "Notes_Pull.last_run.txt");

    private static readonly DateTime BaseStartDate = DateTime.Today.AddYears(-6);
    private static readonly TimeSpan OverlapWindow = TimeSpan.FromMinutes(10);

    private const int ThrottleEvery = 100;
    private const int ThrottleMs = 500;

    private static readonly List<string> DesiredColumns = new List<string>
    {
        "PullRunOn",
        "OrganizationCode",
        "Type",
        "Code",
        "SequenceNumber",
        "Title",
        "LegacyNotePrintCode",
        "PlainText",
        "Lock",
        "LockDateTime",
        "LockByUserID",
        "Class",
        "UpdateStamp",
        "ChangedBy",
        "EnteredOn",
        "EnteredBy",
        "Event",
        "Function",
        "Account",
        "LegacyContactCode",
        "ResourceMasterSequenceNumber",
        "SetupID",
        "GLAccountCode",
        "Department",
        "SpaceCode",
        "PriceList",
        "ProductServiceCode",
        "IssueSequenceNbr",
        "UserID",
        "Actual",
        "InvoiceNumber",
        "OrderNumber",
        "ARSequenceNbr",
        "OrderDetailNbr",
        "AffiliationCode",
        "BookingHeaderSequence",
        "Contract",
        "BoothSequenceNbr",
        "BoothChartID",
        "BoothInventorySeqNbr",
        "InventoryItemCode",
        "RegistrantSequenceNbr",
        "PriceListItem",
        "SetupDetailSeqNbr",
        "Sensitivity",
        "Text",
        "GLSubAccountCode",
        "GLSource",
        "FiscalYearPeriod",
        "JournalEntryNumber",
        "JournalEntryLineNumber",
        "FiscalYear",
        "FiscalPeriod",
        "Abstract",
        "BankReconciliationSeqNbr",
        "CashBookTransactionSeqNbr",
        "BlockCode",
        "FixedAssetCode",
        "SortSequence",
        "Label",
        "MenuCode",
        "Important",
        "Language",
        "TravelSequence",
        "EventRegistrantType",
        "Quote",
        "QuoteItem",
        "ExhibitorID",
        "EventExhibitorTypeSequence",
        "AppointmentID",
        "AbstractID",
        "TaskID",
        "EventOpportunity",
        "ClassName",
        "EnteredByName"
    };

    static int Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        try
        {
            ValidateCredentials();
            Directory.CreateDirectory(OutputFolder);

            Console.WriteLine("-> Building Momentus client...");
            var client = BuildClient();

            DateTime now = DateTime.Now;
            DateTime effectiveSince = GetEffectiveSince();

            Console.WriteLine($"-> Existing file: {OutputFilePath}");
            Console.WriteLine($"-> Last run checkpoint: {effectiveSince:yyyy-MM-dd HH:mm:ss}");

            var existingRows = LoadExistingRows(OutputFilePath);
            Console.WriteLine($"-> Existing CSV rows loaded: {existingRows.Count:N0}");

            var changedRows = PullChangedNotes(client, effectiveSince);
            Console.WriteLine($"-> Changed/new notes returned: {changedRows.Count:N0}");

            int inserted = 0;
            int updated = 0;

            foreach (var row in changedRows)
            {
                string key = GetRowKey(row);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (existingRows.ContainsKey(key))
                {
                    existingRows[key] = row;
                    updated++;
                }
                else
                {
                    existingRows[key] = row;
                    inserted++;
                }
            }

            var finalRows = existingRows.Values
                .OrderBy(r => GetValue(r, "Type"))
                .ThenBy(r => GetValue(r, "Code"))
                .ThenBy(r => ToInt(GetValue(r, "SequenceNumber")))
                .ToList();

            string tempPath = OutputFilePath + ".tmp";
            WriteCsv(tempPath, finalRows, DesiredColumns);
            File.Move(tempPath, OutputFilePath, true);
            WriteRunState(RunStatePath, now);

            Console.WriteLine($"-> Inserted rows: {inserted:N0}");
            Console.WriteLine($"-> Updated rows: {updated:N0}");
            Console.WriteLine($"-> Final CSV rows: {finalRows.Count:N0}");
            Console.WriteLine($"-> CSV saved: {OutputFilePath}");
            Console.WriteLine($"-> Run state saved: {RunStatePath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL:");
            Console.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(ApiUserId) || string.IsNullOrWhiteSpace(Secret) || string.IsNullOrWhiteSpace(Key))
            throw new InvalidOperationException("Set MOMENTUS_APIUSER, MOMENTUS_SECRET, and MOMENTUS_KEY before running.");
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

    private static DateTime GetEffectiveSince()
    {
        DateTime since = BaseStartDate;

        if (File.Exists(RunStatePath))
        {
            string raw = File.ReadAllText(RunStatePath).Trim();
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime parsed))
                since = parsed;
        }
        else if (File.Exists(OutputFilePath))
        {
            since = File.GetLastWriteTime(OutputFilePath);
        }

        since = since - OverlapWindow;
        if (since < BaseStartDate)
            since = BaseStartDate;

        return since;
    }

    private static Dictionary<string, Dictionary<string, string>> LoadExistingRows(string path)
    {
        var rows = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
            return rows;

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null
        };

        using var reader = new StreamReader(path, Encoding.UTF8);
        using var csv = new CsvReader(reader, config);

        if (!csv.Read() || !csv.ReadHeader())
            return rows;

        string[] headers = csv.HeaderRecord ?? Array.Empty<string>();

        while (csv.Read())
        {
            var row = CreateBlankRow();

            foreach (string header in headers)
            {
                if (!row.ContainsKey(header))
                    row[header] = string.Empty;

                row[header] = csv.GetField(header) ?? string.Empty;
            }

            string key = GetRowKey(row);
            if (!string.IsNullOrWhiteSpace(key))
                rows[key] = row;
        }

        return rows;
    }

    private static List<Dictionary<string, string>> PullChangedNotes(ApiClient client, DateTime since)
    {
        var rows = new List<Dictionary<string, string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        DateTime cursor = since.Date;
        DateTime endDate = DateTime.Today.AddDays(1);
        int processed = 0;

        while (cursor < endDate)
        {
            DateTime next = cursor.AddDays(1);
            string startText = cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string endText = next.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            string odata = $"UpdateStamp ge datetime'{startText}' and UpdateStamp lt datetime'{endText}'";
            Console.WriteLine($"-> OData: {odata}");

            var response = client.Endpoints.Notes.Search(OrgCode, odata);
            var results = response.Results?.ToList() ?? new List<NotesModel>();

            foreach (var item in results)
            {
                processed++;

                DateTime changedOn = GetChangedDate(item);
                if (changedOn != DateTime.MinValue && changedOn <= since)
                    continue;

                var row = CreateBlankRow();
                row["PullRunOn"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                PopulateRowFromNote(item, row);

                string key = GetRowKey(row);
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                    continue;

                rows.Add(row);

                if (processed % ThrottleEvery == 0)
                    System.Threading.Thread.Sleep(ThrottleMs);
            }

            cursor = next;
        }

        return rows;
    }

    private static DateTime GetChangedDate(NotesModel item)
    {
        DateTime changedOn = ToDateTime(GetPropertyPathValue(item, "UpdateStamp"));
        if (changedOn != DateTime.MinValue)
            return changedOn;

        changedOn = ToDateTime(GetPropertyPathValue(item, "EnteredOn"));
        return changedOn;
    }

    private static void PopulateRowFromNote(NotesModel item, Dictionary<string, string> row)
    {
        foreach (var column in DesiredColumns)
        {
            if (column == "PullRunOn")
                continue;

            object? value = GetPropertyPathValue(item, column);
            row[column] = ToStringSafe(value);
        }
    }

    private static object? GetPropertyPathValue(object? source, string propertyPath)
    {
        if (source == null || string.IsNullOrWhiteSpace(propertyPath))
            return null;

        object? current = source;

        foreach (var rawPart in propertyPath.Split('.'))
        {
            if (current == null)
                return null;

            string part = rawPart;
            int? index = null;

            int bracketStart = rawPart.IndexOf('[');
            if (bracketStart >= 0 && rawPart.EndsWith("]"))
            {
                part = rawPart.Substring(0, bracketStart);
                string indexText = rawPart.Substring(bracketStart + 1, rawPart.Length - bracketStart - 2);
                if (int.TryParse(indexText, out int parsedIndex))
                    index = parsedIndex;
            }

            var type = current.GetType();
            var prop = type.GetProperty(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null)
                return null;

            current = prop.GetValue(current);

            if (index.HasValue)
            {
                if (current is System.Collections.IList list)
                {
                    if (index.Value < 0 || index.Value >= list.Count)
                        return null;

                    current = list[index.Value];
                }
                else
                {
                    return null;
                }
            }
        }

        return current;
    }

    private static Dictionary<string, string> CreateBlankRow()
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var col in DesiredColumns)
            row[col] = string.Empty;

        return row;
    }

    private static string GetRowKey(Dictionary<string, string> row)
    {
        return
            $"{GetValue(row, "Type").Trim()}|" +
            $"{GetValue(row, "Code").Trim()}|" +
            $"{GetValue(row, "SequenceNumber").Trim()}";
    }

    private static string GetValue(Dictionary<string, string> row, string columnName)
    {
        return row.TryGetValue(columnName, out string? value) ? value ?? string.Empty : string.Empty;
    }

    private static int ToInt(string value)
    {
        return int.TryParse(value, out int number) ? number : 0;
    }

    private static DateTime ToDateTime(object? value)
    {
        if (value == null)
            return DateTime.MinValue;

        if (value is DateTime dt)
            return dt;

        if (value is DateTimeOffset dto)
            return dto.LocalDateTime;

        return DateTime.TryParse(value.ToString(), out DateTime parsed) ? parsed : DateTime.MinValue;
    }

    private static void WriteRunState(string path, DateTime value)
    {
        File.WriteAllText(path, value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    private static string ToStringSafe(object? value)
    {
        if (value == null)
            return string.Empty;

        return value switch
        {
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static void WriteCsv(string path, List<Dictionary<string, string>> rows, List<string> columns)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            Encoding = new UTF8Encoding(true)
        };

        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        using var csv = new CsvWriter(writer, config);

        foreach (var col in columns)
            csv.WriteField(col);
        csv.NextRecord();

        foreach (var row in rows)
        {
            foreach (var col in columns)
            {
                row.TryGetValue(col, out string? value);
                csv.WriteField(value ?? string.Empty);
            }
            csv.NextRecord();
        }
    }
}
