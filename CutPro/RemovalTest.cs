using System.Diagnostics;
using System.Globalization;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// CutPro.exe --removaltest dir: object removal and clean plates, headless, against the truth. A textured background pans 2 px a frame (the
/// camera moving); a red box crosses it 6 px a frame; the same video without the box is the truth.
/// - the camera's movement between frames is measured (sign and size);
/// - Remove Object (the box's tracked rectangle): inside the mask the cleaned copy = the truth (PSNR), outside it = the original, no red left;
/// - a box that never moves: nothing to take from other frames — filled from its edges, no red left;
/// - a clean plate of a still camera = the background;
/// - on a timeline: the clip plays the cleaned copy (keyframes, clip markers and its other masks re-based), one undo step, the export shows no box.
/// </summary>
public static class RemovalTest
{
    static List<string> log = new();
    static int fails;
    static void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
    static string ff = "";
    const int W = 640, H = 360, Box = 60, BoxY = 150;
    /// <summary>Where the box is in each frame of the moving-box videos (measured: a perfect tracker).</summary>
    static int[] boxX = Array.Empty<int>();
    static int BoxX(int n) => n < boxX.Length ? boxX[n] : 40 + 6 * n;
    /// <summary>The red box's left edge in every frame of a file (−10000 when it is off the picture).</summary>
    static int[] MeasureBox(string file, int frames)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-i", file, "-frames:v", frames.ToString(), "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        var res = new List<int>(); var buf = new byte[W * H * 3]; var s = p.StandardOutput.BaseStream;
        while (res.Count < frames)
        {
            int got = 0; while (got < buf.Length) { int k = s.Read(buf, got, buf.Length - got); if (k <= 0) break; got += k; }
            if (got < buf.Length) break;
            int left = -10000, y = BoxY + Box / 2;
            for (int x = 0; x < W; x++) { int i = (y * W + x) * 3; if (buf[i + 2] > 180 && buf[i + 1] < 70 && buf[i] < 70) { left = x; break; } }
            if (left == 0) for (int x = 0; x < W; x++) { int i = (y * W + x) * 3; if (!(buf[i + 2] > 180 && buf[i + 1] < 70)) { left = x - Box; break; } }   // (entering from the left)
            res.Add(left);
        }
        p.WaitForExit();
        return res.ToArray();
    }

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        log = new(); fails = 0;
        ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        AppSettings.ReadOnly = true;
        ObjectRemoval.DirOverride = Path.Combine(dir, "cleaned");
        try { await Body(dir); }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        finally { ObjectRemoval.DirOverride = null; }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "removaltest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static void Exec(string args) { using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!; p.StandardError.ReadToEnd(); p.WaitForExit(); }

    /// <summary>A textured picture larger than the frame; videos crop it (pan = the camera moving) and draw the red box.</summary>
    static string Video(string dir, string name, bool pan, bool box, bool still = false)
    {
        var f = Path.Combine(dir, name);
        if (File.Exists(f)) return f;
        var tex = Path.Combine(dir, "texture.png");
        if (!File.Exists(tex)) Exec($"-y -v error -f lavfi -i \"mandelbrot=s=1400x400:maxiter=60:start_scale=1.2:end_scale=1.2,format=gray,format=rgb24\" -frames:v 1 \"{tex}\"");
        string crop = pan ? "crop=640:360:x='100+2*n':y=20" : "crop=640:360:x=100:y=20";
        if (box)   // (a red square overlaid: drawbox has no frame number)
            Exec($"-y -v error -loop 1 -framerate 30 -i \"{tex}\" -f lavfi -i color=red:s={Box}x{Box}:r=30 -filter_complex \"[0:v]{crop}[bg];[bg][1:v]overlay=x='{(still ? "300" : "40+6*n")}':y={BoxY},format=yuv420p\" -frames:v 180 -c:v libx264 -crf 8 -g 30 \"{f}\"");
        else
            Exec($"-y -v error -loop 1 -framerate 30 -i \"{tex}\" -vf \"{crop},format=yuv420p\" -frames:v 180 -c:v libx264 -crf 8 -g 30 \"{f}\"");
        return f;
    }

    static MaskTrack Track(Func<int, (int x, int y)> at, int frames, int pad = 0)
    {
        var tr = new MaskTrack();
        for (int n = 0; n < frames; n++)
        {
            var (x, y) = at(n);
            tr.Set(n, new List<float[]> { new[] { (x - pad) / (float)W, (y - pad) / (float)H, (x + Box + pad) / (float)W, (y - pad) / (float)H, (x + Box + pad) / (float)W, (y + Box + pad) / (float)H, (x - pad) / (float)W, (y + Box + pad) / (float)H } }, n == 0);
        }
        return tr;
    }

    static async Task Body(string dir)
    {
        Editor.SetSequence(W, H, 30, ReframeMode.Fit);
        var withBox = await MediaProbe.ProbeAsync(Video(dir, "pan_box.mp4", true, true)) ?? throw new Exception(MediaProbe.LastError);
        boxX = MeasureBox(withBox.Path, 180);
        var truth = Video(dir, "pan_truth.mp4", true, false);
        var stillBox = await MediaProbe.ProbeAsync(Video(dir, "still_box.mp4", false, true, still: true)) ?? throw new Exception(MediaProbe.LastError);
        var still = await MediaProbe.ProbeAsync(Video(dir, "still_movingbox.mp4", false, true)) ?? throw new Exception(MediaProbe.LastError);
        var stillTruth = Video(dir, "still_truth.mp4", false, false);

        // ---------- the camera's movement ----------
        {
            var a = Frame(truth, 30); var b = Frame(truth, 31); var c = Frame(truth, 40);
            var ta = ObjectRemoval.Thumb(a, null, W, H);
            var (dx1, dy1) = ObjectRemoval.Shift(ta, ObjectRemoval.Thumb(b, null, W, H), W, H);
            var (dx10, dy10) = ObjectRemoval.Shift(ta, ObjectRemoval.Thumb(c, null, W, H), W, H);
            Check(Math.Abs(dx1 + 2) < 0.35 && Math.Abs(dy1) < 0.35 && Math.Abs(dx10 + 20) < 0.8 && Math.Abs(dy10) < 0.5,
                  $"the camera's movement: one frame ({dx1:0.00}, {dy1:0.00}) px, ten frames ({dx10:0.00}, {dy10:0.00}) px (the picture moves 2 px left a frame)");
        }

        // ---------- remove the moving box over a panning background ----------
        {
            var tr = Track(n => (BoxX(n), BoxY), 180);
            var sw = Stopwatch.StartNew();
            var res = await ObjectRemoval.RenderAsync(withBox, new[] { tr }, 1.0, 5.0, new ObjectRemoval.Options { Range = 60, Expand = 4 }, null, CancellationToken.None);
            double secs = sw.Elapsed.TotalSeconds;
            Check(File.Exists(res.File) && Math.Abs(res.Start) < 1e-9 && res.Frames == 180 && res.Temporal > 0.97,
                  $"Remove Object: a cleaned copy from 0 s ({res.Frames} frames, {secs:0.0} s), {res.Temporal:0.0%} of the masked pixels from other frames");
            double inS = 0, inN = 0, outS = 0, outN = 0; int red = 0, framesChecked = 0; var where = new List<string>();
            foreach (int k in new[] { 35, 60, 90, 120, 145 })
            {
                var got = Frame(res.File, k); var want = Frame(truth, k); var orig = Frame(withBox.Path, k);
                framesChecked++;
                int bx = BoxX(k);
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int i = (y * W + x) * 3;
                        bool inside = x >= bx - 4 && x < bx + Box + 4 && y >= BoxY - 4 && y < BoxY + Box + 4;
                        for (int ch = 0; ch < 3; ch++)
                        {
                            double e = got[i + ch] - (inside ? want[i + ch] : orig[i + ch]);
                            if (inside) { inS += e * e; inN++; } else { outS += e * e; outN++; }
                        }
                        if (got[i + 2] > 180 && got[i + 1] < 60 && got[i] < 60) { red++; if (where.Count < 6) where.Add($"f{k} ({x},{y}) box {bx}"); }
                    }
            }
            double pin = 10 * Math.Log10(255.0 * 255 / Math.Max(1e-9, inS / inN)), pout = 10 * Math.Log10(255.0 * 255 / Math.Max(1e-9, outS / outN));
            Check(pin >= 28 && pout >= 38 && red < 20, $"…inside the mask = the background (PSNR {pin:0.0} dB against the video without the box), outside = the original ({pout:0.0} dB), red pixels left: {red} in {framesChecked} frames" + (where.Count > 0 ? " " + string.Join("; ", where) : ""));
            SavePng(Path.Combine(dir, "removal_frame90.png"), Frame(res.File, 90));
        }

        // ---------- a box that never moves: filled from its edges ----------
        {
            var tr = Track(_ => (300, BoxY), 180);
            var res = await ObjectRemoval.RenderAsync(stillBox, new[] { tr }, 1.0, 4.0, new ObjectRemoval.Options { Range = 30, Expand = 3 }, null, CancellationToken.None);
            var got = Frame(res.File, 60);
            int red = 0; for (int i = 0; i < got.Length; i += 3) if (got[i + 2] > 180 && got[i + 1] < 60 && got[i] < 60) red++;
            Check(res.SpatialShare > 0.95 && red < 10, $"a box that never moves: {res.SpatialShare:0%} filled from its edges, red pixels left: {red}");
        }

        // ---------- clean plate of a still camera ----------
        {
            var tr = Track(n => (BoxX(n), BoxY), 180);
            var f = await ObjectRemoval.CleanPlateAsync(still, new[] { tr }, 0.5, 5.5, 3.0, null, CancellationToken.None);
            var plate = Load(f); var want = Frame(stillTruth, 90);
            double se = 0; for (int i = 0; i < plate.Length; i++) { double e = plate[i] - want[i]; se += e * e; }
            double p = 10 * Math.Log10(255.0 * 255 / Math.Max(1e-9, se / plate.Length));
            int red = 0; for (int i = 0; i < plate.Length; i += 3) if (plate[i + 2] > 180 && plate[i + 1] < 60 && plate[i] < 60) red++;
            Check(p >= 32 && red < 10, $"clean plate: the background without the box ({p:0.0} dB, red pixels {red})");
        }

        // ---------- on a timeline ----------
        {
            var ed = new Editor(); ed.Media.Add(withBox);
            var c = new Clip { MediaId = withBox.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = 0, In = 1.5, Dur = 3, NoAudio = false };
            ed.Clips.Add(c);
            var boxMask = new ObjectMask { Name = "Box", Kind = MaskKind.Rectangle, Effect = MaskFx.Blur };
            var other = new ObjectMask { Name = "Other", Kind = MaskKind.Ellipse, Effect = MaskFx.Darken };
            c.Masks.Add(boxMask); c.Masks.Add(other);
            ed.MaskTracks[boxMask.Id] = Track(n => (BoxX(n), BoxY), 180);
            var ot = new MaskTrack(); ot.Set(60, new List<float[]> { MaskGeometry.Shape(MaskKind.Ellipse, new System.Windows.Rect(0.1, 0.1, 0.2, 0.2)) }, true); ed.MaskTracks[other.Id] = ot;
            string otherId0 = other.Id;
            Anim.SetKey(c, AnimProp.Scale, 2.0, 100); Anim.SetKey(c, AnimProp.Scale, 3.0, 120);
            c.Markers = new() { new Marker { T = 2.5, Name = "here" } };
            ed.Edit(() => { }, "start");
            var (lo, hi) = TimeMap.Span(c);
            var res = await ObjectRemoval.RenderAsync(withBox, new[] { ed.MaskTracks[boxMask.Id] }, lo, hi, new ObjectRemoval.Options { Expand = 4 }, null, CancellationToken.None);
            var item = await MediaProbe.ProbeAsync(res.File) ?? throw new Exception(MediaProbe.LastError);
            ed.Media.Add(item);
            int undo0 = ed.UndoLabels.Count();
            ed.UseCleaned(ed.Clips[0], item, res.Start, boxMask.Id);
            var x = ed.Clips[0];
            double d = res.Start;
            var newOther = x.Masks.FirstOrDefault();
            bool shifted = newOther != null && newOther.Id != otherId0 && ed.MaskTracks.TryGetValue(newOther.Id, out var nt) && nt.Frames.ContainsKey(60 - MaskGeometry.FrameOf(d)) && ed.MaskTracks[otherId0].Frames.ContainsKey(60);

            Check(x.MediaId == item.Id && Math.Abs(x.In - (1.5 - d)) < 1e-9 && x.Masks.Count == 1 && shifted && Math.Abs(x.Anim![AnimProp.Scale].Keys[0].T - (2.0 - d)) < 1e-9
                  && Math.Abs(x.Markers![0].T - (2.5 - d)) < 1e-9 && ed.UndoLabels.Count() == undo0 + 1 && ed.UndoLabels.First() == "Remove Object",
                  $"the clip plays the cleaned copy (from {d:0.000} s of the file): In, keyframes, its clip marker and its other mask re-based, the box's mask gone — one undo step" +
                  $" [media {x.MediaId == item.Id}, In {x.In:0.###}, masks {x.Masks.Count}, other shifted {shifted}, key {x.Anim?[AnimProp.Scale].Keys[0].T:0.###}, marker {x.Markers?[0].T:0.###}, undo +{ed.UndoLabels.Count() - undo0} “{ed.UndoLabels.FirstOrDefault()}”]");
            ed.Undo();
            Check(ed.Clips[0].MediaId == withBox.Id && ed.Clips[0].Masks.Count == 2 && Math.Abs(ed.Clips[0].In - 1.5) < 1e-9, "undo: the original footage, both masks");
            ed.Redo();
            var mp4 = Path.Combine(dir, "removal_export.mp4");
            var err = await Exporter.RunAsync(ff, ed, mp4, new Progress<double>(), CancellationToken.None, null, null, new ExportSettings { Quality = 14, Audio = "none" });
            var mid = Frame(mp4, 45);
            int red = 0; for (int i = 0; i < mid.Length; i += 3) if (mid[i + 2] > 180 && mid[i + 1] < 60 && mid[i] < 60) red++;
            Check(err == null && red < 20, $"the export shows no box ({red} red pixels at 1.5 s)" + (err == null ? "" : ": " + err));
        }
    }

    static byte[] Frame(string file, int k)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-i", file, "-vf", $"select=eq(n\\,{k}),scale={W}:{H}", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var ms = new MemoryStream(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        var b = ms.ToArray();
        if (b.Length < W * H * 3) throw new Exception($"frame {k} of {Path.GetFileName(file)} could not be read");
        return b;
    }
    static byte[] Load(string png)
    {
        var dec = new PngBitmapDecoder(new Uri(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bgr = new FormatConvertedBitmap(dec.Frames[0], System.Windows.Media.PixelFormats.Bgr24, null, 0);
        var px = new byte[W * H * 3]; bgr.CopyPixels(px, W * 3, 0);
        return px;
    }
    static void SavePng(string path, byte[] bgr)
    {
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(W, H, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, bgr, W * 3)));
        using var fs = File.Create(path); enc.Save(fs);
    }
}
