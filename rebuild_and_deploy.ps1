$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "ScrcpyBar.Widget\ScrcpyBar.Widget.csproj"
$packagesRoot = Join-Path $root "ScrcpyBar.Widget\AppPackages"
$layout = Join-Path $packagesRoot "ScrcpyBar_Registered"

$msbuild = (Get-Command msbuild.exe -ErrorAction SilentlyContinue).Source
if (-not $msbuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $install = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($install) {
            $candidate = Join-Path $install "MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path $candidate) { $msbuild = $candidate }
        }
    }
}
if (-not $msbuild) {
    throw "MSBuild not found"
}

& $msbuild $project /t:Rebuild /p:Configuration=Debug /p:Platform=x64 /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Widget build failed" }

$msix = Get-ChildItem -LiteralPath $packagesRoot -Recurse -Filter "*.msix" -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $msix) { throw "MSIX package not found" }

$makeappx = Get-ChildItem -LiteralPath (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin") -Recurse -Filter "makeappx.exe" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "\\x64\\" } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $makeappx) { throw "makeappx.exe not found" }

# The unpacked layout lives inside the package's install folder, so the widget has to be
# unregistered and its process killed before the folder can be deleted - otherwise the running
# exe keeps a handle and Remove-Item fails with "Access to the path is denied".
Get-Process -Name "ScrcpyBar.Widget" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Get-AppxPackage -Name ScrcpyBar.Widget -ErrorAction SilentlyContinue |
    Remove-AppxPackage -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

if (Test-Path -LiteralPath $layout) {
    Remove-Item -LiteralPath $layout -Recurse -Force
}

& $makeappx.FullName unpack /p $msix.FullName /d $layout | Out-Null
if ($LASTEXITCODE -ne 0) { throw "MSIX unpack failed" }

Add-AppxPackage -Register (Join-Path $layout "AppxManifest.xml") -ForceApplicationShutdown
$pkg = Get-AppxPackage -Name ScrcpyBar.Widget
if (-not $pkg) { throw "Widget registration failed" }

CheckNetIsolation.exe LoopbackExempt -a "-n=$($pkg.PackageFamilyName)" | Out-Null
Write-Host "Scrcpy Music widget registered: $($pkg.PackageFullName)"
Write-Host "Open Win+G -> Widgets -> Scrcpy Music"
