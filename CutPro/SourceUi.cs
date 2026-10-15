using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>
/// Source monitor (partial MainWindow), Premiere's: double-click a clip in the Project panel (or F = match frame on the timeline) to open it,
/// play / scrub it, mark In (I) and Out (O), then Insert (,) or Overwrite (.) its range at the program playhead — or drag the picture (or
/// the video-only / audio-only handles) onto the timeline. V / A choose which parts go and to which tracks (right-click). The monitor is the
/// program's own machinery on a one-clip editor of its own: a second <see cref="Preview"/> (decoders, scrub cache, holds) with a second
/// <see cref="AudioOut"/> as its clock; only one of the two monitors plays at a time. Marks and the playhead stay with the media item
/// (saved in the project). A dockable panel ("Source"), by default a tab next to Effect Controls.
/// </summary>
public partial class MainWindow
{
    DockPanel SourceView = null!;
    Editor? srcEd; Preview? srcPv; AudioOut? srcAudio; MediaItem? srcItem;
    SourceBar srcBar = null!; TextBlock srcTime = null!, srcDur = null!, srcHint = null!;
    Button srcPlayBtn = null!; ToggleButton srcV = null!, srcA = null!; ComboBox srcPick = null!;
    Grid srcStage = null!; Viewbox srcBox = null!;
    int srcVTrack = 2, srcATrack = 3;
    bool srcPlaying, srcClock, srcFocus, srcPickBusy;
    long srcPlayWall; double srcT0, srcDrawnT = double.NaN; readonly Stopwatch srcSw = new();
    readonly List<string> srcRecent = new();
    Point srcDown; bool srcPress;

