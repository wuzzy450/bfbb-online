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
            uint h0 = B.L.Global(0x16B0), hits0 = B.L.DbgRender(17);
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
                        var fl = new List<string>();
                        var th = new Thread(() => { Hold(A.L, GcL, 0, 0, 150); Hold(A.L, 0, 0, 0, fly); Hold(A.L, GcL, 0, 0, 150); Hold(A.L, 0, 0, 0, 2000); A.L.PadOverride(false); }) { IsBackground = true };
                        th.Start();
                        for (int k = 0; k < 60 && th.IsAlive; k++) { Thread.Sleep(50); string f = A.L.Mem.ReadU32(0x8028C2A4).ToString("X"); if (fl.Count == 0 || fl[^1] != f) fl.Add(f); }
                        th.Join(5000);
                        Log($"pvp cruise: A's cruise flags over time {string.Join(" > ", fl)}; A anim {AnimDesc(A, LocalAnim(A))}");
                        Log($"pvp cruise: detonated after {fly} ms: B health {B.L.Global(0x16B0)}, hits {B.L.DbgRender(17)}; B saw a puppet missile exploding {B.L.DbgRender(15)} frames, closest blast^2 {B.L.DbgRender(9) / 100.0:F1}; A's cruise flags {A.L.Mem.ReadU32(0x8028C2A4):X}");
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
            // the patch's own PvP hit count (health alone also moves with the party's max-health sync)
            bool hurt = B.L.DbgRender(17) > hits0;
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
        uint Ch(Bot X) => CharOf(X);
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
        int retry = 0;
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
            else B.L.Teleport(spawn.X + 1.5f, spawn.Y + 0.3f, spawn.Z);   // solid ground (6 units off was goo at the seesaw)
            B.L.PadOverride(true, 0, 0, 0); Thread.Sleep(2500); B.L.PadOverride(false);
            // B's puppet of A has caught up with the teleport (else its old spot reads as a rise)
            WaitFor(() => B.L.PuppetPos(slot) is { } q0 && A.L.TryReadLocal(out var a0) && Sq(q0.x - a0.X) + Sq(q0.y - a0.Y) + Sq(q0.z - a0.Z) < 0.5, 5);
            if (A.L.TryReadLocal(out var a1)) ap = a1;
            uint h0 = B.L.Global(0x16B0), hits0 = B.L.DbgRender(17);
            var seen = new List<string>(); var mine = new List<string>(); var path = new List<string>();
            var mineSamples = new Dictionary<string, int>();
            string preA = AnimDesc(A, LocalAnim(A)).Split(' ')[0], preP = AnimDesc(B, PuppetAnim(B, slot)).Split(' ')[0];
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
                string an0 = an.Split(' ')[0];
                mineSamples[an0] = mineSamples.GetValueOrDefault(an0) + 1;
                if (A.L.TryReadLocal(out var ay)) { aMaxY = Math.Max(aMaxY, ay.Y); if (label.StartsWith("seesaw") && k % 3 == 0) path.Add($"A {ay.Y:F1} v{ay.Vy:F0} P {(B.L.PuppetPos(slot) is { } pq2 ? pq2.y.ToString("F1") : "?")}"); }
                if (B.L.PuppetPos(slot) is { } pq) pMaxY = Math.Max(pMaxY, pq.y);
                if (k == 20) Shot(B, $"chars-{CN(Ch(A))}-{label}", true);
            }
            t.Join(5000);
            string N(string d) => d.Split(' ')[0];
            bool Boring(string x) => x.StartsWith("Idle") || x.StartsWith("Inactive") || x.StartsWith("Walk") || x.StartsWith("Run");
            // what each was doing before the move started doesn't count; a state A was in for one
            // sample only (a short in-between) may be missed by the puppet
            var mineSet = mine.Select(N).Where(x => x != preA).ToHashSet(); var seenSet = seen.Select(N).Where(x => x != preP).ToHashSet();
            var missing = mineSet.Where(x => !Boring(x) && !seenSet.Contains(x) && mineSamples.GetValueOrDefault(x) > 1).ToList();
            var extra = seenSet.Where(x => !Boring(x) && !mineSet.Contains(x) && x != preA).ToList();
            Log($"chars {scene}: {CN(Ch(A))} {label}: A's own {string.Join(" > ", mine)}; B's puppet {string.Join(" > ", seen)}; rise A {aMaxY - y0:F2} puppet {pMaxY - y0:F2}" + (path.Count > 0 ? "; A path " + string.Join(" ", path) : ""));
            bool moved = mineSet.Any(x => !Boring(x));
            bool died = mineSet.Any(x => x.StartsWith("Defeated"));
            if (died) { Result($"chars {scene}: {CN(Ch(A))} {label}", false, $"A died ({string.Join(",", mineSet)}): test spot is bad"); return (0, 0); }
            // A's own game ignored the input (it happens now and then): once more
            if (!moved && !label.StartsWith("seesaw") && retry < 1) { Log($"chars {scene}: {label}: A did nothing, again"); retry++; var again = Move(label, btn, jump, px, py, pz, bInFront, delay); retry = 0; return again; }
            Result($"chars {scene}: {CN(Ch(A))} {label}: same move in both games", moved && missing.Count == 0 && extra.Count == 0 && Math.Abs(aMaxY - pMaxY) < 0.35,
                $"A did {string.Join(",", mineSet)}; puppet missed [{string.Join(",", missing)}], extra [{string.Join(",", extra)}]; rise A {aMaxY - y0:F2} puppet {pMaxY - y0:F2}");
            if (bInFront)
            {
                uint h1 = B.L.Global(0x16B0);
                Result($"chars {scene}: {CN(Ch(A))} {label}: pvp {(on ? "on" : "off")}", (B.L.DbgRender(17) > hits0) == on, $"B health {h0} -> {h1} (want hurt: {on}); PvP hits taken {B.L.DbgRender(17)}");
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
                foreach (var (dx, dz, gb) in new[] { (1.0f, 0f, GcX), (-1.0f, 0f, GcX), (0f, 1.0f, GcX), (0f, -1.0f, GcX) })
                {
                    var f = EntPos(A, fe);
                    float yaw = MathF.Atan2(-dx, -dz);   // facing the melon (at = (sin, 0, cos))
                    A.L.Teleport(f.x + dx, f.y + 0.4f, f.z + dz, yaw);
                    A.L.PadOverride(true, 0, 0, 0); Thread.Sleep(1000); A.L.PadOverride(false);
                    Log($"chars {scene}: seesaw: before grab controlOff {A.L.Global(0x1788):X}, talk {A.L.ActiveTalk():X}");
                    WaitControl(A, TimeSpan.FromSeconds(15));
                    A.L.Teleport(f.x + dx, f.y + 0.4f, f.z + dz, yaw);
                    A.L.PadOverride(true, 0, 0, 0); Thread.Sleep(800);
                    var seq = new List<string>();
                    Hold(A.L, gb, 0, 0, 150); A.L.PadOverride(true, 0, 0, 0);
                    for (int k = 0; k < 40; k++) { Thread.Sleep(40); string d = AnimDesc(A, LocalAnim(A)).Split(' ')[0]; if (seq.Count == 0 || seq[^1] != d) seq.Add(d); }
                    Log($"chars {scene}: seesaw: grab with {gb:X}: {string.Join(" > ", seq)}");
                    if (gb == GcY && dx > 0) { B.L.Teleport(f.x + 4, f.y + 1, f.z + 4); B.L.PadOverride(true, 0, 0, 0); Thread.Sleep(2000); B.L.PadOverride(false); Shot(B, "chars-fruitstand", true); A.L.TryReadLocal(out var a9); Log($"chars {scene}: fruit stand shot: A at {a9.X:F1},{a9.Y:F1},{a9.Z:F1}, A frames {A.L.Frames}, pad {A.L.Mem.ReadU32(0x8038C994 + 0x2C):X}, gameState {A.L.Mem.ReadU32(0x803CB8A8)}"); }
                    string an = seq.Any(x => x.StartsWith("Carry_Pickup") || x.StartsWith("Carry_Idle")) ? "Carry" : AnimDesc(A, LocalAnim(A));
                    A.L.TryReadLocal(out var aw);
                    Log($"chars {scene}: seesaw: pick up {fname} at {f.x:F1},{f.y:F1},{f.z:F1} from {dx:F1},{dz:F1}: A at {aw.X:F1},{aw.Y:F1},{aw.Z:F1}, anim {an}");
                    if (an.StartsWith("Carry")) { carrying = true; break; }
                }
                if (!carrying) continue;
                foreach (float end in new[] { 2.3f, 3.0f })
                {
                    if (!AnimDesc(A, LocalAnim(A)).Contains("Carry")) break;
                    // stand on the near end facing the far end (local -z), throw: the game aims it there
                    A.L.Teleport(cX + atX * end, cY + 1.2f, cZ + atZ * end, MathF.Atan2(-atX, -atZ));
                    Thread.Sleep(800);
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

    /// <summary>Our player's animation state, as the patch publishes it once a frame outside puppet swaps.</summary>
    static uint LocalAnim(Bot X) => X.L.DbgRender(18);

    /// <summary>Our player's character as the patch publishes it (gCurrentPlayer is swapped per puppet).</summary>
    static uint CharOf(Bot X) => X.L.TryReadLocal(out var st) ? st.Character : 0;

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

static partial class SelfTest
{
    /// <summary>
    /// --phases plats [--platscenes BB01,..|all]: moving platforms. A enters a scene, B follows 8 s
    /// later (its platforms start 8 s behind); after the owner's states arrive, every self-moving
    /// platform must be at the same place in both games (within 0.6 units: network delay).
    /// </summary>
    static void PlatsPhase(List<Bot> bots, Dictionary<string, string> a)
    {
        var A = bots[0];
        var B = bots[1];
        string list = a.GetValueOrDefault("platscenes", "BB01,BB02,GL01,DB01");
        if (list == "all") list = "HB01,HB00,HB02,HB03,HB04,HB05,HB06,HB08,HB09,HB10,JF01,JF02,JF03,JF04,JF05,JF06,JF07,BB01,BB02,BB03,BB04,BB05,GL01,GL02,GL03,GL04,GL05,B101,RB01,RB02,RB03,RB04,BC01,BC02,BC03,BC04,BC05,BC06,BC07,BC08,SM01,SM02,SM03,SM04,SM05,B201,KF01,KF02,KF03,KF04,KF05,KF06,GY01,GY02,GY03,GY04,GY05,GY06,GY07,DB01,DB02,DB03,DB04,DB05,DB06,DB07";
        foreach (var scene in list.Split(','))
        {
            uint sc = PlayerState.SceneIdOf(scene);
            if (!GoTo(new List<Bot> { A }, sc)) return;
            Thread.Sleep(8000);
            if (!GoTo(new List<Bot> { B }, sc)) return;
            WaitControl(B, TimeSpan.FromSeconds(10));
            uint applied0 = B.L.PlatApplied;
            Thread.Sleep(4000);
            var objs = A.L.SceneObjects();
            var moving = new List<(string name, int index)>();
            foreach (var q in objs.Where(q => q.type == 0x06))
            {
                uint p = A.L.ObjPtr(q.index);
                byte t = A.L.Mem.Read(p + 0xD8 + 4, 1)[0];
                uint flags = A.L.Mem.ReadU32(p + 0xD8 + 4) & 0xFFFF;
                if ((t == 0 || t == 1 || t == 3 || t == 5) && A.L.Mem.ReadU32(p + 0xD8) >= 0x80000000 && (flags & 4) == 0) moving.Add((Nm(sc, q.id), q.index));
            }
            if (moving.Count == 0) { Log($"plats {scene}: no self-moving platforms"); continue; }
            (float x, float y, float z) Pos(Bot X, int index) { uint p = X.L.ObjPtr(index); var m = X.L.Mem.Read(X.L.Mem.ReadU32(X.L.Mem.ReadU32(p + 0x24) + 0x4C) + 0x30, 12); float P(int o) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(m.AsSpan(o)); return (P(0), P(4), P(8)); }
            var worst = new List<(string, double)>();
            for (int s = 0; s < 5; s++)
            {
                foreach (var (name, index) in moving)
                {
                    var pa = Pos(A, index); var pb = Pos(B, index);
                    worst.Add((name, Math.Sqrt(Sq(pa.x - pb.x) + Sq(pa.y - pb.y) + Sq(pa.z - pb.z))));
                }
                Thread.Sleep(400);
            }
            var cube = objs.FirstOrDefault(q => Nm(sc, q.id).StartsWith("CUBE_MECH"));
            if (cube.id != 0)
            {   // look at a spiked cube from B (spikes ride on it)
                var cp = Pos(B, cube.index);
                B.L.Teleport(cp.x + 6, cp.y + 2, cp.z, MathF.Atan2(-6, 0));
                Thread.Sleep(2500);
                Shot(B, $"plats-{scene}-cube", true);
            }
            var byPlat = worst.GroupBy(w => w.Item1).Select(g => (name: g.Key, err: g.Min(x => x.Item2))).OrderByDescending(x => x.err).ToList();
            var bad = byPlat.Where(x => x.err > 0.6).ToList();
            Result($"plats {scene}: moving platforms in step", bad.Count == 0,
                $"{moving.Count} self-moving platforms, B took over {B.L.PlatApplied - applied0}; worst {string.Join(", ", byPlat.Take(4).Select(x => $"{x.name} {x.err:F2}"))}" + (bad.Count > 0 ? $"; {bad.Count} off by > 0.6" : ""));
        }
    }
}

static partial class SelfTest
{
    /// <summary>
    /// --phases socks [--sockscenes GL01,..]: Deluxe's flying socks. Finds sock pickups that move by
    /// themselves, checks they fly in step in A's and B's games (B arrived later), then A catches
    /// one: it must be gone in B's game too and the party's sock count go up once.
    /// </summary>
    static void SocksPhase(List<Bot> bots, Dictionary<string, string> a)
    {
        var A = bots[0];
        var B = bots[1];
        foreach (var scene in a.GetValueOrDefault("sockscenes", "GL01,HB01,JF01,BB01").Split(','))
        {
            uint sc = PlayerState.SceneIdOf(scene);
            if (!GoTo(new List<Bot> { A }, sc)) return;
            Thread.Sleep(6000);
            if (!GoTo(new List<Bot> { B }, sc)) return;
            foreach (var b in new[] { A, B }) WaitControl(b, TimeSpan.FromSeconds(10));
            var socks = A.L.SceneObjects().Where(q => q.type == 0x04 && Nm(sc, q.id) is var n && (n.StartsWith("SOCK") || n.Contains("_SOCK"))).ToList();
            (float x, float y, float z) Pos(Bot X, int index) { uint p = X.L.ObjPtr(index); var m = X.L.Mem.Read(X.L.Mem.ReadU32(X.L.Mem.ReadU32(p + 0x24) + 0x4C) + 0x30, 12); float P(int o) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(m.AsSpan(o)); return (P(0), P(4), P(8)); }
            // a flying sock may only take off when someone comes near: walk up to each one
            var flying = new List<(int index, uint id, byte type, float x, float y, float z)>();
            foreach (var q in socks)
            {
                if (A.L.ObjField(q.index, 0x16C) != 0x41) continue;
                var o = Pos(A, q.index);
                A.L.Teleport(o.x + 1.5f, o.y + 0.5f, o.z);
                A.L.PadOverride(true, 0, 0, 0);
                float moved = 0;
                for (int k = 0; k < 12; k++) { Thread.Sleep(100); var p = Pos(A, q.index); moved = MathF.Max(moved, MathF.Sqrt((float)(Sq(p.x - o.x) + Sq(p.y - o.y) + Sq(p.z - o.z)))); }
                A.L.PadOverride(false);
                Log($"socks {scene}: {Nm(sc, q.id)} with A 4 units away moved {moved:F2}");
                if (moved > 0.5f)
                {
                    flying.Add(q);
                    // what carries it: objects that move by the same amount at the same time
                    var cand = new List<(int idx, string name, byte type, uint ptr)>();
                    foreach (var o2 in A.L.SceneObjects())
                    {
                        uint op = A.L.ObjPtr(o2.index), md = A.L.Mem.ReadU32(op + 0x24);
                        if (md < 0x80000000 || md >= 0x82000000 || A.L.Mem.ReadU32(md + 0x4C) < 0x80000000) continue;
                        cand.Add((o2.index, Nm(sc, o2.id), o2.type, op));
                    }
                    (float, float, float) P2(uint op) { var m = A.L.Mem.Read(A.L.Mem.ReadU32(A.L.Mem.ReadU32(op + 0x24) + 0x4C) + 0x30, 12); float F(int k) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(m.AsSpan(k)); return (F(0), F(4), F(8)); }
                    var c0 = cand.ToDictionary(c => c.idx, c => P2(c.ptr)); var s0 = Pos(A, q.index);
                    Thread.Sleep(150);
                    var c1 = cand.ToDictionary(c => c.idx, c => P2(c.ptr)); var s1 = Pos(A, q.index);
                    float sdx = s1.x - s0.x, sdy = s1.y - s0.y, sdz = s1.z - s0.z;
                    var movers = cand.Where(c => { var (x0, y0, z0) = c0[c.idx]; var (x1, y1, z1) = c1[c.idx]; return MathF.Abs(x1 - x0) + MathF.Abs(y1 - y0) + MathF.Abs(z1 - z0) > 0.05f; })
                        .Select(c => { var (x0, y0, z0) = c0[c.idx]; var (x1, y1, z1) = c1[c.idx]; return $"{c.name} {c.type:X2} d {x1 - x0:F2},{y1 - y0:F2},{z1 - z0:F2}"; });
                    uint sp = A.L.ObjPtr(q.index);
                    Log($"socks {scene}: {Nm(sc, q.id)} moved {sdx:F2},{sdy:F2},{sdz:F2} in 150 ms; moving objects: {string.Join("; ", movers.Take(15))}; pickup bytes 0xD0..0x1A0 {Convert.ToHexString(A.L.Mem.Read(sp + 0xD0, 0xD0))}");
                }
            }
            Log($"socks {scene}: {socks.Count} socks: " + string.Join(", ", socks.Select(q => { var p = Pos(A, q.index); return $"{Nm(sc, q.id)} {p.x:F1},{p.y:F1},{p.z:F1} state {A.L.ObjField(q.index, 0x16C):X}{(flying.Contains(q) ? " FLYING" : "")}"; })));
            if (flying.Count == 0) continue;
            double worst = 0;
            for (int s = 0; s < 8; s++)
            {
                foreach (var q in flying) { var pa = Pos(A, q.index); var pb = Pos(B, q.index); worst = Math.Max(worst, Math.Sqrt(Sq(pa.x - pb.x) + Sq(pa.y - pb.y) + Sq(pa.z - pb.z))); }
                Thread.Sleep(250);
            }
            Result($"socks {scene}: flying socks in step", worst < 1.5, $"{flying.Count} flying, worst distance between A's and B's copy {worst:F2} units");
            // A catches the first one: stand where it is, over and over, until collected
            var f = flying[0];
            Thread.Sleep(3000);
            uint socks0 = A.L.Global(0x1BC4), sB0 = B.L.Global(0x1BC4);
            bool got = false;
            for (int k = 0; k < 80 && !got; k++)
            {
                var p = Pos(A, f.index);
                A.L.Teleport(p.x, p.y - 0.5f, p.z);
                Thread.Sleep(60);
                got = A.L.Global(0x1BC4) > socks0;
            }
            Thread.Sleep(3000);
            uint sA = A.L.Global(0x1BC4), sB = B.L.Global(0x1BC4);
            bool goneB = WaitFor(() => B.L.ObjField(f.index, 0x16C) == A.L.ObjField(f.index, 0x16C), 5);
            Result($"socks {scene}: catch {Nm(sc, f.id)}", got && sA == socks0 + 1 && sB == sB0 + 1 && goneB,
                $"caught {got}; socks A {socks0}->{sA}, B {sB0}->{sB}; state A {A.L.ObjField(f.index, 0x16C):X} B {B.L.ObjField(f.index, 0x16C):X}");
            Compare($"socks {scene}: world", A, B, null);
        }
    }
}

static partial class SelfTest
{
    /// <summary>
    /// --phases slides [--slidescenes SM02,..]: sliding for every character (SpongeBob TongueSlide01,
    /// Sandy TailSlide01, Patrick ButtSlide01). A starts at the scene's spawn (slide levels start on
    /// the slide) and walks forward; the owner's slide animations must be what B's puppet of A plays,
    /// close to where A is. Then, if the scene has a bus stop, as its character (switch, back to spawn).
    /// </summary>
    static void SlidesPhase(List<Bot> bots, Dictionary<string, string> a)
    {
        var A = bots[0];
        var B = bots[1];
        var pair = new List<Bot> { A, B };
        bool IsSlide(string x) => x.Contains("Slide") || x.StartsWith("Slip");
        foreach (var scene in a.GetValueOrDefault("slidescenes", "SM02,RB03").Split(','))
        {
            uint sc = PlayerState.SceneIdOf(scene);
            if (!GoTo(pair, sc)) return;
            foreach (var b in pair) WaitControl(b, TimeSpan.FromSeconds(10));
            A.L.TryReadLocal(out var spawn);
            int slot = SlotOf(B, A.Name);
            var stop = A.L.SceneObjects().FirstOrDefault(q => Nm(sc, q.id).StartsWith("BUSSTOP_BUSSTOP"));
            for (int round = 0; round < (stop.id != 0 ? 2 : 1); round++)
            {
                uint ch = CharOf(A);
                string cn = CharNames[Math.Min(ch, 2u)];
                bool slid = false;
                foreach (var (ax, ay) in new (sbyte, sbyte)[] { (0, 100), (0, -100), (100, 0), (-100, 0) })
                {
                    WaitControl(A, TimeSpan.FromSeconds(20));
                    A.L.Teleport(spawn.X, spawn.Y + 0.3f, spawn.Z);
                    B.L.Teleport(spawn.X + 3, spawn.Y + 1.5f, spawn.Z + 3);
                    A.L.PadOverride(true, 0, 0, 0); Thread.Sleep(1200);
                    var mine = new List<string>(); var seen = new List<string>(); var errs = new List<double>();
                    (float x, float y, float z) lastA = (0, 0, 0);
                    A.L.PadOverride(true, 0, ax, ay);
                    for (int k = 0; k < 200; k++)
                    {
                        Thread.Sleep(40);
                        string an = AnimDesc(A, LocalAnim(A)).Split(' ')[0], pn = AnimDesc(B, PuppetAnim(B, slot)).Split(' ')[0];
                        if (mine.Count == 0 || mine[^1] != an) mine.Add(an);
                        if (seen.Count == 0 || seen[^1] != pn) seen.Add(pn);
                        if (IsSlide(an) && A.L.TryReadLocal(out var ap) && B.L.PuppetPos(slot) is { } pp
                            && (Math.Abs(ap.X - lastA.x) + Math.Abs(ap.Y - lastA.y) + Math.Abs(ap.Z - lastA.z) > 0.001 || Math.Abs(ap.Vx) + Math.Abs(ap.Vy) + Math.Abs(ap.Vz) < 0.01))
                        {   // (a position that stopped updating while moving: A fell out of the world / died)
                            lastA = (ap.X, ap.Y, ap.Z);
                            errs.Add(Math.Sqrt(Sq(ap.X - pp.x) + Sq(ap.Y - pp.y) + Sq(ap.Z - pp.z)));
                            if (a.ContainsKey("slidetrace") && k % 3 == 0) Log($"slide trace {cn} {an}/{pn}: A {ap.X:F1},{ap.Y:F1},{ap.Z:F1} v {ap.Vx:F1},{ap.Vy:F1},{ap.Vz:F1}; puppet {pp.x:F1},{pp.y:F1},{pp.z:F1}; err {errs[^1]:F2}");
                        }
                        if (errs.Count == 15) Shot(B, $"slide-{scene}-{cn}", true);
                        if (an.Contains("Defeated")) break;
                    }
                    A.L.PadOverride(false);
                    var slides = mine.Where(IsSlide).ToHashSet();
                    Log($"slides {scene} {cn} stick {ax},{ay}: A {string.Join(" > ", mine.Take(30))}; B's puppet {string.Join(" > ", seen.Take(30))}");
                    if (slides.Count == 0) continue;
                    slid = true;
                    var pslides = seen.Where(IsSlide).ToHashSet();
                    double med = errs.Count > 0 ? errs.OrderBy(x => x).ElementAt(errs.Count / 2) : 0;
                    Result($"slides {scene}: {cn} slide seen by B", slides.SetEquals(pslides) && errs.Count > 0 && med < 1.0,
                        $"A slid with {string.Join(",", slides)}; B's puppet played {string.Join(",", pslides)}; position error while sliding median {med:F2} max {(errs.Count > 0 ? errs.Max() : 0):F2} ({errs.Count} samples)");
                    break;
                }
                if (!slid) Log($"slides {scene} {cn}: no slide found from the spawn");
                if (stop.id == 0) break;
                // switch character at the stop (round 0), back (round 1)
                uint sp = A.L.ObjPtr(stop.index);
                float SF(uint addr) => BitConverter.Int32BitsToSingle((int)A.L.Mem.ReadU32(addr));
                WaitControl(A, TimeSpan.FromSeconds(20));
                A.L.Teleport(SF(sp + 0x18) - 0.6f, SF(sp + 0x1C) + 0.2f, SF(sp + 0x20)); A.L.PadOverride(true, 0, 0, 0); Thread.Sleep(2000);
                Hold(A.L, GcR, 0, 0, 150); A.L.PadOverride(true, 0, 0, 0);
                WaitFor(() => CharOf(A) != ch, 10);
                A.L.PadOverride(false);
                WaitControl(A, TimeSpan.FromSeconds(15));
                Log($"slides {scene}: switched {cn} -> {CharNames[Math.Min(CharOf(A), 2u)]}");
            }
        }
    }
}


static partial class SelfTest
{
    /// <summary>--phases linkdump [--dumpscene GL01] [--dumpmatch CANNON]: every matching object's event links (diagnostics).</summary>
    static void LinkDumpPhase(List<Bot> bots, Dictionary<string, string> a)
    {
        var A = bots[0];
        uint sc = PlayerState.SceneIdOf(a.GetValueOrDefault("dumpscene", "GL01"));
        if (!GoTo(new List<Bot> { A }, sc)) return;
        string match = a.GetValueOrDefault("dumpmatch", "CANNON");
        foreach (var q in A.L.SceneObjects().Where(q => Nm(sc, q.id).Contains(match)))
        {
            uint p = A.L.ObjPtr(q.index);
            int n = A.L.Mem.Read(p + 5, 1)[0];
            uint links = A.L.Mem.ReadU32(p + 8);
            var sb = new List<string>();
            for (int k = 0; k < n && links >= 0x80000000; k++)
            {
                var l = A.L.Mem.Read(links + (uint)k * 0x20, 0x20);
                ushort src = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(l), dst = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(l.AsSpan(2));
                uint to = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(l.AsSpan(4));
                float p0 = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(l.AsSpan(8));
                sb.Add($"on {src} -> {Nm(sc, to)} ev {dst} ({p0:0.##})");
            }
            Log($"links {Nm(sc, q.id)} type {q.type:X2} flags {A.L.ObjField(q.index, 4) & 0xFFFF:X}: {string.Join("; ", sb)}");
        }
    }
}

static partial class SelfTest
{
    /// <summary>
    /// --phases lasso [--lassoscene BB02]: Sandy's lasso swing. A becomes Sandy at the scene's bus stop,
    /// jumps up next to a swing point (SWING_WINDOW_*) and lassos it; B's puppet of A must play the same
    /// swing animations and stay close. Tries a few inputs (lasso is a button in the air near a point).
    /// </summary>
    static void LassoPhase(List<Bot> bots, Dictionary<string, string> a)
    {
        var A = bots[0];
        var B = bots[1];
        var pair = new List<Bot> { A, B };
        string scene = a.GetValueOrDefault("lassoscene", "BB02");
        uint sc = PlayerState.SceneIdOf(scene);
        if (!GoTo(pair, sc)) return;
        foreach (var b in pair) WaitControl(b, TimeSpan.FromSeconds(10));
        var objs = A.L.SceneObjects();
        var stop = objs.FirstOrDefault(q => Nm(sc, q.id).StartsWith("BUSSTOP_BUSSTOP"));
        if (stop.id == 0) { Result($"lasso {scene}", false, "no bus stop"); return; }
        uint sp = A.L.ObjPtr(stop.index);
        float SF(uint addr) => BitConverter.Int32BitsToSingle((int)A.L.Mem.ReadU32(addr));
        for (int t = 0; t < 2 && CharOf(A) != 2; t++)
        {
            WaitControl(A, TimeSpan.FromSeconds(15));
            A.L.Teleport(SF(sp + 0x18) - 0.6f, SF(sp + 0x1C) + 0.2f, SF(sp + 0x20)); A.L.PadOverride(true, 0, 0, 0); Thread.Sleep(2000);
            Hold(A.L, GcR, 0, 0, 150); A.L.PadOverride(true, 0, 0, 0);
            WaitFor(() => CharOf(A) == 2, 10);
            A.L.PadOverride(false);
        }
        if (CharOf(A) != 2) { Result($"lasso {scene}: become Sandy", false, $"character {CharOf(A)}"); return; }
        WaitControl(A, TimeSpan.FromSeconds(15));
        int slot = SlotOf(B, A.Name);
        bool Swingy(string x) => x.Contains("Swing") || x.Contains("Lasso");
        bool done = false;
        foreach (var w in objs.Where(q => Nm(sc, q.id).StartsWith("SWING_WINDOW")).Take(4))
        {
            foreach (var (dx, dy, btn) in new[] { (3f, -3f, GcX), (5f, -3f, GcX), (4f, -1.5f, GcX), (6f, -4f, GcX), (3f, -3f, GcY) })
            {
                WaitControl(A, TimeSpan.FromSeconds(15));
                var wp = (w.x, w.y, w.z);
                { uint p = A.L.ObjPtr(w.index); var m = A.L.Mem.Read(A.L.Mem.ReadU32(A.L.Mem.ReadU32(p + 0x24) + 0x4C) + 0x30, 12); wp = (System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(m), System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(m.AsSpan(4)), System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(m.AsSpan(8))); }
                A.L.Teleport(wp.Item1 + dx, wp.Item2 + dy, wp.Item3, MathF.Atan2(-dx, 0f));   // facing the hook
                B.L.Teleport(wp.Item1 + dx + 5, wp.Item2 + dy + 1, wp.Item3 + 5);
                Thread.Sleep(300);
                var mine = new List<string>(); var seen = new List<string>(); var errs = new List<double>();
                var th = new Thread(() => { Hold(A.L, GcA, 0, 0, 150); Hold(A.L, 0, 0, 0, 200); Hold(A.L, btn, 0, 0, 200); Hold(A.L, 0, 0, 0, 2500); A.L.PadOverride(false); }) { IsBackground = true };
                th.Start();
                for (int k = 0; k < 75; k++)
                {
                    Thread.Sleep(40);
                    string an = AnimDesc(A, LocalAnim(A)).Split(' ')[0], pn = AnimDesc(B, PuppetAnim(B, slot)).Split(' ')[0];
                    if (mine.Count == 0 || mine[^1] != an) mine.Add(an);
                    if (seen.Count == 0 || seen[^1] != pn) seen.Add(pn);
                    if (Swingy(an) && A.L.TryReadLocal(out var ap) && B.L.PuppetPos(slot) is { } pp) errs.Add(Math.Sqrt(Sq(ap.X - pp.x) + Sq(ap.Y - pp.y) + Sq(ap.Z - pp.z)));
                    if (errs.Count == 8) Shot(B, $"lasso-{scene}", true);
                }
                th.Join(5000);
                Log($"lasso {scene} {Nm(sc, w.id)} at {dx},{dy} button {btn:X}: A {string.Join(" > ", mine)}; B's puppet {string.Join(" > ", seen)}");
                var sw = mine.Where(Swingy).ToHashSet();
                if (sw.Count == 0) continue;
                var psw = seen.Where(Swingy).ToHashSet();
                double med = errs.Count > 0 ? errs.OrderBy(x => x).ElementAt(errs.Count / 2) : 0;
                Result($"lasso {scene}: Sandy swing seen by B", sw.SetEquals(psw) && med < 1.5,
                    $"A swung with {string.Join(",", sw)}; B's puppet played {string.Join(",", psw)}; position error while swinging median {med:F2} max {(errs.Count > 0 ? errs.Max() : 0):F2}");
                done = true;
                break;
            }
            if (done) break;
        }
        if (!done) Result($"lasso {scene}: Sandy swing", false, "no input made A swing (see log)");
    }
}

static partial class SelfTest
{
    /// <summary>
    /// --phases cannons [--cannonscene GL01]: cannons that start when a player walks into their zone
    /// (hazard.c). A walks in: B's copy of the cannon must start too (its group timer runs in B's game);
    /// A walks out: it stops. B inside while A leaves: it keeps running.
    /// </summary>
    static void CannonsPhase(List<Bot> bots, Dictionary<string, string> a)
    {
        var A = bots[0];
        var B = bots[1];
        var pair = new List<Bot> { A, B };
        string scene = a.GetValueOrDefault("cannonscene", "GL01");
        uint sc = PlayerState.SceneIdOf(scene);
        if (!GoTo(pair, sc)) return;
        foreach (var b in pair) WaitControl(b, TimeSpan.FromSeconds(10));
        Thread.Sleep(1000);
        Result($"cannons {scene}: zones found", A.L.HzCount > 0 && A.L.HzCount == B.L.HzCount, $"hazard zones A {A.L.HzCount} B {B.L.HzCount}");
        if (A.L.HzCount == 0) return;
        var objs = A.L.SceneObjects();
        A.L.TryReadLocal(out var spawn);
        foreach (int c in new[] { 1, 2 })
        {
            var timer = objs.FirstOrDefault(q => Nm(sc, q.id) == $"CANNON_{c}_GROUP_TIMER");
            if (timer.id == 0) continue;
            byte TState(Bot X) => X.L.Mem.Read(X.L.ObjPtr(timer.index) + 0x14, 1)[0];
            // candidate spots: the cannon's target markers and the cannon itself
            var spots = new List<(float x, float y, float z)>();
            foreach (var q in objs.Where(q => Nm(sc, q.id).StartsWith($"CANNON_{c}_MP_")))
            {
                uint pp = A.L.Mem.ReadU32(A.L.ObjPtr(q.index) + 0x14);
                if (pp < 0x80000000) continue;
                var m = A.L.Mem.Read(pp, 12);
                spots.Add((System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(m), System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(m.AsSpan(4)), System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(m.AsSpan(8))));
            }
            var dest = objs.FirstOrDefault(q => Nm(sc, q.id) == $"CANNON_DEST_{c}");
            if (dest.id != 0) { spots.Add((dest.x + 3, dest.y + 1, dest.z)); spots.Add((dest.x - 3, dest.y + 1, dest.z)); spots.Add((dest.x, dest.y + 1, dest.z + 3)); spots.Add((dest.x, dest.y + 1, dest.z - 3)); }
            B.L.Teleport(spawn.X, spawn.Y + 0.5f, spawn.Z);
            bool inZone = false;
            foreach (var sp in spots)
            {
                WaitControl(A, TimeSpan.FromSeconds(10));
                A.L.Teleport(sp.x, sp.y + 1, sp.z);
                if (WaitFor(() => TState(A) != 0, 2.5)) { inZone = true; Log($"cannons {scene}: cannon {c} zone entered at {sp.x:F1},{sp.y:F1},{sp.z:F1}"); break; }
            }
            if (!inZone) { Log($"cannons {scene}: cannon {c}: A never started it (spots {spots.Count})"); continue; }
            bool bRuns = WaitFor(() => TState(B) == TState(A) && TState(B) != 0, 5);
            Shot(B, $"cannon-{scene}-{c}", true);
            Result($"cannons {scene}: cannon {c} starts for B when A walks in", bRuns, $"group timer state A {TState(A)} B {TState(B)}; B {(B.L.TryReadLocal(out var bp) ? $"{bp.X:F0},{bp.Z:F0}" : "?")} far away; zone events applied in B {B.L.HzApplied}");
            // B walks in too, A leaves: it keeps running for both
            B.L.TryReadLocal(out _);
            A.L.TryReadLocal(out var ain);
            B.L.Teleport(ain.X + 0.5f, ain.Y + 0.5f, ain.Z);
            Thread.Sleep(2000);
            A.L.Teleport(spawn.X, spawn.Y + 0.5f, spawn.Z);
            Thread.Sleep(3000);
            Result($"cannons {scene}: cannon {c} keeps going while B is still in", TState(A) != 0 && TState(B) != 0, $"group timer state A {TState(A)} B {TState(B)}");
            // B leaves too: stops everywhere
            B.L.Teleport(spawn.X + 2, spawn.Y + 0.5f, spawn.Z);
            bool stopped = WaitFor(() => TState(A) == 0 && TState(B) == 0, 6);
            Result($"cannons {scene}: cannon {c} stops when everyone left", stopped, $"group timer state A {TState(A)} B {TState(B)}");
        }
    }
}
