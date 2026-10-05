using Newtonsoft.Json;
using Ungerboeck.Api.Models.Subjects;

namespace ServiceOrderEntry;

internal sealed record VerifiedHandoff(int EventId, int ExhibitorId, int OrderNumber, string ExhibitorName, DateTime? OrderDate,
    OrderFields Order, string CategoryName, EffectiveBillingState Billing, string PaymentSchedule, int NoteSequence,
    string NoteHash, bool ApprovalNeeded, IReadOnlyList<DocumentInfo> Attachments)
{
    public RunRow EmailRow()
    {
        var row = new RunRow { EventId = EventId, ExhibitorId = ExhibitorId, OrderNumber = OrderNumber, ExhibitorName = ExhibitorName,
            OrderDate = OrderDate, ProposedOrderAccountRep = Order.Rep, ProposedCategory = Order.Category, ProposedCategoryName = CategoryName,
            ProposedBoothNumber = Order.Booth, PaymentScheduleText = PaymentSchedule, ApprovalNeeded = ApprovalNeeded };
        BillingState.Set(row, Billing);
        return row;
    }
}

internal static class HandoffRules
{
    public static IReadOnlyList<DocumentInfo> PreflightAttachments(MomentusGateway gateway, RunRow row)
    {
        var contracts = row.Contracts ?? throw new RecoveryReviewException("REVIEW: contract attachment identity is missing.");
        if (contracts.ReviewMessage.Length > 0 || contracts.ScheduleSource is null) throw new RecoveryReviewException("REVIEW: contract attachment scope is unresolved.");
        var documents = contracts.OrderDocuments.Concat(contracts.Sources).GroupBy(x => x.ContentHash).Select(x => x.First().Document).ToList();
        if (documents.Count == 0) throw new RecoveryReviewException("REVIEW: no applicable contract attachment exists.");
        foreach (var document in documents) _ = gateway.VerifiedDocumentData(document);
        if (gateway.Journal?.Evidence.SendEmail ?? true) _ = ReadyEmailBuilder.Recipients(row);
        return documents;
    }

