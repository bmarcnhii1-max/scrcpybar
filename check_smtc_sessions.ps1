$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager,Windows.Media,ContentType=WindowsRuntime] | Out-Null
Add-Type -AssemblyName System.Runtime.WindowsRuntime

$asTaskGeneric = [System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' }

function Await($operation, $type) {
    $m = $asTaskGeneric[0].MakeGenericMethod($type)
    return $m.Invoke($null, @($operation)).GetAwaiter().GetResult()
}

$mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) `
    ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])

foreach ($s in $mgr.GetSessions()) {
    $info = $s.GetPlaybackInfo()
    $props = Await ($s.TryGetMediaPropertiesAsync()) `
        ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
    Write-Host "--- $($s.SourceAppUserModelId) : $($props.Title)"
    Write-Host "    status=$($info.PlaybackStatus)"
    if ($null -eq $info.Timeline) {
        Write-Host "    timeline=<null>"
    }
    else {
        Write-Host "    timeline min=$($info.Timeline.MinSeekTime) max=$($info.Timeline.MaxSeekTime) pos=$($info.Timeline.Position)"
    }
    if ($null -eq $props.Thumbnail) {
        Write-Host "    thumbnail=<null>"
    }
    else {
        Write-Host "    thumbnail=$($props.Thumbnail.GetType().Name)"
    }
}
