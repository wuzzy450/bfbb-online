using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace BfbbOnline.Shared;

/// <summary>patch/out/bbol_patch.json (written by patch/build.py).</summary>
public sealed class PatchSpec
{
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public uint LoadAddr { get; set; }
    public int Size { get; set; }
    public uint ArenaLo { get; set; }
    public uint Mailbox { get; set; }
    public string Sha1 { get; set; } = "";
    public string[] BaseDolSha1 { get; set; } = Array.Empty<string>();
    public Hook[] Hooks { get; set; } = Array.Empty<Hook>();
    public string Blob { get; set; } = "";

    public sealed class Hook
    {
        public uint Addr { get; set; }
        public uint Expect { get; set; }
        public uint Write { get; set; }
        public string Fn { get; set; } = "";
    }

    public static PatchSpec Load(Stream s) =>
        JsonSerializer.Deserialize<PatchSpec>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("empty patch spec");
}

/// <summary>Applies a <see cref="PatchSpec"/> to a GameCube DOL (adds one text section, rewrites hook sites).</summary>
public static class DolPatcher
{
    public static string Sha1(byte[] b) => Convert.ToHexString(SHA1.HashData(b)).ToLowerInvariant();

    public static byte[] Apply(byte[] dol, PatchSpec spec)
    {
        var b = new byte[dol.Length];
        dol.CopyTo(b, 0);
        var off = new uint[18];
        var addr = new uint[18];
        var size = new uint[18];
        for (int i = 0; i < 18; i++)
        {
            off[i] = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4 * i));
            addr[i] = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(72 + 4 * i));
            size[i] = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(144 + 4 * i));
        }

        int FileOff(uint a)
        {
            for (int i = 0; i < 18; i++)
                if (size[i] != 0 && a >= addr[i] && a < addr[i] + size[i])
                    return (int)(off[i] + (a - addr[i]));
            throw new InvalidDataException($"address {a:X8} is not in the DOL");
        }

        for (int i = 0; i < 18; i++)
            if (size[i] != 0 && addr[i] == spec.LoadAddr)
                throw new InvalidDataException("this DOL is already patched");

        foreach (var h in spec.Hooks)
        {
            int fo = FileOff(h.Addr);
            uint cur = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(fo));
            if (cur != h.Expect)
                throw new InvalidDataException(
                    $"unexpected game code at {h.Addr:X8} ({h.Fn}): {cur:X8}, expected {h.Expect:X8}. Is this BFBB Deluxe 1.1 (USA)?");
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(fo), h.Write);
        }

        int slot = Array.FindIndex(size, 0, 7, s => s == 0);
        if (slot < 0) throw new InvalidDataException("no free text section in the DOL");
        var blob = Convert.FromBase64String(spec.Blob);
        int start = (b.Length + 31) & ~31;
        int end = (start + blob.Length + 31) & ~31;
        var outb = new byte[end];
        b.CopyTo(outb, 0);
        blob.CopyTo(outb, start);
        BinaryPrimitives.WriteUInt32BigEndian(outb.AsSpan(4 * slot), (uint)start);
        BinaryPrimitives.WriteUInt32BigEndian(outb.AsSpan(72 + 4 * slot), spec.LoadAddr);
        BinaryPrimitives.WriteUInt32BigEndian(outb.AsSpan(144 + 4 * slot), (uint)blob.Length);
        return outb;
    }
}
