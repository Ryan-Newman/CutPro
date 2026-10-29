using System.Diagnostics;
using System.Windows;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CutPro;

/// <summary>
/// AI object selection and mask tracking.
/// Select: grab the exact frame (FrameStream, same frame grid as preview/export), MobileSAM embedding (cached per frame),
/// decode the click prompt → outline polygons. Track (AI): frame by frame, prompt SAM with the previous outline's box
/// (shifted by the object's motion), its centre point and its low-res mask; stop if the object is lost.
/// Track (drawn shapes): translation by template matching (SSD) on greyscale frames, like Premiere's position mask tracking.
/// </summary>
public static class ObjectTracker
{
    static SamEngine? sam;
    static readonly SemaphoreSlim gate = new(1, 1);          // one SAM job at a time
    static (string path, int frame, int w, int h, DenseTensor<float> emb)? cache;
    public static string? Provider => sam?.Provider;

    /// <summary>SAM working size: longest side 1024, display aspect (rotation applied), even.</summary>
    public static (int w, int h) WorkSize(MediaItem m, int longSide = SamEngine.Side)
    {
        double mw = Math.Max(2, m.Width), mh = Math.Max(2, m.Height), f = longSide / Math.Max(mw, mh);
        return (Math.Max(2, (int)Math.Round(mw * f / 2) * 2), Math.Max(2, (int)Math.Round(mh * f / 2) * 2));
    }

    /// <summary>Frame `frame` (30 fps index) at w×h, BGR32 — or the last frame before it when the video stream ends
    /// earlier (containers are often a few ms longer than their video, so a clip's last frame can lie past the final
    /// picture; the preview holds that picture, and so do we). Images: the picture.</summary>
    /// <summary>The file and time a source time of m decodes from: the media, or a nested sequence's render (CPU decoder).</summary>
    static (string path, double t, Decoder dec) Src(MediaItem m, double t, Decoder dec)
    {
        if (m.Seq == null) return (m.Path, t, dec);
        var s = Nest.SourceOf(m, t) ?? throw new Exception("The nested sequence isn't rendered there yet: play over it once, then try again.");
        return (s.path, s.t, Decoder.SwFrame);
    }
    /// <summary>What the embedding cache is keyed by (a nested sequence: the render the frame comes from).</summary>
    static string CacheKey(MediaItem m, int frame) => m.Seq == null ? m.Path : Nest.SourceOf(m, frame / (double)TimeUtil.Fps)?.path ?? m.Seq;

