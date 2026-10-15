using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BfbbOnline.Server;
using BfbbOnline.Shared;

namespace BfbbOnline.Client;

/// <summary>
/// The extended self-test phases (--phases core,pickups,keys,enemies,cutscenes,talk,fourmaps):
/// every pickup kind, Deluxe keys and chests, enemies/breakables/bosses, shared story cutscenes
/// (also across worlds), conversations with the watch-along prompt, four players in four maps.
/// </summary>
static partial class SelfTest
{
    sealed record Ctx(ServerHost Server, string Party, int Port, string Dolphin, string Work, uint Scene, uint Hub);
    static Ctx ctx = null!;

    const uint EvtStartConversation = 335, EvtEndConversation = 336, EvtKill = 37, EvtDestroy = 22, EvtDisable = 2;
    const uint BtnStart = 0x1, BtnUp = 0x10, BtnDown = 0x40, BtnA = 0x10000;
    const uint OffHealth = 0x16B0, OffControlOff = 0x1788;

    static readonly string DefaultPickupScenes = "HB01,JF01,JF05,BB01,GL01,HB12,JF06,JFC1,BBC1,GL04,RB04,SM05";

    static uint Fnv1(uint word, uint bits)
    {
        uint h = 2166136261u ^ bits;
        h = (h ^ (word & ((1u << (int)bits) - 1))) * 16777619u;
        return h == 0 ? 1 : h;
    }
    static readonly uint AliveHash = Fnv1(1, 1), DeadHash = Fnv1(0, 1);

    static void RunPhases(HashSet<string> phases, List<Bot> bots, Dictionary<string, string> a)
    {
        void Phase(string name, Action run)
        {
            if (!phases.Contains(name)) return;
            if (AnyHung) { Result(name, false, "skipped: a game is hung"); return; }
            Log($"==== phase {name} ====");
            try { run(); }
            catch (Exception e) { Result(name, false, "crashed: " + e); }
        }
        var scenes = a.GetValueOrDefault("scenes", DefaultPickupScenes).Split(',', StringSplitOptions.RemoveEmptyEntries);
        Phase("pickups", () => PickupsPhase(bots, scenes));
        Phase("keys", () => KeysPhase(bots, a.GetValueOrDefault("keyscenes", "HB01,BB01").Split(',')));
        Phase("enemies", () => EnemiesPhase(bots));
        Phase("cutscenes", () => CutscenePhase(bots));
        Phase("talk", () => TalkPhase(bots));
        Phase("fourmaps", () => FourMapsPhase(bots, a.ContainsKey("sims")));
        Phase("npcmove", () => NpcMovePhase(bots, a.GetValueOrDefault("npcscenes", "JF01,BB01").Split(',')));
        Phase("bosses", () => BossSyncPhase(bots, a.GetValueOrDefault("bossscenes", "B101,B201,JF04,GY04,BC05").Split(',')));
        Phase("heapcmp", () => HeapCmp(bots, a.GetValueOrDefault("tpscenes", "B101,JF01").Split(',')));
        Phase("teleport", () => TeleportPhase(bots, a.GetValueOrDefault("tpscenes", "JF01,B101").Split(',')));
        Phase("racediag", () => RaceDiag(bots, a.GetValueOrDefault("raceobj", "GOLDENSPAT_PICKUP_02")));
    }

    /// <summary>
    /// Enemy movement: an enemy is driven by the game of the nearest player; everyone else's copy
    /// stands where it does in that game. A goes next to an enemy (B far away), then B to another.
    /// </summary>
    static void NpcMovePhase(List<Bot> bots, string[] scenes)
    {
        var A = bots[0]; var B = bots[1];
        foreach (var name in scenes)
        {
            uint sc = PlayerState.SceneIdOf(name);
            if (!GoTo(bots, sc)) continue;
            var objs = A.L.SceneObjects();
            var npcs = objs.Where(o => o.type == 0x2B && IsEnemy(A, o.index) && A.L.ObjField(o.index, 0) == o.id && (o.x != 0 || o.z != 0)).ToList();
            if (npcs.Count < 2) { Result($"enemy movement ({name})", false, $"only {npcs.Count} enemies found"); continue; }
            A.L.TryReadLocal(out var start);
            var notes = new List<string>();
            bool allOk = true;
            foreach (var (driver, follower, e) in new[] { (A, B, npcs[0]), (B, A, npcs[^1]) })
            {
                // the driver stands next to the enemy, the follower far away from it (at the start)
                var p0 = NpcPos(driver, e.index);
                driver.L.Teleport(p0.x + 3f, p0.y + 1f, p0.z + 3f);
                follower.L.Teleport(start.X, start.Y + 0.5f, start.Z);
                Thread.Sleep(4000);
                double worst = 0, sum = 0; int n = 0; double moved = 0;
                var first = NpcPos(driver, e.index);
                for (int k = 0; k < 20; k++)
                {
                    var pd = NpcPos(driver, e.index); var pf = NpcPos(follower, e.index);
                    double d = Math.Sqrt(Sq(pd.x - pf.x) + Sq(pd.y - pf.y) + Sq(pd.z - pf.z));
                    worst = Math.Max(worst, d); sum += d; n++;
                    moved = Math.Max(moved, Math.Sqrt(Sq(pd.x - first.x) + Sq(pd.z - first.z)));
                    Thread.Sleep(150);
                }
                bool ok = sum / n < 1.5 && worst < 4;
                allOk &= ok;
                notes.Add($"{Nm(sc, e.id)} driven by {driver.Name}: {follower.Name}'s copy off by avg {sum / n:0.00} / max {worst:0.00} units (enemy moved {moved:0.0}; poses applied {follower.L.NpcApplied})");
            }
            Result($"enemy movement ({name})", allOk, string.Join("; ", notes));
            foreach (var b in bots) { b.L.Teleport(start.X, start.Y + 0.5f, start.Z); }
            Thread.Sleep(1500);
        }
    }


    static bool IsEnemy(Bot b, int index)
    {
        uint t = b.L.Mem.ReadU32(b.L.ObjPtr(index) + 0xF4);
        if ((t >> 16) != 0x4E54) return false;
        char c = (char)((t >> 8) & 0xFF);
        return c == 'R' || c == 'T' || c == 'D';
    }

    static (float x, float y, float z) NpcPos(Bot b, int index)
    {
        uint fr = b.L.Mem.ReadU32(b.L.ObjPtr(index) + 0x48);
        if (fr < 0x80000000) return (0, 0, 0);
        var p = b.L.Mem.Read(fr + 0x30, 12);
        return (System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(p), System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(p.AsSpan(4)),
            System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(p.AsSpan(8)));
    }

    static readonly Dictionary<uint, (uint hp, uint round)> BossHp = new()
    {
        [0x4E544230] = (0x2D4, 0x2D8), [0x4E544231] = (0x2B8, 0x2BC), [0x4E544830] = (0x2C4, 0x2BC),
        [0x4E544831] = (0x2C4, 0x2C8), [0x4E544832] = (0x2B8, 0x2BC),
    };

