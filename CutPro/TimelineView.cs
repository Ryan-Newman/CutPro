using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>Custom-drawn multi-track timeline with Premiere-style Selection (V) and Razor (C) tools.</summary>
public partial class TimelineView : FrameworkElement
{
    public const double HeaderW = 136, RulerH = 26;                      // (the header: name, padlock, eye or M S)
    enum Mode { None, Scrub, Move, TrimL, TrimR, Marquee, Key, TransEdge, Band, Size, Marker }

    public Editor Ed = null!;
    public bool Razor { get => razor; set { razor = value; HoverT = null; Cursor = value ? RazorCursor() : Cursors.Arrow; OverlayChanged?.Invoke(); } }
    public bool SnapEnabled = true;
    public event Action? ViewChanged;
    public event Action? OverlayChanged;
    /// <summary>The user grabbed the playhead / ruler (playback pauses, like Premiere).</summary>
    public event Action? ScrubStarted;
    /// <summary>Right-click → Speed/Duration… (the dialog lives in the main window).</summary>
    public event Action? SpeedRequested;
    /// <summary>Double-click / menu on a nested clip: open its sequence. Menu "Nest…": nest the selected clips. Notice: a message for the user.</summary>
    public event Action<Clip>? NestOpenRequested;
    public event Action? NestRequested;
    public event Action<string>? Notice;
    /// <summary>A track header asks for a name (double-click / menu) or the Add Tracks dialog (menu; the row the menu was opened on).</summary>
    public event Action<int>? RenameTrackRequested, AddTracksRequested;
    /// <summary>A clip's context menu is being built (the clip, the time under the mouse): the window adds its own items.</summary>
    public event Action<ContextMenu, Clip, double>? ClipMenuOpening;
    /// <summary>A short message for the status bar.</summary>
    public event Action<string>? Status;

    /// <summary>Razor preview: the (frame/snap-adjusted) time and the rows the cut line is drawn on.</summary>
    public double? HoverT;
    public List<int> HoverRows = new();

    bool razor;
    double pps = 80, scrollX;
    public const double MinPps = 0.02, MaxPps = 500;                   // (an hour-long sequence fits a 1000 px timeline at ~0.27)
    public double Pps { get => pps; set { pps = Math.Clamp(value, MinPps, MaxPps); scrollX = Math.Clamp(scrollX, 0, Math.Max(0, ContentWidth - Viewport)); ViewChanged?.Invoke(); InvalidateVisual(); } }
    /// <summary>Zooms by a factor keeping time t where it is on screen (Ctrl+wheel: the mouse; = / -: the playhead).</summary>
    public void ZoomAround(double factor, double t)
    {
        double x = T2X(t) - HeaderW;
        if (x < 0 || x > Viewport) x = Viewport / 2;
        pps = Math.Clamp(pps * factor, MinPps, MaxPps);
        scrollX = Math.Clamp(t * pps - x, 0, Math.Max(0, ContentWidth - Viewport));
        ViewChanged?.Invoke(); InvalidateVisual(); OverlayChanged?.Invoke();
    }
    /// <summary>Shows a time range across the timeline (Zoom to Sequence, Zoom to Selection), with a little room on both sides.</summary>
    public void ZoomTo(double a, double b)
    {
        double len = Math.Max(b - a, 1 / TimeUtil.Fps), pad = len * 0.02;
        pps = Math.Clamp(Viewport / (len + 2 * pad), MinPps, MaxPps);
        scrollX = Math.Clamp((a - pad) * pps, 0, Math.Max(0, ContentWidth - Viewport));
        ViewChanged?.Invoke(); InvalidateVisual(); OverlayChanged?.Invoke();
    }
    public double ScrollX
    {
        get => scrollX;
        set { scrollX = Math.Clamp(value, 0, Math.Max(0, ContentWidth - Viewport)); ViewChanged?.Invoke(); InvalidateVisual(); }
    }
    public double ContentWidth => (Math.Max(Ed.End, 10) + 20) * pps;
    public double Viewport => Math.Max(0, ActualWidth - HeaderW);

    double X2T(double x) => (x - HeaderW + scrollX) / pps;
    public double T2X(double t) => HeaderW + t * pps - scrollX;
    // ---------- rows: one per track (Tracks), each its own height (TrackInfo), scrolled vertically under the ruler ----------
    double scrollY;
    /// <summary>Vertical scroll of the track rows (pixels; the ruler stays).</summary>
    public double ScrollY
    {
        get => scrollY;
        set { scrollY = Math.Clamp(value, 0, Math.Max(0, ContentHeight - TrackViewport)); ViewChanged?.Invoke(); InvalidateVisual(); }
    }
    /// <summary>All rows stacked (+ a little room under the last one), and the height they are shown in.</summary>
    public double ContentHeight => RowTop(Tracks.Count) + 24;
    public double TrackViewport => Math.Max(0, ActualHeight - RulerH);
    static double RowTop(int r) { double y = 0; for (int i = 0; i < r; i++) y += Tracks.Height(i); return y; }
    /// <summary>Top of a row on screen and its height.</summary>
    public double RowY(int r) => RulerH - scrollY + RowTop(r);
    public static double RowHt(int r) => Tracks.Height(r);
    int RowAt(double y)
    {
        if (y < RulerH) return -1;
        double top = RulerH - scrollY;
        for (int r = 0; r < Tracks.Count; r++) { double h = Tracks.Height(r); if (y < top + h) return y >= top ? r : -1; top += h; }
        return -1;
    }
    /// <summary>Scrolls just enough to show a row.</summary>
    public void ShowRow(int r)
    {
        if (r < 0 || r >= Tracks.Count) return;
        double top = RowTop(r), h = RowHt(r);
        if (top < scrollY) ScrollY = top; else if (top + h > scrollY + TrackViewport) ScrollY = top + h - TrackViewport;
    }

    record Orig(Clip C, double Start, double In, double Dur, int Track);
    Mode mode; Clip? drag; List<Orig> group = new(); double x0; double? snapAt;
    Point mStart, mCur, lastMouse; bool mAdditive; HashSet<string> mBase = new();

    public TimelineView()
    {
        ClipToBounds = true; AllowDrop = true; Focusable = false; ReloadTheme();
        Peaks.Ready += () => Dispatcher.BeginInvoke(new Action(InvalidateVisual));   // a waveform finished building
    }

