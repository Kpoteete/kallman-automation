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

function TimeColumnFormula($Row) {
  $prev = "A$($Row - 1)"
  return "=IFERROR(IF($prev=""TBD"","""",IF($prev="""","""",IF($prev+TIME(0,`$D`$2,0)>TIME(23,45,0),""TBD"",$prev+TIME(0,`$D`$2,0)))),"""")"
}

function TimedEventFormula($Row, $ColLetter) {
  $t = "`$A$Row"
  $end = "`$A$Row+TIME(0,`$D`$2,0)"
  $slot = "$ColLetter`$6"
  $cond = "IFERROR(ROUND('Data Input'!`$E`$4:`$E`$484,10)<ROUND($end,10),FALSE)*IFERROR(ROUND('Data Input'!`$G`$4:`$G`$484,10)>ROUND($t,10),FALSE)*('Data Input'!`$A`$4:`$A`$484=`$C`$2)*('Data Input'!`$D`$4:`$D`$484=`$B`$2)*('Data Input'!`$S`$4:`$S`$484=$slot)*((`$E`$2="""")+('Data Input'!`$B`$4:`$B`$484=`$E`$2))*((`$F`$2="""")+('Data Input'!`$J`$4:`$J`$484=`$F`$2))*((`$G`$2="""")+('Data Input'!`$I`$4:`$I`$484=`$G`$2))*((`$H`$2="""")+('Data Input'!`$P`$4:`$P`$484=`$H`$2))"
  return "IFERROR(LET(r,MATCH(1,$cond,0),ev,INDEX('Data Input'!`$H`$4:`$H`$484,r),owner,INDEX('Data Input'!`$B`$4:`$B`$484,r),st,INDEX('Data Input'!`$E`$4:`$E`$484,r),en,INDEX('Data Input'!`$G`$4:`$G`$484,r),loc,INDEX('Data Input'!`$I`$4:`$I`$484,r),cls,INDEX('Data Input'!`$P`$4:`$P`$484,r),cat,INDEX('Data Input'!`$J`$4:`$J`$484,r),req,INDEX('Data Input'!`$M`$4:`$M`$484,r),IF((ROUND(st,10)>=ROUND($t,10))*(ROUND(st,10)<ROUND($end,10)),ev&CHAR(10)&CHAR(10)&IF(owner="""","""",owner&CHAR(10))&TEXT(st,""h:mm AM/PM"")&"" - ""&TEXT(en,""h:mm AM/PM"")&CHAR(10)&loc&CHAR(10)&CHAR(10)&IF((cls="""")*(cat=""""),"""",IF(cls="""",cat,IF(cat="""",cls,cls&CHAR(10)&cat)))&IF(req="""","""",CHAR(10)&""Required: ""&req)&CHAR(10),IF(ROUND(en,10)<=ROUND($end,10),""END"",""CONTINUE""))),"""")"
}

