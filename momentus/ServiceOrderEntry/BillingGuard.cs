using Ungerboeck.Api.Models.Subjects;

namespace ServiceOrderEntry;

internal static class BillingGuard
{
    public static void Instructions(MomentusGateway gateway, RunRow row)
    {
        gateway.BillingConfiguration.Validate();
        if (row.BillingVersion != 1 || row.BillingConfiguration is null || row.EffectiveBilling is null ||
            !row.BillingConfiguration.SameAs(gateway.BillingConfiguration) || !BillingRules.ValidCode(row.BillingSourceAccount))
            throw new RecoveryReviewException("REVIEW: legacy or changed billing configuration/plan requires billing re-evaluation; verified journal identities are preserved.");
        var source = gateway.GetAccountModel(row.BillingSourceAccount);
        if (!TextRules.Same(source.AccountCode, row.BillingSourceAccount) ||
            MomentusGateway.BillingFrom(source, gateway.BillingConfiguration) != row.RequestedBilling)
            throw new RecoveryReviewException("REVIEW: Bill-To selector or billing instructions changed after evaluation; stale billing plan stopped.");
        if (gateway.BillingConfiguration.Selector(row.RequestedBilling) == "UNKNOWN")
            throw new RecoveryReviewException("REVIEW: unknown Bill-To selector.");
        var order = gateway.GetOrder(row.OrderNumber);
        var written = gateway.Journal?.Evidence.Stages.Any(x => x.Operation == "Update service order" && x.Status == StageStatus.Verified) == true;
        if (!TextRules.Same(order.Account, row.BillingSourceAccount) ||
            !TextRules.Same(order.BillToAccount, written ? row.FinalBillToAccount : row.ExistingBillToAccount.AccountCode) ||
            !TextRules.Same(order.BillToContact, written ? row.FinalBillToContact : row.ExistingBillToContact.AccountCode))
            throw new RecoveryReviewException("REVIEW: current service-order billing identity changed; stale billing plan stopped.");
    }

    public static void Preflight(MomentusGateway gateway, RunRow row, ServiceOrdersModel order, ProcessingJournal journal)
    {
        Instructions(gateway, row);
        var state = row.EffectiveBilling!;
        var orderWritten = journal.Evidence.Stages.Any(x => x.Operation == "Update service order" && x.Status == StageStatus.Verified);
        if (!TextRules.Same(order.Account, row.BillingSourceAccount) ||
            !TextRules.Same(order.BillToAccount, orderWritten ? row.FinalBillToAccount : row.ExistingBillToAccount.AccountCode) ||
            !TextRules.Same(order.BillToContact, orderWritten ? row.FinalBillToContact : row.ExistingBillToContact.AccountCode))
            throw new RecoveryReviewException("REVIEW: current service-order billing identity differs from the evaluated/journaled identity.");
        var above = gateway.BillingConfiguration.Selector(row.RequestedBilling) == "ABOVE";
        if (above && (!state.AboveAddress || state.AccountCreationPending || state.ContactCreationPending ||
            row.ContactAction != "KEEP EXISTING" || row.BillToAddressAction != "KEEP EXISTING ABOVE ADDRESS" ||
            !TextRules.Same(row.FinalBillToAccount, row.ExistingBillToAccount.AccountCode) ||
            !TextRules.Same(row.FinalBillToContact, row.ExistingBillToContact.AccountCode) ||
            !TextRules.Same(row.FinalBillToAccount, row.BillingSourceAccount)))
            throw new RecoveryReviewException("REVIEW: Use Above Address cannot authorize separate Bill-To mutations or reassignment.");
        if (!above && state.AboveAddress) throw new RecoveryReviewException("REVIEW: selector conflicts with effective billing state.");
        if (!BillingRules.ValidCode(row.ExistingBillToAccount.AccountCode))
            throw new RecoveryReviewException("REVIEW: original Bill-To account identity is unavailable for the billing relationship.");
        if (row.BillToAddressAction.Contains("UPDATE", StringComparison.OrdinalIgnoreCase))
            throw new RecoveryReviewException("REVIEW: shared account address updates are not authorized.");
        if (!above && BillingRules.RequestedErrors(row.RequestedBilling, gateway.BillingConfiguration).Count > 0)
            throw new RecoveryReviewException("REVIEW: invalid separate billing request.");
        var createdAccount = journal.CreatedAccount("Create organization account");
        if (createdAccount is not null) VerifyCreatedAccount(gateway, row, createdAccount);
        else if (state.AccountCreationPending)
        {
            if (string.IsNullOrWhiteSpace(gateway.BillingConfiguration.EventSalesNotApplicableCode))
                throw new RecoveryReviewException("REVIEW: Not Applicable Event Sales status code is not configured.");
            var matches = gateway.FindOrganizationCandidates(row.RequestedBilling);
            if (matches.Count > 0) throw new RecoveryReviewException("REVIEW: account creation stopped because one or multiple duplicate candidates were found; re-evaluate billing.");
        }
        else VerifyReusedAccount(gateway, row);
        state = row.EffectiveBilling!;
        var createdContact = journal.CreatedAccount("Create contact");
        if (createdContact is not null) VerifyCreatedContact(gateway, row, createdContact);
        else if (state.ContactCreationPending)
        {
            // Complete global absence includes the intended account, and cross-account duplicate evidence.
            var matches = gateway.FindContacts(null, row.RequestedBilling.Email);
            if (matches.Count > 0) throw new RecoveryReviewException("REVIEW: contact creation stopped because existing contacts use the billing email; re-evaluate identity.");
        }
        else VerifyReusedContact(gateway, row);
        var validation = ValidationRules.Validate(row);
        if (validation.Status == "REVIEW") throw new RecoveryReviewException("REVIEW: " + validation.Message);
    }

