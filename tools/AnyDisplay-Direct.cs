using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Devices.WiFiDirect;
using Windows.Foundation;

// The native publisher belongs to this short-lived child, never to an adapter name
// persisted across sessions. EOF also releases it if the launcher exits unexpectedly.
internal sealed class DirectAccessPoint : IDisposable
{
    private readonly string executable;
    private Process process;
    private volatile WiFiDirectAdvertisementPublisherStatus state;
    private volatile string error;
    private bool stopping;
    private Task shutdown;
    private string[] previousAdapters;
    private string adapterId;
    public DirectAccessPoint(string executable) { this.executable = executable; }
    public string Error { get { return error; } }
    public bool HasExited { get {
        var owned = process;
        try { return owned == null || owned.HasExited; }
        catch (InvalidOperationException) { return true; }
    } }
    public WiFiDirectAdvertisementPublisherStatus Status {
        get { return HasExited && state != WiFiDirectAdvertisementPublisherStatus.Aborted
            ? WiFiDirectAdvertisementPublisherStatus.Stopped : state; }
    }

    private static NetworkInterface[] ActiveAdapters()
    {
        return NetworkInterface.GetAllNetworkInterfaces().Where(adapter =>
            adapter.OperationalStatus == OperationalStatus.Up &&
            adapter.Description.IndexOf("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) >= 0 &&
            adapter.GetIPProperties().UnicastAddresses.Any(local =>
                local.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !System.Net.IPAddress.IsLoopback(local.Address) &&
                !local.Address.Equals(System.Net.IPAddress.Any) &&
                !local.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))).ToArray();
    }

    // A newly activated interface wins. A reused sole interface is also valid, but
    // only while our own configured publisher is running. Ambiguous routes are refused.
    internal static string SelectAdapter(string[] before, string[] active)
    {
        var added = active.Except(before, StringComparer.OrdinalIgnoreCase).ToArray();
        return added.Length == 1 ? added[0] : active.Length == 1 ? active[0] : null;
    }

