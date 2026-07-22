# Momentus Activities Pull

This is one read-only extractor with two production modes:

- `full`: a resumable, one-time historical build of `Activities_Pull.csv`.
- `incremental`: the daily overlap pull, deduplicated and upserted by the Momentus activity key (`OrganizationCode|Account|SequenceNumber`).

`probe` is a read-only count check for a small date range.

## Why the historical build does not overload Momentus

The full build queries `EnteredOn` in chronological windows with a strict `MaxResults` ceiling. When Momentus reports that a window exceeds that ceiling, the program immediately splits the date range in half and tries each half. Only one request is active at a time, every request is paced, and transient errors are retried with backoff. A busy day can therefore become hours or minutes instead of one request for tens of thousands of activities.

The Momentus SDK returns one page at a time even when `MaxResults` is higher than the page size. This extractor explicitly follows every SDK `Next` link and rejects any mismatch between the reported total and the rows actually received.

If a historical bulk import placed more than 2,000 activities in one minute, further splitting adds load without making the batch meaningfully safer. Only for that one-minute window, the extractor raises the guarded ceiling (default 100,000) while retaining sequential 250-row pagination and request pacing.

Every successful leaf window is written as an atomic CSV chunk and its cursor is checkpointed. If the process stops, rerunning `full` resumes at the last completed boundary. The existing warehouse CSV is not replaced until every historical chunk has been assembled and validated.

## Setup

PowerShell environment variables (already used by the other Momentus jobs):

```powershell
$env:MOMENTUS_APIUSER = "..."
$env:MOMENTUS_SECRET = "..."
$env:MOMENTUS_KEY = "..."
```

Optional:

```powershell
$env:KALLMAN_DATA_WAREHOUSE = "C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents"
```

Build and run a harmless one-day probe:

```powershell
dotnet build .\ActivitiesPull.csproj -c Release
dotnet run --project .\ActivitiesPull.csproj -c Release -- probe --start 2026-07-20 --end 2026-07-21
```

## One-time full build

The default start is `1900-01-01`, deliberately early enough to cover the complete system history:

```powershell
dotnet run --project .\ActivitiesPull.csproj -c Release -- full
```

Run the same command after any interruption; it resumes. `--restart-full` archives the incomplete state and chunks before beginning a new build. It does not silently delete them.

At successful completion:

- `Activities_Pull.csv` is atomically published.
- the replaced file is retained permanently under `_ActivitiesPull_backups` with a timestamped `before_full` name.
- `Activities_Pull.last_run.txt` is advanced only after publication.
- `_ActivitiesPull_staging` and `Activities_Pull.full_state.json` remain as audit/resume evidence.

## Daily incremental run

```powershell
dotnet run --project .\ActivitiesPull.csproj -c Release -- incremental
```

Incremental mode searches both `ChangedOn` and `EnteredOn`, with a default 48-hour overlap. This captures edits and newly added activities, then streams the existing CSV into a validated temporary file while applying key-based updates. The production file and checkpoint are replaced only after validation succeeds.

The API does not provide a deletion feed through this search pattern. Schedule a periodic full reconciliation (for example, quarterly) if deleted activities must also disappear from the warehouse.

## Scheduling

Publish the project to a fixed release folder, then have Windows Task Scheduler run the published executable daily:

```powershell
dotnet publish .\ActivitiesPull.csproj -c Release -r win-x64 --self-contained false -o .\publish
.\publish\ActivitiesPull.exe incremental
```

Do not schedule `dotnet run` from a changing source checkout. Configure the three Momentus variables and optional warehouse variable in the scheduled task's account/environment.

`Run-Daily.ps1` runs the published executable and appends a dated operational log under `logs`. The installed task should use a non-overlapping instance policy; the extractor's own warehouse lock is a second layer of protection.

For a production Windows server, use the self-contained deployment package and run `Install-ServerTask.ps1` from an elevated PowerShell window. The installer creates a normal Windows Task Scheduler task running as `SYSTEM`; it does not use Codex or ChatGPT. It fails closed unless all three Momentus credentials and `KALLMAN_DATA_WAREHOUSE` exist as machine-level environment variables.

## Safety behavior

- Credentials are environment-only and missing values fail closed.
- No Momentus add, update, or delete method is called.
- API concurrency is exactly one.
- An exclusive warehouse lock prevents overlapping full and incremental runs.
- Oversized windows are split as soon as Momentus returns its max-results signal.
- Every SDK page is followed and reconciled to the reported result count.
- A changed count during a window fails the run instead of accepting an incomplete snapshot.
- Blank or duplicate activity keys fail validation.
- CSV writes, state writes, publication, and checkpoints use temporary files or atomic replacement.
- Full-build backups are permanent timestamped archives; `Activities_Pull.previous.csv` is the rolling pre-incremental snapshot.
- Activity text is never written to logs.
