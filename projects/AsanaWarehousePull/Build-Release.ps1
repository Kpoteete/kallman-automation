[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot 'AsanaWarehousePull.csproj'
$publishFolder = Join-Path $projectRoot 'publish'

dotnet publish $projectFile `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishFolder

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$executable = Join-Path $publishFolder 'AsanaWarehousePull.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Publish completed without the expected executable: $executable"
}

Write-Host "Published: $executable"
