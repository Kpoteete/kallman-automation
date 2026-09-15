$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'KwiImportLauncher.csproj'
$output = Join-Path $PSScriptRoot 'publish'

Write-Host 'Building KWI Account Import Launcher...' -ForegroundColor Cyan

dotnet restore $project
dotnet publish $project -c Release -r win-x64 --self-contained false -o $output

Write-Host ''
Write-Host 'Build complete:' -ForegroundColor Green
Write-Host (Join-Path $output 'KWI Account Import.exe')
