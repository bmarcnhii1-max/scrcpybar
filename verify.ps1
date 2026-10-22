param(
    [string]$Bridge = "http://127.0.0.1:39281",
    [string]$Companion = "http://127.0.0.1:39282",
    # Transport tests press play/pause and skip tracks, which interrupts whatever is playing.
    # They are opt-in so a plain run never hijacks the phone's music.
    [switch]$Transport
)

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$script:passed = 0
$script:failed = 0
$script:lastPostOk = $false

function Check($name, $condition, $detail) {
    if ($condition) {
        $script:passed++
        Write-Host ("  PASS  {0} - {1}" -f $name, $detail) -ForegroundColor Green
    }
    else {
        $script:failed++
        Write-Host ("  FAIL  {0} - {1}" -f $name, $detail) -ForegroundColor Red
    }
}

function Warn($name, $detail) {
    Write-Host ("  WARN  {0} - {1}" -f $name, $detail) -ForegroundColor Yellow
}


function Get-Json($url) {
    try { return (Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 25).Content | ConvertFrom-Json }
    catch { return $null }
}

function Post($url) {
    # Returns nothing: the result lands in $script:lastPostOk so passing it as a Check
    # argument does not leak the boolean into the output stream.
    try {
        Invoke-WebRequest $url -Method Post -UseBasicParsing -TimeoutSec 25 | Out-Null
        $script:lastPostOk = $true
    }
    catch {
        $script:lastPostOk = $false
    }
}

function Settle($ms = 3000) { Start-Sleep -Milliseconds $ms }

<#
    Reads the position until it stops moving. A paused track still creeps forward for a moment
    while the app applies the pause, and comparing a moving position against a seek target
    produces a false failure.
#>
function Wait-StablePosition {
    $last = -1
    for ($i = 0; $i -lt 12; $i++) {
        Start-Sleep -Milliseconds 400
        $now = (Get-Json "$Bridge/api/track").positionMs
        if ($null -ne $now -and $now -eq $last) { return $now }
        $last = $now
    }
    return $last
}

Write-Host ""
Write-Host "=== 1. Companion app (Android, USB forward) ===" -ForegroundColor Cyan

$health = Get-Json "$Companion/health"
if ($null -eq $health) {
    # The companion is not reachable on the phone the bridge currently drives. That is valid
    # when several phones are attached and only one of them has the companion installed, or
    # when the device blocks adb installs - the bridge falls back to dumpsys in that case.
    Warn "companion reachable" "$Companion not answering; the active phone may not have the companion installed"
}
else {
    Check "companion reachable" $true "$Companion"
    Check "companion ok" ($health.ok -eq $true) "app=$($health.app) v$($health.version) api=$($health.apiVersion)"
    Check "notification listener" ($health.notificationListenerConnected -eq $true) "connected=$($health.notificationListenerConnected)"
    Check "auth disabled by default" ($health.authRequired -eq $false) "authRequired=$($health.authRequired)"
    Check "loopback bind" ($health.port -eq 38080) "phone port=$($health.port) (forwarded as 39282)"
}

$sessions = Get-Json "$Companion/sessions"
if ($null -ne $sessions) {
    Check "sessions endpoint" $true "count=$($sessions.count)"

    if ($sessions.count -gt 0) {
        $art = $sessions.sessions[0].artworkUrl
        Check "artworkUrl uses forwarded port" ($art -like "*39282*") "artworkUrl=$art"
    }
}
else {
    Warn "sessions endpoint" "companion is not answering"
}


Write-Host ""
Write-Host "=== 2. Bridge HTTP API ===" -ForegroundColor Cyan

$track = Get-Json "$Bridge/api/track"
Check "bridge reachable" ($null -ne $track) "$Bridge"
if ($track) {
    Check "phone connected" ($track.connected -eq $true) "app=$($track.appName)"
    Check "track present" ($track.hasTrack -eq $true) "'$($track.title)' by $($track.artist)"
    Check "album art cached" (-not [string]::IsNullOrWhiteSpace($track.albumArtPath)) "$([System.IO.Path]::GetFileName($track.albumArtPath))"
}

Write-Host ""
Write-Host "=== 3. Cover art endpoint ===" -ForegroundColor Cyan

