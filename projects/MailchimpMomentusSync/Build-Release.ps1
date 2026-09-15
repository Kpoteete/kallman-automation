[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$artifactRoot = Join-Path $repoRoot 'artifacts\MailchimpMomentusSync'
$publishRoot = Join-Path $artifactRoot 'win-x64'
$packageRoot = Join-Path $artifactRoot 'package'
$appFolder = Join-Path $packageRoot 'app'
$zipPath = Join-Path $artifactRoot 'Kallman-Mailchimp-Momentus-Sync-v3.3.0-win-x64.zip'
$zipHashPath = $zipPath + '.sha256'
$projectPath = Join-Path $projectRoot 'MailchimpMomentusSync.csproj'
$catalogPath = Join-Path $projectRoot 'ImportMappings.json'
$expectedCatalogVersion = '2026.08.19.4'

function Test-BuiltInCatalog {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "ImportMappings.json is missing: $Path"
    }

    $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    if ($raw -match '(?i)"\s*(apiuserid|apiuser|secret|key|credentials|momentus_apiuser|momentus_secret|momentus_key)\s*"\s*:') {
        throw 'ImportMappings.json contains a credential-like field. Credentials must never be packaged.'
    }

    $catalog = $raw | ConvertFrom-Json
    if ([string]$catalog.Version -ne $expectedCatalogVersion) {
        throw "Catalog version mismatch. Expected $expectedCatalogVersion; found $([string]$catalog.Version)."
    }

    $campaigns = @($catalog.CampaignTypes)
    $salespeople = @($catalog.Salespeople)
    if ($campaigns.Count -eq 0) {
        throw 'The built-in catalog must contain at least one campaign mapping.'
    }
    if ($salespeople.Count -eq 0) {
        throw 'The built-in catalog must contain at least one salesperson mapping.'
    }

    foreach ($campaign in $campaigns) {
        if ([string]::IsNullOrWhiteSpace([string]$campaign.CampaignType) -or
            [string]::IsNullOrWhiteSpace([string]$campaign.ClickType) -or
            [string]::IsNullOrWhiteSpace([string]$campaign.OpenType)) {
            throw 'Every campaign must contain CampaignType, ClickType, and OpenType.'
        }
    }

    $duplicateCampaign = @($campaigns |
        Group-Object { ([string]$_.CampaignType).Trim().ToUpperInvariant() } |
        Where-Object Count -gt 1)
    if ($duplicateCampaign.Count -gt 0) {
        throw "Duplicate campaign name: $($duplicateCampaign[0].Name)"
    }

    foreach ($salesperson in $salespeople) {
        if ([string]::IsNullOrWhiteSpace([string]$salesperson.Salesperson) -or
            [string]::IsNullOrWhiteSpace([string]$salesperson.SalespersonAccountCode)) {
            throw 'Every salesperson must contain Salesperson and SalespersonAccountCode.'
        }
    }

    $duplicatePair = @($salespeople |
        Group-Object { (([string]$_.Salesperson).Trim() + '|' + ([string]$_.SalespersonAccountCode).Trim()).ToUpperInvariant() } |
        Where-Object Count -gt 1)
    if ($duplicatePair.Count -gt 0) {
        throw "Duplicate salesperson mapping: $($duplicatePair[0].Name)"
    }

    $duplicateCode = @($salespeople |
        Group-Object { ([string]$_.SalespersonAccountCode).Trim().ToUpperInvariant() } |
        Where-Object Count -gt 1)
    if ($duplicateCode.Count -gt 0) {
        throw "Duplicate salesperson account code: $($duplicateCode[0].Name)"
    }

    return $catalog
}

if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is not installed or dotnet.exe is not on PATH.'
}

$dotnetVersion = (& dotnet.exe --version).Trim()
$dotnetMajor = 0
[void][int]::TryParse(($dotnetVersion -split '\.')[0], [ref]$dotnetMajor)
if ($dotnetMajor -lt 10) {
    throw "The build requires the .NET 10 SDK. Detected: $dotnetVersion"
}

$catalog = Test-BuiltInCatalog -Path $catalogPath

Remove-Item -LiteralPath $publishRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $packageRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zipHashPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publishRoot, $appFolder -Force | Out-Null

Write-Host 'Restoring locked NuGet packages...'
& dotnet.exe restore $projectPath --locked-mode --runtime win-x64 --nologo
if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }

Write-Host 'Publishing self-contained Windows x64 engine...'
& dotnet.exe publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --nologo `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -p:ContinuousIntegrationBuild=true `
    --output $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }

$enginePath = Join-Path $publishRoot 'MailchimpMomentusSync.exe'
if (-not (Test-Path -LiteralPath $enginePath -PathType Leaf)) {
    throw "Published engine was not found: $enginePath"
}

Copy-Item -LiteralPath $enginePath -Destination $appFolder -Force

$packageFiles = @(
    'Run Mailchimp Sync.cmd',
    'Run Mailchimp Sync.vbs',
    'Run-MailchimpSync.ps1',
    'Setup Mailchimp Sync.cmd',
    'Setup Mailchimp Sync.vbs',
    'Setup-MailchimpSync.ps1',
    'Manage Dropdown Lists.cmd',
    'Manage Dropdown Lists.vbs',
    'ImportMappings.json',
    'Instructions.txt',
    'README.md',
    'START-HERE.txt',
    'VERSION.txt',
    'QA-REPORT.txt',
    'BUILD-STATUS.txt'
)
foreach ($name in $packageFiles) {
    $source = Join-Path $projectRoot $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required package file is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination $packageRoot -Force
}

$engineHash = (Get-FileHash -LiteralPath $enginePath -Algorithm SHA256).Hash
$catalogHash = (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash
@(
    'KALLMAN MAILCHIMP TO MOMENTUS SYNC - BUILD INFO',
    '================================================',
    '',
    ('Built UTC: ' + [DateTime]::UtcNow.ToString('o')),
    'Application version: 3.3.0',
    ('Catalog version: ' + [string]$catalog.Version),
    ('Campaign mappings: ' + @($catalog.CampaignTypes).Count),
    ('Salesperson mappings: ' + @($catalog.Salespeople).Count),
    ('Dotnet SDK: ' + $dotnetVersion),
    'Runtime: win-x64',
    ('Engine SHA-256: ' + $engineHash),
    ('ImportMappings.json SHA-256: ' + $catalogHash)
) | Set-Content -LiteralPath (Join-Path $packageRoot 'BUILD-INFO.txt') -Encoding UTF8

Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -Force
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
($zipHash + '  ' + [IO.Path]::GetFileName($zipPath)) |
    Set-Content -LiteralPath $zipHashPath -Encoding ASCII

Write-Host ''
Write-Host 'Release package created:'
Write-Host $zipPath
Write-Host 'SHA-256:'
Write-Host $zipHash
