using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using Windows.Foundation;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

internal sealed class PairedReceiver
{
    public string Id, Name, PairedAt;
    public byte[] Secret;
    public override string ToString() { return Name + " · ID " + Id.Substring(0, 8) + (String.IsNullOrEmpty(PairedAt) ? "" : " · gekoppelt " + PairedAt); }
}

// The same protocol is implemented by Android's BleDirectProtocol.
// Each GATT session receives a fresh server challenge. A paired receiver proves possession
// of its AnyDisplay secret before receiving encrypt-then-MAC protected Wi-Fi credentials.
internal sealed class BleDirect : IDisposable
{
    internal static readonly Guid ServiceId = new Guid("7162447a-9075-4d55-a17c-7ec7b1c00100");
    private static Guid Characteristic(int n) { return new Guid("7162447a-9075-4d55-a17c-7ec7b1c0010" + n); }
    private GattServiceProvider provider;
    private BluetoothLEAdvertisementPublisher broadcaster;
    private volatile bool initializing = true;
    private GattLocalCharacteristic identity, request, offer, ready;
    private readonly Dictionary<string, Exchange> exchanges = new Dictionary<string, Exchange>();
    private readonly object sync = new object();
    private readonly byte[] hostId, deviceId, secret, bssid;
    private readonly string ssid, password;
    private readonly int frequencyMhz;
    private bool broadcastRetried;
    private volatile bool disposed;
    private bool accepted;
    public event Action<string> PeerReady;
    public event Action<string> Failed;

    private sealed class Exchange
    {
        public readonly DateTime Created = DateTime.UtcNow;
        public byte[] ServerNonce = Random(16);
        public byte[] ClientNonce, Packet;
    }