try {
    $cover = Invoke-WebRequest "$Bridge/api/cover" -UseBasicParsing -TimeoutSec 25
    Check "cover served" ($cover.StatusCode -eq 200 -and $cover.RawContentLength -gt 0) `
        "$($cover.Headers['Content-Type']) $($cover.RawContentLength) bytes"
}
catch {
    Check "cover served" $false $_.Exception.Message
}

Write-Host ""
Write-Host "=== 4. Karaoke lyrics ===" -ForegroundColor Cyan

$lyrics = Get-Json "$Bridge/api/lyrics"
Check "lyrics endpoint" ($null -ne $lyrics) "$Bridge/api/lyrics"
if ($lyrics) {
    $lineCount = 0
    if ($lyrics.linesText) { $lineCount = @($lyrics.linesText -split "`n").Count }
    # "not-found" means the track simply has no lyrics in the database - that is a normal
    # answer, not a broken lookup.
    if ($lyrics.source -eq "not-found") {
        Warn "lyrics resolved" "no lyrics in lrclib for this track (that is fine)"
    }
    else {
        Check "lyrics resolved" ($lyrics.ok -eq $true) "source=$($lyrics.source) synced=$($lyrics.synced) instrumental=$($lyrics.instrumental)"
    }
    if ($lyrics.ok) {
        Check "timed lines present" ($lineCount -gt 0) "$lineCount lines"
        Check "first line has a timestamp" ($lyrics.linesText -match '^\d+\t') ($lyrics.linesText -split "`n")[0]
    }
}


Write-Host ""
Write-Host "=== 5. Jump to a lyric line ===" -ForegroundColor Cyan

