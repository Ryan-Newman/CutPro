using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace CutPro;

public enum ShapeKind { Rectangle, Ellipse, Polygon, Star, Line, Arrow, Bubble, Text, Sticker }
public enum ShapeAnim { None, Fade, Pop, Grow, SlideLeft, SlideRight, SlideUp, SlideDown, Draw, Spin }
public enum ShapeLoop { None, Pulse, Wiggle, Bounce, Float, Progress }

/// <summary>A shape layer of a title (Premiere's Essential Graphics shapes, CapCut's stickers): a rectangle, ellipse, polygon, star, line, arrow,
/// speech bubble, a text of its own or a sticker, placed around the title's anchor point (logical px, before the title's own scale / rotation, so
/// the whole graphic moves, scales and turns with the clip) and drawn under or over the title's text by <see cref="TitleVisual"/> — the one
/// renderer of the preview and the export. Each layer comes in / goes out (fade, pop, grow, slide, draw on, spin) after its own delay and can
/// loop (pulse, wiggle, bounce, float, progress over the clip).</summary>
public sealed class ShapeLayer
{
    public ShapeKind Kind { get; set; }
    /// <summary>Kind Sticker: the art's name (<see cref="Shapes.Stickers"/>).</summary>
    public string Sticker { get; set; } = "";
    /// <summary>Kind Text: its words and look (alignment: X is its left edge / centre / right edge, Y its middle).</summary>
    public string Text { get; set; } = "";
    public TextStyle? Style { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 240;
    public double H { get; set; } = 120;
    public double Rotation { get; set; }
    /// <summary>Rectangle / bubble corner radius.</summary>
    public double Radius { get; set; }
    /// <summary>Polygon sides, star points.</summary>
    public int Sides { get; set; } = 5;
    /// <summary>Star: inner radius, % of the outer.</summary>
    public double Inner { get; set; } = 45;
    /// <summary>Line / arrow thickness.</summary>
    public double Thickness { get; set; } = 10;
    /// <summary>Arrow head length; heads at both ends.</summary>
    public double Head { get; set; } = 34;
    public bool TwoHeads { get; set; }
    public bool Filled { get; set; } = true;
    /// <summary>Fill (lines / arrows: their colour; stickers: the main colour, <see cref="Fill2"/> the details).</summary>
    public string Fill { get; set; } = "#FFFFFFFF";
    public bool Gradient { get; set; }
    public string Fill2 { get; set; } = "#FF4A7CFF";
    public double GradientAngle { get; set; } = 90;
    /// <summary>Outline (0 = none).</summary>
    public string Stroke { get; set; } = "#FF000000";
    public double StrokeWidth { get; set; }
    public double Opacity { get; set; } = 100;
    public bool Shadow { get; set; }
    public string ShadowColor { get; set; } = "#FF000000";
    public double ShadowOpacity { get; set; } = 55;
    public double ShadowDistance { get; set; } = 6;
    public double ShadowBlur { get; set; } = 10;
    public double ShadowAngle { get; set; } = 135;
    /// <summary>Drawn over the title's text (false: under it, like a background).</summary>
    public bool Above { get; set; }
    public ShapeAnim In { get; set; }
    public ShapeAnim Out { get; set; }
    public double InDur { get; set; } = 0.5;
    public double OutDur { get; set; } = 0.4;
    /// <summary>Seconds after the clip's start the in animation begins (layers one after another).</summary>
    public double Delay { get; set; }
    public ShapeLoop Loop { get; set; }

    public ShapeLayer Clone() => JsonSerializer.Deserialize<ShapeLayer>(JsonSerializer.Serialize(this))!;
    public string Label => Kind switch
    {
        ShapeKind.Sticker => "Sticker: " + Sticker,
        ShapeKind.Text => "Text: " + (Text.Length > 18 ? Text[..18] + "…" : Text).Replace('\n', ' '),
        _ => Kind.ToString(),
    };
}

/// <summary>The look of a shape layer at a moment: opacity, offset, scale, turn (degrees) and how much of it is drawn (0 … 1: draw on / wipe / progress).</summary>
public readonly record struct ShapeState(double Opacity, double Dx, double Dy, double Scale, double Rotation, double Reveal);

public static class Shapes
{
    public static readonly (ShapeAnim v, string name)[] AnimNames =
    {
        (ShapeAnim.None, "None"), (ShapeAnim.Fade, "Fade"), (ShapeAnim.Pop, "Pop"), (ShapeAnim.Grow, "Grow"), (ShapeAnim.SlideLeft, "Slide from the left"),
        (ShapeAnim.SlideRight, "Slide from the right"), (ShapeAnim.SlideUp, "Slide up"), (ShapeAnim.SlideDown, "Slide down"), (ShapeAnim.Draw, "Draw on / wipe"), (ShapeAnim.Spin, "Spin"),
    };
    public static readonly (ShapeLoop v, string name)[] LoopNames =
    {
        (ShapeLoop.None, "None"), (ShapeLoop.Pulse, "Pulse"), (ShapeLoop.Wiggle, "Wiggle"), (ShapeLoop.Bounce, "Bounce"), (ShapeLoop.Float, "Float"), (ShapeLoop.Progress, "Progress over the clip"),
    };

