using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CutPro;

/// <summary>
/// CutPro.exe --extratest dir: the "extra high" roadmap items, headless.
/// - trim tools: rolling edit (both sides and the linked sound, limits = media / one frame), slip, slide, ripple edit, rate stretch
///   (speed fills the new length, same source range, reversed clips), clamping to the last frame that works, one undo step each;
/// </summary>
public static partial class ExtraTest
{
    static List<string> log = new();
    static int fails;
    static void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
    static string ff = "";
    static readonly CultureInfo inv = CultureInfo.InvariantCulture;

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        log = new(); fails = 0;
        ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        AppSettings.ReadOnly = true;
        try
        {
            Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
            var patF = Gen(dir, "pat.mp4", "-f lavfi -i testsrc2=s=640x360:r=30:d=12 -f lavfi -i sine=frequency=440:duration=12 -c:v libx264 -crf 20 -pix_fmt yuv420p -c:a aac -shortest");
            var mPat = await Imp(patF);
            await Section("trim tools", () => TrimTools(mPat));
            await Section("generators", () => GeneratorsTest(dir));
            await Section("alpha export", () => AlphaExport(dir));
            await Section("render queue", () => QueueTest(dir, mPat));
            await Section("interchange import", () => ImportTest(dir, mPat));
            await Section("secondary colour", () => SecondaryTest(dir));
            await Section("attach to object", () => FollowTest(dir));
            await Section("text animation", () => TextAnimTest(dir));
            await Section("noise reduction, frame interpolation", () => RenderedFxTest(dir));
            await Section("channels: mapping, mono, 5.1", () => ChannelsTest(dir));
            await Section("audio time units", () => AudioUnitsTest(dir));
            await Section("proxies", () => ProxyTest(dir));
            await Section("distort", () => DistortTest(dir));
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "extratest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static async Task Section(string name, Func<Task> body)
    {
        var sw = Stopwatch.StartNew();
        try { await body(); } catch (Exception ex) { Check(false, $"{name}: exception {ex}"); }
        log.Add($"  ({name}: {sw.Elapsed.TotalSeconds:0.0} s)");
    }
    static Task Section(string name, Action body) => Section(name, () => { body(); return Task.CompletedTask; });

    static string Gen(string dir, string name, string args)
    {
        var f = Path.Combine(dir, name);
        if (!File.Exists(f)) Exec($"-y -v error {args} \"{f}\"");
        return f;
    }
    static async Task<MediaItem> Imp(string f) => await MediaProbe.ProbeAsync(f) ?? throw new Exception("import " + f + ": " + MediaProbe.LastError);
    internal static string Exec(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
        var err = p.StandardError.ReadToEnd(); p.WaitForExit(); return err;
    }
    static bool Near(double a, double b, double tol = 1e-6) => Math.Abs(a - b) < tol;

    /// <summary>A linked video + audio pair of the media on V1 / A1.</summary>
    static (Clip v, Clip a) Pair(Editor ed, MediaItem m, double start, double inPt, double dur)
    {
        var link = Guid.NewGuid().ToString("N");
        var v = new Clip { Kind = ClipKind.Video, MediaId = m.Id, Track = Tracks.V1, Start = start, In = inPt, Dur = dur, LinkId = link, NoAudio = true };
        var a = new Clip { Kind = ClipKind.Audio, MediaId = m.Id, Track = Tracks.A1, Start = start, In = inPt, Dur = dur, LinkId = link };
        ed.Clips.Add(v); ed.Clips.Add(a);
        return (v, a);
    }

    /// <summary>One frame of a file at time t as BGRA bytes (ffmpeg, w × h).</summary>
    internal static byte[] Frame(string file, double t, int w, int h, string vf = "", string pre = "")
    {
        var outF = Path.Combine(Path.GetTempPath(), $"cutpro_xt_{Guid.NewGuid():N}.raw");
        Exec($"-y -v error -ss {t.ToString("0.######", inv)} {pre} -i \"{file}\" -frames:v 1 -vf \"{(vf.Length > 0 ? vf + "," : "")}scale={w}:{h}:flags=neighbor,format=bgra\" -f rawvideo \"{outF}\"");
        var b = File.Exists(outF) ? File.ReadAllBytes(outF) : Array.Empty<byte>();
        try { File.Delete(outF); } catch { }
        return b;
    }
    /// <summary>A file's sound as 48 kHz stereo floats (interleaved).</summary>
    internal static float[] Sound(string file, int channels = 2)
    {
        var outF = Path.Combine(Path.GetTempPath(), $"cutpro_xt_{Guid.NewGuid():N}.f32");
        Exec($"-y -v error -i \"{file}\" -vn -ac {channels} -ar 48000 -f f32le \"{outF}\"");
        var b = File.Exists(outF) ? File.ReadAllBytes(outF) : Array.Empty<byte>();
        try { File.Delete(outF); } catch { }
        var f = new float[b.Length / 4]; Buffer.BlockCopy(b, 0, f, 0, f.Length * 4);
        return f;
    }

    // ---------------- generators ----------------
    static async Task GeneratorsTest(string dir)
    {
        Generators.DirOverride = Path.Combine(dir, "generated");
        if (Directory.Exists(Generators.DirOverride)) Directory.Delete(Generators.DirOverride, true);
        try
        {
            var matte = new Generator { Kind = GenKind.ColorMatte, Color1 = "#3366CC", W = 320, H = 180, Fps = 30 };
            var mm = await Generators.MakeAsync(matte);
            var px = mm != null ? Frame(mm.Path, 0, 320, 180) : Array.Empty<byte>();
            bool uniform = px.Length == 320 * 180 * 4 && Enumerable.Range(0, 320 * 180).All(i => px[i * 4] == 0xCC && px[i * 4 + 1] == 0x66 && px[i * 4 + 2] == 0x33);
            Check(mm != null && mm.Type == ClipKind.Image && mm.Name == "Colour Matte" && mm.Width == 320 && uniform, $"colour matte: a {mm?.Width}×{mm?.Height} picture of exactly #3366CC, named “{mm?.Name}”");
            var again = await Generators.EnsureAsync(new Generator { Kind = GenKind.ColorMatte, Color1 = "#3366CC", W = 320, H = 180, Fps = 30 });
            Check(again == mm!.Path && Directory.GetFiles(Generators.DirOverride).Length == 1, "the same settings reuse the file (named by its settings)");

            var grad = new Generator { Kind = GenKind.Gradient, Color1 = "#000000", Color2 = "#FFFFFF", Angle = 0, W = 320, H = 180 };
            var gm = await Generators.MakeAsync(grad);
            var gp = Frame(gm!.Path, 0, 320, 180);
            int L(int x) => gp[(90 * 320 + x) * 4 + 1];
            Check(L(2) < 10 && L(317) > 245 && Math.Abs(L(160) - 128) < 8 && L(80) < L(160) && L(160) < L(240), $"linear gradient left → right: {L(2)} … {L(160)} … {L(317)}");
            grad.Radial = true;
            var rp = Frame((await Generators.MakeAsync(grad))!.Path, 0, 320, 180);
            Check(rp[(90 * 320 + 160) * 4 + 1] < 10 && rp[(1 * 320 + 1) * 4 + 1] > 240, "radial gradient: the start colour in the centre, the end colour in the corners");

            var bars = new Generator { Kind = GenKind.Bars, W = 640, H = 360, Fps = 30, Seconds = 3, ToneDb = -20, ToneHz = 1000 };
            var bm = await Generators.MakeAsync(bars);
            var snd = bm != null ? Sound(bm.Path) : Array.Empty<float>();
            double peak = snd.Length > 0 ? snd.Max(Math.Abs) : 0, peakDb = 20 * Math.Log10(Math.Max(peak, 1e-9));
            int zc = 0; for (int i = 2; i < Math.Min(snd.Length, 96000); i += 2) if (snd[i - 2] < 0 && snd[i] >= 0) zc++;
            Check(bm != null && bm.Type == ClipKind.Video && bm.HasAudio && Math.Abs(bm.Duration - 3) < 0.05 && Math.Abs(peakDb + 20) < 0.2 && Math.Abs(zc - 1000) <= 2,
                  $"bars and tone: {bm?.Width}×{bm?.Height} video {bm?.Duration:0.00} s, tone peak {peakDb:0.00} dBFS at {zc} Hz");
            var bp = Frame(bm!.Path, 1, 640, 360);
            Check(bp.Length > 0 && bp[(60 * 640 + 20) * 4 + 2] > 80 && bp[(60 * 640 + 320) * 4 + 1] > 120, "the bars are drawn");

            var cd = new Generator { Kind = GenKind.Countdown, W = 640, H = 360, Fps = 30, Seconds = 8, ToneDb = -20 };
            var cm = await Generators.MakeAsync(cd);
            var cs = cm != null ? Sound(cm.Path) : Array.Empty<float>();
            int first = -1, last = -1;
            for (int i = 0; i < cs.Length; i += 2) if (Math.Abs(cs[i]) > 0.01) { if (first < 0) first = i / 2; last = i / 2; }
            double popAt = first / 48000.0, popLen = (last - first + 1) / 48000.0;
            Check(cm != null && Math.Abs(cm.Duration - 8) < 0.05 && Math.Abs(popAt - 6) < 0.002 && popLen < 1 / 30.0 + 0.002,
                  $"counting leader: {cm?.Duration:0.00} s, the beep at {popAt:0.000} s (the “2”, two seconds before the end) for {popLen * 1000:0} ms");
            int Grey(byte[] f) => f.Length > 0 ? f[(20 * 640 + 20) * 4 + 1] : -1;
            var f0 = Frame(cm!.Path, 0.5, 640, 360); var f7 = Frame(cm.Path, 7, 640, 360);
            Check(Grey(f0) > 60 && Grey(f7) < 10 && Generators.LeaderNumber(cd, 0, out _) == 8 && Generators.LeaderNumber(cd, 180, out _) == 2 && Generators.LeaderNumber(cd, 181, out _) == 0,
                  "the leader counts 8 … 2 (the 2 for one frame), then black");
            File.Delete(cm.Path);
            var re = await Generators.EnsureAsync(cm.Gen!);
            Check(re == cm.Path && File.Exists(re), "a missing generated file is rendered again from its settings");
            var pf = JsonSerializer.Deserialize<ProjectFile>(JsonSerializer.Serialize(new ProjectFile { Media = new() { mm } }))!;
            Check(pf.Media[0].Gen?.Color1 == "#3366CC" && pf.Media[0].Name == "Colour Matte", "the project file keeps the generator settings and name");
        }
        finally { Generators.DirOverride = null; }
    }

    /// <summary>Exports the editor's sequence like the window does (titles and masks prepared here on the UI thread).</summary>
    internal static async Task<string?> Export(Editor ed, string dir, string file, ExportSettings o)
    {
        var tmp = Path.Combine(dir, "tmp_" + Path.GetFileNameWithoutExtension(file));
        var titles = o.AudioOnly ? new Dictionary<string, string>() : TitleVisual.PrepareExport(ed, tmp, o.OutW, o.OutH);
        var masks = o.AudioOnly ? new Dictionary<string, string>() : MaskExport.Prepare(ed, tmp, null, o.Scale);
        return await Task.Run(() => Exporter.RunAsync(ff, ed, file, new Progress<double>(), CancellationToken.None, titles, masks, o));
    }

    // ---------------- alpha export ----------------
    static async Task AlphaExport(string dir)
    {
        Generators.DirOverride = Path.Combine(dir, "generated");
        var m = await Generators.MakeAsync(new Generator { Kind = GenKind.ColorMatte, Color1 = "#3366CC", W = 1280, H = 720 });
        Generators.DirOverride = null;
        var ed = new Editor();
        ed.Media.Add(m!);
        ed.Clips.Add(new Clip { Kind = ClipKind.Image, MediaId = m!.Id, Track = Tracks.V1, Start = 0, Dur = 1, Scale = 40, X = -30, Y = -30 });   // (the top-left corner: X / Y are % of the frame)
        var t = new Clip { Kind = ClipKind.Text, Track = Tracks.V(2), Start = 0, Dur = 1, Role = TextRole.Title, Text = "ALPHA", Style = Themes.Get(ed.ThemeName).For(TextRole.Title).Clone() };      // (the centre)
        ed.Clips.Add(t);
        var outDir = Path.Combine(dir, "alpha"); if (Directory.Exists(outDir)) Directory.Delete(outDir, true); Directory.CreateDirectory(outDir);
        int Px(int x, int y) => (y * 320 + x) * 4;
        var norm = Path.Combine(outDir, "normal.mp4");
        var errN = await Export(ed, dir, norm, new ExportSettings());
        var nf = Frame(norm, 0.5, 320, 180);
        foreach (var (name, video, container, pre) in new[] { ("prores4444a", "prores4444a", "mov", ""), ("vp9a", "vp9a", "webm", "-c:v libvpx-vp9"), ("qtrle", "qtrle", "mov", ""), ("png", "png", "png", "") })
        {
            var o = ExportSettings.Presets.First(p => p.Video == video).Clone();
            var file = Path.Combine(outDir, $"{name}.{container}");
            var err = await Export(ed, dir, file, o);
            string read = o.ImageSequence ? o.OutputPath(file).Replace("%05d", "00015") : file;
            var f = File.Exists(read) ? Frame(read, o.ImageSequence ? 0 : 0.5, 320, 180, "", pre) : Array.Empty<byte>();
            bool ok = err == null && f.Length == 320 * 180 * 4;
            int aC = ok ? f[Px(64, 36) + 3] : -1, aK = ok ? f[Px(314, 174) + 3] : -1, aT = ok ? Enumerable.Range(75, 30).SelectMany(y => Enumerable.Range(120, 80).Select(x => (int)f[Px(x, y) + 3])).Max() : -1;
            int dB = ok && nf.Length == f.Length ? Math.Max(Math.Abs(f[Px(64, 36)] - nf[Px(64, 36)]), Math.Max(Math.Abs(f[Px(64, 36) + 1] - nf[Px(64, 36) + 1]), Math.Abs(f[Px(64, 36) + 2] - nf[Px(64, 36) + 2]))) : 999;
            int frames = o.ImageSequence ? Directory.GetFiles(outDir, "png_*.png").Length : 0;
            Check(ok && aC == 255 && aK == 0 && aT > 200 && dB <= 3 && (!o.ImageSequence || frames == 30),
                  $"{o.Name}: alpha {aC} on the picture, {aK} where nothing is drawn, the title (apart from it) up to alpha {aT}; colour within {dB} of the normal export" + (o.ImageSequence ? $", {frames} pictures" : "") + (err != null ? " — " + err : ""));
        }
        Check(errN == null && nf.Length > 0 && nf[Px(314, 174)] < 8, "the normal export of the same sequence is black where nothing is drawn");
    }

    static double Psnr(byte[] a, byte[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0;
        double se = 0; long n = 0;
        for (int i = 0; i < a.Length; i++) { if ((i & 3) == 3) continue; double d = a[i] - b[i]; se += d * d; n++; }
        return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255 / (se / n));
    }

    // ---------------- render queue ----------------
    static async Task QueueTest(string dir, MediaItem m)
    {
        var ed = new Editor();
        ed.Media.Add(m);
        Pair(ed, m, 0, 1, 2);
        ed.Clips.Add(new Clip { Kind = ClipKind.Text, Track = Tracks.V(2), Start = 0, Dur = 2, Role = TextRole.Title, Text = "QUEUED", Style = Themes.Get(ed.ThemeName).For(TextRole.Title).Clone() });
        var outDir = Path.Combine(dir, "queue"); if (Directory.Exists(outDir)) Directory.Delete(outDir, true); Directory.CreateDirectory(outDir);
        var o = new ExportSettings { Height = 360 };
        var direct = Path.Combine(outDir, "direct.mp4");
        var errD = await Export(ed, dir, direct, o);
        var j1 = RenderQueue.Add(ed, o, Path.Combine(outDir, "queued.mp4"));
        // editing goes on while it renders: the job keeps the sequence as it was queued
        ed.Clips.First(c => c.Kind == ClipKind.Text).Text = "CHANGED"; ed.Clips.RemoveAll(c => c.Kind == ClipKind.Audio); ed.Clips.First(c => c.Kind == ClipKind.Video).In = 5;
        var j2 = RenderQueue.Add(ed, new ExportSettings { Height = 360 }, Path.Combine(outDir, "second.mp4"));
        var j3 = RenderQueue.Add(ed, new ExportSettings { Height = 360 }, Path.Combine(outDir, "third.mp4"));
        RenderQueue.Cancel(j3);
        bool idle = await Task.Run(() => RenderQueue.WaitIdle(180000));
        var a = Frame(direct, 1, 320, 180); var b = Frame(j1.Output, 1, 320, 180); var c2 = Frame(j2.Output, 1, 320, 180);
        double same = Psnr(a, b), changed = Psnr(a, c2);
        var sa = Sound(direct); var sb = Sound(j1.Output); var sc = Sound(j2.Output);
        double ra = sa.Length > 0 ? Math.Sqrt(sa.Average(x => (double)x * x)) : 0, rb = sb.Length > 0 ? Math.Sqrt(sb.Average(x => (double)x * x)) : 0, rc = sc.Length > 0 ? Math.Sqrt(sc.Average(x => (double)x * x)) : 0;
        Check(errD == null && idle && j1.Status == RenderQueue.State.Done && same > 45 && Math.Abs(ra - rb) < 1e-3 && ra > 0.01,
              $"a queued export renders the sequence as it was when queued, while it is edited (picture {same:0.0} dB vs Export, sound {rb:0.0000} = {ra:0.0000})");
        Check(j2.Status == RenderQueue.State.Done && changed < 30 && rc < 1e-4, $"the next job renders the edited sequence (different picture, {changed:0.0} dB; no sound — the audio clip was removed)");
        Check(j3.Status == RenderQueue.State.Cancelled && !File.Exists(j3.Output) && j1.Started <= j2.Started, "a cancelled job never renders; jobs run in order");
        RenderQueue.Paused = true;
        var j4 = RenderQueue.Add(ed, new ExportSettings { Height = 360 }, Path.Combine(outDir, "paused.mp4"));
        await Task.Delay(1500);
        bool waited = j4.Status == RenderQueue.State.Queued;
        RenderQueue.Paused = false;
        await Task.Run(() => RenderQueue.WaitIdle(120000));
        Check(waited && j4.Status == RenderQueue.State.Done && File.Exists(j4.Output), "a paused queue waits; resumed, it renders");
        RenderQueue.ClearFinished();
        Check(RenderQueue.Jobs.Count == 0, "Clear Finished empties it");
    }

    /// <summary>The preview's picture at t (the Program monitor's renderer, synchronous for pictures) — like --objtest.</summary>
    internal static byte[] PreviewFrame(Editor ed, double t)
    {
        int W = Editor.W, H = Editor.H;
        var pv = new Preview { Ed = ed, Width = W, Height = H, Background = System.Windows.Media.Brushes.Black };
        pv.Sync(t, false);
        pv.Measure(new System.Windows.Size(W, H)); pv.Arrange(new System.Windows.Rect(0, 0, W, H)); pv.UpdateLayout();
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(W, H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); rtb.Render(pv);
        var a = new byte[W * H * 4]; rtb.CopyPixels(a, W * 4, 0);
        pv.DisposeAll();
        return a;
    }

    // ---------------- secondary colour: HSL qualifier, power windows ----------------
    static async Task SecondaryTest(string dir)
    {
        Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
        int W = Editor.W;
        byte[] Px(Grade.Lut? l, byte r, byte g, byte b) { var p = new byte[] { b, g, r, 255 }; if (l != null) Grade.Apply(l, p, 1, 1, 4); return p; }
        // the qualifier on pure colours (baked into the LUT)
        var key = new HslKey { Hue = 60, HueWidth = 15, SatMin = 20, HueShift = 180 };
        var lut = Grade.For(new ColorGrade { Hsl = key });
        var yel = Px(lut, 255, 255, 0); var red = Px(lut, 255, 0, 0); var grey = Px(lut, 128, 128, 128);
        Check(yel[0] > yel[2] + 30 && yel[0] > 250 && red[2] == 255 && red[0] <= 1 && Math.Abs(grey[1] - 128) <= 1,
              $"HSL secondary in the LUT: yellow turned light blue (luma kept: {yel[2]},{yel[1]},{yel[0]}), red and grey untouched ({red[2]},{red[1]},{red[0]} / {grey[1]})");
        key.ShowMask = true;
        var ml = Grade.For(new ColorGrade { Hsl = key });
        var my = Px(ml, 255, 255, 0); var mr = Px(ml, 255, 0, 0);
        key.ShowMask = false; key.Invert = true;
        var il = Grade.For(new ColorGrade { Hsl = key });
        var iy = Px(il, 255, 255, 0); var ir = Px(il, 255, 0, 0);
        Check(my[1] > 250 && mr[1] < 5 && iy[1] > 250 && iy[0] < 5 && ir[0] > ir[2] + 60, "Show mask: white where selected, black elsewhere; Invert corrects everything else");
        key.Invert = false;
        Check(Math.Abs(Grade.Matte(new HslKey { Hue = 350, HueWidth = 20 }, 1, 0, 0.1) - 1) < 1e-9, "the hue range wraps around red (350° ± 20° takes in 6°)");

        // preview = export: the qualifier, a power window, an inverted one (a picture: the preview renders at once)
        var pattern = Path.Combine(dir, "pattern.png");
        FxTest.Save(FxTest.Pattern(), pattern);
        var pm = await MediaProbe.ProbeAsync(pattern) ?? throw new Exception("pattern import failed");
        var ed = new Editor(); ed.Media.Add(pm);
        var shape = new List<float[]> { new float[] { 330f / W, 430f / 720, 470f / W, 420f / 720, 500f / W, 560f / 720, 400f / W, 620f / 720, 320f / W, 560f / 720 } };
        Clip Pic(int i) { var c = new Clip { MediaId = pm.Id, Kind = ClipKind.Image, Track = Tracks.V1, Start = i, Dur = 1 }; ed.Clips.Add(c); return c; }
        Pic(0).Grade = new ColorGrade { Contrast = 10, Hsl = new HslKey { Hue = 60, HueWidth = 15, SatMin = 20, HueShift = 180, Exposure = -0.3 } };
        foreach (var (i, inv, g) in new[] { (1, false, new ColorGrade { Exposure = 1.2, Temperature = -30 }), (2, true, new ColorGrade { Saturation = 0 }) })
        {
            var c = Pic(i);
            var mk = new ObjectMask { Kind = MaskKind.Freehand, Effect = MaskFx.Grade, Invert = inv, Feather = 8, Grade = g };
            c.Masks.Add(mk); ed.MaskTracks[mk.Id] = new MaskTrack { Frames = { [0] = shape } };
        }
        var files = MaskExport.Prepare(ed, Path.Combine(dir, "masks_secondary"));
        var mp4 = Path.Combine(dir, "secondary.mp4");
        var err = await Exporter.RunAsync(ff, ed, mp4, new Progress<double>(), CancellationToken.None, null, files);
        Check(err == null, "export of the secondary corrections" + (err == null ? "" : ": " + err));
        if (err != null) return;
        string[] names = { "HSL secondary (yellow → blue)", "power window (brighter, cooler inside)", "inverted power window (grey outside)" };
        for (int i = 0; i < 3; i++)
        {
            var a = PreviewFrame(ed, i + 0.5);
            var b = FxTest.Frame(ff, mp4, i + 0.5);
            var (psnr, worst) = FxTest.Compare(a, b);
            int P(byte[] f, int x, int y, int ch) => f[(y * W + x) * 4 + ch];
            bool effect = i switch
            {
                0 => P(b, 640, 520, 0) > P(b, 640, 520, 2) + 30 && P(b, 160, 520, 2) > 200,                              // yellow circle blue, red circle red
                1 => P(b, 400, 520, 1) > 245 && P(b, 880, 520, 1) < 40,                                                  // the lime circle brighter; the magenta one as it was
                _ => Math.Abs(P(b, 160, 520, 2) - P(b, 160, 520, 0)) < 12 && P(b, 400, 520, 1) > 200 && P(b, 400, 520, 2) < 60,   // red grey outside, lime kept inside
            };
            Check(effect && psnr >= 30 && worst <= 24, $"preview = export, {names[i]}: PSNR {psnr:0.0} dB, worst 16×16 block {worst:0.0}/255{(effect ? "" : " — the effect is missing")}");
            FxTest.Save(FxTest.SideBySide(a, b), Path.Combine(dir, $"secondary_{i}.png"));
        }
    }

    static List<float[]> Ellipse(double cx, double cy, double rx, double ry)
    {
        var p = new float[48];
        for (int i = 0; i < 24; i++) { double a = i * Math.PI * 2 / 24; p[i * 2] = (float)(cx + rx * Math.Cos(a)); p[i * 2 + 1] = (float)(cy + ry * Math.Sin(a)); }
        return new List<float[]> { p };
    }
    /// <summary>The centre of the bright pixels (value &gt; 200 on all channels) of a BGRA frame, and how many.</summary>
    static (double x, double y, int n) Bright(byte[] f, int w)
    {
        double sx = 0, sy = 0; int n = 0;
        for (int i = 0; i + 3 < f.Length; i += 4)
            if (f[i] > 200 && f[i + 1] > 200 && f[i + 2] > 200) { int k = i / 4; sx += k % w; sy += k / w; n++; }
        return n == 0 ? (-1, -1, 0) : (sx / n, sy / n, n);
    }

    // ---------------- attach to a tracked object ----------------
    static async Task FollowTest(string dir)
    {
        Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
        int W = Editor.W, H = Editor.H;
        Generators.DirOverride = Path.Combine(dir, "generated");
        var black = await Generators.MakeAsync(new Generator { Kind = GenKind.ColorMatte, Color1 = "#000000", W = 1280, H = 720 });
        var white = await Generators.MakeAsync(new Generator { Kind = GenKind.ColorMatte, Color1 = "#FFFFFF", W = 64, H = 64 });
        Generators.DirOverride = null;
        var ed = new Editor(); ed.Media.Add(black!); ed.Media.Add(white!);
        var bg = new Clip { Kind = ClipKind.Image, MediaId = black!.Id, Track = Tracks.V1, Start = 0, Dur = 2 };
        var mk = new ObjectMask { Kind = MaskKind.Ellipse, Name = "Ball", Effect = MaskFx.Tint, Amount = 0 };
        bg.Masks.Add(mk); ed.Clips.Add(bg);
        var tr = new MaskTrack();
        tr.Set(0, Ellipse(0.25, 0.4, 0.04, 0.07), key: true);
        tr.Set(59, Ellipse(0.75, 0.6, 0.08, 0.14), key: true);                 // (moves right and down, twice as big)
        ed.MaskTracks[mk.Id] = tr;
        var f = new Clip { Kind = ClipKind.Image, MediaId = white!.Id, Track = Tracks.V(2), Start = 0, Dur = 2, Scale = 8 };
        ed.Clips.Add(f);
        var o0 = ed.ObjectAt(mk.Id, 0); var o1 = ed.ObjectAt(mk.Id, 1);
        bool ok = ed.AttachToObject(f, mk.Id, false, 0);
        f = ed.Clips.First(x => x.Id == f.Id);
        int keys = Anim.Track(f, AnimProp.X)?.Keys.Count ?? 0;
        double worst = 0;
        for (int k = 0; k < 60; k++)
        {
            double t = k / 30.0;
            var o = ed.ObjectAt(mk.Id, t)!.Value;
            double ex = (o.x + 320 - W / 2.0) / W * 100;                       // (the square began at the centre: 320 px right of the object)
            worst = Math.Max(worst, Math.Abs(Anim.Value(f, AnimProp.X, t) - ex));
        }
        Check(ok && o0 is { } a0 && Math.Abs(a0.x - 320) < 0.5 && Math.Abs(a0.y - 288) < 0.5 && o1 is { } a1 && Math.Abs(a1.x - 640) < 15 && f.Follow?.MaskId == mk.Id && keys >= 2 && keys < 60 && worst < 0.03,
              $"attach: the object's centre from its outline ({o0?.x:0.0}, {o0?.y:0.0}) px → ({o1?.x:0.0}, {o1?.y:0.0}) px at 1 s; Position follows it within {worst:0.000} % with {keys} keys (straight runs merged); one undo step “{ed.UndoLabels.First()}”");
        // preview = export: the white square sits where the keys say, in both
        var mp4 = Path.Combine(dir, "follow.mp4");
        var err = await Export(ed, dir, mp4, new ExportSettings());
        double worstPx = 0, worstPe = 0;
        foreach (double t in new[] { 8 / 30.0, 1.0, 52 / 30.0 })                   // (frame times: the export shows whole frames)
        {
            var a = PreviewFrame(ed, t); var b = FxTest.Frame(ff, mp4, t - 0.4 / 30);   // (output seeking keeps the first frame at or after t)
            var pa = Bright(a, W); var pb = Bright(b, W);
            double ex = W / 2.0 + Anim.Value(f, AnimProp.X, t) * W / 100, ey = H / 2.0 + Anim.Value(f, AnimProp.Y, t) * H / 100;
            worstPx = Math.Max(worstPx, Math.Max(Math.Abs(pa.x - ex), Math.Abs(pa.y - ey)));
            worstPe = Math.Max(worstPe, Math.Max(Math.Abs(pa.x - pb.x), Math.Abs(pa.y - pb.y)));
        }
        Check(err == null && worstPx < 1.5 && worstPe < 1.0, $"the square follows the object in the preview (within {worstPx:0.00} px of the keys) and the export = the preview (within {worstPe:0.00} px)" + (err != null ? " — " + err : ""));
        // size too
        ed.DetachFromObject(f); f = ed.Clips.First(x => x.Id == f.Id);
        Check(f.Follow == null && !Anim.HasKeys(f, AnimProp.X), "Detach removes the keys it made");
        ed.AttachToObject(f, mk.Id, true, 0); f = ed.Clips.First(x => x.Id == f.Id);
        double s0 = Anim.Value(f, AnimProp.Scale, 0), s1 = Anim.Value(f, AnimProp.Scale, 59 / 30.0);
        Check(Math.Abs(s0 - 8) < 0.01 && Math.Abs(s1 / s0 - 2) < 0.03, $"position and size: Scale {s0:0.00} → {s1:0.00} % as the object doubles");
        // re-tracked: Update follows the new outline
        tr.Set(59, Ellipse(0.75, 0.3, 0.08, 0.14), key: true);
        ed.UpdateFollow(f); f = ed.Clips.First(x => x.Id == f.Id);
        var oe = ed.ObjectAt(mk.Id, 59 / 30.0)!.Value;
        Check(Math.Abs(H / 2.0 + Anim.Value(f, AnimProp.Y, 59 / 30.0) * H / 100 - (oe.y + (H / 2.0 - 288))) < 0.6, "Update after the object's track changed follows the new path");
    }

    // ---------------- noise reduction, frame interpolation (rendered with the stabilizer's machinery) ----------------
    static async Task RenderedFxTest(string dir)
    {
        Editor.SetSequence(640, 360, 30, ReframeMode.Fit);
        Stab.Background = false;
        try
        {
            var cleanF = Gen(dir, "clean.mp4", "-f lavfi -i testsrc2=s=640x360:r=30:d=2 -c:v libx264 -crf 12 -pix_fmt yuv420p");
            var noisyF = Gen(dir, "noisy20.mp4", "-f lavfi -i testsrc2=s=640x360:r=30:d=2,noise=alls=20:allf=t -c:v libx264 -crf 12 -pix_fmt yuv420p");
            var mN = await Imp(noisyF);
            byte[] Fr(string f, int k) => Frame(f, 0, 640, 360, $"select=eq(n\\,{k})");
            var clean = Fr(cleanF, 30);
            double plain = Psnr(clean, Fr(noisyF, 30));
            foreach (int method in new[] { 0, 1, 2 })
            {
                var ed = new Editor(); ed.Media.Add(mN);
                var c = new Clip { Kind = ClipKind.Video, MediaId = mN.Id, Track = Tracks.V1, Start = 0, Dur = 2, NoAudio = true, Denoise = new VideoDenoise { Method = method, Strength = 70 } };
                ed.Clips.Add(c);
                var files = Stab.PrepareExport(ed, CancellationToken.None);
                var f = files.GetValueOrDefault(c.Id);
                var (s, e) = Transitions.Compute(ed).Range(c);
                double got = f != null ? Psnr(clean, Fr(f, 30)) : 0;
                var mp4 = Path.Combine(dir, $"denoise_{method}.mp4");
                var err = await Export(ed, dir, mp4, new ExportSettings { Quality = 10 });
                double exported = err == null ? Psnr(clean, Frame(mp4, 1 - 0.4 / 30, 640, 360)) : 0;
                Check(f != null && got > plain + 1 && exported > plain + 1 && Stab.Ready(c, mN, s, e) == f,
                      $"noise reduction ({new[] { "fast", "high quality", "temporal" }[method]}): {plain:0.0} → {got:0.0} dB from the clean source (export {exported:0.0} dB); the preview plays the same rendered file" + (err != null ? " — " + err : ""));
            }
            // slow motion: a bar moving 4 px per native frame, the clip at 50 %
            var barF = Gen(dir, "barmove.mp4", "-f lavfi -i \"color=c=black:s=320x180:r=30:d=3[bg];color=c=white:s=12x60:r=30:d=3[b];[bg][b]overlay=x='20+n*4':y=60\" -c:v libx264 -crf 8 -pix_fmt yuv420p");
            var mB = await Imp(barF);
            Editor.SetSequence(320, 180, 30, ReframeMode.Fit);
            (double x, int bright, int mid) Bar(byte[] f)
            {
                double sx = 0; int nb = 0, nm = 0;
                for (int y = 70; y < 110; y++) for (int x = 0; x < 320; x++) { int v = f[(y * 320 + x) * 4 + 1]; if (v > 200) { sx += x; nb++; } else if (v > 70 && v < 185) nm++; }
                return (nb > 0 ? sx / nb : -1, nb, nm);
            }
            var res = new Dictionary<FrameInterp, (double x, int bright, int mid)>();
            foreach (var ip in new[] { FrameInterp.Sample, FrameInterp.Blend, FrameInterp.OpticalFlow })
            {
                var ed = new Editor(); ed.Media.Add(mB);
                var c = new Clip { Kind = ClipKind.Video, MediaId = mB.Id, Track = Tracks.V1, Start = 0, Dur = 2, In = 0.5, Speed = 50, NoAudio = true, Interp = ip };
                ed.Clips.Add(c);
                string? f;
                if (ip == FrameInterp.Sample) f = Remap.PrepareExport(ed, CancellationToken.None).GetValueOrDefault(c.Id);
                else f = Stab.PrepareExport(ed, CancellationToken.None).GetValueOrDefault(c.Id);
                res[ip] = f != null ? Bar(Frame(f, 0, 320, 180, "select=eq(n\\,21)")) : (-1, 0, 0);   // (output frame 21: source 0.5 + 21 / 60 s = native frame 25.5)
            }
            // the media's own frames 25 and 26 (the bar moves 4 px a frame): sampling shows 25, blending and optical flow land halfway
            double x25 = Bar(Frame(barF, 0, 320, 180, "select=eq(n\\,25)")).x, x26 = Bar(Frame(barF, 0, 320, 180, "select=eq(n\\,26)")).x, half = (x25 + x26) / 2;
            Check(Math.Abs(res[FrameInterp.Sample].x - x25) < 1 && x26 > x25 + 3 && res[FrameInterp.Sample].mid < 20, $"frame sampling repeats a native frame (bar at {res[FrameInterp.Sample].x:0.0} px, {res[FrameInterp.Sample].mid} grey pixels)");
            Check(res[FrameInterp.Blend].mid > 100 && Math.Abs(res[FrameInterp.Blend].x - half) < 1, $"frame blending mixes frames 25 and 26 ({res[FrameInterp.Blend].mid} half-bright pixels where the bars don't overlap, centre {res[FrameInterp.Blend].x:0.0} px)");
            Check(Math.Abs(res[FrameInterp.OpticalFlow].x - half) < 1 && res[FrameInterp.OpticalFlow].bright > 300, $"optical flow makes the in-between frame: the bar halfway at {res[FrameInterp.OpticalFlow].x:0.0} px ({res[FrameInterp.OpticalFlow].bright} bright pixels)");
        }
        finally { Stab.Background = true; Editor.SetSequence(1280, 720, 30, ReframeMode.Fit); }
        // an upscaled copy: twice the pixels, the same pictures and sound
        Upscale.DirOverride = Path.Combine(dir, "upscaled"); if (Directory.Exists(Upscale.DirOverride)) Directory.Delete(Upscale.DirOverride, true);
        try
        {
            var small = await Imp(Gen(dir, "small.mp4", "-f lavfi -i testsrc2=s=320x180:r=30:d=2 -f lavfi -i sine=frequency=440:duration=2 -c:v libx264 -crf 12 -pix_fmt yuv420p -c:a aac -shortest"));
            var up = await MediaProbe.ProbeAsync(await Upscale.MakeAsync(small, 2));
            var a = Frame(small.Path, 1, 320, 180); var b = up != null ? Frame(up.Path, 1, 320, 180) : Array.Empty<byte>();
            double back = Psnr(a, b);
            Check(up != null && up.Width == 640 && up.Height == 360 && up.HasAudio && Math.Abs(up.Duration - small.Duration) < 0.05 && back > 25 && up.Path.Contains("(2x)"),
                  $"Create Upscaled Copy 2×: {up?.Width}×{up?.Height}, sound kept, {up?.Duration:0.00} s; scaled back it matches the original at {back:0.0} dB");
            var pic = await Imp(Path.Combine(dir, "pattern.png"));
            var up4 = await MediaProbe.ProbeAsync(await Upscale.MakeAsync(pic, 4));
            Check(up4 != null && up4.Type == ClipKind.Image && up4.Width == pic.Width * 4, $"a picture 4×: {up4?.Width}×{up4?.Height} PNG");
        }
        finally { Upscale.DirOverride = null; }
    }

    // ---------------- channels: per-clip mapping, mono and 5.1 exports ----------------
    static async Task ChannelsTest(string dir)
    {
        var toneF = Gen(dir, "lr.wav", "-f lavfi -i \"aevalsrc=0.3*sin(2*PI*440*t)|0.2*sin(2*PI*660*t):s=48000:d=2\" -c:a pcm_s16le");
        var bassF = Gen(dir, "bass.wav", "-f lavfi -i \"aevalsrc=0.25*sin(2*PI*110*t)|0.25*sin(2*PI*110*t):s=48000:d=2\" -c:a pcm_s16le");
        var mT = await Imp(toneF); var mB = await Imp(bassF);
        float[] Render(Editor ed, Func<AudioOut.Src, bool>? only = null)
        {
            var srcs = Mixer.Sources(ed, Transitions.Compute(ed), new()).Where(s => only == null || only(s)).ToArray();
            foreach (var p in srcs.Select(s => s.Path).Distinct()) AudioCache.WaitReady(p, 30000);
            var all = new List<float>();
            Mixer.Render(srcs, Mixer.MasterOf(ed), 0, 2, (b, n) => { for (int i = 0; i < n * 2; i++) all.Add(b[i] / 32768f); });
            return all.ToArray();
        }
        double MaxDiff(float[] a, float[] b, int n) { double m = 0; for (int i = 0; i < n; i++) m = Math.Max(m, Math.Abs(a[i] - b[i])); return m * 32768; }
        // 1. the left channel on both sides
        var ed = new Editor(); ed.Media.Add(mT); ed.Media.Add(mB);
        var c = new Clip { Kind = ClipKind.Audio, MediaId = mT.Id, Track = Tracks.A1, Start = 0, Dur = 2, Channels = ChannelMap.Left };
        ed.Clips.Add(c);
        var mix = Render(ed);
        var src = Sound(toneF);
        double lr = 0, ll = 0; int n = Math.Min(mix.Length, src.Length) / 2;
        for (int i = 0; i < n; i++) { lr = Math.Max(lr, Math.Abs(mix[i * 2] - mix[i * 2 + 1])); ll = Math.Max(ll, Math.Abs(mix[i * 2 + 1] - src[i * 2])); }
        var wav = Path.Combine(dir, "left.wav");
        var err = await Export(ed, dir, wav, new ExportSettings { Video = "none", Container = "wav", Audio = "pcm" });
        var ex = err == null ? Sound(wav) : Array.Empty<float>();
        Check(err == null && lr * 32768 < 1.5 && ll * 32768 < 1.5 && MaxDiff(ex, mix, Math.Min(ex.Length, mix.Length)) <= 2,
              $"channels “left on both sides”: right = left ({lr * 32768:0.0} LSB), = the recorded left ({ll * 32768:0.0} LSB); export = mixer ({MaxDiff(ex, mix, Math.Min(ex.Length, mix.Length)):0.0} LSB)");
        foreach (var (map, name) in new[] { (ChannelMap.Mono, "mono"), (ChannelMap.Swap, "swapped") })
        {
            c.Channels = map;
            var mm = Render(ed); double worst = 0;
            for (int i = 0; i < Math.Min(mm.Length, src.Length) / 2; i++)
            {
                double eL = map == ChannelMap.Mono ? (src[i * 2] + src[i * 2 + 1]) / 2 : src[i * 2 + 1], eR = map == ChannelMap.Mono ? eL : src[i * 2];
                worst = Math.Max(worst, Math.Max(Math.Abs(mm[i * 2] - eL), Math.Abs(mm[i * 2 + 1] - eR)));
            }
            Check(worst * 32768 < 1.5, $"channels “{name}”: within {worst * 32768:0.0} LSB of the recording");
        }
        // 2. a mono export folds the stereo mix: (L + R) / 2
        c.Channels = ChannelMap.Stereo;
        var st = Path.Combine(dir, "stereo.wav"); var mo = Path.Combine(dir, "mono.wav");
        var e1 = await Export(ed, dir, st, new ExportSettings { Video = "none", Container = "wav", Audio = "pcm" });
        var e2 = await Export(ed, dir, mo, new ExportSettings { Video = "none", Container = "wav", Audio = "pcm", Channels = "mono" });
        var sS = Sound(st); var sM = Sound(mo, 1);
        double fold = 0; for (int i = 0; i < Math.Min(sM.Length, sS.Length / 2); i++) fold = Math.Max(fold, Math.Abs(sM[i] - (sS[i * 2] + sS[i * 2 + 1]) / 2));
        Check(e1 == null && e2 == null && sM.Length * 2 >= sS.Length - 4 && fold * 32768 <= 1.5, $"mono export = (L + R) / 2 of the stereo export within {fold * 32768:0.0} LSB");
        // 3. 5.1: A1 front, A2 centre, A3 surround (a third audio track), LFE empty
        ed.AddTracks(0, 0, 1, Tracks.Count);                                      // (A3 after the last audio track)
        var cB = new Clip { Kind = ClipKind.Audio, MediaId = mB.Id, Track = Tracks.A(2), Start = 0, Dur = 2 };
        var cS = new Clip { Kind = ClipKind.Audio, MediaId = mT.Id, Track = Tracks.A(3), Start = 0, Dur = 2, Channels = ChannelMap.Swap };
        ed.Clips.Add(cB); ed.Clips.Add(cS);
        var cA = ed.Clips.First(x => x.MediaId == mT.Id && x.Track == Tracks.A1);
        ed.Mix.Channels[Tracks.A(2)].Surround = SurroundOut.Centre; ed.Mix.Channels[Tracks.A(3)].Surround = SurroundOut.Surround;
        var f51 = Path.Combine(dir, "surround.wav");
        var e3 = await Export(ed, dir, f51, new ExportSettings { Video = "none", Container = "wav", Audio = "pcm", Channels = "5.1" });
        var s6 = e3 == null ? Sound(f51, 6) : Array.Empty<float>();
        var front = Render(ed, s => s.Track == Tracks.A1); var centre = Render(ed, s => s.Track == Tracks.A(2)); var rear = Render(ed, s => s.Track == Tracks.A(3));
        double dF = 0, dC = 0, dS = 0, lfe = 0; int frames = Math.Min(s6.Length / 6, front.Length / 2);
        for (int i = 0; i < frames; i++)
        {
            dF = Math.Max(dF, Math.Max(Math.Abs(s6[i * 6] - front[i * 2]), Math.Abs(s6[i * 6 + 1] - front[i * 2 + 1])));
            dC = Math.Max(dC, Math.Abs(s6[i * 6 + 2] - (centre[i * 2] + centre[i * 2 + 1]) / 2));
            lfe = Math.Max(lfe, Math.Abs(s6[i * 6 + 3]));
            dS = Math.Max(dS, Math.Max(Math.Abs(s6[i * 6 + 4] - rear[i * 2]), Math.Abs(s6[i * 6 + 5] - rear[i * 2 + 1])));
        }
        Check(e3 == null && frames > 90000 && dF * 32768 <= 2 && dC * 32768 <= 2 && dS * 32768 <= 2 && lfe * 32768 < 1,
              $"5.1 export: front = A1's mix ({dF * 32768:0.0} LSB), centre = A2 in mono ({dC * 32768:0.0}), surrounds = A3 ({dS * 32768:0.0}), LFE silent — {frames} frames" + (e3 != null ? " — " + e3 : ""));

        // 4. clean-up: a harmonic "voice" (0.5 s on, 0.5 s off) over white noise
        var vF = Gen(dir, "voice_noise.wav", "-f lavfi -i \"aevalsrc=if(lt(mod(t\\,1)\\,0.5)\\,0.1*(sin(2*PI*180*t)+0.6*sin(2*PI*360*t)+0.4*sin(2*PI*540*t)+0.3*sin(2*PI*900*t))\\,0):s=48000:d=4\" " +
                                              "-f lavfi -i \"anoisesrc=c=white:a=0.02:d=4:r=48000\" -filter_complex \"[0][1]amix=inputs=2:normalize=0,pan=stereo|c0=c0|c1=c0\" -c:a pcm_s16le");
        var mV = await Imp(vF);
        double Rms(float[] s, double a, double b) { int i0 = (int)(a * 48000), i1 = (int)(b * 48000); double sum = 0; for (int i = i0; i < i1; i++) sum += s[i * 2] * s[i * 2]; return Math.Sqrt(sum / Math.Max(1, i1 - i0)); }
        double Gaps(float[] s) => (Rms(s, 1.6, 1.9) + Rms(s, 2.6, 2.9) + Rms(s, 3.6, 3.9)) / 3;
        double Bursts(float[] s) => (Rms(s, 1.1, 1.4) + Rms(s, 2.1, 2.4) + Rms(s, 3.1, 3.4)) / 3;
        int Lag(float[] a, float[] b)
        {   // the shift of b against a: where the bursts start and stop (envelopes over 2 ms), the voice's own phase left out
            double[] Env(float[] s) { var e = new double[s.Length / 2]; double acc = 0; for (int i = 0; i < e.Length; i++) { acc += Math.Abs(s[i * 2]) - (i >= 96 ? Math.Abs(s[(i - 96) * 2]) : 0); e[i] = acc; } return e; }
            var ea = Env(a); var eb = Env(b);
            int best = 0; double bestV = double.MinValue;
            for (int l = -480; l <= 480; l++)
            {
                double v = 0;
                foreach (double edge in new[] { 1.0, 1.5, 2.0, 2.5, 3.0, 3.5 })
                    for (int i = (int)(edge * 48000) - 1200; i < (int)(edge * 48000) + 1200; i++) v += ea[i] * eb[i + l];
                if (v > bestV) { bestV = v; best = l; }
            }
            return best;
        }
        var orig = Sound(vF);
        foreach (var mode in new[] { VoiceClean.ReduceNoise, VoiceClean.IsolateVoice })
        {
            var ev = new Editor(); ev.Media.Add(mV);
            ev.Clips.Add(new Clip { Kind = ClipKind.Audio, MediaId = mV.Id, Track = Tracks.A1, Start = 0, Dur = 4, Voice = mode, VoiceAmount = 70 });
            var srcs = Mixer.Sources(ev, Transitions.Compute(ev), new()).ToArray();
            foreach (var p in srcs.Select(s => s.Path).Distinct()) AudioCache.WaitReady(p, 60000);
            var outS = new List<float>();
            Mixer.Render(srcs, Mixer.MasterOf(ev), 0, 4, (b, n) => { for (int i = 0; i < n * 2; i++) outS.Add(b[i] / 32768f); });
            var cleaned = outS.ToArray();
            double noiseDb = 20 * Math.Log10(Gaps(cleaned) / Gaps(orig)), voiceDb = 20 * Math.Log10(Bursts(cleaned) / Bursts(orig));
            int lag = Lag(orig, cleaned);
            var wf = Path.Combine(dir, $"clean_{mode}.wav");
            var er = await Export(ev, dir, wf, new ExportSettings { Video = "none", Container = "wav", Audio = "pcm" });
            var exS = er == null ? Sound(wf) : Array.Empty<float>();
            Check(noiseDb < -10 && voiceDb > -3 && Math.Abs(lag) <= 12 && er == null && MaxDiff(exS, cleaned, Math.Min(exS.Length, cleaned.Length)) <= 2,
                  $"clean-up “{mode}”: noise between words {noiseDb:0.0} dB, the voice {voiceDb:+0.0;−0.0;0.0} dB, no shift ({lag} samples: the band filters' phase); export = mixer ({MaxDiff(exS, cleaned, Math.Min(exS.Length, cleaned.Length)):0.0} LSB)");
        }
    }

    // ---------------- distort: twirl, wave, spherize, mesh warp — the preview's shader vs the export's geq ----------------
    static async Task DistortTest(string dir)
    {
        Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
        var pattern = Path.Combine(dir, "pattern.png");
        FxTest.Save(FxTest.Pattern(), pattern);
        var pm = await MediaProbe.ProbeAsync(pattern) ?? throw new Exception("pattern import failed");
        var ed = new Editor(); ed.Media.Add(pm);
        var mesh = new double[32];
        mesh[(1 * 4 + 1) * 2] = 6; mesh[(1 * 4 + 1) * 2 + 1] = 4; mesh[(2 * 4 + 2) * 2] = -5; mesh[(2 * 4 + 2) * 2 + 1] = 8; mesh[(0 * 4 + 3) * 2 + 1] = 5;
        var cases = new (string name, Distort d, double at)[]
        {
            ("twirl 180° (radius 50 %)", new Distort { Kind = DistortKind.Twirl, Amount = 180, Radius = 50 }, 0.5),
            ("twirl −90° off-centre", new Distort { Kind = DistortKind.Twirl, Amount = -90, Radius = 35, CenterX = -20, CenterY = 10 }, 0.5),
            ("spherize bulge 60", new Distort { Kind = DistortKind.Spherize, Amount = 60, Radius = 60 }, 0.5),
            ("spherize pinch −50", new Distort { Kind = DistortKind.Spherize, Amount = -50, Radius = 70, CenterX = 10 }, 0.5),
            ("wave (2 %, 30°) at 0.5 s", new Distort { Kind = DistortKind.Wave, Amount = 2, Wavelength = 20, Direction = 30, Speed = 1.3 }, 0.5),
            ("wave at 0.9 s (moved on)", new Distort { Kind = DistortKind.Wave, Amount = 2, Wavelength = 20, Direction = 30, Speed = 1.3 }, 0.9),
            ("mesh warp (3 points moved)", new Distort { Kind = DistortKind.Mesh, Mesh = mesh }, 0.5),
        };
        for (int i = 0; i < cases.Length; i++)
            ed.Clips.Add(new Clip { MediaId = pm.Id, Kind = ClipKind.Image, Track = Tracks.V1, Start = i, Dur = 1, Distort = cases[i].d });
        var mp4 = Path.Combine(dir, "distort.mp4");
        var err = await Export(ed, dir, mp4, new ExportSettings { Quality = 12 });
        Check(err == null, "export of every distortion (geq)" + (err == null ? "" : ": " + err));
        if (err != null) return;
        var plain = PreviewFrameOf(ed, pm);
        // 1. the shader against geq on the still, in RGB (no encoder): the warp arithmetic itself
        var still = FxTest.Pattern();
        var srcPx = new byte[1280 * 720 * 4]; still.CopyPixels(srcPx, 1280 * 4, 0);
        for (int i = 0; i < cases.Length; i++)
        {
            if (i == 5) continue;                                                  // (the second wave time: the export check below)
            var d = cases[i].d;
            var raw = Path.Combine(dir, $"geq_{i}.raw");
            Exec($"-y -v error -i \"{pattern}\" -vf \"format=gbrap,{Distorts.Geq(d, 1280, 720).Replace("\"", "\\\"")},format=rgba\" -frames:v 1 -f rawvideo \"{raw}\"");
            var g = File.Exists(raw) ? File.ReadAllBytes(raw) : Array.Empty<byte>();
            var img = new System.Windows.Controls.Image { Source = still, Width = 1280, Height = 720, Stretch = System.Windows.Media.Stretch.Fill };
            var fx = new DistortEffect(d.Kind, Distorts.Shader(d.Kind)!); fx.Set(d, 1280, 720, Pin.Half, 0); img.Effect = fx;
            img.Measure(new System.Windows.Size(1280, 720)); img.Arrange(new System.Windows.Rect(0, 0, 1280, 720)); img.UpdateLayout();
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(1280, 720, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); rtb.Render(img);
            var s = new byte[1280 * 720 * 4]; rtb.CopyPixels(s, 1280 * 4, 0);
            long sum = 0; int mx = 0, off3 = 0, n = 0;
            if (g.Length == s.Length)
                for (int k = 0; k < s.Length; k += 4)
                {
                    int ga = g[k + 3];
                    for (int ch = 0; ch < 3; ch++)
                    {   // geq: straight RGBA; WPF: premultiplied BGRA
                        int gv = g[k + 2 - ch] * ga / 255, dv = Math.Abs(s[k + ch] - gv);
                        sum += dv; mx = Math.Max(mx, dv); if (dv >= 3) off3++; n++;
                    }
                }
            double mean = n > 0 ? sum / (double)n : 99;
            Check(g.Length == s.Length && mean < 0.6 && off3 < n * 0.02, $"shader = geq, {cases[i].name} (software renderer, RGB): mean off {mean:0.000}, max {mx}, {(n > 0 ? off3 / (double)n : 1):P2} off by 3+");
        }
        // 2. preview vs export through the whole pipeline (H.264 4:2:0 output: thin coloured lines lose some colour)
        for (int i = 0; i < cases.Length; i++)
        {
            double t = i + Math.Round(cases[i].at * 30) / 30;
            var a = PreviewFrame(ed, t); var b = FxTest.Frame(ff, mp4, t - 0.4 / 30);
            var (psnr, worst) = FxTest.Compare(a, b);
            double moved = Psnr(a, plain);
            Check(psnr >= 28 && worst <= 24 && moved < 25, $"preview = export, {cases[i].name}: PSNR {psnr:0.0} dB, worst 16×16 block {worst:0.0}/255 (it does distort: {moved:0.0} dB from the plain picture)");
            FxTest.Save(FxTest.SideBySide(a, b), Path.Combine(dir, $"distort_{i}.png"));
        }
        // the wave travels: two times differ
        var w1 = PreviewFrame(ed, 4.5); var w2 = PreviewFrame(ed, 5.9);
        Check(Psnr(w1, w2) < 30, $"the wave moves on over time ({Psnr(w1, w2):0.0} dB between 0.5 s and 0.9 s)");
    }
    /// <summary>The picture with no effect (a reference for "it changed").</summary>
    static byte[] PreviewFrameOf(Editor ed0, MediaItem pm)
    {
        var e = new Editor(); e.Media.Add(pm);
        e.Clips.Add(new Clip { MediaId = pm.Id, Kind = ClipKind.Image, Track = Tracks.V1, Start = 0, Dur = 1 });
        return PreviewFrame(e, 0.5);
    }

