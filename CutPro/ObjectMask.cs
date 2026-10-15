using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace CutPro;

public enum MaskKind { AI, Ellipse, Rectangle, Freehand }
public enum MaskFx { Blur, Pixelate, Darken, Desaturate, Redact, Tint, CutOut }

/// <summary>A selected object/region on a video or image clip and the effect applied to it (or, inverted, to everything else).
/// The per-frame outline lives in <see cref="MaskTrack"/> (Editor.MaskTracks[Id]) so undo snapshots stay small.</summary>
public class ObjectMask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Object";
    public MaskKind Kind { get; set; }
    public MaskFx Effect { get; set; } = MaskFx.Blur;
    public double Amount { get; set; } = 25;          // blur radius / pixel size / darkness % / tint opacity %
    public double Feather { get; set; } = 6;          // edge softness, sequence px
    public double Expand { get; set; }                // grow (+) / shrink (−) the outline, sequence px
    public bool Invert { get; set; }                  // apply to everything except the object
    public bool Enabled { get; set; } = true;
    public string TintColor { get; set; } = "#FFFFD400";

    /// <summary>Premiere-style presets shown in the UI: (label, effect, invert).</summary>
    public static readonly (string label, MaskFx fx, bool invert)[] Presets =
    {
        ("Blur object", MaskFx.Blur, false), ("Pixelate object", MaskFx.Pixelate, false), ("Redact (black out)", MaskFx.Redact, false),
        ("Tint / highlight object", MaskFx.Tint, false), ("Focus on object (blur background)", MaskFx.Blur, true),
        ("Spotlight (darken background)", MaskFx.Darken, true), ("Color pop (grey background)", MaskFx.Desaturate, true),
        ("Cut out object (remove background)", MaskFx.CutOut, false), ("Remove object area (transparent)", MaskFx.CutOut, true),
    };
    [JsonIgnore] public int PresetIndex => Array.FindIndex(Presets, p => p.fx == Effect && p.invert == Invert);
}

/// <summary>Outline of a mask per source frame (30 fps index): polygons as [x0,y0,x1,y1,…] normalised 0..1 in the media's
/// display frame, so the mask follows the content through trims, moves, scale and position.
/// Keyframes (<see cref="Keys"/>) are outlines the user set — selection, drawing, AI refinement, manual correction. Tracking
/// never overwrites them and re-anchors on them; frames with no outline between two outlines are interpolated (so keyframes
/// alone animate a mask, like Premiere's mask path). Stored polygon arrays are never modified in place (edits store new
/// arrays), so undo snapshots can share them.</summary>
public class MaskTrack : IJsonOnDeserialized
{
    public SortedDictionary<int, List<float[]>> Frames { get; set; } = new();
    public SortedSet<int> Keys { get; set; } = new();
    /// <summary>AI prompt of the seed frame (for re-running): points normalised, label 1 = object, 0 = background.</summary>
    public List<float[]> SeedPoints { get; set; } = new();
    public int SeedFrame { get; set; }

    /// <summary>Bumped on every change (UI refresh, undo "did anything change").</summary>
    [JsonIgnore] public int Version { get; private set; }
    int[]? order;
    readonly Dictionary<int, List<float[]>> between = new();

    public void OnDeserialized() { if (Keys.Count == 0 && Frames.ContainsKey(SeedFrame)) Keys.Add(SeedFrame); }   // projects from before keyframes

    public void Set(int f, List<float[]> polys, bool key = false) { Frames[f] = polys; if (key) Keys.Add(f); Touch(); }
    public void Remove(int f) { Frames.Remove(f); Keys.Remove(f); Touch(); }
    /// <summary>Drops tracked frames, keeps keyframes.</summary>
    public void ClearTracked() { foreach (var f in Frames.Keys.Where(f => !Keys.Contains(f)).ToList()) Frames.Remove(f); Touch(); }
    public void Touch() { Version++; lock (between) { order = null; between.Clear(); } }

