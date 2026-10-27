using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// CutPro.exe --tracktest dir: tracks beyond the old 3 + 2.
/// - the layout model: adding tracks at the top / in the middle / above A1 moves every clip and every per-row setting with its track (hidden,
///   fader, automation, effects, name), deleting a track takes its clips, the limits (1 … 32 of each), Delete Empty Tracks, one undo / redo each;
/// - locks: a locked track's clips can't be selected, cut, rippled, inserted over or edited onto;
/// - the project file (version 3) keeps counts, names, locks and heights; a version 2 file opens as 3 + 2;
/// - sequences: each has its own layout (switching makes it active), duplicate / nest copy it, a nested render's signature includes it;
/// - the mixer with 40 tracks (rows past the old 32-bit masks), export = mixer;
/// - preview vs export of a 7-video-track sequence (picture-in-picture on every track, an adjustment layer, a blend mode, a track matte and a
///   cross-dissolve high up the stack), a 4-audio-track mix, and that sequence nested in a 3 + 2 one (its render over black = its export).
/// </summary>
public static class TrackTest
{
    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        try
        {
            Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
            string Gen(string name, string args)
            {
                var f = Path.Combine(dir, name);
                if (!File.Exists(f)) Run(ff, $"-y -v error {args} \"{f}\"");
                return f;
            }
            var landSrc = @"C:\Windows\Web\Wallpaper\ThemeC\img29.jpg";
            var bgF = Gen("bg.png", File.Exists(landSrc) ? $"-i \"{landSrc}\" -vf scale=1280:-2,crop=1280:720 -frames:v 1" : "-f lavfi -i testsrc2=s=1280x720 -frames:v 1");
            var patF = Gen("pat.mp4", "-f lavfi -i testsrc2=s=640x360:r=30:d=8 -c:v libx264 -crf 10 -pix_fmt yuv420p");
            var toneF = new[] { 220, 330, 440, 550 }.Select(f => Gen($"tone{f}.wav", $"-f lavfi -i \"aevalsrc=exprs='0.2*sin(2*PI*{f}*t)*(0.7+0.3*sin(2*PI*2*t))|0.18*sin(2*PI*{f * 1.5}*t)':s=48000:d=8\"")).ToArray();
            async Task<MediaItem> Imp(string f) => await MediaProbe.ProbeAsync(f) ?? throw new Exception("import " + f);
            var patAF = Gen("patA.mp4", "-f lavfi -i testsrc2=s=640x360:r=30:d=6 -f lavfi -i sine=frequency=500:duration=6 -c:v libx264 -crf 14 -pix_fmt yuv420p -c:a aac -shortest");
            var mBg = await Imp(bgF); var mPat = await Imp(patF); var mPatA = await Imp(patAF);
            var tones = new List<MediaItem>(); foreach (var f in toneF) tones.Add(await Imp(f));
            static string J(object o) => JsonSerializer.Serialize(o);

            // ---------- 1. the layout model ----------
            {
                var ed = new Editor(); ed.Media.AddRange(new[] { mBg, mPat }); ed.Media.AddRange(tones);
                Check(Tracks.Video == 3 && Tracks.Audio == 2 && Tracks.V1 == 2 && Tracks.A1 == 3 && string.Join(" ", Enumerable.Range(0, 5).Select(Tracks.Name)) == "V3 V2 V1 A1 A2",
                      "a new sequence: 3 video + 2 audio tracks, rows V3 V2 V1 A1 A2 (projects from before mean the same)");
                var v1 = new Clip { MediaId = mPat.Id, Kind = ClipKind.Video, Track = 2, Start = 0, Dur = 3, LinkId = "L", NoAudio = true };
                var a1 = new Clip { MediaId = tones[0].Id, Kind = ClipKind.Audio, Track = 3, Start = 0, Dur = 3, LinkId = "L" };
                var v2 = new Clip { MediaId = mPat.Id, Kind = ClipKind.Video, Track = 1, Start = 1, Dur = 2, NoAudio = true };
                var a2 = new Clip { MediaId = tones[1].Id, Kind = ClipKind.Audio, Track = 4, Start = 0.5, Dur = 2 };
                ed.Clips.AddRange(new[] { v1, a1, v2, a2 });
                ed.TrackOff[1] = true;                                                     // V2 hidden
                ed.Mix.Channels[4].Gain = -6; ed.Mix.Channels[4].GainKeys = new() { new() { T = 0, V = -12 }, new() { T = 2, V = 0 } };
                ed.Mix.Channels[3].Fx = new() { AudioDsp.New("Reverb", ("size", 45)) };
                ed.RenameTrack(4, "Music");
                ed.SetTrackHeight(3, 96);
                string before = J(ed.Seq);
                Check(Tracks.Name(4) == "Music" && Tracks.Label(4) == "A2" && Math.Abs(Tracks.Height(3) - 96) < 1e-9, "rename A2 “Music”, A1 96 px tall");

                bool ok = ed.AddTracks(2, 0, 0, Tracks.Count);                             // two video tracks on top: V4, V5
                Check(ok && Tracks.Video == 5 && v1.Track == 4 && v2.Track == 3 && a1.Track == 5 && a2.Track == 6 && Tracks.Number(v1.Track) == 1 && Tracks.Label(v2.Track) == "V2"
                      && ed.TrackOff[3] && !ed.TrackOff[0] && !ed.TrackOff[1] && ed.Mix.Channels[6].Gain == -6 && ed.Mix.Channels[6].GainKeys?.Count == 2 && ed.Mix.Channels[5].Fx?.Count == 1
                      && Tracks.Name(6) == "Music" && Math.Abs(Tracks.Height(5) - 96) < 1e-9 && Tracks.Name(0) == "V5" && !ed.Clips.Any(c => c.Track < 2) && ed.Mix.Channels.Length == 7 && ed.TrackOff.Length == 7,
                      "Add 2 video tracks on top: every clip, hidden flag, fader / automation / effect, name and height moves with its track (V1 is row 4 now)");
                ed.Undo();
                Check(J(ed.Seq) == before && Tracks.Video == 3 && ed.Clips.First(c => c.Id == v1.Id).Track == 2, "one undo: the sequence exactly as before (3 + 2)");
                ed.Redo();
                Check(Tracks.Video == 5 && ed.Clips.First(c => c.Id == v1.Id).Track == 4, "redo: 5 video tracks again");
                // a track between V1 and V2 (below V2): the new one becomes V2, the old V2 is V3
                var cv1 = ed.Clips.First(c => c.Id == v1.Id); var cv2 = ed.Clips.First(c => c.Id == v2.Id); var ca2 = ed.Clips.First(c => c.Id == a2.Id);
                ok = ed.AddTrack(cv2.Track, below: true);
                Check(ok && Tracks.Video == 6 && cv1.Track == 5 && cv2.Track == 3 && Tracks.Label(cv2.Track) == "V3" && !ed.Clips.Any(c => c.Track == 4) && Tracks.Label(4) == "V2" && ed.TrackOff[3],
                      "Add Track below V2: the new (empty) track is V2, the old V2 becomes V3 with its clip and hidden flag");
                // above A1: the old A1 becomes A2, “Music” A3
                ok = ed.AddTracks(0, 0, 1, Tracks.A1);
                Check(ok && Tracks.Audio == 3 && Tracks.Label(ca2.Track) == "A3" && Tracks.Name(ca2.Track) == "Music" && ed.Mix.Channels[ca2.Track].Gain == -6 && !ed.Clips.Any(c => c.Track == Tracks.A1),
                      "Add an audio track above A1: “Music” is A3 now, its fader with it");
                // deleting a track takes its clips; the linked sound stays
                int n0 = ed.Clips.Count; string pre = J(ed.Seq);
                ok = ed.DeleteTracks(new[] { cv1.Track });
                Check(ok && ed.Clips.Count == n0 - 1 && !ed.Clips.Any(c => c.Id == v1.Id) && ed.Clips.Any(c => c.Id == a1.Id) && Tracks.Video == 5, "Delete Track (V1 with its clip): the clip goes, its linked sound stays");
                ed.Undo();
                Check(J(ed.Seq) == pre, "undo the delete: the track and its clip are back");
                // limits
                bool lastV = ed.DeleteTracks(Enumerable.Range(0, Tracks.Video)), tooMany = ed.AddTracks(Tracks.MaxVideo, 0, 0, Tracks.Count);
                Check(!lastV && !tooMany && Tracks.Video == 6, $"limits: the last video track can't go, at most {Tracks.MaxVideo} video tracks");
                int k = ed.DeleteEmptyTracks();
                Check(k > 0 && Enumerable.Range(0, Tracks.Count).All(r => ed.Clips.Any(c => c.Track == r) || r == Tracks.V1 || r == Tracks.A1) && Tracks.Name(ed.Clips.First(c => c.Id == a2.Id).Track) == "Music",
                      $"Delete Empty Tracks: {k} gone, every track left has clips (or is V1 / A1)");
                ed.Undo();
            }

            // ---------- 2. locks ----------
            {
                var ed = new Editor(); ed.Media.AddRange(new[] { mBg, mPat }); ed.Media.AddRange(tones);
                ed.Media.Add(mPatA);
                Clip Add(int tr, double s, double d, MediaItem m) { var c = new Clip { MediaId = m.Id, Kind = m.Type == ClipKind.Audio ? ClipKind.Audio : ClipKind.Video, Track = tr, Start = s, Dur = d, NoAudio = true }; ed.Clips.Add(c); return c; }
                var c1 = Add(2, 0, 2, mPat); var c1b = Add(2, 3, 2, mPat);                 // V1: 0–2, gap, 3–5
                var c2 = Add(1, 0.5, 4, mPat);                                              // V2: 0.5–4.5 (locked below)
                var ca = Add(3, 0, 5, tones[0]);                                            // A1
                ed.SetLocked(1, true);
                ed.Select(c2); bool selLocked = ed.IsSel(c2);
                ed.SelectAll(); bool allLocked = ed.IsSel(c2);
                ed.Time = 1; ed.AddEdit(true);
                bool cut = ed.Clips.Count(c => c.Track == 1) > 1;
                double s2 = c2.Start;
                ed.MarkIn = 2; ed.MarkOut = 3; ed.LiftExtract(true);                        // extract 2–3 on every unlocked track
                var place = ed.PlaceSource(mPat, 0.25, 1, null, insert: false);
                ed.AddMedia(mPat, 1, 6); var landed = ed.Clips.LastOrDefault();
                ed.SelectGap(1, 0, c2.Start); ed.DeleteSelection(true);
                Check(!selLocked && !allLocked && !cut && Math.Abs(c2.Start - s2) < 1e-9 && Math.Abs(c2.Dur - 4) < 1e-9 && place.Count == 0 && landed is { Track: not 1 },
                      "a locked track (V2): its clip can't be selected (also by Select All), Add Edit and Extract leave it alone, nothing is overwritten or dropped onto it, its gaps don't ripple");
                Check(Math.Abs(c1b.Start - 2) < 1e-6 && ed.Clips.Any(c => c.Track == 3 && Math.Abs(c.Start - 2) < 1e-6), "the unlocked tracks rippled by the extract (V1 and A1 one second earlier after 3 s)");
                ed.SetLocked(1, false); ed.Select(c2);
                Check(ed.IsSel(c2), "unlocked: it selects again");
                ed.SetLocked(3, true);
                var aBefore = J(ed.Clips.Where(c => c.Track == 3).ToList());
                var ins = ed.PlaceSource(mPatA, 0, 2, 3, insert: true);
                Check(ins.Count == 1 && ins[0].Track == 2 && J(ed.Clips.Where(c => c.Track == 3).ToList()) == aBefore, "Insert with A1 locked: only the picture goes in, A1's clips neither cut nor moved");
            }

            // ---------- 3. the project file ----------
            {
                var ed = new Editor(); ed.Media.Add(mPat);
                ed.AddTracks(2, 0, 2, Tracks.Count);                                       // 5 + 4
                ed.RenameTrack(0, "Titles"); ed.SetLocked(Tracks.A1, true); ed.SetTrackHeight(Tracks.V1, 120);
                ed.Clips.Add(new Clip { MediaId = mPat.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = 0, Dur = 2, NoAudio = true });
                var pf = new ProjectFile { Media = ed.Media, Sequences = ed.Sequences, Active = ed.Seq.Id };
                var json = JsonSerializer.Serialize(pf);
                var back = JsonSerializer.Deserialize<ProjectFile>(json)!;
                var e2 = new Editor(); e2.UseSequences(back.AllSequences(), back.Active);
                Check(back.Version == 3 && Tracks.Video == 5 && Tracks.Audio == 4 && Tracks.Name(0) == "Titles" && e2.Locked(Tracks.A1) && Math.Abs(Tracks.Height(Tracks.V1) - 120) < 1e-9
                      && e2.Clips[0].Track == Tracks.V1 && e2.TrackOff.Length == 9 && e2.Mix.Channels.Length == 9,
                      "project file (version 3): 5 + 4 tracks, a name, a lock, a height and the clip's row come back");
                // a version 2 file: no counts — 3 + 2
                var node = JsonNode.Parse(json)!;
                foreach (var s in node["Sequences"]!.AsArray()) { var o = s!.AsObject(); o.Remove("VideoTracks"); o.Remove("AudioTracks"); o.Remove("TrackInfo"); o["TrackOff"] = new JsonArray(false, false, false, false, false); }
                node["Version"] = 2;
                var old = JsonSerializer.Deserialize<ProjectFile>(node.ToJsonString())!;
                var e3 = new Editor(); e3.UseSequences(old.AllSequences(), old.Active);
                Check(Tracks.Video == 3 && Tracks.Audio == 2 && e3.TrackOff.Length == 5 && e3.Mix.Channels.Length == 5 && Tracks.Name(0) == "V3", "a version 2 file opens as 3 + 2 (rows as they were)");
            }

            // ---------- 4. sequences: each its own layout ----------
            {
                var ed = new Editor(); ed.Media.Add(mPat);
                var s1 = ed.Seq;
                ed.AddTracks(3, 0, 1, Tracks.Count);                                       // 6 + 3
                ed.Clips.Add(new Clip { MediaId = mPat.Id, Kind = ClipKind.Video, Track = 0, Start = 0, Dur = 2, NoAudio = true });   // on V6
                ed.Select(ed.Clips[0]);
                var (nested, err) = ed.NestSelection();
                Check(nested != null && nested.VideoTracks == 6 && nested.AudioTracks == 3 && nested.Clips[0].Track == 0, "Nest: the nested sequence has the same tracks (its clip still on V6)");
                var s2 = ed.NewSequence("Plain");
                Check(Tracks.Video == 3 && Tracks.Audio == 2 && ed.FormatOf(s1).Video == 6 && ed.FormatOf(s2).Video == 3, "a new sequence is 3 + 2 and active; the first keeps 6 + 3 (FormatOf carries the counts)");
                ed.Activate(s1);
                Check(Tracks.Video == 6 && Tracks.Audio == 3, "opening the first sequence again makes its 6 + 3 active");
                var dup = ed.DuplicateSequence(s1);
                Check(dup.VideoTracks == 6 && dup.AudioTracks == 3 && Tracks.Video == 6, "Duplicate keeps the tracks");
                // the same clips in another layout render differently (row 0 is V6 there, V3 here): the nested render's signature includes the layout
                var sA = new Sequence { Name = "A", Clips = new() { new Clip { MediaId = mPat.Id, Kind = ClipKind.Video, Track = 1, Start = 0, Dur = 1, NoAudio = true } } };
                var sB = new Sequence { Name = "B", VideoTracks = 4, Clips = sA.Clips }; sB.Fix();
                ed.Sequences.Add(sA); ed.Sequences.Add(sB); ed.RefreshSeqItems();
                Check(Nest.Take(ed, sA).VSig != Nest.Take(ed, sB).VSig, "nested render signature: same clips, different track counts → different renders");
            }

            // ---------- 5. the mixer with 40 tracks ----------
            {
                var ed = new Editor(); ed.Media.AddRange(tones);
                ed.AddTracks(17, 0, 18, Tracks.Count);                                      // 20 + 20
                int aLast = Tracks.A(20), aFirst = Tracks.A1;
                ed.Clips.Add(new Clip { MediaId = tones[2].Id, Kind = ClipKind.Audio, Track = aLast, Start = 0.5, Dur = 3 });
                ed.Clips.Add(new Clip { MediaId = tones[3].Id, Kind = ClipKind.Audio, Track = aFirst, Start = 1, Dur = 2.5 });
                ed.Mix.Channels[aLast].Gain = -3; ed.Mix.Channels[aLast].Pan = 50;
                foreach (var m in tones) { AudioCache.Ensure(m); AudioCache.WaitReady(m.Path, 30000); }
                var wav = Path.Combine(dir, "a40.wav");
                var e = await Exporter.RunAsync(ff, ed, wav, new Progress<double>(), CancellationToken.None, null, null, new ExportSettings { Video = "none", Container = "wav", Audio = "pcm" });
                Check(e == null, "export of a 20 + 20 sequence's sound" + (e == null ? "" : ": " + e));
                if (e == null)
                {
                    var (snr, lag) = AudioTest.Compare(AudioTest.MixOf(ed, 0, ed.End), AudioTest.Pcm(ff, wav));
                    Check(snr >= 50 && lag == 0, $"20 + 20 tracks: in-app mix = export {snr:0.0} dB, shift {lag} (a clip on A20 = row {aLast}, past the old 32-bit masks)");
                }
                // the meter slot of row 39
                var srcs = Mixer.Sources(ed, Transitions.Compute(ed), new()).ToArray();
                var peaks = new float[Mixer.Slots]; var mix = new float[960 * 2];
                Mixer.Mix(srcs, Mixer.MasterOf(ed), 1.5, 960, mix, Mixer.Bus(960), new short[960 * 2], peaks);
                Check(peaks[aLast * 2] > 0.01 && peaks[aFirst * 2] > 0.01 && peaks[Mixer.Slots - 2] > 0.01, $"meters: A20 (slot {aLast * 2}) {peaks[aLast * 2]:0.00}, A1 {peaks[aFirst * 2]:0.00}, master {peaks[Mixer.Slots - 2]:0.00}");
            }

            // ---------- 6. picture: 7 video tracks, preview vs export ----------
            Editor? big = null; Sequence? bigSeq = null;
            {
                var ed = big = new Editor(); ed.Media.AddRange(new[] { mBg, mPat }); ed.Media.AddRange(tones);
                ed.AddTracks(4, 0, 0, Tracks.Count);                                       // 7 + 2: rows V7 … V1 = 0 … 6
                bigSeq = ed.Seq; bigSeq.Name = "Seven";
                int V(int n) => Tracks.V(n);
                Clip P(int vn, double s, double d, double scale, double x, double y, double inPt = 0)
                {
                    var c = new Clip { MediaId = mPat.Id, Kind = ClipKind.Video, Track = V(vn), Start = s, Dur = d, In = inPt, NoAudio = true, Scale = scale, X = x, Y = y };
                    ed.Clips.Add(c); return c;
                }
                ed.Clips.Add(new Clip { MediaId = mBg.Id, Kind = ClipKind.Image, Track = V(1), Start = 0, Dur = 6 });
                var cases = new List<(string name, double t, double min)>();
                // a picture on every track, overlapping so the stacking order shows (V7 on top)
                for (int vn = 2; vn <= 7; vn++) P(vn, 0, 1, 34, -42 + (vn - 2) * 16, -30 + (vn - 2) * 11, 0.2 * vn);
                cases.Add(("a picture on each of V2 … V7 (V7 on top)", 0.5, 30));
                // an adjustment layer on V4: black and white + vignette on V1 … V3, not on V5 … V7
                for (int vn = 2; vn <= 7; vn++) if (vn != 4) P(vn, 1, 1, 30, -45 + (vn - 2) * 18, 30 - (vn - 2) * 10, 1 + 0.1 * vn);
                ed.Clips.Add(new Clip { Kind = ClipKind.Adjustment, Track = V(4), Start = 1, Dur = 1, Text = "Adjustment layer", Grade = new ColorGrade { Saturation = 0, Vignette = -45 } });
                cases.Add(("an adjustment layer on V4 (V1 … V3 grey, V5 … V7 in colour)", 1.5, 30));
                // a blend mode on V6 over five tracks
                for (int vn = 2; vn <= 5; vn++) P(vn, 2, 1, 40, -30 + (vn - 2) * 20, 0, 2);
                var bl = P(6, 2, 1, 70, 0, 0, 3); bl.Blend = BlendMode.Screen;
                cases.Add(("Screen on V6 over V1 … V5", 2.5, 29));
                // a track matte on V7: the text reveals V1 … V6
                for (int vn = 2; vn <= 6; vn++) P(vn, 3, 1, 45, -36 + (vn - 2) * 18, -20 + (vn - 2) * 10, 3);
                var tm = new Clip { Kind = ClipKind.Text, Track = V(7), Start = 3, Dur = 1, Role = TextRole.Title, Text = "TRACKS", Mask = MaskMode.Reveal,
                                    Style = Themes.Get(ed.ThemeName).For(TextRole.Title).Clone() };
                tm.Style!.Size = 230; tm.Style.Weight = 900; ed.Clips.Add(tm);
                cases.Add(("a text track matte on V7 masking V1 … V6", 3.5, 30));
                // a cross-dissolve on V5 with pictures above and below it
                P(5, 4, 1, 60, -15, 0, 1); var cb = P(5, 5, 1, 60, -15, 0, 4); cb.TransIn = new Transition { Kind = "Cross Dissolve", Dur = 0.6 };
                P(2, 4, 2, 35, 40, 30, 2); P(7, 4, 2, 30, -40, -30, 5);
                cases.Add(("a cross-dissolve on V5 between V2 and V7", 5.0, 30));
                ed.SetLocked(V(3), true);                                                  // (a lock changes nothing in the picture)
                var mp4 = Path.Combine(dir, "seven.mp4");
                var opts = new ExportSettings { Quality = 10, Audio = "none" };
                var titles = TitleVisual.PrepareExport(ed, Path.Combine(dir, "titles"), opts.OutW, opts.OutH);
                var err = await Exporter.RunAsync(ff, ed, mp4, new Progress<double>(), CancellationToken.None, titles, null, opts);
                Check(err == null, "export of the 7-track sequence" + (err == null ? "" : ": " + err));
                if (err == null)
                {
                    var pv = new Preview { Ed = ed, Width = Editor.W, Height = Editor.H, Background = Brushes.Black };
                    foreach (var (name, t1, min) in cases)
                    {
                        double t = Math.Round(t1 * 30) / 30;
                        var swp = Stopwatch.StartNew();
                        do { pv.Sync(t, false); await Task.Delay(20); } while ((!pv.Settled(t) || swp.ElapsedMilliseconds < 400) && swp.ElapsedMilliseconds < 8000);
                        pv.Sync(t, false);
                        pv.Measure(new Size(Editor.W, Editor.H)); pv.Arrange(new Rect(0, 0, Editor.W, Editor.H)); pv.UpdateLayout();
                        var rtb = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
                        var pa = new byte[Editor.W * Editor.H * 4]; rtb.CopyPixels(pa, Editor.W * 4, 0);
                        var pb = FxTest.Frame(ff, mp4, t - 0.001);
                        var (psnr, worst) = FxTest.Compare(pa, pb);
                        var (sx, sy) = SeqTest.Shift(pa, pb, Editor.W, Editor.H);
                        Check(psnr >= min && Math.Abs(sx) <= 1 && Math.Abs(sy) <= 1, $"7 tracks — {name}: preview vs export PSNR {psnr:0.0} dB (≥ {min}), worst block {worst:0.0}/255, shift ({sx},{sy})");
                        FxTest.Save(FxTest.SideBySide(pa, pb), Path.Combine(dir, $"tracks_{string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_')}.png"));
                    }
                    // the preview's canvas tree follows the layout: 7 tracks, then 3 after opening a 3 + 2 sequence
                    Check(TrackCanvases(pv) == 7, $"the preview builds one canvas per video track ({TrackCanvases(pv)})");
                    pv.DisposeAll();
                }
            }

            // ---------- 7. sound: 4 audio tracks ----------
            {
                var ed = new Editor(); ed.Media.AddRange(tones);
                ed.AddTracks(0, 0, 2, Tracks.Count);                                        // 3 + 4
                for (int an = 1; an <= 4; an++)
                    ed.Clips.Add(new Clip { MediaId = tones[an - 1].Id, Kind = ClipKind.Audio, Track = Tracks.A(an), Start = 0.25 * an, Dur = 4, In = 0.1 * an, FadeIn = 0.2 });
                ed.Mix.Channels[Tracks.A(1)].Gain = -2; ed.Mix.Channels[Tracks.A(2)].Pan = -60; ed.Mix.Channels[Tracks.A(4)].Gain = 3; ed.Mix.Channels[Tracks.A(4)].Pan = 40;
                ed.Mix.Channels[Tracks.A(4)].GainKeys = new() { new() { T = 0, V = -9 }, new() { T = 3, V = 0 } };
                ed.TrackOff[Tracks.A(3)] = true;                                             // A3 muted
                foreach (var m in tones) AudioCache.WaitReady(m.Path, 30000);
                var wav = Path.Combine(dir, "four.wav");
                var e = await Exporter.RunAsync(ff, ed, wav, new Progress<double>(), CancellationToken.None, null, null, new ExportSettings { Video = "none", Container = "wav", Audio = "pcm" });
                Check(e == null, "export of 4 audio tracks" + (e == null ? "" : ": " + e));
                if (e == null)
                {
                    var (snr, lag) = AudioTest.Compare(AudioTest.MixOf(ed, 0, ed.End), AudioTest.Pcm(ff, wav));
                    Check(snr >= 50 && lag == 0, $"4 audio tracks (faders, pans, A4 automated, A3 muted): in-app mix = export {snr:0.0} dB, shift {lag}");
                }
            }

            // ---------- 8. the 7-track sequence nested in a 3 + 2 one ----------
            if (big != null && bigSeq != null)
            {
                var ed = big;
                var outer = ed.NewSequence("Outer");                                       // (3 + 2, active)
                ed.Clips.Add(new Clip { MediaId = bigSeq.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = 0, Dur = 6, NoAudio = true });
                Nest.Background = true;
                Dictionary<string, Nest.Picture> pics;
                try { pics = await Task.Run(() => Nest.PrepareExport(ed, true, false, CancellationToken.None)); }
                catch (Exception ex) { Check(false, "render of the nested 7-track sequence: " + ex.Message); pics = new(); }
                var nc = ed.Clips[0];
                Check(pics.TryGetValue(nc.Id, out var pic) && File.Exists(pic.File) && Tracks.Video == 3, "the 7-track sequence renders nested in a 3 + 2 one (its layout on the render thread, 3 + 2 in the UI)");
                ed.Activate(bigSeq);
                var normal = Path.Combine(dir, "seven_normal.mkv");
                var titles = TitleVisual.PrepareExport(ed, Path.Combine(dir, "titles2"), 1280, 720);
                var err = await Task.Run(() => Exporter.RunAsync(ff, ed, normal, new Progress<double>(), CancellationToken.None, titles, null, new ExportSettings { Container = "mkv", Video = "ffv1", Audio = "none" }));
                Check(err == null, "normal lossless export of the 7-track sequence" + (err == null ? "" : ": " + err));
                if (pic != null && err == null)
                    foreach (var t in new[] { 0.5, 1.5, 2.5, 3.5, 5.0 })
                    {
                        double tf = Math.Round(t * 30) / 30;
                        var over = NestTest.OverBlack(ff, pic.File, (int)Math.Round((tf - pic.Offset) * 30));
                        var pb = FxTest.Frame(ff, normal, tf - 0.001);
                        var (psnr, worst) = FxTest.Compare(over, pb);
                        Check(psnr >= 40, $"nested 7-track render over black = its export at {t:0.0} s: PSNR {psnr:0.0} dB, worst block {worst:0.0}/255");
                    }
                ed.Activate(outer);
            }
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "tracktest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>How many track canvases the preview's tree has (one per video track).</summary>
    static int TrackCanvases(Preview pv) =>
        typeof(Preview).GetField("trackCv", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(pv) is System.Windows.Controls.Canvas[] a ? a.Length : -1;

    static void Run(string ff, string args) { using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!; p.StandardError.ReadToEnd(); p.WaitForExit(); }
}
