using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>Shared drawing helpers of the mixer controls (theme brushes are read through the element: per-card palettes).</summary>
public abstract class MixerElement : FrameworkElement
{
    protected MixerElement() { ThemeManager.Changed += InvalidateVisual; }
    protected Brush T(string key) => ThemeManager.Brush(this, key);
    protected FormattedText Txt(string s, double size, Brush b, bool bold = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    public static string FormatDb(double db) => db <= Mixer.MinDb ? "−∞" : (db >= 0.05 ? "+" : "") + db.ToString("0.0", CultureInfo.InvariantCulture).Replace('-', '−');
}

/// <summary>Vertical volume fader (dB) with a scale, in the theme's slider look (vine / glass tube / carved groove / slot + bird / bead / gem / knob).</summary>
public sealed class Fader : MixerElement
{
    double db;
    public double Db { get => db; set { if (value == db) return; db = value; InvalidateVisual(); } }
    public double MaxDb { get; set; } = Mixer.MaxDb;
    /// <summary>The user moved it (new value in dB).</summary>
    public event Action<double>? Moved;
    public event Action? DragStarted, DragEnded;
    public bool Dragging { get; private set; }
    const double ScaleW = 24, Pad = 12;

    public Fader() { Focusable = false; Cursor = Cursors.Hand; ToolTip = "Drag: volume · Ctrl+drag: fine · double-click: 0 dB · wheel: ±0.5 dB"; }

    // taper: −∞ at the bottom, −60…−24 dB over the next quarter, −24…max over the top 70 % (0 dB sits about 86 % up)
    public double ToPos(double v)
    {
        if (v <= Mixer.MinDb) return 0;
        if (v < -60) return 0.05 * (v - Mixer.MinDb) / (-60 - Mixer.MinDb);
        if (v < -24) return 0.05 + 0.25 * (v + 60) / 36;
        return 0.3 + 0.7 * Math.Min(1, (v + 24) / (MaxDb + 24));
    }
    public double ToDb(double p)
    {
        p = Math.Clamp(p, 0, 1);
        if (p <= 0.002) return Mixer.MinDb;
        if (p < 0.05) return Mixer.MinDb + p / 0.05 * (-60 - Mixer.MinDb);
        if (p < 0.3) return -60 + (p - 0.05) / 0.25 * 36;
        return -24 + (p - 0.3) / 0.7 * (MaxDb + 24);
    }
    double Y(double p) => ActualHeight - Pad - p * Math.Max(1, ActualHeight - 2 * Pad);
    double P(double y) => (ActualHeight - Pad - y) / Math.Max(1, ActualHeight - 2 * Pad);
    double TrackX => ScaleW + (ActualWidth - ScaleW) / 2;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight; if (h < 2 * Pad + 10) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // whole area takes the mouse
        var dim = T("Fg.Dim");
        foreach (var m in new double[] { 6, 0, -6, -12, -18, -24, -36, -48, -96 })
        {
            if (m > MaxDb) continue;
            double y = Y(ToPos(m));
            var ft = Txt(m <= Mixer.MinDb ? "−∞" : (m > 0 ? "+" : "") + m.ToString(CultureInfo.InvariantCulture).Replace('-', '−'), 8.5, dim, m == 0);
            dc.DrawText(ft, new Point(ScaleW - 4 - ft.Width, y - ft.Height / 2));
            dc.DrawRectangle(dim, null, new Rect(ScaleW - 2, y - 0.5, m == 0 ? 6 : 3, 1));
        }
        // the track (the theme's slider brushes are drawn along x, so rotate them upright)
        double th = TryFindResource("Th.TrackH") is double d ? d : 3, x = TrackX, len = h - 2 * Pad, cy = h / 2, p = ToPos(db);
        dc.PushTransform(new RotateTransform(-90, x, cy));
        var r = new Rect(x - len / 2, cy - th / 2, len, th);
        dc.DrawRoundedRectangle(TryFindResource("B.Th.Track") as Brush ?? T("Slider.Track"), null, r, 1.5, 1.5);
        dc.DrawRoundedRectangle(TryFindResource("B.Th.TrackFill") as Brush ?? T("Accent"), null, new Rect(r.X, r.Y, len * p, th), 1.5, 1.5);
        dc.Pop();
        // the cap: theme art (bird, bead, gem, knob) or a classic fader cap
        double ty = Y(p);
        if (TryFindResource("Th.ThumbArt") is ImageSource art)
        {
            double aw = TryFindResource("Th.ThumbW") is double tw ? tw : 18, ah = TryFindResource("Th.ThumbH") is double thh ? thh : 18;
            dc.DrawImage(art, new Rect(x - aw / 2, ty - ah / 2, aw, ah));
        }
        else
        {
            var cap = new Rect(x - 12, ty - 7, 24, 14);
            dc.DrawRoundedRectangle(T("Slider.Thumb"), new Pen(T("Input.Border"), 1), cap, 3, 3);
            dc.DrawRectangle(T("Accent"), null, new Rect(cap.X + 4, ty - 1, cap.Width - 8, 2));
        }
    }

