using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CutPro;

/// <summary>
/// CutPro.exe --multicamtest dir: multi-camera editing, headless.
/// Three cameras record the same sound (a noise with a rhythm) from different moments — camera 3 starts between frames — and an external
/// recorder runs longer than all of them. Each camera draws its number and its frame number into the picture, so every exported frame can
/// be checked for the right camera and the frame that was filmed at that moment.
/// - sync by sound: lags within 0.2 ms; the source sequence (cameras on V1–V3, starts on frames, sub-frame rest in In), undo;
/// - on a timeline: camera 1 wherever it has picture, the others fill in; sound linked to each piece;
/// - switching while paused (cut where the camera's picture starts), recorded switches (one frame: the last wins, same camera: no cut), undo;
/// - export: every frame = the camera and the synced frame; the sound runs on without a jump through every cut (audio follows video);
/// - fixed sound (camera 1's, the recorder's: pieces where only it runs), Flatten, project round trip, timecode sync seconds.
/// </summary>
public static class MultiCamTest
{
    static List<string> log = new();
    static int fails;
    static void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
    static string ff = "";
    static readonly CultureInfo inv = CultureInfo.InvariantCulture;
    const int W = 640, H = 360, Bits = 10;
    // where each camera started in the master recording (seconds); camera 3 between frames
    static readonly double[] camStart = { 2.0, 0.5, 3.31 };
    const double CamLen = 14, RecStart = 0.0, RecLen = 20;

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        log = new(); fails = 0;
        ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        AppSettings.ReadOnly = true;
        try { await Body(dir); }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "multicamtest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static string Exec(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
        var err = p.StandardError.ReadToEnd(); p.WaitForExit(); return err;
    }
    static string S(double v) => v.ToString("0.######", inv);
    static bool Near(double a, double b, double tol = 1e-6) => Math.Abs(a - b) < tol;

    /// <summary>The test media (made once): the master recording, three cameras (camera n's number and frame number in the picture, the master's
    /// sound from its start on) and the recorder.</summary>
    internal static (string master, string[] cams, string recorder) EnsureMedia(string dir)
    {
        Directory.CreateDirectory(dir);
        if (ff.Length == 0) ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        var master = Path.Combine(dir, "master.wav");
        if (!File.Exists(master))
            Exec($"-y -v error -f lavfi -i \"anoisesrc=d=24:c=pink:r=48000:a=0.4:seed=7,volume='0.25+0.75*gt(sin(2*PI*t*0.37)+sin(2*PI*t*1.13),0.3)':eval=frame\" -ac 2 -c:a pcm_s16le \"{master}\"");
        var camFiles = new string[3];
        for (int k = 0; k < 3; k++)
        {
            camFiles[k] = Path.Combine(dir, $"cam{k + 1}.mov");
            if (File.Exists(camFiles[k])) continue;
            string band = $"geq=lum='if(lt(Y,96),255*mod(floor(N/pow(2,floor(X/64))),2),if(gt(Y,300),{60 * (k + 1)},128))':cb=128:cr=128";
            Exec($"-y -v error -f lavfi -i color=gray:s={W}x{H}:r=30:d={S(CamLen)} -i \"{master}\" -filter_complex \"[0:v]{band}[v];[1:a]atrim=start={S(camStart[k])}:duration={S(CamLen)},asetpts=N/SR/TB[a]\" " +
                 $"-map [v] -map [a] -c:v libx264 -crf 8 -g 30 -pix_fmt yuv420p -c:a pcm_s16le \"{camFiles[k]}\"");
        }
        var recFile = Path.Combine(dir, "recorder.wav");
        if (!File.Exists(recFile)) Exec($"-y -v error -i \"{master}\" -af \"atrim=start={S(RecStart)}:duration={S(RecLen)},asetpts=N/SR/TB\" -c:a pcm_s16le \"{recFile}\"");
        return (master, camFiles, recFile);
    }
    /// <summary>The camera and frame number in a BGRA picture of a test camera (the Multi-Camera view's cells).</summary>
    internal static (int cam, int frame) DecodeFrame(byte[] px, int w, int h) => Decode(px, w, h, 4, 1);

    static async Task Body(string dir)
    {
        // ---------- media ----------
        var (master, camFiles, recFile) = EnsureMedia(dir);
        var cams = new List<MediaItem>();
        foreach (var f in camFiles) cams.Add(await MediaProbe.ProbeAsync(f) ?? throw new Exception("import " + f + ": " + MediaProbe.LastError));
        var rec = await MediaProbe.ProbeAsync(recFile) ?? throw new Exception("import recorder: " + MediaProbe.LastError);
        Check(cams.All(m => m.HasAudio && Near(m.Fps, 30, 0.01) && m.Width == W) && rec.Type == ClipKind.Audio, "test media: three cameras with sound (30 fps) and a recorder");

        Editor.SetSequence(W, H, 30, ReframeMode.Fit);
        double F = TimeUtil.Fps;
        var ed = new Editor();
        ed.Media.AddRange(cams); ed.Media.Add(rec);

        // ---------- sync by sound ----------
        var lags = new double[3];
        for (int k = 1; k < 3; k++)
        {
            var r = AudioSync.Between(cams[0], cams[k]);
            double want = camStart[0] - camStart[k];
            lags[k] = r?.Lag ?? double.NaN;
            Check(r != null && r.Confidence >= 8 && Math.Abs(r.Lag - want) < 2e-4, $"camera {k + 1} lined up by sound: lag {lags[k] * 1000:0.00} ms (filmed {want * 1000:0.00} ms), confidence {r?.Confidence:0}");
        }
        double[] kk = { 0, -lags[1], -lags[2] };
        int undo0 = ed.UndoLabels.Count();
        var mc = ed.CreateMultiCam("Multicam 01", cams.Select((m, i) => (m, kk[i])).ToList(), new List<(MediaItem, double)>(), -1, "Sound");
        Check(ed.Sequences.Contains(mc) && ed.Seq != mc && mc.MultiCam != null && mc.VideoTracks == 3 && ed.UndoLabels.First() == "Create Multicam Sequence",
              "Create Multi-Camera Source Sequence: a new sequence (not opened), three video tracks, one undo step named “Create Multicam Sequence”");
        Clip CamV(int a) => mc.Clips.First(c => c.Kind == ClipKind.Video && c.Track == Editor.CamRow(mc, a));
        // camera 2 started first: it is at 0; camera 1 at 1.5 s; camera 3 at 2.81 s → on frame 85 (2.8333), 0.0233 s of it in its In
        double t3 = camStart[2] - camStart[1];
        Check(Near(CamV(1).Start, 0) && Near(CamV(0).Start, 1.5, 1e-9) && Near(CamV(2).Start, Math.Ceiling(t3 * F - 1e-6) / F, 1e-9) &&
              Near(CamV(2).In, Math.Ceiling(t3 * F - 1e-6) / F - t3, 3e-4) && Near(CamV(0).In, 0, 3e-4),
              $"cameras placed: 2 at {CamV(1).Start:0.000}, 1 at {CamV(0).Start:0.000}, 3 at {CamV(2).Start:0.0000} (In {CamV(2).In * 1000:0.00} ms) — starts on frames, the rest in In");
        Check(Enumerable.Range(0, 3).All(a => mc.Clips.Any(c => c.Kind == ClipKind.Audio && c.LinkId == CamV(a).LinkId && c.MediaId == CamV(a).MediaId)) &&
              Editor.CamName(mc, 0) == "Camera 1" && Editor.CamName(mc, 2) == "Camera 3",
              "each camera's sound linked to it; tracks named Camera 1 … 3");
        ed.Undo(); bool gone = !ed.Sequences.Contains(mc) && ed.Sequences.All(s => s.MultiCam == null);
        ed.Redo(); mc = ed.Sequences.First(s => s.MultiCam != null);
        Check(gone && mc.Clips.Count == 6, "undo removes the source sequence, redo brings it back");

        // ---------- on the timeline ----------
        var item = ed.SeqItem(mc.Id)!;
        ed.DropMedia(item, Tracks.V1, 1.0, true, true, false);
        List<Clip> Vid() => ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).ToList();
        List<Clip> Snd() => ed.Clips.Where(c => c.Kind == ClipKind.Audio).OrderBy(c => c.Start).ToList();
        string Plan() => string.Join(" ", Vid().Select(c => $"[{c.Start:0.###}-{c.End:0.###} c{c.Cam?.Angle + 1}]"));
        bool Is(params (double a, double b, int cam)[] want)
        {
            var v = Vid();
            return v.Count == want.Length && v.Zip(want).All(p => Near(p.First.Start, p.Second.a, 1e-6) && Near(p.First.End, p.Second.b, 1e-6) && p.First.Cam?.Angle == p.Second.cam - 1
                                                              && p.First.MediaId == cams[p.Second.cam - 1].Id);
        }
        double c3end = Math.Floor((CamV(2).End) * F + 1e-6) / F + 1.0;
        Check(Is((1.0, 2.5, 2), (2.5, 16.5, 1), (16.5, c3end, 3)),
              $"dropped at 1 s: camera 2 until camera 1 has picture, camera 1, then camera 3 after camera 1 ends — {Plan()}");
        Check(Vid().All(v => Snd().Any(a => a.LinkId == v.LinkId && a.MediaId == v.MediaId && Near(a.Start, v.Start) && Near(a.Dur, v.Dur) && a.Cam?.Angle == v.Cam?.Angle)) && Vid().All(v => v.NoAudio),
              "each piece's sound is its camera's, linked (audio follows video)");
        Check(ed.MultiCamClipAt(5) is { } at5 && Near(ed.CamTau(at5, 5) ?? -1, 4) && ed.MultiCamClipAt(0.5) == null, "the multicam clip at a time and its multicam time (timeline − 1 s)");

        // ---------- switching while paused ----------
        var mid = Vid()[1];
        int n = ed.SwitchCamera(new[] { mid }, 2);
        double cut3 = 1.0 + Math.Ceiling(t3 * F - 1e-6) / F;
        Check(n == 1 && Is((1.0, 2.5, 2), (2.5, cut3, 1), (cut3, 16.5, 3), (16.5, c3end, 3)) && ed.UndoLabels.First() == "Multicam Switch",
              $"camera 3 for the long camera-1 piece: cut where camera 3's picture starts ({cut3:0.0000}), camera 1 before it — {Plan()}");
        var sw = Vid()[2];
        Check(Snd().Any(a => a.LinkId == sw.LinkId && a.MediaId == cams[2].Id && Near(a.In, sw.In, 1e-9)), "its sound switched with it (camera 3's, same In)");
        ed.Undo();
        Check(Is((1.0, 2.5, 2), (2.5, 16.5, 1), (16.5, c3end, 3)), "undo: back to three pieces");
        Check(ed.SwitchCamera(new[] { Vid()[0] }, 2) == 0 && Vid().Count == 3, "camera 3 has no picture over the first piece: nothing changes");

        // ---------- recorded switches ----------
        int before = ed.UndoLabels.Count();
        int changed = ed.ApplyCamCuts(Tracks.V1, new[] { (5.0, 1), (7.5, 0), (9.2, 2), (9.2 + 0.2 / F, 1), (11.0, 1) });
        Check(changed == 3 && ed.UndoLabels.Count() == before + 1 && ed.UndoLabels.First() == "Multicam Cuts" &&
              Is((1.0, 2.5, 2), (2.5, 5.0, 1), (5.0, 7.5, 2), (7.5, 9.2, 1), (9.2, 15.0, 2), (15.0, 16.5, 1), (16.5, c3end, 3)),
              $"switches recorded while playing → cuts in one undo step (camera 2 runs out at 15 s: camera 1 stays; two in one frame: the last; the same camera again: no cut) — {Plan()}");

        // ---------- export: every frame shows the right camera and the frame filmed at that moment ----------
        var mp4 = Path.Combine(dir, "multicam.mp4");
        var err = await Task.Run(() => Exporter.RunAsync(ff, ed, mp4, new Progress<double>(), CancellationToken.None, null, null, new ExportSettings { Height = H, Quality = 10 }));
        Check(err == null, "export" + (err == null ? "" : ": " + err));
        if (err == null)
        {
            var got = ReadFrames(mp4);
            int total = 0, okCam = 0, okFrame = 0, okSync = 0; var miss = new List<string>();
            for (int k = 0; k < got.Count; k++)
            {
                double t = k / F;
                var c = ed.Clips.FirstOrDefault(x => x.Kind == ClipKind.Video && t >= x.Start - 1e-9 && t < x.End - 1e-9);
                if (c == null) continue;
                total++;
                int cam = cams.FindIndex(m => m.Id == c.MediaId);
                long want = (long)Math.Ceiling((c.In + (t - c.Start)) * cams[cam].Fps - 1e-3);   // (a plain clip shows its frames from its In on: the first at or after it)
                double filmed = (t - 1.0 + camStart[1] - camStart[cam]) * F;           // the camera's frame at that moment of the recording
                bool a = got[k].cam == cam + 1, b = got[k].frame == want, s = Math.Abs(got[k].frame - filmed) < 1.01;
                if (a) okCam++; if (b) okFrame++; if (s) okSync++;
                if ((!a || !b || !s) && miss.Count < 6) miss.Add($"t={t:0.000}: camera {got[k].cam} frame {got[k].frame} (want {cam + 1} / {want}, filmed {filmed:0.0})");
            }
            Check(total > 400 && okCam == total, $"every exported frame shows the camera of its piece ({okCam} / {total})" + (miss.Count > 0 ? ": " + string.Join("; ", miss) : ""));
            Check(okFrame == total && okSync == total, $"…and that camera's frame of the moment (its frames from its In on: {okFrame} / {total}; within a frame of when it was filmed: {okSync} / {total})");
            // the preview at sample times (around every cut and inside the pieces) = the export's frame
            var pv = new Preview { Ed = ed, Width = Editor.W, Height = Editor.H, Background = System.Windows.Media.Brushes.Black };
            var ks = Vid().SelectMany(c => new[] { (int)Math.Round(c.Start * F), (int)Math.Round(c.Start * F) + 1, (int)Math.Round((c.Start + c.Dur / 2) * F), (int)Math.Round(c.End * F) - 1 })
                          .Where(k => k >= 0 && k < got.Count).Distinct().OrderBy(k => k).ToList();
            int pok = 0; var pmiss = new List<string>();
            foreach (int k in ks)
            {
                double t = k / F;
                var sws = Stopwatch.StartNew();
                do { pv.Sync(t, false); await Task.Delay(15); } while (!pv.Settled(t) && sws.ElapsedMilliseconds < 4000);
                pv.Measure(new System.Windows.Size(Editor.W, Editor.H)); pv.Arrange(new System.Windows.Rect(0, 0, Editor.W, Editor.H)); pv.UpdateLayout();
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(Editor.W, Editor.H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); rtb.Render(pv);
                var px = new byte[Editor.W * Editor.H * 4]; rtb.CopyPixels(px, Editor.W * 4, 0);
                var p = Decode(px, Editor.W, Editor.H, 4, 1);
                if (p == got[k]) pok++; else if (pmiss.Count < 6) pmiss.Add($"t={t:0.000}: preview camera {p.cam} frame {p.frame}, export {got[k].cam} / {got[k].frame}");
            }
            Check(ks.Count > 20 && pok == ks.Count, $"the preview shows the same camera and frame as the export ({pok} / {ks.Count} times: each piece's first, second, middle and last frame)" + (pmiss.Count > 0 ? ": " + string.Join("; ", pmiss) : ""));
        }
        // the sound: audio follows the camera, and every camera recorded the same sound — it runs on through every cut
        var wav = Path.Combine(dir, "multicam.wav");
        err = await Task.Run(() => Exporter.RunAsync(ff, ed, wav, new Progress<double>(), CancellationToken.None, null, null, new ExportSettings { Video = "none", Container = "wav", Audio = "pcm" }));
        if (err == null)
        {
            var outPcm = MonoPcm(wav); var mPcm = MonoPcm(master);
            // 0.3 s windows: across each cut (half before, half after), just before and just after it, and inside pieces
            var points = Vid().Skip(1).Select(c => c.Start).SelectMany(t => new[] { t - 0.15, t - 0.35, t + 0.05 }).Concat(new[] { 3.0, 6.3, 12.0 }).ToList();
            double worst = 0; int bad = 0;
            foreach (var t in points)
            {
                int a0 = (int)Math.Round(t * 48000), b0 = (int)Math.Round((t - 1.0 + camStart[1]) * 48000), len = 14400;
                if (a0 < 0 || a0 + len > outPcm.Length || b0 < 0 || b0 + len > mPcm.Length) continue;
                var r = AudioSync.Lag(mPcm[b0..(b0 + len)], outPcm[a0..(a0 + len)], 48000, 0.01);
                double lagMs = r == null ? 99 : Math.Abs(r.Lag) * 1000;
                worst = Math.Max(worst, lagMs); if (r == null || r.Confidence < 8 || lagMs > 0.25) bad++;
            }
            Check(bad == 0 && points.Count > 10, $"the exported sound = the recording's sound everywhere, also just before / after each cut (worst {worst:0.000} ms over {points.Count} places)");
        }
        else Check(false, "sound export: " + err);

        // ---------- fixed sound: camera 1's, and an external recorder ----------
        {
            var s2 = ed.NewSequence("Fixed sound");
            var mc1 = ed.CreateMultiCam("Multicam fixed", cams.Select((m, i) => (m, kk[i])).ToList(), new List<(MediaItem, double)>(), 0, "Sound");
            ed.DropMedia(ed.SeqItem(mc1.Id)!, Tracks.V1, 0, true, true, false);
            bool soundIsCam1 = Snd().All(a => a.MediaId == cams[0].Id && a.Cam?.Angle == -1);
            bool linked = Vid().Where(v => v.Start >= 1.5 - 1e-6 && v.End <= 15.5 + 1e-6).All(v => Snd().Any(a => a.LinkId == v.LinkId));
            var v0 = Vid().First(v => v.Cam!.Angle == 0);
            ed.SwitchCamera(new[] { v0 }, 1);
            Check(soundIsCam1 && linked && Snd().All(a => a.MediaId == cams[0].Id) && Vid().Any(v => v.Cam!.Angle == 1 && v.Start >= 1.5 - 1e-6),
                  "fixed sound (camera 1's): every sound piece is camera 1's, linked to the pictures it runs under, and stays when the picture switches");
            var mcR = ed.CreateMultiCam("Multicam recorder", cams.Select((m, i) => (m, kk[i])).ToList(),
                                        new List<(MediaItem, double)> { (rec, -(AudioSync.Between(cams[0], rec)?.Lag ?? double.NaN)) }, 3, "Sound");
            var recClip = mcR.Clips.FirstOrDefault(c => c.MediaId == rec.Id);
            Check(mcR.AudioTracks == 4 && recClip != null && recClip.Track == mcR.VideoTracks + 3 && Near(recClip.Start, 0, 1e-9) && Near(mcR.Clips.First(c => c.MediaId == cams[1].Id).Start, 0.5, 1e-3) &&
                  mcR.MultiCam!.Audio == 3, $"a recorder that ran longest: on A4 from 0, camera 2 at {mcR.Clips.First(c => c.MediaId == cams[1].Id).Start:0.000} (it started 0.5 s later)");
            ed.NewSequence("Recorder");
            ed.DropMedia(ed.SeqItem(mcR.Id)!, Tracks.V1, 0, true, true, false);
            var snd = Snd();
            Check(snd.All(a => a.MediaId == rec.Id) && snd.Any(a => a.LinkId == null && Near(a.Start, 0) && Near(a.End, 0.5, 1e-6)) && snd.Any(a => a.LinkId == null && Near(a.End, RecLen, 1e-6)) &&
                  Near(snd.Sum(a => a.Dur), RecLen, 1e-6),
                  $"the recorder's sound throughout: under the pictures (linked) and alone where no camera has picture (0–0.5 s, to {RecLen} s) — {snd.Count} pieces, {snd.Sum(a => a.Dur):0.000} s");
            ed.Activate(ed.Sequences.First(s => s.Name == "Sequence 01"));
        }

        // ---------- flatten, project round trip, duplicate ----------
        {
            int k = ed.FlattenMultiCam(ed.Clips.ToList());
            Check(k == ed.Clips.Count && ed.Clips.All(c => c.Cam == null) && ed.UndoLabels.First() == "Flatten Multicam", $"Flatten: {k} clips forget their source, they stay as they are");
            ed.Undo();
            Check(ed.Clips.All(c => c.Cam != null), "undo: they are multicam clips again");
            var json = JsonSerializer.Serialize(ed.Sequences);
            var back = JsonSerializer.Deserialize<List<Sequence>>(json)!;
            var b1 = back.First(s => s.Id == mc.Id);
            Check(b1.MultiCam is { Audio: -1, Sync: "Sound" } && back.First(s => s.Name == "Sequence 01").Clips.Where(c => c.Kind == ClipKind.Video).All(c => c.Cam?.Seq == mc.Id),
                  "saved and opened: the source keeps its multi-camera settings, the clips their camera");
            var dup = ed.DuplicateSequence(mc);
            Check(dup.MultiCam != null && !ReferenceEquals(dup.MultiCam, mc.MultiCam) && dup.VideoTracks == 3, "Duplicate Sequence keeps a source multi-camera (its own settings)");
        }

        // ---------- timecode ----------
        {
            double? a = MultiCamSync.TimecodeSeconds(new MediaItem { Timecode = "01:00:10:15", Fps = 30 });
            double? b = MultiCamSync.TimecodeSeconds(new MediaItem { Timecode = "00:01:00;02", Fps = 30000.0 / 1001 });
            double? c = MultiCamSync.TimecodeSeconds(new MediaItem { Timecode = "00:10:00;00", Fps = 30000.0 / 1001 });
            Check(a is double x && Near(x, 3610.5) && b is double y && Near(y, 1800 / (30000.0 / 1001)) && c is double z && Near(z, 17982 / (30000.0 / 1001)) && MultiCamSync.TimecodeSeconds(new MediaItem()) == null,
                  $"timecode sync: 01:00:10:15 @30 = {a:0.###} s, drop-frame 00:01:00;02 = {b:0.####} s, 00:10:00;00 = {c:0.###} s (frames 1800 / 17982), none = no sync");
        }
    }

    /// <summary>Every frame's camera number (bottom band: 60 × camera) and frame number (the stripe).</summary>
    static List<(int cam, int frame)> ReadFrames(string file)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-i", file, "-f", "rawvideo", "-pix_fmt", "gray", "-s", $"{W}x{H}", "pipe:1" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        var res = new List<(int, int)>(); var buf = new byte[W * H]; var s = p.StandardOutput.BaseStream;
        while (true)
        {
            int got = 0; while (got < buf.Length) { int k = s.Read(buf, got, buf.Length - got); if (k <= 0) break; got += k; }
            if (got < buf.Length) break;
            res.Add(Decode(buf, W, H, 1, 0));
        }
        p.WaitForExit();
        return res;
    }

    /// <summary>A frame's camera (the bottom band's level: 60 / 120 / 180 as video levels, 51 / 121 / 191 full range) and frame number (the stripe).
    /// Pixel stride / channel: gray = 1 / 0, BGRA = 4 / 1.</summary>
    static (int cam, int frame) Decode(byte[] px, int w, int h, int stride, int ch)
    {
        int v = 0, y = (int)(h * 48.0 / 360);
        for (int b = 0; b < Bits; b++) if (px[(y * w + (int)((b + 0.5) * w / Bits)) * stride + ch] > 128) v |= 1 << b;
        int yb = (int)(h * 330.0 / 360), x0 = (int)(w * 200.0 / 640), x1 = (int)(w * 440.0 / 640);
        double band = 0; for (int x = x0; x < x1; x++) band += px[(yb * w + x) * stride + ch];
        band /= x1 - x0;
        return (band < 30 ? 0 : band < 86 ? 1 : band < 156 ? 2 : 3, v);
    }

    static float[] MonoPcm(string file)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-v", "error", "-i", file, "-f", "f32le", "-ac", "1", "-ar", "48000", "pipe:1" }) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var ms = new MemoryStream(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        var bytes = ms.ToArray(); var r = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, r, 0, r.Length * 4);
        return r;
    }
}
