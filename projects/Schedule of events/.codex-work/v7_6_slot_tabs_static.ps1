$Project = 'C:\kwi-automations\projects\Schedule of events'
$Source = Join-Path $Project 'Schedule of Events V7.5 - Codex Print Ready.xlsx'
$Output = Join-Path $Project 'Schedule of Events V7.6 - Codex.xlsx'
Copy-Item -LiteralPath $Source -Destination $Output -Force

function Retry($ScriptBlock, $Label) {
  for ($i = 0; $i -lt 10; $i++) {
    try { return & $ScriptBlock } catch { Start-Sleep -Milliseconds (500 + 250 * $i) }
  }
  throw "Failed: $Label"
}

function SlotFormula($Row) {
  return "=IF(OR(A$Row="""",D$Row="""",E$Row="""",G$Row="""",NOT(ISNUMBER(E$Row)),NOT(ISNUMBER(G$Row))),"""",LET(curRow,ROW(),shows,`$A`$4:`$A`$484,dates,`$D`$4:`$D`$484,starts,`$E`$4:`$E`$484,ends,`$G`$4:`$G`$484,rows,ROW(`$A`$4:`$A`$484),numeric,IFERROR(ISNUMBER(starts)*ISNUMBER(ends),FALSE),base,SUMPRODUCT((rows<curRow)*(shows=A$Row)*(dates=D$Row)*numeric*IFERROR(starts<E$Row,FALSE)*IFERROR(ends>E$Row,FALSE)),same,SUMPRODUCT((rows<=curRow)*(shows=A$Row)*(dates=D$Row)*numeric*IFERROR(starts=E$Row,FALSE)),IF(base>0,SUMPRODUCT((rows<curRow)*(shows=A$Row)*(dates=D$Row)*numeric*IFERROR(starts<E$Row,FALSE)*IFERROR(ends>=E$Row,FALSE))+same,same)))"
}

function TextAt($Sheet, $Row, $Col) {
  return [string](Retry { $Sheet.Cells.Item($Row, $Col).Text } "text $Row,$Col")
}

function ValueAt($Sheet, $Row, $Col) {
  return Retry { $Sheet.Cells.Item($Row, $Col).Value2 } "value $Row,$Col"
}

function DeleteSheetIfExists($Workbook, $Name) {
  foreach ($ws in @($Workbook.Worksheets)) {
    if ($ws.Name -eq $Name) {
      Retry { $ws.Delete() } "delete $Name" | Out-Null
      return
    }
  }
}

function AddPrintableSheet($Workbook, $Name, $Title, $Headers, $Rows, $PrintCols) {
  DeleteSheetIfExists $Workbook $Name
  $ws = Retry { $Workbook.Worksheets.Add([Type]::Missing, $Workbook.Worksheets.Item($Workbook.Worksheets.Count)) } "add $Name"
  $ws.Name = $Name
  $lastCol = [char]([int][char]'A' + $Headers.Count - 1)
  Retry { $ws.Range("A1:$lastCol`1").Merge() } "merge title $Name" | Out-Null
  Retry { $ws.Range('A1').Value2 = $Title } "title $Name" | Out-Null
  Retry { $ws.Range('A1').Font.Bold = $true } "title bold $Name" | Out-Null
  Retry { $ws.Range('A1').Font.Size = 16 } "title size $Name" | Out-Null
  Retry { $ws.Range('A1').Interior.Color = 8421504 } "title fill $Name" | Out-Null
  Retry { $ws.Range('A1').Font.Color = 16777215 } "title color $Name" | Out-Null

  $lines = New-Object System.Collections.Generic.List[string]
  $lines.Add(($Headers | ForEach-Object { ([string]$_) -replace "`t", " " -replace "`r?`n", " / " }) -join "`t")
  foreach ($row in $Rows) {
    $lines.Add(($row | ForEach-Object { ([string]$_) -replace "`t", " " -replace "`r?`n", " / " }) -join "`t")
  }
  if ($Rows.Count -eq 0) {
    $lines.Add('No matching events.')
  }
  Set-Clipboard -Value ($lines -join "`r`n")
  Retry { $ws.Activate() } "activate $Name" | Out-Null
  Retry { $ws.Range('A3').Select() } "select paste target $Name" | Out-Null
  Retry { $ws.Paste() } "paste table $Name" | Out-Null
  Retry { $ws.Range("A3:$lastCol`3").Font.Bold = $true } "headers bold $Name" | Out-Null
  Retry { $ws.Range("A3:$lastCol`3").Interior.Color = 14277081 } "headers fill $Name" | Out-Null

  $printLastRow = [Math]::Max(20, $Rows.Count + 4)
  $printArea = "`$A`$1:`$$PrintCols`$$printLastRow"
  Retry { $ws.Range($printArea).WrapText = $true } "wrap $Name" | Out-Null
  Retry { $ws.Range($printArea).VerticalAlignment = -4160 } "valign $Name" | Out-Null
  Retry { $ws.Columns.AutoFit() } "autofit cols $Name" | Out-Null
  Retry { $ws.Rows.AutoFit() } "autofit rows $Name" | Out-Null
  Retry { $ws.PageSetup.PrintArea = $printArea } "print area $Name" | Out-Null
  Retry { $ws.PageSetup.PrintTitleRows = '$1:$3' } "print titles $Name" | Out-Null
  Retry { $ws.PageSetup.Orientation = 2 } "landscape $Name" | Out-Null
  Retry { $ws.PageSetup.Zoom = $false } "zoom off $Name" | Out-Null
  Retry { $ws.PageSetup.FitToPagesWide = 1 } "fit wide $Name" | Out-Null
  try { $ws.PageSetup.FitToPagesTall = $false } catch {}
  Retry { $ws.PageSetup.LeftMargin = $ws.Application.InchesToPoints(0.25) } "left margin $Name" | Out-Null
  Retry { $ws.PageSetup.RightMargin = $ws.Application.InchesToPoints(0.25) } "right margin $Name" | Out-Null
  Retry { $ws.PageSetup.TopMargin = $ws.Application.InchesToPoints(0.35) } "top margin $Name" | Out-Null
  Retry { $ws.PageSetup.BottomMargin = $ws.Application.InchesToPoints(0.35) } "bottom margin $Name" | Out-Null
  return $ws
}

function IsRealText($Text) {
  return -not [string]::IsNullOrWhiteSpace($Text) -and $Text -ne '0'
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$excel.EnableEvents = $false
$wb = $null
try {
  $wb = Retry { $excel.Workbooks.Open($Output) } 'open workbook'
  $data = Retry { $wb.Worksheets.Item('Data Input') } 'data sheet'
  $weekly = Retry { $wb.Worksheets.Item('Weekly Calendar') } 'weekly sheet'
  $daily = Retry { $wb.Worksheets.Item('Daily Schedule') } 'daily sheet'

  foreach ($row in 4..484) {
    $formula = SlotFormula $row
    Retry { $data.Range("S$row").Formula2 = $formula } "slot S$row" | Out-Null
  }
  Retry { $excel.CalculateFullRebuild() } 'calculate slots' | Out-Null

  $showFilter = [string](Retry { $weekly.Range('C3').Text } 'show filter')
  $weekStart = [double](Retry { $weekly.Range('B3').Value2 } 'week start')
  $events = @()
  for ($r = 4; $r -le 484; $r++) {
    $event = TextAt $data $r 8
    if ([string]::IsNullOrWhiteSpace($event)) { continue }
    $show = TextAt $data $r 1
    if ($showFilter -and $show -ne $showFilter) { continue }
    $dateVal = ValueAt $data $r 4
    $startVal = ValueAt $data $r 5
    $endVal = ValueAt $data $r 7
    $obj = [pscustomobject]@{
      Row = $r
      Show = $show
      Owner = TextAt $data $r 2
      Poc = TextAt $data $r 3
      DateText = TextAt $data $r 4
      StartText = TextAt $data $r 5
      EndText = TextAt $data $r 7
      DateVal = if ($dateVal -is [double]) { $dateVal } else { 0 }
      StartVal = if ($startVal -is [double]) { $startVal } else { 0 }
      EndVal = if ($endVal -is [double]) { $endVal } else { 0 }
      Event = $event
      Location = TextAt $data $r 9
      Category = TextAt $data $r 10
      Required = TextAt $data $r 13
      Speakers = TextAt $data $r 14
      Sponsors = TextAt $data $r 15
      Status = TextAt $data $r 18
      Slot = TextAt $data $r 19
    }
    $events += $obj
  }

  $weeklyRows = $events |
    Where-Object { $_.DateVal -ge $weekStart -and $_.DateVal -le ($weekStart + 6) -and $_.StartVal -gt 0 -and $_.EndVal -gt 0 } |
    Sort-Object DateVal, StartVal, Event |
    ForEach-Object { @($_.DateText, $_.StartText, $_.EndText, $_.Event, $_.Location, $_.Owner, $_.Required, $_.Speakers) }
  AddPrintableSheet $wb 'Weekly Calendar New' 'Weekly Calendar - Timed Event List' @('Day','Start','End','Event','Location','Owner','Required Attendees','Speakers') $weeklyRows 'H' | Out-Null

  $locationRows = $events |
    Where-Object { IsRealText $_.Location } |
    Sort-Object Location, DateVal, StartVal, Event |
    ForEach-Object { @($_.Location, $_.DateText, $_.StartText, $_.EndText, $_.Event, $_.Owner, $_.Required, $_.Speakers) }
  AddPrintableSheet $wb 'Location Schedule' 'Location Schedule' @('Location','Date','Start','End','Event','Owner','Required Attendees','Speakers') $locationRows 'H' | Out-Null

  $sponsorRows = $events |
    Where-Object { IsRealText $_.Sponsors } |
    Sort-Object Sponsors, DateVal, StartVal, Event |
    ForEach-Object { @($_.Sponsors, $_.DateText, $_.StartText, $_.Event, $_.Location, $_.Owner, $_.Status) }
  AddPrintableSheet $wb 'Sponsors' 'Sponsored Events' @('Sponsors','Date','Start','Event','Location','Owner','Status') $sponsorRows 'G' | Out-Null

  $speakerRows = $events |
    Where-Object { IsRealText $_.Speakers } |
    Sort-Object Speakers, DateVal, StartVal, Event |
    ForEach-Object { @($_.Speakers, $_.DateText, $_.StartText, $_.Event, $_.Location, $_.Owner, $_.Status) }
  AddPrintableSheet $wb 'Speakers' 'Speaker Schedule' @('Speakers','Date','Start','Event','Location','Owner','Status') $speakerRows 'G' | Out-Null

  $attendeeRows = $events |
    Where-Object { IsRealText $_.Required } |
    Sort-Object Required, DateVal, StartVal, Event |
    ForEach-Object { @($_.Required, $_.DateText, $_.StartText, $_.Event, $_.Location, $_.Owner, $_.Status) }
  AddPrintableSheet $wb 'Required Attendees' 'Required Attendees Schedule' @('Required Attendees','Date','Start','Event','Location','Owner','Status') $attendeeRows 'G' | Out-Null

  Retry { $excel.CalculateFullRebuild() } 'final calculate' | Out-Null
  foreach ($row in 41..49) {
    $name = [string]$data.Cells.Item($row, 8).Text
    if ($name -match 'American|Germany|Bon Voyage|TDA|AIA') {
      Write-Output ("Row {0}: slot={1}; {2}; {3}" -f $row, $data.Cells.Item($row, 19).Value2, $data.Cells.Item($row, 5).Text, $name)
    }
  }
  Write-Output ('Daily CF count=' + $daily.Range('B7:K107').FormatConditions.Count)
  Write-Output ('Daily print area=' + $daily.PageSetup.PrintArea)
  Write-Output ('Sheets=' + (($wb.Worksheets | ForEach-Object { $_.Name }) -join ', '))

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
