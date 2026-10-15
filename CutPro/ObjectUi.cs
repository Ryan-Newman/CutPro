using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>Object selection UI: monitor tools (AI click, ellipse, rectangle, freehand), tracking jobs, the Object masks
/// section of Effect Controls, and the first-use AI model download.</summary>
public partial class MainWindow
{
    CancellationTokenSource? trackCts;
    string? trackingId;
    double trackFrac;
    string? aiMaskId, aiClipId;
    int aiFrame = -1;

    void InitObjects()
    {
        Mo.Ed = ed;
        Mo.AiClick += OnAiClick;
        Mo.ShapeDrawn += OnShapeDrawn;
        Mo.Corrected += OnCorrected;
        Po.Ed = ed;
        Po.Enabled = () => Mo.Tool == ObjectTool.None && Mo.PickColor == null;
        Po.Edited = () => { RefreshProps(); Tl.InvalidateVisual(); if (Kfe.IsVisible) Kfe.InvalidateVisual(); };
        ed.PropsChanged += Po.InvalidateVisual;
        ed.Seeked += Po.InvalidateVisual;
        ed.MasksChanged += () => { Mo.InvalidateVisual(); RefreshMaskKeys(); };
        ed.Changed += () =>
        {
            if (Mo.Target == null || (aiClipId != null && Mo.Target?.Id != aiClipId)) EndAiSession();
            // a selected clip with masks: its last mask is the one being edited unless another was picked
            if (ed.Selected is { } s && s.Masks.Count > 0 && s.Masks.All(m => m.Id != Mo.ActiveMaskId)) Mo.ActiveMaskId = s.Masks[^1].Id;
            Mo.InvalidateVisual(); Po.InvalidateVisual();
        };
        ed.Seeked += () => { if (Mo.Target is { } c && Editor.SourceFrame(c, ed.Time) != aiFrame) EndAiSession(); Mo.InvalidateVisual(); };
        // undo / redo while tracking: stop, and make the run so far its own undo step (so this undo reverts it)
        ed.HistoryMoving += () => { if (trackCts != null) { trackCts.Cancel(); ed.MaskEnd(); } };
    }

    // ---------------- tools ----------------
    void ObjTool_Click(object sender, RoutedEventArgs e)
    {
        var t = Enum.Parse<ObjectTool>((string)((FrameworkElement)sender).Tag);
        SetObjectTool(Mo.Tool == t ? ObjectTool.None : t);
    }

    void SetObjectTool(ObjectTool t)
    {
        Mo.Tool = t; Po.InvalidateVisual();
        ToolAI.IsChecked = t == ObjectTool.AI; ToolEllipse.IsChecked = t == ObjectTool.Ellipse;
        ToolRect.IsChecked = t == ObjectTool.Rectangle; ToolFree.IsChecked = t == ObjectTool.Freehand;
        EndAiSession();
        if (t != ObjectTool.None) Pause();
    }

    void EndAiSession() { aiMaskId = aiClipId = null; aiFrame = -1; aiBox = null; Mo.AiPoints.Clear(); }
    Rect? aiBox;

    /// <summary>First use: offer to download the MobileSAM model.</summary>
    async Task<bool> EnsureModelsAsync()
    {
        if (SamEngine.ModelsPresent) return true;
        if (!Alert.Confirm(this, "AI object selection uses the MobileSAM model (about 43 MB, MIT licence) from huggingface.co/Acly/MobileSAM. " +
                                 "It is downloaded once and runs on this PC — nothing is uploaded.", "Download the AI model?", "Download", "Not now")) return false;
        try
        {
            await SamEngine.DownloadAsync(new Progress<double>(p => { Mo.Busy = $"Downloading AI model… {p * 100:0}%"; Mo.InvalidateVisual(); }), CancellationToken.None);
            return true;
        }
        catch (Exception ex) { Alert.Show(this, ex.Message, "AI model download failed", AlertKind.Error); return false; }
        finally { Mo.Busy = null; Mo.InvalidateVisual(); }
    }

    async void OnAiClick(Clip c, float x, float y, bool positive) => await AiClickAsync(c, x, y, positive, Keyboard.Modifiers);

