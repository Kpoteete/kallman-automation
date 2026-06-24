$project = "C:\kwi-automations\projects\Schedule of events"
$source = Join-Path $project "Schedule of events v - category color formatting.xlsx"
if (-not (Test-Path -LiteralPath $source)) {
    $source = Join-Path $project "Schedule of events v - Codex weekly calendar view.xlsx"
}
$output = Join-Path $project "Schedule of events v - category conditional formatting fixed.xlsx"
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
    $last = $StartRow - 1
    for ($r = $StartRow; $r -le 250; $r++) {
        $v = [string](Invoke-ComRetry { $Sheet.Cells.Item($r, $Column).Value2 })
        if (-not [string]::IsNullOrWhiteSpace($v)) {
            $existing[$v.Trim()] = $true
            $last = $r
        }
    }
    foreach ($item in $Items) {
        if (-not $existing.ContainsKey($item)) {
            $last++
            Invoke-ComRetry { $Sheet.Cells.Item($last, $Column).Value2 = $item } | Out-Null
            $existing[$item] = $true
        }
    }
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
    $daily = Invoke-ComRetry { $wb.Worksheets.Item("Daily Schedule") }
    $weekly = $null
    try { $weekly = Invoke-ComRetry { $wb.Worksheets.Item("Weekly Calendar") } } catch {}

    $placeholders = @("TPD1","TPD2","TPD3","TPD4","TPD5","TPD6","TPD7","TPD8","TPD9","TPD10")
    Add-UniqueListItems -Sheet $ref -Column 3 -Items $placeholders
    Add-UniqueListItems -Sheet $ref -Column 10 -Items $placeholders -StartRow 3
    Add-UniqueListItems -Sheet $ref -Column 20 -Items $placeholders

    $dailyRange = Invoke-ComRetry { $daily.Range("B7:K107") }
    Invoke-ComRetry { $dailyRange.FormatConditions.Delete() } | Out-Null
    Invoke-ComRetry { $dailyRange.Interior.ColorIndex = 0 } | Out-Null
    foreach ($category in $colorMap.Keys) {
        $escaped = $category.Replace('"', '""')
        $formula = "=ISNUMBER(SEARCH(""$escaped"",B7))"
        $rule = Invoke-ComRetry { $dailyRange.FormatConditions.Add(2, 0, $formula) }
        $rule.Interior.Color = Convert-Rgb $colorMap[$category]
        $rule.Font.Color = Convert-Rgb "#1F2933"
        $rule.StopIfTrue = $true
    }

    if ($weekly -ne $null) {
        $weeklyRange = Invoke-ComRetry { $weekly.Range("B7:H23") }
        Invoke-ComRetry { $weeklyRange.FormatConditions.Delete() } | Out-Null
        Invoke-ComRetry { $weeklyRange.Interior.ColorIndex = 0 } | Out-Null
        foreach ($category in $colorMap.Keys) {
            $escaped = $category.Replace('"', '""')
            $formula = "=SUMPRODUCT(('Data Input'!`$A`$4:`$A`$484=`$C`$3)*('Data Input'!`$C`$4:`$C`$484=B`$6)*('Data Input'!`$D`$4:`$D`$484<(`$A7+TIME(1,0,0)))*('Data Input'!`$F`$4:`$F`$484>`$A7)*('Data Input'!`$I`$4:`$I`$484=""$escaped"")*((`$D`$3="""")+('Data Input'!`$B`$4:`$B`$484=`$D`$3))*((`$E`$3="""")+('Data Input'!`$I`$4:`$I`$484=`$E`$3))*((`$F`$3="""")+('Data Input'!`$J`$4:`$J`$484=`$F`$3))*((`$G`$3="""")+('Data Input'!`$K`$4:`$K`$484=`$G`$3)))>0"
            $rule = Invoke-ComRetry { $weeklyRange.FormatConditions.Add(2, 0, $formula) }
            $rule.Interior.Color = Convert-Rgb $colorMap[$category]
            $rule.Font.Color = Convert-Rgb "#1F2933"
            $rule.StopIfTrue = $true
        }
    }

    Invoke-ComRetry { $wb.Save() } | Out-Null

    [PSCustomObject]@{
        Output = $output
        DailyRuleCount = $dailyRange.FormatConditions.Count
        WeeklyRuleCount = if ($weekly -ne $null) { $weekly.Range("B7:H23").FormatConditions.Count } else { 0 }
        TPD1 = if ($ref.Range("C:C").Find("TPD1") -ne $null) { $ref.Range("C:C").Find("TPD1").Address($false,$false) } else { "missing" }
        TPD10 = if ($ref.Range("C:C").Find("TPD10") -ne $null) { $ref.Range("C:C").Find("TPD10").Address($false,$false) } else { "missing" }
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