    /// <summary>The title has shape layers that change over time (the export renders it frame by frame then).</summary>
    public static bool Animated(Clip c) => c.Shapes?.Any(s => s.In != ShapeAnim.None || s.Out != ShapeAnim.None || s.Loop != ShapeLoop.None) == true;

    /// <summary>A layer's look at <paramref name="local"/> seconds into a clip <paramref name="dur"/> long (null = fully shown, in place — still renders).</summary>
    public static ShapeState State(ShapeLayer s, double? local, double dur)
    {
        if (local is not double t) return new(1, 0, 0, 1, 0, 1);
        double op = 1, dx = 0, dy = 0, sc = 1, rot = 0, rev = 1;
        double dist = Math.Max(160, Math.Max(s.W, s.H));
        void Apply(ShapeAnim a, double p)
        {
            double e = 1 - Math.Pow(1 - p, 3);                                   // (ease out)
            switch (a)
            {
                case ShapeAnim.Fade: op *= p * p * (3 - 2 * p); break;
                case ShapeAnim.Pop:
                {
                    const double c1 = 1.70158, c3 = c1 + 1;                     // (overshoots a little, then settles)
                    double b = p <= 0 ? 0 : 1 + c3 * Math.Pow(p - 1, 3) + c1 * Math.Pow(p - 1, 2);
                    sc *= b; op *= Math.Min(1, p * 3); break;
                }
                case ShapeAnim.Grow: sc *= e; if (p <= 0) op = 0; break;
                case ShapeAnim.SlideLeft: dx -= (1 - e) * dist; op *= Math.Min(1, p * 3); break;
                case ShapeAnim.SlideRight: dx += (1 - e) * dist; op *= Math.Min(1, p * 3); break;
                case ShapeAnim.SlideUp: dy += (1 - e) * dist; op *= Math.Min(1, p * 3); break;
                case ShapeAnim.SlideDown: dy -= (1 - e) * dist; op *= Math.Min(1, p * 3); break;
                case ShapeAnim.Draw: rev = Math.Min(rev, p * p * (3 - 2 * p)); break;
                case ShapeAnim.Spin: rot -= (1 - e) * 180; sc *= e; if (p <= 0) op = 0; break;
            }
        }
        if (s.In != ShapeAnim.None) Apply(s.In, s.InDur <= 1e-9 ? (t >= s.Delay - 1e-9 ? 1 : 0) : Math.Clamp((t - s.Delay) / s.InDur, 0, 1));
        if (s.Out != ShapeAnim.None)
        {
            double od = Math.Min(s.OutDur, dur);
            Apply(s.Out, od <= 1e-9 ? (t < dur - 1e-9 ? 1 : 0) : Math.Clamp((dur - t) / od, 0, 1));
        }
        switch (s.Loop)
        {
            case ShapeLoop.Pulse: sc *= 1 + 0.06 * Math.Sin(2 * Math.PI * 1.2 * t); break;
            case ShapeLoop.Wiggle: rot += 7 * Math.Sin(2 * Math.PI * 1.6 * t); break;
            case ShapeLoop.Bounce: dy -= 16 * Math.Abs(Math.Sin(Math.PI * 1.6 * t)); break;
            case ShapeLoop.Float: dy += 9 * Math.Sin(2 * Math.PI * 0.45 * t); break;
            case ShapeLoop.Progress: rev = Math.Min(rev, dur <= 1e-9 ? 1 : Math.Clamp(t / dur, 0, 1)); break;
        }
        return new(op, dx, dy, sc, rot, rev);
    }

    /// <summary>A layer drawn as an outline (its stroke draws on) rather than wiped: lines, arrows and unfilled shapes with an outline.</summary>
    public static bool DrawsOn(ShapeLayer s) => s.Kind is ShapeKind.Line or ShapeKind.Arrow || !s.Filled && s.StrokeWidth > 0 && s.Kind is not (ShapeKind.Text or ShapeKind.Sticker);

