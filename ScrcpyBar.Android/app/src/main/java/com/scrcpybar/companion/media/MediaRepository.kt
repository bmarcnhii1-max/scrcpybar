package com.scrcpybar.companion.media

import android.annotation.SuppressLint
import android.content.ComponentName
import android.content.ContentResolver
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.LruCache
import android.util.Size
import java.io.InputStream
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Owns the [MediaSessionManager] / [MediaController] interaction for the whole process.
 *
 * Querying active sessions is only allowed for packages that hold notification listener
 * access, therefore the repository prefers the [ScrcpyMediaListenerService] context once it is
 * connected and falls back to the application context otherwise.
 */
class MediaRepository private constructor(private val appContext: Context) {

    fun interface Observer {
        fun onMediaChanged(repository: MediaRepository)
    }

    private val lock = Any()
    private val mainHandler = Handler(Looper.getMainLooper())
    private val refreshing = AtomicBoolean(false)
    private val observers = CopyOnWriteArrayList<Observer>()

    private var manager: MediaSessionManager? = null
    private var sessionsListener: MediaSessionManager.OnActiveSessionsChangedListener? = null

    private val controllers = LinkedHashMap<String, MediaController>()
    private val callbacks = HashMap<String, MediaController.Callback>()
    private val notificationHints = HashMap<String, NotificationHint>()

    private var preferredPackage: String? = null
    private var lastAccessError: String? = null
    private var lastRefreshElapsedRealtime = 0L

    private val artworkCache = object : LruCache<String, Bitmap>(4 * 1024 * 1024) {
        override fun sizeOf(key: String, value: Bitmap): Int = value.byteCount
    }

    val isListenerConnected: Boolean
        get() = synchronized(lock) { sessionsListener != null }

    val accessError: String?
        get() = synchronized(lock) { lastAccessError }

    val primaryPackage: String?
        get() = synchronized(lock) { preferredPackage }

    // ---------------------------------------------------------------- lifecycle

    fun onListenerConnected() {
        attachSessionsListener()
        refresh(force = true)
    }

    fun onListenerDisconnected() {
        synchronized(lock) {
            detachSessionsListenerLocked()
            clearControllersLocked()
            preferredPackage = null
        }
        notifyObservers()
    }

    /**
     * Text extracted from media-style notifications. Used as a fallback when an app publishes a
     * session without title/artist metadata.
     */
    fun updateNotificationHints(hints: Map<String, NotificationHint>) {
        synchronized(lock) { notificationHints.clear(); notificationHints.putAll(hints) }
        notifyObservers()
    }

    fun addObserver(observer: Observer) {
        observers.addIfAbsent(observer)
    }

    fun removeObserver(observer: Observer) {
        observers.remove(observer)
    }

    // ---------------------------------------------------------------- queries

    fun sessions(): List<MediaSnapshot> {
        refresh()
        val snapshotList = synchronized(lock) { controllers.values.toList() }
        return snapshotList.mapNotNull { snapshotOf(it) }
    }

    fun snapshot(packageName: String? = null): MediaSnapshot? {
        refresh()
        val controller = synchronized(lock) { selectControllerLocked(packageName) } ?: return null
        return snapshotOf(controller)
    }

    fun artwork(packageName: String? = null): Bitmap? {
        refresh()
        val controller = synchronized(lock) { selectControllerLocked(packageName) } ?: return null
        val snapshot = snapshotOf(controller) ?: return null
        return loadArtwork(snapshot, controller.metadata)
    }

    // ---------------------------------------------------------------- control

    fun control(action: ControlAction, packageName: String? = null): ControlResult {
        refresh()
        val controller = synchronized(lock) { selectControllerLocked(packageName) }
            ?: return ControlResult.Failure(
                HttpErrors.NO_SESSION,
                "no active media session is available for control"
            )

        val normalized = action.normalized
        if (normalized !in MediaSnapshot.SUPPORTED_ACTIONS) {
            return ControlResult.Failure(
                HttpErrors.BAD_REQUEST,
                "unknown action '${action.raw}'",
                MediaSnapshot.SUPPORTED_ACTIONS
            )
        }

        val controls = controller.transportControls
        var appliedPosition: Long? = null

        try {
            when (normalized) {
                "play" -> controls.play()
                "pause" -> controls.pause()
                "playPause" -> if (isCurrentlyPlaying(controller)) controls.pause() else controls.play()
                "next" -> controls.skipToNext()
                "previous" -> controls.skipToPrevious()
                "stop" -> controls.stop()
                "seek" -> {
                    val requested = action.positionMs
                        ?: return ControlResult.Failure(
                            HttpErrors.BAD_REQUEST,
                            "action 'seek' requires 'positionMs'"
                        )
                    val duration = snapshotOf(controller)?.durationMs ?: 0L
                    val target = if (duration > 0) requested.coerceIn(0L, duration) else requested.coerceAtLeast(0L)
                    controls.seekTo(target)
                    appliedPosition = target
                }
            }
        } catch (t: Throwable) {
            return ControlResult.Failure(HttpErrors.MEDIA_ERROR, "media command failed: ${t.messageOrType()}")
        }

        return ControlResult.Success(snapshotOf(controller), appliedPosition)
    }

