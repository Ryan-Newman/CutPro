using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Path = System.Windows.Shapes.Path;

namespace CutPro;

/// <summary>
/// Draws a title on a 1280x720 surface as vector layers (background box, drop shadow, glow, outline, fill).
/// The live element is the preview; <see cref="Render"/> rasterises the very same element for export, so titles
/// look identical in both. In matte mode it draws the text-mask shape instead (white = keep).
/// </summary>
public class TitleVisual : Canvas
{
    static int W => Editor.W;
    const int H = Editor.H;
    string sig = "";

    public TitleVisual() { Width = W; Height = H; IsHitTestVisible = false; }
    void Fit() { if (Width != W) Width = W; }

    /// <summary>Scale / position / rotation / blur of a title at one moment (animated per frame without rebuilding the text).</summary>
    public readonly record struct Motion(double Scale, double X, double Y, double Rotation, double Blur);
    public static Motion MotionOf(Clip c) => new(c.Scale, c.X, c.Y, c.Rotation, c.Blur);
    public static Motion MotionAt(Clip c, double t) => new(Anim.Value(c, AnimProp.Scale, t), Anim.Value(c, AnimProp.X, t), Anim.Value(c, AnimProp.Y, t),
                                                            Anim.Value(c, AnimProp.Rotation, t), Anim.Value(c, AnimProp.Blur, t));
    readonly ScaleTransform msc = new(); readonly RotateTransform mro = new(); readonly TranslateTransform mtr = new();
    bool matteBuilt;

    /// <summary>What the drawing depends on, except motion (applied separately).</summary>
    public static string Signature(Clip c, bool matte) =>
        $"{matte}|{c.Text}|{c.Mask}|{c.MaskFeather}|{JsonSerializer.Serialize(c.Look)}|{(TitleAnim.Units(c.TextFx) ? JsonSerializer.Serialize(c.TextFx) : "")}";

