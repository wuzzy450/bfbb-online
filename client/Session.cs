using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BfbbOnline.Shared;

namespace BfbbOnline.Client;

public sealed class Peer
{
    public int Id;
    public string Name = "";
    public string Color = "#ffff00";
    public string Scene = "";
    public PlayerState State;
    public long Seq;          // bumped per received "st"
    public long Written;      // Seq last written into the puppet slot
    public DateTime Updated;
    public int Slot = -1;
}

/// <summary>One play session: the server connection plus the running game.</summary>
public sealed class Session : IDisposable
{
    readonly Config cfg;
    readonly Action<string> log;
    readonly CancellationTokenSource cts = new();
    readonly object gate = new();
    readonly Dictionary<int, Peer> peers = new();
    JsonConnection? conn;
    volatile bool joined;
    int myId;
    Process? dolphin;
    GameLink? link;
    Thread? gameThread;
    Dictionary<uint, byte[]>? baseline;
    TaskCompletionSource<JsonObject>? pendingWorld;
    int loadCounter;
    uint handledLoadSeq;
    volatile bool reloadAfterJoin;
    long lastStateFrame = -1;
    readonly List<ObjState> objIn = new();   // states from the party waiting for the game
    uint syncedScene, syncedReset;
    int objSent, objGot;

    public event Action<string>? StatusChanged;
    public event Action<IReadOnlyList<(string name, string color, string scene)>>? MembersChanged;
    public event Action<string, string, string>? Chat;   // name, color, text
    public event Action<string>? Notice;

    public string Status { get; private set; } = "";
    /// <summary>Log every object/inventory message (self-test).</summary>
    public bool Verbose;
    public GameLink? Link => link;
    public int DolphinPid => dolphin?.Id ?? 0;
    public bool GameRunning => dolphin is { HasExited: false };
    /// <summary>The game stopped advancing (a Dolphin error popup, a hang) while Dolphin stays open.</summary>
    public bool GameStuck { get; private set; }
    /// <summary>"running", "stuck" or "closed".</summary>
    public event Action<string>? GameStateChanged;

    /// <summary>Closes a stuck game (waits up to 5 s).</summary>
    public void KillGame()
    {
        try { if (dolphin is { HasExited: false } d) { d.Kill(); d.WaitForExit(5000); } } catch { }
        gameThread?.Join(3000);   // its "closed" must come before the new game's "running"
    }

    public Session(Config cfg, Action<string> log)
    {
        this.cfg = cfg;
        // everything we log also goes to the host (with Dolphin's own errors): one place to debug from
        this.log = s => { log(s); QueueRemoteLog(s); };
        // sent on a timer, not from the game loop: a game that crashed or closed still gets its last
        // lines (and Dolphin's) to the host
        logTimer = new Timer(_ => { try { FlushRemoteLog(); } catch { } }, null, 1000, 1000);
    }
    readonly Timer logTimer;

