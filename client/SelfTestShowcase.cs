using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BfbbOnline.Shared;

namespace BfbbOnline.Client;

static partial class SelfTest
{
    /// <summary>
    /// --phases showcase --visible B: screenshot test of things you can only judge by eye.
    /// 1. Kelp Forest lost campers: A and B rescue two different campers in the same instant
    ///    (KF01, KF02), A finds the rest (KF01 x3, KF02 x2, KF04 x1) by walking up to
    ///    it; B is in the same level each time. Checks: the camper's state changes in A, B's copy
    ///    matches, the worlds match; then both go to the camp (KF03) and compare CAMPER_COUNTER.
    /// 2. Bubble Bowl / Cruise Bubble: A stands in front of B's camera and bowls, then fires a cruise
    ///    bubble; B's game is captured in bursts while they fly (screen copy, PrintWindow can miss D3D
    ///    models). Shots land in selftest\shots\showcase-*.png.
    /// </summary>
    static (uint btn, int ms)? bowlInput;

    static void ShowcasePhase(List<Bot> bots, Dictionary<string, string> a)
    {
        var A = bots[0];
        var B = bots[1];
        if (visible != B.Name) Log("showcase: run with --visible B to get screenshots of B's view");
        var pair = new List<Bot> { A, B };
        foreach (var b in pair) b.L.Mem.TryWrite(0x803C0558 + 0x9BD, new byte[] { 1, 1 });   // bowl + cruise unlocked

        // ---- 1. lost campers ----
        int found = 0, total = 0;
        var only = a.GetValueOrDefault("showcasescenes");
        foreach (var (scene, names) in new[]
        {
            ("KF01", new[] { "LOST_CHILD_NPC_01", "LOST_CHILD_NPC_02", "LOST_CHILD_NPC_03" }),
            ("KF02", new[] { "CAMPER_NPC_1", "CAMPER_NPC_2" }),
            ("KF04", new[] { "CAMPER_NPC_01" }),
        })
        {
            if (only != null && !only.Split(',').Contains(scene)) continue;
            uint sc = PlayerState.SceneIdOf(scene);
            if (!GoTo(pair, sc)) return;
            var objs = A.L.SceneObjects();
            // a rescue is the camper's trigger: walking into it disables it (persistent), starts the
            // camper's talk and re-checks CAMPERS_GOTALL_COND. The camper NPC itself doesn't change.
            var trigs = objs.Where(q => q.type == 0x01 && Nm(sc, q.id) is var n && (n.StartsWith("LOSTCHILD_") || n.StartsWith("CAMPER_TRIG"))).ToList();
            HashSet<string> Done(Bot X) => trigs.Where(t => (X.L.ObjField(t.index, 4) & 1) == 0).Select(t => Nm(sc, t.id)).ToHashSet();
            string Set(HashSet<string> d) => d.Count == 0 ? "none" : string.Join(",", d.OrderBy(x => x));
            void Rescue(Bot X, (int index, uint id, byte type, float x, float y, float z) o)
            {
                foreach (var (dx, dz) in new[] { (0f, 0f), (1.5f, 0f), (0f, 1.5f), (-1.5f, 0f), (0f, -1.5f) })
                {
                    X.L.Teleport(o.x + dx, o.y + 0.5f, o.z + dz);
                    if (WaitFor(() => X.L.ActiveTalk() != 0 || X.L.Global(OffControlOff) != 0, 2.5)) break;
                }
                WaitControl(X, TimeSpan.FromSeconds(30));   // answers the camper's text
                Thread.Sleep(1500);
            }
            // both games must show exactly `expect` rescued campers, the same ones
            void Check(string label, int expect)
            {
                bool ok = WaitFor(() => Done(A).Count == expect && Done(A).SetEquals(Done(B)), 10);
                Result(label, ok, $"{trigs.Count} camper triggers; rescued in A: {Set(Done(A))}; in B: {Set(Done(B))} (want {expect})");
            }
            var cs = names.Select(n => objs.FirstOrDefault(x => Nm(sc, x.id) == n)).ToList();
            total += names.Length;
            int before = Done(A).Count, next = 0;
            if (cs.Count >= 2 && cs[0].id != 0 && cs[1].id != 0)
            {
                // two players rescue two different campers in the same instant
                var go = new Barrier(2);
                var t1 = new Thread(() => { go.SignalAndWait(); Rescue(A, cs[0]); });
                var t2 = new Thread(() => { go.SignalAndWait(); Rescue(B, cs[1]); });
                t1.Start(); t2.Start();
                Thread.Sleep(1200);
                Shot(B, $"showcase-camper-{scene}-together", true);
                t1.Join(60000); t2.Join(60000);
                Shot(B, $"showcase-camper-{scene}-together-after", true);
                Check($"campers {scene}: A and B rescue two at the same time", before + 2);
                next = 2;
            }
            for (int k = next; k < names.Length; k++)
            {
                var o = cs[k];
                if (o.id == 0) { Result($"camper {scene} {names[k]}", false, "not in the scene"); continue; }
                // B stands a little back so its camera sees the camper and A
                B.L.Teleport(o.x + 5, o.y + 0.5f, o.z + 5);
                Thread.Sleep(1500);
                WaitControl(B, TimeSpan.FromSeconds(10));
                Rescue(A, o);
                Shot(B, $"showcase-camper-{scene}-{names[k]}", true);
                Check($"camper {scene} {names[k]} (A)", before + k + 1);
            }
            found += Done(A).Count;
            Compare($"campers: world in {scene}", A, B, null);
        }
        // the camp: every found camper counted once in both games (also B, who found none)
        uint camp = PlayerState.SceneIdOf("KF03");
        if (GoTo(pair, camp))
        {
            var objs = A.L.SceneObjects();
            var cnt = objs.FirstOrDefault(x => Nm(camp, x.id) == "CAMPER_COUNTER");
            string C(Bot b) => cnt.id == 0 ? "?" : $"{b.L.ObjField(cnt.index, 0x14):X8}";
            bool same = cnt.id != 0 && WaitFor(() => C(A) == C(B), 8);
            Shot(B, "showcase-camp-KF03", true);
            string Camp(Bot b) => string.Join(" ", Enumerable.Range(1, 6).Select(i => objs.FirstOrDefault(x => Nm(camp, x.id) == $"CAMPER_0{i}")).Where(x => x.id != 0).Select(x => $"{b.L.ObjField(x.index, 4) & 0xFFFF:X}"));
            Log($"camp campers baseFlags: A {Camp(A)} / B {Camp(B)}");
            Result("campers: counter at the camp", same, $"{found}/{total} campers rescued (two of them at the same time); CAMPER_COUNTER state/count A {C(A)} B {C(B)}");
            Compare("campers: world in KF03", A, B, null);
        }

        // ---- 2. bubble bowl + cruise bubble seen by a teammate ----
        uint sc2 = PlayerState.SceneIdOf(a.GetValueOrDefault("abilityscene", "JF01"));
        if (!GoTo(pair, sc2)) return;
        foreach (var move in new[] { "wand", "bowl", "cruise" })
        {
            WaitControl(A, TimeSpan.FromSeconds(10));
            WaitControl(B, TimeSpan.FromSeconds(10));
            // A ~7 units ahead of B's camera, a bit to the side, on B's level
            var cam = B.L.Mem.Read(0x803C0558 + 0x14, 0x40);
            float F(int k) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(cam.AsSpan(k));
            float cx = F(0x30), cz = F(0x38), ax = F(0x20), az = F(0x28), hl = MathF.Sqrt(ax * ax + az * az);
            B.L.TryReadLocal(out var bNow);
            A.L.Teleport(cx + ax / hl * 7 - az / hl * 2, bNow.Y + 0.3f, cz + az / hl * 7 + ax / hl * 2);
            Thread.Sleep(2000);
            Shot(B, $"showcase-{move}-0-before", true);
            int slot = SlotOf(B, A.Name);
            var L = A.L;
            if (move == "bowl" && bowlInput == null)
            {
                // which input throws a ball in A's own game (the ball entity leaves its parking spot)
                string BP() { var m = A.L.Mem; uint e = A.L.Global(0x19E4); if (e < 0x80000000) return "none"; uint mat = m.ReadU32(m.ReadU32(e + 0x24) + 0x4C); return Convert.ToHexString(m.Read(mat + 0x30, 12)); }
                foreach (var (label, btn, ms) in new[] { ("X tap", GcX, 150), ("X hold 1 s", GcX, 1000), ("X hold 2.5 s", GcX, 2500) })
                {
                    WaitControl(A, TimeSpan.FromSeconds(10));
                    string p0 = BP();
                    string Live() { var m = A.L.Mem; uint fr = A.L.Global(0x728); var b = m.Read(fr + 0x30, 12); float P(int k) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(k)); return $"{P(0):F2},{P(4):F2},{P(8):F2} padOn {m.ReadU32(0x8038C994 + 0x2C):X}"; }
                    L.PadOverride(true, 0, 0, 0); Thread.Sleep(1500);   // neutral pad: land, nothing held
                    string l0 = Live();
                    L.PadOverride(true, btn, 0, 0); Thread.Sleep(ms / 2); string l1 = Live(); Thread.Sleep(ms - ms / 2); L.PadOverride(false);
                    Log($"bowl input probe live: before {l0}, holding {l1}, after {Live()}");
                    bool thrown = WaitFor(() => BP() != p0, 2.5);
                    A.L.TryReadLocal(out var am);
                    Log($"bowl input probe: {label}: ball {(thrown ? "THROWN" : "not thrown")} (A at {am.X:F1},{am.Y:F1},{am.Z:F1}, controlOff {am.ControlOff:X}, frames {A.L.Frames}, pad {A.L.Mem.ReadU32(GameLink.MailboxAddr + Mb.OPadOverride)}, gameState {A.L.Mem.ReadU32(0x803CB8A8)})");
                    if (thrown) { bowlInput = (btn, ms); break; }
                    Thread.Sleep(1500);
                }
                if (bowlInput == null) { Result("showcase: bowl input", false, "no input made A's game throw its bowling ball (see log)"); bowlInput = (GcB, 900); }
                A.L.Teleport(cx + ax / hl * 7 - az / hl * 2, bNow.Y + 0.3f, cz + az / hl * 7 + ax / hl * 2);
                Thread.Sleep(2000);
            }
            var t = new Thread(() =>
            {
                if (move == "wand") { Hold(L, GcB, 0, 0, 150); Hold(L, 0, 0, 0, 900); Hold(L, GcB, 0, 0, 150); Hold(L, 0, 0, 0, 1500); }   // two swings
                else if (move == "bowl") { Hold(L, bowlInput!.Value.btn, 0, 0, bowlInput.Value.ms); Hold(L, 0, 0, 0, 2000); }
                else { Hold(L, GcL, 0, 0, 150); Hold(L, 0, 50, 80, 700); Hold(L, GcA, 50, 80, 1500); Hold(L, 0, -60, 0, 800); Hold(L, GcL, 0, 0, 150); Hold(L, 0, 0, 0, 1500); }
                L.PadOverride(false);
            }) { IsBackground = true };
            t.Start();
            var errs = new List<double>();
            for (int i = 1; i <= 8; i++)
            {
                Thread.Sleep(450);
                Shot(B, $"showcase-{move}-{i}", true);
                string Ball(Bot X)
                {
                    var m = X.L.Mem;
                    uint e = X.L.Global(0x19E4);
                    if (e < 0x80000000) return $"{X.Name} ball ptr {e:X8}";
                    uint mdl = m.ReadU32(e + 0x24), mat = mdl >= 0x80000000 ? m.ReadU32(mdl + 0x4C) : 0;
                    var pb = mat >= 0x80000000 ? m.Read(mat + 0x30, 12) : new byte[12];
                    float P(int k) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(pb.AsSpan(k));
                    return $"{X.Name} ball {e:X8} upd {m.ReadU32(e + 0x30):X8} flags {Convert.ToHexString(m.Read(e + 0x18, 8))} at {P(0):F1},{P(4):F1},{P(8):F1}";
                }
                string Pos(Bot X) => X.L.TryReadLocal(out var q) ? $"{X.Name} {q.X:F1},{q.Y:F1},{q.Z:F1}" : X.Name + " ?";
                Log($"bowl diag {move} {i}: {string.Join("; ", bots.Select(Pos))}; B puppets " + string.Join(" ", Enumerable.Range(0, Mb.MaxRemote).Where(B.L.RemoteActive).Select(k => $"{B.L.RemoteName(k)}@{(B.L.PuppetPos(k) is { } pp ? $"{pp.x:F1},{pp.y:F1},{pp.z:F1}" : "?")}")) + $"; {Ball(A)}; {Ball(B)}");
                if (slot >= 0 && A.L.TryReadLocal(out var real) && B.L.PuppetPos(slot) is { } p)
                    errs.Add(Math.Sqrt(Sq(p.x - real.X) + Sq(p.y - real.Y) + Sq(p.z - real.Z)));
            }
            t.Join(10000);
            bool ctl = WaitControl(A, TimeSpan.FromSeconds(10)) & WaitControl(B, TimeSpan.FromSeconds(10));
            bool alive = A.Hung == null && B.Hung == null && A.S.GameRunning && B.S.GameRunning;
            Result($"showcase: {move} seen by B", alive && ctl && errs.Count > 0,
                $"{errs.Count} puppet samples, error max {(errs.Count > 0 ? errs.Max() : 0):F2} units; control back {ctl}; look at selftest\\shots\\showcase-{move}-*.png for the {(move == "bowl" ? "ball" : move == "wand" ? "wand swing" : "missile")}");
        }
    }
}

