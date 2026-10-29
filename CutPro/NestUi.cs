using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>Sequences in the window (partial MainWindow): timeline tabs for the open sequences, sequences in the Project panel (double-click
/// opens, drag onto the timeline nests), Sequence menu commands (new, duplicate, rename, delete, nest), double-click a nested clip to open
/// its sequence at the same moment, the status bar while nested sequences render (<see cref="Nest"/>).</summary>
public partial class MainWindow
{
    /// <summary>Ids of the sequences open as timeline tabs (the active one always is).</summary>
    readonly List<string> openSeqs = new();

    void InitNest()
    {
        Editor.SeqIcon = SeqIconImage();
        ed.RefreshSeqItems();
        ed.Switched += OnSwitched;
        ed.SequencesChanged += () => { RefreshSeqTabs(); binRef = null; };
        Tl.NestOpenRequested += OpenNested;
        Tl.NestRequested += NestSelected;
        Tl.Notice += msg => Alert.Show(this, msg, "Timeline", AlertKind.Warning);
        Previews.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            Tl.InvalidateVisual();
            if (Previews.Status is { Length: > 0 } s) SetTask(s, s.StartsWith("Render previews ready") ? 1500 : 0);
        });
        Nest.StatusChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (Nest.Status is { } s) SetTask(s, 0);
            else if (Nest.Rendered) SetTask("Nested sequences rendered", 1500);
            else SetTask("", 1);                                                    // (dropped: nobody needed it any more)
        });
        BinList.MouseRightButtonUp += Bin_RightClick;
        RefreshSeqTabs();
    }

    /// <summary>Another sequence is open: playback stops, the tabs follow, the timeline shows its playhead.</summary>
    void OnSwitched()
    {
        Pause();
        if (!openSeqs.Contains(ed.Seq.Id)) openSeqs.Add(ed.Seq.Id);
        RefreshSeqTabs();
        double x = Tl.T2X(ed.Time);
        if (x < 92 || x > Tl.ActualWidth - 30) Tl.ScrollX = Math.Max(0, ed.Time * Tl.Pps - 120);   // (only when its playhead is out of view)
        binRef = null; lastDrawnT = -1;
        UpdateStatus();
    }

    // ---------------- tabs ----------------
    void RefreshSeqTabs()
    {
        openSeqs.RemoveAll(id => ed.SeqById(id) == null);
        if (!openSeqs.Contains(ed.Seq.Id)) openSeqs.Add(ed.Seq.Id);
        SeqTabs.Children.Clear();
        foreach (var id in openSeqs)
        {
            var s = ed.SeqById(id)!;
            bool active = s == ed.Seq;
            var f = ed.FormatOf(s);
            var name = new TextBlock { Text = s.Name, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, active ? "B.Fg" : "B.Fg.Dim");
            if (active) name.FontWeight = FontWeights.SemiBold;
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(name);
            if (openSeqs.Count > 1)
            {
                var close = new Button { Content = "", FontSize = 8, Width = 16, Height = 16, Padding = new Thickness(0), Margin = new Thickness(5, 0, 0, 0), ToolTip = "Close the tab (the sequence stays in the project)" };
                close.SetResourceReference(StyleProperty, "Icon");
                close.Click += (_, e) => { e.Handled = true; CloseSeqTab(s); };
                row.Children.Add(close);
            }
            var line = new Border { Height = 1.5, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4), Visibility = active ? Visibility.Visible : Visibility.Hidden };
            line.SetResourceReference(Border.BackgroundProperty, "B.Tab.Border");
            var grid = new Grid { Height = 35 }; grid.Children.Add(row); grid.Children.Add(line);
            var tab = new Button { Content = grid, Padding = new Thickness(6, 0, 4, 0), Tag = s.Id,
                                   ToolTip = $"{s.Name} — {f.OutW}×{f.OutH}, {TimeUtil.Label(f.Fps)} fps, {TimeUtil.Format(s.End, false)}\nRight-click: rename, duplicate, settings, delete" };
            tab.SetResourceReference(StyleProperty, "Flat");
            tab.Click += (_, _) => OpenSequence(s);
            tab.ContextMenu = SeqMenu(s);
            SeqTabs.Children.Add(tab);
        }
    }

    /// <summary>Open, insert into the open sequence, rename, duplicate, settings, close, delete.</summary>
    ContextMenu SeqMenu(Sequence s)
    {
        var m = new ContextMenu();
        void Add(string header, Action run, bool enabled = true, string keys = "") { var mi = new MenuItem { Header = header, InputGestureText = keys, IsEnabled = enabled }; mi.Click += (_, _) => run(); m.Items.Add(mi); }
        bool active = s == ed.Seq;
        if (!active) Add("Open", () => OpenSequence(s));
        if (!active) Add($"Insert into “{ed.Seq.Name}” at the Playhead", () => InsertSequence(s), ed.SeqItem(s.Id) is { } item && ed.CanAdd(item) && s.End > 0);
        if (!active) m.Items.Add(new Separator());
        Add("Rename…", () => RenameSeq(s));
        Add("Duplicate", () => ed.DuplicateSequence(s));
        Add("Sequence Settings…", () => { OpenSequence(s); SequenceSettings(); });
        m.Items.Add(new Separator());
        if (openSeqs.Count > 1 && openSeqs.Contains(s.Id)) Add("Close Tab", () => CloseSeqTab(s));
        Add("Delete Sequence…", () => DeleteSeq(s), ed.Sequences.Count > 1);
        return m;
    }

    void CloseSeqTab(Sequence s)
    {
        if (openSeqs.Count < 2) return;
        int i = openSeqs.IndexOf(s.Id);
        openSeqs.Remove(s.Id);
        if (s == ed.Seq) OpenSequence(ed.SeqById(openSeqs[Math.Clamp(i - 1, 0, openSeqs.Count - 1)])!);
        else RefreshSeqTabs();
    }

    void OpenSequence(Sequence s)
    {
        if (!openSeqs.Contains(s.Id)) openSeqs.Add(s.Id);
        if (s != ed.Seq) ed.Activate(s); else RefreshSeqTabs();
    }

    // ---------------- commands ----------------
    void SeqNew_Click(object sender, RoutedEventArgs e) => NewSequence();

    /// <summary>A new empty sequence in the open one's format (change it with the Format buttons or Sequence Settings).</summary>
    void NewSequence()
    {
        var name = AskName("New sequence", "A new, empty timeline in this project, in the same format as the open one.\n\nName:", ed.FreeName("Sequence"), "Create");
        if (name != null) ed.NewSequence(name);
    }

    void RenameSeq(Sequence s)
    {
        var name = AskName("Rename sequence", "Name:", s.Name, "Rename");
        if (name != null) ed.RenameSequence(s, name);
    }

    void DeleteSeq(Sequence s)
    {
        if (ed.Sequences.Count < 2) { Alert.Show(this, "A project keeps at least one sequence.", "Delete sequence"); return; }
        int uses = ed.UsesOf(s);
        if (!Alert.Confirm(this, $"Delete “{s.Name}”?" + (uses > 0 ? $"\n\nIt is used {uses} time{(uses == 1 ? "" : "s")} as a nested clip in other sequences; those clips go too." : "") +
                                 "\n\nUndo (Ctrl+Z) brings it back.", "Delete sequence", "Delete", "Cancel", AlertKind.Warning)) return;
        ed.DeleteSequence(s);
    }

    /// <summary>Clip ▸ Nest: the selected clips become one clip — a new sequence that can be opened, edited and reused.</summary>
    void NestSelected()
    {
        if (!ed.Clips.Any(ed.IsSel)) { Alert.Show(this, "Select the clips to nest first.", "Nest"); return; }
        var name = AskName("Nest", "The selected clips become one clip: a new sequence you can open (double-click it), edit and reuse in other sequences.\n\nName:",
                           ed.FreeName("Nested Sequence"), "Nest");
        if (name == null) return;
        var (s, why) = ed.NestSelection(name);
        if (s == null) Alert.Show(this, why ?? "These clips could not be nested.", "Nest", AlertKind.Warning);
    }

    /// <summary>A sequence as a clip in the open one, at the playhead (the Project panel's menu; dragging it onto the timeline does the same).</summary>
    void InsertSequence(Sequence s)
    {
        if (ed.SeqItem(s.Id) is not { } m) return;
        if (!ed.CanAdd(m)) { Alert.Show(this, $"“{s.Name}” can't go into “{ed.Seq.Name}”: it is this sequence, or contains it.", "Nested sequence", AlertKind.Warning); return; }
        if (s.End <= 0) { Alert.Show(this, $"“{s.Name}” is empty.", "Nested sequence"); return; }
        ed.AddMedia(m, null, ed.Time);
    }

    /// <summary>Opens a nested clip's sequence; with the playhead over the clip, at the same moment inside.</summary>
    void OpenNested(Clip c)
    {
        if (ed.SeqOf(c) is not { } s) return;
        bool over = ed.Time >= c.Start && ed.Time < c.End;
        double inner = Math.Clamp(c.In + (ed.Time - c.Start), 0, Math.Max(0, s.End));
        OpenSequence(s);
        if (over) ed.Seek(inner);
    }

    /// <summary>A name from the user (null = cancelled).</summary>
    string? AskName(string title, string message, string initial, string ok)
    {
        var tb = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 0), MinWidth = 280 };
        tb.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        int r = Alert.Show(this, message, title, AlertKind.Question, new[] { ok, "Cancel" }, tb);
        var n = tb.Text.Trim();
        return r == 0 && n.Length > 0 ? n : null;
    }

    // ---------------- Project panel ----------------
    /// <summary>What the Project panel lists: the sequences (once there is media or more than one), then the media.</summary>


    void Bin_RightClick(object sender, MouseButtonEventArgs e)
    {
        var at = ItemAt((DependencyObject)e.OriginalSource);
        if (at == null) { BinBackgroundMenu(); e.Handled = true; return; }
        if (at.Folder != null) { BinFolderMenu(at); e.Handled = true; return; }
        if (at is { Seq: null } media)
        {
            if (!BinList.SelectedItems.Contains(media)) BinList.SelectedItem = media;   // a clip: the source monitor, or straight onto the timeline at the playhead (its source marks, if any)
            var cm = new ContextMenu();
            void Item(string header, string? gesture, Action a) { var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" }; mi.Click += (_, _) => a(); cm.Items.Add(mi); }
            Item("Open in Source Monitor", "double-click", () => OpenSource(media));
            cm.Items.Add(new Separator());
            Item("Insert at the playhead", ",", () => { OpenSource(media); SourceEdit(true); });
            Item("Overwrite at the playhead", ".", () => { OpenSource(media); SourceEdit(false); });
            Item("Add to the timeline (first free place)", null, () => ed.AddMedia(media, null, ed.Time));
            if (media.SrcIn != null || media.SrcOut != null)
            {
                cm.Items.Add(new Separator());
                Item("Clear source In and Out", null, () => { media.SrcIn = media.SrcOut = null; if (ReferenceEquals(media, srcItem)) SrcRefresh(); });
            }
            cm.Items.Add(new Separator());
            Item(media.Offline ? "Relink…" : "Replace Footage…", null, () => RelinkMedia(media));
            Item("Reveal in File Explorer", null, () => { BinList.SelectedItem = media; ed.ClearSelection(); RevealInExplorer(); });
            Item("Properties…", null, () => ShowProperties(media));
            BinOrganiseMenu(cm, SelectedBinMedia());
            BinMediaExtras(cm, media);
            cm.PlacementTarget = BinList; cm.IsOpen = true; e.Handled = true;
            return;
        }
        if (ItemAt((DependencyObject)e.OriginalSource) is not { Seq: { } id } || ed.SeqById(id) is not { } s) return;
        var menu = SeqMenu(s);
        menu.PlacementTarget = BinList; menu.IsOpen = true; e.Handled = true;
    }

    /// <summary>The Project panel's picture of a sequence: a timeline (stacked clips) on a dark card.</summary>
    static ImageSource SeqIconImage()
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x2E)), null, new Rect(0, 0, 84, 48), 4, 4);
            var v = new SolidColorBrush(Color.FromRgb(0x4A, 0x86, 0xD8)); var a = new SolidColorBrush(Color.FromRgb(0x2F, 0xA7, 0x74)); var n = new SolidColorBrush(Color.FromRgb(0xC8, 0xA4, 0x30));
            dc.DrawRoundedRectangle(n, null, new Rect(14, 9, 34, 7), 1.5, 1.5);
            dc.DrawRoundedRectangle(v, null, new Rect(8, 19, 30, 7), 1.5, 1.5); dc.DrawRoundedRectangle(v, null, new Rect(40, 19, 36, 7), 1.5, 1.5);
            dc.DrawRoundedRectangle(a, null, new Rect(8, 31, 46, 7), 1.5, 1.5); dc.DrawRoundedRectangle(a, null, new Rect(56, 31, 20, 7), 1.5, 1.5);
            dc.DrawRectangle(Brushes.White, null, new Rect(51, 5, 1.5, 38));
        }
        var img = new DrawingImage(g); img.Freeze();
        return img;
    }
}
