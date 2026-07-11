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
using Ungerboeck.Api.Models.Search;
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

    private static readonly string OutputFilePath = Path.Combine(OutputFolder, "ServiceOrderItems_Pull.csv");
    private static readonly string RunStatePath = Path.Combine(OutputFolder, "ServiceOrderItems_Pull.last_run.txt");

    private static readonly DateTime BaseStartDate = DateTime.Today.AddYears(-6);
    private static readonly TimeSpan OverlapWindow = TimeSpan.FromMinutes(10);

    private const int ThrottleEvery = 100;
    private const int ThrottleMs = 500;

    private static readonly List<string> DesiredColumns = new List<string>
    {
        "PullRunOn",
        "OrganizationCode",
        "OrderNumber",
        "OrderLineNumber",
        "Event",
        "Function",
        "Setup",
        "ItemDepartment",
        "Note",
        "EnteredOn",
        "EnteredBy",
        "ChangedOn",
        "ChangedBy",
        "Group",
        "OrderItemPhase",
        "ResourceClass",
        "ResourceType",
        "ResourceCode",
        "Units",
        "LocalUnitCharge",
        "UnitCost",
        "ExtendedCost",
        "ExtendedChrg",
        "UM",
        "UT",
        "Billable",
        "Internal",
        "Reference",
        "SerialNbr",
        "PerUnits",
        "StartDate",
        "EndDate",
        "StartTime",
        "EndTime",
        "StatusClass",
        "RoomType",
        "BatchID",
        "Invoice",
        "LeadDate",
        "StrikeDate",
        "ResourceMstrSeqNbr",
        "Multiplier",
        "PriceListDetailSeqNbr",
        "BillToAccountCode",
        "OrderedItemLineNumber",
        "PriceList",
        "BookingSequence",
        "MinimumUnits",
        "MaximumUnits",
        "Profile",
        "OrderCurrencyUnitCharge",
        "OrderCurrencyExtendedCharge",
        "Currency",
        "Description",
        "PrintSequence",
        "SecondaryPrintSequence",
        "UnitCharge",
        "ExtendedCharge",
        "MinimumCharge",
        "MaximumCharge",
        "OrderForm",
        "ManagementReport",
        "DetailUnits",
        "Per",
        "TimeUnits",
        "RevDistributionPercentage",
        "RegistrantSeqNbr",
        "AltDesc",
        "Registrant",
        "Daily",
        "AltDesc2",
        "ContractSeqNbr",
        "OriginalRate",
        "OrderedOnly",
        "ShowStartDate",
        "ShowStartTime",
        "ShowEndDate",
        "ShowEndTime",
        "ShowRate",
        "ShowUnits",
        "LocalBaseRate",
        "MinMaxUnitsUsed",
        "MinMaxChargeUsed",
        "UserText",
        "UserNumber1",
        "UserNumber2",
        "UserNumber3",
        "BaseRate",
        "RevenueDistributionFlag",
        "Covers",
        "RateType",
        "UseSeasonal",
        "TaxInclusive",
        "TaxAmount",
        "OrderCurrencyUnits",
        "LeadHours",
        "StrikeHours",
        "Memo",
        "MemoExtendedCharge",
        "MemoPriceListCurrencyExtendedCharge",
        "MemoOrderCurrencyExtendedCharge",
        "OverrideTimeUnits",
        "SurchargePercent",
        "TaxInclusiveExtendedCharge",
        "OrderCurrencyTaxInclusiveExtendedCharge",
        "ShippingCarrier",
        "Shippable",
        "Chargeable",
        "StartSequence",
        "EndSequence",
        "Supplier",
        "TrackingNumber",
        "ItemStatus",
        "AltDesc3",
        "AltDesc4",
        "AltDesc5",
        "AutoShipRecord",
        "Fixed",
        "EstimateTBD",
        "RollupCalculatedUnitCharge",
        "RollupExtendedCharge",
        "DiscountItem",
        "PrintStamp",
        "ShowExtendedCharge",
        "PrintSequenceNumber",
        "UnitsSchemeCode",
        "BadgeItemPrint",
        "DiscountPercent",
        "ServiceOrderItemUserFieldSets[0].Header",
        "ServiceOrderItemUserFieldSets[0].Class",
        "ServiceOrderItemUserFieldSets[0].Type",
        "ServiceOrderItemUserFieldSets[0].UserNumber01",
        "ServiceOrderItemUserFieldSets[0].UserNumber02",
        "ServiceOrderItemUserFieldSets[0].UserNumber03",
        "ServiceOrderItemUserFieldSets[0].UserNumber04",
        "ServiceOrderItemUserFieldSets[0].UserNumber05",
        "ServiceOrderItemUserFieldSets[0].UserNumber06",
        "ServiceOrderItemUserFieldSets[0].UserNumber07",
        "ServiceOrderItemUserFieldSets[0].UserNumber08",
        "ServiceOrderItemUserFieldSets[0].UserNumber09",
        "ServiceOrderItemUserFieldSets[0].UserNumber10",
        "ServiceOrderItemUserFieldSets[0].UserNumber11",
        "ServiceOrderItemUserFieldSets[0].UserNumber12",
        "ServiceOrderItemUserFieldSets[0].UserNumber13",
        "ServiceOrderItemUserFieldSets[0].UserNumber14",
        "ServiceOrderItemUserFieldSets[0].UserNumber15",
        "ServiceOrderItemUserFieldSets[0].UserNumber16",
        "ServiceOrderItemUserFieldSets[0].UserNumber17",
        "ServiceOrderItemUserFieldSets[0].UserNumber18",
        "ServiceOrderItemUserFieldSets[0].UserNumber19",
        "ServiceOrderItemUserFieldSets[0].UserNumber20",
        "ServiceOrderItemUserFieldSets[0].UserNumber21",
        "ServiceOrderItemUserFieldSets[0].UserNumber22",
        "ServiceOrderItemUserFieldSets[0].UserNumber23",
        "ServiceOrderItemUserFieldSets[0].UserNumber24",
        "ServiceOrderItemUserFieldSets[0].UserNumber25",
        "ServiceOrderItemUserFieldSets[0].UserNumber26",
        "ServiceOrderItemUserFieldSets[0].UserNumber27",
        "ServiceOrderItemUserFieldSets[0].UserNumber28",
        "ServiceOrderItemUserFieldSets[0].UserNumber29",
        "ServiceOrderItemUserFieldSets[0].UserNumber30",
        "ServiceOrderItemUserFieldSets[0].UserDateTime01",
        "ServiceOrderItemUserFieldSets[0].UserDateTime02",
        "ServiceOrderItemUserFieldSets[0].UserDateTime03",
        "ServiceOrderItemUserFieldSets[0].UserDateTime04",
        "ServiceOrderItemUserFieldSets[0].UserDateTime05",
        "ServiceOrderItemUserFieldSets[0].UserDateTime06",
        "ServiceOrderItemUserFieldSets[0].UserDateTime07",
        "ServiceOrderItemUserFieldSets[0].UserDateTime08",
        "ServiceOrderItemUserFieldSets[0].UserDateTime09",
        "ServiceOrderItemUserFieldSets[0].UserDateTime10",
        "ServiceOrderItemUserFieldSets[0].UserDateTime11",
        "ServiceOrderItemUserFieldSets[0].UserDateTime12",
        "ServiceOrderItemUserFieldSets[0].UserDateTime13",
        "ServiceOrderItemUserFieldSets[0].UserDateTime14",
        "ServiceOrderItemUserFieldSets[0].UserDateTime15",
        "ServiceOrderItemUserFieldSets[0].UserDateTime16",
        "ServiceOrderItemUserFieldSets[0].UserDateTime17",
        "ServiceOrderItemUserFieldSets[0].UserDateTime18",
        "ServiceOrderItemUserFieldSets[0].UserDateTime19",
        "ServiceOrderItemUserFieldSets[0].UserDateTime20",
        "ServiceOrderItemUserFieldSets[0].UserText01",
        "ServiceOrderItemUserFieldSets[0].UserText02",
        "ServiceOrderItemUserFieldSets[0].UserText03",
        "ServiceOrderItemUserFieldSets[0].UserText04",
        "ServiceOrderItemUserFieldSets[0].UserText05",
        "ServiceOrderItemUserFieldSets[0].UserText06",
        "ServiceOrderItemUserFieldSets[0].UserText07",
        "ServiceOrderItemUserFieldSets[0].UserText08",
        "ServiceOrderItemUserFieldSets[0].UserText09",
        "ServiceOrderItemUserFieldSets[0].UserText10",
        "ServiceOrderItemUserFieldSets[0].UserText11",
        "ServiceOrderItemUserFieldSets[0].UserText12",
        "ServiceOrderItemUserFieldSets[0].UserText13",
        "ServiceOrderItemUserFieldSets[0].UserText14",
        "ServiceOrderItemUserFieldSets[0].UserText15",
        "ServiceOrderItemUserFieldSets[0].UserText16",
        "ServiceOrderItemUserFieldSets[0].UserText17",
        "ServiceOrderItemUserFieldSets[0].UserText18",
        "ServiceOrderItemUserFieldSets[0].UserText19",
        "ServiceOrderItemUserFieldSets[0].UserText20",
        "ServiceOrderItemUserFieldSets[0].UserText21",
        "ServiceOrderItemUserFieldSets[0].UserText22",
        "ServiceOrderItemUserFieldSets[0].UserText23",
        "ServiceOrderItemUserFieldSets[0].UserText24",
        "ServiceOrderItemUserFieldSets[0].UserText25",
        "ServiceOrderItemUserFieldSets[0].UserText26",
        "ServiceOrderItemUserFieldSets[0].UserText27",
        "ServiceOrderItemUserFieldSets[0].UserText28",
        "ServiceOrderItemUserFieldSets[0].UserText29",
        "ServiceOrderItemUserFieldSets[0].UserText30",
        "ServiceOrderItemUserFieldSets[0].UserText31",
        "ServiceOrderItemUserFieldSets[0].UserText32",
        "ServiceOrderItemUserFieldSets[0].UserText33",
        "ServiceOrderItemUserFieldSets[0].UserText34",
        "ServiceOrderItemUserFieldSets[0].UserText35",
        "ServiceOrderItemUserFieldSets[0].UserText36",
        "ServiceOrderItemUserFieldSets[0].UserText37",
        "ServiceOrderItemUserFieldSets[0].UserText38",
        "ServiceOrderItemUserFieldSets[0].UserText39",
        "ServiceOrderItemUserFieldSets[0].UserText40",
        "ServiceOrderItemUserFieldSets[0].UserText41",
        "ServiceOrderItemUserFieldSets[0].UserText42",
        "ServiceOrderItemUserFieldSets[0].UserText43",
        "ServiceOrderItemUserFieldSets[0].UserText44",
        "ServiceOrderItemUserFieldSets[0].UserText45",
        "ServiceOrderItemUserFieldSets[0].UserText46",
        "ServiceOrderItemUserFieldSets[0].UserText47",
        "ServiceOrderItemUserFieldSets[0].UserText48",
        "ServiceOrderItemUserFieldSets[0].UserText49",
        "ServiceOrderItemUserFieldSets[0].UserText50"
    };

    static void Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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

            var changedRows = PullChangedServiceOrderItems(client, effectiveSince);
            Console.WriteLine($"-> Changed/new service order items returned: {changedRows.Count:N0}");

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
                .OrderBy(r => ToInt(GetValue(r, "OrderNumber")))
                .ThenBy(r => ToInt(GetValue(r, "OrderLineNumber")))
                .ToList();

            WriteCsv(OutputFilePath, finalRows, DesiredColumns);
            WriteRunState(RunStatePath, now);

            Console.WriteLine($"-> Inserted rows: {inserted:N0}");
            Console.WriteLine($"-> Updated rows: {updated:N0}");
            Console.WriteLine($"-> Final CSV rows: {finalRows.Count:N0}");
            Console.WriteLine($"-> CSV saved: {OutputFilePath}");
            Console.WriteLine($"-> Run state saved: {RunStatePath}");
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

    private static List<Dictionary<string, string>> PullChangedServiceOrderItems(ApiClient client, DateTime since)
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

            var response = client.Endpoints.ServiceOrderItems.Search(OrgCode, odata);
            var results = response.Results?.ToList() ?? new List<ServiceOrderItemsModel>();

            foreach (var item in results)
            {
                processed++;

                DateTime changedOn = ToDateTime(item.ChangedOn);
                if (changedOn != DateTime.MinValue && changedOn <= since)
                    continue;

                var row = CreateBlankRow();
                row["PullRunOn"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                PopulateRowFromServiceOrderItem(item, row);

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

    private static void PopulateRowFromServiceOrderItem(ServiceOrderItemsModel item, Dictionary<string, string> row)
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
        return $"{GetValue(row, "OrderNumber").Trim()}|{GetValue(row, "OrderLineNumber").Trim()}";
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
