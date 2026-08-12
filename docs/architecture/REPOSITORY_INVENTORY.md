# Repository inventory

## Production pull jobs

| Automation | Runtime | Writes source system | Primary output |
| --- | --- | --- | --- |
| Accounts Pull | .NET 10 | No | Excel workbook |
| Exhibitors Pull | .NET 10 | No | CSV plus checkpoint |
| Service Orders Full Rebuild | .NET 10 | No | CSV |
| Service Order Items Full Rebuild | .NET 10 | No | CSV |

## Business workflow jobs

| Automation | Runtime | Write risk | Safety model |
| --- | --- | --- | --- |
| Account Import | .NET 8 | High | Dry-run plus explicit production confirmation |
| Duplicate Merging | .NET 8 | High | Plan, review, approve, apply |
| Market Segment Application | .NET 8 | High | Plan, review, approve, apply |
| Automatic Duplicate Merge | Python | Critical | Resumable ledger plus exact-code checks |
| Registration List Automation | .NET 8 | File writes | Run lock plus temporary-file publication |

## Reporting and validation jobs

| Automation | Runtime | Writes source system | Primary output |
| --- | --- | --- | --- |
| Accounts Data Integrity Report | .NET 10 | No | Excel audit workbooks |
| Stale Momentus Account Report | .NET 8 | No | Excel review workbook |
| Website Validation | Python | No | Excel result workbooks |

## Repository rules

- Source, tests, safe example configuration, documentation, and reusable scripts belong in Git.
- Credentials, local configuration, browser profiles, checkpoints, logs, generated workbooks, and run folders do not.
- Every production executable must return `0` for success, `1` for failure, and may use `2` for a deliberate cancellation or validation-only result.
- Write-enabled programs must default to dry-run or require a reviewed plan and explicit live confirmation.
- Production schedules run published artifacts, not `dotnet run` from a source checkout.
