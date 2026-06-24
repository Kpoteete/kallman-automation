$ErrorActionPreference = "Stop"

$baseDir = "C:\kwi-automations\projects\Schedule of events"
$source = Join-Path $baseDir "Schedule of Events V8.2 - Codex Run of Show.xlsx"
$qa = Join-Path $baseDir "Schedule of Events V8.2 - Codex QA Working Copy.xlsx"

if (Test-Path $qa) { Remove-Item -LiteralPath $qa -Force }
Copy-Item -LiteralPath $source -Destination $qa -Force

$xlOpenXMLWorkbook = 51
$xlCalculationAutomatic = -4105

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$excel.AskToUpdateLinks = $false
$excel.EnableEvents = $false

function Release-ComObject($obj) {
    if ($null -ne $obj) {
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($obj)
    }
}

function Invoke-ComRetry([scriptblock]$action) {
    $lastError = $null
    for ($i = 1; $i -le 25; $i++) {
        try {
            return & $action
        }
        catch [System.Runtime.InteropServices.COMException] {
            $lastError = $_
            Start-Sleep -Milliseconds 250
        }
    }
    throw $lastError
}

function CellText($sheet, [int]$row, [int]$col) {
    if ($null -eq $sheet) { throw "CellText received a null sheet for R$row`C$col" }
    $cell = Invoke-ComRetry { $sheet.Cells.Item($row, $col) }
    $text = Invoke-ComRetry { [string]$cell.Text }
    Release-ComObject $cell
    return $text
}

function CellValue($sheet, [int]$row, [int]$col) {
    if ($null -eq $sheet) { throw "CellValue received a null sheet for R$row`C$col" }
    $cell = Invoke-ComRetry { $sheet.Cells.Item($row, $col) }
    $value = Invoke-ComRetry { $cell.Value2 }
    Release-ComObject $cell
    return $value
}

function SetCellValue($sheet, [int]$row, [int]$col, $value) {
    if ($null -eq $sheet) { throw "SetCellValue received a null sheet for R$row`C$col" }
    $cell = Invoke-ComRetry { $sheet.Cells.Item($row, $col) }
    if ($value -is [datetime]) {
        Invoke-ComRetry { $cell.Value = $value } | Out-Null
    }
    else {
        Invoke-ComRetry { $cell.Value2 = $value } | Out-Null
    }
    Release-ComObject $cell
}

function FindFirstBlankEventRow($ws) {
    for ($r = 4; $r -le 484; $r++) {
        $eventName = CellText $ws $r 8
        if ([string]::IsNullOrWhiteSpace($eventName)) { return $r }
    }
    throw "No blank event row found in Data Input H4:H484"
}

function UsedRangeTexts($ws) {
    $used = $ws.UsedRange
    $rows = $used.Rows.Count
    $cols = $used.Columns.Count
    $out = New-Object System.Collections.Generic.List[string]
    for ($r = 1; $r -le $rows; $r++) {
        for ($c = 1; $c -le $cols; $c++) {
            $text = CellText $ws $r $c
            if ($text -match "#(REF!|VALUE!|NAME\?|SPILL!|N/A|DIV/0!|NULL!|NUM!)") {
                $out.Add("$($ws.Name)!R$r`C$c=$text")
            }
        }
    }
    Release-ComObject $used
    return $out
}

function GetValidationFormula($sheet, [string]$addr) {
    $range = $sheet.Range($addr)
    $formula = $null
    try { $formula = [string]$range.Validation.Formula1 } catch { $formula = "<none>" }
    Release-ComObject $range
    return $formula
}

function GetFormula($sheet, [string]$addr) {
    $range = $sheet.Range($addr)
    $formula = [string]$range.Formula2
    Release-ComObject $range
    return $formula
}

function GetSheet($workbook, [string]$name) {
    return Invoke-ComRetry { $workbook.Worksheets.Item($name) }
}

