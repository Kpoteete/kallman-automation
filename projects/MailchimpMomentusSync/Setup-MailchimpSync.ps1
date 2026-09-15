[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

& (Join-Path $PSScriptRoot 'Run-MailchimpSync.ps1') -ConfigureCredentialsOnly
exit $LASTEXITCODE
