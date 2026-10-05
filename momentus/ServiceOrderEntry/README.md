# Service Order Entry

Completes reviewed Momentus service-order fields for main exhibitors whose status is **Online Booth Order (35)** and whose order status is **Pending Completion (PC)**.

The workflow reads the exhibitor, order, account, contacts, order items, booth activities, `SalesRepLookup.xlsx`, and `OrderCategoryLookup.xlsx`. Every candidate receives one row in a CSV under `runs`, named with a timestamp and unique run ID. The CSV is the human-readable report; the per-order journal is the recovery authority.

Bulk preview and `--all` apply runs search eligible orders across **all events** in the selected Momentus organization. No event list is required or read. Startup prints `Event scope: ALL EVENTS`. The obsolete `enabled-events.txt` is preserved as a historical file but is no longer copied into new packages; the removed `--enabled-events` option is rejected with instructions to omit it.

An explicit `--exhibitor ID` run is the individual-testing path; `--event ID` can optionally narrow that exhibitor's run. All eligibility and safety rules still apply, and live changes still require `apply --confirm-service-order-entry`.

It synchronizes the exhibitor categories managed by this workflow with the selected service-order category, line-item text, and exhibitor `TXT_08`. Stale managed categories are removed—for example, a Space Only order removes an old Turnkey exhibitor category and adds Space Only. Categories outside this workflow's table are preserved. Canada (`55`) and USA (`56`) are intentionally not inferred or changed. If Approval Needed (`102`) is already present, it is preserved; the order is still entered but both default status activations are skipped. Exhibitors carrying Hold (`103`) are excluded before order evaluation and receive no writes.

Exhibitor documents in the `CON` Contract PDF category are copied to the service order after its core fields pass readback. The originals remain on the exhibitor. Existing order copies are reused only with verified content and stable identity evidence.

If neither the exhibitor nor service order has a `CON` Contract PDF, the program checks exhibitor PDFs entered on the order date or one calendar day before/after it. A nearby PDF is used only when its contents contain a recognizable Payment Schedule; this prevents unrelated nearby uploads from being selected. All qualifying fallback PDFs are evaluated; source identities, timestamps, and hashes are retained in the journal.

All applicable exhibitor and order `CON` PDFs are read and hashed before schedule selection. Nearby PDFs are considered only if both normal sets are empty. Conflicting complete schedules, including conflicts within one PDF, require REVIEW. Equivalent schedules use stable identity ordering solely to record the chosen source. Document descriptions never establish equality. Source organization/type/sequence/ID/content hash, selected schedule source, and verified destination identities/hashes are retained in the existing per-order journal. Identical content is copied once; uncertain copies reconcile against the pre-write destination baseline and exact bytes. Ambiguous destinations or a missing previously verified destination require REVIEW.

The complete schedule is synchronized only to the `OH`/`SON` note titled `Payment Schedule [KWI ServiceOrderEntry]`. Unrelated SON instructions are preserved. Multiple managed candidates require REVIEW. A legacy `Payment Schedule` note can migrate only when its stable sequence, unchanged title, and content hash are proven by a previously Verified journal stage; an unjournaled legacy note requires ownership review. The managed note sequence and content hash are persisted.

After all order-entry, document, note, and exhibitor-category writes succeed, apply mode sends a concise ready-for-invoicing email through Momentus to `MiranaC@kallman.com` and `LindsayH@kallman.com`, with `kylep@kallman.com` in CC. The email begins with: "This service order automation is in the early stages of development. There may be bugs or small mistakes. Please review the order details before invoicing." This notice is in the email saved against the order; the Payment Schedule note retains its original purpose. The email includes the exhibitor/event/order identifiers, order date, sales rep, category, booth, essential Bill-To account/contact/address details, complete Payment Schedule text, and a list of the attached Contract PDFs. It attaches the verified applicable contract set from the final service order, including contracts originally present only on that order. Attachment bytes, PDF validity, scope and recipient are checked before the first mutation and again before sending. Internal category, status-action, address-action, and contact-action details remain in the run CSV rather than the email. For Approval Needed (`102`), the subject starts with `WAIT FOR SALES APPROVAL` and a prominent message at the top says not to continue until Sales gives notice because final approval is pending. It is enabled by default; use `--skip-ready-email` for an exceptional apply run that must not send it. Email intent stores recipient, subject, body and attachment hashes, and the existing saved-email sequences. An accepted response is journaled before readback of a new saved email and persistence of the supplemental receipt. This establishes accepted send evidence, not delivery. If the send response is lost, a matching description alone does not prove its recipient/body/attachments: restart requires REVIEW and never automatically resends.

