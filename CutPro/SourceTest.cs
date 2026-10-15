using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// --srctest out.txt: the source monitor through the real window — opening a clip (the frame shown = ffmpeg's frame at that time, also after a
/// step), the keyboard going to the monitor that was clicked last (I / O in the source vs the program), marks inclusive of the frame showing,
/// Insert (everything after the playhead moves later by exactly the range, cut pieces keep their source, linked V + A, playhead at the end,
/// one undo), Overwrite (only the target tracks, video only, into V2), video-only / audio-only drags, playing one monitor pauses the other,
/// Match Frame, the panel docked elsewhere / hidden, the clip leaving the project, the marks in the project file.
/// </summary>
public partial class MainWindow
{
    public async void SourceScenario(string outFile)
    {
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        Alert.Auto = (title, msg, _) => { log.Add($"  ALERT {title}: {msg.Replace('\n', ' ')}"); return 0; };
        async Task Settle(int ms = 350) { UpdateLayout(); await Task.Delay(ms); UpdateLayout(); }
        DependencyObject? Up(DependencyObject e) => LogicalTreeHelper.GetParent(e) ?? VisualTreeHelper.GetParent(e);
        bool Inside(DependencyObject e, DependencyObject host) { for (var p = e; p != null; p = Up(p)) if (p == host) return true; return false; }
        double F = 1 / TimeUtil.Fps;
        bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
        void Key(Key k, ModifierKeys mods = ModifierKeys.None)
        {   // through the window's own key handler (modifiers can't be faked: only plain keys here)
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Window_PreviewKeyDown(this, e);
        }
        async Task<bool> SrcFrame(double t)
        {
            SrcSeek(t); var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000) { await Task.Delay(50); if (srcPv!.Settled(srcEd!.Time)) { await Task.Delay(100); return true; } }
            return false;
        }
        byte[] Grab()
        {
            srcStage.Measure(new Size(Editor.W, Editor.H)); srcStage.Arrange(new Rect(0, 0, Editor.W, Editor.H)); srcStage.UpdateLayout();
            var rtb = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); rtb.Render(srcStage);
            var px = new byte[Editor.W * Editor.H * 4]; rtb.CopyPixels(px, Editor.W * 4, 0);
            return px;
        }
        try
        {
            var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
            var d = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cutpro_test");
            string fa = System.IO.Path.Combine(d, "a.mp4"), fb = System.IO.Path.Combine(d, "b.mp4");
            await ImportAsync(new[] { fa, fb });
            var mA = ed.Media.First(m => m.Path == fa); var mB = ed.Media.First(m => m.Path == fb);
            ed.AddMedia(mA, null, 0); ed.AddMedia(mB, null, ed.End);
            await Settle(800);
            Check(Inside(SourceView, RightHost) && !Shows(SourceView) && Shows(InspectorView) && RightTabs.Children.Count == 2,
                  "default layout: Source is a tab next to Effect Controls (Effect Controls in front)");

            // opening (the Project panel's double-click) — the frame shown is the media's frame at that time
            OpenSource(mA); await Settle(500);
            Check(srcItem == mA && Shows(SourceView) && srcEd!.Clips.Count == 2 && srcEd.Clips[0].LinkId == srcEd.Clips[1].LinkId && srcEd.Clips[0].NoAudio,
                  "open a clip: the Source tab comes to the front, a one-clip editor (picture + linked sound)");
            srcPv!.ResDiv = 1; srcBox.Width = Editor.W;                              // (full size for the pixel compare)
            await Settle(300);
            foreach (var t in new[] { 2.0, 2.0 + F, 4.5 })
            {
                bool settled = await SrcFrame(t);
                if (Near(t, 2.0 + F)) { SrcSeek(2.0); await SrcFrame(2.0); SrcStep(1); var sw = Stopwatch.StartNew(); while (!srcPv.Settled(srcEd.Time) && sw.ElapsedMilliseconds < 8000) await Task.Delay(50); await Task.Delay(100); }
                var (psnr, worst) = FxTest.Compare(Grab(), FxTest.Frame(ff, fa, srcEd.Time + 0.001));
                Check(settled && psnr >= 35 && Near(srcEd.Time, Math.Round(t * TimeUtil.Fps) / TimeUtil.Fps),
                      $"source frame at {srcEd.Time:0.000} s{(Near(t, 2.0 + F) ? " (one step on)" : "")} = ffmpeg's frame: PSNR {psnr:0.0} dB, worst block {worst:0.0}");
            }
            srcBox.ClearValue(WidthProperty);

            // the keyboard goes to the monitor clicked last
            srcFocus = true; SrcSeek(1.0); Key(System.Windows.Input.Key.I);
            SrcSeek(2.5); Key(System.Windows.Input.Key.O);
            var (a0, b0) = Editor.SourceRange(mA);
            Check(Near(a0, 1.0) && Near(b0, 2.5 + F) && ed.MarkIn == null && ed.MarkOut == null && srcBar.In == 1.0,
                  $"source focused: I / O mark the source ({a0:0.000}–{b0:0.000} s: Out includes the frame showing), the program's marks untouched");
            srcFocus = false; ed.Seek(3.0); Key(System.Windows.Input.Key.I);
            Check(ed.MarkIn is double pm && Near(pm, 3.0) && Near(Editor.SourceRange(mA).a, 1.0), "program focused: I marks the sequence, the source keeps its marks");
            ed.ClearMarks();

            // Insert at the program playhead, inside clip a
            double dur = Math.Round((b0 - a0) * TimeUtil.Fps) / TimeUtil.Fps;
            string before = JsonSerializer.Serialize(ed.Clips); double endBefore = ed.End;
            var aRight = ed.Clips.Where(c => c.MediaId == mA.Id).ToList();
            ed.Seek(1.0); Key(System.Windows.Input.Key.OemComma);                   // , = Insert
            var ins = ed.Clips.Where(c => Near(c.Start, 1.0) && Near(c.Dur, dur)).ToList();
            var pieces = ed.Clips.Where(c => c.MediaId == mA.Id && Near(c.Start, 1.0 + dur)).ToList();
            Check(ins.Count == 2 && ins.All(c => Near(c.In, 1.0) && c.MediaId == mA.Id) && ins[0].LinkId != null && ins[0].LinkId == ins[1].LinkId
                  && ins.Select(c => c.Track).OrderBy(x => x).SequenceEqual(new[] { 2, 3 }) && ed.Clips.Count(c => c.LinkId == ins[0].LinkId) == 2,
                  $"Insert (,): the source range at the playhead as linked V1 + A1 clips ({dur:0.000} s from 1.000 s of the source)");
            Check(Near(ed.End, endBefore + dur) && pieces.Count == 2 && pieces.All(c => Near(c.In, 1.0)) && Near(ed.Time, 1.0 + dur)
                  && ed.Clips.Where(c => c.MediaId == mB.Id).All(c => Near(c.Start, mA.Duration + dur)),
                  $"Insert: everything from the playhead moves {dur:0.000} s later (cut pieces keep their source time), the playhead at the end of the new clip");
            // Match Frame on the moved piece: its source frame
            ed.Seek(1.0 + dur + 0.5); srcFocus = false; Key(System.Windows.Input.Key.F);
            Check(srcItem == mA && Near(srcEd.Time, Math.Round(1.5 * TimeUtil.Fps) / TimeUtil.Fps), $"Match Frame (F): the clip under the playhead opens at its source frame ({srcEd.Time:0.000} s)");
            ed.Undo();
            Check(JsonSerializer.Serialize(ed.Clips) == before, "Insert: one undo puts every clip back");

            // Overwrite, video only: only V1 changes
            srcA.IsChecked = false;
            var a1Before = JsonSerializer.Serialize(ed.Clips.Where(c => c.Track == 3).OrderBy(c => c.Start));
            ed.Seek(0.5); SourceEdit(false);
            var v1 = ed.Clips.Where(c => c.Track == 2).OrderBy(c => c.Start).ToList();
            Check(Near(ed.End, endBefore) && JsonSerializer.Serialize(ed.Clips.Where(c => c.Track == 3).OrderBy(c => c.Start)) == a1Before
                  && v1.Any(c => Near(c.Start, 0.5) && Near(c.Dur, dur) && Near(c.In, 1.0) && c.NoAudio && c.LinkId == null)
                  && !v1.Any(c => c.Start < 0.5 + dur - 1e-6 && c.End > 0.5 + 1e-6 && !Near(c.Start, 0.5))
                  && v1.Any(c => Near(c.Start, 0.5 + dur) && Near(c.In, 0.5 + dur)),
                  "Overwrite (.), video only: V1 replaced over the range (the rest of the clip keeps its source time), A1 and the length unchanged");
            ed.Undo();
            Check(JsonSerializer.Serialize(ed.Clips) == before, "Overwrite: one undo");
            // into V2 (the V toggle's track)
            srcVTrack = 1; SrcPatchLabels(); ed.Seek(2.0); SourceEdit(false);
            Check(ed.Clips.Any(c => c.Track == 1 && Near(c.Start, 2.0) && Near(c.Dur, dur)) && JsonSerializer.Serialize(ed.Clips.Where(c => c.Track != 1)) == JsonSerializer.Serialize(JsonSerializer.Deserialize<List<Clip>>(before)!.Where(c => c.Track != 1)),
                  "Overwrite into V2 (right-click V): only V2 gets the clip");
            ed.Undo(); srcVTrack = 2; SrcPatchLabels(); srcA.IsChecked = true;

            // drags: video only / audio only (what the timeline's drop does with the source monitor's parts)
            int n0 = ed.Clips.Count; double tEnd = ed.End + 1;
            ed.AddMedia(mA, 1, tEnd, video: true, audio: false);
            var vo = ed.Clips.Last();
            ed.AddMedia(mA, 4, tEnd, video: false, audio: true);
            var ao = ed.Clips.Last();
            Check(ed.Clips.Count == n0 + 2 && vo.Kind == ClipKind.Video && vo.NoAudio && vo.Track == 1 && Near(vo.In, 1.0) && Near(vo.Dur, dur)
                  && ao.Kind == ClipKind.Audio && ao.Track == 4 && Near(ao.In, 1.0) && vo.LinkId == null && ao.LinkId == null,
                  "drag video only / audio only: one clip each, the source range, not linked");
            ed.Undo(); ed.Undo();

            // playing: one monitor at a time
            SrcSeek(0); SrcPlay();
            var sw2 = Stopwatch.StartNew(); while (sw2.ElapsedMilliseconds < 1600) await Task.Delay(50);
            double heard = srcEd.Time;
            Check(srcPlaying && heard > 0.6 && !playing, $"source plays (its own clock: {heard:0.00} s after 1.6 s), the program stays paused");
            Play(); await Task.Delay(300);
            Check(playing && !srcPlaying, "playing the program pauses the source");
            Pause(); await Settle();
            SrcPlay(); await Task.Delay(300);
            ShowDock("Inspector"); await Settle(200); SourceTick();
            Check(!srcPlaying && !Shows(SourceView), "hiding the Source tab stops it");

            // elsewhere: the left side bar (an activity icon), its own window — still plays
            MoveDock("Source", "Left"); await Settle();
            Check(Inside(SourceView, LeftHost) && ActSource.Visibility == Visibility.Visible && Shows(SourceView), "Source → left side bar: an activity icon, it shows");
            MoveDock("Source", "Float"); await Settle(600);
            SrcSeek(0); SrcPlay(); await Task.Delay(1200);
            Check(DockOf("Source")!.Win != null && Shows(SourceView) && srcPlaying && srcEd.Time > 0.3, $"Source in its own window: plays ({srcEd.Time:0.00} s)");
            SrcPause();
            MoveDock("Source", "Right"); await Settle();
            Check(Inside(SourceView, RightHost) && DockOf("Source")!.Win == null, "back next to Effect Controls");

            // the marks go into the project file; a clip leaving the project closes the monitor
            var copy = JsonSerializer.Deserialize<MediaItem>(JsonSerializer.Serialize(mA))!;
            Check(copy.SrcIn == mA.SrcIn && copy.SrcOut == mA.SrcOut && copy.SrcOut != null, "source marks saved with the media item (project file)");
            OpenSource(mB); await Settle();
            ed.Clips.RemoveAll(c => c.MediaId == mB.Id); ed.Media.Remove(mB); ed.RaiseChanged(); await Settle();
            Check(srcItem == null && srcPick.Items.Count == 1, "the open clip leaves the project: the monitor closes, the recent list forgets it");
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(outFile, log);
        Application.Current.Shutdown(fails == 0 ? 0 : 1);
    }
}
