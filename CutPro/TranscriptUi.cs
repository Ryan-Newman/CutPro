using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CutPro;

/// <summary>The Transcript panel (text-based editing, Premiere's Text panel): the sequence's words as text; click a word = the playhead goes
/// there, drag / Shift+click selects, Delete removes the selected words' time from the sequence (ripple, one undo step); fillers and long
/// pauses can be picked or removed in one go; search finds words. Transcribing is per media file, on this PC (Whisper).</summary>
public partial class MainWindow
{
    DockPanel TranscriptView = null!;
    WrapPanel trWrap = null!;
    ScrollViewer trScroll = null!;
    TextBlock trStatus = null!;
    TextBox trFind = null!;
    ComboBox trLang = null!;
    Button trGo = null!;
    List<Transcript.TWord> trWords = new();
    readonly List<TextBlock> trBlocks = new();
    readonly HashSet<int> trSel = new();
    int trAnchor = -1, trNow = -1;
    bool trStale = true, trDragging;
    string trSig = "";
    CancellationTokenSource? trCts;
    readonly DispatcherTimer trTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    void InitTranscript() { }

    void BuildTranscriptView()
    {
        TranscriptView = new DockPanel { Background = Brushes.Transparent, Visibility = Visibility.Collapsed };
        var head = new Grid { Height = 35 };
        var title = new TextBlock { Text = "TRANSCRIPT" }; title.SetResourceReference(StyleProperty, "PaneHeader");
        head.Children.Add(title);
        DockPanel.SetDock(head, Dock.Top); TranscriptView.Children.Add(head);

        var tools = new StackPanel { Margin = new Thickness(14, 0, 10, 6) };
        DockPanel.SetDock(tools, Dock.Top);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        trLang = new ComboBox { ItemsSource = Whisper.Languages.Select(l => l.name).ToList(), SelectedIndex = 0, ToolTip = "The spoken language (Detect = from the first words)" };
        trGo = new Button { Content = "Transcribe", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), ToolTip = "Transcribe the sequence's clips that aren't yet (on this PC)" };
        trGo.SetResourceReference(StyleProperty, "Primary");
        trGo.Click += (_, _) => { if (trCts != null) trCts.Cancel(); else _ = TranscribeSequenceAsync(); };
        Grid.SetColumn(trGo, 1);
        row.Children.Add(trLang); row.Children.Add(trGo);
        tools.Children.Add(row);
        var row2 = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        Button Tool(string text, string tip, Action a)
        {
            var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 2, 8, 2) };
            b.Click += (_, _) => a();
            row2.Children.Add(b);
            return b;
        }
        Tool("Delete", "Remove the selected words from the sequence (Delete) — the rest closes up", DeleteTranscriptSelection);
        Tool("Select fillers", "Select every “um”, “uh”, “erm” …", () => { trSel.Clear(); for (int i = 0; i < trWords.Count; i++) if (Transcript.IsFiller(trWords[i].W)) trSel.Add(i); PaintTranscript(); trStatus.Text = $"{trSel.Count} filler words selected — Delete removes them"; });
        Tool("Shorten pauses…", "Shorten every pause between words longer than a second", ShortenPauses);
        tools.Children.Add(row2);
        var fg = new Grid();
        trFind = new TextBox { ToolTip = "Find a word or phrase (Enter: the next one)" };
        AutomationProperties.SetName(trFind, "Find in the transcript");
        var hint = new TextBlock { Text = "Search", IsHitTestVisible = false, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        trFind.TextChanged += (_, _) => { hint.Visibility = trFind.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; FindInTranscript(false); };
        trFind.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FindInTranscript(true); e.Handled = true; } };
        fg.Children.Add(trFind); fg.Children.Add(hint);
        tools.Children.Add(fg);
        trStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        trStatus.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        tools.Children.Add(trStatus);
        TranscriptView.Children.Add(tools);

        trWrap = new WrapPanel { Margin = new Thickness(12, 0, 10, 12), Background = Brushes.Transparent, Focusable = true };
        AutomationProperties.SetName(trWrap, "Transcript words");
        trScroll = new ScrollViewer { Content = trWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        trScroll.SizeChanged += (_, _) => FitBreaks();
        TranscriptView.Children.Add(trScroll);
        trWrap.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Delete or Key.Back && trSel.Count > 0) { DeleteTranscriptSelection(); e.Handled = true; }
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { trSel.Clear(); for (int i = 0; i < trWords.Count; i++) trSel.Add(i); PaintTranscript(); e.Handled = true; }
            else if (e.Key == Key.Escape) { trSel.Clear(); PaintTranscript(); e.Handled = true; }
        };
        trWrap.MouseLeftButtonUp += (_, _) => trDragging = false;
        LeftHost.Children.Add(TranscriptView);
        TranscriptView.IsVisibleChanged += (_, _) => { if (TranscriptView.IsVisible) { Dispatcher.BeginInvoke(RefreshTranscript, DispatcherPriority.Loaded); trTimer.Start(); } else trTimer.Stop(); };
        TranscriptView.SizeChanged += (_, _) => { if (trStale) Dispatcher.BeginInvoke(RefreshTranscript, DispatcherPriority.Background); };
        ed.Changed += () => { trStale = true; if (Shows(TranscriptView)) Dispatcher.BeginInvoke(RefreshTranscript, DispatcherPriority.Background); };
        trTimer.Tick += (_, _) => FollowTranscript();
    }

    /// <summary>Rebuilds the words when the sequence's transcript changed (only while the panel shows).</summary>
    void RefreshTranscript()
    {
        if (!trStale || !Shows(TranscriptView)) return;
        trStale = false;
        var words = Transcript.Of(ed);
        var sig = string.Join("|", words.Select(w => $"{w.T0:0.###}{w.W}"));
        int pending = PendingTranscription().Count;
        trGo.IsEnabled = pending > 0 || trCts != null;
        if (sig == trSig) { UpdateTranscriptStatus(pending); return; }
        trSig = sig; trWords = words; trSel.RemoveWhere(i => i >= words.Count); trNow = -1;
        trWrap.Children.Clear(); trBlocks.Clear();
        for (int i = 0; i < words.Count; i++)
        {
            if (i > 0 && words[i].T0 - words[i - 1].T1 > 1.5) trWrap.Children.Add(new Border { Height = 8, Tag = "break" });   // (a paragraph at a long pause)
            int k = i;
            var tb = new TextBlock { Text = words[i].W, Padding = new Thickness(2, 1, 2, 1), Margin = new Thickness(0, 0, 1, 2), Background = Brushes.Transparent, Cursor = Cursors.IBeam, Tag = i,
                                     ToolTip = $"{TimeUtil.Format(words[i].T0)}  –  {TimeUtil.Format(words[i].T1)}" };
            tb.MouseLeftButtonDown += (_, e) => { TranscriptClick(k, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); trDragging = true; trWrap.Focus(); e.Handled = true; };
            tb.MouseEnter += (_, e) => { if (trDragging && e.LeftButton == MouseButtonState.Pressed) TranscriptClick(k, true, false); };
            trBlocks.Add(tb); trWrap.Children.Add(tb);
        }
        FitBreaks();
        PaintTranscript();
        UpdateTranscriptStatus(pending);
    }
    void UpdateTranscriptStatus(int pending)
    {
        if (trCts != null) return;
        trStatus.Text = trWords.Count == 0
            ? (pending > 0 ? $"{pending} clip file{(pending == 1 ? "" : "s")} with sound to transcribe — then edit the video by editing its text." : "No sound clips in this sequence.")
            : $"{trWords.Count} words" + (pending > 0 ? $" · {pending} file{(pending == 1 ? "" : "s")} not transcribed yet" : "") + " · click a word to go there, select and Delete to cut it out";
    }
    void FitBreaks() { foreach (var b in trWrap.Children.OfType<Border>()) b.Width = Math.Max(10, trScroll.ViewportWidth - 30); }

    void TranscriptClick(int i, bool extend, bool toggle)
    {
        if (extend && trAnchor >= 0)
        {
            trSel.Clear();
            for (int k = Math.Min(trAnchor, i); k <= Math.Max(trAnchor, i); k++) trSel.Add(k);
        }
        else if (toggle) { if (!trSel.Remove(i)) trSel.Add(i); trAnchor = i; }
        else { trSel.Clear(); trSel.Add(i); trAnchor = i; Pause(); ed.Seek(trWords[i].T0); }
        PaintTranscript();
    }

    void PaintTranscript()
    {
        var sel = ThemeManager.Brush(TranscriptView, "Selected");
        var now = ThemeManager.Brush(TranscriptView, "Accent");
        for (int i = 0; i < trBlocks.Count; i++)
        {
            var tb = trBlocks[i];
            tb.Background = trSel.Contains(i) ? sel : Brushes.Transparent;
            tb.TextDecorations = i == trNow ? TextDecorations.Underline : null;
            if (i == trNow) tb.SetValue(TextBlock.ForegroundProperty, now); else tb.ClearValue(TextBlock.ForegroundProperty);
            tb.Opacity = Transcript.IsFiller(trWords[i].W) ? 0.65 : 1;
        }
    }

    /// <summary>The word under the playhead is underlined (and kept in view while playing).</summary>
    void FollowTranscript()
    {
        if (trWords.Count == 0) return;
        double t = ed.Time;
        int i = trWords.FindIndex(w => t >= w.T0 && t < w.T1);
        if (i < 0 || i == trNow) return;
        trNow = i;
        PaintTranscript();
        if (playing) trBlocks[i].BringIntoView();
    }

    void DeleteTranscriptSelection()
    {
        if (trSel.Count == 0) { trStatus.Text = "Select words first (click, Shift+click or drag)."; return; }
        var ranges = Transcript.Ranges(trWords, trSel);
        int n = trSel.Count;
        trSel.Clear();
        Pause();
        ed.RemoveRanges(ranges, n == 1 ? "Delete Word" : "Delete Words");
        SetTask($"Removed {n} word{(n == 1 ? "" : "s")} ({ranges.Sum(r => r.B - r.A):0.0} s) — Ctrl+Z brings {(n == 1 ? "it" : "them")} back", 4000);
    }

    void ShortenPauses()
    {
        var box = new ComboBox { ItemsSource = new[] { "Longer than 0.5 s → 0.25 s", "Longer than 1 s → 0.4 s", "Longer than 2 s → 0.5 s" }, SelectedIndex = 1, Margin = new Thickness(0, 10, 0, 0) };
        if (Alert.Show(this, "Pauses between words get shorter; everything after them closes up (all unlocked tracks).", "Shorten pauses", AlertKind.Question, new[] { "Shorten", "Cancel" }, box) != 0) return;
        var (longer, keep) = box.SelectedIndex switch { 0 => (0.5, 0.25), 2 => (2.0, 0.5), _ => (1.0, 0.4) };
        var ranges = Transcript.Pauses(trWords, longer, keep);
        if (ranges.Count == 0) { trStatus.Text = "No pauses that long."; return; }
        Pause();
        ed.RemoveRanges(ranges, "Shorten Pauses");
        SetTask($"Shortened {ranges.Count} pauses ({ranges.Sum(r => r.B - r.A):0.0} s)", 4000);
    }

    void FindInTranscript(bool next)
    {
        var q = trFind.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (q.Length == 0 || trWords.Count == 0) return;
        static string N(string s) => s.Trim(',', '.', '!', '?', '…', '"', '\'').ToLowerInvariant();
        bool At(int i) => i + q.Length <= trWords.Count && q.Select((w, k) => N(trWords[i + k].W).StartsWith(N(w), StringComparison.Ordinal)).All(x => x);
        int from = next && trAnchor >= 0 ? trAnchor + 1 : 0;
        for (int s = 0; s < trWords.Count; s++)
        {
            int i = (from + s) % trWords.Count;
            if (!At(i)) continue;
            trSel.Clear(); for (int k = 0; k < q.Length; k++) trSel.Add(i + k);
            trAnchor = i; PaintTranscript(); trBlocks[i].BringIntoView();
            if (next) { Pause(); ed.Seek(trWords[i].T0); }
            return;
        }
        trStatus.Text = $"“{trFind.Text.Trim()}” isn't in the transcript.";
    }

    /// <summary>Media with sound used in this sequence that has no transcript yet.</summary>
    List<MediaItem> PendingTranscription() => ed.Clips.Where(Mixer.HasSound).Select(ed.MediaOf).OfType<MediaItem>()
        .Where(m => m.Seq == null && m.HasAudio && !m.Offline && m.Words == null).Distinct().ToList();

    async Task TranscribeSequenceAsync() => await TranscribeAsync(PendingTranscription());

    /// <summary>Transcribes media files one after another (progress in the panel and the status bar; Cancel stops).</summary>
    async Task TranscribeAsync(List<MediaItem> items)
    {
        if (items.Count == 0 || trCts != null) return;
        if (!Whisper.Present && !await EnsureWhisperAsync()) return;
        var ff = Exporter.FindFfmpeg();
        if (ff == null) return;
        string? lang = Whisper.Languages[Math.Max(0, trLang.SelectedIndex)].code;
        trCts = new CancellationTokenSource(); var ct = trCts.Token;
        trGo.Content = "Cancel";
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var m = items[i];
                int k = i;
                var prog = new Progress<double>(p =>                                  // (made on the UI thread: it reports there)
                {
                    trStatus.Text = $"Transcribing {m.Name} ({k + 1} of {items.Count})… {p * 100:0}%";
                    SetTask($"Transcribing… {(k + p) / items.Count * 100:0}%", 0);
                });
                var words = await Task.Run(() => Transcript.Transcribe(ff, m, lang, prog, ct), ct);
                m.Words = words;
                trStale = true; RefreshTranscript();
            }
            SetTask($"Transcribed {items.Count} file{(items.Count == 1 ? "" : "s")}", 3000);
        }
        catch (OperationCanceledException) { SetTask("Transcription cancelled", 1500); }
        catch (Exception ex) { Alert.Show(this, ex.Message, "Transcript", AlertKind.Error); }
        finally
        {
            trCts.Dispose(); trCts = null; trGo.Content = "Transcribe";
            trStale = true; RefreshTranscript(); binRef = null; OnChanged();
        }
    }
}
