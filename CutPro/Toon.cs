using System.Globalization;
using System.Text;

namespace CutPro;

public enum ToonMode { None, Cartoon, Cel, Anime }

/// <summary>A drawn look for a clip: cartoon, cel shading or anime (null on the clip = none). See <see cref="Toon"/>.</summary>
public sealed class ToonStyle
{
    public ToonMode Mode { get; set; } = ToonMode.Cartoon;
    /// <summary>Flattening of detail (edge-preserving smoothing), 0…100.</summary>
    public double Smooth { get; set; } = 50;
    /// <summary>Brightness bands (fewer = flatter), 2…12.</summary>
    public double Levels { get; set; } = 7;
    /// <summary>Outline thickness at 720 lines, 0…4 px (0 = no outlines).</summary>
    public double Lines { get; set; } = 1;
    /// <summary>Outline darkness, 0…100.</summary>
    public double Ink { get; set; } = 90;
    /// <summary>Colour saturation, 0…200 %.</summary>
    public double Saturation { get; set; } = 125;

    public ToonStyle Clone() => (ToonStyle)MemberwiseClone();
    public static ToonStyle Preset(ToonMode m) => m switch
    {
        ToonMode.Cel => new ToonStyle { Mode = m, Smooth = 70, Levels = 4, Lines = 2, Ink = 100, Saturation = 135 },
        ToonMode.Anime => new ToonStyle { Mode = m, Smooth = 60, Levels = 6, Lines = 1, Ink = 70, Saturation = 140 },
        _ => new ToonStyle { Mode = ToonMode.Cartoon },
    };
}

/// <summary>
/// Cartoon / cel shading / anime as one ffmpeg filter graph (single input, single output): edge-preserving smoothing (bilateral), brightness
/// bands in YUV (hue kept; anime bands are soft), saturation, and ink outlines (Canny edges, thickened, multiplied over the colours). Every
/// filter is spatial, so a frame looks the same wherever decoding starts. The preview's decoder runs this graph inside its own ffmpeg
/// (<see cref="FrameStream"/>) and the export puts the same graph in the clip's chain — identical pictures; sizes are scaled by the frame
/// height so a 1080p export looks like the 720p preview.
/// </summary>
public static class Toon
{
    public static bool Active(ToonStyle? s) => s != null && s.Mode != ToonMode.None;

    /// <summary>The filter graph for frames of the given height. Labels get <paramref name="tag"/> so several clips can share a filter_complex.</summary>
    public static string Graph(ToonStyle s, int height, string tag = "")
    {
        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        double k = Math.Max(0.25, height / 720.0);
        bool anime = s.Mode == ToonMode.Anime, cel = s.Mode == ToonMode.Cel;
        int passes = s.Smooth < 1 ? 0 : s.Smooth < 40 ? 1 : s.Smooth < 75 ? 2 : 3;
        double sigmaS = (2 + 6 * s.Smooth / 100) * k, sigmaR = 0.06 + 0.10 * s.Smooth / 100;
        int levels = (int)Math.Round(Math.Clamp(s.Levels, 2, 12));
        double q = 219.0 / levels, soft = anime ? 0.35 : cel ? 0.04 : 0.08, lift = cel ? 0.10 : 0.03;
        // band value: 16 + q·(floor(t) + soft step at the band's middle), lifted so the darkest band isn't black
        string t = $"(val-16)/{F(q)}";
        string band = $"16+{F(lift * 219)}+{F(1 - lift)}*{F(q)}*(floor({t})+clip(({t}-floor({t})-0.5)/{F(soft)}+0.5,0,1))";
        var col = new StringBuilder();
        for (int i = 0; i < passes; i++) col.Append($"bilateral=sigmaS={F(sigmaS)}:sigmaR={F(sigmaR)}:planes=7,");
        col.Append($"format=yuv444p,lutyuv=y='clip({band},16,235)',eq=saturation={F(s.Saturation / 100)}");
        if (anime) col.Append(":brightness=0.02:contrast=1.06");
        col.Append(",format=gbrp");
        if (s.Lines <= 0.01 || s.Ink <= 0.5) return $"format=gbrp,{col}";
        // outlines: Canny on a blurred grey copy, thickened to the line width, inverted (black ink on white), softened by the ink amount
        double lo = anime ? 0.12 : cel ? 0.11 : 0.08, hi = anime ? 0.28 : cel ? 0.27 : 0.2;
        int grow = Math.Max(0, (int)Math.Round((s.Lines * k - 1) / 2));      // dilation adds 2 px of width per pass
        var ink = new StringBuilder($"format=gray,gblur=sigma={F(1.6 * k)},edgedetect=low={F(lo)}:high={F(hi)}");
        for (int i = 0; i < grow; i++) ink.Append(",dilation");
        ink.Append($",negate,lut=c0='255-(255-val)*{F(s.Ink / 100)}',format=gbrp");
        return $"format=gbrp,split[tc{tag}][te{tag}];[tc{tag}]{col}[tq{tag}];[te{tag}]{ink}[tl{tag}];[tq{tag}][tl{tag}]blend=all_mode=multiply";
    }

    /// <summary>A short signature (decoders restart when it changes).</summary>
    public static string Sig(ToonStyle? s) => !Active(s) ? "" : FormattableString.Invariant($"{s!.Mode}|{s.Smooth}|{s.Levels}|{s.Lines}|{s.Ink}|{s.Saturation}");
}
