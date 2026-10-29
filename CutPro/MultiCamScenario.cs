using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// Multi-camera editing through the real window: --mcui out.txt (and the .multicam --uishot shot). Three cameras (the --multicamtest media)
/// selected in the Project panel → Create Multi-Camera Source Sequence (auto-answered: by sound, camera 1's sound, onto the timeline) → the
/// Multi-Camera view shows each camera's own picture at the playhead; a click / number key switches while paused (Ctrl: from the playhead on);
/// number keys while playing are recorded (red frame) and become cuts on stop, in one undo step; the cells keep decoding while playing (frame
/// gaps measured); Shift+0 hides the view; the clip's right-click menu has Multi-Camera.
/// </summary>
public partial class MainWindow
{
    public async void MultiCamScenario(string outFile)
    {
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        Alert.Auto = (title, msg, b) => { log.Add($"  ALERT {title}: {msg.Replace('\n', ' ')}"); return 0; };
        async Task Settle(int ms = 300) { UpdateLayout(); await Task.Delay(ms); UpdateLayout(); }
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "cutpro_mc");
            var files = MultiCamTest.EnsureMedia(dir).cams;
            await ImportAsync(files);
            var cams = files.Select(f => ed.Media.First(m => m.Path == f)).ToList();
            ShowSideView("Project"); await Settle(800);
            BinList.SelectedItems.Clear();
            foreach (var m in cams) BinList.SelectedItems.Add(m);
            await Settle(200);

            // ---------- create (the dialog auto-answered: sound sync, camera 1's sound, onto the timeline) ----------
            await CreateMultiCamDialog();
            for (int i = 0; i < 300 && toolCts != null; i++) await Task.Delay(100);
            await Settle(600);
            var mc = ed.Sequences.FirstOrDefault(s => s.MultiCam != null);
            Check(mc != null && mc.VideoTracks == 3 && ed.Seq != mc && ed.Clips.Count(c => c.Cam != null && c.Kind == ClipKind.Video) == 3,
                  $"Project panel selection ▸ Create Multi-Camera Source Sequence: a source with three cameras, its clips on the timeline ({ed.Clips.Count} clips)");
            Check(mc?.MultiCam?.Audio == 0 && ed.Clips.Where(c => c.Kind == ClipKind.Audio).All(a => a.MediaId == cams[0].Id && a.Cam?.Angle == -1),
                  "the dialog's default sound: camera 1's, fixed");
            Check(MultiCamOn && mcView is { Visibility: Visibility.Visible } && MultiCamBtn.Visibility == Visibility.Visible && MultiCamBtn.IsChecked == true && MonitorBox.Margin.Left > 100,
                  "the Multi-Camera view opens next to the program (left half of the monitor area), the transport's Multi-Cam button is on");
            if (mc == null || mcView == null) throw new Exception("no multicam");

            // ---------- the cells: each camera's own picture at the playhead ----------
            ed.Seek(5.0);
            for (int i = 0; i < 40 && mcView.Cells.Any(c => c.Pic.Source == null); i++) await Settle(100);
            await Settle(400);
            var shown = mcView.Cells.Select(CellFrame).ToList();
            double tau = ed.CamTau(ed.MultiCamClipAt(5)!, 5)!.Value;
            var want = Enumerable.Range(0, 3).Select(a => Editor.CamClipAt(mc, a, tau) is { } x ? (x.In + (tau - x.Start)) * 30 : -1).ToList();
            Check(mcView.Cells.Count == 3 && shown.Select(s => s.cam).SequenceEqual(new[] { 1, 2, 3 }) && shown.Zip(want).All(p => Math.Abs(p.First.frame - p.Second) <= 1.01),
                  $"three cells: each shows its camera, at the multicam time {tau:0.000} s ({string.Join(", ", shown.Select((s, i) => $"cell {i + 1}: camera {s.cam} frame {s.frame} ≈ {want[i]:0.0}"))})");
            Check(Marked() == 0, "the program's camera (camera 1) is framed");

            // ---------- paused: click / key / Ctrl+click ----------
            McPick(1, false); await Settle(400);
            Check(ed.MultiCamClipAt(5) is { Cam.Angle: 1 } c2 && c2.MediaId == cams[1].Id && Marked() == 1 && ed.UndoLabels.First() == "Multicam Switch",
                  "a click on camera 2 (paused): the clip at the playhead shows camera 2, its cell is framed");
            ed.Undo(); await Settle(200);
            HandleKey(Key.D3, ModifierKeys.None); await Settle(300);
            Check(ed.MultiCamClipAt(5) is { Cam.Angle: 2 } c3 && c3.MediaId == cams[2].Id, "key 3 (paused): camera 3");
            ed.Undo(); await Settle(200);
            McPick(1, true); await Settle(300);
            Check(ed.MultiCamClipAt(5 - 1 / TimeUtil.Fps) is { Cam.Angle: 0 } && ed.MultiCamClipAt(5) is { Cam.Angle: 1 } cc && Math.Abs(cc.Start - 5) < 1e-6,
                  "Ctrl+click on camera 2: a cut at the playhead, camera 2 from there");
            ed.Undo(); await Settle(200);

