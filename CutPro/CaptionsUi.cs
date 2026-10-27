using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;

namespace CutPro;

/// <summary>The Captions side view (partial MainWindow): captions from the sequence's speech (Whisper on this PC, <see cref="Captioning"/>),
/// the list of captions to read and edit (click a time to go there; the caption under the playhead is highlighted), restyle, SRT / WebVTT
/// import and export. Captions are caption-role titles on the timeline, so they can also be moved, trimmed and styled like any title.</summary>
public partial class MainWindow
{
    /// <summary>One caption in the list; editing the text edits the clip (one undo step after typing pauses).</summary>
    public sealed class CaptionRow : INotifyPropertyChanged
    {
        readonly MainWindow w; public readonly Clip C;
        public CaptionRow(MainWindow w, Clip c) { this.w = w; C = c; }
        public string Time => TimeUtil.Format(C.Start);
        public string Text
        {
            get => C.Text.Replace("\r", "");
            set { if (value == C.Text) return; C.Text = value; w.ed.Touch(); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    ComboBox? capLang, capLayout;
    Button? capGo;
    StackPanel? capBusy; ProgressBar? capBar; TextBlock? capStatus, capInfo;
    CancellationTokenSource? capCts;
    string capSig = "";
    readonly List<CaptionRow> capRows = new();

    void InitCaptions()
    {
        TextBlock Dim(string t, double size = 11.5) { var b = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = new Thickness(0, 0, 0, 4) }; b.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim"); return b; }
        CaptionsTop.Children.Add(Dim("Turns what is said in this sequence into captions — on this PC, nothing is uploaded. Edit them below or on the timeline (they are titles)."));
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
        capLang = new ComboBox { ItemsSource = Whisper.Languages.Select(l => l.name).ToList(), SelectedIndex = 0, Margin = new Thickness(0, 2, 0, 2), ToolTip = "The spoken language (Detect = from the first words)" };
        capLayout = new ComboBox { ItemsSource = Captioning.Layouts.Select(l => l.Name).ToList(), SelectedIndex = Editor.W < Editor.H ? 2 : 0, Margin = new Thickness(0, 2, 0, 2) };
        void Cell(UIElement e, int r, int c) { Grid.SetRow(e, r); Grid.SetColumn(e, c); grid.Children.Add(e); }
        var l1 = Dim("Language"); l1.VerticalAlignment = VerticalAlignment.Center; var l2 = Dim("Style"); l2.VerticalAlignment = VerticalAlignment.Center;
        Cell(l1, 0, 0); Cell(capLang, 0, 1); Cell(l2, 1, 0); Cell(capLayout, 1, 1);
        CaptionsTop.Children.Add(grid);
        capGo = new Button { Content = "Create captions from speech", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        capGo.SetResourceReference(StyleProperty, "Primary");
        capGo.Click += async (_, _) => await CreateCaptionsAsync();
        CaptionsTop.Children.Add(capGo);
        capBusy = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        capBar = new ProgressBar { Height = 4, Maximum = 1 };
        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        cancel.Click += (_, _) => capCts?.Cancel();
        capStatus = Dim(""); capStatus.Margin = new Thickness(0, 4, 0, 0);
        capBusy.Children.Add(capBar); capBusy.Children.Add(capStatus); capBusy.Children.Add(cancel);
        CaptionsTop.Children.Add(capBusy);
        capInfo = Dim(""); capInfo.Margin = new Thickness(0, 10, 0, 0);
        CaptionsTop.Children.Add(capInfo);

        var row1 = new UniformGrid { Columns = 2 }; var row2 = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };
        Button B(string t, string tip, Action a, Panel into) { var b = new Button { Content = t, ToolTip = tip, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(6, 3, 6, 3) }; b.Click += (_, _) => a(); into.Children.Add(b); return b; }
        B("Export .srt…", "Save the captions as SubRip (YouTube, Facebook, LinkedIn, most players)", () => SaveCaptions("srt"), row1);
        B("Export .vtt…", "Save the captions as WebVTT (web players)", () => SaveCaptions("vtt"), row1);
        B("Import…", "Captions from an .srt or .vtt file, onto the timeline", ImportCaptions, row2);
        B("Restyle all", "Give every caption the chosen style's look (the title theme's caption style)", RestyleCaptions, row2);
        var row3 = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };
        B("Select all", "Select every caption (then click a style in Titles to restyle them)", () => ed.SetSelection(Captioning.Clips(ed)), row3);
        B("Delete all", "Remove every caption (Ctrl+Z brings them back)", () => { if (Captioning.Clips(ed).Count > 0) ed.Edit(() => ed.Clips.RemoveAll(Captioning.IsCaption)); }, row3);
        CaptionsBottom.Children.Add(row1); CaptionsBottom.Children.Add(row2); CaptionsBottom.Children.Add(row3);
        CaptionList.ItemsSource = capRows;
        ed.Changed += () => { if (CaptionsView.Visibility == Visibility.Visible) RefreshCaptions(false); };
    }

    /// <summary>The list follows the timeline's captions (rebuilt only when they are added, removed or moved — not while typing).</summary>
    void RefreshCaptions(bool force)
    {
        if (capInfo == null) return;
        var caps = Captioning.Clips(ed);
        var sig = string.Join("|", caps.Select(c => c.Id + "@" + c.Start.ToString("R")));
        if (!force && sig == capSig) { foreach (var r in capRows) if (!(Keyboard.FocusedElement is TextBox { Tag: CaptionRow fr } && fr == r)) r.Changed(); return; }
        capSig = sig;
        capRows.Clear(); capRows.AddRange(caps.Select(c => new CaptionRow(this, c)));
        CaptionList.ItemsSource = null; CaptionList.ItemsSource = capRows;
        capInfo.Text = caps.Count == 0 ? "No captions yet." : $"{caps.Count} caption{(caps.Count == 1 ? "" : "s")} · {TimeUtil.Format(caps[0].Start, false)} – {TimeUtil.Format(caps[^1].End, false)}";
    }

    /// <summary>Render loop: highlight the caption under the playhead (unless one is being edited).</summary>
    void UpdateCaptionHighlight()
    {
        if (CaptionsView.Visibility != Visibility.Visible || capRows.Count == 0 || Keyboard.FocusedElement is TextBox { Tag: CaptionRow }) return;
        var row = capRows.FirstOrDefault(r => ed.Time >= r.C.Start && ed.Time < r.C.End);
        if (row == null || ReferenceEquals(CaptionList.SelectedItem, row)) return;
        CaptionList.SelectedItem = row; CaptionList.ScrollIntoView(row);
    }

    void CaptionTime_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is CaptionRow r) { Pause(); ed.Seek(r.C.Start); ed.Select(r.C); } }
    void CaptionText_Focus(object sender, KeyboardFocusChangedEventArgs e) { if ((sender as FrameworkElement)?.Tag is CaptionRow r) { CaptionList.SelectedItem = r; if (!playing) ed.Seek(r.C.Start); } }