function TbdEventFormula($ColLetter) {
  $slot = "$ColLetter`$6"
  $txt = "'Data Input'!`$D`$4:`$D`$484&"" ""&'Data Input'!`$E`$4:`$E`$484&"" ""&'Data Input'!`$F`$4:`$F`$484&"" ""&'Data Input'!`$G`$4:`$G`$484"
  $when = "IFERROR(TEXT('Data Input'!`$D`$4:`$D`$484,""m/d/yyyy""),'Data Input'!`$D`$4:`$D`$484)&"" ""&IFERROR(TEXT('Data Input'!`$E`$4:`$E`$484,""h:mm AM/PM""),'Data Input'!`$E`$4:`$E`$484)&"" - ""&IFERROR(TEXT('Data Input'!`$F`$4:`$F`$484,""m/d/yyyy""),'Data Input'!`$F`$4:`$F`$484)&"" ""&IFERROR(TEXT('Data Input'!`$G`$4:`$G`$484,""h:mm AM/PM""),'Data Input'!`$G`$4:`$G`$484)"
  $cond = "ISNUMBER(SEARCH(""TBD"",UPPER($txt)))*((IFERROR('Data Input'!`$D`$4:`$D`$484=`$B`$2,FALSE)+IFERROR('Data Input'!`$F`$4:`$F`$484=`$B`$2,FALSE))>0)*('Data Input'!`$A`$4:`$A`$484=`$C`$2)*((`$E`$2="""")+('Data Input'!`$B`$4:`$B`$484=`$E`$2))*((`$F`$2="""")+('Data Input'!`$J`$4:`$J`$484=`$F`$2))*((`$G`$2="""")+('Data Input'!`$I`$4:`$I`$484=`$G`$2))*((`$H`$2="""")+('Data Input'!`$P`$4:`$P`$484=`$H`$2))"
  return "IFERROR(LET(k,$slot,r,INDEX(FILTER(ROW('Data Input'!`$H`$4:`$H`$484)-ROW('Data Input'!`$H`$4)+1,$cond),k),ev,INDEX('Data Input'!`$H`$4:`$H`$484,r),owner,INDEX('Data Input'!`$B`$4:`$B`$484,r),poc,INDEX('Data Input'!`$C`$4:`$C`$484,r),cat,INDEX('Data Input'!`$J`$4:`$J`$484,r),loc,INDEX('Data Input'!`$I`$4:`$I`$484,r),status,INDEX('Data Input'!`$R`$4:`$R`$484,r),whenTxt,INDEX($when,r),ev&CHAR(10)&""TBD""&CHAR(10)&whenTxt&CHAR(10)&IF(owner="""","""",owner)&IF(poc="""","""","" / ""&poc)&CHAR(10)&cat&IF(loc="""","""",CHAR(10)&loc)&IF(status="""","""",CHAR(10)&status)),"""")"
}

function DailyCellFormula($Row, $ColLetter) {
  return "=IF(`$A$Row="""","""",IF(`$A$Row=""TBD"",$(TbdEventFormula $ColLetter),$(TimedEventFormula $Row $ColLetter)))"
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$excel.EnableEvents = $false
$wb = $null
try {
  $wb = Retry { $excel.Workbooks.Open($Output) } 'open workbook'
  $daily = Retry { $wb.Worksheets.Item('Daily Schedule') } 'daily sheet'

  foreach ($row in 8..107) {
    $formula = TimeColumnFormula $row
    Retry { $daily.Range("A$row").Formula2 = $formula } "time formula A$row" | Out-Null
  }

  $cols = @('B','C','D','E','F','G','H','I','J','K')
  foreach ($row in 7..107) {
    foreach ($col in $cols) {
      $formula = DailyCellFormula $row $col
      Retry { $daily.Range("$col$row").Formula2 = $formula } "daily formula $col$row" | Out-Null
    }
  }

  Retry { $excel.CalculateFullRebuild() } 'calculate workbook' | Out-Null

  Write-Output ('Daily CF count=' + $daily.Range('B7:K107').FormatConditions.Count)
  Write-Output ('Default date=' + $daily.Range('B2').Text + ' A62=' + $daily.Range('A62').Text + ' A63=' + $daily.Range('A63').Text + ' A64=' + $daily.Range('A64').Text + ' B63=' + (($daily.Range('B63').Text -replace "`r?`n",' | ')))
  $originalDate = $daily.Range('B2').Value2
  Retry { $daily.Range('B2').Value2 = 46225 } 'temp set 7/22/2026' | Out-Null
  Retry { $excel.CalculateFullRebuild() } 'temp calculate' | Out-Null
  Write-Output ('Temp 7/22 A63=' + $daily.Range('A63').Text + ' B63=' + (($daily.Range('B63').Text -replace "`r?`n",' | ').Substring(0,[Math]::Min(180,($daily.Range('B63').Text -replace "`r?`n",' | ').Length))) + ' C63=' + (($daily.Range('C63').Text -replace "`r?`n",' | ').Substring(0,[Math]::Min(180,($daily.Range('C63').Text -replace "`r?`n",' | ').Length))))
  Retry { $daily.Range('B2').Value2 = $originalDate } 'restore original date' | Out-Null
  Retry { $excel.CalculateFullRebuild() } 'restore calculate' | Out-Null

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
