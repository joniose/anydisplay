package dev.anydisplay.client.session

import android.content.Context
import android.os.SystemClock
import dev.anydisplay.client.Elog
import dev.anydisplay.client.data.Identity
import dev.anydisplay.client.protocol.Proto
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetSocketAddress

/** Handles local broadcasts and unicast queries, including routed VPN interfaces. */
internal class LanDiscovery(context: Context) {
    private val identity = Identity(context)
    private var socket: DatagramSocket? = null
    @Volatile private var stopping = false

    fun start() {
        val listener = try {
            DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                bind(InetSocketAddress(Proto.PORT))
            }
        } catch (e: Exception) {
            Elog.w("UDP discovery unavailable", e)
            return
        }
        socket = listener
        Thread({
            val buffer = ByteArray(64)
            var lastReply = 0L
            while (!stopping) {
                try {
                    val request = DatagramPacket(buffer, buffer.size)
                    listener.receive(request)
                    if (request.address !is Inet4Address || request.address.isMulticastAddress) continue
                    val now = SystemClock.elapsedRealtime()
                    if (now - lastReply < 50) continue
                    val reply = DiscoveryProtocol.reply(request.data.copyOfRange(request.offset, request.offset + request.length),
                        identity.deviceId, identity.deviceName, Proto.PORT) ?: continue
                    listener.send(DatagramPacket(reply, reply.size, request.address, request.port))
                    lastReply = now
                } catch (e: Exception) {
                    if (listener.isClosed || stopping) break
                    Elog.w("UDP discovery failed", e)
                }
            }
        }, "anydisplay-discovery").start()
    }

    fun close() {
        stopping = true
        socket?.close()
        socket = null
    }
}
