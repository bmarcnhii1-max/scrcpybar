package com.scrcpybar.companion.net

import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.Log
import androidx.core.app.NotificationCompat
import com.scrcpybar.companion.R
import com.scrcpybar.companion.core.NetworkUtils
import com.scrcpybar.companion.core.Prefs
import com.scrcpybar.companion.media.MediaRepository
import com.scrcpybar.companion.ui.MainActivity

/**
 * Keeps the HTTP API alive while the app is in the background so the desktop bridge can reach
 * the phone at any time. Without a foreground service Android would kill the process within
 * minutes of the user leaving the app.
 */
class MediaApiService : Service() {

    private lateinit var prefs: Prefs
    private var server: MiniHttpServer? = null
    private val mainHandler = Handler(Looper.getMainLooper())
    private var bindRetry: Runnable? = null

    override fun onCreate() {
        super.onCreate()
        prefs = Prefs.get(this)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopEverything()
                return START_NOT_STICKY
            }
            else -> startEverything()
        }
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        stopEverything()
        super.onDestroy()
    }

    private fun startEverything() {
        startInForeground()

        server?.let { existing ->
            if (existing.isRunning) {
                updateNotification()
                return
            }
            existing.stop()
        }

        val repository = MediaRepository.get(this)
        val router = ApiRouter(this, repository, prefs) {
            ApiRouter.ServerStatus(
                running = server?.isRunning == true,
                port = server?.boundPort ?: prefs.port,
                uptimeMs = server?.uptimeMs ?: 0L,
                error = server?.lastError
            )
        }

        val created = MiniHttpServer(prefs.port, router::dispatch)
        if (created.start()) {
            server = created
            MiniHttpServerHolder.current = created
            Log.i(TAG, "api started on ${created.boundPort}")
            updateNotification()
        } else {
            // The previous process may still hold the port for a moment after a restart, and
            // Android does not re-deliver startForegroundService to a service that is already
            // running. Retrying here is what keeps the phone reachable after a restart.
            Log.e(TAG, "api failed to start: ${created.lastError}")
            scheduleBindRetry()
        }
    }

    private fun scheduleBindRetry() {
        var attempt = 0
        val retry = object : Runnable {
            override fun run() {
                // A stopped service must stay stopped: without this guard a pending retry
                // would resurrect the server after the user pressed "stop" in the notification.
                if (server == null) return
                if (server?.isRunning == true) return
                attempt++
                if (attempt > BIND_RETRY_LIMIT) {
                    Log.e(TAG, "api gave up after $attempt bind attempts")
                    return
                }
                startEverything()
                if (server?.isRunning != true) {
                    mainHandler.postDelayed(this, BIND_RETRY_DELAY_MS)
                }
            }
        }
        bindRetry = retry
        mainHandler.postDelayed(retry, BIND_RETRY_DELAY_MS)
    }

    private fun stopEverything() {
        bindRetry?.let { mainHandler.removeCallbacks(it) }
        bindRetry = null
        server?.stop()
        server = null
        MiniHttpServerHolder.current = null
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    @SuppressLint("InlinedApi")
    private fun startInForeground() {
        val notification = buildNotification()
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                startForeground(
                    NOTIFICATION_ID,
                    notification,
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
                )
            } else {
                startForeground(NOTIFICATION_ID, notification)
            }
        } catch (t: Throwable) {
            Log.e(TAG, "startForeground failed", t)
        }
    }

    private fun buildNotification(): Notification {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && manager.getNotificationChannel(CHANNEL_ID) == null) {
            manager.createNotificationChannel(
                NotificationChannel(
                    CHANNEL_ID,
                    getString(R.string.notification_channel_name),
                    NotificationManager.IMPORTANCE_LOW
                ).apply {
                    description = getString(R.string.notification_channel_description)
                    setShowBadge(false)
                }
            )
        }

        val contentIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        val stopIntent = PendingIntent.getService(
            this,
            1,
            Intent(this, MediaApiService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        val endpoint = NetworkUtils.baseUrl(this, prefs.advertisedPort)

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(getString(R.string.notification_title))
            .setContentText(endpoint ?: getString(R.string.notification_text_fallback))
            .setStyle(NotificationCompat.BigTextStyle().bigText(
                endpoint?.let { getString(R.string.notification_big_text, it, prefs.advertisedPort) }
                    ?: getString(R.string.notification_text_fallback)
            ))
            .setContentIntent(contentIntent)
            .addAction(0, getString(R.string.action_stop_server), stopIntent)
            .setOngoing(true)
            .setShowWhen(false)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
    }

    private fun updateNotification() {
        runCatching {
            val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            manager.notify(NOTIFICATION_ID, buildNotification())
        }
    }

    object MiniHttpServerHolder {
        @Volatile
        var current: MiniHttpServer? = null
    }

    companion object {
        private const val TAG = "ScrcpyBarService"
        private const val CHANNEL_ID = "scrcpybar_api"
        private const val NOTIFICATION_ID = 4711
        private const val ACTION_STOP = "com.scrcpybar.companion.action.STOP_API"
        private const val BIND_RETRY_LIMIT = 10
        private const val BIND_RETRY_DELAY_MS = 1000L

        fun start(context: Context) {
            val intent = Intent(context, MediaApiService::class.java)
            try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    context.startForegroundService(intent)
                } else {
                    context.startService(intent)
                }
            } catch (t: Throwable) {
                Log.e(TAG, "cannot start api service", t)
            }
        }

        fun stop(context: Context) {
            runCatching { context.stopService(Intent(context, MediaApiService::class.java)) }
        }

        fun isRunning(): Boolean = MiniHttpServerHolder.current?.isRunning == true
    }
}