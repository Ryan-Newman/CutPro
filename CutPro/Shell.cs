using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// VS Code-style shell around the editor: title-bar menus + command center, activity bar / side views, animated
/// panel show/hide, persisted layout, status bar, command palette (Ctrl+Shift+P) and editor colour themes with a
/// cross-fade (and live preview while picking a theme in the palette).
/// </summary>
public partial class MainWindow
{
    record Cmd(string Name, string Keys, Action Run);
    enum PaletteMode { Commands, Theme }

    AppSettings S => AppSettings.Current;
    string sideView = "Project";
    bool sideVisible = true, inspVisible = true;
    PaletteMode palMode;
    string? themeBeforePreview;
    bool palCommitted, galleryBusy;
    ContextMenu? lastMenu;   // (diagnostics: --uishot renders it)

    void InitShell()
    {
        SideCol.Width = new GridLength(S.SideBarWidth);
        InspCol.Width = new GridLength(S.InspectorWidth);
        PanelRow.Height = new GridLength(S.TimelineHeight);
        InitLoc();
        BuildShortcuts();
        var forceView = Environment.GetEnvironmentVariable("CUTPRO_SIDEVIEW");          // tests: e.g. measure playback with the mixer showing (applied after the docks)
        ShowSideView(forceView ?? S.SideView, animate: false);
        if (!S.SideBarVisible && forceView == null) SetSideBar(false, animate: false);
        if (!S.InspectorVisible) SetInspector(false, animate: false);
        inkScope = InspCard;
        ApplyCards(); Tl.ReloadTheme();                     // the timeline reads its card's palette
        BuildThemeCards();
        BuildStyleGallery();
        InitEffects();
        InitMixer();
        InitTracks();
        InitMarkers();
        InitBins(); InitAutoSave(); InitTranscript(); InitHigh(); InitTrimUi();
        InitSpeed(); InitStab();
        InitDock();                                          // (panels where the saved layout / workspace puts them)
        if (forceView != null) ShowSideView(forceView);
        InitScopes();
        InitAmbience();
        UpdatePlayArt();
        ThemeManager.Changed += () => { ApplyCards(); UpdatePlayArt(); amb?.Apply(ThemeManager.Current.Name, S.ThemeEffects); Tl.ReloadTheme(); Ph.InvalidateVisual(); Kfe.InvalidateVisual(); BuildInspector(); BuildThemeCards(); UpdateStatus(); };
        PreviewMouseDown += (_, e) =>
        {
            if (Palette.Visibility == Visibility.Visible && e.OriginalSource is DependencyObject d && !Palette.IsAncestorOf(d)) ClosePalette();
        };
        UpdateStatus();
    }