    /// <summary>Builds the Source panel (in code) into the right side bar, before the docking registers it.</summary>
    void BuildSourceView()
    {
        TextBlock Txt(string s, double size, string fg) { var t = new TextBlock { Text = s, FontSize = size, VerticalAlignment = VerticalAlignment.Center }; t.SetResourceReference(TextBlock.ForegroundProperty, fg); return t; }
        Button Btn(string glyph, string tip, Action a, bool icon = true)
        {
            var b = new Button { Content = glyph, ToolTip = tip, Focusable = false };
            if (icon) { b.SetResourceReference(StyleProperty, "Icon"); }
            else { b.Padding = new Thickness(6, 2, 6, 2); b.Margin = new Thickness(0, 0, 3, 0); }
            b.Click += (_, _) => { srcFocus = true; a(); };
            return b;
        }
        SourceView = new DockPanel { Background = Brushes.Transparent };
        // header (first child: the docking's drag handle)
        var head = new Grid { Height = 35 };
        var title = new TextBlock { Text = "SOURCE" }; title.SetResourceReference(StyleProperty, "PaneHeader");
        head.Children.Add(title);
        srcPick = new ComboBox { Width = 170, Height = 24, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                                 ToolTip = "Clips opened in the source monitor (double-click one in the Project panel)" };
        srcPick.SelectionChanged += (_, _) =>
        {
            if (srcPickBusy || srcPick.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            if (ed.Media.FirstOrDefault(x => x.Id == id) is { } m && !ReferenceEquals(m, srcItem)) OpenSource(m);
        };
        head.Children.Add(srcPick);
        DockPanel.SetDock(head, Dock.Top); SourceView.Children.Add(head);

        // transport (bottom up)
        var row3 = new WrapPanel { Margin = new Thickness(4, 2, 4, 8), HorizontalAlignment = HorizontalAlignment.Center };
        var markIn = Btn("{", "Mark In (I) — Shift+I: go to In", () => SrcMark(true)); markIn.FontFamily = new FontFamily("Consolas"); markIn.FontSize = 15;
        var markOut = Btn("}", "Mark Out (O) — Shift+O: go to Out", () => SrcMark(false)); markOut.FontFamily = new FontFamily("Consolas"); markOut.FontSize = 15;
        row3.Children.Add(markIn); row3.Children.Add(markOut);
        row3.Children.Add(Btn("\uE711", "Clear In and Out (X)", SrcClearMarks));
        srcV = new ToggleButton { IsChecked = true, Width = 30, Height = 26, Margin = new Thickness(4, 0, 2, 0), Focusable = false };
        srcA = new ToggleButton { IsChecked = true, Width = 30, Height = 26, Margin = new Thickness(0, 0, 4, 0), Focusable = false };
        foreach (var tb in new[] { srcV, srcA })
        {
            tb.SetResourceReference(StyleProperty, "Tool");
            bool video = tb == srcV;
            tb.ContextMenu = new ContextMenu();
            tb.ContextMenuOpening += (_, _) =>
            {
                tb.ContextMenu.Items.Clear();
                foreach (int tr in video ? new[] { 2, 1, 0 } : new[] { 3, 4 })
                {
                    var mi = new MenuItem { Header = $"Into {Tracks.Names[tr]}", IsCheckable = true, IsChecked = (video ? srcVTrack : srcATrack) == tr };
                    int t2 = tr; mi.Click += (_, _) => { if (video) srcVTrack = t2; else srcATrack = t2; tb.IsChecked = true; SrcPatchLabels(); };
                    tb.ContextMenu.Items.Add(mi);
                }
            };
        }
        SrcPatchLabels();
        row3.Children.Add(srcV); row3.Children.Add(srcA);
        row3.Children.Add(Btn("Insert", "Insert the In–Out range at the program playhead (,) — everything after it moves later", () => SourceEdit(true), icon: false));
        row3.Children.Add(Btn("Overwrite", "Overwrite at the program playhead (.) — replaces what's on the target tracks", () => SourceEdit(false), icon: false));
        DockPanel.SetDock(row3, Dock.Bottom); SourceView.Children.Add(row3);

        var row2 = new Grid { Margin = new Thickness(10, 2, 10, 0), Height = 34 };
        srcTime = Txt("00:00:00:00", 15, "B.Timecode"); srcTime.FontFamily = new FontFamily("Consolas"); srcTime.HorizontalAlignment = HorizontalAlignment.Left;
        row2.Children.Add(srcTime);
        var mid = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(60, 0, 0, 0) };
        mid.Children.Add(Btn("\uE892", "Go to In (Shift+I)", () => SrcSeek(Editor.SourceRange(srcItem!).a)));
        mid.Children.Add(Btn("\uE76B", "Previous frame (←)", () => SrcStep(-1)));
        srcPlayBtn = new Button { Content = "\uE768", Width = 34, Height = 26, Padding = new Thickness(0), Margin = new Thickness(4, 0, 4, 0), FontSize = 13, Focusable = false, ToolTip = "Play / Pause (Space)" };
        srcPlayBtn.SetResourceReference(StyleProperty, "Primary"); srcPlayBtn.SetResourceReference(FontFamilyProperty, "IconFont");
        srcPlayBtn.Click += (_, _) => { srcFocus = true; SrcToggle(); };
        mid.Children.Add(srcPlayBtn);
        mid.Children.Add(Btn("\uE76C", "Next frame (→)", () => SrcStep(1)));
        mid.Children.Add(Btn("\uE893", "Go to Out (Shift+O)", () => SrcSeek(Math.Max(0, Editor.SourceRange(srcItem!).b - 1 / TimeUtil.Fps))));
        row2.Children.Add(mid);
        DockPanel.SetDock(row2, Dock.Bottom); SourceView.Children.Add(row2);

        var row1 = new DockPanel { Margin = new Thickness(10, 4, 10, 0) };
        srcDur = Txt("", 11, "B.Fg.Dim"); srcDur.FontFamily = new FontFamily("Consolas"); srcDur.Margin = new Thickness(8, 0, 0, 0); srcDur.ToolTip = "In–Out duration";
        // drag handles: picture only / sound only
        Button Handle(string glyph, string tip, string parts)
        {
            var h = new Button { Content = glyph, ToolTip = tip, Width = 24, Height = 22, FontSize = 12, Focusable = false };
            h.SetResourceReference(StyleProperty, "Icon");
            h.PreviewMouseLeftButtonDown += (_, e) => { srcDown = e.GetPosition(this); srcPress = true; };
            h.PreviewMouseMove += (_, e) => SrcDragMove(e, parts);
            return h;
        }
        var hv = Handle("\uE714", "Drag video only onto the timeline", "v"); var ha = Handle("\uE767", "Drag audio only onto the timeline", "a");
        DockPanel.SetDock(srcDur, Dock.Right); DockPanel.SetDock(ha, Dock.Right); DockPanel.SetDock(hv, Dock.Right);
        row1.Children.Add(srcDur); row1.Children.Add(ha); row1.Children.Add(hv);
        srcBar = new SourceBar { Height = 22, ToolTip = "Click or drag to move the source playhead" };
        srcBar.Scrub += t => { srcFocus = true; SrcPause(); SrcSeek(t); };
        row1.Children.Add(srcBar);
        DockPanel.SetDock(row1, Dock.Bottom); SourceView.Children.Add(row1);

        // the monitor
        srcStage = new Grid { Background = Brushes.Black, Width = Editor.W, Height = Editor.H, ClipToBounds = true };
        srcBox = new Viewbox { Stretch = Stretch.Uniform, Child = srcStage, Margin = new Thickness(8, 0, 8, 0) };
        var stageHost = new Grid { Background = Brushes.Transparent };
        stageHost.Children.Add(srcBox);
        srcHint = Txt("Double-click a clip in the Project panel to open it here.\nMark In (I) and Out (O), then Insert (,) or Overwrite (.) at the program playhead — or drag it onto the timeline.", 12, "B.Fg.Dim");
        srcHint.TextWrapping = TextWrapping.Wrap; srcHint.TextAlignment = TextAlignment.Center; srcHint.Margin = new Thickness(24); srcHint.HorizontalAlignment = HorizontalAlignment.Center;
        stageHost.Children.Add(srcHint);
        stageHost.PreviewMouseLeftButtonDown += (_, e) => { srcDown = e.GetPosition(this); srcPress = srcItem != null; };
        stageHost.PreviewMouseMove += (_, e) => SrcDragMove(e, SrcParts());
        stageHost.MouseLeftButtonUp += (_, _) => srcPress = false;
        SourceView.Children.Add(stageHost);

        // the source monitor takes the keyboard when clicked; the program monitor / timeline take it back
        SourceView.PreviewMouseDown += (_, _) => srcFocus = true;
        EditorGrid.PreviewMouseDown += (_, _) => srcFocus = false;
        PanelGrid.PreviewMouseDown += (_, _) => srcFocus = false;
        RightHost.Children.Add(SourceView);
        SourceView.Visibility = Visibility.Collapsed;
        Closing += (_, _) => { try { srcAudio?.Stop(); } catch { } };
        SrcRefresh();
    }