    // ---------------------------------------------------------------- internals

    private fun isCurrentlyPlaying(controller: MediaController): Boolean = when (controller.playbackState?.state) {
        PlaybackState.STATE_PLAYING,
        PlaybackState.STATE_FAST_FORWARDING,
        PlaybackState.STATE_REWINDING -> true
        else -> false
    }

    @SuppressLint("ObsoleteSdkInt")
    private fun snapshotOf(controller: MediaController): MediaSnapshot? {
        val packageName = controller.packageName ?: return null
        val metadata = controller.metadata
        val state = controller.playbackState

        val hint = synchronized(lock) { notificationHints[packageName] }
        val rawTitle = metadata?.takeIf { it.containsKey(MediaMetadata.METADATA_KEY_TITLE) }
            ?.getString(MediaMetadata.METADATA_KEY_TITLE)
            ?: metadata?.takeIf { it.containsKey(MediaMetadata.METADATA_KEY_DISPLAY_TITLE) }
                ?.getString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE)
        val rawArtist = metadata?.getString(MediaMetadata.METADATA_KEY_ARTIST)

        val title = rawTitle?.takeIf { it.isNotBlank() } ?: hint?.title
        val artist = rawArtist?.takeIf { it.isNotBlank() } ?: hint?.artist
        val album = metadata?.getString(MediaMetadata.METADATA_KEY_ALBUM)
        val duration = metadata?.getLong(MediaMetadata.METADATA_KEY_DURATION)?.coerceAtLeast(0L) ?: 0L

        val playbackState = state?.state ?: PlaybackState.STATE_NONE
        val position = state?.let(::positionOf) ?: 0L
        val buffered = state?.bufferedPosition?.coerceAtLeast(0L) ?: 0L
        val speed = state?.playbackSpeed ?: 0f

