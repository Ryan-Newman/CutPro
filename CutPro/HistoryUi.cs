using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CutPro;

/// <summary>The History panel (partial MainWindow, a dockable panel tabbed with Effect Controls): every undo step of the project by name, oldest
/// first, the current state highlighted and the steps that can be redone dimmed; click a step to go back (or forward) to it — Premiere's
/// History panel on the editor's one undo history.</summary>
public partial class MainWindow
{
    DockPanel HistoryView = null!;
    ListBox histList = null!;
    TextBlock histCount = null!;
    bool histBusy, histStale = true;

    void BuildHistoryView()
    {
        HistoryView = new DockPanel { Background = Brushes.Transparent };
        var head = new Grid { Height = 35 };                                  // (first child: the docking's drag handle)
        var title = new TextBlock { Text = "HISTORY" }; title.SetResourceReference(StyleProperty, "PaneHeader");
        head.Children.Add(title);
        histCount = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontSize = 11 };
        histCount.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        head.Children.Add(histCount);
        DockPanel.SetDock(head, Dock.Top); HistoryView.Children.Add(head);
        histList = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(6, 0, 6, 6) };
        System.Windows.Automation.AutomationProperties.SetName(histList, "Undo history");
        ScrollViewer.SetHorizontalScrollBarVisibility(histList, ScrollBarVisibility.Disabled);
        histList.SelectionChanged += (_, _) =>
        {
            if (histBusy || histList.SelectedItem is not ListBoxItem { Tag: int row }) return;
            int undoCount = ed.UndoLabels.Count();
            Pause();
            ed.StepHistory(row - undoCount);                                  // (row k = the state after the k-th step; 0 = before any)
        };
        HistoryView.Children.Add(histList);
        RightHost.Children.Add(HistoryView);                                 // (its home: a tab next to Effect Controls)
        HistoryView.IsVisibleChanged += (_, _) => { if (HistoryView.IsVisible) Dispatcher.BeginInvoke(RefreshHistory, System.Windows.Threading.DispatcherPriority.Background); };
        HistoryView.SizeChanged += (_, _) => { if (histStale) Dispatcher.BeginInvoke(RefreshHistory, System.Windows.Threading.DispatcherPriority.Background); };
        ed.HistoryChanged += () => { histStale = true; if (Shows(HistoryView)) Dispatcher.BeginInvoke(RefreshHistory, System.Windows.Threading.DispatcherPriority.Background); };
    }

    /// <summary>Rebuilds the list (only while the panel shows; it is marked stale otherwise and rebuilt when shown).</summary>
    void RefreshHistory()
    {
        if (!histStale || !Shows(HistoryView)) return;
        histStale = false;
        var undo = ed.UndoLabels.Reverse().ToList();                        // oldest first
        var redo = ed.RedoLabels.ToList();                                  // the next one first
        histBusy = true;
        histList.Items.Clear();
        ListBoxItem Row(string text, int row, bool future, string glyph)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var g = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Width = 18, VerticalAlignment = VerticalAlignment.Center };
            var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(g); sp.Children.Add(t);
            var it = new ListBoxItem { Content = sp, Tag = row, Opacity = future ? 0.45 : 1, ToolTip = future ? "Redo up to here" : row == undo.Count ? "The current state" : "Undo back to here" };
            System.Windows.Automation.AutomationProperties.SetName(it, text + (future ? " (undone)" : ""));
            histList.Items.Add(it);
            return it;
        }
        Row(projectPath != null ? Loc.T("Open") + " " + Path.GetFileName(projectPath) : Loc.T("New project"), 0, false, "");
        for (int i = 0; i < undo.Count; i++) Row(Loc.T(undo[i]), i + 1, false, "");
        for (int i = 0; i < redo.Count; i++) Row(Loc.T(redo[i]), undo.Count + 1 + i, true, "");
        var current = (ListBoxItem)histList.Items[undo.Count];
        histList.SelectedItem = current;
        histList.ScrollIntoView(current);
        histCount.Text = Loc.Current == "en" ? $"{undo.Count} step{(undo.Count == 1 ? "" : "s")}" + (redo.Count > 0 ? $" · {redo.Count} undone" : "")
                                            : $"{undo.Count} {Loc.T("steps")}" + (redo.Count > 0 ? $" · {redo.Count} {Loc.T("undone")}" : "");
        histBusy = false;
    }
}
