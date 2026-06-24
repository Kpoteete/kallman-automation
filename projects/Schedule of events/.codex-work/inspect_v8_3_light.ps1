$ErrorActionPreference = "Stop"
$p = "C:\kwi-automations\projects\Schedule of events\Schedule of Events V8.3 - Codex Tested Template.xlsx"
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$excel.AskToUpdateLinks = $false
$excel.EnableEvents = $false
try {
    $wb = $excel.Workbooks.Open($p)
    $excel.Calculation = -4135
    foreach ($name in @("Weekly Calendar New","Location Schedule","Sponsors","Speakers","Required Attendees","Run of Show","Instructions")) {
        $ws = $wb.Worksheets.Item($name)
        Write-Host "[$name]"
        foreach ($addr in @("A1","A4","B4","C4","D4","E4","A5")) {
            $cell = $ws.Range($addr)
            Write-Host $addr "Text=" $cell.Text "Formula=" $cell.Formula2
        }
    }
    $wb.Close($false)
}
finally {
    $excel.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
