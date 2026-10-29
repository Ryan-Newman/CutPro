using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// The selected title's selected shape layer on the program monitor (above the other overlays, on the logical canvas): its box as it shows at the
/// playhead (its own place / size / turn and animation, then the title's motion). Drag inside = move it (Shift: one axis only); drag a corner =
/// resize it about its centre (Shift: keep its shape; lines / arrows: their length; text layers: their font size). One undo step per drag; hidden
/// while playing or while an object tool is on; only the box takes the mouse.
/// </summary>
public sealed class ShapeOverlay : FrameworkElement
{
    public Editor Ed = null!;
    public bool Playing;
    public Func<bool> Enabled = () => true;
    /// <summary>The layer selected in Effect Controls' list.</summary>
    public Func<int> Layer = () => 0;
    /// <summary>A drag ended (refresh Effect Controls).</summary>
    public Action? Edited;

    public ShapeOverlay() { Width = Editor.W; Height = Editor.H; Focusable = false; Editor.SequenceChanged += () => { Width = Editor.W; InvalidateVisual(); }; }

    public (Clip c, ShapeLayer s)? Target =>
        !Playing && Enabled() && Ed?.Selected is { Kind: ClipKind.Text, Shapes.Count: > 0 } c && Ed.Time >= c.Start - 1e-6 && Ed.Time < c.End && Layer() is int i && i >= 0 && i < c.Shapes!.Count
            ? (c, c.Shapes[i]) : null;

    /// <summary>The layer's own coordinates → the monitor's, at the playhead (as <see cref="TitleVisual"/> draws it).</summary>
    public Matrix ToScreen(Clip c, ShapeLayer s)
    {
        var mo = TitleVisual.MotionAt(c, Ed.Time);
        var st = Shapes.State(s, Ed.Time - c.Start, c.Dur);
        var m = Matrix.Identity;
        m.Scale(st.Scale, st.Scale); m.Rotate(s.Rotation + st.Rotation); m.Translate(s.X + st.Dx, s.Y + st.Dy);
        m.Scale(mo.Scale / 100, mo.Scale / 100); m.Rotate(mo.Rotation); m.Translate(Editor.W / 2.0 + mo.X * Editor.W / 100, Editor.H / 2.0 + mo.Y * Editor.H / 100);
        return m;
    }
    static Point[] Corners(Rect b) => new[] { b.TopLeft, b.TopRight, b.BottomRight, b.BottomLeft };

    double Unit()
    {
        if (PresentationSource.FromVisual(this) is not { RootVisual: Visual root } || !root.IsAncestorOf(this)) return 1;
        var t = TransformToAncestor(root);
        double s = (t.Transform(new Point(100, 0)) - t.Transform(new Point(0, 0))).Length / 100;
        return s > 1e-6 ? 1 / s : 1;
    }

    /// <summary>−1 = nothing, 0 … 3 = a corner (TL, TR, BR, BL), 4 = inside.</summary>
    internal int Hit(Point pt)
    {
        if (Target is not var (c, s)) return -1;
        var m = ToScreen(c, s); var box = TitleVisual.LayerBox(s);
        double r = 9 * Unit();
        for (int i = 0; i < 4; i++) if ((m.Transform(Corners(box)[i]) - pt).Length <= r) return i;
        if (!m.HasInverse) return -1;
        var inv = m; inv.Invert();
        var lp = inv.Transform(pt);
        var grown = box; grown.Inflate(4, 4);
        return grown.Contains(lp) ? 4 : -1;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters p) => drag != null || Hit(p.HitPoint) >= 0 ? new PointHitTestResult(this, p.HitPoint) : null;

