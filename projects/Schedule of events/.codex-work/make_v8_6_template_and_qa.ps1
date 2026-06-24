$ErrorActionPreference = "Stop"

$baseDir = "C:\kwi-automations\projects\Schedule of events"
$source = Join-Path $baseDir "Schedule of Events V8.5 - Codex Tested Template.xlsx"
$template = Join-Path $baseDir "Schedule of Events V8.6 - Codex Clean Tested Template.xlsx"
$qa = Join-Path $baseDir "Schedule of Events V8.6 - Codex QA Working Copy.xlsx"
$jsonPath = Join-Path $baseDir ".codex-work\qa_v8_6_results.json"
$logPath = Join-Path $baseDir ".codex-work\qa_v8_6_progress.log"

Remove-Item -LiteralPath $jsonPath,$logPath -Force -ErrorAction SilentlyContinue
if (Test-Path $template) { Remove-Item -LiteralPath $template -Force }
if (Test-Path $qa) { Remove-Item -LiteralPath $qa -Force }
Copy-Item -LiteralPath $source -Destination $template -Force
Copy-Item -LiteralPath $template -Destination $qa -Force

function Log($msg) {
    Add-Content -LiteralPath $logPath -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), $msg)
}

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

function Sheet($wb, $name) { Retry { $wb.Worksheets.Item($name) } }
function Rng($ws, $addr) { Retry { $ws.Range($addr) } }
function Txt($ws, $addr) { $r = Rng $ws $addr; $t = Retry { [string]$r.Text }; Release-ComObject $r; $t.Trim() }
function Setv($ws, $addr, $v) {
    $r = Rng $ws $addr
    if ($v -is [datetime]) { Retry { $r.Value = $v } | Out-Null } else { Retry { $r.Value2 = $v } | Out-Null }
    Release-ComObject $r
}
function Contains($ws, $addr, $needle) {
    $r = Rng $ws $addr
    $vals = Retry { $r.Value2 }
    $ok = $false
    if ($vals -is [System.Array]) {
        foreach ($v in $vals) {
            if ($null -ne $v -and ([string]$v).Contains($needle)) { $ok = $true; break }
        }
    } else {
        if ($null -ne $vals -and ([string]$vals).Contains($needle)) { $ok = $true }
    }
    Release-ComObject $r
    return $ok
}
function CountContains($ws, $addr, $needle) {
    $r = Rng $ws $addr
    $vals = Retry { $r.Value2 }
    $count = 0
    if ($vals -is [System.Array]) {
        foreach ($v in $vals) {
            if ($null -ne $v -and ([string]$v).Contains($needle)) { $count++ }
        }
    } else {
        if ($null -ne $vals -and ([string]$vals).Contains($needle)) { $count++ }
    }
    Release-ComObject $r
    return $count
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$excel.AskToUpdateLinks = $false
$excel.EnableEvents = $false

try {
    Log "open QA"
    $wb = Retry { $excel.Workbooks.Open($qa) }
    Retry { $excel.Calculation = -4135 } | Out-Null

    $data = Sheet $wb "Data Input"
    $ref = Sheet $wb "Reference"
    $daily = Sheet $wb "Daily Schedule"
    $weekly = Sheet $wb "Weekly Calendar"
    $weeklyNew = Sheet $wb "Weekly Calendar New"
    $loc = Sheet $wb "Location Schedule"
    $sponsors = Sheet $wb "Sponsors"
    $speakers = Sheet $wb "Speakers"
    $required = Sheet $wb "Required Attendees"
    $ros = Sheet $wb "Run of Show"
    $instructions = Sheet $wb "Instructions"

    Log "add reference category"
    Setv $ref "D18" "QA Template Category"

    Log "add timed overlap, tbd, all-day rows"
    $rows = @(
        @{ Row=57; Event="QA V8.6 Timed One"; Start="1899-12-30 11:30"; End="1899-12-30 12:00"; Required="QA Required A"; Speaker="QA Speaker A"; Sponsor="QA Sponsor A"; Class="Public"; Status="Confirmed" },
        @{ Row=58; Event="QA V8.6 Timed Two"; Start="1899-12-30 11:30"; End="1899-12-30 11:45"; Required="QA Required B"; Speaker="QA Speaker B"; Sponsor="QA Sponsor B"; Class="Private"; Status="Tentative" },
        @{ Row=59; Event="QA V8.6 Timed Three"; Start="1899-12-30 11:45"; End="1899-12-30 12:15"; Required="QA Required C"; Speaker="QA Speaker C"; Sponsor="QA Sponsor C"; Class="Public"; Status="Confirmed" }
    )
    foreach ($item in $rows) {
        $r = $item.Row
        Setv $data "A$r" "Farnborough 2026"
        Setv $data "B$r" "KWI"
        Setv $data "C$r" "QA Tester"
        Setv $data "D$r" ([datetime]"2026-07-23")
        Setv $data "E$r" ([datetime]$item.Start)
        Setv $data "F$r" ([datetime]"2026-07-23")
        Setv $data "G$r" ([datetime]$item.End)
        Setv $data "H$r" $item.Event
        Setv $data "I$r" "The FORUM"
        Setv $data "J$r" "Category: QA Template Category"
        Setv $data "K$r" "QA description"
        Setv $data "L$r" "QA internal note"
        Setv $data "M$r" $item.Required
        Setv $data "N$r" $item.Speaker
        Setv $data "O$r" $item.Sponsor
        Setv $data "P$r" $item.Class
        Setv $data "Q$r" ""
        Setv $data "R$r" $item.Status
    }
    Setv $data "A60" "Farnborough 2026"; Setv $data "B60" "KWI"; Setv $data "C60" "QA Tester"; Setv $data "D60" ([datetime]"2026-07-23"); Setv $data "E60" "TBD"; Setv $data "F60" ([datetime]"2026-07-23"); Setv $data "G60" "TBD"; Setv $data "H60" "QA V8.6 TBD Event"; Setv $data "I60" "The FORUM"; Setv $data "J60" "Category: QA Template Category"; Setv $data "M60" "QA Required TBD"; Setv $data "N60" "QA Speaker TBD"; Setv $data "O60" "QA Sponsor TBD"; Setv $data "P60" "Private"; Setv $data "R60" "Confirmed"
    Setv $data "A61" "Farnborough 2026"; Setv $data "B61" "KWI"; Setv $data "C61" "QA Tester"; Setv $data "D61" ([datetime]"2026-07-23"); Setv $data "E61" "All Day"; Setv $data "F61" ([datetime]"2026-07-23"); Setv $data "G61" "All Day"; Setv $data "H61" "QA V8.6 All Day Event"; Setv $data "I61" "The FORUM"; Setv $data "J61" "Category: QA Template Category"; Setv $data "M61" "QA Required All Day"; Setv $data "N61" "QA Speaker All Day"; Setv $data "O61" "QA Sponsor All Day"; Setv $data "P61" "Public"; Setv $data "R61" "Confirmed"

    Log "set filters and run-of-show"
    Setv $daily "B2" ([datetime]"2026-07-23")
    Setv $weekly "B3" ([datetime]"2026-07-19")
    Setv $weekly "C3" "Farnborough 2026"
    Retry { (Rng $weekly "D3:G3").ClearContents() } | Out-Null
    Setv $ros "B3" "The FORUM"
    Setv $ros "B4" "QA V8.6 Timed One"

    Log "calculate target sheets"
    foreach ($ws in @($ref,$data,$daily,$weekly,$weeklyNew,$loc,$sponsors,$speakers,$required,$ros,$instructions)) {
        Log ("calc " + $ws.Name)
        Retry { $ws.Calculate() } | Out-Null
    }

    Log "collect results"
    $results = [ordered]@{
        template = $template
        qaCopy = $qa
        validation = [ordered]@{
            categorySourceJ57 = Txt $data "J57"
            newReferenceCategory = Txt $ref "D18"
        }
        scheduleSlots = [ordered]@{
            timedOne = Txt $data "S57"
            timedTwo = Txt $data "S58"
            timedThree = Txt $data "S59"
            tbd = Txt $data "S60"
            allDay = Txt $data "S61"
        }
        daily = [ordered]@{
            timedOne = Contains $daily "B1:K140" "QA V8.6 Timed One"
            timedTwo = Contains $daily "B1:K140" "QA V8.6 Timed Two"
            timedThree = Contains $daily "B1:K140" "QA V8.6 Timed Three"
            tbd = Contains $daily "B1:K140" "QA V8.6 TBD Event"
            allDay = Contains $daily "B1:K140" "QA V8.6 All Day Event"
            tbdDuplicateCount = CountContains $daily "B1:K140" "QA V8.6 TBD Event"
            allDayCount = CountContains $daily "B1:K140" "QA V8.6 All Day Event"
        }
        liveViews = [ordered]@{
            weeklyCalendarNew = Contains $weeklyNew "A1:H320" "QA V8.6 Timed One"
            locationSchedule = Contains $loc "A1:H320" "QA V8.6 Timed One"
            sponsors = Contains $sponsors "A1:H320" "QA Sponsor A"
            speakers = Contains $speakers "A1:H320" "QA Speaker A"
            requiredAttendees = Contains $required "A1:H320" "QA Required A"
        }
        runOfShow = [ordered]@{
            selectedEvent = Txt $ros "B4"
            selectedRow = Txt $ros "B6"
            start = Txt $ros "B9"
            end = Txt $ros "D9"
            firstMinute = Txt $ros "A20"
            secondMinute = Txt $ros "A21"
        }
        instructions = [ordered]@{
            title = Txt $instructions "A1"
            hasDataInput = Contains $instructions "A1:B40" "Data Input"
            hasRunOfShow = Contains $instructions "A1:B40" "Minute-by-minute"
        }
        spotChecks = [ordered]@{
            weeklyA4 = Txt $weeklyNew "A4"
            locationA4 = Txt $loc "A4"
            sponsorsA4 = Txt $sponsors "A4"
            speakersA4 = Txt $speakers "A4"
            requiredA4 = Txt $required "A4"
            dailyAllDayTitle = Txt $daily "A110"
            dailyAllDayFirstType = Txt $daily "A112"
        }
    }
    $results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

    Log "save and close"
    Retry { $wb.Save() } | Out-Null
    Retry { $wb.Close($true) } | Out-Null
    Release-ComObject $wb

    Log "reopen QA copy"
    $wb2 = Retry { $excel.Workbooks.Open($qa) }
    Retry { $excel.Calculation = -4135 } | Out-Null
    $weeklyNew2 = Sheet $wb2 "Weekly Calendar New"
    $daily2 = Sheet $wb2 "Daily Schedule"
    $stillWeekly = Contains $weeklyNew2 "A1:H320" "QA V8.6 Timed One"
    $stillDailyTbdCount = CountContains $daily2 "B1:K140" "QA V8.6 TBD Event"
    Retry { $wb2.Close($false) } | Out-Null
    Release-ComObject $wb2

    $saved = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    $saved | Add-Member -NotePropertyName reopenWeeklyStillContainsTimedEvent -NotePropertyValue $stillWeekly
    $saved | Add-Member -NotePropertyName reopenDailyTbdCount -NotePropertyValue $stillDailyTbdCount
    $saved | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

    Log "open final clean template"
    $wb3 = Retry { $excel.Workbooks.Open($template) }
    $inst = Sheet $wb3 "Instructions"
    $cleanTemplateOpened = ((Txt $inst "A1") -eq "Schedule Workbook Instructions")
    Retry { $wb3.Close($false) } | Out-Null
    Release-ComObject $wb3

    $saved2 = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    $saved2 | Add-Member -NotePropertyName cleanTemplateOpened -NotePropertyValue $cleanTemplateOpened
    $saved2 | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    Log "done"
    Write-Host "Created template: $template"
    Write-Host "QA complete: $jsonPath"
}
finally {
    $excel.Quit()
    Release-ComObject $excel
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