try {
    $wb = $excel.Workbooks.Open($qa)
    $excel.Calculation = $xlCalculationAutomatic

    $sheets = @()
    foreach ($ws in $wb.Worksheets) { $sheets += $ws.Name }

    $data = GetSheet $wb "Data Input"
    $daily = GetSheet $wb "Daily Schedule"
    $weeklyNew = GetSheet $wb "Weekly Calendar New"
    $loc = GetSheet $wb "Location Schedule"
    $sponsors = GetSheet $wb "Sponsors"
    $speakers = GetSheet $wb "Speakers"
    $required = GetSheet $wb "Required Attendees"
    $ros = GetSheet $wb "Run of Show"

    $categoryValidation = GetValidationFormula $data "J4"
    $locationValidation = GetValidationFormula $data "I4"
    $classValidation = GetValidationFormula $data "P4"
    $statusValidation = GetValidationFormula $data "R4"

    $weeklyFormulaProbe = GetFormula $weeklyNew "B4"
    $locFormulaProbe = GetFormula $loc "A4"
    $sponsorFormulaProbe = GetFormula $sponsors "A4"

    $testRow = FindFirstBlankEventRow $data
    $prevRow = $testRow - 1
    $src = $data.Range("A$prevRow:S$prevRow")
    $dst = $data.Range("A$testRow:S$testRow")
    $src.Copy($dst) | Out-Null
    Release-ComObject $src
    Release-ComObject $dst

    $testCategory = "Category: QA Test Category"
    SetCellValue $data $testRow 1 "Farnborough 2026"
    SetCellValue $data $testRow 2 "KWI"
    SetCellValue $data $testRow 3 "QA Tester"
    SetCellValue $data $testRow 4 ([datetime]"2026-07-23")
    SetCellValue $data $testRow 5 ([datetime]"1899-12-30 11:30")
    SetCellValue $data $testRow 6 ([datetime]"2026-07-23")
    SetCellValue $data $testRow 7 ([datetime]"1899-12-30 11:45")
    SetCellValue $data $testRow 8 "QA Test Timed Event"
    SetCellValue $data $testRow 9 "The FORUM"
    SetCellValue $data $testRow 10 $testCategory
    SetCellValue $data $testRow 11 "QA description"
    SetCellValue $data $testRow 12 "QA internal note"
    SetCellValue $data $testRow 13 "QA Required"
    SetCellValue $data $testRow 14 "QA Speaker"
    SetCellValue $data $testRow 15 "QA Sponsor"
    SetCellValue $data $testRow 16 "Public"
    SetCellValue $data $testRow 17 ""
    SetCellValue $data $testRow 18 "Confirmed"

    $tbdRow = $testRow + 1
    $src2 = $data.Range("A$prevRow:S$prevRow")
    $dst2 = $data.Range("A$tbdRow:S$tbdRow")
    $src2.Copy($dst2) | Out-Null
    Release-ComObject $src2
    Release-ComObject $dst2
    SetCellValue $data $tbdRow 1 "Farnborough 2026"
    SetCellValue $data $tbdRow 2 "KWI"
    SetCellValue $data $tbdRow 3 "QA Tester"
    SetCellValue $data $tbdRow 4 ([datetime]"2026-07-23")
    SetCellValue $data $tbdRow 5 "TBD"
    SetCellValue $data $tbdRow 6 ([datetime]"2026-07-23")
    SetCellValue $data $tbdRow 7 "TBD"
    SetCellValue $data $tbdRow 8 "QA Test TBD Event"
    SetCellValue $data $tbdRow 9 "The FORUM"
    SetCellValue $data $tbdRow 10 $testCategory
    SetCellValue $data $tbdRow 13 "QA Required"
    SetCellValue $data $tbdRow 14 "QA Speaker"
    SetCellValue $data $tbdRow 15 "QA Sponsor"
    SetCellValue $data $tbdRow 16 "Private"
    SetCellValue $data $tbdRow 18 "Confirmed"

    $allDayRow = $testRow + 2
    $src3 = $data.Range("A$prevRow:S$prevRow")
    $dst3 = $data.Range("A$allDayRow:S$allDayRow")
    $src3.Copy($dst3) | Out-Null
    Release-ComObject $src3
    Release-ComObject $dst3
    SetCellValue $data $allDayRow 1 "Farnborough 2026"
    SetCellValue $data $allDayRow 2 "KWI"
    SetCellValue $data $allDayRow 3 "QA Tester"
    SetCellValue $data $allDayRow 4 ([datetime]"2026-07-23")
    SetCellValue $data $allDayRow 5 "All Day"
    SetCellValue $data $allDayRow 6 ([datetime]"2026-07-23")
    SetCellValue $data $allDayRow 7 "All Day"
    SetCellValue $data $allDayRow 8 "QA Test All Day Event"
    SetCellValue $data $allDayRow 9 "The FORUM"
    SetCellValue $data $allDayRow 10 $testCategory
    SetCellValue $data $allDayRow 13 "QA Required"
    SetCellValue $data $allDayRow 14 "QA Speaker"
    SetCellValue $data $allDayRow 15 "QA Sponsor"
    SetCellValue $data $allDayRow 16 "Public"
    SetCellValue $data $allDayRow 18 "Confirmed"

    $daily.Range("B2").Value2 = [datetime]"2026-07-23"
    $ros.Range("B3").Value2 = "The FORUM"
    $ros.Range("B4").Value2 = "QA Test Timed Event"
    $wb.RefreshAll()
    $excel.CalculateFullRebuild()

    $dailyTimedHits = New-Object System.Collections.Generic.List[string]
    $dailyTbdHits = New-Object System.Collections.Generic.List[string]
    $dailyAllDayTimedHits = New-Object System.Collections.Generic.List[string]
    $tbdMarkerRows = New-Object System.Collections.Generic.List[int]
    for ($r = 1; $r -le 140; $r++) {
        $a = CellText $daily $r 1
        if ($a -eq "TBD") { $tbdMarkerRows.Add($r) }
        for ($c = 2; $c -le 11; $c++) {
            $txt = CellText $daily $r $c
            if ($txt -like "*QA Test Timed Event*") { $dailyTimedHits.Add("R$r`C$c") }
            if ($txt -like "*QA Test TBD Event*") { $dailyTbdHits.Add("R$r`C$c") }
            if ($txt -like "*QA Test All Day Event*") { $dailyAllDayTimedHits.Add("R$r`C$c") }
        }
    }

    $tabsToSearch = @{
        "Weekly Calendar New" = $weeklyNew
        "Location Schedule" = $loc
        "Sponsors" = $sponsors
        "Speakers" = $speakers
        "Required Attendees" = $required
    }
    $tabHits = @{}
    foreach ($key in $tabsToSearch.Keys) {
        $ws = $tabsToSearch[$key]
        $hits = New-Object System.Collections.Generic.List[string]
        $used = $ws.UsedRange
        for ($r = 1; $r -le $used.Rows.Count; $r++) {
            for ($c = 1; $c -le $used.Columns.Count; $c++) {
                $txt = CellText $ws $r $c
                if ($txt -like "*QA Test Timed Event*" -or $txt -like "*QA Sponsor*" -or $txt -like "*QA Speaker*" -or $txt -like "*QA Required*") {
                    $hits.Add("R$r`C$c=$txt")
                }
            }
        }
        Release-ComObject $used
        $tabHits[$key] = @($hits)
    }

    $rosChecks = [ordered]@{
        Location = CellText $ros 3 2
        Event = CellText $ros 4 2
        Row = CellText $ros 6 2
        Date = CellText $ros 8 2
        Start = CellText $ros 9 2
        End = CellText $ros 9 4
        TimelineFirst = CellText $ros 20 1
        TimelineSecond = CellText $ros 21 1
    }

    $errorHits = New-Object System.Collections.Generic.List[string]
    foreach ($sheetName in $sheets) {
        $ws = GetSheet $wb $sheetName
        $hits = UsedRangeTexts $ws
        foreach ($hit in $hits) { $errorHits.Add($hit) }
        Release-ComObject $ws
    }

    $result = [ordered]@{
        qaCopy = $qa
        sheets = $sheets
        dataValidation = [ordered]@{
            categoryJ4 = $categoryValidation
            locationI4 = $locationValidation
            classP4 = $classValidation
            statusR4 = $statusValidation
        }
        formulaProbes = [ordered]@{
            weeklyCalendarNewB4 = $weeklyFormulaProbe
            locationScheduleA4 = $locFormulaProbe
            sponsorsA4 = $sponsorFormulaProbe
        }
        insertedRows = [ordered]@{
            timed = $testRow
            tbd = $tbdRow
            allDay = $allDayRow
        }
        scheduleSlots = [ordered]@{
            timed = CellText $data $testRow 19
            tbd = CellText $data $tbdRow 19
            allDay = CellText $data $allDayRow 19
        }
        daily = [ordered]@{
            timedHits = @($dailyTimedHits)
            tbdMarkerRows = @($tbdMarkerRows)
            tbdHits = @($dailyTbdHits)
            allDayHitsInGrid = @($dailyAllDayTimedHits)
        }
        generatedTabHits = $tabHits
        runOfShow = $rosChecks
        errorHits = @($errorHits)
    }

    $jsonPath = Join-Path $baseDir ".codex-work\qa_probe_v8_2_results.json"
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

    $wb.Save()
    $wb.Close($true)
    Release-ComObject $wb
    Write-Host "QA probe complete: $jsonPath"
}
finally {
    $excel.Quit()
    Release-ComObject $excel
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
