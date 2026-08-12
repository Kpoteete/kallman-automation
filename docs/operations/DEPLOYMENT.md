# Build, deployment, and rollback

Production scheduled tasks must run immutable published artifacts rather than `dotnet run` from a source checkout.

## Build

```powershell
pwsh .\scripts\publish-all.ps1
```

## Deploy

Copy the `artifacts\publish` folder to the server or run:

```powershell
pwsh .\scripts\deploy.ps1 -ArtifactRoot .\artifacts\publish -DestinationRoot C:\Automations\Published
```

Each deployment is stored under a timestamped `releases` folder. `current-release.txt` identifies the active release.

## Verify

```powershell
pwsh .\scripts\verify-server.ps1
```

## Schedule

Point Windows Scheduled Task at `pwsh.exe` with arguments similar to:

```text
-NoProfile -File C:\Automations\Repository\scripts\run-published-job.ps1 -JobName ExhibitorsPull -ExecutableName ExhbitorPull
```

Use a dedicated service account, prevent overlapping runs, capture nonzero exit codes, and configure task failure alerts.

## Rollback

Change `current-release.txt` to the prior release ID, then run the job manually once. Do not delete the failed release until diagnosis is complete.
