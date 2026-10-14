using Ungerboeck.Api.Models.Subjects;

namespace ServiceOrderEntry;

internal sealed partial class Runner
{
    private static void CompleteBilling(MomentusGateway gateway, RunRow row, ProcessingJournal journal)
    {
        var evidence = journal.Evidence;
        bool Verified(string operation) => evidence.Stages.Any(x => x.Operation == operation && x.Status == StageStatus.Verified);
        var originalBillToAccount = row.ExistingBillToAccount.AccountCode;
        if (row.BillToAddressAction == "CREATE RELATED BILL-TO ACCOUNT")
        {
            var proven = journal.CreatedAccount("Create organization account");
            var recheck = proven is null ? gateway.FindOrganizationCandidates(row.RequestedBilling) : [];
            if (recheck.Count > 0) throw new RecoveryReviewException("REVIEW: account creation stopped because one or multiple duplicate candidates were found; re-evaluate billing.");
            BillingGuard.Instructions(gateway, row);
            row.FinalBillToAccount = proven ?? gateway.CreateOrganizationAccount(row.RequestedBilling);
            evidence.Plan.FinalBillToAccount = row.FinalBillToAccount;
            journal.Save();
            BillingGuard.VerifyCreatedAccount(gateway, row, row.FinalBillToAccount);
            journal.Save();
            gateway.VerifyUndispatched("Create organization account");
            BillingGuard.Instructions(gateway, row);
            gateway.EnsureRelationship(originalBillToAccount, row.FinalBillToAccount, "BTO");
        }
        else if (row.BillToAddressAction == "USE MATCHED BILL-TO ACCOUNT")
        {
            BillingGuard.Instructions(gateway, row);
            BillingGuard.VerifyReusedAccount(gateway, row);
            gateway.EnsureRelationship(originalBillToAccount, row.FinalBillToAccount, "BTO");
        }

        if (row.ContactAction == "CREATE CONTACT")
        {
            if (string.IsNullOrWhiteSpace(row.RequestedBilling.FirstName) || string.IsNullOrWhiteSpace(row.RequestedBilling.LastName))
                throw new InvalidOperationException("Contact creation requires requested first and last name.");
            var proven = journal.CreatedAccount("Create contact");
            if (proven is null && gateway.HasGlobalContactEmail(row.RequestedBilling.Email))
                throw new RecoveryReviewException("REVIEW: contact creation stopped because existing contacts use the requested email; re-evaluate identity.");
            var finalCompany = gateway.GetAccount(row.FinalBillToAccount).Company;
            BillingGuard.Instructions(gateway, row);
            BillingGuard.VerifyReusedAccount(gateway, row);
            row.FinalBillToContact = proven ?? gateway.CreateContact(row.FinalBillToAccount, finalCompany, row.RequestedBilling);
            row.MatchingContactCode = row.FinalBillToContact;
            evidence.Plan.FinalBillToContact = row.FinalBillToContact;
            journal.Save();
            BillingGuard.VerifyCreatedContact(gateway, row, row.FinalBillToContact);
            journal.Save();
            gateway.VerifyUndispatched("Create contact");
        }
        else if (row.ContactAction is "USE EXISTING CONTACT" or "UPDATE EXISTING CONTACT")
        {
            var chosen = gateway.GetAccount(row.FinalBillToContact);
            if (!row.EffectiveBilling!.RequiresContactRelationship && !TextRules.Same(chosen.PrimaryAccount, row.FinalBillToAccount))
                throw new RecoveryReviewException("REVIEW: cross-account contact reuse is not authorized.");
            if (row.ContactAction == "UPDATE EXISTING CONTACT")
            {
                if (!Verified("Update billing contact")) gateway.UpdateBillingContact(row);
                BillingGuard.VerifyReusedContact(gateway, row);
                journal.Save();
            }
        }
        if (row.EffectiveBilling!.RequiresContactRelationship)
        {
            using var contactProgress = gateway.Progress?.Begin("Linking the verified billing contact to the Bill-To company as a secondary affiliation");
            BillingGuard.VerifyCrossAccountContact(gateway, row, checkUniqueness: true);
            gateway.EnsureRelationship(row.FinalBillToAccount, row.FinalBillToContact, "CTA");
            BillingState.Set(row, row.EffectiveBilling with { ContactRelationshipPending = false });
            BillingGuard.VerifyContactRelationship(gateway, row);
            journal.Save();
            var originalCompany = string.IsNullOrWhiteSpace(row.EffectiveBilling.Contact.PrimaryAccount) ? "none" : row.EffectiveBilling.Contact.PrimaryAccount;
            gateway.Progress?.Message($"Billing-company contact affiliation verified. Contact {row.FinalBillToContact} current primary company: {originalCompany}.");
        }
        gateway.VerifyUndispatched("Add relationship");
        BillingGuard.VerifyFinal(gateway, row);
    }
}
