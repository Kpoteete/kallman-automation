$Project = 'C:\kwi-automations\projects\Schedule of events'
$Source = Join-Path $Project 'Schedule of events v - Codex weekly calendar view.xlsx'
$Output = Join-Path $Project 'Schedule of events v - category conditional formatting fixed v3.xlsx'

Copy-Item -LiteralPath $Source -Destination $Output -Force

$CategoryColors = [ordered]@{
  'Forum' = 'DDEBFF'
  'Lunch' = 'FFF2CC'
  'Meet & Greet' = 'E2F0D9'
  'Opening Ceremony' = 'EADCF8'
  'Pavilion Tour' = 'DDEBF7'
  'Reception' = 'FCE4D6'
  'Summit / Keynote / Panel / Speaker' = 'E4DFEC'
  'Welcome Reception' = 'FCE4EC'
  'TPD1' = 'F2F2F2'
  'TPD2' = 'EAF7EA'
  'TPD3' = 'EAF2F8'
  'TPD4' = 'F8EAF2'
  'TPD5' = 'FFF7E6'
  'TPD6' = 'EDE7F6'
  'TPD7' = 'E0F2F1'
  'TPD8' = 'F3E5F5'
  'TPD9' = 'FBE9E7'
  'TPD10' = 'ECEFF1'
}

function Invoke-ExcelRetry {
  param(
    [scriptblock]$ScriptBlock,
    [string]$Label
  )
  for ($i = 0; $i -lt 10; $i++) {
    try {
      return & $ScriptBlock
    } catch {
      Start-Sleep -Milliseconds (500 + 250 * $i)
    }
  }
  throw "Excel COM call failed: $Label"
}

function Convert-HexToExcelColor {
  param([string]$Hex)
  $r = [Convert]::ToInt32($Hex.Substring(0, 2), 16)
  $g = [Convert]::ToInt32($Hex.Substring(2, 2), 16)
  $b = [Convert]::ToInt32($Hex.Substring(4, 2), 16)
  return $r + ($g * 256) + ($b * 65536)
}

function Escape-ExcelText {
  param([string]$Text)
  return $Text.Replace('"', '""')
}

