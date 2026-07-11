# Monitoring and retention

## Minimum scheduler controls

- Run under a dedicated Windows service account.
- Prevent overlapping instances.
- Treat every nonzero process code as failure.
- Alert the owner after one failure and escalate repeated failures.
- Record the deployed release ID with every run.

## Run summaries

New and migrated automations write JSON summaries containing status, timing, row counts, warnings, errors, publication state, and checkpoint state.

## Retention defaults

| Artifact | Default retention |
| --- | --- |
| Text logs | 90 days |
| JSON summaries | 365 days |
| Failure diagnostics | 30 days after resolution |
| Releases | Current plus previous five |
| Checkpoints and destructive-operation ledgers | While workflow remains active |

Never remove an active checkpoint or idempotency ledger as routine log cleanup.
