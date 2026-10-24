package com.scrcpybar.companion.net

import android.content.Context
import android.util.Log
import com.scrcpybar.companion.BuildConfig
import com.scrcpybar.companion.core.NetworkUtils
import com.scrcpybar.companion.core.Prefs
import com.scrcpybar.companion.media.ControlAction
import com.scrcpybar.companion.media.ControlResult
import com.scrcpybar.companion.media.HttpErrors
import com.scrcpybar.companion.media.MediaRepository
import com.scrcpybar.companion.media.MediaSnapshot
import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject
import java.io.ByteArrayOutputStream

/**
 * JSON API exposed by the companion app.
 *
 * ```
 * GET  /health    liveness + protocol version, no authentication
 * GET  /state     current track of the primary (or requested) session
 * GET  /sessions  every active media session
 * GET  /artwork   album art bitmap for a session
 * POST /control   playback command, e.g. {"action":"seek","positionMs":60000}
 * ```
 */
class ApiRouter(
    private val context: Context,
    private val repository: MediaRepository,
    private val prefs: Prefs,
    private val serverState: () -> ServerStatus
) {

    data class ServerStatus(val running: Boolean, val port: Int, val uptimeMs: Long, val error: String?)

    fun dispatch(request: HttpRequest): HttpResponse {
        Log.d(TAG, "${request.method} ${request.target} from ${request.remoteAddress}")

        if (request.method == "OPTIONS") return HttpResponse.noContent()

        val path = request.path.ifEmpty { "/" }
        return when {
            path == "/" || path == "/index" -> HttpResponse.text(200, indexHtml())
            path == "/health" -> health()
            path == "/state" -> state(request)
            path == "/sessions" -> sessions(request)
            path == "/artwork" -> artwork(request)
            path == "/control" -> control(request)
            path == "/facts" -> facts(request)
            else -> error(404, "unknown endpoint '$path'")
        }
    }

    // ---------------------------------------------------------------- endpoints

    private fun health(): HttpResponse {
        val status = serverState()
        return HttpResponse.json(
            HttpResponse.OK,
            JSONObject().apply {
                put("ok", true)
                put("app", "scrcpybar-companion")
                put("version", BuildConfig.VERSION_NAME)
                put("apiVersion", BuildConfig.API_VERSION)
                put("serverTimeMs", System.currentTimeMillis())
                put("uptimeMs", status.uptimeMs)
                put("port", status.port)
                put("authRequired", prefs.authEnabled)
                put("notificationListenerConnected", repository.isListenerConnected)
                put("auth", "Authorization: Bearer <token> or ?token=<token>")
                put("endpoints", JSONArray(listOf("GET /health", "GET /state", "POST /control", "GET /sessions", "GET /artwork")))
            }
        )
    }

    private fun state(request: HttpRequest): HttpResponse {
        if (!authorized(request)) return unauthorized()

        val accessError = repository.accessError
        val snapshot = repository.snapshot(request.queryParam("packageName"))
        if (snapshot == null) {
            val status = if (accessError != null) HttpErrors.ACCESS_DENIED else HttpErrors.NO_SESSION
            return error(status, accessError ?: "no active media session; start playback in any app")
        }

        val payload = JSONObject().apply {
            put("ok", true)
            put("primary", repository.primaryPackage)
            put("session", snapshot.toJson(artworkUrlFor(snapshot)))
            put("serverTimeMs", System.currentTimeMillis())
        }
        return HttpResponse.json(HttpResponse.OK, payload, NO_CACHE)
    }

    private fun facts(request: HttpRequest): HttpResponse {
        if (!authorized(request)) return unauthorized()

        val facts = repository.sessionFacts(request.queryParam("packageName"))
            ?: return error(HttpErrors.NO_SESSION, "no active media session")
        return HttpResponse.json(HttpResponse.OK, JSONObject().apply {
            put("ok", true)
            put("facts", facts)
            put("serverTimeMs", System.currentTimeMillis())
        }, NO_CACHE)
    }

    private fun sessions(request: HttpRequest): HttpResponse {
        if (!authorized(request)) return unauthorized()

        val accessError = repository.accessError
        val list = repository.sessions()
        val payload = JSONObject().apply {
            put("ok", true)
            put("primary", repository.primaryPackage)
            put("count", list.size)
            put("sessions", JSONArray().apply {
                for (snapshot in list) put(snapshot.toJson(artworkUrlFor(snapshot)))
            })
            put("serverTimeMs", System.currentTimeMillis())
        }
        return HttpResponse.json(
            if (list.isEmpty() && accessError != null) HttpErrors.ACCESS_DENIED else HttpResponse.OK,
            payload,
            NO_CACHE
        )
    }

    private fun artwork(request: HttpRequest): HttpResponse {
        if (!authorized(request)) return unauthorized()

        val bitmap = repository.artwork(request.queryParam("packageName"))
            ?: return error(HttpErrors.NO_SESSION, "no artwork available for this session")

        val bytes = ByteArrayOutputStream()
        val compressed = runCatching {
            bitmap.compress(android.graphics.Bitmap.CompressFormat.JPEG, 88, bytes)
        }.getOrDefault(false)
        if (!compressed) return error(HttpErrors.MEDIA_ERROR, "failed to encode artwork")

        return HttpResponse.bytes(
            status = 200,
            contentType = "image/jpeg",
            body = bytes.toByteArray(),
            headers = NO_CACHE
        )
    }

    private fun control(request: HttpRequest): HttpResponse {
        if (!authorized(request)) return unauthorized()
        if (request.method != "POST") return error(405, "use POST for /control")

        val body = parseJson(request.body) ?: return error(400, "body must be valid JSON")
        val action = ControlAction.fromJson(body)
        if (action.raw.isEmpty()) return error(400, "missing 'action'")

        val requestedPackage = body.optString("packageName").takeIf { it.isNotBlank() }
            ?: request.queryParam("packageName")

        return when (val result = repository.control(action, requestedPackage)) {
            is ControlResult.Success -> HttpResponse.json(
                HttpResponse.OK,
                JSONObject().apply {
                    put("ok", true)
                    put("action", action.normalized)
                    result.appliedPositionMs?.let { put("positionMs", it) }
                    result.snapshot?.let { put("session", it.toJson(null)) }
                    put("serverTimeMs", System.currentTimeMillis())
                },
                NO_CACHE
            )

            is ControlResult.Failure -> error(result.httpStatus, result.message, result.supported)
        }
    }

    // ---------------------------------------------------------------- helpers

    private fun authorized(request: HttpRequest): Boolean {
        if (!prefs.authEnabled) return true
        val expected = prefs.token
        val provided = request.accessToken()
        return provided != null && constantTimeEquals(expected, provided)
    }

    private fun unauthorized(): HttpResponse = HttpResponse.json(
        HttpResponse.UNAUTHORIZED,
        JSONObject().apply {
            put("ok", false)
            put("error", "missing or invalid bearer token")
            put("status", HttpResponse.UNAUTHORIZED)
        },
        mapOf("WWW-Authenticate" to "Bearer realm=\"ScrcpyBar companion\"")
    )

    private fun parseJson(body: ByteArray): JSONObject? = try {
        if (body.isEmpty()) JSONObject() else JSONObject(String(body, Charsets.UTF_8))
    } catch (e: JSONException) {
        null
    }

    /**
     * Advertised unconditionally: whether artwork can be served depends on what the publishing app
     * exposes, and `/artwork` answers `409` when it cannot. Advertising it only when a metadata URI
     * happened to be present would hide working endpoints from the bridge.
 */
    private fun artworkUrlFor(snapshot: MediaSnapshot): String? {
        val base = NetworkUtils.baseUrl(context, prefs.advertisedPort) ?: return null
        // The token deliberately stays out of the URL: query strings leak into logs and
        // intermediaries. Authenticated consumers send the Bearer header themselves.
        return "$base/artwork?packageName=${snapshot.packageName}"
    }

    private fun error(status: Int, message: String, supported: List<String> = emptyList()): HttpResponse =
        HttpResponse.json(
            status,
            JSONObject().apply {
                put("ok", false)
                put("error", message)
                put("status", status)
                if (supported.isNotEmpty()) put("supportedActions", JSONArray(supported))
            },
            NO_CACHE
        )

    private fun constantTimeEquals(a: String, b: String): Boolean {
        if (a.length != b.length) return false
        var diff = 0
        for (i in a.indices) diff = diff or (a[i].code xor b[i].code)
        return diff == 0
    }

    private fun indexHtml(): String =
        "ScrcpyBar companion " + BuildConfig.VERSION_NAME + " - API v" + BuildConfig.API_VERSION +
            "\nSee GET /health for the endpoint list.\n"

    companion object {
        private const val TAG = "ScrcpyBarApi"
        private val NO_CACHE = mapOf("Cache-Control" to "no-store")
    }
}