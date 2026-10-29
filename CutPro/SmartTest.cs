using System.Diagnostics;
using System.Globalization;

namespace CutPro;

/// <summary>
/// CutPro.exe --smarttest dir: smart rendering, headless. Test files carry their frame number in a stripe (H.264 and HEVC with a key frame every
/// 30 frames, closed GOPs; an HEVC file with open GOPs). A timeline with cuts inside GOPs, a title, a cross-dissolve, a fade and a razor cut through
/// continuous footage is exported normally and smart:
/// - the files are probed (codec, key frames, closed GOPs; open GOPs refused with the reason);
/// - the plan copies whole GOPs where one plain clip shows and encodes the rest;
/// - every frame of the smart file = the normal export's (frame numbers), copied frames are the source's own bytes, nothing fails to decode;
/// - the sound = the normal export's, chapters are there;
/// - HEVC likewise; open-GOP / other-size files: nothing copied, the export still right;
/// - a long plain timeline: the smart export is much faster.
/// </summary>
public static class SmartTest
{
    static List<string> log = new();
    static int fails;
    static void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
    static string ff = "";
    const int Bits = 10, W = 640, H = 360;

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        log = new(); fails = 0;
        ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        AppSettings.ReadOnly = true;
        try { await Body(dir); }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "smarttest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static void Exec(string args) { using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!; p.StandardError.ReadToEnd(); p.WaitForExit(); }

    static string Gen(string dir, string name, string venc, double dur = 20, int w = W, int h = H)
    {
        var f = Path.Combine(dir, name);
        if (File.Exists(f)) return f;
        string band = $"geq=lum='if(lt(Y,{h * 96 / 360}),255*mod(floor(N/pow(2,floor(X/{w / 10}))),2),80+60*sin(X/40+N/9))':cb=128:cr=128";
        Exec($"-y -v error -f lavfi -i color=gray:s={w}x{h}:r=30:d={dur} -f lavfi -i \"sine=frequency=440:sample_rate=48000:duration={dur}\" -vf \"{band}\" {venc} -pix_fmt yuv420p -c:a aac -b:a 192k -shortest \"{f}\"");
        return f;
    }