    /// <summary>AI click in the monitor: a plain click selects a new object; Shift+click adds to / Alt+click (or right-click)
    /// removes from the current selection. On a frame where the active AI mask has an outline from tracking (or none), a
    /// Shift/Alt+click re-selects the object there, guided by that outline, and keys the result (a correction).
    /// Returns the error text (null = ok).</summary>
    async Task<string?> AiClickAsync(Clip c, float x, float y, bool positive, ModifierKeys mods)
    {
        if (Mo.Busy != null || !await EnsureModelsAsync()) return "busy";
        var m = ed.MediaOf(c); if (m == null) return "no media";
        int frame = Editor.SourceFrame(c, ed.Time);
        bool modifier = mods.HasFlag(ModifierKeys.Shift) || !positive;
        bool refine = aiMaskId != null && aiClipId == c.Id && aiFrame == frame && modifier;
        bool correct = false;
        if (!refine)
        {
            EndAiSession();
            // correction of the active AI mask on this frame
            if (modifier && c.Masks.FirstOrDefault(k => k.Id == Mo.ActiveMaskId) is { Kind: MaskKind.AI } act && ed.MaskTracks.GetValueOrDefault(act.Id)?.At(frame) is { } shown)
            {
                var b = MaskGeometry.Bounds(shown);
                aiBox = new Rect(Math.Max(0, b.X - b.Width * 0.06), Math.Max(0, b.Y - b.Height * 0.06), b.Width * 1.12, b.Height * 1.12);
                aiMaskId = act.Id; aiClipId = c.Id; aiFrame = frame; correct = true;
            }
            else if (!positive) return null;
        }
        Mo.AiPoints.Add((x, y, positive ? 1 : 0));
        Mo.Busy = ObjectTracker.Provider == null ? "Loading AI model…" : "Finding the object…";
        Mo.InvalidateVisual();
        try
        {
            var sw = Stopwatch.StartNew();
            var polys = await ObjectTracker.SelectAsync(m, frame, Mo.AiPoints.ToList(), aiBox);
            if (polys.Count == 0) { SetTask("Nothing found there — try clicking inside the object", 4000); Mo.AiPoints.RemoveAt(Mo.AiPoints.Count - 1); return "nothing found"; }
            if (!refine && !correct)
            {
                var mask = ed.AddMask(c, MaskKind.AI, polys, frame);
                aiMaskId = mask.Id; aiClipId = c.Id; aiFrame = frame; Mo.ActiveMaskId = mask.Id;
                var tr = ed.MaskTracks[mask.Id];
                tr.SeedFrame = frame; tr.SeedPoints = Mo.AiPoints.Select(p => new[] { p.x, p.y, p.label }).ToList();
                SetTask($"Selected in {sw.ElapsedMilliseconds} ms ({ObjectTracker.Provider}) — Track ▶ to follow it", 5000);
            }
            else
            {
                ed.SetMaskFrame(aiMaskId!, frame, polys);
                SetTask($"Keyframe at {TimeUtil.Format(ed.Time)} — Shift/Alt+click to refine, then Track ▶ to re-track from here", 6000);
            }
            return null;
        }
        catch (Exception ex) { EndAiSession(); Alert.Show(this, ex.Message, "Couldn't select the object", AlertKind.Error); return ex.Message; }
        finally { Mo.Busy = null; Mo.InvalidateVisual(); BuildInspector(); }
    }

    void OnShapeDrawn(Clip c, MaskKind kind, List<float[]> polys)
    {
        var mask = ed.AddMask(c, kind, polys, Editor.SourceFrame(c, ed.Time));
        Mo.ActiveMaskId = mask.Id;
        SetTask($"{mask.Name} added — Track ▶ to follow the object", 4000);
    }

    void OnCorrected(Clip c, ObjectMask m, int frame)
    {
        SetTask($"{m.Name}: keyframe at {TimeUtil.Format(ed.Time)} (Ctrl+Z undoes) — Track ▶ to re-track from here", 6000);
        BuildInspector();
    }

