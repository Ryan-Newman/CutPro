using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace CutPro;

/// <summary>
/// --highui out.txt: the "high" features through the real window — the Project panel (a new bin from a selection, the bin's folder item,
/// opening it, the crumb, search across bins, the filter, list view, number keys rate), the Transcript panel (words of a transcribed clip,
/// a click seeks, Shift+click selects, Delete ripples, one undo; select fillers), the Clip menu (its items and their state), effect presets
/// in the Effects panel (applied by the preset drop), auto-save (the recovery copy written by the timer's tick, the title's unsaved dot,
/// "save changes?" only when something changed), drops (overwrite / insert through the editor the timeline calls), EDL / OTIO from the window.
/// </summary>
public partial class MainWindow
{
    public async void HighScenario(string outFile)
    {
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        Alert.Auto = (title, msg, b) => { log.Add($"  ALERT {title}: {msg.Replace('\n', ' ')}"); return 0; };
        async Task Settle(int ms = 300) { UpdateLayout(); await Task.Delay(ms); UpdateLayout(); }
        List<MediaItem> Listed() => BinList.Items.Cast<MediaItem>().ToList();
        try
        {
            var d = Path.Combine(Path.GetTempPath(), "cutpro_test");
            await ImportAsync(new[] { Path.Combine(d, "a.mp4"), Path.Combine(d, "b.mp4") });
            var mA = ed.Media[0]; var mB = ed.Media[1];
            ed.AddMedia(mA, null, 0); ed.AddMedia(mB, null, ed.End);
            ShowSideView("Project");
            await Settle(800);

            // ---------- Project panel: bins, search, filter, list view, ratings ----------
            BinList.SelectedItems.Clear(); BinList.SelectedItems.Add(Listed().First(m => m.Id == mB.Id));
            NewBinHere();                                                           // (renamed by the auto-answered dialog: the name stays)
            await Settle();
            var folder = Listed().FirstOrDefault(m => m.Folder != null);
            Check(folder != null && folder.Name == "New Bin" && mB.Bin == "New Bin" && !Listed().Any(m => m.Id == mB.Id) && Listed().Any(m => m.Id == mA.Id),
                  $"New Bin with a clip selected: the bin shows as a folder and the clip went into it ({Listed().Count} items at the top)");
            OpenBin(folder!.Folder!); await Settle();
            Check(Listed().Count == 1 && Listed()[0].Id == mB.Id && binCrumbRow!.Visibility == Visibility.Visible && binCrumb!.Text.Contains("New Bin"), $"inside the bin: its clip, the crumb “{binCrumb!.Text}”");
            BinList.Focus(); BinList.SelectedItem = Listed()[0];
            BinRatingKey(Key.D4);
            Check(mB.Rating == 4 && Listed()[0].Info.Contains("★★★★"), "the 4 key rates the selected clip ★★★★ (shown in its line)");
            OpenBin(""); await Settle();
            binSearch!.Text = "b.mp4"; await Settle();
            Check(Listed().Count == 1 && Listed()[0].Id == mB.Id && binCrumbRow!.Visibility == Visibility.Collapsed, "the search finds a clip inside a bin from the top");
            binSearch.Text = ""; binFilterBox!.SelectedItem = "Rated"; await Settle();
            Check(Listed().All(m => m.Folder != null || m.Rating > 0), "the Rated filter");
            binFilterBox.SelectedIndex = 0;
            binViewBtn!.IsChecked = true; S.BinListView = true; ApplyBinView(); await Settle();
            Check(BinList.ItemTemplate == BinList.Resources["BinRowTemplate"], "list view: one line per item");
            S.BinListView = false; ApplyBinView();

            // ---------- Transcript panel ----------
            mA.Words = new() { new("In", 0.5, 0.7), new("the", 0.75, 0.9), new("um", 1.1, 1.3), new("beginning", 1.5, 2.1), new("was", 2.2, 2.4), new("the", 2.45, 2.55), new("word.", 2.6, 3.1) };
            ShowDock("Transcript"); trStale = true; RefreshTranscript(); await Settle(500);
            Check(Shows(TranscriptView) && trBlocks.Count == 7 && trBlocks[3].Text == "beginning", $"the Transcript panel shows the clip's {trBlocks.Count} words");
            TranscriptClick(3, false, false);
            Check(Math.Abs(ed.Time - 1.5) < 1.0 / 30 + 1e-9, $"a click on “beginning” moves the playhead there ({ed.Time:0.00} s)");
            TranscriptClick(2, false, false); TranscriptClick(2, true, false);
            double end0 = ed.End;
            DeleteTranscriptSelection(); await Settle();
            var words = Transcript.Of(ed).Select(w => w.W).ToList();
            Check(words.Count == 6 && !words.Contains("um") && ed.End < end0 - 0.15 && ed.UndoLabels.First() == "Delete Word", $"Delete removes “um” (the sequence {end0 - ed.End:0.00} s shorter, one undo step)");
            ed.Undo(); await Settle();
            Check(Transcript.Of(ed).Count == 7, "undo brings it back");
            trStale = true; RefreshTranscript();
            var fill = FindVisualChildren<Button>(TranscriptView).First(b => (b.Content as string) == "Select fillers");
            fill.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(trSel.Count == 1 && trWords[trSel.First()].W == "um", "Select fillers picks “um”");
            trSel.Clear(); mA.Words = null; ShowSideView("Project");

            // ---------- Clip menu, presets ----------
            var clipBtn = FindVisualChildren<Button>(RootGrid).First(b => (b.Tag as string) == "Clip");
            ed.Select(ed.Clips.First(c => c.Kind == ClipKind.Video));
            Menu_Click(clipBtn, new RoutedEventArgs());
            var items = lastMenu!.Items.OfType<MenuItem>().ToDictionary(m => (string)m.Header, m => m.IsEnabled);
            lastMenu.IsOpen = false;
            Check(items.GetValueOrDefault("Scene Edit Detection…") && items.GetValueOrDefault("Colour Match…") && items.GetValueOrDefault("Save Preset…") && items.ContainsKey("Apply Preset")
                  && items.ContainsKey("Cut to the Beat") && !items["Cut to the Beat"], $"the Clip menu ({items.Count} items): the tools for a selected video clip; Cut to the Beat waits for beat markers");
            ShowSideView("Effects"); BuildEffectsList(); await Settle();
            var presetRow = FindVisualChildren<TextBlock>(EffectsView).Any(t => t.Text == "Black & white");
            var v = ed.Clips.First(c => c.Kind == ClipKind.Video);
            ApplyPreset(EffectPresets.Get("Black & white")!, v);
            v = ed.Clips.First(c => c.Id == v.Id);
            Check(presetRow && v.Grade?.Saturation == 0, "the Effects panel lists presets; dropping “Black & white” on a clip greys it");
            ShowSideView("Project");

            // ---------- auto-save, unsaved changes ----------
            AutoSave.DirOverride = Path.Combine(Path.GetTempPath(), "cutpro_highui_autosave");
            if (Directory.Exists(AutoSave.DirOverride)) Directory.Delete(AutoSave.DirOverride, true);
            Check(Dirty && Title.EndsWith(" •") == false, "edits make the project unsaved (an automated run shows no dot)");
            AutoTick(DateTime.Now.AddMinutes(10));
            var rec = Path.Combine(AutoSave.Dir, $"recovery-{AutoSave.Pid}.cutpro");
            var vers = AutoSave.Versions(AutoSave.Key(projectPath));
            Check(File.Exists(rec) && File.ReadAllText(rec).Contains(mA.Id) && vers.Count == 1, "the auto-save tick wrote the recovery copy and a version");
            AutoTick(DateTime.Now.AddMinutes(11));
            Check(AutoSave.Versions(AutoSave.Key(projectPath)).Count == 1, "nothing new to save: no second version a minute later");
            AutoSave.EndSession(); AutoSave.DirOverride = null;
            Check(ConfirmDiscard(), "an automated run never asks to save");

            // ---------- drops (the timeline's call) ----------
            int before = ed.Clips.Count;
            var made = ed.DropMedia(mB, Tracks.V1, 1, true, true, false);
            Check(made.Count == 2 && ed.Clips.Count(c => c.Track == Tracks.V1 && c.Start < 1 + 1e-9 && c.End > 1 + 1e-9 && c.MediaId == mA.Id) == 0 && ed.Clips.Count >= before,
                  "a drop at 1 s overwrites V1 and A1 there");
            ed.Undo();
            Check(ed.Clips.Count == before, "undo");

            // ---------- EDL / OTIO ----------
            var edl = Interchange.Edl(ed, ed.Seq.Name); var otio = Interchange.Otio(ed, ed.Seq.Name);
            Check(edl.Contains("001  ") && edl.Contains("FROM CLIP NAME: a.mp4") && otio.Contains("\"Timeline.1\"") && otio.Contains("a.mp4"), "EDL and OTIO of the open sequence");
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(outFile, log);
        Application.Current.Shutdown(fails == 0 ? 0 : 1);
    }
}
