using Ungerboeck.Api.Models.Subjects;

namespace ServiceOrderEntry;

internal sealed record ContractSnapshot(string Organization, string Owner, int OwnerId, DocumentInfo Document, string ContentHash, string PaymentSchedule);
internal sealed record ContractSelection(IReadOnlyList<ContractSnapshot> Sources, IReadOnlyList<ContractSnapshot> OrderDocuments,
    ContractSnapshot? ScheduleSource, string PaymentSchedule, bool NearbyFallback, string ReviewMessage);
internal sealed record ContractCopyIdentity(ContractSnapshot Source, DocumentInfo Destination, string DestinationHash);

internal static class ContractRules
{
    public static ContractSelection Select(IEnumerable<ContractSnapshot> source, IEnumerable<ContractSnapshot> destination, bool fallback = false)
    {
        var sources = Ordered(source).ToList();
        var destinations = Ordered(destination).ToList();
        var applicable = sources.Concat(destinations).ToList();
        var schedules = applicable.Where(x => x.PaymentSchedule.Length > 0).ToList();
        var distinct = schedules.Select(x => PaymentScheduleExtractor.NormalizeForComparison(x.PaymentSchedule)).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 1) return new(sources, destinations, null, "", fallback, "Applicable contracts contain conflicting payment schedules; REVIEW.");
        if (distinct.Count == 0) return new(sources, destinations, null, "", fallback, "No complete Payment Schedule was found in the applicable contracts.");
        if (destinations.GroupBy(x => x.ContentHash).Any(x => x.Count() > 1))
            return new(sources, destinations, null, "", fallback, "Multiple indistinguishable order contract copies require REVIEW.");
        var selected = Ordered(schedules).First(); // All schedules were proven equivalent; stable ordering only chooses their audit identity.
        return new(sources, destinations, selected, selected.PaymentSchedule, fallback, "");
    }

    public static IEnumerable<ContractSnapshot> Ordered(IEnumerable<ContractSnapshot> values) => values.OrderBy(x => x.Organization, StringComparer.Ordinal)
        .ThenBy(x => x.Owner, StringComparer.Ordinal).ThenBy(x => x.OwnerId).ThenBy(x => x.Document.Type, StringComparer.Ordinal).ThenBy(x => x.Document.SequenceNumber);

    public static bool SameContent(DocumentInfo a, DocumentInfo b) => a.ContentHash.Length > 0 && b.ContentHash.Length > 0 &&
        a.ContentHash == b.ContentHash && TextRules.Same(a.Category, b.Category);
}

internal sealed record ManagedNoteDecision(string Action, NotesModel? Note, string Message);
internal static class ManagedNoteRules
{
    public const string Title = "Payment Schedule [KWI ServiceOrderEntry]";
    public static string ContentHash(string? text) => OrderIdentity.Hash(PaymentScheduleExtractor.NormalizeForComparison(text));
    public static ManagedNoteDecision Resolve(IReadOnlyList<NotesModel> notes, IEnumerable<StageEvidence>? stages = null)
    {
        var proofs = (stages ?? []).Where(x => x.Status == StageStatus.Verified && x.Operation is "Add payment schedule note" or "Update payment schedule note")
            .Select(x => Newtonsoft.Json.JsonConvert.DeserializeObject<NotesModel>(x.Result)).Where(x => x is not null).ToList();
        var provenIds = proofs.Select(x => Convert.ToInt32(x!.SequenceNumber)).ToHashSet();
        var candidates = notes.Where(x => TextRules.Same(x.Title, Title) || provenIds.Contains(Convert.ToInt32(x.SequenceNumber))).ToList();
        if (candidates.Count > 1) return new("REVIEW", null, "Multiple candidate automation-managed payment notes require REVIEW.");
        if (provenIds.Count > 0 && !notes.Any(x => provenIds.Contains(Convert.ToInt32(x.SequenceNumber))))
            return new("REVIEW", null, "Journaled managed payment note is missing; no replacement note may be created automatically.");
        if (candidates.Count == 1)
        {
            var note = candidates[0];
            var proof = proofs.LastOrDefault(x => x!.SequenceNumber == note.SequenceNumber);
            // Legacy ownership requires the journal's stable ID and unchanged verified title/content, not a generic SON class/title.
            if (!TextRules.Same(note.Title, Title) && (proof is null || !TextRules.Same(note.Title, proof.Title) || ContentHash(Text(note)) != ContentHash(Text(proof))))
                return new("REVIEW", null, "Legacy payment-note ownership/content is not established by journal evidence.");
            return new("OWNED", note, "");
        }
        if (notes.Any(x => TextRules.Same(x.Title, "Payment Schedule")))
            return new("REVIEW", null, "Unjournaled legacy Payment Schedule note requires ownership review; it is preserved.");
        return new("ADD", null, "");
    }
    public static string Text(NotesModel note) => string.IsNullOrWhiteSpace(note.PlainText) ? note.Text ?? "" : note.PlainText;
}
