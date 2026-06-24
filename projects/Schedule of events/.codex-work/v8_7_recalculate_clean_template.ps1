$ErrorActionPreference = "Stop"
$baseDir = "C:\kwi-automations\projects\Schedule of events"
$source = Join-Path $baseDir "Schedule of Events V8.6 - Codex Clean Tested Template.xlsx"
$output = Join-Path $baseDir "Schedule of Events V8.7 - Codex Recalculated Template.xlsx"
if (Test-Path $output) { Remove-Item -LiteralPath $output -Force }
Copy-Item -LiteralPath $source -Destination $output -Force

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
    for ($i=1; $i -le 30; $i++) {
        try { return & $action }
        catch [System.Runtime.InteropServices.COMException] {
            $last = $_
            Start-Sleep -Milliseconds 250
        }
    }
    throw $last
}

try {
    $wb = Retry { $excel.Workbooks.Open($output) }
    Retry { $excel.Calculation = -4105 } | Out-Null # xlCalculationAutomatic
    Retry { $wb.ForceFullCalculation = $false } | Out-Null
    Retry { $wb.PrecisionAsDisplayed = $false } | Out-Null
    Retry { $excel.CalculateFullRebuild() } | Out-Null

    $checks = [ordered]@{}
    foreach ($name in @("Data Input","Daily Schedule","Weekly Calendar New","Location Schedule","Sponsors","Speakers","Required Attendees","Run of Show","Instructions")) {
        $ws = Retry { $wb.Worksheets.Item($name) }
        Retry { $ws.Calculate() } | Out-Null
        $checks[$name] = [ordered]@{
            A1 = [string](Retry { $ws.Range("A1").Text })
            A4 = [string](Retry { $ws.Range("A4").Text })
        }
        Release-ComObject $ws
    }

    Retry { $wb.Save() } | Out-Null
    Retry { $wb.Close($true) } | Out-Null
    Release-ComObject $wb

    $result = [ordered]@{
        output = $output
        checks = $checks
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $baseDir ".codex-work\v8_7_recalculate_result.json") -Encoding UTF8
    Write-Host "Created $output"
}
finally {
    $excel.Quit()
    Release-ComObject $excel
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
