namespace CutPro;

/// <summary>Colour Match (Lumetri's Comparison view → Apply Match): the target frame's red, green and blue distributions are matched to a
/// reference frame's, percentile by percentile, and written as the target's R / G / B curves — so the result is an ordinary grade (one LUT:
/// preview = export) the user can still adjust.</summary>
public static class ColorMatch
{
    static readonly double[] Quantiles = { 0.005, 0.05, 0.15, 0.3, 0.5, 0.7, 0.85, 0.95, 0.995 };

    static double[] Cdf(byte[] px, int ch)
    {
        var hist = new double[256]; int n = 0;
        for (int i = ch; i < px.Length; i += 16) { hist[px[i]]++; n++; }          // (every 4th pixel)
        for (int v = 1; v < 256; v++) hist[v] += hist[v - 1];
        for (int v = 0; v < 256; v++) hist[v] /= Math.Max(1, n);
        return hist;
    }
    static double At(double[] cdf, double q)
    {
        int v = 0; while (v < 255 && cdf[v] < q) v++;
        double lo = v > 0 ? cdf[v - 1] : 0, hi = cdf[v];
        return Math.Clamp(v - 1 + (hi > lo ? (q - lo) / (hi - lo) : 1) + 0.5, 0, 255) / 255;   // (bin centres)
    }

    /// <summary>Curve points (x, y pairs in 0…1) that move channel ch (BGRA order: 0 = B, 1 = G, 2 = R) of the target towards the reference;
    /// strength 0 … 1. Monotonic, ends kept inside the range.</summary>
    public static double[] Curve(byte[] target, byte[] reference, int ch, double strength = 1)
    {
        var ct = Cdf(target, ch); var cr = Cdf(reference, ch);
        var pts = new List<(double x, double y)> { (0, 0) };
        foreach (var q in Quantiles)
        {
            double x = At(ct, q), y = At(cr, q);
            y = x + (y - x) * strength;
            if (x - pts[^1].x < 0.02) continue;                                   // (x must grow)
            pts.Add((x, Math.Max(pts[^1].y, y)));
        }
        if (1 - pts[^1].x < 0.02) pts.RemoveAt(pts.Count - 1);
        // past the brightest quantile: continue with its slope towards white (clipped)
        var (lx, ly) = pts[^1];
        pts.Add((1, Math.Clamp(ly + (1 - lx) * (strength * 1 + (1 - strength)), ly, 1)));
        return pts.SelectMany(p => new[] { Math.Round(p.x, 4), Math.Round(p.y, 4) }).ToArray();
    }

    /// <summary>The target's grade with its R / G / B curves replaced by the match (target = the frame graded with the rest of its grade).</summary>
    public static ColorGrade Match(ColorGrade? current, byte[] target, byte[] reference, double strength = 1)
    {
        var g = current?.Clone() ?? new ColorGrade();
        g.CurveR = Curve(target, reference, 2, strength);
        g.CurveG = Curve(target, reference, 1, strength);
        g.CurveB = Curve(target, reference, 0, strength);
        return g;
    }

    /// <summary>The grade without its R / G / B curves (what the match measures the target with).</summary>
    public static ColorGrade? WithoutRgbCurves(ColorGrade? g)
    {
        if (g == null) return null;
        var c = g.Clone(); c.CurveR = c.CurveG = c.CurveB = null;
        return c;
    }

    /// <summary>Mean absolute difference of the channels' means and spreads (a closeness score for tests, in levels).</summary>
    public static double Distance(byte[] a, byte[] b)
    {
        double d = 0;
        for (int ch = 0; ch < 3; ch++)
        {
            var (ma, sa) = Stats(a, ch); var (mb, sb) = Stats(b, ch);
            d += Math.Abs(ma - mb) + Math.Abs(sa - sb);
        }
        return d / 6;
    }
    static (double mean, double sd) Stats(byte[] px, int ch)
    {
        double s = 0, s2 = 0; int n = 0;
        for (int i = ch; i < px.Length; i += 4) { s += px[i]; s2 += px[i] * px[i]; n++; }
        double m = s / n; return (m, Math.Sqrt(Math.Max(0, s2 / n - m * m)));
    }
}
