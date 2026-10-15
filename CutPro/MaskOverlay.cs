using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

public enum ObjectTool { None, AI, Ellipse, Rectangle, Freehand }

/// <summary>
/// Layer over the program monitor (1280x720 sequence space): draws the selected clip's object outlines for the current
/// frame (the active one highlighted, like Premiere's mask path), AI prompt points, shapes being drawn and a busy label;
/// and turns clicks / drags into selections: AI click (Shift+click adds to the object, Alt+click or right-click removes),
/// ellipse / rectangle drag, freehand lasso.
/// With no tool, the active mask is editable on the current frame (Premiere-style mask path editing): drag a point, Shift+drag
/// for a soft (proportional) drag, drag inside to move, Ctrl+click an edge to add a point, Alt+click a point to remove it,
/// Ctrl+wheel to scale, Ctrl+Shift+wheel to rotate. Every correction becomes a keyframe (one undo step each).
/// </summary>
public class MaskOverlay : FrameworkElement
{
    public Editor Ed = null!;
    public string? ActiveMaskId;
    public List<(float x, float y, float label)> AiPoints = new();
    public string? Busy;
    /// <summary>While playing: outlines only (no handles, no editing).</summary>
    public bool Playing;
    public event Action<Clip, float, float, bool>? AiClick;               // clip, nx, ny, positive
    public event Action<Clip, MaskKind, List<float[]>>? ShapeDrawn;
    /// <summary>A manual correction was committed: clip, mask, source frame.</summary>
    public event Action<Clip, ObjectMask, int>? Corrected;
    /// <summary>One-shot eyedropper (chroma key colour): the next left click on the target clip calls it with the point in the clip's
    /// picture (0…1, as shown) and clears it.</summary>
    public Action<Clip, float, float>? PickColor;

    ObjectTool tool;
    public ObjectTool Tool
    {
        get => tool;
        set { tool = value; Cursor = value == ObjectTool.None ? null : Cursors.Cross; InvalidateVisual(); }
    }

    Point? dragStart; readonly List<Point> lasso = new(); Point dragNow;

    public MaskOverlay() { Width = Editor.W; Height = Editor.H; Focusable = false; Editor.SequenceChanged += () => { Width = Editor.W; InvalidateVisual(); }; }

    /// <summary>Selected video/image clip under the playhead (the one selections apply to).</summary>
    public Clip? Target => Ed.Selected is { Kind: ClipKind.Video or ClipKind.Image } c && Ed.Time >= c.Start - 1e-6 && Ed.Time < c.End ? c : null;

    /// <summary>Where the clip's picture sits on the 1280x720 canvas (same maths as Preview / export).</summary>
    public Rect ClipRect(Clip c)
    {
        var (x, y, w, h, _) = Editor.Placement(Ed.MediaOf(c), c, Ed.Time);
        return new Rect(x, y, w, h);
    }

    /// <summary>The target clip's rotation about its centre (screen = rotation · clip space); null when not rotated.</summary>
    RotateTransform? Rot(Clip? c)
    {
        if (c == null || Ed == null) return null;
        var (x, y, w, h, rot) = Editor.Placement(Ed.MediaOf(c), c, Ed.Time);
        return Math.Abs(rot) < 1e-6 ? null : new RotateTransform(rot, x + w / 2, y + h / 2);
    }
    /// <summary>Screen point → the (unrotated) space of the target clip, where outlines, handles and drags live.</summary>
    Point Local(Point p) => Rot(Target) is { } r && r.Inverse is { } inv ? inv.Transform(p) : p;
    Point Screen(Point p) => Rot(Target) is { } r ? r.Transform(p) : p;

    // ---------------- editing the active mask ----------------
    enum HitKind { None, Vertex, Edge, Inside }
    record struct Hit(HitKind Kind, int Poly = -1, int Index = -1, Point At = default);
    record Editable(Clip C, ObjectMask M, MaskTrack Tr, int Frame, List<float[]> Polys, Rect R);

