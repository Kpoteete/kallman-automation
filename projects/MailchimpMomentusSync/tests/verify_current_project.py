#!/usr/bin/env python3
"""Offline validation for Kallman Mailchimp to Momentus Sync release 3.3.0."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import re
import sys
from collections import Counter
from pathlib import Path
from typing import Any, Iterable

from pygments import lex
from pygments.lexers.dotnet import CSharpLexer
from pygments.lexers.shell import PowerShellLexer
from pygments.token import Comment, String

ROOT = Path(__file__).resolve().parent.parent
APP_VERSION = "3.3.0"
CATALOG_VERSION = "2026.08.19.4"
DEFAULT_CATALOG_HASH = "2B5FEE76C35CA3D74C73670ACD1E699020C59C7AE570F340B572B1187B32F809"

REQUIRED_FILES = [
    "Run-MailchimpSync.ps1",
    "Run Mailchimp Sync.cmd",
    "Run Mailchimp Sync.vbs",
    "Setup Mailchimp Sync.cmd",
    "Setup Mailchimp Sync.vbs",
    "Setup-MailchimpSync.ps1",
    "Manage Dropdown Lists.cmd",
    "Manage Dropdown Lists.vbs",
    "ImportMappings.json",
    "Program.cs",
    "MailchimpMomentusSync.csproj",
    "packages.lock.json",
    "Build-Release.ps1",
    "Build Release.cmd",
    "README.md",
    "Instructions.txt",
    "START-HERE.txt",
    "VERSION.txt",
    "CHANGELOG.md",
    "tests/Sample-Mailchimp.csv",
    "tests/Sample-Mailchimp-Reordered.csv",
]

ALIASES = {
    "email": {"email", "emailaddress"},
    "first_name": {"firstname"},
    "last_name": {"lastname"},
    "opens": {"opens", "opencount"},
    "clicks": {"clicks", "clickcount"},
    "account_code": {"accountcode", "contactaccountcode"},
}


def fail(message: str) -> None:
    raise AssertionError(message)


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8-sig")


def require(text: str, needles: Iterable[str], label: str) -> None:
    for needle in needles:
        if needle not in text:
            fail(f"{label} is missing required text: {needle}")


def forbid(text: str, needles: Iterable[str], label: str) -> None:
    lowered = text.lower()
    for needle in needles:
        if needle.lower() in lowered:
            fail(f"{label} still contains forbidden text: {needle}")


def normalize_header(value: str | None) -> str:
    return "".join(ch.lower() for ch in (value or "").lstrip("\ufeff").strip() if ch.isalnum())


def parse_count(value: str | None) -> int:
    text = (value or "").strip()
    if not text:
        return 0
    try:
        return max(0, int(text))
    except ValueError:
        try:
            return max(0, int(float(text) + 0.5))
        except ValueError:
            return 0


def resolve_headers(fieldnames: list[str] | None) -> dict[str, str]:
    if not fieldnames:
        fail("CSV is missing its header row")
    normalized: dict[str, str] = {}
    for header in fieldnames:
        key = normalize_header(header)
        if not key:
            continue
        if key in normalized:
            fail(f"Duplicate normalized header: {header}")
        normalized[key] = header

    result: dict[str, str] = {}
    for canonical, aliases in ALIASES.items():
        match = next((normalized[a] for a in aliases if a in normalized), None)
        if match is None:
            fail(f"Missing required header: {canonical}")
        result[canonical] = match
    return result


def analyze_csv(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        columns = resolve_headers(reader.fieldnames)
        counts = {
            "source_rows": 0,
            "response_rows": 0,
            "clicked_rows": 0,
            "campaign_rows": 0,
            "engaged_campaign_rows": 0,
            "eligible_rows": 0,
            "skipped_no_clicks": 0,
            "skipped_missing_account": 0,
            "status_i": 0,
            "status_ope": 0,
            "status_cli": 0,
        }
        eligible: list[dict[str, Any]] = []
        for row_number, row in enumerate(reader, start=2):
            if not any((value or "").strip() for value in row.values()):
                continue
            counts["source_rows"] += 1
            clicks = parse_count(row.get(columns["clicks"]))
            opens = parse_count(row.get(columns["opens"]))
            if opens > 0 or clicks > 0:
                counts["response_rows"] += 1
            if clicks <= 0:
                counts["skipped_no_clicks"] += 1
            else:
                counts["clicked_rows"] += 1
            account = (row.get(columns["account_code"]) or "").strip()
            if not account:
                counts["skipped_missing_account"] += 1
                continue
            counts["campaign_rows"] += 1
            if opens > 0 or clicks > 0:
                counts["engaged_campaign_rows"] += 1
            if clicks > 0:
                counts["status_cli"] += 1
                counts["eligible_rows"] += 1
                eligible.append(
                    {
                        "source_row": row_number,
                        "email": (row.get(columns["email"]) or "").strip(),
                        "first_name": (row.get(columns["first_name"]) or "").strip(),
                        "last_name": (row.get(columns["last_name"]) or "").strip(),
                        "opens": opens,
                        "clicks": clicks,
                        "account_code": account,
                    }
                )
            elif opens > 0:
                counts["status_ope"] += 1
            else:
                counts["status_i"] += 1
    response_percentage = int((100 * counts["response_rows"]) / counts["source_rows"] + 0.5)
    return {**counts, "response_percentage": response_percentage, "eligible": eligible}


def catalog_hash(config: dict[str, Any]) -> str:
    campaigns = sorted(config["CampaignTypes"], key=lambda x: x["CampaignType"].casefold())
    salespeople = sorted(
        config["Salespeople"],
        key=lambda x: (x["Salesperson"].casefold(), x["SalespersonAccountCode"].casefold()),
    )
    canonical = "CAMPAIGNS\n"
    canonical += "".join(
        f"{row['CampaignType']}|{row['ClickType']}|{row['OpenType']}\n" for row in campaigns
    )
    canonical += "SALESPEOPLE\n"
    canonical += "".join(
        f"{row['Salesperson']}|{row['SalespersonAccountCode']}\n" for row in salespeople
    )
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest().upper()


def verify_catalog() -> dict[str, Any]:
    config = json.loads(read("ImportMappings.json"))
    expected = json.loads(read("tests/expected_catalog.json"))
    if config["Version"] != CATALOG_VERSION:
        fail(f"Unexpected catalog version: {config['Version']}")
    if config["CampaignTypes"] != expected["campaigns"]:
        fail("Built-in campaign catalog differs from the approved expected catalog")
    if config["Salespeople"] != expected["salespeople"]:
        fail("Built-in salesperson catalog differs from the approved expected catalog")

    approved_campaigns = {
        "Launch Email": ("LMC", "LMO"),
        "Sponsorship Mailing": ("1SC", "1SO"),
        "Portfolio Mailing": ("PMC", "PMO"),
        "Blast email #1": ("CC2", "CO2"),
        "Blast email #2": ("6MC", "6MO"),
        "Blast email #3": ("1YC", "1YO"),
        "Blast email #4": ("3MC", "3MO"),
        "Blast email #5": ("CMC", "CMO"),
        "Last call mailer": ("LCC", "LCO"),
    }
    actual_campaigns = {
        row["CampaignType"]: (row["ClickType"], row["OpenType"])
        for row in config["CampaignTypes"]
    }
    if actual_campaigns != approved_campaigns:
        fail(f"Approved campaign/activity mapping mismatch: {actual_campaigns}")
    if len(config["CampaignTypes"]) != 9 or len(config["Salespeople"]) != 66:
        fail("Built-in defaults must contain 9 campaigns and 66 salesperson mappings")

    digest = catalog_hash(config)
    if digest != DEFAULT_CATALOG_HASH or digest != expected["catalogSha256"]:
        fail(f"Built-in catalog integrity hash mismatch: {digest}")

    campaign_names = [row["CampaignType"].casefold() for row in config["CampaignTypes"]]
    if len(campaign_names) != len(set(campaign_names)):
        fail("Built-in campaign names are not unique")

    codes = [row["SalespersonAccountCode"].casefold() for row in config["Salespeople"]]
    if len(codes) != len(set(codes)):
        fail("Built-in salesperson account codes are not unique")

    duplicate_names = {
        name: count
        for name, count in Counter(row["Salesperson"] for row in config["Salespeople"]).items()
        if count > 1
    }
    if duplicate_names != {"Sales Rep": 2, "Lorena Troncoso": 2}:
        fail(f"Unexpected duplicate salesperson names: {duplicate_names}")

    return {
        "version": config["Version"],
        "campaigns": len(config["CampaignTypes"]),
        "salespeople": len(config["Salespeople"]),
        "duplicate_names": duplicate_names,
        "sha256": digest,
    }


def check_balanced_source(path: Path, lexer: object) -> None:
    source = path.read_text(encoding="utf-8-sig")
    stack: list[tuple[str, int]] = []
    pairs = {")": "(", "]": "[", "}": "{"}
    line = 1
    for token_type, value in lex(source, lexer):
        start_line = line
        line += value.count("\n")
        if token_type in String or token_type in Comment:
            continue
        for ch in value:
            if ch in "([{":
                stack.append((ch, start_line))
            elif ch in ")]}":
                if not stack or stack[-1][0] != pairs[ch]:
                    fail(f"Unbalanced {ch} in {path.name} near line {start_line}")
                stack.pop()
    if stack:
        fail(f"Unclosed {stack[-1][0]} in {path.name} near line {stack[-1][1]}")


def parse_engine_summary(text: str) -> dict[str, Any]:
    success_match = re.search(r"(?im)^\s*Files processed successfully:\s*(\d+)\s*$", text)
    error_match = re.search(r"(?im)^\s*Files with errors:\s*(\d+)\s*$", text)
    success_count = int(success_match.group(1)) if success_match else None
    error_count = int(error_match.group(1)) if error_match else None
    return {
        "files_processed_successfully": success_count,
        "files_with_errors": error_count,
        "confirmed_success": success_count is not None and success_count > 0 and error_count == 0,
    }


def verify_completion_fallback() -> dict[str, Any]:
    successful_log = """
