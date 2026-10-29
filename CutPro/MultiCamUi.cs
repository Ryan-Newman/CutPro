using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>The Program monitor's Multi-Camera view: every camera of the multicam clip at the playhead in a grid (numbered; the program's camera
/// framed). Each camera decodes at a small size on its own (scrub-cache frames while paused) — the program monitor's machinery is left alone.</summary>
public sealed class MultiCamView : Grid
{
    public sealed class Cell
    {
        public Border Frame = null!; public Image Pic = null!; public TextBlock Label = null!, Empty = null!;
        public WriteableBitmap? Dec, Scrub;
        public FrameStream? Fs, One;
        public double OneSrc = double.NaN;
        public long ShownIdx = -1;
        public string? ShownKey;
        public void Show(byte[] buf, int w, int h)
        {
            if (Dec == null || Dec.PixelWidth != w || Dec.PixelHeight != h) Dec = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            Dec.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
            if (!ReferenceEquals(Pic.Source, Dec)) Pic.Source = Dec;
            Empty.Visibility = Visibility.Collapsed;
        }
        public void ShowScrub() { if (!ReferenceEquals(Pic.Source, Scrub)) Pic.Source = Scrub; Empty.Visibility = Visibility.Collapsed; }
        public void Blank()
        {
            Stop();
            if (Pic.Source != null) Pic.Source = null;
            Empty.Visibility = Visibility.Visible; ShownKey = null; ShownIdx = -1;
        }
        public void Stop() { Fs?.Dispose(); Fs = null; One?.Dispose(); One = null; OneSrc = double.NaN; }
    }
    public readonly List<Cell> Cells = new();
    readonly UniformGrid grid = new();
    readonly TextBlock hint = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center };
    /// <summary>A camera was clicked (its number from 0; Ctrl held).</summary>
    public event Action<int, bool>? Picked;
    int marked = -2; bool markedLive;

    /// <summary>The cells' shape (the sequence's aspect): the grid is the largest that fits with cells of this shape, centred.</summary>
    public double Aspect { get => aspect; set { if (Math.Abs(value - aspect) > 1e-6) { aspect = value; Fit(); } } }
    double aspect = 16 / 9.0;

    public MultiCamView()
    {
        hint.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        grid.HorizontalAlignment = HorizontalAlignment.Center; grid.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(grid); Children.Add(hint);
        SizeChanged += (_, _) => Fit();
    }
    void Fit()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        int cols = Math.Max(1, grid.Columns), rows = Math.Max(1, grid.Rows);
        const double pad = 4 + 6;                                            // (a cell's margin and frame)
        double cw = Math.Min(w / cols, ((h / rows) - pad) * aspect + pad);
        grid.Width = cw * cols; grid.Height = ((cw - pad) / aspect + pad) * rows;
    }

    public void Build(IReadOnlyList<string> names)
    {
        Release();
        grid.Children.Clear(); Cells.Clear(); marked = -2;
        int n = names.Count, cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n)));
        grid.Columns = cols; grid.Rows = Math.Max(1, (int)Math.Ceiling(n / (double)cols));
        for (int i = 0; i < n; i++)
        {
            var c = new Cell();
            var stage = new Grid { Background = Brushes.Black, ClipToBounds = true };
            c.Pic = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(c.Pic, BitmapScalingMode.Linear);
            c.Empty = new TextBlock { Text = "no picture", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
            c.Empty.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
            var tag = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4), Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(3) };
            tag.SetResourceReference(Border.BackgroundProperty, "B.Scrim");
            c.Label = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            c.Label.SetResourceReference(TextBlock.ForegroundProperty, "B.Accent.Fg");
            tag.Child = c.Label;
            stage.Children.Add(c.Pic); stage.Children.Add(c.Empty); stage.Children.Add(tag);
            c.Frame = new Border { BorderThickness = new Thickness(3), Margin = new Thickness(2), Child = stage, Cursor = Cursors.Hand };
            c.Frame.SetResourceReference(Border.BorderBrushProperty, "B.Border");
            int a = i;
            c.Frame.MouseLeftButtonDown += (_, e) => { Picked?.Invoke(a, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); e.Handled = true; };
            System.Windows.Automation.AutomationProperties.SetName(c.Frame, names[i]);
            grid.Children.Add(c.Frame); Cells.Add(c);
        }
        SetNames(names);
        Fit();
    }

    public void SetNames(IReadOnlyList<string> names)
    {
        for (int i = 0; i < Cells.Count && i < names.Count; i++)
        {
            var t = $"{i + 1}  {names[i]}";
            if (Cells[i].Label.Text != t) { Cells[i].Label.Text = t; Cells[i].Frame.ToolTip = $"{names[i]} — click: this camera (Ctrl+click: from the playhead on), key {i + 1}"; }
        }
    }

    /// <summary>The program's camera framed: yellow, red while switches are being recorded.</summary>
    public void Mark(int active, bool live)
    {
        if (active == marked && live == markedLive) return;
        marked = active; markedLive = live;
        for (int i = 0; i < Cells.Count; i++) Cells[i].Frame.SetResourceReference(Border.BorderBrushProperty, i == active ? (live ? "B.Error" : "B.Warn") : "B.Border");
    }

    public void Hint(string? text)
    {
        hint.Text = text ?? "";
        hint.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        grid.Visibility = text == null ? Visibility.Visible : Visibility.Hidden;
    }

    public void Release() { foreach (var c in Cells) c.Stop(); }
}

