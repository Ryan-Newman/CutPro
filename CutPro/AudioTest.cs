using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace CutPro;

/// <summary>CutPro.exe --audiotest dir: the Audio Track Mixer and loudness.
/// - the in-app mix (the playback mixer, rendered offline) vs the exported WAV, sample by sample: clip volume / fades, track fader + pan
///   per track, master, a 44.1 kHz source, a mono source, NTSC frame times between samples, In/Out range, solo, mute;
/// - BS.1770 loudness and true peak vs ffmpeg's ebur128 (and the EBU Tech 3341 −23 dBFS 1 kHz reference);
/// - export normalization to −14 LUFS without and with the peak limiter (loudness, true peak, no time shift);
/// - waveform peaks, meter peaks, mixer undo, project file round trip, measuring speed.</summary>
public static class AudioTest
{
    const int R = Mixer.Rate;

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        Editor.SetSequence(1280, 720, 30000.0 / 1001, ReframeMode.Fit);       // 29.97: frame times fall between samples
        try
        {
            string Gen(string name, string src)
            {
                var f = Path.Combine(dir, name);
                if (!File.Exists(f)) Run(ff, $"-y -v error -f lavfi -i \"{src}\" \"{f}\"");
                return f;
            }
            var voiceF = Gen("voice.wav", "aevalsrc=exprs='0.25*sin(2*PI*220*t)*(0.6+0.4*sin(2*PI*3*t))+0.06*(random(0)-0.5)|0.2*sin(2*PI*330*t)*(0.6+0.4*sin(2*PI*2*t+1))+0.04*(random(1)-0.5)':s=48000:d=8");
            var musicF = Gen("music44k.wav", "aevalsrc=exprs='0.3*sin(2*PI*440*t)+0.1*sin(2*PI*1320*t)|0.3*sin(2*PI*554.37*t)':s=44100:d=8");
            var monoF = Gen("mono.wav", "sine=frequency=500:sample_rate=48000:duration=6");
            // a moderate bed with sharp 0.5 ms clicks: normalizing it pushes the clicks over −1 dBTP, so the limiter must catch them
            var burstF = Gen("clicks.wav", "aevalsrc=exprs='0.1*sin(2*PI*300*t)+0.05*sin(2*PI*1700*t+1)+0.8*lt(mod(t,0.5),0.0005)*sin(2*PI*2000*mod(t,0.5))|0.1*sin(2*PI*300*t)+0.05*sin(2*PI*1710*t)+0.8*lt(mod(t,0.5),0.0005)*sin(2*PI*2000*mod(t,0.5))':s=48000:d=10");
            var refF = Gen("ref_m23.wav", "aevalsrc=exprs='0.0707946*sin(2*PI*1000*t)|0.0707946*sin(2*PI*1000*t)':s=48000:d=20");
            async Task<MediaItem> Imp(string f) => await MediaProbe.ProbeAsync(f) ?? throw new Exception("import " + f);
            var voice = await Imp(voiceF); var music = await Imp(musicF); var mono = await Imp(monoF); var burst = await Imp(burstF); var refm = await Imp(refF);
            static double Fr(double t) => Math.Round(t * TimeUtil.Fps) / TimeUtil.Fps;

            // ---- 1. mixer parity: in-app mix vs exported WAV ----
            var ed = new Editor(); ed.Media.AddRange(new[] { voice, music, mono });
            ed.Clips.Add(new Clip { MediaId = voice.Id, Kind = ClipKind.Audio, Track = 3, Start = Fr(0.5), In = Fr(0.3), Dur = Fr(5), Volume = 80, FadeIn = 0.4 });
            ed.Clips.Add(new Clip { MediaId = music.Id, Kind = ClipKind.Audio, Track = 4, Start = Fr(1.2), In = Fr(1.0), Dur = Fr(4.5), FadeOut = 0.5 });
            ed.Clips.Add(new Clip { MediaId = mono.Id, Kind = ClipKind.Audio, Track = 4, Start = Fr(6.0), In = 0, Dur = Fr(2) });
            ed.Mix.Channels[3].Gain = -4.5; ed.Mix.Channels[3].Pan = -40;
            ed.Mix.Channels[4].Gain = 2; ed.Mix.Channels[4].Pan = 70;
            ed.Mix.Master = -1.5;
            async Task<(float[] exp, float[] mix, string? err)> Both(Editor e, string name, ExportSettings? o = null, double t0 = 0, double t1 = -1)
            {
                var wav = Path.Combine(dir, name);
                var err = await Exporter.RunAsync(ff, e, wav, new Progress<double>(), CancellationToken.None, null, null, o ?? Wav());
                if (t1 < 0) t1 = e.End;
                return (err == null ? Pcm(ff, wav) : Array.Empty<float>(), MixOf(e, t0, t1), err);
            }
            var (ex1, mx1, e1) = await Both(ed, "mix.wav");
            Check(e1 == null, "export of the mixed timeline" + (e1 == null ? "" : ": " + e1));
            if (e1 == null)
            {
                var (snr, lag) = Compare(mx1, ex1);
                Check(snr >= 50 && lag == 0 && Math.Abs(ex1.Length - mx1.Length) <= 2,
                      $"in-app mix = export: {snr:0.0} dB SNR, shift {lag} samples, {ex1.Length / 2} vs {mx1.Length / 2} frames (fader −4.5 dB pan L40, fader +2 dB pan R70, master −1.5 dB, 44.1 kHz + mono sources)");
                // the pan really moves the sound: the A1 voice is louder on the left than the right of its source ratio
                double l = Energy(mx1, 0, (int)(0.6 * R), (int)(1.1 * R)), r = Energy(mx1, 1, (int)(0.6 * R), (int)(1.1 * R));
                Check(l > r * 1.5, $"pan left 40: left / right energy {l / Math.Max(1e-12, r):0.00} (> 1.5)");
            }
            // In / Out range
            ed.MarkIn = Fr(1.7); ed.MarkOut = Fr(4.9);
            var (ex2, mx2, e2) = await Both(ed, "range.wav", new ExportSettings { Video = "none", Container = "wav", Audio = "pcm", InOutOnly = true }, ed.MarkIn.Value, ed.MarkOut.Value);
            if (e2 == null) { var (snr, lag) = Compare(mx2, ex2); Check(snr >= 50 && lag == 0 && Math.Abs(ex2.Length - mx2.Length) <= 2, $"In/Out export = in-app mix of the range: {snr:0.0} dB, shift {lag}, {ex2.Length / 2} vs {mx2.Length / 2} frames"); }
            else Check(false, "In/Out export: " + e2);
            ed.MarkIn = ed.MarkOut = null;
            // solo A2: A1 silent; then mute A2 (no solo): only A1
            ed.Mix.Channels[4].Solo = true;
            var (ex3, mx3, e3) = await Both(ed, "solo.wav");
            if (e3 == null)
            {
                var (snr, lag) = Compare(mx3, ex3);
                double a1only = Energy(ex3, 0, (int)(0.6 * R), (int)(1.1 * R)) + Energy(ex3, 1, (int)(0.6 * R), (int)(1.1 * R));
                Check(snr >= 50 && lag == 0 && a1only < 1e-9, $"solo A2: export = mix ({snr:0.0} dB), A1 silent (energy {a1only:E1})");
            }
            else Check(false, "solo export: " + e3);
            ed.Mix.Channels[4].Solo = false; ed.TrackOff[4] = true;
            var (ex4, mx4, e4) = await Both(ed, "mute.wav");
            if (e4 == null)
            {
                var (snr, lag) = Compare(mx4, ex4);
                double a2only = Energy(ex4, 0, (int)(6.2 * R), (int)(7.8 * R)) + Energy(ex4, 1, (int)(6.2 * R), (int)(7.8 * R));
                Check(snr >= 50 && lag == 0 && a2only < 1e-9, $"mute A2: export = mix ({snr:0.0} dB), A2 silent (energy {a2only:E1})");
            }
            else Check(false, "mute export: " + e4);
            ed.TrackOff[4] = false;
            // track automation: A1 fader keys (a ramp from −20 dB, an eased dip, a hold step), A2 balance keys (left → right) — the mixer's blocks of
            // 240 samples on both sides
            {
                var a1 = ed.Mix.Channels[3]; var a2 = ed.Mix.Channels[4];
                a1.GainKeys = new() { new Keyframe { T = 0.8, V = -20 }, new Keyframe { T = 2.5, V = 0 }, new Keyframe { T = 3.6, V = 0, Out = KeyType.Bezier },
                                      new Keyframe { T = 4.2, V = -12, In = KeyType.Bezier, Out = KeyType.Hold }, new Keyframe { T = 5.0, V = -3 } };
                a2.PanKeys = new() { new Keyframe { T = 2.0, V = -100 }, new Keyframe { T = 5.0, V = 100 } };
                var (ex5, mx5, e5) = await Both(ed, "automation.wav");
                if (e5 == null)
                {
                    var (snr, lag) = Compare(mx5, ex5);
                    double quiet = Energy(mx5, 0, (int)(0.6 * R), (int)(0.8 * R)), loud = Energy(mx5, 0, (int)(2.6 * R), (int)(2.8 * R));
                    double left = Energy(mx5, 0, (int)(2.1 * R), (int)(2.4 * R)) / Math.Max(1e-12, Energy(mx5, 1, (int)(2.1 * R), (int)(2.4 * R)));
                    Check(snr >= 50 && lag == 0 && Math.Abs(ex5.Length - mx5.Length) <= 2 && loud / Math.Max(1e-12, quiet) > 30,
                          $"track automation (A1 fader ramp / eased dip / hold, A2 balance sweep): export = mix {snr:0.0} dB, shift {lag}, A1 −20 dB → 0 dB energy ×{loud / Math.Max(1e-12, quiet):0}");
                }
                else Check(false, "automation export: " + e5);
                // undo / redo carry the keys
                ed.MixBegin(); a1.GainKeys!.Add(new Keyframe { T = 6, V = -6 }); ed.MixEnd();
                ed.Undo(); bool undone = ed.Mix.Channels[3].GainKeys!.Count == 5; ed.Redo(); bool redone = ed.Mix.Channels[3].GainKeys!.Count == 6;
                Check(undone && redone, $"automation keys: undo / redo ({(undone ? "undone" : "NOT undone")}, {(redone ? "redone" : "NOT redone")})");
                ed.Mix.Channels[3].GainKeys = null; ed.Mix.Channels[4].PanKeys = null;
                // master automation (a ramp, an eased swell, a hold step) over a track with its own automation
                ed.Mix.MasterKeys = new() { new Keyframe { T = 0.6, V = -15 }, new Keyframe { T = 2.0, V = 0, Out = KeyType.Bezier }, new Keyframe { T = 3.5, V = 3, In = KeyType.Bezier, Out = KeyType.Hold },
                                            new Keyframe { T = 5.5, V = -8 } };
                ed.Mix.Channels[4].GainKeys = new() { new Keyframe { T = 1.5, V = 0 }, new Keyframe { T = 4.0, V = -9 } };
                var (ex6, mx6, e6) = await Both(ed, "master_auto.wav");
                if (e6 == null)
                {
                    var (snr, lag) = Compare(mx6, ex6);
                    double early = Energy(mx6, 1, (int)(1.3 * R), (int)(1.5 * R)), late = Energy(mx6, 1, (int)(3.6 * R), (int)(3.8 * R));
                    Check(snr >= 50 && lag == 0 && Math.Abs(ex6.Length - mx6.Length) <= 2, $"master automation (ramp, eased swell, hold) over track automation: export = mix {snr:0.0} dB, shift {lag}");
                }
                else Check(false, "master automation export: " + e6);
                ed.MixBegin(); ed.Mix.MasterKeys!.Add(new Keyframe { T = 7, V = -2 }); ed.MixEnd();
                ed.Undo(); bool mu = ed.Mix.MasterKeys!.Count == 4; ed.Redo(); bool mr = ed.Mix.MasterKeys!.Count == 5;
                Check(mu && mr, "master automation keys: undo / redo");
                ed.Mix.MasterKeys = null; ed.Mix.Channels[4].GainKeys = null;
            }

            // ---- 2. loudness meter vs ffmpeg ebur128 ----
            if (e1 == null)
            {
                var ours = Mixer.Measure(ed, 0, ed.End);
                var (fi, ftp) = Ebur128(ff, Path.Combine(dir, "mix.wav"));
                Check(Math.Abs(ours.Integrated - fi) <= 0.1 && Math.Abs(ours.TruePeak - ftp) <= 0.3,
                      $"loudness of the mix: ours {ours.Integrated:0.00} LUFS / {ours.TruePeak:0.00} dBTP, ffmpeg {fi:0.00} LUFS / {ftp:0.00} dBTP");
            }
            var er = new Editor(); er.Media.Add(refm);
            er.Clips.Add(new Clip { MediaId = refm.Id, Kind = ClipKind.Audio, Track = 3, Start = 0, Dur = 20 });
            var rr = Mixer.Measure(er, 0, 20);
            Check(Math.Abs(rr.Integrated + 23) <= 0.1, $"EBU Tech 3341 reference (1 kHz, −23 dBFS stereo): {rr.Integrated:0.00} LUFS (−23.0 ± 0.1)");
            {
                var liveM = new Loudness.Meter();
                Mixer.Render(Mixer.Sources(er, Transitions.Compute(er), new()).ToArray(), Mixer.MasterOf(er), 0, 4, (buf, n) => liveM.Add(buf, n, 1 / 32768f));
                var lv = liveM.Live();
                Check(Math.Abs(lv.Momentary + 23) <= 0.2 && Math.Abs(lv.ShortTerm + 23) <= 0.2 && Math.Abs(lv.Integrated + 23) <= 0.2,
                      $"live M/S/I of the EBU 1 kHz: M {lv.Momentary:0.00} S {lv.ShortTerm:0.00} I {lv.Integrated:0.00} (all ≈ −23)");
            }

            // ---- 3. normalization ----
            var eq = new Editor(); eq.Media.Add(voice);
            eq.Clips.Add(new Clip { MediaId = voice.Id, Kind = ClipKind.Audio, Track = 3, Start = 0, In = 0, Dur = Fr(7.5), Volume = 25 });
            var oq = new ExportSettings { Video = "none", Container = "wav", Audio = "pcm", Normalize = true, Lufs = -14 };
            var (exq, mxq, eqe) = await Both(eq, "norm_quiet.wav", oq);
            if (eqe == null)
            {
                var (fi, ftp) = Ebur128(ff, Path.Combine(dir, "norm_quiet.wav"));
                var (snr, lag) = Compare(Scaled(mxq, oq.NormGainDb), exq);
                Check(!oq.NormLimit && Math.Abs(fi + 14) <= 0.3 && ftp <= -0.9 && snr >= 50 && lag == 0,
                      $"normalize a quiet voice: measured {oq.Measured?.Integrated:0.00} → gain {oq.NormGainDb:+0.00} dB, export {fi:0.00} LUFS / {ftp:0.00} dBTP, no limiter, = mix × gain ({snr:0.0} dB, shift {lag})");
            }
            else Check(false, "normalize export: " + eqe);
            var eb = new Editor(); eb.Media.Add(burst);
            eb.Clips.Add(new Clip { MediaId = burst.Id, Kind = ClipKind.Audio, Track = 3, Start = 0, In = 0, Dur = Fr(10) });
            var ob = new ExportSettings { Video = "none", Container = "wav", Audio = "pcm", Normalize = true, Lufs = -14 };
            var (exb, mxb, ebe) = await Both(eb, "norm_bursts.wav", ob);
            if (ebe == null)
            {
                var (fi, ftp) = Ebur128(ff, Path.Combine(dir, "norm_bursts.wav"));
                var (_, lag) = Compare(Scaled(mxb, ob.NormGainDb), exb, quietOnly: true);
                Check(ob.NormLimit && Math.Abs(fi + 14) <= 0.6 && ftp <= -0.7 && lag == 0,
                      $"normalize clicks over a bed: measured {ob.Measured?.Integrated:0.00} LUFS / {ob.Measured?.TruePeak:0.00} dBTP → gain {ob.NormGainDb:+0.00} dB + limiter, export {fi:0.00} LUFS / {ftp:0.00} dBTP (≤ −1), shift {lag}");
            }
            else Check(false, "normalize (limiter) export: " + ebe);

            // ---- 4. waveform peaks, meters ----
            Peaks.Get(voice); var sw0 = Stopwatch.StartNew(); Peaks.Data? pk = null;
            while ((pk = Peaks.Get(voice)) == null && sw0.ElapsedMilliseconds < 15000) await Task.Delay(20);
            if (pk == null) Check(false, "waveform peaks were not built");
            else
            {
                var pcm = new short[pk.Frames * 2]; AudioCache.Read(voice.Path, 0, pcm, (int)pk.Frames);
                int Brute(long f0, long f1) { int m = 0; for (long i = Math.Max(0, f0); i < Math.Min(pk.Frames, f1); i++) m = Math.Max(m, Math.Max(Math.Abs((int)pcm[i * 2]), Math.Abs((int)pcm[i * 2 + 1]))); return m; }
                bool ok = Math.Abs(pk.Max(0, pk.Frames) * 32768 - Brute(0, pk.Frames)) < 0.5;
                var rnd = new Random(7);
                for (int i = 0; i < 200 && ok; i++)
                {
                    long f0 = rnd.Next(0, (int)pk.Frames - 10), f1 = f0 + rnd.Next(1, 200000);
                    // Max reads whole blocks of the chosen level: compare with the brute force over the same blocks
                    long span = f1 - f0, bs = Peaks.Block; int lv = 0;
                    while (lv + 1 < pk.Levels.Length && bs * Peaks.Factor <= span) { lv++; bs *= Peaks.Factor; }
                    ok = Math.Abs(pk.Max(f0, f1) * 32768 - Brute(f0 / bs * bs, (f1 + bs - 1) / bs * bs)) < 0.5;
                }
                Check(ok, $"waveform peaks: {pk.Levels.Length} levels, max of any range = the samples' max ({sw0.ElapsedMilliseconds} ms)");
            }
            // a video file's sound (AAC in MP4, like a camera clip) and a second file requested right after the first
            var vidF = Path.Combine(dir, "bars.mp4");
            if (!File.Exists(vidF)) Run(ff, $"-y -v error -f lavfi -i smptebars=size=640x480:rate=30:duration=4 -f lavfi -i sine=frequency=880:duration=4 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \"{vidF}\"");
            var vid = await Imp(vidF);
            Peaks.Get(vid); Peaks.Get(music);
            var sw1 = Stopwatch.StartNew(); Peaks.Data? pv = null, pm = null;
            while (((pv = Peaks.Get(vid)) == null || (pm = Peaks.Get(music)) == null) && sw1.ElapsedMilliseconds < 15000) await Task.Delay(20);
            Check(pv != null && pm != null && pv.Max(0, pv.Frames) > 0.1 && pm.Max(0, pm.Frames) > 0.3,
                  $"waveform peaks of a video's AAC sound and a 44.1 kHz file: {(pv == null ? "missing" : $"{Mixer.Db(pv.Max(0, pv.Frames)):0.0} dBFS")}, {(pm == null ? "missing" : $"{Mixer.Db(pm.Max(0, pm.Frames)):0.0} dBFS")}");
            var em = new Editor(); em.Media.Add(mono);
            em.Clips.Add(new Clip { MediaId = mono.Id, Kind = ClipKind.Audio, Track = 4, Start = 0, Dur = 2 });
            em.Mix.Channels[4].Gain = -6; em.Mix.Master = -3;
            var msrc = Mixer.Sources(em, Transitions.Compute(em), new()).ToArray();
            while (!AudioCache.IsDone(mono.Path)) await Task.Delay(20);
            var mbuf = new float[960 * 2]; var peaks = new float[Mixer.Slots];
            Mixer.Mix(msrc, Mixer.MasterGain(em), 0.5, 960, mbuf, Mixer.Bus(960), new short[960 * 2], peaks);
            var raw = new short[960 * 2]; AudioCache.Read(mono.Path, (long)(0.5 * R), raw, 960);
            double want = raw.Max(v => Math.Abs((int)v)) / 32768.0 * Mixer.Lin(-6);
            Check(Math.Abs(peaks[8] / want - 1) < 0.01 && Math.Abs(peaks[9] / want - 1) < 0.01 && Math.Abs(peaks[^2] / (want * Mixer.Lin(-3)) - 1) < 0.01 && peaks[6] == 0,
                  $"meters: A2 peak {Mixer.Db(peaks[8]):0.00} dBFS (want {Mixer.Db(want):0.00}), master {Mixer.Db(peaks[^2]):0.00} (want {Mixer.Db(want) - 3:0.00}), A1 silent");

            // ---- 5. undo, project file ----
            var eu = new Editor();
            eu.MixBegin(); eu.Mix.Channels[3].Gain = -12; eu.Mix.Channels[3].Pan = 30; eu.MixEnd();
            eu.MixBegin(); eu.Mix.Master = -2; eu.MixEnd();
            eu.Undo(); bool u1 = eu.Mix.Master == 0 && eu.Mix.Channels[3].Gain == -12;
            eu.Undo(); bool u2 = eu.Mix.Channels[3].Gain == 0 && eu.Mix.Channels[3].Pan == 0;
            eu.Redo(); eu.Redo(); bool u3 = eu.Mix.Channels[3].Gain == -12 && eu.Mix.Master == -2;
            Check(u1 && u2 && u3, "mixer undo / redo (fader + pan drag, master)");
            ed.Mix.Channels[4].Solo = true; ed.TrackOff[3] = true;
            var json = System.Text.Json.JsonSerializer.Serialize(new ProjectFile { Mix = ed.Mix, TrackOff = ed.TrackOff });
            var back = System.Text.Json.JsonSerializer.Deserialize<ProjectFile>(json)!;
            var ep = new Editor(); ep.UseMix(back.Mix, back.TrackOff);
            var old = System.Text.Json.JsonSerializer.Deserialize<ProjectFile>("{\"Clips\":[]}")!;
            var eo = new Editor(); eo.UseMix(old.Mix, old.TrackOff);
            Check(ep.Mix.Channels[3].Gain == -4.5 && ep.Mix.Channels[4].Pan == 70 && ep.Mix.Master == -1.5 && ep.Mix.Channels[4].Solo && ep.TrackOff[3]
                  && eo.Mix.Channels.Length == Tracks.Count && eo.Mix.Master == 0 && !eo.TrackOff.Any(x => x),
                  "project file keeps the mixer and track mutes; older projects open at 0 dB");

            // ---- 5b. audio effects: each one does its job (on test tones), the export = the mixer, playback from mid-clip = played through ----
            {
                double Level(short[] b, int f0, int f1) { double s = 0; for (int i = f0; i < f1; i++) s += (double)b[i * 2] * b[i * 2]; return 10 * Math.Log10(Math.Max(1e-12, s / Math.Max(1, f1 - f0))) - 20 * Math.Log10(32768 / Math.Sqrt(2)); }   // dB rel. a full-scale sine
                short[] Tone(double f, double db, double secs = 1)
                {
                    int n = (int)(secs * R); var b = new short[n * 2]; double a = 32767 * Math.Pow(10, db / 20);
                    for (int i = 0; i < n; i++) b[i * 2] = b[i * 2 + 1] = (short)Math.Round(a * Math.Sin(2 * Math.PI * f * i / R));
                    return b;
                }
                double Through(AudioEffect e, double f, double db)
                {
                    var b = Tone(f, db); AudioDsp.Build(new[] { e }).Process(b, b.Length / 2);
                    return Level(b, R / 2, R) - db;                              // gain on the settled second half
                }
                double lc50 = Through(AudioDsp.New("EQ", ("lowcut", 200)), 50, -12), lc1k = Through(AudioDsp.New("EQ", ("lowcut", 200)), 1000, -12);
                double mid = Through(AudioDsp.New("EQ", ("mid", 6), ("midf", 1000)), 1000, -12);
                Check(lc50 < -20 && Math.Abs(lc1k) < 0.5 && Math.Abs(mid - 6) < 0.3, $"EQ: low cut 200 Hz takes 50 Hz down {-lc50:0.0} dB, 1 kHz {lc1k:+0.0;-0.0;0.0} dB; mid +6 dB at 1 kHz → {mid:+0.0} dB");
                double comp = Through(AudioDsp.New("Compressor", ("thr", -20), ("ratio", 4), ("att", 5), ("rel", 100), ("makeup", 0)), 1000, -6) - 6;
                Check(Math.Abs(comp - (-20 + 14 / 4.0)) < 1, $"compressor −20 dB 4:1: a −6 dBFS tone comes out at {comp:0.0} dBFS (expected {-20 + 14 / 4.0:0.0})");
                double lim = Through(AudioDsp.New("Limiter", ("ceil", -6), ("rel", 20)), 1000, -1), limQ = Through(AudioDsp.New("Limiter", ("ceil", -6), ("rel", 20)), 1000, -20);
                Check(Math.Abs((-1 + lim) - (-6)) < 1.2 && Math.Abs(limQ) < 0.3, $"limiter −6 dB ceiling: a −1 dBFS tone comes out at {-1 + lim:0.0} dBFS, a −20 dBFS tone is unchanged ({limQ:+0.0;-0.0;0.0} dB)");
                double gq = Through(AudioDsp.New("Gate", ("thr", -40), ("range", -30)), 1000, -60), gl = Through(AudioDsp.New("Gate", ("thr", -40), ("range", -30)), 1000, -20);
                Check(Math.Abs(gq + 30) < 1.5 && Math.Abs(gl) < 0.3, $"noise gate −40 dB, range −30: a quiet tone drops {gq:0.0} dB, a loud one {gl:+0.0;-0.0;0.0} dB");
                double ds = Through(AudioDsp.New("DeEsser", ("freq", 6000), ("thr", -30), ("amount", 10)), 6000, -10), dl = Through(AudioDsp.New("DeEsser", ("freq", 6000), ("thr", -30), ("amount", 10)), 500, -10);
                Check(ds < -8 && ds > -12 && Math.Abs(dl) < 0.5, $"de-esser 6 kHz: a 6 kHz hiss {ds:0.0} dB (max 10), a 500 Hz voice {dl:+0.0;-0.0;0.0} dB");
                double h50 = Through(AudioDsp.New("DeHum", ("freq", 50), ("harm", 4)), 50, -12), h150 = Through(AudioDsp.New("DeHum", ("freq", 50), ("harm", 4)), 150, -12), h1k = Through(AudioDsp.New("DeHum", ("freq", 50), ("harm", 4)), 1000, -12);
                Check(h50 < -30 && h150 < -30 && Math.Abs(h1k) < 0.3, $"hum removal 50 Hz: 50 Hz {h50:0} dB, 150 Hz {h150:0} dB, 1 kHz {h1k:+0.0;-0.0;0.0} dB");

                // the export = the mixer (voice clean-up on the voice, EQ on music at 150 % speed)
                var efx = new Editor(); efx.Media.AddRange(new[] { voice, music });
                var vfx = new Clip { MediaId = voice.Id, Kind = ClipKind.Audio, Track = 3, Start = Fr(0.4), In = Fr(0.2), Dur = Fr(5), Volume = 90, FadeOut = 0.3, AudioFx = AudioDsp.VoicePreset() };
                var mfx = new Clip { MediaId = music.Id, Kind = ClipKind.Audio, Track = 4, Start = Fr(1.0), In = Fr(0.5), Dur = Fr(3), Speed = 150, AudioFx = new() { AudioDsp.New("EQ", ("lowcut", 300), ("high", -6)) } };
                efx.Clips.Add(vfx); efx.Clips.Add(mfx);
                var (exf, mxf, ef) = await Both(efx, "fx.wav");
                if (ef == null)
                {
                    var (snr, lag) = Compare(mxf, exf);
                    Check(snr >= 50 && lag == 0 && Math.Abs(exf.Length - mxf.Length) <= 2, $"audio effects: export = in-app mix {snr:0.0} dB SNR, shift {lag} (voice clean-up preset; EQ on a clip at 150 %)");
                }
                else Check(false, "export with audio effects: " + ef);
                // playback starting mid-clip (fresh effect state, warmed up on the half second before) vs played through
                var fsrcs = Mixer.Sources(efx, Transitions.Compute(efx), new()).ToArray();
                double tj = Fr(2.5); int nj = R / 2;
                var jump = new float[nj * 2]; Mixer.Mix(fsrcs, 1, tj, nj, jump, Mixer.Bus(nj), new short[nj * 2], null, new AudioDsp.FxState());
                var full = MixOf(efx, 0, efx.End);
                var thru = full.Skip((int)Math.Round(tj * R) * 2).Take(nj * 2).ToArray();
                var (jsnr, _) = Compare(thru, jump.Select(v => v / 32768f).ToArray());
                Check(jsnr >= 35, $"audio effects: playback started mid-clip sounds like playing through ({jsnr:0.0} dB after the warm-up)");

                // track effects: reverb + compressor on A1 (pre-fader; the tail rings on after its clip), EQ on A2 with fader automation; a clip
                // effect under the track chain
                var etr = new Editor(); etr.Media.AddRange(new[] { voice, music, mono });
                etr.Clips.Add(new Clip { MediaId = voice.Id, Kind = ClipKind.Audio, Track = 3, Start = Fr(0.3), In = Fr(0.2), Dur = Fr(2.5), AudioFx = new() { AudioDsp.New("EQ", ("lowcut", 120)) } });
                etr.Clips.Add(new Clip { MediaId = mono.Id, Kind = ClipKind.Audio, Track = 3, Start = Fr(4.0), In = 0, Dur = Fr(0.5), Volume = 60 });
                etr.Clips.Add(new Clip { MediaId = music.Id, Kind = ClipKind.Audio, Track = 4, Start = Fr(1.0), In = Fr(0.5), Dur = Fr(4) });
                etr.Clips.Add(new Clip { MediaId = mono.Id, Kind = ClipKind.Audio, Track = 4, Start = Fr(7.5), In = 0, Dur = Fr(0.5) });   // (the timeline goes on: the A1 tail is heard)
                etr.Mix.Channels[3].Fx = new() { AudioDsp.New("Reverb", ("size", 80), ("wet", 40)), AudioDsp.New("Compressor", ("thr", -24), ("ratio", 3)) };
                etr.Mix.Channels[3].Gain = -2; etr.Mix.Channels[3].Pan = -30;
                etr.Mix.Channels[4].Fx = new() { AudioDsp.New("EQ", ("low", 4), ("highcut", 6000)) };
                etr.Mix.Channels[4].GainKeys = new() { new() { T = 1, V = -12 }, new() { T = 3, V = 0 } };
                var (ext, mxt, et) = await Both(etr, "trackfx.wav");
                if (et == null)
                {
                    var (snr, lag) = Compare(mxt, ext);
                    double tail = 0, dryTail = 0;
                    // the same without the reverb: silence after the voice clip's end
                    etr.Mix.Channels[3].Fx = new() { AudioDsp.New("Compressor", ("thr", -24), ("ratio", 3)) };
                    var noRev = MixOf(etr, 0, etr.End);
                    var diff = mxt.Zip(noRev, (a, b) => a - b).ToArray();                   // (the reverb's share: A1's tail after its clip, A2 untouched)
                    tail = Energy(diff, 0, (int)(2.9 * R), (int)(3.6 * R)); dryTail = Energy(diff, 0, (int)(0.0 * R), (int)(0.25 * R));
                    etr.Mix.Channels[3].Fx = new() { AudioDsp.New("Reverb", ("size", 80), ("wet", 40)), AudioDsp.New("Compressor", ("thr", -24), ("ratio", 3)) };
                    Check(snr >= 50 && lag == 0 && Math.Abs(ext.Length - mxt.Length) <= 2 && tail > 1e-7 && dryTail < 1e-12,
                          $"track effects (A1 reverb + compressor pre-fader, A2 EQ under fader automation, a clip EQ under the track chain): export = mix {snr:0.0} dB, shift {lag}; the reverb adds {tail:E1} after A1's clip ends (and nothing before any sound: {dryTail:E1})");
                }
                else Check(false, "export with track effects: " + et);
                // playback started after a jump: the track chain is warmed up on the seconds before (a reverb's tail included)
                var tsrcs = Mixer.Sources(etr, Transitions.Compute(etr), new()).ToArray();
                double tk = Fr(5.0); int nk = R / 2;
                var tjump = new float[nk * 2]; Mixer.Mix(tsrcs, Mixer.MasterOf(etr), tk, nk, tjump, Mixer.Bus(nk), new short[nk * 2], null, new AudioDsp.FxState());
                var tfull = MixOf(etr, 0, etr.End);
                var tthru = tfull.Skip((int)Math.Round(tk * R) * 2).Take(nk * 2).ToArray();
                var (tsnr, _) = Compare(tthru, tjump.Select(v => v / 32768f).ToArray());
                Check(tsnr >= 35, $"track effects: playback started mid-way sounds like playing through ({tsnr:0.0} dB after the warm-up, reverb included)");
                // undo / redo of a track chain change
                etr.MixBegin(); etr.Mix.Channels[4].Fx = null; etr.MixEnd();
                etr.Undo(); bool fxBack = etr.Mix.Channels[4].Fx is { Count: 1 }; etr.Redo(); bool fxGone = etr.Mix.Channels[4].Fx == null;
                Check(fxBack && fxGone, $"track effects: undo / redo ({(fxBack ? "back" : "NOT back")}, {(fxGone ? "gone again" : "NOT gone")})");
                // submixes: A1 (with its own EQ and fader automation) and the second A1 clip → a submix with reverb + compressor, a fader ramp and
                // pan; A2 straight to the master
                var esb = new Editor(); esb.Media.AddRange(new[] { voice, music, mono });
                esb.Clips.Add(new Clip { MediaId = voice.Id, Kind = ClipKind.Audio, Track = 3, Start = Fr(0.3), In = Fr(0.2), Dur = Fr(2.5) });
                esb.Clips.Add(new Clip { MediaId = mono.Id, Kind = ClipKind.Audio, Track = 3, Start = Fr(4.0), In = 0, Dur = Fr(0.5), Volume = 60 });
                esb.Clips.Add(new Clip { MediaId = music.Id, Kind = ClipKind.Audio, Track = 4, Start = Fr(1.0), In = Fr(0.5), Dur = Fr(4) });
                esb.Mix.Channels[3].Fx = new() { AudioDsp.New("EQ", ("lowcut", 150)) };
                esb.Mix.Channels[3].GainKeys = new() { new() { T = 0.5, V = -6 }, new() { T = 2.0, V = 0 } };
                esb.Mix.Subs.Add(new SubMix { Name = "Voice bus", Gain = -2, Pan = 25, Fx = new() { AudioDsp.New("Reverb", ("size", 75), ("wet", 35)), AudioDsp.New("Compressor", ("thr", -22)) },
                                              GainKeys = new() { new() { T = 3.0, V = -2 }, new() { T = 6.0, V = -12 } } });
                esb.Mix.Channels[3].Output = 0;
                var (exs, mxs, es) = await Both(esb, "submix.wav");
                if (es == null)
                {
                    var (snr, lag) = Compare(mxs, exs);
                    Check(snr >= 50 && lag == 0 && Math.Abs(exs.Length - mxs.Length) <= 2,
                          $"submix (A1 with EQ + automation → a bus with reverb, compressor, fader ramp, pan; A2 → master): export = mix {snr:0.0} dB, shift {lag}");
                }
                else Check(false, "export with a submix: " + es);
                var ssrcs = Mixer.Sources(esb, Transitions.Compute(esb), new()).ToArray();
                double sk = Fr(3.2); int sn = R / 2;                                      // (in the bus reverb's tail after the first A1 clip)
                var sjump = new float[sn * 2]; Mixer.Mix(ssrcs, Mixer.MasterOf(esb), sk, sn, sjump, Mixer.Bus(sn), new short[sn * 2], null, new AudioDsp.FxState());
                var sthru = MixOf(esb, 0, esb.End).Skip((int)Math.Round(sk * R) * 2).Take(sn * 2).ToArray();
                var (ssnr, _) = Compare(sthru, sjump.Select(v => v / 32768f).ToArray());
                Check(ssnr >= 35, $"submix: playback started mid-way sounds like playing through ({ssnr:0.0} dB after the warm-up, the bus reverb included; energy {sjump.Sum(v => (double)v * v) / 1e12:E2} vs {sthru.Sum(v => (double)v * v) * 32768.0 * 32768 / 1e12:E2})");
                // muting the submix silences its tracks; routing changes undo
                esb.Mix.Subs[0].Mute = true;
                var muted = MixOf(esb, 0, esb.End);
                double vox = Energy(muted, 0, (int)(0.5 * R), (int)(0.9 * R));                // (only A1 sounds there)
                esb.Mix.Subs[0].Mute = false;
                esb.MixBegin(); esb.Mix.Channels[3].Output = -1; esb.MixEnd();
                esb.Undo(); bool routedBack = esb.Mix.Channels[3].Output == 0;
                Check(vox < 1e-12 && routedBack, $"submix mute silences its tracks (energy {vox:E1}); routing undo puts A1 back on the bus");

                // pitch shift: a 440 Hz tone up an octave / down 5 semitones (zero crossings), a click train kept in sync with the picture (energy
                // centres within ±1.5 ms), the export = the mixer (both read the rendered file)
                var toneF = Gen("tone440.wav", "sine=frequency=440:sample_rate=48000:duration=4");
                var ticksF = Gen("ticks.wav", "aevalsrc=exprs='0.8*lt(mod(t,0.5),0.002)*sin(2*PI*1500*t)|0.8*lt(mod(t,0.5),0.002)*sin(2*PI*1500*t)':s=48000:d=4");
                var tone = await Imp(toneF); var ticks = await Imp(ticksF);
                double Freq(float[] st, double a, double b)
                {
                    int z = 0; int i0 = (int)(a * R), i1 = (int)(b * R);
                    for (int i = i0 + 1; i < i1; i++) if (st[(i - 1) * 2] <= 0 && st[i * 2] > 0) z++;
                    return z / (b - a);
                }
                foreach (var (semis, wantHz) in new[] { (12.0, 880.0), (-5.0, 440 * Math.Pow(2, -5 / 12.0)) })
                {
                    var eps = new Editor(); eps.Media.Add(tone);
                    eps.Clips.Add(new Clip { MediaId = tone.Id, Kind = ClipKind.Audio, Track = 3, Start = 0, In = 0, Dur = 3, Pitch = semis });
                    AudioCache.WaitReady(AudioCache.EnsureDerived(eps.Clips[0], tone), 30000);
                    var pmx = MixOf(eps, 0, eps.End);
                    double f = Freq(pmx, 0.5, 2.5);
                    Check(Math.Abs(f - wantHz) / wantHz < 0.01 && pmx.Length / 2 == (int)Math.Round(3.0 * R), $"pitch {semis:+0;-0} semitones: a 440 Hz tone comes out at {f:0.0} Hz (want {wantHz:0.0}), length kept");
                }
                {
                    var ek = new Editor(); ek.Media.Add(ticks);
                    ek.Clips.Add(new Clip { MediaId = ticks.Id, Kind = ClipKind.Audio, Track = 3, Start = 0.2, In = 0.1, Dur = 3, Pitch = 7 });
                    AudioCache.WaitReady(AudioCache.EnsureDerived(ek.Clips[0], ticks), 30000);
                    var shifted = MixOf(ek, 0, ek.End);
                    ek.Clips[0].Pitch = 0; var plain = MixOf(ek, 0, ek.End); ek.Clips[0].Pitch = 7;
                    double Centre(float[] st, double t) { double s = 0, w = 0; for (int i = (int)((t - 0.04) * R); i < (int)((t + 0.04) * R); i++) { double v = st[i * 2] * st[i * 2]; s += v; w += v * i; } return s > 0 ? w / s / R : double.NaN; }
                    double worst = 0;
                    for (double t = 0.6; t < 3.0; t += 0.5) worst = Math.Max(worst, Math.Abs(Centre(shifted, t) - Centre(plain, t)));     // (clicks at 0.6, 1.1 … on the timeline)
                    Check(worst < 0.0015, $"pitch +7 keeps the sound in sync: click energy centres within {worst * 1000:0.00} ms of the unshifted ones");
                    ek.Media.Add(voice); ek.Clips.Add(new Clip { MediaId = voice.Id, Kind = ClipKind.Audio, Track = 4, Start = 0.5, In = 0, Dur = 2, Pitch = -3, Formant = false });
                    var (exp, mxp, ep2) = await Both(ek, "pitch.wav");
                    if (ep2 == null) { var (snr, lag) = Compare(mxp, exp); Check(snr >= 50 && lag == 0, $"pitch-shifted clips (+7 formants kept, −3 shifted): export = mix {snr:0.0} dB, shift {lag}"); }
                    else Check(false, "export with pitch shift: " + ep2);
                }

                // reverb on a clip: its tail ends with the clip
                var b2 = Tone(1000, -12); for (int i = R / 4 * 2; i < b2.Length; i++) b2[i] = 0;
                AudioDsp.Build(new[] { AudioDsp.New("Reverb", ("size", 70), ("wet", 50), ("dry", 0)) }).Process(b2, b2.Length / 2);
                double rtail = Level(b2, R / 4 + R / 20, R / 2);
                Check(rtail > -60 && rtail < -10, $"reverb: a tone's tail rings on after it stops ({rtail:0.0} dBFS over the next quarter second)");
            }

            // ---- 6. speed: the meter on 10 minutes of stereo noise ----
            var meter = new Loudness.Meter(); var rng = new Random(1); var blk = new float[R / 10 * 2];
            for (int i = 0; i < blk.Length; i++) blk[i] = (float)(rng.NextDouble() * 1.6 - 0.8);
            var swm = Stopwatch.StartNew();
            for (int i = 0; i < 6000; i++) meter.Add(blk, R / 10);
            var mres = meter.Result();
            double xrt = 600 / swm.Elapsed.TotalSeconds;
            Check(xrt > 60, $"loudness meter speed: 10 min of loud stereo noise in {swm.Elapsed.TotalSeconds:0.00} s ({xrt:0}× real time; {mres.Integrated:0.0} LUFS)");
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "audiotest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static ExportSettings Wav() => new() { Video = "none", Container = "wav", Audio = "pcm" };

    /// <summary>The playback mixer rendered offline over [t0, t1) (interleaved stereo, 1 = full scale).</summary>
    internal static float[] MixOf(Editor ed, double t0, double t1)
    {
        var srcs = Mixer.Sources(ed, Transitions.Compute(ed), new()).ToArray();
        foreach (var p in srcs.Select(s => s.Path).Distinct()) while (!AudioCache.IsDone(p)) Thread.Sleep(20);
        var list = new List<float>();
        Mixer.Render(srcs, Mixer.MasterOf(ed), t0, t1, (b, n) => { for (int i = 0; i < n * 2; i++) list.Add(b[i] / 32768f); });
        return list.ToArray();
    }
    static float[] Scaled(float[] a, double db) { float g = (float)Math.Pow(10, db / 20); return a.Select(v => v * g).ToArray(); }
    static double Energy(float[] a, int ch, int f0, int f1) { double s = 0; for (int i = f0; i < Math.Min(f1, a.Length / 2); i++) s += a[i * 2 + ch] * a[i * 2 + ch]; return s / Math.Max(1, f1 - f0); }

    /// <summary>Signal-to-difference ratio (dB) at the best alignment within ±16 samples, and that alignment.
    /// quietOnly: only where the reference is below −6 dBFS (the limiter changes the loud parts on purpose).</summary>
    internal static (double snr, int lag) Compare(float[] mix, float[] exp, bool quietOnly = false)
    {
        int n = Math.Min(mix.Length, exp.Length) / 2;
        double best = double.MaxValue, sig = 0; int bl = 0;
        for (int lag = -16; lag <= 16; lag++)
        {
            double e = 0, s = 0;
            for (int i = 32; i < n - 32; i++)
                for (int ch = 0; ch < 2; ch++)
                {
                    float a = mix[i * 2 + ch];
                    if (quietOnly && Math.Abs(a) > 0.5) continue;
                    double d = a - exp[(i + lag) * 2 + ch]; e += d * d; s += a * a;
                }
            if (e < best) { best = e; bl = lag; sig = s; }
        }
        return (10 * Math.Log10(Math.Max(1e-30, sig) / Math.Max(1e-30, best)), bl);
    }

    /// <summary>Integrated loudness and true peak by ffmpeg's ebur128 filter.</summary>
    static (double i, double tp) Ebur128(string ff, string file)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-nostats", "-hide_banner", "-i", file, "-af", "ebur128=peak=true", "-f", "null", "-" }) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!; var err = p.StandardError.ReadToEnd(); p.WaitForExit();
        var summary = err[Math.Max(0, err.LastIndexOf("Summary:", StringComparison.Ordinal))..];
        double Get(string pat) { var m = Regex.Match(summary, pat); return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN; }
        return (Get(@"I:\s+(-?[\d.]+|-inf) LUFS"), Get(@"Peak:\s+(-?[\d.]+|-inf) dBFS"));
    }

    static void Run(string ff, string args) { using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true })!; p.WaitForExit(); }

    internal static float[] Pcm(string ff, string file)
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
