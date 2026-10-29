using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// CutPro.exe --graphicstest dir: motion graphics (shape layers, stickers, templates), headless.
/// - the animation states (pop, grow, slides, draw on, spin, delay, out, loops, progress) as numbers;
/// - every sticker and every built-in template draws; a drawn-on arrow grows from its tail; a progress bar covers its share of the clip;
/// - preview = export: every built-in template on V2 over a test pattern, at three moments each (coming in, complete, going out) — the live
///   title element of the preview against the exported frame (PSNR, worst 16 × 16 block; graphics_*.png);
/// - an animated shape as a track matte (a growing ellipse revealing V1), preview = export;
/// - Add Graphic (the free track above V1, one undo step, scaled for 9:16), saving / reading / deleting your own templates, project round trip;
/// - per-frame cost of animating a template's layers (no rebuild).
/// </summary>
public static class GraphicsTest
{
    static List<string> log = new();
    static int fails;
    static void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
    static bool Near(double a, double b, double tol = 1e-6) => Math.Abs(a - b) < tol;
    const int H = Editor.H;
    static int W => Editor.W;

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        log = new(); fails = 0;
        AppSettings.ReadOnly = true;
        try { await Body(dir); }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "graphicstest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static async Task Body(string dir)
    {
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
        double F = TimeUtil.Fps;

        // ---------- animation states ----------
        {
            ShapeState St(ShapeAnim a, double t, double inDur = 0.5, double delay = 0, ShapeAnim o = ShapeAnim.None, double dur = 4, ShapeLoop loop = ShapeLoop.None) =>
                Shapes.State(new ShapeLayer { In = a, InDur = inDur, Delay = delay, Out = o, OutDur = 0.4, Loop = loop, W = 200, H = 100 }, t, dur);
            var p0 = St(ShapeAnim.Pop, 0); var p1 = St(ShapeAnim.Pop, 0.25); var p2 = St(ShapeAnim.Pop, 0.5);
            var g = St(ShapeAnim.Grow, 0.25); var sl = St(ShapeAnim.SlideLeft, 0); var su = St(ShapeAnim.SlideUp, 0); var sd = St(ShapeAnim.Draw, 0.25);
            var sp = St(ShapeAnim.Spin, 0); var dl = St(ShapeAnim.Fade, 0.5, delay: 1); var dl2 = St(ShapeAnim.Fade, 1.5, delay: 1);
            var o1 = St(ShapeAnim.None, 3.8, o: ShapeAnim.Fade); var pr = St(ShapeAnim.None, 1, loop: ShapeLoop.Progress);
            var pulse = Enumerable.Range(0, 40).Select(i => St(ShapeAnim.None, i * 0.05, loop: ShapeLoop.Pulse).Scale).ToList();
            Check(p0.Opacity == 0 && p0.Scale == 0 && p1.Scale > 0.9 && p1.Scale < 1.25 && Near(p2.Scale, 1) && Near(p2.Opacity, 1) && Near(g.Scale, 0.875) &&
                  Near(sl.Dx, -200) && Near(su.Dy, 200) && Near(sd.Reveal, 0.5) && Near(sp.Rotation, -180) && sp.Opacity == 0 &&
                  dl.Opacity == 0 && Near(dl2.Opacity, 1) && Near(o1.Opacity, 0.5) && Near(pr.Reveal, 0.25) && pulse.Max() <= 1.06 + 1e-9 && pulse.Min() >= 0.94 - 1e-9 && pulse.Max() > 1.04,
                  $"states: pop 0 → {p1.Scale:0.00} (overshoot) → 1, grow ½ = {g.Scale:0.000}, slides from {sl.Dx:0} / {su.Dy:0} px, draw ½ = {sd.Reveal:0.00}, spin from {sp.Rotation:0}°, delay holds, fade out ½ = {o1.Opacity:0.00}, progress ¼ = {pr.Reveal:0.00}, pulse ±6 %");
            Check(Shapes.State(new ShapeLayer { In = ShapeAnim.Pop, Out = ShapeAnim.Fade }, null, 4) == new ShapeState(1, 0, 0, 1, 0, 1), "a still (no time): every layer fully shown, in place");
        }

        // ---------- stickers and templates draw ----------
        {
            var empty = new List<string>();
            foreach (var (name, _, _) in Shapes.Stickers)
            {
                var b = Shapes.Area(Shapes.Sticker(name, 100)).Bounds;
                var c = new Clip { Kind = ClipKind.Text, Text = "", Dur = 2, Shapes = new() { Shapes.Sticker(name, 160) } };
                if (b.IsEmpty || b.Width < 40 || b.Left < -52 || b.Right > 52 || Covered(TitleVisual.Render(c, false)) < 2000) empty.Add(name);
            }
            Check(empty.Count == 0, $"every sticker ({Shapes.Stickers.Length}) draws inside its box" + (empty.Count > 0 ? ": not " + string.Join(", ", empty) : ""));
            var bad = GraphicLibrary.BuiltIn.Where(t => { var c = GraphicLibrary.Instance(t); return Covered(TitleVisual.Render(c, false, local: GraphicLibrary.ShownAt(c))) < 1500; }).Select(t => t.Name).ToList();
            Check(GraphicLibrary.BuiltIn.Count >= 12 && bad.Count == 0, $"every built-in template ({GraphicLibrary.BuiltIn.Count}) draws when complete" + (bad.Count > 0 ? ": not " + string.Join(", ", bad) : ""));
            // a drawn-on arrow grows from its tail; a progress bar covers its share of the clip
            var arrow = new Clip { Kind = ClipKind.Text, Text = "", Dur = 3, Shapes = new() { new ShapeLayer { Kind = ShapeKind.Arrow, W = 600, Thickness = 12, Head = 40, Fill = "#FFFFE14A", In = ShapeAnim.Draw, InDur = 1 } } };
            var right = new[] { 0.25, 0.5, 0.75, 1.2 }.Select(t => Extent(TitleVisual.Render(arrow, false, local: t)).right).ToList();
            double tip = W / 2.0 + 300;
            Check(right.Zip(right.Skip(1)).All(p => p.Second > p.First + 20) && Math.Abs(right[^1] - tip) < 4 && right[0] < W / 2.0 - 100,
                  $"a drawn-on arrow grows from its tail: right edge {string.Join(" → ", right.Select(r => r.ToString("0")))} px (tip at {tip:0})");
            var bar = GraphicLibrary.Instance(GraphicLibrary.BuiltIn.First(t => t.Name == "Progress bar"));
            var cover = new[] { 0.25, 0.5, 0.75 }.Select(f => RedRun(TitleVisual.Render(bar, false, local: f * bar.Dur), 712) / (double)W).ToList();
            Check(cover.Zip(new[] { 0.25, 0.5, 0.75 }).All(p => Math.Abs(p.First - p.Second) < 0.02), $"progress bar: {string.Join(" / ", cover.Select(x => x.ToString("0.000")))} of the width at ¼ ½ ¾ of the clip");
        }

        // ---------- preview = export: every built-in template over a test pattern ----------
        {
            var pattern = Path.Combine(dir, "pattern.png");
            if (!File.Exists(pattern)) { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(FxTest.Pattern())); using var fs = File.Create(pattern); enc.Save(fs); }
            var ed = new Editor();
            var m = await MediaProbe.ProbeAsync(pattern) ?? throw new Exception("pattern: " + MediaProbe.LastError);
            ed.Media.Add(m);
            const double Len = 2.4;
            var list = GraphicLibrary.BuiltIn;
            ed.Clips.Add(new Clip { MediaId = m.Id, Kind = ClipKind.Image, Track = Tracks.V1, Start = 0, Dur = list.Count * Len + 3 });
            for (int i = 0; i < list.Count; i++)
            {
                var c = GraphicLibrary.Instance(list[i]);
                c.Track = Tracks.V(2); c.Start = i * Len; c.Dur = Len;
                ed.Clips.Add(c);
            }
            // a shape as a track matte: a growing ellipse reveals the pattern (V3 over V2 = nothing; the matte acts on V1)
            double mt0 = list.Count * Len;
            ed.Clips.Add(new Clip
            {
                Kind = ClipKind.Text, Track = Tracks.V(2), Start = mt0, Dur = 3, Text = "", Mask = MaskMode.Reveal,
                Shapes = new() { new ShapeLayer { Kind = ShapeKind.Ellipse, W = 700, H = 500, In = ShapeAnim.Grow, InDur = 1.5 }, new ShapeLayer { Kind = ShapeKind.Star, X = 420, Y = -150, W = 200, H = 200, In = ShapeAnim.Spin, Delay = 0.5, InDur = 1 } }
            });
            var titles = TitleVisual.PrepareExport(ed, Path.Combine(dir, "titles"));
            Check(ed.Clips.Where(c => c.Kind == ClipKind.Text).All(c => titles.TryGetValue(c.Id, out var f) && (!Shapes.Animated(c) || f.EndsWith(".mkv"))),
                  "the export renders graphics with moving layers frame by frame (FFV1) — the matte too");
            // a 4:4:4 master (ProRes 4444): what is measured is the graphics, not H.264's 4:2:0 on the pattern's thin coloured lines
            var mp4 = Path.Combine(dir, "graphics.mov");
            var sw = Stopwatch.StartNew();
            var err = await Exporter.RunAsync(ff, ed, mp4, new Progress<double>(), CancellationToken.None, titles, null, new ExportSettings { Container = "mov", Video = "prores4444", Audio = "none" });
            Check(err == null, $"export of {list.Count} graphics + a shape matte ({sw.Elapsed.TotalSeconds:0.0} s)" + (err == null ? "" : ": " + err));
            if (err != null) return;
            var moments = new List<(int k, string what)>();
            for (int i = 0; i < list.Count; i++)
            {
                double s0 = i * Len;
                moments.Add(((int)Math.Round((s0 + 0.2) * F), list[i].Name + " · coming in"));
                moments.Add(((int)Math.Round((s0 + 1.3) * F), list[i].Name + " · complete"));
                moments.Add(((int)Math.Round((s0 + Len - 0.17) * F), list[i].Name + " · going out"));
            }
            foreach (var (t, what) in new[] { (0.4, "shape matte · growing"), (1.0, "shape matte · star spinning in"), (2.2, "shape matte · complete") })
                moments.Add(((int)Math.Round((mt0 + t) * F), what));
            var frames = ReadFrames(ff, mp4, moments.Select(x => x.k).ToHashSet());
            var pv = new Preview { Ed = ed, Width = W, Height = H, Background = Brushes.Black };
            int ok = 0; double worstPsnr = 99, sum = 0; var miss = new List<string>();
            foreach (var (k, what) in moments)
            {
                double t = k / F;
                var swp = Stopwatch.StartNew();
                do { pv.Sync(t, false); await Task.Delay(10); } while (!pv.Settled(t) && swp.ElapsedMilliseconds < 3000);
                pv.Measure(new Size(W, H)); pv.Arrange(new Rect(0, 0, W, H)); pv.UpdateLayout();
                var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
                var a = new byte[W * H * 4]; rtb.CopyPixels(a, W * 4, 0);
                if (!frames.TryGetValue(k, out var b)) { miss.Add($"{what}: no export frame"); continue; }
                var (psnr, worst) = FxTest.Compare(a, b);
                worstPsnr = Math.Min(worstPsnr, psnr); sum += psnr;
                if (psnr >= 32 && worst <= 24) ok++; else miss.Add($"{what} (t={t:0.000}): {psnr:0.0} dB, worst block {worst:0.0}");
                if (what.EndsWith("complete") || psnr < 32) Save(FxTest.SideBySide(a, b), Path.Combine(dir, "graphics_" + string.Concat(what.Where(char.IsLetterOrDigit)) + ".png"));
            }
            Check(ok == moments.Count, $"preview = export for every template coming in, complete and going out, and the animated shape matte ({ok} / {moments.Count}; mean {sum / moments.Count:0.0} dB, lowest {worstPsnr:0.0} dB)" +
                                        (miss.Count > 0 ? ": " + string.Join("; ", miss.Take(8)) : ""));
        }

