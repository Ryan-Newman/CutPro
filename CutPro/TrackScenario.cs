using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// --trackui out.txt: more tracks through the real window — adding tracks makes the timeline taller than its panel (the vertical scroll bar
/// shows, scrolling moves the rows), the preview's canvas tree, the mixer strips and the source monitor follow the layout, the band modes
/// move with their tracks, a renamed track shows its name in the mixer, a locked track's clip can't be clicked, track heights change the rows,
/// the program plays with 11 video tracks of pictures (no freezes), and undo puts the 3 + 2 layout back everywhere.
/// </summary>
public partial class MainWindow
{
    public async void TrackScenario(string outFile)
    {
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        Alert.Auto = (title, msg, _) => { log.Add($"  ALERT {title}: {msg.Replace('\n', ' ')}"); return 0; };
        async Task Settle(int ms = 350) { UpdateLayout(); await Task.Delay(ms); UpdateLayout(); }
        int PvCanvases() => typeof(Preview).GetField("trackCv", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(Pv) is Canvas[] a ? a.Length : -1;
        int AudioStrips() => strips.Count(s => s.Track >= 0 && s.Track < SubBase);
        try
        {
            var d = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cutpro_test");
            string fa = System.IO.Path.Combine(d, "a.mp4"), fb = System.IO.Path.Combine(d, "b.mp4");
            await ImportAsync(new[] { fa, fb });
            var mA = ed.Media.First(m => m.Path == fa); var mB = ed.Media.First(m => m.Path == fb);
            ed.AddMedia(mA, null, 0); ed.AddMedia(mB, null, ed.End);
            ShowSideView("Mixer");
            await Settle(800);
            Check(Tracks.Video == 3 && Tracks.Audio == 2 && PvCanvases() == 3 && AudioStrips() == 2 && (VScroll.Visibility == Visibility.Visible) == (Tl.ContentHeight > Tl.TrackViewport),
                  $"start: 3 + 2, the preview has 3 track canvases, the mixer 2 audio strips; the scroll bar shows when the rows ({Tl.ContentHeight:0} px) don't fit ({Tl.TrackViewport:0} px)");

            // band mode on A2: it must follow A2 when tracks are added above it
            Tl.ShowAuto(Tracks.A(2), TimelineView.AutoShow.TrackVolume);
            // eight video tracks on top, four audio tracks after A2
            bool added = ed.AddTracks(8, 0, 4, Tracks.Count);
            await Settle(500);
            Check(added && Tracks.Video == 11 && Tracks.Audio == 6 && PvCanvases() == 11 && AudioStrips() == 6, $"Add 8 video + 4 audio tracks: 11 + 6, 11 track canvases, {AudioStrips()} audio strips in the mixer");
            Check(VScroll.Visibility == Visibility.Visible && Tl.ContentHeight > Tl.TrackViewport && Math.Abs(Tl.Margin.Right - VScroll.Width) < 0.5,
                  $"the tracks ({Tl.ContentHeight:0} px) are taller than the panel ({Tl.TrackViewport:0} px): the vertical scroll bar shows, the timeline makes room for it");
            Check(Tl.BandOf(Tracks.A(2)) == TimelineView.AutoShow.TrackVolume && Tl.BandOf(Tracks.A(1)) == TimelineView.AutoShow.ClipVolume, "A2's band mode (track volume) moved with A2");
            // scrolling: V1's clip is below the panel at the top, in view once scrolled down
            var v1clip = ed.Clips.First(c => c.Kind == ClipKind.Video && c.MediaId == mA.Id);
            double yTop = Tl.RowY(v1clip.Track);
            Tl.ScrollY = Tl.ContentHeight;                                          // (clamped to the end)
            await Settle();
            double yScrolled = Tl.RowY(v1clip.Track);
            Check(Tl.ScrollY > 0 && Math.Abs(yTop - yScrolled - Tl.ScrollY) < 0.5 && Math.Abs(VScroll.Value - Tl.ScrollY) < 0.5 && Tl.RowY(Tracks.Count - 1) + TimelineView.RowHt(Tracks.Count - 1) <= Tl.ActualHeight,
                  $"scrolled to the end ({Tl.ScrollY:0} px): the rows move up by that much, the scroll bar follows, the last audio track is in view");
            Tl.ShowRow(0); await Settle();
            Check(Tl.ScrollY == 0, "showing the highest track scrolls back to the top");
            // heights: a taller V1 makes the content taller by the difference
            double h0 = Tl.ContentHeight;
            ed.SetTrackHeight(Tracks.V1, 120); Tl.InvalidateVisual(); UpdateVScroll(); await Settle();
            Check(Math.Abs(Tl.ContentHeight - h0 - (120 - Tracks.DefaultHeight)) < 0.5 && Math.Abs(TimelineView.RowHt(Tracks.V1) - 120) < 1e-9, $"V1 at 120 px: the rows grow by {Tl.ContentHeight - h0:0} px");
            // rename: the mixer strip shows the name
            ed.RenameTrack(Tracks.A(3), "Music"); await Settle();
            bool named = MixerStrips.Children.OfType<FrameworkElement>().SelectMany(s => FindVisualChildren<TextBlock>(s)).Any(t => t.Text == "Music");
            Check(named && Tracks.Name(Tracks.A(3)) == "Music", "rename A3 “Music”: its mixer strip says so");
            // lock: clicking V1's clip doesn't select it
            ed.SetLocked(v1clip.Track, true); ed.ClearSelection();
            Tl.ShowRow(v1clip.Track); await Settle();
            Check(Tl.ClipRect(v1clip) is { } vr && vr.Top >= TimelineView.RulerH && vr.Bottom <= Tl.ActualHeight, "showing V1 scrolls it into view");
            ed.Select(v1clip);
            Check(!ed.IsSel(v1clip), "a locked V1: its clip can't be selected");
            ed.SetLocked(v1clip.Track, false); ed.Select(v1clip);
            Check(ed.IsSel(v1clip), "unlocked: it can");
            ed.ClearSelection();

            // the source monitor after the layout changed: its clips sit on the new V1 / A1, Insert lands there
            OpenSource(mB); await Settle(600);
            SourceTick();
            Check(srcEd!.Clips.Any(c => c.Track == Tracks.V1) && srcEd.Clips.All(c => c.Track == Tracks.V1 || c.Track == Tracks.A1) && srcEd.Seq.TrackCount == Tracks.Count,
                  "the source monitor's clip is on V1 / A1 of the 11 + 6 layout");
            ed.Seek(1); var ins = ed.PlaceSource(mB, 1, SrcVRow, SrcARow, insert: false);
            Check(ins.Count >= 1 && ins.All(c => c.Track == Tracks.V1 || c.Track == Tracks.A1), "Overwrite from the source monitor lands on V1 / A1");
            ed.Undo();
            ShowDock("Inspector");

            // pictures on every video track, then play: the program keeps up with 11 video tracks
            var before = System.Text.Json.JsonSerializer.Serialize(ed.Clips);
            ed.Edit(() =>
            {
                for (int vn = 2; vn <= 11; vn++)
                    ed.Clips.Add(new Clip { MediaId = (vn % 2 == 0 ? mA : mB).Id, Kind = ClipKind.Video, Track = Tracks.V(vn), Start = 0, Dur = 4, In = 0.1 * vn % 2, NoAudio = true,
                                            Scale = 22, X = -40 + (vn - 2) % 5 * 20, Y = vn < 7 ? -25 : 25 });
            });
            ed.Seek(0); Play();
            // (start-up — every decoder's first frame before the clock starts — apart from the gaps once it runs)
            var sw = Stopwatch.StartNew(); double last = ed.Time, maxGap = 0; long lastMove = -1, startMs = -1;
            while (sw.ElapsedMilliseconds < 3000)
            {
                await Task.Delay(16);
                if (ed.Time == last) continue;
                long now = sw.ElapsedMilliseconds;
                if (startMs < 0) startMs = now; else maxGap = Math.Max(maxGap, now - lastMove);
                last = ed.Time; lastMove = now;
            }
            double played = ed.Time; Pause();
            Check(startMs is >= 0 and < 1600 && played > 1.5 && maxGap < 150,
                  $"plays with 10 picture-in-picture clips on V2 … V11: started after {startMs} ms, {played:0.00} s played, the playhead never stood still more than {maxGap:0} ms once running");
            ed.Seek(1.0); await Settle(1200);
            Check(Pv.Settled(ed.Time), "paused at 1 s: every track's frame on screen");

            // undo back to the start: the layout everywhere
            ed.Undo();                                                               // (the clips)
            while (Tracks.Video != 3 && Tracks.Count > 5) ed.Undo();                 // (heights / locks aren't steps; the rename and the tracks are)
            await Settle(500);
            Check(Tracks.Video == 3 && Tracks.Audio == 2 && PvCanvases() == 3 && AudioStrips() == 2 && (VScroll.Visibility == Visibility.Visible) == (Tl.ContentHeight > Tl.TrackViewport) && ed.Clips.Count == 4,
                  "undo: 3 + 2 again — 3 canvases, 2 strips, the scroll bar as the rows need it, the four clips");
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(outFile, log);
        Application.Current.Shutdown(fails == 0 ? 0 : 1);
    }
}
