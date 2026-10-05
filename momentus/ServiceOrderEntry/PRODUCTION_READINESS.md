# ServiceOrderEntry final production-readiness audit

## 2026-10-05 follow-up: Finance handoff and default activation

Newly evaluated live orders send the Finance email to MiranaC@kallman.com and LindsayH@kallman.com, with kylep@kallman.com in CC (the extra @ in the request was treated as a typo). The email begins with an early-development notice asking Finance to review the order details before invoicing. This notice is in the email saved against the service order; it does not change the managed Payment Schedule note.

Confirmed apply now enables both order Active (A) and exhibitor Active (2) transitions by default. Existing Hold/Approval and verified handoff/group gates remain required. Preview/probe do not activate anything. Exceptional skip switches can retain old recovery options. Journals with recorded mutation stages retain their original options and recipient evidence; mismatched activation options require REVIEW with no additional writes. Undispatched placeholders can use a fresh evaluation under the new policy. Email CC/BCC lists are included in durable intent and accepted-response corroboration whenever nonempty; old empty-copy intents retain their exact serialization shape.

The full updated suite passed **390/390** tests with zero failures/skips (`runs/finance-activation-checks/finance-activation-final.trx`). This includes two-To/CC/notice/default activation, invalid second-To or CC preflight rejection, CC accepted-response mismatch, no-resend recovery, multi-order activation, and upgrading an undispatched placeholder. All four operational scripts parsed and `git diff --check` passed. The new immutable self-contained release is published as `C:\kwi-automations\artifacts\publish\ServiceOrderEntry\2026-10-05-finance-active`. No live emails, status changes, or scheduler installation are part of this follow-up.

All 224 published file hashes matched the manifest. The adjacent ZIP was created without operational state/logs. The packaged Windows PowerShell 5.1 local probe returned 0 and preserved the canonical journal hashes; its log/summary are under `runs/finance-activation-checks/package-probe`. Packaged installer validation passed for LIVE/SYSTEM/PT15M/IgnoreNew/PT0S and explicitly installed no task. Executable help confirmed default activation and exceptional skip switches. The previous scheduler-ready release remains intact.

## 2026-10-05 follow-up: fifteen-minute scheduler preparation

The recurring-run allowance is now reserved at the first actual write dispatch for each order. Completed PC-order readback and unresolved read-only reconciliation cannot consume the allowance or starve later candidates. Confirmed rejected/unknown dispatched writes consume it; deferred stages remain Planned and recoverable. A local-only `probe` verifies billing configuration, lookup loading, credential presence and durable journal/report storage without constructing an API client or requesting Momentus.

The full current ServiceOrderEntry suite passed **384/384** tests with zero failures/skips; evidence is `runs/scheduler-readiness/full/scheduler-full.trx`. Six focused allowance/recovery checks passed beforehand. All four operational scripts parsed. Native Task Scheduler XML metadata verification passed for SYSTEM/live mode, interval PT15M, IgnoreNew overlap, unlimited execution time and disabled hard termination; tests also distinguished running from success and rejected an incorrect interval. Those XML checks used an unregistered in-memory task definition, not an installed task.

Final immutable self-contained package: `C:\kwi-automations\artifacts\publish\ServiceOrderEntry\2026-10-05-scheduler-ready`; ZIP: the adjacent `2026-10-05-scheduler-ready.zip`. All 224 published file hashes matched the manifest. The packaged installer passed `-EnableLiveUpdates -ValidateOnly` under Windows PowerShell 5.1. The packaged runner's local `-Probe` returned 0, loaded 18 sales reps, 5 categories and 8 existing journals, and verified credential presence, state/report storage and UTF-8 logs. Probe journal hashes were checked unchanged. Local probe evidence is preserved under ProgramData/Kallman/ServiceOrderEntry/operations; the XML verification fixture and output are under `runs/scheduler-readiness/script-verification`.

