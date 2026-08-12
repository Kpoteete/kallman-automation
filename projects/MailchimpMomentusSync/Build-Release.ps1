[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$artifactRoot = Join-Path $repoRoot 'artifacts\MailchimpMomentusSync'
$publishRoot = Join-Path $artifactRoot 'win-x64'
$packageRoot = Join-Path $artifactRoot 'package'
$appFolder = Join-Path $packageRoot 'app'
$zipPath = Join-Path $artifactRoot 'MailchimpMomentusSync-win-x64.zip'

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $appFolder -Force | Out-Null

dotnet restore (Join-Path $PSScriptRoot 'MailchimpMomentusSync.csproj') --locked-mode --runtime win-x64
if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }

dotnet publish (Join-Path $PSScriptRoot 'MailchimpMomentusSync.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }

Copy-Item -LiteralPath (Join-Path $publishRoot 'MailchimpMomentusSync.exe') -Destination $appFolder -Force
foreach ($name in @(
    'Run Mailchimp Sync.cmd',
    'Run-MailchimpSync.ps1',
    'Setup Mailchimp Sync.cmd',
    'Setup-MailchimpSync.ps1',
    'Instructions.txt'
)) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $packageRoot -Force
}

Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -Force

Write-Host ''
Write-Host 'Release package created:'
Write-Host $zipPath
