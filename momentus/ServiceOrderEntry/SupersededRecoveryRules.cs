using Ungerboeck.Api.Models.Subjects;

namespace ServiceOrderEntry;

internal static class SupersededRecoveryRules
{
    // Kyle explicitly requested only the newer Vave order on 2026-10-06.
    // Keep the abandoned placeholder as history; never extend this exception to dispatched work.
    public static bool IsVavePlaceholder(OrderEvidence record) => record.Identity ==
        new OrderIdentity("https://kallman.ungerboeck.com/prod", "10", 6246, 185082, 40414) && record.Stages.Count == 0 &&
        !record.OrderComplete && record.Handoff is null;

    public static bool Excludes(OrderEvidence record, IReadOnlyList<ServiceOrdersModel> completeOrders) => IsVavePlaceholder(record) &&
        !completeOrders.Any(x => x.OrderNumber == 40414) && completeOrders.Any(x => x.OrganizationCode == "10" &&
            x.Event == 6246 && x.Exhibitor == 185082 && x.OrderNumber == 40415 && !string.IsNullOrWhiteSpace(x.OrderStatus));

    public const string Message = "Older Vave order 40414 is superseded by 40415 as requested. The complete current order list confirms 40415 exists and 40414 is absent. Undispatched recovery history retained; no writes attempted for 40414.";
}
