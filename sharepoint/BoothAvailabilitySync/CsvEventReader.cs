using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Text;

namespace BoothAvailabilitySync;

public sealed class CsvEventReader
{
    private static readonly string[] RequiredHeaders =
    [
        "EventID",
        "StartDate",
        "EndDate",
        "EventUserFieldSets[0].UserText10",
        "EventUserFieldSets[0].UserText11",
        "Class"
    ];

    public EventCsvSnapshot Read(string path, AppLogger log)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Events CSV was not found.", path);

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
            TrimOptions = TrimOptions.Trim
        };

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(reader, config);

        if (!csv.Read() || !csv.ReadHeader())
            throw new InvalidDataException("Events CSV does not contain a readable header row.");

        var headers = csv.HeaderRecord ?? [];
        var headerLookup = headers.ToDictionary(x => x, x => x, StringComparer.OrdinalIgnoreCase);

        foreach (var required in RequiredHeaders)
        {
            if (!headerLookup.ContainsKey(required))
                throw new InvalidDataException($"Events CSV is missing required column '{required}'.");
        }

        var hasChangedOn = headerLookup.ContainsKey("ChangedOn");
        var byEvent = new Dictionary<string, EventCandidate>(StringComparer.OrdinalIgnoreCase);
        var rawRows = 0;
        var blankEventIds = 0;
        var duplicateRows = 0;

        while (csv.Read())
        {
            rawRows++;

            var eventId = NormalizeEventId(csv.GetField(headerLookup["EventID"]));
            if (string.IsNullOrWhiteSpace(eventId))
            {
                blankEventIds++;
                continue;
            }

            var details = new EventDetails(
                eventId,
                ParseDateOnly(csv.GetField(headerLookup["StartDate"]), "StartDate", eventId),
                ParseDateOnly(csv.GetField(headerLookup["EndDate"]), "EndDate", eventId),
                NormalizeText(csv.GetField(headerLookup["EventUserFieldSets[0].UserText10"])),
                NormalizeText(csv.GetField(headerLookup["EventUserFieldSets[0].UserText11"])),
                NormalizeText(csv.GetField(headerLookup["Class"])));

            var changedOn = hasChangedOn
                ? ParseDateTime(csv.GetField(headerLookup["ChangedOn"]))
                : DateTime.MinValue;

            if (byEvent.TryGetValue(eventId, out var existing))
            {
                duplicateRows++;
                if (changedOn >= existing.ChangedOn)
                    byEvent[eventId] = new EventCandidate(details, changedOn);
            }
            else
            {
                byEvent[eventId] = new EventCandidate(details, changedOn);
            }
        }

        var detailsByEvent = byEvent.ToDictionary(
            x => x.Key,
            x => x.Value.Details,
            StringComparer.OrdinalIgnoreCase);

        var summary = new EventCsvSummary
        {
            RawRows = rawRows,
            UniqueEvents = detailsByEvent.Count,
            DuplicateRows = duplicateRows,
            BlankEventIdRows = blankEventIds,
            EventsWithStartDate = detailsByEvent.Values.Count(x => x.StartDate.HasValue),
            EventsWithEndDate = detailsByEvent.Values.Count(x => x.EndDate.HasValue),
            EventsWithCity = detailsByEvent.Values.Count(x => !string.IsNullOrWhiteSpace(x.City)),
            EventsWithCountry = detailsByEvent.Values.Count(x => !string.IsNullOrWhiteSpace(x.Country)),
            EventsWithSubclass = detailsByEvent.Values.Count(x => !string.IsNullOrWhiteSpace(x.Subclass))
        };

        return new EventCsvSnapshot(
            detailsByEvent,
            summary,
            new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
    }

    private static string NormalizeEventId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        if (decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) &&
            number == decimal.Truncate(number))
        {
            return decimal.Truncate(number).ToString(CultureInfo.InvariantCulture);
        }

        return trimmed;
    }

    private static string NormalizeText(string? value) => value?.Trim() ?? string.Empty;

    private static DateOnly? ParseDateOnly(string? value, string fieldName, string eventId)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateTime.TryParse(
            value.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
            out var parsed))
        {
            return DateOnly.FromDateTime(parsed);
        }

        throw new InvalidDataException(
            $"Events CSV contains an invalid {fieldName} value '{value}' for EventID {eventId}.");
    }

    private static DateTime ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DateTime.MinValue;

        return DateTime.TryParse(
            value.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
            out var parsed)
            ? parsed
            : DateTime.MinValue;
    }

    private sealed record EventCandidate(EventDetails Details, DateTime ChangedOn);
}