    string SrcParts() => (srcV.IsChecked == true ? "v" : "") + (srcA.IsChecked == true ? "a" : "");
    void SrcPatchLabels() { srcV.Content = Tracks.Names[srcVTrack]; srcA.Content = Tracks.Names[srcATrack];
                            srcV.ToolTip = $"Video goes to {Tracks.Names[srcVTrack]} (click: include / leave out; right-click: track)";
                            srcA.ToolTip = $"Audio goes to {Tracks.Names[srcATrack]} (click: include / leave out; right-click: track)"; }

    void SrcDragMove(MouseEventArgs e, string parts)
    {
        if (!srcPress || srcItem == null || e.LeftButton != MouseButtonState.Pressed || parts == "") return;
        var d = e.GetPosition(this) - srcDown;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        srcPress = false; SrcPause();
        var data = new DataObject("CutPro.Media", srcItem.Id);
        data.SetData("CutPro.SrcParts", parts);
        DragDrop.DoDragDrop(SourceView, data, DragDropEffects.Copy);
    }

    double SrcLen => srcItem == null ? 0 : srcItem.Type == ClipKind.Image ? Editor.StillDur : srcItem.Duration;
    /// <summary>The last position with a frame (the playhead stops a frame before the end).</summary>
    double SrcLast => Math.Max(0, Math.Floor(SrcLen * TimeUtil.Fps - 1e-6) / TimeUtil.Fps);

    /// <summary>Opens a media item in the source monitor (at its remembered playhead, or t).</summary>
    void OpenSource(MediaItem m, double? t = null)
    {
        if (m.Seq != null) return;
        if (m.Offline) { Alert.Show(this, m.Path + "\n\nThe file was moved or deleted.", "Media offline", AlertKind.Warning); return; }
        SrcPause();
        if (srcItem != null) srcItem.SrcTime = srcEd?.Time ?? 0;
        srcItem = m;
        srcEd ??= new Editor(false);
        srcAudio ??= new AudioOut();
        if (srcPv == null)
        {
            srcPv = new Preview { Ed = srcEd, Audio = srcAudio, Width = Editor.W, Height = Editor.H, ClipToBounds = true, Background = Brushes.Black, ResDiv = 2 };
            srcStage.Children.Add(srcPv);
        }
        // the one-clip editor: the whole media, picture on V1 and sound on A1 (linked), like a clip dropped on an empty timeline
        srcEd.Media.Clear(); srcEd.Media.Add(m); srcEd.Clips.Clear();
        double len = SrcLen; var link = Guid.NewGuid().ToString("N");
        bool pic = m.Type is ClipKind.Video or ClipKind.Image, snd = m.Type == ClipKind.Audio || m.Type == ClipKind.Video && m.HasAudio;
        if (pic) srcEd.Clips.Add(new Clip { MediaId = m.Id, Kind = m.Type, Track = 2, Start = 0, Dur = len, LinkId = snd ? link : null, NoAudio = snd });
        if (snd) srcEd.Clips.Add(new Clip { MediaId = m.Id, Kind = ClipKind.Audio, Track = 3, Start = 0, Dur = len, LinkId = pic ? link : null });
        if (snd) AudioCache.Ensure(m);
        srcEd.Time = Math.Clamp(Math.Round((t ?? m.SrcTime) * TimeUtil.Fps) / TimeUtil.Fps, 0, SrcLast);
        srcRecent.Remove(m.Id); srcRecent.Insert(0, m.Id);
        if (srcRecent.Count > 10) srcRecent.RemoveRange(10, srcRecent.Count - 10);
        srcDrawnT = double.NaN;
        SrcRefresh();
        ShowDock("Source");
        srcFocus = true;
    }

