using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;

namespace CutPro;

/// <summary>Project panel organisation (the model): bins are folder paths ("Footage/Day 1"); a media item names its bin
/// (<see cref="MediaItem.Bin"/>, "" = the top). <see cref="Editor.Bins"/> keeps every bin, so empty ones survive saving.</summary>
public static class Bins
{
    public static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    public static string Leaf(string path) => path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
    public static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;
    public static bool Inside(string path, string bin) => path == bin || path.StartsWith(bin + "/", StringComparison.Ordinal);
    /// <summary>The bins directly inside a bin.</summary>
    public static List<string> Children(IEnumerable<string> bins, string parent) => bins.Where(b => b.Length > 0 && Parent(b) == parent).Distinct().OrderBy(Leaf, StringComparer.CurrentCultureIgnoreCase).ToList();
    /// <summary>A name not taken in the parent ("New Bin", "New Bin 2", …).</summary>
    public static string FreeName(IEnumerable<string> bins, string parent, string name)
    {
        var taken = Children(bins, parent).Select(Leaf).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int i = 2; ; i++) if (!taken.Contains($"{name} {i}")) return $"{name} {i}";
    }

    /// <summary>The filters of the Project panel's filter box.</summary>
    public static readonly string[] Filters = { "All", "Video", "Audio", "Images", "Sequences", "Labelled", "Rated", "Offline", "Not used" };
    public static readonly string[] Sorts = { "Name", "Duration", "Type", "Rating", "Label", "Date added" };

    /// <summary>Search: every word of the query in the name, keywords, bin, metadata or what is said in it.</summary>
    public static bool Matches(MediaItem m, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string hay = string.Join("\n", new[] { m.Name, m.Keywords, m.Bin, m.Label?.ToString() ?? "", m.Timecode ?? "", m.Reel ?? "" }
                                       .Concat(m.Meta.Select(kv => kv.Value)));
        string said = m.Words is { Count: > 0 } w ? string.Join(" ", w.Select(x => x.W)) : "";
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .All(q => hay.Contains(q, StringComparison.CurrentCultureIgnoreCase) || said.Contains(q, StringComparison.CurrentCultureIgnoreCase));
    }
    public static bool Passes(MediaItem m, string filter, Func<MediaItem, bool> used) => filter switch
    {
        "Video" => m.Seq == null && m.Type == ClipKind.Video,
        "Audio" => m.Seq == null && m.Type == ClipKind.Audio,
        "Images" => m.Seq == null && m.Type == ClipKind.Image,
        "Sequences" => m.Seq != null,
        "Labelled" => m.Label != null,
        "Rated" => m.Rating > 0,
        "Offline" => m.Offline,
        "Not used" => m.Seq == null && !used(m),
        _ => true,
    };
    public static IEnumerable<MediaItem> Sort(IEnumerable<MediaItem> items, string by) => by switch
    {
        "Duration" => items.OrderByDescending(m => m.Duration),
        "Type" => items.OrderBy(m => m.Kind).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
        "Rating" => items.OrderByDescending(m => m.Rating).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
        "Label" => items.OrderBy(m => m.Label == null).ThenBy(m => m.Label).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
        "Date added" => items.OrderByDescending(m => m.Added),
        _ => items.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
    };
}

public partial class Editor
{
    /// <summary>Every bin of the project (folder paths; <see cref="Bins"/>).</summary>
    public List<string> Bins = new();