    /// <summary>The wipe a partly revealed layer is clipped to (its own coordinates: from its left edge; generous room for outline and shadow).</summary>
    public static Rect WipeRect(ShapeLayer s, double reveal)
    {
        double pad = 80 + s.StrokeWidth + s.ShadowDistance + s.ShadowBlur * 2;
        return new Rect(-s.W / 2 - pad, -s.H / 2 - pad, (s.W + 2 * pad) * Math.Clamp(reveal, 0, 1), s.H + 2 * pad);
    }

    /// <summary>The line of a line / arrow (left to right through the centre; arrow heads leave room at the ends).</summary>
    public static Geometry Spine(ShapeLayer s)
    {
        double half = s.W / 2, hd = s.Kind == ShapeKind.Arrow ? Math.Min(s.Head, s.W / 2) * 0.7 : 0;
        return new LineGeometry(new Point(-half + (s.TwoHeads ? hd : 0), 0), new Point(half - hd, 0));
    }
    /// <summary>Arrow heads (filled triangles at the line's end, and its start with two heads).</summary>
    public static Geometry? Heads(ShapeLayer s)
    {
        if (s.Kind != ShapeKind.Arrow) return null;
        double half = s.W / 2, hd = Math.Min(s.Head, s.W / 2), hw = Math.Max(hd * 0.62, s.Thickness * 1.2);
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(Poly(new Point(half, 0), new Point(half - hd, -hw), new Point(half - hd, hw)));
        if (s.TwoHeads) g.Children.Add(Poly(new Point(-half, 0), new Point(-half + hd, -hw), new Point(-half + hd, hw)));
        return g;
    }

    /// <summary>A filled shape's area in its own coordinates (centred on 0, 0). Lines / arrows: their stroke widened; text layers: none here (TitleVisual lays the text out).</summary>
    public static Geometry Area(ShapeLayer s)
    {
        double w = Math.Max(1, s.W), h = Math.Max(1, s.H);
        switch (s.Kind)
        {
            case ShapeKind.Rectangle:
            {
                double r = Math.Clamp(s.Radius, 0, Math.Min(w, h) / 2);
                return new RectangleGeometry(new Rect(-w / 2, -h / 2, w, h), r, r);
            }
            case ShapeKind.Ellipse: return new EllipseGeometry(new Point(0, 0), w / 2, h / 2);
            case ShapeKind.Polygon:
            {
                int n = Math.Clamp(s.Sides, 3, 24);
                return Poly(Enumerable.Range(0, n).Select(i => { double a = -Math.PI / 2 + i * 2 * Math.PI / n; return new Point(Math.Cos(a) * w / 2, Math.Sin(a) * h / 2); }).ToArray());
            }
            case ShapeKind.Star:
            {
                int n = Math.Clamp(s.Sides, 3, 24); double k = Math.Clamp(s.Inner, 5, 95) / 100;
                return Poly(Enumerable.Range(0, 2 * n).Select(i => { double a = -Math.PI / 2 + i * Math.PI / n, f = i % 2 == 0 ? 1 : k; return new Point(Math.Cos(a) * w / 2 * f, Math.Sin(a) * h / 2 * f); }).ToArray());
            }
            case ShapeKind.Bubble:
            {
                double bh = h * 0.78, r = Math.Clamp(s.Radius, 0, Math.Min(w, bh) / 2);
                var body = new RectangleGeometry(new Rect(-w / 2, -h / 2, w, bh), r, r);
                var tail = Poly(new Point(-w * 0.28, -h / 2 + bh - 1), new Point(-w * 0.06, -h / 2 + bh - 1), new Point(-w * 0.34, h / 2));
                return new CombinedGeometry(GeometryCombineMode.Union, body, tail);
            }
            case ShapeKind.Line:
            case ShapeKind.Arrow:
            {
                var g = new GeometryGroup { FillRule = FillRule.Nonzero };
                g.Children.Add(Spine(s).GetWidenedPathGeometry(new Pen(Brushes.Black, Math.Max(0.5, s.Thickness)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }));
                if (Heads(s) is { } hd) g.Children.Add(hd);
                return g;
            }
            case ShapeKind.Sticker:
            {
                var g = new GeometryGroup { FillRule = FillRule.Nonzero };
                foreach (var (part, _) in Art(s.Sticker, w, h)) g.Children.Add(part);
                return g;
            }
            default: return Geometry.Empty;
        }
    }

