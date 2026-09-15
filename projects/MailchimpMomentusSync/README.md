# Kallman Mailchimp to Momentus Sync

Release **3.3.0** imports one or more raw Mailchimp engagement CSVs into the existing
exhibitor/activity/note workflow and creates a Momentus Event Sales campaign as
the final write phase for each queued file.

## Workflow

1. Use the first row to select a raw Mailchimp CSV.
2. Enter that row's **Campaign** name, **Campaign Sent Date**, four-digit Event
   ID, **Campaign Type**, and **Salesperson**.
3. Review the row's source, engaged-contact, and clicked-recipient counts.
4. Select **+ Add another file** for every additional campaign and complete the
   same columns. New rows inherit the previous Event ID and Salesperson.
5. Select **Run Queue**, review the full live-write summary, and confirm.

Queued files run sequentially in the displayed order. Each file gets a separate
run and audit folder. If one file fails, later rows do not start.

## Raw Mailchimp headers

Column order does not matter. Required headers are:

- `email`
- `first_name`
- `last_name`
- `opens`
- `clicks`
- `account_code`

Only rows that opened or clicked, have an `account_code`, and resolve to a
Momentus contact are added to the campaign population. No-interaction rows are
ignored for campaign details, but remain included in Emails Sent and
response-percentage calculations. An engaged contact with no Primary Account
remains a campaign contact but is skipped for organization-dependent work. A
contact account code not found in Momentus is skipped and identified in the
audit CSV. The existing exhibitor, activity, and engagement-note workflow remains click-only.
Extra Mailchimp columns are ignored.

## Momentus campaign behavior

The campaign is created last, after the exhibitor, activity, and note work.
It uses Event Sales designation `C` and these values:

| Momentus field | Value |
|---|---|
| Description | Campaign entered in the launcher |
| Event | Event ID entered in the launcher |
| Coordinator | Selected salesperson account code |
| Emails Sent | Total nonblank rows in the Mailchimp export |
| Email Response Percentage | Rounded whole-number percentage of source rows with opens or clicks |
| Summary | Campaign, sent date, and emails-sent count |
| Start Date | Selected Mailchimp sent date |
| End Date | Import date |
| Group | Blank |

The campaign population contains one detail for every engaged contact. Related
exhibitor organizations are used for the exhibitor/activity/note workflow but
are not campaign details, so OPE and CLI count people. Status is assigned with
click as the strongest signal:

| Code | Meaning | Rule |
|---|---|---|
| `OPE` | Opened | Opens > 0 and Clicks = 0 |
| `CLI` | Opened and Clicked | Clicks > 0 |

A click is classified as `CLI` even if Mailchimp reports zero opens, because a
click is the stronger engagement signal. “Followed Up” is not assigned by the
import because the Mailchimp file has no follow-up field; it remains available
for staff to set in Momentus afterward.

Before any writes, the app validates the engaged campaign contact account codes and
checks read access to the campaign endpoints. If an exact campaign already
exists for the same event, campaign name, and sent date after a partial failure,
the rerun reuses it and adds only missing details. Existing detail rows are not
overwritten, preserving manual follow-up changes. Existing details are loaded
across all API result pages, including campaigns with more than 1,000 details.
If Momentus rejects a stale organization account during detail creation, that
one detail is skipped and the remaining valid details continue.

An engaged contact without a Primary Account remains a campaign contact. Only
the organization-dependent exhibitor, activity, and note work is skipped.

The progress window displays six phases plus live contact, exhibitor, and
campaign-detail counts, the current organization account, and recent log lines.

## Official Campaign Type catalog

| Campaign Type | Open | Click |
|---|---|---|
| Launch Email | LMO | LMC |
| Sponsorship Mailing | 1SO | 1SC |
| Portfolio Mailing | PMO | PMC |
| Blast email #1 | CO2 | CC2 |
| Blast email #2 | 6MO | 6MC |
| Blast email #3 | 1YO | 1YC |
| Blast email #4 | 3MO | 3MC |
| Blast email #5 | CMO | CMC |
| Last call mailer | LCO | LCC |

## Notes behavior

The generated internal import file places the user-entered **Campaign** text in
column B and the selected sent date in column M. Momentus engagement note titles
and campaign-block headings use this format:

`SOFEX 2027 Last Call - Sent on August 21, 2026`

The contact lines below that heading keep the existing format, such as
`Scott Beal - clicked 10 times, opened 2`.

Activity subjects use the same campaign-and-sent-date title instead of internal
codes such as `LCC - Click`. Activity text clearly states both counts, for
example: `SOFEX 2027 Last Call - opened 2 times, clicked 10 times. Sent on
August 21, 2026.` Campaign Type still supplies the underlying Momentus activity
codes.

## Manage lists

Use **Manage Lists** in the main app or `Manage Dropdown Lists.cmd` to edit:

- Campaign Types
- Click activity codes
- Open activity codes
- Salespeople
- Salesperson account codes

When upgrading from an older saved catalog, release 3.3.0 keeps the saved
salesperson list and replaces the older Campaign Type list with the new official
2.4 catalog automatically.

User-managed mappings are stored at:

`%LOCALAPPDATA%\Kallman\MailchimpMomentusSync\ImportMappings.json`

## Credentials and run data

Saved Momentus API credentials remain under the user's Windows profile and are
not replaced by copying this project update.

Each run is stored under:

`%LOCALAPPDATA%\Kallman\MailchimpMomentusSync\runs`

The existing 2.3 completion-status fix is retained. The launcher can confirm a
successful engine run from the engine summary when Windows does not expose a
usable process exit code.
