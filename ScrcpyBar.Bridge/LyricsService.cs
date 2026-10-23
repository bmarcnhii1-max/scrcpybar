using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ScrcpyBar.Bridge;

/// <summary>One synchronised lyric line.</summary>
public record LyricLine(long TimeMs, string Text);

public record LyricsResult(
    bool Found,
    bool Instrumental,
    string Plain,
    List<LyricLine> Lines,
    string Source);

/// <summary>
/// Fetches synchronised lyrics from lrclib.net and caches them on disk.
///
/// lrclib is a free, key-less public API, but it is a shared resource: every track is requested
/// at most once and then served from the on-disk cache, so repeatedly polling the bridge while a
/// song plays never touches the network again.
/// </summary>
public class LyricsService
{
    private const string ApiBase = "https://lrclib.net/api";
    private const string CacheDirectoryName = "lyrics-cache";
    private const string UserAgent = "ScrcpyBar/1.0 (Windows Game Bar music widget)";

    /// <summary>How long a genuine "no lyrics on lrclib" result stays cached in memory.</summary>
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly ILogger<LyricsService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, CachedLyrics> _memory = new(StringComparer.Ordinal);

    private DateTime _serviceUnavailableUntil = DateTime.MinValue;
    private int _unavailableStrikes;

    public LyricsService(ILogger<LyricsService> logger)
    {
        _logger = logger;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    private sealed record CachedLyrics(LyricsResult Result, DateTime CachedAt);

    /// <summary>
    /// Returns the lyrics for a track. The result is cached per artist/title, so repeated calls
    /// for the same song are free.
    /// </summary>
    public async Task<LyricsResult> GetAsync(string title, string artist, long durationMs, bool forceRefresh = false)
    {
        string cleanTitle = Clean(title);
        string cleanArtist = Clean(artist);

        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            return new LyricsResult(false, false, "", new List<LyricLine>(), "none");
        }

        string key = BuildKey(cleanArtist, cleanTitle);

        if (!forceRefresh && _memory.TryGetValue(key, out var cached))
        {
            if (cached.Result.Found || DateTime.UtcNow - cached.CachedAt < NegativeTtl)
            {
                return cached.Result;
            }
            _memory.Remove(key);
        }

        string cachePath = CachePath(key);
        if (!forceRefresh && File.Exists(cachePath))
        {
            try
            {
                string raw = await File.ReadAllTextAsync(cachePath);
                var result = ParseCached(raw, key);
                if (result.Found)
                {
                    _memory[key] = new CachedLyrics(result, DateTime.UtcNow);
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Lyrics cache read failed: {Message}", ex.Message);
            }
        }

        await _lock.WaitAsync();
        try
        {
            if (!forceRefresh && _memory.TryGetValue(key, out var afterLock))
            {
                if (afterLock.Result.Found || DateTime.UtcNow - afterLock.CachedAt < NegativeTtl)
                {
                    return afterLock.Result;
                }
                _memory.Remove(key);
            }

            var fetched = await FetchAsync(cleanTitle, cleanArtist, durationMs);

            // A throttled lrclib must not be remembered as "no lyrics": the warmup and the
            // widget have to retry once the pause expires.
            if (fetched.Source != "unavailable")
            {
                _memory[key] = new CachedLyrics(fetched, DateTime.UtcNow);
            }

            if (fetched.Found)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    await File.WriteAllTextAsync(cachePath, Serialize(fetched));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Lyrics cache write failed: {Message}", ex.Message);
                }
            }

            return fetched;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<LyricsResult> FetchAsync(string title, string artist, long durationMs)
    {
        // Throttled: skip the network entirely so the warmup does not re-open the ban window.
        if (DateTime.UtcNow < _serviceUnavailableUntil)
        {
            return new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable");
        }

        try
        {
            return await FetchCoreAsync(title, artist, durationMs);
        }
        catch (Exception ex)
        {
            // A malformed response must never bubble up as a failed request.
            _logger.LogWarning("Lyrics lookup failed for '{Artist}' - '{Title}': {Message}", artist, title, ex.Message);
            return new LyricsResult(false, false, "", new List<LyricLine>(), "error");
        }
    }

    private async Task<LyricsResult> FetchCoreAsync(string title, string artist, long durationMs)
    {
        int durationSeconds = durationMs > 0 ? (int)(durationMs / 1000) : 0;

        // Exact match first, with the duration so the right version of a cover is preferred.
        string direct = $"/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
        if (durationSeconds > 0)
        {
            direct += $"&duration={durationSeconds}";
        }

        var (directOk, directResult) = await GetJsonAsync(direct);
        if (directOk)
        {
            return FromJson(directResult, "lrclib:get");
        }

        // A throttled lrclib reported as "nothing found" would poison the cache, so the
        // pause set by GetJsonAsync has to be surfaced here as well.
        if (DateTime.UtcNow < _serviceUnavailableUntil)
        {
            return new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable");
        }

        // Fall back to the search endpoint and pick the closest match that has timed lyrics.
        string search = $"/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
        var (searchOk, searchResult) = await GetJsonAsync(search);
        if (!searchOk || DateTime.UtcNow < _serviceUnavailableUntil)
        {
            return new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable");
        }

        if (searchResult.ValueKind != JsonValueKind.Array)
        {
            return new LyricsResult(false, false, "", new List<LyricLine>(), "not-found");
        }

        JsonElement best = default;
        bool hasBest = false;
        int bestScore = int.MinValue;
        foreach (var item in searchResult.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            bool hasSynced = item.TryGetProperty("syncedLyrics", out var synced) &&
                             synced.ValueKind == JsonValueKind.String &&
                             !string.IsNullOrWhiteSpace(synced.GetString());
            bool hasPlain = item.TryGetProperty("plainLyrics", out var plain) &&
                            plain.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(plain.GetString());
            if (!hasSynced && !hasPlain)
            {
                continue;
            }

            int score = hasSynced ? 1000 : 0;
            if (durationSeconds > 0 && item.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
            {
                // lrclib reports the duration as a decimal, so GetInt32 would throw on "172.0".
                int candidate = (int)Math.Round(dur.GetDouble());
                int delta = Math.Abs(candidate - durationSeconds);
                score += Math.Max(0, 300 - delta);
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = item;
                hasBest = true;
            }
        }

        return hasBest
            ? FromJson(best, "lrclib:search")
            : new LyricsResult(false, false, "", new List<LyricLine>(), "not-found");
    }

    private async Task<(bool Ok, JsonElement Element)> GetJsonAsync(string path)
    {
        // lrclib goes down or throttles from time to time. Backing off keeps the warmup from
        // hammering it every few seconds while it is unavailable.
        if (DateTime.UtcNow < _serviceUnavailableUntil)
        {
            return (false, default);
        }

        try
        {
            using var response = await _http.GetAsync(ApiBase + path);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (false, default);
            }
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable ||
                response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Escalating pause instead of a fixed one: a busy lrclib keeps rejecting a
                // client that retries right after the previous window ends.
                _unavailableStrikes++;
                TimeSpan pause = TimeSpan.FromMinutes(Math.Min(10.0 * _unavailableStrikes, 30.0));

                var retryAfter = response.Headers.RetryAfter;
                if (retryAfter?.Delta is TimeSpan delta && delta > pause)
                {
                    pause = delta;
                }
                else if (retryAfter?.Date is DateTimeOffset retryDate)
                {
                    TimeSpan untilDate = retryDate.UtcDateTime - DateTime.UtcNow;
                    if (untilDate > pause)
                    {
                        pause = untilDate;
                    }
                }

                _serviceUnavailableUntil = DateTime.UtcNow + pause;
                _logger.LogWarning(
                    "lrclib is unavailable, pausing lookups for {Minutes} minutes (strike {Strike})",
                    pause.TotalMinutes, _unavailableStrikes);
                return (false, default);
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("lrclib returned {Status} for {Path}", response.StatusCode, path);
                return (false, default);
            }

            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            _unavailableStrikes = 0;
            return (true, document.RootElement.Clone());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("lrclib request failed for {Path}: {Message}", path, ex.Message);
            return (false, default);
        }
    }

