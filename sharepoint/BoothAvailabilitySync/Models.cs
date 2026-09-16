using System.Text.Json;

namespace BoothAvailabilitySync;

public sealed class AppSettings
{
    public string InputFile { get; set; } = "";
    public string EventsInputFile { get; set; } = "";
    public string StateFolder { get; set; } = "state";
    public SharePointSettings SharePoint { get; set; } = new();
    public AuthSettings Auth { get; set; } = new();
    public SafetySettings Safety { get; set; } = new();
}

public sealed class SharePointSettings
{
    public string Hostname { get; set; } = "";
    public string SitePath { get; set; } = "";
    public string ListName { get; set; } = "";
    public string EventIdColumn { get; set; } = "Event ID";

    public string StartDateColumn { get; set; } = "Start Date";
    public string EndDateColumn { get; set; } = "End Date";
    public string CityColumn { get; set; } = "City";
    public string CountryColumn { get; set; } = "Country";
    public string SubclassColumn { get; set; } = "Subclass";

    public string AvailableBoothsColumn { get; set; } = "Available Booths";
    public string AvailableAreaColumn { get; set; } = "Available Area";
    public string SoldBoothsColumn { get; set; } = "Sold Booths";
    public string AreaSoldColumn { get; set; } = "Area Sold";
    public string BoothsOnHoldColumn { get; set; } = "Booths on Hold";
    public string LastUpdatedColumn { get; set; } = "Booth Availability Last Updated";
    public bool RefreshLastUpdatedOnEveryMatchedEvent { get; set; } = true;
}

public sealed class AuthSettings
{
    public string Mode { get; set; } = "InteractiveBrowser";
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string RedirectUri { get; set; } = "http://localhost";
    public string CertificateThumbprint { get; set; } = "";
    public string CertificateStoreLocation { get; set; } = "CurrentUser";
}

public sealed class SafetySettings
{
    public int MaximumFileAgeHours { get; set; } = 24;
    public int MaximumEventsFileAgeHours { get; set; } = 24;
    public int MinimumRawRows { get; set; } = 1000;
    public int MinimumEventRows { get; set; } = 100;
    public decimal MaximumUniqueBoothDropPercent { get; set; } = 40m;
    public bool StopIfAvailableBoothsDropToZero { get; set; } = true;
    public bool StopIfSoldBoothsDropToZero { get; set; } = true;
}

public sealed class BoothCsvRow
{
    public string PullRunOn { get; set; } = "";
    public string SequenceNumber { get; set; } = "";
    public string Booth { get; set; } = "";
    public string BoothStatus { get; set; } = "";
    public string Event { get; set; } = "";
    public string GrossArea { get; set; } = "";
    public string ChangedOn { get; set; } = "";
}

public sealed record NormalizedBoothRow(
    string EventId,
    string Booth,
    string BoothStatus,
    decimal GrossArea,
    DateTime ChangedOn,
    long SequenceNumber);

public sealed record EventMetrics(
    string EventId,
    int AvailableBooths,
    decimal AvailableArea,
    int SoldBooths,
    decimal AreaSold,
    int BoothsOnHold);

public sealed class SnapshotSummary
{
    public DateTimeOffset CreatedUtc { get; set; }
    public int RawRows { get; set; }
    public int UniqueBooths { get; set; }
    public int EventCount { get; set; }
    public int AvailableBooths { get; set; }
    public decimal AvailableArea { get; set; }
    public int SoldBooths { get; set; }
    public decimal AreaSold { get; set; }
    public int BoothsOnHold { get; set; }
    public List<string> StatusesSeen { get; set; } = new();
}

public sealed record CsvSnapshot(
    IReadOnlyDictionary<string, EventMetrics> MetricsByEvent,
    SnapshotSummary Summary,
    DateTimeOffset FileLastWriteUtc);

public sealed record EventDetails(
    string EventId,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string City,
    string Country,
    string Subclass);

public sealed class EventCsvSummary
{
    public int RawRows { get; set; }
    public int UniqueEvents { get; set; }
    public int DuplicateRows { get; set; }
    public int BlankEventIdRows { get; set; }
    public int EventsWithStartDate { get; set; }
    public int EventsWithEndDate { get; set; }
    public int EventsWithCity { get; set; }
    public int EventsWithCountry { get; set; }
    public int EventsWithSubclass { get; set; }
}

public sealed record EventCsvSnapshot(
    IReadOnlyDictionary<string, EventDetails> DetailsByEvent,
    EventCsvSummary Summary,
    DateTimeOffset FileLastWriteUtc);

public sealed record SafetyMessage(bool Fatal, string Message);

public sealed record SharePointColumn(string Id, string InternalName, string DisplayName, bool ReadOnly);

public sealed record SharePointListInfo(string Id, string DisplayName);

public sealed class SharePointItem
{
    public string Id { get; init; } = "";
    public Dictionary<string, JsonElement> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ResolvedColumns(
    string EventId,
    string StartDate,
    string EndDate,
    string City,
    string Country,
    string Subclass,
    string AvailableBooths,
    string AvailableArea,
    string SoldBooths,
    string AreaSold,
    string BoothsOnHold,
    string LastUpdated);

public sealed record FieldChange(
    string Label,
    string InternalName,
    decimal? OldValue,
    decimal NewValue,
    bool IsInteger);

public sealed record DetailFieldChange(
    string Label,
    string InternalName,
    string? OldDisplayValue,
    string? NewDisplayValue,
    object? NewValue);

public sealed class EventUpdatePlan
{
    public string EventId { get; init; } = "";
    public string SharePointItemId { get; init; } = "";
    public List<FieldChange> MetricChanges { get; init; } = new();
    public List<DetailFieldChange> DetailChanges { get; init; } = new();
    public Dictionary<string, object?> FieldsToWrite { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SyncPlan
{
    public List<EventUpdatePlan> Updates { get; } = new();

    public List<string> BoothCsvEventsMissingInSharePoint { get; } = new();
    public List<string> EventsCsvEventsMissingInSharePoint { get; } = new();
    public List<string> SharePointEventsMissingInBoothCsv { get; } = new();
    public List<string> SharePointEventsMissingInEventsCsv { get; } = new();
    public List<string> DuplicateSharePointEventIds { get; } = new();

    public int BoothMatchedEvents { get; set; }
    public int EventsMatchedEvents { get; set; }
    public int EventsWithMetricChanges { get; set; }
    public int EventsWithDetailChanges { get; set; }
    public int EventsWithNoChanges { get; set; }
}
