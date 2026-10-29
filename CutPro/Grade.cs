using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>A colour wheel of Lumetri's Color Wheels (lift / gamma / gain): hue (degrees), how far towards it (0…100), luminance (−100…100).</summary>
public sealed class Wheel
{
    public double Hue { get; set; }
    public double Amount { get; set; }
    public double Luma { get; set; }
    public bool IsZero => Math.Abs(Amount) < 1e-9 && Math.Abs(Luma) < 1e-9;
}

/// <summary>Lumetri's HSL Secondary: a colour range picked by hue, saturation and luminance (soft edges), and a correction applied only there.
/// It depends on the colour alone, so it is baked into the grade's LUT like everything else (preview = export).</summary>
public sealed class HslKey
{
    public double Hue { get; set; } = 30;            // centre of the hue range, degrees
    public double HueWidth { get; set; } = 25;       // ± degrees fully selected (180 = every hue)
    public double SatMin { get; set; } = 15;         // % (chroma: max − min of R G B)
    public double SatMax { get; set; } = 100;
    public double LumMin { get; set; }               // % (Rec. 709 luma)
    public double LumMax { get; set; } = 100;
    public double Soft { get; set; } = 30;           // edge softness, %
    public bool Invert { get; set; }
    /// <summary>Show the selection as grey (white = selected) instead of the correction — for setting the range.</summary>
    public bool ShowMask { get; set; }
    // the correction inside the selection
    public double Temperature { get; set; }
    public double Tint { get; set; }
    public double Exposure { get; set; }
    public double Contrast { get; set; }
    public double Saturation { get; set; } = 100;
    public double HueShift { get; set; }             // degrees
    public bool IsZero => !ShowMask && Temperature == 0 && Tint == 0 && Exposure == 0 && Contrast == 0 && Math.Abs(Saturation - 100) < 1e-9 && HueShift == 0;
}

/// <summary>Colour correction of a clip, like Premiere's Lumetri Color (null on the clip = untouched).</summary>
public sealed class ColorGrade
{
    // basic correction
    public double Temperature { get; set; }          // −100 cool … 100 warm
    public double Tint { get; set; }                 // −100 green … 100 magenta
    public double Exposure { get; set; }             // stops, −4 … 4
    public double Contrast { get; set; }             // −100 … 100
    public double Highlights { get; set; }
    public double Shadows { get; set; }
    public double Whites { get; set; }
    public double Blacks { get; set; }
    public double Saturation { get; set; } = 100;    // %
    public double Vibrance { get; set; }             // −100 … 100
    // creative
    /// <summary>A built-in look (<see cref="Grade.Looks"/>) or the path of a .cube LUT file.</summary>
    public string? Look { get; set; }
    public double LookAmount { get; set; } = 100;    // %
    public double Faded { get; set; }                // 0 … 100
    // colour wheels
    public Wheel Lift { get; set; } = new();
    public Wheel Gamma { get; set; } = new();
    public Wheel Gain { get; set; } = new();
    // curves: (x, y) pairs in 0…1, sorted by x (null = straight)
    public double[]? Curve { get; set; }
    public double[]? CurveR { get; set; }
    public double[]? CurveG { get; set; }
    public double[]? CurveB { get; set; }
    // vignette (spatial: drawn over the clip, not part of the LUT)
    public double Vignette { get; set; }             // −100 dark … 100 light
    public double VigMidpoint { get; set; } = 50;
    public double VigFeather { get; set; } = 50;
    public double VigRoundness { get; set; }
    /// <summary>HSL Secondary (null = none).</summary>
    public HslKey? Hsl { get; set; }

    public ColorGrade Clone() => JsonSerializer.Deserialize<ColorGrade>(JsonSerializer.Serialize(this))!;
    /// <summary>True when the colours pass through unchanged (no LUT needed).</summary>
    public bool ColorIdentity =>
        Temperature == 0 && Tint == 0 && Exposure == 0 && Contrast == 0 && Highlights == 0 && Shadows == 0 && Whites == 0 && Blacks == 0
        && Math.Abs(Saturation - 100) < 1e-9 && Vibrance == 0 && (string.IsNullOrEmpty(Look) || LookAmount <= 0) && Faded == 0
        && Lift.IsZero && Gamma.IsZero && Gain.IsZero && Curve == null && CurveR == null && CurveG == null && CurveB == null && (Hsl == null || Hsl.IsZero);
    public bool HasVignette => Math.Abs(Vignette) > 1e-9;
}

