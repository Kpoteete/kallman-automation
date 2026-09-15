# KWI Account Import Launcher

Windows front end for the existing `AccountImport` console importer.

## Where to put it

Place the entire `KwiImportLauncher` folder inside:

```text
C:\kwi-automations\projects\AccountImport\Program\KwiImportLauncher
```

The launcher auto-detects the parent `Program` folder when it contains:

- `AccountImport.csproj`
- `appsettings.json`

You can also browse to another Program folder in the app.

## Run from source

From PowerShell:

```powershell
cd "C:\kwi-automations\projects\AccountImport\Program\KwiImportLauncher"
.\Run-Launcher.ps1
```

Or:

```powershell
dotnet run --project .\KwiImportLauncher.csproj
```

## Build a normal Windows EXE

```powershell
cd "C:\kwi-automations\projects\AccountImport\Program\KwiImportLauncher"
.\Build-Launcher.ps1
```

The EXE will be written to:

```text
KwiImportLauncher\publish\KWI Account Import.exe
```

This is framework-dependent, so the machine must have the .NET 8 Windows Desktop Runtime / SDK available.

## Safety behavior

- Dry run is selected by default.
- Live mode requires the in-app confirmation checkbox.
- Live mode launches the importer with both required arguments:
  - `--live`
  - `--confirm-production-writes`
- The launcher checks Phase 0 and warns unless exactly one `.xlsx` file is present.
- The existing importer remains the component that performs all Momentus API work.

## Settings

The launcher reads and writes the existing `Program\appsettings.json` file.

The Settings page is generated dynamically from the JSON structure. This means existing sections and newly added JSON settings appear without having to redesign the launcher.

It includes:

- General settings
- Momentus fields
- Duplicate checks
- Relationship settings
- Import ID settings
- Affiliation settings
- Code generation
- Country aliases
- Existing-account updates
- Tagging settings from the current `AppConfig` defaults
- Raw JSON editor for advanced configuration

## Run-time prompts

The Run page includes optional values for:

- Import ID
- Affiliation / interest code

When the console importer asks for these values, the launcher supplies them automatically. Leave a field blank to send a blank response and skip that prompt.