    double grabOff;
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { DragStarted?.Invoke(); Set(0); DragEnded?.Invoke(); e.Handled = true; return; }
        var pt = e.GetPosition(this);
        double ty = Y(ToPos(db));
        grabOff = Math.Abs(pt.Y - ty) <= 10 ? pt.Y - ty : 0;      // on the cap: keep the grab point; elsewhere: jump there
        DragStarted?.Invoke(); Dragging = true; CaptureMouse();
        if (grabOff == 0) Set(ToDb(P(pt.Y)));
        lastY = pt.Y; e.Handled = true;
    }
    double lastY;
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!Dragging) return;
        var pt = e.GetPosition(this);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Set(db - (pt.Y - lastY) * 0.05);          // fine: 0.05 dB per pixel
        else Set(ToDb(P(pt.Y - grabOff)));
        lastY = pt.Y;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!Dragging) return;
        Dragging = false; ReleaseMouseCapture(); DragEnded?.Invoke(); e.Handled = true;
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.5;
        Set((db <= Mixer.MinDb ? -60 : db) + Math.Sign(e.Delta) * step); e.Handled = true;
    }
    void Set(double v)
    {
        v = v <= Mixer.MinDb + 0.01 ? Mixer.MinDb : Math.Clamp(Math.Round(v, 1), Mixer.MinDb, MaxDb);
        if (v == db) return;
        Db = v; Moved?.Invoke(v);
    }
}

/// <summary>Pan / balance knob (−100 left … 100 right).</summary>
public sealed class PanKnob : MixerElement
{
    double val;
    public double Value { get => val; set { if (value == val) return; val = value; InvalidateVisual(); } }
    public event Action<double>? Moved;
    public event Action? DragStarted, DragEnded;
    public bool Dragging { get; private set; }
    public PanKnob() { Width = 34; Height = 34; Cursor = Cursors.SizeWE; ToolTip = "Pan: drag left / right · double-click: centre · wheel: ±5"; }
    public static string Label(double v) => Math.Abs(v) < 0.5 ? "C" : (v < 0 ? "L " : "R ") + Math.Round(Math.Abs(v)).ToString(CultureInfo.InvariantCulture);

    protected override void OnRender(DrawingContext dc)
    {
        double r = Math.Min(ActualWidth, ActualHeight) / 2 - 2, cx = ActualWidth / 2, cy = ActualHeight / 2;
        dc.DrawEllipse(T("Button.Bg"), new Pen(T("Input.Border"), 1), new Point(cx, cy), r, r);
        // arc from the top (centre) to the value
        double a = val / 100 * 135, ar = r + 0.5;
        if (Math.Abs(a) > 1)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                Point Pt(double deg) => new(cx + ar * Math.Sin(deg * Math.PI / 180), cy - ar * Math.Cos(deg * Math.PI / 180));
                c.BeginFigure(Pt(0), false, false);
                c.ArcTo(Pt(a), new Size(ar, ar), 0, Math.Abs(a) > 180, a > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(T("Accent"), 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
        }
        double s = Math.Sin(a * Math.PI / 180), co = Math.Cos(a * Math.PI / 180);
        dc.DrawLine(new Pen(T("Fg"), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                    new Point(cx + s * r * 0.25, cy - co * r * 0.25), new Point(cx + s * r * 0.8, cy - co * r * 0.8));
    }

    Point last;
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { DragStarted?.Invoke(); Set(0); DragEnded?.Invoke(); e.Handled = true; return; }
        last = e.GetPosition(this); Dragging = true; CaptureMouse(); DragStarted?.Invoke(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!Dragging) return;
        var p = e.GetPosition(this);
        double k = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0.2 : 1;
        Set(val + ((p.X - last.X) - (p.Y - last.Y)) * k);
        last = p;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!Dragging) return;
        Dragging = false; ReleaseMouseCapture(); DragEnded?.Invoke(); e.Handled = true;
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { Set(Math.Round(val / 5) * 5 + Math.Sign(e.Delta) * 5); e.Handled = true; }
    void Set(double v)
    {
        v = Math.Clamp(Math.Round(v), -100, 100);
        if (v == val) return;
        Value = v; Moved?.Invoke(v);
    }
}

/// <summary>Stereo peak meter (−60 … 0 dBFS): green / yellow above −12 / red above −3, falling 20 dB/s, peak hold 1.5 s, clip light (click to reset).</summary>
public sealed class LevelMeter : MixerElement
{
    const double Floor = -60, Fall = 20, Hold = 1.5, Top = 8;
    readonly double[] shown = { Floor, Floor }, held = { Floor, Floor }, heldAge = { 0, 0 };
    bool clipped;
    Brush? bg, low, mid, high, scale;
    public LevelMeter()
    {
        Width = 15; ToolTip = "Peak level (dBFS). The light at the top shows clipping — click it to reset.";
        ThemeManager.Changed += () => bg = null;
    }

