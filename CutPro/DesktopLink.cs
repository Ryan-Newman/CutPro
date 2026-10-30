using System.IO;

namespace CutPro;

/// <summary>Puts a <see cref="AppInfo.Name"/> shortcut on the desktop (and in the Start menu) that opens this build, and removes an
/// older CutPro.lnk so the desktop shows the product name rather than the assembly name.</summary>
public static class DesktopLink
{
    public static void Ensure()
    {
        try
        {
            string? exe = ExePath();
            if (exe == null || !File.Exists(exe)) return;
            string name = AppInfo.Name + ".lnk";
            Write(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name), exe);
            Write(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", name), exe);
            foreach (var dir in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                         Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) + "\\Programs"
                     })
            {
                var old = Path.Combine(dir, "CutPro.lnk");
                if (File.Exists(old)) try { File.Delete(old); } catch { }
            }
        }
        catch { }
    }

    static string? ExePath()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "CutPro.exe");
        if (File.Exists(beside)) return Path.GetFullPath(beside);
        try { var p = Environment.ProcessPath; if (p != null && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !p.Contains("dotnet", StringComparison.OrdinalIgnoreCase)) return p; }
        catch { }
        return null;
    }

    static void Write(string lnk, string exe)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        dynamic sh = Activator.CreateInstance(t)!;
        var s = sh.CreateShortcut(lnk);
        s.TargetPath = exe;
        s.WorkingDirectory = Path.GetDirectoryName(exe);
        s.Description = AppInfo.Name;
        s.IconLocation = exe + ",0";
        s.Save();
    }
}
