using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text;
using System.Security.Cryptography;
using System.Windows.Forms;
using System.Threading.Tasks;
using EventWaitHandle = System.Threading.EventWaitHandle;
using Mutex = System.Threading.Mutex;
using Windows.Devices.WiFiDirect;
using Windows.Security.Credentials;

// Increment the final component for each numbered Windows package.
[assembly: System.Reflection.AssemblyFileVersion("0.21.0.21")]

internal sealed class DirectProfile
{
    private const string SsidPrefix = "DIRECT-AnyDisplay-";
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("AnyDisplay Wi-Fi Direct profile v1");
    public readonly string Ssid;
    public readonly string Password;

    private DirectProfile(string ssid, string password)
    {
        Ssid = ssid;
        Password = password;
    }

    public static DirectProfile LoadOrCreate(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        if (!File.Exists(path))
        {
            var bytes = new byte[10];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string ssid = SsidPrefix + Guid.NewGuid().ToString("N").Substring(0, 8);
            string password = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            byte[] protectedProfile = ProtectedData.Protect(Encoding.UTF8.GetBytes(ssid + "\n" + password),
                Purpose, DataProtectionScope.CurrentUser);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(protectedProfile, 0, protectedProfile.Length);
                try { File.Move(temp, path); }
                catch (IOException) { if (!File.Exists(path)) throw; }
            }
            finally { File.Delete(temp); }
        }
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Purpose, DataProtectionScope.CurrentUser);
        var parts = Encoding.UTF8.GetString(plain).Split('\n');
        if (parts.Length != 2 || !Regex.IsMatch(parts[0], "^DIRECT-AnyDisplay-[0-9a-f]{8}$") ||
            !Regex.IsMatch(parts[1], "^[0-9a-f]{20}$"))
            throw new InvalidDataException("Das gespeicherte Direkt-WLAN-Profil hat ein ungültiges Format.");
        return new DirectProfile(parts[0], parts[1]);
    }

    public void Configure(WiFiDirectLegacySettings settings)
    {
        settings.Ssid = Ssid;
        settings.Passphrase = new PasswordCredential("AnyDisplay", "WiFiDirect", Password);
        settings.IsEnabled = true;
    }
}

internal sealed class Launcher : Form
{
    private const string InstanceName = @"Local\AnyDisplay.Windows.Launcher";
    private const string RestartEventName = InstanceName + ".Restart";
    private static readonly string BuildLabel = ReadBuildLabel();

