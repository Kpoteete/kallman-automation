# Kallman Automation

This repository contains internal automation scripts for Momentus API pulls, reporting exports, and Power BI support files.

## Main automations

- Accounts Pull
- AccountsDataIntegrityReport
- Account_name_punctuation_and_email_cleanup
- Exhibitors Pull
- EventsPull
- ServiceOrderItemsPull
- NotesPull
- ServiceOrderPull
- WebsiteCorrectionDaily
- Registration List Workbook Support

## Rules

- Do not commit API keys, passwords, tokens, or secrets.
- Do not commit full CSV/XLSX output files.
- Keep sample files small and clearly marked as SAMPLE.
- Use branches for changes.
- Keep main branch stable.

## Basic run steps

Go to the automation folder and run:

```powershell
.\run.ps1 -FullPull
