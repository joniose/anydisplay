package dev.anydisplay.client.session

import dev.anydisplay.client.protocol.hexToBytes
import dev.anydisplay.client.protocol.toHex
import org.junit.Assert.*
import org.junit.Test

class BleDirectProtocolTest {
    private fun session(client: ByteArray = ByteArray(16) { (it + 48).toByte() }) =
        BleDirectProtocol(ByteArray(32) { it.toByte() }, ByteArray(16) { it.toByte() },
            ByteArray(16) { (it + 32).toByte() }, client)
    private val packet = "404142434445464748494a4b4c4d4e4f433fb81d5c8fe550180ac3460b601d0936852eb3e188d6d9e0eeb35b6e3cce741f2cb09f5db7f54c28ffc80347404ffe58a4c990ccc5a7175e1f0cc525c7900c2318bd1485e873f8986e81b0d152d81fe8f5da91a31a8a53f6646a1b2bc2028e".hexToBytes()

    @Test fun sharedWindowsRequestAndOffer() {
        val request = session().request(ByteArray(16) { (it + 16).toByte() })
        assertEquals("584d7231752f99825838a090df0a7e6d63cfa676bbbb19f3fa0ed47e06549df0", request.copyOfRange(32, 64).toHex())
        val offer = session().offer(packet)
        assertEquals("DIRECT-AnyDisplay-050affa8", offer.ssid)
        assertEquals("0123456789abcdef0123", offer.password)
        assertEquals("9a3b8fd8542d", offer.bssid.toHex())
        val ready = session().ready(byteArrayOf(192.toByte(), 168.toByte(), 137.toByte(), 20), 47820)
        assertEquals("d1569a3024c4293af340d899e852854ddb5f276d5c94cae0b5582eeef36ca497", ready.copyOfRange(6, 38).toHex())
    }

    @Test fun rejectsTamperingAndReplayWithAnotherChallenge() {
        for (index in listOf(0, 16, packet.lastIndex)) {
            val bad = packet.copyOf().also { it[index] = (it[index].toInt() xor 1).toByte() }
            assertThrows(IllegalArgumentException::class.java) { session().offer(bad) }
        }
        assertThrows(IllegalArgumentException::class.java) { session(ByteArray(16)).offer(packet) }
        assertThrows(IllegalArgumentException::class.java) { session().offer(byteArrayOf()) }
    }
}
