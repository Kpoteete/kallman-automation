$ErrorActionPreference = "Stop"
$ppt = "C:\kwi-automations\projects\Schedule of events\Schedule Template Training Deck V8.6.pptx"
$out = "C:\kwi-automations\projects\Schedule of events\.codex-work\ppt_preview_v8_6"
if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null
$pp = New-Object -ComObject PowerPoint.Application
try {
    $deck = $pp.Presentations.Open($ppt, $true, $false, $false)
    foreach ($slide in $deck.Slides) {
        $path = Join-Path $out ("slide-{0:00}.png" -f $slide.SlideIndex)
        $slide.Export($path, "PNG", 1280, 720)
    }
    $count = $deck.Slides.Count
    $deck.Close()
    Write-Host "Exported $count slides to $out"
}
finally {
    $pp.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($pp)
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
