using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>CutPro.exe --speedtest dir: speed / reverse / frame hold / time remapping.
/// The test videos carry their frame number as a 10-bit stripe, so every frame of the export and of the preview can be read back:
/// - export: each of its frames is exactly the native frame <see cref="TimeMap.NativeIndex"/> picks (50 %, 200 %, 37 %, reversed at
///   100 % and 150 %, held, a speed ramp, a 24 fps source at 75 % and a 60 fps source at 250 %, a 29.97 sequence);
/// - preview: the same frames before the remap is rendered (the media on its native grid) and after (the rendered file);
/// - sound: 200 % keeps a 440 Hz tone at 440 Hz (and doubles it without pitch lock), reverse runs a rising sweep downwards, the
///   export's sound = the mixer's sample for sample;
/// - edits: razor cuts, head trims and transition handles keep the frames; reverse = the forward frames backwards; undo.</summary>
public static class SpeedTest
{
    const int Bits = 10, SrcW = 640, SrcH = 360;

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        try
        {
            string Gen(string name, int fps, double dur, string audio)
            {
                var f = Path.Combine(dir, name);
                if (File.Exists(f)) return f;
                string band = $"geq=lum='if(lt(Y,96),255*mod(floor(N/pow(2,floor(X/64))),2),128)':cb=128:cr=128";
                Run(ff, $"-y -v error -f lavfi -i color=gray:s={SrcW}x{SrcH}:r={fps}:d={dur} -f lavfi -i \"{audio}\" -vf \"{band}\" -c:v libx264 -crf 8 -g 30 -pix_fmt yuv420p -c:a aac -b:a 256k -shortest \"{f}\"");
                return f;
            }
            var v30 = await MediaProbe.ProbeAsync(Gen("count30.mp4", 30, 14, "sine=frequency=440:sample_rate=48000:duration=14")) ?? throw new Exception("import 30");
            var v24 = await MediaProbe.ProbeAsync(Gen("count24.mp4", 24, 8, "sine=frequency=440:sample_rate=48000:duration=8")) ?? throw new Exception("import 24");
            var v60 = await MediaProbe.ProbeAsync(Gen("count60.mp4", 60, 10, "sine=frequency=440:sample_rate=48000:duration=10")) ?? throw new Exception("import 60");
            Check(Math.Abs(v30.Fps - 30) < 0.01 && Math.Abs(v24.Fps - 24) < 0.01 && Math.Abs(v60.Fps - 60) < 0.01, $"test videos: {v30.Fps:0.##} / {v24.Fps:0.##} / {v60.Fps:0.##} fps");

            foreach (double seqFps in new[] { 30.0, 30000.0 / 1001 })
            {
                Editor.SetSequence(SrcW, SrcH, seqFps, ReframeMode.Fit);
                double F = TimeUtil.Fps;
                double Fr(double t) => Math.Round(t * F) / F;
                var ed = new Editor(); ed.Media.AddRange(new[] { v30, v24, v60 });
                Clip V(MediaItem m, double start, double inPt, double dur, double speed = 100, bool rev = false, bool hold = false)
                {
                    var c = new Clip { MediaId = m.Id, Kind = ClipKind.Video, Track = 2, Start = Fr(start), In = inPt, Dur = Fr(dur), Speed = speed, Reverse = rev, Hold = hold, NoAudio = true };
                    ed.Clips.Add(c); return c;
                }
                V(v30, 0, 1.0, 2.0, 50);
                V(v30, 2, 3.0, 1.5, 200);
                V(v30, 3.5, 7.5 - 1 / 30.0, 1.5, 100, rev: true);
                V(v30, 5, 12.0, 1, 150, rev: true);
                V(v30, 6, 4.0, 1, hold: true);
                var ramp = V(v30, 7, 0.5, 2);
                Anim.SetKey(ramp, AnimProp.Speed, 0.5, 100); Anim.SetKey(ramp, AnimProp.Speed, 1.5, 300); Anim.SetKey(ramp, AnimProp.Speed, 4.0, 50);
                V(v30, 9, 2.0, 1, 37);
                V(v24, 10, 1.0, 1.5, 75);
                V(v60, 11.5, 2.0, 1.5, 250);
                string tag = Math.Abs(F - 30) < 1e-6 ? "30 fps" : "29.97 fps";

                // expected: the native frame for every output frame
                int n = (int)Math.Round(ed.End * F);
                var want = new int[n];
                for (int k = 0; k < n; k++)
                {
                    double t = k / F;
                    var c = ed.Clips.FirstOrDefault(x => t >= x.Start - 1e-9 && t < x.End - 1e-9);
                    want[k] = c == null ? -1 : (int)TimeMap.NativeIndex(c, ed.MediaOf(c)!, t);
                }

                // preview: before the remap renders (fallback: the media on its native grid) and after (the rendered file)
                var pv = new Preview { Ed = ed, Width = Editor.W, Height = Editor.H, Background = Brushes.Black };
                var samples = Enumerable.Range(0, n).Where(k => k % 7 == 3 && want[k] >= 0).ToList();
                async Task<(int ok, int total, List<string> miss)> PreviewPass()
                {
                    int ok = 0; var miss = new List<string>();
                    foreach (int k in samples)
                    {
                        double t = k / F;
                        var sws = Stopwatch.StartNew();
                        do { pv.Sync(t, false); await Task.Delay(15); } while (!pv.Settled(t) && sws.ElapsedMilliseconds < 4000);
                        pv.Measure(new Size(Editor.W, Editor.H)); pv.Arrange(new Rect(0, 0, Editor.W, Editor.H)); pv.UpdateLayout();
                        var rtb = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
                        var px = new byte[Editor.W * Editor.H * 4]; rtb.CopyPixels(px, Editor.W * 4, 0);
                        int v = Decode(px, Editor.W, Editor.H, 4, 1);
                        if (v == want[k]) ok++; else if (miss.Count < 6) miss.Add($"frame {k}: {v} not {want[k]}");
                    }
                    return (ok, samples.Count, miss);
                }
                if (Math.Abs(F - 30) > 1e-6)
                {
                    Remap.Background = false;                                  // (nothing rendered yet at 29.97: the fallback shows)
                    var (ok1, tot1, miss1) = await PreviewPass();
                    Remap.Background = true;
                    Check(ok1 == tot1, $"{tag}: preview before rendering (the media on its native frame grid) = the time map ({ok1}/{tot1}){(miss1.Count > 0 ? " — " + string.Join(", ", miss1) : "")}");
                }

                // export
                var mp4 = Path.Combine(dir, $"speed_{(int)Math.Round(F * 100)}.mp4");
                var sw = Stopwatch.StartNew();
                var err = await Exporter.RunAsync(ff, ed, mp4, new Progress<double>(), CancellationToken.None, null, null,
                                                  new ExportSettings { Quality = 12, Audio = "none" });
                Check(err == null, $"{tag}: export with every kind of speed change ({sw.Elapsed.TotalSeconds:0.0} s, rendering included)" + (err == null ? "" : ": " + err));
                if (err == null)
                {
                    var got = ReadExport(ff, mp4);
                    int bad = 0; var first = new List<string>();
                    for (int k = 0; k < Math.Min(n, got.Count); k++)
                        if (want[k] >= 0 && got[k] != want[k]) { bad++; if (first.Count < 6) first.Add($"frame {k}: {got[k]} not {want[k]}"); }
                    Check(bad == 0 && Math.Abs(got.Count - n) <= 1, $"{tag}: export frames = the time map's native frames ({n - bad}/{n} exact, {got.Count} frames){(first.Count > 0 ? " — " + string.Join(", ", first) : "")}");
                }

                if (Math.Abs(F - 30) < 1e-6)
                {
                    // the export rendered every remap: the preview now plays those files
                    var (ok2, tot2, miss2) = await PreviewPass();
                    Check(ok2 == tot2, $"{tag}: preview from the rendered remaps = the time map ({ok2}/{tot2}){(miss2.Count > 0 ? " — " + string.Join(", ", miss2) : "")}");
                }
            }

            // edits keep the frames (pure time-map checks at 30 fps)
            Editor.SetSequence(SrcW, SrcH, 30, ReframeMode.Fit);
            {
                double F = 30;
                var e = new Editor(); e.Media.Add(v30);
                var kinds = new (string name, Clip c)[]
                {
                    ("50 %", new Clip { MediaId = v30.Id, Kind = ClipKind.Video, Track = 2, Start = 1, In = 2, Dur = 3, Speed = 50 }),
                    ("reversed 150 %", new Clip { MediaId = v30.Id, Kind = ClipKind.Video, Track = 2, Start = 1, In = 9, Dur = 3, Speed = 150, Reverse = true }),
                    ("37 %", new Clip { MediaId = v30.Id, Kind = ClipKind.Video, Track = 2, Start = 1, In = 2, Dur = 3, Speed = 37 }),
                };
                foreach (var (name, c0) in kinds)
                {
                    e.Clips.Clear(); var c = Json(c0); e.Clips.Add(c);
                    var all = Remap.Frames(c, v30, c.Start, c.End);
                    e.CutClips(new[] { c }, 2.5);
                    var parts = e.Clips.OrderBy(x => x.Start).SelectMany(x => Remap.Frames(x, v30, x.Start, x.End)).ToArray();
                    Check(parts.SequenceEqual(all), $"{name}: a razor cut keeps every frame ({all.Length})");
                    // head trim by 10 frames: the rest is unchanged
                    var t = Json(c0); double d = 10 / F;
                    t.In = TimeMap.SrcAt(t, t.Start, t.In, t.Start + d); t.Start += d; t.Dur -= d;
                    Check(Remap.Frames(t, v30, t.Start, t.End).SequenceEqual(all.Skip(10)), $"{name}: trimming the head keeps the other frames");
                    // a transition handle before the clip shows the frames just before it in its direction
                    var h = Remap.Frames(Json(c0), v30, c0.Start - 0.5, c0.End);
                    Check(h.Skip(15).SequenceEqual(all), $"{name}: transition handles extend the same frame sequence");
                }
                // reverse = the forward frames backwards; Speed/Duration keeps the source range; ripple / no ripple; undo
                var fw = new Clip { MediaId = v30.Id, Kind = ClipKind.Video, Track = 2, Start = 0, In = 2, Dur = 2, Speed = 80 };
                var nx = new Clip { MediaId = v30.Id, Kind = ClipKind.Video, Track = 2, Start = 2.5, In = 0, Dur = 1 };
                e.Clips.Clear(); e.Clips.Add(fw); e.Clips.Add(nx);
                var fwd = Remap.Frames(fw, v30, fw.Start, fw.End);
                e.SetSpeed(new[] { fw }, null, null, true, true, false);
                var rev = Remap.Frames(fw, v30, fw.Start, fw.End);
                Check(rev.SequenceEqual(fwd.Reverse()) && Math.Abs(fw.Dur - 2) < 1e-9, "reverse plays the same frames backwards");
                e.SetSpeed(new[] { fw }, 40, null, false, true, false);
                Check(Math.Abs(fw.Dur - 2.5) < 1e-9 && Math.Abs(fw.Speed - 40) < 1e-9 && Math.Abs(nx.Start - 2.5) < 1e-9, $"40 % without ripple: stops at the next clip ({fw.Dur:0.###} s, next at {nx.Start:0.##})");
                e.Undo();
                fw = e.Clips.First(x => x.Start == 0); nx = e.Clips.First(x => x.Start > 1);   // (undo restores copies)
                e.SetSpeed(new[] { fw }, 40, null, false, true, true);
                Check(Math.Abs(fw.Dur - 4) < 1e-9 && Math.Abs(nx.Start - 4.5) < 1e-9 && Math.Abs(TimeMap.Span(fw).lo - 2) < 1e-9 && Math.Abs(TimeMap.Span(fw).hi - 3.6) < 1e-9,
                      $"40 % with ripple: twice as long, same source range, the next clip moves ({fw.Dur:0.###} s, next at {nx.Start:0.##})");
                e.Undo(); e.Undo();
                Check(!e.Clips[0].Reverse && Math.Abs(e.Clips[0].Speed - 80) < 1e-9, "undo restores speed and direction");
                // frame holds
                var hc = new Clip { MediaId = v30.Id, Kind = ClipKind.Video, Track = 2, Start = 0, In = 1, Dur = 4 };
                e.Clips.Clear(); e.Clips.Add(hc); e.Clips.Add(new Clip { MediaId = v30.Id, Kind = ClipKind.Video, Track = 2, Start = 4, In = 0, Dur = 1 });
                long atT = TimeMap.NativeIndex(hc, v30, 1.5);
                e.AddFrameHold(hc, 1.5);
                var held = e.Clips.Single(x => x.Hold);
                Check(Math.Abs(held.Start - 1.5) < 1e-9 && Remap.Frames(held, v30, held.Start, held.End).All(x => x == atT), $"Add Frame Hold: frame {atT} from the playhead to the clip's end");
                e.Undo();
                e.InsertFrameHold(e.Clips[0], 2.0);
                var ins = e.Clips.Single(x => x.Hold);
                Check(Math.Abs(ins.Start - 2) < 1e-9 && Math.Abs(ins.Dur - 2) < 1e-9 && e.Clips.Count(x => x.Start >= 4 - 1e-9) == 2 && Math.Abs(e.End - 7) < 1e-9,
                      $"Insert Frame Hold Segment: a 2 s still at the playhead, the rest moves later (ends at {e.End:0.##} s)");
            }

            // sound: pitch lock, tape-style speed, reverse; the export's sound = the mixer's
            Editor.SetSequence(SrcW, SrcH, 30, ReframeMode.Fit);
            {
                var sweepF = Path.Combine(dir, "sweep.wav");
                if (!File.Exists(sweepF)) Run(ff, $"-y -v error -f lavfi -i \"aevalsrc=exprs='0.4*sin(2*PI*(200*t+100*t*t))':s=48000:d=6\" -ac 2 \"{sweepF}\"");
                var sweep = await MediaProbe.ProbeAsync(sweepF) ?? throw new Exception("sweep");
                var ea = new Editor(); ea.Media.AddRange(new[] { v30, sweep });
                var a1 = new Clip { MediaId = v30.Id, Kind = ClipKind.Audio, Track = 3, Start = 0, In = 1, Dur = 2, Speed = 200 };
                var a2 = new Clip { MediaId = v30.Id, Kind = ClipKind.Audio, Track = 3, Start = 2, In = 1, Dur = 2, Speed = 200, PitchLock = false };
                var a3 = new Clip { MediaId = sweep.Id, Kind = ClipKind.Audio, Track = 4, Start = 4.5, In = 5.0, Dur = 4, Reverse = true };
                ea.Clips.AddRange(new[] { a1, a2, a3 });
                var srcs = Mixer.Sources(ea, Transitions.Compute(ea), new()).ToArray();
                foreach (var s in srcs) while (!AudioCache.IsDone(s.Path)) await Task.Delay(20);
                float[] Mix(double t0, double t1)
                {
                    var l = new List<float>();
                    Mixer.Render(srcs, 1, t0, t1, (b, k) => { for (int i = 0; i < k * 2; i += 2) l.Add(b[i] / 32768f); });
                    return l.ToArray();
                }
                double f1 = Freq(Mix(0.2, 1.8)), f2 = Freq(Mix(2.2, 3.8));
                Check(Math.Abs(f1 - 440) < 8 && Math.Abs(f2 - 880) < 12, $"200 %: a 440 Hz tone stays {f1:0} Hz with pitch kept, becomes {f2:0} Hz without");
                var rv = Mix(4.5, 8.5);
                double fa = Freq(rv[(int)(0.2 * 48000)..(int)(1.0 * 48000)]), fb = Freq(rv[(int)(3.0 * 48000)..(int)(3.8 * 48000)]);
                Check(fa > fb * 1.5, $"reversed sweep runs downwards: {fa:0} Hz → {fb:0} Hz");
                long derivedLen = AudioCache.Frames(srcs[0].Path);
                Check(Math.Abs(derivedLen - 2 * 48000) < 48000 * 0.01, $"rendered 200 % sound lasts the clip: {derivedLen / 48000.0:0.000} s");
                var wav = Path.Combine(dir, "speed_sound.wav");
                var aerr = await Exporter.RunAsync(ff, ea, wav, new Progress<double>(), CancellationToken.None, null, null, new ExportSettings { Video = "none", Container = "wav", Audio = "pcm" });
                if (aerr == null)
                {
                    var exp = Pcm(ff, wav); var mix = new List<float>();
                    Mixer.Render(srcs, 1, 0, ea.End, (b, k) => { for (int i = 0; i < k * 2; i++) mix.Add(b[i] / 32768f); });
                    double e2 = 0, s2 = 0; int len = Math.Min(exp.Length, mix.Count);
                    for (int i = 0; i < len; i++) { double d = exp[i] - mix[i]; e2 += d * d; s2 += mix[i] * mix[i]; }
                    double snr = 10 * Math.Log10(s2 / Math.Max(1e-30, e2));
                    Check(snr > 60 && Math.Abs(exp.Length - mix.Count) <= 4, $"export sound = the mixer's ({snr:0.0} dB, {exp.Length / 2} vs {mix.Count / 2} frames)");
                }
                else Check(false, "sound export: " + aerr);
            }
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "speedtest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static Clip Json(Clip c) => System.Text.Json.JsonSerializer.Deserialize<Clip>(System.Text.Json.JsonSerializer.Serialize(c))!;

    /// <summary>The frame number in the stripe (pixel stride / channel offset: gray = 1/0, BGRA = 4/1).</summary>
    static int Decode(byte[] px, int w, int h, int stride, int ch)
    {
        int y = (int)(h * 48.0 / SrcH), v = 0;
        for (int b = 0; b < Bits; b++)
        {
            int x = (int)((b + 0.5) * w / Bits);
            if (px[(y * w + x) * stride + ch] > 128) v |= 1 << b;
        }
        return v;
    }

    static List<int> ReadExport(string ff, string file)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-i", file, "-f", "rawvideo", "-pix_fmt", "gray", "-s", $"{SrcW}x{SrcH}", "pipe:1" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        var res = new List<int>(); var buf = new byte[SrcW * SrcH]; var s = p.StandardOutput.BaseStream;
        while (true)
        {
            int got = 0; while (got < buf.Length) { int k = s.Read(buf, got, buf.Length - got); if (k <= 0) break; got += k; }
            if (got < buf.Length) break;
            res.Add(Decode(buf, SrcW, SrcH, 1, 0));
        }
        p.WaitForExit();
        return res;
    }

    /// <summary>Frequency by counting upward zero crossings.</summary>
    static double Freq(float[] a)
    {
        int n = 0; for (int i = 1; i < a.Length; i++) if (a[i - 1] < 0 && a[i] >= 0) n++;
        return n / (a.Length / 48000.0);
    }

    static void Run(string ff, string args) { using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true })!; p.WaitForExit(); }

    static float[] Pcm(string ff, string file)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-v", "error", "-i", file, "-f", "f32le", "-ac", "2", "pipe:1" }) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var ms = new MemoryStream(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        var bytes = ms.ToArray(); var r = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, r, 0, r.Length * 4);
        return r;
    }
}
