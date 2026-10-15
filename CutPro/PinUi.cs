using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>
/// Corner pin handles on the program monitor (above the mask overlay, same logical canvas): the selected pinned clip's outline at the playhead
/// with its four corners. Dragging a corner moves it (Shift: horizontally or vertically only) — a keyframe at the playhead when that corner
/// is animated, otherwise the base corners — one undo step per drag. Hidden while playing or while an object tool is on; only the handles
/// take the mouse, so mask editing underneath keeps working.
/// </summary>
public sealed class PinOverlay : FrameworkElement
{
    public Editor Ed = null!;
    public bool Playing;
    /// <summary>False while another monitor tool owns the mouse (object tools, eyedropper).</summary>
    public Func<bool> Enabled = () => true;
    /// <summary>A corner moved (refresh the Effect Controls rows / timeline diamonds).</summary>
    public Action? Edited;

    public PinOverlay() { Width = Editor.W; Height = Editor.H; Focusable = false; Editor.SequenceChanged += () => { Width = Editor.W; InvalidateVisual(); }; }

    /// <summary>The selected pinned clip under the playhead.</summary>
    public Clip? Target => !Playing && Enabled() && Ed?.Selected is { } c && Pin.Active(c) && Ed.Time >= c.Start - 1e-6 && Ed.Time < c.End ? c : null;

    double KeySrc(Clip c) => Anim.SrcTime(c, Math.Clamp(Ed.Time, c.Start, Math.Max(c.Start, c.End - 1.0 / TimeUtil.Fps)));
    /// <summary>The corners as edited at the playhead: keyed values where a corner is animated (no formula), else the base.</summary>
    CornerPin Shown(Clip c)
    {
        var p = new CornerPin();
        double src = KeySrc(c);
        for (int i = 0; i < 8; i++) p[i] = Anim.HasKeys(c, Pin.Props[i]) ? Anim.Keyed(c, Pin.Props[i], src) : Anim.Base(c, Pin.Props[i]);
        return p;
    }
    /// <summary>Corner i (TL, TR, BL, BR) where it shows: % of the frame → logical px, mirrored by the clip's flips (the export flips after the pin).</summary>
    static Point Screen(Clip c, CornerPin p, int i)
    {
        double x = p[i * 2] / 100 * Editor.W, y = p[i * 2 + 1] / 100 * Editor.H;
        return new Point(c.FlipH ? Editor.W - x : x, c.FlipV ? Editor.H - y : y);
    }

    double Unit()
    {
        if (PresentationSource.FromVisual(this) is not { RootVisual: Visual root } || !root.IsAncestorOf(this)) return 1;
        var t = TransformToAncestor(root);
        double s = (t.Transform(new Point(100, 0)) - t.Transform(new Point(0, 0))).Length / 100;
        return s > 1e-6 ? 1 / s : 1;
    }

    int Hit(Point pt)
    {
        if (Target is not { } c) return -1;
        var p = Pin.At(c, Ed.Time);
        double r = 9 * Unit(), best = double.MaxValue; int hit = -1;
        for (int i = 0; i < 4; i++) { double d = (Screen(c, p, i) - pt).Length; if (d <= r && d < best) { best = d; hit = i; } }
        return hit;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters p) =>
        drag != null || Hit(p.HitPoint) >= 0 ? new PointHitTestResult(this, p.HitPoint) : null;

