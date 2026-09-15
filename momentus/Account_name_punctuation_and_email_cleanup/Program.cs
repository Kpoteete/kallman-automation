using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Options;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;

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

    private const string ChangedDateFieldName = "ChangedOn";
    private const int SearchPageSize = 1000;
    private const int SearchMaxResults = 100000;

    // Match only whole legal-entity designators at the end of a name. The list covers
    // common U.S. and international forms, including punctuation variants.
    private static readonly Regex LegalEntitySuffixRegex = new(
        @"(?:,\s*)?\b(?:
            L\.?L\.?C\.?|L\.?L\.?P\.?|L\.?L\.?L\.?P\.?|L\.?P\.?|P\.?L\.?C\.?|P\.?C\.?|P\.?A\.?|
            Inc\.?|Incorporated|Corp\.?|Corporation|Co\.?|Company|Ltd\.?|Limited|
            GmbH|A\.?G\.?|B\.?V\.?|N\.?V\.?|S\.?A\.?R\.?L\.?|S\.?A\.?|
            Pte\.?\s+Ltd\.?|Pty\.?\s+Ltd\.?|O\.?Y\.?|A\.?B\.?|A\/?S|ApS|K\.?K\.?
        )$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace);

    static int Main(string[] args)
    {
        try
        {
            bool applyChanges = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
            bool accountNamesOnly = args.Contains("--account-names-only", StringComparer.OrdinalIgnoreCase);
            bool websiteOnly = args.Contains("--website-only", StringComparer.OrdinalIgnoreCase);
            bool skipContactEmails = args.Contains("--skip-contact-emails", StringComparer.OrdinalIgnoreCase) || accountNamesOnly || websiteOnly;
            int bucketDays = ReadPositiveIntOption(args, "--bucket-days", 1);
            DateTime endUtc = ReadDateOption(args, "--end", DateTime.UtcNow.Date.AddDays(1));
            DateTime startUtc = ReadDateOption(args, "--start", endUtc.AddDays(-15));

            if (args.Contains("--all-accounts", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine("ERROR: --all-accounts is not supported for historical cleanup because it can exceed the API cap.");
                Console.WriteLine("Use --start YYYY-MM-DD --end YYYY-MM-DD --bucket-days N instead.");
                return 2;
            }

            if (startUtc >= endUtc)
            {
                Console.WriteLine("ERROR: --start must be earlier than --end.");
                return 2;
            }
            Console.WriteLine(applyChanges
                ? "LIVE MODE: approved cleanup changes will be written to Momentus."
                : "DRY RUN: no Momentus records will be changed. Pass --apply to write changes.");
            Console.WriteLine("Initializing Momentus API Client...");

            if (string.IsNullOrWhiteSpace(ApiUserId) || string.IsNullOrWhiteSpace(Secret) || string.IsNullOrWhiteSpace(Key))
            {
                Console.WriteLine("ERROR: Momentus API credentials are missing.");
                Console.WriteLine("Set MOMENTUS_APIUSER, MOMENTUS_SECRET, and MOMENTUS_KEY environment variables.");
                return 1;
            }

            var client = BuildClient();

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            Console.WriteLine($"Searching records changed from {startUtc:yyyy-MM-dd} through {endUtc:yyyy-MM-dd} UTC in {bucketDays}-day bucket(s).");
            Console.WriteLine();

            if (!websiteOnly)
                RunAccountNamePunctuationCleanup(client, startUtc, endUtc, bucketDays, timestamp, applyChanges);

            if (!accountNamesOnly)
                RunWebsiteCleanup(client, startUtc, endUtc, bucketDays, timestamp, applyChanges);

            Console.WriteLine();
            Console.WriteLine("--------------------------------");
            Console.WriteLine();

            if (skipContactEmails)
            {
                Console.WriteLine("JOB 3: Contact email cleanup skipped.");
            }
            else
            {
                RunContactEmailCleanup(client, startUtc, endUtc, bucketDays, timestamp, applyChanges);
            }

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
    // JOB 1: ORGANIZATION ACCOUNT NAME PUNCTUATION AND LEGAL-SUFFIX CLEANUP
    // ============================================================

    private static void RunAccountNamePunctuationCleanup(ApiClient client, DateTime startUtc, DateTime endUtc, int bucketDays, string timestamp, bool applyChanges)
    {
        Console.WriteLine("JOB 1: Account name punctuation and legal-suffix cleanup");
        Console.WriteLine("Searching organization account records in date buckets.");
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
            foreach (var (bucketStartUtc, bucketEndUtc) in GetDateBuckets(startUtc, endUtc, bucketDays))
            {
                string searchOData = $"{ChangedDateFieldName} ge datetime'{bucketStartUtc:yyyy-MM-ddTHH:mm:ss}' and {ChangedDateFieldName} lt datetime'{bucketEndUtc:yyyy-MM-ddTHH:mm:ss}' and Class eq '{USISDKConstants.AccountClass.Account}'";
                Console.WriteLine($"Name bucket: {bucketStartUtc:yyyy-MM-dd} through {bucketEndUtc:yyyy-MM-dd}");

                var searchOptions = new Search { PageSize = SearchPageSize, MaxResults = SearchMaxResults };
                SearchResponse<AllAccountsModel> response = client.Endpoints.Accounts.Search(OrgCode, searchOData, searchOptions);
                var accounts = response?.Results?.ToList() ?? new List<AllAccountsModel>();

                if (accounts.Count >= SearchMaxResults)
                    throw new InvalidOperationException($"Name bucket {bucketStartUtc:yyyy-MM-dd} returned {SearchMaxResults:N0} records and may be capped. Re-run this range with a smaller --bucket-days value.");

                Console.WriteLine($"Organization accounts found: {accounts.Count:N0}");

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

                        string newName = CleanAccountName(oldName);

                        if (string.Equals(oldName, newName, StringComparison.Ordinal))
                        {
                        skippedAlreadyClean++;

                        runLogRows.Add(MakeCsvRow(
                            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                            accountCode,
                            oldName,
                            newName,
                            "Skipped",
                            "Name punctuation and legal suffix already clean"
                        ));

                            continue;
                        }

                        account.Name = newName;

                        if (applyChanges)
                            client.Endpoints.Accounts.Update(account);

                        updatedCount++;

                    runLogRows.Add(MakeCsvRow(
                        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                        accountCode,
                        oldName,
                        newName,
                        "Updated",
                        "Account name punctuation and legal suffix cleaned"
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
        }
        catch (Exception ex)
        {
            Console.WriteLine("Account name punctuation and legal-suffix cleanup search failed.");
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

    private static string CleanAccountName(string name)
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

        // Strip one or more legal entity designators only when they appear at the end.
        // Examples: "Acme, Inc.", "Acme LLC", and "Acme LLC, Inc." become "Acme".
        string withoutSuffix;
        do
        {
            withoutSuffix = LegalEntitySuffixRegex.Replace(cleaned, "").Trim();
            withoutSuffix = Regex.Replace(withoutSuffix, @"[\s,;:/\\\-]+$", "").Trim();
            if (string.Equals(cleaned, withoutSuffix, StringComparison.Ordinal))
                break;

            cleaned = withoutSuffix;
        }
        while (!string.IsNullOrEmpty(cleaned));

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
    // JOB 2: ORGANIZATION WEBSITE CLEANUP
    // ============================================================

    private static void RunWebsiteCleanup(ApiClient client, DateTime startUtc, DateTime endUtc, int bucketDays, string timestamp, bool applyChanges)
    {
        Console.WriteLine("JOB 2: Organization website cleanup");

        int checkedCount = 0, updatedCount = 0, blankCount = 0, cleanCount = 0, errorCount = 0;
        var logRows = new List<string> { "RunDateUtc,AccountCode,AccountName,Class,OldWebsite,NewWebsite,Status,Message" };

        try
        {
            foreach (var (bucketStartUtc, bucketEndUtc) in GetDateBuckets(startUtc, endUtc, bucketDays))
            {
                string searchOData = $"{ChangedDateFieldName} ge datetime'{bucketStartUtc:yyyy-MM-ddTHH:mm:ss}' and {ChangedDateFieldName} lt datetime'{bucketEndUtc:yyyy-MM-ddTHH:mm:ss}' and Class eq '{USISDKConstants.AccountClass.Account}'";
                Console.WriteLine($"Website bucket: {bucketStartUtc:yyyy-MM-dd} through {bucketEndUtc:yyyy-MM-dd}");

                var options = new Search { PageSize = SearchPageSize, MaxResults = SearchMaxResults };
                var response = client.Endpoints.Accounts.Search(OrgCode, searchOData, options);
                var accounts = response?.Results?.ToList() ?? new List<AllAccountsModel>();

                if (accounts.Count >= SearchMaxResults)
                    throw new InvalidOperationException($"Website bucket {bucketStartUtc:yyyy-MM-dd} returned {SearchMaxResults:N0} records and may be capped. Re-run this range with a smaller --bucket-days value.");

                foreach (var account in accounts)
                {
                    checkedCount++;
                    string accountCode = account.AccountCode ?? "";
                    string accountName = account.Name ?? "";
                    string accountClass = account.Class ?? "";
                    string oldWebsite = account.Website ?? "";

                    try
                    {
                        if (string.IsNullOrWhiteSpace(oldWebsite))
                        {
                            blankCount++;
                            logRows.Add(MakeCsvRow(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"), accountCode, accountName, accountClass, oldWebsite, "", "Skipped", "Website blank"));
                            continue;
                        }

                        string newWebsite = CleanWebsite(oldWebsite);
                        if (string.Equals(oldWebsite.Trim(), newWebsite, StringComparison.OrdinalIgnoreCase))
                        {
                            cleanCount++;
                            logRows.Add(MakeCsvRow(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"), accountCode, accountName, accountClass, oldWebsite, newWebsite, "Skipped", "Website already clean"));
                            continue;
                        }

                        account.Website = newWebsite;
                        if (applyChanges)
                            client.Endpoints.Accounts.Update(account);

                        updatedCount++;
                        logRows.Add(MakeCsvRow(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"), accountCode, accountName, accountClass, oldWebsite, newWebsite, "Updated", "Website cleaned"));
                        Console.WriteLine($"Updated website {accountCode}: {oldWebsite} -> {newWebsite}");
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        logRows.Add(MakeCsvRow(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"), accountCode, accountName, accountClass, oldWebsite, "", "Error", ex.Message));
                        Console.WriteLine($"[ERROR] Website {accountCode}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Website cleanup search failed.");
            Console.WriteLine(ex.Message);
            return;
        }

        string logFileName = $"WebsiteCleanupLog_{timestamp}.csv";
        File.WriteAllLines(logFileName, logRows, Encoding.UTF8);
        Console.WriteLine($"Websites checked: {checkedCount}; updated: {updatedCount}; blank: {blankCount}; already clean: {cleanCount}; errors: {errorCount}");
        Console.WriteLine($"Website log file: {logFileName}");
    }

    private static string CleanWebsite(string website)
    {
        string cleaned = website.Trim().Replace("\\", "/");
        if (cleaned.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned["https://".Length..];
        if (cleaned.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned["http://".Length..];
        if (cleaned.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned["www.".Length..];
        int delimiter = cleaned.IndexOfAny(new[] { '/', '?', '#' });
        if (delimiter >= 0) cleaned = cleaned[..delimiter];
        return cleaned.Trim().TrimEnd('.', '/').ToLowerInvariant();
    }

    // ============================================================
    // JOB 3: CONTACT EMAIL CLEANUP
    // ============================================================

    private static void RunContactEmailCleanup(ApiClient client, DateTime startUtc, DateTime endUtc, int bucketDays, string timestamp, bool applyChanges)
    {
        Console.WriteLine("JOB 3: Contact email cleanup");
        Console.WriteLine("Searching contact records in date buckets.");
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
            var contacts = new List<AllAccountsModel>();

            foreach (var (bucketStartUtc, bucketEndUtc) in GetDateBuckets(startUtc, endUtc, bucketDays))
            {
                string searchOData = $"{ChangedDateFieldName} ge datetime'{bucketStartUtc:yyyy-MM-ddTHH:mm:ss}' and {ChangedDateFieldName} lt datetime'{bucketEndUtc:yyyy-MM-ddTHH:mm:ss}' and Class eq '{USISDKConstants.AccountClass.Contact}'";
                Console.WriteLine($"Email bucket: {bucketStartUtc:yyyy-MM-dd} through {bucketEndUtc:yyyy-MM-dd}");

                var searchOptions = new Search { PageSize = SearchPageSize, MaxResults = SearchMaxResults };
                SearchResponse<AllAccountsModel> response = client.Endpoints.Accounts.Search(OrgCode, searchOData, searchOptions);
                var bucketContacts = response?.Results?.ToList() ?? new List<AllAccountsModel>();

                if (bucketContacts.Count >= SearchMaxResults)
                    throw new InvalidOperationException($"Email bucket {bucketStartUtc:yyyy-MM-dd} returned {SearchMaxResults:N0} records and may be capped. Re-run this range with a smaller --bucket-days value.");

                contacts.AddRange(bucketContacts);
            }

            Console.WriteLine($"Contact records found: {contacts.Count:N0}");
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

                    if (applyChanges)
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

    private static IEnumerable<(DateTime StartUtc, DateTime EndUtc)> GetDateBuckets(DateTime startUtc, DateTime endUtc, int bucketDays)
    {
        for (DateTime bucketStart = startUtc; bucketStart < endUtc; bucketStart = bucketStart.AddDays(bucketDays))
            yield return (bucketStart, bucketStart.AddDays(bucketDays) < endUtc ? bucketStart.AddDays(bucketDays) : endUtc);
    }

    private static DateTime ReadDateOption(string[] args, string optionName, DateTime fallback)
    {
        int index = Array.FindIndex(args, argument => string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return fallback;
        if (index == args.Length - 1 || !DateTime.TryParseExact(args[index + 1], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out DateTime value))
            throw new ArgumentException($"{optionName} must be followed by a date in YYYY-MM-DD format.");
        return DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);
    }

    private static int ReadPositiveIntOption(string[] args, string optionName, int fallback)
    {
        int index = Array.FindIndex(args, argument => string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return fallback;
        if (index == args.Length - 1 || !int.TryParse(args[index + 1], out int value) || value < 1)
            throw new ArgumentException($"{optionName} must be followed by a positive whole number.");
        return value;
    }

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
