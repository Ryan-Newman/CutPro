using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>CutPro.exe --animtest dir: keyframes + formulas, preview vs export. Image clips and titles (the preview renders
/// them synchronously) with animated scale / position / rotation / opacity / blur, a hold keyframe (off-by-one check), static
/// rotation, and an animated volume measured on the exported audio.</summary>
public static class AnimTest
{
    static int W => Editor.W;
    const int H = Editor.H;

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");

        var pattern = Path.Combine(dir, "pattern.png");
        FxTest.Save(FxTest.Pattern(), pattern);
        var tone = Path.Combine(dir, "tone.wav");
        if (!File.Exists(tone))
        {
            var p = Process.Start(new ProcessStartInfo(ff, $"-y -v error -f lavfi -i \"sine=frequency=1000:sample_rate=48000:duration=3\" -af volume=0.5 -ac 2 \"{tone}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            p.WaitForExit();
        }
        var ed = new Editor();
        var img = await MediaProbe.ProbeAsync(pattern) ?? throw new Exception("pattern import failed");
        var snd = await MediaProbe.ProbeAsync(tone) ?? throw new Exception("tone import failed");
        ed.Media.Add(img); ed.Media.Add(snd);

        Clip Img(double start, double dur) { var c = new Clip { MediaId = img.Id, Kind = ClipKind.Image, Track = 2, Start = start, Dur = dur }; ed.Clips.Add(c); return c; }
        void Key(Clip c, AnimProp p, double src, double v, string? interp = null) { var k = Anim.SetKey(c, p, src, v); if (interp != null) Anim.ApplyInterp(k, interp); }

        // A: keyframed motion with easing (0–2 s)
        var a = Img(0, 2);
        Key(a, AnimProp.Scale, 0, 100, "Easy Ease"); Key(a, AnimProp.Scale, 1.8, 55, "Easy Ease");
        Key(a, AnimProp.X, 0, 0); Key(a, AnimProp.X, 1.8, 22);
        Key(a, AnimProp.Rotation, 0, 0); Key(a, AnimProp.Rotation, 1.8, 25);
        Key(a, AnimProp.Opacity, 0, 100); Key(a, AnimProp.Opacity, 1.8, 55);
        Key(a, AnimProp.Blur, 0, 0); Key(a, AnimProp.Blur, 1.8, 9);
        // B: formulas + a hold keyframe on Y (2–4 s)
        var b = Img(2, 2); b.Id = "animtest-formulas";        // wiggle() is seeded by the clip id: fixed, so every run measures the same frames
        Anim.Ensure(b, AnimProp.X).Formula = "wiggle(3, 15)";
        Anim.Ensure(b, AnimProp.Rotation).Formula = "10 * sin(t * 2 * pi)";
        Anim.Ensure(b, AnimProp.Scale).Formula = "value * (0.8 + 0.1 * sin(t * pi))";
        Key(b, AnimProp.Y, 0, 0, "Hold"); Key(b, AnimProp.Y, 1.0, -20);
        // C: animated title (4–6 s)
        var c = new Clip { Kind = ClipKind.Text, Track = 1, Start = 4, Dur = 2, Text = "Animated", Style = StyleLibrary.Get("Bold Impact").With(s => s.Size = 150) };
        ed.Clips.Add(c);
        Key(c, AnimProp.Scale, 0, 70); Key(c, AnimProp.Scale, 1.9, 130, "Easy Ease");
        Key(c, AnimProp.Rotation, 0, -12); Key(c, AnimProp.Rotation, 1.9, 12);
        Key(c, AnimProp.Opacity, 0, 35); Key(c, AnimProp.Opacity, 1.9, 100);
        Key(c, AnimProp.Blur, 0, 6); Key(c, AnimProp.Blur, 1.9, 0);
        // D: static rotation (6–7 s)
        var d = Img(6, 1); d.Rotation = 30; d.Scale = 70;
        // audio: volume 100 → 0 over 2 s
        var au = new Clip { MediaId = snd.Id, Kind = ClipKind.Audio, Track = 3, Start = 0, Dur = 3 };
        ed.Clips.Add(au);
        Key(au, AnimProp.Volume, 0, 100); Key(au, AnimProp.Volume, 2, 0);

        foreach (var x in new[] { a, b, c, d }) Check(x == d ? LayerPlan.Needed(x) : Anim.AnyAnimated(x, Anim.Visual), $"clip at {x.Start}s is animated / layered");

        var titles = TitleVisual.PrepareExport(ed, Path.Combine(dir, "titles"));
        Check(titles.TryGetValue(c.Id, out var tf) && tf.EndsWith(".mkv"), "animated title exported as a frame sequence");
        var mp4 = Path.Combine(dir, "anim.mp4");
        var sw = Stopwatch.StartNew();
        var err = await Exporter.RunAsync(ff, ed, mp4, new Progress<double>(), CancellationToken.None, titles);
        Check(err == null, $"export ({sw.Elapsed.TotalSeconds:0.0} s)" + (err == null ? "" : ": " + err));
        if (err != null) { File.WriteAllLines(Path.Combine(dir, "animtest.txt"), log); return 1; }

        var pv = new Preview { Ed = ed, Width = W, Height = H, Background = Brushes.Black };
        var frames = new (int f, string what)[]
        {
            (15, "keys: scale/x/rotation/opacity/blur, eased, 0.5 s"), (30, "keys at 1.0 s"), (57, "keys near the end (1.9 s)"),
            (75, "formulas: wiggle x, sine rotation, scale; 2.5 s"), (89, "hold key: last frame before the jump (2.967 s)"),
            (90, "hold key: first frame after the jump (3.0 s)"), (105, "formulas 3.5 s"),
            (135, "animated title 4.5 s"), (165, "animated title 5.5 s"), (195, "static rotation 30° at 70%"),
        };
        foreach (var (f, what) in frames)
        {
            double t = f / (double)TimeUtil.Fps;
            pv.Sync(t, false);
            pv.Measure(new Size(W, H)); pv.Arrange(new Rect(0, 0, W, H)); pv.UpdateLayout();
            var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
            var pa = new byte[W * H * 4]; rtb.CopyPixels(pa, W * 4, 0);
            var pb = FxTest.Frame(ff, mp4, t - 0.001);
            var (psnr, worst) = FxTest.Compare(pa, pb);
            // best whole-pixel alignment (a placement error shows up as a shift; resampling differences don't)
            (int dx, int dy, double e) best = (0, 0, double.MaxValue);
            for (int sy = -3; sy <= 3; sy++)
                for (int sx = -3; sx <= 3; sx++)
                {
                    double se = 0;
                    for (int y = 40; y < H - 40; y += 2)
                        for (int x = 40; x < W - 40; x += 2)
                        {
                            int i = (y * W + x) * 4, j = ((y + sy) * W + x + sx) * 4;
                            for (int ch = 0; ch < 3; ch++) { double df = pa[i + ch] - pb[j + ch]; se += df * df; }
                        }
                    if (se < best.e) best = (sx, sy, se);
                }
            // rotated / rescaled pictures resample differently (WPF vs ffmpeg rotate + scale): on this 1 px grid pattern that costs
            // a few dB, so image layers must match within 1 px (no placement error) and 23 dB; titles are rendered by WPF itself
            bool title = f >= 120 && f < 180;
            bool ok = title ? psnr >= 30 && worst <= 24 : psnr >= 23 && worst <= 36 && Math.Abs(best.dx) <= 1 && Math.Abs(best.dy) <= 1;
            Check(ok, $"preview = export, frame {f}: {what}: PSNR {psnr:0.0} dB, worst block {worst:0.0}/255, best alignment shift ({best.dx},{best.dy}) px");
            FxTest.Save(FxTest.SideBySide(pa, pb), Path.Combine(dir, $"anim_{f}.png"));
        }

        // volume: RMS of the exported audio vs the preview's gain table
        var pcm = Pcm(ff, mp4);
        double full = 0.5 * 0.125 / Math.Sqrt(2);                 // lavfi sine: amplitude 1/8, then volume 0.5
        var table = Anim.GainTable(au);
        foreach (double t in new[] { 0.25, 1.0, 1.5, 2.5 })
        {
            int s0 = (int)((t - 0.01) * 48000), s1 = (int)((t + 0.01) * 48000);
            double acc = 0; for (int s = s0; s < s1; s++) acc += pcm[s] * pcm[s];
            double rms = Math.Sqrt(acc / (s1 - s0)), expect = full * table[(int)(t / AudioOut.GainStep)];
            Check(Math.Abs(rms - expect) <= 0.01 + expect * 0.04, $"volume at {t:0.00} s: exported RMS {rms:0.000}, preview gain × tone {expect:0.000}");
        }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "animtest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static float[] Pcm(string ff, string file)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-v", "error", "-i", file, "-map", "0:a", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1" }) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var ms = new MemoryStream(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        var bytes = ms.ToArray(); var r = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, r, 0, r.Length * 4);
        return r;
    }
}