    // ---------------- keyframes ----------------
    ObjectMask? ActiveMask(out Clip? clip)
    {
        clip = ed.Selected is { Kind: ClipKind.Video or ClipKind.Image } c ? c : null;
        return clip?.Masks.FirstOrDefault(x => x.Id == Mo.ActiveMaskId) ?? clip?.Masks.LastOrDefault();
    }

    /// <summary>Moves the playhead to the previous (-1) / next (+1) keyframe of the mask within the clip.</summary>
    void GoToKey(Clip c, ObjectMask m, int dir)
    {
        if (!ed.MaskTracks.TryGetValue(m.Id, out var tr)) return;
        int cur = Editor.SourceFrame(c, Math.Clamp(ed.Time, c.Start, c.End - 1.0 / TimeUtil.Fps));
        var (f0, f1) = Editor.SourceFrames(c);
        int? k = dir < 0 ? tr.PrevKey(cur) : tr.NextKey(cur);
        if (k is not int f || f < f0 || f > f1) { SetTask(dir < 0 ? "No earlier keyframe in this clip" : "No later keyframe in this clip", 2500); return; }
        Pause();
        ed.Seek(Editor.TimeOfFrame(c, f));
    }

    void ToggleKeyAtPlayhead(Clip c, ObjectMask m)
    {
        if (ed.Time < c.Start || ed.Time >= c.End) { SetTask("Put the playhead over the clip first", 3000); return; }
        int f = Editor.SourceFrame(c, ed.Time);
        bool was = ed.MaskTracks.GetValueOrDefault(m.Id)?.IsKey(f) == true;
        ed.ToggleKey(m.Id, f);
        SetTask(was ? "Keyframe removed" : "Keyframe added — drag the outline in the monitor to correct it", 3500);
        BuildInspector();
    }

    // live parts of the mask cards (updated as the playhead moves, without rebuilding the inspector)
    readonly List<(Clip c, ObjectMask m, TextBlock state, Button key, MaskKeyStrip strip)> liveKeys = new();

    void RefreshMaskKeys()
    {
        foreach (var (c, m, state, key, strip) in liveKeys)
        {
            strip.InvalidateVisual();
            var tr = ed.MaskTracks.GetValueOrDefault(m.Id);
            if (tr == null) continue;
            bool over = ed.Time >= c.Start && ed.Time < c.End;
            int f = Editor.SourceFrame(c, ed.Time);
            bool isKey = over && tr.IsKey(f);
            state.Text = !over ? $"{tr.Keys.Count} keyframe{(tr.Keys.Count == 1 ? "" : "s")}"
                       : isKey ? "◆ Keyframe at the playhead"
                       : tr.IsTracked(f) ? "Tracked frame — drag the outline to correct it"
                       : tr.Frames.Count > 1 && f > tr.Range.first && f < tr.Range.last ? "Interpolated between keyframes"
                       : "Held from the nearest outline";
            key.Content = isKey ? "◆ Remove key" : "◇ Add key";
        }
    }

