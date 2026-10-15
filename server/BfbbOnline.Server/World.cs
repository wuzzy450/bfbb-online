using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BfbbOnline.Shared;

namespace BfbbOnline.Server;

/// <summary>
/// A party's campaign: the game's save-serializer slots (tag → bytes). 'PLYR' is the inventory,
/// 'CNTR' global counters/cutscene flags, every other tag a scene's persistent objects.
///
/// Merging an upload uses what that uploader was last given (its baseline):
///  - PLYR counters (shinies, spatulas, socks, ...) add the uploader's delta, so two players
///    collecting at once both count, and spending shinies works;
///  - every other bit takes the uploader's value only where the uploader changed it, so players
///    changing different objects in the same scene both keep their changes.
/// </summary>
public sealed class World
{
    readonly Dictionary<uint, byte[]> slots = new();
    readonly object gate = new();
    public string? Path { get; }
    public bool Dirty { get; private set; }

    public World(string? path) { Path = path; }

    public bool IsEmpty { get { lock (gate) return slots.Count == 0; } }

    public Dictionary<uint, byte[]> Snapshot()
    {
        lock (gate) return slots.ToDictionary(k => k.Key, v => (byte[])v.Value.Clone());
    }

    /// <summary>Merges <paramref name="upload"/>; <paramref name="baseline"/> is what the uploader had been given.</summary>
    public void Merge(IReadOnlyDictionary<uint, byte[]> upload, IReadOnlyDictionary<uint, byte[]>? baseline,
        ISet<uint>? onlyTags = null)
    {
        lock (gate)
        {
            foreach (var (tag, up) in upload)
            {
                if (onlyTags != null && !onlyTags.Contains(tag)) continue;
                byte[]? bas = null;
                baseline?.TryGetValue(tag, out bas);
                if (!slots.TryGetValue(tag, out var cur))
                {
                    // nobody had this slot yet (a scene seen first by this player)
                    slots[tag] = (byte[])up.Clone();
                    Dirty = true;
                    continue;
                }
                if (bas != null && bas.AsSpan().SequenceEqual(up)) continue; // nothing changed
                bas ??= new byte[up.Length];
                if (tag == PlyrSlot.Tag) MergePlyr(cur, up, bas);
                else MergeBits(cur, up, bas);
                Dirty = true;
            }
        }
    }

    static void MergeBits(byte[] cur, byte[] up, byte[] bas)
    {
        int n = Math.Min(cur.Length, Math.Min(up.Length, bas.Length));
        for (int i = 0; i < n; i++)
        {
            int mask = up[i] ^ bas[i];
            cur[i] = (byte)((cur[i] & ~mask) | (up[i] & mask));
        }
    }

    static void MergePlyr(byte[] cur, byte[] up, byte[] bas)
    {
        if (cur.Length < 144 || up.Length < 144 || bas.Length < 144) { MergeBits(cur, up, bas); return; }
        SerialBits.SetBit(cur, PlyrSlot.BitExists, SerialBits.GetBit(cur, PlyrSlot.BitExists) || SerialBits.GetBit(up, PlyrSlot.BitExists));
        uint mh = Math.Max(SerialBits.GetValue(cur, PlyrSlot.BitMaxHealth, 4), SerialBits.GetValue(up, PlyrSlot.BitMaxHealth, 4));
        SerialBits.SetValue(cur, PlyrSlot.BitMaxHealth, 4, mh);
        foreach (int b in new[] { PlyrSlot.BitPowerUp0, PlyrSlot.BitPowerUp1 })
            SerialBits.SetValue(cur, b, 1, Math.Max(SerialBits.GetValue(cur, b, 1), SerialBits.GetValue(up, b, 1)));
        // counters (shinies, spatulas, socks, level pickups) are not merged from uploads: they move only
        // through live changes ("inv", sent before every upload) and the server's spatula count. Merging
        // them here raced with those (a stale upload undid a teammate's reward).
        // the character (BitCharacter) is personal: never taken from an upload
    }

    /// <summary>
    /// The world as one player should receive it: their own character and their own story-cutscene
    /// flags (so a cinematic they missed still plays for them).
    /// </summary>
    public Dictionary<uint, byte[]> ForPlayer(uint? character, byte[]? personalPlyr = null)
    {
        var w = Snapshot();
        if (w.TryGetValue(PlyrSlot.Tag, out var p) && p.Length >= 144)
        {
            if (character is uint ch) SerialBits.SetValue(p, PlyrSlot.BitCharacter, 4, ch);
            if (personalPlyr != null && personalPlyr.Length == p.Length)
                for (int k = PlyrSlot.BitPersonal; k < p.Length * 8; k++)
                    SerialBits.SetBit(p, k, SerialBits.GetBit(personalPlyr, k));
        }
        return w;
    }

    /// <summary>A live counter's value (index into PlyrSlot.LiveCounters) in the party's PLYR.</summary>
    public uint GetCounter(int index)
    {
        lock (gate)
            return slots.TryGetValue(PlyrSlot.Tag, out var p) && p.Length >= 144
                ? SerialBits.GetValue(p, PlyrSlot.LiveCounters()[index].bit, 4) : 0;
    }

    /// <summary>Adds a live counter change (index into PlyrSlot.LiveCounters) to the party's PLYR.</summary>
    public void AddCounter(int index, int delta)
    {
        lock (gate)
        {
            if (!slots.TryGetValue(PlyrSlot.Tag, out var p)) return;
            AddCounter(p, index, delta);
            Dirty = true;
        }
    }

    public static void AddCounter(byte[] plyr, int index, int delta)
    {
        var c = PlyrSlot.LiveCounters();
        if (index < 0 || index >= c.Length || plyr.Length < 144) return;
        int bit = c[index].bit;
        long v = Math.Max(0, SerialBits.GetValue(plyr, bit, 4) + (long)delta);
        SerialBits.SetValue(plyr, bit, 4, (uint)Math.Min(v, uint.MaxValue));
    }

    // ---- persistence ------------------------------------------------------------------------
    public static string TagName(uint tag) => PlayerState.SceneName(tag);

    public JsonObject ToJson(bool onlyTags = false)
    {
        var o = new JsonObject();
        lock (gate)
            foreach (var (tag, b) in slots.OrderBy(k => k.Key))
                o[TagName(tag)] = onlyTags ? b.Length : Convert.ToBase64String(b);
        return o;
    }

    public static Dictionary<uint, byte[]> FromJson(JsonObject? o)
    {
        var d = new Dictionary<uint, byte[]>();
        if (o == null) return d;
        foreach (var (k, v) in o)
        {
            if (k.Length != 4 || v is not JsonValue jv || !jv.TryGetValue<string>(out var s)) continue;
            try { d[PlayerState.SceneIdOf(k)] = Convert.FromBase64String(s); } catch (FormatException) { }
        }
        return d;
    }

    public void Load()
    {
        if (Path == null || !File.Exists(Path)) return;
        var o = JsonNode.Parse(File.ReadAllText(Path)) as JsonObject;
        lock (gate)
        {
            slots.Clear();
            foreach (var (k, v) in FromJson(o?["slots"] as JsonObject)) slots[k] = v;
        }
    }

    public void Save()
    {
        if (Path == null) return;
        JsonObject o;
        lock (gate)
        {
            if (!Dirty) return;
            o = new JsonObject { ["format"] = 1, ["saved"] = DateTime.UtcNow.ToString("o"), ["slots"] = ToJson() };
            Dirty = false;
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, Path, true);
    }
}
