# Service Order Booth Updater

Adds a booth number to eligible Momentus service orders from the latest accepted
Booth Proposal activity on the related exhibitor.

## Confirmed business mapping

- Activity `Type` is `BP` (Booth Proposal).
- Activity and service order must have the same `ExhibitorID`/`Exhibitor` and `Event`.
- Activity text must contain `accepted booth <value>, and had these comments`.
- Eligible order statuses are `A` (active) and `PC` (pending completion).
- The API target is `ServiceOrdersModel.BoothNumber` (Order Booth Number).

The newest matching activity by `EnteredOn` wins. The updater fills blank values
only. A different existing booth is logged as a conflict and never overwritten.

## Safety modes

Preview is the default and performs no writes:

```powershell
.\publish\ServiceOrderBoothUpdater.exe preview
```

To review one known exhibitor/event pair without scanning other records:

```powershell
.\publish\ServiceOrderBoothUpdater.exe preview --exhibitor 193914 --event 6208
```

Live writes require both apply mode and the exact confirmation switch:

```powershell
.\publish\ServiceOrderBoothUpdater.exe apply --confirm-update-booth-number
```

Credentials come only from machine/user environment variables:
`MOMENTUS_APIUSER`, `MOMENTUS_SECRET`, and `MOMENTUS_KEY`.

## Build and verify

```powershell
dotnet test .\tests\ServiceOrderBoothUpdater.Tests\ServiceOrderBoothUpdater.Tests.csproj -c Release
dotnet publish .\ServiceOrderBoothUpdater.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

Every run writes a JSON audit file under `state\logs`. Apply mode advances its
checkpoint only after a run with no reconciliation errors. A two-hour overlap
makes hourly runs resilient to late API visibility.

## Windows Task Scheduler

`Install-ServerTask.ps1` installs an hourly preview task by default. After a
reviewed preview, rerun it with `-EnableLiveUpdates` to replace the task with the
live form. The task uses the immutable executable in `publish`, not `dotnet run`.
