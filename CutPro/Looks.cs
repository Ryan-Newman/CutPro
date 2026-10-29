using System.Globalization;
using System.Text;

namespace CutPro;

/// <summary>Look effects on a clip's picture (Effect Controls → Look effects). 0 = off.</summary>
public sealed class LookFx
{
    /// <summary>Unsharp mask on brightness, 0…100.</summary>
    public double Sharpen { get; set; }
    /// <summary>A soft halo on the bright parts, 0…100 (opacity of the halo).</summary>
    public double Glow { get; set; }
    /// <summary>Halo size in px at 720 lines.</summary>
    public double GlowRadius { get; set; } = 14;
    /// <summary>Brightness (%) where the glow starts.</summary>
    public double GlowThreshold { get; set; } = 60;
    /// <summary>Film grain on brightness, 0…100 (changes every frame).</summary>
    public double Grain { get; set; }
    /// <summary>VHS: chroma smear, scanlines, a little desaturation, 0…100.</summary>
    public double Vhs { get; set; }
    /// <summary>Glitch: RGB split and a sliding slice, 0…100.</summary>
    public double Glitch { get; set; }
    public LookFx Clone() => (LookFx)MemberwiseClone();
}

/// <summary>A clip's drop shadow (null = none): cast by its picture's outline (crop, key, masks, fades included) onto what is below it.</summary>
public sealed class DropShadow
{
    public double Opacity { get; set; } = 60;
    /// <summary>Direction the shadow falls, degrees clockwise from up (135 = down-right).</summary>
    public double Angle { get; set; } = 135;
    /// <summary>Offset in logical px.</summary>
    public double Distance { get; set; } = 10;
    /// <summary>Blur radius in logical px (the Gaussian blur's radius: preview BlurEffect, export gblur via Exporter.BlurSigma).</summary>
    public double Softness { get; set; } = 14;
    public string Color { get; set; } = "#000000";
    public DropShadow Clone() => (DropShadow)MemberwiseClone();
}

/// <summary>
/// Look effects as one ffmpeg graph after the drawn look (sharpen → glow → grain → VHS → glitch), run by the preview's decoder and by the
/// export after scale, like the cartoon looks — so both look the same (grain is random: its pattern differs, its strength doesn't). Sizes
/// follow the frame height. Drop shadows are composited instead (preview: a blurred copy of the layer's alpha under it; export: the same
/// from the clip's alpha with gblur).
/// </summary>
public static class Looks
{
    static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    public static bool Active(LookFx? l) => l != null && (l.Sharpen > 0.5 || l.Glow > 0.5 || l.Grain > 0.5 || l.Vhs > 0.5 || l.Glitch > 0.5);
    public static string Sig(LookFx? l) => !Active(l) ? "" : FormattableString.Invariant($"{l!.Sharpen}|{l.Glow}|{l.GlowRadius}|{l.GlowThreshold}|{l.Grain}|{l.Vhs}|{l.Glitch}");

    public static string Graph(LookFx l, int height, string tag = "")
    {
        double k = Math.Max(0.25, height / 720.0);
        var g = new List<string>();
        if (l.Sharpen > 0.5)
        {
            int m = Math.Clamp((int)Math.Round(5 * k) | 1, 3, 23);
            g.Add($"format=yuv444p,unsharp=luma_msize_x={m}:luma_msize_y={m}:luma_amount={F(l.Sharpen / 100 * 2.5)}:chroma_amount=0");
        }
        if (l.Glow > 0.5)
        {
            double thr = Math.Clamp(l.GlowThreshold, 0, 99) / 100 * 255, gain = 255 / (255 - thr), sigma = Math.Max(0.5, l.GlowRadius * k / 2);
            string lift = $"'clip((val-{F(thr)})*{F(gain)},0,255)'";
            g.Add($"format=gbrp,split[lg0{tag}][lg1{tag}];[lg1{tag}]lutrgb=r={lift}:g={lift}:b={lift},gblur=sigma={F(sigma)}[lg2{tag}];[lg0{tag}][lg2{tag}]blend=all_mode=screen:all_opacity={F(l.Glow / 100)}");
        }
        if (l.Grain > 0.5) g.Add($"format=yuv444p,noise=c0s={F(l.Grain / 100 * 40)}:c0f=t+u");
        if (l.Vhs > 0.5)
        {
            double a = l.Vhs / 100, sat = 1 - a * 0.28, con = 1 + a * 0.08; int smear = Math.Max(1, (int)Math.Round(a * 8 * k));
            g.Add($"format=yuv444p,eq=saturation={F(sat)}:contrast={F(con)},geq=lum='lum(X,Y)*(1-{F(a * 0.16)}*mod(Y,2))':cb='cb(X+{smear},Y)':cr='cr(X-{smear},Y)'");
        }
        if (l.Glitch > 0.5)
        {
            double a = l.Glitch / 100; int split = Math.Max(1, (int)Math.Round(a * 10 * k)), slice = Math.Max(4, (int)Math.Round(a * 18 * k));
            g.Add($"format=gbrp,rgbashift=rh={split}:bh=-{split},format=yuv444p,geq=lum='lum(X+if(between(Y,H/3,H/3+H/10),{slice},0),Y)':cb='cb(X,Y)':cr='cr(X,Y)'");
        }
        return string.Join(",", g);
    }

    /// <summary>The picture-changing graph of a clip after its key: drawn look, then look effects ("" = none).</summary>
    public static string ColourGraph(Clip c, int height, string tag = "")
    {
        var parts = new List<string>();
        if (Toon.Active(c.Toon)) parts.Add(Toon.Graph(c.Toon!, height, tag));
        if (Active(c.Looks)) parts.Add(Graph(c.Looks!, height, tag));
        return string.Join(",", parts);
    }

    /// <summary>The clip's picture needs its decoder to run a graph (key, drawn look, look effects; pin: the corner pin too — the preview
    /// warps it with a shader unless <see cref="Warp.InDecoder"/>).</summary>
    public static bool InDecoder(Clip c, bool pin = true) => Keyer.Active(c.Key) || Toon.Active(c.Toon) || Active(c.Looks) || pin && Warp.Active(c);

    /// <summary>The preview decoder's whole graph for a clip at w×h: key, colour work (matte carried around it), then the corner pin.
    /// srcAlpha: the picture has alpha of its own (a nested sequence) — carried around the key and colour work and multiplied with the key's
    /// matte, as the export does.</summary>
    public static string DecoderGraph(Clip c, int w, int h, bool srcAlpha = false, bool pin = true)
    {
        var g = Keyer.DecoderGraph(c.Key, ColourGraph(c, h), w, h);
        if (srcAlpha && g != "")
            g = Keyer.Active(c.Key)
                ? $"split[sa][sb];[sa]alphaextract[sx];[sb]format=gbrp,{g},split[sk][sl];[sl]alphaextract[sy];[sy][sx]blend=all_mode=multiply[sz];[sk][sz]alphamerge,format=rgba"
                : $"split[sa][sb];[sa]alphaextract[sx];[sb]format=gbrp,{g}[st];[st][sx]alphamerge,format=rgba";
        return pin && Warp.Active(c) ? (g == "" ? "" : g + ",") + Warp.Graph(c, w, h, "", null) : g;     // (base values: a track matte's or the no-shader fallback)
    }

    /// <summary>Shadow offset in logical px (x right, y down).</summary>
    public static (double dx, double dy) Offset(DropShadow s)
    {
        double a = s.Angle * Math.PI / 180;
        return (Math.Sin(a) * s.Distance, -Math.Cos(a) * s.Distance);
    }
}