## Safety model

Preview is the default and performs no writes:

Read-only API requests make at most three attempts for transient transport failures and HTTP 408/429/500/502/503/504 responses, with bounded backoff. Permanent read errors are not retried. Step 1's single-dispatch write policy is unchanged. A timeout, lost connection, uncertain server response, or missing mutation response/created identity is recorded as `UNKNOWN WRITE OUTCOME`, not proof of failure. Processing stops for that record, with no subsequent writes, email, or activation. Confirmed rejections remain `FAILED`; either outcome produces a nonzero apply exit code. The CSV retains the uncertain operation, target identifiers, error evidence, processing ID, run ID, and stage summary.

### Complete decision searches (Step 3)

Every API search used for a business decision passes through `MomentusGateway.SearchAll` and `CompleteSearch.Read`. The first request uses the intended filter; navigation follows the API's continuation links with the same search options (including ordering, page size, ceiling, and selection). All pages are collected before matching, absence checks, conflict checks, or recency selection. This covers eligibility, organization/contact matching, items, contract/nearby documents, SON notes, booth activities, exact relationships, and saved emails, including mutation baselines and recovery readback.

Required search metadata must establish page numbers, page size, result/page totals, and completion. A first-page total above `--max-results`, inconsistent totals, missing pages/metadata/identities, short intermediate pages, malformed or repeated continuation links, and failed requests make the decision unavailable. Continuations must remain on the same API endpoint and organization. Totals are checked against received API rows before identical stable-ID duplicates are removed in first-seen order. Conflicting versions of one ID fail the entire search. No partial collection is returned. Complete zero/one/multiple results remain ordinary distinct outcomes; errors carry an explicit failure kind and page.

Evaluation business uncertainty produces REVIEW without writes; retrieval, parsing and configuration failures produce FAILED. Apply failures stop before the related mutation and mark the record FAILED; recovery failures preserve unresolved stages and produce RECOVERY REVIEW. Search failure details during apply/recovery are flushed to the per-order journal's `SearchFailures`. Prior verified stages and created IDs retain Step 2 authority; uncertainty never authorizes redispatch. Descriptions and supplemental receipts alone cannot authorize accepted-send state or automatic resend; accepted response and scoped saved-email evidence are required.

Organization matching requires the bounded **complete organization account index** (`Class eq 'O'`). Exact/full-name substring queries cannot cover the suffix, punctuation, and spacing variations accepted by the current comparison. The index is not cached across creation rechecks; if it cannot complete within the ceiling, matching and creation stop. Missing/empty comparison identities also stop; Step 4 now preserves Unicode and treats normalized equality as candidate evidence only. Relationship searches use the exact organization/master/subordinate/type keys and normally complete on one page; failed or incomplete queries never permit an add.

The search-path inspection and stable keys are recorded in [SEARCH_COMPLETENESS.md](SEARCH_COMPLETENESS.md). Offline pagination regressions can be run with:

```powershell
dotnet test .\tests\ServiceOrderEntry.Tests\ServiceOrderEntry.Tests.csproj -c Release --filter FullyQualifiedName~SearchCompletenessTests
```

## Durable journal and restart

Live runs use one canonical machine-local folder: `%ProgramData%\Kallman\ServiceOrderEntry\state`. Every Release package uses the same location regardless of executable path or working directory. Alternate `--state-folder` values are rejected for live runs; the override is preview-only. The run lock is held there for the entire run and its file is retained after release to avoid unlink races. Before contacting Momentus, startup acquires the lock, checks durable file creation/flush, and loads/validates existing journals. Required state failures prevent mutations. An audit-storage failure after a mutation halts the entire run, surfaces a nonzero error, and attempts to save a CSV without performing further mutations.