    /// <summary>Mix the sequence's sound, listen (Whisper with timestamps), cut it into captions and put them on a free top video track.</summary>
    async Task CreateCaptionsAsync()
    {
        if (capCts != null) return;
        if (!ed.Clips.Any(c => Mixer.HasSound(c) && !ed.TrackOff[c.Track])) { Alert.Show(this, "This sequence has no sound to caption.", "Captions"); return; }
        if (!Whisper.Present && !await EnsureWhisperAsync()) return;
        var ff = Exporter.FindFfmpeg();
        if (ff == null) return;
        var lay = Captioning.Layouts[Math.Max(0, capLayout!.SelectedIndex)];
        string lang = Whisper.Languages[Math.Max(0, capLang!.SelectedIndex)].code;
        Pause();
        capCts = new CancellationTokenSource(); var ct = capCts.Token;
        capGo!.IsEnabled = false; capBusy!.Visibility = Visibility.Visible; capBar!.Value = 0; capStatus!.Text = "Mixing the sound…";
        SetTask("Captions: mixing the sound…", 0);
        var wav = Path.Combine(Path.GetTempPath(), $"cutpro_captions_{Guid.NewGuid().ToString("N")[..8]}.wav");
        try
        {
            var err = await Task.Run(() => Exporter.RunAsync(ff, ed, wav, new Progress<double>(p => capBar.Value = p * 0.1), ct, null, null,
                                                              new ExportSettings { Video = "none", Container = "wav", Audio = "pcm" }));
            if (err != null) { if (err != "Cancelled") Alert.Show(this, err, "Could not mix the sound", AlertKind.Error); return; }
            var pcm = await Task.Run(() => Whisper.Pcm16k(ff, wav), ct);
            capStatus.Text = "Loading the speech model…";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = await Task.Run(() =>
            {
                using var w = new Whisper();
                return w.TranscribeTimed(pcm, lang, new Progress<double>(p =>
                {
                    capBar.Value = 0.1 + p * 0.9;
                    capStatus.Text = $"Listening… {p * 100:0}%"; SetTask($"Captions: listening… {p * 100:0}%", 0);
                }), ct);
            }, ct);
            var (style, y) = Captioning.Look(ed, lay);
            var cues = Captioning.Build(res.Segments, lay, Captioning.Measure(style), Editor.W * 0.88, TimeUtil.Fps, new Captioning.Voice(pcm));
            if (cues.Count == 0) { Alert.Show(this, "No speech was found in this sequence's sound.", "Captions"); return; }
            int track = CaptionTrack(cues, "Captions");
            if (track < 0) return;
            if (ed.Clips.Any(c => Captioning.IsCaption(c) && c.Track == track) &&
                !Alert.Confirm(this, $"Replace the captions on {Tracks.Name(track)}?", "Captions", "Replace", "Cancel")) return;
            int n = Captioning.Place(ed, cues, track, style, y);
            RefreshCaptions(true);
            string lname = Whisper.Languages.FirstOrDefault(l => l.code == res.Language).name ?? res.Language;
            SetTask($"{n} captions ({lname}) in {sw.Elapsed.TotalSeconds:0} s", 4000);
        }
        catch (OperationCanceledException) { SetTask("Captions cancelled", 1500); }
        catch (Exception ex) { Alert.Show(this, ex.Message, "Captions", AlertKind.Error); }
        finally
        {
            try { File.Delete(wav); } catch { }
            capCts.Dispose(); capCts = null;
            capGo.IsEnabled = true; capBusy.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>First use: offer the speech model alone (Whisper base, ~77 MB, MIT).</summary>
    async Task<bool> EnsureWhisperAsync()
    {
        if (Alert.Show(this, "Captions are made on this PC by Whisper base (speech to text, about 77 MB, MIT licence) from huggingface.co — downloaded once, nothing is uploaded.",
                       "Download the speech model?", AlertKind.Question, "Download", "Cancel") != 0) return false;
        var cts = new CancellationTokenSource();
        var dlg = new Alert.ProgressDialog(this, "Downloading the speech model", "Starting…", () => cts.Cancel());
        try
        {
            await ModelStore.DownloadAsync(Whisper.Files, new Progress<double>(p => { dlg.Value = p; dlg.Text = $"Downloading… {p * 100:0}%"; }), cts.Token);
            return Whisper.Present;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Alert.Show(this, ex.Message, "Download failed", AlertKind.Error); return false; }
        finally { dlg.Close(); }
    }

    void SaveCaptions(string kind)
    {
        var cues = Captioning.CuesOf(ed);
        if (cues.Count == 0) { Alert.Show(this, "There are no captions to save yet.", "Captions"); return; }
        var name = (projectPath != null ? Path.GetFileNameWithoutExtension(projectPath) : "captions") + (ed.Sequences.Count > 1 ? " - " + string.Concat(ed.Seq.Name.Split(Path.GetInvalidFileNameChars())) : "");
        var dlg = new SaveFileDialog { Filter = kind == "vtt" ? "WebVTT|*.vtt" : "SubRip|*.srt", DefaultExt = "." + kind, FileName = name };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, kind == "vtt" ? Captioning.ToVtt(cues) : Captioning.ToSrt(cues), new System.Text.UTF8Encoding(false));
        SetTask($"{cues.Count} captions saved", 2500);
    }

    /// <summary>The track the captions go on: the highest free one above V1, else — asked — a new video track on top. −1: none.</summary>
    int CaptionTrack(IReadOnlyList<Cue> cues, string title)
    {
        int track = Captioning.FreeTrack(ed, cues);
        if (track >= 0) return track;
        if (Tracks.Video >= Tracks.MaxVideo) { Alert.Show(this, "Every video track above V1 has clips where the captions would go, and the sequence has the most video tracks it can have. Free one of them, then try again.", title, AlertKind.Warning); return -1; }
        if (!Alert.Confirm(this, "Every video track above V1 has clips (or is locked) where the captions would go. Add a video track on top for the captions?", title, "Add track", "Cancel")) return -1;
        ed.AddTracks(1, 0, 0, Tracks.Count);
        return 0;
    }

    void ImportCaptions()
    {
        var dlg = new OpenFileDialog { Filter = "Captions|*.srt;*.vtt|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        var cues = Captioning.Tidy(Captioning.Parse(File.ReadAllText(dlg.FileName)), TimeUtil.Fps);
        if (cues.Count == 0) { Alert.Show(this, "No captions were found in that file.", "Import captions", AlertKind.Warning); return; }
        var lay = Captioning.Layouts[Math.Max(0, capLayout?.SelectedIndex ?? 0)];
        var (style, y) = Captioning.Look(ed, lay);
        int track = CaptionTrack(cues, "Import captions");
        if (track < 0) return;
        Captioning.Place(ed, cues, track, style, y);
        RefreshCaptions(true);
        SetTask($"{cues.Count} captions imported", 2500);
    }

    /// <summary>Every caption gets the chosen style's look (one undo step).</summary>
    void RestyleCaptions()
    {
        var caps = Captioning.Clips(ed);
        if (caps.Count == 0) return;
        var (style, y) = Captioning.Look(ed, Captioning.Layouts[Math.Max(0, capLayout?.SelectedIndex ?? 0)]);
        ed.Edit(() => { foreach (var c in caps) { c.Style = style.Clone(); c.Y = y; c.X = 0; } });
    }
}