    public bool IsKey(int f) => Keys.Contains(f);
    public bool IsTracked(int f) => Frames.ContainsKey(f) && !Keys.Contains(f);
    public int? PrevKey(int f) { var v = Keys.GetViewBetween(int.MinValue, f - 1); return v.Count > 0 ? v.Max : null; }
    public int? NextKey(int f) { var v = Keys.GetViewBetween(f + 1, int.MaxValue); return v.Count > 0 ? v.Min : null; }

    /// <summary>Outline for a frame: the stored one; between two stored outlines an interpolation (cached, so the same
    /// frame returns the same object); before the first / after the last, that one (hold).</summary>
    public List<float[]>? At(int frame)
    {
        if (Frames.Count == 0) return null;
        if (Frames.TryGetValue(frame, out var p)) return p;
        lock (between)
        {
            var o = order ??= Frames.Keys.ToArray();
            int i = ~Array.BinarySearch(o, frame);             // first stored frame after `frame`
            if (i <= 0) return Frames[o[0]];
            if (i >= o.Length) return Frames[o[^1]];
            if (between.TryGetValue(frame, out var cached)) return cached;
            if (between.Count > 4000) between.Clear();
            int a = o[i - 1], b = o[i];
            return between[frame] = MaskGeometry.Lerp(Frames[a], Frames[b], (frame - a) / (double)(b - a));
        }
    }
    public (int first, int last) Range => Frames.Count == 0 ? (0, -1) : (Frames.Keys.First(), Frames.Keys.Last());

    /// <summary>Shallow snapshot for undo (polygon arrays are shared, never mutated).</summary>
    public sealed record State(SortedDictionary<int, List<float[]>> Frames, SortedSet<int> Keys, int SeedFrame);
    public State Save() => new(new SortedDictionary<int, List<float[]>>(Frames), new SortedSet<int>(Keys), SeedFrame);
    public void Load(State s) { Frames = new(s.Frames); Keys = new(s.Keys); SeedFrame = s.SeedFrame; Touch(); }
}

public static class MaskGeometry
{
    public static int FrameOf(double srcSeconds) => (int)Math.Round(srcSeconds * TimeUtil.Fps);

    /// <summary>Outline polygons scaled to a w×h clip image, with expand/shrink; null if empty.</summary>
    public static Geometry? Outline(List<float[]>? polys, double w, double h, double expand)
    {
        if (polys == null || polys.Count == 0) return null;
        var pg = new PathGeometry { FillRule = FillRule.Nonzero };
        foreach (var poly in polys)
        {
            if (poly.Length < 6) continue;
            var fig = new PathFigure { StartPoint = new Point(poly[0] * w, poly[1] * h), IsClosed = true, IsFilled = true };
            var seg = new PolyLineSegment();
            for (int i = 2; i + 1 < poly.Length; i += 2) seg.Points.Add(new Point(poly[i] * w, poly[i + 1] * h));
            fig.Segments.Add(seg);
            pg.Figures.Add(fig);
        }
        if (pg.Figures.Count == 0) return null;
        Geometry g = pg;
        if (Math.Abs(expand) > 0.05)
        {
            var widened = pg.GetWidenedPathGeometry(new Pen(Brushes.Black, Math.Abs(expand) * 2) { LineJoin = PenLineJoin.Round });
            g = expand > 0 ? new CombinedGeometry(GeometryCombineMode.Union, pg, widened) : new CombinedGeometry(GeometryCombineMode.Exclude, pg, widened);
        }
        g.Freeze();
        return g;
    }

    /// <summary>The area the effect covers (outline, or everything else when inverted) in a w×h clip image.</summary>
    public static Geometry? Area(Geometry? outline, double w, double h, bool invert, double feather)
    {
        if (!invert) return outline;
        double pad = 3 * feather + 2;   // keep the frame edges off-image so feathering doesn't darken the borders
        var full = new RectangleGeometry(new Rect(-pad, -pad, w + 2 * pad, h + 2 * pad));
        Geometry g = outline == null ? full : new CombinedGeometry(GeometryCombineMode.Exclude, full, outline);
        g.Freeze();
        return g;
    }

