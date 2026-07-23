namespace AccountImport.Models;

public sealed class AppConfig
{
    public string RootPath { get; set; } = @"C:\kwi-automations\projects\AccountImport";
    public string MomentusUri { get; set; } = "https://kallman.ungerboeck.com/prod";
    public string OrgCode { get; set; } = "10";

    // Safety default: dry-run unless explicitly overridden by --live. v10p auto-runs phases without Y/IMPORT step confirmations.
    public bool DryRun { get; set; } = true;
    public bool ConfirmProductionWrites { get; set; }
    public int MaxApiRetries { get; set; } = 3;
    public int ApiTimeoutSeconds { get; set; } = 100;
    public bool CleanupPhase0To4AfterArchive { get; set; } = true;

    public MomentusFieldMap MomentusFields { get; set; } = new();
    public CodeGenerationConfig CodeGeneration { get; set; } = new();
    public RelationshipConfig Relationship { get; set; } = new();
    public DuplicateCheckConfig DuplicateCheck { get; set; } = new();
    public ExistingAccountUpdateConfig ExistingAccountUpdates { get; set; } = new();
    public AffiliationConfig Affiliation { get; set; } = new();
    public UserTextTaggingConfig Tagging { get; set; } = new();
    public ImportIdConfig ImportId { get; set; } = new();
    public Dictionary<string, string> CountryAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USA"] = "***",
        ["U.S.A."] = "***",
        ["U.S."] = "***",
        ["United States"] = "***",
        ["United States of America"] = "***",
        ["ARG"] = "AR",
        ["ARE"] = "AE",
        ["CAN"] = "CA",
        ["CHE"] = "CH",
        ["GBR"] = "GB",
        ["NLD"] = "NL",
        ["SAU"] = "SA",
        ["SGP"] = "SG",
        ["UK"] = "GB",
        ["U.K."] = "GB",
        ["United Kingdom"] = "GB",
        ["UAE"] = "AE",
        ["U.A.E."] = "AE",
        ["United Arab Emirates"] = "AE"
    };

    public string ProgramFolder => Path.Combine(RootPath, "Program");
    public string Phase0Folder => Path.Combine(RootPath, "Phase 0");
    public string Phase1Folder => Path.Combine(RootPath, "Phase 1 - lookup and disregard");
    public string Phase2Folder => Path.Combine(RootPath, "Phase 2 Lookup and Match");
    public string Phase3Folder => Path.Combine(RootPath, "Phase 3 - import");
    public string Phase4Folder => Path.Combine(RootPath, "Phase 4 - verify");
    public string Phase5Folder => Path.Combine(RootPath, "Phase 5 - complete");

    public static AppConfig CreateDefault() => new();

    public void EnsureFolders()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(ProgramFolder);
        Directory.CreateDirectory(Phase0Folder);
        Directory.CreateDirectory(Phase1Folder);
        Directory.CreateDirectory(Phase2Folder);
        Directory.CreateDirectory(Phase3Folder);
        Directory.CreateDirectory(Phase4Folder);
        Directory.CreateDirectory(Phase5Folder);
    }

    public string CleanCountryForMomentus(string? rawCountry)
    {
        string clean = TextUtil.CleanKeyField(rawCountry);
        if (string.IsNullOrWhiteSpace(clean)) return string.Empty;
        if (CountryAliases.TryGetValue(clean, out string? mapped)) return mapped;

        foreach (var kvp in CountryAliases)
        {
            if (string.Equals(TextUtil.CleanKeyField(kvp.Key), clean, StringComparison.OrdinalIgnoreCase))
            {
                return TextUtil.CleanKeyField(kvp.Value);
            }
        }

        return clean;
    }
}

public sealed class MomentusFieldMap
{
    public string Organization { get; set; } = "Organization";
    public string AccountCode { get; set; } = "AccountCode";
    public string AccountName { get; set; } = "Name";
    public string Class { get; set; } = "Class";
    public string AccountClassValue { get; set; } = "O";
    public string ContactClassValue { get; set; } = "P";
    public string MarketSegmentMajor { get; set; } = "MarketSegmentMajor";
    public string MarketSegmentMinor { get; set; } = "MarketSegmentMinor";
    public string Country { get; set; } = "Country";
    public string Website { get; set; } = "Website";
    public string ContactEmail { get; set; } = "Email";
    public string ParentAccountCode { get; set; } = "PrimaryAccount";
    public string ParentCompanyField { get; set; } = "Company";
}

public sealed class DuplicateCheckConfig
{
    // Account uniqueness rule: (Company Name OR Website Root Domain) + Country + Market Segment Major.
    public bool UseWebsiteRootDomainForAccountMatch { get; set; } = true;
    public bool UseMarketSegmentForAccountMatch { get; set; } = true;

    // If true, rows missing Market Segment Major are withheld instead of being imported under an ambiguous account key.
    public bool RequireMarketSegmentForAccountMatch { get; set; } = true;

