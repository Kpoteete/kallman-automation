using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Subjects;

class Program
{
    // ========================================================================
    // SECTION 1: CSV COLUMN MAP
    // ------------------------------------------------------------------------
    // This block is the single source of truth for the prepared import CSV.
    // The desktop launcher converts the raw Mailchimp export into this layout.
    // The raw source file is never edited.
    //
    // Prepared layout:
    //   A = Event ID
    //   B = Campaign (free-text note title supplied by the launcher)
    //   C = Salesperson
    //   D = Salesperson Account Code
    //   E = Click Type
    //   F = Open Type
    //   G = Email
    //   H = Clicks
    //   I = Opens
    //   J = First Name
    //   K = Last Name
    //   L = Account Code   (this is treated as the CONTACT account code)
    //   M = Campaign Sent Date (ISO yyyy-MM-dd, selected in the launcher)
    //   N = Campaign Emails Sent (total rows in the Mailchimp export)
    //   O = Campaign Response Percentage (whole-number percent)
    //
    // Important business rule carried forward from the original working file:
    //   The CSV account code is NOT assumed to be the exhibitor org account.
    //   It is treated as a CONTACT account code, and the program resolves that
    //   contact to its PrimaryAccount in Momentus.
    // ========================================================================
    private const string EVENT_ID_COLUMN = "A";
    private const string CAMPAIGN_TYPE_COLUMN = "B";
    private const string SALESPERSON_NAME_COLUMN = "C";
    private const string SALESPERSON_ACCOUNT_COLUMN = "D";
    private const string CLICK_TYPE_COLUMN = "E";
    private const string OPEN_TYPE_COLUMN = "F";
    private const string EMAIL_COLUMN = "G";
    private const string CLICKS_COLUMN = "H";
    private const string OPENS_COLUMN = "I";
    private const string FIRST_NAME_COLUMN = "J";
    private const string LAST_NAME_COLUMN = "K";
    private const string CONTACT_ACCOUNT_COLUMN = "L";
    private const string CAMPAIGN_SENT_DATE_COLUMN = "M";
    private const string CAMPAIGN_EMAILS_SENT_COLUMN = "N";
    private const string CAMPAIGN_RESPONSE_PERCENTAGE_COLUMN = "O";

    // ========================================================================
    // SECTION 2: STANDARDIZED RUNTIME SETTINGS
    // ========================================================================
    private static readonly bool CSV_HAS_HEADER =
        (Environment.GetEnvironmentVariable("CSV_HAS_HEADER") ?? "true")
        .Equals("true", StringComparison.OrdinalIgnoreCase);

    private const string ORG_CODE = "10";
    private const string MOMENTUS_URI = "https://kallman.ungerboeck.com/prod";
    private const bool DRY_RUN = false;
    private const string ACTIVITY_RECIPIENT = "SALESRE";
    private const string CAMPAIGN_DESIGNATION =
        Ungerboeck.Api.Models.USISDKConstants.AccountDesignations.EventSales;

    // ========================================================================
    // SECTION 3: DEFAULT EXHIBITOR VALUES
    // ========================================================================
    private const int DEFAULT_PIPE_STAGE_1 = 23;
    private const int DEFAULT_LEAD_SOURCE = 89;

    // ========================================================================
    // SECTION 4: NOTE SETTINGS
    // ------------------------------------------------------------------------
    // Current note behavior:
    //   - Note type = EX
    //   - Note class = ECA
    //   - Title = <Campaign> - Sent on <Month D, YYYY>
    //   - Duplicate handling = update existing note instead of adding another
    //
    // New enhancement requested here:
    //   The BODY of the note should be append-friendly across campaigns.
    //   That means the note body is now built as one or more campaign blocks:
    //
    //     Campaign A - Sent on March 12, 2026
    //     John Smith - clicked once, opened once
    //     Mary Jones - opened 2
    //
    //     Campaign B - Sent on March 12, 2026
    //     Chris Lane - clicked three times
    //
    //   When importing another campaign later, the code keeps the old block(s)
    //   and appends the new campaign block if that exact block title is not
    //   already present. If the exact block title already exists, that block is
    //   replaced in place.
    // ========================================================================
    private const string EXHIBITOR_NOTE_TYPE = "EX";
    private const string EXHIBITOR_NOTE_CLASS = "ECA";
    private const string EXHIBITOR_NOTE_TITLE_PREFIX = "Engagement - ";

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine("=== Mailchimp CSV -> Momentus Exhibitor Pipeline + Activities + Notes ===");
        Console.WriteLine();

        bool applyWasConfirmed = args.Any(arg =>
            string.Equals(arg, "--apply", StringComparison.OrdinalIgnoreCase));

        if (!applyWasConfirmed)
        {
            Console.WriteLine("STOPPED: this program makes live changes in Momentus.");
            Console.WriteLine("Use \"Run Mailchimp Sync.cmd\" to select and review a file safely.");
            Console.WriteLine("The launcher supplies --apply only after the user confirms the run.");
            return 64;
        }

        string apiUser = RequireEnvironmentVariable("MOMENTUS_APIUSER");
        string secret = RequireEnvironmentVariable("MOMENTUS_SECRET");
        string key = RequireEnvironmentVariable("MOMENTUS_KEY");

        if (string.IsNullOrWhiteSpace(apiUser) ||
            string.IsNullOrWhiteSpace(secret) ||
            string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("ERROR: Momentus credentials are not available.");
            Console.WriteLine("Run \"Setup Mailchimp Sync.cmd\" before running the sync.");
            return 65;
        }

        int pipeStage1 = ParseIntOrDefault(
            Environment.GetEnvironmentVariable("NEW_EXHIBITOR_STATUS"),
            DEFAULT_PIPE_STAGE_1);

        int leadSource = ParseIntOrDefault(
            Environment.GetEnvironmentVariable("LEAD_SOURCE"),
            DEFAULT_LEAD_SOURCE);

        string activityStatus =
            (Environment.GetEnvironmentVariable("ACTIVITY_STATUS") ?? "N").Trim();

        bool skipActivityDupes =
            (Environment.GetEnvironmentVariable("SKIP_ACTIVITY_DUPES") ?? "true")
            .Equals("true", StringComparison.OrdinalIgnoreCase);

        string workingDir = Directory.GetCurrentDirectory();
        string pendingDir = Path.Combine(workingDir, "pending");
        string completeDir = Path.Combine(workingDir, "complete");

        Directory.CreateDirectory(pendingDir);
        Directory.CreateDirectory(completeDir);

        List<string> csvFiles = GetPendingCsvPaths(pendingDir);
        if (csvFiles.Count == 0)
        {
            Console.WriteLine("No pending CSV files found in: " + pendingDir);
            return 2;
        }

        Console.WriteLine("Pending CSV files found: " + csvFiles.Count);
        Console.WriteLine("Org: " + ORG_CODE);
        Console.WriteLine("URI: " + MOMENTUS_URI);
        Console.WriteLine("DryRun: " + DRY_RUN);
        Console.WriteLine("Activity Recipient: " + ACTIVITY_RECIPIENT);
        Console.WriteLine("Exhibitor Note Type: " + EXHIBITOR_NOTE_TYPE);
        Console.WriteLine("Exhibitor Note Class: " + EXHIBITOR_NOTE_CLASS);

        ApiClient apiClient;
        try
        {
            apiClient = BuildClient(MOMENTUS_URI, apiUser, secret, key);
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR creating ApiClient:");
            Console.WriteLine(ex.Message);
            return 3;
        }

        int filesProcessed = 0;
        int filesWithErrors = 0;

        foreach (string csvPath in csvFiles)
        {
            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine("Processing file: " + csvPath);

            try
            {
                ProcessSingleCsvFile(
                    apiClient,
                    csvPath,
                    completeDir,
                    pipeStage1,
                    leadSource,
                    activityStatus,
                    skipActivityDupes);

                filesProcessed++;
            }
            catch (Exception ex)
            {
                filesWithErrors++;
                Console.WriteLine("ERROR processing file: " + csvPath);
                Console.WriteLine(ex.Message);
            }
        }

