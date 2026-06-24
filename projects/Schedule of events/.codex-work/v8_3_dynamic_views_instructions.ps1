$ErrorActionPreference = "Stop"

$baseDir = "C:\kwi-automations\projects\Schedule of events"
$source = Join-Path $baseDir "Schedule of Events V8.2 - Codex Run of Show.xlsx"
$output = Join-Path $baseDir "Schedule of Events V8.3 - Codex Tested Template.xlsx"

if (Test-Path $output) { Remove-Item -LiteralPath $output -Force }
Copy-Item -LiteralPath $source -Destination $output -Force

$xlCalculationAutomatic = -4105
$xlCalculationManual = -4135
$xlLandscape = 2
$xlPortrait = 1
$xlToRight = -4161
$xlDown = -4121
$xlCenter = -4108
$xlLeft = -4131
$xlTop = -4160

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

function Invoke-ComRetry([scriptblock]$action) {
    $lastError = $null
    for ($i = 1; $i -le 30; $i++) {
        try { return & $action }
        catch [System.Runtime.InteropServices.COMException] {
            $lastError = $_
            Start-Sleep -Milliseconds 250
        }
    }
    throw $lastError
}

function GetSheet($wb, [string]$name) {
    return Invoke-ComRetry { $wb.Worksheets.Item($name) }
}

function EnsureSheet($wb, [string]$name, [int]$afterIndex) {
    foreach ($ws in $wb.Worksheets) {
        if ($ws.Name -eq $name) { return $ws }
    }
    $after = $wb.Worksheets.Item($afterIndex)
    $wsNew = $wb.Worksheets.Add([System.Reflection.Missing]::Value, $after)
    $wsNew.Name = $name
    Release-ComObject $after
    return $wsNew
}

function ClearSheetBody($ws) {
    $used = Invoke-ComRetry { $ws.UsedRange }
    Invoke-ComRetry { $used.Clear() } | Out-Null
    Release-ComObject $used
    Invoke-ComRetry { $ws.Range("A1:Z600").Clear() } | Out-Null
}

function FormatHeader($range) {
    Invoke-ComRetry { $range.Font.Bold = $true } | Out-Null
    Invoke-ComRetry { $range.Font.Color = 16777215 } | Out-Null
    Invoke-ComRetry { $range.Interior.Color = 49407 } | Out-Null
    Invoke-ComRetry { $range.HorizontalAlignment = $xlCenter } | Out-Null
    Invoke-ComRetry { $range.VerticalAlignment = $xlCenter } | Out-Null
    Invoke-ComRetry { $range.WrapText = $true } | Out-Null
}

function BasicSheetFormat($ws, [string]$printArea, [int]$orientation) {
    Invoke-ComRetry { $ws.Cells.Font.Name = "Aptos" } | Out-Null
    Invoke-ComRetry { $ws.Cells.Font.Size = 10 } | Out-Null
    Invoke-ComRetry { $ws.Cells.VerticalAlignment = $xlTop } | Out-Null
    Invoke-ComRetry { $ws.Rows.Item(1).RowHeight = 28 } | Out-Null
    Invoke-ComRetry { $ws.PageSetup.Orientation = $orientation } | Out-Null
    Invoke-ComRetry { $ws.PageSetup.Zoom = $false } | Out-Null
    Invoke-ComRetry { $ws.PageSetup.FitToPagesWide = 1 } | Out-Null
    Invoke-ComRetry { $ws.PageSetup.FitToPagesTall = $false } | Out-Null
    Invoke-ComRetry { $ws.PageSetup.PrintArea = $printArea } | Out-Null
    Invoke-ComRetry { $ws.PageSetup.LeftMargin = $excel.InchesToPoints(0.25) } | Out-Null
    Invoke-ComRetry { $ws.PageSetup.RightMargin = $excel.InchesToPoints(0.25) } | Out-Null
    Invoke-ComRetry { $ws.PageSetup.TopMargin = $excel.InchesToPoints(0.45) } | Out-Null
    Invoke-ComRetry { $ws.PageSetup.BottomMargin = $excel.InchesToPoints(0.45) } | Out-Null
}

function SetDynamicFormula($ws, [string]$addr, [string]$formula) {
    $cell = $ws.Range($addr)
    Invoke-ComRetry { $cell.Formula2 = $formula } | Out-Null
    Release-ComObject $cell
}