    // Relaxed company-name matching ignores punctuation, legal suffixes, and parenthetical acronyms.
    // Example: "American Institute in Taiwan (AIT)" matches "American Institute in Taiwan".
    public bool UseRelaxedCompanyNameMatch { get; set; } = true;

    // This tries a broader contains(Name,'...') server search when exact-name search does not return the relaxed candidate.
    // If a Momentus tenant does not support contains(), the code catches that error and continues safely.
    public bool TryContainsSearchForRelaxedNameMatch { get; set; } = true;
}

public sealed class ExistingAccountUpdateConfig
{
    // Conservative enrichment: fill blank fields on existing Momentus organization accounts from nonblank import values.
    // Never overwrites a nonblank Momentus value with an import value.
    public bool UpdateBlankFieldsOnly { get; set; } = true;

    // Require a live-run confirmation before these updates happen. In dry-run, the audit log shows what would be updated.
    public bool EnabledInLiveMode { get; set; } = true;

    // Conservative blank-fill list for existing accounts. MarketSegmentMajor/Minor are included
    // now that the SDK model exposes them directly on AllAccountsModel.
    public List<string> AllowedFields { get; set; } = new()
    {
        "Name",
        "Website",
        "WebSite",
        "WebAddress",
        "URL",
        "Url",
        "Type",
        "AccountType",
        "Email",
        "Phone",
        "Address1",
        "Address2",
        "City",
        "State",
        "PostalCode",
        "Country",
        "MarketSegmentMajor",
        "MarketSegmentMinor",
        "Keyword"
    };
}

public sealed class ImportIdConfig
{
    // Optional run-level import identifier. Prompted at the start of each run.
    // Must be 1-7 alphanumeric characters when provided. Written to the direct account field named Keyword.
    public bool PromptForImportId { get; set; } = true;
    public string Value { get; set; } = string.Empty;
    public string KeywordField { get; set; } = "Keyword";
    public int MaxLength { get; set; } = 7;
    public bool ApplyToCreatedOrganizationAccounts { get; set; } = true;
    public bool ApplyToCreatedContacts { get; set; } = true;
    public bool FillBlankExistingOrganizationAccounts { get; set; } = false;
    public bool ApplyToAllTouchedAccountsAndContacts { get; set; } = true;
    public bool OverwriteExistingKeyword { get; set; } = true;

    public bool HasImportId => !string.IsNullOrWhiteSpace(TextUtil.Clean(Value));
}

public sealed class RelationshipConfig
{
    public bool CreateRelationshipAfterContact { get; set; } = true;
    public string RelationshipType { get; set; } = "CTA";
    public string EventSalesDesignation { get; set; } = "P";

    public bool HasUsableRelationshipType =>
        !string.IsNullOrWhiteSpace(TextUtil.Clean(RelationshipType)) &&
        !TextUtil.Clean(RelationshipType).Contains("REPLACE", StringComparison.OrdinalIgnoreCase);
}

public sealed class UserTextTaggingConfig
{
    // At the start of the run, ask for one run tag. If blank, no tag is applied.
    public bool Enabled { get; set; } = false;

    public bool PromptForRunTag { get; set; } = false;

    // Runtime value populated by the start-of-run prompt. This can also be set in appsettings.json.
    public string RunTagValue { get; set; } = string.Empty;

    // The Momentus user field property to write. User request: UserText02.
    public string UserTextProperty { get; set; } = "UserText02";

    // These are the user field set headers used by the Momentus SDK.
    public string OrganizationUserFieldHeader { get; set; } = "OrganizationAccountUserFields";
    public string IndividualUserFieldHeader { get; set; } = "IndividualAccountUserFields";

    // If your user field set requires Class/Type values, set them here.
    // The tag value itself still goes into UserText02.
    public string UserFieldClass { get; set; } = string.Empty;
    public string UserFieldType { get; set; } = string.Empty;

    public bool ApplyToCreatedOrganizationAccounts { get; set; } = false;
    public bool ApplyToCreatedContacts { get; set; } = false;
    public bool ApplyToMatchedExistingAccounts { get; set; } = false;

    // This assumes UserText02 is the dedicated import tag field.
    public bool OverwriteExistingValue { get; set; } = true;

    public bool HasRunTag => Enabled && !string.IsNullOrWhiteSpace(TextUtil.Clean(RunTagValue));
}

public sealed class AffiliationConfig
{
    // The program prompts near the end for an affiliation/interest code when this is true.
    public bool PromptForAffiliationCode { get; set; } = true;

    // Apply the entered code to all organization account codes touched by this run, all contact account codes created by this run, and duplicate contacts found in Phase 1.
    public bool ApplyToAccountsTouchedThisRun { get; set; } = true;
    public bool ApplyToContactsCreatedThisRun { get; set; } = true;
    public bool ApplyToDuplicateContactsFound { get; set; } = true;
}

public sealed class CodeGenerationConfig
{
    public string ContactAccountCodeMode { get; set; } = "Auto"; // Auto or GenerateFromSeed
    public int StartingContactAccountCode { get; set; } = 223000;
    public int MaxCodeGenerationAttempts { get; set; } = 500;
}