# Active Paid-in-Full Automation

Only main exhibitors (`ME`) are candidates. Co-exhibitors (`CO`) are excluded from the exhibitor search and can never be changed by this automation.

Promotes a Momentus exhibitor from **Active (2)** to **Active Paid in Full (22)** when it has an active order in an approved category with ordered net due of zero or less.

## Exact rule

- Exhibitor status is `2`.
- Service order status is `A`.
- The order category is one of the 19 approved category sequences below.
- `OrderedTotal > 0`.
- `NetDue <= 0`.

Payment amount does not participate in qualification. Zero-dollar and negative-total orders are excluded. The automation never reverses status 22 and never changes any exhibitor that does not begin in status 2.

When an exhibitor has an active approved-category booth order, that booth order takes priority over every other order. A paid-in-full booth order qualifies the exhibitor even if another order remains unpaid. An unpaid booth order blocks the status change even if another order is paid. Other approved-category orders are considered only when no booth order exists.

By default, only events whose end date is within the prior 31 days or later are included. This covers recently completed and all future events while excluding historical-show backlog. Use `--event-lookback-days N` only when a different reviewed window is required.

| Sequence | Order category |
| --- | --- |
| 39 | Additional Booth |
| 25 | Chalet |
| 21 | Co-Exhibitor |
| 19 | Commission on Direct Sales |
| 12 | Executive Club Membership |
| 24 | Executive Suites & Private Conference Rooms |
| 18 | Kiosk |
| 16 | Mini-Booth |
| 17 | Partnership Agreement |
| 15 | SME/SIDO/StartUp Zone |
| 27 | Space Only |
| 30 | Space Only - 2nd Story |
| 35 | Split Order - Space Only |
| 34 | Split Order - Turnkey |
| 22 | Sponsorship |
| 37 | Trade Accelerator |
| 38 | Trade Mission |
| 28 | Turnkey |
| 23 | Walk the Show Membership |

## Safety

Preview is the default and never writes to Momentus:

```powershell
.\publish\ActivePaidInFullAutomation.exe preview
```

Limit a preview to one exhibitor and event:

```powershell
.\publish\ActivePaidInFullAutomation.exe preview --exhibitor 12345 --event 6789
```

Live writes require both apply mode and the exact confirmation switch:

```powershell
.\publish\ActivePaidInFullAutomation.exe apply --confirm-active-paid-in-full
```

Apply mode is capped at 25 successful updates by default. Use `--max-updates N` only for an intentionally reviewed batch size. Preview is not capped by this setting.

Before every write, the order and exhibitor are retrieved again and requalified. After the update, the exhibitor is retrieved again and status 22 must be confirmed. Credentials are read only from `MOMENTUS_APIUSER`, `MOMENTUS_SECRET`, and `MOMENTUS_KEY`.

## Audit and weekly Excel output

Every run writes timestamped JSON below `state\logs`. Confirmed updates and review candidates are also saved as individual JSON records below `state\records`, then used to rebuild the weekly workbook atomically.

Each reporting week runs Saturday through Friday. The workbook is written to:

```text
reports\Ready-to-Register_YYYY-MM-DD_to_YYYY-MM-DD.xlsx
```

The **Ready to Register** sheet contains confirmed status changes only, including category and financial details. Each successful change is saved immediately as a durable JSON record; the Excel workbook is rebuilt once at the end of the run. Power Automate can send the workbook late Friday after the final scheduled run.

## Build and focused tests

```powershell
dotnet restore
dotnet test .\tests\ActivePaidInFullAutomation.Tests\ActivePaidInFullAutomation.Tests.csproj -c Release
dotnet publish .\ActivePaidInFullAutomation.csproj -c Release -r win-x64 --self-contained false -p:PublishDir=publish\
```

## Run dependency

This automation is not scheduled on an independent hourly cadence. `Run-After-Pulls.ps1` checks the two upstream Windows tasks and starts the automation only when:

- the Exhibitor Pull completed with result `0`;
- the Service Order Pull completed with result `0`; and
- both successful run times are newer than the last pair already processed.

The dependency watermark advances only after this automation exits successfully. A failed or still-running pull therefore cannot trigger a status update against a partially refreshed pair.

The production task names must be supplied explicitly during installation because they are server-specific:

```powershell
.\Install-ServerTask.ps1 `
  -ExhibitorPullTaskName '<actual exhibitor pull task name>' `
  -ServiceOrderPullTaskName '<actual service order pull task name>'
```

The installed coordinator checks every five minutes in preview mode by default. Add `-EnableLiveUpdates` only after a scoped preview and API write entitlement are verified. Task installation requires elevated PowerShell. A created package or successful preview is not a live deployment.