    // ---------------- proxies ----------------
    static async Task ProxyTest(string dir)
    {
        Proxies.DirOverride = Path.Combine(dir, "proxies"); if (Directory.Exists(Proxies.DirOverride)) Directory.Delete(Proxies.DirOverride, true);
        try
        {
            var bigF = Gen(dir, "bar720.mp4", "-f lavfi -i \"color=c=black:s=1280x720:r=30:d=3[bg];color=c=white:s=24x120:r=30:d=3[b];[bg][b]overlay=x='40+n*8':y=300\" -c:v libx264 -crf 10 -pix_fmt yuv420p -g 60");
            var m = await Imp(bigF);
            var px = await Proxies.MakeAsync(m, 540);
            var pm = await MediaProbe.ProbeAsync(px);
            double X(string f, int k, int w, int h)
            {
                var b = Frame(f, 0, w, h, $"select=eq(n\\,{k})"); double sx = 0; int n = 0;
                for (int i = 0; i + 3 < b.Length; i += 4) if (b[i + 1] > 128) { sx += (i / 4) % w; n++; }
                return n > 0 ? sx / n : -1;
            }
            double worst = 0;
            foreach (int k in new[] { 0, 1, 37, 89 }) worst = Math.Max(worst, Math.Abs(X(bigF, k, 1280, 720) * 0.75 - X(px, k, 960, 540)));
            Check(pm != null && pm.Width == 960 && pm.Height == 540 && !pm.HasAudio && Math.Abs(pm.Duration - m.Duration) < 0.05 && worst < 1.0,
                  $"a 540p proxy: {pm?.Width}×{pm?.Height}, picture only, the same frames at the same times (bar within {worst:0.00} px of the original's, scaled)");
            m.Proxy = px; m.RefreshOnline();
            Proxies.Use = false; string off = Proxies.PathOf(m);
            Proxies.Use = true; string on = Proxies.PathOf(m);
            Check(off == m.Path && on == px && m.Info.Contains("proxy"), "the toggle: the preview decodes the proxy only while proxies are on");
            // the export reads the original even with proxies on
            Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
            var ed = new Editor(); ed.Media.Add(m);
            ed.Clips.Add(new Clip { Kind = ClipKind.Video, MediaId = m.Id, Track = Tracks.V1, Start = 0, Dur = 2, NoAudio = true });
            var mp4 = Path.Combine(dir, "proxy_export.mp4");
            var err = await Export(ed, dir, mp4, new ExportSettings { Quality = 10 });
            double ex = err == null ? Psnr(Frame(mp4, 1 - 0.4 / 30, 1280, 720), Frame(bigF, 1 - 0.4 / 30, 1280, 720)) : 0;
            Check(err == null && ex > 40, $"with proxies on, the export still reads the original ({ex:0.0} dB from it)");
            var pf = JsonSerializer.Deserialize<ProjectFile>(JsonSerializer.Serialize(new ProjectFile { Media = new() { m } }))!;
            Check(pf.Media[0].Proxy == px, "the project file keeps the proxy");
        }
        finally { Proxies.Use = false; Proxies.DirOverride = null; }
    }

