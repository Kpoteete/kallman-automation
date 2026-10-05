# ServiceOrderEntry final production-readiness audit

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

`AllCustomFields (2).xlsx`, Sheet1 rows 6-16, confirms the field mappings: company TXT_03, attention TXT_13, first name TXT_05, last name TXT_09, email TXT_10, selector TXT_11, address TXT_04, city TXT_15, state TXT_06, postal TXT_07, country TXT_08. It does not contain the API UDF-set Class/Type. Local source/configuration searches found no authoritative billing-set values. Test fixture Class/Type values are synthetic and cannot supply production configuration.

No billing-config.json exists in this checkout. `BillingConfiguration.Validate` and `Runner.Run` stop processing before constructing the live gateway if billing-set identifiers are missing. Production use remains blocked until the actual billing-set Class/Type are established and supplied. No code-level production defect remains identified by this audit; configuration is the remaining readiness blocker.

## Verification

- New cases: 32 Step 7 regressions and 24 audit regressions (56 total).
- Focused final regression gate: 183 passed, 0 failed/skipped.
- Full ServiceOrderEntry suite: 375 passed, 0 failed/skipped.
- Full repository .NET suite: 439 passed, 0 failed/skipped.
- Final repository Release build: succeeded, 0 errors, 0 warnings in the final incremental build. The preceding build reported 7 existing warnings in unrelated AccountImport/AccountsDataIntegrityReport projects.
- `git diff --check` passed. Steps 1-6 tests remain included.

Offline TRX evidence is preserved under `runs/final-audit/confirmed-regressions`, `runs/final-audit/service-order-entry-final` and `runs/final-audit/repository-final`. Reproduction evidence is retained alongside it. The Release build is a source build, not a published/deployed package.
