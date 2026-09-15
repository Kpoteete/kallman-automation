# Kallman BoothsPull

Daily/weekly Momentus booth warehouse pull for Organization **10**.

This project follows the same working pattern as Kallman's ExhibitorsPull:

- .NET 10
- `CsvHelper` 33.1.0
- `Ungerboeck.Api.Sdk` 1.254.1.4
- `Kallman.Automation.Core`
- shared `MomentusCredentials.FromEnvironment()` authentication
- atomic CSV publishing with a `.bak`
- JSON automation run summaries
- checkpoint-based incremental runs
- 10-minute checkpoint overlap by default

## What it does

### Full rebuild

`run-full.bat` asks Momentus for every booth in Organization 10 using the API-supported `All` search, follows every search page, creates a brand-new `Booths_Pull.csv`, and advances the successful-run checkpoint only after the new CSV has been published.

This is the mode intended for the weekly Task Scheduler job. Because the file is rebuilt from Momentus, booths that were deleted from Momentus disappear from the weekly output as well.

### Incremental

`run-incremental.bat` loads the existing CSV and requests booths changed since the last successful checkpoint. Changed/new booths replace or add rows in the existing data set.

The program subtracts the configured overlap window before searching. The default is 10 minutes, which protects against boundary timing issues.

If `Booths_Pull.csv` does not exist, an Incremental run automatically changes itself to a Full rebuild. This makes the first run simple.

## Runtime booth-model discovery

The Momentus example confirms the `Booths` endpoint supports `Search` and `Get`, but the exact property list lives in the installed `Ungerboeck.Api.Models` NuGet package.

To avoid maintaining a guessed list of booth columns, BoothsPull reads the public properties of `BoothsModel` at runtime:

- every top-level standard property becomes a CSV column;
- properties whose names contain `UserField` are deliberately excluded;
- simple values are written normally;
- any other standard complex value is written as compact JSON in its own column;
- the program tries to detect the booth sequence key automatically;
- the program tries to detect the booth change-date field automatically.

At startup, the console prints the detected key field, change-date field, and number of exported booth properties.

## Project location

Put the folder here:

```text
C:\kwi-automations\momentus\BoothsPull
```

The project reference is intentionally the same style as ExhibitorsPull:

```text
..\..\src\Kallman.Automation.Core\Kallman.Automation.Core.csproj
```

With the location above, that resolves to:

```text
C:\kwi-automations\src\Kallman.Automation.Core\Kallman.Automation.Core.csproj
```

## Step-by-step setup

### 1. Copy the project folder

Copy the entire `BoothsPull` folder to:

```text
C:\kwi-automations\momentus\BoothsPull
```

### 2. Confirm the Core project exists

Confirm this file exists:

```text
C:\kwi-automations\src\Kallman.Automation.Core\Kallman.Automation.Core.csproj
```

### 3. Confirm Momentus credentials

This project uses the same Core credential loader as ExhibitorsPull:

```csharp
MomentusCredentials.FromEnvironment()
```

Use the same working Momentus API environment variables already used by the other Kallman automations.

### 4. Restore packages

Open PowerShell in the BoothsPull folder:

```powershell
cd C:\kwi-automations\momentus\BoothsPull
dotnet restore
```

The expected direct packages are:

```text
CsvHelper 33.1.0
Ungerboeck.Api.Sdk 1.254.1.4
```

### 5. Run the first full pull

Double-click:

```text
run-full.bat
```

or run it from PowerShell:

```powershell
.\run-full.bat
```

The console should print lines similar to:

```text
-> Requested mode: Full
-> Organization: 10
-> Standard BoothsModel fields exported: ...
-> Detected key field: ...
-> Detected change field: ...
-> User-field properties: excluded
```

Then it will pull the booth data and publish the CSV.

### 6. Check the warehouse output

Default output location:

```text
C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents\Booths_Pull.csv
```

Other files created there:

```text
Booths_Pull.csv.bak
Booths_Pull.last_run.txt
logs\booths-<run-id>.json
```

The output folder can also be overridden with either:

```text
BOOTHS_OUTPUT_FOLDER
```

or the existing:

```text
KALLMAN_DATA_WAREHOUSE
```

`BOOTHS_OUTPUT_FOLDER` takes priority.

### 7. Test an incremental run

After a successful full pull, run:

```text
run-incremental.bat
```

The console should show the checkpoint and one OData date window at a time. Each search is paged, so the pull is not limited to the first API page.

If a supported BoothsModel change-date property is found, the filter will look like:

```text
<DetectedChangeField> ge datetime'2026-09-13' and <DetectedChangeField> lt datetime'2026-09-14'
```

The code then applies the exact checkpoint time after Momentus returns the day's records, matching the overlap-safe pattern used by the other Kallman pulls.

### 8. Schedule it

