namespace BoothAvailabilitySync;

public static class SyncPlanner
{
    public static SyncPlan Build(
        IReadOnlyDictionary<string, EventMetrics> csvMetrics,
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

        foreach (var duplicate in byEventId.Where(x => x.Value.Count > 1).OrderBy(x => x.Key))
            plan.DuplicateSharePointEventIds.Add(duplicate.Key);

        foreach (var metrics in csvMetrics.Values.OrderBy(x => EventSortKey(x.EventId)))
        {
            if (!byEventId.TryGetValue(metrics.EventId, out var matches))
            {
                plan.CsvEventsMissingInSharePoint.Add(metrics.EventId);
                continue;
            }

            if (matches.Count != 1)
                continue;

            plan.MatchedEvents++;
            var item = matches[0];
            var changes = new List<FieldChange>();

            AddChange(changes, "Available Booths", columns.AvailableBooths,
                GraphSharePointClient.ReadDecimal(item, columns.AvailableBooths), metrics.AvailableBooths, true);
            AddChange(changes, "Available Area", columns.AvailableArea,
                GraphSharePointClient.ReadDecimal(item, columns.AvailableArea), metrics.AvailableArea, false);
            AddChange(changes, "Sold Booths", columns.SoldBooths,
                GraphSharePointClient.ReadDecimal(item, columns.SoldBooths), metrics.SoldBooths, true);
            AddChange(changes, "Area Sold", columns.AreaSold,
                GraphSharePointClient.ReadDecimal(item, columns.AreaSold), metrics.AreaSold, false);
            AddChange(changes, "Booths on Hold", columns.BoothsOnHold,
                GraphSharePointClient.ReadDecimal(item, columns.BoothsOnHold), metrics.BoothsOnHold, true);

            if (changes.Count > 0)
                plan.EventsWithMetricChanges++;
            else
                plan.EventsWithNoMetricChanges++;

            var fieldsToWrite = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes)
            {
                fieldsToWrite[change.InternalName] = change.IsInteger
                    ? decimal.ToInt32(change.NewValue)
                    : change.NewValue;
            }

            if (settings.RefreshLastUpdatedOnEveryMatchedEvent)
                fieldsToWrite[columns.LastUpdated] = timestampUtc.ToString("O");

            if (fieldsToWrite.Count > 0)
            {
                plan.Updates.Add(new EventUpdatePlan
                {
                    EventId = metrics.EventId,
                    SharePointItemId = item.Id,
                    MetricChanges = changes,
                    FieldsToWrite = fieldsToWrite
                });
            }
        }

        var csvEventIds = new HashSet<string>(csvMetrics.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var eventId in byEventId.Keys.Where(x => !csvEventIds.Contains(x)).OrderBy(EventSortKey))
            plan.SharePointEventsMissingInCsv.Add(eventId);

        return plan;
    }

    private static void AddChange(
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

    private static string EventSortKey(string eventId)
    {
        return long.TryParse(eventId, out var number)
            ? number.ToString("D20")
            : "Z" + eventId;
    }
}