    internal static byte[] Hex(string hex, int length)
    {
        if (hex == null || hex.Length != length * 2) throw new InvalidDataException("Ungültige AnyDisplay-Kopplung.");
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    public static PairedReceiver[] Receivers(out byte[] host)
    {
        host = null;
        string file = Environment.GetEnvironmentVariable("ANYDISPLAY_STORE");
        if (String.IsNullOrEmpty(file))
            file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnyDisplay", "host.json");
        if (!File.Exists(file)) return new PairedReceiver[0];
        var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
        host = Hex((string)json["host_id"], 16);
        var devices = (Dictionary<string, object>)json["devices"];
        return devices.Select(item =>
        {
            var value = (Dictionary<string, object>)item.Value;
            Hex(item.Key, 16);
            return new PairedReceiver { Id = item.Key, Name = (string)value["name"], PairedAt = value.ContainsKey("paired_at") ? (string)value["paired_at"] : "", Secret = Hex((string)value["secret"], 32) };
        }).OrderByDescending(item => item.PairedAt).ThenBy(item => item.Name).ToArray();
    }

    public BleDirect(byte[] host, PairedReceiver receiver, string ssid, string password, byte[] bssid, int frequencyMhz)
    {
        hostId = host;
        deviceId = Hex(receiver.Id, 16);
        secret = receiver.Secret;
        this.ssid = ssid;
        this.password = password;
        this.bssid = bssid;
        this.frequencyMhz = frequencyMhz;
    }

    public async Task Start()
    {
        var adapter = await Result(BluetoothAdapter.GetDefaultAsync());
        if (adapter == null || !adapter.IsLowEnergySupported ||
            (!adapter.IsExtendedAdvertisingSupported && !adapter.IsPeripheralRoleSupported))
            throw new InvalidOperationException("Bluetooth LE als Sender ist auf diesem PC nicht verfügbar.");
        if (adapter.IsExtendedAdvertisingSupported)
        {
            try
            {
                await StartBroadcast();
                initializing = false;
                return;
            }
            catch (Exception ex)
            {
                StopBroadcast();
                if (disposed || !adapter.IsPeripheralRoleSupported) throw;
                DiagnosticLog.Note("BLE advertisement start failed, trying GATT: " + ex.GetType().Name);
            }
        }
        await StartGatt();
        initializing = false;
    }

    private async Task StartGatt()
    {
        var result = await Result(GattServiceProvider.CreateAsync(ServiceId));
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException("Bluetooth LE konnte nicht gestartet werden (" + result.Error + ").");
        lock (sync)
        {
            if (disposed) return;
            provider = result.ServiceProvider;
        }
        identity = await Create(1, GattCharacteristicProperties.Read);
        request = await Create(2, GattCharacteristicProperties.Write);
        offer = await Create(3, GattCharacteristicProperties.Read);
        ready = await Create(4, GattCharacteristicProperties.Write);
        lock (sync)
        {
            if (disposed) return;
            identity.ReadRequested += OnIdentity;
            request.WriteRequested += OnRequest;
            offer.ReadRequested += OnOffer;
            ready.WriteRequested += OnReady;
            provider.AdvertisementStatusChanged += OnAdvertisement;
            provider.StartAdvertising(new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true });
        }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!disposed && provider.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Created && DateTime.UtcNow < deadline)
            await Task.Delay(30);
        if (disposed) throw new OperationCanceledException();
        if (provider.AdvertisementStatus != GattServiceProviderAdvertisementStatus.Started)
            throw new InvalidOperationException("Windows konnte den Bluetooth-Verbindungsdienst nicht ankündigen.");
        DiagnosticLog.Note("BLE GATT advertisement started");
    }


    // The encrypted advertisement avoids the GATT handshake on supported PCs.
    // Android verifies the paired receiver, timestamp and replay protection.
    private async Task StartBroadcast()
    {
        byte[] stamp = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (BitConverter.IsLittleEndian) Array.Reverse(stamp);
        byte[] nonce = Join(stamp, Random(8));
        byte[] packet = BroadcastPacket(hostId, deviceId, secret, nonce, bssid, ssid, password, frequencyMhz);
        var current = new BluetoothLEAdvertisementPublisher { UseExtendedAdvertisement = true };
        current.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData(0xffff, Buffer(packet)));
        lock (sync)
        {
            if (disposed) throw new OperationCanceledException();
            broadcaster = current;
            current.StatusChanged += OnBroadcastStatus;
            current.Start();
        }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!disposed && (current.Status == BluetoothLEAdvertisementPublisherStatus.Waiting ||
            current.Status == BluetoothLEAdvertisementPublisherStatus.Created) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        if (disposed) throw new OperationCanceledException();
        if (current.Status != BluetoothLEAdvertisementPublisherStatus.Started)
            throw new InvalidOperationException("Windows konnte die Bluetooth-Ankündigung nicht starten.");
        DiagnosticLog.Note("BLE encrypted advertisement started");
    }

    internal static byte[] BroadcastPacket(byte[] host, byte[] device, byte[] secret, byte[] nonce,
        byte[] bssid, string ssid, string password, int frequencyMhz = 0)
    {
        return Join(Encoding.ASCII.GetBytes("ADW1"), host, nonce, Encrypt(secret, nonce, device, bssid, ssid, password, frequencyMhz));
    }

    private void StopBroadcast()
    {
        lock (sync)
        {
            if (broadcaster == null) return;
            broadcaster.StatusChanged -= OnBroadcastStatus;
            try { broadcaster.Stop(); } catch (Exception) { }
            broadcaster = null;
        }
    }

    private async void OnBroadcastStatus(BluetoothLEAdvertisementPublisher sender, BluetoothLEAdvertisementPublisherStatusChangedEventArgs args)
    {
        if (args.Status != BluetoothLEAdvertisementPublisherStatus.Aborted) return;
        bool retry;
        lock (sync)
        {
            if (disposed || initializing || sender != broadcaster) return;
            retry = !broadcastRetried;
            broadcastRetried = true;
            initializing = retry;
        }
        if (retry)
        {
            try
            {
                DiagnosticLog.Note("BLE advertisement aborted, retrying (" + args.Error + ")");
                StopBroadcast();
                await Task.Delay(250);
                if (disposed) return;
                await StartBroadcast();
                return;
            }
            catch (Exception ex)
            {
                StopBroadcast();
                DiagnosticLog.Note("BLE advertisement retry failed: " + ex.GetType().Name);
            }
            finally { initializing = false; }
        }
        var callback = Failed;
        if (!disposed && callback != null)
            callback("Windows hat die Bluetooth-Ankündigung beendet (" + args.Error + "). Bitte erneut verbinden.");
    }

    private async Task<T> Result<T>(IAsyncOperation<T> operation)
    {
        try
        {
            var info = (IAsyncInfo)operation;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (info.Status == AsyncStatus.Started)
            {
                if (DateTime.UtcNow >= deadline) { info.Cancel(); throw new TimeoutException("Bluetooth antwortet nicht. Bitte erneut verbinden."); }
                if (disposed) { info.Cancel(); throw new OperationCanceledException(); }
                await Task.Delay(20);
            }
            return operation.GetResults();
        }
        finally { ((IAsyncInfo)operation).Close(); }
    }

    private static IBuffer Buffer(byte[] bytes)
    {
        using (var writer = new DataWriter())
        {
            writer.WriteBytes(bytes);
            return writer.DetachBuffer();
        }
    }

    private async Task<GattLocalCharacteristic> Create(int n, GattCharacteristicProperties properties)
    {
        var result = await Result(provider.Service.CreateCharacteristicAsync(Characteristic(n),
            new GattLocalCharacteristicParameters {
                CharacteristicProperties = properties,
                ReadProtectionLevel = GattProtectionLevel.Plain,
                WriteProtectionLevel = GattProtectionLevel.Plain
            }));
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException("Bluetooth-Dienst konnte nicht eingerichtet werden (" + result.Error + ").");
        return result.Characteristic;
    }

    private void OnAdvertisement(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args)
    {
        if (!initializing && args.Status == GattServiceProviderAdvertisementStatus.Aborted)
        {
            var failed = Failed;
            if (failed != null) failed("Bluetooth-Ankündigung wurde beendet (" + args.Error + ").");
        }
    }

    private async void OnIdentity(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var read = await Result(args.GetRequestAsync());
            if (read == null) return;
            lock (sync)
            {
                foreach (var stale in exchanges.Where(item => item.Value.Packet == null &&
                    DateTime.UtcNow - item.Value.Created > TimeSpan.FromSeconds(10)).Select(item => item.Key).ToArray())
                    exchanges.Remove(stale);
                if (disposed || accepted || read.Offset != 0 ||
                    (exchanges.Count >= 16 && !exchanges.ContainsKey(args.Session.DeviceId.Id)))
                { read.RespondWithProtocolError(0x0e); return; }
                var exchange = new Exchange();
                exchanges[args.Session.DeviceId.Id] = exchange;
                read.RespondWithValue(Buffer(Join(hostId, exchange.ServerNonce)));
            }
        }
        catch (Exception) { }
        finally { deferral.Complete(); }
    }

    private async void OnRequest(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var write = await Result(args.GetRequestAsync());
            if (write == null) return;
            byte[] data = Bytes(write.Value);
            lock (sync)
            {
                Exchange exchange;
                if (disposed || accepted || data.Length != 64 ||
                    !exchanges.TryGetValue(args.Session.DeviceId.Id, out exchange) ||
                    !Equal(data.Take(16).ToArray(), deviceId))
                { write.RespondWithProtocolError(0x05); return; }
                byte[] nonce = data.Skip(16).Take(16).ToArray();
                byte[] proof = Tag(secret, "request", hostId, exchange.ServerNonce, deviceId, nonce);
                if (!Equal(proof, data.Skip(32).ToArray())) { write.RespondWithProtocolError(0x05); return; }
                exchange.ClientNonce = nonce;
                exchange.Packet = Encrypt(secret, exchange.ServerNonce, nonce, bssid, ssid, password, frequencyMhz);
                write.Respond();
            }
        }
        catch (Exception) { }
        finally { deferral.Complete(); }
    }

    private async void OnOffer(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var read = await Result(args.GetRequestAsync());
            if (read == null) return;
            lock (sync)
            {
                Exchange exchange;
                if (disposed || accepted || read.Offset != 0 ||
                    !exchanges.TryGetValue(args.Session.DeviceId.Id, out exchange) || exchange.Packet == null)
                { read.RespondWithProtocolError(0x05); return; }
                read.RespondWithValue(Buffer(exchange.Packet));
            }
        }
        catch (Exception) { }
        finally { deferral.Complete(); }
    }

    private async void OnReady(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        string endpoint = null;
        try
        {
            var write = await Result(args.GetRequestAsync());
            if (write == null) return;
            byte[] data = Bytes(write.Value);
            lock (sync)
            {
                Exchange exchange;
                if (disposed || accepted || data.Length != 38 ||
                    !exchanges.TryGetValue(args.Session.DeviceId.Id, out exchange) || exchange.ClientNonce == null ||
                    !Equal(Tag(secret, "ready", exchange.ServerNonce, exchange.ClientNonce, data.Take(6).ToArray()), data.Skip(6).ToArray()))
                { write.RespondWithProtocolError(0x05); return; }
                int port = data[4] * 256 + data[5];
                var ip = new IPAddress(data.Take(4).ToArray());
                if (port != 47820 || IPAddress.IsLoopback(ip) || data[0] == 0 || data[0] >= 224)
                { write.RespondWithProtocolError(0x0d); return; }
                accepted = true;
                endpoint = ip + ":" + port;
                write.Respond();
            }
            var callback = PeerReady;
            if (callback != null) callback(endpoint);
        }
        catch (Exception) { }
        finally { deferral.Complete(); }
    }

    private static byte[] Bytes(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        return bytes;
    }

    internal static byte[] Random(int size)
    {
        var bytes = new byte[size];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return bytes;
    }

    internal static byte[] Join(params byte[][] values) { return values.SelectMany(value => value).ToArray(); }

    internal static byte[] Hmac(byte[] key, byte[] data)
    {
        using (var mac = new HMACSHA256(key)) return mac.ComputeHash(data);
    }

    internal static byte[] Key(byte[] secret, string purpose)
    {
        return Hmac(secret, Encoding.UTF8.GetBytes("AnyDisplay BLE " + purpose + " v1"));
    }

    internal static byte[] Tag(byte[] secret, string kind, params byte[][] data)
    {
        return Hmac(Key(secret, "authentication"), Join(new[] { Encoding.UTF8.GetBytes(kind) }.Concat(data).ToArray()));
    }

    internal static bool Equal(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    internal static byte[] Encrypt(byte[] secret, byte[] server, byte[] client, byte[] bssid, string ssid, string password, int frequencyMhz = 0)
    {
        byte[] name = Encoding.UTF8.GetBytes(ssid), pass = Encoding.UTF8.GetBytes(password);
        if (bssid.Length != 6 || name.Length == 0 || name.Length > 32 || pass.Length < 8 || pass.Length > 63)
            throw new InvalidDataException("Ungültiges Direkt-WLAN-Profil.");
        byte[] encrypted;
        using (var aes = Aes.Create())
        {
            aes.Key = Key(secret, "encryption");
            aes.GenerateIV();
            using (var encryptor = aes.CreateEncryptor())
            {
                byte[] plain = Join(bssid, new[] { (byte)name.Length }, name, new[] { (byte)pass.Length }, pass);
                if (frequencyMhz > 0)
                    plain = Join(plain, new[] { (byte)(frequencyMhz >> 8), (byte)frequencyMhz });
                encrypted = Join(aes.IV, encryptor.TransformFinalBlock(plain, 0, plain.Length));
            }
        }
        return Join(encrypted, Tag(secret, "offer", server, client, encrypted));
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            if (identity != null) identity.ReadRequested -= OnIdentity;
            if (request != null) request.WriteRequested -= OnRequest;
            if (offer != null) offer.ReadRequested -= OnOffer;
            if (ready != null) ready.WriteRequested -= OnReady;
            if (provider != null)
            {
                provider.AdvertisementStatusChanged -= OnAdvertisement;
                try { provider.StopAdvertising(); } catch (Exception) { }
            }
            StopBroadcast();
            exchanges.Clear();
        }
    }
}
