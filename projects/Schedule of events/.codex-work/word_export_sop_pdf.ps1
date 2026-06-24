$ErrorActionPreference = "Stop"
$docx = "C:\kwi-automations\projects\Schedule of events\Schedule Template SOP V8.6.docx"
$pdf = "C:\kwi-automations\projects\Schedule of events\.codex-work\sop_render_v8_6\Schedule Template SOP V8.6.pdf"
New-Item -ItemType Directory -Force -Path (Split-Path $pdf) | Out-Null
$word = New-Object -ComObject Word.Application
$word.Visible = $false
try {
    $doc = $word.Documents.Open($docx, $false, $true)
    $doc.ExportAsFixedFormat($pdf, 17)
    $doc.Close($false)
    Write-Host $pdf
}
finally {
    $word.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($word)
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