    static async Task Body(string dir)
    {
        var h264 = await MediaProbe.ProbeAsync(Gen(dir, "smart_h264.mp4", "-c:v libx264 -crf 14 -g 30 -keyint_min 30 -sc_threshold 0 -bf 2")) ?? throw new Exception("h264: " + MediaProbe.LastError);
        var hevc = await MediaProbe.ProbeAsync(Gen(dir, "smart_hevc.mp4", "-c:v libx265 -crf 18 -x265-params keyint=30:min-keyint=30:scenecut=0:open-gop=0:log-level=error -tag:v hvc1")) ?? throw new Exception("hevc: " + MediaProbe.LastError);
        var hevcOpen = await MediaProbe.ProbeAsync(Gen(dir, "smart_hevc_open.mp4", "-c:v libx265 -crf 18 -x265-params keyint=30:min-keyint=30:scenecut=0:open-gop=1:bframes=4:log-level=error -tag:v hvc1")) ?? throw new Exception("hevc open: " + MediaProbe.LastError);

        // ---------- probing ----------
        var i1 = SmartRender.Probe(h264); var i2 = SmartRender.Probe(hevc); var i3 = SmartRender.Probe(hevcOpen);
        Check(i1.Why == "" && i1.Codec == "h264" && i1.Frames == 600 && i1.Keys.Length == 20 && i1.Keys.All(k => k % 30 == 0) && Math.Abs(i1.Fps - 30) < 1e-9,
              $"H.264 file: {i1.Frames} frames, {i1.Keys.Length} key frames every 30, closed GOPs ({(i1.Why == "" ? "can be copied" : i1.Why)})");
        Check(i2.Why == "" && i2.Codec == "hevc" && i2.Keys.Length == 20, $"HEVC file (closed GOPs): {i2.Keys.Length} key frames ({(i2.Why == "" ? "can be copied" : i2.Why)})");
        Check(i3.Why == "" && i3.Keys.Length < 20 && i3.Keys.Length > 0, $"HEVC with open GOPs: only its clean key frames are copy points ({i3.Keys.Length} of 20: none that frames after it reach back across)");

        // ---------- a timeline ----------
        Editor.SetSequence(W, H, 30, ReframeMode.Fit);
        double F = TimeUtil.Fps;
        Editor Timeline(MediaItem m)
        {
            var ed = new Editor(); ed.Media.Add(m);
            void V(double start, double inPt, double dur, Action<Clip>? f = null)
            {
                var link = Guid.NewGuid().ToString("N");
                var v = new Clip { MediaId = m.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = start, In = inPt, Dur = dur, LinkId = link, NoAudio = true };
                f?.Invoke(v);
                ed.Clips.Add(v);
                ed.Clips.Add(new Clip { MediaId = m.Id, Kind = ClipKind.Audio, Track = Tracks.A1, Start = start, In = inPt, Dur = dur, LinkId = link });
            }
            V(0, 2.0, 4);                                                             // A: from a key frame to a key frame
            V(4, 9.0, 3.5);                                                           // B: a cut inside a GOP at its end; a title over part of it
            V(7.5, 1.5, 3.5, c => c.TransIn = new Transition { Kind = "Cross Dissolve", Dur = 1 });   // C: a dissolve from B
            V(11, 4.0, 4, c => c.FadeOut = 0.5);                                      // D: fades out
            V(15, 8.0, 2); V(17, 10.0, 2);                                            // E | F: a razor cut through continuous footage
            ed.Clips.Add(new Clip { Kind = ClipKind.Text, Track = Tracks.V(2), Start = 5, Dur = 1, Text = "TITLE", Style = new TextStyle { Size = 60 } });
            ed.Markers.Add(new Marker { T = 0, Name = "Start", Chapter = true }); ed.Markers.Add(new Marker { T = 11, Name = "Later", Chapter = true });
            return ed;
        }

        foreach (var (m, codec, tag) in new[] { (h264, "h264", "H.264"), (hevc, "hevc", "HEVC") })
        {
            var ed = Timeline(m);
            var o = new ExportSettings { Video = codec, Quality = 16, Smart = true };
            var parts = SmartRender.Plan(ed, o, 0, ed.End);
            var inf = SmartRender.Probe(m);
            int copied = parts.Where(p => p.Copy).Sum(p => p.K1 - p.K0), total = parts[^1].K1;
            bool whole = parts.Where(p => p.Copy).All(p => p.I0 % 30 == 0 && ((p.I0 + p.K1 - p.K0) % 30 == 0 || p.I0 + p.K1 - p.K0 == inf.Frames));
            bool covers = parts.Zip(parts.Skip(1)).All(x => x.First.K1 == x.Second.K0) && parts[0].K0 == 0 && total == (int)Math.Round(ed.End * F);
            bool titleEncoded = parts.Any(p => !p.Copy && p.K0 <= 150 && p.K1 >= 180), razorCopied = parts.Any(p => p.Copy && p.K0 <= 450 && p.K1 >= 570);
            Check(whole && covers && titleEncoded && razorCopied && copied > total / 2,
                  $"{tag} plan: {parts.Count} parts, {copied} of {total} frames copied in whole GOPs (the title, the dissolve and the fade encoded; the razor cut through continuous footage copied straight through)");

            var normal = Path.Combine(dir, $"out_normal_{codec}.mp4"); var smart = Path.Combine(dir, $"out_smart_{codec}.mp4");
            var on = new ExportSettings { Video = codec, Quality = 16 };
            var sw = Stopwatch.StartNew();
            var titles0 = TitleVisual.PrepareExport(ed, Path.Combine(dir, "t_" + codec));
            var e1 = await Task.Run(() => Exporter.RunAsync(ff, ed, normal, new Progress<double>(), CancellationToken.None, titles0, null, on));
            double tn = sw.Elapsed.TotalSeconds; sw.Restart();
            var titles = TitleVisual.PrepareExport(ed, Path.Combine(dir, "ts_" + codec));
            var e2 = await Task.Run(() => Exporter.RunAsync(ff, ed, smart, new Progress<double>(), CancellationToken.None, titles, null, o));
            double ts = sw.Elapsed.TotalSeconds;
            Check(e1 == null && e2 == null && o.SmartReport?.StartsWith("Smart render:") == true && o.SmartReport.Contains("copied ("),
                  $"{tag}: normal export ({tn:0.0} s) and smart export ({ts:0.0} s): “{o.SmartReport}”" + ((e1 ?? e2) is { } err ? " — " + err : ""));
            if (e1 != null || e2 != null) continue;
            var a = Stripes(normal); var b = Stripes(smart);
            int same = a.Zip(b).Count(x => x.First == x.Second);
            Check(a.Count == total && b.Count == total && same == total, $"{tag}: every frame of the smart file = the normal export's ({same} / {total} frame numbers)");
            // copied frames are the source's own (decoded bytes identical); the decoder finds nothing wrong
            var cp = parts.First(p => p.Copy && p.K1 - p.K0 >= 30);
            int k = cp.K0 + 7, srcFrame = cp.I0 + 7;
            var fromSmart = FrameBytes(smart, k); var fromSrc = FrameBytes(m.Path, srcFrame);
            Check(fromSmart.Length > 0 && fromSmart.AsSpan().SequenceEqual(fromSrc), $"{tag}: a copied frame (timeline frame {k}) is the source's frame {srcFrame}, byte for byte");
            var dec = SmartRender.Problems(RunErr(new[] { "-v", "error", "-i", smart, "-f", "null", "-" }));
            Check(dec.Length == 0, $"{tag}: the smart file decodes without an error" + (dec.Length > 0 ? ": " + dec : ""));
            // sound and chapters
            var pa = Pcm(normal); var pb = Pcm(smart);
            double se = 0, sig = 0; int nS = Math.Min(pa.Length, pb.Length);
            for (int i = 0; i < nS; i++) { double d = pa[i] - pb[i]; se += d * d; sig += pa[i] * pa[i]; }
            double snr = se <= 1e-12 ? 99 : 10 * Math.Log10(sig / se);
            Check(nS > 48000 * 2 * 18 && Math.Abs(pa.Length - pb.Length) < 48000 && snr > 60, $"{tag}: the sound = the normal export's ({snr:0.0} dB)");
            int chaps = RunOut(MediaProbe.FindFfprobe()!, new[] { "-v", "error", "-show_chapters", "-of", "csv=p=0", smart }).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            Check(chaps == 2, $"{tag}: the chapters are in the smart file ({chaps})");
        }

        // ---------- open GOPs (copies between clean key frames only); a file that can't be copied ----------
        {
            var ed = Timeline(hevcOpen);
            var o = new ExportSettings { Video = "hevc", Quality = 16, Smart = true };
            var outF = Path.Combine(dir, "out_smart_hevc_open.mp4");
            var tOpen = TitleVisual.PrepareExport(ed, Path.Combine(dir, "t_open"));
            var err = await Task.Run(() => Exporter.RunAsync(ff, ed, outF, new Progress<double>(), CancellationToken.None, tOpen, null, o));
            var got = Stripes(outF);
            var want = Expected(ed, hevcOpen, (int)Math.Round(ed.End * F));
            int ok = got.Zip(want).Count(x => x.Second < 0 || x.First == x.Second);
            var dec = SmartRender.Problems(RunErr(new[] { "-v", "error", "-i", outF, "-f", "null", "-" }));
            Check(err == null && got.Count == want.Count && ok == want.Count && dec.Length == 0 && o.SmartReport?.Contains("% of the frames copied") == true,
                  $"open GOPs: “{o.SmartReport}” — every frame right ({ok} / {want.Count}), decodes cleanly");
            var ed3 = new Editor(); var red = await MediaProbe.ProbeAsync(Gen(dir, "smart_720.mp4", "-c:v libx264 -crf 20 -g 30", 6, 1280, 720)) ?? throw new Exception("720: " + MediaProbe.LastError);
            ed3.Media.Add(red);
            ed3.Clips.Add(new Clip { MediaId = red.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = 0, In = 0, Dur = 5, NoAudio = true });
            var o3 = new ExportSettings { Video = "h264", Quality = 16, Smart = true };
            var err3 = await Task.Run(() => Exporter.RunAsync(ff, ed3, Path.Combine(dir, "out_smart_720.mp4"), new Progress<double>(), CancellationToken.None, null, null, o3));
            Check(err3 == null && o3.SmartReport?.Contains("nothing could be copied") == true && o3.SmartReport.Contains("1280×720"),
                  $"a file of another size than the sequence: “{o3.SmartReport}”");
            var o2 = new ExportSettings { Video = "h264", Quality = 16, Smart = true, Height = 240 };
            var ed2 = Timeline(h264);
            var parts2 = SmartRender.Plan(ed2, o2, 0, ed2.End);
            Check(parts2.All(p => !p.Copy), "an export at another size (240 lines): nothing can be copied");
        }

        // ---------- a long plain timeline (1080p, the default preset): much faster ----------
        {
            Editor.SetSequence(1920, 1080, 30, ReframeMode.Fit);
            var lm = await MediaProbe.ProbeAsync(Gen(dir, "smart_long1080.mp4", "-c:v libx264 -preset veryfast -crf 20 -g 60 -keyint_min 60 -sc_threshold 0", 90, 1920, 1080)) ?? throw new Exception("long: " + MediaProbe.LastError);
            var ed = new Editor(); ed.Media.Add(lm);
            double at = 0;
            foreach (var (inPt, dur) in new[] { (0.0, 20.0), (25.0, 20.0), (50.0, 20.0), (72.0, 15.0) })
            {
                var link = Guid.NewGuid().ToString("N");
                ed.Clips.Add(new Clip { MediaId = lm.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = at, In = inPt, Dur = dur, LinkId = link, NoAudio = true });
                ed.Clips.Add(new Clip { MediaId = lm.Id, Kind = ClipKind.Audio, Track = Tracks.A1, Start = at, In = inPt, Dur = dur, LinkId = link });
                at += dur;
            }
            var sw = Stopwatch.StartNew();
            var e1 = await Task.Run(() => Exporter.RunAsync(ff, ed, Path.Combine(dir, "out_long_normal.mp4"), new Progress<double>(), CancellationToken.None, null, null, new ExportSettings { Quality = 18 }));
            double tn = sw.Elapsed.TotalSeconds; sw.Restart();
            var o = new ExportSettings { Quality = 18, Smart = true };
            var e2 = await Task.Run(() => Exporter.RunAsync(ff, ed, Path.Combine(dir, "out_long_smart.mp4"), new Progress<double>(), CancellationToken.None, null, null, o));
            double ts = sw.Elapsed.TotalSeconds;
            Check(e1 == null && e2 == null && ts * 2.5 < tn && o.SmartReport?.Contains("% of the frames copied") == true,
                  $"75 s of 1080p cut from one file: normal {tn:0.0} s, smart {ts:0.0} s ({tn / Math.Max(0.01, ts):0.0}× faster) — “{o.SmartReport}”");
        }
    }

