using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using BfbbOnline.Shared;

namespace BfbbOnline.Server;

public sealed class SimOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Most map sims running at once (each ≈ 15-20% of a CPU core, ≈ 500 MB RAM).</summary>
    public int MaxSims { get; set; } = 4;
    public string DolphinPath { get; set; } = "";
    /// <summary>The patched game's sys/main.dol (a folder with sys/ and files/).</summary>
    public string GameDol { get; set; } = "";
    /// <summary>Per-sim Dolphin user folders are created here.</summary>
    public string WorkDir { get; set; } = "sims";
    /// <summary>Seconds a map keeps simulating after its last player left.</summary>
    public int IdleSeconds { get; set; } = 45;
    public bool HideWindows { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Usable => Enabled && File.Exists(DolphinPath) && File.Exists(GameDol);
}

/// <summary>Runs one headless game per (party, scene) that has players in it.</summary>
public sealed class SimManager : IDisposable
{
    readonly SimOptions opt;
    readonly ServerHost host;
    readonly Action<string> log;
    readonly object gate = new();
    readonly Dictionary<(Party, uint), SimInstance> sims = new();
    int slotCounter;
    readonly HashSet<(Party, uint)> capWarned = new();

    /// <summary>The running sim of a scene (tests).</summary>
    public SimInstance? Get(Party party, uint scene) { lock (gate) return sims.GetValueOrDefault((party, scene)); }
    bool warned;

    public SimManager(SimOptions opt, ServerHost host, Action<string> log)
    {
        this.opt = opt;
        this.host = host;
        this.log = log;
    }

    public int Running { get { lock (gate) return sims.Count; } }

    /// <summary>Called whenever the set of players in a (party, scene) may have changed. Never blocks.</summary>
    public void OccupancyChanged(Party party, uint scene)
    {
        if (scene == 0) return;
        if (!opt.Usable)
        {
            if (opt.Enabled && !warned) { warned = true; log("map sims are off: Dolphin or the patched game was not found (see server settings)"); }
            return;
        }
        int count = host.PlayersInScene(party, scene).Count;
        lock (gate)
        {
            if (sims.TryGetValue((party, scene), out var s))
            {
                s.SetOccupied(count > 0);
                return;
            }
            if (count == 0) return;
            if (sims.Count >= opt.MaxSims)
            {
                // a sim nobody is in anymore gives its place to a level with players
                var idle = sims.Values.Where(x => x.IsEmpty).OrderBy(x => x.EmptySince).FirstOrDefault();
                if (idle != null)
                {
                    log($"{PlayerState.SceneName(scene)} needs a sim: stopping the idle one for {PlayerState.SceneName(idle.Scene)}");
                    sims.Remove((idle.Party, idle.Scene));
                    var stopping = idle;
                    System.Threading.Tasks.Task.Run(() => stopping.Stop("evicted"));
                }
                else
                {
                    if (capWarned.Add((party, scene)))
                        log($"sim limit ({opt.MaxSims}) reached: {party.Name}/{PlayerState.SceneName(scene)} runs without a sim");
                    return;
                }
            }
            capWarned.Remove((party, scene));
            int slot = Interlocked.Increment(ref slotCounter);
            s = new SimInstance(this, opt, party, scene, slot % 64);
            sims[(party, scene)] = s;
            s.Start();
        }
    }

    /// <summary>A player changed an object: the sim of that scene applies it too.</summary>
    public void ObjectChanged(Party party, ObjState st)
    {
        SimInstance? s;
        lock (gate) sims.TryGetValue((party, st.Scene), out s);
        s?.Enqueue(st);
    }

    internal void SimObjectChanged(SimInstance s, ObjState st)
    {
        if (st.CountOnly) return;   // a race reward in the sim: nobody won it
        s.Party.StoreObj(st);
        host.RelayObj(s.Party, st, null);
    }

    public void PlayerMoved(Party party, Player p)
    {
        SimInstance? s;
        lock (gate) sims.TryGetValue((party, p.Scene), out s);
        s?.Touch();
    }

    internal List<Player> Occupants(Party party, uint scene) => host.PlayersInScene(party, scene);
    internal void Log(string s) => log(s);

    internal void Ended(SimInstance s)
    {
        lock (gate)
            if (sims.TryGetValue((s.Party, s.Scene), out var cur) && cur == s)
                sims.Remove((s.Party, s.Scene));
    }

    public JsonArray Describe(Party party)
    {
        var a = new JsonArray();
        lock (gate)
            foreach (var s in sims.Values.Where(s => s.Party == party))
                a.Add(new JsonObject { ["s"] = PlayerState.SceneName(s.Scene), ["state"] = s.Status });
        return a;
    }

