using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;

class Program
{
    // ====== MOMENTUS / SDK CONFIG ======
    private const string UngerboeckUri = "https://kallman.ungerboeck.com/prod";
    private const string OrgCode = "10";

    private static readonly string ApiUserId =
        Environment.GetEnvironmentVariable("MOMENTUS_APIUSER") ?? "KYLEPAPI";

    private static readonly string Secret =
        Environment.GetEnvironmentVariable("MOMENTUS_SECRET") ?? "8c247eb8-2342-452a-95c3-cf22bd1c6a56";

    private static readonly string Key =
        Environment.GetEnvironmentVariable("MOMENTUS_KEY") ?? "e2b97782-08d7-40f3-bdbc-fbef5095154c";

    private const string ChangedDateFieldName = "ChangedOn";

    static int Main()
    {
        try
        {
            Console.WriteLine("Initializing Momentus API Client...");

            if (Secret == "PASTE_SECRET_HERE" || Key == "PASTE_KEY_HERE")
            {
                Console.WriteLine("ERROR: API Secret/Key are missing.");
                Console.WriteLine("Set MOMENTUS_SECRET and MOMENTUS_KEY environment variables, or paste them into Program.cs locally.");
                return 1;
            }

            var client = BuildClient();

            DateTime changedSinceUtc = DateTime.UtcNow.AddDays(-1);

            // Momentus Edm.DateTime filter format.
            // Example: ChangedOn ge datetime'2026-05-18T15:54:28'
            string changedSinceText = changedSinceUtc.ToString("yyyy-MM-ddTHH:mm:ss");

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            Console.WriteLine($"Looking for records changed since: {changedSinceText}");
            Console.WriteLine();

            RunAccountNamePunctuationCleanup(client, changedSinceText, timestamp);

            Console.WriteLine();
            Console.WriteLine("--------------------------------");
            Console.WriteLine();

            RunContactEmailCleanup(client, changedSinceText, timestamp);

            Console.WriteLine();
            Console.WriteLine("================================");
            Console.WriteLine("ALL CLEANUP JOBS COMPLETE");
            Console.WriteLine("================================");

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR");
            Console.WriteLine(ex);
            return 1;
        }
    }

    // ============================================================
    // JOB 1: ORGANIZATION ACCOUNT NAME PUNCTUATION CLEANUP
    // ============================================================

