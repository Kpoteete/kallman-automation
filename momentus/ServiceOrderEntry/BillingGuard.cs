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
        var written = gateway.Journal?.Evidence.Stages.Any(x => x.Operation is "Update service order" or "Assign partial billing" && x.Status == StageStatus.Verified) == true;
        if (!TextRules.Same(order.Account, row.BillingSourceAccount) ||
            !TextRules.Same(order.BillToAccount, written ? row.FinalBillToAccount : row.ExistingBillToAccount.AccountCode) ||
            !TextRules.Same(order.BillToContact, written ? row.FinalBillToContact : row.ExistingBillToContact.AccountCode))
            throw new RecoveryReviewException("REVIEW: current service-order billing identity changed; stale billing plan stopped.");
    }

    public static void Preflight(MomentusGateway gateway, RunRow row, ServiceOrdersModel order, ProcessingJournal journal, bool validateWholePlan = true)
    {
        Instructions(gateway, row);
        var state = row.EffectiveBilling!;
        var orderWritten = journal.Evidence.Stages.Any(x => x.Operation is "Update service order" or "Assign partial billing" && x.Status == StageStatus.Verified);
        if (!TextRules.Same(order.Account, row.BillingSourceAccount) ||
            !TextRules.Same(order.BillToAccount, orderWritten ? row.FinalBillToAccount : row.ExistingBillToAccount.AccountCode) ||
            !TextRules.Same(order.BillToContact, orderWritten ? row.FinalBillToContact : row.ExistingBillToContact.AccountCode))
            throw new RecoveryReviewException("REVIEW: current service-order billing identity differs from the evaluated/journaled identity.");
        var above = gateway.BillingConfiguration.Selector(row.RequestedBilling) == "ABOVE";
        if (above && (!state.AboveAddress || state.AccountCreationPending ||
            row.BillToAddressAction != "KEEP EXISTING ABOVE ADDRESS" ||
            !TextRules.Same(row.FinalBillToAccount, row.ExistingBillToAccount.AccountCode) ||
            !TextRules.Same(row.FinalBillToAccount, row.BillingSourceAccount)))
            throw new RecoveryReviewException("REVIEW: Use Above Address cannot authorize a different Bill-To account or address.");
        if (!above && state.AboveAddress) throw new RecoveryReviewException("REVIEW: selector conflicts with effective billing state.");
        if (!BillingRules.ValidCode(row.ExistingBillToAccount.AccountCode))
            throw new RecoveryReviewException("REVIEW: original Bill-To account identity is unavailable for the billing relationship.");
        if (row.BillToAddressAction.Contains("UPDATE", StringComparison.OrdinalIgnoreCase))
            throw new RecoveryReviewException("REVIEW: shared account address updates are not authorized.");
        if (BillingRules.RequestedErrors(row.RequestedBilling, gateway.BillingConfiguration).Count > 0)
            throw new RecoveryReviewException("REVIEW: invalid billing request.");
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
            if (gateway.HasGlobalContactEmail(row.RequestedBilling.Email))
                throw new RecoveryReviewException("REVIEW: contact creation stopped because existing contacts use the billing email; re-evaluate identity.");
        }
        else
        {
            VerifyReusedContact(gateway, row);
            if (state.RequiresContactRelationship) VerifyCrossAccountContact(gateway, row, checkUniqueness: true);
        }
        if (validateWholePlan)
        {
            var validation = ValidationRules.Validate(row);
            if (validation.Status == "REVIEW") throw new RecoveryReviewException("REVIEW: " + validation.Message);
        }
    }

    public static void VerifyCreatedAccount(MomentusGateway gateway, RunRow row, string code)
    {
        var current = gateway.GetAccount(code);
        if (!TextRules.Same(current.AccountCode, code) || current.AccountClass != "O" ||
            current.Company != row.RequestedBilling.CompanyName || !gateway.BillingAddressesMatch(current, row.RequestedBilling) ||
            string.IsNullOrWhiteSpace(gateway.BillingConfiguration.EventSalesNotApplicableCode) ||
            current.EventSalesStatus != gateway.BillingConfiguration.EventSalesNotApplicableCode)
            throw new RecoveryReviewException("REVIEW: created Bill-To account readback failed identity, exact contract name, address, or Not Applicable Event Sales status.");
        BillingState.Set(row, row.EffectiveBilling! with { Account = current, AccountCreationPending = false });
    }

    public static void VerifyCreatedContact(MomentusGateway gateway, RunRow row, string code)
    {
        var current = gateway.GetAccount(code);
        if (!TextRules.Same(current.AccountCode, code) || current.AccountClass != "P" ||
            (!string.IsNullOrWhiteSpace(current.PrimaryAccount) && !TextRules.Same(current.PrimaryAccount, row.FinalBillToAccount)) ||
            TextRules.NormalizeEmail(current.Email) != TextRules.NormalizeEmail(row.RequestedBilling.Email) ||
            !TextRules.Same(current.FirstName, row.RequestedBilling.FirstName) || !TextRules.Same(current.LastName, row.RequestedBilling.LastName))
            throw new RecoveryReviewException("REVIEW: created billing contact readback failed identity, parent account, name or email.");
        var creation = gateway.Journal?.Evidence.Stages.SingleOrDefault(x => x.Operation == "Create contact" && x.Status == StageStatus.Verified);
        if (creation is not null)
        {
            var verified = Newtonsoft.Json.JsonConvert.DeserializeObject<Ungerboeck.Api.Models.Subjects.AllAccountsModel>(creation.Result)!;
            if (!TextRules.Same(current.PrimaryAccount, verified.PrimaryAccount) &&
                !AcceptInitialAffiliationPrimary(gateway, row, MomentusGateway.ToAccount(verified), current))
                throw new RecoveryReviewException("REVIEW: created contact's original primary company changed after its verified creation.");
        }
        if (row.EffectiveBilling!.ContactAddressRequired && !TextRules.AddressMatches(current, BillingState.ContactRequest(row)))
            throw new RecoveryReviewException("REVIEW: created billing contact address differs from the selected contract billing address.");
        BillingState.Set(row, row.EffectiveBilling! with { Contact = current, ContactCreationPending = false,
            RequiresContactRelationship = true, ContactRelationshipPending = true });
        if (gateway.HasActiveContactRelationship(row.FinalBillToAccount, code))
            BillingState.Set(row, row.EffectiveBilling! with { ContactRelationshipPending = false });
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
        var updated = gateway.Journal?.Evidence.Stages.LastOrDefault(x => x.Operation == "Update billing contact" && x.Status == StageStatus.Verified);
        if (updated is not null)
        {
            var intended = Newtonsoft.Json.JsonConvert.DeserializeObject<Ungerboeck.Api.Models.Subjects.AllAccountsModel>(updated.Intent)!;
            var original = updated.Source.TryGetValue("original", out var baseline)
                ? Newtonsoft.Json.JsonConvert.DeserializeObject<Ungerboeck.Api.Models.Subjects.AllAccountsModel>(baseline) : null;
            if (!TextRules.Same(intended.AccountCode, expected.AccountCode) ||
                original is null || !TextRules.Same(intended.PrimaryAccount, original.PrimaryAccount) ||
                TextRules.NormalizeEmail(intended.Email) != TextRules.NormalizeEmail(row.RequestedBilling.Email) ||
                intended.FirstName != row.RequestedBilling.FirstName || intended.LastName != row.RequestedBilling.LastName)
                throw new RecoveryReviewException("REVIEW: verified billing contact update conflicts with the current plan.");
            expected = expected with { FirstName = intended.FirstName, LastName = intended.LastName };
            if (Newtonsoft.Json.Linq.JObject.Parse(updated.Intent).Property("Address1") is not null)
            {
                if (row.EffectiveBilling.ContactAddressRequired && !TextRules.AddressMatches(MomentusGateway.ToAccount(intended), BillingState.ContactRequest(row)))
                    throw new RecoveryReviewException("REVIEW: verified billing contact address update conflicts with the selected billing address.");
                expected = expected with { Address = intended.Address1 ?? "", City = intended.City ?? "", State = intended.State ?? "",
                    PostalCode = intended.PostalCode ?? "", Country = intended.Country ?? "" };
            }
        }
        var current = gateway.GetAccount(row.FinalBillToContact);
        if (!TextRules.Same(current.PrimaryAccount, expected.PrimaryAccount) &&
            AcceptInitialAffiliationPrimary(gateway, row, expected, current))
            expected = current;
        if (!TextRules.Same(current.AccountCode, expected.AccountCode) || current.AccountClass != "P" ||
            current.FirstName != expected.FirstName || current.LastName != expected.LastName ||
            TextRules.NormalizeEmail(current.Email) != TextRules.NormalizeEmail(expected.Email) || !TextRules.Same(current.PrimaryAccount, expected.PrimaryAccount) ||
            row.EffectiveBilling!.ContactAddressRequired && !TextRules.AddressMatches(current, new("", "", "", "", "", "",
                expected.Address, expected.City, expected.State, expected.PostalCode, expected.Country)))
            throw new RecoveryReviewException("REVIEW: effective Bill-To contact changed or cannot be verified.");
        BillingState.Set(row, row.EffectiveBilling with { Contact = current });
        if (row.EffectiveBilling.RequiresContactRelationship && !row.EffectiveBilling.ContactRelationshipPending)
            VerifyContactRelationship(gateway, row);
    }

    internal static void VerifyCrossAccountContact(MomentusGateway gateway, RunRow row, bool checkUniqueness)
    {
        _ = row.EffectiveBilling ?? throw new RecoveryReviewException("REVIEW: cross-account contact evidence is unavailable.");
        VerifyReusedContact(gateway, row);
        var state = row.EffectiveBilling!;
        if (!state.RequiresContactRelationship || state.ContactCreationPending ||
            (!string.IsNullOrWhiteSpace(state.Contact.PrimaryAccount) && !BillingRules.ValidCode(state.Contact.PrimaryAccount)) ||
            !TextRules.Same(state.Contact.FirstName, row.RequestedBilling.FirstName) || !TextRules.Same(state.Contact.LastName, row.RequestedBilling.LastName) ||
            TextRules.NormalizeEmail(state.Contact.Email) != TextRules.NormalizeEmail(row.RequestedBilling.Email))
            throw new RecoveryReviewException("REVIEW: cross-account contact no longer matches the verified billing request.");
        if (!string.IsNullOrWhiteSpace(state.Contact.PrimaryAccount))
        {
            var owner = gateway.GetAccount(state.Contact.PrimaryAccount);
            if (owner.AccountClass != "O" || !TextRules.Same(owner.AccountCode, state.Contact.PrimaryAccount))
                throw new RecoveryReviewException("REVIEW: cross-account contact's original primary company cannot be verified.");
        }
        if (checkUniqueness)
        {
            var matches = gateway.FindGlobalContactCandidates(row.RequestedBilling.Email);
            if (matches.Count != 1 || matches[0] != row.EffectiveBilling!.Contact)
                throw new RecoveryReviewException("REVIEW: a single matching cross-account contact cannot be established from current complete results.");
        }
    }

    internal static void VerifyContactRelationship(MomentusGateway gateway, RunRow row)
    {
        if (!gateway.HasActiveContactRelationship(row.FinalBillToAccount, row.FinalBillToContact))
            throw new RecoveryReviewException("REVIEW: the Bill-To company's active contact relationship cannot be verified.");
        var current = gateway.GetAccount(row.FinalBillToContact);
        if (current != row.EffectiveBilling!.Contact &&
            !AcceptInitialAffiliationPrimary(gateway, row, row.EffectiveBilling.Contact, current))
            throw new RecoveryReviewException("REVIEW: contact identity or its original primary company changed while verifying the affiliation.");
    }

    private static bool AcceptInitialAffiliationPrimary(MomentusGateway gateway, RunRow row, AccountInfo expected, AccountInfo current)
    {
        var journal = gateway.Journal;
        if (journal is null || row.EffectiveBilling?.RequiresContactRelationship != true ||
            !string.IsNullOrWhiteSpace(expected.PrimaryAccount) || current.AccountClass != "P" ||
            !BillingRules.ValidCode(row.FinalBillToAccount) || !TextRules.Same(current.AccountCode, row.FinalBillToContact) ||
            !TextRules.Same(current.PrimaryAccount, row.FinalBillToAccount) ||
            current with { PrimaryAccount = expected.PrimaryAccount } != expected)
            return false;

        bool IntendedSecondary(Ungerboeck.Api.Models.Subjects.RelationshipsModel link) =>
            TextRules.Same(link.MasterOrganizationCode, journal.Evidence.Identity.Organization) &&
            TextRules.Same(link.SubordinateOrganizationCode, journal.Evidence.Identity.Organization) &&
            TextRules.Same(link.MasterAccountCode, row.FinalBillToAccount) &&
            TextRules.Same(link.SubordinateAccountCode, row.FinalBillToContact) && link.RelationshipType == "CTA" &&
            link.EventSalesDesignation == Ungerboeck.Api.Models.USISDKConstants.RelationshipDesignationStatus.Secondary;
        var proven = journal.Evidence.Stages.Any(stage => stage.Operation == "Add relationship" &&
            stage.Status == StageStatus.Verified && stage.Attempts > 0 &&
            stage.Source.TryGetValue("ContactBeforeAffiliation", out var original) &&
            Newtonsoft.Json.JsonConvert.DeserializeObject<AccountInfo>(original) == expected &&
            Newtonsoft.Json.JsonConvert.DeserializeObject<Ungerboeck.Api.Models.Subjects.RelationshipsModel>(stage.Intent) is { } intent && IntendedSecondary(intent) &&
            Newtonsoft.Json.JsonConvert.DeserializeObject<Ungerboeck.Api.Models.Subjects.RelationshipsModel>(stage.Result) is { } result && IntendedSecondary(result));
        if (!proven) return false;
        // Require a fresh secondary readback, not merely a historical success or any active link.
        var relationship = gateway.GetContactRelationship(row.FinalBillToAccount, row.FinalBillToContact);
        if (relationship is null || !IntendedSecondary(relationship)) return false;
        BillingState.Set(row, row.EffectiveBilling! with { Contact = current, ContactRelationshipPending = false });
        journal.Save(); // Persist the verified effective primary before any later order, activation or email write.
        return true;
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
