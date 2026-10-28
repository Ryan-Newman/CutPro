using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>The trim tools on the timeline (Ripple Edit B, Rolling Edit N, Rate Stretch R, Slip Y, Slide U): which edit a click grabs,
/// the drag (Editor.BeginTrim / ApplyTrimClamped from the drag's start, edges snap, one undo step), their cursors, and the monitor's
/// two-up view while dragging (<see cref="TrimChanged"/>).</summary>
public partial class TimelineView
{
    /// <summary>A trim-tool drag moved (the window shows the outgoing / incoming frames) or ended (null).</summary>
    public event Action<TrimDrag?>? TrimChanged;
    TrimDrag? trim;
    double trimOffset;
    /// <summary>The offset the current trim-tool drag applies (tests, the status line).</summary>
    public double TrimOffset => trimOffset;

    static string ToolName(TrimTool t) => t switch
    {
        TrimTool.Ripple => "Ripple Trim", TrimTool.Roll => "Rolling Edit", TrimTool.Rate => "Rate Stretch",
        TrimTool.Slip => "Slip", TrimTool.Slide => "Slide", _ => "Trim",
    };

    /// <summary>Starts a trim-tool drag on clip c at point p (the edge nearer the mouse for ripple / roll / rate stretch).</summary>
    bool ToolDown(Point p, Clip c, bool alt)
    {
        if (tool is TrimTool.Select or TrimTool.Razor) return false;
        bool left = p.X - T2X(c.Start) < T2X(c.End) - p.X;
        var s = Ed.BeginTrim(tool, c, left, alt);
        if (s == null) return true;
        if (!Ed.IsSel(c)) Ed.Select(c);
        Ed.Checkpoint(ToolName(tool));
        trim = s; trimOffset = 0; x0 = p.X; mode = Mode.Tool;
        CaptureMouse(); TrimChanged?.Invoke(s); InvalidateVisual();
        return true;
    }

    /// <summary>Applies a trim-tool drag from the mouse (also used by tests through <see cref="TrimTo"/>).</summary>
    void ToolMove(Point p)
    {
        if (trim == null) return;
        double dt = (p.X - x0) / pps;
        TrimTo(dt);
    }

    /// <summary>Moves the current trim-tool drag to an offset of dt seconds: edges snap (playhead, marks, other edits), slip moves in
    /// whole frames, and a move the media or a neighbour refuses stops at the last frame that works.</summary>
    public void TrimTo(double dt)
    {
        if (trim == null) return;
        var s = trim;
        double d;
        if (s.Tool == TrimTool.Slip) { d = Math.Round(dt * TimeUtil.Fps) / TimeUtil.Fps; snapAt = null; }
        else if (s.Tool == TrimTool.Slide) { var c = s.Clip; d = Snap(s.At + dt, s.Ids, 0, c.Dur) - s.At; }
        else d = Snap(s.At + dt, s.Ids, 0) - s.At;
        trimOffset = Ed.ApplyTrimClamped(s, d);
        if (Math.Abs(trimOffset - d) > 1e-9) snapAt = null;
        TrimChanged?.Invoke(s);
        Status?.Invoke($"{ToolName(s.Tool)}: {(trimOffset >= 0 ? "+" : "−")}{TimeUtil.Format(Math.Abs(trimOffset))}" + (s.Tool == TrimTool.Rate ? $" — {s.Clip.Speed:0.##}%" : ""));
        Ed.RaiseProps(); InvalidateVisual();
    }

    /// <summary>Starts a trim-tool drag without the mouse (tests): the tool's edit on clip c (left = its start edge / the edit at its start).</summary>
    public bool BeginToolDrag(TrimTool t, Clip c, bool left, bool alone = false)
    {
        var s = Ed.BeginTrim(t, c, left, alone);
        if (s == null) return false;
        Ed.Checkpoint(ToolName(t));
        trim = s; trimOffset = 0; mode = Mode.Tool;
        TrimChanged?.Invoke(s);
        return true;
    }
    public void EndToolDrag() { if (mode == Mode.Tool) { ToolUp(); mode = Mode.None; Ed.RaiseChanged(); InvalidateVisual(); } }

    void ToolUp()
    {
        if (trim == null) return;
        Ed.Commit();                                                           // (nothing changed: no undo step)
        trim = null; trimOffset = 0;
        TrimChanged?.Invoke(null);
    }

