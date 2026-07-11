[CmdletBinding()]
param(
    [string]$PublishedRoot = 'C:\Automations\Published'
)

$ErrorActionPreference = 'Stop'
$failures = 0

foreach ($name in 'MOMENTUS_APIUSER', 'MOMENTUS_SECRET', 'MOMENTUS_KEY') {
    $value = [Environment]::GetEnvironmentVariable($name, 'Machine')
    $configured = -not [string]::IsNullOrWhiteSpace($value)
    Write-Host "$name configured: $configured"
    if (-not $configured) { $failures++ }
}

$releaseFile = Join-Path $PublishedRoot 'current-release.txt'
if (Test-Path -LiteralPath $releaseFile) {
    $releaseId = (Get-Content -LiteralPath $releaseFile -Raw).Trim()
    $releasePath = Join-Path $PublishedRoot "releases\$releaseId"
    Write-Host "Current release: $releaseId"
    Write-Host "Release folder exists: $(Test-Path -LiteralPath $releasePath)"
    if (-not (Test-Path -LiteralPath $releasePath)) { $failures++ }
} else {
    Write-Host "No deployed release marker found at $releaseFile"
    $failures++
}

exit $(if ($failures -eq 0) { 0 } else { 1 })