    public static VerifiedHandoff Verify(MomentusGateway gateway, RunRow row, ProcessingJournal journal)
    {
        _ = PlanGuard.BeforeWrite(gateway, row, "Verify handoff", new object());
        BillingGuard.VerifyFinal(gateway, row);
        var order = gateway.GetOrder(row.OrderNumber);
        if (order.OrderNumber != row.OrderNumber || order.Event != row.EventId ||
            !TextRules.Same(order.BillToAccount, row.EffectiveBilling!.Account.AccountCode) || !TextRules.Same(order.BillToContact, row.EffectiveBilling.Contact.AccountCode) ||
            !TextRules.Same(order.OrderAccountRep, row.ProposedOrderAccountRep) || order.Category != row.ProposedCategory || !TextRules.Same(order.BoothNumber, row.ProposedBoothNumber))
            throw new RecoveryReviewException("REVIEW: handoff order identity/fields disagree with verified processing.");
        var exhibitor = gateway.GetExhibitor(row.ExhibitorId);
        var note = ManagedNoteRules.Resolve(gateway.GetOrderSonNotes(row.OrderNumber), journal.Evidence.Stages);
        if (note.Note is null || note.Action != "OWNED" || ManagedNoteRules.ContentHash(ManagedNoteRules.Text(note.Note)) != ManagedNoteRules.ContentHash(row.PaymentScheduleText))
            throw new RecoveryReviewException("REVIEW: handoff payment schedule is not verified in the assigned managed note.");
        if (!ExhibitorCategoryRules.Parse(exhibitor.ExhibitorCategory).SequenceEqual(ExhibitorCategoryRules.Parse(row.FinalExhibitorCategories)))
            throw new RecoveryReviewException("REVIEW: final exhibitor categories differ from verified processing.");
        RequireOrderStages(journal.Evidence, includeActivation: false, includeEmail: false);
        var expectedAttachments = PreflightAttachments(gateway, row);
        var finalAttachments = gateway.GetOrderContractPdfs(row.OrderNumber).Select(x => gateway.ReadContract(x, "Order", row.OrderNumber).Document)
            .GroupBy(x => x.ContentHash).Select(x => x.First()).ToList();
        if (!expectedAttachments.Select(x => x.ContentHash).Order().SequenceEqual(finalAttachments.Select(x => x.ContentHash).Order()))
            throw new RecoveryReviewException("REVIEW: final order attachment set differs from the validated applicable contracts.");
        var result = new VerifiedHandoff(row.EventId, row.ExhibitorId, row.OrderNumber,
            new[] { exhibitor.CompanyBannerName, exhibitor.CompanyName, exhibitor.AccountCode }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "",
            order.OrderDate, new(order.OrderAccountRep ?? "", order.Category, order.BoothNumber ?? "", order.BillToAccount ?? "", order.BillToContact ?? ""),
            row.DecisionInputs!.Categories.Single(x => x.Sequence == order.Category).Description, row.EffectiveBilling,
            ManagedNoteRules.Text(note.Note), Convert.ToInt32(note.Note.SequenceNumber), ManagedNoteRules.ContentHash(ManagedNoteRules.Text(note.Note)),
            ExhibitorCategoryRules.HasCode(exhibitor.ExhibitorCategory, ExhibitorCategoryRules.ApprovalNeeded), finalAttachments);
        journal.Evidence.Handoff = result;
        journal.Save();
        return result;
    }
    public static void RequireOrderStages(OrderEvidence evidence, bool includeActivation, bool includeEmail = true)
    {
        bool Has(string operation) => evidence.Stages.Any(x => x.Operation == operation && x.Status == StageStatus.Verified);
        if (evidence.Stages.Any(x => x.Operation != "Activate exhibitor" && (includeEmail || x.Operation != "Send ready email") &&
                (includeActivation || x.Operation != "Activate order") && x.Status != StageStatus.Verified) || !Has("Update service order") ||
            evidence.Plan.ManagedNoteSequence is null || evidence.Plan.ManagedNoteContentHash != ManagedNoteRules.ContentHash(evidence.Plan.PaymentScheduleText) ||
            includeEmail && evidence.SendEmail && !Has("Send ready email") ||
            includeActivation && evidence.ActivateOrder && !evidence.Plan.ApprovalNeeded && !Has("Activate order"))
            throw new RecoveryReviewException("REVIEW: required order stages are not all Verified; activation is prohibited.");
    }

    public static void VerifyRetained(MomentusGateway gateway, OrderEvidence evidence, ServiceOrdersModel? current = null)
    {
        RequireOrderStages(evidence, includeActivation: true);
        var handoff = evidence.Handoff ?? throw new RecoveryReviewException("REVIEW: completed order lacks verified handoff evidence.");
        var identity = evidence.Identity;
        var row = evidence.Plan;
        current ??= gateway.GetOrder(identity.Order);
        var committed = handoff.Order;
        var activated = evidence.Stages.Any(x => x.Operation == "Activate order" && x.Status == StageStatus.Verified);
        if (handoff.EventId != identity.Event || handoff.ExhibitorId != identity.Exhibitor || handoff.OrderNumber != identity.Order ||
            current.OrganizationCode != identity.Organization || current.OrderNumber != identity.Order || current.Event != identity.Event || current.Exhibitor != identity.Exhibitor ||
            !TextRules.Same(current.OrderStatus, activated ? "A" : "PC") || current.OrderDate != handoff.OrderDate ||
            !TextRules.Same(current.BillToAccount, committed.BillAccount) || !TextRules.Same(current.BillToContact, committed.BillContact) ||
            !TextRules.Same(current.OrderAccountRep, committed.Rep) || current.Category != committed.Category || !TextRules.Same(current.BoothNumber, committed.Booth) ||
            gateway.GetAccount(handoff.Billing.Account.AccountCode) != handoff.Billing.Account || gateway.GetAccount(handoff.Billing.Contact.AccountCode) != handoff.Billing.Contact)
            throw new RecoveryReviewException("REVIEW: completed order's effective final identity/state differs from its verified handoff.");
        var source = gateway.GetAccountModel(row.BillingSourceAccount);
        if (!TextRules.Same(current.Account, source.AccountCode) || !TextRules.Same(source.AccountCode, row.BillingSourceAccount) ||
            row.BillingConfiguration is null || !row.BillingConfiguration.SameAs(gateway.BillingConfiguration) ||
            MomentusGateway.BillingFrom(source, gateway.BillingConfiguration) != row.RequestedBilling)
            throw new RecoveryReviewException("REVIEW: completed order's billing instructions/configuration changed.");
        var note = ManagedNoteRules.Resolve(gateway.GetOrderSonNotes(identity.Order), evidence.Stages);
        if (note.Action != "OWNED" || note.Note?.SequenceNumber != handoff.NoteSequence || ManagedNoteRules.ContentHash(ManagedNoteRules.Text(note.Note!)) != handoff.NoteHash)
            throw new RecoveryReviewException("REVIEW: completed order's managed payment note differs from its verified handoff.");
        PlanGuard.ValidateContracts(gateway, row, evidence);
        var inputs = row.DecisionInputs ?? throw new RecoveryReviewException("REVIEW: completed order lacks decision evidence.");
        PlanGuard.ValidateRetainedIdentities(current, gateway.GetExhibitor(identity.Exhibitor), inputs);
        if (!gateway.GetOrderItems(identity.Order).OrderBy(x => x.LineNumber).SequenceEqual(inputs.Items.OrderBy(x => x.LineNumber)) ||
            gateway.GetBoothEvidence(identity.Exhibitor, identity.Event) != inputs.Booth)
            throw new RecoveryReviewException("REVIEW: completed order's item/booth evidence changed.");
    }
}

