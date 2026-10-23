using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ScrcpyBar.Bridge;

/// <summary>
/// Warms the lyrics cache in the background.
///
/// The widget asks for lyrics on demand, but a cold lookup means a round trip to lrclib.net
/// which can take several seconds. Prefetching as soon as the track changes keeps the request
/// from the widget fast and keeps the widget's own short timeout from killing the lookup.
/// </summary>
public class LyricsWarmupService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly AdbService _adb;
    private readonly LyricsService _lyrics;
    private readonly ILogger<LyricsWarmupService> _logger;
    private string _lastKey = "";
    private DateTime _lastRetryAt = DateTime.MinValue;

    public LyricsWarmupService(AdbService adb, LyricsService lyrics, ILogger<LyricsWarmupService> logger)
    {
        _adb = adb;
        _lyrics = lyrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WarmAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Lyrics warmup failed: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task WarmAsync()
    {
        var track = await _adb.GetNowPlayingAsync();
        if (!track.HasTrack || string.IsNullOrWhiteSpace(track.Title))
        {
            _lastKey = "";
            return;
        }

        string key = $"{track.PackageName}|{track.Title}|{track.Artist}";
        if (key == _lastKey)
        {
            return;
        }

        _lastKey = key;
        var result = await _lyrics.GetAsync(track.Title, track.Artist, track.DurationMs);

        // A throttled or flaky lookup must not stick for the whole track: retry the same song
        // every couple of minutes until it resolves.
        if (!result.Found && result.Source == "unavailable")
        {
            _ = RetryUntilFoundAsync(key);
        }
    }

    private async Task RetryUntilFoundAsync(string key)
    {
        while (key == _lastKey)
        {
            await Task.Delay(TimeSpan.FromMinutes(2));
            if (key != _lastKey)
            {
                return;
            }
            var fresh = await _adb.GetNowPlayingAsync();
            if (!fresh.HasTrack)
            {
                return;
            }
            var result = await _lyrics.GetAsync(fresh.Title, fresh.Artist, fresh.DurationMs, forceRefresh: true);
            if (result.Found)
            {
                return;
            }
        }
    }
}