    // ---------------- audio time units: sample-accurate edits ----------------
    static async Task AudioUnitsTest(string dir)
    {
        var toneF = Gen(dir, "lr.wav", "-f lavfi -i \"aevalsrc=0.3*sin(2*PI*440*t)|0.2*sin(2*PI*660*t):s=48000:d=2\" -c:a pcm_s16le");
        var mT = await Imp(toneF);
        Editor.AudioUnits = true;
        try
        {
            var ed = new Editor(); ed.Media.Add(mT);
            var a = new Clip { Kind = ClipKind.Audio, MediaId = mT.Id, Track = Tracks.A1, Start = 0, Dur = 2 };
            ed.Clips.Add(a);
            double t = 1.0 + 7.0 / 48000;                                        // (between frames: 7 samples after 1 s)
            ed.Edit(() => ed.CutClips(new[] { a }, t));
            var right = ed.Clips.OrderBy(c => c.Start).Last();
            Check(Math.Abs(right.Start - t) < 1e-9 && Math.Abs(right.In - t) < 1e-9, $"audio time units: an audio-only clip cuts at a sample ({right.Start * 48000:0} = 48007)");
            ed.Undo();
            ed.Clips.Clear(); Pair(ed, mT, 0, 0, 2);                               // (a picture with its sound)
            ed.Edit(() => ed.CutClips(new[] { ed.Clips.First(x => x.Kind == ClipKind.Audio) }, t));
            var r2 = ed.Clips.Where(x => x.Kind == ClipKind.Audio).OrderBy(c => c.Start).Last();
            Check(Math.Abs(r2.Start - 1.0) < 1e-9, $"a clip linked to a picture still cuts on frames ({r2.Start:0.######} s)");
            // a clip placed at a sample: the export puts it on the same sample the mixer does
            var e2 = new Editor(); e2.Media.Add(mT);
            e2.Clips.Add(new Clip { Kind = ClipKind.Audio, MediaId = mT.Id, Track = Tracks.A1, Start = Editor.Sample(0.3 + 13.0 / 48000), In = Editor.Sample(0.25 + 5.0 / 48000), Dur = Editor.Sample(1.2 + 3.0 / 48000) });
            var srcs = Mixer.Sources(e2, Transitions.Compute(e2), new()).ToArray();
            foreach (var p in srcs.Select(s => s.Path).Distinct()) AudioCache.WaitReady(p, 30000);
            var mix = new List<float>(); Mixer.Render(srcs, Mixer.MasterOf(e2), 0, 2, (b, n) => { for (int i = 0; i < n * 2; i++) mix.Add(b[i] / 32768f); });
            var wav = Path.Combine(dir, "samples.wav");
            var err = await Export(e2, dir, wav, new ExportSettings { Video = "none", Container = "wav", Audio = "pcm" });
            var ex = err == null ? Sound(wav) : Array.Empty<float>();
            double worst = 0; int first = -1;
            for (int i = 0; i < Math.Min(ex.Length, mix.Count); i++) worst = Math.Max(worst, Math.Abs(ex[i] - mix[i]));
            for (int i = 0; i < ex.Length / 2; i++) if (Math.Abs(ex[i * 2]) > 1e-4) { first = i; break; }
            Check(err == null && worst * 32768 <= 1.5 && Math.Abs(first - (0.3 * 48000 + 13)) <= 1, $"a clip at sample {0.3 * 48000 + 13:0}: the export starts it there ({first}) and equals the mixer within {worst * 32768:0.0} LSB");
        }
        finally { Editor.AudioUnits = false; }
    }

