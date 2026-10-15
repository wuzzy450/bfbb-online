using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BfbbOnline.Client;

/// <summary>Screen captures of a process's largest window (PW_RENDERFULLCONTENT, so D3D content is included even when covered).</summary>
static class Shots
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    static bool dpiSet;
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    public static bool Capture(int pid, string path)
    {
        // physical pixels, or a scaled display crops the capture
        if (!dpiSet) { dpiSet = true; try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } }
        IntPtr best = IntPtr.Zero; long area = 0; RECT br = default;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p == pid && IsWindowVisible(h) && GetWindowRect(h, out var r))
            {
                long a = (long)(r.R - r.L) * (r.B - r.T);
                if (a > area) { area = a; best = h; br = r; }
            }
            return true;
        }, IntPtr.Zero);
        if (best == IntPtr.Zero || area < 100) return false;
        using var bmp = new Bitmap(br.R - br.L, br.B - br.T);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr dc = g.GetHdc();
            PrintWindow(best, dc, 2);   // PW_RENDERFULLCONTENT: includes D3D, works while covered
            g.ReleaseHdc(dc);
        }
        bmp.Save(path, ImageFormat.Png);
        return true;
    }
}
