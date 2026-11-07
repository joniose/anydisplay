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
using Windows.Devices.WiFiDirect;
using Windows.Security.Credentials;

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
    private readonly ComboBox transport, codec, fps, display, scale, bitrate;
    private readonly NumericUpDown manualBitrate = new NumericUpDown();
    private readonly Label bitrateUnit;
    private readonly TextBox address = new TextBox();
    private readonly TextBox detail = new TextBox();
    private readonly Label addressLabel;
    private readonly Button connect = new Button();
    private readonly LinkLabel hotspot = new LinkLabel();
    private readonly Timer tick = new Timer { Interval = 200 };
    private readonly string exe;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem disconnect;
    private bool quitting;
    private WiFiDirectAdvertisementPublisher publisher;
    private DirectProfile directProfile;
    private Process child;
    private string lastHostError;
    private DateTime startupDeadline;
    private bool joiningDirect, usingHotspot;
    private volatile string directError;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint address, out uint index);


    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new Launcher(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "anydisplay-win.exe")))
        {
            if (args.Length == 1 && args[0] == "--smoke-test")
            {
                try { form.SmokeTest(); return 0; }
                catch (Exception e) { Console.Error.WriteLine(e); return 1; }
            }
            Application.Run(form);
        }
        return 0;
    }

    private Launcher(string exe)
    {
        this.exe = exe;
        Text = "AnyDisplay – Windows";
        ClientSize = new Size(550, 419);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 10);
        transport = Choice("Verbindung", 15, "USB", "Direkt-WLAN", "Netzwerk");
        codec = Choice("Codec", 49, "H.264", "HEVC");
        fps = Choice("Bildrate", 83, "60 FPS", "30 FPS", "15 FPS", "Variabel");
        display = Choice("Bildschirm", 117, "Gespiegelt", "Erweitert");
        scale = Choice("Auflösung", 151, "Voll", "Halb", "Viertel");
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
        connect.SetBounds(370, 372, 155, 32);
        connect.Click += delegate { Connect(); };
        Controls.Add(connect);
        AcceptButton = connect;
        transport.SelectedIndexChanged += delegate { UpdateTransport(); };
        tick.Tick += delegate { Poll(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("AnyDisplay öffnen", null, delegate { ShowSettings(); });
        disconnect = new ToolStripMenuItem("Verbindung trennen") { Enabled = false };
        disconnect.Click += delegate { StopHost(); };
        menu.Items.Add(disconnect);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, delegate
        {
            if (child != null && !child.HasExited)
            {
                if (StopHost()) quitting = true;
                return;
            }
            quitting = true;
            Close();
        });
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
        EndDirect();
        bool network = transport.SelectedIndex == 2;
        codec.SelectedIndex = fps.SelectedIndex = scale.SelectedIndex = network ? 1 : 0;
        bitrate.SelectedIndex = network ? 2 : 0;
        UpdateBitrate();
        address.Visible = addressLabel.Visible = network;
        address.Clear();
        hotspot.Visible = transport.SelectedIndex == 1;
        connect.Text = transport.SelectedIndex == 1 ? "Direkt-WLAN öffnen" : "Verbinden";
        detail.Text = new[] {
            "Tablet per USB anschließen und AnyDisplay öffnen. Dieser Build verwendet USB-Debugging.",
            "Windows öffnet ein eigenes WLAN für das Tablet. Ein Router und Internet sind dafür nicht nötig.",
            "PC und Tablet im selben Netzwerk. Standard: 30 FPS, halbe Auflösung und 4 Mbit/s."
        }[transport.SelectedIndex];
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
            new[] { "full", "half", "quarter" }[scale.SelectedIndex], kbps);
    }

    private void Connect()
    {
        try
        {
            if (!File.Exists(exe)) throw new InvalidOperationException("anydisplay-win.exe fehlt neben diesem Programm.");
            if (transport.SelectedIndex == 0) { StartHost("usb " + VideoArguments()); return; }
            string peer = address.Text.Trim();
            if (transport.SelectedIndex == 2)
            {
                if (!Regex.IsMatch(peer, @"\A(?:[A-Za-z0-9][A-Za-z0-9.-]*|\[[0-9A-Fa-f:.]+\])(?::[0-9]{1,5})?\z"))
                    throw new InvalidOperationException("Adresse aus AnyDisplay eingeben, zum Beispiel 192.168.1.25.");
                StartHost("connect " + peer + " " + VideoArguments());
                return;
            }
            if (!joiningDirect) { StartDirect(); return; }
            if (peer.EndsWith(":47820", StringComparison.Ordinal)) peer = peer.Substring(0, peer.Length - 6);
            IPAddress ip;
            if (!IPAddress.TryParse(peer, out ip) || !IsDirectPeer(ip))
                throw new InvalidOperationException("Diese Adresse führt nicht über den Direktadapter. Bitte WLAN und Tablet-Adresse prüfen.");
            if (!usingHotspot && (publisher == null || publisher.Status != WiFiDirectAdvertisementPublisherStatus.Started))
                throw new InvalidOperationException("Das Direkt-WLAN läuft nicht mehr. Bitte erneut öffnen.");
            StartHost("connect " + ip + ":47820 " + VideoArguments());
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

    private void StartDirect()
    {
        EndDirect();
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
        connect.Enabled = false;
        detail.Text = "Direkt-WLAN wird geöffnet …";
        tick.Start();
    }

    private void OnPublisherStatusChanged(WiFiDirectAdvertisementPublisher sender, WiFiDirectAdvertisementPublisherStatusChangedEventArgs args)
    {
        if (args.Status == WiFiDirectAdvertisementPublisherStatus.Aborted)
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
        joiningDirect = true;
        address.Visible = addressLabel.Visible = true;
        connect.Text = "Verbinden";
        connect.Enabled = true;
        address.Focus();
    }

    private void EndDirect()
    {
        if (publisher != null)
        {
            publisher.StatusChanged -= OnPublisherStatusChanged;
            try
            {
                if (publisher.Status == WiFiDirectAdvertisementPublisherStatus.Started) publisher.Stop();
            }
            catch (COMException) { }
            publisher = null;
        }
        joiningDirect = usingHotspot = false;
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

    private bool StopHost()
    {
        if (child == null || child.HasExited) return true;
        try { child.StandardInput.WriteLine("stop"); return true; }
        catch (IOException) { return child.HasExited; }
        catch (InvalidOperationException) { return child.HasExited; }
    }

    private void StartHost(string arguments)
    {
        lastHostError = null;
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, arguments + " --gui")
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
        transport.Enabled = hotspot.Enabled = connect.Enabled = false;
        disconnect.Enabled = true;
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
            detail.Text = "Verbunden. Öffne den Stream auf dem Tablet." + Environment.NewLine + line;
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
            tick.Stop();
            transport.Enabled = hotspot.Enabled = connect.Enabled = true;
            disconnect.Enabled = false;
            tray.Text = "AnyDisplay";
            UpdateTransport();
            detail.Text = code == 0 ? "Verbindung beendet." :
                (lastHostError ?? ("Verbindung beendet (Code " + code + "). Prüfe Tablet, Kopplung und Monitor."));
            if (quitting) Close();
            return;
        }
        if (publisher == null) { tick.Stop(); connect.Enabled = true; return; }
        var status = publisher.Status;
        if (!joiningDirect && status == WiFiDirectAdvertisementPublisherStatus.Started)
        {
            PromptForAddress("WLAN: " + directProfile.Ssid + Environment.NewLine +
                "Passwort: " + directProfile.Password + Environment.NewLine +
                "Bei „Kein Internet“ verbunden bleiben. Danach AnyDisplay auf dem Tablet öffnen.");
        }
        else if (status == WiFiDirectAdvertisementPublisherStatus.Aborted ||
            (joiningDirect && status != WiFiDirectAdvertisementPublisherStatus.Started) ||
            (!joiningDirect && DateTime.UtcNow >= startupDeadline))
        {
            string reason = directError ?? status.ToString();
            tick.Stop();
            UpdateTransport();
            connect.Enabled = true;
            detail.Text = "Direkt-WLAN konnte nicht geöffnet werden (" + reason + "). Mobilen Hotspot und Drahtlosprojektion prüfen.";
        }
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
        bitrate.SelectedIndex = 1;
        if (!VideoArguments().EndsWith("--bitrate 10000")) throw new Exception("10 Mbit/s preset");
        bitrate.SelectedIndex = 3;
        manualBitrate.Value = 7.5m;
        if (!VideoArguments().EndsWith("--bitrate 7500")) throw new Exception("Manual bitrate");
        if (MatchPin("ANYDISPLAY_PIN 012345").Groups[1].Value != "012345" ||
            MatchPin("ANYDISPLAY_PIN 12345").Success || MatchPin("INFO ANYDISPLAY_PIN 012345").Success)
            throw new Exception("Pairing PIN format");
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
