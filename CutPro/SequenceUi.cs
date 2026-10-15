using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CutPro;

/// <summary>Sequence format UI: the Format toggle (16:9 / Shorts 9:16 / 1:1 / 4:5) and layout in the Program monitor bar,
/// the status-bar format chip, the Sequence settings dialog, and the clip-mismatch offer for the first clip.</summary>
public partial class MainWindow
{
    bool seqSync;

    void InitSequence()
    {
        Editor.SequenceChanged += OnSequenceChanged;
        ed.FirstVideo += OfferMatch;
        OnSequenceChanged();
    }

    void OnSequenceChanged()
    {
        Pv.Resize();
        StatusSeqText.Text = $"{Editor.OutW}×{Editor.OutH} · {TimeUtil.Label(TimeUtil.Fps)} fps";
        double a = Editor.W / (double)Editor.H;
        seqSync = true;
        Fmt169.IsChecked = Math.Abs(a - 16 / 9.0) < 0.02; Fmt916.IsChecked = Math.Abs(a - 9 / 16.0) < 0.02;
        Fmt11.IsChecked = Math.Abs(a - 1) < 0.02; Fmt45.IsChecked = Math.Abs(a - 0.8) < 0.02;
        bool other = Fmt169.IsChecked != true;
        LayoutBox.Visibility = other || Editor.Reframe != ReframeMode.Fit ? Visibility.Visible : Visibility.Collapsed;
        LayoutBox.SelectedIndex = Editor.Reframe switch { ReframeMode.Fill => 0, ReframeMode.FitBlur => 1, _ => 2 };
        AutoReframeBtn.Visibility = Editor.Reframe == ReframeMode.Fill ? Visibility.Visible : Visibility.Collapsed;
        seqSync = false;
        BuildInspector(); Tl.InvalidateVisual(); Mo.InvalidateVisual(); if (Kfe.IsVisible) Kfe.InvalidateVisual();
        if (IsLoaded) Dispatcher.BeginInvoke(FitEditorTools, System.Windows.Threading.DispatcherPriority.Loaded);   // layout box / Auto reframe shown or hidden
    }

    /// <summary>The monitor's tool strip shares the row with the Program tab: when they'd overlap, drop the labels ("Format",
    /// "Objects", "Select object" keeps its ✦ icon and tooltip) and the sequence name.</summary>
    void EditorGrid_SizeChanged(object? s, SizeChangedEventArgs? e) => FitEditorTools();
    void FitEditorTools()
    {
        void Compact(bool c)
        {
            var v = c ? Visibility.Collapsed : Visibility.Visible;
            FmtLabel.Visibility = ObjLabel.Visibility = AiLabel.Visibility = ProgramTabSub.Visibility = v;
        }
        Compact(false);
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        EditorTools.Measure(inf); ProgramTab.Measure(inf);
        if (EditorTools.DesiredSize.Width + EditorTools.Margin.Right + ProgramTab.DesiredSize.Width + 12 > EditorGrid.ActualWidth) Compact(true);
    }

    /// <summary>Timeline / keyframe toolbars share their row with the panel tabs: when narrow (side panels open, material-theme
    /// gaps), icon-only Select / Razor, a shorter zoom slider, then fewer easing buttons.</summary>
    void PanelCard_SizeChanged(object s, SizeChangedEventArgs e) => FitPanelTools();
    void FitPanelTools()
    {
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        double avail = PanelGrid.ActualWidth;                                  // (the bottom panel may be in its own window)
        bool Fits(FrameworkElement bar) { bar.Measure(inf); PanelTabs.Measure(inf); return PanelTabs.DesiredSize.Width + PanelTabs.Margin.Left + bar.DesiredSize.Width + bar.Margin.Right + 12 <= avail; }
        SelectLabel.Visibility = RazorLabel.Visibility = Visibility.Visible; ZoomSlider.Width = 140;
        if (!Fits(TlBar)) { SelectLabel.Visibility = RazorLabel.Visibility = Visibility.Collapsed; ZoomSlider.Width = 80; }
        var optional = KeyBar.Children.OfType<Button>().Where(b => (b.Tag as string) is "Ease In" or "Ease Out" or "Auto Bezier").ToList();
        foreach (var b in optional) b.Visibility = Visibility.Visible;
        if (!Fits(KeyBar)) foreach (var b in optional) b.Visibility = Visibility.Collapsed;
    }

    /// <summary>Output size for an aspect, keeping the current size class (the short side: 720, 1080, 2160 …); the social
    /// shapes (Shorts, square, 4:5) use at least 1080 — what every platform asks for.</summary>
    static (int w, int h) SizeFor(string aspect)
    {
        int s = Math.Min(Editor.OutW, Editor.OutH);
        if (aspect != "16:9") s = Math.Max(s, 1080);
        return aspect switch
        {
            "9:16" => (s, ExportSettings.Even(s * 16 / 9.0)),
            "1:1" => (s, s),
            "4:5" => (s, ExportSettings.Even(s * 5 / 4.0)),
            _ => (ExportSettings.Even(s * 16 / 9.0), s),
        };
    }

