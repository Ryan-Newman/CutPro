using System.Text.Json;
using System.Windows;

namespace CutPro;

public partial class App : Application
{
    /// <summary>Headless mode: CutPro.exe --export project.cutpro out.mp4  (used for automated testing / batch render).</summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        var a = e.Args;
        DispatcherUnhandledException += (_, ex) =>
        {   // keep a crash log for bug reports: %LOCALAPPDATA%\CutPro\crash.log
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"), $"--- {DateTime.Now:s}\n{ex.Exception}\n");
            }
            catch { }
        };
        // pixel shaders: the tests and screenshots render with RenderTargetBitmap (WPF's software renderer: pixel centres at 0), and so does a PC
        // without a usable GPU on screen (Pin.Half)
        if (a.Length >= 1 && (a[0].EndsWith("test") && a[0] != "--playtest" || a[0] == "--uishot") || (System.Windows.Media.RenderCapability.Tier >> 16) == 0) Pin.Half = 0;
        if (a.Length >= 2 && a[0] == "--selftest")
        {
            int rc;
            try { rc = SelfTest.Run(a[1]); } catch (Exception ex) { File.WriteAllText(a[1], ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--samprobe")
        {
            int rc;
            try { rc = ObjectTest.RunProbe(a[1]); } catch (Exception ex) { File.WriteAllText(a[1], ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--animtest")
        {
            int rc;
            try { rc = await AnimTest.Run(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "animtest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--seqtest")
        {
            int rc;
            try { rc = await SeqTest.Run(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "seqtest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--contenttest")
        {
            int rc;
            try { rc = await ContentTest.Run(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "contenttest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--colortest")
        {
            int rc;
            try { rc = await ColorTest.Run(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "colortest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--studiotest")
        {
            int rc;
            try { rc = await StudioTest.Run(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "studiotest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--stabtest")
        {
            int rc;
            try { rc = await StabTest.Run(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "stabtest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--adjusttest")
        {
            int rc;
            try { rc = await AdjustTest.Run(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "adjusttest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--captiontest")
        {
            int rc;
            try { rc = await CaptionTest.Run(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "captiontest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--nesttest")
        {
            int rc;
            try { rc = await NestTest.Run(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "nesttest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--compositetest")
        {
            int rc;
            try { rc = await CompositeTest.Run(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "compositetest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--speedtest")
        {
            int rc;
            try { rc = await SpeedTest.Run(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "speedtest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--audiotest")
        {
            int rc;
            try { rc = await AudioTest.Run(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "audiotest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--transtest")
        {
            int rc;
            try { rc = await TransTest.Run(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "transtest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--materials")
        {
            Materials.Sheet(a[1]);
            Shutdown(0); return;
        }
        if (a.Length >= 2 && a[0] == "--makeicon")
        {
            IconArt.Make(a[1]);
            Shutdown(0); return;
        }
        if (a.Length >= 2 && a[0] == "--uploadtest")
        {
            int rc;
            try { rc = await UploadTest.Run(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "uploadtest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--objtest")
        {
            int rc;
            try { rc = await ObjectTest.RunE2E(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "objtest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 2 && a[0] == "--fxtest")
        {
            int rc;
            try { rc = await FxTest.Run(a[1]); } catch (Exception ex) { Directory.CreateDirectory(a[1]); File.WriteAllText(Path.Combine(a[1], "fxtest.txt"), ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 3 && a[0] == "--probetest")
        {
            int rc;
            try { rc = await ProbeTest.Run(a[1], a[2]); } catch (Exception ex) { File.WriteAllText(a[2], ex.ToString()); rc = 2; }
            Shutdown(rc); return;
        }
        if (a.Length >= 3 && a[0] == "--export")
        {
            int code = 1;
            try
            {
                var pf = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(a[1]))!;
                var ed = new Editor { Media = pf.Media, MaskTracks = pf.MaskTracks ?? new() };
                ed.UseSequences(pf.AllSequences(), pf.Active);                   // (the sequence that was open; a version 1 file is one)
                var log = new System.Text.StringBuilder();
                foreach (var m in ed.Media) { bool ok = await MediaProbe.FillAsync(m); log.AppendLine($"{m.Name} ok={ok} err={MediaProbe.LastError} dur={m.Duration:0.00} {m.Width}x{m.Height} audio={m.HasAudio}"); }
                File.WriteAllText(a[2] + ".media.txt", log.ToString());
                var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
                var titles = TitleVisual.PrepareExport(ed, a[2] + "_titles");
                var maskFiles = MaskExport.Prepare(ed, a[2] + "_titles");
                var err = await Exporter.RunAsync(ff, ed, a[2], new Progress<double>(), CancellationToken.None, titles, maskFiles);
                try { Directory.Delete(a[2] + "_titles", true); } catch { }
                if (err == null) { Console.WriteLine("OK"); code = 0; } else File.WriteAllText(a[2] + ".error.txt", err);
            }
            catch (Exception ex) { File.WriteAllText(a[2] + ".error.txt", ex.ToString()); }
            Shutdown(code);
            return;
        }
        // avoid blocking gen2 collections during playback/editing (decoder buffers are pooled, see FramePool)
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        base.OnStartup(e);
        ThemeManager.Apply(a.Length >= 3 && a[0] == "--uishot" ? a[2] : Environment.GetEnvironmentVariable("CUTPRO_THEME") ?? AppSettings.Current.Theme);   // CUTPRO_THEME: tests in another theme   // editor colour theme (default: Dark Theme)
        if (a.Length >= 2 && a[0] is "--uishot" or "--objui" or "--playtest" or "--docktest" or "--srctest") AppSettings.ReadOnly = true;   // automated runs leave the saved settings alone
        var w = new MainWindow(); w.Show();
        if (a.Length >= 2 && a[0] == "--uishot") w.UiShot(a[1]);
        if (a.Length >= 2 && a[0] == "--objui") w.ObjScenario(a[1], a.Length > 2 ? a[2] : null);
        if (a.Length >= 2 && a[0] == "--docktest") w.DockScenario(a[1]);
        if (a.Length >= 2 && a[0] == "--srctest") w.SourceScenario(a[1]);
        if (a.Length >= 2 && a[0] == "--playtest") w.StartPlaytest(a[1], a.Length > 2 && a[2] != "-" ? a[2] : null, a.Length > 3 ? a[3] : "basic");
    }
}
