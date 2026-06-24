$Project = 'C:\kwi-automations\projects\Schedule of events'
$Source = Join-Path $Project 'Schedule of events v - Codex weekly calendar view.xlsx'
$Output = Join-Path $Project 'Schedule of events v - formula test.xlsx'
Copy-Item -LiteralPath $Source -Destination $Output -Force

function Retry($ScriptBlock, $Label) {
  for ($i = 0; $i -lt 8; $i++) {
    try { return & $ScriptBlock } catch { Start-Sleep -Milliseconds (500 + 250 * $i) }
  }
  throw "Failed: $Label"
}

function DailyFormula($Row, $ColLetter) {
  $t = "`$A$Row"
  $end = "`$A$Row+TIME(0,`$D`$2,0)"
  $slot = "$ColLetter`$6"
  $cond = "IFERROR(ROUND('Data Input'!`$D`$4:`$D`$484,10)<ROUND($end,10),FALSE)*IFERROR(ROUND('Data Input'!`$F`$4:`$F`$484,10)>ROUND($t,10),FALSE)*('Data Input'!`$A`$4:`$A`$484=`$C`$2)*('Data Input'!`$C`$4:`$C`$484=`$B`$2)*('Data Input'!`$R`$4:`$R`$484=$slot)*((`$E`$2="""")+('Data Input'!`$B`$4:`$B`$484=`$E`$2))*((`$F`$2="""")+('Data Input'!`$I`$4:`$I`$484=`$F`$2))*((`$G`$2="""")+('Data Input'!`$J`$4:`$J`$484=`$G`$2))*((`$H`$2="""")+('Data Input'!`$K`$4:`$K`$484=`$H`$2))"
  return "=IF($t="""","""",IFERROR(LET(r,MATCH(1,$cond,0),ev,INDEX('Data Input'!`$G`$4:`$G`$484,r),owner,INDEX('Data Input'!`$B`$4:`$B`$484,r),st,INDEX('Data Input'!`$D`$4:`$D`$484,r),en,INDEX('Data Input'!`$F`$4:`$F`$484,r),loc,INDEX('Data Input'!`$J`$4:`$J`$484,r),cls,INDEX('Data Input'!`$K`$4:`$K`$484,r),cat,INDEX('Data Input'!`$I`$4:`$I`$484,r),req,INDEX('Data Input'!`$L`$4:`$L`$484,r),IF((ROUND(st,10)>=ROUND($t,10))*(ROUND(st,10)<ROUND($end,10)),ev&CHAR(10)&CHAR(10)&IF(owner="""","""",owner&CHAR(10))&TEXT(st,""h:mm AM/PM"")&"" - ""&TEXT(en,""h:mm AM/PM"")&CHAR(10)&loc&CHAR(10)&CHAR(10)&IF((cls="""")*(cat=""""),"""",IF(cls="""",cat,IF(cat="""",cls,cls&"" | ""&cat)))&IF(req="""","""",CHAR(10)&""Required: ""&req)&CHAR(10),IF(ROUND(en,10)<=ROUND($end,10),""."",""""))),""""))"
}

function WeeklyFormula($Row, $ColLetter) {
  $start = "`$A$Row"
  $end = "`$A$Row+TIME(1,0,0)"
  $day = "$ColLetter`$6"
  $cond = "('Data Input'!`$A`$4:`$A`$484=`$C`$3)*('Data Input'!`$C`$4:`$C`$484=$day)*IFERROR(ROUND('Data Input'!`$D`$4:`$D`$484,10)<ROUND($end,10),FALSE)*IFERROR(ROUND('Data Input'!`$F`$4:`$F`$484,10)>ROUND($start,10),FALSE)*((`$D`$3="""")+('Data Input'!`$B`$4:`$B`$484=`$D`$3))*((`$E`$3="""")+('Data Input'!`$I`$4:`$I`$484=`$E`$3))*((`$F`$3="""")+('Data Input'!`$J`$4:`$J`$484=`$F`$3))*((`$G`$3="""")+('Data Input'!`$K`$4:`$K`$484=`$G`$3))"
  return "=IFERROR(TEXTJOIN(CHAR(10)&CHAR(10),TRUE,FILTER('Data Input'!`$G`$4:`$G`$484&CHAR(10)&'Data Input'!`$I`$4:`$I`$484&CHAR(10)&TEXT('Data Input'!`$D`$4:`$D`$484,""h:mm AM/PM"")&"" - ""&TEXT('Data Input'!`$F`$4:`$F`$484,""h:mm AM/PM""),$cond)),"""")"
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$excel.EnableEvents = $false
$wb = $null
try {
  $wb = Retry { $excel.Workbooks.Open($Output) } 'open'
  $daily = Retry { $wb.Worksheets.Item('Daily Schedule') } 'daily'
  $weekly = Retry { $wb.Worksheets.Item('Weekly Calendar') } 'weekly'
  $daily.Range('B7').Formula2 = DailyFormula 7 'B'
  $weekly.Range('C14').Formula2 = WeeklyFormula 14 'C'
  Retry { $excel.CalculateFullRebuild() } 'calc' | Out-Null
  Write-Output ('B7=' + ($daily.Range('B7').Text -replace "`r?`n",' | '))
  Write-Output ('C14 formula=' + $weekly.Range('C14').HasFormula + ' text=' + (($weekly.Range('C14').Text -replace "`r?`n",' | ')))
  Retry { $wb.Save() } 'save' | Out-Null
  Retry { $wb.Close($true) } 'close' | Out-Null
  $wb = Retry { $excel.Workbooks.Open($Output) } 'reopen'
  Write-Output 'Reopen OK'
  Retry { $wb.Close($false) } 'close2' | Out-Null
  $wb = $null
}
finally {
  if ($wb) { try { $wb.Close($false) } catch {} }
  $excel.Quit()
  [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
}
