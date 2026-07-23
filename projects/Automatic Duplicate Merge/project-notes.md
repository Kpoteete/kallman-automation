# Automatic Duplicate Merge

## Purpose

Automate repetitive Momentus account merges from a workbook containing two columns:

- `Merge from`: the inactive/source account code to search.
- `Merge into`: the surviving account code to select in the merge dialog.

## Current Inputs

- Workbook: `merge accounts.xlsx`
- Sheet: `Sheet1`
- Rows found: 6,403 data rows
- Valid merge pairs found: 4,267
- Momentus URL: https://kallman.ungerboeck.com/prod/app85.cshtml

## Required Momentus Workflow

1. Open the Accounts area from the left global navigation.
2. Clear existing filters if the page opens with stale criteria.
3. Open Filters and enter the Merge From code only in the `Account Code` field.
4. Apply filters.
5. If there is no result, treat that pair as already completed and continue.
6. Select the result, open the context menu, and choose `Merge Accounts`.
7. Enter Merge Into in the Merge Into Account lookup.
8. Select the exact autocomplete result by account code.
9. Save and confirm with OK.

## Automation Design

- `momentus_merge_runner.py` reads `.xlsx`, `.xlsm`, `.csv`, and `.tsv` inputs.
- Playwright controls a visible Chrome window, so the process uses the normal Momentus UI and does not require an API.
- A persistent browser profile stores the login session after the user signs in once.
- Each run creates a timestamped folder under `runs/`.
- `progress.sqlite`, `progress.csv`, and `summary.json` are updated after every pair.
- Completed and skipped pairs are not repeated when resuming with `--ledger`.
- `--dry-run` searches and reports results without saving merges.

## Known Constraints and Risks

- Momentus has no account-merge API available for this workflow, so browser automation depends on accessible labels and UI behavior.
- The first run should use `--dry-run --limit 5` to verify the workbook and selectors.
- An exact Merge Into autocomplete match is required; the runner stops on ambiguity rather than guessing.
- The runner records unexpected errors, attempts to recover the Accounts grid, and continues; the process returns nonzero if any pair errors or requires human review.
- The user can stop the visible browser with Ctrl+C; progress already written to the ledger remains available.

## Next Validation Steps

1. Install `requirements.txt`.
2. Run a five-row dry run.
3. Confirm the account-code filter and autocomplete selectors against the current Momentus UI.
4. Run a small live batch, review `progress.csv`, then increase the limit in controlled batches.

## Pilot Results

- Date: 2026-07-10
- Command: `py -u momentus_merge_runner.py "merge accounts.xlsx" --dry-run --limit 5 --profile-dir .\\.pilot-profile2`
- Result: passed
- `00000423 -> 00224285`: skipped because no result was returned.
- `00000460 -> 00008240`: skipped because no result was returned.
- `00001227 -> 00015395`: found successfully in Momentus; no merge submitted.
- `00001520 -> 00214334`: found successfully in Momentus; no merge submitted.
- `00001539 -> 00103504`: found successfully in Momentus; no merge submitted.
- Important selector behavior: the Account Code filter fields are already visible on the Accounts page. Do not click `Filters` unless the Account Code field is absent, because clicking it can collapse the fields.
- Before each search, press `Clear All`, then fill Account Code and apply the filter. The runner now stops if the visible filter fields or Clear All control cannot be found; it does not collapse the filter panel automatically.
- Optional non-persistent login: set `MOMENTUS_USER` and `MOMENTUS_PASSWORD` in the process environment. The runner uses them only in memory and never writes them to files or progress logs.

## Live Validation

- Date: 2026-07-10
- Command: `py -u momentus_merge_runner.py "merge accounts.xlsx" --limit 3 --profile-dir .\\.auto-profile2`
- Result: passed end to end. `00000423` and `00000460` returned no rows and were skipped; `00001227 -> 00015395` was merged successfully and Momentus displayed Success.
- The result row is a virtual-grid wrapper whose child grid cells have the usable screen bounds. The runner selects the first grid cell, opens row actions with right-click, and falls back to Shift+F10 if needed.
- The Merge Into control is a generated-id plain text input inside the Merge Accounts dialog, not an ARIA searchbox. The runner now targets the visible dialog input and selects an exact account-code autocomplete match before saving.
