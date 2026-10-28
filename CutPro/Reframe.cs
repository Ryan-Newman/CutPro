using System.Diagnostics;

namespace CutPro;

/// <summary>
/// Auto reframe for Shorts (Fill layout): keeps the subject of each cropped V1 clip in frame by keyframing its Pan.
/// The subject is the clip's tracked object mask when it has one (its centre per frame), else a saliency estimate from frames
/// sampled every half second: edges, skin-tone areas and motion, summed per column, best crop window, smoothed so the virtual
/// camera moves calmly (like Premiere's Auto Reframe "slower motion" preset).
/// </summary>
public static class Reframe
{
    public sealed record Result(Clip C, List<(double src, double pan)> Keys, string How, List<(double src, double scale)>? Zoom = null);

    /// <summary>Pan (−100…100) that centres a subject at normalised media x (0..1) for a picture w wide in a frame W wide.</summary>
    public static double PanFor(double cx, double w, double frameW) => w <= frameW + 0.5 ? 0 : Math.Clamp(200 * w * (cx - 0.5) / (w - frameW), -100, 100);

    /// <summary>zoom (%) &gt; 0: also a slow push-in — Scale keys from the clip's scale at its start to scale·(1 + zoom/100) at its end (eased),
    /// the pan worked out for the picture's width at each moment (a clip that fills the frame exactly can then follow its subject too).</summary>
    public static async Task<Result?> AnalyzeAsync(Editor ed, Clip c, IProgress<double>? progress, CancellationToken ct, double zoom = 0)
    {
        var m = ed.MediaOf(c);
        if (m == null || m.Offline || !Editor.CoversFrame(c)) return null;
        var (_, _, w, _, _) = Editor.Placement(m, c, c.Start);
        if (w <= Editor.W + 1 && zoom <= 0) return null;                        // nothing is cropped
        var (span0, span1) = TimeMap.Span(c);
        List<(double src, double scale)>? zk = zoom > 0 ? new() { (span0, c.Scale), (Math.Max(span0, span1 - 1.0 / TimeUtil.Fps), Math.Round(c.Scale * (1 + zoom / 100), 2)) } : null;
        double WAt(double s) => zk == null ? w : w * (1 + zoom / 100 * Math.Clamp((s - span0) / Math.Max(1e-6, span1 - span0), 0, 1));
        double crop = Math.Clamp(Editor.W / WAt(span1), 0.05, 1);              // (the window the end of the clip shows)
        // 1. a tracked object mask: follow it
        foreach (var mk in c.Masks.Where(x => x.Enabled))
            if (ed.MaskTracks.TryGetValue(mk.Id, out var tr) && tr.Frames.Count > 2)
            {
                var pts = new List<(double src, double cx)>();
                var (lo0, hi0) = TimeMap.Span(c);                                // (speed / reverse: the source the clip shows)
                for (double s = lo0; s < hi0; s += 0.25)
                    if (tr.At(MaskGeometry.FrameOf(s)) is { } polys) pts.Add((s, MaskGeometry.Centroid(polys).x));
                if (pts.Count > 0) return new Result(c, Keys(pts, WAt), $"follows {mk.Name}", zk);
            }
        // 2. saliency over sampled frames
        if (m.Type == ClipKind.Image)
        {
            var one = await Task.Run(() => Saliency(m, new[] { 0.0 }, ct, null, crop), ct);
            return new Result(c, Keys(new() { (c.In, one[0]) }, WAt), "picture's subject", zk);
        }
        var times = new List<double>();
        var (lo, hi) = TimeMap.Span(c);
        for (double s = lo; s < hi - 0.05; s += 0.5) times.Add(s);
        if (times.Count == 0) times.Add(c.In);
        var cx = await Task.Run(() => Saliency(m, times, ct, progress, crop), ct);
        return new Result(c, Keys(times.Zip(cx).ToList(), WAt), "subject estimate", zk);
    }

    /// <summary>Smoothed path → a few keyframes (a static subject gets one).</summary>
    static List<(double src, double pan)> Keys(List<(double src, double cx)> pts, Func<double, double> wAt)
    {
        // median of 5 then moving average of 5: ignores single-sample jumps, then eases
        var med = new List<double>();
        for (int i = 0; i < pts.Count; i++)
        {
            var near = pts.Skip(Math.Max(0, i - 2)).Take(5).Select(q => q.cx).OrderBy(v => v).ToList();
            med.Add(near[near.Count / 2]);
        }
        var avg = med.Select((v, i) => med.Skip(Math.Max(0, i - 2)).Take(5).Average()).ToList();
        var pans = pts.Select((p, i) => (p.src, pan: PanFor(avg[i], wAt(p.src), Editor.W))).ToList();
        if (pans.Max(p => p.pan) - pans.Min(p => p.pan) < 6) return new() { (pts[0].src, Math.Round(pans.Average(p => p.pan), 1)) };
        // keep a key where the path bends (or every 2 s), drop the rest
        var keys = new List<(double, double)> { pans[0] };
        for (int i = 1; i < pans.Count - 1; i++)
        {
            var (s0, p0) = keys[^1];
            double lin = p0 + (pans[i + 1].pan - p0) * (pans[i].src - s0) / Math.Max(1e-6, pans[i + 1].src - s0);
            if (Math.Abs(lin - pans[i].pan) > 4 || pans[i].src - s0 >= 2) keys.Add((pans[i].src, Math.Round(pans[i].pan, 1)));
        }
        keys.Add((pans[^1].src, Math.Round(pans[^1].pan, 1)));
        return keys;
    }

