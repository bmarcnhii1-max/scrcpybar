package com.scrcpybar.companion.media

import android.app.Notification
import android.os.Bundle
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification

/**
 * Grants this app notification listener access, which is the only supported way for a normal
 * application to enumerate [android.media.session.MediaSession] instances.
 *
 * It is also used as a metadata fallback: media-style notifications carry the title/artist even
 * when the publishing app exposes no `MediaMetadata`.
 */
class ScrcpyMediaListenerService : NotificationListenerService() {

    private val repository by lazy { MediaRepository.get(this) }

    override fun onListenerConnected() {
        super.onListenerConnected()
        repository.onListenerConnected()
    }

    override fun onListenerDisconnected() {
        repository.onListenerDisconnected()
        super.onListenerDisconnected()
    }

    override fun onNotificationPosted(sbn: StatusBarNotification?) {
        super.onNotificationPosted(sbn)
        publishHints()
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification?) {
        super.onNotificationRemoved(sbn)
        publishHints()
    }

    private fun publishHints() {
        val active = runCatching { activeNotifications }.getOrNull() ?: return
        val hints = LinkedHashMap<String, MediaRepository.NotificationHint>()
        for (sbn in active) {
            if (sbn.packageName == packageName) continue
            val notification = sbn.notification ?: continue
            val extras = notification.extras ?: continue
            if (extras.containsKey(Notification.EXTRA_MEDIA_SESSION) ||
                extras.containsKey(Notification.EXTRA_TITLE)
            ) {
                hints[sbn.packageName] = readHint(extras)
            }
        }
        repository.updateNotificationHints(hints)
    }

    private fun readHint(extras: Bundle): MediaRepository.NotificationHint {
        val title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString()?.takeIf { it.isNotBlank() }
        val text = extras.getCharSequence(Notification.EXTRA_TEXT)?.toString()?.takeIf { it.isNotBlank() }
        return MediaRepository.NotificationHint(title = title, artist = text)
    }
}