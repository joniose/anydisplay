package dev.anydisplay.client.session

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.*
import android.bluetooth.le.*
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.net.*
import android.net.wifi.WifiNetworkSpecifier
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.ParcelUuid
import androidx.core.content.ContextCompat
import dev.anydisplay.client.Elog
import dev.anydisplay.client.data.Identity
import dev.anydisplay.client.data.PairedHosts
import dev.anydisplay.client.protocol.Proto
import java.net.Inet4Address
import java.util.UUID

/**
 * Scans while the connection foreground service is running and the user has enabled BLE.
 * Network requests remain owned by the service until the AP disappears or the service stops.
 */
@SuppressLint("MissingPermission")
internal class BleDirect(private val context: Context, private val networkChanged: (Network?) -> Unit) {
    private val main = Handler(Looper.getMainLooper())
    private val manager = context.getSystemService(BluetoothManager::class.java)
    private val connectivity = context.getSystemService(ConnectivityManager::class.java)
    private val ignoredUntil = mutableMapOf<String, Long>()
    private var scanner: BluetoothLeScanner? = null
    private var gatt: BluetoothGatt? = null
    private var protocol: BleDirectProtocol? = null
    private var networkCallback: ConnectivityManager.NetworkCallback? = null
    private var requestedNetwork: Network? = null
    private var closed = false
    private var registered = false
    private var readySent = false
    private var readySending = false
    private var waitingForHost = false
    private val timeout = Runnable { fail("Bluetooth-Verbindungsaufbau hat zu lange gedauert") }
    private val hostTimeout = Runnable {
        if (waitingForHost) {
            releaseNetwork()
            status("Windows hat die Direktverbindung nicht geöffnet. Bitte erneut verbinden.")
            start()
        }
    }
    private val radioChanged = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            if (intent?.getIntExtra(BluetoothAdapter.EXTRA_STATE, -1) == BluetoothAdapter.STATE_ON) start()
        }
    }

    fun start() {
        if (closed) return
        if (!enabled(context)) { status("Bluetooth-Direktverbindung ist ausgeschaltet."); return }
        if (Build.VERSION.SDK_INT < 29) { status("Automatische WLAN-Verbindung benötigt Android 10 oder neuer."); return }
        if (!hasPermissions(context)) { status("Bluetooth- und WLAN-Berechtigungen bitte freigeben."); return }
        if (!registered) {
            ContextCompat.registerReceiver(context, radioChanged, IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED),
                ContextCompat.RECEIVER_EXPORTED)
            registered = true
        }
        if (networkCallback != null || gatt != null || scanner != null) return
        val adapter = manager?.adapter
        if (adapter?.isEnabled != true) { status("Bluetooth auf dem Tablet einschalten."); return }
        if (PairedHosts(context).list().isEmpty()) {
            status("Für Bluetooth-Direktverbindung einmal per USB oder Netzwerk mit Windows koppeln.")
            return
        }
        try {
            scanner = adapter.bluetoothLeScanner
            scanner?.startScan(listOf(ScanFilter.Builder().setServiceUuid(ParcelUuid(SERVICE)).build()),
                ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_POWER).build(), scan)
            status("Bluetooth bereit. In Windows Direkt-WLAN wählen und verbinden.")
        } catch (e: Exception) { scanner = null; status("Bluetooth-Suche fehlgeschlagen: " + e.message) }
    }

    private val scan = object : ScanCallback() {
        override fun onScanResult(callbackType: Int, result: ScanResult) {
            main.post {
                if (closed || gatt != null || networkCallback != null || SessionHub.session != null) return@post
                if ((ignoredUntil[result.device.address] ?: 0L) > android.os.SystemClock.elapsedRealtime()) return@post
                ignoredUntil[result.device.address] = android.os.SystemClock.elapsedRealtime() + 20_000
                stopScan()
                try {
                    readySent = false
                    gatt = result.device.connectGatt(context, false, callback, BluetoothDevice.TRANSPORT_LE)
                    status("Windows über Bluetooth gefunden. Prüfe Kopplung …")
                    main.postDelayed(timeout, 20_000)
                } catch (e: Exception) { fail("Bluetooth-Verbindung fehlgeschlagen: " + e.message) }
            }
        }
        override fun onScanFailed(errorCode: Int) {
            main.post {
                scanner = null
                status("Bluetooth-Suche nicht verfügbar (" + errorCode + ").")
            }
        }
    }

    private val callback = object : BluetoothGattCallback() {
        override fun onConnectionStateChange(source: BluetoothGatt, result: Int, newState: Int) {
            main.post {
                if (source !== gatt) return@post
                if (result == BluetoothGatt.GATT_SUCCESS && newState == BluetoothProfile.STATE_CONNECTED) {
                    if (!source.discoverServices()) fail("Bluetooth-Dienstsuche konnte nicht starten")
                } else if (newState == BluetoothProfile.STATE_DISCONNECTED || result != BluetoothGatt.GATT_SUCCESS) {
                    if (readySent || networkCallback != null) closeGatt()
                    else fail("Bluetooth-Verbindung wurde beendet (" + result + ")")
                }
            }
        }
        override fun onServicesDiscovered(source: BluetoothGatt, result: Int) {
            main.post {
                if (source !== gatt) return@post
                if (result != BluetoothGatt.GATT_SUCCESS || source.getService(SERVICE) == null || !source.requestMtu(247))
                    fail("AnyDisplay-Bluetooth-Dienst ist nicht verfügbar")
            }
        }
        override fun onMtuChanged(source: BluetoothGatt, mtu: Int, result: Int) {
            main.post {
                if (source !== gatt) return@post
                if (result != BluetoothGatt.GATT_SUCCESS || mtu < 185) fail("Bluetooth-Paketgröße wird nicht unterstützt")
                else read(IDENTITY)
            }
        }
        @Deprecated("Compatibility callback for Android 12 and earlier")
        override fun onCharacteristicRead(source: BluetoothGatt, characteristic: BluetoothGattCharacteristic, result: Int) {
            @Suppress("DEPRECATION")
            received(source, characteristic.uuid, characteristic.value ?: byteArrayOf(), result)
        }
        override fun onCharacteristicRead(source: BluetoothGatt, characteristic: BluetoothGattCharacteristic, value: ByteArray, result: Int) {
            received(source, characteristic.uuid, value.copyOf(), result)
        }
        override fun onCharacteristicWrite(source: BluetoothGatt, characteristic: BluetoothGattCharacteristic, result: Int) {
            main.post {
                if (source !== gatt) return@post
                if (result != BluetoothGatt.GATT_SUCCESS) { fail("Windows hat die Bluetooth-Anfrage abgelehnt (" + result + ")"); return@post }
                when (characteristic.uuid) {
                    REQUEST -> read(OFFER)
                    READY -> {
                        readySent = true
                        main.removeCallbacks(timeout)
                        waitForHost()
                        closeGatt()
                    }
                }
            }
        }
    }

    private fun received(source: BluetoothGatt, uuid: UUID, value: ByteArray, result: Int) {
        main.post {
            if (source !== gatt) return@post
            try {
                require(result == BluetoothGatt.GATT_SUCCESS)
                when (uuid) {
                    IDENTITY -> {
                        require(value.size == 32)
                        val host = value.copyOfRange(0, 16)
                        val secret = PairedHosts(context).secretFor(host)
                            ?: throw IllegalArgumentException("Dieser Windows-PC ist noch nicht mit AnyDisplay gekoppelt")
                        protocol = BleDirectProtocol(secret, host, value.copyOfRange(16, 32))
                        write(REQUEST, protocol!!.request(Identity(context).deviceId))
                    }
                    OFFER -> {
                        val offer = protocol!!.offer(value)
                        main.removeCallbacks(timeout)
                        joinWifi(offer)
                    }
                }
            } catch (e: Exception) { fail(e.message ?: "Bluetooth-Anfrage ungültig") }
        }
    }

    private fun read(uuid: UUID) {
        val source = gatt ?: return
        val characteristic = source.getService(SERVICE)?.getCharacteristic(uuid)
        if (characteristic == null || !source.readCharacteristic(characteristic))
            fail("Bluetooth-Daten konnten nicht gelesen werden")
    }

    @Suppress("DEPRECATION")
    private fun write(uuid: UUID, value: ByteArray) {
        val source = gatt ?: return
        val characteristic = source.getService(SERVICE)?.getCharacteristic(uuid)
        if (characteristic == null) { fail("Bluetooth-Dienst ist unvollständig"); return }
        val accepted = if (Build.VERSION.SDK_INT >= 33) {
            source.writeCharacteristic(characteristic, value, BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT) == BluetoothStatusCodes.SUCCESS
        } else {
            characteristic.writeType = BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT
            characteristic.value = value
            source.writeCharacteristic(characteristic)
        }
        if (!accepted) fail("Bluetooth-Daten konnten nicht gesendet werden")
    }

    private fun joinWifi(offer: BleDirectProtocol.Offer) {
        if (Build.VERSION.SDK_INT < 29) { fail("Android 10 oder neuer erforderlich"); return }
        if (SessionHub.session != null) { fail("Zuerst die laufende AnyDisplay-Verbindung beenden"); return }
        val specifier = WifiNetworkSpecifier.Builder().setSsid(offer.ssid)
            .setBssid(MacAddress.fromBytes(offer.bssid)).setWpa2Passphrase(offer.password).build()
        val request = NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).setNetworkSpecifier(specifier).build()
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                main.post {
                    if (networkCallback !== this || closed) return@post
                    requestedNetwork = network
                    networkChanged(network)
                    waitForHost()
                    announceAddress(network)
                }
            }
            override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) {
                main.post {
                    if (networkCallback === this && requestedNetwork == network) announceAddress(network)
                }
            }
            override fun onUnavailable() {
                main.post { if (networkCallback === this) fail("WLAN-Anfrage abgelehnt oder Direkt-WLAN nicht erreichbar") }
            }
            override fun onLost(network: Network) {
                main.post {
                    if (networkCallback !== this) return@post
                    releaseNetwork()
                    closeGatt()
                    status("Direkt-WLAN beendet. Bluetooth wartet auf Windows.")
                    start()
                }
            }
        }
        networkCallback = callback
        status("Direkt-WLAN wird verbunden. Die Android-Freigabe bei Bedarf bestätigen.")
        try { connectivity.requestNetwork(request, callback, 90_000) }
        catch (e: Exception) { fail("WLAN konnte nicht angefordert werden: " + e.message) }
    }

    private fun waitForHost() {
        main.removeCallbacks(hostTimeout)
        waitingForHost = SessionHub.status.phase != Phase.CONNECTED
        if (waitingForHost) {
            main.postDelayed(hostTimeout, 30_000)
            status("Direkt-WLAN verbunden. Warte auf Windows …")
        }
    }

    private fun announceAddress(network: Network) {
        if (readySending || gatt == null) return
        val address = connectivity.getLinkProperties(network)?.linkAddresses
            ?.firstOrNull { it.address is Inet4Address }?.address ?: return
        readySending = true // coalesce onAvailable and onLinkPropertiesChanged
        write(READY, protocol!!.ready(address.address, Proto.PORT))
    }

    fun connected() {
        waitingForHost = false
        main.removeCallbacks(hostTimeout)
        if (requestedNetwork != null) status("Direkt-WLAN über Bluetooth eingerichtet.")
    }

    private fun stopScan() {
        try { scanner?.stopScan(scan) } catch (_: Exception) {}
        scanner = null
    }

    private fun closeGatt() {
        val source = gatt
        gatt = null
        protocol = null
        main.removeCallbacks(timeout)
        try { source?.disconnect(); source?.close() } catch (_: Exception) {}
    }

    private fun releaseNetwork() {
        waitingForHost = false
        readySent = false
        readySending = false
        main.removeCallbacks(hostTimeout)
        val old = networkCallback
        networkCallback = null
        requestedNetwork = null
        if (old != null) {
            try { connectivity.unregisterNetworkCallback(old) } catch (_: Exception) {}
            networkChanged(null)
        }
    }

    private fun fail(message: String) {
        Elog.w("BLE direct: " + message)
        closeGatt()
        releaseNetwork()
        status(message)
        if (!closed) main.postDelayed({ start() }, 3000)
    }

    private fun status(message: String) {
        Elog.i("BLE direct: " + message)
        SessionHub.update { it.copy(directStatus = message) }
    }

    fun close() {
        closed = true
        main.removeCallbacksAndMessages(null)
        stopScan()
        closeGatt()
        releaseNetwork()
        if (registered) {
            try { context.unregisterReceiver(radioChanged) } catch (_: Exception) {}
            registered = false
        }
    }

    companion object {
        private val SERVICE = UUID.fromString("7162447a-9075-4d55-a17c-7ec7b1c00100")
        private val IDENTITY = UUID.fromString("7162447a-9075-4d55-a17c-7ec7b1c00101")
        private val REQUEST = UUID.fromString("7162447a-9075-4d55-a17c-7ec7b1c00102")
        private val OFFER = UUID.fromString("7162447a-9075-4d55-a17c-7ec7b1c00103")
        private val READY = UUID.fromString("7162447a-9075-4d55-a17c-7ec7b1c00104")

        fun enabled(context: Context) =
            context.getSharedPreferences("anydisplay_direct", Context.MODE_PRIVATE).getBoolean("ble", false)

        fun enable(context: Context) {
            context.getSharedPreferences("anydisplay_direct", Context.MODE_PRIVATE).edit().putBoolean("ble", true).apply()
        }

        fun disable(context: Context) {
            context.getSharedPreferences("anydisplay_direct", Context.MODE_PRIVATE).edit().putBoolean("ble", false).apply()
        }

        fun permissions(): Array<String> = buildList {
            if (Build.VERSION.SDK_INT >= 31) {
                add(Manifest.permission.BLUETOOTH_SCAN)
                add(Manifest.permission.BLUETOOTH_CONNECT)
            }
            if (Build.VERSION.SDK_INT >= 33) add(Manifest.permission.NEARBY_WIFI_DEVICES)
            else {
                add(Manifest.permission.ACCESS_COARSE_LOCATION)
                add(Manifest.permission.ACCESS_FINE_LOCATION)
            }
        }.toTypedArray()

        fun hasPermissions(context: Context) = permissions().all {
            ContextCompat.checkSelfPermission(context, it) == PackageManager.PERMISSION_GRANTED
        }
    }
}
