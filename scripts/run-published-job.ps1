[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$JobName,
    [string]$ExecutableName = $JobName,
    [string[]]$JobArguments = @(),
    [string]$PublishedRoot = 'C:\Automations\Published',
    [string]$LogRoot = 'C:\Automations\Logs'
)

$ErrorActionPreference = 'Stop'
$releaseId = (Get-Content -LiteralPath (Join-Path $PublishedRoot 'current-release.txt') -Raw).Trim()
$jobFolder = Join-Path $PublishedRoot "releases\$releaseId\$JobName"
$executable = Join-Path $jobFolder "$ExecutableName.exe"
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Published executable not found: $executable"
}

$jobLogFolder = Join-Path $LogRoot $JobName
New-Item -ItemType Directory -Path $jobLogFolder -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logPath = Join-Path $jobLogFolder "$stamp.log"

& $executable @JobArguments *>&1 | Tee-Object -FilePath $logPath
$exitCode = $LASTEXITCODE
Write-Host "Job exit code: $exitCode"
Write-Host "Log: $logPath"
exit $exitCode
