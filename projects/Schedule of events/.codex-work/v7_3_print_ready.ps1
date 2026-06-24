$Project = 'C:\kwi-automations\projects\Schedule of events'
$Source = Join-Path $Project 'Schedule of Events V7.2 - Codex.xlsx'
$Output = Join-Path $Project 'Schedule of Events V7.3 - Codex Print Ready.xlsx'
Copy-Item -LiteralPath $Source -Destination $Output -Force

function Retry($ScriptBlock, $Label) {
  for ($i = 0; $i -lt 10; $i++) {
    try { return & $ScriptBlock } catch { Start-Sleep -Milliseconds (500 + 250 * $i) }
  }
  throw "Failed: $Label"
}

function TrySet($ScriptBlock, $Label) {
  try {
    Retry $ScriptBlock $Label | Out-Null
  } catch {
    Write-Output ("Skipped optional print setting: " + $Label)
  }
}

function Configure-ReadablePrint($Sheet, $PrintArea, $TitleRows, $Landscape = $true, $FitTall = $false) {
  Retry { $Sheet.PageSetup.PrintArea = $PrintArea } "print area $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.PrintTitleRows = $TitleRows } "title rows $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.Orientation = $(if ($Landscape) { 2 } else { 1 }) } "orientation $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.PaperSize = 1 } "paper size $($Sheet.Name)" | Out-Null # Letter
  Retry { $Sheet.PageSetup.LeftMargin = $Sheet.Application.InchesToPoints(0.25) } "left margin $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.RightMargin = $Sheet.Application.InchesToPoints(0.25) } "right margin $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.TopMargin = $Sheet.Application.InchesToPoints(0.35) } "top margin $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.BottomMargin = $Sheet.Application.InchesToPoints(0.35) } "bottom margin $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.HeaderMargin = $Sheet.Application.InchesToPoints(0.15) } "header margin $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.FooterMargin = $Sheet.Application.InchesToPoints(0.15) } "footer margin $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.Zoom = $false } "disable zoom $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.FitToPagesWide = 1 } "fit wide $($Sheet.Name)" | Out-Null
  if ($FitTall) {
    TrySet { $Sheet.PageSetup.FitToPagesTall = 1 } "fit tall $($Sheet.Name)"
  } else {
    TrySet { $Sheet.PageSetup.FitToPagesTall = $false } "unlimited fit tall $($Sheet.Name)"
  }
  Retry { $Sheet.PageSetup.CenterHorizontally = $true } "center horizontal $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.CenterVertically = $false } "center vertical $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.PrintGridlines = $false } "print gridlines $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.BlackAndWhite = $false } "print color $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.Draft = $false } "draft off $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.LeftHeader = '&B&14&""Aptos""' + $Sheet.Name } "left header $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.RightHeader = '&10&D &T' } "right header $($Sheet.Name)" | Out-Null
  Retry { $Sheet.PageSetup.CenterFooter = '&10Page &P of &N' } "footer $($Sheet.Name)" | Out-Null
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
  $notes = Retry { $wb.Worksheets.Item('Event Notes and Attendees') } 'notes sheet'
  $data = Retry { $wb.Worksheets.Item('Data Input') } 'data sheet'

  # Daily: include the full formula grid through the dynamic TBD row area, repeat filters/header on each page.
  Configure-ReadablePrint $daily '$A$1:$K$107' '$1:$6' $true $false
  Retry { $daily.PageSetup.PrintTitleColumns = '$A:$A' } 'daily title column' | Out-Null
  Retry { $daily.Range('A1:K107').Font.Name = 'Aptos' } 'daily font' | Out-Null
  Retry { $daily.Range('A1:K107').WrapText = $true } 'daily wrap' | Out-Null
  Retry { $daily.Range('A6:K6').Font.Size = 12 } 'daily header font size' | Out-Null
  Retry { $daily.Range('B7:K107').Font.Size = 10 } 'daily body font size' | Out-Null
  Retry { $daily.Range('A7:A107').Font.Size = 11 } 'daily time font size' | Out-Null
  Retry { $daily.Range('A7:K107').VerticalAlignment = -4160 } 'daily top align' | Out-Null
  Retry { $daily.Range('A1:K107').Rows.AutoFit() } 'daily autofit rows' | Out-Null
  Retry { $daily.Range('A7:A107').ColumnWidth = 11 } 'daily time width' | Out-Null

  # Weekly: include the calendar and existing lower special section. Leave it readable instead of colored.
  Configure-ReadablePrint $weekly '$A$1:$H$41' '$1:$6' $true $true
  Retry { $weekly.Range('A1:H41').Font.Name = 'Aptos' } 'weekly font' | Out-Null
  Retry { $weekly.Range('A1:H41').WrapText = $true } 'weekly wrap' | Out-Null
  Retry { $weekly.Range('A5:H6').Font.Size = 12 } 'weekly header font size' | Out-Null
  Retry { $weekly.Range('B7:H41').Font.Size = 10 } 'weekly body font size' | Out-Null
  Retry { $weekly.Range('A7:A41').Font.Size = 11 } 'weekly time font size' | Out-Null
  Retry { $weekly.Range('A1:H41').VerticalAlignment = -4160 } 'weekly top align' | Out-Null
  Retry { $weekly.Range('A1:H41').Rows.AutoFit() } 'weekly autofit rows' | Out-Null

  # Event notes are reference/field-useful, so make them printable without becoming the primary packet.
  Configure-ReadablePrint $notes '$A$1:$S$60' '$1:$1' $true $false
  Retry { $notes.Range('A1:S60').Font.Name = 'Aptos' } 'notes font' | Out-Null
  Retry { $notes.Range('A1:S60').Font.Size = 9 } 'notes font size' | Out-Null
  Retry { $notes.Range('A1:S60').WrapText = $true } 'notes wrap' | Out-Null
  Retry { $notes.Range('A1:S1').Font.Size = 10 } 'notes header size' | Out-Null
  Retry { $notes.Range('A1:S60').Rows.AutoFit() } 'notes autofit rows' | Out-Null

  # Data Input is not intended as a field printout, but set a sane first-page print area for accidental printing.
  Configure-ReadablePrint $data '$A$1:$S$40' '$1:$3' $true $false

  Retry { $excel.CalculateFullRebuild() } 'calculate workbook' | Out-Null

  Write-Output ('Daily print area=' + $daily.PageSetup.PrintArea)
  Write-Output ('Daily title rows=' + $daily.PageSetup.PrintTitleRows)
  Write-Output ('Weekly print area=' + $weekly.PageSetup.PrintArea)
  Write-Output ('Weekly title rows=' + $weekly.PageSetup.PrintTitleRows)
  Write-Output ('Notes print area=' + $notes.PageSetup.PrintArea)
  Write-Output ('Daily B2=' + $daily.Range('B2').Text + ' A63=' + $daily.Range('A63').Text)
  Write-Output ('Daily CF count=' + $daily.Range('B7:K107').FormatConditions.Count)
  Write-Output ('Weekly CF count=' + $weekly.Range('B7:H23').FormatConditions.Count)

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
