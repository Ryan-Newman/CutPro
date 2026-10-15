using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace CutPro;

// ======================= design =======================
public sealed class ThumbText
{
    public string Text { get; set; } = "";
    public string Style { get; set; } = "Impact Yellow";
    public double Size { get; set; } = 120;          // px on the 1280-wide (or 1080-wide) canvas
    public double X { get; set; } = 0.3;              // centre, 0…1 of the canvas
    public double Y { get; set; } = 0.7;
    public double Rotation { get; set; }
}

public sealed class ThumbShape
{
    public string Kind { get; set; } = "Arrow";       // Arrow | Circle
    public double X { get; set; } = 0.6;
    public double Y { get; set; } = 0.45;
    public double Size { get; set; } = 220;
    public double Rotation { get; set; } = -25;
    public string Color { get; set; } = "#FF2D2D";
}

/// <summary>A thumbnail: a frame of the video (framed, popped, blurred, darkened), an optional cut-out subject with an outline, texts and
/// shapes. <see cref="ThumbRender"/> draws it the same way on screen and into the saved file.</summary>
public sealed class ThumbDesign
{
    public bool Vertical { get; set; }
    public double Time { get; set; }
    public double Zoom { get; set; } = 1;
    public double PanX { get; set; }
    public double PanY { get; set; }
    public double Pop { get; set; } = 35;
    public double Brightness { get; set; }
    public double Blur { get; set; }
    public double Darken { get; set; }
    public bool Subject { get; set; }
    public double SubjectScale { get; set; } = 1.08;
    public double SubjectX { get; set; }
    public double SubjectY { get; set; }
    public double Outline { get; set; } = 10;
    public double Shadow { get; set; } = 60;
    public string OutlineColor { get; set; } = "#FFFFFF";
    public List<ThumbText> Texts { get; set; } = new();
    public List<ThumbShape> Shapes { get; set; } = new();
    public int W => Vertical ? 1080 : 1280;
    public int H => Vertical ? 1920 : 720;
}

// ======================= text styles =======================
public sealed record ThumbStyle(string Name, string Font, FontWeight Weight, string Fill, string? Fill2, string Stroke, double StrokeK,
                                string Shadow, double ShadowK, string? Box, string? Glow, bool Caps);

public static class ThumbStyles
{
    public static readonly ThumbStyle[] All =
    {
        new("Impact Yellow", "Impact", FontWeights.Normal, "#FFE600", null, "#000000", 0.09, "#B0000000", 0.06, null, null, true),
        new("White Stroke", "Arial Black", FontWeights.Black, "#FFFFFF", null, "#000000", 0.09, "#99000000", 0.05, null, null, true),
        new("Red Box", "Arial Black", FontWeights.Black, "#FFFFFF", null, "#000000", 0, "#00000000", 0, "#E62020", null, true),
        new("Yellow Box", "Arial Black", FontWeights.Black, "#111111", null, "#000000", 0, "#00000000", 0, "#FFD60A", null, true),
        new("Gold", "Impact", FontWeights.Normal, "#FFF4B0", "#FFB300", "#3A2400", 0.07, "#AA000000", 0.05, null, null, true),
        new("Neon", "Arial Black", FontWeights.Black, "#FFFFFF", null, "#18E0FF", 0.03, "#00000000", 0, null, "#18E0FF", true),
        new("Clean", "Segoe UI Black", FontWeights.Black, "#FFFFFF", null, "#000000", 0, "#CC000000", 0.04, null, null, false),
        new("Scripture", "Georgia", FontWeights.Bold, "#FFFFFF", null, "#000000", 0, "#CC000000", 0.03, null, null, false),
    };
    public static ThumbStyle Get(string name) => All.FirstOrDefault(s => s.Name == name) ?? All[0];
}

// ======================= renderer =======================
public static class ThumbRender
{
    static Color C(string hex) => TitleVisual.ParseColor(hex, Colors.White);
    static Brush B(string hex) { var b = new SolidColorBrush(C(hex)); b.Freeze(); return b; }

    /// <summary>The background picture's rectangle on the canvas: covers it, then zoom and pan (−1…1).</summary>
    public static Rect BgRect(ThumbDesign d, double bw, double bh)
    {
        double s = Math.Max(d.W / bw, d.H / bh) * Math.Max(1, d.Zoom), w = bw * s, h = bh * s;
        double x = (d.W - w) / 2 * (1 - d.PanX), y = (d.H - h) / 2 * (1 - d.PanY);
        return new Rect(x, y, w, h);
    }
    public static Rect SubjectRect(ThumbDesign d, Rect bg)
    {
        double w = bg.Width * d.SubjectScale, h = bg.Height * d.SubjectScale;
        return new Rect(bg.X + (bg.Width - w) / 2 + d.SubjectX * d.W, bg.Y + (bg.Height - h) / 2 + d.SubjectY * d.H, w, h);
    }

    /// <summary>Text as geometry centred at its position (lines centred), with its box rectangle.</summary>
    public static (Geometry g, Rect box) TextGeometry(ThumbText t, ThumbDesign d)
    {
        var st = ThumbStyles.Get(t.Style);
        string s = st.Caps ? t.Text.ToUpper(CultureInfo.CurrentCulture) : t.Text;
        var ft = new FormattedText(s.Length == 0 ? " " : s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                   new Typeface(new FontFamily(st.Font), FontStyles.Normal, st.Weight, FontStretches.Normal), t.Size, Brushes.White, 1)
        { TextAlignment = TextAlignment.Center, LineHeight = t.Size * 1.02 };
        double cx = t.X * d.W, cy = t.Y * d.H;
        var g = ft.BuildGeometry(new Point(cx, cy - ft.Height / 2));          // (centre alignment: x is the line centre)
        if (Math.Abs(t.Rotation) > 0.01) { g = g.Clone(); g.Transform = new RotateTransform(t.Rotation, cx, cy); }
        g.Freeze();
        var box = g.Bounds; if (box.IsEmpty) box = new Rect(cx, cy, 1, 1);
        return (g, box);
    }

