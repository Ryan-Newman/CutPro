using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace CutPro;

/// <summary>The Render Queue panel (partial MainWindow, left side bar): every queued export with its progress, what it is doing, and its
/// buttons (cancel, remove, render again, show the file); Pause / Resume the queue, clear the finished ones. The status bar shows the job that
/// renders. Closing CutPro while jobs remain asks first.</summary>
public partial class MainWindow
{
    DockPanel QueueView = null!;
    StackPanel queueList = null!;
    TextBlock queueHead = null!;
    Button queuePause = null!;
    bool queueStale = true;

    void BuildQueueView()
    {
        QueueView = new DockPanel { Background = Brushes.Transparent };
        var head = new Grid { Height = 35 };                                  // (first child: the docking's drag handle)
        var title = new TextBlock { Text = "RENDER QUEUE" }; title.SetResourceReference(StyleProperty, "PaneHeader");
        head.Children.Add(title);
        DockPanel.SetDock(head, Dock.Top); QueueView.Children.Add(head);
        var bar = new DockPanel { Margin = new Thickness(8, 0, 8, 6) };
        queuePause = new Button { Content = "Pause", Padding = new Thickness(10, 3, 10, 3), ToolTip = "Pause the queue after… (the job rendering now carries on)" };
        queuePause.Click += (_, _) => RenderQueue.Paused = !RenderQueue.Paused;
        var clear = new Button { Content = "Clear Finished", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += (_, _) => RenderQueue.ClearFinished();
        DockPanel.SetDock(queuePause, Dock.Left); DockPanel.SetDock(clear, Dock.Left);
        bar.Children.Add(queuePause); bar.Children.Add(clear);
        queueHead = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        queueHead.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        bar.Children.Add(queueHead);
        DockPanel.SetDock(bar, Dock.Top); QueueView.Children.Add(bar);
        queueList = new StackPanel { Margin = new Thickness(6, 0, 6, 6) };
        QueueView.Children.Add(new ScrollViewer { Content = queueList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        LeftHost.Children.Add(QueueView);
        QueueView.IsVisibleChanged += (_, _) => { if (QueueView.IsVisible) RefreshQueue(); };
        RenderQueue.Changed += () => { queueStale = true; Dispatcher.BeginInvoke(QueueTick, System.Windows.Threading.DispatcherPriority.Background); };
        RefreshQueue();
    }

    long queueDrawn;
    /// <summary>Throttled (≤ 5 per second): the panel while it shows, the status bar task while a job renders.</summary>
    void QueueTick()
    {
        if (!queueStale) return;
        long now = Environment.TickCount64;
        if (now - queueDrawn < 200) { Dispatcher.BeginInvoke(async () => { await Task.Delay(220); QueueTick(); }); return; }
        queueDrawn = now;
        RefreshQueue();
        var cur = RenderQueue.Current;
        var all = RenderQueue.Jobs;
        int left = all.Count(j => j.Status == RenderQueue.State.Queued);
        if (cur != null) SetTask($"Render queue: “{cur.Name}” {cur.Progress * 100:0}%" + (left > 0 ? $" · {left} waiting" : ""), 0);
        else if (all.Any(j => j.Finished is { } f && (DateTime.Now - f).TotalSeconds < 1.5)) SetTask(all.Last(j => j.Finished != null) is { } last
            ? $"Render queue: “{last.Name}” {(last.Status == RenderQueue.State.Done ? "done" : last.Status.ToString().ToLowerInvariant())}" : "", 3000);
    }

    void RefreshQueue()
    {
        if (!Shows(QueueView) && !queueStale) return;
        queueStale = false;
        var jobs = RenderQueue.Jobs;
        queuePause.Content = RenderQueue.Paused ? "Resume" : "Pause";
        Loc.Apply(queuePause);
        int waiting = jobs.Count(j => j.Status == RenderQueue.State.Queued), done = jobs.Count(j => j.Status == RenderQueue.State.Done);
        queueHead.Text = jobs.Count == 0 ? "" : $"{done} done · {waiting} waiting" + (RenderQueue.Paused ? " · paused" : "");
        queueList.Children.Clear();
        if (jobs.Count == 0)
        {
            var empty = new TextBlock { Text = "Nothing queued. Export ▸ Add to Queue… renders a sequence in the background while you keep editing.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 0) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
            queueList.Children.Add(empty);
            return;
        }
        foreach (var j in jobs)
        {
            var card = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BorderBrushProperty, "B.Border");
            var sp = new StackPanel();
            var name = new TextBlock { Text = j.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var file = new TextBlock { Text = Path.GetFileName(j.Opts.OutputPath(j.Output)) + "  ·  " + j.Opts.Name, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = j.Output };
            file.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
            sp.Children.Add(name); sp.Children.Add(file);
            var pb = new ProgressBar { Height = 4, Maximum = 1, Value = j.Progress, Margin = new Thickness(0, 5, 0, 3) };
            sp.Children.Add(pb);
            string state = j.Status switch
            {
                RenderQueue.State.Queued => "Waiting",
                RenderQueue.State.Rendering => (j.Step.Length > 0 ? j.Step.TrimEnd('…', '.') + " — " : "Rendering — ") + $"{j.Progress * 100:0}%",
                RenderQueue.State.Done => $"Done in {((j.Finished ?? DateTime.Now) - (j.Started ?? DateTime.Now)).TotalSeconds:0} s",
                RenderQueue.State.Cancelled => "Cancelled",
                _ => "Failed: " + j.Error.Split('\n')[0],
            };
            var row = new DockPanel();
            var st = new TextBlock { Text = state, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = j.Error.Length > 0 ? j.Error : null };
            if (j.Status == RenderQueue.State.Failed) st.SetResourceReference(TextBlock.ForegroundProperty, "B.Error");
            Button Btn(string glyph, string tip, Action a)
            {
                var b = new Button { Content = glyph, ToolTip = tip, FontSize = 11, Width = 24, Height = 22, Margin = new Thickness(2, 0, 0, 0) };
                b.SetResourceReference(StyleProperty, "Icon");
                System.Windows.Automation.AutomationProperties.SetName(b, tip);
                b.Click += (_, _) => a();
                DockPanel.SetDock(b, Dock.Right); row.Children.Add(b);
                return b;
            }
            if (j.Status is RenderQueue.State.Queued or RenderQueue.State.Rendering) Btn("", "Cancel this export", () => RenderQueue.Cancel(j));
            else
            {
                Btn("", "Remove from the queue", () => RenderQueue.Remove(j));
                Btn("", "Render again (the sequence as it was queued)", () => RenderQueue.Retry(j));
                if (j.Status == RenderQueue.State.Done) Btn("", "Show in Explorer", () => RevealFile(j.Opts.ImageSequence ? Path.GetDirectoryName(j.Output)! : j.Output));
            }
            row.Children.Add(st);
            sp.Children.Add(row);
            card.Child = sp;
            queueList.Children.Add(card);
        }
        Loc.Apply(queueList);
    }

    static void RevealFile(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\""); } catch { }
    }

    /// <summary>Closing while the queue renders: ask (the jobs are lost).</summary>
    bool ConfirmQueueExit()
    {
        if (!RenderQueue.Busy || AppSettings.ReadOnly) return true;
        int n = RenderQueue.Jobs.Count(j => j.Status is RenderQueue.State.Queued or RenderQueue.State.Rendering);
        if (!Alert.Confirm(this, $"{n} export{(n == 1 ? " is" : "s are")} still in the render queue. Quit and cancel {(n == 1 ? "it" : "them")}?", "Render queue", "Quit", "Keep CutPro open", AlertKind.Warning)) return false;
        foreach (var j in RenderQueue.Jobs) RenderQueue.Cancel(j);
        return true;
    }
}
