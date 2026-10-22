package com.scrcpybar.companion.media

import android.media.session.PlaybackState
import org.json.JSONArray
import org.json.JSONObject

/**
 * Immutable view of one [android.media.session.MediaController] at a point in time.
 */
data class MediaSnapshot(
    val packageName: String,
    val appLabel: String,
    val title: String?,
    val artist: String?,
    val album: String?,
    val durationMs: Long,
    val positionMs: Long,
    val bufferedPositionMs: Long,
    val playbackSpeed: Float,
    val playbackState: Int,
    val isPlaying: Boolean,
    val artworkUri: String?,
    val updatedAtEpochMs: Long
) {

    val stateName: String
        get() = when (playbackState) {
            PlaybackState.STATE_NONE -> "none"
            PlaybackState.STATE_PLAYING -> "playing"
            PlaybackState.STATE_PAUSED -> "paused"
            PlaybackState.STATE_BUFFERING -> "buffering"
            PlaybackState.STATE_STOPPED -> "stopped"
            PlaybackState.STATE_ERROR -> "error"
            PlaybackState.STATE_FAST_FORWARDING -> "fastForwarding"
            PlaybackState.STATE_REWINDING -> "rewinding"
            PlaybackState.STATE_CONNECTING -> "connecting"
            PlaybackState.STATE_SKIPPING_TO_PREVIOUS -> "skippingToPrevious"
            PlaybackState.STATE_SKIPPING_TO_NEXT -> "skippingToNext"
            PlaybackState.STATE_SKIPPING_TO_QUEUE_ITEM -> "skippingToQueueItem"
            else -> "unknown"
        }

    fun supports(action: String): Boolean = action in SUPPORTED_ACTIONS

    fun capabilities(): List<String> = SUPPORTED_ACTIONS.filter { supports(it) }

    /**
     * @param artworkUrl absolute URL of [MediaSnapshot.artworkUri] on the companion server,
     *   already carrying the access token so that plain `<img>` consumers work.
     */
    fun toJson(artworkUrl: String?): JSONObject = JSONObject().apply {
        put("packageName", packageName)
        put("appLabel", appLabel)
        put("title", title ?: JSONObject.NULL)
        put("artist", artist ?: JSONObject.NULL)
        put("album", album ?: JSONObject.NULL)
        put("durationMs", durationMs)
        put("positionMs", positionMs)
        put("bufferedPositionMs", bufferedPositionMs)
        put("playbackSpeed", playbackSpeed.toDouble())
        put("playbackState", playbackState)
        put("playbackStateName", stateName)
        put("isPlaying", isPlaying)
        put("artworkUrl", artworkUrl ?: JSONObject.NULL)
        put("artworkUri", artworkUri ?: JSONObject.NULL)
        put("updatedAtEpochMs", updatedAtEpochMs)
        put("actions", JSONArray(capabilities()))
    }

    companion object {
        val SUPPORTED_ACTIONS = listOf(
            "play",
            "pause",
            "playPause",
            "next",
            "previous",
            "stop",
            "seek"
        )
    }
}