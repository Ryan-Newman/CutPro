using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace CutPro;

/// <summary>The window's side of the analysis tools and interchange: the Clip menu, scene edit detection, synchronize, beats, auto-ducking,
/// colour match, background removal, reframe push-in, replace / fit to fill, EDL / OTIO export.</summary>
public partial class MainWindow
{
    CancellationTokenSource? toolCts;

    void InitHigh()
    {
        Tl.PresetDropped += (name, c) => { if (EffectPresets.Get(name) is { } p) ApplyPreset(p, c); };
    }

    List<Clip> SelectedClips(Func<Clip, bool>? f = null) => ed.Clips.Where(c => ed.IsSel(c) && ed.Editable(c) && (f == null || f(c))).OrderBy(c => c.Start).ToList();
    static bool Plain(Clip c) => !c.Reverse && !c.Hold && Math.Abs(c.Speed - 100) < 1e-9 && !TimeMap.Ramped(c);

    /// <summary>A labelled slider for the tool dialogs (value shown).</summary>
    static (StackPanel row, Slider s) ToolSlider(string label, double min, double max, double v, string fmt)
    {
        var p = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var t = new TextBlock(); p.Children.Add(t);
        var s = new Slider { Minimum = min, Maximum = max, Value = v, IsSnapToTickEnabled = false };
        void Upd() => t.Text = $"{label}: {s.Value.ToString(fmt)}";
        s.ValueChanged += (_, _) => Upd(); Upd();
        p.Children.Add(s);
        return (p, s);
    }

    /// <summary>Runs a background tool with a status-bar task (one at a time; Esc / the same command again stops it).</summary>
    async Task RunTool(string what, Func<CancellationToken, Task> work)
    {
        if (toolCts != null) { toolCts.Cancel(); return; }
        toolCts = new CancellationTokenSource();
        try { SetTask(what + "…", 0); await work(toolCts.Token); }
        catch (OperationCanceledException) { SetTask(what + " stopped", 2000); }
        catch (Exception ex) { Alert.Show(this, ex.Message, what, AlertKind.Error); SetTask("", 1); }
        finally { toolCts.Dispose(); toolCts = null; }
    }

    // ---------------- Clip menu ----------------
    void ClipMenu(Action<object[]> add, Func<string, string, Action, bool, bool, MenuItem> mi)
    {
        bool sel = ed.Sel.Count > 0, vid = SelectedClips(c => c.Kind == ClipKind.Video).Count > 0, snd = SelectedClips(Mixer.HasSound).Count > 0;
        var preset = new MenuItem { Header = "Apply Preset" };
        foreach (var p in EffectPresets.All) { var pp = p; preset.Items.Add(mi(p.Name, "", () => ApplyPreset(pp), false, sel)); }
        add(new object[]
        {
            mi("Speed/Duration…", K("clip.speed"), () => SpeedDialog(), false, sel),
            mi("Replace with Clip from Project", K("clip.replace"), ReplaceFromBin, false, ed.Selected != null && BinList.SelectedItem is MediaItem { Folder: null }),
            mi("Fit to Fill (source into In–Out)", K("clip.fitFill"), FitToFill, false, srcItem != null && ed.MarkIn != null && ed.MarkOut != null), "-",
            mi("Scene Edit Detection…", K("clip.scenes"), SceneDetectDialog, false, vid),
            mi("Synchronize (by sound)", K("clip.sync"), () => _ = SyncSelected(), false, ed.Sel.Count > 1),
            mi("Detect Beats (markers)…", K("audio.beats"), BeatsDialog, false, snd),
            mi("Cut to the Beat", K("clip.beatCut"), CutToBeat, false, sel && ed.BeatTimes().Count > 1),
            mi("Auto Ducking…", K("audio.ducking"), DuckingDialog, false, snd), "-",
            mi("Auto Colour", K("color.auto"), () => { if (ed.Selected is { } c) AutoGrade(c); }, false, SelectedClips(c => c.Kind is ClipKind.Video or ClipKind.Image).Count > 0),
            mi("Colour Match…", K("color.match"), ColorMatchDialog, false, SelectedClips(c => c.Kind is ClipKind.Video or ClipKind.Image).Count > 0),
            mi("Remove Background", K("clip.removeBg"), () => _ = RemoveBackground(), false, ed.Selected is { Kind: ClipKind.Video }),
            mi("Auto Reframe with Push-in…", K("seq.reframeZoom"), ReframeZoomDialog, false, ed.Clips.Any(Editor.CoversFrame)), "-",
            mi("Save Preset…", K("clip.savePreset"), SavePresetDialog, false, ed.Selected != null),
        });
        add(new object[] { preset });
    }

