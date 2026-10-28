using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>
/// --featureui out.txt: the editing conveniences through the real window — keys (M, Shift+M, Ctrl+C / Ctrl+V, Ctrl+G, \, =, Shift+;) through the
/// window's key handler, a changed shortcut working at once, the Markers / Edit menus (gesture text follows the keymap), the History panel
/// (its rows, clicking a row goes back there), Zoom to Sequence + the logarithmic zoom slider, Smooth / Page scrolling while playing (and what
/// it costs: the frame gaps), Paste Attributes and Properties dialogs (auto-answered), Reveal in Project, the clip menu's extra items, German
/// menus and panel headings (and back), a larger interface, and accessibility names (the timeline's peer, every icon-only button named).
/// </summary>
public partial class MainWindow
{
    public async void FeatureScenario(string outFile)
    {
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        Alert.Auto = (title, msg, b) => { log.Add($"  ALERT {title}: {msg.Replace('\n', ' ')}"); return title == "Properties" ? b.Count - 1 : 0; };   // (Properties: Close, not Replace Footage)
        async Task Settle(int ms = 300) { UpdateLayout(); await Task.Delay(ms); UpdateLayout(); }
        bool Key(Key k, ModifierKeys mods = ModifierKeys.None) => HandleKey(k, mods);
        List<string> MenuItems(string tag)
        {
            var btn = FindVisualChildren<Button>(RootGrid).First(b => (b.Tag as string) == tag);
            Menu_Click(btn, new RoutedEventArgs());
            var items = lastMenu!.Items.OfType<MenuItem>().Select(m => $"{m.Header}|{m.InputGestureText}").ToList();
            lastMenu.IsOpen = false;
            return items;
        }
        AppSettings.Current.Shortcuts.Clear(); Keymap.ResetAll(); SetLanguage("en");   // (defaults, whatever is saved — read-only run)
        try
        {
            var d = Path.Combine(Path.GetTempPath(), "cutpro_test");
            await ImportAsync(new[] { Path.Combine(d, "a.mp4"), Path.Combine(d, "b.mp4") });
            var mA = ed.Media[0]; var mB = ed.Media[1];
            ed.AddMedia(mA, null, 0); ed.AddMedia(mB, null, ed.End);
            ShowSideView("Project");
            await Settle(800);

            // ---------- markers by key ----------
            ed.Seek(1); Check(Key(System.Windows.Input.Key.M) && ed.Markers.Count == 1 && Math.Abs(ed.Markers[0].T - 1) < 1e-9, "M: a marker at the playhead");
            ed.Seek(3); Key(System.Windows.Input.Key.M);
            ed.Seek(0); Key(System.Windows.Input.Key.M, ModifierKeys.Shift);
            Check(Math.Abs(ed.Time - 1) < 1e-9, "Shift+M: to the next marker");
            Key(System.Windows.Input.Key.M, ModifierKeys.Shift); Key(System.Windows.Input.Key.M, ModifierKeys.Control | ModifierKeys.Shift);
            Check(Math.Abs(ed.Time - 1) < 1e-9, "Shift+M then Ctrl+Shift+M: next, previous");
            Tl.InvalidateVisual(); await Settle();
            var markersMenu = MenuItems("Marker");
            Check(markersMenu.Any(m => m == "Add Marker|M") && markersMenu.Any(m => m.StartsWith("Go to Next Marker|Shift+M")), $"the Markers menu: Add Marker (M), Go to Next Marker (Shift+M) — {markersMenu.Count} items");
            // a changed shortcut works at once, and the menu shows it
            Keymap.Set("marker.add", new[] { "Ctrl+Alt+K" });
            ed.Seek(5);
            bool plainM = Key(System.Windows.Input.Key.M);
            Key(System.Windows.Input.Key.K, ModifierKeys.Control | ModifierKeys.Alt);
            Check(!plainM && ed.Markers.Count == 3 && MenuItems("Marker").Any(m => m == "Add Marker|Ctrl+Alt+K"), "Add Marker on Ctrl+Alt+K: M does nothing now, Ctrl+Alt+K adds one, the menu says Ctrl+Alt+K");
            Keymap.ResetAll();

            // ---------- clipboard / groups by key ----------
            var v0 = ed.Clips.First(c => c.Kind == ClipKind.Video && c.Start < 0.01);
            ed.Select(v0);
            Key(System.Windows.Input.Key.C, ModifierKeys.Control);
            ed.Seek(ed.End); int before = ed.Clips.Count;
            Key(System.Windows.Input.Key.V, ModifierKeys.Control);
            Check(ed.Clips.Count == before + 2 && Editor.CanPaste, "Ctrl+C, Ctrl+V at the end: the clip and its sound pasted");
            var edit = MenuItems("Edit");
            Check(edit.Any(m => m.StartsWith("Undo Paste|Ctrl+Z")) && edit.Any(m => m == "Paste Attributes…|Ctrl+Alt+V"), "the Edit menu: “Undo Paste”, Paste Attributes… (Ctrl+Alt+V)");
            var vs = ed.Clips.Where(c => c.Kind == ClipKind.Video).Take(2).ToList();
            ed.SetSelection(vs);
            Key(System.Windows.Input.Key.G, ModifierKeys.Control);
            ed.Select(vs[0]);
            Check(vs.All(ed.IsSel), "Ctrl+G groups them: clicking one selects both");
            Key(System.Windows.Input.Key.G, ModifierKeys.Control | ModifierKeys.Shift);
            // gaps: delete the middle clip pair (a gap in the sequence), Shift+; finds it
            var mid = ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).ElementAt(1);
            double gapAt = mid.Start;
            ed.Select(mid); ed.DeleteSelection(false);
            ed.Seek(0); Key(System.Windows.Input.Key.OemSemicolon, ModifierKeys.Shift);
            Check(Math.Abs(ed.Time - gapAt) < 1e-6, $"Shift+;: the playhead goes to the gap at {gapAt:0.##} s");

            // ---------- History panel ----------
            ShowDock("History"); await Settle(500);
            int undoN = ed.UndoLabels.Count();
            Check(Shows(HistoryView) && histList.Items.Count == undoN + 1 && histList.SelectedIndex == undoN, $"the History panel: {histList.Items.Count} rows (the start + {undoN} steps), the last one current [shows {Shows(HistoryView)} {HistoryView.ActualWidth:0}x{HistoryView.ActualHeight:0} vis {HistoryView.IsVisible} stale {histStale} zone {DockOf("History")?.Zone} right {rightActive}]");
            int clipsNow = ed.Clips.Count;
            histList.SelectedIndex = undoN - 1; await Settle();                // (one row up: before the delete)
            Check(ed.Clips.Count == clipsNow + 2 && ed.RedoLabels.First() == "Delete" && histList.Items.Count == undoN + 1 && ((ListBoxItem)histList.Items[undoN]).Opacity < 1,
                  "clicking the row above: the delete is undone, its row stays (dimmed: it can be redone)");
            histList.SelectedIndex = undoN; await Settle();
            Check(ed.Clips.Count == clipsNow, "clicking the last row again: redone");
            ShowDock("Inspector");

            // ---------- zoom ----------
            Key(System.Windows.Input.Key.Oem5);
            Check(Math.Abs(Tl.Pps * ed.End - Tl.Viewport) < Tl.Viewport * 0.06 && Tl.ScrollX == 0, $"\\ (Zoom to Sequence): the whole sequence across the timeline ({Tl.Pps * ed.End:0} of {Tl.Viewport:0} px)");
            double p0 = Tl.Pps; Key(System.Windows.Input.Key.OemPlus);
            Check(Math.Abs(Tl.Pps / p0 - 1.5) < 1e-6 && Math.Abs(ZoomSlider.Value - Math.Log2(Tl.Pps)) < 1e-6, "= zooms in 1.5×; the slider follows (log scale)");
            Tl.Pps = 0.05;
            Check(Math.Abs(Tl.Pps - 0.05) < 1e-9, "the zoom goes far out (0.05 px/s: an hour in 180 px)");
            Tl.Pps = 200;

            // ---------- scrolling while playing ----------
            async Task<(double minX, double maxX, double maxGap, double played)> PlayFor(int ms)
            {
                ed.Seek(0); Tl.ScrollX = 0; Play();
                var sw = Stopwatch.StartNew(); double minX = double.MaxValue, maxX = 0, maxGap = 0; long last = -1; double lastT = -1;
                while (sw.ElapsedMilliseconds < ms)
                {
                    await Task.Delay(15);
                    if (!clockRunning) continue;
                    if (ed.Time != lastT) { long now = sw.ElapsedMilliseconds; if (last >= 0) maxGap = Math.Max(maxGap, now - last); last = now; lastT = ed.Time; }
                    if (ed.Time * Tl.Pps > Tl.Viewport / 2 + 30) { double x = Tl.T2X(ed.Time); minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }   // (once the view can scroll)
                }
                double played = ed.Time; Pause();
                return (minX, maxX, maxGap, played);
            }
            SetPlaybackScroll("Smooth");
            var (sMin, sMax, sGap, sPlayed) = await PlayFor(3500);
            double mid2 = TimelineView.HeaderW + Tl.Viewport / 2;
            Check(sPlayed > 2.5 && Math.Abs(sMin - mid2) < 6 && Math.Abs(sMax - mid2) < 6 && sGap < 120,
                  $"Smooth: the playhead stays in the middle ({sMin:0}…{sMax:0} px, middle {mid2:0}); playhead never stood still more than {sGap:0} ms");
            SetPlaybackScroll("Page");
            var (pMin, pMax, pGap, _) = await PlayFor(3500);
            Check(pMax <= Tl.ActualWidth - 25 && pMax - pMin > 100 && pGap < 120, $"Page: the playhead moves across and the view jumps a page at the edge ({pMin:0}…{pMax:0} px of {Tl.ActualWidth:0}); max gap {pGap:0} ms");
            SetPlaybackScroll("Page");

            // ---------- dialogs, reveal, the clip menu ----------
            var vA = ed.Clips.First(c => c.Kind == ClipKind.Video && c.MediaId == mA.Id);
            ed.Edit(() => { vA.Scale = 70; vA.Grade = new ColorGrade { Exposure = 0.5 }; });
            ed.Select(vA); ed.CopySelection();
            var vB = ed.Clips.First(c => c.Kind == ClipKind.Video && c.Id != vA.Id);
            ed.Select(vB);
            PasteAttributesDialog();                                            // (Alert.Auto: Paste with the ticked boxes)
            vB = ed.Clips.First(c => c.Id == vB.Id);
            Check(vB.Scale == 70 && vB.Grade?.Exposure == 0.5, "Paste Attributes… (dialog answered Paste): scale and grade copied");
            ShowProperties(mA);
            Check(log.Any(l => l.Contains("ALERT Properties")), "Properties… opens");
            RevealInProject(vB); await Settle();
            Check(Shows(ProjectView) && BinList.SelectedItem is MediaItem sm && sm.Id == vB.MediaId, "Reveal in Project: the Project panel shows and selects the clip's media");
            var cm = new ContextMenu();
            ClipMenuExtras(cm, vB, vB.Start + 0.5);
            var extras = cm.Items.OfType<MenuItem>().Select(m => (string)m.Header).ToList();
            Check(new[] { "Add Clip Marker", "Copy", "Paste Attributes…", "Group", "Reveal in Project", "Replace / Relink Media…", "Properties…" }.All(extras.Contains) && cm.Items.OfType<MenuItem>().Any(m => (string)m.Header == "Layout (split screen / picture in picture)"),
                  $"the clip menu adds marker / clipboard / attributes / layout / group / reveal / relink / properties ({extras.Count} items)");

            // ---------- language ----------
            SetLanguage("de"); await Settle(500);
            var fileBtn = FindVisualChildren<Button>(RootGrid).First(b => (b.Tag as string) == "File");
            var editDe = MenuItems("Edit");
            var heads = FindVisualChildren<TextBlock>(RootGrid).Select(t => t.Text).ToHashSet();
            Check((string)fileBtn.Content == "Datei" && editDe.Any(m => m.StartsWith("Kopieren|")) && editDe.Any(m => m.StartsWith("Rückgängig")) && heads.Contains("PROJEKT"),
                  $"German: the menu bar (Datei), the Edit menu (Kopieren, Rückgängig …), panel headings (PROJEKT) [{fileBtn.Content} / {string.Join(", ", editDe.Take(6))} / PROJEKT {heads.Contains("PROJEKT")}]");
            SetLanguage("en"); await Settle(400);
            heads = FindVisualChildren<TextBlock>(RootGrid).Select(t => t.Text).ToHashSet();
            Check((string)fileBtn.Content == "File" && heads.Contains("PROJECT") && !heads.Contains("PROJEKT") && StatusSnapText.Text.StartsWith("Snap"), $"back to English everywhere (status bar: {StatusSnapText.Text})");

            // ---------- interface size, accessibility ----------
            SetUiScale(1.3); await Settle();
            Check(RootGrid.LayoutTransform is ScaleTransform { ScaleX: 1.3 }, "interface size 130 %: the window's layout is scaled");
            SetUiScale(1); await Settle();
            var peer = UIElementAutomationPeer.CreatePeerForElement(Tl);
            Check(peer?.GetName() == "Timeline" && peer.GetHelpText().Contains("clips"), $"screen readers: the timeline is “{peer?.GetName()}” — “{peer?.GetHelpText()}”");
            Loc.Name(this);
            var unnamed = FindVisualChildren<ButtonBase>(RootGrid).Where(b => b.IsVisible && b.Content is string s && s.Length <= 2 && s.Any(ch => ch >= '' && ch <= ''))
                                                               .Where(b => string.IsNullOrEmpty(AutomationProperties.GetName(b))).Select(b => b.Name).ToList();
            Check(unnamed.Count == 0, "every visible icon-only button has a screen-reader name" + (unnamed.Count > 0 ? ": missing " + string.Join(", ", unnamed) : ""));
            Check(string.IsNullOrEmpty(AutomationProperties.GetName(PlayBtn)) == false || PlayBtn.Content is not string, "the play button is named");
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(outFile, log);
        Application.Current.Shutdown(fails == 0 ? 0 : 1);
    }
}
