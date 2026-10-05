using Newtonsoft.Json;
using Ungerboeck.Api.Models.Subjects;

namespace ServiceOrderEntry;

internal sealed record BoothEvidence(string Booth, string Hash);
internal sealed record OrderFields(string Rep, int? Category, string Booth, string BillAccount, string BillContact);
internal sealed record DecisionInputs(string OrderIdentity, OrderFields OrderFields, string ExhibitorIdentity, string ManagedCategories,
    bool ApprovalNeeded, IReadOnlyList<OrderItemInfo> Items, BoothEvidence Booth, IReadOnlyList<CategoryLookup> Categories);

internal static class PlanGuard
{
    static string OrderIdentity(ServiceOrdersModel order) => JsonConvert.SerializeObject(new
    { order.OrganizationCode, order.OrderNumber, order.Event, order.Exhibitor, order.Function, order.Account, order.OrderDate, order.Currency });
    static string ExhibitorIdentity(ExhibitorsModel exhibitor) => JsonConvert.SerializeObject(new
    { exhibitor.OrganizationCode, exhibitor.ExhibitorID, exhibitor.Event, exhibitor.ExhibitorType, exhibitor.AccountCode,
        exhibitor.CompanyName, exhibitor.CompanyBannerName, exhibitor.Salesperson,
        SalesRep = MomentusGateway.ExhibitorSalesRepUdf(exhibitor), StatePavilion = MomentusGateway.ExhibitorStatePavilionUdf(exhibitor) });
    static OrderFields Fields(ServiceOrdersModel order) => new(order.OrderAccountRep ?? "", order.Category, order.BoothNumber ?? "", order.BillToAccount ?? "", order.BillToContact ?? "");
    static string Managed(string? value) => ExhibitorCategoryRules.Format(ExhibitorCategoryRules.Parse(value).Where(ExhibitorCategoryRules.ManagedCodes.Contains));
    public static DecisionInputs Capture(ServiceOrdersModel order, ExhibitorsModel exhibitor, IReadOnlyList<OrderItemInfo> items,
        BoothEvidence booth, IReadOnlyList<CategoryLookup> categories) => new(OrderIdentity(order), Fields(order), ExhibitorIdentity(exhibitor),
            Managed(exhibitor.ExhibitorCategory), ExhibitorCategoryRules.HasCode(exhibitor.ExhibitorCategory, ExhibitorCategoryRules.ApprovalNeeded), items, booth, categories);
    static void Require(bool condition, string input)
    {
        if (!condition) throw new RecoveryReviewException($"REVIEW: {input} changed or is unresolved; stale plan stopped before mutation.");
    }

    internal static void ValidateRetainedIdentities(ServiceOrdersModel order, ExhibitorsModel exhibitor, DecisionInputs inputs) =>
        Require(OrderIdentity(order) == inputs.OrderIdentity && ExhibitorIdentity(exhibitor) == inputs.ExhibitorIdentity, "completed order/exhibitor decision identity");