function BuildDynamicListSheet($ws, [string]$title, [string[]]$headers, [string]$formula, [string]$printArea) {
    ClearSheetBody $ws
    Invoke-ComRetry { $ws.Range("A1").Value2 = $title } | Out-Null
    Invoke-ComRetry { $ws.Range("A1:H1").Merge() } | Out-Null
    Invoke-ComRetry { $ws.Range("A1").Font.Bold = $true } | Out-Null
    Invoke-ComRetry { $ws.Range("A1").Font.Size = 16 } | Out-Null
    Invoke-ComRetry { $ws.Range("A1").Interior.Color = 13434879 } | Out-Null
    Invoke-ComRetry { $ws.Range("A1").HorizontalAlignment = $xlCenter } | Out-Null

    for ($i = 0; $i -lt $headers.Count; $i++) {
        $col = $i + 1
        Invoke-ComRetry { $ws.Cells.Item(3, $col).Value2 = $headers[$i] } | Out-Null
    }
    FormatHeader $ws.Range("A3:H3")
    SetDynamicFormula $ws "A4" $formula
    Invoke-ComRetry { $ws.Range("A4:H300").WrapText = $true } | Out-Null
    Invoke-ComRetry { $ws.Range("B4:C300").NumberFormat = "m/d/yy" } | Out-Null
    Invoke-ComRetry { $ws.Range("C4:D300").NumberFormat = "h:mm AM/PM" } | Out-Null
    Invoke-ComRetry { $ws.Columns.Item("A:H").AutoFit() } | Out-Null
    Invoke-ComRetry { $ws.Columns.Item("A").ColumnWidth = 24 } | Out-Null
    Invoke-ComRetry { $ws.Columns.Item("B").ColumnWidth = 12 } | Out-Null
    Invoke-ComRetry { $ws.Columns.Item("C").ColumnWidth = 12 } | Out-Null
    Invoke-ComRetry { $ws.Columns.Item("D").ColumnWidth = 12 } | Out-Null
    Invoke-ComRetry { $ws.Columns.Item("E").ColumnWidth = 42 } | Out-Null
    Invoke-ComRetry { $ws.Columns.Item("F").ColumnWidth = 28 } | Out-Null
    Invoke-ComRetry { $ws.Columns.Item("G").ColumnWidth = 34 } | Out-Null
    Invoke-ComRetry { $ws.Columns.Item("H").ColumnWidth = 34 } | Out-Null
    BasicSheetFormat $ws $printArea $xlLandscape
}

