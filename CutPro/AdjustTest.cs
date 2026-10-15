using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>CutPro.exe --adjusttest dir: adjustment layers.
/// - the preview's LUT shader (6×6 slices, trilinear in the shader) on a 256×256 colour grid × 4 blue levels = ffmpeg lut3d trilinear within 2 levels;
/// - preview vs export on a timeline (video on V1, a title on V2): colour, look + vignette, blur, blur + colour at 60 %, mid fade-in, keyframed
///   opacity, an adjustment on V2 that leaves the V3 title alone, one on V1 that changes nothing; and the picture really changes.</summary>
public static class AdjustTest
{
    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        try
        {
            // ---------- 1. the shader = lut3d trilinear ----------
            var grade = new ColorGrade { Look = "Teal & Orange", Contrast = 25, Saturation = 125, Temperature = 15 };
            var lut = Grade.For(grade)!;
            Check(Adjust.Shader() != null, "LUT shader compiles (ps_2_0)" + (Adjust.CompileError != null ? ": " + Adjust.CompileError : ""));
            int G = 256;
            var src = new byte[G * G * 4 * 4];                                    // 4 tiles side by side: blue 0, 85, 170, 255
            for (int tile = 0; tile < 4; tile++)
                for (int y = 0; y < G; y++)
                    for (int x = 0; x < G; x++)
                    {
                        int i = (y * G * 4 + tile * G + x) * 4;
                        src[i] = (byte)(tile * 85); src[i + 1] = (byte)y; src[i + 2] = (byte)x; src[i + 3] = 255;
                    }
            var srcBmp = BitmapSource.Create(G * 4, G, 96, 96, PixelFormats.Bgra32, null, src, G * 16); srcBmp.Freeze();
            var srcPng = Path.Combine(dir, "grid.png");
            using (var fs = File.Create(srcPng)) { var e = new PngBitmapEncoder(); e.Frames.Add(BitmapFrame.Create(srcBmp)); e.Save(fs); }
            Canvas Host()
            {
                var img = new Image { Source = srcBmp, Width = G * 4, Height = G, Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                var inner = new Canvas { Width = G * 4, Height = G }; inner.Children.Add(img); RenderOptions.SetBitmapScalingMode(inner, BitmapScalingMode.Linear);
                var h = new Canvas { Width = G * 4, Height = G }; h.Children.Add(inner);
                RenderOptions.SetBitmapScalingMode(h, BitmapScalingMode.NearestNeighbor);   // (as the preview does: the LUT brush is realised without smoothing)
                h.Effect = new LutEffect(Adjust.Shader()!) { Lut = Adjust.Texture(lut), Strength = 1 };
                return h;
            }
            var host = Host();
            host.Measure(new Size(G * 4, G)); host.Arrange(new Rect(0, 0, G * 4, G)); host.UpdateLayout();
            var rtb = new RenderTargetBitmap(G * 4, G, 96, 96, PixelFormats.Pbgra32); rtb.Render(host);
            var pre = new byte[G * G * 16]; rtb.CopyPixels(pre, G * 16, 0);
            var cube = Path.Combine(dir, "grade.cube"); Grade.WriteCube(Adjust.Small(lut), cube);
            var outRaw = Path.Combine(dir, "lut3d.raw");
            Run(ff, $"-y -v error -i \"{srcPng}\" -vf \"lut3d=file='{cube.Replace("\\", "/").Replace(":", "\\:")}':interp=trilinear,format=bgra\" -frames:v 1 -f rawvideo \"{outRaw}\"");
            var exp = File.Exists(outRaw) ? File.ReadAllBytes(outRaw) : Array.Empty<byte>();
            int maxd = 0; long off = 0;
            if (exp.Length >= pre.Length)
                for (int i = 0; i < pre.Length; i += 4) for (int ch = 0; ch < 3; ch++) { int d = Math.Abs(pre[i + ch] - exp[i + ch]); if (d > maxd) maxd = d; if (d > 1) off++; }
            Check(exp.Length >= pre.Length && maxd <= 2, $"shader vs lut3d trilinear on {G * G * 4} colours: max off {maxd} levels, {off} channel values off by 2+" + (exp.Length < pre.Length ? " (no ffmpeg output)" : ""));
            // the same on screen: the GPU draws the preview, and it puts pixel / texel centres elsewhere than the software renderer (see Pin.Half)
            if (exp.Length >= pre.Length && await CompositeTest.OnScreen(Host(), G * 4, G) is { } scr)
            {
                int smax = 0; long soff = 0;
                for (int i = 0; i < scr.Length; i += 4) for (int ch = 0; ch < 3; ch++) { int d = Math.Abs(scr[i + ch] - exp[i + ch]); if (d > smax) smax = d; if (d > 1) soff++; }
                Check(smax <= 2, $"on screen ({((RenderCapability.Tier >> 16) > 0 ? "GPU" : "software")}): shader vs lut3d: max off {smax} levels, {soff} channel values off by 2+");
            }
            else Check(false, "on screen: the window could not be captured");
            if (maxd > 2 && exp.Length >= pre.Length)
            {
                var both = new byte[G * 4 * G * 2 * 4];
                Buffer.BlockCopy(pre, 0, both, 0, pre.Length); Buffer.BlockCopy(exp, 0, both, pre.Length, pre.Length);
                for (int i = 3; i < both.Length; i += 4) both[i] = 255;
                FxTest.Save(BitmapSource.Create(G * 4, G * 2, 96, 96, PixelFormats.Bgra32, null, both, G * 16), Path.Combine(dir, "lut_shader_vs_lut3d.png"));
                var tex = Adjust.Texture(lut).ImageSource as BitmapSource;
                if (tex != null) FxTest.Save(tex, Path.Combine(dir, "lut_texture.png"));
                var dump = new System.Text.StringBuilder("y: shader RGB | lut3d RGB (tile 0, x = 128)\n");
                for (int y = 0; y < G; y += 3) { int i = (y * G * 4 + 128) * 4; dump.Append($"{y}: {pre[i + 2]},{pre[i + 1]},{pre[i]} | {exp[i + 2]},{exp[i + 1]},{exp[i]}\n"); }
                // identity LUT through the shader: what comes back should be what went in
                int n = Grade.N; var idd = new float[n * n * n * 3];
                for (int r = 0; r < n; r++) for (int g = 0; g < n; g++) for (int b = 0; b < n; b++) { int d = ((r * n + g) * n + b) * 3; idd[d] = r / (float)(n - 1); idd[d + 1] = g / (float)(n - 1); idd[d + 2] = b / (float)(n - 1); }
                host.Effect = new LutEffect(Adjust.Shader()!) { Lut = Adjust.Texture(new Grade.Lut(idd, n, "identity")), Strength = 1 };
                host.UpdateLayout();
                var r4 = new RenderTargetBitmap(G * 4, G, 96, 96, PixelFormats.Pbgra32); r4.Render(host);
                var idp = new byte[pre.Length]; r4.CopyPixels(idp, G * 16, 0);
                dump.Append("identity LUT: y → G out (x = 128, tile 0)\n");
                for (int y = 0; y < G; y += 5) { int i = (y * G * 4 + 128) * 4; dump.Append($"{y}→{idp[i + 1]} r{idp[i + 2]} b{idp[i]}  "); }
                dump.Append("\nidentity LUT: x → R out (y = 128)\n");
                for (int x = 0; x < G; x += 5) { int i = (128 * G * 4 + x) * 4; dump.Append($"{x}→{idp[i + 2]}  "); }
                File.WriteAllText(Path.Combine(dir, "lut_dump.txt"), dump.ToString());
            }

            // ---------- 2. preview vs export on a timeline ----------
            Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
            string Gen(string name, string lavfi) { var f = Path.Combine(dir, name); if (!File.Exists(f)) Run(ff, $"-y -v error {lavfi} \"{f}\""); return f; }
            var landSrc = @"C:\Windows\Web\Wallpaper\ThemeC\img29.jpg";
            var photo = Gen("photo.png", File.Exists(landSrc) ? $"-i \"{landSrc}\" -vf scale=1280:-2,crop=1280:720 -frames:v 1" : "-f lavfi -i testsrc2=s=1280x720 -frames:v 1");
            var vid = Gen("video.mp4", "-f lavfi -i testsrc2=s=1280x720:r=30:d=20 -c:v libx264 -crf 12 -pix_fmt yuv420p");
            var ed = new Editor();
            var mP = await MediaProbe.ProbeAsync(photo) ?? throw new Exception("photo");
            var mV = await MediaProbe.ProbeAsync(vid) ?? throw new Exception("video");
            ed.Media.AddRange(new[] { mP, mV });
            var cases = new List<(string name, double t, double min)>();
            Clip A(int track, double start, double dur) { var c = new Clip { Kind = ClipKind.Adjustment, Track = track, Start = start, Dur = dur, Text = "Adjustment layer", Grade = new ColorGrade() }; ed.Clips.Add(c); return c; }
            ed.Clips.Add(new Clip { MediaId = mP.Id, Kind = ClipKind.Image, Track = 2, Start = 0, Dur = 10 });
            ed.Clips.Add(new Clip { MediaId = mV.Id, Kind = ClipKind.Video, Track = 2, Start = 10, Dur = 10, In = 1, NoAudio = true });
            var title = new Clip { Kind = ClipKind.Text, Track = 1, Start = 0, Dur = 20, Text = "ADJUSTED", Role = TextRole.Title, Style = Themes.Get(ed.ThemeName).Title.Clone() };
            title.Style!.Size = 120; ed.Clips.Add(title);
            var a1 = A(0, 0, 1); a1.Grade = grade.Clone2(); cases.Add(("colour (look, contrast, saturation, warmth)", 0.5, 33));   // (33: the export grades its 4:2:0 composite — saturation 125 % magnifies the chroma it lost in the trees)
            var a2 = A(0, 1, 1); a2.Grade = new ColorGrade { Look = "Black & White", Vignette = -60, VigFeather = 50 }; cases.Add(("black & white + vignette", 1.5, 38));
            var a3 = A(0, 2, 1); a3.Blur = 12; a3.Grade = null; cases.Add(("blur", 2.5, 34));
            var a4 = A(0, 3, 1); a4.Blur = 8; a4.Grade = grade.Clone2(); a4.Opacity = 60; cases.Add(("blur + colour at 60 %", 3.5, 34));
            var a5 = A(0, 4, 2); a5.Grade = new ColorGrade { Exposure = 1, Look = "Cool Night" }; a5.FadeIn = 1.5; cases.Add(("fading in (half way)", 4.73, 38));
            var a6 = A(0, 6, 2); a6.Grade = new ColorGrade { Look = "Vintage Fade" }; Anim.SetKey(a6, AnimProp.Opacity, a6.In, 0); Anim.SetKey(a6, AnimProp.Opacity, a6.In + 2, 100);
            cases.Add(("keyframed opacity (≈ 35 %)", 6.7, 38));
            title.Track = 0; a1.Track = a2.Track = a3.Track = a4.Track = a5.Track = a6.Track = 1;   // (adjustments on V2: the title on V3 stays as it is)
            var a7 = A(1, 10, 2); a7.Grade = new ColorGrade { Look = "Sepia", Contrast = 20 }; cases.Add(("on video, title above untouched", 10.6, 36));
            var a8 = A(2, 14, 2); a8.Grade = new ColorGrade { Look = "Black & White" };              // V1: nothing below
            ed.Clips.Remove(ed.Clips.First(c => c.Kind == ClipKind.Video)); ed.Clips.Add(new Clip { MediaId = mV.Id, Kind = ClipKind.Video, Track = 2, Start = 10, Dur = 4, In = 1, NoAudio = true });
            ed.Clips.Add(new Clip { MediaId = mV.Id, Kind = ClipKind.Video, Track = 2, Start = 16, Dur = 4, In = 7, NoAudio = true });
            a8.Track = 2; a8.Start = 14; a8.Dur = 2;
            cases.Add(("on V1: nothing changes", 14.5, 40));
            var mkv = Path.Combine(dir, "adjust.mkv");
            var opts = new ExportSettings { Container = "mkv", Video = "ffv1", Audio = "none" };
            var titles = TitleVisual.PrepareExport(ed, Path.Combine(dir, "titles"), opts.OutW, opts.OutH);
            var sw = Stopwatch.StartNew();
            var err = await Task.Run(() => Exporter.RunAsync(ff, ed, mkv, new Progress<double>(), CancellationToken.None, titles, null, opts));
            Check(err == null, $"export with adjustment layers ({sw.Elapsed.TotalSeconds:0.0} s)" + (err == null ? "" : ": " + err));
            if (err == null)
            {
                var pv = new Preview { Ed = ed, Width = Editor.W, Height = Editor.H, Background = Brushes.Black };
                foreach (var (name, t1, min) in cases)
                {
                    double t = Math.Round(t1 * 30) / 30;
                    var swp = Stopwatch.StartNew();
                    do { pv.Sync(t, false); await Task.Delay(20); } while ((!pv.Settled(t) || swp.ElapsedMilliseconds < 500) && swp.ElapsedMilliseconds < 8000);
                    pv.Sync(t, false); pv.Measure(new Size(Editor.W, Editor.H)); pv.Arrange(new Rect(0, 0, Editor.W, Editor.H)); pv.UpdateLayout();
                    var r2 = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); r2.Render(pv);
                    var pa = new byte[Editor.W * Editor.H * 4]; r2.CopyPixels(pa, Editor.W * 4, 0);
                    var pb = FxTest.Frame(ff, mkv, t - 0.001);
                    var (psnr, worst) = FxTest.Compare(pa, pb);
                    var (sx, sy) = SeqTest.Shift(pa, pb, Editor.W, Editor.H);
                    // and it really changed the picture (vs the same frame with the adjustment layer off)
                    var adj = ed.Clips.First(c => Adjust.Is(c) && t >= c.Start && t < c.End);
                    bool off0 = ed.TrackOff[adj.Track]; double changed = 99;
                    if (Adjust.Works(adj))
                    {
                        var keep = adj.Grade; var kb = adj.Blur; adj.Grade = null; adj.Blur = 0; ed.RaiseProps();
                        pv.Sync(t, false); await Task.Delay(50); pv.Sync(t, false); pv.UpdateLayout();
                        var r3 = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); r3.Render(pv);
                        var pc = new byte[pa.Length]; r3.CopyPixels(pc, Editor.W * 4, 0);
                        changed = FxTest.Compare(pa, pc).psnr;
                        adj.Grade = keep; adj.Blur = kb;
                    }
                    bool works = Adjust.Works(adj) ? changed < 35 : true;
                    Check(psnr >= min && Math.Abs(sx) <= 1 && Math.Abs(sy) <= 1 && works,
                          $"{name}: preview vs export PSNR {psnr:0.0} dB (≥ {min}), worst block {worst:0.0}/255, shift ({sx},{sy}){(Adjust.Works(adj) ? $"; vs without it {changed:0.0} dB" : "")}");
                    FxTest.Save(FxTest.SideBySide(pa, pb), Path.Combine(dir, $"adj_{string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_').Replace("%", "").Replace("(", "").Replace(")", "")}.png"));
                    _ = off0;
                }
                pv.DisposeAll();
            }
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "adjusttest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static ColorGrade Clone2(this ColorGrade g) => System.Text.Json.JsonSerializer.Deserialize<ColorGrade>(System.Text.Json.JsonSerializer.Serialize(g))!;

    static void Run(string ff, string args) { using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!; p.StandardError.ReadToEnd(); p.WaitForExit(); }
}
