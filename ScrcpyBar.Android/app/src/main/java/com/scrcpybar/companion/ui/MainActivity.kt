package com.scrcpybar.companion.ui

import android.Manifest
import android.annotation.SuppressLint
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.widget.EditText
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.isVisible
import com.scrcpybar.companion.BuildConfig
import com.scrcpybar.companion.R
import com.scrcpybar.companion.core.NetworkUtils
import com.scrcpybar.companion.core.Prefs
import com.scrcpybar.companion.databinding.ActivityMainBinding
import com.scrcpybar.companion.media.ControlAction
import com.scrcpybar.companion.media.ControlResult
import com.scrcpybar.companion.media.MediaRepository
import com.scrcpybar.companion.net.MediaApiService
import java.util.Locale

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private lateinit var prefs: Prefs
    private val repository by lazy { MediaRepository.get(this) }

    private val observer = MediaRepository.Observer { runOnUiThread { render() } }

    private val requestPostNotifications =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { render() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        prefs = Prefs.get(this)
        binding.toolbar.title = getString(R.string.app_name)

        binding.savePortButton.setOnClickListener { savePort() }
        binding.toggleServerButton.setOnClickListener { toggleServer() }
        binding.authSwitch.isChecked = prefs.authEnabled
        binding.authSwitch.setOnCheckedChangeListener { _, checked ->
            prefs.authEnabled = checked
            render()
        }
        binding.openNotificationSettingsButton.setOnClickListener { openNotificationAccessScreen() }
        binding.postNotificationsButton.setOnClickListener { requestPostNotificationsIfNeeded() }
        binding.copyTokenButton.setOnClickListener { copyToken() }
        binding.regenerateTokenButton.setOnClickListener { regenerateToken() }
        binding.previousButton.setOnClickListener { control("previous") }
        binding.playPauseButton.setOnClickListener { control("playPause") }
        binding.nextButton.setOnClickListener { control("next") }

        binding.apiReferenceText.text = getString(
            R.string.api_reference,
            BuildConfig.VERSION_NAME,
            BuildConfig.API_VERSION
        )
    }

    override fun onStart() {
        super.onStart()
        repository.addObserver(observer)
        if (prefs.autoStartServer && !MediaApiService.isRunning()) {
            MediaApiService.start(this)
        }
        render()
    }

    override fun onResume() {
        super.onResume()
        render()
    }

    override fun onStop() {
        repository.removeObserver(observer)
        super.onStop()
    }

    // ---------------------------------------------------------------- actions

    private fun savePort() {
        val value = binding.portInput.text?.toString()?.toIntOrNull()
        if (value == null || value < Prefs.MIN_PORT || value > Prefs.MAX_PORT) {
            binding.portInputLayout.error = getString(R.string.error_invalid_port)
            return
        }
        binding.portInputLayout.error = null
        prefs.port = value
        MediaApiService.stop(this)
        MediaApiService.start(this)
        toast(getString(R.string.toast_port_saved, value))
        render()
    }

    private fun toggleServer() {
        if (MediaApiService.isRunning()) {
            MediaApiService.stop(this)
            toast(getString(R.string.toast_server_stopped))
        } else {
            MediaApiService.start(this)
            toast(getString(R.string.toast_server_started))
        }
        binding.rootLayout.postDelayed({ render() }, 400L)
    }

    private fun openNotificationAccessScreen() {
        startActivity(Intent(this, NotificationAccessActivity::class.java))
    }

    @SuppressLint("InlinedApi")
    private fun requestPostNotificationsIfNeeded() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return
        if (isPostNotificationPermissionGranted()) {
            render()
            return
        }
        requestPostNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
    }

    @SuppressLint("InlinedApi")
    private fun isPostNotificationPermissionGranted(): Boolean =
        ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) ==
            PackageManager.PERMISSION_GRANTED

    private fun copyToken() {
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.setPrimaryClip(ClipData.newPlainText("ScrcpyBar token", prefs.token))
        toast(getString(R.string.toast_token_copied))
    }

    private fun regenerateToken() {
        prefs.regenerateToken()
        render()
        toast(getString(R.string.toast_token_regenerated))
    }

    private fun control(action: String) {
        val result = repository.control(ControlAction(action, null))
        if (result is ControlResult.Failure) toast(result.message)
        render()
    }

    private fun toast(message: String) {
        Toast.makeText(this, message, Toast.LENGTH_SHORT).show()
    }

    // ---------------------------------------------------------------- rendering

    private fun render() {
        val running = MediaApiService.isRunning()
        val port = prefs.port

        binding.portInput.setTextIfChanged(port.toString())
        binding.authSwitch.isChecked = prefs.authEnabled
        binding.tokenText.text = if (prefs.authEnabled) prefs.token else getString(R.string.token_disabled)

        binding.serverStatusChip.setText(if (running) R.string.status_running else R.string.status_stopped)
        binding.serverStatusChip.chipBackgroundColor =
            ContextCompat.getColorStateList(this, if (running) R.color.status_ok else R.color.status_idle)

        binding.endpointText.text = getString(
            R.string.endpoint_summary,
            NetworkUtils.baseUrl(this, prefs.advertisedPort),
            prefs.advertisedPort,
            prefs.port
        )

        binding.toggleServerButton.setText(
            if (running) R.string.action_stop_server else R.string.action_start_server
        )

        binding.serverErrorText.isVisible = !running
        if (binding.serverErrorText.isVisible) {
            binding.serverErrorText.setText(R.string.error_no_network)
        }

        renderAccess()
        renderNowPlaying()
    }

    private fun renderAccess() {
        val granted = NotificationAccess.isEnabled(this)
        binding.listenerStatusText.setText(if (granted) R.string.listener_granted else R.string.listener_denied)

        val needsPostPermission = Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            !isPostNotificationPermissionGranted()
        binding.postNotificationsButton.isVisible = needsPostPermission
        binding.openNotificationSettingsButton.setText(
            if (granted) R.string.action_open_notification_settings else R.string.action_grant_notification_access
        )
    }

    private fun renderNowPlaying() {
        val snapshot = repository.snapshot()
        if (snapshot == null) {
            binding.nowPlayingText.setText(R.string.now_playing_empty)
            binding.playPauseButton.setText(R.string.action_play)
            binding.previousButton.isEnabled = false
            binding.nextButton.isEnabled = false
            return
        }

        binding.nowPlayingText.text = getString(
            R.string.now_playing_format,
            snapshot.title ?: getString(R.string.unknown_title),
            snapshot.artist ?: getString(R.string.unknown_artist),
            snapshot.appLabel,
            snapshot.packageName,
            formatDuration(snapshot.positionMs),
            formatDuration(snapshot.durationMs),
            snapshot.stateName
        )
        binding.playPauseButton.setText(if (snapshot.isPlaying) R.string.action_pause else R.string.action_play)
        binding.previousButton.isEnabled = true
        binding.nextButton.isEnabled = true
    }

    private fun formatDuration(millis: Long): String {
        if (millis <= 0) return "--:--"
        val totalSeconds = millis / 1000
        val hours = totalSeconds / 3600
        val minutes = (totalSeconds % 3600) / 60
        val seconds = totalSeconds % 60
        val locale = Locale.getDefault()
        return if (hours > 0) {
            String.format(locale, "%d:%02d:%02d", hours, minutes, seconds)
        } else {
            String.format(locale, "%d:%02d", minutes, seconds)
        }
    }

    private fun EditText.setTextIfChanged(value: String) {
        if (text?.toString() != value) setText(value)
    }
}