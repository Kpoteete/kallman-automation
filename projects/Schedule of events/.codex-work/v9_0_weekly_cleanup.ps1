$ErrorActionPreference = "Stop"

$baseDir = "C:\kwi-automations\projects\Schedule of events"
$source = Join-Path $baseDir "Schedule of Events V8.9 - Codex No Stale Formula Template.xlsx"
$output = Join-Path $baseDir "Schedule of Events V9.0 - Codex Weekly Cleanup.xlsx"
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
        try {
            $result = & $action
            Write-Output -NoEnumerate $result
            return
        }
        catch [System.Runtime.InteropServices.COMException] {
            $last = $_
            Start-Sleep -Milliseconds 250
        }
    }
    throw $last
}

function Sheet($wb, [string]$name) {
    $result = Retry { $wb.Worksheets.Item($name) }
    Write-Output -NoEnumerate $result
}
function Rng($ws, [string]$addr) {
    $result = Retry { $ws.Range($addr) }
    Write-Output -NoEnumerate $result
}
function SetText($ws, [string]$addr, [string]$text) {
    $r = Rng $ws $addr
    Retry { $r.Value2 = $text } | Out-Null
    Release-ComObject $r
}

function SetHeaders($ws, [int]$row, [string[]]$headers) {
    for ($i = 0; $i -lt $headers.Count; $i++) {
        $col = GetColumnLetter ($i + 1)
        $addr = "{0}{1}" -f $col,$row
        $value = [string]$headers[$i]
        Retry { $ws.Range($addr).Value2 = $value } | Out-Null
    }
}

function GetColumnLetter([int]$col) {
    $letters = ""
    while ($col -gt 0) {
        $rem = ($col - 1) % 26
        $letters = [char](65 + $rem) + $letters
        $col = [math]::Floor(($col - 1) / 26)
    }
    return $letters
}

function HexColor([string]$hex) {
    $hex = $hex.TrimStart("#")
    $r = [Convert]::ToInt32($hex.Substring(0,2),16)
    $g = [Convert]::ToInt32($hex.Substring(2,2),16)
    $b = [Convert]::ToInt32($hex.Substring(4,2),16)
    return $r + ($g * 256) + ($b * 65536)
}

$navy = HexColor "#18324A"
$blue = HexColor "#2E74B5"
$lightBlue = HexColor "#E8F1FA"
$lighterBlue = HexColor "#F3F8FD"
$gray = HexColor "#F2F4F7"
$border = HexColor "#C9D2DC"
$white = HexColor "#FFFFFF"
$softRed = HexColor "#FCE7E7"

