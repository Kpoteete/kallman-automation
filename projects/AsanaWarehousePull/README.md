# Kallman Asana Warehouse Pull

Read-only Asana extractor for producing a normalized CSV warehouse suitable for Excel, Power BI, and a future Momentus integration.

Canonical source location: `C:\kwi-automations\projects\AsanaWarehousePull`.

The program only constructs `HttpMethod.Get` requests. It does not contain create, update, complete, delete, or move operations.

## First target

- Workspace: `kallman.com` (`1201060253118530`)
- Project: `IT ROADMAP` (`1210048713791963`)

## Credential setup

Use the rotated Personal Access Token through the `ASANA_PAT` environment variable. Do not put it in this repository.

```powershell
[Environment]::SetEnvironmentVariable('ASANA_PAT', 'paste-rotated-token-here', 'User')
```

Close and reopen the terminal after setting it. Confirm presence without displaying the secret:

```powershell
[bool][Environment]::GetEnvironmentVariable('ASANA_PAT', 'User')
```

## Run modes

Check workspace access and count active projects without publishing CSVs:

```powershell
dotnet run -c Release -- --mode probe
```

Build the warehouse from all active projects:

```powershell
dotnet run -c Release -- --mode full
```

Include archived projects in a historical full build:

```powershell
dotnet run -c Release -- --mode full --include-archived
```

Pull only the default `IT ROADMAP` test project:

```powershell
dotnet run -c Release -- --mode project
```

Running with no arguments performs the normal active-project full warehouse build.

## Publish for the server

Build the self-contained Windows release:

```powershell
pwsh .\Build-Release.ps1
```

This creates `publish\AsanaWarehousePull.exe`. Server scheduling is intentionally deferred until the project is copied to the server and its execution account, credential scope, warehouse path, schedule, and retention policy are confirmed.

## Server Task Scheduler setup

The source repository includes everything needed to install the daily task, but the installer does not run automatically.

1. Copy or clone this project to its permanent server path.
2. From an elevated PowerShell window, configure machine-level variables for the `SYSTEM` task. `KALLMAN_DATA_WAREHOUSE` is the warehouse root; the runner adds the `Asana` subfolder.

```powershell
[Environment]::SetEnvironmentVariable('ASANA_PAT', 'paste-rotated-token-here', 'Machine')
[Environment]::SetEnvironmentVariable('KALLMAN_DATA_WAREHOUSE', 'D:\Data Warehouse', 'Machine')
```

3. Build the self-contained release and test the read-only probe:

```powershell
pwsh .\Build-Release.ps1
pwsh .\Run-Daily.ps1 -Mode probe
```

4. Preview and then install the daily task:

```powershell
pwsh .\Install-ServerTask.ps1 -DailyAt '06:00' -WhatIf
pwsh .\Install-ServerTask.ps1 -DailyAt '06:00'
```

The installed task runs as `SYSTEM`, refuses to install if either machine variable is absent, ignores overlapping launches, and writes operational logs under `logs\`. `Run-Daily.bat` is included as a convenient manual full-run launcher; Task Scheduler uses `Run-Daily.ps1` directly so exit codes and paths are handled consistently.

Optional overrides:

```powershell
dotnet run -c Release -- --workspace 1201060253118530 --project 1210048713791963 --output output
```

## Outputs

The default warehouse destination is:

`C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents\Asana`

Each successful run creates an immutable timestamped folder under `Asana\runs\` and publishes the latest validated CSVs to `Asana\current\`:

- `Asana_Projects.csv`
- `Asana_Project_CustomFields.csv`
- `Asana_Sections.csv`
- `Asana_Project_Status_Updates.csv`
- `Asana_Tasks.csv`
- `Asana_Task_Memberships.csv`
- `Asana_Task_CustomFields.csv`
- `Asana_Task_Tags.csv`
- `Asana_Users.csv`
- `Asana_Workspace_Memberships.csv`
- `Asana_Teams.csv`
- `Asana_Team_Memberships.csv`
- `Asana_Project_Members.csv`
- `Asana_Project_Followers.csv`
- `Asana_Task_Followers.csv`
- `Asana_Task_Dependencies.csv`
- `Asana_Task_Dependents.csv`
- `Asana_Task_Attachments.csv`
- `Asana_Portfolios.csv`
- `Asana_Portfolio_Items.csv`
- `Asana_Portfolio_Memberships.csv`
- `Asana_Portfolio_CustomFields.csv`
- `Asana_Portfolio_Status_Updates.csv`
- `Asana_Goals.csv`
- `Asana_Goal_Followers.csv`
- `Asana_Goal_CustomFields.csv`
- `Asana_Goal_Relationships.csv`
- `Asana_Goal_Status_Updates.csv`
- `manifest.json`

Raw JSON pages are retained inside the timestamped run for traceability. The local `output/` folder remains excluded from Git and is only used when `--output` explicitly points there for development testing. Workspace tasks are deduplicated by task GID; use `Asana_Task_Memberships.csv` for the many-to-many relationship between tasks, projects, and sections. Milestones and approvals are identified by `ResourceSubtype` in `Asana_Tasks.csv`; approval tasks also expose `ApprovalStatus`.

`Asana_Project_Status_Updates.csv` contains the complete status-update history visible to the token for the selected projects. Dependency and dependent tables may reference task GIDs outside the active-project warehouse when an active task is linked to a task in an archived, private, or otherwise out-of-scope project. In that case the related task name is blank, but the GID is retained for a future historical or expanded-scope join.

Attachment output is metadata only. The extractor does not download attached files and deliberately omits Asana's short-lived `download_url`; it retains the stable authenticated `PermanentUrl` and any available external `ViewUrl`.

Asana requires an `owner` when listing portfolios. With a Personal Access Token, portfolio output therefore covers portfolios owned by the authenticated user, plus their visible items, members, custom-field settings, and status history. It must not be interpreted as a complete inventory of portfolios owned by other workspace users.

Goal output includes goal ownership, dates, status, metrics, time-period values, followers, custom-field values, supporting-work relationships, and status-update history visible to the token. Empty goal CSVs are still published with stable headers when the workspace has no visible goals.

## Safety boundary

- Authentication is read from memory at runtime.
- The token is never written to output or logs.
- Only HTTP GET is implemented.
- Full workspace runs use an exclusive lock to prevent overlapping publication.
- A failed pull does not replace the selected warehouse's `current` folder.
- Previous current outputs are retained as timestamped folders under the selected warehouse's `history` folder.