    /// <summary>Draws the whole thumbnail in canvas coordinates (W×H).</summary>
    public static void Draw(DrawingContext dc, ThumbDesign d, BitmapSource? bg, BitmapSource? subject, BitmapSource? silhouette)
    {
        var full = new Rect(0, 0, d.W, d.H);
        dc.PushClip(new RectangleGeometry(full));
        dc.DrawRectangle(Brushes.Black, null, full);
        if (bg != null)
        {
            var r = BgRect(d, bg.PixelWidth, bg.PixelHeight);
            dc.DrawImage(bg, r);
            if (d.Darken > 0) dc.DrawRectangle(B($"#{(byte)Math.Round(d.Darken / 100 * 255):X2}000000"), null, full);
            if (d.Subject && subject != null)
            {
                var sr = SubjectRect(d, r);
                if (silhouette != null)
                {
                    if (d.Shadow > 0) { dc.PushOpacity(d.Shadow / 100 * 0.8); dc.PushOpacityMask(new ImageBrush(silhouette)); dc.DrawRectangle(Brushes.Black, null, new Rect(sr.X + d.W * 0.008, sr.Y + d.W * 0.01, sr.Width, sr.Height)); dc.Pop(); dc.Pop(); }
                    if (d.Outline > 0) { dc.PushOpacityMask(new ImageBrush(silhouette) { Viewport = new Rect(0, 0, 1, 1) }); dc.DrawRectangle(B(d.OutlineColor), null, sr); dc.Pop(); }
                }
                dc.DrawImage(subject, sr);
            }
        }
        foreach (var s in d.Shapes) DrawShape(dc, s, d);
        foreach (var t in d.Texts) DrawText(dc, t, d);
        dc.Pop();
    }

    static void DrawShape(DrawingContext dc, ThumbShape s, ThumbDesign d)
    {
        double cx = s.X * d.W, cy = s.Y * d.H, z = s.Size;
        dc.PushTransform(new RotateTransform(s.Rotation, cx, cy));
        var ink = B(s.Color); var edge = new Pen(Brushes.Black, z * 0.035) { LineJoin = PenLineJoin.Round };
        if (s.Kind == "Circle")
        {
            var pen = new Pen(ink, z * 0.07); var back = new Pen(Brushes.Black, z * 0.1);
            dc.DrawEllipse(null, back, new Point(cx, cy), z / 2, z * 0.4); dc.DrawEllipse(null, pen, new Point(cx, cy), z / 2, z * 0.4);
        }
        else
        {
            // a fat arrow pointing right (rotate it to aim)
            double L = z, T = z * 0.16, Hd = z * 0.42, Hl = z * 0.38;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(cx - L / 2, cy - T / 2), true, true);
                c.LineTo(new Point(cx + L / 2 - Hl, cy - T / 2), true, false); c.LineTo(new Point(cx + L / 2 - Hl, cy - Hd / 2), true, false);
                c.LineTo(new Point(cx + L / 2, cy), true, false); c.LineTo(new Point(cx + L / 2 - Hl, cy + Hd / 2), true, false);
                c.LineTo(new Point(cx + L / 2 - Hl, cy + T / 2), true, false); c.LineTo(new Point(cx - L / 2, cy + T / 2), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(Brushes.Black, null, Shift(g, z * 0.03)); dc.DrawGeometry(ink, edge, g);
        }
        dc.Pop();
    }

    static Geometry Shift(Geometry g, double k) { var c = g.Clone(); c.Transform = new TranslateTransform(k, k); c.Freeze(); return c; }

    static void DrawText(DrawingContext dc, ThumbText t, ThumbDesign d)
    {
        if (string.IsNullOrWhiteSpace(t.Text)) return;
        var st = ThumbStyles.Get(t.Style);
        var (g, box) = TextGeometry(t, d);
        if (st.Box != null)
        {
            double p = t.Size * 0.18;
            var r = new Rect(box.X - p * 1.4, box.Y - p, box.Width + p * 2.8, box.Height + p * 2);
            dc.PushTransform(new RotateTransform(t.Rotation, t.X * d.W, t.Y * d.H));
            dc.DrawRoundedRectangle(B("#66000000"), null, new Rect(r.X + p * 0.3, r.Y + p * 0.4, r.Width, r.Height), p * 0.5, p * 0.5);
            dc.DrawRoundedRectangle(B(st.Box), null, r, p * 0.5, p * 0.5);
            dc.Pop();
        }
        if (st.ShadowK > 0) dc.DrawGeometry(B(st.Shadow), new Pen(B(st.Shadow), t.Size * (st.StrokeK * 2)) { LineJoin = PenLineJoin.Round }, Shift(g, t.Size * st.ShadowK));
        if (st.Glow != null)
            for (int i = 4; i >= 1; i--)
            {
                var gc = C(st.Glow); gc.A = (byte)(40 + 10 * (4 - i));
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(gc), t.Size * 0.05 * i) { LineJoin = PenLineJoin.Round }, g);
            }
        Brush fill = st.Fill2 != null ? new LinearGradientBrush(C(st.Fill), C(st.Fill2), 90) : B(st.Fill);
        if (st.StrokeK > 0) dc.DrawGeometry(null, new Pen(B(st.Stroke), t.Size * st.StrokeK * 2) { LineJoin = PenLineJoin.Round }, g);
        dc.DrawGeometry(fill, null, g);
    }

    /// <summary>The finished thumbnail as a bitmap (W×H).</summary>
    public static BitmapSource Render(ThumbDesign d, BitmapSource? bg, BitmapSource? subject, BitmapSource? silhouette)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) Draw(dc, d, bg, subject, silhouette);
        var rtb = new RenderTargetBitmap(d.W, d.H, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv); rtb.Freeze();
        return rtb;
    }

    /// <summary>JPG (quality lowered until it is under YouTube's 2 MB limit) or PNG. Returns the file size.</summary>
    public static long Save(BitmapSource bmp, string file, bool jpg)
    {
        if (jpg)
            for (int q = 92; q >= 60; q -= 8)
            {
                var enc = new JpegBitmapEncoder { QualityLevel = q }; enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(file)) enc.Save(fs);
                if (new FileInfo(file).Length < 2_000_000) break;
            }
        else { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var fs = File.Create(file); enc.Save(fs); }
        return new FileInfo(file).Length;
    }

    /// <summary>The subject's outline picture: its cut-out grown by <paramref name="r"/> pixels — a round outline (exact Euclidean distance;
    /// a square max filter was 41 % thicker on diagonals) with a soft 1 px edge — as an opacity mask.</summary>
    public static BitmapSource Silhouette(float[] a, int w, int h, int r)
    {
        // squared distance to the nearest subject pixel: lower envelope of parabolas along columns, then rows (Felzenszwalb & Huttenlocher)
        var d = new double[w * h];
        for (int i = 0; i < d.Length; i++) d[i] = a[i] >= 0.5f ? 0 : 1e20;
        int n = Math.Max(w, h);
        Parallel.For(0, w, () => new EdtBuf(n), (x, _, b) => { Edt(d, x, w, h, b); return b; }, _ => { });
        Parallel.For(0, h, () => new EdtBuf(n), (y, _, b) => { Edt(d, y * w, 1, w, b); return b; }, _ => { });
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            double v = Math.Max(a[i], Math.Clamp(r + 0.5 - Math.Sqrt(d[i]), 0, 1));
            byte b = (byte)Math.Round(v * 255); px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = b; px[i * 4 + 3] = b;
        }
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0); wb.Freeze();
        return wb;
    }

    sealed class EdtBuf { public readonly double[] F, Z; public readonly int[] V; public EdtBuf(int n) { F = new double[n]; Z = new double[n + 1]; V = new int[n]; } }

    /// <summary>1-D squared distance transform of d[off + q·stride], q &lt; n, in place.</summary>
    static void Edt(double[] d, int off, int stride, int n, EdtBuf b)
    {
        var f = b.F; var z = b.Z; var v = b.V;
        for (int q = 0; q < n; q++) f[q] = d[off + q * stride];
        int k = 0; v[0] = 0; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            double s = (f[q] + (double)q * q - f[v[k]] - (double)v[k] * v[k]) / (2.0 * (q - v[k]));
            while (s <= z[k]) { k--; s = (f[q] + (double)q * q - f[v[k]] - (double)v[k] * v[k]) / (2.0 * (q - v[k])); }
            k++; v[k] = q; z[k] = s; z[k + 1] = double.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            d[off + q * stride] = (double)(q - v[k]) * (q - v[k]) + f[v[k]];
        }
    }
}

