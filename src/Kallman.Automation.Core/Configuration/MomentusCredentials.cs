namespace Kallman.Automation.Core.Configuration;

public sealed record MomentusCredentials(string ApiUserId, string Secret, string Key)
{
    public static MomentusCredentials FromEnvironment(string prefix = "MOMENTUS_")
    {
        string apiUser = Environment.GetEnvironmentVariable(prefix + "APIUSER")?.Trim() ?? "";
        string secret = Environment.GetEnvironmentVariable(prefix + "SECRET")?.Trim() ?? "";
        string key = Environment.GetEnvironmentVariable(prefix + "KEY")?.Trim() ?? "";

        var missing = new List<string>();
        if (apiUser.Length == 0) missing.Add(prefix + "APIUSER");
        if (secret.Length == 0) missing.Add(prefix + "SECRET");
        if (key.Length == 0) missing.Add(prefix + "KEY");
        if (missing.Count > 0)
            throw new InvalidOperationException(
                "Missing required environment variable(s): " + string.Join(", ", missing));

        return new MomentusCredentials(apiUser, secret, key);
    }
}
