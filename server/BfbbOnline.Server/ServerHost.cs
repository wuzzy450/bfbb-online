using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BfbbOnline.Shared;

namespace BfbbOnline.Server;

public sealed class ServerOptions
{
    public int Port { get; set; } = Proto.DefaultPort;
    /// <summary>Where party worlds are kept (parties/&lt;name&gt;/world.json).</summary>
    public string DataDir { get; set; } = "data";
    public SimOptions Sims { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public Action<string>? Log { get; set; }
}

public sealed class Player
{
    public int Id;
    public string Name = "";
    public string Color = "#ffff00";
    public JsonConnection Conn = null!;
    public Party? Party;
    public uint Scene;               // scene id the player is in (0 = menu / loading)
    public JsonObject? LastState;    // last "st" (relayed and fed to sims)
    public PlayerState Pose;         // decoded LastState for sims
    public uint Character;
    public Dictionary<uint, byte[]>? Baseline; // world last sent to this player
    public byte[]? PersonalPlyr;               // this player's own PLYR (story cutscenes seen)
    public DateTime LastSeen = DateTime.UtcNow;
}

public sealed class Party
{
    public string Name = "";
    public World World = null!;
    public readonly List<Player> Members = new();
    public IEnumerable<Player> InScene(uint scene) => Members.Where(m => m.Scene == scene && scene != 0);

    /// <summary>Live object states per scene (latest wins), newer than the world slots.</summary>
    readonly Dictionary<uint, Dictionary<(uint, ushort), ObjState>> objects = new();

    readonly HashSet<(uint, uint)> spatulas = new();

    /// <summary>True the first time this spatula pickup is seen collected.</summary>
    public bool FirstSpatula(uint scene, uint id) { lock (spatulas) return spatulas.Add((scene, id)); }

    public void StoreObj(ObjState o)
    {
        lock (objects)
        {
            if (!objects.TryGetValue(o.Scene, out var d)) objects[o.Scene] = d = new();
            // enemies: a kill is for the whole party; one game's enemy (re)appearing doesn't undo it
            if (o.Type == 0x2B && d.TryGetValue((o.Id, o.Index), out var had) && had.Data.Length > 0 && (had.Data[3] & 1) == 0
                && o.Data.Length > 0 && (o.Data[3] & 1) != 0) return;
            d[(o.Id, o.Index)] = o;
        }
    }

    public List<ObjState> ObjectsIn(uint scene)
    {
        lock (objects) return objects.TryGetValue(scene, out var d) ? d.Values.ToList() : new List<ObjState>();
    }
}

/// <summary>The BFBB Online server. Runs dedicated (BfbbOnlineServer.exe) or inside the launcher.</summary>
public sealed class ServerHost : IDisposable
{
    readonly ServerOptions opt;
    readonly object gate = new();
    readonly Dictionary<int, Player> players = new();
    readonly Dictionary<string, Party> parties = new(StringComparer.OrdinalIgnoreCase);
    readonly CancellationTokenSource cts = new();
    TcpListener? listener;
    int nextId = 1;
    public SimManager Sims { get; }

    public ServerHost(ServerOptions opt)
    {
        this.opt = opt;
        Sims = new SimManager(opt.Sims, this, Log);
    }

    public void Log(string s) => opt.Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {s}");

    public void Start()
    {
        listener = new TcpListener(IPAddress.IPv6Any, opt.Port);
        listener.Server.DualMode = true;
        listener.Start();
        Log($"BFBB Online server listening on TCP {opt.Port}; worlds in {Path.GetFullPath(opt.DataDir)}");
        _ = Task.Run(AcceptLoop);
        _ = Task.Run(Housekeeping);
    }

