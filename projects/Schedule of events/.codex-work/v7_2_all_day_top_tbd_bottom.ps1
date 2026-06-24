$Project = 'C:\kwi-automations\projects\Schedule of events'
$Source = Join-Path $Project 'Schedule of Events V7.1 - Codex.xlsx'
$Output = Join-Path $Project 'Schedule of Events V7.2 - Codex.xlsx'
Copy-Item -LiteralPath $Source -Destination $Output -Force

function Retry($ScriptBlock, $Label) {
  for ($i = 0; $i -lt 10; $i++) {
    try { return & $ScriptBlock } catch { Start-Sleep -Milliseconds (500 + 250 * $i) }
  }
  throw "Failed: $Label"
}

function SearchText() {
  return "'Data Input'!`$D`$4:`$D`$484&"" ""&'Data Input'!`$E`$4:`$E`$484&"" ""&'Data Input'!`$F`$4:`$F`$484&"" ""&'Data Input'!`$G`$4:`$G`$484"
}

function WhenText() {
  return "IFERROR(TEXT('Data Input'!`$D`$4:`$D`$484,""m/d/yyyy""),'Data Input'!`$D`$4:`$D`$484)&"" ""&IFERROR(TEXT('Data Input'!`$E`$4:`$E`$484,""h:mm AM/PM""),'Data Input'!`$E`$4:`$E`$484)&"" - ""&IFERROR(TEXT('Data Input'!`$F`$4:`$F`$484,""m/d/yyyy""),'Data Input'!`$F`$4:`$F`$484)&"" ""&IFERROR(TEXT('Data Input'!`$G`$4:`$G`$484,""h:mm AM/PM""),'Data Input'!`$G`$4:`$G`$484)"
}

function DailyFilters() {
  return "('Data Input'!`$A`$4:`$A`$484=`$C`$2)*((`$E`$2="""")+('Data Input'!`$B`$4:`$B`$484=`$E`$2))*((`$F`$2="""")+('Data Input'!`$J`$4:`$J`$484=`$F`$2))*((`$G`$2="""")+('Data Input'!`$I`$4:`$I`$484=`$G`$2))*((`$H`$2="""")+('Data Input'!`$P`$4:`$P`$484=`$H`$2))"
}

function WeeklyFilters() {
  return "('Data Input'!`$A`$4:`$A`$484=`$C`$3)*((`$D`$3="""")+('Data Input'!`$B`$4:`$B`$484=`$D`$3))*((`$E`$3="""")+('Data Input'!`$J`$4:`$J`$484=`$E`$3))*((`$F`$3="""")+('Data Input'!`$I`$4:`$I`$484=`$F`$3))*((`$G`$3="""")+('Data Input'!`$P`$4:`$P`$484=`$G`$3))"
}

function DailyAllDayCondition() {
  $txt = SearchText
  return "(ISNUMBER(SEARCH(""ALL DAY"",UPPER($txt))))*((IFERROR('Data Input'!`$D`$4:`$D`$484=`$B`$2,FALSE)+IFERROR('Data Input'!`$F`$4:`$F`$484=`$B`$2,FALSE))>0)*$(DailyFilters)"
}

function WeeklyAllDayCondition() {
  $txt = SearchText
  return "(ISNUMBER(SEARCH(""ALL DAY"",UPPER($txt))))*((IFERROR(('Data Input'!`$D`$4:`$D`$484>=`$B`$3)*('Data Input'!`$D`$4:`$D`$484<=`$B`$3+6),FALSE)+IFERROR(('Data Input'!`$F`$4:`$F`$484>=`$B`$3)*('Data Input'!`$F`$4:`$F`$484<=`$B`$3+6),FALSE))>0)*$(WeeklyFilters)"
}

function DailyTbdCondition() {
  $txt = SearchText
  return "(ISNUMBER(SEARCH(""TBD"",UPPER($txt))))*((IFERROR('Data Input'!`$D`$4:`$D`$484=`$B`$2,FALSE)+IFERROR('Data Input'!`$F`$4:`$F`$484=`$B`$2,FALSE))>0)*$(DailyFilters)"
}

function WeeklyTbdCondition() {
  $txt = SearchText
  return "(ISNUMBER(SEARCH(""TBD"",UPPER($txt))))*((IFERROR(('Data Input'!`$D`$4:`$D`$484>=`$B`$3)*('Data Input'!`$D`$4:`$D`$484<=`$B`$3+6),FALSE)+IFERROR(('Data Input'!`$F`$4:`$F`$484>=`$B`$3)*('Data Input'!`$F`$4:`$F`$484<=`$B`$3+6),FALSE))>0)*$(WeeklyFilters)"
}

function AllDayTitleFormula($Condition) {
  return "=IFERROR(IF(ROWS(FILTER('Data Input'!`$H`$4:`$H`$484,$Condition))>0,""All-Day Events"",""""),"""")"
}

function AllDayDataFormula($Condition) {
  return "=IFERROR(FILTER(CHOOSE({1,2,3},'Data Input'!`$H`$4:`$H`$484,'Data Input'!`$B`$4:`$B`$484,'Data Input'!`$J`$4:`$J`$484),$Condition),"""")"
}

function TbdTitleFormula($Condition) {
  return "=IFERROR(IF(ROWS(FILTER('Data Input'!`$H`$4:`$H`$484,$Condition))>0,""TBD Events"",""""),"""")"
}

function TbdDataFormula($Condition) {
  $when = WhenText
  return "=IFERROR(FILTER(CHOOSE({1,2,3,4,5,6},$when,'Data Input'!`$H`$4:`$H`$484,'Data Input'!`$B`$4:`$B`$484,'Data Input'!`$J`$4:`$J`$484,'Data Input'!`$I`$4:`$I`$484,'Data Input'!`$R`$4:`$R`$484),$Condition),"""")"
}

