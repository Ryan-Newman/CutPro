using System.Globalization;
using System.Text;
using System.Windows.Media;

namespace CutPro;

/// <summary>A clip's chroma key (Effect Controls → Ultra Key). See <see cref="Keyer"/>.</summary>
public sealed class ChromaKey
{
    /// <summary>The screen colour.</summary>
    public string Color { get; set; } = "#00B140";
    /// <summary>How far from the key colour still counts as screen, 0…100.</summary>
    public double Tolerance { get; set; } = 35;
    /// <summary>Soft transition from screen to subject, 0…100.</summary>
    public double Softness { get; set; } = 25;
    /// <summary>Green / blue spill taken off the subject, 0…100.</summary>
    public double Spill { get; set; } = 60;
    /// <summary>Matte shrunk by this many px (at 720 lines), 0…10.</summary>
    public double Choke { get; set; }
    /// <summary>Matte edge blur (px at 720 lines), 0…10.</summary>
    public double Soften { get; set; }
    /// <summary>Luma key instead of colour: removes the brightness <see cref="Level"/> (0 = black, 100 = white) ± tolerance.</summary>
    public bool Luma { get; set; }
    public double Level { get; set; }
    /// <summary>Garbage matte: extra transparency from each edge of the picture (0…80 %), so lights / stands off the screen never leak. Preview and export share the same crop of the matte.</summary>
    public double GarbageL { get; set; }
    public double GarbageT { get; set; }
    public double GarbageR { get; set; }
    public double GarbageB { get; set; }
    public ChromaKey Clone() => (ChromaKey)MemberwiseClone();
}

/// <summary>
/// Chroma key as one ffmpeg graph (single input → RGBA): <c>chromakey</c> on full-resolution chroma (yuva444p), <c>despill</c> on the colours,
/// then choke (erosion) and soften (blur) on the matte. Like the drawn looks, the preview's decoder runs it (<see cref="FrameStream"/>, BGRA
/// output) and the export runs the same graph at the clip's output size, so both key the same pixels; sizes scale with the frame height.
/// The export takes the alpha off right after the key and multiplies it back after the colour work, so masks / looks / LUTs see the
/// unkeyed colours exactly as the preview's do.
/// </summary>
public static class Keyer
{
    static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    public static bool Active(ChromaKey? k) => k != null;
    public static Color KeyColor(ChromaKey k) => TitleVisual.ParseColor(k.Color, Color.FromRgb(0, 0xB1, 0x40));
    public static bool BlueScreen(ChromaKey k) { var c = KeyColor(k); return c.B > c.G; }

    public static string Graph(ChromaKey k, int height) => Graph(k, Math.Max(2, height * 16 / 9 / 2 * 2), height);
    public static string Graph(ChromaKey k, int width, int height)
    {
        double s = Math.Max(0.25, height / 720.0);
        var c = KeyColor(k);
        double sim = 0.01 + Math.Clamp(k.Tolerance, 0, 100) / 100 * 0.39, blend = Math.Clamp(k.Softness, 0, 100) / 100 * 0.3;
        var g = new StringBuilder(k.Luma
            ? $"format=yuva444p,lumakey=threshold={F((16 + Math.Clamp(k.Level, 0, 100) / 100 * 219) / 255)}:tolerance={F(sim)}:softness={F(blend)},format=rgba"   // (luma key, no spill: 0 = video black (16), 100 = video white (235) — lumakey compares raw codes)
            : $"format=yuva444p,chromakey=color=0x{c.R:X2}{c.G:X2}{c.B:X2}:similarity={F(sim)}:blend={F(blend)},format=rgba");
        if (k.Spill > 0.5 && !k.Luma)
        {
            double a = Math.Clamp(k.Spill, 0, 100) / 100;
            g.Append(BlueScreen(k) ? $",despill=type=blue:mix=0.5:expand=0:red=0:green=0:blue={F(-a)}" : $",despill=type=green:mix=0.5:expand=0:red=0:green={F(-a)}:blue=0");
        }
        int er = (int)Math.Round(Math.Clamp(k.Choke, 0, 10) * s);
        double sigma = Math.Clamp(k.Soften, 0, 10) * s * 0.6;
        if (er > 0 || sigma > 0.05)
        {
            g.Append(",format=gbrap");
            for (int i = 0; i < er; i++) g.Append(",erosion=threshold0=0:threshold1=0:threshold2=0");   // (thresholds 0: only the matte shrinks)
            if (sigma > 0.05) g.Append($",gblur=sigma={F(sigma)}:planes=8");
            g.Append(",format=rgba");
        }
        if (HasGarbage(k))
        {
            // crop the keyed picture to the keep-rect and pad with transparent — outside is always empty (lights / stands / the edge of the screen)
            double l = Math.Clamp(k.GarbageL, 0, 80) / 100, t = Math.Clamp(k.GarbageT, 0, 80) / 100;
            double r = Math.Clamp(k.GarbageR, 0, 80) / 100, btm = Math.Clamp(k.GarbageB, 0, 80) / 100;
            if (l + r > 0.98) { l = Math.Min(l, 0.49); r = Math.Min(r, 0.49); }
            if (t + btm > 0.98) { t = Math.Min(t, 0.49); btm = Math.Min(btm, 0.49); }
            int x = Math.Max(0, (int)Math.Round(width * l) & ~1), y = Math.Max(0, (int)Math.Round(height * t) & ~1);
            int x1 = width - Math.Max(0, (int)Math.Round(width * r) & ~1), y1 = height - Math.Max(0, (int)Math.Round(height * btm) & ~1);
            int cw = Math.Max(2, x1 - x), ch = Math.Max(2, y1 - y);
            if (x + cw > width) cw = Math.Max(2, width - x); if (y + ch > height) ch = Math.Max(2, height - y);
            g.Append($",crop={cw}:{ch}:{x}:{y}:exact=1,pad={width}:{height}:{x}:{y}:color=black@0,format=rgba");
        }
        return g.ToString();
    }

