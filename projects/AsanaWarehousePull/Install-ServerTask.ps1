[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TaskName = 'Kallman Asana Warehouse Pull',
    [datetime]$DailyAt = '06:00'
)

$ErrorActionPreference = 'Stop'
$requiredMachineVariables = @('ASANA_PAT', 'KALLMAN_DATA_WAREHOUSE')
$missing = foreach ($name in $requiredMachineVariables) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name, 'Machine'))) {
        $name
    }
}

if ($missing) {
    throw "Set these machine-level environment variables before installing the SYSTEM task: $($missing -join ', ')"
}

$runner = Join-Path $PSScriptRoot 'Run-Daily.ps1'
$executable = Join-Path $PSScriptRoot 'publish\AsanaWarehousePull.exe'
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw "Runner not found: $runner"
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published executable not found: $executable. Run Build-Release.ps1 first."
}

$actionArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$runner`""
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $actionArguments -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -Daily -At $DailyAt
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Hours 4)

if ($PSCmdlet.ShouldProcess($TaskName, "Install daily SYSTEM task at $($DailyAt.ToString('HH:mm'))")) {
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $action `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings `
        -Description 'Daily read-only Asana normalized CSV warehouse pull.' `
        -Force | Out-Null

    Write-Host "Installed Windows scheduled task '$TaskName' for $($DailyAt.ToString('HH:mm'))."
    Write-Host "Runner: $runner"
}
