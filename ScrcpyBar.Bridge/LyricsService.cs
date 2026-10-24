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
public record LyricLine(long TimeMs, string Text, List<LyricWord> Words);

/// <summary>One word inside a synchronised line. EndMs is 0 when unknown.</summary>
public record LyricWord(long TimeMs, long EndMs, string Text);

public record LyricsResult(
    bool Found,
    bool Instrumental,
    string Plain,
    List<LyricLine> Lines,
    string Source,
    string Version = "");

/// <summary>
/// Fetches synchronised lyrics from lrclib.net and caches them on disk.
///
/// lrclib is a free, key-less public API, but it is a shared resource: every track is requested
/// at most once and then served from the on-disk cache, so repeatedly polling the bridge while a
/// song plays never touches the network again.
/// </summary>
public class LyricsService
{
    private const string LrclibApiBase = "https://lrclib.net/api";
    private const string BetterLyricsApiBase = "https://api.betterlyrics.org";
    private const string CacheDirectoryName = "lyrics-cache";
    private const string UserAgent = "ScrcpyBar/1.0 (Windows Game Bar music widget)";

    /// <summary>How long a genuine "no lyrics on lrclib" result stays cached in memory.</summary>
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly ILogger<LyricsService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, CachedLyrics> _memory = new(StringComparer.Ordinal);

    /// <summary>Per-provider throttle state: one API being busy must not mute the other.</summary>
    private sealed class ProviderBackoff
    {
        public DateTime PausedUntil = DateTime.MinValue;
        public int Strikes;
        public int SuccessStreak;
    }

    private readonly Dictionary<string, ProviderBackoff> _backoff = new(StringComparer.Ordinal)
    {
        ["lrclib"] = new(),
        ["better-lyrics"] = new(),
        ["lrc.red"] = new(),
        ["lrcmux"] = new(),
        ["synclrc"] = new(),
    };
    private readonly Dictionary<string, DateTime> _lastAttemptAt = new(StringComparer.Ordinal);

    /// <summary>When a track may be retried after a failed lookup (throttle/network noise).</summary>
    private static readonly TimeSpan FailedRetryInterval = TimeSpan.FromMinutes(2);

    public LyricsService(ILogger<LyricsService> logger)
    {
        _logger = logger;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        // Registered client-side application: public ID, ~180 req/min instead of ~90.
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Lrclib-App-Id", "OQ87dX1cjpMAwrw4qnYjVDJ0lOhsjTXhbohNQVmH_Jk");
    }

    private sealed record CachedLyrics(LyricsResult Result, DateTime CachedAt);

    /// <summary>
    /// Plain-only cache entries predate line estimation: they must be re-fetched
    /// instead of served, otherwise the dashboard gets text it cannot follow.
    /// </summary>
    private static bool IsStalePlainCache(LyricsResult result) =>
        result.Found && result.Lines.Count == 0 && !string.IsNullOrWhiteSpace(result.Plain);

    /// <summary>
    /// Returns the lyrics for a track. The result is cached per artist/title, so repeated calls
    /// for the same song are free.
    /// </summary>
    public async Task<LyricsResult> GetAsync(string title, string artist, long durationMs, bool forceRefresh = false)
    {
        string rawTitle = (title ?? "").Trim();
        string rawArtist = (artist ?? "").Trim();
        string cleanTitle = Clean(rawTitle);
        string cleanArtist = Clean(rawArtist);

        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            return new LyricsResult(false, false, "", new List<LyricLine>(), "none");
        }

        // The key uses the raw names and the duration: "(Clean)" uploads and
        // "speed up" versions carry different lyrics and must not share a cache entry.
        string key = BuildKey(rawArtist, rawTitle, durationMs);

        // A failed lookup (throttle, network noise) may be retried after a while instead of
        // being stuck for as long as the track keeps playing.
        if (!forceRefresh && _lastAttemptAt.TryGetValue(key, out var lastAttempt) &&
            DateTime.UtcNow - lastAttempt < FailedRetryInterval)
        {
            if (_memory.TryGetValue(key, out var prior))
            {
                if (prior.Result.Found || DateTime.UtcNow - prior.CachedAt < NegativeTtl)
                {
                    return prior.Result;
                }
            }
            return new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable");
        }
        _lastAttemptAt[key] = DateTime.UtcNow;

        if (!forceRefresh && _memory.TryGetValue(key, out var cached))
        {
            if (!IsStalePlainCache(cached.Result) &&
                (cached.Result.Found || DateTime.UtcNow - cached.CachedAt < NegativeTtl))
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
                if (!IsStalePlainCache(result) && result.Found)
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
                if (!IsStalePlainCache(afterLock.Result) &&
                    (afterLock.Result.Found || DateTime.UtcNow - afterLock.CachedAt < NegativeTtl))
                {
                    return afterLock.Result;
                }
                _memory.Remove(key);
            }

            var fetched = await FetchAsync(rawTitle, rawArtist, cleanTitle, cleanArtist, durationMs);

