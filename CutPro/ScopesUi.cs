using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

public enum ScopeKind { Waveform, Parade, Vectorscope, Histogram }

/// <summary>One scope: its picture (computed off the UI thread from the program frame) with the graticule drawn over it.</summary>
public sealed class ScopeView : MixerElement
{
    public readonly ScopeKind Kind;
    public readonly WriteableBitmap Bmp;
    public const int W = 256, H = 256, HistH = 128;
    public ScopeView(ScopeKind k)
    {
        Kind = k;
        Bmp = new WriteableBitmap(k == ScopeKind.Parade ? W * 3 / 2 : W, k == ScopeKind.Histogram ? HistH : H, 96, 96, PixelFormats.Bgra32, null);
        Height = k == ScopeKind.Histogram ? 110 : k == ScopeKind.Vectorscope ? 230 : 170;
        Margin = new Thickness(0, 2, 0, 8);
        ToolTip = k switch
        {
            ScopeKind.Waveform => "Waveform: brightness (0–100 IRE) of every column of the picture",
            ScopeKind.Parade => "RGB parade: the waveform of red, green and blue side by side",
            ScopeKind.Vectorscope => "Vectorscope: hue (angle) and saturation (distance); the line is the skin-tone line",
            _ => "Histogram: how many pixels at each brightness (red, green, blue, and white = luma)",
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var r = Kind == ScopeKind.Vectorscope ? new Rect((w - Math.Min(w, h)) / 2, 0, Math.Min(w, h), Math.Min(w, h)) : new Rect(0, 0, w, h);
        dc.DrawRectangle(T("Meter.Bg"), null, r);
        dc.DrawImage(Bmp, r);
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(70, 200, 200, 200)), 1); grid.Freeze();
        var dim = T("Fg.Dim");
        switch (Kind)
        {
            case ScopeKind.Waveform:
            case ScopeKind.Parade:
                foreach (int ire in new[] { 0, 25, 50, 75, 100 })
                {
                    double y = r.Bottom - ire / 100.0 * r.Height;
                    dc.DrawLine(grid, new Point(r.Left, y), new Point(r.Right, y));
                    dc.DrawText(Txt(ire.ToString(CultureInfo.InvariantCulture), 8, dim), new Point(r.Left + 2, Math.Max(r.Top, y - 11)));
                }
                if (Kind == ScopeKind.Parade) for (int i = 1; i < 3; i++) dc.DrawLine(grid, new Point(r.Left + r.Width * i / 3, r.Top), new Point(r.Left + r.Width * i / 3, r.Bottom));
                break;
            case ScopeKind.Vectorscope:
            {
                var c = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2); double rad = r.Width / 2 - 2;
                dc.DrawEllipse(null, grid, c, rad, rad); dc.DrawEllipse(null, grid, c, rad * 0.75, rad * 0.75);
                dc.DrawLine(grid, new Point(c.X - rad, c.Y), new Point(c.X + rad, c.Y)); dc.DrawLine(grid, new Point(c.X, c.Y - rad), new Point(c.X, c.Y + rad));
                // skin-tone line (≈ 123°) and the 75 % colour targets
                double a = 123 * Math.PI / 180;
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(120, 240, 200, 160)), 1), c, new Point(c.X + Math.Cos(a) * rad, c.Y - Math.Sin(a) * rad));
                foreach (var (name, cr, cg, cb) in new[] { ("R", 0.75, 0.0, 0.0), ("Mg", 0.75, 0.0, 0.75), ("B", 0.0, 0.0, 0.75), ("Cy", 0.0, 0.75, 0.75), ("G", 0.0, 0.75, 0.0), ("Yl", 0.75, 0.75, 0.0) })
                {
                    var (u, v) = Scopes.UV(cr, cg, cb);
                    var p = new Point(c.X + u * rad / Scopes.UVScale, c.Y - v * rad / Scopes.UVScale);
                    dc.DrawRectangle(null, grid, new Rect(p.X - 4, p.Y - 4, 8, 8));
                    dc.DrawText(Txt(name, 8, dim), new Point(p.X + 5, p.Y - 6));
                }
                break;
            }
            default:
                for (int i = 1; i < 4; i++) dc.DrawLine(grid, new Point(r.Left + r.Width * i / 4, r.Top), new Point(r.Left + r.Width * i / 4, r.Bottom));
                break;
        }
    }
}

/// <summary>Scope maths: from BGRA pixels of the program frame to the scope pictures (BT.709).</summary>
public static class Scopes
{
    public const double UVScale = 0.6;      // vectorscope radius = |u, v| of 0.6 (100 % colours reach about the rim)
    public static (double u, double v) UV(double r, double g, double b)
    {
        double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return ((b - y) / 1.8556, (r - y) / 1.5748);
    }