        Console.WriteLine();
        Console.WriteLine("==================================================");
        Console.WriteLine("All pending files processed.");
        Console.WriteLine("Files processed successfully: " + filesProcessed);
        Console.WriteLine("Files with errors: " + filesWithErrors);

        return filesWithErrors > 0 ? 1 : 0;
    }

    private static void ProcessSingleCsvFile(
        ApiClient apiClient,
        string csvPath,
        string completeDir,
        int pipeStage1,
        int leadSource,
        string activityStatus,
        bool skipActivityDupes)
    {
        Console.WriteLine("STEP 1 OF 6 - Reading and validating the prepared Mailchimp file...");
        Console.Out.Flush();
        List<CsvRow> rows = LoadRows(csvPath);

        Console.WriteLine("Campaign recipient rows read: " + rows.Count);
        if (rows.Count == 0)
        {
            WriteAuditAndCompleteEmptyFile(
                csvPath,
                completeDir,
                "NO_ROWS",
                "No campaign recipient rows were found in the prepared file.");
            return;
        }

        List<int> fileEventIds = rows.Select(r => r.EventId).Distinct().OrderBy(x => x).ToList();
        if (fileEventIds.Count != 1)
        {
            throw new Exception(
                "This file contains multiple events. Your process rule is 1 file = 1 event. Split the file before rerunning.");
        }

        int eventId = fileEventIds[0];
        Console.WriteLine("Detected Event ID: " + eventId);

        List<DateTime> fileCampaignSentDates = rows
            .Select(r => r.CampaignSentDate.Date)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        if (fileCampaignSentDates.Count != 1)
        {
            throw new Exception(
                "This prepared file contains multiple Campaign Sent Dates. Use one Mailchimp sent date per import.");
        }

        Console.WriteLine(
            "Campaign Sent Date: " +
            fileCampaignSentDates[0].ToString("MMMM d, yyyy", CultureInfo.InvariantCulture));

        int campaignEmailsSent = GetSinglePreparedMetric(
            rows.Select(r => r.CampaignEmailsSent),
            "Campaign Emails Sent");
        int campaignResponsePercentage = GetSinglePreparedMetric(
            rows.Select(r => r.CampaignResponsePercentage),
            "Campaign Response Percentage");
        if (campaignEmailsSent <= 0)
        {
            throw new Exception("Campaign Emails Sent must be greater than zero.");
        }
        if (campaignResponsePercentage < 0 || campaignResponsePercentage > 100)
        {
            throw new Exception("Campaign Response Percentage must be between 0 and 100.");
        }

        List<string> campaignTitles = rows
            .Select(r => (r.CampaignType ?? "").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (campaignTitles.Count != 1)
        {
            throw new Exception("This prepared file must contain exactly one Campaign name.");
        }
        if (campaignTitles[0].Length > 60)
        {
            throw new Exception("The Campaign name cannot exceed Momentus's 60-character Description limit.");
        }

        List<CsvRow> campaignDetailRows = rows
            .Where(r => r.Opens > 0 || r.Clicks > 0)
            .ToList();
        Console.WriteLine("Engaged campaign contact rows: " + campaignDetailRows.Count);
        Console.WriteLine(
            "No-interaction rows ignored for campaign details: " +
            (rows.Count - campaignDetailRows.Count));

        List<CsvRow> engagementRows = campaignDetailRows.Where(r => r.Clicks > 0).ToList();
        Console.WriteLine("Click-qualified activity/note rows: " + engagementRows.Count);

        // Resolve and validate the complete campaign roster before making any
        // writes. The campaign itself and its details are still created last.
        Console.WriteLine(
            "STEP 2 OF 6 - Resolving " + campaignDetailRows.Count +
            " engaged contacts in Momentus...");
        Console.Out.Flush();
        CampaignRoster campaignRoster;
        try
        {
            campaignRoster = BuildCampaignRoster(apiClient, ORG_CODE, campaignDetailRows);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                ex.Message + " No live changes were made for this file.",
                ex);
        }
        Console.WriteLine("Validated campaign recipients: " + campaignRoster.Recipients.Count);
        Console.WriteLine(
            "Campaign contacts included without a Primary Account: " +
            campaignRoster.ContactsWithoutPrimaryAccounts.Count);
        Console.WriteLine(
            "Skipped contacts not found in Momentus: " +
            campaignRoster.SkippedMissingAccounts.Count);

        Console.WriteLine("STEP 3 OF 6 - Checking the campaign and existing exhibitors...");
        Console.Out.Flush();
        CampaignSyncPlan campaignPlan = PrepareCampaignSync(
            apiClient,
            ORG_CODE,
            eventId,
            campaignTitles[0],
            fileCampaignSentDates[0]);
        Console.WriteLine(
            campaignPlan.ExistingCampaign == null
                ? "Campaign preflight: a new campaign will be created last."
                : "Campaign preflight: an existing partial campaign will be resumed last.");

        List<CsvRow> qualifiedEngagementRows = engagementRows
            .Where(row => campaignRoster.ContactToOrgAccount.ContainsKey(
                NormalizeAccountCode(row.ContactAccountCode)))
            .ToList();
        Dictionary<string, ExhibitorAggregate> aggregates =
            AggregateRowsByResolvedOrg(qualifiedEngagementRows, campaignRoster.ContactToOrgAccount);

        Console.WriteLine("Unique exhibitor org accounts to process: " + aggregates.Count);
        Dictionary<string, int> existingByAccount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (aggregates.Count > 0)
        {
            Console.WriteLine("Loading existing exhibitors for Event " + eventId + "...");
            existingByAccount = LoadExistingExhibitorsByAccount(apiClient, ORG_CODE, eventId);
            Console.WriteLine("Existing exhibitors found: " + existingByAccount.Count);
        }

        Console.WriteLine(
            "STEP 4 OF 6 - Processing " + aggregates.Count +
            " clicked exhibitor organizations, activities, and notes...");
        Console.Out.Flush();

        int addedExhibitors = 0;
        int openActivitiesAdded = 0;
        int clickActivitiesAdded = 0;
        int exhibitorNotesAdded = 0;
        int exhibitorNotesUpdated = 0;
        int skippedNoExhibitorId = 0;

        List<string> auditLines = new List<string>
        {
            "EventId,OrgAccountCode,SalespersonAccount,OpenType,ClickType,Opens,Clicks,ExhibitorID,Action,Message"
        };
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string auditPath = Path.Combine(
            completeDir,
            "momentus_mailchimp_sync_audit_" + Path.GetFileNameWithoutExtension(csvPath) + "_" + timestamp + ".csv");
        File.WriteAllLines(auditPath, auditLines);

        foreach (SkippedCampaignRecipient skipped in campaignRoster.ContactsWithoutPrimaryAccounts)
        {
            auditLines.Add(
                eventId + ",,,,," +
                skipped.Opens + "," +
                skipped.Clicks + ",,CAMPAIGN_CONTACT_ONLY_NO_PRIMARY," +
                EscapeCsv(
                    "Included contact " + skipped.ContactAccountCode +
                    " in the campaign; skipped exhibitor work because the contact has no Primary Account"));
        }

        foreach (SkippedCampaignRecipient skipped in campaignRoster.SkippedMissingAccounts)
        {
            auditLines.Add(
                eventId + ",,,,," +
                skipped.Opens + "," +
                skipped.Clicks + ",,SKIP_ACCOUNT_NOT_FOUND," +
                EscapeCsv(
                    "Skipped contact " + skipped.ContactAccountCode +
                    " because the account was not found in Momentus"));
        }
        File.WriteAllLines(auditPath, auditLines);

        int exhibitorProgress = 0;
        foreach (KeyValuePair<string, ExhibitorAggregate> kvp in aggregates.OrderBy(k => k.Key))
        {
            exhibitorProgress++;
            string orgAccountCode = kvp.Key;
            ExhibitorAggregate exhibitorData = kvp.Value;
            Console.WriteLine(
                "Exhibitor progress " + exhibitorProgress + "/" + aggregates.Count +
                " - org account " + orgAccountCode);
            Console.Out.Flush();

            int exhibitorId;
            bool exhibitorAlreadyExists = existingByAccount.TryGetValue(orgAccountCode, out exhibitorId);

            if (!exhibitorAlreadyExists)
            {
                try
                {
                    ContactSlots slots = exhibitorData.GetContactSlots();

                    ExhibitorsModel newExhibitor = new ExhibitorsModel
                    {
                        OrganizationCode = ORG_CODE,
                        Event = eventId,
                        AccountCode = orgAccountCode,
                        ExhibitorStatus = pipeStage1,
                        LeadSource = leadSource,
                        MainContact = slots.MainContact,
                        BoothContact = slots.BoothContact,
                        AdditionalContact1 = slots.Add1,
                        AdditionalContact2 = slots.Add2,
                        AdditionalContact3 = slots.Add3,
                        AdditionalContact4 = slots.Add4,
                        AdditionalContact5 = slots.Add5
                    };

                    if (!string.IsNullOrWhiteSpace(exhibitorData.SalespersonAccountCode))
                    {
                        newExhibitor.Salesperson = exhibitorData.SalespersonAccountCode;
                    }

                    ExhibitorsModel createdExhibitor = apiClient.Endpoints.Exhibitors.Add(newExhibitor);
                    exhibitorId = createdExhibitor.ExhibitorID ?? 0;

                    if (exhibitorId <= 0)
                    {
                        auditLines.Add(
                            eventId + "," +
                            orgAccountCode + "," +
                            exhibitorData.SalespersonAccountCode + "," +
                            exhibitorData.OpenTypeCode + "," +
                            exhibitorData.ClickTypeCode + "," +
                            exhibitorData.TotalOpens + "," +
                            exhibitorData.TotalClicks + ",,ERROR_ADD_EXHIBITOR,Created exhibitor but ExhibitorID was null or 0");
                        File.WriteAllLines(auditPath, auditLines);
                        continue;
                    }

                    existingByAccount[orgAccountCode] = exhibitorId;
                    addedExhibitors++;

                    auditLines.Add(
                        eventId + "," +
                        orgAccountCode + "," +
                        exhibitorData.SalespersonAccountCode + "," +
                        exhibitorData.OpenTypeCode + "," +
                        exhibitorData.ClickTypeCode + "," +
                        exhibitorData.TotalOpens + "," +
                        exhibitorData.TotalClicks + "," +
                        exhibitorId + ",ADD_EXHIBITOR,Added exhibitor record");
                    File.WriteAllLines(auditPath, auditLines);
                }
                catch (Exception ex)
                {
                    auditLines.Add(
                        eventId + "," +
                        orgAccountCode + "," +
                        exhibitorData.SalespersonAccountCode + "," +
                        exhibitorData.OpenTypeCode + "," +
                        exhibitorData.ClickTypeCode + "," +
                        exhibitorData.TotalOpens + "," +
                        exhibitorData.TotalClicks + ",,ERROR_ADD_EXHIBITOR," + EscapeCsv(ex.Message));
                    File.WriteAllLines(auditPath, auditLines);
                    continue;
                }
            }

            if (exhibitorId <= 0)
            {
                skippedNoExhibitorId++;
                continue;
            }

            foreach (ContactEngagement contact in exhibitorData.Contacts.OrderBy(c => c.SortKey))
            {
                string activitySubject = BuildEngagementTitle(
                    exhibitorData.CampaignType,
                    exhibitorData.CampaignSentDate);
                string activityText = BuildActivityEngagementText(
                    exhibitorData.CampaignType,
                    exhibitorData.CampaignSentDate,
                    contact.Opens,
                    contact.Clicks);

                if (contact.Opens > 0 && !string.IsNullOrWhiteSpace(exhibitorData.OpenTypeCode))
                {
                    bool didAddOpen = AddExhibitorActivity(
                        apiClient,
                        ORG_CODE,
                        eventId,
                        exhibitorId,
                        orgAccountCode,
                        contact.ContactAccountCode,
                        exhibitorData.OpenTypeCode,
                        activitySubject,
                        activityText,
                        contact.Opens,
                        ACTIVITY_RECIPIENT,
                        activityStatus,
                        skipActivityDupes,
                        DRY_RUN);

                    if (didAddOpen)
                    {
                        openActivitiesAdded++;
                    }
                }

                if (contact.Clicks > 0 && !string.IsNullOrWhiteSpace(exhibitorData.ClickTypeCode))
                {
                    bool didAddClick = AddExhibitorActivity(
                        apiClient,
                        ORG_CODE,
                        eventId,
                        exhibitorId,
                        orgAccountCode,
                        contact.ContactAccountCode,
                        exhibitorData.ClickTypeCode,
                        activitySubject,
                        activityText,
                        contact.Clicks,
                        ACTIVITY_RECIPIENT,
                        activityStatus,
                        skipActivityDupes,
                        DRY_RUN);

                    if (didAddClick)
                    {
                        clickActivitiesAdded++;
                    }
                }
            }

            string noteTitle = BuildEngagementTitle(
                exhibitorData.CampaignType,
                exhibitorData.CampaignSentDate);
            string noteBlock = BuildEngagementNoteBlock(noteTitle, exhibitorData);

            NoteUpsertResult noteResult = UpsertExhibitorEngagementNote(
                apiClient,
                ORG_CODE,
                eventId,
                exhibitorId,
                noteTitle,
                noteBlock);

            if (noteResult == NoteUpsertResult.Added)
            {
                exhibitorNotesAdded++;
            }
            else if (noteResult == NoteUpsertResult.Updated)
            {
                exhibitorNotesUpdated++;
            }

            auditLines.Add(
                eventId + "," +
                orgAccountCode + "," +
                exhibitorData.SalespersonAccountCode + "," +
                exhibitorData.OpenTypeCode + "," +
                exhibitorData.ClickTypeCode + "," +
                exhibitorData.TotalOpens + "," +
                exhibitorData.TotalClicks + "," +
                exhibitorId + ",OK,Processed exhibitor with activities and note");
            File.WriteAllLines(auditPath, auditLines);
        }

        // Campaign creation is intentionally the final Momentus write phase.
        Console.WriteLine(
            "STEP 5 OF 6 - Creating or resuming the campaign and processing " +
            campaignRoster.Recipients.Count + " contact details...");
        Console.Out.Flush();
        CampaignSyncResult campaignResult = UpsertCampaignAndDetails(
            apiClient,
            ORG_CODE,
            eventId,
            campaignTitles[0],
            fileCampaignSentDates[0],
            DateTime.Today,
            rows[0].SalespersonAccountCode,
            campaignEmailsSent,
            campaignResponsePercentage,
            campaignRoster.Recipients,
            campaignPlan);

        auditLines.Add(
            eventId + ",,,,,,,," +
            (campaignResult.ReusedExistingCampaign ? "RESUME_CAMPAIGN" : "ADD_CAMPAIGN") + "," +
            EscapeCsv(
                "Campaign " + campaignResult.CampaignId +
                "; details added=" + campaignResult.DetailsAdded +
                "; updated=" + campaignResult.DetailsUpdated +
                "; unchanged=" + campaignResult.DetailsUnchanged +
                "; skipped invalid accounts=" + campaignResult.DetailsSkippedInvalidAccount +
                "; campaign contacts included without Primary Account=" +
                campaignRoster.ContactsWithoutPrimaryAccounts.Count +
                "; contacts skipped because account was not found=" +
                campaignRoster.SkippedMissingAccounts.Count));

        Console.WriteLine("STEP 6 OF 6 - Saving the final audit and completing the run...");
        Console.Out.Flush();
        File.WriteAllLines(auditPath, auditLines);

        string completedCsvPath = MoveFileToComplete(csvPath, completeDir, timestamp);

        Console.WriteLine();
        Console.WriteLine("=== File Summary ===");
        Console.WriteLine("File: " + Path.GetFileName(csvPath));
        Console.WriteLine("Event: " + eventId);
        Console.WriteLine("Exhibitor org accounts processed: " + aggregates.Count);
        Console.WriteLine("Exhibitors added: " + addedExhibitors);
        Console.WriteLine("Open activities added: " + openActivitiesAdded);
        Console.WriteLine("Click activities added: " + clickActivitiesAdded);
        Console.WriteLine("Exhibitor notes added: " + exhibitorNotesAdded);
        Console.WriteLine("Exhibitor notes updated: " + exhibitorNotesUpdated);
        Console.WriteLine("Campaign ID: " + campaignResult.CampaignId);
        Console.WriteLine("Campaign reused after prior partial run: " + campaignResult.ReusedExistingCampaign);
        Console.WriteLine("Campaign details added: " + campaignResult.DetailsAdded);
        Console.WriteLine("Campaign details updated: " + campaignResult.DetailsUpdated);
        Console.WriteLine("Campaign details unchanged: " + campaignResult.DetailsUnchanged);
        Console.WriteLine(
            "Campaign details skipped because account was invalid: " +
            campaignResult.DetailsSkippedInvalidAccount);
        Console.WriteLine(
            "Campaign contacts included without a Primary Account: " +
            campaignRoster.ContactsWithoutPrimaryAccounts.Count);
        Console.WriteLine(
            "Contacts skipped because account was not found: " +
            campaignRoster.SkippedMissingAccounts.Count);
        Console.WriteLine("Skipped because exhibitor id was missing: " + skippedNoExhibitorId);
        Console.WriteLine("Audit CSV: " + auditPath);
        Console.WriteLine("Completed CSV: " + completedCsvPath);
    }

    private static List<string> GetPendingCsvPaths(string pendingDir)
    {
        if (!Directory.Exists(pendingDir))
        {
            return new List<string>();
        }

        return Directory.GetFiles(pendingDir, "*.csv")
            .OrderBy(f => File.GetCreationTime(f))
            .ToList();
    }

    private static string MoveFileToComplete(string csvPath, string completeDir, string timestamp)
    {
        string fileName = Path.GetFileNameWithoutExtension(csvPath);
        string extension = Path.GetExtension(csvPath);
        string destination = Path.Combine(completeDir, fileName + "_" + timestamp + extension);

        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        File.Move(csvPath, destination);
        return destination;
    }

    private static void WriteAuditAndCompleteEmptyFile(
        string csvPath,
        string completeDir,
        string actionCode,
        string message)
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string auditPath = Path.Combine(
            completeDir,
            "momentus_mailchimp_sync_audit_" + Path.GetFileNameWithoutExtension(csvPath) + "_" + timestamp + ".csv");

        File.WriteAllLines(auditPath, new[]
        {
            "EventId,OrgAccountCode,SalespersonAccount,OpenType,ClickType,Opens,Clicks,ExhibitorID,Action,Message",
            ",,,,,,,," + actionCode + "," + EscapeCsv(message)
        });

        string completedCsvPath = MoveFileToComplete(csvPath, completeDir, timestamp);

        Console.WriteLine("Audit CSV: " + auditPath);
        Console.WriteLine("Completed CSV: " + completedCsvPath);
    }

    private static List<CsvRow> LoadRows(string csvPath)
    {
        int eventIdx = ColLetterToIndex(EVENT_ID_COLUMN);
        int campaignTypeIdx = ColLetterToIndex(CAMPAIGN_TYPE_COLUMN);
        int salespersonNameIdx = ColLetterToIndex(SALESPERSON_NAME_COLUMN);
        int salespersonAccountIdx = ColLetterToIndex(SALESPERSON_ACCOUNT_COLUMN);
        int clickTypeIdx = ColLetterToIndex(CLICK_TYPE_COLUMN);
        int openTypeIdx = ColLetterToIndex(OPEN_TYPE_COLUMN);
        int emailIdx = ColLetterToIndex(EMAIL_COLUMN);
        int clicksIdx = ColLetterToIndex(CLICKS_COLUMN);
        int opensIdx = ColLetterToIndex(OPENS_COLUMN);
        int firstNameIdx = ColLetterToIndex(FIRST_NAME_COLUMN);
        int lastNameIdx = ColLetterToIndex(LAST_NAME_COLUMN);
        int contactIdx = ColLetterToIndex(CONTACT_ACCOUNT_COLUMN);
        int campaignSentDateIdx = ColLetterToIndex(CAMPAIGN_SENT_DATE_COLUMN);
        int campaignEmailsSentIdx = ColLetterToIndex(CAMPAIGN_EMAILS_SENT_COLUMN);
        int campaignResponsePercentageIdx = ColLetterToIndex(CAMPAIGN_RESPONSE_PERCENTAGE_COLUMN);

        List<CsvRow> rows = new List<CsvRow>();

        using (StreamReader reader = new StreamReader(csvPath))
        {
            bool first = true;

            while (!reader.EndOfStream)
            {
                string line = reader.ReadLine();
                if (line == null)
                {
                    break;
                }

                if (first && CSV_HAS_HEADER)
                {
                    first = false;
                    continue;
                }
                first = false;

                List<string> cols = ParseCsvLine(line);
                if (cols.Count == 0)
                {
                    continue;
                }

                CsvRow row = new CsvRow();
                row.EventId = ParseIntSafe(GetCol(cols, eventIdx));
                row.CampaignType = (GetCol(cols, campaignTypeIdx) ?? "").Trim();
                row.SalespersonName = (GetCol(cols, salespersonNameIdx) ?? "").Trim();
                row.SalespersonAccountCode = NormalizeAccountCode(GetCol(cols, salespersonAccountIdx));
                row.ClickTypeCode = (GetCol(cols, clickTypeIdx) ?? "").Trim();
                row.OpenTypeCode = (GetCol(cols, openTypeIdx) ?? "").Trim();
                row.Email = (GetCol(cols, emailIdx) ?? "").Trim();
                row.Clicks = ParseIntSafe(GetCol(cols, clicksIdx));
                row.Opens = ParseIntSafe(GetCol(cols, opensIdx));
                row.FirstName = (GetCol(cols, firstNameIdx) ?? "").Trim();
                row.LastName = (GetCol(cols, lastNameIdx) ?? "").Trim();
                row.ContactAccountCode = NormalizeAccountCode(GetCol(cols, contactIdx));
                row.CampaignEmailsSent = ParseIntSafe(GetCol(cols, campaignEmailsSentIdx));
                row.CampaignResponsePercentage = ParseIntSafe(GetCol(cols, campaignResponsePercentageIdx));
                if (row.EventId <= 0)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(row.ContactAccountCode))
                {
                    continue;
                }

                row.CampaignSentDate = ParseCampaignSentDate(GetCol(cols, campaignSentDateIdx));

                rows.Add(row);
            }
        }

        return rows;
    }

    private static CampaignRoster BuildCampaignRoster(
        ApiClient apiClient,
        string orgCode,
        List<CsvRow> rows)
    {
        CampaignRoster roster = new CampaignRoster();
        List<IGrouping<string, CsvRow>> contactGroups = rows
            .GroupBy(r => NormalizeAccountCode(r.ContactAccountCode), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key)
            .ToList();
        int contactsResolved = 0;

        void ReportContactProgress()
        {
            contactsResolved++;
            if (contactsResolved == 1 || contactsResolved % 25 == 0 ||
                contactsResolved == contactGroups.Count)
            {
                Console.WriteLine(
                    "Contact resolution progress " + contactsResolved + "/" +
                    contactGroups.Count);
                Console.Out.Flush();
            }
        }

        foreach (IGrouping<string, CsvRow> group in contactGroups)
        {
            string contactAccountCode = group.Key;
            int opens = group.Sum(r => r.Opens);
            int clicks = group.Sum(r => r.Clicks);
            AllAccountsModel? account;
            try
            {
                account = apiClient.Endpoints.Accounts.Get(orgCode, contactAccountCode);
            }
            catch (Exception ex) when (IsMomentusAccountNotFound(ex))
            {
                roster.SkippedMissingAccounts.Add(new SkippedCampaignRecipient
                {
                    ContactAccountCode = contactAccountCode,
                    Opens = group.Sum(r => r.Opens),
                    Clicks = group.Sum(r => r.Clicks)
                });
                ReportContactProgress();
                continue;
            }

            if (account == null)
            {
                roster.SkippedMissingAccounts.Add(new SkippedCampaignRecipient
                {
                    ContactAccountCode = contactAccountCode,
                    Opens = group.Sum(r => r.Opens),
                    Clicks = group.Sum(r => r.Clicks)
                });
                ReportContactProgress();
                continue;
            }

            string orgAccountCode = NormalizeAccountCode(account.PrimaryAccount);
            CampaignRecipient recipient = new CampaignRecipient
            {
                ContactAccountCode = contactAccountCode,
                OrgAccountCode = orgAccountCode,
                AccountCode = contactAccountCode,
                TargetKind = "Contact",
                Opens = opens,
                Clicks = clicks,
                EmailsSent = group.Count(),
                Status = ClassifyCampaignDetailStatus(opens, clicks)
            };

            roster.Recipients.Add(recipient);
            if (string.IsNullOrWhiteSpace(orgAccountCode))
            {
                roster.ContactsWithoutPrimaryAccounts.Add(new SkippedCampaignRecipient
                {
                    ContactAccountCode = contactAccountCode,
                    Opens = opens,
                    Clicks = clicks
                });
                ReportContactProgress();
                continue;
            }

            roster.ContactToOrgAccount[contactAccountCode] = orgAccountCode;
            ReportContactProgress();
        }

        return roster;
    }

    private static bool IsMomentusAccountNotFound(Exception exception)
    {
        if (exception.Message.IndexOf(
                "Account entry not found for values",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (exception is AggregateException aggregateException)
        {
            return aggregateException.InnerExceptions.Any(IsMomentusAccountNotFound);
        }

        return exception.InnerException != null &&
               IsMomentusAccountNotFound(exception.InnerException);
    }

    private static string ClassifyCampaignDetailStatus(int opens, int clicks)
    {
        if (clicks > 0)
        {
            return "CLI";
        }
        if (opens > 0)
        {
            return "OPE";
        }
        return "I";
    }

    private static Dictionary<string, ExhibitorAggregate> AggregateRowsByResolvedOrg(
        List<CsvRow> rows,
        Dictionary<string, string> contactToOrgAccount)
    {
        Dictionary<string, ExhibitorAggregate> exhibitorMap =
            new Dictionary<string, ExhibitorAggregate>(StringComparer.OrdinalIgnoreCase);

        foreach (CsvRow row in rows)
        {
            string orgAccountCode;
            if (!contactToOrgAccount.TryGetValue(row.ContactAccountCode, out orgAccountCode) ||
                string.IsNullOrWhiteSpace(orgAccountCode))
            {
                throw new InvalidOperationException(
                    "The validated campaign roster did not contain contact " + row.ContactAccountCode + ".");
            }

            ExhibitorAggregate exhibitor;
            if (!exhibitorMap.TryGetValue(orgAccountCode, out exhibitor))
            {
                exhibitor = new ExhibitorAggregate();
                exhibitor.EventId = row.EventId;
                exhibitor.OrgAccountCode = orgAccountCode;
                exhibitor.SalespersonName = row.SalespersonName;
                exhibitor.SalespersonAccountCode = row.SalespersonAccountCode;
                exhibitor.CampaignType = row.CampaignType;
                exhibitor.CampaignSentDate = row.CampaignSentDate;
                exhibitor.ClickTypeCode = row.ClickTypeCode;
                exhibitor.OpenTypeCode = row.OpenTypeCode;
                exhibitorMap[orgAccountCode] = exhibitor;
            }

            exhibitor.TotalClicks += row.Clicks;
            exhibitor.TotalOpens += row.Opens;
            exhibitor.AddOrMergeContact(row);
        }

        return exhibitorMap;
    }

    private static string ResolveOrgAccountFromContact(ApiClient apiClient, string orgCode, string contactAccountCode)
    {
        if (string.IsNullOrWhiteSpace(contactAccountCode))
        {
            return null;
        }

        contactAccountCode = NormalizeAccountCode(contactAccountCode);

        var account = apiClient.Endpoints.Accounts.Get(orgCode, contactAccountCode);
        if (account == null)
        {
            return null;
        }

        string primary = account.PrimaryAccount;
        if (string.IsNullOrWhiteSpace(primary))
        {
            return null;
        }

        return NormalizeAccountCode(primary);
    }

    private static Dictionary<string, int> LoadExistingExhibitorsByAccount(ApiClient apiClient, string orgCode, int eventId)
    {
        Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var response = apiClient.Endpoints.Exhibitors.Search(orgCode, "Event eq " + eventId);
        if (response == null || response.Results == null)
        {
            return map;
        }

        foreach (ExhibitorsModel exhibitor in response.Results)
        {
            if (exhibitor == null || string.IsNullOrWhiteSpace(exhibitor.AccountCode))
            {
                continue;
            }

            int existingId = exhibitor.ExhibitorID ?? 0;
            if (existingId <= 0)
            {
                continue;
            }

            string normalizedAccount = NormalizeAccountCode(exhibitor.AccountCode);
            if (!map.ContainsKey(normalizedAccount))
            {
                map[normalizedAccount] = existingId;
            }
        }

        return map;
    }

    private static bool AddExhibitorActivity(
        ApiClient apiClient,
        string orgCode,
        int eventId,
        int exhibitorId,
        string orgAccountCode,
        string contactAccountCode,
        string typeCode,
        string subject,
        string text,
        int count,
        string recipient,
        string status,
        bool skipDupes,
        bool dryRun)
    {
        if (count <= 0)
        {
            return false;
        }

        contactAccountCode = NormalizeAccountCode(contactAccountCode);

        if (skipDupes)
        {
            try
            {
                var response = apiClient.Endpoints.Activities.Search(
                    orgCode,
                    "Account eq '" + EscapeOData(orgAccountCode) + "'");

                bool exists = response != null &&
                              response.Results != null &&
                              response.Results.Any(activity =>
                                  activity != null &&
                                  (activity.ExhibitorID ?? 0) == exhibitorId &&
                                  (activity.Event ?? 0) == eventId &&
                                  string.Equals(activity.Type, typeCode, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(activity.Subject, subject, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals((activity.Contact ?? "").Trim(), contactAccountCode, StringComparison.OrdinalIgnoreCase));

                if (exists)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not safely check for an existing " + typeCode +
                    " activity for contact " + contactAccountCode +
                    ". No new activity was added because that could create a duplicate.",
                    ex);
            }
        }

        if (dryRun)
        {
            return true;
        }

        ActivitiesModel activityToAdd = new ActivitiesModel
        {
            OrganizationCode = orgCode,
            Account = orgAccountCode,
            ExhibitorID = exhibitorId,
            Event = eventId,
            Type = typeCode,
            Subject = subject,
            Text = text,
            Recipient = recipient,
            Status = status,
            Contact = contactAccountCode
        };

        try
        {
            apiClient.Endpoints.Activities.Add(activityToAdd);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not add " + typeCode +
                " activity for contact " + contactAccountCode +
                " and exhibitor account " + orgAccountCode + ".",
                ex);
        }
        return true;
    }

    // ========================================================================
    // CAMPAIGN + CAMPAIGN DETAILS (FINAL WRITE PHASE)
    // ========================================================================
    private static CampaignSyncPlan PrepareCampaignSync(
        ApiClient apiClient,
        string orgCode,
        int eventId,
        string campaignTitle,
        DateTime campaignSentDate)
    {
        var searchOptions = new Ungerboeck.Api.Models.Options.Search
        {
            PageSize = 1000,
            MaxResults = 100000
        };

        string campaignFilter =
            "Designation eq '" + EscapeOData(CAMPAIGN_DESIGNATION) + "'" +
            " and Event eq " + eventId +
            " and Description eq '" + EscapeOData(campaignTitle) + "'";

        var campaignResponse = apiClient.Endpoints.Campaigns.Search(
            orgCode,
            campaignFilter,
            searchOptions);

        List<CampaignsModel> exactMatches = campaignResponse != null && campaignResponse.Results != null
            ? campaignResponse.Results
                .Where(campaign =>
                    campaign != null &&
                    string.Equals(campaign.Designation, CAMPAIGN_DESIGNATION, StringComparison.OrdinalIgnoreCase) &&
                    (campaign.Event ?? 0) == eventId &&
                    string.Equals((campaign.Description ?? "").Trim(), campaignTitle.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    campaign.StartDate.HasValue &&
                    campaign.StartDate.Value.Date == campaignSentDate.Date)
                .ToList()
            : new List<CampaignsModel>();

        if (exactMatches.Count > 1)
        {
            throw new InvalidOperationException(
                "More than one existing campaign matches this event, campaign name, and sent date. " +
                "No live changes were made for this file.");
        }

        CampaignsModel? existingCampaign = exactMatches.FirstOrDefault();
        string detailCampaignId = existingCampaign != null && !string.IsNullOrWhiteSpace(existingCampaign.ID)
            ? existingCampaign.ID
            : "ZZZZZZZZZZ";

        string detailFilter =
            "CampaignDesignation eq '" + EscapeOData(CAMPAIGN_DESIGNATION) + "'" +
            " and Campaign eq '" + EscapeOData(detailCampaignId) + "'";

        List<CampaignDetailsModel> existingDetails = LoadAllCampaignDetails(
            apiClient,
            orgCode,
            detailFilter,
            searchOptions);

        List<string> duplicateAccounts = existingDetails
            .Where(detail => !string.IsNullOrWhiteSpace(detail.Account))
            .GroupBy(detail => NormalizeAccountCode(detail.Account), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicateAccounts.Count > 0)
        {
            throw new InvalidOperationException(
                "The existing campaign has duplicate detail rows for account " + duplicateAccounts[0] +
                ". Review that campaign in Momentus before rerunning.");
        }

        return new CampaignSyncPlan
        {
            ExistingCampaign = existingCampaign,
            ExistingDetails = existingDetails
        };
    }

    private static List<CampaignDetailsModel> LoadAllCampaignDetails(
        ApiClient apiClient,
        string orgCode,
        string filter,
        Ungerboeck.Api.Models.Options.Search searchOptions)
    {
        List<CampaignDetailsModel> details = new List<CampaignDetailsModel>();
        var response = apiClient.Endpoints.CampaignDetails.Search(
            orgCode,
            filter,
            searchOptions);

        while (response != null)
        {
            if (response.Results != null)
            {
                details.AddRange(response.Results.Where(detail => detail != null));
            }

            string nextLink = response.SearchMetadata != null &&
                              response.SearchMetadata.Links != null
                ? response.SearchMetadata.Links.Next
                : "";
            if (string.IsNullOrWhiteSpace(nextLink))
            {
                break;
            }

            response = apiClient.Endpoints.CampaignDetails.NavigateSearchList(nextLink);
        }

        return details;
    }

    private static CampaignSyncResult UpsertCampaignAndDetails(
        ApiClient apiClient,
        string orgCode,
        int eventId,
        string campaignTitle,
        DateTime campaignSentDate,
        DateTime campaignEndDate,
        string coordinatorAccountCode,
        int emailsSent,
        int responsePercentage,
        List<CampaignRecipient> recipients,
        CampaignSyncPlan plan)
    {
        CampaignsModel? campaign = plan.ExistingCampaign;
        bool reusedExisting = campaign != null;

        if (campaign == null)
        {
            string summary = BuildCampaignSummary(campaignTitle, campaignSentDate, emailsSent);
            CampaignsModel campaignToAdd = new CampaignsModel
            {
                OrganizationCode = orgCode,
                ID = "*AUTO",
                Designation = CAMPAIGN_DESIGNATION,
                Description = campaignTitle,
                Active = "A",
                Event = eventId,
                Coordinator = NormalizeAccountCode(coordinatorAccountCode),
                EmailsSent = emailsSent,
                EmailResponsesPercentage = responsePercentage,
                Summary = summary,
                StartDate = campaignSentDate.Date,
                EndDate = campaignEndDate.Date
                // Group is intentionally left blank.
            };

            campaign = apiClient.Endpoints.Campaigns.Add(campaignToAdd);
            if (campaign == null || string.IsNullOrWhiteSpace(campaign.ID))
            {
                throw new InvalidOperationException(
                    "Momentus created the campaign but did not return a Campaign ID. " +
                    "Do not rerun until the campaign is reviewed.");
            }
        }

        Dictionary<string, CampaignDetailsModel> existingByContact = plan.ExistingDetails
            .Where(detail => !string.IsNullOrWhiteSpace(detail.Account))
            .ToDictionary(
                detail => NormalizeAccountCode(detail.Account),
                detail => detail,
                StringComparer.OrdinalIgnoreCase);

        int detailsAdded = 0;
        int detailsUnchanged = 0;
        int detailsSkippedInvalidAccount = 0;
        int detailsProcessed = 0;
        int detailTotal = recipients.Count;

        void ReportDetailProgress()
        {
            detailsProcessed++;
            if (detailsProcessed == 1 || detailsProcessed % 25 == 0 ||
                detailsProcessed == detailTotal)
            {
                Console.WriteLine(
                    "Campaign detail progress " + detailsProcessed + "/" + detailTotal +
                    " - added " + detailsAdded +
                    ", existing " + detailsUnchanged +
                    ", skipped " + detailsSkippedInvalidAccount);
                Console.Out.Flush();
            }
        }

        foreach (CampaignRecipient recipient in recipients
                     .OrderBy(r => r.TargetKind)
                     .ThenBy(r => r.AccountCode))
        {
            if (existingByContact.ContainsKey(recipient.AccountCode))
            {
                // Preserve any manual follow-up status or other edits made in
                // Momentus if a failed import is being resumed.
                detailsUnchanged++;
                ReportDetailProgress();
                continue;
            }

            CampaignDetailsModel detailToAdd = new CampaignDetailsModel
            {
                OrganizationCode = orgCode,
                CampaignDesignation = CAMPAIGN_DESIGNATION,
                Campaign = campaign.ID,
                Account = recipient.AccountCode,
                Status = recipient.Status,
                EmailsSent = recipient.EmailsSent
            };

            CampaignDetailsModel added;
            try
            {
                added = apiClient.Endpoints.CampaignDetails.Add(detailToAdd);
            }
            catch (Exception ex) when (IsMomentusAccountNotFound(ex) ||
                                       IsMomentusInvalidAccount(ex))
            {
                detailsSkippedInvalidAccount++;
                Console.WriteLine(
                    "Skipping campaign detail for invalid account: " +
                    recipient.AccountCode);
                ReportDetailProgress();
                continue;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not add campaign detail for account " +
                    recipient.AccountCode + ".",
                    ex);
            }
            if (added == null)
            {
                throw new InvalidOperationException(
                    "Momentus did not confirm campaign detail creation for contact " +
                    recipient.AccountCode + ". Do not rerun until the campaign is reviewed.");
            }
            detailsAdded++;
            ReportDetailProgress();
        }

        return new CampaignSyncResult
        {
            CampaignId = campaign.ID,
            ReusedExistingCampaign = reusedExisting,
            DetailsAdded = detailsAdded,
            DetailsUpdated = 0,
            DetailsUnchanged = detailsUnchanged,
            DetailsSkippedInvalidAccount = detailsSkippedInvalidAccount
        };
    }

    private static bool IsMomentusInvalidAccount(Exception exception)
    {
        if (exception.Message.IndexOf(
                "account you have entered does not exist",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (exception is AggregateException aggregateException)
        {
            return aggregateException.InnerExceptions.Any(IsMomentusInvalidAccount);
        }

        return exception.InnerException != null &&
               IsMomentusInvalidAccount(exception.InnerException);
    }

    private static string BuildCampaignSummary(
        string campaignTitle,
        DateTime campaignSentDate,
        int emailsSent)
    {
        return campaignTitle + " - Sent on " +
               campaignSentDate.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture) +
               " - Emails sent: " + emailsSent;
    }

    // ========================================================================
    // NOTE UPSERT WITH CAMPAIGN BLOCK APPEND/REPLACE
    // ------------------------------------------------------------------------
    // The note title is still the primary note record title.
    // The note BODY now also starts with that same title, then the contact
    // engagement lines below it.
    //
    // On later imports for another campaign, the code keeps prior campaign
    // blocks in the same note body and appends the new block.
    //
    // If the same campaign/date title already exists inside the body, that one
    // block is replaced instead of duplicated.
    // ========================================================================
    private static NoteUpsertResult UpsertExhibitorEngagementNote(
        ApiClient apiClient,
        string orgCode,
        int eventId,
        int exhibitorId,
        string title,
        string noteBlock)
    {
        string filter =
            "Type eq '" + EscapeOData(EXHIBITOR_NOTE_TYPE) + "'" +
            " and Event eq " + eventId +
            " and ExhibitorID eq " + exhibitorId;

        var existingResponse = apiClient.Endpoints.Notes.Search(orgCode, filter);
        NotesModel existingNote = existingResponse != null && existingResponse.Results != null
            ? existingResponse.Results.FirstOrDefault(note =>
                note != null &&
                string.Equals((note.Type ?? "").Trim(), EXHIBITOR_NOTE_TYPE, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((note.Class ?? "").Trim(), EXHIBITOR_NOTE_CLASS, StringComparison.OrdinalIgnoreCase))
            : null;

        if (existingNote != null)
        {
            string mergedText = MergeCampaignBlockIntoExistingNote(existingNote.Text, title, noteBlock);
            existingNote.Text = mergedText;
            existingNote.Title = title;
            existingNote.Class = EXHIBITOR_NOTE_CLASS;
            apiClient.Endpoints.Notes.Update(existingNote);
            return NoteUpsertResult.Updated;
        }

        NotesModel newNote = new NotesModel
        {
            OrganizationCode = orgCode,
            Type = EXHIBITOR_NOTE_TYPE,
            Class = EXHIBITOR_NOTE_CLASS,
            Title = title,
            Text = noteBlock,
            Event = eventId,
            ExhibitorID = exhibitorId
        };

        apiClient.Endpoints.Notes.Add(newNote);
        return NoteUpsertResult.Added;
    }

    private static string BuildEngagementTitle(string campaignType, DateTime campaignSentDate)
    {
        string safeCampaignType = (campaignType ?? "").Trim();

        if (string.IsNullOrWhiteSpace(safeCampaignType))
        {
            safeCampaignType = "Engagement";
        }

        return safeCampaignType + " - Sent on " +
               campaignSentDate.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
    }

    private static string BuildActivityEngagementText(
        string campaignType,
        DateTime campaignSentDate,
        int opens,
        int clicks)
    {
        string campaignTitle = (campaignType ?? "").Trim();
        if (string.IsNullOrWhiteSpace(campaignTitle))
        {
            campaignTitle = "Engagement";
        }

        return campaignTitle + " - opened " + DescribeActivityCount(opens) +
               ", clicked " + DescribeActivityCount(clicks) +
               ". Sent on " +
               campaignSentDate.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture) + ".";
    }

    private static string DescribeActivityCount(int count)
    {
        return count == 1 ? "1 time" : count + " times";
    }

    // ------------------------------------------------------------------------
    // A campaign block is the text chunk for one campaign import inside the
    // note body. The first line repeats the campaign/date title, followed by
    // one line per contact.
    // ------------------------------------------------------------------------
    private static string BuildEngagementNoteBlock(string noteTitle, ExhibitorAggregate exhibitorData)
    {
        List<string> lines = new List<string>();
        lines.Add(noteTitle);

        foreach (ContactEngagement contact in exhibitorData.Contacts.OrderBy(c => c.SortKey))
        {
            string displayName = BuildContactDisplayName(contact.FullName, contact.ContactAccountCode);
            string engagementPhrase = BuildContactEngagementPhrase(contact.Opens, contact.Clicks);
            lines.Add(displayName + " - " + engagementPhrase);
        }

        return string.Join(Environment.NewLine, lines);
    }

    // ------------------------------------------------------------------------
    // This helper updates the existing note body intelligently:
    //   - if there is no existing body, use the new block
    //   - if the exact campaign block title already exists, replace that block
    //   - otherwise append the new block with a blank line in between
    // ------------------------------------------------------------------------
    private static string MergeCampaignBlockIntoExistingNote(string existingText, string noteTitle, string newBlock)
    {
        string normalizedExisting = (existingText ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        string normalizedNewBlock = (newBlock ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();

        if (string.IsNullOrWhiteSpace(normalizedExisting))
        {
            return normalizedNewBlock;
        }

        List<string> blocks = SplitNoteIntoBlocks(normalizedExisting);
        bool replaced = false;

        for (int i = 0; i < blocks.Count; i++)
        {
            string firstLine = GetFirstNonEmptyLine(blocks[i]);
            if (string.Equals(firstLine, noteTitle, StringComparison.OrdinalIgnoreCase))
            {
                blocks[i] = normalizedNewBlock;
                replaced = true;
                break;
            }
        }

        if (!replaced)
        {
            blocks.Add(normalizedNewBlock);
        }

        return string.Join(Environment.NewLine + Environment.NewLine, blocks);
    }

    private static List<string> SplitNoteIntoBlocks(string text)
    {
        return text
            .Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Trim())
            .Where(block => !string.IsNullOrWhiteSpace(block))
            .ToList();
    }

    private static string GetFirstNonEmptyLine(string block)
    {
        if (string.IsNullOrWhiteSpace(block))
        {
            return "";
        }

        return block
            .Split(new[] { '\n' }, StringSplitOptions.None)
            .Select(line => (line ?? "").Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
    }

    private static string BuildContactDisplayName(string fullName, string contactAccountCode)
    {
        if (!string.IsNullOrWhiteSpace(fullName))
        {
            return fullName.Trim();
        }

        return "Unknown Contact (" + (contactAccountCode ?? "") + ")";
    }

    private static string BuildContactEngagementPhrase(int opens, int clicks)
    {
        List<string> parts = new List<string>();

        if (clicks > 0)
        {
            parts.Add("clicked " + DescribeCount(clicks));
        }

        if (opens > 0)
        {
            parts.Add("opened " + DescribeCount(opens));
        }

        if (parts.Count == 0)
        {
            return "no engagement";
        }

        return string.Join(", ", parts);
    }

    private static string DescribeCount(int count)
    {
        if (count == 1)
        {
            return "1";
        }

        if (count == 2)
        {
            return "2";
        }

        return count + " times";
    }

    private static ApiClient BuildClient(string uri, string apiUser, string secret, string key)
    {
        Jwt auth = new Jwt
        {
            APIUserID = apiUser,
            Secret = secret,
            Key = key,
            UngerboeckURI = uri,
            AutoRefresh = new AutoRefresh()
        };

        return new ApiClient(auth);
    }

    private static string NormalizeAccountCode(string raw)
    {
        raw = (raw ?? "").Trim();
        bool allDigits = raw.Length > 0 && raw.All(char.IsDigit);
        return allDigits ? raw.PadLeft(8, '0') : raw;
    }

    private static string RequireEnvironmentVariable(string name)
    {
        return (Environment.GetEnvironmentVariable(name) ?? "").Trim();
    }

    private static int ParseIntSafe(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        value = value.Trim();

        int intValue;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            return intValue;
        }

        double doubleValue;
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out doubleValue))
        {
            return (int)Math.Round(doubleValue);
        }

        return 0;
    }

    private static int GetSinglePreparedMetric(IEnumerable<int> values, string fieldName)
    {
        List<int> distinct = values.Distinct().ToList();
        if (distinct.Count != 1)
        {
            throw new FormatException(
                "The prepared file contains multiple values for " + fieldName + ". " +
                "Use one Mailchimp campaign per import.");
        }

        return distinct[0];
    }

    private static DateTime ParseCampaignSentDate(string value)
    {
        DateTime parsed;
        if (!DateTime.TryParseExact(
                (value ?? "").Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsed))
        {
            throw new FormatException(
                "Campaign Sent Date is missing or invalid in prepared column M. " +
                "Use the launcher to select the Mailchimp sent date.");
        }

        return parsed.Date;
    }

    private static int ParseIntOrDefault(string value, int defaultValue)
    {
        int parsed;
        if (int.TryParse(value, out parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static int ColLetterToIndex(string col)
    {
        col = (col ?? "").Trim().ToUpperInvariant();
        int sum = 0;

        for (int i = 0; i < col.Length; i++)
        {
            if (col[i] < 'A' || col[i] > 'Z')
            {
                continue;
            }

            sum *= 26;
            sum += (col[i] - 'A' + 1);
        }

        return sum - 1;
    }

    private static string GetCol(List<string> cols, int idx)
    {
        if (idx < 0 || idx >= cols.Count)
        {
            return "";
        }

        return cols[idx] ?? "";
    }

    private static List<string> ParseCsvLine(string line)
    {
        List<string> result = new List<string>();
        if (line == null)
        {
            return result;
        }

        StringBuilder sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        result.Add(sb.ToString());
        return result;
    }

    private static string EscapeOData(string value)
    {
        return (value ?? "").Replace("'", "''");
    }

    private static string EscapeCsv(string value)
    {
        if (value == null)
        {
            return "";
        }

        value = value.Replace("\r", " ").Replace("\n", " ");
        if (value.Contains(",") || value.Contains("\""))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    private sealed class CsvRow
    {
        public int EventId;
        public string CampaignType;
        public string SalespersonName;
        public string SalespersonAccountCode;
        public string ClickTypeCode;
        public string OpenTypeCode;
        public string Email;
        public int Clicks;
        public int Opens;
        public string FirstName;
        public string LastName;
        public string ContactAccountCode;
        public DateTime CampaignSentDate;
        public int CampaignEmailsSent;
        public int CampaignResponsePercentage;

        public string FullName
        {
            get
            {
                string full = ((FirstName ?? "") + " " + (LastName ?? "")).Trim();
                return full;
            }
        }
    }

    private sealed class ExhibitorAggregate
    {
        public int EventId;
        public string OrgAccountCode;
        public string CampaignType;
        public DateTime CampaignSentDate;
        public string SalespersonName;
        public string SalespersonAccountCode;
        public string ClickTypeCode;
        public string OpenTypeCode;
        public int TotalClicks;
        public int TotalOpens;

        private readonly Dictionary<string, ContactEngagement> _contactsByAccount =
            new Dictionary<string, ContactEngagement>(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<ContactEngagement> Contacts
        {
            get { return _contactsByAccount.Values; }
        }

        public void AddOrMergeContact(CsvRow row)
        {
            ContactEngagement contact;
            if (!_contactsByAccount.TryGetValue(row.ContactAccountCode, out contact))
            {
                contact = new ContactEngagement();
                contact.ContactAccountCode = row.ContactAccountCode;
                contact.Email = row.Email;
                contact.FullName = row.FullName;
                _contactsByAccount[row.ContactAccountCode] = contact;
            }

            if (string.IsNullOrWhiteSpace(contact.FullName) && !string.IsNullOrWhiteSpace(row.FullName))
            {
                contact.FullName = row.FullName;
            }

            if (string.IsNullOrWhiteSpace(contact.Email) && !string.IsNullOrWhiteSpace(row.Email))
            {
                contact.Email = row.Email;
            }

            contact.Clicks += row.Clicks;
            contact.Opens += row.Opens;
        }

        public ContactSlots GetContactSlots()
        {
            ContactSlots slots = new ContactSlots();

            List<string> contactCodes = Contacts
                .Select(c => (c.ContactAccountCode ?? "").Trim())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(7)
                .ToList();

            if (contactCodes.Count > 0) slots.MainContact = contactCodes[0];
            if (contactCodes.Count > 1) slots.BoothContact = contactCodes[1];
            if (contactCodes.Count > 2) slots.Add1 = contactCodes[2];
            if (contactCodes.Count > 3) slots.Add2 = contactCodes[3];
            if (contactCodes.Count > 4) slots.Add3 = contactCodes[4];
            if (contactCodes.Count > 5) slots.Add4 = contactCodes[5];
            if (contactCodes.Count > 6) slots.Add5 = contactCodes[6];

            return slots;
        }
    }

    private sealed class ContactEngagement
    {
        public string ContactAccountCode;
        public string FullName;
        public string Email;
        public int Clicks;
        public int Opens;

        public string SortKey
        {
            get
            {
                return ((FullName ?? "") + "|" + (ContactAccountCode ?? "")).Trim().ToUpperInvariant();
            }
        }
    }

    private sealed class ContactSlots
    {
        public string MainContact;
        public string BoothContact;
        public string Add1;
        public string Add2;
        public string Add3;
        public string Add4;
        public string Add5;
    }

    private sealed class CampaignRoster
    {
        public List<CampaignRecipient> Recipients { get; } = new List<CampaignRecipient>();
        public List<SkippedCampaignRecipient> ContactsWithoutPrimaryAccounts { get; } =
            new List<SkippedCampaignRecipient>();
        public List<SkippedCampaignRecipient> SkippedMissingAccounts { get; } =
            new List<SkippedCampaignRecipient>();
        public Dictionary<string, string> ContactToOrgAccount { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SkippedCampaignRecipient
    {
        public string ContactAccountCode = "";
        public int Opens;
        public int Clicks;
    }

    private sealed class CampaignRecipient
    {
        public string AccountCode = "";
        public string ContactAccountCode = "";
        public string OrgAccountCode = "";
        public string TargetKind = "";
        public int Opens;
        public int Clicks;
        public int EmailsSent;
        public string Status = "";
    }

    private sealed class CampaignSyncPlan
    {
        public CampaignsModel? ExistingCampaign;
        public List<CampaignDetailsModel> ExistingDetails = new List<CampaignDetailsModel>();
    }

    private sealed class CampaignSyncResult
    {
        public string CampaignId = "";
        public bool ReusedExistingCampaign;
        public int DetailsAdded;
        public int DetailsUpdated;
        public int DetailsUnchanged;
        public int DetailsSkippedInvalidAccount;
    }

    private enum NoteUpsertResult
    {
        None = 0,
        Added = 1,
        Updated = 2
    }
}