function Add-ReferenceValues {
  param($Sheet, [int]$Column, [string[]]$Values)
  foreach ($value in $Values) {
    $found = $false
    for ($row = 1; $row -le 300; $row++) {
      $text = Invoke-ExcelRetry { [string]$Sheet.Cells.Item($row, $Column).Text } "read reference $row,$Column"
      if ($text -eq $value) {
        $found = $true
        break
      }
    }
    if (-not $found) {
      for ($row = 2; $row -le 400; $row++) {
        $cell = Invoke-ExcelRetry { $Sheet.Cells.Item($row, $Column) } "reference cell $row,$Column"
        $isMerged = Invoke-ExcelRetry { $cell.MergeCells } "reference merged $row,$Column"
        $text = Invoke-ExcelRetry { [string]$cell.Text } "reference text $row,$Column"
        if ($isMerged -eq $false -and $text -eq '') {
          Invoke-ExcelRetry { $cell.Value2 = $value } "write reference $value"
          break
        }
      }
    }
  }
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$excel.EnableEvents = $false
$wb = $null

try {
  $wb = Invoke-ExcelRetry { $excel.Workbooks.Open($Output) } 'open workbook'
  Start-Sleep -Seconds 2

  $daily = Invoke-ExcelRetry { $wb.Worksheets.Item('Daily Schedule') } 'daily sheet'
  $weekly = Invoke-ExcelRetry { $wb.Worksheets.Item('Weekly Calendar') } 'weekly sheet'
  $ref = Invoke-ExcelRetry { $wb.Worksheets.Item('Reference') } 'reference sheet'

  $placeholders = 1..10 | ForEach-Object { "TPD$_" }
  foreach ($col in 3, 10, 20) {
    Add-ReferenceValues -Sheet $ref -Column $col -Values $placeholders
  }

  $dailyRange = $daily.Range('B7:K107')
  Invoke-ExcelRetry { $dailyRange.FormatConditions.Delete() } 'clear daily conditional formatting' | Out-Null

  foreach ($category in $CategoryColors.Keys) {
    $escaped = Escape-ExcelText $category
    $formula = "=ISNUMBER(SEARCH(""| $escaped"",B7))"
    $fc = Invoke-ExcelRetry { $dailyRange.FormatConditions.Add(2, [Type]::Missing, $formula) } "daily cf $category"
    $fc.Interior.Pattern = 1
    $fc.Interior.Color = Convert-HexToExcelColor $CategoryColors[$category]
  }

  # AA:AG mirrors the visible B:H week grid with the first matching category for each hour.
  $weekly.Range('AA1').Value2 = 'Category helper - do not print'
  for ($offset = 0; $offset -lt 7; $offset++) {
    $helperCol = 27 + $offset
    $dayCol = 2 + $offset
    $helperLetter = $weekly.Cells.Item(1, $helperCol).Address($false, $false).Substring(0, 2).TrimEnd('1')
    $dayLetter = $weekly.Cells.Item(1, $dayCol).Address($false, $false).TrimEnd('1')
    $weekly.Cells.Item(5, $helperCol).Formula = "=$dayLetter`$5"
    $weekly.Cells.Item(6, $helperCol).Formula = "=$dayLetter`$6"
    for ($row = 7; $row -le 23; $row++) {
      $formula = "=IFERROR(INDEX(FILTER('Data Input'!`$I`$4:`$I`$484,('Data Input'!`$A`$4:`$A`$484=`$C`$3)*('Data Input'!`$C`$4:`$C`$484=$dayLetter`$6)*('Data Input'!`$D`$4:`$D`$484<(`$A$row+TIME(1,0,0)))*('Data Input'!`$F`$4:`$F`$484>`$A$row)*IF(`$D`$3="""",1,'Data Input'!`$B`$4:`$B`$484=`$D`$3)*IF(`$E`$3="""",1,'Data Input'!`$I`$4:`$I`$484=`$E`$3)*IF(`$F`$3="""",1,'Data Input'!`$J`$4:`$J`$484=`$F`$3)*IF(`$G`$3="""",1,'Data Input'!`$K`$4:`$K`$484=`$G`$3)),1),"""")"
      $weekly.Cells.Item($row, $helperCol).Formula2 = $formula
    }
    $weekly.Columns.Item($helperCol).Hidden = $true
  }

  $weeklyRange = $weekly.Range('B7:H23')
  Invoke-ExcelRetry { $weeklyRange.FormatConditions.Delete() } 'clear weekly conditional formatting' | Out-Null
  foreach ($category in $CategoryColors.Keys) {
    $escaped = Escape-ExcelText $category
    $formula = "=AA7=""$escaped"""
    $fc = Invoke-ExcelRetry { $weeklyRange.FormatConditions.Add(2, [Type]::Missing, $formula) } "weekly cf $category"
    $fc.Interior.Pattern = 1
    $fc.Interior.Color = Convert-HexToExcelColor $CategoryColors[$category]
  }

  Invoke-ExcelRetry { $excel.CalculateFullRebuild() } 'calculate workbook' | Out-Null

  $checks = @(
    @('Daily Schedule', 'B7'),
    @('Daily Schedule', 'B17'),
    @('Daily Schedule', 'D19'),
    @('Weekly Calendar', 'C11'),
    @('Weekly Calendar', 'C14'),
    @('Weekly Calendar', 'C15'),
    @('Weekly Calendar', 'D14')
  )
  foreach ($check in $checks) {
    $ws = $wb.Worksheets.Item($check[0])
    $cell = $ws.Range($check[1])
    Write-Output ("{0}!{1} color={2} text={3}" -f $check[0], $check[1], $cell.DisplayFormat.Interior.Color, (($cell.Text -replace "`r?`n", ' | ').Substring(0, [Math]::Min(90, ($cell.Text -replace "`r?`n", ' | ').Length))))
  }
  foreach ($addr in 'AB11','AB14','AB15','AC14','AC16') {
    Write-Output ("Weekly helper {0}={1}" -f $addr, $weekly.Range($addr).Text)
  }

  Invoke-ExcelRetry { $wb.Save() } 'save workbook' | Out-Null
  Invoke-ExcelRetry { $wb.Close($true) } 'close workbook' | Out-Null
  $wb = Invoke-ExcelRetry { $excel.Workbooks.Open($Output) } 'reopen workbook'
  Write-Output 'Reopen OK'
  Invoke-ExcelRetry { $wb.Close($false) } 'close reopened workbook' | Out-Null
  $wb = $null
  Write-Output $Output
}
finally {
  if ($null -ne $wb) {
    try { $wb.Close($false) | Out-Null } catch {}
  }
  $excel.Quit()
  [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
}
