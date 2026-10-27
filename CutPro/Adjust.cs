using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// Adjustment layers: a clip with no picture of its own whose Lumetri colour (grade, look, curves, wheels, vignette) and Gaussian blur apply
/// to everything on the tracks below it while it is on screen; its opacity, keyframes and fades are the strength. One rule both sides follow,
/// at strength s: blur by radius·s, then colour mixed s of the way to the grade, then the vignette at s. Preview: the blur is a WPF BlurEffect
/// on the tracks-below canvas, the colour a pixel shader (<see cref="LutEffect"/>: the grade's 33³ LUT as a 6×6 grid of slices, trilinear in
/// the shader), the vignette a rectangle above. Export (<see cref="Graph"/>): the composite's frames under the clip → the same blur (transparent
/// outside the frame, like WPF), lut3d trilinear, the vignette PNG, mixed by s per frame, back over the composite. On V1 it has nothing below.
/// </summary>
public static class Adjust
{
    public static bool Is(Clip c) => c.Kind == ClipKind.Adjustment;
    /// <summary>Tracks below an adjustment layer exist (V3 → V2 + V1, V2 → V1).</summary>
    public static bool Works(Clip c) => Is(c) && c.Track < Tracks.V1;     // (any video track above V1: something is below it)

    /// <summary>The strength at t: opacity (keyframed) × fades.</summary>
    public static double Strength(Clip c, double t) => Math.Clamp(Anim.Value(c, AnimProp.Opacity, t) / 100 * c.Fade(t), 0, 1);
    /// <summary>Blur radius (logical px) at t: the clip's (keyframed) blur × strength.</summary>
    public static double Radius(Clip c, double t) => Math.Max(0, Anim.Value(c, AnimProp.Blur, t)) * Strength(c, t);
    public static bool Blurs(Clip c) => c.Blur > 0.05 || Anim.IsAnimated(c, AnimProp.Blur);

    // ---------------- preview ----------------
    // The preview's LUT is 17³ (every second entry of the grade's 33³ — no interpolation lost making it), laid out as 5×4 cells of 17×17
    // (cell = blue slice, x = red, y = green): 85×68 entries. WPF stretches a shader's brush input to the element's size and its lookups land
    // half a texel either way, so an entry must cover a few screen pixels to be read exactly — 85×68 does on any monitor over ~255×204. The
    // export uses the same 17³ cube (Small), so both sides interpolate the same numbers.
    public const int Nodes = 17;
    const int CellsX = 5, CellsY = 4;
    static readonly Dictionary<string, ImageBrush> textures = new();
    static readonly Dictionary<string, Grade.Lut> smalls = new();
    /// <summary>The 17³ LUT an adjustment layer uses (preview shader and export cube alike).</summary>
    public static Grade.Lut Small(Grade.Lut lut)
    {
        lock (smalls)
        {
            if (smalls.TryGetValue(lut.Sig, out var s)) return s;
            int n = lut.Size, m = Nodes, step = (n - 1) / (m - 1);
            var d = new float[m * m * m * 3];
            for (int r = 0; r < m; r++) for (int g = 0; g < m; g++) for (int b = 0; b < m; b++)
                    {
                        int so = ((r * step * n + g * step) * n + b * step) * 3, o = ((r * m + g) * m + b) * 3;
                        d[o] = lut.D[so]; d[o + 1] = lut.D[so + 1]; d[o + 2] = lut.D[so + 2];
                    }
            if (smalls.Count > 32) smalls.Clear();
            return smalls[lut.Sig] = new Grade.Lut(d, m, lut.Sig + "/17");
        }
    }
    /// <summary>The preview's LUT texture (85×68, one pixel per entry; the element carrying the effect realises it nearest-neighbour).</summary>
    public static ImageBrush Texture(Grade.Lut lut)
    {
        if (textures.TryGetValue(lut.Sig, out var b)) return b;
        var sm = lut.Size == Nodes ? lut : Small(lut);
        int m = Nodes, w = CellsX * m, h = CellsY * m;
        var px = new byte[w * h * 4];
        for (int bl = 0; bl < m; bl++)
            for (int g = 0; g < m; g++)
                for (int r = 0; r < m; r++)
                {
                    int d = ((r * m + g) * m + bl) * 3, x = bl % CellsX * m + r, y = bl / CellsX * m + g, i = (y * w + x) * 4;
                    px[i] = Q(sm.D[d + 2]); px[i + 1] = Q(sm.D[d + 1]); px[i + 2] = Q(sm.D[d]); px[i + 3] = 255;
                }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4); bmp.Freeze();
        b = new ImageBrush(bmp) { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.NearestNeighbor);
        b.Freeze();
        if (textures.Count > 32) textures.Clear();
        textures[lut.Sig] = b;
        return b;
    }
    static byte Q(float v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);

    const string Hlsl = @"
