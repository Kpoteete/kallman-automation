$Project = 'C:\kwi-automations\projects\Schedule of events'
$Source = Join-Path $Project 'Schedule of events v - Codex weekly calendar view.xlsx'
$Output = Join-Path $Project 'Schedule of events v - formula views restored.xlsx'
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
  return "=IF($t="""","""",IFERROR(LET(r,MATCH(1,$cond,0),ev,INDEX('Data Input'!`$G`$4:`$G`$484,r),owner,INDEX('Data Input'!`$B`$4:`$B`$484,r),st,INDEX('Data Input'!`$D`$4:`$D`$484,r),en,INDEX('Data Input'!`$F`$4:`$F`$484,r),loc,INDEX('Data Input'!`$J`$4:`$J`$484,r),cls,INDEX('Data Input'!`$K`$4:`$K`$484,r),cat,INDEX('Data Input'!`$I`$4:`$I`$484,r),req,INDEX('Data Input'!`$L`$4:`$L`$484,r),IF((ROUND(st,10)>=ROUND($t,10))*(ROUND(st,10)<ROUND($end,10)),ev&CHAR(10)&CHAR(10)&IF(owner="""","""",owner&CHAR(10))&TEXT(st,""h:mm AM/PM"")&"" - ""&TEXT(en,""h:mm AM/PM"")&CHAR(10)&loc&CHAR(10)&CHAR(10)&IF((cls="""")*(cat=""""),"""",IF(cls="""",cat,IF(cat="""",cls,cls&"" | ""&cat)))&IF(req="""","""",CHAR(10)&""Required: ""&req)&CHAR(10),IF(ROUND(en,10)<=ROUND($end,10),""."",""""))),""""))"
}

function WeeklyFormula($Row, $ColLetter) {
  $start = "`$A$Row"
  $end = "`$A$Row+TIME(1,0,0)"
  $day = "$ColLetter`$6"
  $cond = "('Data Input'!`$A`$4:`$A`$484=`$C`$3)*('Data Input'!`$C`$4:`$C`$484=$day)*IFERROR(ROUND('Data Input'!`$D`$4:`$D`$484,10)<ROUND($end,10),FALSE)*IFERROR(ROUND('Data Input'!`$F`$4:`$F`$484,10)>ROUND($start,10),FALSE)*((`$D`$3="""")+('Data Input'!`$B`$4:`$B`$484=`$D`$3))*((`$E`$3="""")+('Data Input'!`$I`$4:`$I`$484=`$E`$3))*((`$F`$3="""")+('Data Input'!`$J`$4:`$J`$484=`$F`$3))*((`$G`$3="""")+('Data Input'!`$K`$4:`$K`$484=`$G`$3))"
  return "=IFERROR(TEXTJOIN(CHAR(10)&CHAR(10),TRUE,FILTER('Data Input'!`$G`$4:`$G`$484&CHAR(10)&'Data Input'!`$I`$4:`$I`$484&CHAR(10)&TEXT('Data Input'!`$D`$4:`$D`$484,""h:mm AM/PM"")&"" - ""&TEXT('Data Input'!`$F`$4:`$F`$484,""h:mm AM/PM""),$cond)),"""")"
}

function HexToExcelColor($Hex) {
  $r = [Convert]::ToInt32($Hex.Substring(0,2),16)
  $g = [Convert]::ToInt32($Hex.Substring(2,2),16)
  $b = [Convert]::ToInt32($Hex.Substring(4,2),16)
  return $r + ($g * 256) + ($b * 65536)
}

function AddCategoryFormatting($Range) {
  $colors = [ordered]@{
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
  Retry { $Range.FormatConditions.Delete() } 'clear cf' | Out-Null
  foreach ($category in $colors.Keys) {
    $escaped = $category.Replace('"','""')
    $formula = "=ISNUMBER(SEARCH(""$escaped"",B7))"
    $fc = Retry { $Range.FormatConditions.Add(2, [Type]::Missing, $formula) } "cf $category"
    $fc.Interior.Pattern = 1
    $fc.Interior.Color = HexToExcelColor $colors[$category]
  }
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

  $dailyCols = @('B','C','D','E','F','G','H','I','J','K')
  foreach ($row in 7..107) {
    for ($i = 0; $i -lt $dailyCols.Count; $i++) {
      $col = $dailyCols[$i]
      $formula = DailyFormula $row $col
      Retry { $daily.Range("$col$row").Formula2 = $formula } "daily formula $col$row" | Out-Null
    }
  }

  $weeklyCols = @('B','C','D','E','F','G','H')
  foreach ($row in 7..23) {
    foreach ($col in $weeklyCols) {
      $formula = WeeklyFormula $row $col
      Retry { $weekly.Range("$col$row").Formula2 = $formula } "weekly formula $col$row" | Out-Null
    }
  }

  AddCategoryFormatting $daily.Range('B7:K107')
  AddCategoryFormatting $weekly.Range('B7:H23')

  Retry { $excel.CalculateFullRebuild() } 'calculate' | Out-Null

  foreach ($addr in @('B7','B17','C17','D19')) {
    $cell = $daily.Range($addr)
    Write-Output ("Daily {0}: formula={1}; text={2}" -f $addr, $cell.HasFormula, (($cell.Text -replace "`r?`n",' | ').Substring(0,[Math]::Min(120,($cell.Text -replace "`r?`n",' | ').Length))))
  }
  foreach ($addr in @('C11','C14','C15','D14','D16')) {
    $cell = $weekly.Range($addr)
    Write-Output ("Weekly {0}: formula={1}; text={2}" -f $addr, $cell.HasFormula, (($cell.Text -replace "`r?`n",' | ').Substring(0,[Math]::Min(120,($cell.Text -replace "`r?`n",' | ').Length))))
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
