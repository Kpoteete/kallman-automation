using Newtonsoft.Json;
using Ungerboeck.Api.Models.Subjects;

namespace ServiceOrderEntry;

internal sealed partial class Runner
{
    private static bool CanContinuePartial(OrderEvidence evidence) => !evidence.OrderComplete &&
        evidence.Stages.All(x => x.Status == StageStatus.Verified && x.Operation is not ("Activate order" or "Activate exhibitor" or "Send ready email"));

    internal void ApplyPartial(MomentusGateway gateway, RunRow row)
    {
        journalStore ??= new FileJournalStore(options.StateFolder);
        var identity = OrderIdentity.From(options, row);
        var evidence = journalStore.Load().SingleOrDefault(x => x.Identity == identity)
            ?? new OrderEvidence { Identity = identity, CreatedRunId = runId };
        if (evidence.OrderComplete || evidence.Stages.Any(x => x.Operation is "Activate order" or "Activate exhibitor" or "Send ready email"))
            throw new RecoveryReviewException("REVIEW: completed or activated work cannot become a partial pending order.");
        if (evidence.Stages.Any(x => x.Status is StageStatus.Dispatching or StageStatus.Unknown or StageStatus.Succeeded))
            throw new RecoveryReviewException("REVIEW: unresolved writes prohibit partial processing.");
        evidence.Plan = row;
        // A partial order never qualifies for activation or the Finance completion email.
        evidence.SendEmail = false; evidence.ActivateOrder = false; evidence.ActivateExhibitor = false;
        evidence.Complete = false; evidence.OrderComplete = false;
        var journal = new ProcessingJournal(journalStore, evidence, runId);
        row.ProcessingId = evidence.ProcessingId; row.RunId = runId;
        row.OrderStatusAction = "NO CHANGE (REMAINS PC)";
        row.ExhibitorStatusAction = "NO CHANGE (REMAINS 35)";
        row.ReadyEmailStatus = "WITHHELD (INCOMPLETE ORDER)";
        gateway.Journal = journal;
        var storageFailed = false;
        bool Unresolved() => evidence.Stages.Any(x => x.Status is StageStatus.Dispatching or StageStatus.Unknown or StageStatus.Succeeded);
        void Step(string component, Action action)
        {
            try
            {
                _ = PartialGuard(gateway, row, "Check partial eligibility", new object());
                action(); row.PartialWork.Add(component + " verified"); journal.Save();
            }
            catch (JournalStorageException) { throw; }
            catch (ApplyLimitException) { throw; }
            catch (UnknownWriteOutcomeException) { throw; }
            catch (Exception ex)
            {
                if (Unresolved()) throw new UnknownWriteOutcomeException(component, $"order={row.OrderNumber}", ex);
                row.Issues.Add(new(component, ex.Message, IssueValues(row, component), ex is not RecoveryReviewException));
                journal.Save();
            }
        }
        try
        {
            journal.Save();
            gateway.MutationGuard = (operation, intent) => PartialGuard(gateway, row, operation, intent);
            // Eligibility is a record-wide prerequisite. A stale field blocks its own stage;
            // a changed status, identity or Hold blocks every stage.
            _ = PartialGuard(gateway, row, "Check partial eligibility", new object());
            if (row.RepResolved || row.CategoryResolved || row.BoothResolved) Step("Order fields", () =>
            {
                var order = gateway.GetOrder(row.OrderNumber);
                var original = PlanGuard.Fields(order);
                order = (ServiceOrdersModel)PartialGuard(gateway, row, "Update partial order", order);
                if (PlanGuard.Fields(order) != original) gateway.UpdatePartialOrder(order, billing: false);
            });
            if (row.BillingResolved) Step("Bill-To", () =>
            {
                BillingGuard.Preflight(gateway, row, gateway.GetOrder(row.OrderNumber), journal, validateWholePlan: false);
                CompleteBilling(gateway, row, journal);
                var order = gateway.GetOrder(row.OrderNumber);
                order.BillToAccount = row.FinalBillToAccount; order.BillToContact = row.FinalBillToContact;
                gateway.UpdatePartialOrder(order, billing: true);
                BillingGuard.VerifyFinal(gateway, row);
            });
            if (row.ContractsResolved) Step("Contracts", () =>
            {
                PlanGuard.ValidateContracts(gateway, row);
                var copied = gateway.EnsureContractCopies(gateway.GetOrder(row.OrderNumber), row);
                row.ContractPdfCopyStatus = copied == 0 ? "ALREADY COPIED OR NONE FOUND" : $"COPIED {copied}";
            });
            if (row.ContractsResolved && row.NoteResolved) Step("Payment note", () =>
            {
                PlanGuard.ValidateContracts(gateway, row);
                row.PaymentScheduleNoteStatus = gateway.SavePaymentScheduleNote(row.OrderNumber, row.PaymentScheduleText);
            });
            if (row.CategoryResolved) Step("Category", () =>
            {
                var exhibitor = gateway.GetExhibitor(row.ExhibitorId);
                var original = exhibitor.ExhibitorCategory;
                exhibitor = (ExhibitorsModel)PartialGuard(gateway, row, "Update partial exhibitor categories", exhibitor);
                if (!TextRules.Same(original, row.FinalExhibitorCategories))
                {
                    exhibitor.ExhibitorCategory = row.FinalExhibitorCategories;
                    gateway.UpdatePartialExhibitor(exhibitor);
                }
            });
            row.Outcome = row.Issues.Any(x => x.Failed) ? "FAILED" : "REVIEW";
            row.ServiceOrderUpdateStatus = evidence.Stages.Any(x => x.Status == StageStatus.Verified) ? "PARTIAL (REMAINS PC)" : "NOT ATTEMPTED";
            row.UpdateMessage = "Incomplete order retained at PC; exhibitor retained at 35. " + string.Join(" ", row.PartialWork);
        }
        catch (ApplyLimitException ex)
        {
            row.Outcome = "REVIEW"; row.ServiceOrderUpdateStatus = "DEFERRED (ATTEMPT CAP)"; row.UpdateMessage = ex.Message;
            row.Issues.Add(new("Run limit", ex.Message, $"order={row.OrderNumber}"));
        }
        catch (UnknownWriteOutcomeException ex)
        {
            row.Outcome = "UNKNOWN"; row.ServiceOrderUpdateStatus = "UNKNOWN WRITE OUTCOME"; row.UpdateMessage = ex.Message;
            row.Issues.Add(new("Write outcome", ex.Message, $"order={row.OrderNumber}"));
        }
        catch (JournalStorageException) { storageFailed = true; throw; }
        catch (Exception ex)
        {
            row.Outcome = Unresolved() ? "UNKNOWN" : ex is RecoveryReviewException ? "REVIEW" : "FAILED";
            row.ServiceOrderUpdateStatus = row.Outcome == "UNKNOWN" ? "UNKNOWN WRITE OUTCOME" : "NOT ATTEMPTED";
            row.UpdateMessage = ex.Message; row.Issues.Add(new("Eligibility", ex.Message, $"order={row.OrderNumber}"));
        }
        finally
        {
            row.ValidationStatus = "REVIEW";
            row.ValidationMessage = string.Join(" ", row.Issues.Select(x => x.Description));
            if (!storageFailed) journal.Save();
            row.JournalStages = StageSummary(evidence);
            gateway.MutationGuard = null; gateway.Journal = null;
        }
    }

