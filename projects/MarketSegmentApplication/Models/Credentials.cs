namespace MarketSegmentApplication.Models;

public sealed record MomentusCredentials(string ApiUser, string Secret, string Key)
{
    public static MomentusCredentials FromEnvironment()
    {
        string? apiUser = Environment.GetEnvironmentVariable("MOMENTUS_APIUSER");
        string? secret = Environment.GetEnvironmentVariable("MOMENTUS_SECRET");
        string? key = Environment.GetEnvironmentVariable("MOMENTUS_KEY");

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(apiUser)) missing.Add("MOMENTUS_APIUSER");
        if (string.IsNullOrWhiteSpace(secret)) missing.Add("MOMENTUS_SECRET");
        if (string.IsNullOrWhiteSpace(key)) missing.Add("MOMENTUS_KEY");

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Missing required environment variable(s): " + string.Join(", ", missing) +
                ". Stop. No API calls or imports will run until these are set.");
        }

        return new MomentusCredentials(apiUser!.Trim(), secret!.Trim(), key!.Trim());
    }
}
