using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ungerboeck.Api.Models.Subjects;

namespace ServiceOrderEntry;

internal static class ItemEvidenceRules
{
    public static OrderItemInfo Capture(ServiceOrderItemsModel item)
    {
        var serialized = JsonConvert.SerializeObject(item);
        var decision = JObject.FromObject(item);
        // Momentus can update these audit fields as a side effect of header activation.
        // Keep all remaining fields, including financial values, in the audit fingerprint.
        // Fingerprint comparisons no longer gate processing; live items still determine the category.
        decision.Remove(nameof(item.ChangedOn)); decision.Remove(nameof(item.ChangedBy));
        var originalEntry = JsonConvert.DeserializeObject<ServiceOrderItemsModel>(serialized)!;
        originalEntry.ChangedOn = item.EnteredOn; originalEntry.ChangedBy = item.EnteredBy;
        return new(Convert.ToInt32(item.OrderLineNumber), item.ResourceCode ?? "", item.Description ?? "", item.AltDesc ?? "")
        {
            UnitCharge = item.UnitCharge,
            EvidenceHash = OrderIdentity.Hash(serialized), DecisionHash = OrderIdentity.Hash(decision.ToString(Formatting.None)),
            OriginalEntryMetadataHash = OrderIdentity.Hash(JsonConvert.SerializeObject(originalEntry))
        };
    }

    public static bool Same(IReadOnlyList<OrderItemInfo> expected, IReadOnlyList<OrderItemInfo> current)
    {
        var before = expected.OrderBy(x => x.LineNumber).ToList(); var after = current.OrderBy(x => x.LineNumber).ToList();
        return before.Count == after.Count && before.Zip(after).All(pair =>
            pair.First.LineNumber == pair.Second.LineNumber && pair.First.ResourceCode == pair.Second.ResourceCode &&
            pair.First.Description == pair.Second.Description && pair.First.AlternateDescription == pair.Second.AlternateDescription &&
            (pair.First.DecisionHash.Length > 0 ? pair.First.DecisionHash == pair.Second.DecisionHash :
                pair.First.EvidenceHash.Length > 0 && (pair.First.EvidenceHash == pair.Second.EvidenceHash ||
                pair.First.EvidenceHash == pair.Second.OriginalEntryMetadataHash)));
    }
}