    static PathGeometry Poly(params Point[] pts)
    {
        var f = new PathFigure { StartPoint = pts[0], IsClosed = true, IsFilled = true };
        f.Segments.Add(new PolyLineSegment(pts.Skip(1), true));
        return new PathGeometry(new[] { f });
    }

    /// <summary>The length of a geometry's outline (dash patterns for drawing it on).</summary>
    public static double Length(Geometry g)
    {
        double len = 0;
        foreach (var f in g.GetFlattenedPathGeometry(0.25, ToleranceType.Absolute).Figures)
        {
            var p = f.StartPoint; var first = p;
            foreach (var seg in f.Segments)
                if (seg is PolyLineSegment pl) foreach (var q in pl.Points) { len += (q - p).Length; p = q; }
                else if (seg is LineSegment ls) { len += (ls.Point - p).Length; p = ls.Point; }
            if (f.IsClosed) len += (first - p).Length;
        }
        return Math.Max(1e-3, len);
    }

    // ---------------- stickers: vector art in a 100 × 100 box, each part painted by a role ----------------
    /// <summary>The stickers (name, default main colour, default detail colour).</summary>
    public static readonly (string name, string fill, string fill2)[] Stickers =
    {
        ("Heart", "#FFFF3B5C", "#FFFFFFFF"), ("Like", "#FF2F7BF6", "#FFFFFFFF"), ("Bell", "#FFFFC93C", "#FF8A5A00"), ("Check", "#FF22B35E", "#FFFFFFFF"),
        ("Fire", "#FFFF6A1A", "#FFFFD23C"), ("Pin", "#FFE53935", "#FFFFFFFF"), ("Play", "#FFE53935", "#FFFFFFFF"), ("Sparkle", "#FFFFE14A", "#FFFFFFFF"),
        ("Lightning", "#FFFFD23C", "#FFFF8A00"), ("Music", "#FF8E5CFF", "#FFFFFFFF"), ("Smile", "#FFFFD23C", "#FF3A2A10"), ("Alert", "#FFFF9F1A", "#FFFFFFFF"),
        ("Share", "#FF16C2B0", "#FFFFFFFF"), ("Star", "#FFFFC93C", "#FFFFFFFF"),
    };
    /// <summary>A new sticker layer with its art's colours.</summary>
    public static ShapeLayer Sticker(string name, double size = 120)
    {
        var d = Stickers.FirstOrDefault(x => x.name == name);
        return new ShapeLayer { Kind = ShapeKind.Sticker, Sticker = d.name ?? name, W = size, H = size, Fill = d.fill ?? "#FFFFFFFF", Fill2 = d.fill2 ?? "#FF000000", Above = true };
    }

