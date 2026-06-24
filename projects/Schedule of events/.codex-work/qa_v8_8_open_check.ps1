$ErrorActionPreference = "Stop"
$path = "C:\kwi-automations\projects\Schedule of events\Schedule of Events V8.8 - Codex No Stale Formula Template.xlsx"
$json = "C:\kwi-automations\projects\Schedule of events\.codex-work\qa_v8_8_open_check.json"

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
        catch [System.Runtime.InteropServices.COMException] {
            $last = $_
            Start-Sleep -Milliseconds 250
        }
    }
    throw $last
}
try {
    $wb = Retry { $excel.Workbooks.Open($path) }
    $result = [ordered]@{
        opened = $true
        calculation = $excel.Calculation
        values = [ordered]@{}
    }
    foreach ($name in @("Daily Schedule","Weekly Calendar New","Location Schedule","Run of Show","Instructions")) {
        $ws = Retry { $wb.Worksheets.Item($name) }
        $result.values[$name] = [ordered]@{
            A1 = [string](Retry { $ws.Range("A1").Text })
            A4 = [string](Retry { $ws.Range("A4").Text })
            B4 = [string](Retry { $ws.Range("B4").Text })
        }
        Release-ComObject $ws
    }
    Retry { $wb.Save() } | Out-Null
    Retry { $wb.Close($true) } | Out-Null
    Release-ComObject $wb
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $json -Encoding UTF8
    Write-Host $json
}
finally {
    $excel.Quit()
    Release-ComObject $excel
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
