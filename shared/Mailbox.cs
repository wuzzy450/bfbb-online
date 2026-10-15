using System;
using System.Buffers.Binary;
using System.Text;

namespace BfbbOnline.Shared;

/// <summary>C# view of patch/src/mailbox.h. Offsets must match it.</summary>
public static class Mb
{
    public const uint Magic = 0x42424F4C;   // 'BBOL'
    public const uint BridgeMagic = 0x42524447; // 'BRDG'
    public const int MaxRemote = 5;

    public const uint ModeOffline = 0, ModeClient = 1, ModeSim = 2;
    public const uint RfCheap = 1, RfHidden = 2, RfNoInput = 4;

    // Mailbox
    public const uint OMagic = 0x00, OVersion = 0x04, OBridge = 0x08, OMode = 0x0C, OBootScene = 0x10,
        OWarpScene = 0x14, OWarpSeq = 0x18, OWarpAck = 0x1C, OPadOverride = 0x20, OPadOn = 0x24, OPadAnalog = 0x28,
        OMaxRemote = 0x2C, OCtxReady = 0x30, OFrames = 0x34, OBootWaited = 0x38, OFlags = 0x3C,
        OLoadScene = 0x40, OLoadSeq = 0x44, OLoadAck = 0x48, OLoadWait = 0x4C,
        OEnsureCount = 0x50, OEnsureSeq = 0x54, OEnsureAck = 0x58, OLoadWaited = 0x5C,
        OLocal = 0x60, ORemote = 0xD0, OEnsureTags = 0x300,
        OOutHead = 0x380, OOutTail = 0x384, OInHead = 0x388, OInTail = 0x38C, OObjScene = 0x390,
        OObjPrimed = 0x394, OResetSeq = 0x398, OObjDropped = 0x39C, OObjApplied = 0x3A0, OObjCount = 0x3A4,
        OCsSeq = 0x3A8, OCsId = 0x3AC, OCsScene = 0x3B0, OCsPlayId = 0x3B4, OCsPlaySeq = 0x3B8, OCsPlayAck = 0x3BC,
        OOutRing = 0x3C0, OInRing = 0x3C0 + ObjRing * ObjRecSize;
    public const uint OInvCount = 0x43E0, OInvAdd = 0x4468, OInvSeq = 0x44F0, OInvAck = 0x44F4;
    public const uint OEvtId = 0x44F8, OEvtCode = 0x44FC, OEvtSeq = 0x4500, OEvtAck = 0x4504;
    public const uint OTalkSeq = 0x4508, OTalkId = 0x450C, OTalkScene = 0x4510, OOfferSeq = 0x4514, OOfferAck = 0x4518,
        OOfferId = 0x451C, OOfferResult = 0x4520, OOfferText = 0x4524;
    public const uint ODbgRender = 0x4564, OHeapReserve = 0x4604, OHeapFree = 0x4608, OHeapGap = 0x4620, OHighRam = 0x4624, OOsHeapAdded = 0x4628, OHighRamMapped = 0x462C, OObjNoisy = 0x4630, OInvSetMask = 0x4634;
    public const uint ODbgHashes = 0x43C0, ODbgCtx = 0x43C4, ODbgCtxSize = 0x43C8, OTpSeq = 0x43CC, OTpPos = 0x43D4;
    public const int ObjRing = 32, ObjRecSize = 0x100, ObjData = 240;
    // enemy movement (npcsync.c): poses of the NPCs each game drives
    public const uint ONpcRank = 0x463C, ONpcOutSeq = 0x4640, ONpcOutCount = 0x4644, ONpcOutScene = 0x4648,
        ONpcInSeq = 0x464C, ONpcInCount = 0x4650, ONpcInScene = 0x4654, ONpcApplied = 0x4658, ONpcRemoteRank = 0x465C,
        ONpcOut = 0x4670, ONpcIn = 0x4670 + NpcMax * NpcPoseSize;
    public const int NpcMax = 128, NpcPoseSize = 0x40;
    // paid gates: a payment's follow-ups replayed in teammates' games
    public const uint OPayOutSeq = 0x8670, OPayOutId = 0x8674, OPayOutEvent = 0x8678, OPayOutScene = 0x867C,
        OPayInHead = 0x8680, OPayInTail = 0x8684, OPayIn = 0x8688;
    public const int PayRing = 16;
    /// <summary>The patch's scratch serializer slot; never part of the world.</summary>
    public const uint ScratchTag = 0x42424F53; // 'BBOS'
    public const int LocalSize = 0x70, RemoteSize = 0x70;
}

/// <summary>One persistent object's state in the game's save format (see mailbox.h ObjRec).</summary>
public sealed record ObjState(uint Scene, uint Id, ushort Index, byte Type, ushort Bits, byte[] Data)
{
    /// <summary>Pickups: asset type &lt;&lt; 8 | index (0x0100 = spatula); 0 otherwise.</summary>
    public ushort Kind { get; init; }
    public const ushort KindSpatula = 0x0100;

    /// <summary>A race's reward: reported so the server counts it, never shared with teammates.</summary>
    public bool CountOnly { get; init; }

