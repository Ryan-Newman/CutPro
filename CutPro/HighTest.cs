using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CutPro;

/// <summary>
/// CutPro.exe --hightest dir: the analysis and organisation features (the "high" roadmap items), headless.
/// - bins (new / rename / delete keeps the media / move, search by name, keyword and transcript, filters, sort, the project file);
/// - auto-save (versions kept / pruned, newest first; recovery files of a process that is gone are offered, a running one's not; clean exit);
/// - scene edit detection on a colour-cut video (shot starts found to the frame, cut / markers through a clip with an In point);
/// - synchronize by sound (a delayed, filtered, noisy copy: the lag within 0.2 ms, clips moved so the sound lines up, the start on a frame);
/// - beats (120 BPM clicks: tempo and every beat within 10 ms; a 96 BPM kick / hat loop), beat markers, cut to the beat;
/// - auto-ducking (tone bursts as speech: the music's volume keys dip by the amount during them, not outside; one undo step);
/// - text-based editing (words mapped through moved / cut clips, the ranges of chosen words, several ranges removed in one step, pauses, fillers);
/// - drops: overwrite / insert / matching audio track / locked; replace edit; fit to fill;
/// - effect presets (save / load, apply = the attributes and keys); colour match (a colour cast removed); reframe push-in keys;
/// - EDL (events, record / source timecode, M2) and OpenTimelineIO (tracks, gaps, durations, speed); the voice-over WAV writer.
/// </summary>
public static class HighTest
{
    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        AppSettings.ReadOnly = true;
        var inv = CultureInfo.InvariantCulture;
        try
        {
            Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
            string Gen(string name, string args)
            {
                var f = Path.Combine(dir, name);
                if (!File.Exists(f)) Exec(ff, $"-y -v error {args} \"{f}\"");
                return f;
            }
            async Task<MediaItem> Imp(string f) => await MediaProbe.ProbeAsync(f) ?? throw new Exception("import " + f + ": " + MediaProbe.LastError);
            const double fr = 1 / 30.0;
            var patF = Gen("pat.mp4", "-f lavfi -i testsrc2=s=640x360:r=30:d=12 -f lavfi -i sine=frequency=440:duration=12 -c:v libx264 -crf 20 -pix_fmt yuv420p -c:a aac -shortest");
            var mPat = await Imp(patF);

            // ---------- 1. bins ----------
            {
                var ed = new Editor();
                var a = new MediaItem { Path = @"C:\x\interview.mp4", Type = ClipKind.Video, Keywords = "church, sermon" };
                var b = new MediaItem { Path = @"C:\x\music.wav", Type = ClipKind.Audio, Rating = 4, Words = new() { new Word("Hallelujah", 1, 2) } };
                var c = new MediaItem { Path = @"C:\x\logo.png", Type = ClipKind.Image, Label = MarkerColor.Red };
                ed.Media.AddRange(new[] { a, b, c });
                var foot = ed.NewBin("", "Footage");
                var day = ed.NewBin(foot, "Day 1");
                var foot2 = ed.NewBin("", "Footage");
                Check(foot == "Footage" && day == "Footage/Day 1" && foot2 == "Footage 2", $"new bins: {foot}, {day}, a taken name gets a number ({foot2})");
                ed.MoveToBin(new[] { a }, day); ed.MoveToBin(new[] { b }, foot);
                Check(Bins.Children(ed.Bins, "").SequenceEqual(new[] { "Footage", "Footage 2" }) && Bins.Children(ed.Bins, foot).SequenceEqual(new[] { day }), "bins inside bins");
                Check(ed.RenameBin(foot, "Shoot") && a.Bin == "Shoot/Day 1" && b.Bin == "Shoot" && ed.Bins.Contains("Shoot/Day 1") && !ed.RenameBin("Shoot", "Footage 2"),
                      "renaming a bin moves what is inside it (bins and media); a taken name is refused");
                ed.DeleteBin("Shoot");
                Check(a.Bin == "Day 1" && b.Bin == "" && ed.Bins.Contains("Day 1") && !ed.Bins.Contains("Shoot") && ed.Media.Count == 3, "deleting a bin moves its content up — nothing leaves the project");
                Check(Bins.Matches(a, "sermon") && Bins.Matches(b, "hallelujah") && Bins.Matches(c, "red") && !Bins.Matches(a, "music"), "search: keywords, what is said, label names");
                Check(Bins.Passes(b, "Rated", _ => true) && !Bins.Passes(a, "Rated", _ => true) && Bins.Passes(c, "Labelled", _ => true) && Bins.Passes(c, "Images", _ => true) && Bins.Passes(a, "Not used", _ => false),
                      "filters: rated, labelled, images, not used");
                Check(Bins.Sort(ed.Media, "Rating").First() == b && Bins.Sort(ed.Media, "Name").First() == a && Bins.Sort(ed.Media, "Label").First() == c, "sort by rating / name / label");
                var pf = new ProjectFile { Media = ed.Media, Bins = ed.Bins, Sequences = ed.Sequences, Active = ed.Seq.Id };
                var back = JsonSerializer.Deserialize<ProjectFile>(JsonSerializer.Serialize(pf))!;
                Check(back.Bins!.Contains("Day 1") && back.Bins.Contains("Footage 2") && back.Media[0].Bin == "Day 1" && back.Media[1].Rating == 4 && back.Media[2].Label == MarkerColor.Red
                      && back.Media[0].Keywords == "church, sermon" && back.Media[1].Words![0].W == "Hallelujah", "the project file keeps bins (an empty one too), labels, ratings, keywords, transcripts");
            }

            // ---------- 2. auto-save ----------
            {
                AutoSave.DirOverride = Path.Combine(dir, "autosave"); if (Directory.Exists(AutoSave.DirOverride)) Directory.Delete(AutoSave.DirOverride, true);
                var key = AutoSave.Key(Path.Combine(dir, "My Film.cutpro"));
                var t0 = new DateTime(2026, 10, 27, 12, 0, 0);
                for (int i = 0; i < 5; i++) AutoSave.WriteVersion(key, "{\"v\":" + i + "}", 3, t0.AddMinutes(i * 5));
                var vs = AutoSave.Versions(key);
                Check(key.StartsWith("My Film-") && vs.Count == 3 && vs[0].Time == t0.AddMinutes(20) && File.ReadAllText(vs[0].File).Contains("4") && vs[2].Time == t0.AddMinutes(10),
                      $"versions: the newest 3 kept, newest first ({string.Join(", ", vs.Select(v => v.Time.ToString("HH:mm")))})");
                Check(AutoSave.Versions(AutoSave.Key(Path.Combine(dir, "Other.cutpro"))).Count == 0, "another project's versions are its own");
                int me = AutoSave.Pid;
                AutoSave.Pid = 999001; AutoSave.WriteRecovery("{\"crashed\":1}", @"C:\p\a.cutpro", t0);
                AutoSave.Pid = 999002; AutoSave.WriteRecovery("{\"running\":1}", null, t0);
                AutoSave.Pid = me;
                var pend = AutoSave.Pending(pid => pid == 999002);
                Check(pend.Count == 1 && pend[0].Pid == 999001 && pend[0].Project == @"C:\p\a.cutpro" && File.ReadAllText(pend[0].Recovery).Contains("crashed"),
                      "recovery: the copy of a process that is gone is offered (with its project path), a running one's is not");
                AutoSave.Discard(pend[0]);
                Check(AutoSave.Pending(pid => pid == 999002).Count == 0, "a declined / recovered copy is forgotten");
                AutoSave.WriteRecovery("{}", null, t0); AutoSave.EndSession();
                Check(!File.Exists(Path.Combine(AutoSave.Dir, $"recovery-{me}.cutpro")), "a clean exit leaves nothing to recover");
                AutoSave.DirOverride = null;
            }

            // ---------- 3. scene edit detection ----------
            {
                var cuts = Gen("cuts.mp4", "-f lavfi -i \"testsrc2=s=320x180:r=30:d=2,hue=h=0\" -f lavfi -i \"color=c=blue:s=320x180:r=30:d=1.5\" -f lavfi -i \"mandelbrot=s=320x180:r=30\" " +
                               "-f lavfi -i sine=frequency=300:duration=6 -filter_complex \"[0:v]format=yuv420p[a];[1:v]format=yuv420p[b];[2:v]trim=duration=2.5,format=yuv420p[c];[a][b][c]concat=n=3:v=1:a=0[v]\" -map \"[v]\" -map 3:a -c:v libx264 -crf 18 -g 300 -c:a aac -shortest");
                var mc = await Imp(cuts);
                var found = SceneDetect.Find(ff, mc, 0, mc.Duration, 50, 0.5, CancellationToken.None);
                Check(found.Count == 2 && Math.Abs(found[0] - 2.0) < 1e-6 && Math.Abs(found[1] - 3.5) < 1e-6, $"shot changes at 2.0 and 3.5 s found to the frame (got {string.Join(", ", found.Select(f => f.ToString("0.000", inv)))})");
                var ed = new Editor(); ed.Media.Add(mc);
                ed.AddMedia(mc, Tracks.V1, 0);
                ed.Edit(() => { foreach (var c in ed.Clips) { c.Start = 1; c.In = 0.5; c.Dur = 5; } });
                var v = ed.Clips.First(c => c.Kind == ClipKind.Video);
                var inRange = SceneDetect.Find(ff, mc, TimeMap.Span(v).lo, TimeMap.Span(v).hi, 50, 0.5, CancellationToken.None);
                int n = ed.SceneEdits(v, inRange, true);
                var starts = ed.Clips.Where(c => c.Kind == ClipKind.Video).Select(c => c.Start).OrderBy(x => x).ToList();
                var aStarts = ed.Clips.Where(c => c.Kind == ClipKind.Audio).Select(c => c.Start).OrderBy(x => x).ToList();
                Check(n == 2 && starts.Count == 3 && Math.Abs(starts[1] - 2.5) < 1e-6 && Math.Abs(starts[2] - 4.0) < 1e-6 && aStarts.SequenceEqual(starts),
                      $"cut through a clip at 1 s with In 0.5 s: pieces start at {string.Join(", ", starts.Select(s => s.ToString("0.00", inv)))} (the linked sound too)");
                Check(ed.UndoLabels.First() == "Scene Edit Detection", "one undo step");
                ed.Undo();
                int mk = ed.SceneEdits(ed.Clips.First(c => c.Kind == ClipKind.Video), inRange, false);
                var vv = ed.Clips.First(c => c.Kind == ClipKind.Video);
                Check(mk == 2 && vv.Markers?.Count == 2 && Math.Abs(vv.Markers.Min(x => Editor.TimelineTime(vv, x)!.Value) - 2.5) < 1e-6, "or clip markers at the shot changes");
            }

            // ---------- 4. synchronize by sound ----------
            {
                // a "camera" with scratchy sound and a "recorder" with the same sound 1.2345 s later, quieter, filtered, with its own noise
                var cam = Gen("cam.wav", "-f lavfi -i \"anoisesrc=d=20:c=pink:r=48000:a=0.3:seed=7,volume='if(lt(mod(t,1.3),0.4),1,0.15)':eval=frame\" -ac 2 -c:a pcm_s16le");
                var rec = Gen("rec.wav", $"-i \"{cam}\" -f lavfi -i \"anoisesrc=d=25:c=white:r=48000:a=0.05:seed=3\" -filter_complex \"[0:a]adelay=1234.5ms:all=1,lowpass=f=6000,volume=0.5[d];[d][1:a]amix=inputs=2:duration=first:normalize=0[o]\" -map \"[o]\" -ac 2 -c:a pcm_s16le");
                var mCam = await Imp(cam); var mRec = await Imp(rec);
                var sw = Stopwatch.StartNew();
                var r = AudioSync.Between(mCam, mRec);
                Check(r != null && Math.Abs(r.Lag - 1.2345) < 0.0002 && r.Confidence > 8, $"the lag: {r?.Lag:0.00000} s (made 1.23450 s, ±0.2 ms), confidence {r?.Confidence:0} ({sw.ElapsedMilliseconds} ms)");
                var ed = new Editor(); ed.Media.AddRange(new[] { mCam, mRec });
                ed.AddMedia(mCam, Tracks.A1, 0); ed.AddMedia(mRec, Tracks.A(2), 3);
                var c1 = ed.Clips.First(c => c.MediaId == mCam.Id); var c2 = ed.Clips.First(c => c.MediaId == mRec.Id);
                ed.Edit(() => { c1.Start = 2; c1.In = 1; c1.Dur = 10; c2.Start = 7; c2.In = 0; c2.Dur = 15; });
                var failed = ed.SyncClips(c1, new[] { (c2, r!.Lag) });
                double k1 = c1.Start - c1.In, k2 = c2.Start - c2.In;
                Check(failed.Count == 0 && Math.Abs((k1 - r.Lag) - k2) < 1e-9 && Math.Abs(c2.Start * 30 - Math.Round(c2.Start * 30)) < 1e-6 && c2.In >= 0,
                      $"the recorder's clip moved: its sound plays with the camera's (offset exact, start on a frame {c2.Start:0.000} s, In {c2.In:0.0000} s)");
                Check(ed.UndoLabels.First() == "Synchronize", "one undo step");
            }

            // ---------- 5. beats ----------
            {
                var click = Gen("click120.wav", "-f lavfi -i \"aevalsrc='if(lt(mod(t-0.37+10,0.5),0.012)*gte(t,0.37),sin(2*PI*2000*t)*0.8,0)':s=44100:d=20\" -ac 2 -c:a pcm_s16le");
                var loop = Gen("loop96.wav", "-f lavfi -i \"aevalsrc='0.9*sin(2*PI*55*t)*exp(-30*mod(t,0.625))*lt(mod(t,1.25),0.625)+0.9*sin(2*PI*55*t)*exp(-30*mod(t,0.625))*gte(mod(t,1.25),0.625)+0.25*sin(2*PI*7000*t)*exp(-80*mod(t+0.3125,0.625))':s=44100:d=20\" -f lavfi -i \"anoisesrc=d=20:c=pink:a=0.02\" -filter_complex \"[0:a][1:a]amix=inputs=2:normalize=0[o]\" -map \"[o]\" -ac 2 -c:a pcm_s16le");
                var mClick = await Imp(click); var mLoop = await Imp(loop);
                var pc = Pcm.Mono(mClick, 0, mClick.Duration, 24000)!;
                var br = Beats.Detect(pc, 24000)!;
                var expect = Enumerable.Range(0, 40).Select(i => 0.37 + i * 0.5).Where(t => t < mClick.Duration - 0.3).ToList();
                var errs = expect.Select(t => br.Times.Count > 0 ? br.Times.Min(x => Math.Abs(x - t)) : 9).ToList();
                double mean = br.Times.Count > 0 ? expect.Select(t => br.Times.OrderBy(x => Math.Abs(x - t)).First() - t).Average() : 9;
                Check(Math.Abs(br.Bpm - 120) < 0.5 && errs.Max() < 0.010, $"120 BPM clicks: {br.Bpm:0.00} BPM, {br.Times.Count} beats, worst {errs.Max() * 1000:0.0} ms off (mean {mean * 1000:+0.0;-0.0} ms)");
                var bl = Beats.Detect(Pcm.Mono(mLoop, 0, mLoop.Duration, 24000)!, 24000)!;
                var lerr = Enumerable.Range(0, 30).Select(i => i * 0.625).Where(t => t > 0.3 && t < mLoop.Duration - 0.5).Select(t => bl.Times.Min(x => Math.Abs(x - t))).ToList();
                Check(Math.Abs(bl.Bpm - 96) < 1 && lerr.Max() < 0.02, $"a 96 BPM kick / hat loop with noise: {bl.Bpm:0.00} BPM, worst {lerr.Max() * 1000:0.0} ms off");
                var ed = new Editor(); ed.Media.Add(mClick); ed.Media.Add(mPat);
                int nb = ed.BeatMarkers(br.Times.Select(t => t + 1).ToList(), 4, br.DownbeatPhase);
                Check(nb == (br.Times.Count - br.DownbeatPhase + 3) / 4 && ed.Markers.All(m => m.Name == "Beat 1") && ed.UndoLabels.First() == "Beat Markers", $"every bar: {nb} markers, all downbeats");
                ed.BeatMarkers(Enumerable.Range(0, 20).Select(i => 1 + i * 0.5).ToList(), 1, 0);
                Check(ed.BeatTimes().Count == 20 && ed.Markers.Count == 20, "new beat markers replace the old ones");
                ed.AddMedia(mPat, Tracks.V1, 1);
                ed.Edit(() => { var v0 = ed.Clips.First(c => c.Kind == ClipKind.Video); foreach (var c in ed.Linked(v0)) c.Dur = 1.3; });
                ed.AddMedia(mPat, Tracks.V1, 2.3);
                ed.Edit(() => { var v1 = ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).Last(); foreach (var c in ed.Linked(v1)) { c.In = 3; c.Dur = 2.2; } });
                var vids = ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).ToList();
                ed.SnapToBeats(vids);
                vids = ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).ToList();
                var auds = ed.Clips.Where(c => c.Kind == ClipKind.Audio).OrderBy(c => c.Start).ToList();
                Check(Math.Abs(vids[0].End - 2.5) < 1e-6 && Math.Abs(vids[1].Start - 2.5) < 1e-6 && Math.Abs(vids[1].End - 4.5) < 1e-6 && auds[1].Start == vids[1].Start && auds[1].Dur == vids[1].Dur,
                      $"cut to the beat: edits at {vids[0].End:0.00} and {vids[1].End:0.00} s (beats every 0.5 s; nearest to 2.3 and 4.5), the sound follows");
            }

            // ---------- 6. auto-ducking ----------
            {
                var speech = Gen("speech.wav", "-f lavfi -i \"aevalsrc='0.5*sin(2*PI*220*t)*(between(t,2,4)+between(t,7,9.5))':s=48000:d=12\" -ac 2 -c:a pcm_s16le");
                var music = Gen("music.wav", "-f lavfi -i \"sine=frequency=660:sample_rate=48000:duration=12\" -ac 2 -c:a pcm_s16le");
                var mS = await Imp(speech); var mM = await Imp(music);
                var ed = new Editor(); ed.Media.AddRange(new[] { mS, mM });
                ed.AddMedia(mS, Tracks.A1, 0); ed.AddMedia(mM, Tracks.A(2), 0);
                var mus = ed.Clips.First(c => c.MediaId == mM.Id); var dia = ed.Clips.First(c => c.MediaId == mS.Id);
                var st = new Ducking.Settings(15, 0.3, 0.8);
                var sp = Ducking.Speech(ed, new[] { dia }, 0, 12, st);
                Check(sp.Count == 2 && Math.Abs(sp[0].A - 2) < 0.06 && Math.Abs(sp[0].B - 4) < 0.06 && Math.Abs(sp[1].A - 7) < 0.06 && Math.Abs(sp[1].B - 9.5) < 0.06,
                      $"speech found at {string.Join(", ", sp.Select(s => $"{s.A:0.00}–{s.B:0.00}"))} s (made 2–4, 7–9.5)");
                int dips = Ducking.Apply(ed, new[] { mus }, new[] { dia }, st);
                double V(double t) => Anim.Value(mus, AnimProp.Volume, t);
                double low = 100 * Math.Pow(10, -15 / 20.0);
                Check(dips == 2 && Math.Abs(V(3) - low) < 0.5 && Math.Abs(V(8) - low) < 0.5 && Math.Abs(V(0.5) - 100) < 0.01 && Math.Abs(V(5.5) - 100) < 0.5 && Math.Abs(V(11.5) - 100) < 0.01,
                      $"music volume: {V(0.5):0} % before, {V(3):0.0} % under speech (−15 dB = {low:0.0} %), {V(5.5):0} % between, {V(11.5):0} % after");
                Check(V(1.85) > low + 5 && V(1.85) < 100 - 5, $"it fades down over the attack ({V(1.85):0} % halfway)");
                ed.Undo();
                var mus2 = ed.Clips.First(c => c.Id == mus.Id);
                Check(!Anim.HasKeys(mus2, AnimProp.Volume) && mus2.Volume == 100, "one undo step takes it away");
            }

            // ---------- 7. text-based editing ----------
            {
                var ed = new Editor(); ed.Media.Add(mPat);
                mPat.Words = new() { new("Hello", 1.0, 1.4), new("um,", 1.6, 1.8), new("this", 2.0, 2.3), new("is", 2.35, 2.5), new("a", 2.55, 2.6), new("test.", 2.7, 3.2), new("After", 6.0, 6.5), new("pause.", 6.6, 7.0) };
                ed.AddMedia(mPat, Tracks.V1, 0);
                ed.Edit(() => { foreach (var c in ed.Clips) { c.Start = 10; c.In = 0.5; c.Dur = 8; } });
                var all = Transcript.Of(ed);
                Check(all.Count == 8 && Math.Abs(all[0].T0 - 10.5) < 1e-9 && all[0].W == "Hello", $"words through a clip at 10 s with In 0.5 s: “Hello” at {all[0].T0:0.00} s");
                ed.CutClips(ed.Clips.ToList(), 13);
                ed.Edit(() => { foreach (var c in ed.Clips.Where(c => c.Start >= 13 - 1e-9)) c.Start += 2; });
                all = Transcript.Of(ed);
                Check(all.Count == 8 && Math.Abs(all.First(w => w.W == "After").T0 - 17.5) < 1e-9, "after a cut and a move the transcript follows (no new transcription)");
                Check(Transcript.IsFiller(all[1].W) && !Transcript.IsFiller(all[2].W), "“um,” is a filler word, “this” isn't");
                var rg = Transcript.Ranges(all, new[] { 1, 3, 4 });
                Check(rg.Count == 2 && Math.Abs(rg[0].A - 11.06) < 1e-9 && Math.Abs(rg[0].B - 11.34) < 1e-9 && Math.Abs(rg[1].A - 11.81) < 1e-9 && Math.Abs(rg[1].B - 12.14) < 1e-9,
                      $"ranges of “um,” and “is a”: {string.Join(", ", rg.Select(x => $"{x.A:0.00}–{x.B:0.00}"))}");
                double end0 = ed.End;
                ed.RemoveRanges(rg);
                double removed = end0 - ed.End;
                var after = Transcript.Of(ed);
                Check(ed.UndoLabels.First() == "Delete Words" && after.Count == 5 && string.Join(" ", after.Select(w => w.W)) == "Hello this test. After pause." && Math.Abs(removed - Math.Round(rg.Sum(x => x.B - x.A) * 30) / 30) < 1.01 / 30,
                      $"deleted in one step: “{string.Join(" ", after.Select(w => w.W))}”, the sequence {removed:0.00} s shorter");
                ed.Undo();
                Check(Transcript.Of(ed).Count == 8, "undo brings the words back");
                var ps = Transcript.Pauses(Transcript.Of(ed), 1, 0.4);
                Check(ps.Count == 1 && Math.Abs(ps[0].B - ps[0].A - (16.0 - 13.2 + 2 - 0.4 - 0.0)) < 0.6, $"one long pause to shorten ({ps.Sum(p => p.B - p.A):0.00} s)");
                mPat.Words = null;
            }

            // ---------- 8. drops, replace, fit to fill ----------
            {
                var ed = new Editor(); ed.Media.Add(mPat);
                ed.AddMedia(mPat, Tracks.V1, 0);                                      // 0 … 12 on V1 + A1
                ed.Edit(() => { foreach (var c in ed.Clips) c.Dur = 10; });
                var img = new MediaItem { Path = Path.Combine(dir, "pat.mp4"), Type = ClipKind.Image, Width = 640, Height = 360 };
                ed.Media.Add(img);
                ed.DropMedia(img, Tracks.V1, 3, true, true, false);
                var v1 = ed.Clips.Where(c => c.Track == Tracks.V1).OrderBy(c => c.Start).ToList();
                Check(v1.Count == 3 && Math.Abs(v1[0].End - 3) < 1e-9 && v1[1].MediaId == img.Id && Math.Abs(v1[2].Start - 8) < 1e-9 && ed.Clips.Count(c => c.Track == Tracks.A1) == 1,
                      "a drop overwrites: the picture replaces 3–8 s on V1 (audio untouched: pictures have none)");
                ed.Undo();
                ed.DropMedia(img, Tracks.V1, 3, true, true, true);
                var shifted = ed.Clips.Where(c => c.Start > 3).OrderBy(c => c.Start).ToList();
                Check(Math.Abs(ed.End - 15) < 1e-9 && shifted.Count(c => Math.Abs(c.Start - 8) < 1e-9) == 2, $"Ctrl+drop inserts: everything after 3 s moves 5 s later on every track (end {ed.End:0} s)");
                ed.Undo();
                ed.DropMedia(mPat, Tracks.V(2), 12, true, true, false);
                var lastV = ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).Last();
                var lastA = ed.Clips.First(c => c.Kind == ClipKind.Audio && c.LinkId == lastV.LinkId);
                Check(lastV.Track == Tracks.V(2) && lastA.Track == Tracks.A(2), "a drop on V2 puts the sound on A2");
                ed.SetLocked(Tracks.V1, true);
                Check(ed.DropMedia(mPat, Tracks.V1, 20, true, true, false).Count == 0, "nothing drops on a locked track");
                ed.SetLocked(Tracks.V1, false);
                // replace edit
                var v0 = ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).First();
                ed.Edit(() => { v0.Grade = new ColorGrade { Saturation = 0 }; v0.In = 1; });
                var other = await Imp(Gen("other.mp4", "-f lavfi -i testsrc=s=640x360:r=30:d=12 -f lavfi -i sine=frequency=880:duration=12 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest"));
                ed.Media.Add(other); other.SrcIn = 0.5;
                bool ok = ed.ReplaceClip(v0, other);
                var a0 = ed.Clips.First(c => c.Kind == ClipKind.Audio && c.LinkId == v0.LinkId);
                Check(ok && v0.MediaId == other.Id && a0.MediaId == other.Id && v0.Start == 0 && Math.Abs(v0.Dur - 10) < 1e-9 && v0.Grade?.Saturation == 0 && Math.Abs(v0.In - 0.5) < 1e-9,
                      "replace edit: same place, length and colour, the new media from its source In; the linked sound replaced too");
                var tiny = new MediaItem { Path = "x.mp4", Type = ClipKind.Video, Duration = 2 };
                ed.Media.Add(tiny);
                Check(!ed.ReplaceClip(v0, tiny), "a shorter media can't replace a 10 s clip");
                // fit to fill: 4 s of source into 2 s
                other.SrcIn = 2; other.SrcOut = 6;
                ed.MarkIn = 20; ed.MarkOut = 22;
                var made = ed.FitToFill(other, Tracks.V1, Tracks.A1);
                Check(made.Count == 2 && Math.Abs(made[0].Speed - 200) < 1e-6 && Math.Abs(made[0].Dur - 2) < 1e-9 && Math.Abs(TimeMap.Span(made[0]).hi - 6) < 1e-6 && Math.Abs(TimeMap.Span(made[0]).lo - 2) < 1e-6,
                      $"fit to fill: 4 s of source into a 2 s In–Out at {made.FirstOrDefault()?.Speed:0} %");
                // 4-point Insert / Overwrite: source In–Out + sequence In–Out, at sequence In (not the playhead)
                other.SrcIn = 1; other.SrcOut = 5;
                ed.MarkIn = 24; ed.MarkOut = 26; ed.Time = 0;
                var four = ed.PlaceSource(other, ed.Time, Tracks.V1, Tracks.A1, insert: false);
                Check(four.Count == 2 && Math.Abs(four[0].Start - 24) < 1e-9 && Math.Abs(four[0].Dur - 2) < 1e-9 && Math.Abs(four[0].Speed - 200) < 1e-6 && Math.Abs(four[0].In - 1) < 1e-9,
                      $"4-point overwrite: source In–Out into sequence In–Out at {four.FirstOrDefault()?.Speed:0} % at sequence In (playhead was 0)");
            }

            // ---------- 9. presets, colour match, reframe push-in ----------
            {
                EffectPresets.FileOverride = Path.Combine(dir, "presets.json"); if (File.Exists(EffectPresets.FileOverride)) File.Delete(EffectPresets.FileOverride);
                EffectPresets.Reload();
                var ed = new Editor(); ed.Media.Add(mPat);
                ed.AddMedia(mPat, Tracks.V1, 0); ed.AddMedia(mPat, Tracks.V1, 12);
                var vs = ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).ToList();
                ed.Edit(() => { vs[0].Grade = new ColorGrade { Temperature = 30 }; vs[0].CropL = 10; Anim.SetKey(vs[0], AnimProp.Opacity, vs[0].In + 1, 50); });
                EffectPresets.Add(EffectPresets.Make("My look", vs[0], ClipAttr.Color | ClipAttr.Opacity));
                EffectPresets.Reload();
                var p = EffectPresets.Get("My look");
                ed.Select(vs[1]);
                int n = ed.PasteAttributes(p!.Source(), p.Attrs);
                Check(p != null && n == 1 && vs[1].Grade?.Temperature == 30 && vs[1].CropL == 0 && Anim.HasKeys(vs[1], AnimProp.Opacity) && EffectPresets.BuiltIns.Length >= 5,
                      "a saved preset (read back from its file) applies its colour and opacity keys — not the crop it left out; built-in presets exist");
                // colour match: a cast picture back to the reference
                int w = 160, h = 90;
                var refPx = new byte[w * h * 4]; var castPx = new byte[w * h * 4];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    byte r = (byte)(40 + x), g = (byte)(30 + y * 2), b = (byte)(60 + (x + y) / 2);
                    refPx[i] = b; refPx[i + 1] = g; refPx[i + 2] = r; refPx[i + 3] = 255;
                    castPx[i] = (byte)Math.Min(255, b * 0.7 + 10); castPx[i + 1] = (byte)Math.Min(255, g * 1.1); castPx[i + 2] = (byte)Math.Min(255, r * 1.25 + 15); castPx[i + 3] = 255;
                }
                double d0 = ColorMatch.Distance(castPx, refPx);
                var grade = ColorMatch.Match(null, castPx, refPx);
                var fixedPx = (byte[])castPx.Clone(); Grade.Apply(Grade.For(grade)!, fixedPx, w, h, w * 4);
                double d1 = ColorMatch.Distance(fixedPx, refPx);
                Check(d1 < 2.5 && d0 > 10, $"colour match: a colour cast {d0:0.0} levels off → {d1:0.0} after the matched curves");
                // reframe push-in (16:9 in 16:9: nothing cropped, so only with zoom)
                Editor.SetSequence(1280, 720, 30, ReframeMode.Fill);
                var r0 = await Reframe.AnalyzeAsync(ed, vs[0], null, CancellationToken.None);
                var rz = await Reframe.AnalyzeAsync(ed, vs[0], null, CancellationToken.None, 12);
                Reframe.Apply(ed, new[] { rz! });
                Check(r0 == null && rz?.Zoom is { Count: 2 } && Math.Abs(Anim.Value(vs[0], AnimProp.Scale, vs[0].Start) - 100) < 0.01 && Math.Abs(Anim.Value(vs[0], AnimProp.Scale, vs[0].End - fr) - 112) < 0.01,
                      $"reframe push-in: Scale 100 → {Anim.Value(vs[0], AnimProp.Scale, vs[0].End - fr):0} % over the clip (without zoom nothing to reframe)");
                Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
                EffectPresets.FileOverride = null; EffectPresets.Reload();
            }

            // ---------- 10. EDL / OTIO, voice-over WAV ----------
            {
                var ed = new Editor();
                var tc = await Imp(Gen("tc.mov", "-f lavfi -i testsrc2=s=320x180:r=30:d=10 -f lavfi -i sine=frequency=500:duration=10 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest -timecode 10:00:00:00 -metadata:s:v:0 reel_name=REEL01"));
                ed.Media.Add(tc); ed.Media.Add(mPat);
                ed.AddMedia(tc, Tracks.V1, 0); ed.AddMedia(mPat, Tracks.V1, 5);
                ed.Edit(() =>
                {
                    foreach (var c in ed.Clips.Where(c => c.MediaId == tc.Id)) { c.In = 2; c.Dur = 4; }
                    foreach (var c in ed.Clips.Where(c => c.MediaId == mPat.Id)) { c.Start = 5; c.Dur = 3; c.Speed = 200; }
                });
                ed.AddMarker(1, "Intro");
                var edl = Interchange.Edl(ed, "Test");
                var ev = edl.Split("\r\n").Where(l => l.Length > 3 && char.IsDigit(l[0])).ToList();
                Check(ev.Count == 4 && ev[0].Contains("REEL01") && ev[0].Contains("10:00:02:00 10:00:06:00 01:00:00:00 01:00:04:00") && ev[1].Contains("01:00:05:00 01:00:08:00"),
                      $"EDL: 4 events (2 picture, 2 sound), the reel and source timecode from the file's tags: “{ev.FirstOrDefault()}”");
                Check(edl.Contains("M2   PAT") && edl.Contains("060.0") && edl.Contains("* MARKER: 01:00:01:00"), "EDL: the 200 % clip has an M2 line (60 fps), markers as comments");
                var otio = Interchange.Otio(ed, "Test");
                using var doc = JsonDocument.Parse(otio);
                var tracks = doc.RootElement.GetProperty("tracks").GetProperty("children");
                var v1 = tracks[0].GetProperty("children");
                double Dur(JsonElement e) => e.TryGetProperty("source_range", out var sr) && sr.ValueKind == JsonValueKind.Object ? sr.GetProperty("duration").GetProperty("value").GetDouble() : 0;
                double total = v1.EnumerateArray().Sum(Dur);
                bool gap = v1.EnumerateArray().Any(e => e.GetProperty("OTIO_SCHEMA").GetString() == "Gap.1");
                bool warp = otio.Contains("LinearTimeWarp.1") && otio.Contains("\"time_scalar\": 2");
                Check(tracks.GetArrayLength() == Tracks.Count && tracks[0].GetProperty("kind").GetString() == "Video" && Math.Abs(total - 240) < 1e-9 && gap && warp && otio.Contains("file:///"),
                      $"OTIO: {tracks.GetArrayLength()} tracks, V1 = {total} frames (clip, gap, clip at 2× speed), media as file URLs");
                // voice-over: the WAV writer
                var wavF = Path.Combine(dir, "vo.wav");
                using (var wv = new WavWriter(wavF, 48000, 1))
                {
                    var buf = new byte[48000 * 2];
                    for (int i = 0; i < 48000; i++) BitConverter.TryWriteBytes(buf.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * 440 * i / 48000.0) * 8000));
                    wv.Write(buf);
                }
                var vo = await MediaProbe.ProbeAsync(wavF);
                Check(vo != null && vo.Type == ClipKind.Audio && Math.Abs(vo.Duration - 1) < 0.01, $"a recorded voice-over WAV reads back ({vo?.Duration:0.000} s)");
                ed.Media.Add(vo!);
                var placed = ed.DropMedia(vo!, Tracks.A(2), 2.0, false, true, false, 0.013);
                Check(placed.Count == 1 && placed[0].Start == 2 && Math.Abs(placed[0].In - 0.013) < 1e-9, "a recording that began between frames lands on the frame with its In moved (in sync)");
            }
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "hightest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static void Exec(string ff, string args) { using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!; p.StandardError.ReadToEnd(); p.WaitForExit(); }
}
