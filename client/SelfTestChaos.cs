using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BfbbOnline.Shared;

namespace BfbbOnline.Client;

/// <summary>
/// BFBB-Online.exe --selftest --chaos [--minutes 20] [--seed N] [--bots 4] [--stagger 60]
///   [--routes "HB01,HB02,JF01;BB01,GL01;..."] [--dwell 90] [--visible A]
/// Crash / bug hunt: up to 6 players on this PC, each joining at a different time and playing
/// through its own route of levels (walkthrough order, Deluxe-safe scenes), mashing every in-game
/// button (jumps, attacks, bowl, cruise bubble, sneak, HUD, pause), collecting pickups, smashing
/// breakables and enemies, paying shiny doors/clams, dying out of bounds. Routes cross so players
/// meet in the same level now and then; at the end everyone meets in HB01.
/// Glitches and speedrun strats (cruise boost, void cruise, theater shinies...) are done on purpose
/// but never reported as bugs: they stay in the game. Only crashes, hangs and desyncs count.
/// Checks while they play: no hang, no crash (a crashed game is restarted = rejoin test), shared
/// counters agree across all games (shinies, spatulas, socks), spatulas/socks never go down,
/// shinies only go down through a payment, the same objects are collected in games sharing a level.
/// </summary>
static partial class SelfTest
{
    // GameCube buttons as the game's xPad bits (bfbb xPad.h)
    const uint GcA = 0x10000, GcX = 0x20000, GcY = 0x40000, GcB = 0x80000, GcL = 0x100, GcR = 0x1000, GcZ = 0x100000, GcStart = 0x1;
    const uint EvtGiveShiny = 53;

    // routes from the walkthrough, world by world (B301..B303 left out: the stock game breaks there)
    static readonly string[] ChaosRoutes =
    {
        "HB01,HB02,HB03,HB04,JF01,JF02,JF03,JF04,HB01",
        "HB01,BB01,BB02,BB03,BB04,GL01,GL02,GL03,B101,HB09",
        "HB09,RB01,RB02,RB03,SM01,SM02,SM03,SM04,B201,HB05",
        "HB01,KF01,KF02,KF04,KF05,GY01,GY02,GY03,GY04,HB08",
        "HB01,HB06,HB07,HB10,HB01,HB08,JF05,JF06,BB05,GL04,GL05,RB04",
        "HB09,BC01,BC02,BC03,BC04,BC05,DB01,DB02,DB03,DB04,DB06,SM05",
    };

    // walkthrough: levels with a clam / shiny payment (and Mr. Krabs in HB01): spend more often there
    static readonly HashSet<uint> PayScenes = new[] { "HB01", "BB02", "BB04", "GL01", "GL03", "BC02", "SM01", "GY01" }.Select(PlayerState.SceneIdOf).ToHashSet();
    // walkthrough glitch list: Cruise Bubble while falling into the void (DB03: invisible / tan SpongeBob)
    static readonly HashSet<uint> VoidCruiseScenes = new[] { "DB03", "BB01", "RB01", "SM04" }.Select(PlayerState.SceneIdOf).ToHashSet();

    // the game's cheat table (bfbb zGameExtras.cpp sCheat*: the last 8 / 12 inputs; the guides' lists are wrong).
    // Entered on the pause menu while holding L+R. 'X' = GC X, 'Y' = GC Y.
    static readonly (string name, string keys)[] Cheats =
    {
        ("+1000 shinies", "YXXYYXXY"), ("+10 spatulas", "XYYXXYYX"), ("bubble bowl", "XYXYXXYY"),
        ("cruise bubble", "YXYXYYXX"), ("art theatre", "YXYXXYXY"), ("restore health", "XXXXYXYXYYYY"),
        ("no pants", "XXXXYXXYXYYX"), ("cruise control", "XXXXYYXXYXYY"), ("big plankton", "YYYYXYXYXXXX"),
        ("small townsfolk", "YYYYYXYXYXYX"), ("small co-stars", "YYYYXYXYYYYY"), ("swap camera L/R", "YYXXXXYY"),
    };

    sealed class ChaosStats
    {
        public int Scenes, Actions, Pickups, Smashed, Pays, Deaths, Restarts, Hangs;
        public uint MaxSpat, MaxSock;
        public readonly List<string> Issues = new();
    }

    static readonly object resultLock = new();

    static void ChaosResult(string name, bool ok, string detail) { lock (resultLock) Result(name, ok, detail); }

