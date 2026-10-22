# ScrcpyBar Companion (Android)

Android companion app for ScrcpyBar. It exposes the phone's media playback state and controls
over a small HTTP API so the desktop bridge can talk to the device directly over USB.

- **Kotlin**, Android Studio project, Gradle 8.9 / AGP 8.7.2 / Kotlin 2.0.21
- `minSdk 24`, `targetSdk 35`, package `com.scrcpybar.companion`
- **No root, no AccessibilityService, no UI automation.** The only elevated permission is
  notification listener access, which Android requires before any app may enumerate
  `MediaSession` objects.

---

## How it works

```
Desktop bridge  ──HTTP──▶  phone (ScrcpyBar Companion)
                             │
                             ├── ScrcpyMediaListenerService   (notification access)
                             │        └── MediaSessionManager.getActiveSessions()
                             │             └── MediaController → metadata + PlaybackState
                             └── MediaApiService (foreground)
                                      └── MiniHttpServer  →  /state, /control, …
```

| Component | File |
| --- | --- |
| Notification listener | `media/ScrcpyMediaListenerService.kt` |
| Session access + control | `media/MediaRepository.kt` |
| State model / JSON | `media/MediaSnapshot.kt` |
| HTTP/1.1 server (no deps) | `net/MiniHttpServer.kt` |
| Routing + auth | `net/ApiRouter.kt` |
| Foreground service host | `net/MediaApiService.kt` |

The HTTP server runs inside a **foreground service**. Without it Android terminates the process a
few minutes after the app is backgrounded and the bridge loses the phone.

## Setup

1. Open the project in Android Studio and run it on the phone.
2. Tap **Grant notification access** → enable *ScrcpyBar media listener* → back → **Refresh**.
3. Allow the ongoing notification when Android asks (Android 13+).
4. Connect the phone by USB with USB debugging enabled.
5. Forward the phone port to the PC:

```powershell
adb forward tcp:39282 tcp:38080
```

The phone server listens only on `127.0.0.1:38080`; the PC uses `http://127.0.0.1:39282`.
No Wi-Fi, LAN or internet connection is used. ScrcpyBar.Bridge runs the forward command automatically.

### Two ports, one endpoint

| Port | Where | Meaning |
| --- | --- | --- |
| `38080` | phone loopback | the port the HTTP server actually binds |
| `39282` | PC loopback | the `adb forward` port the desktop connects to |

The two must be kept distinct in any URL advertised to a client. `/state` and `/sessions` build
`artworkUrl` from the **forwarded** port (`Prefs.advertisedPort`, default `39282`) precisely because
`127.0.0.1:38080` means nothing on the PC. `prefs.port` is only what the phone binds, and
`/health.port` reports it so you can tell the two apart when debugging.

## Authentication

Authentication is off by default because the API is reachable only through the USB adb tunnel.
It can be enabled on the main screen; then all endpoints except `/health` require the token:

```
Authorization: Bearer <token>
```

A `?token=<token>` query parameter is also accepted so that `<img src=...>` works for artwork.

---

## HTTP API

Base URL on the PC: `http://127.0.0.1:39282`

### `GET /health`

Liveness probe and protocol version. No authentication.

```json
{
  "ok": true,
  "app": "scrcpybar-companion",
  "version": "1.0.0",
  "apiVersion": "1",
  "serverTimeMs": 1700000000000,
  "uptimeMs": 42130,
  "port": 38080,
  "authRequired": true,
  "notificationListenerConnected": true,
  "auth": "Authorization: Bearer <token> or ?token=<token>",
  "endpoints": ["GET /health", "GET /state", "POST /control", "GET /sessions", "GET /artwork"]
}
```

### `GET /state`

Current track. Optional `?packageName=` to target a specific session instead of the primary one.