/// <summary>
/// The colour pipeline: every per-pixel setting of a <see cref="ColorGrade"/> (white balance, tone, saturation, look, wheels, curves) is
/// baked into one 33³ 3D LUT. The export applies it with ffmpeg's <c>lut3d</c> (tetrahedral, from a .cube file); the preview applies the
/// same table to each decoded frame with the same interpolation (<see cref="Apply"/>), so both match. The vignette is drawn by WPF
/// (<see cref="VignetteBrush"/>): the preview puts it over the clip, the export overlays the same picture as a PNG.
/// </summary>
public static class Grade
{
    public const int N = 33;

    public sealed class Lut
    {
        public readonly float[] D;      // (r * N + g) * N + b → r g b
        public readonly int Size;
        public readonly string Sig;
        public Lut(float[] d, int size, string sig) { D = d; Size = size; Sig = sig; }
    }

    // ---------------- looks ----------------
    public static readonly string[] Looks =
    {
        "Teal & Orange", "Warm Film", "Golden Hour", "Cool Night", "Matte Cinema", "Vintage Fade", "Vivid Pop", "Bleach Bypass", "Black & White", "Sepia",
    };
    public static bool IsCube(string? look) => look != null && look.EndsWith(".cube", StringComparison.OrdinalIgnoreCase);

    static readonly Dictionary<string, Lut> cache = new();
    static readonly Dictionary<string, (DateTime when, CubeFile? cube)> cubes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A grade's identity (what the LUT depends on). Cheap enough per frame (no disk access); <see cref="Forget"/> after a .cube
    /// file changes on disk.</summary>
    public static string Signature(ColorGrade g) => JsonSerializer.Serialize(g);
    public static void Forget() { lock (cache) cache.Clear(); }

    /// <summary>The LUT of a grade, or null when its colours pass through (cached by signature).</summary>
    public static Lut? For(ColorGrade? g)
    {
        if (g == null || g.ColorIdentity) return null;
        var sig = Signature(g);
        lock (cache)
        {
            if (cache.TryGetValue(sig, out var l)) return l;
            if (cache.Count > 64) cache.Clear();
            cache[sig] = l = new Lut(Build(g), N, sig);
            return l;
        }
    }

    static float[] Build(ColorGrade g)
    {
        var d = new float[N * N * N * 3];
        var cube = IsCube(g.Look) ? Cube(g.Look!) : null;
        var curves = new[] { Spline(g.Curve), Spline(g.CurveR), Spline(g.CurveG), Spline(g.CurveB) };
        Parallel.For(0, N, ri =>
        {
            for (int gi = 0; gi < N; gi++)
                for (int bi = 0; bi < N; bi++)
                {
                    var (r, gg, b) = Pixel(g, cube, curves, ri / (double)(N - 1), gi / (double)(N - 1), bi / (double)(N - 1));
                    int o = ((ri * N + gi) * N + bi) * 3;
                    d[o] = (float)r; d[o + 1] = (float)gg; d[o + 2] = (float)b;
                }
        });
        return d;
    }