**Prepared and packaged; no recurring task installed or enabled.** The executing computer has not been specified, and this session is not elevated. No Momentus calls or live changes occurred during this scheduling preparation. The installer must run elevated on the executing computer; it performs a real temporary SYSTEM probe before registering the recurring task. A completed scheduled API run, its logs/CSV/journals and its final task result remain required before calling deployment operational. The one-million-result bounded search ceiling is configured for the runner but has not been verified against the previously failing global contact search. Existing legacy-email and business-data REVIEW decisions remain preserved. Previous release packages and all canonical journals remain intact.

## 2026-10-05 follow-up: all-event bulk scope

The user requested removal of the enabled-event restriction. New-order discovery and unfinished-journal recovery now include all events in the selected organization. The event-list loader, candidate/recovery allowlist gates, and package-copy rule were removed. Old `--enabled-events` commands fail with an explicit removal message; the historical file remains preserved. Explicit individual-exhibitor/event scope is still available. Eligibility, Hold/Approval rules, billing and search validation, canonical state, email recovery, and the ten-attempt bulk cap remain unchanged.

The updated full ServiceOrderEntry suite passed 379 tests with zero failures/skips; TRX evidence is in `runs/event-filter-removal/full/event-filter-full.trx`. A fresh immutable win-x64 framework-dependent Release package was published to `C:\kwi-automations\artifacts\publish\ServiceOrderEntry\2026-10-05-all-events`. Package checks confirmed the executable, both lookup workbooks, matching billing configuration, no copied event-list file, and successful executable help. No Momentus requests or live processing were performed for this follow-up. Earlier packages, run reports and canonical journals were preserved. The prior audit below records its own earlier verification scope.

Date: 2026-10-05. Scope: current ServiceOrderEntry source and offline regressions after Steps 5-7 implementation. The earlier production review was not treated as proof. No live Momentus calls, deployment, publication or push were performed.

## Findings corrected and rechecked

| Finding | Current evidence | Result |
| --- | --- | --- |
| Completed recovery could report READY or activate with stale final billing, instructions, items, notes or contracts | `HandoffRules.VerifyRetained`; `Runner.Apply`; `PlanGuard.ValidateRetainedIdentities/ValidateContracts` | Fresh exact-identity/content checks require REVIEW when retained evidence differs. Reused stages and IDs remain intact. |
| Completed shared activation lacked organization verification | `ActivationRules.Check`; `MomentusGateway.GetExhibitorOrders` | Organization/event/exhibitor/order identity is checked, including recovery. |
| Hold/Approval or unmanaged categories added during final group checks could be overwritten by a stale activation payload | `ActivationRules.Check/BeforeWrite` | Complete order search is repeated; the final exhibitor read supplies blocker checks and the payload. New categories are preserved. A changing order set requires REVIEW. |
| Contract searches discarded actual owner evidence | `MomentusGateway.ValidateDocumentScope` | Missing/conflicting order/exhibitor ownership or contract category is rejected before attachment selection/copy/send. |
| Sponsor category/resource/alternate labels could override explicit item negation | `ExhibitorCategoryRules.Resolve`; `EvidenceRules.NegatedSponsorship` | Same-item negation cannot be overridden. A separate affirmative sponsorship item still supplies positive evidence. |
| Capped, unprocessed apply orders could return successful completion | `Runner.ProcessCandidates`; `FailureRules.ExitCode` | Deferred orders remain journaled, are reported as REVIEW/DEFERRED and return nonzero. |
| An accepted write with unavailable readback was mislabeled FAILED | `Runner.Apply` exception handling; `MomentusGateway.CallWrite/ReconcileIncomplete` | UNKNOWN/exit 3 is reported. Durable Succeeded evidence survives and can reconcile without redispatch. |
| Incomplete activation searches stopped writes but reported operational failure rather than the required review | `ActivationRules.Check` | Search failure evidence is retained; activation requires REVIEW/exit 2. |

## Activation policy verified

Order activation requires a verified final handoff and all required order-level stages, including requested email acceptance. Existing Approval Needed blocks activation. Exhibitor activation requires the complete all-status order set, no PC orders, verified local completion for every applicable scoped journal, and no unfinished/Unknown/Failed/Review peer processing. Current Hold and Approval Needed block activation. A prior active order alone does not authorize activation. Multiple orders can resolve automatically and cause one group activation; they do not inherently require manual activation.

