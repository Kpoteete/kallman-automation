$ErrorActionPreference = "Stop"

Write-Host "GETTING STARTED"
Write-Host "Starting Exhibitors Pull..."
Write-Host "Folder: $PSScriptRoot"

Set-Location $PSScriptRoot

dotnet run
