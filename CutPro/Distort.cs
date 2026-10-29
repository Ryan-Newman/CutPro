using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace CutPro;

public enum DistortKind { Twirl, Wave, Spherize, Mesh, Roll }

/// <summary>A distortion of a clip's picture (null = none; not together with a corner pin or lens distortion): twirl (rotation that fades out
/// over a radius), wave (a travelling sine displacement), spherize (bulge / pinch inside a radius), a 4 × 4 mesh warp (drag the points on the
/// monitor), or rolling-shutter repair (row delay from a CMOS pan).</summary>
public sealed class Distort
{
    public DistortKind Kind { get; set; }
    /// <summary>Twirl: degrees at the centre (−720 … 720). Spherize: −90 (pinch) … 100 (bulge). Wave: amplitude, % of the picture's height.</summary>
    public double Amount { get; set; } = 90;
    /// <summary>Twirl / spherize: % of half the picture's diagonal.</summary>
    public double Radius { get; set; } = 50;
    /// <summary>Centre offset, % of the picture (−50 … 50).</summary>
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    /// <summary>Wave: length (% of the picture's width), direction (degrees: 0 = the wave runs left → right, displacing up / down), speed (waves a second).</summary>
    public double Wavelength { get; set; } = 25;
    public double Direction { get; set; }
    public double Speed { get; set; } = 1;
    /// <summary>Mesh: the 16 points' offsets (dx, dy as % of the picture's width / height), row by row from the top left.</summary>
    public double[] Mesh { get; set; } = new double[32];
    public Distort Clone() { var d = (Distort)MemberwiseClone(); d.Mesh = (double[])Mesh.Clone(); return d; }
}

/// <summary>
/// The distortions, written once in the picture's pixel grid (w × h at the clip's output size, pixel centres at whole numbers): for each output
/// pixel the source position s it shows. The export evaluates it with ffmpeg <c>geq</c> (bilinear: the source clamped to [0, w − 2] like geq's
/// sampler; outside the picture transparent), the preview with a pixel shader per kind doing the same arithmetic (<see cref="DistortEffect"/>).
/// </summary>
public static class Distorts
{
    public static bool Active(Clip c) => c.Distort != null && c.Kind is ClipKind.Video or ClipKind.Image && c.Pin == null && c.Lens == null;
    public static readonly AnimProp[] TwirlProps = { AnimProp.DistAmount, AnimProp.DistRadius, AnimProp.DistCX, AnimProp.DistCY };
    public static readonly AnimProp[] WaveProps = { AnimProp.DistAmount, AnimProp.DistWave, AnimProp.DistDir, AnimProp.DistSpeed, AnimProp.DistCX, AnimProp.DistCY };
    public static readonly AnimProp[] SpherizeProps = { AnimProp.DistAmount, AnimProp.DistRadius, AnimProp.DistCX, AnimProp.DistCY };
    public static readonly AnimProp[] RollProps = { AnimProp.DistAmount, AnimProp.DistDir };
    public static IEnumerable<AnimProp> PropsOf(DistortKind k) => k switch { DistortKind.Wave => WaveProps, DistortKind.Spherize => SpherizeProps, DistortKind.Twirl => TwirlProps, DistortKind.Roll => RollProps, _ => Array.Empty<AnimProp>() };
    public static bool Animated(Clip c) => Active(c) && c.Distort!.Kind != DistortKind.Mesh && Anim.AnyAnimated(c, PropsOf(c.Distort.Kind));
    /// <summary>The distortion at timeline time t (keyframes / formulas on amount, radius, centre, wave).</summary>
    public static Distort At(Clip c, double t)
    {
        var d = c.Distort!.Clone();
        if (!Animated(c)) return d;
        d.Amount = Anim.Value(c, AnimProp.DistAmount, t);
        d.Radius = Anim.Value(c, AnimProp.DistRadius, t);
        d.CenterX = Anim.Value(c, AnimProp.DistCX, t);
        d.CenterY = Anim.Value(c, AnimProp.DistCY, t);
        d.Wavelength = Anim.Value(c, AnimProp.DistWave, t);
        d.Direction = Anim.Value(c, AnimProp.DistDir, t);
        d.Speed = Anim.Value(c, AnimProp.DistSpeed, t);
        return d;
    }
    static string F(double v) => v.ToString("0.########", CultureInfo.InvariantCulture);
    /// <summary>A geq expression of T (seconds from the clip start) that follows the property's keys as piecewise linear segments.</summary>
    static string Piecewise(Clip c, AnimProp p)
    {
        var keys = Anim.Track(c, p)?.Keys;
        if (keys == null || keys.Count == 0) return F(Anim.Base(c, p));
        var k = keys.OrderBy(x => x.T).ToList();
        if (k.Count == 1) return F(k[0].V);
        string expr = F(k[^1].V);
        for (int i = k.Count - 2; i >= 0; i--)
        {
            double t0 = k[i].T - c.In, t1 = k[i + 1].T - c.In, dt = Math.Max(1e-6, t1 - t0);
            string lerp = $"{F(k[i].V)}+({F(k[i + 1].V)}-{F(k[i].V)})*(T-{F(t0)})/{F(dt)}";
            expr = $"if(lt(T,{F(t1)}),{lerp},{expr})";
        }
        return expr;
    }

