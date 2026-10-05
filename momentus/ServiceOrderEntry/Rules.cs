using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ServiceOrderEntry;

internal static class EventScopeRules
{
    public static bool IsAllowed(int eventId, IReadOnlySet<int> enabledEvents, bool individualExhibitorRun) =>
        individualExhibitorRun || enabledEvents.Contains(eventId);
}

internal static class TextRules
{
    public static string Clean(string? value) => value?.Trim() ?? "";
    public static bool Same(string? left, string? right) => string.Equals(Clean(left), Clean(right), StringComparison.OrdinalIgnoreCase);
    public static string NormalizeEmail(string? value) => Clean(value).ToLowerInvariant();
    public static string NormalizeCompany(string? value)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var rune in Clean(value).Normalize(NormalizationForm.FormC).ToLowerInvariant().EnumerateRunes())
        {
            var mark = Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune) || mark) word.Append(rune.ToString());
            else if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
        }
        if (word.Length > 0) words.Add(word.ToString());
        var suffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "inc", "incorporated", "corp", "corporation", "company", "co", "llc", "ltd", "limited", "plc", "lp", "llp"
        };
        while (words.Count > 0 && suffixes.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        return words.Any(x => x.EnumerateRunes().Any(r => Rune.IsLetter(r) || Rune.IsNumber(r))) ? string.Join(" ", words) : "";
    }
    public static bool CompanyMatches(string? left, string? right) =>
        NormalizeCompany(left) is { Length: > 0 } identity && identity == NormalizeCompany(right);
    public static string MomentusCountry(string? value) => Clean(value).ToUpperInvariant() switch
    {
        "USA" or "U.S.A." or "US" or "UNITED STATES" or "UNITED STATES OF AMERICA" => "***",
        var country => country
    };
    public static bool AddressMatches(AccountInfo account, BillingRequest request) =>
        Same(account.Address, request.Address) && Same(account.City, request.City) && Same(account.State, request.State) &&
        Same(account.PostalCode, request.PostalCode) && Same(MomentusCountry(account.Country), MomentusCountry(request.Country));
    public static string OData(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

internal static class SalesRepRules
{
    public static string Resolve(string? udfValue, string? exhibitorSalesperson, IEnumerable<SalesRepLookup> rows)
    {
        var raw = TextRules.Clean(udfValue);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var matches = rows.Where(x => TextRules.Same(x.DisplayName, raw) || TextRules.Same(x.UdfCode, raw))
                .Select(x => TextRules.Clean(x.AccountCode)).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return matches.Count == 1 ? matches[0] : "";
        }
        return TextRules.Clean(exhibitorSalesperson);
    }
}

internal static class BillingRules
{
    public static bool IsComplete(BillingRequest request) => RequestedErrors(request, new()).Count == 0;

    public static bool ValidCode(string? code) => !string.IsNullOrWhiteSpace(code) && !code.Any(char.IsControl);
    public static bool ValidEmail(string? email)
    {
        var value = TextRules.Clean(email);
        return !value.Any(char.IsWhiteSpace) && System.Net.Mail.MailAddress.TryCreate(value, out var parsed) &&
            parsed.Address == value && parsed.Host.Contains('.') && !parsed.Host.StartsWith('.') && !parsed.Host.EndsWith('.');
    }

    public static List<string> AddressErrors(AccountInfo account)
    {
        var errors = new List<string>();
        if (new[] { account.Address, account.City, account.PostalCode, account.Country }.Any(string.IsNullOrWhiteSpace)) errors.Add("Effective billing street/city/postal/country is incomplete.");
        if (account.Country != "***" && !Regex.IsMatch(account.Country, @"^[\p{L}][\p{L}\p{M} .'-]+$")) errors.Add("Billing country is invalid.");
        // Preserve the documented required address fields; the repository has no country/state lookup.
        if (new[] { account.Address, account.City, account.State, account.PostalCode, account.Country }.Any(x => x.Any(char.IsControl))) errors.Add("Billing address contains invalid control characters.");
        return errors;
    }

    public static AccountInfo RequestedAccount(BillingRequest r) => RunRow.EmptyAccount with
    { AccountClass = "O", Company = r.CompanyName, Address = r.Address, City = r.City, State = r.State, PostalCode = r.PostalCode, Country = TextRules.MomentusCountry(r.Country) };
    public static AccountInfo RequestedContact(BillingRequest r) => RunRow.EmptyAccount with
    { AccountClass = "P", FirstName = r.FirstName, LastName = r.LastName, Email = TextRules.NormalizeEmail(r.Email) };

    public static List<string> RequestedErrors(BillingRequest request, BillingConfiguration config)
    {
        var errors = new List<string>();
        var selector = config.Selector(request);
        if (selector == "ABOVE") return errors;
        if (selector == "UNKNOWN") return ["Unknown Bill-To selector; REVIEW."];
        if (TextRules.NormalizeCompany(request.CompanyName).Length == 0) errors.Add("Requested company has no usable identity.");
        if (new[] { request.CompanyName, request.FirstName, request.LastName }.Any(x => x.Any(char.IsControl))) errors.Add("Requested company/contact name contains invalid control characters.");
        if (new[] { request.FirstName, request.LastName }.Any(string.IsNullOrWhiteSpace)) errors.Add("Requested billing contact name is incomplete.");
        if (!ValidEmail(request.Email)) errors.Add("Requested billing email is invalid.");
        errors.AddRange(AddressErrors(RequestedAccount(request)));
        return errors;
    }

