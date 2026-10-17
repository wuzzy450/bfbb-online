using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;
using BfbbOnline.Server;
using BfbbOnline.Shared;

namespace BfbbOnline.Client;

public sealed class MainForm : Form
{
    readonly Config cfg = Config.Load();
    Session? session;
    ServerHost? server;

    readonly TextBox name = new() { Width = 180 };
    readonly Button color = new() { Width = 60, Text = "", FlatStyle = FlatStyle.Flat };
    readonly TextBox serverAddr = new() { Width = 180 };
    readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Width = 80 };
    readonly TextBox party = new() { Width = 180 };
    readonly CheckBox host = new() { Text = "Host the server on this PC", AutoSize = true };
    readonly ComboBox monitor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly CheckBox watchAlong = new() { Text = "Cutscene sync: watch teammates' cutscenes and conversations with them", AutoSize = true, MaximumSize = new Size(360, 0) };
    readonly CheckBox hostUpnp = new() { Text = "Open the port on my router (UPnP, for internet play)", AutoSize = true };
    readonly CheckBox hostPvp = new() { Text = "PvP: attacks hit and hurt other players", AutoSize = true };
    readonly CheckBox hostSims = new() { Text = "Simulate maps on this PC (server-side)", AutoSize = true };
    readonly NumericUpDown maxSims = new() { Minimum = 1, Maximum = 16, Width = 50 };
    readonly Label hostInfo = new() { AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Color.DimGray };
    readonly Label gameInfo = new() { AutoSize = true, MaximumSize = new Size(360, 0) };
    readonly Button play = new() { Text = "Play", Width = 360, Height = 48, Font = new Font(SystemFonts.DefaultFont.FontFamily, 13, FontStyle.Bold) };
    readonly ListView members = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Height = 150 };
    readonly TextBox logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };
    readonly TextBox chat = new() { PlaceholderText = "Chat with your party (Enter to send)" };
    readonly Label status = new() { AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(6) };

    public MainForm()
    {
        Text = "BFBB Online";
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9.5f);
        k = DeviceDpi / 96f;
        foreach (var c in new Control[] { name, color, serverAddr, port, party, maxSims, play })
            c.Size = new Size(Px(c.Width), Px(c.Height));
        hostInfo.MaximumSize = gameInfo.MaximumSize = watchAlong.MaximumSize = new Size(Px(360), 0);
        ClientSize = new Size(Px(900), Px(600));
        MinimumSize = new Size(Px(820), Px(560));
        StartPosition = FormStartPosition.CenterScreen;

        var left = new FlowLayoutPanel { Dock = DockStyle.Left, Width = Px(400), FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12), AutoScroll = true };
        left.Controls.Add(Row("Your name", name));
        left.Controls.Add(Row("Color", color));
        left.Controls.Add(Row("Server", serverAddr));
        left.Controls.Add(Row("Port", port));
        left.Controls.Add(Row("Party", party));
        left.Controls.Add(new Label { Text = "Players in the same party share one campaign.", AutoSize = true, MaximumSize = new Size(Px(360), 0), ForeColor = Color.DimGray, Margin = new Padding(3, 0, 3, 10) });
        left.Controls.Add(Row("Game screen", monitor));
        left.Controls.Add(watchAlong);
        left.Controls.Add(new Label { Height = 6 });
        left.Controls.Add(host);
        hostUpnp.Margin = new Padding(18, 0, 0, 0);
        left.Controls.Add(hostUpnp);
        hostPvp.Margin = new Padding(18, 0, 0, 0);
        left.Controls.Add(hostPvp);
        var simRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(18, 0, 0, 0) };
        simRow.Controls.Add(hostSims);
        simRow.Controls.Add(new Label { Text = "max", AutoSize = true, Margin = new Padding(6, 6, 0, 0) });
        simRow.Controls.Add(maxSims);
        // map simulation is always on when hosting; no need to show the option
        left.Controls.Add(hostInfo);
        left.Controls.Add(new Label { Height = 8 });
        left.Controls.Add(gameInfo);
        var browse = new LinkLabel { Text = "Choose the BFBB Deluxe game folder...", AutoSize = true };
        browse.LinkClicked += (_, _) => BrowseDeluxe();
        var browseDolphin = new LinkLabel { Text = "Choose Dolphin.exe...", AutoSize = true };
        browseDolphin.LinkClicked += (_, _) => BrowseDolphin();
        left.Controls.Add(browse);
        left.Controls.Add(browseDolphin);
        left.Controls.Add(new Label { Height = 8 });
        left.Controls.Add(play);

        members.Columns.Add("Player", Px(160));
        members.Columns.Add("Where", Px(160));
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 12, 0), RowCount = 4, ColumnCount = 1 };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(150)));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(new Label { Text = "Party  (double-click a player to teleport to them)", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        members.Dock = DockStyle.Fill;
        logBox.Dock = DockStyle.Fill;
        chat.Dock = DockStyle.Fill;
        right.Controls.Add(members);
        var tpMenu = new ContextMenuStrip();
        tpMenu.Items.Add("Teleport to this player", null, (_, _) => TeleportToSelected());
        members.ContextMenuStrip = tpMenu;
        members.DoubleClick += (_, _) => TeleportToSelected();
        right.Controls.Add(logBox);
        right.Controls.Add(chat);

        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(status);

        name.Text = cfg.Name;
        SetColor(cfg.Color);
        serverAddr.Text = cfg.Server;
        port.Value = Math.Clamp(cfg.Port, 1, 65535);
        party.Text = cfg.Party;
        host.Checked = cfg.Host;
        hostSims.Checked = true;
        hostUpnp.Checked = cfg.HostUpnp;
        hostPvp.Checked = cfg.HostPvp;
        watchAlong.Checked = cfg.WatchAlong;
        // which monitor the game opens on
        monitor.Items.Add("Automatic");
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
            monitor.Items.Add($"Monitor {i + 1}{(screens[i].Primary ? " (main)" : "")}  {screens[i].Bounds.Width}x{screens[i].Bounds.Height}");
        var picked = GameScreen.Resolve(cfg, screens);
        monitor.SelectedIndex = picked == null ? 0 : Array.IndexOf(screens, picked) + 1;
        monitor.SelectedIndexChanged += (_, _) =>
        {
            GameScreen.Remember(cfg, monitor.SelectedIndex > 0 ? screens[monitor.SelectedIndex - 1] : null);
            try { cfg.Save(); } catch { }
        };
        // takes effect right away, also while playing
        watchAlong.CheckedChanged += (_, _) =>
        {
            cfg.WatchAlong = watchAlong.Checked;
            try { cfg.Save(); } catch { }
            Log(watchAlong.Checked ? "cutscene sync on" : "cutscene sync off: teammates' cutscenes and conversations won't interrupt you");
        };
        maxSims.Value = Math.Clamp(cfg.HostMaxSims, 1, 16);

        color.Click += (_, _) =>
        {
            using var d = new ColorDialog { Color = color.BackColor, FullOpen = true };
            if (d.ShowDialog(this) == DialogResult.OK) SetColor($"#{d.Color.R:x2}{d.Color.G:x2}{d.Color.B:x2}");
        };
        host.CheckedChanged += (_, _) => UpdateHostUi();
        play.Click += (_, _) => Play();
        chat.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || chat.Text.Trim().Length == 0) return;
            e.SuppressKeyPress = true;
            session?.SendChat(chat.Text.Trim());
            chat.Clear();
        };
        FormClosing += (_, _) => Shutdown();
        UpdateHostUi();
        RefreshGameInfo();
    }

    float k = 1f;
    int Px(int v) => (int)Math.Round(v * k);

    Control Row(string label, Control c)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
        p.Controls.Add(new Label { Text = label, Width = Px(100), TextAlign = ContentAlignment.MiddleLeft, Height = Px(28) });
        p.Controls.Add(c);
        return p;
    }

    void SetColor(string hex)
    {
        cfg.Color = hex;
        try { color.BackColor = ColorTranslator.FromHtml(hex); } catch { }
    }

    void UpdateHostUi()
    {
        serverAddr.Enabled = !host.Checked;
        hostSims.Enabled = maxSims.Enabled = hostUpnp.Enabled = hostPvp.Enabled = host.Checked;
        if (!host.Checked) { hostInfo.Text = ""; return; }
        var ips = LanAddresses();
        hostInfo.Text = $"Friends on your network connect to {string.Join(" or ", ips.DefaultIfEmpty("this PC's address"))}, port {port.Value}. "
            + $"Over the internet: the UPnP box opens TCP {port.Value} on your router (or forward it by hand) and the log shows your public address (or use Tailscale/ZeroTier).";
    }

    /// <summary>As many map sims as this PC can run alongside your own game
    /// (each sim ≈ 20% of a core and ≈ 500 MB; keep 2 cores and 4 GB for you).</summary>
    static int AutoMaxSims()
    {
        long ramMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        int byRam = (int)((ramMb - 4096) / 500);
        int byCpu = (Environment.ProcessorCount - 2) * 5;
        return Math.Clamp(Math.Min(byRam, byCpu), 1, 64);
    }

    static List<string> LanAddresses()
    {
        var l = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)
                        && !a.Address.ToString().StartsWith("169.254."))
                        l.Add(a.Address.ToString());
            }
        }
        catch { }
        return l;
    }

    string? DolphinPath => Installer.FindDolphin(cfg.DolphinPath);
    string? DeluxeDir => Installer.FindDeluxe(cfg.DeluxeDir, DolphinPath);

    void RefreshGameInfo()
    {
        var d = DolphinPath;
        var g = DeluxeDir;
        string s = d == null ? "Dolphin: not found (choose Dolphin.exe below)" : $"Dolphin: {d}";
        s += "\n";
        if (g == null) s += "BFBB Deluxe: not found. Build it once with Heavy Mod Manager (Launch Game), or choose its game folder below.";
        else
        {
            try { s += $"{Installer.Status(g)}: {g}"; }
            catch (Exception e) { s += $"BFBB Deluxe: {e.Message}"; }
        }
        gameInfo.Text = s;
    }

    void BrowseDeluxe()
    {
        using var d = new FolderBrowserDialog { Description = "The BFBB Deluxe game folder (contains sys and files)" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var dir = d.SelectedPath;
        if (!Installer.IsGameFolder(dir) && Installer.IsGameFolder(Path.Combine(dir, "game"))) dir = Path.Combine(dir, "game");
        if (!Installer.IsGameFolder(dir)) { MessageBox.Show(this, "That folder has no sys\\main.dol and files folder.", "BFBB Online"); return; }
        cfg.DeluxeDir = dir;
        cfg.Save();
        RefreshGameInfo();
    }

    void BrowseDolphin()
    {
        using var d = new OpenFileDialog { Filter = "Dolphin|Dolphin.exe", Title = "Dolphin.exe" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        cfg.DolphinPath = d.FileName;
        cfg.Save();
        RefreshGameInfo();
    }

    void Play()
    {
        cfg.Name = name.Text.Trim();
        cfg.Server = serverAddr.Text.Trim();
        cfg.Port = (int)port.Value;
        cfg.Party = party.Text.Trim();
        cfg.Host = host.Checked;
        cfg.HostSims = hostSims.Checked;
        cfg.HostUpnp = hostUpnp.Checked;
        cfg.HostPvp = hostPvp.Checked;
        cfg.WatchAlong = watchAlong.Checked;
        cfg.HostMaxSims = (int)maxSims.Value;
        cfg.Save();

        var dolphin = DolphinPath;
        var deluxe = DeluxeDir;
        if (dolphin == null) { BrowseDolphin(); dolphin = DolphinPath; }
        if (deluxe == null) { BrowseDeluxe(); deluxe = DeluxeDir; }
        if (dolphin == null || deluxe == null) return;

        try { Installer.Install(deluxe, Log); }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "BFBB Online: can't patch the game", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (session == null)
        {
            string target = cfg.Server;
            if (cfg.Host && server == null)
            {
                try
                {
                    var data = Path.Combine(AppContext.BaseDirectory, "server-data");
                    server = new ServerHost(new ServerOptions
                    {
                        Port = cfg.Port,
                        Pvp = cfg.HostPvp,
                        DataDir = Path.Combine(data, "worlds"),
                        Sims = new SimOptions
                        {
                            Enabled = true,
                            MaxSims = AutoMaxSims(),
                            DolphinPath = dolphin,
                            GameDol = Installer.GameDol,
                            WorkDir = Path.Combine(data, "sims"),
                        },
                        Log = s => Log("[server] " + s),
                    });
                    server.Start();
                    if (cfg.HostUpnp)
                    {
                        int upPort = cfg.Port;
                        Log($"opening port {upPort} on the router (UPnP)...");
                        _ = Task.Run(async () =>
                        {
                            var (map, msg) = await Upnp.OpenAsync(upPort);
                            upnp = map;
                            Log(msg);
                        });
                    }
                }
                catch (Exception e)
                {
                    server = null;
                    MessageBox.Show(this, $"Couldn't start the server: {e.Message}", "BFBB Online");
                    return;
                }
                target = "127.0.0.1";
            }
            session = new Session(cfg, Log);
            session.StatusChanged += s => Ui(() => status.Text = s);
            session.GameStateChanged += st => Ui(() => play.Text = st switch
            {
                "running" => "Game running",
                "stuck" => "Game crashed - Restart game",
                _ => "Play",
            });
            session.MembersChanged += list => Ui(() =>
            {
                members.Items.Clear();
                foreach (var (n, c, s) in list)
                {
                    var it = new ListViewItem(n) { ForeColor = SafeColor(c) };
                    it.SubItems.Add(s.Length == 0 ? "loading / menu" : s);
                    members.Items.Add(it);
                }
            });
            session.Chat += (n, c, t) => Log($"<{n}> {t}");
            session.Connect(target, cfg.Port);
            foreach (var c in new Control[] { name, serverAddr, port, party, host, hostSims, hostUpnp, hostPvp, maxSims }) c.Enabled = false;
        }
        if (session.GameStuck) { Log("closing the crashed game..."); session.KillGame(); }
        if (!session.GameRunning) session.LaunchGame(dolphin);
        play.Text = "Game running";
    }

    void TeleportToSelected()
    {
        if (session == null || members.SelectedItems.Count == 0) return;
        string who = members.SelectedItems[0].Text;
        if (who == name.Text) { Log("that's you"); return; }
        Log(session.TeleportTo(who));
    }

    static Color SafeColor(string c) { try { return ColorTranslator.FromHtml(c); } catch { return Color.Black; } }

    void Log(string s) => Ui(() =>
    {
        logBox.AppendText($"{DateTime.Now:HH:mm:ss}  {s}{Environment.NewLine}");
    });

    void Ui(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(a);
        else a();
    }

    Upnp? upnp;

    void Shutdown()
    {
        upnp?.Dispose();
        session?.Dispose();
        server?.Dispose();
    }
}
