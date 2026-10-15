using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>CutPro.exe --colortest dir: colour correction, LUTs, drawn looks.
/// - the preview's LUT arithmetic = ffmpeg's lut3d (every pixel of a photo through a strong grade, within 1/255);
/// - preview vs export (PSNR, worst block) for graded video, a graded picture with a vignette, cartoon / cel / anime video and a picture
///   with a drawn look (<c>color_*.png</c> side by side);
/// - .cube files: write → read back, a LUT as a creative look; an untouched grade needs no LUT; speeds of baking / applying.</summary>
public static class ColorTest
{
    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
        try
        {
            // test pictures: a landscape photo and a colourful one from Windows' own wallpapers (else generated patterns)
            string Pic(string name, string src, string fallback)
            {
                var f = Path.Combine(dir, name);
                if (File.Exists(f)) return f;
                if (File.Exists(src)) Run(ff, $"-y -v error -i \"{src}\" -vf scale=1280:-2,crop=1280:720 \"{f}\"");
                else Run(ff, $"-y -v error -f lavfi -i {fallback} -frames:v 1 \"{f}\"");
                return f;
            }
            var land = Pic("land.png", @"C:\Windows\Web\Wallpaper\ThemeC\img29.jpg", "testsrc2=s=1280x720");
            var shape = Pic("shape.png", @"C:\Windows\Web\Wallpaper\ThemeB\img24.jpg", "mandelbrot=s=1280x720");
            var vid = Path.Combine(dir, "land.mp4");
            if (!File.Exists(vid)) Run(ff, $"-y -v error -loop 1 -framerate 30 -t 6 -i \"{land}\" -vf \"zoompan=z='1+0.0004*on':d=1:s=1280x720:fps=30\" -c:v libx264 -crf 10 -pix_fmt yuv420p \"{vid}\"");

            var g1 = new ColorGrade
            {
                Temperature = 35, Tint = -10, Exposure = 0.4, Contrast = 45, Highlights = -30, Shadows = 25, Whites = 10, Blacks = -10, Saturation = 125, Vibrance = 20,
                Look = "Teal & Orange", LookAmount = 80, Faded = 10, Gain = new Wheel { Hue = 35, Amount = 30 }, Lift = new Wheel { Hue = 200, Amount = 25, Luma = -10 },
                Curve = new[] { 0, 0.02, 0.25, 0.2, 0.75, 0.82, 1, 1 },
            };

            // 1. LUT arithmetic: C# = ffmpeg lut3d on every pixel of the photo
            var lut = Grade.For(g1)!;
            var cube = Path.Combine(dir, "g1.cube"); Grade.WriteCube(lut, cube);
            var raw = Raw(ff, land, "", 1280, 720);
            var mine = (byte[])raw.Clone(); Grade.Apply(lut, (byte[])raw.Clone(), 1280, 720, 1280 * 4);       // (warm up)
            var sw = Stopwatch.StartNew(); for (int i = 0; i < 10; i++) { Buffer.BlockCopy(raw, 0, mine, 0, raw.Length); Grade.Apply(lut, mine, 1280, 720, 1280 * 4); }
            double applyMs = sw.Elapsed.TotalMilliseconds / 10;
            var theirs = Raw(ff, land, $"format=rgb24,lut3d=file='{cube.Replace("\\", "/").Replace(":", "\\:")}':interp=tetrahedral,", 1280, 720);
            int maxd = 0; long off1 = 0;
            for (int i = 0; i < mine.Length; i++) { if (i % 4 == 3) continue; int d = Math.Abs(mine[i] - theirs[i]); maxd = Math.Max(maxd, d); if (d > 1) off1++; }
            Check(maxd <= 1 && applyMs < 6, $"preview LUT = ffmpeg lut3d (tetrahedral): max difference {maxd}/255, {off1} values off by more than 1; applied in {applyMs:0.0} ms per 1280×720 frame");
            sw.Restart(); Grade.Forget(); Grade.For(g1); double buildMs = sw.Elapsed.TotalMilliseconds;
            Check(buildMs < 1000, $"baking a full grade into the 33³ LUT: {buildMs:0} ms");
            Check(Grade.For(new ColorGrade()) == null && Grade.For(null) == null, "an untouched grade needs no LUT");

            // scopes: each draws the photo (share of lit pixels), the histogram isn't flattened by one tall bin (a clipped sky), and fast
            var half = Raw(ff, land, "scale=640:360,", 640, 360);
            var kinds = new[] { ScopeKind.Waveform, ScopeKind.Parade, ScopeKind.Vectorscope, ScopeKind.Histogram };
            Scopes.Compute(half, 640, 360, kinds);
            var sws = Stopwatch.StartNew(); var sc = Scopes.Compute(half, 640, 360, kinds); double scMs = sws.Elapsed.TotalMilliseconds;
            static double Lit(byte[] b) { int n = 0; for (int i = 3; i < b.Length; i += 4) if (b[i] > 0) n++; return n / (b.Length / 4.0); }
            var sky = (byte[])half.Clone(); for (int i = 0; i < sky.Length / 3; i++) if (i % 4 != 3) sky[i] = 254;     // top third clipped
            double histSky = Lit(Scopes.Compute(sky, 640, 360, new[] { ScopeKind.Histogram })[ScopeKind.Histogram]);
            Check(Lit(sc[ScopeKind.Waveform]) > 0.05 && Lit(sc[ScopeKind.Parade]) > 0.05 && Lit(sc[ScopeKind.Vectorscope]) > 0.003 && Lit(sc[ScopeKind.Histogram]) > 0.15 && histSky > 0.12 && scMs < 40,
                  $"scopes of the photo: waveform {Lit(sc[ScopeKind.Waveform]):P0}, parade {Lit(sc[ScopeKind.Parade]):P0}, vectorscope {Lit(sc[ScopeKind.Vectorscope]):P1}, " +
                  $"histogram {Lit(sc[ScopeKind.Histogram]):P0} filled ({histSky:P0} with a clipped sky); all four in {scMs:0.0} ms");

            // .cube round trip, and a .cube as a creative look
            var back = Grade.Cube(cube)!;
            double worstCube = 0;
            for (int r = 0; r < Grade.N; r += 4) for (int g = 0; g < Grade.N; g += 4) for (int b = 0; b < Grade.N; b += 4)
            {
                var (a, c, d) = back.Sample(r / 32.0, g / 32.0, b / 32.0); int o = ((r * Grade.N + g) * Grade.N + b) * 3;
                worstCube = Math.Max(worstCube, Math.Max(Math.Abs(a - lut.D[o]), Math.Max(Math.Abs(c - lut.D[o + 1]), Math.Abs(d - lut.D[o + 2]))));
            }
            Check(back.Size == Grade.N && worstCube < 1e-5, $".cube written and read back ({back.Size}³, worst {worstCube:E1})");
            var viaCube = Grade.For(new ColorGrade { Look = cube })!;
            double worstLook = 0;
            for (int i = 0; i < viaCube.D.Length; i++) worstLook = Math.Max(worstLook, Math.Abs(viaCube.D[i] - lut.D[i]));
            Check(worstLook < 1e-4, $"a .cube file as the look reproduces its grade (worst {worstLook:E1})");

            // 2. preview vs export
            var ed = new Editor();
            var mv = await MediaProbe.ProbeAsync(vid) ?? throw new Exception("import video");
            var ms = await MediaProbe.ProbeAsync(shape) ?? throw new Exception("import shape");
            var ml = await MediaProbe.ProbeAsync(land) ?? throw new Exception("import land");
            ed.Media.AddRange(new[] { mv, ms, ml });
            var cases = new List<(string name, Clip c, double psnrMin)>();
            double t0 = 0;
            Clip Add(string name, MediaItem m, ClipKind k, double psnrMin, ColorGrade? g = null, ToonStyle? toon = null)
            {
                var c = new Clip { MediaId = m.Id, Kind = k, Track = 2, Start = t0, In = k == ClipKind.Video ? t0 % 4 : 0, Dur = 1, NoAudio = true, Grade = g, Toon = toon };
                ed.Clips.Add(c); cases.Add((name, c, psnrMin)); t0 += 1; return c;
            }
            Add("graded video", mv, ClipKind.Video, 34, g1);               // (strong grade: the export's 4:2:0 colour shows in fine detail)
            Add("graded picture + vignette", ms, ClipKind.Image, 34, new ColorGrade { Look = "Black & White", Contrast = 20, Vignette = -70, VigMidpoint = 40, VigFeather = 60 });
            Add("cartoon video", mv, ClipKind.Video, 30, new ColorGrade { Exposure = -0.2 }, ToonStyle.Preset(ToonMode.Cartoon));
            Add("cel-shaded video", mv, ClipKind.Video, 30, null, ToonStyle.Preset(ToonMode.Cel));
            Add("anime video", mv, ClipKind.Video, 30, null, ToonStyle.Preset(ToonMode.Anime));
            Add("anime picture", ml, ClipKind.Image, 30, null, ToonStyle.Preset(ToonMode.Anime));
            var mp4 = Path.Combine(dir, "color.mp4");
            sw.Restart();
            var err = await Exporter.RunAsync(ff, ed, mp4, new Progress<double>(), CancellationToken.None, null, null, new ExportSettings { Quality = 10, Audio = "none" });
            Check(err == null, $"export with grades, LUTs, a vignette and drawn looks ({sw.Elapsed.TotalSeconds:0.0} s)" + (err == null ? "" : ": " + err));
            if (err == null)
            {
                var pv = new Preview { Ed = ed, Width = Editor.W, Height = Editor.H, Background = Brushes.Black };
                foreach (var (name, c, psnrMin) in cases)
                {
                    double t = Math.Round((c.Start + 0.5) * 30) / 30;
                    var swp = Stopwatch.StartNew();
                    do { pv.Sync(t, false); await Task.Delay(20); } while ((!pv.Settled(t) || swp.ElapsedMilliseconds < (c.Kind == ClipKind.Image && Toon.Active(c.Toon) ? 2500 : 300)) && swp.ElapsedMilliseconds < 8000);
                    pv.Sync(t, false);
                    pv.Measure(new Size(Editor.W, Editor.H)); pv.Arrange(new Rect(0, 0, Editor.W, Editor.H)); pv.UpdateLayout();
                    var rtb = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
                    var pa = new byte[Editor.W * Editor.H * 4]; rtb.CopyPixels(pa, Editor.W * 4, 0);
                    var pb = FxTest.Frame(ff, mp4, t - 0.001);
                    var (psnr, worst) = FxTest.Compare(pa, pb);
                    var (sx, sy) = SeqTest.Shift(pa, pb, Editor.W, Editor.H);
                    Check(psnr >= psnrMin && Math.Abs(sx) <= 1 && Math.Abs(sy) <= 1, $"{name}: preview vs export PSNR {psnr:0.0} dB (≥ {psnrMin}), worst block {worst:0.0}/255, shift ({sx},{sy})");
                    FxTest.Save(FxTest.SideBySide(pa, pb), Path.Combine(dir, $"color_{name.Replace(' ', '_').Replace("+", "and")}.png"));
                }
            }
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "colortest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>A picture as BGRA bytes after an optional filter prefix (ending with a comma).</summary>
    static byte[] Raw(string ff, string file, string vf, int w, int h)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-v", "error", "-i", file, "-vf", vf + "format=bgra", "-frames:v", "1", "-f", "rawvideo", "pipe:1" }) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var ms = new MemoryStream(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        var b = ms.ToArray();
        if (b.Length < w * h * 4) throw new Exception("raw frame failed: " + file);
        return b;
    }

    static void Run(string ff, string args) { using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true })!; p.WaitForExit(); }
}
