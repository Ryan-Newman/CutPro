using System.Windows;
using System.Windows.Media;

namespace CutPro;

/// <summary>Draws just the playhead and the razor cut guide, so playback doesn't re-render the whole timeline every frame.</summary>
public class PlayheadOverlay : FrameworkElement
{
    public TimelineView Tl = null!;
    public Editor Ed = null!;
    Brush red => ThemeManager.Brush("Playhead");
    Pen redPen => new(red, 1);
    static readonly Pen razorPen = new(Brushes.White, 2);
    static readonly Pen razorShadow = new(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 4);

    public PlayheadOverlay() { IsHitTestVisible = false; ClipToBounds = true; }

    protected override void OnRender(DrawingContext dc)
    {
        if (Tl == null || Ed == null) return;
        double H = ActualHeight, hw = TimelineView.HeaderW;
        // razor guide: a solid line through each clip that the click would cut
        if (Tl.Razor && Tl.HoverT is double ht)
        {
            double x = Tl.T2X(ht);
            if (x >= hw)
                foreach (int r in Tl.HoverRows)
                {
                    double y0 = TimelineView.RulerH + r * TimelineView.RowH + 1, y1 = y0 + TimelineView.RowH - 2;
                    dc.DrawLine(razorShadow, new Point(x, y0), new Point(x, y1));
                    dc.DrawLine(razorPen, new Point(x, y0), new Point(x, y1));
                }
        }
        double px = Tl.T2X(Ed.Time);
        if (px >= hw - 1)
        {
            dc.DrawLine(redPen, new Point(px, 0), new Point(px, H));
            var tri = new StreamGeometry();
            using (var g = tri.Open()) { g.BeginFigure(new Point(px - 6, 0), true, true); g.LineTo(new Point(px + 6, 0), true, false); g.LineTo(new Point(px, 10), true, false); }
            dc.DrawGeometry(red, null, tri);
        }
    }
}
