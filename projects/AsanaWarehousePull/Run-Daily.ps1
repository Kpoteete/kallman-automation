[CmdletBinding()]
param(
    [ValidateSet('full', 'probe')]
    [string]$Mode = 'full',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$executable = Join-Path $PSScriptRoot 'publish\AsanaWarehousePull.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published executable not found: $executable. Run Build-Release.ps1 first."
}

if ([string]::IsNullOrWhiteSpace($env:ASANA_PAT)) {
    throw 'ASANA_PAT is not available to this process. Configure it for the scheduled-task account; SYSTEM tasks require a machine-level variable.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    if ([string]::IsNullOrWhiteSpace($env:KALLMAN_DATA_WAREHOUSE)) {
        throw 'KALLMAN_DATA_WAREHOUSE is not available to this process. Set it to the server data-warehouse root.'
    }
    $OutputDirectory = Join-Path $env:KALLMAN_DATA_WAREHOUSE 'Asana'
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$logDirectory = Join-Path $PSScriptRoot 'logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logPath = Join-Path $logDirectory ("AsanaWarehousePull-{0:yyyyMMdd}.log" -f (Get-Date))

$arguments = @('--mode', $Mode, '--output', $OutputDirectory)
$started = Get-Date
"[$($started.ToString('o'))] Starting Asana warehouse $Mode run. Output: $OutputDirectory" | Add-Content -LiteralPath $logPath

& $executable @arguments *>> $logPath
$exitCode = $LASTEXITCODE

$completed = Get-Date
"[$($completed.ToString('o'))] Completed with exit code $exitCode after $([math]::Round(($completed - $started).TotalMinutes, 2)) minutes." | Add-Content -LiteralPath $logPath

if ($exitCode -ne 0) {
    throw "AsanaWarehousePull failed with exit code $exitCode. Review $logPath"
}

Write-Host "Asana warehouse $Mode run completed successfully."
Write-Host "Output: $OutputDirectory"
Write-Host "Log: $logPath"