            // A throttled lrclib must not be remembered as "no lyrics": the warmup and the
            // widget have to retry once the pause expires.
            if (fetched.Source != "unavailable")
            {
                if (_memory.Count > 500)
                {
                    // Drop the negative entries first: found lyrics are cheap to re-read from disk.
                    foreach (var stale in _memory.Where(kv => !kv.Value.Result.Found).Select(kv => kv.Key).ToList())
                    {
                        _memory.Remove(stale);
                    }
                    if (_memory.Count > 500)
                    {
                        _memory.Clear();
                    }
                }
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

    private async Task<LyricsResult> FetchAsync(string rawTitle, string rawArtist, string title, string artist, long durationMs)
    {
        try
        {
            var core = await FetchCoreAsync(rawTitle, rawArtist, title, artist, durationMs);
            return WithEstimatedTiming(core, durationMs > 0 ? (int)(durationMs / 1000) : 0);
        }
        catch (Exception ex)
        {
            // A malformed response must never bubble up as a failed request.
            _logger.LogWarning("Lyrics lookup failed for '{Artist}' - '{Title}': {Message}", rawArtist, rawTitle, ex.Message);
            return new LyricsResult(false, false, "", new List<LyricLine>(), "error");
        }
    }

    private async Task<LyricsResult> FetchCoreAsync(string rawTitle, string rawArtist, string title, string artist, long durationMs)
    {
        int durationSeconds = durationMs > 0 ? (int)(durationMs / 1000) : 0;

        // Every provider is tried in a random order on each lookup, so no single backend
        // takes the whole load and a throttled one does not block the rest. The first
        // provider with synced lines wins; a plain-only hit is kept as a last resort.
        // A paused provider reports "unavailable" instead of failing the lookup.
        async Task<LyricsResult?> FetchLrclibAsync()
        {
            // 1-2. Exact /get matches. A plain-only hit is kept as a fallback, not returned
            //      immediately: lrclib often hosts several entries per song and /get can land on
            //      the one without synced lyrics while search finds synced versions of the same
            //      track (real case: Anacondaz - "Мама, я люблю").
            LyricsResult? plainFallback = null;

            async Task<LyricsResult?> GetExactAsync(bool withDuration)
            {
                string path = $"/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
                if (withDuration && durationSeconds > 0)
                {
                    path += $"&duration={durationSeconds}";
                }
                if (!withDuration && durationSeconds <= 0)
                {
                    return null; // identical to the previous request
                }

                var (ok, result) = await GetJsonAsync("lrclib", LrclibApiBase + path);
                if (!ok)
                {
                    return null;
                }
                var parsed = FromJson(result, "lrclib");
                if (parsed.Lines.Count > 0)
                {
                    return parsed;
                }
                plainFallback = parsed;
                return null;
            }

            if (durationSeconds > 0)
            {
                var dated = await GetExactAsync(withDuration: true);
                if (dated != null)
                {
                    return dated;
                }
                if (IsPaused("lrclib"))
                {
                    return new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable");
                }
            }

            var direct = await GetExactAsync(withDuration: false);
            if (direct != null)
            {
                return direct;
            }
            if (IsPaused("lrclib"))
            {
                return new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable");
            }

            // 3. Search with both names, then 4. with the track alone: artist tags are often
            //    "Various Artists", a feat. list or a localized spelling the search cannot match
            //    (YouTube Music says "Anacon-daz", lrclib lists "Anacondaz"). An empty artist-scoped
            //    result counts as a failure and still opens the track-only fallback.
            string trackQuery = Uri.EscapeDataString(title);
            string artistQuery = Uri.EscapeDataString(artist);
            var (searchOk, searchResult) = await GetJsonAsync("lrclib",
                LrclibApiBase + $"/search?track_name={trackQuery}&artist_name={artistQuery}");
            if (!searchOk || searchResult.ValueKind != JsonValueKind.Array ||
                !searchResult.EnumerateArray().Any())
            {
                searchOk = false;
            }
            if (!searchOk && !IsPaused("lrclib"))
            {
                (searchOk, searchResult) = await GetJsonAsync("lrclib", LrclibApiBase + $"/search?q={trackQuery}");
                if (!searchOk || searchResult.ValueKind != JsonValueKind.Array)
                {
                    searchOk = false;
                }
            }

            var best = PickBestSearchResult(searchOk ? searchResult : default, durationSeconds, rawTitle, rawArtist, title, artist);
            if (best != null && best.Lines.Count > 0)
            {
                return best;
            }
            if (best != null)
            {
                plainFallback ??= best;
            }

            // Nothing synced here: a plain hit counts as a fallback, otherwise let the
            // other providers try.
            return plainFallback;
        }

        var providers = new List<Func<Task<LyricsResult?>>>
        {
            () => FetchLrcredAsync(rawTitle, rawArtist, title, artist, durationSeconds),
            () => FetchBetterLyricsAsync(rawTitle, rawArtist, durationSeconds),
            () => FetchLrcmuxAsync(rawTitle, rawArtist, title, artist, durationSeconds),
            () => FetchSyncLrcAsync(rawTitle, rawArtist, title, artist, durationSeconds),
            FetchLrclibAsync,
        };
        // Shuffle the order on every lookup to spread the load across backends.
        for (int i = providers.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (providers[i], providers[j]) = (providers[j], providers[i]);
        }

        LyricsResult? plainFallback = null;
        bool sawUnavailable = false;
        foreach (var fetch in providers)
        {
            LyricsResult? result;
            try
            {
                result = await fetch();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Lyrics provider failed for '{Artist}' - '{Title}': {Message}", rawArtist, rawTitle, ex.Message);
                continue;
            }
            if (result == null)
            {
                continue;
            }
            if (result.Source == "unavailable")
            {
                sawUnavailable = true;
                continue;
            }
            if (result.Found && result.Lines.Count > 0)
            {
                // Real word timings win as-is; when nobody has them, spread the words
                // evenly across each line span so the karaoke animation still moves.
                return WithEstimatedWords(result, durationSeconds);
            }
            if (result.Found)
            {
                plainFallback ??= result;
            }
        }

        // Nothing synced anywhere: a plain hit is still much better than nothing.
        return plainFallback ??
            (sawUnavailable
                ? new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable")
                : new LyricsResult(false, false, "", new List<LyricLine>(), "not-found"));
    }

    /// <summary>
    /// Picks the best search result. Versions of one song ("(Clean)", "(Paused)", "speed up")
    /// carry different lyrics, so besides synced lyrics and duration the raw title markers
    /// decide: a candidate with markers the query lacks is a different version, not a match.
    /// </summary>
    private LyricsResult? PickBestSearchResult(JsonElement searchResult, int durationSeconds, string rawTitle, string rawArtist, string title, string artist)
    {
        if (searchResult.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        if (IsPaused("lrclib"))
        {
            return null;
        }

        var queryMarkers = ExtractMarkers(rawTitle);

        JsonElement best = default;
        bool hasBest = false;
        int bestScore = int.MinValue;
        int bestDelta = int.MaxValue;
        int bestMarkerCount = int.MaxValue;
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
            int delta = int.MaxValue;
            if (durationSeconds > 0 && item.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
            {
                // lrclib reports the duration as a decimal, so GetInt32 would throw on "172.0".
                int candidate = (int)Math.Round(dur.GetDouble());
                delta = Math.Abs(candidate - durationSeconds);
                score += delta <= 2 ? 300
                    : delta <= 3 ? 270
                    : delta <= 5 ? 220
                    : delta <= 8 ? 150
                    : delta <= 15 ? 60
                    : delta <= 30 ? 0
                    : -300;
            }

            // Search results for the track-only query can carry unrelated artists; name
            // similarity keeps the right performer on top.
            score += NameSimilarity(item, "artistName", rawArtist) * 2;
            score += NameSimilarity(item, "trackName", rawTitle);

            // A "(Clean)" upload matched against an explicit query is a different text
            // (masked words), not a slightly worse title. Same for "(Paused)", censored
            // versions and the like; neutral markers (remaster, live) cost less.
            string candidateRaw = item.TryGetProperty("trackName", out var tn) && tn.ValueKind == JsonValueKind.String
                ? tn.GetString() ?? ""
                : "";
            if (Normalize(candidateRaw) == Normalize(rawTitle))
            {
                score += 200;
            }
            var candidateMarkers = ExtractMarkers(candidateRaw);
            if (candidateMarkers.SetEquals(queryMarkers))
            {
                score += 150;
            }
            else
            {
                foreach (var marker in candidateMarkers)
                {
                    if (!queryMarkers.Contains(marker))
                    {
                        score -= IsContentMarker(marker) ? 600 : 250;
                    }
                }
                foreach (var marker in queryMarkers)
                {
                    if (!candidateMarkers.Contains(marker))
                    {
                        score -= 150;
                    }
                }
            }

            int markerCount = candidateMarkers.Count;
            if (score > bestScore ||
                (score == bestScore && (delta < bestDelta ||
                    (delta == bestDelta && markerCount < bestMarkerCount))))
            {
                bestScore = score;
                bestDelta = delta;
                bestMarkerCount = markerCount;
                best = item;
                hasBest = true;
            }
        }

        return hasBest ? FromJson(best, "lrclib:search") : null;
    }

    /// <summary>0..100 token-overlap score between a search result field and the query.</summary>
    private static int NameSimilarity(JsonElement item, string field, string rawQuery)
    {
        if (!item.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return 0;
        }

        // Normalize both sides: case, punctuation and yo/ye no longer split identical names
        // ("Anacondaz" vs "AnacondaZ", "Мама," vs "Мама").
        return NameSimilarityRaw(value.GetString() ?? "", rawQuery);
    }

    /// <summary>0..100 token-overlap score between two raw strings.</summary>
    private static int NameSimilarityRaw(string aRaw, string bRaw)
    {
        string a = Normalize(aRaw);
        string b = Normalize(bRaw);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var left = a.Split(' ').Where(t => t.Length > 1).ToHashSet();
        var right = b.Split(' ').Where(t => t.Length > 1).ToList();
        if (left.Count == 0 || right.Count == 0)
        {
            return a.Equals(b, StringComparison.Ordinal) ? 100 : 0;
        }

        int shared = right.Count(left.Contains);
        return (int)(100.0 * shared / Math.Max(left.Count, right.Count));
    }

    /// <summary>Lowercase letters/digits words: "Мама, я люблю (Clean)" becomes "мама я люблю clean".</summary>
    private static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        string text = value.ToLowerInvariant().Replace('ё', 'е');
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[builder.Length - 1] != ' ')
            {
                builder.Append(' ');
            }
        }
        return builder.ToString().Trim();
    }

    /// <summary>Version markers from the raw title: "(Clean)" gives {"clean version", "clean"}.</summary>
    private static HashSet<string> ExtractMarkers(string rawTitle)
    {
        var markers = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(rawTitle))
        {
            return markers;
        }

        foreach (Match match in Regex.Matches(rawTitle, @"[\(\[](?<m>[^\)\]]+)[\)\]]"))
        {
            string inner = Normalize(match.Groups["m"].Value);
            if (inner.Length == 0)
            {
                continue;
            }
            markers.Add(inner);
            int space = inner.IndexOf(' ');
            if (space > 0)
            {
                markers.Add(inner[..space]);
            }
        }
        return markers;
    }

    /// <summary>Markers that change the lyrics text itself (masked words, cut lines).</summary>
    private static bool IsContentMarker(string marker) =>
        marker.Contains("clean", StringComparison.Ordinal) ||
        marker.Contains("paus", StringComparison.Ordinal) ||
        marker.Contains("censor", StringComparison.Ordinal) ||
        marker.Contains("explicit", StringComparison.Ordinal) ||
        marker.Contains("karaoke", StringComparison.Ordinal) ||
        marker.Contains("instrumental", StringComparison.Ordinal);

    /// <summary>Display label for the matched version, e.g. "(Clean)". Empty for plain titles.</summary>
    private static string ExtractVersionLabel(JsonElement element)
    {
        // The marker can sit in the track name ("Мама, я люблю (Clean)") or only in the
        // album ("Байки Инсайдера (Clean version)"): check both, track first.
        string track = element.TryGetProperty("trackName", out var trackName) && trackName.ValueKind == JsonValueKind.String
            ? trackName.GetString() ?? ""
            : "";
        string album = element.TryGetProperty("albumName", out var albumName) && albumName.ValueKind == JsonValueKind.String
            ? albumName.GetString() ?? ""
            : "";
        return ExtractVersionLabel(track, album);
    }

    /// <summary>Display label for the matched version from raw track/album names.</summary>
    private static string ExtractVersionLabel(string trackRaw, string albumRaw)
    {
        string[] candidates = { trackRaw, albumRaw };

        foreach (string raw in candidates)
        {
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }

            Match match = Regex.Match(raw, @"[\(\[](?<m>[^\)\]]+)[\)\]]");
            if (!match.Success)
            {
                continue;
            }

            string inner = match.Groups["m"].Value.Trim();
            string lowered = inner.ToLowerInvariant();
            if (lowered.Contains("clean") || lowered.Contains("paus") ||
                lowered.Contains("speed") || lowered.Contains("slow") ||
                lowered.Contains("nightcore") || lowered.Contains("remix") ||
                lowered.Contains("cover") || lowered.Contains("live") ||
                lowered.Contains("acoustic") || lowered.Contains("karaoke") ||
                lowered.Contains("explicit") || lowered.Contains("edit") ||
                lowered.Contains("version") || lowered.Contains("remaster"))
            {
                return "(" + inner + ")";
            }
        }
        return "";
    }

    /// <summary>
    /// Guarantees followable timing: synced lines keep real words (or get evenly
    /// spread estimates), and a plain-only hit is sliced into evenly spaced lines
    /// across the track duration, then word-estimated. Only a truly empty result
    /// (instrumental / not found) passes through untouched.
    /// </summary>
    internal static LyricsResult WithEstimatedTiming(LyricsResult result, int durationSeconds)
    {
        if (result.Lines.Count > 0)
        {
            return WithEstimatedWords(result, durationSeconds);
        }
        if (string.IsNullOrWhiteSpace(result.Plain))
        {
            return result;
        }

        var texts = result.Plain
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (texts.Count == 0)
        {
            return result;
        }

        long total = durationSeconds > 0 ? durationSeconds * 1000L : texts.Count * 4000L;
        long perLine = Math.Max(1500L, total / texts.Count);
        var lines = texts
            .Select((text, i) => new LyricLine(i * perLine, text, new List<LyricWord>()))
            .ToList();
        return WithEstimatedWords(result with { Lines = lines }, durationSeconds);
    }

    /// <summary>
    /// Spreads a line's words across its time span proportionally to their length.
    /// Used only when no provider has real word timings; lines that already carry
    /// words are left untouched.
    /// </summary>
    internal static LyricsResult WithEstimatedWords(LyricsResult result, int durationSeconds)
    {
        if (!result.Found || result.Lines.Count == 0)
        {
            return result;
        }
        if (result.Lines.All(line => line.Words.Count > 0))
        {
            return result;
        }

        var lines = new List<LyricLine>(result.Lines.Count);
        for (int i = 0; i < result.Lines.Count; i++)
        {
            var line = result.Lines[i];
            if (line.Words.Count > 0)
            {
                lines.Add(line);
                continue;
            }
            long spanEnd = i + 1 < result.Lines.Count
                ? result.Lines[i + 1].TimeMs
                : durationSeconds > 0 ? durationSeconds * 1000L : line.TimeMs + 8000;
            if (spanEnd <= line.TimeMs)
            {
                spanEnd = line.TimeMs + 4000;
            }
            lines.Add(line with { Words = DistributeWords(line.Text, line.TimeMs, spanEnd) });
        }
        return result with { Lines = lines };
    }

    private static List<LyricWord> DistributeWords(string text, long startMs, long endMs)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words = new List<LyricWord>(tokens.Length);
        if (tokens.Length == 0)
        {
            return words;
        }

        // Weight by letters so long words get more time; spaces ride with the word before them.
        int total = tokens.Sum(token => token.Length);
        long span = Math.Max(500, endMs - startMs);
        long cursor = startMs;
        for (int i = 0; i < tokens.Length; i++)
        {
            bool last = i + 1 >= tokens.Length;
            long share = total == 0 ? span / tokens.Length : span * tokens[i].Length / total;
            string wordText = last ? tokens[i] : tokens[i] + " ";
            words.Add(new LyricWord(cursor, last ? endMs : cursor + share, wordText));
            cursor += share;
        }
        return words;
    }

    private static bool IsPaused(ProviderBackoff state) =>
        DateTime.UtcNow < state.PausedUntil;

    private bool IsPaused(string provider) =>
        _backoff.TryGetValue(provider, out var state) && IsPaused(state);

    /// <summary>Escalating pause (2m, 4m, 8m... capped at 30m). Strikes reset only after
    /// a streak of successes, so one lucky hit between bans does not reopen the floodgate.</summary>
    private void NoteStrike(string provider, TimeSpan? retryAfter = null)
    {
        if (!_backoff.TryGetValue(provider, out var state))
        {
            return;
        }

        state.Strikes++;
        state.SuccessStreak = 0;
        double seconds = 120.0 * Math.Pow(2, Math.Min(state.Strikes - 1, 4));
        TimeSpan pause = TimeSpan.FromSeconds(Math.Min(seconds, 30.0 * 60.0));
        if (retryAfter is TimeSpan delta && delta > pause)
        {
            pause = delta;
        }

        state.PausedUntil = DateTime.UtcNow + pause;
        _logger.LogWarning(
            "{Provider} is unavailable, pausing lookups for {Minutes} minutes (strike {Strike})",
            provider, pause.TotalMinutes, state.Strikes);
    }

    private void NoteSuccess(string provider)
    {
        if (!_backoff.TryGetValue(provider, out var state))
        {
            return;
        }

        state.SuccessStreak++;
        if (state.SuccessStreak >= 5)
        {
            state.Strikes = 0;
        }
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta)
        {
            return delta;
        }
        if (retryAfter?.Date is DateTimeOffset date)
        {
            TimeSpan untilDate = date.UtcDateTime - DateTime.UtcNow;
            if (untilDate > TimeSpan.Zero)
            {
                return untilDate;
            }
        }
        return null;
    }

    /// <summary>
    /// better-lyrics (https://api.betterlyrics.org): TTML with syllable timing, backed by
    /// lrc.red's ISRC catalog. Cache hits are keyless; uncached queries answer 401 and the
    /// Kugou endpoint (LRC text, never needs a key) is tried next. Returns null when this
    /// provider has no answer and lrclib should be tried.
    /// </summary>
    private async Task<LyricsResult?> FetchBetterLyricsAsync(string rawTitle, string rawArtist, int durationSeconds)
    {
        const string provider = "better-lyrics";
        if (string.IsNullOrWhiteSpace(rawTitle) || IsPaused(provider))
        {
            return IsPaused(provider)
                ? new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable")
                : null;
        }

        string s = Uri.EscapeDataString(Clean(rawTitle));
        string a = Uri.EscapeDataString(Clean(rawArtist));
        string d = durationSeconds > 0 ? durationSeconds.ToString(CultureInfo.InvariantCulture) : "";

        // Main endpoint: best timing, but uncached queries need an API key we do not have.
        var main = await GetBetterLyricsAsync($"/getLyrics?s={s}&a={a}&al=&d={d}");
        if (main.Unavailable)
        {
            return new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable");
        }
        if (main.Ok)
        {
            var lines = ParseTtml(main.Body);
            if (lines.Count > 0)
            {
                NoteSuccess(provider);
                return new LyricsResult(true, false, "", lines, "better-lyrics");
            }
            // 200 with no usable lines: the Kugou twin uses different cache keys, try it.
        }

        var kugou = await GetBetterLyricsAsync($"/kugou/getLyrics?s={s}&a={a}&al=&d={d}");
        if (kugou.Unavailable)
        {
            return new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable");
        }
        if (kugou.Ok)
        {
            var lines = ParseLrc(kugou.Body);
            if (lines.Count > 0)
            {
                NoteSuccess(provider);
                return new LyricsResult(true, false, "", lines, "better-lyrics");
            }
        }

        return null;
    }

    private readonly record struct BetterLyricsResponse(bool Ok, bool Unavailable, string Body);

    /// <summary>
    /// lrc.red directly (https://lrc.red): the same catalog better-lyrics draws from, without
    /// its API-key wall. search.json finds the ISRC, then /s/{isrc}.ttml (word timing) or
    /// .lrc is fetched. Free and keyless.
    /// </summary>
    private async Task<LyricsResult?> FetchLrcredAsync(string rawTitle, string rawArtist, string title, string artist, int durationSeconds)
    {
        const string provider = "lrc.red";
        if (string.IsNullOrWhiteSpace(rawTitle) || IsPaused(provider))
        {
            return IsPaused(provider)
                ? new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable")
                : null;
        }

        var (searchOk, search) = await GetJsonAsync(provider,
            "https://lrc.red/search.json?q=" + Uri.EscapeDataString(Clean(rawTitle) + " " + Clean(rawArtist)));
        if (!searchOk || search.ValueKind != JsonValueKind.Object ||
            !search.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // Pick the best hit: same name-similarity + duration rules as the lrclib picker.
        var queryMarkers = ExtractMarkers(rawTitle);
        JsonElement best = default;
        bool hasBest = false;
        int bestScore = int.MinValue;
        int bestDelta = int.MaxValue;
        foreach (var hit in hits.EnumerateArray())
        {
            if (hit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string hitTitle = hit.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            string hitArtist = hit.TryGetProperty("artist", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString() ?? "" : "";
            string hitAlbum = hit.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.String ? al.GetString() ?? "" : "";
            if (hitTitle.Length == 0)
            {
                continue;
            }

            int score = NameSimilarityRaw(hitArtist, rawArtist) * 2 + NameSimilarityRaw(hitTitle, rawTitle);
            if (Normalize(hitTitle) == Normalize(rawTitle))
            {
                score += 200;
            }
            int delta = int.MaxValue;
            if (durationSeconds > 0 && hit.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
            {
                delta = Math.Abs((int)Math.Round(dur.GetDouble()) - durationSeconds);
                score += delta <= 2 ? 300 : delta <= 3 ? 270 : delta <= 5 ? 220 : delta <= 8 ? 150 : delta <= 15 ? 60 : delta <= 30 ? 0 : -300;
            }
            var hitMarkers = ExtractMarkers(hitTitle);
            if (hitMarkers.SetEquals(queryMarkers))
            {
                score += 150;
            }
            else
            {
                foreach (var marker in hitMarkers)
                {
                    if (!queryMarkers.Contains(marker))
                    {
                        score -= IsContentMarker(marker) ? 600 : 250;
                    }
                }
                foreach (var marker in queryMarkers)
                {
                    if (!hitMarkers.Contains(marker))
                    {
                        score -= 150;
                    }
                }
            }

            if (score > bestScore || (score == bestScore && delta < bestDelta))
            {
                bestScore = score;
                bestDelta = delta;
                best = hit;
                hasBest = true;
            }
        }
        if (!hasBest)
        {
            return null;
        }

        string isrc = best.TryGetProperty("isrc", out var isrcProp) && isrcProp.ValueKind == JsonValueKind.String
            ? isrcProp.GetString() ?? ""
            : "";
        if (isrc.Length == 0)
        {
            return null;
        }

        string hitTitleFinal = best.TryGetProperty("title", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() ?? "" : "";
        string hitAlbumFinal = best.TryGetProperty("album", out var ta) && ta.ValueKind == JsonValueKind.String ? ta.GetString() ?? "" : "";
        string version = ExtractVersionLabel(hitTitleFinal, hitAlbumFinal);

        var (ttmlOk, ttml) = await GetTextAsync(provider, "https://lrc.red/s/" + isrc + ".ttml");
        if (ttmlOk)
        {
            var lines = ParseTtml(ttml);
            if (lines.Count > 0)
            {
                return new LyricsResult(true, false, "", lines, "lrc.red", version);
            }
        }
        else if (IsPaused(provider))
        {
            return new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable");
        }
        var (lrcOk, lrc) = await GetTextAsync(provider, "https://lrc.red/s/" + isrc + ".lrc");
        if (lrcOk)
        {
            var lines = ParseLrc(lrc);
            if (lines.Count > 0)
            {
                return new LyricsResult(true, false, "", lines, "lrc.red", version);
            }
        }
        else if (IsPaused(provider))
        {
            return new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable");
        }

        return null;
    }

    /// <summary>
    /// lrcmux (https://lrcmux-api.fly.dev): fan-out aggregator over public providers.
    /// JSON with level=word carries per-word timings when the upstream has them.
    /// </summary>
    private async Task<LyricsResult?> FetchLrcmuxAsync(string rawTitle, string rawArtist, string title, string artist, int durationSeconds)
    {
        const string provider = "lrcmux";
        if (string.IsNullOrWhiteSpace(rawTitle) || IsPaused(provider))
        {
            return IsPaused(provider)
                ? new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable")
                : null;
        }

        string url = "https://lrcmux-api.fly.dev/get?artist=" + Uri.EscapeDataString(Clean(rawArtist)) +
                     "&title=" + Uri.EscapeDataString(Clean(rawTitle)) +
                     (durationSeconds > 0 ? "&duration=" + durationSeconds.ToString(CultureInfo.InvariantCulture) : "") +
                     "&level=word&format=json";
        var (ok, element) = await GetJsonAsync(provider, url);
        if (!ok || element.ValueKind != JsonValueKind.Object)
        {
            return IsPaused(provider)
                ? new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable")
                : null;
        }

        string trackTitle = "";
        if (element.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object)
        {
            trackTitle = track.TryGetProperty("title", out var tt) && tt.ValueKind == JsonValueKind.String
                ? tt.GetString() ?? ""
                : "";
        }
        string version = ExtractVersionLabel(trackTitle, "");

        if (!element.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<LyricLine>();
        foreach (var item in lines.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string text = item.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String
                ? (tx.GetString() ?? "").Trim()
                : "";
            if (text.Length == 0)
            {
                continue;
            }
            long start = JsonInt64(item, "start");
            var words = new List<LyricWord>();
            if (item.TryGetProperty("words", out var ws) && ws.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in ws.EnumerateArray())
                {
                    if (w.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    string wtext = w.TryGetProperty("text", out var wt) && wt.ValueKind == JsonValueKind.String
                        ? (wt.GetString() ?? "").Trim()
                        : "";
                    if (wtext.Length == 0)
                    {
                        continue;
                    }
                    words.Add(new LyricWord(JsonInt64(w, "start"), JsonInt64(w, "end"), wtext));
                }
            }
            result.Add(new LyricLine(start, text, words));
        }
        result.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

        if (result.Count > 0)
        {
            return new LyricsResult(true, false, "", result, "lrcmux", version);
        }
        return null;
    }

    /// <summary>
    /// SyncLRC (https://api.synclrc.dev): JSON with synced/karaoke/plain lines. Cached
    /// queries are keyless; the service is occasionally down (503), then it is skipped.
    /// </summary>
    private async Task<LyricsResult?> FetchSyncLrcAsync(string rawTitle, string rawArtist, string title, string artist, int durationSeconds)
    {
        const string provider = "synclrc";
        if (string.IsNullOrWhiteSpace(rawTitle) || IsPaused(provider))
        {
            return IsPaused(provider)
                ? new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable")
                : null;
        }

        string url = "https://api.synclrc.dev/lyrics?track=" + Uri.EscapeDataString(Clean(rawTitle)) +
                     "&artist=" + Uri.EscapeDataString(Clean(rawArtist)) + "&type=karaoke&format=lrc" +
                     (durationSeconds > 0 ? "&duration=" + durationSeconds.ToString(CultureInfo.InvariantCulture) : "");
        var (ok, element) = await GetJsonAsync(provider, url);
        if (!ok || element.ValueKind != JsonValueKind.Object)
        {
            return IsPaused(provider)
                ? new LyricsResult(false, false, "", new List<LyricLine>(), "unavailable")
                : null;
        }

        // Karaoke answers carry the timed text in "synced", plain text in "lyrics".
        string[] candidates =
        {
            element.TryGetProperty("synced", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "",
            element.TryGetProperty("lyrics", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : ""
        };
        foreach (string text in candidates)
        {
            var lines = ParseLrc(text);
            if (lines.Count > 0)
            {
                string track = element.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                string album = element.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.String ? al.GetString() ?? "" : "";
                return new LyricsResult(true, false, "", lines, "synclrc", ExtractVersionLabel(track, album));
            }
        }
        return null;
    }

    /// <summary>Plain-text fetch for endpoints that serve LRC/TTML/documents as text.</summary>
    private async Task<(bool Ok, string Body)> GetTextAsync(string provider, string url)
    {
        if (IsPaused(provider))
        {
            return (false, "");
        }

        try
        {
            using var response = await _http.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.NotFound ||
                response.StatusCode == HttpStatusCode.Unauthorized)
            {
                NoteSuccess(provider);
                return (false, "");
            }
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable ||
                response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                NoteStrike(provider, RetryAfterOf(response));
                return (false, "");
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Provider} returned {Status} for {Path}", provider, response.StatusCode, url);
                return (false, "");
            }

            NoteSuccess(provider);
            return (true, await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("{Provider} request failed for {Path}: {Message}", provider, url, ex.Message);
            return (false, "");
        }
    }

    private async Task<BetterLyricsResponse> GetBetterLyricsAsync(string path)
    {
        const string provider = "better-lyrics";
        if (IsPaused(provider))
        {
            return new BetterLyricsResponse(false, true, "");
        }

        try
        {
            using var response = await _http.GetAsync(BetterLyricsApiBase + path);
            if (response.StatusCode == HttpStatusCode.NotFound ||
                response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // 404 = nothing cached here (lrclib may still have it), 401 = uncached
                // query without a key (Kugou twin may still answer). Neither is a ban.
                NoteSuccess(provider);
                return new BetterLyricsResponse(false, false, "");
            }
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable ||
                response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                NoteStrike(provider, RetryAfterOf(response));
                return new BetterLyricsResponse(false, true, "");
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("better-lyrics returned {Status} for {Path}", response.StatusCode, path);
                return new BetterLyricsResponse(false, false, "");
            }

            string body = await response.Content.ReadAsStringAsync();
            NoteSuccess(provider);
            return new BetterLyricsResponse(true, false, body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("better-lyrics request failed for {Path}: {Message}", path, ex.Message);
            return new BetterLyricsResponse(false, false, "");
        }
    }

    /// <summary>Parses TTML paragraphs into timed lines, keeping word spans as words.</summary>
    internal static List<LyricLine> ParseTtml(string ttml)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(ttml))
        {
            return result;
        }

        foreach (Match match in Regex.Matches(ttml,
                     @"<p\b[^>]*?\bbegin\s*=\s*""([^""]+)""[^>]*>(.*?)</p\s*>",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            string inner = match.Groups[2].Value;
            var words = new List<LyricWord>();
            foreach (Match span in Regex.Matches(inner,
                         @"<span\b[^>]*?\bbegin\s*=\s*""([^""]+)""[^>]*>(.*?)</span\s*>",
                         RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                string wordText = System.Net.WebUtility.HtmlDecode(
                    Regex.Replace(span.Groups[2].Value, @"<[^>]+>", "").Trim());
                wordText = Regex.Replace(wordText, @"\s+", " ");
                if (wordText.Length == 0)
                {
                    continue;
                }
                if (!TryParseTtmlTime(span.Groups[1].Value, out long wordMs))
                {
                    continue;
                }
                long wordEnd = 0;
                var endAttr = Regex.Match(span.Value, @"\bend\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                if (endAttr.Success)
                {
                    TryParseTtmlTime(endAttr.Groups[1].Value, out wordEnd);
                }
                words.Add(new LyricWord(wordMs, wordEnd, wordText));
            }
            words.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

            string text = System.Net.WebUtility.HtmlDecode(
                Regex.Replace(inner, @"<[^>]+>", " ").Trim());
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length == 0)
            {
                continue;
            }
            if (!TryParseTtmlTime(match.Groups[1].Value, out long timeMs))
            {
                continue;
            }
            result.Add(new LyricLine(timeMs, text, words));
        }

        result.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return result;
    }

    private static bool TryParseTtmlTime(string value, out long timeMs)
    {
        timeMs = 0;
        Match match = Regex.Match(value.Trim(), @"^(?:(\d+):)?([0-5]?\d):([0-5]?\d)(?:[.,](\d{1,3}))?$");
        if (!match.Success)
        {
            return false;
        }

        int hours = match.Groups[1].Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        int minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        int millis = 0;
        string fraction = match.Groups[4].Value;
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

        timeMs = ((hours * 60L + minutes) * 60L + seconds) * 1000L + millis;
        return true;
    }

    private async Task<(bool Ok, JsonElement Element)> GetJsonAsync(string provider, string url)
    {
        // A throttled provider is skipped so the warmup does not re-open the ban window.
        if (IsPaused(provider))
        {
            return (false, default);
        }

        try
        {
            using var response = await _http.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                NoteSuccess(provider);
                return (false, default);
            }
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable ||
                response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                NoteStrike(provider, RetryAfterOf(response));
                return (false, default);
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Provider} returned {Status} for {Path}", provider, response.StatusCode, url);
                return (false, default);
            }

            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            NoteSuccess(provider);
            return (true, document.RootElement.Clone());
        }
        catch (HttpRequestException ex) when ((ex.StatusCode ?? 0) == HttpStatusCode.TooManyRequests ||
                                              (ex.StatusCode ?? 0) == HttpStatusCode.ServiceUnavailable)
        {
            // Reached when HttpClient surfaces the status as an exception (auto-decompress
            // failures, etc.) - apply the same backoff as the normal status path.
            NoteStrike(provider, null);
            var state = _backoff[provider];
            _logger.LogWarning(
                "{Provider} unavailable via exception ({Status}), pausing (strike {Strike})",
                provider, ex.StatusCode, state.Strikes);
            return (false, default);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("{Provider} request failed for {Path}: {Message}", provider, url, ex.Message);
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

        return new LyricsResult(found, instrumental, plain, lines, source, ExtractVersionLabel(element));
    }

    private static LyricsResult ParseCached(string raw, string key)
    {
        // The cache file stores the plain lyrics followed by the timed ones so a plain-only
        // entry stays readable and the timed entry can be restored without a network call.
        // A trailing "--source--" section remembers which provider served it; files written
        // before that section existed are lrclib results.
        string source = "lrclib";
        const string sourceMarker = "\n--source--\n";
        int sourceAt = raw.LastIndexOf(sourceMarker, StringComparison.Ordinal);
        if (sourceAt >= 0)
        {
            source = raw[(sourceAt + sourceMarker.Length)..].Trim();
            if (source.Length == 0)
            {
                source = "lrclib";
            }
            raw = raw[..sourceAt];
        }

        int separator = raw.IndexOf("\n--synced--\n", StringComparison.Ordinal);
        if (separator < 0)
        {
            string plain = raw.Trim();
            return new LyricsResult(plain.Length > 0, false, plain, new List<LyricLine>(), source);
        }

        string plainPart = raw[..separator].Trim();
        string syncedPart = raw[(separator + "\n--synced--\n".Length)..];

        // Optional per-word timings, grouped under the line start they belong to:
        // L{lineMs} W{startMs}>{endMs} {word}
        const string wordsMarker = "\n--words--\n";
        string wordsRaw = "";
        int wordsAt = syncedPart.IndexOf(wordsMarker, StringComparison.Ordinal);
        if (wordsAt >= 0)
        {
            wordsRaw = syncedPart[(wordsAt + wordsMarker.Length)..];
            syncedPart = syncedPart[..wordsAt];
        }
        var lines = ParseLrc(syncedPart);
        if (wordsRaw.Length > 0)
        {
            var byLine = new Dictionary<long, List<LyricWord>>();
            foreach (var wordLine in wordsRaw.Split('\n'))
            {
                var m = Regex.Match(wordLine.Trim(), @"^L(\d+) W(\d+)>(\d+) (.*)$");
                if (!m.Success)
                {
                    continue;
                }
                long lineMs = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var word = new LyricWord(
                    long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                    m.Groups[4].Value.Trim());
                if (word.Text.Length == 0)
                {
                    continue;
                }
                if (!byLine.TryGetValue(lineMs, out var list))
                {
                    list = new List<LyricWord>();
                    byLine[lineMs] = list;
                }
                list.Add(word);
            }
            if (byLine.Count > 0)
            {
                var merged = new List<LyricLine>(lines.Count);
                foreach (var line in lines)
                {
                    merged.Add(byLine.TryGetValue(line.TimeMs, out var words)
                        ? line with { Words = words }
                        : line);
                }
                lines = merged;
            }
        }

        return new LyricsResult(true, false, plainPart, lines, source);
    }

    /// <summary>Stores plain lyrics and timed lyrics in one file so both survive a restart.</summary>
    private static string Serialize(LyricsResult result)
    {
        var builder = new StringBuilder();
        if (result.Lines.Count == 0)
        {
            builder.Append(result.Plain);
        }
        else
        {
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
            if (result.Lines.Any(line => line.Words.Count > 0))
            {
                builder.Append("\n--words--\n");
                foreach (var line in result.Lines)
                {
                    foreach (var word in line.Words)
                    {
                        string cleanWord = word.Text.Replace('\n', ' ').Replace('\r', ' ').Trim();
                        if (cleanWord.Length == 0)
                        {
                            continue;
                        }
                        builder.Append('L');
                        builder.Append(line.TimeMs.ToString(CultureInfo.InvariantCulture));
                        builder.Append(" W");
                        builder.Append(word.TimeMs.ToString(CultureInfo.InvariantCulture));
                        builder.Append('>');
                        builder.Append(word.EndMs.ToString(CultureInfo.InvariantCulture));
                        builder.Append(' ');
                        builder.Append(cleanWord);
                        builder.Append('\n');
                    }
                }
            }
        }
        builder.Append("\n--source--\n");
        builder.Append(string.IsNullOrWhiteSpace(result.Source) ? "lrclib" : result.Source.Trim());
        builder.Append('\n');
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
            // Some providers (lrcmux word-sync) embed inline <mm:ss.xx> word stamps and
            // lightweight markup inside the line text: turn the stamps into words, then
            // strip them, keeping things like "<3".
            var words = ParseInlineWordStamps(text);
            text = Regex.Replace(text, @"<\d{1,3}:\d{1,2}(?:[.,]\d{1,3})?>", " ");
            text = Regex.Replace(text, @"<[a-zA-Z/][^<>]*>", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
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

                result.Add(new LyricLine((minutes * 60L + seconds) * 1000L + millis, text, words));
            }
        }

        result.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return result;
    }

    /// <summary>Turns inline word stamps ("&lt;00:34.95&gt;Ночь &lt;00:35.46&gt;коротка")
    /// into timed words. A word lasts until the next stamp starts.</summary>
    internal static List<LyricWord> ParseInlineWordStamps(string text)
    {
        var words = new List<LyricWord>();
        var stamps = new List<(long TimeMs, int Index, int Length)>();
        foreach (Match match in Regex.Matches(text, @"<(\d{1,3}):(\d{1,2})(?:[.,](\d{1,3}))?>"))
        {
            int minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            string fraction = match.Groups[3].Value;
            int millis = fraction.Length switch
            {
                1 => int.Parse(fraction, CultureInfo.InvariantCulture) * 100,
                2 => int.Parse(fraction, CultureInfo.InvariantCulture) * 10,
                3 => int.Parse(fraction, CultureInfo.InvariantCulture),
                _ => 0,
            };
            stamps.Add((((minutes * 60L + seconds) * 1000L) + millis, match.Index, match.Length));
        }
        if (stamps.Count == 0)
        {
            return words;
        }

        for (int i = 0; i < stamps.Count; i++)
        {
            int textStart = stamps[i].Index + stamps[i].Length;
            int textEnd = i + 1 < stamps.Count ? stamps[i + 1].Index : text.Length;
            string word = Regex.Replace(text[textStart..textEnd], @"\s+", " ").Trim();
            if (word.Length == 0)
            {
                continue;
            }
            long end = i + 1 < stamps.Count ? stamps[i + 1].TimeMs : 0;
            words.Add(new LyricWord(stamps[i].TimeMs, end, word));
        }
        return words;
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

    private static long JsonInt64(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
        {
            try
            {
                return (long)Math.Round(value.GetDouble());
            }
            catch
            {
                return 0;
            }
        }
        return 0;
    }

    private static string BuildKey(string artist, string title, long durationMs)
    {
        long seconds = durationMs > 0 ? durationMs / 1000L : 0;
        string raw = $"{artist.ToLowerInvariant()}|{title.ToLowerInvariant()}|{seconds}";
        // string.GetHashCode() is randomized per process on .NET, so a hash-based cache file
        // name would never match again after a restart. SHA-256 is stable across runs.
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash, 0, 4);
    }

    private static string CachePath(string key) =>
        Path.Combine(AppPaths.DataSubDir(CacheDirectoryName), key + ".lyrics");
}