    // ---------- drawing (colours come from the editor theme; ReloadTheme re-reads them) ----------
    static Brush B(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    static Brush Alpha(Color c, byte a) { var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze(); return b; }
    static readonly Brush white = Brushes.White, selTint = B("#40FFFFFF"), shade = B("#46000000");
    static readonly Pen cutPen = new(B("#E6000000"), 2), edgePen = new(B("#40FFFFFF"), 1), selPen = new(Brushes.White, 2), fadePen = new(Brushes.White, 1);
    Brush bgBrush = null!, laneV = null!, laneA = null!, rulerBg = null!, hdrBg = null!, dim = null!, hdrText = null!, btnText = null!,
          snapBrush = null!, offBrush = null!, btnBrush = null!, gapBrush = null!, marqueeFill = null!, rangeBrush = null!, rangeBarBrush = null!,
          vBrush = null!, aBrush = null!, iBrush = null!, tBrush = null!, nBrush = null!, jBrush = null!, waveBrush = null!, soloBrush = null!, soloText = null!;
    Pen tickPen = null!, rowPen = null!, dividerPen = null!, marqueePen = null!;

    public void ReloadTheme()
    {
        Brush T(string k) => ThemeManager.Brush(this, k);                 // the timeline card's palette first
        var acc = ThemeManager.Color(this, "Accent");
        bgBrush = T("Tl.Bg"); laneV = T("Tl.LaneV"); laneA = T("Tl.LaneA"); rulerBg = T("Tl.Ruler"); hdrBg = T("Tl.Header");
        dim = T("Tl.RulerText"); hdrText = T("Tl.HeaderText"); btnText = T("Tl.BtnText"); btnBrush = T("Tl.Btn"); offBrush = T("Tl.BtnOff");
        snapBrush = T("Snap"); vBrush = T("Clip.Video"); aBrush = T("Clip.Audio"); iBrush = T("Clip.Image"); tBrush = T("Clip.Text"); nBrush = T("Clip.Nest"); jBrush = T("Clip.Adjust");
        waveBrush = T("Clip.Wave"); soloBrush = T("Tl.Solo"); soloText = T("Tl.SoloText"); bandBrush = T("Clip.Band");
        gapBrush = Alpha(acc, 0x55); marqueeFill = Alpha(acc, 0x22); rangeBrush = Alpha(acc, 0x32); rangeBarBrush = T("Accent");
        tickPen = new Pen(T("Tl.Tick"), 1); rowPen = new Pen(T("Tl.RowLine"), 1); dividerPen = new Pen(T("Tl.RowLine"), 3); marqueePen = new Pen(T("Accent"), 1);
        InvalidateVisual();
    }
    Brush KindBrush(ClipKind k) => k switch { ClipKind.Video => vBrush, ClipKind.Audio => aBrush, ClipKind.Image => iBrush, ClipKind.Adjustment => jBrush, _ => tBrush };

    FormattedText Txt(string s, double size, Brush b, bool bold = false, double maxW = 0)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxW > 0) { ft.MaxTextWidth = maxW; ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis; }
        return ft;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Ed == null) return;
        double W = ActualWidth, H = ActualHeight;
        dc.DrawRectangle(bgBrush, null, new Rect(0, 0, W, H));

        if (scrollY > Math.Max(0, ContentHeight - TrackViewport)) scrollY = Math.Max(0, ContentHeight - TrackViewport);   // (tracks removed / shrunk)
        dc.PushClip(new RectangleGeometry(new Rect(HeaderW, RulerH, Math.Max(0, W - HeaderW), Math.Max(0, H - RulerH))));
        for (int r = 0; r < Tracks.Count; r++)
        {
            double y = RowY(r), rh = RowHt(r);
            if (y > H || y + rh < RulerH) continue;
            dc.DrawRectangle(Tracks.IsVideo(r) ? laneV : laneA, null, new Rect(HeaderW, y, W, rh));
            dc.DrawLine(r == Tracks.V1 ? dividerPen : rowPen, new Point(HeaderW, y + rh), new Point(W, y + rh));   // (a heavier line between video and audio)
        }
        if (Ed.Gap is { } g)
            dc.DrawRectangle(gapBrush, null, new Rect(T2X(g.A), RowY(g.Track) + 3, Math.Max(2, (g.B - g.A) * pps), RowHt(g.Track) - 6));
        foreach (var c in Ed.Clips) DrawClip(dc, c);
        // locked tracks: diagonal lines over the whole row (Premiere's locked look)
        for (int r = 0; r < Tracks.Count; r++)
            if (Ed.Locked(r)) dc.DrawRectangle(LockHatch(), null, new Rect(HeaderW, RowY(r), Math.Max(0, W - HeaderW), RowHt(r)));
        DrawBands(dc);                                                         // volume rubber bands (clip volume or track automation)
        foreach (var w in Transitions.Compute(Ed).Wins) DrawTransition(dc, w);
        if (dropAt is { } da) DrawDrop(dc, da);
        if (Ed.MarkIn != null || Ed.MarkOut != null)
        {
            double a = T2X(Ed.MarkIn ?? Ed.MarkOut!.Value), b = T2X(Ed.MarkOut ?? Ed.MarkIn!.Value);
            dc.DrawRectangle(rangeBrush, null, new Rect(a, RulerH, Math.Max(1, b - a), H - RulerH));
        }
        if (snapAt is double sa) dc.DrawLine(new Pen(snapBrush, 1) { DashStyle = DashStyles.Dash }, new Point(T2X(sa), RulerH), new Point(T2X(sa), H));
        if (mode == Mode.Marquee) dc.DrawRectangle(marqueeFill, marqueePen, new Rect(mStart, mCur));
        dc.Pop();

        // ruler
        dc.DrawRectangle(rulerBg, null, new Rect(HeaderW, 0, Math.Max(0, W - HeaderW), RulerH));
        dc.PushClip(new RectangleGeometry(new Rect(HeaderW, 0, Math.Max(0, W - HeaderW), RulerH)));
        double[] steps = { 1.0 / 30, 0.1, 0.5, 1, 2, 5, 10, 30, 60, 300, 600, 1800, 3600, 7200 };
        double step = steps.FirstOrDefault(s => s * pps >= 70, 7200);
        for (double t = Math.Floor(X2T(HeaderW) / step) * step; T2X(t) < W; t += step)
        {
            double x = T2X(t); if (x < HeaderW - 60) continue;
            dc.DrawLine(tickPen, new Point(x, 12), new Point(x, RulerH));
            dc.DrawText(Txt(TimeUtil.Format(t, false), 10, dim), new Point(x + 3, 0));
        }
        if (Ed.MarkIn != null || Ed.MarkOut != null)
        {
            double a = T2X(Ed.MarkIn ?? Ed.MarkOut!.Value), b = T2X(Ed.MarkOut ?? Ed.MarkIn!.Value);
            dc.DrawRectangle(rangeBarBrush, null, new Rect(a, RulerH - 6, Math.Max(2, b - a), 6));
        }
        DrawRulerMarkers(dc);
        dc.Pop();

        // track headers: name (its own or V1 / A2 …), lock, hide (video) or mute + solo (audio), the band mode of audio tracks
        dc.DrawRectangle(hdrBg, null, new Rect(0, 0, HeaderW, H));
        dc.PushClip(new RectangleGeometry(new Rect(0, RulerH, HeaderW, Math.Max(0, H - RulerH))));
        for (int r = 0; r < Tracks.Count; r++)
        {
            double y = RowY(r), rh = RowHt(r);
            if (y > H || y + rh < RulerH) continue;
            bool locked = Ed.Locked(r);
            dc.DrawLine(r == Tracks.V1 ? dividerPen : rowPen, new Point(0, y + rh), new Point(HeaderW, y + rh));
            var name = Tracks.Name(r);
            double nameW = LockRect(r).X - 8, nameY = Tracks.IsVideo(r) || rh < 46 ? y + rh / 2 - 9 : y + Math.Min(rh / 2 - 9, rh - 37);   // (audio: above the band mode)
            dc.DrawText(Txt(name, name.Length <= 3 ? 13 : 11.5, hdrText, true, Math.Max(10, nameW)), new Point(6, nameY + (name.Length <= 3 ? 0 : 1)));
            var lk = LockRect(r);
            dc.DrawRoundedRectangle(locked ? offBrush : btnBrush, null, lk, 3, 3);
            Glyph(dc, locked ? "" : "", lk, locked ? white : btnText);    // (closed / open padlock)
            var btn = MuteRect(r);
            dc.DrawRoundedRectangle(Ed.TrackOff[r] ? offBrush : btnBrush, null, btn, 3, 3);
            if (Tracks.IsVideo(r)) { Glyph(dc, Ed.TrackOff[r] ? "" : "", btn, Ed.TrackOff[r] ? white : btnText); continue; }   // (eye: shown / hidden)
            // audio: M (mute) and S (solo), as in Premiere's track headers and the Audio Mixer
            Center(dc, "M", btn, Ed.TrackOff[r] ? white : btnText);
            bool solo = Ed.Mix.Channels[r].Solo;
            dc.DrawRoundedRectangle(solo ? soloBrush : btnBrush, null, SoloRect(r), 3, 3);
            Center(dc, "S", SoloRect(r), solo ? soloText : btnText);
            if (rh >= 46) DrawBandMode(dc, r);
        }
        dc.Pop();
    }
    // header buttons, right-aligned and centred in the row: video [lock][eye], audio [lock][M][S]
    double BtnY(int r) => RowY(r) + RowHt(r) / 2 - 10;
    Rect MuteRect(int r) => Tracks.IsVideo(r) ? new Rect(HeaderW - 30, BtnY(r), 24, 20) : new Rect(HeaderW - 50, BtnY(r), 20, 20);
    Rect SoloRect(int r) => new(HeaderW - 26, BtnY(r), 20, 20);
    Rect LockRect(int r) => Tracks.IsVideo(r) ? new Rect(HeaderW - 56, BtnY(r), 22, 20) : new Rect(HeaderW - 74, BtnY(r), 20, 20);
    static readonly FontFamily IconFont = new("Segoe MDL2 Assets");
    void Glyph(DrawingContext dc, string g, Rect r, Brush b)
    {
        var ft = new FormattedText(g, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                                   11, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
    }
    static Brush? lockHatch;
    /// <summary>Thin diagonal lines (a locked track's rows).</summary>
    static Brush LockHatch()
    {
        if (lockHatch != null) return lockHatch;
        var g = new DrawingGroup();
        using (var c = g.Open())
        {
            c.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 8, 8));
            c.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 1.2), new Point(0, 8), new Point(8, 0));
        }
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return lockHatch = b;
    }
    void Center(DrawingContext dc, string s, Rect r, Brush b)
    {
        var ft = Txt(s, 10.5, b, true);
        dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
    }

    // ---------- audio waveforms (rectified, dB scale: quiet speech stays visible) ----------
    const double WaveFloorDb = -48;
    void DrawWave(DrawingContext dc, Clip c, MediaItem m, Rect rect)
    {
        double top = rect.Y + 16, bot = rect.Bottom - 2, h = bot - top;
        var pk = h < 6 ? null : m.Seq == null ? Peaks.Get(m) : Ed.SeqById(m.Seq) is { } ns && Nest.Sound(Ed, ns) is { } key ? Peaks.Get(key, Nest.FileOfKey(key)! + ".peaks") : null;   // (a nested sequence: its rendered mix)
        if (pk == null) return;
        double xa = Math.Max(rect.Left, HeaderW), xb = Math.Min(rect.Right, ActualWidth);
        if (xb - xa < 1) return;
        bool anim = Anim.IsAnimated(c, AnimProp.Volume);
        double gain = c.Volume / 100;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(xa, bot), true, true);
            for (double x = Math.Floor(xa); x < xb; x++)
            {
                double ta = X2T(x), tb = X2T(x + 1);
                double sa = TimeMap.Src(c, ta), sb = c.Hold ? sa : TimeMap.Src(c, tb);           // (speed / reverse: the source under the column)
                long f0 = (long)Math.Floor(Math.Min(sa, sb) * Mixer.Rate), f1 = (long)Math.Ceiling(Math.Max(sa, sb) * Mixer.Rate);
                double v = pk.Max(f0, f1) * (anim ? Anim.Value(c, AnimProp.Volume, (ta + tb) / 2) / 100 : gain);
                double lv = v > 0 ? Math.Clamp((20 * Math.Log10(v) - WaveFloorDb) / -WaveFloorDb, 0, 1) : 0;
                ctx.LineTo(new Point(Math.Max(xa, x + 0.5), bot - h * lv), true, false);
            }
            ctx.LineTo(new Point(xb, bot), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(waveBrush, null, g);
    }

    // ---------- transitions ----------
    static readonly Brush transFill = MakeTransFill(), transLabel = B("#F2FFFFFF");
    static readonly Pen transEdge = new(B("#B3FFFFFF"), 1), transDiag = new(B("#66FFFFFF"), 1);
    static Brush MakeTransFill()
    {
        var g = new LinearGradientBrush(Color.FromArgb(200, 20, 22, 28), Color.FromArgb(200, 120, 128, 140), 0); g.Freeze(); return g;
    }
    /// <summary>The band a transition box occupies in a track row (Premiere draws it across the cut, over both clips).</summary>
    Rect TransRect(Transitions.Win w)
    {
        double y = RowY(w.Track) + 3, h = RowHt(w.Track) - 6;
        return new Rect(T2X(w.T0), y + h * 0.16, Math.Max(6, T2X(w.T1) - T2X(w.T0)), h * 0.6);
    }
    void DrawTransition(DrawingContext dc, Transitions.Win w)
    {
        var r = TransRect(w);
        if (r.Right < HeaderW || r.Left > ActualWidth) return;
        bool sel = Ed.SelTrans is { } s && s.ClipId == w.Owner.Id && s.Head == w.Head;
        dc.DrawRoundedRectangle(transFill, sel ? new Pen(T2("Accent"), 2) : transEdge, r, 3, 3);
        dc.PushClip(new RectangleGeometry(r));
        // the icon: A fading into B (a diagonal), a dip's colour, a wipe's edge
        dc.DrawLine(transDiag, new Point(r.Left, r.Bottom), new Point(r.Right, r.Top));
        if (r.Width > 54) dc.DrawText(Txt(w.T.Kind, 10, transLabel, false, r.Width - 8), new Point(r.X + 4, r.Y + r.Height / 2 - 7));
        dc.Pop();
    }
    Brush T2(string key) => ThemeManager.Brush(this, key);

    /// <summary>The transition box under the cursor (and whether the cursor is on its left / right edge).</summary>
    (Transitions.Win w, int edge)? TransHit(Point p)
    {
        foreach (var w in Transitions.Compute(Ed).Wins)
        {
            var r = TransRect(w); r.Inflate(3, 2);
            if (!r.Contains(p)) continue;
            int edge = p.X - r.Left < 7 ? -1 : r.Right - p.X < 7 ? 1 : 0;
            return (w, edge);
        }
        return null;
    }

    // dragging a transition's edge changes its duration (the alignment decides which edges move)
    Transitions.Win? transDrag; int transSide;
    void TransEdgeTo(Point p)
    {
        if (transDrag is not { } w || Ed.SelectedTransition is not { } tr) return;
        double t = X2T(p.X), cut = w.Cut, fr = 1 / TimeUtil.Fps;
        double d = tr.Align switch
        {
            TransAlign.Center when w.A != null && w.B != null => 2 * Math.Abs(t - cut),
            _ => transSide > 0 ? t - w.T0 : w.T1 - t,
        };
        tr.Dur = Math.Max(fr, Math.Round(d / fr) * fr);
        Ed.RaiseProps(); InvalidateVisual();
    }

    // drop target while dragging a transition from the Effects panel: an edit point (clip head / tail) near the cursor
    (Clip c, bool head, double t)? dropAt;
    (Clip c, bool head, double t)? EditPointAt(Point p, bool audio)
    {
        int row = RowAt(p.Y); if (row < 0 || p.X < HeaderW || Tracks.IsVideo(row) == audio || Ed.Locked(row)) return null;
        double t = X2T(p.X), best = 24 / pps;
        (Clip c, bool head, double t)? hit = null;
        foreach (var c in Ed.Clips.Where(c => c.Track == row))
        {
            if (Math.Abs(c.Start - t) < best) { best = Math.Abs(c.Start - t); hit = (c, true, c.Start); }
            if (Math.Abs(c.End - t) < best && Ed.After(c) == null) { best = Math.Abs(c.End - t); hit = (c, false, c.End); }
        }
        return hit;
    }
    void DrawDrop(DrawingContext dc, (Clip c, bool head, double t) d)
    {
        double x = T2X(d.t), y = RowY(d.c.Track) + 2;
        var acc = T2("Accent");
        dc.DrawRectangle(acc, null, new Rect(x - 1.5, y, 3, RowHt(d.c.Track) - 4));
        var tri = new StreamGeometry();
        using (var g = tri.Open()) { g.BeginFigure(new Point(x - 6, y - 1), true, true); g.LineTo(new Point(x + 6, y - 1), true, false); g.LineTo(new Point(x, y + 6), true, false); }
        tri.Freeze(); dc.DrawGeometry(acc, null, tri);
    }

    void DrawClip(DrawingContext dc, Clip c)
    {
        double x1 = T2X(c.Start), x2 = T2X(c.End);
        if (x2 < HeaderW || x1 > ActualWidth) return;
        double y = RowY(c.Track) + 3, ch = RowHt(c.Track) - 6;
        if (y > ActualHeight || y + ch < RulerH) return;                    // (scrolled out of view)
        var rect = new Rect(x1, y, Math.Max(3, x2 - x1), ch);
        bool sel = Ed.IsSel(c);
        var m = Ed.MediaOf(c);
        bool nested = m?.Seq != null;
        dc.DrawRectangle(nested ? nBrush : KindBrush(c.Kind), null, rect);
        dc.PushClip(new RectangleGeometry(rect));
        if (m?.Thumb != null && !nested && c.Kind is ClipKind.Video or ClipKind.Image)
        {
            double tw = ch * 16 / 9;
            double xs = x1 + Math.Floor(Math.Max(0, HeaderW - x1) / tw) * tw, xe = Math.Min(x2, ActualWidth);
            for (double x = xs; x < xe; x += tw) dc.DrawImage(m!.Thumb, new Rect(x, y, tw, ch));
            dc.DrawRectangle(shade, null, rect);
        }
        if (c.Kind == ClipKind.Audio && m != null) DrawWave(dc, c, m, rect);
        if (sel) dc.DrawRectangle(selTint, null, rect);
        string label = c.Kind == ClipKind.Text ? (c.Mask != MaskMode.None ? "◐ mask: " : "") + c.Text.Replace('\n', ' ')
                     : c.Kind == ClipKind.Adjustment ? "◑ Adjustment layer" + (Adjust.Works(c) ? "" : " (nothing below it on V1)")
                     : nested ? "▤ " + m!.Name : (m?.Name ?? "(offline)");
        if (c.LinkId != null) label = "🔗 " + label;
        label += TimeMap.Label(c);                                             // "[200%]", "[-100%]", "[hold]", "[time remap]"
        if (Stab.Active(c)) label += " [stabilized]";
        if (rect.Width > 20)
        {
            dc.DrawText(Txt(label, 11, Brushes.Black, false, rect.Width - 8), new Point(x1 + 5, y + 3));
            dc.DrawText(Txt(label, 11, white, false, rect.Width - 8), new Point(x1 + 4, y + 2));
        }
        if (c.FadeIn > 0) dc.DrawLine(fadePen, new Point(x1, y + rect.Height), new Point(x1 + c.FadeIn * pps, y));
        if (c.FadeOut > 0) dc.DrawLine(fadePen, new Point(x2 - c.FadeOut * pps, y), new Point(x2, y + rect.Height));
        DrawKeys(dc, c, rect);
        DrawClipMarkers(dc, c, rect);
        dc.Pop();
        dc.DrawRectangle(null, sel ? selPen : edgePen, rect);
        // hard black edit lines at both ends so every cut is visible, even between two pieces of the same shot
        dc.DrawLine(cutPen, new Point(x1 + 1, y), new Point(x1 + 1, y + rect.Height));
        dc.DrawLine(cutPen, new Point(x2 - 1, y), new Point(x2 - 1, y + rect.Height));
    }

    // ---------- razor cursor (a blade with a cut line; hotspot on the line) ----------
    static Cursor? razorCursor;
    static Cursor RazorCursor()
    {
        if (razorCursor != null) return razorCursor;
        try
        {
            const int S = 32;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1), new Rect(7.5, 0.5, 2, 31));
                dc.PushTransform(new RotateTransform(-35, 21, 9));
                dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.Black, 1.3), new Rect(12.5, 4.5, 17, 9), 1.5, 1.5);
                dc.DrawRectangle(Brushes.Black, null, new Rect(15.5, 8.2, 11, 1.8));
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv);
            var bmp = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
            var px = new byte[S * S * 4]; bmp.CopyPixels(px, S * 4, 0);
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write((short)0); w.Write((short)2); w.Write((short)1);                  // ICONDIR (type 2 = cursor)
            w.Write((byte)S); w.Write((byte)S); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)8); w.Write((short)16);                                    // hotspot
            w.Write(40 + S * S * 4 + S * 4); w.Write(22);
            w.Write(40); w.Write(S); w.Write(S * 2); w.Write((short)1); w.Write((short)32); // BITMAPINFOHEADER
            w.Write(0); w.Write(S * S * 4 + S * 4); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = S - 1; y >= 0; y--) w.Write(px, y * S * 4, S * 4);            // bottom-up BGRA
            w.Write(new byte[S * 4]);                                                 // AND mask (alpha is used)
            w.Flush(); ms.Position = 0;
            razorCursor = new Cursor(ms);
        }
        catch { razorCursor = Cursors.Cross; }
        return razorCursor;
    }

    // ---------- hit testing / snapping ----------

    // ---------- keyframes on clips: property keys (light) and object-mask keys (accent), click to jump, drag to retime ----------
    List<(double t, bool mask)> KeyMarks(Clip c)
    {
        var r = new List<(double, bool)>();
        foreach (var t in Anim.AllKeys(c).Select(k => Math.Round(k.k.T * TimeUtil.Fps)).Distinct()) r.Add((Anim.TimelineTime(c, t / TimeUtil.Fps), false));
        foreach (var f in c.Masks.SelectMany(m => Ed.MaskTracks.TryGetValue(m.Id, out var tr) ? tr.Keys : Enumerable.Empty<int>()).Distinct())
            r.Add((Anim.TimelineTime(c, f / (double)TimeUtil.Fps), true));
        return r.Where(k => k.Item1 >= c.Start - 1e-6 && k.Item1 < c.End + 1e-6).ToList();
    }
    static double KeyY(Rect rect, bool mask) => rect.Bottom - (mask ? 18 : 7);

    void DrawKeys(DrawingContext dc, Clip c, Rect rect)
    {
        if (rect.Height < 30) return;
        var marks = KeyMarks(c);
        if (marks.Count == 0) return;
        var accent = ThemeManager.Brush("Accent"); var pen = new Pen(B("#C0000000"), 1);
        foreach (var (t, mask) in marks)
        {
            double x = T2X(t), y = KeyY(rect, mask), s = 4.5;
            var g = new StreamGeometry();
            using (var ctx = g.Open()) { ctx.BeginFigure(new Point(x, y - s), true, true); ctx.PolyLineTo(new[] { new Point(x + s, y), new Point(x, y + s), new Point(x - s, y) }, true, true); }
            dc.DrawGeometry(mask ? accent : white, pen, g);
        }
    }

    (Clip c, double t, bool mask)? KeyHit(Point p)
    {
        var c = HitClip(p); if (c == null) return null;
        double y = RowY(c.Track) + 3;
        var rect = new Rect(T2X(c.Start), y, Math.Max(3, T2X(c.End) - T2X(c.Start)), RowHt(c.Track) - 6);
        foreach (var (t, mask) in KeyMarks(c).OrderBy(k => Math.Abs(T2X(k.t) - p.X)))
            if (Math.Abs(T2X(t) - p.X) <= 6 && Math.Abs(KeyY(rect, mask) - p.Y) <= 6) return (c, t, mask);
        return null;
    }

    // key drag state
    Clip? keyClip; bool keyMask; double keySrc0; int keyLastDf; string? keyMaskId;
    List<(Keyframe k, double t0)> keyMoving = new();

    void BeginKeyDrag(Clip c, double t, bool mask, Point p)
    {
        if (!Ed.IsSel(c)) Ed.Select(c);
        Ed.Seek(t);
        keyClip = c; keyMask = mask; keySrc0 = Anim.SrcTime(c, t); keyLastDf = 0; x0 = p.X; keyMoving.Clear(); keyMaskId = null;
        if (mask)
        {
            int f = (int)Math.Round(keySrc0 * TimeUtil.Fps);
            keyMaskId = c.Masks.Select(m => m.Id).FirstOrDefault(id => Ed.MaskTracks.TryGetValue(id, out var tr) && tr.IsKey(f));
            if (keyMaskId != null) Ed.MaskBegin(keyMaskId);
        }
        else
        {
            keyMoving = Anim.AllKeys(c).Where(x => Math.Abs(x.k.T - keySrc0) < 0.5 / TimeUtil.Fps).Select(x => (x.k, x.k.T)).ToList();
            Ed.Checkpoint();
        }
        mode = Mode.Key; CaptureMouse();
    }

    void KeyDragTo(Point p)
    {
        var c = keyClip!;
        int df = (int)Math.Round((p.X - x0) / pps * TimeUtil.Fps);
        double src;
        if (!TimeMap.IsRemapped(c))
        {
            src = keySrc0 + df / (double)TimeUtil.Fps;
            src = Math.Clamp(src, c.In, c.In + c.Dur - 1.0 / TimeUtil.Fps);
        }
        else
        {   // speed / reverse: the key follows the mouse in timeline time and lands on the source frame shown there
            double t = Math.Clamp(Math.Round((TimeMap.Out(c, keySrc0) + (p.X - x0) / pps) * TimeUtil.Fps) / TimeUtil.Fps, c.Start, c.End - 1.0 / TimeUtil.Fps);
            src = TimeMap.Src(c, t);
        }
        df = (int)Math.Round((src - keySrc0) * TimeUtil.Fps);
        if (keyMask && keyMaskId != null && Ed.MaskTracks.TryGetValue(keyMaskId, out var tr))
        {
            if (df == keyLastDf) return;
            int f0 = (int)Math.Round(keySrc0 * TimeUtil.Fps);
            var poly = tr.Frames.GetValueOrDefault(f0 + keyLastDf);
            if (poly != null) { tr.Remove(f0 + keyLastDf); tr.Set(f0 + df, poly, key: true); }
            keyLastDf = df; Ed.RaiseMasks();
        }
        else
        {
            foreach (var (k, t0) in keyMoving) k.T = t0 + df / (double)TimeUtil.Fps;
            foreach (var tr2 in c.Anim?.Values ?? Enumerable.Empty<PropTrack>()) tr2.Keys.Sort((a, b) => a.T.CompareTo(b.T));
            Ed.RaiseProps();
        }
        Ed.Seek(Anim.TimelineTime(c, keySrc0 + df / (double)TimeUtil.Fps));
        InvalidateVisual();
    }

    void EndKeyDrag()
    {
        var c = keyClip!;
        if (keyMask) Ed.MaskEnd();
        else
        {
            // a moved key overwrites one it lands on
            var moved = keyMoving.Select(m => m.k).ToHashSet();
            foreach (var tr in c.Anim?.Values ?? Enumerable.Empty<PropTrack>())
                tr.Keys.RemoveAll(k => !moved.Contains(k) && tr.Keys.Any(o => moved.Contains(o) && Math.Abs(o.T - k.T) < 0.5 / TimeUtil.Fps));
            Ed.Commit();
        }
        keyClip = null; keyMoving.Clear();
        Ed.RaiseProps();
    }
    /// <summary>A clip's box on screen (the visible part; null when scrolled out of view).</summary>
    public Rect? ClipRect(Clip c)
    {
        double x1 = Math.Max(HeaderW, T2X(c.Start)), x2 = Math.Min(ActualWidth, T2X(c.End));
        if (x2 - x1 < 1) return null;
        double y = RowY(c.Track) + 3;
        return new Rect(x1, y, x2 - x1, RowHt(c.Track) - 6);
    }

    Clip? HitClip(Point p)
    {
        int row = RowAt(p.Y); if (row < 0 || p.X < HeaderW) return null;
        double t = X2T(p.X);
        return Ed.Clips.FirstOrDefault(c => c.Track == row && t >= c.Start && t < c.End && Ed.Editable(c));   // (a locked track's clips can't be picked)
    }

    /// <summary>Snaps v (plus any of the offsets) to the playhead, marks and clip edges within 8 px, then to a frame.</summary>
    double Snap(double v, ICollection<string>? ignore, params double[] offsets)
    {
        snapAt = null;
        double best = v;
        if (SnapEnabled)
        {
            var pts = new List<double> { 0, Ed.Time };
            if (Ed.MarkIn is double mi) pts.Add(mi);
            if (Ed.MarkOut is double mo) pts.Add(mo);
            foreach (var c in Ed.Clips) if (ignore == null || !ignore.Contains(c.Id)) { pts.Add(c.Start); pts.Add(c.End); }
            foreach (var (mt, mk, _) in Ed.AllMarkers()) if (mk != markerDrag) pts.Add(mt);
            double bd = 8 / pps;
            foreach (var p in pts) foreach (var off in offsets)
            {
                double d = Math.Abs(v + off - p);
                if (d < bd) { bd = d; best = p - off; snapAt = p; }
            }
        }
        return snapAt != null ? best : Editor.Frame(best);
    }

    /// <summary>Which clips a razor click at this point would cut (Shift = all tracks, Alt = ignore links).</summary>
    List<Clip> RazorTargets(Clip? c, double t)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return Ed.ClipsAt(t).Where(Ed.Editable).ToList();
        if (c == null) return new();
        var list = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? new List<Clip> { c } : Ed.Linked(c).ToList();
        return list.Where(x => t > x.Start + 1e-4 && t < x.End - 1e-4 && Ed.Editable(x)).ToList();
    }

    public void UpdateHover() => UpdateHover(lastMouse);
    void UpdateHover(Point p)
    {
        HoverT = null; HoverRows.Clear();
        if (Razor && mode == Mode.None && IsMouseOver && p.X >= HeaderW && p.Y > RulerH)
        {
            double t = Snap(X2T(p.X), null, 0); snapAt = null;
            var targets = RazorTargets(HitClip(p), t);
            if (targets.Count > 0) { HoverT = t; HoverRows = targets.Select(c => c.Track).Distinct().ToList(); }
        }
        OverlayChanged?.Invoke();
    }

    // ---------- mouse ----------
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        var mods = Keyboard.Modifiers;
        bool shift = mods.HasFlag(ModifierKeys.Shift), ctrl = mods.HasFlag(ModifierKeys.Control), alt = mods.HasFlag(ModifierKeys.Alt);
        if (p.X < HeaderW)
        {
            if (p.Y >= RulerH && SizeEdgeAt(p) is int sr) { sizeRow = sr; sizeY0 = p.Y; sizeH0 = RowHt(sr); mode = Mode.Size; CaptureMouse(); return; }   // (drag a header's lower edge: its height)
            int r = RowAt(p.Y);
            if (r < 0) return;
            if (LockRect(r).Contains(p)) { Ed.SetLocked(r, !Ed.Locked(r)); InvalidateVisual(); }
            else if (SoloRect(r).Contains(p) && !Tracks.IsVideo(r)) { Ed.Mix.Channels[r].Solo ^= true; Ed.RaiseMixChanged(); InvalidateVisual(); }
            else if (MuteRect(r).Contains(p)) { Ed.TrackOff[r] = !Ed.TrackOff[r]; Ed.RaiseMixChanged(); InvalidateVisual(); }
            else if (!Tracks.IsVideo(r) && RowHt(r) >= 46 && BandModeRect(r).Contains(p)) CycleBand(r);
            else if (e.ClickCount == 2) RenameTrackRequested?.Invoke(r);       // (double-click the name: rename the track)
            return;
        }
        if (MarkerMouseDown(p, e.ClickCount)) { InvalidateVisual(); return; }
        if (p.Y < RulerH) { ScrubStarted?.Invoke(); mode = Mode.Scrub; CaptureMouse(); Ed.Seek(Snap(X2T(p.X), null, 0)); snapAt = null; return; }

        if (!Razor && BeginBand(p, ctrl, alt)) return;                     // (a rubber band: its keys or its line)
        if (!Razor && KeyHit(p) is { } kh) { BeginKeyDrag(kh.c, kh.t, kh.mask, p); InvalidateVisual(); return; }
        if (!Razor && TransHit(p) is { } th)
        {
            // a transition: select it (Effect Controls shows it); its edges change the duration
            Ed.SelectTransition(th.w.Owner, th.w.Head);
            if (th.edge != 0) { transDrag = th.w; transSide = th.edge; mode = Mode.TransEdge; Ed.Checkpoint("Transition Duration"); CaptureMouse(); }
            InvalidateVisual(); return;
        }
        var c = HitClip(p);
        if (!Razor && e.ClickCount == 2 && c != null && Ed.SeqOf(c) != null) { NestOpenRequested?.Invoke(c); return; }   // (double-click a nested clip: open its sequence)
        if (Razor)
        {
            // Razor tool: cut exactly where the guide line is; the tool stays active for the next cut
            double t = Snap(X2T(p.X), null, 0); snapAt = null;
            var targets = RazorTargets(c, t);
            if (targets.Count > 0) Ed.Edit(() => Ed.CutClips(targets, t), "Razor");
            UpdateHover(p);
            return;
        }

        if (c == null)
        {
            // empty track area: start a marquee; a plain click (no drag) selects the gap under the cursor
            mode = Mode.Marquee; mStart = mCur = p; mAdditive = shift || ctrl;
            mBase = mAdditive ? new HashSet<string>(Ed.Sel) : new();
            if (!mAdditive) Ed.ClearSelection();
            CaptureMouse(); return;
        }

        if (shift || ctrl) { Ed.ToggleSelect(c, !alt); InvalidateVisual(); return; }
        if (alt) Ed.Select(c, false, false);
        else if (!Ed.IsSel(c)) Ed.Select(c);
        else { Ed.SelectedId = c.Id; Ed.RaiseChanged(); }

        double lx = T2X(c.Start), rx = T2X(c.End), edge = Math.Min(7, (rx - lx) / 3);
        mode = p.X - lx < edge ? Mode.TrimL : rx - p.X < edge ? Mode.TrimR : Mode.Move;
        IEnumerable<Clip> members = mode == Mode.Move ? Ed.Clips.Where(Ed.IsSel)
            : alt ? new[] { c }
            : Ed.Linked(c).Where(x => mode == Mode.TrimL ? Math.Abs(x.Start - c.Start) < 1e-4 : Math.Abs(x.End - c.End) < 1e-4);
        group = members.Select(x => new Orig(x, x.Start, x.In, x.Dur, x.Track)).ToList();
        drag = c; x0 = p.X;
        Ed.Checkpoint(mode == Mode.Move ? "Move" : "Trim"); CaptureMouse(); InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this); lastMouse = p;
        if (mode == Mode.Size)
        {
            Ed.SetTrackHeight(sizeRow, sizeH0 + (p.Y - sizeY0));
            ViewChanged?.Invoke(); OverlayChanged?.Invoke(); InvalidateVisual(); return;
        }
        if (mode == Mode.None)
        {
            if (p.X < HeaderW) { Cursor = p.Y >= RulerH && SizeEdgeAt(p) != null ? Cursors.SizeNS : Cursors.Arrow; HoverT = null; HoverRows.Clear(); OverlayChanged?.Invoke(); return; }
            MarkerTip(p);
            if (Razor && Cursor != RazorCursor()) Cursor = RazorCursor();
            UpdateHover(p);
            if (!Razor)
            {
                if (BandCursor(p) is { } bc) { Cursor = bc; return; }
                var h = HitClip(p);
                var tt = KeyHit(p) == null ? TransHit(p) : null;
                Cursor = KeyHit(p) != null ? Cursors.Hand : tt is { edge: not 0 } ? Cursors.SizeWE : tt != null ? Cursors.Hand
                       : h != null && (p.X - T2X(h.Start) < 7 || T2X(h.End) - p.X < 7) ? Cursors.SizeWE : Cursors.Arrow;
            }
            return;
        }
        if (mode == Mode.Marker) { MarkerDragTo(p); return; }
        if (mode == Mode.Scrub) { Ed.Seek(Snap(X2T(p.X), null, 0)); snapAt = null; return; }
        if (mode is Mode.Move or Mode.Marquee && ContentHeight > TrackViewport)
        {   // dragging near the top / bottom edge scrolls the tracks
            if (p.Y > ActualHeight - 16) ScrollY += 8;
            else if (p.Y < RulerH + 16 && p.Y >= RulerH - 8) ScrollY -= 8;
        }
        if (mode == Mode.Key) { KeyDragTo(p); return; }
        if (mode == Mode.Band) { BandDragTo(p); return; }
        if (mode == Mode.TransEdge) { TransEdgeTo(p); return; }
        if (mode == Mode.Marquee)
        {
            mCur = p;
            var r = new Rect(mStart, mCur);
            double ta = X2T(r.Left), tb = X2T(r.Right);
            var hit = Ed.Clips.Where(c => c.Start < tb && c.End > ta &&
                RowY(c.Track) < r.Bottom && RowY(c.Track) + RowHt(c.Track) > r.Top && Ed.Editable(c)).ToList();
            Ed.SetSelection(Ed.Clips.Where(c => mBase.Contains(c.Id)).Concat(hit));
            InvalidateVisual(); return;
        }
        if (drag == null) return;
        double dt = (p.X - x0) / pps;
        var ids = group.Select(o => o.C.Id).ToHashSet();
        var prim = group.First(o => o.C == drag);

        if (mode == Mode.Move)
        {
            double ns = Snap(prim.Start + dt, ids, 0, prim.Dur);
            double d = ns - prim.Start;
            d = Math.Max(d, -group.Min(o => o.Start));
            int row = RowAt(p.Y), dRow = row >= 0 && Tracks.IsVideo(row) == Tracks.IsVideo(prim.Track) ? row - prim.Track : 0;
            if (!TryMove(d, dRow, ids) && !TryMove(d, 0, ids)) TryMove(0, dRow, ids);
        }
        else if (mode == Mode.TrimL)
        {
            double d = Snap(prim.Start + dt, ids, 0) - prim.Start;
            if (!TryTrim(d, true, ids)) { snapAt = null; }
        }
        else
        {
            double d = Snap(prim.Start + prim.Dur + dt, ids, 0) - (prim.Start + prim.Dur);
            if (!TryTrim(d, false, ids)) { snapAt = null; }
        }
        Ed.RaiseProps(); InvalidateVisual();
    }

    bool TryMove(double d, int dRow, HashSet<string> ids)
    {
        bool primVideo = Tracks.IsVideo(group.First(o => o.C == drag).Track);
        var plan = new List<(Orig o, double s, int tr)>();
        foreach (var o in group)
        {
            int tr = Tracks.IsVideo(o.Track) == primVideo ? o.Track + dRow : o.Track;
            if (tr < 0 || tr >= Tracks.Count || Tracks.IsVideo(tr) != Tracks.IsVideo(o.Track) || Ed.Locked(tr)) return false;   // (never onto a locked track)
            double s = Math.Max(0, o.Start + d);
            if (Ed.Collides(tr, s, o.Dur, ids)) return false;
            plan.Add((o, s, tr));
        }
        foreach (var (o, s, tr) in plan) { o.C.Start = s; o.C.Track = tr; }
        return true;
    }

    bool TryTrim(double d, bool left, HashSet<string> ids)
    {
        double minDur = 1.0 / TimeUtil.Fps;
        var plan = new List<(Orig o, double s, double i, double du)>();
        foreach (var o in group)
        {
            var m = Ed.MediaOf(o.C);
            bool timed = o.C.Kind is ClipKind.Video or ClipKind.Audio;
            double s = o.Start, i = o.In, du = o.Dur;
            if (left) { s += d; i = TimeMap.SrcAt(o.C, o.Start, o.In, s); du -= d; } else du += d;   // (speed / reverse: the frame shown at the new start)
            if (du < minDur - 1e-6 || s < -1e-6) return false;
            if (timed && !o.C.Hold)
            {
                double mediaDur = m?.Duration ?? double.MaxValue;
                if (!TimeMap.IsRemapped(o.C)) { if (i < -1e-6 || i + du > mediaDur + 1e-3) return false; }
                else { var (lo, hi) = TimeMap.SpanAt(o.C, s, i, du); if (lo < -1e-3 || hi > mediaDur + 1e-3) return false; }
            }
            if (Ed.Collides(o.C.Track, s, du, ids)) return false;
            plan.Add((o, s, i, du));
        }
        foreach (var (o, s, i, du) in plan) { o.C.Start = s; o.C.In = Math.Max(0, i); o.C.Dur = du; }
        return true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (mode == Mode.None) return;
        ReleaseMouseCapture();
        if (mode == Mode.Marker) MarkerUp();
        if (mode == Mode.Key) EndKeyDrag();
        if (mode == Mode.Band) EndBand();
        if (mode == Mode.TransEdge) { Ed.Commit(); transDrag = null; }
        if (mode == Mode.Marquee && (mCur - mStart).Length < 3 && !mAdditive) SelectGapAt(mStart);
        if (drag != null) Ed.Commit();
        mode = Mode.None; drag = null; group.Clear(); snapAt = null;
        Ed.RaiseChanged(); InvalidateVisual();
    }

    /// <summary>A track header's menu (Premiere's): add tracks above / below or several, rename, lock, delete it or every empty one, its height.</summary>
    void HeaderMenu(int r)
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, Action a, ItemsControl? into = null, bool check = false, bool enabled = true, string? tip = null)
        {
            var mi = new MenuItem { Header = text, IsChecked = check, IsEnabled = enabled, ToolTip = tip };
            mi.Click += (_, _) => a(); (into ?? menu).Items.Add(mi); return mi;
        }
        bool video = Tracks.IsVideo(r), room = video ? Tracks.Video < Tracks.MaxVideo : Tracks.Audio < Tracks.MaxAudio;
        string kind = video ? "Video" : "Audio";
        Item($"Add {kind} Track Above", () => Ed.AddTrack(r), enabled: room);
        Item($"Add {kind} Track Below", () => Ed.AddTrack(r, below: true), enabled: room);
        Item("Add Tracks…", () => AddTracksRequested?.Invoke(r));
        menu.Items.Add(new Separator());
        Item("Rename…", () => RenameTrackRequested?.Invoke(r));
        Item("Lock Track", () => Ed.SetLocked(r, !Ed.Locked(r)), check: Ed.Locked(r), tip: "A locked track's clips can't be selected, moved, cut or rippled");
        menu.Items.Add(new Separator());
        bool last = video ? Tracks.Video <= 1 : Tracks.Audio <= 1;
        int n = Ed.Clips.Count(c => c.Track == r);
        Item(n > 0 ? $"Delete Track (and its {n} clip{(n == 1 ? "" : "s")})" : "Delete Track", () => Ed.DeleteTracks(new[] { r }), enabled: !last,
             tip: last ? $"A sequence keeps at least one {kind.ToLowerInvariant()} track" : null);
        Item("Delete Empty Tracks", () => { int k = Ed.DeleteEmptyTracks(); Status?.Invoke(k > 0 ? $"{k} empty track{(k == 1 ? "" : "s")} deleted" : "No empty tracks to delete"); });
        menu.Items.Add(new Separator());
        var size = new MenuItem { Header = "Track Height" }; menu.Items.Add(size);
        var all = new MenuItem { Header = "All Tracks" }; menu.Items.Add(all);
        foreach (var (label, h) in new[] { ("Small", 32.0), ("Medium", Tracks.DefaultHeight), ("Large", 96.0), ("Extra large", 150.0) })
        {
            Item(label, () => { Ed.SetTrackHeight(r, h == Tracks.DefaultHeight ? 0 : h); ViewChanged?.Invoke(); InvalidateVisual(); OverlayChanged?.Invoke(); }, size, Math.Abs(RowHt(r) - h) < 0.5);
            Item(label, () => { for (int i = 0; i < Tracks.Count; i++) Ed.SetTrackHeight(i, h == Tracks.DefaultHeight ? 0 : h); ViewChanged?.Invoke(); InvalidateVisual(); OverlayChanged?.Invoke(); }, all);
        }
        menu.PlacementTarget = this; menu.IsOpen = true;
    }

    // track heights: drag the lower edge of a track header
    int sizeRow; double sizeY0, sizeH0;
    int? SizeEdgeAt(Point p)
    {
        for (int r = 0; r < Tracks.Count; r++) if (Math.Abs(p.Y - (RowY(r) + RowHt(r))) <= 3) return r;
        return null;
    }

    bool SelectGapAt(Point p)
    {
        int row = RowAt(p.Y); if (row < 0 || Ed.Locked(row)) return false;
        double t = X2T(p.X);
        var onRow = Ed.Clips.Where(c => c.Track == row).ToList();
        var next = onRow.Where(c => c.Start >= t).OrderBy(c => c.Start).FirstOrDefault();
        if (next == null) return false;
        double a = onRow.Where(c => c.End <= t).Select(c => c.End).DefaultIfEmpty(0).Max();
        if (next.Start - a < 1e-4) return false;
        Ed.SelectGap(row, a, next.Start);
        return true;
    }

    protected override void OnMouseLeave(MouseEventArgs e) { HoverT = null; HoverRows.Clear(); OverlayChanged?.Invoke(); }

    // Right-click: select what's under the cursor and offer Premiere's clip / gap menu
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (MarkerMenu(p)) { e.Handled = true; return; }
        if (p.Y < RulerH) return;
        if (p.X < HeaderW) { if (RowAt(p.Y) is >= 0 and var hr) { HeaderMenu(hr); e.Handled = true; } return; }
        var c = HitClip(p);
        var menu = new ContextMenu();
        MenuItem Item(string text, Action a, string gesture = "", ItemsControl? into = null, bool check = false)
        {
            var mi = new MenuItem { Header = text, InputGestureText = gesture, IsChecked = check };
            mi.Click += (_, _) => a(); (into ?? menu).Items.Add(mi); return mi;
        }
        if (!Razor && TransHit(p) is { } th)
        {
            // a transition: change it, its alignment and duration, or delete it
            Ed.SelectTransition(th.w.Owner, th.w.Head);
            var tr = Ed.SelectedTransition!;
            void Change(Action<Transition> f) { Ed.Edit(() => f(tr), "Transition"); Ed.RaiseChanged(); }
            var kinds = new MenuItem { Header = "Transition" }; menu.Items.Add(kinds);
            foreach (var d in Transitions.Get(tr.Kind).Fx == Transitions.Fx.Audio ? Transitions.AudioDefs : Transitions.VideoDefs)
                Item(d.Name, () => Change(x => x.Kind = d.Name), "", kinds, d.Name == tr.Kind);
            var dur = new MenuItem { Header = "Duration" }; menu.Items.Add(dur);
            foreach (var s in new[] { 0.25, 0.5, 1, 1.5, 2, 3 }) Item($"{s:0.##} s", () => Change(x => x.Dur = s), "", dur, Math.Abs(tr.Dur - s) < 1e-6);
            if (th.w.A != null && th.w.B != null)
            {
                var al = new MenuItem { Header = "Alignment" }; menu.Items.Add(al);
                foreach (var (a, label) in new[] { (TransAlign.Center, "Center at Cut"), (TransAlign.Start, "Start at Cut"), (TransAlign.End, "End at Cut") })
                    Item(label, () => Change(x => x.Align = a), "", al, tr.Align == a);
            }
            menu.Items.Add(new Separator());
            Item("Delete Transition", () => Ed.DeleteSelection(false), "Del");
            InvalidateVisual();
            menu.PlacementTarget = this; menu.IsOpen = true; e.Handled = true;
            return;
        }
        if (c != null)
        {
            if (!Ed.IsSel(c)) Ed.Select(c);
            bool nested = Ed.SeqOf(c) != null;
            if (nested) { Item("Open Nested Sequence", () => NestOpenRequested?.Invoke(c)); menu.Items.Add(new Separator()); }
            Item("Nest…", () => NestRequested?.Invoke());
            menu.Items.Add(new Separator());
            Item("Ripple Delete", () => Ed.DeleteSelection(true), "Shift+Del");
            Item("Delete", () => Ed.DeleteSelection(false), "Del");
            menu.Items.Add(new Separator());
            bool linked = Ed.Clips.Where(Ed.IsSel).Any(x => x.LinkId != null);
            Item(linked ? "Unlink" : "Link", () => Ed.ToggleLink(), "Ctrl+L");
            menu.Items.Add(new Separator());
            Item("Apply Default Transitions", () => Ed.ApplyDefaultTransitions(true, true), "Shift+D");
            menu.Items.Add(new Separator());
            if (!nested) Item("Speed/Duration…", () => SpeedRequested?.Invoke(), "Ctrl+R");   // (nested sequences play at 100 %)
            if (c.Kind == ClipKind.Video && !nested)
            {
                // frame holds at the playhead when it is over the clip, else where the menu was opened
                double ht = Ed.Time >= c.Start && Ed.Time < c.End ? Ed.Time : X2T(p.X);
                if (!c.Hold)
                {
                    Item("Add Frame Hold", () => Ed.AddFrameHold(c, ht));
                    Item("Insert Frame Hold Segment", () => Ed.InsertFrameHold(c, ht));
                }
                else Item("Remove Frame Hold", () => Ed.SetHold(c, false));
            }
            ClipMenuOpening?.Invoke(menu, c, X2T(p.X));                     // (the window adds copy / paste attributes / groups / markers / reveal)
        }
        else if (SelectGapAt(p)) Item("Ripple Delete", () => Ed.DeleteSelection(true), "Del");
        else return;
        InvalidateVisual();
        menu.PlacementTarget = this; menu.IsOpen = true;
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control || Keyboard.Modifiers == ModifierKeys.Alt)
        {
            var p = e.GetPosition(this); double t = X2T(p.X);
            pps = Math.Clamp(pps * (e.Delta > 0 ? 1.15 : 1 / 1.15), MinPps, MaxPps);
            scrollX = Math.Clamp(t * pps - (p.X - HeaderW), 0, Math.Max(0, ContentWidth - Viewport));
            ViewChanged?.Invoke(); InvalidateVisual();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Shift || e.GetPosition(this).X < HeaderW) ScrollY -= e.Delta * 0.5;   // (over the headers / Shift: up and down the tracks)
        else ScrollX -= e.Delta;
        OverlayChanged?.Invoke();
        e.Handled = true;
    }

    // ---------- drag & drop from the bin ----------
    protected override void OnDragOver(DragEventArgs e)
    {
        if (e.Data.GetDataPresent("CutPro.Transition"))
        {
            // a transition from the Effects panel: show which edit point it would land on
            var d = Transitions.Get((string)e.Data.GetData("CutPro.Transition")!);
            var at = EditPointAt(e.GetPosition(this), d.Fx == Transitions.Fx.Audio);
            if (!Equals(at, dropAt)) { dropAt = at; InvalidateVisual(); }
            e.Effects = at != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; return;
        }
        e.Effects = e.Data.GetDataPresent("CutPro.Media") ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
    }
    protected override void OnDragLeave(DragEventArgs e) { if (dropAt != null) { dropAt = null; InvalidateVisual(); } }

    protected override void OnDrop(DragEventArgs e)
    {
        if (e.Data.GetDataPresent("CutPro.Transition"))
        {
            var kind = (string)e.Data.GetData("CutPro.Transition")!;
            dropAt = null;
            if (EditPointAt(e.GetPosition(this), Transitions.Get(kind).Fx == Transitions.Fx.Audio) is { } at)
            {
                (Clip c, bool head) placed = default;
                Ed.Edit(() => placed = Ed.PlaceTransition(at.c, at.head, kind), "Add Transition");
                Ed.SelectTransition(placed.c!, placed.head);
            }
            InvalidateVisual(); e.Handled = true; return;
        }
        if (!e.Data.GetDataPresent("CutPro.Media")) return;
        var id = (string)e.Data.GetData("CutPro.Media")!;
        var m = Ed.Media.FirstOrDefault(x => x.Id == id) ?? Ed.SeqItem(id);                  // (a sequence from the Project panel: a nested clip)
        var p = e.GetPosition(this);
        int row = RowAt(p.Y);
        if (m == null || row < 0 || p.X < HeaderW) return;
        if (Ed.Locked(row)) { Status?.Invoke($"{Tracks.Name(row)} is locked — unlock it to drop clips on it"); e.Handled = true; return; }
        if (!Ed.CanAdd(m)) { Notice?.Invoke($"“{m.Name}” can't go into “{Ed.Seq.Name}”: it is this sequence, or contains it."); e.Handled = true; return; }
        bool ok = Tracks.IsVideo(row) ? m.Type != ClipKind.Audio : m.Type == ClipKind.Audio || (m.Type == ClipKind.Video && m.HasAudio);
        // from the source monitor: its V / A choice, or a video-only / audio-only handle
        string parts = e.Data.GetDataPresent("CutPro.SrcParts") ? (string)e.Data.GetData("CutPro.SrcParts")! : "va";
        bool video = parts.Contains('v'), audio = parts.Contains('a');
        if (!video && Tracks.IsVideo(row) || !audio && !Tracks.IsVideo(row) && m.Type != ClipKind.Image) ok = false;
        if (!ok) return;
        Ed.AddMedia(m, row, Snap(X2T(p.X), null, 0), video, audio);
        snapAt = null; e.Handled = true;
    }
}
