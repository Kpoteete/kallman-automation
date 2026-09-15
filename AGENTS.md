# KWI Automations: AI working guide

This is a live automation workspace. Treat it as a mixed source, release, and operational-support repository.

## Start here

1. Read `README.md` and `docs/architecture/AI_NAVIGATION.md`.
2. Identify the affected automation before changing files.
3. Read that automation's `README.md`, then inspect its build, test, and deployment scripts.
4. Check `git status --short` before editing. Preserve unrelated local work.

## Folder ownership

- `momentus/` — Momentus API pulls, rebuilds, and guarded update workflows.
- `projects/` — user-facing business tools, imports, reports, and desktop utilities.
- `src/` and `tests/` — shared reusable code and its tests.
- `scripts/` — repository-wide publish, deploy, health, and verification scripts.
- `docs/` — engineering, deployment, inventory, audit, and operational guidance.
- `artifacts/`, `outputs/`, and `logs/` — generated operational material; do not treat as source of truth or reorganize casually.
- Root-level legacy/one-off folders — inspect before modifying; do not move them without confirming callers and schedules.

## Safety rules

- Never put credentials in files. Use `MOMENTUS_APIUSER`, `MOMENTUS_SECRET`, and `MOMENTUS_KEY` environment variables only.
- Treat Momentus writes as high risk. Keep dry-run/plan behavior unless an explicit live action is requested and the tool's confirmation switch is supplied.
- Do not move, rename, delete, or consolidate project folders, launchers, `publish` folders, or server packages without checking references and receiving approval. Scheduled tasks may use absolute paths.
- Publish and schedule immutable Release artifacts. Do not claim a source build is a production deployment.
- Preserve logs, checkpoints, workbooks, and audit evidence. Inspect a partial run before rerunning a non-transactional import.

## Keeping the repository understandable

- Put new Momentus API automations in `momentus/<AutomationName>/`.
- Put new end-user or business workflow tools in `projects/<ToolName>/`.
- Put shared .NET code in `src/Kallman.Automation.Core/` and tests beside or under `tests/`.
- Add a project README covering purpose, inputs, outputs, write risk, safe command, test command, and deployment location.
- Update `Kallman.Automation.slnx` and `docs/architecture/REPOSITORY_INVENTORY.md` when adding a maintained .NET automation.
- Keep generated content out of Git according to `.gitignore`.