    /// <summary>Computes every requested scope into BGRA buffers (sizes as the views' bitmaps).</summary>
    public static Dictionary<ScopeKind, byte[]> Compute(byte[] px, int w, int h, IEnumerable<ScopeKind> kinds)
    {
        var res = new Dictionary<ScopeKind, byte[]>();
        foreach (var k in kinds)
        {
            int ow = k == ScopeKind.Parade ? ScopeView.W * 3 / 2 : ScopeView.W, oh = k == ScopeKind.Histogram ? ScopeView.HistH : ScopeView.H;
            var outp = new byte[ow * oh * 4];
            switch (k)
            {
                case ScopeKind.Waveform: Wave(px, w, h, outp, ow, oh, 0, ow, -1); break;
                case ScopeKind.Parade: { int third = ow / 3; for (int ch = 0; ch < 3; ch++) Wave(px, w, h, outp, ow, oh, ch * third, third, 2 - ch); break; }
                case ScopeKind.Vectorscope: Vector(px, w, h, outp, ow, oh); break;
                default: Histogram(px, w, h, outp, ow, oh); break;
            }
            res[k] = outp;
        }
        return res;
    }

    /// <summary>Waveform of a channel (bgr index; −1 = luma) into columns [x0, x0 + cw).</summary>
    static void Wave(byte[] px, int w, int h, byte[] o, int ow, int oh, int x0, int cw, int ch)
    {
        var cnt = new int[cw * oh];
        for (int y = 0; y < h; y += 2)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                double v = ch < 0 ? (0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2]) : px[i + ch];
                int col = x * cw / w, row = oh - 1 - (int)(v / 255.0 * (oh - 1));
                cnt[row * cw + col]++;
            }
        int max = Math.Max(1, cnt.Max());
        double scale = 255 / Math.Log(1 + max * 0.25);
        byte cr = ch == 2 ? (byte)255 : ch < 0 ? (byte)200 : (byte)60, cg = ch == 1 ? (byte)255 : ch < 0 ? (byte)255 : (byte)60, cb = ch == 0 ? (byte)255 : ch < 0 ? (byte)200 : (byte)60;
        for (int r = 0; r < oh; r++)
            for (int c = 0; c < cw; c++)
            {
                int n = cnt[r * cw + c]; if (n == 0) continue;
                double a = Math.Min(255, Math.Log(1 + n) * scale * 1.4);
                int j = (r * ow + x0 + c) * 4;
                o[j] = cb; o[j + 1] = cg; o[j + 2] = cr; o[j + 3] = (byte)a;
            }
    }

    static void Vector(byte[] px, int w, int h, byte[] o, int ow, int oh)
    {
        var cnt = new int[ow * oh];
        for (int y = 0; y < h; y += 2)
            for (int x = 0; x < w; x += 2)
            {
                int i = (y * w + x) * 4;
                var (u, v) = UV(px[i + 2] / 255.0, px[i + 1] / 255.0, px[i] / 255.0);
                int cx = ow / 2 + (int)(u / UVScale * (ow / 2 - 2)), cy = oh / 2 - (int)(v / UVScale * (oh / 2 - 2));
                if (cx >= 0 && cx < ow && cy >= 0 && cy < oh) cnt[cy * ow + cx]++;
            }
        int max = Math.Max(1, cnt.Max()); double scale = 255 / Math.Log(1 + max * 0.25);
        for (int j = 0; j < cnt.Length; j++)
        {
            if (cnt[j] == 0) continue;
            int cx = j % ow, cy = j / ow;
            // tint each point with its own hue (light), like Lumetri
            double u = (cx - ow / 2.0) / (ow / 2.0 - 2) * UVScale, v = (oh / 2.0 - cy) / (oh / 2.0 - 2) * UVScale;
            double r = Math.Clamp(0.6 + 1.5748 * v, 0, 1), b = Math.Clamp(0.6 + 1.8556 * u, 0, 1), g = Math.Clamp((0.6 - 0.2126 * r - 0.0722 * b) / 0.7152, 0, 1);
            double a = Math.Min(255, Math.Log(1 + cnt[j]) * scale * 1.5);
            o[j * 4] = (byte)(b * 255); o[j * 4 + 1] = (byte)(g * 255); o[j * 4 + 2] = (byte)(r * 255); o[j * 4 + 3] = (byte)a;
        }
    }

    static void Histogram(byte[] px, int w, int h, byte[] o, int ow, int oh)
    {
        var hist = new int[4, 256];
        for (int i = 0; i < px.Length; i += 8)
        {
            int b = px[i], g = px[i + 1], r = px[i + 2];
            hist[0, b]++; hist[1, g]++; hist[2, r]++; hist[3, (int)(0.0722 * b + 0.7152 * g + 0.2126 * r)]++;
        }
        // lightly smoothed (graded pictures comb: every other value empty), scaled to a high percentile of the bins rather than the tallest
        // one — a clipped sky or a flat backdrop in one bin used to flatten everything else to a line along the bottom
        var sm = new double[4, 256]; var all = new List<double>(1024);
        for (int c = 0; c < 4; c++)
            for (int v = 0; v < 256; v++)
            {
                sm[c, v] = (hist[c, Math.Max(0, v - 1)] + 2.0 * hist[c, v] + hist[c, Math.Min(255, v + 1)]) / 4;
                if (v is > 0 and < 255) all.Add(sm[c, v]);
            }
        all.Sort();
        double max = Math.Max(1, all[(int)(all.Count * 0.97)] * 1.2);
        int prevLuma = -1;
        for (int x = 0; x < ow; x++)
        {
            int v = x * 256 / ow;
            var top = new int[4];
            for (int c = 0; c < 4; c++) top[c] = oh - 1 - (int)(Math.Min(1, sm[c, v] / max) * (oh - 1));
            int l0 = prevLuma < 0 ? top[3] : Math.Min(prevLuma, top[3]), l1 = prevLuma < 0 ? top[3] : Math.Max(prevLuma, top[3]);
            prevLuma = top[3];
            for (int y = 0; y < oh; y++)
            {
                int j = (y * ow + x) * 4;
                // solid channel fills, added together (red + green + blue = white), and the luma outline on top (joined column to column)
                int b = y >= top[0] ? 200 : 0, g = y >= top[1] ? 200 : 0, r = y >= top[2] ? 200 : 0;
                if (b + g + r > 0) { o[j] = (byte)Math.Min(255, b + 20); o[j + 1] = (byte)Math.Min(255, g + 20); o[j + 2] = (byte)Math.Min(255, r + 20); o[j + 3] = 190; }
                if (y >= l0 && y <= l1) { o[j] = o[j + 1] = o[j + 2] = 245; o[j + 3] = 255; }
            }
        }
    }
}