    // ---------------- window chrome ----------------
    void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object s, RoutedEventArgs e) => Close();

    // ---------------- theme ambience ----------------
    Ambience? amb;
    void InitAmbience()
    {
        amb = new Ambience(AmbientLayer, () => new[] { TitleCard, ActivityCard, SideCard, EditorCard, PanelCard, InspCard, StatusCard }, PanelCard, SelectedClipRect);
        double ambT = double.NaN; long ambMoved = 0;
        amb.Playing = () =>
        {   // creatures keep still while the picture is busy: playing, scrubbing / stepping (playhead moved lately), dragging
            if (ed.Time != ambT) { ambT = ed.Time; ambMoved = Environment.TickCount64; }
            return playing || Environment.TickCount64 - ambMoved < 600 || System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed;
        };
        AmbienceChk.IsChecked = S.ThemeEffects;
        if (Environment.GetEnvironmentVariable("CUTPRO_AMBIENCE") == "0") S.ThemeEffects = false;      // test runs: measure without
        Loaded += (_, _) => amb.Apply(ThemeManager.Current.Name, S.ThemeEffects);
    }
    void Ambience_Click(object s, RoutedEventArgs e)
    {
        S.ThemeEffects = AmbienceChk.IsChecked == true; AppSettings.Save();
        amb?.Apply(ThemeManager.Current.Name, S.ThemeEffects);
    }
    /// <summary>The selected clip's box on the timeline (visible part), in the ambience layer's coordinates.</summary>
    Rect? SelectedClipRect()
    {
        if (ed.Selected is not { } c || !Tl.IsVisible) return null;
        var r = Tl.ClipRect(c);
        if (r is not { } rr || rr.Width < 24) return null;
        try { return Tl.TransformToVisual(AmbientLayer).TransformBounds(rr); } catch { return null; }
    }
    /// <summary>Material themes: each panel card gets its own palette (dark text on light stone / metal / sand); the caption
    /// (drag) area covers the taller title row.</summary>
    void ApplyCards()
    {
        foreach (var c in new[] { TitleCard, ActivityCard, SideCard, EditorCard, PanelCard, InspCard, StatusCard }) ThemeManager.ApplyCard(c, c.Region);
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = (36 + ThemeManager.Current.Gap) * Math.Clamp(S.TextScale, 1, 1.5);
    }

    void Window_StateChanged(object? s, EventArgs e)
    {
        // a maximized custom-chrome window overhangs the screen by its resize border
        RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    // ---------------- menus ----------------
    static MenuItem MI(string header, string keys, Action run, bool isChecked = false, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, InputGestureText = keys, IsChecked = isChecked, IsEnabled = enabled };
        mi.Click += (_, _) => run();
        return mi;
    }
    void Menu_Click(object sender, RoutedEventArgs e)
    {
        var b = (Button)sender;
        var m = lastMenu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
        void Add(params object[] items) { foreach (var i in items) m.Items.Add(i is string ? new Separator() : i); }
        switch ((string)b.Tag)
        {
            case "File":
                Add(MI("Import Media…", K("file.import"), () => Import_Click(this, e)), MI("Import Sequence (XML, OTIO, EDL)…", "", () => _ = ImportSequenceAsync()),
                    MI("Open Project…", K("file.open"), () => Open_Click(this, e)),
                    MI("Save Project", K("file.save"), () => Save_Click(this, e)), MI("Auto-Save Versions…", K("file.autosaves"), AutoSaveVersions), "-",
                    MI("New Bin", K("bin.new"), () => RunCommand("bin.new")), NewItemMenu(),
                    MI("Relink Offline Media…", "", () => RelinkOffline(), enabled: ed.Media.Any(m => m.Offline)),
                    MI("Collect Files…", K("file.collect"), () => _ = CollectFilesAsync(), enabled: ed.Media.Count > 0), "-",
                    MI("Record Voice-over…", K("audio.voiceover"), () => VoiceOverDialog()), "-",
                    MI("Export…", K("file.export"), () => Export_Click(this, e)), MI("Render Queue", "", () => ShowDock("Queue")),
                    MI("Export EDL…", K("file.exportEdl"), () => ExportInterchange(false), enabled: ed.Clips.Count > 0),
                    MI("Export OpenTimelineIO…", K("file.exportOtio"), () => ExportInterchange(true), enabled: ed.Clips.Count > 0),
                    MI("Publish…", K("file.publish"), () => OpenPublish(null)), "-",
                    MI("Exit", "Alt+F4", Close));
                break;
            case "Sequence":
            {
                double a = Editor.W / (double)Editor.H;
                Add(MI("New Sequence…", "Ctrl+N", NewSequence), MI("Duplicate Sequence", "", () => ed.DuplicateSequence(ed.Seq)),
                    MI("Rename Sequence…", "", () => RenameSeq(ed.Seq)), MI("Delete Sequence…", "", () => DeleteSeq(ed.Seq), enabled: ed.Sequences.Count > 1));
                if (ed.Sequences.Count > 1)
                {
                    var go = new MenuItem { Header = "Open Sequence" };
                    foreach (var sq in ed.Sequences) go.Items.Add(MI(sq.Name, "", () => OpenSequence(sq), sq == ed.Seq));
                    Add(go);
                }
                var gap = new MenuItem { Header = "Go to Gap" };
                foreach (var (id, h) in new[] { ("gap.next", "Next in Sequence"), ("gap.prev", "Previous in Sequence"), ("gap.nextTrack", "Next in Track"), ("gap.prevTrack", "Previous in Track") })
                    gap.Items.Add(MI(h, K(id), () => RunCommand(id)));
                var zoom = new MenuItem { Header = "Zoom" };
                foreach (var (id, h) in new[] { ("tl.zoomIn", "Zoom In"), ("tl.zoomOut", "Zoom Out"), ("tl.zoomSeq", "Zoom to Sequence"), ("tl.zoomSel", "Zoom to Selection") })
                    zoom.Items.Add(MI(h, K(id), () => RunCommand(id)));
                Add("-", gap, zoom, LayoutMenu());
                Add("-", MI("Audio Scrubbing", K("tl.audioScrub"), () => RunCommand("tl.audioScrub"), S.AudioScrub),
                    MI("Audio Time Units", K("tl.audioUnits"), ToggleAudioUnits, S.AudioUnits));
                Add("-", MI("Add Tracks…", "", () => AddTracksDialog()), MI("Delete Empty Tracks", "", DeleteEmptyTracks),
                    "-", MI("Add Adjustment Layer", "", AddAdjustment), MI("Nest…", "", NestSelected, enabled: ed.Sel.Count > 0),
                    MI("Open Nested Sequence", "", () => { if (ed.Selected is { } nc) OpenNested(nc); }, enabled: ed.Selected is { } sc && ed.SeqOf(sc) != null), "-",
                    MI("Create Multi-Camera Source Sequence…", K("mc.create"), () => _ = CreateMultiCamDialog(), enabled: SelectedBinMedia().Count > 1),
                    MI("Multi-Camera View", K("view.multicam"), ToggleMultiCamView, mcOn), "-",
                    MI("Sequence Settings…", "", SequenceSettings), "-",
                    MI("Landscape 16:9", "", () => Format_Click(Fmt169, e), Math.Abs(a - 16 / 9.0) < 0.02),
                    MI("Shorts 9:16 (YouTube Shorts, TikTok, Reels)", "", () => Format_Click(Fmt916, e), Math.Abs(a - 9 / 16.0) < 0.02),
                    MI("Square 1:1", "", () => Format_Click(Fmt11, e), Math.Abs(a - 1) < 0.02),
                    MI("Portrait 4:5", "", () => Format_Click(Fmt45, e), Math.Abs(a - 0.8) < 0.02), "-",
                    MI("Layout: Fill (crop)", "", () => ed.ApplySequence(Editor.OutW, Editor.OutH, TimeUtil.Fps, ReframeMode.Fill), Editor.Reframe == ReframeMode.Fill),
                    MI("Layout: Fit + blurred background", "", () => ed.ApplySequence(Editor.OutW, Editor.OutH, TimeUtil.Fps, ReframeMode.FitBlur), Editor.Reframe == ReframeMode.FitBlur),
                    MI("Layout: Fit (bars)", "", () => ed.ApplySequence(Editor.OutW, Editor.OutH, TimeUtil.Fps, ReframeMode.Fit), Editor.Reframe == ReframeMode.Fit), "-",
                    MI("Auto Reframe", "", () => AutoReframe_Click(this, e), enabled: Editor.Reframe == ReframeMode.Fill), "-",
                    MI($"Apply Video Transition ({Transitions.DefaultVideo})", "Ctrl+D", () => ApplyTransitions(true, false)),
                    MI($"Apply Audio Transition ({Transitions.DefaultAudio})", "Ctrl+Shift+D", () => ApplyTransitions(false, true)),
                    MI("Apply Default Transitions to Selection", "Shift+D", () => ApplyTransitions(true, true)),
                    MI("Effects (transitions)…", "", () => ShowSideView("Effects")), "-",
                    MI("Speed/Duration…", "Ctrl+R", () => SpeedDialog(), enabled: ed.Sel.Count > 0),
                    MI("Add Frame Hold", "", () => FrameHold(false), enabled: HoldTarget() != null),
                    MI("Insert Frame Hold Segment", "", () => FrameHold(true), enabled: HoldTarget() != null), "-",
                    MI("Audio Track Mixer…", "", () => ShowSideView("Mixer")),
                    MI("Match Loudness of Selected Clips (−14 LUFS)", "", () => _ = MatchLoudnessAsync()));
                break;
            }
            case "Clip":
                ClipMenu(items => Add(items), (h, k, a, c, en) => MI(h, k, a, c, en));
                break;
            case "Publish":
            {
                Add(MI("Publish…", "Ctrl+Shift+U", () => OpenPublish(null)), MI("Thumbnail Studio…", "", () => OpenThumbnails()), MI("Intro & Hook…", "", () => OpenHook()), "-");
                foreach (var p in Platforms.All)
                {
                    var acc = Accounts.Get(p.Id);
                    string state = p.Upload == "intent" ? "" : acc.Connected ? "  ✓" : "";
                    Add(MI(p.Name + "…" + state, "", () => OpenPublish(null, p.Id)));
                }
                Add("-", MI("Accounts & Upload Settings…", "", () => AccountsDialog(null)));
                var auto = new MenuItem { Header = "Auto-upload after export" };
                foreach (var p in Platforms.All.Where(p => p.Upload == "api"))
                {
                    var acc = Accounts.Get(p.Id);
                    var mi = new MenuItem { Header = p.Name + (acc.Connected ? "" : "  (not connected)"), IsCheckable = true, IsChecked = acc.AutoUpload, StaysOpenOnClick = true };
                    mi.Click += (_, _) =>
                    {
                        if (!acc.Connected && mi.IsChecked) { mi.IsChecked = false; AccountsDialog(p.Id); return; }
                        acc.AutoUpload = mi.IsChecked; Accounts.Save();
                        SetTask(mi.IsChecked ? $"Every export will upload to {p.Name} (with this project's {p.Name} details)" : $"Auto-upload to {p.Name} off", 4000);
                    };
                    auto.Items.Add(mi);
                }
                Add(auto);
                break;
            }
            case "Edit":
            {
                bool sel = ed.Sel.Count > 0;
                string Step(string what, string? label) => Loc.T(what) + (label != null ? " " + Loc.T(label) : "");
                Add(MI(Step("Undo", ed.UndoLabels.FirstOrDefault()), K("edit.undo"), ed.Undo, enabled: ed.UndoLabels.Any()),
                    MI(Step("Redo", ed.RedoLabels.FirstOrDefault()), K("edit.redo"), ed.Redo, enabled: ed.RedoLabels.Any()),
                    MI("History", K("view.history"), () => ShowDock("History")), "-",
                    MI("Cut", K("edit.cut"), () => CopyClips(true), enabled: sel), MI("Copy", K("edit.copy"), () => CopyClips(false), enabled: sel),
                    MI("Paste", K("edit.paste"), () => PasteClips(false), enabled: Editor.CanPaste), MI("Paste Insert", K("edit.pasteInsert"), () => PasteClips(true), enabled: Editor.CanPaste),
                    MI("Paste Attributes…", K("edit.pasteAttr"), PasteAttributesDialog, enabled: Editor.CanPaste && sel),
                    MI("Remove Attributes…", K("edit.removeAttr"), RemoveAttributesDialog, enabled: sel), "-",
                    MI("Add Edit", K("edit.addEdit"), () => ed.AddEdit(false)), MI("Add Edit to All Tracks", K("edit.addEditAll"), () => ed.AddEdit(true)),
                    MI("Delete", K("edit.delete"), () => ed.DeleteSelection(false)), MI("Ripple Delete", K("edit.rippleDelete"), () => ed.DeleteSelection(true)), "-",
                    MI("Select All", K("edit.selectAll"), ed.SelectAll), MI("Deselect All", K("edit.deselect"), ed.ClearSelection), MI("Link / Unlink", K("edit.link"), ed.ToggleLink),
                    MI("Group", K("clip.group"), () => RunCommand("clip.group"), enabled: ed.Sel.Count > 1), MI("Ungroup", K("clip.ungroup"), () => RunCommand("clip.ungroup"), enabled: sel), "-",
                    MI("Mark In", K("edit.markIn"), () => ed.SetMark(true)), MI("Mark Out", K("edit.markOut"), () => ed.SetMark(false)),
                    MI("Lift", K("edit.lift"), () => ed.LiftExtract(false)), MI("Extract", K("edit.extract"), () => ed.LiftExtract(true)), "-",
                    MI("Insert from Source", K("src.insert"), () => SourceEdit(true)), MI("Overwrite from Source", K("src.overwrite"), () => SourceEdit(false)), MI("Match Frame", K("src.matchFrame"), MatchFrame),
                    MI("Reveal in Project", K("clip.reveal"), () => RevealInProject(), enabled: ed.Selected != null), "-",
                    MI("Keyboard Shortcuts…", "", () => ShowSideView("Settings")));
                break;
            }
            case "Marker":
            {
                bool clipHere = ed.Clips.Any(c => ed.IsSel(c) && ed.Time >= c.Start && ed.Time < c.End);
                Add(MI("Add Marker", K("marker.add"), AddMarkerAtPlayhead), MI("Add Clip Marker", K("marker.addClip"), AddClipMarkerAtPlayhead, enabled: clipHere),
                    MI("Edit Marker…", K("marker.edit"), EditMarkerAtPlayhead, enabled: MarkerAtPlayhead() != null), "-",
                    MI("Go to Next Marker", K("marker.next"), () => RunCommand("marker.next")), MI("Go to Previous Marker", K("marker.prev"), () => RunCommand("marker.prev")), "-",
                    MI("Clear All Markers", K("marker.clear"), ed.ClearMarkers, enabled: ed.AllMarkers().Any()), "-",
                    MI("Copy YouTube Chapters", K("marker.chapters"), CopyChapters, enabled: ed.Markers.Any(m => m.Chapter)));
                break;
            }
            case "View":
                Add(MI("Command Palette…", "Ctrl+Shift+P", () => OpenPalette()), "-",
                    MI("Project", "", () => ShowSideView("Project"), Showing("Project")),
                    MI("Titles", "", () => ShowSideView("Titles"), Showing("Titles")),
                    MI("Effects", "", () => ShowSideView("Effects"), Showing("Effects")),
                    MI("Audio Track Mixer", "", () => ShowSideView("Mixer"), Showing("Mixer")),
                    MI("Lumetri Scopes", "", () => ShowSideView("Scopes"), Showing("Scopes")),
                    MI("Captions", "", () => ShowSideView("Captions"), Showing("Captions")),
                    MI("Transcript", K("view.transcript"), () => ShowDock("Transcript"), Showing("Transcript")),
                    MI("History", K("view.history"), () => ShowDock("History"), Showing("History")),
                    MI("Settings", "", () => ShowSideView("Settings"), sideVisible && sideView == "Settings"),
                    MI("Side Bar", "Ctrl+B", () => SetSideBar(!sideVisible), sideVisible),
                    MI("Effect Controls", "Ctrl+Alt+B", () => { if (DockOf("Inspector") is { Zone: "Right" }) SetInspector(!inspVisible); else ShowDock("Inspector"); }, Showing("Inspector")), "-",
                    new MenuItem { Header = "Color Theme", IsEnabled = false });
                foreach (var th in ThemeManager.All) Add(MI(th.Name, "", () => SetEditorTheme(th.Name), ThemeManager.Current == th));
                break;
            case "Window":
                WindowMenu(items => Add(items), (h, k, a, c, en) => MI(h, k, a, c, en));
                break;
            case "Help":
                Add(MI("Keyboard Shortcuts", "", () => ShowSideView("Settings")), MI("Command Palette…", K("view.palette"), () => OpenPalette()), "-",
                    MI("About CutPro", "", () => Alert.Show(this, "A Premiere-style video editor.\nWPF · .NET 9 · ffmpeg · ONNX Runtime (MobileSAM)", "About CutPro")));
                break;
        }
        m.IsOpen = true;
    }

    // ---------------- activity bar / side bar / panels ----------------
    void Activity_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        if (sideVisible && sideView == tag) SetSideBar(false);   // clicking the active view collapses the side bar (VS Code)
        else ShowSideView(tag);
    }

    /// <summary>Shows a side view / panel wherever it is docked (DockUi); Settings always lives in the left side bar.</summary>
    void ShowSideView(string view, bool animate = true)
    {
        if (docks.Count == 0) { sideView = view; return; }                    // (before the docks exist: InitDock applies the layout)
        ShowDock(view is "Settings" || DockOf(view) != null ? view : "Project");
    }

    void CheckActivity()
    {
        ActSettings.IsChecked = sideView == "Settings";
        foreach (var d in docks) if (d.Act != null) d.Act.IsChecked = sideView == d.Id && d.Zone == "Left";
    }

    void SetSideBar(bool show, bool animate = true)
    {
        sideVisible = show; S.SideBarVisible = show;
        AnimateLength(SideCol, show ? Math.Max(160, S.SideBarWidth) : 0, animate);
        SideSash.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) { ActSettings.IsChecked = false; foreach (var d in docks) if (d.Act != null) d.Act.IsChecked = false; }
        else CheckActivity();
        AppSettings.Save();
    }

    void SetInspector(bool show, bool animate = true)
    {
        inspVisible = show; S.InspectorVisible = show;
        AnimateLength(InspCol, show ? Math.Max(240, S.InspectorWidth) : 0, animate);
        InspSash.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AppSettings.Save();
    }
    void HideInspector_Click(object s, RoutedEventArgs e) => SetInspector(false);

    static void AnimateLength(ColumnDefinition col, double to, bool animate)
    {
        double from = col.ActualWidth;
        col.BeginAnimation(ColumnDefinition.WidthProperty, null);
        col.Width = new GridLength(to);
        if (!animate || Math.Abs(from - to) < 1) return;
        var a = new GridLengthAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(190), FillBehavior = FillBehavior.Stop,
                                          Easing = new CubicEase { EasingMode = EasingMode.EaseOut } };
        col.BeginAnimation(ColumnDefinition.WidthProperty, a);
    }

    void Sash_DragCompleted(object s, DragCompletedEventArgs e)
    {
        if (s == SideSash)
        {
            if (SideCol.ActualWidth < 120) { SetSideBar(false); return; }   // dragged nearly shut: collapse, like VS Code
            S.SideBarWidth = SideCol.ActualWidth;
        }
        else if (s == InspSash)
        {
            if (InspCol.ActualWidth < 160) { SetInspector(false); return; }
            S.InspectorWidth = InspCol.ActualWidth;
        }
        else S.TimelineHeight = PanelRow.ActualHeight;
        AppSettings.Save();
    }

    // ---------------- titles view ----------------
    void AddRole_Click(object sender, RoutedEventArgs e) => ed.AddTitle(Enum.Parse<TextRole>((string)((FrameworkElement)sender).Tag));

    void BuildStyleGallery()
    {
        galleryBusy = true;
        StyleGallery.Items.Clear();
        AddGraphicsToGallery();
        foreach (var ns in StyleLibrary.All)
        {
            var sp = new StackPanel { Tag = ns, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18)), CornerRadius = new CornerRadius(4), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left,
                Child = new Image { Source = StyleThumb(ns.Style, 216), Width = 216, Height = 61 }
            });
            sp.Children.Add(new TextBlock { Text = ns.Name + (ns.BuiltIn ? "" : "  (mine)"), FontSize = 11.5, Margin = new Thickness(2, 3, 0, 0) });
            StyleGallery.Items.Add(sp);
        }
        galleryBusy = false;
    }

    void StyleGallery_Changed(object s, SelectionChangedEventArgs e)
    {
        if (galleryBusy) return;
        if (StyleGallery.SelectedItem is FrameworkElement { Tag: GraphicTemplate gt }) AddGraphic(gt);
        else if (StyleGallery.SelectedItem is FrameworkElement { Tag: NamedStyle ns })
        {
            if (!ed.Clips.Any(c => c.Kind == ClipKind.Text && ed.IsSel(c))) ed.AddTitle(TextRole.Title);
            ed.ApplyStyle(ns.Style);
        }
        else return;
        galleryBusy = true; StyleGallery.SelectedIndex = -1; galleryBusy = false;
    }

    // ---------------- editor themes ----------------
    void SetEditorTheme(string name, bool animate = true)
    {
        if (animate && IsLoaded && ThemeManager.Current.Name != name) Crossfade(() => ThemeManager.Apply(name));
        else if (ThemeManager.Current.Name != name) ThemeManager.Apply(name);
        S.Theme = ThemeManager.Current.Name;
        AppSettings.Save();
    }

    /// <summary>Snapshot the window, apply the change underneath, fade the snapshot out.</summary>
    void Crossfade(Action change)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)(RootGrid.ActualWidth * dpi.DpiScaleX), h = (int)(RootGrid.ActualHeight * dpi.DpiScaleY);
        if (w < 2 || h < 2) { change(); return; }
        var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(RootGrid);
        var img = new Image { Source = rtb, Stretch = Stretch.Fill, IsHitTestVisible = false };
        FadeHost.Children.Add(img);
        change();
        var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        a.Completed += (_, _) => FadeHost.Children.Remove(img);
        img.BeginAnimation(OpacityProperty, a);
    }

    /// <summary>Settings view: one card per theme with a miniature of its layout — the theme's real surfaces (textures),
    /// rounded floating panels and gaps for the material themes. Built when the Settings view is showing.</summary>
    bool themeCardsStale = true;
    void BuildThemeCards()
    {
        if (SettingsView.Visibility != Visibility.Visible) { themeCardsStale = true; return; }
        themeCardsStale = false;
        ThemeCards.Children.Clear();
        foreach (var th in ThemeManager.All)
        {
            Brush P(string k) => ThemeManager.BrushOf(th, k);
            Brush Ink(string region, string k) => ThemeManager.CardPalette(th, region) is { } pal && pal.TryGetValue(k, out var v) ? ThemeManager.MakeBrush(v) : P(k);
            double r = th.Material ? 3.5 : 0, g = th.Material ? 1.5 : 0;
            var mini = new Grid { Height = 92, ClipToBounds = true, Background = P("Surf.Win") };
            mini.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            mini.RowDefinitions.Add(new RowDefinition());
            mini.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            mini.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            foreach (var w in new[] { 13.0, 50, -1, 44 }) mini.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
            Border Panel(string surf, int row, int col, int rs = 1, int cs = 1)
            {
                var b = new Border { Background = P(surf), CornerRadius = new CornerRadius(r), Margin = new Thickness(g), ClipToBounds = true };
                if (th.Material) { b.BorderBrush = P("Card.Edge"); b.BorderThickness = new Thickness(0.6); }
                Grid.SetRow(b, row); Grid.SetColumn(b, col); Grid.SetRowSpan(b, rs); Grid.SetColumnSpan(b, cs); mini.Children.Add(b);
                return b;
            }
            Panel("Surf.Title", 0, 0, 1, 4);
            Panel("Surf.Activity", 1, 0, 2);
            var side = new StackPanel();
            for (int i = 0; i < 5; i++) side.Children.Add(new Border { Height = 4, Margin = new Thickness(5, 5, 8 + i * 5, 0), CornerRadius = new CornerRadius(2), Background = i == 1 ? P("Accent") : Ink("Side", "Fg.Dim"), Opacity = i == 1 ? 0.9 : 0.7 });
            Panel("Surf.Side", 1, 1, 2).Child = side;
            Panel("Surf.Editor", 1, 2).Child = new Border { Margin = new Thickness(9, 6, 9, 6), Background = Brushes.Black, CornerRadius = new CornerRadius(1.5) };
            var clips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            foreach (var (k, wdt) in new[] { ("Clip.Video", 30.0), ("Clip.Text", 18.0), ("Clip.Audio", 26.0) })
                clips.Children.Add(new Border { Width = wdt, Height = 8, Margin = new Thickness(0, 0, 3, 0), CornerRadius = new CornerRadius(2), Background = P(k) });
            Panel("Surf.Panel", 2, 2).Child = clips;
            var insp = new StackPanel();
            for (int i = 0; i < 6; i++) insp.Children.Add(new Border { Height = 3, Margin = new Thickness(5, 5, 6 + (i % 3) * 6, 0), CornerRadius = new CornerRadius(1.5), Background = i % 3 == 0 ? Ink("Insp", "Heading") : Ink("Insp", "Fg.Dim"), Opacity = 0.75 });
            Panel("Surf.Insp", 1, 3, 2).Child = insp;
            Panel("Surf.Status", 3, 0, 1, 4);

            bool current = ThemeManager.Current == th;
            var name = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 6, 0, 0) };
            name.Children.Add(new TextBlock { Text = th.Name, FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal });
            if (current) name.Children.Add(new TextBlock { Text = "  ", FontFamily = (FontFamily)FindResource("IconFont"), VerticalAlignment = VerticalAlignment.Center });
            var card = new StackPanel();
            card.Children.Add(new Border { CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = mini });
            card.Children.Add(name);
            card.Children.Add(new TextBlock { Text = th.Description, FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(2, 1, 0, 0) });
            var btn = new Button { Content = card, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                                   BorderThickness = new Thickness(current ? 2 : 1), ToolTip = th.Name };
            btn.SetResourceReference(BorderBrushProperty, current ? "B.Accent" : "B.Border");
            btn.SetResourceReference(BackgroundProperty, "B.Strip");
            btn.Click += (_, _) => SetEditorTheme(th.Name);
            ThemeCards.Children.Add(btn);
        }
    }

    // ---------------- status bar ----------------
    void UpdateStatus()
    {
        if (!ready) return;
        var project = projectPath != null ? Path.GetFileNameWithoutExtension(projectPath) : "Untitled";
        StatusProjectText.Text = project;
        CommandCenterText.Text = "CutPro — " + project;
        ProgramTabSub.Text = "  " + ed.Seq.Name;
        if (IsLoaded) FitEditorTools();
        int sel = ed.Clips.Count(ed.IsSel);
        StatusSelText.Text = ed.Gap is { } g ? $"Gap {g.B - g.A:0.00}s selected"
                           : sel == 0 ? $"{ed.Clips.Count} clip{(ed.Clips.Count == 1 ? "" : "s")}" : $"{sel} of {ed.Clips.Count} selected";
        StatusSnapText.Text = "Snap: " + (SnapChk.IsChecked == true ? "On" : "Off");
        StatusResText.Text = "Playback: " + ((ResCombo.SelectedItem as ComboBoxItem)?.Content ?? "Full");
        StatusThemeText.Text = ThemeManager.Current.Name;
        StatusTimeText.Text = $"{TimeUtil.Format(ed.Time)} / {TimeUtil.Format(ed.End)}";
        BinEmpty.Visibility = ed.Media.Count == 0 && ed.Sequences.Count < 2 ? Visibility.Visible : Visibility.Collapsed;
    }
    void StatusSel_Click(object s, RoutedEventArgs e) { if (ed.Selected is { } c) ed.Seek(c.Start); }
    void StatusSnap_Click(object s, RoutedEventArgs e) => SnapChk.IsChecked = SnapChk.IsChecked != true;
    void StatusRes_Click(object s, RoutedEventArgs e) => ResCombo.SelectedIndex = (ResCombo.SelectedIndex + 1) % 3;
    void StatusTheme_Click(object s, RoutedEventArgs e) => OpenPalette(PaletteMode.Theme);

    // ---------------- command palette ----------------
    void CommandCenter_Click(object s, RoutedEventArgs e) => OpenPalette();

    List<Cmd> Commands()
    {
        var r = new RoutedEventArgs();
        var more = new List<Cmd>
        {
            new("File: Import Media…", "Ctrl+I", () => Import_Click(this, r)),
            new("File: Open Project…", "Ctrl+O", () => Open_Click(this, r)),
            new("File: Save Project", "Ctrl+S", () => Save_Click(this, r)),
            new("File: Export MP4…", "Ctrl+E", () => Export_Click(this, r)),
            new("Edit: Undo", "Ctrl+Z", ed.Undo), new("Edit: Redo", "Ctrl+Y", ed.Redo),
            new("Edit: Add Edit (cut at playhead)", "Ctrl+K", () => ed.AddEdit(false)),
            new("Edit: Add Edit to All Tracks", "Ctrl+Shift+K", () => ed.AddEdit(true)),
            new("Edit: Delete", "Del", () => ed.DeleteSelection(false)), new("Edit: Ripple Delete", "Shift+Del", () => ed.DeleteSelection(true)),
            new("Edit: Select All", "Ctrl+A", ed.SelectAll), new("Edit: Deselect All", "Esc", ed.ClearSelection),
            new("Edit: Link / Unlink Clips", "Ctrl+L", ed.ToggleLink),
            new("Sequence: Settings…", "", SequenceSettings), new("Sequence: Shorts 9:16", "", () => Format_Click(Fmt916, r)), new("Sequence: Landscape 16:9", "", () => Format_Click(Fmt169, r)),
            new("Sequence: Auto Reframe", "", () => AutoReframe_Click(this, r)),
            new("File: Publish…", "Ctrl+Shift+U", () => OpenPublish(null)), new("Publish: Accounts & Upload Settings…", "", () => AccountsDialog(null)),
            new("Object: Select Object (AI click)", "", () => SetObjectTool(ObjectTool.AI)),
            new("Object: Ellipse Mask", "", () => SetObjectTool(ObjectTool.Ellipse)), new("Object: Rectangle Mask", "", () => SetObjectTool(ObjectTool.Rectangle)),
            new("Object: Freehand Mask", "", () => SetObjectTool(ObjectTool.Freehand)),
            new("Object: Track Forward", "", () => TrackBtn_Click(new FrameworkElement { Tag = "1" }, r)),
            new("Object: Track Backward", "", () => TrackBtn_Click(new FrameworkElement { Tag = "-1" }, r)),
            new("Object: Track Both Ways", "", () => TrackBtn_Click(new FrameworkElement { Tag = "0" }, r)),
            new("Object: Add / Remove Keyframe at Playhead", "", () => { if (ActiveMask(out var c) is { } m && c != null) ToggleKeyAtPlayhead(c, m); }),
            new("Object: Previous Keyframe", "", () => { if (ActiveMask(out var c) is { } m && c != null) GoToKey(c, m, -1); }),
            new("Object: Next Keyframe", "", () => { if (ActiveMask(out var c) is { } m && c != null) GoToKey(c, m, 1); }),
            new("Edit: Mark In", "I", () => ed.SetMark(true)), new("Edit: Mark Out", "O", () => ed.SetMark(false)), new("Edit: Clear Marks", "X", ed.ClearMarks),
            new("Edit: Lift In–Out", ";", () => ed.LiftExtract(false)), new("Edit: Extract In–Out", "'", () => ed.LiftExtract(true)),
            new("Edit: Ripple Trim Previous Edit to Playhead", "Q", () => ed.RippleTrim(true)),
            new("Edit: Ripple Trim Next Edit to Playhead", "W", () => ed.RippleTrim(false)),
            new("Source: Insert at the Playhead", ",", () => SourceEdit(true)), new("Source: Overwrite at the Playhead", ".", () => SourceEdit(false)),
            new("Source: Match Frame", "F", MatchFrame), new("Source: Show the Source Monitor", "", () => ShowDock("Source")),
            new("Source: Open the Selected Clip's Media", "", () => { if (ed.Selected is { } sc && ed.MediaOf(sc) is { Seq: null } sm) OpenSource(sm, TimeMap.Src(sc, Math.Clamp(ed.Time, sc.Start, sc.End - 1e-6))); }),
            new("Playback: Play / Pause", "Space", TogglePlay), new("Playback: Go to Start", "Home", () => ed.Seek(0)),
            new("Playback: Go to End", "End", () => ed.Seek(ed.End)),
            new("Playback: Resolution Full", "", () => ResCombo.SelectedIndex = 0), new("Playback: Resolution 1/2", "", () => ResCombo.SelectedIndex = 1),
            new("Playback: Resolution 1/4", "", () => ResCombo.SelectedIndex = 2),
            new("Tools: Selection Tool", "V", () => SetTool(false)), new("Tools: Razor Tool", "C", () => SetTool(true)),
            new("Tools: Ripple Edit Tool", "B", () => SetTool(TrimTool.Ripple)), new("Tools: Rolling Edit Tool", "N", () => SetTool(TrimTool.Roll)),
            new("Tools: Rate Stretch Tool", "R", () => SetTool(TrimTool.Rate)), new("Tools: Slip Tool", "Y", () => SetTool(TrimTool.Slip)), new("Tools: Slide Tool", "U", () => SetTool(TrimTool.Slide)),
            new("Timeline: Toggle Snapping", "S", () => SnapChk.IsChecked = SnapChk.IsChecked != true),
            new("Title: Add Title", "", () => ed.AddTitle(TextRole.Title)), new("Title: Add Subtitle", "", () => ed.AddTitle(TextRole.Subtitle)),
            new("Title: Add Lower Third", "", () => ed.AddTitle(TextRole.LowerThird)), new("Title: Add Caption", "", () => ed.AddTitle(TextRole.Caption)),
            new("View: Toggle Side Bar", "Ctrl+B", () => SetSideBar(!sideVisible)),
            new("View: Toggle Effect Controls", "Ctrl+Alt+B", () => SetInspector(!inspVisible)),
            new("View: Show Project", "", () => ShowSideView("Project")), new("View: Show Titles", "", () => ShowSideView("Titles")),
            new("View: Show Settings", "", () => ShowSideView("Settings")),
            new("View: Show Effects", "", () => ShowSideView("Effects")), new("View: Show Audio Track Mixer", "", () => ShowSideView("Mixer")),
            new("View: Show Lumetri Scopes", "", () => ShowSideView("Scopes")),
            new("View: Full-Screen Playback", "F11", ToggleFullScreen), new("View: Maximize Panel Under the Mouse", "`", () => ToggleMaximizePanel()),
            new("Window: Program Monitor in a New Window", "", () => MoveDock("Program", "Float")), new("Window: Timeline in a New Window", "", () => MoveDock("Timeline", "Float")),
            new("Window: Save as New Workspace…", "", SaveWorkspaceAs),
            new("Audio: Match Loudness of Selected Clips (−14 LUFS)", "", () => _ = MatchLoudnessAsync()),
            new("Publish: Thumbnail Studio…", "", () => OpenThumbnails()), new("Publish: Intro & Hook…", "", () => OpenHook()),
            new("Clip: Speed/Duration…", "Ctrl+R", () => SpeedDialog()), new("Clip: Add Frame Hold", "", () => FrameHold(false)),
            new("Clip: Insert Frame Hold Segment", "", () => FrameHold(true)),
            new("Preferences: Color Theme", "", () => Dispatcher.BeginInvoke(() => OpenPalette(PaletteMode.Theme))),
            new("Help: Keyboard Shortcuts", "", () => ShowSideView("Settings")),
        };
        // every command with a shortcut (its current keys), then the rest
        var list = Keymap.All.Select(k => new Cmd(k.Label, string.Join("  ", Keymap.Of(k.Id)), () => RunCommand(k.Id))).ToList();
        var names = list.Select(c => c.Name).ToHashSet();
        list.AddRange(more.Where(c => !names.Contains(c.Name)));
        foreach (var (p, name) in LayoutNames) list.Add(new($"Clip: Layout — {name}", "", () => ApplyLayout(p)));
        foreach (var m in new[] { "Page", "Smooth", "None" }) list.Add(new($"Preferences: Timeline Scrolling While Playing — {m}", S.PlaybackScroll == m ? "current" : "", () => SetPlaybackScroll(m)));
        foreach (var (code, name) in Loc.Languages) list.Add(new($"Preferences: Language — {name}", S.Language == code ? "current" : "", () => SetLanguage(code)));
        list.Add(new("File: Relink Offline Media…", "", () => RelinkOffline()));
        list.Add(new("Clip: Relink / Replace Media…", "", () => { if (ed.Selected is { } c && ed.MediaOf(c) is { Seq: null } m) RelinkMedia(m); }));
        list.Add(new("Clip: Media Properties…", "", () => { if (ed.Selected is { } c && ed.MediaOf(c) is { Seq: null } m) ShowProperties(m); }));
        foreach (var w in BuiltInWorkspaces.Concat(S.Workspaces.Keys)) list.Add(new($"Window: Workspace — {w}", "", () => UseWorkspace(w)));
        foreach (var th in ThemeManager.All) list.Add(new($"Preferences: Color Theme: {th.Name}", "", () => SetEditorTheme(th.Name)));
        foreach (var t in Themes.All) list.Add(new($"Title Theme: {t.Name}", "", () => ThemeCombo.SelectedItem = t.Name));
        foreach (var g in GraphicLibrary.All) list.Add(new($"Graphic: {g.Name}", "", () => AddGraphic(g)));
        list.Add(new("Sequence: Duplicate", "", () => ed.DuplicateSequence(ed.Seq)));
        list.Add(new("Sequence: Rename…", "", () => RenameSeq(ed.Seq)));
        list.Add(new("Sequence: Add Tracks…", "", () => AddTracksDialog()));
        list.Add(new("Sequence: Add Video Track (on top)", "", () => ed.AddTracks(1, 0, 0, Tracks.Count)));
        list.Add(new("Sequence: Add Audio Track (after the last)", "", () => ed.AddTracks(0, 0, 1, Tracks.Count)));
        list.Add(new("Sequence: Delete Empty Tracks", "", DeleteEmptyTracks));
        list.Add(new("Sequence: Delete…", "", () => DeleteSeq(ed.Seq)));
        list.Add(new("Clip: Nest…", "", NestSelected));
        list.Add(new("Sequence: Add Adjustment Layer", "", AddAdjustment));
        list.Add(new("Clip: Open Nested Sequence", "", () => { if (ed.Selected is { } c) OpenNested(c); }));
        foreach (var sq in ed.Sequences.Where(x => x != ed.Seq)) list.Add(new($"Sequence: Open “{sq.Name}”", "", () => OpenSequence(sq)));
        return list;
    }

    void OpenPalette(PaletteMode mode = PaletteMode.Commands)
    {
        palMode = mode; palCommitted = false;
        themeBeforePreview = ThemeManager.Current.Name;
        Palette.Visibility = Visibility.Visible;
        PaletteBox.Text = mode == PaletteMode.Commands ? ">" : "";
        PaletteHint.Text = mode == PaletteMode.Theme ? "Select Color Theme  (↑ ↓ to preview, Enter to keep, Esc to cancel)" : "Type a command — Enter to run, Esc to close";
        PaletteHint.Visibility = Visibility.Visible;
        FilterPalette();
        if (mode == PaletteMode.Theme)
            PaletteList.SelectedIndex = Array.FindIndex(ThemeManager.All, t => t == ThemeManager.Current);
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
        var slide = new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Palette.BeginAnimation(OpacityProperty, fade);
        PaletteShift.BeginAnimation(TranslateTransform.YProperty, slide);
        PaletteBox.Focus();
        PaletteBox.CaretIndex = PaletteBox.Text.Length;
    }

    void ClosePalette()
    {
        if (Palette.Visibility != Visibility.Visible) return;
        Palette.Visibility = Visibility.Collapsed;
        if (palMode == PaletteMode.Theme && !palCommitted && themeBeforePreview != null && ThemeManager.Current.Name != themeBeforePreview)
            ThemeManager.Apply(themeBeforePreview);   // Esc / click-away: undo the live preview
        Keyboard.ClearFocus();
        Focus();
    }

    void FilterPalette()
    {
        var q = PaletteBox.Text.TrimStart('>').Trim();
        var source = palMode == PaletteMode.Theme
            ? ThemeManager.All.Select(t => new Cmd(t.Name, ThemeManager.Current == t ? "current" : "", () => SetEditorTheme(t.Name, animate: false))).ToList()
            : Commands();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        PaletteList.Items.Clear();
        foreach (var c in source.Where(c => words.All(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || Loc.T(c.Name).Contains(w, StringComparison.OrdinalIgnoreCase))))
        {
            var g = new Grid { Tag = c };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = Loc.T(c.Name), VerticalAlignment = VerticalAlignment.Center });
            var keys = new TextBlock { Text = c.Keys, Opacity = 0.65, FontSize = 11.5, Margin = new Thickness(16, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(keys, 1); g.Children.Add(keys);
            PaletteList.Items.Add(new ListBoxItem { Content = g, Focusable = false });
        }
        if (PaletteList.Items.Count > 0 && palMode == PaletteMode.Commands) PaletteList.SelectedIndex = 0;
    }

    void PaletteBox_TextChanged(object s, TextChangedEventArgs e) { if (Palette.Visibility == Visibility.Visible) FilterPalette(); }

    void PaletteBox_PreviewKeyDown(object s, KeyEventArgs e)
    {
        int n = PaletteList.Items.Count;
        switch (e.Key)
        {
            case Key.Down: if (n > 0) PaletteList.SelectedIndex = (PaletteList.SelectedIndex + 1) % n; PaletteList.ScrollIntoView(PaletteList.SelectedItem); e.Handled = true; break;
            case Key.Up: if (n > 0) PaletteList.SelectedIndex = (PaletteList.SelectedIndex - 1 + n) % n; PaletteList.ScrollIntoView(PaletteList.SelectedItem); e.Handled = true; break;
            case Key.Enter: RunPaletteSelection(); e.Handled = true; break;
            case Key.Escape: ClosePalette(); e.Handled = true; break;
        }
    }

    void PaletteBox_LostFocus(object s, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject d && Palette.IsAncestorOf(d)) return;
        ClosePalette();
    }

    void PaletteList_Click(object s, MouseButtonEventArgs e) => RunPaletteSelection();

    void PaletteList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        // theme picker: live preview of the highlighted theme (Esc reverts), like VS Code
        if (palMode == PaletteMode.Theme && Palette.Visibility == Visibility.Visible &&
            PaletteList.SelectedItem is ListBoxItem { Content: Grid { Tag: Cmd c } } && ThemeManager.Current.Name != c.Name)
            ThemeManager.Apply(c.Name);
    }

    void RunPaletteSelection()
    {
        if (PaletteList.SelectedItem is not ListBoxItem { Content: Grid { Tag: Cmd c } }) return;
        palCommitted = true;
        ClosePalette();
        c.Run();
    }
}