    /// <summary>
    /// Bosses: one game drives the fight, the others show the same boss (pose, animation, AI goal,
    /// health). A hit in a following game moves the boss to that game and costs everyone's boss the
    /// same hit point.
    /// </summary>
    static void BossSyncPhase(List<Bot> bots, string[] scenes)
    {
        var A = bots[0]; var B = bots[1];
        foreach (var name in scenes)
        {
            uint sc = PlayerState.SceneIdOf(name);
            if (!GoTo(bots, sc)) continue;
            Thread.Sleep(3000);
            foreach (var bt in bots) WaitControl(bt, TimeSpan.FromSeconds(60));
            var boss = A.L.SceneObjects().Where(o => o.type == 0x2B && BossHp.ContainsKey(A.L.Mem.ReadU32(A.L.ObjPtr(o.index) + 0xF4))).ToList();
            if (boss.Count == 0) { Result($"boss sync ({name})", false, "no boss with known hit points in this scene"); continue; }
            var o = boss[0];
            uint ptr = A.L.ObjPtr(o.index);
            var (hpo, ro) = BossHp[A.L.Mem.ReadU32(ptr + 0xF4)];
            int Hp(Bot b) => (int)b.L.Mem.ReadU32(ptr + hpo);
            int Round(Bot b) => (int)b.L.Mem.ReadU32(ptr + ro);
            uint Anim(Bot b) { uint m = b.L.Mem.ReadU32(ptr + 0x24); uint pl = m >= 0x80000000 ? b.L.Mem.ReadU32(m + 0x0C) : 0; uint si = pl >= 0x80000000 ? b.L.Mem.ReadU32(pl + 8) : 0; return si >= 0x80000000 ? b.L.Mem.ReadU32(si + 4) : 0; }
            uint Goal(Bot b) { uint psy = b.L.Mem.ReadU32(ptr + 0x228); if (psy < 0x80000000) return 0; int top = (int)b.L.Mem.ReadU32(psy + 0x38); if (top < 0 || top > 4) return 0; uint g = b.L.Mem.ReadU32(psy + 0x10 + 4 * (uint)top); return g >= 0x80000000 ? b.L.Mem.ReadU32(g + 0x1C) : 0; }
            string G(uint g) => g == 0 ? "-" : System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(g).Reverse().ToArray());
            // 1. B's boss follows A's
            int samples = 0, posOk = 0, animOk = 0, goalOk = 0, hpOk = 0;
            double worst = 0;
            for (int k = 0; k < 30; k++)
            {
                var pa = NpcPos(A, o.index); var pb = NpcPos(B, o.index);
                double d = Math.Sqrt(Sq(pa.x - pb.x) + Sq(pa.y - pb.y) + Sq(pa.z - pb.z));
                worst = Math.Max(worst, d);
                samples++;
                if (d < 1.5) posOk++;
                if (Anim(A) == Anim(B)) animOk++;
                if (Goal(A) == Goal(B)) goalOk++;
                if (Hp(A) == Hp(B) && Round(A) == Round(B)) hpOk++;
                Thread.Sleep(200);
            }
            bool follow = posOk >= samples * 0.8 && animOk >= samples * 0.6 && goalOk >= samples * 0.6 && hpOk == samples;
            Result($"boss follows ({name})", follow, $"{Nm(sc, o.id)}: position within 1.5 in {posOk}/{samples} samples (worst {worst:0.0}), same animation {animOk}/{samples}, same goal {goalOk}/{samples} (A {G(Goal(A))}, B {G(Goal(B))}), same health {hpOk}/{samples} (hp {Hp(A)}, round {Round(A)})");
            // 2. a hit in B's game: B takes the boss over, everyone loses the same hit point
            int hp0 = Hp(A);
            if (hp0 <= 1) { Result($"boss hit ({name})", false, $"boss hit points {hp0}: nothing to take away"); continue; }
            B.L.Mem.WriteU32(ptr + hpo, (uint)(hp0 - 1));
            bool both = WaitFor(() => Hp(A) == hp0 - 1 && Hp(B) == hp0 - 1, 5);
            Thread.Sleep(2000);
            bool stays = Hp(A) == hp0 - 1 && Hp(B) == hp0 - 1;
            // 3. and one in A's game
            A.L.Mem.WriteU32(ptr + hpo, (uint)(hp0 - 2));
            bool both2 = hp0 - 2 < 0 || WaitFor(() => Hp(A) == hp0 - 2 && Hp(B) == hp0 - 2, 5);
            Thread.Sleep(2000);
            bool stays2 = hp0 - 2 < 0 || (Hp(A) == hp0 - 2 && Hp(B) == hp0 - 2);
            Result($"boss hit ({name})", both && stays && both2 && stays2,
                $"hit points {hp0}: B hit it -> A {(both ? "lost it too" : "DID NOT")}{(stays ? "" : " (then it CAME BACK)")}; A hit it -> B {(both2 ? "lost it too" : "DID NOT")}{(stays2 ? "" : " (then it CAME BACK)")}; now A {Hp(A)} B {Hp(B)} (round {Round(A)}/{Round(B)})");
        }
    }

    /// <summary>Diagnostics: are the scene objects at the same addresses in every game?</summary>
    static void HeapCmp(List<Bot> bots, string[] scenes)
    {
        foreach (var name in scenes)
        {
            uint sc = PlayerState.SceneIdOf(name);
            if (!GoTo(bots, sc)) continue;
            var A = bots[0]; var B = bots[1];
            int n = A.L.SceneObjects().Count, same = 0, diff = 0;
            var ex = new List<string>();
            for (int i = 0; i < n; i++)
            {
                uint pa = A.L.ObjPtr(i), pb = B.L.ObjPtr(i);
                if (pa == pb) same++; else { diff++; if (ex.Count < 8) ex.Add($"#{i} {pa:X8}/{pb:X8} type {A.L.Mem.Read(pa + 4, 1)[0]:X2}"); }
            }
            var bosses = A.L.SceneObjects().Where(o => o.type == 0x2B && (A.L.Mem.ReadU32(A.L.ObjPtr(o.index) + 0xF4) >> 8 & 0xFFFF) is 0x5442 or 0x5448)
                .Select(o => $"{Nm(sc, o.id)} #{o.index} @{A.L.ObjPtr(o.index):X8} type {A.L.Mem.ReadU32(A.L.ObjPtr(o.index) + 0xF4):X8} vt {A.L.Mem.ReadU32(A.L.ObjPtr(o.index) + 0x1B8):X8}");
            Result($"heap layout ({name})", diff == 0, $"{same} objects at the same address, {diff} differ: {string.Join(", ", ex)}; bosses: {string.Join(", ", bosses)}");
        }
    }

    /// <summary>"Teleport to teammate": B travels to A in another scene (incl. a boss arena) and in the same scene.</summary>
    static void TeleportPhase(List<Bot> bots, string[] scenes)
    {
        var A = bots[0]; var B = bots[1];
        foreach (var name in scenes)
        {
            uint sc = PlayerState.SceneIdOf(name);
            if (!GoTo(new List<Bot> { A }, sc)) continue;
            // A walks somewhere away from the start point
            A.L.TryReadLocal(out var a0);
            uint away = PlayerState.SceneIdOf(name == "HB01" ? "JF01" : "HB01");
            if (SceneOf(B) == sc) { B.L.Warp(away); WaitAll(new List<Bot> { B }, away, TimeSpan.FromSeconds(90)); WaitControl(B, TimeSpan.FromSeconds(30)); }
            Thread.Sleep(1500);
            A.L.TryReadLocal(out var a);
            string msg = B.S.TeleportTo("A");
            bool arrived = WaitFor(() => SceneOf(B) == sc, 120)
                && WaitFor(() => B.L.TryReadLocal(out var b) && A.L.TryReadLocal(out a) && b.ControlOff == 0 && Dist(b, a) < 4f, 90);
            B.L.TryReadLocal(out var bEnd); A.L.TryReadLocal(out a);
            Result($"teleport to teammate ({name})", arrived, $"{msg}; B ended in {SN(bEnd.SceneId)} {Dist(bEnd, a):0.0} units from A");
        }
        // same scene: A goes somewhere else (falls and respawns if that's over a pit), B follows
        uint cur = SceneOf(A);
        if (SceneOf(B) == cur && A.L.TryReadLocal(out var s0))
        {
            A.L.Teleport(s0.X + 15, s0.Y + 1, s0.Z + 15);
            Thread.Sleep(3000);
            WaitControl(A, TimeSpan.FromSeconds(20));
            Thread.Sleep(1000);
            string msg = B.S.TeleportTo("A");
            bool ok = WaitFor(() => B.L.TryReadLocal(out var b) && A.L.TryReadLocal(out var a1) && Dist(b, a1) < 3f, 20);
            B.L.TryReadLocal(out var b1); A.L.TryReadLocal(out var s1);
            Result("teleport to teammate (same scene)", ok, $"{msg}; {Dist(b1, s1):0.0} units from A");
            // name tag: A stands a few units in front of B's camera; screenshot of B's game
            Thread.Sleep(3000);
            var cam = B.L.Mem.Read(0x803C0558 + 0x14, 0x40);
            float F(int o) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(cam.AsSpan(o));
            float cx = F(0x30), cy = F(0x34), cz = F(0x38), ax = F(0x20), ay = F(0x24), az = F(0x28);
            float hl = MathF.Sqrt(ax * ax + az * az);
            B.L.TryReadLocal(out var bNow);
            // on B's level, ~8 units ahead of the camera along its horizontal view direction
            A.L.Teleport(cx + ax / hl * 9, bNow.Y + 0.3f, cz + az / hl * 9);
            Thread.Sleep(2500);
            Shot(B, "nametag");
            A.L.TryReadLocal(out var aNow);
            Log($"name tag: cam {cx:0.0},{cy:0.0},{cz:0.0} at {ax:0.00},{ay:0.00},{az:0.00}; A {aNow.X:0.0},{aNow.Y:0.0},{aNow.Z:0.0}; B {bNow.X:0.0},{bNow.Y:0.0},{bNow.Z:0.0}");
            // what B's game should draw: each puppet's feet and feet + 1.3, projected like the patch does
            var cm = B.L.Mem.Read(0x803C0558 + 0x14, 0x40);
            float M(int k) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(cm.AsSpan(4 * k));
            for (int slot = 0; slot < Mb.MaxRemote; slot++)
            {
                if (!B.L.RemoteActive(slot) || B.L.PuppetPos(slot) is not { } pp) continue;
                foreach (var dy in new[] { 0f, 1.3f })
                {
                    float dx0 = pp.x - M(12), dy0 = pp.y + dy - M(13), dz0 = pp.z - M(14);
                    float z = dx0 * M(8) + dy0 * M(9) + dz0 * M(10), x = dx0 * M(0) + dy0 * M(1) + dz0 * M(2), y = dx0 * M(4) + dy0 * M(5) + dz0 * M(6);
                    float t = MathF.Tan(75f * MathF.PI / 360f);
                    Log($"  slot {slot} {B.L.RemoteName(slot)} at {pp.x:0.0},{pp.y:0.0},{pp.z:0.0} +{dy}: cam {x:0.00},{y:0.00},{z:0.00} -> screen {0.5f - 0.5f * x / (z * t):0.000},{0.5f - 0.5f * y / (z * t * 0.75f):0.000}");
                }
            }
        }
    }

    static float Dist(in PlayerState p, in PlayerState q)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Diagnostics: A collects a race reward; logs the timers that run in B's game afterwards.</summary>
    static void RaceDiag(List<Bot> bots, string reward)
    {
        var A = bots[0]; var B = bots[1];
        uint sc = SceneOf(A);
        var names = Names(sc);
        var objs = A.L.SceneObjects();
        var o = objs.First(x => names.TryGetValue(x.id, out var n) && n == reward);
        var timers = objs.Where(x => x.type == 0x0E).ToList();
        string Running(Bot b) => string.Join(" ", timers.Select(t => (t, w: b.L.ObjField(t.index, 0x14), f: b.L.ObjField(t.index, 0x18)))
            .Where(q => (q.w >> 24) != 0).Select(q => $"{Nm(sc, q.t.id)}:{q.w >> 24}/{BitConverter.Int32BitsToSingle((int)q.f):0.0}"));
        Log($"racediag before: A {Running(A)} | B {Running(B)}");
        A.L.Teleport(o.x, o.y + 0.3f, o.z);
        for (int k = 0; k < 30 && SceneOf(B) == sc; k++)
        {
            Thread.Sleep(500);
            Log($"racediag {k / 2.0:0.0}s: B ctl {B.L.Global(OffControlOff):X} running {Running(B)}");
        }
        Result("racediag", true, $"B scene {SN(SceneOf(B))}");
    }

    // ---- helpers ---------------------------------------------------------------------------
    static string SN(uint s) => PlayerState.SceneName(s);

    static uint SceneOf(Bot b) => b.L.TryReadLocal(out var me) ? me.SceneId : 0;

    /// <summary>Warps the bots to a scene and waits until each has control.</summary>
    static bool GoTo(List<Bot> group, uint scene)
    {
        foreach (var b in group)
            if (SceneOf(b) != scene) b.L.Warp(scene);
        bool ok = WaitAll(group, scene, TimeSpan.FromSeconds(120));
        if (!ok) { Result($"warp to {SN(scene)}", false, Describe(group)); return false; }
        Thread.Sleep(2000);
        foreach (var b in group) WaitControl(b, TimeSpan.FromSeconds(60));
        Thread.Sleep(1500);
        return true;
    }

    static uint Hash(Bot b, int index) => b.L.ReadObjHashes(index + 1)[index];

    static bool WaitFor(Func<bool> cond, double seconds, int stepMs = 100)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (AnyHung) return false;
            if (cond()) return true;
            Thread.Sleep(stepMs);
        }
        return cond();
    }

    static int IndexOf(Bot b, uint id)
    {
        foreach (var o in b.L.SceneObjects()) if (o.id == id) return o.index;
        return -1;
    }

    static string Nm(uint scene, uint id) => Names(scene).TryGetValue(id, out var n) ? n : id.ToString("X8");

    static SimInstance? SimOf(uint scene)
    {
        var p = ctx.Server.FindParty(ctx.Party);
        var s = p == null ? null : ctx.Server.Sims.Get(p, scene);
        return s is { Status: "running" } && s.Link != null ? s : null;
    }

    /// <summary>Presses buttons for a moment (one pad override pulse).</summary>
    static void Tap(Bot b, uint buttons)
    {
        b.L.PadOverride(true, buttons);
        Thread.Sleep(120);
        b.L.PadOverride(true, 0);
        Thread.Sleep(120);
        b.L.PadOverride(false);
    }

    // ---- 1. every pickup kind -------------------------------------------------------------------
    static void PickupsPhase(List<Bot> bots, string[] scenes)
    {
        var A = bots[0];
        var others = bots.Skip(1).ToList();
        var kinds = new SortedDictionary<string, List<string>>();
        foreach (var name in scenes)
        {
            uint sc = PlayerState.SceneIdOf(name);
            if (!GoTo(bots, sc)) continue;
            var objs = A.L.SceneObjects();
            var ha = A.L.ReadObjHashes(objs.Count);
            var picks = objs.Where(o => o.type == 4).Select(o => (o, info: A.L.PickupInfo(o.index))).ToList();
            foreach (var g in picks.GroupBy(p => (p.info.type, p.info.index)).OrderBy(g => g.Key))
            {
                string kind = PickupName(g.Key.type, g.Key.index);
                if (!kinds.TryGetValue(kind, out var notes)) kinds[kind] = notes = new();
                var shared = g.Where(p => ha[p.o.index] != 0).ToList();
                if (shared.Count == 0) { notes.Add($"{name}: x{g.Count()} personal (respawning, not shared)"); continue; }
                A.L.TryReadLocal(out var me);
                var cands = shared.Where(p => (p.info.flags & 2) != 0 && (p.info.state & 0x0C) == 0 && (p.info.state & 0x40) != 0 && (p.o.x != 0 || p.o.z != 0))
                    .OrderBy(p => Sq(p.o.x - me.X) + Sq(p.o.y - me.Y) + Sq(p.o.z - me.Z)).Take(4).ToList();
                if (cands.Count == 0) { notes.Add($"{name}: x{g.Count()} shared, none collectable by touch here"); continue; }
                (bool ok, string detail)? r = null;
                foreach (var c in cands)
                {
                    r = CollectOne(A, others, c.o, kind, g.Key.type, g.Key.index);
                    if (r != null) break;
                }
                if (r == null) { notes.Add($"{name}: touching {cands.Count} didn't collect one"); continue; }
                Result($"pickup {kind} ({name})", r.Value.ok, r.Value.detail);
                notes.Add($"{name}: {(r.Value.ok ? "synced" : "FAILED")}");
            }
            Compare($"consistency after pickups ({name})", A, bots[1], null);
            if (SimOf(sc) != null) CompareSim(ctx.Server, ctx.Party, sc, A);
        }
        Result("pickup kinds", true, string.Join("; ", kinds.Select(k => $"{k.Key}: {string.Join(", ", k.Value)}")));
    }

    /// <summary>
    /// Collects one pickup in A by standing on it. Null if touching it didn't collect it.
    /// Checks every teammate gets the same object state and the same counter changes, once.
    /// </summary>
    static (bool ok, string detail)? CollectOne(Bot A, List<Bot> others, (int index, uint id, byte type, float x, float y, float z) o,
        string kind, byte ptype, byte pindex)
    {
        uint before = Hash(A, o.index);
        A.L.TryReadLocal(out var home);   // solid ground: A goes back here afterwards
        uint reset0 = A.L.ResetSeq;
        var all = new List<Bot> { A };
        all.AddRange(others);
        var c0 = all.Select(b => b.L.ReadCounters()).ToList();
        bool got = false;
        foreach (var dy in new[] { 0.3f, 1.0f, -0.4f })
        {
            // a hint (out of bounds tutorial...) takes control away and pickups ignore the player
            if (A.L.TryReadLocal(out var cs) && cs.ControlOff != 0) WaitControl(A, TimeSpan.FromSeconds(20));
            A.L.Teleport(o.x, o.y + dy, o.z);
            if (WaitFor(() => (A.L.PickupInfo(o.index).state & 0x0C) != 0 || Hash(A, o.index) != before, 3)) { got = true; break; }
        }
        if (!got) { A.L.Teleport(home.X, home.Y + 0.2f, home.Z); Thread.Sleep(500); return null; }
        // the pickup settles (flies to the player) before its state is final
        WaitFor(() => (A.L.PickupInfo(o.index).state & 0x04) == 0 && Hash(A, o.index) != before, 5);
        uint after = Hash(A, o.index);
        // a spatula plays the reward animation for the collector only: teammates keep control
        uint othersCtl = 0;
        bool synced = WaitFor(() =>
        {
            foreach (var b in others) othersCtl |= b.L.Global(OffControlOff);
            return others.All(b => Hash(b, o.index) == after);
        }, 8);
        // a pickup that ends the level (a race's golden spatula) takes the whole party on to the next
        // scene, like a story cutscene: then the objects can't be compared, the counters can
        uint sc0 = SceneOf(A);
        bool levelDone = ptype == 1 && pindex == 0 && WaitFor(() => SceneOf(A) != sc0, 15);
        // a race's reward is per player (user decision 2026-10-14): the winner moves on, teammates keep
        // racing in their own game, the spatula counts once for everyone
        bool raceOk = true;
        string raceNote = "";
        if (levelDone)
        {
            synced = true; othersCtl = 0;
            Thread.Sleep(15000);
            var gone = others.Where(b => SceneOf(b) != sc0).ToList();
            raceOk = gone.Count == 0;
            raceNote = raceOk ? " (level complete for A only: teammates stayed in the race)" : $" (TEAMMATES WERE TAKEN ALONG: {string.Join(",", gone.Select(b => b.Name))})";
            if (raceOk && others.Count > 0)
            {
                // a teammate can still grab their own copy of the reward; it doesn't count twice
                var B = others[0];
                uint sp0 = B.L.ReadCounters()[1];
                bool bGot = false;
                foreach (var dy in new[] { 0.3f, 1.0f, -0.4f })
                {
                    B.L.Teleport(o.x, o.y + dy, o.z);
                    if (WaitFor(() => (B.L.PickupInfo(o.index).state & 0x0C) != 0, 3)) { bGot = true; break; }
                }
                WaitFor(() => SceneOf(B) != sc0, 20);
                Thread.Sleep(6000);
                uint sp1 = B.L.ReadCounters()[1];
                raceOk = bGot && sp1 == sp0;
                raceNote += bGot ? $"; B collected its own copy too: spatulas {sp0} -> {sp1}{(sp1 == sp0 ? " (counted once)" : " COUNTED TWICE")}" : "; B could NOT collect its copy";
            }
            WaitAll(all, SceneOf(A), TimeSpan.FromSeconds(1));
        }
        bool aBack = WaitControl(A, TimeSpan.FromSeconds(40));
        bool fell = A.L.ResetSeq != reset0;   // the pickup floats over a pit: A fell / was taken out of bounds
        if (!fell && !levelDone) { A.L.Teleport(home.X, home.Y + 0.2f, home.Z); Thread.Sleep(700); }
        Thread.Sleep(3000);
        var c1 = all.Select(b => b.L.ReadCounters()).ToList();
        Thread.Sleep(2500);
        var c2 = all.Select(b => b.L.ReadCounters()).ToList();
        int[] D(int k, List<uint[]> x) => x[k].Select((v, i) => (int)v - (int)c0[k][i]).ToArray();
        var dA = D(0, c1);
        string Fmt(int[] d) { var nz = d.Select((v, i) => (v, i)).Where(t => t.v != 0).Select(t => $"{CounterName(t.i)}{t.v:+#;-#}"); var s = string.Join(" ", nz); return s.Length == 0 ? "nothing" : s; }
        bool sameDeltas = others.Select((b, k) => D(k + 1, c1)).All(d => d.SequenceEqual(dA));
        // still changing is fine while every game changes the same way (A standing in a shiny stream);
        // a double count shows as games disagreeing
        int[] Moved(int k) => c2[k].Select((v, i) => (int)v - (int)c1[k][i]).ToArray();
        bool stable = all.Select((b, k) => Moved(k)).All(m => m.SequenceEqual(Moved(0)));
        // what one of this pickup is worth: a double count shows up here
        string? expect = null;
        string note = "";
        bool once = true;
        if (ptype == 1 && pindex == 0) { once = dA[1] == 1; expect = "spatulas +1"; }
        else if (ptype == 1 && pindex == 6) { once = dA[2] == 1; expect = "socks +1"; }
        else if (ptype == 1 && pindex == 2) { once = dA[33] == 1 || c0[0][33] >= 6; expect = "max health +1"; }
        else if (ptype == 0) { once = dA[0] > 0; expect = "shinies up"; }
        // the reward animation must play for the collector only (control owner 0x1000); level logic
        // reacting to the change (GLOBAL 1) or a teammate's own out-of-bounds (4) aren't ours
        bool ok = synced && sameDeltas && stable && once && (othersCtl & 0x1000) == 0 && aBack && raceOk;
        if (fell) note = " (A fell out of bounds after grabbing it: the scene reset in A's game)";
        if (levelDone) note += raceNote;
        var detail = $"#{o.index} {o.id:X8}{note}: A got {Fmt(dA)}" + (expect != null ? $" (expected {expect})" : "")
            + "; " + string.Join("; ", others.Select((b, k) => $"{b.Name} {(levelDone ? "raced on" : Hash(b, o.index) == after ? "same state" : "STATE DIFFERS")}, got {Fmt(D(k + 1, c1))}"))
            + (stable ? "" : "; counters changed differently between the games afterwards (double count?)")
            + (othersCtl != 0 ? $"; a teammate's controls were off (controlOff {othersCtl:X}{((othersCtl & 0x1000) != 0 ? ": reward animation!" : ": level logic / own")})" : "")
            + (aBack ? "" : "; A never got control back");
        return (ok, detail);
    }

    static string CounterName(int i) => i switch
    {
        0 => "shinies", 1 => "spatulas", 2 => "socks", 33 => "maxHealth",
        _ => (i - 3) % 2 == 0 ? $"socks[{(i - 3) / 2}]" : $"levelPickups[{(i - 3) / 2}]",
    };

    // ---- 1b. Deluxe keys and treasure chests ----------------------------------------------------
    static void KeysPhase(List<Bot> bots, string[] scenes)
    {
        var A = bots[0];
        var others = bots.Skip(1).ToList();
        foreach (var name in scenes)
        {
            uint sc = PlayerState.SceneIdOf(name);
            if (!GoTo(bots, sc)) continue;
            var objs = A.L.SceneObjects();
            var keys = objs.Where(o => o.type == 4 && A.L.PickupInfo(o.index) is { type: 1, index: 13 }).ToList();
            if (keys.Count == 0) { Result($"keys ({name})", false, "no keys in this scene"); continue; }
            // the key count: what a key's pickup event (35) decrements
            uint countId = A.L.Links(keys[0].index).Where(l => l.src == 35 && l.dst == 12).Select(l => l.target).FirstOrDefault();
            int ci = countId == 0 ? -1 : IndexOf(A, countId);
            short Count(Bot b) => ci < 0 ? (short)-1 : (short)(b.L.ObjField(ci, 0x14) >> 16);
            var ha = A.L.ReadObjHashes(objs.Count);
            Log($"{name}: {keys.Count} keys ({keys.Count(k => ha[k.index] != 0)} tracked), key count {Nm(sc, countId)} = {Count(A)}");
            int n = 0, okN = 0;
            var notes = new List<string>();
            foreach (var k in keys)
            {
                var info = A.L.PickupInfo(k.index);
                if ((info.state & 0x0C) != 0 || (info.state & 0x40) == 0) { notes.Add($"{Nm(sc, k.id)} not collectable (state {info.state:X})"); continue; }
                short before = Count(A);
                uint h0 = Hash(A, k.index);
                bool got = false;
                foreach (var dy in new[] { 0.3f, 1.0f, -0.4f })
                {
                    A.L.Teleport(k.x, k.y + dy, k.z);
                    if (WaitFor(() => (A.L.PickupInfo(k.index).state & 0x0C) != 0, 3)) { got = true; break; }
                }
                if (!got) { notes.Add($"{Nm(sc, k.id)} not collected by touching it"); continue; }
                n++;
                WaitFor(() => (A.L.PickupInfo(k.index).state & 0x04) == 0, 4);
                short ca = 0;
                bool same = WaitFor(() =>
                {
                    ca = Count(A);
                    return others.All(b => (b.L.PickupInfo(k.index).state & 0x0C) != 0 && Count(b) == ca);
                }, 8);
                WaitControl(A, TimeSpan.FromSeconds(20));
                if (same) okN++;
                notes.Add($"{Nm(sc, k.id)}: count {before}->{ca}" + string.Join("", others.Select(b => $", {b.Name} {Count(b)}{((b.L.PickupInfo(k.index).state & 0x0C) != 0 ? "" : " (key still there)")}")));
            }
            Result($"keys ({name})", n > 0 && okN == n, $"{okN}/{n} keys synced (key gone and key count equal for everyone). " + string.Join("; ", notes));
            // all keys found: the chest opens for everyone (each game counts down to 0 itself)
            Thread.Sleep(4000);
            foreach (var b in bots) WaitControl(b, TimeSpan.FromSeconds(30));
            var counts = bots.Select(b => $"{b.Name} {Count(b)}");
            var doneIdx = objs.Where(o => Nm(sc, o.id) == "KEY_DONE_COUNTER").Select(o => o.index).FirstOrDefault(-1);
            var chestIdx = objs.Where(o => Nm(sc, o.id) == "TREASURE_CHEST").Select(o => o.index).FirstOrDefault(-1);
            string Done(Bot b) => doneIdx < 0 ? "?" : ((short)(b.L.ObjField(doneIdx, 0x14) >> 16)).ToString();
            string Chest(Bot b) => chestIdx < 0 ? "?" : $"{b.L.ObjField(chestIdx, 0x18) >> 24:X}";
            bool allDone = bots.All(b => Count(b) == Count(A)) && bots.All(b => Done(b) == Done(A));
            Result($"treasure chest ({name})", allDone && Count(A) <= 0,
                $"key counts {string.Join(", ", counts)}; KEY_DONE_COUNTER {string.Join(", ", bots.Select(b => $"{b.Name} {Done(b)}"))}; chest flags {string.Join(", ", bots.Select(b => $"{b.Name} {Chest(b)}"))}");
            // the chest's reward: a spatula, shared like any other
            objs = A.L.SceneObjects();
            ha = A.L.ReadObjHashes(objs.Count);
            var reward = objs.Where(o => o.type == 4 && Nm(sc, o.id).StartsWith("TREASURE_SPATULA")).ToList();
            foreach (var r in reward)
            {
                var info = A.L.PickupInfo(r.index);
                var res = (info.state & 0x0C) == 0 ? CollectOne(A, others, r, "chest spatula", info.type, info.index) : null;
                Result($"chest spatula ({name})", res?.ok ?? false, res?.detail ?? $"{Nm(sc, r.id)} couldn't be collected (state {info.state:X}, tracked {ha[r.index] != 0})");
            }
            Compare($"consistency after keys ({name})", A, bots[1], null);
        }
    }

    // ---- 4. enemies, breakables, generators, bosses -----------------------------------------------
    static void EnemiesPhase(List<Bot> bots)
    {
        var A = bots[0];
        var B = bots[1];
        var others = bots.Skip(1).ToList();
        uint sc = PlayerState.SceneIdOf("JF01");
        if (!GoTo(bots, sc)) return;
        var objs = A.L.SceneObjects();
        A.L.TryReadLocal(out var me);
        var hs = bots.Select(b => b.L.ReadObjHashes(objs.Count)).ToList();
        var npcs = objs.Where(o => o.type == 0x2B && hs.All(h => h[o.index] == AliveHash))
            .OrderBy(o => Sq(o.x - me.X) + Sq(o.z - me.Z)).ToList();
        Log($"JF01: {objs.Count(o => o.type == 0x2B)} NPCs, {npcs.Count} alive in every game");
        if (npcs.Count == 0) { Result("enemy kill", false, "no enemy alive in every game"); return; }
        // villagers can't die: kill the nearest ones that do
        var victims = new List<(int index, uint id, byte type, float x, float y, float z)>();
        foreach (var v in npcs.Take(25))
        {
            A.L.SendEvent(v.id, EvtKill);
            if (WaitFor(() => Hash(A, v.index) == DeadHash, 3)) victims.Add(v);
            if (victims.Count == 3) break;
        }
        bool deadA = victims.Count > 0;
        bool deadAll = WaitFor(() => others.All(b => victims.All(v => Hash(b, v.index) == DeadHash)), 8);
        var sim = SimOf(sc);
        bool deadSim = sim == null || WaitFor(() => victims.All(v => sim.Link!.ReadObjHashes(v.index + 1)[v.index] == DeadHash), 8);
        Result("enemy kill", deadA && deadAll && deadSim, $"killed {string.Join(" ", victims.Select(v => Nm(sc, v.id)))} in A: dead in A {deadA}, in "
            + string.Join(", ", others.Select(b => $"{b.Name} {victims.Count(v => Hash(b, v.index) == DeadHash)}/{victims.Count}"))
            + (sim == null ? " (no sim)" : $", sim {deadSim}"));

        // B dies: the scene resets in B's game, the enemies must stay dead
        uint reset0 = B.L.ResetSeq;
        B.L.SetGlobal(OffHealth, 0);
        // dying can wait on a prompt (a hint that was up): answer it like a player
        bool reset = WaitFor(() => { if (B.L.ResetSeq != reset0) return true; if (B.L.TryReadLocal(out var bm) && bm.ControlOff != 0) Tap(B, PromptAnswer(B.L)); return false; }, 30, 500);
        WaitAll(new List<Bot> { B }, sc, TimeSpan.FromSeconds(30));
        WaitControl(B, TimeSpan.FromSeconds(30));
        bool stay = WaitFor(() => victims.All(v => Hash(B, v.index) == DeadHash), 12);
        Result("enemies stay dead after B dies", reset && stay, $"B's scene reset {(reset ? "happened" : "did NOT happen")}; dead in B {victims.Count(v => Hash(B, v.index) == DeadHash)}/{victims.Count}");

        // a breakable
        objs = A.L.SceneObjects();
        hs = bots.Select(b => b.L.ReadObjHashes(objs.Count)).ToList();
        // standing (state != 2 destroyed), and not a bowling pin (those stand back up by themselves)
        var brk = objs.Where(o => o.type == 0x1B && hs[0][o.index] != 0 && hs.All(h => h[o.index] == hs[0][o.index]) && A.L.ObjField(o.index, 0xD8) != 2)
            .OrderBy(o => Nm(sc, o.id).Contains("BOWLING") ? 1 : 0).ToList();
        if (brk.Count == 0) Result("breakable", false, "no tracked breakable in JF01");
        else
        {
            var d = brk[0];
            uint h0 = Hash(A, d.index);
            A.L.SendEvent(d.id, EvtDestroy);
            bool chA = WaitFor(() => Hash(A, d.index) != h0, 6);
            uint h1 = Hash(A, d.index);
            bool same = WaitFor(() => others.All(b => Hash(b, d.index) == h1), 8);
            Result("breakable", chA && same, $"{Nm(sc, d.id)} destroyed in A ({(chA ? "state changed" : "state did NOT change")}); " + string.Join(", ", others.Select(b => $"{b.Name} {(Hash(b, d.index) == h1 ? "same" : "differs")}")));
        }

        // leave and come back: still dead
        if (GoTo(bots, ctx.Hub) && GoTo(bots, sc))
        {
            bool still = WaitFor(() => bots.All(b => victims.All(v => Hash(b, v.index) == DeadHash)), 12);
            Result("enemies stay dead after re-entering", still, string.Join(", ", bots.Select(b => $"{b.Name} {victims.Count(v => Hash(b, v.index) == DeadHash)}/{victims.Count} dead")));
        }

        // an enemy generator (HB14 has some)
        uint gsc = PlayerState.SceneIdOf("HB14");
        if (GoTo(bots, gsc))
        {
            objs = A.L.SceneObjects();
            hs = bots.Select(b => b.L.ReadObjHashes(objs.Count)).ToList();
            var gen = objs.Where(o => o.type == 0x29 && hs[0][o.index] != 0).ToList();
            if (gen.Count == 0) Result("enemy generator", false, $"no tracked generator in HB14 ({objs.Count(o => o.type == 0x29)} untracked)");
            else
            {
                var g = gen[0];
                uint h0 = Hash(A, g.index);
                A.L.SendEvent(g.id, EvtDisable);
                bool chA = WaitFor(() => Hash(A, g.index) != h0, 6);
                uint h1 = Hash(A, g.index);
                bool same = WaitFor(() => others.All(b => Hash(b, g.index) == h1), 8);
                Result("enemy generator", chA && same, $"{Nm(gsc, g.id)} switched off in A ({(chA ? "state changed" : "state did NOT change")}); " + string.Join(", ", others.Select(b => $"{b.Name} {(Hash(b, g.index) == h1 ? "same" : "differs")}")));
            }
        }

        // a boss: beating it counts for the party (B101, Robo-Sandy)
        uint bsc = PlayerState.SceneIdOf("B101");
        if (GoTo(new List<Bot> { A, B }, bsc))
        {
            objs = A.L.SceneObjects();
            var ha = A.L.ReadObjHashes(objs.Count);
            var hb = B.L.ReadObjHashes(objs.Count);
            var alive = objs.Where(o => o.type == 0x2B && ha[o.index] == AliveHash && hb[o.index] == AliveHash).ToList();
            // bosses ignore a plain kill: try what the game uses, then hits
            foreach (var o in alive)
            {
                foreach (var e in new uint[] { EvtKill, 441 })
                {
                    A.L.SendEvent(o.id, e);
                    if (WaitFor(() => Hash(A, o.index) == DeadHash, 2)) break;
                }
                for (int k = 0; k < 40 && Hash(A, o.index) != DeadHash; k++) { A.L.SendEvent(o.id, k % 2 == 0 ? 58u : 357u); Thread.Sleep(250); }
            }
            var deadInA = alive.Where(o => Hash(A, o.index) == DeadHash).ToList();
            if (deadInA.Count > 0)
            {
                bool same = WaitFor(() => deadInA.All(o => Hash(B, o.index) == DeadHash), 10);
                Result("boss beaten for the party", same, $"{SN(bsc)}: killed in A {string.Join(" ", deadInA.Select(o => Nm(bsc, o.id)))}; dead in B {deadInA.Count(o => Hash(B, o.index) == DeadHash)}/{deadInA.Count}");
            }
            else
            {
                // a boss only goes down through its scripted fight (rounds, cutscenes), which a test
                // can't play; check that hitting it keeps both games' worlds the same
                Log($"{SN(bsc)}: the boss ignores test kills ({string.Join(" ", alive.Select(o => Nm(bsc, o.id)))}); real boss defeat is untested");
                Compare("boss fight keeps the world consistent", A, B, null);
            }
            foreach (var b in new[] { A, B }) WaitControl(b, TimeSpan.FromSeconds(60));
        }
    }

    // ---- 3 + 7. story cutscenes: same scene, other scene, missed and played later --------------------
    static void CutscenePhase(List<Bot> bots)
    {
        var A = bots[0];
        var B = bots[1];
        var C = bots.Count > 2 ? bots[2] : null;
        uint sc = PlayerState.SceneIdOf("JF01");
        if (!GoTo(new List<Bot> { A, B }, sc)) return;
        if (C != null && !GoTo(new List<Bot> { C }, ctx.Hub)) C = null;
        var objs = A.L.SceneObjects();
        var mgrs = objs.Where(o => o.type == 0x28).Select(o => o.id).ToHashSet();
        (int index, uint id, ushort src, uint mgr) starter = default;
        foreach (var o in objs.Where(o => o.type == 0x01))
            foreach (var l in A.L.Links(o.index))
                if (mgrs.Contains(l.target) && l.dst == 0xBE && starter.id == 0) starter = (o.index, o.id, l.src, l.target);
        if (starter.id == 0) { Result("story cutscene", false, $"no trigger starting a cutscene manager in JF01 ({mgrs.Count} managers)"); return; }
        Log($"cutscene: {Nm(sc, starter.id)} --{starter.src}--> {Nm(sc, starter.mgr)}");

        var notices = new List<string>();
        Action<string>? onNotice = null;
        if (C != null) { onNotice = t => { lock (notices) notices.Add(t); }; C.S.Notice += onNotice; }
        uint cCtl = 0, cPlay = 0;
        A.L.SendEvent(starter.id, starter.src);
        bool aPlays = WaitFor(() => A.L.PlayingCutscene() == starter.mgr, 10);
        bool bPlays = WaitFor(() =>
        {
            if (C != null) { cCtl |= C.L.Global(OffControlOff); cPlay |= C.L.PlayingCutscene(); }
            return B.L.PlayingCutscene() == starter.mgr;
        }, 10);
        Result("story cutscene plays for the teammate", aPlays && bPlays, $"{Nm(sc, starter.mgr)} started in A ({aPlays}), B watching it too ({bPlays})");

        // both watch to the end (skip), both get control back
        bool ended = WaitFor(() =>
        {
            foreach (var b in new[] { A, B })
            {
                if (C != null) { cCtl |= C.L.Global(OffControlOff); cPlay |= C.L.PlayingCutscene(); }
                if (b.L.PlayingCutscene() != 0) Tap(b, BtnStart);
                else if (b.L.Global(OffControlOff) != 0) Tap(b, BtnA);
            }
            return A.L.PlayingCutscene() == 0 && B.L.PlayingCutscene() == 0 && A.L.Global(OffControlOff) == 0 && B.L.Global(OffControlOff) == 0;
        }, 120, 300);
        Result("cutscene ends with control back", ended, $"A playing {A.L.PlayingCutscene():X8} controlOff {A.L.Global(OffControlOff):X}; B playing {B.L.PlayingCutscene():X8} controlOff {B.L.Global(OffControlOff):X}");
        uint replay = 0;
        WaitFor(() => { replay |= A.L.PlayingCutscene() | B.L.PlayingCutscene(); return false; }, 8, 200);
        Result("cutscene not replayed", replay == 0, replay == 0 ? "neither A nor B played it again" : $"played again ({replay:X8})");
        if (C == null) return;

        // 7. C was in another world: not interrupted, told, and still gets the cinematic later
        Thread.Sleep(1000);
        C.S.Notice -= onNotice;
        bool told;
        lock (notices) told = notices.Any(n => n.Contains("cutscene"));
        Result("other world not interrupted", cCtl == 0 && cPlay == 0 && told, $"C in {SN(SceneOf(C))}: controlOff seen {cCtl:X}, played {cPlay:X8}, notice: {(told ? string.Join(" | ", notices) : "none")}");
        if (!GoTo(new List<Bot> { C }, sc)) return;
        bool armed = Hash(C, starter.index) == 0; // the trigger is personal: not synced, still armed for C
        uint aRe = 0;
        C.L.SendEvent(starter.id, starter.src);
        bool cPlays = WaitFor(() => { aRe |= A.L.PlayingCutscene() | B.L.PlayingCutscene(); return C.L.PlayingCutscene() == starter.mgr; }, 10);
        WaitFor(() =>
        {
            aRe |= A.L.PlayingCutscene() | B.L.PlayingCutscene();
            if (C.L.PlayingCutscene() != 0) Tap(C, BtnStart);
            else if (C.L.Global(OffControlOff) != 0) Tap(C, BtnA);
            return C.L.PlayingCutscene() == 0 && C.L.Global(OffControlOff) == 0;
        }, 120, 300);
        Result("missed cutscene plays later", cPlays && aRe == 0, $"C reached the trigger in {SN(sc)}: cinematic played for C {cPlays} (trigger personal: {armed}); A/B replayed: {(aRe == 0 ? "no" : aRe.ToString("X8"))}");
        Compare("world effects after the cutscene (A vs C)", A, C, null);
    }

    // ---- 5. conversations ---------------------------------------------------------------------------
    static void TalkPhase(List<Bot> bots)
    {
        var A = bots[0];
        var B = bots[1];
        uint sc = ctx.Hub;
        if (!GoTo(new List<Bot> { A, B }, sc)) return;
        var objs = A.L.SceneObjects();
        uint box = 0;
        // what starts a conversation in the game: an NPC or trigger whose link sends "start conversation"
        // (with the text as parameter) to a talk box
        var boxes = objs.Where(o => o.type == 0x34).Select(o => o.id).ToHashSet();
        var starters = new List<(uint id, ushort src, uint box, byte type)>();
        foreach (var o in objs.Where(o => o.type is 0x2B or 0x01 or 0x2A or 0x1E or 0x0B))
            foreach (var l in A.L.Links(o.index))
                if (l.dst == EvtStartConversation && boxes.Contains(l.target)) starters.Add((o.id, l.src, l.target, o.type));
        Log($"{SN(sc)}: {starters.Count} conversation starters");
        foreach (var t in starters.OrderBy(t => t.type == 0x2B ? 0 : 1).Take(16))
        {
            A.L.SendEvent(t.id, t.src);
            bool on = WaitFor(() => A.L.ActiveTalk() == t.box, 3);
            Log($"{Nm(sc, t.id)} --{t.src}--> {Nm(sc, t.box)}: active {Nm(sc, A.L.ActiveTalk())} controlOff {A.L.Global(OffControlOff):X}");
            if (on && A.L.Global(OffControlOff) != 0) { box = t.box; break; }
            if (A.L.ActiveTalk() != 0) A.L.SendEvent(A.L.ActiveTalk(), EvtEndConversation);
            WaitControl(A, TimeSpan.FromSeconds(10));
        }
        var start = starters.FirstOrDefault(t => t.box == box);
        // villagers: walking up to one starts its conversation (task box) and locks the player
        (int index, uint id, byte type, float x, float y, float z) villager = default;
        void MoveBAway(float x, float z)
        {
            // B stands somewhere else (not at the same villager), on a pickup's spot (solid ground)
            var spot = objs.Where(o => o.type == 4 && (o.x != 0 || o.z != 0)).OrderByDescending(o => Sq(o.x - x) + Sq(o.z - z)).Skip(5).FirstOrDefault();
            if (spot.id != 0) { uint bf = B.L.Frames; B.L.Teleport(spot.x, spot.y + 0.5f, spot.z); Thread.Sleep(1500); Log($"B moved to #{spot.index} ({spot.x:F0},{spot.y:F0},{spot.z:F0}); B frames {bf} -> {B.L.Frames}"); }
            WaitControl(B, TimeSpan.FromSeconds(20));
        }
        if (box == 0)
        {
            A.L.TryReadLocal(out var me);
            foreach (var v in objs.Where(o => o.type == 0x2B && (o.x != 0 || o.z != 0)).OrderBy(o => Sq(o.x - me.X) + Sq(o.z - me.Z)).Take(30))
            {
                MoveBAway(v.x, v.z);
                foreach (var (dx, dz) in new[] { (1.5f, 0f), (0f, 1.5f), (-1.5f, 0f), (0f, -1.5f) })
                {
                    A.L.Teleport(v.x + dx, v.y + 0.5f, v.z + dz);
                    if (WaitFor(() => A.L.ActiveTalk() != 0 && A.L.Global(OffControlOff) != 0, 2)) break;
                }
                Log($"walked up to {Nm(sc, v.id)}: talk {Nm(sc, A.L.ActiveTalk())} controlOff {A.L.Global(OffControlOff):X}; B frames {B.L.Frames} B talk {Nm(sc, B.L.ActiveTalk())} A frames {A.L.Frames}");
                if (A.L.ActiveTalk() != 0 && A.L.Global(OffControlOff) != 0) { box = A.L.ActiveTalk(); villager = v; break; }
                if (A.L.ActiveTalk() != 0) A.L.SendEvent(A.L.ActiveTalk(), EvtEndConversation);
            }
        }
        if (box == 0) { Result("conversation", false, $"none of {objs.Count(o => o.type == 0x34)} talk boxes in {SN(sc)} started a conversation"); return; }
        Log($"conversation: {Nm(sc, box)}");
        bool offered = WaitFor(() => B.L.OfferHandled && B.L.OfferResult == 0, 6);
        Shot(B, "talk-prompt");
        // B is not frozen: it can walk while A talks (the prompt never locks it)
        B.L.TryReadLocal(out var p0);
        B.L.PadOverride(true, 0, 0, 110);
        Thread.Sleep(1500);
        B.L.PadOverride(false);
        B.L.TryReadLocal(out var p1);
        double moved = Math.Sqrt(Sq(p1.X - p0.X) + Sq(p1.Z - p0.Z));
        int slot = SlotOf(B, "A");
        bool idle = slot >= 0 && (B.L.RemoteFlags(slot) & Mb.RfNoInput) != 0;
        {
            var d = B.L.Mem.Read(GameLink.MailboxAddr + 0x4564, 16);
            uint DU(int k) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(k * 4));
            uint f0 = B.L.Frames; Thread.Sleep(500);
            Log($"B frames {f0} -> {B.L.Frames}, mailbox frames {B.L.Mem.ReadU32(GameLink.MailboxAddr + 0x34)}, local frame {(B.L.TryReadLocal(out var bl) ? bl.Frame : 0)}");
            Log($"B: talks stopped after a puppet update {DU(0)} (last {Nm(sc, DU(1))}), talks started by applying a state {DU(2)} (object {Nm(sc, DU(3))}); B's talk now {Nm(sc, B.L.ActiveTalk())}");
        }
        Result("conversation: teammate not frozen", B.L.Global(OffControlOff) == 0 && moved > 1 && idle,
            $"A talks ({Nm(sc, box)}); B controlOff {B.L.Global(OffControlOff):X}, B walked {moved:F1} units, B's puppet of A gets no input: {idle}");
        Result("watch-along prompt shown", offered, offered ? "B shows the prompt and keeps playing" : $"B's talk box {Nm(sc, B.L.ActiveTalk())}, offer result {B.L.OfferResult} handled {B.L.OfferHandled}");
        // accept: D-pad Up starts the same conversation for B
        if (offered)
        {
            Tap(B, BtnUp);
            bool watching = WaitFor(() => B.L.OfferResult == 1 && B.L.ActiveTalk() != 0, 5);
            Shot(B, "talk-watching");
            Result("watch along (accept)", watching, $"offer result {B.L.OfferResult}, B's talk box {Nm(sc, B.L.ActiveTalk())}");
        }
        bool done = WaitFor(() =>
        {
            foreach (var b in new[] { A, B })
                if (b.L.ActiveTalk() != 0 || b.L.Global(OffControlOff) != 0) Tap(b, BtnA);
            return A.L.ActiveTalk() == 0 && B.L.ActiveTalk() == 0 && A.L.Global(OffControlOff) == 0 && B.L.Global(OffControlOff) == 0;
        }, 90, 300);
        if (!done) foreach (var b in new[] { A, B }) if (b.L.ActiveTalk() != 0) b.L.SendEvent(b.L.ActiveTalk(), EvtEndConversation);
        Result("conversation ends for both", done, $"A talk {A.L.ActiveTalk():X8} controlOff {A.L.Global(OffControlOff):X}; B talk {B.L.ActiveTalk():X8} controlOff {B.L.Global(OffControlOff):X}");

        // decline: D-pad Down closes the prompt, B keeps playing (the villager won't start the same
        // conversation twice, so the offer is handed to B's game directly, as the bridge would)
        WaitControl(B, TimeSpan.FromSeconds(10));
        B.L.OfferTalk(box, "A is talking. Watch along? Up: yes  Down: no");
        if (WaitFor(() => B.L.OfferHandled && B.L.OfferResult == 0, 5))
        {
            Thread.Sleep(500);
            Tap(B, BtnDown);
            bool declined = WaitFor(() => B.L.OfferResult == 2, 5);
            Result("watch along (decline)", declined && B.L.ActiveTalk() == 0 && B.L.Global(OffControlOff) == 0, $"offer result {B.L.OfferResult}, B's talk box {B.L.ActiveTalk():X8}, controlOff {B.L.Global(OffControlOff):X}");
        }
        else Result("watch along (decline)", false, $"prompt not shown (result {B.L.OfferResult})");
        // and no answer: it goes away by itself
        B.L.OfferTalk(box, "A is talking. Watch along? Up: yes  Down: no");
        bool timedOut = WaitFor(() => B.L.OfferHandled && B.L.OfferResult == 2, 12);
        Result("watch along (no answer)", timedOut, $"offer result {B.L.OfferResult} after waiting");
        WaitFor(() =>
        {
            foreach (var b in new[] { A, B })
                if (b.L.ActiveTalk() != 0 || b.L.Global(OffControlOff) != 0) Tap(b, BtnA);
            return A.L.ActiveTalk() == 0 && A.L.Global(OffControlOff) == 0;
        }, 60, 300);
    }

    // ---- 6. four players, four maps ---------------------------------------------------------------
    static void FourMapsPhase(List<Bot> bots, bool sims)
    {
        if (!sims) { Result("four maps", false, "needs --sims"); return; }
        if (bots.Count < 4)
        {
            var D = StartBot("D", ctx.Party, ctx.Port, ctx.Hub, ctx.Dolphin, ctx.Work);
            bots.Add(D);
            if (!WaitAll(new List<Bot> { D }, ctx.Hub, TimeSpan.FromSeconds(150))) { Result("four maps", false, "D never came up: " + Describe(new List<Bot> { D })); return; }
        }
        var four = bots.Take(4).ToList();
        var maps = new[] { "HB01", "JF01", "BB01", "GL01" }.Select(PlayerState.SceneIdOf).ToArray();
        for (int i = 0; i < 4; i++) if (SceneOf(four[i]) != maps[i]) four[i].L.Warp(maps[i]);
        bool all = true;
        for (int i = 0; i < 4; i++) all &= GoTo(new List<Bot> { four[i] }, maps[i]);
        if (!all) return;
        bool simsUp = WaitFor(() => maps.All(m => SimOf(m) != null), 120, 500);
        Result("four maps: a sim per map", simsUp, string.Join(", ", maps.Select(m => $"{SN(m)} {(SimOf(m) != null ? "running" : "no sim")}")));

        // CPU and RAM per sim
        var usage = new List<string>();
        var simProcs = maps.Select(m => SimOf(m)?.Pid ?? 0).Where(p => p != 0).Select(p => { try { return Process.GetProcessById(p); } catch { return null; } }).Where(p => p != null).ToList();
        var t0 = simProcs.Select(p => p!.TotalProcessorTime).ToList();
        Thread.Sleep(5000);
        for (int i = 0; i < simProcs.Count; i++)
        {
            var p = simProcs[i]!;
            p.Refresh();
            double cpu = (p.TotalProcessorTime - t0[i]).TotalSeconds / 5.0 * 100;
            usage.Add($"pid {p.Id}: {cpu:F0}% of a core, {p.WorkingSet64 / (1 << 20)} MB");
        }
        Result("four maps: sim cost", simProcs.Count == 4, string.Join("; ", usage));

        // each player collects in their own map
        var start = four.Select(b => b.L.ReadCounters()[0]).ToList();
        var got = new List<(uint scene, int index, uint hash)>();
        int gainedSum = 0;
        for (int i = 0; i < 4; i++)
        {
            var b = four[i];
            var objs = b.L.SceneObjects();
            var h = b.L.ReadObjHashes(objs.Count);
            b.L.TryReadLocal(out var me);
            var cands = objs.Where(o => o.type == 4 && h[o.index] != 0 && b.L.PickupInfo(o.index) is { type: 0 } pi && (pi.state & 0x0C) == 0 && (pi.state & 0x40) != 0 && (pi.flags & 2) != 0)
                .OrderBy(o => Sq(o.x - me.X) + Sq(o.z - me.Z)).Take(4);
            foreach (var o in cands)
            {
                uint s0 = b.L.ReadCounters()[0];
                var r = CollectOne(b, new List<Bot>(), o, "shiny", 0, 0);
                if (r == null) continue;
                got.Add((maps[i], o.index, Hash(b, o.index)));
                gainedSum += (int)(b.L.ReadCounters()[0] - s0);
                break;
            }
        }
        Thread.Sleep(4000);
        var simOk = got.Select(g => (g, sim: SimOf(g.scene))).Select(x => x.sim != null && x.sim.Link!.ReadObjHashes(x.g.index + 1)[x.g.index] == x.g.hash).ToList();
        var party = ctx.Server.FindParty(ctx.Party)!;
        var stored = got.Select(g => party.ObjectsIn(g.scene).Any(o => o.Index == g.index)).ToList();
        Result("four maps: changes reach the party world", got.Count == 4 && simOk.All(x => x) && stored.All(x => x),
            string.Join("; ", got.Select((g, k) => $"{SN(g.scene)} #{g.index}: sim {(simOk[k] ? "has it" : "MISSING")}, server {(stored[k] ? "has it" : "MISSING")}")));
        var now = four.Select(b => b.L.ReadCounters()[0]).ToList();
        bool sums = now.Distinct().Count() == 1 && four.Select((b, k) => (int)now[k] - (int)start[k]).All(d => d == gainedSum);
        Result("four maps: shared inventory sums", sums, $"gained {gainedSum} shinies in total; " + string.Join(", ", four.Select((b, k) => $"{b.Name} {start[k]}->{now[k]}")));

        // everyone visits the next player's map and sees what was collected there
        for (int i = 0; i < 4; i++) four[i].L.Warp(maps[(i + 1) % 4]);
        var seen = new List<string>();
        bool allSeen = true;
        for (int i = 0; i < 4; i++)
        {
            uint m = maps[(i + 1) % 4];
            if (!GoTo(new List<Bot> { four[i] }, m)) { allSeen = false; continue; }
            foreach (var g in got.Where(g => g.scene == m))
            {
                bool ok = WaitFor(() => Hash(four[i], g.index) == g.hash, 8);
                allSeen &= ok;
                seen.Add($"{four[i].Name} in {SN(m)}: {(ok ? "collected" : "NOT collected")}");
            }
        }
        Result("four maps: visitors see the changes", allSeen && seen.Count == got.Count, string.Join("; ", seen));

        // sims stop once a map is empty (IdleSeconds after the last player left)
        foreach (var b in four) b.L.Warp(ctx.Hub);
        foreach (var b in four) GoTo(new List<Bot> { b }, ctx.Hub);
        var empty = maps.Where(m => m != ctx.Hub).ToList();
        bool stopped = WaitFor(() => empty.All(m => ctx.Server.Sims.Get(party, m) == null), 90, 1000);
        Result("four maps: idle sims stop", stopped, string.Join(", ", empty.Select(m => $"{SN(m)} {(ctx.Server.Sims.Get(party, m)?.Status ?? "stopped")}")) + $"; hub sim {(SimOf(ctx.Hub) != null ? "running" : "not running")}");
    }
}