    private static object PartialGuard(MomentusGateway gateway, RunRow row, string operation, object intent)
    {
        var inputs = row.DecisionInputs ?? throw new RecoveryReviewException("REVIEW: partial plan lacks decision evidence.");
        var evidence = gateway.Journal!.Evidence;
        var order = gateway.GetOrder(row.OrderNumber);
        var exhibitor = gateway.GetExhibitor(row.ExhibitorId);
        PlanGuard.ValidateRetainedIdentities(order, exhibitor, inputs);
        if (!TextRules.Same(order.OrganizationCode, evidence.Identity.Organization) || order.OrderNumber != row.OrderNumber ||
            order.Event != row.EventId || order.Exhibitor != row.ExhibitorId ||
            !TextRules.Same(exhibitor.OrganizationCode, evidence.Identity.Organization) || exhibitor.ExhibitorID != row.ExhibitorId ||
            exhibitor.Event != row.EventId || exhibitor.ExhibitorType != "ME" ||
            !TextRules.Same(order.OrderStatus, "PC") || Value(exhibitor.ExhibitorStatus) != 35 ||
            ExhibitorCategoryRules.HasCode(exhibitor.ExhibitorCategory, ExhibitorCategoryRules.Hold))
            throw new RecoveryReviewException("REVIEW: partial work requires the original eligible PC order and main exhibitor at 35 without Hold.");
        if (operation == "Check partial eligibility") return intent;
        var last = evidence.Stages.LastOrDefault(x => x.Status == StageStatus.Verified &&
            x.Operation is "Update partial order" or "Assign partial billing" or "Update service order");
        var expected = last is null ? inputs.OrderFields : PlanGuard.Fields(JsonConvert.DeserializeObject<ServiceOrdersModel>(last.Intent)!);
        if (PlanGuard.Fields(order) != expected) throw new RecoveryReviewException("REVIEW: order fields changed since evaluation or verified write.");