`orders/<identity-hash>.json` records are keyed by canonical base endpoint, organization, event ID, exhibitor ID, and order number. Names and descriptions are never the journal key. Each record retains its processing ID, original run ID, evaluated plan, requested email/activation options, and individual mutation stages. The state transitions are `Planned → Dispatching → Succeeded → Verified`; confirmed rejections are `Failed`, and ambiguous/in-flight effects are `Unknown`. Intent and dispatch state are flushed to disk and atomically replaced before invoking a mutation. The returned response (including created IDs) is persisted immediately, followed by exact-target readback evidence and verification before any next stage. State files contain business evidence, not credentials; preserve them with access appropriate to billing/contact data.

Startup adds incomplete journaled orders across all events to discovery by exact order/exhibitor IDs independently of PC/35 eligibility. Existing endpoint, organization, explicit exhibitor/event scope, confirmation, and attempt-cap restrictions still apply. Recovery reuses the saved plan and verified IDs; changing its email/activation options requires REVIEW. Recorded activations permit recovery to recognize order A/exhibitor 2 without losing the unfinished work. Current identity, Hold, and status checks remain required. Completed records are reused instead of executing the pipeline again.

An interrupted `Dispatching` stage becomes `Unknown`. Recovery reads current external state and either persists positive reconciliation as `Verified` or stops with `RECOVERY REVIEW` and a nonzero result. It never automatically redispatches unknown writes, even when an absence search or unchanged old values might suggest non-execution. Created accounts/contacts require their returned ID and matching readback; name/email searches without a returned ID are insufficient proof. Relationships use their exact organization/account/type composite key. Updates use exact target IDs and intended fields. Document recovery uses the returned type/sequence when present, or a unique new destination outside the recorded baseline, plus the source content hash. Notes use returned/existing sequence, intended title/text, and unique matching readback. Email reconciliation requires an accepted response plus a new saved-email record; otherwise it requires review. Planned stages and confirmed rejections may proceed through existing validation; skipped journaled stages must have positive readback evidence before completion.

The journal is evidence and recovery state, not a transaction or rollback mechanism. Never clear an unknown stage to make it eligible for retry. Existing legacy CSVs, receipts, and package-local state are preserved; they do not supply missing per-stage evidence for pre-journal mutations. The journal and supplemental receipts use endpoint/organization-aware identity. Keep the canonical state across restarts and future releases.

```powershell
.\publish\ServiceOrderEntry.exe preview --exhibitor 193914 --event 6208
```

Apply mode requires a single exhibitor and the exact confirmation switch. The initial cap is one completed order:

```powershell
.\publish\ServiceOrderEntry.exe apply --confirm-service-order-entry --exhibitor 193914 --event 6208
```

Bulk apply requires the explicit `--all` scope and is hard-capped at 10 write attempts. REVIEW rows do not consume the write-attempt cap; failed writes do. Omitting `--max-updates` with `--all` defaults to 10:

The allowance is reserved immediately before an order's first actual write dispatch. Read-only verification of completed orders and unresolved recovery does not consume it. Confirmed rejections and unknown responses to a dispatched write do consume it. Orders deferred at the dispatch boundary retain their planned journal stages and return REVIEW.

```powershell
.\publish\ServiceOrderEntry.exe apply --confirm-service-order-entry --all --max-updates 10
```

Live apply activates verified service orders and completed exhibitors by default. Preview never changes either status. Hold and Approval Needed still block activation, and exhibitor activation waits until all scoped pending orders and unfinished journaled work resolve:

```powershell
.\publish\ServiceOrderEntry.exe apply --confirm-service-order-entry --exhibitor 193914 --event 6208 --activate-order --activate-exhibitor
```

