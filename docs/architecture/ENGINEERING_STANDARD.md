# Kallman automation engineering standard

## Configuration precedence

1. Safe defaults committed in code or `appsettings.json`.
2. Machine-specific settings from environment variables or `appsettings.local.json`.
3. Command-line options for intentional per-run overrides.

Credentials are accepted only from environment variables or a future approved secret store.

## Process exit codes

- `0`: all intended work completed successfully.
- `1`: fatal, partial, incomplete, or uncertain result.
- `2`: deliberate cancellation, validation-only result, or human action required.

## Writes

- Read operations may retry bounded transient failures.
- Create operations reconcile by a unique business key before retrying.
- Merge, delete, and inactivate operations require a postcondition check.
- Every source-system write tool defaults to dry-run or consumes an explicitly approved plan.

## Files

- Write to a temporary file in the destination directory.
- Flush and validate the temporary file.
- Replace the destination only after validation.
- Advance checkpoints only after publication succeeds.

## Operations

- Scheduled tasks execute published Release artifacts.
- Every run emits a timestamped text log and JSON summary.
- Run scripts preserve the child process exit code.
- Production output and logs have documented retention and rollback procedures.

## Tests

Business rules, idempotency keys, paging/cap behavior, output schemas, file publication, and destructive-operation preflight require automated tests.
