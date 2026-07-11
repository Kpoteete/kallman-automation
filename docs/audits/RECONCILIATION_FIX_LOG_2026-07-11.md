# Production Import Reconciliation Fix Log

Date: July 11, 2026

## Scope

This pass reconciled the programs recently copied from the production computer with the automation standards already developed on the hardening branch.

## Completed fixes

- Merged the latest `main` branch into the hardening branch while preserving the shared automation library and extracted registration models.
- Removed embedded Momentus credential fallbacks from six imported programs. All three credentials must now come from `MOMENTUS_APIUSER`, `MOMENTUS_SECRET`, and `MOMENTUS_KEY`.
- Added a repository-specific CI check that rejects future non-empty Momentus credential fallbacks, including GUID-shaped values that generic secret scanners can miss.
- Fixed seven Windows launchers that attempted to change directory into a `.cs` file. Launchers now use their own folder, name the project explicitly, and return the program's exit code.
- Made the four incremental CSV jobs return a failure code when a run fails.
- Made the four incremental CSV jobs write a temporary file and replace the live report only after the new file is complete. Their checkpoints are written after successful publication.
- Added `KALLMAN_DATA_WAREHOUSE` as an optional server-specific output-folder setting while retaining the existing path as the compatibility default.
- Made both Momentus correction jobs preview-only by default. Live changes require the explicit `--apply` argument.
- Added all newly imported .NET projects to the repository solution so they are compiled by continuous integration.
- Removed the duplicate, double-spaced Automatic Duplicate Merge folder from the merged result; the original local copy remains in the safety stash.

## Verification

- All 19 .NET projects restored and built successfully in Release mode.
- The automated .NET test command completed successfully.
- Python tests passed: 2 passed.
- The current working tree contains no non-empty Momentus environment-variable fallbacks.
- Git whitespace validation passed.

## Compatibility notes for the server

- Read-only pull jobs keep their existing CSV names and column layouts.
- Existing output paths remain the default. Set `KALLMAN_DATA_WAREHOUSE` only if the server uses a different folder.
- The server must have the three Momentus environment variables configured before scheduled jobs run.
- The two correction launchers now perform a dry run unless their scheduled command includes `--apply`. This is intentional protection against accidental production edits.
