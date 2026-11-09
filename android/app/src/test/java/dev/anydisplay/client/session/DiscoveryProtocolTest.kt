package dev.anydisplay.client.session

import java.nio.ByteBuffer
import org.junit.Assert.*
import org.junit.Test

class DiscoveryProtocolTest {
    @Test fun replyPreservesChallengeAndIdentity() {
        val challenge = ByteArray(16) { it.toByte() }
        val id = ByteArray(16) { (it + 16).toByte() }
        val query = byteArrayOf(65, 68, 81, 49) + challenge
        val reply = ByteBuffer.wrap(DiscoveryProtocol.reply(query, id, "OPPO Pad 5", 47820)!!)
        assertEquals(0x41445231, reply.int)
        assertArrayEquals(challenge, ByteArray(16).also(reply::get))
        assertArrayEquals(id, ByteArray(16).also(reply::get))
        assertEquals(47820, reply.short.toInt() and 65535)
        val name = ByteArray(reply.short.toInt() and 65535).also(reply::get)
        assertEquals("OPPO Pad 5", name.toString(Charsets.UTF_8))
        assertFalse(reply.hasRemaining())
        assertNull(DiscoveryProtocol.reply(query + 0, id, "Tablet", 47820))
        assertNull(DiscoveryProtocol.reply(byteArrayOf(), id, "Tablet", 47820))
    }
}
