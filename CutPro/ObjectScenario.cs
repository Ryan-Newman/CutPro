using System.Diagnostics;
using System.IO;
using System.Windows.Input;

namespace CutPro;

/// <summary>CutPro.exe --objui out.txt [media]: scripted object-selection session through the real window code
/// (select, track, delete / undo / re-add the clip, select and track again). Alerts are logged instead of shown.</summary>
public partial class MainWindow
{
    public async void ObjScenario(string outFile, string? media)
    {
        var log = new List<string>();
        Alert.Auto = (title, msg, _) => { log.Add($"  ALERT {title}: {msg.Replace('\n', ' ')}"); return 0; };
        try
        {
            media ??= Path.Combine(Path.GetTempPath(), "cutpro_test", "a.mp4");
            await ImportAsync(new[] { media });
            var m = ed.Media.Last();
            log.Add($"media {Path.GetFileName(m.Path)} {m.Width}x{m.Height} dur {m.Duration:0.000} seek={m.SeekDecoder} play={m.PlayDecoder}");
            ed.AddMedia(m, null, 0);
            Clip V() => ed.Clips.First(c => c.Kind == ClipKind.Video && c.MediaId == m.Id);
            async Task Select(string what, double t)
            {
                var c = V(); ed.Select(c); ed.Seek(c.Start + t); await Task.Delay(400);
                var sw = Stopwatch.StartNew();
                var err = await AiClickAsync(c, 0.5f, 0.5f, true, ModifierKeys.None);
                log.Add($"{what}: select at clip+{t:0.00}s (source frame {Editor.SourceFrame(c, ed.Time)}): {err ?? "ok"} in {sw.ElapsedMilliseconds} ms, masks={c.Masks.Count}");
            }
            async Task Track(string what)
            {
                var c = V(); if (c.Masks.Count == 0) { log.Add($"{what}: no mask to track"); return; }
                var sw = Stopwatch.StartNew();
                var r = await TrackMaskAsync(c, c.Masks[^1], 0, 15);
                log.Add($"{what}: track both ways 15 frames: {r ?? "ok"} in {sw.ElapsedMilliseconds} ms, stored {ed.MaskTracks[c.Masks[^1].Id].Frames.Count} frames");
            }
            await Select("1 fresh clip", 1.0); await Track("1");
            ed.Select(V()); ed.DeleteSelection(false); await Task.Delay(400);
            ed.Undo(); await Task.Delay(400);
            await Select("2 deleted + undo", 2.0); await Track("2");
            ed.Select(V()); ed.DeleteSelection(false); await Task.Delay(400);
            ed.AddMedia(m, null, 3.0); await Task.Delay(400);
            await Select("3 deleted + re-added from the bin at 3 s", 0.5); await Track("3");
            ed.Select(V()); ed.DeleteSelection(true); await Task.Delay(400);
            ed.AddMedia(m, null, 0); await Task.Delay(400);
            await Select("4 ripple-deleted + re-added at 0", Math.Min(3.0, V().Dur - 0.2)); await Track("4");
            await Select("5 near the clip end", V().Dur - 1.0 / 30); await Track("5");
            // drawn ellipse, playhead parked after the clip (tracking starts from the clip's last frame)
            var cv = V(); ed.Select(cv); ed.Seek(cv.Start + 1);
            OnShapeDrawn(cv, MaskKind.Ellipse, new() { MaskGeometry.Shape(MaskKind.Ellipse, new System.Windows.Rect(0.4, 0.4, 0.2, 0.2)) });
            ed.Seek(cv.End + 2); await Task.Delay(300);
            var sw6 = Stopwatch.StartNew();
            var r6 = await TrackMaskAsync(V(), V().Masks[^1], -1, 20);
            log.Add($"6 ellipse, playhead after the clip, track backwards: {r6 ?? "ok"} in {sw6.ElapsedMilliseconds} ms, stored {ed.MaskTracks[V().Masks[^1].Id].Frames.Count} frames");
            // 7. a tracking run is one undo step; keyframe add / navigation
            var c7 = V(); var m7 = c7.Masks[^1]; var tr7 = ed.MaskTracks[m7.Id];
            ed.Seek(c7.Start + 1.0); await Task.Delay(200);
            ed.ClearTracking(m7.Id);
            int n0 = tr7.Frames.Count;
            await TrackMaskAsync(c7, m7, 1, 20);
            int n1 = tr7.Frames.Count;
            ed.Undo();
            log.Add($"7 track 20 frames then Ctrl+Z: {n0} → {n1} → {tr7.Frames.Count} outlines ({(tr7.Frames.Count == n0 && n1 > n0 ? "ok" : "FAIL")})");
            ed.Seek(c7.Start + 0.5); ToggleKeyAtPlayhead(c7, m7);
            int kf = Editor.SourceFrame(c7, ed.Time);
            ed.Seek(c7.Start + 0.8); GoToKey(c7, m7, -1);      // (the drawn ellipse also has its seed key at 1.0 s)
            log.Add($"7 add key at {kf}, jump back from later: playhead at source frame {Editor.SourceFrame(c7, ed.Time)} ({(Editor.SourceFrame(c7, ed.Time) == kf && tr7.IsKey(kf) ? "ok" : "FAIL")})");
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); }
        File.WriteAllLines(outFile, log);
        Close();
    }
}
