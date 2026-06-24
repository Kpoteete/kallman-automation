$project = "C:\kwi-automations\projects\Schedule of events"
$source = Join-Path $project "Schedule of events v - Codex weekly calendar view.xlsx"
$output = Join-Path $project "Schedule of events v - category color formatting.xlsx"
$ErrorActionPreference = "Stop"

Copy-Item -LiteralPath $source -Destination $output -Force

function Invoke-ComRetry {
    param([scriptblock]$Action)
    for ($try = 1; $try -le 25; $try++) {
        try { return & $Action }
        catch [System.Runtime.InteropServices.COMException] {
            if ($try -eq 25) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

function Convert-Rgb {
    param([string]$Hex)
    $h = $Hex.TrimStart("#")
    $r = [Convert]::ToInt32($h.Substring(0, 2), 16)
    $g = [Convert]::ToInt32($h.Substring(2, 2), 16)
    $b = [Convert]::ToInt32($h.Substring(4, 2), 16)
    return $r + ($g * 256) + ($b * 65536)
}

function Add-UniqueListItems {
    param($Sheet, [int]$Column, [string[]]$Items, [int]$StartRow = 2)
    $existing = @{}
    for ($r = $StartRow; $r -le 250; $r++) {
        $v = [string](Invoke-ComRetry { $Sheet.Cells.Item($r, $Column).Value2 })
        if (-not [string]::IsNullOrWhiteSpace($v)) { $existing[$v.Trim()] = $true }
    }
    $last = $StartRow - 1
    for ($r = $StartRow; $r -le 250; $r++) {
        $v = [string](Invoke-ComRetry { $Sheet.Cells.Item($r, $Column).Value2 })
        if (-not [string]::IsNullOrWhiteSpace($v)) { $last = $r }
    }
    foreach ($item in $Items) {
        if (-not $existing.ContainsKey($item)) {
            $last++
            Invoke-ComRetry { $Sheet.Cells.Item($last, $Column).Value2 = $item } | Out-Null
            $existing[$item] = $true
        }
    }
}

function Get-TimeSerial {
    param($Value)
    if ($null -eq $Value -or $Value -eq "") { return $null }
    if ($Value -is [double] -or $Value -is [int]) { return [double]$Value }
    if ($Value -is [datetime]) { return (($Value.Hour * 3600) + ($Value.Minute * 60) + $Value.Second) / 86400 }
    return [double]$Value
}

$colorMap = [ordered]@{
    "Forum" = "#DDEBFF"
    "Lunch" = "#FFF2CC"
    "Meet & Greet" = "#E2F0D9"
    "Opening Ceremony" = "#EADCF8"
    "Pavilion Tour" = "#DDEBF7"
    "Reception" = "#FCE4D6"
    "Summit / Keynote / Panel / Speaker" = "#E4DFEC"
    "Welcome Reception" = "#FCE4EC"
    "Keynote" = "#EAF3F8"
    "Speaker" = "#E4DFEC"
    "TPD1" = "#F2F2F2"
    "TPD2" = "#EAF7EA"
    "TPD3" = "#EAF2F8"
    "TPD4" = "#F8EAF2"
    "TPD5" = "#FFF7E6"
    "TPD6" = "#EDE7F6"
    "TPD7" = "#E0F2F1"
    "TPD8" = "#F3E5F5"
    "TPD9" = "#FBE9E7"
    "TPD10" = "#ECEFF1"
}

$excel = $null
$wb = $null
try {
    $excel = New-Object -ComObject Excel.Application
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $excel.EnableEvents = $false
    $wb = Invoke-ComRetry { $excel.Workbooks.Open($output, 0, $false) }

    $ref = Invoke-ComRetry { $wb.Worksheets.Item("Reference") }
    $data = Invoke-ComRetry { $wb.Worksheets.Item("Data Input") }
    $daily = Invoke-ComRetry { $wb.Worksheets.Item("Daily Schedule") }
    $weekly = $null
    try { $weekly = Invoke-ComRetry { $wb.Worksheets.Item("Weekly Calendar") } } catch {}

    $placeholderCategories = @("TPD1","TPD2","TPD3","TPD4","TPD5","TPD6","TPD7","TPD8","TPD9","TPD10")
    Add-UniqueListItems -Sheet $ref -Column 3 -Items $placeholderCategories
    Add-UniqueListItems -Sheet $ref -Column 10 -Items $placeholderCategories -StartRow 3
    Add-UniqueListItems -Sheet $ref -Column 20 -Items $placeholderCategories

    $dailyRange = Invoke-ComRetry { $daily.Range("B7:K107") }
    Invoke-ComRetry { $dailyRange.FormatConditions.Delete() } | Out-Null
    foreach ($category in $colorMap.Keys) {
        $escaped = $category.Replace('"', '""')
        $formula = "=ISNUMBER(SEARCH(""$escaped"",B7))"
        $rule = Invoke-ComRetry { $dailyRange.FormatConditions.Add(2, 0, $formula) }
        $rule.Interior.Color = Convert-Rgb $colorMap[$category]
        $rule.Font.Color = Convert-Rgb "#1F2933"
        $rule.StopIfTrue = $false
    }

    if ($weekly -ne $null) {
        $weeklyRange = Invoke-ComRetry { $weekly.Range("B7:H23") }
        Invoke-ComRetry { $weeklyRange.Interior.Color = Convert-Rgb "#FFFFFF" } | Out-Null
        $weekStart = [double](Invoke-ComRetry { $weekly.Range("B3").Value2 })
        $showFilter = [string](Invoke-ComRetry { $weekly.Range("C3").Value2 })
        $ownerFilter = [string](Invoke-ComRetry { $weekly.Range("D3").Value2 })
        $categoryFilter = [string](Invoke-ComRetry { $weekly.Range("E3").Value2 })
        $locationFilter = [string](Invoke-ComRetry { $weekly.Range("F3").Value2 })
        $classFilter = [string](Invoke-ComRetry { $weekly.Range("G3").Value2 })

        for ($day = 0; $day -lt 7; $day++) {
            for ($hour = 6; $hour -le 22; $hour++) {
                $chosenCategory = $null
                for ($row = 4; $row -le 484; $row++) {
                    $show = [string](Invoke-ComRetry { $data.Cells.Item($row, 1).Value2 })
                    if ([string]::IsNullOrWhiteSpace($show)) { continue }
                    if ($showFilter -and $show -ne $showFilter) { continue }
                    $owner = [string](Invoke-ComRetry { $data.Cells.Item($row, 2).Value2 })
                    $startDate = Invoke-ComRetry { $data.Cells.Item($row, 3).Value2 }
                    $startTime = Get-TimeSerial (Invoke-ComRetry { $data.Cells.Item($row, 4).Value2 })
                    $endTime = Get-TimeSerial (Invoke-ComRetry { $data.Cells.Item($row, 6).Value2 })
                    $eventName = [string](Invoke-ComRetry { $data.Cells.Item($row, 7).Value2 })
                    $category = [string](Invoke-ComRetry { $data.Cells.Item($row, 9).Value2 })
                    $location = [string](Invoke-ComRetry { $data.Cells.Item($row, 10).Value2 })
                    $class = [string](Invoke-ComRetry { $data.Cells.Item($row, 11).Value2 })
                    if ([string]::IsNullOrWhiteSpace($eventName) -or $null -eq $startDate -or $null -eq $startTime -or $null -eq $endTime) { continue }
                    if ($ownerFilter -and $owner -ne $ownerFilter) { continue }
                    if ($categoryFilter -and $category -ne $categoryFilter) { continue }
                    if ($locationFilter -and $location -ne $locationFilter) { continue }
                    if ($classFilter -and $class -ne $classFilter) { continue }
                    $dayOffset = [int]([double]$startDate - $weekStart)
                    if ($dayOffset -ne $day) { continue }
                    if ($startTime -lt (($hour + 1) / 24) -and $endTime -gt ($hour / 24)) {
                        $chosenCategory = $category
                        break
                    }
                }
                if ($chosenCategory -and $colorMap.Contains($chosenCategory)) {
                    $targetRow = 7 + ($hour - 6)
                    $targetCol = 2 + $day
                    Invoke-ComRetry { $weekly.Cells.Item($targetRow, $targetCol).Interior.Color = Convert-Rgb $colorMap[$chosenCategory] } | Out-Null
                }
            }
        }
    }

    Invoke-ComRetry { $wb.Save() } | Out-Null

    [PSCustomObject]@{
        Output = $output
        AddedPlaceholders = ($placeholderCategories -join ", ")
        DailyRules = $colorMap.Count
        WeeklyCalendarFound = [bool]($weekly -ne $null)
    } | Format-List
} finally {
    if ($wb -ne $null) { $wb.Close($true) | Out-Null }
    if ($excel -ne $null) {
        $excel.EnableEvents = $true
        $excel.Quit() | Out-Null
    }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
