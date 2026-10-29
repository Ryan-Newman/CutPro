using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CutPro;

/// <summary>
/// Proxies (Premiere's proxy workflow): a small copy of a large video (540p or 720p H.264, short GOP for quick seeks, same frames and times)
/// made in the background. With proxies on (the Program monitor's Proxies button), the preview decodes the proxy instead of the original —
/// 4K / 8K plays like HD; the export, renders (speed, stabilizing, nesting), frame grabs and analysis always read the original. Proxies live in
/// %LOCALAPPDATA%\CutPro\Proxies (named by the source file) or anywhere when attached by hand.
/// </summary>
public static class Proxies
{
    public static string? DirOverride;                                       // (tests)
    public static string Dir => DirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro", "Proxies");
    /// <summary>The preview decodes proxies (the toggle).</summary>
    public static bool Use;
    /// <summary>The file the preview decodes for this media: its proxy when proxies are on and it is there (cached flag, no disk access).</summary>
    public static string PathOf(MediaItem m) => Use && m.ProxyOk ? m.Proxy! : m.Path;

    public static string FileFor(MediaItem m, int height)
    {
        var fi = new FileInfo(m.Path);
        var key = AudioCache.Hash($"{m.Path.ToLowerInvariant()}|{(fi.Exists ? fi.Length : 0)}|{(fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0)}|{height}");
        return Path.Combine(Dir, $"{Path.GetFileNameWithoutExtension(m.Path)}_{height}p_{key[..8]}.mp4");
    }

    /// <summary>Makes a proxy (picture only) at the height (never larger than the original); returns its path.</summary>
    public static async Task<string> MakeAsync(MediaItem m, int height, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        Directory.CreateDirectory(Dir);
        int h = Math.Min(height, m.Height) / 2 * 2, w = Math.Max(2, (int)Math.Round(m.Width * (double)h / Math.Max(1, m.Height) / 2) * 2);
        var outF = FileFor(m, height);
        if (File.Exists(outF)) return outF;
        var tmp = outF + ".part.mp4";
        var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-y", "-v", "error", "-progress", "pipe:1", "-nostats", "-i", m.Path, "-map", "0:v:0", "-an", "-sn", "-dn",
                                  "-vf", $"scale={w}:{h}:flags=bicubic{Hdr.Post(m)},format=yuv420p", "-fps_mode", "passthrough",
                                  "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-g", "15", "-bf", "0", "-tune", "fastdecode", "-movflags", "+faststart", tmp })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var err = p.StandardError.ReadToEndAsync();
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync()) != null)
        {
            if (ct.IsCancellationRequested) { try { p.Kill(); } catch { } break; }
            if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && m.Duration > 0)
                progress?.Report(Math.Clamp(us / 1e6 / m.Duration, 0, 1));
        }
        await p.WaitForExitAsync();
        if (ct.IsCancellationRequested) { try { File.Delete(tmp); } catch { } throw new OperationCanceledException(); }
        if (p.ExitCode != 0 || !File.Exists(tmp)) throw new Exception((await err).Trim().Split('\n').LastOrDefault() ?? "ffmpeg failed");
        File.Move(tmp, outF, true);
        return outF;
    }

    /// <summary>A file attached as a proxy must show the same thing: same aspect, about the same length.</summary>
    public static string? CheckAttach(MediaItem m, MediaItem proxy)
    {
        if (proxy.Type != ClipKind.Video) return "The proxy must be a video.";
        if (Math.Abs(proxy.Duration - m.Duration) > Math.Max(0.5, m.Duration * 0.01)) return $"The proxy is {proxy.Duration:0.0} s long, the original {m.Duration:0.0} s.";
        if (m.Width > 0 && proxy.Width > 0 && Math.Abs(proxy.Width / (double)proxy.Height - m.Width / (double)m.Height) > 0.02) return "The proxy's shape (aspect ratio) differs from the original's.";
        return null;
    }
}