    /// <summary>A sticker's parts scaled to w × h around 0, 0: geometry and paint role (0 = the main colour, 1 = the detail colour).</summary>
    public static List<(Geometry g, int role)> Art(string name, double w, double h)
    {
        var parts = new List<(Geometry, int)>();
        var into = new MatrixTransform(w / 100, 0, 0, h / 100, -w / 2, -h / 2);
        void Add(Geometry g, int role) { var c = g.Clone(); c.Transform = c.Transform is { } t && !t.Value.IsIdentity ? new TransformGroup { Children = { t, into } } : into; parts.Add((c, role)); }
        Geometry P(string data) => Geometry.Parse(data);
        Geometry Stroke(string data, double width) => P(data).GetWidenedPathGeometry(new Pen(Brushes.Black, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
        var circle = new EllipseGeometry(new Point(50, 50), 48, 48);
        switch (name)
        {
            case "Heart": Add(P("M50,90 C24,70 4,52 4,31 C4,15 16,5 30,5 C40,5 47,11 50,19 C53,11 60,5 70,5 C84,5 96,15 96,31 C96,52 76,70 50,90 Z"), 0); break;
            case "Like":
                Add(circle, 0);
                Add(P("M28,46 L38,46 L38,74 L28,74 Z M42,46 C49,40 53,32 54,24 C55,17 64,17 64,26 C64,32 62,38 60,42 L72,42 C77,42 80,46 79,51 L75,69 C74,73 71,75 67,75 L42,75 Z"), 1);
                break;
            case "Bell":
                Add(P("M50,12 C35,12 27,24 27,39 L27,57 L19,68 L81,68 L73,57 L73,39 C73,24 65,12 50,12 Z"), 0);
                Add(new EllipseGeometry(new Point(50, 9), 5, 5), 0);
                Add(P("M41,72 A9,9 0 0 0 59,72 Z"), 1);
                break;
            case "Check": Add(circle, 0); Add(Stroke("M27,52 L43,68 L74,34", 10), 1); break;
            case "Fire":
                Add(P("M50,3 C58,22 79,34 79,59 C79,80 65,96 50,96 C35,96 21,81 21,62 C21,48 29,40 36,31 C38,44 44,49 48,49 C46,34 44,18 50,3 Z"), 0);
                Add(P("M50,50 C57,59 65,66 65,77 C65,87 58,94 50,94 C42,94 35,87 35,79 C35,71 41,66 46,60 C46,66 48,70 50,70 C50,64 48,57 50,50 Z"), 1);
                break;
            case "Pin":
                Add(P("M50,3 C32,3 17,18 17,36 C17,61 50,97 50,97 C50,97 83,61 83,36 C83,18 68,3 50,3 Z"), 0);
                Add(new EllipseGeometry(new Point(50, 36), 13, 13), 1);
                break;
            case "Play":
                Add(new RectangleGeometry(new Rect(3, 18, 94, 64), 18, 18), 0);
                Add(P("M41,34 L67,50 L41,66 Z"), 1);
                break;
            case "Sparkle":
                Add(P("M50,2 C54,36 64,46 98,50 C64,54 54,64 50,98 C46,64 36,54 2,50 C36,46 46,36 50,2 Z"), 0);
                Add(P("M82,6 C83,14 86,17 94,18 C86,19 83,22 82,30 C81,22 78,19 70,18 C78,17 81,14 82,6 Z"), 1);
                break;
            case "Lightning": Add(P("M60,2 L20,57 L46,57 L37,98 L80,40 L54,40 Z"), 0); break;
            case "Music":
                Add(P("M34,18 L84,6 L84,70 C84,79 77,85 68,85 C60,85 55,80 55,74 C55,67 62,62 70,62 C73,62 75,63 76,64 L76,26 L42,34 L42,80 C42,89 35,95 26,95 C18,95 13,90 13,84 C13,77 20,72 28,72 C31,72 33,73 34,74 Z"), 0);
                break;
            case "Smile":
                Add(circle, 0);
                Add(new EllipseGeometry(new Point(35, 40), 6, 8), 1); Add(new EllipseGeometry(new Point(65, 40), 6, 8), 1);
                Add(Stroke("M27,58 C35,77 65,77 73,58", 7), 1);
                break;
            case "Alert":
                Add(circle, 0);
                Add(P("M44,18 L56,18 L54,62 L46,62 Z"), 1); Add(new EllipseGeometry(new Point(50, 76), 7, 7), 1);
                break;
            case "Share": Add(P("M58,12 L94,46 L58,80 L58,60 C36,60 20,67 6,86 C11,58 27,40 58,38 Z"), 0); break;
            case "Star":
                Add(Poly(Enumerable.Range(0, 10).Select(i => { double a = -Math.PI / 2 + i * Math.PI / 5, f = i % 2 == 0 ? 48 : 21; return new Point(50 + Math.Cos(a) * f, 53 + Math.Sin(a) * f); }).ToArray()), 0);
                break;
            default: Add(circle, 0); break;
        }
        return parts;
    }
}

/// <summary>A motion graphics template (Premiere's .mogrt, simply): a title with its shape layers, look, text animation, place and length,
/// saved by name. Built-in ones are made here; the user's own live in %APPDATA%\CutPro\graphics.json.</summary>
public sealed class GraphicTemplate
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Mine";
    public bool BuiltIn { get; set; }
    public Clip Clip { get; set; } = new();
}

public static class GraphicLibrary
{
    /// <summary>Tests point the user's file elsewhere (null = %APPDATA%\CutPro\graphics.json).</summary>
    public static string? StoreOverride;
    static string File => StoreOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CutPro", "graphics.json");
    static List<GraphicTemplate>? user;

    public static List<GraphicTemplate> User
    {
        get
        {
            if (user != null) return user;
            try { user = System.IO.File.Exists(File) ? JsonSerializer.Deserialize<List<GraphicTemplate>>(System.IO.File.ReadAllText(File)) ?? new() : new(); }
            catch { user = new(); }
            return user;
        }
    }
    public static IEnumerable<GraphicTemplate> All => BuiltIn.Concat(User);
    /// <summary>Reads the user's file again (tests).</summary>
    public static void Reload() => user = null;

