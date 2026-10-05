using System.Globalization;

namespace ServiceOrderEntry;

internal sealed record SalesRepLookup(string DisplayName, string UdfCode, string AccountCode);
internal sealed record CategoryLookup(string Description, int Sequence, IReadOnlyList<string> Identifiers);
internal sealed record DocumentInfo(string Type, int SequenceNumber, string DocumentId, string Description, string Category, DateTime? EnteredOn = null)
{
    public string ContentHash { get; init; } = "";
}
internal sealed record OrderItemInfo(int LineNumber, string ResourceCode, string Description, string AlternateDescription)
{
    public string EvidenceHash { get; init; } = "";
    public string SearchText => string.Join(" | ", new[] { Description, AlternateDescription, ResourceCode }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string Identifier => !string.IsNullOrWhiteSpace(Description) ? Description.Trim() : (!string.IsNullOrWhiteSpace(AlternateDescription) ? AlternateDescription.Trim() : ResourceCode.Trim());
}

internal sealed record BillingRequest(
    string CompanyName,
    string AttentionOf,
    string FirstName,
    string LastName,
    string Email,
    string UseRequestedAddress,
    string Address,
    string City,
    string State,
    string PostalCode,
    string Country);

internal sealed record AccountInfo(
    string AccountCode,
    string Company,
    string FirstName,
    string LastName,
    string Email,
    string Address,
    string City,
    string State,
    string PostalCode,
    string Country,
    string PrimaryAccount)
{
    public string AccountClass { get; init; } = "";
    public string EventSalesStatus { get; init; } = "";
}

internal sealed class RunRow
{
    public string Outcome { get; set; } = "";
    public bool ExhibitorActivationPending { get; set; }
    public DecisionInputs? DecisionInputs { get; set; }
    public ContractSelection? Contracts { get; set; }
    public List<ContractCopyIdentity> ContractCopies { get; set; } = [];
    public int? ManagedNoteSequence { get; set; }
    public string ManagedNoteContentHash { get; set; } = "";
    public int BillingVersion { get; set; }
    public string BillingSourceAccount { get; set; } = "";
    public BillingConfiguration? BillingConfiguration { get; set; }
    public EffectiveBillingState? EffectiveBilling { get; set; }
    public AccountMatchKind AccountMatchKind { get; set; }
    public List<int> ValidCategoryIds { get; set; } = [];
    public int EventId { get; set; }
    public int ExhibitorId { get; set; }
    public int OrderNumber { get; set; }
    public string ExhibitorName { get; set; } = "";
    public DateTime? OrderDate { get; set; }
    public string ExhibitorSalesRep { get; set; } = "";
    public string ProposedOrderAccountRep { get; set; } = "";
    public string ServiceOrderItemIdentifier { get; set; } = "";
    public int? ProposedCategory { get; set; }
    public string ProposedCategoryName { get; set; } = "";
    public string ExistingExhibitorCategories { get; set; } = "";
    public string ExhibitorCategoriesToAdd { get; set; } = "";
    public string ExhibitorCategoriesToRemove { get; set; } = "";
    public string FinalExhibitorCategories { get; set; } = "";
    public bool ApprovalNeeded { get; set; }
    public int ContractPdfCount { get; set; }
    public int ContractPdfCopiesNeeded { get; set; }
    public string ContractPdfCopyStatus { get; set; } = "NOT ATTEMPTED";
    public string PaymentScheduleText { get; set; } = "";
    public string PaymentScheduleNoteAction { get; set; } = "";
    public string PaymentScheduleNoteStatus { get; set; } = "NOT ATTEMPTED";
    public string PaymentScheduleDecisionMessage { get; set; } = "";
    public string ReadyEmailRecipient { get; set; } = "";
    public string ReadyEmailCcRecipient { get; set; } = "";
    public string ReadyEmailStatus { get; set; } = "NOT ATTEMPTED";
    public string OrderStatusAction { get; set; } = "";
    public string ExhibitorStatusAction { get; set; } = "";
    public AccountInfo ExistingBillToAccount { get; set; } = EmptyAccount;
    public AccountInfo ExistingBillToContact { get; set; } = EmptyAccount;
    public BillingRequest RequestedBilling { get; set; } = EmptyBilling;
    public string BillToAddressAction { get; set; } = "";
    public string BillToContactMatchResult { get; set; } = "";
    public string MatchingContactCode { get; set; } = "";
    public string ContactAction { get; set; } = "";
    public string FinalBillToAccount { get; set; } = "";
    public string FinalBillToContact { get; set; } = "";
    public string FinalAddress { get; set; } = "";
    public string FinalCity { get; set; } = "";
    public string FinalState { get; set; } = "";
    public string FinalPostalCode { get; set; } = "";
    public string FinalCountry { get; set; } = "";
    public string ProposedBoothNumber { get; set; } = "";
    public string ValidationStatus { get; set; } = "REVIEW";
    public string ValidationMessage { get; set; } = "";
    public string ServiceOrderUpdateStatus { get; set; } = "NOT ATTEMPTED";
    public string UpdateMessage { get; set; } = "";
    public string AccountDecisionMessage { get; set; } = "";
    public string ContactDecisionMessage { get; set; } = "";
    public bool BillingRequestBypassed { get; set; }
    public string RunId { get; set; } = "";
    public string ProcessingId { get; set; } = "";
    public string JournalStages { get; set; } = "";