    /// <summary>Ellipse / rectangle as a polygon (normalised).</summary>
    public static float[] Shape(MaskKind kind, Rect r)
    {
        if (kind == MaskKind.Rectangle)
            return new[] { (float)r.Left, (float)r.Top, (float)r.Right, (float)r.Top, (float)r.Right, (float)r.Bottom, (float)r.Left, (float)r.Bottom };
        const int n = 72;
        var a = new float[n * 2];
        for (int i = 0; i < n; i++)
        {
            double t = 2 * Math.PI * i / n;
            a[i * 2] = (float)(r.X + r.Width / 2 + Math.Cos(t) * r.Width / 2);
            a[i * 2 + 1] = (float)(r.Y + r.Height / 2 + Math.Sin(t) * r.Height / 2);
        }
        return a;
    }

    public static float[] Translate(float[] poly, float dx, float dy)
    {
        var o = new float[poly.Length];
        for (int i = 0; i + 1 < poly.Length; i += 2) { o[i] = poly[i] + dx; o[i + 1] = poly[i + 1] + dy; }
        return o;
    }

    /// <summary>Even-odd scanline fill of normalised polygons at w×h.</summary>
    public static bool[] Raster(List<float[]>? polys, int w, int h)
    {
        var o = new bool[w * h];
        if (polys == null) return o;
        var xs = new List<double>();
        for (int y = 0; y < h; y++)
        {
            double py = (y + 0.5) / h; xs.Clear();
            foreach (var p in polys)
                for (int i = 0, n = p.Length / 2; i < n; i++)
                {
                    double x0 = p[i * 2], y0 = p[i * 2 + 1], x1 = p[(i + 1) % n * 2], y1 = p[(i + 1) % n * 2 + 1];
                    if ((y0 <= py) != (y1 <= py)) xs.Add(x0 + (py - y0) / (y1 - y0) * (x1 - x0));
                }
            xs.Sort();
            for (int j = 0; j + 1 < xs.Count; j += 2)
                for (int x = Math.Max(0, (int)Math.Ceiling(xs[j] * w - 0.5)); x < w && (x + 0.5) / w < xs[j + 1]; x++) o[y * w + x] = true;
        }
        return o;
    }

