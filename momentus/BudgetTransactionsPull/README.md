# Momentus Budget Transactions Pull

Read-only extraction of Momentus `BudgetTransactions` into `Budget_Transactions_Pull.csv`.

- `full` performs a resumable historical build using `EnteredOn` windows.
- `incremental` searches both `ChangedOn` and `EnteredOn` with a 48-hour overlap, then upserts by `OrganizationCode|Batch|TransactionNum`.
- `probe` checks a date range without writing transaction data.

The pull is sequential and paced. Oversized API windows split automatically, completed full-build chunks are checkpointed, and warehouse files/checkpoints are published only after validation. Credentials are read only from `MOMENTUS_APIUSER`, `MOMENTUS_SECRET`, and `MOMENTUS_KEY`.

## Build and safe probe

```powershell
dotnet build .\BudgetTransactionsPull.csproj -c Release
dotnet run --project .\BudgetTransactionsPull.csproj -c Release -- probe --start 2026-09-28 --end 2026-09-29
```

## One-time historical build

```powershell
dotnet run --project .\BudgetTransactionsPull.csproj -c Release -- full
```

The default start is `1900-01-01`. Rerun the same command to resume after interruption. Use `--restart-full` only to archive the current incomplete staging state and start over.

Successful completion atomically publishes `Budget_Transactions_Pull.csv`, retains the prior full file under `_BudgetTransactionsPull_backups`, and advances `Budget_Transactions_Pull.last_run.txt` only after publication.

## Daily incremental

```powershell
dotnet run --project .\BudgetTransactionsPull.csproj -c Release -- incremental
```

The API search does not expose deletions. Run a periodic full reconciliation if deleted transactions must disappear from the warehouse.

## Publish and schedule

```powershell
dotnet publish .\BudgetTransactionsPull.csproj -c Release -r win-x64 --self-contained false -o .\publish
.\publish\BudgetTransactionsPull.exe incremental
```

`Run-Daily.ps1` runs the published executable and appends dated logs. `Install-ServerTask.ps1` installs the daily SYSTEM task after verifying all required machine-level environment variables. Schedule the published Release artifact, never `dotnet run` from the checkout.

No Momentus add, update, or delete endpoint is called.
