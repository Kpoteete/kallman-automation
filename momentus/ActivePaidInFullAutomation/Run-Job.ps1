param([switch]$EnableLiveUpdates)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'publish\ActivePaidInFullAutomation.exe'
$logDir = Join-Path $PSScriptRoot 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$stamp = Get-Date -Format 'yyyy-MM-dd'
$log = Join-Path $logDir "paid-in-full-$stamp.log"
$arguments = if ($EnableLiveUpdates) { @('apply', '--confirm-active-paid-in-full') } else { @('preview') }
& $exe @arguments *>> $log
exit $LASTEXITCODE
