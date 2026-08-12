[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$automationRoot = 'C:\kwi-automations'
$logDirectory = Join-Path $automationRoot 'logs\Run-All'
$runStarted = Get-Date
$logPath = Join-Path $logDirectory ("Run-All-{0}.log" -f $runStarted.ToString('yyyy-MM-dd_HH-mm-ss'))

$steps = @(
    [pscustomobject]@{
        Name = 'Exhibitors Pull'
        WorkingDirectory = Join-Path $automationRoot 'momentus\exhibitors-pull'
        Command = 'cmd.exe'
        Arguments = @('/d', '/c', 'call', (Join-Path $automationRoot 'momentus\exhibitors-pull\run.bat'))
    }
    [pscustomobject]@{
        Name = 'Service Orders Pull'
        WorkingDirectory = Join-Path $automationRoot 'momentus\ServiceOrderPull'
        Command = 'cmd.exe'
        Arguments = @('/d', '/c', 'call', (Join-Path $automationRoot 'momentus\ServiceOrderPull\ServiceOrderItemsPull.bat'))
    }
    [pscustomobject]@{
        Name = 'Service Order Items Pull'
        WorkingDirectory = Join-Path $automationRoot 'momentus\ServiceOrderItemsPull'
        Command = 'cmd.exe'
        Arguments = @('/d', '/c', 'call', (Join-Path $automationRoot 'momentus\ServiceOrderItemsPull\ServiceOrderItemsPull.bat'))
    }
    [pscustomobject]@{
        Name = 'Activities Pull (incremental)'
        WorkingDirectory = Join-Path $automationRoot 'momentus\ActivitiesPull'
        Command = 'powershell.exe'
        Arguments = @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $automationRoot 'momentus\ActivitiesPull\Run-Daily.ps1'))
    }
    [pscustomobject]@{
        Name = 'Events Pull'
        WorkingDirectory = Join-Path $automationRoot 'momentus\EventsPull'
        Command = 'cmd.exe'
        Arguments = @('/d', '/c', 'call', (Join-Path $automationRoot 'momentus\EventsPull\EventsPull.bat'))
    }
    [pscustomobject]@{
        Name = 'Notes Pull'
        WorkingDirectory = Join-Path $automationRoot 'momentus\NotesPull'
        Command = 'cmd.exe'
        Arguments = @('/d', '/c', 'call', (Join-Path $automationRoot 'momentus\NotesPull\NotesPull.bat'))
    }
    [pscustomobject]@{
        Name = 'Account Name, Punctuation, and Email Cleanup (live)'
        WorkingDirectory = Join-Path $automationRoot 'momentus\Account_name_punctuation_and_email_cleanup'
        Command = 'dotnet.exe'
        Arguments = @('run', '--project', (Join-Path $automationRoot 'momentus\Account_name_punctuation_and_email_cleanup\Account_name_punctuation_and_email_cleanup.csproj'), '-c', 'Release', '--', '--apply')
    }
    [pscustomobject]@{
        Name = 'Website Correction Daily (live)'
        WorkingDirectory = Join-Path $automationRoot 'momentus\WebsiteCorrectionDaily'
        Command = 'dotnet.exe'
        Arguments = @('run', '--project', (Join-Path $automationRoot 'momentus\WebsiteCorrectionDaily\WebsiteCorrectionDaily.csproj'), '-c', 'Release', '--', '--apply')
    }
    [pscustomobject]@{
        Name = 'Accounts Pull'
        WorkingDirectory = Join-Path $automationRoot 'momentus\Accounts_Pull'
        Command = 'cmd.exe'
        Arguments = @('/d', '/c', 'call', (Join-Path $automationRoot 'momentus\Accounts_Pull\run.bat'))
    }
    [pscustomobject]@{
        Name = 'Registration List Automation'
        WorkingDirectory = Join-Path $automationRoot 'projects\RegistrationListAutomation'
        Command = 'cmd.exe'
        Arguments = @('/d', '/c', 'call', (Join-Path $automationRoot 'projects\RegistrationListAutomation\run.bat'))
    }
    [pscustomobject]@{
        Name = 'Accounts Data Integrity Report'
        WorkingDirectory = Join-Path $automationRoot 'projects\AccountsDataIntegrityReport'
        Command = 'dotnet.exe'
        Arguments = @('run', '--project', (Join-Path $automationRoot 'projects\AccountsDataIntegrityReport\Program_Momentus_AccountExport_WithAffiliations.csproj'), '-c', 'Release')
    }
)

