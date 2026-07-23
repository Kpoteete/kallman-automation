# Efficiency and management audit

**Repository:** Kpoteete/kallman-automation  
**Branch:** agent/automation-platform-hardening  
**Date:** 2026-07-11

## Executive conclusion

The repository is moving from a collection of independent scripts toward a managed internal automation platform. The shared solution, tests, published-release workflow, lock files, common credential/publication utilities, health summaries, and documented operating standards materially improve control. The remaining efficiency risk is concentrated in repeated Momentus SDK implementations, large orchestration classes, N+1 API access patterns, workbook rewrite frequency, and incomplete centralized monitoring.

## Improvements completed in this modernization

- Three workstation-only projects are now versioned and included in CI.
- Generated `.codex-work` content is no longer tracked.
- One solution builds the complete .NET estate and runs all test projects.
- NuGet dependencies are locked and vulnerability reporting is part of CI.
- Python destructive-merge identity and graph validation have automated tests.
- Production can be published once, deployed as a timestamped release, and rolled back.
- Pull jobs share credential validation and backup-aware file publication.
- Exhibitors and Accounts emit structured JSON run summaries.
- Pull output directories can be overridden without recompilation.
- Registration List history is preserved independently from the selected current-view status filter.

## Efficiency findings

### E1 - Repeated Momentus SDK layers

Account Import, Duplicate Merging, and Market Segment Application contain similar credential, reflection, retry, search, model conversion, and text-normalization code. Their copies have already diverged. A fix in one project does not automatically reach the others.

**Recommendation:** migrate common SDK invocation, OData escaping, bounded search, transient classification, credentials, and normalization into focused shared libraries. Keep business-specific calls in each application.

### E2 - Large orchestration classes

Registration List Automation, Duplicate Merging, Market Segment Application, Account Import, and Accounts Data Integrity Report contain classes between roughly 1,000 and 4,000 lines. This increases AI-edit risk because an apparently local change can affect unrelated behavior.

**Recommendation:** continue extraction by responsibility: source readers, plan builders, workbook renderers, API gateways, output publishers, history stores, and run coordinators. Require regression fixtures before moving workbook-rendering code.

### E3 - N+1 API requests

Several workflows retrieve a list and then make one or more API calls per account/contact/exhibitor. This is sometimes unavoidable, but it multiplies runtime and throttling exposure.

**Recommendation:** add per-run caches, bounded concurrency, batch endpoints where supported, and metrics for API calls per output row. Establish a configurable concurrency ceiling rather than project-specific guesses.

### E4 - Repeated reflection

Older pull jobs resolve SDK fields through reflection for each record and column. Missing fields can silently become blanks and reflection work is repeated thousands of times.

**Recommendation:** compile and cache property accessors once, validate required fields at startup, and use typed mappings for identifiers and checkpoint-critical columns.

### E5 - Fixed scanning ranges

Service Orders, Service Order Items, Duplicate Merging, and Market Segment Application use configured account/order ranges. Fixed ceilings eventually become stale and broad ranges may encounter API caps.

**Recommendation:** use verified high-water marks, adaptive subdivision, cap detection, and source/output reconciliation. A full rebuild must fail closed when completeness cannot be demonstrated.

### E6 - Workbook rewrite cost

Plan/apply workflows update Excel workbooks repeatedly. Registration history also grows continuously. Full workbook saves are expensive and increase corruption exposure.

**Recommendation:** use SQLite/JSONL as durable per-row state, checkpoint every row there, flush Excel in batches, and publish workbook snapshots atomically. Introduce monthly history archives or an external canonical store.

### E7 - Build and deployment efficiency

The former `dotnet run` production pattern rebuilt on the server. The new publish/deploy workflow fixes this, but scheduled tasks still need to be migrated.

**Recommendation:** deploy immutable artifacts, retain six releases, record the release ID in summaries, and prohibit production tasks from pointing into the source checkout.

## Management findings

### M1 - Ownership is now declared but operational responsibility needs names

CODEOWNERS identifies the repository owner, but each automation still needs a business owner, technical custodian, schedule, SLA, output consumer, and escalation contact.

### M2 - Monitoring is a framework, not yet an active service

JSON summaries and a health script now exist. They must be connected to Windows Task Scheduler failure actions or a daily alert mechanism. Missing runs are as important as failed runs.

### M3 - Main branch governance

The repository needs required pull requests, required Quality checks, stale-approval dismissal, conversation resolution, and force-push/deletion protection. Administrator bypass should remain available for emergency recovery but be documented.

### M4 - Recovery documentation varies

Newer tools describe plan folders and retries; older pulls do not consistently document checkpoint recovery, rerun safety, rollback, or partial-output handling.

### M5 - Configuration drift

The repository contains .NET 8 and .NET 10, two Momentus SDK generations, and different ClosedXML/CsvHelper versions. Immediate forced upgrades could break SDK reflection behavior.

**Recommendation:** maintain a compatibility matrix, then upgrade one automation at a time with output comparison. Centralize only versions proven compatible across consumers.

## Recommended next implementation batches

1. Add run summaries to Service Orders, Service Order Items, Registration Lists, Account Import, Duplicate Merging, and Market Segment Application.
2. Add a shared paging/cap-detection library and migrate one pull at a time.
3. Add cached reflection accessors and startup schema validation to the pull jobs.
4. Introduce API-call and elapsed-time metrics by operation.
5. Add Registration List golden-workbook regression tests before further renderer extraction.
6. Add Account Import tests for placeholder suppression, country normalization, duplicate reconciliation, and relationship repair.
7. Move plan/apply durable state from frequent workbook saves into a transactional store.
8. Connect daily health output to an email or Teams alert and assign named owners.

## Management scorecard after modernization

| Area | Current | Target |
| --- | ---: | ---: |
| Credential handling | 8/10 | 9/10 |
| Build reproducibility | 8/10 | 9/10 |
| Deployment/rollback | 7/10 | 9/10 |
| Automated testing | 5/10 | 8/10 |
| Runtime completeness controls | 6/10 | 9/10 |
| Monitoring/alerting | 4/10 | 8/10 |
| Maintainability | 5/10 | 8/10 |
| Documentation/governance | 7/10 | 9/10 |

The repository is suitable for controlled internal use when write-enabled jobs retain dry-run/review safeguards and server deployment follows the documented release process. Unattended destructive execution should remain limited until postcondition verification and source-completeness controls are fully standardized.
