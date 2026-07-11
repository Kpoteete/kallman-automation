[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string]$ArtifactRoot,
    [string]$DestinationRoot = 'C:\Automations\Published'
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $ArtifactRoot).Path
$destination = [IO.Path]::GetFullPath($DestinationRoot)
$releaseId = Get-Date -Format 'yyyyMMdd-HHmmss'
$releaseRoot = Join-Path $destination "releases\$releaseId"

if ($PSCmdlet.ShouldProcess($releaseRoot, "Deploy published automation release")) {
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    Get-ChildItem -LiteralPath $source | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $releaseRoot -Recurse -Force
    }
    Set-Content -LiteralPath (Join-Path $destination 'current-release.txt') -Value $releaseId
    Write-Host "Deployed release $releaseId to $releaseRoot"
    Write-Host "Rollback: change current-release.txt to the previous release ID and update scheduled-task paths."
}