    public static bool HasGarbage(ChromaKey k) => k.GarbageL > 0.05 || k.GarbageT > 0.05 || k.GarbageR > 0.05 || k.GarbageB > 0.05;
    public static string Sig(ChromaKey? k) => k == null ? "" : FormattableString.Invariant($"{k.Color}|{k.Tolerance}|{k.Softness}|{k.Spill}|{k.Choke}|{k.Soften}|{k.Luma}|{k.Level}|{k.GarbageL}|{k.GarbageT}|{k.GarbageR}|{k.GarbageB}");

    /// <summary>The decoder graph for a clip: key, then the colour work (drawn look, look effects) with the matte carried around it (those
    /// works in planar RGB without alpha).</summary>
    public static string DecoderGraph(ChromaKey? key, string colour, int height) => DecoderGraph(key, colour, Math.Max(2, height * 16 / 9 / 2 * 2), height);
    public static string DecoderGraph(ChromaKey? key, string colour, int width, int height)
    {
        // colour = the drawn look + look effects (Looks.ColourGraph), which work without alpha: with a key the matte is carried around them
        if (!Active(key)) return colour;
        var kg = Graph(key!, width, height);
        return colour != "" ? $"{kg},split[ka][kb];[kb]{colour}[kt];[ka]alphaextract[kx];[kt][kx]alphamerge,format=rgba" : kg;
    }

    /// <summary>The screen colour of a frame (BGRA): the most common strongly green or blue hue, averaged.</summary>
    public static string? AutoColor(byte[] px, int w, int h)
    {
        var count = new int[36]; var sum = new long[36, 3];
        for (int y = 0; y < h; y += 2)
            for (int x = 0; x < w; x += 2)
            {
                int o = (y * w + x) * 4; int b = px[o], g = px[o + 1], r = px[o + 2];
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max < 50 || max - min < 0.3 * max) continue;                  // dark or grey
                double hue = max == r ? 60.0 * ((g - b) / (double)(max - min)) : max == g ? 60.0 * (2 + (b - r) / (double)(max - min)) : 60.0 * (4 + (r - g) / (double)(max - min));
                if (hue < 0) hue += 360;
                if (hue < 75 || hue > 260) continue;                            // keep greens, cyans and blues (not skin, red, yellow, purple)
                int bin = (int)(hue / 10) % 36;
                count[bin]++; sum[bin, 0] += r; sum[bin, 1] += g; sum[bin, 2] += b;
            }
        int best = Array.IndexOf(count, count.Max());
        if (count[best] < w * h / 4 / 50) return null;                          // under 2 % of the frame: no screen
        // the peak bin and its neighbours
        long n = 0, R = 0, G = 0, B = 0;
        for (int d = -1; d <= 1; d++) { int i = (best + d + 36) % 36; n += count[i]; R += sum[i, 0]; G += sum[i, 1]; B += sum[i, 2]; }
        return $"#{R / n:X2}{G / n:X2}{B / n:X2}";
    }
}
