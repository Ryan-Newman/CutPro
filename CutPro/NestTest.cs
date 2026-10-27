using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>CutPro.exe --nesttest dir: nested sequences.
/// - a sequence rendered with a transparent background (what a nested clip shows), put over black = the same sequence exported normally
///   (scaled clip with empty borders, title fade, cross-dissolve, clock wipe, Screen blend, title track matte, dip to white), and empty where nothing is;
/// - Nest keeps the picture and the sound (export before = after, sample by sample within 2 LSB), undo / redo;
/// - preview vs export of nested clips: transparent titles over video, scaled + rotated with a drop shadow, graded + cropped + flipped,
///   Multiply blend, a dissolve between two nested clips, a sequence nested two levels deep, 1080p25 and 9:16 sequences in a 720p30 one;
/// - renders are reused (same content, a trim inside the rendered range) and made again after an edit inside;
/// - sequences: can't contain themselves, switching keeps each one's playhead / marks / mixer, duplicate (own masks), delete, project round trip.</summary>
public static class NestTest
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
            string Gen(string name, string lavfi)
            {
                var f = Path.Combine(dir, name);
                if (!File.Exists(f)) Run(ff, $"-y -v error {lavfi} \"{f}\"");
                return f;
            }
            var landSrc = @"C:\Windows\Web\Wallpaper\ThemeC\img29.jpg";
            var bgPic = Gen("bg.png", File.Exists(landSrc) ? $"-i \"{landSrc}\" -vf scale=1280:-2,crop=1280:720 -frames:v 1" : "-f lavfi -i testsrc2=s=1280x720 -frames:v 1");
            var fg = Gen("fg.mp4", "-f lavfi -i testsrc2=s=960x540:r=30:d=10 -c:v libx264 -crf 10 -pix_fmt yuv420p");
            var fgA = Gen("fga.mp4", "-f lavfi -i testsrc2=s=1280x720:r=30:d=10 -f lavfi -i sine=frequency=330:duration=10:sample_rate=48000 -f lavfi -i sine=frequency=550:duration=10:sample_rate=48000 " +
                                     "-filter_complex \"[1][2]amerge=inputs=2,volume=0.5[a]\" -map 0:v -map \"[a]\" -c:v libx264 -crf 10 -pix_fmt yuv420p -c:a pcm_s16le -shortest -f mov");
            var ed = new Editor();
            var mBg = await MediaProbe.ProbeAsync(bgPic) ?? throw new Exception("import bg");
            var mFg = await MediaProbe.ProbeAsync(fg) ?? throw new Exception("import fg");
            var mFa = await MediaProbe.ProbeAsync(fgA) ?? throw new Exception("import fga");
            ed.Media.AddRange(new[] { mBg, mFg, mFa });
            AudioCache.Ensure(mFa); AudioCache.WaitReady(mFa.Path, 30000);
            Nest.Background = true;

            Sequence NewSeq(string name, int w = 1280, int h = 720, double fps = 30)
            {
                var s = new Sequence { Name = name, SeqW = w, SeqH = h, SeqFps = fps };
                ed.Sequences.Add(s); ed.RefreshSeqItems();
                return s;
            }
            Clip V(Sequence s, MediaItem m, int track, double start, double dur, double inPt = 0)
            {
                var c = new Clip { MediaId = m.Id, Kind = m.Type, Track = track, Start = start, Dur = dur, In = m.Type == ClipKind.Video ? inPt : 0, NoAudio = true };
                s.Clips.Add(c); return c;
            }
            Clip T(Sequence s, string text, int track, double start, double dur, double size = 120)
            {
                var c = new Clip { Kind = ClipKind.Text, Track = track, Start = start, Dur = dur, Role = TextRole.Title, Text = text, Style = Themes.Get(ed.ThemeName).For(TextRole.Title).Clone() };
                c.Style!.Size = size; s.Clips.Add(c); return c;
            }
            Clip N(Sequence s, Sequence inner, int track, double start, double dur, double inPt = 0)
            {
                var c = new Clip { MediaId = inner.Id, Kind = ClipKind.Video, Track = track, Start = start, Dur = dur, In = inPt, NoAudio = true };
                s.Clips.Add(c); return c;
            }
            async Task<string?> Export(Sequence s, string file, ExportSettings o)
            {
                ed.Activate(s);
                var tmp = Path.Combine(dir, "tmp_" + Path.GetFileNameWithoutExtension(file));
                var titles = o.AudioOnly ? new Dictionary<string, string>() : TitleVisual.PrepareExport(ed, tmp, o.OutW, o.OutH);
                var masks = o.AudioOnly ? new Dictionary<string, string>() : MaskExport.Prepare(ed, tmp, null, o.Scale);
                return await Task.Run(() => Exporter.RunAsync(ff, ed, file, new Progress<double>(), CancellationToken.None, titles, masks, o));
            }
            ExportSettings Lossless() => new() { Container = "mkv", Video = "ffv1", Audio = "none" };
            ExportSettings Wav() => new() { Container = "wav", Video = "none", Audio = "pcm" };

            // ---------- 1. the transparent render of a sequence over black = its normal export ----------
            var S = NewSeq("Inner");
            var a1 = V(S, mFg, 2, 0, 2, 0); a1.Scale = 80;                                    // empty borders
            var b1 = V(S, mFg, 2, 2, 2, 3); b1.TransIn = new Transition { Kind = "Cross Dissolve", Dur = 1 };
            {   // an object mask inside the nested sequence (pixelate inside an ellipse): rendered on the render thread like the export does
                var om0 = new ObjectMask { Kind = MaskKind.Ellipse, Name = "Ellipse 1", Effect = MaskFx.Pixelate, Amount = 24 };
                var tr0 = new MaskTrack(); tr0.Set(0, new List<float[]> { Ellipse(0.5f, 0.5f, 0.25f, 0.3f) }, key: true);
                ed.MaskTracks[om0.Id] = tr0; b1.Masks.Add(om0);
            }
            var c1 = V(S, mBg, 2, 4, 2); c1.TransIn = new Transition { Kind = "Clock Wipe", Dur = 1 };
            var tt = T(S, "NESTED", 1, 0.5, 5); tt.FadeIn = 0.5;
            var d1 = V(S, mFg, 2, 6, 2, 5); d1.Scale = 90;
            var e1 = V(S, mFg, 1, 6, 1, 1); e1.Scale = 50; e1.Blend = BlendMode.Screen;
            var mt = T(S, "MATTE", 0, 7, 1, 200); mt.Mask = MaskMode.Reveal;
            d1.TransOut = new Transition { Kind = "Dip to White", Dur = 0.6, Align = TransAlign.End };
            var P = NewSeq("Outer");
            var nS = N(P, S, 2, 0, 8);
            ed.UseSequences(ed.Sequences.ToList(), P.Id);
            var sw = Stopwatch.StartNew();
            Dictionary<string, Nest.Picture> pics;
            try { pics = await Task.Run(() => Nest.PrepareExport(ed, true, false, CancellationToken.None)); }
            catch (Exception ex) { Check(false, "render of a nested sequence: " + ex.Message); pics = new(); }
            Check(pics.TryGetValue(nS.Id, out var sPic) && File.Exists(sPic.File), $"nested sequence rendered ({sw.Elapsed.TotalSeconds:0.0} s for 8 s at 1280×720) → {Path.GetFileName(sPic?.File ?? "none")}, offset {sPic?.Offset ?? -1:0.###}");
            var sNormal = Path.Combine(dir, "inner_normal.mkv");
            var err = await Export(S, sNormal, Lossless());
            Check(err == null, "normal lossless export of the inner sequence" + (err == null ? "" : ": " + err));
            if (sPic != null && err == null)
            {
                foreach (var (name, t) in new[] { ("scaled clip (empty borders)", 0.3), ("title fading in", 0.8), ("cross-dissolve", 2.0), ("object mask (pixelate)", 3.0), ("clock wipe", 4.0), ("photo + title", 5.2),
                                                  ("Screen blend", 6.5), ("title track matte", 7.3), ("dip to white", 7.8) })
                {
                    var tf = Math.Round(t * 30) / 30;
                    var pa = FxTest.Frame(ff, sPic.File, tf - sPic.Offset - 0.001);
                    var over = OverBlack(ff, sPic.File, (int)Math.Round((tf - sPic.Offset) * 30));   // (composited in the export's composite format: 4:4:4 by default)
                    var pb = FxTest.Frame(ff, sNormal, tf - 0.001);
                    var (psnr, worst) = FxTest.Compare(over, pb);
                    string extra = "";
                    bool ok = psnr >= 40;
                    if (name.StartsWith("scaled"))
                    {
                        int corner = pa[3], centre = pa[(360 * Editor.W + 640) * 4 + 3];
                        extra = $"; alpha corner {corner}, centre {centre}";
                        ok &= corner == 0 && centre == 255;
                    }
                    Check(ok, $"transparent render over black = normal export, {name}: PSNR {psnr:0.0} dB (≥ 40), worst block {worst:0.0}/255{extra}");
                    FxTest.Save(FxTest.SideBySide(over, pb), Path.Combine(dir, $"nest_alpha_{name.Split(' ')[0]}.png"));
                }
            }

            // ---------- 2. Nest keeps the picture and the sound; undo / redo ----------
            var Q = NewSeq("Nest me");
            var qa = new Clip { MediaId = mFa.Id, Kind = ClipKind.Video, Track = 2, Start = 0, Dur = 4, In = 1, LinkId = "L1", NoAudio = true };
            var qs = new Clip { MediaId = mFa.Id, Kind = ClipKind.Audio, Track = 3, Start = 0, Dur = 4, In = 1, LinkId = "L1", FadeIn = 0.3 };
            var qb = new Clip { MediaId = mFg.Id, Kind = ClipKind.Video, Track = 2, Start = 4, Dur = 3, In = 2, NoAudio = true, TransIn = new Transition { Kind = "Cross Dissolve", Dur = 0.8 } };
            Q.Clips.AddRange(new[] { qa, qs, qb });
            var qt = T(Q, "IN THE NEST", 1, 1, 4); qt.FadeOut = 0.5;
            var qkeep = V(Q, mBg, 0, 5, 2); qkeep.Scale = 40; qkeep.X = 25;                     // stays outside the nest (V3)
            ed.RefreshSeqItems();
            var beforeMp = Path.Combine(dir, "nest_before.mkv"); var beforeWav = Path.Combine(dir, "nest_before.wav");
            err = await Export(Q, beforeMp, Lossless()) ?? await Export(Q, beforeWav, Wav());
            Check(err == null, "export before nesting" + (err == null ? "" : ": " + err));
            int clipsBefore = Q.Clips.Count, seqsBefore = ed.Sequences.Count;
            ed.Activate(Q);
            ed.SetSelection(new[] { qa, qb, qt });                                            // (+ qs through the link)
            var (nested, why) = ed.NestSelection("Nested");
            Check(nested != null && Q.Clips.Count == 3 && nested.Clips.Count == 4 && ed.Sequences.Count == seqsBefore + 1,
                  $"Nest: {Q.Clips.Count} clips left (nested picture + sound + the one outside), {nested?.Clips.Count ?? 0} inside{(why != null ? ": " + why : "")}");
            if (nested != null)
            {
                var nv = Q.Clips.FirstOrDefault(c => c.MediaId == nested.Id && c.Kind == ClipKind.Video);
                var na = Q.Clips.FirstOrDefault(c => c.MediaId == nested.Id && c.Kind == ClipKind.Audio);
                Check(nv is { Track: 2, Start: 0, In: 0 } && Math.Abs(nv.Dur - 7) < 1e-6 && na is { Track: 3 } && nv.LinkId != null && nv.LinkId == na.LinkId && nv.NoAudio,
                      $"nested clip on V1 0–7 s with its sound linked on A1 (video {nv?.Track}/{nv?.Start}/{nv?.Dur:0.###}, sound {na?.Track})");
                var afterMp = Path.Combine(dir, "nest_after.mkv"); var afterWav = Path.Combine(dir, "nest_after.wav");
                sw.Restart();
                err = await Export(Q, afterMp, Lossless()) ?? await Export(Q, afterWav, Wav());
                Check(err == null, $"export after nesting ({sw.Elapsed.TotalSeconds:0.0} s, renders included)" + (err == null ? "" : ": " + err));
                if (err == null)
                {
                    foreach (var (t, min) in new[] { (0.5, 40.0), (2.0, 40), (4.0, 38), (4.4, 40), (5.5, 40), (6.8, 40) })   // (38 inside the cross-dissolve: a transparent render mixes it premultiplied, through 4:4:4)
                    {
                        var tf = Math.Round(t * 30) / 30;
                        var (psnr, worst) = FxTest.Compare(FxTest.Frame(ff, afterMp, tf - 0.001), FxTest.Frame(ff, beforeMp, tf - 0.001));
                        Check(psnr >= min, $"picture after Nest = before at {tf:0.00} s: PSNR {psnr:0.0} dB (≥ {min}), worst block {worst:0.0}/255");
                    }
                    var (maxd, rms, n) = WavDiff(beforeWav, afterWav);
                    Check(n > 48000 * 6 && maxd <= 2, $"sound after Nest = before: {n} samples, max difference {maxd} LSB (≤ 2), rms {rms:0.000}");
                }
                ed.Undo();
                Q = ed.SeqById(Q.Id)!;                                                       // (undo restores the sequences as copies)
                Check(Q.Clips.Count == clipsBefore && ed.Sequences.Count == seqsBefore && Q.Clips.Any(c => c.Id == qa.Id) && ed.Seq.Id == Q.Id,
                      $"undo Nest: {Q.Clips.Count} clips back (were {clipsBefore}), {ed.Sequences.Count} sequences (were {seqsBefore})");
                ed.Redo();
                Q = ed.SeqById(Q.Id)!;
                Check(Q.Clips.Count == 3 && ed.Sequences.Count == seqsBefore + 1, $"redo Nest: {Q.Clips.Count} clips, {ed.Sequences.Count} sequences");
            }

            // ---------- 3. preview vs export of nested clips ----------
            S = ed.SeqById(S.Id)!; P = ed.SeqById(P.Id)!;
            var L3 = NewSeq("Lower third");                                                   // titles only: transparent around them
            var lt = T(L3, "Pastor James", 1, 0, 3, 70); lt.Y = 30; lt.FadeIn = 0.4;
            var U = NewSeq("Two deep");
            var uS = N(U, S, 2, 0, 6, 1); uS.Scale = 50; uS.X = -20;                         // S inside U, U inside the outer sequence
            T(U, "DEEP", 1, 0, 6, 90).Y = -30;
            var HD = NewSeq("1080p 25", 1920, 1080, 25);
            V(HD, mFg, 2, 0, 3, 1); T(HD, "25 fps", 1, 0, 3, 100);
            var VT = NewSeq("Vertical", 1080, 1920, 30);
            var vfg = V(VT, mFg, 2, 0, 3, 2); vfg.Scale = 300;
            var R = NewSeq("Preview test");
            V(R, mBg, 2, 0, 18);                                                             // the photo under everything (V1)
            var cases = new List<(string name, double t, double min)>();
            N(R, L3, 1, 0, 3); cases.Add(("transparent titles over video", 1.5, 32));
            var r1 = N(R, S, 1, 3, 2, 0); r1.Scale = 60; r1.Rotation = 10; r1.Shadow = new DropShadow { Opacity = 80, Distance = 20, Softness = 12 };
            cases.Add(("scaled, rotated, drop shadow", 3.3, 27)); cases.Add(("scaled, rotated, drop shadow (title)", 4.5, 27));
            var r2 = N(R, S, 1, 5, 1, 2.5); r2.Grade = new ColorGrade { Contrast = 25, Saturation = 130 }; r2.CropL = 10; r2.CropB = 20; r2.FlipH = true;
            cases.Add(("graded, cropped, flipped", 5.5, 32));
            var r3 = N(R, S, 1, 6, 1, 4.2); r3.Blend = BlendMode.Multiply; r3.Scale = 80;
            cases.Add(("Multiply blend", 6.5, 30));
            N(R, S, 1, 7, 1, 1.5); var r5 = N(R, S, 1, 8, 1, 5.2); r5.TransIn = new Transition { Kind = "Cross Dissolve", Dur = 0.6 };
            cases.Add(("dissolve between two nested clips", 8.0, 32));
            N(R, U, 1, 9, 2, 0.5); cases.Add(("nested two levels deep", 9.6, 32));
            N(R, HD, 1, 11, 2, 0.4); cases.Add(("1080p 25 fps sequence in 720p 30", 11.5, 30)); cases.Add(("1080p 25 fps sequence in 720p 30, later", 12.37, 30));
            var r8 = N(R, VT, 1, 13, 1, 1); cases.Add(("9:16 sequence in 16:9", 13.5, 32));
            N(R, S, 1, 14, 1, 13 / 30.0); cases.Add(("starting between milliseconds (In 13/30 s)", 14.0, 32)); cases.Add(("starting between milliseconds, later", 14.6, 32));
            N(R, HD, 1, 15, 1, 0.9); cases.Add(("25 fps sequence, rate conversion on other frames", 15.3, 30)); cases.Add(("25 fps sequence, rate conversion, later", 15.77, 30));
            var r9 = N(R, S, 1, 16, 1, 2.2); r9.Pin = new CornerPin { TLX = 15, TLY = 10, TRX = 80, TRY = 18, BLX = 10, BLY = 85, BRX = 90, BRY = 75 };
            cases.Add(("corner pin on a nested clip", 16.5, 30));
            var r10 = N(R, L3, 1, 17, 1, 1.2); r10.Mask = MaskMode.Reveal; r10.MatteLuma = false;      // the titles' alpha as a track matte over the photo
            cases.Add(("nested titles as an alpha track matte", 17.5, 32));
            ed.RefreshSeqItems();
            var rMp = Path.Combine(dir, "nest_preview.mkv");
            sw.Restart();
            err = await Export(R, rMp, Lossless());
            Check(err == null, $"export with nested clips ({sw.Elapsed.TotalSeconds:0.0} s, renders included)" + (err == null ? "" : ": " + err));
            if (err == null)
            {
                var pv = new Preview { Ed = ed, Width = Editor.W, Height = Editor.H, Background = Brushes.Black };
                foreach (var (name, t1, min) in cases)
                {
                    double t = Math.Round(t1 * 30) / 30;
                    var swp = Stopwatch.StartNew();
                    do { pv.Sync(t, false); await Task.Delay(20); } while ((!pv.Settled(t) || swp.ElapsedMilliseconds < 400) && swp.ElapsedMilliseconds < 15000);
                    pv.Sync(t, false);
                    pv.Measure(new Size(Editor.W, Editor.H)); pv.Arrange(new Rect(0, 0, Editor.W, Editor.H)); pv.UpdateLayout();
                    var rtb = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
                    var pa = new byte[Editor.W * Editor.H * 4]; rtb.CopyPixels(pa, Editor.W * 4, 0);
                    var pb = FxTest.Frame(ff, rMp, t - 0.001);
                    var (psnr, worst) = FxTest.Compare(pa, pb);
                    var (sx, sy) = SeqTest.Shift(pa, pb, Editor.W, Editor.H);
                    Check(psnr >= min && Math.Abs(sx) <= 1 && Math.Abs(sy) <= 1, $"{name}: preview vs export PSNR {psnr:0.0} dB (≥ {min}), worst block {worst:0.0}/255, shift ({sx},{sy})");
                    FxTest.Save(FxTest.SideBySide(pa, pb), Path.Combine(dir, $"nest_{string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_').Replace(",", "")}.png"));
                }
                pv.DisposeAll();

                // ---------- 4. renders are reused; an edit inside renders again ----------
                int files = Directory.GetFiles(NestDir(), "*.mkv").Length;
                sw.Restart();
                var again = await Task.Run(() => Nest.PrepareExport(ed, true, true, CancellationToken.None));
                Check(Directory.GetFiles(NestDir(), "*.mkv").Length == files && sw.ElapsedMilliseconds < 3000, $"same content: no new render ({sw.ElapsedMilliseconds} ms, {again.Count} pictures)");
                r8.Dur -= 0.3;                                                              // a trim inside the rendered range
                var trimmed = await Task.Run(() => Nest.PrepareExport(ed, true, false, CancellationToken.None));
                Check(Directory.GetFiles(NestDir(), "*.mkv").Length == files && trimmed.ContainsKey(r8.Id), "trim inside the rendered range: no new render");
                r8.Dur += 0.3;
                ed.Activate(L3);
                ed.Edit(() => L3.Clips[0].Text = "Pastor Jim");
                ed.Activate(R);
                var edited = await Task.Run(() => Nest.PrepareExport(ed, true, false, CancellationToken.None));
                var l3clip = R.Clips.First(c => c.MediaId == L3.Id);
                Check(edited.TryGetValue(l3clip.Id, out var ep) && again.TryGetValue(l3clip.Id, out var op) && ep.File != op.File && File.Exists(ep.File),
                      "edit inside a nested sequence: rendered again (new file)");
            }

            // ---------- 4b. a 29.97 fps project: nested 29.97 (starting between milliseconds) and nested 30 fps ----------
            {
                var S29 = NewSeq("NTSC inner", 1280, 720, 29.97);
                var s29a = V(S29, mFg, 2, 0, 4, 0.5); s29a.Scale = 85; T(S29, "29.97", 1, 0, 4, 110);
                var R29 = NewSeq("NTSC outer", 1280, 720, 29.97);
                double fr = 1001 / 30000.0;
                V(R29, mBg, 2, 0, 190 * fr);
                N(R29, S29, 1, 0, 3, 7 * fr);                                                 // In = 7 frames (0.2336 s)
                N(R29, S, 1, 90 * fr, 3, 1.5);                                               // a 30 fps sequence in 29.97 (at frame 90)
                ed.RefreshSeqItems();
                var mp29 = Path.Combine(dir, "nest_2997.mkv");
                err = await Export(R29, mp29, Lossless());
                Check(err == null, "29.97 fps project with nested sequences exported" + (err == null ? "" : ": " + err));
                if (err == null)
                {
                    var pv = new Preview { Ed = ed, Width = Editor.W, Height = Editor.H, Background = Brushes.Black };
                    foreach (var (name, k) in new[] { ("29.97 in 29.97, In between milliseconds", 1), ("29.97 in 29.97, later", 47), ("30 fps in 29.97", 95), ("30 fps in 29.97, later", 160) })
                    {
                        double t = k * fr;
                        var swp = Stopwatch.StartNew();
                        do { pv.Sync(t, false); await Task.Delay(20); } while ((!pv.Settled(t) || swp.ElapsedMilliseconds < 400) && swp.ElapsedMilliseconds < 15000);
                        pv.Sync(t, false);
                        pv.Measure(new Size(Editor.W, Editor.H)); pv.Arrange(new Rect(0, 0, Editor.W, Editor.H)); pv.UpdateLayout();
                        var rtb = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
                        var pa = new byte[Editor.W * Editor.H * 4]; rtb.CopyPixels(pa, Editor.W * 4, 0);
                        var pb = FxTest.Frame(ff, mp29, t - 0.0005);
                        var (psnr, worst) = FxTest.Compare(pa, pb);
                        var (sx, sy) = SeqTest.Shift(pa, pb, Editor.W, Editor.H);
                        Check(psnr >= 32 && Math.Abs(sx) <= 1 && Math.Abs(sy) <= 1, $"{name} (frame {k}): preview vs export PSNR {psnr:0.0} dB (≥ 32), worst block {worst:0.0}/255, shift ({sx},{sy})");
                        FxTest.Save(FxTest.SideBySide(pa, pb), Path.Combine(dir, $"nest_ntsc_{k}.png"));
                    }
                    pv.DisposeAll();
                }
            }

            // ---------- 5. sequences: rules, switching, duplicate, delete, project round trip ----------
            ed.Activate(P);
            Check(!ed.CanAdd(ed.SeqItem(P.Id)!), "a sequence can't be added to itself");
            ed.Activate(S);
            Check(!ed.CanAdd(ed.SeqItem(P.Id)!) && !ed.CanAdd(ed.SeqItem(R.Id)!) && !ed.CanAdd(ed.SeqItem(U.Id)!) && ed.CanAdd(ed.SeqItem(L3.Id)!),
                  "a sequence can't be added inside one that contains it (directly or two levels deep); an unrelated one can");
            int sClips = S.Clips.Count;
            ed.AddMedia(ed.SeqItem(U.Id)!, 1, 0);
            Check(S.Clips.Count == sClips, "adding a sequence that contains this one is refused");
            ed.Activate(P); ed.Seek(3.4); ed.MarkIn = 1; ed.Mix.Channels[3].Gain = -6;
            ed.Activate(Q); ed.Seek(1.2);
            Check(Math.Abs(ed.Time - 1.2) < 1e-9 && ed.MarkIn == null && ed.Mix.Channels[3].Gain == 0, "another sequence has its own playhead, marks and mixer");
            ed.Activate(P);
            Check(Math.Abs(ed.Time - 3.4) < 1e-9 && ed.MarkIn == 1 && ed.Mix.Channels[3].Gain == -6, "switching back restores them");
            var mtrack = new MaskTrack(); mtrack.Set(0, new List<float[]> { new float[] { 0.1f, 0.1f, 0.5f, 0.1f, 0.5f, 0.5f } }, key: true);
            var om = new ObjectMask { Kind = MaskKind.Ellipse, Name = "Ellipse 1" }; ed.MaskTracks[om.Id] = mtrack;
            ed.Activate(S);
            ed.Edit(() => S.Clips[0].Masks.Add(om));
            var dup = ed.DuplicateSequence(S);
            var dm = dup.Clips[0].Masks.FirstOrDefault();
            Check(ed.Seq == dup && dup.Clips.Count == S.Clips.Count && dup.Clips.All(c => !S.Clips.Any(o => o.Id == c.Id)) && dm != null && dm.Id != om.Id && ed.MaskTracks.ContainsKey(dm.Id),
                  $"duplicate: {dup.Name}, opened, {dup.Clips.Count} clips with new ids, its mask copied");
            int uses = ed.UsesOf(S);
            ed.DeleteSequence(S);
            Check(!ed.Sequences.Contains(S) && ed.Sequences.All(o => o.Clips.All(c => c.MediaId != S.Id)), $"delete a sequence: gone, with its {uses} nested clips");
            ed.Undo();
            Check(ed.SeqById(S.Id) is { } back && ed.UsesOf(back) == uses, "undo delete: the sequence and its nested clips are back");
            ed.CaptureFormat();
            var pf = new ProjectFile { Media = ed.Media, Sequences = ed.Sequences, Active = ed.Seq.Id, MaskTracks = ed.MaskTracks };
            var json = JsonSerializer.Serialize(pf);
            var pf2 = JsonSerializer.Deserialize<ProjectFile>(json)!;
            var seqs2 = pf2.AllSequences();
            Check(seqs2.Count == ed.Sequences.Count && seqs2.Select(s => s.Name).SequenceEqual(ed.Sequences.Select(s => s.Name)) && pf2.Active == ed.Seq.Id
                  && seqs2.First(s => s.Id == HD.Id) is { SeqW: 1920, SeqH: 1080 } hd2 && Math.Abs(hd2.SeqFps - 25) < 1e-9
                  && seqs2.Sum(s => s.Clips.Count) == ed.Sequences.Sum(s => s.Clips.Count), $"project round trip: {seqs2.Count} sequences, formats and clips kept");
            var v1 = JsonSerializer.Deserialize<ProjectFile>("{\"Version\":1,\"Clips\":[{\"Kind\":2,\"Track\":1,\"Start\":0,\"Dur\":3}],\"SeqW\":1080,\"SeqH\":1920,\"SeqFps\":25}")!;
            var old = v1.AllSequences();
            Check(old.Count == 1 && old[0].Clips.Count == 1 && old[0].SeqW == 1080 && old[0].SeqH == 1920 && old[0].Mix != null, "a version 1 project opens as one sequence");
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "nesttest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static string NestDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro", "Nest");

    /// <summary>An ellipse outline (normalised to the media frame) as a mask polygon.</summary>
    static float[] Ellipse(float cx, float cy, float rx, float ry)
    {
        var p = new float[64 * 2];
        for (int i = 0; i < 64; i++) { double a = i * Math.PI * 2 / 64; p[i * 2] = cx + rx * (float)Math.Cos(a); p[i * 2 + 1] = cy + ry * (float)Math.Sin(a); }
        return p;
    }

    /// <summary>Frame k of a transparent render put over black by ffmpeg's 4:2:0 overlay (how the export composites it), as BGRA.</summary>
    internal static byte[] OverBlack(string ff, string file, int k)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-v", "error", "-i", file, "-filter_complex", $"[0:v]select='eq(n,{k})',setpts=PTS-STARTPTS[s];color=black:s={Editor.W}x{Editor.H}:r=30[b];[b][s]overlay=format={(ExportSettings.Default.Full444 ? "yuv444" : "yuv420")}:shortest=1,format=bgra",
                                  "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" })
            psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        var b = ms.ToArray();
        if (b.Length < Editor.W * Editor.H * 4) throw new Exception($"over-black frame {k} failed");
        return b;
    }

    /// <summary>Largest and rms difference between two 16-bit WAV files' samples (header skipped), and how many were compared.</summary>
    static (int max, double rms, long n) WavDiff(string a, string b)
    {
        static short[] Pcm(string f)
        {
            var bytes = File.ReadAllBytes(f);
            int i = 12;
            while (i + 8 <= bytes.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(bytes, i, 4); int len = BitConverter.ToInt32(bytes, i + 4);
                if (id == "data") { var s = new short[Math.Min(len, bytes.Length - i - 8) / 2]; Buffer.BlockCopy(bytes, i + 8, s, 0, s.Length * 2); return s; }
                i += 8 + len + (len & 1);
            }
            return Array.Empty<short>();
        }
        var x = Pcm(a); var y = Pcm(b);
        long n = Math.Min(x.Length, y.Length); int max = 0; double se = 0;
        for (long k = 0; k < n; k++) { int d = Math.Abs(x[k] - y[k]); if (d > max) max = d; se += (double)d * d; }
        return (max, n > 0 ? Math.Sqrt(se / n) : 0, n);
    }

    static void Run(string ff, string args) { using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!; p.StandardError.ReadToEnd(); p.WaitForExit(); }
}
