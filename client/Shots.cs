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
    [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32")] static extern uint GetCurrentThreadId();
    [DllImport("user32")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    /// <summary>Windows refuses focus to a background process: attach to the foreground thread and tap Alt first.</summary>
    static bool ForceForeground(IntPtr h)
    {
        if (GetForegroundWindow() == h) return true;
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out _), me = GetCurrentThreadId();
        keybd_event(0x12, 0, 0, IntPtr.Zero); keybd_event(0x12, 0, 2, IntPtr.Zero);
        AttachThreadInput(me, fg, true);
        ShowWindow(h, 9);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        AttachThreadInput(me, fg, false);
        for (int i = 0; i < 10 && GetForegroundWindow() != h; i++) System.Threading.Thread.Sleep(50);
        return GetForegroundWindow() == h;
    }
    [DllImport("user32")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    static bool dpiSet;
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    public static bool Capture(int pid, string path, bool fromScreen = false)
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
        if (fromScreen)
        {
            // topmost for the copy: a window the person clicks meanwhile can't cover it
            SetWindowPos(best, new IntPtr(-1), 0, 0, 0, 0, 0x13);   // HWND_TOPMOST, NOSIZE|NOMOVE|NOACTIVATE
            ForceForeground(best);
            try {
            // PrintWindow can miss D3D models: copy the window's client area from the desktop instead
            System.Threading.Thread.Sleep(250);
            GetClientRect(best, out var cr);
            var tl = new POINT();
            ClientToScreen(best, ref tl);
            using var sb = new Bitmap(Math.Max(1, cr.R), Math.Max(1, cr.B));
            using (var g = Graphics.FromImage(sb)) g.CopyFromScreen(tl.X, tl.Y, 0, 0, sb.Size);
            sb.Save(path, ImageFormat.Png);
            return true;
            } finally { SetWindowPos(best, new IntPtr(-2), 0, 0, 0, 0, 0x13); }
        }
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

    /// <summary>
    /// The frame the console is showing, read from emulated RAM (the VI's current XFB, YUYV 4:2:2):
    /// needs Dolphin's "XFB copies to texture only" off. Works with the window off-screen or covered,
    /// no focus or desktop capture involved.
    /// </summary>
    public static bool CaptureXfb(BfbbOnline.Shared.DolphinMemory m, string path, int w = 640, int h = 480)
    {
        uint a = m.ReadU32(0x803CC674);   // CurrBufAddr (VI)
        if (a == 0) return false;
        if (a < 0x80000000) a |= 0x80000000;
        var raw = m.Read(a, w * h * 2);
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        var row = new byte[bd.Stride];
        static byte C(double v) => (byte)Math.Clamp((int)(v + 0.5), 0, 255);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x += 2)
            {
                int o = (y * w + x) * 2;
                double y0 = raw[o], u = raw[o + 1] - 128, y1 = raw[o + 2], v = raw[o + 3] - 128;
                for (int k = 0; k < 2; k++)
                {
                    double yy = 1.164 * ((k == 0 ? y0 : y1) - 16);
                    int p = (x + k) * 3;
                    row[p] = C(yy + 2.018 * u);                 // B
                    row[p + 1] = C(yy - 0.391 * u - 0.813 * v); // G
                    row[p + 2] = C(yy + 1.596 * v);             // R
                }
            }
            Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, bd.Stride);
        }
        bmp.UnlockBits(bd);
        bmp.Save(path, ImageFormat.Png);
        return true;
    }
}
