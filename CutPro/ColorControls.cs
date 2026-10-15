using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>A Lumetri colour wheel: drag the puck towards a hue (distance = amount), double-click to reset.</summary>
public sealed class ColorWheelControl : MixerElement
{
    double hue, amount;
    public event Action<double, double>? Moved;       // hue, amount
    public event Action? DragStarted, DragEnded;
    static BitmapSource? disc;
    public ColorWheelControl() { Width = 78; Height = 78; Cursor = Cursors.Cross; ToolTip = "Drag towards a colour · double-click: reset"; }
    public void Set(double h, double a) { hue = h; amount = a; InvalidateVisual(); }

    /// <summary>The hue disc (colour at the rim, white at the centre), drawn once.</summary>
    static BitmapSource Disc()
    {
        if (disc != null) return disc;
        const int S = 128; var px = new byte[S * S * 4];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                double dx = (x + 0.5 - S / 2.0) / (S / 2.0), dy = (y + 0.5 - S / 2.0) / (S / 2.0), r = Math.Sqrt(dx * dx + dy * dy);
                int o = (y * S + x) * 4;
                if (r > 1) continue;
                double h = Math.Atan2(-dy, dx) * 180 / Math.PI; var (cr, cg, cb) = Grade.HueRgb(h);
                double s = Math.Min(1, r) * 0.85, a = Math.Clamp((1 - r) * S / 2, 0, 1);
                px[o + 2] = (byte)(255 * (1 - s + s * cr)); px[o + 1] = (byte)(255 * (1 - s + s * cg)); px[o] = (byte)(255 * (1 - s + s * cb)); px[o + 3] = (byte)(255 * a);
                for (int k = 0; k < 3; k++) px[o + k] = (byte)(px[o + k] * a);      // premultiplied
            }
        var wb = new WriteableBitmap(S, S, 96, 96, PixelFormats.Pbgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, S, S), px, S * 4, 0); wb.Freeze();
        return disc = wb;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight), r = s / 2 - 2, cx = ActualWidth / 2, cy = ActualHeight / 2;
        dc.DrawEllipse(T("Input.Bg"), new Pen(T("Input.Border"), 1), new Point(cx, cy), r + 1.5, r + 1.5);
        dc.PushOpacity(0.85); dc.DrawImage(Disc(), new Rect(cx - r, cy - r, 2 * r, 2 * r)); dc.Pop();
        var dim = new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 1);
        dc.DrawLine(dim, new Point(cx - r, cy), new Point(cx + r, cy)); dc.DrawLine(dim, new Point(cx, cy - r), new Point(cx, cy + r));
        double a = hue * Math.PI / 180, d = Math.Clamp(amount / 100, 0, 1) * r;
        var p = new Point(cx + Math.Cos(a) * d, cy - Math.Sin(a) * d);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 2.5), p, 5, 5);
        dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), p, 5, 5);
    }

    bool dragging;
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { DragStarted?.Invoke(); hue = 0; amount = 0; InvalidateVisual(); Moved?.Invoke(0, 0); DragEnded?.Invoke(); e.Handled = true; return; }
        dragging = true; CaptureMouse(); DragStarted?.Invoke(); MoveTo(e.GetPosition(this)); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e) { if (dragging) MoveTo(e.GetPosition(this)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (!dragging) return; dragging = false; ReleaseMouseCapture(); DragEnded?.Invoke(); e.Handled = true; }
    void MoveTo(Point p)
    {
        double r = Math.Min(ActualWidth, ActualHeight) / 2 - 2, dx = p.X - ActualWidth / 2, dy = ActualHeight / 2 - p.Y;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { dx *= 0.3; dy *= 0.3; }
        double dist = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / r);
        hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360; amount = Math.Round(dist * 100, 1);
        InvalidateVisual(); Moved?.Invoke(hue, amount);
    }
}

/// <summary>RGB curves (master, red, green, blue): click to add a point, drag it, double-click a point to remove it.</summary>
public sealed class CurveEditor : MixerElement
{
    double[]?[] curves = new double[4][];
    int channel;
    public event Action<int, double[]?>? Changed;     // channel (0 master, 1 R, 2 G, 3 B), points (null = straight)
    public event Action? DragStarted, DragEnded;
    public int Channel { get => channel; set { channel = value; InvalidateVisual(); } }
    public CurveEditor() { Height = 170; Cursor = Cursors.Cross; ToolTip = "Click: add a point · drag · double-click a point: remove"; }
    public void Set(double[]? m, double[]? r, double[]? g, double[]? b) { curves = new[] { m, r, g, b }; InvalidateVisual(); }

