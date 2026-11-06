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
using System.Windows.Forms;
using Windows.Devices.WiFiDirect;

internal sealed class Launcher : Form
{
    private readonly ComboBox transport, codec, fps, display, scale;
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
    private Process child;
    private DateTime startupDeadline;
    private bool joiningDirect, usingHotspot;
    private volatile string directError;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint address, out uint index);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool ignore);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint signal, uint processGroup);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

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
        ClientSize = new Size(550, 382);
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
        addressLabel = Caption("Tablet-Adresse", 18, 194, 145);
        address.SetBounds(165, 190, 360, 27);
        Controls.Add(address);
        hotspot.Text = "Optional: Windows-Hotspot mit Internet";
        hotspot.SetBounds(165, 220, 360, 24);
        hotspot.LinkClicked += delegate { SelectHotspot(); };
        Controls.Add(hotspot);
        detail.Multiline = true;
        detail.ReadOnly = true;
        detail.ScrollBars = ScrollBars.Vertical;
        detail.SetBounds(18, 248, 507, 78);
        Controls.Add(detail);
        connect.SetBounds(370, 338, 155, 32);
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
            if (child != null && !child.HasExited && !StopHost()) return;
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

    private string VideoArguments()
    {
        return String.Format("--codec {0} --fps {1} --display {2} --scale {3}",
            new[] { "h264", "hevc" }[codec.SelectedIndex],
            new[] { "60", "30", "15", "variable" }[fps.SelectedIndex],
            new[] { "mirror", "extend" }[display.SelectedIndex],
            new[] { "full", "half", "quarter" }[scale.SelectedIndex]);
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
                StartHost("connect " + peer + " " + VideoArguments() + " --bitrate 4000");
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
        publisher = new WiFiDirectAdvertisementPublisher();
        publisher.StatusChanged += OnPublisherStatusChanged;
        var advertisement = publisher.Advertisement;
        advertisement.IsAutonomousGroupOwnerEnabled = true;
        advertisement.LegacySettings.Ssid = "DIRECT-AnyDisplay-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        advertisement.LegacySettings.IsEnabled = true;
        try { publisher.Start(); }
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
        using (var canvas = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(canvas))
        using (var blue = new SolidBrush(Color.FromArgb(27, 120, 220)))
        {
            g.FillRectangle(blue, 2, 3, 28, 21);
            g.FillRectangle(Brushes.White, 5, 6, 22, 14);
            g.FillRectangle(blue, 13, 23, 6, 5);
            g.FillRectangle(blue, 8, 27, 16, 3);
            var handle = canvas.GetHicon();
            try { return (Icon)Icon.FromHandle(handle).Clone(); }
            finally { DestroyIcon(handle); }
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
        if (AttachConsole((uint)child.Id))
        {
            bool sent;
            try
            {
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                sent = GenerateConsoleCtrlEvent(0, 0);
            }
            finally { FreeConsole(); }
            if (sent) return true;
        }
        ShowSettings();
        MessageBox.Show(this, "Zum Trennen im AnyDisplay-Verbindungsfenster Strg+C drücken.", "AnyDisplay");
        return false;
    }

    private void StartHost(string arguments)
    {
        child = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true });
        transport.Enabled = hotspot.Enabled = connect.Enabled = false;
        disconnect.Enabled = true;
        tray.Text = "AnyDisplay – Verbindung läuft";
        tick.Start();
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
                "Verbindung beendet (Code " + code + "). Prüfe die Adresse, die Kopplung und ob der gewählte Monitor aktiv ist.";
            return;
        }
        if (publisher == null) { tick.Stop(); connect.Enabled = true; return; }
        var status = publisher.Status;
        if (!joiningDirect && status == WiFiDirectAdvertisementPublisherStatus.Started)
        {
            var legacy = publisher.Advertisement.LegacySettings;
            PromptForAddress("Tablet mit " + legacy.Ssid + " verbinden. Passwort: " +
                legacy.Passphrase.Password.TrimEnd('\0') + ". Bei „Kein Internet“ verbunden bleiben. Dann AnyDisplay öffnen.");
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
            VideoArguments() != "--codec h264 --fps 60 --display mirror --scale full") throw new Exception("USB defaults");
        transport.SelectedIndex = 2;
        if (VideoArguments() != "--codec hevc --fps 30 --display mirror --scale half") throw new Exception("Network defaults");
        transport.SelectedIndex = 1;
        fps.SelectedIndex = 3;
        display.SelectedIndex = 1;
        scale.SelectedIndex = 2;
        if (VideoArguments() != "--codec h264 --fps variable --display extend --scale quarter") throw new Exception("Direct options");
        var probe = new WiFiDirectAdvertisementPublisher();
        probe.StatusChanged += OnPublisherStatusChanged;
        probe.StatusChanged -= OnPublisherStatusChanged;
    }
}