    /// <summary>Saves a title (its look, layers, animation, place, length) as a template; the same name replaces it.</summary>
    public static GraphicTemplate Save(string name, Clip c)
    {
        var t = new GraphicTemplate { Name = name, Category = "Mine", Clip = Strip(c) };
        User.RemoveAll(x => x.Name == name);
        User.Add(t);
        Write();
        return t;
    }
    public static void Delete(string name) { User.RemoveAll(x => x.Name == name); Write(); }
    static void Write()
    {
        if (AppSettings.ReadOnly && StoreOverride == null) return;                // (automated runs never touch the user's file)
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(User, new JsonSerializerOptions { WriteIndented = true }));
    }
    /// <summary>What a template keeps of a clip: the look and timing, not its place on a timeline, masks or links.</summary>
    static Clip Strip(Clip c)
    {
        var x = JsonSerializer.Deserialize<Clip>(JsonSerializer.Serialize(c))!;
        x.Id = ""; x.Start = 0; x.Track = 0; x.LinkId = null; x.GroupId = null; x.Masks.Clear(); x.Markers = null; x.TransIn = x.TransOut = null; x.Follow = null; x.Cam = null;
        return x;
    }
    /// <summary>A fresh clip made from a template (new id).</summary>
    public static Clip Instance(GraphicTemplate t)
    {
        var x = JsonSerializer.Deserialize<Clip>(JsonSerializer.Serialize(t.Clip))!;
        x.Id = Guid.NewGuid().ToString("N"); x.Kind = ClipKind.Text;
        return x;
    }
    /// <summary>The moment a template looks complete (every layer in, before anything goes out) — thumbnails.</summary>
    public static double ShownAt(Clip c)
    {
        double t = c.Shapes?.Select(s => s.In == ShapeAnim.None ? 0 : s.Delay + s.InDur).DefaultIfEmpty(0).Max() ?? 0;
        if (c.TextFx is { In: not TextIn.None } fx) t = Math.Max(t, fx.InDur);
        if (c.Shapes?.Any(s => s.Loop == ShapeLoop.Progress) == true) t = Math.Max(t, c.Dur * 0.6);
        return Math.Min(t + 0.05, Math.Max(0, c.Dur - 0.6));
    }

    static TextStyle Txt(double size, string fill = "#FFFFFFFF", int weight = 700, string align = "Left", string font = "Segoe UI", bool shadow = false) =>
        new() { Font = font, Size = size, Weight = weight, Fill = fill, Align = align, Shadow = shadow, ShadowOpacity = 55, ShadowDistance = 3, ShadowBlur = 6 };
    static GraphicTemplate T(string name, string cat, double dur, double x, double y, string text, TextStyle st, TextAnim? fx, params ShapeLayer[] shapes) =>
        new() { Name = name, Category = cat, BuiltIn = true, Clip = new Clip { Kind = ClipKind.Text, Text = text, Style = st, TextFx = fx, Dur = dur, X = x, Y = y, Role = TextRole.Title, Shapes = shapes.ToList() } };

