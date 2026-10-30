using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace CutPro;

public partial class MainWindow : Window
{
    readonly Editor ed = new();
    readonly Stopwatch sw = new();
    readonly AudioOut audio = new();
    bool playing, ready, clockRunning;
    long playWall;
    List<MediaItem>? binRef; string binSig = "";
    double lastDrawnT = -1;
    double t0;
    Point binDown;
    MediaItem? binDownItem;
    string? projectPath;

    public MainWindow()
    {
        InitializeComponent();
        Pv.Ed = ed; Pv.Audio = audio; Tl.Ed = ed; Ph.Ed = ed; Ph.Tl = Tl;
        ed.Changed += OnChanged;
        ed.PropsChanged += () => { Tl.InvalidateVisual(); DurText.Text = TimeUtil.Format(ed.End); };
        // (the playhead lives on the overlay: no full timeline redraw per scrub step)
        ed.Seeked += () => { if (playing) { audio.Stop(); clockRunning = false; playWall = Environment.TickCount64; } Ph.InvalidateVisual(); };
        ScrubCache.Updated += _ => Dispatcher.BeginInvoke(() => { binRef = null; OnChanged(); });
        Tl.ViewChanged += () => { UpdateScrollBar(); UpdateVScroll(); SyncZoomSlider(); Ph.InvalidateVisual(); };
        Tl.OverlayChanged += () => Ph.InvalidateVisual();
        Tl.ScrubStarted += Pause;
        CompositionTarget.Rendering += OnRendering;
        SizeChanged += (_, _) => UpdateScrollBar();
        suppressTheme = true; ThemeCombo.ItemsSource = Themes.All.Select(t => t.Name).ToList(); ThemeCombo.SelectedIndex = 0; suppressTheme = false;
        ready = true;
        InitShell();
        InitObjects();
        InitKeyframes();
        InitSequence();
        InitNest();
        InitCaptions();
        TitleLogo.Source = IconArt.Image(20);                 // vector logo (sharp at any DPI); the exe / taskbar use Assets/CutPro.ico
        var online = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        online.Tick += (_, _) => { if (ed.Media.Count(m => m.RefreshOnline()) > 0) { binRef = null; ed.RaiseChanged(); } };
        online.Start();
        Loaded += (_, _) =>
        {
            if (!AppSettings.ReadOnly) DesktopLink.Ensure();
            if (Exporter.FindFfmpeg() == null)
                Alert.Show(this, $"{AppInfo.Name} needs ffmpeg for playback and export, but ffmpeg.exe was not found.\n\nInstall it with:  winget install Gyan.FFmpeg\nthen restart {AppInfo.Name}.",
                    "ffmpeg required", AlertKind.Warning);
        };
        OnChanged();
    }

    // ---------- refresh ----------
    void OnChanged()
    {
        if (srcItem != null && !ed.Media.Contains(srcItem)) CloseSource();       // (its clip left the project: removed, another project opened)
        // the Project panel: sequences + media (rebuilt when one is added / removed / renamed, or a sequence's length or format changes)
        var items = BinItems();
        var sig = string.Join("\n", items.Select(m => m.Id + "|" + m.Name + "|" + m.Info));
        if (!ReferenceEquals(binRef, ed.Media) || sig != binSig) { BinList.ItemsSource = null; BinList.ItemsSource = items; binRef = ed.Media; binSig = sig; }
        DurText.Text = TimeUtil.Format(ed.End);
        McButtonState();
        BuildInspector(); UpdateScrollBar(); Tl.InvalidateVisual(); Ph.InvalidateVisual();
        Title = AppInfo.Name + (projectPath != null ? " — " + Path.GetFileName(projectPath) : "") + (Dirty && !AppSettings.ReadOnly ? " •" : "");
        UpdateStatus();
    }

    void UpdateScrollBar()
    {
        if (!ready) return;
        HScroll.Maximum = Math.Max(0, Tl.ContentWidth - Tl.Viewport);
        HScroll.ViewportSize = Tl.Viewport; HScroll.LargeChange = Tl.Viewport * 0.8; HScroll.SmallChange = 40;
        HScroll.Value = Tl.ScrollX;
    }
    void HScroll_Scroll(object s, System.Windows.Controls.Primitives.ScrollEventArgs e) => Tl.ScrollX = e.NewValue;

    // ---------- playback ----------
    // The audio device is the master clock (AudioOut.Clock); video frames are chosen for that time.
    void OnRendering(object? s, EventArgs e)
    {
        PlaytestTick();
        UpdateMeters();
        UpdateScopes();
        SourceTick();
        if (!playing)
        {   // audio scrubbing: grains of the sound under the playhead while it is dragged
            if (Tl.Scrubbing && S.AudioScrub && audio.Ok) audio.Scrub(ed.Time);
            else if (audio.Scrubbing) audio.Stop();
        }
        if (playing)
        {
            if (!clockRunning && playRate == 1)
            {
                // prime decoders + audio, then start the clock once the first frame is ready (max 1.5 s)
                Pv.ClockRunning = false;
                Pv.Sync(ed.Time, true);
                if (Pv.ReadyToPlay(ed.Time) || Environment.TickCount64 - playWall > 1500)
                {
                    clockRunning = true; t0 = ed.Time; sw.Restart();
                    audio.Start(ed.Time);
                    ptStartLatency?.Add(Environment.TickCount64 - playWall);
                }
            }
            else
            {
                ed.Time = audio.Ok ? audio.Clock : t0 + sw.Elapsed.TotalSeconds * playRate;
                if (ed.Time >= ed.End) { ed.Time = ed.End; Pause(); }
                else if (ed.Time <= 0 && playRate < 0) { ed.Time = 0; Pause(); }
                FollowPlayhead();
            }
        }
        Pv.ClockRunning = playing && clockRunning && playRate == 1;
        Pv.Sync(ed.Time, playing && playRate == 1);                            // (shuttling: the scrub machinery — cached frames while the time moves)
        McTick();
        if (Mo.Playing != playing) { Mo.Playing = playing; Mo.InvalidateVisual(); }
        if (Po.Playing != playing) { Po.Playing = playing; Po.InvalidateVisual(); }
        if (ed.Time != lastDrawnT && ed.Selected is { Pin: not null }) Po.InvalidateVisual();
        if (shapeOverlay != null && (shapeOverlay.Playing != playing || ed.Time != lastDrawnT && ed.Selected is { Shapes: not null })) { shapeOverlay.Playing = playing; shapeOverlay.InvalidateVisual(); }
        if (meshOverlay != null && (meshOverlay.Playing != playing || ed.Time != lastDrawnT && ed.Selected is { Distort.Kind: DistortKind.Mesh })) { meshOverlay.Playing = playing; meshOverlay.InvalidateVisual(); }
        if (ed.Time != lastDrawnT && (Mo.Tool != ObjectTool.None || Mo.Target is { Masks.Count: > 0 })) Mo.InvalidateVisual();
        if (ed.Time != lastDrawnT && liveKeys.Count > 0) RefreshMaskKeys();
        if (ed.Time != lastDrawnT) { if (liveProps.Count > 0) RefreshProps(); if (Kfe.IsVisible) Kfe.InvalidateVisual(); UpdateCaptionHighlight(); }
        if (ed.Time != lastDrawnT) { lastDrawnT = ed.Time; Ph.InvalidateVisual(); TimeText.Text = TimeUtil.Format(ed.Time); StatusTimeText.Text = $"{TimeText.Text} / {DurText.Text}"; }
        PlaytestFrame();
    }
    void Play()
    {
        if (playing || ed.Clips.Count == 0) return;
        SrcPause();                                                          // (one monitor plays at a time)
        if (ed.Time >= ed.End - 0.01) ed.Time = 0;
        playRate = 1;
        playing = true; clockRunning = false; playWall = Environment.TickCount64; UpdatePlayArt(); ShuttleBadge();
    }

    /// <summary>Play / pause button: the theme's art (leaf, glass orb, sapphire, metal disc) or the classic glyph.</summary>
    void UpdatePlayArt()
    {
        if (TryFindResource(playing ? "Th.PauseArt" : "Th.PlayArt") is ImageSource art)
        {
            PlayBtn.SetResourceReference(StyleProperty, "ArtBtn");
            PlayBtn.Content = new Image { Source = art, Stretch = Stretch.None };
            PlayBtn.Width = 46; PlayBtn.Height = 38;
        }
        else
        {
            PlayBtn.SetResourceReference(StyleProperty, "Primary");
            PlayBtn.Content = playing ? "" : "";
            PlayBtn.Width = 40; PlayBtn.Height = 30;
        }
    }
    void Pause()
    {
        if (!playing) return;
        audio.Stop();
        playing = false; clockRunning = false; playRate = 1; UpdatePlayArt(); ShuttleBadge();
        ed.Time = Math.Floor(ed.Time * TimeUtil.Fps + 1e-3) / TimeUtil.Fps;   // park on the frame that is showing
        McStop();                                                            // (camera switches recorded while playing become cuts)
    }
    void Res_Changed(object s, SelectionChangedEventArgs e) { Pv.ResDiv = ResCombo.SelectedIndex switch { 1 => 2, 2 => 4, _ => 1 }; UpdateStatus(); }
    void TogglePlay() { if (playing) Pause(); else Play(); }
    void Step(double dt) => ed.Seek(Math.Min(Math.Max(0, ed.Time + dt), ed.End));

    void Play_Click(object s, RoutedEventArgs e) => TogglePlay();
    void ToStart_Click(object s, RoutedEventArgs e) => ed.Seek(0);
    void ToEnd_Click(object s, RoutedEventArgs e) => ed.Seek(ed.End);
    void StepBack_Click(object s, RoutedEventArgs e) => Step(-1.0 / TimeUtil.Fps);
    void StepFwd_Click(object s, RoutedEventArgs e) => Step(1.0 / TimeUtil.Fps);

