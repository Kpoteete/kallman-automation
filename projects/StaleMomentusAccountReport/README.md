# Stale Momentus Account Report

This .NET console tool queries Momentus/Ungerboeck accounts, service orders, and exhibitors, then writes an Excel workbook for stale-account cleanup.

The current stale-account pass is:

1. Pull candidate accounts that are older than 5 years, then find each account's most recent exhibitor record `EnteredOn` date. If that exhibitor date is missing or older than 6.1 years, the account is stale in phase 1.
2. Exclude phase 1 stale accounts, then check all active accounts for their most recent service order date. If that service order date is missing or older than 6.1 years, the account is stale in phase 2.

## Run a formatting dry run

```powershell
dotnet run --project src\StaleMomentusAccountReport -- --dry-run
```

The dry run creates `outputs\stale-accounts-yyyyMMdd.xlsx` with sample data so you can inspect the review columns, filters, dropdowns, and summary formulas without connecting to Momentus.

## Configure Momentus

Copy `src\StaleMomentusAccountReport\appsettings.example.json` values into `appsettings.json`, or set environment variables with the `STALE_` prefix:

```powershell
$env:STALE_Momentus__BaseUrl = "https://your-momentus-site.example.com"
$env:STALE_Momentus__OrganizationCode = "10"
$env:STALE_Momentus__ApiUserId = "api-user"
$env:STALE_Momentus__ApiKey = "api-key"
$env:STALE_Momentus__ApiSecret = "api-secret"
```

Update the account and contact mapping lists to match your Momentus codes for Active, Prospect, Company, Organization, and active contacts.

## Run live report

```powershell
dotnet run --project src\StaleMomentusAccountReport -- --org 10 --output outputs\stale-accounts.xlsx
```

Useful options:

- `--account-age-years 5`
- `--stale-activity-years 6.1`
- `--cutoff-years 6.1` as a back-compatible alias for stale activity years
- `--dry-run`
- `--help`

The tool does not edit Momentus. The workbook is a cleanup tracker for later human review and correction.
