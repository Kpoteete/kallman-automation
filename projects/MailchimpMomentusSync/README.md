# Mailchimp to Momentus Sync

This utility imports one-event Mailchimp engagement CSVs into Momentus. It can
add event exhibitors, open/click activities, and exhibitor engagement notes.

The intended monthly workflow is:

1. Double-click `Run Mailchimp Sync.cmd`.
2. Select one or more CSV files.
3. Review the detected Event ID and usable engagement-row count.
4. Type `RUN` to authorize the live update.
5. Review the generated audit CSV in the results folder that opens.

See `Instructions.txt` for the exact CSV layout and end-user instructions.

## Safety and data handling

- The original source CSV is copied, never changed or moved.
- Each attempt uses an isolated folder under
  `%LOCALAPPDATA%\Kallman\MailchimpMomentusSync\runs`.
- The executable refuses to run without the launcher's explicit `--apply`
  argument.
- The launcher passes credentials to the child process only for the duration of
  the run and removes them from its environment afterward.
- Setup stores the Secret and Key using Windows user-bound PowerShell
  encryption. The credential file is outside the repository.
- An error retains that run's copied input and output for troubleshooting.

This is not transactional: some Momentus writes may have succeeded before a
later error. When a run fails, review its audit and console error before rerunning.

## Developer verification

```powershell
dotnet restore .\MailchimpMomentusSync.csproj --locked-mode
dotnet build .\MailchimpMomentusSync.csproj --no-restore
powershell -ExecutionPolicy Bypass -File .\Build-Release.ps1
```

`Build-Release.ps1` publishes a self-contained Windows x64 executable and
creates:

`C:\kwi-automations\artifacts\MailchimpMomentusSync\MailchimpMomentusSync-win-x64.zip`

The recipient does not need the .NET SDK. They extract the ZIP, run setup once,
then use the monthly launcher.
