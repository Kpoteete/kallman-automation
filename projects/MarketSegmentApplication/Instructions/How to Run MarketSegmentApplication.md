# How to Run MarketSegmentApplication

## What It Does

This tool applies market segment cleanup before duplicate merging:

- rolls contact interest codes up to the existing organization account;
- applies the one mapped market segment when all mapped interests point to one segment;
- counts each mapped interest separately when choosing the account segment;
- applies a dominant mapped segment when it reaches the configured threshold, currently `60%`;
- applies Aerospace (`AER`) when mapped interest votes are tied and Aerospace is one of the tied segments;
- otherwise applies the configured parent/multiple-industries segment, currently `P / A / PA`;
- never creates new organization accounts.

## Step By Step

1. Open PowerShell.

2. Go to the project folder:

```powershell
cd "C:\kwi-automations\projects\MarketSegmentApplication"
```

3. Create a review workbook:

```powershell
dotnet run --project "C:\kwi-automations\projects\MarketSegmentApplication\MarketSegmentApplication.csproj"
```

4. Open the workbook in:

```text
C:\kwi-automations\projects\MarketSegmentApplication\01 Pending Review
```

5. On the `Actions` sheet, type `APPROVED` in column A for rows you want to run.

6. Save and close the workbook.

7. Move the workbook to:

```text
C:\kwi-automations\projects\MarketSegmentApplication\02 Approved
```

8. Apply approved workbooks:

```powershell
dotnet run --project "C:\kwi-automations\projects\MarketSegmentApplication\MarketSegmentApplication.csproj" -- --live --apply-approved
```

To retry failed rows after the first pass:

```powershell
dotnet run --project "C:\kwi-automations\projects\MarketSegmentApplication\MarketSegmentApplication.csproj" -- --reset-failed-approved
dotnet run --project "C:\kwi-automations\projects\MarketSegmentApplication\MarketSegmentApplication.csproj" -- --live --apply-approved
```

Worker settings are in `appsettings.json`:

```json
"MaxConcurrentPlanParents": 10,
"MaxConcurrentApplyRows": 50,
"ApplyWorkbookSaveEveryRows": 100
```

Successful workbooks move to `03 Completed`.

## Reset Planning History

```powershell
dotnet run --project "C:\kwi-automations\projects\MarketSegmentApplication\MarketSegmentApplication.csproj" -- --reset-plan-history
```

Dry-run is on by default. Live writes require `--live`.