function ApplyListStyle($ws, [string]$title, [int]$headerRow, [string]$printArea) {
    Retry { $ws.Range("A1:H1").UnMerge() } | Out-Null
    Retry { $ws.Range("A1:H1").Merge() } | Out-Null
    Retry { $ws.Range("A1").Value2 = $title } | Out-Null
    Retry { $ws.Range("A1:H1").Interior.Color = $lightBlue } | Out-Null
    Retry { $ws.Range("A1:H1").Font.Color = $navy } | Out-Null
    Retry { $ws.Range("A1:H1").Font.Bold = $true } | Out-Null
    Retry { $ws.Range("A1:H1").Font.Size = 16 } | Out-Null
    Retry { $ws.Range("A1:H1").HorizontalAlignment = -4108 } | Out-Null

    $headerAddr = "A{0}:H{0}" -f $headerRow
    Retry { $ws.Range($headerAddr).Interior.Color = $blue } | Out-Null
    Retry { $ws.Range($headerAddr).Font.Color = $white } | Out-Null
    Retry { $ws.Range($headerAddr).Font.Bold = $true } | Out-Null
    Retry { $ws.Range($headerAddr).WrapText = $true } | Out-Null
    Retry { $ws.Range($headerAddr).HorizontalAlignment = -4108 } | Out-Null
    Retry { $ws.Range($headerAddr).VerticalAlignment = -4108 } | Out-Null

    $bodyAddr = "A{0}:H300" -f ($headerRow + 1)
    Retry { $ws.Range($bodyAddr).Interior.Color = $white } | Out-Null
    Retry { $ws.Range($bodyAddr).WrapText = $true } | Out-Null
    Retry { $ws.Range($bodyAddr).VerticalAlignment = -4160 } | Out-Null
    Retry { $ws.Range($bodyAddr).Borders.Color = $border } | Out-Null
    Retry { $ws.Range($bodyAddr).Borders.Weight = 2 } | Out-Null

    foreach ($col in @("A:A","E:E","G:H")) {
        Retry { $ws.Range($col).Interior.Color = $lighterBlue } | Out-Null
    }
    foreach ($col in @("B:C","F:F")) {
        Retry { $ws.Range($col).Interior.Color = $gray } | Out-Null
    }

    # Reapply header styling after column fills so the full header row stays readable.
    Retry { $ws.Range($headerAddr).Interior.Color = $blue } | Out-Null
    Retry { $ws.Range($headerAddr).Font.Color = $white } | Out-Null
    Retry { $ws.Range($headerAddr).Font.Bold = $true } | Out-Null

    Retry { $ws.Columns.Item("A").ColumnWidth = 16 } | Out-Null
    Retry { $ws.Columns.Item("B").ColumnWidth = 12 } | Out-Null
    Retry { $ws.Columns.Item("C").ColumnWidth = 12 } | Out-Null
    Retry { $ws.Columns.Item("D").ColumnWidth = 42 } | Out-Null
    Retry { $ws.Columns.Item("E").ColumnWidth = 34 } | Out-Null
    Retry { $ws.Columns.Item("F").ColumnWidth = 18 } | Out-Null
    Retry { $ws.Columns.Item("G").ColumnWidth = 28 } | Out-Null
    Retry { $ws.Columns.Item("H").ColumnWidth = 28 } | Out-Null
    Retry { $ws.Rows.Item($headerRow).RowHeight = 28 } | Out-Null
    Retry { $ws.Rows.Item("1:300").Font.Name = "Aptos" } | Out-Null
    Retry { $ws.Rows.Item("1:300").Font.Size = 10 } | Out-Null
    Retry { $ws.PageSetup.PrintArea = $printArea } | Out-Null
    Retry { $ws.PageSetup.Orientation = 2 } | Out-Null
    Retry { $ws.PageSetup.Zoom = $false } | Out-Null
    Retry { $ws.PageSetup.FitToPagesWide = 1 } | Out-Null
    Retry { $ws.PageSetup.FitToPagesTall = $false } | Out-Null
    Retry { $ws.PageSetup.LeftMargin = $excel.InchesToPoints(0.25) } | Out-Null
    Retry { $ws.PageSetup.RightMargin = $excel.InchesToPoints(0.25) } | Out-Null
    Retry { $ws.PageSetup.LeftHeader = "" } | Out-Null
    Retry { $ws.PageSetup.CenterHeader = "" } | Out-Null
    Retry { $ws.PageSetup.RightHeader = "" } | Out-Null
    Retry { $ws.PageSetup.LeftFooter = "" } | Out-Null
    Retry { $ws.PageSetup.CenterFooter = "" } | Out-Null
    Retry { $ws.PageSetup.RightFooter = "" } | Out-Null
}

function ClearSheetArea($ws) {
    Retry { $ws.Range("A1:K320").Clear() } | Out-Null
}