            // ---------- playing: number keys are recorded, cuts on stop ----------
            ed.Seek(2.0); await Settle(600);
            int undoN = ed.UndoLabels.Count();
            var gaps = new List<double>(); long last = 0; bool measuring = false;
            void Tick(object? s, EventArgs e) { long now = System.Diagnostics.Stopwatch.GetTimestamp(); if (measuring && last != 0) gaps.Add((now - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency); last = now; }
            CompositionTarget.Rendering += Tick;
            Play();
            for (int i = 0; i < 150 && !clockRunning; i++) await Task.Delay(20);
            await Task.Delay(600); measuring = true;
            var times = new List<double>();
            long idx0 = mcView.Cells[0].ShownIdx;
            HandleKey(Key.D2, ModifierKeys.None); times.Add(ed.Time); await Task.Delay(120);
            bool red = Marked(live: true) == 1;
            await Task.Delay(700);
            HandleKey(Key.D3, ModifierKeys.None); times.Add(ed.Time); await Task.Delay(800);
            HandleKey(Key.D1, ModifierKeys.None); times.Add(ed.Time); await Task.Delay(500);
            long idx1 = mcView.Cells[0].ShownIdx;
            measuring = false; CompositionTarget.Rendering -= Tick;
            bool beforeStop = ed.UndoLabels.Count() == undoN;
            Pause(); await Settle(400);
            var cutAt = times.Select(Editor.Frame).ToList();
            bool cuts = cutAt.Zip(new[] { 1, 2, 0 }).All(p => ed.MultiCamClipAt(p.First) is { } x && x.Cam!.Angle == p.Second && Math.Abs(x.Start - p.First) < 1e-6);
            Check(beforeStop && red && cuts && ed.UndoLabels.Count() == undoN + 1 && ed.UndoLabels.First() == "Multicam Cuts",
                  $"keys 2, 3, 1 while playing: recorded (camera 2's cell framed red, nothing changed yet), cut in on stop at {string.Join(", ", cutAt.Select(t => t.ToString("0.000")))} in one undo step");
            gaps.Sort();
            double worst = gaps.Count > 0 ? gaps[^1] : 999, p95 = gaps.Count > 0 ? gaps[(int)(gaps.Count * 0.95)] : 999;
            Check(idx1 > idx0 + 30 && worst < 150, $"while playing the cells keep decoding (camera 1: frame {idx0} → {idx1}); UI frame gaps 95 % ≤ {p95:0.0} ms, worst {worst:0.0} ms");
            ed.Undo(); await Settle(200);
            Check(ed.Clips.Count(c => c.Cam != null && c.Kind == ClipKind.Video) == 3, "undo: the recorded cuts go in one step");

            // ---------- menus, hiding the view ----------
            var menu = new ContextMenu();
            ClipMenuHigh(menu, ed.MultiCamClipAt(5)!);
            var mcItem = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == "Multi-Camera");
            Check(mcItem != null && mcItem.Items.OfType<MenuItem>().Count(i => (i.Header as string)?.StartsWith("Camera ") == true) == 3 &&
                  mcItem.Items.OfType<MenuItem>().Any(i => i.Header as string == "Flatten"), "the clip's right-click menu: Multi-Camera ▸ Camera 1 … 3, Flatten");
            HandleKey(Key.D0, ModifierKeys.Shift); await Settle(300);
            Check(!MultiCamOn && mcView.Visibility == Visibility.Collapsed && MonitorBox.Margin.Left == 12, "Shift+0 hides the view, the program monitor takes the whole area again");
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(outFile, log);
        Application.Current.Shutdown(fails == 0 ? 0 : 1);
    }

    /// <summary>The camera number and frame number a cell shows (its picture read back).</summary>
    static (int cam, int frame) CellFrame(MultiCamView.Cell c)
    {
        if (c.Pic.Source is not BitmapSource b) return (0, -1);
        var bgra = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight; var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        return MultiCamTest.DecodeFrame(px, w, h);
    }
    /// <summary>The framed cell (yellow; red while recording), −1 = none.</summary>
    int Marked(bool live = false)
    {
        if (mcView == null) return -1;
        var want = ((SolidColorBrush)FindResource(live ? "B.Error" : "B.Warn")).Color;
        return mcView.Cells.FindIndex(c => c.Frame.BorderBrush is SolidColorBrush s && s.Color == want);
    }

    /// <summary>--uishot .multicam: an interview cut between three cameras, the Multi-Camera view open.</summary>
    async Task MultiCamShot(string path, Action<string> shot)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cutpro_mc");
        string[] files;
        try { files = MultiCamTest.EnsureMedia(dir).cams; } catch { return; }
        var keep = ed.Seq;
        await ImportAsync(files);
        var cams = files.Select(f => ed.Media.FirstOrDefault(m => m.Path == f)).Where(m => m != null).Cast<MediaItem>().ToList();
        if (cams.Count < 3) return;
        var k = new double[3];
        for (int i = 1; i < 3; i++) { var other = cams[i]; k[i] = -(await Task.Run(() => AudioSync.Between(cams[0], other)))?.Lag ?? 0; }
        var mc = ed.CreateMultiCam("Interview multicam", cams.Select((m, i) => (m, k[i])).ToList(), new List<(MediaItem, double)>(), 0, "Sound");
        ed.NewSequence("Interview");
        ed.AddMedia(ed.SeqItem(mc.Id)!, null, 0);
        ed.ApplyCamCuts(Tracks.V1, new[] { (4.0, 1), (7.0, 2), (10.0, 0) });
        ed.Seek(8.0); SetMultiCamView(true);
        await Task.Delay(3000);
        shot(Path.ChangeExtension(path, ".multicam.png"));
        SetMultiCamView(false); ed.Activate(keep);
    }
}