    /// <summary>New peaks (linear, 1 = 0 dBFS) after dt seconds; redraws only when something visibly changed.</summary>
    public void Push(double l, double r, double dt)
    {
        bool dirty = false;
        for (int ch = 0; ch < 2; ch++)
        {
            double v = ch == 0 ? l : r, d = v > 0 ? Math.Max(Floor, 20 * Math.Log10(v)) : Floor;
            if (v >= 1) { if (!clipped) dirty = true; clipped = true; }
            double s = Math.Max(d, shown[ch] - Fall * dt);
            if (Math.Abs(s - shown[ch]) > 0.2) { shown[ch] = s; dirty = true; }
            else if (s == Floor && shown[ch] != Floor) { shown[ch] = Floor; dirty = true; }
            heldAge[ch] += dt;
            if (d >= held[ch]) { if (Math.Abs(d - held[ch]) > 0.2) dirty = true; held[ch] = d; heldAge[ch] = 0; }
            else if (heldAge[ch] > Hold && held[ch] > Floor) { held[ch] = Math.Max(Floor, held[ch] - Fall * dt); dirty = true; }
        }
        if (dirty) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (bg == null) { bg = T("Meter.Bg"); low = T("Meter.Low"); mid = T("Meter.Mid"); high = T("Meter.High"); scale = T("Fg.Dim"); }
        double w = ActualWidth, h = ActualHeight, bh = h - Top - 12;   // Fader's Pad = 12 at the bottom, so −∞ lines up
        if (bh < 10) return;
        double bw = (w - 3) / 2;
        dc.DrawRoundedRectangle(clipped ? high : bg, null, new Rect(0, 0, w, Top - 2), 1.5, 1.5);
        double Y(double d) => Top + bh * (1 - (d - Floor) / -Floor);
        for (int ch = 0; ch < 2; ch++)
        {
            double x = 1 + ch * (bw + 1);
            dc.DrawRectangle(bg, null, new Rect(x - 1, Top - 1, bw + 2, bh + 2));
            double v = shown[ch];
            if (v > Floor)
            {
                void Seg(double a, double b, Brush br) { double lo = Math.Max(a, Floor), hi = Math.Min(b, v); if (hi > lo) dc.DrawRectangle(br, null, new Rect(x, Y(hi), bw, Y(lo) - Y(hi))); }
                Seg(Floor, -12, low!); Seg(-12, -3, mid!); Seg(-3, 0, high!);
            }
            if (held[ch] > Floor) dc.DrawRectangle(held[ch] > -3 ? high : held[ch] > -12 ? mid : low, null, new Rect(x, Y(held[ch]) - 1, bw, 1.5));
        }
        foreach (var m in new double[] { -6, -12, -24, -48 }) dc.DrawRectangle(scale, null, new Rect(0, Y(m) - 0.5, 2, 1));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { if (clipped) { clipped = false; InvalidateVisual(); } e.Handled = true; }
    public void Reset() { shown[0] = shown[1] = held[0] = held[1] = Floor; clipped = false; InvalidateVisual(); }
}