try {
    $wb = Retry { $excel.Workbooks.Open($output) }
    Retry { $excel.Calculation = -4135 } | Out-Null # manual while editing

    $weekly = Sheet $wb "Weekly Calendar"
    $weeklyNew = Sheet $wb "Weekly Calendar New"
    $weeklyFormula = '=LET(show,''Weekly Calendar''!$C$3,owner,''Weekly Calendar''!$D$3,cat,''Weekly Calendar''!$E$3,place,''Weekly Calendar''!$F$3,class,''Weekly Calendar''!$G$3,weekStart,''Weekly Calendar''!$B$3,shows,EventInputTable[Show Name],sd,EventInputTable[Start Date],st,EventInputTable[Start Time],et,EventInputTable[End Time],ev,EventInputTable[Event Name (250 CHARACTERS)],lo,EventInputTable[Location],org,EventInputTable[Organizer],req,EventInputTable[Required Attendees],spk,EventInputTable[Speakers],cats,EventInputTable[Category],cls,EventInputTable[Class],valid,(ev<>"")*ISNUMBER(sd)*ISNUMBER(st)*ISNUMBER(et)*(sd>=weekStart)*(sd<weekStart+7)*IF(show="",1,shows=show)*IF(owner="",1,org=owner)*IF(cat="",1,cats=cat)*IF(place="",1,lo=place)*IF(class="",1,cls=class),rows,FILTER(HSTACK(TEXT(sd,"ddd m/d"),st,et,ev,lo,org,req,spk),valid),IFERROR(SORTBY(rows,FILTER(sd,valid),1,FILTER(st,valid),1),"No timed events found for the selected week/filters."))'

    $weekStart = Retry { $weekly.Range("B3").Value2 }
    $show = [string](Retry { $weekly.Range("C3").Value2 })
    $owner = [string](Retry { $weekly.Range("D3").Value2 })
    $cat = [string](Retry { $weekly.Range("E3").Value2 })
    $place = [string](Retry { $weekly.Range("F3").Value2 })
    $class = [string](Retry { $weekly.Range("G3").Value2 })

    ClearSheetArea $weekly
    ApplyListStyle $weekly "Weekly Calendar - Timed Event List" 5 '$A$1:$H$75'
    SetHeaders $weekly 2 @("Filters","Week Start","Show","Owner","Category","Location","Class","")
    Retry { $weekly.Range("A2:H2").Interior.Color = $gray } | Out-Null
    Retry { $weekly.Range("A2:H2").Font.Bold = $true } | Out-Null
    Retry { $weekly.Range("B3").Value = $weekStart } | Out-Null
    Retry { $weekly.Range("B3").NumberFormat = "m/d/yyyy" } | Out-Null
    Retry { $weekly.Range("C3").Value2 = $show } | Out-Null
    Retry { $weekly.Range("D3").Value2 = $owner } | Out-Null
    Retry { $weekly.Range("E3").Value2 = $cat } | Out-Null
    Retry { $weekly.Range("F3").Value2 = $place } | Out-Null
    Retry { $weekly.Range("G3").Value2 = $class } | Out-Null
    SetHeaders $weekly 5 @("Date","Start","End","Event Name","Location","Owner","Required Attendees","Speakers")
    Retry { $weekly.Range("A6:H300").ClearContents() } | Out-Null
    Retry { $weekly.Range("A6").Formula2 = $weeklyFormula } | Out-Null
    Retry { $weekly.Range("B6:C300").NumberFormat = "h:mm AM/PM" } | Out-Null
    Retry { $weekly.Range("A5:H300").AutoFilter() } | Out-Null
    $weekly.Activate() | Out-Null
    $weekly.Range("A1").Select() | Out-Null

    # Delete the duplicate/older list tab after moving its formula into Weekly Calendar.
    Retry { $weeklyNew.Delete() } | Out-Null

    $tabs = @(
        @{Name="Location Schedule"; Title="Location Schedule"; Headers=@("Location","Date","Start","End","Event Name","Category","Required Attendees","Speakers")},
        @{Name="Sponsors"; Title="Sponsored Events"; Headers=@("Sponsors","Date","Start","End","Event Name","Location","Category","Notes")},
        @{Name="Speakers"; Title="Speaker Schedule"; Headers=@("Speakers","Date","Start","End","Event Name","Location","Category","Required Attendees")},
        @{Name="Required Attendees"; Title="Required Attendees Schedule"; Headers=@("Required Attendees","Date","Start","End","Event Name","Location","Category","Speakers")}
    )
    foreach ($tab in $tabs) {
        $ws = Sheet $wb $tab.Name
        SetHeaders $ws 3 $tab.Headers
        ApplyListStyle $ws $tab.Title 3 '$A$1:$H$80'
        Retry { $ws.Range("B4:B300").NumberFormat = "m/d/yy" } | Out-Null
        Retry { $ws.Range("C4:D300").NumberFormat = "h:mm AM/PM" } | Out-Null
        Retry { $ws.Range("A3:H300").AutoFilter() } | Out-Null
        Release-ComObject $ws
    }

    # Update Instructions to remove the now-deleted Weekly Calendar New reference.
    $inst = Sheet $wb "Instructions"
    Retry { $inst.Range("B6").Value2 = "Formula-driven weekly timed event list with filters at the top. Use this for weekly planning and cleaner printouts when the daily grid is too detailed." } | Out-Null
    Retry { $inst.Rows.Item(7).Delete() } | Out-Null
    Retry { $inst.Range("B15").Value2 = "Daily Schedule, Weekly Calendar, Location Schedule, Sponsors, Speakers, Required Attendees, and Run of Show have print areas set. Print in landscape for wide views; Run of Show and Instructions are set to be readable one page wide." } | Out-Null
    Release-ComObject $inst

    Retry { $excel.Calculation = -4105 } | Out-Null
    Retry { $weekly.Calculate() } | Out-Null
    foreach ($tabName in @("Location Schedule","Sponsors","Speakers","Required Attendees","Run of Show","Instructions")) {
        $ws = Sheet $wb $tabName
        Retry { $ws.Calculate() } | Out-Null
        Release-ComObject $ws
    }
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