static partial class SelfTest
{
    /// <summary>
    /// --phases pvp [--pvp]: A attacks B from point blank (wand, bash, bowl). With --pvp (server
    /// setting) every attack must cost B health and knock B back; without it nothing may happen.
    /// Standing next to B without attacking must never hurt.
    /// </summary>
    static void PvpPhase(List<Bot> bots, Dictionary<string, string> a)
    {
        var A = bots[0];
        var B = bots[1];
        bool on = a.ContainsKey("pvp");
        var pair = new List<Bot> { A, B };
        uint sc = PlayerState.SceneIdOf(a.GetValueOrDefault("abilityscene", "JF01"));
        if (!GoTo(pair, sc)) return;
        foreach (var b in pair) b.L.Mem.TryWrite(0x803C0558 + 0x9BD, new byte[] { 1, 1 });
        WaitFor(() => (B.L.Flags & Mb.FPvp) != 0 == on, 10);
        Result("pvp: flag reaches the game", ((B.L.Flags & Mb.FPvp) != 0) == on, $"server pvp {on}, B's mb.flags {B.L.Flags:X}");
        foreach (var move in a.GetValueOrDefault("pvpmoves", "idle,bowl,wand,bash,bounce,cruise").Split(","))
        {
            foreach (var b in pair) { WaitControl(b, TimeSpan.FromSeconds(10)); b.L.SetGlobal(0x16B0, 3); }   // full health
            // B in front of A along A's own facing (frame.mat.at), 1 unit (bowl: 4, the ball rolls into B)
            A.L.TryReadLocal(out var ap);
            var fm = A.L.Mem.Read(A.L.Global(0x728) + 0x20, 12);
            float atx = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(fm.AsSpan(0)), atz = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(fm.AsSpan(8));
            float al = MathF.Max(0.001f, MathF.Sqrt(atx * atx + atz * atz));
            float off = move == "bowl" ? 4f : move == "cruise" ? 5f : 1f;
            B.L.Teleport(ap.X + atx / al * off, ap.Y + 0.3f, ap.Z + atz / al * off);
            B.L.PadOverride(true, 0, 0, 0);
            Thread.Sleep(2500);   // land, damage/control timers run out
            B.L.PadOverride(false);
            B.L.TryReadLocal(out var bp);
            var ap2 = ap;
            B.L.TryReadLocal(out var b0);
            uint h0 = B.L.Global(0x16B0);
            switch (move)
            {
                case "idle": Thread.Sleep(2500); break;
                case "wand": Hold(A.L, GcB, 0, 0, 150); Hold(A.L, 0, 0, 0, 1500); break;
                case "bash": Hold(A.L, GcY, 0, 0, 150); Hold(A.L, 0, 0, 0, 1500); break;
                case "bounce": Hold(A.L, GcA, 0, 0, 150); Hold(A.L, 0, 0, 0, 300); Hold(A.L, GcY, 0, 0, 150); Hold(A.L, 0, 0, 0, 1500); break;
                case "cruise":
                    // fire (L), let it fly toward B (straight ahead), detonate (L) after a growing delay
                    foreach (int fly in new[] { 300, 600, 1000, 1500 })
                    {
                        if (B.L.Global(0x16B0) < h0) break;
                        WaitControl(A, TimeSpan.FromSeconds(10));
                        Hold(A.L, GcL, 0, 0, 150); Hold(A.L, 0, 0, 0, fly); Hold(A.L, GcL, 0, 0, 150); Hold(A.L, 0, 0, 0, 2000);
                        A.L.PadOverride(false);
                        Log($"pvp cruise: detonated after {fly} ms: B health {B.L.Global(0x16B0)}, hits {B.L.DbgRender(17)}");
                    }
                    break;
                case "bowl":
                    string BallAt(Bot X) { var m = X.L.Mem; uint e = X.L.Global(0x19E4); if (e < 0x80000000) return "?"; uint mat = m.ReadU32(m.ReadU32(e + 0x24) + 0x4C); var q = m.Read(mat + 0x30, 12); float P(int o) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(q.AsSpan(o)); return $"{P(0):F1},{P(4):F1},{P(8):F1}"; }
                    for (int tries = 0; tries < 3 && B.L.Global(0x16B0) >= h0; tries++)
                    {
                        string Anim(Bot X) { try { var m = X.L.Mem; uint mdl = X.L.Global(0x704), an = m.ReadU32(mdl + 0xC), sg = m.ReadU32(an + 8), st = m.ReadU32(sg + 4), nm = m.ReadU32(st + 4); var raw = m.Read(nm, 24); int z = Array.IndexOf(raw, (byte)0); return System.Text.Encoding.ASCII.GetString(raw, 0, z < 0 ? 24 : z); } catch { return "?"; } }
                        string parked = BallAt(A);
                        A.L.PadOverride(true, 0, 0, 0);
                        WaitFor(() => Anim(A).StartsWith("Idle"), 4);   // bowling starts only from standing
                        Log($"pvp bowl try {tries}: A anim {Anim(A)} controlOff {A.L.Global(0x1788):X} powerup {A.L.Mem.Read(0x803C0558 + 0x9BD, 2)[0]}");
                        Hold(A.L, GcX, 0, 0, 150); A.L.PadOverride(true, 0, 0, 0);
                        Thread.Sleep(100); Log($"pvp bowl try {tries}: after X A anim {Anim(A)}");
                        for (int k = 0; k < 10; k++)
                        {
                            Thread.Sleep(200);
                            B.L.TryReadLocal(out var bq);
                            Log($"pvp bowl try {tries}: A's ball {BallAt(A)} (parked {parked}), B's ball {BallAt(B)}, B {bq.X:F1},{bq.Y:F1},{bq.Z:F1}, owner/rolling/hit {B.L.DbgRender(16):X}, hits {B.L.DbgRender(17)}");
                        }
                    }
                    break;
            }
            A.L.PadOverride(false);
            Shot(B, $"pvp-{(on ? "on" : "off")}-{move}", true);
            uint h1 = B.L.Global(0x16B0);
            B.L.TryReadLocal(out var b1);
            double pushed = Math.Sqrt(Sq(b1.X - b0.X) + Sq(b1.Z - b0.Z));
            bool hurt = h1 < h0;
            bool want = on && move != "idle";
            Result($"pvp {(on ? "on" : "off")}: {move}", hurt == want,
                $"B health {h0} -> {h1}, B pushed {pushed:F1} units; A {Math.Sqrt(Sq(ap2.X - bp.X) + Sq(ap2.Z - bp.Z)):F1} units from B before the {move} (want hurt: {want})");
        }
    }