- Order activation changes the successfully completed order from `PC` to `A`.
- Exhibitor activation changes the completed exhibitor from status `35` to Active status `2` after the group checks pass.
- `--skip-order-activation` and `--skip-exhibitor-activation` disable the respective transition for an exceptional run or to match an older recovery plan. Existing `--activate-order` and `--activate-exhibitor` switches remain accepted.
- Before writing, the program rereads the order and exhibitor and requires the original eligibility state, or the exact activation state already verified in its journal.
- After writing, it rereads and verifies every requested service-order value and enabled status transition.
- Credentials come only from `MOMENTUS_APIUSER`, `MOMENTUS_SECRET`, and `MOMENTUS_KEY`.

## Bill-To identity and effective billing (Step 4)

The customer's selector is authoritative. The revised Step 4 requirements supersede the earlier creation/fallback policy conflicts in this README and the original design.

- **Use Above Address**, `ECA`, `No`, `N`, and **blank** retain the existing order account/contact/address. The existing Bill-To account must equal the order's source account, with an existing usable contact on that account. Conflicting identities require REVIEW without reassignment. Separate company/address/contact/attention fields are ignored even when populated. No duplicate search, account/contact creation, account update, or billing relationship mutation occurs on this path.
- **Below Address**, `BA`, `Yes`, and `Y` explicitly request separate billing. Only this path searches/matches/creates separate Bill-To records. Unknown selectors require REVIEW. Presence of address text never infers separate billing.
- Unicode letters/digits and combining marks are preserved for comparison; token boundaries remain distinct. Only trailing whole tokens `inc`, `incorporated`, `corp`, `corporation`, `company`, `co`, `llc`, `ltd`, `limited`, `plc`, `lp`, `llp` are removed internally. Empty/suffix-only identities never match. Comparison values are never written to Momentus.
- Account matching requires the complete bounded organization index. Normalized company equality identifies candidates. A sole candidate with matching complete street/city/state/postal/country evidence and stable organization-account code is confirmed and reused with its existing name and Event Sales Status. A same-name candidate with conflicting/missing evidence requires REVIEW. Multiple plausible accounts remain ambiguous; neither ambiguity nor search failure permits creation.
- Complete no-candidate results plus valid separate billing permit account creation. The account **Name is the exact submitted company text**, including capitalization, suffix, punctuation and surrounding spaces. The configured tenant Not Applicable Event Sales status is included in the creation intent and verified with the exact returned account code, name and address. No existing account is renamed or has its status/address overwritten. BTO relationships do not force an Event Sales designation.
- Contacts use a complete account contact index, then local trimmed/case-insensitive email comparison. The known current contact ID wins only when present as a valid contact on the intended account in the complete result; otherwise one exact-email contact is reused regardless of submitted name variation. Multiple contacts without a known stable identity require REVIEW. If no account contact matches, a complete global contact index checks cross-account email evidence. Any cross-account match requires REVIEW; no automatic movement or cross-account relationship is authorized. Complete absence permits contact creation with verified parent/code/email/name.
- Separate billing requires a usable company identity, first/last name, a single valid email, and the documented street/city/postal/country fields. State is retained and compared but is not arbitrarily made mandatory: this project has no country/state requirements lookup. Country/control-character checks reject clearly invalid values. Above billing validates existing effective values, with no requirement for the irrelevant separate fields.
- Category IDs must be positive and present in the loaded validated category lookup. Bad category identifiers and missing sales-rep lookup codes fail startup. Category text matching itself remains unchanged.

### Required billing configuration

Supply `--billing-config PATH`, or place `billing-config.json` beside the executable. No tenant Class/Type or Not Applicable status value is guessed. Header/Class/Type must be configured before any processing; Class is one character and Type at most two. SDK documentation defines `OrgAccountUDF` as the organization-account header. The saved `Accounts_Pull.xlsx` extract dated 2026-10-04 establishes the Kallman organization-account billing set as Header `OrgAccountUDF`, Class `C`, Type `AU`: its billing-selector column contains `BA`, `ECA`, `Y` and `N`, alongside the billing fields confirmed by `AllCustomFields (2).xlsx`. The user-supplied Account Status screenshot on 2026-10-05 confirms Not Applicable status class `0`, status code `0`, and weight `0`.