    // ---- game window on the chosen monitor ---------------------------------------------------
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumWinProc cb, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out WinRect r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    delegate bool EnumWinProc(IntPtr h, IntPtr l);
    struct WinRect { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// Moves Dolphin's windows onto the chosen screen once they appear (Dolphin restores its own
    /// saved window position, so its config alone isn't enough). Keeps each window's size.
    /// </summary>
    static void MoveGameWindow(Process p, System.Windows.Forms.Screen screen)
    {
        var area = screen.WorkingArea;
        new Thread(() =>
        {
            var moved = new HashSet<IntPtr>();
            var until = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < until && !p.HasExited)
            {
                EnumWindows((h, _) =>
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid != (uint)p.Id || !IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero || moved.Contains(h)) return true;   // 4 = GW_OWNER: skip popups
                    if (!GetWindowRect(h, out var r) || r.Right - r.Left < 200) return true;
                    int w = Math.Min(r.Right - r.Left, area.Width), hgt = Math.Min(r.Bottom - r.Top, area.Height);
                    int x = area.X + (area.Width - w) / 2, y = area.Y + (area.Height - hgt) / 2;
                    if (!area.Contains(r.Left + (r.Right - r.Left) / 2, r.Top + (r.Bottom - r.Top) / 2))
                        SetWindowPos(h, IntPtr.Zero, x, y, w, hgt, 0x0004 | 0x0010);   // NOZORDER | NOACTIVATE
                    moved.Add(h);
                    return true;
                }, IntPtr.Zero);
                Thread.Sleep(500);
            }
        }) { IsBackground = true, Name = "move game window" }.Start();
    }

    // ---- logs for the host ----------------------------------------------------------------
    readonly Queue<string> remoteLog = new();
    void QueueRemoteLog(string s)
    {
        lock (remoteLog) { if (remoteLog.Count < 500) remoteLog.Enqueue(s); }
    }

    /// <summary>Sends queued log lines (ours and new lines of Dolphin's log) to the server.</summary>
    readonly object flushGate = new();
    void FlushRemoteLog()
    {
        lock (flushGate) FlushRemoteLogLocked();
    }
    void FlushRemoteLogLocked()
    {
        ReadDolphinLog();
        if (conn == null || !joined) return;
        var lines = new System.Text.Json.Nodes.JsonArray();
        lock (remoteLog) while (remoteLog.Count > 0 && lines.Count < 40) lines.Add(remoteLog.Dequeue());
        if (lines.Count == 0) return;
        var m = Proto.Msg("plog"); m["l"] = lines;
        Send(m);
    }

    string DolphinLogPath => System.IO.Path.Combine(UserDir ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dolphin Emulator"), "Logs", "dolphin.log");
    long dolphinLogPos = -1;
    DateTime nextLogRead;
    void ReadDolphinLog()
    {
        if (DateTime.UtcNow < nextLogRead) return;
        nextLogRead = DateTime.UtcNow.AddSeconds(1);
        try
        {
            using var f = new System.IO.FileStream(DolphinLogPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            if (dolphinLogPos < 0 || dolphinLogPos > f.Length) dolphinLogPos = f.Length;   // only what's new from now on
            if (f.Length == dolphinLogPos) return;
            f.Position = dolphinLogPos;
            using var r = new System.IO.StreamReader(f);
            string? line;
            int n = 0;
            while ((line = r.ReadLine()) != null && n++ < 200)
                if (line.Length > 0) lock (remoteLog) { if (remoteLog.Count < 500) remoteLog.Enqueue("[dolphin] " + line); }
            dolphinLogPos = f.Length;
        }
        catch { }
    }

    void SetStatus(string s) { Status = s; StatusChanged?.Invoke(s); }

    // ---- network -----------------------------------------------------------------------------
    public void Connect(string host, int port)
    {
        _ = Task.Run(async () =>
        {
            int delay = 1;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    SetStatus($"connecting to {host}:{port}...");
                    var c = await JsonConnection.ConnectAsync(host, port, cts.Token);
                    var closed = new TaskCompletionSource<string>();
                    c.Received += OnMessage;
                    c.Closed += why => closed.TrySetResult(why);
                    c.Start();
                    conn = c;
                    var hello = Proto.Msg("hello");
                    hello["name"] = cfg.Name; hello["color"] = cfg.Color; hello["version"] = Proto.Version;
                    c.Send(hello);
                    delay = 1;
                    string why = await closed.Task.WaitAsync(cts.Token);
                    joined = false;
                    conn = null;
                    pendingWorld?.TrySetCanceled();
                    lock (gate) peers.Clear();
                    MembersChanged?.Invoke(Array.Empty<(string, string, string)>());
                    if (cts.IsCancellationRequested) return;
                    SetStatus($"disconnected ({why}), reconnecting...");
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    SetStatus($"can't reach {host}:{port} ({e.Message}), retrying in {delay}s");
                }
                try { await Task.Delay(TimeSpan.FromSeconds(delay), cts.Token); } catch { return; }
                delay = Math.Min(delay * 2, 15);
            }
        });
    }

    void Send(JsonObject o) => conn?.Send(o);

    void OnMessage(JsonObject o)
    {
        switch (Proto.Type(o))
        {
            case "welcome":
                myId = o["id"]!.GetValue<int>();
                log($"connected as {o["name"]}");
                var j = Proto.Msg("join"); j["party"] = cfg.Party;
                Send(j);
                break;
            case "joined":
                joined = true;
                baseline = null;
                bool fresh = o["fresh"]?.GetValue<bool>() ?? false;
                log(fresh ? $"party \"{o["party"]}\" is new: your progress starts it"
                          : $"joined party \"{o["party"]}\": loading the party's progress");
                SetStatus($"in party {o["party"]}");
                // Already playing a level: reload it once so it comes back with the party's
                // progress. Joining before (or during) the first load needs no reload, the load
                // handshake brings the party's world in.
                reloadAfterJoin = !fresh && link != null && link.TryReadLocal(out var now) && now.InGame && !link.LoadPending;
                break;
            case "members":
                var list = new List<(string, string, string)>();
                if (o["members"] is JsonArray arr)
                    foreach (var m in arr.OfType<JsonObject>())
                    {
                        int id = m["id"]!.GetValue<int>();
                        string name = m["name"]?.GetValue<string>() ?? "?", color = m["color"]?.GetValue<string>() ?? "#ffffff",
                            scene = m["s"]?.GetValue<string>() ?? "";
                        list.Add((name, color, scene));
                        if (id == myId) continue;
                        lock (gate)
                        {
                            if (!peers.TryGetValue(id, out var p)) peers[id] = p = new Peer { Id = id };
                            p.Name = name; p.Color = color; p.Scene = scene;
                        }
                    }
                lock (gate)
                {
                    var ids = (o["members"] as JsonArray)?.OfType<JsonObject>().Select(m => m["id"]!.GetValue<int>()).ToHashSet() ?? new();
                    foreach (var gone in peers.Keys.Where(k => !ids.Contains(k)).ToList()) peers.Remove(gone);
                }
                MembersChanged?.Invoke(list);
                break;
            case "st":
                OnPeerState(o);
                break;
            case "world":
                pendingWorld?.TrySetResult(o);
                break;
            case "obj":
                if (ObjState.FromJson(o) is { } st)
                {
                    lock (objIn) objIn.Add(st);
                    if (Verbose) log($"obj in  #{st.Index} {st.Id:X8} type {st.Type:X2} {st.Bits} bits");
                }
                break;
            case "cs":
                // a teammate in our scene started a story cutscene: watch it with them
                if (cfg.WatchAlong && o["id"]?.GetValue<uint>() is uint csid && link != null
                    && link.TryReadLocal(out var meCs) && PlayerState.SceneName(meCs.SceneId) == o["s"]?.GetValue<string>())
                {
                    log($"{o["name"]} started a cutscene, watching along");
                    link.PlayCutscene(csid);
                }
                break;
            case "np":
                // poses of the enemies a teammate's game drives (nearest to them)
                if (o["d"]?.GetValue<string>() is string npd && o["s"]?.GetValue<string>() is string nps)
                {
                    var raw = Convert.FromBase64String(npd);
                    var npNow = DateTime.UtcNow;
                    uint nsc = PlayerState.SceneIdOf(nps);
                    lock (npcIn)
                    {
                        if (nsc != npcInScene) { npcIn.Clear(); npcInScene = nsc; }
                        for (int k = 0; k + Mb.NpcPoseSize <= raw.Length; k += Mb.NpcPoseSize)
                        {
                            ushort idx = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(k));
                            npcIn[idx] = (raw.AsSpan(k, Mb.NpcPoseSize).ToArray(), npNow);
                        }
                        npcInDirty = true;
                    }
                }
                break;
            case "pay":
                // a teammate paid (a shiny door...): replay what that set off, now or when we get there
                if (o["s"]?.GetValue<string>() is string pscene && o["id"]?.GetValue<uint>() is uint pid
                    && o["ev"]?.GetValue<uint>() is uint pev)
                    lock (pays)
                    {
                        if (!pays.TryGetValue(pscene, out var set)) pays[pscene] = set = new();
                        set.Add((pid, pev));
                    }
                break;
            case "spat":
                // the party's spatula count (the server counts each spatula once)
                if (o["n"]?.GetValue<uint>() is uint sp) partySpatulas = sp;
                break;
            case "inv":
                if (o["d"] is JsonArray invd)
                    lock (invIn)
                        foreach (var e in invd.OfType<JsonArray>())
                            if (e.Count == 2) invIn.Add((e[0]!.GetValue<int>(), e[1]!.GetValue<int>()));
                break;
            case "objs":
                if (o["list"] is JsonArray objs)
                    lock (objIn)
                        foreach (var x in objs.OfType<JsonObject>())
                            if (ObjState.FromJson(x) is { } st2) objIn.Add(st2);
                break;
            case "chat":
                Chat?.Invoke(o["name"]?.GetValue<string>() ?? "?", o["color"]?.GetValue<string>() ?? "#ffffff", o["text"]?.GetValue<string>() ?? "");
                break;
            case "error":
                log($"server: {o["msg"]}");
                break;
            case "notice":
                log($"server: {o["text"]}");
                Notice?.Invoke(o["text"]?.GetValue<string>() ?? "");
                break;
            case "talk":
                // a teammate in our scene started a conversation: offer to watch along
                if (cfg.WatchAlong && o["id"]?.GetValue<uint>() is uint tid && link != null
                    && link.TryReadLocal(out var meT) && PlayerState.SceneName(meT.SceneId) == o["s"]?.GetValue<string>())
                {
                    string who = o["name"]?.GetValue<string>() ?? "A teammate";
                    log($"{who} is talking to someone: watch along? (D-pad Up yes, Down no)");
                    link.OfferTalk(tid, $"{(who.Length > 12 ? who[..12] : who)}: watch along? Up yes, Down no");
                }
                break;
        }
    }

    void OnPeerState(JsonObject o)
    {
        int id = o["id"]?.GetValue<int>() ?? 0;
        var s = new PlayerState { SceneId = PlayerState.SceneIdOf(o["s"]?.GetValue<string>() ?? "") };
        if (o["p"] is JsonArray p && p.Count == 3) { s.X = F(p[0]); s.Y = F(p[1]); s.Z = F(p[2]); }
        if (o["v"] is JsonArray v && v.Count == 3) { s.Vx = F(v[0]); s.Vy = F(v[1]); s.Vz = F(v[2]); }
        s.Yaw = F(o["y"]);
        s.Buttons = o["b"]?.GetValue<uint>() ?? 0;
        s.Character = o["ch"]?.GetValue<uint>() ?? 0;
        s.Health = o["hp"]?.GetValue<uint>() ?? 3;
        s.ControlOff = o["co"] != null ? 1u : 0u;
        if (o["a"] is JsonArray a && a.Count == 4) { s.Ax = (sbyte)(int)F(a[0]); s.Ay = (sbyte)(int)F(a[1]); s.Cx = (sbyte)(int)F(a[2]); s.Cy = (sbyte)(int)F(a[3]); }
        if (o["c"] is JsonArray c && c.Count == 9) { s.Cam = new float[9]; for (int i = 0; i < 9; i++) s.Cam[i] = F(c[i]); }
        lock (gate)
        {
            if (!peers.TryGetValue(id, out var peer)) peers[id] = peer = new Peer { Id = id };
            peer.State = s;
            peer.Scene = PlayerState.SceneName(s.SceneId);
            peer.Seq++;
            peer.Updated = DateTime.UtcNow;
        }
    }

    static float F(JsonNode? n) => n == null ? 0 : (float)n.GetValue<double>();

    public void SendChat(string text)
    {
        var c = Proto.Msg("chat"); c["text"] = text;
        Send(c);
    }

    // ---- game ----------------------------------------------------------------------------------
    /// <summary>Test/bot options: a separate Dolphin user folder, and a scene to boot straight into.</summary>
    public string? UserDir;
    public uint BootScene;
    public string? SaveState;

    public void LaunchGame(string dolphinPath)
    {
        if (GameRunning) return;
        var psi = new ProcessStartInfo(dolphinPath) { UseShellExecute = false };
        if (UserDir != null) { psi.ArgumentList.Add("-u"); psi.ArgumentList.Add(UserDir); }
        if (SaveState != null) { psi.ArgumentList.Add("-s"); psi.ArgumentList.Add(SaveState); }
        foreach (var a in new[] { "-b", "-e", Installer.GameDol, "-C", "Dolphin.Interface.SkipNKitWarning=True" }.Concat(DolphinMemory.LaunchArgs))
            psi.ArgumentList.Add(a);
        // Dolphin's log file with warnings and errors (crash popups are logged too): sent to the host
        foreach (var a in new[] { "-C", "Logger.Options.WriteToFile=True", "-C", "Logger.Options.Verbosity=3",
                                  "-C", "Logger.Logs.MASTER=True", "-C", "Logger.Logs.POWERPC=True", "-C", "Logger.Logs.CORE=True" })
            psi.ArgumentList.Add(a);
        dolphinLogPos = -1;
        // its own memory card (a GCI folder next to the launcher): the player's normal card often
        // holds the game's 3 save files already and the game then stops at "not enough space" on
        // boot. Online progress is kept by the server; this card only takes the game's own saves.
        if (UserDir == null)
        {
            string card = System.IO.Path.Combine(AppContext.BaseDirectory, "memcard");
            System.IO.Directory.CreateDirectory(card);
            foreach (var a in new[] { "-C", "Dolphin.Core.SlotA=8", "-C", $"Dolphin.Core.GCIFolderAPathOverride={card}" })
                psi.ArgumentList.Add(a);
        }
        // the game window on the chosen monitor (fullscreen also goes to the monitor the window is on)
        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == cfg.Monitor);
        if (screen != null)
        {
            var b = screen.WorkingArea;
            foreach (var a in new[] { "-C", $"Dolphin.Display.RenderWindowXPos={b.X + 40}", "-C", $"Dolphin.Display.RenderWindowYPos={b.Y + 40}",
                                      "-C", "Dolphin.Interface.RenderToMain=False" })
                psi.ArgumentList.Add(a);
        }
        dolphin = Process.Start(psi) ?? throw new InvalidOperationException("could not start Dolphin");
        if (screen != null) MoveGameWindow(dolphin, screen);
        log("started Dolphin");
        gameThread = new Thread(GameLoop) { IsBackground = true, Name = "game link" };
        gameThread.Start();
    }

    void GameLoop()
    {
        try
        {
            link = GameLink.Attach(dolphin!.Id, TimeSpan.FromSeconds(90), cts.Token);
            if (link == null) { log("couldn't find the patched game in Dolphin's memory"); return; }
            link.Hello(Mb.ModeClient, BootScene, loadWaitFrames: 600);
            log("linked to the game");
            _ = Task.Run(() =>
            {
                // the extra RAM is mapped at boot: report it once the first scene has puppets
                for (int k = 0; k < 120 && link != null; k++)
                {
                    Thread.Sleep(500);
                    try
                    {
                        if (link == null || link.CtxReady == 0) continue;
                        log(link.HighRamMapped ? "memory: extra RAM in use (teammates fit in every level)"
                            : "memory: Dolphin didn't give the game extra RAM: big levels can only show a few teammates");
                        return;
                    }
                    catch { }
                }
            });
            int errors = 0;
            uint lastFrames = 0;
            var lastAdvance = DateTime.UtcNow;
            GameStuck = false;
            GameStateChanged?.Invoke("running");
            while (!cts.IsCancellationRequested && !dolphin.HasExited)
            {
                // frames stop for 20 s with Dolphin still open: crashed (error popup) or frozen
                uint fr = 0;
                bool loading = false;
                try { fr = link.Frames; loading = link.LoadPending; } catch { }
                if (fr != lastFrames || loading) { lastFrames = fr; lastAdvance = DateTime.UtcNow; }
                bool stuck = fr != 0 && DateTime.UtcNow - lastAdvance > TimeSpan.FromSeconds(20);
                if (stuck != GameStuck)
                {
                    GameStuck = stuck;
                    log(stuck ? "the game stopped responding (crashed, frozen or paused). Click Restart game to start it again"
                              : "the game is running again");
                    GameStateChanged?.Invoke(stuck ? "stuck" : "running");
                }
                if (stuck) { Thread.Sleep(250); continue; }
                try
                {
                    if (!link.Bridged) link.Hello(Mb.ModeClient, BootScene, loadWaitFrames: 600);
                    if (link.LoadPending && link.LoadSeq != handledLoadSeq) LoadHandshake();
                    if (link.TryReadLocal(out var me)) Tick(me);
                    errors = 0;
                }
                catch (Exception e) when (!cts.IsCancellationRequested && !dolphin.HasExited && ++errors < 200)
                {
                    // one bad frame (odd values mid scene change...) must not end the session
                    if (errors == 1 || errors % 50 == 0) log($"game link: {e.GetType().Name}: {e.Message} (continuing)");
                    Thread.Sleep(50);
                }
                Thread.Sleep(8);
            }
        }
        catch (Exception e) when (!cts.IsCancellationRequested)
        {
            if (dolphin is { HasExited: false }) log($"game link error: {e.Message}");
        }
        finally
        {
            link?.Dispose();
            link = null;
            GameStuck = false;
            if (!cts.IsCancellationRequested) log("game closed");
            GameStateChanged?.Invoke("closed");
        }
    }

    /// <summary>The game is about to load a scene and waits for the party's world.</summary>
    void LoadHandshake()
    {
        uint seq = link!.LoadSeq;
        handledLoadSeq = seq;
        uint scene = link.LoadScene;
        if (!joined || conn == null) { link.AckLoad(seq); return; }
        try
        {
            FlushCounters();
            var mine = link.ExportWorld();
            var up = new Dictionary<uint, byte[]>();
            foreach (var (tag, b) in mine)
                if (baseline == null || !baseline.TryGetValue(tag, out var bas) || !bas.AsSpan().SequenceEqual(b))
                    up[tag] = b;
            var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingWorld = tcs;
            var m = Proto.Msg("load");
            m["seq"] = ++loadCounter;
            m["s"] = PlayerState.SceneName(scene);
            m["ch"] = link.TryReadLocal(out var me) ? me.Character : 0;
            var upj = new JsonObject();
            foreach (var (tag, b) in up) upj[PlayerState.SceneName(tag)] = Convert.ToBase64String(b);
            m["up"] = upj;
            Send(m);
            if (!tcs.Task.Wait(TimeSpan.FromSeconds(8)))
            {
                log("server didn't answer in time; loading with your own progress");
                return;
            }
            var world = WorldFromJson(tcs.Task.Result["slots"] as JsonObject);
            if (link.ImportWorld(world, TimeSpan.FromSeconds(3)))
            {
                baseline = world;
                importedPlyr = world.TryGetValue(PlyrSlot.Tag, out var ip) ? ip : null;
                lock (invIn) invIn.Clear();   // the party's changes so far are in this world
            }
            log($"{PlayerState.SceneName(scene)}: synced party progress ({up.Count} changes up, {world.Count} slots down)");
        }
        catch (Exception e)
        {
            log($"progress sync failed: {e.Message}");
        }
        finally
        {
            pendingWorld = null;
            lastCounters = null;
            syncedScene = 0;
            link.AckLoad(seq);
        }
    }

    static Dictionary<uint, byte[]> WorldFromJson(JsonObject? o)
    {
        var d = new Dictionary<uint, byte[]>();
        if (o == null) return d;
        foreach (var (k, v) in o)
            if (k.Length == 4 && v is JsonValue jv && jv.TryGetValue<string>(out var s))
                d[PlayerState.SceneIdOf(k)] = Convert.FromBase64String(s);
        return d;
    }

    // --- teleport to a teammate ---------------------------------------------------------------
    int tpPeer;                 // peer we're travelling to (0 = none)
    DateTime tpUntil, tpAfter;
    // Boss arenas the game can't be warped into cold (the stock game crashes too): only the story path
    static readonly HashSet<string> NoWarpScenes = new() { "B301", "B302", "B303" };

    /// <summary>
    /// Puts our player next to a teammate: same scene = just move there; another scene = warp to it
    /// first, then move to where they are once the scene has loaded.
    /// </summary>
    public string TeleportTo(string name)
    {
        Peer? p;
        lock (gate) p = peers.Values.FirstOrDefault(x => x.Name == name);
        if (p == null) return $"{name} isn't in the party";
        if (link == null || !link.TryReadLocal(out var me) || !me.InGame) return "start the game first";
        // the scene the server last saw them load (their state samples pause during loads and cutscenes)
        if (p.Scene.Length != 4) return $"{name} isn't in a level right now";
        uint target = PlayerState.SceneIdOf(p.Scene);
        if (p.Scene != PlayerState.SceneName(me.SceneId) && NoWarpScenes.Contains(p.Scene))
            return $"{name} is in {p.Scene}, which can only be reached through the story";
        tpPeer = p.Id;
        tpUntil = DateTime.UtcNow.AddSeconds(180);
        tpAfter = DateTime.UtcNow;
        if (target != me.SceneId)
        {
            link.Warp(target);
            return $"travelling to {name} in {p.Scene}...";
        }
        return $"teleporting to {name}";
    }

    void TeleportTick(in PlayerState me)
    {
        if (tpPeer == 0) return;
        Peer? p;
        lock (gate) peers.TryGetValue(tpPeer, out p);
        if (p == null || DateTime.UtcNow > tpUntil) { tpPeer = 0; return; }
        // wait for the scene (and our control after its intro), and for a fresh position of theirs
        // from this scene while they can move (not in a cutscene)
        if (p.State.SceneId != me.SceneId || link!.LoadPending || me.ControlOff != 0) return;
        if (p.Updated < tpAfter || p.State.ControlOff != 0 || DateTime.UtcNow - p.Updated > TimeSpan.FromSeconds(1)) return;
        link.Teleport(p.State.X, p.State.Y + 0.5f, p.State.Z);
        log($"teleported to {p.Name}");
        tpPeer = 0;
    }

    void Tick(in PlayerState me)
    {
        if (!me.InGame) return;
        TeleportTick(me);
        if (reloadAfterJoin && joined)
        {
            reloadAfterJoin = false;
            link!.Warp(me.SceneId, reload: true);
            return;
        }
        // buttons pressed between two sends (a quick tap: bubble bash, bounce...) are sent as held
        // once, or teammates' games would never see them
        if (me.Frame != lastButtonFrame) { lastButtonFrame = me.Frame; tappedButtons |= me.Buttons; }
        // our state, ~20 times a second (every 3rd game frame at 60 fps)
        if (me.Frame != lastStateFrame && (me.Frame % 3 == 0 || me.Frame - lastStateFrame >= 3) && joined)
        {
            lastStateFrame = me.Frame;
            var st = Proto.Msg("st");
            st["s"] = PlayerState.SceneName(me.SceneId);
            st["p"] = Proto.Floats(me.X, me.Y, me.Z);
            st["y"] = Proto.Finite(MathF.Round(me.Yaw, 4));
            st["v"] = Proto.Floats(me.Vx, me.Vy, me.Vz);
            st["b"] = me.Buttons | tappedButtons;
            tappedButtons = 0;
            st["a"] = new JsonArray(me.Ax, me.Ay, me.Cx, me.Cy);
            st["c"] = Proto.Floats(me.Cam);
            st["ch"] = me.Character;
            st["hp"] = me.Health;
            if (me.ControlOff != 0) st["co"] = 1;
            Send(st);
        }
        UpdatePuppets(me.SceneId);
        SyncObjects(me.SceneId);
        SyncNpcs(me.SceneId);
        uint cs = link!.CutsceneSeq;
        if (cs != lastCutsceneSeq)
        {
            bool first = lastCutsceneSeq == uint.MaxValue;
            lastCutsceneSeq = cs;
            var (id, sc) = link.LastCutscene;
            if (!first && joined)
            {
                var m = Proto.Msg("cs"); m["s"] = PlayerState.SceneName(sc); m["id"] = id;
                Send(m);
            }
        }
        PayTick(me);
        uint released = link.DbgRender(31);
        if (released != lastReleased)
        {
            if (lastReleased != uint.MaxValue)
                log($"your character was frozen for 15 s with nothing going on: gave control back (held by 0x{link.DbgRender(30):X})");
            lastReleased = released;
        }
        uint ts = link.TalkSeq;
        if (ts != lastTalkSeq)
        {
            bool first = lastTalkSeq == uint.MaxValue;
            lastTalkSeq = ts;
            var (id, sc) = link.LastTalk;
            if (!first && joined)
            {
                var m = Proto.Msg("talk"); m["s"] = PlayerState.SceneName(sc); m["id"] = id;
                Send(m);
            }
        }
    }

    uint tappedButtons, lastButtonFrame;
    uint lastReleased = uint.MaxValue, lastCutsceneSeq = uint.MaxValue, lastTalkSeq = uint.MaxValue, lastPaySeq = uint.MaxValue;

    // Payments (shiny doors...) made in the party, per scene: replayed in our game whenever we are
    // in that scene (the game closes those doors again on every load and scene reset).
    readonly Dictionary<string, HashSet<(uint id, uint ev)>> pays = new();
    readonly HashSet<(uint, uint)> paysApplied = new();
    (uint scene, uint reset) payVisit;
    DateTime payVisitStart;

    void PayTick(in PlayerState me)
    {
        uint ps = link!.PaySeq;
        if (ps != lastPaySeq)
        {
            bool first = lastPaySeq == uint.MaxValue;
            lastPaySeq = ps;
            var (id, ev, sc) = link.LastPay;
            if (!first && joined)
            {
                var m = Proto.Msg("pay"); m["s"] = PlayerState.SceneName(sc); m["id"] = id; m["ev"] = ev;
                Send(m);
                lock (pays) paysApplied.Add((id, ev));   // done in our game already
            }
        }
        var visit = (me.SceneId, link.ResetSeq);
        if (visit != payVisit) { payVisit = visit; payVisitStart = DateTime.UtcNow; lock (pays) paysApplied.Clear(); }
        // after the scene settled (the party's object states are in)
        if (!joined || link.LoadPending || DateTime.UtcNow - payVisitStart < TimeSpan.FromSeconds(1.5)) return;
        lock (pays)
        {
            if (!pays.TryGetValue(PlayerState.SceneName(me.SceneId), out var set)) return;
            foreach (var k in set)
                if (!paysApplied.Contains(k) && link.ReplayPay(k.id, k.ev))
                {
                    paysApplied.Add(k);
                    log("a teammate's payment opened something here");
                }
        }
    }
    uint[]? lastCounters;
    readonly List<(int i, int d)> invIn = new();

    /// <summary>
    /// Our own counter changes out, the party's in (the patch applies them between frames).
    /// <c>lastCounters</c> is what the party has as far as we know, and what our game should show.
    /// </summary>
    void SyncInventory()
    {
        var cur = link!.ReadCounters();
        var now = DateTime.UtcNow;
        if (lastCounters == null || !joined)
        {
            // a batch of teammates' changes handed to the game before the load is still waiting to be
            // applied: let it land first (the party's values below already include it; setting them
            // now let it be added on top: chaos test, party 75 -> game 85)
            if (!link.CountersApplied) return;
            // new scene: the game may or may not have taken its counters from the imported save slot
            // (and may still overwrite them): set the party's values outright, then let them settle
            lastCounters = (uint[])cur.Clone();
            countersReset = link.ResetSeq;
            // teammates' changes handed to the game after the pre-load flush aren't ours: they are
            // already in the party's world (chaos test: a +1 in flight during a load was counted twice)
            if (flushed != null)
                foreach (var (i, d) in invApplied) if (i < flushed.Length) flushed[i] = (uint)Math.Max(0, (long)flushed[i] + d);
            invApplied.Clear();
            if (importedPlyr is { } pl && joined)
            {
                var map = PlyrSlot.LiveCounters();
                var gained = new JsonArray();
                for (int i = 0; i < map.Length && i < lastCounters.Length; i++)
                {
                    uint party = SerialBits.GetValue(pl, map[i].bit, 4);
                    if (Verbose && i < 3 && (cur[i] != party || (flushed != null && flushed[i] != party)))
                        log($"inv: scene start counter {i}: game {cur[i]} party {party} flushed {(flushed != null ? flushed[i].ToString() : "-")} frames {link.Frames} applied {link.CountersApplied}");
                    // our game changed this after our last report (a level-end reward during the load):
                    // a change of ours, not something to overwrite
                    if (flushed != null && i != 1 && cur[i] != party && cur[i] != flushed[i])
                    {
                        long d = (long)cur[i] - flushed[i];
                        if (Math.Abs(d) <= 5000 && !(d < 0 && NeverDecrease.Contains(i)))
                        {
                            gained.Add(new JsonArray(i, (int)d));
                            party = (uint)Math.Max(0, party + d);
                        }
                    }
                    lastCounters[i] = party;
                }
                if (gained.Count > 0) { var m = Proto.Msg("inv"); m["d"] = gained; Send(m); log($"inv: changes our game made while loading: {gained.ToJsonString()}"); }
                importedPlyr = null;
            }
            flushed = null;
            Array.Clear(settleTries);
            partySpatulas = null;
            spatulaOffSince = null;
            SetCounters(Enumerable.Range(0, cur.Length), "scene start");
            return;
        }
        // something we handed the game is still on its way: judge nothing until it's in
        if (fixingCounters)
        {
            if (!link.CountersApplied) return;
            fixingCounters = false;
            if (invApplied.Count > 0)
            {
                foreach (var (i, d) in invApplied)
                {
                    lastCounters[i] = (uint)Math.Max(0, (long)lastCounters[i] + d);
                    if (flushed != null && i < flushed.Length) flushed[i] = (uint)Math.Max(0, (long)flushed[i] + d);
                }
                invApplied.Clear();
            }
            return;
        }
        // a scene reset (died, out of bounds) or the game's own loading put counters back: not a
        // change of ours, put the party's values back
        uint reset = link.ResetSeq;
        bool resetNow = reset != countersReset;
        countersReset = reset;
        var wrong = Enumerable.Range(0, cur.Length).Where(i => cur[i] != lastCounters[i]).ToList();
        if (wrong.Count > 0 && (resetNow || now < settleUntil))
        {
            // a value the game won't take (max health is kept within 3..6) after a few tries is the game's
            foreach (int i in wrong) if (++settleTries[i] > 3) { lastCounters[i] = cur[i]; settleTries[i] = 0; }
            wrong = wrong.Where(i => cur[i] != lastCounters[i]).ToList();
            if (wrong.Count > 0) SetCounters(wrong, resetNow ? "scene reset" : "settling");
            return;
        }
        // spatulas: the server's count is the truth. Our game's own extra (it awarded itself one
        // through synced level logic) goes after a few seconds; the server's newer count right away
        if (partySpatulas is uint ps && ps != lastCounters[1]) { lastCounters[1] = ps; SetCounters(new[] { 1 }, "party spatulas"); return; }
        // the "+10 spatulas" cheat (only works on the pause menu): the server adds them for the party
        if (cur[1] > lastCounters[1] && cur[1] == Math.Min(100u, lastCounters[1] + 10) && link.Mem.ReadU32(0x803CB8A8) == GameModePause)
        {
            var m = Proto.Msg("cheat"); m["spat"] = (int)(cur[1] - lastCounters[1]); Send(m);
            log($"cheat: +{cur[1] - lastCounters[1]} spatulas for the party");
            lastCounters[1] = cur[1];
            spatulaOffSince = null;
            return;
        }
        if (cur[1] != lastCounters[1])
        {
            if (cur[1] < lastCounters[1] || (spatulaOffSince is DateTime t0 && now - t0 > TimeSpan.FromSeconds(3)))
            { spatulaOffSince = null; SetCounters(new[] { 1 }, "spatulas are counted by the server"); return; }
            spatulaOffSince ??= now;
        }
        else spatulaOffSince = null;
        wrong.Remove(1);
        var arr = new JsonArray();
        var restore = new List<int>();
        foreach (int i in wrong)
        {
            long d = (long)cur[i] - lastCounters[i];
            if (Math.Abs(d) > 5000) { if (Verbose) log($"inv: ignored jump of counter {i} by {d}"); restore.Add(i); continue; }
            // spatulas, socks and max health never go down: that's the game resetting them, not a loss
            if (d < 0 && NeverDecrease.Contains(i)) { restore.Add(i); continue; }
            arr.Add(new JsonArray(i, (int)d));
            lastCounters[i] = cur[i];
            if (Verbose) log($"inv out counter {i} {d:+#;-#;0}");
        }
        if (arr.Count > 0) { var m = Proto.Msg("inv"); m["d"] = arr; Send(m); }
        if (restore.Count > 0) { SetCounters(restore, "the game lowered a counter that never goes down"); return; }
        lock (invIn)
        {
            if (invIn.Count > 0 && link.TryAddCounters(invIn))
            {
                if (Verbose) foreach (var (i, d) in invIn) log($"inv in  counter {i} {d:+#;-#;0}");
                invApplied.AddRange(invIn);
                invIn.Clear();
                fixingCounters = true;
            }
        }
    }

    /// <summary>
    /// Before a scene-load upload: counter changes our game made since the last look (e.g. a reward
    /// given during the level-end sequence) go out live now; uploads don't carry counters.
    /// </summary>
    void FlushCounters()
    {
        if (!joined || lastCounters == null || fixingCounters) return;
        var cur = link!.ReadCounters();
        var arr = new JsonArray();
        for (int i = 0; i < cur.Length && i < lastCounters.Length; i++)
        {
            long d = (long)cur[i] - lastCounters[i];
            if (d == 0 || i == 1 || Math.Abs(d) > 5000 || (d < 0 && NeverDecrease.Contains(i))) continue;
            arr.Add(new JsonArray(i, (int)d));
            lastCounters[i] = cur[i];
        }
        if (arr.Count > 0) { var m = Proto.Msg("inv"); m["d"] = arr; Send(m); if (Verbose) log($"inv out before loading: {arr.ToJsonString()}"); }
        flushed = (uint[])cur.Clone();
    }

    uint[]? flushed;   // our game's counters when we last reported before a load

    /// <summary>Sets these counters in the game to <c>lastCounters</c> (absolute), and lets them settle.</summary>
    void SetCounters(IEnumerable<int> which, string why)
    {
        var vals = which.Select(i => (i, lastCounters![i])).ToList();
        if (vals.Count == 0 || !link!.TrySetCounters(vals)) return;
        fixingCounters = true;
        if (why == "scene start") settleUntil = DateTime.UtcNow.AddSeconds(1.5);
        if (Verbose || why != "settling") log($"inv: {why}: set {vals.Count} counters to the party's (shinies {lastCounters![0]}, spatulas {lastCounters[1]})");
    }

    const uint GameModePause = 7;   // gGameMode (eGameMode_Pause): cheats are entered on the pause menu
    DateTime settleUntil;
    uint? partySpatulas;
    DateTime? spatulaOffSince;
    readonly int[] settleTries = new int[64];
    readonly List<(int i, int d)> invApplied = new();
    byte[]? importedPlyr;
    bool fixingCounters;
    uint countersReset;
    /// <summary>Counters the game never lowers: spatulas, socks (total and per level), max health.</summary>
    static readonly int[] NeverDecrease = new[] { 1, 2, 33 }.Concat(Enumerable.Range(0, 15).Select(l => 3 + 2 * l)).ToArray();

    /// <summary>Live object states: ours to the party, the party's into the game.</summary>
    void SyncObjects(uint scene)
    {
        if (!joined || link!.ObjScene != scene || !link.ObjPrimed) return;
        uint reset = link.ResetSeq;
        if (syncedScene != scene) lastCounters = null;   // the load just set them from the party save
        SyncInventory();
        if (syncedScene != scene)
        {
            // new scene, or the scene was reset (we died): get every live state the party has
            syncedScene = scene;
            syncedReset = reset;
            lock (objIn) objIn.RemoveAll(o => o.Scene != scene);
            var m = Proto.Msg("objsync"); m["s"] = PlayerState.SceneName(scene);
            Send(m);
        }
        foreach (var st in link.TakeObjChanges())
        {
            if (st.Scene != scene) continue;
            Send(st.ToJson());
            objSent++;
            if (Verbose) log($"obj out #{st.Index} {st.Id:X8} type {st.Type:X2} {st.Bits} bits");
        }
        lock (objIn)
        {
            objIn.RemoveAll(o => o.Scene != scene);
            if (objIn.Count > 0)
            {
                int n = link.PushObjStates(objIn);
                objIn.RemoveRange(0, n);
                objGot += n;
            }
        }
    }

    public (int sent, int got) ObjCounts => (objSent, objGot);

    // --- enemy movement (npcsync.c) ------------------------------------------------------------
    readonly Dictionary<ushort, (byte[] pose, DateTime at)> npcIn = new();
    uint npcInScene;
    bool npcInDirty;
    uint lastNpcOutSeq;
    readonly Dictionary<ushort, byte[]> npcSent = new();
    DateTime npcFullAt;
    DateTime npcInWritten;
    readonly uint[] slotRank = new uint[Mb.MaxRemote];
    public bool NpcSync = true;

    /// <summary>
    /// Our game's NPC poses out (only those that moved, everything once a second), the poses other
    /// games drive in. A pose not refreshed for 1.5 s is dropped (that NPC is no longer theirs).
    /// </summary>
    void SyncNpcs(uint scene)
    {
        if (!joined) return;
        link!.SetNpcRanks(NpcSync ? (uint)myId + 1 : 0, slotRank);
        if (!NpcSync) return;
        var now = DateTime.UtcNow;
        uint seq = link.NpcOutSeq;
        if (seq != lastNpcOutSeq)
        {
            lastNpcOutSeq = seq;
            var (sc, poses) = link.ReadNpcOut();
            bool full = now >= npcFullAt;
            if (full) { npcFullAt = now.AddSeconds(1); npcSent.Clear(); }
            bool anyoneHere;
            lock (gate) anyoneHere = peers.Values.Any(p => p.State.SceneId == scene && now - p.Updated < TimeSpan.FromSeconds(3));
            if (!anyoneHere) npcSent.Clear();
            else if (sc == scene && poses.Length > 0)
            {
                var changed = new List<byte[]>();
                foreach (var p in poses)
                {
                    ushort idx = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(p);
                    if (npcSent.TryGetValue(idx, out var last) && !PoseMoved(last, p)) continue;
                    npcSent[idx] = p;
                    changed.Add(p);
                }
                if (changed.Count > 0)
                {
                    var m = Proto.Msg("np");
                    m["s"] = PlayerState.SceneName(scene);
                    m["d"] = Convert.ToBase64String(changed.SelectMany(x => x).ToArray());
                    Send(m);
                }
            }
        }
        lock (npcIn)
        {
            if (npcInScene != scene) { npcIn.Clear(); npcInScene = scene; npcInDirty = true; }
            foreach (var k in npcIn.Where(kv => now - kv.Value.at > TimeSpan.FromSeconds(1.5)).Select(kv => kv.Key).ToList())
            { npcIn.Remove(k); npcInDirty = true; }
            if (npcInDirty || now - npcInWritten > TimeSpan.FromSeconds(0.5))
            {
                npcInDirty = false;
                npcInWritten = now;
                link.WriteNpcIn(scene, npcIn.Values.Select(v => v.pose).ToList());
            }
        }
    }

    /// <summary>Worth sending again: moved/turned, or a new animation, goal, hit points or owner generation.</summary>
    static bool PoseMoved(byte[] a, byte[] b)
    {
        for (int k = 8; k < 0x2C; k += 4)
        {
            float x = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(a.AsSpan(k));
            float y = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(k));
            if (MathF.Abs(x - y) > (k < 0x14 ? 0.02f : 0.01f)) return true;
        }
        return !a.AsSpan(0x2C, 4).SequenceEqual(b.AsSpan(0x2C, 4)) || !a.AsSpan(0x34, 12).SequenceEqual(b.AsSpan(0x34, 12));
    }

    void UpdatePuppets(uint scene)
    {
        var used = new bool[Mb.MaxRemote];
        List<Peer> here;
        lock (gate)
        {
            var now = DateTime.UtcNow;
            here = peers.Values
                .Where(p => p.State.SceneId == scene && now - p.Updated < TimeSpan.FromSeconds(3))
                .OrderBy(p => p.Slot < 0 ? 1 : 0).ThenBy(p => p.Id).ToList();
            foreach (var p in peers.Values.Except(here))
                if (p.Slot >= 0) { link!.ClearRemote(p.Slot); p.Slot = -1; }
            foreach (var p in here)
                if (p.Slot >= 0) used[p.Slot] = true;
            foreach (var p in here)
            {
                if (p.Slot < 0)
                {
                    int s = Array.IndexOf(used, false);
                    if (s < 0) continue; // more players here than puppet slots
                    used[s] = true;
                    p.Slot = s;
                    p.Written = 0;
                }
                slotRank[p.Slot] = (uint)p.Id + 1;
                if (p.Written != p.Seq)
                {
                    p.Written = p.Seq;
                    // their controls are off (a cutscene, a conversation): their sticks mustn't walk the puppet.
                    // A control-off bit left over while they clearly run around doesn't count (the puppet
                    // would only glide to their position without walking or turning).
                    bool moving = p.State.Vx * p.State.Vx + p.State.Vz * p.State.Vz > 0.5f;
                    link!.SetRemote(p.Slot, p.State, p.State.ControlOff != 0 && !moving ? Mb.RfNoInput : 0, ParseColor(p.Color), p.Name);
                }
            }
        }
    }

    static uint ParseColor(string c) =>
        c.Length == 7 && uint.TryParse(c.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var v) ? (v << 8) | 0xFF : 0xFFFFFFFF;

    public void Dispose()
    {
        logTimer.Dispose();
        cts.Cancel();
        conn?.Close("quit");
        gameThread?.Join(500);
    }
}
