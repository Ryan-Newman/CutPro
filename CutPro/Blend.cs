using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace CutPro;

/// <summary>Premiere's per-channel blend modes (Opacity → Blend Mode). Dissolve, Darker / Lighter Color and the HSL modes are not here.</summary>
public enum BlendMode
{
    Normal, Darken, Multiply, ColorBurn, LinearBurn, Lighten, Screen, ColorDodge, LinearDodge, Overlay, SoftLight, HardLight, VividLight,
    LinearLight, PinLight, HardMix, Difference, Exclusion, Subtract, Divide,
}

/// <summary>
/// Blend modes. Each mode is ONE formula on 8-bit values (t = the clip's colour, b = the picture below it, both 0…255, result rounded to
/// an integer) written three ways from the same definition: <see cref="Reference"/> (C#, for tests), <see cref="Expr"/> (ffmpeg lut2 —
/// the export) and <see cref="Hlsl"/> (the preview's pixel shader, <see cref="BlendEffect"/>). The clip's alpha (fades, opacity, key,
/// crop, masks) then mixes the result over the picture below: out = b + (B(t, b) − b)·α.
/// </summary>
public static class Blend
{
    public static readonly (BlendMode mode, string name, int group)[] Modes =
    {
        (BlendMode.Normal, "Normal", 0),
        (BlendMode.Darken, "Darken", 1), (BlendMode.Multiply, "Multiply", 1), (BlendMode.ColorBurn, "Color Burn", 1), (BlendMode.LinearBurn, "Linear Burn", 1),
        (BlendMode.Lighten, "Lighten", 2), (BlendMode.Screen, "Screen", 2), (BlendMode.ColorDodge, "Color Dodge", 2), (BlendMode.LinearDodge, "Linear Dodge (Add)", 2),
        (BlendMode.Overlay, "Overlay", 3), (BlendMode.SoftLight, "Soft Light", 3), (BlendMode.HardLight, "Hard Light", 3), (BlendMode.VividLight, "Vivid Light", 3),
        (BlendMode.LinearLight, "Linear Light", 3), (BlendMode.PinLight, "Pin Light", 3), (BlendMode.HardMix, "Hard Mix", 3),
        (BlendMode.Difference, "Difference", 4), (BlendMode.Exclusion, "Exclusion", 4), (BlendMode.Subtract, "Subtract", 4), (BlendMode.Divide, "Divide", 4),
    };
    public static string Name(BlendMode m) => Modes.First(x => x.mode == m).name;

    static int R(double v) => (int)Math.Floor(v + 0.5);
    static int Burn(int b, int s) => b == 255 ? 255 : s == 0 ? 0 : Math.Max(0, 255 - R((255.0 - b) * 255 / s));
    static int Dodge(int b, int s) => b == 0 ? 0 : s == 255 ? 255 : Math.Min(255, R(b * 255.0 / (255 - s)));

    /// <summary>The formula: t = the clip's channel value, b = the value below (0…255).</summary>
    public static int Reference(BlendMode m, int t, int b) => m switch
    {
        BlendMode.Darken => Math.Min(t, b),
        BlendMode.Multiply => R(t * b / 255.0),
        BlendMode.ColorBurn => Burn(b, t),
        BlendMode.LinearBurn => Math.Max(0, t + b - 255),
        BlendMode.Lighten => Math.Max(t, b),
        BlendMode.Screen => 255 - R((255 - t) * (255 - b) / 255.0),
        BlendMode.ColorDodge => Dodge(b, t),
        BlendMode.LinearDodge => Math.Min(255, t + b),
        BlendMode.Overlay => b < 128 ? R(2.0 * t * b / 255) : 255 - R(2.0 * (255 - t) * (255 - b) / 255),
        BlendMode.SoftLight => SoftLight(t, b),
        BlendMode.HardLight => t < 128 ? R(2.0 * t * b / 255) : 255 - R(2.0 * (255 - t) * (255 - b) / 255),
        BlendMode.VividLight => t < 128 ? Burn(b, 2 * t) : Dodge(b, 2 * t - 255),
        BlendMode.LinearLight => Math.Clamp(b + 2 * t - 255, 0, 255),
        BlendMode.PinLight => t < 128 ? Math.Min(b, 2 * t) : Math.Max(b, 2 * t - 255),
        BlendMode.HardMix => t + b >= 255 ? 255 : 0,
        BlendMode.Difference => Math.Abs(t - b),
        BlendMode.Exclusion => t + b - R(2.0 * t * b / 255),
        BlendMode.Subtract => Math.Max(0, b - t),
        BlendMode.Divide => t == 0 ? (b == 0 ? 0 : 255) : Math.Min(255, R(b * 255.0 / t)),
        _ => t,
    };
    // W3C soft light (s = t/255, d = b/255)
    static int SoftLight(int t, int b)
    {
        double s = t / 255.0, d = b / 255.0, r;
        if (t <= 127) r = d - (1 - 2 * s) * d * (1 - d);
        else { double D = b <= 63 ? ((16 * d - 12) * d + 4) * d : Math.Sqrt(d); r = d + (2 * s - 1) * (D - d); }
        return Math.Clamp(R(r * 255), 0, 255);
    }

