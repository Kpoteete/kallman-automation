using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Kallman.Automation.Core.Configuration;
using Kallman.Automation.Core.Files;
using Kallman.Automation.Core.Operations;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

class Program
{
    private const string OrgCode = "10";
    private const string UngerboeckUri = "https://kallman.ungerboeck.com/prod";

    private static readonly string OutputFolder =
        Environment.GetEnvironmentVariable("EXHIBITORS_OUTPUT_FOLDER")
        ?? @"C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents";

    private static readonly string OutputFilePath = Path.Combine(OutputFolder, "Exhibitor_Pull.csv");
    private static readonly string RunStatePath = Path.Combine(OutputFolder, "Exhibitor_Pull.last_run.txt");

    private static readonly DateTime BaseStartDate = DateTime.Today.AddYears(-6);
    private static readonly TimeSpan OverlapWindow = TimeSpan.FromMinutes(10);

    private const int ThrottleEvery = 100;
    private const int ThrottleMs = 500;

    private static readonly List<string> DesiredColumns = new List<string>
    {
        "PullRunOn",
        "OrganizationCode",
        "ExhibitorID",
        "Event",
        "AccountCode",
        "ExhibitorType",
        "ExhibitorStatus",
        "ProjectID",
        "Salesperson",
        "WinProbability",
        "SalesStage",
        "ExpectedCloseDate",
        "LastStatusChange",
        "EnteredOn",
        "EnteredBy",
        "ChangedOn",
        "ChangedBy",
        "ExpectedRevenueAmount",
        "OrderedLength",
        "OrderedWidth",
        "OrderedArea",
        "MoveInDate",
        "CompanyBannerName",
        "Type",
        "PreferredBooth1",
        "Pavilion",
        "Location",
        "PreferredLength",
        "PreferredWidth",
        "PreferredOpenSides",
        "PreferredArea",
        "PreferredBooth2",
        "PreferredBooth3",
        "Comments",
        "Competitors",
        "MainContact",
        "AdditionalContact6",
        "BoothContact",
        "BillToContact",
        "PressContact",
        "IssueClass",
        "IssueType",
        "Points",
        "CatalogAddress1",
        "CatalogAddress2",
        "CatalogAddress3",
        "CatalogAddress4",
        "CatalogAddress5",
        "CatalogAddress6",
        "CatalogCity",
        "CatalogState",
        "CatalogPostalCode",
        "CatalogCountry",
        "CatalogPhone",
        "CatalogFax",
        "CatalogMobile",
        "CatalogDirect",
        "CatalogEmail",
        "CatalogContactName",
        "CompanyName",
        "AlternateName1",
        "AlternateName2",
        "AlternateName3",
        "AlternateName4",
        "AlternateName5",
        "CatalogInfoCompleteFlag",
        "ShellScheme",
        "Hall",
        "Water",
        "Electric",
        "Network",
        "AudioVisual",
        "Phone",
        "Catering",
        "Beverages",
        "OversizedBooth",
        "TwoStoryBooth",
        "Drayage",
        "Sponsorship",
        "Website",
        "ExhibitorApplicationType",
        "BillToAccount",
        "ShipToAccount",
        "ShipToContact",
        "AdditionalContact1",
        "AdditionalContact2",
        "AdditionalContact3",
        "AdditionalContact4",
        "AdditionalContact5",
        "MoveOutDate",
        "PreferredBooth4",
        "PreferredBooth5",
        "LastEventPreferredArea",
        "LastEventPreferredLength",
        "LastEventPreferredWidth",
        "LastEventPreferredOpenSides",
        "LastEventPreferredBooth",
        "UnitofMeasurement",
        "CatalogCompanyDescription",
        "ExpandedCatalogCompanyDescription",
        "CatalogContactName2",
        "CatalogContactName3",
        "CatalogContactTitle1",
        "CatalogContactTitle2",
        "CatalogContactTitle3",
        "Facebook",
        "Twitter",
        "LinkedIn",
        "MainExhibitor",
        "LeadSource",
        "PreferredPavilion",
        "PreferredFloorPlanSection",
        "ContractorAccount",
        "ContractorContact",
        "BoothProposalApprovedinExhibitorPortal",
        "BoothProposalRejectedinExhibitorPortal",
        "PreventexhibitorsfromaccessingExhibitorPortal",
        "LastEventBoothNumber",
        "LastEventBoothLength",
        "LastEventBoothWidth",
        "LastEventBoothOpenSides",
        "LastEventBoothArea",
        "LastEventExhibitorStatus",
        "LastEventComments",
        "CatalogCountryName",
        "OnlineCatalogImage",
        "AllBooths",
        "ExhibitorCategory",
        "PrimaryExhibitor",
        "BoothNumber",
        "ExhibitorUserFields.Header",
        "ExhibitorUserFields.Class",
        "ExhibitorUserFields.Type",
        "ExhibitorUserFields.UserNumber01",
        "ExhibitorUserFields.UserNumber02",
        "ExhibitorUserFields.UserNumber03",
        "ExhibitorUserFields.UserNumber04",
        "ExhibitorUserFields.UserNumber05",
        "ExhibitorUserFields.UserNumber06",
        "ExhibitorUserFields.UserNumber07",
        "ExhibitorUserFields.UserNumber08",
        "ExhibitorUserFields.UserNumber09",
        "ExhibitorUserFields.UserNumber10",
        "ExhibitorUserFields.UserNumber11",
        "ExhibitorUserFields.UserNumber12",
        "ExhibitorUserFields.UserNumber13",
        "ExhibitorUserFields.UserNumber14",
        "ExhibitorUserFields.UserNumber15",
        "ExhibitorUserFields.UserNumber16",
        "ExhibitorUserFields.UserNumber17",
        "ExhibitorUserFields.UserNumber18",
        "ExhibitorUserFields.UserNumber19",
        "ExhibitorUserFields.UserNumber20",
        "ExhibitorUserFields.UserNumber21",
        "ExhibitorUserFields.UserNumber22",
        "ExhibitorUserFields.UserNumber23",
        "ExhibitorUserFields.UserNumber24",
        "ExhibitorUserFields.UserNumber25",
        "ExhibitorUserFields.UserNumber26",
        "ExhibitorUserFields.UserNumber27",
        "ExhibitorUserFields.UserNumber28",
        "ExhibitorUserFields.UserNumber29",
        "ExhibitorUserFields.UserNumber30",
        "ExhibitorUserFields.UserDateTime01",
        "ExhibitorUserFields.UserDateTime02",
        "ExhibitorUserFields.UserDateTime03",
        "ExhibitorUserFields.UserDateTime04",
        "ExhibitorUserFields.UserDateTime05",
        "ExhibitorUserFields.UserDateTime06",
        "ExhibitorUserFields.UserDateTime07",
        "ExhibitorUserFields.UserDateTime08",
        "ExhibitorUserFields.UserDateTime09",
        "ExhibitorUserFields.UserDateTime10",
        "ExhibitorUserFields.UserDateTime11",
        "ExhibitorUserFields.UserDateTime12",
        "ExhibitorUserFields.UserDateTime13",
        "ExhibitorUserFields.UserDateTime14",
        "ExhibitorUserFields.UserDateTime15",
        "ExhibitorUserFields.UserDateTime16",
        "ExhibitorUserFields.UserDateTime17",
        "ExhibitorUserFields.UserDateTime18",
        "ExhibitorUserFields.UserDateTime19",
        "ExhibitorUserFields.UserDateTime20",
        "ExhibitorUserFields.UserText01",
        "ExhibitorUserFields.UserText02",
        "ExhibitorUserFields.UserText03",
        "ExhibitorUserFields.UserText04",
        "ExhibitorUserFields.UserText05",
        "ExhibitorUserFields.UserText06",
        "ExhibitorUserFields.UserText07",
        "ExhibitorUserFields.UserText08",
        "ExhibitorUserFields.UserText09",
        "ExhibitorUserFields.UserText10",
        "ExhibitorUserFields.UserText11",
        "ExhibitorUserFields.UserText12",
        "ExhibitorUserFields.UserText13",
        "ExhibitorUserFields.UserText14",
        "ExhibitorUserFields.UserText15",
        "ExhibitorUserFields.UserText16",
        "ExhibitorUserFields.UserText17",
        "ExhibitorUserFields.UserText18",
        "ExhibitorUserFields.UserText19",
        "ExhibitorUserFields.UserText20",
        "ExhibitorUserFields.UserText21",
        "ExhibitorUserFields.UserText22",
        "ExhibitorUserFields.UserText23",
        "ExhibitorUserFields.UserText24",
        "ExhibitorUserFields.UserText25",
        "ExhibitorUserFields.UserText26",
        "ExhibitorUserFields.UserText27",
        "ExhibitorUserFields.UserText28",
        "ExhibitorUserFields.UserText29",
        "ExhibitorUserFields.UserText30",
        "ExhibitorUserFields.UserText31",
        "ExhibitorUserFields.UserText32",
        "ExhibitorUserFields.UserText33",
        "ExhibitorUserFields.UserText34",
        "ExhibitorUserFields.UserText35",
        "ExhibitorUserFields.UserText36",
        "ExhibitorUserFields.UserText37",
        "ExhibitorUserFields.UserText38",
        "ExhibitorUserFields.UserText39",
        "ExhibitorUserFields.UserText40",
        "ExhibitorUserFields.UserText41",
        "ExhibitorUserFields.UserText42",
        "ExhibitorUserFields.UserText43",
        "ExhibitorUserFields.UserText44",
        "ExhibitorUserFields.UserText45",
        "ExhibitorUserFields.UserText46",
        "ExhibitorUserFields.UserText47",
        "ExhibitorUserFields.UserText48",
        "ExhibitorUserFields.UserText49",
        "ExhibitorUserFields.UserText50"
    };

