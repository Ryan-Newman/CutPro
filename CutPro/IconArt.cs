using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// The CutPro logo, drawn as vectors (so the title bar is sharp at any DPI) and exported to the multi-size app icon with
/// <c>CutPro.exe --makeicon dir</c>: a monitor whose screen shows a cross in front of a sunrise, a timeline with clips and a
/// playhead, a film strip and a clapperboard. Sizes up to 40 px use a simplified drawing (monitor + cross) that stays readable.
/// Design space: 256 × 256.
/// </summary>
public static class IconArt
{
    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static SolidColorBrush S(string hex) { var b = new SolidColorBrush(C(hex)); b.Freeze(); return b; }
    static LinearGradientBrush L(double x0, double y0, double x1, double y1, params (string c, double o)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(x0, y0), EndPoint = new Point(x1, y1) };
        foreach (var (c, o) in stops) b.GradientStops.Add(new GradientStop(C(c), o));
        b.Freeze(); return b;
    }
    static Geometry R(double x, double y, double w, double h, double r = 0) => new RectangleGeometry(new Rect(x, y, w, h), r, r);

    /// <summary>The logo for a target pixel size (≤ 40: simplified).</summary>
    public static Drawing Logo(int px) => px <= 40 ? Simple(px) : Full(px >= 64);

    static Drawing Full(bool details)
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 256, 256));
            // stand
            dc.DrawGeometry(L(0, 0, 0, 1, ("#3A404D", 0), ("#20242C", 1)), null, Geometry.Parse("M110,186 L146,186 L154,214 L102,214 Z"));
            dc.DrawGeometry(L(0, 0, 0, 1, ("#4A5160", 0), ("#22262E", 1)), null, R(74, 208, 108, 14, 7));
            // monitor body + bezel highlight
            dc.DrawGeometry(L(0, 0, 0, 1, ("#3B4150", 0), ("#1B1E25", 1)), new Pen(S("#5C6475"), 2), R(14, 30, 228, 162, 18));
            // screen: dawn sky
            var screen = R(28, 44, 200, 134, 8);
            dc.DrawGeometry(L(0, 0, 0, 1, ("#16235C", 0), ("#4B3A97", 0.45), ("#C8668A", 0.75), ("#FFB65C", 1)), null, screen);
            dc.PushClip(screen);
            // light behind the cross
            var glow = new RadialGradientBrush(C("#FFFBE0"), Color.FromArgb(0, 255, 230, 160)) { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5 };
            glow.GradientStops.Insert(1, new GradientStop(Color.FromArgb(150, 255, 214, 120), 0.35)); glow.Freeze();
            dc.DrawEllipse(glow, null, new Point(128, 92), 92, 70);
            if (details)
                for (int i = 0; i < 12; i++)
                {   // soft rays
                    double a = i * Math.PI / 6 + 0.13, a2 = a + 0.11;
                    var ray = new PathGeometry(new[] { new PathFigure(new Point(128, 92), new PathSegment[]
                    {
                        new LineSegment(new Point(128 + 200 * Math.Cos(a), 92 + 200 * Math.Sin(a)), false),
                        new LineSegment(new Point(128 + 200 * Math.Cos(a2), 92 + 200 * Math.Sin(a2)), false),
                    }, true) });
                    dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(28, 255, 240, 200)), null, ray);
                }
            // timeline: track lane, three clips, playhead
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(150, 14, 16, 24)), null, R(28, 150, 200, 28));
            dc.DrawGeometry(S("#4DAAFC"), null, R(38, 156, 58, 14, 3));
            dc.DrawGeometry(S("#7ED957"), null, R(100, 156, 46, 14, 3));
            dc.DrawGeometry(S("#FF8A5C"), null, R(150, 156, 66, 14, 3));
            dc.Pop();
            // the cross (with a thin dark edge so it stands out from the light)
            var cross = new CombinedGeometry(GeometryCombineMode.Union, R(116, 52, 24, 92, 3), R(90, 74, 76, 22, 3));
            dc.DrawGeometry(L(0, 0, 0, 1, ("#FFFDF2", 0), ("#FFE7A3", 0.55), ("#F2B544", 1)), new Pen(new SolidColorBrush(Color.FromArgb(120, 120, 70, 20)), 2), cross);
            // playhead over the timeline
            dc.DrawGeometry(S("#FF3B5C"), null, R(170, 148, 3, 30));
            dc.DrawGeometry(S("#FF3B5C"), null, Geometry.Parse("M164,144 L179,144 L171.5,152 Z"));
            dc.DrawGeometry(null, new Pen(S("#0B0D12"), 1.5), screen);

            // film strip curling across the lower left
            dc.PushTransform(new RotateTransform(-24, 54, 206));
            var film = R(0, 186, 112, 40, 4);
            dc.DrawGeometry(L(0, 0, 0, 1, ("#2C2F38", 0), ("#15171C", 1)), new Pen(S("#5C6475"), 1.5), film);
            for (int i = 0; i < 7; i++)
            {
                dc.DrawGeometry(S("#E8E2D0"), null, R(5 + i * 15.5, 189, 8, 6, 1.5));
                dc.DrawGeometry(S("#E8E2D0"), null, R(5 + i * 15.5, 217, 8, 6, 1.5));
            }
            string[] frames = { "#6EC3FF", "#FFD27A", "#B794F6" };
            for (int i = 0; i < 3; i++) dc.DrawGeometry(L(0, 0, 0, 1, (frames[i], 0), ("#2A3350", 1)), null, R(6 + i * 35, 198, 31, 16, 2));
            dc.Pop();

            // clapperboard at the lower right (striped arm hinged on the board's top-left corner)
            dc.DrawGeometry(L(0, 0, 0, 1, ("#2F333D", 0), ("#121418", 1)), new Pen(S("#5C6475"), 1.5), R(174, 194, 70, 50, 5));
            if (details)
                for (int i = 0; i < 3; i++) dc.DrawGeometry(S("#8C93A3"), null, R(182, 209 + i * 11, 54 - i * 14, 4, 2));
            dc.PushTransform(new RotateTransform(-14, 175, 194));
            var stick = R(174, 179, 70, 15, 3);
            dc.DrawGeometry(S("#F4F1E8"), null, stick);
            dc.PushClip(stick);
            for (int i = 0; i < 6; i++)
                dc.DrawGeometry(S("#1A1C22"), null, Geometry.Parse(FormattableString.Invariant($"M{168 + i * 16},194 L{178 + i * 16},179 L{186 + i * 16},179 L{176 + i * 16},194 Z")));
            dc.Pop(); dc.Pop();
            dc.DrawEllipse(S("#C9CED8"), null, new Point(178, 195), 3.5, 3.5);
        }
        g.Freeze();
        return g;
    }

    /// <summary>16–32 px: monitor + cross on the sunrise, thick shapes on the pixel grid (16 units = 1 px at 16 px).</summary>
    static Drawing Simple(int px)
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 256, 256));
            dc.DrawGeometry(S("#2A2F3A"), null, Geometry.Parse("M104,200 L152,200 L160,224 L96,224 Z"));
            dc.DrawGeometry(S("#3A404D"), null, R(64, 224, 128, 24, 8));
            dc.DrawGeometry(L(0, 0, 0, 1, ("#4A5160", 0), ("#1B1E25", 1)), null, R(0, 16, 256, 192, 32));
            var screen = R(24, 40, 208, 144, 10);
            dc.DrawGeometry(L(0, 0, 0, 1, ("#1E2C70", 0), ("#5A3E9E", 0.5), ("#FFB65C", 1)), null, screen);
            var glow = new RadialGradientBrush(Color.FromArgb(200, 255, 236, 170), Color.FromArgb(0, 255, 220, 140)); glow.Freeze();
            dc.PushClip(screen); dc.DrawEllipse(glow, null, new Point(128, 104), 110, 80); dc.Pop();
            var cross = new CombinedGeometry(GeometryCombineMode.Union, R(112, 48, 32, 128), R(72, 80, 112, 32));
            dc.DrawGeometry(L(0, 0, 0, 1, ("#FFFFF6", 0), ("#FFD970", 1)), px >= 24 ? new Pen(new SolidColorBrush(Color.FromArgb(110, 110, 60, 10)), 6) : null, cross);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Vector image for WPF (title bar).</summary>
    public static DrawingImage Image(int px) { var d = new DrawingImage(Logo(px)); d.Freeze(); return d; }

    public static BitmapSource Render(int px)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) { dc.PushTransform(new ScaleTransform(px / 256.0, px / 256.0)); dc.DrawDrawing(Logo(px)); }
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv); rtb.Freeze();
        return rtb;
    }

    /// <summary>Multi-size .ico (PNG-compressed entries: Windows Vista and later).</summary>
    public static void WriteIco(string path, int[] sizes)
    {
        var pngs = sizes.Select(s =>
        {
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(Render(s)));
            using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
        }).ToList();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset); offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }

    /// <summary>CutPro.exe --makeicon dir: CutPro.ico + a preview sheet of every size on dark and light backgrounds.</summary>
    public static void Make(string dir)
    {
        Directory.CreateDirectory(dir);
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
        WriteIco(Path.Combine(dir, "CutPro.ico"), sizes);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(S("#1F1F1F"), null, new Rect(0, 0, 900, 300));
            dc.DrawRectangle(S("#F3F3F3"), null, new Rect(0, 300, 900, 300));
            for (int row = 0; row < 2; row++)
            {
                double x = 16;
                foreach (var s in sizes)
                {
                    dc.DrawImage(Render(s), new Rect(x, row * 300 + 280 - s, s, s));
                    x += s + 14;
                }
            }
        }
        var rtb = new RenderTargetBitmap(900, 600, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var f = File.Create(Path.Combine(dir, "icon_sheet.png"))) enc.Save(f);
        var big = new PngBitmapEncoder(); big.Frames.Add(BitmapFrame.Create(Render(512)));
        using (var f = File.Create(Path.Combine(dir, "icon_512.png"))) big.Save(f);
    }
}
