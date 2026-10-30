using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace CutPro;

/// <summary>Screen and webcam recording through ffmpeg (gdigrab / dshow) into an H.264 file that lands in the Project panel like any import.
/// Preview and export then read that file — the same path as a camera card.</summary>
public static class Capture
{
    public static List<string> Cameras(string ffmpeg)
    {
        var list = new List<string>();
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(12000)) try { p.Kill(true); } catch { }
            bool video = false;
            foreach (var line in err.Split('\n'))
            {
                if (line.Contains("DirectShow video", StringComparison.OrdinalIgnoreCase)) { video = true; continue; }
                if (line.Contains("DirectShow audio", StringComparison.OrdinalIgnoreCase)) { video = false; continue; }
                if (!video) continue;
                var m = Regex.Match(line, "\"([^\"]+)\"");
                if (m.Success) list.Add(m.Groups[1].Value);
            }
        }
        catch { }
        return list;
    }

    /// <summary>Records until cancelled. Null = the output file; otherwise an error.</summary>
    public static string? Run(string ffmpeg, IEnumerable<string> input, string output, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-y");
        foreach (var a in input) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-pix_fmt", "yuv420p", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", output }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) { err.AppendLine(e.Data); if (err.Length > 4000) err.Remove(0, err.Length - 4000); } };
        p.BeginErrorReadLine();
        using var reg = ct.Register(() =>
        {
            try
            {
                if (p.HasExited) return;
                try { p.StandardInput.Write('q'); p.StandardInput.Flush(); } catch { }
                if (!p.WaitForExit(2500)) p.Kill(true);
            }
            catch { }
        });
        p.WaitForExit();
        if (ct.IsCancellationRequested) return File.Exists(output) && new FileInfo(output).Length > 2000 ? null : "Cancelled";
        if (p.ExitCode != 0 && !(File.Exists(output) && new FileInfo(output).Length > 2000))
            return string.IsNullOrWhiteSpace(err.ToString()) ? "Recording failed." : err.ToString().Trim();
        return File.Exists(output) && new FileInfo(output).Length > 2000 ? null : "Nothing was recorded.";
    }
}

public partial class MainWindow
{
    string CaptureDir() => projectPath != null ? Path.Combine(Path.GetDirectoryName(projectPath)!, "Capture")
                                               : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppInfo.Name, "Capture");

    async void RecordScreen()
    {
        var ff = Exporter.FindFfmpeg();
        if (ff == null) { Alert.Show(this, "Recording needs ffmpeg.exe.", "Record Screen", AlertKind.Warning); return; }
        var fps = new ComboBox { ItemsSource = new[] { "24", "25", "30", "60" }, SelectedItem = "30", Width = 80, Margin = new Thickness(0, 8, 0, 0) };
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = "Records the whole desktop (this window stays in front). Stop when you are done — the take lands in the Project panel.", TextWrapping = TextWrapping.Wrap });
        p.Children.Add(new TextBlock { Text = "Frame rate", Margin = new Thickness(0, 10, 0, 0) }); p.Children.Add(fps);
        if (Alert.Show(this, "", "Record Screen", AlertKind.Question, new[] { "Start", "Cancel" }, p) != 0) return;
        string rate = fps.SelectedItem as string ?? "30";
        await RecordToBin("Screen", new[] { "-f", "gdigrab", "-framerate", rate, "-draw_mouse", "1", "-i", "desktop", "-an" }, "Record Screen");
    }

    async void RecordWebcam()
    {
        var ff = Exporter.FindFfmpeg();
        if (ff == null) { Alert.Show(this, "Recording needs ffmpeg.exe.", "Record Webcam", AlertKind.Warning); return; }
        var cams = await Task.Run(() => Capture.Cameras(ff));
        if (cams.Count == 0) { Alert.Show(this, "No webcam was found. Connect one (or allow camera access in Windows' privacy settings) and try again.", "Record Webcam", AlertKind.Warning); return; }
        var cam = new ComboBox { ItemsSource = cams, SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 0) };
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = "Records the camera until you stop. The take lands in the Project panel.", TextWrapping = TextWrapping.Wrap });
        p.Children.Add(new TextBlock { Text = "Camera", Margin = new Thickness(0, 10, 0, 0) }); p.Children.Add(cam);
        if (Alert.Show(this, "", "Record Webcam", AlertKind.Question, new[] { "Start", "Cancel" }, p) != 0) return;
        string name = cam.SelectedItem as string ?? cams[0];
        await RecordToBin("Webcam", new[] { "-f", "dshow", "-rtbufsize", "256M", "-i", "video=" + name, "-an" }, "Record Webcam");
    }

    async Task RecordToBin(string kind, string[] input, string title)
    {
        Pause();
        var file = Path.Combine(CaptureDir(), $"{kind} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        var ff = Exporter.FindFfmpeg()!;
        var cts = new CancellationTokenSource();
        var dlg = new Alert.ProgressDialog(this, title, "Recording…  Stop when you are done.", () => cts.Cancel()) { IsIndeterminate = true };
        string? err;
        try { err = await Task.Run(() => Capture.Run(ff, input, file, cts.Token)); }
        catch (Exception ex) { err = ex.Message; }
        finally { dlg.Close(); }
        if (err == "Cancelled" && File.Exists(file) && new FileInfo(file).Length > 2000) err = null;
        if (err != null) { if (err != "Cancelled") Alert.Show(this, err, title, AlertKind.Error); try { File.Delete(file); } catch { } return; }
        var m = await MediaProbe.ProbeAsync(file);
        if (m == null) { Alert.Show(this, MediaProbe.LastError ?? "The recording could not be read.", title, AlertKind.Error); return; }
        if (!ed.Bins.Contains("Capture")) ed.Bins.Add("Capture");
        m.Bin = "Capture";
        ed.Media.Add(m);
        RefreshBin();
        SetTask($"{kind} {m.Duration:0.0} s — {Path.GetFileName(file)}", 5000);
    }
}
