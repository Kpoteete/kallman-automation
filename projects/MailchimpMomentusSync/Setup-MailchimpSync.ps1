[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$appRoot = Join-Path $env:LOCALAPPDATA 'Kallman\MailchimpMomentusSync'
$credentialPath = Join-Path $appRoot 'credentials.clixml'

Write-Host ''
Write-Host 'Mailchimp to Momentus Sync - First-Time Setup'
Write-Host '------------------------------------------------'
Write-Host 'This stores the Momentus API credentials encrypted for this Windows'
Write-Host 'account on this computer. They are not stored in the project folder.'
Write-Host ''

if ((Test-Path -LiteralPath $credentialPath) -and -not $Force) {
    $replace = Read-Host 'Setup already exists. Type REPLACE to enter new credentials, or press Enter to keep it'
    if ($replace -cne 'REPLACE') {
        Write-Host ''
        Write-Host 'Existing setup kept. You are ready to use "Run Mailchimp Sync.cmd".'
        exit 0
    }
}

$apiUser = (Read-Host 'Momentus API User ID').Trim()
if ([string]::IsNullOrWhiteSpace($apiUser)) {
    throw 'API User ID cannot be blank.'
}

$secret = Read-Host 'Momentus Secret (the text will be hidden)' -AsSecureString
$key = Read-Host 'Momentus Key (the text will be hidden)' -AsSecureString

if ($secret.Length -eq 0 -or $key.Length -eq 0) {
    throw 'Secret and Key cannot be blank.'
}

New-Item -ItemType Directory -Path $appRoot -Force | Out-Null

[pscustomobject]@{
    ApiUser = $apiUser
    Secret = $secret
    Key = $key
    SavedAt = (Get-Date)
} | Export-Clixml -LiteralPath $credentialPath -Force

Write-Host ''
Write-Host 'Setup complete.'
Write-Host ('Encrypted credential file: ' + $credentialPath)
Write-Host 'Next: double-click "Run Mailchimp Sync.cmd".'