/// <summary>
/// Multi-camera editing (partial MainWindow), Premiere's: Project panel ▸ Create Multi-Camera Source Sequence… lines the selected clips up (by
/// sound, timecode, In points or their starts) into a source sequence; on a timeline it becomes switchable clips. The Program monitor's
/// Multi-Camera view (Shift+0) shows every camera at the playhead next to the program: click a camera or press 1–9 — paused, the clip under the
/// playhead switches (Ctrl: from the playhead on); playing, the switches are recorded and become cuts when playback stops (one undo step).
/// Clip ▸ Multi-Camera: Camera n for the selected clips, Flatten.
/// </summary>
public partial class MainWindow
{
    MultiCamView? mcView; bool mcOn;
    string? mcSeq; int mcGen = -1; double mcTau = double.NaN; long mcMoved;
    readonly List<(double t, int cam)> mcRec = new();
    int mcRecTrack = -1, mcLive = -1;
    /// <summary>The Multi-Camera view is on (tests).</summary>
    bool MultiCamOn => mcOn;

    void ToggleMultiCamView() => SetMultiCamView(!mcOn);
    void SetMultiCamView(bool on)
    {
        if (on && mcView == null)
        {
            mcView = new MultiCamView();
            Grid.SetRow(mcView, 1);
            mcView.Picked += (a, ctrl) => McPick(a, ctrl);
            EditorGrid.Children.Add(mcView);
        }
        mcOn = on;
        if (mcView != null) mcView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) { mcView?.Release(); mcSeq = null; }
        MultiCamBtn.IsChecked = on;
        McLayout();
        if (on)
        {
            foreach (var m in ed.Sequences.Where(s => s.MultiCam != null).SelectMany(s => s.Clips).Select(c => ed.Media.FirstOrDefault(x => x.Id == c.MediaId)).Where(m => m is { Type: ClipKind.Video }).Distinct())
                ScrubCache.Ensure(m!);
            SetTask("Multi-Camera view: click a camera or press 1–9 to switch (Ctrl: from the playhead on); while playing the switches are recorded", 4500);
        }
    }

    /// <summary>The view takes the left half of the monitor area; the program the right half.</summary>
    void McLayout()
    {
        if (mcView == null) return;
        double w = EditorGrid.ActualWidth;
        bool home = MonitorBox.Parent == EditorGrid;
        if (mcOn)
        {
            mcView.HorizontalAlignment = HorizontalAlignment.Left;
            mcView.Width = Math.Max(0, w / 2 - 18); mcView.Margin = new Thickness(12, 12, 0, 12);
            if (home) MonitorBox.Margin = new Thickness(w / 2 + 6, 12, 12, 12);
        }
        else if (home) MonitorBox.Margin = new Thickness(12);
    }

    /// <summary>The multicam source and time the view shows: the multicam clip at the playhead (and its camera), or the open sequence when it is a source itself.</summary>
    (Sequence m, double tau, int active)? McContext()
    {
        if (ed.MultiCamClipAt(ed.Time) is { } c && ed.SeqById(c.Cam!.Seq) is { MultiCam: not null } m && ed.CamTau(c, ed.Time) is double tau) return (m, tau, c.Cam.Angle);
        if (ed.Seq.MultiCam != null) return (ed.Seq, ed.Time, -1);
        return null;
    }

    /// <summary>Per frame (OnRendering), only while the view shows.</summary>
    void McTick()
    {
        if (!mcOn || mcView == null || !Shows(mcView)) return;
        if (McContext() is not { } ctx)
        {
            if (mcSeq != null) { mcView.Release(); mcSeq = null; }
            mcView.Hint("No multi-camera clip at the playhead.\nSelect clips in the Project panel ▸ right-click ▸ Create Multi-Camera Source Sequence…, then drag it onto the timeline.");
            return;
        }
        var (m, tau, active) = ctx;
        mcView.Hint(null);
        mcView.Aspect = Editor.W / (double)Editor.H;
        int n = Editor.CamCount(m);
        if (m.Id != mcSeq || mcView.Cells.Count != n || Editor.Gen != mcGen)
        {
            var names = Enumerable.Range(0, n).Select(a => Editor.CamName(m, a) + (m.Clips.FirstOrDefault(c => c.Track == Editor.CamRow(m, a) && c.Kind == ClipKind.Video) is { } x && ed.MediaOf(x) is { } xm ? "  ·  " + xm.Name : "")).ToList();
            if (m.Id != mcSeq || mcView.Cells.Count != n) mcView.Build(names); else mcView.SetNames(names);
            mcSeq = m.Id; mcGen = Editor.Gen;
        }
        bool live = playing && mcRec.Count > 0;
        mcView.Mark(live ? mcLive : active, live);
        if (tau != mcTau) { mcTau = tau; mcMoved = Environment.TickCount64; }
        bool play = playing && playRate == 1, rested = Environment.TickCount64 - mcMoved > 60;
        for (int a = 0; a < n; a++)
        {
            var cell = mcView.Cells[a];
            if (Editor.CamClipAt(m, a, tau) is not { } x || ed.Media.FirstOrDefault(mm => mm.Id == x.MediaId) is not { Offline: false } media) { cell.Blank(); continue; }
            McFeed(cell, media, x.In + (tau - x.Start), play, rested);
        }
    }

    /// <summary>A camera's decode size: its picture in a box that shrinks with the number of cameras.</summary>
    static void McSize(MediaItem m, int n, out int w, out int h)
    {
        double bw = n <= 4 ? 640 : n <= 9 ? 480 : 320, bh = bw * 9 / 16;
        double f = Math.Min(Math.Min(bw / Math.Max(1, m.Width), bh / Math.Max(1, m.Height)), 1);
        w = Math.Max(2, (int)Math.Round(m.Width * f / 2) * 2); h = Math.Max(2, (int)Math.Round(m.Height * f / 2) * 2);
    }

    void McFeed(MultiCamView.Cell c, MediaItem m, double src, bool play, bool rested)
    {
        if (Exporter.FindFfmpeg() is not { } ff) return;
        string path = Proxies.PathOf(m);
        McSize(m, mcView!.Cells.Count, out int w, out int h);
        if (play)
        {   // a decoder per camera on the sequence's frame grid, started a little ahead (like a playback restart)
            if (c.One != null) { c.One.Dispose(); c.One = null; }
            if (c.Fs == null || c.Fs.Path != path || c.Fs.W != w || c.Fs.H != h || !c.Fs.CanServe(src))
            {
                bool first = c.Fs == null;
                c.Fs?.Dispose();
                c.Fs = new FrameStream(ff, path, src, w, h, 8, 0, first ? 0 : 0.35, path == m.Path ? m.PlayDecoder : Decoder.SwFrame);
            }
            var (idx, buf) = c.Fs.Take(src);
            if (buf != null && idx != c.ShownIdx) { c.Show(buf, w, h); c.ShownIdx = idx; c.ShownKey = null; }
            return;
        }
        if (c.Fs != null) { c.Fs.Dispose(); c.Fs = null; c.ShownIdx = -1; }
        string key = path + "|" + Math.Round(src * TimeUtil.Fps);
        if (key == c.ShownKey) return;
        if (ScrubCache.Has(m.Path, src) && ScrubCache.Show(m.Path, src, ref c.Scrub) >= 0) { c.ShowScrub(); c.ShownKey = key; return; }
        if (!rested) return;                                                 // (no cached frame yet: decode it once the time stops moving)
        if (c.One == null || c.One.Path != path || Math.Abs(c.OneSrc - src) > 1e-6) { c.One?.Dispose(); c.One = new FrameStream(ff, path, src, w, h, 1, 0, 0, path == m.Path ? m.SeekDecoder : Decoder.SwFrame); c.OneSrc = src; }
        var (_, b) = c.One.Take(src);
        if (b != null) { c.Show(b, w, h); c.ShownKey = key; c.One.Dispose(); c.One = null; c.OneSrc = double.NaN; }
        else if (c.One.Ended) { c.One.Dispose(); c.One = null; c.ShownKey = key; }   // (past the picture's end: keep what shows)
    }

    /// <summary>A camera chosen (a click on it or its number key): recorded while playing, else the clip at the playhead switches (Ctrl: from the playhead on).</summary>
    void McPick(int a, bool ctrl)
    {
        var c = ed.MultiCamClipAt(ed.Time);
        if (c == null || ed.SeqById(c.Cam!.Seq) is not { } m)
        {
            SetTask(ed.Seq.MultiCam != null ? "This is the multi-camera source itself — drag it onto a timeline to switch cameras there" : "No multi-camera clip at the playhead", 2500);
            return;
        }
        if (a < 0 || a >= Editor.CamCount(m)) return;
        if (playing && playRate == 1)
        {
            if (mcRecTrack < 0) mcRecTrack = c.Track;
            mcRec.Add((ed.Time, a)); mcLive = a;
            SetTask($"Recording: {Editor.CamName(m, a)} — the switches become cuts when playback stops", 0);
            return;
        }
        int n = ctrl ? ed.CutToCamera(c, ed.Time, a) : ed.SwitchCamera(new[] { c }, a);
        SetTask(n > 0 ? $"{Editor.CamName(m, a)}{(ctrl ? " from the playhead" : "")} (Ctrl+Z undoes)" : $"{Editor.CamName(m, a)} has no picture there (or it is showing already)", 2500);
    }

    /// <summary>Playback stopped: the recorded switches become cuts (one undo step).</summary>
    void McStop()
    {
        mcLive = -1;
        if (mcRec.Count == 0) { mcRecTrack = -1; return; }
        var list = mcRec.ToList(); int track = mcRecTrack;
        mcRec.Clear(); mcRecTrack = -1;
        int n = ed.ApplyCamCuts(track, list);
        SetTask(n > 0 ? $"{n} camera switch{(n == 1 ? "" : "es")} cut in (Ctrl+Z undoes)" : "No camera switches to cut", 4000);
    }

    void MultiCamBtn_Click(object s, RoutedEventArgs e) => SetMultiCamView(MultiCamBtn.IsChecked == true);
    /// <summary>The transport's Multi-Cam button shows once the project has a multi-camera source.</summary>
    void McButtonState() => MultiCamBtn.Visibility = mcOn || ed.Sequences.Any(s => s.MultiCam != null) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Clip ▸ Multi-Camera (and the clip's right-click menu): Camera n for the selected multicam clips, Flatten, the view.</summary>
    MenuItem MultiCamMenu()
    {
        var root = new MenuItem { Header = "Multi-Camera" };
        var sel = SelectedClips(c => c.Cam != null && c.Kind != ClipKind.Audio);
        var m = sel.Select(c => ed.SeqById(c.Cam!.Seq)).FirstOrDefault(s => s?.MultiCam != null);
        if (m != null)
            for (int a = 0; a < Editor.CamCount(m); a++)
            {
                int aa = a; bool on = sel.All(c => c.Cam!.Angle == aa);
                var mi = new MenuItem { Header = $"Camera {a + 1}: {Editor.CamName(m, a)}", InputGestureText = K($"mc.cam{a + 1}"), IsCheckable = true, IsChecked = on };
                mi.Click += (_, _) => { int n = ed.SwitchCamera(sel, aa); SetTask(n > 0 ? $"{Editor.CamName(m, aa)} (Ctrl+Z undoes)" : $"{Editor.CamName(m, aa)} has no picture there", 2500); };
                root.Items.Add(mi);
            }
        else root.Items.Add(new MenuItem { Header = "(select multi-camera clips)", IsEnabled = false });
        root.Items.Add(new Separator());
        var fl = new MenuItem { Header = "Flatten", IsEnabled = sel.Count > 0, ToolTip = "The clips stay as they are and forget their multi-camera source" };
        fl.Click += (_, _) => SetTask($"Flattened {ed.FlattenMultiCam(sel)} clips", 2500);
        root.Items.Add(fl);
        var view = new MenuItem { Header = "Multi-Camera View", InputGestureText = K("view.multicam"), IsCheckable = true, IsChecked = mcOn };
        view.Click += (_, _) => ToggleMultiCamView();
        root.Items.Add(view);
        var make = new MenuItem { Header = "Create Multi-Camera Source Sequence…", IsEnabled = SelectedBinMedia().Count > 1 };
        make.Click += (_, _) => _ = CreateMultiCamDialog();
        root.Items.Add(make);
        return root;
    }

    /// <summary>Project panel ▸ Create Multi-Camera Source Sequence…: the selected clips lined up into a source sequence (cameras = video clips,
    /// sound-only clips = an external recorder), optionally put on the timeline at the playhead.</summary>
    async Task CreateMultiCamDialog()
    {
        var items = SelectedBinMedia().Where(m => !m.Offline && m.Gen == null && m.Type is ClipKind.Video or ClipKind.Audio).ToList();
        var cams = items.Where(m => m.Type == ClipKind.Video).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var snds = items.Where(m => m.Type == ClipKind.Audio).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (cams.Count < 1 || cams.Count + snds.Count < 2)
        {
            Alert.Show(this, "Select the cameras' clips in the Project panel (two or more, and any sound recorded apart) first.", "Multi-Camera Source Sequence");
            return;
        }
        var panel = new StackPanel();
        TextBlock L(string s) { var t = new TextBlock { Text = s, Margin = new Thickness(0, 10, 0, 2) }; return t; }
        var name = new TextBox { Text = ed.FreeName("Multicam"), MinWidth = 300 };
        panel.Children.Add(L("Name")); panel.Children.Add(name);
        bool sound = items.Count(m => m.HasAudio) >= 2, tc = items.All(m => MultiCamSync.TimecodeSeconds(m) != null), ins = items.All(m => m.SrcIn != null);
        var sync = new ComboBox { ItemsSource = new[] { "Sound", "Timecode", "In points", "Clip starts" }, SelectedIndex = sound ? 0 : tc ? 1 : ins ? 2 : 3 };
        panel.Children.Add(L("Synchronize by")); panel.Children.Add(sync);
        var choices = new List<(string label, int from)> { ("The camera shown (audio follows video)", -1) };
        for (int i = 0; i < cams.Count; i++) if (cams[i].HasAudio) choices.Add(($"Camera {i + 1}: {cams[i].Name}", i));
        for (int j = 0; j < snds.Count; j++) choices.Add(($"{snds[j].Name} (recorded apart)", cams.Count + j));
        var audio = new ComboBox { ItemsSource = choices.Select(c => c.label).ToList(), SelectedIndex = snds.Count > 0 ? choices.FindIndex(c => c.from == cams.Count) : Math.Min(1, choices.Count - 1) };
        panel.Children.Add(L("Sound")); panel.Children.Add(audio);
        var place = new CheckBox { Content = "Put it on the timeline at the playhead", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(place);
        string list = string.Join("\n", cams.Select((m, i) => $"Camera {i + 1}: {m.Name}").Concat(snds.Select(m => $"Sound: {m.Name}")));
        if (Alert.Show(this, "Lines the clips up so the same moment shows on every camera, then switches between them like one clip.\n\n" + list,
                       "Multi-Camera Source Sequence", AlertKind.Question, new[] { "Create", "Cancel" }, panel) != 0) return;
        string seqName = string.IsNullOrWhiteSpace(name.Text) ? ed.FreeName("Multicam") : name.Text.Trim();
        int how = sync.SelectedIndex, audioFrom = choices[Math.Max(0, audio.SelectedIndex)].from;
        bool put = place.IsChecked == true;
        var all = cams.Concat(snds).ToList();
        await RunTool("Multi-Camera", async ct =>
        {
            var k = new double[all.Count]; var bad = new List<string>();
            switch (how)
            {
                case 0:
                    var refm = all.FirstOrDefault(m => m.HasAudio);
                    for (int i = 0; i < all.Count && refm != null; i++)
                    {
                        if (all[i] == refm) continue;
                        if (!all[i].HasAudio) { bad.Add(all[i].Name); continue; }
                        SetTask($"Multi-Camera: listening to {all[i].Name}…", 0);
                        var other = all[i];
                        var r = await Task.Run(() => AudioSync.Between(refm, other), ct);
                        if (r == null || r.Confidence < 8) bad.Add(other.Name); else k[i] = -r.Lag;
                    }
                    break;
                case 1:
                    for (int i = 0; i < all.Count; i++) if (MultiCamSync.TimecodeSeconds(all[i]) is double s) k[i] = s; else bad.Add(all[i].Name);
                    break;
                case 2:
                    for (int i = 0; i < all.Count; i++) k[i] = -(all[i].SrcIn ?? 0);
                    break;
            }
            var S = ed.CreateMultiCam(seqName, cams.Select((m, i) => (m, k[i])).ToList(), snds.Select((m, j) => (m, k[cams.Count + j])).ToList(), audioFrom,
                                      new[] { "Sound", "Timecode", "In points", "Starts" }[how]);
            RefreshBin(); McButtonState();
            if (put && ed.SeqItem(S.Id) is { } item) ed.AddMedia(item, null, ed.Time);
            SetTask($"Multi-camera source “{S.Name}”{(put ? " is on the timeline" : " is in the Project panel — drag it onto a timeline")}; Shift+0 shows its cameras" +
                    (bad.Count > 0 ? $" — not lined up: {string.Join(", ", bad)}" : ""), 7000);
            if (put) SetMultiCamView(true);
        });
    }
}