    public static readonly AccountInfo EmptyAccount = new("", "", "", "", "", "", "", "", "", "", "");
    public static readonly BillingRequest EmptyBilling = new("", "", "", "", "", "", "", "", "", "", "");

    public IReadOnlyList<string> ToCsvFields() =>
    [
        EventId.ToString(CultureInfo.InvariantCulture), ExhibitorId.ToString(CultureInfo.InvariantCulture), OrderNumber.ToString(CultureInfo.InvariantCulture), ExhibitorName,
        OrderDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", ExhibitorSalesRep, ProposedOrderAccountRep,
        ServiceOrderItemIdentifier, ProposedCategory?.ToString(CultureInfo.InvariantCulture) ?? "", ProposedCategoryName,
        ExistingExhibitorCategories, ExhibitorCategoriesToAdd, ExhibitorCategoriesToRemove, FinalExhibitorCategories, ApprovalNeeded ? "YES" : "NO",
        ContractPdfCount.ToString(CultureInfo.InvariantCulture), ContractPdfCopiesNeeded.ToString(CultureInfo.InvariantCulture), ContractPdfCopyStatus,
        PaymentScheduleText, PaymentScheduleNoteAction, PaymentScheduleNoteStatus, ReadyEmailRecipient, ReadyEmailStatus,
        OrderStatusAction, ExhibitorStatusAction,
        ExistingBillToAccount.AccountCode, ExistingBillToAccount.Company, ExistingBillToAccount.Address, ExistingBillToAccount.City,
        ExistingBillToAccount.State, ExistingBillToAccount.PostalCode, ExistingBillToAccount.Country,
        ExistingBillToContact.AccountCode, ExistingBillToContact.FirstName, ExistingBillToContact.LastName, ExistingBillToContact.Email,
        RequestedBilling.CompanyName, RequestedBilling.AttentionOf, RequestedBilling.FirstName, RequestedBilling.LastName, RequestedBilling.Email,
        RequestedBilling.UseRequestedAddress, RequestedBilling.Address, RequestedBilling.City, RequestedBilling.State, RequestedBilling.PostalCode, RequestedBilling.Country,
        BillToAddressAction, BillToContactMatchResult, MatchingContactCode, ContactAction, FinalBillToAccount, FinalBillToContact,
        FinalAddress, FinalCity, FinalState, FinalPostalCode, FinalCountry, ProposedBoothNumber, ValidationStatus, ValidationMessage,
        ServiceOrderUpdateStatus, UpdateMessage, RunId, ProcessingId, JournalStages,
        EffectiveBilling?.Account.Company ?? "", EffectiveBilling?.Contact.FirstName ?? "", EffectiveBilling?.Contact.LastName ?? "",
        EffectiveBilling?.Contact.Email ?? "", EffectiveBilling?.AccountSource ?? "", EffectiveBilling?.ContactSource ?? "",
        EffectiveBilling?.AboveAddress == true ? "YES" : "NO", AccountMatchKind.ToString(), Outcome, ExhibitorActivationPending ? "YES" : "NO",
        ReadyEmailCcRecipient
    ];
}