    /// <summary>The built-in templates: lower thirds, call-outs, social, titles, stickers.</summary>
    public static readonly List<GraphicTemplate> BuiltIn = new()
    {
        T("Lower third — bar", "Lower thirds", 6, -36, 30, "Your Name", Txt(50), new TextAnim { In = TextIn.RiseWords, InDur = 0.6, Out = TextIn.FadeWords, OutDur = 0.4 },
          new ShapeLayer { Kind = ShapeKind.Rectangle, X = 250, Y = 22, W = 560, H = 132, Fill = "#D0101418", Radius = 6, In = ShapeAnim.Draw, InDur = 0.45, Out = ShapeAnim.Fade, OutDur = 0.4 },
          new ShapeLayer { Kind = ShapeKind.Rectangle, X = -18, Y = 22, W = 9, H = 132, Fill = "#FF3DA5FF", In = ShapeAnim.Grow, InDur = 0.35, Out = ShapeAnim.Fade },
          new ShapeLayer { Kind = ShapeKind.Text, Text = "Title or role", Style = Txt(28, "#FFB9C4D0", 400), X = 0, Y = 54, Above = true, In = ShapeAnim.Fade, Delay = 0.35, InDur = 0.4, Out = ShapeAnim.Fade }),
        T("Lower third — pill", "Lower thirds", 6, -30, 32, "Your Name", Txt(40, "#FF14161A"), new TextAnim { In = TextIn.FadeWords, InDur = 0.5, Out = TextIn.FadeWords },
          new ShapeLayer { Kind = ShapeKind.Rectangle, X = 150, Y = 0, W = 420, H = 76, Radius = 38, Fill = "#FFFFFFFF", Shadow = true, In = ShapeAnim.SlideLeft, InDur = 0.5, Out = ShapeAnim.SlideLeft, OutDur = 0.4 },
          new ShapeLayer { Kind = ShapeKind.Ellipse, X = -14, Y = 0, W = 18, H = 18, Fill = "#FFFF3B5C", Above = true, In = ShapeAnim.Pop, Delay = 0.35, InDur = 0.35, Loop = ShapeLoop.Pulse, Out = ShapeAnim.Fade }),
        T("Subscribe button", "Social", 5, 30, 32, "SUBSCRIBE", Txt(42, "#FFFFFFFF", 900, "Center"), null,
          new ShapeLayer { Kind = ShapeKind.Rectangle, X = 0, Y = 0, W = 330, H = 86, Radius = 14, Fill = "#FFE5322D", Shadow = true, In = ShapeAnim.Pop, InDur = 0.45, Out = ShapeAnim.Fade },
          new ShapeLayer { Kind = ShapeKind.Sticker, Sticker = "Bell", X = 205, Y = -4, W = 70, H = 70, Fill = "#FFFFFFFF", Fill2 = "#FFE5322D", Above = true, In = ShapeAnim.Pop, Delay = 0.3, Loop = ShapeLoop.Wiggle, Out = ShapeAnim.Fade }),
        T("Like · comment · share", "Social", 5, 0, 30, "", Txt(40, align: "Center"), null,
          new ShapeLayer { Kind = ShapeKind.Sticker, Sticker = "Like", X = -120, W = 96, H = 96, Fill = "#FF2F7BF6", Fill2 = "#FFFFFFFF", In = ShapeAnim.Pop, InDur = 0.4, Loop = ShapeLoop.Pulse, Out = ShapeAnim.Fade },
          new ShapeLayer { Kind = ShapeKind.Bubble, X = 0, Y = 2, W = 96, H = 92, Radius = 22, Fill = "#FF22B35E", In = ShapeAnim.Pop, Delay = 0.15, InDur = 0.4, Loop = ShapeLoop.Pulse, Out = ShapeAnim.Fade },
          new ShapeLayer { Kind = ShapeKind.Sticker, Sticker = "Share", X = 120, W = 92, H = 92, Fill = "#FF16C2B0", In = ShapeAnim.Pop, Delay = 0.3, InDur = 0.4, Loop = ShapeLoop.Pulse, Out = ShapeAnim.Fade }),
        T("Call-out arrow", "Call-outs", 4, -20, -18, "Look here!", Txt(56, "#FFFFE14A", 800, "Center", shadow: true) .With(s => { s.Stroke = true; s.StrokeWidth = 3; }), new TextAnim { In = TextIn.PopWords, InDur = 0.5 },
          new ShapeLayer { Kind = ShapeKind.Arrow, X = 150, Y = 110, W = 240, Thickness = 10, Head = 40, Rotation = 38, Fill = "#FFFFE14A", Above = true, In = ShapeAnim.Draw, Delay = 0.25, InDur = 0.6, Out = ShapeAnim.Fade }),
        T("Circle highlight", "Call-outs", 4, 0, 0, "", Txt(40, align: "Center"), null,
          new ShapeLayer { Kind = ShapeKind.Ellipse, W = 300, H = 210, Filled = false, Stroke = "#FFFFE14A", StrokeWidth = 9, In = ShapeAnim.Draw, InDur = 0.7, Out = ShapeAnim.Fade }),
        T("Speech bubble", "Call-outs", 4, 22, -24, "So good!", Txt(46, "#FF14161A", 800, "Center"), null,
          new ShapeLayer { Kind = ShapeKind.Bubble, X = 0, Y = 14, W = 330, H = 150, Radius = 30, Fill = "#FFFFFFFF", Shadow = true, In = ShapeAnim.Pop, InDur = 0.45, Out = ShapeAnim.Fade }),
        T("Progress bar", "Titles", 10, 0, 0, "", Txt(30), null,
          new ShapeLayer { Kind = ShapeKind.Rectangle, X = 0, Y = 352, W = 1280, H = 14, Fill = "#55FFFFFF" },
          new ShapeLayer { Kind = ShapeKind.Rectangle, X = 0, Y = 352, W = 1280, H = 14, Fill = "#FFFF3B5C", Loop = ShapeLoop.Progress }),
        T("Chapter title", "Titles", 5, -40, 0, "Chapter title", Txt(76, "#FFFFFFFF", 800, shadow: true), new TextAnim { In = TextIn.RiseWords, InDur = 0.7, Out = TextIn.FadeWords },
          new ShapeLayer { Kind = ShapeKind.Text, Text = "01", Style = Txt(120, "#FF3DA5FF", 900), X = 0, Y = -112, In = ShapeAnim.SlideUp, InDur = 0.5, Out = ShapeAnim.Fade },
          new ShapeLayer { Kind = ShapeKind.Line, X = 210, Y = 62, W = 420, Thickness = 6, Fill = "#FF3DA5FF", In = ShapeAnim.Draw, Delay = 0.3, InDur = 0.6, Out = ShapeAnim.Fade }),
        T("Quote", "Titles", 7, 0, -4, "Words worth\nremembering.", Txt(58, "#FFFFFFFF", 600, "Center", "Georgia", true).With(s => s.Italic = true), new TextAnim { In = TextIn.FadeLines, InDur = 1.0, Out = TextIn.FadeLines },
          new ShapeLayer { Kind = ShapeKind.Text, Text = "“", Style = Txt(220, "#FF3DA5FF", 700, "Center", "Georgia"), X = 0, Y = -150, In = ShapeAnim.Fade, InDur = 0.6, Out = ShapeAnim.Fade },
          new ShapeLayer { Kind = ShapeKind.Text, Text = "— Author", Style = Txt(30, "#FFB9C4D0", 400, "Center"), X = 0, Y = 120, In = ShapeAnim.Fade, Delay = 0.8, InDur = 0.5, Out = ShapeAnim.Fade }),
        T("Location tag", "Lower thirds", 5, -34, -32, "City, Country", Txt(38), new TextAnim { In = TextIn.FadeWords, InDur = 0.5, Out = TextIn.FadeWords },
          new ShapeLayer { Kind = ShapeKind.Rectangle, X = 128, Y = 0, W = 360, H = 70, Radius = 35, Fill = "#C0101418", In = ShapeAnim.Draw, InDur = 0.4, Out = ShapeAnim.Fade },
          new ShapeLayer { Kind = ShapeKind.Sticker, Sticker = "Pin", X = -30, Y = -2, W = 46, H = 46, Fill = "#FFFF3B5C", Fill2 = "#FFFFFFFF", Above = true, In = ShapeAnim.SlideDown, Delay = 0.2, InDur = 0.4, Loop = ShapeLoop.Bounce, Out = ShapeAnim.Fade }),
        T("Headline bar", "Lower thirds", 6, -46, 36, "Headline goes here", Txt(40), new TextAnim { In = TextIn.FadeWords, InDur = 0.5 },
          new ShapeLayer { Kind = ShapeKind.Rectangle, X = 600, Y = 0, W = 1500, H = 74, Fill = "#E0B01822", In = ShapeAnim.SlideRight, InDur = 0.5, Out = ShapeAnim.SlideRight, OutDur = 0.4 },
          new ShapeLayer { Kind = ShapeKind.Rectangle, X = -70, Y = 0, W = 110, H = 74, Fill = "#FFFFFFFF", Above = false, In = ShapeAnim.Grow, Delay = 0.3, InDur = 0.3, Out = ShapeAnim.Fade },
          new ShapeLayer { Kind = ShapeKind.Text, Text = "LIVE", Style = Txt(30, "#FFB01822", 900, "Center"), X = -70, Y = 0, Above = true, In = ShapeAnim.Fade, Delay = 0.45, InDur = 0.3, Out = ShapeAnim.Fade }),
        T("Heart pop", "Stickers", 3, 30, -20, "", Txt(40, align: "Center"), null,
          new ShapeLayer { Kind = ShapeKind.Sticker, Sticker = "Heart", W = 140, H = 140, Fill = "#FFFF3B5C", Shadow = true, In = ShapeAnim.Pop, InDur = 0.45, Loop = ShapeLoop.Pulse, Out = ShapeAnim.Grow, OutDur = 0.3 }),
        T("Sparkle", "Stickers", 3, 30, -20, "", Txt(40, align: "Center"), null,
          new ShapeLayer { Kind = ShapeKind.Sticker, Sticker = "Sparkle", W = 150, H = 150, Fill = "#FFFFE14A", Fill2 = "#FFFFFFFF", In = ShapeAnim.Spin, InDur = 0.5, Loop = ShapeLoop.Pulse, Out = ShapeAnim.Fade }),
        T("Fire", "Stickers", 3, 30, -20, "", Txt(40, align: "Center"), null,
          new ShapeLayer { Kind = ShapeKind.Sticker, Sticker = "Fire", W = 130, H = 130, Fill = "#FFFF6A1A", Fill2 = "#FFFFD23C", In = ShapeAnim.Grow, InDur = 0.4, Loop = ShapeLoop.Wiggle, Out = ShapeAnim.Fade }),
    };
}
