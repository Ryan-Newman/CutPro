using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace CutPro;

/// <summary>Corner pin: where the picture's four corners go in the frame, % of the frame (null on the clip = off). With a pin the clip
/// covers the whole frame layer and its motion is the pin. These are the base corners; keyframes live on <see cref="Pin.Props"/>.</summary>
public sealed class CornerPin
{
    public double TLX { get; set; }
    public double TLY { get; set; }
    public double TRX { get; set; } = 100;
    public double TRY { get; set; }
    public double BLX { get; set; }
    public double BLY { get; set; } = 100;
    public double BRX { get; set; } = 100;
    public double BRY { get; set; } = 100;
    public CornerPin Clone() => (CornerPin)MemberwiseClone();
    /// <summary>By index: TLX TLY TRX TRY BLX BLY BRX BRY (the order of <see cref="Pin.Props"/>).</summary>
    public double this[int i]
    {
        get => i switch { 0 => TLX, 1 => TLY, 2 => TRX, 3 => TRY, 4 => BLX, 5 => BLY, 6 => BRX, _ => BRY };
        set { switch (i) { case 0: TLX = value; break; case 1: TLY = value; break; case 2: TRX = value; break; case 3: TRY = value; break;
                           case 4: BLX = value; break; case 5: BLY = value; break; case 6: BRX = value; break; default: BRY = value; break; } }
    }
}

/// <summary>
/// Corner pin. The export warps with ffmpeg perspective (a 1 px transparent pad stretched with the picture onto the pins; colours and alpha
/// warped apart, perspective keeps no alpha); keyframed corners become per-frame expressions of the frame number. The preview warps the
/// picture with <see cref="PinEffect"/>, a pixel shader doing the same geometry and edge (so dragging a corner shows at once) — except for a
/// track matte, whose matte comes from the decoder: it keeps its base corners and its decoder runs the export's graph.
/// </summary>
public static class Pin
{
    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static string G(double v) => v.ToString("0.##########", CultureInfo.InvariantCulture);
    public static readonly AnimProp[] Props = { AnimProp.PinTLX, AnimProp.PinTLY, AnimProp.PinTRX, AnimProp.PinTRY, AnimProp.PinBLX, AnimProp.PinBLY, AnimProp.PinBRX, AnimProp.PinBRY };
    public static readonly string[] Corners = { "Top left", "Top right", "Bottom left", "Bottom right" };
    public static bool Active(Clip c) => c.Pin != null && c.Kind is ClipKind.Video or ClipKind.Image;
    /// <summary>Keyframed (or formula) corners. A track matte keeps its base corners (its preview matte comes from the decoder).</summary>
    public static bool Animated(Clip c) => Active(c) && c.Mask == MaskMode.None && Anim.AnyAnimated(c, Props);
    /// <summary>The corners at timeline time t (the base corners unless animated). Only for clips with a pin.</summary>
    public static CornerPin At(Clip c, double t)
    {
        if (!Animated(c)) return c.Pin!;
        var p = new CornerPin();
        for (int i = 0; i < 8; i++) p[i] = Anim.Value(c, Props[i], t);
        return p;
    }
    public static string Sig(CornerPin? p) => p == null ? "" : FormattableString.Invariant($"{p.TLX}|{p.TLY}|{p.TRX}|{p.TRY}|{p.BLX}|{p.BLY}|{p.BRX}|{p.BRY}");

