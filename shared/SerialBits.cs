using System;
using System.Buffers.Binary;

namespace BfbbOnline.Shared;

/// <summary>
/// Bit access matching the game's xSerial: stream bit k lives in big-endian word k/32, bit k%32
/// (LSB first). Values are written byte by byte in memory (big-endian) order, each byte LSB first.
/// </summary>
public static class SerialBits
{
    public static bool GetBit(ReadOnlySpan<byte> buf, int k)
    {
        uint w = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice((k >> 5) * 4));
        return (w & (1u << (k & 31))) != 0;
    }

    public static void SetBit(Span<byte> buf, int k, bool on)
    {
        var s = buf.Slice((k >> 5) * 4);
        uint w = BinaryPrimitives.ReadUInt32BigEndian(s);
        w = on ? w | (1u << (k & 31)) : w & ~(1u << (k & 31));
        BinaryPrimitives.WriteUInt32BigEndian(s, w);
    }

    /// <summary>Reads an n-byte value written with xSerial::Write at stream bit <paramref name="k"/>.</summary>
    public static uint GetValue(ReadOnlySpan<byte> buf, int k, int bytes)
    {
        uint v = 0;
        for (int j = 0; j < bytes; j++)
        {
            uint by = 0;
            for (int b = 0; b < 8; b++)
                if (GetBit(buf, k + 8 * j + b)) by |= 1u << b;
            v = (v << 8) | by;
        }
        return v;
    }

    public static void SetValue(Span<byte> buf, int k, int bytes, uint v)
    {
        for (int j = 0; j < bytes; j++)
        {
            uint by = (v >> (8 * (bytes - 1 - j))) & 0xFF;
            for (int b = 0; b < 8; b++)
                SetBit(buf, k + 8 * j + b, (by & (1u << b)) != 0);
        }
    }
}

/// <summary>The 'PLYR' serializer slot (zSceneSave): the player's inventory.</summary>
public static class PlyrSlot
{
    public const uint Tag = 0x504C5952; // 'PLYR'
    public const int LevelCount = 15;
    public const int BitExists = 0, BitMaxHealth = 1, BitCharacter = 33, BitShiny = 65, BitSpatula = 97,
        BitPowerUp0 = 129, BitPowerUp1 = 137, BitLevels = 145, BitSockTotal = 145 + LevelCount * 64,
        /// <summary>From here on PLYR holds the 14 story-cinematic "played" flags (zCutsceneMgrSave) and
        /// out-of-bounds hint state: personal, never shared across the party.</summary>
        BitPersonal = BitSockTotal + 32;

    /// <summary>The live counters in globals.player (address) and where PLYR stores each.</summary>
    public static (uint addr, int bit)[] LiveCounters()
    {
        const uint G = 0x803C0558;
        var l = new System.Collections.Generic.List<(uint, int)> { (G + 0x1B00, BitShiny), (G + 0x1B04, BitSpatula), (G + 0x1BC4, BitSockTotal) };
        for (int i = 0; i < LevelCount; i++)
        {
            l.Add((G + 0x1B08 + 4 * (uint)i, SockBit(i)));
            l.Add((G + 0x1B84 + 4 * (uint)i, LevelPickupBit(i)));
        }
        l.Add((G + 0x1738, BitMaxHealth));   // golden underwear: everyone's max health goes up
        return l.ToArray();
    }

    public static int SockBit(int level) => BitLevels + 64 * level;
    public static int LevelPickupBit(int level) => BitLevels + 64 * level + 32;

    /// <summary>Every counter that adds up across players (deltas are summed).</summary>
    public static int[] CounterBits()
    {
        var l = new System.Collections.Generic.List<int> { BitShiny, BitSpatula, BitSockTotal };
        for (int i = 0; i < LevelCount; i++) { l.Add(SockBit(i)); l.Add(LevelPickupBit(i)); }
        return l.ToArray();
    }
}