    // ---------------- text animation ----------------
    static async Task TextAnimTest(string dir)
    {
        Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
        int W = Editor.W;
        var ed = new Editor();
        Clip T(string text, double start, double dur, TextAnim fx)
        {
            var c = new Clip { Kind = ClipKind.Text, Track = Tracks.V(2), Start = start, Dur = dur, Role = TextRole.Title, Text = text, Style = Themes.Get(ed.ThemeName).For(TextRole.Title).Clone(), TextFx = fx };
            c.Style!.Shadow = false; c.Style.Glow = false; c.Style.Stroke = false; c.Style.Fill = "#FFFFFF"; c.Style.Gradient = false;
            ed.Clips.Add(c); return c;
        }
        T("TYPEWRITER TEXT", 0, 3, new TextAnim { In = TextIn.Typewriter, InDur = 1.2, Out = TextIn.FadeWords, OutDur = 0.8 });
        T("POP BY WORD HERE", 3, 2, new TextAnim { In = TextIn.PopWords, InDur = 1 });
        T("Directed by\nA. Person\n\nMusic\nB. Person\n\nThanks for watching", 5, 3, new TextAnim { Roll = TextRoll.Roll });
        T("BREAKING: a crawl across the screen", 8, 2, new TextAnim { Roll = TextRoll.Crawl });
        var mp4 = Path.Combine(dir, "textanim.mp4");
        var err = await Export(ed, dir, mp4, new ExportSettings { Video = "h264", Quality = 12 });
        Check(err == null, "export of animated titles" + (err == null ? "" : ": " + err));
        if (err != null) return;
        var res = new List<string>(); double worstPsnr = 99; int worstCount = 0; bool ok = true;
        var counts = new Dictionary<int, (int pv, int ex, double y, double x)>();
        foreach (int k in new[] { 3, 12, 30, 60, 84, 95, 100, 112, 150, 165, 195, 230, 280 })
        {
            double t = k / 30.0;
            var a = PreviewFrame(ed, t); var b = FxTest.Frame(ff, mp4, t - 0.4 / 30);
            var (psnr, _) = FxTest.Compare(a, b);
            var pa = Bright(a, W); var pb = Bright(b, W);
            counts[k] = (pa.n, pb.n, pa.y, pa.x);
            int dn = Math.Abs(pa.n - pb.n);
            worstPsnr = Math.Min(worstPsnr, psnr); worstCount = Math.Max(worstCount, pa.n == 0 ? dn : (int)(100.0 * dn / Math.Max(1, pa.n)));
            if (psnr < 32 || (pa.n > 50 && Math.Abs(pa.y - pb.y) > 1.5)) { ok = false; res.Add($"{t:0.00}s {psnr:0.0}dB n{pa.n}/{pb.n} y{pa.y:0.0}/{pb.y:0.0}"); }
            if (k == 84 || k == 150) FxTest.Save(FxTest.SideBySide(a, b), Path.Combine(dir, $"textanim_{k}.png"));
        }
        Check(ok, $"preview = export for typewriter, fade by word, pop by word, roll and crawl at 13 frames: PSNR ≥ {worstPsnr:0.0} dB, lit pixels within {worstCount} %" + (res.Count > 0 ? " — " + string.Join("; ", res) : ""));
        Check(counts[3].pv < counts[12].pv && counts[12].pv < counts[60].pv && counts[84].pv < counts[60].pv,
              $"typewriter: more letters as it types ({counts[3].pv} → {counts[12].pv} → {counts[60].pv} lit pixels), fewer as the words fade out ({counts[84].pv})");
        Check(counts[95].pv < counts[112].pv, $"pop by word grows in ({counts[95].pv} → {counts[112].pv})");
        Check(counts[165].pv > 0 && counts[195].y < counts[165].y - 50 && counts[230].y < counts[195].y, $"roll: the credits move up ({counts[165].y:0} → {counts[195].y:0} → {counts[230].y:0} px)");
        var c245 = Bright(PreviewFrame(ed, 245 / 30.0), W);
        Check(counts[280].pv > 0 && c245.n > 0 && c245.x > counts[280].x + 100, $"crawl: the text moves left ({c245.x:0} → {counts[280].x:0} px)");
    }

