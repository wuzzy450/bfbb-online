using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BfbbOnline.Shared;

/// <summary>
/// Reads and writes the emulated GameCube RAM (MEM1, 24 MB mirrored in a 32 MB view) of a running
/// Dolphin process, the same way Dolphin Memory Engine does: find the MEM_MAPPED region of size
/// 0x2000000 that starts with the game id.
/// </summary>
public sealed class DolphinMemory : IDisposable
{
    const uint PROCESS_VM_READ = 0x10, PROCESS_VM_WRITE = 0x20, PROCESS_VM_OPERATION = 0x08,
        PROCESS_QUERY_INFORMATION = 0x400;
    const uint MEM_COMMIT = 0x1000, MEM_MAPPED = 0x40000;

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] static extern UIntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, UIntPtr len);
    [DllImport("kernel32", SetLastError = true)] static extern unsafe bool ReadProcessMemory(IntPtr h, IntPtr addr, byte* buf, UIntPtr n, out UIntPtr read);
    [DllImport("kernel32", SetLastError = true)] static extern unsafe bool WriteProcessMemory(IntPtr h, IntPtr addr, byte* buf, UIntPtr n, out UIntPtr written);

    readonly IntPtr handle;
    readonly IntPtr mem1;
    readonly uint mask;

    /// <summary>
    /// Dolphin options every BFBB Online game runs with: 32 MB MEM1 (RAM override; the OS maps 32 MB).
    /// The game itself only uses 24 MB; the patch puts the teammates' player models and 6 MB of extra
    /// OS heap above that (see bbol.c).
    /// </summary>
    public static string[] LaunchArgs => ExtraRam
        ? new[] { "-C", "Dolphin.Core.RAMOverrideEnable=True", "-C", "Dolphin.Core.MEM1Size=33554432" }
        : new[] { "-C", "Dolphin.Core.RAMOverrideEnable=False" };
    /// <summary>Tests only: false runs the stock 24 MB.</summary>
    public static bool ExtraRam = true;
    public int ProcessId { get; }

    DolphinMemory(int pid, IntPtr h, IntPtr mem1, long size)
    {
        mask = (uint)(size - 1);
        ProcessId = pid;
        handle = h;
        this.mem1 = mem1;
    }

    /// <summary>Attaches to the given Dolphin process once its MEM1 holds a game with this id.</summary>
    public static DolphinMemory? TryAttach(int pid, string gameId = "GQPE78")
    {
        var h = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        long addr = 0;
        var size = (UIntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        Span<byte> id = stackalloc byte[6];
        while (VirtualQueryEx(h, (IntPtr)addr, out var mbi, size) != UIntPtr.Zero)
        {
            long regionSize = (long)(ulong)mbi.RegionSize;
            if (mbi.Type == MEM_MAPPED && mbi.State == MEM_COMMIT && (regionSize == 0x2000000 || regionSize == 0x4000000))
            {
                var probe = new DolphinMemory(pid, h, mbi.BaseAddress, regionSize);
                if (probe.TryRead(0x80000000, id) && System.Text.Encoding.ASCII.GetString(id) == gameId)
                    return probe;
            }
            long next = (long)mbi.BaseAddress + regionSize;
            if (next <= addr) break;
            addr = next;
        }
        CloseHandle(h);
        return null;
    }

    public bool IsAlive
    {
        get
        {
            try { return !Process.GetProcessById(ProcessId).HasExited; }
            catch { return false; }
        }
    }

    IntPtr Host(uint gcAddr) => mem1 + (int)(gcAddr & mask);

    public unsafe bool TryRead(uint gcAddr, Span<byte> dst)
    {
        fixed (byte* p = dst)
            return ReadProcessMemory(handle, Host(gcAddr), p, (UIntPtr)dst.Length, out var n) && (int)(ulong)n == dst.Length;
    }

    public unsafe bool TryWrite(uint gcAddr, ReadOnlySpan<byte> src)
    {
        fixed (byte* p = src)
            return WriteProcessMemory(handle, Host(gcAddr), p, (UIntPtr)src.Length, out var n) && (int)(ulong)n == src.Length;
    }

    public byte[] Read(uint gcAddr, int len)
    {
        var b = new byte[len];
        if (!TryRead(gcAddr, b)) throw new InvalidOperationException($"read {gcAddr:X8} failed");
        return b;
    }

    public uint ReadU32(uint gcAddr)
    {
        Span<byte> b = stackalloc byte[4];
        if (!TryRead(gcAddr, b)) throw new InvalidOperationException($"read {gcAddr:X8} failed");
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    public void WriteU32(uint gcAddr, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        if (!TryWrite(gcAddr, b)) throw new InvalidOperationException($"write {gcAddr:X8} failed");
    }

    public void Dispose() => CloseHandle(handle);
}