public partial class MainWindow
{
    CancellationTokenSource? proxyCts;

    /// <summary>Project panel ▸ Proxy ▸ Create Proxies: one after another in the background (status bar), then the preview can use them.</summary>
    async Task CreateProxies(List<MediaItem> items, int height)
    {
        items = items.Where(m => m.Type == ClipKind.Video && m.Seq == null && !m.Offline).ToList();
        if (items.Count == 0) { SetTask("Select videos in the Project panel", 2500); return; }
        proxyCts?.Cancel(); proxyCts = new(); var ct = proxyCts.Token;
        int done = 0;
        foreach (var m in items)
        {
            try
            {
                int i = done + 1;
                m.Proxy = await Proxies.MakeAsync(m, height, new Progress<double>(x => SetTask($"Creating proxies {i} / {items.Count}: “{m.Name}” {x:0%}", 0)), ct);
                m.RefreshOnline(); done++;
                binRef = null; RefreshBin();
            }
            catch (OperationCanceledException) { SetTask("Proxies cancelled", 2000); return; }
            catch (Exception ex) { Alert.Show(this, $"“{m.Name}”: {ex.Message}", "Create Proxies", AlertKind.Warning); }
        }
        if (!Proxies.Use && done > 0) SetProxies(true);
        SetTask($"{done} prox{(done == 1 ? "y" : "ies")} ready — the Proxies button switches between proxies and the originals; exports always use the originals", 4500);
    }

    void SetProxies(bool on)
    {
        Proxies.Use = on;
        if (ProxyBtn != null) ProxyBtn.IsChecked = on;
        Pv.InvalidateVisual(); Pv.Sync(ed.Time, false);
        UpdateStatus();
    }

    /// <summary>The Project panel's Proxy submenu for the selected media.</summary>
    MenuItem ProxyMenu(List<MediaItem> sel)
    {
        var mi = new MenuItem { Header = "Proxy" };
        void Item(string h, Action a, bool en = true) { var x = new MenuItem { Header = h, IsEnabled = en }; x.Click += (_, _) => a(); mi.Items.Add(x); }
        bool vids = sel.Any(m => m.Type == ClipKind.Video && m.Seq == null);
        Item("Create Proxies (540p)", () => _ = CreateProxies(sel, 540), vids);
        Item("Create Proxies (720p)", () => _ = CreateProxies(sel, 720), vids);
        Item("Attach Proxy…", () => _ = AttachProxy(sel[0]), sel.Count == 1 && vids);
        Item("Detach Proxies", () => { foreach (var m in sel) { m.Proxy = null; m.RefreshOnline(); } binRef = null; RefreshBin(); Pv.Sync(ed.Time, false); }, sel.Any(m => m.Proxy != null));
        mi.Items.Add(new Separator());
        var t = new MenuItem { Header = "Use Proxies in the Preview", IsCheckable = true, IsChecked = Proxies.Use, InputGestureText = K("view.proxies") };
        t.Click += (_, _) => SetProxies(!Proxies.Use);
        mi.Items.Add(t);
        return mi;
    }

    async Task AttachProxy(MediaItem m)
    {
        var dlg = new OpenFileDialog { Title = $"A proxy for “{m.Name}”", Filter = MediaProbe.DialogFilter };
        if (dlg.ShowDialog(this) != true) return;
        var p = await MediaProbe.ProbeAsync(dlg.FileName);
        if (p == null) { Alert.Show(this, MediaProbe.LastError ?? "The file could not be read.", "Attach Proxy", AlertKind.Error); return; }
        if (Proxies.CheckAttach(m, p) is { } why) { Alert.Show(this, why, "Attach Proxy", AlertKind.Warning); return; }
        m.Proxy = dlg.FileName; m.RefreshOnline(); binRef = null; RefreshBin(); Pv.Sync(ed.Time, false);
    }
}
