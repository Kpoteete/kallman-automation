[CmdletBinding()]
param(
    [string[]]$InputFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Convert-SecureStringToPlainText {
    param([Parameter(Mandatory)][Security.SecureString]$SecureValue)

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Read-CsvForPreflight {
    param([Parameter(Mandatory)][string]$Path)

    Add-Type -AssemblyName Microsoft.VisualBasic
    $parser = [Microsoft.VisualBasic.FileIO.TextFieldParser]::new($Path)
    $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
    $parser.SetDelimiters(',')
    $parser.HasFieldsEnclosedInQuotes = $true

    try {
        if ($parser.EndOfData) {
            throw 'The file is empty.'
        }

        $header = $parser.ReadFields()
        if ($header.Count -lt 12) {
            throw "Expected at least 12 columns (A through L), but found $($header.Count)."
        }

        $eligibleRows = 0
        $rowNumber = 1
        $eventIds = [Collections.Generic.HashSet[int]]::new()

        while (-not $parser.EndOfData) {
            $rowNumber++
            $fields = $parser.ReadFields()

            if ($fields.Count -lt 12) {
                throw "Row $rowNumber has only $($fields.Count) columns; at least 12 are required."
            }

            $eventId = 0
            $clicks = 0
            $opens = 0
            $hasEvent = [int]::TryParse($fields[0].Trim(), [ref]$eventId) -and $eventId -gt 0
            [void][int]::TryParse($fields[7].Trim(), [ref]$clicks)
            [void][int]::TryParse($fields[8].Trim(), [ref]$opens)
            $hasContact = -not [string]::IsNullOrWhiteSpace($fields[11])

            if ($hasEvent -and $hasContact -and ($clicks -gt 0 -or $opens -gt 0)) {
                [void]$eventIds.Add($eventId)
                $eligibleRows++
            }
        }

        if ($eligibleRows -eq 0) {
            throw 'No usable engagement rows were found. Check Event ID (A), Clicks/Opens (H/I), and Contact Account Code (L).'
        }

        if ($eventIds.Count -ne 1) {
            throw 'The usable rows must contain exactly one Event ID. Split different events into separate CSV files.'
        }

        return [pscustomobject]@{
            Path = $Path
            EventId = @($eventIds)[0]
            EligibleRows = $eligibleRows
        }
    }
    finally {
        $parser.Close()
    }
}

function Select-CsvFiles {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = [Windows.Forms.OpenFileDialog]::new()
    $dialog.Title = 'Choose the Mailchimp CSV file'
    $dialog.Filter = 'CSV files (*.csv)|*.csv'
    $dialog.Multiselect = $true
    $dialog.CheckFileExists = $true

    if ($dialog.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) {
        return @()
    }

    return @($dialog.FileNames)
}

function Find-SyncCommand {
    $packagedExe = Join-Path $PSScriptRoot 'app\MailchimpMomentusSync.exe'
    if (Test-Path -LiteralPath $packagedExe) {
        return [pscustomobject]@{ Kind = 'Exe'; Path = $packagedExe }
    }

    $repoExe = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts\MailchimpMomentusSync\win-x64\MailchimpMomentusSync.exe'
    if (Test-Path -LiteralPath $repoExe) {
        return [pscustomobject]@{ Kind = 'Exe'; Path = $repoExe }
    }

    $projectPath = Join-Path $PSScriptRoot 'MailchimpMomentusSync.csproj'
    if ((Test-Path -LiteralPath $projectPath) -and (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        return [pscustomobject]@{ Kind = 'Dotnet'; Path = $projectPath }
    }

    throw 'The sync application is missing. Ask the automation owner for the complete release folder.'
}

$appRoot = Join-Path $env:LOCALAPPDATA 'Kallman\MailchimpMomentusSync'
$credentialPath = Join-Path $appRoot 'credentials.clixml'

Write-Host ''
Write-Host 'Mailchimp to Momentus Sync'
Write-Host '---------------------------'

if (-not (Test-Path -LiteralPath $credentialPath)) {
    Write-Host 'First-time setup is needed.'
    & (Join-Path $PSScriptRoot 'Setup-MailchimpSync.ps1')
    if (-not (Test-Path -LiteralPath $credentialPath)) {
        throw 'Setup did not complete.'
    }
}

if (-not $InputFile -or $InputFile.Count -eq 0) {
    $InputFile = @(Select-CsvFiles)
}

if (-not $InputFile -or $InputFile.Count -eq 0) {
    Write-Host 'No file was selected. Nothing was changed.'
    exit 0
}

$reviews = foreach ($path in $InputFile) {
    $resolvedPath = (Resolve-Path -LiteralPath $path).Path
    if ([IO.Path]::GetExtension($resolvedPath) -ine '.csv') {
        throw "Only CSV files are supported: $resolvedPath"
    }

    Read-CsvForPreflight -Path $resolvedPath
}

Write-Host ''
Write-Host 'Ready to process:'
foreach ($review in $reviews) {
    Write-Host ("  {0}  |  Event {1}  |  {2} usable engagement rows" -f
        [IO.Path]::GetFileName($review.Path), $review.EventId, $review.EligibleRows)
}

Write-Host ''
Write-Host 'This makes LIVE Momentus changes: it can add exhibitors, activities, and notes.'
$confirmation = Read-Host 'Type RUN to continue, or press Enter to cancel'
if ($confirmation -cne 'RUN') {
    Write-Host 'Cancelled. Nothing was changed.'
    exit 0
}

$credential = Import-Clixml -LiteralPath $credentialPath
if ([string]::IsNullOrWhiteSpace([string]$credential.ApiUser) -or
    $null -eq $credential.Secret -or $null -eq $credential.Key) {
    throw 'The saved setup is incomplete. Run "Setup Mailchimp Sync.cmd" again.'
}

$runId = (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = Join-Path $appRoot ('runs\' + $runId)
$pendingPath = Join-Path $runRoot 'pending'
$completePath = Join-Path $runRoot 'complete'
New-Item -ItemType Directory -Path $pendingPath -Force | Out-Null
New-Item -ItemType Directory -Path $completePath -Force | Out-Null

foreach ($review in $reviews) {
    Copy-Item -LiteralPath $review.Path -Destination (Join-Path $pendingPath ([IO.Path]::GetFileName($review.Path)))
}

$command = Find-SyncCommand
$env:MOMENTUS_APIUSER = [string]$credential.ApiUser
$env:MOMENTUS_SECRET = Convert-SecureStringToPlainText -SecureValue $credential.Secret
$env:MOMENTUS_KEY = Convert-SecureStringToPlainText -SecureValue $credential.Key

try {
    Push-Location $runRoot
    try {
        if ($command.Kind -eq 'Exe') {
            & $command.Path --apply
        }
        else {
            dotnet run --project $command.Path --configuration Release -- --apply
        }
        $syncExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}
finally {
    Remove-Item Env:MOMENTUS_APIUSER -ErrorAction SilentlyContinue
    Remove-Item Env:MOMENTUS_SECRET -ErrorAction SilentlyContinue
    Remove-Item Env:MOMENTUS_KEY -ErrorAction SilentlyContinue
    $credential = $null
}

Write-Host ''
if ($syncExitCode -eq 0) {
    Write-Host 'SYNC COMPLETED.'
    Write-Host ('Results and audit files: ' + $completePath)
    Start-Process explorer.exe -ArgumentList ('"' + $completePath + '"')
}
else {
    Write-Host ('SYNC NEEDS ATTENTION (exit code ' + $syncExitCode + ').')
    Write-Host ('Run folder retained for troubleshooting: ' + $runRoot)
}

exit $syncExitCode
