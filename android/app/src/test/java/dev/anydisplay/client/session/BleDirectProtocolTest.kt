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
        assertNull(offer.frequencyMhz)
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

    @Test fun readsCurrentFrequencyFromWindowsBeacon() {
        val packet = "41445731000102030405060708090a0b0c0d0e0f0000018bcfe56800a0a1a2a3a4a5a6a7652f193679bba245f5cbf47c65167271dd085723e51a37ca63c39929c7bc4b406da2e6aff00a4eba102a1ce4ec32f1a4694cb9c420abf00020379179d480ac0414685bf3a490d5e0bb5bc92d84b810011365da61239956ec19eb836610e739547eccfc4f164c4b06a52760a1c08f833f".hexToBytes()
        val device = ByteArray(16) { (it + 16).toByte() }
        val secretFor: (ByteArray) -> ByteArray? = { ByteArray(32) { i -> i.toByte() } }
        val decoded = BleDirectProtocol.broadcast(packet, device, secretFor, 1_700_000_000_000L)!!
        assertEquals("DIRECT-AnyDisplay-1234abcd", decoded.offer.ssid)
        assertEquals(5785, decoded.offer.frequencyMhz)
        packet[packet.size - 33] = (packet[packet.size - 33].toInt() xor 1).toByte()
        assertNull(BleDirectProtocol.broadcast(packet, device, secretFor, 1_700_000_000_000L))
    }

    @Test fun extendedBeaconIsAddressedAuthenticatedAndTimeLimited() {
        val packet = "41445731000102030405060708090a0b0c0d0e0f0000018bcfe56800a0a1a2a3a4a5a6a7404142434445464748494a4b4c4d4e4f433fb81d5c8fe550180ac3460b601d0936852eb3e188d6d9e0eeb35b6e3cce741f2cb09f5db7f54c28ffc80347404ffe58a4c990ccc5a7175e1f0cc525c7900c82b85e43e676debaed292eb480988d86eaab5d2ba20bd32d82f0079c03e1522e".hexToBytes()
        val device = ByteArray(16) { (it + 16).toByte() }
        val secretFor: (ByteArray) -> ByteArray? = { ByteArray(32) { i -> i.toByte() } }
        val decoded = BleDirectProtocol.broadcast(packet, device, secretFor, 1_700_000_000_000L)!!
        assertEquals(1_700_000_000_000L, decoded.issuedAt)
        assertEquals("DIRECT-AnyDisplay-050affa8", decoded.offer.ssid)
        assertNull(BleDirectProtocol.broadcast(packet, ByteArray(16), secretFor, 1_700_000_000_000L))
        val changed = packet.copyOf().also { it[25] = (it[25].toInt() xor 1).toByte() }
        assertNull(BleDirectProtocol.broadcast(changed, device, secretFor, 1_700_000_000_000L))
        assertThrows(IllegalArgumentException::class.java) {
            BleDirectProtocol.broadcast(packet, device, secretFor, 1_700_000_121_000L)
        }
    }
}
