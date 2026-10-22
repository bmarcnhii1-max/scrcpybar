package com.scrcpybar.companion.core

import android.content.Context
import android.content.SharedPreferences
import java.security.SecureRandom

class Prefs private constructor(private val sp: SharedPreferences) {

    var port: Int
        get() = sp.getInt(KEY_PORT, DEFAULT_PORT).coerceIn(MIN_PORT, MAX_PORT)
        set(value) {
            sp.edit().putInt(KEY_PORT, value.coerceIn(MIN_PORT, MAX_PORT)).apply()
        }

    /**
     * Port the desktop must use to reach this device. The server binds [port] on the phone loopback,
     * but in USB mode the bridge connects through `adb forward tcp<advertisedPort> tcp<port>`, so any
     * URL advertised to the client has to carry the forwarded port instead of the phone-side one.
     */
    var advertisedPort: Int
        get() = sp.getInt(KEY_ADVERTISED_PORT, DEFAULT_ADVERTISED_PORT).coerceIn(MIN_PORT, MAX_PORT)
        set(value) {
            sp.edit().putInt(KEY_ADVERTISED_PORT, value.coerceIn(MIN_PORT, MAX_PORT)).apply()
        }

    var authEnabled: Boolean
        get() = sp.getBoolean(KEY_AUTH_ENABLED, false)
        set(value) = sp.edit().putBoolean(KEY_AUTH_ENABLED, value).apply()

    var autoStartServer: Boolean
        get() = sp.getBoolean(KEY_AUTO_START, true)
        set(value) = sp.edit().putBoolean(KEY_AUTO_START, value).apply()

    val token: String
        get() = existingToken() ?: regenerateToken()

    fun existingToken(): String? = sp.getString(KEY_TOKEN, null)?.takeIf { it.isNotBlank() }

    fun regenerateToken(): String {
        val bytes = ByteArray(TOKEN_BYTES)
        SecureRandom().nextBytes(bytes)
        val token = bytes.joinToString("") { String.format("%02x", it) }
        sp.edit().putString(KEY_TOKEN, token).apply()
        return token
    }

    companion object {
        const val DEFAULT_PORT = 38080
        const val DEFAULT_ADVERTISED_PORT = 39282
        const val MIN_PORT = 1024
        const val MAX_PORT = 65535

        private const val PREFS_NAME = "scrcpybar_companion"
        private const val KEY_PORT = "http_port"
        private const val KEY_ADVERTISED_PORT = "http_advertised_port"
        private const val KEY_AUTH_ENABLED = "http_auth_enabled"
        private const val KEY_AUTO_START = "server_auto_start"
        private const val KEY_TOKEN = "http_token"
        private const val TOKEN_BYTES = 16

        @Volatile
        private var instance: Prefs? = null

        fun get(context: Context): Prefs =
            instance ?: synchronized(this) {
                instance ?: Prefs(
                    context.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
                ).also { instance = it }
            }
    }
}