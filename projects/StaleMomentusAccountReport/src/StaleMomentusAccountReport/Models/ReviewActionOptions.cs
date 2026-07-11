namespace StaleMomentusAccountReport.Models;

public static class ReviewActionOptions
{
    public static readonly string[] ReviewStatuses =
    [
        "Needs Review",
        "Keep Active",
        "Merge",
        "Deactivate",
        "Convert Status",
        "Needs Research",
        "Do Not Touch"
    ];

    public static readonly string[] MomentusUpdatedOptions = ["No", "Yes"];
}
