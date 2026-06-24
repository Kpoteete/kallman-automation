$Project = 'C:\kwi-automations\projects\Schedule of events'
$Source = Join-Path $Project 'Schedule of events v - formula views restored.xlsx'
$Output = Join-Path $Project 'Schedule of events v - formula views restored continuation marks.xlsx'
Copy-Item -LiteralPath $Source -Destination $Output -Force

function Retry($ScriptBlock, $Label) {
  for ($i = 0; $i -lt 10; $i++) {
    try { return & $ScriptBlock } catch { Start-Sleep -Milliseconds (500 + 250 * $i) }
  }
  throw "Failed: $Label"
}

function DailyFormula($Row, $ColLetter) {
  $t = "`$A$Row"
  $end = "`$A$Row+TIME(0,`$D`$2,0)"
  $slot = "$ColLetter`$6"
  $cond = "IFERROR(ROUND('Data Input'!`$D`$4:`$D`$484,10)<ROUND($end,10),FALSE)*IFERROR(ROUND('Data Input'!`$F`$4:`$F`$484,10)>ROUND($t,10),FALSE)*('Data Input'!`$A`$4:`$A`$484=`$C`$2)*('Data Input'!`$C`$4:`$C`$484=`$B`$2)*('Data Input'!`$R`$4:`$R`$484=$slot)*((`$E`$2="""")+('Data Input'!`$B`$4:`$B`$484=`$E`$2))*((`$F`$2="""")+('Data Input'!`$I`$4:`$I`$484=`$F`$2))*((`$G`$2="""")+('Data Input'!`$J`$4:`$J`$484=`$G`$2))*((`$H`$2="""")+('Data Input'!`$K`$4:`$K`$484=`$H`$2))"
  $quoteMarker = '""""'
  return "=IF($t="""","""",IFERROR(LET(r,MATCH(1,$cond,0),ev,INDEX('Data Input'!`$G`$4:`$G`$484,r),owner,INDEX('Data Input'!`$B`$4:`$B`$484,r),st,INDEX('Data Input'!`$D`$4:`$D`$484,r),en,INDEX('Data Input'!`$F`$4:`$F`$484,r),loc,INDEX('Data Input'!`$J`$4:`$J`$484,r),cls,INDEX('Data Input'!`$K`$4:`$K`$484,r),cat,INDEX('Data Input'!`$I`$4:`$I`$484,r),req,INDEX('Data Input'!`$L`$4:`$L`$484,r),IF((ROUND(st,10)>=ROUND($t,10))*(ROUND(st,10)<ROUND($end,10)),ev&CHAR(10)&CHAR(10)&IF(owner="""","""",owner&CHAR(10))&TEXT(st,""h:mm AM/PM"")&"" - ""&TEXT(en,""h:mm AM/PM"")&CHAR(10)&loc&CHAR(10)&CHAR(10)&IF((cls="""")*(cat=""""),"""",IF(cls="""",cat,IF(cat="""",cls,cls&"" | ""&cat)))&IF(req="""","""",CHAR(10)&""Required: ""&req)&CHAR(10),IF(ROUND(en,10)<=ROUND($end,10),""."",$quoteMarker))),""""))"
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$excel.EnableEvents = $false
$wb = $null
try {
  $wb = Retry { $excel.Workbooks.Open($Output) } 'open workbook'
  $daily = Retry { $wb.Worksheets.Item('Daily Schedule') } 'daily sheet'

  $dailyCols = @('B','C','D','E','F','G','H','I','J','K')
  foreach ($row in 7..107) {
    foreach ($col in $dailyCols) {
      $formula = DailyFormula $row $col
      Retry { $daily.Range("$col$row").Formula2 = $formula } "daily formula $col$row" | Out-Null
    }
  }

  Retry { $excel.CalculateFullRebuild() } 'calculate' | Out-Null
  foreach ($addr in @('B7','B8','B9','B10','B11','B17','B18','B19','D19','D20','D21','D22')) {
    $cell = $daily.Range($addr)
    Write-Output ("Daily {0}: [{1}]" -f $addr, (($cell.Text -replace "`r?`n",' | ').Substring(0,[Math]::Min(120,($cell.Text -replace "`r?`n",' | ').Length))))
  }

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
