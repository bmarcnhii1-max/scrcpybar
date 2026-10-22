package com.scrcpybar.companion.core

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkAddress
import android.net.NetworkCapabilities
import java.net.Inet4Address
import java.net.NetworkInterface

object NetworkUtils {

    /** Best guess of the address the desktop bridge can reach this device on. */
    fun primaryLanAddress(context: Context): String? {
        val wifi = wifiAddress(context)
        if (wifi != null) return wifi
        return ethernetAddress(context) ?: interfaceAddresses().firstOrNull()
    }

    fun allLanAddresses(context: Context): List<String> {
        val fromConnectivity = connectivityAddresses(context)
        val fromInterfaces = interfaceAddresses()
        return (fromConnectivity + fromInterfaces).distinct().sorted()
    }

    fun baseUrl(@Suppress("UNUSED_PARAMETER") context: Context, port: Int): String =
        "http://127.0.0.1:$port"

    private fun wifiAddress(context: Context): String? {
        val manager = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            ?: return null
        val network = manager.activeNetwork ?: return null
        val capabilities = manager.getNetworkCapabilities(network) ?: return null
        if (!capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) return null
        return manager.getLinkProperties(network)
            ?.linkAddresses
            ?.firstNotNullOfOrNull { it.ipv4OrNull() }
    }

    private fun ethernetAddress(context: Context): String? {
        val manager = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            ?: return null
        val network = manager.activeNetwork ?: return null
        val capabilities = manager.getNetworkCapabilities(network) ?: return null
        if (!capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) return null
        return manager.getLinkProperties(network)
            ?.linkAddresses
            ?.firstNotNullOfOrNull { it.ipv4OrNull() }
    }

    @Suppress("DEPRECATION")
    private fun connectivityAddresses(context: Context): List<String> {
        val manager = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            ?: return emptyList()
        val result = mutableListOf<String>()
        for (network in manager.allNetworks) {
            val capabilities = manager.getNetworkCapabilities(network) ?: continue
            val local = capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ||
                capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)
            if (!local) continue
            manager.getLinkProperties(network)?.linkAddresses?.forEach { address ->
                address.ipv4OrNull()?.let(result::add)
            }
        }
        return result
    }

    private fun interfaceAddresses(): List<String> {
        val result = mutableListOf<String>()
        val interfaces = runCatching { NetworkInterface.getNetworkInterfaces() }.getOrNull() ?: return result
        while (interfaces?.hasMoreElements() == true) {
            val networkInterface = interfaces.nextElement()
            if (networkInterface.isLoopback || !networkInterface.isUp) continue
            val addresses = networkInterface.inetAddresses ?: continue
            while (addresses.hasMoreElements()) {
                val address = addresses.nextElement()
                if (address is Inet4Address && !address.isLoopbackAddress) {
                    result.add(address.hostAddress ?: continue)
                }
            }
        }
        return result
    }

    private fun LinkAddress.ipv4OrNull(): String? {
        val address = address as? Inet4Address ?: return null
        if (address.isLoopbackAddress) return null
        return address.hostAddress
    }

    fun isWifiConnected(context: Context): Boolean {
        val manager = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            ?: return false
        val network = manager.activeNetwork ?: return false
        val capabilities = manager.getNetworkCapabilities(network) ?: return false
        return capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)
    }
}