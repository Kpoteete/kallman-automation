[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'WarehousePublisher.csproj'
$output = Join-Path $PSScriptRoot 'publish'

dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $output `
    --nologo

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "WarehousePublisher published to: $output"