    /// <summary>
    /// --phases chars [--pvp] [--busscene JF02]: characters through a real bus stop (R at the stop).
    /// 1. A and B press R at the stop in the same instant: both become the stop's character; each
    ///    game's puppet of the other must wear that model within a moment (latency logged), C too;
    ///    worlds stay equal, both get control back.
    /// 2. As that character A jumps and attacks in front of B: A's own game and B's puppet must play
    ///    the same move (anims, jump height); with --pvp an attack hurts B, without never.
    /// 3. Patrick: belly flop onto the Patrick seesaw spring (PATTEETER_*): A must be flung up, B's
    ///    puppet of A too, and the seesaw's state must match in both games.
    /// 4. Both switch back at once (SpongeBob everywhere again).
    /// </summary>
    const uint GCurrentPlayer = 0x803CB660, GModelSb = 0x16A0, GEntModel = 0x704;
    static readonly string[] CharNames = { "SpongeBob", "Patrick", "Sandy" };
    static void CharsPhase(List<Bot> bots, Dictionary<string, string> a)
    {
        var A = bots[0];
        var B = bots[1];
        bool on = a.ContainsKey("pvp");
        var pair = new List<Bot> { A, B };
        string scene = a.GetValueOrDefault("busscene", "JF02");
        uint sc = PlayerState.SceneIdOf(scene);
        if (!GoTo(pair, sc)) return;
        foreach (var b in pair) WaitControl(b, TimeSpan.FromSeconds(10));
        A.L.TryReadLocal(out var spawn);   // solid ground (the bus stop stands at the top of a slide)
        var objs = A.L.SceneObjects();
        var stop = objs.FirstOrDefault(q => Nm(sc, q.id).StartsWith("BUSSTOP_BUSSTOP"));
        if (stop.id == 0) { Result($"chars {scene}: bus stop", false, "no BUSSTOP_BUSSTOP object"); return; }
        uint sp = A.L.ObjPtr(stop.index);
        float SF(Bot X, uint addr) => BitConverter.Int32BitsToSingle((int)X.L.Mem.ReadU32(addr));
        float sx = SF(A, sp + 0x18), sy = SF(A, sp + 0x1C), sz = SF(A, sp + 0x20);
        uint Ch(Bot X) => X.L.Mem.ReadU32(GCurrentPlayer);
        bool Wears(Bot X, uint ch) => ch < 3 && X.L.Global(GEntModel) != 0 && X.L.Global(GEntModel) == X.L.Global(GModelSb + 4 * ch);
        bool PupWears(Bot viewer, int slot, uint ch) => ch < 3 && slot >= 0 && viewer.L.PuppetGlobal(slot, GEntModel) != 0 && viewer.L.PuppetGlobal(slot, GEntModel) == viewer.L.PuppetGlobal(slot, GModelSb + 4 * ch);
        string CN(uint c) => CharNames[Math.Min(c, 2u)];
        Log($"chars {scene}: bus stop {Nm(sc, stop.id)} at {sx:F1},{sy:F1},{sz:F1}");

        // both at the stop, R in the same instant
        bool Switch(string label)
        {
            uint was = Ch(A);
            A.L.Teleport(sx - 0.6f, sy + 0.2f, sz); B.L.Teleport(sx + 0.6f, sy + 0.2f, sz);
            foreach (var b in pair) b.L.PadOverride(true, 0, 0, 0);
            Thread.Sleep(2000);   // land (JumpState 0), the stop reaches state 1
            Log($"chars {scene}: {label}: stop state A {A.L.Mem.ReadU32(sp + 0x24)} B {B.L.Mem.ReadU32(B.L.ObjPtr(stop.index) + 0x24)}, characters A {Ch(A)} B {Ch(B)}");
            var go = new Barrier(2);
            var ts = pair.Select(b => new Thread(() => { go.SignalAndWait(); Hold(b.L, GcR, 0, 0, 150); b.L.PadOverride(true, 0, 0, 0); })).ToList();
            ts.ForEach(t => t.Start()); ts.ForEach(t => t.Join(5000));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool changed = WaitFor(() => Ch(A) != was && Ch(B) != was && Wears(A, Ch(A)) && Wears(B, Ch(B)), 12, 20);
            double tSwitch = sw.Elapsed.TotalSeconds;
            var lat = new Dictionary<string, double>();
            var watchers = bots.Where(v => v.S.GameRunning && v.Hung == null && v.L.TryReadLocal(out var vs) && vs.SceneId == sc).ToList();
            int want = watchers.Sum(v => pair.Count(w => w != v));
            var swPup = System.Diagnostics.Stopwatch.StartNew();
            bool pupOk = changed && WaitFor(() =>
            {
                foreach (var v in watchers) foreach (var who in pair)
                {
                    if (v == who) continue;
                    string k = $"{v.Name} sees {who.Name}";
                    if (!lat.ContainsKey(k) && PupWears(v, SlotOf(v, who.Name), Ch(who))) lat[k] = swPup.Elapsed.TotalSeconds;
                }
                return lat.Count == want;
            }, 5, 20);
            Shot(B, $"chars-bus-{label}", true);
            Result($"chars {scene}: {label}: both switch at once", changed && Ch(A) == Ch(B),
                $"A {CN(Ch(A))} B {CN(Ch(B))} {tSwitch:F1} s after R (were {CN(was)}); models A {Wears(A, Ch(A))} B {Wears(B, Ch(B))}");
            Result($"chars {scene}: {label}: teammates see the new character", pupOk,
                $"{lat.Count}/{want} after both games swapped: " + string.Join(", ", lat.Select(kv => $"{kv.Key} {kv.Value * 1000:F0} ms")));
            foreach (var b in pair) b.L.PadOverride(false);
            bool ctl = WaitControl(A, TimeSpan.FromSeconds(15)) & WaitControl(B, TimeSpan.FromSeconds(15));
            Result($"chars {scene}: {label}: control back", ctl, $"controlOff A {A.L.Global(0x1788):X} B {B.L.Global(0x1788):X}");
            Compare($"chars {scene}: {label}: world", A, B, null);
            return changed;
        }

        // A does a move (jump first unless a ground attack); compares A's own anims with B's puppet
        (float aRise, float pRise) Move(string label, uint btn, bool jump, float px, float py, float pz, bool bInFront, int delay = 350)
        {
            foreach (var b in pair) { WaitControl(b, TimeSpan.FromSeconds(10)); b.L.SetGlobal(0x16B0, 3); }
            A.L.Teleport(px, py, pz);
            A.L.PadOverride(true, 0, 0, 0); Thread.Sleep(1500);
            A.L.TryReadLocal(out var ap);
            int slot = SlotOf(B, A.Name);
            if (bInFront)
            {
                var fm = A.L.Mem.Read(A.L.Global(0x728) + 0x20, 12);
                float atx = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(fm.AsSpan(0)), atz = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(fm.AsSpan(8));
                float al = MathF.Max(0.001f, MathF.Sqrt(atx * atx + atz * atz));
                B.L.Teleport(ap.X + atx / al, ap.Y + 0.3f, ap.Z + atz / al);
            }
            else B.L.Teleport(ap.X + 6, ap.Y + 0.3f, ap.Z + 6);
            B.L.PadOverride(true, 0, 0, 0); Thread.Sleep(2500); B.L.PadOverride(false);
            // B's puppet of A has caught up with the teleport (else its old spot reads as a rise)
            WaitFor(() => B.L.PuppetPos(slot) is { } q0 && A.L.TryReadLocal(out var a0) && Sq(q0.x - a0.X) + Sq(q0.y - a0.Y) + Sq(q0.z - a0.Z) < 0.5, 5);
            if (A.L.TryReadLocal(out var a1)) ap = a1;
            uint h0 = B.L.Global(0x16B0);
            var seen = new List<string>(); var mine = new List<string>(); var path = new List<string>();
            float aMaxY = ap.Y, pMaxY = ap.Y, y0 = ap.Y;
            var t = new Thread(() =>
            {
                if (jump) { Hold(A.L, GcA, 0, 0, 150); Hold(A.L, 0, 0, 0, delay); }
                if (btn != 0) Hold(A.L, btn, 0, 0, 150);
                Hold(A.L, 0, 0, 0, 2000); A.L.PadOverride(false);
            }) { IsBackground = true };
            t.Start();
            for (int k = 0; k < 150; k++)
            {
                Thread.Sleep(20);
                string pn = AnimDesc(B, PuppetAnim(B, slot)), an = AnimDesc(A, LocalAnim(A));
                if (seen.Count == 0 || seen[^1] != pn) seen.Add(pn);
                if (mine.Count == 0 || mine[^1] != an) mine.Add(an);
                if (A.L.TryReadLocal(out var ay)) { aMaxY = Math.Max(aMaxY, ay.Y); if (label.StartsWith("seesaw") && k % 4 == 0) path.Add($"{ay.X:F1},{ay.Y:F1},{ay.Z:F1}"); }
                if (B.L.PuppetPos(slot) is { } pq) pMaxY = Math.Max(pMaxY, pq.y);
                if (k == 20) Shot(B, $"chars-{CN(Ch(A))}-{label}", true);
            }
            t.Join(5000);
            string N(string d) => d.Split(' ')[0];
            bool Boring(string x) => x.StartsWith("Idle") || x.StartsWith("Inactive") || x.StartsWith("Walk") || x.StartsWith("Run");
            var mineSet = mine.Select(N).ToHashSet(); var seenSet = seen.Select(N).ToHashSet();
            var missing = mineSet.Where(x => !Boring(x) && !seenSet.Contains(x)).ToList();
            var extra = seenSet.Where(x => !Boring(x) && !mineSet.Contains(x)).ToList();
            Log($"chars {scene}: {CN(Ch(A))} {label}: A's own {string.Join(" > ", mine)}; B's puppet {string.Join(" > ", seen)}; rise A {aMaxY - y0:F2} puppet {pMaxY - y0:F2}" + (path.Count > 0 ? "; A path " + string.Join(" ", path) : ""));
            bool moved = mineSet.Any(x => !Boring(x));
            bool died = mineSet.Any(x => x.StartsWith("Defeated"));
            if (died) { Result($"chars {scene}: {CN(Ch(A))} {label}", false, $"A died ({string.Join(",", mineSet)}): test spot is bad"); return (0, 0); }
            Result($"chars {scene}: {CN(Ch(A))} {label}: same move in both games", moved && missing.Count == 0 && extra.Count == 0 && Math.Abs(aMaxY - pMaxY) < 0.35,
                $"A did {string.Join(",", mineSet)}; puppet missed [{string.Join(",", missing)}], extra [{string.Join(",", extra)}]; rise A {aMaxY - y0:F2} puppet {pMaxY - y0:F2}");
            if (bInFront)
            {
                uint h1 = B.L.Global(0x16B0);
                Result($"chars {scene}: {CN(Ch(A))} {label}: pvp {(on ? "on" : "off")}", (h1 < h0) == on, $"B health {h0} -> {h1} (want hurt: {on}); PvP hits taken {B.L.DbgRender(17)}");
            }
            return (aMaxY - y0, pMaxY - y0);
        }

        float ox = spawn.X, oy = spawn.Y + 0.3f, oz = spawn.Z;
        Move("jump", 0, true, ox, oy, oz, false);
        Move("double jump", GcA, true, ox, oy, oz, false);
        if (a.ContainsKey("probepound"))
            foreach (var (l, bt, d) in new[] { ("B", GcB, 350), ("X", GcX, 350), ("Y", GcY, 100), ("Y", GcY, 600), ("B", GcB, 600) })
                Move($"pound probe {l}@{d}", bt, true, ox, oy, oz, false, d);
        Move("bounce", GcX, true, ox, oy, oz, true);   // ground pounds are X in the air (XPAD_BUTTON_O)
        if (!Switch("switch")) return;
        uint pc = Ch(A);
        Move("jump", 0, true, ox, oy, oz, false);
        Move("double jump", GcA, true, ox, oy, oz, false);
        if (pc == 1 && a.ContainsKey("probepound"))
            foreach (var (l, bt, d) in new[] { ("Y", GcY, 100), ("Y", GcY, 600), ("X", GcX, 350), ("B", GcB, 100), ("L", GcL, 350) })
                Move($"pound probe {l}@{d}", bt, true, ox, oy, oz, false, d);
        if (pc == 1) { Move("belly", GcB, false, ox, oy, oz, true); Move("flop", GcX, true, ox, oy, oz, true); }
        else { Move("chop", GcB, false, ox, oy, oz, true); Move("lasso", GcY, false, ox, oy, oz, true); }

        // Patrick's seesaw: flop onto each end in turn, the one that flings him is the right one
        var tt = objs.Where(q => Nm(sc, q.id).StartsWith("PATTEETER")).ToList();
        if (pc == 1 && tt.Count > 0)
        {
            var pl = tt.FirstOrDefault(q => q.type == 0x07);   // a PLAT, the spring itself
            if (pl.id == 0) pl = tt[0];
            Log($"chars {scene}: near the seesaw: " + string.Join(", ", objs.Where(q => (q.x != 0 || q.z != 0) && Sq(q.x - pl.x) + Sq(q.z - pl.z) < 30 * 30).Select(q => $"{Nm(sc, q.id)} {q.type:X2} {q.x:F1},{q.y:F1},{q.z:F1}")));
            Log($"chars {scene}: seesaw objects " + string.Join(", ", tt.Select(q => $"{Nm(sc, q.id)} type {q.type:X2} at {q.x:F1},{q.y:F1},{q.z:F1}")));
            // the seesaw's own axis: Patrick stands at local z +2, the throw aims at z -2 (GetPatrickTarget)
            uint pm = A.L.Mem.ReadU32(A.L.Mem.ReadU32(A.L.ObjPtr(pl.index) + 0x24) + 0x4C);
            var mm = A.L.Mem.Read(pm, 0x40);
            float M(int o) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(mm.AsSpan(o));
            float atX = M(0x20), atZ = M(0x28), cX = M(0x30), cY = M(0x34), cZ = M(0x38);
            // throwables: zThrownList (0x802F2EE8, 32 x 0x1A4, ent at +0), zThrownCount (0x803CB9F8)
            (float x, float y, float z) EntPos(Bot X, uint ent) { var q = X.L.Mem.Read(X.L.Mem.ReadU32(X.L.Mem.ReadU32(ent + 0x24) + 0x4C) + 0x30, 12); float P(int o) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(q.AsSpan(o)); return (P(0), P(4), P(8)); }
            List<uint> Throwables(Bot X) { uint n = Math.Min(X.L.Mem.ReadU32(0x803CB9F8), 32u); return Enumerable.Range(0, (int)n).Select(k => X.L.Mem.ReadU32(0x802F2EE8 + (uint)k * 0x1A4)).Where(e => e >= 0x80000000).ToList(); }
            // every object with a model near the seesaw (SceneObjects gives positions for a few types only)
            var anyNear = new List<(uint ptr, string name, byte type, float x, float y, float z)>();
            foreach (var q in objs)
            {
                uint op = A.L.ObjPtr(q.index), md = A.L.Mem.ReadU32(op + 0x24);
                if (md < 0x80000000 || md >= 0x82000000) continue;
                uint mt = A.L.Mem.ReadU32(md + 0x4C);
                if (mt < 0x80000000 || mt >= 0x82000000) continue;
                var (x, y, z) = EntPos(A, op);
                if (!float.IsFinite(x) || Sq(x - cX) + Sq(z - cZ) > 25 * 25) continue;
                anyNear.Add((op, Nm(sc, q.id), q.type, x, y, z));
            }
            Log($"chars {scene}: any objects near the seesaw: " + string.Join(", ", anyNear.Where(o => !o.name.Contains("BAMBOO") && !o.name.Contains("TIKI")).Select(o => $"{o.name} {o.type:X2} {o.x:F1},{o.y:F1},{o.z:F1}")));
            var fruits = anyNear.Where(o => o.name.StartsWith("THROWFRUIT")).OrderBy(o => Sq(o.x - cX) + Sq(o.z - cZ)).Select(o => o.ptr).Take(2).ToList();
            Log($"chars {scene}: zThrownCount {A.L.Mem.ReadU32(0x803CB9F8)}, list[0..3] {string.Join(" ", Enumerable.Range(0, 4).Select(k => A.L.Mem.ReadU32(0x802F2EE8 + (uint)k * 0x1A4).ToString("X8")))}");
            Log($"chars {scene}: seesaw at {cX:F1},{cY:F1},{cZ:F1} axis {atX:F2},{atZ:F2}; nearest throwables " + string.Join(", ", fruits.Select(e => { var p = EntPos(A, e); return $"{e:X8} {Nm(sc, A.L.Mem.ReadU32(e))} at {p.x:F1},{p.y:F1},{p.z:F1}"; })));
            foreach (var fe0 in fruits)
            {
                var hits = new List<string>();
                for (uint b0 = 0x80280000; b0 < 0x80400000; b0 += 0x10000)
                {
                    var blk = A.L.Mem.Read(b0, 0x10000);
                    for (int o = 0; o < blk.Length; o += 4) if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(blk.AsSpan(o)) == fe0) hits.Add((b0 + (uint)o).ToString("X8"));
                }
                Log($"chars {scene}: pointer {fe0:X8} found at {string.Join(" ", hits.Take(20))}");
            }
            float best = 0, bestP = 0; string where = "";
            foreach (var fe in fruits)
            {
                WaitControl(A, TimeSpan.FromSeconds(10));
                string fname = Nm(sc, A.L.Mem.ReadU32(fe));
                bool carrying = false;
                foreach (var (dx, dz, gb) in new[] { (1.0f, 0f, GcY), (-1.0f, 0f, GcY), (0f, 1.0f, GcY), (0f, -1.0f, GcY), (1.0f, 0f, GcX), (1.0f, 0f, GcB) })
                {
                    var f = EntPos(A, fe);
                    A.L.Teleport(f.x + dx, f.y + 0.4f, f.z + dz);
                    A.L.PadOverride(true, 0, 0, 0); Thread.Sleep(1000); A.L.PadOverride(false);
                    Log($"chars {scene}: seesaw: before grab controlOff {A.L.Global(0x1788):X}, talk {A.L.ActiveTalk():X}");
                    WaitControl(A, TimeSpan.FromSeconds(15));
                    A.L.Teleport(f.x + dx, f.y + 0.4f, f.z + dz);
                    A.L.PadOverride(true, 0, 0, 0); Thread.Sleep(800);
                    var seq = new List<string>();
                    Hold(A.L, gb, 0, 0, 150); A.L.PadOverride(true, 0, 0, 0);
                    for (int k = 0; k < 40; k++) { Thread.Sleep(40); string d = AnimDesc(A, LocalAnim(A)).Split(' ')[0]; if (seq.Count == 0 || seq[^1] != d) seq.Add(d); }
                    Log($"chars {scene}: seesaw: grab with {gb:X}: {string.Join(" > ", seq)}");
                    if (gb == GcY && dx > 0) { B.L.Teleport(f.x + 4, f.y + 1, f.z + 4); B.L.PadOverride(true, 0, 0, 0); Thread.Sleep(2000); B.L.PadOverride(false); Shot(B, "chars-fruitstand", true); A.L.TryReadLocal(out var a9); Log($"chars {scene}: fruit stand shot: A at {a9.X:F1},{a9.Y:F1},{a9.Z:F1}, A frames {A.L.Frames}, pad {A.L.Mem.ReadU32(0x8038C994 + 0x2C):X}, gameState {A.L.Mem.ReadU32(0x803CB8A8)}"); }
                    string an = seq.Any(x => x.StartsWith("PatPickup") || x.StartsWith("PatCarry")) ? "PatCarry" : AnimDesc(A, LocalAnim(A));
                    A.L.TryReadLocal(out var aw);
                    Log($"chars {scene}: seesaw: pick up {fname} at {f.x:F1},{f.y:F1},{f.z:F1} from {dx:F1},{dz:F1}: A at {aw.X:F1},{aw.Y:F1},{aw.Z:F1}, anim {an}");
                    if (an.StartsWith("PatCarry") || an.StartsWith("PatPickup") || an.StartsWith("Carry")) { carrying = true; break; }
                }
                if (!carrying) continue;
                foreach (float end in new[] { 2.3f, 3.0f })
                {
                    if (!AnimDesc(A, LocalAnim(A)).Contains("Carry")) break;
                    var (ra, rp) = Move($"seesaw throw {fname} at +{end:F1}", GcX, false, cX + atX * end, cY + 1.2f, cZ + atZ * end, false);
                    if (ra > best) { best = ra; bestP = rp; where = $"{fname} +{end:F1}"; }
                    if (ra > 5) break;
                }
                if (best > 5) break;
            }
            Result($"chars {scene}: Patrick seesaw fling", best > 5 && Math.Abs(best - bestP) < 1.0,
                $"best rise A {best:F1} units ({where}), B's puppet of A {bestP:F1} (want > 5, equal)");
            Compare($"chars {scene}: seesaw: world", A, B, null);
        }
        else if (pc == 1) Result($"chars {scene}: Patrick seesaw", false, "no PATTEETER objects");

