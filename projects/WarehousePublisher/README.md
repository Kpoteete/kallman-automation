# Warehouse Publisher

Publishes the private KWI data warehouse to a separate read-only consumer location.

This program is intended to run unattended. End users should not run it or work from the private warehouse.

## Purpose

The private warehouse remains the write target for Momentus, Asana, and other extraction jobs. WarehousePublisher copies supported data files into the consumer-facing published warehouse used by Power BI, reporting tools, and user-facing automation.

The publisher copies only supported data files that are new or changed. Existing good published files remain in place if a source file is unavailable, changing during a scan, or fails verification.

## Supported files

- .csv
- .xlsx
- .xls

Publication scope is deliberately narrow:

- Supported CSV/Excel files in the warehouse root are eligible.
- Asana\current is eligible recursively.
- Other warehouse subfolders are not scanned or published.
- Files ending in .previous.csv are skipped.

This keeps report history such as Data Integrity Reports private while still publishing the current warehouse datasets used by downstream tools.

Excel temporary files beginning with ~$ and hidden-style files beginning with . are also skipped.

## Configuration

Required environment variables:

- KALLMAN_DATA_WAREHOUSE: private source warehouse root
- KALLMAN_PUBLISHED_WAREHOUSE: published read-only warehouse root

Optional environment variable:

- KALLMAN_WAREHOUSE_PUBLISHER_STATE: state JSON path

If the state path is not supplied, the publisher uses C:\ProgramData\Kallman\WarehousePublisher\state.json.

The state file stores file metadata and SHA-256 hashes so the normal scan can avoid re-hashing and re-copying files that have not changed.

No credentials are required.

## Safe test

Preview with environment variables:

    dotnet run -c Release --project .\projects\WarehousePublisher\WarehousePublisher.csproj -- preview

Or provide paths explicitly:

    dotnet run -c Release --project .\projects\WarehousePublisher\WarehousePublisher.csproj -- preview --source "D:\Data Warehouse" --destination "D:\Published Data Warehouse" --state "C:\ProgramData\Kallman\WarehousePublisher\state.json"

Preview does not create or replace published files and does not update publisher state.

## Publish

    dotnet run -c Release --project .\projects\WarehousePublisher\WarehousePublisher.csproj -- publish

Production should use a published Release artifact rather than dotnet run.

## Safety behavior

- Source and destination cannot be the same folder.
- Source and destination cannot be nested inside each other.
- Each destination file is written to a temporary file first.
- The temporary copy is SHA-256 verified before it replaces the existing published file.
- If the source changes during hashing or copying, that file is deferred until the next run.
- Concurrent publisher runs are blocked by an exclusive lock.
- Missing source files are not deleted from the published warehouse.
- Historical/report subfolders are not scanned; only root-level datasets and Asana\current are eligible.
- Files ending in .previous.csv are ignored.
- Empty source files are refused and the existing published copy is retained.
- _warehouse_status.json is written to the published root after each non-preview run.

## Scheduling

The intended production cadence is every 1-2 minutes. A scan with no changes performs no data-file copies.

Before scheduling, confirm that the scheduled-task account can read the private warehouse, write the published warehouse, and write the publisher state location.

Do not assume SYSTEM can access a SharePoint/OneDrive synchronized user-profile folder. Use the service or user account that actually has access to both locations.

## Exit codes

- 0: success, no changes, deferred active source files, or another publisher run already active
- 1: configuration, scan, copy, verification, or publication error

## Deployment

Build a Release artifact with:

    pwsh .\projects\WarehousePublisher\Build-Release.ps1

The output is written to projects\WarehousePublisher\publish\, which is ignored by Git.