    sealed class Drag { public Clip C = null!; public int Corner; public Point Start; public double X0, Y0; }
    Drag? drag; int hover = -1;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Target is not { } c) return;
        var pt = e.GetPosition(this); int i = Hit(pt);
        if (i < 0) return;
        var p = Shown(c);
        drag = new Drag { C = c, Corner = i, Start = pt, X0 = p[i * 2], Y0 = p[i * 2 + 1] };
        Ed.Checkpoint();
        CaptureMouse(); e.Handled = true; InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pt = e.GetPosition(this);
        if (drag == null)
        {
            int h = Hit(pt);
            if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.SizeAll : null; InvalidateVisual(); }
            return;
        }
        var d = pt - drag.Start;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { if (Math.Abs(d.X) >= Math.Abs(d.Y)) d.Y = 0; else d.X = 0; }
        double dx = d.X / Editor.W * 100 * (drag.C.FlipH ? -1 : 1), dy = d.Y / Editor.H * 100 * (drag.C.FlipV ? -1 : 1);
        MoveCorner(drag.C, drag.Corner, Math.Round((drag.X0 + dx) * 100) / 100, Math.Round((drag.Y0 + dy) * 100) / 100);
        Ed.Touch(); Edited?.Invoke(); InvalidateVisual();
    }

    /// <summary>Corner i (TL, TR, BL, BR) to (x, y) % of the frame at the playhead: a key for a coordinate that is animated, else the base corners.</summary>
    public void MoveCorner(Clip c, int i, double x, double y) { Set(c, i * 2, x); Set(c, i * 2 + 1, y); }

    void Set(Clip c, int i, double v)
    {
        var p = Pin.Props[i]; var info = Anim.Of(p);
        v = Math.Clamp(v, info.Min, info.Max);
        if (Anim.HasKeys(c, p)) Anim.SetKey(c, p, KeySrc(c), v);
        else Anim.SetBase(c, p, v);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (drag == null) return;
        drag = null; ReleaseMouseCapture();
        Ed.Commit(); Edited?.Invoke(); InvalidateVisual();
        e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { if (drag != null) { drag = null; Ed.Commit(); InvalidateVisual(); } }
    protected override void OnMouseLeave(MouseEventArgs e) { if (hover >= 0 && drag == null) { hover = -1; Cursor = null; InvalidateVisual(); } }

    protected override void OnRender(DrawingContext dc)
    {
        if (Target is not { } c) return;
        var p = Pin.At(c, Ed.Time);
        double u = Unit();
        var accent = ThemeManager.Brush("Accent");
        var pts = new[] { Screen(c, p, 0), Screen(c, p, 1), Screen(c, p, 3), Screen(c, p, 2) };   // (outline order: TL TR BR BL)
        var g = new StreamGeometry();
        using (var ctx = g.Open()) { ctx.BeginFigure(pts[0], false, true); ctx.PolyLineTo(pts[1..], true, true); }
        g.Freeze();
        dc.DrawGeometry(null, new Pen(Brushes.Black, 3 * u), g);
        dc.DrawGeometry(null, new Pen(accent, 1.4 * u), g);
        for (int i = 0; i < 4; i++)
        {
            var at = Screen(c, p, i); double s = (drag?.Corner == i || hover == i ? 6 : 4.5) * u;
            var r = new Rect(at.X - s, at.Y - s, 2 * s, 2 * s);
            dc.DrawRectangle(drag?.Corner == i || hover == i ? accent : Brushes.White, new Pen(Brushes.Black, 1.2 * u), r);
        }
        if (drag != null)
        {
            int i = drag.Corner;
            var tx = new FormattedText($"{Pin.Corners[i]}  {p[i * 2].ToString("0.#", CultureInfo.InvariantCulture)} %, {p[i * 2 + 1].ToString("0.#", CultureInfo.InvariantCulture)} %"
                                       + (Anim.HasKeys(c, Pin.Props[i * 2]) || Anim.HasKeys(c, Pin.Props[i * 2 + 1]) ? "  ◆" : ""),
                                       CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12 * u, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var at = Screen(c, p, i);
            double x = Math.Clamp(at.X + 10 * u, 0, Math.Max(0, Editor.W - tx.Width - 8 * u)), y = Math.Clamp(at.Y - 26 * u, 0, Editor.H - tx.Height - 4 * u);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), null, new Rect(x - 4 * u, y - 2 * u, tx.Width + 8 * u, tx.Height + 4 * u), 3 * u, 3 * u);
            dc.DrawText(tx, new Point(x, y));
        }
    }
}
