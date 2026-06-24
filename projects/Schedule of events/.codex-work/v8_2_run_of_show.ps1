$Project = 'C:\kwi-automations\projects\Schedule of events'
$Source = Join-Path $Project 'Schedule of Events V8.1 - Codex.xlsx'
$Output = Join-Path $Project 'Schedule of Events V8.2 - Codex Run of Show.xlsx'
Copy-Item -LiteralPath $Source -Destination $Output -Force

function Retry($ScriptBlock, $Label) {
  for ($i = 0; $i -lt 10; $i++) {
    try { return & $ScriptBlock } catch { Start-Sleep -Milliseconds (500 + 250 * $i) }
  }
  throw "Failed: $Label"
}

function DeleteSheetIfExists($Workbook, $Name) {
  foreach ($ws in @($Workbook.Worksheets)) {
    if ($ws.Name -eq $Name) {
      Retry { $ws.Delete() } "delete $Name" | Out-Null
      return
    }
  }
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$excel.EnableEvents = $false
$wb = $null
try {
  $wb = Retry { $excel.Workbooks.Open($Output) } 'open workbook'
  DeleteSheetIfExists $wb 'Run of Show'
  $ros = Retry { $wb.Worksheets.Add([Type]::Missing, $wb.Worksheets.Item($wb.Worksheets.Count)) } 'add run of show'
  $ros.Name = 'Run of Show'

  Retry { $ros.Range('A1:G1').Merge() } 'merge title' | Out-Null
  Retry { $ros.Range('A1').Value2 = 'Run of Show - Minute by Minute' } 'title' | Out-Null
  Retry { $ros.Range('A1').Font.Bold = $true } 'title bold' | Out-Null
  Retry { $ros.Range('A1').Font.Size = 18 } 'title size' | Out-Null
  Retry { $ros.Range('A1').Interior.Color = 8421504 } 'title fill' | Out-Null
  Retry { $ros.Range('A1').Font.Color = 16777215 } 'title color' | Out-Null

  $labels = @{
    'A3'='Location'; 'A4'='Event'; 'A6'='Selected Data Row';
    'A8'='Date'; 'C8'='Location'; 'E8'='Category';
    'A9'='Start'; 'C9'='End'; 'E9'='Status';
    'A10'='Owner'; 'C10'='KWI POC'; 'E10'='Class';
    'A12'='Required Attendees'; 'A14'='Speakers';
    'A16'='Internal Notes'
  }
  foreach ($addr in $labels.Keys) {
    Retry { $ros.Range($addr).Value2 = $labels[$addr] } "label $addr" | Out-Null
  }
  Retry { $ros.Range('A3:A16').Font.Bold = $true } 'left labels bold' | Out-Null
  Retry { $ros.Range('C8:C10').Font.Bold = $true } 'mid labels bold' | Out-Null
  Retry { $ros.Range('E8:E10').Font.Bold = $true } 'right labels bold' | Out-Null

  # Hidden helper dropdown lists.
  Retry { $ros.Range('AA1').Value2 = 'Location List' } 'location helper header' | Out-Null
  Retry { $ros.Range('AA2').Formula2 = '=SORT(UNIQUE(FILTER(''Data Input''!$I$4:$I$484,(''Data Input''!$I$4:$I$484<>"")*(''Data Input''!$I$4:$I$484<>0))))' } 'location helper formula' | Out-Null
  Retry { $ros.Range('AB1').Value2 = 'Event List' } 'event helper header' | Out-Null
  Retry { $ros.Range('AB2').Formula2 = '=SORT(UNIQUE(FILTER(''Data Input''!$H$4:$H$484,(''Data Input''!$H$4:$H$484<>"")*((''Run of Show''!$B$3="")+(''Data Input''!$I$4:$I$484=''Run of Show''!$B$3)))))' } 'event helper formula' | Out-Null
  Retry { $ros.Columns('AA:AB').Hidden = $true } 'hide helpers' | Out-Null

  Retry { $ros.Range('B3').Value2 = 'The FORUM' } 'default location' | Out-Null
  Retry { $ros.Range('B4').Formula2 = '=IFERROR(INDEX($AB$2:$AB$300,1),"")' } 'default event' | Out-Null
  Retry { $ros.Range('B6').Formula2 = '=IFERROR(MATCH(1,(''Data Input''!$H$4:$H$484=$B$4)*(($B$3="")+(''Data Input''!$I$4:$I$484=$B$3)),0)+3,"")' } 'selected row formula' | Out-Null

  Retry { $ros.Range('B3').Validation.Delete() } 'clear loc validation' | Out-Null
  Retry { $ros.Range('B3').Validation.Add(3, 1, 1, "='Run of Show'!`$AA`$2:`$AA`$200") } 'location validation' | Out-Null
  Retry { $ros.Range('B4').Validation.Delete() } 'clear event validation' | Out-Null
  Retry { $ros.Range('B4').Validation.Add(3, 1, 1, "='Run of Show'!`$AB`$2:`$AB`$300") } 'event validation' | Out-Null

  $summaryFormulas = @{
    'B8'='=IF($B$6="","",INDEX(''Data Input''!$D:$D,$B$6))';
    'D8'='=IF($B$6="","",INDEX(''Data Input''!$I:$I,$B$6))';
    'F8'='=IF($B$6="","",INDEX(''Data Input''!$J:$J,$B$6))';
    'B9'='=IF($B$6="","",INDEX(''Data Input''!$E:$E,$B$6))';
    'D9'='=IF($B$6="","",INDEX(''Data Input''!$G:$G,$B$6))';
    'F9'='=IF($B$6="","",INDEX(''Data Input''!$R:$R,$B$6))';
    'B10'='=IF($B$6="","",INDEX(''Data Input''!$B:$B,$B$6))';
    'D10'='=IF($B$6="","",INDEX(''Data Input''!$C:$C,$B$6))';
    'F10'='=IF($B$6="","",INDEX(''Data Input''!$P:$P,$B$6))';
    'B12'='=IF($B$6="","",INDEX(''Data Input''!$M:$M,$B$6))';
    'B14'='=IF($B$6="","",INDEX(''Data Input''!$N:$N,$B$6))';
    'B16'='=IF($B$6="","",INDEX(''Data Input''!$L:$L,$B$6))'
  }
  foreach ($addr in $summaryFormulas.Keys) {
    Retry { $ros.Range($addr).Formula2 = $summaryFormulas[$addr] } "summary $addr" | Out-Null
  }
  Retry { $ros.Range('B8').NumberFormat = 'm/d/yyyy' } 'date format' | Out-Null
  Retry { $ros.Range('B9:D9').NumberFormat = 'h:mm AM/PM' } 'time format' | Out-Null
  Retry { $ros.Range('B12:G17').Merge($false) } 'merge notes area maybe' | Out-Null
  Retry { $ros.Range('B12:G17').WrapText = $true } 'wrap summary notes' | Out-Null

  $headers = @('Clock Time','Minute','Segment / Action','Speaker / Lead','Cue / Transition','Tech / AV / Visual','Notes / Status')
  for ($i = 0; $i -lt $headers.Count; $i++) {
    Retry { $ros.Cells.Item(19, $i + 1).Value2 = $headers[$i] } "timeline header $i" | Out-Null
  }
  Retry { $ros.Range('A19:G19').Font.Bold = $true } 'timeline headers bold' | Out-Null
  Retry { $ros.Range('A19:G19').Interior.Color = 14277081 } 'timeline header fill' | Out-Null

  for ($row = 20; $row -le 260; $row++) {
    $offset = $row - 20
    $timeFormula = "=IF(OR(`$B`$9="""",`$D`$9="""",NOT(ISNUMBER(`$B`$9)),NOT(ISNUMBER(`$D`$9))),IF(ROW()=20,""Set a selected event with numeric start/end times to generate the timeline."",""""),IF($offset<=ROUND((`$D`$9-`$B`$9)*1440,0),`$B`$9+TIME(0,$offset,0),""""))"
    $minuteFormula = "=IF(ISNUMBER(A$row),TEXT(A$row-`$B`$9,""h:mm""),"""")"
    Retry { $ros.Range("A$row").Formula2 = $timeFormula } "time formula $row" | Out-Null
    Retry { $ros.Range("B$row").Formula2 = $minuteFormula } "minute formula $row" | Out-Null
  }
  Retry { $ros.Range('A20:A260').NumberFormat = 'h:mm AM/PM' } 'timeline time format' | Out-Null

  Retry { $ros.Range('A3:G17').Interior.Color = 15921906 } 'summary fill' | Out-Null
  Retry { $ros.Range('A3:G17').Borders.LineStyle = 1 } 'summary borders' | Out-Null
  Retry { $ros.Range('A19:G260').Borders.LineStyle = 1 } 'timeline borders' | Out-Null
  Retry { $ros.Range('A19:G260').VerticalAlignment = -4160 } 'timeline valign' | Out-Null
  Retry { $ros.Range('A19:G260').WrapText = $true } 'timeline wrap' | Out-Null
  Retry { $ros.Range('A:A').ColumnWidth = 13 } 'col A width' | Out-Null
  Retry { $ros.Range('B:B').ColumnWidth = 9 } 'col B width' | Out-Null
  Retry { $ros.Range('C:C').ColumnWidth = 24 } 'col C width' | Out-Null
  Retry { $ros.Range('D:D').ColumnWidth = 20 } 'col D width' | Out-Null
  Retry { $ros.Range('E:E').ColumnWidth = 24 } 'col E width' | Out-Null
  Retry { $ros.Range('F:F').ColumnWidth = 20 } 'col F width' | Out-Null
  Retry { $ros.Range('G:G').ColumnWidth = 28 } 'col G width' | Out-Null
  Retry { $ros.Rows('20:260').RowHeight = 30 } 'timeline row height' | Out-Null
  Retry { $ros.Rows('1:19').AutoFit() } 'top autofit' | Out-Null
  Retry { $ros.Activate() } 'activate ros' | Out-Null
  Retry { $ros.Range('A20').Select() } 'select timeline start' | Out-Null
  Retry { $excel.ActiveWindow.FreezePanes = $true } 'freeze panes' | Out-Null

  Retry { $ros.PageSetup.PrintArea = '$A$1:$G$120' } 'print area' | Out-Null
  Retry { $ros.PageSetup.PrintTitleRows = '$1:$19' } 'print title rows' | Out-Null
  Retry { $ros.PageSetup.Orientation = 2 } 'landscape' | Out-Null
  Retry { $ros.PageSetup.Zoom = $false } 'zoom false' | Out-Null
  Retry { $ros.PageSetup.FitToPagesWide = 1 } 'fit wide' | Out-Null
  try { $ros.PageSetup.FitToPagesTall = $false } catch {}
  Retry { $ros.PageSetup.LeftMargin = $ros.Application.InchesToPoints(0.25) } 'left margin' | Out-Null
  Retry { $ros.PageSetup.RightMargin = $ros.Application.InchesToPoints(0.25) } 'right margin' | Out-Null
  Retry { $ros.PageSetup.TopMargin = $ros.Application.InchesToPoints(0.35) } 'top margin' | Out-Null
  Retry { $ros.PageSetup.BottomMargin = $ros.Application.InchesToPoints(0.35) } 'bottom margin' | Out-Null
  Retry { $ros.PageSetup.CenterHorizontally = $true } 'center horizontal' | Out-Null

  Retry { $excel.CalculateFullRebuild() } 'calculate workbook' | Out-Null
  Write-Output ('Default location=' + $ros.Range('B3').Text)
  Write-Output ('Default event=' + $ros.Range('B4').Text)
  Write-Output ('Selected row=' + $ros.Range('B6').Text)
  Write-Output ('Timeline A20=' + $ros.Range('A20').Text + ' A21=' + $ros.Range('A21').Text + ' A22=' + $ros.Range('A22').Text)
  Write-Output ('Print area=' + $ros.PageSetup.PrintArea)

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
