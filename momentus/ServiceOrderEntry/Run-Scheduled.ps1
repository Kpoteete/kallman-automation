[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$Probe,
    [ValidateRange(1, 10)][int]$MaxUpdates = 10,
    [ValidateRange(1000, 1000000)][int]$MaxResults = 1000000,
    [string]$OperationalRoot = (Join-Path $env:ProgramData 'Kallman\ServiceOrderEntry\operations')
)

$ErrorActionPreference = 'Stop'
$runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([guid]::NewGuid().ToString('N'))
$started = [DateTimeOffset]::Now
$exitCode = 1
$mode = if ($Probe) { 'PROBE' } elseif ($Apply) { 'APPLY' } else { 'PREVIEW' }
$logFolder = Join-Path $OperationalRoot 'logs'
$reportFolder = Join-Path $OperationalRoot 'runs'
$logPath = Join-Path $logFolder ("scheduled-$runId.log")
$summaryPath = Join-Path $OperationalRoot 'last-run.json'
$summaryTemp = Join-Path $OperationalRoot ("last-run-$runId.tmp")
$executable = Join-Path $PSScriptRoot 'ServiceOrderEntry.exe'
try {
    if ($Apply -and $Probe) { throw 'Use either -Apply or -Probe.' }
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Published executable missing: $executable" }
    New-Item -ItemType Directory -Path $logFolder,$reportFolder -Force | Out-Null
    # Refresh only this process so SYSTEM sees current machine credentials without exposing values.
    foreach ($name in @('MOMENTUS_APIUSER','MOMENTUS_SECRET','MOMENTUS_KEY')) {
        $value = [Environment]::GetEnvironmentVariable($name, 'Machine')
        if ([string]::IsNullOrWhiteSpace($value)) { throw "Required machine environment variable missing: $name" }
        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
    $value = $null
    $arguments = @('preview')
    if ($Probe) { $arguments = @('probe') }
    elseif ($Apply) { $arguments = @('apply','--confirm-service-order-entry','--all','--max-updates',"$MaxUpdates") }
    $arguments += @('--max-results',"$MaxResults",'--run-folder',$reportFolder)
    "[$($started.ToString('o'))] $mode; release=$PSScriptRoot; user=$([Security.Principal.WindowsIdentity]::GetCurrent().Name); run=$runId" | Out-File -LiteralPath $logPath -Encoding utf8
    & $executable @arguments 2>&1 | Out-File -LiteralPath $logPath -Append -Encoding utf8
    $exitCode = $LASTEXITCODE
    if ($null -eq $exitCode) { throw 'Executable did not return an exit code.' }
}
catch {
    $exitCode = 1
    if (Test-Path -LiteralPath $logFolder) { $_.Exception.Message | Out-File -LiteralPath $logPath -Append -Encoding utf8 }
    Write-Error $_ -ErrorAction Continue
}
finally {
    $status = switch ($exitCode) { 0 { 'SUCCESS' } 2 { 'REVIEW' } 3 { 'UNKNOWN' } default { 'FAILED' } }
    try {
        New-Item -ItemType Directory -Path $OperationalRoot -Force | Out-Null
        if (Test-Path -LiteralPath $logPath) { "[$([DateTimeOffset]::Now.ToString('o'))] Finished; status=$status; exit=$exitCode" | Out-File -LiteralPath $logPath -Append -Encoding utf8 }
        [ordered]@{ RunId=$runId; Mode=$mode; Status=$status; ExitCode=$exitCode; Started=$started.ToString('o'); Finished=[DateTimeOffset]::Now.ToString('o'); Release=$PSScriptRoot; Log=$logPath; Reports=$reportFolder } |
            ConvertTo-Json | Set-Content -LiteralPath $summaryTemp -Encoding utf8
        Move-Item -LiteralPath $summaryTemp -Destination $summaryPath -Force
    }
    catch { $exitCode = 1; Write-Error 'Operational summary storage failed.' -ErrorAction Continue }
}
Write-Output "ServiceOrderEntry $mode finished with exit $exitCode. Log: $logPath"
exit $exitCode