    // ---------------- tracking ----------------
    void TrackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ed.Selected is not { Kind: ClipKind.Video or ClipKind.Image } c || c.Masks.Count == 0)
        { SetTask("Select a clip with an object mask first (✦ Select object)", 4000); return; }
        var m = c.Masks.FirstOrDefault(x => x.Id == Mo.ActiveMaskId) ?? c.Masks[^1];
        _ = TrackMaskAsync(c, m, int.Parse((string)((FrameworkElement)sender).Tag));
    }

    /// <summary>Tracks from the playhead (dir 1 forwards, -1 backwards, 0 both); click again (or Esc) to stop. Keyframes are
    /// kept and re-anchor the tracker (so after a correction, Track ▶ re-tracks from it). One undo step per run.</summary>
    async Task<string?> TrackMaskAsync(Clip c, ObjectMask mask, int dir, int limit = int.MaxValue)
    {
        if (trackCts != null) { trackCts.Cancel(); return "stopped"; }
        var media = ed.MediaOf(c);
        if (media == null || c.Kind != ClipKind.Video) { SetTask("Tracking needs a video clip", 4000); return "not video"; }
        if (mask.Kind == MaskKind.AI && !await EnsureModelsAsync()) return "no model";
        if (!ed.MaskTracks.TryGetValue(mask.Id, out var tr)) return "no track";
        double t = Math.Clamp(ed.Time, c.Start, c.End - 1.0 / TimeUtil.Fps);
        int cur = Editor.SourceFrame(c, t); var (first, last) = Editor.SourceFrames(c);
        last = Math.Min(last, cur + limit); first = Math.Max(first, cur - limit);
        var start = tr.At(cur);
        if (start == null) return "no outline";
        Pause(); SetObjectTool(ObjectTool.None);
        Mo.ActiveMaskId = mask.Id;
        ed.MaskBegin(mask.Id);
        if (!tr.Frames.ContainsKey(cur)) tr.Set(cur, start);          // track from what is on screen
        var keys = tr.Keys.Where(k => k != cur).ToDictionary(k => k, k => tr.Frames[k]);
        var cts = trackCts = new CancellationTokenSource(); trackingId = mask.Id; trackFrac = 0;
        var sw = Stopwatch.StartNew();
        int stored = 0, totalAll = (dir >= 0 ? last - cur : 0) + (dir <= 0 ? cur - first : 0), doneBefore = 0;
        void Store(int f, List<float[]> p) => Dispatcher.BeginInvoke(() =>
        {
            if (cts.IsCancellationRequested || tr.IsKey(f)) return;          // stopped (maybe undone), or a keyframe: never overwritten
            tr.Set(f, p); if (++stored % 2 == 0) ed.RaiseMasks();
        });
        var prog = new Progress<(int done, int total)>(p =>
        {
            trackFrac = (doneBefore + p.done) / (double)Math.Max(1, totalAll);
            var fps = (doneBefore + p.done) / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            Mo.Busy = $"Tracking {mask.Name} — {trackFrac * 100:0}%  ({fps:0} fps) · Esc stops";
            SetTask($"Tracking {mask.Name}: {doneBefore + p.done}/{totalAll} frames", 0);
            Mo.InvalidateVisual(); UpdateTrackProgress();
        });
        string? result = null, result2 = null;
        BuildInspector();
        try
        {
            if (dir >= 0 && last > cur) result = await ObjectTracker.TrackAsync(media, mask, start, cur, last, Store, prog, cts.Token, keys);
            doneBefore = dir >= 0 ? last - cur : 0;
            if (dir <= 0 && cur > first && !cts.IsCancellationRequested) result2 = await ObjectTracker.TrackAsync(media, mask, start, cur, first, Store, prog, cts.Token, keys);
        }
        catch (OperationCanceledException) { result = "stopped"; }
        catch (Exception ex) { result = ex.Message; }
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);   // let queued Store calls land
        if (ReferenceEquals(trackCts, cts)) { trackCts = null; trackingId = null; }
        Mo.Busy = null;
        ed.MaskEnd();
        var msg = result ?? result2;
        SetTask(msg == null ? $"Tracked {mask.Name}: {stored} frames in {sw.Elapsed.TotalSeconds:0.0} s (Ctrl+Z undoes)" : $"{mask.Name}: {msg}", 8000);
        ed.RaiseMasks(); BuildInspector(); Mo.InvalidateVisual();
        return msg;
    }

    void StatusTask_Click(object s, RoutedEventArgs e) { trackCts?.Cancel(); reframeCts?.Cancel(); }

    readonly System.Windows.Threading.DispatcherTimer taskTimer = new();
    /// <summary>Status-bar task text; hides after ms (0 = keep).</summary>
    void SetTask(string text, int ms)
    {
        StatusTaskText.Text = text; StatusTask.Visibility = Visibility.Visible;
        taskTimer.Stop();
        if (ms > 0)
        {
            taskTimer.Interval = TimeSpan.FromMilliseconds(ms);
            taskTimer.Tick -= HideTask; taskTimer.Tick += HideTask; taskTimer.Start();
        }
    }
    void HideTask(object? s, EventArgs e) { taskTimer.Stop(); if (trackCts == null) StatusTask.Visibility = Visibility.Collapsed; }

    ProgressBar? trackBar;
    void UpdateTrackProgress() { if (trackBar != null) trackBar.Value = trackFrac; }

    // ---------------- Effect Controls: object masks ----------------
    static (string label, double min, double max, double step)? AmountSpec(MaskFx fx) => fx switch
    {
        MaskFx.Blur => ("Blurriness", 0, 150, 0.5),
        MaskFx.Pixelate => ("Block size", 2, 120, 1),
        MaskFx.Darken => ("Darkness %", 0, 100, 1),
        MaskFx.Tint => ("Opacity %", 0, 100, 1),
        _ => null,
    };

    void BuildMaskInspector(Clip c)
    {
        var sec = Section("Object masks", true);
        var tools = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        void ToolBtn(string text, ObjectTool t, string tip)
        {
            var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(8, 3, 8, 3) };
            if (t == ObjectTool.AI) b.Style = (Style)FindResource("Primary");
            b.Click += (_, _) => SetObjectTool(t);
            tools.Children.Add(b);
        }
        ToolBtn("✦ Select object", ObjectTool.AI, "Click a person or object in the monitor (AI)");
        ToolBtn("◯", ObjectTool.Ellipse, "Ellipse"); ToolBtn("▭", ObjectTool.Rectangle, "Rectangle"); ToolBtn("✎", ObjectTool.Freehand, "Freehand");
        sec.Children.Add(tools);
        if (c.Masks.Count == 0)
        {
            sec.Children.Add(new TextBlock
            {
                Text = "Select a person or object to blur it, pixelate or black it out, keep it sharp while the background blurs, spotlight it, " +
                       "colour-pop it or cut it out. Put the playhead on a frame where it's visible, click ✦ Select object, click it in the monitor, then Track ▶.",
                TextWrapping = TextWrapping.Wrap, Foreground = Dim, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0)
            });
            return;
        }
        foreach (var m in c.Masks.ToList())
        {
            bool active = m.Id == Mo.ActiveMaskId;
            var card = new StackPanel();
            var border = new Border { Child = card, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(active ? 1.5 : 1), Padding = new Thickness(8, 6, 8, 8), Margin = new Thickness(0, 6, 0, 0) };
            border.SetResourceReference(Border.BorderBrushProperty, active ? "B.Accent" : "B.Border");
            border.PreviewMouseLeftButtonDown += (_, _) => { if (Mo.ActiveMaskId != m.Id) { Mo.ActiveMaskId = m.Id; Mo.InvalidateVisual(); Dispatcher.BeginInvoke(BuildInspector); } };
            // header: enable · name · delete
            var head = new DockPanel();
            var del = new Button { Content = "", Style = (Style)FindResource("Icon"), ToolTip = "Delete mask" };
            del.Click += (_, _) => { ed.RemoveMask(c, m.Id); if (Mo.ActiveMaskId == m.Id) Mo.ActiveMaskId = null; };
            DockPanel.SetDock(del, Dock.Right);
            var on = new CheckBox { IsChecked = m.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "On / off" };
            on.Click += (_, _) => { m.Enabled = on.IsChecked == true; ed.Touch(); };
            head.Children.Add(del); head.Children.Add(on);
            head.Children.Add(new TextBlock { Text = $"{m.Name}  ·  {(m.Kind == MaskKind.AI ? "AI" : m.Kind.ToString())}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            card.Children.Add(head);
            // effect preset
            var preset = new ComboBox { ItemsSource = ObjectMask.Presets.Select(p => p.label).ToList(), SelectedIndex = Math.Max(0, m.PresetIndex) };
            preset.SelectionChanged += (_, _) =>
            {
                var p = ObjectMask.Presets[preset.SelectedIndex];
                if (p.fx != m.Effect) m.Amount = p.fx switch { MaskFx.Pixelate => 20, MaskFx.Darken => 70, MaskFx.Tint => 45, _ => 25 };
                m.Effect = p.fx; m.Invert = p.invert; ed.Touch(); Dispatcher.BeginInvoke(BuildInspector);
            };
            preset.Margin = new Thickness(0, 6, 0, 2); preset.ToolTip = "What happens to the selected object"; card.Children.Add(preset);
            if (AmountSpec(m.Effect) is { } spec) Slider(spec.label, spec.min, spec.max, spec.step, m.Amount, v => m.Amount = v, card);
            if (m.Effect == MaskFx.Tint) Row(card, "Color", new ColorField(m.TintColor, hex => { m.TintColor = hex; ed.Touch(); }));
            Slider("Feather", 0, 100, 0.5, m.Feather, v => m.Feather = v, card);
            Slider("Expand", -60, 60, 0.5, m.Expand, v => m.Expand = v, card);
            var inv = new CheckBox { Content = "Invert (apply outside the object)", IsChecked = m.Invert, Margin = new Thickness(0, 4, 0, 2) };
            inv.Click += (_, _) => { m.Invert = inv.IsChecked == true; ed.Touch(); Dispatcher.BeginInvoke(BuildInspector); };
            card.Children.Add(inv);
            // keyframes + tracking
            var tr = ed.MaskTracks.GetValueOrDefault(m.Id);
            int n = tr?.Frames.Count ?? 0, keysN = tr?.Keys.Count ?? 0, tracked = n - keysN;
            string status = tracked <= 0 ? $"{keysN} keyframe{(keysN == 1 ? "" : "s")} — Track to follow the object"
                          : $"{keysN} keyframe{(keysN == 1 ? "" : "s")} · {tracked} tracked frame{(tracked == 1 ? "" : "s")}";
            card.Children.Add(new TextBlock { Text = status, Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            var strip = new MaskKeyStrip(ed, c, m.Id);
            card.Children.Add(strip);
            var krow = new DockPanel { Margin = new Thickness(0, 2, 0, 4), LastChildFill = true };
            Button KeyBtn(string text, string tip, Action a)
            {
                var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 0, 4, 0) };
                b.Click += (_, _) => a();
                DockPanel.SetDock(b, Dock.Left); krow.Children.Add(b);
                return b;
            }
            KeyBtn("◀", "Previous keyframe", () => GoToKey(c, m, -1));
            var keyBtn = KeyBtn("◇ Add key", "Add a keyframe with the outline shown at the playhead, or remove the keyframe there", () => ToggleKeyAtPlayhead(c, m));
            KeyBtn("▶", "Next keyframe", () => GoToKey(c, m, 1));
            var state = new TextBlock { Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 0, 0) };
            krow.Children.Add(state);
            card.Children.Add(krow);
            liveKeys.Add((c, m, state, keyBtn, strip));
            var trow = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            if (trackingId == m.Id)
            {
                trackBar = new ProgressBar { Height = 6, Width = 150, Minimum = 0, Maximum = 1, Value = trackFrac, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                var stop = new Button { Content = "Stop" };
                stop.Click += (_, _) => trackCts?.Cancel();
                trow.Children.Add(trackBar); trow.Children.Add(stop);
            }
            else if (c.Kind == ClipKind.Video)
            {
                foreach (var (label, d, tip) in new[] { ("◀ Track", -1, "Track backwards from the playhead"), ("Track ▶", 1, "Track forwards from the playhead (keyframes are kept and re-anchor the tracker)"), ("◀ Both ▶", 0, "Track both ways") })
                {
                    var b = new Button { Content = label, ToolTip = tip, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 0) };
                    b.Click += (_, _) => _ = TrackMaskAsync(c, m, d);
                    trow.Children.Add(b);
                }
                if (tracked > 0)
                {
                    var clr = new Button { Content = "Clear", ToolTip = "Forget the tracked frames (keyframes stay)", Padding = new Thickness(8, 3, 8, 3) };
                    clr.Click += (_, _) => { ed.ClearTracking(m.Id); BuildInspector(); };
                    trow.Children.Add(clr);
                }
            }
            card.Children.Add(trow);
            if (active)
                card.Children.Add(new TextBlock { Text = "Correct any frame in the monitor: drag points, drag inside to move (a keyframe is added), then Track ▶ to re-track from it.",
                                                  Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            sec.Children.Add(border);
        }
        RefreshMaskKeys();
    }
}
