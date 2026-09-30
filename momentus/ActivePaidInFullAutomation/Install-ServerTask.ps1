param(
    [Parameter(Mandatory)] [string]$ExhibitorPullTaskName,
    [Parameter(Mandatory)] [string]$ServiceOrderPullTaskName,
    [switch]$EnableLiveUpdates,
    [string]$TaskName = '(MMTS) ACTIVE PAID IN FULL',
    [int]$CheckEveryMinutes = 5
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'publish\ActivePaidInFullAutomation.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Published executable not found: $exe" }
$coordinator = Join-Path $PSScriptRoot 'Run-After-Pulls.ps1'
if (-not (Test-Path -LiteralPath $coordinator)) { throw "Coordinator script not found: $coordinator" }
$mode = if ($EnableLiveUpdates) { '-EnableLiveUpdates' } else { '' }
$arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$coordinator`" -ExhibitorPullTaskName `"$ExhibitorPullTaskName`" -ServiceOrderPullTaskName `"$ServiceOrderPullTaskName`" $mode".Trim()
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddMinutes(5) -RepetitionInterval (New-TimeSpan -Minutes $CheckEveryMinutes)
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 50) -MultipleInstances IgnoreNew -StartWhenAvailable
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Description "Runs after successful Exhibitor and Service Order pulls. Momentus exhibitor Active (2) to Active Paid in Full (22)." -Force
Get-ScheduledTask -TaskName $TaskName | Select-Object TaskName, State, @{n='Action';e={$_.Actions.Execute}}, @{n='Arguments';e={$_.Actions.Arguments}}
