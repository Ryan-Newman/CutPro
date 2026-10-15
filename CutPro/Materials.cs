using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// Procedural surface textures for the material themes (Frosted Glass, Stone, Metals, Nature): seamless 512 px tiles built
/// from periodic gradient noise, Worley cells and drawn shapes (grass blades, leaves), plus full-size backdrops. Generated once
/// (in parallel), then loaded from %LOCALAPPDATA%\CutPro\Textures. Palette values "tex:name" (tiled) / "img:name" (stretched)
/// become brushes through <see cref="ThemeManager.MakeBrush"/>.
/// </summary>
public static class Materials
{
    const int Size = 512, Version = 5;
    static readonly Dictionary<string, BitmapSource> cache = new();
    static readonly object gate = new();

    public static readonly string[] Names =
    {
        "frost", "frost2", "aurora",
        "marble", "graphite", "obsidian", "sapphire", "lapis", "rosequartz", "slate", "basalt",
        "gold", "silver", "platinum", "copper", "bronze", "gunmetal",
        "grass", "bark", "dirt", "moss", "leaves", "sand", "water", "forest",
    };

    /// <summary>"tex:x" → tiled brush, "img:x" → stretched (UniformToFill) brush.</summary>
    public static Brush Brush(string spec)
    {
        bool tiled = spec.StartsWith("tex:");
        string name = spec[4..];
        var src = Get(name);
        var b = tiled ? new ImageBrush(src) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, src.PixelWidth, src.PixelHeight), Stretch = Stretch.None }
                      : new ImageBrush(src) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
        RenderOptions.SetCachingHint(b, CachingHint.Cache);
        b.Freeze();
        return b;
    }

    /// <summary>Mean colour (for "C." colour resources, theme previews).</summary>
    public static Color Average(string name)
    {
        var src = Get(name);
        int w = src.PixelWidth, h = src.PixelHeight, step = 7;
        var px = new byte[w * h * 4]; src.CopyPixels(px, w * 4, 0);
        double r = 0, g = 0, b = 0, a = 0; int n = 0;
        for (int i = 0; i < px.Length; i += 4 * step) { b += px[i]; g += px[i + 1]; r += px[i + 2]; a += px[i + 3]; n++; }
        return Color.FromArgb((byte)(a / n), (byte)Math.Min(255, r / n * 255 / Math.Max(1, a / n)), (byte)Math.Min(255, g / n * 255 / Math.Max(1, a / n)), (byte)Math.Min(255, b / n * 255 / Math.Max(1, a / n)));
    }

    public static BitmapSource Get(string name)
    {
        lock (gate) if (cache.TryGetValue(name, out var c)) return c;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro", "Textures");
        var file = Path.Combine(dir, $"{name}_v{Version}.png");
        BitmapSource? bmp = null;
        try
        {
            if (File.Exists(file))
            {
                var d = BitmapFrame.Create(new Uri(file), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                bmp = d.Format == PixelFormats.Pbgra32 ? d : new FormatConvertedBitmap(d, PixelFormats.Pbgra32, null, 0);
            }
        }
        catch { bmp = null; }
        if (bmp == null)
        {
            bmp = Make(name);
            try
            {
                Directory.CreateDirectory(dir);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = File.Create(file + ".part"); enc.Save(fs); fs.Close();
                File.Move(file + ".part", file, true);
            }
            catch { }
        }
        bmp.Freeze();
        lock (gate) cache[name] = bmp;
        return bmp;
    }

    /// <summary>CutPro.exe --materials out.png: every texture as a labelled swatch (generation time in the label).</summary>
    public static void Sheet(string path)
    {
        const int cell = 300, cols = 5;
        int rows = (Names.Length + cols - 1) / cols;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 24, 24)), null, new Rect(0, 0, cols * cell, rows * cell));
            for (int i = 0; i < Names.Length; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var b = Brush((Names[i] == "aurora" ? "img:" : "tex:") + Names[i]);
                long ms = sw.ElapsedMilliseconds;
                var r = new Rect(i % cols * cell + 6, i / cols * cell + 6, cell - 12, cell - 12);
                dc.DrawRoundedRectangle(b, new Pen(Brushes.Black, 1), r, 12, 12);
                var ft = new FormattedText($"{Names[i]}  {ms} ms", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                           new Typeface("Segoe UI Semibold"), 15, Brushes.White, 1.0);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, new Rect(r.X + 8, r.Y + 8, ft.Width + 12, ft.Height + 4), 4, 4);
                dc.DrawText(ft, new Point(r.X + 14, r.Y + 10));
            }
        }
        var rtb = new RenderTargetBitmap(cols * cell, rows * cell, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path); enc.Save(fs);
    }

    static BitmapSource Make(string name) => name switch
    {
        // glass
        "frost" => Frost(0.10f), "frost2" => Frost(0.16f), "aurora" => Aurora(),
        // stone
        "marble" => Marble(), "graphite" => Graphite(), "obsidian" => Obsidian(), "sapphire" => Sapphire(), "lapis" => Lapis(),
        "rosequartz" => RoseQuartz(), "slate" => Slate(), "basalt" => Basalt(),
        // metals (base, light)
        "gold" => Brushed(0xC4A04A, 0xF4DE8E, 3), "silver" => Brushed(0xB7BBC2, 0xF0F2F5, 5), "platinum" => Brushed(0xCFCFCB, 0xF6F6F3, 7),
        "copper" => Brushed(0x83462A, 0xC98458, 11), "bronze" => Brushed(0x6E5230, 0xAE8550, 13), "gunmetal" => Brushed(0x24272C, 0x4A505A, 17),
        // nature
        "grass" => Grass(), "bark" => Bark(), "dirt" => Dirt(), "moss" => Moss(), "leaves" => Leaves(), "sand" => Sand(), "water" => Water(), "forest" => Forest(),
        _ => Pixels((u, v) => new C4(1, 0, 1)),
    };

    // ======================= noise =======================
    static readonly float[] gx = new float[256], gy = new float[256];
    static Materials()
    {
        for (int i = 0; i < 256; i++) { double a = i * Math.PI * 2 / 256; gx[i] = (float)Math.Cos(a); gy[i] = (float)Math.Sin(a); }
    }
    static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }
    static int Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)seed * 2246822519u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return (int)(h ^ (h >> 16));
        }
    }
    static float Rand(int x, int y, int seed) => (Hash(x, y, seed) & 0xFFFFFF) / 16777216f;
    static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
    static float Lerp(float a, float b, float t) => a + (b - a) * t;
    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    static float Smooth(float e0, float e1, float x) { float t = Clamp01((x - e0) / (e1 - e0)); return t * t * (3 - 2 * t); }

    /// <summary>Gradient noise, periodic with period (px, py) lattice cells; ≈ −1..1.</summary>
    static float Perlin(float x, float y, int px, int py, int seed)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        int X0 = Mod(x0, px), X1 = Mod(x0 + 1, px), Y0 = Mod(y0, py), Y1 = Mod(y0 + 1, py);
        int h00 = Hash(X0, Y0, seed) & 255, h10 = Hash(X1, Y0, seed) & 255, h01 = Hash(X0, Y1, seed) & 255, h11 = Hash(X1, Y1, seed) & 255;
        float n00 = gx[h00] * fx + gy[h00] * fy, n10 = gx[h10] * (fx - 1) + gy[h10] * fy;
        float n01 = gx[h01] * fx + gy[h01] * (fy - 1), n11 = gx[h11] * (fx - 1) + gy[h11] * (fy - 1);
        float u = Fade(fx), v = Fade(fy);
        return Lerp(Lerp(n00, n10, u), Lerp(n01, n11, u), v) * 1.41f;
    }

    /// <summary>Fractal noise over the tile (u, v in 0..1): fx × fy cells at the first octave, doubling; turbulence = |noise|.</summary>
    static float Fbm(float u, float v, int fx, int fy, int oct, int seed, bool turb = false, float gain = 0.5f)
    {
        float sum = 0, amp = 1, norm = 0;
        for (int o = 0; o < oct; o++)
        {
            float n = Perlin(u * fx, v * fy, fx, fy, seed + o * 101);
            sum += amp * (turb ? MathF.Abs(n) : n); norm += amp; amp *= gain; fx *= 2; fy *= 2;
        }
        return sum / norm;
    }

    /// <summary>Periodic Worley (cellular) noise: nearest / second-nearest feature distance in cell units, nearest cell id and its feature point offset.</summary>
    static (float f1, float f2, int id, float dx, float dy) Worley(float u, float v, int cells, int seed)
    {
        float x = u * cells, y = v * cells;
        int cx = (int)MathF.Floor(x), cy = (int)MathF.Floor(y);
        float f1 = 9, f2 = 9; int id = 0; float bdx = 0, bdy = 0;
        for (int j = -1; j <= 1; j++)
            for (int i = -1; i <= 1; i++)
            {
                int gxi = cx + i, gyi = cy + j, wx = Mod(gxi, cells), wy = Mod(gyi, cells);
                float fx = gxi + Rand(wx, wy, seed), fy = gyi + Rand(wx, wy, seed + 7);
                float dx = x - fx, dy = y - fy, d = MathF.Sqrt(dx * dx + dy * dy);
                if (d < f1) { f2 = f1; f1 = d; id = Hash(wx, wy, seed + 13); bdx = dx; bdy = dy; } else if (d < f2) f2 = d;
            }
        return (f1, f2, id, bdx, bdy);
    }

    // ======================= image helpers =======================
    readonly record struct C4(float R, float G, float B, float A = 1);
    static C4 Hex(int rgb, float k = 1) => new(((rgb >> 16) & 255) / 255f * k, ((rgb >> 8) & 255) / 255f * k, (rgb & 255) / 255f * k);
    static C4 Mix(C4 a, C4 b, float t) { t = Clamp01(t); return new(Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t), Lerp(a.A, b.A, t)); }
    static C4 Mul(C4 a, float k) => new(a.R * k, a.G * k, a.B * k, a.A);
    static C4 Add(C4 a, float k) => new(a.R + k, a.G + k, a.B + k, a.A);

    /// <summary>Per-pixel tile (straight colour + alpha → premultiplied BGRA), rows in parallel.</summary>
    static BitmapSource Pixels(Func<float, float, C4> f, int w = Size, int h = Size) => Pixels((x, y) => f((x + 0.5f) / w, (y + 0.5f) / h), w, h, true);
    static BitmapSource Pixels(Func<int, int, C4> f, int w, int h, bool _)
    {
        var px = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                var c = f(x, y);
                float a = Clamp01(c.A);
                int i = (y * w + x) * 4;
                px[i] = (byte)(Clamp01(c.B) * a * 255 + 0.5f); px[i + 1] = (byte)(Clamp01(c.G) * a * 255 + 0.5f);
                px[i + 2] = (byte)(Clamp01(c.R) * a * 255 + 0.5f); px[i + 3] = (byte)(a * 255 + 0.5f);
            }
        });
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Shapes drawn with WPF on a tile; each shape is drawn at every wrap offset so the tile is seamless. post(px) adjusts pixels.</summary>
    static BitmapSource Drawn(Color bg, Action<DrawingContext, Action<Action<double, double>>> draw, Func<int, int, C4, C4>? post = null)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, Size, Size));
            draw(dc, shape => { for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) shape(i * Size, j * Size); });
        }
        var rtb = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv);
        if (post == null) { rtb.Freeze(); return rtb; }
        var px = new byte[Size * Size * 4]; rtb.CopyPixels(px, Size * 4, 0);
        return Pixels((x, y) =>
        {
            int i = (y * Size + x) * 4;
            return post(x, y, new C4(px[i + 2] / 255f, px[i + 1] / 255f, px[i] / 255f, 1));
        }, Size, Size, true);
    }

    // ======================= glass =======================
    /// <summary>Lightly frosted glass: translucent white with a fine, uneven frost grain.</summary>
    static BitmapSource Frost(float level) => Pixels((x, y) =>
    {
        float u = (x + 0.5f) / Size, v = (y + 0.5f) / Size;
        float a = level + 0.03f * Fbm(u, v, 6, 6, 4, 5) + 0.02f * (Rand(x, y, 9) - 0.5f);
        if (Rand(x, y, 3) > 0.9965f) a += 0.10f;
        return new C4(1, 1, 1, a);
    }, Size, Size, true);

    /// <summary>The coloured light behind the glass panels: soft aurora glows on deep navy (1600 × 1000, stretched).</summary>
    static BitmapSource Aurora()
    {
        const int W = 1600, H = 1000;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(10, 14, 36), Color.FromRgb(20, 26, 58), 90), null, new Rect(0, 0, W, H));
            void Glow(double cx, double cy, double r, Color c, byte a)
            {
                var g = new RadialGradientBrush(Color.FromArgb(a, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
                g.GradientStops.Insert(1, new GradientStop(Color.FromArgb((byte)(a * 0.45), c.R, c.G, c.B), 0.5));
                dc.DrawEllipse(g, null, new Point(cx * W, cy * H), r * W, r * W * 0.75);
            }
            Glow(0.12, 0.22, 0.42, Color.FromRgb(0, 194, 199), 170);
            Glow(0.62, 0.12, 0.48, Color.FromRgb(123, 77, 255), 170);
            Glow(0.88, 0.78, 0.40, Color.FromRgb(255, 79, 154), 150);
            Glow(0.30, 0.95, 0.34, Color.FromRgb(255, 179, 71), 120);
            Glow(0.50, 0.55, 0.36, Color.FromRgb(47, 123, 255), 120);
            Glow(0.95, 0.20, 0.22, Color.FromRgb(0, 229, 255), 90);
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv);
        var src = new byte[W * H * 4]; rtb.CopyPixels(src, W * 4, 0);
        return Pixels((x, y) =>
        {   // a little grain so the soft gradients don't band
            int i = (y * W + x) * 4; float n = (Rand(x, y, 21) - 0.5f) * 0.018f;
            return new C4(src[i + 2] / 255f + n, src[i + 1] / 255f + n, src[i] / 255f + n);
        }, W, H, true);
    }

    // ======================= stone =======================
    static BitmapSource Marble() => Pixels((u, v) =>
    {
        float t = Fbm(u, v, 2, 2, 5, 11, turb: true, gain: 0.45f);
        float vein = MathF.Abs(MathF.Sin(MathF.PI * 2 * (u * 2 + v * 1 + t * 2.2f)));
        float vein2 = MathF.Abs(MathF.Sin(MathF.PI * 2 * (-u * 1 + v * 3 + Fbm(u, v, 3, 3, 4, 23, turb: true, gain: 0.45f) * 1.6f)));
        float line = MathF.Pow(1 - vein, 9), line2 = MathF.Pow(1 - vein2, 28) * 0.55f;
        var bas = Mix(Hex(0xF5F3EF), Hex(0xE3DFD8), Fbm(u, v, 3, 3, 4, 31) * 0.9f + 0.45f);
        bas = Add(bas, 0.012f * Fbm(u, v, 32, 32, 2, 37));
        return Mix(bas, Hex(0x8E9298), line * 0.5f + line2 * 0.3f + Smooth(0.7f, 1, 1 - vein) * 0.12f);   // soft enough to read text on
    });

    static BitmapSource Graphite() => Pixels((x, y) =>
    {
        float u = (x + 0.5f) / Size, v = (y + 0.5f) / Size;
        float mott = Fbm(u, v, 3, 3, 4, 41) * 0.05f, grain = Perlin(u * 96, v * 96, 96, 96, 43) * 0.025f;
        float sheen = MathF.Sin(MathF.PI * 2 * (u + v + Fbm(u, v, 2, 2, 3, 47) * 0.6f)) * 0.035f;
        var c = Add(Hex(0x2C2E33), mott + grain + sheen + (Rand(x, y, 49) - 0.5f) * 0.02f);
        if (Rand(x, y, 51) > 0.9975f) c = Add(c, 0.12f);
        return c;
    }, Size, Size, true);

    static BitmapSource Obsidian() => Pixels((u, v) =>
    {
        float n = Fbm(u, v, 2, 2, 4, 63), m = Fbm(u, v, 3, 3, 3, 67);
        var w = Worley(u, v, 4, 61);
        float edge = MathF.Exp(-(w.f2 - w.f1) * 30) * 0.035f;
        float gloss = MathF.Pow(Clamp01(0.5f + 0.5f * MathF.Sin(MathF.PI * 2 * (u * 1 + v * 2 + n * 0.8f))), 6) * 0.06f;
        float b = 0.07f + edge + gloss;
        return new C4(b + 0.012f * n, b + 0.004f * m, b + 0.02f + 0.015f * (n + m) * 0.5f);
    });

    static BitmapSource Sapphire() => Pixels((x, y) =>
    {
        float u = (x + 0.5f) / Size, v = (y + 0.5f) / Size;
        var w = Worley(u, v, 7, 71);
        float s = (w.id & 255) / 255f, la = ((w.id >> 8) & 255) / 255f * MathF.PI * 2;
        var c = Mix(Hex(0x081650), Hex(0x1C46B4), s * 0.8f + 0.1f);
        float facet = (w.dx * MathF.Cos(la) + w.dy * MathF.Sin(la)) * 0.35f;
        c = Mul(c, 1 + facet);
        c = Mix(c, Hex(0x6E9BFF), MathF.Exp(-(w.f2 - w.f1) * 28) * 0.45f);
        c = Add(c, Fbm(u, v, 4, 4, 3, 73) * 0.02f);
        if (Rand(x, y, 77) > 0.9988f) c = Mix(c, Hex(0xFFFFFF), 0.8f);
        return c;
    }, Size, Size, true);

    static BitmapSource Lapis() => Pixels((x, y) =>
    {
        float u = (x + 0.5f) / Size, v = (y + 0.5f) / Size;
        var c = Mix(Hex(0x14256F), Hex(0x2B47AE), Fbm(u, v, 3, 3, 5, 81) * 0.9f + 0.5f);
        float calcite = Smooth(0.18f, 0.45f, Fbm(u, v, 3, 2, 5, 83)) * Smooth(0.0f, 0.3f, Fbm(u, v, 6, 6, 3, 84) + 0.15f);
        c = Mix(c, Hex(0xB9C2D8), calcite * 0.55f);
        var w = Worley(u, v, 26, 85);
        if (w.f1 < 0.16f && (w.id & 15) == 0) c = Mix(c, Hex(0xD9B54E), 0.8f * (1 - w.f1 / 0.16f));
        return Add(c, (Rand(x, y, 87) - 0.5f) * 0.02f);
    }, Size, Size, true);

    static BitmapSource RoseQuartz() => Pixels((u, v) =>
    {
        var c = Mix(Hex(0xE7C2C9), Hex(0xF5E2E6), Fbm(u, v, 3, 3, 5, 91) * 0.9f + 0.5f);
        c = Mix(c, Hex(0xFBF4F5), Smooth(0.15f, 0.55f, Fbm(u, v, 2, 2, 4, 93)) * 0.55f);
        var w = Worley(u, v, 4, 95);
        c = Mix(c, Hex(0xFFFFFF), MathF.Exp(-(w.f2 - w.f1) * 45) * 0.22f);
        return Add(c, Fbm(u, v, 24, 24, 2, 97) * 0.012f);
    });

    static BitmapSource Slate() => Pixels((x, y) =>
    {
        float u = (x + 0.5f) / Size, v = (y + 0.5f) / Size;
        float layers = MathF.Sin(MathF.PI * 2 * (v * 7 + 0.45f * Fbm(u, v, 2, 4, 3, 107))) * 0.022f + Perlin(u * 4, v * 64, 4, 64, 109) * 0.012f;
        var c = Add(Hex(0x32353A), Fbm(u, v, 3, 3, 5, 101) * 0.04f + layers + (Rand(x, y, 103) - 0.5f) * 0.025f);
        var w = Worley(u, v, 60, 105);
        if (w.f1 < 0.22f && (w.id & 31) < 2) c = Mix(c, Hex(0x7C8189), 0.3f);
        return c;
    }, Size, Size, true);

    static BitmapSource Basalt() => Pixels((x, y) =>
    {
        float u = (x + 0.5f) / Size, v = (y + 0.5f) / Size;
        var c = Add(Hex(0x111215), Fbm(u, v, 4, 4, 5, 111) * 0.025f + (Rand(x, y, 113) - 0.5f) * 0.02f);
        if (Rand(x, y, 115) > 0.996f) c = Add(c, 0.08f);
        return c;
    }, Size, Size, true);

    // ======================= metals =======================
    /// <summary>Brushed metal: long horizontal streaks, broad anisotropic sheen, fine grain.</summary>
    static BitmapSource Brushed(int baseRgb, int lightRgb, int seed) => Pixels((x, y) =>
    {
        float u = (x + 0.5f) / Size, v = (y + 0.5f) / Size;
        float streak = 0.55f * Perlin(u * 3, v * 160, 3, 160, seed) + 0.30f * Perlin(u * 6, v * 320, 6, 320, seed + 1) + 0.15f * Perlin(u * 12, v * 48, 12, 48, seed + 2);
        float sheen = Fbm(u, v, 1, 2, 3, seed + 3) * 0.9f + 0.35f * MathF.Sin(MathF.PI * 2 * (v * 1 + u * 1 + 0.3f * Fbm(u, v, 2, 2, 2, seed + 4)));
        var c = Mul(Hex(baseRgb), 1 + 0.085f * streak + (Rand(x, y, seed + 5) - 0.5f) * 0.05f);
        c = Mix(c, Hex(lightRgb), Clamp01(sheen) * 0.38f);
        c = Mul(c, 1 - Clamp01(-sheen) * 0.18f);
        return c;
    }, Size, Size, true);

    // ======================= nature =======================
    static BitmapSource Grass()
    {
        var rnd = new Random(121);
        Color[] greens = { Color.FromRgb(38, 74, 26), Color.FromRgb(52, 96, 32), Color.FromRgb(66, 116, 40), Color.FromRgb(86, 134, 48), Color.FromRgb(104, 146, 56), Color.FromRgb(46, 84, 30), Color.FromRgb(120, 140, 62) };
        return Drawn(Color.FromRgb(22, 40, 16), (dc, wrap) =>
        {
            for (int i = 0; i < 3400; i++)
            {
                double x = rnd.NextDouble() * Size, y = rnd.NextDouble() * Size, h = 26 + rnd.NextDouble() * 58, wdt = 2.2 + rnd.NextDouble() * 3.2;
                double bend = (rnd.NextDouble() - 0.5) * 30, lean = (rnd.NextDouble() - 0.5) * 10;
                var col = greens[rnd.Next(greens.Length)];
                double shade = 0.75 + rnd.NextDouble() * 0.4;
                var brush = new LinearGradientBrush(Color.FromRgb((byte)(col.R * shade * 0.55), (byte)(col.G * shade * 0.55), (byte)(col.B * shade * 0.55)),
                                                    Color.FromRgb((byte)Math.Min(255, col.R * shade), (byte)Math.Min(255, col.G * shade), (byte)Math.Min(255, col.B * shade)), 90) { MappingMode = BrushMappingMode.RelativeToBoundingBox };
                brush.StartPoint = new Point(0, 1); brush.EndPoint = new Point(0, 0); brush.Freeze();
                wrap((ox, oy) =>
                {
                    double bx = x + ox, by = y + oy;
                    if (bx < -80 || bx > Size + 80 || by < -10 || by - h > Size + 10) return;
                    var g = new StreamGeometry();
                    using (var sc = g.Open())
                    {
                        sc.BeginFigure(new Point(bx - wdt / 2, by), true, true);
                        sc.QuadraticBezierTo(new Point(bx + lean, by - h * 0.55), new Point(bx + bend, by - h), true, false);
                        sc.QuadraticBezierTo(new Point(bx + lean + wdt * 0.3, by - h * 0.5), new Point(bx + wdt / 2, by), true, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(brush, null, g);
                });
            }
        }, (x, y, c) => Mul(c, 0.82f));
    }

    /// <summary>Tree bark: broad vertical plates split by deep, branching fissures, fibrous grain, a few cross cracks.</summary>
    static BitmapSource Bark() => Pixels((x, y) =>
    {
        float u = (x + 0.5f) / Size, v = (y + 0.5f) / Size;
        float warp = Fbm(u, v, 2, 1, 3, 131);
        float n1 = Perlin(u * 7 + warp * 2.2f, v * 1.5f, 7, 1, 133), n2 = Perlin(u * 14 + warp * 3f, v * 3, 14, 3, 135);
        float fissure = MathF.Pow(1 - MathF.Abs(n1), 7) + 0.6f * MathF.Pow(1 - MathF.Abs(n2), 10);
        float plate = Fbm(u, v, 4, 2, 3, 137) * 0.5f + 0.5f;
        float fibre = Perlin(u * 96, v * 6, 96, 6, 139) * 0.07f + Perlin(u * 48, v * 3, 48, 3, 141) * 0.05f;
        var c = Mix(Hex(0x4E3828), Hex(0x86664A), plate * 0.85f);
        c = Mul(c, 1 + fibre);
        // plates are lit on their left, shadowed towards the fissure on the right
        float slope = Perlin(u * 7 + 0.02f + warp * 2.2f, v * 1.5f, 7, 1, 133) - n1;
        c = Mul(c, 1 + Math.Clamp(slope * 6, -0.25f, 0.25f));
        float crack = MathF.Pow(1 - MathF.Abs(Perlin(u * 5, v * 18 + warp * 2, 5, 18, 143)), 24) * Smooth(0.15f, 0.5f, Fbm(u, v, 3, 3, 2, 145));
        c = Mix(c, Hex(0x150E09), Clamp01(fissure * 0.9f + crack * 0.6f));
        return Add(c, (Rand(x, y, 147) - 0.5f) * 0.03f);
    }, Size, Size, true);

    /// <summary>Rich soil: crumbly dark earth, darker damp patches, scattered small flat stones and grit.</summary>
    static BitmapSource Dirt() => Pixels((x, y) =>
    {
        float u = (x + 0.5f) / Size, v = (y + 0.5f) / Size;
        var c = Mix(Hex(0x261A12), Hex(0x4A3424), Fbm(u, v, 4, 4, 6, 151) * 0.9f + 0.5f);
        c = Mul(c, 1 - 0.25f * Smooth(0.1f, 0.5f, Fbm(u, v, 2, 2, 4, 152)));
        float crumb = Fbm(u, v, 48, 48, 2, 153);
        c = Mul(c, 1 + crumb * 0.18f + (Rand(x, y, 154) - 0.5f) * 0.12f);
        float wu = u + 0.02f * Fbm(u, v, 8, 8, 2, 156), wv = v + 0.02f * Fbm(u, v, 8, 8, 2, 157);
        var w = Worley(wu - MathF.Floor(wu), wv - MathF.Floor(wv), 18, 155);
        float r = 0.14f + ((w.id >> 4) & 15) / 15f * 0.12f;
        if ((w.id & 7) == 0 && w.f1 < r)
        {
            float t = w.f1 / r, light = Clamp01(0.5f - (w.dx * 0.6f + w.dy * 0.8f) / r * 0.6f);
            var stone = Mix(Hex(0x544638), Hex(0x6E6252), ((w.id >> 8) & 255) / 255f);
            stone = Mul(stone, 0.85f + 0.25f * light);
            c = Mix(stone, c, Smooth(0.7f, 1f, t));
        }
        if (Rand(x, y, 158) > 0.994f) c = Add(c, 0.08f);
        return c;
    }, Size, Size, true);

    static BitmapSource Moss() => Pixels((x, y) =>
    {
        float u = (x + 0.5f) / Size, v = (y + 0.5f) / Size;
        float n = Fbm(u, v, 20, 20, 4, 161), m = Fbm(u, v, 3, 3, 4, 163);
        var c = Mix(Hex(0x1E3214), Hex(0x5E8234), Smooth(-0.35f, 0.55f, n + m * 0.5f));
        c = Mix(c, Hex(0x8BA64A), Smooth(0.45f, 0.75f, n) * 0.4f);
        return Add(c, (Rand(x, y, 165) - 0.5f) * 0.03f);
    }, Size, Size, true);

    static BitmapSource Leaves()
    {
        var rnd = new Random(171);
        Color[] cols = { Color.FromRgb(47, 90, 30), Color.FromRgb(62, 122, 42), Color.FromRgb(79, 143, 51), Color.FromRgb(106, 158, 60), Color.FromRgb(138, 154, 58),
                         Color.FromRgb(156, 122, 46), Color.FromRgb(56, 104, 36), Color.FromRgb(92, 130, 44) };
        return Drawn(Color.FromRgb(24, 36, 18), (dc, wrap) =>
        {
            for (int i = 0; i < 520; i++)
            {
                double x = rnd.NextDouble() * Size, y = rnd.NextDouble() * Size, len = 24 + rnd.NextDouble() * 26, wd = len * (0.36 + rnd.NextDouble() * 0.14), ang = rnd.NextDouble() * 360;
                var col = cols[rnd.Next(cols.Length)]; double sh = 0.7 + rnd.NextDouble() * 0.45;
                var fill = new SolidColorBrush(Color.FromRgb((byte)Math.Min(255, col.R * sh), (byte)Math.Min(255, col.G * sh), (byte)Math.Min(255, col.B * sh))); fill.Freeze();
                var rib = new Pen(new SolidColorBrush(Color.FromArgb(110, 20, 30, 10)), 1); rib.Freeze();
                var edge = new Pen(new SolidColorBrush(Color.FromArgb(90, 10, 16, 6)), 0.8); edge.Freeze();
                wrap((ox, oy) =>
                {
                    double bx = x + ox, by = y + oy;
                    if (bx < -60 || bx > Size + 60 || by < -60 || by > Size + 60) return;
                    dc.PushTransform(new RotateTransform(ang, bx, by));
                    var g = new StreamGeometry();
                    using (var sc = g.Open())
                    {
                        sc.BeginFigure(new Point(bx - len / 2, by), true, true);
                        sc.QuadraticBezierTo(new Point(bx, by - wd), new Point(bx + len / 2, by), true, false);
                        sc.QuadraticBezierTo(new Point(bx, by + wd), new Point(bx - len / 2, by), true, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(fill, edge, g);
                    dc.DrawLine(rib, new Point(bx - len / 2, by), new Point(bx + len / 2 - 2, by));
                    dc.Pop();
                });
            }
        }, (x, y, c) => Mul(c, 0.72f));
    }

    static BitmapSource Sand() => Pixels((x, y) =>
    {
        float u = (x + 0.5f) / Size, v = (y + 0.5f) / Size;
        float ripple = MathF.Sin(MathF.PI * 2 * (v * 9 + u * 1 + 0.7f * Fbm(u, v, 2, 2, 3, 181)));
        var c = Mix(Hex(0xD9C69E), Hex(0xE8DAB8), Fbm(u, v, 3, 3, 4, 183) * 0.8f + 0.5f);
        c = Mul(c, 1 + ripple * 0.035f + (Rand(x, y, 185) - 0.5f) * 0.09f);
        if (Rand(x, y, 187) > 0.992f) c = Mul(c, 0.72f);
        return c;
    }, Size, Size, true);

    static BitmapSource Water() => Pixels((u, v) =>
    {
        float wu = u + 0.06f * Fbm(u, v, 3, 3, 3, 191), wv = v + 0.06f * Fbm(u, v, 3, 3, 3, 193);
        var w = Worley(wu - MathF.Floor(wu), wv - MathF.Floor(wv), 6, 195);
        float caustic = MathF.Exp(-(w.f2 - w.f1) * 9);
        var c = Mix(Hex(0x0C3644), Hex(0x125466), Fbm(u, v, 2, 2, 4, 197) * 0.9f + 0.5f);
        return Mix(c, Hex(0x58C8D8), caustic * 0.32f);
    });

    static BitmapSource Forest() => Pixels((x, y) =>
    {
        float u = (x + 0.5f) / Size, v = (y + 0.5f) / Size;
        var c = Mix(Hex(0x0A120A), Hex(0x16261A), Fbm(u, v, 3, 3, 5, 201) * 0.9f + 0.5f);
        return Add(c, (Rand(x, y, 203) - 0.5f) * 0.02f);
    }, Size, Size, true);
}
