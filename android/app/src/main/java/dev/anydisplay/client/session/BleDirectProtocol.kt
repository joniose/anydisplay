package dev.anydisplay.client.session

import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.IvParameterSpec
import javax.crypto.spec.SecretKeySpec

/** Version 1 of the authenticated BLE bootstrap, shared with tools/AnyDisplay-Ble.cs. */
internal class BleDirectProtocol(
    private val secret: ByteArray,
    private val hostId: ByteArray,
    private val serverNonce: ByteArray,
    private val clientNonce: ByteArray = ByteArray(16).also(SecureRandom()::nextBytes),
) {
    data class Offer(val bssid: ByteArray, val ssid: String, val password: String)

    init {
        require(secret.size == 32 && hostId.size == 16 && serverNonce.size == 16 && clientNonce.size == 16)
    }

    fun request(deviceId: ByteArray): ByteArray {
        require(deviceId.size == 16)
        return deviceId + clientNonce + tag("request", hostId, serverNonce, deviceId, clientNonce)
    }

    fun offer(packet: ByteArray): Offer {
        require(packet.size in 80..208)
        val encrypted = packet.copyOfRange(0, packet.size - 32)
        val signature = packet.copyOfRange(packet.size - 32, packet.size)
        require(MessageDigest.isEqual(signature, tag("offer", serverNonce, clientNonce, encrypted))) {
            "Bluetooth-Anfrage ist nicht authentifiziert"
        }
        val iv = encrypted.copyOfRange(0, 16)
        val cipher = Cipher.getInstance("AES/CBC/PKCS5Padding")
        cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key("encryption"), "AES"), IvParameterSpec(iv))
        val body = ByteBuffer.wrap(cipher.doFinal(encrypted.copyOfRange(16, encrypted.size)))
        val bssid = ByteArray(6).also(body::get)
        fun text(): String {
            val size = body.get().toInt() and 255
            require(size <= body.remaining())
            val bytes = ByteArray(size).also(body::get)
            return StandardCharsets.UTF_8.newDecoder().decode(ByteBuffer.wrap(bytes)).toString()
        }
        val ssid = text()
        val password = text()
        require(!body.hasRemaining() && ssid.matches(Regex("DIRECT-AnyDisplay-[0-9a-f]{8}")))
        require(password.length in 8..63 && password.all { it.code in 32..126 })
        require(bssid[0].toInt() and 1 == 0 && bssid.any { it.toInt() != 0 })
        return Offer(bssid, ssid, password)
    }

    fun ready(address: ByteArray, port: Int): ByteArray {
        require(address.size == 4 && port in 1..65535)
        val endpoint = address + byteArrayOf((port shr 8).toByte(), port.toByte())
        return endpoint + tag("ready", serverNonce, clientNonce, endpoint)
    }

    private fun key(purpose: String) = hmac(secret, "AnyDisplay BLE $purpose v1".toByteArray(Charsets.UTF_8))
    private fun tag(kind: String, vararg values: ByteArray): ByteArray =
        hmac(key("authentication"), kind.toByteArray(Charsets.UTF_8) + values.fold(byteArrayOf()) { a, b -> a + b })

    private fun hmac(key: ByteArray, data: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").run { init(SecretKeySpec(key, "HmacSHA256")); doFinal(data) }

    data class BroadcastOffer(val hostId: ByteArray, val issuedAt: Long, val offer: Offer)

    companion object {
        val BROADCAST_MAGIC = byteArrayOf(65, 68, 87, 49)

        fun broadcast(packet: ByteArray, deviceId: ByteArray, secretFor: (ByteArray) -> ByteArray?, now: Long): BroadcastOffer? {
            if (packet.size !in 116..244 || !packet.copyOfRange(0, 4).contentEquals(BROADCAST_MAGIC)) return null
            val host = packet.copyOfRange(4, 20)
            val nonce = packet.copyOfRange(20, 36)
            val secret = secretFor(host) ?: return null
            val decoded = runCatching {
                BleDirectProtocol(secret, host, nonce, deviceId).offer(packet.copyOfRange(36, packet.size))
            }.getOrNull() ?: return null
            val issuedAt = ByteBuffer.wrap(nonce).long
            require(issuedAt >= now - 120_000 && issuedAt <= now + 30_000) {
                "Uhrzeit auf Windows und Tablet prüfen und erneut verbinden."
            }
            return BroadcastOffer(host, issuedAt, decoded)
        }
    }
}
