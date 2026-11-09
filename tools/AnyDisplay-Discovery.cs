using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;

// UDP supplements mDNS on LANs and sends unicast queries to known VPN peers.
// Discovery never establishes a streaming session or accepts a pairing code.
internal static class ReceiverDiscovery
{
    private const int Port = 47820;
    private static string HistoryPath {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnyDisplay", "network-peers.txt"); }
    }

    internal static void Remember(string endpoint)
    {
        IPAddress ip;
        if (!IPAddress.TryParse(endpoint.Split(':')[0], out ip) || ip.AddressFamily != AddressFamily.InterNetwork) return;
        try
        {
            var old = File.Exists(HistoryPath) ? File.ReadAllLines(HistoryPath) : new string[0];
            string address = ip.ToString();
            if (old.Contains(address)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath));
            File.WriteAllLines(HistoryPath, new[] { address }.Concat(old).Distinct().Take(16));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static string Find()
    {
        var targets = new HashSet<IPAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up &&
            (item.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || item.NetworkInterfaceType == NetworkInterfaceType.Ethernet)))
        {
            foreach (var local in adapter.GetIPProperties().UnicastAddresses.Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork && item.IPv4Mask != null))
            {
                byte[] ip = local.Address.GetAddressBytes(), mask = local.IPv4Mask.GetAddressBytes();
                if (IPAddress.IsLoopback(local.Address) || ip[0] == 169 || mask.All(value => value == 255)) continue;
                targets.Add(new IPAddress(Enumerable.Range(0, 4).Select(i => (byte)(ip[i] | (mask[i] ^ 255))).ToArray()));
            }
        }
        try
        {
            if (File.Exists(HistoryPath)) foreach (var address in File.ReadAllLines(HistoryPath).Take(16))
            {
                IPAddress ip;
                if (IPAddress.TryParse(address, out ip) && ip.AddressFamily == AddressFamily.InterNetwork) targets.Add(ip);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        foreach (var address in TailscalePeers()) targets.Add(address);
        if (targets.Count == 0) return "";
        byte[] nonce = BleDirect.Random(16);
        byte[] query = BleDirect.Join(Encoding.ASCII.GetBytes("ADQ1"), nonce);
        var output = new StringBuilder();
        using (var client = new UdpClient(AddressFamily.InterNetwork))
        {
            client.EnableBroadcast = true;
            client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            client.Client.ReceiveTimeout = 150;
            var clock = Stopwatch.StartNew();
            long nextQuery = 0;
            while (clock.ElapsedMilliseconds < 2200)
            {
                if (clock.ElapsedMilliseconds >= nextQuery)
                {
                    foreach (var address in targets.Take(80))
                        try { client.Send(query, query.Length, new IPEndPoint(address, Port)); } catch (SocketException) { }
                    nextQuery += 750;
                }
                try
                {
                    var source = new IPEndPoint(IPAddress.Any, 0);
                    byte[] reply = client.Receive(ref source);
                    string peer = ParseReply(reply, nonce, source.Address);
                    if (peer != null) output.Append(peer);
                }
                catch (SocketException) { }
            }
        }
        return output.ToString();
    }

    internal static string ParseReply(byte[] packet, byte[] nonce, IPAddress source)
    {
        if (packet.Length < 40 || packet.Length > 280 || nonce.Length != 16 || source.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(source) || !packet.Take(4).SequenceEqual(Encoding.ASCII.GetBytes("ADR1")) ||
            !BleDirect.Equal(packet.Skip(4).Take(16).ToArray(), nonce)) return null;
        int port = packet[36] * 256 + packet[37], length = packet[38] * 256 + packet[39];
        if (port != Port || length != packet.Length - 40) return null;
        string id = BitConverter.ToString(packet, 20, 16).Replace("-", "").ToLowerInvariant();
        string name = length == 0 ? "" : BitConverter.ToString(packet, 40, length).Replace("-", "").ToLowerInvariant();
        string endpoint = source + ":" + port;
        return "PEER " + endpoint + " " + id + " 0" + Environment.NewLine +
            "PEER_NAME " + endpoint + " " + name + Environment.NewLine;
    }

    private static IEnumerable<IPAddress> TailscalePeers()
    {
        string cli = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        if (!File.Exists(cli)) return new IPAddress[0];
        try
        {
            var text = new StringBuilder();
            using (var process = new Process {
                StartInfo = new ProcessStartInfo(cli, "status --json") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                }
            })
            {
                process.OutputDataReceived += (sender, args) => { if (args.Data != null) lock (text) text.AppendLine(args.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(1800)) { process.Kill(); process.WaitForExit(); return new IPAddress[0]; }
                process.WaitForExit();
                if (process.ExitCode != 0) return new IPAddress[0];
            }
            string json;
            lock (text) json = text.ToString();
            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            object peers;
            if (!root.TryGetValue("Peer", out peers) || !(peers is Dictionary<string, object>)) return new IPAddress[0];
            var addresses = new List<IPAddress>();
            foreach (var peer in ((Dictionary<string, object>)peers).Values.OfType<Dictionary<string, object>>())
            {
                object ips;
                if (!peer.TryGetValue("TailscaleIPs", out ips) || !(ips is System.Collections.IEnumerable) || ips is string) continue;
                foreach (var value in (System.Collections.IEnumerable)ips)
                {
                    IPAddress ip;
                    if (IPAddress.TryParse(value as string, out ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        byte[] bytes = ip.GetAddressBytes();
                        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) addresses.Add(ip);
                    }
                }
                if (addresses.Count >= 64) break;
            }
            return addresses;
        }
        catch (Exception) { return new IPAddress[0]; }
    }
}