    public static List<string> EffectiveErrors(RunRow row, bool requireIds = false)
    {
        var errors = new List<string>();
        if (row.BillingVersion != 1 || row.EffectiveBilling is null) return ["Effective billing state is unavailable; legacy plan requires REVIEW."];
        var state = row.EffectiveBilling;
        if (state.Account.AccountClass != "O") errors.Add("Effective Bill-To account is not an organization account.");
        if (TextRules.NormalizeCompany(state.Account.Company).Length == 0) errors.Add("Effective company has no usable identity.");
        if (new[] { state.Account.Company, state.Contact.FirstName, state.Contact.LastName }.Any(x => x.Any(char.IsControl))) errors.Add("Effective company/contact name contains invalid control characters.");
        errors.AddRange(AddressErrors(state.Account));
        if (new[] { state.Contact.FirstName, state.Contact.LastName }.Any(string.IsNullOrWhiteSpace)) errors.Add("Effective billing contact name is incomplete.");
        if (!ValidEmail(state.Contact.Email)) errors.Add("Effective billing email is invalid.");
        if (state.Contact.AccountClass != "P") errors.Add("Effective Bill-To contact is not a contact account.");
        if (!state.ContactCreationPending && !TextRules.Same(state.Contact.PrimaryAccount, state.Account.AccountCode)) errors.Add("Effective contact does not belong to the intended Bill-To account.");
        if (!state.AboveAddress && TextRules.NormalizeEmail(state.Contact.Email) != TextRules.NormalizeEmail(row.RequestedBilling.Email)) errors.Add("Effective billing email differs from the separate Bill-To request.");
        if (requireIds || !state.AccountCreationPending)
            if (!ValidCode(state.Account.AccountCode) || !TextRules.Same(row.FinalBillToAccount, state.Account.AccountCode)) errors.Add("Effective account identity is invalid.");
        if (requireIds || !state.ContactCreationPending)
            if (!ValidCode(state.Contact.AccountCode) || !TextRules.Same(row.FinalBillToContact, state.Contact.AccountCode)) errors.Add("Effective contact identity is invalid.");
        return errors;
    }
}

internal sealed record CategoryDecision(int? Sequence, string Description, string ItemIdentifier, string Message);

internal static class CategoryRules
{
    public static CategoryDecision Resolve(IEnumerable<OrderItemInfo> items, IEnumerable<CategoryLookup> lookups)
    {
        var matches = new List<(CategoryLookup Lookup, OrderItemInfo Item, string Identifier)>();
        foreach (var item in items.Where(x => x.SearchText.Contains("package", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var lookup in lookups)
            {
                foreach (var identifier in lookup.Identifiers.Where(x => x.Length > 0))
                {
                    if (item.SearchText.Contains(identifier, StringComparison.OrdinalIgnoreCase))
                        matches.Add((lookup, item, identifier));
                }
            }
        }

        var categories = matches.GroupBy(x => x.Lookup.Sequence).ToList();
        if (categories.Count == 0) return new(null, "", "", "Order Category could not be determined from package lines.");
        if (categories.Count > 1)
            return new(null, "", string.Join(" | ", matches.Select(x => x.Item.Identifier).Distinct()), "Multiple Order Categories matched package lines.");
        var chosen = categories[0].OrderByDescending(x => x.Identifier.Length).First();
        return new(chosen.Lookup.Sequence, chosen.Lookup.Description, chosen.Item.Identifier, "");
    }
}

internal sealed record ExhibitorCategoryDecision(IReadOnlyList<int> Existing, IReadOnlyList<int> Add, IReadOnlyList<int> Remove, IReadOnlyList<int> Final, bool ApprovalNeeded);

internal static class ExhibitorCategoryRules
{
    public const int ApprovalNeeded = 102;
    public const int Hold = 103;
    private static readonly HashSet<int> ManagedCodes = [1, 2, 3, 5, 9, 22, 65, 66, 88];

