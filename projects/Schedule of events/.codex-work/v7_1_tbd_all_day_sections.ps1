$Project = 'C:\kwi-automations\projects\Schedule of events'
$Source = Join-Path $Project 'Schedule of Events V7.xlsx'
$Output = Join-Path $Project 'Schedule of Events V7.1 - Codex.xlsx'
Copy-Item -LiteralPath $Source -Destination $Output -Force

function Retry($ScriptBlock, $Label) {
  for ($i = 0; $i -lt 10; $i++) {
    try { return & $ScriptBlock } catch { Start-Sleep -Milliseconds (500 + 250 * $i) }
  }
  throw "Failed: $Label"
}

function WeeklyTimedFormula($Row, $ColLetter) {
  $start = "`$A$Row"
  $end = "`$A$Row+TIME(1,0,0)"
  $day = "$ColLetter`$6"
  $cond = "('Data Input'!`$A`$4:`$A`$484=`$C`$3)*('Data Input'!`$D`$4:`$D`$484=$day)*IFERROR(ROUND('Data Input'!`$E`$4:`$E`$484,10)>=ROUND($start,10),FALSE)*IFERROR(ROUND('Data Input'!`$E`$4:`$E`$484,10)<ROUND($end,10),FALSE)*IFERROR(ISNUMBER('Data Input'!`$G`$4:`$G`$484),FALSE)*((`$D`$3="""")+('Data Input'!`$B`$4:`$B`$484=`$D`$3))*((`$E`$3="""")+('Data Input'!`$J`$4:`$J`$484=`$E`$3))*((`$F`$3="""")+('Data Input'!`$I`$4:`$I`$484=`$F`$3))*((`$G`$3="""")+('Data Input'!`$P`$4:`$P`$484=`$G`$3))"
  return "=IFERROR(TEXTJOIN(CHAR(10)&CHAR(10),TRUE,FILTER('Data Input'!`$H`$4:`$H`$484&CHAR(10)&TEXT('Data Input'!`$E`$4:`$E`$484,""h:mm AM/PM"")&"" - ""&TEXT('Data Input'!`$G`$4:`$G`$484,""h:mm AM/PM""),$cond)),"""")"
}

function DailySpecialFormula() {
  $whenText = "IFERROR(TEXT('Data Input'!`$D`$4:`$D`$484,""m/d/yyyy""),'Data Input'!`$D`$4:`$D`$484)&"" ""&IFERROR(TEXT('Data Input'!`$E`$4:`$E`$484,""h:mm AM/PM""),'Data Input'!`$E`$4:`$E`$484)&"" - ""&IFERROR(TEXT('Data Input'!`$F`$4:`$F`$484,""m/d/yyyy""),'Data Input'!`$F`$4:`$F`$484)&"" ""&IFERROR(TEXT('Data Input'!`$G`$4:`$G`$484,""h:mm AM/PM""),'Data Input'!`$G`$4:`$G`$484)"
  $dg = "'Data Input'!`$D`$4:`$D`$484&"" ""&'Data Input'!`$E`$4:`$E`$484&"" ""&'Data Input'!`$F`$4:`$F`$484&"" ""&'Data Input'!`$G`$4:`$G`$484"
  $isSpecial = "((ISNUMBER(SEARCH(""TBD"",$dg))+ISNUMBER(SEARCH(""ALL DAY"",UPPER($dg))))>0)"
  $isRelevantDay = "((IFERROR('Data Input'!`$D`$4:`$D`$484=`$B`$2,FALSE)+IFERROR('Data Input'!`$F`$4:`$F`$484=`$B`$2,FALSE)+ISNUMBER(SEARCH(""TBD"",$dg)))>0)"
  $filters = "('Data Input'!`$A`$4:`$A`$484=`$C`$2)*((`$E`$2="""")+('Data Input'!`$B`$4:`$B`$484=`$E`$2))*((`$F`$2="""")+('Data Input'!`$J`$4:`$J`$484=`$F`$2))*((`$G`$2="""")+('Data Input'!`$I`$4:`$I`$484=`$G`$2))*((`$H`$2="""")+('Data Input'!`$P`$4:`$P`$484=`$H`$2))"
  $type = "IF(ISNUMBER(SEARCH(""ALL DAY"",UPPER($dg))),""All Day"",""TBD"")"
  $owner = "'Data Input'!`$B`$4:`$B`$484&IF('Data Input'!`$C`$4:`$C`$484="""","""","" / ""&'Data Input'!`$C`$4:`$C`$484)"
  return "=IFERROR(FILTER(CHOOSE({1,2,3,4,5,6},$type,$whenText,'Data Input'!`$H`$4:`$H`$484,'Data Input'!`$I`$4:`$I`$484,$owner,'Data Input'!`$R`$4:`$R`$484),$isSpecial*$isRelevantDay*$filters),"""")"
}

