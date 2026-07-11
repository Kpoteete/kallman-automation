# DuplicateMerging

Momentus duplicate organization-account review and contact consolidation tool.

## Official Workflow

1. Create duplicate merge plan workbooks into `01 Pending Review`.
2. Review a workbook and type `APPROVED` in column A for rows to run.
3. Move reviewed workbooks into `02 Approved`.
4. Run the approved queue.
5. Successful workbooks move to `03 Completed`; failed workbooks move to `04 Failed`.

Control/checkpoint files live in `00 Control`.

## Commands

Create the next checkpointed 1,000-account-code planning batch:

```powershell
dotnet run --project C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj -- --plan-next
```

Keep planning batches until the configured AccountCode range is complete:

```powershell
dotnet run --project C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj -- --plan-all
```

Keep planning checkpointed batches through the next 200,000 account-code values:

```powershell
dotnet run --project C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj -- --plan-count 200000
```

Create one wide duplicate-matching workbook across the next 200,000 account-code values:

```powershell
dotnet run --project C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj -- --plan-wide-count 200000
```

Apply every workbook currently in `02 Approved`:

```powershell
dotnet run --project C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj -- --live --apply-approved
```

Reset failed rows in approved workbooks back to `PROPOSED` for another apply pass:

```powershell
dotnet run --project C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj -- --reset-failed-approved
```

Approved apply rows use `BatchLimits:MaxConcurrentApplyRows` workers from `appsettings.json`. The default is `10`. Copy-field and contact-move rows run in parallel; duplicate-account inactivation rows run after those complete.
Any row-level apply error is written back to that row as `FAILED`, and the tool continues with the remaining approved rows.

Reset full-database scan progress:

```powershell
dotnet run --project C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj -- --reset-scan
```

Create the starter interest-code to market-segment mapping workbook:

```powershell
dotnet run --project C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj -- --init-segments-template
```

Create one non-checkpointed plan using `MaxAccountsToScanPerPlan`:

```powershell
dotnet run --project C:\kwi-automations\projects\DuplicateMerging\DuplicateMerging.csproj
```

## Approved Actions

- `CopyBlankAccountFields`: fills blank survivor account fields from the duplicate account.
- `MoveContactPrimaryAccount`: sets the contact `PrimaryAccount` to the survivor account, ensures the survivor `CTA` relationship, and removes the duplicate account `CTA` relationship.
- `InactivateDuplicateAccountWhenEmpty`: sets duplicate account `EventSalesStatus` to `I`, only if no contacts remain.

The tool does not delete duplicate organization accounts.

## Planning Failure Handling

Planning batches are saved and checkpointed in 1,000-account-code slices. If one account still fails after API retries, the account is written to `00 Control\failed_duplicate_scan_items.tsv`, skipped for that slice, and the scan continues. If a whole range cannot be pulled, the range is logged in the same file and the checkpoint advances to the next range.

Use `--plan-wide-count` when duplicate matching must happen across the whole requested AccountCode window instead of inside each 1,000-code slice. Wide mode still skips and logs account-level failures, but it advances the checkpoint only after the full wide workbook finishes.

Account inspection uses `MaxConcurrentScanAccounts` workers from `appsettings.json`. The default is `3`, which speeds up contact/interest lookups without hammering Momentus too hard. If Momentus starts returning more transient failures, lower it to `2` or `1`; if it is stable, try `4` cautiously.
