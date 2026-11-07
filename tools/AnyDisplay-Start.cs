using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
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
[assembly: System.Reflection.AssemblyFileVersion("0.13.0.13")]

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
    private readonly TextBox address = new TextBox();
    private readonly TextBox detail = new TextBox();
    private readonly Label addressLabel;
    private readonly Button connect = new Button();
    private readonly Button reconnect = new Button();
    private readonly LinkLabel hotspot = new LinkLabel();
    private readonly Timer tick = new Timer { Interval = 200 };
    private readonly string exe;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem disconnect;
    private bool quitting;
    private WiFiDirectAdvertisementPublisher publisher;
    private WiFiDirectAdvertisementPublisher retiringPublisher;
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
    private DateTime startupDeadline;
    private bool usingHotspot;
    private DateTime? searchDeadline;
    private Task<string> discovery;
    private volatile string directError;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint address, out uint index);


    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
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
        bitrate.SelectedIndexChanged += delegate { UpdateBitrate(); };
        addressLabel = Caption("Tablet-Adresse", 18, 228, 145);
        address.SetBounds(165, 224, 360, 27);
        address.TextChanged += delegate
        {
            if (transport.SelectedIndex == 2 && !address.ReadOnly) networkAddress = address.Text;
        };
        Controls.Add(address);
        hotspot.Text = "Optional: Windows-Hotspot mit Internet";
        hotspot.SetBounds(165, 254, 360, 24);
        hotspot.LinkClicked += delegate { SelectHotspot(); };
        Controls.Add(hotspot);
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
        if (child != null)
        {
            UpdateAddressField();
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
        address.Visible = addressLabel.Visible = network || direct;
        if (network) address.Text = networkAddress;
        else if (direct) address.Text = hostTarget.Substring("connect ".Length);
        address.ReadOnly = !network;
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

    private string VideoArguments()
    {
        string kbps = bitrate.SelectedIndex == 3
            ? ((int)(manualBitrate.Value * 1000m)).ToString(System.Globalization.CultureInfo.InvariantCulture)
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
            if (child != null) { Disconnect(); return; }
            if (searchDeadline.HasValue) { FinishDirectSearch("Suche abgebrochen."); return; }
            if (!File.Exists(exe)) throw new InvalidOperationException("anydisplay-win.exe fehlt neben diesem Programm.");
            if (transport.SelectedIndex == 0) { StartHost("usb"); return; }
            string peer = address.Text.Trim();
            if (transport.SelectedIndex == 2) { StartHost(NetworkTarget()); return; }
            if (!usingHotspot) { StartDirect(); return; }
            if (peer.EndsWith(":47820", StringComparison.Ordinal)) peer = peer.Substring(0, peer.Length - 6);
            IPAddress ip;
            if (!IPAddress.TryParse(peer, out ip) || !IsDirectPeer(ip))
                throw new InvalidOperationException("Diese Adresse führt nicht über den Direktadapter. Bitte WLAN und Tablet-Adresse prüfen.");
            StartHost("connect " + ip + ":47820");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "AnyDisplay"); }
    }

    private static bool IsDirectPeer(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        uint index;
        if (GetBestInterface(BitConverter.ToUInt32(address.GetAddressBytes(), 0), out index) != 0) return false;
        return NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.Description.IndexOf("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) < 0) return false;
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
        EndDirect();
        directVideoArguments = videoArguments;
        AcceptButton = connect;
        if (retiringPublisher != null)
        {
            startupDeadline = DateTime.UtcNow.AddSeconds(4);
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

    private static bool HasActiveDirectAdapter()
    {
        return NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
            adapter.OperationalStatus == OperationalStatus.Up &&
            adapter.Description.IndexOf("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) >= 0 &&
            adapter.GetIPProperties().UnicastAddresses.Any(local =>
            {
                var ip = local.Address;
                if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                    return !ip.IsIPv6LinkLocal && !ip.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(ip);
                var bytes = ip.GetAddressBytes();
                return !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) &&
                    (bytes[0] != 169 || bytes[1] != 254);
            }));
    }

    private const string DirectAdapterBusy =
        "Wi-Fi Direct ist belegt. Beende die andere Direktverbindung, Drahtlosprojektion oder den mobilen Hotspot und versuche es erneut.";

    private void OpenDirectPublisher()
    {
        if (HasActiveDirectAdapter()) throw new InvalidOperationException(DirectAdapterBusy);
        retiringPublisher = null;
        directError = null;
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnyDisplay", "direct-profile.dat");
        directProfile = DirectProfile.LoadOrCreate(file);
        publisher = new WiFiDirectAdvertisementPublisher();
        try
        {
            publisher.StatusChanged += OnPublisherStatusChanged;
            var advertisement = publisher.Advertisement;
            advertisement.IsAutonomousGroupOwnerEnabled = true;
            directProfile.Configure(advertisement.LegacySettings);
            publisher.Start();
        }
        catch { EndDirect(); throw; }
        startupDeadline = DateTime.UtcNow.AddSeconds(10);
        SetConnectionSettingsEnabled(false);
        connect.Text = "Abbrechen";
        connect.Enabled = disconnect.Enabled = false;
        searchDeadline = startupDeadline;
        detail.Text = "Direkt-WLAN wird geöffnet …";
        tick.Start();
    }

    private void OnPublisherStatusChanged(WiFiDirectAdvertisementPublisher sender, WiFiDirectAdvertisementPublisherStatusChangedEventArgs args)
    {
        if (sender == publisher && args.Status == WiFiDirectAdvertisementPublisherStatus.Aborted)
            directError = args.Error.ToString();
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

    private void EndDirect()
    {
        if (publisher != null)
        {
            publisher.StatusChanged -= OnPublisherStatusChanged;
            try
            {
                if (publisher.Status != WiFiDirectAdvertisementPublisherStatus.Stopped &&
                    publisher.Status != WiFiDirectAdvertisementPublisherStatus.Aborted) publisher.Stop();
            }
            catch (COMException) { }
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
        transport.Enabled = hotspot.Enabled = address.Enabled = enabled;
    }

    private void ExitLauncher()
    {
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
        string networkTarget = null;
        if (restart && transport.SelectedIndex == 2)
        {
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
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, target + " " + (videoArguments ?? VideoArguments()) + " --gui")
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
        detail.Text = "Verbindung wird aufgebaut …";
        hostTarget = target;
        activeTransport = transport.SelectedIndex;
        SetConnectionSettingsEnabled(false);
        transport.Enabled = address.Enabled = true;
        UpdateAddressField();
        AcceptButton = null;
        connect.Text = "Verbindung trennen";
        connect.Enabled = disconnect.Enabled = reconnect.Enabled = true;
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
            bool confirmed = RequestPin(code);
            if (child != process || process.HasExited) return;
            try { process.StandardInput.WriteLine(confirmed ? "confirm " + code : "reject"); }
            catch (IOException) { }
            catch (InvalidOperationException) { }
        }
        else if (line.Contains(" WARN "))
        {
            lastHostError = line;
        }
        else if (line.Contains(" CONFIG stream="))
        {
            detail.Text = "Verbunden. Öffne den Stream auf dem Tablet. Bildänderungen gelten beim Neuaufbau." + Environment.NewLine + line;
        }
    }

    private static Match MatchPin(string line)
    {
        return Regex.Match(line, @"^ANYDISPLAY_PIN ([0-9]{6})$");
    }

    private bool RequestPin(string code)
    {
        using (var dialog = new Form
        {
            Text = "AnyDisplay koppeln",
            ClientSize = new Size(450, 173),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            Font = Font
        })
        using (var label = new Label())
        using (var input = new TextBox())
        using (var confirm = new Button())
        using (var cancel = new Button())
        {
            label.Text = "Windows-PIN: " + code + Environment.NewLine + "Gib den sechsstelligen PIN vom Tablet ein und bestätige die Kopplung auch dort.";
            label.SetBounds(14, 12, 422, 56);
            input.SetBounds(14, 72, 140, 27);
            input.MaxLength = 6;
            confirm.Text = "Koppeln";
            confirm.SetBounds(223, 119, 99, 34);
            confirm.DialogResult = DialogResult.OK;
            confirm.Enabled = false;
            input.TextChanged += delegate { confirm.Enabled = input.Text == code; };
            cancel.Text = "Abbrechen";
            cancel.SetBounds(330, 119, 106, 34);
            cancel.DialogResult = DialogResult.Cancel;
            dialog.Controls.AddRange(new Control[] { label, input, confirm, cancel });
            dialog.AcceptButton = confirm;
            dialog.CancelButton = cancel;
            dialog.ActiveControl = input;
            return dialog.ShowDialog(this) == DialogResult.OK && input.Text == code;
        }
    }

    private void Poll()
    {
        if (child != null)
        {
            if (!child.HasExited) return;
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
            var previous = retiringPublisher.Status;
            if (previous != WiFiDirectAdvertisementPublisherStatus.Started &&
                DateTime.UtcNow >= retireUntil && !HasActiveDirectAdapter())
                OpenDirectPublisher();
            else if (DateTime.UtcNow >= startupDeadline)
                FinishDirectSearch(DirectAdapterBusy);
            return;
        }
        var status = publisher.Status;
        if (status == WiFiDirectAdvertisementPublisherStatus.Aborted ||
            (discovery == null && status != WiFiDirectAdvertisementPublisherStatus.Started &&
             DateTime.UtcNow >= startupDeadline))
        {
            string reason = directError ?? status.ToString();
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
        if (discovery == null)
        {
            detail.Text = "Warte auf das Tablet …" + Environment.NewLine +
                "WLAN: " + directProfile.Ssid + Environment.NewLine +
                "Passwort: " + directProfile.Password + Environment.NewLine +
                "Falls nötig, das WLAN einmal auf dem Tablet auswählen und AnyDisplay öffnen.";
            connect.Enabled = disconnect.Enabled = true;
            searchDeadline = DateTime.UtcNow.AddSeconds(90);
            discovery = Task.Run((Func<string>)FindDirectPeer);
            return;
        }
        if (!discovery.IsCompleted) return;
        string endpoint = discovery.GetAwaiter().GetResult();
        if (endpoint != null)
        {
            address.ReadOnly = true;
            address.Visible = addressLabel.Visible = true;
            address.Text = endpoint;
            searchDeadline = null;
            discovery = null;
            StartHost("connect " + endpoint, directVideoArguments);
            directVideoArguments = null;
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
        var probe = new WiFiDirectAdvertisementPublisher();
        probe.StatusChanged += OnPublisherStatusChanged;
        probe.StatusChanged -= OnPublisherStatusChanged;
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
