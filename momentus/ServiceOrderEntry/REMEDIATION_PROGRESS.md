# Remaining remediation verification

## Policy update - 2026-10-06

The user clarified that Approval Needed (102) affects only the email subject and warning. It does not block order or exhibitor activation. Hold (103) remains a complete stop. The earlier activation-block descriptions below are historical and superseded by this instruction.

Each invocation now plans from current Momentus data and creates a separate current-run audit folder. Older plans, recovery queues and receipts are not read as inputs. Prior email records do not suppress a new eligible workflow's Finance notification. This supersedes the historical cross-run recovery descriptions below.

## Step 5 - complete

Contract selection evaluates all applicable exhibitor/order PDFs, detects conflicting schedules, and uses content hashes and stable document identities. Copy recovery retains source/destination evidence and never repeats an unknown dispatch. Managed payment notes have an explicit marker; unrelated SON notes are preserved. Legacy migration requires unchanged verified journal evidence.

Offline gate: 25 new regressions; 272 ServiceOrderEntry tests; 336 repository tests passed. Repository Release build succeeded with 0 errors and 7 warnings in unrelated AccountImport/AccountsDataIntegrityReport projects. Steps 1-4 regressions passed. No live calls, deployment, or push.

## Step 6 - complete

The journal now retains decision-input evidence. Every dispatched write checks fresh statuses, Hold/Approval Needed, billing instructions/effective targets, item values, category evidence, booth activities, contract inventory/content, payment terms, and managed note identity. Own Verified stages are recognized during recovery. Fresh payloads preserve unrelated order/exhibitor fields; managed-category changes are recomputed against current unmanaged categories. Category matching uses exact resource identifiers or token/phrase boundaries, and negated sponsorship does not add Sponsor.

Offline gate: 47 new regressions; 319 ServiceOrderEntry tests; 383 repository tests passed. Repository Release build succeeded with 0 errors and 7 warnings in unrelated projects. Previous remediation regressions passed. No live calls, deployment, or push.

## Step 7 - complete

Handoff uses a persisted verified final-state packet: committed order fields, effective account/contact/address, managed payment note, and order-level contract identities/content hashes. Contract bytes, PDF validity, attachment scope and recipient are checked before the first write and again before sending. Accepted send evidence must match the journaled recipient/body/attachment manifest and a new correctly scoped saved email; uncertain sends cannot resend automatically.

Order activation requires verified local stages. Shared exhibitor activation searches the complete exhibitor/event order set and all scoped journals. PC orders, unfinished/Unknown/Failed/Review journals, Hold and Approval Needed defer activation. Every applicable order must complete first; multiple orders alone do not require manual activation. Deferred shared activation survives restarts, including interruption between the two activation stages. All discovered candidates are journaled before the attempt cap can defer them.

Reports distinguish SUCCESS, REVIEW, FAILED and UNKNOWN, plus a shared-activation-pending flag. Exit status is 0 for resolved success, 1 for operational failure, 2 for review/deferred activation and 3 for unknown outcomes. Local reporting storage is checked before requests. Evaluation failures no longer return success.

Offline gate: 32 new handoff/activation regressions; 351 ServiceOrderEntry tests; 415 repository tests passed. Repository Release build succeeded with 0 errors, 7 existing unrelated warnings and 1 transient copy-retry warning from overlapping test execution. No live calls, deployment or push. Fresh final production audit follows this gate.

## Fresh final production audit - complete

The audit inspected the current source rather than relying on the earlier review. Reproductions exposed stale completed-order evidence, a missing recovery organization check, foreign contract ownership, a stale final group payload overwriting newly added Hold/Approval/unmanaged categories, sponsorship labels overriding negation, false-success attempt-cap reporting, and accepted writes with failed readback mislabeled FAILED. Those defects are corrected with 24 focused audit regressions, including the newly confirmed Not Applicable status code `0`. Incomplete activation order searches now explicitly require REVIEW.

