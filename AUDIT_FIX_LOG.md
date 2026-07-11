# Audit remediation log

Tracks remediation from the main-branch audit at commit `43918278f7b8f8505d25b5d80d8bd1d342437180`.

## 2026-07-11

- Externalized committed Momentus credential fallbacks and added fail-closed validation.
- Ignored browser profiles, run ledgers, diagnostics, databases, generated artifacts, and builds.
- Corrected merge idempotency; added graph preflight and exact source verification.
- Made dry-run merge results reusable and incomplete/error runs return nonzero.
- Prevented partial Exhibitor search records from overwriting full production rows.
- Added temporary-file publishing and corrected fatal/partial process exit codes.
- Required `--live --confirm-production-writes` for Account Import live writes.
- Blocked Website Validation requests to non-public targets and validated redirect hops.
- Preserved Website Validation row context on worker failure.
- Added CI for secret scanning, Python compilation, and all .NET projects.

## Still requiring owner/API validation

- Rotate/revoke exposed credentials, inspect API logs, and coordinate Git-history cleanup.
- Enable secret scanning, push protection, and branch protection in GitHub settings.
- Validate Momentus paging, record keys, and post-merge verification fields.
- Redesign Registration List canonical history/retention.
- Add Account Import relationship reconciliation and safe write retry semantics.
