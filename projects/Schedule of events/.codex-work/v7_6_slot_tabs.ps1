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

function DeleteSheetIfExists($Workbook, $Name) {
  foreach ($ws in @($Workbook.Worksheets)) {
    if ($ws.Name -eq $Name) {
      Retry { $ws.Delete() } "delete sheet $Name" | Out-Null
      return
    }
  }
}

function SetupSheet($Workbook, $Name, $Title, $Headers, $Formula, $PrintArea) {
  DeleteSheetIfExists $Workbook $Name
  $ws = Retry { $Workbook.Worksheets.Add([Type]::Missing, $Workbook.Worksheets.Item($Workbook.Worksheets.Count)) } "add sheet $Name"
  $ws.Name = $Name
  $lastCol = [char]([int][char]'A' + $Headers.Count - 1)
  Retry { $ws.Range("A1:$lastCol`1").Merge() } "merge title $Name" | Out-Null
  Retry { $ws.Range('A1').Value2 = $Title } "title $Name" | Out-Null
  Retry { $ws.Range('A1').Font.Bold = $true } "title bold $Name" | Out-Null
  Retry { $ws.Range('A1').Font.Size = 16 } "title size $Name" | Out-Null
  Retry { $ws.Range('A1').Interior.Color = 8421504 } "title fill $Name" | Out-Null
  Retry { $ws.Range('A1').Font.Color = 16777215 } "title color $Name" | Out-Null
  for ($i = 0; $i -lt $Headers.Count; $i++) {
    $colNum = $i + 1
    Retry { $ws.Cells.Item(3, $colNum).Value2 = $Headers[$i] } "header $Name $colNum" | Out-Null
  }
  Retry { $ws.Range("A3:$lastCol`3").Font.Bold = $true } "headers bold $Name" | Out-Null
  Retry { $ws.Range("A3:$lastCol`3").Interior.Color = 14277081 } "headers fill $Name" | Out-Null
  Retry { $ws.Range('A4').Formula2 = $Formula } "formula $Name" | Out-Null
  Retry { $ws.Range($PrintArea).WrapText = $true } "wrap $Name" | Out-Null
  Retry { $ws.Range($PrintArea).VerticalAlignment = -4160 } "valign $Name" | Out-Null
  Retry { $ws.Columns.AutoFit() } "autofit columns $Name" | Out-Null
  Retry { $ws.Rows.AutoFit() } "autofit rows $Name" | Out-Null
  Retry { $ws.PageSetup.PrintArea = $PrintArea } "print area $Name" | Out-Null
  Retry { $ws.PageSetup.PrintTitleRows = '$1:$3' } "print titles $Name" | Out-Null
  Retry { $ws.PageSetup.Orientation = 2 } "landscape $Name" | Out-Null
  Retry { $ws.PageSetup.Zoom = $false } "zoom off $Name" | Out-Null
  Retry { $ws.PageSetup.FitToPagesWide = 1 } "fit wide $Name" | Out-Null
  try { $ws.PageSetup.FitToPagesTall = $false } catch {}
  Retry { $ws.PageSetup.LeftMargin = $ws.Application.InchesToPoints(0.25) } "left margin $Name" | Out-Null
  Retry { $ws.PageSetup.RightMargin = $ws.Application.InchesToPoints(0.25) } "right margin $Name" | Out-Null
  Retry { $ws.PageSetup.TopMargin = $ws.Application.InchesToPoints(0.35) } "top margin $Name" | Out-Null
  Retry { $ws.PageSetup.BottomMargin = $ws.Application.InchesToPoints(0.35) } "bottom margin $Name" | Out-Null
  Retry { $ws.PageSetup.CenterHorizontally = $true } "center $Name" | Out-Null
  return $ws
}

