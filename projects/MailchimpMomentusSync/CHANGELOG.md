# Changelog

## 3.3.0 - 2026-08-24

- Replaced the single-file launcher path with a multi-file queue grid.
- Added one row per campaign with File, Campaign Name, Sent Date, Event ID,
  Campaign Type, Salesperson, and CSV-review columns.
- Added Add another file and Remove controls; new rows inherit Event ID and
  Salesperson for faster entry while other values remain independent.
- Queued files run sequentially with separate run/audit folders and visible
  queue position. A failed file stops every later row before it starts.
- Added one confirmation summary for the complete live-write queue and one final
  batch result message.

## 3.2.0 - 2026-08-24

- Campaign details now contain engaged contacts only; exhibitor organizations
  no longer inflate OPE or CLI people counts.
- Engaged contacts without a Primary Account remain campaign details while
  their organization-dependent exhibitor work is skipped.
- Added a larger live progress window with six named phases, contact-resolution
  counts, current exhibitor progress, campaign-detail counts, and recent log
  output.
- For the validated SOFEX file, the intended campaign population is 811 people:
  747 OPE and 64 CLI, while Emails Sent remains 5,226 and response remains 16%.

## 3.1.1 - 2026-08-24

- Added full pagination when loading an existing campaign's details, including
  campaigns with more than Momentus's 1,000-row search page limit.
- Campaign-detail accounts rejected by Momentus as nonexistent are now skipped
  and logged instead of stopping the remaining detail recovery.
- Added account-specific processing messages and incremental audit persistence
  during exhibitor processing.
- Supports safe recovery of campaign 314 while preserving its existing details,
  activities, notes, and manual statuses.

## 3.1.0 - 2026-08-24

- Campaign details now include only contacts who opened or clicked and their
  related exhibitor organizations.
- No-interaction recipients are ignored for campaign details, eliminating
  thousands of unnecessary Momentus account lookups and detail records.
- Campaign Emails Sent and Email Response Percentage still use the complete
  Mailchimp send population, so campaign-level statistics remain accurate.
- The launcher preview, confirmation, run settings, and preflight audit now
  distinguish engaged campaign contacts from ignored no-interaction rows.

## 3.0.2 - 2026-08-24

- Momentus contact account codes that return `Account entry not found` are now
  skipped instead of stopping the import.
- Missing-account contacts are excluded from all writes and listed in the audit
  CSV separately from contacts that exist but have no Primary Account.
- Other Momentus lookup failures still stop safely before writes.

## 3.0.1 - 2026-08-24

- Contacts whose Momentus record has no Primary Account are now skipped instead
  of stopping the complete import.
- Skipped contacts are excluded from campaign details and from the
  exhibitor/activity/note workflow, and are identified in the audit CSV.
- Preflight failures that made no Momentus changes now show an accurate safe-to-
  rerun message in the launcher.

## 3.0.0 - 2026-08-24

- Added final-step creation of a Momentus Event Sales campaign for every import.
- Added all valid contacts and their exhibitor organizations as campaign details.
- Added campaign detail classification: `I` No Interaction, `OPE` Opened, and
  `CLI` Opened and Clicked.
- Kept the exhibitor/activity/note pipeline click-only while retaining all valid
  recipients for the campaign population.
- Added Event, Coordinator, Emails Sent, Email Response Percentage, Summary,
  Start Date, and End Date campaign values; Group remains blank.
- Added read-only campaign preflight before writes and partial-run recovery that
  resumes an exact campaign without overwriting existing manual detail statuses.

## 2.5.0 - 2026-08-24

- Added a Campaign Sent Date picker to the main import screen.
- Stored the selected Mailchimp sent date in prepared CSV column M and in each
  run's settings audit.
- Changed exhibitor note headings to `<Campaign> - Sent on <Month D, YYYY>`
  while preserving the existing per-contact engagement lines.
- Replaced internal-code-looking activity subjects with the campaign name and
  sent date.
- Changed activity text to clearly show the campaign, opened count, clicked
  count, and sent date.

## 2.4.0 - 2026-08-19

- Added a required free-text **Campaign** field to the main import screen.
- The Campaign value is now written into the internal prepared file and is used
  by the existing Momentus engine for the exhibitor engagement note.
- Campaign Type now serves only as the activity-code mapping selector.
- Replaced the official campaign/activity catalog with 9 approved mappings:
  Launch Email, Sponsorship Mailing, Portfolio Mailing, Blast email #1-#5, and
  Last call mailer.
- Added automatic migration for older user-managed catalogs: existing
  salesperson mappings are preserved while old Campaign Type mappings are
  replaced with the new official 2.4 campaign catalog.
- Retained the 2.3 blank-exit-code completion fix and Manage Lists editor.

## 2.3.0 - 2026-08-19

- Fixed the false **Sync needs attention** result when Windows PowerShell did not
  expose the child process exit code even though the engine summary reported a
  successful run.
- Added the **Manage Lists** editor for campaign/activity and salesperson mappings.
- Added JSON import/export, backups, and user-managed dropdown storage.

## 2.2.1

- Added the raw Mailchimp CSV workflow.
- Added header-based CSV parsing and click-only filtering.