    void CloseSource()
    {
        SrcPause();
        srcItem = null;
        if (srcEd != null) { srcEd.Clips.Clear(); srcEd.Media.Clear(); srcEd.Time = 0; }
        srcPv?.Sync(0, false);
        srcRecent.RemoveAll(id => !ed.Media.Any(x => x.Id == id));
        SrcRefresh();
    }

    /// <summary>Header list, marks, labels after opening / marking.</summary>
    void SrcRefresh()
    {
        srcPickBusy = true;
        srcPick.Items.Clear();
        foreach (var id in srcRecent)
            if (ed.Media.FirstOrDefault(x => x.Id == id) is { } m) srcPick.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Id, ToolTip = m.Path });
        srcPick.SelectedIndex = srcItem != null ? 0 : -1;
        srcPick.Visibility = srcPick.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        srcPickBusy = false;
        bool has = srcItem != null;
        srcHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        srcBox.Visibility = has ? Visibility.Visible : Visibility.Hidden;
        bool pic = srcItem?.Type is ClipKind.Video or ClipKind.Image, snd = srcItem != null && (srcItem.Type == ClipKind.Audio || srcItem.Type == ClipKind.Video && srcItem.HasAudio);
        srcV.IsEnabled = pic; srcA.IsEnabled = snd;
        srcBar.Len = SrcLen; srcBar.In = srcItem?.SrcIn; srcBar.Out = srcItem?.SrcOut; srcBar.T = srcEd?.Time ?? 0;
        srcBar.InvalidateVisual();
        if (has) { var (a, b) = Editor.SourceRange(srcItem!); srcDur.Text = (srcItem!.SrcIn != null || srcItem.SrcOut != null ? "▸ " : "") + TimeUtil.Format(b - a); }
        else srcDur.Text = "";
        srcTime.Text = TimeUtil.Format(srcEd?.Time ?? 0);
    }

    // ---------------- playback ----------------
    /// <summary>Per frame (OnRendering): the source monitor's clock and picture — only while it shows.</summary>
    void SourceTick()
    {
        if (srcPv == null || srcEd == null) return;
        if (!Shows(SourceView)) { if (srcPlaying) SrcPause(); return; }
        if (srcStage.Width != Editor.W || srcStage.Height != Editor.H) { srcStage.Width = srcPv.Width = Editor.W; srcStage.Height = srcPv.Height = Editor.H; }
        if (!srcPlaying)
        {   // decode at about the size it is shown (the panel is often narrow)
            int div = srcBox.ActualWidth < Editor.W * 0.55 ? 2 : 1;
            if (srcPv.ResDiv != div) srcPv.ResDiv = div;
        }
        if (srcItem == null) return;
        if (srcPlaying)
        {
            if (!srcClock)
            {
                srcPv.ClockRunning = false;
                srcPv.Sync(srcEd.Time, true);
                if (srcPv.ReadyToPlay(srcEd.Time) || Environment.TickCount64 - srcPlayWall > 1500)
                {
                    srcClock = true; srcT0 = srcEd.Time; srcSw.Restart();
                    srcAudio!.Start(srcEd.Time);
                }
            }
            else
            {
                srcEd.Time = srcAudio!.Ok ? srcAudio.Clock : srcT0 + srcSw.Elapsed.TotalSeconds;
                if (srcEd.Time >= SrcLast) { srcEd.Time = SrcLast; SrcPause(); }
            }
        }
        srcPv.ClockRunning = srcPlaying && srcClock;
        srcPv.Sync(srcEd.Time, srcPlaying);
        if (srcEd.Time != srcDrawnT)
        {
            srcDrawnT = srcEd.Time;
            srcBar.T = srcEd.Time; srcBar.InvalidateVisual();
            srcTime.Text = TimeUtil.Format(srcEd.Time);
        }
    }

    void SrcPlay()
    {
        if (srcItem == null || srcPlaying) return;
        Pause();                                                             // (one monitor plays at a time)
        if (srcEd!.Time >= SrcLast - 0.01) srcEd.Time = 0;
        srcPlaying = true; srcClock = false; srcPlayWall = Environment.TickCount64;
        srcPlayBtn.Content = "\uE769";
    }
    void SrcPause()
    {
        if (!srcPlaying) return;
        srcAudio?.Stop();
        srcPlaying = false; srcClock = false;
        srcPlayBtn.Content = "\uE768";
        if (srcEd != null) srcEd.Time = Math.Floor(srcEd.Time * TimeUtil.Fps + 1e-3) / TimeUtil.Fps;   // park on the frame that is showing
        if (srcItem != null && srcEd != null) srcItem.SrcTime = srcEd.Time;
    }
    void SrcToggle() { if (srcPlaying) SrcPause(); else SrcPlay(); }
    void SrcSeek(double t)
    {
        if (srcItem == null || srcEd == null) return;
        SrcPause();
        srcEd.Time = Math.Clamp(Math.Round(t * TimeUtil.Fps) / TimeUtil.Fps, 0, SrcLast);
        srcItem.SrcTime = srcEd.Time;
    }
    void SrcStep(int frames) { if (srcEd != null) SrcSeek(srcEd.Time + frames / TimeUtil.Fps); }

    /// <summary>I / O: In at the frame showing, Out after it (the frame under the playhead is the last one in, as in Premiere).</summary>
    void SrcMark(bool isIn)
    {
        if (srcItem == null || srcEd == null) return;
        double t = srcEd.Time, len = SrcLen, f = 1 / TimeUtil.Fps;
        if (isIn) { srcItem.SrcIn = t; if (srcItem.SrcOut is double o && o <= t + f / 2) srcItem.SrcOut = null; }
        else { srcItem.SrcOut = Math.Min(len, t + f); if (srcItem.SrcIn is double i && i >= t + f / 2) srcItem.SrcIn = null; }
        if (srcItem.SrcIn is double a && a <= 1e-9) srcItem.SrcIn = isIn ? 0 : null;
        SrcRefresh();
    }
    void SrcClearMarks()
    {
        if (srcItem == null) return;
        srcItem.SrcIn = srcItem.SrcOut = null;
        SrcRefresh();
    }

    /// <summary>, / . : the source's range into the timeline at the program playhead (the parts and tracks of V / A).</summary>
    void SourceEdit(bool insert)
    {
        if (srcItem == null) { SetTask("Open a clip in the Source monitor first (double-click it in the Project panel)", 3000); return; }
        if (!ed.CanAdd(srcItem)) return;
        SrcPause(); Pause();
        int? vt = srcV.IsChecked == true && srcV.IsEnabled ? srcVTrack : null, at = srcA.IsChecked == true && srcA.IsEnabled ? srcATrack : null;
        var made = ed.PlaceSource(srcItem, ed.Time, vt, at, insert);
        if (made.Count == 0) { SetTask("Nothing to place: switch on V or A", 2500); return; }
        SetTask($"{(insert ? "Inserted" : "Overwrote")} {TimeUtil.Format(made[0].Dur)} of “{srcItem.Name}”", 2500);
    }

    /// <summary>F = Match Frame: the topmost picture under the program playhead (or the selected clip) opens in the source monitor at the frame
    /// it shows.</summary>
    void MatchFrame()
    {
        double t = ed.Time;
        var c = ed.Selected is { MediaId: not null } s && s.Start <= t + 1e-6 && t < s.End - 1e-6 && ed.MediaOf(s) is { Seq: null } ? s
              : ed.Clips.Where(x => x.MediaId != null && x.Start <= t + 1e-6 && t < x.End - 1e-6 && !ed.TrackOff[x.Track] && ed.MediaOf(x) is { Seq: null })
                        .OrderBy(x => x.Kind == ClipKind.Audio ? 1 : 0).ThenBy(x => x.Track).FirstOrDefault();
        if (c == null || ed.MediaOf(c) is not { } m) { SetTask("No clip under the playhead to match", 2500); return; }
        OpenSource(m, m.Type == ClipKind.Image ? 0 : TimeMap.Src(c, t));
    }

    /// <summary>Space, J / K / L, I / O, X, ← / →, Home / End, Shift+I / Shift+O while the source monitor has the keyboard. True when used.</summary>
    bool SourceKey(Key key, bool ctrl, bool shift)
    {
        if (!srcFocus || srcItem == null || ctrl || !Shows(SourceView)) return false;
        switch (key)
        {
            case Key.Space: SrcToggle(); return true;
            case Key.K: SrcPause(); return true;
            case Key.L: SrcPlay(); return true;
            case Key.J: SrcStep(-(int)Math.Round(TimeUtil.Fps)); return true;
            case Key.I when shift: SrcSeek(Editor.SourceRange(srcItem).a); return true;
            case Key.O when shift: SrcSeek(Math.Max(0, Editor.SourceRange(srcItem).b - 1 / TimeUtil.Fps)); return true;
            case Key.I: SrcMark(true); return true;
            case Key.O: SrcMark(false); return true;
            case Key.X: SrcClearMarks(); return true;
            case Key.Left: SrcStep(shift ? -(int)Math.Round(TimeUtil.Fps) : -1); return true;
            case Key.Right: SrcStep(shift ? (int)Math.Round(TimeUtil.Fps) : 1); return true;
            case Key.Home: SrcSeek(0); return true;
            case Key.End: SrcSeek(SrcLast); return true;
        }
        return false;
    }
}