function Q($Sheet, $Col) { return "'Data Input'!`$$Col`$4:`$$Col`$484" }

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
    Retry { $data.Range("S$row").Formula2 = $formula } "slot S$row" | Out-Null
  }

  $weeklyFormula = "=LET(ws,'Weekly Calendar'!`$B`$3,show,'Weekly Calendar'!`$C`$3,FILTER(SORTBY(CHOOSE({1,2,3,4,5,6,7,8},TEXT($(Q D),""ddd m/d""),$(Q E),$(Q G),$(Q H),$(Q I),$(Q B),$(Q M),$(Q N)),$(Q D),1,$(Q E),1),($(Q A)=show)*IFERROR($(Q D)>=ws,FALSE)*IFERROR($(Q D)<=ws+6,FALSE)*IFERROR(ISNUMBER($(Q E))*ISNUMBER($(Q G)),FALSE)),"""")"
  SetupSheet $wb 'Weekly Calendar New' 'Weekly Calendar - Timed Event List' @('Day','Start','End','Event','Location','Owner','Required Attendees','Speakers') $weeklyFormula '$A$1:$H$80' | Out-Null

  $locFormula = "=LET(show,'Weekly Calendar'!`$C`$3,FILTER(SORTBY(CHOOSE({1,2,3,4,5,6,7,8},$(Q I),$(Q D),$(Q E),$(Q G),$(Q H),$(Q B),$(Q M),$(Q N)),$(Q I),1,$(Q D),1,$(Q E),1),($(Q A)=show)*($(Q I)<>"""")*($(Q I)<>0)),"""")"
  SetupSheet $wb 'Location Schedule' 'Location Schedule' @('Location','Date','Start','End','Event','Owner','Required Attendees','Speakers') $locFormula '$A$1:$H$120' | Out-Null

  $sponsorFormula = "=LET(show,'Weekly Calendar'!`$C`$3,FILTER(SORTBY(CHOOSE({1,2,3,4,5,6,7},$(Q O),$(Q D),$(Q E),$(Q H),$(Q I),$(Q B),$(Q R)),$(Q O),1,$(Q D),1,$(Q E),1),($(Q A)=show)*($(Q O)<>"""")*($(Q O)<>0)),"""")"
  SetupSheet $wb 'Sponsors' 'Sponsored Events' @('Sponsors','Date','Start','Event','Location','Owner','Status') $sponsorFormula '$A$1:$G$80' | Out-Null

  $speakerFormula = "=LET(show,'Weekly Calendar'!`$C`$3,FILTER(SORTBY(CHOOSE({1,2,3,4,5,6,7},$(Q N),$(Q D),$(Q E),$(Q H),$(Q I),$(Q B),$(Q R)),$(Q N),1,$(Q D),1,$(Q E),1),($(Q A)=show)*($(Q N)<>"""")*($(Q N)<>0)),"""")"
  SetupSheet $wb 'Speakers' 'Speaker Schedule' @('Speakers','Date','Start','Event','Location','Owner','Status') $speakerFormula '$A$1:$G$100' | Out-Null

  $attendeeFormula = "=LET(show,'Weekly Calendar'!`$C`$3,FILTER(SORTBY(CHOOSE({1,2,3,4,5,6,7},$(Q M),$(Q D),$(Q E),$(Q H),$(Q I),$(Q B),$(Q R)),$(Q M),1,$(Q D),1,$(Q E),1),($(Q A)=show)*($(Q M)<>"""")*($(Q M)<>0)),"""")"
  SetupSheet $wb 'Required Attendees' 'Required Attendees Schedule' @('Required Attendees','Date','Start','Event','Location','Owner','Status') $attendeeFormula '$A$1:$G$120' | Out-Null

  Retry { $excel.CalculateFullRebuild() } 'calculate' | Out-Null

  foreach ($row in 41..49) {
    $name = [string]$data.Cells.Item($row, 8).Text
    $slot = [string]$data.Cells.Item($row, 19).Value2
    $start = [string]$data.Cells.Item($row, 5).Text
    if ($name -match 'American|Germany|Bon Voyage|TDA|AIA') {
      Write-Output ("Row {0}: slot={1}; {2}; {3}" -f $row, $slot, $start, $name)
    }
  }

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