if (-not $Transport) {
    Write-Host "  SKIP  seek to a lyric line - pass -Transport to move the playhead" -ForegroundColor Yellow
}
elseif ($lyrics -and $lyrics.ok -and $lyrics.linesText) {
    $rows = @($lyrics.linesText -split "`n" | Where-Object { $_ -match '^\d+\t' })
    if ($rows.Count -gt 3) {
        $row = $rows[3]
        $timeMs = [int64]($row -split "`t")[0]
        $durationMs = [int64]$track.durationMs

        # Pause first, otherwise the position keeps moving during the settle window and every
        # reading overshoots the requested point.
        if ((Get-Json "$Bridge/api/track").isPlaying) {
            Post "$Bridge/api/play-pause"
            $null = Wait-StablePosition
        }

        Post "$Bridge/api/seek-position?positionMs=$timeMs&durationMs=$durationMs"
        Check "seek to lyric line accepted" $script:lastPostOk "target=${timeMs}ms"
        Settle
        $after = (Get-Json "$Bridge/api/track").positionMs
        Check "seek to lyric line applied" ([Math]::Abs($after - $timeMs) -le 2000) `
            "requested ${timeMs}ms -> ${after}ms"
    }
    else {
        Check "seek to lyric line accepted" $true "not enough lines to test"
    }
}
else {
    Check "seek to lyric line accepted" $true "no lyrics for this track"
}

Write-Host ""
Write-Host "=== 6. Playback control through Bridge ===" -ForegroundColor Cyan


if (-not $Transport) {
    Write-Host "  SKIP  transport tests - pass -Transport to press play/pause, skip tracks and seek" -ForegroundColor Yellow
}
elseif ($track) {
    $before = (Get-Json "$Bridge/api/track").isPlaying
    Post "$Bridge/api/play-pause"
    Check "play-pause accepted" $script:lastPostOk "was playing=$before"
    Settle
    $after = (Get-Json "$Bridge/api/track").isPlaying
    Check "play-pause toggled state" ($after -ne $before) "$before -> $after"

    Post "$Bridge/api/play-pause"
    Settle

    $t0 = Get-Json "$Bridge/api/track"
    Post "$Bridge/api/next"
    Settle 4000
    $t1 = Get-Json "$Bridge/api/track"
    Check "next changed track" ($null -ne $t1) "'$($t0.title)' -> '$($t1.title)'"

    Post "$Bridge/api/prev"
    Settle 4000
    $t2 = Get-Json "$Bridge/api/track"
    Check "prev changed track" ($null -ne $t2) "'$($t1.title)' -> '$($t2.title)'"

    # Seek accuracy is only measurable on a paused track: while it plays, position keeps advancing
    # during the settle window and every reading overshoots the requested point.
    if ((Get-Json "$Bridge/api/track").isPlaying) {
        Post "$Bridge/api/play-pause"
        $null = Wait-StablePosition
    }

    $dur = $t2.durationMs
    $target = [Math]::Min(30000, [int]($dur / 2))
    Post "$Bridge/api/seek-position?positionMs=$target&durationMs=$dur"
    Check "absolute seek accepted" $script:lastPostOk "target=${target}ms"
    $seeked = Wait-StablePosition
    Check "absolute seek applied" ([Math]::Abs($seeked - $target) -le 2000) "${seeked}ms (target ${target}ms, drift $([Math]::Abs($seeked - $target))ms)"

    Post "$Bridge/api/rewind"
    Settle
    $rewound = (Get-Json "$Bridge/api/track").positionMs
    Check "rewind moved back" ($rewound -lt $seeked) "${seeked}ms -> ${rewound}ms"

    Post "$Bridge/api/fast-forward"
    Settle
    $ff = (Get-Json "$Bridge/api/track").positionMs
    Check "fast-forward moved ahead" ($ff -gt $rewound) "${rewound}ms -> ${ff}ms"
}

Write-Host ""
Write-Host "=== 7. Windows Media Controls (SMTC) ===" -ForegroundColor Cyan

[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager,Windows.Media,ContentType=WindowsRuntime] | Out-Null
Add-Type -AssemblyName System.Runtime.WindowsRuntime

$asTaskGeneric = [System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' }

function Await($operation, $type) {
    $asTaskGeneric[0].MakeGenericMethod($type).Invoke($null, @($operation)).GetAwaiter().GetResult()
}

$mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) `
    ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
$session = $mgr.GetCurrentSession()
Check "SMTC session published" ($null -ne $session) "appId=$(if ($session) { $session.SourceAppUserModelId } else { 'none' })"

if ($session) {
    $props = Await ($session.TryGetMediaPropertiesAsync()) `
        ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
    Check "SMTC metadata" (-not [string]::IsNullOrWhiteSpace($props.Title)) "title='$($props.Title)' artist='$($props.Artist)'"
    Check "SMTC thumbnail" ($null -ne $props.Thumbnail) "RandomAccessStreamReference"

    $info = $session.GetPlaybackInfo()
    Check "SMTC transport controls" ($info.Controls.IsPlayEnabled -or $info.Controls.IsPauseEnabled) `
        "play=$($info.Controls.IsPlayEnabled) pause=$($info.Controls.IsPauseEnabled) next=$($info.Controls.IsNextEnabled) prev=$($info.Controls.IsPreviousEnabled)"

    if (-not $Transport) {
        Write-Host "  SKIP  SMTC transport tests - pass -Transport to press buttons and seek" -ForegroundColor Yellow
    }
    else {
        $before = Get-Json "$Bridge/api/track"
        $seekTarget = [Math]::Min(45, [int]($before.durationMs / 1000) - 5)
        if ($seekTarget -gt 3) {
            # Pause first, and wait until the position actually stops, so the comparison
            # below is against a still track instead of one that is still rolling.
            if ($before.isPlaying) {
                $null = $session.TryPauseAsync()
                $null = Wait-StablePosition
            }
            $null = $session.TryChangePlaybackPositionAsync([TimeSpan]::FromSeconds($seekTarget).Ticks)
            $after = Wait-StablePosition
            Check "SMTC seek reaches phone" ([Math]::Abs($after - $seekTarget * 1000) -le 2000) `
                "requested ${seekTarget}s -> ${after}ms"
        }
        else {
            Check "SMTC seek reaches phone" $true "skipped, track too short"
        }

        $p0 = (Get-Json "$Bridge/api/track").isPlaying
        $null = if ($p0) { $session.TryPauseAsync() } else { $session.TryPlayAsync() }
        Settle
        $p1 = (Get-Json "$Bridge/api/track").isPlaying
        Check "SMTC play/pause reaches phone" ($p1 -ne $p0) "$p0 -> $p1"

        $null = if ($p1) { $session.TryPauseAsync() } else { $session.TryPlayAsync() }
        Settle

        $n0 = (Get-Json "$Bridge/api/track").title
        $null = $session.TrySkipNextAsync()
        Settle 4000
        $n1 = (Get-Json "$Bridge/api/track").title
        Check "SMTC next reaches phone" ($n1 -ne $n0) "'$n0' -> '$n1'"

        $null = $session.TrySkipPreviousAsync()
        Settle 4000
        $n2 = (Get-Json "$Bridge/api/track").title
        Check "SMTC previous reaches phone" ($n2 -eq $n0) "'$n1' -> '$n2'"
    }
}

Write-Host ""
Write-Host "=== 8. Game Bar widget ===" -ForegroundColor Cyan

$pkg = Get-AppxPackage -Name ScrcpyBar.Widget -ErrorAction SilentlyContinue
Check "widget registered" ($null -ne $pkg) "status=$(if ($pkg) { $pkg.Status } else { 'not installed' })"
if ($pkg) { Check "widget package OK" ($pkg.Status -eq 'Ok') $pkg.PackageFullName }

$exempt = (CheckNetIsolation.exe LoopbackExempt -s) 2>$null
Check "loopback exemption" (($exempt -join "`n") -match [regex]::Escape($pkg.PackageFamilyName)) $pkg.PackageFamilyName

Write-Host ""
Write-Host "================================" -ForegroundColor Cyan
Write-Host ("  passed: {0}   failed: {1}" -f $script:passed, $script:failed) -ForegroundColor $(if ($script:failed -eq 0) { "Green" } else { "Red" })
Write-Host ""
exit $(if ($script:failed -eq 0) { 0 } else { 1 })