    public static void VerifyCreatedAccount(MomentusGateway gateway, RunRow row, string code)
    {
        var current = gateway.GetAccount(code);
        if (!TextRules.Same(current.AccountCode, code) || current.AccountClass != "O" ||
            current.Company != row.RequestedBilling.CompanyName || !TextRules.AddressMatches(current, row.RequestedBilling) ||
            string.IsNullOrWhiteSpace(gateway.BillingConfiguration.EventSalesNotApplicableCode) ||
            current.EventSalesStatus != gateway.BillingConfiguration.EventSalesNotApplicableCode)
            throw new RecoveryReviewException("REVIEW: created Bill-To account readback failed identity, exact contract name, address, or Not Applicable Event Sales status.");
        BillingState.Set(row, row.EffectiveBilling! with { Account = current, AccountCreationPending = false });
    }

    public static void VerifyCreatedContact(MomentusGateway gateway, RunRow row, string code)
    {
        var current = gateway.GetAccount(code);
        if (!TextRules.Same(current.AccountCode, code) || current.AccountClass != "P" ||
            !TextRules.Same(current.PrimaryAccount, row.FinalBillToAccount) ||
            TextRules.NormalizeEmail(current.Email) != TextRules.NormalizeEmail(row.RequestedBilling.Email) ||
            !TextRules.Same(current.FirstName, row.RequestedBilling.FirstName) || !TextRules.Same(current.LastName, row.RequestedBilling.LastName))
            throw new RecoveryReviewException("REVIEW: created billing contact readback failed identity, parent account, name or email.");
        BillingState.Set(row, row.EffectiveBilling! with { Contact = current, ContactCreationPending = false });
    }

    public static void VerifyReusedAccount(MomentusGateway gateway, RunRow row)
    {
        var expected = row.EffectiveBilling!.Account;
        var current = gateway.GetAccount(row.FinalBillToAccount);
        if (!TextRules.Same(current.AccountCode, expected.AccountCode) || current.AccountClass != "O" ||
            current.Company != expected.Company || !TextRules.AddressMatches(current, new("", "", "", "", "", "", expected.Address,
                expected.City, expected.State, expected.PostalCode, expected.Country)))
            throw new RecoveryReviewException("REVIEW: effective Bill-To account changed or cannot be verified.");
        if (row.BillToAddressAction == "CREATE RELATED BILL-TO ACCOUNT" &&
            current.EventSalesStatus != gateway.BillingConfiguration.EventSalesNotApplicableCode)
            throw new RecoveryReviewException("REVIEW: new Bill-To account no longer has the configured Not Applicable Event Sales status.");
        BillingState.Set(row, row.EffectiveBilling with { Account = current });
    }

    public static void VerifyReusedContact(MomentusGateway gateway, RunRow row)
    {
        var expected = row.EffectiveBilling!.Contact;
        var current = gateway.GetAccount(row.FinalBillToContact);
        if (!TextRules.Same(current.AccountCode, expected.AccountCode) || current.AccountClass != "P" ||
            current.FirstName != expected.FirstName || current.LastName != expected.LastName ||
            TextRules.NormalizeEmail(current.Email) != TextRules.NormalizeEmail(expected.Email) || !TextRules.Same(current.PrimaryAccount, expected.PrimaryAccount))
            throw new RecoveryReviewException("REVIEW: effective Bill-To contact changed or cannot be verified.");
        BillingState.Set(row, row.EffectiveBilling with { Contact = current });
    }

    public static void VerifyFinal(MomentusGateway gateway, RunRow row)
    {
        Instructions(gateway, row);
        VerifyReusedAccount(gateway, row);
        VerifyReusedContact(gateway, row);
        var errors = BillingRules.EffectiveErrors(row, requireIds: true);
        if (errors.Count > 0) throw new RecoveryReviewException("REVIEW: " + string.Join(" ", errors));
    }
}
