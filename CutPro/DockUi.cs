using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CutPro;

/// <summary>Where every panel is: a saved layout or a workspace (settings.json).</summary>
public sealed class DockLayout
{
    /// <summary>Panel id → zone: Left / Right / Bottom / Float (Program: Center / Float; Timeline: Bottom / Float).</summary>
    public Dictionary<string, string> Zones { get; set; } = new();
    /// <summary>Floating panels' windows: x, y, width, height in DIPs (screen).</summary>
    public Dictionary<string, double[]> Floats { get; set; } = new();
    public string Left { get; set; } = "Project";
    public string Right { get; set; } = "Inspector";
    public string Bottom { get; set; } = "Timeline";
    public double SideW { get; set; } = 280;
    public double RightW { get; set; } = 320;
    public double PanelH { get; set; } = 300;
    public bool SideOpen { get; set; } = true;
    public bool RightOpen { get; set; } = true;
    public DockLayout Clone() => JsonSerializer.Deserialize<DockLayout>(JsonSerializer.Serialize(this))!;
}

/// <summary>
/// Dockable panels (partial MainWindow). The side views (Project, Titles, Effects, Mixer, Scopes, Captions) and Effect Controls dock in the
/// left side bar (activity bar icons), the right side bar (tabs when there are several) or the bottom panel (tabs next to TIMELINE /
/// KEYFRAMES), or float in their own window (a second screen); the Program monitor and the timeline panel float or stay home. Drag a panel by
/// its header / tab onto a drop target (outside the window = its own window), or right-click the header. Window menu: workspaces (built-in
/// and saved), full-screen playback (F11), maximize the panel under the mouse (`). The panels are the same elements wherever they are —
/// moving re-parents them, so every feature keeps working; what is "showing" is measured on the element (<see cref="Shows"/>).
/// </summary>
public partial class MainWindow
{
    sealed class DockItem
    {
        public string Id = "", Title = "", Home = "Left", Zone = "Left";
        public FrameworkElement El = null!;
        public RadioButton? Act;
        public DockWindow? Win;
        public bool Fixed;                                                   // Program / Timeline: home or a window
    }
    readonly List<DockItem> docks = new();
    string rightActive = "Inspector", bottomActive = "Timeline";
    bool dockApplying;
    DockItem? DockOf(string id) => docks.FirstOrDefault(d => d.Id == id);
    IEnumerable<DockItem> In(string zone) => docks.Where(d => d.Zone == zone);
    /// <summary>The panel is on screen with some size (not collapsed, not in a closed side bar, not on a hidden tab).</summary>
    static bool Shows(FrameworkElement e) => e.IsVisible && e.ActualWidth > 2 && e.ActualHeight > 2;
    public static readonly string[] BuiltInWorkspaces = { "Editing", "Color", "Audio", "Effects", "Captions", "Second screen" };

    void InitDock()
    {
        void Add(string id, string title, FrameworkElement el, string home, RadioButton? act = null, bool fix = false) =>
            docks.Add(new DockItem { Id = id, Title = title, El = el, Home = home, Zone = home, Act = act, Fixed = fix });
        Add("Project", "Project", ProjectView, "Left", ActProject);
        Add("Titles", "Titles", TitlesView, "Left", ActTitles);
        Add("Effects", "Effects", EffectsView, "Left", ActEffects);
        Add("Mixer", "Audio Track Mixer", MixerView, "Left", ActMixer);
        Add("Scopes", "Lumetri Scopes", ScopesView, "Left", ActScopes);
        Add("Captions", "Captions", CaptionsView, "Left", ActCaptions);
        BuildTranscriptView();
        Add("Transcript", "Transcript", TranscriptView, "Left", ActTranscript);
        BuildQueueView();
        Add("Queue", "Render Queue", QueueView, "Left", ActQueue);
        Add("Inspector", "Effect Controls", InspectorView, "Right", ActInspector);
        BuildSourceView();
        Add("Source", "Source", SourceView, "Right", ActSource);
        BuildHistoryView();
        Add("History", "History", HistoryView, "Right", ActHistory);
        BuildMarkersView();
        Add("Markers", "Markers", MarkersView, "Right", ActMarkers);
        Add("Program", "Program", EditorGrid, "Center", fix: true);
        Add("Timeline", "Timeline", PanelGrid, "Bottom", fix: true);
        foreach (var d in docks.Where(d => !d.Fixed)) if (d.El is DockPanel dp && dp.Children.Count > 0 && dp.Children[0] is FrameworkElement head) HookDock(head, d.Id);
        HookDock(ProgramTab, "Program");
        HookDock(TabTimeline, "Timeline");
        PanelGrid.SizeChanged += (_, _) => FitPanelTools();
        Closing += (_, e) => { if (e.Cancel) return; if (!AppSettings.ReadOnly) SaveLayout(); foreach (var d in docks) if (d.Win != null) d.Win.Quiet = true; };
        // the saved layout (automated runs: the default one, or CUTPRO_WORKSPACE)
        var forced = Environment.GetEnvironmentVariable("CUTPRO_WORKSPACE");
        var start = forced != null ? Workspace(forced) : AppSettings.ReadOnly ? null : S.Layout;
        if (start == null)
        {   // first run with docking (or a test run): the Editing workspace with the remembered sizes and side view
            start = Workspace("Editing")!;
            if (!AppSettings.ReadOnly) { start.SideW = S.SideBarWidth; start.RightW = S.InspectorWidth; start.PanelH = S.TimelineHeight; start.SideOpen = S.SideBarVisible; start.RightOpen = S.InspectorVisible; start.Left = S.SideView; }
        }
        ApplyLayout(start, save: false);
    }

