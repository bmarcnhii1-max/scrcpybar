param(
    [int]$Tail = 40
)

$pkg = Get-AppxPackage -Name ScrcpyBar.Widget
if (-not $pkg) { Write-Host "ScrcpyBar.Widget is not installed"; exit 1 }

$log = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalState\widget.log"
Write-Host "log: $log"

if (-not (Test-Path $log)) {
    Write-Host "No widget.log yet - it is written the first time you drag the progress bar."
    exit 0
}

Get-Content $log -Tail $Tail