    public static object BeforeWrite(MomentusGateway gateway, RunRow row, string operation, object intent)
    {
        var inputs = row.DecisionInputs ?? throw new RecoveryReviewException("REVIEW: saved plan lacks fresh-input evidence; re-evaluation required.");
        var evidence = gateway.Journal!.Evidence;
        bool Verified(string op) => evidence.Stages.Any(x => x.Operation == op && x.Status == StageStatus.Verified);
        BillingGuard.Instructions(gateway, row);
        if (!row.EffectiveBilling!.AccountCreationPending) BillingGuard.VerifyReusedAccount(gateway, row);
        if (!row.EffectiveBilling.ContactCreationPending) BillingGuard.VerifyReusedContact(gateway, row);
        var items = gateway.GetOrderItems(row.OrderNumber);
        Require(items.OrderBy(x => x.LineNumber).SequenceEqual(inputs.Items.OrderBy(x => x.LineNumber)), "service-order items");
        var category = CategoryRules.Resolve(items, inputs.Categories);
        Require(category.Sequence == row.ProposedCategory && category.Description == row.ProposedCategoryName, "order category evidence");
        Require(gateway.GetBoothEvidence(row.ExhibitorId, row.EventId) == inputs.Booth && inputs.Booth.Booth == row.ProposedBoothNumber, "booth/activity evidence");
        ValidateContracts(gateway, row);
        ValidateNote(gateway, row);
        // These reads are last so the payloads preserve the latest unrelated fields and categories.
        var order = gateway.GetOrder(row.OrderNumber);
        var exhibitor = gateway.GetExhibitor(row.ExhibitorId);
        Require(OrderIdentity(order) == inputs.OrderIdentity && ExhibitorIdentity(exhibitor) == inputs.ExhibitorIdentity, "order/exhibitor decision identity");
        Require(TextRules.Same(order.OrderStatus, Verified("Activate order") ? "A" : "PC") &&
            Convert.ToInt32(exhibitor.ExhibitorStatus) == (Verified("Activate exhibitor") ? 2 : 35), "order/exhibitor status");
        Require(!ExhibitorCategoryRules.HasCode(exhibitor.ExhibitorCategory, ExhibitorCategoryRules.Hold), "Hold");
        Require(ExhibitorCategoryRules.HasCode(exhibitor.ExhibitorCategory, ExhibitorCategoryRules.ApprovalNeeded) == inputs.ApprovalNeeded, "Approval Needed");
        var updated = evidence.Stages.LastOrDefault(x => x.Operation == "Update service order" && x.Status == StageStatus.Verified);
        var expectedFields = updated is null ? inputs.OrderFields : Fields(JsonConvert.DeserializeObject<ServiceOrdersModel>(updated.Intent)!);
        Require(Fields(order) == expectedFields, "current order category/rep/booth/billing fields");
        var expectedManaged = Verified("Update exhibitor categories") ? Managed(row.FinalExhibitorCategories) : inputs.ManagedCategories;
        Require(Managed(exhibitor.ExhibitorCategory) == expectedManaged, "managed exhibitor categories");
        var categories = ExhibitorCategoryRules.Resolve(exhibitor.ExhibitorCategory, category.Description, items, MomentusGateway.ExhibitorStatePavilionUdf(exhibitor));
        row.FinalExhibitorCategories = ExhibitorCategoryRules.Format(categories.Final);
        row.ExhibitorCategoriesToAdd = ExhibitorCategoryRules.Format(categories.Add);
        row.ExhibitorCategoriesToRemove = ExhibitorCategoryRules.Format(categories.Remove);
        row.ApprovalNeeded = categories.ApprovalNeeded;
        gateway.Journal.Save();
        if (intent is ServiceOrdersModel desiredOrder)
        {
            if (operation == "Activate order") order.OrderStatus = desiredOrder.OrderStatus;
            else
            {
                order.OrderAccountRep = row.ProposedOrderAccountRep; order.Category = row.ProposedCategory;
                order.BoothNumber = row.ProposedBoothNumber; order.BillToAccount = row.FinalBillToAccount; order.BillToContact = row.FinalBillToContact;
            }
            return order;
        }
        if (intent is ExhibitorsModel desiredExhibitor)
        {
            if (operation == "Activate exhibitor") exhibitor.ExhibitorStatus = desiredExhibitor.ExhibitorStatus;
            else exhibitor.ExhibitorCategory = row.FinalExhibitorCategories;
            return exhibitor;
        }
        return intent;
    }

