using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// --stabtest &lt;dir&gt;: stabilization. A synthetic shaky shot (a 1280×720 window wandering over a larger photo: a slow pan plus fast jitter)
/// → the stabilized render's frame-to-frame jitter vs the original's (measured by block matching on small grey frames); the file covers
/// the clip's range on the sequence grid (Remap's layout); preview vs export of the stabilized clip (PSNR, alignment); a razor cut gives each
/// piece its own render; a clip with a speed change isn't stabilized; tripod mode nearly stops the motion; edges shown vs zoomed in.
/// </summary>
public static class StabTest
{
    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
        Stab.Background = false;
        try
        {
            var land = @"C:\Windows\Web\Wallpaper\ThemeC\img29.jpg";
            var big = Path.Combine(dir, "big.png");
            if (!File.Exists(big)) Run(ff, File.Exists(land) ? $"-y -v error -i \"{land}\" -vf scale=1600:900:force_original_aspect_ratio=increase,crop=1600:900 -frames:v 1 \"{big}\""
                                                           : $"-y -v error -f lavfi -i testsrc2=s=1600x900 -frames:v 1 \"{big}\"");
            var shaky = Path.Combine(dir, "shaky.mp4");
            // the window: a slow pan to the right plus jitter (three incommensurate frequencies per axis)
            string X = "100+60*t+28*sin(2*PI*4.7*t+1)+14*sin(2*PI*9.1*t)", Y = "90+22*sin(2*PI*5.3*t+2)+12*sin(2*PI*11.3*t)";
            if (!File.Exists(shaky)) Run(ff, $"-y -v error -loop 1 -framerate 30 -i \"{big}\" -t 5 -vf \"crop=1280:720:'{X}':'{Y}',format=yuv420p\" -c:v libx264 -crf 12 -r 30 \"{shaky}\"");
            var m = await MediaProbe.ProbeAsync(shaky) ?? throw new Exception("import");
            var ed = new Editor(); ed.Media.Add(m);
            var c = new Clip { MediaId = m.Id, Kind = ClipKind.Video, Track = 2, Start = 0, In = 0.5, Dur = 3, NoAudio = true, Stab = new Stabilize { Smooth = 60 } };
            ed.Clips.Add(c);
            Check(Stab.Active(c) && Stab.Active(new Clip { Kind = ClipKind.Video, Speed = 50, Dur = 1, Stab = new Stabilize() }) && !Stab.Active(new Clip { Kind = ClipKind.Audio, Stab = new Stabilize() }),
                  "stabilization applies to video clips, speed changes included");

            // render (what the export and the preview read)
            var sw = Stopwatch.StartNew();
            var files = Stab.PrepareExport(ed, CancellationToken.None);
            double secs = sw.Elapsed.TotalSeconds;
            Check(files.TryGetValue(c.Id, out var sf) && File.Exists(sf), $"stabilized render: {secs:0.0} s for a {c.Dur:0} s clip at 1280×720" + (sf != null ? $" ({new FileInfo(sf).Length / 1e6:0} MB)" : ""));
            if (sf == null) throw new Exception("no render");
            int frames = FrameCount(ff, sf);
            Check(frames == (int)Math.Round(c.Dur * 30), $"the render covers the clip's range on the sequence grid: {frames} frames (expected {Math.Round(c.Dur * 30)})");

            // jitter: frame-to-frame shifts of the original range vs the render, minus their smooth part (the pan)
            var orig = Grey(ff, shaky, c.In, c.Dur); var stab = Grey(ff, sf, 0, c.Dur);
            var (oj, op) = Jitter(orig); var (sj, sp) = Jitter(stab);
            Check(sj < oj * 0.35 && sp > op * 0.5, $"jitter (frame-to-frame shake around the smooth path): {oj:0.00} px → {sj:0.00} px ({100 * sj / Math.Max(1e-9, oj):0} %); the pan is kept ({op:0.0} → {sp:0.0} px/frame)");

            // tripod (a handheld shot of something still: jitter and a slow drift): the motion nearly stops
            var held = Path.Combine(dir, "held.mp4");
            string HX = "160+20*sin(2*PI*0.4*t)+18*sin(2*PI*4.7*t+1)+9*sin(2*PI*9.1*t)", HY = "90+12*sin(2*PI*0.3*t)+14*sin(2*PI*5.3*t+2)+8*sin(2*PI*11.3*t)";
            if (!File.Exists(held)) Run(ff, $"-y -v error -loop 1 -framerate 30 -i \"{big}\" -t 4 -vf \"crop=1280:720:'{HX}':'{HY}',format=yuv420p\" -c:v libx264 -crf 12 -r 30 \"{held}\"");
            var mh = await MediaProbe.ProbeAsync(held) ?? throw new Exception("import held");
            ed.Media.Add(mh);
            var ct = new Clip { MediaId = mh.Id, Kind = ClipKind.Video, Track = 2, Start = 10, In = 0.3, Dur = 3, NoAudio = true, Stab = new Stabilize { Tripod = true } };
            ed.Clips.Add(ct);
            var tf = Stab.PrepareExport(ed, CancellationToken.None)[ct.Id];
            var (hj, _) = Jitter(Grey(ff, held, ct.In, ct.Dur));
            var hframes = Grey(ff, tf, 0, ct.Dur);
            var (tj, _) = Jitter(hframes);
            var (dfx, dfy) = Match(hframes[0], hframes[^1]);                     // (first to last frame: no drift)
            double drift = Math.Sqrt(dfx * dfx + dfy * dfy) * 4;
            Check(tj < hj * 0.35 && drift < 6, $"tripod: jitter {hj:0.00} → {tj:0.00} px, first → last frame {drift:0.0} px (the shot drifts ±20 px)");
            ed.Clips.Remove(ct);

            // preview vs export of the stabilized clip
            var mp4 = Path.Combine(dir, "stab.mp4");
            var err = await Exporter.RunAsync(ff, ed, mp4, new Progress<double>(), CancellationToken.None, null, null, new ExportSettings { Quality = 10, Audio = "none" });
            Check(err == null, "export of the stabilized clip" + (err == null ? "" : ": " + err));
            if (err == null)
            {
                var pv = new Preview { Ed = ed, Width = Editor.W, Height = Editor.H, Background = Brushes.Black };
                foreach (double t1 in new[] { 0.2, 1.5, 2.8 })
                {
                    double t = Math.Round(t1 * 30) / 30;
                    var swp = Stopwatch.StartNew();
                    do { pv.Sync(t, false); await Task.Delay(20); } while ((!pv.Settled(t) || swp.ElapsedMilliseconds < 400) && swp.ElapsedMilliseconds < 8000);
                    pv.Sync(t, false);
                    pv.Measure(new Size(Editor.W, Editor.H)); pv.Arrange(new Rect(0, 0, Editor.W, Editor.H)); pv.UpdateLayout();
                    var rtb = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
                    var pa = new byte[Editor.W * Editor.H * 4]; rtb.CopyPixels(pa, Editor.W * 4, 0);
                    var pb = FxTest.Frame(ff, mp4, t - 0.001);
                    var (psnr, worst) = FxTest.Compare(pa, pb);
                    var (sx, sy) = SeqTest.Shift(pa, pb, Editor.W, Editor.H);
                    // and the preview isn't the unstabilized frame
                    var po = FxTest.Frame(ff, shaky, c.In + t - 0.001);
                    var (psnrO, _) = FxTest.Compare(pa, po);
                    Check(psnr >= 35 && sx == 0 && sy == 0 && psnrO < psnr - 6, $"preview vs export at {t:0.00} s: {psnr:0.0} dB, worst block {worst:0.0}, shift ({sx},{sy}); vs the shaky original {psnrO:0.0} dB");
                    FxTest.Save(FxTest.SideBySide(pa, pb), Path.Combine(dir, $"stab_{t:0.00}.png"));
                }
            }

            // speed changes: 50 % (frames repeat, in order) and reversed (out of order: a stabilized segment, then Remap's frame picking) —
            // jitter vs the same clip's unstabilized speed render, the reversed pan runs the other way, the render's length
            foreach (var (label, speed, rev) in new[] { ("50 %", 50.0, false), ("reversed", 100.0, true) })
            {
                var es = new Editor(); es.Media.Add(m);
                var cs = new Clip { MediaId = m.Id, Kind = ClipKind.Video, Track = 2, Start = 0, In = rev ? 3.5 : 0.5, Dur = 3, NoAudio = true, Speed = speed, Reverse = rev, Stab = new Stabilize { Smooth = 60 } };
                es.Clips.Add(cs);
                var swr = Stopwatch.StartNew();
                var sfile = Stab.PrepareExport(es, CancellationToken.None)[cs.Id];
                double rsecs = swr.Elapsed.TotalSeconds;
                cs.Stab = null;
                var plainFile = Remap.PrepareExport(es, CancellationToken.None)[cs.Id];
                var (sjit, _) = Jitter(Grey(ff, sfile, 0, cs.Dur)); var (pjit, _) = Jitter(Grey(ff, plainFile, 0, cs.Dur));
                var fs = Grey(ff, sfile, 0, cs.Dur);
                var (dxs, _) = Match(fs[2], fs[^3]);                                  // (the camera pan over the clip: positive forwards, negative reversed)
                int nfr = FrameCount(ff, sfile);
                Check(sjit < pjit * 0.35 && nfr == (int)Math.Round(cs.Dur * 30) && (rev ? dxs < -2 : dxs > 2),
                      $"stabilized {label}: jitter {pjit:0.00} → {sjit:0.00} px vs its unstabilized speed render, {nfr} frames, pan over the clip {dxs * 4:0} px ({(rev ? "reversed: the other way" : "forwards")}), {rsecs:0.0} s");
                cs.Stab = new Stabilize { Smooth = 60 };
            }

            // a razor cut: each piece its own render; the edges setting changes the render
            var e2 = new Editor(); e2.Media.Add(m);
            var c2 = new Clip { MediaId = m.Id, Kind = ClipKind.Video, Track = 2, Start = 0, In = 0.5, Dur = 3, NoAudio = true, Stab = new Stabilize { Smooth = 60 } };
            e2.Clips.Add(c2);
            e2.CutClips(new[] { c2 }, 1.5);
            var pieces = e2.Clips.OrderBy(x => x.Start).ToList();
            var pf = Stab.PrepareExport(e2, CancellationToken.None);
            Check(pieces.Count == 2 && pieces.All(p => p.Stab != null) && pf.Count == 2 && pf[pieces[0].Id] != pf[pieces[1].Id] && pf.Values.All(File.Exists),
                  "razor cut: both pieces stay stabilized, each with its own render");
            var p0 = pieces[0]; var before = pf[p0.Id];
            p0.Stab!.Edges = 2;
            var showEdges = Stab.PrepareExport(e2, CancellationToken.None)[p0.Id];
            double Black(string file) { var fr = Grey(ff, file, 0, p0.Dur); return fr.Count == 0 ? 0 : fr.Average(b => b.Count(v => v < 6) / (double)b.Length); }
            double black = Black(showEdges), zoomed = Black(before);
            Check(showEdges != before && black > zoomed + 0.005, $"show edges: a new render with black borders ({black:P1} of the frames black, zoomed in {zoomed:P1})");
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        finally { Stab.Background = true; }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "stabtest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    const int GW = 320, GH = 180;
    /// <summary>Grey 320×180 frames of [t0, t0 + dur) at 30 fps.</summary>
    static List<byte[]> Grey(string ff, string file, double t0, double dur)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-v", "error", "-ss", t0.ToString("0.####", CultureInfo.InvariantCulture), "-i", file, "-t", dur.ToString("0.####", CultureInfo.InvariantCulture),
                                  "-vf", $"fps=30,scale={GW}:{GH},format=gray", "-f", "rawvideo", "pipe:1" }) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        var list = new List<byte[]>(); var s = p.StandardOutput.BaseStream;
        while (true)
        {
            var b = new byte[GW * GH]; int got = 0;
            while (got < b.Length) { int n = s.Read(b, got, b.Length - got); if (n <= 0) break; got += n; }
            if (got < b.Length) break;
            list.Add(b);
        }
        p.WaitForExit();
        return list;
    }

    /// <summary>Frame-to-frame shifts (block matching on the centre, ±12 px at 1/4 scale, sub-pixel by a parabola through the SADs); returns
    /// (RMS of the shifts minus their 9-frame moving average = jitter, |mean shift| = the smooth motion), in full-size pixels.</summary>
    static (double jitter, double pan) Jitter(List<byte[]> f)
    {
        var dx = new List<double>(); var dy = new List<double>();
        for (int i = 1; i < f.Count; i++) { var (x, y) = Match(f[i - 1], f[i]); dx.Add(x * 4); dy.Add(y * 4); }
        if (dx.Count < 10) return (double.NaN, double.NaN);
        double J(List<double> v)
        {
            double s = 0; int n = 0;
            for (int i = 4; i < v.Count - 4; i++) { double avg = v.Skip(i - 4).Take(9).Average(); s += (v[i] - avg) * (v[i] - avg); n++; }
            return Math.Sqrt(s / Math.Max(1, n));
        }
        return (Math.Sqrt(J(dx) * J(dx) + J(dy) * J(dy)), Math.Sqrt(Math.Pow(dx.Average(), 2) + Math.Pow(dy.Average(), 2)));
    }
    static long Sad(byte[] a, byte[] b, int sx, int sy, long stop = long.MaxValue)
    {
        const int x0 = 60, y0 = 40, w = GW - 120, h = GH - 80;
        long sad = 0;
        for (int y = y0; y < y0 + h && sad < stop; y += 2)
        {
            int ia = y * GW, ib = (y + sy) * GW + sx;
            for (int x = x0; x < x0 + w; x += 2) sad += Math.Abs(a[ia + x] - b[ib + x]);
        }
        return sad;
    }
    static (double x, double y) Match(byte[] a, byte[] b)
    {
        const int R = 12;
        long best = long.MaxValue; int bx = 0, by = 0;
        for (int sy = -R; sy <= R; sy++)
            for (int sx = -R; sx <= R; sx++)
            {
                long sad = Sad(a, b, sx, sy, best);
                if (sad < best) { best = sad; bx = sx; by = sy; }
            }
        double Sub(long m, long c, long p) { double d = m - 2.0 * c + p; return d > 0 ? Math.Clamp((m - p) / (2 * d), -0.5, 0.5) : 0; }
        double fx = Math.Abs(bx) < R ? Sub(Sad(a, b, bx - 1, by), best, Sad(a, b, bx + 1, by)) : 0;
        double fy = Math.Abs(by) < R ? Sub(Sad(a, b, bx, by - 1), best, Sad(a, b, bx, by + 1)) : 0;
        // b = a shifted by the window's motion: content moves opposite to the window
        return (-(bx + fx), -(by + fy));
    }

    static int FrameCount(string ff, string file)
    {
        var probe = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe");
        var psi = new ProcessStartInfo(File.Exists(probe) ? probe : "ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", file }) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        return int.TryParse(o.Trim(), out var n) ? n : -1;
    }

    static void Run(string ff, string args) { using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!; p.StandardError.ReadToEnd(); p.WaitForExit(); }
}