Confirmed Kallman configuration (missing Class/Type fail closed; empty status blocks new account creation):

```json
{
  "Header": "OrgAccountUDF",
  "Class": "C",
  "Type": "AU",
  "EventSalesNotApplicableCode": "0",
  "AboveSelectors": ["", "ECA", "No", "N", "Use Above Address"],
  "SeparateSelectors": ["BA", "Yes", "Y", "Below Address"]
}
```

The local `billing-config.json` contains these values and is excluded from Git as machine-local configuration. It is copied beside the executable by the project's existing build/publish rules; verify the configuration in any future immutable package. The confirmed Kallman **single-character Not Applicable Account Status code is `0`**. `NA` is the business label, not an assumed API code. Selector lists must be unique and disjoint, and blank can never authorize separate billing. New account creation remains REVIEW until the Not Applicable code is explicitly configured. The account extract exports only the first field set, so it does not establish the absence of additional live sets; runtime selection still requires exactly one matching Header/Class/Type. These identifiers were established offline, with no live account validation or deployment.

### Effective billing and recovery

The journal retains requested submission and `EffectiveBilling` separately: actual account/contact snapshots, their origins, above-address flag and pending creation flags. CSV effective company/contact/email/source columns are appended without removing earlier audit columns. Existing identities can be READY after effective validation. Valid plans requiring a returned account/contact ID are **PREPARED**, not READY. Apply accepts PREPARED solely to create the validated missing identity; exact-target readback and journal persistence must resolve pending IDs before READY or order completion. No creation escapes a REVIEW decision.

Immediately before billing writes and order assignment, re-read the configured UDF set/selector/instructions and current order billing IDs. Changed inputs/configuration or target account/contact values require REVIEW. Creation rechecks remain complete; newly discovered candidates stop the stale plan for re-evaluation. Final account/contact data and assigned order IDs are verified before later stages. Existing email layout/content is unchanged; its billing identity fields now bind to effective records, and irrelevant above-address attention text is ignored.

Verified Step 2 created identities are reused without repeating matching or creation. Interrupted stages still use Step 1 single-dispatch and Step 2 reconciliation. Pre-Step 4 journal files remain loadable and retain verified IDs; legacy unfinished plans lack the billing snapshot/configuration boundary and therefore require REVIEW before further writes. Historical account creation/address stages can still be reconciled read-only, but cannot authorize a new shared-address update. Do not clear or edit journals to bypass review.

Unresolved authority to change a shared account address or move contacts across accounts remains a narrow REVIEW decision, not an enabled policy.

Account/address/contact creation can be non-transactional. If a later step fails, the row is marked `FAILED` with the error instead of being marked complete; created records remain in Momentus and their returned IDs survive in the journal for recovery.

Service-order fields, exhibitor-category additions, and document copies are separate Momentus writes. The program journals and verifies each stage and records partial failure. Resume with the original scope and options; unresolved recovery remains REVIEW without repeating mutations.

## Build and test