    // ---------------- moving panels ----------------
    static void Detach(FrameworkElement el)
    {
        switch (el.Parent)
        {
            case Panel p: p.Children.Remove(el); break;
            case Decorator d: d.Child = null; break;
            case ContentControl cc: cc.Content = null; break;
        }
    }

    Grid? programPlaceholder;
    /// <summary>Puts a panel in a zone (Left / Right / Bottom / Float; Program: Center / Float; Timeline: Bottom / Float).</summary>
    void MoveDock(string id, string zone, Rect? at = null, bool save = true)
    {
        if (DockOf(id) is not { } d) return;
        if (d.Fixed && zone != "Float") zone = d.Home;
        if (!d.Fixed && zone is not ("Left" or "Right" or "Bottom" or "Float")) zone = d.Home;
        if (d.Zone == zone && (zone != "Float" || d.Win != null)) { if (!dockApplying) ShowDock(id); return; }
        var oldWin = d.Win; d.Win = null;
        if (oldWin != null) { oldWin.Quiet = true; oldWin.Detach(); oldWin.Close(); }
        else Detach(d.El);
        d.Zone = zone;
        switch (zone)
        {
            case "Left": LeftHost.Children.Add(d.El); break;
            case "Right": RightHost.Children.Add(d.El); break;
            case "Bottom" when d.Id == "Timeline": PanelCard.Child = d.El; break;
            case "Bottom": BottomHost.Children.Add(d.El); break;
            case "Center": EditorCard.Child = d.El; break;
            case "Float":
                d.Win = new DockWindow(this, d.Id, d.Title, d.Id == "Program" ? "Editor" : d.Id == "Timeline" ? "Panel" : d.Home == "Right" ? "Insp" : "Side", d.El, at);
                d.Win.DockBack += () => MoveDock(d.Id, d.Home);
                d.Win.KeyForward += e => Window_PreviewKeyDown(this, e);
                d.El.Visibility = Visibility.Visible;
                d.Win.Show();
                break;
        }
        // what the main window shows instead of a floating monitor / timeline
        if (d.Id == "Program" && zone == "Float")
        {
            programPlaceholder ??= Placeholder("The Program monitor is in its own window.", "Bring it back", () => MoveDock("Program", "Center"));
            EditorCard.Child = programPlaceholder;
        }
        if (d.Id == "Timeline")
        {
            bool away = zone == "Float";
            PanelRow.MinHeight = away ? 0 : 160;
            PanelRow.Height = new GridLength(away ? 0 : Math.Max(160, S.TimelineHeight));
            PanelSash.Visibility = away ? Visibility.Collapsed : Visibility.Visible;
            if (away) PanelCard.Child = null;
            Dispatcher.BeginInvoke(() => { Tl.ReloadTheme(); Kfe.InvalidateVisual(); FitPanelTools(); });
        }
        if (d.Id == "Inspector") Dispatcher.BeginInvoke(() => { inkScope = CardOf(InspectorView); BuildInspector(); });
        RefreshDock();
        if (!dockApplying) ShowDock(id);
        if (save) SaveLayout();
    }

    Grid Placeholder(string text, string button, Action back)
    {
        var g = new Grid();
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var t = new TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        var b = new Button { Content = button, Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Center };
        b.Click += (_, _) => back();
        sp.Children.Add(t); sp.Children.Add(b); g.Children.Add(sp);
        return g;
    }