    public NetworkInterface Adapter {
        get {
            if (Status != WiFiDirectAdvertisementPublisherStatus.Started) return null;
            var active = ActiveAdapters();
            if (adapterId == null) {
                adapterId = SelectAdapter(previousAdapters, active.Select(item => item.Id).ToArray());
                if (adapterId != null) DiagnosticLog.Note("Wi-Fi Direct owned session adapter " + adapterId);
            }
            return active.FirstOrDefault(item => String.Equals(item.Id, adapterId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Start()
    {
        if (process != null) throw new InvalidOperationException("Direkt-WLAN-Sitzung wurde bereits gestartet.");
        previousAdapters = ActiveAdapters().Select(item => item.Id).ToArray();
        state = WiFiDirectAdvertisementPublisherStatus.Created;
        process = new Process { StartInfo = new ProcessStartInfo(executable, "--direct-host") {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        }};
        process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) {
            if (args.Data == null) return;
            string[] fields = args.Data.Split(' ');
            WiFiDirectAdvertisementPublisherStatus status;
            if (fields.Length == 3 && fields[0] == "AD_DIRECT" && Enum.TryParse(fields[1], out status)) {
                if (fields[2] != "Success") error = fields[2];
                state = status;
                DiagnosticLog.Note("Wi-Fi Direct status " + status + " / " + fields[2]);
            }
        };
        process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) {
            if (args.Data != null) { error = args.Data; DiagnosticLog.Note("Wi-Fi Direct helper " + args.Data); }
        };
        try {
            process.Start();
            process.StandardInput.AutoFlush = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            DiagnosticLog.Note("Wi-Fi Direct owned helper started PID " + process.Id);
        } catch { process.Dispose(); process = null; throw; }
    }

    public void Stop()
    {
        if (stopping || process == null) return;
        stopping = true;
        var owned = process;
        DiagnosticLog.Note("Stopping owned Wi-Fi Direct helper PID " + owned.Id);
        try { if (!owned.HasExited) owned.StandardInput.WriteLine("stop"); }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        shutdown = Task.Run(() => {
            try {
                if (!owned.WaitForExit(3000)) {
                    DiagnosticLog.Note("Owned Wi-Fi Direct helper required forced exit");
                    owned.Kill();
                    owned.WaitForExit();
                }
                DiagnosticLog.Note("Owned Wi-Fi Direct helper exited");
            } catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception e) { DiagnosticLog.Note("Wi-Fi Direct shutdown: " + e.NativeErrorCode); }
        });
    }

    public void Dispose()
    {
        Stop();
        if (shutdown != null) shutdown.Wait(5000);
        if (HasExited && process != null) { process.Dispose(); process = null; }
    }

    internal static int RunHelper()
    {
        using (var host = new NativeHost()) return host.Run();
    }

    private sealed class NativeHost : IDisposable
    {
        private readonly Control dispatcher = new Control();
        private readonly List<WiFiDirectDevice> devices = new List<WiFiDirectDevice>();
        private WiFiDirectAdvertisementPublisher publisher;
        private WiFiDirectConnectionListener listener;
        private volatile bool stopping;

        public int Run()
        {
            // The named lease serializes only AnyDisplay helpers; it never stops
            // another application's hotspot or manipulates network adapters.
            using (var lease = new System.Threading.Mutex(false, @"Local\AnyDisplay.Windows.DirectHost")) {
                bool owned;
                try { owned = lease.WaitOne(6000); }
                catch (System.Threading.AbandonedMutexException) { owned = true; }
                if (!owned) { Console.Out.WriteLine("AD_DIRECT Aborted ResourceInUse"); return 1; }
                try {
                    var handle = dispatcher.Handle;
                    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnyDisplay", "direct-profile.dat");
                    var profile = DirectProfile.LoadOrCreate(path);
                    publisher = new WiFiDirectAdvertisementPublisher();
                    publisher.StatusChanged += OnStatus;
                    publisher.Advertisement.IsAutonomousGroupOwnerEnabled = true;
                    profile.Configure(publisher.Advertisement.LegacySettings);
                    // A stopped/closed parent pipe is sufficient; no credentials
                    // or WLAN password pass through process arguments or stdout.
                    Task.Run(() => {
                        try { while (true) { var line = Console.ReadLine(); if (line == null || line == "stop") break; } }
                        catch (IOException) { }
                        RequestStop();
                    });
                    publisher.Start();
                    Application.Run();
                    return 0;
                } catch (Exception e) {
                    Console.Error.WriteLine(e.GetType().Name);
                    Console.Out.WriteLine("AD_DIRECT Aborted OtherError");
                    return 1;
                } finally { Dispose(); lease.ReleaseMutex(); }
            }
        }

        private void OnStatus(WiFiDirectAdvertisementPublisher sender, WiFiDirectAdvertisementPublisherStatusChangedEventArgs args)
        {
            Console.Out.WriteLine("AD_DIRECT " + args.Status + " " + args.Error);
            // Use the helper's UI thread for all native-resource lifetime changes.
            try { dispatcher.BeginInvoke((Action)delegate {
                if (stopping) return;
                try {
                    if (args.Status == WiFiDirectAdvertisementPublisherStatus.Started && listener == null) {
                        listener = new WiFiDirectConnectionListener();
                        listener.ConnectionRequested += OnConnection;
                    }
                } catch (Exception e) { Console.Error.WriteLine("Listener: " + e.GetType().Name); RequestStop(); }
                if (args.Status == WiFiDirectAdvertisementPublisherStatus.Aborted) RequestStop();
            }); } catch (InvalidOperationException) { }
        }

        private void RequestStop()
        {
            try { dispatcher.BeginInvoke((Action)delegate { Dispose(); Application.ExitThread(); }); }
            catch (InvalidOperationException) { }
        }

        private async void OnConnection(WiFiDirectConnectionListener sender, WiFiDirectConnectionRequestedEventArgs args)
        {
            if (stopping) return;
            try {
                using (var request = args.GetConnectionRequest()) {
                    var operation = WiFiDirectDevice.FromIdAsync(request.DeviceInformation.Id);
                    var info = (IAsyncInfo)operation;
                    try {
                        var deadline = DateTime.UtcNow.AddSeconds(8);
                        while (info.Status == AsyncStatus.Started) {
                            if (stopping || DateTime.UtcNow >= deadline) { info.Cancel(); return; }
                            await Task.Delay(30);
                        }
                        var device = operation.GetResults();
                        if (device != null) lock (devices) { if (stopping) device.Dispose(); else devices.Add(device); }
                    } finally { if (info.Status != AsyncStatus.Started) info.Close(); }
                }
            } catch (Exception e) { Console.Error.WriteLine("Peer: " + e.GetType().Name); }
        }

        public void Dispose()
        {
            if (stopping) return;
            stopping = true;
            if (listener != null) { listener.ConnectionRequested -= OnConnection; listener = null; }
            lock (devices) {
                foreach (var device in devices) try { device.Dispose(); } catch (Exception) { }
                devices.Clear();
            }
            if (publisher != null) {
                publisher.StatusChanged -= OnStatus;
                try { publisher.Stop(); } catch (Exception) { }
                publisher = null;
            }
            dispatcher.Dispose();
        }
    }
}