    /// <summary>A pickup's save bits: base, visible, 7 bits of state (0x04 grabbing, 0x08 collected), flag.</summary>
    public bool PickupCollected => Type == 0x04 && Data.Length >= 4
        && ((System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(Data) >> 2) & 0x0C) != 0;

    public System.Text.Json.Nodes.JsonObject ToJson(string t = "obj") => new()
    {
        ["t"] = t, ["s"] = PlayerState.SceneName(Scene), ["id"] = Id, ["i"] = Index, ["ty"] = Type,
        ["n"] = Bits, ["d"] = Convert.ToBase64String(Data), ["k"] = Kind, ["p"] = CountOnly,
    };

    public static ObjState? FromJson(System.Text.Json.Nodes.JsonObject o)
    {
        try
        {
            var d = Convert.FromBase64String(o["d"]!.GetValue<string>());
            ushort bits = o["n"]!.GetValue<ushort>();
            if (bits == 0 || bits > Mb.ObjData * 8 || d.Length < (bits + 31) / 32 * 4) return null;
            return new ObjState(PlayerState.SceneIdOf(o["s"]!.GetValue<string>()), o["id"]!.GetValue<uint>(),
                o["i"]!.GetValue<ushort>(), o["ty"]!.GetValue<byte>(), bits, d) { Kind = o["k"]?.GetValue<ushort>() ?? 0, CountOnly = o["p"]?.GetValue<bool>() ?? false };
        }
        catch (Exception) { return null; }
    }
}

/// <summary>A player's state as the game reports it (Local) or as we feed a puppet (Remote).</summary>
public struct PlayerState
{
    public uint Frame, SceneId, Character, Buttons, Health, Shiny, Spatula, ControlOff;
    public bool InGame;
    public sbyte Ax, Ay, Cx, Cy;
    public float X, Y, Z, Yaw, Vx, Vy, Vz;
    public float[] Cam; // 9: right, up, at

    public static string SceneName(uint id) =>
        id == 0 ? "" : Encoding.ASCII.GetString(new[] { (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id });

    public static uint SceneIdOf(string name)
    {
        var b = Encoding.ASCII.GetBytes(name.ToUpperInvariant().PadRight(4).Substring(0, 4));
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    /// <summary>Decodes the patch's Local block; false while the game is mid-write.</summary>
    public static bool TryParseLocal(ReadOnlySpan<byte> b, out PlayerState s)
    {
        s = default;
        uint seq = U32(b, 0x00), seqEnd = U32(b, 0x68);
        if (seq != seqEnd || (seq & 1) != 0) return false;
        s.Frame = U32(b, 0x04);
        s.SceneId = U32(b, 0x08);
        s.InGame = U32(b, 0x0C) != 0;
        s.Character = U32(b, 0x10);
        s.Buttons = U32(b, 0x14);
        s.Ax = (sbyte)b[0x18]; s.Ay = (sbyte)b[0x19]; s.Cx = (sbyte)b[0x1A]; s.Cy = (sbyte)b[0x1B];
        s.X = F32(b, 0x1C); s.Y = F32(b, 0x20); s.Z = F32(b, 0x24);
        s.Yaw = F32(b, 0x28);
        s.Vx = F32(b, 0x2C); s.Vy = F32(b, 0x30); s.Vz = F32(b, 0x34);
        s.Cam = new float[9];
        for (int i = 0; i < 9; i++) s.Cam[i] = F32(b, 0x38 + 4 * i);
        s.Health = U32(b, 0x5C);
        s.Shiny = U32(b, 0x60);
        s.Spatula = U32(b, 0x64);
        s.ControlOff = U32(b, 0x6C);
        return true;
    }

    /// <summary>Encodes a Remote slot (see mailbox.h Remote).</summary>
    public void WriteRemote(Span<byte> b, uint seq, uint flags, uint color, string name)
    {
        b.Clear();
        W32(b, 0x00, 1);
        W32(b, 0x04, seq);
        W32(b, 0x08, Character);
        W32(b, 0x0C, Buttons);
        b[0x10] = (byte)Ax; b[0x11] = (byte)Ay; b[0x12] = (byte)Cx; b[0x13] = (byte)Cy;
        WF(b, 0x14, X); WF(b, 0x18, Y); WF(b, 0x1C, Z);
        WF(b, 0x20, Yaw);
        WF(b, 0x24, Vx); WF(b, 0x28, Vy); WF(b, 0x2C, Vz);
        for (int i = 0; i < 9; i++) WF(b, 0x30 + 4 * i, Cam != null && Cam.Length == 9 ? Cam[i] : (i == 0 || i == 4 || i == 8 ? 1 : 0));
        W32(b, 0x54, Health);
        W32(b, 0x58, flags);
        W32(b, 0x5C, color);
        var nb = Encoding.ASCII.GetBytes(name ?? "");
        nb.AsSpan(0, Math.Min(15, nb.Length)).CopyTo(b.Slice(0x60));
    }

    static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.Slice(o));
    static float F32(ReadOnlySpan<byte> b, int o) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(b.Slice(o)));
    static void W32(Span<byte> b, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.Slice(o), v);
    static void WF(Span<byte> b, int o, float v) => BinaryPrimitives.WriteInt32BigEndian(b.Slice(o), BitConverter.SingleToInt32Bits(v));
}
