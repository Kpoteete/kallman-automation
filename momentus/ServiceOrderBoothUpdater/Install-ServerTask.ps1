[CmdletBinding()]
param(
    [string]$TaskName = 'Kallman Momentus Service Order Booth Updater',
    [switch]$EnableLiveUpdates
)

$ErrorActionPreference = 'Stop'
$requiredMachineVariables = @('MOMENTUS_APIUSER', 'MOMENTUS_SECRET', 'MOMENTUS_KEY')
$missing = foreach ($name in $requiredMachineVariables) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name, 'Machine'))) { $name }
}
if ($missing) {
    throw "Set these machine-level environment variables before installing the task: $($missing -join ', ')"
}

$runner = Join-Path $PSScriptRoot 'Run-Hourly.ps1'
$executable = Join-Path $PSScriptRoot 'publish\ServiceOrderBoothUpdater.exe'
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) { throw "Runner not found: $runner" }
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Published executable not found: $executable" }

$liveArgument = if ($EnableLiveUpdates) { ' -Apply' } else { '' }
$actionArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$runner`"$liveArgument"
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $actionArguments -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(2)) -RepetitionInterval (New-TimeSpan -Hours 1)
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 45)

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description 'Hourly Momentus BP activity to service-order BoothNumber reconciliation.' -Force | Out-Null

$mode = if ($EnableLiveUpdates) { 'LIVE' } else { 'PREVIEW' }
Write-Host "Installed '$TaskName' to run hourly in $mode mode."
Write-Host "Runner: $runner"
