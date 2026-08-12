[CmdletBinding()]
param(
    [string]$SummaryRoot = 'C:\Automations\Logs',
    [int]$LookbackHours = 30
)

$ErrorActionPreference = 'Stop'
$cutoff = (Get-Date).AddHours(-$LookbackHours)
$summaries = Get-ChildItem -LiteralPath $SummaryRoot -Recurse -Filter *.json -ErrorAction SilentlyContinue |
    Where-Object LastWriteTime -ge $cutoff |
    ForEach-Object {
        try { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json }
        catch { [pscustomobject]@{ Automation = $_.BaseName; Status = 'InvalidSummary'; Errors = 1; Message = $_.Exception.Message } }
    }

$summaries | Sort-Object Automation, StartedAt |
    Format-Table Automation, Status, RecordsRead, RecordsWritten, Errors, FinishedAt -AutoSize
$unhealthy = @($summaries | Where-Object { $_.Status -notin @('Succeeded', 'Success') -or $_.Errors -gt 0 })
exit $(if ($unhealthy.Count -eq 0) { 0 } else { 1 })
