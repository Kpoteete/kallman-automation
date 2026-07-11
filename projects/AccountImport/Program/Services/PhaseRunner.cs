using AccountImport.Models;

namespace AccountImport.Services;

public sealed class PhaseRunner
{
    private readonly AppConfig _config;
    private readonly ExcelService _excel;
    private readonly IMomentusApiService _api;
    private readonly AuditLogger _audit;
    private bool _liveConfirmed;

    public PhaseRunner(AppConfig config, ExcelService excel, IMomentusApiService api, AuditLogger audit)
    {
        _config = config;
        _excel = excel;
        _api = api;
        _audit = audit;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _config.EnsureFolders();
        var session = new SessionContext();
        session.Phase4AuditLogFile = _audit.AuditPath;
        session.TrackFile(_audit.AuditPath);

        Console.WriteLine(_config.DryRun
            ? "DRY_RUN mode is ON. This run will read files, search Momentus, write outputs, and write audit logs only."
            : "LIVE mode is ON. Auto-run is enabled: the program will run all phases without Y/IMPORT step confirmations.");


        session.Phase0SourceFile = _excel.GetSinglePhase0File(_config.Phase0Folder);
        session.TrackFile(session.Phase0SourceFile);

        Console.WriteLine($"Phase 0 source file: {session.Phase0SourceFile}");
        _excel.ValidateWorkbook(session.Phase0SourceFile);
        PromptForImportId();

        Console.WriteLine("API client initialized. Phase 1 will test Momentus access with actual Contact Email lookups.");
        await _api.EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

        await RunPhase1Async(session, cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Phase 1 complete. Auto-continuing to Phase 2.");

        await RunPhase2Async(session, cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Phase 2 complete. Auto-continuing to Phase 3.");

        await RunPhase3Async(session, cancellationToken).ConfigureAwait(false);
        await RunImportIdKeywordUpdateAsync(session, cancellationToken).ConfigureAwait(false);
        await RunAffiliationPromptAsync(session, cancellationToken).ConfigureAwait(false);

        Console.WriteLine("Phase 4 verification complete. Auto-continuing to archive/cleanup.");

        RunPhase5(session);
        PrintFinalSummary(session);
    }


    private static void WriteProgress(string label, int current, int total, string detail = "")
    {
        if (total <= 0)
        {
            Console.WriteLine($"{label}: [------------------------------] 0/0 (100%) | no rows");
            return;
        }

        int barWidth = 30;
        double ratio = Math.Clamp((double)current / total, 0, 1);
        int filled = (int)Math.Round(ratio * barWidth);
        string bar = new string('#', filled) + new string('-', barWidth - filled);
        int percent = (int)Math.Round(ratio * 100);
        string suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : " | " + detail;

        Console.Write($"\r{label}: [{bar}] {current}/{total} ({percent}%){suffix}   ");

        if (current >= total)
        {
            Console.WriteLine();
        }
    }

    private static void WriteSection(string message)
    {
        Console.WriteLine();
        Console.WriteLine(message);
    }

    private async Task RunPhase1Async(SessionContext session, CancellationToken cancellationToken)
    {
        Console.WriteLine("Starting Phase 1: Lookup and Disregard.");

        List<ImportRow> rows = _excel.LoadRows(session.Phase0SourceFile);
        var duplicateRows = new List<int>();
        var nonDuplicateRows = new List<int>();
        var reviewRows = new List<int>();
        var annotations = new Dictionary<int, RowAnnotation>();
        var emailLookupCache = new Dictionary<string, ContactLookupResult>(StringComparer.OrdinalIgnoreCase);
        int totalRows = rows.Count;
        int processedRows = 0;
        int apiLookupCount = 0;
        int cacheHitCount = 0;

        WriteProgress("Phase 1 duplicate-email check", 0, totalRows, "starting");

        foreach (ImportRow row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedRows++;

            var annotation = BaseAnnotation(row);
            annotations[row.WorksheetRowNumber] = annotation;

            if (!TextUtil.IsValidEmailForLookup(row.ContactEmail))
            {
                reviewRows.Add(row.WorksheetRowNumber);
                annotation.DuplicateFound = string.Empty;
                annotation.ImportStatus = "Skipped";
                annotation.ImportMessage = "Missing or invalid Contact Email. Withheld from import for human review.";
                session.Summary.RowsSkipped++;

                Log(row, "Phase 1", "SearchContactByEmail", "Skipped", string.Empty,
                    "Missing or invalid Contact Email.", string.Empty);
                WriteProgress("Phase 1 duplicate-email check", processedRows, totalRows,
                    $"duplicates +{duplicateRows.Count}, clean +{nonDuplicateRows.Count}, review +{reviewRows.Count}, API +{apiLookupCount}, cache +{cacheHitCount}");
                continue;
            }

            try
            {
                bool emailCacheHit = emailLookupCache.TryGetValue(row.NormalizedEmail, out ContactLookupResult existingContact);
                if (emailCacheHit)
                {
                    cacheHitCount++;
                }
                else
                {
                    apiLookupCount++;
                    existingContact = await _api.FindContactByEmailAsync(row.NormalizedEmail, cancellationToken).ConfigureAwait(false);
                    emailLookupCache[row.NormalizedEmail] = existingContact;
                }
                if (existingContact.Found)
                {
                    duplicateRows.Add(row.WorksheetRowNumber);
                    annotation.DuplicateFound = "YES";
                    annotation.ImportStatus = "Skipped";
                    annotation.ImportMessage = existingContact.Message;
                    session.Summary.DuplicateContactsFound++;
                    session.TrackDuplicateContact(existingContact.AccountCode);

                    Log(row, "Phase 1", "SearchContactByEmail", "Success", existingContact.AccountCode,
                        existingContact.Message, string.Empty);
                }
                else
                {
                    nonDuplicateRows.Add(row.WorksheetRowNumber);
                    annotation.DuplicateFound = "NO";
                    annotation.ImportStatus = string.Empty;
                    annotation.ImportMessage = "No existing Momentus contact found by email.";

                    Log(row, "Phase 1", "SearchContactByEmail", "Success", string.Empty,
                        "No existing Momentus contact found by email.", string.Empty);
                }

                WriteProgress("Phase 1 duplicate-email check", processedRows, totalRows,
                    $"duplicates +{duplicateRows.Count}, clean +{nonDuplicateRows.Count}, review +{reviewRows.Count}, API +{apiLookupCount}, cache +{cacheHitCount}");
            }
            catch (Exception ex)
            {
                reviewRows.Add(row.WorksheetRowNumber);
                annotation.ImportStatus = "Failed";
                annotation.ImportMessage = "Email lookup failed. Withheld from import. " + TextUtil.Shorten(ex.Message, 300);
                session.Summary.Failures++;

                Log(row, "Phase 1", "SearchContactByEmail", "Failed", string.Empty, string.Empty, ex.Message);
                WriteProgress("Phase 1 duplicate-email check", processedRows, totalRows,
                    $"duplicates +{duplicateRows.Count}, clean +{nonDuplicateRows.Count}, review +{reviewRows.Count}, API +{apiLookupCount}, cache +{cacheHitCount}");
            }
        }

        Console.WriteLine($"Phase 1 email lookup summary: {apiLookupCount} Momentus API lookup(s), {cacheHitCount} cache hit(s), {duplicateRows.Count} duplicate(s), {reviewRows.Count} review row(s).");

        session.Summary.NonDuplicatesProcessed = nonDuplicateRows.Count;

        session.Phase1DuplicateFile = ExcelService.TimestampedPath(_config.Phase1Folder, "phase1_duplicate_contacts", session.Timestamp);
        session.Phase1NonDuplicateFile = ExcelService.TimestampedPath(_config.Phase1Folder, "phase1_non_duplicate_contacts", session.Timestamp);

        _excel.CreateFilteredCopy(session.Phase0SourceFile, session.Phase1DuplicateFile, duplicateRows, annotations);
        _excel.CreateFilteredCopy(session.Phase0SourceFile, session.Phase1NonDuplicateFile, nonDuplicateRows, annotations);
        session.TrackFile(session.Phase1DuplicateFile);
        session.TrackFile(session.Phase1NonDuplicateFile);

        if (reviewRows.Count > 0)
        {
            session.Phase1ReviewRequiredFile = ExcelService.TimestampedPath(_config.Phase1Folder, "phase1_review_required_not_imported", session.Timestamp);
            _excel.CreateFilteredCopy(session.Phase0SourceFile, session.Phase1ReviewRequiredFile, reviewRows, annotations);
            session.TrackFile(session.Phase1ReviewRequiredFile);
            Console.WriteLine($"Phase 1 created a review-required file for {reviewRows.Count} row(s): {session.Phase1ReviewRequiredFile}");
        }

        Console.WriteLine($"Phase 1 duplicate file: {session.Phase1DuplicateFile}");
        Console.WriteLine($"Phase 1 non-duplicate file: {session.Phase1NonDuplicateFile}");
    }

    private async Task RunPhase2Async(SessionContext session, CancellationToken cancellationToken)
    {
        Console.WriteLine("Starting Phase 2: Lookup and Match.");

        List<ImportRow> rows = _excel.LoadRows(session.Phase1NonDuplicateFile);
        var existingRows = new List<int>();
        var newRows = new List<int>();
        var reviewRows = new List<int>();
        var annotations = new Dictionary<int, RowAnnotation>();

        // Phase 2 can otherwise be slow because repeated company rows would each trigger
        // the same Momentus account lookup. Cache by the normalized duplicate-check key
        // so each Company/Website + Country + Market Segment group is searched once per run.
        var accountLookupCache = new Dictionary<string, AccountLookupResult>(StringComparer.OrdinalIgnoreCase);
        int totalRows = rows.Count;
        int processedRows = 0;
        int apiLookupCount = 0;
        int cacheHitCount = 0;

        WriteProgress("Phase 2 account lookup/match", 0, totalRows, "starting");

        foreach (ImportRow row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedRows++;

            var annotation = BaseAnnotation(row);
            annotations[row.WorksheetRowNumber] = annotation;

            if (!row.HasRequiredAccountKeyFields(_config))
            {
                reviewRows.Add(row.WorksheetRowNumber);
                annotation.AccountMatchFound = string.Empty;
                annotation.ImportStatus = "Skipped";
                annotation.ImportMessage = "Missing Company Name, Country, or required Market Segment. Withheld from import.";
                session.Summary.RowsSkipped++;

                Log(row, "Phase 2", "SearchOrganizationAccount", "Skipped", string.Empty,
                    "Missing Company Name, Country, or required Market Segment.", string.Empty);
                WriteProgress("Phase 2 account lookup/match", processedRows, totalRows,
                    $"existing +{existingRows.Count}, new +{newRows.Count}, review +{reviewRows.Count}, API +{apiLookupCount}, cache +{cacheHitCount}");
                continue;
            }

            try
            {
                string cacheKey = BuildAccountLookupCacheKey(row);
                bool cacheHit = accountLookupCache.TryGetValue(cacheKey, out AccountLookupResult? match);

                if (cacheHit && match is not null)
                {
                    cacheHitCount++;
                }
                else
                {
                    apiLookupCount++;
                    match = await _api.FindOrganizationAccountAsync(
                        row.CompanyName,
                        row.MarketSegmentMajor,
                        row.Country,
                        row.WebsiteRootDomain,
                        cancellationToken).ConfigureAwait(false);
                    accountLookupCache[cacheKey] = match;
                }

                if (match.Found)
                {
                    existingRows.Add(row.WorksheetRowNumber);
                    annotation.AccountMatchFound = "YES";
                    annotation.AccountCodeUsed = match.AccountCode;
                    annotation.AccountCodeToWrite = match.AccountCode;
                    annotation.ImportMessage = match.Message;
                    session.Summary.ExistingAccountsMatched++;
                    session.TrackTouchedAccount(match.AccountCode, created: false);

                    Log(row, "Phase 2", "SearchOrganizationAccount", "Success", match.AccountCode,
                        match.Message, string.Empty);
                }
                else
                {
                    newRows.Add(row.WorksheetRowNumber);
                    annotation.AccountMatchFound = "NO";
                    annotation.AccountCodeToWrite = string.Empty;
                    annotation.ImportMessage = "No organization account match found by Company/Website + Country + Market Segment. New account needed.";

                    Log(row, "Phase 2", "SearchOrganizationAccount", "Success", string.Empty,
                        "No organization account match found by Company/Website + Country + Market Segment. New account needed.", string.Empty);
                }

                WriteProgress("Phase 2 account lookup/match", processedRows, totalRows,
                    $"existing +{existingRows.Count}, new +{newRows.Count}, review +{reviewRows.Count}, API +{apiLookupCount}, cache +{cacheHitCount}");
            }
            catch (Exception ex)
            {
                reviewRows.Add(row.WorksheetRowNumber);
                annotation.ImportStatus = "Failed";
                annotation.ImportMessage = "Organization account lookup failed. Withheld from import. " + TextUtil.Shorten(ex.Message, 300);
                session.Summary.Failures++;

                Log(row, "Phase 2", "SearchOrganizationAccount", "Failed", string.Empty, string.Empty, ex.Message);
                WriteProgress("Phase 2 account lookup/match", processedRows, totalRows,
                    $"existing +{existingRows.Count}, new +{newRows.Count}, review +{reviewRows.Count}, API +{apiLookupCount}, cache +{cacheHitCount}");
            }
        }

        session.Phase2ExistingAccountsFile = ExcelService.TimestampedPath(_config.Phase2Folder, "phase2_existing_organization_accounts", session.Timestamp);
        session.Phase2NewAccountsFile = ExcelService.TimestampedPath(_config.Phase2Folder, "phase2_new_organization_accounts_needed", session.Timestamp);

        _excel.CreateFilteredCopy(session.Phase1NonDuplicateFile, session.Phase2ExistingAccountsFile, existingRows, annotations);
        _excel.CreateFilteredCopy(session.Phase1NonDuplicateFile, session.Phase2NewAccountsFile, newRows, annotations);
        session.TrackFile(session.Phase2ExistingAccountsFile);
        session.TrackFile(session.Phase2NewAccountsFile);

        if (reviewRows.Count > 0)
        {
            session.Phase2ReviewRequiredFile = ExcelService.TimestampedPath(_config.Phase2Folder, "phase2_review_required_not_imported", session.Timestamp);
            _excel.CreateFilteredCopy(session.Phase1NonDuplicateFile, session.Phase2ReviewRequiredFile, reviewRows, annotations);
            session.TrackFile(session.Phase2ReviewRequiredFile);
            Console.WriteLine($"Phase 2 created a review-required file for {reviewRows.Count} row(s): {session.Phase2ReviewRequiredFile}");
        }

        Console.WriteLine($"Phase 2 existing accounts file: {session.Phase2ExistingAccountsFile}");
        Console.WriteLine($"Phase 2 new accounts needed file: {session.Phase2NewAccountsFile}");
        Console.WriteLine($"Phase 2 lookup summary: {apiLookupCount} Momentus API lookup(s), {cacheHitCount} cache hit(s), {accountLookupCache.Count} unique account key(s).");
    }

    private static string BuildAccountLookupCacheKey(ImportRow row)
    {
        string companyKey = TextUtil.CanonicalCompanyName(row.CompanyName);
        string websiteKey = TextUtil.RootDomain(row.WebsiteRootDomain);
        string countryKey = TextUtil.CleanKeyField(row.Country).ToUpperInvariant();
        string segmentKey = TextUtil.CleanKeyField(row.MarketSegmentMajor).ToUpperInvariant();

        return string.Join("|", companyKey, websiteKey, countryKey, segmentKey);
    }

    private async Task RunPhase3Async(SessionContext session, CancellationToken cancellationToken)
    {
        Console.WriteLine("Starting Phase 3: Import.");
        EnsureLiveImportConfirmedIfNeeded();
        Console.WriteLine("Phase 3 import confirmed. Beginning write operations and progress tracking.");

        var existingAnnotations = await ImportContactsForExistingAccountsAsync(session, cancellationToken).ConfigureAwait(false);
        var newAnnotations = await ImportNewAccountsAndContactsAsync(session, cancellationToken).ConfigureAwait(false);

        session.Phase4ExistingAccountsFinalFile = ExcelService.TimestampedPath(_config.Phase4Folder, "phase4_existing_accounts_final_working_file", session.Timestamp);
        session.Phase4NewAccountsFinalFile = ExcelService.TimestampedPath(_config.Phase4Folder, "phase4_new_accounts_final_working_file", session.Timestamp);

        _excel.CreateAnnotatedCopy(session.Phase2ExistingAccountsFile, session.Phase4ExistingAccountsFinalFile, existingAnnotations);
        _excel.CreateAnnotatedCopy(session.Phase2NewAccountsFile, session.Phase4NewAccountsFinalFile, newAnnotations);
        session.TrackFile(session.Phase4ExistingAccountsFinalFile);
        session.TrackFile(session.Phase4NewAccountsFinalFile);
        session.TrackFile(_audit.AuditPath);

        Console.WriteLine($"Phase 4 audit log: {_audit.AuditPath}");
        Console.WriteLine($"Phase 4 existing accounts final file: {session.Phase4ExistingAccountsFinalFile}");
        Console.WriteLine($"Phase 4 new accounts final file: {session.Phase4NewAccountsFinalFile}");
    }

    private async Task<Dictionary<int, RowAnnotation>> ImportContactsForExistingAccountsAsync(SessionContext session, CancellationToken cancellationToken)
    {
        var annotations = new Dictionary<int, RowAnnotation>();
        List<ImportRow> rows = _excel.LoadRows(session.Phase2ExistingAccountsFile);
        int totalRows = rows.Count;
        int processedRows = 0;
        int successStart = session.Summary.ContactsCreated;
        int failureStart = session.Summary.Failures;

        WriteProgress("Phase 3A existing-account contacts", 0, totalRows, "starting");

        foreach (ImportRow row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedRows++;
            var annotation = BaseAnnotation(row);
            annotations[row.WorksheetRowNumber] = annotation;

            string accountCode = TextUtil.Clean(row.AccountCode);
            if (string.IsNullOrWhiteSpace(accountCode))
            {
                annotation.ImportStatus = "Failed";
                annotation.ImportMessage = "Missing Account Code in existing-account file. Contact was not created.";
                session.Summary.Failures++;
                Log(row, "Phase 3A", "CreateContact", "Failed", string.Empty, string.Empty, annotation.ImportMessage);
                WriteProgress("Phase 3A existing-account contacts", processedRows, totalRows,
                    $"contacts +{session.Summary.ContactsCreated - successStart}, failures +{session.Summary.Failures - failureStart}");
                continue;
            }

            session.TrackTouchedAccount(accountCode, created: false);

            await FillBlankExistingAccountFieldsAsync(
                session,
                row,
                session.Phase2ExistingAccountsFile,
                accountCode,
                annotation,
                "Phase 3A",
                cancellationToken).ConfigureAwait(false);


            await CreateContactForRowAsync(session, row, session.Phase2ExistingAccountsFile, accountCode, annotation, "Phase 3A", cancellationToken)
                .ConfigureAwait(false);
            WriteProgress("Phase 3A existing-account contacts", processedRows, totalRows,
                $"contacts +{session.Summary.ContactsCreated - successStart}, failures +{session.Summary.Failures - failureStart}");
        }

        return annotations;
    }

    private async Task<Dictionary<int, RowAnnotation>> ImportNewAccountsAndContactsAsync(SessionContext session, CancellationToken cancellationToken)
    {
        var annotations = new Dictionary<int, RowAnnotation>();
        List<ImportRow> rows = _excel.LoadRows(session.Phase2NewAccountsFile);
        int totalRows = rows.Count;
        int processedRows = 0;
        int accountStart = session.Summary.NewAccountsCreated;
        int contactStart = session.Summary.ContactsCreated;
        int relationshipStart = session.Summary.RelationshipsCreated;
        int failureStart = session.Summary.Failures;

        WriteProgress("Phase 3B new-account contacts", 0, totalRows, "starting");

        // One new organization account per (Company Name OR Website Root Domain) + Country + Market Segment group.
        // Market Segment is used for matching/grouping and is now written to organization accounts when available.
        // All contacts in that group reuse the same organization AccountCode as the relationship master account.
        var accountCodeByGroupKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void WritePhase3BProgress()
        {
            WriteProgress("Phase 3B new-account contacts", processedRows, totalRows,
                $"accounts +{session.Summary.NewAccountsCreated - accountStart}, contacts +{session.Summary.ContactsCreated - contactStart}, relationships +{session.Summary.RelationshipsCreated - relationshipStart}, failures +{session.Summary.Failures - failureStart}");
        }

        foreach (ImportRow row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedRows++;
            var annotation = BaseAnnotation(row);
            annotations[row.WorksheetRowNumber] = annotation;

            if (!row.HasRequiredAccountKeyFields(_config))
            {
                annotation.ImportStatus = "Skipped";
                annotation.ImportMessage = "Missing Company Name, Country, or required Market Segment. Account/contact not created.";
                session.Summary.RowsSkipped++;
                Log(row, "Phase 3B", "CreateOrganizationAccount", "Skipped", string.Empty, annotation.ImportMessage, string.Empty);
                WritePhase3BProgress();
                continue;
            }

            string accountCode = string.Empty;
            string groupKey = BuildAccountGroupKey(row);

            if (accountCodeByGroupKey.TryGetValue(groupKey, out string? cachedAccountCode))
            {
                accountCode = cachedAccountCode;
                annotation.AccountCodeToWrite = accountCode;
                annotation.AccountCodeUsed = accountCode;
                annotation.ImportMessage = "Reused organization AccountCode from earlier row in same Company/Website + Country + Market Segment group.";

                session.TrackTouchedAccount(accountCode, created: false);
                Log(row, "Phase 3B", "CreateOrganizationAccount", "Skipped", accountCode,
                    annotation.ImportMessage, string.Empty);

                await CreateContactForRowAsync(session, row, session.Phase2NewAccountsFile, accountCode, annotation, "Phase 3B", cancellationToken)
                    .ConfigureAwait(false);
                WritePhase3BProgress();
                continue;
            }

            try
            {
                AccountLookupResult recheck = await _api.FindOrganizationAccountAsync(
                    row.CompanyName,
                    row.MarketSegmentMajor,
                    row.Country,
                    row.WebsiteRootDomain,
                    cancellationToken).ConfigureAwait(false);

                if (recheck.Found)
                {
                    accountCode = recheck.AccountCode;
                    annotation.AccountCodeToWrite = accountCode;
                    annotation.AccountCodeUsed = accountCode;
                    annotation.AccountMatchFound = "YES";
                    annotation.ImportMessage = "Account was found during pre-create recheck by Company/Website + Country + Market Segment. New account creation skipped to prevent duplicate.";

                    session.TrackTouchedAccount(accountCode, created: false);
                    Log(row, "Phase 3B", "CreateOrganizationAccount", "Skipped", accountCode,
                        "Account found during pre-create recheck; creation skipped.", string.Empty);

                    await FillBlankExistingAccountFieldsAsync(
                        session,
                        row,
                        session.Phase2NewAccountsFile,
                        accountCode,
                        annotation,
                        "Phase 3B",
                        cancellationToken).ConfigureAwait(false);

                }
                else if (_config.DryRun)
                {
                    accountCode = "[DRY-RUN-NEW-ACCOUNT]";
                    annotation.AccountCodeToWrite = accountCode;
                    annotation.AccountCodeUsed = accountCode;
                    annotation.ImportStatus = "Skipped";
                    annotation.ImportMessage = "DRY_RUN: would create organization account.";
                    session.Summary.DryRunAccountsPrepared++;

                    Log(row, "Phase 3B", "CreateOrganizationAccount", "Skipped", accountCode,
                        "DRY_RUN: would create organization account.", string.Empty);
                }
                else
                {
                    IReadOnlyDictionary<string, string> accountFields = _excel.ReadAccountFields(
                        session.Phase2NewAccountsFile,
                        row.WorksheetRowNumber);

                    ApiWriteResult createResult = await _api.CreateOrganizationAccountAsync(
                        row.CompanyName,
                        row.MarketSegmentMajor,
                        row.Country,
                        accountFields,
                        cancellationToken).ConfigureAwait(false);

                    if (!createResult.Success || string.IsNullOrWhiteSpace(createResult.AccountCode))
                    {
                        annotation.ImportStatus = "Failed";
                        annotation.ImportMessage = "Organization account creation failed. " + createResult.ErrorMessage;
                        session.Summary.Failures++;
                        Log(row, "Phase 3B", "CreateOrganizationAccount", "Failed", string.Empty,
                            createResult.Message, createResult.ErrorMessage ?? string.Empty);
                        WritePhase3BProgress();
                        continue;
                    }

                    accountCode = createResult.AccountCode;
                    annotation.AccountCodeToWrite = accountCode;
                    annotation.AccountCodeUsed = accountCode;
                    annotation.ImportMessage = createResult.Message;
                    session.Summary.NewAccountsCreated++;
                    session.TrackTouchedAccount(accountCode, created: true);

                    Log(row, "Phase 3B", "CreateOrganizationAccount", "Success", accountCode,
                        createResult.Message, string.Empty);
                }
            }
            catch (Exception ex)
            {
                AccountLookupResult afterFailureMatch = AccountLookupResult.NotFound();
                if (!RetryPolicy.LooksLikeValidationError(ex))
                {
                    try
                    {
                        afterFailureMatch = await _api.FindOrganizationAccountAsync(
                            row.CompanyName,
                            row.MarketSegmentMajor,
                            row.Country,
                            row.WebsiteRootDomain,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Preserve the original account-create failure.
                    }
                }

                if (afterFailureMatch.Found)
                {
                    accountCode = afterFailureMatch.AccountCode;
                    annotation.AccountCodeToWrite = accountCode;
                    annotation.AccountCodeUsed = accountCode;
                    annotation.ImportMessage = "Account create response failed, but exact account now exists. Using found Account Code to avoid duplicate.";
                    session.TrackTouchedAccount(accountCode, created: false);
                    Log(row, "Phase 3B", "CreateOrganizationAccount", "Success", accountCode,
                        annotation.ImportMessage, ex.Message);
                }
                else
                {
                    annotation.ImportStatus = "Failed";
                    annotation.ImportMessage = "Organization account creation failed. Contact was not attempted. " + TextUtil.Shorten(ex.Message, 300);
                    session.Summary.Failures++;
                    Log(row, "Phase 3B", "CreateOrganizationAccount", "Failed", string.Empty, string.Empty, ex.Message);
                    WritePhase3BProgress();
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(accountCode))
            {
                accountCodeByGroupKey[groupKey] = accountCode;
            }

            await CreateContactForRowAsync(session, row, session.Phase2NewAccountsFile, accountCode, annotation, "Phase 3B", cancellationToken)
                .ConfigureAwait(false);
            WritePhase3BProgress();
        }

        return annotations;
    }

    private async Task FillBlankExistingAccountFieldsAsync(
        SessionContext session,
        ImportRow row,
        string workbookPath,
        string accountCode,
        RowAnnotation annotation,
        string phase,
        CancellationToken cancellationToken)
    {
        if (!_config.ExistingAccountUpdates.UpdateBlankFieldsOnly || !_config.ExistingAccountUpdates.EnabledInLiveMode)
        {
            return;
        }

        try
        {
            IReadOnlyDictionary<string, string> accountFields = _excel.ReadAccountFields(workbookPath, row.WorksheetRowNumber);
            ApiWriteResult updateResult = await _api.UpdateBlankOrganizationAccountFieldsAsync(accountCode, accountFields, cancellationToken)
                .ConfigureAwait(false);

            if (updateResult.Success)
            {
                if (updateResult.Message.StartsWith("No blank", StringComparison.OrdinalIgnoreCase) ||
                    updateResult.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase))
                {
                    Log(row, phase, "FillBlankOrganizationFields", "Skipped", accountCode, updateResult.Message, string.Empty);
                }
                else
                {
                    session.Summary.ExistingAccountBlankFieldUpdates++;
                    Log(row, phase, "FillBlankOrganizationFields", "Success", accountCode, updateResult.Message, string.Empty);
                }
            }
            else
            {
                session.Summary.ExistingAccountBlankFieldUpdateFailures++;
                session.Summary.Failures++;
                Log(row, phase, "FillBlankOrganizationFields", "Failed", accountCode,
                    updateResult.Message, updateResult.ErrorMessage ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            session.Summary.ExistingAccountBlankFieldUpdateFailures++;
            session.Summary.Failures++;
            Log(row, phase, "FillBlankOrganizationFields", "Failed", accountCode, string.Empty, ex.Message);
        }
    }

    private async Task CreateContactForRowAsync(
        SessionContext session,
        ImportRow row,
        string workbookPath,
        string accountCode,
        RowAnnotation annotation,
        string phase,
        CancellationToken cancellationToken)
    {
        annotation.AccountCodeUsed = accountCode;
        annotation.AccountCodeToWrite = accountCode;

        if (!TextUtil.IsValidEmailForLookup(row.ContactEmail))
        {
            annotation.ImportStatus = "Skipped";
            annotation.ImportMessage = "Missing or invalid Contact Email at import time. Contact not created.";
            session.Summary.RowsSkipped++;
            Log(row, phase, "CreateContact", "Skipped", accountCode, annotation.ImportMessage, string.Empty);
            return;
        }

        try
        {
            ContactLookupResult duplicateNow = await _api.FindContactByEmailAsync(row.NormalizedEmail, cancellationToken).ConfigureAwait(false);
            if (duplicateNow.Found)
            {
                annotation.DuplicateFound = "YES";
                annotation.ImportStatus = "Skipped";
                annotation.ImportMessage = "Contact email was found during pre-create recheck. Contact creation skipped. " + duplicateNow.Message;
                session.Summary.RowsSkipped++;
                session.TrackDuplicateContact(duplicateNow.AccountCode);

                Log(row, phase, "CreateContact", "Skipped", duplicateNow.AccountCode,
                    "Contact email found during pre-create recheck. Creation skipped. " + duplicateNow.Message, string.Empty);
                return;
            }

            IReadOnlyDictionary<string, string> contactFields = _excel.ReadContactFields(workbookPath, row.WorksheetRowNumber);

            if (_config.DryRun)
            {
                annotation.ImportStatus = "Skipped";
                annotation.ImportMessage = "DRY_RUN: would create contact under Account Code " + accountCode + ".";
                session.Summary.DryRunContactsPrepared++;

                Log(row, phase, "CreateContact", "Skipped", accountCode,
                    "DRY_RUN: would create contact under account.", string.Empty);
                return;
            }

            ApiWriteResult result = await _api.CreateContactAsync(accountCode, row.CompanyName, contactFields, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                annotation.ImportStatus = "Success";
                annotation.ImportMessage = result.Message;
                session.Summary.ContactsCreated++;
                session.TrackCreatedContact(result.AccountCode);

                Log(row, phase, "CreateContact", "Success", accountCode, result.Message, string.Empty);

                if (_config.Relationship.CreateRelationshipAfterContact)
                {
                    if (string.IsNullOrWhiteSpace(result.AccountCode))
                    {
                        annotation.ImportStatus = "Failed";
                        annotation.ImportMessage = result.Message + " Relationship was not created because the contact AccountCode was not returned.";
                        session.Summary.RelationshipFailures++;
                        session.Summary.Failures++;

                        Log(row, phase, "CreateRelationship", "Failed", accountCode,
                            "Contact was created, but Momentus did not return the contact AccountCode needed for relationship creation.", string.Empty);
                    }
                    else
                    {
                        ApiWriteResult relationshipResult = await _api.CreateRelationshipAsync(accountCode, result.AccountCode, cancellationToken)
                            .ConfigureAwait(false);

                        if (relationshipResult.Success)
                        {
                            session.Summary.RelationshipsCreated++;
                            annotation.ImportMessage = result.Message + " " + relationshipResult.Message;
                            Log(row, phase, "CreateRelationship", "Success", accountCode, relationshipResult.Message, string.Empty);
                        }
                        else
                        {
                            annotation.ImportStatus = "Failed";
                            annotation.ImportMessage = result.Message + " Relationship creation failed. " + relationshipResult.ErrorMessage;
                            session.Summary.RelationshipFailures++;
                            session.Summary.Failures++;

                            Log(row, phase, "CreateRelationship", "Failed", accountCode,
                                relationshipResult.Message, relationshipResult.ErrorMessage ?? string.Empty);
                        }
                    }
                }
            }
            else
            {
                annotation.ImportStatus = "Failed";
                annotation.ImportMessage = "Contact creation failed. " + result.ErrorMessage;
                session.Summary.Failures++;

                Log(row, phase, "CreateContact", "Failed", accountCode, result.Message, result.ErrorMessage ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            annotation.ImportStatus = "Failed";
            annotation.ImportMessage = "Contact creation failed. " + TextUtil.Shorten(ex.Message, 300);
            session.Summary.Failures++;

            Log(row, phase, "CreateContact", "Failed", accountCode, string.Empty, ex.Message);
        }
    }


    private string BuildAccountGroupKey(ImportRow row)
    {
        string nameOrWebsite = _config.DuplicateCheck.UseWebsiteRootDomainForAccountMatch &&
            !string.IsNullOrWhiteSpace(row.WebsiteRootDomain)
            ? TextUtil.RootDomain(row.WebsiteRootDomain)
            : TextUtil.CleanKeyField(row.CompanyName);

        return string.Join("|", new[]
        {
            nameOrWebsite.ToUpperInvariant(),
            _config.CleanCountryForMomentus(row.Country).ToUpperInvariant(),
            _config.DuplicateCheck.UseMarketSegmentForAccountMatch
                ? TextUtil.CleanKeyField(row.MarketSegmentMajor).ToUpperInvariant()
                : string.Empty
        });
    }

    private void EnsureLiveImportConfirmedIfNeeded()
    {
        if (_config.DryRun || _liveConfirmed) return;

        if (!_config.ConfirmProductionWrites)
            throw new InvalidOperationException("Live writes were not confirmed with --confirm-production-writes.");
        Console.WriteLine("LIVE MODE ENABLED. Production writes were explicitly confirmed.");
        _liveConfirmed = true;
    }


    private async Task RunImportIdKeywordUpdateAsync(SessionContext session, CancellationToken cancellationToken)
    {
        if (!_config.ImportId.HasImportId)
        {
            return;
        }

        if (!_config.ImportId.ApplyToAllTouchedAccountsAndContacts)
        {
            return;
        }

        var accountCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string code in session.AccountCodesTouched) accountCodes.Add(code);
        foreach (string code in session.ContactCodesCreated) accountCodes.Add(code);
        foreach (string code in session.DuplicateContactCodesFound) accountCodes.Add(code);

        if (accountCodes.Count == 0)
        {
            Console.WriteLine("No account/contact codes were available for the Import ID Keyword update.");
            return;
        }

        Console.WriteLine($"Writing Import ID '{TextUtil.Clean(_config.ImportId.Value)}' to {_config.ImportId.KeywordField} for {accountCodes.Count} touched account/contact record(s).");

        int total = accountCodes.Count;
        int processed = 0;
        int updated = 0;
        int failures = 0;
        WriteProgress("Phase 4A Import ID Keyword update", 0, total, "starting");

        foreach (string accountCode in accountCodes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;

            var pseudoRow = new ImportRow(
                WorksheetRowNumber: 0,
                SourceRowNumber: 0,
                SourceFileName: "Session",
                CompanyName: string.Empty,
                AccountCode: accountCode,
                MarketSegmentMajor: string.Empty,
                Country: string.Empty,
                WebsiteRootDomain: string.Empty,
                ContactEmail: string.Empty);

            try
            {
                ApiWriteResult result = await _api.ApplyImportIdToAccountAsync(accountCode, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Success)
                {
                    updated++;
                    Log(pseudoRow, "Phase 4A", "ApplyImportIdKeyword", "Success", accountCode, result.Message, string.Empty);
                }
                else
                {
                    failures++;
                    session.Summary.Failures++;
                    Log(pseudoRow, "Phase 4A", "ApplyImportIdKeyword", "Failed", accountCode,
                        result.Message, result.ErrorMessage ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                failures++;
                session.Summary.Failures++;
                Log(pseudoRow, "Phase 4A", "ApplyImportIdKeyword", "Failed", accountCode, string.Empty, ex.Message);
            }

            WriteProgress("Phase 4A Import ID Keyword update", processed, total,
                $"updated +{updated}, failures +{failures}");
        }
    }


    private async Task RunAffiliationPromptAsync(SessionContext session, CancellationToken cancellationToken)
    {
        if (!_config.Affiliation.PromptForAffiliationCode)
        {
            return;
        }

        Console.WriteLine();
        Console.Write("Enter an affiliation/interest code to add to all accounts, created contacts, and duplicate contacts found in this run, or press Enter to skip: ");
        string affiliationCode = TextUtil.CleanKeyField(Console.ReadLine());
        if (string.IsNullOrWhiteSpace(affiliationCode))
        {
            Console.WriteLine("No affiliation/interest code entered. Skipping affiliation step.");
            return;
        }

        var accountCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_config.Affiliation.ApplyToAccountsTouchedThisRun)
        {
            foreach (string code in session.AccountCodesTouched) accountCodes.Add(code);
        }

        if (_config.Affiliation.ApplyToContactsCreatedThisRun)
        {
            foreach (string code in session.ContactCodesCreated) accountCodes.Add(code);
        }

        if (_config.Affiliation.ApplyToDuplicateContactsFound)
        {
            foreach (string code in session.DuplicateContactCodesFound) accountCodes.Add(code);
        }

        if (accountCodes.Count == 0)
        {
            Console.WriteLine("No account/contact codes were available for the affiliation step.");
            return;
        }

        Console.WriteLine($"Adding affiliation/interest code '{affiliationCode}' to {accountCodes.Count} account/contact record(s).");
        int totalAffiliations = accountCodes.Count;
        int processedAffiliations = 0;
        int affiliationStart = session.Summary.AffiliationsAdded;
        int affiliationFailureStart = session.Summary.AffiliationFailures;
        WriteProgress("Phase 4 affiliation update", 0, totalAffiliations, "starting");

        foreach (string accountCode in accountCodes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedAffiliations++;

            var pseudoRow = new ImportRow(
                WorksheetRowNumber: 0,
                SourceRowNumber: 0,
                SourceFileName: "Session",
                CompanyName: string.Empty,
                AccountCode: accountCode,
                MarketSegmentMajor: string.Empty,
                Country: string.Empty,
                WebsiteRootDomain: string.Empty,
                ContactEmail: string.Empty);

            if (_config.DryRun)
            {
                Log(pseudoRow, "Phase 4", "AddAffiliation", "Skipped", accountCode,
                    $"DRY_RUN: would add affiliation/interest code '{affiliationCode}'.", string.Empty);
                WriteProgress("Phase 4 affiliation update", processedAffiliations, totalAffiliations,
                    $"affiliations +{session.Summary.AffiliationsAdded - affiliationStart}, failures +{session.Summary.AffiliationFailures - affiliationFailureStart}");
                continue;
            }

            try
            {
                ApiWriteResult result = await _api.AddAccountAffiliationAsync(accountCode, affiliationCode, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Success)
                {
                    session.Summary.AffiliationsAdded++;
                    Log(pseudoRow, "Phase 4", "AddAffiliation", "Success", accountCode, result.Message, string.Empty);
                }
                else
                {
                    session.Summary.AffiliationFailures++;
                    session.Summary.Failures++;
                    Log(pseudoRow, "Phase 4", "AddAffiliation", "Failed", accountCode,
                        result.Message, result.ErrorMessage ?? string.Empty);
                }

                WriteProgress("Phase 4 affiliation update", processedAffiliations, totalAffiliations,
                    $"affiliations +{session.Summary.AffiliationsAdded - affiliationStart}, failures +{session.Summary.AffiliationFailures - affiliationFailureStart}");
            }
            catch (Exception ex)
            {
                session.Summary.AffiliationFailures++;
                session.Summary.Failures++;
                Log(pseudoRow, "Phase 4", "AddAffiliation", "Failed", accountCode, string.Empty, ex.Message);
                WriteProgress("Phase 4 affiliation update", processedAffiliations, totalAffiliations,
                    $"affiliations +{session.Summary.AffiliationsAdded - affiliationStart}, failures +{session.Summary.AffiliationFailures - affiliationFailureStart}");
            }
        }
    }

    private void RunPhase5(SessionContext session)
    {
        Console.WriteLine("Starting Phase 5: Complete and archive session.");

        string archiveFolder = Path.Combine(_config.Phase5Folder, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(archiveFolder);
        session.ArchiveFolder = archiveFolder;

        foreach (string file in session.FilesUsedOrCreated.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string destination = GetUniqueArchivePath(archiveFolder, Path.GetFileName(file));
            File.Copy(file, destination, overwrite: false);
        }

        string reportPath = Path.Combine(archiveFolder, $"team_update_summary_{session.Timestamp}.xlsx");
        _excel.CreateTeamUpdateWorkbook(reportPath, session, _audit.Records);
        session.TeamUpdateWorkbookFile = reportPath;
        Console.WriteLine($"Team update workbook: {reportPath}");

        if (_config.CleanupPhase0To4AfterArchive)
        {
            CleanupPhase0To4Files(session);
        }
    }


    private void CleanupPhase0To4Files(SessionContext session)
    {
        Console.WriteLine("Cleaning Phase 0 through Phase 4 files used or created by this run after successful archive copy.");
        string[] cleanupRoots =
        {
            _config.Phase0Folder,
            _config.Phase1Folder,
            _config.Phase2Folder,
            _config.Phase3Folder,
            _config.Phase4Folder
        };

        foreach (string file in session.FilesUsedOrCreated.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string fullPath = Path.GetFullPath(file);
            bool isInCleanupRoot = cleanupRoots.Any(root => IsUnderFolder(fullPath, root));
            if (!isInCleanupRoot) continue;

            try
            {
                File.Delete(fullPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not delete {fullPath}: {ex.Message}");
            }
        }
    }

    private static bool IsUnderFolder(string filePath, string folderPath)
    {
        string fullFolder = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullFile = Path.GetFullPath(filePath);
        return fullFile.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetUniqueArchivePath(string folder, string fileName)
    {
        string candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate)) return candidate;

        string name = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int i = 1; ; i++)
        {
            candidate = Path.Combine(folder, $"{name}_{i}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }


    private void PromptForImportId()
    {
        if (!_config.ImportId.PromptForImportId)
        {
            _config.ImportId.Value = CleanImportId(_config.ImportId.Value);
            return;
        }

        int maxLength = Math.Max(1, _config.ImportId.MaxLength);
        while (true)
        {
            Console.Write($"Enter an Import ID for this run, max {maxLength} letters/numbers, or press Enter to skip: ");
            string raw = Console.ReadLine() ?? string.Empty;
            string clean = CleanImportId(raw);

            if (string.IsNullOrWhiteSpace(raw))
            {
                _config.ImportId.Value = string.Empty;
                Console.WriteLine("No Import ID will be written to Keyword for this run.");
                return;
            }

            if (clean.Length <= maxLength && clean.Length == raw.Trim().Length)
            {
                _config.ImportId.Value = clean;
                Console.WriteLine($"Import ID '{clean}' will be written to {_config.ImportId.KeywordField} on every account/contact touched by this run. Existing values will be overwritten.");
                return;
            }

            Console.WriteLine($"Invalid Import ID. Use only letters and numbers, max {maxLength} characters. Example: SG27VIP");
        }
    }

    private static string CleanImportId(string? raw)
    {
        string value = TextUtil.Clean(raw).Trim();
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    private static bool PromptYes(string message)
    {
        Console.WriteLine(message);
        Console.Write("Continue? ");
        string? input = Console.ReadLine();
        return string.Equals(input?.Trim(), "Y", StringComparison.OrdinalIgnoreCase);
    }

    private RowAnnotation BaseAnnotation(ImportRow row)
    {
        return new RowAnnotation
        {
            SourceRowNumber = row.SourceRowNumber,
            SourceFileName = row.SourceFileName
        };
    }

    private void Log(
        ImportRow row,
        string phase,
        string action,
        string result,
        string accountCode,
        string momentusResponse,
        string errorMessage)
    {
        _audit.Log(new AuditRecord
        {
            Timestamp = DateTimeOffset.Now,
            Phase = phase,
            DryRun = _config.DryRun,
            SourceFileName = row.SourceFileName,
            RowNumber = row.SourceRowNumber,
            CompanyName = row.CompanyName,
            ContactEmail = row.ContactEmail,
            ActionAttempted = action,
            Result = result,
            AccountCode = accountCode,
            MomentusResponseMessage = momentusResponse,
            ErrorMessage = errorMessage
        });
    }

    private static void PrintFinalSummary(SessionContext session)
    {
        Console.WriteLine();
        Console.WriteLine("Final summary");
        Console.WriteLine("-------------");
        Console.WriteLine($"Number of duplicate contacts found: {session.Summary.DuplicateContactsFound}");
        Console.WriteLine($"Number of non-duplicates processed: {session.Summary.NonDuplicatesProcessed}");
        Console.WriteLine($"Number of existing accounts matched: {session.Summary.ExistingAccountsMatched}");
        Console.WriteLine($"Number of new accounts created: {session.Summary.NewAccountsCreated}");
        Console.WriteLine($"Number of existing account blank-field update actions: {session.Summary.ExistingAccountBlankFieldUpdates}");
        Console.WriteLine($"Number of existing account blank-field update failures: {session.Summary.ExistingAccountBlankFieldUpdateFailures}");
        Console.WriteLine($"Number of contacts created: {session.Summary.ContactsCreated}");
        Console.WriteLine($"Number of relationships created: {session.Summary.RelationshipsCreated}");
        Console.WriteLine($"Number of relationship failures: {session.Summary.RelationshipFailures}");
        Console.WriteLine($"Number of affiliations added: {session.Summary.AffiliationsAdded}");
        Console.WriteLine($"Number of affiliation failures: {session.Summary.AffiliationFailures}");
        Console.WriteLine($"Number of failures: {session.Summary.Failures}");
        Console.WriteLine($"Location of final archive folder: {session.ArchiveFolder}");
        Console.WriteLine($"Team update workbook: {session.TeamUpdateWorkbookFile}");

        if (session.Summary.DryRunAccountsPrepared > 0 || session.Summary.DryRunContactsPrepared > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Dry-run prepared counts");
            Console.WriteLine($"Organization accounts that would be created: {session.Summary.DryRunAccountsPrepared}");
            Console.WriteLine($"Contacts that would be created: {session.Summary.DryRunContactsPrepared}");
        }
    }
}