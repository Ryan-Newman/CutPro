using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>
/// Keyframe editor for the selected clip (the KEYFRAMES panel): one lane per animatable property plus one per object mask.
/// Dope sheet: diamonds shaped by interpolation (◆ linear, ● auto, ⧗ ease/Bézier, ■ hold). Graph: the selected property's value
/// curve (and the formula result, dashed) with Bézier handles. Click / Shift / Ctrl select, marquee, drag to retime (graph:
/// and change the value), double-click adds a key, Delete removes, Ctrl+C / Ctrl+V copy to the playhead, right-click for
/// interpolation presets. Property keys are source-anchored (Anim), mask keys are MaskTrack keyframes.
/// </summary>
public sealed class KeyframeEditor : FrameworkElement
{
    public Editor Ed = null!;
    public event Action? Edited;
    const double ListW = 200, RulerH = 24, RowH = 24;
    bool graph;
    public bool GraphMode { get => graph; set { graph = value; InvalidateVisual(); } }

    double t0 = 0, t1 = 10;                  // visible timeline time
    string? clipId;
    string? graphLane;                        // property lane shown in the graph
    readonly HashSet<(string lane, long f)> sel = new();

    public KeyframeEditor() { Focusable = true; ClipToBounds = true; }

    sealed record Lane(string Id, string Name, AnimProp? P, string? MaskId);
    new Clip? Clip => Ed?.Selected;
    static List<Lane> Lanes(Clip c) =>
        Anim.PropsOf(c).Select(p => new Lane(p.ToString(), Anim.Of(p).Label, (AnimProp?)p, null))
            .Concat(c.Masks.Select(m => new Lane("mask:" + m.Id, "Mask · " + m.Name, null, m.Id))).ToList();

    // ---------------- coordinates ----------------
    double X(double t) => ListW + (t - t0) / Math.Max(1e-6, t1 - t0) * (ActualWidth - ListW - 8);
    double T(double x) => t0 + (x - ListW) / Math.Max(1, ActualWidth - ListW - 8) * (t1 - t0);
    static long Fr(double seconds) => (long)Math.Round(seconds * TimeUtil.Fps);
    (double lo, double hi) vr = (0, 100);    // graph value range
    double GY(double v) => RulerH + 10 + (vr.hi - v) / Math.Max(1e-9, vr.hi - vr.lo) * (ActualHeight - RulerH - 20);
    double GV(double y) => vr.hi - (y - RulerH - 10) / Math.Max(1, ActualHeight - RulerH - 20) * (vr.hi - vr.lo);

    public void Fit()
    {
        if (Clip is not { } c) return;
        double pad = Math.Max(0.1, c.Dur * 0.04);
        t0 = c.Start - pad; t1 = c.End + pad;
        InvalidateVisual();
    }

    void SyncClip()
    {
        var c = Clip;
        if (c?.Id != clipId) { clipId = c?.Id; sel.Clear(); graphLane = null; if (c != null) Fit(); }
        if (c != null && (graphLane == null || Lanes(c).All(l => l.Id != graphLane)))
            graphLane = Anim.PropsOf(c).Where(p => Anim.IsAnimated(c, p)).Select(p => p.ToString()).FirstOrDefault() ?? Anim.PropsOf(c).First().ToString();
    }

    // ---------------- keys of a lane (timeline time, value, key object) ----------------
    IEnumerable<(double t, long f, Keyframe? k, int maskFrame)> KeysOf(Clip c, Lane l)
    {
        if (l.P is { } p)
        {
            if (Anim.Track(c, p) is { } tr) foreach (var k in tr.Keys) yield return (Anim.TimelineTime(c, k.T), Fr(k.T), k, 0);
        }
        else if (l.MaskId != null && Ed.MaskTracks.TryGetValue(l.MaskId, out var mt))
            foreach (var f in mt.Keys) yield return (Anim.TimelineTime(c, f / (double)TimeUtil.Fps), f, null, f);
    }