    static byte[] Grab(MediaItem m, int frame, int w, int h, CancellationToken ct = default)
    {
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        double t0 = m.Type == ClipKind.Image ? 0 : frame / (double)TimeUtil.Fps;
        var (path, t, _) = Src(m, t0, m.SeekDecoder);
        // exact frame; else decode from a little earlier and keep the newest; last resort: CPU decoder
        foreach (var (back, dec0) in new[] { (0.0, m.SeekDecoder), (1.5, m.SeekDecoder), (4.0, Decoder.SwFrame) })
        {
            var dec = m.Seq != null ? Decoder.SwFrame : dec0;
            if (back > 0 && t == 0 && dec0 == m.SeekDecoder) continue;
            using var fs = new FrameStream(ff, path, Math.Max(0, t - back), w, h, 2, 0, 0, dec, vf: path == m.Path ? Hdr.Of(m) : null);   // (HDR: as the preview shows it)
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 20000)
            {
                ct.ThrowIfCancellationRequested();
                bool ended = fs.Ended;                               // read before Take: a frame taken after this is final
                var (idx, buf) = fs.Take(t);
                if (buf != null && (idx >= fs.Index(t) || ended)) return (byte[])buf.Clone();
                if (ended) break;
                Thread.Sleep(3);
            }
        }
        throw new Exception($"ffmpeg could not decode {m.Name} at {TimeUtil.Format(t0)}");
    }

    static SamEngine Sam() => sam ??= new SamEngine();

    /// <summary>Click prompt → outline (normalised polygons). Points normalised 0..1; label 1 = object, 0 = background.</summary>
    public static async Task<List<float[]>> SelectAsync(MediaItem m, int frame, IList<(float x, float y, float label)> pts, Rect? box = null)
    {
        await gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                var (w, h) = WorkSize(m);
                if (m.Type == ClipKind.Image) frame = 0;
                var ck = CacheKey(m, frame);
                if (cache is not { } c || c.path != ck || c.frame != frame || c.w != w)
                {
                    var px = Grab(m, frame, w, h);
                    cache = (ck, frame, w, h, Sam().Encode(px, w, h));
                }
                var prompt = pts.Select(p => (p.x * w, p.y * h, p.label)).ToList();
                if (box is { } bx) { prompt.Add(((float)(bx.Left * w), (float)(bx.Top * h), 2f)); prompt.Add(((float)(bx.Right * w), (float)(bx.Bottom * h), 3f)); }
                var (mask, _, _) = Sam().Decode(cache.Value.emb, prompt, null, w, h);
                return MaskGeometry.FromMask(mask, w, h);
            });
        }
        finally { gate.Release(); }
    }

    public static double Area(List<float[]> polys)
    {
        double a = 0;
        foreach (var p in polys)
        {
            double s = 0; int n = p.Length / 2;
            for (int i = 0; i < n; i++) { int j = (i + 1) % n; s += p[i * 2] * p[j * 2 + 1] - p[j * 2] * p[i * 2 + 1]; }
            a += Math.Abs(s) / 2;
        }
        return a;
    }

    public static bool Inside(List<float[]> polys, double x, double y)
    {
        bool inside = false;
        foreach (var p in polys)
        {
            int n = p.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
                if ((p[i * 2 + 1] > y) != (p[j * 2 + 1] > y) &&
                    x < (p[j * 2] - p[i * 2]) * (y - p[i * 2 + 1]) / (p[j * 2 + 1] - p[i * 2 + 1] + 1e-12) + p[i * 2]) inside = !inside;
        }
        return inside;
    }

    static (double x, double y) Centroid(List<float[]> polys)
    {
        double sx = 0, sy = 0, n = 0;
        foreach (var p in polys) for (int i = 0; i + 1 < p.Length; i += 2) { sx += p[i]; sy += p[i + 1]; n++; }
        return n == 0 ? (0.5, 0.5) : (sx / n, sy / n);
    }

    /// <summary>
    /// Tracks `mask` from source frame `from` (its outline there is the start) to `to` (inclusive, either direction).
    /// Each new outline is stored on the UI thread via `store`. Returns null when done, or why it stopped early.
    /// </summary>
    public static async Task<string?> TrackAsync(MediaItem m, ObjectMask mask, List<float[]> start, int from, int to,
        Action<int, List<float[]>> store, IProgress<(int done, int total)> progress, CancellationToken ct,
        IReadOnlyDictionary<int, List<float[]>>? keys = null)
    {
        if (from == to) return null;
        if (mask.Kind == MaskKind.AI) await gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => mask.Kind == MaskKind.AI
                ? TrackAi(m, start, from, to, store, progress, ct, keys)
                : TrackShape(m, start, from, to, store, progress, ct, keys), ct);
        }
        finally { if (mask.Kind == MaskKind.AI) gate.Release(); }
    }

    /// <summary>Frames from `from` (exclusive) towards `to` (inclusive), in visiting order. Backwards runs decode forward in
    /// chunks and hand the chunk's frames out in reverse.</summary>
    static IEnumerable<(int frame, byte[] px)> Frames(MediaItem m, int from, int to, int w, int h, CancellationToken ct)
    {
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        int dir = Math.Sign(to - from);
        if (dir > 0)
        {
            var (fp, ft, fd) = Src(m, (from + 1) / (double)TimeUtil.Fps, m.PlayDecoder);
            using var fs = new FrameStream(ff, fp, ft, w, h, 8, 0, 0, fd, vf: fp == m.Path ? Hdr.Of(m) : null);
            for (int k = 0; from + 1 + k <= to; k++)
            {
                var px = Wait(fs, k, ct);
                if (px == null) yield break;
                yield return (from + 1 + k, px);
            }
            yield break;
        }
        for (int end = from - 1; end >= to; end -= 30)
        {
            int begin = Math.Max(to, end - 29);
            var chunk = new List<byte[]>();
            var (bp, bt, bd) = Src(m, begin / (double)TimeUtil.Fps, m.PlayDecoder);
            using (var fs = new FrameStream(ff, bp, bt, w, h, 8, 0, 0, bd, vf: bp == m.Path ? Hdr.Of(m) : null))
                for (int k = 0; begin + k <= end; k++) { var px = Wait(fs, k, ct); if (px == null) break; chunk.Add(px); }
            for (int k = chunk.Count - 1; k >= 0; k--) yield return (begin + k, chunk[k]);
        }
    }

    static byte[]? Wait(FrameStream fs, int k, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        double src = fs.Start + k / (double)TimeUtil.Fps;
        while (sw.ElapsedMilliseconds < 15000)
        {
            ct.ThrowIfCancellationRequested();
            var (idx, buf) = fs.Take(src);
            if (buf != null && idx == k) return (byte[])buf.Clone();
            if (buf != null && idx > k) return null;
            if (fs.Ended && buf == null) return null;          // end of media
            Thread.Sleep(1);
        }
        return null;                                       // end of media
    }

    /// <summary>AI tracking: per frame, SAM with a box around the previous outline (moved by the velocity), its centroid as
    /// a positive point and the previous low-res mask. Keyframes win: the tracker takes the user's outline there and continues
    /// from it; if the object is lost and a keyframe lies ahead, frames up to it are skipped instead of giving up.</summary>
    static string? TrackAi(MediaItem m, List<float[]> start, int from, int to, Action<int, List<float[]>> store,
                           IProgress<(int, int)> progress, CancellationToken ct, IReadOnlyDictionary<int, List<float[]>>? keys)
    {
        var (w, h) = WorkSize(m);
        int total = Math.Abs(to - from), done = 0, dir = Math.Sign(to - from);
        var prev = start;
        double prevArea = Area(prev);
        float[]? prevLow = null;
        (double x, double y) vel = (0, 0), prevC = Centroid(prev);
        string? lostMsg = null; int lostAt = -1;
        bool KeyAhead(int f) => keys != null && keys.Keys.Any(k => dir > 0 ? k > f && k <= to : k < f && k >= to);
        foreach (var (f, px) in Frames(m, from, to, w, h, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (keys != null && keys.TryGetValue(f, out var key))
            {
                if (lostAt >= 0) { lostMsg = $"lost the object at frames {Math.Min(lostAt, f - dir)}–{Math.Max(lostAt, f - dir)}; continued from the keyframe at {f}"; lostAt = -1; }
                var kc = Centroid(key);
                if (lostMsg == null) vel = ((vel.x + kc.x - prevC.x) / 2, (vel.y + kc.y - prevC.y) / 2); else vel = (0, 0);
                prevC = kc; prev = key; prevArea = Area(key); prevLow = null;
                progress.Report((++done, total));
                continue;
            }
            if (lostAt >= 0) { progress.Report((++done, total)); continue; }      // skipping to the next keyframe
            var emb = Sam().Encode(px, w, h);
            var b = MaskGeometry.Bounds(prev);
            double mx = b.Width * 0.12 + 6.0 / w, my = b.Height * 0.12 + 6.0 / h;
            double x0 = Math.Clamp(b.Left + vel.x - mx, 0, 1), y0 = Math.Clamp(b.Top + vel.y - my, 0, 1);
            double x1 = Math.Clamp(b.Right + vel.x + mx, 0, 1), y1 = Math.Clamp(b.Bottom + vel.y + my, 0, 1);
            var pts = new List<(float, float, float)> { ((float)(x0 * w), (float)(y0 * h), 2), ((float)(x1 * w), (float)(y1 * h), 3) };
            var c = (x: prevC.x + vel.x, y: prevC.y + vel.y);
            if (Inside(prev, prevC.x, prevC.y)) pts.Insert(0, ((float)(c.x * w), (float)(c.y * h), 1));
            var (mask, low, _) = Sam().Decode(emb, pts, prevLow, w, h);
            var polys = MaskGeometry.FromMask(mask, w, h);
            double area = Area(polys);
            string? lost = polys.Count == 0 || area < prevArea * 0.2 ? $"lost the object at frame {f} (it left the frame, or something covered it)"
                         : area > prevArea * 4 && prevArea > 0.0005 ? $"lost the object at frame {f} (the selection grew into the background)" : null;
            if (lost != null)
            {
                if (!KeyAhead(f)) return lost;
                lostAt = f; progress.Report((++done, total)); continue;
            }
            var nc = Centroid(polys);
            vel = ((vel.x + nc.x - prevC.x) / 2, (vel.y + nc.y - prevC.y) / 2);
            prevC = nc; prev = polys; prevArea = area; prevLow = low;
            store(f, polys);
            progress.Report((++done, total));
        }
        return lostMsg;
    }

    /// <summary>
    /// Drawn-shape tracking (translation, like Premiere's "Position" mask tracking): the pixels inside the shape are matched
    /// in each new frame by zero-mean SSD against both the previous frame (follows appearance changes) and the start frame
    /// (stops drift), searching around a velocity prediction, with sub-pixel refinement. At a keyframe the tracker restarts
    /// from the user's outline (shape and template).
    /// </summary>
    static string? TrackShape(MediaItem m, List<float[]> start, int from, int to, Action<int, List<float[]>> store,
                              IProgress<(int, int)> progress, CancellationToken ct, IReadOnlyDictionary<int, List<float[]>>? keys)
    {
        var (w, h) = WorkSize(m, 640);
        int total = Math.Abs(to - from), done = 0;
        var shape = start;
        var pts = new List<(int x, int y)>();
        int[] t0 = Array.Empty<int>(), tp = t0;
        int ox = 0, oy = 0, reach = 8;
        double px = 0, py = 0, vx = 0, vy = 0;                       // shape corner (work pixels) and velocity
        // (re)start from an outline: template points = shape interior on a grid (≤ ~1500 points) relative to its corner
        bool Init(List<float[]> s, byte[] g)
        {
            var b0 = MaskGeometry.Bounds(s);
            int nx = (int)Math.Floor(b0.Left * w), ny = (int)Math.Floor(b0.Top * h);
            var inside = MaskGeometry.Raster(s, w, h);
            int step = Math.Max(1, (int)Math.Sqrt(b0.Width * w * b0.Height * h / 1500));
            var np = new List<(int x, int y)>();
            for (int y = Math.Max(0, ny); y < Math.Min(h, ny + (int)Math.Ceiling(b0.Height * h) + 1); y += step)
                for (int x = Math.Max(0, nx); x < Math.Min(w, nx + (int)Math.Ceiling(b0.Width * w) + 1); x += step)
                    if (inside[y * w + x]) np.Add((x - nx, y - ny));
            if (np.Count < 8) return false;
            shape = s; pts = np; ox = nx; oy = ny; px = ox; py = oy;
            t0 = pts.Select(p => (int)g[(oy + p.y) * w + ox + p.x]).ToArray(); tp = (int[])t0.Clone();
            reach = Math.Clamp((int)(Math.Max(b0.Width * w, b0.Height * h) * 0.35), 8, 48);
            return true;
        }
        if (!Init(start, Gray(Grab(m, from, w, h, ct), w, h))) return "the shape is too small to track";
        foreach (var (f, frame) in Frames(m, from, to, w, h, ct))
        {
            ct.ThrowIfCancellationRequested();
            var g = Gray(frame, w, h);
            if (keys != null && keys.TryGetValue(f, out var key) && Init(key, g)) { progress.Report((++done, total)); continue; }
            // zero-mean SSD of template t at integer corner (cx, cy); points outside the frame are skipped
            double Cost(int[] t, int cx, int cy)
            {
                double s = 0, s2 = 0; int n = 0;
                for (int i = 0; i < pts.Count; i++)
                {
                    int x = cx + pts[i].x, y = cy + pts[i].y;
                    if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
                    double d = g[y * w + x] - t[i]; s += d; s2 += d * d; n++;
                }
                return n < pts.Count / 3 ? double.MaxValue : (s2 - s * s / n) / n;
            }
            double Both(int cx, int cy) { double a = Cost(tp, cx, cy), b = Cost(t0, cx, cy); return a == double.MaxValue || b == double.MaxValue ? double.MaxValue : 0.6 * a + 0.4 * b; }
            int ex = (int)Math.Round(px + vx), ey = (int)Math.Round(py + vy);
            // coarse (step 2) search on the previous-frame template, then ±2 on the blended cost
            (int x, int y) best = (ex, ey); double bs = Cost(tp, ex, ey);
            for (int dy = -reach; dy <= reach; dy += 2)
                for (int dx = -reach; dx <= reach; dx += 2)
                { double c = Cost(tp, ex + dx, ey + dy); if (c < bs) { bs = c; best = (ex + dx, ey + dy); } }
            var fine = best; double fs = Both(best.x, best.y);
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                { double c = Both(best.x + dx, best.y + dy); if (c < fs) { fs = c; fine = (best.x + dx, best.y + dy); } }
            // sub-pixel: parabola through the neighbours
            static double Sub(double l, double c, double r) { double d = l - 2 * c + r; return d > 1e-9 && l < double.MaxValue && r < double.MaxValue ? Math.Clamp(0.5 * (l - r) / d, -0.5, 0.5) : 0; }
            double nx = fine.x + Sub(Both(fine.x - 1, fine.y), fs, Both(fine.x + 1, fine.y));
            double ny = fine.y + Sub(Both(fine.x, fine.y - 1), fs, Both(fine.x, fine.y + 1));
            vx = 0.5 * vx + 0.5 * (nx - px); vy = 0.5 * vy + 0.5 * (ny - py);
            px = nx; py = ny;
            for (int i = 0; i < pts.Count; i++)
            {
                int x = fine.x + pts[i].x, y = fine.y + pts[i].y;
                if ((uint)x < (uint)w && (uint)y < (uint)h) tp[i] = g[y * w + x];
            }
            float dxn = (float)((px - ox) / w), dyn = (float)((py - oy) / h);
            store(f, shape.Select(p => MaskGeometry.Translate(p, dxn, dyn)).ToList());
            progress.Report((++done, total));
        }
        return null;
    }

    static byte[] Gray(byte[] bgr, int w, int h)
    {
        var g = new byte[w * h];
        for (int i = 0; i < g.Length; i++) g[i] = (byte)((bgr[i * 4 + 2] * 77 + bgr[i * 4 + 1] * 150 + bgr[i * 4] * 29) >> 8);
        return g;
    }

    public static void ResetCache() => cache = null;
}
