$ErrorActionPreference = "Stop"

$baseDir = "C:\kwi-automations\projects\Schedule of events"
$source = Join-Path $baseDir "Schedule of Events V8.3 - Codex Tested Template.xlsx"
$qa = Join-Path $baseDir "Schedule of Events V8.3 - Codex QA Working Copy.xlsx"
$jsonPath = Join-Path $baseDir ".codex-work\qa_v8_3_results.json"

if (Test-Path $qa) { Remove-Item -LiteralPath $qa -Force }
Copy-Item -LiteralPath $source -Destination $qa -Force

$xlCalculationAutomatic = -4105
$xlCalculationManual = -4135

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

function CellText($ws, [int]$row, [int]$col) {
    $addr = Get-A1Address $row $col
    $cell = Invoke-ComRetry { $ws.Range($addr) }
    $text = Invoke-ComRetry { [string]$cell.Text }
    Release-ComObject $cell
    return $text
}

function Get-A1Address([int]$row, [int]$col) {
    $letters = ""
    $n = $col
    while ($n -gt 0) {
        $rem = ($n - 1) % 26
        $letters = [char](65 + $rem) + $letters
        $n = [math]::Floor(($n - 1) / 26)
    }
    return "$letters$row"
}

function SetValue($ws, [int]$row, [int]$col, $value) {
    $addr = Get-A1Address $row $col
    $cell = Invoke-ComRetry { $ws.Range($addr) }
    if ($value -is [datetime]) {
        Invoke-ComRetry { $cell.Value = $value } | Out-Null
    } else {
        Invoke-ComRetry { $cell.Value2 = $value } | Out-Null
    }
    Release-ComObject $cell
}

function FindFirstBlankEventRow($ws) {
    for ($r = 4; $r -le 482; $r++) {
        if ([string]::IsNullOrWhiteSpace((CellText $ws $r 8))) { return $r }
    }
    throw "No room for three QA rows in Data Input table."
}

function FindFirstBlankReferenceRow($ws, [int]$col) {
    for ($r = 4; $r -le 203; $r++) {
        if ([string]::IsNullOrWhiteSpace((CellText $ws $r $col))) { return $r }
    }
    throw "No blank row found in Reference column $col."
}

function SheetContains($ws, [string]$needle) {
    $used = Invoke-ComRetry { $ws.UsedRange }
    $found = Invoke-ComRetry { $used.Find($needle) }
    $ok = $null -ne $found
    Release-ComObject $found
    Release-ComObject $used
    return $ok
}

function RangeContains($ws, [string]$addr, [string]$needle) {
    $range = Invoke-ComRetry { $ws.Range($addr) }
    $found = Invoke-ComRetry { $range.Find($needle) }
    $ok = $null -ne $found
    Release-ComObject $found
    Release-ComObject $range
    return $ok
}

function FormulaErrorHits($wb) {
    $errors = New-Object System.Collections.Generic.List[string]
    $errorTokens = @("#REF!","#VALUE!","#NAME?","#SPILL!","#DIV/0!","#NULL!","#NUM!")
    foreach ($ws in $wb.Worksheets) {
        $used = Invoke-ComRetry { $ws.UsedRange }
        foreach ($token in $errorTokens) {
            $found = Invoke-ComRetry { $used.Find($token) }
            if ($null -ne $found) {
                $errors.Add("$($ws.Name): $token near $($found.Address($false,$false))")
                Release-ComObject $found
            }
        }
        Release-ComObject $used
    }
    return @($errors)
}