    Editable? CurrentEditable()
    {
        if (Ed == null || tool != ObjectTool.None || Playing || Busy != null) return null;
        var c = Target;
        if (c == null || c.Masks.FirstOrDefault(x => x.Id == ActiveMaskId) is not { } m) return null;
        if (!Ed.MaskTracks.TryGetValue(m.Id, out var tr)) return null;
        int frame = Editor.SourceFrame(c, Ed.Time);
        var polys = tr.At(frame);
        var r = ClipRect(c);
        return polys == null || r.Width < 1 ? null : new Editable(c, m, tr, frame, polys, r);
    }

    /// <summary>Overlay units per screen DIP (handles keep their on-screen size whatever the monitor zoom).</summary>
    double Unit()
    {
        if (PresentationSource.FromVisual(this) is not { RootVisual: Visual root } || !root.IsAncestorOf(this)) return 1;
        var t = TransformToAncestor(root);
        double s = (t.Transform(new Point(100, 0)) - t.Transform(new Point(0, 0))).Length / 100;
        return s > 1e-6 ? 1 / s : 1;
    }

    static Point P(Rect r, float[] p, int i) => new(r.X + p[i * 2] * r.Width, r.Y + p[i * 2 + 1] * r.Height);

    Hit HitTestMask(Editable e, Point pt)
    {
        double u = Unit(), rv = 7 * u, re = 5 * u;
        Hit best = default; double bd = double.MaxValue;
        for (int k = 0; k < e.Polys.Count; k++)
        {
            var p = e.Polys[k]; int n = p.Length / 2;
            for (int i = 0; i < n; i++)
            {
                double d = (P(e.R, p, i) - pt).Length;
                if (d <= rv && d < bd) { bd = d; best = new Hit(HitKind.Vertex, k, i); }
            }
        }
        if (best.Kind != HitKind.None) return best;
        for (int k = 0; k < e.Polys.Count; k++)
        {
            var p = e.Polys[k]; int n = p.Length / 2;
            for (int i = 0; i < n; i++)
            {
                Point a = P(e.R, p, i), b = P(e.R, p, (i + 1) % n);
                var ab = b - a; double len2 = ab.LengthSquared;
                double t = len2 < 1e-9 ? 0 : Math.Clamp(Vector.Multiply(pt - a, ab) / len2, 0, 1);
                var q = a + ab * t; double d = (q - pt).Length;
                if (d <= re && d < bd) { bd = d; best = new Hit(HitKind.Edge, k, i, q); }
            }
        }
        if (best.Kind != HitKind.None) return best;
        return ObjectTracker.Inside(e.Polys, (pt.X - e.R.X) / e.R.Width, (pt.Y - e.R.Y) / e.R.Height) ? new Hit(HitKind.Inside) : default;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters p)
    {
        if (tool != ObjectTool.None || edit != null || PickColor != null) return new PointHitTestResult(this, p.HitPoint);
        return CurrentEditable() is { } e && HitTestMask(e, Local(p.HitPoint)).Kind != HitKind.None ? new PointHitTestResult(this, p.HitPoint) : null;
    }

    // drag state
    enum EditMode { Vertex, Soft, Move }
    sealed class EditDrag { public Editable E = null!; public EditMode Mode; public int Poly, Index; public List<float[]> Base = null!; public Point Start; public bool Moved; }
    EditDrag? edit;
    Hit hover;

    static List<float[]> Copy(List<float[]> polys) => polys.Select(p => (float[])p.Clone()).ToList();

    void Commit(Editable e, List<float[]> polys) { e.Tr.Set(e.Frame, polys, key: true); Ed.RaiseMasks(); }