    private static LyricsResult FromJson(JsonElement element, string source)
    {
        bool instrumental = element.TryGetProperty("instrumental", out var inst) && inst.ValueKind == JsonValueKind.True;

        string synced = element.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? ""
            : "";
        string plain = element.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? ""
            : "";

        var lines = ParseLrc(synced);
        bool found = lines.Count > 0 || !string.IsNullOrWhiteSpace(plain);

        return new LyricsResult(found, instrumental, plain, lines, source);
    }

    private static LyricsResult ParseCached(string raw, string key)
    {
        // The cache file stores the plain lyrics followed by the timed ones so a plain-only
        // entry stays readable and the timed entry can be restored without a network call.
        int separator = raw.IndexOf("\n--synced--\n", StringComparison.Ordinal);
        if (separator < 0)
        {
            string plain = raw.Trim();
            return new LyricsResult(plain.Length > 0, false, plain, new List<LyricLine>(), "cache");
        }

        string plainPart = raw[..separator].Trim();
        string syncedPart = raw[(separator + "\n--synced--\n".Length)..];
        var lines = ParseLrc(syncedPart);
        return new LyricsResult(true, false, plainPart, lines, "cache");
    }

    /// <summary>Stores plain lyrics and timed lyrics in one file so both survive a restart.</summary>
    private static string Serialize(LyricsResult result)
    {
        if (result.Lines.Count == 0)
        {
            return result.Plain;
        }

        var builder = new StringBuilder();
        builder.Append(result.Plain);
        builder.Append("\n--synced--\n");
        foreach (var line in result.Lines)
        {
            builder.Append('[');
            builder.Append((line.TimeMs / 60000L).ToString("00", CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(((line.TimeMs % 60000L) / 1000L).ToString("00", CultureInfo.InvariantCulture));
            builder.Append('.');
            builder.Append((line.TimeMs % 1000L).ToString("000", CultureInfo.InvariantCulture));
            builder.Append(']');
            builder.Append(line.Text);
            builder.Append('\n');
        }
        return builder.ToString();
    }

    /// <summary>Parses LRC timestamps into timed lines. Multiple stamps per line are supported.</summary>
    internal static List<LyricLine> ParseLrc(string lrc)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return result;
        }

        var stamp = new Regex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]");

