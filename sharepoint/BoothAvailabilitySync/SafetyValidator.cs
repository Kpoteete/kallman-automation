namespace BoothAvailabilitySync;

public static class SafetyValidator
{
    public static IReadOnlyList<SafetyMessage> ValidateBooths(
        CsvSnapshot current,
        SnapshotSummary? previous,
        SafetySettings settings)
    {
        var messages = new List<SafetyMessage>();

        if (current.Summary.RawRows < settings.MinimumRawRows)
        {
            messages.Add(new SafetyMessage(
                true,
                $"Booths CSV contains only {current.Summary.RawRows:N0} rows; minimum configured value is {settings.MinimumRawRows:N0}."));
        }

        var fileAge = DateTimeOffset.UtcNow - current.FileLastWriteUtc;
        if (fileAge.TotalHours > settings.MaximumFileAgeHours)
        {
            messages.Add(new SafetyMessage(
                true,
                $"Booths CSV is {fileAge.TotalHours:N1} hours old; maximum configured age is {settings.MaximumFileAgeHours} hours."));
        }

        if (previous is null)
            return messages;

        if (previous.UniqueBooths > 0)
        {
            var dropPercent = (previous.UniqueBooths - current.Summary.UniqueBooths) * 100m / previous.UniqueBooths;
            if (dropPercent > settings.MaximumUniqueBoothDropPercent)
            {
                messages.Add(new SafetyMessage(
                    true,
                    $"Unique booth count dropped {dropPercent:N1}% from {previous.UniqueBooths:N0} to {current.Summary.UniqueBooths:N0}."));
            }
        }

        if (settings.StopIfAvailableBoothsDropToZero &&
            previous.AvailableBooths > 0 &&
            current.Summary.AvailableBooths == 0)
        {
            messages.Add(new SafetyMessage(
                true,
                $"Available booth total dropped from {previous.AvailableBooths:N0} to zero."));
        }

        if (settings.StopIfSoldBoothsDropToZero &&
            previous.SoldBooths > 0 &&
            current.Summary.SoldBooths == 0)
        {
            messages.Add(new SafetyMessage(
                true,
                $"Sold booth total dropped from {previous.SoldBooths:N0} to zero."));
        }

        var previousStatuses = new HashSet<string>(previous.StatusesSeen ?? [], StringComparer.OrdinalIgnoreCase);
        var newStatuses = current.Summary.StatusesSeen
            .Where(x => !previousStatuses.Contains(x))
            .Select(x => string.IsNullOrWhiteSpace(x) ? "<blank>" : x)
            .ToArray();

        if (newStatuses.Length > 0)
        {
            messages.Add(new SafetyMessage(
                false,
                $"New booth status value(s) appeared since the previous successful run: {string.Join(", ", newStatuses)}."));
        }

        return messages;
    }

    public static IReadOnlyList<SafetyMessage> ValidateEvents(
        EventCsvSnapshot current,
        SafetySettings settings)
    {
        var messages = new List<SafetyMessage>();

        if (current.Summary.UniqueEvents < settings.MinimumEventRows)
        {
            messages.Add(new SafetyMessage(
                true,
                $"Events CSV contains only {current.Summary.UniqueEvents:N0} unique Event IDs; minimum configured value is {settings.MinimumEventRows:N0}."));
        }

        var fileAge = DateTimeOffset.UtcNow - current.FileLastWriteUtc;
        if (fileAge.TotalHours > settings.MaximumEventsFileAgeHours)
        {
            messages.Add(new SafetyMessage(
                true,
                $"Events CSV is {fileAge.TotalHours:N1} hours old; maximum configured age is {settings.MaximumEventsFileAgeHours} hours."));
        }

        if (current.Summary.EventsWithStartDate == 0)
        {
            messages.Add(new SafetyMessage(
                true,
                "Events CSV contains zero populated StartDate values. Event detail updates are blocked."));
        }

        if (current.Summary.DuplicateRows > 0)
        {
            messages.Add(new SafetyMessage(
                false,
                $"Events CSV contained {current.Summary.DuplicateRows:N0} duplicate EventID row(s); newest ChangedOn row was used."));
        }

        if (current.Summary.BlankEventIdRows > 0)
        {
            messages.Add(new SafetyMessage(
                false,
                $"Events CSV contained {current.Summary.BlankEventIdRows:N0} row(s) with blank EventID; those rows were ignored."));
        }

        return messages;
    }
}