/// <summary>The source monitor's time bar: the media's length, the In–Out range, the playhead; click / drag scrubs.</summary>
public sealed class SourceBar : FrameworkElement
{
    public double Len = 1, T;
    public double? In, Out;
    public event Action<double>? Scrub;
    public SourceBar() { ThemeManager.Changed += InvalidateVisual; Cursor = Cursors.Hand; }
    double X2T(double x) => Math.Clamp((x - 6) / Math.Max(1, ActualWidth - 12), 0, 1) * Len;
    double T2X(double t) => 6 + (Len > 0 ? t / Len : 0) * Math.Max(1, ActualWidth - 12);
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, y = h / 2;
        Brush B(string k) => ThemeManager.Brush(this, k);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));                      // (hit area)
        dc.DrawRoundedRectangle(B("Tl.Ruler"), null, new Rect(6, y - 3, Math.Max(1, w - 12), 6), 3, 3);
        var tick = new Pen(B("Tl.Tick"), 1);
        if (Len > 0)
        {
            double step = Len > 600 ? 60 : Len > 120 ? 10 : Len > 20 ? 5 : 1;
            for (double s = step; s < Len; s += step) { double x = Math.Round(T2X(s)) + 0.5; dc.DrawLine(tick, new Point(x, y + 4), new Point(x, y + 7)); }
        }
        if (In != null || Out != null)
        {
            double a = T2X(In ?? 0), b = T2X(Out ?? Len);
            dc.DrawRoundedRectangle(B("Accent"), null, new Rect(a, y - 3, Math.Max(2, b - a), 6), 3, 3);
            var acc = new Pen(B("Accent"), 2);
            if (In != null) { dc.DrawLine(acc, new Point(a, y - 8), new Point(a, y + 8)); dc.DrawLine(acc, new Point(a, y - 8), new Point(a + 4, y - 8)); dc.DrawLine(acc, new Point(a, y + 8), new Point(a + 4, y + 8)); }
            if (Out != null) { dc.DrawLine(acc, new Point(b, y - 8), new Point(b, y + 8)); dc.DrawLine(acc, new Point(b, y - 8), new Point(b - 4, y - 8)); dc.DrawLine(acc, new Point(b, y + 8), new Point(b - 4, y + 8)); }
        }
        double px = T2X(T);
        var ph = B("Playhead");
        dc.DrawLine(new Pen(ph, 2), new Point(px, 1), new Point(px, h - 1));
        var tri = new StreamGeometry();
        using (var g = tri.Open()) { g.BeginFigure(new Point(px - 5, 0), true, true); g.LineTo(new Point(px + 5, 0), false, false); g.LineTo(new Point(px, 6), false, false); }
        tri.Freeze(); dc.DrawGeometry(ph, null, tri);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { CaptureMouse(); Scrub?.Invoke(X2T(e.GetPosition(this).X)); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Scrub?.Invoke(X2T(e.GetPosition(this).X)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); }
}