    void ClipMenuHigh(ContextMenu menu, Clip c)
    {
        MenuItem Item(string text, Action a, string gesture = "", bool enabled = true)
        {
            var mi = new MenuItem { Header = text, InputGestureText = gesture, IsEnabled = enabled };
            mi.Click += (_, _) => a(); menu.Items.Add(mi); return mi;
        }
        menu.Items.Add(new Separator());
        if (c.Kind == ClipKind.Video) Item("Scene Edit Detection…", SceneDetectDialog, K("clip.scenes"));
        if (ed.Sel.Count > 1) Item("Synchronize (by sound)", () => _ = SyncSelected(), K("clip.sync"));
        if (Mixer.HasSound(c)) { Item("Detect Beats (markers)…", BeatsDialog, K("audio.beats")); Item("Auto Ducking…", DuckingDialog, K("audio.ducking")); }
        if (c.Kind is ClipKind.Video or ClipKind.Image) Item("Colour Match…", ColorMatchDialog, K("color.match"));
        if (c.Kind == ClipKind.Video) Item("Remove Background", () => _ = RemoveBackground(), K("clip.removeBg"));
        Item("Save Preset…", SavePresetDialog, K("clip.savePreset"));
    }

    /// <summary>The Project panel's extras for a media item.</summary>
    void BinMediaExtras(ContextMenu cm, MediaItem m)
    {
        void Item(string h, Action a, bool en = true) { var mi = new MenuItem { Header = h, IsEnabled = en }; mi.Click += (_, _) => a(); cm.Items.Add(mi); }
        cm.Items.Add(new Separator());
        if (m.HasAudio) Item(m.Words == null ? "Transcribe (text-based editing)" : "Transcribe Again", () => { m.Words = null; ShowDock("Transcript"); _ = TranscribeAsync(new() { m }); });
        Item("Replace Selected Clip with This", () => { BinList.SelectedItem = m; ReplaceFromBin(); }, ed.Selected != null);
        if (m.Gen != null) Item("Edit " + m.Gen.Title + "…", () => _ = EditGenerated(m));
        if (m.Type == ClipKind.Video && m.Seq == null) { var sel = SelectedBinMedia(); cm.Items.Add(ProxyMenu(sel.Contains(m) ? sel : new() { m })); }
        if (m.Type is ClipKind.Video or ClipKind.Image && m.Seq == null && m.Gen == null && !m.Offline)
        {
            var up = new MenuItem { Header = "Create Upscaled Copy" };
            foreach (int f in new[] { 2, 4 })
            {
                var mi = new MenuItem { Header = $"{f}× ({m.Width * f}×{m.Height * f})" };
                mi.Click += (_, _) => _ = UpscaleCopy(m, f);
                up.Items.Add(mi);
            }
            cm.Items.Add(up);
        }
    }