    /// <summary>The picture-space constants of a distortion on a w × h picture.</summary>
    public static (double cx, double cy, double invR) Geometry(Distort d, int w, int h)
    {
        double cx = (0.5 + d.CenterX / 100) * (w - 1), cy = (0.5 + d.CenterY / 100) * (h - 1);
        double r = Math.Max(1, Math.Clamp(d.Radius, 1, 400) / 100 * Math.Sqrt((double)w * w + (double)h * h) / 2);
        return (cx, cy, 1 / r);
    }
    public static double Exponent(Distort d) => Math.Clamp(d.Amount, -90, 100) / 100;          // (spherize: source distance × u^k)

    /// <summary>The ffmpeg geq filter for a clip (keyframed parameters become expressions of T).</summary>
    public static string Geq(Clip c, int w, int h) => Animated(c) ? Geq(At(c, c.Start), w, h, p => Piecewise(c, p)) : Geq(c.Distort!, w, h);
    /// <summary>The ffmpeg geq filter (input and output gbrap) for a w × h picture. T = seconds since the clip's start (the wave's travel).</summary>
    public static string Geq(Distort d, int w, int h, Func<AnimProp, string>? expr = null)
    {
        var (cx, cy, invR) = Geometry(d, w, h);
        string Cx = expr != null ? $"(0.5+({expr(AnimProp.DistCX)})/100)*{w - 1}" : F(cx);
        string Cy = expr != null ? $"(0.5+({expr(AnimProp.DistCY)})/100)*{h - 1}" : F(cy);
        string InvR = expr != null ? $"(1/max(1,({expr(AnimProp.DistRadius)})/100*{F(Math.Sqrt((double)w * w + h * h) / 2)}))" : F(invR);
        string pos;   // stores the source position in ld(4), ld(5)
        switch (d.Kind)
        {
            case DistortKind.Twirl:
                string ang = expr != null ? $"(({expr(AnimProp.DistAmount)})*PI/180)" : F(Math.Clamp(d.Amount, -720, 720) * Math.PI / 180);
                pos = $"st(0,X-({Cx}));st(1,Y-({Cy}));st(2,max(0,1-hypot(ld(0),ld(1))*({InvR})));st(3,({ang})*ld(2)*ld(2));" +
                      $"st(4,({Cx})+ld(0)*cos(ld(3))+ld(1)*sin(ld(3)));st(5,({Cy})-ld(0)*sin(ld(3))+ld(1)*cos(ld(3)))";
                break;
            case DistortKind.Spherize:
                string exp = expr != null ? $"(({expr(AnimProp.DistAmount)})/100)" : F(Exponent(d));
                pos = $"st(0,X-({Cx}));st(1,Y-({Cy}));st(2,hypot(ld(0),ld(1))*({InvR}));st(3,if(lt(ld(2),1),pow(max(ld(2),0.001),{exp}),1));" +
                      $"st(4,({Cx})+ld(0)*ld(3));st(5,({Cy})+ld(1)*ld(3))";
                break;
            case DistortKind.Wave:
            {
                var (nx, ny, invL, amp, speed) = WaveOf(d, w, h);
                string Nx = expr != null ? $"cos(({expr(AnimProp.DistDir)})*PI/180)" : F(nx);
                string Ny = expr != null ? $"sin(({expr(AnimProp.DistDir)})*PI/180)" : F(ny);
                string InvL = expr != null ? $"(1/max(1,({expr(AnimProp.DistWave)})/100*{w}))" : F(invL);
                string Amp = expr != null ? $"(({expr(AnimProp.DistAmount)})/100*{h})" : F(amp);
                string Spd = expr != null ? $"({expr(AnimProp.DistSpeed)})" : F(speed);
                pos = $"st(0,({Amp})*sin(2*PI*((X*({Nx})+Y*({Ny}))*({InvL})-({Spd})*T)));st(4,X-({Ny})*ld(0));st(5,Y+({Nx})*ld(0))";
                break;
            }
            case DistortKind.Roll:
            {
                // CMOS readout delay along the scan axis (0° = top→bottom, shear sideways): row y was captured later, so a pan skews it — undo that shear
                string K = expr != null ? $"(({expr(AnimProp.DistAmount)})/100*{w - 1})" : F(Math.Clamp(d.Amount, -50, 50) / 100.0 * (w - 1));
                string Nx = expr != null ? $"cos(({expr(AnimProp.DistDir)})*PI/180)" : F(Math.Cos(d.Direction * Math.PI / 180));
                string Ny = expr != null ? $"sin(({expr(AnimProp.DistDir)})*PI/180)" : F(Math.Sin(d.Direction * Math.PI / 180));
                pos = $"st(0,{Nx});st(1,{Ny});st(2,({K})*((X*ld(1)+Y*ld(0))/(({w - 1})*ld(1)+({h - 1})*ld(0)+0.0001)-0.5));st(4,X-ld(0)*ld(2));st(5,Y-ld(1)*ld(2))";
                break;
            }
            default:
            {   // mesh: smoothstep tents per axis (they add up to 1), the displacement field D = Σ wy_j Σ wx_i d_ij; source = pixel − D
                string T(string u, int i) => $"(max(0,1-abs({u}-{i}))^2*(3-2*max(0,1-abs({u}-{i}))))";
                string ux = $"(3*X/{F(w - 1)})", uy = $"(3*Y/{F(h - 1)})";
                var sb = new StringBuilder();
                for (int i = 0; i < 4; i++) sb.Append($"st({i},{T(ux, i)});");
                for (int j = 0; j < 4; j++) sb.Append($"st({4 + j},{T(uy, j)});");
                string Sum(int comp, double scale)
                {
                    var rows = new List<string>();
                    for (int j = 0; j < 4; j++)
                        rows.Add($"ld({4 + j})*(" + string.Join("+", Enumerable.Range(0, 4).Select(i => $"ld({i})*{F(d.Mesh[(j * 4 + i) * 2 + comp] / 100 * scale)}")) + ")");
                    return string.Join("+", rows);
                }
                sb.Append($"st(8,{Sum(0, w)});st(9,{Sum(1, h)});st(4,X-ld(8));st(5,Y-ld(9))");
                pos = sb.ToString();
                break;
            }
        }
        string In = "between(ld(4),0,W-1)*between(ld(5),0,H-1)";
        string P(string f) => $"'{pos};if({In},{f}(ld(4),ld(5)),0)'";
        return $"geq=r={P("r")}:g={P("g")}:b={P("b")}:a={P("alpha")}";
    }
    /// <summary>Wave constants: direction, 1 / length (px), amplitude (px), speed (waves a second).</summary>
    public static (double nx, double ny, double invL, double amp, double speed) WaveOf(Distort d, int w, int h)
    {
        double a = d.Direction * Math.PI / 180;
        return (Math.Cos(a), Math.Sin(a), 1 / Math.Max(1, Math.Clamp(d.Wavelength, 0.5, 400) / 100 * w), Math.Clamp(d.Amount, -50, 50) / 100 * h, d.Speed);
    }

