using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BfbbOnline.Shared;

namespace BfbbOnline.Client;

/// <summary>
/// Headless test player: BFBB-Online.exe --bot --name A --party test [--server host] [--port n]
///   [--user dolphinUserDir] [--boot HB01] [--walk] [--secs 120] [--log file]
/// Connects, starts its own Dolphin, optionally walks in circles (pad override), logs to a file.
/// </summary>
static class BotMode
{
    public static int Run(string[] args)
    {
        var a = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
            if (args[i].StartsWith("--"))
                a[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "1";
        string logPath = a.GetValueOrDefault("log", Path.Combine(AppContext.BaseDirectory, $"bot-{a.GetValueOrDefault("name", "bot")}.log"));
        using var logw = new StreamWriter(logPath, false) { AutoFlush = true };
        void Log(string s) { lock (logw) logw.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }

        var cfg = new Config
        {
            Name = a.GetValueOrDefault("name", "bot"),
            Party = a.GetValueOrDefault("party", "test"),
            Server = a.GetValueOrDefault("server", "127.0.0.1"),
            Port = int.Parse(a.GetValueOrDefault("port", Proto.DefaultPort.ToString())),
            Color = a.GetValueOrDefault("color", "#00ccff"),
        };
        var dolphin = Installer.FindDolphin(a.GetValueOrDefault("dolphin"));
        var deluxe = Installer.FindDeluxe(a.GetValueOrDefault("deluxe"), dolphin);
        if (dolphin == null || deluxe == null) { Log("Dolphin or Deluxe not found"); return 2; }
        Installer.Install(deluxe, Log);

        using var s = new Session(cfg, Log);
        s.StatusChanged += st => Log("status: " + st);
        string? follow = a.GetValueOrDefault("follow");
        uint followScene = 0;
        s.MembersChanged += m =>
        {
            Log("members: " + string.Join(", ", m));
            if (follow != null)
                foreach (var (n, _, sc) in m)
                    if (n.Equals(follow, StringComparison.OrdinalIgnoreCase) && sc.Length == 4 && sc != "HB00")
                        followScene = PlayerState.SceneIdOf(sc);
        };
        if (a.TryGetValue("user", out var user)) s.UserDir = Path.GetFullPath(user);
        if (a.TryGetValue("boot", out var boot)) s.BootScene = PlayerState.SceneIdOf(boot);
        if (a.TryGetValue("state", out var state)) s.SaveState = Path.GetFullPath(state);
        s.Connect(cfg.Server, cfg.Port);
        s.LaunchGame(dolphin);
        Log($"dolphin pid {s.DolphinPid}");

        int secs = int.Parse(a.GetValueOrDefault("secs", "120"));
        bool walk = a.ContainsKey("walk");
        var until = DateTime.UtcNow.AddSeconds(secs);
        double t = 0;
        var lastReport = DateTime.MinValue;
        uint to = a.TryGetValue("to", out var toS) ? PlayerState.SceneIdOf(toS) : (a.ContainsKey("follow") ? PlayerState.SceneIdOf("HB01") : 0);
        var nextWarp = DateTime.MinValue;
        uint lastFrame = 0;
        int optionsDone = 0;
        while (DateTime.UtcNow < until && (s.DolphinPid == 0 || s.GameRunning))
        {
            Thread.Sleep(50);
            t += 0.05;
            var link = s.Link;
            if (link == null) continue;
            try
            {
                // --to XXXX: go through Deluxe's real new-game start (HB00 options, intro) first,
                // then warp; booting a level directly skips Deluxe's own setup.
                if (follow != null && followScene != 0) to = followScene;
                if (to != 0 && link.TryReadLocal(out var mt))
                {
                    uint hb00 = PlayerState.SceneIdOf("HB00");
                    if (mt.SceneId == hb00 || !mt.InGame)
                    {
                        int ph = (int)(t * 4) % 8;   // START, then A, ~1 s apart: options, confirm
                        link.PadOverride(true, ph == 0 ? 0x1u : ph == 4 ? 0x10000u : 0u);
                        // once the options are confirmed the intro cutscene runs (frames stop):
                        // leave for the target right away
                        if (a.ContainsKey("skipintro") && mt.SceneId == hb00 && mt.Frame == lastFrame && DateTime.UtcNow > nextWarp && optionsDone++ > 40)
                        {
                            Log("options confirmed, skipping the intro");
                            link.PadOverride(false);
                            link.Warp(to, anytime: true);
                            nextWarp = DateTime.UtcNow.AddSeconds(20);
                        }
                        lastFrame = mt.Frame;
                        continue;
                    }
                    if (mt.SceneId != to)
                    {
                        if (DateTime.UtcNow > nextWarp)
                        {
                            Log($"warping {PlayerState.SceneName(mt.SceneId)} -> {PlayerState.SceneName(to)}");
                            link.PadOverride(false);
                            link.Warp(to);
                            nextWarp = DateTime.UtcNow.AddSeconds(15);
                        }
                        continue;
                    }
                }
                if (walk && link.TryReadLocal(out var me0) && me0.InGame)
                {
                    // walk in circles; tap A now and then to get through dialogs (jumps otherwise)
                    uint btn = (int)(t * 10) % 30 == 0 ? 0x10000u : 0u;
                    link.PadOverride(true, btn, (sbyte)(Math.Cos(t * 0.8) * 100), (sbyte)(Math.Sin(t * 0.8) * 100));
                }
                else if (a.ContainsKey("taps") && link.TryReadLocal(out var me1) && me1.InGame)
                    link.PadOverride(true, (int)(t * 10) % 30 == 0 ? 0x10000u : 0u);
                if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(2) && link.TryReadLocal(out var me))
                {
                    lastReport = DateTime.UtcNow;
                    var (drop, appl, cnt) = link.ObjStats;
                    Log($"me {PlayerState.SceneName(me.SceneId)} ingame={me.InGame} pos=({me.X:F1},{me.Y:F1},{me.Z:F1}) frame={me.Frame} ctx={link.CtxReady} shiny={me.Shiny} spat={me.Spatula} objs={cnt} primed={link.ObjPrimed} sent={s.ObjCounts.sent} got={s.ObjCounts.got} applied={appl} dropped={drop}");
                }
            }
            catch (Exception e) { Log("bot: " + e.Message); }
        }
        Log("bot done");
        return 0;
    }
}
