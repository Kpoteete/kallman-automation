# Momentus GL Datamill Pull

This project creates a read-only financial dataset for Datamill/Power BI. It exports
all nine GL sections exposed by the Kallman Momentus API, plus the journal, fiscal,
dimension, and revenue/cost tables needed to calculate revenue, expense, profit,
and margin by event, function, order, account, resource, department, and GL account.

## Files produced

GL configuration and dimensions:

- `GL_Account_Analysis_Codes.csv`
- `GL_Accounts.csv`
- `GL_Deferral_Revenue_Details.csv`
- `GL_Deferral_Revenue_Headers.csv`
- `GL_Distributions.csv`
- `GL_Main_Accounts.csv`
- `GL_Sources.csv`
- `GL_Space_Major.csv`
- `GL_Space_Minor.csv`
- `GL_Core_Dimensions.csv`
- `GL_Fiscal_Years.csv`
- `GL_Fiscal_Periods.csv`

Financial facts:

- `GL_Journal_Entries.csv`
- `GL_Journal_Entry_Details.csv`
- `GL_Daily_Revenue_And_Cost.csv`

`GL_Journal_Entry_Details.csv` contains debit/credit amounts and the event, order,
invoice, account, function, department, currency, and analytical dimensions.
`GL_Daily_Revenue_And_Cost.csv` is the simplest starting point for event/order
margin reporting because it contains both `RevenueAmount` and `DailyCostAmount`.

The live Kallman read-only verification on 2026-07-29 returned:

- 79,375 journal headers
- 410,585 journal details
- 256 GL account/subaccount combinations
- 178 GL distribution rules
- 24 GL sources
- zero Daily Revenue & Cost Analysis rows

Until Momentus populates Daily Revenue & Cost Analysis, build the Finance model
from `GL_Journal_Entry_Details.csv`, joined to `GL_Journal_Entries.csv` and
`GL_Accounts.csv`. Join existing event/order warehouse feeds through the `Event`,
`Order`, `OrderLine`, `Invoice`, and `Account` columns. Finance must confirm which
account types represent revenue versus expense and the correct amount sign
convention before publishing profit measures.

## Safety

- The program calls only SDK `Search` and `NavigateSearchList` methods.
- It contains no Momentus add, update, post, or delete operation.
- Credentials are read only from `MOMENTUS_APIUSER`, `MOMENTUS_SECRET`, and
  `MOMENTUS_KEY`.
- API concurrency is one and every request is paced.
- All selected datasets are built and reconciled in staging before any current
  Datamill export is replaced.
- Full rebuilds retain permanent timestamped pre-full exports under
  `_GLDataMillPull_backups`. Daily increments retain one rolling pre-incremental
  copy under `_GLDataMillPull_previous`, preventing unbounded daily storage growth.
- Existing unrelated warehouse files are never scanned, modified, or deleted.
- A warehouse lock prevents overlapping runs.

## Build and harmless probe

```powershell
cd C:\kwi-automations\momentus\GLDataMillPull
dotnet build .\GLDataMillPull.csproj -c Release
dotnet run --project .\GLDataMillPull.csproj -c Release -- probe
```

Probe mode reads row counts and writes no warehouse data.

Probe one dataset:

```powershell
dotnet run --project .\GLDataMillPull.csproj -c Release -- probe --dataset GLAccounts
```

## Full export

The default destination is:

`%KALLMAN_DATA_WAREHOUSE%\Momentus GL`

If `KALLMAN_DATA_WAREHOUSE` is not set, the existing Kallman warehouse default is
used. To test without touching the warehouse:

```powershell
$test = Join-Path $env:TEMP "GLDataMillPull-Test"
dotnet run --project .\GLDataMillPull.csproj -c Release -- full --output-folder $test
```

Production:

```powershell
dotnet run --project .\GLDataMillPull.csproj -c Release -- full
```

The run also publishes `GL_DataMill_manifest.json` with row counts, timestamps,
source endpoint names, organization, and extraction status.

## Daily checkpointed incremental

After the successful historical full run:

```powershell
dotnet run --project .\GLDataMillPull.csproj -c Release -- incremental
```

Each dataset has its own durable checkpoint under
`_GLDataMillPull_checkpoints`. The daily query starts 48 hours before that
checkpoint and filters on `ChangedOn`, which safely re-reads a small overlap.
Rows are upserted using the documented Momentus composite keys. A checkpoint is
advanced only after every selected API query and staged CSV succeeds.

`GLAccountAnalysisCodes` and `CoreDimensions` do not expose `ChangedOn`; they
currently contain only 12 and 2 rows, respectively, so incremental mode refreshes
those two small reference files in full. It does not re-pull the journal history.

The API does not provide a deletion feed through `ChangedOn`. Run a periodic full
reconciliation, such as quarterly, if source deletions must be reflected.

For a published daily executable:

```powershell
dotnet publish .\GLDataMillPull.csproj -c Release -r win-x64 --self-contained false -o .\publish
.\publish\GLDataMillPull.exe incremental
```

`Run-Daily.ps1` executes the published release and records dated logs. On the
production server, `Install-ServerTask.ps1` installs a non-overlapping daily
Windows Scheduled Task after verifying the required machine-level environment
variables. Do not schedule `dotnet run` from a changing source checkout.

## Power BI starting measures

If `GL_Daily_Revenue_And_Cost.csv` becomes populated:

```DAX
Revenue = SUM('GL Daily Revenue And Cost'[RevenueAmount])

Direct Cost = SUM('GL Daily Revenue And Cost'[DailyCostAmount])

Gross Profit = [Revenue] - [Direct Cost]

Gross Margin % = DIVIDE([Gross Profit], [Revenue])
```

Before publishing management reports, Finance should confirm the sign convention,
record statuses, and whether budget/forecast/unposted journal rows should be included.
