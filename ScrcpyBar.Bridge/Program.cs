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
        // Tab separated "timeMs<TAB>text" per line. The widget parses this with a plain string
        // split because the WinRT JSON projection cannot walk a JSON array.
        linesText = string.Join("\n", result.Lines.Select(line => $"{line.TimeMs}\t{line.Text}")),
        lines = result.Lines.Select(line => new { timeMs = line.TimeMs, text = line.Text })
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
    <title>Scrcpy Music Control</title>
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link href="https://fonts.googleapis.com/css2?family=Outfit:wght@400;600;700;800&display=swap" rel="stylesheet">
    <style>
        :root {
            --bg-color: #0b0f19;
            --card-bg: rgba(18, 24, 38, 0.75);
            --border-color: rgba(255, 255, 255, 0.1);
            --primary: #107c41; /* Xbox Green */
            --primary-glow: rgba(16, 124, 65, 0.45);
            --accent: #52b788;
            --text-main: #f3f4f6;
            --text-muted: #9ca3af;
        }

        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
            font-family: 'Outfit', sans-serif;
            -webkit-user-select: none;
            user-select: none;
        }

        body {
            background-color: var(--bg-color);
            background-image: 
                radial-gradient(circle at 10% 20%, rgba(16, 124, 65, 0.18) 0%, transparent 40%),
                radial-gradient(circle at 90% 80%, rgba(82, 183, 136, 0.12) 0%, transparent 40%);
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 20px;
            color: var(--text-main);
        }

        .widget-card {
            width: 100%;
            max-width: 380px;
            background: var(--card-bg);
            backdrop-filter: blur(24px);
            -webkit-backdrop-filter: blur(24px);
            border: 1px solid var(--border-color);
            border-radius: 20px;
            padding: 24px;
            box-shadow: 0 20px 50px rgba(0, 0, 0, 0.5), inset 0 1px 1px rgba(255, 255, 255, 0.15);
            display: flex;
            flex-direction: column;
            gap: 20px;
            transition: all 0.3s ease;
        }

        .header {
            display: flex;
            align-items: center;
            justify-content: space-between;
        }

        .brand {
            display: flex;
            align-items: center;
            gap: 8px;
            font-size: 13px;
            font-weight: 700;
            letter-spacing: 1px;
            text-transform: uppercase;
            color: var(--text-muted);
        }

        .brand-icon {
            width: 22px;
            height: 22px;
            background: var(--primary);
            border-radius: 50%;
            display: flex;
            align-items: center;
            justify-content: center;
            color: #fff;
            box-shadow: 0 0 10px var(--primary-glow);
        }

        .status-badge {
            display: inline-flex;
            align-items: center;
            gap: 6px;
            font-size: 12px;
            font-weight: 600;
            padding: 4px 10px;
            border-radius: 99px;
            background: rgba(255, 255, 255, 0.06);
            border: 1px solid rgba(255, 255, 255, 0.08);
        }

        .status-dot {
            width: 8px;
            height: 8px;
            border-radius: 50%;
            background: #ef4444;
            box-shadow: 0 0 8px #ef4444;
            transition: background 0.3s;
        }

        .status-dot.connected {
            background: #10b981;
            box-shadow: 0 0 8px #10b981;
        }

        .track-hero {
            display: flex;
            align-items: center;
            gap: 16px;
        }

        .art-box {
            width: 72px;
            height: 72px;
            border-radius: 14px;
            background: linear-gradient(135deg, #1f293d, #111827);
            border: 1px solid rgba(255, 255, 255, 0.1);
            display: flex;
            align-items: center;
            justify-content: center;
            flex-shrink: 0;
            box-shadow: 0 8px 16px rgba(0, 0, 0, 0.3);
            position: relative;
            overflow: hidden;
        }

        .art-box svg {
            width: 32px;
            height: 32px;
            fill: var(--text-muted);
            transition: all 0.3s ease;
        }

        .art-box.playing svg {
            fill: var(--accent);
            filter: drop-shadow(0 0 8px var(--primary-glow));
            animation: pulse 2s infinite ease-in-out;
        }

        @keyframes pulse {
            0%, 100% { transform: scale(1); }
            50% { transform: scale(1.08); }
        }

        .track-meta {
            overflow: hidden;
            display: flex;
            flex-direction: column;
            gap: 4px;
        }

        .track-title {
            font-size: 17px;
            font-weight: 700;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
            line-height: 1.25;
        }

        .track-artist {
            font-size: 14px;
            color: var(--text-muted);
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }

        .track-app {
            font-size: 11px;
            color: var(--accent);
            text-transform: uppercase;
            font-weight: 700;
            letter-spacing: 0.5px;
            margin-top: 2px;
        }

        .controls {
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 16px;
            padding-top: 6px;
        }

        .btn-ctrl {
            background: rgba(255, 255, 255, 0.07);
            border: 1px solid rgba(255, 255, 255, 0.08);
            border-radius: 50%;
            width: 44px;
            height: 44px;
            display: flex;
            align-items: center;
            justify-content: center;
            color: var(--text-main);
            cursor: pointer;
            transition: all 0.2s cubic-bezier(0.4, 0, 0.2, 1);
            outline: none;
        }

        .btn-ctrl:hover {
            background: rgba(255, 255, 255, 0.15);
            transform: scale(1.06);
            border-color: rgba(255, 255, 255, 0.2);
        }

.btn-ctrl:active {
        transform: scale(0.94);
    }

        .btn-play {
            width: 56px;
            height: 56px;
            background: var(--primary);
            border-color: rgba(255, 255, 255, 0.2);
            box-shadow: 0 4px 18px var(--primary-glow);
        }

        .btn-play:hover {
            background: #15803d;
            box-shadow: 0 6px 24px rgba(16, 124, 65, 0.6);
            transform: scale(1.08);
        }

        .btn-ctrl svg {
            width: 20px;
            height: 20px;
            fill: currentColor;
        }

        .btn-play svg {
            width: 26px;
            height: 26px;
        }

        .footer {
            display: flex;
            align-items: center;
            justify-content: space-between;
            font-size: 11px;
            color: var(--text-muted);
            border-top: 1px solid rgba(255, 255, 255, 0.05);
            padding-top: 14px;
        }

        .vol-group {
            display: flex;
            align-items: center;
            gap: 6px;
        }

        .vol-btn {
            background: rgba(255, 255, 255, 0.06);
            border: none;
            border-radius: 6px;
            color: var(--text-muted);
            width: 26px;
            height: 24px;
            cursor: pointer;
            font-size: 13px;
            font-weight: 700;
        }

        .vol-btn:hover {
            background: rgba(255, 255, 255, 0.15);
            color: #fff;
        }
    </style>
</head>
<body>

<div class="widget-card" id="widget">
    <div class="header">
        <div class="brand">
            <div class="brand-icon">
                <svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor">
                    <path d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8zm-2-12.5v7l6-3.5-6-3.5z"/>
                </svg>
            </div>
            <span>Scrcpy Bar</span>
        </div>
        <div class="status-badge">
            <span class="status-dot" id="statusDot"></span>
            <span id="statusText">Подключение...</span>
        </div>
    </div>

    <div class="track-hero">
        <div class="art-box" id="artBox">
            <svg viewBox="0 0 24 24">
                <path d="M12 3v10.55c-.59-.34-1.27-.55-2-.55-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4V7h4V3h-6z"/>
            </svg>
        </div>
        <div class="track-meta">
            <div class="track-title" id="trackTitle">Загрузка...</div>
            <div class="track-artist" id="trackArtist">Ожидание данных с телефона</div>
            <div class="track-app" id="trackApp">--</div>
        </div>
    </div>

<div class="controls">
        <button class="btn-ctrl" id="prevBtn" title="Предыдущий (Previous)" onclick="control('prev')">
            <svg viewBox="0 0 24 24"><path d="M6 6h2v12H6zm3.5 6l8.5 6V6z"/></svg>
        </button>
        <button class="btn-ctrl btn-play" id="playBtn" title="Пауза/Воспроизведение" onclick="control('play-pause')">
            <svg id="playIcon" viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg>
        </button>
        <button class="btn-ctrl" id="nextBtn" title="Следующий (Next)" onclick="control('next')">
            <svg viewBox="0 0 24 24"><path d="M6 18l8.5-6L6 6v12zM16 6v12h2V6h-2z"/></svg>
        </button>
    </div>

    <div class="footer">
        <span id="phoneStatus">ADB: OK</span>
        <div class="vol-group">
            <span>Громкость:</span>
            <button class="vol-btn" onclick="control('vol-down')">-</button>
            <span id="volLevel">13</span>
            <button class="vol-btn" onclick="control('vol-up')">+</button>
        </div>
    </div>
</div>

<script>
    let isPlaying = false;

    async function updateTrack() {
        try {
            const res = await fetch('/api/track');
            if (!res.ok) throw new Error('API error');
            const data = await res.json();
            applyData(data);
        } catch (e) {
            document.getElementById('statusDot').className = 'status-dot';
            document.getElementById('statusText').innerText = 'Офлайн';
            document.getElementById('trackTitle').innerText = 'Нет связи с мостом';
            document.getElementById('trackArtist').innerText = 'Проверь запуск ScrcpyBar.Bridge';
        }
    }

    function applyData(data) {
        const dot = document.getElementById('statusDot');
        const statusText = document.getElementById('statusText');
        const title = document.getElementById('trackTitle');
        const artist = document.getElementById('trackArtist');
        const app = document.getElementById('trackApp');
        const artBox = document.getElementById('artBox');
        const playIcon = document.getElementById('playIcon');
        const vol = document.getElementById('volLevel');

        if (!data.connected) {
            dot.className = 'status-dot';
            statusText.innerText = 'Нет телефона';
            title.innerText = 'Телефон не подключен';
            artist.innerText = data.message || 'Включи отладку по USB';
            app.innerText = '--';
            artBox.classList.remove('playing');
            return;
        }

        dot.className = 'status-dot connected';
        statusText.innerText = 'Онлайн';

        title.innerText = data.title || 'Нет трека';
        artist.innerText = data.artist || (data.hasTrack ? '' : 'Запусти музыку на телефоне');
        app.innerText = data.appName || data.packageName || '';

        isPlaying = data.isPlaying;
        if (isPlaying) {
            artBox.classList.add('playing');
            // Show pause icon: 2 vertical bars
            playIcon.innerHTML = '<path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z"/>';
        } else {
            artBox.classList.remove('playing');
            // Show play icon: triangle
            playIcon.innerHTML = '<path d="M8 5v14l11-7z"/>';
        }

        if (data.volumeCurrent !== undefined) {
            vol.innerText = data.volumeCurrent;
        }
    }

    async function control(action) {
        try {
            const res = await fetch('/api/' + action, { method: 'POST' });
            if (res.ok) {
                const data = await res.json();
                applyData(data);
            }
        } catch (e) {
            console.error(e);
        }
    }

    // Live polling every 1 second
    setInterval(updateTrack, 1000);
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