```json
{
  "ok": true,
  "primary": "com.spotify.music",
  "session": {
    "packageName": "com.spotify.music",
    "appLabel": "Spotify",
    "title": "Track title",
    "artist": "Artist",
    "album": "Album",
    "durationMs": 215000,
    "positionMs": 42000,
    "bufferedPositionMs": 96000,
    "playbackSpeed": 1.0,
    "playbackState": 3,
    "playbackStateName": "playing",
    "isPlaying": true,
    "artworkUrl": "http://127.0.0.1:39282/artwork?packageName=com.spotify.music&token=…",
    "artworkUri": "content://media/external/audio/media/1234",
    "updatedAtEpochMs": 1700000000123,
    "actions": ["play", "pause", "playPause", "next", "previous", "stop", "seek"]
  },
  "serverTimeMs": 1700000000000
}
```

`playbackState` is the raw framework `PlaybackState` constant — prefer `playbackStateName`:
`0` none, `1` stopped, `2` paused, `3` playing, `4` fast-forwarding, `5` rewinding,
`6` buffering, `7` error, `8` connecting, `9–11` skipping.

Errors: `409` no active session, `503` notification access missing.

### `POST /control`

```jsonc
{ "action": "seek", "positionMs": 60000 }
```

| Action | Body |
| --- | --- |
| `play`, `pause`, `playPause`, `next`, `previous`, `stop` | `{"action":"next"}` |
| `seek` | `{"action":"seek","positionMs":60000}` — absolute, clamped to `[0, duration]` |

`seek` also accepts `position` or `to` as aliases. An optional `packageName` in the body (or
`?packageName=` in the query) targets a specific session.

```json
{
  "ok": true,
  "action": "seek",
  "positionMs": 60000,
  "session": { "…": "same shape as GET /state" },
  "serverTimeMs": 1700000000000
}
```

Errors: `400` unknown action or missing `positionMs`, `401` bad token, `409` no session,
`502` the app rejected the command. The error body lists `supportedActions` when relevant.

### `GET /sessions`

Every active media session, so the bridge can let the user pick a target app.

```json
{ "ok": true, "primary": "com.spotify.music", "count": 2, "sessions": [ { "…": "session objects" } ] }
```

### `GET /artwork`

`image/jpeg` bytes for the album art. Accepts `?packageName=` and `?token=`.

---

## Bridge examples

### curl

```bash
adb forward tcp:39282 tcp:38080
BASE=http://127.0.0.1:39282

curl -s "$BASE/health"
curl -s "$BASE/state"
curl -s "$BASE/sessions"
curl -s -X POST "$BASE/control" -H "Content-Type: application/json" \
     -d '{"action":"seek","positionMs":60000}'
curl -s -o cover.jpg "$BASE/artwork"
```

### C# (.NET)

```csharp
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:39282") };

var json = await client.GetStringAsync("/state");
using var doc = JsonDocument.Parse(json);
var session = doc.RootElement.GetProperty("session");
Console.WriteLine($"{session.GetProperty("title")} — {session.GetProperty("artist")}");

var body = new StringContent("""{"action":"seek","positionMs":60000}""", Encoding.UTF8, "application/json");
var response = await client.PostAsync("/control", body);
Console.WriteLine(response.StatusCode);
```

### Interpolation

`positionMs` is absolute, so smooth progress bars extrapolate locally:

```csharp
double position = session.positionMs + (isPlaying ? now - session.updatedAtEpochMs : 0);
```

## Artwork

The public Android SDK hides `METADATA_KEY_ALBUM_ID` and `METADATA_KEY_ART_BITMAP_ID`, so
artwork is resolved from the URI keys apps actually publish (`METADATA_KEY_MEDIA_URI`,
`METADATA_KEY_ART_URI`, `METADATA_KEY_ALBUM_ART_URI`, `METADATA_KEY_DISPLAY_ICON_URI`) and falls
back to the in-memory bitmap when the session lives in this process. Some apps publish no
artwork at all; `/artwork` then returns `409`.

## Build and test

```bash
./gradlew assembleDebug      # APK -> app/build/outputs/apk/debug/
./gradlew testDebugUnitTest  # 21 JVM tests, no device required
./gradlew lintDebug
```

`MiniHttpServerTest` covers keep-alive, chunked and `Content-Length` bodies,
`Expect: 100-continue`, `HEAD`, binary responses, error handling and `HttpURLConnection`
round-trips.