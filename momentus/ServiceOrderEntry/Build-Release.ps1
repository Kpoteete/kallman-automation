[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\..\artifacts\publish\ServiceOrderEntry'),
    [string]$ReleaseId = ('scheduled-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
if ($ReleaseId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_-]+$') { throw 'ReleaseId must be a simple directory name.' }
$destination = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $ReleaseId
if (Test-Path -LiteralPath $destination) { throw "Release already exists; preserving it: $destination" }
if (-not $SkipTests) {
    & dotnet test (Join-Path $PSScriptRoot 'tests\ServiceOrderEntry.Tests\ServiceOrderEntry.Tests.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests did not pass; no package published.' }
}
& dotnet publish (Join-Path $PSScriptRoot 'ServiceOrderEntry.csproj') -c Release -r win-x64 --self-contained true -o $destination --nologo
if ($LASTEXITCODE -ne 0) { throw 'Release publication failed.' }
foreach ($file in @('ServiceOrderEntry.exe','billing-config.json','SalesRepLookup.xlsx','OrderCategoryLookup.xlsx','Run-Scheduled.ps1','Install-ServerTask.ps1','Verify-ServerTask.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $destination $file))) { throw "Required package file missing: $file" }
}
if ((Get-FileHash -LiteralPath (Join-Path $destination 'billing-config.json')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'billing-config.json')).Hash) { throw 'Published billing configuration does not match source.' }
Get-ChildItem -LiteralPath $destination -File | Sort-Object Name | Get-FileHash -Algorithm SHA256 | Select-Object @{n='File';e={[IO.Path]::GetFileName($_.Path)}},Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'release-manifest.json') -Encoding utf8
Write-Output "Published immutable self-contained release: $destination"