    sealed class Drag { public Clip C = null!; public ShapeLayer S = null!; public int Part; public Point Start; public double X0, Y0, W0, H0, Size0; public Point Local0; public Matrix M0; }
    Drag? drag; int hover = -1;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !Begin(e.GetPosition(this))) return;
        CaptureMouse(); e.Handled = true;
    }

    /// <summary>A drag starts at pt (the mouse, or a test): true when it is on the layer's box or a corner.</summary>
    internal bool Begin(Point pt)
    {
        if (Target is not var (c, s)) return false;
        int part = Hit(pt);
        if (part < 0) return false;
        drag = new Drag { C = c, S = s, Part = part, Start = pt, X0 = s.X, Y0 = s.Y, W0 = s.W, H0 = s.H, Size0 = s.Style?.Size ?? 40, M0 = ToScreen(c, s),
                          Local0 = part < 4 ? Corners(TitleVisual.LayerBox(s))[part] : default };
        Ed.Checkpoint("Move Shape Layer");
        InvalidateVisual();
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pt = e.GetPosition(this);
        if (drag == null)
        {
            int h = Hit(pt);
            if (h != hover) { hover = h; Cursor = h == 4 ? Cursors.SizeAll : h >= 0 ? Cursors.SizeNWSE : null; InvalidateVisual(); }
            return;
        }
        MoveTo(pt, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    /// <summary>The drag at pt: the layer moved (part 4) or resized about its centre (a corner).</summary>
    internal void MoveTo(Point pt, bool shift)
    {
        if (drag == null) return;
        var d = pt - drag.Start;
        var s = drag.S;
        if (drag.Part == 4)
        {   // move: the screen offset back into the title's own coordinates (its turn and scale undone)
            if (shift) { if (Math.Abs(d.X) >= Math.Abs(d.Y)) d.Y = 0; else d.X = 0; }
            var mo = TitleVisual.MotionAt(drag.C, Ed.Time);
            var back = Matrix.Identity; back.Rotate(-mo.Rotation); back.Scale(100 / Math.Max(1, mo.Scale), 100 / Math.Max(1, mo.Scale));
            var a = back.Transform(d);
            s.X = Math.Round(drag.X0 + a.X); s.Y = Math.Round(drag.Y0 + a.Y);
        }
        else if (drag.M0.HasInverse)
        {   // resize about the centre: the corner's new place in the layer's own coordinates
            var inv = drag.M0; inv.Invert();
            var lp = inv.Transform(pt);
            double fx = Math.Abs(drag.Local0.X) > 1e-6 ? Math.Abs(lp.X / drag.Local0.X) : 1, fy = Math.Abs(drag.Local0.Y) > 1e-6 ? Math.Abs(lp.Y / drag.Local0.Y) : 1;
            if (shift || s.Kind is ShapeKind.Text or ShapeKind.Sticker) fx = fy = Math.Max(fx, fy);
            fx = Math.Clamp(fx, 0.05, 20); fy = Math.Clamp(fy, 0.05, 20);
            if (s.Kind == ShapeKind.Text) { if (s.Style != null) s.Style.Size = Math.Round(Math.Clamp(drag.Size0 * fx, 6, 600)); }
            else
            {
                s.W = Math.Round(Math.Max(4, drag.W0 * fx));
                if (s.Kind is not (ShapeKind.Line or ShapeKind.Arrow)) s.H = Math.Round(Math.Max(4, drag.H0 * fy));
            }
        }
        Ed.Touch(); InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (drag == null) return;
        ReleaseMouseCapture(); End();
        e.Handled = true;
    }
    /// <summary>The drag ends: one undo step.</summary>
    internal void End()
    {
        if (drag == null) return;
        drag = null;
        Ed.Commit(); Edited?.Invoke(); InvalidateVisual();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { if (drag != null) { drag = null; Ed.Commit(); Edited?.Invoke(); InvalidateVisual(); } }
    protected override void OnMouseLeave(MouseEventArgs e) { if (hover >= 0 && drag == null) { hover = -1; Cursor = null; InvalidateVisual(); } }

    protected override void OnRender(DrawingContext dc)
    {
        if (Target is not var (c, s)) return;
        var m = ToScreen(c, s); double u = Unit();
        var pts = Corners(TitleVisual.LayerBox(s)).Select(m.Transform).ToArray();
        var g = new StreamGeometry();
        using (var ctx = g.Open()) { ctx.BeginFigure(pts[0], false, true); ctx.PolyLineTo(pts[1..], true, true); }
        g.Freeze();
        var accent = ThemeManager.Brush("Accent");
        dc.DrawGeometry(null, new Pen(Brushes.Black, 3 * u), g);
        dc.DrawGeometry(null, new Pen(accent, 1.4 * u) { DashStyle = drag?.Part == 4 || hover == 4 ? null : DashStyles.Dash }, g);
        for (int i = 0; i < 4; i++)
        {
            double sz = (drag?.Part == i || hover == i ? 6 : 4.5) * u;
            dc.DrawRectangle(drag?.Part == i || hover == i ? accent : Brushes.White, new Pen(Brushes.Black, 1.2 * u), new Rect(pts[i].X - sz, pts[i].Y - sz, 2 * sz, 2 * sz));
        }
    }
}

public partial class Editor
{
    /// <summary>A motion graphics template as a new title at the playhead: on the lowest track above V1 that is free for its length (else V2 from
    /// the first free time), scaled down for narrow formats (it is made for 16:9). One undo step; the new clip is selected.</summary>
    public Clip? AddGraphic(GraphicTemplate t)
    {
        var c = GraphicLibrary.Instance(t);
        double start = Frame(Time);
        c.Dur = Math.Max(OneFrame, Frame(c.Dur));
        int track = Tracks.VideoRows.Skip(1).FirstOrDefault(r => !Locked(r) && !Collides(r, start, c.Dur, null), -1);
        if (track < 0)
        {
            track = Tracks.VideoRows.Skip(1).Append(Tracks.V1).FirstOrDefault(r => !Locked(r), -1);
            if (track < 0) return null;
            start = FreeStart(track, start, c.Dur);
        }
        if (W < 1280) c.Scale = Math.Round(c.Scale * Math.Clamp(W / 1280.0 * 1.6, 0.45, 1), 1);
        c.Track = track; c.Start = start;
        Edit(() => Clips.Add(c), "Add Graphic");
        Select(c);
        return c;
    }
}

/// <summary>
/// Motion graphics (partial MainWindow): the Titles view's Graphics gallery (built-in templates by category and the user's own: click = a new
/// graphic at the playhead, right-click a saved one = delete), Effect Controls ▸ Shapes and stickers for a title (add shapes, arrows, speech
/// bubbles, texts and stickers; order, duplicate, delete; place, size, turn, colours, outline, shadow, over / under the text, in / out
/// animation with a delay, loops), and Save as Graphic Template…. Everything is drawn by <see cref="TitleVisual"/>.
/// </summary>
public partial class MainWindow
{
    int shapeSel; string? shapeClip;
    ContextMenu? lastStickerMenu;
    ShapeOverlay? shapeOverlay;

    /// <summary>The monitor's shape-layer handles (InitObjects, next to the corner pin's).</summary>
    void InitShapeOverlay()
    {
        shapeOverlay = new ShapeOverlay { Ed = ed, Enabled = () => Mo.Tool == ObjectTool.None && Mo.PickColor == null, Layer = () => shapeSel, Edited = () => BuildInspector() };
        ((Grid)Po.Parent).Children.Add(shapeOverlay);
        ed.PropsChanged += shapeOverlay.InvalidateVisual; ed.Seeked += shapeOverlay.InvalidateVisual; ed.Changed += shapeOverlay.InvalidateVisual;
    }
    static readonly Dictionary<string, BitmapSource> graphicThumbs = new();

    /// <summary>A new layer of a kind, sized for a 1280 × 720 frame.</summary>
    static ShapeLayer NewShape(ShapeKind k) => k switch
    {
        ShapeKind.Rectangle => new ShapeLayer { Kind = k, W = 420, H = 120, Radius = 10, Fill = "#D0101418" },
        ShapeKind.Ellipse => new ShapeLayer { Kind = k, W = 200, H = 200, Fill = "#FF3DA5FF" },
        ShapeKind.Polygon => new ShapeLayer { Kind = k, W = 180, H = 180, Sides = 6, Fill = "#FF3DA5FF" },
        ShapeKind.Star => new ShapeLayer { Kind = k, W = 180, H = 180, Sides = 5, Inner = 45, Fill = "#FFFFC93C", Above = true },
        ShapeKind.Line => new ShapeLayer { Kind = k, W = 360, H = 20, Thickness = 8, Fill = "#FFFFFFFF", Above = true },
        ShapeKind.Arrow => new ShapeLayer { Kind = k, W = 260, H = 60, Thickness = 10, Head = 38, Fill = "#FFFFE14A", Above = true, In = ShapeAnim.Draw, InDur = 0.6 },
        ShapeKind.Bubble => new ShapeLayer { Kind = k, W = 320, H = 150, Radius = 28, Fill = "#FFFFFFFF", Shadow = true },
        ShapeKind.Text => new ShapeLayer { Kind = k, Text = "Text", Style = new TextStyle { Size = 44, Weight = 700, Shadow = false, Align = "Center" }, Above = true },
        _ => new ShapeLayer { Kind = k },
    };

    /// <summary>Effect Controls ▸ Shapes and stickers (the selected title's layers; one layer's settings at a time).</summary>
    void BuildShapeSection(Clip c)
    {
        var sec = Section("Shapes and stickers", c.Shapes is { Count: > 0 });
        if (shapeClip != c.Id) { shapeClip = c.Id; shapeSel = 0; }
        void Changed() { ed.Touch(); ed.RaiseChanged(); }
        void Add(ShapeLayer s)
        {
            (c.Shapes ??= new()).Add(s);
            shapeSel = c.Shapes.Count - 1; Changed(); BuildInspector();
        }
        Button Btn(string text, string tip, Action a)
        {
            var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 0, 4, 4) };
            b.Click += (_, _) => a();
            return b;
        }
        var add = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var (k, label) in new[] { (ShapeKind.Rectangle, "▭ Box"), (ShapeKind.Ellipse, "◯ Ellipse"), (ShapeKind.Arrow, "➜ Arrow"), (ShapeKind.Line, "╱ Line"),
                                           (ShapeKind.Star, "★ Star"), (ShapeKind.Polygon, "⬠ Polygon"), (ShapeKind.Bubble, "💬 Bubble"), (ShapeKind.Text, "T Text") })
        {
            var kk = k; add.Children.Add(Btn(label, $"Add a {k.ToString().ToLowerInvariant()} layer", () => Add(NewShape(kk))));
        }
        var stick = Btn("☺ Sticker ▾", "Add a sticker", () => { });
        stick.Click += (_, _) =>
        {
            var m = lastStickerMenu = new ContextMenu { PlacementTarget = stick, Placement = PlacementMode.Bottom };
            foreach (var (name, _, _) in Shapes.Stickers)
            {
                var n = name;
                var mi = new MenuItem { Header = name, Icon = new Image { Source = StickerIcon(name), Width = 20, Height = 20 } };
                mi.Click += (_, _) => Add(Shapes.Sticker(n));
                m.Items.Add(mi);
            }
            m.IsOpen = true;
        };
        add.Children.Add(stick);
        sec.Children.Add(add);
        sec.Children.Add(Btn("Save as Graphic Template…", "Keep this title (look, layers, animation, place, length) in the Titles view's Graphics", () => SaveGraphicTemplate(c)));
        if (c.Shapes is not { Count: > 0 } list) return;

        shapeSel = Math.Clamp(shapeSel, 0, list.Count - 1);
        var lb = new ListBox { ItemsSource = list.Select(s => s.Label + (s.Above ? "  · over the text" : "")).ToList(), SelectedIndex = shapeSel, MaxHeight = 150, Margin = new Thickness(0, 4, 0, 4) };
        lb.SelectionChanged += (_, _) => { if (lb.SelectedIndex >= 0 && lb.SelectedIndex != shapeSel) { shapeSel = lb.SelectedIndex; BuildInspector(); } };
        sec.Children.Add(lb);
        var ops = new WrapPanel();
        ops.Children.Add(Btn("▲", "Move the layer up (drawn later)", () => { if (shapeSel < list.Count - 1) { (list[shapeSel], list[shapeSel + 1]) = (list[shapeSel + 1], list[shapeSel]); shapeSel++; Changed(); BuildInspector(); } }));
        ops.Children.Add(Btn("▼", "Move the layer down", () => { if (shapeSel > 0) { (list[shapeSel], list[shapeSel - 1]) = (list[shapeSel - 1], list[shapeSel]); shapeSel--; Changed(); BuildInspector(); } }));
        ops.Children.Add(Btn("Duplicate", "Duplicate the layer (a little lower right)", () => { var d = list[shapeSel].Clone(); d.X += 20; d.Y += 20; list.Insert(shapeSel + 1, d); shapeSel++; Changed(); BuildInspector(); }));
        ops.Children.Add(Btn("Delete", "Delete the layer", () => { list.RemoveAt(shapeSel); if (list.Count == 0) c.Shapes = null; shapeSel = Math.Max(0, shapeSel - 1); Changed(); BuildInspector(); }));
        sec.Children.Add(ops);

        var s = list[shapeSel];
        void Check(string label, bool v, Action<bool> set) { var cb = new CheckBox { IsChecked = v, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center }; cb.Click += (_, _) => { set(cb.IsChecked == true); Changed(); BuildInspector(); }; Row(sec, label, cb); }
        void Colour(string label, string v, Action<string> set) => Row(sec, label, new ColorField(v, hex => { set(hex); Changed(); }));
        ComboBox Combo(IEnumerable<string> items, int sel, Action<int> set) { var cb = new ComboBox { ItemsSource = items.ToList(), SelectedIndex = sel }; cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex >= 0) { set(cb.SelectedIndex); Changed(); } }; return cb; }

        if (s.Kind == ShapeKind.Text)
        {
            var st = s.Style ??= new TextStyle { Size = 44 };
            var tb = new TextBox { Text = s.Text, AcceptsReturn = true, MinHeight = 40, TextWrapping = TextWrapping.Wrap };
            tb.TextChanged += (_, _) => { s.Text = tb.Text; ed.Touch(); };
            Row(sec, "Text", tb);
            Slider("Size", 8, 400, 1, st.Size, v => st.Size = v, sec);
            Colour("Colour", st.Fill, h => st.Fill = h);
            Row(sec, "Weight", Combo(Weights.Select(w => w.name), Math.Max(0, Array.FindIndex(Weights, w => w.w == st.Weight)), i => st.Weight = Weights[i].w));
            Row(sec, "Align", Combo(new[] { "Left", "Center", "Right" }, Math.Max(0, Array.IndexOf(new[] { "Left", "Center", "Right" }, st.Align)), i => st.Align = new[] { "Left", "Center", "Right" }[i]));
            Check("Shadow", st.Shadow, v => st.Shadow = v);
        }
        Slider("X", -1500, 1500, 1, s.X, v => s.X = v, sec);
        Slider("Y", -900, 900, 1, s.Y, v => s.Y = v, sec);
        if (s.Kind != ShapeKind.Text)
        {
            Slider(s.Kind is ShapeKind.Line or ShapeKind.Arrow ? "Length" : "Width", 4, 2000, 1, s.W, v => s.W = v, sec);
            if (s.Kind is not (ShapeKind.Line or ShapeKind.Arrow)) Slider("Height", 4, 1200, 1, s.H, v => s.H = v, sec);
        }
        Slider("Rotation", -360, 360, 1, s.Rotation, v => s.Rotation = v, sec);
        Slider("Opacity %", 0, 100, 1, s.Opacity, v => s.Opacity = v, sec);
        switch (s.Kind)
        {
            case ShapeKind.Rectangle or ShapeKind.Bubble: Slider("Corners", 0, 300, 1, s.Radius, v => s.Radius = v, sec); break;
            case ShapeKind.Polygon: Slider("Sides", 3, 24, 1, s.Sides, v => s.Sides = (int)v, sec); break;
            case ShapeKind.Star: Slider("Points", 3, 24, 1, s.Sides, v => s.Sides = (int)v, sec); Slider("Inner %", 5, 95, 1, s.Inner, v => s.Inner = v, sec); break;
            case ShapeKind.Line or ShapeKind.Arrow:
                Slider("Thickness", 1, 80, 0.5, s.Thickness, v => s.Thickness = v, sec);
                if (s.Kind == ShapeKind.Arrow) { Slider("Head", 4, 200, 1, s.Head, v => s.Head = v, sec); Check("Both ends", s.TwoHeads, v => s.TwoHeads = v); }
                break;
        }
        if (s.Kind != ShapeKind.Text)
        {
            if (s.Kind is not (ShapeKind.Line or ShapeKind.Arrow or ShapeKind.Sticker)) Check("Filled", s.Filled, v => s.Filled = v);
            Colour(s.Kind is ShapeKind.Line or ShapeKind.Arrow ? "Colour" : "Fill", s.Fill, h => s.Fill = h);
            if (s.Kind == ShapeKind.Sticker) Colour("Details", s.Fill2, h => s.Fill2 = h);
            else
            {
                Check("Gradient", s.Gradient, v => s.Gradient = v);
                if (s.Gradient) { Colour("Colour 2", s.Fill2, h => s.Fill2 = h); Slider("Angle", 0, 360, 1, s.GradientAngle, v => s.GradientAngle = v, sec); }
            }
            if (s.Kind is not (ShapeKind.Line or ShapeKind.Arrow))
            {
                Colour("Outline", s.Stroke, h => s.Stroke = h);
                Slider("Outline width", 0, 40, 0.5, s.StrokeWidth, v => s.StrokeWidth = v, sec);
            }
            Check("Shadow", s.Shadow, v => s.Shadow = v);
            if (s.Shadow) { Slider("Shadow dist.", 0, 100, 0.5, s.ShadowDistance, v => s.ShadowDistance = v, sec); Slider("Softness", 0, 100, 0.5, s.ShadowBlur, v => s.ShadowBlur = v, sec); }
        }
        Check("Over the text", s.Above, v => s.Above = v);
        Row(sec, "In", Combo(Shapes.AnimNames.Select(n => n.name), Math.Max(0, Array.FindIndex(Shapes.AnimNames, n => n.v == s.In)), i => s.In = Shapes.AnimNames[i].v));
        Slider("In (s)", 0.05, 5, 0.05, s.InDur, v => s.InDur = v, sec);
        Slider("Delay (s)", 0, 10, 0.05, s.Delay, v => s.Delay = v, sec);
        Row(sec, "Out", Combo(Shapes.AnimNames.Select(n => n.name), Math.Max(0, Array.FindIndex(Shapes.AnimNames, n => n.v == s.Out)), i => s.Out = Shapes.AnimNames[i].v));
        Slider("Out (s)", 0.05, 5, 0.05, s.OutDur, v => s.OutDur = v, sec);
        Row(sec, "Loop", Combo(Shapes.LoopNames.Select(n => n.name), Math.Max(0, Array.FindIndex(Shapes.LoopNames, n => n.v == s.Loop)), i => s.Loop = Shapes.LoopNames[i].v));
        sec.Children.Add(new TextBlock
        {
            Text = "Drag the layer on the program monitor to move it, a corner to resize it (Shift: one axis / keep its shape). Layers sit around the title's position and move, scale and turn with it (Motion); X / Y are pixels of a 720-line frame from that point. Delay staggers layers; Draw on traces lines, arrows and outlines and wipes filled shapes.",
            TextWrapping = TextWrapping.Wrap, Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 4, 0, 0)
        });
        shapeOverlay?.InvalidateVisual();
    }

    /// <summary>A sticker drawn small (menus).</summary>
    static BitmapSource StickerIcon(string name)
    {
        var key = "sticker|" + name;
        if (graphicThumbs.TryGetValue(key, out var b)) return b;
        var s = Shapes.Sticker(name, 36);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(20, 20));
            foreach (var (g, role) in Shapes.Art(name, 36, 36))
                dc.DrawGeometry(new SolidColorBrush(TitleVisual.ParseColor(role == 0 ? s.Fill : s.Fill2, Colors.White)), null, g);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(40, 40, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv); rtb.Freeze();
        return graphicThumbs[key] = rtb;
    }

    /// <summary>A template drawn complete (every layer in), cropped to what it covers.</summary>
    static BitmapSource GraphicThumb(GraphicTemplate t)
    {
        var key = Editor.W + "|" + JsonSerializer.Serialize(t.Clip);
        if (graphicThumbs.TryGetValue(key, out var b)) return b;
        var c = GraphicLibrary.Instance(t);
        int w = Editor.W / 2, h = Editor.H / 2;
        var bmp = TitleVisual.Render(c, false, outW: w, outH: h, local: GraphicLibrary.ShownAt(c));
        var px = new byte[w * h * 4]; bmp.CopyPixels(px, w * 4, 0);
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (px[(y * w + x) * 4 + 3] > 8) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        BitmapSource res = bmp;
        if (x1 >= x0)
        {
            int m = 6; x0 = Math.Max(0, x0 - m); y0 = Math.Max(0, y0 - m); x1 = Math.Min(w - 1, x1 + m); y1 = Math.Min(h - 1, y1 + m);
            res = new CroppedBitmap(bmp, new Int32Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
        }
        res.Freeze();
        return graphicThumbs[key] = res;
    }

    /// <summary>The Graphics part of the Titles view's gallery (before the styles).</summary>
    void AddGraphicsToGallery()
    {
        StyleGallery.Items.Add(new ListBoxItem { IsEnabled = false, Content = new TextBlock { Text = "Graphics — click to add one at the playhead", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) } });
        foreach (var t in GraphicLibrary.All)
        {
            var sp = new StackPanel { Tag = t, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18)), CornerRadius = new CornerRadius(4), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, Width = 216, Height = 64,
                Child = new Image { Source = GraphicThumb(t), Stretch = Stretch.Uniform, Margin = new Thickness(4) }
            });
            sp.Children.Add(new TextBlock { Text = t.Name + (t.BuiltIn ? $"  · {t.Category}" : "  (mine)"), FontSize = 11.5, Margin = new Thickness(2, 3, 0, 0) });
            if (!t.BuiltIn)
            {
                var del = new MenuItem { Header = "Delete Template" };
                del.Click += (_, _) => { GraphicLibrary.Delete(t.Name); BuildStyleGallery(); };
                sp.ContextMenu = new ContextMenu { Items = { del } };
            }
            StyleGallery.Items.Add(sp);
        }
        StyleGallery.Items.Add(new ListBoxItem { IsEnabled = false, Content = new TextBlock { Text = "Styles — click to apply to the selected titles (or add a new title)", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 4) } });
    }

    void AddGraphic(GraphicTemplate t)
    {
        if (ed.AddGraphic(t) is { } c) { ShowDock("Inspector"); SetTask($"Graphic “{t.Name}” at the playhead on {Tracks.Label(c.Track)} — Effect Controls ▸ Shapes and stickers edits it", 4000); }
        else SetTask("No unlocked video track for the graphic", 3000);
    }

    /// <summary>Clip ▸ Save as Graphic Template… / the Shapes section's button: the title by a name, in the Graphics gallery from then on.</summary>
    void SaveGraphicTemplate(Clip c)
    {
        var name = AskName("Save as Graphic Template", "Name of the template (the title's look, layers, animation, place and length):", c.Text.Split('\n')[0].Trim() is { Length: > 0 } t ? t : "My graphic", "Save");
        if (string.IsNullOrWhiteSpace(name)) return;
        GraphicLibrary.Save(name.Trim(), c);
        BuildStyleGallery();
        SetTask($"Template “{name.Trim()}” saved — the Titles view's Graphics list has it", 3500);
    }
}
