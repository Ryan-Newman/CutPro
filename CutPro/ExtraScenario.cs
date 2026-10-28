using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// The "extra high" features through the real window: --extraui out.txt (scenario) and the extra --uishot shots. The scenario: tool shortcuts
/// (B N R Y U / V / C) and a rolling edit through the timeline's drag with the monitor's two-up view; J / K / L shuttle (2× forward, reverse,
/// stop on a frame) on the audio clock; audio scrubbing; the opacity band toggle; a generated colour matte from File ▸ New Item; an imported
/// OTIO sequence; the render queue panel rendering a job while the timeline changes; proxies on / off.
/// </summary>
public partial class MainWindow
{
    public async void ExtraScenario(string outFile)
    {
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        Alert.Auto = (title, msg, b) => { log.Add($"  ALERT {title}: {msg.Replace('\n', ' ')}"); return 0; };
        async Task Settle(int ms = 300) { UpdateLayout(); await Task.Delay(ms); UpdateLayout(); }
        try
        {
            var d = Path.Combine(Path.GetTempPath(), "cutpro_test");
            await ImportAsync(new[] { Path.Combine(d, "a.mp4"), Path.Combine(d, "b.mp4") });
            var mA = ed.Media[0]; var mB = ed.Media[1];
            ed.AddMedia(mA, null, 0); ed.AddMedia(mB, null, ed.End);
            await Settle(1500);

            // ---------- tools ----------
            var keys = new Dictionary<Key, TrimTool> { [Key.B] = TrimTool.Ripple, [Key.N] = TrimTool.Roll, [Key.R] = TrimTool.Rate, [Key.Y] = TrimTool.Slip, [Key.U] = TrimTool.Slide, [Key.C] = TrimTool.Razor, [Key.V] = TrimTool.Select };
            var got = new List<string>();
            foreach (var (k, t) in keys) { HandleKey(k, ModifierKeys.None); got.Add(Tl.Tool == t ? "ok" : $"{k}→{Tl.Tool}"); }
            Check(got.All(x => x == "ok"), "B N R Y U C V pick Ripple, Rolling, Rate Stretch, Slip, Slide, Razor, Selection — " + string.Join(" ", got));
            SetTool(TrimTool.Roll);
            Check(ToolTrim.IsChecked == true && TrimLabel.Text == "Rolling", $"the toolbar's trim button shows the tool (“{TrimLabel.Text}”)");
            var v1 = ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).ToList();
            ed.Edit(() =>
            {   // (both clips use their whole media: give the edit a second of handles each side)
                foreach (var x in ed.Linked(v1[0]).ToList()) x.Dur -= 1;
                foreach (var x in ed.Linked(v1[1]).ToList()) { x.Start -= 1; x.In += 1; x.Dur -= 1; }
            });
            v1 = ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).ToList();
            double cut = v1[0].End, end0 = ed.End;
            bool began = Tl.BeginToolDrag(TrimTool.Roll, v1[0], false);
            Tl.TrimTo(-0.5);
            bool twoUp = trimView is { Visibility: Visibility.Visible };
            Tl.EndToolDrag(); await Settle();
            var v1b = ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).ToList();
            Check(began && twoUp && Math.Abs(v1b[0].End - (cut - 0.5)) < 1e-6 && Math.Abs(v1b[1].Start - (cut - 0.5)) < 1e-6 && Math.Abs(ed.End - end0) < 1e-6 && trimView!.Visibility == Visibility.Collapsed
                  && ed.UndoLabels.First() == "Rolling Edit", $"a rolling edit by the timeline's drag: the cut moves to {v1b[0].End:0.00} s, the end stays, the monitor shows the two-up view while dragging");
            ed.Undo(); SetTool(TrimTool.Select);

            // ---------- J K L shuttle ----------
            ed.Seek(1); await Settle(300);
            HandleKey(Key.L, ModifierKeys.None); HandleKey(Key.L, ModifierKeys.None);
            double r2 = playRate; await Task.Delay(1000);
            double t2 = ed.Time;
            HandleKey(Key.J, ModifierKeys.None);
            double r3 = playRate; await Task.Delay(700);
            double t3 = ed.Time;
            HandleKey(Key.K, ModifierKeys.None); await Settle(200);
            double parked = ed.Time;
            Check(r2 == 2 && t2 > 2.4 && t2 < 3.6 && r3 == -1 && t3 < t2 - 0.3 && !playing && Math.Abs(parked * TimeUtil.Fps - Math.Round(parked * TimeUtil.Fps)) < 1e-6,
                  $"L L = 2× ({t2 - 1:0.00} s of timeline in 1 s), J = backwards ({t2 - t3:0.00} s back in 0.7 s), K stops on a frame ({parked:0.000} s)");
            HandleKey(Key.J, ModifierKeys.None); HandleKey(Key.J, ModifierKeys.None); HandleKey(Key.J, ModifierKeys.None);
            double r4 = playRate; HandleKey(Key.K, ModifierKeys.None);
            Check(r4 == -4, $"J J J = 4× backwards ({r4}×)");
            // audio scrubbing: grains while the playhead is dragged, silence (stopped) after
            audio.Scrub(1.0); await Task.Delay(100); audio.Scrub(1.05); bool scr = audio.Scrubbing;
            audio.Stop(); Check(!audio.Ok || scr && !audio.Scrubbing, "audio scrubbing starts on a drag and stops after it");

            // ---------- opacity band ----------
            int row = Tracks.V1;
            Tl.ShowOpacity(row, true);
            Check(Tl.OpacityBand(row), "a video track's header shows its clips' opacity band (Opacity button)");
            Tl.ShowOpacity(row, false);

            // ---------- File ▸ New Item ▸ Colour Matte ----------
            int media0 = ed.Media.Count;
            await NewGenerated(GenKind.ColorMatte);
            Check(ed.Media.Count == media0 + 1 && ed.Media[^1].Gen?.Kind == GenKind.ColorMatte && ed.Media[^1].Name == "Colour Matte", "File ▸ New Item ▸ Colour Matte adds a generated picture to the Project panel");

            // ---------- import an OTIO ----------
            var otio = Path.Combine(Path.GetTempPath(), "cutpro_extraui.otio");
            File.WriteAllText(otio, Interchange.Otio(ed, "From OTIO"));
            int seqs = ed.Sequences.Count;
            await ImportSequenceAsync(otio); await Settle();
            Check(ed.Sequences.Count == seqs + 1 && ed.Seq.Name == "From OTIO" && ed.Clips.Count(c => c.Kind == ClipKind.Video) == 2,
                  $"File ▸ Import Sequence: “{ed.Seq.Name}” opens with its clips ({ed.Sequences.Count - seqs} new, {string.Join(", ", ed.Clips.GroupBy(c => c.Kind).Select(g => $"{g.Count()} {g.Key}"))})");
            OpenSequence(ed.Sequences[0]); await Settle();

            // ---------- render queue ----------
            ShowDock("Queue"); await Settle();
            var outQ = Path.Combine(Path.GetTempPath(), "cutpro_extraui_queue.mp4");
            var job = RenderQueue.Add(ed, new ExportSettings { Height = 360 }, outQ);
            ed.Edit(() => ed.Clips.RemoveAll(c => c.Kind == ClipKind.Audio));          // (editing goes on while it renders)
            await Task.Run(() => RenderQueue.WaitIdle(120000)); await Settle(500);
            bool listed = FindVisualChildren<TextBlock>(QueueView).Any(t => t.Text.StartsWith("Done"));
            Check(job.Status == RenderQueue.State.Done && File.Exists(outQ) && listed && Shows(QueueView), $"the Render Queue panel shows the job done ({job.Status}); the edit made meanwhile isn't in it");
            ed.Undo(); RenderQueue.ClearFinished(); ShowSideView("Project");

            // ---------- Clip ▸ Blur Faces (the test clips have no faces: nothing is added, nothing breaks) ----------
            if (FaceDetector.Present)
            {
                ed.Select(ed.Clips.First(c => c.Kind == ClipKind.Video));
                int masks0 = ed.Clips.Sum(c => c.Masks.Count);
                await BlurFacesDialog(); await Settle(300);
                Check(ed.Clips.Sum(c => c.Masks.Count) == masks0 && toolCts == null, "Clip ▸ Blur Faces runs through the selected clip (no faces in the test footage: no masks)");
            }
            else log.Add("SKIP Blur Faces: the face model isn't downloaded");
            // Clip ▸ Scene Edit Detection through the window (its progress is reported from a background task: it must reach the UI thread)
            ed.Select(ed.Clips.First(c => c.Kind == ClipKind.Video));
            SceneDetectDialog();
            for (int i = 0; i < 300 && toolCts != null; i++) await Task.Delay(100);
            Check(toolCts == null, "Clip ▸ Scene Edit Detection runs to the end through the window (progress on the UI thread)");

            // ---------- proxies ----------
            SetProxies(true);
            Check(Proxies.Use && ProxyBtn.IsChecked == true, "the Proxies button turns proxies on");
            SetProxies(false);
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(outFile, log);
        Application.Current.Shutdown(fails == 0 ? 0 : 1);
    }

    /// <summary>--uishot extras: the trim tool menu and a rolling edit's two-up view, the opacity band, the render queue, the HSL secondary,
    /// distort with the mesh, text animation, the New Item dialog.</summary>
    async Task ExtraShots(string path, Clip vc, Action<string> shot, Action<BitmapSource, string> save)
    {
        string P(string ext) => Path.ChangeExtension(path, ext);
        // trim tools
        ToolTrim_Click(ToolTrim, new RoutedEventArgs()); await Task.Delay(400);
        if (lastMenu != null) { var r = new RenderTargetBitmap((int)lastMenu.ActualWidth, (int)lastMenu.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); r.Render(lastMenu); save(r, P(".trimmenu.png")); lastMenu.IsOpen = false; }
        var v = ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).ToList();
        if (v.Count > 1)
        {
            SetTool(TrimTool.Roll);
            Tl.BeginToolDrag(TrimTool.Roll, v[0], false); Tl.TrimTo(-0.4);
            await Task.Delay(800); shot(P(".trim.png"));
            Tl.EndToolDrag(); ed.Undo(); SetTool(TrimTool.Select);
        }
        vc = ed.Clips.FirstOrDefault(c => c.Id == vc.Id) ?? vc;
        // opacity band
        Tl.ShowOpacity(vc.Track, true);
        Anim.SetKey(vc, AnimProp.Opacity, Anim.SrcTime(vc, vc.Start + 0.3), 100); Anim.SetKey(vc, AnimProp.Opacity, Anim.SrcTime(vc, vc.Start + 1.2), 35);
        Tl.InvalidateVisual(); await Task.Delay(500); shot(P(".opacity.png"));
        vc.Anim?.Remove(AnimProp.Opacity); Anim.Prune(vc); Tl.ShowOpacity(vc.Track, false);
        // render queue: two jobs waiting
        RenderQueue.Paused = true;
        var j1 = RenderQueue.Add(ed, ExportSettings.Presets[1], Path.Combine(Path.GetTempPath(), "cutpro_shot_q1.mp4"));
        var j2 = RenderQueue.Add(ed, ExportSettings.Presets.First(p => p.Video == "prores4444a"), Path.Combine(Path.GetTempPath(), "cutpro_shot_q2.mov"));
        ShowDock("Queue"); await Task.Delay(600); shot(P(".queue.png"));
        RenderQueue.Remove(j1); RenderQueue.Remove(j2); RenderQueue.Paused = false; ShowSideView("Project");
        ShowDock("Inspector");
        // Effect Controls sections brought into view
        async Task Section(string heading, string ext)
        {
            BuildInspector(); await Task.Delay(400);
            var tb = FindVisualChildren<TextBlock>(Inspector).FirstOrDefault(t => t.Text == heading || t.Text == heading.ToUpperInvariant());
            if (tb == null) foreach (var ex in FindVisualChildren<Expander>(Inspector)) if (ex.Header is string h && h == heading) { ex.IsExpanded = true; ex.BringIntoView(); }
            tb?.BringIntoView(new Rect(0, 0, 10, 640));                            // (the heading near the top of the panel)
            await Task.Delay(600); shot(P(ext));
        }
        ed.Select(vc);
        vc.Grade = new ColorGrade { Hsl = new HslKey { Hue = 25, HueWidth = 18, SatMin = 10, SatMax = 65, LumMin = 15, LumMax = 92, Saturation = 120, Temperature = 10 } };
        sectionOpen["Lumetri color"] = true;
        await Section("HSL Secondary", ".hsl.png");
        vc.Grade = null;
        vc.Distort = new Distort { Kind = DistortKind.Mesh, Mesh = new double[32] }; vc.Distort.Mesh[(1 * 4 + 2) * 2] = 6; vc.Distort.Mesh[(1 * 4 + 2) * 2 + 1] = -5;
        sectionOpen["Distort"] = true;
        ed.Seek(vc.Start + 0.5); meshOverlay?.InvalidateVisual();
        await Section("Distort", ".distort.png");
        vc.Distort = null;
        if (ed.Clips.FirstOrDefault(c => c.Kind == ClipKind.Text) is { } title)
        {
            title.TextFx = new TextAnim { In = TextIn.RiseWords, Out = TextIn.FadeWords };
            ed.Select(title); sectionOpen["Text animation"] = true;
            await Section("Text animation", ".textanim.png");
            title.TextFx = null;
        }
        // the New Item dialog
        Alert.Shown = async w => { await Task.Delay(900); save(ScreenGrab.Window(this), P(".newitem.png")); w.Close(); };
        var auto = Alert.Auto; Alert.Auto = null;
        GeneratorDialog(Generators.For(GenKind.Countdown), "Counting Leader");
        Alert.Auto = auto; Alert.Shown = null;
    }
}