    public void Dispose()
    {
        List<SimInstance> all;
        lock (gate) all = sims.Values.ToList();
        foreach (var s in all) s.Stop("server stopping");
    }
}

/// <summary>One headless Dolphin simulating one scene for one party.</summary>
public sealed class SimInstance
{
    readonly SimManager mgr;
    readonly SimOptions opt;
    public Party Party { get; }
    public uint Scene { get; }
    readonly int slot;
    readonly Thread thread;
    readonly CancellationTokenSource cts = new();
    Process? proc;
    GameLink? link;
    Dictionary<uint, byte[]>? baseline;
    DateTime emptySince = DateTime.MaxValue;
    readonly Dictionary<int, int> puppetSlots = new(); // player id -> remote slot
    public volatile string Status = "starting";
    readonly System.Collections.Concurrent.ConcurrentQueue<ObjState> inbound = new();
    readonly List<ObjState> pendingIn = new();
    bool synced;

    public void Enqueue(ObjState st) => inbound.Enqueue(st);

    public SimInstance(SimManager mgr, SimOptions opt, Party party, uint scene, int slot)
    {
        this.mgr = mgr;
        this.opt = opt;
        Party = party;
        Scene = scene;
        this.slot = slot;
        thread = new Thread(Run) { IsBackground = true, Name = $"sim {party.Name}/{PlayerState.SceneName(scene)}" };
    }

    string Tag => $"sim {Party.Name}/{PlayerState.SceneName(Scene)}";

    public void Start() => thread.Start();

    public bool IsEmpty => emptySince != DateTime.MaxValue;
    public DateTime EmptySince => emptySince;
    public GameLink? Link => link;
    public int Pid => proc is { HasExited: false } ? proc.Id : 0;

    public void SetOccupied(bool occupied)
    {
        if (occupied) emptySince = DateTime.MaxValue;
        else if (emptySince == DateTime.MaxValue) emptySince = DateTime.UtcNow;
    }

    public void Touch() { }

    public void Stop(string why)
    {
        if (cts.IsCancellationRequested) return;
        Status = "stopping";
        cts.Cancel();
        if (!thread.Join(TimeSpan.FromSeconds(10))) Kill();
        mgr.Log($"{Tag} stopped ({why})");
    }

    void Run()
    {
        try
        {
            Launch();
            mgr.Log($"{Tag}: Dolphin pid {proc!.Id} started");
            link = GameLink.Attach(proc.Id, TimeSpan.FromSeconds(60), cts.Token)
                ?? throw new Exception("the patched game did not come up");
            link.Hello(Mb.ModeSim, Scene, loadWaitFrames: 1200);
            Status = "loading";
            var lastExport = DateTime.UtcNow;
            var lastHide = DateTime.MinValue;
            var started = DateTime.UtcNow;
            while (!cts.IsCancellationRequested)
            {
                if (proc.HasExited) throw new Exception($"Dolphin exited ({proc.ExitCode})");
                if (!link.Bridged) link.Hello(Mb.ModeSim, Scene, loadWaitFrames: 1200);
                if (link.LoadPending) ImportWorld();
                FeedPuppets();
                SyncObjects();
                if (opt.HideWindows && DateTime.UtcNow - lastHide > TimeSpan.FromSeconds(1) && DateTime.UtcNow - started < TimeSpan.FromMinutes(2))
                {
                    lastHide = DateTime.UtcNow;
                    WindowTools.HideProcessWindows(proc.Id);
                }
                if (Status == "running" && DateTime.UtcNow - lastExport > TimeSpan.FromSeconds(10))
                {
                    lastExport = DateTime.UtcNow;
                    ExportWorld();
                }
                if (emptySince != DateTime.MaxValue && DateTime.UtcNow - emptySince > TimeSpan.FromSeconds(opt.IdleSeconds))
                {
                    mgr.Log($"{Tag}: no players for {opt.IdleSeconds}s, shutting down");
                    break;
                }
                Thread.Sleep(33);
            }
            if (Status == "running") ExportWorld();
        }
        catch (Exception e) when (!cts.IsCancellationRequested)
        {
            mgr.Log($"{Tag} failed: {e.Message}");
        }
        catch (Exception) { }
        finally
        {
            Status = "stopped";
            Kill();
            link?.Dispose();
            mgr.Ended(this);
        }
    }

