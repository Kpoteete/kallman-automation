# How to Run DuplicateMerging

This tool finds likely duplicate organization accounts in Momentus, creates Excel review plans, and applies only the rows you approve.

## Folder Workflow

- `00 Control`: checkpoint and history files used by the tool.
- `01 Pending Review`: new review workbooks are created here.
- `02 Approved`: move reviewed workbooks here when ready to apply.
- `03 Completed`: successfully applied workbooks move here automatically.
- `04 Failed`: failed apply workbooks move here automatically.
- `99 History`: old plans, smoke tests, and history.

## Step By Step

1. Open PowerShell.

2. Go to the project folder:

```powershell
cd "C:\kwi-automations\projects\DuplicateMerging"
```

3. Create the next duplicate review batch:

```powershell
dotnet run --project "C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj" -- --plan-next
```

If a batch has no duplicate groups, the tool prints `No duplicate groups found in this batch` and does not create a blank workbook. The checkpoint still advances to the next AccountCode range.

4. Open the new Excel workbook in:

```text
C:\kwi-automations\projects\DuplicateMerging\01 Pending Review
```

5. Go to the `Actions` sheet.

6. Type `APPROVED` in column A for each row you want to run.

7. Save and close the workbook.

8. Move the workbook from `01 Pending Review` to `02 Approved`.

9. Apply all approved workbooks:

```powershell
dotnet run --project "C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj" -- --live --apply-approved
```

To retry failed rows after the first pass:

```powershell
dotnet run --project "C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj" -- --reset-failed-approved
dotnet run --project "C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj" -- --live --apply-approved
```

10. Check the result:

- Successful workbooks move to `03 Completed`.
- Failed workbooks move to `04 Failed`.

## Full Database Planning

Create one checkpointed planning batch:

```powershell
dotnet run --project "C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj" -- --plan-next
```

Create planning batches continuously until the configured AccountCode range is complete:

```powershell
dotnet run --project "C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj" -- --plan-all
```

Create planning batches through the next 200,000 account-code values:

```powershell
dotnet run --project "C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj" -- --plan-count 200000
```

Create one wide duplicate-matching workbook across the next 200,000 account-code values:

```powershell
dotnet run --project "C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj" -- --plan-wide-count 200000
```

Reset scan progress and start over:

```powershell
dotnet run --project "C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj" -- --reset-scan
```

## Interest To Market Segment Mapping

Create the starter mapping workbook:

```powershell
dotnet run --project "C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj" -- --init-segments-template
```

Fill `Segments.xlsx` with one row per interest code:

- `InterestCode`: Momentus interest/affiliation code.
- `MarketSegmentMajor`: target market segment major code.
- `MarketSegmentMinor`: target market segment minor code.
- `MarketSegmentCombined`: readable combined code, such as `PA`.
- `Skip`: `TRUE` for interest codes that should be ignored.

## What Approved Actions Do

- `CopyBlankAccountFields`: copies values from the duplicate account into blank fields on the survivor account. Existing survivor values are preserved.
- `MoveContactPrimaryAccount`: moves the contact to the survivor account, ensures the survivor `CTA` relationship exists, and removes the duplicate account `CTA` relationship.
- `InactivateDuplicateAccountWhenEmpty`: sets duplicate account `EventSalesStatus` to `I`, only if no contacts remain under it.

The tool does not delete duplicate organization accounts.

## Batch Size

The batch size is controlled in:

```text
C:\kwi-automations\projects\DuplicateMerging\appsettings.json
```

Setting:

```json
"PlanBatchSize": 1000
```

The recommended planning batch size is `1000`. Each completed slice advances the checkpoint, writes any duplicate review workbook, and records failed accounts or failed ranges in:

```text
C:\kwi-automations\projects\DuplicateMerging\00 Control\failed_duplicate_scan_items.tsv
```

Smaller batches create more workbooks, but they are much easier to recover from if Momentus or the network drops a request.

## Worker Count

Account inspection uses this setting:

```json
"MaxConcurrentScanAccounts": 3
```

Use `3` as the normal starting point. If Momentus starts throwing more HTTP or timeout errors, lower it to `2` or `1`. If it runs cleanly, you can cautiously test `4`.

Approved apply uses this setting:

```json
"MaxConcurrentApplyRows": 10
```

Copy-field and contact-move rows run with workers. Inactivation rows wait until those are done so duplicate accounts are not checked before their contacts move.

If an approved row fails after retries, the row is marked `FAILED` with the error message and the apply run continues.