    Rect Box => new(6, 6, Math.Max(10, ActualWidth - 12), Math.Max(10, ActualHeight - 12));
    Point ToScreen(double x, double y) { var b = Box; return new Point(b.X + x * b.Width, b.Bottom - y * b.Height); }
    (double x, double y) FromScreen(Point p) { var b = Box; return (Math.Clamp((p.X - b.X) / b.Width, 0, 1), Math.Clamp((b.Bottom - p.Y) / b.Height, 0, 1)); }
    static readonly Color[] Ink = { Colors.WhiteSmoke, Color.FromRgb(240, 80, 80), Color.FromRgb(90, 210, 90), Color.FromRgb(90, 140, 255) };
    List<(double x, double y)> Pts(int ch)
    {
        var c = curves[ch];
        if (c == null) return new() { (0, 0), (1, 1) };
        var l = new List<(double, double)>(); for (int i = 0; i + 1 < c.Length; i += 2) l.Add((c[i], c[i + 1])); return l;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var b = Box;
        dc.DrawRectangle(T("Meter.Bg"), new Pen(T("Input.Border"), 1), b);
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
        for (int i = 1; i < 4; i++) { double x = b.X + b.Width * i / 4, y = b.Y + b.Height * i / 4; dc.DrawLine(grid, new Point(x, b.Y), new Point(x, b.Bottom)); dc.DrawLine(grid, new Point(b.X, y), new Point(b.Right, y)); }
        dc.DrawLine(grid, ToScreen(0, 0), ToScreen(1, 1));
        for (int ch = 0; ch < 4; ch++)
        {
            if (ch != channel && curves[ch] == null) continue;
            var f = Grade.Spline(curves[ch]) ?? (x => x);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(ToScreen(0, f(0)), false, false);
                for (int i = 1; i <= 64; i++) { double x = i / 64.0; ctx.LineTo(ToScreen(x, f(x)), true, false); }
            }
            g.Freeze();
            var col = Ink[ch]; if (ch != channel) col.A = 90;
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(col), ch == channel ? 2 : 1), g);
        }
        foreach (var (x, y) in Pts(channel)) dc.DrawEllipse(new SolidColorBrush(Ink[channel]), new Pen(Brushes.Black, 1), ToScreen(x, y), 4, 4);
    }

    int drag = -1;
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this); var pts = Pts(channel);
        int hit = pts.FindIndex(q => (ToScreen(q.x, q.y) - p).Length < 8);
        if (e.ClickCount == 2 && hit > 0 && hit < pts.Count - 1) { DragStarted?.Invoke(); pts.RemoveAt(hit); Commit(pts); DragEnded?.Invoke(); e.Handled = true; return; }
        DragStarted?.Invoke();
        if (hit < 0) { var (x, y) = FromScreen(p); pts.Add((x, y)); pts.Sort((a, c) => a.x.CompareTo(c.x)); hit = pts.FindIndex(q => q.x == x && q.y == y); Commit(pts); }
        drag = hit; CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (drag < 0) return;
        var pts = Pts(channel); if (drag >= pts.Count) return;
        var (x, y) = FromScreen(e.GetPosition(this));
        double lo = drag == 0 ? 0 : pts[drag - 1].x + 0.01, hi = drag == pts.Count - 1 ? 1 : pts[drag + 1].x - 0.01;
        if (drag == 0) x = Math.Min(x, hi); else if (drag == pts.Count - 1) x = Math.Max(x, lo); else x = Math.Clamp(x, lo, hi);
        pts[drag] = (x, y); Commit(pts);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (drag < 0) return; drag = -1; ReleaseMouseCapture(); DragEnded?.Invoke(); e.Handled = true; }
    void Commit(List<(double x, double y)> pts)
    {
        bool straight = pts.Count == 2 && pts[0] == (0, 0) && pts[1] == (1, 1);
        var arr = straight ? null : pts.SelectMany(q => new[] { Math.Round(q.x, 4), Math.Round(q.y, 4) }).ToArray();
        curves[channel] = arr; InvalidateVisual(); Changed?.Invoke(channel, arr);
    }
}