    private static void RunAccountNamePunctuationCleanup(ApiClient client, string changedSinceText, string timestamp)
    {
        Console.WriteLine("JOB 1: Account name punctuation cleanup");
        Console.WriteLine("Searching organization account records changed in the last 1 day.");
        Console.WriteLine();

        int accountsChecked = 0;
        int updatedCount = 0;
        int skippedBlankName = 0;
        int skippedAlreadyClean = 0;
        int flaggedForReview = 0;
        int errorCount = 0;

        var runLogRows = new List<string>();
        runLogRows.Add("RunDateUtc,AccountCode,OldName,NewName,Status,Message");

        var reviewRows = new List<string>();
        reviewRows.Add("RunDateUtc,AccountCode,CurrentName,ReviewReason");

        try
        {
            string searchOData =
                $"{ChangedDateFieldName} ge datetime'{changedSinceText}' " +
                $"and Class eq '{USISDKConstants.AccountClass.Account}'";

            Console.WriteLine($"Search OData: {searchOData}");
            Console.WriteLine();

            SearchResponse<AllAccountsModel> response =
                client.Endpoints.Accounts.Search(OrgCode, searchOData);

            var accounts = response?.Results?.ToList() ?? new List<AllAccountsModel>();

            Console.WriteLine($"Organization accounts found: {accounts.Count}");
            Console.WriteLine();

            foreach (var account in accounts)
            {
                accountsChecked++;

                string accountCode = account.AccountCode ?? "";
                string oldName = account.Name ?? "";

                try
                {
                    if (string.IsNullOrWhiteSpace(oldName))
                    {
                        skippedBlankName++;

                        runLogRows.Add(MakeCsvRow(
                            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                            accountCode,
                            oldName,
                            "",
                            "Skipped",
                            "Name blank"
                        ));

                        continue;
                    }

                    List<string> reviewReasons = GetAccountNameReviewReasons(oldName);

                    if (reviewReasons.Any())
                    {
                        flaggedForReview++;

                        reviewRows.Add(MakeCsvRow(
                            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                            accountCode,
                            oldName,
                            string.Join(" | ", reviewReasons)
                        ));
                    }

                    string newName = CleanAccountNamePunctuation(oldName);

                    if (string.Equals(oldName, newName, StringComparison.Ordinal))
                    {
                        skippedAlreadyClean++;

                        runLogRows.Add(MakeCsvRow(
                            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                            accountCode,
                            oldName,
                            newName,
                            "Skipped",
                            "Name punctuation already clean"
                        ));

                        continue;
                    }

                    account.Name = newName;

                    client.Endpoints.Accounts.Update(account);

                    updatedCount++;

                    runLogRows.Add(MakeCsvRow(
                        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                        accountCode,
                        oldName,
                        newName,
                        "Updated",
                        "Account name punctuation cleaned"
                    ));

                    Console.WriteLine($"Updated account {accountCode}: {oldName} -> {newName}");
                }
                catch (Exception ex)
                {
                    errorCount++;

                    runLogRows.Add(MakeCsvRow(
                        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                        accountCode,
                        oldName,
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
            Console.WriteLine("Account name punctuation cleanup search failed.");
            Console.WriteLine(ex.Message);
            return;
        }

        string runLogFileName = $"AccountNamePunctuationCleanupLog_{timestamp}.csv";
        string reviewFileName = $"AccountNamePunctuationReview_{timestamp}.csv";

        File.WriteAllLines(runLogFileName, runLogRows, Encoding.UTF8);
        File.WriteAllLines(reviewFileName, reviewRows, Encoding.UTF8);

        Console.WriteLine();
        Console.WriteLine("JOB 1 COMPLETE");
        Console.WriteLine($"Organization Accounts Checked: {accountsChecked}");
        Console.WriteLine($"Names Updated:                 {updatedCount}");
        Console.WriteLine($"Flagged For Review:            {flaggedForReview}");
        Console.WriteLine($"Skipped - Blank Name:          {skippedBlankName}");
        Console.WriteLine($"Skipped - Already Clean:       {skippedAlreadyClean}");
        Console.WriteLine($"Errors:                        {errorCount}");
        Console.WriteLine($"Run Log File:                  {runLogFileName}");
        Console.WriteLine($"Review File:                   {reviewFileName}");
    }

    private static string CleanAccountNamePunctuation(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        string cleaned = name.Trim();

        // Normalize smart quotes.
        cleaned = cleaned.Replace("’", "'");
        cleaned = cleaned.Replace("‘", "'");
        cleaned = cleaned.Replace("“", "\"");
        cleaned = cleaned.Replace("”", "\"");

        // Normalize long dashes.
        cleaned = cleaned.Replace("–", "-");
        cleaned = cleaned.Replace("—", "-");

        // Remove repeated spaces.
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");

        // Remove spaces before punctuation.
        cleaned = Regex.Replace(cleaned, @"\s+([,.;:])", "$1");

        // Add one space after comma/semicolon/colon if missing.
        cleaned = Regex.Replace(cleaned, @"([,;:])(?=\S)", "$1 ");

        // Clean repeated punctuation.
        cleaned = Regex.Replace(cleaned, @",{2,}", ",");
        cleaned = Regex.Replace(cleaned, @"\.{3,}", "...");
        cleaned = Regex.Replace(cleaned, @";{2,}", ";");
        cleaned = Regex.Replace(cleaned, @":{2,}", ":");

        // Remove trailing punctuation that is usually bad data.
        cleaned = Regex.Replace(cleaned, @"[\s,;:/\\\-]+$", "");

        // Remove trailing single period.
        cleaned = Regex.Replace(cleaned, @"\.$", "");

        // Final cleanup.
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();

        return cleaned;
    }

    private static List<string> GetAccountNameReviewReasons(string name)
    {
        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(name))
            return reasons;

        string trimmed = name.Trim();

        if (Regex.IsMatch(trimmed, @"\([^)]+\)"))
            reasons.Add("Contains parentheses");

        if (Regex.IsMatch(trimmed, @"^[A-Z0-9\s\.,&'\-\/]+$") &&
            trimmed.Any(char.IsLetter) &&
            trimmed.Length > 6)
        {
            reasons.Add("Name appears to be all caps");
        }

        if (Regex.IsMatch(trimmed, @"[^\w\s\.,&'""/\-()]+"))
            reasons.Add("Contains unusual characters");

        if (trimmed.Length <= 2)
            reasons.Add("Name is very short");

        if (Regex.IsMatch(trimmed, @"\btest\b", RegexOptions.IgnoreCase))
            reasons.Add("Name contains possible test value");

        return reasons;
    }

    // ============================================================
    // JOB 2: CONTACT EMAIL CLEANUP
    // ============================================================

    private static void RunContactEmailCleanup(ApiClient client, string changedSinceText, string timestamp)
    {
        Console.WriteLine("JOB 2: Contact email cleanup");
        Console.WriteLine("Searching contact records changed in the last 1 day.");
        Console.WriteLine("This uses Accounts endpoint with Class = Contact.");
        Console.WriteLine();

        int contactsChecked = 0;
        int updatedCount = 0;
        int skippedBlankEmail = 0;
        int skippedAlreadyClean = 0;
        int flaggedForReview = 0;
        int skippedReviewOnly = 0;
        int errorCount = 0;

        var runLogRows = new List<string>();
        runLogRows.Add("RunDateUtc,AccountCode,Name,OldEmail,NewEmail,Status,Message");

        var reviewRows = new List<string>();
        reviewRows.Add("RunDateUtc,AccountCode,Name,CurrentEmail,ReviewReason");

        try
        {
            string searchOData =
                $"{ChangedDateFieldName} ge datetime'{changedSinceText}' " +
                $"and Class eq '{USISDKConstants.AccountClass.Contact}'";

            Console.WriteLine($"Search OData: {searchOData}");
            Console.WriteLine();

            SearchResponse<AllAccountsModel> response =
                client.Endpoints.Accounts.Search(OrgCode, searchOData);

            var contacts = response?.Results?.ToList() ?? new List<AllAccountsModel>();

            Console.WriteLine($"Contact records found: {contacts.Count}");
            Console.WriteLine();

            foreach (var contact in contacts)
            {
                contactsChecked++;

                string accountCode = contact.AccountCode ?? "";
                string firstName = contact.FirstName ?? "";
                string lastName = contact.LastName ?? "";
                string name = $"{firstName} {lastName}".Trim();

                if (string.IsNullOrWhiteSpace(name))
                    name = contact.Name ?? "";

                string oldEmail = contact.Email ?? "";

                try
                {
                    if (string.IsNullOrWhiteSpace(oldEmail))
                    {
                        skippedBlankEmail++;

                        runLogRows.Add(MakeCsvRow(
                            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                            accountCode,
                            name,
                            oldEmail,
                            "",
                            "Skipped",
                            "Email blank"
                        ));

                        continue;
                    }

                    string newEmail = CleanEmailAddress(oldEmail);
                    List<string> reviewReasons = GetEmailReviewReasons(oldEmail, newEmail);

                    if (reviewReasons.Any())
                    {
                        flaggedForReview++;

                        reviewRows.Add(MakeCsvRow(
                            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                            accountCode,
                            name,
                            oldEmail,
                            string.Join(" | ", reviewReasons)
                        ));
                    }

                    bool reviewOnly = IsEmailReviewOnly(oldEmail, newEmail);

                    if (reviewOnly)
                    {
                        skippedReviewOnly++;

                        runLogRows.Add(MakeCsvRow(
                            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                            accountCode,
                            name,
                            oldEmail,
                            newEmail,
                            "Skipped",
                            "Email flagged for review only. Not automatically changed."
                        ));

                        continue;
                    }

                    if (string.Equals(oldEmail, newEmail, StringComparison.Ordinal))
                    {
                        skippedAlreadyClean++;

                        runLogRows.Add(MakeCsvRow(
                            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                            accountCode,
                            name,
                            oldEmail,
                            newEmail,
                            "Skipped",
                            "Email already clean"
                        ));

                        continue;
                    }

                    contact.Email = newEmail;

                    client.Endpoints.Accounts.Update(contact);

                    updatedCount++;

                    runLogRows.Add(MakeCsvRow(
                        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                        accountCode,
                        name,
                        oldEmail,
                        newEmail,
                        "Updated",
                        "Email address cleaned"
                    ));

                    Console.WriteLine($"Updated contact {accountCode}: {oldEmail} -> {newEmail}");
                }
                catch (Exception ex)
                {
                    errorCount++;

                    runLogRows.Add(MakeCsvRow(
                        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                        accountCode,
                        name,
                        oldEmail,
                        "",
                        "Error",
                        ex.Message
                    ));

                    Console.WriteLine($"[ERROR] Contact {accountCode}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Contact email cleanup search failed.");
            Console.WriteLine(ex.Message);
            return;
        }

        string runLogFileName = $"ContactEmailCleanupLog_{timestamp}.csv";
        string reviewFileName = $"ContactEmailReview_{timestamp}.csv";

        File.WriteAllLines(runLogFileName, runLogRows, Encoding.UTF8);
        File.WriteAllLines(reviewFileName, reviewRows, Encoding.UTF8);

        Console.WriteLine();
        Console.WriteLine("JOB 2 COMPLETE");
        Console.WriteLine($"Contacts Checked:          {contactsChecked}");
        Console.WriteLine($"Emails Updated:            {updatedCount}");
        Console.WriteLine($"Flagged For Review:        {flaggedForReview}");
        Console.WriteLine($"Skipped - Review Only:     {skippedReviewOnly}");
        Console.WriteLine($"Skipped - Blank Email:     {skippedBlankEmail}");
        Console.WriteLine($"Skipped - Already Clean:   {skippedAlreadyClean}");
        Console.WriteLine($"Errors:                    {errorCount}");
        Console.WriteLine($"Run Log File:              {runLogFileName}");
        Console.WriteLine($"Review File:               {reviewFileName}");
    }

    private static string CleanEmailAddress(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return "";

        string cleaned = email.Trim();

        // Remove mailto prefix.
        if (cleaned.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned.Substring("mailto:".Length).Trim();

        // If email is in format Name <person@company.com>, pull the email inside brackets.
        Match angleMatch = Regex.Match(cleaned, @"<([^<>@\s]+@[^<>@\s]+\.[^<>@\s]+)>");

        if (angleMatch.Success)
            cleaned = angleMatch.Groups[1].Value.Trim();

        // Remove wrapping quotes.
        cleaned = cleaned.Trim('"', '\'');

        // Remove trailing punctuation commonly pasted into fields.
        cleaned = cleaned.Trim().TrimEnd(';', ',', '.');

        // Remove spaces around the email.
        cleaned = cleaned.Trim();

        // Lowercase email.
        cleaned = cleaned.ToLowerInvariant();

        return cleaned;
    }

    private static List<string> GetEmailReviewReasons(string oldEmail, string cleanedEmail)
    {
        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(oldEmail))
            return reasons;

        if (oldEmail.Contains(";") || oldEmail.Contains(","))
            reasons.Add("May contain multiple emails or trailing punctuation");

        if (Regex.IsMatch(oldEmail.Trim(), @"\s") && !Regex.IsMatch(oldEmail, @"<[^<>]+@[^<>]+>"))
            reasons.Add("Contains spaces");

        if (!IsValidEmail(cleanedEmail))
            reasons.Add("Does not look like a valid email after cleanup");

        if (Regex.IsMatch(cleanedEmail, @"^(info|sales|admin|office|contact|support|marketing|hello|service|customerservice)@", RegexOptions.IgnoreCase))
            reasons.Add("Generic email address");

        if (cleanedEmail.EndsWith("@gmail.com") ||
            cleanedEmail.EndsWith("@yahoo.com") ||
            cleanedEmail.EndsWith("@hotmail.com") ||
            cleanedEmail.EndsWith("@outlook.com") ||
            cleanedEmail.EndsWith("@aol.com"))
        {
            reasons.Add("Personal email domain");
        }

        if (cleanedEmail.Contains("test") || cleanedEmail.Contains("example.com"))
            reasons.Add("Possible test/example email");

        return reasons;
    }

    private static bool IsEmailReviewOnly(string oldEmail, string cleanedEmail)
    {
        if (string.IsNullOrWhiteSpace(oldEmail))
            return false;

        // Do not auto-change invalid emails.
        if (!IsValidEmail(cleanedEmail))
            return true;

        // Do not auto-change possible multiple-email values.
        if (oldEmail.Contains(";") || oldEmail.Contains(","))
            return true;

        // Do not auto-change values with internal spaces unless it was a safe angle bracket format.
        bool hasSpaces = Regex.IsMatch(oldEmail.Trim(), @"\s");
        bool safeAngleBracketFormat = Regex.IsMatch(oldEmail, @"<[^<>@\s]+@[^<>@\s]+\.[^<>@\s]+>");

        if (hasSpaces && !safeAngleBracketFormat)
            return true;

        return false;
    }

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        string value = email.Trim();

        if (value.Contains(" "))
            return false;

        return Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.IgnoreCase);
    }

    // ============================================================
    // SHARED HELPERS
    // ============================================================

    private static string MakeCsvRow(params string[] values)
    {
        return string.Join(",", values.Select(Csv));
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