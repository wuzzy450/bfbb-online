using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BfbbOnline.Shared;

namespace BfbbOnline.Client;

/// <summary>
/// Makes the online-patched copy of BFBB Deluxe: game\sys (small files copied, main.dol patched)
/// and game\files (a junction to the Deluxe files Heavy Mod Manager built, nothing copied).
/// Nothing of the game is shipped with BFBB Online; it patches the player's own copy.
/// </summary>
public static class Installer
{
    public static PatchSpec Spec { get; } = LoadSpec();

    static PatchSpec LoadSpec()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("bbol_patch.json")
            ?? throw new InvalidOperationException("patch missing from the build");
        return PatchSpec.Load(s);
    }

    public static string AppDir => AppContext.BaseDirectory;
    public static string GameDir => Path.Combine(AppDir, "game");
    public static string GameDol => Path.Combine(GameDir, "sys", "main.dol");

    /// <summary>Dolphin.exe next to us or one folder up (BFBB-Online inside the Dolphin folder).</summary>
    public static string? FindDolphin(string? configured)
    {
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
        var d = new DirectoryInfo(AppDir);
        for (int up = 0; d != null && up < 8; up++, d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "Dolphin.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>The Deluxe game folder (has sys\main.dol and files\) Heavy Mod Manager builds.</summary>
    public static string? FindDeluxe(string? configured, string? dolphin)
    {
        var tries = new List<string>();
        if (!string.IsNullOrEmpty(configured)) tries.Add(configured);
        var roots = new List<string> { AppDir, Path.Combine(AppDir, "..") };
        for (var d = new DirectoryInfo(AppDir).Parent; d != null && roots.Count < 9; d = d.Parent) roots.Add(d.FullName);
        if (dolphin != null) roots.Add(Path.GetDirectoryName(dolphin)!);
        foreach (var r in roots.Select(Path.GetFullPath).Distinct())
        {
            if (!Directory.Exists(r)) continue;
            foreach (var hmm in Directory.GetDirectories(r, "HeavyModManager*"))
                tries.Add(Path.Combine(hmm, "Games", "gc", "bfbb", "game"));
        }
        foreach (var t in tries)
            if (IsGameFolder(t)) return Path.GetFullPath(t);
        return null;
    }

    public static bool IsGameFolder(string dir) =>
        File.Exists(Path.Combine(dir, "sys", "main.dol")) && Directory.Exists(Path.Combine(dir, "files"));

    public static string Status(string deluxeDir)
    {
        var dol = File.ReadAllBytes(Path.Combine(deluxeDir, "sys", "main.dol"));
        var sha = DolPatcher.Sha1(dol);
        return Spec.BaseDolSha1.Contains(sha) ? "BFBB Deluxe 1.1 found" : $"unknown main.dol ({sha[..8]}), trying anyway";
    }

    /// <summary>Builds (or refreshes) game\. Cheap: run before every launch.</summary>
    public static void Install(string deluxeDir, Action<string> log)
    {
        var srcSys = Path.Combine(deluxeDir, "sys");
        var dstSys = Path.Combine(GameDir, "sys");
        Directory.CreateDirectory(dstSys);
        foreach (var f in new[] { "apploader.img", "bi2.bin", "boot.bin", "fst.bin" })
            File.Copy(Path.Combine(srcSys, f), Path.Combine(dstSys, f), true);

        var dol = File.ReadAllBytes(Path.Combine(srcSys, "main.dol"));
        var patched = DolPatcher.Apply(dol, Spec);
        if (!File.Exists(GameDol) || !File.ReadAllBytes(GameDol).AsSpan().SequenceEqual(patched))
        {
            File.WriteAllBytes(GameDol, patched);
            log($"patched main.dol (BFBB Online patch v{Spec.Version}, {Spec.Size / 1024} KB)");
        }

        var files = Path.Combine(GameDir, "files");
        var target = Path.Combine(deluxeDir, "files");
        var info = new DirectoryInfo(files);
        if (info.Exists && info.LinkTarget != null && !PathsEqual(info.LinkTarget, target))
            Directory.Delete(files);
        if (!Directory.Exists(files))
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(files);
            psi.ArgumentList.Add(target);
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            if (!Directory.Exists(files)) throw new IOException("could not link the game files folder");
            log("linked the Deluxe game files");
        }
    }

    static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