        Switch("switch back");
        Result($"chars {scene}: back to SpongeBob", Ch(A) == 0 && Ch(B) == 0, $"A {Ch(A)} B {Ch(B)}");
    }

    static uint LocalAnim(Bot X)
    {
        try { var m = X.L.Mem; uint mdl = X.L.Global(GEntModel), an = m.ReadU32(mdl + 0xC), sg = m.ReadU32(an + 8); return m.ReadU32(sg + 4); } catch { return 0; }
    }

    static string AnimDesc(Bot X, uint st)
    {
        try { var m = X.L.Mem; var raw = m.Read(m.ReadU32(st + 4), 24); int z = Array.IndexOf(raw, (byte)0); return $"{System.Text.Encoding.ASCII.GetString(raw, 0, z < 0 ? 24 : z)} uf {m.ReadU32(st + 0x10):X}"; } catch { return $"{st:X8}"; }
    }

    /// <summary>Puppet's current animation state pointer (model->Anim->Single->State).</summary>
    static uint PuppetAnim(Bot X, int slot)
    {
        try { var m = X.L.Mem; uint mdl = X.L.PuppetGlobal(slot, GEntModel), an = m.ReadU32(mdl + 0xC), sg = m.ReadU32(an + 8); return m.ReadU32(sg + 4); } catch { return 0; }
    }
}
