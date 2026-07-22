[CmdletBinding()]
param(
    [string]$TaskName = 'Kallman Momentus Activities Pull',
    [datetime]$DailyAt = '06:30'
)

$ErrorActionPreference = 'Stop'
$requiredMachineVariables = @(
    'MOMENTUS_APIUSER',
    'MOMENTUS_SECRET',
    'MOMENTUS_KEY',
    'KALLMAN_DATA_WAREHOUSE'
)

$missing = foreach ($name in $requiredMachineVariables) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name, 'Machine'))) {
        $name
    }
}

if ($missing) {
    throw "Set these machine-level environment variables before installing the task: $($missing -join ', ')"
}

$runner = Join-Path $PSScriptRoot 'Run-Daily.ps1'
$executable = Join-Path $PSScriptRoot 'publish\ActivitiesPull.exe'
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw "Runner not found: $runner"
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published executable not found: $executable"
}

$actionArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$runner`""
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $actionArguments -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -Daily -At $DailyAt
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2)

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description 'Daily read-only Momentus activities warehouse incremental pull.' `
    -Force | Out-Null

Write-Host "Installed Windows scheduled task '$TaskName' for $($DailyAt.ToString('HH:mm'))."
Write-Host "Runner: $runner"
