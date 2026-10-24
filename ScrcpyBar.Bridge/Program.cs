using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ScrcpyBar.Bridge
{
    public static class Program
    {
        private static Mutex? _instanceMutex;

        [STAThread]
        public static void Main(string[] args)
        {
            var consoleMode = args.Any(a => a.Equals("--console", StringComparison.OrdinalIgnoreCase));

            _instanceMutex = new Mutex(true, "ScrcpyBar.Bridge.SingleInstance", out var granted);
            if (!granted)
            {
                if (consoleMode)
                {
                    AttachParentConsole();
                    Console.WriteLine("ScrcpyBar Bridge already running — check the tray icon.");
                    return;
                }

                MessageBox.Show(
                    "ScrcpyBar Bridge уже запущен — значок в трее рядом с часами.",
                    "ScrcpyBar",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var app = CreateApp(args);

            if (consoleMode)
            {
                // Debug mode re-attaches the console it was launched from (start_bridge.bat).
                AttachParentConsole();
                app.Run();
                return;
            }

            // Windowless mode: the app becomes a background service with a tray icon.
            // The host runs on a thread-pool task, the WinForms message loop owns the main thread.
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
            }
            catch
            {
            }

            var hostStart = Task.Run(async () => await app.StartAsync());
            SetupResources.ExtractAll();
            Application.Run(new TrayApp(app, hostStart));

            _instanceMutex.ReleaseMutex();
        }

        private static WebApplication CreateApp(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Everything the tray-less WinExe cannot see in a console window lands in the log file.
            builder.Logging.AddProvider(new FileLoggerProvider());


// Configure port: 39281 (dedicated port for ScrcpyBar)
builder.WebHost.UseUrls("http://127.0.0.1:39281");

builder.Services.AddSingleton<AdbService>();
builder.Services.AddSingleton<LyricsService>();
builder.Services.AddHostedService<SmtcSyncService>();
builder.Services.AddHostedService<LyricsWarmupService>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();
app.UseStaticFiles();

// API Endpoints
app.MapGet("/api/track", async (AdbService adb) =>
{
    var track = await adb.GetNowPlayingAsync();
    return Results.Ok(track);
});

app.MapPost("/api/play-pause", async (AdbService adb) =>
{
    await adb.PlayPauseAsync();
    await Task.Delay(350);
    var track = await adb.GetNowPlayingAsync(forceRefresh: true);
    return Results.Ok(track);
});

app.MapPost("/api/like", async (bool? value, AdbService adb) =>
{
    bool want = value ?? !(await adb.GetNowPlayingAsync()).IsLiked.GetValueOrDefault(false);
    bool applied = await adb.LikeAsync(want);
    await Task.Delay(350);
    var track = await adb.GetNowPlayingAsync(forceRefresh: true);
    return Results.Ok(new { ok = applied, track });
});

app.MapPost("/api/next", async (AdbService adb) =>
{
    await adb.NextTrackAsync();
    await Task.Delay(250); // Small grace period for Android to update session
    var track = await adb.GetNowPlayingAsync(forceRefresh: true);
    return Results.Ok(track);
});

app.MapPost("/api/prev", async (AdbService adb) =>
{
    await adb.PreviousTrackAsync();
    await Task.Delay(250);
    var track = await adb.GetNowPlayingAsync(forceRefresh: true);
    return Results.Ok(track);
});

app.MapPost("/api/seek", async (int? seconds, AdbService adb) =>
{
    int delta = Math.Clamp(seconds ?? 5, -300, 300);
    await adb.SeekRelativeAsync(delta);
    await Task.Delay(250);
    var track = await adb.GetNowPlayingAsync(forceRefresh: true);
    return Results.Ok(track);
});

app.MapPost("/api/rewind", async (AdbService adb) =>
{
    await adb.SeekRelativeAsync(-5);
    await Task.Delay(250);
    var track = await adb.GetNowPlayingAsync(forceRefresh: true);
    return Results.Ok(track);
});

app.MapPost("/api/fast-forward", async (AdbService adb) =>
{
    await adb.SeekRelativeAsync(5);
    await Task.Delay(250);
    var track = await adb.GetNowPlayingAsync(forceRefresh: true);
    return Results.Ok(track);
});

app.MapPost("/api/seek-position", async (long positionMs, long durationMs, AdbService adb) =>
{
    bool applied = await adb.SeekToPositionAsync(positionMs, durationMs);
    await Task.Delay(250);
    var track = await adb.GetNowPlayingAsync(forceRefresh: true);
    return Results.Ok(new { ok = applied, track });
});

app.MapGet("/api/cover", async (AdbService adb) =>
{
    var track = await adb.GetNowPlayingAsync();
    if (!track.HasTrack || string.IsNullOrWhiteSpace(track.AlbumArtPath) || !File.Exists(track.AlbumArtPath))
    {
        return Results.NotFound();
    }

    string contentType = Path.GetExtension(track.AlbumArtPath).Equals(".png", StringComparison.OrdinalIgnoreCase)
        ? "image/png"
        : "image/jpeg";
    return Results.File(track.AlbumArtPath, contentType);
});

app.MapGet("/api/lyrics", async (AdbService adb, LyricsService lyrics, bool? refresh, string? title, string? artist, long? durationMs) =>
{
    // The widget sends the track it is showing: at a track switch the bridge's cached
    // "now playing" can still be the previous song, and unscoped lookups returned lyrics
    // of the wrong track with a "not found" status.
    TrackInfo track;
    if (!string.IsNullOrWhiteSpace(title))
    {
        track = await adb.GetNowPlayingAsync();
        track = track with { Title = title, Artist = artist ?? track.Artist, DurationMs = durationMs ?? track.DurationMs };
    }
    else
    {
        track = await adb.GetNowPlayingAsync();
    }

    if (!track.HasTrack || string.IsNullOrWhiteSpace(track.Title))
    {
        return Results.Ok(new { ok = false, reason = "no track", lines = Array.Empty<object>() });
    }

    var result = await lyrics.GetAsync(track.Title, track.Artist, track.DurationMs, refresh == true);
    return Results.Ok(new
    {
        ok = result.Found,
        instrumental = result.Instrumental,
        synced = result.Lines.Count > 0,
        plain = result.Plain,
        source = result.Source,
        version = result.Version,
        // Tab separated "timeMs<TAB>text" per line. The widget parses this with a plain string
        // split because the WinRT JSON projection cannot walk a JSON array.
        linesText = string.Join("\n", result.Lines.Select(line => $"{line.TimeMs}\t{line.Text}")),
        lines = result.Lines.Select(line => new
        {
            timeMs = line.TimeMs,
            text = line.Text,
            words = line.Words.Select(word => new { t = word.TimeMs, e = word.EndMs, text = word.Text })
        })
    });
});

app.MapPost("/api/vol-up", async (AdbService adb) =>
{
    await adb.VolumeUpAsync();
    await Task.Delay(100);
    var track = await adb.GetNowPlayingAsync(forceRefresh: true);
    return Results.Ok(track);
});

app.MapPost("/api/vol-down", async (AdbService adb) =>
{
    await adb.VolumeDownAsync();
    await Task.Delay(100);
    var track = await adb.GetNowPlayingAsync(forceRefresh: true);
    return Results.Ok(track);
});

app.MapPost("/api/volume", async (int? level, AdbService adb) =>
{
    if (level.HasValue)
    {
        await adb.SetVolumeAsync(level.Value);
        await Task.Delay(100);
    }
    var track = await adb.GetNowPlayingAsync(forceRefresh: true);
    return Results.Ok(track);
});

// HTML Web Dashboard
app.MapGet("/", () => Results.Content(GetHtmlDashboard(), "text/html; charset=utf-8"));

return app;
        }

static string GetHtmlDashboard() => """
<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Scrcpy Bar</title>
<style>
:root { color-scheme: dark; --bg: #0a0e14; --card: rgba(20, 26, 38, 0.82); --line: rgba(255, 255, 255, 0.09); --green: #16a34a; --green-hi: #4ade80; --txt: #f3f4f6; --mut: #9ca3af; }
* { box-sizing: border-box; margin: 0; padding: 0; font-family: system-ui, 'Segoe UI', Roboto, sans-serif; -webkit-user-select: none; user-select: none; }
body { background: var(--bg); background-image: radial-gradient(120px 120px at 15% 10%, rgba(22, 163, 74, 0.25), transparent), radial-gradient(160px 160px at 85% 90%, rgba(74, 222, 128, 0.12), transparent); min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 12px; color: var(--txt); }
.card { width: 100%; max-width: 300px; background: var(--card); backdrop-filter: blur(20px); -webkit-backdrop-filter: blur(20px); border: 1px solid var(--line); border-radius: 16px; padding: 14px; display: flex; flex-direction: column; gap: 10px; box-shadow: 0 12px 32px rgba(0, 0, 0, 0.5); }
.top { display: flex; align-items: center; justify-content: space-between; }
.brand { display: flex; align-items: center; gap: 6px; font-size: 11px; font-weight: 700; letter-spacing: 1px; text-transform: uppercase; color: var(--mut); }
.dot { width: 7px; height: 7px; border-radius: 50%; background: #ef4444; box-shadow: 0 0 6px #ef4444; }
.dot.on { background: #22c55e; box-shadow: 0 0 6px #22c55e; }
.st { font-size: 11px; color: var(--mut); }
.hero { display: flex; align-items: center; gap: 10px; min-height: 52px; }
.art { width: 52px; height: 52px; border-radius: 10px; flex-shrink: 0; background: linear-gradient(135deg, #1e293b, #0f172a); border: 1px solid var(--line); display: flex; align-items: center; justify-content: center; overflow: hidden; }
.art svg { width: 24px; height: 24px; fill: var(--mut); }
.art img { width: 100%; height: 100%; object-fit: cover; display: none; }
.art.has-art svg { display: none; }
.art.has-art img { display: block; }
.art.playing svg { fill: var(--green-hi); animation: pulse 2s infinite ease-in-out; }
@keyframes pulse { 0%, 100% { transform: scale(1); } 50% { transform: scale(1.1); } }
.meta { overflow: hidden; flex: 1; min-width: 0; }
.title { font-size: 14px; font-weight: 700; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.artist { font-size: 12px; color: var(--mut); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.app { font-size: 10px; color: var(--green-hi); text-transform: uppercase; font-weight: 700; letter-spacing: 0.5px; }
.prog { height: 14px; display: flex; align-items: center; cursor: pointer; }
.bar { height: 3px; flex: 1; background: rgba(255, 255, 255, 0.12); border-radius: 99px; overflow: hidden; }
.fill { height: 100%; width: 0; background: linear-gradient(90deg, var(--green), var(--green-hi)); border-radius: 99px; }
.times { display: flex; justify-content: space-between; font-size: 10px; color: var(--mut); margin-top: -6px; }
.row { display: flex; align-items: center; justify-content: center; gap: 10px; }
.btn { width: 36px; height: 36px; border-radius: 50%; border: 1px solid var(--line); background: rgba(255, 255, 255, 0.06); color: var(--txt); cursor: pointer; display: flex; align-items: center; justify-content: center; transition: transform 0.15s, background 0.15s; outline: none; }
.btn:hover { background: rgba(255, 255, 255, 0.14); transform: scale(1.07); }
.btn:active { transform: scale(0.93); }
.btn svg { width: 16px; height: 16px; fill: currentColor; }
.play { width: 46px; height: 46px; background: var(--green); border-color: rgba(255, 255, 255, 0.2); box-shadow: 0 3px 14px rgba(22, 163, 74, 0.45); }
.play:hover { background: #15803d; }
.play svg { width: 20px; height: 20px; }
.like.liked { color: #f87171; border-color: rgba(248, 113, 113, 0.4); }
.like.liked svg { fill: #f87171; }
.bot { display: flex; align-items: center; justify-content: space-between; font-size: 10px; color: var(--mut); border-top: 1px solid rgba(255, 255, 255, 0.06); padding-top: 8px; }
.vol { display: flex; align-items: center; gap: 4px; }
.vbtn { background: rgba(255, 255, 255, 0.06); border: none; border-radius: 5px; color: var(--mut); min-width: 20px; height: 20px; cursor: pointer; font-size: 12px; font-weight: 700; }
.vbtn:hover { background: rgba(255, 255, 255, 0.15); color: #fff; }
.lyr-wrap { border-top: 1px solid rgba(255, 255, 255, 0.06); padding-top: 8px; display: flex; flex-direction: column; gap: 6px; }
.lyr-head { display: flex; justify-content: space-between; align-items: center; font-size: 10px; color: var(--mut); }
.lyr-box { max-height: 180px; overflow-y: auto; font-size: 12px; line-height: 1.7; scrollbar-width: thin; overscroll-behavior: contain; }
.lyr-line { color: var(--mut); white-space: pre-wrap; padding: 1px 6px; border-radius: 6px; }
.lyr-line.cur { color: #fff; font-weight: 700; background: rgba(22, 163, 74, 0.18); }
.lyr-plain { color: var(--mut); white-space: pre-wrap; }
.lyr-empty { color: var(--mut); font-size: 11px; }
.lyr-jump { background: rgba(22, 163, 74, 0.25); border: 1px solid rgba(74, 222, 128, 0.4); border-radius: 99px; color: #fff; font-size: 10px; padding: 2px 10px; cursor: pointer; display: none; }
.ghost-off { opacity: 0.45; }
@media (max-width: 420px) {
  body { padding: 8px; align-items: flex-start; }
  .card { max-width: 100%; border-radius: 14px; }
  .btn { width: 44px; height: 44px; }
  .btn svg { width: 19px; height: 19px; }
  .play { width: 56px; height: 56px; }
  .play svg { width: 24px; height: 24px; }
  .title { font-size: 15px; }
  .art { width: 56px; height: 56px; }
  .lyr-box { max-height: 42vh; font-size: 13px; }
}
</style>
</head>
<body>
<div class="card">
<div class="top"><div class="brand"><span class="dot" id="statusDot"></span>Scrcpy Bar</div><span class="st" id="statusText">...</span></div>
<div class="hero">
<div class="art" id="artBox"><svg viewBox="0 0 24 24"><path d="M12 3v10.55c-.59-.34-1.27-.55-2-.55-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4V7h4V3h-6z"/></svg><img id="coverImg" alt=""></div>
<div class="meta"><div class="title" id="trackTitle">Загрузка...</div><div class="artist" id="trackArtist">Ожидание данных</div><div class="app" id="trackApp"></div></div>
</div>
<div><div class="prog" id="progBar" title="Перемотать"><div class="bar"><div class="fill" id="progFill"></div></div></div><div class="times"><span id="posTime">0:00</span><span id="durTime">0:00</span></div></div>
<div class="row">
<button class="btn" onclick="control('prev')" title="Назад"><svg viewBox="0 0 24 24"><path d="M6 6h2v12H6zm3.5 6l8.5 6V6z"/></svg></button>
<button class="btn play" onclick="control('play-pause')" title="Пауза / играть"><svg id="playIcon" viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg></button>
<button class="btn" onclick="control('next')" title="Вперед"><svg viewBox="0 0 24 24"><path d="M6 18l8.5-6L6 6v12zM16 6v12h2V6h-2z"/></svg></button>
<button class="btn like" id="likeBtn" onclick="toggleLike()" title="Нравится" style="display:none"><svg viewBox="0 0 24 24"><path d="M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z"/></svg></button>
<button class="btn" id="lyrBtn" onclick="toggleLyrics()" title="Текст песни"><svg viewBox="0 0 24 24"><path d="M4 5h16v2H4zm0 4h16v2H4zm0 4h10v2H4zm0 4h10v2H4z"/></svg></button>
</div>
<div class="lyr-wrap" id="lyrWrap">
<div class="lyr-head"><span>Текст песни</span><span><span id="lyrSrc"></span> <button class="lyr-jump" id="lyrJump" onclick="jumpToCurrent()">▼ к строке</button></span></div>
<div class="lyr-box" id="lyrBox"><div class="lyr-empty">загрузка...</div></div>
</div>
<div class="bot"><span>USB &middot; ADB &middot; <span id="dashVer"></span></span><div class="vol"><button class="vbtn" onclick="control('vol-down')">-</button><span id="volLevel">-</span><button class="vbtn" onclick="control('vol-up')">+</button></div></div>
</div>
<script>
let isPlaying = false, isLiked = null, posMs = 0, durMs = 0, stampMs = 0, coverKey = '';
let lyrLines = [], lyrKey = '', lyrIdx = -1, lyrFollow = true, lyrAutoTarget = -1, lyrLastTop = -1, lyrAbort = null, lyrLoadingAt = 0;
try { if (localStorage.getItem('lyr') === '0') { document.getElementById('lyrWrap').style.display = 'none'; document.getElementById('lyrBtn').classList.add('ghost-off'); } } catch (e) {}
function toggleLyrics() {
    const w = document.getElementById('lyrWrap');
    const hidden = w.style.display === 'none';
    w.style.display = hidden ? 'flex' : 'none';
    document.getElementById('lyrBtn').classList.toggle('ghost-off', !hidden);
    try { localStorage.setItem('lyr', hidden ? '1' : '0'); } catch (e) {}
}
document.getElementById('lyrBox').addEventListener('scroll', () => {
    // Our own smooth scrolls fire scroll events too. They always move toward
    // the follow target, so only a scroll that moves away (or stalls off it)
    // means the user took over — no time windows, nothing to mistime.
    const box = $('lyrBox');
    const top = box.scrollTop;
    if (Math.abs(top - lyrAutoTarget) <= 2) { lyrLastTop = top; return; }
    const closing = lyrLastTop >= 0 && Math.abs(top - lyrAutoTarget) < Math.abs(lyrLastTop - lyrAutoTarget) - 1;
    lyrLastTop = top;
    if (!closing) lyrFollow = false;
}, { passive: true });
function lineTop(el) {
    // offsetTop is relative to the offsetParent (here: way above the box),
    // so it cannot drive box.scrollTop. Measure against the box itself.
    const box = $('lyrBox');
    return el.getBoundingClientRect().top - box.getBoundingClientRect().top + box.scrollTop;
}
function followLine(el, instant) {
    const box = $('lyrBox');
    const target = lineTop(el) - box.clientHeight / 2 + el.clientHeight / 2;
    lyrAutoTarget = target;
    box.scrollTo({ top: target, behavior: instant ? 'auto' : 'smooth' });
}
function jumpToCurrent() {
    const ch = $('lyrBox').children;
    lyrFollow = true;
    if (lyrIdx >= 0 && ch[lyrIdx]) followLine(ch[lyrIdx], true);
    updateJumpBtn();
}
function updateJumpBtn() {
    const box = $('lyrBox'), btn = $('lyrJump'), ch = box.children;
    const el = lyrIdx >= 0 ? ch[lyrIdx] : null;
    let out = false;
    if (el) {
        const r = el.getBoundingClientRect(), b = box.getBoundingClientRect();
        out = r.top < b.top - 1 || r.bottom > b.bottom + 1;
    }
    btn.style.display = out ? 'inline-block' : 'none';
}
async function loadLyrics() {
    const box = $('lyrBox');
    if (lyrAbort) { try { lyrAbort.abort(); } catch (e) {} }
    lyrAbort = new AbortController();
    lyrLoadingAt = Date.now();
    try {
        const res = await fetch('/api/lyrics', { signal: lyrAbort.signal });
        if (!res.ok) throw 0;
        const d = await res.json();
        $('lyrSrc').innerText = d.source ? '· ' + d.source : '';
        box.innerHTML = '';
        lyrLines = [];
        lyrIdx = -1;
        lyrFollow = true;
        // Clearing the box snaps scrollTop to 0 and fires a scroll event:
        // aim the target there upfront so the reset is not mistaken for the user.
        lyrAutoTarget = 0;
        lyrLastTop = -1;
        if (d.synced && d.lines && d.lines.length) {
            lyrLines = d.lines;
            d.lines.forEach(l => {
                const div = document.createElement('div');
                div.className = 'lyr-line';
                div.textContent = (l.text ?? l.Text) || '';
                box.appendChild(div);
            });
        } else if (d.plain) {
            d.plain.split('\n').forEach(t => {
                const div = document.createElement('div');
                div.className = 'lyr-plain';
                div.textContent = t;
                box.appendChild(div);
            });
        } else {
            box.innerHTML = '<div class="lyr-empty">текста нет</div>';
        }
    } catch (e) {
        if (e && e.name === 'AbortError') return; // superseded by a newer track, not a failure
        box.innerHTML = '<div class="lyr-empty">текста нет</div>';
        lyrLines = [];
        lyrIdx = -1;
    }
}
const $ = id => document.getElementById(id);
window.__lyrState = () => ({ idx: lyrIdx, follow: lyrFollow, lines: lyrLines.length });
$('dashVer').innerText = 'v1.1.1e';
const fmt = ms => { if (!ms || ms < 0) ms = 0; const s = Math.floor(ms / 1000); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); };
const curPos = () => posMs + (isPlaying ? Date.now() - stampMs : 0);
async function updateTrack() {
    try {
        const res = await fetch('/api/track');
        if (!res.ok) throw 0;
        applyData(await res.json());
        // Self-heal: if a lookup hung or died while the page stayed open, retry.
        if (lyrLines.length === 0 && !$('lyrBox').querySelector('.lyr-line,.lyr-plain')
            && Date.now() - lyrLoadingAt > 10000) loadLyrics();
    } catch (e) {
        $('statusDot').className = 'dot';
        $('statusText').innerText = 'офлайн';
    }
}
function applyData(d) {
    const dot = $('statusDot');
    if (!d.connected) {
        dot.className = 'dot';
        $('statusText').innerText = 'нет телефона';
        $('trackTitle').innerText = 'Телефон не подключен';
        $('trackArtist').innerText = d.message || 'Включи отладку по USB';
        $('trackApp').innerText = '';
        $('posTime').innerText = '0:00'; $('durTime').innerText = '0:00'; $('progFill').style.width = '0';
        $('artBox').classList.remove('playing');
        return;
    }
    dot.className = 'dot on';
    $('statusText').innerText = 'онлайн';
    $('trackTitle').innerText = d.title || 'Нет трека';
    $('trackArtist').innerText = d.artist || (d.hasTrack ? '' : 'Запусти музыку на телефоне');
    $('trackApp').innerText = d.appName || d.packageName || '';
    isPlaying = !!d.isPlaying;
    $('artBox').classList.toggle('playing', isPlaying);
    $('playIcon').innerHTML = isPlaying ? '<path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z"/>' : '<path d="M8 5v14l11-7z"/>';
    if (d.volumeCurrent !== undefined) $('volLevel').innerText = d.volumeCurrent;
    posMs = (d.positionMs ?? d.PositionMs) || 0;
    durMs = (d.durationMs ?? d.DurationMs) || 0;
    stampMs = Date.now();
    const key = (d.title || '') + '|' + (d.artist || '');
    if (key !== coverKey) {
        coverKey = key;
        const img = $('coverImg');
        img.onload = () => $('artBox').classList.add('has-art');
        img.onerror = () => $('artBox').classList.remove('has-art');
        img.src = '/api/cover?k=' + Date.now();
    }
    if (key !== lyrKey) { lyrKey = key; loadLyrics(); }
    isLiked = d.isLiked === true ? true : d.isLiked === false ? false : null;
    const lb = $('likeBtn');
    lb.style.display = isLiked === null ? 'none' : 'flex';
    lb.classList.toggle('liked', isLiked === true);
    paintProg();
}
function paintProg() {
    const p = curPos();
    $('durTime').innerText = fmt(durMs);
    $('posTime').innerText = fmt(Math.min(p, durMs || p));
    $('progFill').style.width = (durMs > 0 ? Math.max(0, Math.min(1, p / durMs)) * 100 : 0) + '%';
    if (lyrLines.length) {
        let idx = 0;
        for (let i = 0; i < lyrLines.length; i++) {
            const t = Number(lyrLines[i].timeMs ?? lyrLines[i].TimeMs) || 0;
            if (p >= t) idx = i; else break;
        }
        if (idx !== lyrIdx) {
            const ch = $('lyrBox').children;
            if (lyrIdx >= 0 && ch[lyrIdx]) ch[lyrIdx].classList.remove('cur');
            const jump = Math.abs(idx - lyrIdx) > 3;
            lyrIdx = idx;
            if (ch[idx]) {
                ch[idx].classList.add('cur');
                if (isPlaying && lyrFollow) followLine(ch[idx], jump);
            }
        }
        updateJumpBtn();
        // Watchdog: the highlight must never silently vanish (desynced DOM,
        // throttled tab, missed tick) — restore it instead of staying dark.
        if (lyrIdx >= 0 && !$('lyrBox').querySelector('.cur')) {
            const chd = $('lyrBox').children;
            if (chd[lyrIdx]) chd[lyrIdx].classList.add('cur');
        }
    }
}
async function control(action) {
    try {
        const res = await fetch('/api/' + action, { method: 'POST' });
        if (res.ok) applyData(await res.json());
    } catch (e) { /* offline */ }
}
async function toggleLike() {
    try {
        const res = await fetch('/api/like', { method: 'POST' });
        if (res.ok) { const d = await res.json(); applyData(d.track || d); }
    } catch (e) { /* offline */ }
}
$('progBar').addEventListener('click', async e => {
    if (!durMs || durMs <= 0) return;
    const r = $('progBar').getBoundingClientRect();
    const target = Math.floor(durMs * Math.max(0, Math.min(1, (e.clientX - r.left) / r.width)));
    try {
        const res = await fetch('/api/seek-position?positionMs=' + target + '&durationMs=' + durMs, { method: 'POST' });
        if (res.ok) { const d = await res.json(); if (d.track) applyData(d.track); }
    } catch (err) { /* offline */ }
});
setInterval(updateTrack, 2000);
setInterval(paintProg, 250);
updateTrack();
</script>
</body>
</html>
""";

        private static void AttachParentConsole()
        {
            try
            {
                _ = AttachConsole(AttachParentProcess);
            }
            catch
            {
            }
        }

        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);
    }
}
