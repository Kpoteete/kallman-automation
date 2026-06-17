using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

class Program
{
    private const string ApiUrl = "https://kallman.ungerboeck.com/prod";
    private const string ApiUser = "KYLEPAPI";
    private const string ApiPassword = "8c247eb8-2342-452a-95c3-cf22bd1c6a56";
    private const string ApiKey = "e2b97782-08d7-40f3-bdbc-fbef5095154c";
    private const string OrgCode = "10";

    private static readonly DateTime LookbackStartDate = new DateTime(2026, 3, 1);

    private const string RootFolder =
        @"C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents\Data Integrity Reports\Accounts & Contacts";

    private static readonly string MasterAuditPath =
        Path.Combine(RootFolder, "Master_Audit_Log.xlsx");

    private static readonly string[] AllowedGenericNameTokens = { "marketing", "sales", "colleague" };

    static int Main()
    {
        try
        {
            DateTime cutoff = LookbackStartDate;
            string runFolder = Path.Combine(RootFolder, DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(runFolder);

            Console.WriteLine("Loading user map...");
            var userMap = LoadUserMap(RootFolder);

            Console.WriteLine("Pulling accounts...");
            var client = BuildClient();
            var accounts = GetAccounts(client, cutoff);

            Console.WriteLine("Pulling contacts...");
            var contacts = GetContacts(client, cutoff);

            Console.WriteLine("Evaluating account data quality...");
            var accountIssues = BuildAccountIssues(accounts);

            Console.WriteLine("Evaluating contact data quality...");
            var contactIssues = BuildContactIssues(contacts);

            Console.WriteLine("Checking for possible duplicates...");
            AddAccountDuplicateIssues(accounts, accountIssues);
            AddContactDuplicateIssues(contacts, contactIssues);

            Console.WriteLine("Loading skipped issues from master audit log...");
            var skippedKeys = LoadSkippedErrorKeys();

            Console.WriteLine("Applying skipped issue filter...");
            accountIssues = FilterIssuesBySkippedStatus(accountIssues, skippedKeys);
            contactIssues = FilterIssuesBySkippedStatus(contactIssues, skippedKeys);

            Console.WriteLine("Creating user workbooks...");
            int workbookCount = CreateUserWorkbooks(runFolder, userMap, accountIssues, contactIssues);

            Console.WriteLine("Creating run summary workbook...");
            CreateRunSummaryWorkbook(runFolder, accountIssues, contactIssues);

            Console.WriteLine("Updating master audit log...");
            int masterRows = UpdateMasterAuditLog(DateTime.Now, runFolder, accountIssues, contactIssues);

            Console.WriteLine($"Workbooks created: {workbookCount}");
            Console.WriteLine("Run summary workbook created: Run_Summary.xlsx");
            Console.WriteLine($"Master log rows updated: {masterRows}");
            Console.WriteLine($"Output folder: {runFolder}");
            Console.WriteLine($"Master log: {MasterAuditPath}");
            Console.WriteLine("Done.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR");
            Console.WriteLine(ex);
            return 1;
        }
    }

    private static ApiClient BuildClient()
    {
        var auth = new Jwt
        {
            APIUserID = ApiUser,
            Secret = ApiPassword,
            Key = ApiKey,
            UngerboeckURI = ApiUrl,
            AutoRefresh = new AutoRefresh()
        };

        return new ApiClient(auth);
    }

    private static Dictionary<string, UserRecipient> LoadUserMap(string folder)
    {
        string[] candidates = Directory.GetFiles(folder, "users*.xlsx", SearchOption.TopDirectoryOnly);
        if (candidates.Length == 0)
            throw new FileNotFoundException("Could not find users Excel file in the root folder.");

        string usersPath = candidates.OrderByDescending(File.GetLastWriteTime).First();
        var map = new Dictionary<string, UserRecipient>(StringComparer.OrdinalIgnoreCase);

        using var workbook = new XLWorkbook(usersPath);
        var ws = workbook.Worksheets.First();
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

        for (int row = 1; row <= lastRow; row++)
        {
            string accountCode = ws.Cell(row, 1).GetString().Trim();
            string email = ws.Cell(row, 2).GetString().Trim();
            string name = ws.Cell(row, 3).GetString().Trim();

            if (string.IsNullOrWhiteSpace(accountCode) || string.IsNullOrWhiteSpace(email))
                continue;

            if (!map.ContainsKey(accountCode))
            {
                map[accountCode] = new UserRecipient
                {
                    AccountCode = accountCode,
                    Email = email,
                    Name = name
                };
            }
        }

        return map;
    }

    private static List<AccountExportRow> GetAccounts(ApiClient client, DateTime cutoff)
    {
        string filter =
            $"Class eq '{USISDKConstants.AccountClass.Account}' " +
            $"and (EventSalesStatus eq 'A' or EventSalesStatus eq 'P') " +
            $"and EnteredOn ge datetime'{cutoff:yyyy-MM-ddT00:00:00}'";

        SearchResponse<AllAccountsModel> response = client.Endpoints.Accounts.Search(OrgCode, filter);
        var results = response?.Results ?? new List<AllAccountsModel>();
        var rows = new List<AccountExportRow>();

        foreach (var account in results)
        {
            string accountCode = account.AccountCode ?? string.Empty;
            var interests = GetInterests(client, accountCode);

            rows.Add(new AccountExportRow
            {
                CompanyName = account.Name ?? string.Empty,
                Type = account.Type ?? string.Empty,
                Country = account.Country ?? string.Empty,
                State = account.State ?? string.Empty,
                Phone = account.Phone ?? string.Empty,
                Website = account.Website ?? string.Empty,
                AccountCode = accountCode,
                EnteredBy = account.EnteredBy ?? string.Empty,
                EnteredOn = account.EnteredOn,
                EventSalesStatus = account.EventSalesStatus ?? string.Empty,
                InterestCount = interests.Count,
                Interests = FlattenInterests(interests)
            });
        }

        return rows;
    }

    private static List<ContactExportRow> GetContacts(ApiClient client, DateTime cutoff)
    {
        string filter =
            $"Class eq '{USISDKConstants.AccountClass.Contact}' " +
            $"and (EventSalesStatus eq 'A' or EventSalesStatus eq 'P') " +
            $"and EnteredOn ge datetime'{cutoff:yyyy-MM-ddT00:00:00}'";

        SearchResponse<AllAccountsModel> response = client.Endpoints.Accounts.Search(OrgCode, filter);
        var results = response?.Results ?? new List<AllAccountsModel>();
        var rows = new List<ContactExportRow>();

        foreach (var contact in results)
        {
            string contactAccountCode = contact.AccountCode ?? string.Empty;
            var interests = GetInterests(client, contactAccountCode);
            string primaryAccountCode = ReadStringProperty(contact, "PrimaryAccount");
            string companyName = ResolveCompanyName(client, primaryAccountCode);

            rows.Add(new ContactExportRow
            {
                FirstName = contact.FirstName ?? string.Empty,
                LastName = contact.LastName ?? string.Empty,
                CompanyName = companyName,
                PrimaryAccount = primaryAccountCode,
                Country = contact.Country ?? string.Empty,
                State = contact.State ?? string.Empty,
                Phone = contact.Phone ?? string.Empty,
                Email = contact.Email ?? string.Empty,
                AccountCode = contactAccountCode,
                EnteredBy = contact.EnteredBy ?? string.Empty,
                EnteredOn = contact.EnteredOn,
                EventSalesStatus = contact.EventSalesStatus ?? string.Empty,
                InterestCount = interests.Count,
                Interests = FlattenInterests(interests),
                Type = contact.Type ?? string.Empty,
                DirectMailOptIn = ReadStringProperty(contact, "DirectMailOptIn")
            });
        }

        return rows;
    }

    private static List<AccountAffiliationsModel> GetInterests(ApiClient client, string accountCode)
    {
        if (string.IsNullOrWhiteSpace(accountCode))
            return new List<AccountAffiliationsModel>();

        try
        {
            SearchResponse<AccountAffiliationsModel> response =
                client.Endpoints.AccountAffiliations.Search(
                    OrgCode,
                    $"{nameof(AccountAffiliationsModel.AccountCode)} eq '{EscapeODataValue(accountCode)}'");

            return response?.Results?.ToList() ?? new List<AccountAffiliationsModel>();
        }
        catch
        {
            return new List<AccountAffiliationsModel>();
        }
    }

    private static string ResolveCompanyName(ApiClient client, string primaryAccountCode)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(primaryAccountCode))
            {
                var parent = client.Endpoints.Accounts.Get(OrgCode, primaryAccountCode);
                if (parent != null && !string.IsNullOrWhiteSpace(parent.Name))
                    return parent.Name;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static List<DataIssueRow> BuildAccountIssues(List<AccountExportRow> accounts)
    {
        var issues = new List<DataIssueRow>();

        foreach (var account in accounts)
        {
            AddIssueIfBlank(issues, "Account", account.AccountCode, account.CompanyName, "CompanyName", account.CompanyName,
                account.EnteredBy, "High", "Missing required field", "Company name is required.",
                "Open the account and enter the core company name only, with no commas or abbreviated legal suffixes.");

            AddIssueIfBlank(issues, "Account", account.AccountCode, account.CompanyName, "Type", account.Type,
                account.EnteredBy, "High", "Missing required field", "Type is required.",
                "Open the account and select the correct Type.");

            AddIssueIfBlank(issues, "Account", account.AccountCode, account.CompanyName, "Country", account.Country,
                account.EnteredBy, "High", "Missing required field", "Country is required.",
                "Open the account and select the best-fit Country.");

            AddIssueIfBlank(issues, "Account", account.AccountCode, account.CompanyName, "Website", account.Website,
                account.EnteredBy, "High", "Missing required field", "Website is required.",
                "Open the account and enter the root domain only, for example companyname.com.");

            if (!string.IsNullOrWhiteSpace(account.Website) && !IsValidRootDomain(account.Website))
            {
                issues.Add(new DataIssueRow
                {
                    RecordType = "Account",
                    AccountCode = account.AccountCode,
                    RecordName = account.CompanyName,
                    EnteredBy = account.EnteredBy,
                    FieldName = "Website",
                    CurrentValue = account.Website,
                    Severity = "High",
                    IssueType = "Bad format",
                    Rule = "Website must be the root domain only. Do not include www, http/https, or extra path text.",
                    HowToFix = "Open the account and replace the website with only the root domain, for example companyname.com."
                });
            }

            if (HasBadCompanySuffix(account.CompanyName))
            {
                issues.Add(new DataIssueRow
                {
                    RecordType = "Account",
                    AccountCode = account.AccountCode,
                    RecordName = account.CompanyName,
                    EnteredBy = account.EnteredBy,
                    FieldName = "CompanyName",
                    CurrentValue = account.CompanyName,
                    Severity = "Medium",
                    IssueType = "Standardization",
                    Rule = "Account names should not include abbreviated legal suffixes such as Inc, LLC, Corp, or Ltd.",
                    HowToFix = "Open the account and remove the abbreviated legal suffix so only the core company name remains."
                });
            }

            if (!string.IsNullOrWhiteSpace(account.CompanyName) && account.CompanyName.Contains(","))
            {
                issues.Add(new DataIssueRow
                {
                    RecordType = "Account",
                    AccountCode = account.AccountCode,
                    RecordName = account.CompanyName,
                    EnteredBy = account.EnteredBy,
                    FieldName = "CompanyName",
                    CurrentValue = account.CompanyName,
                    Severity = "Medium",
                    IssueType = "Standardization",
                    Rule = "Account names should use only the core company name with no commas.",
                    HowToFix = "Open the account and remove commas from the company name."
                });
            }
        }

        return issues;
    }

    private static List<DataIssueRow> BuildContactIssues(List<ContactExportRow> contacts)
    {
        var issues = new List<DataIssueRow>();

        foreach (var contact in contacts)
        {
            string fullName = $"{contact.FirstName} {contact.LastName}".Trim();
            bool allowedGenericName = ContainsAllowedGenericToken(contact.FirstName) || ContainsAllowedGenericToken(contact.LastName);

            if (!allowedGenericName)
            {
                AddIssueIfBlank(issues, "Contact", contact.AccountCode, fullName, "FirstName", contact.FirstName,
                    contact.EnteredBy, "High", "Missing required field", "First name is required.",
                    "Open the contact and enter the contact's first name.");

                AddIssueIfBlank(issues, "Contact", contact.AccountCode, fullName, "LastName", contact.LastName,
                    contact.EnteredBy, "High", "Missing required field", "Last name is required.",
                    "Open the contact and enter the contact's last name.");
            }

            if (string.IsNullOrWhiteSpace(contact.CompanyName))
            {
                issues.Add(new DataIssueRow
                {
                    RecordType = "Contact",
                    AccountCode = contact.AccountCode,
                    RecordName = fullName,
                    EnteredBy = contact.EnteredBy,
                    FieldName = "CompanyName",
                    CurrentValue = contact.CompanyName,
                    Severity = "High",
                    IssueType = "Missing required field",
                    Rule = "The relationship for this contact was not created and must be added.",
                    HowToFix = "Go into the contact, open Organizations, add a relationship, and add the correct account."
                });
            }

            if (contact.InterestCount <= 0)
            {
                issues.Add(new DataIssueRow
                {
                    RecordType = "Contact",
                    AccountCode = contact.AccountCode,
                    RecordName = fullName,
                    EnteredBy = contact.EnteredBy,
                    FieldName = "Interests",
                    CurrentValue = contact.Interests,
                    Severity = "High",
                    IssueType = "Missing required field",
                    Rule = "Every contact must have at least one interest.",
                    HowToFix = "Open the contact and add at least one relevant interest."
                });
            }

            AddIssueIfBlank(issues, "Contact", contact.AccountCode, fullName, "Type", contact.Type,
                contact.EnteredBy, "High", "Missing required field", "Type is required.",
                "Open the contact and select the correct Type.");

            AddIssueIfBlank(issues, "Contact", contact.AccountCode, fullName, "DirectMailOptIn", contact.DirectMailOptIn,
                contact.EnteredBy, "High", "Missing required field", "DirectMailOptIn is required.",
                "Open the contact and set the DirectMailOptIn checkbox correctly.");

            AddIssueIfBlank(issues, "Contact", contact.AccountCode, fullName, "Email", contact.Email,
                contact.EnteredBy, "Medium", "Missing recommended field", "Email is recommended.",
                "Open the contact and add the correct email address if it is known.");

            if (!string.IsNullOrWhiteSpace(contact.Email) && !IsValidEmail(contact.Email))
            {
                issues.Add(new DataIssueRow
                {
                    RecordType = "Contact",
                    AccountCode = contact.AccountCode,
                    RecordName = fullName,
                    EnteredBy = contact.EnteredBy,
                    FieldName = "Email",
                    CurrentValue = contact.Email,
                    Severity = "High",
                    IssueType = "Bad format",
                    Rule = "Email syntax appears invalid.",
                    HowToFix = "Open the contact and correct the email format or remove the bad email if it is invalid."
                });
            }

            if (!string.IsNullOrWhiteSpace(contact.DirectMailOptIn) &&
                IsTruthLike(contact.DirectMailOptIn) &&
                !string.IsNullOrWhiteSpace(contact.Email) &&
                !IsValidEmail(contact.Email))
            {
                issues.Add(new DataIssueRow
                {
                    RecordType = "Contact",
                    AccountCode = contact.AccountCode,
                    RecordName = fullName,
                    EnteredBy = contact.EnteredBy,
                    FieldName = "DirectMailOptIn",
                    CurrentValue = contact.DirectMailOptIn,
                    Severity = "High",
                    IssueType = "Conflict",
                    Rule = "Contacts marked as opted in for direct mail should not have an invalid email address.",
                    HowToFix = "Correct the email address or uncheck DirectMailOptIn until a valid email is added."
                });
            }
        }

        return issues;
    }

    private static void AddAccountDuplicateIssues(List<AccountExportRow> accounts, List<DataIssueRow> issues)
    {
        var duplicateGroups = accounts
            .Where(x => !string.IsNullOrWhiteSpace(x.CompanyName))
            .Select(x => new { Row = x, StandardizedName = StandardizeCompanyName(x.CompanyName) })
            .Where(x => !string.IsNullOrWhiteSpace(x.StandardizedName))
            .GroupBy(x => x.StandardizedName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Row.AccountCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .ToList();

        foreach (var group in duplicateGroups)
        {
            string relatedAccounts = string.Join(", ",
                group.Select(x => $"{x.Row.CompanyName} [{x.Row.AccountCode}]")
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            foreach (var item in group)
            {
                issues.Add(new DataIssueRow
                {
                    RecordType = "Account",
                    AccountCode = item.Row.AccountCode,
                    RecordName = item.Row.CompanyName,
                    EnteredBy = item.Row.EnteredBy,
                    FieldName = "CompanyName",
                    CurrentValue = item.Row.CompanyName,
                    Severity = "Medium",
                    IssueType = "Possible duplicate",
                    IsPossibleDuplicate = true,
                    Rule = $"Standardized account name matches other accounts: {group.Key}",
                    HowToFix = $"Review these possible duplicate accounts: {relatedAccounts}. Merge them or adjust the account names if they are truly different companies."
                });
            }
        }
    }

    private static void AddContactDuplicateIssues(List<ContactExportRow> contacts, List<DataIssueRow> issues)
    {
        var duplicateGroups = contacts
            .Where(x => !string.IsNullOrWhiteSpace(x.Email))
            .GroupBy(x => x.Email.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.AccountCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .ToList();

        foreach (var group in duplicateGroups)
        {
            string relatedContacts = string.Join(", ",
                group.Select(x => $"{x.FirstName} {x.LastName}".Trim() + $" [{x.AccountCode}]")
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            foreach (var item in group)
            {
                string fullName = $"{item.FirstName} {item.LastName}".Trim();

                issues.Add(new DataIssueRow
                {
                    RecordType = "Contact",
                    AccountCode = item.AccountCode,
                    RecordName = fullName,
                    EnteredBy = item.EnteredBy,
                    FieldName = "Email",
                    CurrentValue = item.Email,
                    Severity = "Medium",
                    IssueType = "Possible duplicate",
                    IsPossibleDuplicate = true,
                    Rule = $"Email appears on multiple contacts: {group.Key}",
                    HowToFix = $"Review these possible duplicate contacts: {relatedContacts}. Merge, inactivate, or remove the duplicate email where appropriate."
                });
            }
        }
    }

    private static HashSet<string> LoadSkippedErrorKeys()
    {
        var skippedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(MasterAuditPath))
            return skippedKeys;

        using var workbook = new XLWorkbook(MasterAuditPath);
        var ws = workbook.Worksheets.FirstOrDefault();
        if (ws == null || ws.LastRowUsed() == null)
            return skippedKeys;

        var headerMap = BuildHeaderMap(ws);
        int lastRow = ws.LastRowUsed().RowNumber();

        for (int row = 2; row <= lastRow; row++)
        {
            string errorKey = ReadCell(ws, row, headerMap, "ErrorKey");
            string status = ReadCell(ws, row, headerMap, "Status");

            if (string.IsNullOrWhiteSpace(errorKey))
                continue;

            if (string.Equals(status, "Skip", StringComparison.OrdinalIgnoreCase))
                skippedKeys.Add(errorKey);
        }

        return skippedKeys;
    }

    private static List<DataIssueRow> FilterIssuesBySkippedStatus(List<DataIssueRow> issues, HashSet<string> skippedKeys)
    {
        if (issues == null || issues.Count == 0)
            return new List<DataIssueRow>();

        if (skippedKeys == null || skippedKeys.Count == 0)
            return issues;

        return issues
            .Where(x => !skippedKeys.Contains(BuildErrorKey(x)))
            .ToList();
    }

    private static int CreateUserWorkbooks(
        string runFolder,
        Dictionary<string, UserRecipient> userMap,
        List<DataIssueRow> accountIssues,
        List<DataIssueRow> contactIssues)
    {
        int created = 0;

        var employeeUsers = accountIssues.Select(x => NormalizeUserName(x.EnteredBy))
            .Concat(contactIssues.Select(x => NormalizeUserName(x.EnteredBy)))
            .Where(IsEmployeeUser)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var user in employeeUsers)
        {
            var userAccountIssues = accountIssues
                .Where(x => string.Equals(NormalizeUserName(x.EnteredBy), user, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var userContactIssues = contactIssues
                .Where(x => string.Equals(NormalizeUserName(x.EnteredBy), user, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (userAccountIssues.Count == 0 && userContactIssues.Count == 0)
                continue;

            string workbookPath = Path.Combine(runFolder, $"{SanitizeFileName(user)}_DataIntegrityAudit.xlsx");
            CreateUserWorkbook(workbookPath, userAccountIssues, userContactIssues);
            created++;
        }

        return created;
    }

    private static void CreateUserWorkbook(string workbookPath, List<DataIssueRow> accountIssues, List<DataIssueRow> contactIssues)
    {
        using var workbook = new XLWorkbook();

        if (accountIssues.Count > 0)
            WriteIssueSheet(workbook, "Account Issues", accountIssues);

        if (contactIssues.Count > 0)
            WriteIssueSheet(workbook, "Contact Issues", contactIssues);

        workbook.SaveAs(workbookPath);
    }

    private static void CreateRunSummaryWorkbook(
        string runFolder,
        List<DataIssueRow> accountIssues,
        List<DataIssueRow> contactIssues)
    {
        string summaryPath = Path.Combine(runFolder, "Run_Summary.xlsx");

        using var workbook = new XLWorkbook();

        var allIssues = accountIssues
            .Concat(contactIssues)
            .OrderBy(x => NormalizeUserName(x.EnteredBy), StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.RecordType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.AccountCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.FieldName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        WriteUserSummarySheet(workbook, "User Summary", accountIssues, contactIssues);
        WriteIssueTypeSummarySheet(workbook, "Issue Type Summary", accountIssues, contactIssues);
        WriteIssueSheet(workbook, "All Issues", allIssues);

        workbook.SaveAs(summaryPath);
    }

    private static void WriteUserSummarySheet(
        XLWorkbook workbook,
        string sheetName,
        List<DataIssueRow> accountIssues,
        List<DataIssueRow> contactIssues)
    {
        var ws = workbook.Worksheets.Add(sheetName);

        string[] headers =
        {
            "EnteredBy",
            "Account Issue Count",
            "Contact Issue Count",
            "Total Issue Count",
            "High Severity Count",
            "Medium Severity Count",
            "Duplicate Count"
        };

        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];

        var allIssues = accountIssues.Concat(contactIssues).ToList();

        var grouped = allIssues
            .GroupBy(x => NormalizeUserName(x.EnteredBy), StringComparer.OrdinalIgnoreCase)
            .Where(g => IsEmployeeUser(g.Key))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int row = 2;

        foreach (var group in grouped)
        {
            string user = group.Key;
            int accountCount = accountIssues.Count(x =>
                string.Equals(NormalizeUserName(x.EnteredBy), user, StringComparison.OrdinalIgnoreCase));

            int contactCount = contactIssues.Count(x =>
                string.Equals(NormalizeUserName(x.EnteredBy), user, StringComparison.OrdinalIgnoreCase));

            int totalCount = group.Count();
            int highCount = group.Count(x => string.Equals(x.Severity, "High", StringComparison.OrdinalIgnoreCase));
            int mediumCount = group.Count(x => string.Equals(x.Severity, "Medium", StringComparison.OrdinalIgnoreCase));
            int duplicateCount = group.Count(x => x.IsPossibleDuplicate);

            ws.Cell(row, 1).Value = user;
            ws.Cell(row, 2).Value = accountCount;
            ws.Cell(row, 3).Value = contactCount;
            ws.Cell(row, 4).Value = totalCount;
            ws.Cell(row, 5).Value = highCount;
            ws.Cell(row, 6).Value = mediumCount;
            ws.Cell(row, 7).Value = duplicateCount;
            row++;
        }

        var table = ws.Range(1, 1, Math.Max(row - 1, 1), headers.Length)
            .CreateTable("UserSummary");
        table.Theme = XLTableTheme.None;

        var headerRange = ws.Range(1, 1, 1, headers.Length);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Font.FontColor = XLColor.Black;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
    }

    private static void WriteIssueTypeSummarySheet(
        XLWorkbook workbook,
        string sheetName,
        List<DataIssueRow> accountIssues,
        List<DataIssueRow> contactIssues)
    {
        var ws = workbook.Worksheets.Add(sheetName);

        string[] headers =
        {
            "IssueType",
            "RecordType",
            "Count"
        };

        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];

        var grouped = accountIssues
            .Concat(contactIssues)
            .GroupBy(x => new
            {
                IssueType = x.IssueType ?? string.Empty,
                RecordType = x.RecordType ?? string.Empty
            })
            .OrderBy(g => g.Key.IssueType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.RecordType, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int row = 2;

        foreach (var group in grouped)
        {
            ws.Cell(row, 1).Value = group.Key.IssueType;
            ws.Cell(row, 2).Value = group.Key.RecordType;
            ws.Cell(row, 3).Value = group.Count();
            row++;
        }

        var table = ws.Range(1, 1, Math.Max(row - 1, 1), headers.Length)
            .CreateTable("IssueTypeSummary");
        table.Theme = XLTableTheme.None;

        var headerRange = ws.Range(1, 1, 1, headers.Length);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Font.FontColor = XLColor.Black;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
    }

    private static void WriteIssueSheet(XLWorkbook workbook, string sheetName, List<DataIssueRow> rows)
    {
        var ws = workbook.Worksheets.Add(sheetName);

        string[] headers =
        {
            "EnteredBy", "RecordType", "AccountCode", "RecordName", "FieldName",
            "CurrentValue", "Severity", "IssueType", "DuplicateFlag", "Rule", "HowToFix"
        };

        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];

        int row = 2;
        foreach (var item in rows)
        {
            ws.Cell(row, 1).Value = item.EnteredBy;
            ws.Cell(row, 2).Value = item.RecordType;
            ws.Cell(row, 3).Value = item.AccountCode;
            ws.Cell(row, 4).Value = item.RecordName;
            ws.Cell(row, 5).Value = item.FieldName;
            ws.Cell(row, 6).Value = item.CurrentValue;
            ws.Cell(row, 7).Value = item.Severity;
            ws.Cell(row, 8).Value = item.IssueType;
            ws.Cell(row, 9).Value = item.IsPossibleDuplicate ? "Yes" : "No";
            ws.Cell(row, 10).Value = item.Rule;
            ws.Cell(row, 11).Value = item.HowToFix;

            var rowRange = ws.Range(row, 1, row, headers.Length);
            rowRange.Style.Fill.BackgroundColor = string.Equals(item.Severity, "High", StringComparison.OrdinalIgnoreCase)
                ? XLColor.LightPink
                : XLColor.LightYellow;

            row++;
        }

        var table = ws.Range(1, 1, Math.Max(row - 1, 1), headers.Length)
            .CreateTable(SanitizeTableName(sheetName.Replace(" ", "")));
        table.Theme = XLTableTheme.None;

        var headerRange = ws.Range(1, 1, 1, headers.Length);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Font.FontColor = XLColor.Black;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;

        ws.Columns(1, headers.Length).AdjustToContents();
        ws.SheetView.FreezeRows(1);
    }

    private static int UpdateMasterAuditLog(DateTime runDate, string runFolder, List<DataIssueRow> accountIssues, List<DataIssueRow> contactIssues)
    {
        var currentIssues = accountIssues
            .Concat(contactIssues)
            .Select(x => ToMasterAuditRow(x, runDate, runFolder))
            .ToList();

        var currentByKey = currentIssues
            .GroupBy(x => x.ErrorKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var existingRows = LoadMasterAuditRows();
        var existingByKey = existingRows
            .GroupBy(x => x.ErrorKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var pair in currentByKey)
        {
            if (existingByKey.TryGetValue(pair.Key, out MasterAuditRow existing))
            {
                existing.LastSeenRunDate = pair.Value.LastSeenRunDate;
                existing.LastSeenRunFolder = pair.Value.LastSeenRunFolder;
                existing.CurrentValue = pair.Value.CurrentValue;
                existing.Severity = pair.Value.Severity;
                existing.IssueType = pair.Value.IssueType;
                existing.IsPossibleDuplicate = pair.Value.IsPossibleDuplicate;
                existing.Rule = pair.Value.Rule;
                existing.HowToFix = pair.Value.HowToFix;
                existing.RecordName = pair.Value.RecordName;
                existing.EnteredBy = pair.Value.EnteredBy;

                if (!string.Equals(existing.Status, "Skip", StringComparison.OrdinalIgnoreCase))
                    existing.Status = "Open";

                existing.SeenCount += 1;
            }
            else
            {
                pair.Value.FirstSeenRunDate = pair.Value.LastSeenRunDate;
                pair.Value.Status = "Open";
                pair.Value.SeenCount = 1;
                existingRows.Add(pair.Value);
            }
        }

        foreach (var row in existingRows)
        {
            if (!currentByKey.ContainsKey(row.ErrorKey) && string.Equals(row.Status, "Open", StringComparison.OrdinalIgnoreCase))
                row.Status = "Not Seen This Run";
        }

        SaveMasterAuditRows(existingRows);
        return currentByKey.Count;
    }

    private static MasterAuditRow ToMasterAuditRow(DataIssueRow issue, DateTime runDate, string runFolder)
    {
        return new MasterAuditRow
        {
            ErrorKey = BuildErrorKey(issue),
            FirstSeenRunDate = runDate,
            LastSeenRunDate = runDate,
            LastSeenRunFolder = runFolder,
            Status = "Open",
            SeenCount = 1,
            EnteredBy = issue.EnteredBy,
            RecordType = issue.RecordType,
            AccountCode = issue.AccountCode,
            RecordName = issue.RecordName,
            FieldName = issue.FieldName,
            CurrentValue = issue.CurrentValue,
            Severity = issue.Severity,
            IssueType = issue.IssueType,
            IsPossibleDuplicate = issue.IsPossibleDuplicate,
            Rule = issue.Rule,
            HowToFix = issue.HowToFix
        };
    }

    private static string BuildErrorKey(DataIssueRow issue)
    {
        string baseText = string.Join("|",
            issue.RecordType ?? string.Empty,
            issue.AccountCode ?? string.Empty,
            issue.FieldName ?? string.Empty,
            issue.IssueType ?? string.Empty,
            issue.Rule ?? string.Empty);

        return baseText.Trim().ToLowerInvariant();
    }

    private static List<MasterAuditRow> LoadMasterAuditRows()
    {
        var rows = new List<MasterAuditRow>();

        if (!File.Exists(MasterAuditPath))
            return rows;

        using var workbook = new XLWorkbook(MasterAuditPath);
        var ws = workbook.Worksheets.FirstOrDefault();
        if (ws == null || ws.LastRowUsed() == null)
            return rows;

        var headerMap = BuildHeaderMap(ws);
        int lastRow = ws.LastRowUsed().RowNumber();

        for (int row = 2; row <= lastRow; row++)
        {
            if (string.IsNullOrWhiteSpace(ReadCell(ws, row, headerMap, "ErrorKey")))
                continue;

            rows.Add(new MasterAuditRow
            {
                ErrorKey = ReadCell(ws, row, headerMap, "ErrorKey"),
                FirstSeenRunDate = ReadDateCell(ws, row, headerMap, "FirstSeenRunDate"),
                LastSeenRunDate = ReadDateCell(ws, row, headerMap, "LastSeenRunDate"),
                LastSeenRunFolder = ReadCell(ws, row, headerMap, "LastSeenRunFolder"),
                Status = ReadCell(ws, row, headerMap, "Status"),
                SeenCount = ReadIntCell(ws, row, headerMap, "SeenCount"),
                EnteredBy = ReadCell(ws, row, headerMap, "EnteredBy"),
                RecordType = ReadCell(ws, row, headerMap, "RecordType"),
                AccountCode = ReadCell(ws, row, headerMap, "AccountCode"),
                RecordName = ReadCell(ws, row, headerMap, "RecordName"),
                FieldName = ReadCell(ws, row, headerMap, "FieldName"),
                CurrentValue = ReadCell(ws, row, headerMap, "CurrentValue"),
                Severity = ReadCell(ws, row, headerMap, "Severity"),
                IssueType = ReadCell(ws, row, headerMap, "IssueType"),
                IsPossibleDuplicate = string.Equals(ReadCell(ws, row, headerMap, "DuplicateFlag"), "Yes", StringComparison.OrdinalIgnoreCase),
                Rule = ReadCell(ws, row, headerMap, "Rule"),
                HowToFix = ReadCell(ws, row, headerMap, "HowToFix")
            });
        }

        return rows;
    }

    private static void SaveMasterAuditRows(List<MasterAuditRow> rows)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Master Audit Log");

        string[] headers =
        {
            "ErrorKey", "FirstSeenRunDate", "LastSeenRunDate", "LastSeenRunFolder", "Status", "SeenCount",
            "EnteredBy", "RecordType", "AccountCode", "RecordName", "FieldName", "CurrentValue",
            "Severity", "IssueType", "DuplicateFlag", "Rule", "HowToFix", "Days Aged"
        };

        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];

        int row = 2;
        foreach (var item in rows.OrderByDescending(x => x.LastSeenRunDate).ThenBy(x => x.EnteredBy).ThenBy(x => x.RecordType).ThenBy(x => x.AccountCode))
        {
            ws.Cell(row, 1).Value = item.ErrorKey;
            ws.Cell(row, 2).Value = item.FirstSeenRunDate;
            ws.Cell(row, 3).Value = item.LastSeenRunDate;
            ws.Cell(row, 4).Value = item.LastSeenRunFolder;
            ws.Cell(row, 5).Value = item.Status;
            ws.Cell(row, 6).Value = item.SeenCount;
            ws.Cell(row, 7).Value = item.EnteredBy;
            ws.Cell(row, 8).Value = item.RecordType;
            ws.Cell(row, 9).Value = item.AccountCode;
            ws.Cell(row, 10).Value = item.RecordName;
            ws.Cell(row, 11).Value = item.FieldName;
            ws.Cell(row, 12).Value = item.CurrentValue;
            ws.Cell(row, 13).Value = item.Severity;
            ws.Cell(row, 14).Value = item.IssueType;
            ws.Cell(row, 15).Value = item.IsPossibleDuplicate ? "Yes" : "No";
            ws.Cell(row, 16).Value = item.Rule;
            ws.Cell(row, 17).Value = item.HowToFix;

            if (TryCalculateDaysAged(item, out int daysAged))
                ws.Cell(row, 18).Value = daysAged;

            row++;
        }

        var table = ws.Range(1, 1, Math.Max(row - 1, 1), headers.Length).CreateTable("MasterAuditLog");
        table.Theme = XLTableTheme.None;

        var headerRange = ws.Range(1, 1, 1, headers.Length);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Font.FontColor = XLColor.Black;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);

        workbook.SaveAs(MasterAuditPath);
    }

    private static bool TryCalculateDaysAged(MasterAuditRow item, out int daysAged)
    {
        daysAged = 0;

        if (!string.Equals(item.Status, "Open", StringComparison.OrdinalIgnoreCase))
            return false;

        if (item.FirstSeenRunDate == DateTime.MinValue || item.LastSeenRunDate == DateTime.MinValue)
            return false;

        daysAged = (item.LastSeenRunDate.Date - item.FirstSeenRunDate.Date).Days;
        return true;
    }

    private static Dictionary<string, int> BuildHeaderMap(IXLWorksheet ws)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int lastColumn = ws.LastColumnUsed()?.ColumnNumber() ?? 0;

        for (int col = 1; col <= lastColumn; col++)
        {
            string header = ws.Cell(1, col).GetString().Trim();
            if (!string.IsNullOrWhiteSpace(header) && !map.ContainsKey(header))
                map[header] = col;
        }

        return map;
    }

    private static string ReadCell(IXLWorksheet ws, int row, Dictionary<string, int> headerMap, string header)
    {
        return headerMap.TryGetValue(header, out int col) ? ws.Cell(row, col).GetString().Trim() : string.Empty;
    }

    private static DateTime ReadDateCell(IXLWorksheet ws, int row, Dictionary<string, int> headerMap, string header)
    {
        if (!headerMap.TryGetValue(header, out int col))
            return DateTime.MinValue;

        var cell = ws.Cell(row, col);
        if (cell.IsEmpty())
            return DateTime.MinValue;

        if (cell.TryGetValue<DateTime>(out DateTime dateValue))
            return dateValue;

        if (DateTime.TryParse(cell.GetString(), out DateTime parsed))
            return parsed;

        return DateTime.MinValue;
    }

    private static int ReadIntCell(IXLWorksheet ws, int row, Dictionary<string, int> headerMap, string header)
    {
        if (!headerMap.TryGetValue(header, out int col))
            return 0;

        var cell = ws.Cell(row, col);

        if (cell.TryGetValue<int>(out int intValue))
            return intValue;

        if (int.TryParse(cell.GetString(), out int parsed))
            return parsed;

        return 0;
    }

    private static bool ContainsAllowedGenericToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string lowered = value.Trim().ToLowerInvariant();
        return AllowedGenericNameTokens.Any(t => lowered.Contains(t));
    }

    private static string StandardizeCompanyName(string companyName)
    {
        if (string.IsNullOrWhiteSpace(companyName))
            return string.Empty;

        string value = companyName.Trim().ToLowerInvariant();
        value = Regex.Replace(value, @"[,\.]+$", "");
        value = Regex.Replace(value, @"(?:,?\s)(inc\.?|llc|l\.l\.c\.|ltd\.?|corp\.?)\s*$", "", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\s+", " ").Trim();
        return value;
    }

    private static void AddIssueIfBlank(List<DataIssueRow> issues, string recordType, string accountCode,
        string recordName, string fieldName, string currentValue, string enteredBy, string severity,
        string issueType, string rule, string howToFix)
    {
        if (!string.IsNullOrWhiteSpace(currentValue))
            return;

        issues.Add(new DataIssueRow
        {
            RecordType = recordType,
            AccountCode = accountCode,
            RecordName = recordName,
            EnteredBy = enteredBy,
            FieldName = fieldName,
            CurrentValue = currentValue ?? string.Empty,
            Severity = severity,
            IssueType = issueType,
            Rule = rule,
            HowToFix = howToFix
        });
    }

    private static bool HasBadCompanySuffix(string companyName)
    {
        if (string.IsNullOrWhiteSpace(companyName))
            return false;

        return Regex.IsMatch(companyName.Trim(), @"(?:,?\s)(inc\.?|llc|l\.l\.c\.|ltd\.?|corp\.?)\s*$", RegexOptions.IgnoreCase);
    }

    private static bool IsValidRootDomain(string website)
    {
        if (string.IsNullOrWhiteSpace(website))
            return false;

        string value = website.Trim().ToLowerInvariant();
        if (value.StartsWith("http://")) return false;
        if (value.StartsWith("https://")) return false;
        if (value.StartsWith("www.")) return false;
        if (value.Contains("/")) return false;
        if (value.Contains("\\")) return false;
        if (value.Contains("?")) return false;
        if (value.Contains("#")) return false;
        if (value.Contains(" ")) return false;

        return Regex.IsMatch(value, @"^[a-z0-9][a-z0-9\-\.]*\.[a-z]{2,}$", RegexOptions.IgnoreCase);
    }

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        string value = email.Trim();
        if (value.Contains(" ")) return false;

        return Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.IgnoreCase);
    }

    private static bool IsTruthLike(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string normalized = value.Trim().ToLowerInvariant();
        return normalized == "true" || normalized == "yes" || normalized == "y" || normalized == "1" || normalized == "a";
    }

    private static string FlattenInterests(List<AccountAffiliationsModel> affiliations)
    {
        if (affiliations == null || affiliations.Count == 0)
            return string.Empty;

        var lines = new List<string>();
        foreach (var aff in affiliations)
        {
            var props = aff.GetType().GetProperties();
            var pairs = new List<string>();

            foreach (var prop in props)
            {
                try
                {
                    object value = prop.GetValue(aff);
                    if (value == null) continue;

                    string text = value is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm:ss") : value.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    pairs.Add($"{prop.Name}={text}");
                }
                catch
                {
                }
            }

            if (pairs.Count > 0)
                lines.Add(string.Join("; ", pairs));
        }

        return string.Join(" | ", lines);
    }

    private static string ReadStringProperty(object obj, string propertyName)
    {
        try
        {
            var prop = obj.GetType().GetProperty(propertyName);
            if (prop == null) return string.Empty;

            object value = prop.GetValue(obj);
            return value?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string EscapeODataValue(string value)
    {
        return value.Replace("'", "''");
    }

    private static string NormalizeUserName(string enteredBy)
    {
        return string.IsNullOrWhiteSpace(enteredBy) ? "Unknown User" : enteredBy.Trim();
    }

    private static bool IsEmployeeUser(string enteredBy)
    {
        string value = NormalizeUserName(enteredBy);
        return Regex.IsMatch(value, "[A-Za-z]") && !Regex.IsMatch(value, @"^\d+$");
    }

    private static string SanitizeFileName(string name)
    {
        string safe = NormalizeUserName(name);
        foreach (char c in Path.GetInvalidFileNameChars())
            safe = safe.Replace(c, '_');

        safe = Regex.Replace(safe, @"\s+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(safe) ? "Unknown_User" : safe;
    }

    private static string SanitizeTableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Tbl";

        string safe = Regex.Replace(name, @"[^A-Za-z0-9_]", "_");
        if (!char.IsLetter(safe[0]) && safe[0] != '_') safe = "_" + safe;
        if (safe.Length > 200) safe = safe.Substring(0, 200);
        return safe;
    }

    private sealed class UserRecipient
    {
        public string AccountCode { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    private sealed class AccountExportRow
    {
        public string CompanyName { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Website { get; set; } = string.Empty;
        public string AccountCode { get; set; } = string.Empty;
        public string EnteredBy { get; set; } = string.Empty;
        public DateTime? EnteredOn { get; set; }
        public string EventSalesStatus { get; set; } = string.Empty;
        public int InterestCount { get; set; }
        public string Interests { get; set; } = string.Empty;
    }

    private sealed class ContactExportRow
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string PrimaryAccount { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string AccountCode { get; set; } = string.Empty;
        public string EnteredBy { get; set; } = string.Empty;
        public DateTime? EnteredOn { get; set; }
        public string EventSalesStatus { get; set; } = string.Empty;
        public int InterestCount { get; set; }
        public string Interests { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string DirectMailOptIn { get; set; } = string.Empty;
    }

    private sealed class MasterAuditRow
    {
        public string ErrorKey { get; set; } = string.Empty;
        public DateTime FirstSeenRunDate { get; set; }
        public DateTime LastSeenRunDate { get; set; }
        public string LastSeenRunFolder { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int SeenCount { get; set; }
        public string EnteredBy { get; set; } = string.Empty;
        public string RecordType { get; set; } = string.Empty;
        public string AccountCode { get; set; } = string.Empty;
        public string RecordName { get; set; } = string.Empty;
        public string FieldName { get; set; } = string.Empty;
        public string CurrentValue { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string IssueType { get; set; } = string.Empty;
        public bool IsPossibleDuplicate { get; set; }
        public string Rule { get; set; } = string.Empty;
        public string HowToFix { get; set; } = string.Empty;
    }

    private sealed class DataIssueRow
    {
        public string EnteredBy { get; set; } = string.Empty;
        public string RecordType { get; set; } = string.Empty;
        public string AccountCode { get; set; } = string.Empty;
        public string RecordName { get; set; } = string.Empty;
        public string FieldName { get; set; } = string.Empty;
        public string CurrentValue { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string IssueType { get; set; } = string.Empty;
        public bool IsPossibleDuplicate { get; set; }
        public string Rule { get; set; } = string.Empty;
        public string HowToFix { get; set; } = string.Empty;
    }
}