function StyleTopAllDay($Sheet, $Title, $Header, $Body) {
  Retry { $Title.Merge() } 'merge all-day title' | Out-Null
  Retry { $Title.Font.Bold = $true } 'all-day title bold' | Out-Null
  Retry { $Title.Interior.Color = 10086143 } 'all-day title fill' | Out-Null
  Retry { $Header.Value2 = @('Event Title','Event Owner','Category') } 'all-day header text' | Out-Null
  Retry { $Header.Font.Bold = $true } 'all-day header bold' | Out-Null
  Retry { $Header.Interior.Color = 14277081 } 'all-day header fill' | Out-Null
  Retry { $Body.WrapText = $true } 'all-day body wrap' | Out-Null
  Retry { $Header.Borders.LineStyle = 1 } 'all-day header border' | Out-Null
  Retry { $Body.Borders.LineStyle = 1 } 'all-day body border' | Out-Null
}

function StyleTbd($Sheet, $Title, $Header, $Body) {
  Retry { $Title.Merge() } 'merge tbd title' | Out-Null
  Retry { $Title.Font.Bold = $true } 'tbd title bold' | Out-Null
  Retry { $Title.Interior.Color = 13434879 } 'tbd title fill' | Out-Null
  Retry { $Header.Value2 = @('Date / Time','Event','Event Owner','Category','Location','Status') } 'tbd header text' | Out-Null
  Retry { $Header.Font.Bold = $true } 'tbd header bold' | Out-Null
  Retry { $Header.Interior.Color = 14277081 } 'tbd header fill' | Out-Null
  Retry { $Body.WrapText = $true } 'tbd body wrap' | Out-Null
  Retry { $Header.Borders.LineStyle = 1 } 'tbd header border' | Out-Null
  Retry { $Body.Borders.LineStyle = 1 } 'tbd body border' | Out-Null
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$excel.EnableEvents = $false
$wb = $null
try {
  $wb = Retry { $excel.Workbooks.Open($Output) } 'open workbook'
  $daily = Retry { $wb.Worksheets.Item('Daily Schedule') } 'daily sheet'
  $weekly = Retry { $wb.Worksheets.Item('Weekly Calendar') } 'weekly sheet'

  # Remove V7.1's fixed special sections before creating the new top/bottom areas.
  Retry { $daily.Range('A110:F126').Clear() } 'clear old daily special' | Out-Null
  Retry { $weekly.Range('A25:F41').Clear() } 'clear old weekly special' | Out-Null

  # Daily: insert an all-day section above the timed schedule.
  Retry { $daily.Rows('6:9').Insert() } 'insert daily all-day rows' | Out-Null
  StyleTopAllDay $daily $daily.Range('A6:C6') $daily.Range('A7:C7') $daily.Range('A8:C9')
  $daily.Range('A6').Formula2 = AllDayTitleFormula (DailyAllDayCondition)
  $daily.Range('A8').Formula2 = AllDayDataFormula (DailyAllDayCondition)

  # After inserting four rows, the 11:45 PM row is 66 and row 67 is the first blank.
  StyleTbd $daily $daily.Range('A67:F67') $daily.Range('A68:F68') $daily.Range('A69:F83')
  $daily.Range('A67').Formula2 = TbdTitleFormula (DailyTbdCondition)
  $daily.Range('A69').Formula2 = TbdDataFormula (DailyTbdCondition)

  # Weekly: insert an all-day section above the timed week grid.
  Retry { $weekly.Rows('7:10').Insert() } 'insert weekly all-day rows' | Out-Null
  StyleTopAllDay $weekly $weekly.Range('A7:C7') $weekly.Range('A8:C8') $weekly.Range('A9:C10')
  $weekly.Range('A7').Formula2 = AllDayTitleFormula (WeeklyAllDayCondition)
  $weekly.Range('A9').Formula2 = AllDayDataFormula (WeeklyAllDayCondition)

  # After inserting four rows, the timed week grid ends at row 27; row 28 is first blank.
  StyleTbd $weekly $weekly.Range('A28:F28') $weekly.Range('A29:F29') $weekly.Range('A30:F44')
  $weekly.Range('A28').Formula2 = TbdTitleFormula (WeeklyTbdCondition)
  $weekly.Range('A30').Formula2 = TbdDataFormula (WeeklyTbdCondition)

  Retry { $excel.CalculateFullRebuild() } 'calculate workbook' | Out-Null

  Write-Output ('Daily all-day title=' + $daily.Range('A6').Text)
  Write-Output ('Daily TBD title=' + $daily.Range('A67').Text)
  Write-Output ('Weekly all-day title=' + $weekly.Range('A7').Text)
  Write-Output ('Weekly TBD title=' + $weekly.Range('A28').Text)
  Write-Output ('Daily CF count=' + $daily.Range('B11:K111').FormatConditions.Count)
  Write-Output ('Weekly CF count=' + $weekly.Range('B11:H27').FormatConditions.Count)

  Retry { $wb.Save() } 'save workbook' | Out-Null
  Retry { $wb.Close($true) } 'close workbook' | Out-Null
  $wb = Retry { $excel.Workbooks.Open($Output) } 'reopen workbook'
  Write-Output 'Reopen OK'
  Retry { $wb.Close($false) } 'close reopened workbook' | Out-Null
  $wb = $null
  Write-Output $Output
}
finally {
  if ($wb) { try { $wb.Close($false) } catch {} }
  $excel.Quit()
  [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
}