/// <summary>Lumetri Scopes side view (partial MainWindow).</summary>
public partial class MainWindow
{
    readonly List<ScopeView> scopeViews = new();
    RenderTargetBitmap? scopeRtb; byte[]? scopePx;
    long scopeTick; bool scopeBusy; double scopeTime = double.NaN; int scopeEdits, scopeSeen = -1;

    void InitScopes()
    {
        ScopesList.Children.Clear(); scopeViews.Clear();
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var shown = new HashSet<ScopeKind> { ScopeKind.Waveform, ScopeKind.Vectorscope, ScopeKind.Histogram };
        foreach (var k in Enum.GetValues<ScopeKind>())
        {
            var view = new ScopeView(k) { Visibility = shown.Contains(k) ? Visibility.Visible : Visibility.Collapsed };
            var tb = new ToggleButton { Content = k == ScopeKind.Parade ? "RGB Parade" : k.ToString(), IsChecked = shown.Contains(k), Style = (Style)FindResource("Tool"), Padding = new Thickness(7, 2, 7, 2) };
            tb.Click += (_, _) => { view.Visibility = tb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; scopeSeen = -1; };
            bar.Children.Add(tb);
            scopeViews.Add(view);
        }
        ScopesList.Children.Add(bar);
        foreach (var v in scopeViews) ScopesList.Children.Add(v);
        ed.Changed += () => scopeEdits++; ed.PropsChanged += () => scopeEdits++;
    }

    /// <summary>Per frame (render loop): while the scopes show, recompute ~8 times a second when playing, else when the frame or an edit changed.</summary>
    void UpdateScopes()
    {
        if (scopeViews.Count == 0 || !Shows(ScopesView) || scopeBusy) return;   // (wherever the scopes are docked)
        long now = Environment.TickCount64;
        bool changed = ed.Time != scopeTime || scopeEdits != scopeSeen;
        if (!changed || now - scopeTick < (playing ? 125 : 60)) return;
        scopeTick = now; scopeTime = ed.Time; scopeSeen = scopeEdits;
        int w = Editor.W / 2, h = Editor.H / 2;
        // one capture bitmap + buffer, reused (a new 640×360 bitmap per update grew the working set by ~200 MB between collections);
        // the buffer is free again here: no compute is running (scopeBusy)
        if (scopeRtb == null || scopeRtb.PixelWidth != w || scopeRtb.PixelHeight != h) { scopeRtb = new RenderTargetBitmap(w, h, 48, 48, PixelFormats.Pbgra32); scopePx = new byte[w * h * 4]; }
        else scopeRtb.Clear();
        scopeRtb.Render(Pv);
        var px = scopePx!; scopeRtb.CopyPixels(px, w * 4, 0);
        var kinds = scopeViews.Where(v => v.Visibility == Visibility.Visible).Select(v => v.Kind).ToList();
        scopeBusy = true;
        Task.Run(() => Scopes.Compute(px, w, h, kinds)).ContinueWith(t =>
        {
            scopeBusy = false;
            if (t.Status != TaskStatus.RanToCompletion) return;
            foreach (var v in scopeViews)
                if (t.Result.TryGetValue(v.Kind, out var buf)) v.Bmp.WritePixels(new Int32Rect(0, 0, v.Bmp.PixelWidth, v.Bmp.PixelHeight), buf, v.Bmp.PixelWidth * 4, 0);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