    /// <summary>The source frame each timeline frame shows (the plain-clip rule; −1 where a title / transition / fade changes it or nothing shows).</summary>
    static List<int> Expected(Editor ed, MediaItem m, int n)
    {
        double F = TimeUtil.Fps; var res = new List<int>();
        var plan = Transitions.Compute(ed);
        for (int k = 0; k < n; k++)
        {
            double t = k / F;
            var vs = ed.Clips.Where(c => c.Kind != ClipKind.Audio && plan.Shows(c, t)).ToList();
            if (vs.Count != 1 || vs[0].Kind != ClipKind.Video || vs[0].FadeOut > 0 && t >= vs[0].End - vs[0].FadeOut - 1e-9 || plan.Wins.Any(w => t >= w.T0 - 1e-9 && t < w.T1 - 1e-9)) { res.Add(-1); continue; }
            var c = vs[0];
            res.Add((int)Math.Ceiling((c.In + (t - c.Start)) * m.Fps - 1e-3));
        }
        return res;
    }

    static List<int> Stripes(string file)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-i", file, "-f", "rawvideo", "-pix_fmt", "gray", "-s", $"{W}x{H}", "pipe:1" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        var res = new List<int>(); var buf = new byte[W * H]; var s = p.StandardOutput.BaseStream;
        while (true)
        {
            int got = 0; while (got < buf.Length) { int n = s.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
            if (got < buf.Length) break;
            int v = 0, y = H * 48 / 360;
            for (int b = 0; b < Bits; b++) if (buf[y * W + (int)((b + 0.5) * W / Bits)] > 128) v |= 1 << b;
            res.Add(v);
        }
        p.WaitForExit();
        return res;
    }

    /// <summary>Frame number k of a file, decoded to raw YUV (the codec's own output, no conversion).</summary>
    static byte[] FrameBytes(string file, int k)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-i", file, "-vf", $"select=eq(n\\,{k})", "-frames:v", "1", "-f", "rawvideo", "pipe:1" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var ms = new MemoryStream(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        return ms.ToArray();
    }

    static float[] Pcm(string file)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-v", "error", "-i", file, "-map", "0:a:0", "-f", "f32le", "-ac", "2", "-ar", "48000", "pipe:1" }) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var ms = new MemoryStream(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        var bytes = ms.ToArray(); var r = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, r, 0, r.Length * 4);
        return r;
    }

    static string RunErr(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEnd(); p.WaitForExit(); _ = o.Result;
        return e;
    }
    static string RunOut(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var e = p.StandardError.ReadToEndAsync(); var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); _ = e.Result;
        return o;
    }
}