    // ---------------- export (and the matte decoder) ----------------
    /// <summary>The warp alone for a w×h picture or matte plane: a 1 px black pad (so the outside is black / 0), corners sent to the pins.</summary>
    public static string Warp(CornerPin p, int w, int h)
    {
        var q = new string[8];
        for (int i = 0; i < 8; i++) q[i] = F(p[i] / 100 * (i % 2 == 0 ? w : h) + 1);
        return Warp(q, w, h, false);
    }
    static string Warp(string[] q, int w, int h, bool perFrame) =>
        $"pad=iw+2:ih+2:1:1:color=black,perspective=x0={q[0]}:y0={q[1]}:x1={q[2]}:y1={q[3]}:x2={q[4]}:y2={q[5]}:x3={q[6]}:y3={q[7]}"
        + $":sense=destination:interpolation=linear{(perFrame ? ":eval=frame" : "")},crop={w}:{h}:1:1";
    /// <summary>The export's warp for clip c at w×h: its corners, or — keyframed — per-frame expressions of perspective's frame number
    /// (in = k + 1 for the clip's frame k, at c.Start + k / fps, the time the preview evaluates for that frame).</summary>
    public static string Warp(Clip c, int w, int h)
    {
        if (!Animated(c)) return Warp(c.Pin!, w, h);
        int n = Math.Max(1, (int)Math.Round(c.Dur * TimeUtil.Fps)) + 1;
        var v = new double[8][];
        for (int i = 0; i < 8; i++) v[i] = new double[n];
        for (int k = 0; k < n; k++)
        {
            var p = At(c, c.Start + k / TimeUtil.Fps);
            for (int i = 0; i < 8; i++) v[i][k] = p[i] / 100 * (i % 2 == 0 ? w : h) + 1;
        }
        return Warp(v.Select(a => "'" + Expr(a) + "'").ToArray(), w, h, true);
    }
    /// <summary>The picture stretched to the frame layer, colours and alpha (white, or the key's matte) warped separately and merged —
    /// outside the pins transparent. tag keeps labels apart.</summary>
    public static string Graph(Clip c, int w, int h, string tag = "") => Graph(Warp(c, w, h), w, h, tag);
    public static string Graph(CornerPin p, int w, int h, string tag = "") => Graph(Warp(p, w, h), w, h, tag);
    static string Graph(string wp, int w, int h, string tag) =>
        $"scale={w}:{h},setsar=1,format=rgba,split[pc{tag}][pa{tag}];[pa{tag}]alphaextract,{wp}[pm{tag}];[pc{tag}]{wp}[pw{tag}];[pw{tag}][pm{tag}]alphamerge";

    /// <summary>Per-frame values v[k] as one ffmpeg expression of in (= k + 1): runs on a straight line (within 0.001) become a + b·in,
    /// joined by a balanced if(lt(in, …)) tree (only the taken branch is evaluated).</summary>
    public static string Expr(double[] v)
    {
        var seg = Segments(v);
        string Leaf((int k0, double a, double b) s) => Math.Abs(s.b) < 1e-12 ? G(s.a) : $"{G(s.a)}{(s.b < 0 ? "-" : "+")}{G(Math.Abs(s.b))}*in";
        string Node(int i, int j)
        {
            if (j - i == 1) return Leaf(seg[i]);
            int m = (i + j) / 2;
            return $"if(lt(in,{seg[m].k0 + 1}),{Node(i, m)},{Node(m, j)})";
        }
        return Node(0, seg.Count);
    }
    /// <summary>Straight runs of v: from frame k0 on, value = a + b·(k + 1).</summary>
    public static List<(int k0, double a, double b)> Segments(double[] v)
    {
        var seg = new List<(int, double, double)>();
        int n = v.Length, s = 0;
        while (s < n)
        {
            double b = 0; int e = s + 1;
            if (s + 1 < n)
            {
                b = v[s + 1] - v[s]; e = s + 2;
                while (e < n && Math.Abs(v[s] + b * (e - s) - v[e]) < 1e-3) e++;
            }
            if (Math.Abs(b) < 1e-9) b = 0;
            seg.Add((s, v[s] - b * (s + 1), b));
            s = e;
        }
        return seg;
    }
    /// <summary>Evaluates <see cref="Segments"/> at frame k (tests).</summary>
    public static double SegmentValue(List<(int k0, double a, double b)> seg, int k)
    {
        var s = seg.Last(x => x.k0 <= k);
        return s.a + s.b * (k + 1);
    }