function WeeklySpecialFormula() {
  $whenText = "IFERROR(TEXT('Data Input'!`$D`$4:`$D`$484,""m/d/yyyy""),'Data Input'!`$D`$4:`$D`$484)&"" ""&IFERROR(TEXT('Data Input'!`$E`$4:`$E`$484,""h:mm AM/PM""),'Data Input'!`$E`$4:`$E`$484)&"" - ""&IFERROR(TEXT('Data Input'!`$F`$4:`$F`$484,""m/d/yyyy""),'Data Input'!`$F`$4:`$F`$484)&"" ""&IFERROR(TEXT('Data Input'!`$G`$4:`$G`$484,""h:mm AM/PM""),'Data Input'!`$G`$4:`$G`$484)"
  $dg = "'Data Input'!`$D`$4:`$D`$484&"" ""&'Data Input'!`$E`$4:`$E`$484&"" ""&'Data Input'!`$F`$4:`$F`$484&"" ""&'Data Input'!`$G`$4:`$G`$484"
  $isSpecial = "((ISNUMBER(SEARCH(""TBD"",$dg))+ISNUMBER(SEARCH(""ALL DAY"",UPPER($dg))))>0)"
  $isRelevantWeek = "((IFERROR(('Data Input'!`$D`$4:`$D`$484>=`$B`$3)*('Data Input'!`$D`$4:`$D`$484<=`$B`$3+6),FALSE)+IFERROR(('Data Input'!`$F`$4:`$F`$484>=`$B`$3)*('Data Input'!`$F`$4:`$F`$484<=`$B`$3+6),FALSE)+ISNUMBER(SEARCH(""TBD"",$dg)))>0)"
  $filters = "('Data Input'!`$A`$4:`$A`$484=`$C`$3)*((`$D`$3="""")+('Data Input'!`$B`$4:`$B`$484=`$D`$3))*((`$E`$3="""")+('Data Input'!`$J`$4:`$J`$484=`$E`$3))*((`$F`$3="""")+('Data Input'!`$I`$4:`$I`$484=`$F`$3))*((`$G`$3="""")+('Data Input'!`$P`$4:`$P`$484=`$G`$3))"
  $type = "IF(ISNUMBER(SEARCH(""ALL DAY"",UPPER($dg))),""All Day"",""TBD"")"
  $owner = "'Data Input'!`$B`$4:`$B`$484&IF('Data Input'!`$C`$4:`$C`$484="""","""","" / ""&'Data Input'!`$C`$4:`$C`$484)"
  return "=IFERROR(FILTER(CHOOSE({1,2,3,4,5,6},$type,$whenText,'Data Input'!`$H`$4:`$H`$484,'Data Input'!`$I`$4:`$I`$484,$owner,'Data Input'!`$R`$4:`$R`$484),$isSpecial*$isRelevantWeek*$filters),"""")"
}

function StyleSpecialSection($Sheet, $TitleRange, $HeaderRange, $BodyRange) {
  Retry { $TitleRange.Merge() } 'merge special title' | Out-Null
  Retry { $TitleRange.Value2 = 'All-Day / TBD Events' } 'special title text' | Out-Null
  Retry { $TitleRange.Font.Bold = $true } 'special title bold' | Out-Null
  Retry { $TitleRange.Font.Color = 16777215 } 'special title font color' | Out-Null
  Retry { $TitleRange.Interior.Color = 8421504 } 'special title fill' | Out-Null
  Retry { $HeaderRange.Value2 = @('Type','Date / Time','Event','Location','Organizer / POC','Status') } 'special headers' | Out-Null
  Retry { $HeaderRange.Font.Bold = $true } 'special header bold' | Out-Null
  Retry { $HeaderRange.Interior.Color = 14277081 } 'special header fill' | Out-Null
  Retry { $HeaderRange.Borders.LineStyle = 1 } 'special header borders' | Out-Null
  Retry { $BodyRange.Borders.LineStyle = 1 } 'special body borders' | Out-Null
  Retry { $BodyRange.WrapText = $true } 'special body wrap' | Out-Null
  Retry { $BodyRange.VerticalAlignment = -4160 } 'special body valign' | Out-Null
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

  # Weekly: remove category from displayed event text, show only starting hour, remove all conditional formatting.
  $weeklyCols = @('B','C','D','E','F','G','H')
  foreach ($row in 7..23) {
    foreach ($col in $weeklyCols) {
      $formula = WeeklyTimedFormula $row $col
      Retry { $weekly.Range("$col$row").Formula2 = $formula } "weekly formula $col$row" | Out-Null
    }
  }
  Retry { $weekly.Range('B7:H23').FormatConditions.Delete() } 'remove weekly conditional formatting' | Out-Null

  # Daily: do not touch existing grid conditional formatting; add special-event section below the timed grid.
  StyleSpecialSection $daily $daily.Range('A110:F110') $daily.Range('A111:F111') $daily.Range('A112:F126')
  $daily.Range('A112').Formula2 = DailySpecialFormula
  $daily.Rows('110:126').RowHeight = 30
  $daily.Rows('110:111').RowHeight = 22
  $daily.Range('A112:F126').Font.Size = 9

  # Weekly: add matching special-event section below the week grid.
  StyleSpecialSection $weekly $weekly.Range('A25:F25') $weekly.Range('A26:F26') $weekly.Range('A27:F41')
  $weekly.Range('A27').Formula2 = WeeklySpecialFormula
  $weekly.Rows('25:41').RowHeight = 30
  $weekly.Rows('25:26').RowHeight = 22
  $weekly.Range('A27:F41').Font.Size = 9

  Retry { $excel.CalculateFullRebuild() } 'calculate workbook' | Out-Null

  foreach ($addr in @('C14','D16','F17')) {
    $cell = $weekly.Range($addr)
    Write-Output ("Weekly {0}: formula={1}; text={2}" -f $addr, $cell.HasFormula, (($cell.Text -replace "`r?`n",' | ').Substring(0,[Math]::Min(140,($cell.Text -replace "`r?`n",' | ').Length))))
  }
  Write-Output ('Weekly CF count B7:H23=' + $weekly.Range('B7:H23').FormatConditions.Count)
  Write-Output ('Daily CF count B7:K107=' + $daily.Range('B7:K107').FormatConditions.Count)
  Write-Output ('Daily special A112=' + (($daily.Range('A112').Text -replace "`r?`n",' | ')))
  Write-Output ('Weekly special A27=' + (($weekly.Range('A27').Text -replace "`r?`n",' | ')))

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
