# Event Calendar

This project now has an Excel-driven webpage.

1. Edit and save `outputs/event-calendar-template/event-calendar-template.xlsx`.
2. Run `.\start-calendar-server.cmd`.
3. Open the localhost URL printed in the terminal.

When the Excel workbook is saved, the watcher rebuilds `outputs/event-calendar-template/events.json`. The webpage checks that JSON every few seconds, so the open calendar updates automatically.

The webpage reads the `Schedule` sheet columns:

`Event Name`, `Date`, `Time`, `Location`, `Category`, `Description`, `Notes`, `Required Attendees`, `Speakers`

You can also rebuild the JSON once without starting the server:

```powershell
& "$env:USERPROFILE\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe" scripts\export_event_calendar_data.mjs
```
