using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

namespace BoothAvailabilitySync;

public sealed class CsvBoothReader
{
    private static readonly string[] RequiredColumns =
    [
        "Event",
        "Booth",
        "BoothStatus",
        "GrossArea",
        "ChangedOn",
        "SequenceNumber"
    ];

    public CsvSnapshot Read(string path, AppLogger log)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Booth CSV was not found.", path);

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            TrimOptions = TrimOptions.Trim,
            BadDataFound = null,
            MissingFieldFound = null,
            HeaderValidated = null
        };

        var rawRows = new List<BoothCsvRow>();
        using (var reader = new StreamReader(path))
        using (var csv = new CsvReader(reader, config))
        {
            if (!csv.Read() || !csv.ReadHeader())
                throw new InvalidDataException("CSV is empty or does not contain a header row.");

            var headers = csv.HeaderRecord ?? Array.Empty<string>();
            var missing = RequiredColumns
                .Where(required => !headers.Contains(required, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            if (missing.Length > 0)
                throw new InvalidDataException($"CSV is missing required columns: {string.Join(", ", missing)}");

            while (csv.Read())
            {
                rawRows.Add(csv.GetRecord<BoothCsvRow>());
            }
        }

        if (rawRows.Count == 0)
            throw new InvalidDataException("CSV contains no booth rows.");

        var statusSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newestByEventBooth = new Dictionary<string, NormalizedBoothRow>(StringComparer.OrdinalIgnoreCase);
        var skippedRows = 0;

        foreach (var row in rawRows)
        {
            var eventId = (row.Event ?? "").Trim();
            var booth = (row.Booth ?? "").Trim();
            var status = (row.BoothStatus ?? "").Trim();
            statusSet.Add(status);

            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(booth))
            {
                skippedRows++;
                continue;
            }

            if (!DateTime.TryParseExact(
                    (row.ChangedOn ?? "").Trim(),
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var changedOn))
            {
                throw new InvalidDataException(
                    $"Could not parse ChangedOn '{row.ChangedOn}' for Event {eventId}, Booth {booth}.");
            }

            decimal grossArea = 0m;
            var areaText = (row.GrossArea ?? "").Trim();
            if (!string.IsNullOrEmpty(areaText) &&
                !decimal.TryParse(areaText, NumberStyles.Number, CultureInfo.InvariantCulture, out grossArea))
            {
                throw new InvalidDataException(
                    $"Could not parse GrossArea '{row.GrossArea}' for Event {eventId}, Booth {booth}.");
            }

            long.TryParse((row.SequenceNumber ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequenceNumber);

            var normalized = new NormalizedBoothRow(
                eventId,
                booth,
                status,
                grossArea,
                changedOn,
                sequenceNumber);

            var key = MakeKey(eventId, booth);
            if (!newestByEventBooth.TryGetValue(key, out var existing) || IsNewer(normalized, existing))
            {
                newestByEventBooth[key] = normalized;
            }
        }

        if (skippedRows > 0)
            log.Warn($"Skipped {skippedRows:N0} row(s) with a blank Event or Booth value.");

        var metrics = newestByEventBooth.Values
            .GroupBy(x => x.EventId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => CalculateMetrics(g.Key, g),
                StringComparer.OrdinalIgnoreCase);

        var summary = new SnapshotSummary
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            RawRows = rawRows.Count,
            UniqueBooths = newestByEventBooth.Count,
            EventCount = metrics.Count,
            AvailableBooths = metrics.Values.Sum(x => x.AvailableBooths),
            AvailableArea = metrics.Values.Sum(x => x.AvailableArea),
            SoldBooths = metrics.Values.Sum(x => x.SoldBooths),
            AreaSold = metrics.Values.Sum(x => x.AreaSold),
            BoothsOnHold = metrics.Values.Sum(x => x.BoothsOnHold),
            StatusesSeen = statusSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
        };

        return new CsvSnapshot(
            metrics,
            summary,
            new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
    }

    private static bool IsNewer(NormalizedBoothRow candidate, NormalizedBoothRow existing)
    {
        var dateCompare = candidate.ChangedOn.CompareTo(existing.ChangedOn);
        if (dateCompare != 0)
            return dateCompare > 0;

        return candidate.SequenceNumber > existing.SequenceNumber;
    }

    private static EventMetrics CalculateMetrics(string eventId, IEnumerable<NormalizedBoothRow> rows)
    {
        var availableBooths = 0;
        var availableArea = 0m;
        var soldBooths = 0;
        var areaSold = 0m;
        var boothsOnHold = 0;

        foreach (var row in rows)
        {
            if (row.BoothStatus.Equals("AV", StringComparison.OrdinalIgnoreCase))
            {
                availableBooths++;
                availableArea += row.GrossArea;
            }
            else if (row.BoothStatus.Equals("RE", StringComparison.OrdinalIgnoreCase) || row.BoothStatus == "30")
            {
                soldBooths++;
                areaSold += row.GrossArea;
            }
            else if (row.BoothStatus == "20")
            {
                boothsOnHold++;
            }
        }

        return new EventMetrics(
            eventId,
            availableBooths,
            decimal.Round(availableArea, 2, MidpointRounding.AwayFromZero),
            soldBooths,
            decimal.Round(areaSold, 2, MidpointRounding.AwayFromZero),
            boothsOnHold);
    }

    private static string MakeKey(string eventId, string booth) => $"{eventId}\u001F{booth}";
}
