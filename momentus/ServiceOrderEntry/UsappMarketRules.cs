using System.Globalization;
using Ungerboeck.Api.Models.Subjects;

namespace ServiceOrderEntry;

internal static class UsappMarketRules
{
    internal const string ResourceCode = "USAPP MARKET";

    public static ProcessingIssue? Correction(ExhibitorsModel exhibitor, IReadOnlyList<OrderItemInfo>? completeItems)
    {
        var fields = exhibitor.ExhibitorUserFields;
        var current = fields?.UserText14 ?? "";
        if (string.Equals(current.Trim(), "YES", StringComparison.OrdinalIgnoreCase)) return null;
        if (fields is null || completeItems is null)
            return new("USAPP Market", "The check is unavailable. Manually verify TEXT_14 and the order items; no YES/NO correction could be established.",
                $"TEXT_14='{(fields is null ? "UNAVAILABLE" : current)}'; complete order items={(completeItems is null ? "UNAVAILABLE" : "available")}");

        var resourceItems = completeItems.Where(x => string.Equals(x.ResourceCode.Trim(), ResourceCode, StringComparison.OrdinalIgnoreCase)).ToList();
        var qualifies = resourceItems.Any(x => x.UnitCharge is 550m or 350m);
        if (!qualifies && resourceItems.Any(x => x.UnitCharge is null))
            return new("USAPP Market", "A USAPP MARKET resource line has no readable unit charge. Manually verify TEXT_14 and that line; no YES/NO correction could be established.",
                $"current TEXT_14='{current}'; missing unit charge on lines {string.Join(", ", resourceItems.Where(x => x.UnitCharge is null).Select(x => x.LineNumber))}");
        var desired = qualifies ? "YES" : "NO";
        if (current == desired) return null;
        var lines = resourceItems.Count == 0 ? "no USAPP MARKET resource lines" : string.Join("; ", resourceItems.Select(x =>
            $"line {x.LineNumber}: resource='{x.ResourceCode}', unit charge={x.UnitCharge?.ToString(CultureInfo.InvariantCulture) ?? "UNAVAILABLE"}"));
        return new("USAPP Market", $"Please set exhibitor TEXT_14 to {desired} manually. " +
            (qualifies ? "A USAPP MARKET item at unit charge 550 or 350 was found." : "No USAPP MARKET item at unit charge 550 or 350 was found.") +
            " The automation cannot update this read-only exhibitor UDF. Order processing continues.",
            $"current TEXT_14='{current}'; required TEXT_14='{desired}'; {lines}");
    }
}
