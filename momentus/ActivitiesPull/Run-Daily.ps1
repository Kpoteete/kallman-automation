[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$executable = Join-Path $projectRoot 'publish\ActivitiesPull.exe'
$logFolder = Join-Path $projectRoot 'logs'

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published executable not found: $executable"
}

New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
$logPath = Join-Path $logFolder ("incremental-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

"[$(Get-Date -Format o)] Starting Activities Pull incremental run." | Out-File -LiteralPath $logPath -Append -Encoding utf8
& $executable incremental *>> $logPath
$runExitCode = $LASTEXITCODE
"[$(Get-Date -Format o)] Activities Pull exited with code $runExitCode." | Out-File -LiteralPath $logPath -Append -Encoding utf8

exit $runExitCode