sampler2D input : register(s0);
sampler2D lut : register(s1);
float4 k : register(c0);                       // x = strength
float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 c = tex2D(input, uv);
    float a = max(c.a, 0.002);
    float3 rgb = saturate(c.rgb / a);           // straight colour (WPF hands premultiplied pixels)
    float3 s = rgb * 16;
    float3 f = frac(s);
    float3 i0 = s - f;
    float3 i1 = min(i0 + 1, 16);
    float2 bb = float2(i0.b, i1.b) / 5 + 0.0001;
    float2 cy = floor(bb);
    float2 cx = floor((bb - cy) * 5 + 0.01);
    float4 o = float4(cx.x, cy.x, cx.y, cy.y) * 17 + 0.5;   // the two slices' first entry (+ half an entry)
    float4 r = float4(i0.r, i1.r, i0.g, i1.g);
    float2 sz = float2(1.0 / 85, 1.0 / 68);
    float3 a00 = tex2D(lut, (o.xy + r.xz) * sz).rgb;
    float3 a10 = tex2D(lut, (o.xy + r.yz) * sz).rgb;
    float3 a01 = tex2D(lut, (o.xy + r.xw) * sz).rgb;
    float3 a11 = tex2D(lut, (o.xy + r.yw) * sz).rgb;
    float3 b00 = tex2D(lut, (o.zw + r.xz) * sz).rgb;
    float3 b10 = tex2D(lut, (o.zw + r.yz) * sz).rgb;
    float3 b01 = tex2D(lut, (o.zw + r.xw) * sz).rgb;
    float3 b11 = tex2D(lut, (o.zw + r.yw) * sz).rgb;
    float3 sa = lerp(lerp(a00, a10, f.r), lerp(a01, a11, f.r), f.g);
    float3 sb = lerp(lerp(b00, b10, f.r), lerp(b01, b11, f.r), f.g);
    float3 g = lerp(sa, sb, f.b);
    return float4(lerp(rgb, g, k.x) * c.a, c.a);
}";
    static PixelShader? shader; static bool tried;
    public static string? CompileError;
    public static PixelShader? Shader()
    {
        if (tried) return shader;
        tried = true;
        try
        {
            var code = Blend.Compile(Hlsl, "ps_2_0");
            var ps = new PixelShader(); ps.SetStreamSource(new MemoryStream(code)); ps.Freeze();
            shader = ps;
        }
        catch (Exception ex) { CompileError = ex.Message; }
        return shader;
    }

    // ---------------- export ----------------
    /// <summary>The filter chain from the composite's frames under the clip ([in], 0-based, the clip's length) to the adjusted frames ([out],
    /// gbrap): blur (transparent outside the frame, strength-scaled radius per frame), the colour mixed by strength, the vignette at strength.
    /// cmd: a sendcmd file for the per-frame values when they change; inputs: extra inputs (the vignette PNG) appended by the caller.</summary>
    public static string Graph(Clip c, string tag, string inLabel, string outLabel, int w, int h, double scale, string? cube, string? vignettePng, int vignetteInput,
                               List<string> temps)
    {
        double fps = TimeUtil.Fps;
        int n = Math.Max(1, (int)Math.Round(c.Dur * fps));
        var s = new double[n]; var rad = new double[n];
        for (int k = 0; k < n; k++) { double t = c.Start + k / fps; s[k] = Strength(c, t); rad[k] = Radius(c, t); }
        bool varies = s.Any(v => Math.Abs(v - s[0]) > 1e-4) || rad.Any(v => Math.Abs(v - rad[0]) > 1e-3);
        double maxR = rad.Max();
        var sb = new StringBuilder();
        string F(double d) => d.ToString("0.#####", CultureInfo.InvariantCulture);
        string cur = inLabel;
        string? cmdFile = null;
        if (varies)
        {
            var cmd = new StringBuilder();
            for (int k = 0; k < n; k++)
            {
                cmd.Append(F(k / fps)).Append(" [enter] colorchannelmixer@as").Append(tag).Append(" aa ").Append(F(s[k])).Append(';');
                if (maxR > 0.05) cmd.Append(" gblur@ab").Append(tag).Append(" sigma ").Append(F(rad[k] > 0.05 ? Exporter.BlurSigma(rad[k] * scale) : 0.01)).Append(';');
                if (vignettePng != null) cmd.Append(" colorchannelmixer@av").Append(tag).Append(" aa ").Append(F(s[k])).Append(';');
                cmd.Append('\n');
            }
            cmdFile = $"cutpro_adj_{Guid.NewGuid().ToString("N")[..8]}_{tag}.txt";
            File.WriteAllText(Path.Combine(Path.GetTempPath(), cmdFile), cmd.ToString()); temps.Add(Path.Combine(Path.GetTempPath(), cmdFile));
        }
        sb.Append($"[{cur}]format=gbrap" + (cmdFile != null ? $",sendcmd=f={cmdFile}" : ""));
        if (maxR > 0.05)
        {
            // like WPF's BlurEffect on the canvas: transparent beyond the frame, blurred premultiplied
            double sigma = Exporter.BlurSigma(maxR * scale);
            int pad = 2 * (int)Math.Ceiling((3 * sigma + 2) / 2);
            sb.Append($",pad=iw+{2 * pad}:ih+{2 * pad}:{pad}:{pad}:color=black@0,gblur@ab{tag}=sigma={F(Exporter.BlurSigma(rad[0] * scale > 0.05 ? rad[0] * scale : 0.01))},crop={w}:{h}:{pad}:{pad},unpremultiply=inplace=1");
        }
        sb.Append($"[ab{tag}]\n");
        cur = $"ab{tag}";
        if (cube != null)
        {
            // colour: mixed s of the way to the grade (the LUT on straight colours; the alpha a blur left stays)
            sb.Append($"[{cur}]split[am{tag}][ag{tag}]\n");
            sb.Append($"[ag{tag}]lut3d=file={cube}:interp=trilinear,colorchannelmixer@as{tag}=aa={F(s[0])}[al{tag}]\n");
            sb.Append($"[am{tag}][al{tag}]overlay=format=rgb[ac{tag}]\n");
            cur = $"ac{tag}";
        }
        if (vignettePng != null)
        {
            sb.Append($"[{vignetteInput}:v]format=rgba,colorchannelmixer@av{tag}=aa={F(s[0])}[avg{tag}]\n");
            sb.Append($"[{cur}][avg{tag}]overlay=format=rgb:shortest=1[avo{tag}]\n");
            cur = $"avo{tag}";
        }
        sb.Append($"[{cur}]format=gbrap[{outLabel}]\n");
        return sb.ToString();
    }
}

/// <summary>The preview's adjustment colour: the tracks below (Input) through the grade's LUT (<see cref="Adjust.Texture"/>), mixed by Strength.</summary>
public sealed class LutEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(LutEffect), 0, SamplingMode.NearestNeighbor);
    public static readonly DependencyProperty LutProperty = RegisterPixelShaderSamplerProperty("Lut", typeof(LutEffect), 1, SamplingMode.NearestNeighbor);
    public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register("Strength", typeof(double), typeof(LutEffect), new UIPropertyMetadata(1.0, PixelShaderConstantCallback(0)));
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Brush Lut { get => (Brush)GetValue(LutProperty); set => SetValue(LutProperty, value); }
    public double Strength { get => (double)GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }
    public string Sig = "";
    public LutEffect(PixelShader shader)
    {
        PixelShader = shader;
        UpdateShaderValue(InputProperty); UpdateShaderValue(LutProperty); UpdateShaderValue(StrengthProperty);
    }
}