    static int Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var summary = new AutomationRunSummary { Automation = "ExhibitorsPull" };
        string summaryPath = Path.Combine(OutputFolder, "logs", $"exhibitors-{summary.RunId}.json");

        try
        {
            Directory.CreateDirectory(OutputFolder);

            Console.WriteLine("-> Building Momentus client...");
            var client = BuildClient();

            DateTime now = DateTime.Now;
            DateTime effectiveSince = GetEffectiveSince();

            Console.WriteLine($"-> Existing file: {OutputFilePath}");
            Console.WriteLine($"-> Last run checkpoint: {effectiveSince:yyyy-MM-dd HH:mm:ss}");

            var existingRows = LoadExistingRows(OutputFilePath);
            Console.WriteLine($"-> Existing CSV rows loaded: {existingRows.Count:N0}");

            var changedRows = PullChangedExhibitors(client, effectiveSince);
            summary.RecordsRead = changedRows.Count;
            Console.WriteLine($"-> Changed/new exhibitors returned: {changedRows.Count:N0}");

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
                .OrderBy(r => ToInt(GetValue(r, "ExhibitorID")))
                .ToList();

            string tempOutputPath = AtomicFilePublisher.CreateTemporaryPath(OutputFilePath);
            WriteCsv(tempOutputPath, finalRows, DesiredColumns);
            AtomicFilePublisher.Publish(tempOutputPath, OutputFilePath, OutputFilePath + ".bak");
            WriteRunState(RunStatePath, now);
            summary.RecordsWritten = finalRows.Count;
            summary.CheckpointAdvanced = true;
            summary.Complete("Succeeded");
            summary.WriteJson(summaryPath);

            Console.WriteLine($"-> Inserted rows: {inserted:N0}");
            Console.WriteLine($"-> Updated rows: {updated:N0}");
            Console.WriteLine($"-> Final CSV rows: {finalRows.Count:N0}");
            Console.WriteLine($"-> CSV saved: {OutputFilePath}");
            Console.WriteLine($"-> Run state saved: {RunStatePath}");
            return 0;
        }
        catch (Exception ex)
        {
            summary.Errors++;
            summary.Complete("Failed", ex.Message);
            summary.WriteJson(summaryPath);
            Console.Error.WriteLine("FATAL:");
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static ApiClient BuildClient()
    {
        MomentusCredentials credentials = MomentusCredentials.FromEnvironment();
        var auth = new Jwt
        {
            APIUserID = credentials.ApiUserId,
            Secret = credentials.Secret,
            Key = credentials.Key,
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

    private static List<Dictionary<string, string>> PullChangedExhibitors(ApiClient client, DateTime since)
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
            string odata = $"ChangedOn ge datetime'{startText}' and ChangedOn lt datetime'{endText}'";

            Console.WriteLine($"-> OData: {odata}");

            var response = client.Endpoints.Exhibitors.Search(OrgCode, odata);
            var results = response.Results?.ToList() ?? new List<ExhibitorsModel>();

            foreach (var exhibitor in results)
            {
                processed++;

                DateTime changedOn = ToDateTime(exhibitor.ChangedOn);
                if (changedOn != DateTime.MinValue && changedOn <= since)
                    continue;

                int? exhibitorId = GetIntPropertyValue(exhibitor, "ExhibitorID");
                if (!exhibitorId.HasValue)
                    continue;

                ExhibitorsModel exportExhibitor = GetFullExhibitor(client, exhibitorId.Value);

                var row = CreateBlankRow();
                row["PullRunOn"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                PopulateRowFromExhibitor(exportExhibitor, row);

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

    private static ExhibitorsModel GetFullExhibitor(ApiClient client, int exhibitorId)
    {
        try
        {
            return client.Endpoints.Exhibitors.Get(OrgCode, exhibitorId);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Full exhibitor pull failed for {exhibitorId}; output and checkpoint were not updated.", ex);
        }
    }

    private static void PopulateRowFromExhibitor(ExhibitorsModel exhibitor, Dictionary<string, string> row)
    {
        foreach (var column in DesiredColumns)
        {
            if (column == "PullRunOn")
                continue;

            row[column] = GetColumnValue(exhibitor, column);
        }
    }

    private static string GetColumnValue(ExhibitorsModel exhibitor, string column)
    {
        foreach (string path in GetCandidatePaths(column))
        {
            object? value = GetPropertyPathValue(exhibitor, path);

            if (value == null)
                continue;

            string text = ToStringSafe(value);

            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return string.Empty;
    }

    private static IEnumerable<string> GetCandidatePaths(string column)
    {
        yield return column;

        if (column.Equals("UnitofMeasurement", StringComparison.OrdinalIgnoreCase))
            yield return "UnitOfMeasurement";

        if (column.StartsWith("ExhibitorUserFields.", StringComparison.OrdinalIgnoreCase))
        {
            string fieldName = column.Split('.').Last();

            yield return column.Replace("ExhibitorUserFields.", "ExhibitorUserFields[0].");
            yield return column.Replace("ExhibitorUserFields.", "ExhibitorUserFieldSets.");
            yield return column.Replace("ExhibitorUserFields.", "ExhibitorUserFieldSets[0].");
            yield return column.Replace("ExhibitorUserFields.", "UserFieldSets.");
            yield return column.Replace("ExhibitorUserFields.", "UserFieldSets[0].");
            yield return column.Replace("ExhibitorUserFields.", "UserFields.");
            yield return column.Replace("ExhibitorUserFields.", "UserFields[0].");

            yield return fieldName;
        }
    }

    private static object? GetPropertyPathValue(object? source, string propertyPath)
    {
        if (source == null || string.IsNullOrWhiteSpace(propertyPath))
            return null;

        object? current = source;
        foreach (string rawPart in propertyPath.Split('.'))
        {
            if (current == null)
                return null;

            string part = rawPart;
            int? index = null;

            int bracketStart = rawPart.IndexOf('[');

            if (bracketStart >= 0 && rawPart.EndsWith("]"))
            {
                part = rawPart.Substring(0, bracketStart);

                string indexText = rawPart.Substring(
                    bracketStart + 1,
                    rawPart.Length - bracketStart - 2
                );

                if (int.TryParse(indexText, out int parsedIndex))
                    index = parsedIndex;
            }

            var prop = current
                .GetType()
                .GetProperty(
                    part,
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.IgnoreCase
                );

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

    private static int? GetIntPropertyValue(object source, string propertyName)
    {
        object? value = GetPropertyPathValue(source, propertyName);

        if (value == null)
            return null;

        if (value is int intValue)
            return intValue;

        if (int.TryParse(value.ToString(), out int parsed))
            return parsed;

        return null;
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
        return GetValue(row, "ExhibitorID").Trim();
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
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
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