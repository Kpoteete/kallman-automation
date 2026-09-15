# AI navigation guide

Use this guide to orient before changing a KWI automation. It reflects the current folder layout; it does not declare every folder production-deployed.

## Fast routing

| If the request concerns... | Start in... | Read next |
| --- | --- | --- |
| Momentus API extraction, rebuild, or update | `momentus/` | The automation's `README.md`, project file, and run/install scripts |
| A user-operated import, report, or desktop tool | `projects/` | The tool's `README.md` and its launcher/build scripts |
| Shared .NET behavior | `src/Kallman.Automation.Core/` | Corresponding tests in `tests/` |
| Build, publishing, deployment, or server validation | `scripts/` | `docs/operations/DEPLOYMENT.md` and `docs/operations/MONITORING_AND_RETENTION.md` |
| Architecture, standards, or inventory | `docs/architecture/` | `ENGINEERING_STANDARD.md` and `REPOSITORY_INVENTORY.md` |

## Top-level map

| Location | Purpose | Change caution |
| --- | --- | --- |
| `momentus/` | API-based pulls and operational workflows | Some tools can write Momentus. Verify mode and confirmation flags. |
| `projects/` | Business applications and end-user automations | Launchers and local data contracts may depend on exact paths. |
| `src/` / `tests/` | Shared code and automated tests | Prefer changes here only when behavior is truly shared. |
| `scripts/` | Cross-repository release and server tooling | Review parameter defaults before running. |
| `docs/` | Standards and operating instructions | Update when a maintained workflow changes. |
| `artifacts/`, `outputs/`, `logs/` | Generated packages, results, and evidence | Preserve; they are intentionally ignored by Git. |
| Root legacy folders | Earlier or standalone utilities | Do not relocate until callers and schedules are confirmed. |

## Current maintained areas

### Momentus jobs

- Read-only pulls and rebuilds include Accounts, Events, Exhibitors, Notes, Service Orders, Service Order Items, Activities, and GL data.
- `ServiceOrderBoothUpdater` is a guarded update workflow: use preview/readback before any apply mode.
- `Account_name_punctuation_and_email_cleanup` combines account-name, website, and contact-email cleanup. For historical name and website cleanup, use explicit `--start YYYY-MM-DD --end YYYY-MM-DD --bucket-days N` values; the job stops if a bucket reaches the API cap instead of silently missing records. Use `--account-names-only`, `--website-only`, or `--skip-contact-emails` to keep the contact-email job out of a run.
- `WebsiteCorrectionDaily` and account-cleanup utilities require explicit inspection of their write behavior before use.

### Business tools

- `AccountImport`, `DuplicateMerging`, and `MarketSegmentApplication` can change Momentus data; preserve their review/approval workflow.
- `MailchimpMomentusSync` performs non-transactional imports. Review run evidence before rerunning.
- `RegistrationListAutomation` is a file-based desktop workflow. Preserve its workbook history and local staging/locking behavior.
- Reporting tools such as Accounts Data Integrity and Stale Account Report should remain read-only against Momentus.

## Before making a change

1. Confirm the target folder and whether it has uncommitted work.
2. Find references to its path in launchers, install scripts, scheduled-task scripts, and documentation.
3. Make the smallest change that preserves existing input/output contracts.
4. Build and test only the affected project when possible.
5. For releases, distinguish build success, published-package success, scheduled-task installation, and verified production run.

## Known clutter to handle deliberately

`projects/` includes duplicate-looking and informal folder names such as `Automatic Duplicate  Merge`, `Automatic Duplicate Merge`, `New folder`, and `Working Account import`. These may be active local work. Do not consolidate them until their content, references, and owners have been reviewed.