    private static string ReadBuildLabel()
    {
        var assembly = typeof(Launcher).Assembly;
        var version = FileVersionInfo.GetVersionInfo(assembly.Location);
        using (var reader = new StreamReader(assembly.GetManifestResourceStream("AnyDisplay.BuildTime")))
        {
            string stamp = reader.ReadToEnd().Trim();
            if (!Regex.IsMatch(stamp, @"^[0-9]{8}-[0-9]{4}$"))
                throw new InvalidDataException("Ungültiger AnyDisplay-Build-Zeitstempel.");
            return String.Format("Build: V{0}.{1}.{2}_{3}",
                version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, stamp);
        }
    }
    private readonly ComboBox transport, codec, fps, display, scale, bitrate;
    private readonly NumericUpDown manualBitrate = new NumericUpDown();
    private readonly Label bitrateUnit;
    private readonly Button findReceivers = new Button();
    private bool networkSearching;
    private bool manualNetworkAddress;
    private int networkSearchGeneration;
    private BleDirect ble;
    private PairedReceiver bleReceiver;
    private byte[] bleHostId;
    private bool bleAttempted;
    private string bleMessage;
    private volatile string bleEndpoint;
    private readonly TextBox address = new TextBox();
    private readonly TextBox detail = new TextBox();
    private readonly Label addressLabel;
    private readonly Button connect = new Button();
    private readonly Button reconnect = new Button();
    private readonly LinkLabel hotspot = new LinkLabel();
    private readonly LinkLabel networkRoutes = new LinkLabel();
    private NetworkReceiver selectedReceiver;
    private readonly Timer tick = new Timer { Interval = 200 };
    private readonly string exe;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem disconnect;
    private bool quitting;
    private DirectAccessPoint publisher;
    private DirectAccessPoint retiringPublisher;
    private DateTime retireUntil;
    private DirectProfile directProfile;
    private Process child;
    private string hostTarget;
    private int activeTransport = -1;
    private string networkAddress = "";
    private string directVideoArguments;
    private string pendingVideoArguments;
    private string pendingNetworkTarget;
    private string lastHostError;
    private string lastVideoArguments;
    private DateTime videoApplyAfter;
    private DateTime startupDeadline;
    private bool usingHotspot;
    private DateTime? searchDeadline;
    private Task<string> discovery;
    private Task<string> directDiscovery;
    private System.Threading.CancellationTokenSource directDiscoveryCancellation;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint address, out uint index);


    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 1 && args[0] == "--direct-host") return DirectAccessPoint.RunHelper();
        string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "anydisplay-win.exe");
        if (args.Length == 1 && args[0] == "--smoke-test")
        {
            try { using (var form = new Launcher(exe)) form.SmokeTest(); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
        using (var instance = new Mutex(false, InstanceName))
        {
            bool ownsInstance = WaitForInstance(instance, 0);
            try
            {
                if (!ownsInstance || HasOtherLauncher())
                {
                    EventWaitHandle signal;
                    if (ownsInstance || !EventWaitHandle.TryOpenExisting(RestartEventName, out signal))
                    {
                        MessageBox.Show("Eine ältere AnyDisplay-Version läuft bereits. Wähle einmal „Beenden“ im Tray-Menü und öffne diese EXE erneut.", "AnyDisplay");
                        return 0;
                    }
                    using (signal)
                    {
                        if (!ConfirmRestart()) return 0;
                        signal.Set();
                    }
                    ownsInstance = WaitForInstance(instance, 10000);
                    if (!ownsInstance)
                    {
                        MessageBox.Show("Die laufende Version wurde noch nicht beendet. Bitte erneut versuchen.", "AnyDisplay");
                        return 0;
                    }
                }
                using (var form = new Launcher(exe))
                using (var signal = new EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, RestartEventName))
                {
                    // Create the handle before accepting cross-process restart requests.
                    var handle = form.Handle;
                    var wait = System.Threading.ThreadPool.RegisterWaitForSingleObject(signal, delegate
                    {
                        try { form.BeginInvoke((Action)form.ExitLauncher); }
                        catch (InvalidOperationException) { }
                    }, null, System.Threading.Timeout.Infinite, false);
                    try { Application.Run(form); }
                    finally { wait.Unregister(null); }
                }
            }
            finally { if (ownsInstance) instance.ReleaseMutex(); }
        }
        return 0;
    }

    private static bool WaitForInstance(Mutex instance, int timeoutMs)
    {
        try { return instance.WaitOne(timeoutMs); }
        catch (System.Threading.AbandonedMutexException) { return true; }
    }

    private static bool ConfirmRestart()
    {
        using (var dialog = new Form
        {
            Text = "AnyDisplay läuft bereits",
            ClientSize = new Size(460, 130),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false
        })
        using (var message = new Label
        {
            Text = "Die laufende Version wird beendet und diese Version geöffnet. Ein aktiver Stream wird dabei getrennt."
        })
        using (var restart = new Button { Text = "Neu starten", DialogResult = DialogResult.OK })
        using (var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel })
        {
            message.SetBounds(16, 16, 428, 55);
            restart.SetBounds(212, 84, 112, 32);
            cancel.SetBounds(332, 84, 112, 32);
            dialog.Controls.AddRange(new Control[] { message, restart, cancel });
            dialog.AcceptButton = restart;
            dialog.CancelButton = cancel;
            return dialog.ShowDialog() == DialogResult.OK;
        }
    }

    private static bool HasOtherLauncher()
    {
        using (var current = Process.GetCurrentProcess())
        {
            foreach (var candidate in Process.GetProcessesByName(current.ProcessName))
            {
                using (candidate)
                {
                    try
                    {
                        if (candidate.Id != current.Id && candidate.SessionId == current.SessionId &&
                            !candidate.HasExited && candidate.MainModule.FileVersionInfo.FilePrivatePart < 11) return true;
                    }
                    catch (InvalidOperationException) { } // A launcher may exit during enumeration.
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
        }
        return false;
    }

    private Launcher(string exe)
    {
        this.exe = exe;
        Text = "AnyDisplay – Windows";
        ClientSize = new Size(550, 444);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 10);
        transport = Choice("Verbindung", 15, "USB", "Direkt-WLAN", "Netzwerk");
        codec = Choice("Codec", 49, "H.264", "HEVC");
        fps = Choice("Bildrate", 83, "60 FPS", "30 FPS", "15 FPS", "Variabel");
        display = Choice("Bildschirm", 117, "Gespiegelt", "Erweitert");
        scale = Choice("Auflösung", 151, "Voll", "Halb", "Viertel", "¾");
        bitrate = Choice("Bitrate", 185, "Auto", "10 Mbit/s", "4 Mbit/s", "Manuell");
        bitrate.SetBounds(165, 185, 174, 29);
        manualBitrate.SetBounds(347, 185, 97, 27);
        manualBitrate.DecimalPlaces = 1;
        manualBitrate.Increment = 0.1m;
        manualBitrate.Minimum = 0.1m;
        manualBitrate.Maximum = 80m;
        manualBitrate.Value = 10m;
        Controls.Add(manualBitrate);
        bitrateUnit = Caption("Mbit/s", 452, 188, 73);
        bitrate.SelectedIndexChanged += delegate { UpdateBitrate(); ScheduleVideoSettings(); };
        foreach (var option in new[] { codec, fps, display, scale })
            option.SelectedIndexChanged += delegate { ScheduleVideoSettings(); };
        manualBitrate.ValueChanged += delegate { ScheduleVideoSettings(); };
        manualBitrate.TextChanged += delegate { ScheduleVideoSettings(); };
        addressLabel = Caption("Tablet-Adresse", 18, 228, 145);
        address.SetBounds(165, 224, 360, 27);
        address.TextChanged += delegate
        {
            if (transport.SelectedIndex == 2 && !address.ReadOnly)
            {
                networkAddress = address.Text;
                if (address.Focused) { manualNetworkAddress = true; selectedReceiver = null; UpdateNetworkRoutes(); }
                UpdateReconnect();
            }
        };
        Controls.Add(address);
        findReceivers.Text = "Suchen";
        findReceivers.SetBounds(445, 224, 80, 29);
        findReceivers.Click += delegate { BeginNetworkSearch(false); };
        Controls.Add(findReceivers);
        hotspot.Text = "Optional: Windows-Hotspot mit Internet";
        hotspot.SetBounds(165, 254, 360, 24);
        hotspot.LinkClicked += delegate { SelectHotspot(); };
        Controls.Add(hotspot);
        networkRoutes.SetBounds(165, 254, 360, 24);
        networkRoutes.LinkClicked += delegate { ChooseNetworkRoute(); };
        Controls.Add(networkRoutes);
        detail.Multiline = true;
        detail.ReadOnly = true;
        detail.ScrollBars = ScrollBars.Vertical;
        detail.SetBounds(18, 282, 507, 78);
        Controls.Add(detail);
        connect.SetBounds(158, 372, 172, 32);
        connect.Click += delegate { Connect(); };
        Controls.Add(connect);
        reconnect.Text = "Verbindung neu aufbauen";
        reconnect.SetBounds(338, 372, 187, 32);
        reconnect.Enabled = false;
        reconnect.Click += delegate { RequestDisconnect(true); };
        Controls.Add(reconnect);
        AcceptButton = connect;
        var buildLabel = Caption(BuildLabel, 230, 414, 295);
        buildLabel.TextAlign = ContentAlignment.MiddleRight;
        buildLabel.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        transport.SelectedIndexChanged += delegate { UpdateTransport(); };
        tick.Tick += delegate { Poll(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripLabel("AnyDisplay · " + BuildLabel));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("AnyDisplay öffnen", null, delegate { ShowSettings(); });
        disconnect = new ToolStripMenuItem("Verbindung trennen") { Enabled = false };
        disconnect.Click += delegate { Disconnect(); };
        menu.Items.Add(disconnect);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, delegate { ExitLauncher(); });
        tray = new NotifyIcon { Icon = MakeTrayIcon(), Text = "AnyDisplay", ContextMenuStrip = menu };
        tray.DoubleClick += delegate { ShowSettings(); };
        Shown += delegate { tray.Visible = true; };
        Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !quitting)
            {
                e.Cancel = true;
                Hide();
            }
        };
        FormClosed += delegate
        {
            tray.Visible = false;
            tray.Icon.Dispose();
            tray.Dispose();
            menu.Dispose();
            tick.Stop();
            tick.Dispose();
            EndDirect();
            if (retiringPublisher != null) { retiringPublisher.Dispose(); retiringPublisher = null; }
        };
        UpdateTransport();
    }

    private Label Caption(string text, int x, int y, int width)
    {
        var label = new Label { Text = text };
        label.SetBounds(x, y, width, 24);
        Controls.Add(label);
        return label;
    }

    private ComboBox Choice(string text, int y, params string[] entries)
    {
        Caption(text, 18, y + 3, 142);
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        box.SetBounds(165, y, 360, 29);
        box.Items.AddRange(entries);
        box.SelectedIndex = 0;
        Controls.Add(box);
        return box;
    }

    private void UpdateTransport()
    {
        if (networkSearching) CancelNetworkSearch();
        if (child != null)
        {
            UpdateAddressField();
            UpdateReconnect();
            return;
        }
        EndDirect();
        bool network = transport.SelectedIndex == 2;
        codec.SelectedIndex = fps.SelectedIndex = scale.SelectedIndex = network ? 1 : 0;
        bitrate.SelectedIndex = network ? 2 : 0;
        UpdateBitrate();
        UpdateAddressField();
        hotspot.Visible = transport.SelectedIndex == 1;
        connect.Text = "Verbinden";
        detail.Text = new[] {
            "Tablet per USB anschließen und AnyDisplay öffnen. Dieser Build verwendet USB-Debugging.",
            "Windows öffnet ein eigenes WLAN für das Tablet. Ein Router und Internet sind dafür nicht nötig.",
            "PC und Tablet im selben Netzwerk. Standard: 30 FPS, halbe Auflösung und 4 Mbit/s."
        }[transport.SelectedIndex];
    }

    private void UpdateAddressField()
    {
        bool network = transport.SelectedIndex == 2;
        bool direct = transport.SelectedIndex == 1 && activeTransport == 1 && child != null;
        address.ReadOnly = true;
        findReceivers.Visible = network;
        address.Width = network ? 272 : 360;
        address.Visible = addressLabel.Visible = network || direct;
        if (network) address.Text = networkAddress;
        else if (direct) address.Text = hostTarget.Substring("connect ".Length);
        address.ReadOnly = !network;
        UpdateNetworkRoutes();
    }

    private string NetworkTarget()
    {
        string peer = networkAddress.Trim();
        var match = Regex.Match(peer, @"^(?:[A-Za-z0-9][A-Za-z0-9.-]*|\[[0-9A-Fa-f:.]+\])(?::([0-9]{1,5}))?$");
        ushort port;
        if (!match.Success || (match.Groups[1].Success &&
            (!ushort.TryParse(match.Groups[1].Value, out port) || port == 0)))
            throw new InvalidOperationException("Adresse aus AnyDisplay eingeben, zum Beispiel 192.168.1.25.");
        return "connect " + peer;
    }

    private void UpdateBitrate()
    {
        manualBitrate.Visible = bitrateUnit.Visible = bitrate.SelectedIndex == 3;
    }

    private void ScheduleVideoSettings()
    {
        videoApplyAfter = DateTime.UtcNow.AddMilliseconds(400);
    }

    private bool TryManualBitrate(out decimal value)
    {
        return decimal.TryParse(manualBitrate.Text, out value) &&
            value >= manualBitrate.Minimum && value <= manualBitrate.Maximum &&
            value * 10m == decimal.Truncate(value * 10m);
    }

    private bool HasConnectionChange()
    {
        return transport.SelectedIndex != activeTransport ||
            (activeTransport == 2 && !String.Equals(hostTarget, "connect " + networkAddress.Trim(),
                StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateReconnect()
    {
        reconnect.Enabled = child != null && connect.Enabled && HasConnectionChange();
    }

    private void ApplyVideoSettings()
    {
        decimal manual;
        if (quitting || !connect.Enabled || DateTime.UtcNow < videoApplyAfter ||
            (bitrate.SelectedIndex == 3 && !TryManualBitrate(out manual))) return;
        string arguments = VideoArguments();
        if (arguments == lastVideoArguments) return;
        try
        {
            child.StandardInput.WriteLine("video " + arguments);
            lastVideoArguments = arguments;
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    private string VideoArguments()
    {
        decimal manual;
        if (!TryManualBitrate(out manual)) manual = manualBitrate.Value;
        string kbps = bitrate.SelectedIndex == 3
            ? ((int)(manual * 1000m)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : new[] { "auto", "10000", "4000" }[bitrate.SelectedIndex];
        return String.Format("--codec {0} --fps {1} --display {2} --scale {3} --bitrate {4}",
            new[] { "h264", "hevc" }[codec.SelectedIndex],
            new[] { "60", "30", "15", "variable" }[fps.SelectedIndex],
            new[] { "mirror", "extend" }[display.SelectedIndex],
            new[] { "full", "half", "quarter", "three-quarter" }[scale.SelectedIndex], kbps);
    }

    private void Connect()
    {
        try
        {
            if (networkSearching && child == null) { CancelNetworkSearch(); return; }
            if (child != null) { Disconnect(); return; }
            if (searchDeadline.HasValue) { FinishDirectSearch("Suche abgebrochen."); return; }
            if (!File.Exists(exe)) throw new InvalidOperationException("anydisplay-win.exe fehlt neben diesem Programm.");
            if (transport.SelectedIndex == 0) { StartHost("usb"); return; }
            string peer = address.Text.Trim();
            if (transport.SelectedIndex == 2)
            {
                if (manualNetworkAddress && !String.IsNullOrWhiteSpace(networkAddress)) StartHost(NetworkTarget());
                else BeginNetworkSearch(true);
                return;
            }
            if (!usingHotspot) { StartDirect(); return; }
            if (peer.EndsWith(":47820", StringComparison.Ordinal)) peer = peer.Substring(0, peer.Length - 6);
            IPAddress ip;
            if (!IPAddress.TryParse(peer, out ip) || !IsDirectPeer(ip))
                throw new InvalidOperationException("Diese Adresse führt nicht über den Direktadapter. Bitte WLAN und Tablet-Adresse prüfen.");
            StartHost("connect " + ip + ":47820");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "AnyDisplay"); }
    }

    private bool IsDirectPeer(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var owner = publisher;
        var ownedAdapter = owner == null ? null : owner.Adapter;
        if (!usingHotspot && ownedAdapter == null) return false;
        uint index;
        if (GetBestInterface(BitConverter.ToUInt32(address.GetAddressBytes(), 0), out index) != 0) return false;
        return NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.Description.IndexOf("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) < 0 ||
                (!usingHotspot && adapter.Id != ownedAdapter.Id)) return false;
            var ip = adapter.GetIPProperties();
            var ipv4 = ip.GetIPv4Properties();
            if (ipv4 == null || ipv4.Index != index) return false;
            var target = address.GetAddressBytes();
            return ip.UnicastAddresses.Any(local =>
            {
                if (local.Address.AddressFamily != AddressFamily.InterNetwork || local.Address.Equals(address)) return false;
                var mask = local.IPv4Mask.GetAddressBytes();
                var source = local.Address.GetAddressBytes();
                for (int i = 0; i < 4; ++i)
                    if ((target[i] & mask[i]) != (source[i] & mask[i])) return false;
                return true;
            });
        });
    }

    private void StartDirect(string videoArguments = null)
    {
        if (!PrepareBluetooth()) { FinishConnection("Bluetooth-Auswahl abgebrochen."); return; }
        EndDirect();
        directVideoArguments = videoArguments;
        AcceptButton = connect;
        if (retiringPublisher != null)
        {
            startupDeadline = DateTime.UtcNow.AddSeconds(10);
            searchDeadline = startupDeadline;
            SetConnectionSettingsEnabled(false);
            connect.Text = "Abbrechen";
            connect.Enabled = disconnect.Enabled = true;
            detail.Text = "Windows gibt das vorige Direkt-WLAN frei …";
            tick.Start();
            return;
        }
        OpenDirectPublisher();
    }

    private const string DirectAdapterBusy =
        "Windows meldet Wi-Fi Direct als belegt. Eine andere Direktverbindung, Drahtlosprojektion oder ein mobiler Hotspot kann die Ressource verwenden.";

    private void OpenDirectPublisher()
    {
        // An Up adapter is not evidence of another owner: Windows may retain or
        // reuse our previous virtual interface. Only our native helper owns this session.
        if (retiringPublisher != null) {
            if (!retiringPublisher.HasExited) throw new InvalidOperationException("Das vorherige AnyDisplay-Direkt-WLAN wird noch beendet. Bitte erneut verbinden.");
            retiringPublisher.Dispose();
            retiringPublisher = null;
        }
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnyDisplay", "direct-profile.dat");
        directProfile = DirectProfile.LoadOrCreate(file);
        publisher = new DirectAccessPoint(Application.ExecutablePath);
        try { publisher.Start(); }
        catch { EndDirect(); throw; }
        startupDeadline = DateTime.UtcNow.AddSeconds(15);
        SetConnectionSettingsEnabled(false);
        connect.Text = "Abbrechen";
        connect.Enabled = disconnect.Enabled = true;
        searchDeadline = startupDeadline;
        detail.Text = "Direkt-WLAN wird geöffnet …";
        tick.Start();
    }

    private void SelectHotspot()
    {
        try
        {
            EndDirect();
            Process.Start("ms-settings:network-mobilehotspot");
            usingHotspot = true;
            PromptForAddress("Aktiviere den mobilen Hotspot in Windows und verbinde das Tablet mit ihm. Öffne dann AnyDisplay auf dem Tablet.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "AnyDisplay"); }
    }

    private void PromptForAddress(string instructions)
    {
        detail.Text = instructions;
        address.Visible = addressLabel.Visible = true;
        connect.Text = "Verbinden";
        connect.Enabled = true;
        address.ReadOnly = false;
        address.Clear();
        address.Focus();
    }

    private void StopDirectDiscovery()
    {
        if (directDiscoveryCancellation != null)
        {
            directDiscoveryCancellation.Cancel();
            directDiscoveryCancellation.Dispose();
            directDiscoveryCancellation = null;
        }
        directDiscovery = null;
    }

    private void EndDirect()
    {
        StopDirectDiscovery();
        bleEndpoint = null;
        bleAttempted = false;
        if (ble != null) { ble.Dispose(); ble = null; }
        if (publisher != null)
        {
            // Hold the exact owned process until it has exited. Never disable a
            // virtual interface or stop another app's network by name or IP.
            publisher.Stop();
            retiringPublisher = publisher;
            retireUntil = DateTime.UtcNow.AddMilliseconds(750);
            publisher = null;
        }
        usingHotspot = false;
        searchDeadline = null;
        discovery = null;
        directVideoArguments = null;
    }

    private static Icon MakeTrayIcon()
    {
        using (var stream = typeof(Launcher).Assembly.GetManifestResourceStream("AnyDisplay.ico"))
        {
            if (stream == null) throw new InvalidOperationException("AnyDisplay icon resource is missing.");
            using (var icon = new Icon(stream, SystemInformation.SmallIconSize))
                return (Icon)icon.Clone();
        }
    }

    private void ShowSettings()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void SetConnectionSettingsEnabled(bool enabled)
    {
        transport.Enabled = hotspot.Enabled = address.Enabled = findReceivers.Enabled = networkRoutes.Enabled = enabled;
    }

    private void ExitLauncher()
    {
        networkSearchGeneration++;
        if (child != null && !child.HasExited)
        {
            if (StopHost()) quitting = true;
            return;
        }
        quitting = true;
        Close();
    }

    private void Disconnect()
    {
        RequestDisconnect(false);
    }

    private void RequestDisconnect(bool restart)
    {
        if (restart && (child == null || !HasConnectionChange())) return;
        string networkTarget = null;
        if (restart && transport.SelectedIndex == 2)
        {
            if (String.IsNullOrWhiteSpace(networkAddress)) { BeginNetworkSearch(true); return; }
            try { networkTarget = NetworkTarget(); }
            catch (InvalidOperationException ex) { detail.Text = ex.Message; return; }
        }
        if (!StopHost())
        {
            detail.Text = "Die Verbindung konnte nicht getrennt werden. Bitte erneut versuchen.";
            return;
        }
        if (child == null) return;
        pendingNetworkTarget = networkTarget;
        pendingVideoArguments = restart ? VideoArguments() : null;
        SetConnectionSettingsEnabled(false);
        connect.Enabled = disconnect.Enabled = reconnect.Enabled = false;
        detail.Text = restart ? "Verbindung wird neu aufgebaut …" : "Verbindung wird getrennt …";
    }

    private bool StopHost()
    {
        if (child == null || child.HasExited)
        {
            if (searchDeadline.HasValue) FinishDirectSearch("Suche abgebrochen.");
            return true;
        }
        try { child.StandardInput.WriteLine("stop"); return true; }
        catch (IOException) { return child.HasExited; }
        catch (InvalidOperationException) { return child.HasExited; }
    }

    private void StartHost(string target, string videoArguments = null)
    {
        lastHostError = null;
        string arguments = videoArguments ?? VideoArguments();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, target + " " + arguments + " --gui")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
        process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate { OnHostLine(process, e.Data); }); }
            catch (InvalidOperationException) { }
        };
        try
        {
            process.Start();
            child = process;
            process.StandardInput.AutoFlush = true;
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
        }
        catch
        {
            process.Dispose();
            child = null;
            throw;
        }
        StopDirectDiscovery();
        detail.Text = "Verbindung wird aufgebaut …";
        hostTarget = target;
        activeTransport = transport.SelectedIndex;
        if (ble != null) { ble.Dispose(); ble = null; }
        SetConnectionSettingsEnabled(false);
        transport.Enabled = address.Enabled = networkRoutes.Enabled = true;
        UpdateAddressField();
        AcceptButton = null;
        connect.Text = "Verbindung trennen";
        connect.Enabled = disconnect.Enabled = true;
        lastVideoArguments = arguments;
        UpdateReconnect();
        tray.Text = "AnyDisplay – Verbindung läuft";
        tick.Start();
    }

    private void OnHostLine(Process process, string line)
    {
        if (child != process) return;
        var pin = MatchPin(line);
        if (pin.Success && !quitting)
        {
            ShowSettings();
            string code = pin.Groups[1].Value;
            bool confirmed = ConfirmPairing(code);
            if (child != process || process.HasExited) return;
            try { process.StandardInput.WriteLine(confirmed ? "confirm " + code : "reject"); }
            catch (IOException) { }
            catch (InvalidOperationException) { }
        }
        else if (line.Contains(" WARN  video settings rejected:"))
        {
            detail.Text = "Bildänderung konnte nicht übernommen werden. Die Verbindung bleibt bestehen." +
                Environment.NewLine + line;
        }
        else if (line.Contains(" WARN "))
        {
            lastHostError = line;
        }
        else if (line.Contains(" CONFIG stream="))
        {
            if (activeTransport == 2) ReceiverDiscovery.Remember(hostTarget.Substring("connect ".Length));
            detail.Text = "Verbunden. Bildänderungen werden direkt übernommen." + Environment.NewLine + line;
        }
    }

    private static Match MatchPin(string line)
    {
        return Regex.Match(line, @"^ANYDISPLAY_PIN ([0-9]{6})$");
    }

    private bool ConfirmPairing(string code)
    {
        using (var dialog = new Form
        {
            Text = "AnyDisplay koppeln",
            ClientSize = new Size(510, 181),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            Font = Font
        })
        using (var codeFont = new Font(Font.FontFamily, 16, FontStyle.Bold))
        using (var codeLabel = new Label())
        using (var instructions = new Label())
        using (var confirm = new Button())
        using (var cancel = new Button())
        {
            codeLabel.Text = "Windows-PIN: " + code;
            codeLabel.Font = codeFont;
            codeLabel.SetBounds(14, 12, 482, 36);
            instructions.Text = "Vergleiche den Code mit dem auf dem Tablet." + Environment.NewLine +
                "Wenn beide gleich sind, bestätige auf beiden Geräten.";
            instructions.SetBounds(14, 53, 482, 66);
            confirm.Text = "Codes stimmen überein";
            confirm.SetBounds(164, 131, 216, 36);
            confirm.DialogResult = DialogResult.OK;
            cancel.Text = "Abbrechen";
            cancel.SetBounds(390, 131, 106, 36);
            cancel.DialogResult = DialogResult.Cancel;
            dialog.Controls.AddRange(new Control[] { codeLabel, instructions, confirm, cancel });
            dialog.CancelButton = cancel;
            dialog.ActiveControl = cancel;
            return dialog.ShowDialog(this) == DialogResult.OK;
        }
    }

    private void Poll()
    {
        if (child != null)
        {
            if (!child.HasExited)
            {
                ApplyVideoSettings();
                return;
            }
            int code = child.ExitCode;
            child.Dispose();
            child = null;
            if (pendingVideoArguments != null && !quitting)
            {
                // Let Android release the previous session before opening another socket.
                tick.Interval = 500;
                return;
            }
            FinishConnection(code == 0 ? "Verbindung beendet." :
                (lastHostError ?? ("Verbindung beendet (Code " + code + "). Prüfe Tablet, Kopplung und Monitor.")));
            return;
        }
        if (pendingVideoArguments != null)
        {
            RestartHost();
            return;
        }
        if (!searchDeadline.HasValue) { tick.Stop(); return; }
        try { PollDirectSearch(); }
        catch (Exception ex) { FinishDirectSearch(ex.Message); }
    }

    private void RestartHost()
    {
        string arguments = pendingVideoArguments;
        string networkTarget = pendingNetworkTarget;
        pendingVideoArguments = pendingNetworkTarget = null;
        tick.Interval = 200;
        try
        {
            if (transport.SelectedIndex != activeTransport)
            {
                if (transport.SelectedIndex == 1)
                {
                    StartDirect(arguments);
                    return;
                }
                EndDirect();
            }
            if (transport.SelectedIndex == 1)
            {
                IPAddress ip;
                var peer = hostTarget.Substring("connect ".Length).Split(':')[0];
                if ((!usingHotspot && (publisher == null ||
                    publisher.Status != WiFiDirectAdvertisementPublisherStatus.Started)) ||
                    !IPAddress.TryParse(peer, out ip) || !IsDirectPeer(ip))
                    throw new InvalidOperationException("Das Tablet ist über das Direkt-WLAN nicht mehr erreichbar. Bitte erneut verbinden.");
            }
            string target = transport.SelectedIndex == 0 ? "usb" : (networkTarget ?? hostTarget);
            StartHost(target, arguments);
        }
        catch (Exception ex) { FinishConnection(ex.Message); }
    }

    private void FinishConnection(string message)
    {
        CancelNetworkSearch();
        tick.Stop();
        SetConnectionSettingsEnabled(true);
        AcceptButton = connect;
        connect.Enabled = true;
        disconnect.Enabled = reconnect.Enabled = false;
        tray.Text = "AnyDisplay";
        EndDirect();
        activeTransport = -1;
        UpdateAddressField();
        connect.Text = "Verbinden";
        detail.Text = message;
        if (quitting) Close();
    }

    private void PollDirectSearch()
    {
        if (publisher == null)
        {
            if (retiringPublisher != null && retiringPublisher.HasExited &&
                DateTime.UtcNow >= retireUntil)
                OpenDirectPublisher();
            else if (DateTime.UtcNow >= startupDeadline)
                FinishDirectSearch("Das vorherige AnyDisplay-Direkt-WLAN wurde noch nicht freigegeben. Bitte erneut verbinden.");
            return;
        }
        var status = publisher.Status;
        if (status == WiFiDirectAdvertisementPublisherStatus.Aborted ||
            (discovery == null && status != WiFiDirectAdvertisementPublisherStatus.Started &&
             DateTime.UtcNow >= startupDeadline))
        {
            string reason = publisher.Error ?? status.ToString();
            FinishDirectSearch(reason == "ResourceInUse"
                ? DirectAdapterBusy
                : "Direkt-WLAN konnte nicht geöffnet werden (" + reason + "). Mobilen Hotspot und Drahtlosprojektion prüfen.");
            return;
        }
        if (status != WiFiDirectAdvertisementPublisherStatus.Started)
        {
            if (discovery != null) FinishDirectSearch("Das Direkt-WLAN wurde beendet.");
            return;
        }
        var adapter = publisher.Adapter;
        if (adapter == null) {
            if (DateTime.UtcNow >= startupDeadline)
                FinishDirectSearch("Der aktive AnyDisplay-Direktadapter ist noch nicht eindeutig verfügbar. Bitte erneut verbinden.");
            return;
        }
        if (!bleAttempted) StartBluetooth();
        string bluetoothPeer = bleEndpoint;
        if (bluetoothPeer != null)
        {
            IPAddress ip;
            if (IPAddress.TryParse(bluetoothPeer.Split(':')[0], out ip) && IsDirectPeer(ip))
            {
                StartDirectHost(bluetoothPeer);
                return;
            }
        }
        if (discovery == null)
        {
            detail.Text = "Warte auf das Tablet …" + Environment.NewLine +
                "WLAN: " + directProfile.Ssid + Environment.NewLine +
                "Passwort: " + directProfile.Password + Environment.NewLine +
                (bleMessage ?? "Bluetooth LE wird eingerichtet …");
            connect.Enabled = disconnect.Enabled = true;
            searchDeadline = DateTime.UtcNow.AddSeconds(90);
            discovery = Task.Run((Func<string>)FindDirectPeer);
            if (bleReceiver != null)
            {
                var local = adapter.GetIPProperties().UnicastAddresses.FirstOrDefault(item =>
                    item.Address.AddressFamily == AddressFamily.InterNetwork &&
                    item.IPv4Mask != null && !IPAddress.IsLoopback(item.Address) &&
                    !item.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
                if (local != null)
                {
                    string deviceId = bleReceiver.Id;
                    directDiscoveryCancellation = new System.Threading.CancellationTokenSource();
                    var token = directDiscoveryCancellation.Token;
                    directDiscovery = Task.Run(() =>
                    {
                        try { return ReceiverDiscovery.FindDirect(local, deviceId, token); }
                        catch (SocketException) { return null; }
                    });
                }
            }
            return;
        }
        if (directDiscovery != null && directDiscovery.Status == TaskStatus.RanToCompletion)
        {
            string fastEndpoint = directDiscovery.Result;
            IPAddress ip;
            if (fastEndpoint != null && IPAddress.TryParse(fastEndpoint.Split(':')[0], out ip) && IsDirectPeer(ip))
            {
                DiagnosticLog.Note("Direct receiver found through UDP");
                StartDirectHost(fastEndpoint);
                return;
            }
        }
        if (!discovery.IsCompleted) return;
        string endpoint = discovery.GetAwaiter().GetResult();
        if (endpoint != null)
        {
            StartDirectHost(endpoint);
        }
        else if (DateTime.UtcNow >= searchDeadline.Value)
        {
            FinishDirectSearch("Kein eindeutiges AnyDisplay-Gerät gefunden. Prüfe am Tablet das gespeicherte Direkt-WLAN und öffne AnyDisplay. Falls mehrere Geräte verbunden sind, trenne die anderen.");
        }
        else
        {
            discovery = Task.Run((Func<string>)FindDirectPeer);
        }
    }


    private void StartDirectHost(string endpoint)
    {
        address.ReadOnly = true;
        address.Visible = addressLabel.Visible = true;
        address.Text = endpoint;
        searchDeadline = null;
        discovery = null;
        StartHost("connect " + endpoint, directVideoArguments);
        directVideoArguments = null;
    }

    private static object ChooseReceiver(IWin32Window owner, object[] receivers, string title)
    {
        if (receivers.Length == 1) return receivers[0];
        using (var dialog = new Form {
            Text = title, ClientSize = new Size(600, 260), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            Font = new Font("Segoe UI", 10)
        })
        using (var list = new ListBox { Left = 16, Top = 42, Width = 568, Height = 154, HorizontalScrollbar = true })
        using (var ok = new Button { Left = 360, Top = 212, Width = 108, Text = "Auswählen", DialogResult = DialogResult.OK })
        using (var cancel = new Button { Left = 476, Top = 212, Width = 108, Text = "Abbrechen", DialogResult = DialogResult.Cancel })
        {
            var hint = new Label { Left = 16, Top = 12, Width = 568, Height = 26, Text = receivers.Any(item => item is PairedReceiver)
                ? "Gespeicherte Kopplungen · Geräte-ID unten in der Android-App vergleichen"
                : "Empfänger mit Name und Adresse auswählen" };
            dialog.Controls.Add(hint);
            list.Items.AddRange(receivers);
            list.SelectedIndex = 0;
            list.DoubleClick += delegate { dialog.DialogResult = DialogResult.OK; };
            dialog.Controls.AddRange(new Control[] { list, ok, cancel });
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            return dialog.ShowDialog(owner) == DialogResult.OK ? list.SelectedItem : null;
        }
    }

    private bool PrepareBluetooth()
    {
        bleReceiver = null;
        bleMessage = null;
        try
        {
            var receivers = BleDirect.Receivers(out bleHostId);
            if (receivers.Length == 0)
            {
                bleMessage = "Für Bluetooth-Automatik einmal per USB oder Netzwerk koppeln. Manuelles WLAN ist ebenfalls möglich.";
                return true;
            }
            bleReceiver = (PairedReceiver)ChooseReceiver(this, receivers.Cast<object>().ToArray(), "Tablet für Bluetooth-Direktverbindung");
            return bleReceiver != null;
        }
        catch (Exception ex)
        {
            bleMessage = "Bluetooth-Kopplung konnte nicht gelesen werden (" + ex.Message + "). WLAN bitte manuell auswählen.";
            return true;
        }
    }

    private async void StartBluetooth()
    {
        if (bleReceiver == null) { bleAttempted = true; return; }
        var owner = publisher;
        var adapter = owner == null ? null : owner.Adapter;
        if (adapter == null) return;
        bleAttempted = true;
        int frequencyMhz = DirectAccessPoint.ReadFrequencyMhz(adapter);
        DiagnosticLog.Note(frequencyMhz > 0 ? "Direct Wi-Fi frequency " + frequencyMhz + " MHz" : "Direct Wi-Fi frequency unavailable");
        var current = new BleDirect(bleHostId, bleReceiver, directProfile.Ssid, directProfile.Password,
            adapter.GetPhysicalAddress().GetAddressBytes(), frequencyMhz);
        ble = current;
        DiagnosticLog.Note("BLE target " + bleReceiver.Id.Substring(0, 8));
        current.PeerReady += endpoint => { if (ble == current) bleEndpoint = endpoint; };
        current.Failed += message =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate { BluetoothStatus(current, message); }); }
            catch (InvalidOperationException) { }
        };
        try
        {
            await current.Start();
            BluetoothStatus(current, "Bluetooth LE bereit für " + bleReceiver.Name +
                ". AnyDisplay auf dem Tablet öffnen und Bluetooth-Direktverbindung aktivieren.");
        }
        catch (Exception ex)
        {
            BluetoothStatus(current, ex.Message + " WLAN bitte manuell auswählen.");
            current.Dispose();
            if (ble == current) ble = null;
        }
    }

    private void BluetoothStatus(BleDirect current, string message)
    {
        if (ble != current || !searchDeadline.HasValue || directProfile == null) return;
        bleMessage = message;
        DiagnosticLog.Note("BLE " + message);
        detail.Text = "Warte auf das Tablet …" + Environment.NewLine +
            "WLAN: " + directProfile.Ssid + Environment.NewLine +
            "Passwort: " + directProfile.Password + Environment.NewLine + message;
    }

    private sealed class NetworkReceiver
    {
        public string Name, Endpoint, Id;
        public string[] Endpoints;
        public override string ToString() { return Name + " · " + Endpoint; }
    }

    private static NetworkReceiver[] ParseReceivers(string output)
    {
        var names = Regex.Matches(output, @"(?m)^PEER_NAME ([0-9.]+:[0-9]+) ([0-9a-f]+)\r?$")
            .Cast<Match>().GroupBy(match => match.Groups[1].Value)
            .ToDictionary(group => group.Key, group => group.First().Groups[2].Value);
        var receivers = new List<NetworkReceiver>();
        foreach (Match match in Regex.Matches(output, @"(?m)^PEER ([0-9.]+):([0-9]+) ([0-9a-f]{32}|-) ([01])\r?$"))
        {
            IPAddress ip;
            ushort port;
            if (!IPAddress.TryParse(match.Groups[1].Value, out ip) || IPAddress.IsLoopback(ip) ||
                !ushort.TryParse(match.Groups[2].Value, out port) || port == 0) continue;
            string endpoint = ip + ":" + port, name = "AnyDisplay";
            string encoded;
            if (names.TryGetValue(endpoint, out encoded) && encoded.Length % 2 == 0 && encoded.Length <= 2048)
            {
                try { name = Encoding.UTF8.GetString(BleDirect.Hex(encoded, encoded.Length / 2)); }
                catch (FormatException) { }
            }
            name = new string(name.Where(ch => !Char.IsControl(ch)).ToArray()).Trim();
            receivers.Add(new NetworkReceiver {
                Endpoint = endpoint, Name = String.IsNullOrWhiteSpace(name) ? "AnyDisplay" : name,
                Id = match.Groups[3].Value == "-" ? ip.ToString() : match.Groups[3].Value
            });
        }
        return receivers.GroupBy(item => item.Id).Select(group => {
            var ordered = group.OrderByDescending(item => IsLocalSubnet(IPAddress.Parse(item.Endpoint.Split(':')[0])))
                .ThenBy(item => item.Endpoint, StringComparer.Ordinal).ToArray();
            var receiver = ordered[0];
            receiver.Endpoints = ordered.Select(item => item.Endpoint).Distinct().ToArray();
            return receiver;
        }).OrderBy(item => item.Name).ToArray();
    }


    private void UpdateNetworkRoutes()
    {
        bool visible = transport.SelectedIndex == 2 && selectedReceiver != null && selectedReceiver.Endpoints.Length > 1;
        networkRoutes.Visible = visible;
        if (visible) networkRoutes.Text = "Verbindungsweg auswählen (" + selectedReceiver.Endpoints.Length + " Adressen)";
    }

    private sealed class NetworkRoute
    {
        public string Endpoint, Label;
        public override string ToString() { return Label + " · " + Endpoint; }
    }

    private void ChooseNetworkRoute()
    {
        var receiver = selectedReceiver;
        if (receiver == null) return;
        var routes = receiver.Endpoints.OrderByDescending(endpoint => endpoint == networkAddress).Select(endpoint =>
            new NetworkRoute { Endpoint = endpoint, Label = RouteLabel(IPAddress.Parse(endpoint.Split(':')[0])) }).ToArray();
        var route = (NetworkRoute)ChooseReceiver(this, routes.Cast<object>().ToArray(), receiver.Name + " – Verbindungsweg");
        if (route == null) return;
        address.Text = route.Endpoint;
        selectedReceiver = receiver;
        manualNetworkAddress = true;
        UpdateNetworkRoutes();
        detail.Text = "Ausgewählt: " + receiver.Name + " · " + route;
    }

    private static string RouteLabel(IPAddress ip)
    {
        uint index;
        if (GetBestInterface(BitConverter.ToUInt32(ip.GetAddressBytes(), 0), out index) == 0)
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                try {
                    var ipv4 = adapter.GetIPProperties().GetIPv4Properties();
                    if (ipv4 == null || ipv4.Index != index) continue;
                    if (adapter.Description.IndexOf("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) >= 0) return "Direkt-WLAN";
                    if (adapter.Description.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0) return "Tailscale";
                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "WLAN";
                    if (adapter.Description.IndexOf("Tunnel", StringComparison.OrdinalIgnoreCase) >= 0) return "VPN";
                    return "Netzwerk";
                } catch (NetworkInformationException) { }
            }
        }
        return "Netzwerk";
    }

    private static bool IsLocalSubnet(IPAddress peer)
    {
        byte[] remote = peer.GetAddressBytes();
        return NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
            adapter.OperationalStatus == OperationalStatus.Up &&
            adapter.GetIPProperties().UnicastAddresses.Any(local =>
            {
                if (local.Address.AddressFamily != AddressFamily.InterNetwork || local.IPv4Mask == null) return false;
                byte[] host = local.Address.GetAddressBytes(), mask = local.IPv4Mask.GetAddressBytes();
                return Enumerable.Range(0, 4).All(i => (remote[i] & mask[i]) == (host[i] & mask[i]));
            }));
    }

    private string DiscoverOutput()
    {
        var additional = Task.Run(() => { try { return ReceiverDiscovery.Find(); } catch (Exception) { return ""; } });
        using (var process = Process.Start(new ProcessStartInfo(exe, "discover --seconds 3")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true
        }))
        {
            process.BeginErrorReadLine();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            string extra = additional.GetAwaiter().GetResult();
            if (process.ExitCode != 0 && extra.Length == 0) throw new InvalidOperationException("Die Netzwerksuche ist fehlgeschlagen.");
            return output + Environment.NewLine + extra;
        }
    }

    private async void BeginNetworkSearch(bool connectAfter)
    {
        if (networkSearching) return;
        networkSearching = true;
        int generation = ++networkSearchGeneration;
        var running = child;
        findReceivers.Enabled = false;
        if (child == null)
        {
            transport.Enabled = false;
            connect.Text = "Suche abbrechen";
        }
        detail.Text = "Suche AnyDisplay-Geräte im Netzwerk …";
        try
        {
            string output = await Task.Run((Func<string>)DiscoverOutput);
            if (generation != networkSearchGeneration || IsDisposed || transport.SelectedIndex != 2 || child != running) return;
            var receivers = ParseReceivers(output);
            if (receivers.Length == 0)
            {
                detail.Text = "Kein AnyDisplay-Gerät gefunden. App im selben Netzwerk öffnen und erneut suchen oder die Adresse eingeben.";
                return;
            }
            var receiver = (NetworkReceiver)ChooseReceiver(this, receivers.Cast<object>().ToArray(), "AnyDisplay-Empfänger auswählen");
            if (receiver == null) { detail.Text = "Geräteauswahl abgebrochen."; return; }
            address.Text = receiver.Endpoint;
            selectedReceiver = receiver;
            UpdateNetworkRoutes();
            manualNetworkAddress = false;
            detail.Text = "Ausgewählt: " + receiver;
            if (connectAfter)
            {
                if (child == null) StartHost(NetworkTarget());
                else RequestDisconnect(true);
            }
        }
        catch (Exception ex)
        {
            if (generation == networkSearchGeneration && !IsDisposed) detail.Text = ex.Message;
        }
        finally
        {
            if (generation == networkSearchGeneration && !IsDisposed)
            {
                networkSearching = false;
                findReceivers.Enabled = transport.Enabled = connect.Enabled;
                if (connect.Enabled) connect.Text = child == null ? "Verbinden" : "Verbindung trennen";
            }
        }
    }

    private void CancelNetworkSearch()
    {
        networkSearchGeneration++;
        networkSearching = false;
        findReceivers.Enabled = transport.Enabled = true;
        connect.Text = child == null ? "Verbinden" : "Verbindung trennen";
        detail.Text = "Suche abgebrochen.";
    }

    private static void TestReceiverParsing()
    {
        string id = "00000000000000000000000000000001";
        string result = "PEER 192.0.2.20:47820 00000000000000000000000000000001 1\nPEER_NAME 192.0.2.20:47820 5461626c6574\nPEER 192.0.2.21:47820 00000000000000000000000000000002 0\nPEER_NAME 192.0.2.21:47820 48616e6479\nPEER 127.0.0.1:47820 00000000000000000000000000000001 1\n";
        var parsed = ParseReceivers(result);
        if (parsed.Length != 2 || parsed[0].Name != "Handy" || parsed[1].Name != "Tablet" ||
            parsed[1].Endpoint != "192.0.2.20:47820") throw new Exception("Named network receivers");
        if (ParseReceivers(result + "PEER 192.0.2.22:47820 " + id + " 1\n").Length != 2)
            throw new Exception("Duplicate receiver addresses");
        if (ParseReceivers(result + "PEER 192.0.2.22:47820 " + id + " 1\n").Single(item => item.Id == id).Endpoints.Length != 2)
            throw new Exception("Receiver route choices");
    }

    private string FindDirectPeer()
    {
        using (var process = Process.Start(new ProcessStartInfo(exe, "discover --seconds 2")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        }))
        {
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) return null;
            return SelectDirectPeer(output, IsDirectPeer);
        }
    }

    private static string SelectDirectPeer(string output, Func<IPAddress, bool> isDirect)
    {
        var candidates = Regex.Matches(output, @"(?m)^PEER ([0-9.]+):([0-9]+) ([0-9a-f]{32}|-) ([01])\r?$")
            .Cast<Match>()
            .Where(match => {
                IPAddress ip;
                return IPAddress.TryParse(match.Groups[1].Value, out ip) && isDirect(ip);
            })
            .ToArray();
        var paired = candidates.Where(match => match.Groups[4].Value == "1").ToArray();
        if (paired.Length != 0) candidates = paired;
        var devices = candidates.GroupBy(match => match.Groups[3].Value == "-"
            ? match.Groups[1].Value : match.Groups[3].Value).ToArray();
        if (devices.Length != 1) return null;
        var peer = devices[0].First();
        return peer.Groups[1].Value + ":" + peer.Groups[2].Value;
    }

    private void FinishDirectSearch(string message)
    {
        bleReceiver = null;
        EndDirect();
        tick.Stop();
        SetConnectionSettingsEnabled(true);
        connect.Enabled = true;
        disconnect.Enabled = false;
        address.Visible = addressLabel.Visible = false;
        connect.Text = "Verbinden";
        detail.Text = message;
    }

    private void SmokeTest()
    {
        if (transport.SelectedIndex != 0 ||
            VideoArguments() != "--codec h264 --fps 60 --display mirror --scale full --bitrate auto") throw new Exception("USB defaults");
        transport.SelectedIndex = 2;
        if (VideoArguments() != "--codec hevc --fps 30 --display mirror --scale half --bitrate 4000") throw new Exception("Network defaults");
        transport.SelectedIndex = 1;
        fps.SelectedIndex = 3;
        display.SelectedIndex = 1;
        scale.SelectedIndex = 2;
        if (VideoArguments() != "--codec h264 --fps variable --display extend --scale quarter --bitrate auto") throw new Exception("Direct options");
        scale.SelectedIndex = 3;
        if (!VideoArguments().Contains("--scale three-quarter")) throw new Exception("Three-quarter scale");
        bitrate.SelectedIndex = 1;
        if (!VideoArguments().EndsWith("--bitrate 10000")) throw new Exception("10 Mbit/s preset");
        bitrate.SelectedIndex = 3;
        manualBitrate.Value = 7.5m;
        if (!VideoArguments().EndsWith("--bitrate 7500")) throw new Exception("Manual bitrate");
        if (MatchPin("ANYDISPLAY_PIN 012345").Groups[1].Value != "012345" ||
            MatchPin("ANYDISPLAY_PIN 12345").Success || MatchPin("INFO ANYDISPLAY_PIN 012345").Success)
            throw new Exception("Pairing PIN format");
        string paired = "PEER 192.168.137.22:47820 00000000000000000000000000000001 1" + Environment.NewLine;
        string other = "PEER 192.168.137.23:47820 00000000000000000000000000000002 0" + Environment.NewLine;
        string lan = "PEER 192.168.1.44:47820 00000000000000000000000000000003 1" + Environment.NewLine;
        string secondPaired = "PEER 192.168.137.24:47820 00000000000000000000000000000004 1" + Environment.NewLine;
        Func<IPAddress, bool> directRoute = ip => ip.ToString().StartsWith("192.168.137.", StringComparison.Ordinal);
        if (SelectDirectPeer(other + lan + paired, directRoute) != "192.168.137.22:47820" ||
            SelectDirectPeer(lan, directRoute) != null ||
            SelectDirectPeer(paired + secondPaired, directRoute) != null)
            throw new Exception("Direct peer selection");
        byte[] testSecret = Enumerable.Range(0, 32).Select(n => (byte)n).ToArray();
        byte[] testHost = Enumerable.Range(0, 16).Select(n => (byte)n).ToArray();
        byte[] testDevice = Enumerable.Range(16, 16).Select(n => (byte)n).ToArray();
        byte[] testServer = Enumerable.Range(32, 16).Select(n => (byte)n).ToArray();
        byte[] testClient = Enumerable.Range(48, 16).Select(n => (byte)n).ToArray();
        if (!BleDirect.Equal(BleDirect.Tag(testSecret, "request", testHost, testServer, testDevice, testClient),
                BleDirect.Hex("584d7231752f99825838a090df0a7e6d63cfa676bbbb19f3fa0ed47e06549df0", 32)) ||
            !BleDirect.Equal(BleDirect.Tag(testSecret, "ready", testServer, testClient, new byte[] { 192, 168, 137, 20, 186, 204 }),
                BleDirect.Hex("d1569a3024c4293af340d899e852854ddb5f276d5c94cae0b5582eeef36ca497", 32)))
            throw new Exception("Bluetooth protocol vectors");
        byte[] nonce = Enumerable.Range(0, 16).Select(n => (byte)n).ToArray();
        byte[] reply = BleDirect.Join(Encoding.ASCII.GetBytes("ADR1"), nonce, nonce, new byte[] { 186, 204, 0, 6 }, Encoding.UTF8.GetBytes("Tablet"));
        string udp = ReceiverDiscovery.ParseReply(reply, nonce, IPAddress.Parse("192.0.2.20"));
        if (udp == null || ParseReceivers(udp).Single().Name != "Tablet" ||
            ReceiverDiscovery.ParseReply(reply, new byte[16], IPAddress.Parse("192.0.2.20")) != null)
            throw new Exception("UDP receiver identity and challenge");
        TestReceiverParsing();
        if (DirectAccessPoint.FrequencyForChannel(1) != 2412 ||
            DirectAccessPoint.FrequencyForChannel(14) != 2484 ||
            DirectAccessPoint.FrequencyForChannel(149) != 5745 ||
            DirectAccessPoint.FrequencyForChannel(157) != 5785 ||
            DirectAccessPoint.FrequencyForChannel(0) != 0)
            throw new Exception("Direct Wi-Fi channel mapping");
        var probe = new WiFiDirectAdvertisementPublisher();
        if (DirectAccessPoint.SelectAdapter(new[] { "old" }, new[] { "old" }) != "old" ||
            DirectAccessPoint.SelectAdapter(new[] { "foreign" }, new[] { "foreign", "own" }) != "own" ||
            DirectAccessPoint.SelectAdapter(new string[0], new[] { "a", "b" }) != null ||
            DirectAccessPoint.SelectAdapter(new[] { "a", "b" }, new[] { "a", "b" }) != null ||
            DirectAccessPoint.SelectAdapter(new[] { "old" }, new string[0]) != null)
            throw new Exception("Owned direct adapter selection");
        string dir = Path.Combine(Path.GetTempPath(), "AnyDisplay-profile-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "profile.dat");
        try
        {
            var first = DirectProfile.LoadOrCreate(path);
            var second = DirectProfile.LoadOrCreate(path);
            if (first.Ssid != second.Ssid || first.Password != second.Password)
                throw new Exception("Direct Wi-Fi profile changed after reopening");
            first.Configure(probe.Advertisement.LegacySettings);
            if (probe.Advertisement.LegacySettings.Ssid != first.Ssid ||
                probe.Advertisement.LegacySettings.Passphrase.Password.TrimEnd('\0') != first.Password)
                throw new Exception("WinRT did not retain the direct Wi-Fi profile");
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(dir);
        }
    }
}