    /// <summary>ffmpeg lut2 expression (x = the clip = first input, y = the picture below = second input).</summary>
    public static string Expr(BlendMode m)
    {
        const string burn = "if(eq(y,255),255,if(eq(S,0),0,max(0,255-floor((255-y)*255/S+0.5))))";
        const string dodge = "if(eq(y,0),0,if(eq(S,255),255,min(255,floor(y*255/(255-S)+0.5))))";
        return m switch
        {
            BlendMode.Darken => "min(x,y)",
            BlendMode.Multiply => "floor(x*y/255+0.5)",
            BlendMode.ColorBurn => burn.Replace("S", "x"),
            BlendMode.LinearBurn => "max(0,x+y-255)",
            BlendMode.Lighten => "max(x,y)",
            BlendMode.Screen => "255-floor((255-x)*(255-y)/255+0.5)",
            BlendMode.ColorDodge => dodge.Replace("S", "x"),
            BlendMode.LinearDodge => "min(255,x+y)",
            BlendMode.Overlay => "if(lt(y,128),floor(2*x*y/255+0.5),255-floor(2*(255-x)*(255-y)/255+0.5))",
            BlendMode.SoftLight => "clip(floor(255*if(lte(x,127),y/255-(1-2*x/255)*(y/255)*(1-y/255),y/255+(2*x/255-1)*(if(lte(y,63),((16*y/255-12)*y/255+4)*y/255,sqrt(y/255))-y/255))+0.5),0,255)",
            BlendMode.HardLight => "if(lt(x,128),floor(2*x*y/255+0.5),255-floor(2*(255-x)*(255-y)/255+0.5))",
            BlendMode.VividLight => $"if(lt(x,128),{burn.Replace("S", "(2*x)")},{dodge.Replace("S", "(2*x-255)")})",
            BlendMode.LinearLight => "clip(y+2*x-255,0,255)",
            BlendMode.PinLight => "if(lt(x,128),min(y,2*x),max(y,2*x-255))",
            BlendMode.HardMix => "if(gte(x+y,255),255,0)",
            BlendMode.Difference => "abs(x-y)",
            BlendMode.Exclusion => "x+y-floor(2*x*y/255+0.5)",
            BlendMode.Subtract => "max(0,y-x)",
            BlendMode.Divide => "if(eq(x,0),if(eq(y,0),0,255),min(255,floor(y*255/x+0.5)))",
            _ => "x",
        };
    }

    /// <summary>The same formula in HLSL on float3 t, b holding integers 0…255 (component-wise selects).</summary>
    public static string Hlsl(BlendMode m)
    {
        return m switch
        {
            BlendMode.Darken => "min(t, b)",
            BlendMode.Multiply => "Rn(t * b / 255)",
            BlendMode.ColorBurn => "Burn(b, t)",
            BlendMode.LinearBurn => "max(0, t + b - 255)",
            BlendMode.Lighten => "max(t, b)",
            BlendMode.Screen => "255 - Rn((255 - t) * (255 - b) / 255)",
            BlendMode.ColorDodge => "Dodge(b, t)",
            BlendMode.LinearDodge => "min(255, t + b)",
            BlendMode.Overlay => "b < 128 ? Rn(2 * t * b / 255) : 255 - Rn(2 * (255 - t) * (255 - b) / 255)",
            BlendMode.SoftLight => "Soft(t, b)",
            BlendMode.HardLight => "t < 128 ? Rn(2 * t * b / 255) : 255 - Rn(2 * (255 - t) * (255 - b) / 255)",
            BlendMode.VividLight => "t < 128 ? Burn(b, 2 * t) : Dodge(b, 2 * t - 255)",
            BlendMode.LinearLight => "clamp(b + 2 * t - 255, 0, 255)",
            BlendMode.PinLight => "t < 128 ? min(b, 2 * t) : max(b, 2 * t - 255)",
            BlendMode.HardMix => "t + b >= 255 ? 255 : 0",
            BlendMode.Difference => "abs(t - b)",
            BlendMode.Exclusion => "t + b - Rn(2 * t * b / 255)",
            BlendMode.Subtract => "max(0, b - t)",
            BlendMode.Divide => "t == 0 ? (b == 0 ? 0 : 255) : min(255, Rn(b * 255 / max(t, 1)))",
            _ => "t",
        };
    }