    // ---------------- rendering ----------------
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(ThemeManager.Brush(this, "Tl.Bg"), null, new Rect(0, 0, w, h));
        var fg = ThemeManager.Brush(this, "Fg"); var dim = ThemeManager.Brush(this, "Fg.Dim"); var accent = ThemeManager.Brush(this, "Accent");
        var border = new Pen(ThemeManager.Brush(this, "Border"), 1);
        if (Ed == null) return;
        SyncClip();
        var c = Clip;
        if (c == null) { dc.DrawText(Txt("Select a clip to see and edit its keyframes.", 13, dim), new Point(20, 40)); return; }
        var lanes = Lanes(c);
        // clip range + ruler
        double cx1 = Math.Max(ListW, X(c.Start)), cx2 = Math.Min(w, X(c.End));
        dc.DrawRectangle(ThemeManager.Brush(this, "Tl.LaneV"), null, new Rect(cx1, RulerH, Math.Max(0, cx2 - cx1), h - RulerH));
        dc.DrawRectangle(ThemeManager.Brush(this, "Tl.Ruler"), null, new Rect(ListW, 0, w - ListW, RulerH));
        double step = NiceStep((t1 - t0) / Math.Max(1, (w - ListW) / 90));
        var tick = new Pen(ThemeManager.Brush(this, "Tl.Tick"), 1);
        for (double t = Math.Ceiling(t0 / step) * step; t <= t1; t += step)
        {
            double x = X(t); if (x < ListW) continue;
            dc.DrawLine(tick, new Point(x, RulerH - 7), new Point(x, RulerH));
            dc.DrawText(Txt(TimeUtil.Format(t), 10, ThemeManager.Brush(this, "Tl.RulerText")), new Point(x + 3, 4));
        }
        if (!graph) DrawDope(dc, c, lanes, accent, fg, dim, border);
        else DrawGraph(dc, c, accent, fg, dim);
        // list
        dc.DrawRectangle(ThemeManager.Brush(this, "Strip"), null, new Rect(0, 0, ListW, h));
        dc.DrawLine(border, new Point(ListW, 0), new Point(ListW, h));
        dc.DrawText(Txt(graph ? "GRAPH — click a property" : "DOPE SHEET", 10, dim), new Point(10, 6));
        for (int i = 0; i < lanes.Count; i++)
        {
            var l = lanes[i]; double y = RulerH + i * RowH;
            bool on = graph && l.Id == graphLane;
            if (on) dc.DrawRectangle(ThemeManager.Brush(this, "Selected"), null, new Rect(0, y, ListW, RowH));
            bool anim = l.P is { } p ? Anim.IsAnimated(c, p) : true;
            string mark = l.P is { } pp ? (Anim.FormulaOf(c, pp) != null ? "ƒ" : Anim.HasKeys(c, pp) ? "◷" : " ") : "◆";
            dc.DrawText(Txt(mark, 12, anim ? accent : dim), new Point(8, y + 4));
            dc.DrawText(Txt(l.Name, 12, graph && l.P == null ? dim : fg, ListW - 90), new Point(24, y + 4));
            if (l.P is { } vp)
            {
                double v = Anim.Value(c, vp, Math.Clamp(Ed.Time, c.Start, c.End));
                var tv = Txt(v.ToString("0.##", CultureInfo.InvariantCulture), 11, dim);
                dc.DrawText(tv, new Point(ListW - 8 - tv.Width, y + 5));
            }
        }
        // playhead
        double px = X(Ed.Time);
        if (px >= ListW) dc.DrawLine(new Pen(ThemeManager.Brush(this, "Playhead"), 1.5), new Point(px, 0), new Point(px, h));
        // marquee
        if (drag == DragKind.Marquee) dc.DrawRectangle(new SolidColorBrush(ThemeManager.Color(this, "Accent")) { Opacity = 0.15 }, new Pen(accent, 1), new Rect(dStart, dNow));
    }

    void DrawDope(DrawingContext dc, Clip c, List<Lane> lanes, Brush accent, Brush fg, Brush dim, Pen border)
    {
        var outline = new Pen(ThemeManager.Brush(this, "Tl.Bg"), 1);
        for (int i = 0; i < lanes.Count; i++)
        {
            var l = lanes[i]; double y = RulerH + i * RowH, mid = y + RowH / 2;
            dc.DrawLine(new Pen(ThemeManager.Brush(this, "Tl.RowLine"), 1), new Point(ListW, y + RowH), new Point(ActualWidth, y + RowH));
            if (l.MaskId != null && Ed.MaskTracks.TryGetValue(l.MaskId, out var mt))
            {
                // tracked frames as a bar
                var trk = new SolidColorBrush(ThemeManager.Color(this, "Accent")) { Opacity = 0.3 };
                long runA = -1, prev = -2;
                void Run(long a, long b) { if (a >= 0) { double xa = X(Anim.TimelineTime(c, a / (double)TimeUtil.Fps)), xb = X(Anim.TimelineTime(c, b / (double)TimeUtil.Fps)); dc.DrawRectangle(trk, null, new Rect(xa, mid - 3, Math.Max(2, xb - xa), 6)); } }
                foreach (var f in mt.Frames.Keys) { if (mt.IsKey(f)) continue; if (f != prev + 1) { Run(runA, prev); runA = f; } prev = f; }
                Run(runA, prev);
            }
            else if (l.P is { } p && Anim.Track(c, p) is { Keys.Count: > 1 } tr)
                dc.DrawLine(new Pen(ThemeManager.Brush(this, "Tl.Tick"), 1), new Point(X(Anim.TimelineTime(c, tr.Keys[0].T)), mid), new Point(X(Anim.TimelineTime(c, tr.Keys[^1].T)), mid));
            foreach (var (t, f, k, _) in KeysOf(c, l))
            {
                double x = X(t); if (x < ListW - 6 || x > ActualWidth + 6) continue;
                bool s = sel.Contains((l.Id, f));
                Glyph(dc, new Point(x, mid), k, s ? accent : fg, s ? new Pen(Brushes.White, 1.2) : outline, s ? 6 : 5);
            }
        }
    }

    /// <summary>Key glyph by interpolation (After Effects style): ◆ linear, ■ hold, ● auto, ⧗ Bézier / ease.</summary>
    static void Glyph(DrawingContext dc, Point p, Keyframe? k, Brush fill, Pen pen, double s)
    {
        if (k == null || (k.In == KeyType.Linear && k.Out == KeyType.Linear))
        {
            var g = new StreamGeometry();
            using (var x = g.Open()) { x.BeginFigure(new Point(p.X, p.Y - s), true, true); x.PolyLineTo(new[] { new Point(p.X + s, p.Y), new Point(p.X, p.Y + s), new Point(p.X - s, p.Y) }, true, true); }
            dc.DrawGeometry(fill, pen, g);
        }
        else if (k.Out == KeyType.Hold) dc.DrawRectangle(fill, pen, new Rect(p.X - s * 0.8, p.Y - s * 0.8, s * 1.6, s * 1.6));
        else if (k.In == KeyType.Auto && k.Out == KeyType.Auto) dc.DrawEllipse(fill, pen, p, s * 0.9, s * 0.9);
        else
        {
            var g = new StreamGeometry();          // hourglass
            using (var x = g.Open())
            {
                x.BeginFigure(new Point(p.X - s * 0.8, p.Y - s), true, true); x.PolyLineTo(new[] { new Point(p.X + s * 0.8, p.Y - s), new Point(p.X, p.Y) }, true, true);
                x.BeginFigure(new Point(p.X - s * 0.8, p.Y + s), true, true); x.PolyLineTo(new[] { new Point(p.X + s * 0.8, p.Y + s), new Point(p.X, p.Y) }, true, true);
            }
            dc.DrawGeometry(fill, pen, g);
        }
    }

    void DrawGraph(DrawingContext dc, Clip c, Brush accent, Brush fg, Brush dim)
    {
        if (graphLane == null || !Enum.TryParse<AnimProp>(graphLane, out var p)) return;
        var keys = Anim.Track(c, p)?.Keys ?? new List<Keyframe>();
        var f = Anim.FormulaOf(c, p);
        double x0 = ListW, x1 = ActualWidth;
        // value range from both curves over the view
        double lo = double.MaxValue, hi = double.MinValue;
        var keyed = new List<Point>(); var formula = new List<Point>();
        for (double x = x0; x <= x1; x += 2)
        {
            double t = T(x), src = Anim.SrcTime(c, t), v = Anim.Keyed(c, p, src);
            lo = Math.Min(lo, v); hi = Math.Max(hi, v);
            if (f != null && t >= c.Start && t <= c.End) { double r = Anim.Value(c, p, t); lo = Math.Min(lo, r); hi = Math.Max(hi, r); }
        }
        foreach (var k in keys) { lo = Math.Min(lo, k.V); hi = Math.Max(hi, k.V); }
        if (drag is DragKind.Keys or DragKind.Handle && vrLock is { } locked) vr = locked;      // don't rescale under the mouse
        else { if (hi - lo < 1e-6) { lo -= 1; hi += 1; } double m = (hi - lo) * 0.12; vr = (lo - m, hi + m); }
        // grid
        double vs = NiceStep((vr.hi - vr.lo) / Math.Max(1, (ActualHeight - RulerH) / 50));
        var grid = new Pen(ThemeManager.Brush(this, "Tl.RowLine"), 1);
        for (double v = Math.Ceiling(vr.lo / vs) * vs; v <= vr.hi; v += vs)
        {
            double y = GY(v); dc.DrawLine(grid, new Point(x0, y), new Point(x1, y));
            dc.DrawText(Txt(v.ToString("0.##", CultureInfo.InvariantCulture), 10, dim), new Point(x0 + 4, y - 13));
        }
        for (double x = x0; x <= x1; x += 2)
        {
            double t = T(x); keyed.Add(new Point(x, GY(Anim.Keyed(c, p, Anim.SrcTime(c, t)))));
            if (f != null && t >= c.Start && t <= c.End) formula.Add(new Point(x, GY(Anim.Value(c, p, t))));
        }
        Poly(dc, keyed, new Pen(accent, 2));
        if (formula.Count > 1) Poly(dc, formula, new Pen(fg, 1.5) { DashStyle = DashStyles.Dash });
        if (f != null) dc.DrawText(Txt("dashed: after the formula  " + Anim.Track(c, p)!.Formula, 11, dim, ActualWidth - ListW - 20), new Point(x0 + 8, ActualHeight - 20));
        // keys + handles
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i]; var pt = new Point(X(Anim.TimelineTime(c, k.T)), GY(k.V));
            bool s = sel.Contains((graphLane, Fr(k.T)));
            if (s)
                foreach (var (hp, _) in Handles(c, keys, i))
                {
                    dc.DrawLine(new Pen(dim, 1), pt, hp);
                    dc.DrawEllipse(Brushes.White, new Pen(accent, 1.5), hp, 4, 4);
                }
            dc.DrawRectangle(s ? accent : fg, new Pen(ThemeManager.Brush(this, "Tl.Bg"), 1), new Rect(pt.X - 4, pt.Y - 4, 8, 8));
        }
    }

    /// <summary>Screen positions of key i's Bézier handles (in: false, out: true) in the graph.</summary>
    List<(Point p, bool outgoing)> Handles(Clip c, List<Keyframe> keys, int i)
    {
        var r = new List<(Point, bool)>();
        var k = keys[i];
        if (i < keys.Count - 1 && k.Out != KeyType.Hold)
        {
            var (s, w) = Curve.Tangent(keys, i, true); double d = (keys[i + 1].T - k.T) * w;
            r.Add((new Point(X(Anim.TimelineTime(c, k.T + d)), GY(k.V + s * d)), true));
        }
        if (i > 0 && keys[i - 1].Out != KeyType.Hold)
        {
            var (s, w) = Curve.Tangent(keys, i, false); double d = (k.T - keys[i - 1].T) * w;
            r.Add((new Point(X(Anim.TimelineTime(c, k.T - d)), GY(k.V - s * d)), false));
        }
        return r;
    }

    static void Poly(DrawingContext dc, List<Point> pts, Pen pen)
    {
        if (pts.Count < 2) return;
        var g = new StreamGeometry();
        using (var x = g.Open()) { x.BeginFigure(pts[0], false, false); x.PolyLineTo(pts.Skip(1).ToList(), true, false); }
        g.Freeze(); dc.DrawGeometry(null, pen, g);
    }

    static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw)) return 1;
        double e = Math.Pow(10, Math.Floor(Math.Log10(raw))), m = raw / e;
        return (m < 1.5 ? 1 : m < 3.5 ? 2 : m < 7.5 ? 5 : 10) * e;
    }
    static FormattedText Txt(string s, double size, Brush b, double maxW = 0)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b, 1.25);
        if (maxW > 0) { ft.MaxTextWidth = Math.Max(10, maxW); ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis; }
        return ft;
    }

    // ---------------- hit testing ----------------
    (Lane lane, long f, double t)? HitKey(Point p)
    {
        if (Clip is not { } c || p.X < ListW || p.Y < RulerH) return null;
        var lanes = Lanes(c);
        if (!graph)
        {
            int row = (int)((p.Y - RulerH) / RowH);
            if (row < 0 || row >= lanes.Count) return null;
            var l = lanes[row];
            foreach (var (t, f, _, _) in KeysOf(c, l).OrderBy(k => Math.Abs(X(k.t) - p.X)))
                return Math.Abs(X(t) - p.X) <= 7 ? (l, f, t) : null;
            return null;
        }
        var gl = lanes.FirstOrDefault(l => l.Id == graphLane);
        if (gl?.P is not { } pp || Anim.Track(c, pp) is not { } tr) return null;
        foreach (var k in tr.Keys)
        {
            var kp = new Point(X(Anim.TimelineTime(c, k.T)), GY(k.V));
            if ((kp - p).Length <= 7) return (gl, Fr(k.T), Anim.TimelineTime(c, k.T));
        }
        return null;
    }

    (Keyframe k, int i, bool outgoing)? HitHandle(Point p)
    {
        if (!graph || Clip is not { } c || graphLane == null || !Enum.TryParse<AnimProp>(graphLane, out var pp) || Anim.Track(c, pp) is not { } tr) return null;
        for (int i = 0; i < tr.Keys.Count; i++)
        {
            if (!sel.Contains((graphLane, Fr(tr.Keys[i].T)))) continue;
            foreach (var (hp, o) in Handles(c, tr.Keys, i)) if ((hp - p).Length <= 6) return (tr.Keys[i], i, o);
        }
        return null;
    }

    // ---------------- mouse ----------------
    enum DragKind { None, Scrub, Keys, Handle, Marquee }
    DragKind drag;
    Point dStart, dNow;
    (double lo, double hi)? vrLock;
    List<(string lane, Keyframe? k, int maskFrame, double T0, double V0)> moving = new();
    (Keyframe k, int i, bool outgoing)? handle;
    bool changed;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (Clip is not { } c) return;
        var p = e.GetPosition(this);
        var lanes = Lanes(c);
        if (e.ChangedButton == MouseButton.Right) { ContextMenuAt(c, p); e.Handled = true; return; }
        if (e.ChangedButton != MouseButton.Left) return;
        if (p.X < ListW)
        {
            int row = (int)((p.Y - RulerH) / RowH);
            if (p.Y >= RulerH && row >= 0 && row < lanes.Count && lanes[row].P != null) { graphLane = lanes[row].Id; InvalidateVisual(); }
            return;
        }
        if (p.Y < RulerH) { drag = DragKind.Scrub; CaptureMouse(); Ed.Seek(Snap(T(p.X))); return; }
        bool add = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.ClickCount == 2) { AddKeyAt(c, lanes, p); return; }
        if (HitHandle(p) is { } hh)
        {
            handle = hh; drag = DragKind.Handle; vrLock = vr; changed = false;
            Ed.Checkpoint(); CaptureMouse(); return;
        }
        if (HitKey(p) is { } hk)
        {
            var id = (hk.lane.Id, hk.f);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && sel.Contains(id)) { sel.Remove(id); InvalidateVisual(); return; }
            if (!sel.Contains(id)) { if (!add) sel.Clear(); sel.Add(id); }
            BeginMove(c, lanes);
            drag = DragKind.Keys; dStart = dNow = p; vrLock = vr; changed = false;
            CaptureMouse(); InvalidateVisual();
            return;
        }
        if (!add) sel.Clear();
        drag = DragKind.Marquee; dStart = dNow = p; CaptureMouse(); InvalidateVisual();
    }

    void BeginMove(Clip c, List<Lane> lanes)
    {
        moving.Clear();
        foreach (var l in lanes)
            foreach (var (t, f, k, mf) in KeysOf(c, l))
                if (sel.Contains((l.Id, f))) moving.Add((l.Id, k, mf, k?.T ?? mf / (double)TimeUtil.Fps, k?.V ?? 0));
        if (moving.Any(m => m.k != null)) Ed.Checkpoint();
        var maskLane = moving.FirstOrDefault(m => m.k == null).lane;
        if (maskLane != null) Ed.MaskBegin(maskLane[5..]);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (drag == DragKind.None) { Cursor = HitHandle(p) != null || HitKey(p) != null ? Cursors.Hand : null; return; }
        if (Clip is not { } c) return;
        dNow = p;
        switch (drag)
        {
            case DragKind.Scrub: Ed.Seek(Snap(T(p.X))); break;
            case DragKind.Marquee: InvalidateVisual(); break;
            case DragKind.Keys:
            {
                double dt = Math.Round((T(p.X) - T(dStart.X)) * TimeUtil.Fps) / TimeUtil.Fps;
                double dv = graph ? GV(p.Y) - GV(dStart.Y) : 0;
                if (graph && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { if (Math.Abs(p.X - dStart.X) > Math.Abs(p.Y - dStart.Y)) dv = 0; else dt = 0; }
                MoveTo(c, dt, dv);
                break;
            }
            case DragKind.Handle when handle is { } hh:
            {
                var keys = Anim.Track(c, Enum.Parse<AnimProp>(graphLane!))!.Keys;
                int i = keys.IndexOf(hh.k); if (i < 0) break;
                double src = Anim.SrcTime(c, T(p.X)), dtS = src - hh.k.T, dvS = GV(p.Y) - hh.k.V;
                bool broken = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);           // Alt: break the tangent (in ≠ out)
                if (hh.outgoing && i < keys.Count - 1 && dtS > 1e-4)
                {
                    double seg = keys[i + 1].T - hh.k.T, slope = dvS / dtS;
                    hh.k.Out = KeyType.Bezier; hh.k.OutSlope = slope; hh.k.OutInfl = Math.Clamp(dtS / seg, 0.02, 1);
                    if (!broken && i > 0) { hh.k.In = KeyType.Bezier; hh.k.InSlope = slope; }
                }
                else if (!hh.outgoing && i > 0 && dtS < -1e-4)
                {
                    double seg = hh.k.T - keys[i - 1].T, slope = dvS / dtS;
                    hh.k.In = KeyType.Bezier; hh.k.InSlope = slope; hh.k.InInfl = Math.Clamp(-dtS / seg, 0.02, 1);
                    if (!broken && i < keys.Count - 1) { hh.k.Out = KeyType.Bezier; hh.k.OutSlope = slope; }
                }
                changed = true; Ed.RaiseProps(); InvalidateVisual();
                break;
            }
        }
    }

    void MoveTo(Clip c, double dt, double dv)
    {
        foreach (var m in moving)
        {
            if (m.k != null)
            {
                m.k.T = m.T0 + dt;
                if (graph) { var info = Anim.Of(Enum.Parse<AnimProp>(m.lane)); m.k.V = Math.Clamp(m.V0 + dv, info.Min, info.Max); }
            }
        }
        foreach (var l in moving.Where(m => m.k != null).Select(m => m.lane).Distinct())
            Anim.Track(c, Enum.Parse<AnimProp>(l))!.Keys.Sort((a, b) => a.T.CompareTo(b.T));
        // mask keys: move the outline to the new frame
        // mask keys (one mask per drag — the undo step is per mask): move the outlines from the previous offset to the new one
        var maskLane = moving.FirstOrDefault(m => m.k == null).lane;
        var mm = moving.Where(m => m.k == null && m.lane == maskLane).ToList();
        int df = (int)Math.Round(dt * TimeUtil.Fps);
        if (mm.Count > 0 && df != lastDf && Ed.MaskTracks.TryGetValue(maskLane![5..], out var mt))
        {
            var outlines = mm.Select(m => (m.maskFrame, poly: mt.Frames.GetValueOrDefault(m.maskFrame + lastDf))).ToList();
            foreach (var (f0, _) in outlines) if (mt.IsKey(f0 + lastDf)) mt.Remove(f0 + lastDf);
            foreach (var (f0, poly) in outlines) if (poly != null) mt.Set(f0 + df, poly, key: true);
            lastDf = df;
            Ed.RaiseMasks();
        }
        moving.RemoveAll(m => m.k == null && m.lane != maskLane);
        // selection follows
        sel.Clear();
        foreach (var m in moving) sel.Add((m.lane, m.k != null ? Fr(m.k.T) : m.maskFrame + (long)Math.Round(dt * TimeUtil.Fps)));
        changed = true;
        Ed.RaiseProps(); InvalidateVisual();
    }
    int lastDf;

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (drag == DragKind.None) return;
        ReleaseMouseCapture();
        var c = Clip;
        if (drag == DragKind.Marquee && c != null)
        {
            var r = new Rect(dStart, dNow);
            if (r.Width < 3 && r.Height < 3) { if (sel.Count == 0) Ed.Seek(Snap(T(dStart.X))); }
            else
            {
                var lanes = Lanes(c);
                for (int i = 0; i < lanes.Count; i++)
                    foreach (var (t, f, k, _) in KeysOf(c, lanes[i]))
                    {
                        var pt = graph ? (lanes[i].Id == graphLane && k != null ? new Point(X(t), GY(k.V)) : new Point(-99, -99)) : new Point(X(t), RulerH + i * RowH + RowH / 2);
                        if (r.Contains(pt)) sel.Add((lanes[i].Id, f));
                    }
            }
        }
        if (drag is DragKind.Keys or DragKind.Handle && c != null)
        {
            // overwrite: an unselected key landing on a moved key's frame goes
            foreach (var l in moving.Where(m => m.k != null).Select(m => m.lane).Distinct())
            {
                var keys = Anim.Track(c, Enum.Parse<AnimProp>(l))!.Keys;
                var movedKeys = moving.Where(m => m.lane == l).Select(m => m.k).ToHashSet();
                keys.RemoveAll(k => !movedKeys.Contains(k) && keys.Any(o => movedKeys.Contains(o) && Math.Abs(o.T - k.T) < 0.5 / TimeUtil.Fps));
            }
            if (moving.Any(m => m.k != null) || drag == DragKind.Handle) Ed.Commit();
            if (moving.Any(m => m.k == null)) Ed.MaskEnd();
            if (changed) Edited?.Invoke();
        }
        drag = DragKind.None; moving.Clear(); handle = null; vrLock = null; lastDf = 0;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double span = t1 - t0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double at = T(e.GetPosition(this).X), f = e.Delta > 0 ? 0.8 : 1.25;
            t0 = at - (at - t0) * f; t1 = at + (t1 - at) * f;
        }
        else { double d = -Math.Sign(e.Delta) * span * 0.1; t0 += d; t1 += d; }
        InvalidateVisual(); e.Handled = true;
    }

    double Snap(double t)
    {
        t = Math.Round(t * TimeUtil.Fps) / TimeUtil.Fps;
        return Math.Max(0, t);
    }

    // ---------------- commands ----------------
    void AddKeyAt(Clip c, List<Lane> lanes, Point p)
    {
        double t = Math.Clamp(Snap(T(p.X)), c.Start, c.End - 1.0 / TimeUtil.Fps), src = Anim.SrcTime(c, t);
        Lane? l = graph ? lanes.FirstOrDefault(x => x.Id == graphLane) : lanes.ElementAtOrDefault((int)((p.Y - RulerH) / RowH));
        if (l == null) return;
        if (l.P is { } prop)
        {
            double v = graph ? GV(p.Y) : Anim.Keyed(c, prop, src);
            Ed.Edit(() => Anim.SetKey(c, prop, src, v));
            sel.Clear(); sel.Add((l.Id, Fr(src)));
        }
        else if (l.MaskId != null) Ed.ToggleKey(l.MaskId, (int)Fr(src));
        Edited?.Invoke(); InvalidateVisual();
    }

    public int SelectedCount => sel.Count;
    /// <summary>Show a property lane (graph mode).</summary>
    public void ShowLane(string lane) { graphLane = lane; InvalidateVisual(); }

    public void DeleteSelected()
    {
        if (Clip is not { } c || sel.Count == 0) return;
        var byMask = sel.Where(s => s.lane.StartsWith("mask:")).GroupBy(s => s.lane[5..]).ToList();
        var props = sel.Where(s => !s.lane.StartsWith("mask:")).ToList();
        if (props.Count > 0)
            Ed.Edit(() =>
            {
                foreach (var (lane, f) in props)
                    Anim.Track(c, Enum.Parse<AnimProp>(lane))?.Keys.RemoveAll(k => Fr(k.T) == f);
                Anim.Prune(c);
            });
        foreach (var g in byMask)
            Ed.MaskEdit(g.Key, tr => { foreach (var (_, f) in g) if (tr.Frames.Count > 1) tr.Remove((int)f); });
        sel.Clear(); Edited?.Invoke(); InvalidateVisual();
    }

    public void ApplyInterp(string preset)
    {
        if (Clip is not { } c) return;
        var keys = sel.Where(s => !s.lane.StartsWith("mask:")).ToList();
        if (keys.Count == 0) return;
        Ed.Edit(() =>
        {
            foreach (var (lane, f) in keys)
                if (Anim.Track(c, Enum.Parse<AnimProp>(lane))?.Keys.FirstOrDefault(k => Fr(k.T) == f) is { } k) Anim.ApplyInterp(k, preset);
        });
        Edited?.Invoke(); InvalidateVisual();
    }

    static List<(AnimProp p, double dt, Keyframe k)>? clipboard;
    public void Copy()
    {
        if (Clip is not { } c) return;
        var list = new List<(AnimProp, double, Keyframe)>();
        foreach (var (lane, f) in sel.Where(s => !s.lane.StartsWith("mask:")))
            if (Anim.Track(c, Enum.Parse<AnimProp>(lane))?.Keys.FirstOrDefault(k => Fr(k.T) == f) is { } k) list.Add((Enum.Parse<AnimProp>(lane), k.T, k.Clone()));
        if (list.Count == 0) return;
        double first = list.Min(x => x.Item2);
        clipboard = list.Select(x => (x.Item1, x.Item2 - first, x.Item3)).ToList();
    }
    public void Paste()
    {
        if (Clip is not { } c || clipboard == null) return;
        double at = Anim.SrcTime(c, Math.Clamp(Ed.Time, c.Start, c.End));
        Ed.Edit(() =>
        {
            foreach (var (p, dt, k) in clipboard)
            {
                if (!Anim.PropsOf(c).Contains(p)) continue;
                var nk = Anim.SetKey(c, p, at + dt, k.V);
                nk.In = k.In; nk.Out = k.Out; nk.InSlope = k.InSlope; nk.InInfl = k.InInfl; nk.OutSlope = k.OutSlope; nk.OutInfl = k.OutInfl;
            }
        });
        Edited?.Invoke(); InvalidateVisual();
    }
    public void SelectAll()
    {
        if (Clip is not { } c) return;
        foreach (var l in Lanes(c)) foreach (var (_, f, _, _) in KeysOf(c, l)) sel.Add((l.Id, f));
        InvalidateVisual();
    }

    /// <summary>Keyboard (when the panel has focus): Delete, Ctrl+C / Ctrl+V / Ctrl+A, F = fit.</summary>
    public bool HandleKey(Key key, bool ctrl)
    {
        switch (key)
        {
            case Key.Delete or Key.Back when sel.Count > 0: DeleteSelected(); return true;
            case Key.C when ctrl: Copy(); return true;
            case Key.V when ctrl: Paste(); return true;
            case Key.A when ctrl: SelectAll(); return true;
            case Key.F when !ctrl: Fit(); return true;
        }
        return false;
    }

    void ContextMenuAt(Clip c, Point p)
    {
        if (HitKey(p) is { } hk && !sel.Contains((hk.lane.Id, hk.f))) { sel.Clear(); sel.Add((hk.lane.Id, hk.f)); InvalidateVisual(); }
        var menu = new ContextMenu { PlacementTarget = this };
        MenuItem Item(string text, Action a, bool enabled = true) { var mi = new MenuItem { Header = text, IsEnabled = enabled }; mi.Click += (_, _) => a(); menu.Items.Add(mi); return mi; }
        bool anyProp = sel.Any(s => !s.lane.StartsWith("mask:"));
        foreach (var preset in Anim.InterpPresets) Item(preset, () => ApplyInterp(preset), anyProp);
        menu.Items.Add(new Separator());
        Item("Copy", Copy, anyProp); Item("Paste at playhead", Paste, clipboard != null);
        Item("Delete", DeleteSelected, sel.Count > 0);
        menu.Items.Add(new Separator());
        Item("Select all", SelectAll); Item("Fit view (F)", Fit);
        menu.IsOpen = true;
    }
}
