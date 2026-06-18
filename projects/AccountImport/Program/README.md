# Momentus/Ungerboeck Account + Contact Import Console App

This project implements a production-safe phased import flow for accounts and contacts using the Momentus/Ungerboeck API SDK and ClosedXML.

## Safety defaults

- `DryRun` defaults to `true` in `appsettings.json`.
- Dry-run reads Excel files, searches Momentus, prepares output files, and writes audit logs.
- Dry-run does **not** create or update anything in Momentus.
- Live mode requires both:
  1. Running with `--live` or changing `DryRun` to `false`.
  2. Typing `IMPORT` at the Phase 3 live confirmation prompt.
- The original Phase 0 workbook is never overwritten or deleted.
- Every generated workbook uses a timestamped file name.
- Phase 5 copies all session files into a timestamped archive folder. It does not delete Phase 0.

## Project structure

```text
C:\kwi-automations\projects\AccountImport
├── Program
│   ├── AccountImport.csproj
│   ├── Program.cs
│   ├── appsettings.json
│   ├── Models
│   └── Services
├── Phase 0
├── Phase 1 - lookup and disregard
├── Phase 2 Lookup and Match
├── Phase 3 - import
├── Phase 4 - verify
└── Phase 5 - complete
```

## Important adjustment point

`Services/MomentusSdkApiService.cs` is the only place that touches the Momentus/Ungerboeck SDK. The public SDK exposes an `ApiClient`, JWT authorization, and `Accounts` endpoint methods, but tenant/version-specific model property names for fields like Class, Market Segment Major, Country, and the parent-account link may differ.

Adjust these values in `appsettings.json` if your SDK/model uses different names:

```json
"MomentusFields": {
  "Organization": "Organization",
  "AccountCode": "AccountCode",
  "AccountName": "Name",
  "Class": "Class",
  "AccountClassValue": "Account",
  "ContactClassValue": "Contact",
  "MarketSegmentMajor": "MarketSegmentMajor",
  "Country": "Country",
  "ContactEmail": "Email",
  "ParentAccountCode": "AccountCode"
}
```

The current contact-parent link follows your provided contact field mapping where Column V is `AccountCode`. If your SDK distinguishes a contact's own account code from its parent organization account code, change `ParentAccountCode` before live import.

## Install packages

From the `Program` folder:

```powershell
cd "C:\kwi-automations\projects\AccountImport\Program"
dotnet restore
```

The `.csproj` references:

- `ClosedXML` for Excel handling
- `Ungerboeck.Api.Sdk` for Momentus/Ungerboeck API calls

If your Momentus/Ungerboeck version is not compatible with the referenced SDK version, install the package version whose second version segment matches your Momentus/Ungerboeck version.

Example:

```powershell
dotnet remove package Ungerboeck.Api.Sdk
dotnet add package Ungerboeck.Api.Sdk --version <your-compatible-version>
```

## Set environment variables in Windows

Use user-level variables:

```powershell
setx MOMENTUS_APIUSER "KYLEPAPI"
setx MOMENTUS_SECRET "8c247eb8-2342-452a-95c3-cf22bd1c6a56"
setx MOMENTUS_KEY "e2b97782-08d7-40f3-bdbc-fbef5095154c"
```

Close and reopen PowerShell after using `setx`, then confirm:

```powershell
$env:MOMENTUS_APIUSER
$env:MOMENTUS_SECRET
$env:MOMENTUS_KEY
```

For a one-session test only:

```powershell
$env:MOMENTUS_APIUSER = "your-api-user-id"
$env:MOMENTUS_SECRET = "your-secret-guid"
$env:MOMENTUS_KEY = "your-key-guid"
```

## Run in dry-run mode

Dry-run is the default.

```powershell
cd "C:\kwi-automations\projects\AccountImport\Program"
dotnet run -- --dry-run
```

You can also omit `--dry-run` if `appsettings.json` still has `"DryRun": true`.

## Run in live mode

```powershell
cd "C:\kwi-automations\projects\AccountImport\Program"
dotnet run -- --live
```

The app still performs Phase 1 and Phase 2 with human review checkpoints first. Before Phase 3 writes, it prints:

```text
LIVE MODE ENABLED. This will write to Momentus.
```

It then requires:

```text
IMPORT
```

Any other input stops safely before writes.

## Output behavior

Phase 1 creates:

- duplicate contacts workbook
- non-duplicate contacts workbook
- optional review-required workbook for blank/invalid emails or lookup failures

Phase 2 creates:

- existing organization accounts workbook
- new organization accounts needed workbook
- optional review-required workbook for missing account key fields or lookup failures

Phase 4 creates:

- audit CSV
- final annotated existing-accounts workbook
- final annotated new-accounts workbook

Phase 5 creates a timestamped archive folder and copies every file used or created during the session.

## Audit log fields

The audit CSV contains:

- Timestamp
- Phase
- DryRun
- SourceFileName
- RowNumber
- CompanyName
- ContactEmail
- ActionAttempted
- Result
- AccountCode
- MomentusResponseMessage
- ErrorMessage

## Required Excel assumptions

- Row 1 contains API field names.
- Row 2 contains friendly field names.
- Data starts at Row 3.
- Column A = Company Name.
- Column B = Account Code.
- Column F = Market Segment Major.
- Column O = Country.
- Column V through AL = contact fields.
- Column AC = Contact Email.
- Column AM = Duplicate Found.

The app validates the contact API header mapping from Column V through AL before processing.
