using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using BfbbOnline.Server;
using BfbbOnline.Shared;

namespace BfbbOnline.Client;

static partial class SelfTest
{
    /// <summary>
    /// --selftest --monitors [--seconds 30]: the launcher's "Game screen" choice. For every monitor,
    /// windowed and fullscreen, starts the game through Session (the launcher's code path, D3D11
    /// window) and checks that all of Dolphin's windows end up on that monitor and inside it. Also
    /// checks that a saved choice is found again after Windows renumbers the displays.
    /// </summary>
    static void MonitorTest(string dolphin, string work, System.Collections.Generic.Dictionary<string, string> a)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);   // as the launcher (ApplicationConfiguration)
        GameScreen.DpiAwareThread();
        var screens = Screen.AllScreens;
        Log($"{screens.Length} screens: " + string.Join("; ", screens.Select(s => $"{s.DeviceName} {(s.Primary ? "main " : "")}{s.Bounds} work {s.WorkingArea}")));
        int seconds = int.Parse(a.GetValueOrDefault("seconds", "30"));

        // a saved choice survives renumbering (\\.\DISPLAY2 -> \\.\DISPLAY9) and an unplugged screen
        foreach (var s in screens)
        {
            var c = new Config(); GameScreen.Remember(c, s);
            c.Monitor = @"\\.\DISPLAY99";
            var found = GameScreen.Resolve(c, screens);
            Result($"saved choice found after renumbering ({s.DeviceName})", found?.DeviceName == s.DeviceName, $"found {found?.DeviceName ?? "none"}");
        }
        {
            var c = new Config { Monitor = @"\\.\DISPLAY99", MonitorBounds = "-9999,-9999,640,480" };
            Result("unplugged screen falls back to automatic", GameScreen.Resolve(c, screens) == null, "no screen chosen");
        }

        foreach (bool full in new[] { false, true })
            foreach (var s in screens)
            {
                string label = $"game on {s.DeviceName}{(s.Primary ? " (main)" : "")} {(full ? "fullscreen" : "windowed")}";
                var user = Path.Combine(work, "user-monitor");
                if (Directory.Exists(user)) try { Directory.Delete(user, true); } catch { }
                SimProfile.Write(user);
                var ini = Path.Combine(user, "Config", "Dolphin.ini");
                File.WriteAllText(ini, File.ReadAllText(ini).Replace("GFXBackend = Null", "GFXBackend = D3D11")
                    .Replace("RenderWindowWidth = 160", "RenderWindowWidth = 640").Replace("RenderWindowHeight = 120", "RenderWindowHeight = 480")
                    .Replace("Fullscreen = False", full ? "Fullscreen = True" : "Fullscreen = False"));
                var cfg = new Config { Name = "M", Party = "monitors", Server = "127.0.0.1", Port = 1 };
                GameScreen.Remember(cfg, s);
                var sess = new Session(cfg, m => Log($"[M] {m}")) { UserDir = user, BootScene = PlayerState.SceneIdOf("HB01") };
                Process? p = null;
                try
                {
                    sess.LaunchGame(dolphin);
                    p = Process.GetProcessById(sess.DolphinPid);
                    string last = "no window";
                    bool ok = false;
                    var until = DateTime.UtcNow.AddSeconds(seconds);
                    int stable = 0;
                    while (DateTime.UtcNow < until && !p.HasExited)
                    {
                        Thread.Sleep(500);
                        var wins = GameScreen.Windows(p);
                        // the render window is the one with the game; a fullscreen one covers the monitor
                        bool good = wins.Count > 0 && wins.All(w => w.device == s.DeviceName && Inside(w.r, s.Bounds));
                        last = string.Join(", ", wins.Select(w => $"{w.r} on {w.device}"));
                        stable = good ? stable + 1 : 0;
                        if (stable >= 10 && DateTime.UtcNow > until.AddSeconds(-seconds / 2.0)) { ok = true; break; }   // still right 5 s later, after boot
                    }
                    if (full && ok)
                    {
                        var wins = GameScreen.Windows(p);
                        ok = wins.Any(w => w.r.Width >= s.Bounds.Width - 2 && w.r.Height >= s.Bounds.Height - 2);
                        if (!ok) last += " (no window covers the monitor)";
                    }
                    Shot(new Bot { Name = "M", S = sess }, $"monitor-{s.DeviceName.Trim('\\', '.')}-{(full ? "full" : "win")}");
                    Result(label, ok, last);
                }
                catch (Exception e) { Result(label, false, e.Message); }
                finally
                {
                    try { sess.Dispose(); } catch { }
                    try { if (p != null && !p.HasExited) p.Kill(); } catch { }
                    Thread.Sleep(2000);
                }
            }
    }

    static bool Inside(System.Drawing.Rectangle r, System.Drawing.Rectangle m)
    {
        // window frames stick out a few invisible pixels
        var g = m; g.Inflate(16, 16);
        return g.Contains(r);
    }
}
