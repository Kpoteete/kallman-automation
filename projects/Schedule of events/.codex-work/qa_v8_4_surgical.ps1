$ErrorActionPreference = "Stop"
$baseDir = "C:\kwi-automations\projects\Schedule of events"
$source = Join-Path $baseDir "Schedule of Events V8.4 - Codex Tested Template.xlsx"
$qa = Join-Path $baseDir "Schedule of Events V8.4 - Codex QA Working Copy.xlsx"
$jsonPath = Join-Path $baseDir ".codex-work\qa_v8_3_results.json"
$logPath = Join-Path $baseDir ".codex-work\qa_v8_3_progress.log"
Remove-Item -LiteralPath $jsonPath,$logPath -Force -ErrorAction SilentlyContinue
if (Test-Path $qa) { Remove-Item -LiteralPath $qa -Force }
Copy-Item -LiteralPath $source -Destination $qa -Force

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
    for ($i=1; $i -le 20; $i++) {
        try { return & $action }
        catch [System.Runtime.InteropServices.COMException] { $last = $_; Start-Sleep -Milliseconds 250 }
    }
    throw $last
}

function Sheet($wb, $name) { Retry { $wb.Worksheets.Item($name) } }
function Rng($ws, $addr) { Retry { $ws.Range($addr) } }
function Txt($ws, $addr) { $r = Rng $ws $addr; $t = Retry { [string]$r.Text }; Release-ComObject $r; $t }
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
    $ok
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$excel.AskToUpdateLinks = $false
$excel.EnableEvents = $false

