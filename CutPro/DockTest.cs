using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CutPro;

/// <summary>
/// --docktest out.txt: dockable panels through the real window. Every kind of move (left / right / bottom / own window, Effect Controls to
/// the left, the Program monitor and the timeline in their own windows, full screen, maximize, closing a window docks it back), what keeps
/// working where it lands (the inspector fills, the mixer counts as showing, the monitor settles on a frame and plays from a floating
/// window), workspaces (built-in, saved, the JSON round trip), and that the user's settings file isn't written (read-only run).
/// </summary>
public partial class MainWindow
{
    public async void DockScenario(string outFile)
    {
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        Alert.Auto = (title, msg, _) => { log.Add($"  ALERT {title}: {msg.Replace('\n', ' ')}"); return 0; };
        async Task Settle(int ms = 350) { UpdateLayout(); await Task.Delay(ms); UpdateLayout(); }
        DependencyObject? Up(DependencyObject e) => LogicalTreeHelper.GetParent(e) ?? VisualTreeHelper.GetParent(e);
        bool Inside(DependencyObject e, DependencyObject host) { for (var p = e; p != null; p = Up(p)) if (p == host) return true; return false; }
        async Task<bool> Frame(double t)
        {
            ed.Seek(t); var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000) { await Task.Delay(50); if (Pv.Settled(ed.Time)) return true; }
            return false;
        }
        try
        {
            var settingsFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CutPro", "settings.json");
            var stamp = File.Exists(settingsFile) ? File.GetLastWriteTimeUtc(settingsFile) : DateTime.MinValue;
            var d = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cutpro_test");
            await ImportAsync(new[] { System.IO.Path.Combine(d, "a.mp4"), System.IO.Path.Combine(d, "b.mp4") });
            if (ed.Media.Count > 1) { ed.AddMedia(ed.Media[0], null, 0); ed.AddMedia(ed.Media[1], null, ed.End); }
            await Settle(800);

            // default layout
            Check(Inside(ProjectView, LeftHost) && Inside(InspectorView, RightHost) && EditorCard.Child == EditorGrid && PanelCard.Child == PanelGrid && Shows(ProjectView),
                  "default (Editing) layout: Project in the left side bar, Effect Controls on the right, monitor and timeline home");

            // the mixer to the right: tabs appear, it shows, the activity bar loses its icon
            MoveDock("Mixer", "Right"); await Settle();
            Check(Inside(MixerView, RightHost) && RightTabsBar.Visibility == Visibility.Visible && RightTabs.Children.Count == 3 && Shows(MixerView) && !Shows(InspectorView)
                  && ActMixer.Visibility == Visibility.Collapsed, "Mixer → right side bar: a third tab (Effect Controls, Source, Mixer), the mixer shows (meters run), its activity icon goes");
            ShowDock("Inspector"); await Settle();
            Check(Shows(InspectorView) && !Shows(MixerView), "right tabs: Effect Controls again, the mixer hidden");

            // the scopes into the bottom panel: a tab next to TIMELINE / KEYFRAMES
            MoveDock("Scopes", "Bottom"); await Settle();
            Check(Inside(ScopesView, BottomHost) && DockTabs.Children.Count == 1 && Shows(ScopesView) && !Shows(Tl), "Scopes → bottom panel: its tab is chosen, the timeline steps aside");
            ShowPanel("Timeline"); await Settle();
            Check(Shows(Tl) && !Shows(ScopesView) && BottomHost.Visibility == Visibility.Collapsed, "TIMELINE tab: the timeline back, the scopes hidden");

            // Effect Controls to the left: an activity icon, the inspector fills there with its card's ink
            MoveDock("Inspector", "Left"); await Settle();
            ed.Select(ed.Clips.First(c => c.Kind == ClipKind.Video)); BuildInspector(); await Settle();
            Check(Inside(InspectorView, LeftHost) && ActInspector.Visibility == Visibility.Visible && Shows(InspectorView) && Inspector.Children.Count > 3 && inkScope == SideCard,
                  $"Effect Controls → left side bar: activity icon, {Inspector.Children.Count} rows built, ink from the side bar's card");
            Check(Inside(MixerView, RightHost) && Shows(MixerView), "with Effect Controls gone, the right side bar shows the mixer alone");

            // a side view in its own window; closing the window docks it back home
            MoveDock("Captions", "Float"); await Settle(500);
            var cw = DockOf("Captions")!.Win;
            Check(cw != null && cw.IsVisible && Inside(CaptionsView, cw), "Captions → own window");
            cw?.Close(); await Settle(500);
            Check(DockOf("Captions")!.Zone == "Left" && Inside(CaptionsView, LeftHost) && DockOf("Captions")!.Win == null, "closing its window docks it back in the left side bar");

            // the Program monitor in its own window: it settles on frames and plays
            MoveDock("Program", "Float"); await Settle(800);
            var pw = DockOf("Program")!.Win;
            bool f1 = await Frame(1.2);
            Check(pw != null && Inside(EditorGrid, pw) && EditorCard.Child == programPlaceholder && f1, "Program monitor → own window: placeholder at home, the frame at 1.2 s settles there");
            double t0 = ed.Time; Play(); await Task.Delay(1500); Pause(); await Settle();
            Check(ed.Time - t0 > 1.0, $"plays from the floating monitor: {ed.Time - t0:0.00} s in 1.5 s");
            MoveDock("Program", "Center"); await Settle(600);
            Check(EditorCard.Child == EditorGrid && DockOf("Program")!.Win == null && await Frame(2.0), "monitor docked back: home, settles again");

            // the timeline panel in its own window
            MoveDock("Timeline", "Float"); await Settle(600);
            var tw = DockOf("Timeline")!.Win;
            Check(tw != null && Inside(PanelGrid, tw) && PanelRow.ActualHeight < 1 && Shows(Tl), "timeline → own window: the monitor takes the whole height, the timeline draws in the window");
            MoveDock("Timeline", "Bottom"); await Settle(600);
            Check(PanelCard.Child == PanelGrid && PanelRow.ActualHeight >= 150, $"timeline docked back ({PanelRow.ActualHeight:0} px high)");

            // full screen and back
            var home = MonitorBox.Parent;
            ToggleFullScreen(); await Settle(800);
            bool fsFrame = await Frame(3.0);
            Check(FullScreenOn && MonitorBox.Parent != home && fsFrame, "full-screen playback: the monitor in a borderless window, a frame settles");
            ToggleFullScreen(); await Settle(500);
            Check(!FullScreenOn && MonitorBox.Parent == home && Grid.GetRow(MonitorBox) == 1, "out of full screen: the monitor back in its row");

            // maximize the panel under the mouse (no mouse in tests: the monitor) and back
            double ph = PanelRow.ActualHeight;
            ToggleMaximizePanel("Program"); await Settle();
            bool max = PanelRow.ActualHeight < 1 && !sideVisible;
            ToggleMaximizePanel("Program"); await Settle();
            Check(max && Math.Abs(PanelRow.ActualHeight - ph) < 2 && sideVisible, $"maximize (`): the monitor alone, then back ({ph:0} → {PanelRow.ActualHeight:0} px)");
            double eh = EditorRow.ActualHeight;
            ToggleMaximizePanel("Timeline"); await Settle();
            bool tmax = EditorRow.ActualHeight < 1 && PanelRow.ActualHeight > ph + 100;
            ToggleMaximizePanel(); await Settle();
            Check(tmax && Math.Abs(EditorRow.ActualHeight - eh) < 2, $"maximize the timeline: the whole body, then back (monitor row {eh:0} → {EditorRow.ActualHeight:0} px)");

            // workspaces
            UseWorkspace("Audio"); await Settle();
            Check(sideView == "Mixer" && Shows(MixerView) && Inside(MixerView, LeftHost) && Inside(InspectorView, RightHost) && Math.Abs(SideCol.ActualWidth - 340) < 2,
                  $"workspace Audio: the mixer in the left side bar ({SideCol.ActualWidth:0} px), Effect Controls back on the right");
            UseWorkspace("Second screen"); await Settle(800);
            Check(DockOf("Program")!.Zone == "Float" && DockOf("Program")!.Win is { IsVisible: true } && await Frame(1.5), $"workspace Second screen: the monitor in its own window ({Screens().Count} screen(s))");
            MoveDock("Effects", "Bottom"); MoveDock("Titles", "Right"); await Settle();
            S.Workspaces["Dock test"] = CaptureLayout();
            var json = JsonSerializer.Serialize(S);
            var back = JsonSerializer.Deserialize<AppSettings>(json)!;
            UseWorkspace("Editing"); await Settle(600);
            Check(docks.All(x => x.Zone == x.Home) && EditorCard.Child == EditorGrid && docks.All(x => x.Win == null), "workspace Editing: every panel home, no windows left");
            UseWorkspace("Dock test"); await Settle(800);
            Check(DockOf("Effects")!.Zone == "Bottom" && DockOf("Titles")!.Zone == "Right" && DockOf("Program")!.Zone == "Float" && back.Workspaces["Dock test"].Zones["Titles"] == "Right",
                  "a saved workspace brings its layout back (and survives the settings JSON)");
            S.Workspaces.Remove("Dock test");
            UseWorkspace("Editing"); await Settle(600);
            var after = File.Exists(settingsFile) ? File.GetLastWriteTimeUtc(settingsFile) : DateTime.MinValue;
            Check(after == stamp, "the user's settings.json was not written by this run");
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(outFile, log);
        Close();
    }
}