    // ---------------- replace / fit to fill ----------------
    void ReplaceFromBin()
    {
        if (ed.Selected is not { } c || BinList.SelectedItem is not MediaItem { Folder: null } m) { SetTask("Select a clip on the timeline and a media item in the Project panel", 3000); return; }
        if (!ed.ReplaceClip(c, m)) Alert.Show(this, $"“{m.Name}” can't replace this clip (another kind of media, or shorter than the clip).", "Replace with Clip");
        else SetTask($"Replaced with {m.Name} (Ctrl+Z undoes)", 3000);
    }
    void FitToFill()
    {
        if (srcItem == null) { SetTask("Open a clip in the Source monitor and mark In / Out on the timeline first", 3000); return; }
        int? vt = srcV.IsChecked == true && srcV.IsEnabled ? SrcVRow : null, at = srcA.IsChecked == true && srcA.IsEnabled ? SrcARow : null;
        SrcPause(); Pause();
        var made = ed.FitToFill(srcItem, vt, at);
        SetTask(made.Count == 0 ? "Fit to Fill needs In and Out on the timeline and a video / audio clip in the Source monitor"
                                : $"Fit to Fill at {made[0].Speed:0.#} % (Ctrl+Z undoes)", 4000);
    }

    // ---------------- scene edit detection ----------------
    void SceneDetectDialog()
    {
        var clips = SelectedClips(c => c.Kind == ClipKind.Video && ed.MediaOf(c) is { Seq: null, Offline: false });
        if (clips.Count == 0) { SetTask("Select video clips to find their shot changes", 3000); return; }
        var (sr, sens) = ToolSlider("Sensitivity", 0, 100, 50, "0");
        var (mr, min) = ToolSlider("Shortest shot (s)", 0.2, 3, 0.5, "0.0");
        var mode = new ComboBox { ItemsSource = new[] { "Cut at each shot change", "Add clip markers" }, SelectedIndex = 0, Margin = new Thickness(0, 10, 0, 0) };
        var p = new StackPanel(); p.Children.Add(mode); p.Children.Add(sr); p.Children.Add(mr);
        if (Alert.Show(this, $"Finds where the shot changes in {clips.Count} clip{(clips.Count == 1 ? "" : "s")} (ffmpeg's scene score).", "Scene Edit Detection",
                       AlertKind.Question, new[] { "Analyze", "Cancel" }, p) != 0) return;
        bool cut = mode.SelectedIndex == 0; double s = sens.Value, ms = min.Value;
        _ = RunTool("Scene edit detection", async ct =>
        {
            var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
            var plan = new List<(Clip c, List<double> t)>();
            for (int i = 0; i < clips.Count; i++)
            {
                var c = clips[i]; var m = ed.MediaOf(c)!; var (lo, hi) = TimeMap.Span(c); int k = i;
                var times = await Task.Run(() => SceneDetect.Find(ff, m, lo, hi, s, ms, ct,
                                           new Progress<double>(x => SetTask($"Scene edit detection: clip {k + 1} of {clips.Count} — {x * 100:0}%", 0))), ct);
                plan.Add((c, times));
            }
            int n = 0;
            foreach (var (c, t) in plan) n += ed.SceneEdits(c, t, cut);
            SetTask(n == 0 ? "No shot changes found (try a higher sensitivity)" : $"{n} shot change{(n == 1 ? "" : "s")} — {(cut ? "cut" : "marked")} (Ctrl+Z undoes)", 5000);
        });
    }