    // ---------------- the preview's shaders (ps_2_0: s = the source pixel; outside the picture transparent; clamp to w − 2 like geq) ----------------
    const string Common = @"
sampler2D input : register(s0);
float4 sz : register(c0);
float4 hc : register(c1);
float4 p : register(c2);
float4 q : register(c3);
float4 Pick(float2 s)
{
    float2 ok = step(0, s) * step(s, sz.xy - 1);
    float4 c = tex2D(input, (clamp(s, 0, sz.xy - 2) + hc.xy) * sz.zw);
    return c * (ok.x * ok.y);
}
";
    static readonly Dictionary<DistortKind, string> Bodies = new()
    {
        [DistortKind.Twirl] = @"
float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 x = uv * sz.xy - hc.xy;
    float2 d = x - hc.zw;
    float f = saturate(1 - length(d) * p.y);
    float a = p.x * f * f;
    float sn, cs; sincos(a, sn, cs);
    return Pick(hc.zw + float2(d.x * cs + d.y * sn, -d.x * sn + d.y * cs));
}",
        [DistortKind.Spherize] = @"
float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 x = uv * sz.xy - hc.xy;
    float2 d = x - hc.zw;
    float u = length(d) * p.y;
    float m = lerp(pow(max(u, 0.001), p.x), 1, step(1, u));
    return Pick(hc.zw + d * m);
}",
        [DistortKind.Wave] = @"