try {
    Log "open"
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

    $testRow = 57
    $tbdRow = 58
    $allDayRow = 59
    $prevRow = 56
    Log "add category"
    Setv $ref "D18" "QA Test Category"

    Log "write rows"
    Setv $data "A57" "Farnborough 2026"; Setv $data "B57" "KWI"; Setv $data "C57" "QA Tester"; Setv $data "D57" ([datetime]"2026-07-23"); Setv $data "E57" ([datetime]"1899-12-30 11:30"); Setv $data "F57" ([datetime]"2026-07-23"); Setv $data "G57" ([datetime]"1899-12-30 11:45"); Setv $data "H57" "QA Test Timed Event"; Setv $data "I57" "The FORUM"; Setv $data "J57" "Category: QA Test Category"; Setv $data "K57" "QA description"; Setv $data "L57" "QA internal note"; Setv $data "M57" "QA Required"; Setv $data "N57" "QA Speaker"; Setv $data "O57" "QA Sponsor"; Setv $data "P57" "Public"; Setv $data "Q57" ""; Setv $data "R57" "Confirmed"
    Setv $data "A58" "Farnborough 2026"; Setv $data "B58" "KWI"; Setv $data "C58" "QA Tester"; Setv $data "D58" ([datetime]"2026-07-23"); Setv $data "E58" "TBD"; Setv $data "F58" ([datetime]"2026-07-23"); Setv $data "G58" "TBD"; Setv $data "H58" "QA Test TBD Event"; Setv $data "I58" "The FORUM"; Setv $data "J58" "Category: QA Test Category"; Setv $data "M58" "QA Required"; Setv $data "N58" "QA Speaker"; Setv $data "O58" "QA Sponsor"; Setv $data "P58" "Private"; Setv $data "R58" "Confirmed"
    Setv $data "A59" "Farnborough 2026"; Setv $data "B59" "KWI"; Setv $data "C59" "QA Tester"; Setv $data "D59" ([datetime]"2026-07-23"); Setv $data "E59" "All Day"; Setv $data "F59" ([datetime]"2026-07-23"); Setv $data "G59" "All Day"; Setv $data "H59" "QA Test All Day Event"; Setv $data "I59" "The FORUM"; Setv $data "J59" "Category: QA Test Category"; Setv $data "M59" "QA Required"; Setv $data "N59" "QA Speaker"; Setv $data "O59" "QA Sponsor"; Setv $data "P59" "Public"; Setv $data "R59" "Confirmed"

    Log "set filters"
    Setv $daily "B2" ([datetime]"2026-07-23")
    Setv $weekly "B3" ([datetime]"2026-07-19")
    Setv $weekly "C3" "Farnborough 2026"
    Retry { (Rng $weekly "D3:G3").ClearContents() } | Out-Null
    Setv $ros "B3" "The FORUM"
    Setv $ros "B4" "QA Test Timed Event"

    Log "calculate target sheets"
    foreach ($ws in @($ref,$data,$daily,$weekly,$weeklyNew,$loc,$sponsors,$speakers,$required,$ros,$instructions)) {
        Log ("calc " + $ws.Name)
        Retry { $ws.Calculate() } | Out-Null
    }

    Log "collect"
    Log "collect schedule slots"
    $scheduleSlots = [ordered]@{ timed = Txt $data "S57"; tbd = Txt $data "S58"; allDay = Txt $data "S59" }
    Log "collect daily"
    $dailyResults = [ordered]@{
        timedAppears = Contains $daily "B1:K140" "QA Test Timed Event"
        tbdAppears = Contains $daily "B1:K140" "QA Test TBD Event"
        allDayAppearsInScheduleArea = Contains $daily "B1:K140" "QA Test All Day Event"
    }
    Log "collect live views"
    $liveViews = [ordered]@{
        weeklyCalendarNew = Contains $weeklyNew "A1:H300" "QA Test Timed Event"
        locationSchedule = Contains $loc "A1:H300" "QA Test Timed Event"
        sponsors = Contains $sponsors "A1:H300" "QA Sponsor"
        speakers = Contains $speakers "A1:H300" "QA Speaker"
        requiredAttendees = Contains $required "A1:H300" "QA Required"
    }
    Log "collect run of show"
    $runOfShow = [ordered]@{ selectedEvent = Txt $ros "B4"; selectedRow = Txt $ros "B6"; start = Txt $ros "B9"; end = Txt $ros "D9"; firstMinute = Txt $ros "A20"; secondMinute = Txt $ros "A21" }
    Log "collect instructions"
    $instructionResults = [ordered]@{ title = Txt $instructions "A1"; hasRunOfShow = Contains $instructions "A1:B40" "Minute-by-minute" }
    Log "collect spot checks"
    $errorSpotChecks = [ordered]@{
        weeklyA4 = Txt $weeklyNew "A4"; sponsorsA4 = Txt $sponsors "A4"; speakersA4 = Txt $speakers "A4"; requiredA4 = Txt $required "A4"; locationA4 = Txt $loc "A4"
    }
    $results = [ordered]@{
        qaCopy = $qa
        rows = [ordered]@{ category = 18; timed = 57; tbd = 58; allDay = 59 }
        scheduleSlots = $scheduleSlots
        daily = $dailyResults
        liveViews = $liveViews
        runOfShow = $runOfShow
        instructions = $instructionResults
        errorSpotChecks = $errorSpotChecks
    }
    $results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

    Log "save close"
    Retry { $wb.Save() } | Out-Null
    Retry { $wb.Close($true) } | Out-Null
    Release-ComObject $wb

    Log "reopen"
    $wb2 = Retry { $excel.Workbooks.Open($qa) }
    Retry { $excel.Calculation = -4135 } | Out-Null
    $wn2 = Sheet $wb2 "Weekly Calendar New"
    $still = Contains $wn2 "A1:H300" "QA Test Timed Event"
    Retry { $wb2.Close($false) } | Out-Null
    Release-ComObject $wb2
    $saved = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    $saved | Add-Member -NotePropertyName reopenWeeklyStillContainsTimedEvent -NotePropertyValue $still
    $saved | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    Log "done"
    Write-Host "QA complete: $jsonPath"
}
finally {
    $excel.Quit()
    Release-ComObject $excel
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
