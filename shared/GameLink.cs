using System.Linq;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;

namespace BfbbOnline.Shared;

/// <summary>
/// High-level access to one running patched game: the mailbox (local player out, puppets in,
/// pad override, warps, scene-load handshake) and the game's save serializer (the world state).
/// </summary>
public sealed class GameLink : IDisposable
{
    public const uint MailboxAddr = 0x803D8A80;
    const uint XSerData = 0x80288B10;        // st_XSERIAL_DATA_PRIV g_xserdata
    const int MaxSerialClients = 0x80;       // xSerialStartup(0x80, ...)

    public DolphinMemory Mem { get; }
    readonly uint mb = MailboxAddr;
    readonly uint[] remoteSeq = new uint[Mb.MaxRemote];

    GameLink(DolphinMemory mem) { Mem = mem; }

    /// <summary>Waits until the patched game is loaded in this Dolphin process.</summary>
    public static GameLink? Attach(int pid, TimeSpan timeout, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + timeout;
        DolphinMemory? mem = null;
        Span<byte> b = stackalloc byte[4];
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            mem ??= DolphinMemory.TryAttach(pid);
            if (mem != null)
            {
                if (mem.TryRead(MailboxAddr, b) && BinaryPrimitives.ReadUInt32BigEndian(b) == Mb.Magic)
                    return new GameLink(mem);
                if (!mem.IsAlive) return null;
            }
            Thread.Sleep(15);
        }
        mem?.Dispose();
        return null;
    }

    public bool Alive => Mem.IsAlive;

    uint U(uint off) => Mem.ReadU32(mb + off);
    void W(uint off, uint v) => Mem.WriteU32(mb + off, v);

    /// <summary>Tells the patch a bridge is attached. Re-run if the game reloaded (Bridged false).</summary>
    public void Hello(uint mode, uint bootScene = 0, uint loadWaitFrames = 0)
    {
        W(Mb.OMode, mode);
        W(Mb.OBootScene, bootScene);
        W(Mb.OLoadWait, loadWaitFrames);
        W(Mb.OBridge, Mb.BridgeMagic);
    }

    public bool Bridged => U(Mb.OBridge) == Mb.BridgeMagic;
    public void SetFlag(uint bit, bool on) => W(Mb.OFlags, on ? U(Mb.OFlags) | bit : U(Mb.OFlags) & ~bit);
    public uint Frames => U(Mb.OFrames);
    public uint CtxReady => U(Mb.OCtxReady);
    public bool HighRam => U(Mb.OHighRam) != 0;
    public uint HeapGap => U(Mb.OHeapGap);

    /// <summary>The OS heap the game's RenderWare and sound buffers come from: free bytes and largest free block.</summary>
    public (uint free, uint largest) OsHeapFree()
    {
        uint arr = Mem.ReadU32(0x803CC548), cur = Mem.ReadU32(0x803CAF40);
        if (arr < 0x80000000 || cur > 8) return (0, 0);
        uint cell = Mem.ReadU32(arr + 0xC * cur + 4), total = 0, big = 0;
        for (int i = 0; i < 4096 && cell >= 0x80000000 && cell < 0x84000000; i++)
        {
            uint size = Mem.ReadU32(cell + 8);
            total += size; big = Math.Max(big, size);
            cell = Mem.ReadU32(cell + 4);
        }
        return (total, big);
    }
    /// <summary>Free heap bytes before the puppets were built, then after each one.</summary>
    public uint[] HeapFree => Enumerable.Range(0, Mb.MaxRemote + 1).Select(i => U(Mb.OHeapFree + 4 * (uint)i)).ToArray();

    public bool TryReadLocal(out PlayerState s)
    {
        Span<byte> b = stackalloc byte[Mb.LocalSize];
        s = default;
        for (int tries = 0; tries < 3; tries++)
        {
            if (!Mem.TryRead(mb + Mb.OLocal, b)) return false;
            if (PlayerState.TryParseLocal(b, out s)) return true;
        }
        return false;
    }

    public void SetRemote(int slot, in PlayerState s, uint flags, uint color, string name)
    {
        Span<byte> b = stackalloc byte[Mb.RemoteSize];
        s.WriteRemote(b, ++remoteSeq[slot], flags, color, name);
        Mem.TryWrite(mb + Mb.ORemote + (uint)(slot * Mb.RemoteSize), b);
    }

    public void ClearRemote(int slot) => W(Mb.ORemote + (uint)(slot * Mb.RemoteSize), 0);

    public void PadOverride(bool on, uint buttons = 0, sbyte ax = 0, sbyte ay = 0)
    {
        W(Mb.OPadOn, buttons);
        Mem.TryWrite(mb + Mb.OPadAnalog, new byte[] { (byte)ax, (byte)ay, 0, 0 });
        W(Mb.OPadOverride, on ? 1u : 0u);
    }

    /// <summary>Goes to a scene; <paramref name="reload"/> also reloads the scene we are in.</summary>
    public void Warp(uint sceneId, bool reload = false, bool anytime = false)
    {
        uint f = U(Mb.OFlags) & ~3u;
        if (reload) f |= 1u;
        if (anytime) f |= 2u;
        W(Mb.OFlags, f);
        W(Mb.OWarpScene, sceneId);
        W(Mb.OWarpSeq, U(Mb.OWarpSeq) + 1);
    }

    // ---- scene-load handshake ----------------------------------------------------------
    public uint LoadSeq => U(Mb.OLoadSeq);
    public uint LoadScene => U(Mb.OLoadScene);
    /// <summary>True while the game waits in the handshake for us.</summary>
    public bool LoadPending => U(Mb.OLoadSeq) != U(Mb.OLoadAck);
    public void AckLoad(uint seq) => W(Mb.OLoadAck, seq);

    // ---- the save serializer (world state) ---------------------------------------------
    public record struct SerialClient(uint Tag, uint MemBuf, int Size);

    public List<SerialClient> ReadSerialClients()
    {
        var hdr = Mem.Read(XSerData, 0x14);
        uint cltbuf = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x0C));
        uint cltnext = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x10));
        var list = new List<SerialClient>();
        if (cltbuf == 0 || cltnext < cltbuf) return list;
        int n = (int)Math.Min((cltnext - cltbuf) / 16, MaxSerialClients);
        var t = Mem.Read(cltbuf, n * 16);
        for (int i = 0; i < n; i++)
        {
            var e = t.AsSpan(i * 16);
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(e);
            if (tag == 0) continue;
            list.Add(new SerialClient(tag, BinaryPrimitives.ReadUInt32BigEndian(e.Slice(4)),
                (int)BinaryPrimitives.ReadUInt32BigEndian(e.Slice(12))));
        }
        return list;
    }

    /// <summary>Every serializer slot (PLYR, CNTR, one per visited scene) as tag → bytes.</summary>
    public Dictionary<uint, byte[]> ExportWorld()
    {
        var d = new Dictionary<uint, byte[]>();
        foreach (var c in ReadSerialClients())
            if (c.Tag != Mb.ScratchTag) d[c.Tag] = Mem.Read(c.MemBuf, c.Size);
        return d;
    }

    /// <summary>
    /// Writes slots into the serializer, asking the patch to create missing ones first.
    /// The patch services that request every frame and inside the load handshake.
    /// </summary>
    public bool ImportWorld(IReadOnlyDictionary<uint, byte[]> world, TimeSpan timeout)
    {
        var have = new Dictionary<uint, SerialClient>();
        foreach (var c in ReadSerialClients()) have[c.Tag] = c;
        var missing = new List<uint>();
        foreach (var tag in world.Keys) if (!have.ContainsKey(tag)) missing.Add(tag);
        if (missing.Count > 0)
        {
            if (missing.Count > 32) missing.RemoveRange(32, missing.Count - 32);
            for (int i = 0; i < missing.Count; i++) W(Mb.OEnsureTags + (uint)(4 * i), missing[i]);
            W(Mb.OEnsureCount, (uint)missing.Count);
            uint seq = U(Mb.OEnsureSeq) + 1;
            W(Mb.OEnsureSeq, seq);
            var until = DateTime.UtcNow + timeout;
            while (U(Mb.OEnsureAck) != seq)
            {
                if (DateTime.UtcNow > until || !Alive) return false;
                Thread.Sleep(5);
            }
            have.Clear();
            foreach (var c in ReadSerialClients()) have[c.Tag] = c;
        }
        foreach (var (tag, bytes) in world)
        {
            if (!have.TryGetValue(tag, out var c)) continue;
            int n = Math.Min(bytes.Length, c.Size);
            Mem.TryWrite(c.MemBuf, bytes.AsSpan(0, n));
        }
        return true;
    }

    // ---- live object sync ----------------------------------------------------------------
    public bool ObjPrimed => U(Mb.OObjPrimed) != 0;
    public uint ObjScene => U(Mb.OObjScene);
    public uint ResetSeq => U(Mb.OResetSeq);
    public (uint dropped, uint applied, uint count) ObjStats => (U(Mb.OObjDropped), U(Mb.OObjApplied), U(Mb.OObjCount));

    /// <summary>Takes the object states the game reported since the last call.</summary>
    public List<ObjState> TakeObjChanges()
    {
        var list = new List<ObjState>();
        uint head = U(Mb.OOutHead), tail = U(Mb.OOutTail);
        if (head - tail > Mb.ObjRing) tail = head - Mb.ObjRing;
        for (; tail != head; tail++)
        {
            var r = Mem.Read(mb + Mb.OOutRing + (uint)(tail % Mb.ObjRing * Mb.ObjRecSize), Mb.ObjRecSize);
            if (Rec(r) is { } o) list.Add(o);
        }
        W(Mb.OOutTail, tail);
        return list;
    }

    static ObjState? Rec(byte[] r)
    {
        ushort bits = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(0x0A));
        if (bits == 0 || bits > Mb.ObjData * 8) return null;
        return new ObjState(BinaryPrimitives.ReadUInt32BigEndian(r), BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(4)),
            BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(8)), r[0x0C], bits, r.AsSpan(0x10, (bits + 31) / 32 * 4).ToArray())
            { Kind = (ushort)(r[0x0D] << 8 | r[0x0E]), CountOnly = r[0x0F] != 0 };
    }

    /// <summary>Queues states for the game to apply; returns how many fit (the rest: try again later).</summary>
    public int PushObjStates(IReadOnlyList<ObjState> states)
    {
        uint head = U(Mb.OInHead), tail = U(Mb.OInTail);
        int n = 0;
        var r = new byte[Mb.ObjRecSize];
        foreach (var o in states)
        {
            if (head - tail >= Mb.ObjRing) break;
            Array.Clear(r);
            BinaryPrimitives.WriteUInt32BigEndian(r, o.Scene);
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), o.Id);
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(8), o.Index);
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(0x0A), o.Bits);
            r[0x0C] = o.Type;
            o.Data.AsSpan(0, Math.Min(o.Data.Length, Mb.ObjData)).CopyTo(r.AsSpan(0x10));
            Mem.TryWrite(mb + Mb.OInRing + (uint)(head % Mb.ObjRing * Mb.ObjRecSize), r);
            head++;
            n++;
        }
        if (n > 0) W(Mb.OInHead, head);
        return n;
    }

    // ---- story cutscenes -------------------------------------------------------------------
    public uint CutsceneSeq => U(Mb.OCsSeq);
    public (uint id, uint scene) LastCutscene => (U(Mb.OCsId), U(Mb.OCsScene));

    /// <summary>Starts the cutscene manager <paramref name="id"/> (a teammate's cinematic).</summary>
    public void PlayCutscene(uint id)
    {
        W(Mb.OCsPlayId, id);
        W(Mb.OCsPlaySeq, U(Mb.OCsPlaySeq) + 1);
    }

    // ---- conversations ---------------------------------------------------------------------
    public uint TalkSeq => U(Mb.OTalkSeq);
    public (uint id, uint scene) LastTalk => (U(Mb.OTalkId), U(Mb.OTalkScene));
    /// <summary>The talk box running now (0 = none).</summary>
    public uint ActiveTalk() { uint a = Mem.ReadU32(0x8031B328); return a >= 0x80000000 ? Mem.ReadU32(a) : 0; }

    /// <summary>Shows the watch-along prompt in a teammate's talk box (<see cref="OfferResult"/> says how it went).</summary>
    public void OfferTalk(uint talkBox, string text)
    {
        var b = new byte[64];
        var t = System.Text.Encoding.ASCII.GetBytes(text);
        t.AsSpan(0, Math.Min(63, t.Length)).CopyTo(b);
        Mem.TryWrite(mb + Mb.OOfferText, b);
        W(Mb.OOfferId, talkBox);
        W(Mb.OOfferResult, 0);
        W(Mb.OOfferSeq, U(Mb.OOfferSeq) + 1);
    }

    /// <summary>0 showing/none, 1 accepted, 2 declined, 3 not shown (busy).</summary>
    public uint OfferResult => U(Mb.OOfferResult);
    public bool OfferHandled => U(Mb.OOfferSeq) == U(Mb.OOfferAck);

    // ---- live inventory --------------------------------------------------------------------
    static readonly (uint addr, int bit)[] counters = PlyrSlot.LiveCounters();

    /// <summary>The counters as the patch last published them (consistent, between frames).</summary>
    public uint[] ReadCounters()
    {
        var b = Mem.Read(mb + Mb.OInvCount, counters.Length * 4);
        var r = new uint[counters.Length];
        for (int i = 0; i < r.Length; i++) r[i] = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4 * i));
        return r;
    }

    public bool CountersApplied => U(Mb.OInvSeq) == U(Mb.OInvAck);

    /// <summary>Hands the game counter changes to add; false while the last batch isn't applied yet.</summary>
    public bool TryAddCounters(IReadOnlyList<(int i, int d)> deltas)
    {
        if (U(Mb.OInvSeq) != U(Mb.OInvAck)) return false;
        var b = new byte[counters.Length * 4];
        foreach (var (i, d) in deltas)
            if (i >= 0 && i < counters.Length)
                BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4 * i), BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(4 * i)) + d);
        Mem.TryWrite(mb + Mb.OInvAdd, b);
        W(Mb.OInvSeq, U(Mb.OInvSeq) + 1);
        return true;
    }

    /// <summary>Hands the game counter values to set (absolute); false while the last batch isn't applied yet.</summary>
    public bool TrySetCounters(IReadOnlyList<(int i, uint v)> values)
    {
        if (U(Mb.OInvSeq) != U(Mb.OInvAck)) return false;
        var b = new byte[counters.Length * 4];
        uint lo = 0, hi = 0;
        foreach (var (i, v) in values)
            if (i >= 0 && i < counters.Length)
            {
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4 * i), v);
                if (i < 32) lo |= 1u << i; else hi |= 1u << (i - 32);
            }
        Mem.TryWrite(mb + Mb.OInvAdd, b);
        W(Mb.OInvSetMask, lo);
        W(Mb.OInvSetMask + 4, hi);
        W(Mb.OInvSeq, U(Mb.OInvSeq) + 1);
        return true;
    }

    // ---- test / debug -------------------------------------------------------------------
    /// <summary>The patch's object fingerprints by scene base index (0 = untracked).</summary>
    public uint[] ReadObjHashes(int count)
    {
        uint addr = U(Mb.ODbgHashes);
        var r = new uint[count];
        if (addr == 0) return r;
        var b = Mem.Read(addr, count * 4);
        for (int i = 0; i < count; i++) r[i] = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4 * i));
        return r;
    }

    /// <summary>A u32 field of scene object <paramref name="index"/>.</summary>
    public uint ObjField(int index, uint off)
    {
        uint sc = Mem.ReadU32(0x803C0558 + 0x1FC0);
        uint b = Mem.ReadU32(Mem.ReadU32(sc + 0x78) + (uint)index * 4);
        return b >= 0x80000000 ? Mem.ReadU32(b + off) : 0;
    }

    /// <summary>Scene object <paramref name="index"/>'s address (0 = none).</summary>
    public uint ObjPtr(int index)
    {
        uint sc = Mem.ReadU32(0x803C0558 + 0x1FC0);
        if (sc < 0x80000000) return 0;
        uint b = Mem.ReadU32(Mem.ReadU32(sc + 0x78) + (uint)index * 4);
        return b >= 0x80000000 ? b : 0;
    }

    /// <summary>A u32 of the game's globals (zGlobals at 0x803C0558).</summary>
    public uint Global(uint off) => Mem.ReadU32(0x803C0558 + off);
    public void SetGlobal(uint off, uint v) => Mem.WriteU32(0x803C0558 + off, v);

    /// <summary>A pickup's table entry: type (0 shiny, 1 item), index, entity pickupFlags, state.</summary>
    public (byte type, byte index, ushort flags, uint state, uint quantity) PickupInfo(int index)
    {
        uint b = ObjPtr(index);
        if (b == 0) return default;
        uint p = Mem.ReadU32(b + 0x170);
        if (p < 0x80000000) return default;
        var a = Mem.Read(p, 12);
        return (a[4], a[5], (ushort)(Mem.ReadU32(b + 0x264) >> 16), Mem.ReadU32(b + 0x16C), BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(8)));
    }

    /// <summary>An object's event links (xLinkAsset): source event, event sent, target id.</summary>
    public List<(ushort src, ushort dst, uint target)> Links(int index)
    {
        var r = new List<(ushort, ushort, uint)>();
        uint b = ObjPtr(index);
        if (b == 0) return r;
        int n = Mem.Read(b + 5, 1)[0];
        uint l = Mem.ReadU32(b + 8);
        if (n == 0 || l < 0x80000000) return r;
        var d = Mem.Read(l, n * 0x20);
        for (int i = 0; i < n; i++)
            r.Add((BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i * 0x20)), BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i * 0x20 + 2)),
                BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i * 0x20 + 4))));
        return r;
    }

    /// <summary>The cutscene asset a cutscene manager plays.</summary>
    public uint CutsceneAssetOf(int index)
    {
        uint b = ObjPtr(index);
        uint t = b == 0 ? 0 : Mem.ReadU32(b + 0x10);
        return t >= 0x80000000 ? Mem.ReadU32(t + 8) : 0;
    }

    /// <summary>The id of the cutscene manager playing now (0 = none).</summary>
    public uint PlayingCutscene()
    {
        uint cm = Global(0x1FBC);
        return cm >= 0x80000000 ? Mem.ReadU32(cm) : 0;
    }

    /// <summary>zCutSceneNamesTable: the 14 story cinematics' "played" flags (per player).</summary>
    public bool[] CinematicsPlayed()
    {
        var b = Mem.Read(0x8028BD08, 14 * 72);
        var r = new bool[14];
        for (int i = 0; i < 14; i++) r[i] = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i * 72 + 64)) != 0;
        return r;
    }

    /// <summary>Test hook: sends event <paramref name="evt"/> to scene object <paramref name="id"/>.</summary>
    public void SendEvent(uint id, uint evt)
    {
        W(Mb.OEvtId, id);
        W(Mb.OEvtCode, evt);
        W(Mb.OEvtSeq, U(Mb.OEvtSeq) + 1);
    }

    public bool EventDone => U(Mb.OEvtSeq) == U(Mb.OEvtAck);

    public static uint StrHash(string s)
    {
        uint h = 0;
        foreach (char ch in s)
        {
            uint i = ch;
            h = ((i - (i & (i >> 1) & 0x20)) & 0xFF) + h * 0x83;
        }
        return h;
    }

    /// <summary>Turns enemy movement sync on (our party id + the party id in each puppet slot; 0 = off).</summary>
    public void SetNpcRanks(uint me, uint[] slots)
    {
        W(Mb.ONpcRank, me);
        for (int i = 0; i < Mb.MaxRemote; i++) W(Mb.ONpcRemoteRank + (uint)(4 * i), i < slots.Length ? slots[i] : 0);
    }

    public uint NpcOutSeq => U(Mb.ONpcOutSeq);
    public uint NpcApplied => U(Mb.ONpcApplied);

    /// <summary>The poses of the NPCs our game drives: scene + raw 32-byte NpcPose records.</summary>
    public (uint scene, byte[][] poses) ReadNpcOut()
    {
        uint scene = U(Mb.ONpcOutScene);
        int n = (int)Math.Min(U(Mb.ONpcOutCount), (uint)Mb.NpcMax);
        var raw = n == 0 ? Array.Empty<byte>() : Mem.Read(mb + Mb.ONpcOut, n * Mb.NpcPoseSize);
        var list = new byte[n][];
        for (int i = 0; i < n; i++) list[i] = raw.AsSpan(i * Mb.NpcPoseSize, Mb.NpcPoseSize).ToArray();
        return (scene, list);
    }

    /// <summary>Hands the game the poses of NPCs other games drive.</summary>
    public void WriteNpcIn(uint scene, IReadOnlyList<byte[]> poses)
    {
        int n = Math.Min(poses.Count, Mb.NpcMax);
        var buf = new byte[Math.Max(n, 1) * Mb.NpcPoseSize];
        for (int i = 0; i < n; i++) poses[i].AsSpan(0, Mb.NpcPoseSize).CopyTo(buf.AsSpan(i * Mb.NpcPoseSize));
        if (n > 0) Mem.TryWrite(mb + Mb.ONpcIn, buf.AsSpan(0, n * Mb.NpcPoseSize).ToArray());
        W(Mb.ONpcInScene, scene);
        W(Mb.ONpcInCount, (uint)n);
        W(Mb.ONpcInSeq, U(Mb.ONpcInSeq) + 1);
    }

    public void Teleport(float x, float y, float z)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleBigEndian(b, x);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        Mem.TryWrite(mb + Mb.OTpPos, b);
        W(Mb.OTpSeq, U(Mb.OTpSeq) + 1);
    }

    /// <summary>Where puppet <paramref name="slot"/> actually is in this game.</summary>
    public (float x, float y, float z)? PuppetPos(int slot)
    {
        uint ctx = U(Mb.ODbgCtx), size = U(Mb.ODbgCtxSize);
        if (ctx == 0) return null;
        uint frame = Mem.ReadU32(ctx + (uint)slot * size + 0x48);
        if (frame < 0x80000000) return null;
        var b = Mem.Read(frame + 0x30, 12);
        return (BinaryPrimitives.ReadSingleBigEndian(b), BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(4)), BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(8)));
    }

    public string RemoteName(int slot)
    {
        var b = Mem.Read(mb + Mb.ORemote + (uint)(slot * Mb.RemoteSize) + 0x60, 16);
        int n = Array.IndexOf(b, (byte)0); return System.Text.Encoding.ASCII.GetString(b, 0, n < 0 ? 16 : n);
    }

    public uint RemoteFlags(int slot) => U(Mb.ORemote + (uint)(slot * Mb.RemoteSize) + 0x58);

    public bool RemoteActive(int slot) => U(Mb.ORemote + (uint)(slot * Mb.RemoteSize)) != 0;

    /// <summary>Scene objects: (index, id, type, position if it is an entity with a model).</summary>
    public List<(int index, uint id, byte type, float x, float y, float z)> SceneObjects()
    {
        var list = new List<(int, uint, byte, float, float, float)>();
        uint sc = Mem.ReadU32(0x803C0558 + 0x1FC0);
        if (sc == 0) return list;
        int n = (int)Mem.ReadU32(sc + 0x74);
        uint baseArr = Mem.ReadU32(sc + 0x78);
        var ptrs = Mem.Read(baseArr, n * 4);
        for (int i = 0; i < n; i++)
        {
            uint b = BinaryPrimitives.ReadUInt32BigEndian(ptrs.AsSpan(4 * i));
            if (b < 0x80000000) continue;
            var h = Mem.Read(b, 0x28);
            uint id = BinaryPrimitives.ReadUInt32BigEndian(h);
            byte type = h[4];
            float x = 0, y = 0, z = 0;
            uint model = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(0x24));
            if ((type == 0x04 || type == 0x06 || type == 0x1B || type == 0x29 || type == 0x2B) && model >= 0x80000000)
            {
                uint mat = Mem.ReadU32(model + 0x4C);
                if (mat >= 0x80000000)
                {
                    var p = Mem.Read(mat + 0x30, 12);
                    x = BinaryPrimitives.ReadSingleBigEndian(p); y = BinaryPrimitives.ReadSingleBigEndian(p.AsSpan(4)); z = BinaryPrimitives.ReadSingleBigEndian(p.AsSpan(8));
                }
            }
            list.Add((i, id, type, x, y, z));
        }
        return list;
    }

    public void Dispose() => Mem.Dispose();
}
