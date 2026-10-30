using System.Windows;
using System.Windows.Media;

namespace CutPro;

/// <summary>
/// Themed controls for the material themes, as vector art (resources set by <see cref="ThemeManager.Apply"/>): the play / pause
/// button (a leaf-shaped play triangle and twigs; a glass orb; a faceted sapphire; an engraved metal disc), slider thumbs
/// (a robin perched on the track; a glass bead; a round-cut gem; a knurled knob), slider tracks (a vine, filled part in
/// leaf and blossom; a glass tube filling with light; a carved groove inlaid with sapphire; a machined slot inlaid with gold)
/// and toolbar separators (twigs, glass lines, chiselled grooves, bevels). Flat themes keep the classic look.
/// </summary>
public static class ThemeArt
{
    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static SolidColorBrush S(string hex) { var b = new SolidColorBrush(C(hex)); b.Freeze(); return b; }
    static LinearGradientBrush L(double x0, double y0, double x1, double y1, params (string c, double o)[] st)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(x0, y0), EndPoint = new Point(x1, y1) };
        foreach (var (c, o) in st) b.GradientStops.Add(new GradientStop(C(c), o));
        b.Freeze(); return b;
    }
    static RadialGradientBrush R(double cx, double cy, double r, params (string c, double o)[] st)
    {
        var b = new RadialGradientBrush { Center = new Point(cx, cy), GradientOrigin = new Point(cx, cy), RadiusX = r, RadiusY = r };
        foreach (var (c, o) in st) b.GradientStops.Add(new GradientStop(C(c), o));
        b.Freeze(); return b;
    }
    static Pen P(string hex, double w) { var p = new Pen(S(hex), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; p.Freeze(); return p; }
    static Geometry G(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }
    static DrawingImage Img(double w, double h, Action<DrawingContext> draw)
    {
        var g = new DrawingGroup();
        using (var dc = g.Open()) { dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); draw(dc); }
        g.Freeze();
        var i = new DrawingImage(g); i.Freeze(); return i;
    }

    /// <summary>Sets the art resources for a theme (null art = the classic controls).</summary>
    public static void Apply(ResourceDictionary res, EditorTheme th)
    {
        string? kind = th.Name switch { "Frosted Glass" => "glass", "Stone" => "stone", "Metals" => "metal", "Nature" => "nature", _ => null };
        res["Th.Art"] = kind != null;
        res["Th.ArtVis"] = kind != null ? Visibility.Visible : Visibility.Collapsed;
        res["Th.ClassicVis"] = kind != null ? Visibility.Collapsed : Visibility.Visible;
        res["Th.PlayArt"] = kind == null ? null : Play(kind);
        res["Th.PauseArt"] = kind == null ? null : Pause(kind);
        res["Th.ThumbArt"] = kind == null ? null : Thumb(kind);
        (double tw, double thh) = kind switch { "nature" => (26.0, 30.0), null => (14.0, 18.0), _ => (18.0, 18.0) };
        res["Th.ThumbW"] = tw; res["Th.ThumbH"] = thh;
        res["Th.TrackH"] = kind switch { "nature" => 8.0, "glass" => 6.0, null => 3.0, _ => 5.0 };
        res["B.Th.Track"] = kind == null ? res["B.Slider.Track"] : Track(kind, filled: false);
        res["B.Th.TrackFill"] = kind == null ? res["B.Accent"] : Track(kind, filled: true);
        res["B.Th.Sep"] = kind == null ? res["B.Border"] : Separator(kind);
        res["Th.SepW"] = kind switch { "nature" => 4.0, "stone" or "metal" => 2.0, _ => 1.0 };
        res["Th.Screws"] = kind == "metal";
    }

    // ---------------- play / pause ----------------
    static DrawingImage Play(string kind) => kind switch
    {
        // a leaf whose outline is the play triangle: stem on the left, midrib to the tip, veins, a curled highlight
        "nature" => Img(38, 32, dc =>
        {
            var leaf = G("M 6,4 C 15,4.5 26,9 34,16 C 26,23 15,27.5 6,28 C 4.2,22 4.2,10 6,4 Z");
            dc.DrawGeometry(L(0, 0, 1, 1, ("#9AD65A", 0), ("#5BA83A", 0.45), ("#2E7322", 1)), P("#1E4F17", 1.1), leaf);
            dc.DrawGeometry(null, P("#C8EFA0", 0.9), G("M 5.5,16 C 14,15.2 24,15.4 33,16"));
            foreach (var x in new[] { 10.0, 15.5, 21, 26 })
            {
                dc.DrawGeometry(null, P("#B5E28A", 0.6), G(FormattableString.Invariant($"M {x},15.6 Q {x + 3},12 {x + 5.5},{8.5 + (x - 10) * 0.22}")));
                dc.DrawGeometry(null, P("#B5E28A", 0.6), G(FormattableString.Invariant($"M {x},16.2 Q {x + 3},20 {x + 5.5},{23.5 - (x - 10) * 0.22}")));
            }
            dc.DrawGeometry(L(0, 0, 0, 1, ("#55FFFFFF", 0), ("#00FFFFFF", 1)), null, G("M 7,5.5 C 15,6 23,9.5 30,15 C 22,11.5 14,9.5 7,9.5 Z"));
            dc.DrawGeometry(null, P("#6B4A2A", 1.6), G("M 0.8,19.5 C 2.5,18.5 4,17 6,16"));
        }),
        // iPhone-style glass disc: milky pane, dark ink triangle, a bright top specular
        "glass" => Img(34, 34, dc =>
        {
            dc.DrawEllipse(R(0.5, 0.32, 0.72, ("#F2FFFFFF", 0), ("#C8FFFFFF", 0.45), ("#8AE8EEF4", 1)), new Pen(L(0, 0, 0, 1, ("#FFFFFFFF", 0), ("#59FFFFFF", 1)), 1.15), new Point(17, 17), 15.5, 15.5);
            dc.DrawGeometry(S("#1C1C1E"), null, G("M 13,10 L 24,17 L 13,24 Z"));
            dc.DrawEllipse(L(0, 0, 0, 1, ("#E6FFFFFF", 0), ("#00FFFFFF", 1)), null, new Point(12, 8.5), 8, 4.2);
        }),
        // a faceted sapphire cut as a triangle
        "stone" => Img(36, 32, dc =>
        {
            Point a = new(4, 3), b = new(4, 29), t = new(33, 16), c = new(14, 16);
            Geometry Tri(Point p1, Point p2, Point p3) { var s = new StreamGeometry(); using (var g = s.Open()) { g.BeginFigure(p1, true, true); g.LineTo(p2, true, false); g.LineTo(p3, true, false); } s.Freeze(); return s; }
            dc.DrawGeometry(L(0, 0, 1, 1, ("#7FB2FF", 0), ("#2F6BE0", 1)), null, Tri(a, t, c));
            dc.DrawGeometry(L(0, 0, 1, 1, ("#1C47B8", 0), ("#0B1F66", 1)), null, Tri(b, t, c));
            dc.DrawGeometry(L(0, 0, 1, 0, ("#255BD6", 0), ("#163E9E", 1)), null, Tri(a, b, c));
            dc.DrawGeometry(null, P("#A9C8FF", 0.7), Tri(a, b, t));
            foreach (var p in new[] { a, b, t }) dc.DrawLine(P("#8DB4FF", 0.5), c, p);
            dc.DrawGeometry(S("#E6FFFFFF"), null, G("M 21,9.5 L 22,11 L 23.5,11.5 L 22,12 L 21,13.5 L 20,12 L 18.5,11.5 L 20,11 Z"));
        }),
        // an engraved gold triangle on a brushed steel disc with a bevelled rim
        _ => Img(34, 34, dc => { MetalDisc(dc); Engraved(dc, G("M 13,10 L 24,17 L 13,24 Z")); }),
    };

    static DrawingImage Pause(string kind) => kind switch
    {
        // two twigs, one with a sprouting leaf
        "nature" => Img(38, 32, dc =>
        {
            var bark = L(0, 0, 1, 0, ("#8A6440", 0), ("#5E4128", 0.6), ("#3E2A18", 1));
            dc.DrawRoundedRectangle(bark, P("#2A1C10", 0.8), new Rect(10, 5, 6, 23), 3, 3);
            dc.DrawRoundedRectangle(bark, P("#2A1C10", 0.8), new Rect(21, 5, 6, 23), 3, 3);
            dc.DrawLine(P("#3A2716", 0.7), new Point(12, 12), new Point(14, 13)); dc.DrawLine(P("#3A2716", 0.7), new Point(23, 20), new Point(25, 21));
            dc.DrawGeometry(L(0, 0, 1, 1, ("#9AD65A", 0), ("#2E7322", 1)), P("#1E4F17", 0.7), G("M 26,7 C 30,2 35,3 36,4 C 34,8 30,9 26,7 Z"));
        }),
        "glass" => Img(34, 34, dc =>
        {
            dc.DrawEllipse(R(0.5, 0.55, 0.6, ("#1AFFFFFF", 0), ("#33B8E8FF", 0.75), ("#80FFFFFF", 1)), new Pen(L(0, 0, 0, 1, ("#E6FFFFFF", 0), ("#40FFFFFF", 1)), 1.2), new Point(17, 17), 15.5, 15.5);
            dc.DrawRoundedRectangle(S("#EBFFFFFF"), null, new Rect(11.5, 10, 4, 14), 1.5, 1.5);
            dc.DrawRoundedRectangle(S("#EBFFFFFF"), null, new Rect(18.5, 10, 4, 14), 1.5, 1.5);
            dc.DrawEllipse(L(0, 0, 0, 1, ("#CCFFFFFF", 0), ("#00FFFFFF", 1)), null, new Point(13, 8.5), 8, 4.5);
        }),
        "stone" => Img(36, 32, dc =>
        {
            foreach (var x in new[] { 9.0, 20.0 })
            {
                var r = new Rect(x, 4, 7, 24);
                dc.DrawRectangle(L(0, 0, 1, 0, ("#5C93FF", 0), ("#1B47B8", 0.55), ("#0B1F66", 1)), P("#A9C8FF", 0.7), r);
                dc.DrawRectangle(L(0, 0, 0, 1, ("#4DFFFFFF", 0), ("#00FFFFFF", 1)), null, new Rect(x + 1.5, 6, 4, 20));
            }
        }),
        _ => Img(34, 34, dc => { MetalDisc(dc); Engraved(dc, new RectangleGeometry(new Rect(12, 10, 4, 14), 1, 1)); Engraved(dc, new RectangleGeometry(new Rect(18, 10, 4, 14), 1, 1)); }),
    };

    static void MetalDisc(DrawingContext dc)
    {
        dc.DrawEllipse(L(0, 0, 1, 1, ("#FFFFFF", 0), ("#9097A1", 0.55), ("#4C5259", 1)), null, new Point(17, 17), 16, 16);
        dc.DrawEllipse(R(0.42, 0.38, 0.75, ("#F2F3F5", 0), ("#C5C9CF", 0.55), ("#8C939C", 1)), null, new Point(17, 17), 13.6, 13.6);
        for (int i = 3; i < 13; i += 2) dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(i % 4 == 1 ? 26 : 18), 255, 255, 255)), 0.5), new Point(17, 17), i, i);
    }
    static void Engraved(DrawingContext dc, Geometry shape)
    {
        var shadow = shape.Clone(); shadow.Transform = new TranslateTransform(0.8, 1); shadow.Freeze();
        dc.DrawGeometry(S("#73000000"), null, shadow);
        dc.DrawGeometry(L(0, 0, 0, 1, ("#FFE9A3", 0), ("#D9A82E", 0.5), ("#9A6A12", 1)), P("#6E4A08", 0.6), shape);
    }

    // ---------------- slider thumbs ----------------
    static DrawingImage Thumb(string kind) => kind switch
    {
        // a robin perched on the vine: feet at the image's vertical centre (= the track)
        "nature" => Img(26, 30, dc =>
        {
            dc.DrawGeometry(null, P("#6B4E2E", 1.6), G("M 0,15.5 C 8,14.6 17,16.2 26,15.2"));                       // the vine it sits on
            dc.DrawGeometry(S("#5E3D22"), null, G("M 4,9 L 0.5,6.5 L 1,10.5 Z"));                                   // tail
            dc.DrawEllipse(L(0, 0, 0, 1, ("#8A5A33", 0), ("#5E3D22", 1)), P("#3B2614", 0.6), new Point(11, 9.5), 8, 5.5);  // body
            dc.DrawGeometry(S("#E2733A"), null, G("M 13,6.5 C 18,7 19.5,11 16.5,14 C 14,14.5 12,13 12,10 Z"));       // red breast
            dc.DrawGeometry(L(0, 0, 1, 1, ("#6E4A2A", 0), ("#3E2915", 1)), null, G("M 5,8 C 8,5.5 13,6 15,9 C 12,10.5 8,11 5,8 Z"));   // wing
            dc.DrawEllipse(S("#6E4A2A"), P("#3B2614", 0.5), new Point(18.5, 5.5), 4, 3.8);                         // head
            dc.DrawGeometry(S("#E8B33C"), null, G("M 22,5 L 25.5,5.8 L 22,6.8 Z"));                                 // beak
            dc.DrawEllipse(S("#111111"), null, new Point(19.8, 4.8), 0.95, 0.95);
            dc.DrawEllipse(S("#FFFFFF"), null, new Point(20.1, 4.5), 0.3, 0.3);
            dc.DrawLine(P("#C27A3A", 0.8), new Point(10, 14.2), new Point(9.5, 15.2));                              // feet on the vine
            dc.DrawLine(P("#C27A3A", 0.8), new Point(13, 14.2), new Point(13.4, 15.2));
        }),
        "glass" => Img(18, 18, dc =>
        {
            dc.DrawEllipse(R(0.4, 0.35, 0.7, ("#CCFFFFFF", 0), ("#33A9E4FF", 0.6), ("#9900B4E0", 1)), new Pen(S("#B3FFFFFF"), 0.8), new Point(9, 9), 7.5, 7.5);
            dc.DrawEllipse(S("#E6FFFFFF"), null, new Point(6.8, 6.3), 2.2, 1.4);
        }),
        // a round brilliant-cut sapphire seen from above
        "stone" => Img(18, 18, dc =>
        {
            dc.DrawEllipse(R(0.5, 0.5, 0.5, ("#5C93FF", 0), ("#1B47B8", 0.7), ("#0B1F66", 1)), P("#A9C8FF", 0.7), new Point(9, 9), 7.5, 7.5);
            var oct = new StreamGeometry();
            using (var g = oct.Open())
            {
                for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4 + Math.PI / 8; var p = new Point(9 + 4 * Math.Cos(a), 9 + 4 * Math.Sin(a)); if (i == 0) g.BeginFigure(p, true, true); else g.LineTo(p, true, false); }
            }
            oct.Freeze();
            dc.DrawGeometry(S("#334FA0FF"), P("#8DB4FF", 0.5), oct);
            for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4 + Math.PI / 8; dc.DrawLine(P("#668DB4FF", 0.4), new Point(9 + 4 * Math.Cos(a), 9 + 4 * Math.Sin(a)), new Point(9 + 7.5 * Math.Cos(a), 9 + 7.5 * Math.Sin(a))); }
            dc.DrawEllipse(S("#E6FFFFFF"), null, new Point(7, 6.5), 1.2, 0.8);
        }),
        // a knurled metal knob
        _ => Img(18, 18, dc =>
        {
            dc.DrawEllipse(L(0, 0, 1, 1, ("#F5F6F8", 0), ("#8F96A0", 1)), P("#4C5259", 0.6), new Point(9, 9), 8, 8);
            for (int i = 0; i < 24; i++) { double a = i * Math.PI / 12; dc.DrawLine(P(i % 2 == 0 ? "#59000000" : "#66FFFFFF", 0.5), new Point(9 + 6.2 * Math.Cos(a), 9 + 6.2 * Math.Sin(a)), new Point(9 + 7.8 * Math.Cos(a), 9 + 7.8 * Math.Sin(a))); }
            dc.DrawEllipse(R(0.4, 0.35, 0.7, ("#FFFFFF", 0), ("#B5BAC1", 1)), null, new Point(9, 9), 5.6, 5.6);
            dc.DrawEllipse(L(0, 0, 0, 1, ("#FFE9A3", 0), ("#B8861B", 1)), null, new Point(9, 9), 1.6, 1.6);
        }),
    };

    // ---------------- slider tracks ----------------
    static Brush Track(string kind, bool filled)
    {
        switch (kind)
        {
            case "nature":
            {
                // a vine: a wavy stem with leaves (the filled part buds and blossoms), tiled every 44 px
                var g = new DrawingGroup();
                using (var dc = g.Open())
                {
                    dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 44, 8));
                    dc.DrawGeometry(null, P(filled ? "#4F7A2A" : "#6B4E2E", 1.6), G("M 0,4.5 C 11,1.5 22,7.5 33,4.5 C 38,3.2 41,3.8 44,4.5"));
                    dc.DrawGeometry(L(0, 0, 1, 1, ("#8CCB52", 0), ("#3E8E2A", 1)), null, G("M 9,3.2 C 11,0 15,0 16.5,0.6 C 15,2.6 12,3.6 9,3.2 Z"));
                    dc.DrawGeometry(L(0, 0, 1, 1, ("#8CCB52", 0), ("#3E8E2A", 1)), null, G("M 30,5.4 C 32,8 36,8 37.5,7.5 C 36,5.5 33,4.8 30,5.4 Z"));
                    if (filled)
                    {
                        dc.DrawEllipse(S("#F6B8D0"), null, new Point(22, 2.2), 1.6, 1.6); dc.DrawEllipse(S("#FFE27A"), null, new Point(22, 2.2), 0.6, 0.6);
                        dc.DrawEllipse(S("#FFFFFF"), null, new Point(41, 6), 1.3, 1.3); dc.DrawEllipse(S("#FFE27A"), null, new Point(41, 6), 0.5, 0.5);
                    }
                }
                g.Freeze();
                var b = new DrawingBrush(g) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 44, 8), Stretch = Stretch.None, AlignmentY = AlignmentY.Center };
                b.Freeze(); return b;
            }
            case "glass":
                return filled ? L(0, 0, 0, 1, ("#E6E6FAFF", 0), ("#9952B8FF", 0.45), ("#CC2F8CFF", 1))
                              : L(0, 0, 0, 1, ("#66FFFFFF", 0), ("#1AFFFFFF", 0.5), ("#40FFFFFF", 1));
            case "stone":
                return filled ? L(0, 0, 0, 1, ("#0B1F66", 0), ("#3D7BFF", 0.55), ("#9EC0FF", 1))
                              : L(0, 0, 0, 1, ("#80000000", 0), ("#33000000", 0.6), ("#26FFFFFF", 1));
            default:
                return filled ? L(0, 0, 0, 1, ("#7A520C", 0), ("#E9C766", 0.5), ("#FFF1C2", 1))
                              : L(0, 0, 0, 1, ("#99000000", 0), ("#4D000000", 0.6), ("#4DFFFFFF", 1));
        }
    }

    // ---------------- toolbar separators ----------------
    static Brush Separator(string kind)
    {
        switch (kind)
        {
            case "nature":
            {
                var g = new DrawingGroup();
                using (var dc = g.Open())
                {
                    dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 4, 22));
                    dc.DrawGeometry(null, P("#7A5A3A", 1.5), G("M 2,0 C 1,6 3,12 2,22"));
                    dc.DrawGeometry(null, P("#7A5A3A", 0.9), G("M 2,8 L 3.8,5.5"));
                    dc.DrawGeometry(L(0, 0, 1, 1, ("#8CCB52", 0), ("#3E8E2A", 1)), null, G("M 2.2,14 C 4,12 4,10.5 3.6,10 C 2.4,11 2,12.5 2.2,14 Z"));
                }
                g.Freeze();
                var b = new DrawingBrush(g) { Stretch = Stretch.Fill }; b.Freeze(); return b;
            }
            case "glass": return L(0, 0, 0, 1, ("#00FFFFFF", 0), ("#99FFFFFF", 0.5), ("#00FFFFFF", 1));
            case "stone": return L(0, 0, 1, 0, ("#66000000", 0), ("#66000000", 0.5), ("#26FFFFFF", 0.5), ("#26FFFFFF", 1));
            default: return L(0, 0, 1, 0, ("#80000000", 0), ("#80000000", 0.5), ("#59FFFFFF", 0.5), ("#59FFFFFF", 1));
        }
    }
}