Recommended setup:

**Daily task**

```text
C:\kwi-automations\momentus\BoothsPull\run-incremental.bat
```

Run once each day.

**Weekly task**

```text
C:\kwi-automations\momentus\BoothsPull\run-full.bat
```

Run once each week after the normal daily pull window.

The weekly full rebuild is the reconciliation pass and removes locally retained records that no longer exist in Momentus.

## appsettings.json

Normal configuration:

```json
{
  "BoothsPull": {
    "DefaultMode": "Incremental",
    "OutputFileName": "Booths_Pull.csv",
    "RunStateFileName": "Booths_Pull.last_run.txt",
    "OverlapMinutes": 10,
    "ChangeField": "Auto",
    "KeyField": "Auto",
    "IncrementalFallbackToFullScan": true,
    "FullSearchFilter": "All",
    "SearchMaxResults": 500000,
    "SearchPageSize": 500,
    "ThrottleEveryRecords": 100,
    "ThrottleDelayMilliseconds": 500
  }
}
```

### Important settings

`OverlapMinutes`
: How far before the last successful run the incremental search begins. Keep this at 10 unless there is a reason to change it.

`ChangeField`
: `Auto` tells the program to inspect `BoothsModel`. If the console shows no detected change field but you learn the exact Momentus property name later, put that property name here.

`KeyField`
: `Auto` tries common Momentus sequence-number property names. If none is available, the program uses `Event + Function + Booth` as the unique row key, matching Momentus' own booth lookup requirement that booth name be paired with Event and Function.

`IncrementalFallbackToFullScan`
: If `true` and no change-date property is available, the daily Incremental BAT performs a full rebuild instead of silently missing booth changes.

`FullSearchFilter`
: Keep this as `All` for the weekly full rebuild. Momentus requires the literal `All` value when no OData filter is desired.

`SearchMaxResults`
: Sets Momentus' `$maxresults` search option. The default is 500,000 so the API will allow a large historical booth result set. Increase it only if Momentus reports that the total number of booths exceeds this value.

`SearchPageSize`
: Number of booth records requested per API page. The default is 500. BoothsPull follows the Momentus `Next` navigation link until all pages have been read.

`ThrottleEveryRecords` / `ThrottleDelayMilliseconds`
: Default is a 500 ms pause after every 100 processed records.

## BAT files versus command-line switches

No `dotnet run -- --full` syntax is required.

The BAT files set a temporary environment value only for that run:

```text
BOOTHS_PULL_MODE=Incremental
```

or:

```text
BOOTHS_PULL_MODE=Full
```

The application settings remain in `appsettings.json`.

## Safety behavior

The project is intentionally conservative:

- API/GET failures stop the run;
- a bad or missing booth key stops the run rather than corrupting the CSV;
- the existing CSV is not replaced until the new temporary CSV is complete;
- the previous CSV is retained as `.bak` by `AtomicFilePublisher`;
- the checkpoint is written only after the CSV is successfully published;
- a failed run writes a failed JSON run summary;
- duplicate booth search results are collapsed by the detected key or by `Event + Function + Booth`;
- full and incremental searches use Momentus paging rather than assuming one API response contains the entire result set.

## First-run validation checklist

After the first run, confirm:

1. The console detected a **key field** or explicitly says it is using `Event + Function + Booth`.
2. The console detected a **change field**.
3. `Booths_Pull.csv` contains the expected historical booth count.
4. Columns include the expected Momentus booth details and do not include booth UserField columns.
5. Run `run-incremental.bat` a second time and confirm it returns only a small changed set rather than rebuilding everything.
6. Confirm `Booths_Pull.last_run.txt` changes only after successful runs.
7. Confirm `Booths_Pull.csv.bak` is created after the output has been replaced at least once.

## If the change field is not detected

The application will print:

```text
-> Detected change field: (none)
```

With the provided settings, Incremental mode then runs a complete rebuild so it does not miss data.

If Momentus exposes a differently named change field, put its exact `BoothsModel` property name in `appsettings.json`, for example:

```json
"ChangeField": "ActualMomentusPropertyName"
```

Then rerun `run-incremental.bat`.

## Files in this project

```text
BoothsPull\
  Program.cs
  BoothsPull.csproj
  appsettings.json
  packages.lock.json
  run-incremental.bat
  run-full.bat
  README.md
```


## Search-result fallback for stale booth records

Momentus can occasionally return a booth in a search result that a later `GET` by `SequenceNumber` cannot retrieve. BoothsPull now treats this as a warning rather than a fatal error. It keeps the booth using the standard fields already returned by the search response, prints the affected sequence number, and continues processing the remaining booths. The final run summary reports how many search-record fallbacks were required.

During a full rebuild the console prints progress every 100 booths, including the number of successful full GETs and search-result fallbacks.