function Write-RunMessage {
    param(
        [Parameter(Mandatory)]
        [string]$Message,
        [ConsoleColor]$Color = [ConsoleColor]::Gray
    )

    $timestampedMessage = '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Host $timestampedMessage -ForegroundColor $Color
    Add-Content -LiteralPath $logPath -Value $timestampedMessage -Encoding UTF8
}

function Invoke-AutomationStep {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Step,
        [Parameter(Mandatory)]
        [int]$Number,
        [Parameter(Mandatory)]
        [int]$Total
    )

    Write-RunMessage "STEP $Number of $Total - STARTING: $($Step.Name)" Cyan
    Push-Location -LiteralPath $Step.WorkingDirectory
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Some native tools write routine progress to stderr. Preserve and log it,
        # then use the process exit code as the authoritative success signal.
        $ErrorActionPreference = 'Continue'
        & $Step.Command @($Step.Arguments) 2>&1 | Tee-Object -FilePath $logPath -Append
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        Pop-Location
    }

    if ($exitCode -ne 0) {
        throw "$($Step.Name) failed with exit code $exitCode."
    }

    Write-RunMessage "STEP $Number of $Total - COMPLETED: $($Step.Name)" Green
    Write-RunMessage ('-' * 72)
}

try {
    if (-not (Test-Path -LiteralPath $automationRoot -PathType Container)) {
        throw "Automation folder not found: $automationRoot"
    }

    if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
        throw 'dotnet.exe was not found. Install or repair the .NET SDK before running this launcher.'
    }

    $missingCredentials = @('MOMENTUS_APIUSER', 'MOMENTUS_SECRET', 'MOMENTUS_KEY') |
        Where-Object { [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_)) }

    if ($missingCredentials.Count -gt 0) {
        throw "Required environment variables are missing: $($missingCredentials -join ', ')"
    }

    foreach ($step in $steps) {
        if (-not (Test-Path -LiteralPath $step.WorkingDirectory -PathType Container)) {
            throw "Required working folder not found: $($step.WorkingDirectory)"
        }

        if ([IO.Path]::IsPathRooted($step.Command) -and -not (Test-Path -LiteralPath $step.Command -PathType Leaf)) {
            throw "Required command not found: $($step.Command)"
        }

        foreach ($argument in $step.Arguments) {
            if ($argument -match '^[A-Za-z]:\\' -and -not (Test-Path -LiteralPath $argument)) {
                throw "Required runner or project not found: $argument"
            }
        }
    }

    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    New-Item -ItemType File -Path $logPath -Force | Out-Null

    Clear-Host
    Write-Host 'KWI AUTOMATION SEQUENCE' -ForegroundColor Cyan
    Write-Host ''
    Write-Host 'This will run 11 automations in order.'
    Write-Host 'Two steps will make live corrections in Momentus:' -ForegroundColor Yellow
    Write-Host '  - Account Name, Punctuation, and Email Cleanup'
    Write-Host '  - Website Correction Daily'
    Write-Host ''
    $confirmation = Read-Host 'Type LIVE to begin, or close this window to cancel'
    if ($confirmation -cne 'LIVE') {
        Write-Host 'Cancelled. No automation was started.' -ForegroundColor Yellow
        exit 2
    }

    Write-RunMessage "RUN STARTED. Log: $logPath" Cyan
    for ($index = 0; $index -lt $steps.Count; $index++) {
        Invoke-AutomationStep -Step $steps[$index] -Number ($index + 1) -Total $steps.Count
    }

    $elapsed = (Get-Date) - $runStarted
    Write-RunMessage ("ALL 11 AUTOMATIONS COMPLETED SUCCESSFULLY in {0:hh\:mm\:ss}." -f $elapsed) Green
    Write-Host ''
    Write-Host "Log saved to: $logPath" -ForegroundColor Cyan
    exit 0
}
catch {
    if (-not (Test-Path -LiteralPath $logDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    }
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        New-Item -ItemType File -Path $logPath -Force | Out-Null
    }

    Write-RunMessage "FAILED: $($_.Exception.Message)" Red
    Write-Host ''
    Write-Host 'The sequence stopped. Later steps were not run.' -ForegroundColor Red
    Write-Host "Review the log: $logPath" -ForegroundColor Yellow
    exit 1
}