Final offline regression gate: 183 focused checks passed. Full ServiceOrderEntry: 375 passed, 0 failed/skipped. Full repository .NET suite: 439 passed, 0 failed/skipped. Earlier Step 1-6 and every-stage interruption/recovery regressions remain included and passed. Final repository Release build succeeded with 0 errors and 0 warnings in the final incremental build; the preceding build reported 7 existing unrelated warnings. No live calls, deploy, publish or push.

No unresolved code-level production defect remains identified. Subsequent offline configuration discovery found the billing set `OrgAccountUDF/C/AU` in the 2026-10-04 saved account extract, including the `BA`/`ECA` selector values and the mapped billing fields. The local, Git-excluded `billing-config.json` now supplies those identifiers and the screenshot-confirmed Not Applicable code `0`; the actual configuration passed offline load and field-selection checks. The missing-configuration blocker is resolved for this checkout. Current live state and deployment remain unverified. Full findings and current function evidence are in PRODUCTION_READINESS.md.


## Partial completion and exception summary - 2026-10-13

The workflow now evaluates independent components and retains all reported problems with their submitted values. Safe partial fields, verified billing when available, contracts, payment notes and exhibitor categories may be saved while the order remains PC and the exhibitor remains 35. Activation and Finance completion email remain gated on full completion. One run-level exception summary goes to kylep@kallman.com through Momentus during confirmed apply; preview saves the summary locally without sending. Its separate durable notification journal retains exact send intent, acceptance/readback and uncertain outcomes, with no automatic retry. Independent stage guards preserve fresh input checks, complete matching searches, manual values, the existing ten-order allowance and single-dispatch writes. Unknown writes still stop later record mutations; journal failures halt the run.

Final offline gate: 669 tests passed, 0 failed/skipped. The current immutable self-contained package is `C:\kwi-automations\artifacts\publish\ServiceOrderEntry\2026-10-13-partial-completion-v28`; all 224 manifest hashes matched, all 60 tested source/test/script hashes were unchanged during packaging, and the packaged local-only probe passed with exit 0. Validation evidence is under `C:\kwi-automations\artifacts\validation\soe-partial-20261013` (v28 source hashes, final TRX and release-validation-v28.json). V28 adds fallback notification attachment to another verified order when the first reported order is unavailable and gives notification failure precedence over ordinary REVIEW; V27 is preserved as an earlier offline package. No live Momentus requests, email, deployment or schedule change occurred.

## USAPP Market notification only - V29

Kyle requested an email identifying any needed exhibitor TEXT_14 correction while allowing the rest of the order to continue. Complete item evidence retains UnitCharge. Existing YES skips this check; otherwise exact USAPP MARKET at unit charge 550 or 350 recommends YES, and complete absence recommends NO. Already-correct NO produces no alert. Missing UDF data, incomplete item reads and unreadable candidate prices require manual verification, never an inferred NO. These are separate manual corrections, not blocking validation issues. They appear in the CSV and the existing single run-level Kyle summary even for successful orders; Finance completion email and activation retain their existing gates. The field itself is never changed. Preview does not send, and the summary's existing durable single-dispatch and unknown-response behavior remain intact.

Offline validation: all 690 tests passed, including 21 notification checks for YES retention, exact code/price matching, unchanged UDFs, normal completion plus Kyle email, preview, missing inputs, aggregated alerts, email opt-out independence and escaped values. The immutable package is `C:\kwi-automations\artifacts\publish\ServiceOrderEntry\2026-10-13-usapp-alerts-v29`. All 224 package hashes matched; all 63 source/test/script hashes remained unchanged through publication. The packaged local-only probe exited 0. Evidence is in `C:\kwi-automations\artifacts\validation\ServiceOrderEntry\usapp-alerts-results\usapp-alerts-full.trx` and `usapp-alerts-package-validation.json` beside that results directory. No live Momentus requests, emails, deployment or scheduler changes occurred for this implementation.