    /// <summary>Subject centre (normalised x) per sampled source time: per-column saliency, best window of the crop width.</summary>
    static double[] Saliency(MediaItem m, IList<double> times, CancellationToken ct, IProgress<double>? progress = null, double? crop = null)
    {
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        int w = 192, h = Math.Max(2, (int)Math.Round(192.0 * m.Height / Math.Max(1, m.Width) / 2) * 2);
        double cropFrac = crop ?? Math.Clamp(Editor.W / (double)Editor.H / (m.Width / (double)Math.Max(1, m.Height)), 0.05, 1);
        int win = Math.Max(4, (int)Math.Round(w * cropFrac));
        var result = new double[times.Count];
        byte[]? prev = null;
        for (int i = 0; i < times.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var px = Grab(ff, m, times[i], w, h);
            var col = new double[w];
            if (px != null)
            {
                for (int y = 1; y < h - 1; y++)
                    for (int x = 1; x < w - 1; x++)
                    {
                        int k = (y * w + x) * 4;
                        double L(int o) => 0.299 * px[o + 2] + 0.587 * px[o + 1] + 0.114 * px[o];
                        double gx = L(k + 4) - L(k - 4), gy = L(k + w * 4) - L(k - w * 4);
                        double edge = Math.Sqrt(gx * gx + gy * gy) / 255;
                        int r = px[k + 2], g = px[k + 1], b = px[k];
                        bool skin = r > 95 && g > 40 && b > 20 && r > g && r > b && r - Math.Min(g, b) > 15 && Math.Abs(r - g) > 15;   // classic RGB skin rule
                        double motion = prev != null ? Math.Abs(L(k) - (0.299 * prev[k + 2] + 0.587 * prev[k + 1] + 0.114 * prev[k])) / 255 : 0;
                        double center = 1 - Math.Abs(y / (double)h - 0.45);         // subjects sit in the upper-middle band
                        col[x] += (edge * 0.6 + (skin ? 1.5 : 0) + motion * 2) * center;
                    }
                prev = px;
            }
            double best = double.MinValue; int bx = (w - win) / 2;
            double sum = col.Take(win).Sum();
            for (int x = 0; x + win <= w; x++)
            {
                if (x > 0) sum += col[x + win - 1] - col[x - 1];
                double bias = -Math.Abs((x + win / 2.0) / w - 0.5) * sum * 0.15;     // ties go to the centre
                if (sum + bias > best) { best = sum + bias; bx = x; }
            }
            result[i] = (bx + win / 2.0) / w;
            progress?.Report((i + 1) / (double)times.Count);
        }
        return result;
    }

    static byte[]? Grab(string ff, MediaItem m, double t, int w, int h)
    {
        if (Nest.SourceOf(m, m.Type == ClipKind.Image ? 0 : t) is not var (path, ft, _)) return null;   // (a nested sequence: its render)
        using var fs = new FrameStream(ff, path, ft, w, h, 2, 0, 0, m.Seq != null ? Decoder.SwFrame : m.SeekDecoder);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10000)
        {
            bool ended = fs.Ended;
            var (_, buf) = fs.Take(fs.Start);
            if (buf != null) return (byte[])buf.Clone();
            if (ended) return null;
            Thread.Sleep(3);
        }
        return null;
    }

    /// <summary>Writes the result as Pan keyframes (Auto Bézier) — one undo step for all clips.</summary>
    public static void Apply(Editor ed, IEnumerable<Result> results)
    {
        ed.Edit(() =>
        {
            foreach (var r in results)
            {
                if (r.Zoom is { Count: > 1 } z)
                {
                    Anim.Ensure(r.C, AnimProp.Scale).Keys.Clear();
                    foreach (var (src, sc) in z) Anim.ApplyInterp(Anim.SetKey(r.C, AnimProp.Scale, src, sc), "Easy Ease");
                }
                var tr = Anim.Ensure(r.C, AnimProp.Pan);
                tr.Keys.Clear();
                if (r.Keys.Count == 1) { r.C.Pan = r.Keys[0].pan; Anim.Prune(r.C); continue; }
                foreach (var (src, pan) in r.Keys) Anim.ApplyInterp(Anim.SetKey(r.C, AnimProp.Pan, src, pan), "Auto Bezier");
            }
        });
    }
}