    // ---------------- synchronize ----------------
    async Task SyncSelected()
    {
        // units: one per link group (its clip with sound decides); the reference is the one on the lowest video track (camera A), else the earliest
        var units = SelectedClips().GroupBy(c => c.LinkId ?? c.Id).Select(g => g.OrderBy(c => Mixer.HasSound(c) ? 0 : 1).First()).ToList();
        units = units.Where(c => ed.MediaOf(c) is { Seq: null, Offline: false, HasAudio: true } && Plain(c)).ToList();
        if (units.Count < 2) { SetTask("Select two or more clips with sound (plain speed) to line them up", 3500); return; }
        var reference = SelectedClips(c => c.Kind == ClipKind.Video && units.Any(u => u.LinkId != null && u.LinkId == c.LinkId || u.Id == c.Id))
                        .OrderByDescending(c => c.Track).Select(c => units.First(u => u.LinkId != null && u.LinkId == c.LinkId || u.Id == c.Id)).FirstOrDefault() ?? units[0];
        await RunTool("Synchronize", async ct =>
        {
            var lags = new List<(Clip, double)>(); var bad = new List<string>();
            var rm = ed.MediaOf(reference)!;
            foreach (var u in units.Where(u => u != reference))
            {
                var um = ed.MediaOf(u)!;
                SetTask($"Synchronize: listening to {um.Name}…", 0);
                var r = await Task.Run(() => AudioSync.Between(rm, um), ct);
                if (r == null || r.Confidence < 8) bad.Add(um.Name); else lags.Add((u, r.Lag));
            }
            if (lags.Count == 0) { Alert.Show(this, "No common sound was found between the clips.", "Synchronize"); SetTask("", 1); return; }
            var failed = ed.SyncClips(reference, lags);
            if (failed.Count > 0) { Alert.Show(this, "There is no free track for some clips at their synced place — make room (or add a track) and try again.", "Synchronize"); SetTask("", 1); return; }
            SetTask($"Synchronized {lags.Count} clip{(lags.Count == 1 ? "" : "s")} to {rm.Name}" + (bad.Count > 0 ? $" — no match for {string.Join(", ", bad)}" : "") + " (Ctrl+Z undoes)", 6000);
        });
    }

    // ---------------- beats ----------------
    void BeatsDialog()
    {
        var c = SelectedClips(x => Mixer.HasSound(x) && Plain(x)).FirstOrDefault();
        if (c == null || ed.MediaOf(c) is not { Seq: null } m) { SetTask("Select a music clip (plain speed)", 3000); return; }
        var every = new ComboBox { ItemsSource = new[] { "Every beat", "Every 2nd beat", "Every bar (4 beats)", "Every 2 bars" }, SelectedIndex = 0, Margin = new Thickness(0, 10, 0, 0) };
        if (Alert.Show(this, "Puts a marker on the beats of the selected music (old beat markers go). Clip ▸ Cut to the Beat then moves the edits of selected clips onto them.",
                       "Detect Beats", AlertKind.Question, new[] { "Detect", "Cancel" }, every) != 0) return;
        int n = every.SelectedIndex switch { 1 => 2, 2 => 4, 3 => 8, _ => 1 };
        _ = RunTool("Beat detection", async ct =>
        {
            var (lo, hi) = TimeMap.Span(c);
            var r = await Task.Run(() => Pcm.Mono(m, lo, hi - lo, 24000) is { } pcm ? Beats.Detect(pcm, 24000) : null, ct);
            if (r == null || r.Times.Count < 2) { Alert.Show(this, "No steady beat was found in this clip.", "Detect Beats"); SetTask("", 1); return; }
            var times = r.Times.Select(t => TimeMap.Out(c, lo + t)).Where(t => t >= c.Start - 1e-6 && t < c.End).ToList();
            int k = ed.BeatMarkers(times, n, r.DownbeatPhase);
            SetTask($"{r.Bpm:0.#} BPM — {k} beat markers (Ctrl+Z undoes)", 5000);
        });
    }
    void CutToBeat()
    {
        var clips = SelectedClips(c => c.Kind is ClipKind.Video or ClipKind.Image);
        if (clips.Count == 0) clips = SelectedClips();
        if (!ed.SnapToBeats(clips)) SetTask("Detect beats first (Clip ▸ Detect Beats), then select the clips to cut", 3500);
        else SetTask("Edits moved onto the beat (Ctrl+Z undoes)", 3000);
    }

