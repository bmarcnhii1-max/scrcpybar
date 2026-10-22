using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ScrcpyBar.Bridge;

public class SmtcSyncService : IHostedService
{
    private readonly AdbService _adb;
    private readonly ILogger<SmtcSyncService> _logger;
    private MediaPlayer? _player;
    private SystemMediaTransportControls? _smtc;
    private CancellationTokenSource? _cts;
    private string _lastArtPath = "";

    public SmtcSyncService(AdbService adb, ILogger<SmtcSyncService> logger)
    {
        _adb = adb;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _player = new MediaPlayer();
            _player.CommandManager.IsEnabled = false;

            // Load silent audio loop to register this player with Windows Audio Service and SMTC.
            // The wav ships inside the exe; on first run it is unpacked into the data dir.
            var silencePath = ResolveSilencePath();
            if (silencePath.Length > 0)
            {
                _player.Source = MediaSource.CreateFromUri(new Uri(silencePath));
                _player.IsLoopingEnabled = true;
                _player.Volume = 0.0; // 100% silent
            }

            _smtc = _player.SystemMediaTransportControls;
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;

            _smtc.ButtonPressed += async (sender, args) =>
            {
                try
                {
                    _logger.LogInformation("SMTC Button pressed: {Button}", args.Button);
                    switch (args.Button)
                    {
                        case SystemMediaTransportControlsButton.Play:
                        case SystemMediaTransportControlsButton.Pause:
                            await _adb.PlayPauseAsync();
                            break;
                        case SystemMediaTransportControlsButton.Next:
                            await _adb.NextTrackAsync();
                            break;
                        case SystemMediaTransportControlsButton.Previous:
                            await _adb.PreviousTrackAsync();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error handling SMTC button: {Msg}", ex.Message);
                }
            };

            _smtc.PlaybackPositionChangeRequested += async (sender, args) =>
            {
                try
                {
                    var track = await _adb.GetNowPlayingAsync(forceRefresh: true);
                    long positionMs = (long)Math.Max(0, args.RequestedPlaybackPosition.TotalMilliseconds);
                    long durationMs = track.DurationMs;
                    bool applied = false;

                    if (durationMs > 0)
                    {
                        applied = await _adb.SeekToPositionAsync(positionMs, durationMs);
                        if (!applied)
                        {
                            await Task.Delay(250);
                            applied = await _adb.SeekToPositionAsync(positionMs, durationMs);
                        }
                    }

                    File.AppendAllText(
                        Path.Combine(AppPaths.EnsureDataDir(), "smtc_seek.log"),
                        $"{DateTime.Now:O} requested={positionMs} current={track.PositionMs} duration={durationMs} applied={applied}{Environment.NewLine}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error handling SMTC seek: {Msg}", ex.Message);
                }
            };

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => PollingLoopAsync(_cts.Token));
            _logger.LogInformation("Windows Media Controls (SMTC) bridge initialized successfully!");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize SMTC bridge");
        }

        return Task.CompletedTask;
    }

    private async Task PollingLoopAsync(CancellationToken ct)
    {
        string lastTitle = "";
        string lastArtist = "";
        bool? lastPlaying = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var track = await _adb.GetNowPlayingAsync();
                if (_smtc != null && track.Connected)
                {
                    if (track.Title != lastTitle || track.Artist != lastArtist || track.IsPlaying != lastPlaying || track.AlbumArtPath != _lastArtPath)
                    {
                        lastTitle = track.Title;
                        lastArtist = track.Artist;
                        lastPlaying = track.IsPlaying;

                        var updater = _smtc.DisplayUpdater;
                        updater.Type = MediaPlaybackType.Music;
                        updater.MusicProperties.Title = string.IsNullOrWhiteSpace(track.Title) ? "Scrcpy" : track.Title;
                        updater.MusicProperties.Artist = string.IsNullOrWhiteSpace(track.Artist) ? track.AppName : track.Artist;
                        updater.MusicProperties.AlbumArtist = track.AppName;
                        updater.MusicProperties.AlbumTitle = track.Album;
                        _lastArtPath = track.AlbumArtPath;
                        if (!string.IsNullOrWhiteSpace(track.AlbumArtPath) && File.Exists(track.AlbumArtPath))
                        {
                            try
                            {
                                var artFile = await StorageFile.GetFileFromPathAsync(track.AlbumArtPath);
                                updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(artFile);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("SMTC thumbnail failed: {Msg}", ex.Message);
                            }
                        }
                        updater.Update();
                    }

                    _smtc.PlaybackStatus = track.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
                    if (track.IsPlaying)
                    {
                        _player?.Play();
                    }
                    else
                    {
                        _player?.Pause();
                    }

                    if (track.DurationMs > 0)
                    {
                        var timeline = new SystemMediaTransportControlsTimelineProperties
                        {
                            StartTime = TimeSpan.Zero,
                            EndTime = TimeSpan.FromMilliseconds(track.DurationMs),
                            MinSeekTime = TimeSpan.Zero,
                            MaxSeekTime = TimeSpan.FromMilliseconds(track.DurationMs),
                            Position = TimeSpan.FromMilliseconds(Math.Clamp(track.PositionMs, 0, track.DurationMs))
                        };
                        _smtc.UpdateTimelineProperties(timeline);
                    }
                }
            }
            catch { }

            await Task.Delay(1000, ct);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _player?.Pause();
        _player?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Extracts the embedded silence.wav into the data dir (once) and returns its path,
    /// falling back to the legacy "next to the exe" locations.
    /// </summary>
    private static string ResolveSilencePath()
    {
        var dataDir = AppPaths.EnsureDataDir();
        var candidate = Path.Combine(dataDir, "silence.wav");
        if (!File.Exists(candidate))
        {
            try
            {
                using var stream = typeof(SmtcSyncService).Assembly.GetManifestResourceStream("silence.wav");
                if (stream != null)
                {
                    using var output = File.Create(candidate);
                    stream.CopyTo(output);
                }
            }
            catch
            {
            }
        }

        if (File.Exists(candidate))
        {
            return candidate;
        }

        string? legacy = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "silence.wav"),
            Path.Combine(AppContext.BaseDirectory, "silence.wav")
        }.FirstOrDefault(File.Exists);
        return legacy ?? "";
    }
}