float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 x = uv * sz.xy - hc.xy;
    float s = p.w * sin(6.28318530718 * (dot(x, p.xy) * p.z - q.x));
    return Pick(x + float2(-p.y, p.x) * s);
}",
        [DistortKind.Mesh] = @"
float4 X0 : register(c4); float4 X1 : register(c5); float4 X2 : register(c6); float4 X3 : register(c7);
float4 Y0 : register(c8); float4 Y1 : register(c9); float4 Y2 : register(c10); float4 Y3 : register(c11);
float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 x = uv * sz.xy - hc.xy;
    float2 u = x / (sz.xy - 1) * 3;
    float4 tx = saturate(1 - abs(u.x - float4(0, 1, 2, 3))); tx = tx * tx * (3 - 2 * tx);
    float4 ty = saturate(1 - abs(u.y - float4(0, 1, 2, 3))); ty = ty * ty * (3 - 2 * ty);
    float4 rx = float4(dot(tx, X0), dot(tx, X1), dot(tx, X2), dot(tx, X3));
    float4 ry = float4(dot(tx, Y0), dot(tx, Y1), dot(tx, Y2), dot(tx, Y3));
    return Pick(x - float2(dot(ty, rx), dot(ty, ry)));
}",
        [DistortKind.Roll] = @"
float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 x = uv * sz.xy - hc.xy;
    float2 n = p.xy;
    float t = (x.x * n.y + x.y * n.x) / max((sz.x - 1) * n.y + (sz.y - 1) * n.x, 0.0001) - 0.5;
    return Pick(x - n * p.z * t);
}",
    };
    static readonly Dictionary<DistortKind, PixelShader?> shaders = new();
    public static string? CompileError;
    public static PixelShader? Shader(DistortKind k)
    {
        if (shaders.TryGetValue(k, out var s)) return s;
        try
        {
            var code = Blend.Compile(Common + Bodies[k], "ps_2_0");
            var ps = new PixelShader(); ps.SetStreamSource(new MemoryStream(code)); ps.Freeze();
            s = ps;
        }
        catch (Exception ex) { CompileError = ex.Message; s = null; }
        shaders[k] = s;
        return s;
    }
}

/// <summary>The preview's distortion on the picture element: the export's geq arithmetic per kind (<see cref="Distorts"/>).</summary>
public sealed class DistortEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(DistortEffect), 0, SamplingMode.Bilinear);
    static DependencyProperty C(string n, int i) => DependencyProperty.Register(n, typeof(Point4D), typeof(DistortEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(i)));
    public static readonly DependencyProperty[] Cs = Enumerable.Range(0, 12).Select(i => C("C" + i, i)).ToArray();
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public readonly DistortKind Kind;
    public DistortEffect(DistortKind k, PixelShader shader)
    {
        Kind = k; PixelShader = shader;
        UpdateShaderValue(InputProperty); foreach (var c in Cs) UpdateShaderValue(c);
    }
    /// <summary>Constants for a w × h picture at tau seconds since the clip's start; half: where the renderer puts pixel centres (<see cref="Pin.Half"/>).</summary>
    public void Set(Distort d, int w, int h, double half, double tau)
    {
        var (cx, cy, invR) = Distorts.Geometry(d, w, h);
        SetValue(Cs[0], new Point4D(w, h, 1.0 / w, 1.0 / h));
        SetValue(Cs[1], new Point4D(half, half, cx, cy));
        switch (d.Kind)
        {
            case DistortKind.Twirl: SetValue(Cs[2], new Point4D(Math.Clamp(d.Amount, -720, 720) * Math.PI / 180, invR, 0, 0)); break;
            case DistortKind.Spherize: SetValue(Cs[2], new Point4D(Distorts.Exponent(d), invR, 0, 0)); break;
            case DistortKind.Wave:
            {
                var (nx, ny, invL, amp, speed) = Distorts.WaveOf(d, w, h);
                SetValue(Cs[2], new Point4D(nx, ny, invL, amp));
                SetValue(Cs[3], new Point4D(speed * tau, 0, 0, 0));
                break;
            }
            case DistortKind.Roll:
            {
                double a = d.Direction * Math.PI / 180, k = Math.Clamp(d.Amount, -50, 50) / 100.0 * (w - 1);
                SetValue(Cs[2], new Point4D(Math.Cos(a), Math.Sin(a), k, 0));
                break;
            }
            default:
                for (int j = 0; j < 4; j++)
                {
                    double V(int i, int comp) => d.Mesh[(j * 4 + i) * 2 + comp] / 100 * (comp == 0 ? w : h);
                    SetValue(Cs[4 + j], new Point4D(V(0, 0), V(1, 0), V(2, 0), V(3, 0)));
                    SetValue(Cs[8 + j], new Point4D(V(0, 1), V(1, 1), V(2, 1), V(3, 1)));
                }
                break;
        }
    }
}