    bool EditMouseDown(MouseButtonEventArgs ev)
    {
        if (ev.ChangedButton != MouseButton.Left || CurrentEditable() is not { } e) return false;
        var pt = Local(ev.GetPosition(this));
        var hit = HitTestMask(e, pt);
        if (hit.Kind == HitKind.None) return false;
        var mods = Keyboard.Modifiers;
        if (hit.Kind == HitKind.Vertex && mods.HasFlag(ModifierKeys.Alt))
        {
            if (e.Polys[hit.Poly].Length / 2 <= 3) return true;
            var polys = Copy(e.Polys);
            var list = polys[hit.Poly].ToList(); list.RemoveRange(hit.Index * 2, 2); polys[hit.Poly] = list.ToArray();
            Ed.MaskBegin(e.M.Id); Commit(e, polys); Ed.MaskEnd();
            Corrected?.Invoke(e.C, e.M, e.Frame);
            return true;
        }
        var d = new EditDrag { E = e, Start = pt, Base = Copy(e.Polys), Poly = hit.Poly, Index = hit.Index,
                               Mode = hit.Kind == HitKind.Vertex ? (mods.HasFlag(ModifierKeys.Shift) ? EditMode.Soft : EditMode.Vertex) : EditMode.Move };
        if (hit.Kind == HitKind.Edge && mods.HasFlag(ModifierKeys.Control))
        {
            // insert a point on the edge and drag it
            var list = d.Base[hit.Poly].ToList();
            list.InsertRange((hit.Index + 1) * 2, new[] { (float)((hit.At.X - e.R.X) / e.R.Width), (float)((hit.At.Y - e.R.Y) / e.R.Height) });
            d.Base[hit.Poly] = list.ToArray(); d.Index = hit.Index + 1; d.Mode = EditMode.Vertex;
            Ed.MaskBegin(e.M.Id); Commit(e, Copy(d.Base)); d.Moved = true;
        }
        else Ed.MaskBegin(e.M.Id);
        edit = d;
        CaptureMouse();
        return true;
    }

    void EditMouseMove(Point pt)
    {
        var d = edit!; var e = d.E;
        double dx = (pt.X - d.Start.X) / e.R.Width, dy = (pt.Y - d.Start.Y) / e.R.Height;
        if (!d.Moved && (pt - d.Start).Length < 2 * Unit()) return;
        d.Moved = true;
        var polys = Copy(d.Base);
        switch (d.Mode)
        {
            case EditMode.Move:
                for (int k = 0; k < polys.Count; k++) polys[k] = MaskGeometry.Translate(d.Base[k], (float)dx, (float)dy);
                break;
            case EditMode.Vertex:
                polys[d.Poly][d.Index * 2] += (float)dx; polys[d.Poly][d.Index * 2 + 1] += (float)dy;
                break;
            case EditMode.Soft:
            {
                // neighbours follow with a falloff over ~15% of the outline length (measured on screen, so aspect is kept)
                var p = d.Base[d.Poly]; int n = p.Length / 2;
                var cum = new double[n + 1];
                for (int i = 0; i < n; i++) cum[i + 1] = cum[i] + (P(e.R, p, (i + 1) % n) - P(e.R, p, i)).Length;
                double total = cum[n], sigma = Math.Max(total * 0.15, 1e-6);
                for (int i = 0; i < n; i++)
                {
                    double along = Math.Abs(cum[i] - cum[d.Index]), dist = Math.Min(along, total - along);
                    double wgt = Math.Exp(-(dist * dist) / (2 * sigma * sigma));
                    polys[d.Poly][i * 2] += (float)(dx * wgt); polys[d.Poly][i * 2 + 1] += (float)(dy * wgt);
                }
                break;
            }
        }
        Commit(e, polys);
    }