internal sealed record ActivationDecision(bool Allowed, bool AlreadyVerified, string Message, ExhibitorsModel Exhibitor);
internal static class ActivationRules
{
    public static IReadOnlyList<OrderEvidence> Group(IEnumerable<OrderEvidence> records, OrderIdentity scope) => records.Where(x =>
        x.Identity.Endpoint == scope.Endpoint && x.Identity.Organization == scope.Organization && x.Identity.Exhibitor == scope.Exhibitor && x.Identity.Event == scope.Event).ToList();
    public static ActivationDecision Check(MomentusGateway gateway, OrderEvidence owner, IReadOnlyList<OrderEvidence> records)
    {
        IReadOnlyList<ServiceOrdersModel> ReadOrders()
        {
            try { return gateway.GetExhibitorOrders(owner.Identity.Exhibitor, owner.Identity.Event); }
            catch (DecisionSearchException ex) { throw new RecoveryReviewException("REVIEW: complete activation order set cannot be established. " + ex.Message); }
        }
        ExhibitorsModel ReadExhibitor()
        {
            var current = gateway.GetExhibitor(owner.Identity.Exhibitor);
            if (current.OrganizationCode != owner.Identity.Organization || current.ExhibitorID != owner.Identity.Exhibitor || current.Event != owner.Identity.Event)
                throw new RecoveryReviewException("REVIEW: activation exhibitor/organization/event identity conflicts.");
            return current;
        }
        var orders = ReadOrders();
        var exhibitor = ReadExhibitor();
        if (ExhibitorCategoryRules.HasCode(exhibitor.ExhibitorCategory, ExhibitorCategoryRules.Hold) || ExhibitorCategoryRules.HasCode(exhibitor.ExhibitorCategory, ExhibitorCategoryRules.ApprovalNeeded))
            return new(false, false, "Current Hold or Approval Needed blocks exhibitor activation.", exhibitor);
        var group = Group(records, owner.Identity).Where(x => x.Identity != owner.Identity).Append(owner).ToList();
        if (orders.Count == 0 || !orders.Any(x => x.OrderNumber == owner.Identity.Order)) throw new RecoveryReviewException("REVIEW: complete activation order set does not contain the current verified order.");
        foreach (var record in group)
        {
            if (record.Stages.Any(x => x.Operation == "Activate order" && x.Status == StageStatus.Verified) &&
                orders.SingleOrDefault(x => x.OrderNumber == record.Identity.Order)?.OrderStatus != "A")
                throw new RecoveryReviewException("REVIEW: current order status conflicts with its Verified activation.");
        }
        if (orders.Any(x => TextRules.Same(x.OrderStatus, "PC"))) return new(false, false, "A service order remains PC; exhibitor activation deferred.", exhibitor);
        foreach (var record in group)
        {
            var shared = record.Identity == owner.Identity;
            if (!record.OrderComplete || record.Handoff is null || !shared && (!record.Complete || record.Plan.Outcome is "REVIEW" or "FAILED" or "UNKNOWN") ||
                record.Stages.Any(x => (!shared || x.Operation != "Activate exhibitor") && x.Status != StageStatus.Verified))
                return new(false, false, "A journaled order has unresolved/Review/Failed/Unknown processing; exhibitor activation deferred.", exhibitor);
            HandoffRules.RequireOrderStages(record, includeActivation: true);
            var current = orders.SingleOrDefault(x => x.OrderNumber == record.Identity.Order);
            if (current is null) throw new RecoveryReviewException("REVIEW: journaled order is absent from the complete exhibitor/event order set.");
            HandoffRules.VerifyRetained(gateway, record, current);
        }
        // Group verification can take several reads. Stop if the complete order set changed,
        // then use the final exhibitor read as the mutation payload, preserving new categories.
        if (JsonConvert.SerializeObject(orders.OrderBy(x => x.OrderNumber)) != JsonConvert.SerializeObject(ReadOrders().OrderBy(x => x.OrderNumber)))
            throw new RecoveryReviewException("REVIEW: complete activation order set changed during group verification.");
        exhibitor = ReadExhibitor();
        if (ExhibitorCategoryRules.HasCode(exhibitor.ExhibitorCategory, ExhibitorCategoryRules.Hold) || ExhibitorCategoryRules.HasCode(exhibitor.ExhibitorCategory, ExhibitorCategoryRules.ApprovalNeeded))
            return new(false, false, "Current Hold or Approval Needed blocks exhibitor activation.", exhibitor);
        foreach (var record in group) PlanGuard.ValidateRetainedIdentities(orders.Single(x => x.OrderNumber == record.Identity.Order), exhibitor, record.Plan.DecisionInputs!);
        var verified = group.Any(x => x.Stages.Any(s => s.Operation == "Activate exhibitor" && s.Status == StageStatus.Verified));
        if (exhibitor.ExhibitorStatus == 2 && verified) return new(false, true, "Exhibitor activation already Verified by the group journal.", exhibitor);
        if (exhibitor.ExhibitorStatus != 35 || verified) throw new RecoveryReviewException("REVIEW: current exhibitor status conflicts with group activation evidence.");
        return new(true, false, "Every PC and journaled order is resolved; exhibitor activation authorized.", exhibitor);
    }
    public static object BeforeWrite(MomentusGateway gateway, OrderEvidence owner, IReadOnlyList<OrderEvidence> records)
    {
        var result = Check(gateway, owner, records);
        if (!result.Allowed) throw new RecoveryReviewException("REVIEW: " + result.Message);
        result.Exhibitor.ExhibitorStatus = 2;
        return result.Exhibitor;
    }
}