    // ---------- toolbar ----------
    void Undo_Click(object s, RoutedEventArgs e) => ed.Undo();
    void Redo_Click(object s, RoutedEventArgs e) => ed.Redo();
    void Title_Click(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var (role, name) in new[] { (TextRole.Title, "Title"), (TextRole.Subtitle, "Subtitle"), (TextRole.LowerThird, "Lower third"), (TextRole.Caption, "Caption") })
        {
            var mi = new MenuItem { Header = name };
            mi.Click += (_, _) => ed.AddTitle(role);
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = TitleBtn; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }
    bool suppressTheme;
    void Theme_Changed(object s, SelectionChangedEventArgs e) { if (ready && !suppressTheme && ThemeCombo.SelectedItem is string name) ed.ApplyTheme(name); }
    void Split_Click(object s, RoutedEventArgs e) => ed.AddEdit(false);
    void Delete_Click(object s, RoutedEventArgs e) => ed.DeleteSelection(false);
    void Ripple_Click(object s, RoutedEventArgs e) => ed.DeleteSelection(true);
    void MarkIn_Click(object s, RoutedEventArgs e) => ed.SetMark(true);
    void MarkOut_Click(object s, RoutedEventArgs e) => ed.SetMark(false);
    void Lift_Click(object s, RoutedEventArgs e) => ed.LiftExtract(false);
    void Extract_Click(object s, RoutedEventArgs e) => ed.LiftExtract(true);
    void Snap_Changed(object s, RoutedEventArgs e) { if (ready) { Tl.SnapEnabled = SnapChk.IsChecked == true; UpdateStatus(); } }
    /// <summary>The zoom slider is logarithmic (pixels per second = 2^value: from a whole hour down to single frames).</summary>
    bool zoomSync;
    void Zoom_Changed(object s, RoutedPropertyChangedEventArgs<double> e) { if (ready && !zoomSync) Tl.Pps = Math.Pow(2, e.NewValue); }
    void SyncZoomSlider() { zoomSync = true; ZoomSlider.Value = Math.Log2(Tl.Pps); zoomSync = false; }
    void ToolSelect_Click(object s, RoutedEventArgs e) => SetTool(false);
    void ToolRazor_Click(object s, RoutedEventArgs e) => SetTool(true);
    void SetTool(bool razor) => SetTool(razor ? TrimTool.Razor : TrimTool.Select);


    void Window_PreviewKeyUp(object s, KeyEventArgs e) { if (e.Key is Key.LeftShift or Key.RightShift or Key.System or Key.LeftAlt or Key.RightAlt) Tl.UpdateHover(); }

