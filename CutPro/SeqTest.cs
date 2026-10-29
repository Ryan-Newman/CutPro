using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>CutPro.exe --seqtest dir: sequence formats and export settings. Exports at a larger output size than the 720-line
/// logical canvas (1080p, vertical 1080×1920 Fill with pan, Fit + blur) must look like the preview (export scaled back to the
/// canvas); other frame rates give the right rate + frame count (animated title, LayerPlan, audio); changing the frame rate
/// conforms clips/keyframes and undoes exactly; every codec/container preset and each working hardware encoder writes what
/// it says; In/Out export starts on the In frame.</summary>
public static class SeqTest
{
    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        try
        {
            Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
            var pattern = Path.Combine(dir, "pattern.png");
            FxTest.Save(FxTest.Pattern(), pattern);
            var tone = Path.Combine(dir, "tone.wav");
            if (!File.Exists(tone)) Run(ff, $"-y -v error -f lavfi -i \"sine=frequency=1000:sample_rate=48000:duration=3\" -ac 2 \"{tone}\"");
            var img = await MediaProbe.ProbeAsync(pattern) ?? throw new Exception("pattern import failed");
            var snd = await MediaProbe.ProbeAsync(tone) ?? throw new Exception("tone import failed");

            Editor New() { var e = new Editor(); e.Media.Add(img); e.Media.Add(snd); return e; }
            Clip Img(Editor e, double start, double dur, int track = 2) { var c = new Clip { MediaId = img.Id, Kind = ClipKind.Image, Track = track, Start = start, Dur = dur }; e.Clips.Add(c); return c; }
            Clip Title(Editor e, double start, double dur, string text) { var c = new Clip { Kind = ClipKind.Text, Track = 1, Start = start, Dur = dur, Text = text, Style = StyleLibrary.Get("Bold Impact").With(s => s.Size = 110) }; e.Clips.Add(c); return c; }
            void Key(Clip c, AnimProp p, double src, double v) => Anim.SetKey(c, p, src, v);

            async Task<string?> Export(Editor e, string file, ExportSettings o)
            {
                var tmp = Path.Combine(dir, "tmp_" + Path.GetFileNameWithoutExtension(file));
                var titles = o.AudioOnly ? new Dictionary<string, string>() : TitleVisual.PrepareExport(e, tmp, o.OutW, o.OutH);
                var masks = o.AudioOnly ? new Dictionary<string, string>() : MaskExport.Prepare(e, tmp, null, o.Scale);
                return await Exporter.RunAsync(ff, e, file, new Progress<double>(), CancellationToken.None, titles, masks, o);
            }
            // preview at the logical canvas vs the export scaled back to it (area average)
            void Parity(Editor e, string file, double t, string what, double minPsnr, double maxWorst, int f)
            {
                int W = Editor.W, H = Editor.H;
                var pv = new Preview { Ed = e, Width = W, Height = H, Background = Brushes.Black };
                pv.Sync(t, false);
                pv.Measure(new Size(W, H)); pv.Arrange(new Rect(0, 0, W, H)); pv.UpdateLayout();
                var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
                var pa = new byte[W * H * 4]; rtb.CopyPixels(pa, W * 4, 0);
                var pb = Frame(ff, file, t - 0.001, W, H);
                var (psnr, worst) = FxTest.Compare(pa, pb);
                var (dx, dy) = Shift(pa, pb, W, H);
                Check(psnr >= minPsnr && worst <= maxWorst && Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1,
                      $"preview = export {what}: PSNR {psnr:0.0} dB, worst block {worst:0.0}/255, best alignment shift ({dx},{dy}) px");
                FxTest.Save(FxTest.SideBySide(pa, pb), Path.Combine(dir, $"seq_{f}.png"));
            }

            // ---- 1. 1080p output of the 720-line canvas: static, animated (LayerPlan), title, blur, object mask ----
            {
                var ed = New();
                var a = Img(ed, 0, 1); a.Scale = 80; a.X = 12; a.Y = -9;
                var b = Img(ed, 1, 1); Key(b, AnimProp.Scale, 0, 100); Key(b, AnimProp.Scale, 1, 60); Key(b, AnimProp.Rotation, 0, 0); Key(b, AnimProp.Rotation, 1, 20);
                Img(ed, 2, 1); var tt = Title(ed, 2, 1, "Grace upon grace");
                var d = Img(ed, 3, 1); d.Blur = 8;
                var m = Img(ed, 4, 1);
                var mk = new ObjectMask { Kind = MaskKind.Freehand, Effect = MaskFx.Blur, Amount = 25, Feather = 6 };
                m.Masks.Add(mk);
                ed.MaskTracks[mk.Id] = new MaskTrack { Frames = { [0] = new List<float[]> { new float[] { 330f / 1280, 430f / 720, 470f / 1280, 420f / 720, 500f / 1280, 560f / 720, 400f / 1280, 620f / 720, 320f / 1280, 560f / 720 } } } };
                var o = new ExportSettings { Height = 1080 };
                Check(o.OutW == 1920 && o.OutH == 1080 && Math.Abs(o.Scale - 1.5) < 1e-9, $"1080p settings on a 16:9 sequence: {o.OutW}×{o.OutH}, scale {o.Scale}");
                var tmp = Path.Combine(dir, "tmp_titles1080");
                var tf = TitleVisual.PrepareExport(ed, tmp, o.OutW, o.OutH);
                if (tf.TryGetValue(tt.Id, out var png) && png.EndsWith(".png"))
                {
                    var bf = BitmapFrame.Create(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    Check(bf.PixelWidth == 1920 && bf.PixelHeight == 1080, $"title rendered at the output size (sharp, not upscaled): {bf.PixelWidth}×{bf.PixelHeight}");
                }
                else Check(false, "title PNG for the 1080p export");
                var mp4 = Path.Combine(dir, "seq1080.mp4");
                var err = await Export(ed, mp4, o);
                Check(err == null, "1080p export" + (err == null ? "" : ": " + err));
                if (err == null)
                {
                    var p = Probe(ff, mp4);
                    Check(p.Get("width") == "1920" && p.Get("height") == "1080", $"1080p file is {p.Get("width")}×{p.Get("height")}");
                    Parity(ed, mp4, 0.5, "1080p, static scaled + moved image", 23, 30, 15);   // 1 px grid lines resampled at ×1.2 then area-reduced
                    Parity(ed, mp4, 1.5, "1080p, animated scale + rotation (LayerPlan)", 23, 36, 45);
                    Parity(ed, mp4, 2.5, "1080p, title", 28, 40, 75);
                    Parity(ed, mp4, 3.5, "1080p, blur 8", 30, 30, 105);
                    Parity(ed, mp4, 4.5, "1080p, object mask blur 25 / feather 6", 28, 36, 135);

                    // In/Out range: starts on the In frame, lasts Out − In
                    ed.MarkIn = 1.2; ed.MarkOut = 1.8;
                    var io = new ExportSettings { Height = 1080, InOutOnly = true };
                    var mp4io = Path.Combine(dir, "seq_inout.mp4");
                    var err2 = await Export(ed, mp4io, io);
                    Check(err2 == null, "In/Out export" + (err2 == null ? "" : ": " + err2));
                    if (err2 == null)
                    {
                        var q = Probe(ff, mp4io);
                        Check(q.Get("nb_read_frames") == "18", $"In 1.2 s → Out 1.8 s = 18 frames: got {q.Get("nb_read_frames")}");
                        double ad = q.Num("duration", "audio");
                        Check(ad < 0 || Math.Abs(ad - 0.6) < 0.05, $"In/Out audio length 0.6 s: {ad:0.000}");
                        var first = Frame(ff, mp4io, 0, Editor.W, Editor.H);
                        var at = Frame(ff, mp4, 1.2 - 0.001, Editor.W, Editor.H);
                        var (ps, _) = FxTest.Compare(first, at);
                        Check(ps >= 35, $"In/Out first frame = timeline frame at the In point: PSNR {ps:0.0} dB");
                    }
                }
            }

            // ---- 2. Shorts 1080×1920, Fill: cover + pan (static, keyframed, static off-centre) ----
            {
                Editor.SetSequence(1080, 1920, 30, ReframeMode.Fill);
                Check(Editor.W == 405 && Editor.OutW == 1080 && Editor.OutH == 1920, $"9:16 sequence: canvas {Editor.W}×{Editor.H}, output {Editor.OutW}×{Editor.OutH}");
                var ed = New();
                var c0 = Img(ed, 0, 1);
                var c1 = Img(ed, 1, 1); Key(c1, AnimProp.Pan, 0, -100); Key(c1, AnimProp.Pan, 1, 100);
                var c2 = Img(ed, 2, 1); c2.Pan = 60;
                Title(ed, 0, 3, "Shorts");
                Check(Editor.CoversFrame(c0) && Anim.PropsOf(c0).Contains(AnimProp.Pan), "Fill: V1 images cover the frame and get the Reframe pan property");
                var mp4 = Path.Combine(dir, "seq_shorts_fill.mp4");
                var err = await Export(ed, mp4, new ExportSettings());
                Check(err == null, "Shorts (Fill) export" + (err == null ? "" : ": " + err));
                if (err == null)
                {
                    var p = Probe(ff, mp4);
                    Check(p.Get("width") == "1080" && p.Get("height") == "1920", $"Shorts file is {p.Get("width")}×{p.Get("height")}");
                    Parity(ed, mp4, 0.5, "Shorts Fill, centred", 26, 36, 1015);   // picture ×2.67 for 1920, area-reduced to compare
                    Parity(ed, mp4, 1.0 + 1 / 30.0, "Shorts Fill, pan keyframes near −100", 26, 40, 1031);
                    Parity(ed, mp4, 1.9, "Shorts Fill, pan keyframes near +100", 26, 40, 1057);
                    Parity(ed, mp4, 2.5, "Shorts Fill, pan 60", 26, 36, 1075);
                    // pan direction: −100 shows the picture's left edge (blue end of the gradient), +100 the right (orange)
                    var left = Frame(ff, mp4, 1.0, Editor.W, Editor.H); var right = Frame(ff, mp4, 1.99, Editor.W, Editor.H);
                    var (lb, lr) = EdgeColour(left, Editor.W, Editor.H, true); var (rb, rr) = EdgeColour(right, Editor.W, Editor.H, false);
                    Check(lb > lr && rr > rb, $"pan −100 shows the left of the picture (B {lb:0} > R {lr:0}), +100 the right (R {rr:0} > B {rb:0})");
                }
                // auto reframe: pan formula centres a subject; the saliency path keys an image clip within range
                Check(Math.Abs(Reframe.PanFor(0.5, 1280, 405)) < 1e-9 && Math.Abs(Reframe.PanFor(0.1, 1280, 405) + 100) < 1e-9, "PanFor: centre → 0, far left → −100");
                var rf = await Reframe.AnalyzeAsync(ed, c0, null, CancellationToken.None);
                Check(rf != null && rf.Keys.Count >= 1 && rf.Keys.All(k => k.pan >= -100 && k.pan <= 100), $"auto reframe ({rf?.How}): {rf?.Keys.Count} pan key(s) in range");
            }

            // ---- 3. Shorts, Fit + blur backdrop ----
            {
                Editor.SetSequence(1080, 1920, 30, ReframeMode.FitBlur);
                var ed = New(); Img(ed, 0, 1);
                var mp4 = Path.Combine(dir, "seq_shorts_blur.mp4");
                var err = await Export(ed, mp4, new ExportSettings());
                Check(err == null, "Shorts (Fit + blur) export" + (err == null ? "" : ": " + err));
                if (err == null) Parity(ed, mp4, 0.5, "Shorts Fit + blurred backdrop", 25, 30, 2015);   // fitted picture: 1 px lines reduced 1280 → 405
            }

            // ---- 4. frame rates: rate, frame count, animated title + LayerPlan timing, audio length ----
            foreach (double fps in new[] { 25.0, 29.97, 23.976, 60.0 })
            {
                Editor.SetSequence(1280, 720, fps, ReframeMode.Fit);
                var ed = New();
                var b = Img(ed, 0, 2); Key(b, AnimProp.X, 0, -20); Key(b, AnimProp.X, 2, 20);
                var tt = Title(ed, 0, 2, "Rate"); Key(tt, AnimProp.Scale, 0, 60); Key(tt, AnimProp.Scale, 2, 140);
                ed.Clips.Add(new Clip { MediaId = snd.Id, Kind = ClipKind.Audio, Track = 3, Start = 0, Dur = 2 });
                var mp4 = Path.Combine(dir, $"seq_{fps.ToString(CultureInfo.InvariantCulture)}.mp4");
                var err = await Export(ed, mp4, new ExportSettings());
                Check(err == null, $"{TimeUtil.Label(fps)} fps export" + (err == null ? "" : ": " + err));
                if (err != null) continue;
                var p = Probe(ff, mp4);
                int expect = (int)Math.Round(2 * TimeUtil.Fps);
                Check(p.Get("r_frame_rate") == (TimeUtil.Rate.Contains('/') ? TimeUtil.Rate : TimeUtil.Rate + "/1"), $"{TimeUtil.Label(fps)} fps: stream rate {p.Get("r_frame_rate")} (want {TimeUtil.Rate})");
                Check(int.TryParse(p.Get("nb_read_frames"), out int n) && Math.Abs(n - expect) <= 1, $"{TimeUtil.Label(fps)} fps: {p.Get("nb_read_frames")} frames in 2 s (want {expect})");
                double ad = p.Num("duration", "audio");
                Check(Math.Abs(ad - 2) < 0.05, $"{TimeUtil.Label(fps)} fps: audio {ad:0.000} s");
                int f = (int)Math.Round(TimeUtil.Fps * 1.37);
                Parity(ed, mp4, f / TimeUtil.Fps, $"at {TimeUtil.Label(fps)} fps, frame {f} (animated title + moving image)", 26, 40, 3000 + (int)fps);
            }

            // ---- 5. changing the frame rate conforms clips + keyframes; undo/redo restore it exactly ----
            {
                Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
                var ed = New();
                var c = Img(ed, 1.0, 61 / 30.0); c.In = 7 / 30.0;
                Key(c, AnimProp.Opacity, c.In + 16 / 30.0, 40); Key(c, AnimProp.Opacity, c.In + 47 / 30.0, 90);
                var c2 = Img(ed, c.End, 1.5);
                string before = JsonSerializer.Serialize(ed.Clips);
                ed.ApplySequence(1920, 1080, 25, ReframeMode.Fit);
                bool OnGrid(double v) => Math.Abs(v * 25 - Math.Round(v * 25)) < 1e-6;
                Check(Math.Abs(TimeUtil.Fps - 25) < 1e-9 && Editor.OutW == 1920 && Editor.OutH == 1080, $"ApplySequence: {Editor.OutW}×{Editor.OutH} @ {TimeUtil.Fps}");
                Check(ed.Clips.All(x => OnGrid(x.Start) && OnGrid(x.Dur) && OnGrid(x.In)), "conformed clip start/length/in on the 25 fps grid: " +
                      string.Join(" ", ed.Clips.Select(x => $"{x.Start * 25:0.###}/{x.Dur * 25:0.###}/{x.In * 25:0.###}")));
                Check(c.Anim!.Values.SelectMany(a => a.Keys).All(k => OnGrid(k.T)), "conformed keyframes on the 25 fps grid");
                Check(Math.Abs(c2.Start - c.End) < 1e-9, $"adjacent clips stay adjacent after conforming ({c.End:0.####} → {c2.Start:0.####})");
                ed.Undo();
                Check(Math.Abs(TimeUtil.Fps - 30) < 1e-9 && Editor.OutW == 1280 && JsonSerializer.Serialize(ed.Clips) == before, $"undo restores 1280×720 @ 30 and the exact clip times");
                ed.Redo();
                Check(Math.Abs(TimeUtil.Fps - 25) < 1e-9 && Editor.OutW == 1920, "redo applies 25 fps / 1080p again");
                ed.Undo();
            }

            // ---- 6. codecs / containers / audio-only + every hardware encoder that works here ----
            {
                Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
                var ed = New(); Img(ed, 0, 1);
                ed.Clips.Add(new Clip { MediaId = snd.Id, Kind = ClipKind.Audio, Track = 3, Start = 0, Dur = 1 });
                var usable = await ExportSettings.UsableEncodersAsync();
                log.Add("INFO usable encoders: " + string.Join(", ", usable.OrderBy(x => x)));
                var cases = new List<(ExportSettings o, string vcodec, string acodec)>();
                foreach (var pr in ExportSettings.Presets.Where(x => x.Name.Contains("H.265") || x.Name.Contains("AV1") || x.Name.Contains("ProRes") || x.Name.Contains("WebM") || x.Name.Contains("DNxHR") || x.Name.StartsWith("HLG") || x.Name.StartsWith("HDR10") || x.Name.StartsWith("Audio") || x.Name.StartsWith("YouTube 1080")))
                {
                    if (!pr.AudioOnly && !usable.Contains(pr.VideoEncoder)) { log.Add($"INFO skipped {pr.Name}: {pr.VideoEncoder} not available"); continue; }
                    cases.Add((pr, pr.Video switch { "none" => "", "prores" or "prores4444" or "prores4444a" => "prores", "dnxhr_lb" or "dnxhr_sq" or "dnxhr_hq" or "dnxhr_hqx" or "dnxhr_444" => "dnxhd", "vp9a" => "vp9", _ => pr.Video }, pr.Audio switch { "pcm" => pr.Container == "wav" ? "pcm_s16le" : "pcm_s24le", "mp3" => "mp3", "opus" => "opus", _ => "aac" }));
                }
                foreach (var enc in new[] { "nvenc", "qsv", "amf" })
                    foreach (var codec in new[] { "h264", "hevc", "av1" })
                    {
                        var o = new ExportSettings { Name = $"{codec} {enc}", Video = codec, Encoder = enc };
                        if (usable.Contains(o.VideoEncoder))
                        {
                            cases.Add((o, codec, "aac"));
                            var vbr = o.Clone(); vbr.ConstantQuality = false; vbr.BitrateMbps = 6; vbr.Name += " (bitrate)";
                            cases.Add((vbr, codec, "aac"));
                        }
                    }
                foreach (var (o0, vcodec, acodec) in cases)
                {
                    var o = o0.Clone(); if (!o.AudioOnly) o.Height = 360; o.Aspect = 0;
                    var file = Path.Combine(dir, "codec_" + string.Concat(o.Name.Where(char.IsLetterOrDigit)) + o.Extension);
                    var sw = Stopwatch.StartNew();
                    var err = await Export(ed, file, o);
                    if (err != null) { Check(false, $"{o.Name}: {err.Split('\n')[0]}"); continue; }
                    var p = Probe(ff, file);
                    bool ok = (vcodec == "" ? p.Get("codec_name", "video") == null : p.Get("codec_name", "video") == vcodec && p.Get("width") == "640" && p.Get("height") == "360")
                              && p.Get("codec_name", "audio") == acodec;
                    Check(ok, $"{o.Name} → {Path.GetExtension(file)}: video {p.Get("codec_name", "video") ?? "none"} {p.Get("width")}×{p.Get("height")}, audio {p.Get("codec_name", "audio")} ({sw.Elapsed.TotalSeconds:0.0} s)");
                }
            }
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        finally { Editor.SetSequence(1280, 720, 30, ReframeMode.Fit); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "seqtest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static void Run(string ff, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
    }

    /// <summary>One frame scaled (area average) to w × h, BGRA.</summary>
    internal static byte[] Frame(string ff, string file, double t, int w, int h)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-v", "error", "-i", file, "-ss", Math.Max(0, t).ToString("0.###", CultureInfo.InvariantCulture), "-frames:v", "1",
                                  "-vf", $"scale={w}:{h}:flags=area", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" })
            psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        var b = ms.ToArray();
        if (b.Length < w * h * 4) throw new Exception($"frame extract at {t}s from {Path.GetFileName(file)} failed");
        return b;
    }

    /// <summary>Best whole-pixel alignment (a placement error shows up as a shift; resampling differences don't).</summary>
    internal static (int dx, int dy) Shift(byte[] a, byte[] b, int W, int H)
    {
        (int dx, int dy, double e) best = (0, 0, double.MaxValue);
        for (int sy = -3; sy <= 3; sy++)
            for (int sx = -3; sx <= 3; sx++)
            {
                double se = 0;
                for (int y = 20; y < H - 20; y += 2)
                    for (int x = 20; x < W - 20; x += 2)
                    {
                        int i = (y * W + x) * 4, j = ((y + sy) * W + x + sx) * 4;
                        for (int ch = 0; ch < 3; ch++) { double d = a[i + ch] - b[j + ch]; se += d * d; }
                    }
                if (se < best.e) best = (sx, sy, se);
            }
        return (best.dx, best.dy);
    }

    /// <summary>Mean blue / red of a 12 px strip at the left or right edge (middle third, away from the title).</summary>
    static (double b, double r) EdgeColour(byte[] px, int W, int H, bool left)
    {
        double sb = 0, sr = 0; int n = 0;
        for (int y = H / 3; y < 2 * H / 3; y++)
            for (int x = left ? 0 : W - 12; x < (left ? 12 : W); x++) { int i = (y * W + x) * 4; sb += px[i]; sr += px[i + 2]; n++; }
        return (sb / n, sr / n);
    }

    sealed class ProbeInfo
    {
        public readonly List<Dictionary<string, string>> Streams = new();
        public string? Get(string key, string type = "video") => Streams.FirstOrDefault(s => s.GetValueOrDefault("codec_type") == type)?.GetValueOrDefault(key);
        public double Num(string key, string type = "video") => double.TryParse(Get(key, type), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;
    }

    static ProbeInfo Probe(string ff, string file)
    {
        var fp = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe");
        var psi = new ProcessStartInfo(fp) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-v", "error", "-count_frames", "-show_entries", "stream=codec_type,codec_name,width,height,r_frame_rate,nb_read_frames,duration", "-of", "json", file })
            psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        var json = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        var info = new ProbeInfo();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
                info.Streams.Add(s.EnumerateObject().ToDictionary(k => k.Name, k => k.Value.ValueKind == JsonValueKind.String ? k.Value.GetString()! : k.Value.ToString()));
        }
        catch { }
        return info;
    }
}