    // ---------- cursors: one per tool, drawn like the razor's ----------
    static readonly Dictionary<TrimTool, Cursor> toolCursors = new();
    Cursor ToolCursor(TrimTool t, Clip h, Point p)
    {
        if (t == TrimTool.Razor) return RazorCursor();
        if (toolCursors.TryGetValue(t, out var cur)) return cur;
        var y = Brushes.Gold; var k = Brushes.Black;
        var pen = new Pen(k, 1);
        void Arrow(DrawingContext dc, double x, double yy, int dir)
        {
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(new Point(x, yy - 4), true, true); c.LineTo(new Point(x + 5 * dir, yy), true, false); c.LineTo(new Point(x, yy + 4), true, false); }
            dc.DrawGeometry(Brushes.White, pen, g);
        }
        void Bracket(DrawingContext dc, double x, int dir)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x + 4 * dir, 6), true, true); c.LineTo(new Point(x, 6), true, false); c.LineTo(new Point(x, 26), true, false);
                c.LineTo(new Point(x + 4 * dir, 26), true, false); c.LineTo(new Point(x + 4 * dir, 24), true, false); c.LineTo(new Point(x + 2 * dir, 24), true, false);
                c.LineTo(new Point(x + 2 * dir, 8), true, false); c.LineTo(new Point(x + 4 * dir, 8), true, false);
            }
            dc.DrawGeometry(y, pen, g);
        }
        cur = MakeCursor(dc =>
        {
            switch (t)
            {
                case TrimTool.Ripple: Bracket(dc, 14, 1); Arrow(dc, 10, 16, -1); Arrow(dc, 22, 16, 1); break;
                case TrimTool.Roll: Bracket(dc, 13, -1); Bracket(dc, 19, 1); Arrow(dc, 8, 16, -1); Arrow(dc, 24, 16, 1); break;
                case TrimTool.Rate:
                    Bracket(dc, 14, 1); Arrow(dc, 10, 16, -1); Arrow(dc, 22, 16, 1);
                    dc.DrawEllipse(Brushes.White, pen, new Point(22, 6), 4, 4); dc.DrawLine(pen, new Point(22, 6), new Point(22, 3.5)); dc.DrawLine(pen, new Point(22, 6), new Point(24, 6));
                    break;
                case TrimTool.Slip:
                    dc.DrawRectangle(y, pen, new Rect(7.5, 8.5, 2, 16)); dc.DrawRectangle(y, pen, new Rect(22.5, 8.5, 2, 16));
                    Arrow(dc, 13, 16, -1); Arrow(dc, 19, 16, 1); dc.DrawLine(pen, new Point(12, 16), new Point(20, 16));
                    break;
                default:
                    dc.DrawRectangle(y, pen, new Rect(4.5, 11.5, 23, 9));
                    Arrow(dc, 10, 16, -1); Arrow(dc, 22, 16, 1);
                    break;
            }
        }, 16, 16);
        toolCursors[t] = cur;
        return cur;
    }

    /// <summary>A 32 × 32 cursor from a drawing (hotspot hx, hy).</summary>
    static Cursor MakeCursor(Action<DrawingContext> draw, short hx, short hy)
    {
        try
        {
            const int S = 32;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) draw(dc);
            var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv);
            var bmp = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
            var px = new byte[S * S * 4]; bmp.CopyPixels(px, S * 4, 0);
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write((short)0); w.Write((short)2); w.Write((short)1);                  // ICONDIR (type 2 = cursor)
            w.Write((byte)S); w.Write((byte)S); w.Write((byte)0); w.Write((byte)0);
            w.Write(hx); w.Write(hy);                                                 // hotspot
            w.Write(40 + S * S * 4 + S * 4); w.Write(22);
            w.Write(40); w.Write(S); w.Write(S * 2); w.Write((short)1); w.Write((short)32); // BITMAPINFOHEADER
            w.Write(0); w.Write(S * S * 4 + S * 4); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = S - 1; y >= 0; y--) w.Write(px, y * S * 4, S * 4);            // bottom-up BGRA
            w.Write(new byte[S * 4]);                                                 // AND mask (alpha is used)
            w.Flush(); ms.Position = 0;
            return new Cursor(ms);
        }
        catch { return Cursors.SizeWE; }
    }
}
