using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>Markers on the timeline (partial TimelineView): sequence markers in the ruler (a coloured tab; a bar for a marker with a length; the
/// name beside it when there is room), clip markers on the clips' top edge. Click = go there, drag = move (one undo step), double-click = edit,
/// right-click = the marker's menu; the playhead and clip edges snap to markers.</summary>
public partial class TimelineView
{
    /// <summary>Double-click / menu "Edit…": the marker dialog (the clip it is on, or null). Menu "Add Marker" on the ruler: at a time.</summary>
    public event Action<Marker, Clip?>? MarkerEditRequested;
    Marker? markerDrag; double markerT0;

    static readonly Dictionary<MarkerColor, Brush> markerBrushes = new();
    static Brush MarkerBrush(MarkerColor c)
    {
        if (markerBrushes.TryGetValue(c, out var b)) return b;
        var nb = new SolidColorBrush(Marker.Rgb(c)); nb.Freeze();
        return markerBrushes[c] = nb;
    }
    static readonly Pen markerEdge = MakeEdge();
    static Pen MakeEdge() { var p = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 1); p.Freeze(); return p; }

    /// <summary>Sequence markers in the ruler (drawn inside the ruler's clip).</summary>
    void DrawRulerMarkers(DrawingContext dc)
    {
        var list = Ed.Markers.OrderBy(m => m.T).ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var m = list[i];
            double x = T2X(m.T);
            if (x < HeaderW - 200 || x > ActualWidth + 6) continue;
            var br = MarkerBrush(m.Color);
            if (m.Dur > 0) dc.DrawRectangle(br, null, new Rect(x, RulerH - 4, Math.Max(2, m.Dur * pps), 3));   // (a length: a bar along the bottom of the ruler)
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(x - 4.5, 12), true, true);
                ctx.PolyLineTo(new[] { new Point(x + 4.5, 12), new Point(x + 4.5, 20), new Point(x, RulerH - 1), new Point(x - 4.5, 20) }, true, true);
            }
            g.Freeze();
            dc.DrawGeometry(br, markerEdge, g);
            if (!m.Chapter) dc.DrawLine(markerEdge, new Point(x - 2.5, 15), new Point(x + 2.5, 15));    // (a dash: not a chapter)
            // its name, when there is room before the next one
            double room = (i + 1 < list.Count ? T2X(list[i + 1].T) : ActualWidth) - x - 10;
            if (m.Name.Length > 0 && room > 24) dc.DrawText(Txt(m.Name, 9.5, hdrText, false, room), new Point(x + 6, 12));
        }
    }

    /// <summary>A clip's markers along its top edge (small tabs in their colours).</summary>
    void DrawClipMarkers(DrawingContext dc, Clip c, Rect rect)
    {
        if (c.Markers == null) return;
        foreach (var m in c.Markers)
        {
            if (Editor.TimelineTime(c, m) is not double t) continue;
            double x = T2X(t);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(x - 4, rect.Top), true, true);
                ctx.PolyLineTo(new[] { new Point(x + 4, rect.Top), new Point(x + 4, rect.Top + 5), new Point(x, rect.Top + 9), new Point(x - 4, rect.Top + 5) }, true, true);
            }
            g.Freeze();
            dc.DrawGeometry(MarkerBrush(m.Color), markerEdge, g);
        }
    }

    /// <summary>The sequence marker under a point of the ruler.</summary>
    Marker? RulerMarkerAt(Point p) =>
        p.Y < RulerH && p.Y >= 9 && p.X >= HeaderW ? Ed.Markers.Where(m => Math.Abs(T2X(m.T) - p.X) <= 5).OrderBy(m => Math.Abs(T2X(m.T) - p.X)).FirstOrDefault() : null;
    /// <summary>The clip marker under a point (on a clip's top edge).</summary>
    (Clip c, Marker m, double t)? ClipMarkerAt(Point p)
    {
        int row = RowAt(p.Y); if (row < 0 || p.X < HeaderW) return null;
        double top = RowY(row) + 3;
        if (p.Y > top + 11) return null;
        foreach (var c in Ed.Clips.Where(c => c.Track == row && c.Markers != null))
            foreach (var m in c.Markers!)
                if (Editor.TimelineTime(c, m) is double t && Math.Abs(T2X(t) - p.X) <= 5) return (c, m, t);
        return null;
    }

    /// <summary>Ruler click on a marker: go there; double-click: edit it; drag: move it.</summary>
    bool MarkerMouseDown(Point p, int clicks)
    {
        if (RulerMarkerAt(p) is { } m)
        {
            ScrubStarted?.Invoke();
            Ed.Seek(m.T);
            if (clicks == 2) { MarkerEditRequested?.Invoke(m, null); return true; }
            markerDrag = m; markerT0 = m.T; Ed.MarkBegin();
            mode = Mode.Marker; CaptureMouse(); return true;
        }
        if (ClipMarkerAt(p) is { } cm)
        {
            Ed.Seek(cm.t);
            if (clicks == 2) MarkerEditRequested?.Invoke(cm.m, cm.c);
            return true;
        }
        return false;
    }
    void MarkerDragTo(Point p)
    {
        if (markerDrag == null) return;
        markerDrag.T = Math.Max(0, Snap(X2T(p.X), null, 0));
        Ed.Seek(markerDrag.T); Ed.RaiseMarkers();
        InvalidateVisual();
    }
    void MarkerUp() { if (markerDrag != null) Ed.MarkEnd(); markerDrag = null; }

    /// <summary>Right-click in the ruler: a marker's menu, or add / clear markers.</summary>
    bool MarkerMenu(Point p)
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, Action a, ItemsControl? into = null, bool check = false, string gesture = "")
        {
            var mi = new MenuItem { Header = text, IsChecked = check, InputGestureText = gesture };
            mi.Click += (_, _) => a(); (into ?? menu).Items.Add(mi); return mi;
        }
        (Marker m, Clip? c)? hit = RulerMarkerAt(p) is { } rm ? (rm, null) : ClipMarkerAt(p) is { } cm ? (cm.m, cm.c) : null;
        if (hit is { } h)
        {
            Item("Edit Marker…", () => MarkerEditRequested?.Invoke(h.m, h.c));
            if (h.c == null) Item("Chapter", () => Ed.UpdateMarker(h.m, null, x => x.Chapter = !x.Chapter), check: h.m.Chapter);
            var col = new MenuItem { Header = "Colour" }; menu.Items.Add(col);
            foreach (var mc in Enum.GetValues<MarkerColor>())
            {
                var mi = Item(mc.ToString(), () => Ed.UpdateMarker(h.m, h.c, x => x.Color = mc), col, h.m.Color == mc);
                mi.Icon = new System.Windows.Shapes.Rectangle { Width = 10, Height = 10, Fill = MarkerBrush(mc) };
            }
            menu.Items.Add(new Separator());
            Item("Delete Marker", () => Ed.DeleteMarker(h.m, h.c));
        }
        else if (p.Y < RulerH && p.X >= HeaderW)
        {
            double t = Editor.Frame(X2T(p.X));
            Item("Add Marker", () => { var m = Ed.AddMarker(t); Ed.Seek(m.T); }, gesture: Keymap.Text("marker.add"));
            Item("Clear All Markers", () => Ed.ClearMarkers(), gesture: Keymap.Text("marker.clear")).IsEnabled = Ed.Markers.Count > 0;
        }
        else return false;
        menu.PlacementTarget = this; menu.IsOpen = true;
        return true;
    }

    /// <summary>Accessibility: screen readers hear "Timeline" with the sequence's state (clips, tracks, playhead, selection, markers).</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new TimelinePeer(this);
    sealed class TimelinePeer : System.Windows.Automation.Peers.FrameworkElementAutomationPeer
    {
        readonly TimelineView tl;
        public TimelinePeer(TimelineView t) : base(t) { tl = t; }
        protected override string GetNameCore() => "Timeline";
        protected override string GetClassNameCore() => nameof(TimelineView);
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.Pane;
        protected override string GetHelpTextCore()
        {
            if (tl.Ed == null) return "";
            int sel = tl.Ed.Clips.Count(tl.Ed.IsSel);
            return $"{tl.Ed.Clips.Count} clips on {Tracks.Video} video and {Tracks.Audio} audio tracks, playhead at {TimeUtil.Format(tl.Ed.Time)} of {TimeUtil.Format(tl.Ed.End)}"
                 + (sel > 0 ? $", {sel} selected" : "") + (tl.Ed.Markers.Count > 0 ? $", {tl.Ed.Markers.Count} markers" : "") + ". Every command has a keyboard shortcut (Settings) and is in the command palette (Ctrl+Shift+P).";
        }
    }

    /// <summary>A marker's name and note as the tooltip while the mouse is over it.</summary>
    void MarkerTip(Point p)
    {
        (Marker m, Clip? c)? hit = RulerMarkerAt(p) is { } rm ? (rm, null) : ClipMarkerAt(p) is { } cm ? (cm.m, cm.c) : null;
        string? tip = hit is { } h ? (h.m.Name.Length > 0 ? h.m.Name : h.c == null ? "Marker" : "Clip marker") + $"  {TimeUtil.Format(h.c == null ? h.m.T : Editor.TimelineTime(h.c, h.m) ?? 0)}"
                                     + (h.m.Note.Length > 0 ? "\n" + h.m.Note : "") + (h.c == null && h.m.Chapter ? "\n(chapter)" : "") : null;
        if (!Equals(ToolTip as string, tip)) ToolTip = tip;
    }
}
