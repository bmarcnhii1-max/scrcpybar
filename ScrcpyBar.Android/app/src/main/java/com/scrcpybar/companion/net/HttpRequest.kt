package com.scrcpybar.companion.net

import org.json.JSONObject
import java.nio.charset.StandardCharsets
import java.util.Locale

class HttpRequest(
    val method: String,
    val target: String,
    val version: String,
    val path: String,
    val query: Map<String, String>,
    val headers: Map<String, String>,
    val body: ByteArray,
    val remoteAddress: String
) {
    fun bodyText(): String = String(body, StandardCharsets.UTF_8)

    fun header(name: String): String? = headers[name.lowercase(Locale.US)]

    fun queryParam(name: String): String? = query[name]

    /** Token from `Authorization: Bearer <token>` or, for `<img>` style consumers, `?token=`. */
    fun accessToken(): String? {
        header("authorization")?.let { value ->
            if (value.regionMatches(0, "Bearer ", 0, 7, ignoreCase = true)) {
                return value.substring(7).trim().takeIf { it.isNotEmpty() }
            }
        }
        return queryParam("token")
    }
}

class HttpResponse private constructor(
    val status: Int,
    val contentType: String,
    val body: ByteArray,
    val headers: Map<String, String>
) {
    companion object {
        const val OK = 200
        const val NO_CONTENT = 204
        const val BAD_REQUEST = 400
        const val UNAUTHORIZED = 401
        const val NOT_FOUND = 404
        const val METHOD_NOT_ALLOWED = 405
        const val INTERNAL_ERROR = 500

        private val REASONS = mapOf(
            200 to "OK",
            204 to "No Content",
            400 to "Bad Request",
            401 to "Unauthorized",
            403 to "Forbidden",
            404 to "Not Found",
            405 to "Method Not Allowed",
            408 to "Request Timeout",
            409 to "Conflict",
            413 to "Payload Too Large",
            414 to "URI Too Long",
            415 to "Unsupported Media Type",
            431 to "Request Header Fields Too Large",
            500 to "Internal Server Error",
            501 to "Not Implemented",
            503 to "Service Unavailable"
        )

        fun reason(status: Int): String = REASONS[status] ?: "Unknown"

        fun json(
            status: Int,
            json: JSONObject,
            headers: Map<String, String> = emptyMap()
        ): HttpResponse = HttpResponse(
            status = status,
            contentType = "application/json; charset=utf-8",
            body = json.toString(2).toByteArray(StandardCharsets.UTF_8),
            headers = headers
        )

        fun text(status: Int, text: String): HttpResponse = HttpResponse(
            status = status,
            contentType = "text/plain; charset=utf-8",
            body = text.toByteArray(StandardCharsets.UTF_8),
            headers = emptyMap()
        )

        fun bytes(
            status: Int,
            contentType: String,
            body: ByteArray,
            headers: Map<String, String> = emptyMap()
        ): HttpResponse = HttpResponse(status, contentType, body, headers)

        fun noContent(): HttpResponse = HttpResponse(NO_CONTENT, "text/plain; charset=utf-8", ByteArray(0), emptyMap())
    }
}