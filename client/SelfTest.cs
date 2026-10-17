using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BfbbOnline.Server;
using BfbbOnline.Shared;

namespace BfbbOnline.Client;

/// <summary>
/// BFBB-Online.exe --selftest [--scene JF01] [--hub HB01] [--keep]
/// Runs a whole co-op session on this PC (in-process server, three headless games) and checks
/// sync by reading every game's memory. Writes selftest-report.md next to the exe.
/// </summary>
static partial class SelfTest
{
    sealed class Bot
    {
        public string Name = "";
        public Session S = null!;
        public GameLink L => S.Link!;
        public string? Hung;          // set by the watchdog: the game stopped running frames
        public uint lastFrames, lastPad;
        public uint waitFrames; public int waitSame;   // WaitAll: is the game paused on a prompt?
        public DateTime lastChange = DateTime.UtcNow, lastValid = DateTime.UtcNow;
    }

    // ---- hang watchdog: a game whose frame counters stop for HangSeconds is hung ----------------
    static readonly List<Bot> watched = new();
    static double HangSeconds = 20;
    static bool dualCore;
    static uint testFlags;
    static bool noTap;   // --flags: diagnostics bits (mailbox.h) set in every bot
    static System.Threading.Timer? watchdog;
    static bool AnyHung { get { lock (watched) return watched.Any(b => b.Hung != null); } }

    /// <summary>Disc state of a hung game: the game's async read queue and the DVD driver.</summary>
    static string DvdState(GameLink l)
    {
        var m = l.Mem;
        string Hex(uint a, int n) => Convert.ToHexString(m.Read(a, n));
        uint exec = m.ReadU32(0x803CC420);
        return $"file_queue {Hex(0x803C35F0, 0x70)}; executing {exec:X8} {(exec >= 0x80000000 && exec < 0x81800000 ? Hex(exec, 0x30) : "")}; "
            + $"Pause {m.ReadU32(0x803CC42C)} Pausing {m.ReadU32(0x803CC430)} CurrCommand {m.ReadU32(0x803CC43C)} Canceling {m.ReadU32(0x803CC440)} ResetRequired {m.ReadU32(0x803CC458)} "
            + $"WaitingQueue {Hex(0x8037D838, 0x20)} syncActive {m.ReadU32(0x803CBAC4)}";
    }

    /// <summary>
    /// Where the game's CPU is, without a debugger: every interrupt saves the running code's
    /// registers into the current OSContext (0x800000D4). PC (srr0), LR and the stack back-chain.
    /// </summary>
    static string CpuSample(GameLink l) => CpuSample(l.Mem);

    static string CpuSample(DolphinMemory m)
    {
        uint c = m.ReadU32(0x800000D4);
        if (c < 0x80000000 || c >= 0x81800000) return $"context {c:X8}?";
        uint pc = m.ReadU32(c + 0x198), lr = m.ReadU32(c + 0x84), sp = m.ReadU32(c + 4);
        var chain = new List<string>();
        for (int i = 0; i < 16 && sp >= 0x80000000 && sp < 0x81800000; i++)
        {
            uint ret = m.ReadU32(sp + 4);
            chain.Add(ret.ToString("X8"));
            uint next = m.ReadU32(sp);
            if (next <= sp) break;
            sp = next;
        }
        string gpr = string.Join(" ", new[] { 3, 4, 27, 28, 29, 30, 31 }.Select(r => $"r{r}={m.ReadU32(c + 4 * (uint)r):X8}"));
        return $"PC {pc:X8} LR {lr:X8} {gpr} stack {string.Join(" ", chain)}";
    }

    /// <summary>RenderWare's dirty-frame list: frame, its flags, parent/root and attached objects (type, callback).</summary>
    static string RwDirty(GameLink l)
    {
        var m = l.Mem;
        bool Ok(uint a) => a >= 0x80000000 && a < 0x84000000;
        uint eng = m.ReadU32(0x803CC87C), head = eng + 0xBC;
        var sb = new StringBuilder($"RW engine {eng:X8} dirty head {m.ReadU32(head):X8}/{m.ReadU32(head + 4):X8}:");
        uint lk = m.ReadU32(head);
        for (int i = 0; i < 40 && Ok(lk) && lk != head; i++)
        {
            uint fr = lk - 8;
            sb.Append($" [frame {fr:X8} flags {m.Read(fr + 3, 1)[0]:X2} root {m.ReadU32(fr + 0x98):X8} child {m.ReadU32(fr + 0x9C):X8} next {m.ReadU32(fr + 0xA0):X8} objs:");
            uint oh = fr + 0x90, ol = m.ReadU32(oh);
            for (int j = 0; j < 8 && Ok(ol) && ol != oh; j++)
            {
                uint ob = ol - 8;
                sb.Append($" {ob:X8} type {m.Read(ob, 1)[0]} cb {m.ReadU32(ol + 8):X8}");
                ol = m.ReadU32(ol);
            }
            sb.Append(']');
            lk = m.ReadU32(lk);
        }
        return sb.ToString();
    }

