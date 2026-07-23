using ClosedXML.Excel;
using MarketSegmentApplication.Models;
using System.Collections.Concurrent;

namespace MarketSegmentApplication.Services;

public sealed class SegmentationRunner
{
    private readonly AppConfig _config;
    private readonly IMomentusApiService _api;
    private readonly SemaphoreSlim _applyApiLock = new(1, 1);

    public SegmentationRunner(AppConfig config, IMomentusApiService api)
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

        string? repairNoSegmentPlanPath = GetArgValue(args, "--repair-no-segment-plan");
        if (!string.IsNullOrWhiteSpace(repairNoSegmentPlanPath))
        {
            RepairNoSegmentBlankParentRows(repairNoSegmentPlanPath);
            return;
        }

        string? retallyPlanPath = GetArgValue(args, "--retally-failed-segments-plan");
        if (!string.IsNullOrWhiteSpace(retallyPlanPath))
        {
            RetallyFailedMarketSegmentRows(retallyPlanPath);
            return;
        }

        string? topInterestsPath = GetArgValue(args, "--top-contact-interests");
        if (!string.IsNullOrWhiteSpace(topInterestsPath))
        {
            await CreateTopContactInterestsReportAsync(topInterestsPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (HasArg(args, "--audit-data-quality"))
        {
            await CreateDataQualityAuditWorkbookAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (HasArg(args, "--reset-plan-history"))
        {
            ResetPlanHistory();
            return;
        }

        await CreatePlanAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateDataQualityAuditWorkbookAsync(CancellationToken cancellationToken)
    {
        int maxResults = _config.BatchLimits.MaxParentAccountsPerPlan;
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string outputPath = Path.Combine(_config.PendingReviewFolder, $"momentus_data_quality_audit_{timestamp}.xlsx");
        Directory.CreateDirectory(_config.PendingReviewFolder);

        string individualClassValue = _config.MomentusFields.ContactClassValue;

        Console.WriteLine($"Read-only audit. Scanning organization/company accounts where Class = '{_config.MomentusFields.AccountClassValue}' from {_config.Segmentation.AccountCodeScanStart} through {_config.Segmentation.AccountCodeScanEnd}...");
        IReadOnlyList<MomentusAuditAccountSnapshot> organizations = await _api
            .SearchAuditAccountsByClassAsync(_config.MomentusFields.AccountClassValue, maxResults, cancellationToken)
            .ConfigureAwait(false);
        organizations = organizations
            .Where(IsActiveOrProspectiveAuditAccount)
            .ToList();

        Console.WriteLine($"Loaded {organizations.Count:N0} active/prospective organization/company account(s). Scanning individual/contact/person accounts where Class = '{individualClassValue}'...");
        IReadOnlyList<MomentusAuditAccountSnapshot> individuals = await _api
            .SearchAuditAccountsByClassAsync(individualClassValue, maxResults, cancellationToken)
            .ConfigureAwait(false);
        individuals = individuals
            .Where(IsActiveOrProspectiveAuditAccount)
            .ToList();

        Console.WriteLine($"Loaded {individuals.Count:N0} active/prospective individual/contact/person account(s). Building audit workbook...");

        using var workbook = new XLWorkbook();
        WriteAuditSummarySheet(workbook, organizations, individuals);
        WritePotentialDuplicateOrganizationNamesSheet(workbook, organizations);
        WritePotentialDuplicateWebsitesSheet(workbook, organizations);
        WriteOrganizationCleanupSheet(workbook, organizations);
        WriteDuplicateIndividualNameEmailSheet(workbook, individuals);
        WriteContactLinkIssuesSheet(workbook, organizations, individuals);
        WriteOrganizationEmailWebsiteMismatchSheet(workbook, organizations);
        WriteParentContactEmailDomainsSheet(workbook, organizations, individuals);
        WriteContactEmailWebsiteMismatchSheet(workbook, organizations, individuals);
        WriteAuditRawOrganizationsSheet(workbook, organizations);
        WriteAuditRawIndividualsSheet(workbook, individuals);

        workbook.SaveAs(outputPath);
        Console.WriteLine($"Data-quality audit workbook created: {outputPath}");
    }

    private static void WriteAuditSummarySheet(
        XLWorkbook workbook,
        IReadOnlyList<MomentusAuditAccountSnapshot> organizations,
        IReadOnlyList<MomentusAuditAccountSnapshot> individuals)
    {
        var ws = workbook.Worksheets.Add("Summary");
        WriteHeaders(ws, new[] { "Metric", "Value" });

        var duplicateNameGroups = BuildDuplicateOrganizationNameGroups(organizations).Count;
        var duplicateWebsiteGroups = BuildDuplicateWebsiteGroups(organizations).Count;
        var duplicateIndividualNameEmailGroups = BuildDuplicateIndividualNameEmailGroups(individuals).Count;
        var orgCodes = organizations.Select(o => TextUtil.CleanKeyField(o.AccountCode)).Where(c => !string.IsNullOrWhiteSpace(c)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int individualsMissingPrimary = individuals.Count(c => string.IsNullOrWhiteSpace(TextUtil.CleanKeyField(c.PrimaryAccount)));
        int individualsMissingExistingPrimary = individuals.Count(c =>
        {
            string primary = TextUtil.CleanKeyField(c.PrimaryAccount);
            return !string.IsNullOrWhiteSpace(primary) && !orgCodes.Contains(primary);
        });

        var rows = new (string Metric, object Value)[]
        {
            ("Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
            ("Active/prospective organization/company accounts scanned (Class O)", organizations.Count),
            ($"Active/prospective individual/contact/person accounts scanned (Class {individuals.FirstOrDefault()?.Class ?? "configured contact class"})", individuals.Count),
            ("Potential duplicate organization-name groups", duplicateNameGroups),
            ("Potential duplicate website groups", duplicateWebsiteGroups),
            ("Duplicate individual name+email groups", duplicateIndividualNameEmailGroups),
            ("Individuals with blank PrimaryAccount", individualsMissingPrimary),
            ("Individuals with PrimaryAccount not found in scanned orgs", individualsMissingExistingPrimary),
            ("Organizations with email domain not matching website domain", CountOrganizationEmailWebsiteMismatches(organizations)),
            ("Parent accounts with multiple individual email domains", CountParentsWithMultipleContactEmailDomains(organizations, individuals)),
            ("Individuals with email domain not matching parent website domain", CountContactEmailWebsiteMismatches(organizations, individuals)),
            ("Organizations missing market segment major", organizations.Count(o => string.IsNullOrWhiteSpace(TextUtil.Clean(o.MarketSegmentMajor)))),
            ("Organizations missing country", organizations.Count(o => string.IsNullOrWhiteSpace(TextUtil.Clean(o.Country)))),
            ("Organizations missing website", organizations.Count(o => string.IsNullOrWhiteSpace(TextUtil.Clean(o.Website))))
        };

        int row = 2;
        foreach (var item in rows)
        {
            ws.Cell(row, 1).Value = item.Metric;
            if (item.Value is int intValue) ws.Cell(row, 2).Value = intValue;
            else ws.Cell(row, 2).Value = item.Value?.ToString() ?? string.Empty;
            row++;
        }

        FormatSheet(ws, 2);
    }

    private static void WritePotentialDuplicateOrganizationNamesSheet(XLWorkbook workbook, IReadOnlyList<MomentusAuditAccountSnapshot> organizations)
    {
        var ws = workbook.Worksheets.Add("Dup Org Names");
        string[] headers =
        {
            "GroupKey", "GroupCount", "AccountCode", "Name", "Country", "MarketSegmentMajor",
            "MarketSegmentMinor", "EventSalesStatus", "AccountStatus", "WebsiteRoot", "Website", "Email", "Phone"
        };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (var group in BuildDuplicateOrganizationNameGroups(organizations))
        {
            foreach (MomentusAuditAccountSnapshot account in group.OrderBy(a => a.AccountCode, StringComparer.OrdinalIgnoreCase))
            {
                WriteOrganizationAuditRow(ws, row++, group.Key, group.Count(), account);
            }
        }

        FormatSheet(ws, headers.Length);
    }

    private static void WritePotentialDuplicateWebsitesSheet(XLWorkbook workbook, IReadOnlyList<MomentusAuditAccountSnapshot> organizations)
    {
        var ws = workbook.Worksheets.Add("Dup Websites");
        string[] headers =
        {
            "WebsiteRoot", "GroupCount", "AccountCode", "Name", "Country", "MarketSegmentMajor",
            "MarketSegmentMinor", "EventSalesStatus", "AccountStatus", "WebsiteRootAgain", "Website", "Email", "Phone"
        };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (var group in BuildDuplicateWebsiteGroups(organizations))
        {
            foreach (MomentusAuditAccountSnapshot account in group.OrderBy(a => a.AccountCode, StringComparer.OrdinalIgnoreCase))
            {
                WriteOrganizationAuditRow(ws, row++, group.Key, group.Count(), account);
            }
        }

        FormatSheet(ws, headers.Length);
    }

    private static void WriteOrganizationCleanupSheet(XLWorkbook workbook, IReadOnlyList<MomentusAuditAccountSnapshot> organizations)
    {
        var ws = workbook.Worksheets.Add("Org Cleanup Flags");
        string[] headers =
        {
            "Severity", "Issue", "AccountCode", "Name", "Country", "MarketSegmentMajor", "MarketSegmentMinor",
            "EventSalesStatus", "AccountStatus", "WebsiteRoot", "Website", "Email", "Phone"
        };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (MomentusAuditAccountSnapshot account in organizations.OrderBy(a => a.AccountCode, StringComparer.OrdinalIgnoreCase))
        {
            foreach ((string severity, string issue) in GetOrganizationCleanupIssues(account))
            {
                ws.Cell(row, 1).Value = severity;
                ws.Cell(row, 2).Value = issue;
                WriteOrganizationDetails(ws, row, 3, account);
                row++;
            }
        }

        FormatSheet(ws, headers.Length);
    }

    private static void WriteDuplicateIndividualNameEmailSheet(XLWorkbook workbook, IReadOnlyList<MomentusAuditAccountSnapshot> individuals)
    {
        var ws = workbook.Worksheets.Add("Dup Individuals");
        string[] headers =
        {
            "NameEmailKey", "GroupCount", "IndividualAccountCode", "FirstName", "LastName", "Email", "Company",
            "PrimaryAccount", "EventSalesStatus", "AccountStatus"
        };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (var group in BuildDuplicateIndividualNameEmailGroups(individuals))
        {
            foreach (MomentusAuditAccountSnapshot contact in group.OrderBy(c => c.AccountCode, StringComparer.OrdinalIgnoreCase))
            {
                ws.Cell(row, 1).Value = group.Key;
                ws.Cell(row, 2).Value = group.Count();
                ws.Cell(row, 3).Value = contact.AccountCode;
                ws.Cell(row, 4).Value = contact.FirstName;
                ws.Cell(row, 5).Value = contact.LastName;
                ws.Cell(row, 6).Value = contact.Email;
                ws.Cell(row, 7).Value = contact.Company;
                ws.Cell(row, 8).Value = contact.PrimaryAccount;
                ws.Cell(row, 9).Value = contact.EventSalesStatus;
                ws.Cell(row, 10).Value = contact.AccountStatus;
                row++;
            }
        }

        FormatSheet(ws, headers.Length);
    }

    private static void WriteContactLinkIssuesSheet(
        XLWorkbook workbook,
        IReadOnlyList<MomentusAuditAccountSnapshot> organizations,
        IReadOnlyList<MomentusAuditAccountSnapshot> contacts)
    {
        var ws = workbook.Worksheets.Add("Contact Link Issues");
        string[] headers =
        {
            "Issue", "ContactAccountCode", "FirstName", "LastName", "Email", "Company",
            "PrimaryAccount", "EventSalesStatus", "AccountStatus"
        };
        WriteHeaders(ws, headers);

        var orgCodes = organizations.Select(o => TextUtil.CleanKeyField(o.AccountCode)).Where(c => !string.IsNullOrWhiteSpace(c)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int row = 2;
        foreach (MomentusAuditAccountSnapshot contact in contacts.OrderBy(c => c.AccountCode, StringComparer.OrdinalIgnoreCase))
        {
            string primary = TextUtil.CleanKeyField(contact.PrimaryAccount);
            string issue = string.Empty;
            if (string.IsNullOrWhiteSpace(primary))
                issue = "Blank PrimaryAccount on contact.";
            else if (!orgCodes.Contains(primary))
                issue = "PrimaryAccount was not found among scanned organization accounts.";

            if (string.IsNullOrWhiteSpace(issue)) continue;

            ws.Cell(row, 1).Value = issue;
            ws.Cell(row, 2).Value = contact.AccountCode;
            ws.Cell(row, 3).Value = contact.FirstName;
            ws.Cell(row, 4).Value = contact.LastName;
            ws.Cell(row, 5).Value = contact.Email;
            ws.Cell(row, 6).Value = contact.Company;
            ws.Cell(row, 7).Value = contact.PrimaryAccount;
            ws.Cell(row, 8).Value = contact.EventSalesStatus;
            ws.Cell(row, 9).Value = contact.AccountStatus;
            row++;
        }

        FormatSheet(ws, headers.Length);
    }

    private static void WriteOrganizationEmailWebsiteMismatchSheet(XLWorkbook workbook, IReadOnlyList<MomentusAuditAccountSnapshot> organizations)
    {
        var ws = workbook.Worksheets.Add("Org Email Website");
        string[] headers =
        {
            "Issue", "AccountCode", "Name", "WebsiteDomain", "EmailDomain", "Website", "Email",
            "Country", "MarketSegmentMajor", "MarketSegmentMinor", "EventSalesStatus", "AccountStatus"
        };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (MomentusAuditAccountSnapshot org in organizations.OrderBy(o => o.AccountCode, StringComparer.OrdinalIgnoreCase))
        {
            string websiteDomain = NormalizedComparableDomainFromWebsite(org.Website);
            string emailDomain = NormalizedComparableDomainFromEmail(org.Email);
            if (string.IsNullOrWhiteSpace(websiteDomain) || string.IsNullOrWhiteSpace(emailDomain)) continue;
            if (TextUtil.EqualsTrimmedIgnoreCase(websiteDomain, emailDomain)) continue;

            ws.Cell(row, 1).Value = "Organization email domain does not match website domain.";
            ws.Cell(row, 2).Value = org.AccountCode;
            ws.Cell(row, 3).Value = org.Name;
            ws.Cell(row, 4).Value = websiteDomain;
            ws.Cell(row, 5).Value = emailDomain;
            ws.Cell(row, 6).Value = org.Website;
            ws.Cell(row, 7).Value = org.Email;
            ws.Cell(row, 8).Value = org.Country;
            ws.Cell(row, 9).Value = org.MarketSegmentMajor;
            ws.Cell(row, 10).Value = org.MarketSegmentMinor;
            ws.Cell(row, 11).Value = org.EventSalesStatus;
            ws.Cell(row, 12).Value = org.AccountStatus;
            row++;
        }

        FormatSheet(ws, headers.Length);
    }

    private static void WriteParentContactEmailDomainsSheet(
        XLWorkbook workbook,
        IReadOnlyList<MomentusAuditAccountSnapshot> organizations,
        IReadOnlyList<MomentusAuditAccountSnapshot> contacts)
    {
        var ws = workbook.Worksheets.Add("Parent Contact Domains");
        string[] headers =
        {
            "Issue", "ParentAccountCode", "ParentName", "ParentWebsiteDomain", "ContactCount",
            "ContactEmailDomainCount", "ContactEmailDomains", "DomainsNotMatchingWebsite"
        };
        WriteHeaders(ws, headers);

        var orgByCode = organizations
            .Where(o => !string.IsNullOrWhiteSpace(TextUtil.CleanKeyField(o.AccountCode)))
            .GroupBy(o => TextUtil.CleanKeyField(o.AccountCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        int row = 2;
        foreach (var group in contacts
            .Where(c => !string.IsNullOrWhiteSpace(TextUtil.CleanKeyField(c.PrimaryAccount)))
            .GroupBy(c => TextUtil.CleanKeyField(c.PrimaryAccount), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            orgByCode.TryGetValue(group.Key, out MomentusAuditAccountSnapshot? parent);
            string parentWebsiteDomain = parent is null ? string.Empty : NormalizedComparableDomainFromWebsite(parent.Website);
            var contactDomains = group
                .Select(c => NormalizedComparableDomainFromEmail(c.Email))
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (contactDomains.Count == 0) continue;

            var mismatches = contactDomains
                .Where(domain => !string.IsNullOrWhiteSpace(parentWebsiteDomain) &&
                                 !TextUtil.EqualsTrimmedIgnoreCase(domain, parentWebsiteDomain))
                .ToList();

            bool multipleDomains = contactDomains.Count > 1;
            bool hasWebsiteMismatch = mismatches.Count > 0;
            if (!multipleDomains && !hasWebsiteMismatch) continue;

            ws.Cell(row, 1).Value = multipleDomains && hasWebsiteMismatch
                ? "Multiple contact email domains; at least one does not match parent website."
                : multipleDomains
                    ? "Multiple contact email domains under one parent account."
                    : "Contact email domain does not match parent website.";
            ws.Cell(row, 2).Value = group.Key;
            ws.Cell(row, 3).Value = parent?.Name ?? string.Empty;
            ws.Cell(row, 4).Value = parentWebsiteDomain;
            ws.Cell(row, 5).Value = group.Count();
            ws.Cell(row, 6).Value = contactDomains.Count;
            ws.Cell(row, 7).Value = string.Join(", ", contactDomains);
            ws.Cell(row, 8).Value = string.Join(", ", mismatches);
            row++;
        }

        FormatSheet(ws, headers.Length);
    }

    private static void WriteContactEmailWebsiteMismatchSheet(
        XLWorkbook workbook,
        IReadOnlyList<MomentusAuditAccountSnapshot> organizations,
        IReadOnlyList<MomentusAuditAccountSnapshot> contacts)
    {
        var ws = workbook.Worksheets.Add("Contact Email Website");
        string[] headers =
        {
            "Issue", "ContactAccountCode", "FirstName", "LastName", "Email", "ContactEmailDomain",
            "PrimaryAccount", "ParentName", "ParentWebsiteDomain", "ParentWebsite", "Company",
            "EventSalesStatus", "AccountStatus"
        };
        WriteHeaders(ws, headers);

        var orgByCode = organizations
            .Where(o => !string.IsNullOrWhiteSpace(TextUtil.CleanKeyField(o.AccountCode)))
            .GroupBy(o => TextUtil.CleanKeyField(o.AccountCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        int row = 2;
        foreach (MomentusAuditAccountSnapshot contact in contacts.OrderBy(c => c.AccountCode, StringComparer.OrdinalIgnoreCase))
        {
            string primary = TextUtil.CleanKeyField(contact.PrimaryAccount);
            if (string.IsNullOrWhiteSpace(primary) || !orgByCode.TryGetValue(primary, out MomentusAuditAccountSnapshot? parent)) continue;

            string contactEmailDomain = NormalizedComparableDomainFromEmail(contact.Email);
            string parentWebsiteDomain = NormalizedComparableDomainFromWebsite(parent.Website);
            if (string.IsNullOrWhiteSpace(contactEmailDomain) || string.IsNullOrWhiteSpace(parentWebsiteDomain)) continue;
            if (TextUtil.EqualsTrimmedIgnoreCase(contactEmailDomain, parentWebsiteDomain)) continue;

            ws.Cell(row, 1).Value = "Contact email domain does not match parent account website domain.";
            ws.Cell(row, 2).Value = contact.AccountCode;
            ws.Cell(row, 3).Value = contact.FirstName;
            ws.Cell(row, 4).Value = contact.LastName;
            ws.Cell(row, 5).Value = contact.Email;
            ws.Cell(row, 6).Value = contactEmailDomain;
            ws.Cell(row, 7).Value = primary;
            ws.Cell(row, 8).Value = parent.Name;
            ws.Cell(row, 9).Value = parentWebsiteDomain;
            ws.Cell(row, 10).Value = parent.Website;
            ws.Cell(row, 11).Value = contact.Company;
            ws.Cell(row, 12).Value = contact.EventSalesStatus;
            ws.Cell(row, 13).Value = contact.AccountStatus;
            row++;
        }

        FormatSheet(ws, headers.Length);
    }

    private static void WriteAuditRawOrganizationsSheet(XLWorkbook workbook, IReadOnlyList<MomentusAuditAccountSnapshot> organizations)
    {
        var ws = workbook.Worksheets.Add("Raw Organizations");
        string[] headers =
        {
            "AccountCode", "Name", "Class", "AccountStatus", "EventSalesStatus", "MarketSegmentMajor",
            "MarketSegmentMinor", "Country", "WebsiteRoot", "Website", "Email", "Phone"
        };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (MomentusAuditAccountSnapshot account in organizations.OrderBy(a => a.AccountCode, StringComparer.OrdinalIgnoreCase))
        {
            ws.Cell(row, 1).Value = account.AccountCode;
            ws.Cell(row, 2).Value = account.Name;
            ws.Cell(row, 3).Value = account.Class;
            ws.Cell(row, 4).Value = account.AccountStatus;
            ws.Cell(row, 5).Value = account.EventSalesStatus;
            ws.Cell(row, 6).Value = account.MarketSegmentMajor;
            ws.Cell(row, 7).Value = account.MarketSegmentMinor;
            ws.Cell(row, 8).Value = account.Country;
            ws.Cell(row, 9).Value = TextUtil.RootDomain(account.Website);
            ws.Cell(row, 10).Value = account.Website;
            ws.Cell(row, 11).Value = account.Email;
            ws.Cell(row, 12).Value = account.Phone;
            row++;
        }

        FormatSheet(ws, headers.Length);
    }

    private static void WriteAuditRawIndividualsSheet(XLWorkbook workbook, IReadOnlyList<MomentusAuditAccountSnapshot> individuals)
    {
        var ws = workbook.Worksheets.Add("Raw Individuals");
        string[] headers =
        {
            "AccountCode", "FirstName", "LastName", "Email", "Class", "AccountStatus", "EventSalesStatus",
            "PrimaryAccount", "Company", "Country", "WebsiteRoot", "Website", "Phone"
        };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (MomentusAuditAccountSnapshot individual in individuals.OrderBy(a => a.AccountCode, StringComparer.OrdinalIgnoreCase))
        {
            ws.Cell(row, 1).Value = individual.AccountCode;
            ws.Cell(row, 2).Value = individual.FirstName;
            ws.Cell(row, 3).Value = individual.LastName;
            ws.Cell(row, 4).Value = individual.Email;
            ws.Cell(row, 5).Value = individual.Class;
            ws.Cell(row, 6).Value = individual.AccountStatus;
            ws.Cell(row, 7).Value = individual.EventSalesStatus;
            ws.Cell(row, 8).Value = individual.PrimaryAccount;
            ws.Cell(row, 9).Value = individual.Company;
            ws.Cell(row, 10).Value = individual.Country;
            ws.Cell(row, 11).Value = TextUtil.RootDomain(individual.Website);
            ws.Cell(row, 12).Value = individual.Website;
            ws.Cell(row, 13).Value = individual.Phone;
            row++;
        }

        FormatSheet(ws, headers.Length);
    }

    private static IReadOnlyList<IGrouping<string, MomentusAuditAccountSnapshot>> BuildDuplicateOrganizationNameGroups(
        IReadOnlyList<MomentusAuditAccountSnapshot> organizations)
    {
        return organizations
            .Select(account => new
            {
                Account = account,
                Key = $"{TextUtil.CanonicalCompanyName(account.Name)}|{TextUtil.CleanKeyField(account.Country)}"
            })
            .Where(item => item.Key.Length > 1 && !item.Key.StartsWith("|", StringComparison.Ordinal))
            .GroupBy(item => item.Key, item => item.Account, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<IGrouping<string, MomentusAuditAccountSnapshot>> BuildDuplicateWebsiteGroups(
        IReadOnlyList<MomentusAuditAccountSnapshot> organizations)
    {
        return organizations
            .Select(account => new
            {
                Account = account,
                Key = TextUtil.RootDomain(account.Website)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key, item => item.Account, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<IGrouping<string, MomentusAuditAccountSnapshot>> BuildDuplicateIndividualNameEmailGroups(
        IReadOnlyList<MomentusAuditAccountSnapshot> individuals)
    {
        return individuals
            .Select(contact => new
            {
                Contact = contact,
                Key = $"{TextUtil.CanonicalCompanyName($"{contact.FirstName} {contact.LastName}")}|{TextUtil.NormalizeEmail(contact.Email)}"
            })
            .Where(item => !item.Key.StartsWith("|", StringComparison.Ordinal) &&
                           TextUtil.IsValidEmailForLookup(item.Contact.Email))
            .GroupBy(item => item.Key, item => item.Contact, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<(string Severity, string Issue)> GetOrganizationCleanupIssues(MomentusAuditAccountSnapshot account)
    {
        if (string.IsNullOrWhiteSpace(TextUtil.Clean(account.Name)))
            yield return ("High", "Organization account is missing a name.");
        if (string.IsNullOrWhiteSpace(TextUtil.Clean(account.MarketSegmentMajor)))
            yield return ("High", "Organization account is missing MarketSegmentMajor.");
        if (string.IsNullOrWhiteSpace(TextUtil.Clean(account.Country)))
            yield return ("Medium", "Organization account is missing Country.");
        if (string.IsNullOrWhiteSpace(TextUtil.Clean(account.Website)))
            yield return ("Low", "Organization account is missing Website.");
        if (!string.IsNullOrWhiteSpace(TextUtil.Clean(account.Website)) && string.IsNullOrWhiteSpace(TextUtil.RootDomain(account.Website)))
            yield return ("Medium", "Website is present but could not be normalized to a root domain.");
        if (!string.IsNullOrWhiteSpace(TextUtil.Clean(account.Email)) && !TextUtil.IsValidEmailForLookup(account.Email))
            yield return ("Medium", "Email is present but does not look valid.");
    }

    private static bool IsActiveOrProspectiveAuditAccount(MomentusAuditAccountSnapshot account)
    {
        return IsActiveOrProspectiveStatus(account.AccountStatus) &&
               IsActiveOrProspectiveStatus(account.EventSalesStatus);
    }

    private static bool IsActiveOrProspectiveStatus(string? status)
    {
        string clean = TextUtil.CleanKeyField(status);
        return TextUtil.EqualsTrimmedIgnoreCase(clean, "A") ||
               TextUtil.EqualsTrimmedIgnoreCase(clean, "P");
    }

    private static void WriteOrganizationAuditRow(IXLWorksheet ws, int row, string groupKey, int groupCount, MomentusAuditAccountSnapshot account)
    {
        ws.Cell(row, 1).Value = groupKey;
        ws.Cell(row, 2).Value = groupCount;
        WriteOrganizationDetails(ws, row, 3, account);
    }

    private static void WriteOrganizationDetails(IXLWorksheet ws, int row, int startColumn, MomentusAuditAccountSnapshot account)
    {
        ws.Cell(row, startColumn).Value = account.AccountCode;
        ws.Cell(row, startColumn + 1).Value = account.Name;
        ws.Cell(row, startColumn + 2).Value = account.Country;
        ws.Cell(row, startColumn + 3).Value = account.MarketSegmentMajor;
        ws.Cell(row, startColumn + 4).Value = account.MarketSegmentMinor;
        ws.Cell(row, startColumn + 5).Value = account.EventSalesStatus;
        ws.Cell(row, startColumn + 6).Value = account.AccountStatus;
        ws.Cell(row, startColumn + 7).Value = TextUtil.RootDomain(account.Website);
        ws.Cell(row, startColumn + 8).Value = account.Website;
        ws.Cell(row, startColumn + 9).Value = account.Email;
        ws.Cell(row, startColumn + 10).Value = account.Phone;
    }

    private static int CountOrganizationEmailWebsiteMismatches(IReadOnlyList<MomentusAuditAccountSnapshot> organizations)
    {
        return organizations.Count(org =>
        {
            string websiteDomain = NormalizedComparableDomainFromWebsite(org.Website);
            string emailDomain = NormalizedComparableDomainFromEmail(org.Email);
            return !string.IsNullOrWhiteSpace(websiteDomain) &&
                   !string.IsNullOrWhiteSpace(emailDomain) &&
                   !TextUtil.EqualsTrimmedIgnoreCase(websiteDomain, emailDomain);
        });
    }

    private static int CountParentsWithMultipleContactEmailDomains(
        IReadOnlyList<MomentusAuditAccountSnapshot> organizations,
        IReadOnlyList<MomentusAuditAccountSnapshot> contacts)
    {
        return contacts
            .Where(c => !string.IsNullOrWhiteSpace(TextUtil.CleanKeyField(c.PrimaryAccount)))
            .GroupBy(c => TextUtil.CleanKeyField(c.PrimaryAccount), StringComparer.OrdinalIgnoreCase)
            .Count(group => group
                .Select(c => NormalizedComparableDomainFromEmail(c.Email))
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1);
    }

    private static int CountContactEmailWebsiteMismatches(
        IReadOnlyList<MomentusAuditAccountSnapshot> organizations,
        IReadOnlyList<MomentusAuditAccountSnapshot> contacts)
    {
        var orgByCode = organizations
            .Where(o => !string.IsNullOrWhiteSpace(TextUtil.CleanKeyField(o.AccountCode)))
            .GroupBy(o => TextUtil.CleanKeyField(o.AccountCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return contacts.Count(contact =>
        {
            string primary = TextUtil.CleanKeyField(contact.PrimaryAccount);
            if (string.IsNullOrWhiteSpace(primary) || !orgByCode.TryGetValue(primary, out MomentusAuditAccountSnapshot? parent)) return false;
            string contactEmailDomain = NormalizedComparableDomainFromEmail(contact.Email);
            string parentWebsiteDomain = NormalizedComparableDomainFromWebsite(parent.Website);
            return !string.IsNullOrWhiteSpace(contactEmailDomain) &&
                   !string.IsNullOrWhiteSpace(parentWebsiteDomain) &&
                   !TextUtil.EqualsTrimmedIgnoreCase(contactEmailDomain, parentWebsiteDomain);
        });
    }

    private static string NormalizedComparableDomainFromEmail(string? email)
    {
        string normalized = TextUtil.NormalizeEmail(email);
        int at = normalized.LastIndexOf('@');
        if (at < 0 || at == normalized.Length - 1) return string.Empty;
        return ComparableDomain(normalized[(at + 1)..]);
    }

    private static string NormalizedComparableDomainFromWebsite(string? website)
    {
        return ComparableDomain(TextUtil.RootDomain(website));
    }

    private static string ComparableDomain(string? rawDomain)
    {
        string domain = TextUtil.CleanKeyField(rawDomain).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(domain)) return string.Empty;

        domain = domain.Replace("https://", string.Empty).Replace("http://", string.Empty);
        domain = domain.Split('/')[0].Split(':')[0].Trim('.');
        if (domain.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) domain = domain[4..];
        if (string.IsNullOrWhiteSpace(domain) || !domain.Contains('.')) return domain;

        string[] parts = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2) return domain;

        string tld = parts[^1];
        string second = parts[^2];
        if (tld.Length == 2 && second.Length <= 3 && parts.Length >= 3)
        {
            return string.Join('.', parts[^3], second, tld);
        }

        return string.Join('.', second, tld);
    }

    private async Task CreatePlanAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Loading segment mapping...");
        IReadOnlyDictionary<string, SegmentMapping> mappings = LoadSegmentMappings();
        Console.WriteLine($"Mappings loaded: {mappings.Count:N0}");

        HashSet<string> previouslyPlannedParentCodes = LoadPlannedParentHistory();
        int pullCount = _config.BatchLimits.MaxParentAccountsPerPlan + previouslyPlannedParentCodes.Count + 250;

        Console.WriteLine($"Pulling active/prospective parent organization accounts from AccountCode {_config.Segmentation.AccountCodeScanStart} through {_config.Segmentation.AccountCodeScanEnd}...");
        if (previouslyPlannedParentCodes.Count > 0 && _config.Segmentation.ExcludePreviouslyPlannedParents)
        {
            Console.WriteLine($"Skipping {previouslyPlannedParentCodes.Count:N0} parent accounts already present in planning history.");
        }

        IReadOnlyList<MomentusAccountSnapshot> parentCandidates = await _api.SearchActiveParentAccountsAsync(
            pullCount,
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Pulled {parentCandidates.Count:N0} active/prospective organization account candidate(s).");

        IReadOnlyList<MomentusAccountSnapshot> parents = parentCandidates
            .Where(parent => !_config.Segmentation.ExcludePreviouslyPlannedParents ||
                             !previouslyPlannedParentCodes.Contains(TextUtil.CleanKeyField(parent.AccountCode)))
            .Take(_config.BatchLimits.MaxParentAccountsPerPlan)
            .ToList();

        Console.WriteLine($"Planning next {parents.Count:N0} parent account(s).");

        var plans = new ConcurrentBag<AccountSegmentationPlan>();
        int processed = 0;
        int failed = 0;
        int maxWorkers = Math.Max(1, _config.BatchLimits.MaxConcurrentPlanParents);
        Console.WriteLine($"Planning with {maxWorkers:N0} worker(s).");

        await Parallel.ForEachAsync(parents, new ParallelOptions
        {
            MaxDegreeOfParallelism = maxWorkers,
            CancellationToken = cancellationToken
        }, async (parent, ct) =>
        {
            int current = Interlocked.Increment(ref processed);
            Console.WriteLine($"Planning {current}/{parents.Count}: {parent.AccountCode} - {parent.Name}");

            try
            {
                IReadOnlyList<string> parentInterestCodes = await _api.GetAccountAffiliationCodesAsync(parent.AccountCode, ct)
                    .ConfigureAwait(false);

                IReadOnlyList<MomentusContactSnapshot> contacts = await _api.GetContactsForParentAccountAsync(parent.AccountCode, ct)
                    .ConfigureAwait(false);

                var decisions = new List<ContactSegmentDecision>();
                var reviewReasons = new List<string>();

                foreach (MomentusContactSnapshot contact in contacts)
                {
                    IReadOnlyList<string> interestCodes = await _api.GetAccountAffiliationCodesAsync(contact.AccountCode, ct)
                        .ConfigureAwait(false);

                    decisions.Add(BuildContactDecision(contact, interestCodes, mappings));
                }

                foreach (ContactSegmentDecision decision in decisions.Where(d => d.UnmappedInterestCodes.Count > 0))
                {
                    reviewReasons.Add(
                        $"Contact {decision.Contact.AccountCode} has unmapped interest(s): {string.Join(", ", decision.UnmappedInterestCodes)}");
                }

                IReadOnlyList<SegmentMapping> requiredSegments = decisions
                    .SelectMany(d => d.ValidSegments)
                    .GroupBy(s => SegmentKey(s), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(s => s.MarketSegmentCombined, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                plans.Add(new AccountSegmentationPlan(parent, parentInterestCodes, decisions, requiredSegments, reviewReasons));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
                Console.WriteLine($"Planning failed for parent {parent.AccountCode} - {parent.Name}; skipping. Error: {ex.Message}");
            }
        }).ConfigureAwait(false);

        List<AccountSegmentationPlan> orderedPlans = plans
            .OrderBy(plan => plan.ParentAccount.AccountCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Console.WriteLine($"Planning complete. Successful={orderedPlans.Count:N0}; Failed={failed:N0}.");

        string outputPath = TimestampedPath(
            _config.Phase2Folder,
            _config.Segmentation.PlanWorkbookPrefix,
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"),
            ".xlsx");

        WritePlanWorkbook(outputPath, orderedPlans, mappings.Values);
        AppendPlannedParentHistory(orderedPlans.Select(plan => plan.ParentAccount));
        Console.WriteLine($"Plan workbook created: {outputPath}");
        Console.WriteLine($"Review the Actions sheet, mark approved action rows with APPROVED, then move the workbook to: {_config.ApprovedFolder}");
        Console.WriteLine("When ready, run: dotnet run -- --live --apply-approved");
    }

    private ContactSegmentDecision BuildContactDecision(
        MomentusContactSnapshot contact,
        IReadOnlyList<string> interestCodes,
        IReadOnlyDictionary<string, SegmentMapping> mappings)
    {
        var valid = new List<SegmentMapping>();
        var skipped = new List<string>();
        var unmapped = new List<string>();

        foreach (string rawCode in interestCodes)
        {
            string code = TextUtil.CleanKeyField(rawCode);
            if (string.IsNullOrWhiteSpace(code)) continue;

            if (!mappings.TryGetValue(code, out SegmentMapping? mapping))
            {
                unmapped.Add(code);
                continue;
            }

            if (mapping.Skip)
            {
                skipped.Add(code);
                continue;
            }

            valid.Add(mapping);
        }

        return new ContactSegmentDecision(
            contact,
            interestCodes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList(),
            valid,
            skipped.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList(),
            unmapped.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private void WritePlanWorkbook(string outputPath, IReadOnlyList<AccountSegmentationPlan> plans, IEnumerable<SegmentMapping> mappings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var workbook = new XLWorkbook();
        WriteSummarySheet(workbook, plans);
        WriteActionSheet(workbook, plans);
        WriteContactDetailsSheet(workbook, plans);
        WriteMappingSheet(workbook, mappings);
        workbook.SaveAs(outputPath);
    }

    private void WriteSummarySheet(XLWorkbook workbook, IReadOnlyList<AccountSegmentationPlan> plans)
    {
        var ws = workbook.Worksheets.Add("Summary");
        string[] headers =
        {
            "ParentAccountCode", "ParentAccountName", "CurrentMajor", "CurrentMinor", "Contacts",
            "ClassifiedSegments", "Decision", "ReviewRequired", "ReviewReasons"
        };

        WriteHeaders(ws, headers);

        int row = 2;
        foreach (AccountSegmentationPlan plan in plans)
        {
            SegmentTargetDecision targetDecision = ResolveSegmentTarget(plan);
            string decision = plan.NeedsReview
                ? "REVIEW_REQUIRED"
                : targetDecision.Kind;

            ws.Cell(row, 1).Value = plan.ParentAccount.AccountCode;
            ws.Cell(row, 2).Value = plan.ParentAccount.Name;
            ws.Cell(row, 3).Value = plan.ParentAccount.MarketSegmentMajor;
            ws.Cell(row, 4).Value = plan.ParentAccount.MarketSegmentMinor;
            ws.Cell(row, 5).Value = plan.Contacts.Count;
            ws.Cell(row, 6).Value = string.Join(" | ", plan.RequiredSegments.Select(s => s.MarketSegmentCombined));
            ws.Cell(row, 7).Value = decision;
            ws.Cell(row, 8).Value = plan.NeedsReview ? "YES" : "NO";
            ws.Cell(row, 9).Value = string.Join(" | ", plan.ReviewReasons);
            row++;
        }

        FormatSheet(ws, headers.Length);
    }

    private void WriteActionSheet(XLWorkbook workbook, IReadOnlyList<AccountSegmentationPlan> plans)
    {
        var ws = workbook.Worksheets.Add("Actions");
        string[] headers =
        {
            "ReviewDecision", "Action", "Status", "ParentAccountCode", "ParentAccountName",
            "ContactAccountCode", "ContactName", "ContactEmail", "InterestCodes",
            "MarketSegmentMajor", "MarketSegmentMinor", "MarketSegmentCombined",
            "TargetSegmentAccountCode", "Message"
        };

        WriteHeaders(ws, headers);

        int row = 2;
        foreach (PlanActionRow action in BuildActionRows(plans))
        {
            ws.Cell(row, 1).Value = string.Empty;
            ws.Cell(row, 2).Value = action.Action;
            ws.Cell(row, 3).Value = action.Status;
            ws.Cell(row, 4).Value = action.ParentAccountCode;
            ws.Cell(row, 5).Value = action.ParentAccountName;
            ws.Cell(row, 6).Value = action.ContactAccountCode;
            ws.Cell(row, 7).Value = action.ContactName;
            ws.Cell(row, 8).Value = action.ContactEmail;
            ws.Cell(row, 9).Value = action.InterestCodes;
            ws.Cell(row, 10).Value = action.MarketSegmentMajor;
            ws.Cell(row, 11).Value = action.MarketSegmentMinor;
            ws.Cell(row, 12).Value = action.MarketSegmentCombined;
            ws.Cell(row, 13).Value = action.TargetSegmentAccountCode;
            ws.Cell(row, 14).Value = action.Message;
            row++;
        }

        FormatSheet(ws, headers.Length);
        ws.Column(1).Style.Fill.BackgroundColor = XLColor.LightYellow;
    }

    private List<PlanActionRow> BuildActionRows(IReadOnlyList<AccountSegmentationPlan> plans)
    {
        var actions = new List<PlanActionRow>();

        foreach (AccountSegmentationPlan plan in plans)
        {
            HashSet<string> parentInterestCodes = plan.ParentInterestCodes
                .Select(TextUtil.CleanKeyField)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<string> contactInterestCodes = plan.Contacts
                .SelectMany(contact => contact.InterestCodes)
                .Select(TextUtil.CleanKeyField)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string interestCode in contactInterestCodes)
            {
                bool alreadyOnAccount = parentInterestCodes.Contains(interestCode);
                string message = alreadyOnAccount
                    ? "Interest is already on the organization account."
                    : "Roll contact interest up to the organization account.";

                actions.Add(new PlanActionRow(
                    "AddAccountInterest",
                    alreadyOnAccount ? "ALREADY_SET" : "PROPOSED",
                    plan.ParentAccount.AccountCode, plan.ParentAccount.Name,
                    string.Empty, string.Empty, string.Empty, interestCode,
                    string.Empty, string.Empty, string.Empty, plan.ParentAccount.AccountCode,
                    message));
            }

            if (!plan.HasClassifiedSegments)
            {
                bool parentMissingMarketSegment = string.IsNullOrWhiteSpace(TextUtil.Clean(plan.ParentAccount.MarketSegmentMajor));
                actions.Add(new PlanActionRow(
                    parentMissingMarketSegment ? "UpdateParentMarketSegment" : "NoMarketSegmentUpdate",
                    parentMissingMarketSegment ? "PROPOSED" : "INFO",
                    plan.ParentAccount.AccountCode, plan.ParentAccount.Name,
                    string.Empty, string.Empty, string.Empty, string.Empty,
                    parentMissingMarketSegment ? _config.Segmentation.ParentMarketSegmentMajor : string.Empty,
                    parentMissingMarketSegment ? _config.Segmentation.ParentMarketSegmentMinor : string.Empty,
                    parentMissingMarketSegment ? _config.Segmentation.ParentMarketSegmentCombined : string.Empty,
                    plan.ParentAccount.AccountCode,
                    parentMissingMarketSegment
                        ? "No mapped market-segment interests found, and parent market segment is blank. Apply parent/multiple-industries segment."
                        : "No mapped market-segment interests found. Interests may still be rolled up, but market segment is unchanged."));
                continue;
            }

            SegmentTargetDecision targetDecision = ResolveSegmentTarget(plan);
            SegmentMapping targetSegment = targetDecision.Segment;

            string marketStatus = TextUtil.EqualsTrimmedIgnoreCase(plan.ParentAccount.MarketSegmentMajor, targetSegment.MarketSegmentMajor) &&
                                  TextUtil.EqualsTrimmedIgnoreCase(plan.ParentAccount.MarketSegmentMinor, targetSegment.MarketSegmentMinor)
                ? "ALREADY_SET"
                : "PROPOSED";

            actions.Add(new PlanActionRow(
                "UpdateParentMarketSegment",
                marketStatus,
                plan.ParentAccount.AccountCode, plan.ParentAccount.Name,
                string.Empty, string.Empty, string.Empty, string.Join(", ", contactInterestCodes),
                targetSegment.MarketSegmentMajor,
                targetSegment.MarketSegmentMinor,
                targetSegment.MarketSegmentCombined,
                plan.ParentAccount.AccountCode,
                targetDecision.Message));

            if (plan.NeedsReview)
            {
                actions.Add(new PlanActionRow(
                    "ReviewRequired", "REVIEW_REQUIRED",
                    plan.ParentAccount.AccountCode, plan.ParentAccount.Name,
                    string.Empty, string.Empty, string.Empty, string.Join(", ", contactInterestCodes),
                    string.Empty, string.Empty, string.Empty, plan.ParentAccount.AccountCode,
                    string.Join(" | ", plan.ReviewReasons)));
            }
        }

        return actions;
    }

    private SegmentTargetDecision ResolveSegmentTarget(AccountSegmentationPlan plan)
    {
        if (!plan.HasClassifiedSegments)
        {
            return new SegmentTargetDecision(
                "NO_ACTION",
                new SegmentMapping(string.Empty, string.Empty, string.Empty, string.Empty, Skip: true),
                "No mapped market-segment interests found.");
        }

        if (plan.NeedsSingleSegmentUpdate)
        {
            return new SegmentTargetDecision(
                "APPLY_SINGLE_SEGMENT",
                plan.RequiredSegments[0],
                "Exactly one mapped market segment found across contact interests; apply it to the organization account.");
        }

        List<SegmentMapping> segmentVotes = plan.Contacts
            .SelectMany(contact => contact.ValidSegments)
            .Where(segment => !segment.Skip)
            .ToList();

        var rankedSegments = segmentVotes
            .GroupBy(SegmentKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Segment = group.First(),
                Count = group.Count()
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Segment.MarketSegmentCombined, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Segment.MarketSegmentMajor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Segment.MarketSegmentMinor, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var winner = rankedSegments.FirstOrDefault();
        if (winner is not null)
        {
            bool tied = rankedSegments.Count(item => item.Count == winner.Count) > 1;
            return new SegmentTargetDecision(
                tied ? "APPLY_ALPHABETICAL_TIE_SEGMENT" : "APPLY_TOP_TALLY_SEGMENT",
                winner.Segment,
                tied
                    ? $"Mapped interest tallies are tied at {winner.Count:N0}; alphabetical tie-break selected {winner.Segment.MarketSegmentCombined}."
                    : $"Mapped interest tally selected {winner.Segment.MarketSegmentCombined} with {winner.Count:N0} point(s).");
        }

        var parentSegment = new SegmentMapping(
            "MULTIPLE",
            _config.Segmentation.ParentMarketSegmentMajor,
            _config.Segmentation.ParentMarketSegmentMinor,
            _config.Segmentation.ParentMarketSegmentCombined,
            Skip: false);

        return new SegmentTargetDecision(
            "APPLY_PARENT_MULTIPLE_SEGMENTS",
            parentSegment,
            "No mapped segment votes were available. Applying parent/multiple-industries segment.");
    }

    private void WriteContactDetailsSheet(XLWorkbook workbook, IReadOnlyList<AccountSegmentationPlan> plans)
    {
        var ws = workbook.Worksheets.Add("Contact Details");
        string[] headers =
        {
            "ParentAccountCode", "ParentAccountName", "ContactAccountCode", "ContactName", "ContactEmail",
            "PrimaryAccount", "AllInterestCodes", "ValidSegments", "SkippedInterestCodes", "UnmappedInterestCodes"
        };

        WriteHeaders(ws, headers);

        int row = 2;
        foreach (AccountSegmentationPlan plan in plans)
        {
            foreach (ContactSegmentDecision decision in plan.Contacts)
            {
                ws.Cell(row, 1).Value = plan.ParentAccount.AccountCode;
                ws.Cell(row, 2).Value = plan.ParentAccount.Name;
                ws.Cell(row, 3).Value = decision.Contact.AccountCode;
                ws.Cell(row, 4).Value = ContactName(decision.Contact);
                ws.Cell(row, 5).Value = decision.Contact.Email;
                ws.Cell(row, 6).Value = decision.Contact.PrimaryAccount;
                ws.Cell(row, 7).Value = string.Join(", ", decision.InterestCodes);
                ws.Cell(row, 8).Value = string.Join(" | ", decision.ValidSegments.Select(s => s.MarketSegmentCombined));
                ws.Cell(row, 9).Value = string.Join(", ", decision.SkippedInterestCodes);
                ws.Cell(row, 10).Value = string.Join(", ", decision.UnmappedInterestCodes);
                row++;
            }
        }

        FormatSheet(ws, headers.Length);
    }

    private static void WriteMappingSheet(XLWorkbook workbook, IEnumerable<SegmentMapping> mappings)
    {
        var ws = workbook.Worksheets.Add("Segment Mapping");
        string[] headers = { "InterestCode", "MarketSegmentMajor", "MarketSegmentMinor", "MarketSegmentCombined", "Skip" };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (SegmentMapping mapping in mappings.OrderBy(m => m.InterestCode, StringComparer.OrdinalIgnoreCase))
        {
            ws.Cell(row, 1).Value = mapping.InterestCode;
            ws.Cell(row, 2).Value = mapping.MarketSegmentMajor;
            ws.Cell(row, 3).Value = mapping.MarketSegmentMinor;
            ws.Cell(row, 4).Value = mapping.MarketSegmentCombined;
            ws.Cell(row, 5).Value = mapping.Skip ? "YES" : "NO";
            row++;
        }

        FormatSheet(ws, headers.Length);
    }

    private async Task ApplyApprovedFolderAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_config.ApprovedFolder);
        IReadOnlyList<string> workbooks = Directory.GetFiles(_config.ApprovedFolder, "*.xlsx", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (workbooks.Count == 0)
        {
            Console.WriteLine($"No approved workbooks found in: {_config.ApprovedFolder}");
            Console.WriteLine("Create a plan, mark APPROVED rows, then move the workbook into the Approved folder.");
            return;
        }

        Console.WriteLine($"Found {workbooks.Count:N0} approved workbook(s).");
        foreach (string workbook in workbooks)
        {
            await ApplyApprovedPlanAsync(workbook, moveAfterApply: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyApprovedPlanAsync(string planPath, bool moveAfterApply, CancellationToken cancellationToken)
    {
        if (_config.DryRun)
        {
            throw new InvalidOperationException("Refusing to apply plan while DryRun is true. Re-run with --live after reviewing the workbook.");
        }

        if (!File.Exists(planPath))
            throw new FileNotFoundException("Plan workbook not found.", planPath);

        Console.WriteLine($"Applying approved rows from: {planPath}");

        bool appliedSuccessfully = false;
        try
        {
            using var workbook = new XLWorkbook(planPath);
            var ws = workbook.Worksheet("Actions");
            int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
            var approvedRows = new List<ApprovedActionRow>();
            int alreadyFinishedRows = 0;

            for (int row = 2; row <= lastRow; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string reviewDecision = ws.Cell(row, 1).GetString().Trim();
                if (!TextUtil.EqualsTrimmedIgnoreCase(reviewDecision, "APPROVED")) continue;

                string currentStatus = ws.Cell(row, 3).GetString().Trim();
                if (currentStatus is "APPLIED" or "FAILED" or "SKIPPED" or "SKIPPED_OLD_SPLIT_ACTION" or "ALREADY_SET")
                {
                    alreadyFinishedRows++;
                    continue;
                }

                approvedRows.Add(new ApprovedActionRow(
                    row,
                    ws.Cell(row, 2).GetString().Trim(),
                    ws.Cell(row, 4).GetString().Trim(),
                    ws.Cell(row, 9).GetString().Trim(),
                    ws.Cell(row, 10).GetString().Trim(),
                    ws.Cell(row, 11).GetString().Trim()));
            }

            object workbookLock = new();
            int maxWorkers = Math.Max(1, _config.BatchLimits.MaxConcurrentApplyRows);
            int saveEveryRows = Math.Max(1, _config.BatchLimits.ApplyWorkbookSaveEveryRows);
            if (alreadyFinishedRows > 0)
                Console.WriteLine($"Skipping {alreadyFinishedRows:N0} already-finished approved row(s).");
            Console.WriteLine($"Applying {approvedRows.Count:N0} approved row(s) with {maxWorkers:N0} worker(s). Saving every {saveEveryRows:N0} completed row(s).");

            ApplyRowsResult result = await ApplyApprovedRowsAsync(approvedRows, ws, workbook, workbookLock, maxWorkers, saveEveryRows, cancellationToken)
                .ConfigureAwait(false);

            workbook.Save();
            Console.WriteLine($"Apply complete. Account interests added/confirmed: {result.AccountInterestsAdded:N0}; parent market-segment updates: {result.ParentUpdates:N0}; failed rows: {result.FailedRows:N0}");
            appliedSuccessfully = result.FailedRows == 0;
        }
        finally
        {
            if (moveAfterApply)
            {
                MoveWorkbook(planPath, appliedSuccessfully ? _config.CompletedFolder : _config.FailedFolder);
            }
        }
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
        int parentUpdates = 0;
        int accountInterestsAdded = 0;
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
                    if (applied.Action == "AddAccountInterest") Interlocked.Add(ref accountInterestsAdded, applied.InterestsAdded);
                    if (applied.Action == "UpdateParentMarketSegment") Interlocked.Increment(ref parentUpdates);
                }
                else
                {
                    Interlocked.Increment(ref failedRows);
                }

                lock (workbookLock)
                {
                    WriteApplyResult(ws, row.RowNumber, applied);
                    int completed = Interlocked.Increment(ref completedRows);
                    rowsSinceSave++;
                    if (rowsSinceSave >= saveEveryRows)
                    {
                        workbook.Save();
                        rowsSinceSave = 0;
                        Console.WriteLine($"Saved apply progress after {completed:N0}/{rows.Count:N0} row(s).");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failedRows);
                lock (workbookLock)
                {
                    ws.Cell(row.RowNumber, 3).Value = "FAILED";
                    ws.Cell(row.RowNumber, 14).Value = ex.Message;
                    int completed = Interlocked.Increment(ref completedRows);
                    rowsSinceSave++;
                    if (rowsSinceSave >= saveEveryRows)
                    {
                        workbook.Save();
                        rowsSinceSave = 0;
                        Console.WriteLine($"Saved apply progress after {completed:N0}/{rows.Count:N0} row(s).");
                    }
                }
            }
        }).ConfigureAwait(false);

        lock (workbookLock)
        {
            workbook.Save();
        }

        return new ApplyRowsResult(parentUpdates, accountInterestsAdded, failedRows);
    }

    private void ResetFailedApplyRowsInApprovedFolder()
    {
        Directory.CreateDirectory(_config.ApprovedFolder);
        IReadOnlyList<string> workbooks = Directory.GetFiles(_config.ApprovedFolder, "*.xlsx", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
        int skipped = 0;

        for (int row = 2; row <= lastRow; row++)
        {
            string reviewDecision = ws.Cell(row, 1).GetString().Trim();
            if (!TextUtil.EqualsTrimmedIgnoreCase(reviewDecision, "APPROVED")) continue;

            string status = ws.Cell(row, 3).GetString().Trim();
            if (!TextUtil.EqualsTrimmedIgnoreCase(status, "FAILED")) continue;

            string action = ws.Cell(row, 2).GetString().Trim();
            if (action is "NoMarketSegmentUpdate" or "ReviewRequired" or "CreateSegmentAccount" or "CreateRelationship" or "UpdateParentToMultipleIndustries")
            {
                ws.Cell(row, 3).Value = "SKIPPED";
                ws.Cell(row, 14).Value = "No writable market-segment action for this row; skipped.";
                skipped++;
                continue;
            }

            string previousMessage = ws.Cell(row, 14).GetString().Trim();
            ws.Cell(row, 3).Value = "PROPOSED";
            ws.Cell(row, 14).Value = string.IsNullOrWhiteSpace(previousMessage)
                ? "Retry requested after previous failure."
                : $"Retry requested after previous failure. Previous error: {previousMessage}";
            reset++;
        }

        if (reset > 0 || skipped > 0) workbook.Save();
        Console.WriteLine($"Reset {reset:N0} failed writable row(s) and skipped {skipped:N0} non-writable row(s) in: {planPath}");
        return reset;
    }

    private int RepairNoSegmentBlankParentRows(string planPath)
    {
        if (!File.Exists(planPath))
            throw new FileNotFoundException("Plan workbook not found.", planPath);

        using var workbook = new XLWorkbook(planPath);
        var summary = workbook.Worksheet("Summary");
        var actions = workbook.Worksheet("Actions");

        int summaryLastRow = summary.LastRowUsed()?.RowNumber() ?? 1;
        var blankNoSegmentParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int row = 2; row <= summaryLastRow; row++)
        {
            string parentCode = TextUtil.CleanKeyField(summary.Cell(row, 1).GetString());
            string currentMajor = TextUtil.Clean(summary.Cell(row, 3).GetString());
            string classifiedSegments = TextUtil.Clean(summary.Cell(row, 6).GetString());
            string decision = TextUtil.Clean(summary.Cell(row, 7).GetString());

            if (!string.IsNullOrWhiteSpace(parentCode) &&
                string.IsNullOrWhiteSpace(currentMajor) &&
                string.IsNullOrWhiteSpace(classifiedSegments) &&
                TextUtil.EqualsTrimmedIgnoreCase(decision, "NO_ACTION"))
            {
                blankNoSegmentParents.Add(parentCode);
            }
        }

        int repaired = 0;
        int actionsLastRow = actions.LastRowUsed()?.RowNumber() ?? 1;
        for (int row = 2; row <= actionsLastRow; row++)
        {
            string parentCode = TextUtil.CleanKeyField(actions.Cell(row, 4).GetString());
            string action = TextUtil.Clean(actions.Cell(row, 2).GetString());
            if (!blankNoSegmentParents.Contains(parentCode) ||
                !TextUtil.EqualsTrimmedIgnoreCase(action, "NoMarketSegmentUpdate"))
            {
                continue;
            }

            actions.Cell(row, 1).Value = "APPROVED";
            actions.Cell(row, 2).Value = "UpdateParentMarketSegment";
            actions.Cell(row, 3).Value = "PROPOSED";
            actions.Cell(row, 10).Value = _config.Segmentation.ParentMarketSegmentMajor;
            actions.Cell(row, 11).Value = _config.Segmentation.ParentMarketSegmentMinor;
            actions.Cell(row, 12).Value = _config.Segmentation.ParentMarketSegmentCombined;
            actions.Cell(row, 14).Value = "No mapped market-segment interests found, and parent market segment is blank. Apply parent/multiple-industries segment.";
            repaired++;
        }

        if (repaired > 0) workbook.Save();
        Console.WriteLine($"Repaired {repaired:N0} blank-parent no-segment row(s) in: {planPath}");
        return repaired;
    }

    private int RetallyFailedMarketSegmentRows(string planPath)
    {
        if (!File.Exists(planPath))
            throw new FileNotFoundException("Plan workbook not found.", planPath);

        using var workbook = new XLWorkbook(planPath);
        var actions = workbook.Worksheet("Actions");
        var contacts = workbook.Worksheet("Contact Details");
        var mapping = workbook.Worksheet("Segment Mapping");

        Dictionary<string, SegmentMapping> segmentsByCombined = LoadSegmentsByCombined(mapping);
        Dictionary<string, SegmentTally> talliesByParent = BuildSegmentTalliesByParent(contacts, segmentsByCombined);

        int retallied = 0;
        int skippedNoVotes = 0;
        int actionsLastRow = actions.LastRowUsed()?.RowNumber() ?? 1;
        for (int row = 2; row <= actionsLastRow; row++)
        {
            string action = TextUtil.Clean(actions.Cell(row, 2).GetString());
            if (!TextUtil.EqualsTrimmedIgnoreCase(action, "UpdateParentMarketSegment")) continue;

            string status = TextUtil.Clean(actions.Cell(row, 3).GetString());
            if (!TextUtil.EqualsTrimmedIgnoreCase(status, "FAILED") &&
                !TextUtil.EqualsTrimmedIgnoreCase(status, "PROPOSED"))
            {
                continue;
            }

            string parentCode = TextUtil.CleanKeyField(actions.Cell(row, 4).GetString());
            if (!talliesByParent.TryGetValue(parentCode, out SegmentTally? tally))
            {
                skippedNoVotes++;
                continue;
            }

            actions.Cell(row, 1).Value = "APPROVED";
            actions.Cell(row, 2).Value = "UpdateParentMarketSegment";
            actions.Cell(row, 3).Value = "PROPOSED";
            actions.Cell(row, 10).Value = tally.Segment.MarketSegmentMajor;
            actions.Cell(row, 11).Value = tally.Segment.MarketSegmentMinor;
            actions.Cell(row, 12).Value = tally.Segment.MarketSegmentCombined;
            actions.Cell(row, 14).Value = tally.Tied
                ? $"Retallied mapped contact interest points. Tie at {tally.Count:N0}; alphabetical tie-break selected {tally.Segment.MarketSegmentCombined}."
                : $"Retallied mapped contact interest points. Selected {tally.Segment.MarketSegmentCombined} with {tally.Count:N0} point(s).";
            retallied++;
        }

        if (retallied > 0) workbook.Save();
        Console.WriteLine($"Retallied {retallied:N0} market-segment retry row(s); skipped {skippedNoVotes:N0} row(s) with no mapped segment votes in: {planPath}");
        return retallied;
    }

    private async Task CreateTopContactInterestsReportAsync(string accountCodeWorkbookPath, CancellationToken cancellationToken)
    {
        if (!Path.IsPathRooted(accountCodeWorkbookPath))
            accountCodeWorkbookPath = Path.Combine(_config.RootPath, accountCodeWorkbookPath);

        IReadOnlyList<string> accountCodes = LoadAccountCodesFromWorkbook(accountCodeWorkbookPath);
        Console.WriteLine($"Loaded {accountCodes.Count:N0} account code(s) from: {accountCodeWorkbookPath}");

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string progressPath = Path.Combine(_config.ControlFolder, $"top_contact_interests_{timestamp}.tsv");
        string outputPath = Path.Combine(_config.PendingReviewFolder, $"top_contact_interests_{timestamp}.xlsx");
        Directory.CreateDirectory(_config.ControlFolder);
        Directory.CreateDirectory(_config.PendingReviewFolder);

        File.WriteAllText(progressPath, string.Join('\t',
            "ParentAccountCode",
            "ParentAccountName",
            "AccountClass",
            "AccountStatus",
            "EventSalesStatus",
            "CurrentMajor",
            "CurrentMinor",
            "ContactsFound",
            "ContactsWithInterests",
            "UniqueInterestCodes",
            "Top1InterestCode",
            "Top1Count",
            "Top2InterestCode",
            "Top2Count",
            "Top3InterestCode",
            "Top3Count",
            "Status",
            "Message") + Environment.NewLine);

        var rows = new ConcurrentBag<TopContactInterestReportRow>();
        object fileLock = new();
        int processed = 0;
        int maxWorkers = Math.Max(1, _config.BatchLimits.MaxConcurrentPlanParents);
        Console.WriteLine($"Fetching contact interests with {maxWorkers:N0} worker(s). Progress TSV: {progressPath}");

        await Parallel.ForEachAsync(accountCodes, new ParallelOptions
        {
            MaxDegreeOfParallelism = maxWorkers,
            CancellationToken = cancellationToken
        }, async (accountCode, ct) =>
        {
            int current = Interlocked.Increment(ref processed);
            if (current == 1 || current == accountCodes.Count || current % 100 == 0)
                Console.WriteLine($"Checking {current:N0}/{accountCodes.Count:N0}: {accountCode}");

            TopContactInterestReportRow row;
            try
            {
                row = await BuildTopContactInterestReportRowAsync(accountCode, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                row = new TopContactInterestReportRow(
                    accountCode,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0,
                    0,
                    0,
                    Array.Empty<InterestCount>(),
                    "FAILED",
                    ex.Message);
            }

            rows.Add(row);
            lock (fileLock)
            {
                File.AppendAllText(progressPath, ToTopInterestsTsvLine(row) + Environment.NewLine);
            }
        }).ConfigureAwait(false);

        WriteTopContactInterestsWorkbook(outputPath, rows.OrderBy(row => row.ParentAccountCode, StringComparer.OrdinalIgnoreCase).ToList());
        Console.WriteLine($"Top contact interests report created: {outputPath}");
        Console.WriteLine($"Progress/details TSV retained: {progressPath}");
    }

    private async Task<TopContactInterestReportRow> BuildTopContactInterestReportRowAsync(
        string parentAccountCode,
        CancellationToken cancellationToken)
    {
        MomentusAccountSnapshot? account = null;
        string accountLookupMessage = string.Empty;
        try
        {
            account = await _api.GetAccountByCodeAsync(parentAccountCode, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            accountLookupMessage = $"Parent account detail lookup failed: {ex.Message}";
        }

        IReadOnlyList<MomentusContactSnapshot> contacts = await _api.GetContactsForParentAccountAsync(parentAccountCode, cancellationToken)
            .ConfigureAwait(false);

        var interestCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int contactsWithInterests = 0;
        foreach (MomentusContactSnapshot contact in contacts)
        {
            IReadOnlyList<string> interests = await _api.GetAccountAffiliationCodesAsync(contact.AccountCode, cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<string> uniqueContactInterests = interests
                .Select(TextUtil.CleanKeyField)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (uniqueContactInterests.Count > 0) contactsWithInterests++;

            foreach (string interest in uniqueContactInterests)
            {
                interestCounts[interest] = interestCounts.TryGetValue(interest, out int count) ? count + 1 : 1;
            }
        }

        IReadOnlyList<InterestCount> topInterests = interestCounts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(pair => new InterestCount(pair.Key, pair.Value))
            .ToList();

        return new TopContactInterestReportRow(
            parentAccountCode,
            account?.Name ?? string.Empty,
            account?.Class ?? string.Empty,
            account?.AccountStatus ?? string.Empty,
            account?.EventSalesStatus ?? string.Empty,
            account?.MarketSegmentMajor ?? string.Empty,
            account?.MarketSegmentMinor ?? string.Empty,
            contacts.Count,
            contactsWithInterests,
            interestCounts.Count,
            topInterests,
            "OK",
            accountLookupMessage);
    }

    private static IReadOnlyList<string> LoadAccountCodesFromWorkbook(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Account-code workbook not found.", path);

        using var workbook = new XLWorkbook(path);
        var ws = workbook.Worksheets.First();
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        var codes = new List<string>();
        for (int row = 1; row <= lastRow; row++)
        {
            string raw = TextUtil.CleanKeyField(ws.Cell(row, 1).GetString());
            string digits = new string(raw.Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(digits)) continue;
            codes.Add(digits.PadLeft(8, '0'));
        }

        return codes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void WriteTopContactInterestsWorkbook(
        string outputPath,
        IReadOnlyList<TopContactInterestReportRow> rows)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Top Contact Interests");
        string[] headers =
        {
            "ParentAccountCode",
            "ParentAccountName",
            "AccountClass",
            "AccountStatus",
            "EventSalesStatus",
            "CurrentMajor",
            "CurrentMinor",
            "ContactsFound",
            "ContactsWithInterests",
            "UniqueInterestCodes",
            "Top1InterestCode",
            "Top1Count",
            "Top2InterestCode",
            "Top2Count",
            "Top3InterestCode",
            "Top3Count",
            "Status",
            "Message"
        };

        WriteHeaders(ws, headers);
        int rowNumber = 2;
        foreach (TopContactInterestReportRow row in rows)
        {
            ws.Cell(rowNumber, 1).Value = row.ParentAccountCode;
            ws.Cell(rowNumber, 2).Value = row.ParentAccountName;
            ws.Cell(rowNumber, 3).Value = row.AccountClass;
            ws.Cell(rowNumber, 4).Value = row.AccountStatus;
            ws.Cell(rowNumber, 5).Value = row.EventSalesStatus;
            ws.Cell(rowNumber, 6).Value = row.CurrentMajor;
            ws.Cell(rowNumber, 7).Value = row.CurrentMinor;
            ws.Cell(rowNumber, 8).Value = row.ContactsFound;
            ws.Cell(rowNumber, 9).Value = row.ContactsWithInterests;
            ws.Cell(rowNumber, 10).Value = row.UniqueInterestCodes;
            for (int i = 0; i < 3; i++)
            {
                InterestCount? interest = row.TopInterests.Count > i ? row.TopInterests[i] : null;
                ws.Cell(rowNumber, 11 + (i * 2)).Value = interest?.InterestCode ?? string.Empty;
                ws.Cell(rowNumber, 12 + (i * 2)).Value = interest?.Count ?? 0;
            }

            ws.Cell(rowNumber, 17).Value = row.Status;
            ws.Cell(rowNumber, 18).Value = row.Message;
            rowNumber++;
        }

        FormatSheet(ws, headers.Length);
        workbook.SaveAs(outputPath);
    }

    private static string ToTopInterestsTsvLine(TopContactInterestReportRow row)
    {
        static string Clean(string value) => (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        string[] values =
        {
            row.ParentAccountCode,
            row.ParentAccountName,
            row.AccountClass,
            row.AccountStatus,
            row.EventSalesStatus,
            row.CurrentMajor,
            row.CurrentMinor,
            row.ContactsFound.ToString(),
            row.ContactsWithInterests.ToString(),
            row.UniqueInterestCodes.ToString(),
            row.TopInterests.Count > 0 ? row.TopInterests[0].InterestCode : string.Empty,
            row.TopInterests.Count > 0 ? row.TopInterests[0].Count.ToString() : "0",
            row.TopInterests.Count > 1 ? row.TopInterests[1].InterestCode : string.Empty,
            row.TopInterests.Count > 1 ? row.TopInterests[1].Count.ToString() : "0",
            row.TopInterests.Count > 2 ? row.TopInterests[2].InterestCode : string.Empty,
            row.TopInterests.Count > 2 ? row.TopInterests[2].Count.ToString() : "0",
            row.Status,
            row.Message
        };

        return string.Join('\t', values.Select(Clean));
    }

    private static Dictionary<string, SegmentMapping> LoadSegmentsByCombined(IXLWorksheet mapping)
    {
        var segments = new Dictionary<string, SegmentMapping>(StringComparer.OrdinalIgnoreCase);
        int lastRow = mapping.LastRowUsed()?.RowNumber() ?? 1;
        for (int row = 2; row <= lastRow; row++)
        {
            string combined = TextUtil.CleanKeyField(mapping.Cell(row, 4).GetString());
            if (string.IsNullOrWhiteSpace(combined)) continue;

            string major = TextUtil.CleanKeyField(mapping.Cell(row, 2).GetString());
            string minor = TextUtil.CleanKeyField(mapping.Cell(row, 3).GetString());
            bool skip = IsSkip(major) || IsSkip(minor) || IsSkip(combined);
            if (skip) continue;

            segments[combined] = new SegmentMapping(
                combined,
                major,
                minor,
                combined,
                Skip: false);
        }

        return segments;
    }

    private static Dictionary<string, SegmentTally> BuildSegmentTalliesByParent(
        IXLWorksheet contacts,
        IReadOnlyDictionary<string, SegmentMapping> segmentsByCombined)
    {
        var countsByParent = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        int lastRow = contacts.LastRowUsed()?.RowNumber() ?? 1;
        for (int row = 2; row <= lastRow; row++)
        {
            string parentCode = TextUtil.CleanKeyField(contacts.Cell(row, 1).GetString());
            if (string.IsNullOrWhiteSpace(parentCode)) continue;

            foreach (string combined in SplitSegmentCodes(contacts.Cell(row, 8).GetString()))
            {
                if (!segmentsByCombined.ContainsKey(combined)) continue;

                if (!countsByParent.TryGetValue(parentCode, out Dictionary<string, int>? counts))
                {
                    counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    countsByParent[parentCode] = counts;
                }

                counts[combined] = counts.TryGetValue(combined, out int count) ? count + 1 : 1;
            }
        }

        var result = new Dictionary<string, SegmentTally>(StringComparer.OrdinalIgnoreCase);
        foreach ((string parentCode, Dictionary<string, int> counts) in countsByParent)
        {
            var ranked = counts
                .Where(pair => segmentsByCombined.ContainsKey(pair.Key))
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ranked.Count == 0) continue;

            var winner = ranked[0];
            bool tied = ranked.Count > 1 && ranked[1].Value == winner.Value;
            result[parentCode] = new SegmentTally(segmentsByCombined[winner.Key], winner.Value, tied);
        }

        return result;
    }

    private async Task<AppliedActionResult> ApplyApprovedActionRowAsync(ApprovedActionRow row, CancellationToken cancellationToken)
    {
        if (row.Action == "AddAccountInterest")
        {
            await _applyApiLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                int added = 0;
                ApiWriteResult result = ApiWriteResult.Succeeded(row.ParentAccountCode, "No interest codes found on row.");
                foreach (string interestCode in SplitInterestCodes(row.InterestCodes))
                {
                    result = await _api.AddAccountAffiliationAsync(row.ParentAccountCode, interestCode, cancellationToken)
                        .ConfigureAwait(false);

                    if (!result.Success)
                    {
                        return new AppliedActionResult(row.Action, result, added, null);
                    }

                    added++;
                }

                return new AppliedActionResult(row.Action, result, added, null);
            }
            finally
            {
                _applyApiLock.Release();
            }
        }

        if (row.Action == "UpdateParentMarketSegment")
        {
            await _applyApiLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ApiWriteResult result = await _api.UpdateAccountMarketSegmentAsync(row.ParentAccountCode, row.MarketSegmentMajor, row.MarketSegmentMinor, cancellationToken)
                    .ConfigureAwait(false);
                return new AppliedActionResult(row.Action, result, 0, null);
            }
            finally
            {
                _applyApiLock.Release();
            }
        }

        if (row.Action is "NoMarketSegmentUpdate" or "ReviewRequired" or "CreateSegmentAccount" or "CreateRelationship" or "UpdateParentToMultipleIndustries")
        {
            return new AppliedActionResult(
                row.Action,
                ApiWriteResult.Succeeded(row.ParentAccountCode, "No writable market-segment action for this row; skipped."),
                0,
                "SKIPPED");
        }

        return new AppliedActionResult(
            row.Action,
            ApiWriteResult.Failed("Only AddAccountInterest and UpdateParentMarketSegment rows are applied."),
            0,
            null);
    }

    private IReadOnlyDictionary<string, SegmentMapping> LoadSegmentMappings()
    {
        string path = _config.Segmentation.SegmentMappingFile;
        if (!Path.IsPathRooted(path))
            path = Path.Combine(_config.RootPath, path);

        if (!File.Exists(path))
            throw new FileNotFoundException("Segment mapping workbook not found.", path);

        var map = new Dictionary<string, SegmentMapping>(StringComparer.OrdinalIgnoreCase);

        using var workbook = new XLWorkbook(path);
        var ws = workbook.Worksheets.First();
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

        for (int row = 2; row <= lastRow; row++)
        {
            string interestCode = TextUtil.CleanKeyField(ws.Cell(row, 1).GetString());
            if (string.IsNullOrWhiteSpace(interestCode)) continue;

            string major = TextUtil.CleanKeyField(ws.Cell(row, 2).GetString());
            string minor = TextUtil.CleanKeyField(ws.Cell(row, 3).GetString());
            string combined = TextUtil.CleanKeyField(ws.Cell(row, 4).GetString());
            bool skip = IsSkip(major) || IsSkip(minor) || IsSkip(combined);

            map[interestCode] = new SegmentMapping(
                interestCode,
                skip ? string.Empty : major,
                skip ? string.Empty : minor,
                skip ? string.Empty : combined,
                skip);
        }

        return map;
    }

    private HashSet<string> LoadPlannedParentHistory()
    {
        var history = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!_config.Segmentation.ExcludePreviouslyPlannedParents) return history;

        string path = PlanHistoryPath();
        if (!File.Exists(path)) return history;

        foreach (string line in File.ReadLines(path))
        {
            string code = TextUtil.CleanKeyField(line.Split('\t')[0]);
            if (!string.IsNullOrWhiteSpace(code)) history.Add(code);
        }

        return history;
    }

    private void AppendPlannedParentHistory(IEnumerable<MomentusAccountSnapshot> parents)
    {
        if (!_config.Segmentation.ExcludePreviouslyPlannedParents) return;

        string path = PlanHistoryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        HashSet<string> existingCodes = LoadPlannedParentHistory();

        var lines = parents
            .GroupBy(parent => TextUtil.CleanKeyField(parent.AccountCode), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(parent => !string.IsNullOrWhiteSpace(TextUtil.CleanKeyField(parent.AccountCode)) &&
                             !existingCodes.Contains(TextUtil.CleanKeyField(parent.AccountCode)))
            .Select(parent => $"{TextUtil.CleanKeyField(parent.AccountCode)}\t{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{parent.Name}")
            .ToList();

        if (lines.Count > 0)
            File.AppendAllLines(path, lines);
    }

    private void ResetPlanHistory()
    {
        string path = PlanHistoryPath();
        if (File.Exists(path))
        {
            File.Delete(path);
            Console.WriteLine($"Deleted planning history: {path}");
        }
        else
        {
            Console.WriteLine($"No planning history file found: {path}");
        }
    }

    private static void WriteApplyResult(IXLWorksheet ws, int row, AppliedActionResult applied)
    {
        ApiWriteResult result = applied.Result;
        ws.Cell(row, 3).Value = applied.StatusOverride ?? (result.Success ? "APPLIED" : "FAILED");
        ws.Cell(row, 14).Value = result.Success ? result.Message : $"{result.Message} {result.ErrorMessage}".Trim();
    }

    private static void MoveWorkbook(string sourcePath, string destinationFolder)
    {
        Directory.CreateDirectory(destinationFolder);
        string destinationPath = Path.Combine(destinationFolder, Path.GetFileName(sourcePath));
        if (File.Exists(destinationPath))
        {
            string fileName = Path.GetFileNameWithoutExtension(sourcePath);
            string extension = Path.GetExtension(sourcePath);
            destinationPath = Path.Combine(destinationFolder, $"{fileName}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}");
        }

        File.Move(sourcePath, destinationPath);
        Console.WriteLine($"Moved workbook to: {destinationPath}");
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

    private static string ContactName(MomentusContactSnapshot contact)
    {
        return TextUtil.Clean($"{contact.FirstName} {contact.LastName}");
    }

    private static string SegmentKey(SegmentMapping segment)
    {
        return $"{segment.MarketSegmentMajor}|{segment.MarketSegmentMinor}|{segment.MarketSegmentCombined}";
    }

    private static bool IsSkip(string value)
    {
        return TextUtil.EqualsTrimmedIgnoreCase(value, "skip");
    }

    private static IReadOnlyList<string> SplitInterestCodes(string value)
    {
        return (value ?? string.Empty)
            .Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TextUtil.CleanKeyField)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> SplitSegmentCodes(string value)
    {
        return (value ?? string.Empty)
            .Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TextUtil.CleanKeyField)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToList();
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

    private string PlanHistoryPath()
    {
        string path = _config.Segmentation.PlannedParentHistoryFile;
        return Path.IsPathRooted(path) ? path : Path.Combine(_config.RootPath, path);
    }

    private static string TimestampedPath(string folder, string prefix, string timestamp, string extension)
    {
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{prefix}_{timestamp}{extension}");
    }

    private static bool HasArg(string[] args, string name)
    {
        return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record SegmentTargetDecision(
        string Kind,
        SegmentMapping Segment,
        string Message);

    private sealed record ApprovedActionRow(
        int RowNumber,
        string Action,
        string ParentAccountCode,
        string InterestCodes,
        string MarketSegmentMajor,
        string MarketSegmentMinor);

    private sealed record AppliedActionResult(
        string Action,
        ApiWriteResult Result,
        int InterestsAdded,
        string? StatusOverride);

    private sealed record ApplyRowsResult(
        int ParentUpdates,
        int AccountInterestsAdded,
        int FailedRows);

    private sealed record SegmentTally(
        SegmentMapping Segment,
        int Count,
        bool Tied);

    private sealed record InterestCount(
        string InterestCode,
        int Count);

    private sealed record TopContactInterestReportRow(
        string ParentAccountCode,
        string ParentAccountName,
        string AccountClass,
        string AccountStatus,
        string EventSalesStatus,
        string CurrentMajor,
        string CurrentMinor,
        int ContactsFound,
        int ContactsWithInterests,
        int UniqueInterestCodes,
        IReadOnlyList<InterestCount> TopInterests,
        string Status,
        string Message);
}
