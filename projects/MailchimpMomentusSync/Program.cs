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
    // This block is the single source of truth for the Mailchimp CSV layout.
    // If the source export ever changes, update these constants first.
    //
    // Final confirmed layout from Kyle:
    //   A = Event ID
    //   B = Campaign Type
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
    //   - Title = <Campaign Type> - YYYY-DD-MM
    //   - Duplicate handling = update existing note instead of adding another
    //
    // New enhancement requested here:
    //   The BODY of the note should be append-friendly across campaigns.
    //   That means the note body is now built as one or more campaign blocks:
    //
    //     Campaign A - 2026-12-03
    //     John Smith - clicked once, opened once
    //     Mary Jones - opened 2
    //
    //     Campaign B - 2026-12-03
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
        List<CsvRow> rows = LoadRows(csvPath);

        Console.WriteLine("CSV rows read: " + rows.Count);
        if (rows.Count == 0)
        {
            WriteAuditAndCompleteEmptyFile(
                csvPath,
                completeDir,
                "NO_ROWS",
                "No valid rows found in file. Check the configured columns and confirm opens/clicks are populated.");
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

        Dictionary<string, ExhibitorAggregate> aggregates =
            AggregateRowsByResolvedOrg(apiClient, ORG_CODE, rows);

        Console.WriteLine("Unique exhibitor org accounts to process: " + aggregates.Count);
        if (aggregates.Count == 0)
        {
            WriteAuditAndCompleteEmptyFile(
                csvPath,
                completeDir,
                "NO_ORG_MATCH",
                "No rows resolved to an org PrimaryAccount. Check the contact account codes in column L.");
            return;
        }

        Console.WriteLine("Loading existing exhibitors for Event " + eventId + "...");
        Dictionary<string, int> existingByAccount =
            LoadExistingExhibitorsByAccount(apiClient, ORG_CODE, eventId);
        Console.WriteLine("Existing exhibitors found: " + existingByAccount.Count);

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

        foreach (KeyValuePair<string, ExhibitorAggregate> kvp in aggregates.OrderBy(k => k.Key))
        {
            string orgAccountCode = kvp.Key;
            ExhibitorAggregate exhibitorData = kvp.Value;

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
                        exhibitorData.OpenTypeCode + " - Open",
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
                        exhibitorData.ClickTypeCode + " - Click",
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

            string noteTitle = BuildEngagementNoteTitle(exhibitorData.CampaignType, DateTime.Now);
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
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string auditPath = Path.Combine(
            completeDir,
            "momentus_mailchimp_sync_audit_" + Path.GetFileNameWithoutExtension(csvPath) + "_" + timestamp + ".csv");

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

                if (row.EventId <= 0)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(row.ContactAccountCode))
                {
                    continue;
                }

                if (row.Opens <= 0 && row.Clicks <= 0)
                {
                    continue;
                }

                rows.Add(row);
            }
        }

        return rows;
    }

    private static Dictionary<string, ExhibitorAggregate> AggregateRowsByResolvedOrg(
        ApiClient apiClient,
        string orgCode,
        List<CsvRow> rows)
    {
        Dictionary<string, ExhibitorAggregate> exhibitorMap =
            new Dictionary<string, ExhibitorAggregate>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string> contactToOrgCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (CsvRow row in rows)
        {
            string orgAccountCode;
            if (!contactToOrgCache.TryGetValue(row.ContactAccountCode, out orgAccountCode))
            {
                try
                {
                    orgAccountCode = ResolveOrgAccountFromContact(apiClient, orgCode, row.ContactAccountCode);
                }
                catch
                {
                    orgAccountCode = null;
                }

                contactToOrgCache[row.ContactAccountCode] = orgAccountCode;
            }

            if (string.IsNullOrWhiteSpace(orgAccountCode))
            {
                continue;
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

        string text = subject + " (count=" + count + ") - imported " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");

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

        apiClient.Endpoints.Activities.Add(activityToAdd);
        return true;
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

    private static string BuildEngagementNoteTitle(string campaignType, DateTime runDate)
    {
        string safeCampaignType = (campaignType ?? "").Trim();

        if (string.IsNullOrWhiteSpace(safeCampaignType))
        {
            safeCampaignType = "Engagement";
        }

        return safeCampaignType + " - " + runDate.ToString("yyyy-dd-MM");
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

    private enum NoteUpsertResult
    {
        None = 0,
        Added = 1,
        Updated = 2
    }
}
