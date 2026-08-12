param(
    [Parameter(Mandatory = $true)]
    [string]$InputFile,
    [int]$Limit = 0,
    [switch]$DryRun,
    [string]$Ledger = ""
)

$args = @($InputFile)
if ($Limit -gt 0) { $args += @("--limit", $Limit) }
if ($DryRun) { $args += "--dry-run" }
if ($Ledger) { $args += @("--ledger", $Ledger) }

py .\momentus_merge_runner.py @args
