# KWI Event Portal SharePoint Sync

This utility keeps the SharePoint **Event Portals and Links** list synchronized from two Kallman data-warehouse CSV exports.

## Sources

### Booths_Pull.csv
Matched by:

- CSV `Event` -> SharePoint `Event ID`
- Unique booth key = `Event + Booth`
- If duplicate booth rows exist, the row with the newest `ChangedOn` is used.

Updates:

- `Available Booths` = count where `BoothStatus = AV`
- `Available Area` = sum `GrossArea` where `BoothStatus = AV`
- `Sold Booths` = count where `BoothStatus = RE` or `30`
- `Area Sold` = sum `GrossArea` where `BoothStatus = RE` or `30`
- `Booths on Hold` = count where `BoothStatus = 20`
- `Booth Availability Last Updated` = refreshed for matched booth events

### Events_Pull.csv
Matched by:

- CSV `EventID` -> SharePoint `Event ID`

Updates:

| Events_Pull.csv | SharePoint |
|---|---|
| `StartDate` | `Start Date` |
| `EndDate` | `End Date` |
| `EventUserFieldSets[0].UserText10` | `City` |
| `EventUserFieldSets[0].UserText11` | `Country` |
| `Class` | `Subclass` |

Blank values in Events_Pull.csv intentionally clear the corresponding SharePoint field so SharePoint mirrors Momentus.

## Safety behavior

- Preview never writes to SharePoint.
- Sync stops if either CSV is missing, stale, malformed, or fails configured minimum-data checks.
- SharePoint items are never created automatically.
- Duplicate SharePoint Event IDs are skipped and flagged.
- Events in the CSV but not in SharePoint are logged and skipped.
- SharePoint events missing from a source CSV are logged and left unchanged for that source.
- Only changed fields are written, except `Booth Availability Last Updated`, which can be refreshed for every matched booth event.

## Running

Use:

- `preview.bat` to calculate and display changes without writing.
- `sync.bat` to apply changes.

For laptop testing, authentication uses the configured interactive Entra app registration. Server deployment can later switch to certificate-based unattended authentication without changing the sync logic.
