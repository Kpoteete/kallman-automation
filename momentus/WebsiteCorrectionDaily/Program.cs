using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Options;

class Program
{
    // ====== MOMENTUS / SDK CONFIG ======
    private const string UngerboeckUri = "https://kallman.ungerboeck.com/prod";
    private const string OrgCode = "10";

    private static readonly string ApiUserId =
        Environment.GetEnvironmentVariable("MOMENTUS_APIUSER")?.Trim() ?? "";

    private static readonly string Secret =
        Environment.GetEnvironmentVariable("MOMENTUS_SECRET")?.Trim() ?? "";

    private static readonly string Key =
        Environment.GetEnvironmentVariable("MOMENTUS_KEY")?.Trim() ?? "";

    private const string AccountChangedDateFieldName = "ChangedOn";
    private const string AccountClassFieldName = "Class";
    private const string AccountClassValue = "Organization";

    static int Main(string[] args)
    {
        bool applyChanges = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
        Console.WriteLine(applyChanges
            ? "LIVE MODE: approved website corrections will be written to Momentus."
            : "DRY RUN: no Momentus records will be changed. Pass --apply to write changes.");
        Console.WriteLine("Initializing Momentus API Client...");

        if (string.IsNullOrWhiteSpace(ApiUserId) || string.IsNullOrWhiteSpace(Secret) || string.IsNullOrWhiteSpace(Key))
        {
            Console.WriteLine("ERROR: Momentus API credentials are missing.");
            Console.WriteLine("Set MOMENTUS_APIUSER, MOMENTUS_SECRET, and MOMENTUS_KEY environment variables.");
            return 1;
        }

        var client = BuildClient();

        DateTime changedSinceUtc = DateTime.UtcNow.AddDays(-1);

        // Edm.DateTime filter format: no Z, no milliseconds
        string changedSinceText = changedSinceUtc.ToString("yyyy-MM-ddTHH:mm:ss");

        Console.WriteLine($"Looking for Organization accounts changed since: {changedSinceText}");
        Console.WriteLine("This checks all changed Organization accounts, then cleans website values only when needed.");
        Console.WriteLine();

        int accountsChecked = 0;
        int updatedCount = 0;
        int skippedNoWebsite = 0;
        int skippedAlreadyClean = 0;
        int errorCount = 0;

        var logRows = new List<string>();
        logRows.Add("RunDateUtc,AccountCode,AccountName,Class,OldWebsite,NewWebsite,Status,Message");

        try
        {
            string searchOData =
                $"{AccountChangedDateFieldName} ge datetime'{changedSinceText}' and {AccountClassFieldName} eq '{AccountClassValue}'";

            Console.WriteLine($"Search OData: {searchOData}");
            Console.WriteLine();

            var options = new Search
            {
                PageSize = 1000,
                MaxResults = 100000
            };

            var response = client.Endpoints.Accounts.Search(OrgCode, searchOData, options);

            if (response == null || response.Results == null)
            {
                Console.WriteLine("No accounts returned from Momentus.");
                return 0;
            }

            var accounts = response.Results.ToList();

            Console.WriteLine($"Organization accounts changed in the last 1 day: {accounts.Count}");
            Console.WriteLine("Checking website values...");
            Console.WriteLine();

            foreach (var account in accounts)
            {
                accountsChecked++;

                string accountCode = account.AccountCode ?? "";
                string accountName = account.Name ?? "";
                string accountClass = account.Class ?? "";
                string oldWebsite = account.Website ?? "";

                try
                {
                    if (string.IsNullOrWhiteSpace(oldWebsite))
                    {
                        skippedNoWebsite++;

                        logRows.Add(MakeCsvRow(
                            accountCode,
                            accountName,
                            accountClass,
                            oldWebsite,
                            "",
                            "Skipped",
                            "Website blank"
                        ));

                        continue;
                    }

                    string newWebsite = CleanWebsite(oldWebsite);

                    if (string.Equals(oldWebsite.Trim(), newWebsite, StringComparison.OrdinalIgnoreCase))
                    {
                        skippedAlreadyClean++;

                        logRows.Add(MakeCsvRow(
                            accountCode,
                            accountName,
                            accountClass,
                            oldWebsite,
                            newWebsite,
                            "Skipped",
                            "Website already clean"
                        ));

                        continue;
                    }

                    account.Website = newWebsite;

                    if (applyChanges)
                        client.Endpoints.Accounts.Update(account);

                    updatedCount++;

                    logRows.Add(MakeCsvRow(
                        accountCode,
                        accountName,
                        accountClass,
                        oldWebsite,
                        newWebsite,
                        "Updated",
                        "Website cleaned because Organization account was changed in the last 1 day"
                    ));

                    Console.WriteLine($"Updated {accountCode}: {oldWebsite} -> {newWebsite}");
                }
                catch (Exception ex)
                {
                    errorCount++;

                    logRows.Add(MakeCsvRow(
                        accountCode,
                        accountName,
                        accountClass,
                        oldWebsite,
                        "",
                        "Error",
                        ex.Message
                    ));

                    Console.WriteLine($"[ERROR] Account {accountCode}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Search failed.");
            Console.WriteLine(ex.Message);
            Console.WriteLine();

            Console.WriteLine("Search failed using:");
            Console.WriteLine($"{AccountChangedDateFieldName} ge datetime'{changedSinceText}' and {AccountClassFieldName} eq '{AccountClassValue}'");
            Console.WriteLine();

            Console.WriteLine("If this fails, the likely issue is that Class is not the searchable field name on the Accounts endpoint.");
            Console.WriteLine("Possible alternatives may be AccountClass, Type, or AccountType depending on the API model.");
            Console.WriteLine();

            return 1;
        }

        string logFileName = $"WebsiteCleanupLog_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        File.WriteAllLines(logFileName, logRows, Encoding.UTF8);

        Console.WriteLine();
        Console.WriteLine("================================");
        Console.WriteLine("PROCESS COMPLETE");
        Console.WriteLine($"Organization Accounts Checked:  {accountsChecked}");
        Console.WriteLine($"Websites Updated:               {updatedCount}");
        Console.WriteLine($"Skipped - Blank Website:        {skippedNoWebsite}");
        Console.WriteLine($"Skipped - Already Clean:        {skippedAlreadyClean}");
        Console.WriteLine($"Errors:                         {errorCount}");
        Console.WriteLine($"Log File:                       {logFileName}");
        Console.WriteLine("================================");
        return errorCount == 0 ? 0 : 1;
    }

    private static string CleanWebsite(string website)
    {
        if (string.IsNullOrWhiteSpace(website))
            return "";

        string cleaned = website.Trim();

        cleaned = cleaned
            .Replace("\\", "/")
            .Trim();

        if (cleaned.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned.Substring("https://".Length);

        if (cleaned.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned.Substring("http://".Length);

        if (cleaned.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned.Substring("www.".Length);

        int slashIndex = cleaned.IndexOf('/');
        if (slashIndex >= 0)
            cleaned = cleaned.Substring(0, slashIndex);

        int questionIndex = cleaned.IndexOf('?');
        if (questionIndex >= 0)
            cleaned = cleaned.Substring(0, questionIndex);

        int hashIndex = cleaned.IndexOf('#');
        if (hashIndex >= 0)
            cleaned = cleaned.Substring(0, hashIndex);

        cleaned = cleaned.Trim().TrimEnd('.', '/');
        cleaned = cleaned.ToLowerInvariant();

        return cleaned;
    }

    private static string MakeCsvRow(
        string accountCode,
        string accountName,
        string accountClass,
        string oldWebsite,
        string newWebsite,
        string status,
        string message)
    {
        return string.Join(",",
            Csv(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")),
            Csv(accountCode),
            Csv(accountName),
            Csv(accountClass),
            Csv(oldWebsite),
            Csv(newWebsite),
            Csv(status),
            Csv(message)
        );
    }

    private static string Csv(string value)
    {
        if (value == null)
            value = "";

        value = value.Replace("\"", "\"\"");
        return $"\"{value}\"";
    }

    private static ApiClient BuildClient()
    {
        var auth = new Jwt
        {
            APIUserID = ApiUserId,
            Secret = Secret,
            Key = Key,
            UngerboeckURI = UngerboeckUri,
            AutoRefresh = new AutoRefresh()
        };

        return new ApiClient(auth);
    }
}