/// <summary>The mesh warp's points on the Program monitor (the selected clip, paused): drag a point to move the picture there; one undo step a drag.</summary>
public sealed class MeshOverlay : FrameworkElement
{
    public Editor Ed = null!;
    public bool Playing;
    public Action? Edited;
    public MeshOverlay() { Width = Editor.W; Height = Editor.H; Focusable = false; IsHitTestVisible = true; Editor.SequenceChanged += () => { Width = Editor.W; InvalidateVisual(); }; }
    Clip? Target => !Playing && Ed?.Selected is { } c && Distorts.Active(c) && c.Distort!.Kind == DistortKind.Mesh && Ed.Time >= c.Start - 1e-6 && Ed.Time < c.End ? c : null;

    Point P(Clip c, int i, int j)
    {
        var (x, y, w, h, _) = Editor.Placement(Ed.MediaOf(c), c, Math.Clamp(Ed.Time, c.Start, c.End - 1e-6));
        var m = c.Distort!.Mesh; int k = (j * 4 + i) * 2;
        double px = x + w * (i / 3.0 + m[k] / 100), py = y + h * (j / 3.0 + m[k + 1] / 100);
        return new Point(c.FlipH ? x + x + w - px : px, c.FlipV ? y + y + h - py : py);
    }
    double Unit()
    {
        if (PresentationSource.FromVisual(this) is not { RootVisual: Visual root } || !root.IsAncestorOf(this)) return 1;
        var t = TransformToAncestor(root);
        double s = (t.Transform(new Point(100, 0)) - t.Transform(new Point(0, 0))).Length / 100;
        return s > 1e-6 ? 1 / s : 1;
    }
    int Hit(Point pt)
    {
        if (Target is not { } c) return -1;
        double r = 9 * Unit();
        for (int k = 0; k < 16; k++) if ((P(c, k % 4, k / 4) - pt).Length <= r) return k;
        return -1;
    }
    protected override HitTestResult? HitTestCore(PointHitTestParameters p) => drag >= 0 || Hit(p.HitPoint) >= 0 ? new PointHitTestResult(this, p.HitPoint) : null;
    int drag = -1; Point start; double dx0, dy0;
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Target is not { } c) return;
        var pt = e.GetPosition(this); int k = Hit(pt); if (k < 0) return;
        drag = k; start = pt; dx0 = c.Distort!.Mesh[k * 2]; dy0 = c.Distort.Mesh[k * 2 + 1];
        Ed.Checkpoint("Mesh Warp"); CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (drag < 0 || Target is not { } c) { Cursor = Hit(e.GetPosition(this)) >= 0 ? Cursors.Hand : null; return; }
        var (_, _, w, h, _) = Editor.Placement(Ed.MediaOf(c), c, Math.Clamp(Ed.Time, c.Start, c.End - 1e-6));
        var d = e.GetPosition(this) - start;
        c.Distort!.Mesh[drag * 2] = Math.Round(dx0 + (c.FlipH ? -d.X : d.X) / Math.Max(1, w) * 100, 2);
        c.Distort.Mesh[drag * 2 + 1] = Math.Round(dy0 + (c.FlipV ? -d.Y : d.Y) / Math.Max(1, h) * 100, 2);
        Ed.RaiseProps(); Edited?.Invoke(); InvalidateVisual();
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (drag < 0) return;
        drag = -1; ReleaseMouseCapture(); Ed.Commit(); Edited?.Invoke(); InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (Target is not { } c) return;
        double u = Unit();
        var line = new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), 1 * u); line.Freeze();
        for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++)
        {
            if (i < 3) dc.DrawLine(line, P(c, i, j), P(c, i + 1, j));
            if (j < 3) dc.DrawLine(line, P(c, i, j), P(c, i, j + 1));
        }
        for (int k = 0; k < 16; k++)
        {
            var p = P(c, k % 4, k / 4);
            dc.DrawEllipse(k == drag ? Brushes.Gold : Brushes.White, new Pen(Brushes.Black, 1 * u), p, 5 * u, 5 * u);
        }
    }
}
