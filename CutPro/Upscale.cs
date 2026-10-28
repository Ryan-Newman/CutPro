using System.Diagnostics;
using System.Globalization;

namespace CutPro;

/// <summary>Project panel ▸ Create Upscaled Copy: a video or picture at 2× (or 4×) its size — Lanczos scaling with a light unsharp mask, sound
/// copied — as a new project item next to the original (Resolve's Super Scale, without a neural network). Small footage in a large sequence
/// then exports from more pixels than its own; preview and export read the copy like any media.</summary>
public static class Upscale
{
    public static string? DirOverride;                                       // (tests)
    static string Dir => DirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro", "Upscaled");

    /// <summary>The copy's file: in the original's folder when it can be written there, else CutPro's own folder.</summary>
    public static string TargetOf(MediaItem m, int factor)
    {
        string stem = Path.GetFileNameWithoutExtension(m.Path) + $" ({factor}x)", ext = m.Type == ClipKind.Image ? ".png" : ".mp4";
        string? folder = DirOverride ?? Path.GetDirectoryName(m.Path);
        try { if (folder == null || !Directory.Exists(folder)) folder = Dir; var probe = Path.Combine(folder, ".cutpro_write_test"); File.WriteAllText(probe, ""); File.Delete(probe); }
        catch { folder = Dir; }
        Directory.CreateDirectory(folder!);
        var f = Path.Combine(folder!, stem + ext);
        for (int i = 2; File.Exists(f); i++) f = Path.Combine(folder!, $"{stem} {i}{ext}");
        return f;
    }

    public static async Task<string> MakeAsync(MediaItem m, int factor, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        var outF = TargetOf(m, factor);
        int w = m.Width * factor / 2 * 2, h = m.Height * factor / 2 * 2;
        string vf = $"scale={w}:{h}:flags=lanczos+accurate_rnd+full_chroma_int,unsharp=5:5:{(factor >= 4 ? "0.8" : "0.5")}:5:5:0";
        var args = m.Type == ClipKind.Image
            ? new List<string> { "-y", "-v", "error", "-i", m.Path, "-vf", vf, "-frames:v", "1", outF }
            : new List<string> { "-y", "-v", "error", "-progress", "pipe:1", "-nostats", "-i", m.Path, "-map", "0:v:0", "-map", "0:a?", "-vf", vf + ",format=yuv420p",
                                 "-c:v", "libx264", "-crf", "12", "-preset", "medium", "-c:a", "copy", "-movflags", "+faststart", outF };
        var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync()) != null)
        {
            if (ct.IsCancellationRequested) { try { p.Kill(); } catch { } break; }
            if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && m.Duration > 0)
                progress?.Report(Math.Clamp(us / 1e6 / m.Duration, 0, 1));
        }
        await p.WaitForExitAsync();
        if (ct.IsCancellationRequested) { try { File.Delete(outF); } catch { } throw new OperationCanceledException(); }
        if (p.ExitCode != 0 || !File.Exists(outF))
        {   // (audio that can't be copied into MP4: encode it)
            var e = await err;
            if (m.Type != ClipKind.Image && e.Contains("codec", StringComparison.OrdinalIgnoreCase))
            {
                args[args.IndexOf("copy")] = "aac";
                psi.ArgumentList.Clear(); foreach (var a in args) psi.ArgumentList.Add(a);
                using var p2 = Process.Start(psi)!;
                p2.StandardOutput.ReadToEnd(); var e2 = p2.StandardError.ReadToEnd(); p2.WaitForExit();
                if (p2.ExitCode != 0 || !File.Exists(outF)) throw new Exception(e2.Trim().Split('\n')[^1]);
            }
            else throw new Exception(e.Trim().Split('\n').LastOrDefault() ?? "ffmpeg failed");
        }
        return outF;
    }
}

public partial class MainWindow
{
    async Task UpscaleCopy(MediaItem m, int factor)
    {
        if (m.Type is not (ClipKind.Video or ClipKind.Image) || m.Seq != null) return;
        SetTask($"Upscaling “{m.Name}” {factor}×…", 0);
        try
        {
            var f = await Upscale.MakeAsync(m, factor, new Progress<double>(x => SetTask($"Upscaling “{m.Name}” {factor}×… {x:0%}", 0)));
            var nm = await MediaProbe.ProbeAsync(f);
            if (nm == null) throw new Exception(MediaProbe.LastError ?? "The copy could not be read.");
            nm.Bin = m.Bin; nm.Label = m.Label; nm.Keywords = m.Keywords;
            ed.Media.Add(nm); binRef = null; ed.RaiseChanged(); RefreshBin();
            BinList.SelectedItem = nm;
            SetTask($"“{nm.Name}” ({nm.Width}×{nm.Height}) added — Replace Selected Clip with This swaps it into the timeline", 4000);
        }
        catch (Exception ex) { SetTask("", 1); Alert.Show(this, ex.Message, "Create Upscaled Copy", AlertKind.Error); }
    }
}
