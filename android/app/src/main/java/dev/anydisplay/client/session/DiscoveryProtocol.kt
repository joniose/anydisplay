package dev.anydisplay.client.session

import java.nio.ByteBuffer

/** Small UDP discovery exchange. Pairing and stream authentication still happen over TCP. */
internal object DiscoveryProtocol {
    private val queryMagic = byteArrayOf(65, 68, 81, 49)
    private val replyMagic = byteArrayOf(65, 68, 82, 49)

    fun reply(query: ByteArray, deviceId: ByteArray, name: String, port: Int): ByteArray? {
        if (query.size != 20 || !query.copyOfRange(0, 4).contentEquals(queryMagic)) return null
        require(deviceId.size == 16 && port in 1..65535)
        val encoded = name.toByteArray(Charsets.UTF_8).take(240).toByteArray()
        return ByteBuffer.allocate(40 + encoded.size).put(replyMagic)
            .put(query, 4, 16).put(deviceId).putShort(port.toShort()).putShort(encoded.size.toShort())
            .put(encoded).array()
    }
}
