[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputRoot = [IO.Path]::GetFullPath($OutputRoot)

$projects = [ordered]@{
    AccountsPull = 'momentus\Accounts_Pull\Accounts_Pull.csproj'
    ExhibitorsPull = 'momentus\exhibitors-pull\ExhbitorPull.csproj'
    ServiceOrderItemsPull = 'momentus\ServiceOrderItems_FullRebuild\ServiceOrderItemsPull.csproj'
    ServiceOrdersPull = 'momentus\ServiceOrders_FullRebuild\ServiceOrdersPull.csproj'
    AccountImport = 'projects\AccountImport\Program\AccountImport.csproj'
    DuplicateMerging = 'projects\DuplicateMerging\DuplicateMerging.csproj'
    MarketSegmentApplication = 'projects\MarketSegmentApplication\MarketSegmentApplication.csproj'
    RegistrationListAutomation = 'projects\RegistrationListAutomation\RegistrationListAutomation.csproj'
    StaleMomentusAccountReport = 'projects\StaleMomentusAccountReport\src\StaleMomentusAccountReport\StaleMomentusAccountReport.csproj'
}

foreach ($entry in $projects.GetEnumerator()) {
    $projectPath = Join-Path $repoRoot $entry.Value
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "Project not found: $projectPath"
    }
    $destination = Join-Path $outputRoot $entry.Key
    Write-Host "Publishing $($entry.Key) -> $destination"
    dotnet publish $projectPath --configuration $Configuration --output $destination --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "Published artifacts: $outputRoot"