    // ---------------- auto-ducking ----------------
    void DuckingDialog()
    {
        var music = SelectedClips(c => c.Kind == ClipKind.Audio || c.Kind == ClipKind.Video && !c.NoAudio);
        if (music.Count == 0) { SetTask("Select the music clips to duck", 3000); return; }
        var musicTracks = music.Select(c => c.Track).ToHashSet();
        var others = Tracks.AudioRows.Where(r => !musicTracks.Contains(r)).ToList();
        var from = new ComboBox { ItemsSource = new[] { "Speech on every other audio track" }.Concat(others.Select(r => "Speech on " + Tracks.Name(r))).ToList(), SelectedIndex = 0, Margin = new Thickness(0, 10, 0, 0) };
        var (ar, amount) = ToolSlider("Duck by (dB)", 3, 30, 15, "0");
        var (fr, fade) = ToolSlider("Fades (s)", 0.1, 2, 0.5, "0.0");
        var p = new StackPanel(); p.Children.Add(from); p.Children.Add(ar); p.Children.Add(fr);
        if (Alert.Show(this, "The music gets quieter while someone speaks — written as volume keyframes on the music clips (they replace their volume keys).",
                       "Auto Ducking", AlertKind.Question, new[] { "Duck", "Cancel" }, p) != 0) return;
        var rows = from.SelectedIndex <= 0 ? others : new List<int> { others[from.SelectedIndex - 1] };
        var dialogue = ed.Clips.Where(c => rows.Contains(c.Track) && Mixer.HasSound(c) && !ed.TrackOff[c.Track] && Plain(c)).ToList();
        if (dialogue.Count == 0) { SetTask("No speech clips on those tracks", 3000); return; }
        var s = new Ducking.Settings(amount.Value, fade.Value * 0.6, fade.Value * 1.4);
        _ = RunTool("Auto ducking", async ct =>
        {
            var plan = await Task.Run(() => Ducking.Plan(ed, music, dialogue, s), ct);
            int dips = Ducking.Write(ed, plan);
            SetTask(dips == 0 ? "No speech found under the music" : $"Music ducked {dips} time{(dips == 1 ? "" : "s")} by {s.Db:0} dB (Ctrl+Z undoes; the keys show on the Clip vol band)", 6000);
        });
    }

    // ---------------- colour match ----------------
    void ColorMatchDialog()
    {
        var targets = SelectedClips(c => c.Kind is ClipKind.Video or ClipKind.Image);
        if (targets.Count == 0) { SetTask("Select the clips to match", 3000); return; }
        var refs = ed.Clips.Where(c => c.Kind is ClipKind.Video or ClipKind.Image && !targets.Contains(c) && ed.MediaOf(c) is { Offline: false }).OrderBy(c => c.Start).ToList();
        if (refs.Count == 0) { Alert.Show(this, "There is no other clip to match to.", "Colour Match"); return; }
        var box = new ComboBox { ItemsSource = refs.Select(c => $"{TimeUtil.Format(c.Start)}  {Tracks.Label(c.Track)}  {ed.MediaOf(c)?.Name}").ToList(), Margin = new Thickness(0, 10, 0, 0) };
        var before = refs.LastOrDefault(c => c.Start < targets[0].Start);
        box.SelectedIndex = before != null ? refs.IndexOf(before) : 0;
        var (sr, strength) = ToolSlider("Strength (%)", 10, 100, 100, "0");
        var p = new StackPanel(); p.Children.Add(new TextBlock { Text = "Match to (its middle frame, as graded)" }); p.Children.Add(box); p.Children.Add(sr);
        if (Alert.Show(this, "The selected clips' colours are matched to the reference — written as their red, green and blue curves (Lumetri ▸ Curves), so you can fine-tune them.",
                       "Colour Match", AlertKind.Question, new[] { "Apply Match", "Cancel" }, p) != 0) return;
        var reference = refs[Math.Max(0, box.SelectedIndex)];
        double st = strength.Value / 100;
        _ = RunTool("Colour match", async ct =>
        {
            var rpx = await Task.Run(() => ThumbFrames.GrabClip(ed, reference, (reference.Start + reference.End) / 2, 480, reference.Grade), ct);
            if (rpx == null) throw new Exception("The reference frame could not be read.");
            var grades = new List<(Clip c, ColorGrade g)>();
            foreach (var c in targets)
            {
                double t = ed.Time >= c.Start && ed.Time < c.End ? ed.Time : (c.Start + c.End) / 2;
                var tpx = await Task.Run(() => ThumbFrames.GrabClip(ed, c, t, 480, ColorMatch.WithoutRgbCurves(c.Grade)), ct);
                if (tpx != null) grades.Add((c, ColorMatch.Match(c.Grade, tpx.Value.px, rpx.Value.px, st)));
            }
            ed.Edit(() => { foreach (var (c, g) in grades) c.Grade = g; }, "Colour Match");
            BuildInspector();
            SetTask($"Matched {grades.Count} clip{(grades.Count == 1 ? "" : "s")} to {ed.MediaOf(reference)?.Name} (Ctrl+Z undoes)", 4000);
        });
    }

