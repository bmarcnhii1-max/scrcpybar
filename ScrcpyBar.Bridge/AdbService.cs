using System;
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ScrcpyBar.Bridge;

public record TrackInfo(
    bool Connected,
    bool HasTrack,
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    string PackageName,
    string AppName,
    int VolumeCurrent,
    int VolumeMax,
    string Message = ""
)
{
    public long PositionMs { get; init; }
    public long DurationMs { get; init; }
    public long UpdatedMs { get; init; }
    public double Speed { get; init; } = 1.0;
    public string AlbumArtPath { get; init; } = "";
}

public class AdbService
{
    private readonly string _adbPath;
    private readonly ILogger<AdbService> _logger;
    private TrackInfo? _cachedInfo;
    private DateTime _lastCacheTime = DateTime.MinValue;
    private readonly Dictionary<string, long> _durationCache = new();
    private DateTime _lastDurationProbe = DateTime.MinValue;
    private long _lastKnownDurationMs;
    private string _lastUiXml = "";
    private DateTime _lastUiXmlTime = DateTime.MinValue;
    private DateTime _lastArtProbe = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private HttpClient _companionHttp = CreateCompanionHttp();
    private bool _companionForwarded;
    private DateTime _companionRetryAfter = DateTime.MinValue;

    private static HttpClient CreateCompanionHttp() => new HttpClient
    {
        BaseAddress = new Uri("http://127.0.0.1:39282"),
        // A seek has to travel to the phone and the app has to apply it, which regularly takes
        // longer than two seconds when the phone is busy.
        Timeout = TimeSpan.FromSeconds(8)
    };


    public AdbService(ILogger<AdbService> logger)
    {
        _logger = logger;
        _adbPath = ResolveAdbPath();
        _logger.LogInformation("Using ADB Path: {Path}", _adbPath);
    }

    private static string ResolveAdbPath()
    {
        // 1. Direct path in scrcpy folder
        string[] candidateDirs = [
            @"D:\scrcpy-win64-v4.0",
            @"C:\scrcpy-win64-v4.0",
            @"D:\scrcpy",
            @"C:\scrcpy"
        ];

        foreach (var dir in candidateDirs)
        {
            var p = Path.Combine(dir, "adb.exe");
            if (File.Exists(p)) return p;
        }

        // 2. Check current directory or parent directories
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var p = Path.Combine(current, "adb.exe");
            if (File.Exists(p)) return p;
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        // 3. Fallback to adb in PATH
        return "adb";
    }

