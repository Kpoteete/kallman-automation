// Adaptive full rebuild version for ServiceOrderItems.
// Pulls OrderNumber 1 through 50,000 and automatically splits large ranges to avoid API-capped results.
// Final output overwrites ServiceOrderItems_Pull.csv only after a temp CSV is successfully written.
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

    // Full rebuild version: credentials can come from environment variables.
    // Fallback values are included so this can run before rotation.
    private const string DefaultApiUserId = "KYLEPAPI";
    private const string DefaultSecret = "8c247eb8-2342-452a-95c3-cf22bd1c6a56";
    private const string DefaultKey = "e2b97782-08d7-40f3-bdbc-fbef5095154c";

    private const string OutputFolder =
        @"C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents";

    private static readonly string OutputFilePath = Path.Combine(OutputFolder, "ServiceOrderItems_Pull.csv");
    private static readonly string TempOutputFilePath = Path.Combine(OutputFolder, "ServiceOrderItems_Pull.tmp.csv");

    private const int InitialOrderBatchSize = 250;
    private const int MinimumOrderBatchSize = 1;
    private const int SuspectedApiCapRowCount = 250;
    private const int DefaultStartOrderNumber = 1;
    private const int DefaultEndOrderNumber = 75000;
    private const int ThrottleEveryQueries = 10;
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

            int startOrderNumber = GetOptionalIntEnvironmentVariable("MOMENTUS_START_ORDER_NUMBER", DefaultStartOrderNumber);
            int endOrderNumber = GetOptionalIntEnvironmentVariable("MOMENTUS_END_ORDER_NUMBER", DefaultEndOrderNumber);

            if (endOrderNumber < startOrderNumber)
                throw new InvalidOperationException($"End order number {endOrderNumber} is lower than start order number {startOrderNumber}.");

            Console.WriteLine($"-> Full rebuild order range: {startOrderNumber:N0} - {endOrderNumber:N0}");
            Console.WriteLine($"-> Pulling service order items by OrderNumber, starting with {InitialOrderBatchSize:N0} order increments...");
            Console.WriteLine($"-> If a range returns {SuspectedApiCapRowCount:N0}+ rows, it will be split smaller to avoid capped results.");
            Console.WriteLine($"-> Output file: {OutputFilePath}");

            var rowsByKey = PullAllServiceOrderItemsByOrderNumber(client, startOrderNumber, endOrderNumber);

            var finalRows = rowsByKey.Values
                .OrderBy(r => ToInt(GetValue(r, "OrderNumber")))
                .ThenBy(r => ToInt(GetValue(r, "OrderLineNumber")))
                .ToList();

            WriteCsv(TempOutputFilePath, finalRows, DesiredColumns);

            if (File.Exists(OutputFilePath))
                File.Delete(OutputFilePath);

            File.Move(TempOutputFilePath, OutputFilePath);

            Console.WriteLine($"-> Final CSV rows: {finalRows.Count:N0}");
            Console.WriteLine($"-> CSV saved: {OutputFilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL:");
            Console.WriteLine(ex.ToString());
        }
    }

    private static ApiClient BuildClient()
    {
        string apiUserId = GetEnvironmentVariableOrDefault("MOMENTUS_APIUSER", DefaultApiUserId);
        string secret = GetEnvironmentVariableOrDefault("MOMENTUS_SECRET", DefaultSecret);
        string key = GetEnvironmentVariableOrDefault("MOMENTUS_KEY", DefaultKey);

        var auth = new Jwt
        {
            APIUserID = apiUserId,
            Secret = secret,
            Key = key,
            UngerboeckURI = UngerboeckUri,
            AutoRefresh = new AutoRefresh()
        };

        return new ApiClient(auth);
    }

    private static int GetNewestServiceOrderNumber(ApiClient client)
    {
        // Find the current high-water order number by looking at recent service orders.
        // This avoids relying on SDK support for $orderby / $top, which failed in your run.
        object? serviceOrdersEndpoint = GetEndpoint(client, "ServiceOrders");
        if (serviceOrdersEndpoint == null)
            throw new InvalidOperationException("Could not find client.Endpoints.ServiceOrders in this SDK version.");

        DateTime weekStart = DateTime.Today.AddDays(-7);
        string weekStartText = weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var orderNumbers = new List<int>();

        string[] lastWeekQueries = new[]
        {
            $"EnteredOn ge datetime'{weekStartText}'",
            $"ChangedOn ge datetime'{weekStartText}'",
            $"OrderDate ge datetime'{weekStartText}'",
            $"OrderedOn ge datetime'{weekStartText}'"
        };

        foreach (string query in lastWeekQueries)
        {
            try
            {
                Console.WriteLine($"-> Searching ServiceOrders high number using: {query}");

                var results = SearchEndpoint(serviceOrdersEndpoint, OrgCode, query).ToList();
                int found = 0;

                foreach (object order in results)
                {
                    int orderNumber = GetOrderNumberFromObject(order);
                    if (orderNumber > 0)
                    {
                        orderNumbers.Add(orderNumber);
                        found++;
                    }
                }

                Console.WriteLine($"-> ServiceOrders returned {found:N0} usable order numbers for this query.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"-> ServiceOrders query failed and will be skipped: {query}");
                Console.WriteLine($"   {ex.GetType().Name}: {ex.Message}");
            }
        }

        int newestFromLastWeek = orderNumbers.DefaultIfEmpty(0).Max();
        if (newestFromLastWeek > 0)
        {
            Console.WriteLine($"-> Newest/highest service order number found from last-week search: {newestFromLastWeek:N0}");
            return newestFromLastWeek;
        }

        // Fallback: use MOMENTUS_MAX_ORDER_NUMBER if no recent service orders were returned.
        int manualMax = GetOptionalIntEnvironmentVariable("MOMENTUS_MAX_ORDER_NUMBER", 0);
        if (manualMax > 0)
            return manualMax;

        throw new InvalidOperationException(
            "Could not determine newest service order number from last week's ServiceOrders API results. " +
            "Set MOMENTUS_MAX_ORDER_NUMBER as a fallback, or confirm the correct ServiceOrders date field name.");
    }

    private static Dictionary<string, Dictionary<string, string>> PullAllServiceOrderItemsByOrderNumber(ApiClient client, int startOrderNumber, int endOrderNumber)
    {
        var rowsByKey = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        int queryCount = 0;

        for (int batchStart = startOrderNumber; batchStart <= endOrderNumber; batchStart += InitialOrderBatchSize)
        {
            int batchEnd = Math.Min(batchStart + InitialOrderBatchSize - 1, endOrderNumber);
            PullServiceOrderItemsAdaptive(client, batchStart, batchEnd, rowsByKey, ref queryCount);
        }

        return rowsByKey;
    }

    private static void PullServiceOrderItemsAdaptive(
        ApiClient client,
        int batchStart,
        int batchEnd,
        Dictionary<string, Dictionary<string, string>> rowsByKey,
        ref int queryCount)
    {
        string odata = batchStart == batchEnd
            ? $"OrderNumber eq {batchStart}"
            : $"OrderNumber ge {batchStart} and OrderNumber le {batchEnd}";

        queryCount++;
        Console.WriteLine($"-> Query {queryCount:N0}: orders {batchStart:N0} - {batchEnd:N0}");

        var response = client.Endpoints.ServiceOrderItems.Search(OrgCode, odata);
        var results = response.Results?.ToList() ?? new List<ServiceOrderItemsModel>();

        Console.WriteLine($"   Returned: {results.Count:N0}");

        int rangeSize = batchEnd - batchStart + 1;

        if (results.Count >= SuspectedApiCapRowCount && rangeSize > MinimumOrderBatchSize)
        {
            int midpoint = batchStart + ((batchEnd - batchStart) / 2);

            Console.WriteLine($"   Possible API cap. Splitting range into {batchStart:N0}-{midpoint:N0} and {midpoint + 1:N0}-{batchEnd:N0}.");

            PullServiceOrderItemsAdaptive(client, batchStart, midpoint, rowsByKey, ref queryCount);
            PullServiceOrderItemsAdaptive(client, midpoint + 1, batchEnd, rowsByKey, ref queryCount);
            return;
        }

        if (results.Count >= SuspectedApiCapRowCount && rangeSize == MinimumOrderBatchSize)
        {
            Console.WriteLine($"   WARNING: Single order {batchStart:N0} returned {results.Count:N0} rows. The API may still be capping this one order.");
        }

        foreach (var item in results)
        {
            var row = CreateBlankRow();
            row["PullRunOn"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            PopulateRowFromServiceOrderItem(item, row);

            string key = GetRowKey(row);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            rowsByKey[key] = row;
        }

        Console.WriteLine($"   Total unique rows: {rowsByKey.Count:N0}");

        if (queryCount % ThrottleEveryQueries == 0)
            System.Threading.Thread.Sleep(ThrottleMs);
    }

    private static object? GetEndpoint(ApiClient client, string endpointName)
    {
        return client.Endpoints
            .GetType()
            .GetProperty(endpointName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?.GetValue(client.Endpoints);
    }

    private static IEnumerable<object> SearchEndpoint(object endpoint, string orgCode, string searchText)
    {
        MethodInfo? searchMethod = endpoint.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => string.Equals(m.Name, "Search", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length >= 2 &&
                       p[0].ParameterType == typeof(string) &&
                       p[1].ParameterType == typeof(string);
            });

        if (searchMethod == null)
            throw new InvalidOperationException($"Search(string, string) was not found on endpoint {endpoint.GetType().Name}.");

        object? response = searchMethod.Invoke(endpoint, new object[] { orgCode, searchText });
        if (response == null)
            return Enumerable.Empty<object>();

        object? results = response.GetType()
            .GetProperty("Results", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?.GetValue(response);

        if (results is System.Collections.IEnumerable enumerable)
            return enumerable.Cast<object>();

        return Enumerable.Empty<object>();
    }

    private static int GetOrderNumberFromObject(object source)
    {
        object? value = GetPropertyPathValue(source, "OrderNumber");
        string text = ToStringSafe(value);
        return ToInt(text);
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
        return int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out int number) ? number : 0;
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

    private static string GetEnvironmentVariableOrDefault(string name, string defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static int GetOptionalIntEnvironmentVariable(string name, int defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : defaultValue;
    }
}
