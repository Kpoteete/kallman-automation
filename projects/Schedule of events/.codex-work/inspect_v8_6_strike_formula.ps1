$ErrorActionPreference = "Stop"
$path = "C:\kwi-automations\projects\Schedule of events\Schedule of Events V8.6 - Codex Clean Tested Template.xlsx"
$json = "C:\kwi-automations\projects\Schedule of events\.codex-work\inspect_v8_6_strike_formula.json"

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

try {
    $wb = $excel.Workbooks.Open($path)
    $result = [ordered]@{
        file = $path
        sheets = @()
    }
    foreach ($ws in $wb.Worksheets) {
        $used = $ws.UsedRange
        $strikeCells = New-Object System.Collections.Generic.List[string]
        $formulaTextCells = New-Object System.Collections.Generic.List[string]
        $formulaCells = 0
        for ($r = 1; $r -le [Math]::Min($used.Rows.Count, 180); $r++) {
            for ($c = 1; $c -le [Math]::Min($used.Columns.Count, 30); $c++) {
                $cell = $used.Cells.Item($r,$c)
                try {
                    if ($cell.Font.Strikethrough -eq $true) {
                        $strikeCells.Add($cell.Address($false,$false) + "=" + [string]$cell.Text)
                    }
                    if ($cell.HasFormula) {
                        $formulaCells++
                        if ([string]$cell.Text -like "=*") {
                            $formulaTextCells.Add($cell.Address($false,$false) + "=" + [string]$cell.Text)
                        }
                    }
                } finally {
                    Release-ComObject $cell
                }
            }
        }
        $result.sheets += [ordered]@{
            name = $ws.Name
            usedRange = $used.Address($false,$false)
            displayPageBreaks = $ws.DisplayPageBreaks
            formulasInUsedScan = $formulaCells
            strikethroughSample = @($strikeCells | Select-Object -First 20)
            formulaDisplayedAsTextSample = @($formulaTextCells | Select-Object -First 20)
        }
        Release-ComObject $used
    }
    $wb.Close($false)
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $json -Encoding UTF8
    Write-Host $json
}
finally {
    $excel.Quit()
    Release-ComObject $excel
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