    public async Task<string> RunAdbCommandAsync(string arguments, int timeoutMs = 4000)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _adbPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        try
        {
            process.Start();
            using var cts = new CancellationTokenSource(timeoutMs);
            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            // stderr has to be drained too: if the process writes a lot of it and nobody
            // reads, the pipe fills up and adb deadlocks until the timeout.
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            await outputTask;
            await errorTask;
            return await outputTask;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            _logger.LogWarning("ADB command timed out after {Timeout}ms: {Args}", timeoutMs, arguments);
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("ADB command failed ({Args}): {Message}", arguments, ex.Message);
            return string.Empty;
        }
    }

    public async Task<byte[]?> RunAdbBinaryAsync(string arguments, int timeoutMs = 6000)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _adbPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            process.Start();
            using var cts = new CancellationTokenSource(timeoutMs);
            using var output = new MemoryStream();
            Task copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cts.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            await copyTask;
            await errorTask;
            return output.ToArray();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("ADB binary command failed ({Args}): {Message}", arguments, ex.Message);
            return null;
        }
    }

    // ---------------------------------------------------------------- multi device

    private string _deviceSerial = "";
    private string _forwardedSerial = "";
    private string _companionSetupDoneFor = "";
    private string _companionStartedFor = "";
    private string? _preferredDevice;
    private DateTime _deviceCheckedAt = DateTime.MinValue;

    private static readonly TimeSpan DeviceReselectInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Prefixes an adb command with the selected device. Without it adb silently picks the first
    /// attached phone, which is the wrong one as soon as two devices are plugged in.
    /// </summary>
    private string Adb(string arguments) =>
        string.IsNullOrEmpty(_deviceSerial) ? arguments : $"-s {_deviceSerial} {arguments}";

    private async Task<List<string>> ListDeviceSerialsAsync()
    {
        var serials = new List<string>();
        string output = await RunAdbCommandAsync("devices", 2500);

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 ||
                line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("*", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1] == "device")
            {
                serials.Add(parts[0]);
            }
        }

        return serials;
    }

    /// <summary>
    /// The phone the bridge should use when more than one is attached. Read from "device.txt"

    /// next to the executable, then from SCRCPYBAR_DEVICE. A serial or a serial prefix work.
    /// </summary>
    private string GetPreferredDevice()
    {
        if (_preferredDevice != null)
        {
            return _preferredDevice;
        }

        string value = "";

        try
        {
            string file = Path.Combine(AppContext.BaseDirectory, "device.txt");
            if (File.Exists(file))
            {
                // Read line by line, skip empty lines and comments starting with '#'.
                foreach (var line in File.ReadAllLines(file))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    {
                        continue;
                    }
                    value = trimmed;
                    break;
                }
            }
        }
        catch
        {
        }

        if (value.Length == 0)
        {
            value = Environment.GetEnvironmentVariable("SCRCPYBAR_DEVICE")?.Trim() ?? "";
        }

        _preferredDevice = value;
        return _preferredDevice;
    }

    private async Task<bool> ResolveDeviceAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _deviceCheckedAt < DeviceReselectInterval)
        {
            return !string.IsNullOrEmpty(_deviceSerial);
        }

        _deviceCheckedAt = DateTime.UtcNow;
        var serials = await ListDeviceSerialsAsync();

        if (serials.Count == 0)
        {
            if (_deviceSerial != "")
            {
                _logger.LogInformation("Phone disconnected");
                _deviceSerial = "";
                _forwardedSerial = "";
                _companionForwarded = false;
                _cachedInfo = null;
            }
            return false;
        }

        string chosen;
        if (serials.Count == 1)
        {
            chosen = serials[0];
        }
        else
        {
            string preference = GetPreferredDevice();
            string? preferred = serials.FirstOrDefault(s =>
                s.Equals(preference, StringComparison.OrdinalIgnoreCase) ||
                (preference.Length > 0 && s.StartsWith(preference, StringComparison.OrdinalIgnoreCase)));

            if (preferred == null)
            {
                chosen = serials[0];
                _logger.LogWarning(
                    "Preferred phone '{Preference}' is not connected, falling back to {Serial}",
                    preference, chosen);
            }
            else
            {
                chosen = preferred;
                _logger.LogInformation("Using preferred phone {Serial}", chosen);
            }
        }

        if (chosen != _deviceSerial)
        {
            _logger.LogInformation("Selected phone: {Serial}", chosen);

            _deviceSerial = chosen;
            _companionForwarded = false;
            _cachedInfo = null;
        }

        await EnsureCompanionInstalledAsync(_deviceSerial);

        return true;
    }


    /// <summary>
    /// Makes sure the companion app is present on the selected phone. Without it the bridge
    /// falls back to parsing dumpsys, which is slower and cannot seek precisely.
    /// </summary>
    private DateTime _companionSetupRetryAfter = DateTime.MinValue;

    private async Task EnsureCompanionInstalledAsync(string serial)
    {
        if (_companionSetupDoneFor == serial)
        {
            return;
        }
        if (DateTime.UtcNow < _companionSetupRetryAfter && _companionSetupRetryFor == serial)
        {
            return;
        }

        const string package = "com.scrcpybar.companion";
        const string listener = "com.scrcpybar.companion/com.scrcpybar.companion.media.ScrcpyMediaListenerService";

        string packages = await RunAdbCommandAsync($"-s {serial} shell pm list packages {package}", 4000);
        if (packages.Contains(package))
        {
            // Granting the notification listener access works over adb, which saves a manual trip
            // through the settings on every new phone.
            await RunAdbCommandAsync($"-s {serial} shell cmd notification allow_listener {listener}", 4000);
            _companionSetupDoneFor = serial;
            return;
        }

        string? apk = FindCompanionApk();
        if (apk == null)
        {
            _logger.LogWarning("Companion APK not found, cannot install it on {Serial}", serial);
            _companionSetupRetryFor = serial;
            _companionSetupRetryAfter = DateTime.UtcNow.AddSeconds(60);
            return;
        }

        _logger.LogInformation("Installing the companion app on {Serial}", serial);
        string install = await RunAdbCommandAsync($"-s {serial} install -r \"{apk}\"", 90000);
        if (!install.Contains("Success"))
        {
            _logger.LogWarning(
                "Could not install the companion on {Serial}: {Result}. Install the APK by hand or allow adb installs in the developer options.",
                serial, install.Trim());
            // Retry later: a manual install or a developer-options change should get another chance.
            _companionSetupRetryFor = serial;
            _companionSetupRetryAfter = DateTime.UtcNow.AddSeconds(60);
            return;
        }

        await RunAdbCommandAsync($"-s {serial} shell cmd notification allow_listener {listener}", 4000);
        _companionSetupDoneFor = serial;
    }

    private string _companionSetupRetryFor = "";

    /// <summary>Manual companion install from the first-run setup window. Returns a human-readable result.</summary>
    public async Task<string> SetupCompanionNowAsync()
    {
        if (!await ResolveDeviceAsync(force: true))
        {
            return "Телефон не найден — проверь кабель и включи «Отладку по USB»";
        }

        await EnsureCompanionInstalledAsync(_deviceSerial);
        _companionStartedFor = "";
        await StartCompanionAppAsync(_deviceSerial);

        bool ok = await EnsureCompanionAsync();
        return ok
            ? "OK"
            : "Companion не отвечает — открой приложение на телефоне и выдай «Доступ к уведомлениям»";
    }

    /// <summary>Starts the companion app so its foreground service brings up the HTTP server.</summary>
    private async Task StartCompanionAppAsync(string serial)
    {
        if (_companionStartedFor == serial)
        {
            return;
        }
        _companionStartedFor = serial;

        await RunAdbCommandAsync(
            $"-s {serial} shell monkey -p com.scrcpybar.companion -c android.intent.category.LAUNCHER 1", 6000);
    }

    private string? FindCompanionApk()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "ScrcpyBar.Android", "app", "build", "outputs", "apk", "debug", "app-debug.apk"),
            Path.Combine(AppContext.BaseDirectory, "companion.apk"),
            // The single-file exe unpacks its embedded APK into %LOCALAPPDATA%\ScrcpyBar.
            Path.Combine(AppPaths.DataDir, "companion.apk")
        };

        foreach (var candidate in candidates)
        {
            string full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    private async Task<bool> EnsureCompanionAsync()
    {
        if (_companionForwarded)
        {
            return true;
        }
        if (DateTime.UtcNow < _companionRetryAfter)

        {
            return false;
        }

        // A forward that survived a phone reconnect can point at a dead socket, so drop it
        // before re-creating it instead of trusting whatever adb still has in its table.
        // The old owner has to release the local port first, otherwise binding it for the
        // other phone fails with "cannot bind listener".
        if (_forwardedSerial != "" && _forwardedSerial != _deviceSerial)
        {
            await RunAdbCommandAsync($"-s {_forwardedSerial} forward --remove tcp:39282", 2000);
        }
        await RunAdbCommandAsync(Adb("forward --remove tcp:39282"), 2000);
        await RunAdbCommandAsync(Adb("forward tcp:39282 tcp:38080"), 2000);
        _forwardedSerial = _deviceSerial;

        // The old socket may still be pooled in the HttpClient and would fail on the next
        // request, so the connection pool is replaced together with the forward.
        _companionHttp.Dispose();
        _companionHttp = CreateCompanionHttp();

        try
        {
            using var response = await _companionHttp.GetAsync("/health");
            if (response.IsSuccessStatusCode)
            {
                _companionForwarded = true;
                _companionRetryAfter = DateTime.MinValue;
                return true;
            }
        }
        catch
        {
        }

        // The companion may simply not be running yet on this phone.
        await StartCompanionAppAsync(_deviceSerial);
        await Task.Delay(1500);

        try
        {
            using var response = await _companionHttp.GetAsync("/health");
            if (response.IsSuccessStatusCode)
            {
                _companionForwarded = true;
                _companionRetryAfter = DateTime.MinValue;
                return true;
            }
        }
        catch
        {
        }

        _companionRetryAfter = DateTime.UtcNow.AddSeconds(5);
        return false;
    }


    /// <summary>
    /// Marks the companion as unreachable so the next call re-creates the adb forward and probes
    /// the phone again. Without this the bridge keeps a stale "forwarded" flag and silently
    /// falls back to the slower dumpsys path for as long as the tunnel stays broken.
    /// </summary>
    private void InvalidateCompanion(string reason)
    {
        if (_companionForwarded)
        {
            _logger.LogWarning("Companion marked unreachable: {Reason}", reason);
        }
        _companionForwarded = false;
        _companionRetryAfter = DateTime.UtcNow.AddSeconds(5);
    }

    private async Task<TrackInfo?> TryGetCompanionTrackAsync()
    {
        if (!await EnsureCompanionAsync())
        {
            return null;
        }

        try
        {
            using var response = await _companionHttp.GetAsync("/state");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            if (!document.RootElement.TryGetProperty("session", out var session) ||
                session.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string packageName = JsonString(session, "packageName");
            string title = JsonString(session, "title");
            string artist = JsonString(session, "artist");
            string album = JsonString(session, "album");
            string appName = JsonString(session, "appLabel", packageName);
            bool hasTrack = !string.IsNullOrWhiteSpace(title);
            long positionMs = JsonInt64(session, "positionMs");
            long durationMs = JsonInt64(session, "durationMs");
            bool isPlaying = JsonBoolean(session, "isPlaying");

            return new TrackInfo(
                Connected: true,
                HasTrack: hasTrack,
                Title: title,
                Artist: artist,
                Album: album,
                IsPlaying: isPlaying,
                PackageName: packageName,
                AppName: string.IsNullOrWhiteSpace(appName) ? packageName : appName,
                VolumeCurrent: 0,
                VolumeMax: 15
            )
            {
                PositionMs = positionMs,
                DurationMs = durationMs,
                UpdatedMs = 0,
                Speed = JsonDouble(session, "playbackSpeed", 1.0)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Companion /state failed: {Message}", ex.Message);
            InvalidateCompanion(ex.Message);
            return null;
        }
    }

    private async Task<bool> TryCompanionControlAsync(string action, long? positionMs = null)
    {
        if (!await EnsureCompanionAsync())
        {
            return false;
        }

        try
        {
            string body = positionMs.HasValue
                ? $"{{\"action\":\"{action}\",\"positionMs\":{positionMs.Value}}}"
                : $"{{\"action\":\"{action}\"}}";
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _companionHttp.PostAsync("/control", content);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Companion /control {Action} failed: {Message}", action, ex.Message);
            InvalidateCompanion(ex.Message);
            return false;
        }
    }

    private static string JsonString(JsonElement element, string name, string fallback = "")
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static long JsonInt64(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetInt64(out long result)
            ? result
            : 0;
    }

    private static double JsonDouble(JsonElement element, string name, double fallback)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetDouble(out double result)
            ? result
            : fallback;
    }

    private static bool JsonBoolean(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    public async Task<TrackInfo> GetNowPlayingAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedInfo != null && (DateTime.UtcNow - _lastCacheTime).TotalMilliseconds < 800)
        {
            return _cachedInfo;
        }

        await _lock.WaitAsync();
        try
        {
            if (!forceRefresh && _cachedInfo != null && (DateTime.UtcNow - _lastCacheTime).TotalMilliseconds < 800)
            {
                return _cachedInfo;
            }

            bool anyDevice = await ResolveDeviceAsync();
            if (!anyDevice)
            {
                _cachedInfo = new TrackInfo(
                    Connected: false,
                    HasTrack: false,
                    Title: "",
                    Artist: "",
                    Album: "",
                    IsPlaying: false,
                    PackageName: "",
                    AppName: "",
                    VolumeCurrent: 0,
                    VolumeMax: 15,
                    Message: "Телефон не подключен или выключена отладка по USB"
                );
                _lastCacheTime = DateTime.UtcNow;
                return _cachedInfo;
            }

            var companionTrack = await TryGetCompanionTrackAsync();
            if (companionTrack != null)
            {
                var (companionVolume, companionVolumeMax) = await GetVolumeAsync();
                string companionArtwork = await GetCompanionArtworkPathAsync(companionTrack);
                _cachedInfo = companionTrack with
                {
                    VolumeCurrent = companionVolume,
                    VolumeMax = companionVolumeMax,
                    AlbumArtPath = companionArtwork
                };
                _lastCacheTime = DateTime.UtcNow;
                return _cachedInfo;
            }

            var dumpOutput = await RunAdbCommandAsync(Adb("shell dumpsys media_session"), 3000);
            var track = ParseMediaSession(dumpOutput);

            // Fetch volume
            var durationMs = await GetTrackDurationAsync(track);
            long positionMs = await GetEstimatedPositionAsync(track);
            string albumArtPath = await GetAlbumArtPathAsync(track);

            var (volCur, volMax) = await GetVolumeAsync();

            _cachedInfo = track with
            {
                Connected = true,
                VolumeCurrent = volCur,
                VolumeMax = volMax,
                DurationMs = durationMs,
                PositionMs = positionMs,
                AlbumArtPath = albumArtPath
            };
            _lastCacheTime = DateTime.UtcNow;
            return _cachedInfo;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> GetUiSnapshotAsync()
        {
            if (!string.IsNullOrEmpty(_lastUiXml) && (DateTime.UtcNow - _lastUiXmlTime).TotalSeconds < 2)
            {
                return _lastUiXml;
            }

            await RunAdbCommandAsync(Adb("shell uiautomator dump /sdcard/scrcpybar_window.xml"), 4000);
            _lastUiXml = await RunAdbCommandAsync(Adb("shell cat /sdcard/scrcpybar_window.xml"), 4000);
            _lastUiXmlTime = DateTime.UtcNow;
            return _lastUiXml;
        }

        private async Task<long> GetTrackDurationAsync(TrackInfo track)
        {
            if (!track.HasTrack || string.IsNullOrWhiteSpace(track.Title))
            {
                return 0;
            }

            string key = $"{track.PackageName}|{track.Title}|{track.Artist}";
            if (_durationCache.TryGetValue(key, out long cached))
            {
                return cached;
            }

            if ((DateTime.UtcNow - _lastDurationProbe).TotalSeconds < 5)
            {
                return _lastKnownDurationMs;
            }

            _lastDurationProbe = DateTime.UtcNow;
            string xml = await GetUiSnapshotAsync();
            long duration = ParseDurationFromSeekBar(xml);
            if (duration > 0)
            {
                if (_durationCache.Count > 500)
                {
                    _durationCache.Clear();
                }
                _durationCache[key] = duration;
                _lastKnownDurationMs = duration;
            }

            return duration;
        }

        private static long ParseDurationFromSeekBar(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                return 0;
            }

            var seekBar = Regex.Match(xml, "class=\\\"android\\.widget\\.SeekBar\\\"[^>]*content-desc=\\\"([^\\\"]*)\\\"");
            if (!seekBar.Success)
            {
                return 0;
            }

            var numbers = Regex.Matches(seekBar.Groups[1].Value, @"\d+")
                .Select(x => x.Value)
                .ToArray();
            if (numbers.Length < 2)
            {
                return 0;
            }

            // The last group is either "M:SS" or "H:MM:SS" for tracks longer than an hour.
            // Taking only the last two numbers would read "1:02:03" as 2:03.
            if (numbers.Length >= 3 &&
                int.TryParse(numbers[^3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours))
            {
                if (!int.TryParse(numbers[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) ||
                    !int.TryParse(numbers[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
                {
                    return 0;
                }
                return Math.Max(0, (hours * 3600L) + (minutes * 60L) + seconds) * 1000L;
            }

            if (!int.TryParse(numbers[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mins) ||
                !int.TryParse(numbers[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int secs))
            {
                return 0;
            }

            return Math.Max(0, (mins * 60L) + secs) * 1000L;
        }

        private async Task<string> GetCompanionArtworkPathAsync(TrackInfo track)
        {
            if (!track.HasTrack || string.IsNullOrWhiteSpace(track.PackageName) || !await EnsureCompanionAsync())
            {
                return "";
            }

            try
            {
                string key = $"{track.PackageName}|{track.Title}|{track.Artist}";
                string coverDirectory = AppPaths.DataSubDir("cover-cache");
                string coverPath = Path.Combine(coverDirectory, StableHash(key) + ".jpg");
                if (File.Exists(coverPath))
                {
                    return coverPath;
                }

                using var response = await _companionHttp.GetAsync($"/artwork?packageName={Uri.EscapeDataString(track.PackageName)}");
                if (!response.IsSuccessStatusCode)
                {
                    return "";
                }

                byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0)
                {
                    return "";
                }

                Directory.CreateDirectory(coverDirectory);
                await File.WriteAllBytesAsync(coverPath, bytes);
                return coverPath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Companion artwork failed: {Message}", ex.Message);
                InvalidateCompanion(ex.Message);
                return "";
            }
        }

        private async Task<string> GetAlbumArtPathAsync(TrackInfo track)
        {
            if (!track.HasTrack || string.IsNullOrWhiteSpace(track.Title))
            {
                return "";
            }

            string key = $"{track.PackageName}|{track.Title}|{track.Artist}";
            string coverDirectory = AppPaths.DataSubDir("cover-cache");
            string coverPath = Path.Combine(coverDirectory, StableHash(key) + ".png");
            if (File.Exists(coverPath))
            {
                return coverPath;
            }

            if ((DateTime.UtcNow - _lastArtProbe).TotalSeconds < 15)
            {
                return "";
            }

            _lastArtProbe = DateTime.UtcNow;
            string xml = await GetUiSnapshotAsync();
            var bounds = ParseAlbumArtBounds(xml);
            if (!bounds.HasValue)
            {
                return "";
            }

            byte[]? screenshot = await RunAdbBinaryAsync(Adb("exec-out screencap -p"), 7000);
            if (screenshot == null || screenshot.Length == 0)
            {
                return "";
            }

            try
            {
                Directory.CreateDirectory(coverDirectory);
                var (left, top, right, bottom) = bounds.Value;
                using var source = Image.FromStream(new MemoryStream(screenshot));
                int x = Math.Clamp(left, 0, Math.Max(0, source.Width - 1));
                int y = Math.Clamp(top, 0, Math.Max(0, source.Height - 1));
                int width = Math.Clamp(right - x, 1, source.Width - x);
                int height = Math.Clamp(bottom - y, 1, source.Height - y);
                using var cover = new Bitmap(width, height);
                using (var graphics = Graphics.FromImage(cover))
                {
                    graphics.DrawImage(source, new Rectangle(0, 0, width, height), new Rectangle(x, y, width, height), GraphicsUnit.Pixel);
                }
                cover.Save(coverPath, ImageFormat.Png);
                return coverPath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Album art capture failed: {Message}", ex.Message);
                return "";
            }
        }

        private static (int Left, int Top, int Right, int Bottom)? ParseAlbumArtBounds(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                return null;
            }

            var preferred = Regex.Matches(xml, @"<node[^>]*resource-id=""[^""]*(?:player_thumbnail|album_art|cover|thumbnail)[^""]*""[^>]*bounds=""\[(\d+),(\d+)\]\[(\d+),(\d+)\]""[^>]*>");
            var best = preferred
                .Cast<Match>()
                .Select(ParseBounds)
                .Where(bounds => bounds.HasValue && (bounds.Value.Right - bounds.Value.Left) * (bounds.Value.Bottom - bounds.Value.Top) > 10000)
                .OrderByDescending(bounds => (bounds!.Value.Right - bounds.Value.Left) * (bounds.Value.Bottom - bounds.Value.Top))
                .FirstOrDefault();

            if (best.HasValue)
            {
                return best;
            }

            var images = Regex.Matches(xml, @"<node[^>]*class=""android\.widget\.ImageView""[^>]*bounds=""\[(\d+),(\d+)\]\[(\d+),(\d+)\]""[^>]*>");
            return images
                .Cast<Match>()
                .Select(ParseBounds)
                .Where(bounds => bounds.HasValue && (bounds.Value.Right - bounds.Value.Left) * (bounds.Value.Bottom - bounds.Value.Top) > 40000)
                .OrderByDescending(bounds => (bounds!.Value.Right - bounds.Value.Left) * (bounds.Value.Bottom - bounds.Value.Top))
                .FirstOrDefault();
        }

        private static (int Left, int Top, int Right, int Bottom)? ParseBounds(Match match)
        {
            return (
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture));
        }

        private async Task<long> GetEstimatedPositionAsync(TrackInfo track)
        {
            if (!track.IsPlaying || track.UpdatedMs <= 0)
            {
                return Math.Max(0, track.PositionMs);
            }

            string uptimeOutput = await RunAdbCommandAsync(Adb("shell cat /proc/uptime"), 1500);
            string uptimeValue = uptimeOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "0";
            if (!double.TryParse(uptimeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double uptimeSeconds))
            {
                return Math.Max(0, track.PositionMs);
            }

            long uptimeMs = (long)(uptimeSeconds * 1000);
            long elapsed = Math.Max(0, uptimeMs - track.UpdatedMs);
            return Math.Max(0, track.PositionMs + (long)(elapsed * Math.Max(0, track.Speed)));
        }

        private async Task<(int Current, int Max)> GetVolumeAsync()
    {
        try
        {
            // 1. Primary method: cmd audio get-stream-volume 3
            var audioOutput = await RunAdbCommandAsync(Adb("shell cmd audio get-stream-volume 3"), 2000);
            var matchAudio = Regex.Match(audioOutput, @"getStreamVolume\(3\)\s*->\s*(\d+)");
            if (matchAudio.Success)
            {
                int cur = int.Parse(matchAudio.Groups[1].Value);
                return (cur, 15);
            }

            // 2. Fallback: cmd media_session volume --stream 3 --get
            var output = await RunAdbCommandAsync(Adb("shell cmd media_session volume --stream 3 --get"), 2000);
            // Example: [V] volume is 13 in range [0..15]
            var match = Regex.Match(output, @"volume is (\d+) in range \[(\d+)\.\.(\d+)\]");
            if (match.Success)
            {
                int cur = int.Parse(match.Groups[1].Value);
                int max = int.Parse(match.Groups[3].Value);
                return (cur, max);
            }
        }
        catch { }
        return (0, 15);
    }

    private static TrackInfo ParseMediaSession(string dump)
    {
        if (string.IsNullOrWhiteSpace(dump))
        {
            return new TrackInfo(true, false, "", "", "", false, "", "", 0, 15, "Нет ответа от службы медиа");
        }

        // Look for Sessions Stack - have X sessions:
        // Then individual sessions
        var sessionsMatch = Regex.Match(dump, @"Sessions Stack - have \d+ sessions:(.*?)Audio playback", RegexOptions.Singleline);
        string searchBlock = sessionsMatch.Success ? sessionsMatch.Groups[1].Value : dump;

        // Find active session block
        // Find packages and descriptions
        var sessionBlocks = Regex.Split(searchBlock, @"(?=\s+([A-Za-z0-9_]+(\s+[A-Za-z0-9_]+)*)\s+[a-zA-Z0-9_\.]+\/[a-zA-Z0-9_\.]+.*\(userId=\d+\))");

        string? bestPackage = null;
        string? bestDescription = null;
        string? bestBlock = null;
        bool isPlaying = false;

        foreach (var block in sessionBlocks)
        {
            if (string.IsNullOrWhiteSpace(block)) continue;

            var pkgMatch = Regex.Match(block, @"package=([a-zA-Z0-9_\.]+)");
            if (!pkgMatch.Success) continue;
            var pkg = pkgMatch.Groups[1].Value;

            var stateMatch = Regex.Match(block, @"state=PlaybackState\s*\{\s*state=([A-Za-z]+)\((\d+)\)");
            bool blockPlaying = false;
            if (stateMatch.Success)
            {
                var stateName = stateMatch.Groups[1].Value;
                blockPlaying = stateName.Equals("PLAYING", StringComparison.OrdinalIgnoreCase);
            }

            var descMatch = Regex.Match(block, @"(?:description|metadata:.*?description)=([^\r\n]+)");
            if (descMatch.Success)
            {
                var desc = descMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(desc) && desc != "null")
                {
                    bestPackage = pkg;
                    bestDescription = desc;
                    bestBlock = block;
                    isPlaying = blockPlaying;

                    // If it's playing, priority is highest
                    if (isPlaying) break;
                }
            }
        }

        // Fallback: If no session block was matched with split, try direct global regex on searchBlock
        if (string.IsNullOrEmpty(bestDescription))
        {
            var descMatch = Regex.Match(searchBlock, @"metadata:.*?description=([^\r\n]+)");
            if (descMatch.Success)
            {
                bestDescription = descMatch.Groups[1].Value.Trim();
            }

            var pkgMatch = Regex.Match(searchBlock, @"package=([a-zA-Z0-9_\.]+)");
            if (pkgMatch.Success)
            {
                bestPackage = pkgMatch.Groups[1].Value;
            }

            var stateMatch = Regex.Match(searchBlock, @"state=PlaybackState\s*\{\s*state=([A-Za-z]+)");
            if (stateMatch.Success)
            {
                isPlaying = stateMatch.Groups[1].Value.Equals("PLAYING", StringComparison.OrdinalIgnoreCase);
            }

            bestBlock = searchBlock;
        }

        if (string.IsNullOrWhiteSpace(bestDescription) || bestDescription == "null")
        {
            return new TrackInfo(
                Connected: true,
                HasTrack: false,
                Title: "Нет активного трека",
                Artist: "Включи музыку на телефоне",
                Album: "",
                IsPlaying: false,
                PackageName: bestPackage ?? "",
                AppName: GetFriendlyAppName(bestPackage),
                VolumeCurrent: 0,
                VolumeMax: 15
            );
        }

        // Parse description: typically "Title, Artist, Album" or "Title, Artist"
        string title = bestDescription;
        string artist = "";
        string album = "";

        var parts = bestDescription.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            title = parts[0];
            artist = parts[1];
            if (parts.Length >= 3)
            {
                album = parts[2];
            }
        }

        string stateBlock = bestBlock ?? searchBlock;
        var positionMatch = Regex.Match(stateBlock, @"position=(\d+)");
        var updatedMatch = Regex.Match(stateBlock, @"updated=(\d+)");
        var speedMatch = Regex.Match(stateBlock, @"speed=([\d\.]+)");
        long positionMs = positionMatch.Success && long.TryParse(positionMatch.Groups[1].Value, out long parsedPosition) ? parsedPosition : 0;
        long updatedMs = updatedMatch.Success && long.TryParse(updatedMatch.Groups[1].Value, out long parsedUpdated) ? parsedUpdated : 0;
        double speed = speedMatch.Success && double.TryParse(speedMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedSpeed) ? parsedSpeed : 1.0;

        return new TrackInfo(
            Connected: true,
            HasTrack: true,
            Title: title,
            Artist: artist,
            Album: album,
            IsPlaying: isPlaying,
            PackageName: bestPackage ?? "",
            AppName: GetFriendlyAppName(bestPackage),
            VolumeCurrent: 0,
            VolumeMax: 15
        )
        {
            PositionMs = positionMs,
            UpdatedMs = updatedMs,
            Speed = speed
        };
    }

    /// <summary>
    /// Deterministic file-name hash. string.GetHashCode() is randomized per process on .NET,
    /// so cache files named with it never match again after a restart.
    /// </summary>
    private static string StableHash(string value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 4);
    }

    private static string GetFriendlyAppName(string? pkg) => pkg switch
    {
        "com.google.android.apps.youtube.music" => "YouTube Music",
        "com.spotify.music" => "Spotify",
        "ru.yandex.music" => "Яндекс Музыка",
        "com.vkontakte.android" => "VK",
        "com.vkmusic" => "VK Музыка",
        "com.soundcloud.android" => "SoundCloud",
        "com.apple.android.music" => "Apple Music",
        "deezer.android.app" => "Deezer",
        _ when !string.IsNullOrEmpty(pkg) => pkg,
        _ => "Медиаплеер"
    };

    public async Task PlayPauseAsync()
    {
        if (await TryCompanionControlAsync("playPause"))
        {
            _cachedInfo = null;
            return;
        }

        await RunAdbCommandAsync(Adb("shell cmd media_session dispatch play-pause"), 2000);
        // Force refresh cache quickly
        _cachedInfo = null;
    }

    public async Task NextTrackAsync()
    {
        if (await TryCompanionControlAsync("next"))
        {
            _cachedInfo = null;
            return;
        }

        await RunAdbCommandAsync(Adb("shell cmd media_session dispatch next"), 2000);
        _cachedInfo = null;
    }

    public async Task PreviousTrackAsync()
    {
        if (await TryCompanionControlAsync("previous"))
        {
            _cachedInfo = null;
            return;
        }

        await RunAdbCommandAsync(Adb("shell cmd media_session dispatch previous"), 2000);
        _cachedInfo = null;
    }

    public async Task SeekRelativeAsync(int seconds)
    {
        if (seconds == 0)
        {
            return;
        }

        var current = await GetNowPlayingAsync(forceRefresh: true);
        if (current.DurationMs > 0)
        {
            long target = Math.Clamp(current.PositionMs + (seconds * 1000L), 0, current.DurationMs);
            if (await TryCompanionControlAsync("seek", target))
            {
                _cachedInfo = null;
                return;
            }
        }

        string action = seconds > 0 ? "fast-forward" : "rewind";
        await RunAdbCommandAsync(Adb($"shell cmd media_session dispatch {action}"), 2000);
        _cachedInfo = null;
    }

    public async Task<bool> SeekToPositionAsync(long positionMs, long durationMs)
    {
        if (durationMs <= 0)
        {
            return false;
        }

        if (await TryCompanionControlAsync("seek", Math.Clamp(positionMs, 0, durationMs)))
        {
            _cachedInfo = null;
            return true;
        }

        double fraction = Math.Clamp((double)positionMs / durationMs, 0, 1);
        await RunAdbCommandAsync(Adb("shell uiautomator dump /sdcard/scrcpybar_window.xml"), 4000);
        string xml = await RunAdbCommandAsync(Adb("shell cat /sdcard/scrcpybar_window.xml"), 4000);
        var bounds = ParseSeekBarBounds(xml);
        if (!bounds.HasValue)
        {
            return false;
        }

        var (left, top, right, bottom) = bounds.Value;
        int x = left + (int)Math.Round((right - left) * fraction);
        int y = top + ((bottom - top) / 2);
        await RunAdbCommandAsync(Adb($"shell input tap {x} {y}"), 2000);
        _cachedInfo = null;
        return true;
    }

    private static (int Left, int Top, int Right, int Bottom)? ParseSeekBarBounds(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        var match = Regex.Match(xml, @"class=""android\.widget\.SeekBar""[^>]*bounds=""\[(\d+),(\d+)\]\[(\d+),(\d+)\]""");
        if (!match.Success)
        {
            return null;
        }

        return (
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture));
    }

    public async Task VolumeUpAsync()
    {
        // 1. Primary method: cmd audio adj-volume 3 RAISE. An empty reply means the command
        // succeeded silently — without this distinction the fallback below would fire too and
        // the volume would jump two steps per click.
        var output = await RunAdbCommandAsync(Adb("shell cmd audio adj-volume 3 RAISE"), 2500);
        if (!string.IsNullOrWhiteSpace(output) && !output.Contains("adjustStreamVolume"))
        {
            // 2. Hardware key fallback: KEYCODE_VOLUME_UP (24)
            await RunAdbCommandAsync(Adb("shell input keyevent 24"), 2500);
        }
        _cachedInfo = null;
    }

    public async Task VolumeDownAsync()
    {
        // 1. Primary method: cmd audio adj-volume 3 LOWER
        var output = await RunAdbCommandAsync(Adb("shell cmd audio adj-volume 3 LOWER"), 2500);
        if (!string.IsNullOrWhiteSpace(output) && !output.Contains("adjustStreamVolume"))
        {
            // 2. Hardware key fallback: KEYCODE_VOLUME_DOWN (25)
            await RunAdbCommandAsync(Adb("shell input keyevent 25"), 2500);
        }
        _cachedInfo = null;
    }

    public async Task SetVolumeAsync(int volume)
    {
        volume = Math.Clamp(volume, 0, 15);
        var output = await RunAdbCommandAsync(Adb($"shell cmd audio set-volume 3 {volume}"), 2500);
        if (!string.IsNullOrWhiteSpace(output) && !output.Contains("setStreamVolume"))
        {
            await RunAdbCommandAsync(Adb($"shell cmd media_session volume --stream 3 --set {volume}"), 2500);
        }
        _cachedInfo = null;
    }
}

