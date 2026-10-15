using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>CutPro.exe --transtest dir: every video transition, preview vs export at ¼, ½ and ¾ through (pictures on V1, titles
/// with transitions on V2 above them — pushes must move only the titles), single-sided transitions from / to nothing, video
/// clips extended past their media (handles / repeated frames), audio crossfades measured (constant power keeps loudness).</summary>
public static class TransTest
{
    static int W => Editor.W;
    const int H = Editor.H;

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
        try
        {
            var p1 = Path.Combine(dir, "pattern.png"); FxTest.Save(FxTest.Pattern(), p1);
            var p2 = Path.Combine(dir, "pattern2.png"); FxTest.Save(Second(), p2);
            var a = await MediaProbe.ProbeAsync(p1) ?? throw new Exception("import");
            var b = await MediaProbe.ProbeAsync(p2) ?? throw new Exception("import");
            var ed = new Editor(); ed.Media.Add(a); ed.Media.Add(b);
            string[] kinds = { "Cross Dissolve", "Wipe Left", "Push Left", "Iris Round", "Dip to Black", "Wipe Up", "Push Down", "Dip to White", "Wipe Right", "Push Up",
                               "Clock Wipe", "Diagonal Wipe", "Venetian Blinds", "Inset", "Iris Box", "Iris Diamond", "Barn Doors", "Barn Doors Horizontal",
                               "Slide Left", "Slide Right", "Slide Up", "Slide Down", "Cross Zoom", "Zoom In", "Zoom Out", "Spin In", "Spin Away" };
            // V1: alternating pictures, 2 s each, a transition at every cut (centre, 1 s); a fade in from nothing at the start
            var v1 = new List<Clip>();
            for (int i = 0; i <= kinds.Length; i++)
            {
                var c = new Clip { MediaId = (i % 2 == 0 ? a : b).Id, Kind = ClipKind.Image, Track = 2, Start = i * 2, Dur = 2 };
                if (i > 0) c.TransIn = new Transition { Kind = kinds[i - 1], Dur = 1 };
                ed.Clips.Add(c); v1.Add(c);
            }
            v1[0].TransIn = new Transition { Kind = "Cross Dissolve", Dur = 0.6 };              // from nothing (black)
            v1[^1].TransOut = new Transition { Kind = "Iris Round", Dur = 0.8 };                  // to nothing
            // V2: titles over the pictures with their own transitions (the pictures below must not move in a push)
            Clip T(double s, double d, string text) { var c = new Clip { Kind = ClipKind.Text, Track = 1, Start = s, Dur = d, Text = text, Style = StyleLibrary.Get("Bold Impact").With(st => st.Size = 120) }; ed.Clips.Add(c); return c; }
            T(1, 4, "First"); var t2 = T(5, 4, "Second"); t2.TransIn = new Transition { Kind = "Push Right", Dur = 1 };
            var t3 = T(9, 4, "Third"); t3.TransIn = new Transition { Kind = "Cross Dissolve", Dur = 1, Align = TransAlign.Start };
            // titles moved by Move transitions (their motion is moved: preview and the per-frame title export)
            T(30, 3, "Zoom").TransIn = new Transition { Kind = "Zoom In", Dur = 1 };
            T(36, 3, "Spin").TransIn = new Transition { Kind = "Spin In", Dur = 1 };
            T(42, 3, "Slide").TransIn = new Transition { Kind = "Slide Left", Dur = 1 };

            var plan = Transitions.Compute(ed);
            Check(plan.Wins.Count == kinds.Length + 7, $"{plan.Wins.Count} transition windows resolved");
            var mp4 = Path.Combine(dir, "trans.mp4");
            var tmp = Path.Combine(dir, "tmp");
            var titles = TitleVisual.PrepareExport(ed, tmp);
            var sw = Stopwatch.StartNew();
            var err = await Exporter.RunAsync(ff, ed, mp4, new Progress<double>(), CancellationToken.None, titles);
            Check(err == null, $"export ({sw.Elapsed.TotalSeconds:0.0} s)" + (err == null ? "" : ": " + err));
            if (err == null)
            {
                var pv = new Preview { Ed = ed, Width = W, Height = H, Background = Brushes.Black };
                foreach (var w in plan.Wins.OrderBy(w => w.T0))
                    foreach (double q in new[] { 0.25, 0.5, 0.75 })
                    {
                        int f = (int)Math.Round((w.T0 + q * w.Dur) * TimeUtil.Fps);
                        double t = f / TimeUtil.Fps;
                        pv.Sync(t, false);
                        pv.Measure(new Size(W, H)); pv.Arrange(new Rect(0, 0, W, H)); pv.UpdateLayout();
                        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
                        var pa = new byte[W * H * 4]; rtb.CopyPixels(pa, W * 4, 0);
                        var pb = FxTest.Frame(ff, mp4, t - 0.001);
                        var (psnr, worst) = FxTest.Compare(pa, pb);
                        string where = w.Track == 1 ? "V2 title" : "V1";
                        bool title = w.Track == 1;
                        var (sx, sy) = SeqTest.Shift(pa, pb, W, H);
                        // (spins turn the 1 px grid pattern: WPF and ffmpeg resample it differently — 23 dB, like rotated layers in --animtest;
                        //  near-identical frames (> 45 dB) can't show a shift, e.g. a mostly black iris to nothing)
                        double min = w.D.Name.StartsWith("Spin") ? 22 : 28;
                        Check(psnr >= min && worst <= 40 && (psnr > 45 || Math.Abs(sx) <= 1 && Math.Abs(sy) <= 1), $"{where} {w.T.Kind}{(w.A == null ? " (from nothing)" : w.B == null ? " (to nothing)" : "")} at {q:0.##}: PSNR {psnr:0.0} dB (≥ {min}), worst block {worst:0.0}/255, shift ({sx},{sy})");
                        FxTest.Save(FxTest.SideBySide(pa, pb), Path.Combine(dir, $"trans_{f}.png"));
                    }
            }

            // video clips: a transition shows frames past the outgoing clip's out point and before the incoming clip's in point;
            // past the end of the media the last frame repeats
            var vdir = Path.Combine(Path.GetTempPath(), "cutpro_test");
            var va = await MediaProbe.ProbeAsync(Path.Combine(vdir, "a.mp4"));
            if (va != null)
            {
                var ev = new Editor(); ev.Media.Add(va);
                ev.Clips.Add(new Clip { MediaId = va.Id, Kind = ClipKind.Video, Track = 2, Start = 0, In = 0, Dur = 2, NoAudio = true });
                ev.Clips.Add(new Clip { MediaId = va.Id, Kind = ClipKind.Video, Track = 2, Start = 2, In = 1, Dur = 2, NoAudio = true, TransIn = new Transition { Kind = "Push Left", Dur = 1 } });
                ev.Clips.Add(new Clip { MediaId = va.Id, Kind = ClipKind.Video, Track = 2, Start = 4, In = va.Duration - 1, Dur = 1, NoAudio = true, TransIn = new Transition { Kind = "Cross Dissolve", Dur = 1, Align = TransAlign.Start } });
                ev.Clips.Add(new Clip { MediaId = va.Id, Kind = ClipKind.Video, Track = 2, Start = 5, In = 0.2, Dur = 2, NoAudio = true, TransIn = new Transition { Kind = "Wipe Left", Dur = 1, Align = TransAlign.End } });
                var vmp4 = Path.Combine(dir, "trans_video.mp4");
                var verr = await Exporter.RunAsync(ff, ev, vmp4, new Progress<double>(), CancellationToken.None);
                Check(verr == null, "video clips with transitions (handles, repeated last frame) export" + (verr == null ? "" : ": " + verr));
                if (verr == null)
                {
                    int frames = CountFrames(ff, vmp4);
                    Check(Math.Abs(frames - (int)Math.Round(ev.End * 30)) <= 1, $"video transitions keep the timeline length: {frames} frames for {ev.End:0.##} s");
                }
            }

            // audio: two tones crossfaded; constant power keeps the loudness, constant gain dips ~3 dB in the middle
            foreach (var (kind, expectMid) in new[] { ("Constant Power", 1.0), ("Constant Gain", Math.Sqrt(0.5)) })
            {
                var t1 = Path.Combine(dir, "tone1.wav"); var t2w = Path.Combine(dir, "tone2.wav");
                if (!File.Exists(t1)) Run(ff, $"-y -v error -f lavfi -i \"sine=frequency=1000:sample_rate=48000:duration=6\" -ac 2 \"{t1}\"");
                if (!File.Exists(t2w)) Run(ff, $"-y -v error -f lavfi -i \"sine=frequency=1500:sample_rate=48000:duration=6\" -ac 2 \"{t2w}\"");
                var s1 = await MediaProbe.ProbeAsync(t1) ?? throw new Exception("tone");
                var s2 = await MediaProbe.ProbeAsync(t2w) ?? throw new Exception("tone");
                var ea = new Editor(); ea.Media.Add(s1); ea.Media.Add(s2);
                ea.Clips.Add(new Clip { MediaId = s1.Id, Kind = ClipKind.Audio, Track = 3, Start = 0, In = 1, Dur = 2 });
                ea.Clips.Add(new Clip { MediaId = s2.Id, Kind = ClipKind.Audio, Track = 3, Start = 2, In = 1, Dur = 2, TransIn = new Transition { Kind = kind, Dur = 1 } });
                var wav = Path.Combine(dir, $"xfade_{kind.Replace(' ', '_')}.wav");
                var aerr = await Exporter.RunAsync(ff, ea, wav, new Progress<double>(), CancellationToken.None, null, null, new ExportSettings { Video = "none", Container = "wav", Audio = "pcm" });
                Check(aerr == null, $"{kind} crossfade export" + (aerr == null ? "" : ": " + aerr));
                if (aerr != null) continue;
                var pcm = Pcm(ff, wav);
                double Rms(double t) { int a0 = (int)((t - 0.02) * 48000), a1 = (int)((t + 0.02) * 48000); double s = 0; for (int k = a0; k < a1; k++) s += pcm[k] * pcm[k]; return Math.Sqrt(s / (a1 - a0)); }
                double full = Rms(0.8), mid = Rms(2.0) / full, q1 = Rms(1.75) / full, q3 = Rms(2.25) / full;
                Check(Math.Abs(mid - expectMid) < 0.05 && Math.Abs(Rms(3.2) / full - 1) < 0.05,
                      $"{kind}: loudness through the crossfade {q1:0.00} / {mid:0.00} / {q3:0.00} of full (middle expected {expectMid:0.00})");
                // the preview mixer uses the same curve
                var pl = Transitions.Compute(ea);
                var w = pl.Wins.Single();
                double ga = Transitions.AudioGain(pl, ea.Clips[0], 2.0), gb = Transitions.AudioGain(pl, ea.Clips[1], 2.0);
                Check(Math.Abs(Math.Sqrt(ga * ga + gb * gb) - expectMid) < 0.02 && Math.Abs(w.T0 - 1.5) < 1e-6 && Math.Abs(w.T1 - 2.5) < 1e-6,
                      $"{kind}: preview mixer gains at the cut {ga:0.000} + {gb:0.000}, window {w.T0:0.##}–{w.T1:0.##} s");
            }

            // editing: a razor cut keeps the head transition on the left piece and the tail one on the right
            var ec = new Editor(); ec.Media.Add(a);
            var cc = new Clip { MediaId = a.Id, Kind = ClipKind.Image, Track = 2, Start = 0, Dur = 4, TransIn = new Transition(), TransOut = new Transition { Kind = "Wipe Left" } };
            ec.Clips.Add(cc);
            ec.CutClips(new[] { cc }, 2);
            var right = ec.Clips.Single(c => c.Id != cc.Id);
            Check(cc.TransIn != null && cc.TransOut == null && right.TransIn == null && right.TransOut?.Kind == "Wipe Left", "razor cut: head transition stays left, tail transition goes right");
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "transtest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>A second test picture, clearly different from the first (mirrored gradient, stripes, other circles).</summary>
    static BitmapSource Second()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(30, 150, 90), Color.FromRgb(200, 40, 120), 90), null, new Rect(0, 0, W, H));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 3);
            for (int x = -H; x < W; x += 48) dc.DrawLine(pen, new Point(x, 0), new Point(x + H, H));
            for (int i = 0; i < 4; i++) dc.DrawEllipse(Brushes.Gold, new Pen(Brushes.Black, 4), new Point(220 + i * 280, 220), 90, 90);
            dc.DrawText(new FormattedText("SECOND PICTURE", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Black"), 64, Brushes.White, 1.0), new Point(330, 520));
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv); rtb.Freeze();
        return rtb;
    }

    static void Run(string ff, string args) { using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true })!; p.WaitForExit(); }

    static int CountFrames(string ff, string file)
    {
        var fp = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe");
        var psi = new ProcessStartInfo(fp) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", file }) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        return int.TryParse(o.Trim(), out var n) ? n : -1;
    }

    static float[] Pcm(string ff, string file)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-v", "error", "-i", file, "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1" }) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var ms = new MemoryStream(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        var bytes = ms.ToArray(); var r = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, r, 0, r.Length * 4);
        return r;
    }
}
