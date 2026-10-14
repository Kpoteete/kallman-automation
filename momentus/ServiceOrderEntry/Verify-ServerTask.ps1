[CmdletBinding()]
param(
    [string]$TaskName = 'Kallman Momentus Service Order Entry',
    [string]$ReleaseFolder = $PSScriptRoot,
    [switch]$AllowDisabled
)
$ErrorActionPreference = 'Stop'
$task = Get-ScheduledTask -TaskName $TaskName -TaskPath '\'
$info = Get-ScheduledTaskInfo -TaskName $TaskName -TaskPath '\'
[xml]$xml = Export-ScheduledTask -TaskName $TaskName -TaskPath '\'
$ns = [Xml.XmlNamespaceManager]::new($xml.NameTable)
$ns.AddNamespace('t','http://schemas.microsoft.com/windows/2004/02/mit/task')
function Read-TaskValue([string]$xpath) { $node=$xml.SelectSingleNode($xpath,$ns); if ($node) { $node.InnerText } }
$interval = Read-TaskValue '//t:TimeTrigger/t:Repetition/t:Interval'
$duration = Read-TaskValue '//t:TimeTrigger/t:Repetition/t:Duration'
$overlap = Read-TaskValue '//t:Settings/t:MultipleInstancesPolicy'
$limit = Read-TaskValue '//t:Settings/t:ExecutionTimeLimit'
$hardTerminate = Read-TaskValue '//t:Settings/t:AllowHardTerminate'
$enabled = Read-TaskValue '//t:Settings/t:Enabled'
$endBoundary = Read-TaskValue '//t:TimeTrigger/t:EndBoundary'
$arguments = Read-TaskValue '//t:Actions/t:Exec/t:Arguments'
$command = Read-TaskValue '//t:Actions/t:Exec/t:Command'
$expectedRunner = Join-Path $ReleaseFolder 'Run-Scheduled.ps1'
$mismatches = @(
    if ($interval -ne 'PT15M') { "Interval=[$interval], expected [PT15M]" }
    if ($duration) { "Repetition duration=[$duration], expected no duration" }
    if ($endBoundary) { "End boundary=[$endBoundary], expected no end boundary" }
    if ($overlap -ne 'IgnoreNew') { "Overlap=[$overlap], expected [IgnoreNew]" }
    if ($limit -ne 'PT0S') { "Execution time limit=[$limit], expected [PT0S]" }
    if ($hardTerminate -ne 'false') { "AllowHardTerminate=[$hardTerminate], expected [false]" }
    if ($enabled -ne 'true' -and -not ($AllowDisabled -and $enabled -eq 'false')) { "Enabled=[$enabled], expected [true]" }
)
if ($mismatches.Count) { throw ('Scheduled task settings mismatch: ' + ($mismatches -join '; ')) }
if ($arguments.IndexOf('"' + $expectedRunner + '"', [StringComparison]::OrdinalIgnoreCase) -lt 0 -or $command -ne (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe')) { throw 'Task action does not point to this published package.' }
$principalId = Read-TaskValue '//t:Principals/t:Principal/t:UserId'
if ($principalId -notin @('S-1-5-18','SYSTEM')) { throw 'Task principal is not SYSTEM.' }
$hexResult = '0x{0:X8}' -f $info.LastTaskResult
$result = if ($task.State -eq 'Running' -or $info.LastTaskResult -eq 267009) { 'RUNNING - not yet verified' } elseif ($enabled -eq 'false') { 'DISABLED - not operational' } elseif ($info.LastRunTime.Year -lt 2000) { 'NOT RUN YET' } else { switch ($info.LastTaskResult) { 0 { 'SUCCESS' } 2 { 'REVIEW / DEFERRED' } 3 { 'UNKNOWN WRITE OUTCOME' } default { 'FAILED' } } }
[pscustomobject]@{ Task=$TaskName; State=$task.State; Enabled=$enabled; Mode=$(if ($arguments -match '(?:^|\s)-Apply(?:\s|$)') { 'LIVE' } else { 'PREVIEW' }); Interval=$interval; Principal=$principalId; Overlap=$overlap; LastRun=$info.LastRunTime; NextRun=$info.NextRunTime; LastTaskResult=$hexResult; Outcome=$result; Runner=$expectedRunner; Summary=(Join-Path $env:ProgramData 'Kallman\ServiceOrderEntry\operations\last-run.json') } | Format-List