        if (operation == "Update partial order")
        {
            if (row.RepResolved)
            {
                gateway.VerifyOrderAccountRepIdentity(row.ProposedOrderAccountRep);
                var rep = SalesRepRules.Resolve(MomentusGateway.ExhibitorSalesRepUdf(exhibitor), exhibitor.Salesperson, row.PartialSalesReps);
                if (rep != row.ProposedOrderAccountRep) throw new RecoveryReviewException("REVIEW: salesperson evidence changed.");
                order.OrderAccountRep = row.ProposedOrderAccountRep;
            }
            if (row.CategoryResolved) { ValidatePartialCategory(gateway, row); order.Category = row.ProposedCategory; }
            if (row.BoothResolved)
            {
                PlanGuard.ValidateBooth(gateway, row, order);
                if (!inputs.Booth.UsesExistingOrderBooth) order.BoothNumber = row.ProposedBoothNumber;
            }
            return order;
        }
        if (operation is "Copy contract document" or "Add payment schedule note" or "Update payment schedule note")
        {
            PlanGuard.ValidateContracts(gateway, row);
            return intent;
        }
        if (operation == "Update partial exhibitor categories")
        {
            var items = ValidatePartialCategory(gateway, row);
            var expectedCategories = evidence.Stages.Any(x => x.Operation == operation && x.Status == StageStatus.Verified)
                ? row.FinalExhibitorCategories : row.ExistingExhibitorCategories;
            if (!ExhibitorCategoryRules.Parse(exhibitor.ExhibitorCategory).Where(ExhibitorCategoryRules.ManagedCodes.Contains)
                .SequenceEqual(ExhibitorCategoryRules.Parse(expectedCategories).Where(ExhibitorCategoryRules.ManagedCodes.Contains)))
                throw new RecoveryReviewException("REVIEW: managed exhibitor categories changed.");
            var categories = ExhibitorCategoryRules.Resolve(exhibitor.ExhibitorCategory, row.ProposedCategoryName, items, MomentusGateway.ExhibitorStatePavilionUdf(exhibitor));
            row.FinalExhibitorCategories = ExhibitorCategoryRules.Format(categories.Final);
            exhibitor.ExhibitorCategory = row.FinalExhibitorCategories;
            return exhibitor;
        }
        if (operation is "Create organization account" or "Create contact" or "Update billing contact" or "Add relationship" or "Assign partial billing")
        {
            BillingGuard.Instructions(gateway, row);
            if (!row.EffectiveBilling!.AccountCreationPending) BillingGuard.VerifyReusedAccount(gateway, row);
            if (!row.EffectiveBilling.ContactCreationPending) BillingGuard.VerifyReusedContact(gateway, row);
            if (operation == "Assign partial billing")
            {
                BillingGuard.VerifyFinal(gateway, row);
                order.BillToAccount = row.FinalBillToAccount; order.BillToContact = row.FinalBillToContact;
                return order;
            }
            if (operation == "Update billing contact" && intent is AllAccountsModel desired)
            {
                var contact = gateway.GetAccountModel(row.FinalBillToContact);
                if (MomentusGateway.ToAccount(contact) != row.EffectiveBilling.Contact)
                    throw new RecoveryReviewException("REVIEW: billing contact changed before update.");
                contact.FirstName = desired.FirstName; contact.LastName = desired.LastName;
                if (row.EffectiveBilling.ContactAddressRequired) BillingState.SetContactAddress(contact, row);
                return contact;
            }
            return intent;
        }
        throw new RecoveryReviewException($"REVIEW: partial processing cannot dispatch {operation}.");
    }

    private static IReadOnlyList<OrderItemInfo> ValidatePartialCategory(MomentusGateway gateway, RunRow row)
    {
        var items = gateway.GetOrderItems(row.OrderNumber);
        var category = CategoryRules.Resolve(items, row.DecisionInputs!.Categories);
        if (category.Sequence != row.ProposedCategory || category.Description != row.ProposedCategoryName)
            throw new RecoveryReviewException("REVIEW: category evidence changed.");
        return items;
    }
}