    async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener!.AcceptTcpClientAsync(cts.Token); }
            catch (Exception) when (cts.IsCancellationRequested) { return; }
            catch (SocketException) { continue; }
            var p = new Player { Conn = new JsonConnection(tcp) };
            lock (gate) { p.Id = nextId++; players[p.Id] = p; }
            p.Conn.Received += o => { try { Handle(p, o); } catch (Exception e) { Log($"error handling {Proto.Type(o)} from {p.Name}: {e.Message}"); } };
            p.Conn.Closed += why => Disconnected(p, why);
            p.Conn.Start();
        }
    }

    async Task Housekeeping()
    {
        while (!cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), cts.Token); } catch { return; }
            SaveAll();
        }
    }

    public void SaveAll()
    {
        List<Party> ps;
        lock (gate) ps = parties.Values.ToList();
        foreach (var p in ps)
            try { p.World.Save(); } catch (Exception e) { Log($"saving {p.Name}: {e.Message}"); }
    }

    // ---- messages ----------------------------------------------------------------------------
    void Handle(Player p, JsonObject o)
    {
        p.LastSeen = DateTime.UtcNow;
        switch (Proto.Type(o))
        {
            case "hello": Hello(p, o); break;
            case "join": Join(p, o); break;
            case "leave": lock (gate) LeaveParty(p); break;
            case "st": State(p, o); break;
            case "load": Load(p, o); break;
            case "chat": Chat(p, o); break;
            case "obj": Obj(p, o); break;
            case "np": NpcPoses(p, o); break;
            case "objsync": ObjSync(p, o); break;
            case "cs": Cutscene(p, o); break;
            case "talk": Talk(p, o); break;
            case "pay": Pay(p, o); break;
            case "plog": PlayerLog(p, o); break;
            case "inv": Inventory(p, o); break;
            case "cheat": Cheat(p, o); break;
            case "ping":
                var pong = Proto.Msg("pong"); pong["ts"] = o["ts"]?.DeepClone(); p.Conn.Send(pong);
                break;
            case "bye": p.Conn.Close("bye"); break;
        }
    }

    void Hello(Player p, JsonObject o)
    {
        int ver = o["version"]?.GetValue<int>() ?? 0;
        if (ver != Proto.Version)
        {
            var e = Proto.Msg("error");
            e["msg"] = $"version mismatch: server {Proto.Version}, you {ver}. Update BFBB Online.";
            p.Conn.Send(e);
            p.Conn.Close("version");
            return;
        }
        lock (gate)
        {
            string name = Clean(o["name"]?.GetValue<string>() ?? "", 16);
            if (name.Length == 0) name = "Player";
            string baseName = name;
            for (int i = 2; players.Values.Any(q => q != p && q.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++)
                name = $"{baseName}{i}";
            p.Name = name;
            var c = o["color"]?.GetValue<string>() ?? "";
            if (Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$")) p.Color = c;
        }
        var w = Proto.Msg("welcome");
        w["id"] = p.Id; w["name"] = p.Name; w["version"] = Proto.Version;
        w["parties"] = PartyList();
        p.Conn.Send(w);
        Log($"{p.Name} connected from {p.Conn.Remote}");
    }

    void Join(Player p, JsonObject o)
    {
        string name = Clean(o["party"]?.GetValue<string>() ?? "", 24);
        if (name.Length == 0) name = "default";
        Party party;
        lock (gate)
        {
            LeaveParty(p);
            if (!parties.TryGetValue(name, out party!))
            {
                string dir = Path.Combine(opt.DataDir, "parties", SafeFile(name));
                party = new Party { Name = name, World = new World(Path.Combine(dir, "world.json")) };
                try { party.World.Load(); } catch (Exception e) { Log($"loading party {name}: {e.Message}"); }
                parties[name] = party;
            }
            party.Members.Add(p);
            p.Party = party;
            p.Baseline = null;
            p.Scene = 0;
        }
        var j = Proto.Msg("joined");
        j["party"] = party.Name;
        j["fresh"] = party.World.IsEmpty;
        p.Conn.Send(j);
        Log($"{p.Name} joined party {party.Name} ({party.Members.Count} in it)");
        SendMembers(party);
    }

    void LeaveParty(Player p)
    {
        var party = p.Party;
        if (party == null) return;
        party.Members.Remove(p);
        uint scene = p.Scene;
        p.Party = null;
        p.Scene = 0;
        SendMembers(party);
        if (scene != 0) Sims.OccupancyChanged(party, scene);
        if (party.Members.Count == 0)
            try { party.World.Save(); } catch (Exception e) { Log($"saving {party.Name}: {e.Message}"); }
    }

    void State(Player p, JsonObject o)
    {
        var party = p.Party;
        if (party == null) return;
        uint scene = PlayerState.SceneIdOf(o["s"]?.GetValue<string>() ?? "");
        p.LastState = o;
        p.Pose = DecodePose(o, scene);
        p.Character = p.Pose.Character;
        bool moved = false;
        lock (gate)
        {
            if (scene != p.Scene)
            {
                uint old = p.Scene;
                p.Scene = scene;
                moved = true;
                if (old != 0) Sims.OccupancyChanged(party, old);
            }
        }
        if (moved)
        {
            SendMembers(party);
            Sims.OccupancyChanged(party, scene);
        }
        // relay to party members in the same scene
        var fwd = (JsonObject)o.DeepClone();
        fwd["id"] = p.Id;
        string line = fwd.ToJsonString();
        List<Player> to;
        lock (gate) to = party.InScene(scene).Where(m => m != p).ToList();
        foreach (var m in to) m.Conn.Send(line);
        Sims.PlayerMoved(party, p);
    }

    /// <summary>
    /// Scene-load handshake: the player's game is about to load a scene. Take its upload (what it
    /// changed since we last gave it the world), merge, and answer with the party's world.
    /// </summary>
    void Load(Player p, JsonObject o)
    {
        var party = p.Party;
        if (party == null) return;
        var up = World.FromJson(o["up"] as JsonObject);
        uint scene = PlayerState.SceneIdOf(o["s"]?.GetValue<string>() ?? "");
        uint character = o["ch"]?.GetValue<uint>() ?? 0;
        string Sh(IReadOnlyDictionary<uint, byte[]>? w) =>
            w != null && w.TryGetValue(PlyrSlot.Tag, out var pl) && pl.Length >= 16 ? SerialBits.GetValue(pl, PlyrSlot.BitShiny, 4).ToString() : "-";
        string before = $"shinies: upload {Sh(up)} baseline {Sh(p.Baseline)} party {Sh(party.World.Snapshot())}";
        bool seeded = false;
        if (up.TryGetValue(PlyrSlot.Tag, out var myPlyr)) p.PersonalPlyr = (byte[])myPlyr.Clone();
        if (party.World.IsEmpty)
        {
            if (up.Count > 0) { party.World.Merge(up, null); seeded = true; }
        }
        else if (p.Baseline != null)
        {
            party.World.Merge(up, p.Baseline);   // counters aren't merged: they only move through live changes
        }
        var world = party.World.ForPlayer(character, p.PersonalPlyr);
        p.Baseline = world.ToDictionary(k => k.Key, v => (byte[])v.Value.Clone());
        foreach (var (ps, pid, pev) in party.World.AllPays()) p.Conn.Send(PayMsg(ps, pid, pev));
        var r = Proto.Msg("world");
        r["seq"] = o["seq"]?.DeepClone();
        r["slots"] = ToJsonSlots(world);
        p.Conn.Send(r);
        Log($"{p.Name} loads {PlayerState.SceneName(scene)}: uploaded {up.Count} slots{(seeded ? " (seeded the party)" : "")}, got {world.Count}; {before} -> {Sh(world)}");
    }

    /// <summary>An object in the player's scene changed (picked up, broken, pressed, killed...).</summary>
    void Obj(Player p, JsonObject o)
    {
        var party = p.Party;
        if (party == null || ObjState.FromJson(o) is not { } st || st.Scene != p.Scene) return;
        if (st.CountOnly) { CountSpatula(party, st); return; }   // a race's reward: per player
        party.StoreObj(st);
        RelayObj(party, st, p);
        Sims.ObjectChanged(party, st);
        CountSpatula(party, st);
    }

    /// <summary>
    /// Spatulas are counted here, once per spatula pickup, whoever's game collected it (games also
    /// award themselves spatulas through synced level logic, e.g. a race won by a teammate).
    /// Everyone gets the party's count.
    /// </summary>
    public void CountSpatula(Party party, ObjState st)
    {
        if (st.Kind != ObjState.KindSpatula || !st.PickupCollected || !party.FirstSpatula(st.Scene, st.Id)) return;
        AddSpatulas(party, 1);
        Log($"{party.Name}: spatula {st.Id:X8} in {PlayerState.SceneName(st.Scene)} -> {party.World.GetCounter(1)} spatulas");
    }

    /// <summary>
    /// The game's "+10 spatulas" cheat (pause, hold L+R, X Y Y X X Y Y X): the cheater's launcher
    /// reports it and the whole party gets the spatulas, like every other counter (max 100).
    /// </summary>
    void Cheat(Player p, JsonObject o)
    {
        var party = p.Party;
        if (party == null) return;
        int add = Math.Clamp(o["spat"]?.GetValue<int>() ?? 0, 0, 10);
        uint have = party.World.GetCounter(1);
        add = (int)Math.Min(add, 100 - Math.Min(100u, have));
        if (add <= 0) return;
        AddSpatulas(party, add);
        Log($"{party.Name}: {p.Name} used the spatula cheat -> {party.World.GetCounter(1)} spatulas");
    }

    void AddSpatulas(Party party, int add)
    {
        party.World.AddCounter(1, add);
        uint n = party.World.GetCounter(1);
        var m = Proto.Msg("spat"); m["n"] = n;
        string line = m.ToJsonString();
        List<Player> members;
        lock (gate) members = party.Members.ToList();
        foreach (var mem in members)
        {
            if (mem.Baseline != null && mem.Baseline.TryGetValue(PlyrSlot.Tag, out var bp)) World.AddCounter(bp, 1, add);
            mem.Conn.Send(line);
        }
    }

    /// <summary>Sends an object state to everyone in its scene except <paramref name="from"/>.</summary>
    public void RelayObj(Party party, ObjState st, Player? from)
    {
        var fwd = st.ToJson();
        if (from != null) fwd["from"] = from.Id;
        string line = fwd.ToJsonString();
        List<Player> to;
        lock (gate) to = party.InScene(st.Scene).Where(m => m != from).ToList();
        foreach (var m in to) m.Conn.Send(line);
    }

    /// <summary>Poses of the enemies a player's game drives: to the others in that scene (not stored).</summary>
    void NpcPoses(Player p, JsonObject o)
    {
        var party = p.Party;
        if (party == null) return;
        uint scene = PlayerState.SceneIdOf(o["s"]?.GetValue<string>() ?? "");
        if (scene != p.Scene) return;
        o["from"] = p.Id;
        string line = o.ToJsonString();
        List<Player> to;
        lock (gate) to = party.InScene(scene).Where(m => m != p).ToList();
        foreach (var m in to) m.Conn.Send(line);
    }

    /// <summary>A player (re)entered a scene, or reset it: send every live state we have for it.</summary>
    void ObjSync(Player p, JsonObject o)
    {
        var party = p.Party;
        if (party == null) return;
        uint scene = PlayerState.SceneIdOf(o["s"]?.GetValue<string>() ?? "");
        var r = Proto.Msg("objs");
        r["s"] = PlayerState.SceneName(scene);
        var arr = new JsonArray();
        foreach (var st in party.ObjectsIn(scene)) arr.Add(st.ToJson());
        r["list"] = arr;
        p.Conn.Send(r);
    }

    /// <summary>A story cutscene started: teammates in that scene watch it too, the rest get told.</summary>
    void Cutscene(Player p, JsonObject o)
    {
        var party = p.Party;
        if (party == null) return;
        uint scene = PlayerState.SceneIdOf(o["s"]?.GetValue<string>() ?? "");
        var cs = Proto.Msg("cs");
        cs["s"] = PlayerState.SceneName(scene); cs["id"] = o["id"]?.DeepClone(); cs["from"] = p.Id; cs["name"] = p.Name;
        var note = Proto.Msg("notice");
        note["text"] = $"{p.Name} is watching a cutscene in {PlayerState.SceneName(scene)}";
        List<Player> members;
        lock (gate) members = party.Members.Where(m => m != p).ToList();
        foreach (var m in members) m.Conn.Send(m.Scene == scene ? cs : note);
        Log($"{p.Name} started cutscene {o["id"]} in {PlayerState.SceneName(scene)}");
    }

    /// <summary>
    /// Someone paid (a shiny door...): every teammate's game replays what the payment set off, now
    /// and whenever they enter that scene. The shinies themselves move through the shared inventory.
    /// </summary>
    void Pay(Player p, JsonObject o)
    {
        var party = p.Party;
        if (party == null || o["id"]?.GetValue<uint>() is not uint id || o["ev"]?.GetValue<uint>() is not uint ev) return;
        uint scene = PlayerState.SceneIdOf(o["s"]?.GetValue<string>() ?? "");
        if (scene == 0) return;
        party.World.AddPay(scene, id, ev);
        var m = PayMsg(scene, id, ev).ToJsonString();
        List<Player> to;
        lock (gate) to = party.Members.Where(x => x != p).ToList();
        foreach (var x in to) x.Conn.Send(m);
        Log($"{p.Name} paid at {id:X8} in {PlayerState.SceneName(scene)}");
    }

    /// <summary>
    /// A player's launcher log and Dolphin errors: kept in DataDir/../player-logs/&lt;name&gt;.log; Dolphin's
    /// lines (crash popups...) are also shown in the host's log.
    /// </summary>
    void PlayerLog(Player p, JsonObject o)
    {
        if (o["l"] is not JsonArray lines) return;
        var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(opt.DataDir)) ?? ".", "player-logs");
        string safe = string.Concat((p.Name ?? "player").Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_'));
        try
        {
            Directory.CreateDirectory(dir);
            File.AppendAllLines(Path.Combine(dir, safe + ".log"),
                lines.Select(l => $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {l?.GetValue<string>()}"));
        }
        catch { }
        foreach (var l in lines)
            if (l?.GetValue<string>() is string s && s.StartsWith("[dolphin]")
                && (s.Contains("Invalid", StringComparison.OrdinalIgnoreCase) || s.Contains("error", StringComparison.OrdinalIgnoreCase) || s.Contains("PC =")))
                Log($"{p.Name}'s game: {s[10..]}");
    }

    static JsonObject PayMsg(uint scene, uint id, uint ev)
    {
        var m = Proto.Msg("pay");
        m["s"] = PlayerState.SceneName(scene); m["id"] = id; m["ev"] = ev;
        return m;
    }

    /// <summary>A conversation started: teammates in that scene are offered to watch along.</summary>
    void Talk(Player p, JsonObject o)
    {
        var party = p.Party;
        if (party == null) return;
        uint scene = PlayerState.SceneIdOf(o["s"]?.GetValue<string>() ?? "");
        var t = Proto.Msg("talk");
        t["s"] = PlayerState.SceneName(scene); t["id"] = o["id"]?.DeepClone(); t["from"] = p.Id; t["name"] = p.Name;
        string line = t.ToJsonString();
        List<Player> to;
        lock (gate) to = party.InScene(scene).Where(m => m != p).ToList();
        foreach (var m in to) m.Conn.Send(line);
    }

    /// <summary>
    /// The player's own counters changed (collected, spent): add it for the whole party. Every
    /// baseline moves too, since each game now has it and must not upload it again.
    /// </summary>
    void Inventory(Player p, JsonObject o)
    {
        var party = p.Party;
        if (party == null || o["d"] is not JsonArray d) return;
        var deltas = new List<(int i, int d)>();
        foreach (var e in d.OfType<JsonArray>())
            if (e.Count == 2 && e[0]!.GetValue<int>() != 1) deltas.Add((e[0]!.GetValue<int>(), e[1]!.GetValue<int>()));   // 1 = spatulas: counted by the server
        if (deltas.Count == 0) return;
        foreach (var (i, dd) in deltas) party.World.AddCounter(i, dd);
        var fwd = Proto.Msg("inv"); fwd["d"] = new JsonArray(deltas.Select(x => (JsonNode)new JsonArray(x.i, x.d)).ToArray()); fwd["from"] = p.Id; fwd["name"] = p.Name;
        string line = fwd.ToJsonString();
        List<Player> members;
        lock (gate) members = party.Members.ToList();
        foreach (var m in members)
        {
            if (m.Baseline != null && m.Baseline.TryGetValue(PlyrSlot.Tag, out var bp))
                foreach (var (i, dd) in deltas) World.AddCounter(bp, i, dd);
            if (m != p) m.Conn.Send(line);
        }
    }

    void Chat(Player p, JsonObject o)
    {
        var party = p.Party;
        string text = Clean(o["text"]?.GetValue<string>() ?? "", 200);
        if (text.Length == 0) return;
        var c = Proto.Msg("chat");
        c["id"] = p.Id; c["name"] = p.Name; c["color"] = p.Color; c["text"] = text;
        string line = c.ToJsonString();
        List<Player> to;
        lock (gate) to = party != null ? party.Members.ToList() : players.Values.Where(q => q.Party == null).ToList();
        foreach (var m in to) m.Conn.Send(line);
        Log($"<{p.Name}> {text}");
    }

    void Disconnected(Player p, string why)
    {
        lock (gate)
        {
            players.Remove(p.Id);
            LeaveParty(p);
        }
        if (p.Name.Length > 0) Log($"{p.Name} disconnected ({why})");
    }

    // ---- helpers -----------------------------------------------------------------------------
    void SendMembers(Party party)
    {
        JsonObject m;
        List<Player> to;
        lock (gate)
        {
            m = Proto.Msg("members");
            m["party"] = party.Name;
            var arr = new JsonArray();
            foreach (var q in party.Members)
                arr.Add(new JsonObject { ["id"] = q.Id, ["name"] = q.Name, ["color"] = q.Color, ["s"] = PlayerState.SceneName(q.Scene) });
            m["members"] = arr;
            m["sims"] = Sims.Describe(party);
            to = party.Members.ToList();
        }
        string line = m.ToJsonString();
        foreach (var q in to) q.Conn.Send(line);
    }

    JsonArray PartyList()
    {
        var a = new JsonArray();
        lock (gate)
            foreach (var p in parties.Values.Where(p => p.Members.Count > 0))
                a.Add(new JsonObject { ["name"] = p.Name, ["players"] = p.Members.Count });
        return a;
    }

    public static JsonObject ToJsonSlots(IReadOnlyDictionary<uint, byte[]> w)
    {
        var s = new JsonObject();
        foreach (var (tag, b) in w) s[PlayerState.SceneName(tag)] = Convert.ToBase64String(b);
        return s;
    }

    static PlayerState DecodePose(JsonObject o, uint scene)
    {
        var s = new PlayerState { SceneId = scene };
        if (o["p"] is JsonArray p && p.Count == 3) { s.X = F(p[0]); s.Y = F(p[1]); s.Z = F(p[2]); }
        if (o["v"] is JsonArray v && v.Count == 3) { s.Vx = F(v[0]); s.Vy = F(v[1]); s.Vz = F(v[2]); }
        s.Yaw = F(o["y"]);
        s.Character = o["ch"]?.GetValue<uint>() ?? 0;
        s.Health = o["hp"]?.GetValue<uint>() ?? 3;
        s.Buttons = o["b"]?.GetValue<uint>() ?? 0;
        if (o["a"] is JsonArray a && a.Count == 4) { s.Ax = (sbyte)(int)F(a[0]); s.Ay = (sbyte)(int)F(a[1]); s.Cx = (sbyte)(int)F(a[2]); s.Cy = (sbyte)(int)F(a[3]); }
        if (o["c"] is JsonArray c && c.Count == 9) { s.Cam = new float[9]; for (int i = 0; i < 9; i++) s.Cam[i] = F(c[i]); }
        return s;
    }

    static float F(JsonNode? n) => n == null ? 0 : (float)n.GetValue<double>();

    static string Clean(string s, int max)
    {
        s = new string(s.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return s.Length > max ? s[..max] : s;
    }

    static string SafeFile(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9_-]", "_");

    public Party? FindParty(string name) { lock (gate) return parties.GetValueOrDefault(name); }

    public List<Party> PartiesSnapshot() { lock (gate) return parties.Values.ToList(); }
    public List<Player> PlayersInScene(Party party, uint scene) { lock (gate) return party.InScene(scene).ToList(); }

    public void Dispose()
    {
        cts.Cancel();
        try { listener?.Stop(); } catch { }
        Sims.Dispose();
        List<Player> ps;
        lock (gate) ps = players.Values.ToList();
        foreach (var p in ps) p.Conn.Close("server stopping");
        SaveAll();
    }
}
