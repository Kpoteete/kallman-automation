using System.Collections;
using System.Reflection;
using DuplicateMerging.Models;
using Ungerboeck.Api.Models.Authorization;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace DuplicateMerging.Services;

public sealed class MomentusSdkApiService : IMomentusApiService
{
    private readonly AppConfig _config;
    private readonly MomentusFieldMap _fields;
    private readonly ApiClient _client;
    private readonly RetryPolicy _retryPolicy;
    private int _nextGeneratedContactCode;

    public MomentusSdkApiService(AppConfig config, MomentusCredentials credentials)
    {
        _config = config;
        _fields = config.MomentusFields;
        _retryPolicy = new RetryPolicy(config.MaxApiRetries);
        _nextGeneratedContactCode = Math.Max(1, config.CodeGeneration.StartingContactAccountCode);

        var auth = new Jwt
        {
            APIUserID = credentials.ApiUser,
            Secret = credentials.Secret,
            Key = credentials.Key,
            UngerboeckURI = config.MomentusUri,
            AutoRefresh = new AutoRefresh()
        };

        _client = new ApiClient(auth);
    }

    public Task EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        // Do not run a broad "All" search as a connection test.
        // Some Momentus tenants reject broad searches before maxresults is applied.
        // The first real Phase 1 email lookup will confirm API access using the user's actual import data.
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<ContactLookupResult> FindContactByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedEmail)) return ContactLookupResult.NotFound();

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string odata = BuildEmailSearchOData(normalizedEmail);
            var candidates = await SearchAccountsAsync(odata, cancellationToken).ConfigureAwait(false);

            foreach (object model in candidates)
            {
                string candidateEmail = TextUtil.NormalizeEmail(GetString(model, _fields.ContactEmail, "Email", "EmailAddress", "EMail"));
                if (!string.Equals(candidateEmail, normalizedEmail, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string classValue = GetString(model, _fields.Class, "Class", "AccountClass", "ClassCode");
                if (!TextUtil.EqualsTrimmedIgnoreCase(classValue, _fields.ContactClassValue))
                {
                    continue;
                }

                string accountCode = GetString(model, _fields.AccountCode, "AccountCode", "Code");
                return new ContactLookupResult(
                    true,
                    accountCode,
                    candidateEmail,
                    GetString(model, "FirstName"),
                    GetString(model, "LastName"),
                    string.IsNullOrWhiteSpace(accountCode)
                        ? "Duplicate contact email found in Momentus, but the response did not include AccountCode."
                        : $"Duplicate contact email found in Momentus. Existing contact AccountCode: {accountCode}.");
            }

            return ContactLookupResult.NotFound();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        ContactLookupResult result = await FindContactByEmailAsync(normalizedEmail, cancellationToken).ConfigureAwait(false);
        return result.Found;
    }

    public async Task<IReadOnlyList<MomentusContactSnapshot>> SearchActiveContactsByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedEmail)) return Array.Empty<MomentusContactSnapshot>();

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanEmail = TextUtil.NormalizeEmail(normalizedEmail);
            string odata =
                $"{BuildEmailSearchOData(cleanEmail)} and " +
                $"{_fields.Class} eq '{ODataString(_fields.ContactClassValue)}' and " +
                "(EventSalesStatus eq 'A' or EventSalesStatus eq 'P')";

            var candidates = await SearchAccountsAsync(odata, cancellationToken, maxResults: 10000).ConfigureAwait(false);
            return candidates
                .Select(ToContactSnapshot)
                .Where(contact => !string.IsNullOrWhiteSpace(contact.AccountCode))
                .Where(contact => TextUtil.EqualsTrimmedIgnoreCase(TextUtil.NormalizeEmail(contact.Email), cleanEmail))
                .GroupBy(contact => contact.AccountCode, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(contact => contact.AccountCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountLookupResult> FindOrganizationAccountAsync(
        string companyName,
        string marketSegmentMajor,
        string country,
        string websiteRootDomain,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanCountry = _config.CleanCountryForMomentus(country);
            string cleanWebsiteRoot = TextUtil.RootDomain(websiteRootDomain);
            var candidates = new List<object>();

            string nameSearch = BuildOrganizationAccountSearchOData(companyName, marketSegmentMajor, cleanCountry);
            candidates.AddRange(await SearchAccountsAsync(nameSearch, cancellationToken).ConfigureAwait(false));

            if (_config.DuplicateCheck.UseRelaxedCompanyNameMatch)
            {
                foreach (string relaxedExpression in BuildRelaxedCompanyNameSearchExpressions(companyName))
                {
                    try
                    {
                        candidates.AddRange(await SearchAccountsAsync(relaxedExpression, cancellationToken).ConfigureAwait(false));
                    }
                    catch
                    {
                        // Some tenants do not support contains() or broader name searches.
                        // Exact-name and website matching still run; local relaxed matching is used on returned candidates.
                    }
                }
            }

            if (_config.DuplicateCheck.UseWebsiteRootDomainForAccountMatch && !string.IsNullOrWhiteSpace(cleanWebsiteRoot))
            {
                foreach (string websiteExpression in BuildWebsiteSearchExpressions(cleanWebsiteRoot))
                {
                    try
                    {
                        candidates.AddRange(await SearchAccountsAsync(websiteExpression, cancellationToken).ConfigureAwait(false));
                    }
                    catch
                    {
                        // Some tenants use a different website field name or do not support website search.
                        // Name-based matching still runs; local website matching is used on any returned candidates.
                    }
                }
            }

            var exactMatches = candidates
                .Select(ToAccountLookupResult)
                .Where(result => result.Found)
                .GroupBy(result => result.AccountCode, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Where(result => IsExactOrganizationMatch(result, companyName, marketSegmentMajor, country, cleanCountry, cleanWebsiteRoot))
                .ToList();

            if (exactMatches.Count == 0) return AccountLookupResult.NotFound();

            if (exactMatches.Count > 1)
            {
                throw new InvalidOperationException(
                    "Multiple organization accounts matched the Company/Website + Country + Market Segment rule. " +
                    "Stop this row for human review. Matching account codes: " +
                    string.Join(", ", exactMatches.Select(m => m.AccountCode)));
            }

            string matchedBy = TextUtil.EqualsTrimmedIgnoreCase(exactMatches[0].CompanyName, companyName)
                ? "Company Name"
                : TextUtil.RelaxedCompanyNameEquals(exactMatches[0].CompanyName, companyName)
                    ? "Relaxed Company Name"
                    : "Website Root Domain";

            return exactMatches[0] with
            {
                Message = $"Organization account match found by {matchedBy} + Country + Market Segment."
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiWriteResult> CreateOrganizationAccountAsync(
        string companyName,
        string marketSegmentMajor,
        string country,
        IReadOnlyDictionary<string, string> accountFields,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var model = new AllAccountsModel();
            SetRequired(model, _fields.Organization, _config.OrgCode);
            SetRequired(model, _fields.Class, _fields.AccountClassValue);

            // Send all account/company fields from Columns A:U using Row 1 API headers.
            // This is what populates Website, Type, AccountRep, Email, Phone, Address, City, State, Postal, Country, etc.
            var fieldsToEvaluate = new Dictionary<string, string>(accountFields, StringComparer.OrdinalIgnoreCase);
            if (_config.ImportId.HasImportId && _config.ImportId.ApplyToCreatedOrganizationAccounts)
            {
                string keywordField = TextUtil.Clean(_config.ImportId.KeywordField);
                if (!string.IsNullOrWhiteSpace(keywordField))
                {
                    fieldsToEvaluate[keywordField] = TextUtil.Clean(_config.ImportId.Value);
                }
            }

            foreach (var kvp in fieldsToEvaluate)
            {
                string apiField = TextUtil.Clean(kvp.Key);
                string value = TextUtil.Clean(kvp.Value);
                if (string.IsNullOrWhiteSpace(apiField) || string.IsNullOrWhiteSpace(value)) continue;
                if (string.Equals(apiField, "na", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(apiField, _fields.AccountCode, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(apiField, "AccountCode", StringComparison.OrdinalIgnoreCase)) continue;

                if (string.Equals(apiField, _fields.Country, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(apiField, "Country", StringComparison.OrdinalIgnoreCase))
                {
                    value = _config.CleanCountryForMomentus(value);
                }

                SetRequired(model, apiField, value);
            }

            // Also set the configured canonical fields. These are fallback/required values,
            // and they help if the template uses Company while the SDK also exposes Name.
            SetRequired(model, _fields.AccountName, TextUtil.CleanKeyField(companyName));
            // Market Segment Major/Minor are direct fields on AllAccountsModel in this tenant.
            // Major is also passed separately because it is part of the duplicate-check key.
            if (!string.IsNullOrWhiteSpace(TextUtil.Clean(marketSegmentMajor)))
            {
                SetRequired(model, _fields.MarketSegmentMajor, TextUtil.Clean(marketSegmentMajor));
            }

            SetRequired(model, _fields.Country, _config.CleanCountryForMomentus(country));
            ApplyImportIdToNewAccountModel(model, isOrganizationAccount: true);

            ApplyRunTagToNewAccountModel(model, isOrganizationAccount: true);

            object added = await InvokeAccountsEndpointAsync("AddAsync", "Add", model, cancellationToken).ConfigureAwait(false);
            string accountCode = GetString(added, _fields.AccountCode, "AccountCode", "Code");

            if (string.IsNullOrWhiteSpace(accountCode))
            {
                return ApiWriteResult.Failed(
                    "Momentus account creation returned no Account Code. Check SDK model mapping and response object.",
                    "Account create response did not include Account Code.");
            }

            string tagMessage = _config.Tagging.HasRunTag && _config.Tagging.ApplyToCreatedOrganizationAccounts
                ? $" Run tag written to {_config.Tagging.UserTextProperty}."
                : string.Empty;

            return ApiWriteResult.Succeeded(accountCode, "Organization account created." + tagMessage);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiWriteResult> UpdateBlankOrganizationAccountFieldsAsync(
        string accountCode,
        IReadOnlyDictionary<string, string> accountFields,
        CancellationToken cancellationToken)
    {
        if (!_config.ExistingAccountUpdates.UpdateBlankFieldsOnly || !_config.ExistingAccountUpdates.EnabledInLiveMode)
        {
            return ApiWriteResult.Succeeded(accountCode, "Existing-account blank-field updates are disabled in appsettings.json.");
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanAccountCode = TextUtil.CleanKeyField(accountCode);
            if (string.IsNullOrWhiteSpace(cleanAccountCode))
            {
                return ApiWriteResult.Failed("Cannot update existing account because AccountCode is blank.");
            }

            object endpoint = GetEndpoint("Accounts");
            object? existing = await InvokeEndpointAsync(endpoint, "GetAsync", "Get", new object?[] { _config.OrgCode, cleanAccountCode, null }, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                return ApiWriteResult.Failed($"Could not retrieve existing organization account {cleanAccountCode} before blank-field update.");
            }

            var updatedFields = new List<string>();
            var skippedNonBlankFields = new List<string>();
            var skippedNotAllowedFields = new List<string>();

            var fieldsToEvaluate = new Dictionary<string, string>(accountFields, StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in fieldsToEvaluate)
            {
                string apiField = TextUtil.Clean(kvp.Key);
                string importValue = TextUtil.Clean(kvp.Value);

                if (string.IsNullOrWhiteSpace(apiField) || string.IsNullOrWhiteSpace(importValue)) continue;
                if (string.Equals(apiField, "na", StringComparison.OrdinalIgnoreCase)) continue;

                if (string.Equals(apiField, _fields.AccountCode, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(apiField, "AccountCode", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(apiField, _fields.Class, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(apiField, "Class", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(apiField, _fields.Organization, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(apiField, "Organization", StringComparison.OrdinalIgnoreCase))
                {
                    skippedNotAllowedFields.Add(apiField);
                    continue;
                }

                if (!IsExistingAccountUpdateFieldAllowed(apiField))
                {
                    skippedNotAllowedFields.Add(apiField);
                    continue;
                }

                if (string.Equals(apiField, _fields.Country, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(apiField, "Country", StringComparison.OrdinalIgnoreCase))
                {
                    importValue = _config.CleanCountryForMomentus(importValue);
                }

                string currentValue = GetString(existing, apiField);
                if (!string.IsNullOrWhiteSpace(TextUtil.Clean(currentValue)))
                {
                    skippedNonBlankFields.Add(apiField);
                    continue;
                }

                SetRequired(existing, apiField, importValue);
                updatedFields.Add(apiField);
            }

            if (updatedFields.Count == 0)
            {
                return ApiWriteResult.Succeeded(
                    cleanAccountCode,
                    "No blank existing organization account fields needed updating. Nonblank Momentus values were preserved.");
            }

            if (_config.DryRun)
            {
                return ApiWriteResult.Succeeded(
                    cleanAccountCode,
                    "DRY_RUN: would fill blank existing organization account fields: " + string.Join(", ", updatedFields.Distinct(StringComparer.OrdinalIgnoreCase)));
            }

            await InvokeEndpointAsync(endpoint, "UpdateAsync", "Update", new object?[] { existing, null }, cancellationToken)
                .ConfigureAwait(false);

            return ApiWriteResult.Succeeded(
                cleanAccountCode,
                "Filled blank existing organization account fields from import file: " + string.Join(", ", updatedFields.Distinct(StringComparer.OrdinalIgnoreCase)) + ". Nonblank Momentus values were preserved.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiWriteResult> CreateContactAsync(
        string parentAccountCode,
        string parentCompanyName,
        IReadOnlyDictionary<string, string> contactFields,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(parentAccountCode))
                return ApiWriteResult.Failed("Cannot create a contact without a parent Account Code.");

            string contactCodeMode = TextUtil.Clean(_config.CodeGeneration.ContactAccountCodeMode);
            bool generateContactCode = string.Equals(contactCodeMode, "GenerateFromSeed", StringComparison.OrdinalIgnoreCase);

            if (generateContactCode)
            {
                return await CreateContactWithGeneratedCodeAsync(parentAccountCode, parentCompanyName, contactFields, cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                return await CreateContactInternalAsync(
                    parentAccountCode,
                    parentCompanyName,
                    contactFields,
                    contactAccountCode: null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (LooksLikeDuplicateCodeError(ex))
            {
                // Some Momentus tenants require AccountCode on P/individual records and reject blank/auto codes.
                // If Auto mode fails with a duplicate/code message, retry safely with generated unique codes.
                return await CreateContactWithGeneratedCodeAsync(parentAccountCode, parentCompanyName, contactFields, cancellationToken)
                    .ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiWriteResult> CreateRelationshipAsync(
        string companyAccountCode,
        string contactAccountCode,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanCompanyCode = TextUtil.CleanKeyField(companyAccountCode);
            string cleanContactCode = TextUtil.CleanKeyField(contactAccountCode);

            if (!_config.Relationship.CreateRelationshipAfterContact)
            {
                return ApiWriteResult.Succeeded(cleanContactCode, "Relationship creation disabled in appsettings.json.");
            }

            if (!_config.Relationship.HasUsableRelationshipType)
            {
                return ApiWriteResult.Failed(
                    "RelationshipType is not configured. Set Relationship.RelationshipType in appsettings.json to the EV876 relationship type code.",
                    "Relationship not created because RelationshipType is missing or still set to the placeholder.");
            }

            if (string.IsNullOrWhiteSpace(cleanCompanyCode) || string.IsNullOrWhiteSpace(cleanContactCode))
            {
                return ApiWriteResult.Failed("Cannot create relationship without both company AccountCode and contact AccountCode.");
            }

            var relationship = new RelationshipsModel
            {
                MasterOrganizationCode = _config.OrgCode,
                MasterAccountCode = cleanCompanyCode,
                SubordinateOrganizationCode = _config.OrgCode,
                SubordinateAccountCode = cleanContactCode,
                RelationshipType = TextUtil.Clean(_config.Relationship.RelationshipType),
                EventSalesDesignation = TextUtil.Clean(_config.Relationship.EventSalesDesignation)
            };

            object endpoint = GetEndpoint("Relationships");
            await InvokeEndpointAsync(endpoint, "AddAsync", "Add", new object?[] { relationship, null }, cancellationToken)
                .ConfigureAwait(false);

            return ApiWriteResult.Succeeded(
                cleanContactCode,
                $"Relationship created. MasterAccountCode: {cleanCompanyCode}; SubordinateAccountCode: {cleanContactCode}; RelationshipType: {TextUtil.Clean(_config.Relationship.RelationshipType)}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiWriteResult> AddAccountAffiliationAsync(
        string accountCode,
        string affiliationCode,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanAccountCode = TextUtil.CleanKeyField(accountCode);
            string cleanAffiliationCode = TextUtil.CleanKeyField(affiliationCode);

            if (string.IsNullOrWhiteSpace(cleanAccountCode))
                return ApiWriteResult.Failed("Cannot add affiliation because AccountCode is blank.");

            if (string.IsNullOrWhiteSpace(cleanAffiliationCode))
                return ApiWriteResult.Failed("Cannot add affiliation because the affiliation code is blank.");

            object endpoint = GetEndpoint("AccountAffiliations");

            try
            {
                object? existing = await InvokeEndpointAsync(endpoint, "GetAsync", "Get", new object?[] { _config.OrgCode, cleanAccountCode, cleanAffiliationCode, null }, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is not null)
                {
                    return ApiWriteResult.Succeeded(cleanAccountCode, $"Affiliation/interest code '{cleanAffiliationCode}' already exists on AccountCode {cleanAccountCode}; add skipped.");
                }
            }
            catch
            {
                // If the GET fails because the affiliation does not exist, continue with Add.
                // Other add-time validation errors will still be logged below.
            }

            var affiliation = new AccountAffiliationsModel
            {
                OrganizationCode = _config.OrgCode,
                AccountCode = cleanAccountCode,
                AffiliationCode = cleanAffiliationCode
            };

            try
            {
                await InvokeEndpointAsync(endpoint, "AddAsync", "Add", new object?[] { affiliation, null }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (LooksLikeExistingAffiliationError(ex))
            {
                return ApiWriteResult.Succeeded(cleanAccountCode, $"Affiliation/interest code '{cleanAffiliationCode}' already exists on AccountCode {cleanAccountCode}; add skipped.");
            }

            return ApiWriteResult.Succeeded(cleanAccountCode, $"Affiliation/interest code '{cleanAffiliationCode}' added to AccountCode {cleanAccountCode}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiWriteResult> ApplyImportIdToAccountAsync(
        string accountCode,
        CancellationToken cancellationToken)
    {
        if (!_config.ImportId.HasImportId)
        {
            return ApiWriteResult.Succeeded(accountCode, "No Import ID entered; Keyword update skipped.");
        }

        string keywordField = TextUtil.Clean(_config.ImportId.KeywordField);
        string importId = TextUtil.Clean(_config.ImportId.Value);

        if (string.IsNullOrWhiteSpace(keywordField) || string.IsNullOrWhiteSpace(importId))
        {
            return ApiWriteResult.Succeeded(accountCode, "Import ID or Keyword field name is blank; Keyword update skipped.");
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanAccountCode = TextUtil.CleanKeyField(accountCode);
            if (string.IsNullOrWhiteSpace(cleanAccountCode))
            {
                return ApiWriteResult.Failed("Cannot apply Import ID because AccountCode is blank.");
            }

            if (_config.DryRun)
            {
                return ApiWriteResult.Succeeded(cleanAccountCode, $"DRY_RUN: would write Import ID '{importId}' to {keywordField}.");
            }

            object endpoint = GetEndpoint("Accounts");
            object? existing = await InvokeEndpointAsync(endpoint, "GetAsync", "Get", new object?[] { _config.OrgCode, cleanAccountCode, null }, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                return ApiWriteResult.Failed($"Could not retrieve AccountCode {cleanAccountCode} before applying Import ID to {keywordField}.");
            }

            string currentValue = GetString(existing, keywordField);
            if (!string.IsNullOrWhiteSpace(currentValue) && !_config.ImportId.OverwriteExistingKeyword &&
                !TextUtil.EqualsTrimmedIgnoreCase(currentValue, importId))
            {
                return ApiWriteResult.Succeeded(cleanAccountCode, $"{keywordField} already has a value; Import ID not overwritten.");
            }

            SetRequired(existing, keywordField, importId);

            await InvokeEndpointAsync(endpoint, "UpdateAsync", "Update", new object?[] { existing, null }, cancellationToken)
                .ConfigureAwait(false);

            string action = TextUtil.EqualsTrimmedIgnoreCase(currentValue, importId)
                ? "confirmed"
                : string.IsNullOrWhiteSpace(currentValue)
                    ? "written"
                    : "overwritten";

            return ApiWriteResult.Succeeded(cleanAccountCode, $"Import ID '{importId}' {action} in {keywordField}.");
        }, cancellationToken).ConfigureAwait(false);
    }


    public async Task<ApiWriteResult> ApplyRunTagToAccountAsync(
        string accountCode,
        bool isOrganizationAccount,
        CancellationToken cancellationToken)
    {
        if (!_config.Tagging.HasRunTag)
        {
            return ApiWriteResult.Succeeded(accountCode, "No run tag entered; UserText02 tagging skipped.");
        }

        if (isOrganizationAccount && !_config.Tagging.ApplyToMatchedExistingAccounts)
        {
            return ApiWriteResult.Succeeded(accountCode, "Run tag is not configured to apply to matched existing organization accounts.");
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanAccountCode = TextUtil.CleanKeyField(accountCode);
            if (string.IsNullOrWhiteSpace(cleanAccountCode))
            {
                return ApiWriteResult.Failed("Cannot apply run tag because AccountCode is blank.");
            }

            if (_config.DryRun)
            {
                return ApiWriteResult.Succeeded(cleanAccountCode, $"DRY_RUN: would write run tag '{TextUtil.Clean(_config.Tagging.RunTagValue)}' to {_config.Tagging.UserTextProperty}.");
            }

            object endpoint = GetEndpoint("Accounts");
            object? existing = await InvokeEndpointAsync(endpoint, "GetAsync", "Get", new object?[] { _config.OrgCode, cleanAccountCode, null }, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not AllAccountsModel accountModel)
            {
                return ApiWriteResult.Failed($"Could not retrieve AccountCode {cleanAccountCode} as an AllAccountsModel before applying the run tag.");
            }

            string currentValue = GetRunTagCurrentValue(accountModel, isOrganizationAccount);
            if (!string.IsNullOrWhiteSpace(currentValue) && !_config.Tagging.OverwriteExistingValue &&
                !TextUtil.EqualsTrimmedIgnoreCase(currentValue, _config.Tagging.RunTagValue))
            {
                return ApiWriteResult.Succeeded(cleanAccountCode, $"{_config.Tagging.UserTextProperty} already has a value; run tag not overwritten.");
            }

            SetRunTagOnAccountModel(accountModel, isOrganizationAccount);

            await InvokeEndpointAsync(endpoint, "UpdateAsync", "Update", new object?[] { accountModel, null }, cancellationToken)
                .ConfigureAwait(false);

            return ApiWriteResult.Succeeded(cleanAccountCode, $"Run tag '{TextUtil.Clean(_config.Tagging.RunTagValue)}' written to {_config.Tagging.UserTextProperty}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MomentusAccountSnapshot>> SearchActiveParentAccountsAsync(int maxResults, CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            int safeMax = Math.Max(1, maxResults);
            string odata =
                $"{_fields.Class} eq '{ODataString(_fields.AccountClassValue)}' and " +
                "(EventSalesStatus eq 'A' or EventSalesStatus eq 'P')";

            var accounts = await SearchAccountsAsync(odata, cancellationToken, safeMax).ConfigureAwait(false);

            return accounts
                .Select(ToAccountSnapshot)
                .Where(a => !string.IsNullOrWhiteSpace(a.AccountCode))
                .Take(safeMax)
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MomentusAccountSnapshot>> SearchActiveParentAccountsByAccountCodeRangeAsync(
        string startAccountCode,
        string endAccountCode,
        int maxResults,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            int safeMax = Math.Max(1, maxResults);
            string start = TextUtil.CleanKeyField(startAccountCode);
            string end = TextUtil.CleanKeyField(endAccountCode);

            List<object> accounts = await SearchAccountsByAccountCodeRangeInSlicesAsync(start, end, safeMax, cancellationToken)
                .ConfigureAwait(false);

            return accounts
                .Select(ToAccountSnapshot)
                .Where(a => !string.IsNullOrWhiteSpace(a.AccountCode))
                .OrderBy(a => a.AccountCode, StringComparer.OrdinalIgnoreCase)
                .Take(safeMax)
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<object>> SearchAccountsByAccountCodeRangeInSlicesAsync(
        string startAccountCode,
        string endAccountCode,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(startAccountCode, out long start) || !long.TryParse(endAccountCode, out long end))
        {
            string odata =
                $"{_fields.Class} eq '{ODataString(_fields.AccountClassValue)}' and " +
                "(EventSalesStatus eq 'A' or EventSalesStatus eq 'P') and " +
                $"{_fields.AccountCode} ge '{ODataString(startAccountCode)}' and " +
                $"{_fields.AccountCode} le '{ODataString(endAccountCode)}'";

            return await SearchAccountsAsync(odata, cancellationToken, maxResults).ConfigureAwait(false);
        }

        int width = Math.Max(startAccountCode.Length, endAccountCode.Length);
        const int apiSafeSliceSize = 1000;
        var byAccountCode = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        for (long sliceStart = start; sliceStart <= end && byAccountCode.Count < maxResults; sliceStart += apiSafeSliceSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long sliceEnd = Math.Min(end, sliceStart + apiSafeSliceSize - 1);
            string sliceStartCode = sliceStart.ToString().PadLeft(width, '0');
            string sliceEndCode = sliceEnd.ToString().PadLeft(width, '0');

            string odata =
                $"{_fields.Class} eq '{ODataString(_fields.AccountClassValue)}' and " +
                "(EventSalesStatus eq 'A' or EventSalesStatus eq 'P') and " +
                $"{_fields.AccountCode} ge '{ODataString(sliceStartCode)}' and " +
                $"{_fields.AccountCode} le '{ODataString(sliceEndCode)}'";

            List<object> sliceAccounts = await SearchAccountsAsync(odata, cancellationToken, apiSafeSliceSize)
                .ConfigureAwait(false);

            foreach (object account in sliceAccounts)
            {
                string accountCode = GetString(account, _fields.AccountCode, "AccountCode", "Code");
                if (string.IsNullOrWhiteSpace(accountCode)) continue;
                byAccountCode[accountCode] = account;
            }
        }

        return byAccountCode
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(pair => pair.Value)
            .ToList();
    }

    public async Task<IReadOnlyList<MomentusContactSnapshot>> GetContactsForParentAccountAsync(string parentAccountCode, CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanParent = TextUtil.CleanKeyField(parentAccountCode);
            if (string.IsNullOrWhiteSpace(cleanParent)) return new List<MomentusContactSnapshot>();

            string odata =
                $"{_fields.Class} eq '{ODataString(_fields.ContactClassValue)}' and " +
                $"{_fields.ParentAccountCode} eq '{ODataString(cleanParent)}' and " +
                "(EventSalesStatus eq 'A' or EventSalesStatus eq 'P')";

            var contacts = await SearchAccountsAsync(odata, cancellationToken, maxResults: 10000).ConfigureAwait(false);
            var allContacts = contacts
                .Select(ToContactSnapshot)
                .Where(c => !string.IsNullOrWhiteSpace(c.AccountCode))
                .ToDictionary(c => c.AccountCode, StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<string> relatedContactCodes;
            try
            {
                relatedContactCodes = await GetRelationshipContactCodesAsync(cleanParent, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Relationship search can fail on some legacy/stale relationship data.
                // Keep the direct PrimaryAccount contacts instead of dropping the whole account from planning.
                relatedContactCodes = Array.Empty<string>();
            }

            foreach (string relatedContactCode in relatedContactCodes)
            {
                if (allContacts.ContainsKey(relatedContactCode)) continue;

                MomentusContactSnapshot? relatedContact = await GetContactSnapshotByAccountCodeAsync(relatedContactCode, cancellationToken)
                    .ConfigureAwait(false);

                if (relatedContact is not null && !string.IsNullOrWhiteSpace(relatedContact.AccountCode))
                {
                    allContacts[relatedContact.AccountCode] = relatedContact;
                }
            }

            return allContacts.Values
                .OrderBy(c => c.LastName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.FirstName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.AccountCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetAccountAffiliationCodesAsync(string accountCode, CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanAccountCode = TextUtil.CleanKeyField(accountCode);
            if (string.IsNullOrWhiteSpace(cleanAccountCode)) return new List<string>();

            object endpoint = GetEndpoint("AccountAffiliations");
            string odata = $"{nameof(AccountAffiliationsModel.AccountCode)} eq '{ODataString(cleanAccountCode)}'";

            object? searchOptions = CreateSearchOptions(10000);
            object? response = await InvokeEndpointAsync(endpoint, "SearchAsync", "Search", new object?[] { _config.OrgCode, odata, searchOptions }, cancellationToken)
                .ConfigureAwait(false);

            return ExtractResponseItems(response)
                .Select(item => GetString(item, "AffiliationCode", "Code", "InterestCode"))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetCompanyRelationshipCodesForContactAsync(string contactAccountCode, CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync<IReadOnlyList<string>>(async () =>
        {
            string cleanContact = TextUtil.CleanKeyField(contactAccountCode);
            if (string.IsNullOrWhiteSpace(cleanContact)) return Array.Empty<string>();

            object endpoint = GetEndpoint("Relationships");
            string relationshipType = TextUtil.CleanKeyField(_config.Relationship.RelationshipType);
            string odata =
                $"SubordinateAccountCode eq '{ODataString(cleanContact)}' and " +
                $"RelationshipType eq '{ODataString(relationshipType)}'";

            object? searchOptions = CreateSearchOptions(10000);
            object? response = await InvokeEndpointAsync(endpoint, "SearchAsync", "Search", new object?[] { _config.OrgCode, odata, searchOptions }, cancellationToken)
                .ConfigureAwait(false);

            return ExtractResponseItems(response)
                .Where(item => TextUtil.EqualsTrimmedIgnoreCase(GetString(item, "SubordinateAccountCode"), cleanContact))
                .Where(item => TextUtil.EqualsTrimmedIgnoreCase(GetString(item, "RelationshipType"), relationshipType))
                .Select(item => GetString(item, "MasterAccountCode"))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> GetRelationshipContactCodesAsync(string parentAccountCode, CancellationToken cancellationToken)
    {
        string cleanParent = TextUtil.CleanKeyField(parentAccountCode);
        if (string.IsNullOrWhiteSpace(cleanParent)) return Array.Empty<string>();

        object endpoint = GetEndpoint("Relationships");
        string relationshipType = TextUtil.CleanKeyField(_config.Relationship.RelationshipType);
        string odata =
            $"MasterAccountCode eq '{ODataString(cleanParent)}' and " +
            $"RelationshipType eq '{ODataString(relationshipType)}'";

        object? searchOptions = CreateSearchOptions(10000);
        object? response = await InvokeEndpointAsync(endpoint, "SearchAsync", "Search", new object?[] { _config.OrgCode, odata, searchOptions }, cancellationToken)
            .ConfigureAwait(false);

        return ExtractResponseItems(response)
            .Where(item => TextUtil.EqualsTrimmedIgnoreCase(GetString(item, "MasterAccountCode"), cleanParent))
            .Where(item => TextUtil.EqualsTrimmedIgnoreCase(GetString(item, "RelationshipType"), relationshipType))
            .Where(item => !TextUtil.EqualsTrimmedIgnoreCase(GetString(item, "EventSalesDesignation"), "I"))
            .Select(item => GetString(item, "SubordinateAccountCode"))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<MomentusContactSnapshot?> GetContactSnapshotByAccountCodeAsync(string contactAccountCode, CancellationToken cancellationToken)
    {
        string cleanContactCode = TextUtil.CleanKeyField(contactAccountCode);
        if (string.IsNullOrWhiteSpace(cleanContactCode)) return null;

        object? existing;
        try
        {
            object endpoint = GetEndpoint("Accounts");
            existing = await InvokeEndpointAsync(endpoint, "GetAsync", "Get", new object?[] { _config.OrgCode, cleanContactCode, null }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Some legacy relationship rows can point at account codes that no longer exist.
            // Treat those as stale relationship noise instead of failing the whole planning run.
            return null;
        }

        if (existing is null) return null;

        string classValue = GetString(existing, _fields.Class, "Class", "AccountClass", "ClassCode");
        if (!TextUtil.EqualsTrimmedIgnoreCase(classValue, _fields.ContactClassValue)) return null;

        return ToContactSnapshot(existing);
    }

    public async Task<AccountLookupResult> FindSegmentAccountAsync(
        string companyName,
        string marketSegmentMajor,
        string marketSegmentMinor,
        string country,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string odata =
                $"{_fields.Class} eq '{ODataString(_fields.AccountClassValue)}' and " +
                $"{_fields.AccountName} eq '{ODataString(TextUtil.CleanKeyField(companyName))}'";

            var candidates = await SearchAccountsAsync(odata, cancellationToken, maxResults: 1000).ConfigureAwait(false);
            string cleanCountry = _config.CleanCountryForMomentus(country);

            var matches = candidates
                .Where(model => TextUtil.EqualsTrimmedIgnoreCase(GetString(model, _fields.AccountName, "Name", "AccountName", "CompanyName"), companyName))
                .Where(model => TextUtil.EqualsTrimmedIgnoreCase(GetString(model, _fields.MarketSegmentMajor, "MarketSegmentMajor", "MarketSegment", "Market"), marketSegmentMajor))
                .Where(model => string.IsNullOrWhiteSpace(TextUtil.Clean(marketSegmentMinor)) ||
                                TextUtil.EqualsTrimmedIgnoreCase(GetString(model, _fields.MarketSegmentMinor, "MarketSegmentMinor"), marketSegmentMinor))
                .Where(model => string.IsNullOrWhiteSpace(cleanCountry) ||
                                TextUtil.EqualsTrimmedIgnoreCase(GetString(model, _fields.Country, "Country", "CountryCode"), cleanCountry) ||
                                TextUtil.EqualsTrimmedIgnoreCase(_config.CleanCountryForMomentus(GetString(model, _fields.Country, "Country", "CountryCode")), cleanCountry))
                .Select(ToAccountLookupResult)
                .ToList();

            if (matches.Count == 0) return AccountLookupResult.NotFound("No existing segment account found.");
            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    "Multiple existing segment accounts matched. Matching account codes: " +
                    string.Join(", ", matches.Select(m => m.AccountCode)));
            }

            return matches[0] with { Message = "Existing segment account found." };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiWriteResult> UpdateAccountMarketSegmentAsync(
        string accountCode,
        string marketSegmentMajor,
        string marketSegmentMinor,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanAccountCode = TextUtil.CleanKeyField(accountCode);
            if (string.IsNullOrWhiteSpace(cleanAccountCode))
                return ApiWriteResult.Failed("Cannot update market segment because AccountCode is blank.");

            if (_config.DryRun)
            {
                return ApiWriteResult.Succeeded(
                    cleanAccountCode,
                    $"DRY_RUN: would update market segment to Major='{TextUtil.Clean(marketSegmentMajor)}', Minor='{TextUtil.Clean(marketSegmentMinor)}'.");
            }

            object endpoint = GetEndpoint("Accounts");
            object? existing = await InvokeEndpointAsync(endpoint, "GetAsync", "Get", new object?[] { _config.OrgCode, cleanAccountCode, null }, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
                return ApiWriteResult.Failed($"Could not retrieve AccountCode {cleanAccountCode} before market-segment update.");

            SetRequired(existing, _fields.MarketSegmentMajor, TextUtil.Clean(marketSegmentMajor));
            if (!string.IsNullOrWhiteSpace(TextUtil.Clean(marketSegmentMinor)))
            {
                SetRequired(existing, _fields.MarketSegmentMinor, TextUtil.Clean(marketSegmentMinor));
            }

            await InvokeEndpointAsync(endpoint, "UpdateAsync", "Update", new object?[] { existing, null }, cancellationToken)
                .ConfigureAwait(false);

            return ApiWriteResult.Succeeded(cleanAccountCode, $"Market segment updated to Major='{TextUtil.Clean(marketSegmentMajor)}', Minor='{TextUtil.Clean(marketSegmentMinor)}'.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiWriteResult> CreateSegmentAccountCopyAsync(
        string sourceAccountCode,
        string marketSegmentMajor,
        string marketSegmentMinor,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanSourceCode = TextUtil.CleanKeyField(sourceAccountCode);
            if (string.IsNullOrWhiteSpace(cleanSourceCode))
                return ApiWriteResult.Failed("Cannot create segment account copy because source AccountCode is blank.");

            object endpoint = GetEndpoint("Accounts");
            object? existing = await InvokeEndpointAsync(endpoint, "GetAsync", "Get", new object?[] { _config.OrgCode, cleanSourceCode, null }, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not AllAccountsModel source)
                return ApiWriteResult.Failed($"Could not retrieve source AccountCode {cleanSourceCode} as an account model.");

            string sourceName = GetString(source, _fields.AccountName, "Name", "AccountName", "CompanyName");
            string sourceCountry = GetString(source, _fields.Country, "Country", "CountryCode");
            AccountLookupResult alreadyExists = await FindSegmentAccountAsync(
                sourceName,
                marketSegmentMajor,
                marketSegmentMinor,
                sourceCountry,
                cancellationToken).ConfigureAwait(false);

            if (alreadyExists.Found)
            {
                return ApiWriteResult.Succeeded(
                    alreadyExists.AccountCode,
                    $"Segment account already exists for {sourceName} with Major='{TextUtil.Clean(marketSegmentMajor)}', Minor='{TextUtil.Clean(marketSegmentMinor)}'; creation skipped.");
            }

            if (_config.DryRun)
            {
                return ApiWriteResult.Succeeded(
                    $"DRYRUN-{TextUtil.Clean(marketSegmentMajor)}-{TextUtil.Clean(marketSegmentMinor)}",
                    $"DRY_RUN: would copy AccountCode {cleanSourceCode} ({sourceName}) with Major='{TextUtil.Clean(marketSegmentMajor)}', Minor='{TextUtil.Clean(marketSegmentMinor)}'.");
            }

            var copy = new AllAccountsModel();
            CopySimpleWritableAccountFields(source, copy);
            SetRequired(copy, _fields.Organization, _config.OrgCode);
            SetRequired(copy, _fields.Class, _fields.AccountClassValue);
            SetRequired(copy, _fields.AccountName, sourceName);
            SetRequired(copy, _fields.MarketSegmentMajor, TextUtil.Clean(marketSegmentMajor));
            if (!string.IsNullOrWhiteSpace(TextUtil.Clean(marketSegmentMinor)))
            {
                SetRequired(copy, _fields.MarketSegmentMinor, TextUtil.Clean(marketSegmentMinor));
            }

            object added = await InvokeAccountsEndpointAsync("AddAsync", "Add", copy, cancellationToken).ConfigureAwait(false);
            string newAccountCode = GetString(added, _fields.AccountCode, "AccountCode", "Code");

            if (string.IsNullOrWhiteSpace(newAccountCode))
            {
                return ApiWriteResult.Failed(
                    "Momentus segment account copy returned no Account Code.",
                    $"Segment account copy for {sourceName} was attempted but response did not expose AccountCode.");
            }

            return ApiWriteResult.Succeeded(newAccountCode, $"Segment account copy created from {cleanSourceCode} with Major='{TextUtil.Clean(marketSegmentMajor)}', Minor='{TextUtil.Clean(marketSegmentMinor)}'.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RelationshipExistsAsync(
        string companyAccountCode,
        string contactAccountCode,
        string relationshipType,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanCompanyCode = TextUtil.CleanKeyField(companyAccountCode);
            string cleanContactCode = TextUtil.CleanKeyField(contactAccountCode);
            string cleanRelationshipType = TextUtil.CleanKeyField(relationshipType);

            if (string.IsNullOrWhiteSpace(cleanCompanyCode) ||
                string.IsNullOrWhiteSpace(cleanContactCode) ||
                string.IsNullOrWhiteSpace(cleanRelationshipType))
            {
                return false;
            }

            object endpoint = GetEndpoint("Relationships");
            string odata =
                $"MasterAccountCode eq '{ODataString(cleanCompanyCode)}' and " +
                $"SubordinateAccountCode eq '{ODataString(cleanContactCode)}' and " +
                $"RelationshipType eq '{ODataString(cleanRelationshipType)}'";

            object? searchOptions = CreateSearchOptions(10);
            object? response = await InvokeEndpointAsync(endpoint, "SearchAsync", "Search", new object?[] { _config.OrgCode, odata, searchOptions }, cancellationToken)
                .ConfigureAwait(false);

            return ExtractResponseItems(response).Any(item =>
                TextUtil.EqualsTrimmedIgnoreCase(GetString(item, "MasterAccountCode"), cleanCompanyCode) &&
                TextUtil.EqualsTrimmedIgnoreCase(GetString(item, "SubordinateAccountCode"), cleanContactCode) &&
                TextUtil.EqualsTrimmedIgnoreCase(GetString(item, "RelationshipType"), cleanRelationshipType));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiWriteResult> DeleteRelationshipAsync(
        string companyAccountCode,
        string contactAccountCode,
        string relationshipType,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanCompanyCode = TextUtil.CleanKeyField(companyAccountCode);
            string cleanContactCode = TextUtil.CleanKeyField(contactAccountCode);
            string cleanRelationshipType = TextUtil.CleanKeyField(relationshipType);

            if (string.IsNullOrWhiteSpace(cleanCompanyCode) ||
                string.IsNullOrWhiteSpace(cleanContactCode) ||
                string.IsNullOrWhiteSpace(cleanRelationshipType))
            {
                return ApiWriteResult.Failed("Cannot delete relationship without company AccountCode, contact AccountCode, and RelationshipType.");
            }

            bool exists = await RelationshipExistsAsync(cleanCompanyCode, cleanContactCode, cleanRelationshipType, cancellationToken)
                .ConfigureAwait(false);
            if (!exists)
            {
                return ApiWriteResult.Succeeded(cleanContactCode, $"Relationship {cleanCompanyCode}->{cleanContactCode} ({cleanRelationshipType}) did not exist; delete skipped.");
            }

            object endpoint = GetEndpoint("Relationships");
            try
            {
                await InvokeEndpointAsync(endpoint, "DeleteAsync", "Delete", new object?[] { _config.OrgCode, cleanCompanyCode, cleanContactCode, cleanRelationshipType, null }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ApiWriteResult.Failed(
                    $"Could not delete relationship {cleanCompanyCode}->{cleanContactCode} ({cleanRelationshipType}).",
                    ex.GetBaseException().Message);
            }

            return ApiWriteResult.Succeeded(cleanContactCode, $"Deleted relationship {cleanCompanyCode}->{cleanContactCode} ({cleanRelationshipType}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiWriteResult> InactivateRelationshipAsync(
        string companyAccountCode,
        string contactAccountCode,
        string relationshipType,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanCompanyCode = TextUtil.CleanKeyField(companyAccountCode);
            string cleanContactCode = TextUtil.CleanKeyField(contactAccountCode);
            string cleanRelationshipType = TextUtil.CleanKeyField(relationshipType);

            if (string.IsNullOrWhiteSpace(cleanCompanyCode) ||
                string.IsNullOrWhiteSpace(cleanContactCode) ||
                string.IsNullOrWhiteSpace(cleanRelationshipType))
            {
                return ApiWriteResult.Failed("Cannot inactivate relationship without company AccountCode, contact AccountCode, and RelationshipType.");
            }

            object endpoint = GetEndpoint("Relationships");
            object? existing = await InvokeEndpointAsync(
                    endpoint,
                    "GetAsync",
                    "Get",
                    new object?[] { _config.OrgCode, cleanCompanyCode, cleanContactCode, cleanRelationshipType, null },
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                return ApiWriteResult.Succeeded(cleanContactCode, $"Relationship {cleanCompanyCode}->{cleanContactCode} ({cleanRelationshipType}) was not found; inactivation skipped.");
            }

            SetRequired(existing, "EventSalesDesignation", "I");

            await InvokeEndpointAsync(endpoint, "UpdateAsync", "Update", new object?[] { existing, null }, cancellationToken)
                .ConfigureAwait(false);

            return ApiWriteResult.Succeeded(cleanContactCode, $"Relationship {cleanCompanyCode}->{cleanContactCode} ({cleanRelationshipType}) EventSalesDesignation set to I.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiWriteResult> UpdateContactPrimaryAccountAsync(
        string contactAccountCode,
        string survivorAccountCode,
        string survivorCompanyName,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanContactCode = TextUtil.CleanKeyField(contactAccountCode);
            string cleanSurvivorCode = TextUtil.CleanKeyField(survivorAccountCode);

            if (string.IsNullOrWhiteSpace(cleanContactCode) || string.IsNullOrWhiteSpace(cleanSurvivorCode))
                return ApiWriteResult.Failed("Cannot update contact PrimaryAccount without both contact and survivor AccountCode.");

            if (_config.DryRun)
            {
                return ApiWriteResult.Succeeded(cleanContactCode, $"DRY_RUN: would set PrimaryAccount to {cleanSurvivorCode}.");
            }

            object endpoint = GetEndpoint("Accounts");
            object? existing = await InvokeEndpointAsync(endpoint, "GetAsync", "Get", new object?[] { _config.OrgCode, cleanContactCode, null }, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
                return ApiWriteResult.Failed($"Could not retrieve contact AccountCode {cleanContactCode} before PrimaryAccount update.");

            SetRequired(existing, _fields.ParentAccountCode, cleanSurvivorCode);
            if (!string.IsNullOrWhiteSpace(_fields.ParentCompanyField) &&
                !string.Equals(_fields.ParentCompanyField, _fields.ParentAccountCode, StringComparison.OrdinalIgnoreCase))
            {
                SetRequired(existing, _fields.ParentCompanyField, TextUtil.Clean(survivorCompanyName));
            }

            await InvokeEndpointAsync(endpoint, "UpdateAsync", "Update", new object?[] { existing, null }, cancellationToken)
                .ConfigureAwait(false);

            return ApiWriteResult.Succeeded(cleanContactCode, $"Contact PrimaryAccount updated to {cleanSurvivorCode}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiWriteResult> CopyBlankAccountFieldsAsync(
        string sourceAccountCode,
        string targetAccountCode,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanSourceCode = TextUtil.CleanKeyField(sourceAccountCode);
            string cleanTargetCode = TextUtil.CleanKeyField(targetAccountCode);

            if (string.IsNullOrWhiteSpace(cleanSourceCode) || string.IsNullOrWhiteSpace(cleanTargetCode))
                return ApiWriteResult.Failed("Cannot copy blank account fields without both source and target AccountCode.");

            if (TextUtil.EqualsTrimmedIgnoreCase(cleanSourceCode, cleanTargetCode))
                return ApiWriteResult.Succeeded(cleanTargetCode, "Source and target are the same account; blank-field copy skipped.");

            object endpoint = GetEndpoint("Accounts");
            object? source = await InvokeEndpointAsync(endpoint, "GetAsync", "Get", new object?[] { _config.OrgCode, cleanSourceCode, null }, cancellationToken)
                .ConfigureAwait(false);
            object? target = await InvokeEndpointAsync(endpoint, "GetAsync", "Get", new object?[] { _config.OrgCode, cleanTargetCode, null }, cancellationToken)
                .ConfigureAwait(false);

            if (source is null)
                return ApiWriteResult.Failed($"Could not retrieve source duplicate account {cleanSourceCode}.");
            if (target is null)
                return ApiWriteResult.Failed($"Could not retrieve target survivor account {cleanTargetCode}.");

            var copiedFields = new List<string>();
            foreach (string rawField in _config.ExistingAccountUpdates.AllowedFields)
            {
                string field = TextUtil.Clean(rawField);
                if (string.IsNullOrWhiteSpace(field)) continue;

                string sourceValue = GetString(source, field);
                if (string.IsNullOrWhiteSpace(sourceValue)) continue;

                string targetValue = GetString(target, field);
                if (!string.IsNullOrWhiteSpace(targetValue)) continue;

                SetRequired(target, field, sourceValue);
                copiedFields.Add(field);
            }

            if (copiedFields.Count == 0)
                return ApiWriteResult.Succeeded(cleanTargetCode, $"No blank survivor fields needed values from duplicate account {cleanSourceCode}.");

            if (_config.DryRun)
            {
                return ApiWriteResult.Succeeded(
                    cleanTargetCode,
                    $"DRY_RUN: would copy blank survivor fields from {cleanSourceCode}: {string.Join(", ", copiedFields.Distinct(StringComparer.OrdinalIgnoreCase))}.");
            }

            await InvokeEndpointAsync(endpoint, "UpdateAsync", "Update", new object?[] { target, null }, cancellationToken)
                .ConfigureAwait(false);

            return ApiWriteResult.Succeeded(
                cleanTargetCode,
                $"Copied blank survivor fields from duplicate account {cleanSourceCode}: {string.Join(", ", copiedFields.Distinct(StringComparer.OrdinalIgnoreCase))}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiWriteResult> UpdateAccountEventSalesStatusAsync(
        string accountCode,
        string eventSalesStatus,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            string cleanAccountCode = TextUtil.CleanKeyField(accountCode);
            string cleanStatus = TextUtil.CleanKeyField(eventSalesStatus);

            if (string.IsNullOrWhiteSpace(cleanAccountCode))
                return ApiWriteResult.Failed("Cannot update EventSalesStatus because AccountCode is blank.");
            if (string.IsNullOrWhiteSpace(cleanStatus))
                return ApiWriteResult.Failed("Cannot update EventSalesStatus because status is blank.");

            if (_config.DryRun)
            {
                return ApiWriteResult.Succeeded(cleanAccountCode, $"DRY_RUN: would set EventSalesStatus to '{cleanStatus}'.");
            }

            object endpoint = GetEndpoint("Accounts");
            object? existing = await InvokeEndpointAsync(endpoint, "GetAsync", "Get", new object?[] { _config.OrgCode, cleanAccountCode, null }, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
                return ApiWriteResult.Failed($"Could not retrieve AccountCode {cleanAccountCode} before EventSalesStatus update.");

            SetRequired(existing, "EventSalesStatus", cleanStatus);

            await InvokeEndpointAsync(endpoint, "UpdateAsync", "Update", new object?[] { existing, null }, cancellationToken)
                .ConfigureAwait(false);

            return ApiWriteResult.Succeeded(cleanAccountCode, $"EventSalesStatus set to '{cleanStatus}'.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApiWriteResult> CreateContactWithGeneratedCodeAsync(
        string parentAccountCode,
        string parentCompanyName,
        IReadOnlyDictionary<string, string> contactFields,
        CancellationToken cancellationToken)
    {
        int maxAttempts = Math.Max(1, _config.CodeGeneration.MaxCodeGenerationAttempts);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            string candidateCode = await GetNextUnusedContactAccountCodeAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await CreateContactInternalAsync(
                    parentAccountCode,
                    parentCompanyName,
                    contactFields,
                    candidateCode,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (LooksLikeDuplicateCodeError(ex))
            {
                // Another user/process may have used this code. Try the next candidate.
                continue;
            }
        }

        return ApiWriteResult.Failed(
            $"Could not generate an unused contact AccountCode after {maxAttempts} attempts. " +
            "Increase CodeGeneration.StartingContactAccountCode or MaxCodeGenerationAttempts.");
    }

    private async Task<ApiWriteResult> CreateContactInternalAsync(
        string parentAccountCode,
        string parentCompanyName,
        IReadOnlyDictionary<string, string> contactFields,
        string? contactAccountCode,
        CancellationToken cancellationToken)
    {
        var model = new AllAccountsModel();
        SetRequired(model, _fields.Organization, _config.OrgCode);
        SetRequired(model, _fields.Class, _fields.ContactClassValue);

        if (!string.IsNullOrWhiteSpace(contactAccountCode))
        {
            SetRequired(model, _fields.AccountCode, TextUtil.Clean(contactAccountCode));
        }

        // Parent organization/company link for the contact.
        // PrimaryAccount should hold the parent organization AccountCode.
        // Company is a display/company-name field in this tenant, so it should hold the company name, not the code.
        SetRequired(model, _fields.ParentAccountCode, TextUtil.Clean(parentAccountCode));
        if (!string.IsNullOrWhiteSpace(_fields.ParentCompanyField) &&
            !string.Equals(_fields.ParentCompanyField, _fields.ParentAccountCode, StringComparison.OrdinalIgnoreCase))
        {
            SetRequired(model, _fields.ParentCompanyField, TextUtil.Clean(parentCompanyName));
        }

        foreach (var kvp in contactFields)
        {
            string apiField = TextUtil.Clean(kvp.Key);
            string value = TextUtil.Clean(kvp.Value);
            if (string.IsNullOrWhiteSpace(apiField) || string.IsNullOrWhiteSpace(value)) continue;
            if (string.Equals(apiField, "na", StringComparison.OrdinalIgnoreCase)) continue;

            // Do not let Column V overwrite the generated/auto contact AccountCode.
            // The parent organization account code belongs in PrimaryAccount, not AccountCode.
            if (string.Equals(apiField, _fields.AccountCode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(apiField, "AccountCode", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Company is set from the row's Company Name above. Do not let a contact mapping
            // overwrite it with a code or blank/template value.
            if (string.Equals(apiField, _fields.ParentCompanyField, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(apiField, "Company", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SetRequired(model, apiField, value);
        }

        ApplyImportIdToNewAccountModel(model, isOrganizationAccount: false);

        ApplyRunTagToNewAccountModel(model, isOrganizationAccount: false);

        object added = await InvokeAccountsEndpointAsync("AddAsync", "Add", model, cancellationToken).ConfigureAwait(false);
        string returnedCode = GetString(added, _fields.AccountCode, "AccountCode", "Code");
        string contactCodeForMessage = string.IsNullOrWhiteSpace(returnedCode) ? TextUtil.Clean(contactAccountCode) : returnedCode;

        string message = string.IsNullOrWhiteSpace(contactCodeForMessage)
            ? $"Contact created under PrimaryAccount {parentAccountCode}; response did not expose contact AccountCode."
            : $"Contact created. Contact AccountCode: {contactCodeForMessage}; PrimaryAccount: {parentAccountCode}; Company: {TextUtil.Clean(parentCompanyName)}.";

        if (_config.Tagging.HasRunTag && _config.Tagging.ApplyToCreatedContacts)
        {
            message += $" Run tag written to {_config.Tagging.UserTextProperty}.";
        }

        return ApiWriteResult.Succeeded(contactCodeForMessage, message);
    }

    private async Task<string> GetNextUnusedContactAccountCodeAsync(CancellationToken cancellationToken)
    {
        for (int i = 0; i < Math.Max(1, _config.CodeGeneration.MaxCodeGenerationAttempts); i++)
        {
            int next = Interlocked.Increment(ref _nextGeneratedContactCode) - 1;
            string candidate = next.ToString();

            if (!await AccountCodeExistsAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to find an unused contact AccountCode candidate.");
    }

    private async Task<bool> AccountCodeExistsAsync(string accountCode, CancellationToken cancellationToken)
    {
        string cleanCode = TextUtil.CleanKeyField(accountCode);
        if (string.IsNullOrWhiteSpace(cleanCode)) return false;

        string odata = $"{_fields.AccountCode} eq '{ODataString(cleanCode)}'";
        var candidates = await SearchAccountsAsync(odata, cancellationToken, maxResults: 5).ConfigureAwait(false);

        return candidates.Any(model =>
            TextUtil.EqualsTrimmedIgnoreCase(GetString(model, _fields.AccountCode, "AccountCode", "Code"), cleanCode));
    }

    private static bool LooksLikeDuplicateCodeError(Exception ex)
    {
        string message = ex.ToString();
        return message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("change the code", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeExistingAffiliationError(Exception ex)
    {
        string message = ex.ToString();
        return message.Contains("chosen affiliation has already been added", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("affiliation has already been added", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsExistingAccountUpdateFieldAllowed(string apiField)
    {
        if (_config.ExistingAccountUpdates.AllowedFields is null || _config.ExistingAccountUpdates.AllowedFields.Count == 0)
        {
            return true;
        }

        return _config.ExistingAccountUpdates.AllowedFields
            .Any(allowed => string.Equals(TextUtil.Clean(allowed), apiField, StringComparison.OrdinalIgnoreCase));
    }


    private void ApplyImportIdToNewAccountModel(AllAccountsModel model, bool isOrganizationAccount)
    {
        if (!_config.ImportId.HasImportId) return;
        if (isOrganizationAccount && !_config.ImportId.ApplyToCreatedOrganizationAccounts) return;
        if (!isOrganizationAccount && !_config.ImportId.ApplyToCreatedContacts) return;

        string keywordField = TextUtil.Clean(_config.ImportId.KeywordField);
        string importId = TextUtil.Clean(_config.ImportId.Value);
        if (string.IsNullOrWhiteSpace(keywordField) || string.IsNullOrWhiteSpace(importId)) return;

        SetRequired(model, keywordField, importId);
    }

    private void ApplyRunTagToNewAccountModel(AllAccountsModel model, bool isOrganizationAccount)
    {
        if (!_config.Tagging.HasRunTag) return;
        if (isOrganizationAccount && !_config.Tagging.ApplyToCreatedOrganizationAccounts) return;
        if (!isOrganizationAccount && !_config.Tagging.ApplyToCreatedContacts) return;

        SetRunTagOnAccountModel(model, isOrganizationAccount);
    }

    private void SetRunTagOnAccountModel(AllAccountsModel model, bool isOrganizationAccount)
    {
        model.AccountUserFieldSets ??= new List<UserFields>();

        UserFields userField = FindMatchingRunTagUserFieldSet(model, isOrganizationAccount);
        SetRunTagHeaderClassAndType(userField, isOrganizationAccount);
        SetRequired(userField, _config.Tagging.UserTextProperty, TextUtil.Clean(_config.Tagging.RunTagValue));
    }

    private string GetRunTagCurrentValue(AllAccountsModel model, bool isOrganizationAccount)
    {
        if (model.AccountUserFieldSets is null) return string.Empty;
        UserFields? userField = model.AccountUserFieldSets.FirstOrDefault(field => IsMatchingRunTagUserFieldSet(field, isOrganizationAccount));
        return userField is null ? string.Empty : GetString(userField, _config.Tagging.UserTextProperty);
    }

    private UserFields FindMatchingRunTagUserFieldSet(AllAccountsModel model, bool isOrganizationAccount)
    {
        model.AccountUserFieldSets ??= new List<UserFields>();

        UserFields? existing = model.AccountUserFieldSets.FirstOrDefault(field => IsMatchingRunTagUserFieldSet(field, isOrganizationAccount));
        if (existing is not null) return existing;

        var created = new UserFields();
        SetRunTagHeaderClassAndType(created, isOrganizationAccount);
        model.AccountUserFieldSets.Add(created);
        return created;
    }

    private bool IsMatchingRunTagUserFieldSet(UserFields field, bool isOrganizationAccount)
    {
        string expectedHeader = isOrganizationAccount
            ? TextUtil.Clean(_config.Tagging.OrganizationUserFieldHeader)
            : TextUtil.Clean(_config.Tagging.IndividualUserFieldHeader);

        string actualHeader = GetString(field, "Header");
        if (!string.IsNullOrWhiteSpace(expectedHeader) &&
            !TextUtil.EqualsTrimmedIgnoreCase(actualHeader, expectedHeader))
        {
            return false;
        }

        string expectedType = TextUtil.Clean(_config.Tagging.UserFieldType);
        if (!string.IsNullOrWhiteSpace(expectedType))
        {
            string actualType = GetString(field, "Type");
            if (!TextUtil.EqualsTrimmedIgnoreCase(actualType, expectedType)) return false;
        }

        string expectedClass = TextUtil.Clean(_config.Tagging.UserFieldClass);
        if (!string.IsNullOrWhiteSpace(expectedClass))
        {
            string actualClass = GetString(field, "Class");
            if (!TextUtil.EqualsTrimmedIgnoreCase(actualClass, expectedClass)) return false;
        }

        return true;
    }

    private void SetRunTagHeaderClassAndType(UserFields userField, bool isOrganizationAccount)
    {
        string header = isOrganizationAccount
            ? TextUtil.Clean(_config.Tagging.OrganizationUserFieldHeader)
            : TextUtil.Clean(_config.Tagging.IndividualUserFieldHeader);

        if (!string.IsNullOrWhiteSpace(header)) SetOptionalProperty(userField, "Header", header);
        if (!string.IsNullOrWhiteSpace(_config.Tagging.UserFieldClass)) SetOptionalProperty(userField, "Class", TextUtil.Clean(_config.Tagging.UserFieldClass));
        if (!string.IsNullOrWhiteSpace(_config.Tagging.UserFieldType)) SetOptionalProperty(userField, "Type", TextUtil.Clean(_config.Tagging.UserFieldType));
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private string BuildEmailSearchOData(string normalizedEmail)
    {
        // Search by the exact Contact Email value from Column AC.
        // The local code still performs exact trim/lowercase matching after the API returns candidates.
        // If your tenant requires a different email API field name, change ContactEmail in appsettings.json.
        return $"{_fields.ContactEmail} eq '{ODataString(normalizedEmail)}'";
    }

    private string BuildOrganizationAccountSearchOData(string companyName, string marketSegmentMajor, string country)
    {
        // Server-side search is intentionally simple and safe. The full uniqueness rule is enforced locally:
        // (Company Name OR Website Root Domain) + Country + Market Segment Major.
        return $"{_fields.Class} eq '{ODataString(_fields.AccountClassValue)}' and " +
               $"{_fields.AccountName} eq '{ODataString(TextUtil.CleanKeyField(companyName))}'";
    }

    private IEnumerable<string> BuildRelaxedCompanyNameSearchExpressions(string companyName)
    {
        string cleanName = TextUtil.CleanKeyField(companyName);
        string noParenthesesName = TextUtil.CompanyNameWithoutParentheses(companyName);

        if (!string.IsNullOrWhiteSpace(noParenthesesName) &&
            !TextUtil.EqualsTrimmedIgnoreCase(noParenthesesName, cleanName))
        {
            yield return $"{_fields.Class} eq '{ODataString(_fields.AccountClassValue)}' and {_fields.AccountName} eq '{ODataString(noParenthesesName)}'";
        }

        if (_config.DuplicateCheck.TryContainsSearchForRelaxedNameMatch)
        {
            string containsValue = !string.IsNullOrWhiteSpace(noParenthesesName) ? noParenthesesName : cleanName;
            if (containsValue.Length >= 5)
            {
                yield return $"{_fields.Class} eq '{ODataString(_fields.AccountClassValue)}' and contains({_fields.AccountName},'{ODataString(containsValue)}')";
            }
        }
    }

    private IEnumerable<string> BuildWebsiteSearchExpressions(string websiteRootDomain)
    {
        string field = TextUtil.Clean(_fields.Website);
        if (string.IsNullOrWhiteSpace(field)) yield break;

        string root = TextUtil.RootDomain(websiteRootDomain);
        if (string.IsNullOrWhiteSpace(root)) yield break;

        foreach (string value in new[] { root, "www." + root, "https://" + root, "https://www." + root, "http://" + root, "http://www." + root })
        {
            yield return $"{_fields.Class} eq '{ODataString(_fields.AccountClassValue)}' and {field} eq '{ODataString(value)}'";
        }
    }

    private bool IsExactOrganizationMatch(AccountLookupResult result, string companyName, string marketSegmentMajor, string rawCountry, string cleanCountry, string websiteRootDomain)
    {
        bool nameMatches = TextUtil.EqualsTrimmedIgnoreCase(result.CompanyName, companyName) ||
            (_config.DuplicateCheck.UseRelaxedCompanyNameMatch && TextUtil.RelaxedCompanyNameEquals(result.CompanyName, companyName));
        bool websiteMatches = _config.DuplicateCheck.UseWebsiteRootDomainForAccountMatch &&
            !string.IsNullOrWhiteSpace(websiteRootDomain) &&
            TextUtil.EqualsTrimmedIgnoreCase(result.WebsiteRootDomain, websiteRootDomain);

        bool countryMatches = TextUtil.EqualsTrimmedIgnoreCase(result.Country, cleanCountry) ||
            TextUtil.EqualsTrimmedIgnoreCase(result.Country, rawCountry) ||
            TextUtil.EqualsTrimmedIgnoreCase(_config.CleanCountryForMomentus(result.Country), cleanCountry);

        bool marketMatches = !_config.DuplicateCheck.UseMarketSegmentForAccountMatch ||
            TextUtil.EqualsTrimmedIgnoreCase(result.MarketSegmentMajor, marketSegmentMajor);

        return (nameMatches || websiteMatches) && countryMatches && marketMatches;
    }

    private async Task<List<object>> SearchAccountsAsync(string searchExpression, CancellationToken cancellationToken, int? maxResults = 100)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object endpoint = GetEndpoint("Accounts");

        // The Momentus SDK Search method expects the raw search expression, for example:
        //   "All"
        //   "Email eq 'person@example.com'"
        //   "Name eq 'Example Company'"
        // Do not pass a URL query string such as "$filter=...&$top=..." here.
        object? searchOptions = CreateSearchOptions(maxResults);

        object? response = await InvokeEndpointAsync(endpoint, "SearchAsync", "Search", new object?[] { _config.OrgCode, searchExpression, searchOptions }, cancellationToken)
            .ConfigureAwait(false);

        return ExtractModels(response).ToList();
    }

    private async Task<object> InvokeAccountsEndpointAsync(string asyncMethodName, string syncMethodName, AllAccountsModel model, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object endpoint = GetEndpoint("Accounts");
        object? response = await InvokeEndpointAsync(endpoint, asyncMethodName, syncMethodName, new object?[] { model, null }, cancellationToken)
            .ConfigureAwait(false);

        return response ?? throw new InvalidOperationException($"Accounts endpoint {asyncMethodName}/{syncMethodName} returned null.");
    }

    private object GetEndpoint(string endpointName)
    {
        object endpoints = _client.Endpoints;
        PropertyInfo? property = endpoints.GetType().GetProperty(endpointName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property is null)
            throw new MissingMemberException($"Could not find SDK endpoint '{endpointName}' on ApiClient.Endpoints.");

        return property.GetValue(endpoints)
            ?? throw new InvalidOperationException($"SDK endpoint '{endpointName}' returned null.");
    }

    private static async Task<object?> InvokeEndpointAsync(
        object endpoint,
        string asyncMethodName,
        string syncMethodName,
        object?[] preferredArgs,
        CancellationToken cancellationToken)
    {
        MethodInfo? method = FindCompatibleMethod(endpoint.GetType(), asyncMethodName, preferredArgs)
            ?? FindCompatibleMethod(endpoint.GetType(), syncMethodName, preferredArgs);

        if (method is null)
        {
            throw new MissingMethodException(
                endpoint.GetType().FullName,
                $"{asyncMethodName}/{syncMethodName} with compatible parameter count");
        }

        object?[] args = preferredArgs.Take(method.GetParameters().Length).ToArray();
        object? result;
        try
        {
            result = method.Invoke(endpoint, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }

        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            PropertyInfo? resultProperty = task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
            return resultProperty?.GetValue(task);
        }

        return result;
    }

    private static MethodInfo? FindCompatibleMethod(Type type, string name, object?[] preferredArgs)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => string.Equals(m.Name, name, StringComparison.Ordinal))
            .Where(m => !m.IsGenericMethodDefinition)
            .Where(m => IsMethodCompatible(m, preferredArgs))
            .OrderByDescending(m => m.GetParameters().Length)
            .FirstOrDefault();
    }

    private static bool IsMethodCompatible(MethodInfo method, object?[] preferredArgs)
    {
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length > preferredArgs.Length) return false;

        for (int i = 0; i < parameters.Length; i++)
        {
            object? arg = preferredArgs[i];
            if (arg is null)
            {
                if (parameters[i].ParameterType.IsValueType && Nullable.GetUnderlyingType(parameters[i].ParameterType) is null)
                    return false;

                continue;
            }

            Type parameterType = parameters[i].ParameterType;
            Type argType = arg.GetType();
            if (!parameterType.IsAssignableFrom(argType)) return false;
        }

        return true;
    }


    private static object? CreateSearchOptions(int? maxResults)
    {
        if (maxResults is null) return null;

        Type? optionsType = typeof(AllAccountsModel).Assembly.GetType("Ungerboeck.Api.Models.Options.Search")
            ?? Type.GetType("Ungerboeck.Api.Models.Options.Search, Ungerboeck.Api.Models")
            ?? Type.GetType("Ungerboeck.Api.Models.Options.Search, Ungerboeck.Api.Sdk");

        if (optionsType is null) return null;

        object? options = Activator.CreateInstance(optionsType);
        if (options is null) return null;

        SetOptionalProperty(options, "MaxResults", maxResults.Value);
        SetOptionalProperty(options, "MaxResult", maxResults.Value);
        SetOptionalProperty(options, "Maxresults", maxResults.Value);

        return options;
    }

    private static void SetOptionalProperty(object target, string propertyName, object value)
    {
        PropertyInfo? property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property is null || !property.CanWrite) return;

        object? converted = ConvertForProperty(property.PropertyType, Convert.ToString(value) ?? string.Empty);
        property.SetValue(target, converted);
    }

    private static IEnumerable<object> ExtractModels(object? response)
    {
        if (response is null) yield break;

        if (LooksLikeAccountModel(response))
        {
            yield return response;
            yield break;
        }

        if (response is IEnumerable directEnumerable && response is not string)
        {
            foreach (object? item in directEnumerable)
            {
                if (item is not null && LooksLikeAccountModel(item)) yield return item;
            }
            yield break;
        }

        foreach (PropertyInfo property in response.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType == typeof(string)) continue;
            if (!typeof(IEnumerable).IsAssignableFrom(property.PropertyType)) continue;

            object? value = property.GetValue(response);
            if (value is not IEnumerable enumerable) continue;

            foreach (object? item in enumerable)
            {
                if (item is not null && LooksLikeAccountModel(item)) yield return item;
            }
        }
    }

    private static IEnumerable<object> ExtractResponseItems(object? response)
    {
        if (response is null) yield break;

        if (response is IEnumerable directEnumerable && response is not string)
        {
            foreach (object? item in directEnumerable)
            {
                if (item is not null) yield return item;
            }

            yield break;
        }

        foreach (PropertyInfo property in response.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType == typeof(string)) continue;
            if (!typeof(IEnumerable).IsAssignableFrom(property.PropertyType)) continue;

            object? value = property.GetValue(response);
            if (value is not IEnumerable enumerable) continue;

            foreach (object? item in enumerable)
            {
                if (item is not null) yield return item;
            }
        }
    }

    private AccountLookupResult ToAccountLookupResult(object model)
    {
        string classValue = GetString(model, _fields.Class, "Class", "AccountClass", "ClassCode");
        if (!TextUtil.EqualsTrimmedIgnoreCase(classValue, _fields.AccountClassValue))
            return AccountLookupResult.NotFound("Candidate was not an organization account class.");

        string accountCode = GetString(model, _fields.AccountCode, "AccountCode", "Code");
        string name = GetString(model, _fields.AccountName, "Name", "AccountName", "CompanyName");
        string market = GetString(model, _fields.MarketSegmentMajor, "MarketSegmentMajor", "MarketSegment", "Market");
        string country = GetString(model, _fields.Country, "Country", "CountryCode");
        string websiteRoot = TextUtil.RootDomain(GetString(model, _fields.Website, "Website", "WebSite", "WebAddress", "URL", "Url"));

        if (string.IsNullOrWhiteSpace(accountCode))
            return AccountLookupResult.NotFound("Candidate had no Account Code.");

        return new AccountLookupResult(true, accountCode, name, market, country, websiteRoot, "Candidate organization account.");
    }

    private MomentusAccountSnapshot ToAccountSnapshot(object model)
    {
        return new MomentusAccountSnapshot(
            GetString(model, _fields.AccountCode, "AccountCode", "Code"),
            GetString(model, _fields.AccountName, "Name", "AccountName", "CompanyName"),
            GetString(model, _fields.MarketSegmentMajor, "MarketSegmentMajor", "MarketSegment", "Market"),
            GetString(model, _fields.MarketSegmentMinor, "MarketSegmentMinor"),
            GetString(model, _fields.Country, "Country", "CountryCode"),
            GetString(model, _fields.Website, "Website", "WebSite", "WebAddress", "URL", "Url"),
            GetString(model, "Email", "EmailAddress", "EMail"),
            GetString(model, "Phone", "PhoneNumber"),
            GetString(model, "EventSalesStatus"));
    }

    private MomentusContactSnapshot ToContactSnapshot(object model)
    {
        return new MomentusContactSnapshot(
            GetString(model, _fields.AccountCode, "AccountCode", "Code"),
            GetString(model, "FirstName"),
            GetString(model, "LastName"),
            GetString(model, _fields.ContactEmail, "Email", "EmailAddress", "EMail"),
            GetString(model, _fields.ParentAccountCode, "PrimaryAccount"),
            GetString(model, _fields.ParentCompanyField, "Company"));
    }

    private static void CopySimpleWritableAccountFields(AllAccountsModel source, AllAccountsModel target)
    {
        var blockedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AccountCode",
            "Code",
            "AccountUserFieldSets",
            "EnteredBy",
            "EnteredOn",
            "ChangedBy",
            "ChangedOn",
            "UpdatedBy",
            "UpdatedOn",
            "LastChangedBy",
            "LastChangedOn"
        };

        foreach (PropertyInfo sourceProperty in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!sourceProperty.CanRead || blockedNames.Contains(sourceProperty.Name)) continue;
            if (!IsSimpleCopyType(sourceProperty.PropertyType)) continue;

            PropertyInfo? targetProperty = target.GetType().GetProperty(sourceProperty.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (targetProperty is null || !targetProperty.CanWrite) continue;

            object? value = sourceProperty.GetValue(source);
            if (value is null) continue;

            targetProperty.SetValue(target, value);
        }
    }

    private static bool IsSimpleCopyType(Type type)
    {
        Type realType = Nullable.GetUnderlyingType(type) ?? type;
        return realType.IsPrimitive ||
               realType.IsEnum ||
               realType == typeof(string) ||
               realType == typeof(decimal) ||
               realType == typeof(DateTime) ||
               realType == typeof(DateTimeOffset) ||
               realType == typeof(Guid);
    }

    private static bool LooksLikeAccountModel(object model)
    {
        string typeName = model.GetType().Name;
        if (typeName.Contains("Account", StringComparison.OrdinalIgnoreCase)) return true;

        return model.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => string.Equals(p.Name, "AccountCode", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(p.Name, "Name", StringComparison.OrdinalIgnoreCase));
    }

    private static void SetRequired(object model, string propertyName, string value)
    {
        string cleanPropertyName = TextUtil.Clean(propertyName);
        if (string.IsNullOrWhiteSpace(cleanPropertyName))
            throw new InvalidOperationException("A required SDK property name is blank in configuration or Row 1 headers.");

        PropertyInfo? property = model.GetType().GetProperty(cleanPropertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property is null || !property.CanWrite)
        {
            throw new MissingMemberException(
                model.GetType().FullName,
                $"Writable property '{cleanPropertyName}'. Adjust appsettings.json MomentusFields or the Row 1 API field names.");
        }

        object? converted = ConvertForProperty(property.PropertyType, value);
        property.SetValue(model, converted);
    }

    private static object? ConvertForProperty(Type propertyType, string value)
    {
        Type targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (targetType == typeof(string)) return value;
        if (targetType.IsEnum) return Enum.Parse(targetType, value, ignoreCase: true);
        if (targetType == typeof(int) && int.TryParse(value, out int intValue)) return intValue;
        if (targetType == typeof(decimal) && decimal.TryParse(value, out decimal decimalValue)) return decimalValue;
        if (targetType == typeof(bool) && bool.TryParse(value, out bool boolValue)) return boolValue;
        if (targetType == typeof(DateTime) && DateTime.TryParse(value, out DateTime dateTimeValue)) return dateTimeValue;

        // Let the runtime surface a clear type error for unsupported conversions.
        return Convert.ChangeType(value, targetType);
    }

    private static string GetString(object model, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            PropertyInfo? property = model.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null) continue;

            object? value = property.GetValue(model);
            if (value is null) continue;
            return TextUtil.Clean(Convert.ToString(value));
        }

        return string.Empty;
    }

    private static string ODataString(string value) => TextUtil.CleanKeyField(value).Replace("'", "''");
}
