using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BfbbOnline.Client;

/// <summary>Puts the game window on the monitor picked in the launcher.</summary>
static class GameScreen
{
    /// <summary>
    /// The picked screen, or null for Dolphin's choice. Windows renumbers displays (\\.\DISPLAY9...)
    /// after replugging or docking, so a screen with the saved bounds counts too; a screen that's
    /// gone falls back to Dolphin's choice.
    /// </summary>
    public static Screen? Resolve(Config cfg, Screen[]? screens = null)
    {
        if (string.IsNullOrEmpty(cfg.Monitor)) return null;
        screens ??= Screen.AllScreens;
        var bounds = ParseBounds(cfg.MonitorBounds);
        return screens.FirstOrDefault(s => s.DeviceName == cfg.Monitor && (bounds == null || s.Bounds.Size == bounds.Value.Size))
            ?? (bounds == null ? null : screens.FirstOrDefault(s => s.Bounds == bounds.Value))
            ?? screens.FirstOrDefault(s => s.DeviceName == cfg.Monitor);
    }

    public static void Remember(Config cfg, Screen? s)
    {
        cfg.Monitor = s?.DeviceName ?? "";
        cfg.MonitorBounds = s == null ? "" : $"{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width},{s.Bounds.Height}";
    }

    static Rectangle? ParseBounds(string v)
    {
        var p = (v ?? "").Split(',');
        return p.Length == 4 && p.All(x => int.TryParse(x, out _))
            ? new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3])) : null;
    }

    /// <summary>Dolphin's own window position settings for that screen (render window separate from the main one).</summary>
    public static IEnumerable<string> DolphinArgs(Screen screen)
    {
        var b = screen.WorkingArea;
        return new[] { "-C", $"Dolphin.Display.RenderWindowXPos={b.X + 40}", "-C", $"Dolphin.Display.RenderWindowYPos={b.Y + 40}",
                       "-C", "Dolphin.Interface.RenderToMain=False" };
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWinProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out WinRect r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr m, ref MonitorInfoEx mi);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    delegate bool EnumWinProc(IntPtr h, IntPtr l);
    struct WinRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfoEx { public int Size; public WinRect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    static readonly IntPtr PerMonitorV2 = new(-4);

    /// <summary>Makes the calling thread work in physical pixels (mixed-DPI setups).</summary>
    public static void DpiAwareThread() => SetThreadDpiAwarenessContext(PerMonitorV2);

    /// <summary>Dolphin's top-level windows (no popups), with the device name of the monitor each is on.</summary>
    public static List<(IntPtr h, Rectangle r, string device)> Windows(Process p)
    {
        var list = new List<(IntPtr, Rectangle, string)>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid != (uint)p.Id || !IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return true;   // 4 = GW_OWNER: skip popups
            if (!GetWindowRect(h, out var r) || r.Right - r.Left < 200) return true;
            var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            GetMonitorInfo(MonitorFromWindow(h, 2), ref mi);   // 2 = MONITOR_DEFAULTTONEAREST
            list.Add((h, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), mi.Device ?? ""));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Moves Dolphin's windows onto the screen once they appear (Dolphin restores its own saved
    /// window position, so its config alone isn't enough, and creates the render window again when
    /// going fullscreen). Keeps each window's size where it fits. Works in physical pixels, so
    /// mixed-DPI setups and screens left of / above the main one work.
    /// </summary>
    public static void MoveWindows(Process p, Screen screen)
    {
        new Thread(() =>
        {
            DpiAwareThread();
            var target = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == screen.DeviceName) ?? screen;   // physical bounds
            var area = target.WorkingArea;
            var moved = new HashSet<IntPtr>();
            var until = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < until && !p.HasExited)
            {
                foreach (var (h, r, device) in Windows(p))
                {
                    if (!moved.Add(h) || device == target.DeviceName) continue;
                    int w = Math.Min(r.Width, area.Width), hgt = Math.Min(r.Height, area.Height);
                    SetWindowPos(h, IntPtr.Zero, area.X + (area.Width - w) / 2, area.Y + (area.Height - hgt) / 2, w, hgt, 0x0004 | 0x0010);   // NOZORDER | NOACTIVATE
                }
                Thread.Sleep(400);
            }
        }) { IsBackground = true, Name = "move game window" }.Start();
    }
}