    void Launch()
    {
        string user = Path.GetFullPath(Path.Combine(opt.WorkDir, $"sim-{slot}"));
        SimProfile.Write(user);
        var psi = new ProcessStartInfo(opt.DolphinPath)
        {
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Minimized,
        };
        foreach (var a in new[] { "-b", "-u", user, "-e", opt.GameDol }.Concat(DolphinMemory.LaunchArgs)) psi.ArgumentList.Add(a);
        proc = Process.Start(psi) ?? throw new Exception("could not start Dolphin");
        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
    }

    void ImportWorld()
    {
        uint seq = link!.LoadSeq;
        var world = Party.World.ForPlayer(null);
        if (world.Count > 0 && !link.ImportWorld(world, TimeSpan.FromSeconds(5)))
            mgr.Log($"{Tag}: world import timed out");
        baseline = world;
        link.AckLoad(seq);
        if (link.LoadScene == Scene)
        {
            if (Status != "running") mgr.Log($"{Tag}: running ({world.Count} world slots)");
            Status = "running";
        }
    }

    void ExportWorld()
    {
        try
        {
            var w = link!.ExportWorld();
            if (!w.ContainsKey(Scene)) return;
            Party.World.Merge(w, baseline, new HashSet<uint> { Scene });
            baseline ??= new Dictionary<uint, byte[]>();
            baseline[Scene] = w[Scene];
        }
        catch (Exception e) { mgr.Log($"{Tag}: export failed: {e.Message}"); }
    }

    void SyncObjects()
    {
        if (Status != "running" || link!.ObjScene != Scene || !link.ObjPrimed) return;
        if (!synced)
        {
            // states players made before this sim was up
            synced = true;
            pendingIn.AddRange(Party.ObjectsIn(Scene));
        }
        while (inbound.TryDequeue(out var st)) if (st.Scene == Scene) pendingIn.Add(st);
        if (pendingIn.Count > 0)
        {
            int n = link.PushObjStates(pendingIn);
            pendingIn.RemoveRange(0, n);
        }
        foreach (var st in link.TakeObjChanges())
            if (st.Scene == Scene) mgr.SimObjectChanged(this, st);
    }

    void FeedPuppets()
    {
        var here = mgr.Occupants(Party, Scene);
        var ids = here.Select(p => p.Id).ToHashSet();
        foreach (var gone in puppetSlots.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            link!.ClearRemote(puppetSlots[gone]);
            puppetSlots.Remove(gone);
        }
        foreach (var p in here)
        {
            if (!puppetSlots.TryGetValue(p.Id, out int s))
            {
                var used = puppetSlots.Values.ToHashSet();
                s = Enumerable.Range(0, Mb.MaxRemote).FirstOrDefault(i => !used.Contains(i), -1);
                if (s < 0) continue;
                puppetSlots[p.Id] = s;
            }
            var pose = p.Pose;
            if (pose.SceneId != Scene) continue;
            link!.SetRemote(s, pose, Mb.RfCheap, 0xFFFFFFFF, p.Name);
        }
    }

    void Kill()
    {
        try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch { }
    }
}

/// <summary>The Dolphin settings a map sim runs with: everything expensive or interactive off.</summary>
public static class SimProfile
{
    public static void Write(string userDir)
    {
        var cfg = Path.Combine(userDir, "Config");
        Directory.CreateDirectory(cfg);
        File.WriteAllText(Path.Combine(cfg, "Dolphin.ini"), """
            [Core]
            GFXBackend = Null
            CPUThread = False
            SkipIPL = True
            EnableCheats = False
            OverclockEnable = False
            [DSP]
            Backend = No Audio Output
            EnableJIT = True
            DSPHLE = True
            [Interface]
            SkipNKitWarning = True
            UsePanicHandlers = False
            OnScreenDisplayMessages = False
            ConfirmStop = False
            ShowActiveTitle = False
            [Display]
            RenderToMain = False
            Fullscreen = False
            RenderWindowWidth = 160
            RenderWindowHeight = 120
            RenderWindowAutoSize = False
            [Analytics]
            PermissionAsked = True
            Enabled = False
            [AutoUpdate]
            UpdateTrack =
            """.Replace("            ", ""));
        File.WriteAllText(Path.Combine(cfg, "GFX.ini"), "[Settings]\nHiresTextures = False\nWaitForShadersBeforeStarting = False\n");
    }
}

static class WindowTools
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32")] static extern bool IsWindowVisible(IntPtr h);

    public static void HideProcessWindows(int pid)
    {
        if (!OperatingSystem.IsWindows()) return;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p == pid && IsWindowVisible(h)) ShowWindow(h, 0);
            return true;
        }, IntPtr.Zero);
    }
}
