using System.Globalization;

namespace CutPro;

/// <summary>
/// Crop: an alpha mask on the clip's picture as it is shown (after flips), the same numbers in the preview (opacity mask on the layer, at
/// its displayed size) and the export (a grey mask at the clip's output size multiplied into its alpha; a mask video when the crop is
/// keyframed). Edges: pixel coverage when hard, a linear ramp inwards over the feather when soft — the same at any resolution.
/// </summary>
public static class Crop
{
    public static bool Applied(Clip c) => c.CropL > 0 || c.CropT > 0 || c.CropR > 0 || c.CropB > 0 || Anim.AnyAnimated(c, Anim.Crop);
    public static bool Animated(Clip c) => Anim.AnyAnimated(c, Anim.Crop);

    /// <summary>Sides as fractions of the picture at timeline time t.</summary>
    public static (double l, double t, double r, double b) At(Clip c, double time) =>
        (Anim.Value(c, AnimProp.CropL, time) / 100, Anim.Value(c, AnimProp.CropT, time) / 100, Anim.Value(c, AnimProp.CropR, time) / 100, Anim.Value(c, AnimProp.CropB, time) / 100);

    public static string Sig((double l, double t, double r, double b) s, double feather, int w, int h) =>
        FormattableString.Invariant($"{s.l:0.#####}|{s.t:0.#####}|{s.r:0.#####}|{s.b:0.#####}|{feather:0.###}|{w}x{h}");

    // coverage of the pixel by the kept side of an edge d px away (pixel centre), or a ramp over f px: continuous at f = 1
    static double Edge(double d, double f) => Math.Clamp((d + 0.5) / Math.Max(f, 1), 0, 1);

    /// <summary>Writes the w×h mask: 1 byte per pixel (grey, export) or 4 (premultiplied white BGRA, the preview's opacity mask).
    /// feather = logical px at a picture height of 720.</summary>
    public static void Fill((double l, double t, double r, double b) s, double feather, int w, int h, byte[] dst, int bpp)
    {
        double f = feather * h / 720.0;
        var fx = new double[w]; var fy = new double[h];
        double x0 = s.l * w, x1 = (1 - s.r) * w, y0 = s.t * h, y1 = (1 - s.b) * h;
        for (int x = 0; x < w; x++) { double u = x + 0.5; fx[x] = Edge(u - x0, f) * Edge(x1 - u, f); }
        for (int y = 0; y < h; y++) { double v = y + 0.5; fy[y] = Edge(v - y0, f) * Edge(y1 - v, f); }
        Parallel.For(0, h, y =>
        {
            int o = y * w * bpp; double ky = fy[y];
            for (int x = 0; x < w; x++)
            {
                byte m = (byte)Math.Round(fx[x] * ky * 255);
                if (bpp == 1) dst[o + x] = m;
                else { int i = o + x * 4; dst[i] = dst[i + 1] = dst[i + 2] = dst[i + 3] = m; }
            }
        });
    }

    /// <summary>A grey PGM file (the export loops it for a crop that doesn't move).</summary>
    public static void WritePgm(string file, byte[] grey, int w, int h)
    {
        using var fs = File.Create(file);
        fs.Write(System.Text.Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"P5\n{w} {h}\n255\n")));
        fs.Write(grey, 0, w * h);
    }
}