Open activities added: 61
Click activities added: 61
All pending files processed.
Files processed successfully: 1
Files with errors: 0
"""
    failed_log = """
All pending files processed.
Files processed successfully: 0
Files with errors: 1
"""
    incomplete_log = "Click activities added: 61\n"

    success = parse_engine_summary(successful_log)
    failure = parse_engine_summary(failed_log)
    incomplete = parse_engine_summary(incomplete_log)
    if not success["confirmed_success"]:
        fail("Successful engine summary was not recognized")
    if failure["confirmed_success"]:
        fail("Engine summary with errors was incorrectly recognized as successful")
    if incomplete["confirmed_success"]:
        fail("Incomplete engine summary was incorrectly recognized as successful")
    return {"successful_log": success, "failed_log": failure, "incomplete_log": incomplete}


def verify_source() -> dict[str, Any]:
    runner = read("Run-MailchimpSync.ps1")
    require(
        runner,
        [
            "Get-RequiredHeaderIndex",
            "Read-RawMailchimpCsv",
            "Get-CampaignDetailStatus",
            "$campaignRows",
            "$engagedCampaignRows",
            "$Selection.Review.EngagedCampaignRows",
            "IGNORED_NO_INTERACTION",
            "CampaignDetailStatus",
            "CampaignIgnoredNoInteraction",
            "CampaignOpenedAndClicked",
            "$script:UserMappingsPath",
            "$script:MappingBackupRoot",
            "Show-MappingEditor",
            "Manage Lists",
            "Import JSON...",
            "Export JSON...",
            "Restore Built-In Defaults",
            "$ConfigureMappingsOnly",
            "Get-EngineOutputSummary",
            "ConfirmedSuccess",
            "CompletionSource",
            "launcher-result.json",
            "if ($result.Succeeded)",
            "ProgressStatus",
            "ProgressDetails",
            "-ProgressForm $progressForm",
            "ACTIVITY TYPES (SET BY CAMPAIGN TYPE)",
            "$campaignTitleLabel.Text = 'CAMPAIGN'",
            "$campaignTitleTextBox",
            "$campaignTitleTextBox.MaxLength = 60",
            "$sentDatePicker",
            "$sentDateLabel.Text = 'CAMPAIGN SENT DATE'",
            "$hasCampaignTitle",
            "CampaignTitle = $campaignTitleTextBox.Text.Trim()",
            "CampaignSentDate = $sentDatePicker.Value.Date",
            "[string]$Selection.CampaignTitle",
            "'Campaign Sent Date'",
            "$Selection.CampaignSentDate.ToString('yyyy-MM-dd'",
            "Campaign = [string]$Selection.CampaignTitle",
            "CampaignSentDate = $Selection.CampaignSentDate.ToString('yyyy-MM-dd'",
            "Campaign Type: $([string]$Selection.Campaign.CampaignType)",
            "if ([string]$userMappings.Version -ne $script:CatalogVersion)",
            "^[1-9]\\d{3}$",
            "Write-PreparedImportCsv",
            "mailchimp_preflight.csv",
            "run-settings.json",
            "Select-ImportQueueOptions",
            "DataGridView",
            "+ Add another file",
            "Run Queue",
            "Files run sequentially in the order shown",
            "QueueIndex",
            "QueueCount",
            "-SuppressResultUi",
            "$ConfigureCredentialsOnly",
            "WindowStyle = 'Hidden'",
        ],
        "Run-MailchimpSync.ps1",
    )
    # The user-entered Campaign must drive column B / note text, while the
    # Campaign Type dropdown only supplies the activity codes.
    if "[string]$Selection.CampaignTitle," not in runner:
        fail("Prepared import does not write Campaign into column B")
    if "[string]$Selection.Campaign.ClickType," not in runner or "[string]$Selection.Campaign.OpenType," not in runner:
        fail("Prepared import does not use Campaign Type for activity codes")

    queue_form = runner.index("function Select-ImportQueueOptions")
    queue_grid = runner.index("[Windows.Forms.DataGridView]::new()", queue_form)
    queue_add = runner.index("+ Add another file", queue_grid)
    queue_run = runner.index("Run Queue", queue_add)
    queue_main = runner.index("Select-ImportQueueOptions -Mappings", queue_run)
    queue_workflow = runner.index("Invoke-ImportWorkflow -Selection", queue_main)
    queue_stop = runner.index("if (-not $workflowResult.Succeeded)", queue_workflow)
    if not queue_form < queue_grid < queue_add < queue_run < queue_main < queue_workflow < queue_stop:
        fail("Batch queue UI or stop-on-error orchestration is out of order")

    forbid(
        runner,
        [
            "ExpectedCatalogSha256",
            "Count -ne 7",
            "Count -ne 66",
            "Select Yes to load them from your existing 12-column import template",
            "Update Dropdown Lists",
            "Import Lists from Template",
            "Update-DropdownLists.ps1",
        ],
        "Run-MailchimpSync.ps1",
    )

    # User-managed catalog must take precedence over built-in defaults.
    user_branch = runner.index("if (Test-Path -LiteralPath $script:UserMappingsPath")
    default_branch = runner.index("return Get-DefaultImportMappings", user_branch)
    if user_branch > default_branch:
        fail("User-managed mapping file does not take precedence")

    program = read("Program.cs")
    require(
        program,
        [
            'private const string EVENT_ID_COLUMN = "A"',
            'private const string CLICKS_COLUMN = "H"',
            'private const string CONTACT_ACCOUNT_COLUMN = "L"',
            'private const string CAMPAIGN_SENT_DATE_COLUMN = "M"',
            'private const string CAMPAIGN_EMAILS_SENT_COLUMN = "N"',
            'private const string CAMPAIGN_RESPONSE_PERCENTAGE_COLUMN = "O"',
            "USISDKConstants.AccountDesignations.EventSales",
            "List<CsvRow> engagementRows = campaignDetailRows.Where(r => r.Clicks > 0).ToList()",
            "account.PrimaryAccount",
            "newExhibitor.Salesperson = exhibitorData.SalespersonAccountCode",
            "AddExhibitorActivity",
            "BuildActivityEngagementText",
            "exhibitorData.CampaignSentDate",
            '" - Sent on "',
            '" - opened "',
            '", clicked "',
            "Subject = subject",
            "Text = text",
            "ParseCampaignSentDate",
            "UpsertExhibitorEngagementNote",
            "BuildCampaignRoster",
            "PrepareCampaignSync",
            "UpsertCampaignAndDetails",
            "apiClient.Endpoints.Campaigns.Add",
            "apiClient.Endpoints.CampaignDetails.Add",
            'Status = recipient.Status',
            'return "CLI"',
            'return "OPE"',
            'return "I"',
            'ID = "*AUTO"',
            "Designation = CAMPAIGN_DESIGNATION",
            "Event = eventId",
            "Coordinator = NormalizeAccountCode(coordinatorAccountCode)",
            "EmailsSent = emailsSent",
            "EmailResponsesPercentage = responsePercentage",
            "StartDate = campaignSentDate.Date",
            "EndDate = campaignEndDate.Date",
            "Group is intentionally left blank",
            "Campaign creation is intentionally the final Momentus write phase",
            'TargetKind = "Contact"',
            "Account = recipient.AccountCode",
            "ContactsWithoutPrimaryAccounts",
            "CAMPAIGN_CONTACT_ONLY_NO_PRIMARY",
            "Contact resolution progress",
            "Exhibitor progress",
            "Campaign detail progress",
            "STEP 1 OF 6",
            "STEP 6 OF 6",
            "SKIP_ACCOUNT_NOT_FOUND",
            "SkippedMissingAccounts",
            "IsMomentusAccountNotFound",
            "IsMomentusInvalidAccount",
            "LoadAllCampaignDetails",
            "NavigateSearchList",
            "DetailsSkippedInvalidAccount",
            "qualifiedEngagementRows",
            "List<CsvRow> campaignDetailRows",
            "BuildCampaignRoster(apiClient, ORG_CODE, campaignDetailRows)",
        ],
        "Program.cs",
    )

    process_start = program.index("private static void ProcessSingleCsvFile")
    note_call = program.index("UpsertExhibitorEngagementNote(", process_start)
    campaign_call = program.index("CampaignSyncResult campaignResult = UpsertCampaignAndDetails(", process_start)
    if campaign_call < note_call:
        fail("Campaign write phase does not occur after exhibitor note processing")

    campaign_add = program.index("apiClient.Endpoints.Campaigns.Add")
    detail_add = program.index("apiClient.Endpoints.CampaignDetails.Add")
    if detail_add < campaign_add:
        fail("Campaign details are added before the campaign itself")

    forbid(program, ['TargetKind = "Exhibitor"'], "Program.cs contact-only campaign roster")
    roster_start = program.index("private static CampaignRoster BuildCampaignRoster")
    contact_recipient_add = program.index("roster.Recipients.Add(recipient)", roster_start)
    no_primary_branch = program.index(
        "if (string.IsNullOrWhiteSpace(orgAccountCode))",
        roster_start,
    )
    if contact_recipient_add > no_primary_branch:
        fail("No-Primary-Account contacts are excluded before campaign recipient creation")

    build = read("Build-Release.ps1")
    require(
        build,
        [
            "$expectedCatalogVersion = '2026.08.19.4'",
            "--locked-mode",
            "PublishSingleFile=true",
            "Manage Dropdown Lists.cmd",
            "Manage Dropdown Lists.vbs",
            "Kallman-Mailchimp-Momentus-Sync-v3.3.0-win-x64.zip",
        ],
        "Build-Release.ps1",
    )
    forbid(build, ["expectedCatalogSha256"], "Build-Release.ps1")

    run_cmd = read("Run Mailchimp Sync.cmd")
    setup_cmd = read("Setup Mailchimp Sync.cmd")
    manage_cmd = read("Manage Dropdown Lists.cmd")
    manage_vbs = read("Manage Dropdown Lists.vbs")
    require(run_cmd, ["wscript.exe", "Run Mailchimp Sync.vbs"], "run CMD")
    require(setup_cmd, ["wscript.exe", "Setup Mailchimp Sync.vbs"], "setup CMD")
    require(manage_cmd, ["wscript.exe", "Manage Dropdown Lists.vbs"], "manage CMD")
    require(manage_vbs, ["Run-MailchimpSync.ps1", "-ConfigureMappingsOnly"], "manage VBS")

    setup_wrapper = read("Setup-MailchimpSync.ps1")
    require(setup_wrapper, ["Run-MailchimpSync.ps1", "-ConfigureCredentialsOnly"], "setup wrapper")

    check_balanced_source(ROOT / "Run-MailchimpSync.ps1", PowerShellLexer())
    check_balanced_source(ROOT / "Build-Release.ps1", PowerShellLexer())
    check_balanced_source(ROOT / "Setup-MailchimpSync.ps1", PowerShellLexer())
    check_balanced_source(ROOT / "Program.cs", CSharpLexer())

    return {"powershell_files_checked": 3, "csharp_files_checked": 1}


def verify_no_secrets() -> str:
    forbidden_key_pattern = re.compile(
        r'(?i)"\s*(apiuserid|apiuser|secret|key|credentials|momentus_apiuser|momentus_secret|momentus_key)\s*"\s*:'
    )
    for path in ROOT.rglob("*"):
        if not path.is_file() or path.suffix.lower() in {".zip", ".png", ".ico"}:
            continue
        text = path.read_text(encoding="utf-8-sig", errors="ignore")
        if path.name == "ImportMappings.json" and forbidden_key_pattern.search(text):
            fail("Credential-like field found in ImportMappings.json")
        if re.search(r"(?im)^\s*(MOMENTUS_SECRET|MOMENTUS_KEY)\s*=\s*['\"][^$'\"]+['\"]", text):
            fail(f"Possible hardcoded credential in {path.relative_to(ROOT)}")
    return "passed"


def verify_fixtures(sofex: Path | None) -> dict[str, Any]:
    expected = {
        "source_rows": 6,
        "response_rows": 5,
        "clicked_rows": 4,
        "campaign_rows": 5,
        "engaged_campaign_rows": 4,
        "eligible_rows": 3,
        "skipped_no_clicks": 2,
        "skipped_missing_account": 1,
        "status_i": 1,
        "status_ope": 1,
        "status_cli": 3,
        "response_percentage": 83,
    }
    normal = analyze_csv(ROOT / "tests/Sample-Mailchimp.csv")
    reordered = analyze_csv(ROOT / "tests/Sample-Mailchimp-Reordered.csv")
    for label, result in (("normal", normal), ("reordered", reordered)):
        for key, value in expected.items():
            if result[key] != value:
                fail(f"{label} fixture expected {key}={value}, got {result[key]}")

    output: dict[str, Any] = {
        "normal": {key: normal[key] for key in expected},
        "reordered": {key: reordered[key] for key in expected},
    }
    if sofex is not None and sofex.exists():
        result = analyze_csv(sofex)
        expected_sofex = {
            "source_rows": 883,
            "clicked_rows": 61,
            "eligible_rows": 61,
            "skipped_no_clicks": 822,
            "skipped_missing_account": 0,
        }
        for key, value in expected_sofex.items():
            if result[key] != value:
                fail(f"Sofex expected {key}={value}, got {result[key]}")
        output["sofex"] = {key: result[key] for key in expected_sofex}
    return output


def generate_manifest() -> str:
    lines: list[str] = []
    for path in sorted(ROOT.rglob("*")):
        if not path.is_file() or path.name in {"SOURCE-MANIFEST.sha256", "VALIDATION.json"}:
            continue
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        lines.append(f"{digest}  {path.relative_to(ROOT).as_posix()}")
    text = "\n".join(lines) + "\n"
    (ROOT / "SOURCE-MANIFEST.sha256").write_text(text, encoding="ascii")
    return hashlib.sha256(text.encode("ascii")).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--sofex", type=Path)
    args = parser.parse_args()

    missing = [name for name in REQUIRED_FILES if not (ROOT / name).exists()]
    if missing:
        fail(f"Missing required files: {missing}")

    result = {
        "release": APP_VERSION,
        "catalog": verify_catalog(),
        "source": verify_source(),
        "completion_fallback": verify_completion_fallback(),
        "secret_scan": verify_no_secrets(),
        "fixtures": verify_fixtures(args.sofex),
        "manifest_sha256": generate_manifest(),
    }
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"VERIFICATION FAILED: {exc}", file=sys.stderr)
        raise
