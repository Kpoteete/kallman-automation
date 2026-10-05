# Step 3 decision-search inspection

All paths below use the same complete-search collector. There are no remaining direct first-page consumers. This inspection concerns search completeness only; selection/identity policies assigned to later steps are unchanged.

| Search/callers | Before Step 3 | Stable identity within organization/query | Decisions and absence authority |
| --- | --- | --- | --- |
| `FindCandidates` → `SearchExhibitors`, `SearchOrders` (also recovery discovery) | Followed pages, but lost ordering options; no loop/duplicate checks | Exhibitor ID; order number | Eligibility and work discovery; incomplete discovery aborts before the runner processes records |
| `FindOrganizationCandidates` → `SearchAccounts` (evaluation, pre-create, unknown-create reconciliation) | Exact/contains filters excluded accepted variants; relaxed failure swallowed | Account code | Reuse/ambiguity/create; now requires the complete bounded `Class eq 'O'` index; no failed search becomes no match |
| `FindContacts` → `SearchAccounts` (scoped/global evaluation, pre-create, unknown-create reconciliation) | First page; silently retained first repeated account code | Account code | Contact reuse by known stable ID or unique exact normalized email, ambiguity, create; scoped/global complete indexes precede local email comparison |
| `GetOrderItems` | First page | Order line number, scoped to one order | Package/category and category conflict; full set precedes resolution |
| `GetExhibitorContractPdfs`, `GetOrderContractPdfs` → `SearchDocuments` | First page; invalid IDs silently dropped | Document type + sequence | Contract selection, copy/deduplication, payment extraction, attachment set; baseline and copy reconciliation also complete |
| `GetNearbyExhibitorPdfs` → `SearchDocuments` | First page | Document type + sequence | Nearby fallback/date filtering and ordering happen after completion |
| `GetOrderSonNotes` (evaluation, save, verification, recovery) | First page | Note sequence, scoped to order and OH type | Existing match, add, update, multiple-note conflict; no mutation after incomplete notes |
| `GetBoothNumber` | First page | Activity sequence | Booth selection and same-time conflicts; existing recency ordering applied after completion |
| `EnsureRelationship` | First page; nullable Results could authorize add | Master org/account + subordinate org/account + type | Exact-key query; all required pages verified before absence permits add. Reconciliation/readback uses exact-key GET |
| `ReadyEmailWasSent`, `GetSavedEmails` (send baseline and journal verification) | First page | Document type + sequence | Existing saved email prevents send; all pages required for baseline/recovery. Receipt/journal positive evidence remains authoritative |

## Shared answers to the pagination checks

1. All required pages are collected, including the first; only complete verified collections return.
2. First requests retain their original filters; API continuation links retain the cached search, and every request receives the same options, including ordering and selection.
3. A required later-page failure throws `IncompletePagination`; callers receive no partial results.
4. Repeated continuations throw `PaginationLoop`. Page progression, finite result/page ceilings, and short-page checks also prevent infinite traversal.
5. Stable IDs are required. Identical serialized result duplicates are retained once in first-seen order; different versions throw `ConflictingDuplicateId`. API totals describe received rows, before deduplication.
6. `DecisionSearchException` explicitly separates request failure, incomplete pagination, loop, malformed response, ceiling, duplicate conflict, and unavailable matching identity from complete empty/unique/multiple collections.
7. Account/contact/relationship creation, note add/update, document copy, and email dispatch only use absence from complete searches. Evaluation catches require REVIEW; apply catches require FAILED; reconciliation keeps unresolved stages and requires RECOVERY REVIEW. Journaled search failures are persisted without changing verified mutation stages.

SDK inspection: installed `Ungerboeck.Api.Sdk` 1.262.8.1 `Search` and `NavigateSearchList` each read one response. Navigation forwards the API-provided URL and options. `X-SearchMetadata` supplies `Page`, `Page_Size`, `PageTotal`, `ResultsTotal`, and `Links.Next`. The installed models documentation states that exceeding `MaxResults` returns an API error rather than a successfully truncated search. The collector also checks first-page totals and received-row ceilings independently. Missing or contradictory metadata fails closed.

Step 4 now implements Unicode company identity, explicit billing UDF selection, selector-gated billing workflows, contact duplicate evidence and effective-state validation; see README. Steps 5–7 retain responsibility for contract/content identity, note ownership, category evidence, fresh-input revalidation, handoff/attachments, activation policy, and final failure reporting. Complete retrieval does not settle those policies.
