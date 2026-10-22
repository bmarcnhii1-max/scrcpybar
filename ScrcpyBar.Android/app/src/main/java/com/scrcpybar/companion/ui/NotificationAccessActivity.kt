package com.scrcpybar.companion.ui

import android.content.Intent
import android.os.Bundle
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import com.google.android.material.appbar.MaterialToolbar
import com.scrcpybar.companion.R
import com.scrcpybar.companion.databinding.ActivityNotificationAccessBinding

/**
 * Dedicated screen whose only job is to get the user to the system notification-access settings,
 * which is the permission MediaSessionManager requires.
 */
class NotificationAccessActivity : AppCompatActivity() {

    private lateinit var binding: ActivityNotificationAccessBinding

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityNotificationAccessBinding.inflate(layoutInflater)
        setContentView(binding.root)

        val toolbar = findViewById<MaterialToolbar>(R.id.toolbar)
        setSupportActionBar(toolbar)
        toolbar.setNavigationOnClickListener { finish() }

        binding.openSettingsButton.setOnClickListener { open(NotificationAccess.settingsIntent(this)) }
        binding.openAppNotificationSettingsButton.setOnClickListener {
            open(NotificationAccess.appNotificationSettingsIntent(this))
        }
        binding.refreshButton.setOnClickListener { render() }
    }

    override fun onResume() {
        super.onResume()
        render()
    }

    private fun open(intent: Intent) {
        try {
            startActivity(intent)
        } catch (t: Throwable) {
            toast(getString(R.string.toast_no_settings_screen))
        }
    }

    private fun render() {
        val granted = NotificationAccess.isEnabled(this)
        binding.statusBanner.setText(if (granted) R.string.listener_granted_banner else R.string.listener_denied_banner)
        binding.refreshButton.isEnabled = !granted
        binding.openAppNotificationSettingsButton.visibility =
            if (granted) android.view.View.GONE else android.view.View.VISIBLE
    }

    private fun toast(message: String) {
        Toast.makeText(this, message, Toast.LENGTH_SHORT).show()
    }
}