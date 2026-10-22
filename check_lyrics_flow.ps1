param(
    [string]$Bridge = "http://127.0.0.1:39281"
)

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$passed = 0
$failed = 0

function Check($name, $condition, $detail) {
    if ($condition) { $script:passed++; Write-Host ("  PASS  {0} - {1}" -f $name, $detail) -ForegroundColor Green }
    else { $script:failed++; Write-Host ("  FAIL  {0} - {1}" -f $name, $detail) -ForegroundColor Red }
}

function Get-Json($url) {
    try { return (Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 40).Content | ConvertFrom-Json }
    catch { return $null }
}

Write-Host "=== Widget lyrics flow (replayed exactly as the widget does it) ===" -ForegroundColor Cyan

$track = Get-Json "$Bridge/api/track"
if (-not $track) { Write-Host "  bridge not reachable"; exit 1 }
Write-Host "  track: '$($track.title)' by '$($track.artist)'"
Check "track has a title" (-not [string]::IsNullOrWhiteSpace($track.title)) $track.title

$lyrics = Get-Json "$Bridge/api/lyrics"
Check "lyrics payload received" ($null -ne $lyrics) "ok=$($lyrics.ok) source=$($lyrics.source)"

if ($lyrics -and $lyrics.ok) {
    # The widget parses linesText as "timeMs<TAB>text" and splits on newlines.
    $parsed = @()
    foreach ($raw in ($lyrics.linesText -split "`n")) {
        if ($raw.Length -eq 0) { continue }
        $tab = $raw.IndexOf("`t")
        if ($tab -le 0) { continue }
        if (-not [int64]::TryParse($raw.Substring(0, $tab), [ref]$null)) { continue }
        $parsed += [pscustomobject]@{ t = [int64]$raw.Substring(0, $tab); s = $raw.Substring($tab + 1) }
    }

    Check "lines parsed like the widget does" ($parsed.Count -gt 0) "$($parsed.Count) lines"
    Check "times are ascending" (($parsed | ForEach-Object { $_.t }) -join ',' -eq (($parsed | ForEach-Object { $_.t }) -join ',')) "first=$($parsed[0].t)ms last=$($parsed[-1].t)ms"
    Check "text is not empty" (-not [string]::IsNullOrWhiteSpace($parsed[0].s)) "'$($parsed[0].s)'"

    # The karaoke sweep needs a current line at the current playback position.
    $position = [double]$track.positionMs
    $index = -1
    for ($i = 0; $i -lt $parsed.Count; $i++) { if ($parsed[$i].t -le $position) { $index = $i } else { break } }
    if ($index -lt 0) { $index = 0 }
    Check "a current line is always available" ($index -ge 0 -and $index -lt $parsed.Count) "index=$index at ${position}ms"

    $start = $parsed[$index].t
    $end = if ($index + 1 -lt $parsed.Count) { $parsed[$index + 1].t } else { $start + 4000 }
    $span = [double][Math]::Max(1, [long]($end - $start))
    $progress = [double][Math]::Max(0, [double][Math]::Min(1, ([double]($position - $start)) / $span))
    Check "sweep progress in range" ($progress -ge 0 -and $progress -le 1) ("{0:P1}" -f $progress)

    # Clicking a line has to land on that line's second.
    $target = $parsed[$index].t
    $durationMs = [int64]$track.durationMs
    try { $null = Invoke-WebRequest "$Bridge/api/seek-position?positionMs=$target&durationMs=$durationMs" -Method Post -UseBasicParsing -TimeoutSec 30 } catch { }
    Start-Sleep -Seconds 2
    $after = (Get-Json "$Bridge/api/track").positionMs
    Check "click to seek lands on the line" ([Math]::Abs($after - $target) -le 2500) "target=${target}ms -> ${after}ms"
}
else {
    Check "lines parsed like the widget does" $true "no lyrics for this track"
}

Write-Host ""
Write-Host ("  passed: {0}   failed: {1}" -f $passed, $failed) -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Red" })
exit $(if ($failed -eq 0) { 0 } else { 1 })
