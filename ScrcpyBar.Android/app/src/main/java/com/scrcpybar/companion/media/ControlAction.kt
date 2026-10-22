package com.scrcpybar.companion.media

import org.json.JSONObject

/**
 * Parsed `POST /control` payload, e.g. `{"action":"seek","positionMs":60000}`.
 */
data class ControlAction(
    val raw: String,
    val positionMs: Long?
) {
    val normalized: String
        get() = raw.trim().lowercase()

    companion object {
        fun fromJson(body: JSONObject): ControlAction {
            val action = body.optString("action").trim()
            return ControlAction(
                raw = action,
                positionMs = readPosition(body)
            )
        }

        private fun readPosition(body: JSONObject): Long? {
            val value = when {
                body.has("positionMs") -> body.opt("positionMs")
                body.has("position") -> body.opt("position")
                body.has("to") -> body.opt("to")
                else -> null
            }
            return when (value) {
                null, JSONObject.NULL -> null
                is Number -> value.toLong()
                is String -> value.toLongOrNull()
                else -> null
            }
        }
    }
}