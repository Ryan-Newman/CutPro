using System.Diagnostics;

namespace CutPro;

/// <summary>Headless checks for AI object selection: CutPro.exe --samprobe out.txt (model IO, speed, click-to-mask accuracy).</summary>
public static class ObjectTest
{
    /// <summary>Synthetic scene: textured background + a filled circle "object" at (cx,cy,r). BGRA pixels.</summary>
    public static byte[] Scene(int w, int h, double cx, double cy, double r)
    {
        var px = new byte[w * h * 4];
        var rnd = new Random(7);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool obj = (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;
                int stripes = ((x / 23) + (y / 31)) % 2 == 0 ? 40 : 0, n = rnd.Next(25);
                if (obj) { px[i + 2] = (byte)(210 + n); px[i + 1] = (byte)(60 + n); px[i] = (byte)(40 + n); }                  // red-ish ball
                else { px[i + 2] = (byte)(70 + stripes + n); px[i + 1] = (byte)(110 + stripes + n); px[i] = (byte)(90 + x * 60 / w + n); }
                px[i + 3] = 255;
            }
        return px;
    }

    public static double IoU(float[] logits, int w, int h, double cx, double cy, double r)
    {
        long inter = 0, uni = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool m = logits[y * w + x] > 0, g = (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;
                if (m && g) inter++; if (m || g) uni++;
            }
        return uni == 0 ? 0 : (double)inter / uni;
    }

    public static int RunProbe(string outFile)
    {
        var log = new List<string>();
        if (!SamEngine.ModelsPresent) { File.WriteAllText(outFile, "models missing"); return 1; }
        int w = SamEngine.Side, h = 576;
        var img = Scene(w, h, 420, 300, 130);
        foreach (bool gpu in new[] { true, false })
        {
            using var sam = new SamEngine(gpu);
            if (gpu) log.Add(sam.Describe());
            var sw = Stopwatch.StartNew();
            var emb = sam.Encode(img, w, h);
            double first = sw.Elapsed.TotalMilliseconds;
            sw.Restart(); for (int i = 0; i < 5; i++) emb = sam.Encode(img, w, h);
            double encMs = sw.Elapsed.TotalMilliseconds / 5;
            sw.Restart();
            var (mask, low, score) = sam.Decode(emb, new[] { (420f, 300f, 1f) }, null, w, h);
            double decMs = sw.Elapsed.TotalMilliseconds;
            log.Add($"{sam.Provider}: encode first {first:0} ms, then {encMs:0} ms; decode {decMs:0} ms; click IoU {IoU(mask, w, h, 420, 300, 130):0.000} score {score:0.00} lowres {low.Length}");
            // box prompt + previous mask (tracking step) on the ball moved by (+40,+10)
            var img2 = Scene(w, h, 460, 310, 130);
            var emb2 = sam.Encode(img2, w, h);
            var (m2, _, s2) = sam.Decode(emb2, new[] { (300f, 170f, 2f), (590f, 440f, 3f) }, low, w, h);
            log.Add($"{sam.Provider}: box-prompt track step IoU {IoU(m2, w, h, 460, 310, 130):0.000} score {s2:0.00}");
        }
        File.WriteAllLines(outFile, log);
        return 0;
    }

    // ---------------- end to end: CutPro.exe --objtest dir ----------------
    static int W => Editor.W;
    const int H = Editor.H;
    /// <summary>Ground-truth ball in ball.mp4 at source frame k: centre and radius in 1280x720 pixels (overlay rounds x/y down to even).</summary>
    static (double cx, double cy, double r) Ball(int k)
    {
        double t = k / 30.0;
        return (((int)(40 + 250 * t) & ~1) + 79.5, ((int)(260 + 80 * Math.Sin(1.5 * t)) & ~1) + 79.5, 78);
    }