    public static ExhibitorCategoryDecision Resolve(string? existingValue, string? orderCategoryName, IEnumerable<OrderItemInfo> items, string? statePavilionUdf)
    {
        var existing = Parse(existingValue);
        var desired = new HashSet<int>();
        var category = TextRules.Clean(orderCategoryName);
        var lineText = string.Join(" | ", items.Select(x => x.SearchText));
        var customBuild = Contains(category, "custom build") || Contains(lineText, "custom build");
        var spaceOnly = Contains(category, "space only");

        if (customBuild) desired.Add(3);
        if (Contains(category, "kiosk")) desired.Add(9);
        if (Contains(category, "turnkey")) desired.Add(1);
        if (spaceOnly) desired.Add(2);
        if (customBuild && spaceOnly) desired.Add(65);
        if (Contains(category, "trade accelerator")) desired.Add(88);
        if (Contains(category, "trade mission")) desired.Add(22);
        if (Contains(category, "sponsor") || Contains(lineText, "sponsor")) desired.Add(5);
        if (TextRules.Same(statePavilionUdf, "Y") || TextRules.Same(statePavilionUdf, "Yes")) desired.Add(66);

        var add = desired.Except(existing).Order().ToList();
        var remove = existing.Where(x => ManagedCodes.Contains(x) && !desired.Contains(x)).Order().ToList();
        var final = existing.Except(remove).Concat(add).Distinct().Order().ToList();
        return new(existing, add, remove, final, existing.Contains(ApprovalNeeded));
    }

    public static string Format(IEnumerable<int> values) => string.Join(",", values.Distinct().Order());
    public static bool HasCode(string? value, int code) => Parse(value).Contains(code);
    private static List<int> Parse(string? value) => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : 0)
        .Where(x => x > 0).Distinct().Order().ToList();
    private static bool Contains(string? value, string phrase) => (value ?? "").Contains(phrase, StringComparison.OrdinalIgnoreCase);
}

internal static partial class BoothRules
{
    [GeneratedRegex(@"\baccepted\s+booth\s+(?<booth>.+?)\s*,\s*and\s+had\s+these\s+comments\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex AcceptedBoothPattern();

    public static string Parse(string? activityText)
    {
        var match = AcceptedBoothPattern().Match(activityText ?? "");
        if (!match.Success) return "";
        var value = Regex.Replace(match.Groups["booth"].Value.Trim(), @"\s+", " ");
        value = string.Join(",", value.Split(',', StringSplitOptions.TrimEntries));
        return IsSafe(value) ? value : "";
    }

    private static bool IsSafe(string value) => value.Length is > 0 and <= 100 && value.Split(',').All(x => x.Length > 0) &&
        value.All(c => char.IsLetterOrDigit(c) || c is '-' or '/' or '.' or ' ' or ',' or '&' or '+');
}

internal static class ValidationRules
{
    public static (string Status, string Message) Validate(RunRow row)
    {
        var messages = new List<string>();
        if (row.EventId <= 0) messages.Add("EventID is missing.");
        if (row.ExhibitorId <= 0) messages.Add("ExhibitorID is missing.");
        if (row.OrderNumber <= 0) messages.Add("Order Number is missing.");
        if (string.IsNullOrWhiteSpace(row.ProposedOrderAccountRep)) messages.Add("Sales Rep lookup failed.");
        if (!row.ProposedCategory.HasValue || row.ProposedCategory <= 0 || !row.ValidCategoryIds.Contains(row.ProposedCategory.Value)) messages.Add("Order Category is missing or not a validated lookup identifier.");
        if (string.IsNullOrWhiteSpace(row.ProposedBoothNumber)) messages.Add("Booth number could not be determined.");
        if (string.IsNullOrWhiteSpace(row.FinalBillToAccount) && !TextRules.Same(row.BillToAddressAction, "CREATE RELATED BILL-TO ACCOUNT")) messages.Add("BillToAccount is missing.");
        if (string.IsNullOrWhiteSpace(row.FinalBillToContact) && !TextRules.Same(row.ContactAction, "CREATE CONTACT")) messages.Add("BillToContact could not be determined.");
        if (string.IsNullOrWhiteSpace(row.BillToAddressAction)) messages.Add("Billing address decision could not be determined.");
        if (string.IsNullOrWhiteSpace(row.PaymentScheduleText)) messages.Add("Payment Schedule text could not be found in a Contract PDF.");
        if (TextRules.Same(row.PaymentScheduleNoteAction, "REVIEW")) messages.Add("Multiple SON order notes already exist.");
        if (row.BillToAddressAction.Contains("ACCOUNT", StringComparison.OrdinalIgnoreCase))
        {
            if (new[] { row.FinalAddress, row.FinalCity, row.FinalPostalCode, row.FinalCountry }.Any(string.IsNullOrWhiteSpace))
                messages.Add("Requested billing address is incomplete.");
        }
        if (!string.IsNullOrWhiteSpace(row.AccountDecisionMessage)) messages.Add(row.AccountDecisionMessage);
        if (!string.IsNullOrWhiteSpace(row.ContactDecisionMessage)) messages.Add(row.ContactDecisionMessage);
        if (!string.IsNullOrWhiteSpace(row.PaymentScheduleDecisionMessage)) messages.Add(row.PaymentScheduleDecisionMessage);
        if (row.BillToAddressAction.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)) messages.Add("Shared account address updates are not authorized.");
        messages.AddRange(BillingRules.EffectiveErrors(row));
        return messages.Count == 0 ? (row.EffectiveBilling!.AccountCreationPending || row.EffectiveBilling.ContactCreationPending ? "PREPARED" : "READY", "") : ("REVIEW", string.Join(" ", messages.Distinct()));
    }
}