// ======================= frames =======================
/// <summary>Frames of the program for thumbnails: the top video / picture clip at a time, at full resolution, with its drawn look and colour.</summary>
public static class ThumbFrames
{
    public static (byte[] px, int w, int h)? Grab(Editor ed, double t, int maxW)
    {
        var c = ed.Clips.Where(x => x.Kind is ClipKind.Video or ClipKind.Image && t >= x.Start && t < x.End && !ed.TrackOff[x.Track]).OrderBy(x => x.Track).FirstOrDefault();
        if (c == null || ed.MediaOf(c) is not { Offline: false } m || Exporter.FindFfmpeg() is not { } ff) return null;
        double src = m.Type == ClipKind.Image ? 0 : Math.Clamp(TimeMap.Src(c, t), 0, Math.Max(0, m.Duration - 0.05));
        int w = Math.Min(maxW, m.Width > 0 ? m.Width : maxW); w -= w % 2;
        int h = m.Width > 0 ? (int)Math.Round(w * m.Height / (double)m.Width / 2) * 2 : (int)(w * 9.0 / 16) / 2 * 2;
        string vf = $"scale={w}:{h}" + (Toon.Active(c.Toon) ? "," + Toon.Graph(c.Toon!, h) : "") + ",format=bgra";
        if (Nest.SourceOf(m, src) is not var (path, fsrc, _)) return null;                // (a nested sequence: its render)
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string> { "-v", "error", "-nostdin" };
        if (m.Type != ClipKind.Image) args.AddRange(new[] { "-ss", fsrc.ToString("0.###", CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-i", path, "-frames:v", "1", "-vf", vf, "-f", "rawvideo", "pipe:1" });
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
            var buf = new byte[w * h * 4]; int got = 0; var s = p.StandardOutput.BaseStream;
            while (got < buf.Length) { int n = s.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
            p.WaitForExit();
            if (got < buf.Length) return null;
            for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
            if (Grade.For(c.Grade) is { } lut) Grade.Apply(lut, buf, w, h, w * 4);
            return (buf, w, h);
        }
        catch { return null; }
    }

    public static BitmapSource ToBitmap(byte[] px, int w, int h)
    {
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0); wb.Freeze();
        return wb;
    }

    /// <summary>How good a frame looks as a thumbnail: sharp, colourful, well exposed (0…1-ish).</summary>
    public static double Score(byte[] px, int w, int h)
    {
        double lapSum = 0, lapSq = 0, meanL = 0, rg = 0, yb = 0, rg2 = 0, yb2 = 0; int n = 0;
        for (int y = 1; y < h - 1; y += 2)
            for (int x = 1; x < w - 1; x += 2)
            {
                int i = (y * w + x) * 4;
                double L(int k) => 0.0722 * px[k] + 0.7152 * px[k + 1] + 0.2126 * px[k + 2];
                double c = L(i), lap = 4 * c - L(i - 4) - L(i + 4) - L(i - w * 4) - L(i + w * 4);
                lapSum += lap; lapSq += lap * lap; meanL += c;
                double a = px[i + 2] - px[i + 1], b = 0.5 * (px[i + 2] + px[i + 1]) - px[i];
                rg += a; yb += b; rg2 += a * a; yb2 += b * b; n++;
            }
        if (n == 0) return 0;
        double sharp = Math.Sqrt(Math.Max(0, lapSq / n - Math.Pow(lapSum / n, 2)));
        double sdr = Math.Sqrt(Math.Max(0, rg2 / n - Math.Pow(rg / n, 2))), sdy = Math.Sqrt(Math.Max(0, yb2 / n - Math.Pow(yb / n, 2)));
        double colour = Math.Sqrt(sdr * sdr + sdy * sdy) + 0.3 * Math.Sqrt(Math.Pow(rg / n, 2) + Math.Pow(yb / n, 2));
        double expo = 1 - Math.Abs(meanL / n - 120) / 120;
        return Math.Min(1, sharp / 25) * 0.45 + Math.Min(1, colour / 80) * 0.35 + Math.Max(0, expo) * 0.2;
    }
}

// ======================= window =======================
/// <summary>Thumbnail Studio: design a YouTube thumbnail (or a Shorts cover) from a frame of the video.</summary>
sealed class ThumbnailWindow : Window
{
    readonly MainWindow main; readonly Editor ed;
    readonly ThumbDesign d = new();
    readonly ThumbCanvas canvas;
    readonly StackPanel side = new() { Margin = new Thickness(14, 4, 14, 14) };
    readonly TextBlock status = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    readonly WrapPanel suggestions = new();
    byte[]? framePx; int frameW, frameH;
    BitmapSource? bgProcessed, subjectBmp, silhouette;
    float[]? subjectAlpha;
    string bgKey = "", silKey = "";
    bool picking;
    readonly List<(float x, float y, float label)> pickPts = new();
    (string sig, Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float> emb, int w, int h, byte[] px)? embCache;
    static SamEngine? sam;

    public ThumbnailWindow(MainWindow main, Editor ed)
    {
        this.main = main; this.ed = ed;
        status.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        Title = "Thumbnail Studio"; Width = 1240; Height = 780; MinWidth = 980; MinHeight = 600;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 13; UseLayoutRounding = true;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 44, GlassFrameThickness = new Thickness(-1),
                                                                    ResizeBorderThickness = new Thickness(6), CornerRadius = new CornerRadius(8), UseAeroCaptionButtons = false });
        var root = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        root.SetResourceReference(Border.BackgroundProperty, "B.Glass.Tint");
        root.SetResourceReference(Border.BorderBrushProperty, "B.Glass.Edge");
        Content = root;
        SourceInitialized += (_, _) => AlertWindow.Backdrop(this, root);
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !picking) Close(); if (e.Key == Key.Delete && canvas!.Selected is { } s) DeleteSelected(s); };
        canvas = new ThumbCanvas(this);
        d.Time = ed.Time;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) }); grid.RowDefinitions.Add(new RowDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        var head = new DockPanel { Margin = new Thickness(16, 0, 8, 0) };
        var close = new Button { Content = "", FontSize = 10, Style = (Style)main.FindResource("Icon"), Width = 36, Height = 30, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right);
        var title = Th(new TextBlock { Text = "Thumbnail Studio", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }, "B.Fg");
        head.Children.Add(close); head.Children.Add(title);
        Grid.SetColumnSpan(head, 2); grid.Children.Add(head);
        var cbox = new Border { Margin = new Thickness(16, 0, 8, 16), CornerRadius = new CornerRadius(6), Child = canvas, ClipToBounds = true };
        cbox.SetResourceReference(Border.BackgroundProperty, "B.Monitor.Bg");
        Grid.SetRow(cbox, 1); grid.Children.Add(cbox);
        var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = side };
        Grid.SetRow(sv, 1); Grid.SetColumn(sv, 1); grid.Children.Add(sv);
        root.Child = grid;
        ApplyTemplate("Bold left");
        BuildSide();
        Loaded += async (_, _) => { await LoadFrameAsync(); _ = SuggestFramesAsync(); };
    }

    static T Th<T>(T e, string key) where T : FrameworkElement { e.SetResourceReference(TextBlock.ForegroundProperty, key); return e; }
    TextBlock Head(string s) => Th(new TextBlock { Text = s, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) }, "B.Fg");
    TextBlock Dim(string s) => Th(new TextBlock { Text = s, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }, "B.Fg.Dim");
    Button Btn(string text, Action a, string? tip = null) { var b = new Button { Content = text, Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 6), ToolTip = tip }; b.Click += (_, _) => a(); return b; }
    void SliderRow(Panel into, string label, double min, double max, double v, Action<double> set)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) }); g.ColumnDefinitions.Add(new ColumnDefinition());
        var l = Th(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, "B.Fg.Dim");
        var s = new Slider { Minimum = min, Maximum = max, Value = v, VerticalAlignment = VerticalAlignment.Center };
        s.ValueChanged += (_, e) => { set(e.NewValue); Redraw(); };
        Grid.SetColumn(s, 1); g.Children.Add(l); g.Children.Add(s); into.Children.Add(g);
    }

    void BuildSide()
    {
        side.Children.Clear();
        // size and templates
        side.Children.Add(Head("Size"));
        var size = new WrapPanel();
        var h16 = new RadioButton { Content = "YouTube 16:9 (1280×720)", IsChecked = !d.Vertical, Margin = new Thickness(0, 0, 12, 4) };
        var v916 = new RadioButton { Content = "Shorts cover 9:16", IsChecked = d.Vertical };
        h16.Checked += (_, _) => { d.Vertical = false; ApplyTemplate(lastTemplate); BuildSide(); Redraw(); };
        v916.Checked += (_, _) => { d.Vertical = true; ApplyTemplate(lastTemplate); BuildSide(); Redraw(); };
        size.Children.Add(h16); size.Children.Add(v916); side.Children.Add(size);
        side.Children.Add(Head("Template"));
        var tp = new WrapPanel();
        foreach (var t in new[] { "Bold left", "Big center", "Top banner", "Scripture", "Point at it", "Minimal" }) tp.Children.Add(Btn(t, () => { ApplyTemplate(t); BuildSide(); Redraw(); }));
        side.Children.Add(tp);

        // frame
        side.Children.Add(Head("Frame"));
        var fr = new Slider { Minimum = 0, Maximum = Math.Max(0.1, ed.End - 0.04), Value = d.Time, ToolTip = "Pick the frame (the video's timeline)" };
        fr.PreviewMouseLeftButtonUp += async (_, _) => { d.Time = fr.Value; await LoadFrameAsync(); };
        side.Children.Add(fr);
        var fb = new WrapPanel();
        fb.Children.Add(Btn("Playhead frame", async () => { d.Time = ed.Time; fr.Value = d.Time; await LoadFrameAsync(); }));
        fb.Children.Add(Btn("Suggest frames", () => _ = SuggestFramesAsync(), "Sharp, colourful, well-exposed frames from across the video"));
        side.Children.Add(fb);
        side.Children.Add(suggestions);

        // background
        side.Children.Add(Head("Background"));
        SliderRow(side, "Zoom", 1, 2.5, d.Zoom, v => d.Zoom = v);
        SliderRow(side, "Pop", 0, 100, d.Pop, v => { d.Pop = v; bgKey = ""; });
        SliderRow(side, "Brightness", -50, 50, d.Brightness, v => { d.Brightness = v; bgKey = ""; });
        SliderRow(side, "Blur", 0, 40, d.Blur, v => { d.Blur = v; bgKey = ""; });
        SliderRow(side, "Darken", 0, 80, d.Darken, v => d.Darken = v);
        side.Children.Add(Dim("Drag the picture to frame it; the mouse wheel zooms the selected item."));

        // subject
        side.Children.Add(Head("Subject cut-out"));
        var sb = new WrapPanel();
        var pick = new ToggleButton { Content = picking ? "Click the subject…" : "✦ Cut out subject", IsChecked = picking, Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 6),
                                      ToolTip = "Click the person or object (AI, on this PC). Shift+click adds, Alt+click removes." };
        pick.Click += async (_, _) =>
        {
            if (pick.IsChecked == true) { if (!await EnsureSamAsync()) { pick.IsChecked = false; return; } picking = true; pickPts.Clear(); Status("Click the subject in the picture."); }
            else picking = false;
            BuildSide();
        };
        sb.Children.Add(pick);
        if (d.Subject) sb.Children.Add(Btn("Remove", () => { d.Subject = false; subjectBmp = silhouette = null; BuildSide(); Redraw(); }));
        side.Children.Add(sb);
        if (d.Subject)
        {
            SliderRow(side, "Size", 0.6, 2, d.SubjectScale, v => d.SubjectScale = v);
            SliderRow(side, "Outline", 0, 30, d.Outline, v => { d.Outline = v; silKey = ""; });
            SliderRow(side, "Shadow", 0, 100, d.Shadow, v => d.Shadow = v);
            var oc = new ComboBox { ItemsSource = new[] { "White", "Yellow", "Red", "Black", "Cyan" }, SelectedIndex = Array.IndexOf(new[] { "#FFFFFF", "#FFE600", "#E62020", "#000000", "#18E0FF" }, d.OutlineColor) };
            oc.SelectionChanged += (_, _) => { d.OutlineColor = new[] { "#FFFFFF", "#FFE600", "#E62020", "#000000", "#18E0FF" }[Math.Max(0, oc.SelectedIndex)]; Redraw(); };
            side.Children.Add(oc);
        }

        // texts
        for (int i = 0; i < d.Texts.Count; i++)
        {
            var t = d.Texts[i];
            side.Children.Add(Head(i == 0 ? "Headline" : $"Text {i + 1}"));
            var tb = new TextBox { Text = t.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 30 };
            tb.TextChanged += (_, _) => { t.Text = tb.Text; Redraw(); };
            side.Children.Add(tb);
            var st = new ComboBox { ItemsSource = ThumbStyles.All.Select(s => s.Name).ToList(), SelectedItem = t.Style, Margin = new Thickness(0, 4, 0, 0) };
            st.SelectionChanged += (_, _) => { t.Style = st.SelectedItem as string ?? t.Style; Redraw(); };
            side.Children.Add(st);
            SliderRow(side, "Size", 30, 320, t.Size, v => t.Size = v);
            SliderRow(side, "Tilt", -20, 20, t.Rotation, v => t.Rotation = v);
            var tr = new WrapPanel(); int idx = i;
            tr.Children.Add(Btn("Remove text", () => { d.Texts.RemoveAt(idx); BuildSide(); Redraw(); }));
            side.Children.Add(tr);
        }
        var add = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        add.Children.Add(Btn("+ Text", () => { d.Texts.Add(new ThumbText { Text = "YOUR TEXT", Style = "White Stroke", Size = 90, X = 0.5, Y = 0.5 }); BuildSide(); Redraw(); }));
        add.Children.Add(Btn("+ Arrow", () => { d.Shapes.Add(new ThumbShape()); Redraw(); }));
        add.Children.Add(Btn("+ Circle", () => { d.Shapes.Add(new ThumbShape { Kind = "Circle", Rotation = 0, Size = 260 }); Redraw(); }));
        side.Children.Add(add);
        var chips = TextSuggestions();
        if (chips.Count > 0)
        {
            side.Children.Add(Head("Ideas for the headline"));
            var cw = new WrapPanel();
            foreach (var s in chips) cw.Children.Add(Btn(s, () => { if (d.Texts.Count == 0) d.Texts.Add(new ThumbText()); d.Texts[0].Text = s; BuildSide(); Redraw(); }));
            side.Children.Add(cw);
        }

        // save
        side.Children.Add(Head("Save"));
        var sv = new WrapPanel();
        var save = Btn("Save JPG…", () => Save(jpg: true), "1280×720 JPG under 2 MB (YouTube's limit)");
        save.SetResourceReference(StyleProperty, "Primary");
        sv.Children.Add(save);
        sv.Children.Add(Btn("Save PNG…", () => Save(jpg: false)));
        sv.Children.Add(Btn("Use for publishing", () => { var f = Save(jpg: true, auto: true); if (f != null) UseForPublish(f); }, "Saves the JPG next to the project and sets it as the YouTube thumbnail in Publish"));
        side.Children.Add(sv);
        side.Children.Add(status);
    }

    string lastTemplate = "Bold left";
    /// <summary>Layouts: text positions, sizes and styles, and the background treatment (the headline text is kept).</summary>
    void ApplyTemplate(string name)
    {
        lastTemplate = name;
        string head = d.Texts.Count > 0 && !string.IsNullOrWhiteSpace(d.Texts[0].Text) ? d.Texts[0].Text : DefaultHeadline();
        string second = d.Texts.Count > 1 ? d.Texts[1].Text : "";
        bool v = d.Vertical;
        d.Texts.Clear(); d.Shapes.Clear();
        d.Darken = 0; d.Blur = 0; d.Pop = 35; d.Zoom = 1; d.PanX = d.PanY = 0; bgKey = "";
        switch (name)
        {
            case "Big center":
                d.Texts.Add(new ThumbText { Text = head, Style = "White Stroke", Size = v ? 150 : 150, X = 0.5, Y = v ? 0.78 : 0.74 });
                d.Darken = 10; break;
            case "Top banner":
                d.Texts.Add(new ThumbText { Text = head, Style = "Red Box", Size = v ? 100 : 82, X = 0.5, Y = v ? 0.14 : 0.16 });
                d.Texts.Add(new ThumbText { Text = second.Length > 0 ? second : "WATCH TO THE END", Style = "Impact Yellow", Size = v ? 110 : 96, X = 0.5, Y = v ? 0.86 : 0.82 });
                break;
            case "Scripture":
                d.Blur = 14; d.Darken = 45; d.Pop = 20;
                d.Texts.Add(new ThumbText { Text = head, Style = "Scripture", Size = v ? 96 : 80, X = 0.5, Y = v ? 0.45 : 0.44 });
                d.Texts.Add(new ThumbText { Text = second.Length > 0 ? second : (main.LastAnalysis?.Scripture.FirstOrDefault() ?? ""), Style = "Gold", Size = v ? 70 : 56, X = 0.5, Y = v ? 0.62 : 0.74 });
                break;
            case "Point at it":
                d.Texts.Add(new ThumbText { Text = head, Style = "Impact Yellow", Size = v ? 130 : 118, X = v ? 0.5 : 0.28, Y = v ? 0.2 : 0.5, Rotation = v ? 0 : -4 });
                d.Shapes.Add(new ThumbShape { Kind = "Arrow", X = v ? 0.5 : 0.56, Y = v ? 0.38 : 0.52, Size = v ? 260 : 220, Rotation = v ? 70 : -10 });
                d.Shapes.Add(new ThumbShape { Kind = "Circle", X = v ? 0.5 : 0.78, Y = v ? 0.6 : 0.5, Size = v ? 420 : 300, Rotation = 0 });
                d.Darken = 10; break;
            case "Minimal":
                d.Texts.Add(new ThumbText { Text = head, Style = "Clean", Size = v ? 90 : 70, X = v ? 0.5 : 0.3, Y = v ? 0.88 : 0.86 });
                d.Pop = 20; break;
            default:            // Bold left
                d.Texts.Add(new ThumbText { Text = head, Style = "Impact Yellow", Size = v ? 140 : 130, X = v ? 0.5 : 0.3, Y = v ? 0.2 : 0.38, Rotation = v ? 0 : -3 });
                d.Texts.Add(new ThumbText { Text = second.Length > 0 ? second : "", Style = "White Stroke", Size = v ? 100 : 86, X = v ? 0.5 : 0.3, Y = v ? 0.32 : 0.62, Rotation = v ? 0 : -3 });
                d.Darken = 20; d.Pop = 45;
                if (d.Subject) d.SubjectX = v ? 0 : 0.18;
                break;
        }
    }

    string DefaultHeadline()
    {
        var a = main.LastAnalysis;
        if (a?.Titles.FirstOrDefault().text is { Length: > 1 } t) return Analyzer.Shorten(t, 28);
        var name = main.ProjectTitle;
        return name.Length > 1 && !name.StartsWith("Untitled", StringComparison.OrdinalIgnoreCase) ? Analyzer.Shorten(name, 28) : "WATCH THIS";
    }

    List<string> TextSuggestions()
    {
        var l = new List<string>();
        var a = main.LastAnalysis;
        if (a != null)
        {
            l.AddRange(a.Titles.Select(t => Analyzer.Shorten(t.text, 28)));
            l.AddRange(a.Scripture.Take(3));
            foreach (var s in a.Transcript.Select(s => s.Text.Trim()).Where(s => s.EndsWith('?') && s.Length is > 8 and < 46).Take(2)) l.Add(s);
        }
        var name = main.ProjectTitle; if (name.Length > 1 && !name.StartsWith("Untitled", StringComparison.OrdinalIgnoreCase)) l.Add(Analyzer.Shorten(name, 28));
        l.AddRange(new[] { "WATCH THIS", "YOU NEED TO HEAR THIS", "GOD'S PLAN FOR YOU", "HOPE IS HERE", "DON'T MISS THIS" });
        return l.Where(s => s.Length > 1).Distinct().Take(10).ToList();
    }

    void Status(string s) => status.Text = s;
    internal void Redraw() { EnsureBackground(); EnsureSilhouette(); canvas.InvalidateVisual(); }

    // ---------------- frame, background, subject ----------------
    async Task LoadFrameAsync()
    {
        Status("Loading the frame…");
        double t = d.Time;
        var g = await Task.Run(() => ThumbFrames.Grab(ed, t, d.Vertical ? 1920 : 1920));
        if (g == null) { Status("No video or picture at that time."); return; }
        (framePx, frameW, frameH) = g.Value;
        bgKey = ""; d.Subject = false; subjectBmp = silhouette = null; subjectAlpha = null; embCache = null; silKey = "";
        Status("");
        BuildSide(); Redraw();
    }

    /// <summary>The background with pop / brightness (a colour LUT) and blur, rebuilt when those change.</summary>
    void EnsureBackground()
    {
        if (framePx == null) { bgProcessed = null; return; }
        string key = FormattableString.Invariant($"{d.Time}|{d.Pop}|{d.Brightness}|{d.Blur}|{frameW}");
        if (key == bgKey && bgProcessed != null) return;
        bgKey = key;
        var px = (byte[])framePx.Clone();
        var g = new ColorGrade { Contrast = d.Pop * 0.45, Saturation = 100 + d.Pop * 0.6, Vibrance = d.Pop * 0.3, Exposure = d.Brightness / 40, Highlights = -d.Pop * 0.2 };
        if (Grade.For(g) is { } lut) Grade.Apply(lut, px, frameW, frameH, frameW * 4);
        BitmapSource bmp = ThumbFrames.ToBitmap(px, frameW, frameH);
        // the cut-out comes from the popped frame before any blur (a blurred background keeps a sharp subject)
        if (d.Subject && subjectAlpha != null)
        {
            var spx = new byte[px.Length];
            for (int i = 0; i < frameW * frameH; i++) { float a = subjectAlpha[i]; spx[i * 4] = (byte)(px[i * 4] * a); spx[i * 4 + 1] = (byte)(px[i * 4 + 1] * a); spx[i * 4 + 2] = (byte)(px[i * 4 + 2] * a); spx[i * 4 + 3] = (byte)(255 * a); }
            var swb = new WriteableBitmap(frameW, frameH, 96, 96, PixelFormats.Pbgra32, null); swb.WritePixels(new Int32Rect(0, 0, frameW, frameH), spx, frameW * 4, 0); swb.Freeze();
            subjectBmp = swb;
        }
        if (d.Blur > 0.5)
        {
            var img = new Image { Source = bmp, Width = frameW, Height = frameH, Effect = new BlurEffect { Radius = d.Blur * frameW / 1280, KernelType = KernelType.Gaussian } };
            var host = new Grid { Width = frameW, Height = frameH, ClipToBounds = true, Background = Brushes.Black }; host.Children.Add(img);
            host.Measure(new Size(frameW, frameH)); host.Arrange(new Rect(0, 0, frameW, frameH));
            var rtb = new RenderTargetBitmap(frameW, frameH, 96, 96, PixelFormats.Pbgra32); rtb.Render(host); rtb.Freeze(); bmp = rtb;
        }
        bgProcessed = bmp;
    }

    /// <summary>The subject's outline: its mask grown by the outline width (separable max filter), as an opacity picture.</summary>
    void EnsureSilhouette()
    {
        if (!d.Subject || subjectAlpha == null) { silhouette = null; return; }
        string key = FormattableString.Invariant($"{d.Outline}|{frameW}|{subjectAlpha.GetHashCode()}");
        if (key == silKey && silhouette != null) return;
        silKey = key;
        silhouette = ThumbRender.Silhouette(subjectAlpha, frameW, frameH, (int)Math.Round(d.Outline * frameW / 1280.0));
    }

    async Task<bool> EnsureSamAsync()
    {
        if (SamEngine.ModelsPresent) return true;
        if (!Alert.Confirm(this, "Cutting out the subject uses the MobileSAM model (about 43 MB, MIT licence) from huggingface.co/Acly/MobileSAM. It is downloaded once and runs on this PC — nothing is uploaded.",
                           "Download the AI model?", "Download", "Not now")) return false;
        try { Status("Downloading the AI model…"); await SamEngine.DownloadAsync(new Progress<double>(p => Status($"Downloading the AI model… {p * 100:0}%")), CancellationToken.None); Status(""); return true; }
        catch (Exception ex) { Alert.Show(this, ex.Message, "AI model download failed", AlertKind.Error); return false; }
    }

    /// <summary>A click on the picture while picking: AI selection at that point (Shift adds, Alt removes), then the cut-out.</summary>
    internal async void PickAt(Point canvasPt)
    {
        if (framePx == null || bgProcessed == null) return;
        var r = ThumbRender.BgRect(d, frameW, frameH);
        float fx = (float)((canvasPt.X - r.X) / r.Width * frameW), fy = (float)((canvasPt.Y - r.Y) / r.Height * frameH);
        if (fx < 0 || fy < 0 || fx >= frameW || fy >= frameH) return;
        float label = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? 0 : 1;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && label == 1) pickPts.Clear();
        Status("Finding the subject…");
        double f = SamEngine.Side / (double)Math.Max(frameW, frameH);
        int w = (int)Math.Round(frameW * f), h = (int)Math.Round(frameH * f);
        pickPts.Add((fx * (float)f, fy * (float)f, label));
        var pts = pickPts.ToList(); var src = framePx; int sw = frameW, shh = frameH; string sig = $"{d.Time}|{frameW}";
        try
        {
            var mask = await Task.Run(() =>
            {
                if (embCache is not { } ec || ec.sig != sig)
                {
                    var small = Resize(src, sw, shh, w, h);
                    sam ??= new SamEngine();
                    embCache = (sig, sam.Encode(small, w, h), w, h, small);
                }
                var (logits, _, _) = sam!.Decode(embCache!.Value.emb, pts, null, w, h);
                return logits;
            });
            // soft alpha at the work size, scaled up to the frame (bilinear), then the cut-out pixels
            var alpha = new float[frameW * frameH];
            for (int y = 0; y < frameH; y++)
                for (int x = 0; x < frameW; x++)
                {
                    double sx = Math.Min(w - 1.001, (x + 0.5) * w / (double)frameW - 0.5), sy = Math.Min(h - 1.001, (y + 0.5) * h / (double)frameH - 0.5);
                    sx = Math.Max(0, sx); sy = Math.Max(0, sy);
                    int x0 = (int)sx, y0 = (int)sy; double ax = sx - x0, ay = sy - y0;
                    double v = mask[y0 * w + x0] * (1 - ax) * (1 - ay) + mask[y0 * w + x0 + 1] * ax * (1 - ay) + mask[(y0 + 1) * w + x0] * (1 - ax) * ay + mask[(y0 + 1) * w + x0 + 1] * ax * ay;
                    alpha[y * frameW + x] = (float)Math.Clamp(0.5 + v * 0.5, 0, 1);
                }
            subjectAlpha = alpha;
            d.Subject = true; silKey = ""; bgKey = "";                       // (EnsureBackground builds the cut-out pixels)
            if (lastTemplate == "Bold left" && !d.Vertical && Math.Abs(d.SubjectX) < 1e-6) d.SubjectX = 0.18;
            Status("Subject cut out. Shift+click to add a part, Alt+click to remove one; click the button again when done.");
            BuildSide(); Redraw();
        }
        catch (Exception ex) { Status("Couldn't cut out the subject: " + ex.Message); }
    }

    static byte[] Resize(byte[] px, int w, int h, int nw, int nh)
    {
        var src = ThumbFrames.ToBitmap(px, w, h);
        var t = new TransformedBitmap(src, new ScaleTransform(nw / (double)w, nh / (double)h));
        var conv = new FormatConvertedBitmap(t, PixelFormats.Bgra32, null, 0);
        var o = new byte[conv.PixelWidth * conv.PixelHeight * 4]; conv.CopyPixels(o, conv.PixelWidth * 4, 0);
        if (conv.PixelWidth == nw && conv.PixelHeight == nh) return o;
        var fixedBuf = new byte[nw * nh * 4];
        for (int y = 0; y < Math.Min(nh, conv.PixelHeight); y++) Buffer.BlockCopy(o, y * conv.PixelWidth * 4, fixedBuf, y * nw * 4, Math.Min(nw, conv.PixelWidth) * 4);
        return fixedBuf;
    }

    async Task SuggestFramesAsync()
    {
        suggestions.Children.Clear();
        if (ed.End <= 0.1) return;
        Status("Looking for good frames…");
        int n = 24; double span = ed.End;
        var res = new List<(double t, double score, BitmapSource bmp)>();
        await Task.Run(() =>
        {
            for (int i = 0; i < n; i++)
            {
                double t = span * (0.03 + 0.94 * i / (n - 1));
                if (ThumbFrames.Grab(ed, t, 320) is { } g)
                    lock (res) res.Add((t, ThumbFrames.Score(g.px, g.w, g.h), Dispatcher.Invoke(() => ThumbFrames.ToBitmap(g.px, g.w, g.h))));
            }
        });
        var picked = new List<(double t, double score, BitmapSource bmp)>();
        foreach (var r in res.OrderByDescending(r => r.score))
            if (picked.All(p => Math.Abs(p.t - r.t) > span / 14)) { picked.Add(r); if (picked.Count == 6) break; }
        foreach (var p in picked.OrderBy(p => p.t))
        {
            var b = new Button { Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 6), ToolTip = $"{TimeUtil.Format(p.t)} · score {p.score:0.00}",
                                 Content = new Image { Source = p.bmp, Width = 100, Height = 100 * p.bmp.PixelHeight / Math.Max(1, p.bmp.PixelWidth), Stretch = Stretch.UniformToFill } };
            double tt = p.t;
            b.Click += async (_, _) => { d.Time = tt; await LoadFrameAsync(); };
            suggestions.Children.Add(b);
        }
        Status(picked.Count > 0 ? "Suggested frames: click one to use it." : "");
    }

    // ---------------- selection, save ----------------
    internal void DeleteSelected(object s)
    {
        if (s is ThumbText t) d.Texts.Remove(t); else if (s is ThumbShape sh) d.Shapes.Remove(sh); else if (s is "subject") { d.Subject = false; subjectBmp = silhouette = null; }
        canvas.Selected = null; BuildSide(); Redraw();
    }

    internal ThumbDesign Design => d;
    internal (BitmapSource? bg, BitmapSource? subject, BitmapSource? sil) Layers => (bgProcessed, subjectBmp, silhouette);
    internal bool Picking => picking;

    /// <summary>Renders the thumbnail and saves it (JPG: quality lowered until it is under 2 MB).</summary>
    internal string? Save(bool jpg, bool auto = false)
    {
        EnsureBackground(); EnsureSilhouette();
        var bmp = ThumbRender.Render(d, bgProcessed, subjectBmp, silhouette);
        string dir = main.ProjectFolder() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        string name = (main.ProjectTitle is { Length: > 0 } pn && !pn.StartsWith("Untitled") ? pn : "thumbnail") + (d.Vertical ? "_cover" : "_thumbnail") + (jpg ? ".jpg" : ".png");
        string file = Path.Combine(dir, name);
        if (!auto)
        {
            var dlg = new SaveFileDialog { Filter = jpg ? "JPG|*.jpg" : "PNG|*.png", FileName = name, InitialDirectory = dir };
            if (dlg.ShowDialog(this) != true) return null;
            file = dlg.FileName;
        }
        try
        {
            long size = ThumbRender.Save(bmp, file, jpg);
            Status($"Saved {Path.GetFileName(file)} ({size / 1024} KB).");
            return file;
        }
        catch (Exception ex) { Status("Couldn't save: " + ex.Message); return null; }
    }

    void UseForPublish(string file)
    {
        foreach (var p in Platforms.All)
            foreach (var f in p.Fields.Where(f => f.Kind == FieldKind.Image))
            {
                if (!ed.Publish.TryGetValue(p.Id, out var post)) ed.Publish[p.Id] = post = new();
                post[f.Id] = file;
            }
        Status($"Saved {Path.GetFileName(file)} and set it as the thumbnail in Publish.");
    }
}