    // ---------------- preview ----------------
    /// <summary>The export's geometry as one matrix for <see cref="PinEffect"/>: output position (u, v in 0…1 over the frame) → the picture's
    /// pixel in index units (pixel centres on integers). perspective maps the padded source (w+2)×(h+2) with its corners at 0 and w+2 onto the
    /// pins + 1 and samples destination pixel x of the padded output (x = output pixel + 1 = u·w + ½) — so source index = padded − 1.</summary>
    public static double[] Inverse(CornerPin p, int w, int h, double du = 0.5)
    {
        double X(double v) => v / 100 * w + 1;
        double Y(double v) => v / 100 * h + 1;
        // unit square → quad (Heckbert): (0,0) TL, (1,0) TR, (1,1) BR, (0,1) BL
        double x0 = X(p.TLX), y0 = Y(p.TLY), x1 = X(p.TRX), y1 = Y(p.TRY), x2 = X(p.BRX), y2 = Y(p.BRY), x3 = X(p.BLX), y3 = Y(p.BLY);
        double dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3, dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;
        double a, b, c, d, e, f, g = 0, hh = 0;
        double del = dx1 * dy2 - dx2 * dy1;
        if ((Math.Abs(dx3) > 1e-9 || Math.Abs(dy3) > 1e-9) && Math.Abs(del) > 1e-12)
        {
            g = (dx3 * dy2 - dx2 * dy3) / del; hh = (dx1 * dy3 - dx3 * dy1) / del;
        }
        a = x1 - x0 + g * x1; b = x3 - x0 + hh * x3; c = x0;
        d = y1 - y0 + g * y1; e = y3 - y0 + hh * y3; f = y0;
        // inverse (adjugate: scale doesn't matter for homogeneous coordinates)
        double[] q = { e - f * hh, c * hh - b, b * f - c * e,
                       f * g - d, a - c * g, c * d - a * f,
                       d * hh - e * g, b * g - a * hh, a * e - b * d };
        // M = T · Q⁻¹ · A: A = (u·w + ½, v·h + ½, 1), T = (s·(w+2) − 1, t·(h+2) − 1, 1)
        double[] qa = new double[9];
        for (int r = 0; r < 3; r++)
        {
            qa[r * 3] = q[r * 3] * w; qa[r * 3 + 1] = q[r * 3 + 1] * h;
            qa[r * 3 + 2] = q[r * 3] * du + q[r * 3 + 1] * du + q[r * 3 + 2];
        }
        var m = new double[9];
        for (int col = 0; col < 3; col++)
        {
            m[col] = (w + 2) * qa[col] - qa[6 + col];
            m[3 + col] = (h + 2) * qa[3 + col] - qa[6 + col];
            m[6 + col] = qa[6 + col];
        }
        double mx = m.Max(Math.Abs);                                       // (moderate magnitudes for the shader's floats)
        if (mx > 0) for (int i = 0; i < 9; i++) m[i] /= mx;
        return m;
    }
    /// <summary>m read from the mirrored output position: pixel x ↔ w − 1 − x, i.e. u → 1 − u + (2·half − 1)/w (the export flips after the pin).</summary>
    public static double[] Mirror(double[] m, bool fx, bool fy, int w, int h, double half)
    {
        if (!fx && !fy) return m;
        var r = (double[])m.Clone();
        for (int row = 0; row < 3; row++)
        {
            if (fx) { r[row * 3 + 2] += m[row * 3] * (1 + (2 * half - 1) / w); r[row * 3] = -m[row * 3]; }
            if (fy) { r[row * 3 + 2] += m[row * 3 + 1] * (1 + (2 * half - 1) / h); r[row * 3 + 1] = -m[row * 3 + 1]; }
        }
        return r;
    }
    // ps_2_0: s = the picture's pixel for this output pixel (the pin's matrix, or uv on the pixel grid); coverage ramps over the pad (1 px, as
    // perspective's clamped bilinear does) and the colour fades towards the pad's black by the same weight — straight colour c·k, alpha k →
    // premultiplied c·k², k (the export, exactly). Lens (to.w = 1) before the pin: lenscorrection's bilinear source position for its output pixel s
    // is centre + m·(s − centre) + ½ (its rounding constant also lands in the fraction), m = 1 + k1·r² + k2·r⁴, r² = |s − centre|²·4/(w² + h²);
    // outside [0, w) × [0, h) transparent (its fill), the far edge clamped
    const string Hlsl = @"
sampler2D input : register(s0);
float4 r0 : register(c0);
float4 r1 : register(c1);
float4 r2 : register(c2);
float4 sz : register(c3);
float4 to : register(c4);
float4 lc : register(c5);
float4 lr : register(c6);
float4 main(float2 uv : TEXCOORD) : COLOR
{
    float3 q = float3(uv, 1);
    float2 s = float2(dot(r0.xyz, q), dot(r1.xyz, q)) / dot(r2.xyz, q);
    float2 e = saturate(min(s + 1, sz.xy - s));
    float k = lerp(1, e.x * e.y, to.z);
    float2 d = s - lc.xy;
    float r = dot(d, d) * lr.x;
    float2 t = lerp(s, lc.xy + (1 + lc.z * r + lc.w * r * r) * d + 0.5, to.w);
    float2 ok = step(0, t) * step(t, sz.xy - lr.y);
    k *= lerp(1, ok.x * ok.y, to.w);
    float4 c = tex2D(input, (clamp(t, 0, sz.xy - 1) + to.xy) * sz.zw);
    return float4(c.rgb * (k * k), c.a * k);
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
    /// <summary>The preview's decoder runs the pin (the export's graph, base corners): track mattes, or no shader compiler on this PC.</summary>
    public static bool InDecoder(Clip c) => Active(c) && (c.Mask != MaskMode.None || Shader() == null);
    public static bool InShader(Clip c) => Active(c) && !InDecoder(c);
    /// <summary>Where WPF puts pixel and texel centres for a pixel shader: on screen (GPU) uv = (x + ½)/w and texel i at (i + ½)/w; the software
    /// renderer (every RenderTargetBitmap: tests, screenshots) uv = x/w and texel i at i/w — 0. Measured by --compositetest (screen probe).</summary>
    public static double Half = 0.5;
}

/// <summary>The preview's corner pin on the picture element (frame-sized): <see cref="Pin.Inverse"/> rows in c0…c2, (w, h, 1/w, 1/h) in c3
/// with w×h the output size (the export's pixel grid).</summary>
public sealed class PinEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(PinEffect), 0, SamplingMode.Bilinear);
    public static readonly DependencyProperty R0Property = DependencyProperty.Register("R0", typeof(Point4D), typeof(PinEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty R1Property = DependencyProperty.Register("R1", typeof(Point4D), typeof(PinEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(1)));
    public static readonly DependencyProperty R2Property = DependencyProperty.Register("R2", typeof(Point4D), typeof(PinEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(2)));
    public static readonly DependencyProperty SzProperty = DependencyProperty.Register("Sz", typeof(Point4D), typeof(PinEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(3)));
    public static readonly DependencyProperty ToProperty = DependencyProperty.Register("To", typeof(Point4D), typeof(PinEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(4)));
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public static readonly DependencyProperty LcProperty = DependencyProperty.Register("Lc", typeof(Point4D), typeof(PinEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(5)));
    public static readonly DependencyProperty LrProperty = DependencyProperty.Register("Lr", typeof(Point4D), typeof(PinEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(6)));
    public PinEffect(PixelShader shader)
    {
        PixelShader = shader;
        UpdateShaderValue(InputProperty); UpdateShaderValue(R0Property); UpdateShaderValue(R1Property); UpdateShaderValue(R2Property); UpdateShaderValue(SzProperty); UpdateShaderValue(ToProperty);
        UpdateShaderValue(LcProperty); UpdateShaderValue(LrProperty);
    }
    public void Set(CornerPin p, int w, int h) => SetMatrix(Pin.Inverse(p, w, h, 1 - Pin.Half), w, h, Pin.Half);
    /// <summary>The identity matrix for a picture without a pin: uv → pixel index uv·size − ½ on the GPU (uv·size in the software renderer).</summary>
    public static double[] Plain(int w, int h, double half) => new double[] { w, 0, -half, 0, h, -half, 0, 0, 1 };
    /// <summary>m: uv → the picture's pixel (homogeneous rows); half: where the renderer puts pixel and texel centres (see <see cref="Pin.Half"/>);
    /// pin = coverage ramps (off: m is <see cref="Plain"/>); lens = (k1, k2, cx, cy) of <see cref="Lens.At"/> or null.</summary>
    public void SetMatrix(double[] m, int w, int h, double half, bool pin = true, (double k1, double k2, double cx, double cy)? lens = null)
    {
        SetValue(R0Property, new Point4D(m[0], m[1], m[2], 0));
        SetValue(R1Property, new Point4D(m[3], m[4], m[5], 0));
        SetValue(R2Property, new Point4D(m[6], m[7], m[8], 0));
        SetValue(SzProperty, new Point4D(w, h, 1.0 / w, 1.0 / h));
        SetValue(ToProperty, new Point4D(half, half, pin ? 1 : 0, lens != null ? 1 : 0));
        var l = lens ?? (0, 0, 0.5, 0.5);
        SetValue(LcProperty, new Point4D(Math.Floor(l.cx * w), Math.Floor(l.cy * h), l.k1, l.k2));  // (lenscorrection's centre: int xcenter = cx·w)
        SetValue(LrProperty, new Point4D(4.0 / ((double)w * w + (double)h * h), 1.0 / 4096, 0, 0));    // (y: t < w, inside float precision)
    }
}
