# Events_Pull Upgrade

This version expands the existing BoothAvailabilitySync project rather than creating a second SharePoint project.

New source:

`C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents\Events_Pull.csv`

New SharePoint mappings:

- StartDate -> Start Date
- EndDate -> End Date
- EventUserFieldSets[0].UserText10 -> City
- EventUserFieldSets[0].UserText11 -> Country
- Class -> Subclass

Blank Event CSV values clear the corresponding SharePoint field.

Recommended first run:

1. Back up the current project folder.
2. Replace the source files with this package.
3. Keep any existing `state\last-success.json` file.
4. Confirm appsettings.json paths and ClientId.
5. Run `preview.bat`.
6. Review event-detail and booth changes.
7. Only then run `sync.bat`.