        // ---------- per-frame cost of animating layers ----------
        {
            var c = GraphicLibrary.Instance(GraphicLibrary.BuiltIn.First(t => t.Name == "Like · comment · share"));
            var tv = new TitleVisual(); tv.Update(c, false, null, 0);
            tv.Measure(new Size(W, H)); tv.Arrange(new Rect(0, 0, W, H));
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 300; i++) tv.Update(c, false, null, i / 60.0);
            double ms = sw.Elapsed.TotalMilliseconds / 300;
            Check(ms < 0.5, $"animating a template's layers per frame: {ms * 1000:0} µs (transforms only, nothing rebuilt)");
        }

        // ---------- Add Graphic: track, undo, 9:16 ----------
        {
            var ed = new Editor();
            ed.Media.Clear();
            var tpl = GraphicLibrary.BuiltIn[0];
            ed.Time = 1;
            var a = ed.AddGraphic(tpl); var b = ed.AddGraphic(tpl); var c = ed.AddGraphic(tpl);
            Check(a != null && b != null && c != null && a.Track == Tracks.V(2) && Near(a.Start, 1) && b.Track == Tracks.V(3) && Near(b.Start, 1) &&
                  c.Track == Tracks.V(2) && Near(c.Start, a.End) && ed.UndoLabels.First() == "Add Graphic" && a.Shapes!.Count == tpl.Clip.Shapes!.Count && a.Id != b.Id,
                  $"Add Graphic: at the playhead on V2, then V3, then V2 after the first ({Tracks.Label(a!.Track)} {a.Start:0.0} s, {Tracks.Label(b!.Track)}, {Tracks.Label(c!.Track)} {c.Start:0.0} s), its own copy of the layers");
            ed.Undo();
            Check(ed.Clips.Count == 2, "undo removes the last graphic");
            a.Shapes![0].W += 10;
            Check(tpl.Clip.Shapes![0].W != a.Shapes[0].W, "the template isn't changed by editing a graphic made from it");
            Editor.SetSequence(1080, 1920, 30, ReframeMode.Fit);
            var v = new Editor();
            var g9 = v.AddGraphic(tpl);
            Check(g9 != null && g9.Scale < 60 && g9.Scale > 40, $"in a 9:16 sequence it comes in scaled to {g9?.Scale:0.#} % (made for 16:9)");
            Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
        }

        // ---------- Effect Controls' undo: a property changed, then Touch() (how every inspector row reports a change) ----------
        {
            var e = new Editor();
            var c = new Clip { Kind = ClipKind.Text, Text = "A", Track = Tracks.V(2), Dur = 2, Shapes = new() { new ShapeLayer { Kind = ShapeKind.Ellipse } } };
            e.Edit(() => e.Clips.Add(c), "Add");
            c.Opacity = 50; e.Touch(); e.Commit();
            int n1 = e.UndoLabels.Count();
            e.Undo();
            bool one = e.Clips.FirstOrDefault(x => x.Id == c.Id)?.Opacity == 100;
            var c2 = e.Clips.First(x => x.Id == c.Id);
            c2.Shapes!.Add(Shapes.Sticker("Heart")); e.Touch();
            c2.Shapes[1].X = 30; e.Touch(); e.Commit();
            e.Undo();
            var c3 = e.Clips.FirstOrDefault(x => x.Id == c.Id);
            Check(n1 == 2 && one && c3 != null && c3.Shapes!.Count == 1, $"an Effect Controls change (made, then reported) is one undo step back to before it (a single change: {(one ? "undone" : "lost")}; a layer added and moved: {(c3?.Shapes?.Count == 1 ? "undone" : c3 == null ? "the clip went too" : "not undone")})");
        }

        // ---------- your own templates, project round trip ----------
        {
            GraphicLibrary.StoreOverride = Path.Combine(dir, "graphics.json");
            if (File.Exists(GraphicLibrary.StoreOverride)) File.Delete(GraphicLibrary.StoreOverride);
            GraphicLibrary.Reload();
            var c = GraphicLibrary.Instance(GraphicLibrary.BuiltIn.First(t => t.Name == "Subscribe button"));
            c.Text = "FOLLOW"; c.Start = 9; c.Track = 3; c.Shapes![0].Fill = "#FF2266FF";
            GraphicLibrary.Save("My follow button", c);
            GraphicLibrary.Reload();
            var mine = GraphicLibrary.User.FirstOrDefault(t => t.Name == "My follow button");
            Check(File.Exists(GraphicLibrary.StoreOverride) && mine != null && !mine.BuiltIn && mine.Clip.Text == "FOLLOW" && mine.Clip.Shapes?[0].Fill == "#FF2266FF" && mine.Clip.Start == 0 &&
                  GraphicLibrary.All.Contains(mine), "Save as Graphic Template: kept in the user's file (text, layers, colours; not its place), listed after the built-in ones");
            GraphicLibrary.Delete("My follow button"); GraphicLibrary.Reload();
            Check(GraphicLibrary.User.All(t => t.Name != "My follow button"), "Delete Template");
            GraphicLibrary.StoreOverride = null; GraphicLibrary.Reload();
            var json = JsonSerializer.Serialize(c);
            var back = JsonSerializer.Deserialize<Clip>(json)!;
            Check(TitleVisual.Signature(back, false) == TitleVisual.Signature(c, false) && back.Shapes!.Count == c.Shapes.Count && back.Shapes[1].Sticker == "Bell",
                  "saved and opened: the layers come back the same (the drawing's signature matches)");
            var sig0 = TitleVisual.Signature(c, false); c.Shapes[0].Radius += 1;
            Check(TitleVisual.Signature(c, false) != sig0, "changing a layer changes the drawing's signature (the preview redraws)");
        }
    }

    /// <summary>Pixels with some coverage.</summary>
    static int Covered(BitmapSource b)
    {
        var bgra = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight; var px = new byte[w * h * 4]; bgra.CopyPixels(px, w * 4, 0);
        int n = 0; for (int i = 3; i < px.Length; i += 4) if (px[i] > 32) n++;
        return n;
    }
    /// <summary>Leftmost / rightmost covered column.</summary>
    static (int left, int right) Extent(BitmapSource b)
    {
        var bgra = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight; var px = new byte[w * h * 4]; bgra.CopyPixels(px, w * 4, 0);
        int l = w, r = -1;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (px[(y * w + x) * 4 + 3] > 64) { l = Math.Min(l, x); r = Math.Max(r, x); }
        return (l, r);
    }
    /// <summary>How many pixels of row y are the progress bar's red.</summary>
    static int RedRun(BitmapSource b, int y)
    {
        var bgra = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth; var px = new byte[w * 4]; bgra.CopyPixels(new Int32Rect(0, y, w, 1), px, w * 4, 0);
        int n = 0; for (int x = 0; x < w; x++) { int i = x * 4; if (px[i + 2] > 200 && px[i + 1] < 120 && px[i + 3] > 200) n++; }
        return n;
    }

    /// <summary>The export's frames with the given numbers (BGRA at the canvas size).</summary>
    static Dictionary<int, byte[]> ReadFrames(string ff, string file, HashSet<int> want)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-i", file, "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{W}x{H}", "pipe:1" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        var res = new Dictionary<int, byte[]>(); var s = p.StandardOutput.BaseStream; int last = want.Count == 0 ? -1 : want.Max();
        for (int k = 0; k <= last; k++)
        {
            var buf = new byte[W * H * 4];
            int got = 0; while (got < buf.Length) { int n = s.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
            if (got < buf.Length) break;
            if (want.Contains(k)) res[k] = buf;
        }
        try { if (!p.HasExited) p.Kill(); } catch { }
        return res;
    }

    static void Save(BitmapSource b, string path)
    {
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(b));
        using var fs = File.Create(path); enc.Save(fs);
    }
}