```powershell
dotnet restore .\ServiceOrderEntry.csproj
dotnet test .\tests\ServiceOrderEntry.Tests\ServiceOrderEntry.Tests.csproj -c Release
dotnet publish .\ServiceOrderEntry.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

## Deployment

The verified 2026-10-05 all-events package is available for manual runs at `C:\kwi-automations\artifacts\publish\ServiceOrderEntry\2026-10-05-all-events`. Commands for this package:

```powershell
$folder = 'C:\kwi-automations\artifacts\publish\ServiceOrderEntry\2026-10-05-all-events'
& "$folder\ServiceOrderEntry.exe" preview
& "$folder\ServiceOrderEntry.exe" apply --confirm-service-order-entry --all --max-updates 10
```

The apply command attempts up to ten eligible orders across all events, makes live Momentus changes, sends the normally enabled Finance email, and activates verified orders and completed exhibitors when the safety checks pass. Do not include the obsolete `--enabled-events` option.

For unattended execution every 15 minutes, see [SCHEDULING.md](SCHEDULING.md). `Build-Release.ps1` creates a fresh self-contained Windows package; `Install-ServerTask.ps1` installs the Windows task only when explicitly run. It defaults to preview; `-EnableLiveUpdates` opts into live order entry and ready emails across all events. The installer first executes a local-only `probe` under SYSTEM and refuses to register the recurring task if credentials, configuration, runtime, journal or report storage fail verification. No scheduler is enabled by building or publishing.

The final prepared scheduler package is `C:\kwi-automations\artifacts\publish\ServiceOrderEntry\2026-10-05-scheduler-ready`, with an adjacent ZIP for transfer. The current full suite passed 384 tests and its packaged local probe passed. No recurring task has been installed by this preparation. On the executing computer, open PowerShell as Administrator and run the packaged `Install-ServerTask.ps1 -EnableLiveUpdates`, then verify its first actual run with the packaged `Verify-ServerTask.ps1`.

Preserve `runs` and the canonical state as operational evidence. The executing account must be able to read/write/flush that state folder; the installer does not migrate legacy state. Scheduled logs/reports are separate from immutable executable packages.

## Step 6: fresh inputs and category rules

Evaluation persists decision evidence for order/exhibitor identities, original mutable order values, salesperson and state-pavilion inputs, items (including financial values), booth activities, contract inventory/content, payment terms, note ownership, managed categories, and Approval Needed. Before every dispatched write, the guard completely rereads and checks that evidence, billing instructions/effective targets, Hold, and eligibility/verified activation states. Material changes require REVIEW; missing evidence in older journals also requires re-evaluation without discarding verified stages. Verified copies and notes are recognized as the automation's own changes.

The guard supplies freshly read order/exhibitor payloads and computes the managed-category delta against their current categories. New unmanaged categories survive updates and activation; changes to managed categories or Approval Needed stop stale processing. Invalid category identifiers are preserved pending REVIEW. Package identifiers use exact configured resource codes or token/phrase boundaries. `Cosmetic Package` cannot match `SME`. Sponsorship requires an explicit affirmative sponsor/sponsorship/sponsoring term; negated clauses do not add Sponsor.

## Step 7: handoff, activation and outcomes

The ready email is built from the journaled verified final-state packet, using effective Momentus account/contact/address and committed order values. Requested attention text is not a committed field and is omitted. The packet retains the managed note identity/hash and final order-level attachment identities/hashes. Accepted email evidence must corroborate recipient, body and attachment manifest plus a new saved-email record under the intended order. A lost/uncertain response requires review and never automatically resends.

Before order activation, every required order-entry/document/note/category/email stage must be Verified. Before exhibitor activation, a complete all-status search must establish that no order for that exhibitor/event is PC. Every scoped journaled order must have verified local completion and no unfinished, Unknown, Failed or Review processing. Current Hold or Approval Needed blocks activation. An already active order alone supplies no authorization. Multiple applicable orders can activate the exhibitor automatically only after every relevant PC and unfinished journaled order resolves. Incomplete searches stop activation. Local order completion and pending shared exhibitor activation are persisted separately so interruption or an attempt cap cannot lose the remaining work.

CSV `Outcome` distinguishes SUCCESS, REVIEW, FAILED and UNKNOWN; `ExhibitorActivationPending` identifies deferred group work. Exit codes are 0 for resolved success, 1 for operational failure, 2 for review or deferred activation and 3 for unknown write outcomes (unknown takes precedence). Both journal and report storage are checked before requests. Evaluation and partial/systemic failures return nonzero.

Completed-order recovery rereads committed billing/order identity, source instructions, item/booth evidence, managed-note identity/content and contract inventory/content before reporting READY or finishing shared activation. Group verification repeats the complete order search to detect a changed set and uses a final exhibitor read for Hold/Approval checks and the mutation payload. New unmanaged categories survive that boundary. Contract search results must carry the correct owner/category identity. An accepted write with failed readback remains UNKNOWN in the report while its durable Succeeded stage is preserved for reconciliation. Apply orders deferred by the attempt cap return review status instead of successful completion.