    static string ContractKey(ContractSnapshot value) => JsonConvert.SerializeObject(new
    { value.Organization, value.Owner, value.OwnerId, value.Document.Type, value.Document.SequenceNumber, value.Document.DocumentId,
        value.Document.Description, value.Document.Category, value.Document.EnteredOn, value.ContentHash });
    static bool SameInventory(IEnumerable<ContractSnapshot> expected, IEnumerable<ContractSnapshot> current) =>
        expected.Select(ContractKey).Order(StringComparer.Ordinal).SequenceEqual(current.Select(ContractKey).Order(StringComparer.Ordinal));
    internal static void ValidateContracts(MomentusGateway gateway, RunRow row, OrderEvidence? evidence = null)
    {
        var plan = row.Contracts!;
        Require(plan is not null && plan.ReviewMessage.Length == 0 && plan.ScheduleSource is not null, "selected contract");
        var normal = gateway.GetExhibitorContractPdfs(row.ExhibitorId);
        Require(!plan!.NearbyFallback || normal.Count == 0, "fallback contract scope");
        var sources = (plan.NearbyFallback ? gateway.GetNearbyExhibitorPdfs(row.ExhibitorId, row.OrderDate) : normal)
            .Select(x => gateway.ReadContract(x, plan.NearbyFallback ? "Nearby" : "Exhibitor", row.ExhibitorId))
            .Where(x => !plan.NearbyFallback || x.PaymentSchedule.Length > 0).ToList();
        Require(SameInventory(plan.Sources, sources), "source contract inventory/content");
        var current = gateway.GetOrderContractPdfs(row.OrderNumber).Select(x => gateway.ReadContract(x, "Order", row.OrderNumber)).ToList();
        var originalIds = plan.OrderDocuments.Select(x => (x.Document.Type, x.Document.SequenceNumber)).ToHashSet();
        Require(SameInventory(plan.OrderDocuments, current.Where(x => originalIds.Contains((x.Document.Type, x.Document.SequenceNumber)))), "existing order contract inventory/content");
        var extras = current.Where(x => !originalIds.Contains((x.Document.Type, x.Document.SequenceNumber))).ToList();
        var copies = (evidence ?? gateway.Journal!.Evidence).Stages.Where(x => x.Operation == "Copy contract document" && x.Status == StageStatus.Verified).ToList();
        Require(extras.Count == copies.Count && extras.All(x => copies.Any(c =>
            c.Source.GetValueOrDefault("destinationType") == x.Document.Type && c.Source.GetValueOrDefault("destinationSequence") == x.Document.SequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            c.Source.GetValueOrDefault("destinationHash") == x.ContentHash)), "order contract copies");
        var selection = ContractRules.Select(sources, current, plan.NearbyFallback);
        Require(selection.ReviewMessage.Length == 0 && ManagedNoteRules.ContentHash(selection.PaymentSchedule) == ManagedNoteRules.ContentHash(row.PaymentScheduleText) &&
            ManagedNoteRules.ContentHash(plan.PaymentSchedule) == ManagedNoteRules.ContentHash(row.PaymentScheduleText), "payment schedule");
        var selected = sources.Concat(current).SingleOrDefault(x => x.Owner == plan.ScheduleSource!.Owner && x.OwnerId == plan.ScheduleSource.OwnerId &&
            x.Document.Type == plan.ScheduleSource.Document.Type && x.Document.SequenceNumber == plan.ScheduleSource.Document.SequenceNumber);
        Require(selected is not null && selected.ContentHash == plan.ScheduleSource!.ContentHash, "payment schedule source");
    }
    static void ValidateNote(MomentusGateway gateway, RunRow row)
    {
        var stages = gateway.Journal!.Evidence.Stages;
        var decision = ManagedNoteRules.Resolve(gateway.GetOrderSonNotes(row.OrderNumber), stages);
        Require(decision.Action != "REVIEW", "managed note ownership");
        var verified = stages.LastOrDefault(x => x.Status == StageStatus.Verified && x.Operation is "Add payment schedule note" or "Update payment schedule note");
        var expected = verified is null ? row.ManagedNoteSequence : Convert.ToInt32(JsonConvert.DeserializeObject<NotesModel>(verified.Result)!.SequenceNumber);
        var hash = verified is null ? row.ManagedNoteContentHash : verified.Source.GetValueOrDefault("noteHash", "");
        Require(decision.Note is null ? expected is null : expected == Convert.ToInt32(decision.Note.SequenceNumber) &&
            hash == ManagedNoteRules.ContentHash(ManagedNoteRules.Text(decision.Note)), "managed note identity/content");
    }
}