    // ---------- bin ----------
    async void Import_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true, Title = "Import media",
            Filter = MediaProbe.DialogFilter
        };
        if (dlg.ShowDialog(this) == true) await ImportAsync(dlg.FileNames);
    }

    async Task ImportAsync(IEnumerable<string> paths)
    {
        var failed = new List<string>();
        foreach (var p in paths)
        {
            if (Path.GetExtension(p).ToLowerInvariant() is ".otio" or ".edl" or ".xml" or ".aaf") { await ImportSequenceAsync(p); continue; }   // (a timeline from another editor)
            if (ed.Media.Any(m => string.Equals(m.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            var m = await MediaProbe.ProbeAsync(p);
            if (m == null) { failed.Add($"{Path.GetFileName(p)}: {MediaProbe.LastError}"); continue; }
            m.Bin = binPath;                                                     // (into the open bin)
            ed.Media.Add(m); ed.RaiseChanged();
        }
        if (failed.Count > 0)
            Alert.Show(this, string.Join("\n", failed), "Some files could not be imported", AlertKind.Warning);
    }

    void Bin_DragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy
                  : e.Data.GetDataPresent("CutPro.Media") && ItemAt((DependencyObject)e.OriginalSource) is { Folder: not null } ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }
    async void Bin_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { await ImportAsync(files); return; }
        if (ItemAt((DependencyObject)e.OriginalSource) is { Folder: { } f } && DraggedMedia(e) is { Count: > 0 } moving) { ed.MoveToBin(moving, f); e.Handled = true; }
    }
    MediaItem? ItemAt(DependencyObject src) => (ItemsControl.ContainerFromElement(BinList, src) as ListBoxItem)?.DataContext as MediaItem;
    void Bin_MouseDown(object s, MouseButtonEventArgs e) { binDown = e.GetPosition(null); binDownItem = ItemAt((DependencyObject)e.OriginalSource); if (binDownItem?.Folder != null) binDownItem = null; }
    void Bin_MouseMove(object s, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || binDownItem == null) return;
        var d = e.GetPosition(null) - binDown;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = binDownItem; binDownItem = null;
        DragDrop.DoDragDrop(BinList, new DataObject("CutPro.Media", item.Id), DragDropEffects.Copy);
    }
    void Bin_DoubleClick(object s, MouseButtonEventArgs e)
    {
        var m = ItemAt((DependencyObject)e.OriginalSource);
        if (m == null) return;
        if (m.Folder != null) { OpenBin(m.Folder); return; }
        if (m.Seq != null) { if (ed.SeqById(m.Seq) is { } seq) OpenSequence(seq); return; }     // a sequence: open it (drag it onto the timeline to nest it)
        if (m.Offline) { Alert.Show(this, m.Path + "\n\nThe file was moved or deleted.", "Media offline", AlertKind.Warning); return; }
        OpenSource(m);                                                       // (Premiere: double-click opens the clip in the Source monitor)
    }

    // ---------- inspector ----------
    void BuildInspector()
    {
        BuildInspectorCore();
        if (Loc.Current != "en") Loc.Apply(Inspector);                   // (fixed labels in the interface language)
    }
    void BuildInspectorCore()
    {
        Inspector.Children.Clear(); liveKeys.Clear(); liveProps.Clear();
        if (BuildTrackFxInspector()) return;                          // a track's effects (mixer strip fx button)
        if (BuildTransitionInspector()) return;                       // a transition is selected
        var c = ed.Selected;
        if (c == null)
        {
            Inspector.Children.Add(new TextBlock { Text = "Select a clip on the timeline.", Foreground = Dim, TextWrapping = TextWrapping.Wrap });
            return;
        }
        var m = ed.MediaOf(c);
        Inspector.Children.Add(new TextBlock { Text = c.Kind == ClipKind.Text ? "Title" : c.Kind == ClipKind.Adjustment ? "Adjustment layer" : m?.Name ?? "Clip", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        Inspector.Children.Add(new TextBlock
        {
            Text = $"{TimeUtil.Format(c.Start)}  →  {TimeUtil.Format(c.End)}   ({c.Dur:0.00}s)",
            Foreground = Dim, FontSize = 11
        });

        if (c.Kind == ClipKind.Adjustment)
        {   // adjustment layer: strength (opacity, keyframes, fades), blur and colour for everything on the tracks below
            Inspector.Children.Add(new TextBlock { Text = Adjust.Works(c) ? "Its colour and blur apply to every track below it while it is on screen; opacity sets how much." : "On V1 there is nothing below it — move it to V2 or V3.",
                                                   Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            Group("Opacity"); PropRow(c, AnimProp.Opacity);
            Group("Gaussian blur"); PropRow(c, AnimProp.Blur);
            BuildColorInspector(c);
            Group("Transitions (fade)");
            Slider("Fade in (s)", 0, 5, 0.1, c.FadeIn, v => c.FadeIn = v);
            Slider("Fade out (s)", 0, 5, 0.1, c.FadeOut, v => c.FadeOut = v);
            return;
        }
        if (c.Kind == ClipKind.Text) BuildTitleInspector(c);
        if (c.Kind != ClipKind.Audio)
        {
            Group("Motion");
            PropRow(c, AnimProp.Scale); PropRow(c, AnimProp.X); PropRow(c, AnimProp.Y); PropRow(c, AnimProp.Rotation);
            if (c.Kind != ClipKind.Text) { PropRow(c, AnimProp.AnchorX); PropRow(c, AnimProp.AnchorY); }
            if (Editor.CoversFrame(c)) PropRow(c, AnimProp.Pan);
            BuildFollowRow(c);
            Group("Opacity"); PropRow(c, AnimProp.Opacity); BuildBlendRow(c); BuildMatteRow(c);
            Group("Gaussian blur"); PropRow(c, AnimProp.Blur);
        }
        BuildKeyInspector(c);
        BuildCropInspector(c);
        BuildPinInspector(c);
        BuildLensInspector(c);
        BuildDistortInspector(c);
        BuildColorInspector(c);
        BuildToonInspector(c);
        BuildLooksInspector(c);
        BuildShadowInspector(c);
        if (c.Kind is ClipKind.Video or ClipKind.Image) BuildMaskInspector(c);
        if (c.Kind == ClipKind.Audio || (c.Kind == ClipKind.Video && !c.NoAudio)) { Group("Audio"); PropRow(c, AnimProp.Volume); BuildPitchRow(c); }
        BuildAudioFxInspector(c);
        BuildTimeInspector(c);
        BuildStabInspector(c);
        BuildDenoiseInspector(c);
        Group("Transitions (fade)");
        Slider("Fade in (s)", 0, 5, 0.1, c.FadeIn, v => c.FadeIn = v);
        Slider("Fade out (s)", 0, 5, 0.1, c.FadeOut, v => c.FadeOut = v);
    }

    void Group(string name) => Inspector.Children.Add(new TextBlock
    { Text = name.ToUpperInvariant(), Foreground = Accent, FontSize = 11, Margin = new Thickness(0, 14, 0, 2) });

    void Slider(string label, double min, double max, double step, double value, Action<double> set, Panel? into = null)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        var lb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Dim };
        var sl = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = step, LargeChange = step * 10, VerticalAlignment = VerticalAlignment.Center };
        var tb = new TextBox { Text = value.ToString("0.##", CultureInfo.InvariantCulture), VerticalContentAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sl, 1); Grid.SetColumn(tb, 2);
        bool busy = false;
        sl.ValueChanged += (_, e) =>
        {
            if (busy) return; busy = true;
            double v = Math.Round(e.NewValue / step) * step;
            set(v); tb.Text = v.ToString("0.##", CultureInfo.InvariantCulture); ed.Touch(); busy = false;
        };
        tb.TextChanged += (_, _) =>
        {
            if (busy) return;
            if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) sl.Value = Math.Clamp(v, min, max);
        };
        g.Children.Add(lb); g.Children.Add(sl); g.Children.Add(tb);
        (into ?? Inspector).Children.Add(g);
    }

    // ---------- save / open ----------
    void Save_Click(object s, RoutedEventArgs e)
    {
        if (projectPath == null)
        {
            var dlg = new SaveFileDialog { Filter = $"{AppInfo.Name} project|*.cutpro", DefaultExt = ".cutpro", FileName = "Untitled" };
            if (dlg.ShowDialog(this) != true) return;
            projectPath = dlg.FileName;
        }
        File.WriteAllText(projectPath, ProjectJson(true));
        Title = AppInfo.Name + " — " + Path.GetFileName(projectPath);
        MarkSaved(true);
        histStale = true; RefreshHistory();
    }
    /// <summary>The project as saved (Save, Collect Files).</summary>
    ProjectFile BuildProjectFile(bool commit = true)
    {
        if (commit) ed.Commit();
        ed.CaptureFormat();
        var used = ed.Sequences.SelectMany(s => s.Clips).SelectMany(c => c.Masks).Select(m => m.Id).ToHashSet();
        return new ProjectFile { Media = ed.Media, Theme = ed.ThemeName, MaskTracks = ed.MaskTracks.Where(kv => used.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
                                 SeqW = Editor.OutW, SeqH = Editor.OutH, SeqFps = TimeUtil.Fps, Reframe = Editor.Reframe, Publish = ed.Publish,
                                 Sequences = ed.Sequences, Active = ed.Seq.Id, Open = openSeqs.ToList(), Bins = ed.Bins.ToList() };
    }

    async void Open_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = $"{AppInfo.Name} project|*.cutpro|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        if (!ConfirmDiscard()) return;
        await OpenProjectFile(dlg.FileName, dlg.FileName);
    }

    /// <summary>Opens a project file; asPath = where Save writes it (null = Save asks: an auto-save version, a recovered project keeps its own).</summary>
    async Task OpenProjectFile(string file, string? asPath)
    {
        try
        {
            var pf = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(file))!;
            Pause();
            ed.Publish = pf.Publish ?? new();
            ed.Media = pf.Media; ed.Bins = pf.Bins ?? new(); binPath = ""; ed.MaskTracks = pf.MaskTracks ?? new(); ed.ThemeName = Themes.Get(pf.Theme).Name; suppressTheme = true; ThemeCombo.SelectedItem = ed.ThemeName; suppressTheme = false;
            openSeqs.Clear(); openSeqs.AddRange(pf.Open ?? new());
            ed.UseSequences(pf.AllSequences(), pf.Active);                          // (each sequence's clips, format, mixer, playhead; a version 1 file is one)
            ed.Sel.Clear(); ed.SelectedId = null; ed.Gap = null; ed.ResetHistory();
            projectPath = asPath; ed.RaiseChanged();
            MarkSaved(file == asPath);
            foreach (var m in ed.Media)
            {
                if (m.Gen != null && !File.Exists(m.Path))                          // (a generated item: rendered again from its settings)
                    try { m.Path = await Generators.EnsureAsync(m.Gen); } catch { }
                await MediaProbe.FillAsync(m);
            }
            binRef = null; ed.RaiseChanged();
            var missing = ed.Media.Where(m => m.Offline).Select(m => m.Path).ToList();
            if (missing.Count > 0 && Alert.Show(this, string.Join("\n", missing.Take(12)) + (missing.Count > 12 ? $"\n… and {missing.Count - 12} more" : "") +
                                                "\n\nFind them now? (Locate one and the others in the same folder are found too.)",
                                                "Some media files are missing", AlertKind.Warning, "Relink…", "Later") == 0)
                RelinkOffline();
        }
        catch (Exception ex) { Alert.Show(this, ex.Message, "Could not open the project", AlertKind.Error); }
    }

    // ---------- export ----------


    // ---------- automated playback test (CutPro.exe --playtest out.txt [mediaDir|-] [basic|edits]) ----------
    // Builds a cut-up timeline from mediaDir\a.* and b.*, runs a scripted session and reports A/V sync
    // (picture vs audio clock), freezes, UI hitches, decoder restarts, GC and leftover ffmpeg processes.
    //  basic: start mid-clip, pause/resume, seek while playing, scrubbing, cuts, a gap, a file change.
    //  edits: razor cuts / ripple delete / undo / redo while playing, dragging clips under a paused playhead.
    List<(double wall, double t, bool running, double? shown, double? exp, string? clip, bool full)>? ptLog;
    readonly List<double> ptStops = new(), ptSteps = new();          // times the playhead/drag stopped, frame steps
    readonly List<(double a, double b)> ptMoves = new();              // windows of continuous scrubbing
    string ptScenario = "basic"; string ptCacheInfo = "";
    List<long>? ptStartLatency;
    List<(double at, Action act)>? ptScript;
    double ptScrubTarget = -1, ptScrubAt = -1, ptScrubSettle = -1, ptDragEnd = -1, ptDragSettle = -1;
    string? ptOut; readonly Stopwatch ptWall = new();
    int ptGen2; TimeSpan ptGcPause; int ptFfmpeg, ptDragSteps;

    public async void StartPlaytest(string outFile, string? mediaDir = null, string scenario = "basic")
    {
        ptOut = outFile; ptScenario = scenario;
        var d = mediaDir ?? Path.Combine(Path.GetTempPath(), "cutpro_test");
        string Pick(string n) => Directory.GetFiles(d, n + ".*").First(f => !f.EndsWith(".txt"));
        await ImportAsync(new[] { Pick("a"), Pick("b") });
        var A = ed.Media[0]; var B = ed.Media[1];
        if (scenario == "speed") Remap.Background = false;
        if (scenario == "speed")
        {
            // speed changes: slow, fast, reversed (picture + linked sound), a frame hold and a speed ramp; played once straight away
            // (not rendered yet: the media on its native grid / the scrub cache) and once after the background renders
            void Pair(MediaItem m, double start, double inPt, double dur, double speed, bool rev)
            {
                var link = Guid.NewGuid().ToString("N");
                ed.Clips.Add(new Clip { MediaId = m.Id, Kind = ClipKind.Video, Track = 2, Start = start, In = inPt, Dur = dur, Speed = speed, Reverse = rev, LinkId = link, NoAudio = true });
                ed.Clips.Add(new Clip { MediaId = m.Id, Kind = ClipKind.Audio, Track = 3, Start = start, In = inPt, Dur = dur, Speed = speed, Reverse = rev, LinkId = link });
            }
            Pair(A, 0, 1, 3, 50, false);
            Pair(A, 3, 0.5, 2, 200, false);
            Pair(A, 5, 5.5, 3, 100, true);
            ed.Clips.Add(new Clip { MediaId = B.Id, Kind = ClipKind.Video, Track = 2, Start = 8, In = 1, Dur = 1.5, Hold = true });
            var rc = new Clip { MediaId = A.Id, Kind = ClipKind.Video, Track = 2, Start = 9.5, In = 0.5, Dur = 3 };
            Anim.SetKey(rc, AnimProp.Speed, 0.5, 100); Anim.SetKey(rc, AnimProp.Speed, 2.5, 200);
            ed.Clips.Add(rc);
        }
        else
        {
        ed.AddMedia(A, null, 0);                                            // linked V1+A1
        foreach (var t in new[] { 1.0, 2.0, 4.0 }) { ed.Time = t; ed.ClearSelection(); ed.AddEdit(true); }
        ed.Select(ed.Clips.First(c => c.Kind == ClipKind.Video && Math.Abs(c.Start - 2) < 1e-3));
        ed.DeleteSelection(true);                                           // through-edit @1s, jump cut @2s
        ed.AddMedia(B, null, ed.End);                                       // file change
        ed.AddMedia(A, null, ed.End + 0.5);                                 // gap, then A again
        ed.Time = 1; ed.AddTitle();                                         // title on V2
        }
        if (scenario == "composite")                                        // the basic session with a blended, cropped, keyed clip on V2 (shader +
        {                                                                   // brush of V1 every frame, a BGRA decoder) and a blended title
            var v2 = new Clip { MediaId = A.Id, Kind = ClipKind.Video, Track = 1, Start = 0.3, In = 1, Dur = Math.Min(6, A.Duration - 1), NoAudio = true,
                                Blend = BlendMode.Screen, Scale = 70, X = 8, CropR = 15, CropFeather = 12, FlipH = true,
                                Key = new ChromaKey { Color = "#FF0000", Tolerance = 20, Softness = 15, Spill = 0 } };
            ed.Clips.Add(v2);
            foreach (var t in ed.Clips.Where(c => c.Kind == ClipKind.Text)) t.Blend = BlendMode.Overlay;
            foreach (var a in ed.Clips.Where(c => c.Kind == ClipKind.Audio)) a.AudioFx = AudioDsp.VoicePreset();   // (effects on the mixer thread)
            // an adjustment layer over the first seconds on V3: the LUT shader on the tracks-below canvas every frame
            if (!ed.Collides(0, 0, 6, null)) ed.Clips.Add(new Clip { Kind = ClipKind.Adjustment, Track = 0, Start = 0, Dur = 6, Text = "Adjustment layer", Grade = new ColorGrade { Look = "Warm Film", Contrast = 15 }, FadeIn = 0.5 });
            // track automation on every audio track (fader ramps + a balance sweep: block tables in the mixer) and a clip volume band
            for (int tr = Tracks.A1; tr < Tracks.Count; tr++) { ed.Mix.Channels[tr].GainKeys = new() { new() { T = 0, V = -12 }, new() { T = 3, V = 0 }, new() { T = 9, V = -6 } }; ed.Mix.Channels[tr].PanKeys = new() { new() { T = 1, V = -50 }, new() { T = 8, V = 50 } }; }
            if (ed.Clips.FirstOrDefault(c => c.Kind == ClipKind.Audio) is { } va) { Anim.SetKey(va, AnimProp.Volume, va.In, 100); Anim.SetKey(va, AnimProp.Volume, va.In + 2, 60); }
        }
        if (scenario == "looks")                                            // the basic session with a grade (LUT per frame) and / or a drawn look
        {                                                                   // (ffmpeg filter in the decoder) on every video clip
            int k = 0;
            foreach (var c in ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start))
            {
                c.Grade = k % 3 == 1 ? null : new ColorGrade { Temperature = 20, Contrast = 30, Saturation = 120, Look = "Teal & Orange", Vignette = -40 };
                c.Toon = k % 3 == 0 ? null : ToonStyle.Preset(k % 3 == 1 ? ToonMode.Cartoon : ToonMode.Anime);
                k++;
            }
        }
        if (scenario == "edits")                                            // lots of clips: lag used to grow with clip count
            for (double t = 0.35; t < ed.End; t += 0.45) { ed.Time = t; ed.ClearSelection(); ed.AddEdit(true); }
        if (scenario == "nest")                                             // the basic session with its first clips (cuts, title, sound) nested: the
        {                                                                   // outer timeline plays a nested render (BGRA, its own frame grid), then media
            ed.SetSelection(ed.Clips.Where(c => c.Start < 3.9).ToList());
            ed.NestSelection("Nested opening");
        }
        if (scenario == "tracks")                                           // the basic session in a 12 + 4 track sequence: a picture-in-picture clip on
        {                                                                   // each of V3 … V12 (ten decoders, the canvas tree 12 deep) and sound on A3 / A4
            ed.AddTracks(9, 0, 2, Tracks.Count);
            for (int vn = 3; vn <= 12; vn++)
                ed.Clips.Add(new Clip { MediaId = (vn % 2 == 0 ? A : B).Id, Kind = ClipKind.Video, Track = Tracks.V(vn), Start = 0.2 * (vn - 3), Dur = vn % 2 == 0 ? 5 : 3.5, In = 0.3,
                                        NoAudio = true, Scale = 24, X = -40 + (vn - 3) % 5 * 20, Y = vn < 8 ? -28 : 28 });
            for (int an = 3; an <= 4; an++) ed.Clips.Add(new Clip { MediaId = A.Id, Kind = ClipKind.Audio, Track = Tracks.A(an), Start = 0.5 * an, Dur = 4, In = 0.5, Volume = 60 });
        }
        ed.ClearSelection();
        foreach (var m in ed.Media) AudioCache.WaitReady(m.Path, 20000);
        var cw = Stopwatch.StartNew();
        foreach (var m in ed.Media) await Task.Run(() => ScrubCache.WaitReady(m.Path, 180000));
        ptCacheInfo = string.Join("; ", ed.Media.Select(m => $"{m.Name}: {m.Width}x{m.Height} {m.Codec}{(m.TenBit ? " 10-bit" : "")} seek={m.SeekDecoder} play={m.PlayDecoder} " +
                                                         $"scrubCache={ScrubCache.FrameCount(m.Path)}f/{ScrubCache.BuildMs(m.Path)}ms/{ScrubCache.Bytes(m.Path) / Math.Max(1, ScrubCache.FrameCount(m.Path)) / 1024}KB-per-frame"));
        if (ed.Media.Any(m => m.DecodeNote != "")) ptCacheInfo += "\n  calibration: " + string.Join(" | ", ed.Media.Where(m => m.DecodeNote != "").Select(m => m.Name + " " + m.DecodeNote));
        ed.Seek(0.5);
        await Task.Delay(1000);
        if (scenario == "nest")
        {   // the nested picture and sound render in the background first (the preview asks for them while paused here)
            var nw = Stopwatch.StartNew();
            while (nw.ElapsedMilliseconds < 60000 && (Nest.Status != null || !Pv.Settled(ed.Time) || !Pv.ReadyToPlay(ed.Time))) await Task.Delay(100);
            ptCacheInfo += $"\n  nested render ready after {nw.ElapsedMilliseconds} ms";
            await Task.Delay(500);
        }
        ptLog = new(); ptStartLatency = new();
        if (scenario == "edits")
        {
            ptScript = new()
            {
                (0, Play),
                (700, () => ed.AddEdit(true)), (1300, () => ed.AddEdit(true)),   // razor at playhead while playing
                (1900, RippleDeleteNext),                                        // ripple delete ahead while playing
                (2600, ed.Undo), (3000, ed.Redo),
                (3400, PausePt),
            };
            ptScript.Add((3450, () =>
            {   // park the playhead near the end of the clip under it so the drag keeps that clip under the playhead
                var c = ed.Clips.Where(x => x.Kind == ClipKind.Video && ed.Time >= x.Start && ed.Time < x.End).OrderByDescending(x => x.Track).FirstOrDefault();
                if (c != null) { ed.Seek(c.End - 1.0 / TimeUtil.Fps); ptDragSteps = Math.Min(20, (int)((c.Dur - 2.0 / TimeUtil.Fps) * TimeUtil.Fps)); }
            }));
            for (int i = 0; i < 20; i++) ptScript.Add((3500 + i * 33, () => { if (ptDragSteps-- > 0) DragUnderPlayhead(1.0 / TimeUtil.Fps); }));
            ptScript.Add((4250, () => { ed.Commit(); ed.RaiseChanged(); ptDragEnd = ptWall.Elapsed.TotalMilliseconds; }));
            ptScript.Add((4700, Play));
            ptScript.Add((6000, () => ed.AddEdit(true)));
            ptScript.Add((7500, PausePt));
        }
        else if (scenario == "speed")
        {
            ptScript = new()
            {
                (0, () => { ed.Seek(0.2); Play(); }),                     // straight away: nothing rendered yet (background rendering held)
                (12000, PausePt),                                           // (inside the ramp: a frame to settle on)
                (12900, () => { Remap.Background = true; ed.Seek(0.2); }), (16500, Play),   // again, from the rendered files
                (28200, PausePt),
            };
        }
        else if (scenario == "scrub")
        {
            ptScript = new();
            // 1. fast playhead drag like a mouse (60 Hz): 1.0 -> 12.0 -> stop mid-clip at 6.37
            for (int i = 0; i <= 125; i++)
            {
                double u = i / 125.0, pos = u < 0.6 ? 1.0 + 11.0 * (u / 0.6) : 12.0 - (12.0 - 6.37) * ((u - 0.6) / 0.4);
                ptScript.Add((i * 16, () => ed.Seek(pos)));
            }
            ptMoves.Add((0, 2000)); ptScript.Add((2000, () => ptStops.Add(ptWall.Elapsed.TotalMilliseconds)));
            // 2. fine back-and-forth scrub around one spot, then stop
            for (int i = 0; i <= 30; i++) { double pos = 6.37 + 0.1 * Math.Sin(i / 3.0); ptScript.Add((3000 + i * 33, () => ed.Seek(pos))); }
            ptMoves.Add((3000, 4000)); ptScript.Add((4000, () => ptStops.Add(ptWall.Elapsed.TotalMilliseconds)));
            // 3. frame steps back and forward (arrow keys)
            for (int i = 0; i < 6; i++)
            {
                double step = i < 3 ? -1.0 / TimeUtil.Fps : 1.0 / TimeUtil.Fps;
                ptScript.Add((5000 + i * 250, () => { ed.Seek(ed.Time + step); ptSteps.Add(ptWall.Elapsed.TotalMilliseconds); }));
            }
            ptScript.Add((7200, () => { }));
        }
        else
        {
            ptScript = new()
            {
                (0, Play),                                                  // start mid-clip
                (1500, PausePt), (1900, Play),                                // pause / resume
                (3000, () => ed.Seek(3.2)),                                 // seek while playing
                (4800, PausePt),
            };
            double[] scrub = { 5.0, 7.3, 2.1, 9.0, 3.3, 10.4, 6.5 };        // scrub while paused
            for (int i = 0; i < scrub.Length; i++)
            {
                double tt = scrub[i];
                ptScript.Add((5000 + i * 60, () => { ed.Seek(tt); ptScrubTarget = tt; ptScrubAt = ptWall.Elapsed.TotalMilliseconds; }));
            }
            ptScript.Add((6200, Play));                                     // through file change, gap, to end
        }
        if (Environment.GetEnvironmentVariable("CUTPRO_PTDUMP") != null) Pv.Trace = new();
        ptGen2 = GC.CollectionCount(2); ptGcPause = GC.GetTotalPauseDuration(); ptFfmpeg = Process.GetProcessesByName("ffmpeg").Length;
        ptWall.Restart();
    }

    void PausePt() { Pause(); ptStops.Add(ptWall.Elapsed.TotalMilliseconds); }

    void RippleDeleteNext()
    {
        var n = ed.Clips.Where(c => c.Kind == ClipKind.Video && c.Start > ed.Time + 0.3).OrderBy(c => c.Start).FirstOrDefault();
        if (n != null) { ed.Select(n); ed.DeleteSelection(true); }
    }

    /// <summary>Like dragging the clip under the playhead (and everything after it) right by d, mid-drag.</summary>
    void DragUnderPlayhead(double d)
    {
        var c = ed.Clips.Where(x => x.Kind == ClipKind.Video && ed.Time >= x.Start && ed.Time < x.End).OrderByDescending(x => x.Track).FirstOrDefault();
        if (c == null) return;
        ed.Checkpoint();
        foreach (var x in ed.Clips.Where(x => x.Start >= c.Start - 1e-6)) x.Start += d;
        ed.RaiseProps();
    }

    /// <summary>Runs due script actions; called at the start of a frame so edits are measured on the frame they affect.</summary>
    void PlaytestTick()
    {
        if (ptLog == null) return;
        double now = ptWall.Elapsed.TotalMilliseconds;
        while (ptScript!.Count > 0 && ptScript[0].at <= now) { var a0 = ptScript[0].act; ptScript.RemoveAt(0); a0(); }
    }

    void PlaytestFrame()
    {
        if (ptLog == null) return;
        double now = ptWall.Elapsed.TotalMilliseconds;
        var a = Pv.ActiveVideo(ed.Time);
        double? shownV = a?.shown is double sv && !double.IsNaN(sv) ? sv : null;
        ptLog.Add((now, ed.Time, playing && clockRunning, shownV, a?.expected, a?.clip, a?.full ?? false));
        bool onFrame = shownV != null && a != null && Math.Abs(shownV.Value - a.Value.expected) < 0.034;
        if (ptScrubTarget >= 0 && ptScrubSettle < 0 && !playing && onFrame && Math.Abs(ed.Time - ptScrubTarget) < 1e-3) ptScrubSettle = now - ptScrubAt;
        if (ptDragEnd >= 0 && ptDragSettle < 0 && !playing && onFrame) ptDragSettle = now - ptDragEnd;
        if (ptScript!.Count == 0 && !playing && now > 7000) FinishPlaytest();
    }

    void FinishPlaytest()
    {
        var L = ptLog!; ptLog = null;
        var r = new List<string>();
        var gaps = L.Zip(L.Skip(1), (x, y) => (at: y.wall, g: y.wall - x.wall)).ToList();
        var abs = L.Where(f => f.running && f.shown != null).Select(f => Math.Abs(f.shown!.Value - f.exp!.Value) * 1000).OrderBy(x => x).ToList();
        r.Add($"wall={L[^1].wall / 1000:0.0}s timeline={ed.End:0.00}s clips={ed.Clips.Count} decoderStarts={Pv.Restarts} audioDevice={(audio.Ok ? "ok" : "MISSING")}");
        r.Add($"ui frame gap ms: avg={gaps.Average(x => x.g):0.0} max={gaps.Max(x => x.g):0.0} over50ms={gaps.Count(x => x.g > 50)}");
        if (ptDragEnd >= 0)
        {
            var dg = gaps.Where(x => x.at > 3500 && x.at < ptDragEnd + 300).Select(x => x.g).DefaultIfEmpty(0).ToList();
            r.Add($"dragging a clip under the paused playhead: max ui gap={dg.Max():0}ms, picture settle after drop={ptDragSettle:0}ms");
        }
        r.Add($"play start latency ms: {string.Join(", ", ptStartLatency!)}");
        if (abs.Count > 0)
            r.Add($"A/V offset ms (picture - audio clock): median={abs[abs.Count / 2]:0} p95={abs[(int)(abs.Count * 0.95)]:0} max={abs[^1]:0} framesOff>1={abs.Count(x => x > 34)}/{abs.Count}");
        double since = -1, longest = 0; int freezes = 0; double? last = null, lastWant = null;   // shown frame not advancing while playing
        foreach (var f in L)
        {
            if (!f.running || f.shown == null) { since = -1; last = null; continue; }
            bool wantMoved = lastWant is not double lw || f.exp is not double fe || Math.Abs(fe - lw) > 1e-6;   // (a frame hold doesn't move: not a freeze)
            if (last is double l && Math.Abs(f.shown.Value - l) < 1e-6 && wantMoved) { if (since < 0) since = f.wall; }
            else { if (since >= 0) { double len = f.wall - since; if (len > 100) freezes++; longest = Math.Max(longest, len); } since = -1; }
            last = f.shown; lastWant = f.exp;
        }
        r.Add($"picture freezes >100ms while playing: {freezes}, longest={longest:0}ms");
        var spikes = L.Where(f => f.running && f.shown != null && Math.Abs(f.shown!.Value - f.exp!.Value) > 0.034).Select(f => (f.wall, off: (f.shown!.Value - f.exp!.Value) * 1000)).ToList();
        if (spikes.Count > 0)
        {
            var groups = new List<(double a, double b, double worst)>();
            foreach (var s in spikes)
                if (groups.Count > 0 && s.wall - groups[^1].b < 100) groups[^1] = (groups[^1].a, s.wall, Math.Abs(s.off) > Math.Abs(groups[^1].worst) ? s.off : groups[^1].worst);
                else groups.Add((s.wall, s.wall, s.off));
            r.Add("offset >1 frame at (script ms): " + string.Join(", ", groups.Select(g => $"{g.a:0}-{g.b:0} ({g.worst:0}ms)")));
        }
        if (ptScrubAt >= 0) r.Add($"scrub settle ms (last scrub -> exact frame on screen): {ptScrubSettle:0}");
        static bool Exact(double? s, double? e) => s != null && e != null && Math.Abs(s.Value - e.Value) < 0.034;
        if (ptMoves.Count > 0)
        {
            var mv = L.Where(f => f.exp != null && ptMoves.Any(w => f.wall >= w.a && f.wall <= w.b)).ToList();
            var offs = mv.Where(f => f.shown != null).Select(f => Math.Abs(f.shown!.Value - f.exp!.Value) * 1000).OrderBy(x => x).ToList();
            if (mv.Count > 0 && offs.Count > 0)
                r.Add($"while scrubbing: exact frame on screen {100.0 * mv.Count(f => Exact(f.shown, f.exp)) / mv.Count:0}% of {mv.Count} frames, " +
                      $"offset median={offs[offs.Count / 2]:0}ms p95={offs[(int)(offs.Count * 0.95)]:0}ms max={offs[^1]:0}ms, " +
                      $"from scrub cache {100.0 * mv.Count(f => f.shown != null && !f.full) / Math.Max(1, mv.Count(f => f.shown != null)):0}%");
        }
        string Settle(double s, bool full)
        {
            var hit = L.FirstOrDefault(f => f.wall >= s && Exact(f.shown, f.exp) && (!full || f.full));
            return hit.clip == null ? "never" : $"{hit.wall - s:0}";
        }
        if (ptStops.Count > 0)
            r.Add("after stopping (pause/scrub end) -> exact frame ms [any res / full res]: " + string.Join(", ", ptStops.Select(s => $"{Settle(s, false)}/{Settle(s, true)}")));
        if (ptSteps.Count > 0)
            r.Add("frame steps -> exact full-res frame ms: " + string.Join(", ", ptSteps.Select(s => Settle(s, true))));
        if (ScrubCache.DecodeCount > 0) r.Add($"scrub cache frames shown: {ScrubCache.DecodeCount}, avg decode+upload {ScrubCache.DecodeMsTotal / ScrubCache.DecodeCount:0.0} ms");
        if (ptCacheInfo != "") r.Add("media: " + ptCacheInfo);
        Pv.DisposeAll(); Thread.Sleep(500);
        r.Add($"gc: gen2={GC.CollectionCount(2) - ptGen2} pause={(GC.GetTotalPauseDuration() - ptGcPause).TotalMilliseconds:0}ms peakWorkingSet={Process.GetCurrentProcess().PeakWorkingSet64 >> 20}MB leftoverFfmpeg={Process.GetProcessesByName("ffmpeg").Length - ptFfmpeg}");
        if (Environment.GetEnvironmentVariable("CUTPRO_PTDUMP") != null)
            File.WriteAllLines(ptOut! + ".csv", L.Select(f => $"{f.wall:0},{f.t:0.000},{f.running},{f.shown:0.000},{f.exp:0.000},{f.full},{f.clip}"));
        if (Pv.Trace != null) File.WriteAllLines(ptOut! + ".trace.txt", Pv.Trace);
        File.WriteAllLines(ptOut!, r);
        Close();
    }
    /// <summary>Headless UI screenshots (CutPro.exe --uishot out.png [theme]): main layout with media, clips and a styled title
    /// selected; out.titles.png (Titles view); out.settings.png (Settings view + theme picker open).</summary>
    public async void UiShot(string path)
    {
        SetInspector(true, animate: false); ShowDock("Inspector");             // (whatever the saved layout: the shots show Effect Controls; settings are read-only here)
        foreach (var k in new[] { "Text", "Font", "Fill" }) sectionOpen[k] = true;
        var d = Path.Combine(Path.GetTempPath(), "cutpro_test");
        await ImportAsync(new[] { Path.Combine(d, "a.mp4"), Path.Combine(d, "b.mp4") });
        if (ed.Media.Count > 1) { ed.AddMedia(ed.Media[0], null, 0); ed.AddMedia(ed.Media[1], null, ed.End); }
        ed.Time = 0.5; ed.AddTitle(TextRole.LowerThird);
        ed.Seek(1.5);
        await Task.Delay(2500);
        void Shot(string p)
        {
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)RootGrid.ActualWidth, (int)RootGrid.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(RootGrid);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(p); enc.Save(fs);
        }
        static void Save(System.Windows.Media.Imaging.BitmapSource b, string p)
        {
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(b));
            using var fs = File.Create(p); enc.Save(fs);
        }
        Shot(path);
        ShowSideView("Titles"); await Task.Delay(600); Shot(Path.ChangeExtension(path, ".titles.png"));
        ShowSideView("Settings"); OpenPalette(PaletteMode.Theme); await Task.Delay(600); Shot(Path.ChangeExtension(path, ".settings.png"));
        ClosePalette();
        OpenPalette(); PaletteBox.Text = ">theme"; await Task.Delay(400); Shot(Path.ChangeExtension(path, ".palette.png")); ClosePalette();
        // a title-bar menu (popups aren't part of the window, so render the menu control itself)
        var viewBtn = FindVisualChildren<Button>(RootGrid).First(b => (b.Tag as string) == "View");
        Menu_Click(viewBtn, new RoutedEventArgs());
        await Task.Delay(400);
        if (lastMenu != null)
        {
            var mrtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)lastMenu.ActualWidth, (int)lastMenu.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            mrtb.Render(lastMenu);
            var menc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            menc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(mrtb));
            using (var fs = File.Create(Path.ChangeExtension(path, ".menu.png"))) menc.Save(fs);
            lastMenu.IsOpen = false;
        }
        // docking: the Window menu; the mixer docked right and the scopes in the bottom panel, the drop targets of a drag in progress;
        // the Program monitor in its own window (a screen capture of it)
        {
            var winBtn = FindVisualChildren<Button>(RootGrid).First(b => (b.Tag as string) == "Window");
            Menu_Click(winBtn, new RoutedEventArgs());
            await Task.Delay(400);
            if (lastMenu != null)
            {
                var wr = new System.Windows.Media.Imaging.RenderTargetBitmap((int)lastMenu.ActualWidth, (int)lastMenu.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                wr.Render(lastMenu); Save(wr, Path.ChangeExtension(path, ".windowmenu.png")); lastMenu.IsOpen = false;
            }
            ShowSideView("Project"); MoveDock("Mixer", "Right"); MoveDock("Scopes", "Bottom"); ShowDock("Scopes");
            await Task.Delay(900);
            UpdateDrop("Captions");                                            // (the overlay as a drag of the Captions header would show it)
            await Task.Delay(300); Shot(Path.ChangeExtension(path, ".docked.png"));
            dockLayer?.Children.Clear();
            MoveDock("Program", "Float", new Rect(Left + 120, Top + 90, 900, 560));
            await Task.Delay(1500);
            if (DockOf("Program")!.Win is { } pwin) Save(ScreenGrab.Window(pwin), Path.ChangeExtension(path, ".float.png"));
            UseWorkspace("Editing"); await Task.Delay(600);
        }
        // live switch (cross-fade path) to another theme, then restore the user's saved theme
        var original = ThemeManager.Current.Name;
        ShowSideView("Project");
        SetEditorTheme(original == "Light Theme" ? "Colorful Theme" : "Light Theme");
        await Task.Delay(800); Shot(Path.ChangeExtension(path, ".switched.png"));
        SetEditorTheme(original, animate: false);
        // object selection: a clip with a "Focus on object" ellipse mask, the AI tool armed
        if (ed.Clips.FirstOrDefault(c => c.Kind == ClipKind.Video && c.Start <= ed.Time && ed.Time < c.End) is { } vc)
        {
            ShowSideView("Project");
            ed.Select(vc);
            sectionOpen["Object masks"] = true;
            var mk = ed.AddMask(vc, MaskKind.Ellipse, new() { MaskGeometry.Shape(MaskKind.Ellipse, new Rect(0.3, 0.2, 0.4, 0.55)) }, Editor.SourceFrame(vc, ed.Time));
            mk.Effect = MaskFx.Blur; mk.Invert = true; mk.Amount = 30; mk.Feather = 20;
            // a second keyframe later on (the strip shows both and the interpolated span); playhead on an interpolated frame
            ed.SetMaskFrame(mk.Id, Editor.SourceFrame(vc, ed.Time) + 45, new() { MaskGeometry.Shape(MaskKind.Ellipse, new Rect(0.45, 0.3, 0.3, 0.45)) });
            ed.Seek(ed.Time + 0.5);
            Mo.ActiveMaskId = mk.Id; SetObjectTool(ObjectTool.None); ed.Touch(); BuildInspector();
            await Task.Delay(1200); Shot(Path.ChangeExtension(path, ".objects.png"));
            // keyframes: eased scale, held opacity steps, a wiggle formula on X; Effect Controls + KEYFRAMES panel (dope, graph)
            ed.Edit(() =>
            {
                double s0 = vc.In + 0.3, s1 = vc.In + 2.5;
                foreach (var k in new[] { Anim.SetKey(vc, AnimProp.Scale, s0, 100), Anim.SetKey(vc, AnimProp.Scale, s1, 70) }) Anim.ApplyInterp(k, "Easy Ease");
                Anim.SetKey(vc, AnimProp.Scale, vc.In + 4, 90);
                Anim.ApplyInterp(Anim.SetKey(vc, AnimProp.Opacity, s0, 100), "Hold"); Anim.SetKey(vc, AnimProp.Opacity, vc.In + 1.5, 70);
                Anim.SetKey(vc, AnimProp.Rotation, s0, 0); Anim.SetKey(vc, AnimProp.Rotation, s1, 8);
                Anim.Ensure(vc, AnimProp.X).Formula = "wiggle(2, 4)";
            });
            ed.Seek(vc.Start + 1.2); BuildInspector();
            ShowPanel("Keys"); KfMode_Click(KfDope, new RoutedEventArgs());
            await Task.Delay(700); Shot(Path.ChangeExtension(path, ".keys.png"));
            KfMode_Click(KfGraph, new RoutedEventArgs()); Kfe.ShowLane("Scale");
            await Task.Delay(500); Shot(Path.ChangeExtension(path, ".graph.png"));
            ShowPanel("Timeline");
            await Task.Delay(500); Shot(Path.ChangeExtension(path, ".keyline.png"));
            // themed glass alerts (screen capture: RenderTargetBitmap can't see the DWM acrylic)
            Activate();
            var aw = Alert.Preview(this, "ffmpeg could not decode clip.mkv at 00:00:04:29.\n\nThe clip may be damaged or still being written.", "Couldn't select the object", AlertKind.Error, "OK");
            await Task.Delay(900);
            Save(ScreenGrab.Window(this), Path.ChangeExtension(path, ".alert.png"));
            aw.Close();
            var qw = Alert.Preview(this, "AI object selection uses the MobileSAM model (about 43 MB, MIT licence). It is downloaded once and runs on this PC — nothing is uploaded.",
                                   "Download the AI model?", AlertKind.Question, "Download", "Not now");
            await Task.Delay(900);
            Save(ScreenGrab.Window(this), Path.ChangeExtension(path, ".confirm.png"));
            qw.Close();
            var pd = new Alert.ProgressDialog(this, "Exporting holiday.mp4", "Rendering… 42%", () => { });
            pd.Value = 0.42;
            await Task.Delay(900);
            Save(ScreenGrab.Window(this), Path.ChangeExtension(path, ".progress.png"));
            pd.Close();
            await Task.Delay(300);
            // sequence / export / publish dialogs are modal: Alert.Shown captures each one on screen, then closes it
            string shot = ""; int shotDelay = 1000;
            Alert.Shown = async w => { await Task.Delay(shotDelay); Save(ScreenGrab.Window(this), Path.ChangeExtension(path, shot)); w.Close(); };
            shot = ".sequence.png"; SequenceSettings();
            shot = ".addtracks.png"; AddTracksDialog(Tracks.V1);
            shot = ".export.png"; await ExportDialogAsync(ExportSettings.Presets.First(p => p.Name == "YouTube 1080p"));
            shot = ".accounts.png"; AccountsDialog("youtube");
            shot = ".publish.png"; shotDelay = 7000; OpenPublish(Path.Combine(d, "a.mp4"), "youtube");   // time for the automatic analysis
            Alert.Shown = null;
            // transitions: a Push Left on the V1 cut, selected (Effect Controls), Effects view open, playhead inside it
            if (ed.Clips.FirstOrDefault(x => x.Kind == ClipKind.Video && x.Track == 2 && x.Start > 0.5) is { } vb)
            {
                ed.Edit(() => ed.PlaceTransition(vb, true, "Push Left", 1.5));
                ed.SelectTransition(vb, true);
                ShowSideView("Effects"); ed.Seek(vb.Start + 0.2);
                await Task.Delay(1500); Shot(Path.ChangeExtension(path, ".transitions.png"));
            }
            // speed: a clip at 50 % (timeline label, Effect Controls → Time), then the Speed/Duration dialog
            if (ed.Clips.FirstOrDefault(x => x.Kind == ClipKind.Video && x.Track == 2) is { } sc)
            {
                ShowSideView("Project"); ed.Select(sc);
                ed.SetSpeed(ed.Clips.Where(ed.IsSel).ToList(), 50, null, false, true, true);
                ed.Select(sc); BuildInspector(); ed.Seek(sc.Start + 0.5);
                await Task.Delay(300);
                (Inspector.Parent as ScrollViewer)?.ScrollToEnd();
                await Task.Delay(1200); Shot(Path.ChangeExtension(path, ".speed.png"));
                Alert.Shown = async w => { await Task.Delay(900); Save(ScreenGrab.Window(this), Path.ChangeExtension(path, ".speeddialog.png")); w.Close(); };
                SpeedDialog(); Alert.Shown = null;
                ed.Undo(); (Inspector.Parent as ScrollViewer)?.ScrollToHome();
            }
            // stabilize: the section open on a V1 clip (rendering in the background: the status bar and the section show it), the timeline label
            if (ed.Clips.FirstOrDefault(x => x.Kind == ClipKind.Video && x.Track == 2) is { } stc)
            {
                ed.Edit(() => stc.Stab = new Stabilize { Smooth = 60 }); sectionOpen["Stabilize"] = true;
                ed.Select(stc); BuildInspector(); ed.Seek(stc.Start + 0.5);
                await Task.Delay(300);
                (Inspector.Parent as ScrollViewer)?.ScrollToEnd();
                await Task.Delay(2500); Shot(Path.ChangeExtension(path, ".stab.png"));
                ed.Undo(); sectionOpen["Stabilize"] = false; (Inspector.Parent as ScrollViewer)?.ScrollToHome();
            }
            // colour: a landscape (made by --colortest) graded, the Lumetri section open, scopes beside it; then a cartoon look
            var landVid = Path.Combine(Path.GetTempPath(), "cutpro_color", "land.mp4");
            if (File.Exists(landVid))
            {
                await ImportAsync(new[] { landVid });
                if (ed.Media.FirstOrDefault(x => x.Path == landVid) is { } lm)
                {
                    double at = ed.Time;
                    ed.Clips.Add(new Clip { MediaId = lm.Id, Kind = ClipKind.Video, Track = 0, Start = 0, In = 1, Dur = 4, NoAudio = true });
                    var gc = ed.Clips.Last();
                    gc.Grade = new ColorGrade { Temperature = 25, Contrast = 35, Highlights = -20, Saturation = 120, Look = "Teal & Orange", LookAmount = 70, Vignette = -45,
                                                Gain = new Wheel { Hue = 40, Amount = 25 }, Curve = new[] { 0, 0, 0.3, 0.25, 0.7, 0.78, 1, 1 } };
                    sectionOpen["Lumetri color"] = true;
                    ed.Select(gc); ed.RaiseChanged(); ShowSideView("Scopes"); ed.Seek(1.5);
                    await Task.Delay(1500);
                    foreach (var ex in FindVisualChildren<Expander>(Inspector)) if (ex.Header is TextBlock { Text: "LUMETRI COLOR" }) ex.BringIntoView();
                    await Task.Delay(800); Shot(Path.ChangeExtension(path, ".color.png"));
                    gc.Grade = null; gc.Toon = ToonStyle.Preset(ToonMode.Anime); sectionOpen["Lumetri color"] = false; sectionOpen["Stylize — cartoon, cel, anime"] = true;
                    ed.Touch(); BuildInspector(); ShowSideView("Project");
                    await Task.Delay(3000);
                    foreach (var ex in FindVisualChildren<Expander>(Inspector)) if (ex.Header is TextBlock { Text: var tx } && tx.StartsWith("STYLIZE")) ex.BringIntoView();
                    await Task.Delay(800); Shot(Path.ChangeExtension(path, ".toon.png"));
                    // Thumbnail Studio on the landscape, then Intro & Hook (modeless glass windows: screen captures)
                    gc.Toon = null; ed.Touch(); ed.Seek(1.5);
                    OpenThumbnails();
                    if (thumbWin != null) { await Task.Delay(6000); Save(ScreenGrab.Window(thumbWin), Path.ChangeExtension(path, ".thumbnail.png")); thumbWin.Close(); }
                    OpenHook();
                    if (hookWin != null) { await Task.Delay(6000); Save(ScreenGrab.Window(hookWin), Path.ChangeExtension(path, ".hook.png")); hookWin.Close(); }
                    ed.Clips.Remove(gc); ed.RaiseChanged(); ed.Seek(at);
                }
            }
            // composite: a green-screen clip (made by --compositetest) keyed, cropped and in Screen mode on V3; Ultra key + Crop sections open
            var greenVid = Path.Combine(Path.GetTempPath(), "cutpro_composite", "green.mp4");
            if (File.Exists(greenVid))
            {
                await ImportAsync(new[] { greenVid });
                if (ed.Media.FirstOrDefault(x => x.Path == greenVid) is { } gm)
                {
                    double at = ed.Time;
                    var kc = new Clip { MediaId = gm.Id, Kind = ClipKind.Video, Track = 0, Start = 0, In = 1, Dur = 4, NoAudio = true, Scale = 90, CropL = 8, CropB = 6, CropFeather = 10,
                                        Key = new ChromaKey { Color = "#00B140", Tolerance = 35, Softness = 25, Spill = 60 } };
                    ed.Clips.Add(kc);
                    sectionOpen["Ultra key — green / blue screen"] = true; sectionOpen["Crop and flip"] = true;
                    ed.Select(kc); ed.RaiseChanged(); ShowSideView("Project"); ed.Seek(1.5);
                    await Task.Delay(2500);
                    foreach (var ex in FindVisualChildren<Expander>(Inspector)) if (ex.Header is TextBlock { Text: var tx } && tx.StartsWith("ULTRA KEY")) ex.BringIntoView();
                    await Task.Delay(800); Shot(Path.ChangeExtension(path, ".composite.png"));
                    // look effects, drop shadow and corner pin on the same clip
                    kc.Looks = new LookFx { Sharpen = 30, Glow = 40 }; kc.Shadow = new DropShadow { Distance = 16, Softness = 18 };
                    kc.Pin = new CornerPin { TLX = 18, TLY = 12, TRX = 82, TRY = 4, BLX = 14, BLY = 92, BRX = 88, BRY = 84 }; kc.Lens = new LensDistort { Curvature = -25, Edge = 6 };
                    Anim.SetKey(kc, AnimProp.PinTLX, kc.In, 4); Anim.SetKey(kc, AnimProp.PinTLX, kc.In + 2.5, 26);      // (keyframed: the monitor shows the handles)
                    Anim.SetKey(kc, AnimProp.PinBRY, kc.In + 0.5, 96); Anim.SetKey(kc, AnimProp.PinBRY, kc.In + 2, 78);
                    sectionOpen["Ultra key — green / blue screen"] = false; sectionOpen["Crop and flip"] = false;
                    sectionOpen["Look effects — sharpen, glow, grain"] = true; sectionOpen["Drop shadow"] = true; sectionOpen["Corner pin"] = true; sectionOpen["Lens distortion"] = true;
                    ed.Touch(); BuildInspector();
                    await Task.Delay(3000);
                    foreach (var ex in FindVisualChildren<Expander>(Inspector)) if (ex.Header is TextBlock { Text: var tx2 } && tx2.StartsWith("CORNER PIN")) ex.BringIntoView();
                    await Task.Delay(800); Shot(Path.ChangeExtension(path, ".looks.png"));
                    sectionOpen["Look effects — sharpen, glow, grain"] = false; sectionOpen["Drop shadow"] = false; sectionOpen["Corner pin"] = false; sectionOpen["Lens distortion"] = false;
                    ed.Clips.Remove(kc); sectionOpen["Ultra key — green / blue screen"] = false; sectionOpen["Crop and flip"] = false;
                    ed.RaiseChanged(); ed.Seek(at);
                }
            }
            // audio mixer: faders / pan / solo set, playing so the meters move; waveforms on the audio clips
            var vw = Path.Combine(Path.GetTempPath(), "cutpro_audio", "voice.wav");                       // (made by --audiotest: a varied waveform on A2)
            if (File.Exists(vw)) { await ImportAsync(new[] { vw }); if (ed.Media.FirstOrDefault(x => x.Path == vw) is { } vm) ed.AddMedia(vm, 4, 0.4); }
            ShowSideView("Mixer"); ed.Select(ed.Clips.First(x => x.Kind == ClipKind.Audio));
            ed.Mix.Channels[3].Gain = -3.5; ed.Mix.Channels[3].Pan = -30; ed.Mix.Master = -1; ed.RaiseMixChanged();
            var ph = PanelRow.Height; PanelRow.Height = new GridLength(380);                                 // all five tracks (A2 has the voice)
            ed.Seek(0.3); Play(); await Task.Delay(2200); Shot(Path.ChangeExtension(path, ".mixer.png")); Pause();
            // automation: A1 fader keys shown on the timeline (Trk vol), a clip volume band with keys on A2's voice, the strips following the playhead
            {
                ed.Mix.Channels[3].GainKeys = new() { new Keyframe { T = 0.5, V = -18 }, new Keyframe { T = 2, V = 0 }, new Keyframe { T = 4, V = -6, Out = KeyType.Hold }, new Keyframe { T = 6, V = 2 } };
                ed.Mix.Channels[3].PanKeys = new() { new Keyframe { T = 1, V = -60 }, new Keyframe { T = 5, V = 60 } };
                Tl.ShowAuto(3, TimelineView.AutoShow.TrackVolume);
                if (ed.Clips.FirstOrDefault(x => x.Kind == ClipKind.Audio && x.Track == 4) is { } bvc)
                { Anim.SetKey(bvc, AnimProp.Volume, bvc.In + 0.4, 100); Anim.SetKey(bvc, AnimProp.Volume, bvc.In + 1.2, 35); Anim.SetKey(bvc, AnimProp.Volume, bvc.In + 2.4, 120); }
                AutoWrite.IsChecked = true;
                ed.RaiseMixChanged(); ed.Seek(2.6); ed.RaiseChanged();
                await Task.Delay(1200); Shot(Path.ChangeExtension(path, ".automation.png"));
                // track effects: a hall reverb + compressor on A1, its chain in Effect Controls (the strip's fx button)
                ed.Mix.Channels[3].Fx = new() { AudioDsp.New("Reverb", ("size", 85), ("damp", 30), ("pre", 35), ("wet", 30)), AudioDsp.New("Compressor") };
                ed.RaiseMixChanged(); ShowTrackFx(3);
                await Task.Delay(800); Shot(Path.ChangeExtension(path, ".trackfx.png"));
                ed.Mix.Channels[3].Fx = null; fxTrack = null;
                // a submix: A1 sent to "Voice bus" (hall reverb on the bus), its strip between the tracks and Mix, its effects in Effect Controls
                ed.Mix.Subs.Add(new SubMix { Name = "Voice bus", Gain = -2, Fx = new() { AudioDsp.New("Reverb", ("size", 85), ("damp", 30), ("pre", 35), ("wet", 30)) } });
                ed.Mix.Channels[3].Output = 0; ed.RaiseMixChanged(); ShowTrackFx(SubBase);
                Play(); await Task.Delay(1500); Shot(Path.ChangeExtension(path, ".submix.png")); Pause();
                ed.Mix.Channels[3].Output = -1; ed.Mix.Subs.Clear(); fxTrack = null; ed.RaiseMixChanged();
                AutoWrite.IsChecked = false; Tl.ShowAuto(3, TimelineView.AutoShow.ClipVolume);
            }
            PanelRow.Height = ph;
            ed.Mix = new MixState(); ed.RaiseMixChanged();
            // audio effects: the voice clean-up preset on the selected audio clip, its section open
            if (ed.Clips.FirstOrDefault(x => x.Kind == ClipKind.Audio) is { } ac)
            {
                ac.AudioFx = AudioDsp.VoicePreset(); sectionOpen["Audio effects"] = true;
                ed.Select(ac); ed.Touch(); BuildInspector(); ShowSideView("Project");
                await Task.Delay(1200);
                foreach (var ex in FindVisualChildren<Expander>(Inspector)) if (ex.Header is TextBlock { Text: "AUDIO EFFECTS" }) ex.BringIntoView();
                await Task.Delay(800); Shot(Path.ChangeExtension(path, ".audiofx.png"));
                ac.AudioFx = null; sectionOpen["Audio effects"] = false; ed.Touch();
            }
            // nested sequence: the first clip (picture + sound) nested, both sequences open as tabs, the Project panel lists them, the
            // nested clip selected (rendered in the background, then on the monitor)
            if (ed.Clips.FirstOrDefault(x => x.Kind == ClipKind.Video && ed.SeqOf(x) == null) is { } first)
            {
                ed.Select(first);
                var (nseq, _) = ed.NestSelection("Opening");
                if (nseq != null)
                {
                    var outer = ed.Seq;
                    OpenSequence(nseq); OpenSequence(outer);
                    ShowSideView("Project");
                    if (ed.Clips.FirstOrDefault(x => x.MediaId == nseq.Id && x.Kind == ClipKind.Video) is { } nc) { ed.Select(nc); ed.Seek(nc.Start + Math.Min(1, nc.Dur / 2)); }
                    var wait = Stopwatch.StartNew();
                    while (wait.ElapsedMilliseconds < 15000 && !Pv.Settled(ed.Time)) await Task.Delay(100);
                    await Task.Delay(600); Shot(Path.ChangeExtension(path, ".sequences.png"));
                    ed.Undo();                                                         // (back to the plain timeline for the shots below)
                }
            }
            // adjustment layer: on V3 over the first clip, graded (Warm Film + a vignette), selected (its inspector), the Effects view open
            {
                double keepT = ed.Time; ed.Seek(0);
                var adj = AddAdjustmentClip();
                if (adj != null)
                {
                    adj.Grade = new ColorGrade { Look = "Warm Film", Contrast = 15, Vignette = -45 }; adj.Track = 0; ed.RaiseChanged(); ed.Select(adj);
                    ShowSideView("Effects"); ed.Seek(1.2); BuildInspector();
                    await Task.Delay(1500); Shot(Path.ChangeExtension(path, ".adjust.png"));
                    ed.Undo(); ShowSideView("Project");
                }
                ed.Seek(keepT);
            }
            // captions: a few (as if made from speech) on V3, the Captions view open, the playhead in the second
            {
                var cues = new List<Cue> { new(0.4, 2.2, "Good morning, church, and welcome\nto our service today."), new(2.2, 4.1, "Please open your Bibles\nto Psalm twenty three."),
                                           new(4.1, 5.6, "The Lord is my shepherd,\nI shall not want."), new(5.6, 7.9, "He makes me lie down in green pastures.") };
                var (cst, cy) = Captioning.Look(ed, Captioning.Layouts[0]);
                Captioning.Place(ed, cues, 0, cst, cy);
                ShowSideView("Captions"); ed.Seek(2.8); ed.ClearSelection();
                await Task.Delay(1500); Shot(Path.ChangeExtension(path, ".captions.png"));
                ed.Undo(); ShowSideView("Project");
            }
            // the Source monitor: a clip opened with In / Out marks, next to Effect Controls
            if (ed.Media.FirstOrDefault(x => x.Type == ClipKind.Video) is { } sm)
            {
                OpenSource(sm, 1.2); sm.SrcIn = 0.8; sm.SrcOut = 3.4; SrcRefresh();
                await Task.Delay(1800); Shot(Path.ChangeExtension(path, ".source.png"));
                sm.SrcIn = sm.SrcOut = null; SrcRefresh(); ShowDock("Inspector");
            }
            // tracks: 6 video + 4 audio, named, one locked, mixed heights, pictures on the upper tracks, the mixer with its strips (undone after)
            if (ed.Media.FirstOrDefault(x => x.Type == ClipKind.Video) is { } pm && ed.Media.FirstOrDefault(x => x.HasAudio) is { } am)
            {
                ed.AddTracks(3, 0, 2, Tracks.Count);
                ed.RenameTrack(Tracks.V(6), "Titles"); ed.RenameTrack(Tracks.A(3), "Music"); ed.RenameTrack(Tracks.A(4), "Voice-over");
                ed.Edit(() =>
                {
                    for (int vn = 4; vn <= 5; vn++)
                        ed.Clips.Add(new Clip { MediaId = pm.Id, Kind = ClipKind.Video, Track = Tracks.V(vn), Start = 0.4 * vn - 1, Dur = 4, In = 0.5, NoAudio = true, Scale = 32, X = vn == 4 ? 30 : -30, Y = -28 });
                    ed.Clips.Add(new Clip { MediaId = am.Id, Kind = ClipKind.Audio, Track = Tracks.A(3), Start = 0, Dur = 5.5, In = 0.3, Volume = 70 });
                    ed.Clips.Add(new Clip { MediaId = am.Id, Kind = ClipKind.Audio, Track = Tracks.A(4), Start = 1.5, Dur = 3, In = 1 });
                });
                ed.SetLocked(Tracks.V(5), true);
                ed.SetTrackHeight(Tracks.V(6), 36); ed.SetTrackHeight(Tracks.V1, 74); ed.SetTrackHeight(Tracks.A(4), 84);
                ed.ClearSelection(); ed.Seek(2.4); ShowSideView("Mixer");
                await Task.Delay(1800); Tl.ScrollY = 30;
                await Task.Delay(600); Shot(Path.ChangeExtension(path, ".tracks.png"));
                for (int i = 0; i < 5; i++) ed.Undo();                                   // (clips, three names, the tracks: back to 3 + 2)
                Tl.ScrollY = 0; ShowSideView("Project");
            }
            // markers (sequence: named, coloured, a chapter-less one, one with a length; a clip marker), the History panel, the Markers menu,
            // the shortcuts list, a menu in German, and the marker / Paste Attributes / Properties / shortcut dialogs
            {
                vc = ed.Clips.First(c => c.Id == vc.Id);                         // (the undos above replaced the clip objects)
                ed.AddMarker(0, "Intro"); ed.AddMarker(1.2, "Welcome");
                var mk2 = ed.AddMarker(2.6, "Song"); ed.UpdateMarker(mk2, null, m => { m.Color = MarkerColor.Purple; m.Dur = 1.2; });
                var mk3 = ed.AddMarker(4.2, "Note"); ed.UpdateMarker(mk3, null, m => { m.Color = MarkerColor.Yellow; m.Chapter = false; m.Note = "fix the colour here"; });
                ed.AddClipMarker(vc, vc.Start + 0.8);
                ed.Select(vc); ed.CopySelection(); ed.Seek(1.9);
                ShowDock("History"); Tl.ZoomTo(0, ed.End);
                await Task.Delay(900); Shot(Path.ChangeExtension(path, ".markers.png"));
                var mkBtn = FindVisualChildren<Button>(RootGrid).First(b => (b.Tag as string) == "Marker");
                Menu_Click(mkBtn, new RoutedEventArgs()); await Task.Delay(400);
                if (lastMenu != null) { var r = new System.Windows.Media.Imaging.RenderTargetBitmap((int)lastMenu.ActualWidth, (int)lastMenu.ActualHeight, 96, 96, PixelFormats.Pbgra32); r.Render(lastMenu); Save(r, Path.ChangeExtension(path, ".markermenu.png")); lastMenu.IsOpen = false; }
                ShowSideView("Settings"); await Task.Delay(300);
                if (shortcutFilter != null)
                {   // (scrolled so the preferences and the filtered shortcut list fill the view)
                    shortcutFilter.Text = "mark"; await Task.Delay(200);
                    DependencyObject? up = shortcutFilter; while (up != null && up is not ScrollViewer) up = VisualTreeHelper.GetParent(up);
                    if (up is ScrollViewer sv && sv.Content is UIElement content) sv.ScrollToVerticalOffset(shortcutFilter.TranslatePoint(new Point(0, 0), content).Y - 150);
                }
                await Task.Delay(500); Shot(Path.ChangeExtension(path, ".shortcuts.png"));
                if (shortcutFilter != null) shortcutFilter.Text = "";
                SetLanguage("de");
                var editBtn = FindVisualChildren<Button>(RootGrid).First(b => (b.Tag as string) == "Edit");
                Menu_Click(editBtn, new RoutedEventArgs()); await Task.Delay(500);
                if (lastMenu != null) { var r = new System.Windows.Media.Imaging.RenderTargetBitmap((int)lastMenu.ActualWidth, (int)lastMenu.ActualHeight, 96, 96, PixelFormats.Pbgra32); r.Render(lastMenu); Save(r, Path.ChangeExtension(path, ".german.png")); lastMenu.IsOpen = false; }
                await Task.Delay(300); Shot(Path.ChangeExtension(path, ".germanwin.png"));
                SetLanguage("en");
                ShowSideView("Project");
                string dshot = "";
                Alert.Shown = async w => { await Task.Delay(900); Save(ScreenGrab.Window(this), Path.ChangeExtension(path, dshot)); w.Close(); };
                dshot = ".marker.png"; EditMarker(mk2, null);
                dshot = ".pasteattr.png"; vc.Scale = 80; vc.Grade = new ColorGrade { Exposure = 0.3 }; ed.CopySelection(); PasteAttributesDialog();
                dshot = ".properties.png"; if (ed.MediaOf(vc) is { } pm2) ShowProperties(pm2);
                dshot = ".shortcutedit.png"; EditShortcut(Keymap.Get("marker.add")!);
                Alert.Shown = null;
                ShowDock("Inspector");
            }
            // the Project panel with bins, labels and ratings (icon and list view), the Transcript panel, the Clip menu, the colour match dialog
            {
                ed.ClearMarkers();
                var media = ed.Media.Where(m => m.Seq == null).ToList();
                var b1 = ed.NewBin("", "Interviews"); ed.NewBin("", "B-roll"); ed.NewBin(b1, "Day 2");
                if (media.Count > 0) { media[0].Label = MarkerColor.Green; media[0].Rating = 4; media[0].Keywords = "sermon"; }
                if (media.Count > 1) { media[1].Label = MarkerColor.Purple; media[1].Rating = 2; }
                RefreshBin(); ShowSideView("Project");
                await Task.Delay(700); Shot(Path.ChangeExtension(path, ".bins.png"));
                S.BinListView = true; ApplyBinView(); binSearch!.Text = "";
                await Task.Delay(500); Shot(Path.ChangeExtension(path, ".binlist.png"));
                S.BinListView = false; ApplyBinView();
                if (media.Count > 0)
                    media[0].Words = "In the beginning was the Word, and the Word was with God, and the Word was God. um The same was in the beginning with God."
                        .Split(' ').Select((w, i) => new Word(w, 0.2 + i * 0.32, 0.2 + i * 0.32 + 0.26)).ToList();
                ShowDock("Transcript"); trStale = true; await Task.Delay(500); RefreshTranscript();
                if (trWords.Count > 6) { trSel.Clear(); trSel.Add(4); trSel.Add(5); trSel.Add(6); PaintTranscript(); }
                ed.Seek(1.1); await Task.Delay(500); FollowTranscript();
                await Task.Delay(400); Shot(Path.ChangeExtension(path, ".transcript.png"));
                ShowSideView("Project");
                ed.Select(vc);
                var clipBtn = FindVisualChildren<Button>(RootGrid).First(b => (b.Tag as string) == "Clip");
                Menu_Click(clipBtn, new RoutedEventArgs()); await Task.Delay(400);
                if (lastMenu != null) { var r = new System.Windows.Media.Imaging.RenderTargetBitmap((int)lastMenu.ActualWidth, (int)lastMenu.ActualHeight, 96, 96, PixelFormats.Pbgra32); r.Render(lastMenu); Save(r, Path.ChangeExtension(path, ".clipmenu.png")); lastMenu.IsOpen = false; }
                Alert.Shown = async w => { await Task.Delay(900); Save(ScreenGrab.Window(this), Path.ChangeExtension(path, ".colormatch.png")); w.Close(); };
                var auto = Alert.Auto; Alert.Auto = null;
                ColorMatchDialog();
                Alert.Auto = auto; Alert.Shown = null;
                foreach (var m in media) { m.Words = null; m.Label = null; m.Rating = 0; m.Keywords = ""; }
                ShowDock("Inspector");
            }
            // the extra-high features: trim tools, opacity band, render queue, HSL secondary, distort, text animation, New Item
            await ExtraShots(path, vc, Shot, Save);
            await MultiCamShot(path, Shot);
            await GraphicsShots(path, Shot);
            // Shorts: 9:16, Fill (crop + pan) then Fit + blurred background
            Format_Click(Fmt916, new RoutedEventArgs()); ed.Select(vc); BuildInspector();
            await Task.Delay(1500); Shot(Path.ChangeExtension(path, ".shorts.png"));
            LayoutBox.SelectedIndex = 1;
            await Task.Delay(1500); Shot(Path.ChangeExtension(path, ".shortsblur.png"));
        }
        Close();
    }

    static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var ch = VisualTreeHelper.GetChild(root, i);
            if (ch is T t) yield return t;
            foreach (var x in FindVisualChildren<T>(ch)) yield return x;
        }
    }

    void Window_Closing(object? s, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmQueueExit() || !ConfirmDiscard()) { e.Cancel = true; return; }
        AutoSave.EndSession();
        CompositionTarget.Rendering -= OnRendering; audio.Dispose(); Pv.DisposeAll();
    }
}
