using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using BfbbOnline.Server;
using BfbbOnline.Shared;

namespace BfbbOnline.Dedicated;

static class Program
{
    static int Main(string[] args)
    {
        string dir = AppContext.BaseDirectory;
        string cfgPath = Path.Combine(dir, "server.json");
        var jso = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        ServerOptions opt;
        if (File.Exists(cfgPath))
            opt = JsonSerializer.Deserialize<ServerOptions>(File.ReadAllText(cfgPath), jso) ?? new ServerOptions();
        else
        {
            opt = new ServerOptions();
            GuessGamePaths(opt.Sims, dir);
            File.WriteAllText(cfgPath, JsonSerializer.Serialize(opt, jso));
            Console.WriteLine($"wrote default settings to {cfgPath}");
        }
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--port" && int.TryParse(args[i + 1], out int port)) opt.Port = port;
        opt.DataDir = Path.GetFullPath(Path.Combine(dir, opt.DataDir));
        opt.Sims.WorkDir = Path.GetFullPath(Path.Combine(dir, opt.Sims.WorkDir));
        opt.Log = Console.WriteLine;

        using var host = new ServerHost(opt);
        try { host.Start(); }
        catch (Exception e)
        {
            Console.WriteLine($"could not start: {e.Message}");
            return 1;
        }
        Console.WriteLine(opt.Sims.Usable
            ? $"map sims on (max {opt.Sims.MaxSims}): {opt.Sims.GameDol}"
            : "map sims off (set Sims.DolphinPath and Sims.GameDol in server.json)");
        Console.WriteLine("commands: list, save, stop");
        var quit = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
        while (!quit.IsSet)
        {
            string? line = Console.ReadLine();
            if (line == null) { quit.Wait(); break; } // no console input (service): run until killed
            switch (line.Trim().ToLowerInvariant())
            {
                case "stop": case "quit": case "exit": quit.Set(); break;
                case "save": host.SaveAll(); Console.WriteLine("saved"); break;
                case "list":
                    foreach (var p in host.PartiesSnapshot())
                    {
                        Console.WriteLine($"party {p.Name}: {string.Join(", ", p.Members.Select(m => $"{m.Name}@{PlayerState.SceneName(m.Scene)}"))}");
                        Console.WriteLine($"  sims: {host.Sims.Describe(p).ToJsonString()}  world: {p.World.ToJson(onlyTags: true).ToJsonString()}");
                    }
                    break;
            }
        }
        Console.WriteLine("stopping...");
        return 0;
    }

    /// <summary>Server next to a BFBB Online install: ..\Dolphin.exe and ..\game\sys\main.dol.</summary>
    static void GuessGamePaths(SimOptions s, string dir)
    {
        foreach (var d in new[] { dir, Path.Combine(dir, ".."), Path.Combine(dir, "..", "..") })
        {
            var dolphin = Path.GetFullPath(Path.Combine(d, "Dolphin.exe"));
            if (File.Exists(dolphin) && s.DolphinPath.Length == 0) s.DolphinPath = dolphin;
            var game = Path.GetFullPath(Path.Combine(d, "BFBB-Online", "game", "sys", "main.dol"));
            if (File.Exists(game) && s.GameDol.Length == 0) s.GameDol = game;
            game = Path.GetFullPath(Path.Combine(d, "game", "sys", "main.dol"));
            if (File.Exists(game) && s.GameDol.Length == 0) s.GameDol = game;
        }
    }
}
