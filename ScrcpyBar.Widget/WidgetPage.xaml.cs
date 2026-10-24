using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.UI;

using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using Microsoft.Gaming.XboxGameBar;

namespace ScrcpyBar.Widget
{
    public sealed partial class WidgetPage : Page
    {
        private XboxGameBarWidget _widget;
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private const string BaseApi = "http://127.0.0.1:39281";
        // Segoe MDL2 Assets private-use codepoints for the transport glyphs.
        private static readonly string GlyphPlay = ((char)0xE768).ToString();
        private static readonly string GlyphPause = ((char)0xE769).ToString();
        private bool _isPlaying = false;
    private bool? _isLiked;
        private bool _isSeeking;
        private double _durationSeconds;
        private double _positionSeconds;
        private double _seekSeconds;
        private string _coverKey = "";
        private bool _coverLoaded;
        private DateTime _coverAttemptedAt = DateTime.MinValue;

        // --- karaoke lyrics -------------------------------------------------
        private readonly DispatcherTimer _karaokeTimer = new DispatcherTimer();
        private readonly List<LyricLine> _lyrics = new List<LyricLine>();
        private bool _lyricsEnabled;
        private bool _lyricsLoading;
        private bool _lyricsFetchPending;
        private string _lyricsKey = "";
        private string _karaokeText = "";
        private int _lastLyricIndex = -2;
        private double _positionMs;
        private DateTime _positionStampUtc = DateTime.UtcNow;
        private string _currentTitle = "";
        private string _currentArtist = "";
        private string _lastLoggedTrack = "";
        private DateTime _lyricsLastAttemptUtc = DateTime.MinValue;
        private bool _lyricsUnavailable;

        // --- click a line to jump there ----------------------------------------
        // A cold lyrics lookup can take a few seconds while the bridge reaches lrclib.net.
        private readonly HttpClient _lyricsHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private readonly HttpClient _seekHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private int _prevIndex = -1;
        private int _curIndex = -1;
        private int _nextIndex = -1;

        private sealed class LyricWord
        {
            public long TimeMs;
            public long EndMs;
            public string Text = "";
        }

        private sealed class LyricLine
        {
            public long TimeMs;
            public string Text = "";
            public List<LyricWord> Words = new List<LyricWord>();
        }

        public WidgetPage()
        {
            this.InitializeComponent();

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += async (s, e) => await FetchTrackAsync();

            // The current line has to follow the song, so the check runs on its own timer
            // instead of riding on the one second track poll. It only runs while the panel is
            // open, the rest of the time the widget stays idle.
            _karaokeTimer.Interval = TimeSpan.FromMilliseconds(100);
            _karaokeTimer.Tick += (s, e) => UpdateKaraoke();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.Parameter is XboxGameBarWidgetActivatedEventArgs widgetArgs)
            {
                _widget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, Frame);
            }

            _timer.Start();
            if (_lyricsEnabled)
            {
                _karaokeTimer.Start();
            }
            _ = FetchTrackAsync();
            // The bridge may have resolved the lyrics since the last attempt (restart, backoff
            // expired), so opening the panel re-asks instead of sticking to the old failure.
            if (_lyricsEnabled && _lyricsUnavailable)
            {
                _ = FetchLyricsAsync();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _timer.Stop();
            _karaokeTimer.Stop();
        }

