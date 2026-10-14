using Ungerboeck.Api.Models.Subjects;

namespace ServiceOrderEntry;

internal sealed partial class Runner
{
    private static RunRow EvaluateComponents(MomentusGateway gateway, Candidate candidate,
        IReadOnlyList<SalesRepLookup> reps, IReadOnlyList<CategoryLookup> categories)
    {
        var order = candidate.Order;
        var exhibitor = candidate.Exhibitor;
        var udfRep = MomentusGateway.ExhibitorSalesRepUdf(exhibitor);
        var row = new RunRow
        {
            ComponentEvaluation = true, PartialSalesReps = reps.ToList(), EventId = Value(order.Event), ExhibitorId = Value(exhibitor.ExhibitorID),
            OrderNumber = Value(order.OrderNumber), OrderDate = DateValue(order.OrderDate),
            ExhibitorName = First(exhibitor.CompanyBannerName, exhibitor.CompanyName, exhibitor.AccountCode),
            BillingSourceAccount = order.Account ?? "", BillingConfiguration = gateway.BillingConfiguration,
            ValidCategoryIds = categories.Select(x => x.Sequence).ToList(),
            ExhibitorSalesRep = First(udfRep, exhibitor.Salesperson),
            ReadyEmailRecipient = ReadyEmailBuilder.FinanceRecipients, ReadyEmailCcRecipient = ReadyEmailBuilder.FinanceCcRecipient,
            ReadyEmailStatus = "WOULD SEND"
        };
        IReadOnlyList<OrderItemInfo> items = [];
        var itemsComplete = false;
        var booth = new BoothEvidence("", "");
        void Check(string component, Action action)
        {
            try { action(); }
            catch (JournalStorageException) { throw; }
            catch (Exception ex)
            {
                row.Issues.Add(new(component, ex.Message, IssueValues(row, component), ex is not RecoveryReviewException));
            }
        }
        void Review(string component, string reason) => row.Issues.Add(new(component, reason, IssueValues(row, component)));

        Check("Salesperson", () =>
        {
            row.ProposedOrderAccountRep = SalesRepRules.Resolve(udfRep, exhibitor.Salesperson, reps);
            if (!BillingRules.ValidCode(row.ProposedOrderAccountRep)) { Review("Salesperson", "A unique salesperson could not be found in the lookup."); return; }
            gateway.VerifyOrderAccountRepIdentity(row.ProposedOrderAccountRep);
            row.RepResolved = true;
        });
        Check("Category", () =>
        {
            items = gateway.GetOrderItems(row.OrderNumber);
            itemsComplete = true;
            var category = CategoryRules.Resolve(items, categories);
            row.ServiceOrderItemIdentifier = category.ItemIdentifier.Length > 0 ? category.ItemIdentifier : string.Join(" | ", items.Select(x => x.SearchText));
            row.ProposedCategory = category.Sequence; row.ProposedCategoryName = category.Description;
            row.CategoryResolved = category.Sequence is > 0 && row.ValidCategoryIds.Contains(category.Sequence.Value);
            if (!row.CategoryResolved) { Review("Category", category.Message); return; }
            var decision = ExhibitorCategoryRules.Resolve(exhibitor.ExhibitorCategory, category.Description, items, MomentusGateway.ExhibitorStatePavilionUdf(exhibitor));
            row.ExistingExhibitorCategories = ExhibitorCategoryRules.Format(decision.Existing);
            row.ExhibitorCategoriesToAdd = ExhibitorCategoryRules.Format(decision.Add);
            row.ExhibitorCategoriesToRemove = ExhibitorCategoryRules.Format(decision.Remove);
            row.FinalExhibitorCategories = ExhibitorCategoryRules.Format(decision.Final); row.ApprovalNeeded = decision.ApprovalNeeded;
        });
        var marketCorrection = UsappMarketRules.Correction(exhibitor, itemsComplete ? items : null);
        if (marketCorrection is not null) row.ManualCorrections.Add(marketCorrection);
        Check("Booth", () =>
        {
            booth = !string.IsNullOrWhiteSpace(order.BoothNumber)
                ? new BoothEvidence(order.BoothNumber, "") { UsesExistingOrderBooth = true }
                : gateway.GetBoothEvidence(row.ExhibitorId, row.EventId);
            row.ProposedBoothNumber = booth.Booth; row.BoothResolved = !string.IsNullOrWhiteSpace(booth.Booth);
            if (!row.BoothResolved) Review("Booth", "No unique accepted booth number could be found.");
        });
        Check("Bill-To", () =>
        {
            row.RequestedBilling = MomentusGateway.BillingFrom(gateway.GetAccountModel(row.BillingSourceAccount), gateway.BillingConfiguration);
            row.ExistingBillToAccount = gateway.GetAccount(order.BillToAccount ?? "");
            row.ExistingBillToContact = gateway.GetAccount(order.BillToContact ?? "");
            DecideAccountAndAddress(gateway, row);
            DecideContact(gateway, row);
            var errors = BillingRules.RequestedErrors(row.RequestedBilling, gateway.BillingConfiguration)
                .Concat(new[] { row.AccountDecisionMessage, row.ContactDecisionMessage }.Where(x => x.Length > 0))
                .Concat(BillingRules.EffectiveErrors(row)).Distinct().ToList();
            row.BillingResolved = errors.Count == 0 && row.BillToAddressAction != "REVIEW" && row.ContactAction != "REVIEW";
            if (!row.BillingResolved) Review("Bill-To", string.Join(" ", errors));
        });
        Check("Contracts", () =>
        {
            row.Contracts = gateway.ResolveContracts(row.ExhibitorId, row.OrderNumber, row.OrderDate);
            row.ContractsResolved = row.Contracts.HasResolvedSchedule;
            row.PaymentScheduleText = row.Contracts.PaymentSchedule;
            row.ContractPdfCount = row.Contracts.Sources.Count;
            row.ContractPdfCopiesNeeded = row.Contracts.Sources.GroupBy(x => x.ContentHash)
                .Count(g => !row.Contracts.OrderDocuments.Any(x => x.ContentHash == g.Key));
            row.ContractPdfCopyStatus = row.ContractPdfCopiesNeeded > 0 ? "WOULD COPY" : "ALREADY COPIED OR NONE FOUND";
            row.PaymentScheduleDecisionMessage = row.Contracts.ReviewMessage;
            if (!row.ContractsResolved) Review("Contracts", row.Contracts.ReviewMessage);
        });
        if (row.ContractsResolved) Check("Payment note", () =>
        {
            var decision = ManagedNoteRules.Resolve(gateway.GetOrderSonNotes(row.OrderNumber), gateway.Journal?.Evidence.Stages, row.PaymentScheduleText);
            row.ManagedNoteSequence = decision.Note is null ? null : Value(decision.Note.SequenceNumber);
            row.ManagedNoteContentHash = decision.Note is null ? "" : ManagedNoteRules.ContentHash(ManagedNoteRules.Text(decision.Note));
            row.PaymentScheduleNoteAction = decision.Action is "OWNED" or "CURRENT MATCH"
                ? (row.ManagedNoteContentHash == ManagedNoteRules.ContentHash(row.PaymentScheduleText) ? "KEEP EXISTING" : "UPDATE") : decision.Action;
            row.NoteResolved = decision.Action != "REVIEW";
            if (!row.NoteResolved) Review("Payment note", decision.Message);
        });
        if (gateway.SendReadyEmail && row.RepResolved) Check("Finance email", () =>
        {
            row.OrderAccountRepEmail = gateway.GetOrderAccountRepEmail(row.ProposedOrderAccountRep);
            row.ReadyEmailCcRecipient = ReadyEmailBuilder.FinanceCcWithEmployee(row.OrderAccountRepEmail);
        });
        row.DecisionInputs = PlanGuard.Capture(order, exhibitor, items, booth, categories);
        var validation = ValidationRules.Validate(row);
        row.ValidationStatus = row.Issues.Count > 0 ? "REVIEW" : validation.Status;
        row.ValidationMessage = string.Join(" ", row.Issues.Select(x => x.Description).Append(validation.Message).Where(x => x.Length > 0));
        if (row.Issues.Any(x => x.Failed)) row.Outcome = "FAILED";
        return row;
    }