try {
    Write-Host "Opening QA workbook"
    $wb = Invoke-ComRetry { $excel.Workbooks.Open($qa) }
    Invoke-ComRetry { $excel.Calculation = $xlCalculationManual } | Out-Null

    Write-Host "Getting sheets"
    $data = GetSheet $wb "Data Input"
    $ref = GetSheet $wb "Reference"
    $daily = GetSheet $wb "Daily Schedule"
    $weekly = GetSheet $wb "Weekly Calendar"
    $weeklyNew = GetSheet $wb "Weekly Calendar New"
    $loc = GetSheet $wb "Location Schedule"
    $sponsors = GetSheet $wb "Sponsors"
    $speakers = GetSheet $wb "Speakers"
    $required = GetSheet $wb "Required Attendees"
    $ros = GetSheet $wb "Run of Show"
    $instructions = GetSheet $wb "Instructions"

    Write-Host "Adding reference category"
    $categoryRow = FindFirstBlankReferenceRow $ref 4
    SetValue $ref $categoryRow 4 "QA Test Category"

    Write-Host "Finding event row"
    $testRow = FindFirstBlankEventRow $data
    $prevRow = $testRow - 1
    Write-Host "Copying template rows"
    for ($i = 0; $i -lt 3; $i++) {
        $targetRow = $testRow + $i
        $src = Invoke-ComRetry { $data.Range("A$prevRow:S$prevRow") }
        $dst = Invoke-ComRetry { $data.Range("A$targetRow:S$targetRow") }
        Invoke-ComRetry { $src.Copy($dst) } | Out-Null
        Release-ComObject $src
        Release-ComObject $dst
    }

    $data = GetSheet $wb "Data Input"

    Write-Host "Writing timed row"
    SetValue $data $testRow 1 "Farnborough 2026"
    SetValue $data $testRow 2 "KWI"
    SetValue $data $testRow 3 "QA Tester"
    SetValue $data $testRow 4 ([datetime]"2026-07-23")
    SetValue $data $testRow 5 ([datetime]"1899-12-30 11:30")
    SetValue $data $testRow 6 ([datetime]"2026-07-23")
    SetValue $data $testRow 7 ([datetime]"1899-12-30 11:45")
    SetValue $data $testRow 8 "QA Test Timed Event"
    SetValue $data $testRow 9 "The FORUM"
    SetValue $data $testRow 10 "Category: QA Test Category"
    SetValue $data $testRow 11 "QA description"
    SetValue $data $testRow 12 "QA internal note"
    SetValue $data $testRow 13 "QA Required"
    SetValue $data $testRow 14 "QA Speaker"
    SetValue $data $testRow 15 "QA Sponsor"
    SetValue $data $testRow 16 "Public"
    SetValue $data $testRow 17 ""
    SetValue $data $testRow 18 "Confirmed"

    $tbdRow = $testRow + 1
    Write-Host "Writing TBD row"
    SetValue $data $tbdRow 1 "Farnborough 2026"
    SetValue $data $tbdRow 2 "KWI"
    SetValue $data $tbdRow 3 "QA Tester"
    SetValue $data $tbdRow 4 ([datetime]"2026-07-23")
    SetValue $data $tbdRow 5 "TBD"
    SetValue $data $tbdRow 6 ([datetime]"2026-07-23")
    SetValue $data $tbdRow 7 "TBD"
    SetValue $data $tbdRow 8 "QA Test TBD Event"
    SetValue $data $tbdRow 9 "The FORUM"
    SetValue $data $tbdRow 10 "Category: QA Test Category"
    SetValue $data $tbdRow 13 "QA Required"
    SetValue $data $tbdRow 14 "QA Speaker"
    SetValue $data $tbdRow 15 "QA Sponsor"
    SetValue $data $tbdRow 16 "Private"
    SetValue $data $tbdRow 18 "Confirmed"

    $allDayRow = $testRow + 2
    Write-Host "Writing all-day row"
    SetValue $data $allDayRow 1 "Farnborough 2026"
    SetValue $data $allDayRow 2 "KWI"
    SetValue $data $allDayRow 3 "QA Tester"
    SetValue $data $allDayRow 4 ([datetime]"2026-07-23")
    SetValue $data $allDayRow 5 "All Day"
    SetValue $data $allDayRow 6 ([datetime]"2026-07-23")
    SetValue $data $allDayRow 7 "All Day"
    SetValue $data $allDayRow 8 "QA Test All Day Event"
    SetValue $data $allDayRow 9 "The FORUM"
    SetValue $data $allDayRow 10 "Category: QA Test Category"
    SetValue $data $allDayRow 13 "QA Required"
    SetValue $data $allDayRow 14 "QA Speaker"
    SetValue $data $allDayRow 15 "QA Sponsor"
    SetValue $data $allDayRow 16 "Public"
    SetValue $data $allDayRow 18 "Confirmed"

    Write-Host "Setting view filters"
    Invoke-ComRetry { $daily.Range("B2").Value = [datetime]"2026-07-23" } | Out-Null
    Invoke-ComRetry { $weekly.Range("B3").Value = [datetime]"2026-07-19" } | Out-Null
    Invoke-ComRetry { $weekly.Range("C3").Value2 = "Farnborough 2026" } | Out-Null
    Invoke-ComRetry { $weekly.Range("D3:G3").ClearContents() } | Out-Null
    Invoke-ComRetry { $ros.Range("B3").Value2 = "The FORUM" } | Out-Null
    Invoke-ComRetry { $ros.Range("B4").Value2 = "QA Test Timed Event" } | Out-Null

    Write-Host "Calculating target sheets"
    foreach ($wsCalc in @($ref,$data,$daily,$weekly,$weeklyNew,$loc,$sponsors,$speakers,$required,$ros,$instructions)) {
        Write-Host ("  calculating " + $wsCalc.Name)
        Invoke-ComRetry { $wsCalc.Calculate() } | Out-Null
    }
    Start-Sleep -Seconds 2

    Write-Host "Reacquiring sheets for verification"
    $data = GetSheet $wb "Data Input"
    $daily = GetSheet $wb "Daily Schedule"
    $weeklyNew = GetSheet $wb "Weekly Calendar New"
    $loc = GetSheet $wb "Location Schedule"
    $sponsors = GetSheet $wb "Sponsors"
    $speakers = GetSheet $wb "Speakers"
    $required = GetSheet $wb "Required Attendees"
    $ros = GetSheet $wb "Run of Show"
    $instructions = GetSheet $wb "Instructions"

    Write-Host "Collecting results"
    $results = [ordered]@{
        qaCopy = $qa
        insertedRows = [ordered]@{ categoryReferenceRow = $categoryRow; timed = $testRow; tbd = $tbdRow; allDay = $allDayRow }
        validationStillPresent = [ordered]@{
            categoryJ4 = [string]$data.Range("J4").Validation.Formula1
            testCategoryJ = [string]$data.Range("J$testRow").Validation.Formula1
        }
        scheduleSlots = [ordered]@{
            timed = CellText $data $testRow 19
            tbd = CellText $data $tbdRow 19
            allDay = CellText $data $allDayRow 19
        }
        daily = [ordered]@{
            timedAppears = RangeContains $daily "B1:K140" "QA Test Timed Event"
            tbdAppears = RangeContains $daily "B1:K140" "QA Test TBD Event"
            allDayAppearsInScheduleArea = RangeContains $daily "B1:K140" "QA Test All Day Event"
        }
        liveViewsContainTimedEvent = [ordered]@{
            weeklyCalendarNew = SheetContains $weeklyNew "QA Test Timed Event"
            locationSchedule = SheetContains $loc "QA Test Timed Event"
            sponsors = SheetContains $sponsors "QA Test Timed Event"
            speakers = SheetContains $speakers "QA Test Timed Event"
            requiredAttendees = SheetContains $required "QA Test Timed Event"
        }
        liveViewsContainPeopleSponsor = [ordered]@{
            sponsor = SheetContains $sponsors "QA Sponsor"
            speaker = SheetContains $speakers "QA Speaker"
            required = SheetContains $required "QA Required"
        }
        runOfShow = [ordered]@{
            selectedEvent = CellText $ros 4 2
            selectedRow = CellText $ros 6 2
            start = CellText $ros 9 2
            end = CellText $ros 9 4
            firstMinute = CellText $ros 20 1
            secondMinute = CellText $ros 21 1
        }
        instructions = [ordered]@{
            exists = $true
            title = CellText $instructions 1 1
            dataInputRow = CellText $instructions 4 1
            runOfShowMention = SheetContains $instructions "Minute-by-minute"
        }
        targetedFormulaErrorChecks = [ordered]@{
            weeklyCalendarNewA4 = CellText $weeklyNew 4 1
            locationScheduleA4 = CellText $loc 4 1
            sponsorsA4 = CellText $sponsors 4 1
            speakersA4 = CellText $speakers 4 1
            requiredAttendeesA4 = CellText $required 4 1
            runOfShowB4 = CellText $ros 4 2
        }
    }

    Write-Host "Writing JSON"
    $results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

    Write-Host "Saving QA workbook"
    Invoke-ComRetry { $wb.Save() } | Out-Null
    Invoke-ComRetry { $wb.Close($true) } | Out-Null
    Release-ComObject $wb

    # Reopen once more to confirm the saved workbook opens and recalculates cleanly.
    Write-Host "Reopening QA workbook"
    $wb2 = Invoke-ComRetry { $excel.Workbooks.Open($qa) }
    Invoke-ComRetry { $excel.Calculation = $xlCalculationManual } | Out-Null
    $weeklyNew2 = GetSheet $wb2 "Weekly Calendar New"
    $reopenOk = SheetContains $weeklyNew2 "QA Test Timed Event"
    $wb2.Close($false)
    Release-ComObject $wb2

    $saved = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    $saved | Add-Member -NotePropertyName reopenWeeklyStillContainsTimedEvent -NotePropertyValue $reopenOk
    $saved | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    Write-Host "QA complete: $jsonPath"
}
finally {
    $excel.Quit()
    Release-ComObject $excel
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