Local order completion and pending shared activation are durable separate facts. Unknown dispatches cannot be retried blindly. Interrupted accepted stages reconcile read-only before later stages. Prepared/confirmed-rejected shared activation can proceed only after its local order stages and the full group gate pass. Recovery tests interrupt every mutation before/after durable result and verification, including both activation stages.

## Handoff and attachment policy verified

The production send path builds the email from `VerifiedHandoff.EmailRow`, using committed order identifiers/fields, effective account/contact/address, the managed note and final order-level contract identities/content hashes. Requested-only attention text is omitted. Preflight downloads/hashes/parses applicable PDFs and validates recipient before the first external mutation. Sending checks the packet again. Accepted response must corroborate recipient/body/attachment manifest and a new correctly scoped saved-email record. This is acceptance evidence, not delivery evidence. Descriptions/legacy receipts alone cannot authorize send success or automatic resend.

## Tenant configuration evidence

The supplied Account Status screenshot shows Not Applicable description, class `0`, status code `0`, weight `0`. The README configuration example now uses code `0`; a regression verifies new account creation/readback with it while preserving the exact submitted name.

`AllCustomFields (2).xlsx`, Sheet1 rows 6-16, confirms the field mappings: company TXT_03, attention TXT_13, first name TXT_05, last name TXT_09, email TXT_10, selector TXT_11, address TXT_04, city TXT_15, state TXT_06, postal TXT_07, country TXT_08. It does not itself contain the API UDF-set Class/Type. Subsequent offline configuration discovery inspected the saved `C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents\Accounts_Pull.xlsx` extract dated 2026-10-04. All 185,489 rows were scanned: 17,782 carried organization-account set `OrgAccountUDF/C/AU`; its billing-selector values included `Y` (307), `N` (22), `ECA` (19) and `BA` (6). The remaining rows had no set or the individual-account set `IndivAccountUDF/C/CO`. No synthetic test identifiers supplied these production values. Metadata-only discovery evidence is retained in `runs/billing-configuration-discovery.json`.

A machine-local, Git-excluded `billing-config.json` now supplies Header `OrgAccountUDF`, Class `C`, Type `AU`, and Not Applicable status code `0`. An offline probe loaded this actual file through `BillingConfiguration.Load/Validate` and `BillingState.Read`: it verified exact-set selection in either collection order, exact submitted company text, above/separate selector behavior, and rejection of missing/duplicate matching sets. No API client was constructed. The configuration blocker is resolved for this checkout; no code-level production defect remains identified by the preceding audit. This is not live verification or a deployment. The account extract flattens only the first set; runtime selection still requires exactly one current matching set. Future packages must include the configuration.

After completing the configuration, the full ServiceOrderEntry suite was rerun: 375 passed, 0 failed/skipped. The ServiceOrderEntry Release build succeeded with 0 warnings/errors. The build copied `billing-config.json` beside the executable; its SHA-256 matches the source configuration. TRX evidence is retained in `runs/billing-configuration-tests/billing-configuration.trx`. The earlier full repository gate below was not rerun for this configuration/documentation-only follow-up.

## Verification

- New cases: 32 Step 7 regressions and 24 audit regressions (56 total).
- Focused final regression gate: 183 passed, 0 failed/skipped.
- Full ServiceOrderEntry suite: 375 passed, 0 failed/skipped.
- Full repository .NET suite: 439 passed, 0 failed/skipped.
- Final repository Release build: succeeded, 0 errors, 0 warnings in the final incremental build. The preceding build reported 7 existing warnings in unrelated AccountImport/AccountsDataIntegrityReport projects.
- `git diff --check` passed. Steps 1-6 tests remain included.

Offline TRX evidence is preserved under `runs/final-audit/confirmed-regressions`, `runs/final-audit/service-order-entry-final` and `runs/final-audit/repository-final`. Reproduction evidence is retained alongside it. The Release build is a source build, not a published/deployed package.