    public string NewBin(string parent, string name = "New Bin")
    {
        var path = CutPro.Bins.Join(parent, CutPro.Bins.FreeName(Bins, parent, name.Replace("/", "-")));
        Bins.Add(path); RaiseChanged();
        return path;
    }
    /// <summary>Renames a bin (and the bins and media inside it). False when the name is taken.</summary>
    public bool RenameBin(string path, string name)
    {
        name = name.Replace("/", "-").Trim();
        if (name.Length == 0) return false;
        var to = CutPro.Bins.Join(CutPro.Bins.Parent(path), name);
        if (to == path) return true;
        if (Bins.Contains(to)) return false;
        string Moved(string p) => CutPro.Bins.Inside(p, path) ? to + p[path.Length..] : p;
        for (int i = 0; i < Bins.Count; i++) Bins[i] = Moved(Bins[i]);
        foreach (var m in Media) m.Bin = Moved(m.Bin);
        RaiseChanged();
        return true;
    }
    /// <summary>Deletes a bin; what was in it (media and bins) moves up to its parent — nothing leaves the project.</summary>
    public void DeleteBin(string path)
    {
        var parent = CutPro.Bins.Parent(path);
        string Up(string p) => p == path ? parent : CutPro.Bins.Inside(p, path) ? CutPro.Bins.Join(parent, p[(path.Length + 1)..]) : p;
        Bins = Bins.Where(b => b != path).Select(Up).Distinct().ToList();
        foreach (var m in Media) m.Bin = Up(m.Bin);
        RaiseChanged();
    }
    public void MoveToBin(IEnumerable<MediaItem> items, string bin)
    {
        if (bin.Length > 0 && !Bins.Contains(bin)) Bins.Add(bin);
        foreach (var m in items.Where(m => m.Seq == null && m.Folder == null)) m.Bin = bin;
        RaiseChanged();
    }
    /// <summary>Whether a media item is used by any sequence of the project.</summary>
    public bool Used(MediaItem m) => Sequences.Any(s => s.Clips.Any(c => c.MediaId == m.Id));
}

/// <summary>Project panel: bins (folders), search, filters, sort, icon / list view, colour labels, ratings, keywords.</summary>
public partial class MainWindow
{
    string binPath = "";
    string binQuery = "", binFilter = "All";
    TextBox? binSearch;
    ComboBox? binFilterBox, binSortBox;
    TextBlock? binCrumb;
    FrameworkElement? binCrumbRow;
    ToggleButton? binViewBtn;
    DataTemplate? binIconTemplate;

