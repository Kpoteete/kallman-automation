[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$executable = Join-Path $projectRoot 'publish\GLDataMillPull.exe'
$logFolder = Join-Path $projectRoot 'logs'

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published executable not found: $executable"
}

New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
$logPath = Join-Path $logFolder ("incremental-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

"[$(Get-Date -Format o)] Starting GL Datamill incremental run." |
    Out-File -LiteralPath $logPath -Append -Encoding utf8
& $executable incremental *>> $logPath
$runExitCode = $LASTEXITCODE
"[$(Get-Date -Format o)] GL Datamill pull exited with code $runExitCode." |
    Out-File -LiteralPath $logPath -Append -Encoding utf8

exit $runExitCode
