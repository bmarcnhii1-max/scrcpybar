package com.scrcpybar.companion.ui

import android.annotation.SuppressLint
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.os.Build
import android.provider.Settings
import android.text.TextUtils
import com.scrcpybar.companion.media.ScrcpyMediaListenerService

object NotificationAccess {

    fun isEnabled(context: Context): Boolean {
        val enabledServices = runCatching {
            Settings.Secure.getString(context.contentResolver, SETTING_ENABLED_LISTENERS)
        }.getOrNull() ?: return false

        val expected = ComponentName(context, ScrcpyMediaListenerService::class.java)
        val splitter = TextUtils.SimpleStringSplitter(':')
        splitter.setString(enabledServices)
        while (splitter.hasNext()) {
            if (ComponentName.unflattenFromString(splitter.next()) == expected) return true
        }
        return false
    }

    /**
     * Opens the system screen where the user grants notification access for this app.
     *
     * Android 11+ can deep link straight to this app's row, older versions only expose the list.
     * Neither lookup is guaranteed to resolve, so the caller falls back when the start throws.
     */
    fun settingsIntent(context: Context): Intent {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val component = ComponentName(context, ScrcpyMediaListenerService::class.java)
            val detail = Intent(Settings.ACTION_NOTIFICATION_LISTENER_DETAIL_SETTINGS)
                .putExtra(Settings.EXTRA_NOTIFICATION_LISTENER_COMPONENT_NAME, component.flattenToString())
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            return detail
        }
        return Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
    }

    /** Fallback for devices that do not expose the notification listener screen. */
    @SuppressLint("InlinedApi")
    fun appNotificationSettingsIntent(context: Context): Intent =
        Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
            .putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

    private const val SETTING_ENABLED_LISTENERS = "enabled_notification_listeners"
}