    void InitBins()
    {
        binIconTemplate = BinList.ItemTemplate;
        var dim = (Brush)FindResource("B.Fg.Dim");
        var bar = new DockPanel { Margin = new Thickness(10, 0, 8, 4), LastChildFill = true };
        DockPanel.SetDock(bar, Dock.Top);
        binViewBtn = new ToggleButton { Content = "", FontFamily = (FontFamily)FindResource("IconFont"), ToolTip = "List view (one line per item)", Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(4, 0, 0, 0), IsChecked = S.BinListView };
        binViewBtn.SetResourceReference(StyleProperty, "Tool");
        AutomationProperties.SetName(binViewBtn, "List view");
        binViewBtn.Click += (_, _) => { S.BinListView = binViewBtn.IsChecked == true; if (!AppSettings.ReadOnly) AppSettings.Save(); ApplyBinView(); };
        var newBin = new Button { Content = "", ToolTip = "New bin (folder)", Margin = new Thickness(2, 0, 0, 0) };
        newBin.SetResourceReference(StyleProperty, "Icon");
        AutomationProperties.SetName(newBin, "New bin");
        newBin.Click += (_, _) => NewBinHere();
        DockPanel.SetDock(binViewBtn, Dock.Right); DockPanel.SetDock(newBin, Dock.Right);
        bar.Children.Add(binViewBtn); bar.Children.Add(newBin);
        binSearch = new TextBox { ToolTip = "Search names, keywords, labels, metadata and what is said (transcribed clips)", VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(binSearch, "Search the project");
        var hint = new TextBlock { Text = "Search", Foreground = dim, IsHitTestVisible = false, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        binSearch.TextChanged += (_, _) => { hint.Visibility = binSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; binQuery = binSearch.Text; RefreshBin(); };
        var sg = new Grid(); sg.Children.Add(binSearch); sg.Children.Add(hint);
        bar.Children.Add(sg);

        var row2 = new Grid { Margin = new Thickness(10, 0, 8, 4) };
        row2.ColumnDefinitions.Add(new ColumnDefinition()); row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); row2.ColumnDefinitions.Add(new ColumnDefinition());
        DockPanel.SetDock(row2, Dock.Top);
        binFilterBox = new ComboBox { ItemsSource = Bins.Filters, SelectedIndex = 0, ToolTip = "Show only…" };
        binFilterBox.SelectionChanged += (_, _) => { binFilter = (string)binFilterBox.SelectedItem; RefreshBin(); };
        binSortBox = new ComboBox { ItemsSource = Bins.Sorts, SelectedItem = Bins.Sorts.Contains(S.BinSort) ? S.BinSort : "Name", ToolTip = "Sort by" };
        binSortBox.SelectionChanged += (_, _) => { S.BinSort = (string)binSortBox.SelectedItem; if (!AppSettings.ReadOnly) AppSettings.Save(); RefreshBin(); };
        AutomationProperties.SetName(binFilterBox, "Filter"); AutomationProperties.SetName(binSortBox, "Sort by");
        Grid.SetColumn(binSortBox, 2);
        row2.Children.Add(binFilterBox); row2.Children.Add(binSortBox);

        // where we are: Project ▸ Footage ▸ Day 1 (⮤ goes up; clips dropped on it move up)
        var crumb = new DockPanel { Margin = new Thickness(10, 0, 8, 4), Visibility = Visibility.Collapsed, AllowDrop = true, Background = Brushes.Transparent };
        DockPanel.SetDock(crumb, Dock.Top);
        var up = new Button { Content = "", ToolTip = "Up one bin (Backspace)" };
        up.SetResourceReference(StyleProperty, "Icon");
        AutomationProperties.SetName(up, "Up one bin");
        up.Click += (_, _) => OpenBin(Bins.Parent(binPath));
        DockPanel.SetDock(up, Dock.Left);
        binCrumb = new TextBlock { Foreground = dim, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 0, 0) };
        crumb.Children.Add(up); crumb.Children.Add(binCrumb);
        crumb.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("CutPro.Media") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        crumb.Drop += (_, e) => { if (DraggedMedia(e) is { Count: > 0 } l) ed.MoveToBin(l, Bins.Parent(binPath)); e.Handled = true; };
        binCrumbRow = crumb;

        ProjectView.Children.Insert(1, crumb);
        ProjectView.Children.Insert(1, row2);
        ProjectView.Children.Insert(1, bar);
        BinList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Back && binPath.Length > 0) { OpenBin(Bins.Parent(binPath)); e.Handled = true; }
            else if (e.Key == Key.Enter && BinList.SelectedItem is MediaItem { Folder: { } f }) { OpenBin(f); e.Handled = true; }
            else if (e.Key == Key.F2 && BinList.SelectedItem is MediaItem { Folder: { } rf }) { RenameBinAsk(rf); e.Handled = true; }
        };
        ApplyBinView();
    }

    void ApplyBinView()
    {
        BinList.ItemTemplate = S.BinListView ? (DataTemplate)BinList.Resources["BinRowTemplate"] : binIconTemplate;
        if (binViewBtn != null) binViewBtn.IsChecked = S.BinListView;
    }

    void OpenBin(string path)
    {
        binPath = path.Length > 0 && !ed.Bins.Contains(path) ? "" : path;
        if (binSearch != null && binSearch.Text.Length > 0) binSearch.Text = "";      // (refreshes)
        RefreshBin();
    }
    void RefreshBin() { binRef = null; binSig = ""; OnChanged(); }

    /// <summary>What the Project panel lists: searching = every match in the project (any bin); else the open bin's bins, its sequences
    /// (the top bin), its media — filtered and sorted.</summary>
    List<MediaItem> BinItems()
    {
        if (binPath.Length > 0 && !ed.Bins.Contains(binPath)) binPath = "";
        if (binCrumbRow != null)
        {
            binCrumbRow.Visibility = binPath.Length > 0 && binQuery.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            binCrumb!.Text = Loc.T("Project") + string.Concat(binPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(p => "  ▸  " + p));
        }
        var seqs = ed.Media.Count > 0 || ed.Sequences.Count > 1 || ed.Bins.Count > 0 ? ed.SeqItems : new List<MediaItem>();
        bool Used(MediaItem m) => ed.Used(m);
        if (binQuery.Trim().Length > 0)
            return Bins.Sort(seqs.Concat(ed.Media).Where(m => Bins.Matches(m, binQuery) && Bins.Passes(m, binFilter, Used)), S.BinSort).ToList();
        var folders = Bins.Children(ed.Bins, binPath).Select(b => new MediaItem
        {
            Id = "bin:" + b, Folder = b, FolderName = Bins.Leaf(b),
            FolderCount = ed.Media.Count(m => Bins.Inside(m.Bin, b)) + ed.Bins.Count(x => x != b && Bins.Inside(x, b)),
        });
        var here = (binPath.Length == 0 ? seqs : new List<MediaItem>()).Concat(ed.Media.Where(m => m.Bin == binPath || m.Bin.Length > 0 && !ed.Bins.Contains(m.Bin) && binPath.Length == 0));
        return folders.Concat(Bins.Sort(here.Where(m => Bins.Passes(m, binFilter, Used)), S.BinSort)).ToList();
    }

    void NewBinHere()
    {
        var moving = SelectedBinMedia();                                         // (before the list refreshes)
        var b = ed.NewBin(binPath);
        if (moving.Count > 0) ed.MoveToBin(moving, b);                             // (Premiere: New Bin from a selection puts it in)
        RefreshBin();
        if (BinList.Items.Cast<MediaItem>().FirstOrDefault(m => m.Folder == b) is { } item) { BinList.SelectedItem = item; BinList.ScrollIntoView(item); }
        RenameBinAsk(b);
    }
    void RenameBinAsk(string path)
    {
        if (AskName("Rename Bin", "Bin name:", Bins.Leaf(path), "Rename") is not { } n) return;
        if (!ed.RenameBin(path, n)) { Alert.Show(this, $"There is already a bin called “{n}” here.", "Rename Bin"); return; }
        if (Bins.Inside(binPath, path)) binPath = Bins.Join(Bins.Parent(path), n.Replace("/", "-").Trim()) + binPath[path.Length..];
        RefreshBin();
    }

    List<MediaItem> SelectedBinMedia() => BinList.SelectedItems.Cast<MediaItem>().Where(m => m.Folder == null && m.Seq == null).ToList();
    /// <summary>Media dragged from the Project panel (the selection when the dragged item is part of it).</summary>
    List<MediaItem>? DraggedMedia(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("CutPro.Media")) return null;
        var id = (string)e.Data.GetData("CutPro.Media")!;
        var m = ed.Media.FirstOrDefault(x => x.Id == id);
        if (m == null) return null;
        var sel = SelectedBinMedia();
        return sel.Contains(m) ? sel : new List<MediaItem> { m };
    }

    /// <summary>The right-click menu's organising part (for every selected media item).</summary>
    void BinOrganiseMenu(ContextMenu cm, List<MediaItem> items)
    {
        if (items.Count == 0) return;
        void Apply(Action<MediaItem> f) { foreach (var m in items) f(m); RefreshBin(); }
        var label = new MenuItem { Header = "Label" };
        foreach (MarkerColor col in Enum.GetValues(typeof(MarkerColor)))
        {
            var mi = new MenuItem { Header = col.ToString(), IsChecked = items.All(m => m.Label == col), Icon = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Marker.Rgb(col)) } };
            mi.Click += (_, _) => Apply(m => m.Label = col);
            label.Items.Add(mi);
        }
        var none = new MenuItem { Header = "No Label" }; none.Click += (_, _) => Apply(m => m.Label = null); label.Items.Add(new Separator()); label.Items.Add(none);
        var rate = new MenuItem { Header = "Rating" };
        for (int r = 0; r <= 5; r++)
        {
            int rr = r;
            var mi = new MenuItem { Header = r == 0 ? "No Rating" : new string('★', r), IsChecked = items.All(m => m.Rating == rr), InputGestureText = r == 0 ? "" : r.ToString() };
            mi.Click += (_, _) => Apply(m => m.Rating = rr);
            rate.Items.Add(mi);
        }
        var kw = new MenuItem { Header = "Keywords…" };
        kw.Click += (_, _) =>
        {
            if (AskName("Keywords", "Keywords (comma-separated; the search finds them):", items[0].Keywords, "OK") is { } k)
                Apply(m => m.Keywords = string.Join(", ", k.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0)));
        };
        var move = new MenuItem { Header = "Move to Bin" };
        void Target(string path, string header, int depth)
        {
            var mi = new MenuItem { Header = new string(' ', depth * 3) + header, IsChecked = items.All(m => m.Bin == path) };
            mi.Click += (_, _) => ed.MoveToBin(items, path);
            move.Items.Add(mi);
            foreach (var c in Bins.Children(ed.Bins, path)) Target(c, Bins.Leaf(c), depth + 1);
        }
        Target("", Loc.T("Project") + " (top)", 0);
        move.Items.Add(new Separator());
        var nb = new MenuItem { Header = "New Bin…" };
        nb.Click += (_, _) => { var b = ed.NewBin(binPath); ed.MoveToBin(items, b); RenameBinAsk(b); };
        move.Items.Add(nb);
        cm.Items.Add(new Separator());
        cm.Items.Add(move); cm.Items.Add(label); cm.Items.Add(rate); cm.Items.Add(kw);
    }

    void BinFolderMenu(MediaItem folder)
    {
        var cm = new ContextMenu();
        void Item(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); cm.Items.Add(mi); }
        Item("Open Bin", () => OpenBin(folder.Folder!));
        Item("Rename Bin…", () => RenameBinAsk(folder.Folder!));
        Item("New Bin Inside", () => { var b = ed.NewBin(folder.Folder!); OpenBin(folder.Folder!); RenameBinAsk(b); });
        cm.Items.Add(new Separator());
        Item("Delete Bin (keeps its media)", () => { ed.DeleteBin(folder.Folder!); RefreshBin(); });
        cm.PlacementTarget = BinList; cm.IsOpen = true;
    }

    void BinBackgroundMenu()
    {
        var cm = new ContextMenu();
        void Item(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); cm.Items.Add(mi); }
        Item("Import Media…", () => Import_Click(this, new RoutedEventArgs()));
        Item("New Bin", NewBinHere);
        Item("New Sequence…", NewSequence);
        if (binPath.Length > 0) { cm.Items.Add(new Separator()); Item("Up One Bin", () => OpenBin(Bins.Parent(binPath))); }
        cm.PlacementTarget = BinList; cm.IsOpen = true;
    }

    /// <summary>Number keys rate the selected media (Premiere's ratings), 0 clears.</summary>
    bool BinRatingKey(Key k)
    {
        if (!BinList.IsKeyboardFocusWithin || Keyboard.Modifiers != ModifierKeys.None) return false;
        int r = k is >= Key.D0 and <= Key.D5 ? k - Key.D0 : k is >= Key.NumPad0 and <= Key.NumPad5 ? k - Key.NumPad0 : -1;
        var items = SelectedBinMedia();
        if (r < 0 || items.Count == 0) return false;
        foreach (var m in items) m.Rating = r;
        RefreshBin();
        return true;
    }
}
