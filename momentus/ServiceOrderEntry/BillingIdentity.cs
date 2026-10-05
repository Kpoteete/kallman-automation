using System.Text.Json;
using Ungerboeck.Api.Models.Subjects;

namespace ServiceOrderEntry;

internal sealed record BillingConfiguration
{
    private static readonly string[] RequiredAboveSelectors = ["", "ECA", "No", "N", "Use Above Address"];
    public string Header { get; init; } = "";
    public string Class { get; init; } = "";
    public string Type { get; init; } = "";
    // Tenant Account Status code, explicitly confirmed as class 0 / Not Applicable.
    public string EventSalesNotApplicableCode { get; init; } = "";
    public string[] AboveSelectors { get; init; } = ["", "ECA", "No", "N", "Use Above Address"];
    public string[] SeparateSelectors { get; init; } = ["BA", "Yes", "Y", "Below Address"];

    public static BillingConfiguration Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<BillingConfiguration>(File.ReadAllText(path), new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Billing configuration is empty.")
        : new();

    public void Validate()
    {
        if (new[] { Header, Class, Type }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Billing UDF Header/Class/Type must be explicitly configured in --billing-config before processing.");
        if (Class.Length != 1 || Type.Length > 2 || new[] { Header, Class, Type }.Any(x => x.Any(char.IsControl)))
            throw new InvalidDataException("Billing UDF selector configuration is invalid (Class: 1 character; Type: at most 2).");
        if (EventSalesNotApplicableCode is null || EventSalesNotApplicableCode.Length > 1 || EventSalesNotApplicableCode.Any(char.IsWhiteSpace) || EventSalesNotApplicableCode.Any(char.IsControl))
            throw new InvalidDataException("EventSalesNotApplicableCode must be the tenant's single-character Not Applicable Account Status code.");
        if (AboveSelectors is null || SeparateSelectors is null || AboveSelectors.Length == 0 || SeparateSelectors.Length == 0 ||
            AboveSelectors.Concat(SeparateSelectors).Any(x => x is null) || SeparateSelectors.Any(string.IsNullOrWhiteSpace) ||
            AboveSelectors.Concat(SeparateSelectors).Select(TextRules.Clean).Distinct(StringComparer.OrdinalIgnoreCase).Count() != AboveSelectors.Length + SeparateSelectors.Length)
            throw new InvalidDataException("Billing selectors must be explicit, unique and disjoint; blank cannot request separate Bill-To.");
        if (SeparateSelectors.Any(x => RequiredAboveSelectors.Any(y => TextRules.Same(x, y))))
            throw new InvalidDataException("Use Above Address/ECA/No/N/blank cannot be configured as separate Bill-To.");
    }

    public string Selector(BillingRequest request) => RequiredAboveSelectors.Concat(AboveSelectors).Any(x => TextRules.Same(x, request.UseRequestedAddress)) ? "ABOVE" :
        SeparateSelectors.Any(x => TextRules.Same(x, request.UseRequestedAddress)) ? "SEPARATE" : "UNKNOWN";

    public bool SameAs(BillingConfiguration other) => Header == other.Header && Class == other.Class && Type == other.Type &&
        EventSalesNotApplicableCode == other.EventSalesNotApplicableCode && AboveSelectors.SequenceEqual(other.AboveSelectors) && SeparateSelectors.SequenceEqual(other.SeparateSelectors);
}

internal enum AccountMatchKind { Confirmed, Candidate, None, Ambiguous, SearchFailure }
internal sealed record AccountMatch(AccountMatchKind Kind, AccountInfo? Account, string Message);

internal static class AccountIdentityRules
{
    public static AccountMatch Resolve(IReadOnlyList<AccountInfo> candidates, BillingRequest request)
    {
        if (candidates.Count == 0) return new(AccountMatchKind.None, null, "");
        // A same-name record with conflicting/missing address remains a possible shared identity.
        // It cannot be bypassed by creating another account or overwriting its address.
        if (candidates.Count > 1) return new(AccountMatchKind.Ambiguous, null,
            $"Multiple plausible Bill-To accounts: {string.Join(", ", candidates.Select(x => x.AccountCode))}.");
        var account = candidates[0];
        if (BillingRules.ValidCode(account.AccountCode) && account.AccountClass == "O" &&
            TextRules.CompanyMatches(account.Company, request.CompanyName) && TextRules.AddressMatches(account, request))
            return new(AccountMatchKind.Confirmed, account, "");
        return new(AccountMatchKind.Candidate, null, "Possible shared Bill-To account has different or insufficient billing evidence; address-update authority is not established. REVIEW.");
    }
}

internal sealed record EffectiveBillingState(AccountInfo Account, AccountInfo Contact, string AccountSource, string ContactSource,
    bool AboveAddress, bool AccountCreationPending = false, bool ContactCreationPending = false);

internal static class BillingState
{
    public static void Set(RunRow row, EffectiveBillingState state)
    {
        row.EffectiveBilling = state;
        row.FinalBillToAccount = state.Account.AccountCode;
        row.FinalBillToContact = state.Contact.AccountCode;
        row.FinalAddress = state.Account.Address;
        row.FinalCity = state.Account.City;
        row.FinalState = state.Account.State;
        row.FinalPostalCode = state.Account.PostalCode;
        row.FinalCountry = state.Account.Country;
    }

    public static BillingRequest Read(AllAccountsModel model, BillingConfiguration config)
    {
        config.Validate();
        var matches = model.AccountUserFieldSets?.Where(x => TextRules.Same(x.Header, config.Header) &&
            TextRules.Same(x.Class, config.Class) && TextRules.Same(x.Type, config.Type)).ToList() ?? [];
        if (matches.Count != 1) throw new InvalidDataException($"Expected exactly one billing UDF set ({config.Header}/{config.Class}/{config.Type}); found {matches.Count}.");
        var f = matches[0];
        // Name is the exact submission, never the comparison identity (not even trimmed).
        return new(f.UserText03 ?? "", TextRules.Clean(f.UserText13), TextRules.Clean(f.UserText05), TextRules.Clean(f.UserText09),
            TextRules.Clean(f.UserText10), TextRules.Clean(f.UserText11), TextRules.Clean(f.UserText04), TextRules.Clean(f.UserText15),
            TextRules.Clean(f.UserText06), TextRules.Clean(f.UserText07), TextRules.Clean(f.UserText08));
    }
}
