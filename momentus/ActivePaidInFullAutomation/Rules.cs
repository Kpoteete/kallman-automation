namespace ActivePaidInFullAutomation;

public static class QualificationRules
{
    public const int ActiveExhibitorStatus = 2;
    public const int PaidInFullExhibitorStatus = 22;
    public const string ActiveOrderStatus = "A";

    public static readonly IReadOnlyDictionary<int, string> EligibleCategories = new Dictionary<int, string>
    {
        [39] = "Additional Booth", [25] = "Chalet", [21] = "Co-Exhibitor", [19] = "Commission on Direct Sales",
        [12] = "Executive Club Membership", [24] = "Executive Suites & Private Conference Rooms", [18] = "Kiosk",
        [16] = "Mini-Booth", [17] = "Partnership Agreement", [15] = "SME/SIDO/StartUp Zone", [27] = "Space Only",
        [30] = "Space Only - 2nd Story", [35] = "Split Order - Space Only", [34] = "Split Order - Turnkey",
        [22] = "Sponsorship", [37] = "Trade Accelerator", [38] = "Trade Mission", [28] = "Turnkey",
        [23] = "Walk the Show Membership"
    };

    public static QualificationDecision Evaluate(int? exhibitorStatus, string? exhibitorType, string? orderStatus, int? category, decimal orderedTotal, decimal netDue)
    {
        var sequence = category?.ToString() ?? "";
        if (exhibitorStatus != ActiveExhibitorStatus) return new(false, "Exhibitor status is not Active (2).", sequence, CategoryName(category));
        if (!string.Equals(exhibitorType?.Trim(), "ME", StringComparison.OrdinalIgnoreCase)) return new(false, "Exhibitor type is not Main Exhibitor (ME).", sequence, CategoryName(category));
        if (!string.Equals(orderStatus?.Trim(), ActiveOrderStatus, StringComparison.OrdinalIgnoreCase)) return new(false, "Order status is not active (A).", sequence, CategoryName(category));
        if (!category.HasValue || !EligibleCategories.ContainsKey(category.Value)) return new(false, "Order category is not in the approved category list.", sequence, "");
        if (orderedTotal <= 0m) return new(false, "Ordered total must be greater than zero.", sequence, CategoryName(category));
        if (netDue > 0m) return new(false, "Ordered net due is greater than zero.", sequence, CategoryName(category));
        return new(true, "Eligible category has ordered net due of zero or less.", sequence, CategoryName(category));
    }

    public static string CategoryName(int? category) => category.HasValue && EligibleCategories.TryGetValue(category.Value, out var name) ? name : "";
    public static string ODataCategoryFilter() => "(" + string.Join(" or ", EligibleCategories.Keys.Order().Select(x => $"Category eq {x}")) + ")";
}

public sealed record QualificationDecision(bool Qualifies, string Reason, string CategorySequence, string CategoryName);

public sealed record OrderPriorityInput(int OrderNumber, string? OrderStatus, string? BoothOrder, int? Category, decimal OrderedTotal, decimal NetDue);

public static class OrderPriorityRules
{
    public static int? SelectQualifyingOrder(IEnumerable<OrderPriorityInput> orders)
    {
        var eligible = orders.Where(x => string.Equals(x.OrderStatus?.Trim(), QualificationRules.ActiveOrderStatus, StringComparison.OrdinalIgnoreCase) &&
            x.Category.HasValue && QualificationRules.EligibleCategories.ContainsKey(x.Category.Value) && x.OrderedTotal > 0m).ToList();
        var boothOrders = eligible.Where(x => string.Equals(x.BoothOrder?.Trim(), "Y", StringComparison.OrdinalIgnoreCase)).ToList();
        if (boothOrders.Count > 0)
        {
            if (boothOrders.Any(x => x.NetDue > 0m)) return null;
            return boothOrders.OrderBy(x => x.OrderNumber).First().OrderNumber;
        }
        return eligible.Where(x => x.NetDue <= 0m).OrderBy(x => x.OrderNumber).Select(x => (int?)x.OrderNumber).FirstOrDefault();
    }
}

public static class ReportingWeek
{
    public static (DateOnly Start, DateOnly End) For(DateTime localDateTime)
    {
        var date = DateOnly.FromDateTime(localDateTime);
        var daysSinceSaturday = ((int)date.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7;
        var start = date.AddDays(-daysSinceSaturday);
        return (start, start.AddDays(6));
    }
}