    // ---------------- interchange import ----------------
    static void ImportTest(string dir, MediaItem m)
    {
        Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
        var ed = new Editor();
        ed.Media.Add(m);
        var (a, aa) = Pair(ed, m, 0, 1, 2);
        var (b, ba) = Pair(ed, m, 2, 4, 2);
        b.TransIn = new Transition { Kind = "Cross Dissolve", Dur = 0.5, Align = TransAlign.Center };
        var (c, ca) = Pair(ed, m, 5, 0, 1); c.Speed = ca.Speed = 200;
        ed.Clips.Add(new Clip { Kind = ClipKind.Text, Track = Tracks.V(2), Start = 1, Dur = 2, Role = TextRole.Title, Text = "HELLO", Style = Themes.Get(ed.ThemeName).For(TextRole.Title).Clone() });
        ed.Markers.Add(new Marker { T = 1.5, Name = "M1", Color = MarkerColor.Red });
        a.Markers = new() { new Marker { T = 1.5, Name = "on A" } };
        Dictionary<ImpClip, MediaItem?> Map(ImpTimeline t) => t.Clips.ToDictionary(x => x, x => x.Path != null && string.Equals(Path.GetFullPath(x.Path), Path.GetFullPath(m.Path), StringComparison.OrdinalIgnoreCase) ? m : null);
        bool Same(Clip x, Clip y) => x.Track == y.Track && x.Kind == y.Kind && Near(x.Start, y.Start) && Near(x.Dur, y.Dur) && Near(x.In, y.In, 1e-4) && Near(x.Speed, y.Speed) && x.Reverse == y.Reverse;
        // OpenTimelineIO round trip
        var otioF = Path.Combine(dir, "round.otio"); File.WriteAllText(otioF, Interchange.Otio(ed, "Round"));
        var t1 = InterchangeImport.Read(otioF, 30);
        var e1 = new Editor(); e1.Media.Add(m);
        var s1 = e1.ImportTimeline(t1, Map(t1));
        var missing = ed.Clips.Where(x => !s1.Clips.Any(y => Same(x, y))).Select(x => $"{x.Kind}@{x.Start}").ToList();
        var bb = s1.Clips.FirstOrDefault(y => y.Kind == ClipKind.Video && Near(y.Start, 2));
        var av = s1.Clips.FirstOrDefault(y => y.Kind == ClipKind.Video && Near(y.Start, 0)); var aa1 = s1.Clips.FirstOrDefault(y => y.Kind == ClipKind.Audio && Near(y.Start, 0));
        Check(missing.Count == 0 && s1.Clips.Count == ed.Clips.Count && bb?.TransIn is { Kind: "Cross Dissolve", Align: TransAlign.Center } && Near(bb.TransIn.Dur, 0.5)
              && av?.LinkId != null && av.LinkId == aa1?.LinkId && av.NoAudio && s1.Clips.Any(y => y.Kind == ClipKind.Text && y.Text == "HELLO")
              && s1.Markers?.FirstOrDefault()?.Name == "M1" && Near(s1.Markers![0].T, 1.5) && av.Markers?.FirstOrDefault()?.Name == "on A" && e1.Seq == s1,
              $"OpenTimelineIO round trip: {s1.Clips.Count} clips (cuts, gap, dissolve, 200 %, title, links, markers) as a new open sequence" + (missing.Count > 0 ? " — missing " + string.Join(", ", missing) : ""));
        // EDL round trip (picture + two sound tracks, source file comments)
        var edlF = Path.Combine(dir, "round.edl"); File.WriteAllText(edlF, Interchange.Edl(ed, "Round"));
        var t2 = InterchangeImport.Read(edlF, 30);
        var e2 = new Editor(); e2.Media.Add(m);
        var s2 = e2.ImportTimeline(t2, Map(t2));
        var media2 = ed.Clips.Where(x => x.Kind is ClipKind.Video or ClipKind.Audio).ToList();
        var miss2 = media2.Where(x => !s2.Clips.Any(y => Same(x, y))).Select(x => $"{x.Kind}@{x.Start}/{x.In}").ToList();
        var b2 = s2.Clips.FirstOrDefault(y => y.Kind == ClipKind.Video && Near(y.Start, 2));
        Check(miss2.Count == 0 && b2?.TransIn is { Kind: "Cross Dissolve" } && Near(b2.TransIn.Dur, 0.5) && s2.Markers?.FirstOrDefault()?.Name == "M1" && t2.Name == "Round",
              $"EDL round trip: {s2.Clips.Count} clips, record times from 01:00:00:00, the dissolve, M2 speed, the marker" + (miss2.Count > 0 ? " — missing " + string.Join(", ", miss2) : ""));
        // Final Cut Pro 7 XML (as Premiere writes it), 29.97 fps
        var url = "file://localhost/" + Uri.EscapeDataString(Path.GetFullPath(m.Path).Replace('\\', '/')).Replace("%2F", "/");
        string Clip(string id, int st, int en, int i, int o, string link, string extra = "") =>
            $"<clipitem id=\"{id}\"><name>pat.mp4</name><enabled>TRUE</enabled><rate><timebase>30</timebase><ntsc>FALSE</ntsc></rate><start>{st}</start><end>{en}</end><in>{i}</in><out>{o}</out>" +
            (id == "c1" ? $"<file id=\"f1\"><name>pat.mp4</name><pathurl>{url}</pathurl><rate><timebase>30</timebase></rate></file>" : "<file id=\"f1\"/>") + extra + link + "</clipitem>";
        string L(string a1, string b1) => $"<link><linkclipref>{a1}</linkclipref></link><link><linkclipref>{b1}</linkclipref></link>";
        var speed = "<filter><effect><name>Time Remap</name><effectid>timeremap</effectid><parameter><parameterid>speed</parameterid><value>200</value></parameter><parameter><parameterid>reverse</parameterid><value>FALSE</value></parameter></effect></filter>";
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE xmeml><xmeml version=\"4\"><sequence id=\"s1\"><name>From Premiere</name><rate><timebase>30</timebase><ntsc>TRUE</ntsc></rate>" +
                  "<media><video><format><samplecharacteristics><width>1920</width><height>1080</height></samplecharacteristics></format>" +
                  "<track>" + Clip("c1", 0, -1, 30, 90, L("c1", "a1")) + "<transitionitem><start>50</start><end>70</end><alignment>center</alignment><effect><name>Cross Dissolve</name></effect></transitionitem>" +
                  Clip("c2", -1, 150, 120, 210, L("c2", "a2")) + "</track><track>" + Clip("c3", 200, 260, 0, 120, "", speed) + "</track></video>" +
                  "<audio><track>" + Clip("a1", 0, 60, 30, 90, L("c1", "a1")) + Clip("a2", 60, 150, 120, 210, L("c2", "a2")) + "</track></audio></media>" +
                  "<marker><name>Chapter A</name><comment>hi</comment><in>45</in><out>-1</out></marker></sequence></xmeml>";
        var xmlF = Path.Combine(dir, "premiere.xml"); File.WriteAllText(xmlF, xml);
        var t3 = InterchangeImport.Read(xmlF, 30);
        var e3 = new Editor(); e3.Media.Add(m);
        var s3 = e3.ImportTimeline(t3, Map(t3));
        double f = 1000 / 1001.0 * 30;
        var v1 = s3.Clips.Where(y => y.Kind == ClipKind.Video && y.Track == s3.VideoTracks - 1).OrderBy(y => y.Start).ToList();
        var a1c = s3.Clips.Where(y => y.Kind == ClipKind.Audio).OrderBy(y => y.Start).ToList();
        var v2 = s3.Clips.FirstOrDefault(y => y.Track == s3.VideoTracks - 2);
        Check(Math.Abs(s3.SeqFps - f) < 1e-6 && s3.SeqW == 1920 && s3.SeqH == 1080 && v1.Count == 2 && Near(v1[0].In, 1) && Near(v1[0].Dur, 60 / f, 1e-6) && Near(v1[1].Start, 60 / f, 1e-6)
              && Near(v1[1].In, 4) && Near(v1[1].Dur, 90 / f, 1e-6) && v1[1].TransIn is { Kind: "Cross Dissolve", Align: TransAlign.Center } && Near(v1[1].TransIn!.Dur, 20 / f, 1e-6)
              && a1c.Count == 2 && v1[0].LinkId != null && v1[0].LinkId == a1c[0].LinkId && v1[1].LinkId == a1c[1].LinkId && v1[0].LinkId != v1[1].LinkId
              && v2 != null && Near(v2.Speed, 200) && s3.Markers?.FirstOrDefault() is { Name: "Chapter A", Note: "hi" } mk && Near(mk.T, 45 / f, 1e-6) && s3.Name == "From Premiere",
              $"Premiere / FCP 7 XML at 29.97: 1920×1080, {v1.Count} + {a1c.Count} linked clips (In / Out at the clip's 30 fps), the centred dissolve from a transition item, 200 % from Time Remap, the marker"
              + $" [fps {s3.SeqFps:0.####} {s3.SeqW}x{s3.SeqH}; " + string.Join("; ", v1.Select(y => $"{y.Start:0.####}/{y.In:0.####}/{y.Dur:0.####} {y.TransIn?.Kind}:{y.TransIn?.Dur:0.####}:{y.TransIn?.Align} L{y.LinkId?[..4]}"))
              + "; A " + string.Join("; ", a1c.Select(y => $"{y.Start:0.####} L{y.LinkId?[..4]}")) + $"; v2 {v2?.Speed}; mk {s3.Markers?.FirstOrDefault()?.Name}@{s3.Markers?.FirstOrDefault()?.T:0.####}; {s3.Name}]");
        Check(InterchangeImport.LocalPath("file://localhost/C%3a/Media/My%20Clip.mov") == @"C:\Media\My Clip.mov" && InterchangeImport.LocalPath("file:///D:/a/b.mp4") == @"D:\a\b.mp4",
              "file URLs from other programs (file://localhost/C%3a/…) become local paths");
        bool aafRefused = false; try { File.WriteAllText(Path.Combine(dir, "x.aaf"), "x"); InterchangeImport.Read(Path.Combine(dir, "x.aaf"), 30); } catch (NotSupportedException) { aafRefused = true; }
        Check(aafRefused, "AAF is refused with advice (export XML or OTIO instead)");
    }