    /// <summary>Rebuilds the drawing if anything that affects it changed (cheap when nothing did).</summary>
    /// <param name="local">seconds since the clip's start (text animation and roll / crawl; null = the text fully shown, in place)</param>
    public void Update(Clip c, bool matte = false, Motion? motion = null, double? local = null)
    {
        Fit();
        var s = Signature(c, matte);
        if (s != sig) Build(c, matte, s);
        var mo = motion ?? MotionOf(c);
        if (msc.ScaleX != mo.Scale / 100) { msc.ScaleX = msc.ScaleY = mo.Scale / 100; }
        if (mro.Angle != mo.Rotation) mro.Angle = mo.Rotation;
        double tx = W / 2.0 + mo.X * W / 100, ty = H / 2.0 + mo.Y * H / 100;
        if (local is double lt && c.TextFx is { Roll: not TextRoll.None } fx && !matte)
        {   // roll: the block's top enters at the bottom edge, its bottom leaves at the top; crawl: right edge in, left edge out
            double u = TitleAnim.RollU(lt, c.Dur), k = mo.Scale / 100;
            if (fx.Roll == TextRoll.Roll) ty = H + blockR.Height / 2 * k + u * (-blockR.Height * k - H);
            else tx = W - (blockR.Left - anchorX) * k + u * (-(blockR.Right - anchorX) * k - W + (blockR.Left - anchorX) * k);
        }
        if (units != null && c.TextFx is { } ta)
        {
            double em = Math.Max(1, c.Look.Size);
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                var (op, dy, scl) = local is double lu ? TitleAnim.State(ta, u.Idx, unitCounts, lu, c.Dur) : (1.0, 0.0, 1.0);
                if (u.El.Opacity != op) u.El.Opacity = op;
                if (u.Sc.ScaleX != scl) { u.Sc.ScaleX = u.Sc.ScaleY = scl; }
                if (u.Tr.Y != dy * em) u.Tr.Y = dy * em;
            }
        }
        if (mtr.X != tx) mtr.X = tx; if (mtr.Y != ty) mtr.Y = ty;
        if (!matteBuilt)
        {
            if (mo.Blur <= 0.05) { if (Effect != null) Effect = null; }
            else if (Effect is BlurEffect be) { if (Math.Abs(be.Radius - mo.Blur) > 1e-3) be.Radius = mo.Blur; }
            else Effect = Blur(mo.Blur);
        }
    }

    /// <summary>Text animation: each unit (letter / word / line) on its own canvas with its own layers, opacity and transform (null = whole text).</summary>
    sealed record Unit(Canvas El, ScaleTransform Sc, TranslateTransform Tr, (int c, int w, int l) Idx);
    List<Unit>? units;
    (int c, int w, int l) unitCounts;
    Rect blockR; double anchorX;

    void Build(Clip c, bool matte, string s)
    {
        sig = s;
        Children.Clear(); Effect = null; units = null;
        var st = c.Look;
        if (!matte && TitleAnim.Units(c.TextFx))
        {
            var parts = UnitLayout(c.Text, st, out var ub);
            blockR = ub;
            var uxf = Placement(st, ub);
            matteBuilt = false;
            var ul = new Canvas { RenderTransform = uxf };
            Children.Add(ul);
            if (st.Box)
            {
                var r = ub; r.Inflate(st.BoxPadding, st.BoxPadding);
                ul.Children.Add(new Path { Data = new RectangleGeometry(r, st.BoxRadius, st.BoxRadius), Fill = Solid(st.BoxColor, st.BoxOpacity / 100) });
            }
            units = new();
            // letters scale about the centre of the unit the animation uses (a popping word grows as a whole)
            var kind = TitleAnim.UnitOf(c.TextFx!);
            Rect GroupBounds(int w, int l) => parts.Where(p => kind == TextUnit.Word ? p.w == w : p.l == l).Select(p => p.b).Aggregate(Rect.Union);
            foreach (var (g, b, ci, wi, li) in parts)
            {
                var gb = kind == TextUnit.Char ? b : GroupBounds(wi, li);
                var sc = new ScaleTransform(1, 1, gb.X + gb.Width / 2, gb.Y + gb.Height / 2); var tr = new TranslateTransform();
                var u = new Canvas { RenderTransform = new TransformGroup { Children = { sc, tr } } };
                Layers(u, g, st);
                ul.Children.Add(u);
                units.Add(new Unit(u, sc, tr, (ci, wi, li)));
            }
            unitCounts = parts.Count == 0 ? (1, 1, 1) : (parts.Count, parts.Max(p => p.w) + 1, parts.Max(p => p.l) + 1);
            return;
        }
        var geom = Layout(c.Text, st, out var block);
        blockR = block;
        var xf = Placement(st, block);
        matteBuilt = matte;
        Geometry outlined = st.Stroke && st.StrokeWidth > 0 ? Group(geom, geom.GetWidenedPathGeometry(new Pen(Brushes.Black, st.StrokeWidth * 2) { LineJoin = PenLineJoin.Round })) : geom;

        if (matte)
        {
            var placed = Group(outlined); placed.Transform = xf;
            double pad = 3 * c.MaskFeather + 2;   // keep the frame's own edges off-screen when feathering a cut-out
            Geometry shape = c.Mask == MaskMode.CutOut
                ? new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(-pad, -pad, W + 2 * pad, H + 2 * pad)), placed)
                : placed;
            Children.Add(new Path { Data = shape, Fill = Brushes.White, Effect = Blur(c.MaskFeather) });
            return;
        }

        var layer = new Canvas { RenderTransform = xf };          // all layers in text-block coordinates
        Children.Add(layer);
        if (st.Box)
        {
            var r = block; r.Inflate(st.BoxPadding, st.BoxPadding);
            layer.Children.Add(new Path { Data = new RectangleGeometry(r, st.BoxRadius, st.BoxRadius), Fill = Solid(st.BoxColor, st.BoxOpacity / 100) });
        }
        Layers(layer, geom, st);
    }

    /// <summary>The text's layers (shadow, glow, outline, fill) for a geometry, into a panel.</summary>
    void Layers(Panel layer, Geometry geom, TextStyle st)
    {
        Geometry outlined = st.Stroke && st.StrokeWidth > 0 ? Group(geom, geom.GetWidenedPathGeometry(new Pen(Brushes.Black, st.StrokeWidth * 2) { LineJoin = PenLineJoin.Round })) : geom;
        if (st.Shadow && st.ShadowOpacity > 0)
        {
            double a = st.ShadowAngle * Math.PI / 180;
            layer.Children.Add(new Path
            {
                Data = outlined, Fill = Solid(st.ShadowColor, st.ShadowOpacity / 100), Effect = Blur(st.ShadowBlur),
                RenderTransform = new TranslateTransform(Math.Sin(a) * st.ShadowDistance, -Math.Cos(a) * st.ShadowDistance)
            });
        }
        if (st.Glow && st.GlowOpacity > 0 && st.GlowSize > 0)
            layer.Children.Add(new Path
            {
                Data = Group(outlined, outlined.GetWidenedPathGeometry(new Pen(Brushes.Black, st.GlowSize / 2) { LineJoin = PenLineJoin.Round })),
                Fill = Solid(st.GlowColor, st.GlowOpacity / 100), Effect = Blur(st.GlowSize)
            });
        if (st.Stroke && st.StrokeWidth > 0)   // centred stroke drawn under the fill = an outline of StrokeWidth outside the letters
            layer.Children.Add(new Path { Data = geom, Stroke = Solid(st.StrokeColor, 1), StrokeThickness = st.StrokeWidth * 2, StrokeLineJoin = PenLineJoin.Round });
        layer.Children.Add(new Path { Data = geom, Fill = FillBrush(st) });
    }

    /// <summary>Text geometry split into letters in block coordinates, laid out letter by letter (with tracking) like <see cref="Layout"/>: each
    /// letter's bounds and its letter / word / line number (an animation by word or line moves its letters together).</summary>
    static List<(Geometry g, Rect b, int c, int w, int l)> UnitLayout(string text, TextStyle st, out Rect block)
    {
        var tf = new Typeface(new FontFamily(st.Font), st.Italic ? FontStyles.Italic : FontStyles.Normal,
                              FontWeight.FromOpenTypeWeight(Math.Clamp(st.Weight, 1, 999)), FontStretches.Normal);
        var lines = (st.AllCaps ? text.ToUpper(CultureInfo.CurrentCulture) : text).Replace("\r", "").Split('\n');
        double size = Math.Max(1, st.Size), lh = size * Math.Max(10, st.Leading) / 100, extra = st.Tracking / 1000 * size;
        var laid = new List<(List<(Geometry g, bool space)> els, double w, FormattedText probe)>();
        foreach (var line in lines)
        {
            var els = new List<(Geometry g, bool space)>();
            var probe = FT(line.Length > 0 ? line : " ", tf, size);
            double x = 0; bool first = true;
            var e = StringInfo.GetTextElementEnumerator(line);
            while (e.MoveNext())
            {
                if (!first) x += extra;
                first = false;
                var s = (string)e.Current;
                var ft = FT(s, tf, size);
                els.Add((ft.BuildGeometry(new Point(x, 0)), string.IsNullOrWhiteSpace(s)));
                x += ft.WidthIncludingTrailingWhitespace;
            }
            laid.Add((els, Math.Max(0, x), probe));
        }
        double maxW = Math.Max(1, laid.Max(l => l.w));
        var result = new List<(Geometry g, Rect b, int c, int w, int l)>();
        int word = 0, lineNo = 0;
        for (int i = 0; i < laid.Count; i++)
        {
            var (els, w, probe) = laid[i];
            double dx = st.Align == "Left" ? 0 : st.Align == "Right" ? maxW - w : (maxW - w) / 2;
            double dy = i * lh + (lh - probe.Height) / 2;
            var move = new TranslateTransform(dx, dy);
            bool inWord = false, any = false;
            foreach (var (g, space) in els)
            {
                if (space) { if (inWord) word++; inWord = false; continue; }
                inWord = true; any = true;
                var gg = new GeometryGroup { FillRule = FillRule.Nonzero, Transform = move };
                gg.Children.Add(g);
                var b = gg.Bounds;
                result.Add((gg, b.IsEmpty ? new Rect(dx, dy, 1, 1) : b, result.Count, word, lineNo));
            }
            if (inWord) word++;
            if (any) lineNo++;                                                  // (empty lines count for nothing)
        }
        block = new Rect(0, 0, maxW, laid.Count * lh);
        return result;
    }

    /// <summary>Text geometry in block coordinates (0,0 = top-left of the text block) with tracking, leading, alignment, underline.</summary>
    static Geometry Layout(string text, TextStyle st, out Rect block)
    {
        var tf = new Typeface(new FontFamily(st.Font), st.Italic ? FontStyles.Italic : FontStyles.Normal,
                              FontWeight.FromOpenTypeWeight(Math.Clamp(st.Weight, 1, 999)), FontStretches.Normal);
        var lines = (st.AllCaps ? text.ToUpper(CultureInfo.CurrentCulture) : text).Replace("\r", "").Split('\n');
        double size = Math.Max(1, st.Size), lh = size * Math.Max(10, st.Leading) / 100, extra = st.Tracking / 1000 * size;
        var laid = new List<(GeometryGroup g, double w, FormattedText probe)>();
        foreach (var line in lines)
        {
            var g = new GeometryGroup { FillRule = FillRule.Nonzero };
            var probe = FT(line.Length > 0 ? line : " ", tf, size);
            double x = 0;
            if (line.Length > 0 && Math.Abs(extra) < 1e-6)
            {
                g.Children.Add(probe.BuildGeometry(new Point(0, 0)));
                x = probe.WidthIncludingTrailingWhitespace;
            }
            else if (line.Length > 0)
            {
                var e = StringInfo.GetTextElementEnumerator(line);
                bool first = true;
                while (e.MoveNext())
                {
                    if (!first) x += extra;
                    first = false;
                    var ft = FT((string)e.Current, tf, size);
                    g.Children.Add(ft.BuildGeometry(new Point(x, 0)));
                    x += ft.WidthIncludingTrailingWhitespace;
                }
            }
            laid.Add((g, Math.Max(0, x), probe));
        }
        double maxW = Math.Max(1, laid.Max(l => l.w));
        var all = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (int i = 0; i < laid.Count; i++)
        {
            var (g, w, probe) = laid[i];
            double dx = st.Align == "Left" ? 0 : st.Align == "Right" ? maxW - w : (maxW - w) / 2;
            double dy = i * lh + (lh - probe.Height) / 2;
            g.Transform = new TranslateTransform(dx, dy);
            all.Children.Add(g);
            if (st.Underline && w > 0)
                all.Children.Add(new RectangleGeometry(new Rect(dx, dy + probe.Baseline + size * 0.09, w, Math.Max(1, size * 0.055))));
        }
        block = new Rect(0, 0, maxW, laid.Count * lh);
        return all;
    }

    /// <summary>Block → frame: anchor (left/centre/right edge by alignment, vertical centre) at the clip position, scaled and rotated about it.</summary>
    Transform Placement(TextStyle st, Rect block)
    {
        double ax = st.Align == "Left" ? block.Left : st.Align == "Right" ? block.Right : block.Left + block.Width / 2;
        anchorX = ax;
        var g = new TransformGroup();
        g.Children.Add(new TranslateTransform(-ax, -(block.Top + block.Height / 2)));
        g.Children.Add(msc);
        g.Children.Add(mro);
        g.Children.Add(mtr);
        return g;
    }

    static FormattedText FT(string s, Typeface tf, double size) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, Brushes.White, 1.0);
    static GeometryGroup Group(params Geometry[] gs) { var g = new GeometryGroup { FillRule = FillRule.Nonzero }; foreach (var x in gs) g.Children.Add(x); return g; }

    public static Color ParseColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
    }
    static Brush Solid(string hex, double opacity)
    {
        var b = new SolidColorBrush(ParseColor(hex, Colors.White)) { Opacity = Math.Clamp(opacity, 0, 1) };
        b.Freeze(); return b;
    }
    static Brush FillBrush(TextStyle st)
    {
        if (!st.Gradient) return Solid(st.Fill, 1);
        double a = st.GradientAngle * Math.PI / 180, cx = Math.Cos(a) / 2, cy = Math.Sin(a) / 2;
        var b = new LinearGradientBrush(ParseColor(st.Fill, Colors.White), ParseColor(st.Fill2, Colors.White), new Point(0.5 - cx, 0.5 - cy), new Point(0.5 + cx, 0.5 + cy));
        b.Freeze(); return b;
    }
    public static Effect? Blur(double radius) => radius > 0.05 ? new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian } : null;

    /// <summary>Rasterises a title: its look on transparency, or its matte (white = keep) — on transparency for the preview's
    /// opacity mask, or opaque on black for the exporter. outW × outH: the export size (the logical canvas is scaled to it;
    /// vectors stay sharp); 0 = the logical canvas.</summary>
    public static BitmapSource Render(Clip c, bool matte, bool opaque = false, Motion? motion = null, double opacity = 1, int outW = 0, int outH = 0)
    {
        var (root, tv) = Root(opaque, outW, outH);
        tv.Update(c, matte, motion);
        return Snap(root, opacity, outW, outH);
    }

    /// <summary>A title visual inside a root sized for the export (black backing for mattes), scaled from the logical canvas.</summary>
    static (FrameworkElement root, TitleVisual tv) Root(bool opaque, int outW, int outH)
    {
        var tv = new TitleVisual();
        int rw = outW > 0 ? outW : W, rh = outH > 0 ? outH : H;
        var g = new Grid { Width = rw, Height = rh, Background = opaque ? Brushes.Black : null };
        var box = new Canvas { Width = W, Height = H, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,   // scale from the corner, not centred in the larger grid
                               RenderTransform = new ScaleTransform(rw / (double)W, rh / (double)H) };
        box.Children.Add(tv); g.Children.Add(box);
        return (g, tv);
    }

    static BitmapSource Snap(FrameworkElement root, double opacity, int outW, int outH)
    {
        int rw = outW > 0 ? outW : W, rh = outH > 0 ? outH : H;
        root.Opacity = opacity;
        root.Measure(new Size(rw, rh)); root.Arrange(new Rect(0, 0, rw, rh)); root.UpdateLayout();
        var rtb = new RenderTargetBitmap(rw, rh, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Animated title → FFV1 (bgra) video at the export size, one frame per sequence frame; unchanged frames are reused.</summary>
    static bool RenderSequence(string ff, Clip c, bool matte, string file, int outW, int outH, Transitions.Plan? plan = null)
    {
        int n = Math.Max(1, (int)Math.Round(c.Dur * TimeUtil.Fps));
        var psi = new System.Diagnostics.ProcessStartInfo(ff) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{outW}x{outH}", "-r", TimeUtil.Rate,
                                  "-i", "-", "-c:v", "ffv1", "-pix_fmt", "bgra", file }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        var stdin = p.StandardInput.BaseStream;
        var (root, tv) = Root(matte, outW, outH);
        var px = new byte[outW * outH * 4];
        (Motion mo, double op, int k)? last = null;
        bool textAnim = TitleAnim.Active(c.TextFx) && !matte;                  // (text animation: every frame drawn)
        for (int k = 0; k < n; k++)
        {
            double t = c.Start + k / TimeUtil.Fps;
            var lk = plan != null && !matte ? Transitions.Look(plan, c, t, _ => null) : default;   // (a Move transition: moved and faded like the preview)
            var cur = (lk.Moves ? lk.Apply(MotionAt(c, t)) : MotionAt(c, t), Math.Clamp(Anim.Value(c, AnimProp.Opacity, t) / 100 * (lk.Moves ? lk.Opacity : 1), 0, 1), textAnim ? k : 0);
            if (last != cur)
            {
                tv.Update(c, matte, cur.Item1, k / TimeUtil.Fps);
                new FormatConvertedBitmap(Snap(root, cur.Item2, outW, outH), PixelFormats.Bgra32, null, 0).CopyPixels(px, outW * 4, 0);   // straight alpha for ffmpeg
                last = cur;
            }
            stdin.Write(px, 0, px.Length);
        }
        stdin.Close();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    /// <summary>Writes each title for the exporter at outW × outH (0 = the sequence's output size): PNG (look, or opaque matte
    /// for masking titles), or an FFV1 sequence when animated. UI thread.</summary>
    public static Dictionary<string, string> PrepareExport(Editor ed, string dir, int outW = 0, int outH = 0, bool skipCaptions = false)
    {
        if (outW <= 0 || outH <= 0) { outW = Editor.OutW; outH = Editor.OutH; }
        Directory.CreateDirectory(dir);
        var map = new Dictionary<string, string>();
        var plan = Transitions.Compute(ed);
        foreach (var c in Transitions.Extended(ed.Clips, plan).Where(c => c.Kind == ClipKind.Text && !ed.TrackOff[c.Track] && !(skipCaptions && Captioning.IsCaption(c))))   // (lengthened by transitions; captions not burned in: none)
        {
            bool matte = c.Mask != MaskMode.None;
            if ((Anim.AnyAnimated(c, Anim.Visual) || Transitions.Moves(plan, c) || TitleAnim.Active(c.TextFx) && !matte) && Exporter.FindFfmpeg() is { } ff)
            {
                // animated: every frame through this renderer (motion + opacity baked in) → lossless BGRA video
                var mkv = System.IO.Path.Combine(dir, c.Id + ".mkv");
                if (RenderSequence(ff, c, matte, mkv, outW, outH, plan)) { map[c.Id] = mkv; continue; }
            }
            var bmp = new FormatConvertedBitmap(Render(c, matte, opaque: matte, outW: outW, outH: outH), PixelFormats.Bgra32, null, 0);   // straight alpha for ffmpeg
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            var png = System.IO.Path.Combine(dir, c.Id + ".png");
            using (var fs = File.Create(png)) enc.Save(fs);
            map[c.Id] = png;
        }
        return map;
    }
}
