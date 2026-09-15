# KWI Booth Availability Sync

Reads the Momentus booth warehouse CSV and updates booth inventory metrics on the SharePoint list **Event Portals and Links**.

## Source

`C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents\Booths\_Pull.csv`

## Match key

- CSV: `Event`
- SharePoint: `Event ID`

Booths are deduplicated by **Event + Booth** before any metric is calculated. If multiple CSV rows exist for the same Event + Booth, the row with the newest `ChangedOn` wins. `SequenceNumber` is the tie-breaker when `ChangedOn` is identical.

## Metrics

- **Available Booths** = count of current booth records with `BoothStatus = AV`
- **Available Area** = sum of `GrossArea` for `AV`
- **Sold Booths** = count with `BoothStatus = RE` or `30`
- **Area Sold** = sum of `GrossArea` for `RE` or `30`
- **Booths on Hold** = count with `BoothStatus = 20`
- **Booth Availability Last Updated** = UTC timestamp of the successful SharePoint processing attempt for each matched event

## SharePoint

- Host: `kallmanworldwideinc.sharepoint.com`
- Site: `/sites/IT`
- List: `Event Portals and Links`

The program discovers SharePoint internal field names from the configured display names. You do not need to hard-code names such as `Event_x0020_ID`.

## Run

### Safe preview

Double-click:

`preview.bat`

Preview reads the CSV and SharePoint list and prints exactly which booth metrics differ. It does not update SharePoint.

### Live update

After preview looks correct, double-click:

`sync.bat`

## Safety behavior

The sync stops before SharePoint writes when:

- the CSV is missing or empty;
- required CSV columns are missing;
- the CSV has fewer than the configured minimum rows;
- the file is older than the configured limit;
- unique Event + Booth inventory drops more than the configured percentage versus the last successful run;
- available booths unexpectedly drop from a non-zero baseline to zero; or
- sold booths unexpectedly drop from a non-zero baseline to zero.

A preview still shows results when a safety rule fails, but clearly warns that live sync would stop.

## SharePoint mismatch behavior

- CSV Event ID not found in SharePoint: skip and log.
- SharePoint Event ID not found in CSV: leave unchanged and log.
- Duplicate Event ID in SharePoint: update neither duplicate and mark the run partial.
- One SharePoint update fails: continue other events, log the failure, and do not advance the safety baseline.

## Authentication

See `AUTH-SETUP.md` for the initial laptop configuration.

Laptop testing uses `InteractiveBrowser`. Server production will use `Certificate` authentication later without changing the CSV or SharePoint logic.

## State and logs

- `state\last-success.json` stores the last fully successful CSV baseline used by safety checks.
- `state\logs\` contains timestamped run logs.
