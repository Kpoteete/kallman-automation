using ClosedXML.Excel;
using DuplicateMerging.Models;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DuplicateMerging.Services;

public sealed class DuplicateMergeRunner
{
    private readonly AppConfig _config;
    private readonly IMomentusApiService _api;
    private readonly object _failedScanHistoryLock = new();

    public DuplicateMergeRunner(AppConfig config, IMomentusApiService api)
    {
        _config = config;
        _api = api;
    }

    public async Task RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (HasArg(args, "--apply-approved"))
        {
            await ApplyApprovedFolderAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        string? applyPlanPath = GetArgValue(args, "--apply-plan");
        if (!string.IsNullOrWhiteSpace(applyPlanPath))
        {
            await ApplyApprovedPlanAsync(applyPlanPath, moveAfterApply: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        string? resetFailedPlanPath = GetArgValue(args, "--reset-failed-plan");
        if (!string.IsNullOrWhiteSpace(resetFailedPlanPath))
        {
            ResetFailedApplyRows(resetFailedPlanPath);
            return;
        }

        if (HasArg(args, "--reset-failed-approved"))
        {
            ResetFailedApplyRowsInApprovedFolder();
            return;
        }

        if (HasArg(args, "--init-segments-template"))
        {
            CreateSegmentMappingTemplate();
            return;
        }

        if (HasArg(args, "--reset-scan"))
        {
            ResetScanCheckpoint();
            return;
        }

        if (HasArg(args, "--plan-all"))
        {
            await CreateAllCheckpointedPlansAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        string? planCountValue = GetArgValue(args, "--plan-count");
        if (!string.IsNullOrWhiteSpace(planCountValue))
        {
            if (!int.TryParse(planCountValue, out int accountCodeCount) || accountCodeCount <= 0)
                throw new InvalidOperationException("--plan-count must be followed by a positive number.");

            await CreateCheckpointedPlansForAccountCodeCountAsync(accountCodeCount, cancellationToken).ConfigureAwait(false);
            return;
        }

        string? widePlanCountValue = GetArgValue(args, "--plan-wide-count");
        if (!string.IsNullOrWhiteSpace(widePlanCountValue))
        {
            if (!int.TryParse(widePlanCountValue, out int accountCodeCount) || accountCodeCount <= 0)
                throw new InvalidOperationException("--plan-wide-count must be followed by a positive number.");

            await CreateWideCheckpointedPlanAsync(accountCodeCount, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (HasArg(args, "--plan-next"))
        {
            await CreateNextCheckpointedPlanAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        string? contactDedupeEmail = GetArgValue(args, "--dedupe-contact-email");
        if (!string.IsNullOrWhiteSpace(contactDedupeEmail))
        {
            await DedupeContactEmailPilotAsync(contactDedupeEmail, cancellationToken).ConfigureAwait(false);
            return;
        }

        string? contactDedupeAuditPath = GetArgValue(args, "--dedupe-contacts-from-audit");
        if (!string.IsNullOrWhiteSpace(contactDedupeAuditPath))
        {
            await DedupeContactsFromAuditAsync(contactDedupeAuditPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        string? auditDuplicateOrgPlanPath = GetArgValue(args, "--plan-from-audit-dupe-orgs");
        if (!string.IsNullOrWhiteSpace(auditDuplicateOrgPlanPath))
        {
            await CreatePlanFromAuditAsync(auditDuplicateOrgPlanPath, "Dup Org Names", "AUDIT_DUP_ORG_NAME", cancellationToken).ConfigureAwait(false);
            return;
        }

        string? auditDuplicateWebsitePlanPath = GetArgValue(args, "--plan-from-audit-dupe-websites");
        if (!string.IsNullOrWhiteSpace(auditDuplicateWebsitePlanPath))
        {
            await CreatePlanFromAuditAsync(auditDuplicateWebsitePlanPath, "Dup Websites", "AUDIT_DUP_WEBSITE_COUNTRY", cancellationToken).ConfigureAwait(false);
            return;
        }

        await CreatePlanAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DedupeContactEmailPilotAsync(string email, CancellationToken cancellationToken)
    {
        string normalizedEmail = TextUtil.NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
            throw new InvalidOperationException("--dedupe-contact-email requires an email address.");

        string outputPath = Path.Combine(
            _config.PendingReviewFolder,
            $"contact_dedupe_pilot_{SanitizeFileName(normalizedEmail)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.xlsx");
        Directory.CreateDirectory(_config.PendingReviewFolder);

        Console.WriteLine($"Contact dedupe pilot for email: {normalizedEmail}");
        Console.WriteLine(_config.DryRun
            ? "Running in DRY RUN mode. No Momentus updates will be made."
            : "Running LIVE. Momentus updates will be made for this one email.");

        ContactDedupeEmailResult result = await DedupeContactEmailAsync(normalizedEmail, cancellationToken)
            .ConfigureAwait(false);

        WriteContactDedupeWorkbook(outputPath, normalizedEmail, result.Contacts, result.LogRows, result.SurvivorContactAccountCode, result.FinalStatus);
        if (result.FinalStatus == "NO_DUPLICATES")
        {
            Console.WriteLine($"No duplicate active/prospective contacts found. Workbook created: {outputPath}");
            return;
        }

        Console.WriteLine($"Contact dedupe pilot complete. Status={result.FinalStatus}; survivor={result.SurvivorContactAccountCode}; workbook={outputPath}");
        if (result.FinalStatus != "APPLIED")
        {
            Console.WriteLine("Pilot failed after trying available survivor contacts. Review the workbook before continuing.");
        }
    }

    private async Task DedupeContactsFromAuditAsync(string auditWorkbookPath, CancellationToken cancellationToken)
    {
        if (!Path.IsPathRooted(auditWorkbookPath))
            auditWorkbookPath = Path.Combine(_config.RootPath, auditWorkbookPath);

        if (!File.Exists(auditWorkbookPath))
            throw new FileNotFoundException("Audit workbook not found.", auditWorkbookPath);

        Directory.CreateDirectory(_config.PendingReviewFolder);
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string outputPath = Path.Combine(_config.PendingReviewFolder, $"contact_dedupe_full_run_{timestamp}.xlsx");

        IReadOnlyList<ContactDedupeQueueItem> queue = LoadContactDedupeQueueFromAudit(auditWorkbookPath);
        Console.WriteLine($"Contact dedupe full run from audit: {auditWorkbookPath}");
        Console.WriteLine($"Loaded {queue.Count:N0} duplicate contact email group(s).");
        Console.WriteLine(_config.DryRun
            ? "Running in DRY RUN mode. No Momentus updates will be made."
            : "Running LIVE. Momentus updates will be made.");
        Console.WriteLine($"Progress workbook: {outputPath}");

        var summaries = new List<ContactDedupeRunSummaryRow>();
        var details = new List<ContactDedupeRunDetailRow>();
        WriteContactDedupeRunWorkbook(outputPath, auditWorkbookPath, summaries, details);

        for (int i = 0; i < queue.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ContactDedupeQueueItem item = queue[i];
            Console.WriteLine($"Contact dedupe {i + 1:N0}/{queue.Count:N0}: {item.Email} ({item.NameEmailKey}; audit count {item.AuditGroupCount:N0})");

            try
            {
                ContactDedupeEmailResult result = await DedupeContactEmailAsync(item.Email, cancellationToken)
                    .ConfigureAwait(false);

                summaries.Add(new ContactDedupeRunSummaryRow(
                    item.Email,
                    item.NameEmailKey,
                    item.AuditGroupCount,
                    result.FinalStatus,
                    result.SurvivorContactAccountCode,
                    result.Contacts.Count,
                    result.Contacts.SelectMany(c => c.CompanyCodes).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    result.Contacts.SelectMany(c => c.InterestCodes).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    string.Empty));

                details.AddRange(result.LogRows.Select(log => new ContactDedupeRunDetailRow(
                    item.Email,
                    item.NameEmailKey,
                    log.Timestamp,
                    log.Action,
                    log.Status,
                    log.SourceContactCode,
                    log.TargetSurvivorCode,
                    log.RelatedCode,
                    log.Message)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string message = $"{ex.GetType().Name}: {ex.Message}";
                summaries.Add(new ContactDedupeRunSummaryRow(
                    item.Email,
                    item.NameEmailKey,
                    item.AuditGroupCount,
                    "FAILED",
                    string.Empty,
                    0,
                    0,
                    0,
                    message));
                details.Add(new ContactDedupeRunDetailRow(
                    item.Email,
                    item.NameEmailKey,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    "UnhandledGroupError",
                    "FAILED",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    message));
                Console.WriteLine($"FAILED but continuing: {item.Email}; {message}");
            }

            WriteContactDedupeRunWorkbook(outputPath, auditWorkbookPath, summaries, details);
        }

        int applied = summaries.Count(row => row.FinalStatus == "APPLIED");
        int failed = summaries.Count(row => row.FinalStatus == "FAILED");
        int skipped = summaries.Count(row => row.FinalStatus == "NO_DUPLICATES");
        Console.WriteLine($"Contact dedupe full run complete. Applied={applied:N0}; NoDuplicates={skipped:N0}; Failed={failed:N0}; workbook={outputPath}");
    }

    private async Task<ContactDedupeEmailResult> DedupeContactEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        IReadOnlyList<MomentusContactSnapshot> contacts = await _api.SearchActiveContactsByEmailAsync(normalizedEmail, cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<ContactDedupeLogRow>();
        var contactInfos = new List<ContactDedupeContactInfo>();
        foreach (MomentusContactSnapshot contact in contacts)
        {
            IReadOnlyList<string> interests = await _api.GetAccountAffiliationCodesAsync(contact.AccountCode, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<string> relationships = await _api.GetCompanyRelationshipCodesForContactAsync(contact.AccountCode, cancellationToken)
                .ConfigureAwait(false);

            var companyCodes = relationships
                .Concat(new[] { TextUtil.CleanKeyField(contact.PrimaryAccount) })
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();

            contactInfos.Add(new ContactDedupeContactInfo(contact, interests, companyCodes));
        }

        if (contactInfos.Count < 2)
        {
            rows.Add(ContactDedupeLogRow.Info("No duplicate active/prospective contact records were found for this email."));
            return new ContactDedupeEmailResult(normalizedEmail, "NO_DUPLICATES", string.Empty, contactInfos, rows);
        }

        List<string> allCompanyCodes = contactInfos
            .SelectMany(info => info.CompanyCodes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> allInterestCodes = contactInfos
            .SelectMany(info => info.InterestCodes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<ContactDedupeContactInfo> survivorCandidates = contactInfos
            .OrderByDescending(info => info.CompanyCodes.Count)
            .ThenByDescending(info => info.InterestCodes.Count)
            .ThenByDescending(info => ContactFieldCompletenessScore(info.Contact))
            .ThenBy(info => info.Contact.AccountCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string finalStatus = "FAILED";
        string finalSurvivor = string.Empty;
        var unavailableSurvivors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (survivorCandidates.Any(candidate => !unavailableSurvivors.Contains(candidate.Contact.AccountCode)))
        {
            ContactDedupeContactInfo survivor = survivorCandidates.First(candidate => !unavailableSurvivors.Contains(candidate.Contact.AccountCode));
            finalSurvivor = survivor.Contact.AccountCode;
            rows.Add(ContactDedupeLogRow.Info($"Trying survivor contact {survivor.Contact.AccountCode} ({ContactName(survivor.Contact)})."));

            ContactDedupeAttemptResult result = await TryApplyContactDedupeSurvivorAsync(
                survivor,
                contactInfos,
                allCompanyCodes,
                allInterestCodes,
                rows,
                cancellationToken).ConfigureAwait(false);

            if (result.Success)
            {
                finalStatus = "APPLIED";
                break;
            }

            if (!string.IsNullOrWhiteSpace(result.BlockingContactAccountCode))
            {
                rows.Add(ContactDedupeLogRow.Info($"Pivoting survivor to blocking contact {result.BlockingContactAccountCode}."));
                unavailableSurvivors.Add(survivor.Contact.AccountCode);
                ContactDedupeContactInfo? blocking = survivorCandidates.FirstOrDefault(candidate =>
                    TextUtil.EqualsTrimmedIgnoreCase(candidate.Contact.AccountCode, result.BlockingContactAccountCode));
                if (blocking is not null)
                {
                    survivorCandidates.Remove(blocking);
                    survivorCandidates.Insert(0, blocking);
                }
            }
            else
            {
                unavailableSurvivors.Add(survivor.Contact.AccountCode);
            }
        }

        return new ContactDedupeEmailResult(normalizedEmail, finalStatus, finalSurvivor, contactInfos, rows);
    }

    private async Task<ContactDedupeAttemptResult> TryApplyContactDedupeSurvivorAsync(
        ContactDedupeContactInfo survivor,
        IReadOnlyList<ContactDedupeContactInfo> allContacts,
        IReadOnlyList<string> allCompanyCodes,
        IReadOnlyList<string> allInterestCodes,
        List<ContactDedupeLogRow> rows,
        CancellationToken cancellationToken)
    {
        string survivorCode = survivor.Contact.AccountCode;

        foreach (ContactDedupeContactInfo source in allContacts.Where(info => !TextUtil.EqualsTrimmedIgnoreCase(info.Contact.AccountCode, survivorCode)))
        {
            ApiWriteResult copy = await _api.CopyBlankAccountFieldsAsync(source.Contact.AccountCode, survivorCode, cancellationToken)
                .ConfigureAwait(false);
            rows.Add(ContactDedupeLogRow.FromResult("CopyBlankContactFields", source.Contact.AccountCode, survivorCode, string.Empty, copy));
            if (!copy.Success && LooksLikeHardContactDedupeFailure(copy))
                return new ContactDedupeAttemptResult(false, source.Contact.AccountCode);
        }

        foreach (string interestCode in allInterestCodes)
        {
            ApiWriteResult addInterest = await _api.AddAccountAffiliationAsync(survivorCode, interestCode, cancellationToken)
                .ConfigureAwait(false);
            rows.Add(ContactDedupeLogRow.FromResult("CopyInterestToSurvivor", survivorCode, survivorCode, interestCode, addInterest));
            if (!addInterest.Success)
                return new ContactDedupeAttemptResult(false, string.Empty);
        }

        var contactAccountCodes = allContacts
            .Select(info => info.Contact.AccountCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string companyCode in allCompanyCodes)
        {
            if (contactAccountCodes.Contains(companyCode))
            {
                rows.Add(ContactDedupeLogRow.Info($"Skipped relationship target {companyCode} because it is one of the duplicate contact account codes, not a company account."));
                continue;
            }

            bool exists = await _api.RelationshipExistsAsync(companyCode, survivorCode, _config.Relationship.RelationshipType, cancellationToken)
                .ConfigureAwait(false);
            if (exists)
            {
                rows.Add(ContactDedupeLogRow.Applied("EnsureSurvivorRelationship", survivorCode, survivorCode, companyCode, "Relationship already exists."));
                continue;
            }

            ApiWriteResult createRelationship = await _api.CreateRelationshipAsync(companyCode, survivorCode, cancellationToken)
                .ConfigureAwait(false);
            rows.Add(ContactDedupeLogRow.FromResult("EnsureSurvivorRelationship", survivorCode, survivorCode, companyCode, createRelationship));
            if (!createRelationship.Success)
                return new ContactDedupeAttemptResult(false, string.Empty);
        }

        foreach (ContactDedupeContactInfo duplicate in allContacts.Where(info => !TextUtil.EqualsTrimmedIgnoreCase(info.Contact.AccountCode, survivorCode)))
        {
            ApiWriteResult inactivate = await _api.UpdateAccountEventSalesStatusAsync(duplicate.Contact.AccountCode, "I", cancellationToken)
                .ConfigureAwait(false);
            rows.Add(ContactDedupeLogRow.FromResult("InactivateDuplicateContact", duplicate.Contact.AccountCode, survivorCode, string.Empty, inactivate));
            if (!inactivate.Success)
            {
                if (LooksLikeOrderHistoryBlock(inactivate))
                    return new ContactDedupeAttemptResult(false, duplicate.Contact.AccountCode);

                return new ContactDedupeAttemptResult(false, string.Empty);
            }
        }

        return new ContactDedupeAttemptResult(true, string.Empty);
    }

    private static void WriteContactDedupeWorkbook(
        string outputPath,
        string email,
        IReadOnlyList<ContactDedupeContactInfo> contacts,
        IReadOnlyList<ContactDedupeLogRow> logRows,
        string survivorAccountCode,
        string finalStatus)
    {
        using var workbook = new XLWorkbook();

        var summary = workbook.Worksheets.Add("Summary");
        WriteHeaders(summary, new[] { "Metric", "Value" });
        summary.Cell(2, 1).Value = "Email";
        summary.Cell(2, 2).Value = email;
        summary.Cell(3, 1).Value = "FinalStatus";
        summary.Cell(3, 2).Value = finalStatus;
        summary.Cell(4, 1).Value = "SurvivorContactAccountCode";
        summary.Cell(4, 2).Value = survivorAccountCode;
        summary.Cell(5, 1).Value = "DuplicateContactRecordsFound";
        summary.Cell(5, 2).Value = contacts.Count;
        summary.Cell(6, 1).Value = "DistinctCompanyRelationshipsFound";
        summary.Cell(6, 2).Value = contacts.SelectMany(c => c.CompanyCodes).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        summary.Cell(7, 1).Value = "DistinctInterestCodesFound";
        summary.Cell(7, 2).Value = contacts.SelectMany(c => c.InterestCodes).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        FormatSheet(summary, 2);

        var contactSheet = workbook.Worksheets.Add("Contacts");
        string[] contactHeaders =
        {
            "Role", "AccountCode", "FirstName", "LastName", "Email", "PrimaryAccount", "Company",
            "RelationshipCompanyCodes", "InterestCodes", "CompletenessScore"
        };
        WriteHeaders(contactSheet, contactHeaders);
        int row = 2;
        foreach (ContactDedupeContactInfo info in contacts.OrderBy(c => c.Contact.AccountCode, StringComparer.OrdinalIgnoreCase))
        {
            contactSheet.Cell(row, 1).Value = TextUtil.EqualsTrimmedIgnoreCase(info.Contact.AccountCode, survivorAccountCode) ? "SURVIVOR" : "DUPLICATE";
            contactSheet.Cell(row, 2).Value = info.Contact.AccountCode;
            contactSheet.Cell(row, 3).Value = info.Contact.FirstName;
            contactSheet.Cell(row, 4).Value = info.Contact.LastName;
            contactSheet.Cell(row, 5).Value = info.Contact.Email;
            contactSheet.Cell(row, 6).Value = info.Contact.PrimaryAccount;
            contactSheet.Cell(row, 7).Value = info.Contact.Company;
            contactSheet.Cell(row, 8).Value = string.Join(", ", info.CompanyCodes);
            contactSheet.Cell(row, 9).Value = string.Join(", ", info.InterestCodes);
            contactSheet.Cell(row, 10).Value = ContactFieldCompletenessScore(info.Contact);
            row++;
        }
        FormatSheet(contactSheet, contactHeaders.Length);

        var logSheet = workbook.Worksheets.Add("Action Log");
        string[] logHeaders = { "Timestamp", "Action", "Status", "SourceContactCode", "TargetSurvivorCode", "RelatedCode", "Message" };
        WriteHeaders(logSheet, logHeaders);
        row = 2;
        foreach (ContactDedupeLogRow log in logRows)
        {
            logSheet.Cell(row, 1).Value = log.Timestamp;
            logSheet.Cell(row, 2).Value = log.Action;
            logSheet.Cell(row, 3).Value = log.Status;
            logSheet.Cell(row, 4).Value = log.SourceContactCode;
            logSheet.Cell(row, 5).Value = log.TargetSurvivorCode;
            logSheet.Cell(row, 6).Value = log.RelatedCode;
            logSheet.Cell(row, 7).Value = log.Message;
            row++;
        }
        FormatSheet(logSheet, logHeaders.Length);

        workbook.SaveAs(outputPath);
    }

    private static IReadOnlyList<ContactDedupeQueueItem> LoadContactDedupeQueueFromAudit(string auditWorkbookPath)
    {
        using var workbook = new XLWorkbook(auditWorkbookPath);
        if (!workbook.TryGetWorksheet("Dup Individuals", out IXLWorksheet? worksheet))
            throw new InvalidOperationException("Audit workbook does not contain a 'Dup Individuals' worksheet.");

        var headerMap = worksheet.Row(1)
            .CellsUsed()
            .ToDictionary(
                cell => TextUtil.CleanKeyField(cell.GetString()),
                cell => cell.Address.ColumnNumber,
                StringComparer.OrdinalIgnoreCase);

        int keyCol = RequiredColumn(headerMap, "NameEmailKey");
        int countCol = RequiredColumn(headerMap, "GroupCount");
        int emailCol = RequiredColumn(headerMap, "Email");

        var queue = new List<ContactDedupeQueueItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;

        for (int row = 2; row <= lastRow; row++)
        {
            string email = TextUtil.NormalizeEmail(worksheet.Cell(row, emailCol).GetString());
            if (string.IsNullOrWhiteSpace(email) || !seen.Add(email))
                continue;

            string key = TextUtil.Clean(worksheet.Cell(row, keyCol).GetString());
            int count = 0;
            _ = int.TryParse(worksheet.Cell(row, countCol).GetString(), out count);
            queue.Add(new ContactDedupeQueueItem(email, key, count));
        }

        return queue
            .OrderByDescending(item => item.AuditGroupCount)
            .ThenBy(item => item.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int RequiredColumn(IReadOnlyDictionary<string, int> headerMap, string name)
    {
        if (!headerMap.TryGetValue(name, out int column))
            throw new InvalidOperationException($"Required worksheet column missing: {name}");

        return column;
    }

    private static void WriteContactDedupeRunWorkbook(
        string outputPath,
        string auditWorkbookPath,
        IReadOnlyList<ContactDedupeRunSummaryRow> summaries,
        IReadOnlyList<ContactDedupeRunDetailRow> details)
    {
        using var workbook = new XLWorkbook();

        var summary = workbook.Worksheets.Add("Summary");
        WriteHeaders(summary, new[] { "Metric", "Value" });
        summary.Cell(2, 1).Value = "AuditWorkbook";
        summary.Cell(2, 2).Value = auditWorkbookPath;
        summary.Cell(3, 1).Value = "GroupsProcessed";
        summary.Cell(3, 2).Value = summaries.Count;
        summary.Cell(4, 1).Value = "Applied";
        summary.Cell(4, 2).Value = summaries.Count(row => row.FinalStatus == "APPLIED");
        summary.Cell(5, 1).Value = "NoDuplicates";
        summary.Cell(5, 2).Value = summaries.Count(row => row.FinalStatus == "NO_DUPLICATES");
        summary.Cell(6, 1).Value = "Failed";
        summary.Cell(6, 2).Value = summaries.Count(row => row.FinalStatus == "FAILED");
        summary.Cell(7, 1).Value = "LastUpdated";
        summary.Cell(7, 2).Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        FormatSheet(summary, 2);

        var groups = workbook.Worksheets.Add("Groups");
        string[] groupHeaders =
        {
            "Email", "NameEmailKey", "AuditGroupCount", "FinalStatus", "SurvivorContactAccountCode",
            "LiveContactRecordsFound", "DistinctCompanyRelationshipsFound", "DistinctInterestCodesFound", "ErrorMessage"
        };
        WriteHeaders(groups, groupHeaders);
        int row = 2;
        foreach (ContactDedupeRunSummaryRow item in summaries)
        {
            groups.Cell(row, 1).Value = item.Email;
            groups.Cell(row, 2).Value = item.NameEmailKey;
            groups.Cell(row, 3).Value = item.AuditGroupCount;
            groups.Cell(row, 4).Value = item.FinalStatus;
            groups.Cell(row, 5).Value = item.SurvivorContactAccountCode;
            groups.Cell(row, 6).Value = item.LiveContactRecordsFound;
            groups.Cell(row, 7).Value = item.DistinctCompanyRelationshipsFound;
            groups.Cell(row, 8).Value = item.DistinctInterestCodesFound;
            groups.Cell(row, 9).Value = item.ErrorMessage;
            row++;
        }
        FormatSheet(groups, groupHeaders.Length);

        var log = workbook.Worksheets.Add("Action Log");
        string[] detailHeaders =
        {
            "Email", "NameEmailKey", "Timestamp", "Action", "Status", "SourceContactCode",
            "TargetSurvivorCode", "RelatedCode", "Message"
        };
        WriteHeaders(log, detailHeaders);
        row = 2;
        foreach (ContactDedupeRunDetailRow item in details)
        {
            log.Cell(row, 1).Value = item.Email;
            log.Cell(row, 2).Value = item.NameEmailKey;
            log.Cell(row, 3).Value = item.Timestamp;
            log.Cell(row, 4).Value = item.Action;
            log.Cell(row, 5).Value = item.Status;
            log.Cell(row, 6).Value = item.SourceContactCode;
            log.Cell(row, 7).Value = item.TargetSurvivorCode;
            log.Cell(row, 8).Value = item.RelatedCode;
            log.Cell(row, 9).Value = item.Message;
            row++;
        }
        FormatSheet(log, detailHeaders.Length);

        workbook.SaveAs(outputPath);
    }

    private static int ContactFieldCompletenessScore(MomentusContactSnapshot contact)
    {
        return new[]
        {
            contact.FirstName,
            contact.LastName,
            contact.Email,
            contact.PrimaryAccount,
            contact.Company
        }.Count(value => !string.IsNullOrWhiteSpace(TextUtil.Clean(value)));
    }

    private static bool LooksLikeHardContactDedupeFailure(ApiWriteResult result)
    {
        string message = $"{result.Message} {result.ErrorMessage}";
        return LooksLikeOrderHistoryBlock(result) ||
               message.Contains("record has changed since your last refresh", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeOrderHistoryBlock(ApiWriteResult result)
    {
        string message = $"{result.Message} {result.ErrorMessage}";
        return message.Contains("used on an Order", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Order or Bill To Contact", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string value)
    {
        string clean = TextUtil.CleanKeyField(value);
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            clean = clean.Replace(invalid, '_');
        }

        return clean.Replace('@', '_').Replace('.', '_');
    }

    private async Task CreatePlanFromAuditAsync(string auditWorkbookPath, string worksheetName, string matchRule, CancellationToken cancellationToken)
    {
        if (!Path.IsPathRooted(auditWorkbookPath))
            auditWorkbookPath = Path.Combine(_config.RootPath, auditWorkbookPath);

        if (!File.Exists(auditWorkbookPath))
            throw new FileNotFoundException("Audit workbook not found.", auditWorkbookPath);

        Console.WriteLine($"Creating organization-dedupe merge plan from audit workbook: {auditWorkbookPath}");
        Console.WriteLine($"Source worksheet: {worksheetName}; match rule: {matchRule}");

        IReadOnlyList<AuditDuplicateOrgRow> auditRows = LoadAuditDuplicateOrgRows(auditWorkbookPath, worksheetName, matchRule);
        Console.WriteLine($"Loaded {auditRows.Count:N0} duplicate organization row(s) from {worksheetName}.");

        var candidates = new ConcurrentBag<(string MatchKey, AuditDuplicateOrgRow Row, DuplicateAccountCandidate Candidate)>();
        int failed = 0;
        int processed = 0;
        int maxWorkers = Math.Max(1, _config.DuplicateMerge.MaxConcurrentScanAccounts);

        await Parallel.ForEachAsync(auditRows, new ParallelOptions
        {
            MaxDegreeOfParallelism = maxWorkers,
            CancellationToken = cancellationToken
        }, async (auditRow, ct) =>
        {
            int current = Interlocked.Increment(ref processed);
            if (current == 1 || current == auditRows.Count || current % 100 == 0)
                Console.WriteLine($"Inspecting audit duplicate org {current:N0}/{auditRows.Count:N0}: {auditRow.AccountCode} - {auditRow.Name}");

            try
            {
                IReadOnlyList<MomentusContactSnapshot> contacts = await _api.GetContactsForParentAccountAsync(auditRow.AccountCode, ct)
                    .ConfigureAwait(false);
                IReadOnlyList<string> interests;
                try
                {
                    interests = await _api.GetAccountAffiliationCodesAsync(auditRow.AccountCode, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    interests = Array.Empty<string>();
                }

                var account = new MomentusAccountSnapshot(
                    auditRow.AccountCode,
                    auditRow.Name,
                    auditRow.MarketSegmentMajor,
                    auditRow.MarketSegmentMinor,
                    auditRow.Country,
                    auditRow.Website,
                    auditRow.Email,
                    auditRow.Phone,
                    auditRow.EventSalesStatus);

                candidates.Add((auditRow.MatchKey, auditRow, new DuplicateAccountCandidate(account, contacts, interests)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
                Console.WriteLine($"Audit duplicate org failed after retries and will be skipped: {auditRow.AccountCode} - {auditRow.Name}. Error: {ex.Message}");
                AppendFailedScanHistory("AUDIT_ACCOUNT", string.Empty, string.Empty, auditRow.AccountCode, auditRow.Name, ex);
            }
        }).ConfigureAwait(false);

        var groups = candidates
            .GroupBy(item => item.MatchKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                AuditDuplicateOrgRow first = group.First().Row;
                return BuildDuplicateGroup(
                    group.Key,
                    new MatchKeyParts(group.Key, matchRule, first.NormalizedName, first.WebsiteRootDomain, first.Country),
                    group.Select(item => item.Candidate));
            })
            .OrderBy(group => group.NormalizedName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(group => group.Duplicates.Count)
            .ThenByDescending(group => group.Duplicates.Sum(d => d.ContactCount))
            .ToList();

        string outputPath = Path.Combine(
            _config.PendingReviewFolder,
            $"duplicate_merge_from_{worksheetName.Replace(" ", "_").ToLowerInvariant()}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.xlsx");

        if (groups.Count == 0)
        {
            Console.WriteLine("No duplicate organization groups found from the audit workbook. No review workbook was created.");
            return;
        }

        WritePlanWorkbook(outputPath, groups);
        Console.WriteLine($"Audit-based duplicate organization merge plan created: {outputPath}");
        Console.WriteLine($"Groups={groups.Count:N0}; duplicate accounts={groups.Sum(group => group.Duplicates.Count):N0}; contacts to move={groups.Sum(group => group.Duplicates.Sum(d => d.ContactCount)):N0}; failed account inspections={failed:N0}.");
        Console.WriteLine("Review the Actions sheet and mark rows APPROVED before running live apply.");
    }

    private async Task CreatePlanAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"Scanning up to {_config.DuplicateMerge.MaxAccountsToScanPerPlan:N0} active/prospective organization accounts...");
        IReadOnlyList<MomentusAccountSnapshot> accounts = await _api.SearchActiveParentAccountsAsync(
            _config.DuplicateMerge.MaxAccountsToScanPerPlan,
            cancellationToken).ConfigureAwait(false);

        string outputPath = TimestampedPath(
            _config.PendingReviewFolder,
            _config.DuplicateMerge.PlanWorkbookPrefix,
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"),
            ".xlsx");

        await CreatePlanFromAccountsAsync(accounts, outputPath, "ad hoc plan", cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateAllCheckpointedPlansAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            bool created = await CreateNextCheckpointedPlanAsync(cancellationToken).ConfigureAwait(false);
            if (!created) break;
        }
    }

    private async Task CreateCheckpointedPlansForAccountCodeCountAsync(int accountCodeCount, CancellationToken cancellationToken)
    {
        ScanCheckpoint checkpoint = LoadScanCheckpoint();
        if (checkpoint.Complete)
        {
            Console.WriteLine("Full database scan is already marked complete. Use --reset-scan to start over.");
            return;
        }

        string runEnd = AddToAccountCode(checkpoint.NextAccountCode, accountCodeCount - 1);
        Console.WriteLine($"Planning checkpointed batches from {NormalizeAccountCode(checkpoint.NextAccountCode)} through {runEnd}.");

        while (true)
        {
            bool created = await CreateNextCheckpointedPlanAsync(cancellationToken, runEnd).ConfigureAwait(false);
            if (!created) break;
        }
    }

    private async Task CreateWideCheckpointedPlanAsync(int accountCodeCount, CancellationToken cancellationToken)
    {
        ScanCheckpoint checkpoint = LoadScanCheckpoint();
        if (checkpoint.Complete)
        {
            Console.WriteLine("Full database scan is already marked complete. Use --reset-scan to start over.");
            return;
        }

        string start = NormalizeAccountCode(checkpoint.NextAccountCode);
        string end = AddToAccountCode(start, accountCodeCount - 1);
        string configuredEnd = NormalizeAccountCode(_config.DuplicateMerge.AccountCodeScanEnd);
        if (string.CompareOrdinal(end, configuredEnd) > 0) end = configuredEnd;

        Console.WriteLine($"Planning one wide duplicate-matching batch: AccountCode {start} through {end}");
        IReadOnlyList<MomentusAccountSnapshot> accounts = await _api.SearchActiveParentAccountsByAccountCodeRangeAsync(
            start,
            end,
            accountCodeCount,
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Pulled {accounts.Count:N0} active/prospective organization accounts for this AccountCode range.");

        string outputPath = Path.Combine(
            _config.PendingReviewFolder,
            $"{_config.DuplicateMerge.PlanWorkbookPrefix}_wide_{checkpoint.BatchNumber:0000}_{start}_to_{end}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.xlsx");

        await CreatePlanFromAccountsAsync(accounts, outputPath, $"{start} through {end}", cancellationToken).ConfigureAwait(false);
        AdvanceScanCheckpoint(checkpoint, end, configuredEnd);
    }

    private async Task<bool> CreateNextCheckpointedPlanAsync(CancellationToken cancellationToken, string? stopAtAccountCode = null)
    {
        ScanCheckpoint checkpoint = LoadScanCheckpoint();
        if (checkpoint.Complete)
        {
            Console.WriteLine("Full database scan is already marked complete. Use --reset-scan to start over.");
            return false;
        }

        string start = NormalizeAccountCode(checkpoint.NextAccountCode);
        string? runEnd = string.IsNullOrWhiteSpace(stopAtAccountCode) ? null : NormalizeAccountCode(stopAtAccountCode);
        if (runEnd is not null && string.CompareOrdinal(start, runEnd) > 0)
        {
            Console.WriteLine($"Requested planning range is complete through {runEnd}.");
            return false;
        }

        string end = AddToAccountCode(start, _config.DuplicateMerge.PlanBatchSize - 1);
        string configuredEnd = NormalizeAccountCode(_config.DuplicateMerge.AccountCodeScanEnd);
        if (string.CompareOrdinal(end, configuredEnd) > 0) end = configuredEnd;
        if (runEnd is not null && string.CompareOrdinal(end, runEnd) > 0) end = runEnd;

        Console.WriteLine($"Planning batch {checkpoint.BatchNumber:N0}: AccountCode {start} through {end}");
        IReadOnlyList<MomentusAccountSnapshot> accounts;
        try
        {
            accounts = await _api.SearchActiveParentAccountsByAccountCodeRangeAsync(
                start,
                end,
                _config.DuplicateMerge.PlanBatchSize,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"Range failed after retries and will be skipped: {start} through {end}. Error: {ex.Message}");
            AppendFailedScanHistory("RANGE", start, end, string.Empty, string.Empty, ex);
            AdvanceScanCheckpoint(checkpoint, end, configuredEnd);
            return !checkpoint.Complete && (runEnd is null || string.CompareOrdinal(checkpoint.NextAccountCode, runEnd) <= 0);
        }

        Console.WriteLine($"Pulled {accounts.Count:N0} active/prospective organization accounts for this AccountCode range.");

        string outputPath = Path.Combine(
            _config.PendingReviewFolder,
            $"{_config.DuplicateMerge.PlanWorkbookPrefix}_batch_{checkpoint.BatchNumber:0000}_{start}_to_{end}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.xlsx");

        await CreatePlanFromAccountsAsync(accounts, outputPath, $"{start} through {end}", cancellationToken).ConfigureAwait(false);

        AdvanceScanCheckpoint(checkpoint, end, configuredEnd);

        return !checkpoint.Complete && (runEnd is null || string.CompareOrdinal(checkpoint.NextAccountCode, runEnd) <= 0);
    }

    private async Task<PlanCreationResult> CreatePlanFromAccountsAsync(
        IReadOnlyList<MomentusAccountSnapshot> accounts,
        string outputPath,
        string batchLabel,
        CancellationToken cancellationToken)
    {
        var candidates = new ConcurrentBag<DuplicateAccountCandidate>();
        int failed = 0;
        int processed = 0;
        int maxWorkers = Math.Max(1, _config.DuplicateMerge.MaxConcurrentScanAccounts);
        Console.WriteLine($"Inspecting accounts with {maxWorkers:N0} worker(s).");

        await Parallel.ForEachAsync(accounts, new ParallelOptions
        {
            MaxDegreeOfParallelism = maxWorkers,
            CancellationToken = cancellationToken
        }, async (account, ct) =>
        {
            int current = Interlocked.Increment(ref processed);
            Console.WriteLine($"Inspecting {current}/{accounts.Count}: {account.AccountCode} - {account.Name}");

            IReadOnlyList<MomentusContactSnapshot> contacts;
            IReadOnlyList<string> interests;
            try
            {
                contacts = await _api.GetContactsForParentAccountAsync(account.AccountCode, ct)
                    .ConfigureAwait(false);
                interests = await _api.GetAccountAffiliationCodesAsync(account.AccountCode, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
                Console.WriteLine($"Account failed after retries and will be skipped: {account.AccountCode} - {account.Name}. Error: {ex.Message}");
                AppendFailedScanHistory("ACCOUNT", string.Empty, string.Empty, account.AccountCode, account.Name, ex);
                return;
            }

            candidates.Add(new DuplicateAccountCandidate(account, contacts, interests));
        }).ConfigureAwait(false);

        List<DuplicateAccountCandidate> candidateList = candidates.ToList();

        Console.WriteLine($"Finished inspecting {processed:N0} account(s) for {batchLabel}. Successful={candidateList.Count:N0}; Failed={failed:N0}.");

        IReadOnlyList<DuplicateAccountGroup> groups = BuildDuplicateGroups(candidateList);
        if (groups.Count == 0)
        {
            Console.WriteLine("No duplicate groups found in this batch. No review workbook was created.");
            return new PlanCreationResult(false, failed);
        }

        WritePlanWorkbook(outputPath, groups);
        AppendPlannedGroupHistory(outputPath, groups);
        Console.WriteLine($"Duplicate merge plan created: {outputPath}");
        Console.WriteLine($"Review the Actions sheet, mark rows with APPROVED, then move the workbook to: {_config.ApprovedFolder}");
        Console.WriteLine("When ready, run: dotnet run -- --live --apply-approved");
        return new PlanCreationResult(true, failed);
    }

    private IReadOnlyList<DuplicateAccountGroup> BuildDuplicateGroups(IReadOnlyList<DuplicateAccountCandidate> candidates)
    {
        var rawGroups = candidates
            .SelectMany(candidate => BuildMatchKeys(candidate.Account).Select(key => new { Candidate = candidate, Key = key }))
            .GroupBy(item => item.Key.MatchKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => BuildDuplicateGroup(group.Key, group.First().Key, group.Select(item => item.Candidate)))
            .OrderByDescending(group => group.MatchRule == "NAME_COUNTRY")
            .ThenByDescending(group => group.Duplicates.Count)
            .ThenByDescending(group => group.Duplicates.Sum(duplicate => duplicate.ContactCount))
            .ThenBy(group => group.MatchKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var plannedAccountCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<DuplicateAccountGroup>();

        foreach (DuplicateAccountGroup group in rawGroups)
        {
            List<DuplicateAccountCandidate> available = new[] { group.Survivor }
                .Concat(group.Duplicates)
                .Where(candidate => !plannedAccountCodes.Contains(candidate.Account.AccountCode))
                .ToList();

            if (available.Count < 2) continue;

            DuplicateAccountGroup availableGroup = BuildDuplicateGroup(group.MatchKey, new MatchKeyParts(
                group.MatchKey,
                group.MatchRule,
                group.NormalizedName,
                group.WebsiteRootDomain,
                group.Country), available);

            groups.Add(availableGroup);
            foreach (DuplicateAccountCandidate candidate in available)
            {
                plannedAccountCodes.Add(candidate.Account.AccountCode);
            }
        }

        return groups
            .OrderBy(group => group.NormalizedName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.WebsiteRootDomain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.MatchRule, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DuplicateAccountGroup BuildDuplicateGroup(
        string matchKey,
        MatchKeyParts parts,
        IEnumerable<DuplicateAccountCandidate> candidates)
    {
        List<DuplicateAccountCandidate> ordered = candidates
            .OrderByDescending(candidate => candidate.ActiveStatusScore)
            .ThenByDescending(candidate => candidate.ContactCount)
            .ThenByDescending(candidate => candidate.FieldCompletenessScore)
            .ThenBy(candidate => candidate.Account.AccountCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DuplicateAccountCandidate survivor = ordered[0];

        return new DuplicateAccountGroup(
            matchKey,
            parts.MatchRule,
            parts.NormalizedName,
            parts.WebsiteRootDomain,
            parts.Country,
            survivor,
            ordered.Skip(1).ToList());
    }

    private IReadOnlyList<MatchKeyParts> BuildMatchKeys(MomentusAccountSnapshot account)
    {
        string normalizedName = NormalizeCompanyName(account.Name);
        string websiteRoot = TextUtil.RootDomain(account.Website);
        string country = _config.CleanCountryForMomentus(account.Country);

        if (_config.DuplicateMerge.RequireCountryMatch && string.IsNullOrWhiteSpace(country))
            return Array.Empty<MatchKeyParts>();

        var keys = new List<MatchKeyParts>();
        if (!string.IsNullOrWhiteSpace(normalizedName))
        {
            keys.Add(new MatchKeyParts(
                $"NAME_COUNTRY|{normalizedName}|{country}",
                "NAME_COUNTRY",
                normalizedName,
                string.Empty,
                country));
        }

        if (!string.IsNullOrWhiteSpace(websiteRoot))
        {
            keys.Add(new MatchKeyParts(
                $"WEBSITE_COUNTRY|{websiteRoot}|{country}",
                "WEBSITE_COUNTRY",
                string.Empty,
                websiteRoot,
                country));
        }

        return keys;
    }

    private void WritePlanWorkbook(string outputPath, IReadOnlyList<DuplicateAccountGroup> groups)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var workbook = new XLWorkbook();
        WriteSummarySheet(workbook, groups);
        WriteActionsSheet(workbook, groups);
        WriteAccountsSheet(workbook, groups);
        workbook.SaveAs(outputPath);
    }

    private static void WriteSummarySheet(XLWorkbook workbook, IReadOnlyList<DuplicateAccountGroup> groups)
    {
        var ws = workbook.Worksheets.Add("Summary");
        string[] headers =
        {
            "MatchKey", "MatchRule", "NormalizedName", "WebsiteRootDomain", "Country", "SurvivorAccountCode",
            "SurvivorName", "DuplicateAccountCount", "ContactsToMove"
        };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (DuplicateAccountGroup group in groups)
        {
            ws.Cell(row, 1).Value = group.MatchKey;
            ws.Cell(row, 2).Value = group.MatchRule;
            ws.Cell(row, 3).Value = group.NormalizedName;
            ws.Cell(row, 4).Value = group.WebsiteRootDomain;
            ws.Cell(row, 5).Value = group.Country;
            ws.Cell(row, 6).Value = group.Survivor.Account.AccountCode;
            ws.Cell(row, 7).Value = group.Survivor.Account.Name;
            ws.Cell(row, 8).Value = group.Duplicates.Count;
            ws.Cell(row, 9).Value = group.Duplicates.Sum(duplicate => duplicate.ContactCount);
            row++;
        }

        FormatSheet(ws, headers.Length);
    }

    private static void WriteActionsSheet(XLWorkbook workbook, IReadOnlyList<DuplicateAccountGroup> groups)
    {
        var ws = workbook.Worksheets.Add("Actions");
        string[] headers =
        {
            "ReviewDecision", "Action", "Status", "MatchKey", "SurvivorAccountCode", "SurvivorName",
            "DuplicateAccountCode", "DuplicateName", "ContactAccountCode", "ContactName", "ContactEmail", "Message"
        };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (DuplicateMergeActionRow action in BuildActionRows(groups))
        {
            ws.Cell(row, 1).Value = string.Empty;
            ws.Cell(row, 2).Value = action.Action;
            ws.Cell(row, 3).Value = action.Status;
            ws.Cell(row, 4).Value = action.MatchKey;
            ws.Cell(row, 5).Value = action.SurvivorAccountCode;
            ws.Cell(row, 6).Value = action.SurvivorName;
            ws.Cell(row, 7).Value = action.DuplicateAccountCode;
            ws.Cell(row, 8).Value = action.DuplicateName;
            ws.Cell(row, 9).Value = action.ContactAccountCode;
            ws.Cell(row, 10).Value = action.ContactName;
            ws.Cell(row, 11).Value = action.ContactEmail;
            ws.Cell(row, 12).Value = action.Message;
            row++;
        }

        FormatSheet(ws, headers.Length);
        ws.Column(1).Style.Fill.BackgroundColor = XLColor.LightYellow;
    }

    private static IReadOnlyList<DuplicateMergeActionRow> BuildActionRows(IReadOnlyList<DuplicateAccountGroup> groups)
    {
        var rows = new List<DuplicateMergeActionRow>();

        foreach (DuplicateAccountGroup group in groups)
        {
            foreach (DuplicateAccountCandidate duplicate in group.Duplicates)
            {
                rows.Add(new DuplicateMergeActionRow(
                    "CopyBlankAccountFields", "PROPOSED", group.MatchKey,
                    group.Survivor.Account.AccountCode, group.Survivor.Account.Name,
                    duplicate.Account.AccountCode, duplicate.Account.Name,
                    string.Empty, string.Empty, string.Empty,
                    "Copy values from duplicate account into blank fields on survivor account. Existing survivor values are preserved."));

                if (duplicate.Contacts.Count == 0)
                {
                    rows.Add(new DuplicateMergeActionRow(
                        "NoContactsToMove", "INFO", group.MatchKey,
                        group.Survivor.Account.AccountCode, group.Survivor.Account.Name,
                        duplicate.Account.AccountCode, duplicate.Account.Name,
                        string.Empty, string.Empty, string.Empty,
                        "Duplicate account has no contacts under PrimaryAccount."));
                }
                else
                {
                    foreach (MomentusContactSnapshot contact in duplicate.Contacts)
                    {
                        rows.Add(new DuplicateMergeActionRow(
                            "MoveContactPrimaryAccount", "PROPOSED", group.MatchKey,
                            group.Survivor.Account.AccountCode, group.Survivor.Account.Name,
                            duplicate.Account.AccountCode, duplicate.Account.Name,
                            contact.AccountCode, ContactName(contact), contact.Email,
                            "Set contact PrimaryAccount to survivor account. Duplicate account is not deleted or inactivated."));
                    }
                }

                rows.Add(new DuplicateMergeActionRow(
                    "InactivateDuplicateAccountWhenEmpty", "PROPOSED", group.MatchKey,
                    group.Survivor.Account.AccountCode, group.Survivor.Account.Name,
                    duplicate.Account.AccountCode, duplicate.Account.Name,
                    string.Empty, string.Empty, string.Empty,
                    "After approved contact moves, set duplicate account EventSalesStatus to I only if no contacts remain under it."));
            }
        }

        return rows;
    }

    private static IReadOnlyList<AuditDuplicateOrgRow> LoadAuditDuplicateOrgRows(string auditWorkbookPath, string worksheetName, string matchRule)
    {
        using var workbook = new XLWorkbook(auditWorkbookPath);
        if (!workbook.TryGetWorksheet(worksheetName, out IXLWorksheet? ws))
            throw new InvalidOperationException($"Audit workbook does not contain a '{worksheetName}' worksheet.");

        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        var rows = new List<AuditDuplicateOrgRow>();
        var seenAccountCodesByMatchKey = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int row = 2; row <= lastRow; row++)
        {
            string sourceKey = TextUtil.CleanKeyField(ws.Cell(row, 1).GetString());
            string accountCode = TextUtil.CleanKeyField(ws.Cell(row, 3).GetString());
            string name = TextUtil.Clean(ws.Cell(row, 4).GetString());
            string country = TextUtil.CleanKeyField(ws.Cell(row, 5).GetString());
            string major = TextUtil.CleanKeyField(ws.Cell(row, 6).GetString());
            string minor = TextUtil.CleanKeyField(ws.Cell(row, 7).GetString());
            string eventSalesStatus = TextUtil.CleanKeyField(ws.Cell(row, 8).GetString());
            string website = TextUtil.Clean(ws.Cell(row, 11).GetString());
            string email = TextUtil.Clean(ws.Cell(row, 12).GetString());
            string phone = TextUtil.Clean(ws.Cell(row, 13).GetString());
            string websiteRoot = TextUtil.RootDomain(website);
            string normalizedName = sourceKey.Split('|', 2)[0];
            string matchKey = matchRule == "AUDIT_DUP_WEBSITE_COUNTRY"
                ? $"WEBSITE_COUNTRY|{websiteRoot}|{country}"
                : sourceKey;

            if (string.IsNullOrWhiteSpace(matchKey) ||
                string.IsNullOrWhiteSpace(accountCode) ||
                string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (matchRule == "AUDIT_DUP_WEBSITE_COUNTRY" && string.IsNullOrWhiteSpace(websiteRoot)) continue;

            if (!seenAccountCodesByMatchKey.Add($"{matchKey}|{accountCode}")) continue;

            rows.Add(new AuditDuplicateOrgRow(
                matchKey,
                normalizedName,
                websiteRoot,
                accountCode,
                name,
                country,
                major,
                minor,
                eventSalesStatus,
                website,
                email,
                phone));
        }

        return rows;
    }

    private static void WriteAccountsSheet(XLWorkbook workbook, IReadOnlyList<DuplicateAccountGroup> groups)
    {
        var ws = workbook.Worksheets.Add("Accounts");
        string[] headers =
        {
            "MatchKey", "MatchRule", "Role", "AccountCode", "Name", "Website", "Country", "MarketSegmentMajor",
            "MarketSegmentMinor", "EventSalesStatus", "ContactCount", "AccountInterestCodes", "FieldCompletenessScore"
        };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (DuplicateAccountGroup group in groups)
        {
            foreach ((string role, DuplicateAccountCandidate candidate) in new[] { ("SURVIVOR", group.Survivor) }.Concat(group.Duplicates.Select(d => ("DUPLICATE", d))))
            {
                ws.Cell(row, 1).Value = group.MatchKey;
                ws.Cell(row, 2).Value = group.MatchRule;
                ws.Cell(row, 3).Value = role;
                ws.Cell(row, 4).Value = candidate.Account.AccountCode;
                ws.Cell(row, 5).Value = candidate.Account.Name;
                ws.Cell(row, 6).Value = candidate.Account.Website;
                ws.Cell(row, 7).Value = candidate.Account.Country;
                ws.Cell(row, 8).Value = candidate.Account.MarketSegmentMajor;
                ws.Cell(row, 9).Value = candidate.Account.MarketSegmentMinor;
                ws.Cell(row, 10).Value = candidate.Account.EventSalesStatus;
                ws.Cell(row, 11).Value = candidate.ContactCount;
                ws.Cell(row, 12).Value = string.Join(", ", candidate.InterestCodes);
                ws.Cell(row, 13).Value = candidate.FieldCompletenessScore;
                row++;
            }
        }

        FormatSheet(ws, headers.Length);
    }

    private async Task ApplyApprovedFolderAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> workbooks = Directory.Exists(_config.ApprovedFolder)
            ? Directory.GetFiles(_config.ApprovedFolder, "*.xlsx", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : Array.Empty<string>();

        if (workbooks.Count == 0)
        {
            Console.WriteLine($"No approved workbooks found in: {_config.ApprovedFolder}");
            Console.WriteLine("Create a plan, mark APPROVED rows, then move the workbook into the Approved folder.");
            return;
        }

        Console.WriteLine($"Found {workbooks.Count:N0} approved workbook(s).");
        foreach (string workbookPath in workbooks)
        {
            try
            {
                await ApplyApprovedPlanAsync(workbookPath, moveAfterApply: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Workbook failed and was moved to Failed. Continuing with next approved workbook. Error: {ex.Message}");
            }
        }
    }

    private async Task ApplyApprovedPlanAsync(string planPath, bool moveAfterApply, CancellationToken cancellationToken)
    {
        if (_config.DryRun)
            throw new InvalidOperationException("Refusing to apply plan while DryRun is true. Re-run with --live after reviewing the workbook.");

        if (!File.Exists(planPath))
            throw new FileNotFoundException("Plan workbook not found.", planPath);

        Console.WriteLine($"Applying approved duplicate merge rows from: {planPath}");

        bool appliedSuccessfully = false;
        try
        {
            using var workbook = new XLWorkbook(planPath);
            var ws = workbook.Worksheet("Actions");
            int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
            int moved = 0;
            int fieldCopies = 0;
            int inactivated = 0;
            int failedRows = 0;
            var approvedRows = new List<ApprovedActionRow>();
            int alreadyFinishedRows = 0;

            for (int row = 2; row <= lastRow; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string reviewDecision = ws.Cell(row, 1).GetString().Trim();
                if (!TextUtil.EqualsTrimmedIgnoreCase(reviewDecision, "APPROVED")) continue;

                string currentStatus = ws.Cell(row, 3).GetString().Trim();
                if (currentStatus is "APPLIED" or "FAILED" or "SKIPPED")
                {
                    alreadyFinishedRows++;
                    continue;
                }

                string action = ws.Cell(row, 2).GetString().Trim();
                string survivorCode = ws.Cell(row, 5).GetString().Trim();
                string survivorName = ws.Cell(row, 6).GetString().Trim();
                string duplicateCode = ws.Cell(row, 7).GetString().Trim();
                string contactCode = ws.Cell(row, 9).GetString().Trim();

                approvedRows.Add(new ApprovedActionRow(row, action, survivorCode, survivorName, duplicateCode, contactCode));
            }

            object workbookLock = new();
            int maxWorkers = Math.Max(1, _config.BatchLimits.MaxConcurrentApplyRows);
            int saveEveryRows = Math.Max(1, _config.BatchLimits.ApplyWorkbookSaveEveryRows);
            if (alreadyFinishedRows > 0)
                Console.WriteLine($"Skipping {alreadyFinishedRows:N0} already-finished approved row(s).");
            Console.WriteLine($"Applying approved rows with {maxWorkers:N0} worker(s). Saving workbook every {saveEveryRows:N0} completed row(s). Inactivation rows run after moves/copies finish.");

            IReadOnlyList<ApprovedActionRow> firstWaveRows = approvedRows
                .Where(row => row.Action is "MoveContactPrimaryAccount" or "CopyBlankAccountFields")
                .ToList();
            IReadOnlyList<ApprovedActionRow> inactivateRows = approvedRows
                .Where(row => row.Action == "InactivateDuplicateAccountWhenEmpty")
                .ToList();
            IReadOnlyList<ApprovedActionRow> unsupportedRows = approvedRows
                .Where(row => row.Action is not ("MoveContactPrimaryAccount" or "CopyBlankAccountFields" or "InactivateDuplicateAccountWhenEmpty"))
                .ToList();

            foreach (ApprovedActionRow row in unsupportedRows)
            {
                ws.Cell(row.RowNumber, 3).Value = "SKIPPED";
                ws.Cell(row.RowNumber, 12).Value = "Only CopyBlankAccountFields, MoveContactPrimaryAccount, and InactivateDuplicateAccountWhenEmpty rows are applied.";
            }
            if (unsupportedRows.Count > 0) workbook.Save();

            ApplyRowsResult firstWave = await ApplyApprovedRowsAsync(firstWaveRows, ws, workbook, workbookLock, maxWorkers, saveEveryRows, cancellationToken)
                .ConfigureAwait(false);
            ApplyRowsResult inactivateWave = await ApplyApprovedRowsAsync(inactivateRows, ws, workbook, workbookLock, maxWorkers, saveEveryRows, cancellationToken)
                .ConfigureAwait(false);

            moved = firstWave.Moved + inactivateWave.Moved;
            fieldCopies = firstWave.FieldCopies + inactivateWave.FieldCopies;
            inactivated = firstWave.Inactivated + inactivateWave.Inactivated;
            failedRows = firstWave.FailedRows + inactivateWave.FailedRows;

            workbook.Save();
            Console.WriteLine($"Apply complete. Field-copy rows applied: {fieldCopies:N0}; contacts moved: {moved:N0}; duplicate accounts inactivated: {inactivated:N0}");
            string status = failedRows == 0 ? "Completed" : "Failed";
            AppendProcessedPlanHistory(planPath, status, $"FieldCopies={fieldCopies}; ContactsMoved={moved}; Inactivated={inactivated}; FailedRows={failedRows}");
            appliedSuccessfully = failedRows == 0;
        }
        finally
        {
            if (moveAfterApply)
            {
                MoveAppliedWorkbook(planPath, appliedSuccessfully ? _config.CompletedFolder : _config.FailedFolder);
            }
        }
    }

    private void ResetFailedApplyRowsInApprovedFolder()
    {
        IReadOnlyList<string> workbooks = Directory.Exists(_config.ApprovedFolder)
            ? Directory.GetFiles(_config.ApprovedFolder, "*.xlsx", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : Array.Empty<string>();

        if (workbooks.Count == 0)
        {
            Console.WriteLine($"No approved workbooks found in: {_config.ApprovedFolder}");
            return;
        }

        int totalReset = 0;
        foreach (string workbookPath in workbooks)
        {
            totalReset += ResetFailedApplyRows(workbookPath);
        }

        Console.WriteLine($"Reset {totalReset:N0} failed row(s) across {workbooks.Count:N0} approved workbook(s).");
    }

    private int ResetFailedApplyRows(string planPath)
    {
        if (!File.Exists(planPath))
            throw new FileNotFoundException("Plan workbook not found.", planPath);

        using var workbook = new XLWorkbook(planPath);
        var ws = workbook.Worksheet("Actions");
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        int reset = 0;

        for (int row = 2; row <= lastRow; row++)
        {
            string reviewDecision = ws.Cell(row, 1).GetString().Trim();
            if (!TextUtil.EqualsTrimmedIgnoreCase(reviewDecision, "APPROVED")) continue;

            string status = ws.Cell(row, 3).GetString().Trim();
            if (!TextUtil.EqualsTrimmedIgnoreCase(status, "FAILED")) continue;

            string previousMessage = ws.Cell(row, 12).GetString().Trim();
            ws.Cell(row, 3).Value = "PROPOSED";
            ws.Cell(row, 12).Value = string.IsNullOrWhiteSpace(previousMessage)
                ? "Retry requested after previous failure."
                : $"Retry requested after previous failure. Previous error: {previousMessage}";
            reset++;
        }

        if (reset > 0) workbook.Save();
        Console.WriteLine($"Reset {reset:N0} failed row(s) in: {planPath}");
        return reset;
    }

    private void CreateSegmentMappingTemplate()
    {
        string path = Path.Combine(_config.RootPath, _config.Segmentation.SegmentMappingFile);
        if (File.Exists(path))
        {
            Console.WriteLine($"Segment mapping template already exists: {path}");
            return;
        }

        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Segments");
        string[] headers =
        {
            "InterestCode",
            "MarketSegmentMajor",
            "MarketSegmentMinor",
            "MarketSegmentCombined",
            "Skip",
            "Notes"
        };

        WriteHeaders(ws, headers);

        string[,] examples =
        {
            { "EXAMPLE", "P", "A", "PA", "TRUE", "Replace this row. TRUE means ignore this interest code." },
            { "ABC", "A", "B", "AB", "FALSE", "Example mapping: interest ABC maps to market segment Major A / Minor B." }
        };

        for (int r = 0; r < examples.GetLength(0); r++)
        {
            for (int c = 0; c < examples.GetLength(1); c++)
            {
                ws.Cell(r + 2, c + 1).Value = examples[r, c];
            }
        }

        ws.Column(1).Style.NumberFormat.Format = "@";
        ws.Column(2).Style.NumberFormat.Format = "@";
        ws.Column(3).Style.NumberFormat.Format = "@";
        ws.Column(4).Style.NumberFormat.Format = "@";
        ws.Column(5).Style.NumberFormat.Format = "@";
        FormatSheet(ws, headers.Length);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        workbook.SaveAs(path);
        Console.WriteLine($"Created segment mapping template: {path}");
        Console.WriteLine("Fill this file with one row per interest code, then we can create the segmentation planning command.");
    }

    private async Task<ApplyRowsResult> ApplyApprovedRowsAsync(
        IReadOnlyList<ApprovedActionRow> rows,
        IXLWorksheet ws,
        XLWorkbook workbook,
        object workbookLock,
        int maxWorkers,
        int saveEveryRows,
        CancellationToken cancellationToken)
    {
        int moved = 0;
        int fieldCopies = 0;
        int inactivated = 0;
        int failedRows = 0;
        int completedRows = 0;
        int rowsSinceSave = 0;

        await Parallel.ForEachAsync(rows, new ParallelOptions
        {
            MaxDegreeOfParallelism = maxWorkers,
            CancellationToken = cancellationToken
        }, async (row, ct) =>
        {
            try
            {
                AppliedActionResult applied = await ApplyApprovedActionRowAsync(row, ct).ConfigureAwait(false);
                if (applied.Result.Success)
                {
                    if (applied.Action == "MoveContactPrimaryAccount") Interlocked.Increment(ref moved);
                    if (applied.Action == "CopyBlankAccountFields") Interlocked.Increment(ref fieldCopies);
                    if (applied.Action == "InactivateDuplicateAccountWhenEmpty" && applied.ChangedAccountStatus)
                        Interlocked.Increment(ref inactivated);
                }
                else
                {
                    Interlocked.Increment(ref failedRows);
                }

                lock (workbookLock)
                {
                    ws.Cell(row.RowNumber, 3).Value = applied.Result.Success ? "APPLIED" : "FAILED";
                    ws.Cell(row.RowNumber, 12).Value = applied.Result.Success
                        ? applied.Result.Message
                        : $"{applied.Result.Message} {applied.Result.ErrorMessage}".Trim();
                    int completed = Interlocked.Increment(ref completedRows);
                    rowsSinceSave++;
                    if (rowsSinceSave >= saveEveryRows)
                    {
                        workbook.Save();
                        rowsSinceSave = 0;
                        Console.WriteLine($"Saved apply progress after {completed:N0}/{rows.Count:N0} row(s) in this wave.");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failedRows);
                lock (workbookLock)
                {
                    ws.Cell(row.RowNumber, 3).Value = "FAILED";
                    ws.Cell(row.RowNumber, 12).Value = ex.Message;
                    int completed = Interlocked.Increment(ref completedRows);
                    rowsSinceSave++;
                    if (rowsSinceSave >= saveEveryRows)
                    {
                        workbook.Save();
                        rowsSinceSave = 0;
                        Console.WriteLine($"Saved apply progress after {completed:N0}/{rows.Count:N0} row(s) in this wave.");
                    }
                }
            }
        }).ConfigureAwait(false);

        lock (workbookLock)
        {
            workbook.Save();
        }

        return new ApplyRowsResult(moved, fieldCopies, inactivated, failedRows);
    }

    private async Task<AppliedActionResult> ApplyApprovedActionRowAsync(ApprovedActionRow row, CancellationToken cancellationToken)
    {
        if (row.Action == "MoveContactPrimaryAccount")
        {
            ApiWriteResult result = await _api.UpdateContactPrimaryAccountAsync(row.ContactAccountCode, row.SurvivorAccountCode, row.SurvivorName, cancellationToken)
                .ConfigureAwait(false);
            if (result.Success)
            {
                var messages = new List<string> { result.Message };

                if (!await _api.RelationshipExistsAsync(row.SurvivorAccountCode, row.ContactAccountCode, _config.Relationship.RelationshipType, cancellationToken)
                        .ConfigureAwait(false))
                {
                    ApiWriteResult createRelationship = await _api.CreateRelationshipAsync(row.SurvivorAccountCode, row.ContactAccountCode, cancellationToken)
                        .ConfigureAwait(false);
                    messages.Add(createRelationship.Message);
                    if (!createRelationship.Success)
                    {
                        result = createRelationship;
                    }
                }

                if (result.Success)
                {
                    ApiWriteResult deleteOldRelationship = await _api.DeleteRelationshipAsync(row.DuplicateAccountCode, row.ContactAccountCode, _config.Relationship.RelationshipType, cancellationToken)
                        .ConfigureAwait(false);
                    messages.Add(deleteOldRelationship.Message);
                    if (!deleteOldRelationship.Success)
                    {
                        string deleteMessage = $"{deleteOldRelationship.Message} {deleteOldRelationship.ErrorMessage}".Trim();
                        if (deleteMessage.Contains("used on an Order", StringComparison.OrdinalIgnoreCase) ||
                            deleteMessage.Contains("Could not delete relationship", StringComparison.OrdinalIgnoreCase))
                        {
                            ApiWriteResult inactivateRelationship = await _api.InactivateRelationshipAsync(row.DuplicateAccountCode, row.ContactAccountCode, _config.Relationship.RelationshipType, cancellationToken)
                                .ConfigureAwait(false);
                            messages.Add(inactivateRelationship.Message);
                            if (!inactivateRelationship.Success)
                            {
                                result = inactivateRelationship;
                            }
                            else
                            {
                                messages.Add($"Old relationship could not be deleted because of order history, so it was marked inactive instead: {deleteOldRelationship.ErrorMessage}");
                            }
                        }
                        else
                        {
                            result = deleteOldRelationship;
                        }
                    }
                }

                result = result with { Message = string.Join(" ", messages.Where(message => !string.IsNullOrWhiteSpace(message))) };
            }

            return new AppliedActionResult(row.Action, result, false);
        }

        if (row.Action == "CopyBlankAccountFields")
        {
            ApiWriteResult result = await _api.CopyBlankAccountFieldsAsync(row.DuplicateAccountCode, row.SurvivorAccountCode, cancellationToken)
                .ConfigureAwait(false);
            return new AppliedActionResult(row.Action, result, false);
        }

        if (row.Action == "InactivateDuplicateAccountWhenEmpty")
        {
            IReadOnlyList<MomentusContactSnapshot> remainingContacts = await _api.GetContactsForParentAccountAsync(row.DuplicateAccountCode, cancellationToken)
                .ConfigureAwait(false);

            if (remainingContacts.Count > 0)
            {
                return new AppliedActionResult(
                    row.Action,
                    ApiWriteResult.Succeeded(
                        row.DuplicateAccountCode,
                        $"Duplicate account still has {remainingContacts.Count:N0} contact(s); EventSalesStatus not changed."),
                    false);
            }

            ApiWriteResult result = await _api.UpdateAccountEventSalesStatusAsync(row.DuplicateAccountCode, "I", cancellationToken)
                .ConfigureAwait(false);
            return new AppliedActionResult(row.Action, result, result.Success);
        }

        return new AppliedActionResult(
            row.Action,
            ApiWriteResult.Failed("Only CopyBlankAccountFields, MoveContactPrimaryAccount, and InactivateDuplicateAccountWhenEmpty rows are applied."),
            false);
    }

    private static void WriteHeaders(IXLWorksheet ws, IReadOnlyList<string> headers)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
        }
    }

    private static void FormatSheet(IXLWorksheet ws, int headerCount)
    {
        ws.SheetView.FreezeRows(1);
        ws.Range(1, 1, Math.Max(ws.LastRowUsed()?.RowNumber() ?? 1, 1), headerCount).CreateTable();
        ws.Columns(1, headerCount).AdjustToContents(1, Math.Min(ws.LastRowUsed()?.RowNumber() ?? 1, 1000));
    }

    private static string NormalizeCompanyName(string value)
    {
        string normalized = TextUtil.CanonicalCompanyName(value).ToUpperInvariant();
        return normalized;
    }

    private static string ContactName(MomentusContactSnapshot contact)
    {
        return TextUtil.Clean($"{contact.FirstName} {contact.LastName}");
    }

    private static string? GetArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static bool HasArg(string[] args, string name)
    {
        return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string TimestampedPath(string folder, string prefix, string timestamp, string extension)
    {
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{prefix}_{timestamp}{extension}");
    }

    private void MoveAppliedWorkbook(string planPath, string destinationFolder)
    {
        Directory.CreateDirectory(destinationFolder);
        string destinationPath = UniquePath(Path.Combine(destinationFolder, Path.GetFileName(planPath)));
        File.Move(planPath, destinationPath);
        Console.WriteLine($"Moved workbook to: {destinationPath}");
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        string folder = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        int counter = 1;

        while (true)
        {
            string candidate = Path.Combine(folder, $"{name}_{counter}{extension}");
            if (!File.Exists(candidate)) return candidate;
            counter++;
        }
    }

    private void AppendProcessedPlanHistory(string planPath, string status, string message)
    {
        Directory.CreateDirectory(_config.HistoryFolder);
        string historyPath = Path.Combine(_config.HistoryFolder, _config.DuplicateMerge.ProcessedPlanHistoryFile);
        string line = string.Join('\t', DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), status, Path.GetFileName(planPath), message);
        File.AppendAllLines(historyPath, new[] { line });
    }

    private ScanCheckpoint LoadScanCheckpoint()
    {
        string path = ScanCheckpointPath();
        if (!File.Exists(path))
        {
            return new ScanCheckpoint
            {
                NextAccountCode = NormalizeAccountCode(_config.DuplicateMerge.AccountCodeScanStart),
                BatchNumber = 1,
                Complete = false,
                LastUpdated = DateTime.Now
            };
        }

        string json = File.ReadAllText(path);
        ScanCheckpoint? checkpoint = JsonSerializer.Deserialize<ScanCheckpoint>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return checkpoint ?? new ScanCheckpoint
        {
            NextAccountCode = NormalizeAccountCode(_config.DuplicateMerge.AccountCodeScanStart),
            BatchNumber = 1,
            Complete = false,
            LastUpdated = DateTime.Now
        };
    }

    private void SaveScanCheckpoint(ScanCheckpoint checkpoint)
    {
        Directory.CreateDirectory(_config.ControlFolder);
        checkpoint.NextAccountCode = NormalizeAccountCode(checkpoint.NextAccountCode);
        checkpoint.LastUpdated = DateTime.Now;
        string json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ScanCheckpointPath(), json);
        Console.WriteLine($"Checkpoint saved. NextAccountCode={checkpoint.NextAccountCode}; BatchNumber={checkpoint.BatchNumber}; Complete={checkpoint.Complete}");
    }

    private void AdvanceScanCheckpoint(ScanCheckpoint checkpoint, string completedRangeEnd, string configuredEnd)
    {
        checkpoint.BatchNumber++;
        checkpoint.NextAccountCode = AddToAccountCode(completedRangeEnd, 1);
        checkpoint.LastUpdated = DateTime.Now;
        checkpoint.Complete = string.CompareOrdinal(checkpoint.NextAccountCode, configuredEnd) > 0;
        SaveScanCheckpoint(checkpoint);
    }

    private void ResetScanCheckpoint()
    {
        var checkpoint = new ScanCheckpoint
        {
            NextAccountCode = NormalizeAccountCode(_config.DuplicateMerge.AccountCodeScanStart),
            BatchNumber = 1,
            Complete = false,
            LastUpdated = DateTime.Now
        };

        SaveScanCheckpoint(checkpoint);
        Console.WriteLine($"Scan checkpoint reset: {ScanCheckpointPath()}");
    }

    private string ScanCheckpointPath()
    {
        return Path.Combine(_config.ControlFolder, _config.DuplicateMerge.ScanCheckpointFile);
    }

    private void AppendPlannedGroupHistory(string planPath, IReadOnlyList<DuplicateAccountGroup> groups)
    {
        if (groups.Count == 0) return;

        Directory.CreateDirectory(_config.ControlFolder);
        string historyPath = Path.Combine(_config.ControlFolder, _config.DuplicateMerge.PlannedGroupHistoryFile);
        var lines = groups.Select(group => string.Join('\t',
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Path.GetFileName(planPath),
            group.MatchKey,
            group.MatchRule,
            group.Survivor.Account.AccountCode,
            group.Duplicates.Count,
            group.Duplicates.Sum(duplicate => duplicate.ContactCount)));

        File.AppendAllLines(historyPath, lines);
    }

    private void AppendFailedScanHistory(
        string itemType,
        string rangeStart,
        string rangeEnd,
        string accountCode,
        string accountName,
        Exception ex)
    {
        lock (_failedScanHistoryLock)
        {
            Directory.CreateDirectory(_config.ControlFolder);
            string historyPath = Path.Combine(_config.ControlFolder, _config.DuplicateMerge.FailedScanHistoryFile);
            bool writeHeader = !File.Exists(historyPath);
            var lines = new List<string>();
            if (writeHeader)
            {
                lines.Add("Timestamp\tItemType\tRangeStart\tRangeEnd\tAccountCode\tAccountName\tErrorType\tErrorMessage");
            }

            lines.Add(string.Join('\t',
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                itemType,
                rangeStart,
                rangeEnd,
                accountCode,
                SanitizeTsv(accountName),
                ex.GetType().FullName,
                SanitizeTsv(ex.Message)));

            File.AppendAllLines(historyPath, lines);
        }
    }

    private static string SanitizeTsv(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private static string NormalizeAccountCode(string value)
    {
        string digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(digits)) digits = "0";
        return digits.PadLeft(8, '0');
    }

    private static string AddToAccountCode(string accountCode, int increment)
    {
        if (!long.TryParse(NormalizeAccountCode(accountCode), out long value)) value = 0;
        long next = Math.Max(0, value + increment);
        return next.ToString().PadLeft(8, '0');
    }

    private sealed record MatchKeyParts(
        string MatchKey,
        string MatchRule,
        string NormalizedName,
        string WebsiteRootDomain,
        string Country);

    private sealed record PlanCreationResult(bool WorkbookCreated, int FailedAccounts);

    private sealed record AuditDuplicateOrgRow(
        string MatchKey,
        string NormalizedName,
        string WebsiteRootDomain,
        string AccountCode,
        string Name,
        string Country,
        string MarketSegmentMajor,
        string MarketSegmentMinor,
        string EventSalesStatus,
        string Website,
        string Email,
        string Phone);

    private sealed record ContactDedupeContactInfo(
        MomentusContactSnapshot Contact,
        IReadOnlyList<string> InterestCodes,
        IReadOnlyList<string> CompanyCodes);

    private sealed record ContactDedupeEmailResult(
        string Email,
        string FinalStatus,
        string SurvivorContactAccountCode,
        IReadOnlyList<ContactDedupeContactInfo> Contacts,
        IReadOnlyList<ContactDedupeLogRow> LogRows);

    private sealed record ContactDedupeAttemptResult(
        bool Success,
        string BlockingContactAccountCode);

    private sealed record ContactDedupeQueueItem(
        string Email,
        string NameEmailKey,
        int AuditGroupCount);

    private sealed record ContactDedupeRunSummaryRow(
        string Email,
        string NameEmailKey,
        int AuditGroupCount,
        string FinalStatus,
        string SurvivorContactAccountCode,
        int LiveContactRecordsFound,
        int DistinctCompanyRelationshipsFound,
        int DistinctInterestCodesFound,
        string ErrorMessage);

    private sealed record ContactDedupeRunDetailRow(
        string Email,
        string NameEmailKey,
        string Timestamp,
        string Action,
        string Status,
        string SourceContactCode,
        string TargetSurvivorCode,
        string RelatedCode,
        string Message);

    private sealed record ContactDedupeLogRow(
        string Timestamp,
        string Action,
        string Status,
        string SourceContactCode,
        string TargetSurvivorCode,
        string RelatedCode,
        string Message)
    {
        public static ContactDedupeLogRow Info(string message) =>
            new(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "Info", "INFO", string.Empty, string.Empty, string.Empty, message);

        public static ContactDedupeLogRow Applied(string action, string sourceContactCode, string targetSurvivorCode, string relatedCode, string message) =>
            new(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), action, "APPLIED", sourceContactCode, targetSurvivorCode, relatedCode, message);

        public static ContactDedupeLogRow FromResult(string action, string sourceContactCode, string targetSurvivorCode, string relatedCode, ApiWriteResult result) =>
            new(
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                action,
                result.Success ? "APPLIED" : "FAILED",
                sourceContactCode,
                targetSurvivorCode,
                relatedCode,
                result.Success ? result.Message : $"{result.Message} {result.ErrorMessage}".Trim());
    }

    private sealed record ApprovedActionRow(
        int RowNumber,
        string Action,
        string SurvivorAccountCode,
        string SurvivorName,
        string DuplicateAccountCode,
        string ContactAccountCode);

    private sealed record AppliedActionResult(
        string Action,
        ApiWriteResult Result,
        bool ChangedAccountStatus);

    private sealed record ApplyRowsResult(
        int Moved,
        int FieldCopies,
        int Inactivated,
        int FailedRows);
}