    internal static string IssueValues(RunRow row, string component) => component switch
    {
        "Bill-To" => $"company='{row.RequestedBilling.CompanyName}', selector='{row.RequestedBilling.UseRequestedAddress}', street='{row.RequestedBilling.Address}', city='{row.RequestedBilling.City}', state='{row.RequestedBilling.State}', postal='{row.RequestedBilling.PostalCode}', country='{row.RequestedBilling.Country}', contact='{row.RequestedBilling.FirstName} {row.RequestedBilling.LastName}', email='{row.RequestedBilling.Email}', existing account='{row.ExistingBillToAccount.AccountCode}', existing address='{row.ExistingBillToAccount.Address}, {row.ExistingBillToAccount.City}, {row.ExistingBillToAccount.State}, {row.ExistingBillToAccount.PostalCode}, {row.ExistingBillToAccount.Country}'",
        "Salesperson" or "Finance email" => $"submitted salesperson='{row.ExhibitorSalesRep}', resolved account='{row.ProposedOrderAccountRep}', rep email='{row.OrderAccountRepEmail}'",
        "Category" => $"item='{row.ServiceOrderItemIdentifier}', category='{row.ProposedCategoryName}'",
        "Booth" => $"proposed booth='{row.ProposedBoothNumber}', existing booth='{row.DecisionInputs?.OrderFields.Booth}'",
        _ => $"order={row.OrderNumber}, exhibitor={row.ExhibitorId}, event={row.EventId}"
    };
}