internal static class FailureRules
{
    public static int ExitCode(IEnumerable<RunRow> source)
    {
        var rows = source.ToList();
        if (rows.Any(x => x.Outcome == "UNKNOWN" || x.ServiceOrderUpdateStatus == "UNKNOWN WRITE OUTCOME")) return 3;
        if (rows.Any(x => x.Outcome == "FAILED" || x.ServiceOrderUpdateStatus == "FAILED")) return 1;
        return rows.Any(x => x.Outcome == "REVIEW" || x.ValidationStatus == "REVIEW" || x.ServiceOrderUpdateStatus == "RECOVERY REVIEW" || x.ExhibitorActivationPending) ? 2 : 0;
    }
    public static RunRow Evaluation(Candidate candidate, Exception error) => new()
    {
        EventId = Convert.ToInt32(candidate.Exhibitor.Event), ExhibitorId = Convert.ToInt32(candidate.Exhibitor.ExhibitorID), OrderNumber = Convert.ToInt32(candidate.Order.OrderNumber),
        ExhibitorName = candidate.Exhibitor.CompanyName ?? "", Outcome = error is RecoveryReviewException ? "REVIEW" : "FAILED",
        ValidationStatus = error is RecoveryReviewException ? "REVIEW" : "FAILED", ServiceOrderUpdateStatus = error is RecoveryReviewException ? "REVIEW" : "FAILED",
        ValidationMessage = $"Evaluation failed: {error.Message}", UpdateMessage = error.Message
    };
}