    static double IoU(bool[] a, int w, int h, (double cx, double cy, double r) b, double scale)
    {
        long inter = 0, uni = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double dx = (x + 0.5) / scale - b.cx, dy = (y + 0.5) / scale - b.cy;
                bool g = dx * dx + dy * dy <= b.r * b.r, m = a[y * w + x];
                if (m && g) inter++; if (m || g) uni++;
            }
        return uni == 0 ? 0 : (double)inter / uni;
    }

    static string Run(string ff, params string[] args)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd(); p.WaitForExit();
        return p.ExitCode == 0 ? "" : err;
    }

    public static async Task<int> RunE2E(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        string I(double d) => d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        // 1. synthetic clip: a shaded ball moving over noisy colour bars (4 s, 30 fps)
        var video = Path.Combine(dir, "ball.mp4");
        if (!File.Exists(video))
        {
            var e = Run(ff, "-y", "-v", "error", "-f", "lavfi", "-i", "smptehdbars=s=1280x720:r=30:d=4,noise=alls=12:allf=t",
                "-f", "lavfi", "-i", "color=c=black:s=160x160:r=30:d=4,format=rgba,geq=r='235-0.6*hypot(X-55,Y-55)':g='95-0.3*hypot(X-55,Y-55)':b='45':a='if(lt(hypot(X-79.5,Y-79.5),78),255,0)'",
                "-filter_complex", "[0:v][1:v]overlay=x='40+250*t':y='260+80*sin(1.5*t)'", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "16", "-g", "30", video);
            if (e != "") throw new Exception("test clip: " + e);
        }
        var m = await MediaProbe.ProbeAsync(video) ?? throw new Exception("probe failed: " + MediaProbe.LastError);
        const int RW = 320, RH = 180; const double RS = RW / 1280.0;

        // 2. AI click on frame 30
        int seed = 30; var b30 = Ball(seed);
        var sw = Stopwatch.StartNew();
        var polys = await ObjectTracker.SelectAsync(m, seed, new[] { ((float)(b30.cx / W), (float)(b30.cy / H), 1f) });
        double selMs = sw.Elapsed.TotalMilliseconds;
        double selIoU = IoU(MaskGeometry.Raster(polys, RW, RH), RW, RH, b30, RS);
        Check(selIoU >= 0.9, $"AI click-select on frame {seed}: IoU {selIoU:0.000} ({selMs:0} ms incl. model load, {ObjectTracker.Provider})");

        // 3. AI tracking both ways over the whole clip
        var ai = new ObjectMask { Kind = MaskKind.AI, Name = "Ball", Effect = MaskFx.Redact, Feather = 0 };
        var aiTrack = new MaskTrack(); aiTrack.Set(seed, polys, key: true);
        var noProgress = new Progress<(int, int)>();
        void StoreAi(int f, List<float[]> p) { lock (aiTrack) aiTrack.Set(f, p); }
        sw.Restart();
        var r1 = await ObjectTracker.TrackAsync(m, ai, polys, seed, 119, StoreAi, noProgress, CancellationToken.None);
        var r2 = await ObjectTracker.TrackAsync(m, ai, polys, seed, 0, StoreAi, noProgress, CancellationToken.None);
        double trackS = sw.Elapsed.TotalSeconds;
        var ious = Enumerable.Range(0, 120).Select(k => IoU(MaskGeometry.Raster(aiTrack.At(k), RW, RH), RW, RH, Ball(k), RS)).ToList();
        int worstK = ious.IndexOf(ious.Min());
        Check(r1 == null && r2 == null && aiTrack.Frames.Count == 120 && ious.Average() >= 0.9 && ious.Min() >= 0.75,
              $"AI tracking 120 frames ({r1 ?? "ok"}/{r2 ?? "ok"}): mean IoU {ious.Average():0.000}, min {ious.Min():0.000} at frame {worstK}, {119 / trackS:0.0} fps");

        // 4. drawn ellipse tracked forwards (template matching)
        var el = new ObjectMask { Kind = MaskKind.Ellipse, Name = "Ellipse" };
        var elStart = new List<float[]> { MaskGeometry.Shape(MaskKind.Ellipse, new System.Windows.Rect((b30.cx - 80) / W, (b30.cy - 80) / H, 160.0 / W, 160.0 / H)) };
        var elTrack = new MaskTrack(); elTrack.Set(seed, elStart, key: true);
        sw.Restart();
        var r3 = await ObjectTracker.TrackAsync(m, el, elStart, seed, 119, (f, p) => { lock (elTrack) elTrack.Set(f, p); }, noProgress, CancellationToken.None);
        double elS = sw.Elapsed.TotalSeconds;
        var eIous = Enumerable.Range(seed, 90).Select(k => IoU(MaskGeometry.Raster(elTrack.At(k), RW, RH), RW, RH, Ball(k), RS)).ToList();
        Check(r3 == null && eIous.Average() >= 0.85 && eIous.Min() >= 0.75, $"ellipse tracking 89 frames: mean IoU {eIous.Average():0.000}, min {eIous.Min():0.000}, {89 / elS:0.0} fps");

        // 4b. keyframes alone animate a mask (interpolation), and one more keyframe fixes the worst frames
        List<float[]> Circle(int k, double rad) { var b = Ball(k); return new() { MaskGeometry.Shape(MaskKind.Ellipse, new System.Windows.Rect((b.cx - rad) / W, (b.cy - rad) / H, 2 * rad / W, 2 * rad / H)) }; }
        var kf = new MaskTrack();
        kf.Set(30, Circle(30, 80), key: true); kf.Set(90, Circle(90, 80), key: true);
        var kIou = Enumerable.Range(30, 61).Select(k => IoU(MaskGeometry.Raster(kf.At(k), RW, RH), RW, RH, Ball(k), RS)).ToList();
        kf.Set(60, Circle(60, 80), key: true);
        var kIou2 = Enumerable.Range(30, 61).Select(k => IoU(MaskGeometry.Raster(kf.At(k), RW, RH), RW, RH, Ball(k), RS)).ToList();
        Check(kIou.Average() >= 0.75 && kIou2.Min() > kIou.Min() && kIou2.Average() >= 0.85,
              $"keyframes 30+90 interpolate the ellipse: mean IoU {kIou.Average():0.000} (min {kIou.Min():0.000}); + key at 60: mean {kIou2.Average():0.000} (min {kIou2.Min():0.000})");

        // 4c. tracking keeps keyframes and re-anchors on them: a smaller ellipse keyed at 75 is followed from there on
        var anc = new MaskTrack(); anc.Set(30, Circle(30, 80), key: true);
        var key75 = Circle(75, 50); anc.Set(75, key75, key: true);
        var ancKeys = anc.Keys.Where(k => k != 30).ToDictionary(k => k, k => anc.Frames[k]);
        var r4 = await ObjectTracker.TrackAsync(m, el, anc.Frames[30], 30, 119, (f, p) => { lock (anc) if (!anc.IsKey(f)) anc.Set(f, p); }, noProgress, CancellationToken.None, ancKeys);
        var after = Enumerable.Range(76, 44).Select(k => IoU(MaskGeometry.Raster(anc.At(k), RW, RH), RW, RH, (Ball(k).cx, Ball(k).cy, 50), RS)).ToList();
        var before = Enumerable.Range(31, 44).Select(k => IoU(MaskGeometry.Raster(anc.At(k), RW, RH), RW, RH, Ball(k), RS)).ToList();
        Check(r4 == null && ReferenceEquals(anc.Frames[75], key75) && after.Average() >= 0.8 && before.Average() >= 0.85,
              $"ellipse tracking through a keyframe: key kept, before it mean IoU {before.Average():0.000}, after it (key's smaller shape) {after.Average():0.000}");

        // 4d. AI tracking through a keyframe (a drawn circle keyed at 60): the key stays, tracking continues from it
        var aiK = new MaskTrack(); aiK.Set(30, polys, key: true);
        var key60 = Circle(60, 78); aiK.Set(60, key60, key: true);
        var r5 = await ObjectTracker.TrackAsync(m, ai, polys, 30, 90, (f, p) => { lock (aiK) if (!aiK.IsKey(f)) aiK.Set(f, p); }, noProgress, CancellationToken.None,
                                                aiK.Keys.Where(k => k != 30).ToDictionary(k => k, k => aiK.Frames[k]));
        var aiAfter = Enumerable.Range(61, 30).Select(k => IoU(MaskGeometry.Raster(aiK.At(k), RW, RH), RW, RH, Ball(k), RS)).ToList();
        Check(r5 == null && ReferenceEquals(aiK.Frames[60], key60) && aiK.Frames.Count == 61 && aiAfter.Average() >= 0.9,
              $"AI tracking through a keyframe: key kept, {aiK.Frames.Count} outlines, after it mean IoU {aiAfter.Average():0.000}");

        // 5. export of the tracked mask on a trimmed, moved clip: redacted pixels must sit on the ball of the same source frame
        var ed = new Editor();
        ed.Media.Add(m);
        var vc = new Clip { MediaId = m.Id, Kind = ClipKind.Video, Track = 2, Start = 0.2, In = 0.5, Dur = 3.0, NoAudio = true };
        vc.Masks.Add(ai); ed.Clips.Add(vc); ed.MaskTracks[ai.Id] = aiTrack;
        var maskFiles = MaskExport.Prepare(ed, Path.Combine(dir, "masks"));
        var mp4 = Path.Combine(dir, "objects.mp4");
        var err = await Exporter.RunAsync(ff, ed, mp4, new Progress<double>(), CancellationToken.None, null, maskFiles);
        Check(err == null && maskFiles.Count == 1, "export with tracked mask" + (err == null ? "" : ": " + err));
        if (err == null)
            foreach (int k in new[] { 0, 2, 31, 60, 89 })
            {
                double tt = 0.2 + k / 30.0 - 0.001, ts = 0.5 + k / 30.0 - 0.001;     // first frame at or after t
                var a = FxTest.Frame(ff, mp4, tt); var src = FxTest.Frame(ff, video, ts);
                var changed = new bool[RW * RH];
                for (int y = 0; y < RH; y++)
                    for (int x = 0; x < RW; x++)
                    {
                        int i = ((y * 4 + 2) * W + x * 4 + 2) * 4;       // sample the centre of each 4x4 cell
                        changed[y * RW + x] = Math.Abs(a[i] - src[i]) + Math.Abs(a[i + 1] - src[i + 1]) + Math.Abs(a[i + 2] - src[i + 2]) > 90;
                    }
                int f = 15 + k;
                var byOffset = Enumerable.Range(-2, 5).Select(o => IoU(changed, RW, RH, Ball(f + o), RS)).ToList();
                int best = byOffset.IndexOf(byOffset.Max()) - 2;
                var trk = Enumerable.Range(-1, 3).Select(o => IoU(MaskGeometry.Raster(aiTrack.At(f), RW, RH), RW, RH, Ball(f + o), RS)).ToList();
                Check(byOffset[2] >= 0.85 && (best == 0 || trk[0] > trk[1]), $"export redact at timeline {I(tt)} s = source frame {f}: IoU {byOffset[2]:0.000}, best frame offset {best} (tracked outline vs ball f-1/f/f+1: {string.Join(" / ", trk.Select(v => v.ToString("0.000")))})");
            }

        // 6. preview vs export for every mask effect (image clips: the preview renders synchronously)
        var pattern = Path.Combine(dir, "pattern.png");
        FxTest.Save(FxTest.Pattern(), pattern);
        var pm = await MediaProbe.ProbeAsync(pattern) ?? throw new Exception("pattern import failed");
        var ed2 = new Editor(); ed2.Media.Add(pm);
        var cases = new List<(string name, ObjectMask mask, Action<Clip>? f)>
        {
            ("blur object", new ObjectMask { Effect = MaskFx.Blur, Amount = 25, Feather = 6 }, null),
            ("pixelate", new ObjectMask { Effect = MaskFx.Pixelate, Amount = 20, Feather = 0 }, null),
            ("spotlight (darken, inverted)", new ObjectMask { Effect = MaskFx.Darken, Amount = 70, Invert = true, Feather = 20 }, null),
            ("colour pop (desaturate, inverted)", new ObjectMask { Effect = MaskFx.Desaturate, Invert = true, Feather = 6 }, null),
            ("redact, expanded 20", new ObjectMask { Effect = MaskFx.Redact, Expand = 20, Feather = 0 }, null),
            ("tint 45%", new ObjectMask { Effect = MaskFx.Tint, Amount = 45, TintColor = "#FF00C8FF", Feather = 10 }, null),
            ("focus (blur background) on a scaled, moved clip", new ObjectMask { Effect = MaskFx.Blur, Amount = 30, Invert = true, Feather = 10 }, c => { c.Scale = 60; c.X = 15; c.Y = 10; }),
            ("cut out object", new ObjectMask { Effect = MaskFx.CutOut, Feather = 8 }, null),
        };
        // outline: a lasso-like pentagon around the lime circle, so it's not just an ellipse
        var shape = new List<float[]> { new float[] { 330f / W, 430f / H, 470f / W, 420f / H, 500f / W, 560f / H, 400f / W, 620f / H, 320f / W, 560f / H } };
        for (int i = 0; i < cases.Count; i++)
        {
            var c = new Clip { MediaId = pm.Id, Kind = ClipKind.Image, Track = 2, Start = i, Dur = 1 };
            cases[i].f?.Invoke(c);
            var mk = cases[i].mask; mk.Kind = MaskKind.Freehand;
            c.Masks.Add(mk); ed2.Clips.Add(c);
            ed2.MaskTracks[mk.Id] = new MaskTrack { Frames = { [0] = shape } };
        }
        var files2 = MaskExport.Prepare(ed2, Path.Combine(dir, "masks2"));
        var mp4b = Path.Combine(dir, "maskfx.mp4");
        var err2 = await Exporter.RunAsync(ff, ed2, mp4b, new Progress<double>(), CancellationToken.None, null, files2);
        Check(err2 == null, "export of every mask effect" + (err2 == null ? "" : ": " + err2));
        if (err2 == null)
        {
            var pv = new Preview { Ed = ed2, Width = W, Height = H, Background = System.Windows.Media.Brushes.Black };
            for (int i = 0; i < cases.Count; i++)
            {
                double t = i + 0.5;
                pv.Sync(t, false);
                pv.Measure(new System.Windows.Size(W, H)); pv.Arrange(new System.Windows.Rect(0, 0, W, H)); pv.UpdateLayout();
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(W, H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); rtb.Render(pv);
                var a = new byte[W * H * 4]; rtb.CopyPixels(a, W * 4, 0);
                var b = FxTest.Frame(ff, mp4b, t);
                var (psnr, worst) = FxTest.Compare(a, b);
                Check(psnr >= 30 && worst <= 24, $"preview = export, {cases[i].name}: PSNR {psnr:0.0} dB, worst 16x16 block {worst:0.0}/255");
                FxTest.Save(FxTest.SideBySide(a, b), Path.Combine(dir, $"mask_compare_{i}.png"));
            }
        }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "objtest.txt"), log);
        return fails == 0 ? 0 : 1;
    }
}
