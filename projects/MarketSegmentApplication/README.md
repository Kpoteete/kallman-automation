# Market Segment Application

This tool rolls contact interests up to existing organization accounts and applies market segments to those same accounts. It does not create new organization accounts.

## Rules

- Pulls only active/prospective organization accounts: `EventSalesStatus = A` or `P`.
- Reads each account's related contacts.
- Reads each contact's interest/affiliation codes.
- Uses `Segments.xlsx` to map interest codes to market segment major/minor values.
- If a mapping row says `skip`, that interest is ignored for market-segment classification.
- Adds all contact interests to the organization account when approved.
- Counts each mapped interest separately when choosing a segment.
- If exactly one mapped segment exists, applies that segment to the organization account.
- If multiple mapped segments exist and one segment has at least `60%` of mapped interest votes, applies that dominant segment.
- If mapped interest votes are tied and one tied segment is Aerospace (`AER`), applies Aerospace.
- If multiple mapped segments exist and none reaches `60%` and the Aerospace tie-break does not apply, applies the configured parent/multiple-industries segment: `P / A / PA`.

## Folder Workflow

- `00 Control`: planning history.
- `01 Pending Review`: new review workbooks.
- `02 Approved`: move approved workbooks here before live apply.
- `03 Completed`: successfully applied workbooks.
- `04 Failed`: failed apply workbooks.
- `99 History`: old files and manual archives.

## Commands

Create a review workbook:

```powershell
dotnet run --project "C:\kwi-automations\projects\MarketSegmentApplication\MarketSegmentApplication.csproj"
```

Approve rows in the `Actions` sheet by typing `APPROVED` in column A, save/close the workbook, then move it from `01 Pending Review` to `02 Approved`.

Apply all approved workbooks:

```powershell
dotnet run --project "C:\kwi-automations\projects\MarketSegmentApplication\MarketSegmentApplication.csproj" -- --live --apply-approved
```

Retry failed approved rows after a pass:

```powershell
dotnet run --project "C:\kwi-automations\projects\MarketSegmentApplication\MarketSegmentApplication.csproj" -- --reset-failed-approved
dotnet run --project "C:\kwi-automations\projects\MarketSegmentApplication\MarketSegmentApplication.csproj" -- --live --apply-approved
```

Speed settings live in `appsettings.json`:

- `MaxConcurrentPlanParents`: parent accounts planned at once.
- `MaxConcurrentApplyRows`: approved rows applied at once.
- `ApplyWorkbookSaveEveryRows`: completed rows between workbook saves.

Reset planning history:

```powershell
dotnet run --project "C:\kwi-automations\projects\MarketSegmentApplication\MarketSegmentApplication.csproj" -- --reset-plan-history
```

Dry-run is enabled by default in `appsettings.json`; live writes require `--live`.