    static readonly HashSet<string> arenaLogged = new();
    static void Watch(Bot b)
    {
        lock (watched) watched.Add(b);
        watchdog ??= new System.Threading.Timer(_ =>
        {
            List<Bot> list;
            lock (watched) list = watched.ToList();
            foreach (var w in list)
            {
                try
                {
                    var l = w.S.Link;
                    if (l == null || w.Hung != null) { w.lastChange = DateTime.UtcNow; continue; }
                    uint sw = l.DbgRender(6) + l.DbgRender(7);
                    if (sw != 0 && arenaLogged.Add($"{w.Name}snd{sw}")) Log($"{w.Name}: sound stream block in flight: set up over it {l.DbgRender(6)}x, reads held {l.DbgRender(7)}x");
                    if (l.DbgRender(21) != 0 && arenaLogged.Add(w.Name + "xfer")) Log($"{w.Name}: BAD DMA: flags {l.DbgRender(21):X}, dst {l.DbgRender(22):X8}, len {l.DbgRender(23):X}, caller {l.DbgRender(24):X8}, offset/type/block {l.DbgRender(25):X8}, block length {l.DbgRender(26):X} addr {l.DbgRender(27):X8} offset {l.DbgRender(28):X8}");
                    if (l.DbgRender(32) != 0 && arenaLogged.Add(w.Name)) Log($"{w.Name}: RW ARENA CORRUPT: {Session.ArenaReport(l)}");
                    uint f = l.Frames, pf = l.Mem.ReadU32(GameLink.MailboxAddr + Mb.ODbgRender + 20);
                    bool valid = l.TryReadLocal(out PlayerState _) && l.Mem.ReadU32(GameLink.MailboxAddr) == Mb.Magic;
                    if (valid) w.lastValid = DateTime.UtcNow;
                    bool lost = (DateTime.UtcNow - w.lastValid).TotalSeconds >= HangSeconds;
                    if (!lost && (f != w.lastFrames || pf != w.lastPad || f == 0)) { w.lastFrames = f; w.lastPad = pf; w.lastChange = DateTime.UtcNow; continue; }
                    if (lost) w.lastChange = DateTime.MinValue;
                    if ((DateTime.UtcNow - w.lastChange).TotalSeconds < HangSeconds) continue;
                    l.TryReadLocal(out var me);
                    w.Hung = $"{w.Name} HUNG: {((DateTime.UtcNow - w.lastValid).TotalSeconds >= HangSeconds ? "no valid player state/mailbox" : "no game frame")} for {HangSeconds:F0} s (frames {f}, scene {PlayerState.SceneName(me.SceneId)}, loading {l.LoadPending}, mb.loadScene {PlayerState.SceneName(l.Mem.ReadU32(GameLink.MailboxAddr + Mb.OLoadScene))})";
                    Log(w.Hung);
                    Log("  " + DvdState(l));
                    Log($"  OS heap free {l.OsHeapFree().free / 1024} KB, largest {l.OsHeapFree().largest / 1024} KB, added {l.Mem.ReadU32(GameLink.MailboxAddr + Mb.OOsHeapAdded) / 1024} KB");
                    Log($"  GX tokens: last read {l.Mem.Read(0x803CC940, 2)[0] * 256 + l.Mem.Read(0x803CC940, 2)[1]:X4}, issued {l.Mem.Read(0x803CB130, 2)[0] * 256 + l.Mem.Read(0x803CB130, 2)[1]:X4}");
                    try
                    {
                        uint eng = l.Mem.ReadU32(0x803CC87C), cur = l.Mem.ReadU32(eng), ext = l.Mem.ReadU32(0x803CC7E8), main = l.Mem.ReadU32(0x803C0558 + 0x10);
                        uint W(uint c) => c >= 0x80000000 && c < 0x82000000 ? l.Mem.ReadU32(c + ext + 0xC) : 0;
                        Log($"  RW cameras: current {cur:X8} (world {W(cur):X8}, frame {(cur >= 0x80000000 ? l.Mem.ReadU32(cur + 4) : 0):X8}), main {main:X8} (world {W(main):X8}), curWorld {l.Mem.ReadU32(eng + 4):X8}");
                    }
                    catch (Exception) { }
                    try
                    {
                        uint sb = l.Mem.ReadU32(0x803CBBCC);
                        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "selftest", $"sndbuf-{w.Name}-{sb:X8}.bin");
                        System.IO.File.WriteAllBytes(path, l.Mem.Read(sb, 0x40000));
                        Log($"  sound buffer {sb:X8} + 256 KB saved to {path}");
                    }
                    catch (Exception e) { Log($"  sound buffer dump failed: {e.Message}"); }
                    try { Log($"  RW resource arena: {ArenaDump(l)}"); } catch (Exception e) { Log($"  RW resource arena unreadable: {e.Message}"); }
                    for (int k = 0; k < 4; k++) { Log("  " + CpuSample(l)); Thread.Sleep(150); }
                    Log("  " + RwDirty(l) + " | stage heads exit-before/exit-after/setup-before/setup-after/frame, -, rootless LR/count/frame: " + string.Join(" ", Enumerable.Range(10, 19).Select(k => l.Mem.ReadU32(GameLink.MailboxAddr + Mb.ODbgRender + 4 * (uint)k).ToString("X8"))));
                }
                catch (Exception) { }
            }
        }, null, 1000, 1000);
    }

    static readonly StringBuilder report = new();
    static readonly List<(string name, bool ok, string detail)> results = new();
    static StreamWriter? logw;

    static void Log(string s)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {s}";
        lock (report) { logw?.WriteLine(line); Console.WriteLine(line); }
    }

    /// <summary>Paid doors survive a server restart: World saves its payments and loads them back.</summary>
    static void CheckPaysPersist()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bbol-pays-{Environment.ProcessId}.json");
        try
        {
            var w = new World(path);
            w.AddPay(PlayerState.SceneIdOf("BB02"), 0x1234ABCD, 53);
            w.AddPay(PlayerState.SceneIdOf("GL01"), 0xDEADBEEF, 10);
            bool dup = !w.AddPay(PlayerState.SceneIdOf("GL01"), 0xDEADBEEF, 10);
            w.Save();
            var r = new World(path); r.Load();
            var got = r.AllPays().OrderBy(x => x.id).ToList();
            bool ok = dup && got.Count == 2 && got[0] == (PlayerState.SceneIdOf("BB02"), 0x1234ABCDu, 53u)
                && got[1] == (PlayerState.SceneIdOf("GL01"), 0xDEADBEEFu, 10u);
            Result("payments persist", ok, $"saved 2, loaded {got.Count}: {string.Join(", ", got.Select(x => $"{PlayerState.SceneName(x.scene)} {x.id:X8}:{x.ev}"))}");
        }
        catch (Exception e) { Result("payments persist", false, e.Message); }
        finally { try { File.Delete(path); } catch { } }
    }

    static void Result(string name, bool ok, string detail)
    {
        lock (results) results.Add((name, ok, detail));
        Log($"{(ok ? "PASS" : "FAIL")} {name}: {detail}");
    }

    public static int Run(string[] args)
    {
        var a = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
            if (args[i].StartsWith("--"))
                a[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "1";
        string dir = AppContext.BaseDirectory;
        string work = Path.Combine(dir, "selftest");
        Directory.CreateDirectory(work);
        logw = new StreamWriter(Path.Combine(work, "selftest.log"), false) { AutoFlush = true };
        uint scene = PlayerState.SceneIdOf(a.GetValueOrDefault("scene", "JF01"));
        uint hub = PlayerState.SceneIdOf(a.GetValueOrDefault("hub", "HB01"));
        int port = 25610;

        var dolphin = Installer.FindDolphin(a.GetValueOrDefault("dolphin"));
        var deluxe = Installer.FindDeluxe(a.GetValueOrDefault("deluxe"), dolphin);
        if (dolphin == null || deluxe == null) { Log("Dolphin or Deluxe not found"); return 2; }
        Installer.Install(deluxe, Log);
        if (a.ContainsKey("monitors")) { visible = "M"; MonitorTest(dolphin, work, a); return Finish(new List<Bot>(), a); }

        var data = Path.Combine(work, "server-data");
        if (Directory.Exists(data)) Directory.Delete(data, true);
        using var server = new ServerHost(new ServerOptions
        {
            Port = port,
            Pvp = a.ContainsKey("pvp"),
            DataDir = data,
            Sims = new SimOptions { Enabled = a.ContainsKey("sims"), MaxSims = 4, DolphinPath = dolphin, GameDol = Installer.GameDol, WorkDir = Path.Combine(work, "sims") },
            Log = s => Log("[server] " + s),
        });
        server.Start();
        string party = "selftest";
        var bots = new List<Bot>();
        visible = a.TryGetValue("visible", out var vb) ? (vb == "1" ? "A" : vb) : null;
        offscreen = a.ContainsKey("offscreen");
        if (offscreen) visible ??= "B";
        gdbBot = a.GetValueOrDefault("gdb");
        if (a.ContainsKey("noram")) DolphinMemory.ExtraRam = false;
        dualCore = a.ContainsKey("dualcore");
        if (a.TryGetValue("flags", out var tf)) testFlags = uint.Parse(tf);
        noTap = a.ContainsKey("notap");
        if (a.TryGetValue("hang", out var hs)) HangSeconds = double.Parse(hs, System.Globalization.CultureInfo.InvariantCulture);
        namesDir = a.GetValueOrDefault("names") ?? Path.Combine(dir, "names");
        if (a.TryGetValue("probe", out var pr)) probe = new System.Text.RegularExpressions.Regex(pr, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (a.TryGetValue("rawboot", out var rawDol))
        {
            // --rawboot DOL [--seconds N]: run a DOL (e.g. patch\out\main.dol from a MINIMAL build) in a
            // normal window with no bridge, then sample the CPU and take a screenshot
            var user = Path.Combine(work, "user-raw");
            SimProfile.Write(user);
            var ini = Path.Combine(user, "Config", "Dolphin.ini");
            File.WriteAllText(ini, File.ReadAllText(ini).Replace("GFXBackend = Null", "GFXBackend = D3D11")
                .Replace("RenderWindowWidth = 160", "RenderWindowWidth = 640").Replace("RenderWindowHeight = 120", "RenderWindowHeight = 480"));
            if (offscreen) Offscreen(user);
            var psi = new System.Diagnostics.ProcessStartInfo(dolphin) { UseShellExecute = false };
            foreach (var x in new[] { "-u", user, "-b", "-e", rawDol }.Concat(DolphinMemory.LaunchArgs)) psi.ArgumentList.Add(x);
            var p = System.Diagnostics.Process.Start(psi)!;
            DolphinMemory? m = null;
            for (int i = 0; i < 100 && m == null; i++) { Thread.Sleep(200); m = DolphinMemory.TryAttach(p.Id); }
            int secs = int.Parse(a.GetValueOrDefault("seconds", "40"));
            for (int t = 0; t < secs; t += 5)
            {
                Thread.Sleep(5000);
                if (m != null) Log($"t={t + 5}s " + CpuSample(m));
            }
            var shotDir = Path.Combine(AppContext.BaseDirectory, "selftest", "shots");
            Directory.CreateDirectory(shotDir);
            Shots.Capture(p.Id, Path.Combine(shotDir, "rawboot.png"));
            try { p.Kill(); } catch { }
            Result("rawboot", true, rawDol);
            return Finish(bots, a);
        }
        if (a.ContainsKey("diag"))
        {
            try { Diag(party, port, scene, dolphin, work, bots); }
            catch (Exception e) { Result("diag", false, "crashed: " + e); }
            return Finish(bots, a);
        }
        if (a.TryGetValue("survey", out var sv))
        {
            try
            {
                var S = StartBot("S", party, port, scene, dolphin, work);
                bots.Add(S);
                if (a.ContainsKey("noimport")) { WaitAll(bots, scene, TimeSpan.FromSeconds(120)); S.L.Mem.WriteU32(GameLink.MailboxAddr + Mb.OLoadWait, 0); Log("scene loads no longer wait for the party world (--noimport)"); }
                if (a.TryGetValue("flags", out var fl)) { WaitAll(bots, scene, TimeSpan.FromSeconds(120)); S.L.SetFlag(uint.Parse(fl), true); }
                foreach (var name in sv.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (S.Hung != null) { Result("survey " + name, false, "skipped: " + S.Hung); continue; }
                    uint id = PlayerState.SceneIdOf(name);
                    if (id != scene || !WaitAll(bots, id, TimeSpan.FromSeconds(gdbBot != null ? 240 : 5))) S.L.Warp(id);
                    if (!WaitAll(bots, id, TimeSpan.FromSeconds(60))) { Shot(S, "survey-" + name); Result("survey " + name, false, (S.Hung != null ? S.Hung + "; " : "") + Describe(bots) + $" cmgr {S.L.PlayingCutscene():X8} talk {S.L.ActiveTalk():X8} controlOff {S.L.Global(0x1788):X} gameMode {S.L.Mem.ReadU32(0x803CB8A8)} loadPending {S.L.LoadPending} padFrames {S.L.Mem.ReadU32(GameLink.MailboxAddr + 0x4564 + 20)}"); Thread.Sleep(2000); Log($"padFrames now {S.L.Mem.ReadU32(GameLink.MailboxAddr + 0x4564 + 20)}, talk {S.L.ActiveTalk():X8}"); continue; }
                    Thread.Sleep(3000);
                    if (a.TryGetValue("dwell", out var dw))
                    {
                        // stay a while: things that only happen later in a scene (race timers...)
                        var until = DateTime.UtcNow.AddSeconds(double.Parse(dw));
                        while (DateTime.UtcNow < until && S.Hung == null) Thread.Sleep(500);
                        if (S.Hung != null || !S.L.TryReadLocal(out var dme) || dme.SceneId != id) { Result("survey " + name, false, $"after {dw} s: " + (S.Hung ?? Describe(bots))); continue; }
                    }
                    uint Dbg(int k) => S.L.Mem.ReadU32(GameLink.MailboxAddr + Mb.ODbgRender + 4 * (uint)k);
                    if (Dbg(29) != 0) Log($"{name}: race/challenge end chain: {Dbg(29)} objects kept per player");
                    if (Dbg(32) != 0) Log($"{name}: RW ARENA CORRUPT: {Session.ArenaReport(S.L)}");
                    if (Enumerable.Range(0, 5).Any(k => Dbg(20 + 2 * k) != 0)) Log($"{name}: RW guards (translate/transform/rotate/addchild/ortho caller x count): " + string.Join(" ", Enumerable.Range(0, 5).Select(k => $"{Dbg(19 + 2 * k):X8}x{Dbg(20 + 2 * k)}")));
                    if (Dbg(17) != 0) Log($"{name}: RwFrameUpdateObjects skipped {Dbg(17)} rootless frames, last {Dbg(18):X8} from {Dbg(16):X8}");
                    Result("survey " + name, true, $"puppets {S.L.CtxReady}{(S.L.HighRam ? " (high RAM)" : "")}, free heap {S.L.HeapGap / 1024} KB, OS heap free {S.L.OsHeapFree().free / 1024} KB (+{S.L.Mem.ReadU32(GameLink.MailboxAddr + Mb.OOsHeapAdded) / 1024} KB) (largest {S.L.OsHeapFree().largest / 1024}), puppet heap {string.Join("/", S.L.HeapFree.Take((int)S.L.CtxReady + 1).Select(f => f / 1024))} KB. " + Survey(S));
                }
            }
            catch (Exception e) { Result("survey", false, "crashed: " + e); }
            return Finish(bots, a);
        }
        if (a.ContainsKey("chaos"))
        {
            ctx = new Ctx(server, party, port, dolphin, work, scene, hub);
            try { ChaosMode(a, bots); }
            catch (Exception e) { Result("chaos", false, "crashed: " + e); }
            return Finish(bots, a);
        }
        try
        {
            var phases = a.GetValueOrDefault("phases", "core,pickups,keys,enemies,npcmove,bosses,teleport,cutscenes,talk,cheats,pay,tbox,abilities,loadstorm,fourmaps").Split(',').ToHashSet();
            var A = StartBot("A", party, port, scene, dolphin, work);
            var B = StartBot("B", party, port, scene, dolphin, work);
            bots.Add(A); bots.Add(B);
            ctx = new Ctx(server, party, port, dolphin, work, scene, hub);

            // 1. both in the scene, puppets built, object table primed
            bool up = WaitAll(bots, scene, TimeSpan.FromSeconds(120));
            {
                var M = A.L.Mem;
                string H(int k) { uint h = 0x8038C5E4u + 0xDCu * (uint)k; return $"heap{k} base {M.ReadU32(h + 4):X8} size {M.ReadU32(h + 8):X}"; }
                Log($"DMA probe calls: DVD {A.L.DbgRender(20)}, ARQ {A.L.DbgRender(19)}; memory layout: arenaLo {M.ReadU32(0x803CC550):X8}..{M.ReadU32(0x803CC554):X8}, RW engine {M.ReadU32(0x803CC87C):X8}, sound buffer {M.ReadU32(0x803CBBC8):X8}/{M.ReadU32(0x803CBBCC):X8}, arena heap {M.ReadU32(M.ReadU32(0x803CC87C) + M.ReadU32(0x803CC848) + 0xC):X8}, {H(0)}, {H(1)}, {H(2)}");
            }
            Result("presence", up, up ? $"A and B in {PlayerState.SceneName(scene)}, puppet contexts A {A.L.CtxReady} B {B.L.CtxReady} (heap A {string.Join("/", A.L.HeapFree.Select(f => f / 1024))} KB), objects primed" : Describe(bots));
            if (!up) return Finish(bots, a);
            Thread.Sleep(3000);

            if (!phases.Contains("core"))
            {
                var C0 = StartBot("C", party, port, scene, dolphin, work);
                bots.Add(C0);
                WaitAll(bots, scene, TimeSpan.FromSeconds(120));
                foreach (var b in bots) WaitControl(b, TimeSpan.FromSeconds(60));
                RunPhases(phases, bots, a);
                return Finish(bots, a);
            }

            // 2. puppet tracking
            TestPuppets(A, B);

            // 3. full consistency before anything is touched
            Compare("consistency (start)", A, B, null);

            // 4. collect pickups in A, check B
            TestPickups(A, B, scene, "level", 3);
            Thread.Sleep(2000);
            Compare("consistency (after pickups)", A, B, null);
            CompareSim(server, party, scene, A);

            // 5. late joiner
            var C = StartBot("C", party, port, scene, dolphin, work);
            bots.Add(C);
            bool cUp = WaitAll(new List<Bot> { C }, scene, TimeSpan.FromSeconds(120));
            if (cUp)
            {
                Thread.Sleep(4000);
                Compare("late join (C vs A)", A, C, null);
                A.L.TryReadLocal(out var la); C.L.TryReadLocal(out var lc);
                var cw = C.L.ExportWorld();
                string slot = cw.TryGetValue(PlyrSlot.Tag, out var cp) ? SerialBits.GetValue(cp, PlyrSlot.BitShiny, 4).ToString() : "-";
                Result("late join inventory", la.Shiny == lc.Shiny, $"A has {la.Shiny} shinies, C has {lc.Shiny} (C's PLYR slot says {slot}, C counters {string.Join(",", C.L.ReadCounters().Take(3))})");
            }
            else Result("late join (C vs A)", false, "C never got into the scene: " + Describe(new List<Bot> { C }));

            // 6. hub: every pickup there by type (Deluxe keys etc.), collect some, compare
            foreach (var b in bots) b.L.Warp(hub);
            bool inHub = WaitAll(bots, hub, TimeSpan.FromSeconds(90));
            Result("warp to hub", inHub, inHub ? $"all in {PlayerState.SceneName(hub)}" : Describe(bots));
            if (inHub)
            {
                Thread.Sleep(3000);
                ListPickups(A, hub);
                TestPickups(A, B, hub, "hub", 4);
                Thread.Sleep(2000);
                Compare("hub consistency A vs B", A, B, null);
                Compare("hub consistency A vs C", A, C, null);
            }

            // 7. persistence: back to the level, the pickups stay collected for everyone
            foreach (var b in bots) b.L.Warp(scene);
            bool back = WaitAll(bots, scene, TimeSpan.FromSeconds(90));
            if (back)
            {
                Thread.Sleep(4000);
                CheckCollectedStay(A, B, scene);
                Compare("consistency (after coming back)", A, B, null);
            }
            else Result("return to level", false, Describe(bots));
            CheckPaysPersist();

            // 8a. the PLYR save slot decodes to the live counters (just loaded: slot == counters)
            {
                var w = A.L.ExportWorld();
                A.L.TryReadLocal(out var ma);
                if (w.TryGetValue(PlyrSlot.Tag, out var plyr))
                {
                    uint sh = SerialBits.GetValue(plyr, PlyrSlot.BitShiny, 4), sp = SerialBits.GetValue(plyr, PlyrSlot.BitSpatula, 4);
                    uint mh = SerialBits.GetValue(plyr, PlyrSlot.BitMaxHealth, 4), ch = SerialBits.GetValue(plyr, PlyrSlot.BitCharacter, 4);
                    Result("PLYR layout", sh == ma.Shiny && sp == ma.Spatula,
                        $"slot says shinies {sh} spatulas {sp} maxHealth {mh} character {ch}; game has shinies {ma.Shiny} spatulas {ma.Spatula}; first bytes {Convert.ToHexString(plyr, 0, 24)}");
                }
                else Result("PLYR layout", false, "no PLYR slot");
            }

            // 8. inventory: counters agree
            var inv = bots.Select(b => b.L.TryReadLocal(out var s) ? (b.Name, s.Shiny, s.Spatula) : (b.Name, 0u, 0u)).ToList();
            bool same = inv.Select(x => (x.Item2, x.Item3)).Distinct().Count() == 1 && inv[0].Item2 >= gained;
            Result("shared inventory", same, $"collected {gained} shinies in total; " + string.Join(", ", inv.Select(x => $"{x.Name}: {x.Item2} shinies {x.Item3} spatulas")) + (same ? "" : " (differ: counters only re-sync at the next level load)"));

            RunPhases(phases, bots, a);
        }
        catch (Exception e)
        {
            Result("selftest", false, "crashed: " + e);
        }
        return Finish(bots, a);
    }

    /// <summary>--offscreen: the render window far off the desktop, XFB copies kept in emulated RAM (Shots.CaptureXfb).</summary>
    static void Offscreen(string user)
    {
        var ini = Path.Combine(user, "Config", "Dolphin.ini");
        var t = File.ReadAllText(ini);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"RenderWindow[XY]Pos = -?\d+\r?\n", "");
        t = t.Contains("[Display]") ? t.Replace("[Display]", "[Display]\nRenderWindowXPos = -20000\nRenderWindowYPos = -20000") : t + "\n[Display]\nRenderWindowXPos = -20000\nRenderWindowYPos = -20000\n";
        File.WriteAllText(ini, t);
        var gfx = Path.Combine(user, "Config", "GFX.ini");
        var g = File.Exists(gfx) ? File.ReadAllText(gfx) : "";
        g = System.Text.RegularExpressions.Regex.Replace(g, @"XFBToTextureEnable = \w+\r?\n?", "");
        g = g.Contains("[Hacks]") ? g.Replace("[Hacks]", "[Hacks]\nXFBToTextureEnable = False") : g + "\n[Hacks]\nXFBToTextureEnable = False\n";
        File.WriteAllText(gfx, g);
    }

    static Bot StartBot(string name, string party, int port, uint scene, string dolphin, string work)
    {
        var user = Path.Combine(work, "user-" + name);
        SimProfile.Write(user);   // headless: Null video, no audio, no prompts
        if (dualCore) { var ini = Path.Combine(user, "Config", "Dolphin.ini"); File.WriteAllText(ini, File.ReadAllText(ini).Replace("CPUThread = False", "CPUThread = True")); }
        if (visible == name)
        {
            // --visible: A renders in a normal window so a person can watch the run
            var ini = Path.Combine(user, "Config", "Dolphin.ini");
            File.WriteAllText(ini, File.ReadAllText(ini).Replace("GFXBackend = Null", "GFXBackend = D3D11")
                .Replace("RenderWindowWidth = 160", "RenderWindowWidth = 640").Replace("RenderWindowHeight = 120", "RenderWindowHeight = 480"));
            if (offscreen) Offscreen(user);
        }
        if (gdbBot == name)
        {
            // --gdb X: Dolphin's GDB stub on port 2345 for that bot (it waits for a debugger at boot)
            var ini = Path.Combine(user, "Config", "Dolphin.ini");
            File.WriteAllText(ini, File.ReadAllText(ini).Replace("[Core]", "[General]\nGDBPort = 2345\n[Core]"));
        }
        var cfg = new Config { Name = name, Party = party, Server = "127.0.0.1", Port = port, Color = "#00ccff" };
        var s = new Session(cfg, m => Log($"[{name}] {m}")) { UserDir = user, BootScene = scene, Verbose = true };
        s.Connect("127.0.0.1", port);
        s.LaunchGame(dolphin);
        Log($"started {name} (pid {s.DolphinPid})");
        var bot = new Bot { Name = name, S = s };
        Watch(bot);
        return bot;
    }

    static bool WaitAll(List<Bot> bots, uint scene, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (bots.Any(b => b.Hung != null)) return false;
            if (testFlags != 0) foreach (var b in bots) try { if (b.S.Link is { } fl && (fl.Mem.ReadU32(GameLink.MailboxAddr + Mb.OFlags) & testFlags) != testFlags) fl.SetFlag(testFlags, true); } catch { }
            bool all = true;
            foreach (var b in bots)
            {
                var l = b.S.Link;
                if (l == null || !l.TryReadLocal(out var me) || !me.InGame || me.SceneId != scene || !l.ObjPrimed || l.ObjScene != scene || (l.CtxReady < 1 && (l.Mem.ReadU32(GameLink.MailboxAddr + Mb.OFlags) & 4) == 0))
                {
                    all = false;
                    // a conversation or hint that pauses the game at the start of a scene: answer it like a player
                    // (talk box, cinematic, or a paused game: frames stopped while the pad loop runs, e.g. a tutorial)
                    bool paused = false;
                    if (l != null)
                    {
                        uint f = l.Frames;
                        paused = !noTap && f == b.waitFrames && f != 0 && ++b.waitSame > 8;
                        if (paused && b.waitSame == 9) { Log($"{b.Name}: game paused in {(l.TryReadLocal(out var pm) ? PlayerState.SceneName(pm.SceneId) : "?")} (frames stopped, controlOff {(l.TryReadLocal(out var pc) ? pc.ControlOff : 0):X}): answering"); Shot(b, $"paused-{b.Name}-{PlayerState.SceneName(scene)}"); }
                        if (f != b.waitFrames) { b.waitFrames = f; b.waitSame = 0; }
                    }
                    // (a paused game is answered wherever it is: it can't follow a warp until it runs again)
                    if (l != null && l.TryReadLocal(out var m2) && !l.LoadPending && ((m2.SceneId == scene && (l.ActiveTalk() != 0 || l.PlayingCutscene() != 0)) || paused)
                        && ++waitTaps % 4 == 0)
                    {
                        // on the pause menu (a tap landed as Start once the level ran): leave it the
                        // way its resume entry does; more taps only wander the menu
                        if (l.Mem.ReadU32(0x803CB8A8) == 7) { l.Resume(); continue; }
                        uint[] cycle = { 0x10000u, 0x80000u, 0x20000u, 0x40000u, 0x1u };
                        l.PadOverride(true, l.PlayingCutscene() != 0 ? 0x1u : l.ActiveTalk() == 0 ? PromptAnswer(l) : cycle[(waitTaps / 4) % cycle.Length]);
                        Thread.Sleep(100);
                        l.PadOverride(false);
                    }
                }
            }
            if (all) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    static int waitTaps;
    static string? gdbBot;

    static string Describe(List<Bot> bots) => string.Join("; ", bots.Select(b =>
    {
        var l = b.S.Link;
        if (l == null) return $"{b.Name}: no game link";
        if (!l.TryReadLocal(out var me)) return $"{b.Name}: no state";
        return $"{b.Name}: scene {PlayerState.SceneName(me.SceneId)} inGame={me.InGame} ctx={l.CtxReady} gap={l.HeapGap / 1024}K heap={string.Join("/", l.HeapFree.Select(f => f / 1024))}K primed={l.ObjPrimed} frames={l.Frames}";
    }));

    static int SlotOf(Bot viewer, string name)
    {
        for (int i = 0; i < Mb.MaxRemote; i++)
            if (viewer.L.RemoteActive(i) && viewer.L.RemoteName(i) == name) return i;
        return -1;
    }

    /// <summary>
    /// The button that dismisses whatever holds the player without going anywhere: B declines a
    /// taxi / bus stop / teleport box ride (A would take it), A continues everything else (on the
    /// out-of-bounds tutorial B means Quit).
    /// </summary>
    static uint PromptAnswer(GameLink l)
    {
        uint ctl = l.TryReadLocal(out var me) ? me.ControlOff : 0;
        return (ctl & (0x20u | 0x40u | 0x80u)) != 0 ? 0x80000u : 0x10000u;
    }

    /// <summary>Taps A (or B, see PromptAnswer) until the game gives the player control (intro dialogs/cutscenes).</summary>
    static bool WaitControl(Bot b, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        int n = 0;
        while (DateTime.UtcNow < until)
        {
            if (b.L.TryReadLocal(out var me) && me.ControlOff == 0 && n > 4) { b.L.PadOverride(false); return true; }
            b.L.PadOverride(true, (n++ % 4) == 0 ? PromptAnswer(b.L) : 0u);
            Thread.Sleep(250);
        }
        b.L.PadOverride(false);
        return false;
    }

    static void TestPuppets(Bot A, Bot B)
    {
        bool ctl = WaitControl(A, TimeSpan.FromSeconds(60)) & WaitControl(B, TimeSpan.FromSeconds(30));
        if (!ctl) Log("warning: a player still has no control after the intro");
        A.L.TryReadLocal(out var start);
        var errs = new List<double>();
        var sw = Stopwatch.StartNew();
        double t = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(8))
        {
            t += 0.1;
            A.L.PadOverride(true, 0, (sbyte)(Math.Cos(t) * 110), (sbyte)(Math.Sin(t) * 110));
            Thread.Sleep(100);
            if (sw.Elapsed < TimeSpan.FromSeconds(2)) continue;
            int slot = SlotOf(B, "A");
            if (slot < 0 || !A.L.TryReadLocal(out var me)) continue;
            var p = B.L.PuppetPos(slot);
            if (p is not { } q) continue;
            errs.Add(Math.Sqrt(Sq(q.x - me.X) + Sq(q.y - me.Y) + Sq(q.z - me.Z)));
        }
        A.L.PadOverride(false);
        if (errs.Count == 0) { Result("puppet tracking", false, "B never showed A as a puppet"); return; }
        errs.Sort();
        double med = errs[errs.Count / 2], max = errs[^1];
        A.L.TryReadLocal(out var end);
        double moved = Math.Sqrt(Sq(end.X - start.X) + Sq(end.Z - start.Z));
        if (moved < 3) { Result("puppet tracking", false, $"A didn't move ({moved:F1} units; controlOff={end.ControlOff}), test inconclusive"); return; }
        Result("puppet tracking", med < 1.5, $"A walked {moved:F0} units in circles; B's puppet of A is off by median {med:F2}, max {max:F2} units ({errs.Count} samples, A ended at {end.X:F1},{end.Y:F1},{end.Z:F1})");
    }

    static double Sq(double v) => v * v;

    static readonly Dictionary<uint, uint> collected = new();
    static int gained; // object id -> fingerprint after collecting

    static void TestPickups(Bot A, Bot B, uint scene, string label, int want)
    {
        A.L.TryReadLocal(out var me);
        var objs = A.L.SceneObjects();
        var ha = A.L.ReadObjHashes(objs.Count);
        // only what a touch collects: shown, not collected/collecting, touch-collectable
        var picks = objs.Where(o => o.type == 4 && ha[o.index] != 0 && (o.x != 0 || o.z != 0))
            .Where(o => { var i = A.L.PickupInfo(o.index); return (i.flags & 2) != 0 && (i.state & 0x0C) == 0 && (i.state & 0x40) != 0; })
            .OrderBy(o => Sq(o.x - me.X) + Sq(o.z - me.Z)).Take(want * 3).ToList();
        int done = 0, synced = 0;
        var details = new List<string>();
        foreach (var o in picks)
        {
            if (done >= want) break;
            uint before = A.L.ReadObjHashes(objs.Count)[o.index];
            uint shiny0 = A.L.TryReadLocal(out var s0) ? s0.Shiny : 0;
            uint after = before;
            DateTime until;
            foreach (var dy in new[] { 0.3f, 1.0f, -0.4f })
            {
                // a hint (out of bounds tutorial...) takes control away and pickups ignore the player
                if (A.L.TryReadLocal(out var cs) && cs.ControlOff != 0) WaitControl(A, TimeSpan.FromSeconds(20));
                A.L.Teleport(o.x, o.y + dy, o.z);
                until = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < until)
                {
                    Thread.Sleep(100);
                    after = A.L.ReadObjHashes(objs.Count)[o.index];
                    if (after != before) break;
                }
                if (after != before) break;
            }
            if (after == before)
            {
                A.L.TryReadLocal(out var at);
                var pi = A.L.PickupInfo(o.index);
                details.Add($"#{o.index} {o.id:X8} not collected by touching it (A at {at.X:F1},{at.Y:F1},{at.Z:F1} for {o.x:F1},{o.y:F1},{o.z:F1}; state {pi.state:X} flags {pi.flags:X}; controlOff {at.ControlOff:X}; hp {at.Health})");
                if (details.Count == 1) Shot(A, "pickup-miss");
                continue;
            }
            done++;
            collected[o.id] = after;
            uint hb = 0;
            until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until)
            {
                Thread.Sleep(100);
                hb = B.L.ReadObjHashes(objs.Count)[o.index];
                if (hb == after) break;
            }
            A.L.TryReadLocal(out var s1);
            gained += (int)s1.Shiny - (int)shiny0;
            bool ok = hb == after;
            if (ok) synced++;
            details.Add($"#{o.index} {o.id:X8}: collected in A (shinies {shiny0}->{s1.Shiny}), {(ok ? "same in B" : $"B differs ({hb:X8} vs {after:X8})")}"
                + $" [state A {A.L.ObjField(o.index, 0x16C):X} B {B.L.ObjField(o.index, 0x16C):X}, flags A {A.L.ObjField(o.index, 0x18) >> 24:X} B {B.L.ObjField(o.index, 0x18) >> 24:X}, B applied {B.L.ObjStats.applied}]");
        }
        Result($"pickup sync ({label})", done > 0 && synced == done, $"{synced}/{done} collected pickups matched in B. " + string.Join("; ", details));
    }

    static void ListPickups(Bot A, uint scene)
    {
        var objs = A.L.SceneObjects();
        var ha = A.L.ReadObjHashes(objs.Count);
        var types = objs.GroupBy(o => o.type).OrderBy(g => g.Key)
            .Select(g => $"type 0x{g.Key:X2}: {g.Count()} ({g.Count(o => ha[o.index] != 0)} synced)");
        Log($"{PlayerState.SceneName(scene)} objects by type: " + string.Join(", ", types));
    }

    static readonly string[] Cinematics = { "cin_1", "cin_2", "cin_3", "cin_4", "cin_5", "cin_6", "cin_7", "cin_8", "cin_9", "cin_10", "cin_11", "cin_mm_post_death", "cin_mm_scare", "cin_sh_barrel" };

    public static string PickupName(byte type, byte index) => type == 0
        ? "shiny " + (index switch { 0 => "purple", 1 => "blue", 2 => "green", 3 => "yellow", 4 => "red", _ => index.ToString() })
        : index switch
        {
            0 => "spatula", 1 => "underwear", 2 => "golden underwear", 5 => "sundae", 6 => "sock", 7 => "spongeball",
            _ => $"item {index}",
        };

    /// <summary>What a scene holds: pickups by kind, cutscene managers, enemies, breakables.</summary>
    static string Survey(Bot b)
    {
        var objs = b.L.SceneObjects();
        var h = b.L.ReadObjHashes(objs.Count);
        var sb = new StringBuilder();
        sb.Append($"{objs.Count} objects, {h.Count(x => x != 0)} synced. ");
        var picks = objs.Where(o => o.type == 4).Select(o => (o, info: b.L.PickupInfo(o.index))).ToList();
        sb.Append("pickups: " + string.Join(", ", picks.GroupBy(p => (p.info.type, p.info.index)).OrderBy(g => g.Key)
            .Select(g => $"{PickupName(g.Key.type, g.Key.index)} ({g.Key.type}/{g.Key.index}) x{g.Count()} [touch {g.Count(p => (p.info.flags & 2) != 0)}, synced {g.Count(p => h[p.o.index] != 0)}, ids {string.Join(" ", g.Take(3).Select(p => $"#{p.o.index}:{p.o.id:X8}"))}]")));
        var cms = objs.Where(o => o.type == 0x28).Select(o =>
        {
            uint asset = b.L.CutsceneAssetOf(o.index);
            int story = Array.FindIndex(Cinematics, c => GameLink.StrHash(c) == asset);
            return $"#{o.index}:{o.id:X8}->{asset:X8}{(story >= 0 ? "=" + Cinematics[story] : "")}";
        });
        sb.Append(". cutscene mgrs: " + string.Join(" ", cms));
        // portals (0x10): where each one leads (xPortalAsset +0x14: scene id)
        var portals = objs.Where(o => o.type == 0x10).Select(o =>
        {
            uint asset = b.L.Mem.ReadU32(b.L.ObjPtr(o.index) + 0x10);
            uint dest = asset >= 0x80000000 ? b.L.Mem.ReadU32(asset + 0x14) : 0;
            return dest != 0 ? PlayerState.SceneName(dest) : "?";
        }).Distinct().OrderBy(x => x);
        sb.Append(". portals to: " + string.Join(" ", portals));
        sb.Append(". types: " + string.Join(", ", objs.GroupBy(o => o.type).OrderBy(g => g.Key).Select(g => $"{g.Key:X2}x{g.Count()}({g.Count(o => h[o.index] != 0)})")));
        if (probe != null)
        {
            var names = Names(b.L.ObjScene);
            string N(uint id) => names.TryGetValue(id, out var n) ? n : id.ToString("X8");
            foreach (var o in objs.Where(o => names.TryGetValue(o.id, out var n) && probe.IsMatch(n)))
            {
                uint flags = b.L.Mem.ReadU32(b.L.ObjPtr(o.index) + 4) & 0xFFFF;
                sb.Append($"\n  {N(o.id)} #{o.index} type {o.type:X2} flags {flags:X4} synced {h[o.index] != 0}: "
                    + string.Join(", ", b.L.Links(o.index).Select(l => $"{l.src}->{l.dst} {N(l.target)}")));
            }
        }
        return sb.ToString();
    }

    static System.Text.RegularExpressions.Regex? probe;
    static string? visible;
    static bool offscreen;   // --offscreen: the visible bot's window is parked off the desktop, shots come from its XFB
    static string? namesDir;
    static readonly Dictionary<uint, Dictionary<uint, string>> nameCache = new();

    /// <summary>Asset names of a scene (from hipnames dumps: "ID TYPE name" per line), if available.</summary>
    static Dictionary<uint, string> Names(uint scene)
    {
        if (nameCache.TryGetValue(scene, out var d)) return d;
        d = new();
        var f = namesDir == null ? null : Path.Combine(namesDir, PlayerState.SceneName(scene).ToLowerInvariant() + ".txt");
        if (f != null && File.Exists(f))
            foreach (var line in File.ReadLines(f))
            {
                var p = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 3 && uint.TryParse(p[0], System.Globalization.NumberStyles.HexNumber, null, out var id)) d[id] = p[2];
            }
        return nameCache[scene] = d;
    }

    /// <summary>--diag: jump height and the player's visibility, alone, with a teammate, after teleports.</summary>
    static void Diag(string party, int port, uint scene, string dolphin, string work, List<Bot> bots)
    {
        var A = StartBot("A", party, port, scene, dolphin, work);
        bots.Add(A);
        WaitAll(bots, scene, TimeSpan.FromSeconds(120));
        WaitControl(A, TimeSpan.FromSeconds(60));
        string Mat()
        {
            uint mdl = A.L.Global(0x704), fr = A.L.Global(0x728);
            uint m = A.L.Mem.ReadU32(mdl + 0x4C);
            var b = A.L.Mem.Read(m, 0x40);
            float F(int o) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(o));
            var f = A.L.Mem.Read(fr + 0x30, 12);
            float G(int o) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(f.AsSpan(o));
            return $"mat right {F(0):F2},{F(4):F2},{F(8):F2} up {F(16):F2},{F(20):F2},{F(24):F2} pos {F(48):F2},{F(52):F2},{F(56):F2}; frame pos {G(0):F2},{G(4):F2},{G(8):F2}; flags {A.L.Mem.ReadU32(mdl + 0x44) >> 16:X4} alpha {BitConverter.Int32BitsToSingle((int)A.L.Mem.ReadU32(mdl + 0x24)):F2} bucketNext {A.L.Mem.ReadU32(mdl + 0x38):X8}";
        }
        string Cam()
        {
            var c = A.L.Mem.Read(0x803C0558 + 0x14, 0x40);
            float F(int o) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(c.AsSpan(o));
            return $"camera at {F(48):F1},{F(52):F1},{F(56):F1} looking {F(32):F2},{F(36):F2},{F(40):F2}";
        }
        string Chain()
        {
            var sb = new StringBuilder();
            uint m = A.L.Global(0x704);
            for (int k = 0; m >= 0x80000000 && k < 16; k++)
            {
                uint mat = A.L.Mem.ReadU32(m + 0x4C), data = A.L.Mem.ReadU32(m + 0x10), parent = A.L.Mem.ReadU32(m + 4);
                uint fl = A.L.Mem.ReadU32(m + 0x44);
                int bones = (int)((fl >> 8) & 0xFF);
                var mb = A.L.Mem.Read(mat, (bones + 1) * 64);
                uint h = 2166136261; foreach (var x in mb) h = (h ^ x) * 16777619;
                float py = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(mb.AsSpan(0x34));
                // the shared atomic's world bounding sphere (RpAtomic +0x40.. : worldBoundingSphere center/radius per RW 3.x layout guess)
                sb.Append($"[{k} {m:X8} flags {fl >> 16:X4} bones {bones} parent {parent:X8} data {data:X8} matHash {h:X8} y {py:F2}] ");
                m = A.L.Mem.ReadU32(m);
            }
            return sb.ToString();
        }
        string Vis() => $"{Cam()}; {Mat()}; chain {Chain()}; visible {A.L.Global(0x16AC)} transparent {A.L.Global(0x1780)} ctl {A.L.Global(0x1788):X}";
        string Jump(string label)
        {
            Thread.Sleep(1500);
            A.L.TryReadLocal(out var s0);
            Shot(A, "before-" + label.Replace(' ', '-').Replace(",", "").Replace("(", "").Replace(")", ""));
            A.L.PadOverride(true, 0x10000);
            double top = s0.Y;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500)
            {
                if (sw.ElapsedMilliseconds > 400) A.L.PadOverride(true, 0);
                if (sw.ElapsedMilliseconds is > 300 and < 330) Shot(A, "mid-" + label.Replace(' ', '-').Replace(",", "").Replace("(", "").Replace(")", ""));
                if (A.L.TryReadLocal(out var s)) top = Math.Max(top, s.Y);
                if (sw.ElapsedMilliseconds % 300 < 20)
                {
                    uint fr = A.L.Global(0x728), mdl = A.L.Global(0x704);
                    uint mat = mdl >= 0x80000000 ? A.L.Mem.ReadU32(mdl + 0x4C) : 0;
                    float fy = fr >= 0x80000000 ? BitConverter.Int32BitsToSingle((int)A.L.Mem.ReadU32(fr + 0x34)) : 0;
                    float my = mat >= 0x80000000 ? BitConverter.Int32BitsToSingle((int)A.L.Mem.ReadU32(mat + 0x34)) : 0;
                    float vy = fr >= 0x80000000 ? BitConverter.Int32BitsToSingle((int)A.L.Mem.ReadU32(fr + 0xD8)) : 0;
                    Log($"  t={sw.ElapsedMilliseconds} local y {s.Y:F2} frame y {fy:F2} model y {my:F2} vel y {vy:F2} jumpState {A.L.Global(0x16D4)} isJumping {A.L.Global(0x1AD8)} pad on {A.L.Mem.ReadU32(0x8038C994 + 0x2C):X} frames {A.L.Frames}");
                }
                Thread.Sleep(20);
            }
            A.L.PadOverride(false);
            Log($"{label} state: {Vis()}");
            var r = $"{label}: jump rose {top - s0.Y:F2} units; {Vis()}";
            Result("diag " + label, top - s0.Y > 0.5, r);
            return r;
        }
        {
            A.L.TryReadLocal(out var w0);
            A.L.PadOverride(true, 0, 0, 110);
            Thread.Sleep(1500);
            A.L.PadOverride(false);
            A.L.TryReadLocal(out var w1);
            Result("diag walk", Math.Abs(w1.X - w0.X) + Math.Abs(w1.Z - w0.Z) > 1, $"walked from {w0.X:F1},{w0.Y:F1},{w0.Z:F1} to {w1.X:F1},{w1.Y:F1},{w1.Z:F1}");
        }
        Jump("alone");
        Thread.Sleep(25000);
        Log($"idle timers: minor {BitConverter.Int32BitsToSingle((int)A.L.Global(0x1774)):F1} major {BitConverter.Int32BitsToSingle((int)A.L.Global(0x1778)):F1} sit {BitConverter.Int32BitsToSingle((int)A.L.Global(0x177C)):F1}");
        Jump("alone after 25s idle");
        Jump("alone, second try");
        var B = StartBot("B", party, port, scene, dolphin, work);
        bots.Add(B);
        WaitAll(bots, scene, TimeSpan.FromSeconds(120));
        WaitControl(B, TimeSpan.FromSeconds(60));
        Thread.Sleep(2000);
        {
            uint ctxBase = A.L.Mem.ReadU32(GameLink.MailboxAddr + Mb.ODbgCtx), size = A.L.Mem.ReadU32(GameLink.MailboxAddr + Mb.ODbgCtxSize);
            string M(uint p)
            {
                uint m = A.L.Mem.ReadU32(p + 0x704 - 0x6E0);
                var parts = Enumerable.Range(0, 14).Select(k => A.L.Mem.ReadU32(p + 0x1F48 - 0x6E0 + (uint)k * 4).ToString("X8"));
                return $"model {m:X8} mat {A.L.Mem.ReadU32(m + 0x4C):X8} anim {A.L.Mem.ReadU32(m + 0x0C):X8} next {A.L.Mem.ReadU32(m):X8} frame {A.L.Mem.ReadU32(p + 0x728 - 0x6E0):X8} sb_models {string.Join(",", parts)}";
            }
            Log($"models: local {M(0x803C0558 + 0x6E0)}");
            for (uint i = 0; i < 5; i++) Log($"models: ctx{i} {M(ctxBase + i * size)}");
            string AnimInfo(uint p)
            {
                uint m = A.L.Mem.ReadU32(p + 0x704 - 0x6E0), an = A.L.Mem.ReadU32(m + 0x0C);
                var b = A.L.Mem.Read(an, 0x20);
                return string.Join(" ", Enumerable.Range(0, 8).Select(k => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(k * 4)).ToString("X8")));
            }
            Log($"anim local {AnimInfo(0x803C0558 + 0x6E0)}");
            for (uint i = 0; i < 5; i++) Log($"anim ctx{i} {AnimInfo(ctxBase + i * size)}");
        }
        Jump("with teammate");
        {
            const uint lo = 0x80250000, hi = 0x803CC400;
            List<uint[]> Snap(int n)
            {
                var l = new List<uint[]>();
                for (int k = 0; k < n; k++)
                {
                    var b = A.L.Mem.Read(lo, (int)(hi - lo));
                    var w = new uint[b.Length / 4];
                    for (int j = 0; j < w.Length; j++) w[j] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(j * 4));
                    l.Add(w); Thread.Sleep(150);
                }
                return l;
            }
            string FlagStats()
            {
                var h = new Dictionary<string, int>();
                for (int k = 0; k < 200; k++)
                {
                    var e = A.L.Mem.Read(0x803C0558 + 0x6F8, 8);
                    uint data = A.L.Mem.ReadU32(A.L.Global(0x704) + 0x10);
                    var sp = A.L.Mem.Read(data + 0x1C, 16);
                    float F(int o) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(sp.AsSpan(o));
                    var dr = A.L.Mem.Read(GameLink.MailboxAddr + 0x4564, 40 * 4);
                    float D(int k) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(dr.AsSpan(k * 4));
                    uint DU(int k) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(dr.AsSpan(k * 4));
                    if (k == 0) Log($"at render: culled {DU(0)} pos {D(1):F2},{D(2):F2},{D(3):F2} planes " + string.Join(" ", Enumerable.Range(0, 6).Select(q => $"[{D(4 + q * 4):F2},{D(5 + q * 4):F2},{D(6 + q * 4):F2} d{D(7 + q * 4):F1}]")) + $" rwcam {DU(28):X8} cur {DU(29):X8}; at cull: pos {D(31):F2},{D(32):F2},{D(33):F2} right {D(34):F2},{D(35):F2},{D(36):F2} sphere {D(37):F2},{D(38):F2},{D(39):F2}");
                    uint cam = A.L.Global(0x10);
                    var pl = A.L.Mem.Read(cam + 0x94, 6 * 20);
                    var mt = A.L.Mem.Read(A.L.Mem.ReadU32(A.L.Global(0x704) + 0x4C) + 0x30, 12);
                    float P(int o) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(pl.AsSpan(o));
                    float M(int o) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(mt.AsSpan(o));
                    float cx = M(0), cy = M(4) + 0.5f, cz = M(8);
                    var outs = Enumerable.Range(0, 6).Where(k => cx * P(k * 20) + cy * P(k * 20 + 4) + cz * P(k * 20 + 8) - P(k * 20 + 12) > 1.1f).Select(k => k.ToString());
                    string key = $"culled {e[5]} outside planes [{string.Join(",", outs)}] plane0 {P(0):F2},{P(4):F2},{P(8):F2} d{P(12):F1}";
                    h[key] = h.GetValueOrDefault(key) + 1;
                    Thread.Sleep(5);
                }
                return string.Join(" ", h.OrderByDescending(x => x.Value).Select(x => $"{x.Key}x{x.Value}"));
            }
            A.L.SetFlag(32, true); Thread.Sleep(800);
            Log("ent flags/culled, swap only: " + FlagStats());
            A.L.SetFlag(32, false); Thread.Sleep(800);
            Log("ent flags/culled, full update: " + FlagStats());
            A.L.SetFlag(32, true); Thread.Sleep(800);
            var swapOnly = Snap(6);
            A.L.SetFlag(32, false); Thread.Sleep(800);
            var full = Snap(6);
            var diffs = new List<string>();
            for (int j = 0; j < swapOnly[0].Length; j++)
            {
                bool s1 = swapOnly.All(x => x[j] == swapOnly[0][j]), s2 = full.All(x => x[j] == full[0][j]);
                uint ad = lo + 4 * (uint)j;
                if (ad >= 0x803C0558 && ad < 0x803C2520) continue;   // globals (swapped)
                if (s1 && s2 && swapOnly[0][j] != full[0][j]) diffs.Add($"{ad:X8}: {swapOnly[0][j]:X8} -> {full[0][j]:X8}");
            }
            Log("statics that differ (swap only -> full puppet update): " + string.Join("; ", diffs));
            var trace = new List<string>();
            for (int k = 0; k < 10; k++) { trace.Add($"{A.L.Mem.ReadU32(0x803CBB14):X8}/{A.L.Mem.ReadU32(0x803CBC28)}"); Thread.Sleep(1000); }
            Log("mem_base_alloc/alloc_num over 10 s (full update): " + string.Join(" ", trace));
            A.L.SetFlag(32, true); trace.Clear();
            for (int k = 0; k < 5; k++) { trace.Add($"{A.L.Mem.ReadU32(0x803CBB14):X8}/{A.L.Mem.ReadU32(0x803CBC28)}"); Thread.Sleep(1000); }
            A.L.SetFlag(32, false);
            Log("mem_base_alloc/alloc_num over 5 s (swap only): " + string.Join(" ", trace));
        }
        A.L.SetFlag(64, true);
        Jump("with teammate (re-eval before render)");
        A.L.SetFlag(64, false);
        A.L.SetFlag(16, true);
        Jump("with teammate (puppet not drawn)");
        A.L.SetFlag(32, true);
        Jump("with teammate (swap only, not drawn)");
        A.L.SetFlag(32, false);
        A.L.SetFlag(16, false);
        A.L.SetFlag(8, true);
        Jump("with teammate (cheap puppet)");
        A.L.SetFlag(8, false);
        Jump("with teammate again");
        var objs = A.L.SceneObjects();
        foreach (var o in objs.Where(o => o.type == 4 && (o.x != 0 || o.z != 0)).Take(5))
        {
            A.L.Teleport(o.x, o.y + 0.3f, o.z);
            Thread.Sleep(1500);
            Log($"after teleport to #{o.index}: {Vis()}");
            Shot(A, $"teleport-{o.index}");
        }
        Jump("after teleports");
        // without puppet contexts
        A.L.SetFlag(4, true);
        A.L.Warp(scene, reload: true);
        Thread.Sleep(3000);
        WaitFor(() => A.L.TryReadLocal(out var m) && m.InGame && !A.L.LoadPending, 60);
        WaitControl(A, TimeSpan.FromSeconds(60));
        Jump("no puppet contexts");
    }

    static void Shot(Bot b, string name, bool fromScreen = false)
    {
        if (visible != b.Name) return;
        var dir = Path.Combine(AppContext.BaseDirectory, "selftest", "shots");
        Directory.CreateDirectory(dir);
        try
        {
            // --offscreen: read the frame from emulated RAM, the window never shows on the desktop
            bool ok = offscreen ? Shots.CaptureXfb(b.L.Mem, Path.Combine(dir, name + ".png")) : Shots.Capture(b.S.DolphinPid, Path.Combine(dir, name + ".png"), fromScreen);
            if (!ok) Log("screenshot failed");
        } catch (Exception e) { Log("screenshot failed: " + e.Message); }
    }

    static void CheckCollectedStay(Bot A, Bot B, uint scene)
    {
        var objs = A.L.SceneObjects();
        var ha = A.L.ReadObjHashes(objs.Count);
        var hb = B.L.ReadObjHashes(objs.Count);
        int n = 0, okA = 0, okB = 0;
        foreach (var o in objs)
        {
            if (!collected.TryGetValue(o.id, out var h)) continue;
            n++;
            if (ha[o.index] == h) okA++;
            if (hb[o.index] == h) okB++;
        }
        Result("persistence", n > 0 && okA == n && okB == n, $"{n} pickups collected earlier in {PlayerState.SceneName(scene)}: still collected in A {okA}/{n}, in B {okB}/{n} after leaving and coming back");
    }

    /// <summary>The map sim holds the same object states as the players.</summary>
    static void CompareSim(ServerHost server, string party, uint scene, Bot A)
    {
        var p = server.FindParty(party);
        var sim = p == null ? null : server.Sims.Get(p, scene);
        if (sim == null) { Log("(no map sim for this scene: sims off or not up)"); return; }
        var until = DateTime.UtcNow.AddSeconds(30);
        while (sim.Status != "running" && DateTime.UtcNow < until) Thread.Sleep(250);
        var l = sim.Link;
        if (l == null || sim.Status != "running") { Result("map sim", false, $"sim not running ({sim.Status})"); return; }
        if (!A.L.TryReadLocal(out var am) || am.SceneId != scene) { Log($"(map sim {PlayerState.SceneName(scene)}: A has left the scene, nothing to compare)"); return; }
        // the sim (re)loads the scene on its own schedule: compare only once it runs this scene with
        // its object table primed, and give the last relayed states a moment to land
        WaitFor(() => l.ObjScene == scene && l.ObjPrimed, 60);
        var objs = A.L.SceneObjects();
        int tracked = 0;
        var diff = new List<byte>();
        var dl = DateTime.UtcNow.AddSeconds(12);
        var detail = new List<string>();
        do
        {
            detail.Clear();
            Thread.Sleep(1000);
            var ha = A.L.ReadObjHashes(objs.Count);
            var hs = l.ReadObjHashes(objs.Count);
            tracked = 0;
            diff.Clear();
            // what the sim is for: the party's changes (the server's store). Things every player's own
            // game changes by itself (a fly-through intro switching its trigger off) aren't party state.
            var changed = p!.ObjectsIn(scene).Select(st => st.Index).ToHashSet();
            foreach (var o in objs)
            {
                if (ha[o.index] == 0 && hs[o.index] == 0) continue;
                if (!changed.Contains((ushort)o.index)) continue;
                tracked++;
                if (ha[o.index] != hs[o.index] && o.type != 0x2B) { diff.Add(o.type); if (diff.Count <= 6) detail.Add($"{Nm(scene, o.id)} #{o.index} type {o.type:X2} A {ha[o.index]:X8} sim {hs[o.index]:X8}"); }
            }
        } while (diff.Count > 0 && DateTime.UtcNow < dl);
        if (detail.Count > 0) Log("map sim differs: " + string.Join("; ", detail));
        Result("map sim consistency", diff.Count == 0, $"{tracked} objects the party changed, {diff.Count} differ between A and the server's sim"
            + (diff.Count > 0 ? ": " + string.Join(", ", diff.GroupBy(t => t).Select(g => $"type 0x{g.Key:X2} x{g.Count()}")) : ""));
    }

    static void Compare(string name, Bot X, Bot Y, string? _, ISet<uint>? personal = null)
    {
        var objs = X.L.SceneObjects();
        int tracked = 0;
        var diff = new List<(int index, uint id, byte type)>();
        var dl = DateTime.UtcNow.AddSeconds(8);   // states in flight settle first
        while (true)
        {
            var hx = X.L.ReadObjHashes(objs.Count);
            var hy = Y.L.ReadObjHashes(objs.Count);
            tracked = 0;
            diff.Clear();
            foreach (var o in objs)
            {
                if (hx[o.index] == 0 && hy[o.index] == 0) continue;
                if (personal != null && personal.Contains(o.id)) continue;
                tracked++;
                if (hx[o.index] != hy[o.index]) diff.Add((o.index, o.id, o.type));
            }
            if (diff.All(d => d.type == 0x2B) || DateTime.UtcNow > dl) break;
            Thread.Sleep(500);
        }
        // enemies: only kills are shared, each game spawns and runs its own (alive bits may differ)
        int npc = diff.Count(d => d.type == 0x2B);
        diff.RemoveAll(d => d.type == 0x2B);
        var byType = diff.GroupBy(d => d.type).Select(g => $"type 0x{g.Key:X2} x{g.Count()}");
        if (diff.Count > 0)
        {
            uint sc = X.L.ObjScene;
            Log($"{name}: {X.Name} stats {X.L.ObjStats}, {Y.Name} stats {Y.L.ObjStats}; differing " + string.Join(", ", diff.Take(10).Select(d => $"{Nm(sc, d.id)} #{d.index} type {d.type:X2}" + $" hash {X.L.ReadObjHashes(d.index + 1)[d.index]:X8}/{Y.L.ReadObjHashes(d.index + 1)[d.index]:X8} baseFlags {X.L.ObjField(d.index, 4) & 0xFFFF:X}/{Y.L.ObjField(d.index, 4) & 0xFFFF:X}" + (d.type == 0x1B || d.type == 0x16 ? $" [{X.Name} {X.L.ObjField(d.index, 0xD8):X}/{X.L.ObjField(d.index, 0xDC):X}/{X.L.ObjField(d.index, 0x14):X8}/{X.L.ObjField(d.index, 0x18):X8} {Y.Name} {Y.L.ObjField(d.index, 0xD8):X}/{Y.L.ObjField(d.index, 0xDC):X}/{Y.L.ObjField(d.index, 0x14):X8}/{Y.L.ObjField(d.index, 0x18):X8}]" : ""))));
        }
        Result(name, diff.Count == 0, $"{tracked} tracked objects, {diff.Count} differ between {X.Name} and {Y.Name}" + (diff.Count > 0 ? ": " + string.Join(", ", byType) : "")
            + (npc > 0 ? $" ({npc} enemies alive in one game only: own spawning)" : ""));
    }

    static int Finish(List<Bot> bots, Dictionary<string, string> a)
    {
        // a hung game under the debugger: leave it up for tools\gdb.py to sample
        if (gdbBot != null && AnyHung) { Log("hang with --gdb: keeping the games up 90 s for sampling"); Thread.Sleep(90000); }
        var md = new StringBuilder();
        md.AppendLine($"# BFBB Online self-test, {DateTime.Now:yyyy-MM-dd HH:mm}");
        md.AppendLine();
        md.AppendLine($"**{results.Count(r => r.ok)}/{results.Count} passed**");
        md.AppendLine();
        md.AppendLine("| | test | details |");
        md.AppendLine("|---|---|---|");
        foreach (var (n, ok, d) in results) md.AppendLine($"| {(ok ? "PASS" : "FAIL")} | {n} | {d.Replace("|", "/")} |");
        var path = Path.Combine(AppContext.BaseDirectory, "selftest-report.md");
        File.WriteAllText(path, md.ToString());
        Log($"report: {path}");
        if (!a.ContainsKey("keep"))
            foreach (var b in bots)
            {
                try { if (b.S.DolphinPid != 0) Process.GetProcessById(b.S.DolphinPid).Kill(); } catch { }
                b.S.Dispose();
            }
        return results.All(r => r.ok) ? 0 : 1;
    }
}