    static FrameworkElement? CardOf(DependencyObject e)
    {
        for (var p = e; p != null; p = VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p)) if (p is Card c) return c;
        return null;
    }

    /// <summary>Visibility, tabs and activity icons from the zones.</summary>
    void RefreshDock()
    {
        // left: one panel at a time (activity bar), Settings is always here
        if (sideView != "Settings" && DockOf(sideView) is not { Zone: "Left" }) sideView = In("Left").FirstOrDefault()?.Id ?? "Settings";
        foreach (var d in In("Left")) d.El.Visibility = d.Id == sideView ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = sideView == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var d in docks) if (d.Act != null) d.Act.Visibility = d.Zone == "Left" ? Visibility.Visible : Visibility.Collapsed;
        if (sideVisible) CheckActivity();
        // right: tabs when there are several
        var right = In("Right").ToList();
        if (right.All(d => d.Id != rightActive)) rightActive = right.FirstOrDefault()?.Id ?? "";
        foreach (var d in right) d.El.Visibility = d.Id == rightActive ? Visibility.Visible : Visibility.Collapsed;
        RightTabs.Children.Clear();
        RightTabsBar.Visibility = right.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (right.Count > 1) foreach (var d in right) RightTabs.Children.Add(DockTab(d, d.Id == rightActive, () => ShowDock(d.Id)));
        if (right.Count == 0 && inspVisible && !dockApplying) SetInspector(false);
        // bottom: extra tabs after TIMELINE / KEYFRAMES
        var bottom = In("Bottom").Where(d => !d.Fixed).ToList();
        if (bottomActive is not ("Timeline" or "Keys") && bottom.All(d => d.Id != bottomActive)) bottomActive = "Timeline";
        DockTabs.Children.Clear();
        foreach (var d in bottom) DockTabs.Children.Add(DockTab(d, d.Id == bottomActive, () => ShowDock(d.Id), upper: true));
        ShowPanel(bottomActive);
        if (Loc.Current != "en") Dispatcher.BeginInvoke(() => Loc.Apply(this), System.Windows.Threading.DispatcherPriority.Loaded);   // (tabs rebuilt in another language)
    }

    /// <summary>A tab for a docked panel (click shows it, drag moves it, right-click: where to).</summary>
    Button DockTab(DockItem d, bool active, Action show, bool upper = false)
    {
        var text = new TextBlock { Text = upper ? d.Title.ToUpperInvariant() : d.Title, FontSize = upper ? 11 : 12, FontWeight = upper ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, active ? "B.Fg" : "B.Fg.Dim");
        var line = new Border { Height = 1.5, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, upper ? 4 : 2), Visibility = active ? Visibility.Visible : Visibility.Hidden };
        line.SetResourceReference(Border.BackgroundProperty, "B.Tab.Border");
        var g = new Grid { Height = upper ? 35 : 30 }; g.Children.Add(text); g.Children.Add(line);
        var b = new Button { Content = g, Style = (Style)FindResource("Flat"), Padding = new Thickness(6, 0, 6, 0), Tag = d.Id, ToolTip = $"{d.Title} — drag to move, right-click for places" };
        b.Click += (_, _) => show();
        HookDock(b, d.Id);
        return b;
    }

    /// <summary>Shows a panel wherever it is docked (opens its side bar, picks its tab, brings its window forward).</summary>
    void ShowDock(string id)
    {
        if (id == "Settings")
        {
            sideView = "Settings"; S.SideView = "Settings"; RefreshDock();
            if (themeCardsStale) BuildThemeCards();
            if (!sideVisible) SetSideBar(true);
            return;
        }
        if (DockOf(id) is not { } d) return;
        switch (d.Zone)
        {
            case "Left":
                sideView = d.Id; S.SideView = d.Id; RefreshDock();
                if (!sideVisible) SetSideBar(true);
                break;
            case "Right":
                rightActive = d.Id; RefreshDock();
                if (!inspVisible) SetInspector(true);
                break;
            case "Bottom":
                bottomActive = d.Fixed ? "Timeline" : d.Id; RefreshDock();
                break;
            case "Float": d.Win?.Activate(); break;
        }
        if (d.Id == "Captions") RefreshCaptions(true);
        if (d.Id == "History") Dispatcher.BeginInvoke(RefreshHistory, System.Windows.Threading.DispatcherPriority.Loaded);
        if (!dockApplying) SaveLayout();
    }
    /// <summary>The panel is the one showing in its zone (menus' check marks).</summary>
    bool Showing(string id) => DockOf(id) is { } d && (d.Zone == "Float" || Shows(d.El));

    // ---------------- drag to dock ----------------
    string? dragId; Point dragStart; bool dockDragging; FrameworkElement? dragHandle; string? dropZone;
    Canvas? dockLayer;
    void HookDock(FrameworkElement handle, string id)
    {
        handle.PreviewMouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) { dragId = id; dragHandle = handle; dragStart = e.GetPosition(handle); } };
        handle.PreviewMouseMove += (_, e) =>
        {
            if (dragId != id || dragHandle != handle) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndDockDrag(false); return; }
            if (!dockDragging)
            {
                if ((e.GetPosition(handle) - dragStart).Length < 12) return;
                dockDragging = true; handle.CaptureMouse(); handle.Cursor = Cursors.SizeAll;
            }
            UpdateDrop(id);
            e.Handled = true;
        };
        handle.PreviewMouseLeftButtonUp += (_, e) => { if (dockDragging && dragId == id) { EndDockDrag(true); e.Handled = true; } else dragId = null; };
        handle.LostMouseCapture += (_, _) => { if (dockDragging && dragHandle == handle) EndDockDrag(false); };
        handle.MouseRightButtonUp += (_, e) => { var m = DockMenu(id); m.PlacementTarget = handle; m.IsOpen = true; e.Handled = true; };
    }

    /// <summary>Drop targets a panel may go to, in main-window coordinates.</summary>
    List<(string zone, Rect r, string label)> DropTargets(string id)
    {
        var d = DockOf(id)!; var list = new List<(string, Rect, string)>();
        Rect Of(FrameworkElement e) => e.IsVisible && e.ActualWidth > 40 ? new Rect(e.TranslatePoint(new Point(0, 0), this), new Size(e.ActualWidth, e.ActualHeight)) : Rect.Empty;
        var body = Of(EditorCard); if (body.IsEmpty) body = new Rect(0, 60, ActualWidth, ActualHeight - 100);
        double top = body.Top, h = Math.Max(100, (Of(PanelCard).IsEmpty ? body.Bottom : Of(PanelCard).Bottom) - top);
        if (!d.Fixed)
        {
            var l = Of(SideCard); if (l.IsEmpty || l.Width < 120) l = new Rect(Of(ActivityCard) is { IsEmpty: false } a ? a.Right : 0, top, 240, h);
            var r = Of(InspCard); if (r.IsEmpty || r.Width < 120) r = new Rect(ActualWidth - 260, top, 250, h);
            var b = Of(PanelCard); if (b.IsEmpty) b = new Rect(body.Left, body.Bottom - 220, body.Width, 220);
            list.Add(("Left", l, "Left side bar")); list.Add(("Right", r, "Right side bar")); list.Add(("Bottom", b, "Bottom panel"));
        }
        else if (d.Zone == "Float") list.Add((d.Home, d.Id == "Program" ? body : Of(PanelCard) is { IsEmpty: false } pc ? pc : new Rect(body.Left, body.Bottom - 220, body.Width, 220), "Back in place"));
        var mid = Of(EditorCard); if (mid.IsEmpty) mid = body;
        list.Add(("Float", new Rect(mid.Left + mid.Width / 2 - 110, mid.Top + 46, 220, 70), "New window"));
        return list;
    }

    void UpdateDrop(string id)
    {
        var p = Mouse.GetPosition(this);
        bool outside = p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight;
        var targets = DropTargets(id);
        dropZone = outside ? "Float" : targets.FirstOrDefault(t => t.zone == "Float" && t.r.Contains(p)).zone ?? targets.FirstOrDefault(t => t.zone != "Float" && t.r.Contains(p)).zone;
        if (dockLayer == null) { dockLayer = new Canvas { IsHitTestVisible = false }; Overlay.Children.Add(dockLayer); }
        dockLayer.Children.Clear();
        var accent = ThemeManager.Brush("Accent");
        foreach (var (zone, r, label) in targets)
        {
            bool hot = zone == dropZone;
            var box = new Rectangle { Width = r.Width - 8, Height = r.Height - 8, RadiusX = 6, RadiusY = 6, Stroke = accent, StrokeThickness = hot ? 2.5 : 1.2,
                                      StrokeDashArray = hot ? null : new DoubleCollection { 4, 3 }, Fill = new SolidColorBrush(Color.FromArgb(hot ? (byte)70 : (byte)18, 80, 140, 255)) };
            Canvas.SetLeft(box, r.Left + 4); Canvas.SetTop(box, r.Top + 4); dockLayer.Children.Add(box);
            var tb = new TextBlock { Text = label, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), Padding = new Thickness(8, 3, 8, 3) };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, r.Left + (r.Width - tb.DesiredSize.Width) / 2); Canvas.SetTop(tb, r.Top + (r.Height - tb.DesiredSize.Height) / 2); dockLayer.Children.Add(tb);
        }
    }

    void EndDockDrag(bool drop)
    {
        var id = dragId; var zone = dropZone; var h = dragHandle;
        bool was = dockDragging;
        dragId = null; dockDragging = false; dropZone = null; dragHandle = null;
        dockLayer?.Children.Clear();
        if (h != null) { h.Cursor = null; if (h.IsMouseCaptured) h.ReleaseMouseCapture(); }
        if (!drop || !was || id == null || zone == null) return;
        Rect? at = null;
        if (zone == "Float")
        {   // the new window where the mouse let go (screen DIPs)
            var s = PointToScreen(Mouse.GetPosition(this)); var dpi = VisualTreeHelper.GetDpi(this);
            var size = id == "Program" ? new Size(800, 520) : id == "Timeline" ? new Size(1100, 360) : new Size(360, 560);
            at = new Rect(s.X / dpi.DpiScaleX - 60, s.Y / dpi.DpiScaleY - 16, size.Width, size.Height);
        }
        MoveDock(id, zone, at);
    }

    ContextMenu DockMenu(string id)
    {
        var d = DockOf(id)!; var cm = new ContextMenu();
        void Mi(string text, Action a, bool on = true, bool check = false) { var mi = new MenuItem { Header = text, IsEnabled = on, IsChecked = check }; mi.Click += (_, _) => a(); cm.Items.Add(mi); }
        if (!d.Fixed)
        {
            Mi("Dock in the left side bar", () => MoveDock(id, "Left"), d.Zone != "Left", d.Zone == "Left");
            Mi("Dock in the right side bar", () => MoveDock(id, "Right"), d.Zone != "Right", d.Zone == "Right");
            Mi("Dock in the bottom panel", () => MoveDock(id, "Bottom"), d.Zone != "Bottom", d.Zone == "Bottom");
        }
        Mi("Open in a new window", () => MoveDock(id, "Float"), d.Zone != "Float", d.Zone == "Float");
        if (d.Zone != d.Home) Mi("Back to its usual place", () => MoveDock(id, d.Home));
        if (id == "Program") { cm.Items.Add(new Separator()); Mi("Full-screen playback (F11)", ToggleFullScreen); }
        return cm;
    }

    // ---------------- layouts and workspaces ----------------
    DockLayout CaptureLayout()
    {
        var l = new DockLayout { Left = sideView == "Settings" ? (In("Left").FirstOrDefault()?.Id ?? "Project") : sideView, Right = rightActive, Bottom = bottomActive,
                                 SideW = S.SideBarWidth, RightW = S.InspectorWidth, PanelH = S.TimelineHeight, SideOpen = sideVisible, RightOpen = inspVisible };
        foreach (var d in docks)
        {
            l.Zones[d.Id] = d.Zone;
            if (d.Win != null) l.Floats[d.Id] = new[] { d.Win.Left, d.Win.Top, d.Win.Width, d.Win.Height };
        }
        return l;
    }
    void SaveLayout()
    {
        if (dockApplying || docks.Count == 0) return;
        S.Layout = CaptureLayout();
        AppSettings.Save();
    }

    /// <summary>Applies a layout: zones, floating windows (on a screen that still exists), active panels, sizes.</summary>
    void ApplyLayout(DockLayout l, bool save = true)
    {
        dockApplying = true;
        try
        {
            S.SideBarWidth = Math.Clamp(l.SideW, 160, 900); S.InspectorWidth = Math.Clamp(l.RightW, 240, 900); S.TimelineHeight = Math.Clamp(l.PanelH, 160, 1200);
            foreach (var d in docks)
            {
                var zone = l.Zones.TryGetValue(d.Id, out var z) ? z : d.Home;
                Rect? at = null;
                if (zone == "Float" && l.Floats.TryGetValue(d.Id, out var f) && f.Length == 4) at = OnSomeScreen(new Rect(f[0], f[1], Math.Max(240, f[2]), Math.Max(160, f[3])));
                if (zone == "Float" && at == null && d.Id == "Program") at = SecondScreenRect(new Size(960, 600));
                if (zone == "Float" && !IsLoaded)
                {   // (a window can't be owned by one not shown yet: float it once the main window is up)
                    var (pid, pat) = (d.Id, at);
                    MoveDock(d.Id, d.Home, null, save: false);
                    RoutedEventHandler? once = null;
                    once = (_, _) => { Loaded -= once; Dispatcher.BeginInvoke(() => { if (DockOf(pid) is { Zone: not "Float" }) MoveDock(pid, "Float", pat, save: false); }); };
                    Loaded += once;
                    continue;
                }
                MoveDock(d.Id, zone, at, save: false);
            }
            sideView = DockOf(l.Left) is { Zone: "Left" } ? l.Left : In("Left").FirstOrDefault()?.Id ?? "Settings";
            rightActive = l.Right; bottomActive = l.Bottom is "Timeline" or "Keys" || DockOf(l.Bottom) is { Zone: "Bottom" } ? l.Bottom : "Timeline";
            RefreshDock();
            SideCol.Width = new GridLength(S.SideBarWidth); InspCol.Width = new GridLength(S.InspectorWidth);
            if (DockOf("Timeline")!.Zone == "Bottom") PanelRow.Height = new GridLength(S.TimelineHeight);
            SetSideBar(l.SideOpen, animate: false);
            SetInspector(l.RightOpen && In("Right").Any(), animate: false);
        }
        finally { dockApplying = false; }
        if (save) SaveLayout();
    }

    /// <summary>A built-in or saved workspace by name (null if unknown).</summary>
    DockLayout? Workspace(string name)
    {
        if (S.Workspaces.TryGetValue(name, out var saved)) return saved.Clone();
        var l = new DockLayout();
        foreach (var d in docks) l.Zones[d.Id] = d.Home;
        switch (name)
        {
            case "Editing": break;
            case "Color": l.Left = "Scopes"; l.SideW = 420; l.RightW = 380; l.PanelH = 250; break;
            case "Audio": l.Left = "Mixer"; l.SideW = 340; l.PanelH = 380; break;
            case "Effects": l.Left = "Effects"; l.SideW = 300; break;
            case "Captions": l.Left = "Captions"; l.SideW = 380; break;
            case "Second screen": l.Zones["Program"] = "Float"; l.PanelH = 420; break;
            default: return null;
        }
        return l;
    }
    void UseWorkspace(string name)
    {
        if (Workspace(name) is not { } l) return;
        ApplyLayout(l);
        S.Workspace = name; AppSettings.Save();
        SetTask($"Workspace: {name}", 1800);
    }
    void SaveWorkspaceAs()
    {
        var name = AskName("Save workspace", "Name of the new workspace (the panels as they are now):", S.Workspace is { Length: > 0 } w && !BuiltInWorkspaces.Contains(w) ? w : "My workspace", "Save");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        S.Workspaces[name] = CaptureLayout(); S.Workspace = name; AppSettings.Save();
        SetTask($"Workspace “{name}” saved", 2500);
    }

    // ---------------- screens ----------------
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    delegate bool MonitorProc(IntPtr mon, IntPtr hdc, ref RECT r, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    /// <summary>Every screen's (monitor, work area) in DIPs of this window's scale; the first is the one this window is on.</summary>
    List<(Rect full, Rect work)> Screens()
    {
        var list = new List<(Rect, Rect)>(); var dpi = VisualTreeHelper.GetDpi(this);
        Rect R(RECT r) => new(r.L / dpi.DpiScaleX, r.T / dpi.DpiScaleY, (r.R - r.L) / dpi.DpiScaleX, (r.B - r.T) / dpi.DpiScaleY);
        var mine = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(m, ref mi)) { var e = (R(mi.Monitor), R(mi.Work)); if (m == mine) list.Insert(0, e); else list.Add(e); }
            return true;
        }, IntPtr.Zero);
        return list;
    }
    /// <summary>A window rect on another screen (centred, filling most of it), or over this window when there is one screen.</summary>
    Rect SecondScreenRect(Size fallback)
    {
        var s = Screens();
        if (s.Count > 1) { var w = s[1].work; return new Rect(w.Left + w.Width * 0.05, w.Top + w.Height * 0.05, w.Width * 0.9, w.Height * 0.9); }
        return new Rect(Left + 80, Top + 80, fallback.Width, fallback.Height);
    }
    /// <summary>The rect if most of it is on a screen, else moved onto this window's screen.</summary>
    Rect OnSomeScreen(Rect r)
    {
        var s = Screens();
        foreach (var (full, _) in s) { var i = Rect.Intersect(full, r); if (!i.IsEmpty && i.Width * i.Height > r.Width * r.Height * 0.4) return r; }
        var w = s.Count > 0 ? s[0].work : new Rect(0, 0, 1280, 800);
        return new Rect(w.Left + 60, w.Top + 60, Math.Min(r.Width, w.Width - 120), Math.Min(r.Height, w.Height - 120));
    }

    // ---------------- full-screen playback ----------------
    Window? fullWin; DependencyObject? monitorHome; Thickness monitorMargin;
    /// <summary>The program monitor alone, full screen on the other screen (or this one); F11 / Esc / double-click back.</summary>
    void ToggleFullScreen()
    {
        if (fullWin != null) { fullWin.Close(); return; }
        var s = Screens();
        var target = s.Count > 1 && DockOf("Program")!.Zone != "Float" ? s[1].full : s.Count > 0 ? s[0].full : new Rect(0, 0, 1280, 720);
        monitorHome = MonitorBox.Parent; monitorMargin = MonitorBox.Margin;
        int row = Grid.GetRow(MonitorBox);
        Detach(MonitorBox);
        MonitorBox.Margin = new Thickness(0);
        var host = new Grid { Background = Brushes.Black };
        host.Children.Add(MonitorBox);
        fullWin = new Window
        {
            Owner = this, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Background = Brushes.Black,
            Left = target.Left, Top = target.Top, Width = target.Width, Height = target.Height, Content = host, Title = "CutPro — full screen",
        };
        fullWin.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.F11) { fullWin.Close(); e.Handled = true; return; }
            Window_PreviewKeyDown(this, e);
        };
        host.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) fullWin?.Close(); };
        fullWin.Closed += (_, _) =>
        {
            host.Children.Remove(MonitorBox);
            MonitorBox.Margin = monitorMargin;
            if (monitorHome is Grid g) { Grid.SetRow(MonitorBox, row); g.Children.Add(MonitorBox); }
            fullWin = null;
            Activate();
        };
        fullWin.Show();
        fullWin.WindowState = WindowState.Maximized;                       // (covers the screen it was placed on, taskbar included)
        SetTask("Full screen — Esc or F11 to leave", 2500);
    }
    public bool FullScreenOn => fullWin != null;

    // ---------------- maximize the panel under the mouse (` like Premiere) ----------------
    (double side, double right, double panel, double editor, bool sideOpen, bool rightOpen)? maxed;
    void ToggleMaximizePanel(string? target = null)                         // target: Program / Timeline / Left / Right (null = under the mouse)
    {
        if (maxed is { } m)
        {
            EditorRow.MinHeight = 200; EditorRow.Height = new GridLength(1, GridUnitType.Star);
            if (DockOf("Timeline")!.Zone == "Bottom") { PanelRow.MinHeight = 160; PanelRow.Height = new GridLength(m.panel); PanelSash.Visibility = Visibility.Visible; }
            SetSideBar(m.sideOpen, animate: false); SetInspector(m.rightOpen, animate: false);
            maxed = null; return;
        }
        var over = Mouse.DirectlyOver as DependencyObject;
        bool Under(FrameworkElement e) { for (var p = over; p != null; p = VisualTreeHelper.GetParent(p)) if (p == e) return true; return false; }
        maxed = (S.SideBarWidth, S.InspectorWidth, PanelRow.ActualHeight, EditorRow.ActualHeight, sideVisible, inspVisible);
        bool sideW = sideVisible, rightW = inspVisible;
        target ??= Under(PanelCard) ? "Timeline" : Under(SideCard) ? "Left" : Under(InspCard) ? "Right" : "Program";
        if (target == "Timeline") { EditorRow.MinHeight = 0; EditorRow.Height = new GridLength(0); PanelRow.Height = new GridLength(1, GridUnitType.Star); sideW = rightW = false; }
        else if (target == "Left") { rightW = false; }
        else if (target == "Right") { sideW = false; }
        else { PanelRow.MinHeight = 0; PanelRow.Height = new GridLength(0); PanelSash.Visibility = Visibility.Collapsed; sideW = rightW = false; }    // the monitor
        bool keepS = S.SideBarVisible, keepR = S.InspectorVisible;
        SetSideBar(sideW, animate: false); SetInspector(rightW, animate: false);
        S.SideBarVisible = keepS; S.InspectorVisible = keepR;               // (a maximize isn't a layout change)
        SetTask("Maximized — ` again to restore", 2000);
    }

    // ---------------- Window menu ----------------
    void WindowMenu(Action<object[]> add, Func<string, string, Action, bool, bool, MenuItem> mi)
    {
        var ws = new MenuItem { Header = "Workspaces" };
        foreach (var n in BuiltInWorkspaces) ws.Items.Add(mi(n, "", () => UseWorkspace(n), S.Workspace == n, true));
        if (S.Workspaces.Count > 0) { ws.Items.Add(new Separator()); foreach (var n in S.Workspaces.Keys.ToList()) ws.Items.Add(mi(n, "", () => UseWorkspace(n), S.Workspace == n, true)); }
        ws.Items.Add(new Separator());
        ws.Items.Add(mi("Save as New Workspace…", "", SaveWorkspaceAs, false, true));
        if (S.Workspaces.Count > 0)
        {
            var del = new MenuItem { Header = "Delete Workspace" };
            foreach (var n in S.Workspaces.Keys.ToList()) del.Items.Add(mi(n, "", () => { S.Workspaces.Remove(n); AppSettings.Save(); SetTask($"Workspace “{n}” deleted", 2000); }, false, true));
            ws.Items.Add(del);
        }
        ws.Items.Add(mi("Reset to Editing", "", () => UseWorkspace("Editing"), false, true));
        var items = new List<object> { ws, "-" };
        foreach (var d in docks.Where(d => !d.Fixed)) items.Add(mi(d.Title + (d.Zone == "Float" ? "  (window)" : d.Zone == "Right" ? "  (right)" : d.Zone == "Bottom" ? "  (bottom)" : ""), "", () => ShowDock(d.Id), Showing(d.Id), true));
        items.Add("-");
        items.Add(mi(DockOf("Program")!.Zone == "Float" ? "Dock the Program Monitor" : "Program Monitor in a New Window", "", () => MoveDock("Program", DockOf("Program")!.Zone == "Float" ? "Center" : "Float"), false, true));
        items.Add(mi(DockOf("Timeline")!.Zone == "Float" ? "Dock the Timeline" : "Timeline in a New Window", "", () => MoveDock("Timeline", DockOf("Timeline")!.Zone == "Float" ? "Bottom" : "Float"), false, true));
        items.Add(mi("Full-Screen Playback", "F11", ToggleFullScreen, fullWin != null, true));
        items.Add(mi("Maximize Panel Under the Mouse", "`", () => ToggleMaximizePanel(), maxed != null, true));
        add(items.ToArray());
    }
}