        foreach (var rawLine in lrc.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var matches = stamp.Matches(line);
            if (matches.Count == 0)
            {
                continue;
            }

            var last = matches[^1];
            string text = line[(last.Index + last.Length)..].Trim();
            if (text.Length == 0)
            {
                continue;
            }

            foreach (Match match in matches)
            {
                int minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                int seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                string fraction = match.Groups[3].Value;

                int millis = 0;
                if (fraction.Length == 1)
                {
                    millis = int.Parse(fraction, CultureInfo.InvariantCulture) * 100;
                }
                else if (fraction.Length == 2)
                {
                    millis = int.Parse(fraction, CultureInfo.InvariantCulture) * 10;
                }
                else if (fraction.Length == 3)
                {
                    millis = int.Parse(fraction, CultureInfo.InvariantCulture);
                }

                result.Add(new LyricLine((minutes * 60L + seconds) * 1000L + millis, text));
            }
        }

        result.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return result;
    }

    private static string Clean(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        // Feeds often carry decorations that break the lookup: "Song (feat. X) [Remastered]".
        string text = value.Trim();
        text = Regex.Replace(text, @"[\(\[\{][^\)\]\}]*[\)\]\}]", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }

    private static string BuildKey(string artist, string title)
    {
        string raw = $"{artist.ToLowerInvariant()}|{title.ToLowerInvariant()}";
        return Math.Abs(raw.GetHashCode()).ToString("X8");
    }

    private static string CachePath(string key) =>
        Path.Combine(AppPaths.DataSubDir(CacheDirectoryName), key + ".lyrics");
}
