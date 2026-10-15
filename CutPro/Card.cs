using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace CutPro;

/// <summary>
/// A panel of the main window (title bar, activity bar, side bar, monitor, timeline, inspector, status bar). Flat themes leave it
/// square and edge-less; material themes make it a rounded slab of glass / stone / metal / nature: soft shadow, surface
/// (texture), sheen and a bevel edge are drawn here (no Effect — those re-render the live video every frame), and the
/// content is clipped to the rounded shape. Each card can carry its own text colours (<see cref="ThemeManager.CardPalette"/>,
/// applied as local resources) so a white marble panel gets dark text next to a graphite one with light text.
/// </summary>
public sealed class Card : System.Windows.Controls.Decorator
{
    public static readonly DependencyProperty BackgroundProperty = Dp(nameof(Background), typeof(Brush), null);
    public static readonly DependencyProperty SheenProperty = Dp(nameof(Sheen), typeof(Brush), null);
    public static readonly DependencyProperty EdgeProperty = Dp(nameof(Edge), typeof(Brush), null);
    public static readonly DependencyProperty EdgeWidthProperty = Dp(nameof(EdgeWidth), typeof(double), 0.0, measure: true);
    public static readonly DependencyProperty RadiusProperty = Dp(nameof(Radius), typeof(double), 0.0, measure: true);
    public static readonly DependencyProperty ShadowProperty = Dp(nameof(Shadow), typeof(double), 0.0);
    /// <summary>Which theme region this is ("Title", "Activity", "Side", "Editor", "Panel", "Insp", "Status").</summary>
    public string Region { get; set; } = "";

    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush? Sheen { get => (Brush?)GetValue(SheenProperty); set => SetValue(SheenProperty, value); }
    public Brush? Edge { get => (Brush?)GetValue(EdgeProperty); set => SetValue(EdgeProperty, value); }
    public double EdgeWidth { get => (double)GetValue(EdgeWidthProperty); set => SetValue(EdgeWidthProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public double Shadow { get => (double)GetValue(ShadowProperty); set => SetValue(ShadowProperty, value); }

    static DependencyProperty Dp(string name, Type t, object? def, bool measure = false) =>
        DependencyProperty.Register(name, t, typeof(Card), new FrameworkPropertyMetadata(def, measure ? FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender
                                                                                                        : FrameworkPropertyMetadataOptions.AffectsRender));

    public Card()
    {
        SetResourceReference(SheenProperty, "B.Card.Sheen");
        SetResourceReference(EdgeProperty, "B.Card.Edge");
        SetResourceReference(EdgeWidthProperty, "Th.CardEdge");
        SetResourceReference(RadiusProperty, "Th.Radius");
        SetResourceReference(ShadowProperty, "Th.Shadow");
        SetResourceReference(MarginProperty, "Th.CardMargin");
        SetResourceReference(TextElement.ForegroundProperty, "B.Fg");      // the card's own text colour (local palette)
    }

    /// <summary>Surface brush resource key, e.g. "B.Surf.Side".</summary>
    public string Surface { get => surface; set { surface = value; SetResourceReference(BackgroundProperty, value); } }
    string surface = "";

    protected override Size MeasureOverride(Size c)
    {
        double e = EdgeWidth;
        if (Child == null) return new Size(2 * e, 2 * e);
        Child.Measure(new Size(Math.Max(0, c.Width - 2 * e), Math.Max(0, c.Height - 2 * e)));
        return new Size(Child.DesiredSize.Width + 2 * e, Child.DesiredSize.Height + 2 * e);
    }

    protected override Size ArrangeOverride(Size s)
    {
        double e = EdgeWidth;
        if (Child != null)
        {
            var inner = new Rect(e, e, Math.Max(0, s.Width - 2 * e), Math.Max(0, s.Height - 2 * e));
            Child.Arrange(inner);
            double r = Math.Max(0, Radius - e);
            Child.Clip = r > 0.5 ? new RectangleGeometry(new Rect(0, 0, inner.Width, inner.Height), r, r) : null;
        }
        return s;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, r = Radius;
        if (w <= 0 || h <= 0) return;
        var rect = new Rect(0, 0, w, h);
        // soft shadow: stacked, slightly offset rounded rects fading out (cheap, no Effect)
        if (Shadow > 0.5)
        {
            int steps = 6;
            for (int i = steps; i >= 1; i--)
            {
                double grow = Shadow * i / steps;
                var sb = new SolidColorBrush(Color.FromArgb((byte)(16 * (steps - i + 1) / steps + 4), 0, 0, 0)); sb.Freeze();
                dc.DrawRoundedRectangle(sb, null, new Rect(-grow * 0.6, -grow * 0.3, w + grow * 1.2, h + grow * 1.2), r + grow, r + grow);
            }
        }
        if (Background != null) dc.DrawRoundedRectangle(Background, null, rect, r, r);
        if (Sheen != null) dc.DrawRoundedRectangle(Sheen, null, rect, r, r);
        if (Edge != null && EdgeWidth > 0)
        {
            double e = EdgeWidth;
            var pen = new Pen(Edge, e); pen.Freeze();
            dc.DrawRoundedRectangle(null, pen, new Rect(e / 2, e / 2, w - e, h - e), Math.Max(0, r - e / 2), Math.Max(0, r - e / 2));
        }
        // metal plates are screwed on: a slotted screw head in each corner
        if (TryFindResource("Th.Screws") is true && w > 40 && h > 22)
        {
            double inset = Math.Min(h / 2, r * 0.45 + 6.5);
            int k = 0;
            foreach (var (x, y) in new[] { (inset, inset), (w - inset, inset), (inset, h - inset), (w - inset, h - inset) })
                Screw(dc, x, y, (Region.GetHashCode() & 0xFFFF) * 0.37 + k++ * 47);
        }
    }

    static readonly Brush screwShadow = Frozen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)));
    static readonly Brush screwHead = Frozen(new RadialGradientBrush(Color.FromRgb(246, 247, 249), Color.FromRgb(118, 124, 133)) { GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.45, 0.4), RadiusX = 0.7, RadiusY = 0.7 });
    static readonly Pen screwRim = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(150, 40, 40, 44)), 0.6));
    static readonly Pen screwSlot = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(200, 40, 42, 46)), 1.1));
    static readonly Pen screwGlint = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 0.6));
    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    static void Screw(DrawingContext dc, double x, double y, double angleDeg)
    {
        const double rr = 3.3;
        dc.DrawEllipse(screwShadow, null, new Point(x + 0.6, y + 0.8), rr + 0.4, rr + 0.4);
        dc.DrawEllipse(screwHead, screwRim, new Point(x, y), rr, rr);
        double a = angleDeg * Math.PI / 180, dx = Math.Cos(a) * (rr - 0.8), dy = Math.Sin(a) * (rr - 0.8);
        dc.DrawLine(screwSlot, new Point(x - dx, y - dy), new Point(x + dx, y + dy));
        dc.DrawLine(screwGlint, new Point(x - dx + 0.5, y - dy + 0.7), new Point(x + dx + 0.5, y + dy + 0.7));
    }
}
