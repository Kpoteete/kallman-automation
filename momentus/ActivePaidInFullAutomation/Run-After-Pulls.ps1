param(
    [Parameter(Mandatory)] [string]$ExhibitorPullTaskName,
    [Parameter(Mandatory)] [string]$ServiceOrderPullTaskName,
    [switch]$EnableLiveUpdates
)

$ErrorActionPreference = 'Stop'
$stateFolder = Join-Path $PSScriptRoot 'state'
$logFolder = Join-Path $PSScriptRoot 'logs'
$watermarkPath = Join-Path $stateFolder 'pull-dependency-watermark.json'
New-Item -ItemType Directory -Force -Path $stateFolder, $logFolder | Out-Null
$logPath = Join-Path $logFolder ("pull-coordinator-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

function Write-CoordinatorLog([string]$Message) {
    $line = "{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding utf8
    Write-Host $line
}

function Get-SuccessfulTaskRun([string]$TaskName) {
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
    $info = Get-ScheduledTaskInfo -TaskName $TaskName -TaskPath $task.TaskPath
    if ($task.State -eq 'Running') {
        Write-CoordinatorLog "Waiting: task '$TaskName' is still running."
        return $null
    }
    if ($info.LastTaskResult -ne 0) {
        Write-CoordinatorLog "Waiting: task '$TaskName' last result is $($info.LastTaskResult), not 0."
        return $null
    }
    if ($info.LastRunTime -eq [datetime]::MinValue) {
        Write-CoordinatorLog "Waiting: task '$TaskName' has no recorded run."
        return $null
    }
    return $info.LastRunTime
}

$exhibitorRun = Get-SuccessfulTaskRun $ExhibitorPullTaskName
$serviceOrderRun = Get-SuccessfulTaskRun $ServiceOrderPullTaskName
if (-not $exhibitorRun -or -not $serviceOrderRun) { exit 0 }

$lastExhibitorRun = [datetime]::MinValue
$lastServiceOrderRun = [datetime]::MinValue
if (Test-Path -LiteralPath $watermarkPath) {
    try {
        $watermark = Get-Content -LiteralPath $watermarkPath -Raw | ConvertFrom-Json
        $lastExhibitorRun = [datetime]::Parse($watermark.ExhibitorPullLastRun)
        $lastServiceOrderRun = [datetime]::Parse($watermark.ServiceOrderPullLastRun)
    }
    catch {
        Write-CoordinatorLog "ERROR: dependency watermark is invalid; no automation was run. $($_.Exception.Message)"
        exit 1
    }
}

if ($exhibitorRun -le $lastExhibitorRun -or $serviceOrderRun -le $lastServiceOrderRun) {
    Write-CoordinatorLog "No new successful pair of pull runs is ready."
    exit 0
}

$exe = Join-Path $PSScriptRoot 'publish\ActivePaidInFullAutomation.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Published executable not found: $exe" }
$arguments = if ($EnableLiveUpdates) { @('apply', '--confirm-active-paid-in-full') } else { @('preview') }
Write-CoordinatorLog "Both pulls succeeded. Starting paid-in-full automation in $($arguments[0]) mode."
& $exe @arguments *>> $logPath
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
    Write-CoordinatorLog "Paid-in-full automation failed with exit code $exitCode. Dependency watermark was not advanced."
    exit $exitCode
}

$newWatermark = [ordered]@{
    ExhibitorPullTaskName = $ExhibitorPullTaskName
    ExhibitorPullLastRun = $exhibitorRun.ToString('O')
    ServiceOrderPullTaskName = $ServiceOrderPullTaskName
    ServiceOrderPullLastRun = $serviceOrderRun.ToString('O')
    PaidInFullCompletedOn = (Get-Date).ToString('O')
}
$tempPath = "$watermarkPath.$([guid]::NewGuid().ToString('N')).tmp"
$newWatermark | ConvertTo-Json | Set-Content -LiteralPath $tempPath -Encoding utf8
Move-Item -LiteralPath $tempPath -Destination $watermarkPath -Force
Write-CoordinatorLog "Paid-in-full automation succeeded. Dependency watermark advanced."
exit 0