    // ---- keyframe interpolation ----
    public static double SignedArea(float[] p)
    {
        double a = 0; int n = p.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++) a += (double)p[j * 2] * p[i * 2 + 1] - (double)p[i * 2] * p[j * 2 + 1];
        return a / 2;
    }

    /// <summary>n points evenly spaced along the closed polygon, counter-clockwise (positive area).</summary>
    public static float[] Resample(float[] p, int n)
    {
        int m = p.Length / 2;
        if (SignedArea(p) < 0) p = Reverse(p);
        var cum = new double[m + 1];
        for (int i = 0; i < m; i++) { int j = (i + 1) % m; cum[i + 1] = cum[i] + Math.Sqrt(Sq(p[j * 2] - p[i * 2]) + Sq(p[j * 2 + 1] - p[i * 2 + 1])); }
        var o = new float[n * 2];
        double total = Math.Max(cum[m], 1e-9);
        for (int k = 0, s = 0; k < n; k++)
        {
            double d = total * k / n;
            while (s < m - 1 && cum[s + 1] < d) s++;
            int j = (s + 1) % m; double seg = cum[s + 1] - cum[s], t = seg > 1e-12 ? (d - cum[s]) / seg : 0;
            o[k * 2] = (float)(p[s * 2] + (p[j * 2] - p[s * 2]) * t); o[k * 2 + 1] = (float)(p[s * 2 + 1] + (p[j * 2 + 1] - p[s * 2 + 1]) * t);
        }
        return o;
        static double Sq(double v) => v * v;
    }

    static float[] Reverse(float[] p)
    {
        int n = p.Length / 2; var r = new float[p.Length];
        for (int i = 0; i < n; i++) { r[i * 2] = p[(n - 1 - i) * 2]; r[i * 2 + 1] = p[(n - 1 - i) * 2 + 1]; }
        return r;
    }

    /// <summary>b (same point count as a) rotated to the cyclic start that best matches a point-for-point.</summary>
    static float[] Align(float[] a, float[] b)
    {
        int n = a.Length / 2, best = 0; double bestD = double.MaxValue;
        int step = n > 96 ? 2 : 1;
        for (int s = 0; s < n; s++)
        {
            double d = 0;
            for (int i = 0; i < n && d < bestD; i += step) { int j = (i + s) % n; double dx = a[i * 2] - b[j * 2], dy = a[i * 2 + 1] - b[j * 2 + 1]; d += dx * dx + dy * dy; }
            if (d < bestD) { bestD = d; best = s; }
        }
        if (best == 0) return b;
        var r = new float[b.Length];
        for (int i = 0; i < n; i++) { int j = (i + best) % n; r[i * 2] = b[j * 2]; r[i * 2 + 1] = b[j * 2 + 1]; }
        return r;
    }

    /// <summary>Outline between two keyed outlines (t 0..1). Polygons with matching vertex counts blend vertex-for-vertex
    /// (edited keyframes keep their points); others are resampled by arc length and aligned first. Polygons pair up
    /// largest-with-largest; unmatched extras hold until halfway.</summary>
    public static List<float[]> Lerp(List<float[]> a, List<float[]> b, double t)
    {
        if (a.Count != b.Count)
        {
            a = a.OrderByDescending(p => Math.Abs(SignedArea(p))).ToList();
            b = b.OrderByDescending(p => Math.Abs(SignedArea(p))).ToList();
        }
        var r = new List<float[]>();
        int n = Math.Min(a.Count, b.Count);
        for (int k = 0; k < n; k++)
        {
            float[] x = a[k], y = b[k];
            if (x.Length != y.Length)
            {
                int m = Math.Clamp(Math.Max(x.Length, y.Length) / 2, 24, 160);
                x = Resample(x, m); y = Align(x, Resample(y, m));
            }
            else if (Math.Sign(SignedArea(x)) != Math.Sign(SignedArea(y))) y = Align(x, Reverse(y));
            var o = new float[x.Length];
            for (int i = 0; i < o.Length; i++) o[i] = (float)(x[i] + (y[i] - x[i]) * t);
            r.Add(o);
        }
        var rest = t < 0.5 ? a : b;
        for (int k = n; k < rest.Count; k++) r.Add(rest[k]);
        return r;
    }

    public static (double x, double y) Centroid(List<float[]> polys)
    {
        double sx = 0, sy = 0, sa = 0;
        foreach (var p in polys)
        {
            int n = p.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double cr = (double)p[j * 2] * p[i * 2 + 1] - (double)p[i * 2] * p[j * 2 + 1];
                sa += cr; sx += (p[j * 2] + p[i * 2]) * cr; sy += (p[j * 2 + 1] + p[i * 2 + 1]) * cr;
            }
        }
        if (Math.Abs(sa) < 1e-12) { var b = Bounds(polys); return (b.X + b.Width / 2, b.Y + b.Height / 2); }
        return (sx / (3 * sa), sy / (3 * sa));
    }

    /// <summary>Scaled copy about (cx, cy) in a w×h pixel space (keeps the aspect of what is on screen).</summary>
    public static List<float[]> Scale(List<float[]> polys, double f, double cx, double cy)
        => polys.Select(p => { var o = new float[p.Length]; for (int i = 0; i < p.Length; i += 2) { o[i] = (float)(cx + (p[i] - cx) * f); o[i + 1] = (float)(cy + (p[i + 1] - cy) * f); } return o; }).ToList();

    public static Rect Bounds(List<float[]> polys)
    {
        double x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
        foreach (var p in polys)
            for (int i = 0; i + 1 < p.Length; i += 2) { x0 = Math.Min(x0, p[i]); x1 = Math.Max(x1, p[i]); y0 = Math.Min(y0, p[i + 1]); y1 = Math.Max(y1, p[i + 1]); }
        return x1 < x0 ? Rect.Empty : new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Binary mask (logits &gt; 0) → outline polygons (normalised), via boundary tracing + Douglas–Peucker.
    /// Small specks (&lt; minFrac of the largest part) are dropped.</summary>
    public static List<float[]> FromMask(float[] logits, int w, int h, double minFrac = 0.02, double tolerance = 1.2)
    {
        var inside = new bool[w * h];
        for (int i = 0; i < inside.Length; i++) inside[i] = logits[i] > 0;
        var label = new int[w * h];
        var comps = new List<(int start, int area)>();
        var stack = new Stack<int>();
        for (int i = 0; i < inside.Length; i++)
        {
            if (!inside[i] || label[i] != 0) continue;
            int id = comps.Count + 1, area = 0;
            label[i] = id; stack.Push(i);
            while (stack.Count > 0)
            {
                int p = stack.Pop(); area++;
                int x = p % w, y = p / w;
                if (x > 0 && inside[p - 1] && label[p - 1] == 0) { label[p - 1] = id; stack.Push(p - 1); }
                if (x < w - 1 && inside[p + 1] && label[p + 1] == 0) { label[p + 1] = id; stack.Push(p + 1); }
                if (y > 0 && inside[p - w] && label[p - w] == 0) { label[p - w] = id; stack.Push(p - w); }
                if (y < h - 1 && inside[p + w] && label[p + w] == 0) { label[p + w] = id; stack.Push(p + w); }
            }
            comps.Add((i, area));   // i = first pixel in scan order = top-most, left-most
        }
        var result = new List<float[]>();
        if (comps.Count == 0) return result;
        int maxArea = comps.Max(c => c.area);
        for (int k = 0; k < comps.Count; k++)
        {
            if (comps[k].area < Math.Max(12, maxArea * minFrac)) continue;
            var pts = Trace(label, k + 1, comps[k].start, w, h);
            var simp = Simplify(pts, tolerance);
            if (simp.Count < 3) continue;
            var a = new float[simp.Count * 2];
            for (int i = 0; i < simp.Count; i++) { a[i * 2] = (float)(simp[i].X / w); a[i * 2 + 1] = (float)(simp[i].Y / h); }
            result.Add(a);
        }
        return result;
    }

    /// <summary>Moore-neighbour tracing of one component's outer boundary (pixel corners at +0.5 → centres).</summary>
    static List<Point> Trace(int[] label, int id, int start, int w, int h)
    {
        int[] dx = { 1, 1, 0, -1, -1, -1, 0, 1 }, dy = { 0, 1, 1, 1, 0, -1, -1, -1 };
        bool In(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && label[y * w + x] == id;
        var pts = new List<Point>();
        int sx = start % w, sy = start / w, cx = sx, cy = sy, dir = 7;
        int guard = w * h * 4;
        do
        {
            pts.Add(new Point(cx + 0.5, cy + 0.5));
            int d = (dir + 6) % 8, i;
            for (i = 0; i < 8; i++, d = (d + 1) % 8)
                if (In(cx + dx[d], cy + dy[d])) break;
            if (i == 8) break;                                   // single pixel
            cx += dx[d]; cy += dy[d]; dir = d;
        } while ((cx != sx || cy != sy) && --guard > 0);
        return pts;
    }

    static List<Point> Simplify(List<Point> pts, double tol)
    {
        if (pts.Count < 4) return pts;
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var st = new Stack<(int a, int b)>();
        st.Push((0, pts.Count - 1));
        while (st.Count > 0)
        {
            var (a, b) = st.Pop();
            double best = 0; int idx = -1;
            var A = pts[a]; var B = pts[b];
            double len = Math.Max(1e-9, (B - A).Length);
            for (int i = a + 1; i < b; i++)
            {
                double d = Math.Abs((B.X - A.X) * (A.Y - pts[i].Y) - (A.X - pts[i].X) * (B.Y - A.Y)) / len;
                if (d > best) { best = d; idx = i; }
            }
            if (idx >= 0 && best > tol) { keep[idx] = true; st.Push((a, idx)); st.Push((idx, b)); }
        }
        var o = new List<Point>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) o.Add(pts[i]);
        return o;
    }
}