    void Format_Click(object sender, RoutedEventArgs e)
    {
        string aspect = (string)((FrameworkElement)sender).Tag;
        var (w, h) = SizeFor(aspect);
        // Shorts and other narrow frames start in Fill (crop); back to 16:9 fits again
        var mode = aspect == "16:9" ? ReframeMode.Fit : Editor.Reframe == ReframeMode.Fit ? ReframeMode.Fill : Editor.Reframe;
        ed.ApplySequence(w, h, TimeUtil.Fps, mode);
        if (aspect == "9:16")
        {
            double len = ed.End;
            string fit = len <= 60 ? "fits every Shorts / Reels / TikTok limit" : len <= 90 ? "fits YouTube Shorts, TikTok and Reels (Facebook Reels max 90 s)"
                       : len <= 180 ? "fits YouTube Shorts (3 min), TikTok and Instagram Reels; too long for Facebook Reels (90 s)"
                       : "longer than a YouTube Short (3 min) — fine for TikTok (10 min) and Instagram Reels";
            SetTask($"Shorts 9:16 ({w}×{h}) — {TimeUtil.Format(len, false)} {fit}. Layout: {LayoutBox.Text}; Ctrl+Z goes back", 9000);
        }
        else SetTask($"Format {aspect} ({w}×{h}) — Ctrl+Z goes back", 4000);
    }

    void Layout_Changed(object s, SelectionChangedEventArgs e)
    {
        if (seqSync || !IsLoaded) return;
        var mode = LayoutBox.SelectedIndex switch { 0 => ReframeMode.Fill, 1 => ReframeMode.FitBlur, _ => ReframeMode.Fit };
        if (mode != Editor.Reframe) ed.ApplySequence(Editor.OutW, Editor.OutH, TimeUtil.Fps, mode);
    }

    void StatusSeq_Click(object s, RoutedEventArgs e) => SequenceSettings();

    CancellationTokenSource? reframeCts;
    async void AutoReframe_Click(object s, RoutedEventArgs e)
    {
        if (reframeCts != null) { reframeCts.Cancel(); return; }
        var clips = ed.Clips.Where(Editor.CoversFrame).OrderBy(c => c.Start).ToList();
        if (clips.Count == 0) { SetTask("Auto reframe works on V1 clips in the Fill layout", 4000); return; }
        Pause();
        var cts = reframeCts = new CancellationTokenSource();
        var results = new List<Reframe.Result>();
        try
        {
            for (int i = 0; i < clips.Count; i++)
            {
                int k = i;
                var prog = new Progress<double>(p => SetTask($"Auto reframe: clip {k + 1} of {clips.Count} — {p * 100:0}% (click to stop)", 0));
                SetTask($"Auto reframe: clip {i + 1} of {clips.Count}…", 0);
                if (await Reframe.AnalyzeAsync(ed, clips[i], prog, cts.Token) is { } r) results.Add(r);
            }
            Reframe.Apply(ed, results);
            int moving = results.Count(r => r.Keys.Count > 1);
            SetTask(results.Count == 0 ? "Nothing to reframe — every V1 clip already fills the frame without cropping"
                    : $"Auto reframe: {results.Count} clip{(results.Count == 1 ? "" : "s")} ({moving} following movement). Fine-tune with Reframe pan / its keyframes; Ctrl+Z undoes", 8000);
        }
        catch (OperationCanceledException) { SetTask("Auto reframe stopped", 3000); }
        catch (Exception ex) { Alert.Show(this, ex.Message, "Auto reframe failed", AlertKind.Error); }
        finally { reframeCts = null; }
    }

    static readonly (string name, int w, int h)[] SizePresets =
    {
        ("HD 1080p 16:9", 1920, 1080), ("HD 720p 16:9", 1280, 720), ("4K UHD 16:9", 3840, 2160), ("QHD 1440p 16:9", 2560, 1440),
        ("Shorts / Reels / TikTok 9:16", 1080, 1920), ("Square 1:1", 1080, 1080), ("Portrait 4:5", 1080, 1350),
        ("Cinema 2.39:1", 1920, 804), ("Classic 4:3", 1440, 1080),
    };

