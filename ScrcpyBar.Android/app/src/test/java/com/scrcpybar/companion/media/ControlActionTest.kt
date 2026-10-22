package com.scrcpybar.companion.media

import android.media.session.PlaybackState
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ControlActionTest {

    @Test
    fun `parses the documented seek payload`() {
        val action = ControlAction.fromJson(JSONObject("""{"action":"seek","positionMs":60000}"""))
        assertEquals("seek", action.raw)
        assertEquals(60_000L, action.positionMs)
        assertEquals("seek", action.normalized)
    }

    @Test
    fun `action name is trimmed and lower cased`() {
        assertEquals("next", ControlAction("  NEXT  ", null).normalized)
    }

    @Test
    fun `position aliases are accepted`() {
        assertEquals(1_000L, ControlAction.fromJson(JSONObject("""{"action":"seek","position":1000}""")).positionMs)
        assertEquals(2_000L, ControlAction.fromJson(JSONObject("""{"action":"seek","to":2000}""")).positionMs)
        assertEquals(3_000L, ControlAction.fromJson(JSONObject("""{"action":"seek","positionMs":"3000"}""")).positionMs)
    }

    @Test
    fun `position is null when absent or unparsable`() {
        assertNull(ControlAction.fromJson(JSONObject("""{"action":"play"}""")).positionMs)
        assertNull(ControlAction.fromJson(JSONObject("""{"action":"seek","positionMs":"abc"}""")).positionMs)
        assertNull(ControlAction.fromJson(JSONObject("""{"action":"seek","positionMs":null}""")).positionMs)
    }
}

class MediaSnapshotTest {

    private val snapshot = MediaSnapshot(
        packageName = "com.example.player",
        appLabel = "Example Player",
        title = "Track",
        artist = "Artist",
        album = "Album",
        durationMs = 180_000,
        positionMs = 60_000,
        bufferedPositionMs = 90_000,
        playbackSpeed = 1f,
        playbackState = PlaybackState.STATE_PLAYING,
        isPlaying = true,
        artworkUri = "content://media/external/audio/media/7",
        updatedAtEpochMs = 1_700_000_000_000
    )

    @Test
    fun `json exposes every field the bridge needs`() {
        val json = snapshot.toJson("http://10.0.0.2:38080/artwork?packageName=com.example.player")

        assertEquals("com.example.player", json.getString("packageName"))
        assertEquals("Track", json.getString("title"))
        assertEquals("Artist", json.getString("artist"))
        assertEquals("Album", json.getString("album"))
        assertEquals(180_000, json.getLong("durationMs"))
        assertEquals(60_000, json.getLong("positionMs"))
        assertTrue(json.getBoolean("isPlaying"))
        assertEquals("playing", json.getString("playbackStateName"))
        assertEquals("http://10.0.0.2:38080/artwork?packageName=com.example.player", json.getString("artworkUrl"))
        assertEquals("content://media/external/audio/media/7", json.getString("artworkUri"))
    }

    @Test
    fun `null metadata is serialised as json null`() {
        val empty = snapshot.copy(title = null, artist = null, album = null, artworkUri = null)
        val json = empty.toJson(null)
        assertTrue(json.isNull("title"))
        assertTrue(json.isNull("artist"))
        assertTrue(json.isNull("album"))
        assertTrue(json.isNull("artworkUrl"))
    }

    @Test
    fun `state names cover the framework states`() {
        assertEquals("paused", snapshot.copy(playbackState = PlaybackState.STATE_PAUSED).stateName)
        assertEquals("buffering", snapshot.copy(playbackState = PlaybackState.STATE_BUFFERING).stateName)
        assertEquals("stopped", snapshot.copy(playbackState = PlaybackState.STATE_STOPPED).stateName)
        assertEquals("none", snapshot.copy(playbackState = PlaybackState.STATE_NONE).stateName)
        assertEquals("error", snapshot.copy(playbackState = PlaybackState.STATE_ERROR).stateName)
        assertEquals("fastForwarding", snapshot.copy(playbackState = PlaybackState.STATE_FAST_FORWARDING).stateName)
        assertEquals("rewinding", snapshot.copy(playbackState = PlaybackState.STATE_REWINDING).stateName)
        assertEquals("connecting", snapshot.copy(playbackState = PlaybackState.STATE_CONNECTING).stateName)
        assertEquals("skippingToNext", snapshot.copy(playbackState = PlaybackState.STATE_SKIPPING_TO_NEXT).stateName)
        assertEquals("unknown", snapshot.copy(playbackState = 99).stateName)
    }

    @Test
    fun `capabilities list only the documented actions`() {
        assertTrue(snapshot.supports("seek"))
        assertTrue(snapshot.supports("playPause"))
        assertFalse(snapshot.supports("fastForward"))
        assertEquals(MediaSnapshot.SUPPORTED_ACTIONS, snapshot.capabilities())
    }
}