    static void ChaosMode(Dictionary<string, string> a, List<Bot> bots)
    {
        double minutes = double.Parse(a.GetValueOrDefault("minutes", "20"), System.Globalization.CultureInfo.InvariantCulture);
        int seed = int.Parse(a.GetValueOrDefault("seed", Environment.TickCount.ToString()));
        int count = Math.Clamp(int.Parse(a.GetValueOrDefault("bots", "4")), 2, 6);
        double stagger = double.Parse(a.GetValueOrDefault("stagger", "60"), System.Globalization.CultureInfo.InvariantCulture);
        double dwell = double.Parse(a.GetValueOrDefault("dwell", "90"), System.Globalization.CultureInfo.InvariantCulture);
        var routes = (a.TryGetValue("routes", out var rs) ? rs.Split(';') : ChaosRoutes)
            .Select(r => r.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(PlayerState.SceneIdOf).ToArray()).ToArray();
        Log($"chaos: {count} players, {minutes} min, seed {seed}, join every {stagger} s, ~{dwell} s per level");
        var end = DateTime.UtcNow.AddMinutes(minutes);
        var stats = new Dictionary<string, ChaosStats>();
        var threads = new List<Thread>();
        var live = new List<Bot>();
        string[] names = { "A", "B", "C", "D", "E", "F" };

        for (int i = 0; i < count; i++)
        {
            int k = i;
            var st = new ChaosStats();
            stats[names[k]] = st;
            var th = new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(stagger * k));   // players join at different times
                if (DateTime.UtcNow > end) return;
                try { ChaosPlayer(names[k], routes[k % routes.Length], new Random(seed + 7919 * k), end, dwell, st, live, bots); }
                catch (Exception e) { lock (st) st.Issues.Add("player thread crashed: " + e.Message); Log($"{names[k]}: chaos thread crashed: {e}"); }
            }) { IsBackground = true, Name = "chaos-" + names[k] };
            threads.Add(th);
            th.Start();
        }

        // referee: shared counters agree across every game that is in a level and settled
        var mism = new Dictionary<string, int>();
        int checks = 0;
        while (DateTime.UtcNow < end && threads.Any(t => t.IsAlive))
        {
            Thread.Sleep(5000);
            List<Bot> now;
            lock (live) now = live.Where(b => b.Hung == null && b.S.GameRunning && b.S.Link != null).ToList();
            var snap = new List<(string name, uint[] c)>();
            foreach (var b in now)
                try { if (b.L.TryReadLocal(out var me) && me.InGame && !b.L.LoadPending && b.L.CountersApplied && b.L.ObjPrimed) snap.Add((b.Name, b.L.ReadCounters())); } catch { }
            if (snap.Count < 2) continue;
            checks++;
            foreach (var (label, idx) in new[] { ("shinies", 0), ("spatulas", 1), ("socks", 2) })
            {
                bool same = snap.All(s => s.c[idx] == snap[0].c[idx]);
                mism[label] = same ? 0 : mism.GetValueOrDefault(label) + 1;
                // a change takes a moment to reach everyone: 4 checks in a row (~20 s) is a bug
                if (mism[label] == 4)
                    ChaosResult($"chaos: shared {label}", false, $"games disagree for 20 s: " + string.Join(", ", snap.Select(s => $"{s.name} {s.c[idx]}")));
            }
        }
        foreach (var t in threads) t.Join(TimeSpan.FromSeconds(240));

        // finale: everyone still up meets in HB01 and compares the world
        List<Bot> up;
        lock (live) up = live.Where(b => b.Hung == null && b.S.GameRunning).ToList();
        uint hb01 = PlayerState.SceneIdOf("HB01");
        if (up.Count >= 2 && GoTo(up, hb01))
        {
            Thread.Sleep(5000);
            for (int i = 1; i < up.Count; i++) Compare($"chaos finale: world {up[0].Name} vs {up[i].Name}", up[0], up[i], null);
            var c = up.Select(b => (b.Name, b.L.ReadCounters())).ToList();
            bool same = c.All(x => x.Item2.Take(3).SequenceEqual(c[0].Item2.Take(3)));
            ChaosResult("chaos finale: shared counters", same, string.Join("; ", c.Select(x => $"{x.Name}: shinies {x.Item2[0]} spatulas {x.Item2[1]} socks {x.Item2[2]}")));
        }
        else ChaosResult("chaos finale", false, up.Count < 2 ? "fewer than 2 games still up" : Describe(up));

        ChaosResult("chaos: counter checks", true, $"{checks} cross-game checks");
        foreach (var (n, st) in stats)
            lock (st)
                ChaosResult($"chaos player {n}", st.Hangs == 0 && st.Issues.Count == 0,
                    $"{st.Scenes} levels, {st.Actions} actions, {st.Pickups} pickups, {st.Smashed} smash attempts, {st.Pays} payments, {st.Deaths} OOB deaths, {st.Restarts} restarts, {st.Hangs} hangs/crashes"
                    + (st.Issues.Count > 0 ? ". Issues: " + string.Join(" | ", st.Issues.Take(12)) + (st.Issues.Count > 12 ? $" (+{st.Issues.Count - 12} more)" : "") : ""));
    }

    static void ChaosPlayer(string name, uint[] route, Random rng, DateTime end, double dwell, ChaosStats st, List<Bot> live, List<Bot> bots)
    {
        void Issue(string s) { lock (st) st.Issues.Add(s); Log($"{name}: ISSUE {s}"); }
        Bot b = StartBot(name, ctx.Party, ctx.Port, route[0], ctx.Dolphin, ctx.Work);
        lock (live) { live.Add(b); }
        lock (bots) bots.Add(b);
        if (!WaitAll(new List<Bot> { b }, route[0], TimeSpan.FromSeconds(150))) { Issue($"never came up in {SN(route[0])}: {Describe(new List<Bot> { b })}"); return; }
        WaitControl(b, TimeSpan.FromSeconds(60));

        uint lastSpat = 0, lastSock = 0;
        int leg = 0;
        while (DateTime.UtcNow < end)
        {
            uint scene = route[leg % route.Length];
            leg++;
            // go to the next level of the route (crash/hang -> restart the game and rejoin)
            if (!ChaosEnsure(ref b, name, scene, st, live, bots, Issue)) return;
            lock (st) st.Scenes++;
            Log($"{name}: playing {SN(scene)}");
            var until = DateTime.UtcNow.AddSeconds(dwell * (0.6 + 0.8 * rng.NextDouble()));
            if (until > end) until = end;
            while (DateTime.UtcNow < until)
            {
                if (b.Hung != null || !b.S.GameRunning) break;
                if (!b.L.TryReadLocal(out var me) || !me.InGame) { Thread.Sleep(500); continue; }
                if (me.SceneId != scene)
                {
                    // a level-ending pickup / portal / teammate's story cutscene took us elsewhere: play there
                    Log($"{name}: moved {SN(scene)} -> {SN(me.SceneId)} by the game");
                    if (!WaitAll(new List<Bot> { b }, me.SceneId, TimeSpan.FromSeconds(60))) break;
                    scene = me.SceneId;
                }
                if (me.ControlOff != 0) WaitControl(b, TimeSpan.FromSeconds(10));
                uint shiny0 = b.L.ReadCounters()[0], pay0 = b.L.PaySeq;
                long payAny0 = Interlocked.Read(ref payEpoch);
                string act = ChaosAction(b, rng, st);
                lock (st) st.Actions++;
                if (act == StuckPause)
                {
                    // test limitation: the bots can't always drive the pause menu UI; restart the game
                    Log($"{name}: {StuckPause} in {SN(scene)} (bot limitation), restarting the game");
                    b.Hung = StuckPause;
                    break;
                }
                if (b.Hung != null || !b.S.GameRunning) { Issue($"game died during '{act}' in {SN(scene)}"); break; }
                // invariants of this game's own counters
                var c = b.L.ReadCounters();
                if (c[1] < lastSpat) Issue($"spatulas went down {lastSpat} -> {c[1]} in {SN(scene)} after '{act}'");
                if (c[2] < lastSock) Issue($"socks went down {lastSock} -> {c[2]} in {SN(scene)} after '{act}'");
                lastSpat = Math.Max(lastSpat, c[1]); lastSock = Math.Max(lastSock, c[2]);
                // shinies are shared: a teammate paying (during this action or just before) lowers ours too
                bool anyPay = Interlocked.Read(ref payEpoch) != payAny0 || Interlocked.Read(ref paying) > 0 || DateTime.UtcNow.Ticks < Interlocked.Read(ref payQuietUntil);
                if (c[0] + 200 < shiny0 && b.L.PaySeq == pay0 && act != "pay" && !anyPay)
                    Issue($"shinies dropped {shiny0} -> {c[0]} with no payment in {SN(scene)} after '{act}'");
                lock (st) { st.MaxSpat = lastSpat; st.MaxSock = lastSock; }
            }
            if (b.Hung == null && b.S.GameRunning) ChaosCompareMates(b, live, scene, Issue);
        }
    }

    /// <summary>Gets <paramref name="b"/> into <paramref name="scene"/>; a hung or crashed game is killed and restarted.</summary>
    static bool ChaosEnsure(ref Bot b, string name, uint scene, ChaosStats st, List<Bot> live, List<Bot> bots, Action<string> issue)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (b.Hung == null && b.S.GameRunning)
            {
                if (GoTo(new List<Bot> { b }, scene)) return true;
                // still running but stuck elsewhere: note it and play wherever the game is
                if (b.Hung == null && b.S.GameRunning) { issue($"could not get into {SN(scene)}: {Describe(new List<Bot> { b })}"); return true; }
            }
            // crashed or hung: record it, then restart the game (also tests rejoining mid-session)
            string why = b.Hung ?? "Dolphin closed";
            if (why == StuckPause) lock (st) st.Restarts++;
            else
            {
                lock (st) { st.Hangs++; st.Restarts++; }
                issue($"CRASH/HANG heading to {SN(scene)}: {why}" + (b.S.Link != null ? " | " + SafeCpu(b) : ""));
                ChaosResult($"chaos {name}: crash/hang #{st.Hangs}", false, why);
            }
            lock (watched) watched.Remove(b);
            lock (live) live.Remove(b);
            lock (bots) bots.Remove(b);
            try { b.S.KillGame(); b.S.Dispose(); } catch { }
            Thread.Sleep(3000);
            b = StartBot(name, ctx.Party, ctx.Port, scene, ctx.Dolphin, ctx.Work);
            lock (live) live.Add(b);
            lock (bots) bots.Add(b);
            if (WaitAll(new List<Bot> { b }, scene, TimeSpan.FromSeconds(150))) { WaitControl(b, TimeSpan.FromSeconds(60)); return true; }
        }
        issue("gave up after 3 restarts");
        return false;
    }

    static bool ChaosMatesHave(Bot b, List<Bot> live, int i, uint v)
    {
        List<Bot> mates;
        lock (live) mates = live.Where(m => m != b && m.Hung == null && m.S.GameRunning).ToList();
        foreach (var m in mates) try { if (m.L.ReadCounters()[i] == v) return true; } catch { }
        return false;
    }

    static string SafeCpu(Bot b) { try { return CpuSample(b.L); } catch { return "no cpu sample"; } }

    /// <summary>One random thing a player would do; returns its name.</summary>
    static string ChaosAction(Bot b, Random rng, ChaosStats st)
    {
        var L = b.L;
        L.TryReadLocal(out var me);
        int roll = rng.Next(100);
        if (roll < 22) { if (ChaosCollect(b, rng)) lock (st) st.Pickups++; return "collect"; }
        if (roll < 36) { ChaosSmash(b, rng); lock (st) st.Smashed++; return "smash"; }
        if (roll < (PayScenes.Contains(me.SceneId) ? 52 : 42)) { if (ChaosPay(b, rng)) lock (st) st.Pays++; return "pay"; }
        if (roll < 55 && me.Character == 0 && VoidCruiseScenes.Contains(me.SceneId))
        {
            // walkthrough glitch: fire a Cruise Bubble and fall into the void while it flies.
            // Speedrun strats / vanilla glitches are NOT bugs to fix: only a crash, hang or desync counts.
            Hold(L, GcL, 0, 0, 150); Hold(L, 0, 0, 0, 400);
            L.Teleport(me.X, me.Y - 400f, me.Z);
            Hold(L, GcA, 50, 0, 1500);
            L.PadOverride(false);
            Thread.Sleep(3500);
            WaitControl(b, TimeSpan.FromSeconds(20));
            lock (st) st.Deaths++;
            return "void cruise";
        }
        if (roll < 58 && me.Character == 0)
        {
            // cruise boost: fire on the frame a hazard/out-of-bounds hits, holding a direction
            L.Teleport(me.X, me.Y - 400f, me.Z);
            Hold(L, GcL, 100, 0, 150); Hold(L, 0, 100, 0, 1500);
            L.PadOverride(false);
            Thread.Sleep(3000);
            WaitControl(b, TimeSpan.FromSeconds(20));
            return "cruise boost";
        }
        if (roll < 60)
        {
            // a cheat: pause, hold L+R, X/Y sequence, release, unpause (only party counters are checked;
            // spatulas/shinies from cheats go to the whole party)
            var (cn, keys) = Cheats[rng.Next(Cheats.Length)];
            if (!EnterCheat(L, keys)) return StuckPause;
            Log($"{b.Name}: cheat '{cn}' (counters now {string.Join(",", L.ReadCounters().Take(3))})");
            return "cheat " + cn;
        }
        if (roll < 61)
        {
            // fall out of the world: scene reset path (objects restored, counters kept)
            L.Teleport(me.X, me.Y - 400f, me.Z);
            Thread.Sleep(5000);
            WaitControl(b, TimeSpan.FromSeconds(20));
            lock (st) st.Deaths++;
            return "oob death";
        }
        if (roll < 64)
        {
            // pause menu open and closed again
            // (no D-pad: Start on another menu entry opens a submenu and the game stays paused)
            Tap(L, GcStart, 150); Thread.Sleep(1200);
            return Unpause(L) ? "pause" : StuckPause;
        }
        if (roll < 67) { Hold(L, GcZ, 0, 0, 1500); return "hud"; }
        // a move from the controls guide, while running in some direction
        sbyte ax = (sbyte)rng.Next(-110, 111), ay = (sbyte)rng.Next(-110, 111);
        string[] combos = me.Character switch
        {
            1 => new[] { "jump", "double", "belly", "cartwheel", "flop", "pickup", "throw", "sneak", "run" },
            2 => new[] { "jump", "double", "chop", "kick", "lasso", "copter", "swing", "sneak", "run" },
            _ => new[] { "jump", "double", "spin", "airspin", "bash", "bounce", "bowl", "cruise", "sneak", "run", "stall" },
        };
        string c = combos[rng.Next(combos.Length)];
        switch (c)
        {
            case "jump": Hold(L, GcA, ax, ay, 300); Hold(L, 0, ax, ay, 600); break;
            case "double": Hold(L, GcA, ax, ay, 150); Hold(L, 0, ax, ay, 250); Hold(L, GcA, ax, ay, 150); Hold(L, 0, ax, ay, 800); break;
            case "spin": case "belly": case "chop": Hold(L, GcB, 0, 0, 150); Hold(L, 0, 0, 0, 500); break;
            case "cartwheel": Hold(L, 0, ax, ay, 400); Hold(L, GcB, ax, ay, 150); Hold(L, 0, ax, ay, 600); break;
            case "airspin": case "kick": Hold(L, GcA, ax, ay, 150); Hold(L, 0, ax, ay, 200); Hold(L, GcB, ax, ay, 150); Hold(L, 0, ax, ay, 600); break;
            case "bash": case "lasso": Hold(L, GcY, 0, 0, 150); Hold(L, 0, 0, 0, 700); break;
            case "bounce": case "flop": Hold(L, GcA, 0, 0, 150); Hold(L, 0, 0, 0, 300); Hold(L, GcY, 0, 0, 150); Hold(L, 0, 0, 0, 900); break;
            case "bowl": Hold(L, GcB, 0, 0, 900); Hold(L, 0, ax, 0, 1200); break;
            case "cruise":
                Hold(L, GcL, 0, 0, 150); Hold(L, 0, ax, ay, 600); Hold(L, GcA, ax, ay, 900);
                Hold(L, GcB, 0, 0, 150); Hold(L, 0, 0, 0, 800); break;   // detonate early
            case "copter": Hold(L, GcA, ax, ay, 150); Hold(L, 0, ax, ay, 200); Hold(L, GcA, ax, ay, 150); Hold(L, 0, ax, ay, 100); Hold(L, GcA, ax, ay, 1400); break;
            case "swing": Hold(L, GcA, ax, ay, 150); Hold(L, 0, ax, ay, 200); Hold(L, GcX, ax, ay, 150); Hold(L, 0, ax, ay, 600); Hold(L, GcA, 0, 0, 150); break;
            case "pickup": Hold(L, GcX, 0, 0, 150); Hold(L, 0, 0, 0, 600); break;
            case "throw": Hold(L, GcX, 0, 0, 1200); Hold(L, 0, 0, 0, 800); break;
            case "sneak": Hold(L, GcR, (sbyte)(ax / 3), (sbyte)(ay / 3), 1500); break;
            case "stall": Hold(L, GcB, ax, ay, 150); Hold(L, GcA, ax, ay, 150); Hold(L, 0, ax, ay, 300); Hold(L, GcY, ax, ay, 150); Hold(L, 0, ax, ay, 700); break;
            default: Hold(L, 0, ax, ay, 1200 + rng.Next(1500)); if (rng.Next(3) == 0) Tap(L, GcX, 150); break;
        }
        L.PadOverride(false);
        return c;
    }

    /// <summary>Pause, hold L+R, the X/Y sequence, release, unpause.</summary>
    const string StuckPause = "stuck on the pause menu";

    static bool EnterCheat(GameLink L, string keys)
    {
        Tap(L, GcStart, 150); Thread.Sleep(1200);
        if (cheatDiag) Log($"cheat diag: after Start gameMode {L.Mem.ReadU32(0x803CB8A8)} gTrcPad {Convert.ToHexString(L.Mem.Read(0x803BEF88, 0x30))}");
        Hold(L, GcL | GcR, 0, 0, 200);
        foreach (char k in keys) { Hold(L, GcL | GcR | (k == 'X' ? GcX : GcY), 0, 0, 80); Hold(L, GcL | GcR, 0, 0, 80); }
        if (cheatDiag)
            Log($"cheat diag: gameMode {L.Mem.ReadU32(0x803CB8A8)} pressed buffer {string.Join(" ", Enumerable.Range(0, 16).Select(k => L.Mem.ReadU32(0x8028F8A4 + 4 * (uint)k).ToString("X")))} chEnabled {L.Mem.ReadU32(0x803CB890):X}");
        Hold(L, 0, 0, 0, 300);
        L.PadOverride(false);
        return Unpause(L);
    }
    static bool cheatDiag;

    /// <summary>
    /// Leaves the pause menu (gGameMode 7). Bot pad taps don't reliably reach the menu's resume entry,
    /// so the patch runs the same dispatcher event it sends (SetGameState_Play); taps are the fallback.
    /// </summary>
    static bool Unpause(GameLink L)
    {
        for (int k = 0; k < 3 && L.Mem.ReadU32(0x803CB8A8) == 7; k++)
        {
            L.Resume();
            for (int w = 0; w < 20 && L.Mem.ReadU32(0x803CB8A8) == 7; w++) Thread.Sleep(100);
        }
        if (L.Mem.ReadU32(0x803CB8A8) != 7) return true;
        Log("resume event didn't leave the pause menu, trying pad taps");
        uint[] tries = { GcStart, GcB, GcA, GcX, GcY, GcStart, GcB, GcA, GcX, GcY, GcStart, GcA, GcStart, GcB, GcA };
        for (int k = 0; k < tries.Length; k++)
        {
            Thread.Sleep(900);
            if (L.Mem.ReadU32(0x803CB8A8) != 7) { if (cheatDiag && k > 0) Log($"unpaused after {k} taps, last {tries[k - 1]:X}"); return true; }
            Tap(L, tries[k], 100);
        }
        Thread.Sleep(600);
        bool ok = L.Mem.ReadU32(0x803CB8A8) != 7;
        if (!ok) Log($"still paused after {tries.Length} taps (gameMode 7)");
        return ok;
    }

    /// <summary>
    /// --phases cheats: A enters "+1000 shinies" and "+10 spatulas"; both games (and late loads)
    /// must end up with them, and the launcher must not take the spatulas back.
    /// </summary>
    static void CheatsPhase(List<Bot> bots)
    {
        var A = bots[0]; var B = bots[1];
        cheatDiag = true;
        foreach (var bt in bots) WaitControl(bt, TimeSpan.FromSeconds(30));
        foreach (var (cn, keys, idx, add) in new[] { ("+1000 shinies", "YXXYYXXY", 0, 1000u), ("+10 spatulas", "XYYXXYYX", 1, 10u) })
        {
            // (leaving the pause menu reloads the level for the bots: wait until it's playable again)
            WaitAll(bots, SceneOf(A) != 0 ? SceneOf(A) : ctx.Scene, TimeSpan.FromSeconds(60));
            Thread.Sleep(2000);
            WaitControl(A, TimeSpan.FromSeconds(30));
            uint a0 = A.L.ReadCounters()[idx], b0 = B.L.ReadCounters()[idx];
            uint want = idx == 1 ? Math.Min(100, a0 + add) : Math.Min(50000, a0 + add);
            EnterCheat(A.L, keys);   // (a bot left on the pause menu is fine here: the counters publish while paused)
            bool ok = WaitFor(() => A.L.ReadCounters()[idx] == want && B.L.ReadCounters()[idx] == want, 10, 250);
            // still there after the launcher's 3 s spatula check
            Thread.Sleep(5000);
            uint a1 = A.L.ReadCounters()[idx], b1 = B.L.ReadCounters()[idx];
            ok &= a1 == want && b1 == want;
            Result($"cheat {cn}", ok, $"A {a0} -> {a1}, B {b0} -> {b1} (want {want})");
        }
    }

    static void Hold(GameLink l, uint buttons, sbyte ax, sbyte ay, int ms) { l.PadOverride(true, buttons, ax, ay); Thread.Sleep(ms); }
    static void Tap(GameLink l, uint buttons, int ms) { l.PadOverride(true, buttons); Thread.Sleep(ms); l.PadOverride(false); }

    /// <summary>Goes to a random touch-collectable pickup nearby and takes it.</summary>
    static bool ChaosCollect(Bot b, Random rng)
    {
        var L = b.L;
        L.TryReadLocal(out var me);
        var objs = L.SceneObjects();
        var h = L.ReadObjHashes(objs.Count);
        var picks = objs.Where(o => o.type == 4 && h[o.index] != 0 && (o.x != 0 || o.z != 0))
            .Where(o => { var i = L.PickupInfo(o.index); return (i.flags & 2) != 0 && (i.state & 0x0C) == 0 && (i.state & 0x40) != 0; })
            .OrderBy(o => Sq(o.x - me.X) + Sq(o.z - me.Z)).Take(6).ToList();
        if (picks.Count == 0) return false;
        var p = picks[rng.Next(picks.Count)];
        uint before = h[p.index];
        foreach (var dy in new[] { 0.3f, 1.0f })
        {
            L.Teleport(p.x, p.y + dy, p.z);
            for (int k = 0; k < 20; k++) { Thread.Sleep(100); if (L.ReadObjHashes(p.index + 1)[p.index] != before) return true; }
        }
        return false;
    }

    /// <summary>Goes next to a breakable or an enemy and attacks it a few times.</summary>
    static void ChaosSmash(Bot b, Random rng)
    {
        var L = b.L;
        L.TryReadLocal(out var me);
        var objs = L.SceneObjects();
        var targets = objs.Where(o => (o.type == 0x1B || (o.type == 0x2B && IsEnemy(b, o.index))) && (o.x != 0 || o.z != 0))
            .OrderBy(o => Sq(o.x - me.X) + Sq(o.z - me.Z)).Take(8).ToList();
        if (targets.Count == 0) return;
        var t = targets[rng.Next(targets.Count)];
        L.Teleport(t.x + 1.5f, t.y + 1.0f, t.z + 1.5f);
        Thread.Sleep(600);
        for (int k = 0; k < 4; k++)
        {
            uint btn = (k % 3) switch { 0 => GcB, 1 => GcY, _ => GcA };
            Hold(L, btn, 0, 0, 150);
            if (btn == GcA) { Hold(L, 0, 0, 0, 250); Hold(L, GcY, 0, 0, 150); }   // bubble bounce / belly flop
            Hold(L, 0, 0, 0, 600);
        }
        L.PadOverride(false);
    }

    /// <summary>
    /// Spending: an object whose link gives the player a negative number of shinies (shiny door,
    /// clam bridge, Mr. Krabs) is visited and interacted with (X, then A to confirm).
    /// </summary>
    /// <summary>
    /// Paying objects in the current scene: talk boxes whose answer (TalkBox_OnYes 453, Mr. Krabs'
    /// OnSignal1 343...) sends GiveShinyObjects (53) with a negative amount. They have no position
    /// (reached through triggers/conditions), so a bot starts their conversation directly.
    /// </summary>
    static List<(uint id, uint ev, int price)> FindPayers(GameLink L)
    {
        var r = new List<(uint, uint, int)>();
        foreach (var o in L.SceneObjects())
        {
            uint ptr = L.ObjPtr(o.index);
            if (ptr == 0) continue;
            int n = L.Mem.Read(ptr + 5, 1)[0];
            uint lk = L.Mem.ReadU32(ptr + 8);
            if (n == 0 || n > 64 || lk < 0x80000000) continue;
            var d = L.Mem.Read(lk, n * 0x20);
            for (int i = 0; i < n; i++)
            {
                float amt = BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(i * 0x20 + 8));
                if (BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i * 0x20 + 2)) == EvtGiveShiny && amt < 0)
                {
                    r.Add((o.id, BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i * 0x20)), (int)-amt));
                    break;
                }
            }
        }
        return r;
    }

    /// <summary>Starts a payer's conversation and answers it; true once the patch reported a payment.</summary>
    static bool PayAt(Bot b, (uint id, uint ev, int price) p)
    {
        var L = b.L;
        if (L.ReadCounters()[0] < p.price + 100 && !EnterCheat(L, "YXXYYXXY")) return false;   // +1000 shinies
        for (int k = 0; k < 40 && L.ReadCounters()[0] < p.price + 100; k++)
        {
            if (k % 8 == 7 && !EnterCheat(L, "YXXYYXXY")) return false;
            Thread.Sleep(250);
        }
        WaitControl(b, TimeSpan.FromSeconds(10));
        Interlocked.Increment(ref payEpoch); Interlocked.Increment(ref paying);
        try { return PayTalk(b, p); }
        finally { Interlocked.Decrement(ref paying); Interlocked.Increment(ref payEpoch); Interlocked.Exchange(ref payQuietUntil, DateTime.UtcNow.AddSeconds(5).Ticks); }
    }

    /// <summary>Payments by any bot (bumped at start and end) and when their effect has surely arrived everywhere.</summary>
    static long payEpoch, payQuietUntil, paying;

    static bool PayTalk(Bot b, (uint id, uint ev, int price) p)
    {
        var L = b.L;
        uint pay0 = L.PaySeq, sh0 = L.ReadCounters()[0];
        // what starts it in the game (a condition behind the door's trigger) carries the text: fire that link
        var objs = L.SceneObjects();
        var starter = objs.SelectMany(o => L.Links(o.index).Where(l => l.dst == 335 && l.target == p.id).Select(l => (o.id, l.src))).FirstOrDefault();
        if (starter.id == 0)
        {
            // Mr. Krabs: a counter starts the payer's task box, which runs the conversation
            var payer = objs.FirstOrDefault(o => o.id == p.id);
            var tasks = L.Links(payer.index).Select(l => l.target).Where(t => objs.Any(o => o.id == t && o.type == 0x35)).ToHashSet();
            starter = objs.Where(o => o.id != p.id)
                .SelectMany(o => L.Links(o.index).Where(l => l.dst == 335 && tasks.Contains(l.target)).Select(l => (o.id, l.src))).FirstOrDefault();
        }
        if (starter.id == 0) { Log($"{b.Name}: nothing starts {p.id:X8}"); return false; }
        L.SendEvent(starter.id, starter.src);
        Thread.Sleep(1500);
        Log($"{b.Name}: {starter.id:X8} --{starter.src}--> talk {L.ActiveTalk():X8} control {(L.TryReadLocal(out var me0) ? me0.ControlOff : 0)}");
        Shot(b, "pay-start");
        // A through the text; the yes/no question starts on yes
        for (int k = 0; k < 30 && L.PaySeq == pay0; k++) { Tap(L, GcA, 120); Thread.Sleep(600); if (k == 3 || k == 8) Shot(b, $"pay-{k}"); }
        for (int k = 0; k < 15 && L.ActiveTalk() != 0; k++) { Tap(L, GcA, 120); Thread.Sleep(500); }
        WaitControl(b, TimeSpan.FromSeconds(10));
        bool paid = L.PaySeq != pay0;
        Log($"{b.Name}: {(paid ? "paid" : "no payment")} at {p.id:X8} (event {p.ev}, price {p.price}): shinies {sh0} -> {L.ReadCounters()[0]}");
        return paid;
    }

    static bool ChaosPay(Bot b, Random rng)
    {
        // shiny doors and gates only: a bot starting Mr. Krabs' talk directly skips his "already
        // bought" task state, so two bots would buy the same spatula (the `pay` phase covers him)
        var objs = b.L.SceneObjects();
        var payers = FindPayers(b.L).Where(p => !b.L.Links(objs.First(o => o.id == p.id).index).Any(l => l.dst == 35)).ToList();
        return payers.Count > 0 && PayAt(b, payers[rng.Next(payers.Count)]);
    }

    /// <summary>
    /// --phases tbox: A walks into a teleport box in JF01 and comes out of the other one; B, standing
    /// elsewhere in the same level, must not be moved or lose control (playtest: a teammate's box use
    /// took the other player along).
    /// </summary>
    static void TboxPhase(List<Bot> bots)
    {
        var A = bots[0]; var B = bots[1];
        uint scene = PlayerState.SceneIdOf("JF01");
        if (!GoTo(new List<Bot> { A, B }, scene)) return;
        var objs = A.L.SceneObjects();
        (float x, float y, float z) Pos(uint id)
        {
            var o = objs.FirstOrDefault(o => o.id == id);
            uint ptr = o.id == 0 ? 0 : A.L.ObjPtr(o.index);
            uint model = ptr == 0 ? 0 : A.L.Mem.ReadU32(ptr + 0x24);
            uint mat = model < 0x80000000 ? 0 : A.L.Mem.ReadU32(model + 0x4C);
            if (mat < 0x80000000) return (0, 0, 0);
            var p = A.L.Mem.Read(mat + 0x30, 12);
            return (BinaryPrimitives.ReadSingleBigEndian(p), BinaryPrimitives.ReadSingleBigEndian(p.AsSpan(4)), BinaryPrimitives.ReadSingleBigEndian(p.AsSpan(8)));
        }
        var b1 = Pos(0x9EF45781); var b2 = Pos(0x9EF45782);
        if (b1 == (0, 0, 0) || b2 == (0, 0, 0)) { Result("teleport box", false, "TBOX_TELEPORT_01/02 not found in JF01"); return; }
        static float D((float x, float y, float z) a, (float x, float y, float z) b) => MathF.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z));
        B.L.TryReadLocal(out var b0);
        var bStart = (b0.X, b0.Y, b0.Z);
        Log($"tbox: box1 {b1}, box2 {b2}, B at {bStart}");
        // a box works once its target is open; a box opens when a player comes within ~3.4 units:
        // visit the far one, then step next to and onto the near one
        A.L.Teleport(b2.x + 2.5f, b2.y + 1f, b2.z);
        Thread.Sleep(3000);
        WaitControl(A, TimeSpan.FromSeconds(10));
        A.L.Teleport(b1.x + 2.5f, b1.y + 1f, b1.z);
        Thread.Sleep(2500);
        A.L.Teleport(b1.x, b1.y + 1.5f, b1.z);
        float bMove = 0; uint bCtl = 0;
        bool arrived = WaitFor(() =>
        {
            if (B.L.TryReadLocal(out var bm)) { bMove = Math.Max(bMove, D((bm.X, bm.Y, bm.Z), bStart)); bCtl |= bm.ControlOff; }
            return A.L.TryReadLocal(out var am) && D((am.X, am.Y, am.Z), b2) < 5f;
        }, 20, 100);
        WaitFor(() => { if (B.L.TryReadLocal(out var bm)) { bMove = Math.Max(bMove, D((bm.X, bm.Y, bm.Z), bStart)); bCtl |= bm.ControlOff; } return false; }, 4, 100);
        A.L.TryReadLocal(out var a1);
        Result("teleport box: user arrives", arrived, $"A at ({a1.X:F1},{a1.Y:F1},{a1.Z:F1}), other box at ({b2.x:F1},{b2.y:F1},{b2.z:F1})");
        Result("teleport box: teammate stays", bMove < 1.5f && bCtl == 0, $"B moved at most {bMove:F2} units, control-off bits seen {bCtl:X}");
        WaitControl(A, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// --phases pay [--payscene SM01]: A pays a shiny door; both games lose the price once (shared
    /// inventory), B's game replays what the payment set off, and a late visitor gets it too.
    /// </summary>
    static void PayPhase(List<Bot> bots, string scenes)
    {
        foreach (var sc in scenes.Split(',', StringSplitOptions.RemoveEmptyEntries)) PayIn(bots, sc);
    }

    static void PayIn(List<Bot> bots, string sceneName)
    {
        var A = bots[0]; var B = bots[1];
        uint scene = PlayerState.SceneIdOf(sceneName);
        if (!GoTo(bots, scene)) return;
        var payers = FindPayers(A.L);
        Log($"pay: {payers.Count} payers in {sceneName}: {string.Join(", ", payers.Select(p => $"{p.id:X8}/{p.ev}/{p.price}"))}");
        if (payers.Count == 0) { Result($"pay in {sceneName}", false, "no paying object found"); return; }
        var p = payers.OrderBy(x => x.price).First();
        uint bTail0 = B.L.Mem.ReadU32(GameLink.MailboxAddr + Mb.OPayInTail);
        bool paid = PayAt(A, p);
        if (!paid) { Result($"pay in {sceneName}", false, $"A answered {p.id:X8} but no payment was reported"); return; }
        Thread.Sleep(1500);
        uint a1 = A.L.ReadCounters()[0];
        bool same = WaitFor(() => A.L.ReadCounters()[0] == B.L.ReadCounters()[0], 10, 250);
        bool replayed = WaitFor(() => B.L.Mem.ReadU32(GameLink.MailboxAddr + Mb.OPayInTail) != bTail0, 15, 250);
        Result($"pay in {sceneName}", same && replayed,
            $"A paid {p.price} at {p.id:X8}: shinies A {a1}, B {B.L.ReadCounters()[0]}; B's game replayed the payment: {replayed}");
        Thread.Sleep(3000);
        // what the payer bought (a pickup the payment hands out with Pickup 35: Mr. Krabs' golden
        // spatula) is the payer's own reward, shown only in their game
        var all = A.L.SceneObjects();
        var payerObj = all.FirstOrDefault(o => o.id == p.id);
        var bought = A.L.Links(payerObj.index).Where(l => l.dst == 35).Select(l => l.target).ToHashSet();
        Compare($"world after paying ({sceneName})", A, B, null, bought);
        // a teammate entering later replays it too (stored per scene on the server)
        uint park = PlayerState.SceneIdOf(sceneName == "HB01" ? "JF01" : "HB01");
        var others = bots.Where(x => x != A).ToList();
        if (!GoTo(others, park)) return;
        uint bTail1 = B.L.Mem.ReadU32(GameLink.MailboxAddr + Mb.OPayInTail);
        if (!GoTo(others, scene)) return;
        bool again = WaitFor(() => B.L.Mem.ReadU32(GameLink.MailboxAddr + Mb.OPayInTail) != bTail1, 15, 250);
        Result($"pay replayed on re-entry ({sceneName})", again, $"B came back to {sceneName}: payment replayed {again}, shinies A {A.L.ReadCounters()[0]} B {B.L.ReadCounters()[0]}");
    }

    /// <summary>Teammates in the same level must see the same collected/broken objects.</summary>
    static void ChaosCompareMates(Bot b, List<Bot> live, uint scene, Action<string> issue)
    {
        List<Bot> mates;
        lock (live) mates = live.Where(m => m != b && m.Hung == null && m.S.GameRunning && SceneOf(m) == scene && m.L.ObjPrimed && m.L.ObjScene == scene).ToList();
        if (mates.Count == 0 || !b.L.ObjPrimed) return;
        Thread.Sleep(3000);
        var objs = b.L.SceneObjects();
        var mine = b.L.ReadObjHashes(objs.Count);
        var pick = objs.Where(o => o.type == 4 || o.type == 0x1B).Select(o => o.index).ToList();
        foreach (var m in mates)
        {
            var theirs = m.L.ReadObjHashes(objs.Count);
            var diff = pick.Where(i => mine[i] != theirs[i]).ToList();
            if (diff.Count > 0)
                issue($"{SN(scene)}: {b.Name} and {m.Name} differ on {diff.Count} pickups/breakables (e.g. {string.Join(", ", diff.Take(4).Select(i => Nm(scene, objs[i].id)))})");
            else Log($"{b.Name}: {SN(scene)} matches {m.Name} ({pick.Count} pickups/breakables)");
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MemStatus { public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirt, AvailVirt, AvailExt; }
    [System.Runtime.InteropServices.DllImport("kernel32")] static extern bool GlobalMemoryStatusEx(ref MemStatus m);
    static ulong PcFreeMb() { var m = new MemStatus { Length = 64 }; return GlobalMemoryStatusEx(ref m) ? m.AvailPhys >> 20 : 0; }

    // the biggest levels (least scene heap left, session 4 notes) + boss arenas
    const string StormScenes = "JF02,RB01,BB02,GL01,SM04,KF04,GY03,DB02,BC02,B201,HB01,JF01";

    /// <summary>
    /// --phases loadstorm [--stormbots 6] [--stormrounds 8] [--stormscenes ...]: every game loads a
    /// level at the same moment, rounds alternating "all into one big level" and "each into a different
    /// one". Fails on a hang/crash (Dolphin's out-of-memory / NULL alloc ends that way) or when a game's
    /// scene heap / OS heap runs out; reports the lowest heap and PC RAM seen.
    /// </summary>
    static void LoadStormPhase(List<Bot> bots, Dictionary<string, string> a)
    {
        int want = Math.Clamp(int.Parse(a.GetValueOrDefault("stormbots", "6")), 2, 6);
        int rounds = int.Parse(a.GetValueOrDefault("stormrounds", "8"));
        var scenes = a.GetValueOrDefault("stormscenes", StormScenes).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(PlayerState.SceneIdOf).ToArray();
        string[] names = { "A", "B", "C", "D", "E", "F" };
        // more players, all starting together too
        var starting = new List<Bot>();
        for (int i = bots.Count; i < want; i++) { var nb = StartBot(names[i], ctx.Party, ctx.Port, ctx.Hub, ctx.Dolphin, ctx.Work); bots.Add(nb); starting.Add(nb); }
        if (starting.Count > 0 && !WaitAll(starting, ctx.Hub, TimeSpan.FromSeconds(180))) { Result("load storm: players up", false, Describe(starting)); return; }
        var group = bots.Take(want).ToList();
        ulong pcMin = PcFreeMb();
        uint gapMin = uint.MaxValue, osMin = uint.MaxValue, largestMin = uint.MaxValue;
        string gapAt = "", osAt = "";
        bool allHigh = true;
        uint shrinkMax = 0; string shrinkAt = "";
        var rng = new Random(7);
        for (int r = 0; r < rounds; r++)
        {
            if (group.Any(b => b.Hung != null || !b.S.GameRunning)) break;
            bool same = r % 2 == 0;
            var targets = group.Select((b, i) => same ? scenes[(r / 2) % scenes.Length] : scenes[(r * 3 + i) % scenes.Length]).ToArray();
            foreach (var b in group) WaitControl(b, TimeSpan.FromSeconds(10));
            // fire all warps in the same instant
            for (int i = 0; i < group.Count; i++) if (SceneOf(group[i]) != targets[i]) group[i].L.Warp(targets[i], anytime: true);
            var until = DateTime.UtcNow.AddSeconds(120);
            var arrived = new bool[group.Count];
            var firstGap = new uint[group.Count];
            while (DateTime.UtcNow < until && !arrived.All(x => x))
            {
                Thread.Sleep(300);
                pcMin = Math.Min(pcMin, PcFreeMb());
                for (int i = 0; i < group.Count; i++)
                {
                    var b = group[i];
                    if (b.Hung != null || !b.S.GameRunning) { arrived[i] = true; continue; }
                    try
                    {
                        if (!arrived[i] && WaitAll(new List<Bot> { b }, targets[i], TimeSpan.FromMilliseconds(1))) arrived[i] = true;
                        if (b.L.TryReadLocal(out var me) && me.InGame && me.SceneId == targets[i])
                        {
                            uint gap = b.L.HeapGap; var os = b.L.OsHeapFree();
                            allHigh &= b.L.HighRam;
                            if (firstGap[i] == 0) firstGap[i] = gap;
                            else if (firstGap[i] > gap && firstGap[i] - gap > shrinkMax) { shrinkMax = firstGap[i] - gap; shrinkAt = $"{b.Name} in {SN(targets[i])}"; }
                            if (gap < gapMin) { gapMin = gap; gapAt = $"{b.Name} in {SN(targets[i])}"; }
                            if (os.free < osMin) { osMin = os.free; osAt = $"{b.Name} in {SN(targets[i])}"; }
                            largestMin = Math.Min(largestMin, os.largest);
                        }
                    }
                    catch { }
                }
            }
            var bad = group.Where((b, i) => b.Hung != null || !b.S.GameRunning || SceneOf(b) != targets[i]).ToList();
            var rss = group.Select(b => { try { return System.Diagnostics.Process.GetProcessById(b.S.DolphinPid).WorkingSet64 >> 20; } catch { return 0L; } }).ToList();
            Result($"load storm round {r + 1} ({(same ? "all into " + SN(targets[0]) : "each into its own level")})", bad.Count == 0,
                (bad.Count == 0 ? "everyone loaded" : "FAILED: " + string.Join("; ", bad.Select(b => b.Hung ?? (b.S.GameRunning ? $"{b.Name} stuck in {SN(SceneOf(b))}" : $"{b.Name}: Dolphin closed")))) +
                $". Targets {string.Join(",", targets.Select(SN))}; per game: " + string.Join(", ", group.Select((b, i) => { try { return $"{b.Name} heap gap {b.L.HeapGap / 1024}K OS free {b.L.OsHeapFree().free / 1024}K puppets {b.L.CtxReady} RAM {rss[i]} MB"; } catch { return b.Name + " ?"; } }))
                + $"; PC free RAM {PcFreeMb()} MB");
            if (bad.Count > 0) foreach (var b in bad.Where(b => b.S.Link != null)) Log($"{b.Name}: " + SafeCpu(b));
            Thread.Sleep(2000);
        }
        // Low-RAM puppets need mb.heapReserve (24 KB) left; with high RAM they don't touch the scene heap
        // and the gap is the level's own (SM04 leaves 19 KB with 1 or 5 puppets, stock layout). Either
        // way the gap must not shrink while a level runs (gxHeap[0]/[1] would meet = session-4 crash).
        uint gapBar = allHigh ? 8 * 1024u : 24 * 1024u;
        Result("load storm: memory", gapMin >= gapBar && shrinkMax < 4096 && largestMin >= 64 * 1024 && pcMin >= 512,
            $"lowest scene heap gap {gapMin / 1024} KB ({gapAt}, bar {gapBar / 1024} KB{(allHigh ? ", puppets in high RAM" : "")}), most shrink during a visit {shrinkMax / 1024} KB {shrinkAt}, lowest OS heap free {osMin / 1024} KB ({osAt}), smallest largest OS block {largestMin / 1024} KB, lowest PC free RAM {pcMin} MB with {group.Count} games");
    }

    /// <summary>
    /// --phases abilities [--abilityscene JF01]: everyone in one level fires the same ability in the
    /// same instant (bubble bash, bubble bounce, bubble bowl, cruise bubble), then a mixed round.
    /// Every game simulates every teammate's move on its puppet, so these share per-player state the
    /// patch swaps (cruise bubble, bowl). Checks: no hang/crash, puppets follow their player while
    /// the moves run, nobody's own player is locked or camera taken over by a teammate's cruise
    /// bubble, everyone has control afterwards.
    /// </summary>
    static void AbilitiesPhase(List<Bot> bots, Dictionary<string, string> a)
    {
        uint sc = PlayerState.SceneIdOf(a.GetValueOrDefault("abilityscene", "JF01"));
        var group = bots.Where(b => b.Hung == null && b.S.GameRunning).Take(6).ToList();
        if (group.Count < 2 || !GoTo(group, sc)) { Result("abilities", false, "players not in " + SN(sc)); return; }
        // unlock bubble bowl + cruise bubble everywhere (player.g.PowerUp, as the cheats do)
        foreach (var b in group) b.L.Mem.TryWrite(0x803C0558 + 0x9BD, new byte[] { 1, 1 });
        // stand a few units apart on the start spot so puppets don't overlap
        group[0].L.TryReadLocal(out var start);
        for (int i = 0; i < group.Count; i++) group[i].L.Teleport(start.X + 3f * i, start.Y + 0.5f, start.Z);
        Thread.Sleep(2500);

        string[] moves = { "bash", "bounce", "bowl", "cruise", "mixed", "mixed" };
        foreach (var round in moves)
        {
            if (group.Any(b => b.Hung != null || !b.S.GameRunning)) { Result($"abilities: {round}", false, "a game is down: " + Describe(group)); return; }
            foreach (var b in group) WaitControl(b, TimeSpan.FromSeconds(10));
            var plan = group.Select((b, i) => round != "mixed" ? round : new[] { "bash", "bounce", "bowl", "cruise" }[(i + Environment.TickCount / 1000) % 4]).ToArray();
            var go = new Barrier(group.Count);
            double worst = 0, sum = 0; int n = 0;
            var samplerStop = false;
            var sampler = new Thread(() =>
            {
                while (!Volatile.Read(ref samplerStop))
                {
                    foreach (var viewer in group)
                        foreach (var other in group)
                        {
                            if (viewer == other) continue;
                            try
                            {
                                int slot = SlotOf(viewer, other.Name);
                                if (slot < 0 || !other.L.TryReadLocal(out var real)) continue;
                                var pp = viewer.L.PuppetPos(slot);
                                if (pp is not { } p) continue;
                                double d = Math.Sqrt(Sq(p.x - real.X) + Sq(p.y - real.Y) + Sq(p.z - real.Z));
                                lock (group) { worst = Math.Max(worst, d); sum += d; n++; }
                            }
                            catch { }
                        }
                    Thread.Sleep(100);
                }
            }) { IsBackground = true };
            sampler.Start();
            var threads = group.Select((b, i) => new Thread(() =>
            {
                go.SignalAndWait();
                var L = b.L;
                switch (plan[i])
                {
                    case "bash": Hold(L, GcY, 0, 0, 150); Hold(L, 0, 0, 0, 900); break;
                    case "bounce": Hold(L, GcA, 0, 0, 150); Hold(L, 0, 0, 0, 300); Hold(L, GcY, 0, 0, 150); Hold(L, 0, 0, 0, 1200); break;
                    case "bowl": Hold(L, GcB, 0, 0, 900); Hold(L, 0, 0, 0, 1500); break;   // (no stick: it walked C off a ledge)
                    case "cruise": Hold(L, GcL, 0, 0, 150); Hold(L, 0, 50, 80, 700); Hold(L, GcA, 50, 80, 1200); Hold(L, 0, -60, 0, 600); Hold(L, GcL, 0, 0, 150); Hold(L, 0, 0, 0, 1500); break;
                }
                L.PadOverride(false);
            })).ToList();
            var before = group.Select(b => b.L.TryReadLocal(out var m) ? m : default).ToList();
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join(TimeSpan.FromSeconds(20));
            Thread.Sleep(2000);
            Volatile.Write(ref samplerStop, true);
            sampler.Join(2000);
            var notes = new List<string>();
            bool ok = true;
            for (int i = 0; i < group.Count; i++)
            {
                var b = group[i];
                if (b.Hung != null || !b.S.GameRunning) { ok = false; notes.Add($"{b.Name} DOWN: {b.Hung ?? "Dolphin closed"}"); continue; }
                // everyone (also those who only watched teammates' cruise bubbles) gets control back
                bool ctl = WaitFor(() => b.L.TryReadLocal(out var m) && m.ControlOff == 0 && b.L.Mem.ReadU32(0x803CB8A8) != 7, 8, 200);
                if (!ctl)
                {
                    // a hint/prompt (out-of-bounds tutorial = controlOff 4) is answered like a player; anything else is a lock
                    b.L.TryReadLocal(out var m2);
                    bool answered = WaitControl(b, TimeSpan.FromSeconds(10));
                    if (!answered || (m2.ControlOff & ~4u) != 0) { ok = false; notes.Add($"{b.Name} still has no control after {plan[i]} (controlOff {m2.ControlOff:X})"); }
                    else notes.Add($"{b.Name} got a prompt after {plan[i]} (controlOff {m2.ControlOff:X}), answered");
                }
                // a player who didn't cruise must not have been carried away (a cruise bubble that isn't ours driving our player/camera)
                if (plan[i] != "cruise" && plan[i] != "bowl" && b.L.TryReadLocal(out var after))
                {
                    double moved = Math.Sqrt(Sq(after.X - before[i].X) + Sq(after.Z - before[i].Z));
                    if (moved > 8) { ok = false; notes.Add($"{b.Name} ({plan[i]}) moved {moved:F1} units"); }
                }
            }
            double avg = n > 0 ? sum / n : 0;
            // cruise/bowl move the player little; bash/bounce are short hops. 20 Hz updates + interpolation
            if (n == 0) { ok = false; notes.Add("no puppet positions read"); }
            else if (avg > 2.5 || worst > 8) { ok = false; notes.Add($"puppets lag their players: avg {avg:F2} max {worst:F2} units"); }
            Result($"abilities: {round} ({string.Join(",", group.Select((b, i) => b.Name + "=" + plan[i]))})", ok,
                $"{group.Count} players at once in {SN(sc)}; puppet error avg {avg:F2} / max {worst:F2} units over {n} samples" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : ""));
            Compare($"abilities: world after {round}", group[0], group[1], null);
        }
    }
}
