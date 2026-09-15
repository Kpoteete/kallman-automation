[CmdletBinding()]
param(
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'publish\ServiceOrderBoothUpdater.exe'
$stateFolder = Join-Path $PSScriptRoot 'state'
$logFolder = Join-Path $PSScriptRoot 'logs'

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published executable not found: $executable"
}

New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
$logPath = Join-Path $logFolder ("hourly-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$modeArguments = if ($Apply) { @('apply', '--confirm-update-booth-number') } else { @('preview') }

"[$(Get-Date -Format o)] Starting Service Order Booth updater ($($modeArguments[0]))." |
    Out-File -LiteralPath $logPath -Append -Encoding utf8
& $executable @modeArguments --state-folder $stateFolder *>> $logPath
$runExitCode = $LASTEXITCODE
"[$(Get-Date -Format o)] Service Order Booth updater exited with code $runExitCode." |
    Out-File -LiteralPath $logPath -Append -Encoding utf8

exit $runExitCode
