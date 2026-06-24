$ErrorActionPreference = "Stop"
$path = "C:\kwi-automations\projects\Schedule of events\Schedule of Events V9.0 - Codex Weekly Cleanup.xlsx"
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
    $result = [ordered]@{ opened=$true; values=[ordered]@{} }
    foreach ($name in @("Weekly Calendar","Location Schedule","Sponsors","Speakers","Required Attendees","Run of Show","Daily Schedule")) {
        $ws = Retry { $wb.Worksheets.Item($name) }
        $result.values[$name] = [ordered]@{
            A1 = [string](Retry { $ws.Range("A1").Text })
            A3 = [string](Retry { $ws.Range("A3").Text })
            A4 = [string](Retry { $ws.Range("A4").Text })
            A5 = [string](Retry { $ws.Range("A5").Text })
        }
        Release-ComObject $ws
    }
    Retry { $wb.Close($false) } | Out-Null
    Release-ComObject $wb
    $result | ConvertTo-Json -Depth 5
}
finally {
    $excel.Quit()
    Release-ComObject $excel
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
