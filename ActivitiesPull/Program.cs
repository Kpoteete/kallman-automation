using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
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
        Environment.GetEnvironmentVariable("MOMENTUS_APIUSER") ?? "KYLEPAPI";
    private static readonly string Secret =
        Environment.GetEnvironmentVariable("MOMENTUS_SECRET") ?? "8c247eb8-2342-452a-95c3-cf22bd1c6a56";
    private static readonly string Key =
        Environment.GetEnvironmentVariable("MOMENTUS_KEY") ?? "e2b97782-08d7-40f3-bdbc-fbef5095154c";

    private const string OutputFolder =
        @"C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents";

    private static readonly string OutputFilePath = Path.Combine(OutputFolder, "Activities_Pull.csv");

    private static readonly DateTime BaseStartDate = new DateTime(2026, 1, 1);

    private const int ThrottleEvery = 100;
    private const int ThrottleMs = 500;

    private const int MaxRetries = 3;
    private const int RetryDelayMs = 3000;

    private static readonly List<string> DesiredColumns = new List<string>
    {
        "PullRunOn",
        "OrganizationCode",
        "Account",
        "SequenceNumber",
        "Priority",
        "Due",
        "PlainText",
        "Opportunity",
        "Recipient",
        "DueTime",
        "Type",
        "EnteredByCode",
        "Privileged",
        "EnteredOn",
        "ChangedByCode",
        "ChangedOn",
        "Event",
        "Function",
        "Checklist",
        "Contact",
        "EntryDesignation",
        "NotificationFlag",
        "NoteType",
        "NoteCode",
        "NoteSequenceNbr",
        "NotifiedDate",
        "EventOpportunity",
        "Subject",
        "Locked",
        "OrderNumber",
        "EmailSent",
        "EmailSentOn",
        "ProjectID",
        "ActualStartDate",
        "ActualEndDate",
        "ProjectDesignation",
        "EmailSentUserID",
        "Text",
        "ContractSequenceNbr",
        "MultiOrgAccountCode",
        "BlockCode",
        "FiscalYearPeriod",
        "InventoryItemCode",
        "ReminderDate",
        "Status",
        "Quote",
        "ExhibitorID"
    };

    static void Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        try
        {
            Directory.CreateDirectory(OutputFolder);

            Console.WriteLine("-> Building Momentus client...");
            var client = BuildClient();

            if (!File.Exists(OutputFilePath))
            {
                Console.WriteLine("-> Activities_Pull.csv not found. Creating a new empty file with headers.");
                WriteCsv(OutputFilePath, new List<Dictionary<string, string>>(), DesiredColumns);
            }

            Console.WriteLine($"-> Existing file: {OutputFilePath}");

            var existingRows = LoadExistingRows(OutputFilePath);
            Console.WriteLine($"-> Existing CSV rows loaded: {existingRows.Count:N0}");

            DateTime latestPullRunOn = GetLatestPullRunOn(existingRows);
            DateTime startDate = GetEffectiveStartDate(latestPullRunOn);
            DateTime endDate = DateTime.Today.AddDays(1);

            Console.WriteLine($"-> Latest PullRunOn in file: {(latestPullRunOn == DateTime.MinValue ? "(none found)" : latestPullRunOn.ToString("yyyy-MM-dd HH:mm:ss"))}");
            Console.WriteLine($"-> Pull start date: {startDate:yyyy-MM-dd}");
            Console.WriteLine($"-> Pull end date: {DateTime.Today:yyyy-MM-dd}");
            Console.WriteLine("-> Excluding activity type EMS");

            var changedRows = PullActivities(client, startDate, endDate);
            Console.WriteLine($"-> Changed/new activities returned: {changedRows.Count:N0}");

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
                .OrderBy(r => GetValue(r, "Account"))
                .ThenBy(r => ToInt(GetValue(r, "SequenceNumber")))
                .ToList();

            string savedPath = SaveCsvSafely(OutputFilePath, finalRows, DesiredColumns);

            Console.WriteLine($"-> Inserted rows: {inserted:N0}");
            Console.WriteLine($"-> Updated rows: {updated:N0}");
            Console.WriteLine($"-> Final CSV rows: {finalRows.Count:N0}");
            Console.WriteLine($"-> CSV saved: {savedPath}");
            Console.WriteLine(File.Exists(savedPath)
                ? "-> Confirmed CSV file exists."
                : "-> WARNING: CSV file was not found after write.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL:");
            Console.WriteLine(ex.ToString());
        }
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

    private static DateTime GetEffectiveStartDate(DateTime latestPullRunOn)
    {
        if (latestPullRunOn == DateTime.MinValue)
            return BaseStartDate;

        DateTime startDate = latestPullRunOn.Date.AddDays(-1);

        if (startDate < BaseStartDate)
            startDate = BaseStartDate;

        return startDate;
    }

    private static DateTime GetLatestPullRunOn(Dictionary<string, Dictionary<string, string>> rows)
    {
        DateTime latest = DateTime.MinValue;

        foreach (var row in rows.Values)
        {
            string raw = GetValue(row, "PullRunOn");
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime parsed))
            {
                if (parsed > latest)
                    latest = parsed;
            }
        }

        return latest;
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

    private static List<Dictionary<string, string>> PullActivities(ApiClient client, DateTime startDate, DateTime endDate)
    {
        var rows = new List<Dictionary<string, string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int processed = 0;

        DateTime cursor = startDate.Date;

        while (cursor < endDate)
        {
            DateTime next = cursor.AddDays(1);

            string startText = cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string endText = next.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            string filter =
                $"ChangedOn ge datetime'{startText}' and ChangedOn lt datetime'{endText}' and Type ne 'EMS'";

            Console.WriteLine($"-> Filter: {filter}");

            List<ActivitiesModel> results = SearchActivitiesWithRetry(client, filter);

            Console.WriteLine($"-> Returned: {results.Count:N0}");

            foreach (var item in results)
            {
                processed++;

                var row = CreateBlankRow();
                row["PullRunOn"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                PopulateRowFromActivity(item, row);

                string key = GetRowKey(row);
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                    continue;

                rows.Add(row);

                if (processed % ThrottleEvery == 0)
                    Thread.Sleep(ThrottleMs);
            }

            cursor = next;
        }

        return rows;
    }

    private static List<ActivitiesModel> SearchActivitiesWithRetry(ApiClient client, string filter)
    {
        Exception? lastError = null;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                if (attempt > 1)
                    Console.WriteLine($"-> Retry attempt {attempt} of {MaxRetries}...");

                var response = client.Endpoints.Activities.Search(OrgCode, filter);
                return response.Results?.ToList() ?? new List<ActivitiesModel>();
            }
            catch (Exception ex)
            {
                lastError = ex;
                Console.WriteLine($"-> Search failed on attempt {attempt}: {ex.Message}");

                if (attempt < MaxRetries)
                    Thread.Sleep(RetryDelayMs);
            }
        }

        throw new Exception($"Activities search failed after {MaxRetries} attempts for filter: {filter}", lastError);
    }

    private static void PopulateRowFromActivity(ActivitiesModel item, Dictionary<string, string> row)
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
            $"{GetValue(row, "OrganizationCode").Trim()}|" +
            $"{GetValue(row, "Account").Trim()}|" +
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

    private static string ToStringSafe(object? value)
    {
        if (value == null)
            return string.Empty;

        if (value is DateTime dt)
            return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        if (value is DateTimeOffset dto)
            return dto.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

        if (value is byte[] bytes)
            return Convert.ToBase64String(bytes);

        return value.ToString() ?? string.Empty;
    }

    private static string SaveCsvSafely(string targetPath, List<Dictionary<string, string>> rows, List<string> columns)
    {
        string folder = Path.GetDirectoryName(targetPath) ?? OutputFolder;
        Directory.CreateDirectory(folder);

        string tempPath = Path.Combine(folder, $"Activities_Pull_tmp_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        WriteCsv(tempPath, rows, columns);

        try
        {
            if (File.Exists(targetPath))
                File.Delete(targetPath);

            File.Move(tempPath, targetPath);
            return targetPath;
        }
        catch (IOException)
        {
            string fallbackPath = Path.Combine(folder, $"Activities_Pull_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            if (File.Exists(fallbackPath))
                File.Delete(fallbackPath);

            File.Move(tempPath, fallbackPath);

            Console.WriteLine("-> Main CSV file is locked by another process.");
            Console.WriteLine($"-> Saved to fallback file instead: {fallbackPath}");

            return fallbackPath;
        }
    }

    private static void WriteCsv(string path, List<Dictionary<string, string>> rows, List<string> columns)
    {
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true
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

        writer.Flush();
    }
}