    // ---------------- 1. trim tools ----------------
    static void TrimTools(MediaItem m)
    {
        const double fr = 1 / 30.0;
        var ed = new Editor();
        ed.Media.Add(m);
        var (a, aa) = Pair(ed, m, 0, 1, 3);       // source 1 … 4
        var (b, ba) = Pair(ed, m, 3, 5, 3);       // source 5 … 8
        var (c, ca) = Pair(ed, m, 6, 2, 2);       // source 2 … 4
        double end0 = ed.End;
        Clip F(Clip x) => ed.Clips.First(y => y.Id == x.Id);

        // rolling edit at A | B: A longer, B shorter, the sound too, the sequence keeps its length
        double got = ed.TrimEdit(TrimTool.Roll, F(b), true, 10 * fr);
        Check(Near(got, 10 * fr) && Near(F(a).Dur, 3 + 10 * fr) && Near(F(b).Start, 3 + 10 * fr) && Near(F(b).In, 5 + 10 * fr) && Near(F(b).Dur, 3 - 10 * fr)
              && Near(F(aa).Dur, F(a).Dur) && Near(F(ba).In, F(b).In) && Near(ed.End, end0) && ed.UndoLabels.First() == "Rolling Edit",
              $"rolling edit +10 frames: A {F(a).Dur:0.000} s, B from {F(b).Start:0.000} s (source {F(b).In:0.000}), the linked sound rolls too, one undo step");
        ed.Undo();
        Check(Near(F(a).Dur, 3) && Near(F(b).Start, 3) && Near(F(b).In, 5), "undo");
        got = ed.TrimEdit(TrimTool.Roll, F(a), false, -10); ed.Undo();
        Check(Near(got, -(3 - fr)), $"rolling left stops when A is one frame long ({got:0.000} s)");
        got = ed.TrimEdit(TrimTool.Roll, F(a), false, 10); ed.Undo();
        Check(Near(got, 3 - fr), $"rolling right stops when B is one frame long ({got:0.000} s)");
        F(b).In = 0.5; F(ba).In = 0.5;
        got = ed.TrimEdit(TrimTool.Roll, F(a), false, -2); ed.Undo();
        Check(Near(got, -0.5), $"rolling left stops at B's first source frame ({got:0.000} s)");
        F(b).In = 5; F(ba).In = 5;

        // slip: the content moves, the clip stays
        got = ed.TrimEdit(TrimTool.Slip, F(b), false, 0.5);
        Check(Near(got, 0.5) && Near(F(b).In, 4.5) && Near(F(ba).In, 4.5) && Near(F(b).Start, 3) && Near(F(b).Dur, 3) && ed.UndoLabels.First() == "Slip",
              $"slip right by 0.5 s shows earlier frames (In {F(b).In:0.000}), picture and sound alike");
        ed.Undo();
        got = ed.TrimEdit(TrimTool.Slip, F(b), false, 6); ed.Undo();
        Check(Near(got, 5), $"slip stops at the media's start ({got:0.000} s)");
        got = ed.TrimEdit(TrimTool.Slip, F(b), false, -9); ed.Undo();
        Check(Near(got, -4), $"slip stops at the media's end ({got:0.000} s)");

        // slide: the clip moves with its content, the neighbours trim
        got = ed.TrimEdit(TrimTool.Slide, F(b), false, 0.5);
        Check(Near(got, 0.5) && Near(F(b).Start, 3.5) && Near(F(b).In, 5) && Near(F(a).Dur, 3.5) && Near(F(c).Start, 6.5) && Near(F(c).In, 2.5) && Near(F(c).Dur, 1.5)
              && Near(F(ca).Start, 6.5) && Near(F(aa).Dur, 3.5) && Near(ed.End, end0) && ed.UndoLabels.First() == "Slide",
              $"slide right by 0.5 s: B at {F(b).Start:0.000} s, A longer, C shorter from its head, the sound too");
        ed.Undo();
        got = ed.TrimEdit(TrimTool.Slide, F(b), false, -5); ed.Undo();
        Check(Near(got, -2), $"slide left stops at C's first source frame ({got:0.000} s)");
        F(c).In = F(ca).In = 5;
        got = ed.TrimEdit(TrimTool.Slide, F(b), false, -5); ed.Undo();
        Check(Near(got, -(3 - fr)), $"slide left stops when A is one frame long ({got:0.000} s)");
        F(c).In = F(ca).In = 2;
        got = ed.TrimEdit(TrimTool.Slide, F(b), false, 5); ed.Undo();
        Check(Near(got, 2 - fr), $"slide right stops when C is one frame long ({got:0.000} s)");

        // ripple edit: the edge moves, the clips after it follow
        got = ed.TrimEdit(TrimTool.Ripple, F(a), false, -1);
        Check(Near(F(a).Dur, 2) && Near(F(b).Start, 2) && Near(F(c).Start, 5) && Near(F(ba).Start, 2) && Near(ed.End, end0 - 1) && ed.UndoLabels.First() == "Ripple Trim",
              $"ripple the end of A by −1 s: B and C move left (end {ed.End:0.000} s)");
        ed.Undo();
        got = ed.TrimEdit(TrimTool.Ripple, F(b), true, 0.5);
        Check(Near(F(b).Start, 3) && Near(F(b).In, 5.5) && Near(F(b).Dur, 2.5) && Near(F(c).Start, 5.5) && Near(F(ca).Start, 5.5),
              $"ripple the start of B by +0.5 s: B stays at 3 s showing from {F(b).In:0.0} s, C moves left");
        ed.Undo();
        got = ed.TrimEdit(TrimTool.Ripple, F(b), true, -6); ed.Undo();
        Check(Near(got, -5), $"rippling B's head out stops at its media's start ({got:0.000} s)");

        // rate stretch: the same source range in the new length
        got = ed.TrimEdit(TrimTool.Rate, F(c), false, 2);
        var (lo, hi) = TimeMap.Span(F(c));
        Check(Near(F(c).Dur, 4) && Near(F(c).Speed, 50) && Near(F(ca).Speed, 50) && Near(lo, 2) && Near(hi, 4, 1e-3) && ed.UndoLabels.First() == "Rate Stretch",
              $"rate stretch C from 2 to 4 s: {F(c).Speed:0.##} %, source {lo:0.000} … {hi:0.000} s, the sound too");
        ed.Undo();
        got = ed.TrimEdit(TrimTool.Rate, F(a), false, 1); if (got != 0) ed.Undo();
        Check(Near(got, 0), "rate stretch into the next clip is refused");
        F(c).Start = F(ca).Start = 7;
        got = ed.TrimEdit(TrimTool.Rate, F(c), true, -1);
        Check(Near(F(c).Start, 6) && Near(F(c).Dur, 3) && Near(F(c).Speed, 66.6667, 1e-3) && Near(F(c).In, 2), $"rate stretch C's head into the gap: {F(c).Speed:0.###} %, still from source 2 s");
        ed.Undo();
        ed.SetSpeed(new[] { F(c) }, 100, null, true, true, false);
        var (lo0, hi0) = TimeMap.Span(F(c));
        got = ed.TrimEdit(TrimTool.Rate, F(c), false, 2);
        var (lo1, hi1) = TimeMap.Span(F(c));
        Check(F(c).Reverse && Near(F(c).Speed, 50) && Near(lo1, lo0, 1e-3) && Near(hi1, hi0, 1e-3), $"a reversed clip keeps its frames when stretched ({lo1:0.000} … {hi1:0.000} s)");
        ed.Undo(); ed.Undo();
        F(c).Start = F(ca).Start = 6;

        // alone (Alt): just the clip
        got = ed.TrimEdit(TrimTool.Slip, F(b), false, 0.5, alone: true);
        Check(Near(F(b).In, 4.5) && Near(F(ba).In, 5), "Alt: slip the picture without its linked sound");
        ed.Undo();
        // locked tracks stay
        ed.SetLocked(Tracks.A1, true);
        got = ed.TrimEdit(TrimTool.Roll, F(a), false, 0.5);
        Check(Near(F(a).Dur, 3.5) && Near(F(aa).Dur, 3), "a locked track's clips don't roll");
        ed.Undo(); ed.SetLocked(Tracks.A1, false);
    }
}