    // ---------------- background removal ----------------
    /// <summary>Remove Background: the AI selects the main subject (the middle of the frame) on the frame at the playhead, the mask becomes a
    /// cut-out (the background transparent: what is on the tracks below shows), then it is tracked through the whole clip.</summary>
    async Task RemoveBackground()
    {
        if (ed.Selected is not { Kind: ClipKind.Video } c || ed.MediaOf(c) is not { Seq: null, Offline: false } m) { SetTask("Select a video clip", 3000); return; }
        if (!await EnsureModelsAsync()) return;
        Pause();
        if (ed.Time < c.Start || ed.Time >= c.End) ed.Seek(c.Start + Math.Min(1, c.Dur / 2));
        int frame = Editor.SourceFrame(c, ed.Time);
        Mo.Busy = "Finding the subject…"; Mo.InvalidateVisual();
        List<float[]> polys;
        try { polys = await ObjectTracker.SelectAsync(m, frame, new List<(float, float, float)> { (0.5f, 0.45f, 1f) }, new Rect(0.12, 0.04, 0.76, 0.96)); }
        finally { Mo.Busy = null; Mo.InvalidateVisual(); }
        if (polys.Count == 0) { SetTask("No subject found in the middle of the frame — select it with the AI tool instead", 4000); return; }
        var mask = ed.AddMask(c, MaskKind.AI, polys, frame);
        ed.Edit(() => { mask.Effect = MaskFx.CutOut; mask.Invert = false; mask.Feather = Math.Max(mask.Feather, 2); mask.Name = "Background removed"; }, "Remove Background");
        Mo.ActiveMaskId = mask.Id;
        BuildInspector();
        await TrackMaskAsync(c, mask, 0);
    }

    // ---------------- reframe with push-in ----------------
    void ReframeZoomDialog()
    {
        var (zr, zoom) = ToolSlider("Push in by (%)", 2, 40, 12, "0");
        if (Alert.Show(this, "Each V1 clip slowly zooms in over its length while the reframe keeps its subject in view (Scale and Pan keyframes).",
                       "Auto Reframe with Push-in", AlertKind.Question, new[] { "Reframe", "Cancel" }, zr) != 0) return;
        AutoReframe(zoom.Value);
    }

    // ---------------- EDL / OTIO ----------------
    void ExportInterchange(bool otio)
    {
        string name = ed.Seq.Name;
        var dlg = new SaveFileDialog { Filter = otio ? "OpenTimelineIO|*.otio" : "CMX 3600 EDL|*.edl", FileName = name, DefaultExt = otio ? ".otio" : ".edl" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, otio ? Interchange.Otio(ed, name) : Interchange.Edl(ed, name));
            SetTask($"Exported {Path.GetFileName(dlg.FileName)}", 3000);
        }
        catch (Exception ex) { Alert.Show(this, ex.Message, "Export failed", AlertKind.Error); }
    }
}
