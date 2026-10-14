[CmdletBinding()]
param(
    [string]$TaskName = 'Kallman Momentus Service Order Entry',
    [switch]$EnableLiveUpdates,
    [switch]$ReplaceExistingTask,
    [switch]$ValidateOnly,
    [ValidateRange(1,10)][int]$MaxUpdates = 10,
    [ValidateRange(1000,1000000)][int]$MaxResults = 1000000
)

$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'Run-Scheduled.ps1'
$hostPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
foreach ($file in @('ServiceOrderEntry.exe','Run-Scheduled.ps1','Verify-ServerTask.ps1','billing-config.json','SalesRepLookup.xlsx','OrderCategoryLookup.xlsx')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $file) -PathType Leaf)) { throw "Required package file missing: $file" }
}
foreach ($name in @('MOMENTUS_APIUSER','MOMENTUS_SECRET','MOMENTUS_KEY')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name, 'Machine'))) { throw "Required machine environment variable missing: $name" }
}
$baseArguments = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$runner`""
$actionArguments = "$baseArguments -MaxUpdates $MaxUpdates -MaxResults $MaxResults"
if ($EnableLiveUpdates) { $actionArguments += ' -Apply' }
$action = New-ScheduledTaskAction -Execute $hostPath -Argument $actionArguments -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(2)) -RepetitionInterval (New-TimeSpan -Minutes 15)
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
# Do not terminate an in-flight Momentus write merely because 15 minutes elapsed.
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -DisallowHardTerminate
$mode = if ($EnableLiveUpdates) { 'LIVE' } else { 'PREVIEW' }
if ($ValidateOnly) {
    Write-Output "Package validation passed. Proposed task: $TaskName; $mode; SYSTEM; interval=$($trigger.Repetition.Interval); overlap=$($settings.MultipleInstances); execution limit=$($settings.ExecutionTimeLimit). No task installed."
    return
}
$identity = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this installer in PowerShell as Administrator on the computer that will execute the job.' }
$existing = Get-ScheduledTask -TaskName $TaskName -TaskPath '\' -ErrorAction SilentlyContinue
if ($existing -and -not $ReplaceExistingTask) { throw 'Task already exists. Inspect it first; use -ReplaceExistingTask to replace it with a preserved XML backup.' }
if ($existing -and $existing.State -eq 'Running') { throw 'Existing task is running. Wait for completion before replacement.' }
if ($existing) {
    $backupFolder = Join-Path $env:ProgramData ('Kallman\ServiceOrderEntry\task-backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $backupFolder -Force | Out-Null
    Export-ScheduledTask -TaskName $TaskName -TaskPath '\' | Set-Content -LiteralPath (Join-Path $backupFolder 'task.xml') -Encoding utf8
    Write-Output "Prior task preserved: $backupFolder"
}
# Verify credentials, runtime, lookups, journals and storage under the actual scheduled identity.
$probeName = "$TaskName Probe $([guid]::NewGuid().ToString('N'))"
$probeAction = New-ScheduledTaskAction -Execute $hostPath -Argument "$baseArguments -Probe" -WorkingDirectory $PSScriptRoot
try {
    Register-ScheduledTask -TaskName $probeName -Action $probeAction -Principal $principal -Settings $settings -Description 'Temporary local-only ServiceOrderEntry identity/storage probe.' | Out-Null
    Start-ScheduledTask -TaskName $probeName
    $deadline = (Get-Date).AddSeconds(90)
    do {
        Start-Sleep -Seconds 1
        $probeTask = Get-ScheduledTask -TaskName $probeName
        $probeInfo = Get-ScheduledTaskInfo -TaskName $probeName
        $completed = $probeInfo.LastRunTime.Year -gt 2000 -and $probeTask.State -ne 'Running'
    } while (-not $completed -and (Get-Date) -lt $deadline)
    if (-not $completed -or $probeInfo.LastTaskResult -ne 0) { throw 'SYSTEM probe did not pass. Inspect ProgramData\Kallman\ServiceOrderEntry\operations\logs. The recurring task was not installed.' }
}
finally {
    $probeTask = Get-ScheduledTask -TaskName $probeName -ErrorAction SilentlyContinue
    if ($probeTask) {
        if ($probeTask.State -eq 'Running') { Stop-ScheduledTask -TaskName $probeName }
        Unregister-ScheduledTask -TaskName $probeName -Confirm:$false
    }
}
$register = @{ TaskName=$TaskName; TaskPath='\'; Action=$action; Trigger=$trigger; Principal=$principal; Settings=$settings; Description="ServiceOrderEntry $mode every 15 minutes; all events; $MaxUpdates-order maximum; verified orders and completed exhibitors activate in live mode." }
if ($existing) { $register.Force = $true }
Register-ScheduledTask @register | Out-Null
try { & (Join-Path $PSScriptRoot 'Verify-ServerTask.ps1') -TaskName $TaskName }
catch { Disable-ScheduledTask -TaskName $TaskName | Out-Null; throw }
Write-Output "Installed $TaskName in $mode mode every 15 minutes. First automatic run is in approximately two minutes."
