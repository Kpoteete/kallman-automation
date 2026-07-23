# Kallman Automation

Internal Momentus data pulls, reviewed write workflows, reporting exports, and operational support tools.

## Main automations

- Accounts, Exhibitors, Service Orders, and Service Order Items pulls
- Account Import
- Accounts Data Integrity and Stale Account reports
- Duplicate Merging and Automatic Duplicate Merge
- Market Segment Application
- Registration List Automation
- Website Validation

## Repository rules

- Never commit credentials, local configuration, production output, logs, profiles, or checkpoints.
- Write-enabled tools default to dry-run or require an explicitly approved plan.
- Use branches and pull requests; keep `main` deployable.
- Production schedules run published Release artifacts, not `dotnet run` from source.

## Build and test

```powershell
dotnet restore Kallman.Automation.slnx
dotnet build Kallman.Automation.slnx --configuration Release --no-restore
dotnet test Kallman.Automation.slnx --configuration Release --no-build
python -m compileall -q projects
```

## Credentials

Momentus jobs use `MOMENTUS_APIUSER`, `MOMENTUS_SECRET`, and `MOMENTUS_KEY` environment variables. Never place their values in source or committed JSON.

## Production deployment

```powershell
pwsh .\scripts\publish-all.ps1
pwsh .\scripts\deploy.ps1 -ArtifactRoot .\artifacts\publish
pwsh .\scripts\verify-server.ps1
```

See `docs/operations/DEPLOYMENT.md`, `docs/architecture/ENGINEERING_STANDARD.md`, and each automation README before enabling a schedule or live writes.
