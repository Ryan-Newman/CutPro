using System.Globalization;
using System.Text;

namespace CutPro;

/// <summary>Lens distortion of a clip's picture (null = off): barrel (negative curvature — fixes a wide-angle / action-camera look) or pincushion
/// (positive — a bulge), with an edge term and a movable centre. Base values; keyframes on <see cref="Lens.Props"/>.</summary>
public sealed class LensDistort
{
    /// <summary>−100 … 100 → ffmpeg lenscorrection k1 −1 … 1.</summary>
    public double Curvature { get; set; }
    /// <summary>−100 … 100 → k2 (the curve grows towards the edges).</summary>
    public double Edge { get; set; }
    /// <summary>Centre offset, % of the picture (−50 … 50).</summary>
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public LensDistort Clone() => (LensDistort)MemberwiseClone();
}

/// <summary>
/// Lens distortion: the export runs ffmpeg <c>lenscorrection</c> (bilinear, transparent fill) on the clip's picture at its output size — after the
/// key and the colour work, before the corner pin, like the preview — with per-frame commands when keyframed; the preview runs the same
/// radial model in the picture's warp shader (<see cref="PinEffect"/>, with the pin when there is one): source = centre + (1 + k1·r² + k2·r⁴)·
/// (pixel − centre), r² = d²·4 / (w² + h²) (1 at the corners), centre = ⌊cx·w⌋ on whole pixels like lenscorrection.
/// </summary>
public static class Lens
{
    static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    public static readonly AnimProp[] Props = { AnimProp.LensCurve, AnimProp.LensEdge, AnimProp.LensCX, AnimProp.LensCY };
    public static bool Active(Clip c) => c.Lens != null && c.Kind is ClipKind.Video or ClipKind.Image;
    /// <summary>Keyframed (or formula) parameters; a track matte keeps its base values (its matte comes from the decoder).</summary>
    public static bool Animated(Clip c) => Active(c) && c.Mask == MaskMode.None && Anim.AnyAnimated(c, Props);
    /// <summary>(k1, k2, cx, cy) at timeline time t; cx / cy relative (0.5 = the middle).</summary>
    public static (double k1, double k2, double cx, double cy) At(Clip c, double t)
    {
        double V(AnimProp p) => Animated(c) ? Anim.Value(c, p, t) : Anim.Base(c, p);
        return (Math.Clamp(V(AnimProp.LensCurve) / 100, -1, 1), Math.Clamp(V(AnimProp.LensEdge) / 100, -1, 1),
                Math.Clamp(0.5 + V(AnimProp.LensCX) / 100, 0, 1), Math.Clamp(0.5 + V(AnimProp.LensCY) / 100, 0, 1));
    }
    public static string Sig((double k1, double k2, double cx, double cy) v) => FormattableString.Invariant($"{v.k1:0.######}|{v.k2:0.######}|{v.cx:0.######}|{v.cy:0.######}");
    /// <summary>The filter for one plane set (name = its sendcmd target).</summary>
    public static string Filter((double k1, double k2, double cx, double cy) v, string name) =>
        $"lenscorrection@{name}=cx={F(v.cx)}:cy={F(v.cy)}:k1={F(v.k1)}:k2={F(v.k2)}:i=bilinear:fc=black@0";
    /// <summary>Keyframed: a sendcmd file (bare name in %TEMP%) setting every named instance for each frame k of the clip (time k / fps, half a frame
    /// early), only where a value changes.</summary>
    public static string Commands(Clip c, IReadOnlyList<string> names, List<string> temps)
    {
        int n = Math.Max(1, (int)Math.Round(c.Dur * TimeUtil.Fps)) + 1;
        var sb = new StringBuilder(); var last = At(c, c.Start);
        for (int k = 1; k < n; k++)
        {
            var v = At(c, c.Start + k / TimeUtil.Fps);
            var parts = new List<string>();
            foreach (var name in names)
            {
                if (v.k1 != last.k1) parts.Add($"lenscorrection@{name} k1 {F(v.k1)}");
                if (v.k2 != last.k2) parts.Add($"lenscorrection@{name} k2 {F(v.k2)}");
                if (v.cx != last.cx) parts.Add($"lenscorrection@{name} cx {F(v.cx)}");
                if (v.cy != last.cy) parts.Add($"lenscorrection@{name} cy {F(v.cy)}");
            }
            if (parts.Count > 0) sb.Append(F(Math.Max(0, (k - 0.5) / TimeUtil.Fps))).Append(" [enter] ").Append(string.Join(", ", parts)).Append(";\n");
            last = v;
        }
        if (sb.Length == 0) sb.Append($"{F(c.Dur + 1)} [enter] lenscorrection@{names[0]} k1 {F(last.k1)};\n");
        var file = $"cutpro_lens_{Guid.NewGuid().ToString("N")[..8]}.txt";
        File.WriteAllText(Path.Combine(Path.GetTempPath(), file), sb.ToString()); temps.Add(Path.Combine(Path.GetTempPath(), file));
        return file;
    }
}

/// <summary>The picture warps (lens distortion, then corner pin) as one ffmpeg graph from the picture with its alpha (opaque, or the key's matte
/// merged in) to warped RGBA — transparent outside. lenscorrection runs on all four planes at once (gbrap: its fill is 0 there — on a grey
/// plane it would be video black, 16); perspective keeps no alpha, so the pin warps colours and alpha apart. The preview does the same in one
/// shader (<see cref="PinEffect"/>).</summary>
public static class Warp
{
    public static bool Active(Clip c) => Pin.Active(c) || Lens.Active(c);
    /// <summary>The preview's decoder runs the warps (base values): track mattes, or no shader compiler on this PC.</summary>
    public static bool InDecoder(Clip c) => Active(c) && (c.Mask != MaskMode.None || Pin.Shader() == null);
    public static bool InShader(Clip c) => Active(c) && !InDecoder(c);

    /// <summary>The whole graph from the clip's picture (scaled to w×h) to warped RGBA. temps = export: keyframed values (lens commands in
    /// %TEMP%, per-frame pin expressions); null = base values (the preview's decoder).</summary>
    public static string Graph(Clip c, int w, int h, string tag, List<string>? temps)
    {
        var g = new StringBuilder($"scale={w}:{h},setsar=1,format=rgba");
        if (Lens.Active(c))
        {
            string cmd = temps != null && Lens.Animated(c) ? ",sendcmd=f=" + Lens.Commands(c, new[] { $"lc{tag}" }, temps) : "";
            g.Append($",format=gbrap{cmd},{Lens.Filter(Lens.At(c, c.Start), $"lc{tag}")},format=rgba");
        }
        if (Pin.Active(c))
        {
            string wp = temps != null ? Pin.Warp(c, w, h) : Pin.Warp(c.Pin!, w, h);
            g.Append($",split[pc{tag}][pa{tag}];[pa{tag}]alphaextract,{wp}[pm{tag}];[pc{tag}]{wp}[pw{tag}];[pw{tag}][pm{tag}]alphamerge");
        }
        return g.ToString();
    }
}