    /// <summary>Sequence settings (glass dialog): frame size, frame rate, layout. One undo step.</summary>
    void SequenceSettings()
    {
        var form = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        Grid Row(string label, UIElement c)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; l.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
            Grid.SetColumn(c, 1); g.Children.Add(l); g.Children.Add(c); form.Children.Add(g); return g;
        }
        var preset = new ComboBox { ItemsSource = SizePresets.Select(p => $"{p.name}  ({p.w}×{p.h})").Append("Custom").ToList() };
        var wBox = new TextBox { Text = Editor.OutW.ToString(), Width = 70 }; var hBox = new TextBox { Text = Editor.OutH.ToString(), Width = 70 };
        var size = new StackPanel { Orientation = Orientation.Horizontal };
        size.Children.Add(wBox); size.Children.Add(new TextBlock { Text = "  ×  ", VerticalAlignment = VerticalAlignment.Center }); size.Children.Add(hBox);
        var rate = new ComboBox { ItemsSource = TimeUtil.Standard.Select(f => TimeUtil.Label(f) + " fps").ToList() };
        rate.SelectedIndex = Array.FindIndex(TimeUtil.Standard, f => Math.Abs(TimeUtil.Exact(f) - TimeUtil.Fps) < 0.001);
        var layout = new ComboBox { ItemsSource = new[] { "Fill (crop to the frame)", "Fit + blurred background", "Fit (black bars)" },
                                    SelectedIndex = Editor.Reframe switch { ReframeMode.Fill => 0, ReframeMode.FitBlur => 1, _ => 2 } };
        Row("Frame size", preset); Row("Width × height", size); Row("Frame rate", rate); Row("Other shapes", layout);
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0),
                                   Text = "Titles, blur and positions keep their look at any size (they scale with the frame). A new frame rate moves clip edges, keyframes and mask frames to its frame grid. Ctrl+Z undoes the change." };
        note.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        form.Children.Add(note);
        void SyncPreset()
        {
            int.TryParse(wBox.Text, out int w); int.TryParse(hBox.Text, out int h);
            int i = Array.FindIndex(SizePresets, p => p.w == w && p.h == h);
            preset.SelectedIndex = i >= 0 ? i : SizePresets.Length;
        }
        SyncPreset();
        bool busy = false;
        preset.SelectionChanged += (_, _) =>
        {
            if (busy || preset.SelectedIndex < 0 || preset.SelectedIndex >= SizePresets.Length) return;
            busy = true; wBox.Text = SizePresets[preset.SelectedIndex].w.ToString(); hBox.Text = SizePresets[preset.SelectedIndex].h.ToString(); busy = false;
        };
        wBox.TextChanged += (_, _) => { if (!busy) { busy = true; SyncPreset(); busy = false; } };
        hBox.TextChanged += (_, _) => { if (!busy) { busy = true; SyncPreset(); busy = false; } };
        if (Alert.Show(this, $"Currently {Editor.OutW}×{Editor.OutH} at {TimeUtil.Label(TimeUtil.Fps)} fps.", "Sequence settings", AlertKind.Info, new[] { "Apply", "Cancel" }, form) != 0) return;
        if (!int.TryParse(wBox.Text, out int nw) || !int.TryParse(hBox.Text, out int nh) || nw < 16 || nh < 16 || nw > 16384 || nh > 16384)
        { Alert.Show(this, "Width and height must be whole numbers between 16 and 16384.", "Sequence settings", AlertKind.Warning); return; }
        double fps = rate.SelectedIndex >= 0 ? TimeUtil.Standard[rate.SelectedIndex] : TimeUtil.Fps;
        var mode = layout.SelectedIndex switch { 0 => ReframeMode.Fill, 1 => ReframeMode.FitBlur, _ => ReframeMode.Fit };
        ed.ApplySequence(nw, nh, fps, mode);
        SetTask($"Sequence: {Editor.OutW}×{Editor.OutH} · {TimeUtil.Label(TimeUtil.Fps)} fps (Ctrl+Z undoes)", 5000);
    }

    /// <summary>Premiere's clip-mismatch warning: the first clip on an empty timeline can set the sequence format.</summary>
    void OfferMatch(MediaItem m)
    {
        if (m.Width <= 0 || m.Height <= 0 || AppSettings.ReadOnly) return;   // automated runs keep their sequence (and never block on a dialog)
        double fps = m.Fps > 0 ? TimeUtil.Standard.OrderBy(f => Math.Abs(TimeUtil.Exact(f) - m.Fps)).First() : TimeUtil.Fps;
        bool sameSize = m.Width == Editor.OutW && m.Height == Editor.OutH, sameRate = Math.Abs(TimeUtil.Exact(fps) - TimeUtil.Fps) < 0.001;
        if (sameSize && sameRate) return;
        Dispatcher.BeginInvoke(() =>
        {
            int pick = Alert.Show(this,
                $"{m.Name} is {m.Width}×{m.Height} at {TimeUtil.Label(TimeUtil.Exact(fps))} fps; the sequence is {Editor.OutW}×{Editor.OutH} at {TimeUtil.Label(TimeUtil.Fps)} fps.",
                "Clip mismatch", AlertKind.Question, new[] { "Change sequence settings", "Keep existing settings" });
            if (pick != 0) return;
            ed.ApplySequence(m.Width, m.Height, fps, Editor.Reframe);
            SetTask($"Sequence set to {Editor.OutW}×{Editor.OutH} · {TimeUtil.Label(TimeUtil.Fps)} fps to match {m.Name}", 5000);
        });
    }
}