    static double Luma(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;
    static double ToLin(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    static double ToSrgb(double v) => v <= 0 ? 0 : v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
    static double Smooth(double a, double b, double x) { double t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
    static double C01(double v) => Math.Clamp(v, 0, 1);

    /// <summary>One colour through the whole grade (display-referred 0…1 in and out).</summary>
    static (double r, double g, double b) Pixel(ColorGrade g, CubeFile? cube, Func<double, double>?[] curves, double r, double gg, double b)
    {
        // 1. white balance and exposure, in linear light
        if (g.Temperature != 0 || g.Tint != 0 || g.Exposure != 0)
        {
            double t = g.Temperature / 100, m = g.Tint / 100;
            double kr = 1 + 0.30 * t + 0.10 * m, kg = 1 - 0.20 * m, kb = 1 - 0.30 * t + 0.10 * m;
            double norm = Luma(kr, kg, kb), e = Math.Pow(2, g.Exposure) / norm;
            r = ToSrgb(ToLin(r) * kr * e); gg = ToSrgb(ToLin(gg) * kg * e); b = ToSrgb(ToLin(b) * kb * e);
        }
        // 2. tone: contrast, highlights / shadows, whites / blacks
        if (g.Contrast != 0)
        {
            double k = g.Contrast / 100;
            double Ct(double x) { x = C01(x); return k >= 0 ? x + k * (x * x * (3 - 2 * x) - x) : 0.5 + (x - 0.5) * (1 + 0.6 * k); }
            r = Ct(r); gg = Ct(gg); b = Ct(b);
        }
        if (g.Highlights != 0 || g.Shadows != 0)
        {
            double L = C01(Luma(r, gg, b));
            double off = 0.28 * (g.Shadows / 100) * (1 - Smooth(0, 0.65, L)) + 0.28 * (g.Highlights / 100) * Smooth(0.35, 1, L);
            r += off; gg += off; b += off;
        }
        if (g.Whites != 0 || g.Blacks != 0)
        {
            double wp = 1 - 0.25 * g.Whites / 100, bp = -0.12 * g.Blacks / 100;
            r = (r - bp) / (wp - bp); gg = (gg - bp) / (wp - bp); b = (b - bp) / (wp - bp);
        }
        // 3. saturation, vibrance
        r = C01(r); gg = C01(gg); b = C01(b);
        if (Math.Abs(g.Saturation - 100) > 1e-9 || g.Vibrance != 0)
        {
            double L = Luma(r, gg, b);
            double s = g.Saturation / 100;
            double sat = Math.Max(r, Math.Max(gg, b)) - Math.Min(r, Math.Min(gg, b));
            s *= 1 + g.Vibrance / 100 * (1 - sat);
            r = C01(L + (r - L) * s); gg = C01(L + (gg - L) * s); b = C01(L + (b - L) * s);
        }
        // 4. creative look at its intensity, faded film
        if (!string.IsNullOrEmpty(g.Look) && g.LookAmount > 0)
        {
            var (lr, lg, lb) = cube != null ? cube.Sample(r, gg, b) : BuiltIn(g.Look!, r, gg, b);
            double a = g.LookAmount / 100;
            r += (lr - r) * a; gg += (lg - gg) * a; b += (lb - b) * a;
        }
        if (g.Faded > 0)
        {
            double f = g.Faded / 100;
            r = r * (1 - 0.22 * f) + 0.11 * f; gg = gg * (1 - 0.22 * f) + 0.11 * f; b = b * (1 - 0.22 * f) + 0.12 * f;
        }
        // 5. colour wheels: lift (shadows), gamma (midtones), gain (highlights)
        if (!g.Lift.IsZero || !g.Gamma.IsZero || !g.Gain.IsZero)
        {
            var lf = WheelVec(g.Lift, 0.15); var gm = WheelVec(g.Gamma, 0.35); var gn = WheelVec(g.Gain, 0.3);
            double W(double x, double lift, double gamma, double gain)
            {
                x = C01(x * (1 + gain) + lift * (1 - x));
                return Math.Pow(x, 1 / Math.Max(0.1, 1 + gamma));
            }
            r = W(r, lf.r, gm.r, gn.r); gg = W(gg, lf.g, gm.g, gn.g); b = W(b, lf.b, gm.b, gn.b);
        }
        // 6. curves: master, then each channel
        if (curves[0] is { } cm) { r = cm(C01(r)); gg = cm(C01(gg)); b = cm(C01(b)); }
        if (curves[1] is { } cr) r = cr(C01(r));
        if (curves[2] is { } cg) gg = cg(C01(gg));
        if (curves[3] is { } cb) b = cb(C01(b));
        // 7. HSL secondary: a correction weighted by how much the colour is in the picked range
        if (g.Hsl is { } hk && !hk.IsZero)
        {
            r = C01(r); gg = C01(gg); b = C01(b);
            double m = Matte(hk, r, gg, b);
            if (hk.ShowMask) return (m, m, m);
            var (sr, sg, sb) = Secondary(hk, r, gg, b);
            r += (sr - r) * m; gg += (sg - gg) * m; b += (sb - b) * m;
        }
        return (C01(r), C01(gg), C01(b));
    }

    /// <summary>Hue (degrees), chroma and Rec. 709 luma of a colour (0…1).</summary>
    public static (double h, double s, double l) Hsl(double r, double g, double b)
    {
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), c = mx - mn, h = 0;
        if (c > 1e-9) h = mx == r ? 60 * (((g - b) / c % 6 + 6) % 6) : mx == g ? 60 * ((b - r) / c + 2) : 60 * ((r - g) / c + 4);
        return (h, c, Luma(r, g, b));
    }
    /// <summary>How much a colour is in the HSL range (0…1): hue distance, chroma and luma, each with soft edges, multiplied.</summary>
    public static double Matte(HslKey k, double r, double g, double b)
    {
        var (h, s, l) = Hsl(r, g, b);
        double soft = Math.Max(0.5, k.Soft) / 100;
        double d = Math.Abs(((h - k.Hue) % 360 + 540) % 360 - 180);              // (angular distance, 0 … 180)
        double wh = k.HueWidth >= 180 ? 1 : 1 - Smooth(k.HueWidth, k.HueWidth + 1 + 60 * soft, d);
        double s100 = s * 100, l100 = l * 100, es = 1 + 30 * soft;
        double ws = (k.SatMin <= 0 ? 1 : Smooth(k.SatMin - es, k.SatMin, s100)) * (k.SatMax >= 100 ? 1 : 1 - Smooth(k.SatMax, k.SatMax + es, s100));
        double wl = (k.LumMin <= 0 ? 1 : Smooth(k.LumMin - es, k.LumMin, l100)) * (k.LumMax >= 100 ? 1 : 1 - Smooth(k.LumMax, k.LumMax + es, l100));
        double m = wh * ws * wl;
        return k.Invert ? 1 - m : m;
    }
    /// <summary>The secondary correction on one colour: white balance and exposure in linear light, contrast, saturation, hue rotation.</summary>
    static (double r, double g, double b) Secondary(HslKey k, double r, double g, double b)
    {
        if (k.Temperature != 0 || k.Tint != 0 || k.Exposure != 0)
        {
            double t = k.Temperature / 100, m = k.Tint / 100;
            double kr = 1 + 0.30 * t + 0.10 * m, kg = 1 - 0.20 * m, kb = 1 - 0.30 * t + 0.10 * m;
            double e = Math.Pow(2, k.Exposure) / Luma(kr, kg, kb);
            r = ToSrgb(ToLin(r) * kr * e); g = ToSrgb(ToLin(g) * kg * e); b = ToSrgb(ToLin(b) * kb * e);
        }
        if (k.Contrast != 0) { double c = 1 + k.Contrast / 100; r = 0.5 + (r - 0.5) * c; g = 0.5 + (g - 0.5) * c; b = 0.5 + (b - 0.5) * c; }
        double L = Luma(r, g, b);
        if (k.HueShift != 0)
        {   // rotate the colour difference (Cb, Cr) about the grey axis
            double cbv = b - L, crv = r - L, a = k.HueShift * Math.PI / 180, cs = Math.Cos(a), sn = Math.Sin(a);
            double cb2 = cbv * cs - crv * sn, cr2 = cbv * sn + crv * cs;
            r = L + cr2; b = L + cb2; g = (L - 0.2126 * r - 0.0722 * b) / 0.7152;
        }
        if (Math.Abs(k.Saturation - 100) > 1e-9) { double s = k.Saturation / 100; r = L + (r - L) * s; g = L + (g - L) * s; b = L + (b - L) * s; }
        return (C01(r), C01(g), C01(b));
    }

    /// <summary>A wheel as an RGB offset: the hue's colour (luma removed) × amount, plus the luminance slider (scaled by k).</summary>
    static (double r, double g, double b) WheelVec(Wheel w, double k)
    {
        var (hr, hg, hb) = HueRgb(w.Hue);
        double L = Luma(hr, hg, hb), a = w.Amount / 100, l = w.Luma / 100;
        return (k * (a * (hr - L) + l), k * (a * (hg - L) + l), k * (a * (hb - L) + l));
    }
    public static (double r, double g, double b) HueRgb(double hue)
    {
        double h = ((hue % 360) + 360) % 360 / 60, x = 1 - Math.Abs(h % 2 - 1);
        return (int)h switch { 0 => (1, x, 0), 1 => (x, 1, 0), 2 => (0, 1, x), 3 => (0, x, 1), 4 => (x, 0, 1), _ => (1, 0, x) };
    }

    static (double r, double g, double b) BuiltIn(string look, double r, double g, double b)
    {
        double L = Luma(r, g, b);
        (double, double, double) Mix(double s, double cr, double cg, double cb) => (L + (cr - L) * s, L + (cg - L) * s, L + (cb - L) * s);
        double S(double x) => x + 0.6 * (x * x * (3 - 2 * x) - x);                  // gentle S-curve
        switch (look)
        {
            case "Teal & Orange":
            {
                double sh = 1 - Smooth(0.1, 0.6, L), hi = Smooth(0.35, 0.95, L);
                double rr = r - 0.10 * sh + 0.10 * hi, gg = g + 0.02 * sh + 0.03 * hi, bb = b + 0.10 * sh - 0.08 * hi;
                var (a, c, d) = Mix(1.12, rr, gg, bb); return (S(C01(a)), S(C01(c)), S(C01(d)));
            }
            case "Warm Film": return (S(C01(r * 1.06 + 0.02)), S(C01(g * 1.0 + 0.01)), S(C01(b * 0.88 + 0.03)));
            case "Golden Hour":
            {
                var (a, c, d) = Mix(1.18, r * 1.12 + 0.03, g * 1.03 + 0.01, b * 0.80);
                return (C01(a), C01(c), C01(d));
            }
            case "Cool Night":
            {
                var (a, c, d) = Mix(0.75, r * 0.85, g * 0.95, b * 1.12 + 0.03);
                return (C01(a * 0.9), C01(c * 0.9), C01(d * 0.92));
            }
            case "Matte Cinema":
            {
                double sh = 1 - Smooth(0, 0.5, L);
                double rr = S(r) * 0.9 + 0.06, gg = S(g) * 0.9 + 0.06 + 0.02 * sh, bb = S(b) * 0.9 + 0.07 + 0.04 * sh;
                return (C01(rr), C01(gg), C01(bb));
            }
            case "Vintage Fade":
            {
                var (a, c, d) = Mix(0.78, r, g, b);
                return (C01(a * 0.84 + 0.12), C01(c * 0.82 + 0.11), C01(d * 0.74 + 0.10));
            }
            case "Vivid Pop": { var (a, c, d) = Mix(1.35, S(r), S(g), S(b)); return (C01(a), C01(c), C01(d)); }
            case "Bleach Bypass": { var (a, c, d) = Mix(0.45, r, g, b); return (C01(S(S(a))), C01(S(S(c))), C01(S(S(d)))); }
            case "Black & White": { double y = S(L); return (y, y, y); }
            case "Sepia": { double y = L; return (C01(y * 1.07 + 0.05), C01(y * 0.90 + 0.04), C01(y * 0.68 + 0.02)); }
            default: return (r, g, b);
        }
    }

    // ---------------- curves (monotone cubic, Fritsch–Carlson) ----------------
    public static Func<double, double>? Spline(double[]? pts)
    {
        if (pts == null || pts.Length < 4) return null;
        int n = pts.Length / 2;
        var xs = new double[n]; var ys = new double[n];
        for (int i = 0; i < n; i++) { xs[i] = pts[2 * i]; ys[i] = pts[2 * i + 1]; }
        var m = new double[n]; var dk = new double[n - 1];
        for (int i = 0; i < n - 1; i++) dk[i] = (ys[i + 1] - ys[i]) / Math.Max(1e-9, xs[i + 1] - xs[i]);
        m[0] = dk[0]; m[n - 1] = dk[n - 2];
        for (int i = 1; i < n - 1; i++) m[i] = dk[i - 1] * dk[i] <= 0 ? 0 : (dk[i - 1] + dk[i]) / 2;
        for (int i = 0; i < n - 1; i++)
        {
            if (Math.Abs(dk[i]) < 1e-12) { m[i] = m[i + 1] = 0; continue; }
            double a = m[i] / dk[i], b = m[i + 1] / dk[i], h = a * a + b * b;
            if (h > 9) { double t = 3 / Math.Sqrt(h); m[i] = t * a * dk[i]; m[i + 1] = t * b * dk[i]; }
        }
        return x =>
        {
            if (x <= xs[0]) return C01(ys[0]);
            if (x >= xs[n - 1]) return C01(ys[n - 1]);
            int i = 0; while (i < n - 2 && x > xs[i + 1]) i++;
            double h = xs[i + 1] - xs[i], t = (x - xs[i]) / h, t2 = t * t, t3 = t2 * t;
            return C01((2 * t3 - 3 * t2 + 1) * ys[i] + (t3 - 2 * t2 + t) * h * m[i] + (-2 * t3 + 3 * t2) * ys[i + 1] + (t3 - t2) * h * m[i + 1]);
        };
    }

    // ---------------- applying a LUT to 8-bit pixels (like ffmpeg's lut3d, tetrahedral) ----------------
    /// <summary>Applies the LUT in place to BGR(A/0) pixels (4 bytes each). Same arithmetic as ffmpeg's lut3d for 8-bit RGB: coordinates
    /// v·(N−1)/255 in float, tetrahedral interpolation, output truncated to 0…255.</summary>
    public static void Apply(Lut lut, byte[] px, int w, int h, int stride)
    {
        var d = lut.D; int n = lut.Size; float sc = (n - 1) / 255f; int max = n - 1;
        // per 8-bit value: the lower node's offset on each axis, the next node's, and the fraction (computed exactly as lut3d does)
        var fr = new float[256]; int[] r0 = new int[256], r1 = new int[256], g0 = new int[256], g1 = new int[256], b0 = new int[256], b1 = new int[256];
        for (int v = 0; v < 256; v++)
        {
            float s = v * sc; int p = (int)s, q = Math.Min(p + 1, max);
            fr[v] = s - p; r0[v] = p * n * n * 3; r1[v] = q * n * n * 3; g0[v] = p * n * 3; g1[v] = q * n * 3; b0[v] = p * 3; b1[v] = q * 3;
        }
        Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, y =>
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                int o = row + x * 4;
                int vr = px[o + 2], vg = px[o + 1], vb = px[o];
                float dr = fr[vr], dg = fr[vg], db = fr[vb];
                int Rp = r0[vr], Rn = r1[vr], Gp = g0[vg], Gn = g1[vg], Bp = b0[vb], Bn = b1[vb];
                int i000 = Rp + Gp + Bp, i111 = Rn + Gn + Bn;
                float cr, cg, cb;
                if (dr > dg)
                {
                    if (dg > db)
                    {
                        int i100 = Rn + Gp + Bp, i110 = Rn + Gn + Bp;
                        float a = 1 - dr, b = dr - dg, c = dg - db;
                        cr = a * d[i000] + b * d[i100] + c * d[i110] + db * d[i111];
                        cg = a * d[i000 + 1] + b * d[i100 + 1] + c * d[i110 + 1] + db * d[i111 + 1];
                        cb = a * d[i000 + 2] + b * d[i100 + 2] + c * d[i110 + 2] + db * d[i111 + 2];
                    }
                    else if (dr > db)
                    {
                        int i100 = Rn + Gp + Bp, i101 = Rn + Gp + Bn;
                        float a = 1 - dr, b = dr - db, c = db - dg;
                        cr = a * d[i000] + b * d[i100] + c * d[i101] + dg * d[i111];
                        cg = a * d[i000 + 1] + b * d[i100 + 1] + c * d[i101 + 1] + dg * d[i111 + 1];
                        cb = a * d[i000 + 2] + b * d[i100 + 2] + c * d[i101 + 2] + dg * d[i111 + 2];
                    }
                    else
                    {
                        int i001 = Rp + Gp + Bn, i101 = Rn + Gp + Bn;
                        float a = 1 - db, b = db - dr, c = dr - dg;
                        cr = a * d[i000] + b * d[i001] + c * d[i101] + dg * d[i111];
                        cg = a * d[i000 + 1] + b * d[i001 + 1] + c * d[i101 + 1] + dg * d[i111 + 1];
                        cb = a * d[i000 + 2] + b * d[i001 + 2] + c * d[i101 + 2] + dg * d[i111 + 2];
                    }
                }
                else
                {
                    if (db > dg)
                    {
                        int i001 = Rp + Gp + Bn, i011 = Rp + Gn + Bn;
                        float a = 1 - db, b = db - dg, c = dg - dr;
                        cr = a * d[i000] + b * d[i001] + c * d[i011] + dr * d[i111];
                        cg = a * d[i000 + 1] + b * d[i001 + 1] + c * d[i011 + 1] + dr * d[i111 + 1];
                        cb = a * d[i000 + 2] + b * d[i001 + 2] + c * d[i011 + 2] + dr * d[i111 + 2];
                    }
                    else if (db > dr)
                    {
                        int i010 = Rp + Gn + Bp, i011 = Rp + Gn + Bn;
                        float a = 1 - dg, b = dg - db, c = db - dr;
                        cr = a * d[i000] + b * d[i010] + c * d[i011] + dr * d[i111];
                        cg = a * d[i000 + 1] + b * d[i010 + 1] + c * d[i011 + 1] + dr * d[i111 + 1];
                        cb = a * d[i000 + 2] + b * d[i010 + 2] + c * d[i011 + 2] + dr * d[i111 + 2];
                    }
                    else
                    {
                        int i010 = Rp + Gn + Bp, i110 = Rn + Gn + Bp;
                        float a = 1 - dg, b = dg - dr, c = dr - db;
                        cr = a * d[i000] + b * d[i010] + c * d[i110] + db * d[i111];
                        cg = a * d[i000 + 1] + b * d[i010 + 1] + c * d[i110 + 1] + db * d[i111 + 1];
                        cb = a * d[i000 + 2] + b * d[i010 + 2] + c * d[i110 + 2] + db * d[i111 + 2];
                    }
                }
                px[o + 2] = (byte)Math.Clamp((int)(cr * 255f), 0, 255);
                px[o + 1] = (byte)Math.Clamp((int)(cg * 255f), 0, 255);
                px[o] = (byte)Math.Clamp((int)(cb * 255f), 0, 255);
            }
        });
    }

    /// <summary>A graded copy of a picture (Bgra32), for still images in the preview.</summary>
    public static BitmapSource ApplyTo(BitmapSource src, Lut lut)
    {
        var b = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = b.PixelWidth, h = b.PixelHeight, stride = w * 4;
        var px = new byte[stride * h]; b.CopyPixels(px, stride, 0);
        Apply(lut, px, w, h, stride);
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), px, stride, 0);
        wb.Freeze();
        return wb;
    }

    // ---------------- .cube files ----------------
    /// <summary>Writes the LUT as a .cube file (R fastest, as ffmpeg's lut3d reads it).</summary>
    public static void WriteCube(Lut lut, string path)
    {
        var sb = new StringBuilder("TITLE \"VEdiT grade\"\nLUT_3D_SIZE " + lut.Size + "\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n");
        int n = lut.Size;
        for (int b = 0; b < n; b++)
            for (int g = 0; g < n; g++)
                for (int r = 0; r < n; r++)
                {
                    int o = ((r * n + g) * n + b) * 3;
                    sb.Append(lut.D[o].ToString("0.######", CultureInfo.InvariantCulture)).Append(' ')
                      .Append(lut.D[o + 1].ToString("0.######", CultureInfo.InvariantCulture)).Append(' ')
                      .Append(lut.D[o + 2].ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');
                }
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>A loaded .cube LUT (3D, or 1D per channel) used as a creative look.</summary>
    public sealed class CubeFile
    {
        public int Size; public bool Is1D; public float[] D = Array.Empty<float>();
        public double[] Min = { 0, 0, 0 }, Max = { 1, 1, 1 };
        public string Title = "";
        public (double, double, double) Sample(double r, double g, double b)
        {
            double Nrm(double v, int c) => C01((v - Min[c]) / Math.Max(1e-9, Max[c] - Min[c])) * (Size - 1);
            if (Is1D)
            {
                double One(double v, int c) { double x = Nrm(v, c); int i = Math.Min((int)x, Size - 2); double f = x - i; return D[i * 3 + c] * (1 - f) + D[(i + 1) * 3 + c] * f; }
                return (One(r, 0), One(g, 1), One(b, 2));
            }
            double xr = Nrm(r, 0), xg = Nrm(g, 1), xb = Nrm(b, 2);
            int r0 = Math.Min((int)xr, Size - 2), g0 = Math.Min((int)xg, Size - 2), b0 = Math.Min((int)xb, Size - 2);
            double fr = xr - r0, fg = xg - g0, fb = xb - b0;
            // trilinear (a look is sampled only while baking the grade's own LUT)
            double Ch(int c)
            {
                double V(int i, int j, int k) => D[(((r0 + i) * Size + g0 + j) * Size + b0 + k) * 3 + c];
                double c00 = V(0, 0, 0) * (1 - fr) + V(1, 0, 0) * fr, c01 = V(0, 0, 1) * (1 - fr) + V(1, 0, 1) * fr;
                double c10 = V(0, 1, 0) * (1 - fr) + V(1, 1, 0) * fr, c11 = V(0, 1, 1) * (1 - fr) + V(1, 1, 1) * fr;
                double c0 = c00 * (1 - fg) + c10 * fg, c1 = c01 * (1 - fg) + c11 * fg;
                return c0 * (1 - fb) + c1 * fb;
            }
            return (C01(Ch(0)), C01(Ch(1)), C01(Ch(2)));
        }
    }

    /// <summary>Loads a .cube file (cached until it changes); null when it is missing or not a LUT.</summary>
    public static CubeFile? Cube(string path)
    {
        DateTime when;
        try { if (!File.Exists(path)) return null; when = File.GetLastWriteTimeUtc(path); } catch { return null; }
        lock (cubes) if (cubes.TryGetValue(path, out var c) && c.when == when) return c.cube;
        CubeFile? res = null;
        try
        {
            var cf = new CubeFile(); var vals = new List<float>();
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                switch (p[0].ToUpperInvariant())
                {
                    case "TITLE": cf.Title = line[5..].Trim().Trim('"'); continue;
                    case "LUT_3D_SIZE": cf.Size = int.Parse(p[1], CultureInfo.InvariantCulture); continue;
                    case "LUT_1D_SIZE": cf.Size = int.Parse(p[1], CultureInfo.InvariantCulture); cf.Is1D = true; continue;
                    case "DOMAIN_MIN": cf.Min = p.Skip(1).Take(3).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray(); continue;
                    case "DOMAIN_MAX": cf.Max = p.Skip(1).Take(3).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray(); continue;
                }
                if (p.Length >= 3 && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                    && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var bb) && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var cc))
                { vals.Add(a); vals.Add(bb); vals.Add(cc); }
            }
            int need = cf.Is1D ? cf.Size * 3 : cf.Size * cf.Size * cf.Size * 3;
            if (cf.Size >= 2 && vals.Count >= need)
            {
                if (cf.Is1D) cf.D = vals.Take(need).ToArray();
                else
                {
                    // file order: R fastest → (r * N + g) * N + b
                    cf.D = new float[need]; int n = cf.Size, i = 0;
                    for (int b = 0; b < n; b++) for (int g = 0; g < n; g++) for (int r = 0; r < n; r++, i++)
                    { int o = ((r * n + g) * n + b) * 3; cf.D[o] = vals[i * 3]; cf.D[o + 1] = vals[i * 3 + 1]; cf.D[o + 2] = vals[i * 3 + 2]; }
                }
                res = cf;
            }
        }
        catch { res = null; }
        lock (cubes) cubes[path] = (when, res);
        return res;
    }

    // ---------------- vignette ----------------
    /// <summary>The vignette as a brush over the clip (relative to its box): transparent inside the midpoint, then black (amount &lt; 0) or
    /// white (&gt; 0) over the feather. Roundness 0 follows the clip's shape, 100 is a circle.</summary>
    public static Brush? VignetteBrush(ColorGrade? g, double w, double h)
    {
        if (g == null || !g.HasVignette || w <= 0 || h <= 0) return null;
        // gradient offset 0 = centre, 1 = the corners; the darkening starts at midpoint − feather/2 and is full at midpoint + feather/2
        double a = Math.Min(1, Math.Abs(g.Vignette) / 100), m = 0.25 + 0.6 * g.VigMidpoint / 100, f = 0.05 + 0.6 * g.VigFeather / 100;
        double s0 = Math.Clamp(m - f / 2, 0, 0.995), s1 = Math.Clamp(m + f / 2, s0 + 0.005, 1);
        double round = g.VigRoundness / 100, aspect = w / h, rx = 0.5, ry = 0.5;
        if (round > 0) { if (aspect > 1) ry = 0.5 * (1 + round * (aspect - 1)); else rx = 0.5 * (1 + round * (1 / aspect - 1)); }   // towards a circle
        byte v = g.Vignette < 0 ? (byte)0 : (byte)255;
        Color C(double alpha) => Color.FromArgb((byte)Math.Round(255 * alpha), v, v, v);
        var br = new RadialGradientBrush { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = rx * Math.Sqrt(2), RadiusY = ry * Math.Sqrt(2),
                                           MappingMode = BrushMappingMode.RelativeToBoundingBox };
        br.GradientStops.Add(new GradientStop(C(0), 0));
        br.GradientStops.Add(new GradientStop(C(0), s0));
        foreach (double t in new[] { 0.25, 0.5, 0.75 }) br.GradientStops.Add(new GradientStop(C(a * t * t * (3 - 2 * t)), s0 + (s1 - s0) * t));
        br.GradientStops.Add(new GradientStop(C(a), s1));
        br.Freeze();
        return br;
    }

    /// <summary>The vignette as a PNG of w×h output pixels (the export overlays it on the clip).</summary>
    public static string? VignettePng(ColorGrade? g, int w, int h, string file)
    {
        var br = VignetteBrush(g, w, h);
        if (br == null) return null;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(br, null, new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(file)) enc.Save(fs);
        return file;
    }
}
