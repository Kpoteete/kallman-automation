$ErrorActionPreference = "Stop"
$baseDir = "C:\kwi-automations\projects\Schedule of events"
$source = Join-Path $baseDir "Schedule of Events V8.4 - Codex Tested Template.xlsx"
$output = Join-Path $baseDir "Schedule of Events V8.5 - Codex Tested Template.xlsx"
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
    for ($i=1; $i -le 25; $i++) {
        try { return & $action }
        catch [System.Runtime.InteropServices.COMException] { $last = $_; Start-Sleep -Milliseconds 250 }
    }
    throw $last
}

try {
    $wb = Retry { $excel.Workbooks.Open($output) }
    Retry { $excel.Calculation = -4135 } | Out-Null
    $daily = Retry { $wb.Worksheets.Item("Daily Schedule") }
    Retry { $daily.Range("A110").Value2 = "All-Day Events" } | Out-Null
    Retry { $daily.Range("A112:F140").ClearContents() } | Out-Null
    $formula = '=IFERROR(FILTER(CHOOSE({1,2,3,4,5,6},"All Day",IFERROR(TEXT(''Data Input''!$D$4:$D$484,"m/d/yyyy"),''Data Input''!$D$4:$D$484)&" "&IFERROR(TEXT(''Data Input''!$E$4:$E$484,"h:mm AM/PM"),''Data Input''!$E$4:$E$484)&" - "&IFERROR(TEXT(''Data Input''!$F$4:$F$484,"m/d/yyyy"),''Data Input''!$F$4:$F$484)&" "&IFERROR(TEXT(''Data Input''!$G$4:$G$484,"h:mm AM/PM"),''Data Input''!$G$4:$G$484),''Data Input''!$H$4:$H$484,''Data Input''!$I$4:$I$484,''Data Input''!$B$4:$B$484&IF(''Data Input''!$C$4:$C$484="",""," / "&''Data Input''!$C$4:$C$484),''Data Input''!$R$4:$R$484),(''Data Input''!$H$4:$H$484<>"")*ISNUMBER(SEARCH("ALL DAY",UPPER(''Data Input''!$D$4:$D$484&" "&''Data Input''!$E$4:$E$484&" "&''Data Input''!$F$4:$F$484&" "&''Data Input''!$G$4:$G$484)))*((IFERROR(''Data Input''!$D$4:$D$484=$B$2,FALSE)+IFERROR(''Data Input''!$F$4:$F$484=$B$2,FALSE))>0)*(''Data Input''!$A$4:$A$484=$C$2)*(($E$2="")+(''Data Input''!$B$4:$B$484=$E$2))*(($F$2="")+(''Data Input''!$J$4:$J$484=$F$2))*(($G$2="")+(''Data Input''!$I$4:$I$484=$G$2))*(($H$2="")+(''Data Input''!$P$4:$P$484=$H$2))),"")'
    Retry { $daily.Range("A112").Formula2 = $formula } | Out-Null
    Retry { $daily.Calculate() } | Out-Null
    Retry { $wb.Save() } | Out-Null
    Retry { $wb.Close($true) } | Out-Null
    Release-ComObject $wb
    Write-Host "Created $output"
}
finally {
    $excel.Quit()
    Release-ComObject $excel
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
