# ServiceOrderEntry every 15 minutes

## Behavior

The Windows task `Kallman Momentus Service Order Entry` polls all events every 15 minutes. Live mode enters eligible ME/35/PC orders and sends ready-for-invoicing emails to MiranaC@kallman.com and LindsayH@kallman.com, with kylep@kallman.com in CC. The email begins with the early-development notice. Verified service orders become Active (A); exhibitors become Active (2) after all scoped pending orders and unfinished journaled work resolve. Hold, Approval Needed, billing/contract validation and uncertain-write safeguards still apply. A review is not permission to repeat an email or guess missing data.

Older journals with recorded mutation stages retain their original recipients and activation options. Different activation options require REVIEW before any additional write. A placeholder with no mutation stages can use the freshly evaluated current policy. Already accepted emails are never resent merely because recipients or the notice changed. Preserve all state when updating the task to a new release.

The ten-order allowance counts orders that dispatch a write, once per order. Completed PC orders and unresolved read-only recovery do not consume it. Confirmed rejected or uncertain dispatched writes do. Repeated polling reuses verified journals instead of repeating mutations. Deferred planned stages remain recoverable.

The runner uses a bounded `--max-results 1000000` ceiling. The former 100000 ceiling was exceeded by a real global contact decision search. Raising the configured ceiling does not accept partial searches or authorize any billing decision; searches that still exceed it fail closed. No live recheck of that contact search has been claimed by this scheduling change.

## Package and installation

Build from the source folder:

```powershell
& 'C:\kwi-automations\momentus\ServiceOrderEntry\Build-Release.ps1'
```

The build tests the project and creates a new immutable, self-contained win-x64 package. It never replaces an earlier release. Copy the entire package to a permanent local disk folder on the computer that will execute the task. No .NET runtime installation is needed for this package. All three scripts, both lookup workbooks and `billing-config.json` must remain beside the executable.

Keep credentials only in these **machine environment variables** on that computer: `MOMENTUS_APIUSER`, `MOMENTUS_SECRET`, `MOMENTUS_KEY`. The runner refreshes them into its own process without printing or writing their values. A credential's presence is not proof of successful Momentus authentication; the first API run supplies that evidence.

When moving this automation between computers, preserve its canonical journal folder separately: `%ProgramData%\Kallman\ServiceOrderEntry\state`. Stop competing runs before taking/copying a consistent state snapshot. Do not overwrite existing target journals, clear unknown stages, or infer completed processing from CSVs alone. If journals are not migrated, existing saved emails can require REVIEW rather than being resent. The release package and installer never migrate or delete journals automatically.

Open PowerShell **as Administrator on the executing computer**, change to the permanent package folder, and run:

```powershell
.\Install-ServerTask.ps1 -EnableLiveUpdates
```

Omit `-EnableLiveUpdates` for scheduled preview. `-ValidateOnly` validates package, machine credential presence and proposed task settings without installing or contacting Momentus. An existing task is not replaced unless `-ReplaceExistingTask` is explicitly supplied; replacement preserves a dated XML backup under `%ProgramData%\Kallman\ServiceOrderEntry\task-backups` and refuses while the existing task is running.

Before enabling the recurring task, the installer registers and runs a temporary local-only SYSTEM probe. It verifies configuration, lookups, existing journal validity, credentials present, runtime startup, core lock and durable state/report storage. It makes no Momentus requests. Its task is removed after verification; its logs are preserved. The recurring task's first run begins approximately two minutes after installation.

## Task settings and verification

- Principal: SYSTEM, ServiceAccount, Highest. The installer must run elevated.
- Trigger: once, repeating indefinitely every 15 minutes; no repetition duration or end boundary.
- Overlap: IgnoreNew, plus the executable's canonical machine-wide run lock.
- Execution time limit: unlimited; hard termination is disabled. An in-flight write is not killed at the next timer tick. If a run takes longer than 15 minutes, later ticks skip it. Investigate unusually long runs using logs before intervention.
- Retries: the next 15-minute poll handles the next run. No immediate Task Scheduler failure retry is enabled.
- Action: the absolute Windows PowerShell host and absolute packaged `Run-Scheduled.ps1`, with hidden/noninteractive execution. Never `dotnet run` or a mutable source checkout.

Microsoft documents [IgnoreNew](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-multipleinstancespolicy-settingstype-element), [indefinite repetition without a duration](https://learn.microsoft.com/en-us/windows/win32/taskschd/repetitionpattern-duration), and [the unlimited execution time setting](https://learn.microsoft.com/en-us/windows/win32/taskschd/tasksettings-executiontimelimit).

Inspect the installed task:

```powershell
.\Verify-ServerTask.ps1
Get-ScheduledTaskInfo -TaskName 'Kallman Momentus Service Order Entry'
```

The verifier checks the registered XML interval, duration, SYSTEM principal, overlap, time limit and exact package action. It reports actual LastRun/NextRun/LastTaskResult and distinguishes an unrun or running task from success. `0x00041301` means running, not successful completion. Expected application codes: 0 SUCCESS, 1 FAILED, 2 REVIEW/deferred work, 3 UNKNOWN write outcome. Preserve nonzero codes for investigation; do not mask them as successful runs.

Logs: `%ProgramData%\Kallman\ServiceOrderEntry\operations\logs\scheduled-*.log`.
CSV reports: `%ProgramData%\Kallman\ServiceOrderEntry\operations\runs\`.
Latest launcher summary: `%ProgramData%\Kallman\ServiceOrderEntry\operations\last-run.json`.
Recovery authority: `%ProgramData%\Kallman\ServiceOrderEntry\state\orders\`.

Verify the first actual run's completed scheduler result, log, CSV and canonical journals before calling the deployment operational. A successful local probe alone is insufficient. The setup does not send extra failure notifications or install an external monitor.

## Stop and rollback

Disable future triggers without terminating an in-flight run:

```powershell
Disable-ScheduledTask -TaskName 'Kallman Momentus Service Order Entry'
```

Wait for the current run to finish before changing releases. Preserve its logs and canonical journals. To restore a previous registered task, inspect the dated `task.xml` backup and register that exact XML under the same task name. Do not move/delete old packages or roll back journal evidence. Restoring older processing options may require REVIEW for journals created with different options.
