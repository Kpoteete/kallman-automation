using System.Globalization;

namespace BoothAvailabilitySync;

public static class SyncPlanner
{
    public static SyncPlan Build(
        IReadOnlyDictionary<string, EventMetrics> boothMetrics,
        IReadOnlyDictionary<string, EventDetails> eventDetails,
        IReadOnlyList<SharePointItem> sharePointItems,
        ResolvedColumns columns,
        SharePointSettings settings,
        DateTimeOffset timestampUtc)
    {
        var plan = new SyncPlan();

        var itemsWithEventIds = sharePointItems
            .Select(item => new
            {
                Item = item,
                EventId = GraphSharePointClient.ReadEventId(item, columns.EventId)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.EventId))
            .ToList();

        var byEventId = itemsWithEventIds
            .GroupBy(x => x.EventId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Item).ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var duplicate in byEventId.Where(x => x.Value.Count > 1).OrderBy(x => EventSortKey(x.Key)))
            plan.DuplicateSharePointEventIds.Add(duplicate.Key);

        foreach (var eventId in boothMetrics.Keys.Where(x => !byEventId.ContainsKey(x)).OrderBy(EventSortKey))
            plan.BoothCsvEventsMissingInSharePoint.Add(eventId);

        foreach (var eventId in eventDetails.Keys.Where(x => !byEventId.ContainsKey(x)).OrderBy(EventSortKey))
            plan.EventsCsvEventsMissingInSharePoint.Add(eventId);

        foreach (var pair in byEventId.OrderBy(x => EventSortKey(x.Key)))
        {
            var eventId = pair.Key;
            var matches = pair.Value;

            if (matches.Count != 1)
                continue;

            var item = matches[0];
            var metricChanges = new List<FieldChange>();
            var detailChanges = new List<DetailFieldChange>();
            var fieldsToWrite = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            var hasBoothMetrics = boothMetrics.TryGetValue(eventId, out var metrics);
            if (hasBoothMetrics && metrics is not null)
            {
                plan.BoothMatchedEvents++;

                AddNumericChange(metricChanges, "Available Booths", columns.AvailableBooths,
                    GraphSharePointClient.ReadDecimal(item, columns.AvailableBooths), metrics.AvailableBooths, true);
                AddNumericChange(metricChanges, "Available Area", columns.AvailableArea,
                    GraphSharePointClient.ReadDecimal(item, columns.AvailableArea), metrics.AvailableArea, false);
                AddNumericChange(metricChanges, "Sold Booths", columns.SoldBooths,
                    GraphSharePointClient.ReadDecimal(item, columns.SoldBooths), metrics.SoldBooths, true);
                AddNumericChange(metricChanges, "Area Sold", columns.AreaSold,
                    GraphSharePointClient.ReadDecimal(item, columns.AreaSold), metrics.AreaSold, false);
                AddNumericChange(metricChanges, "Booths on Hold", columns.BoothsOnHold,
                    GraphSharePointClient.ReadDecimal(item, columns.BoothsOnHold), metrics.BoothsOnHold, true);

                foreach (var change in metricChanges)
                {
                    fieldsToWrite[change.InternalName] = change.IsInteger
                        ? decimal.ToInt32(change.NewValue)
                        : change.NewValue;
                }

                if (settings.RefreshLastUpdatedOnEveryMatchedEvent)
                    fieldsToWrite[columns.LastUpdated] = timestampUtc.ToString("O", CultureInfo.InvariantCulture);
            }
            else
            {
                plan.SharePointEventsMissingInBoothCsv.Add(eventId);
            }

            var hasEventDetails = eventDetails.TryGetValue(eventId, out var details);
            if (hasEventDetails && details is not null)
            {
                plan.EventsMatchedEvents++;

                AddDateChange(detailChanges, "Start Date", columns.StartDate,
                    GraphSharePointClient.ReadDateOnly(item, columns.StartDate), details.StartDate);
                AddDateChange(detailChanges, "End Date", columns.EndDate,
                    GraphSharePointClient.ReadDateOnly(item, columns.EndDate), details.EndDate);
                AddTextChange(detailChanges, "City", columns.City,
                    GraphSharePointClient.ReadString(item, columns.City), details.City);
                AddTextChange(detailChanges, "Country", columns.Country,
                    GraphSharePointClient.ReadString(item, columns.Country), details.Country);
                AddTextChange(detailChanges, "Subclass", columns.Subclass,
                    GraphSharePointClient.ReadString(item, columns.Subclass), details.Subclass);

                foreach (var change in detailChanges)
                    fieldsToWrite[change.InternalName] = change.NewValue;
            }
            else
            {
                plan.SharePointEventsMissingInEventsCsv.Add(eventId);
            }

            if (metricChanges.Count > 0)
                plan.EventsWithMetricChanges++;

            if (detailChanges.Count > 0)
                plan.EventsWithDetailChanges++;

            if (metricChanges.Count == 0 && detailChanges.Count == 0)
                plan.EventsWithNoChanges++;

            if (fieldsToWrite.Count > 0)
            {
                plan.Updates.Add(new EventUpdatePlan
                {
                    EventId = eventId,
                    SharePointItemId = item.Id,
                    MetricChanges = metricChanges,
                    DetailChanges = detailChanges,
                    FieldsToWrite = fieldsToWrite
                });
            }
        }

        return plan;
    }

    private static void AddNumericChange(
        ICollection<FieldChange> changes,
        string label,
        string internalName,
        decimal? oldValue,
        decimal newValue,
        bool isInteger)
    {
        var normalizedNew = isInteger
            ? decimal.Truncate(newValue)
            : decimal.Round(newValue, 2, MidpointRounding.AwayFromZero);

        decimal? normalizedOld = oldValue.HasValue
            ? (isInteger
                ? decimal.Truncate(oldValue.Value)
                : decimal.Round(oldValue.Value, 2, MidpointRounding.AwayFromZero))
            : null;

        if (!normalizedOld.HasValue || normalizedOld.Value != normalizedNew)
            changes.Add(new FieldChange(label, internalName, normalizedOld, normalizedNew, isInteger));
    }

    private static void AddTextChange(
        ICollection<DetailFieldChange> changes,
        string label,
        string internalName,
        string? oldValue,
        string? newValue)
    {
        var normalizedOld = NormalizeText(oldValue);
        var normalizedNew = NormalizeText(newValue);

        if (string.Equals(normalizedOld, normalizedNew, StringComparison.Ordinal))
            return;

        changes.Add(new DetailFieldChange(
            label,
            internalName,
            normalizedOld,
            normalizedNew,
            normalizedNew));
    }

    private static void AddDateChange(
        ICollection<DetailFieldChange> changes,
        string label,
        string internalName,
        DateOnly? oldValue,
        DateOnly? newValue)
    {
        if (oldValue == newValue)
            return;

        var oldDisplay = oldValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var newDisplay = newValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        object? valueToWrite = newValue.HasValue
            ? $"{newValue.Value:yyyy-MM-dd}T00:00:00Z"
            : null;

        changes.Add(new DetailFieldChange(
            label,
            internalName,
            oldDisplay,
            newDisplay,
            valueToWrite));
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    private static string EventSortKey(string eventId)
    {
        return long.TryParse(eventId, out var number)
            ? number.ToString("D20", CultureInfo.InvariantCulture)
            : "Z" + eventId;
    }
}