/// <summary>A floating panel's window (a second screen): the panel inside its own card, the theme's colours, dark title bar in dark themes.
/// Closing it docks the panel back where it usually lives; keys go to the main window's shortcuts.</summary>
public sealed class DockWindow : Window
{
    readonly Card card;
    readonly FrameworkElement content;
    public readonly string PanelId;
    public readonly string Region;
    public bool Quiet;
    public event Action? DockBack;
    public event Action<KeyEventArgs>? KeyForward;

    public DockWindow(Window owner, string id, string title, string region, FrameworkElement content, Rect? at)
    {
        PanelId = id; Region = region; this.content = content;
        Owner = owner; ShowInTaskbar = false; Title = "CutPro — " + title;
        Icon = owner.Icon;
        SetResourceReference(BackgroundProperty, "B.Surf.Win");
        SetResourceReference(ForegroundProperty, "B.Fg");
        if (at is { } r) { WindowStartupLocation = WindowStartupLocation.Manual; Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height; }
        else { WindowStartupLocation = WindowStartupLocation.CenterOwner; Width = 420; Height = 600; }
        card = new Card { Region = region, Surface = "B.Surf." + region, Child = content };
        ThemeManager.ApplyCard(card, region);
        var bar = new DockPanel { Height = 28, LastChildFill = false };
        bar.SetResourceReference(Panel.BackgroundProperty, "B.Strip");
        var back = new Button { Content = "Dock back", Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 2, 6, 2), ToolTip = "Put this panel back in the main window" };
        back.SetResourceReference(StyleProperty, "Flat");
        back.Click += (_, _) => DockBack?.Invoke();
        DockPanel.SetDock(back, Dock.Right); bar.Children.Add(back);
        var name = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        bar.Children.Add(name);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar); root.Children.Add(card);
        Content = root;
        PreviewKeyDown += (_, e) => { if (Keyboard.FocusedElement is not (TextBox or ComboBox or ComboBoxItem)) KeyForward?.Invoke(e); };
        Closing += (_, e) => { if (!Quiet) { e.Cancel = true; Dispatcher.BeginInvoke(() => DockBack?.Invoke()); } };
        SourceInitialized += (_, _) => DarkTitle();
        ThemeManager.Changed += OnTheme;
        Closed += (_, _) => ThemeManager.Changed -= OnTheme;
    }
    void OnTheme() { ThemeManager.ApplyCard(card, Region); DarkTitle(); }
    /// <summary>Takes the panel out (it is being docked elsewhere).</summary>
    public void Detach() { if (card.Child == content) card.Child = null; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    void DarkTitle()
    {
        try
        {
            var bg = (TryFindResource("B.Surf.Win") as SolidColorBrush)?.Color ?? Colors.Black;
            int dark = (bg.R * 0.299 + bg.G * 0.587 + bg.B * 0.114) < 128 ? 1 : 0;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch { }
    }
}