    // divisions get a small bias before rounding: results of the integer formulas never sit within 1/255 of a .5, so the bias only absorbs
    // the shader's reciprocal error
    const string ShaderLib = @"
sampler2D input : register(s0);
sampler2D below : register(s1);
float3 Rn(float3 v) { return floor(v + 0.5); }
float3 Burn(float3 b, float3 s) { return b == 255 ? 255 : (s == 0 ? 0 : max(0, 255 - floor((255 - b) * 255 / max(s, 1) + 0.5))); }
float3 Dodge(float3 b, float3 s) { return b == 0 ? 0 : (s == 255 ? 255 : min(255, floor(b * 255 / max(255 - s, 1) + 0.5))); }
float3 Soft(float3 t, float3 b)
{
    float3 s = t / 255, d = b / 255;
    float3 D = b <= 63 ? ((16 * d - 12) * d + 4) * d : sqrt(d);
    float3 r = t <= 127 ? d - (1 - 2 * s) * d * (1 - d) : d + (2 * s - 1) * (D - d);
    return clamp(floor(r * 255 + 0.5), 0, 255);
}
float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 s = tex2D(input, uv);
    float4 d = tex2D(below, uv);
    float a = s.a;
    float3 t = floor(saturate(s.rgb / max(a, 0.002)) * 255 + 0.5);   // straight colour of the clip (WPF hands premultiplied pixels)
    float3 b = floor(d.rgb * 255 + 0.5);                               // the picture below (over black)
    float3 r = (BLEND) / 255;
    return float4(r * a, a);                                           // premultiplied: drawn over the picture below = b + (r - b)·a
}";

    static readonly Dictionary<BlendMode, PixelShader?> shaders = new();
    public static string? CompileError;

    /// <summary>The preview's pixel shader for a mode, compiled once with Windows' own HLSL compiler (d3dcompiler_47.dll, ps_2_0 so WPF's
    /// software renderer runs it too). Null if it can't be compiled (the preview then shows the clip normally).</summary>
    public static PixelShader? Shader(BlendMode m)
    {
        if (shaders.TryGetValue(m, out var ps)) return ps;
        ps = null;
        try
        {
            var code = Compile(ShaderLib.Replace("BLEND", Hlsl(m)), "ps_2_0");
            ps = new PixelShader();
            ps.SetStreamSource(new MemoryStream(code));
            ps.Freeze();
        }
        catch (Exception ex) { CompileError = $"{m}: {ex.Message}"; ps = null; }
        shaders[m] = ps;
        return ps;
    }

    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ID3DBlob { [PreserveSig] nint GetBufferPointer(); [PreserveSig] nint GetBufferSize(); }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    static extern int D3DCompile(byte[] src, nint size, string? name, nint defines, nint include, string entry, string target, uint flags1, uint flags2,
                                 out ID3DBlob? code, out ID3DBlob? errors);

    internal static byte[] Compile(string hlsl, string target)
    {
        var src = Encoding.ASCII.GetBytes(hlsl);
        const uint Optimize3 = 1 << 15;
        int hr = D3DCompile(src, src.Length, "blend.hlsl", 0, 0, "main", target, Optimize3, 0, out var code, out var errors);
        try
        {
            if (hr < 0 || code == null)
            {
                string msg = errors != null ? Marshal.PtrToStringAnsi(errors.GetBufferPointer(), (int)errors.GetBufferSize()) ?? "" : $"HRESULT 0x{hr:X8}";
                throw new InvalidOperationException(msg.Trim());
            }
            var bytes = new byte[(int)code.GetBufferSize()];
            Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (code != null) Marshal.ReleaseComObject(code);
            if (errors != null) Marshal.ReleaseComObject(errors);
        }
    }
}

/// <summary>The preview's blend: drawn on a full-frame host holding the clip's layer; <see cref="Below"/> = a brush of the tracks below
/// (frame-sized, absolute viewbox), so both samplers line up pixel for pixel.</summary>
public sealed class BlendEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(BlendEffect), 0, SamplingMode.NearestNeighbor);
    public static readonly DependencyProperty BelowProperty = RegisterPixelShaderSamplerProperty("Below", typeof(BlendEffect), 1, SamplingMode.NearestNeighbor);
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Brush Below { get => (Brush)GetValue(BelowProperty); set => SetValue(BelowProperty, value); }
    public readonly BlendMode Mode;
    public BlendEffect(BlendMode mode, PixelShader shader)
    {
        Mode = mode; PixelShader = shader;
        UpdateShaderValue(InputProperty); UpdateShaderValue(BelowProperty);
    }
}
