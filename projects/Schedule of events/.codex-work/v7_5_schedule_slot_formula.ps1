$Project = 'C:\kwi-automations\projects\Schedule of events'
$Source = Join-Path $Project 'Schedule of Events V7.4 - Codex Print Ready.xlsx'
$Output = Join-Path $Project 'Schedule of Events V7.5 - Codex Print Ready.xlsx'
Copy-Item -LiteralPath $Source -Destination $Output -Force

function Retry($ScriptBlock, $Label) {
  for ($i = 0; $i -lt 10; $i++) {
    try { return & $ScriptBlock } catch { Start-Sleep -Milliseconds (500 + 250 * $i) }
  }
  throw "Failed: $Label"
}

function SlotFormula($Row) {
  return "=IF(OR(A$Row="""",D$Row="""",E$Row="""",G$Row="""",NOT(ISNUMBER(E$Row)),NOT(ISNUMBER(G$Row))),"""",LET(curRow,ROW(),shows,`$A`$4:`$A`$484,dates,`$D`$4:`$D`$484,starts,`$E`$4:`$E`$484,ends,`$G`$4:`$G`$484,rows,ROW(`$A`$4:`$A`$484),base,SUMPRODUCT((rows<curRow)*(shows=A$Row)*(dates=D$Row)*IFERROR(starts<E$Row,FALSE)*IFERROR(ends>E$Row,FALSE)),same,SUMPRODUCT((rows<=curRow)*(shows=A$Row)*(dates=D$Row)*IFERROR(starts=E$Row,FALSE)),IF(base>0,SUMPRODUCT((rows<curRow)*(shows=A$Row)*(dates=D$Row)*IFERROR(starts<E$Row,FALSE)*IFERROR(ends>=E$Row,FALSE))+same,same)))"
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$excel.EnableEvents = $false
$wb = $null
try {
  $wb = Retry { $excel.Workbooks.Open($Output) } 'open workbook'
  $data = Retry { $wb.Worksheets.Item('Data Input') } 'data sheet'
  $daily = Retry { $wb.Worksheets.Item('Daily Schedule') } 'daily sheet'

  foreach ($row in 4..484) {
    $formula = SlotFormula $row
    Retry { $data.Range("S$row").Formula2 = $formula } "slot formula S$row" | Out-Null
  }

  Retry { $excel.CalculateFullRebuild() } 'calculate workbook' | Out-Null

  foreach ($row in 13..18) {
    $name = [string]$data.Cells.Item($row, 8).Text
    $start = [string]$data.Cells.Item($row, 5).Text
    $end = [string]$data.Cells.Item($row, 7).Text
    $slot = [string]$data.Cells.Item($row, 19).Value2
    Write-Output ("Data row {0}: slot={1}; {2}-{3}; {4}" -f $row, $slot, $start, $end, $name)
  }

  $originalDate = $daily.Range('B2').Value2
  Retry { $daily.Range('B2').Value2 = 46224 } 'set daily 7/21' | Out-Null
  Retry { $excel.CalculateFullRebuild() } 'calculate daily 7/21' | Out-Null
  foreach ($r in 9..12) {
    Write-Output ("Daily row {0} A={1} B=[{2}] C=[{3}] D=[{4}]" -f $r, $daily.Range("A$r").Text, (($daily.Range("B$r").Text -replace "`r?`n",' | ')), (($daily.Range("C$r").Text -replace "`r?`n",' | ')), (($daily.Range("D$r").Text -replace "`r?`n",' | ')))
  }
  Retry { $daily.Range('B2').Value2 = $originalDate } 'restore daily date' | Out-Null
  Retry { $excel.CalculateFullRebuild() } 'restore calculate' | Out-Null

  Write-Output ('Daily CF count=' + $daily.Range('B7:K107').FormatConditions.Count)
  Write-Output ('Daily print area=' + $daily.PageSetup.PrintArea)

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
