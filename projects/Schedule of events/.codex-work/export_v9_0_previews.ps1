$ErrorActionPreference = "Stop"
$path = "C:\kwi-automations\projects\Schedule of events\Schedule of Events V9.0 - Codex Weekly Cleanup.xlsx"
$outDir = "C:\kwi-automations\projects\Schedule of events\.codex-work\v9_0_previews"
if (Test-Path $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$excel.AskToUpdateLinks = $false
$excel.EnableEvents = $false
function Release-ComObject($obj) {
    if ($null -ne $obj -and [System.Runtime.InteropServices.Marshal]::IsComObject($obj)) {
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($obj)
    }
}
function Retry([scriptblock]$action) {
    $last = $null
    for ($i=1; $i -le 25; $i++) {
        try { return & $action }
        catch [System.Runtime.InteropServices.COMException] { $last = $_; Start-Sleep -Milliseconds 250 }
    }
    throw $last
}
try {
    $wb = Retry { $excel.Workbooks.Open($path) }
    foreach ($name in @("Weekly Calendar","Location Schedule","Sponsors","Speakers","Required Attendees")) {
        $ws = Retry { $wb.Worksheets.Item($name) }
        $pdf = Join-Path $outDir (($name -replace '[\\/:*?"<>| ]','_') + ".pdf")
        Retry { $ws.ExportAsFixedFormat(0, $pdf) } | Out-Null
        Release-ComObject $ws
    }
    Retry { $wb.Close($false) } | Out-Null
    Release-ComObject $wb
    Write-Host $outDir
}
finally {
    $excel.Quit()
    Release-ComObject $excel
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