    void EditMouseUp()
    {
        var d = edit!; edit = null;
        ReleaseMouseCapture();
        Ed.MaskEnd();
        if (d.Moved) Corrected?.Invoke(d.E.C, d.E.M, d.E.Frame);
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs ev)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || CurrentEditable() is not { } e) { base.OnMouseWheel(ev); return; }
        var (cx, cy) = MaskGeometry.Centroid(e.Polys);
        List<float[]> polys;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            // rotate on screen (pixel space), 3° a notch
            double a = Math.Sign(ev.Delta) * 3 * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a), W = e.R.Width, H = e.R.Height;
            polys = e.Polys.Select(p =>
            {
                var o = new float[p.Length];
                for (int i = 0; i < p.Length; i += 2)
                {
                    double x = (p[i] - cx) * W, y = (p[i + 1] - cy) * H;
                    o[i] = (float)(cx + (x * cos - y * sin) / W); o[i + 1] = (float)(cy + (x * sin + y * cos) / H);
                }
                return o;
            }).ToList();
        }
        else polys = MaskGeometry.Scale(e.Polys, ev.Delta > 0 ? 1.04 : 1 / 1.04, cx, cy);
        Ed.MaskEdit(e.M.Id, tr => tr.Set(e.Frame, polys, key: true));
        Corrected?.Invoke(e.C, e.M, e.Frame);
        ev.Handled = true;
    }

    // ---------------- drawing ----------------
    protected override void OnRender(DrawingContext dc)
    {
        if (Ed == null) return;
        if (tool != ObjectTool.None) dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Editor.W, Editor.H));   // hit-testable everywhere
        var c = Target;
        var accent = ThemeManager.Brush("Accent");
        var editable = CurrentEditable();
        double u = Unit();
        (string text, Point at)? label = null;
        var pen = new Pen(accent, 2.5 * u) { DashStyle = DashStyles.Dash };
        if (c != null)
        {
            var r = ClipRect(c);
            var rot = Rot(c);
            if (rot != null) dc.PushTransform(rot);                   // everything below is in the clip's own (rotated) space
            int frame = Editor.SourceFrame(c, Ed.Time);
            foreach (var m in c.Masks)
            {
                var tr = Ed.MaskTracks.GetValueOrDefault(m.Id);
                var polys = tr?.At(frame);
                var g = MaskGeometry.Outline(polys, r.Width, r.Height, 0);
                if (g == null) continue;
                bool active = m.Id == ActiveMaskId;
                dc.PushTransform(new TranslateTransform(r.X, r.Y));
                if (active) dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(edit?.Mode == EditMode.Move || hover.Kind == HitKind.Inside ? (byte)60 : (byte)34, 255, 255, 255)), null, g);
                dc.DrawGeometry(null, new Pen(Brushes.Black, (active ? 4 : 3.5) * u) { LineJoin = PenLineJoin.Round }, g);
                dc.DrawGeometry(null, new Pen(active ? accent : Brushes.White, (active ? 2 : 1.6) * u) { LineJoin = PenLineJoin.Round, DashStyle = m.Enabled ? DashStyles.Solid : DashStyles.Dash }, g);
                dc.Pop();
                if (active && polys != null && tr != null)
                {
                    var b = MaskGeometry.Bounds(polys);
                    string state = tr.IsKey(frame) ? "◆ keyframe" : tr.IsTracked(frame) ? "tracked" : tr.Frames.Count > 1 && frame > tr.Range.first && frame < tr.Range.last ? "interpolated" : "held";
                    label = ($"{m.Name} · {state}", Screen(new Point(r.X + b.Left * r.Width, r.Y + b.Top * r.Height)) - new Vector(0, 26 * u));
                    if (editable != null && editable.M.Id == m.Id) Handles(dc, editable, accent, u);
                }
            }
            foreach (var (x, y, lbl) in AiPoints)
            {
                var p = new Point(r.X + x * r.Width, r.Y + y * r.Height);
                dc.DrawEllipse(lbl > 0 ? Brushes.LimeGreen : Brushes.Red, new Pen(Brushes.White, 2 * u), p, 8 * u, 8 * u);
                dc.DrawLine(new Pen(Brushes.White, 2 * u), new Point(p.X - 4 * u, p.Y), new Point(p.X + 4 * u, p.Y));
                if (lbl > 0) dc.DrawLine(new Pen(Brushes.White, 2 * u), new Point(p.X, p.Y - 4 * u), new Point(p.X, p.Y + 4 * u));
            }
            // shape being drawn (clip space)
            if (dragStart is Point s && tool is ObjectTool.Ellipse or ObjectTool.Rectangle)
            {
                var rr = new Rect(s, dragNow);
                if (tool == ObjectTool.Ellipse) dc.DrawEllipse(null, pen, new Point(rr.X + rr.Width / 2, rr.Y + rr.Height / 2), rr.Width / 2, rr.Height / 2);
                else dc.DrawRectangle(null, pen, rr);
            }
            if (lasso.Count > 1)
            {
                var sg = new StreamGeometry();
                using (var ctx = sg.Open()) { ctx.BeginFigure(lasso[0], false, false); ctx.PolyLineTo(lasso.Skip(1).ToList(), true, true); }
                dc.DrawGeometry(null, pen, sg);
            }
            if (rot != null) dc.Pop();
            if (label is { } lb) Label(dc, lb.text, lb.at, accent, u);
        }
        var top = new Point(20 * u, 20 * u);
        if (tool != ObjectTool.None && c == null)
            Label(dc, "Select a video or image clip on the timeline, with the playhead over it", top, accent, u);
        else if (tool == ObjectTool.AI && c != null && Busy == null)
            Label(dc, AiPoints.Count == 0 ? "Click a person or object to select it · Shift+click corrects the selected mask on this frame"
                                          : "Shift+click adds · Alt+click / right-click removes · Esc finishes", top, accent, u);
        else if (editable != null && (hover.Kind != HitKind.None || edit != null))
            Label(dc, "Drag points · Shift+drag soft · drag inside to move · Ctrl+click edge adds a point · Alt+click removes · Ctrl+wheel scale · Ctrl+Shift+wheel rotate",
                  new Point(20 * u, Editor.H - 50 * u), accent, u, 15);
        if (Busy != null) Label(dc, Busy, new Point(20 * u, Editor.H - 60 * u), accent, u);
    }

    void Handles(DrawingContext dc, Editable e, Brush accent, double u)
    {
        int count = e.Polys.Sum(p => p.Length / 2);
        double s = (count > 120 ? 2.5 : 3.5) * u;
        var fill = Brushes.White; var pen = new Pen(accent, 1.5 * u);
        for (int k = 0; k < e.Polys.Count; k++)
            for (int i = 0; i < e.Polys[k].Length / 2; i++)
            {
                var p = P(e.R, e.Polys[k], i);
                bool hot = (hover.Kind == HitKind.Vertex && hover.Poly == k && hover.Index == i) || (edit is { Mode: not EditMode.Move } d && d.Poly == k && d.Index == i);
                double hs = hot ? s * 1.6 : s;
                dc.DrawRectangle(hot ? accent : fill, pen, new Rect(p.X - hs, p.Y - hs, hs * 2, hs * 2));
            }
        if (hover.Kind == HitKind.Edge && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            dc.DrawEllipse(accent, new Pen(Brushes.White, 1.5 * u), hover.At, s * 1.3, s * 1.3);
    }

    void Label(DrawingContext dc, string text, Point at, Brush accent, double u, double size = 20)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), size * Math.Min(u, 1.6), Brushes.White, 1.0);
        var bg = new Rect(at.X, Math.Max(0, at.Y), ft.Width + 20 * u, ft.Height + 10 * u);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 20, 20, 24)), new Pen(accent, 1.5 * u), bg, 6 * u, 6 * u);
        dc.DrawText(ft, new Point(bg.X + 10 * u, bg.Y + 5 * u));
    }

    (float x, float y)? Norm(Clip c, Point p)
    {
        var r = ClipRect(c);
        if (r.Width < 1 || r.Height < 1) return null;
        return ((float)((p.X - r.X) / r.Width), (float)((p.Y - r.Y) / r.Height));
    }

    // ---------------- mouse ----------------
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (PickColor is { } pick && e.ChangedButton == MouseButton.Left)
        {
            if (Target is { } pc && Norm(pc, Local(e.GetPosition(this))) is { } pn && pn.x is >= 0 and <= 1 && pn.y is >= 0 and <= 1) { PickColor = null; Cursor = null; pick(pc, pn.x, pn.y); }
            e.Handled = true; return;
        }
        if (tool == ObjectTool.None) { if (EditMouseDown(e)) e.Handled = true; return; }
        var c = Target; if (c == null) return;
        var p = Local(e.GetPosition(this));
        if (tool == ObjectTool.AI)
        {
            if (Norm(c, p) is not { } n || n.x < 0 || n.y < 0 || n.x > 1 || n.y > 1) return;
            bool negative = e.ChangedButton == MouseButton.Right || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            AiClick?.Invoke(c, n.x, n.y, !negative);
            e.Handled = true;
            return;
        }
        dragStart = dragNow = p; lasso.Clear();
        if (tool == ObjectTool.Freehand) lasso.Add(p);
        CaptureMouse(); e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pt = Local(e.GetPosition(this));
        if (edit != null) { EditMouseMove(pt); return; }
        if (tool == ObjectTool.None)
        {
            var h = CurrentEditable() is { } ed ? HitTestMask(ed, pt) : default;
            if (h != hover) { hover = h; InvalidateVisual(); }
            Cursor = h.Kind switch
            {
                HitKind.Vertex => Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? Cursors.No : Cursors.Hand,
                HitKind.Edge => Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? Cursors.Cross : Cursors.SizeAll,
                HitKind.Inside => Cursors.SizeAll,
                _ => null,
            };
            return;
        }
        if (dragStart == null) return;
        dragNow = pt;
        if (tool == ObjectTool.Freehand && (lasso.Count == 0 || (dragNow - lasso[^1]).Length > 4)) lasso.Add(dragNow);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { if (hover.Kind != HitKind.None) { hover = default; InvalidateVisual(); } }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (edit != null) { EditMouseUp(); e.Handled = true; return; }
        if (dragStart is not Point s) return;
        ReleaseMouseCapture();
        var c = Target; dragStart = null;
        if (c != null)
        {
            var r = ClipRect(c);
            float X(double px) => (float)((px - r.X) / r.Width);
            float Y(double py) => (float)((py - r.Y) / r.Height);
            if (tool is ObjectTool.Ellipse or ObjectTool.Rectangle)
            {
                var rr = new Rect(s, dragNow);
                if (rr.Width > 6 && rr.Height > 6)
                    ShapeDrawn?.Invoke(c, tool == ObjectTool.Ellipse ? MaskKind.Ellipse : MaskKind.Rectangle,
                        new() { MaskGeometry.Shape(tool == ObjectTool.Ellipse ? MaskKind.Ellipse : MaskKind.Rectangle, new Rect(X(rr.Left), Y(rr.Top), rr.Width / r.Width, rr.Height / r.Height)) });
            }
            else if (tool == ObjectTool.Freehand && lasso.Count >= 3)
            {
                var poly = new float[lasso.Count * 2];
                for (int i = 0; i < lasso.Count; i++) { poly[i * 2] = X(lasso[i].X); poly[i * 2 + 1] = Y(lasso[i].Y); }
                ShapeDrawn?.Invoke(c, MaskKind.Freehand, new() { poly });
            }
        }
        lasso.Clear();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) { if (edit != null) EditMouseUp(); }
}