        private async Task FetchTrackAsync()
        {
            try
            {
                string payload = await GetUtf8StringAsync(_http, $"{BaseApi}/api/track");
                if (JsonObject.TryParse(payload, out JsonObject json))
                {
                    ApplyTrackData(json);
                }

                // The bridge might have been busy when the lyrics were first requested, so the
                // same song is re-tried every 30s while the panel is open.
                if (_lyricsEnabled && _lyricsUnavailable && !_lyricsLoading &&
                    (DateTime.UtcNow - _lyricsLastAttemptUtc).TotalSeconds > 30)
                {
                    _ = FetchLyricsAsync();
                }
            }
            catch
            {
                StatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68)); // Red
                StatusText.Text = "Мост офлайн";
                TrackTitleText.Text = "Служба не запущена";
                TrackArtistText.Text = "Запусти ScrcpyBar.Bridge";
                TrackAppText.Text = "--";
                ClearCover();
                MusicIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 107, 114, 128));
            }
        }

        /// <summary>
        /// Reads a response as raw bytes and decodes them as UTF-8.
        ///
        /// The bridge already sends `Content-Type: application/json; charset=utf-8` and UTF-8
        /// bytes, but the UWP HttpClient does not reliably honour the charset parameter, so
        /// `GetStringAsync` turned every Cyrillic track title into mojibake. Decoding the bytes
        /// explicitly removes the only step that could re-encode the payload.
        /// </summary>
        private static async Task<string> GetUtf8StringAsync(HttpClient client, string url)
        {
            using (var response = await client.GetAsync(new Uri(url)))
            {
                byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                return new UTF8Encoding(false).GetString(bytes);
            }
        }

        private void ApplyTrackData(JsonObject json)
        {
            bool connected = json.GetNamedBoolean("connected", false);
            bool hasTrack = json.GetNamedBoolean("hasTrack", false);
            string title = json.GetNamedString("title", "");
            string artist = json.GetNamedString("artist", "");
            string appName = json.GetNamedString("appName", "");
            _isPlaying = json.GetNamedBoolean("isPlaying", false);
            double volume = json.GetNamedNumber("volumeCurrent", 0);

            if (!connected)
            {
                StatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68));
                StatusText.Text = "Нет телефона";
                TrackTitleText.Text = "Телефон не подключен";
                TrackArtistText.Text = "Включи отладку по USB";
                TrackAppText.Text = "--";
                ClearCover();
                MusicIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 107, 114, 128));
                PlayIcon.Glyph = GlyphPlay;
                LikeButton.Visibility = Visibility.Collapsed;
                return;
            }

            StatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129)); // Green
            StatusText.Text = "Онлайн";

                TrackTitleText.Text = string.IsNullOrWhiteSpace(title) ? "Нет трека" : title;
                TrackArtistText.Text = string.IsNullOrWhiteSpace(artist) ? (hasTrack ? "" : "Запусти музыку на телефоне") : artist;
                TrackAppText.Text = appName;

                bool? isLiked = null;
                if (json.TryGetValue("isLiked", out var likedValue) && likedValue.ValueType == JsonValueType.Boolean)
                {
                    isLiked = likedValue.GetBoolean();
                }
                _isLiked = isLiked;
                UpdateLikeButton();

            // Log the decoded text so an encoding problem shows up in widget.log instead of
            // having to be guessed from a screenshot.
            string logKey = $"{title}|{artist}|{appName}";
            if (logKey != _lastLoggedTrack)
            {
                _lastLoggedTrack = logKey;
                SeekLog.Write($"track title='{title}' artist='{artist}' app='{appName}'");
            }

            VolText.Text = volume.ToString("0");
            UpdateProgress(json);
            _ = RefreshCoverAsync(json);
            _currentTitle = title;
            _currentArtist = artist;
            TrackLyricsKey(hasTrack, title, artist);

            if (_isPlaying)
            {
                PlayIcon.Glyph = GlyphPause;
                MusicIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 124, 65)); // Xbox green
            }
            else
            {
                PlayIcon.Glyph = GlyphPlay;
                MusicIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175));
            }
        }

        private void ClearCover()
        {
            _coverKey = "";
            _coverLoaded = false;
            CoverImage.Source = null;
            CoverImage.Visibility = Visibility.Collapsed;
            MusicIcon.Visibility = Visibility.Visible;
        }

        private async Task RefreshCoverAsync(JsonObject json)
        {
            if (!json.GetNamedBoolean("hasTrack", false))
            {
                ClearCover();
                return;
            }

            string albumArtPath = json.GetNamedString("albumArtPath", "");
            string key = string.IsNullOrWhiteSpace(albumArtPath)
                ? ""
                : $"{albumArtPath}|{json.GetNamedString("title", "")}|{json.GetNamedString("artist", "")}";

            if (_coverLoaded && key.Length > 0 && key == _coverKey)
            {
                return;
            }

            // Retry the same track at a slow cadence so a cover that shows up late still appears.
            if (key == _coverKey && (DateTime.UtcNow - _coverAttemptedAt).TotalSeconds < 5)
            {
                return;
            }

            _coverKey = key;
            _coverAttemptedAt = DateTime.UtcNow;
            _coverLoaded = false;
            CoverImage.Visibility = Visibility.Collapsed;
            MusicIcon.Visibility = Visibility.Visible;

            try
            {
                var response = await _http.GetAsync(new Uri($"{BaseApi}/api/cover?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"));
                if (!response.IsSuccessStatusCode)
                {
                    return;
                }

                byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0 || _coverKey != key)
                {
                    return;
                }

                var bitmap = new Windows.UI.Xaml.Media.Imaging.BitmapImage();
                using (var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                using (var writer = new Windows.Storage.Streams.DataWriter(stream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    writer.DetachStream();
                    stream.Seek(0);
                    await bitmap.SetSourceAsync(stream);
                }

                if (_coverKey != key)
                {
                    return;
                }

                CoverImage.Source = bitmap;
                CoverImage.Visibility = Visibility.Visible;
                MusicIcon.Visibility = Visibility.Collapsed;
                _coverLoaded = true;
                FadeIn(CoverImage, 0, 1, 320);
            }
            catch
            {
            }
        }

        // ---------------------------------------------------------------- karaoke lyrics

        private void LyricsToggle_Click(object sender, RoutedEventArgs e)
        {
            _lyricsEnabled = !_lyricsEnabled;
            LyricsToggleBtn.Background = _lyricsEnabled
                ? new SolidColorBrush(Color.FromArgb(255, 16, 124, 65))
                : new SolidColorBrush(Color.FromArgb(255, 21, 255, 255));
            LyricsToggleBtn.Foreground = _lyricsEnabled
                ? new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))
                : new SolidColorBrush(Color.FromArgb(255, 156, 163, 175));

            TrackInfoPanel.Visibility = _lyricsEnabled ? Visibility.Collapsed : Visibility.Visible;
            LyricsPanel.Visibility = _lyricsEnabled ? Visibility.Visible : Visibility.Collapsed;
            FadeIn(_lyricsEnabled ? LyricsPanel : TrackInfoPanel, 0, 1, 240);

            if (_lyricsEnabled)
            {
                _karaokeTimer.Start();
                TrackLyricsKey(!string.IsNullOrWhiteSpace(_currentTitle), _currentTitle, _currentArtist);
                UpdateKaraoke();
            }
            else
            {
                // Nothing to animate while the panel is closed.
                _karaokeTimer.Stop();
            }
        }

        /// <summary>Fetches lyrics again whenever the song changes while the panel is open.</summary>
        private void TrackLyricsKey(bool hasTrack, string title, string artist)
        {
            if (!_lyricsEnabled)
            {
                return;
            }

            string key = hasTrack ? $"{title}|{artist}" : "";
            if (key == _lyricsKey)
            {
                return;
            }

            _lyricsKey = key;
            _lyrics.Clear();
            _karaokeText = "";
            _prevIndex = -1;
            _curIndex = -1;
            _nextIndex = -1;
            _lastLyricIndex = -2;
            SetCurrentLine("");
            LyricPrev.Text = "";
            LyricNext.Text = "";

            if (!hasTrack)
            {
                LyricsStatus.Text = "Нет трека";
                return;
            }

            _ = FetchLyricsAsync();
        }

        private async Task FetchLyricsAsync()
        {
            if (_lyricsLoading)
            {
                // A lookup is already running. Remember the request instead of dropping it, or
                // the new track would never get its lyrics.
                _lyricsFetchPending = true;
                return;
            }

            _lyricsLoading = true;
            _lyricsLastAttemptUtc = DateTime.UtcNow;
            string requestedKey = _lyricsKey;
            try
            {
                // Scoped to the exact track the widget shows: right after a track switch the
                // bridge's "now playing" can still be the previous song.
                var query = new System.Text.StringBuilder("?t=").Append(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (requestedKey.Length > 0)
                {
                    query.Append("&title=").Append(Uri.EscapeDataString(_currentTitle));
                    query.Append("&artist=").Append(Uri.EscapeDataString(_currentArtist));
                    if (_durationSeconds > 0)
                    {
                        query.Append("&durationMs=").Append((long)Math.Round(_durationSeconds * 1000));
                    }
                }
                string payload = await GetUtf8StringAsync(_lyricsHttp, $"{BaseApi}/api/lyrics{query}");
                if (!JsonObject.TryParse(payload, out JsonObject json))
                {
                    LyricsStatus.Text = "Слова недоступны";
                    _lyricsUnavailable = true;
                    return;
                }

                bool found = json.GetNamedBoolean("ok", false);
                bool instrumental = json.GetNamedBoolean("instrumental", false);
                bool synced = json.GetNamedBoolean("synced", false);
                string plain = json.GetNamedString("plain", "");
                string version = json.GetNamedString("version", "");
                string source = json.GetNamedString("source", "lrclib");
                _lyricsUnavailable = !found && !instrumental;

            if (instrumental)
            {
                if (requestedKey == _lyricsKey)
                {
                    LyricsStatus.Text = "Инструментальная композиция";
                    SetCurrentLine("");
                }
                else
                {
                    _lyricsFetchPending = true;
                }
                return;
            }

            if (!found)
            {
                if (requestedKey == _lyricsKey)
                {
                    // Same track, genuinely nothing: clear the lines too, or the panel
                    // keeps showing stale lyrics under a "not found" label.
                    LyricsStatus.Text = "Слова не найдены";
                    _lyrics.Clear();
                    _karaokeText = "";
                    _prevIndex = -1;
                    _curIndex = -1;
                    _nextIndex = -1;
                    _lastLyricIndex = -2;
                    SetCurrentLine("");
                    LyricPrev.Text = "";
                    LyricNext.Text = "";
                }
                else
                {
                    _lyricsFetchPending = true;
                }
                return;
            }


                var parsed = new List<LyricLine>();
                // Structured lines (with per-word timings when the provider has them).
                // linesText stays as a fallback for older bridge builds.
                bool usedStructured = false;
                if (json.TryGetValue("lines", out var linesValue) && linesValue.ValueType == JsonValueType.Array)
                {
                    foreach (var lineValue in linesValue.GetArray())
                    {
                        if (lineValue.ValueType != JsonValueType.Object)
                        {
                            continue;
                        }
                        var lineObj = lineValue.GetObject();
                        var line = new LyricLine
                        {
                            TimeMs = (long)lineObj.GetNamedNumber("timeMs", 0),
                            Text = lineObj.GetNamedString("text", "")
                        };
                        if (lineObj.TryGetValue("words", out var wordsValue) && wordsValue.ValueType == JsonValueType.Array)
                        {
                            foreach (var wordValue in wordsValue.GetArray())
                            {
                                if (wordValue.ValueType != JsonValueType.Object)
                                {
                                    continue;
                                }
                                var wordObj = wordValue.GetObject();
                                string wordText = wordObj.GetNamedString("text", "");
                                if (wordText.Length == 0)
                                {
                                    continue;
                                }
                                line.Words.Add(new LyricWord
                                {
                                    TimeMs = (long)wordObj.GetNamedNumber("t", 0),
                                    EndMs = (long)wordObj.GetNamedNumber("e", 0),
                                    Text = wordText
                                });
                            }
                        }
                        parsed.Add(line);
                    }
                    usedStructured = parsed.Count > 0;
                }
                if (!usedStructured)
                {
                    string linesText = json.GetNamedString("linesText", "");
                    if (!string.IsNullOrWhiteSpace(linesText))
                    {
                        foreach (var raw in linesText.Split('\n'))
                        {
                            if (raw.Length == 0)
                            {
                                continue;
                            }

                            int tab = raw.IndexOf('\t');
                            if (tab <= 0)
                            {
                                continue;
                            }

                            if (!long.TryParse(raw.Substring(0, tab), out long timeMs))
                            {
                                continue;
                            }

                            parsed.Add(new LyricLine { TimeMs = timeMs, Text = raw.Substring(tab + 1) });
                        }
                    }
                }

                // The track may have changed while the lookup was running. In that case the
                // result belongs to the old song and has to be thrown away.
                if (requestedKey != _lyricsKey)
                {
                    _lyricsFetchPending = true;
                    return;
                }

                _lyrics.Clear();
                _lyrics.AddRange(parsed);

                // When lrclib serves a different cut of the song ("(Clean)", "(Paused)"),
                // the label names it so a masked line is not a mystery.
                string versionSuffix = string.IsNullOrEmpty(version) ? "" : " " + version;
                LyricsStatus.Text = synced
                    ? "Синхронные слова" + versionSuffix + " · " + source
                    : "Слова песни" + versionSuffix + " · " + source;

                if (_lyrics.Count == 0)
                {
                    // No timed lyrics: show the plain text instead of an empty panel.
                    SetCurrentLine(plain);
                }
                else
                {
                    UpdateKaraoke();
                }
            }
            catch
            {
                LyricsStatus.Text = "Слова недоступны";
                _lyricsUnavailable = true;
            }
            finally
            {
                _lyricsLoading = false;
                if (_lyricsFetchPending)
                {
                    _lyricsFetchPending = false;
                    _ = FetchLyricsAsync();
                }
            }
        }

        /// <summary>
        /// Moves the karaoke highlight to the line that is playing right now and fills the part
        /// of it that has already been sung.
        /// </summary>
        private void UpdateKaraoke()
        {
            if (!_lyricsEnabled || _lyrics.Count == 0)
            {
                return;
            }

            double position = _positionMs;
            if (_isPlaying)
            {
                position += (DateTime.UtcNow - _positionStampUtc).TotalMilliseconds;
            }

            int index = -1;
            for (int i = 0; i < _lyrics.Count; i++)
            {
                if (_lyrics[i].TimeMs <= position)
                {
                    index = i;
                }
                else
                {
                    break;
                }
            }

            if (index < 0)
            {
                // The playhead is still before the first line. Showing it as the current line
                // keeps the panel readable right from the start instead of showing nothing.
                index = 0;
            }

            var current = _lyrics[index];

            SetLyricText(LyricPrev, index > 0 ? _lyrics[index - 1].Text : "");
            SetLyricText(LyricNext, index + 1 < _lyrics.Count ? _lyrics[index + 1].Text : "");
            _prevIndex = index > 0 ? index - 1 : -1;
            _curIndex = index;
            _nextIndex = index + 1 < _lyrics.Count ? index + 1 : -1;

            if (index != _lastLyricIndex)
            {
                _lastLyricIndex = index;
                AnimateLyricChange();
            }

            SetCurrentLineWords(current, position);
        }

        // ---------------------------------------------------------------- animations

        /// <summary>
        /// The karaoke lines change with the song, so the swap itself is the animation: the
        /// current line slides up from below, the neighbours follow with a lighter move.
        /// </summary>
        private void AnimateLyricChange()
        {
            SlideAndFade(LyricCur, fromOffset: 16, toOpacity: 1.0);
            SlideAndFade(LyricNext, fromOffset: 10, toOpacity: 0.45);
            SlideAndFade(LyricPrev, fromOffset: 10, toOpacity: 0.6);
        }

        private static void SlideAndFade(TextBlock block, double fromOffset, double toOpacity)
        {
            if (block == null)
            {
                return;
            }

            var transform = block.RenderTransform as Windows.UI.Xaml.Media.TranslateTransform;
            if (transform == null)
            {
                transform = new Windows.UI.Xaml.Media.TranslateTransform();
                block.RenderTransform = transform;
            }

            var easing = new Windows.UI.Xaml.Media.Animation.CubicEase { EasingMode = Windows.UI.Xaml.Media.Animation.EasingMode.EaseOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(320));

            var fade = new Windows.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = toOpacity,
                Duration = duration,
                EasingFunction = easing
            };
            var slide = new Windows.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = fromOffset,
                To = 0,
                Duration = duration,
                EasingFunction = easing
            };

            Storyboard.SetTarget(fade, block);
            Storyboard.SetTargetProperty(fade, "Opacity");
            Storyboard.SetTarget(slide, block);
            Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.Y)");

            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Children.Add(slide);
            storyboard.Begin();
        }

        private static void FadeIn(Windows.UI.Xaml.UIElement element, double fromOpacity, double toOpacity, uint milliseconds = 280)
        {
            if (element == null)
            {
                return;
            }

            element.Opacity = fromOpacity;
            var fade = new Windows.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = fromOpacity,
                To = toOpacity,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                EasingFunction = new Windows.UI.Xaml.Media.Animation.CubicEase { EasingMode = Windows.UI.Xaml.Media.Animation.EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Begin();
        }

        /// <summary>Writes the current line into the lyric text.</summary>
        private void SetCurrentLine(string text)
        {
            if (_karaokeText == text)
            {
                return;
            }

            _karaokeText = text;
            _curWordRuns.Clear();
            LyricCur.Text = text;
        }

        private readonly SolidColorBrush _wordSungBrush =
            new SolidColorBrush(Color.FromArgb(255, 249, 250, 251));
        private readonly SolidColorBrush _wordTodoBrush =
            new SolidColorBrush(Color.FromArgb(255, 107, 114, 128));
        private List<(Run Run, long StartMs)> _curWordRuns =
            new List<(Run, long)>();

        /// <summary>
        /// Word-by-word karaoke: the line is split into runs once, then every 100 ms tick
        /// only flips run colors. Lines without word timings fall back to plain text.
        /// </summary>
        private void SetCurrentLineWords(LyricLine line, double positionMs)
        {
            if (_karaokeText != line.Text)
            {
                _karaokeText = line.Text;
                LyricCur.Inlines.Clear();
                _curWordRuns.Clear();
                if (line.Words.Count == 0)
                {
                    LyricCur.Text = line.Text;
                }
                else
                {
                    foreach (var word in line.Words)
                    {
                        var run = new Run { Text = word.Text };
                        LyricCur.Inlines.Add(run);
                        _curWordRuns.Add((run, word.TimeMs));
                    }
                }
            }
            UpdateWordColors(positionMs);
        }

        private void UpdateWordColors(double positionMs)
        {
            if (_curWordRuns.Count == 0)
            {
                return;
            }

            foreach (var (run, startMs) in _curWordRuns)
            {
                var want = positionMs >= startMs ? _wordSungBrush : _wordTodoBrush;
                if (!ReferenceEquals(run.Foreground, want))
                {
                    run.Foreground = want;
                }
            }
        }

        private static void SetLyricText(TextBlock block, string text)
        {
            if (block.Text != text)
            {
                block.Text = text;
            }
        }

        // ---------------------------------------------------------------- jump to a line

        private async void LyricPrev_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await JumpToLineAsync(_prevIndex);
        }

        private async void LyricCur_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await JumpToLineAsync(_curIndex);
        }

        private async void LyricNext_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await JumpToLineAsync(_nextIndex);
        }

        /// <summary>
        /// Moves the track to the moment the clicked lyric line belongs to, so a line that is
        /// still ahead can be reached without scrubbing the progress bar.
        /// </summary>
        private async Task JumpToLineAsync(int index)
        {
            if (index < 0 || index >= _lyrics.Count || _durationSeconds <= 0)
            {
                return;
            }

            long positionMs = _lyrics[index].TimeMs;
            long durationMs = (long)Math.Round(_durationSeconds * 1000);
            if (positionMs >= durationMs)
            {
                return;
            }

            try
            {
                var uri = new Uri($"{BaseApi}/api/seek-position?positionMs={positionMs}&durationMs={durationMs}");
                using (var content = new StringContent(string.Empty))
                {
                    var response = await _seekHttp.PostAsync(uri, content);
                    if (response.IsSuccessStatusCode)
                    {
                        await FetchTrackAsync();
                    }
                }
            }
            catch
            {
            }
        }

        private void UpdateProgress(JsonObject json)
        {
            double durationSeconds = json.GetNamedNumber("durationMs", 0) / 1000.0;
            double positionSeconds = json.GetNamedNumber("positionMs", 0) / 1000.0;

            if (durationSeconds <= 0)
            {
                ProgressPanel.Visibility = Visibility.Collapsed;
                _durationSeconds = 0;
                return;
            }

            ProgressPanel.Visibility = Visibility.Visible;
            TrackProgress.Maximum = Math.Max(1, durationSeconds);
            _durationSeconds = durationSeconds;
            _positionMs = positionSeconds * 1000.0;
            _positionStampUtc = DateTime.UtcNow;

            if (!_isSeeking)
            {
                _positionSeconds = Math.Max(0, Math.Min(durationSeconds, positionSeconds));
                TrackProgress.Value = _positionSeconds;
            }

            PositionText.Text = FormatTime(_isSeeking ? _seekSeconds : _positionSeconds);
            DurationText.Text = FormatTime(durationSeconds);
            UpdateSeekThumb();
        }

        private void SeekVisual_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSeekThumb();
        }

        private void UpdateSeekThumb()
        {
            if (SeekThumb == null || TrackProgress == null || _durationSeconds <= 0)
            {
                return;
            }

            double width = TrackProgress.ActualWidth;
            double thumbWidth = SeekThumb.Width;
            if (width <= thumbWidth)
            {
                SeekThumb.Margin = new Thickness(0, 0, 0, 0);
                return;
            }

            double shown = _isSeeking ? _seekSeconds : _positionSeconds;
            double ratio = Math.Max(0, Math.Min(1, shown / _durationSeconds));
            SeekThumb.Margin = new Thickness(ratio * (width - thumbWidth), 0, 0, 0);
        }

        private void SeekVisual_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_durationSeconds <= 0)
            {
                SeekLog.Write("pointerPressed ignored: no duration");
                return;
            }

            _isSeeking = true;
            bool captured = SeekVisual.CapturePointer(e.Pointer);
            UpdateSeekFromPointer(e);
            SeekLog.Write($"pointerPressed id={e.Pointer.PointerId} inContact={e.Pointer.IsInContact} captured={captured} x={e.GetCurrentPoint(SeekVisual).Position.X:F1} width={SeekVisual.ActualWidth:F1} seek={_seekSeconds:F1}s");
            e.Handled = true;
        }

        private void SeekVisual_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isSeeking)
            {
                return;
            }

            UpdateSeekFromPointer(e);
            e.Handled = true;
        }

        private async void SeekVisual_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isSeeking)
            {
                SeekLog.Write("pointerReleased ignored: not seeking");
                return;
            }

            SeekVisual.ReleasePointerCapture(e.Pointer);
            SeekLog.Write($"pointerReleased seek={_seekSeconds:F1}s");
            await CompleteSeekAsync();
            e.Handled = true;
        }

        private void SeekVisual_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (!_isSeeking)
            {
                return;
            }

            _isSeeking = false;
            PositionText.Text = FormatTime(_positionSeconds);
            UpdateSeekThumb();
        }

        private async void SeekVisual_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            await CompleteSeekAsync();
        }

        /// <summary>Maps the pointer X inside the track onto a track position.</summary>
        private void UpdateSeekFromPointer(PointerRoutedEventArgs e)
        {
            double width = SeekVisual.ActualWidth;
            if (width <= 0 || _durationSeconds <= 0)
            {
                return;
            }

            var point = e.GetCurrentPoint(SeekVisual);
            double ratio = Math.Max(0, Math.Min(1, point.Position.X / width));
            _seekSeconds = ratio * _durationSeconds;

            TrackProgress.Value = _seekSeconds;
            PositionText.Text = FormatTime(_seekSeconds);
            UpdateSeekThumb();
        }

        private async Task CompleteSeekAsync()
        {
            if (!_isSeeking)
            {
                return;
            }

            _isSeeking = false;
            long positionMs = (long)Math.Round(_seekSeconds * 1000);
            long durationMs = (long)Math.Round(_durationSeconds * 1000);
            SeekLog.Write($"seek start positionMs={positionMs} durationMs={durationMs}");
            try
            {
                var uri = new Uri($"{BaseApi}/api/seek-position?positionMs={positionMs}&durationMs={durationMs}");
                using (var content = new StringContent(string.Empty))
                {
                    // _seekHttp has the 10s timeout: a seek can take far longer than the 2s
                    // of the polling client, and a timeout here would hide an applied seek.
                    var response = await _seekHttp.PostAsync(uri, content);
                    SeekLog.Write($"seek response status={(int)response.StatusCode}");
                    if (response.IsSuccessStatusCode)
                    {
                        await FetchTrackAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                SeekLog.Write($"seek failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Appends widget diagnostics next to the package install folder so seek input problems can
        /// be diagnosed from the PC instead of guessed at - Game Bar gives widgets no console.
        /// </summary>
        private static class SeekLog
        {
            private const string FileName = "widget.log";
            private static readonly object Gate = new object();

            public static void Write(string message)
            {
                try
                {
                    var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                    var file = folder.CreateFileAsync(FileName, Windows.Storage.CreationCollisionOption.OpenIfExists)
                        .AsTask().GetAwaiter().GetResult();

                    // Rotate like bridge.log does: an append-only diagnostics file would grow
                    // forever on a machine that never clears app data.
                    var props = file.GetBasicPropertiesAsync().AsTask().GetAwaiter().GetResult();
                    if (props.Size > 1_000_000)
                    {
                        file.DeleteAsync(Windows.Storage.StorageDeleteOption.PermanentDelete)
                            .AsTask().GetAwaiter().GetResult();
                        file = folder.CreateFileAsync(FileName, Windows.Storage.CreationCollisionOption.OpenIfExists)
                            .AsTask().GetAwaiter().GetResult();
                    }

                    lock (Gate)
                    {
                        // OpenStreamForWriteAsync leaves the stream at position 0 without
                        // truncating, so every line used to overwrite the previous one and the
                        // log ended up as overlapping fragments. Jump to the end instead.
                        var stream = file.OpenStreamForWriteAsync().GetAwaiter().GetResult();
                        stream.Seek(0, System.IO.SeekOrigin.End);
                        using (var writer = new System.IO.StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                        {
                            writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            {
                seconds = 0;
            }

            int total = (int)Math.Round(seconds);
            return $"{total / 60}:{total % 60:00}";
        }

        private async void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            await SendCommandAsync("play-pause");
        }

        private async void Prev_Click(object sender, RoutedEventArgs e)
        {
            await SendCommandAsync("prev");
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            await SendCommandAsync("next");
        }

        private async void Like_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool want = !_isLiked.GetValueOrDefault(false);
                var content = new StringContent(string.Empty);
                var response = await _http.PostAsync(
                    new Uri($"{BaseApi}/api/like?value={want.ToString().ToLowerInvariant()}"), content);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (JsonObject.TryParse(body, out JsonObject json) &&
                        json.TryGetValue("track", out var trackValue) &&
                        trackValue.ValueType == JsonValueType.Object)
                    {
                        ApplyTrackData(trackValue.GetObject());
                    }
                    else
                    {
                        await FetchTrackAsync();
                    }
                }
            }
            catch { }
        }

        private void UpdateLikeButton()
        {
            if (!_isLiked.HasValue)
            {
                LikeButton.Visibility = Visibility.Collapsed;
                return;
            }
            LikeButton.Visibility = Visibility.Visible;
            LikeGlyph.Foreground = _isLiked.Value
                ? new SolidColorBrush(Color.FromArgb(255, 229, 72, 77))
                : new SolidColorBrush(Color.FromArgb(255, 209, 213, 219));
        }

        private async void VolUp_Click(object sender, RoutedEventArgs e)
        {
            await SendCommandAsync("vol-up");
        }

        private async void VolDown_Click(object sender, RoutedEventArgs e)
        {
            await SendCommandAsync("vol-down");
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await FetchTrackAsync();
        }

        private async Task SendCommandAsync(string endpoint)
        {
            try
            {
                var content = new StringContent(string.Empty);
                var response = await _http.PostAsync(new Uri($"{BaseApi}/api/{endpoint}"), content);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (JsonObject.TryParse(body, out JsonObject json))
                    {
                        ApplyTrackData(json);
                    }
                }
            }
            catch { }
        }
    }
}