try {
    $wb = Invoke-ComRetry { $excel.Workbooks.Open($output) }
    Invoke-ComRetry { $excel.Calculation = $xlCalculationManual } | Out-Null

    $weeklyNew = GetSheet $wb "Weekly Calendar New"
    $loc = GetSheet $wb "Location Schedule"
    $sponsors = GetSheet $wb "Sponsors"
    $speakers = GetSheet $wb "Speakers"
    $required = GetSheet $wb "Required Attendees"
    $instructions = EnsureSheet $wb "Instructions" 1

    $weeklyFormula = '=LET(show,''Weekly Calendar''!$C$3,owner,''Weekly Calendar''!$D$3,cat,''Weekly Calendar''!$E$3,place,''Weekly Calendar''!$F$3,class,''Weekly Calendar''!$G$3,weekStart,''Weekly Calendar''!$B$3,shows,EventInputTable[Show Name],sd,EventInputTable[Start Date],st,EventInputTable[Start Time],et,EventInputTable[End Time],ev,EventInputTable[Event Name (250 CHARACTERS)],lo,EventInputTable[Location],org,EventInputTable[Organizer],req,EventInputTable[Required Attendees],spk,EventInputTable[Speakers],cats,EventInputTable[Category],cls,EventInputTable[Class],valid,(ev<>"")*ISNUMBER(sd)*ISNUMBER(st)*ISNUMBER(et)*(sd>=weekStart)*(sd<weekStart+7)*IF(show="",1,shows=show)*IF(owner="",1,org=owner)*IF(cat="",1,cats=cat)*IF(place="",1,lo=place)*IF(class="",1,cls=class),rows,FILTER(HSTACK(TEXT(sd,"ddd m/d"),st,et,ev,lo,org,req,spk),valid),IFERROR(SORTBY(rows,FILTER(sd,valid),1,FILTER(st,valid),1),"No timed events found for the selected week/filters."))'
    BuildDynamicListSheet $weeklyNew "Weekly Event List - Timed Events" @("Day","Start","End","Event","Location","Owner","Required Attendees","Speakers") $weeklyFormula '$A$1:$H$70'
    Invoke-ComRetry { $weeklyNew.Range("J1").Value2 = "Uses the filters on the Weekly Calendar tab." } | Out-Null
    Invoke-ComRetry { $weeklyNew.Range("B4:C300").NumberFormat = "h:mm AM/PM" } | Out-Null

    $locationFormula = '=LET(sd,EventInputTable[Start Date],st,EventInputTable[Start Time],et,EventInputTable[End Time],ev,EventInputTable[Event Name (250 CHARACTERS)],lo,EventInputTable[Location],cat,EventInputTable[Category],req,EventInputTable[Required Attendees],spk,EventInputTable[Speakers],valid,(ev<>"")*(lo<>""),rows,FILTER(HSTACK(lo,sd,st,et,ev,cat,req,spk),valid),IFERROR(SORTBY(rows,FILTER(lo,valid),1,FILTER(sd,valid),1,FILTER(IF(ISNUMBER(st),st,0),valid),1),"No location events found."))'
    BuildDynamicListSheet $loc "Location Schedule" @("Location","Date","Start","End","Event","Category","Required Attendees","Speakers") $locationFormula '$A$1:$H$80'

    $sponsorFormula = '=LET(sd,EventInputTable[Start Date],st,EventInputTable[Start Time],et,EventInputTable[End Time],ev,EventInputTable[Event Name (250 CHARACTERS)],lo,EventInputTable[Location],cat,EventInputTable[Category],spon,EventInputTable[Sponsors],valid,(ev<>"")*(spon<>""),rows,FILTER(HSTACK(spon,sd,st,et,ev,lo,cat,""),valid),IFERROR(SORTBY(rows,FILTER(sd,valid),1,FILTER(IF(ISNUMBER(st),st,0),valid),1),"No sponsored events found."))'
    BuildDynamicListSheet $sponsors "Sponsored Events" @("Sponsors","Date","Start","End","Event","Location","Category","Notes") $sponsorFormula '$A$1:$H$80'

    $speakerFormula = '=LET(sd,EventInputTable[Start Date],st,EventInputTable[Start Time],et,EventInputTable[End Time],ev,EventInputTable[Event Name (250 CHARACTERS)],lo,EventInputTable[Location],cat,EventInputTable[Category],spk,EventInputTable[Speakers],req,EventInputTable[Required Attendees],valid,(ev<>"")*(spk<>""),rows,FILTER(HSTACK(spk,sd,st,et,ev,lo,cat,req),valid),IFERROR(SORTBY(rows,FILTER(sd,valid),1,FILTER(IF(ISNUMBER(st),st,0),valid),1),"No speaker events found."))'
    BuildDynamicListSheet $speakers "Speaker Schedule" @("Speakers","Date","Start","End","Event","Location","Category","Required Attendees") $speakerFormula '$A$1:$H$80'

    $requiredFormula = '=LET(sd,EventInputTable[Start Date],st,EventInputTable[Start Time],et,EventInputTable[End Time],ev,EventInputTable[Event Name (250 CHARACTERS)],lo,EventInputTable[Location],cat,EventInputTable[Category],req,EventInputTable[Required Attendees],spk,EventInputTable[Speakers],valid,(ev<>"")*(req<>""),rows,FILTER(HSTACK(req,sd,st,et,ev,lo,cat,spk),valid),IFERROR(SORTBY(rows,FILTER(sd,valid),1,FILTER(IF(ISNUMBER(st),st,0),valid),1),"No required-attendee events found."))'
    BuildDynamicListSheet $required "Required Attendees Schedule" @("Required Attendees","Date","Start","End","Event","Location","Category","Speakers") $requiredFormula '$A$1:$H$80'

    ClearSheetBody $instructions
    $instructions.Range("A1").Value2 = "Schedule Workbook Instructions"
    $instructions.Range("A1:H1").Merge() | Out-Null
    $instructions.Range("A1").Font.Bold = $true
    $instructions.Range("A1").Font.Size = 18
    $instructions.Range("A1").Interior.Color = 13434879
    $instructions.Range("A1").HorizontalAlignment = $xlCenter

    $rows = @(
        @("Quick Start","Use the Data Input tab as the source of truth. Enter one event per row, then use the daily, weekly, run-of-show, and printable summary tabs for different views."),
        @("Data Input","Main event table. Fill in show name, organizer, dates/times, title, location, category, notes, attendees, speakers, sponsors, class, status, and schedule slot. Dropdowns come from Reference. Use numeric times for timed events; use TBD when the time is unknown; use All Day for all-day items."),
        @("Daily Schedule","One-day printable schedule. Choose the date and filters at the top. Timed events appear in the hourly grid. TBD items are held in the TBD section instead of mixing into the timed grid."),
        @("Weekly Calendar","Grid-style week view. Set week start and optional filters at the top. It is best for a quick visual look at the week by day and hour."),
        @("Weekly Calendar New","Live formula list of timed events for the selected weekly filters. Use this when the hourly grid is too dense or when you need a cleaner printout."),
        @("Run of Show","Minute-by-minute planning sheet. Pick a location and event at the top, then fill in segment/action, speaker/lead, cue/transition, tech/AV/visual, and notes. The minute timeline generates from the selected event start/end time."),
        @("Location Schedule","Live printable view grouped by location, then date and time. Use this to see when a room or booth is busy and who is involved."),
        @("Sponsors","Live printable list of events that have sponsors entered. Leave Sponsors blank on Data Input if the event should not appear here."),
        @("Speakers","Live printable list of events that have speakers entered. Leave Speakers blank on Data Input if the event should not appear here."),
        @("Required Attendees","Live printable list of events that have required attendees entered. Leave Required Attendees blank on Data Input if the event should not appear here."),
        @("Event Notes and Attendees","Working notes area for event details, attendees, and follow-up notes."),
        @("Reference","Dropdown/source-list tab. Add new shows, organizers, categories, locations, classes, statuses, and filter values here. Keep new entries inside the shaded/table area so validation and formulas continue to pick them up."),
        @("Calc","Helper formulas for the schedule views. Leave this tab alone unless you are intentionally changing the template logic."),
        @("Printing","Daily Schedule, Weekly Calendar, Weekly Calendar New, Location Schedule, Sponsors, Speakers, Required Attendees, and Run of Show have print areas set. Print in landscape for wide views; Run of Show and Instructions are set to be readable one page wide."),
        @("Good Data Habits","Use consistent spelling in dropdown fields, especially Location and Category. Put multiple names in Attendees/Speakers/Sponsors separated by commas. If an event is not appearing where expected, check Start Date, Start Time, End Time, Event Name, Location, and whether the relevant people/sponsor fields are blank.")
    )
    $r = 3
    foreach ($item in $rows) {
        $instructions.Cells.Item($r,1).Value2 = $item[0]
        $instructions.Cells.Item($r,2).Value2 = $item[1]
        $r++
    }
    FormatHeader $instructions.Range("A2:B2")
    $instructions.Range("A2").Value2 = "Section"
    $instructions.Range("B2").Value2 = "How to Use It"
    $instructions.Columns.Item("A").ColumnWidth = 24
    $instructions.Columns.Item("B").ColumnWidth = 120
    $instructions.Range("A2:B40").WrapText = $true
    $instructions.Range("A3:A40").Font.Bold = $true
    $instructions.Range("A3:B40").VerticalAlignment = $xlTop
    BasicSheetFormat $instructions '$A$1:$B$25' $xlPortrait

    Invoke-ComRetry { $excel.Calculation = $xlCalculationAutomatic } | Out-Null
    Invoke-ComRetry { $excel.CalculateFullRebuild() } | Out-Null
    Invoke-ComRetry { $wb.Save() } | Out-Null
    Invoke-ComRetry { $wb.Close($true) } | Out-Null
    Release-ComObject $wb
    Write-Host "Created $output"
}
finally {
    $excel.Quit()
    Release-ComObject $excel
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
