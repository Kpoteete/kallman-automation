param([Parameter(Mandatory=$true)][string]$Path)
$ErrorActionPreference = "Stop"

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$excel.AskToUpdateLinks = $false
$excel.EnableEvents = $false

function Release-ComObject($obj) {
    if ($null -ne $obj -and [System.Runtime.InteropServices.Marshal]::IsComObject($obj)) {
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($obj)
    }
}
function Retry([scriptblock]$action) {
    $last = $null
    for ($i=1; $i -le 25; $i++) {
        try { return & $action }
        catch [System.Runtime.InteropServices.COMException] {
            $last = $_
            Start-Sleep -Milliseconds 250
        }
    }
    throw $last
}
try {
    $wb = Retry { $excel.Workbooks.Open($Path) }
    if ($null -eq $wb) { throw "Workbook open returned null" }
    $names = @()
    foreach ($ws in $wb.Worksheets) { $names += $ws.Name }
    $result = [ordered]@{
        opened = $true
        sheets = $names
        values = [ordered]@{}
    }
    foreach ($name in @("Daily Schedule","Weekly Calendar New","Instructions")) {
        $ws = Retry { $wb.Worksheets.Item($name) }
        $result.values[$name] = [ordered]@{
            A1 = [string](Retry { $ws.Range("A1").Text })
            A4 = [string](Retry { $ws.Range("A4").Text })
        }
        Release-ComObject $ws
    }
    Retry { $wb.Close($false) } | Out-Null
    Release-ComObject $wb
    $result | ConvertTo-Json -Depth 5
}
finally {
    $excel.Quit()
    Release-ComObject $excel
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