        return MediaSnapshot(
            packageName = packageName,
            appLabel = labelOf(packageName),
            title = title,
            artist = artist,
            album = album,
            durationMs = duration,
            positionMs = if (duration > 0 && position > duration) duration else position,
            bufferedPositionMs = buffered,
            playbackSpeed = speed,
            playbackState = playbackState,
            isPlaying = playbackState == PlaybackState.STATE_PLAYING,
            artworkUri = artworkUriOf(metadata),
            updatedAtEpochMs = System.currentTimeMillis()
        )
    }

    /**
     * [android.media.session.PlaybackState] has no `getCurrentPosition()`, so the anchor position
     * is extrapolated with `getLastPositionUpdateTime()`, which only exists from API 26.
     */
    @SuppressLint("ObsoleteSdkInt")
    private fun positionOf(state: PlaybackState): Long {
        val base = state.position
        if (base < 0L) return 0L

        val playing = when (state.state) {
            PlaybackState.STATE_PLAYING,
            PlaybackState.STATE_FAST_FORWARDING,
            PlaybackState.STATE_REWINDING -> true
            else -> false
        }
        if (!playing) return base
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return base

        val elapsed = SystemClock.elapsedRealtime() - state.lastPositionUpdateTime
        if (elapsed < 0L) return base
        return base + elapsed
    }

    /**
     * Resolves artwork in the only way the public SDK allows.
 *
 * `METADATA_KEY_ART_BITMAP_ID` / `METADATA_KEY_ALBUM_ID` are hidden, so the URI based keys are
 * tried in order and, as a last resort, the in-memory bitmap (available while the session lives in
 * this process) is used directly.
 */
    @SuppressLint("InlinedApi")
    private fun artworkUriOf(metadata: MediaMetadata?): String? {
        if (metadata == null) return null
        for (key in ARTWORK_URI_KEYS) {
            metadata.getString(key)?.takeIf { it.isNotBlank() }?.let { return it }
        }
        return null
    }

    private fun inlineArtworkOf(metadata: MediaMetadata?): Bitmap? {
        if (metadata == null) return null
        for (key in ARTWORK_BITMAP_KEYS) {
            metadata.getBitmap(key)?.let { return it }
        }
        return null
    }

    private fun loadArtwork(snapshot: MediaSnapshot, metadata: MediaMetadata?): Bitmap? {
        val uriText = snapshot.artworkUri
        if (uriText != null) {
            artworkCache.get(uriText)?.let { return it }
            val decoded = runCatching { decodeBitmap(uriText) }.getOrNull()
            if (decoded != null) {
                artworkCache.put(uriText, decoded)
                return decoded
            }
        }
        return inlineArtworkOf(metadata)
    }

    private fun decodeBitmap(uriText: String): Bitmap? {
        val resolver = appContext.contentResolver
        val uri = Uri.parse(uriText)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            val thumbnail = runCatching {
                resolver.loadThumbnail(uri, Size(512, 512), null)
            }.getOrNull()
            if (thumbnail != null) return thumbnail
        }

        openStream(resolver, uri)?.use { stream ->
            return BitmapFactory.decodeStream(stream)
        }
        return null
    }

    @Suppress("DEPRECATION")
    private fun openStream(resolver: ContentResolver, uri: Uri): InputStream? =
        runCatching { resolver.openInputStream(uri) }.getOrNull()

    private fun labelOf(packageName: String): String = runCatching {
        val info = appContext.packageManager.getApplicationInfo(packageName, 0)
        appContext.packageManager.getApplicationLabel(info).toString()
    }.getOrDefault(packageName)

    // ---------------------------------------------------------------- session bookkeeping

    private fun attachSessionsListener() {
        val mediaSessionManager =
            appContext.getSystemService(Context.MEDIA_SESSION_SERVICE) as? MediaSessionManager
        if (mediaSessionManager == null) {
            synchronized(lock) { lastAccessError = "MediaSessionManager is unavailable" }
            return
        }
        synchronized(lock) { manager = mediaSessionManager }

        val component = listenerComponent()
        val listener = MediaSessionManager.OnActiveSessionsChangedListener { refresh(force = true) }
        try {
            mediaSessionManager.addOnActiveSessionsChangedListener(listener, component, mainHandler)
            synchronized(lock) { sessionsListener = listener }
        } catch (t: Throwable) {
            synchronized(lock) {
                lastAccessError = "cannot register session listener: ${t.messageOrType()}"
            }
        }
    }

    private fun detachSessionsListenerLocked() {
        val activeManager = manager
        val listener = sessionsListener ?: return
        runCatching { activeManager?.removeOnActiveSessionsChangedListener(listener) }
        sessionsListener = null
        manager = null
    }

    private fun listenerComponent(): ComponentName =
        ComponentName(appContext, ScrcpyMediaListenerService::class.java)

    private fun refresh(force: Boolean = false) {
        val now = SystemClock.elapsedRealtime()
        synchronized(lock) {
            if (!force && now - lastRefreshElapsedRealtime < MIN_REFRESH_INTERVAL_MS) return
            if (!refreshing.compareAndSet(false, true)) return
            lastRefreshElapsedRealtime = now
        }

        try {
            val mediaSessionManager = synchronized(lock) { manager }
                ?: (appContext.getSystemService(Context.MEDIA_SESSION_SERVICE) as? MediaSessionManager)
                ?: return
            synchronized(lock) { manager = mediaSessionManager }

            val activeControllers: List<MediaController> = try {
                mediaSessionManager.getActiveSessions(listenerComponent()) ?: emptyList()
            } catch (t: Throwable) {
                synchronized(lock) { lastAccessError = "cannot read active sessions: ${t.messageOrType()}" }
                emptyList()
            }

            synchronized(lock) { lastAccessError = null }
            applySessions(activeControllers)
            notifyObservers()
        } finally {
            refreshing.set(false)
        }
    }

    /**
     * [MediaSessionManager.getActiveSessions] already hands out [MediaController] instances, so
     * the returned controllers are reused directly instead of opening a second session connection.
     */
    private fun applySessions(activeControllers: List<MediaController>) {
        val liveTokens = HashSet<Any>()
        for (controller in activeControllers) liveTokens += controller.sessionToken

        synchronized(lock) {
            val removals = controllers
                .filterValues { it.sessionToken !in liveTokens }
                .keys
                .toList()

            val additions = activeControllers.filter { controller ->
                val packageName = controller.packageName
                packageName != null &&
                    packageName != appContext.packageName &&
                    controllers[packageName]?.sessionToken != controller.sessionToken
            }

            for (packageName in removals) {
                callbacks.remove(packageName)?.let { callback ->
                    controllers[packageName]?.let { controller ->
                        runCatching { controller.unregisterCallback(callback) }
                    }
                }
                controllers.remove(packageName)
                artworkCache.evictAll()
            }

            for (controller in additions) {
                val packageName = controller.packageName ?: continue
                callbacks.remove(packageName)?.let { callback ->
                    controllers[packageName]?.let { runCatching { it.unregisterCallback(callback) } }
                }
                controllers[packageName] = controller
                val callback = object : MediaController.Callback() {
                    override fun onMetadataChanged(metadata: MediaMetadata?) {
                        notifyObservers()
                    }

                    override fun onPlaybackStateChanged(state: PlaybackState?) {
                        notifyObservers()
                    }

                    override fun onSessionDestroyed() {
                        mainHandler.post { refresh(force = true) }
                    }
                }
                runCatching { controller.registerCallback(callback, mainHandler) }
                callbacks[packageName] = callback
            }

            preferredPackage = choosePreferredLocked(controllers.keys.toList())
        }
    }

    private fun choosePreferredLocked(activePackages: List<String>): String? {
        if (preferredPackage != null && preferredPackage in controllers) return preferredPackage
        if (activePackages.isEmpty()) return null

        val playing = activePackages.filter { packageName ->
            val state = controllers[packageName]?.playbackState?.state
            state == PlaybackState.STATE_PLAYING ||
                state == PlaybackState.STATE_FAST_FORWARDING ||
                state == PlaybackState.STATE_REWINDING
        }
        if (playing.size == 1) return playing.first()

        val withTrack = activePackages.filter { packageName ->
            val metadata = controllers[packageName]?.metadata
            metadata != null && metadata.getString(MediaMetadata.METADATA_KEY_TITLE) != null
        }

        return when {
            playing.isNotEmpty() -> playing.first()
            withTrack.size == 1 -> withTrack.first()
            withTrack.isNotEmpty() -> withTrack.first()
            else -> activePackages.first()
        }
    }

    private fun selectControllerLocked(packageName: String?): MediaController? {
        if (controllers.isEmpty()) return null
        if (packageName != null) return controllers[packageName]
        val preferred = preferredPackage?.let { controllers[it] }
        if (preferred != null) return preferred
        return controllers.values.firstOrNull { isCurrentlyPlaying(it) } ?: controllers.values.first()
    }

    private fun clearControllersLocked() {
        for ((packageName, callback) in callbacks) {
            controllers[packageName]?.let { controller ->
                runCatching { controller.unregisterCallback(callback) }
            }
        }
        callbacks.clear()
        controllers.clear()
        artworkCache.evictAll()
    }

    private fun notifyObservers() {
        if (observers.isEmpty()) return
        mainHandler.post {
            for (observer in observers) {
                runCatching { observer.onMediaChanged(this) }
            }
        }
    }

    data class NotificationHint(val title: String?, val artist: String?)

    @SuppressLint("InlinedApi")
    companion object {
        private const val MIN_REFRESH_INTERVAL_MS = 500L

        private val ARTWORK_URI_KEYS = listOf(
            MediaMetadata.METADATA_KEY_MEDIA_URI,
            MediaMetadata.METADATA_KEY_ART_URI,
            MediaMetadata.METADATA_KEY_ALBUM_ART_URI,
            MediaMetadata.METADATA_KEY_DISPLAY_ICON_URI
        )

        private val ARTWORK_BITMAP_KEYS = listOf(
            MediaMetadata.METADATA_KEY_ART,
            MediaMetadata.METADATA_KEY_ALBUM_ART,
            MediaMetadata.METADATA_KEY_DISPLAY_ICON
        )

        @Volatile
        private var instance: MediaRepository? = null

        fun get(context: Context): MediaRepository =
            instance ?: synchronized(this) {
                instance ?: MediaRepository(context.applicationContext).also { instance = it }
            }
    }
}

sealed interface ControlResult {
    data class Success(val snapshot: MediaSnapshot?, val appliedPositionMs: Long?) : ControlResult
    data class Failure(
        val httpStatus: Int,
        val message: String,
        val supported: List<String> = emptyList()
    ) : ControlResult
}

object HttpErrors {
    const val BAD_REQUEST = 400
    const val UNAUTHORIZED = 401
    const val NOT_FOUND = 404
    const val NO_SESSION = 409
    const val MEDIA_ERROR = 502
    const val ACCESS_DENIED = 503
}

private fun Throwable.messageOrType(): String = message?.takeIf { it.isNotBlank() } ?: javaClass.simpleName