/// <summary>Mini timeline of one mask over its clip (Effect Controls): tracked frames as a bar, interpolated spans as a thin
/// line, keyframes as diamonds, the playhead. Click / drag to move the playhead; clicks near a diamond snap to it.</summary>
public class MaskKeyStrip : FrameworkElement
{
    readonly Editor ed; readonly Clip clip; readonly string maskId;
    public MaskKeyStrip(Editor ed, Clip clip, string maskId)
    {
        this.ed = ed; this.clip = clip; this.maskId = maskId;
        Height = 22; Margin = new Thickness(0, 4, 0, 2); Cursor = Cursors.Hand; ClipToBounds = true;
        ToolTip = "Keyframes (◆) and tracked frames of this mask over the clip. Click to move the playhead.";
    }

    (int f0, int f1) Span() => Editor.SourceFrames(clip);
    double X(int f) { var (f0, f1) = Span(); return 6 + (f - f0) / (double)Math.Max(1, f1 - f0) * (ActualWidth - 12); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, mid = h / 2;
        if (w < 2 || h < 2) return;                                   // (laid out at no size for a moment while the inspector rebuilds)
        dc.DrawRoundedRectangle(ThemeManager.Brush(this, "Input.Bg"), new Pen(ThemeManager.Brush(this, "Input.Border"), 1), new Rect(0.5, 0.5, w - 1, h - 1), 4, 4);
        if (!ed.MaskTracks.TryGetValue(maskId, out var tr) || tr.Frames.Count == 0) return;
        var (f0, f1) = Span();
        var accent = ThemeManager.Brush(this, "Accent");
        var trackBrush = new SolidColorBrush(ThemeManager.Color(this, "Accent")) { Opacity = 0.45 };
        // interpolated spans (thin) then tracked runs (thick)
        var keys = tr.Frames.Keys.Where(f => f >= f0 && f <= f1).ToList();
        if (keys.Count > 1) dc.DrawRectangle(trackBrush, null, new Rect(X(keys[0]), mid - 1, Math.Max(1, X(keys[^1]) - X(keys[0])), 2));
        int runStart = -1, prev = -2;
        void Run(int a, int b) { if (a >= 0) dc.DrawRectangle(trackBrush, null, new Rect(X(a) - 1, mid - 4, Math.Max(2, X(b) - X(a) + 2), 8)); }
        foreach (var f in keys)
        {
            if (tr.IsKey(f)) { Run(runStart, prev); runStart = -1; prev = f; continue; }
            if (f != prev + 1 || runStart < 0) { Run(runStart, prev); runStart = f; }
            prev = f;
        }
        Run(runStart, prev);
        var fg = new Pen(ThemeManager.Brush(this, "Fg"), 1);
        foreach (var k in tr.Keys.Where(f => f >= f0 && f <= f1))
        {
            double x = X(k), s = 5;
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(new Point(x, mid - s), true, true); c.PolyLineTo(new[] { new Point(x + s, mid), new Point(x, mid + s), new Point(x - s, mid) }, true, true); }
            dc.DrawGeometry(accent, fg, g);
        }
        int cur = Editor.SourceFrame(clip, ed.Time);
        if (ed.Time >= clip.Start && ed.Time < clip.End)
        {
            double x = X(cur);
            dc.DrawRectangle(ThemeManager.Brush(this, "Playhead"), null, new Rect(x - 0.75, 1, 1.5, h - 2));
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e) { CaptureMouse(); Seek(e.GetPosition(this).X, true); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Seek(e.GetPosition(this).X, false); }
    protected override void OnMouseUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    void Seek(double x, bool snap)
    {
        var (f0, f1) = Span();
        int f = (int)Math.Round(f0 + (x - 6) / Math.Max(1, ActualWidth - 12) * (f1 - f0));
        if (snap && ed.MaskTracks.TryGetValue(maskId, out var tr))
        {
            var near = tr.Keys.Where(k => k >= f0 && k <= f1).OrderBy(k => Math.Abs(X(k) - x)).FirstOrDefault(-1);
            if (near >= 0 && Math.Abs(X(near) - x) <= 7) f = near;
        }
        f = Math.Clamp(f, f0, f1);
        ed.Seek(Editor.TimeOfFrame(clip, f));
    }
}