/// <summary>The thumbnail canvas: draws the design scaled to fit, selects and drags texts, shapes, the subject and the picture.</summary>
sealed class ThumbCanvas : FrameworkElement
{
    readonly ThumbnailWindow win;
    public object? Selected;
    Point last; bool dragging;
    public ThumbCanvas(ThumbnailWindow w) { win = w; Focusable = true; Cursor = Cursors.Arrow; }

    (double s, double ox, double oy) Fit()
    {
        var d = win.Design; double s = Math.Min(ActualWidth / d.W, ActualHeight / d.H);
        return (s, (ActualWidth - d.W * s) / 2, (ActualHeight - d.H * s) / 2);
    }
    Point ToDesign(Point p) { var (s, ox, oy) = Fit(); return new Point((p.X - ox) / s, (p.Y - oy) / s); }

    static readonly Pen EdgePen = MakeEdge();                    // (a monitor overlay, like guides: white over the picture area)
    static Pen MakeEdge() { var p = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1); p.Freeze(); return p; }
    protected override void OnRender(DrawingContext dc)
    {
        var d = win.Design; var (s, ox, oy) = Fit();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        dc.PushTransform(new TranslateTransform(ox, oy)); dc.PushTransform(new ScaleTransform(s, s));
        var (bg, subject, sil) = win.Layers;
        ThumbRender.Draw(dc, d, bg, subject, sil);
        var sel = SelectionRect();
        if (sel is Rect r) { var pen = new Pen(Brushes.White, 2 / s) { DashStyle = DashStyles.Dash }; dc.DrawRectangle(null, new Pen(Brushes.Black, 4 / s), r); dc.DrawRectangle(null, pen, r); }
        dc.Pop(); dc.Pop();
        dc.DrawRectangle(null, EdgePen, new Rect(ox - 0.5, oy - 0.5, d.W * s + 1, d.H * s + 1));   // the thumbnail's edges on the dark monitor
        if (win.Picking) dc.DrawText(new FormattedText("Click the subject · Shift+click adds · Alt+click removes", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                                       new Typeface("Segoe UI"), 13, Brushes.White, 1), new Point(ox + 10, oy + 10));
    }

    Rect? SelectionRect()
    {
        var d = win.Design;
        if (Selected is ThumbText t) return ThumbRender.TextGeometry(t, d).box;
        if (Selected is ThumbShape sh) return new Rect(sh.X * d.W - sh.Size / 2, sh.Y * d.H - sh.Size / 2, sh.Size, sh.Size);
        if (Selected is "subject" && win.Layers.bg is { } bg) return ThumbRender.SubjectRect(d, ThumbRender.BgRect(d, bg.PixelWidth, bg.PixelHeight));
        return null;
    }

    object? HitTestDesign(Point p)
    {
        var d = win.Design;
        for (int i = d.Texts.Count - 1; i >= 0; i--) { var b = ThumbRender.TextGeometry(d.Texts[i], d).box; b.Inflate(10, 10); if (b.Contains(p)) return d.Texts[i]; }
        for (int i = d.Shapes.Count - 1; i >= 0; i--) { var sh = d.Shapes[i]; if (Math.Abs(p.X - sh.X * d.W) < sh.Size / 2 && Math.Abs(p.Y - sh.Y * d.H) < sh.Size / 2) return sh; }
        if (d.Subject && win.Layers.bg is { } bg && ThumbRender.SubjectRect(d, ThumbRender.BgRect(d, bg.PixelWidth, bg.PixelHeight)) is var sr && sr.Contains(p)) return "subject";
        return "background";
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = ToDesign(e.GetPosition(this));
        if (win.Picking) { win.PickAt(p); e.Handled = true; return; }
        Selected = HitTestDesign(p); last = p; dragging = true; CaptureMouse(); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging) return;
        var p = ToDesign(e.GetPosition(this)); var d = win.Design;
        double dx = p.X - last.X, dy = p.Y - last.Y; last = p;
        switch (Selected)
        {
            case ThumbText t: t.X += dx / d.W; t.Y += dy / d.H; break;
            case ThumbShape s: s.X += dx / d.W; s.Y += dy / d.H; break;
            case "subject": d.SubjectX += dx / d.W; d.SubjectY += dy / d.H; break;
            default:
                if (win.Layers.bg is { } bg)
                {
                    var r = ThumbRender.BgRect(d, bg.PixelWidth, bg.PixelHeight);
                    double mx = (d.W - r.Width) / 2, my = (d.H - r.Height) / 2;
                    if (Math.Abs(mx) > 1) d.PanX = Math.Clamp(d.PanX - dx / mx, -1, 1);
                    if (Math.Abs(my) > 1) d.PanY = Math.Clamp(d.PanY - dy / my, -1, 1);
                }
                break;
        }
        win.Redraw();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (!dragging) return; dragging = false; ReleaseMouseCapture(); }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double k = e.Delta > 0 ? 1.06 : 1 / 1.06; var d = win.Design;
        switch (Selected)
        {
            case ThumbText t: t.Size = Math.Clamp(t.Size * k, 20, 400); break;
            case ThumbShape s: s.Size = Math.Clamp(s.Size * k, 40, 900); break;
            case "subject": d.SubjectScale = Math.Clamp(d.SubjectScale * k, 0.4, 3); break;
            default: d.Zoom = Math.Clamp(d.Zoom * k, 1, 3); break;
        }
        win.Redraw(); e.Handled = true;
    }
}
