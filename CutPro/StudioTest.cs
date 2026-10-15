using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>CutPro.exe --studiotest dir: Intro &amp; Hook and Thumbnail Studio.
/// - hooks: line scoring, transcript → moments, loudest moments of the mix;
/// - every intro template inserted on a real timeline: the video moves later by exactly the intro, the intro's pieces fill [0, intro) without
///   overlaps, titles / transitions / links are right, one undo restores the timeline; the intro exports (length, slow motion) and its title
///   card looks the same in the preview and the export;
/// - thumbnails: frame grab (+ the clip's colour), frame scores, every text style and shape drawn, the subject outline (grown alpha), both
///   sizes, JPG under 2 MB even for noise, PNG (<c>thumb_*.png</c>).</summary>
public static class StudioTest
{
    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
        try
        {
            // a 10 s test video: moving pattern, a tone that is loud only from 6 to 7.5 s
            var vid = Path.Combine(dir, "talk.mp4");
            if (!File.Exists(vid))
                Run(ff, $"-y -v error -f lavfi -i testsrc2=s=640x360:r=30:d=10 -f lavfi -i \"sine=f=330:r=48000:d=10\" " +
                        $"-af \"volume='if(between(t,6,7.5),0.9,0.06)':eval=frame\" -c:v libx264 -crf 16 -pix_fmt yuv420p -c:a aac -b:a 160k -shortest \"{vid}\"");

            // ---------- 1. hook moments ----------
            double q = Hooks.ScoreLine("Have you ever wondered why God answers prayer the way He does?", 20);
            double plain = Hooks.ScoreLine("The meeting starts after the second song.", 20);
            double tiny = Hooks.ScoreLine("Okay.", 20);
            double verse = Hooks.ScoreLine("For God so loved the world, John 3:16 says it all.", 20);
            double verseNoRef = Hooks.ScoreLine("For God so loved the world, it says it all.", 20);
            double early = Hooks.ScoreLine("Have you ever wondered why God answers prayer the way He does?", 1);
            Check(q > plain + 2 && plain > tiny && verse > verseNoRef && early < q,
                  $"line scores: question {q:0.0} > plain {plain:0.0} > one word {tiny:0.0}; with Scripture {verse:0.0} > without {verseNoRef:0.0}; at the very start {early:0.0} < later");
            var info = new ContentInfo
            {
                Transcript =
                {
                    new(0, 1.5, " Welcome back everyone."),
                    new(4, 6, " Have you ever wondered why"),
                    new(6, 7.5, " God answers prayer the way He does?"),
                    new(9, 9.6, " Okay."),
                    new(11, 21, " So today we are going to read a long passage together and talk about it for a while because it matters."),
                }
            };
            var tm = Hooks.FromTranscript(info);
            Check(tm.Count == 3 && tm[0].Text.StartsWith("Have you ever wondered why God") && tm[0].Start == 4 && tm[0].End == 7.5 && tm[0].Why == "a question"
                  && tm.All(m => m.End - m.Start is >= 2 - 1e-9 and <= 8 + 1e-9) && tm.Any(m => m.Start == 11 && m.End == 19),
                  $"transcript → {tm.Count} moments (pieces joined into sentences, \"Okay.\" dropped, 2–8 s): best \"{tm.FirstOrDefault()?.Text}\" {tm.FirstOrDefault()?.Start}–{tm.FirstOrDefault()?.End} s");

            var ed = new Editor();
            var mv = await MediaProbe.ProbeAsync(vid) ?? throw new Exception("import talk.mp4");
            ed.Media.Add(mv);
            ed.AddMedia(mv, 2, 0);
            AudioCache.Ensure(mv);
            var srcs = Mixer.Sources(ed, Transitions.Compute(ed), new()).ToArray();
            var loud = await Task.Run(() => Hooks.FromLoudness(srcs, Mixer.MasterGain(ed), ed.End));
            Check(loud.Count > 0 && loud[0].Start <= 6 + 1e-9 && loud[0].Start + 3 >= 7.5 - 1e-9,
                  $"loudest moment of the mix: {(loud.Count > 0 ? $"{loud[0].Start:0.00}–{loud[0].End:0.00} s" : "none")} (the loud part is 6–7.5 s); {loud.Count} moments");

            // ---------- 2. every intro template ----------
            ed.Clips.Add(new Clip { Kind = ClipKind.Text, Track = 1, Start = 2, Dur = 2, Role = TextRole.Title, Text = "Main title", Style = Themes.Get(ed.ThemeName).For(TextRole.Title).Clone() });
            var moments = new List<HookMoment> { new(6, 8, "Have you ever wondered why God answers prayer?", 3, "a question"), new(2, 3.5, "We all wait.", 1, ""), new(8.5, 9.8, "", 0.5, "") };
            var origIds = ed.Clips.ToDictionary(c => c.Id, c => c.Start);
            double fps = TimeUtil.Fps;
            foreach (var (tpl, slow, expect) in new[] { ("Quote hook", false, 2.0), ("Quote hook", true, 2.5), ("Question hook", false, 3.0), ("Teaser", false, 3.9),
                                                        ("Title card", false, 3.0), ("Hook + title card", false, 5.0) })
            {
                string before = JsonSerializer.Serialize(ed.Clips);
                string trans = tpl == "Teaser" ? "Dip to White" : "Cross Dissolve";
                double total = HookBuilder.Insert(ed, new HookBuilder.Plan(tpl, moments, tpl == "Question hook" ? "" : "Why wait?", "A Sunday message", slow, trans, 5));
                var added = ed.Clips.Where(c => !origIds.ContainsKey(c.Id)).ToList();
                bool shifted = ed.Clips.Where(c => origIds.ContainsKey(c.Id)).All(c => Math.Abs(c.Start - (origIds[c.Id] + total)) < 1e-9);
                bool inside = added.All(c => c.Start >= -1e-9 && c.End <= total + 1e-6);
                bool onFrames = added.All(c => Math.Abs(c.Start * fps - Math.Round(c.Start * fps)) < 1e-6 && Math.Abs(c.Dur * fps - Math.Round(c.Dur * fps)) < 1e-6);
                bool overlaps = ed.Clips.GroupBy(c => c.Track).Any(g => g.OrderBy(c => c.Start).Zip(g.OrderBy(c => c.Start).Skip(1)).Any(p => p.Second.Start < p.First.End - 1e-6));
                // the picture is covered from 0 to the intro's end (no black gap), with words on the first part
                bool covered = Enumerable.Range(0, (int)Math.Round(total * fps)).All(k => added.Any(c => Tracks.IsVideo(c.Track) && c.Kind != ClipKind.Text && c.Start <= (k + 0.5) / fps && c.End > (k + 0.5) / fps));
                bool words = added.Any(c => c.Kind == ClipKind.Text && c.Start < 1e-9) && !added.Any(c => c.Kind == ClipKind.Text && c.Text == "Main title");
                var into = ed.Clips.FirstOrDefault(c => origIds.ContainsKey(c.Id) && c.Kind == ClipKind.Video);
                bool transOk = into?.TransIn?.Kind == trans;
                // linked copies: video + its sound share a fresh link
                var vCopies = added.Where(c => c.Kind == ClipKind.Video && c.LinkId != null).ToList();
                bool links = vCopies.All(v => added.Any(a => a.Kind == ClipKind.Audio && a.LinkId == v.LinkId) && !ed.Clips.Any(o => origIds.ContainsKey(o.Id) && o.LinkId == v.LinkId));
                string detail = "";
                if (tpl == "Quote hook")
                {
                    var v = added.First(c => c.Kind == ClipKind.Video);
                    var a = added.First(c => c.Kind == ClipKind.Audio);
                    bool src = Math.Abs(v.In - 6) < 1e-6 && Math.Abs(a.In - 6) < 1e-6 && (slow ? Math.Abs(v.Speed - 80) < 1e-9 && Math.Abs(a.Speed - 80) < 1e-9 : v.Speed == 100);
                    Check(src, $"{tpl}{(slow ? " (slow)" : "")}: the hook shows source 6 s on, picture and sound at {v.Speed:0} %");
                    detail = $", caption \"{added.FirstOrDefault(c => c.Kind == ClipKind.Text)?.Text}\"";
                }
                if (tpl is "Question hook" or "Title card")
                {
                    var st = added.FirstOrDefault(c => c.Kind == ClipKind.Video);
                    Check(st != null && st.Hold && st.Track == 2 && Math.Abs(st.In - (6.5)) < 1e-6 && st.NoAudio && (tpl == "Title card" ? st.Blur > 0 : st.Blur == 0) && st.Grade != null,
                          $"{tpl}: a held frame of the best moment (source {st?.In:0.00} s, blur {st?.Blur}, graded) under the words");
                }
                if (tpl == "Teaser")
                {
                    int cards = added.Count(c => c.Kind == ClipKind.Video);
                    var firsts = added.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).ToList();
                    Check(cards == 3 && firsts.Skip(1).All(c => c.TransIn?.Kind == "Dip to White") && added.Count(c => c.Kind == ClipKind.Text) == 1,
                          $"Teaser: 3 highlights ({string.Join(", ", firsts.Select(c => $"{c.In:0.0} s"))}), flashes between, one caption");
                }
                Check(Math.Abs(total - expect) < 1e-6 && shifted && inside && onFrames && !overlaps && covered && words && transOk && links,
                      $"{tpl}{(slow ? " (slow)" : "")}: {total:0.00} s intro (expected {expect}), video moved later {(shifted ? "exactly" : "WRONG")}, " +
                      $"{added.Count} new clips inside [0, intro) {(inside && onFrames ? "on frames" : "NO")}, overlaps {(overlaps ? "YES" : "none")}, picture covered {covered}, words {words}, " +
                      $"into the video: {into?.TransIn?.Kind ?? "none"}, links {links}{detail}");
                if (tpl == "Hook + title card" && !slow)
                {
                    // export it: length, and the title card (held, blurred, graded frame + titles) preview vs export
                    var o = new ExportSettings { Quality = 10 };
                    var titles = TitleVisual.PrepareExport(ed, Path.Combine(dir, "titles"), o.OutW, o.OutH);
                    var mp4 = Path.Combine(dir, "hook.mp4");
                    var sw = Stopwatch.StartNew();
                    var err = await Exporter.RunAsync(ff, ed, mp4, new Progress<double>(), CancellationToken.None, titles, null, o);
                    Check(err == null, $"intro exports ({sw.Elapsed.TotalSeconds:0.0} s)" + (err == null ? "" : ": " + err));
                    if (err == null)
                    {
                        var pm = await MediaProbe.ProbeAsync(mp4);
                        Check(pm != null && Math.Abs(pm.Duration - ed.End) < 0.1, $"exported length {pm?.Duration:0.00} s = timeline {ed.End:0.00} s");
                        var pv = new Preview { Ed = ed, Width = Editor.W, Height = Editor.H, Background = Brushes.Black };
                        double t = Math.Round(3.5 * fps) / fps;                         // inside the title card (2–5 s), past its fade in
                        var swp = Stopwatch.StartNew();
                        do { pv.Sync(t, false); await Task.Delay(20); } while ((!pv.Settled(t) || swp.ElapsedMilliseconds < 600) && swp.ElapsedMilliseconds < 8000);
                        pv.Sync(t, false);
                        pv.Measure(new Size(Editor.W, Editor.H)); pv.Arrange(new Rect(0, 0, Editor.W, Editor.H)); pv.UpdateLayout();
                        var rtb = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
                        var pa = new byte[Editor.W * Editor.H * 4]; rtb.CopyPixels(pa, Editor.W * 4, 0);
                        var pb = FxTest.Frame(ff, mp4, t - 0.001);
                        var (psnr, worst) = FxTest.Compare(pa, pb);
                        Check(psnr >= 30, $"title card preview vs export PSNR {psnr:0.0} dB (≥ 30), worst block {worst:0.0}/255");
                        FxTest.Save(FxTest.SideBySide(pa, pb), Path.Combine(dir, "hook_titlecard.png"));
                    }
                }
                ed.Undo();
                Check(JsonSerializer.Serialize(ed.Clips) == before, $"{tpl}{(slow ? " (slow)" : "")}: one undo takes the intro out again");
            }
            Check(HookBuilder.Insert(new Editor(), new HookBuilder.Plan("Quote hook", new(), "", "", false, "Cross Dissolve", 5)) == 0, "nothing is inserted on an empty timeline");

            // ---------- 3. thumbnails ----------
            var g0 = ThumbFrames.Grab(ed, 3, 1920);
            Check(g0 is { w: 640, h: 360 }, $"frame grab at the media's size: {g0?.w}×{g0?.h}");
            var vclip = ed.Clips.First(c => c.Kind == ClipKind.Video);
            vclip.Grade = new ColorGrade { Exposure = 1, Contrast = 20, Saturation = 130 };
            var g1 = ThumbFrames.Grab(ed, 3, 1920);
            vclip.Grade = null;
            if (g0 is { } a0 && g1 is { } a1)
            {
                // (the test pattern is mostly pure colours, which a grade barely moves: compare with the LUT applied to the plain grab)
                var want = (byte[])a0.px.Clone(); Grade.Apply(Grade.For(new ColorGrade { Exposure = 1, Contrast = 20, Saturation = 130 })!, want, a0.w, a0.h, a0.w * 4);
                int diff = 0, changed = 0; for (int i = 0; i < want.Length; i++) { diff = Math.Max(diff, Math.Abs(want[i] - a1.px[i])); if (a0.px[i] != a1.px[i]) changed++; }
                Check(diff == 0 && changed > want.Length / 20, $"the grab carries the clip's colour grade (= its LUT exactly, {changed * 100.0 / want.Length:0}% of values changed)");
                var black = new byte[a0.px.Length]; for (int i = 3; i < black.Length; i += 4) black[i] = 255;
                var soft = Box((byte[])a0.px.Clone(), a0.w, a0.h, 6);
                double s0 = ThumbFrames.Score(a0.px, a0.w, a0.h), sb = ThumbFrames.Score(black, a0.w, a0.h), ss = ThumbFrames.Score(soft, a0.w, a0.h);
                Check(s0 > ss && ss > sb, $"frame scores: sharp {s0:0.00} > blurred {ss:0.00} > black {sb:0.00}");
            }

            // the outline: alpha grown by r pixels
            int W = 200, H = 200; var disc = new float[W * H];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) disc[y * W + x] = Math.Sqrt((x - 100) * (x - 100) + (y - 100) * (y - 100)) <= 50 ? 1 : 0;
            var sil = ThumbRender.Silhouette(disc, W, H, 10);
            var sp = new byte[W * H * 4]; sil.CopyPixels(sp, W * 4, 0);
            byte A(int x, int y) => sp[(y * W + x) * 4 + 3];
            // round: 8 px out along the axis and the diagonal is inside, 12 px out is not (a square filter reached (144,144))
            bool round = A(100, 100) == 255 && A(158, 100) == 255 && A(100, 41) == 255 && A(162, 100) == 0 && A(100, 37) == 0 && A(140, 140) == 255 && A(144, 144) == 0;
            var bigA = new float[1920 * 1080]; for (int y = 300; y < 900; y++) for (int x = 700; x < 1200; x++) bigA[y * 1920 + x] = 1;
            ThumbRender.Silhouette(bigA, 1920, 1080, 30);
            var swSil = Stopwatch.StartNew(); ThumbRender.Silhouette(bigA, 1920, 1080, 30); double silMs = swSil.Elapsed.TotalMilliseconds;
            Check(round && silMs < 400, $"subject outline = the cut-out grown by its width, round (axis and diagonal alike); 1920×1080 at 30 px in {silMs:0} ms");

            // every style and shape, a cut-out subject with outline + shadow, 16:9 and 9:16
            var bg = ThumbFrames.ToBitmap(g0!.Value.px, g0.Value.w, g0.Value.h);
            var alpha = new float[g0.Value.w * g0.Value.h];
            for (int y = 0; y < g0.Value.h; y++) for (int x = 0; x < g0.Value.w; x++) alpha[y * g0.Value.w + x] = Math.Pow((x - 420) / 110.0, 2) + Math.Pow((y - 180) / 140.0, 2) <= 1 ? 1 : 0;
            var subPx = (byte[])g0.Value.px.Clone();
            for (int i = 0; i < alpha.Length; i++) for (int k = 0; k < 4; k++) subPx[i * 4 + k] = (byte)(subPx[i * 4 + k] * alpha[i]);
            var subj = new WriteableBitmap(g0.Value.w, g0.Value.h, 96, 96, PixelFormats.Pbgra32, null);
            subj.WritePixels(new Int32Rect(0, 0, g0.Value.w, g0.Value.h), subPx, g0.Value.w * 4, 0); subj.Freeze();
            var silh = ThumbRender.Silhouette(alpha, g0.Value.w, g0.Value.h, 8);
            foreach (bool vertical in new[] { false, true })
            {
                var d = new ThumbDesign { Vertical = vertical, Subject = true, Outline = 16, Shadow = 60, SubjectX = vertical ? 0 : 0.1 };
                for (int i = 0; i < ThumbStyles.All.Length; i++)
                    d.Texts.Add(new ThumbText { Text = ThumbStyles.All[i].Name, Style = ThumbStyles.All[i].Name, Size = vertical ? 80 : 56, X = 0.27, Y = 0.08 + i * 0.112, Rotation = i == 0 ? -4 : 0 });
                d.Shapes.Add(new ThumbShape { Kind = "Arrow", X = 0.62, Y = 0.8, Size = 200, Rotation = -20 });
                d.Shapes.Add(new ThumbShape { Kind = "Circle", X = 0.85, Y = 0.8, Size = 160 });
                var bmp = ThumbRender.Render(d, bg, subj, silh);
                var px = new byte[d.W * d.H * 4]; bmp.CopyPixels(px, d.W * 4, 0);
                // each text's fill colour shows inside its letters
                int styleOk = 0; var miss = new List<string>();
                foreach (var t in d.Texts)
                {
                    var st = ThumbStyles.Get(t.Style);
                    var (geo, box) = ThumbRender.TextGeometry(t, d);
                    var fill = TitleVisual.ParseColor(st.Fill, Colors.White);
                    var fill2 = st.Fill2 != null ? TitleVisual.ParseColor(st.Fill2, Colors.White) : fill;   // (a gradient: anywhere between its two colours)
                    bool In(byte v, byte c1, byte c2) => v > Math.Min(c1, c2) - 40 && v < Math.Max(c1, c2) + 40;
                    int hits = 0, n = 0;
                    for (int y = (int)Math.Max(0, box.Top); y < Math.Min(d.H, box.Bottom); y += 2)
                        for (int x = (int)Math.Max(0, box.Left); x < Math.Min(d.W, box.Right); x += 2)
                        {
                            if (!geo.FillContains(new Point(x + 0.5, y + 0.5))) continue;
                            n++; int o = (y * d.W + x) * 4;
                            if (In(px[o + 2], fill.R, fill2.R) && In(px[o + 1], fill.G, fill2.G) && In(px[o], fill.B, fill2.B)) hits++;
                        }
                    if (n > 50 && hits > n * 0.6) styleOk++; else miss.Add($"{t.Style} {hits}/{n}");
                }
                // the outline colour shows just outside the subject's edge (left edge of the ellipse), the subject inside
                var bgr = ThumbRender.BgRect(d, g0.Value.w, g0.Value.h); var sr = ThumbRender.SubjectRect(d, bgr);
                double kx = sr.Width / g0.Value.w, ky = sr.Height / g0.Value.h;
                int ox = (int)(sr.X + (420 - 110 - 4) * kx), oy = (int)(sr.Y + 180 * ky);
                int oi = (oy * d.W + ox) * 4;
                bool outline = px[oi] > 235 && px[oi + 1] > 235 && px[oi + 2] > 235;
                Check(bmp.PixelWidth == d.W && bmp.PixelHeight == d.H && styleOk == d.Texts.Count && outline,
                      $"thumbnail {d.W}×{d.H}: {styleOk}/{d.Texts.Count} text styles drawn in their colours{(miss.Count > 0 ? " (missing: " + string.Join(", ", miss) + ")" : "")}, subject outline {(outline ? "white" : $"({px[oi + 2]},{px[oi + 1]},{px[oi]})")}");
                var jpg = Path.Combine(dir, vertical ? "thumb_cover.jpg" : "thumb_youtube.jpg");
                long size = ThumbRender.Save(bmp, jpg, true);
                var back = BitmapFrame.Create(new Uri(jpg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check(size < 2_000_000 && back.PixelWidth == d.W && back.PixelHeight == d.H, $"saved {Path.GetFileName(jpg)}: {size / 1024} KB, {back.PixelWidth}×{back.PixelHeight}");
                ThumbRender.Save(bmp, Path.ChangeExtension(jpg, ".png"), false);
            }
            // worst case for the 2 MB limit: full-size noise
            var rnd = new Random(7); var noise = new byte[1280 * 720 * 4]; rnd.NextBytes(noise); for (int i = 3; i < noise.Length; i += 4) noise[i] = 255;
            var nd = new ThumbDesign { Pop = 0 };
            var nb = ThumbRender.Render(nd, ThumbFrames.ToBitmap(noise, 1280, 720), null, null);
            long ns = ThumbRender.Save(nb, Path.Combine(dir, "thumb_noise.jpg"), true);
            Check(ns < 2_000_000, $"a picture of pure noise still saves under 2 MB ({ns / 1024} KB)");
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "studiotest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>A box blur (radius r) of BGRA pixels, for a soft test frame.</summary>
    static byte[] Box(byte[] px, int w, int h, int r)
    {
        var o = new byte[px.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                {
                    int s = 0, n = 0;
                    for (int dy = -r; dy <= r; dy += 2) for (int dx = -r; dx <= r; dx += 2)
                    {
                        int xx = Math.Clamp(x + dx, 0, w - 1), yy = Math.Clamp(y + dy, 0, h - 1);
                        s += px[(yy * w + xx) * 4 + c]; n++;
                    }
                    o[(y * w + x) * 4 + c] = (byte)(s / n);
                }
        return o;
    }

    static void Run(string ff, string args) { using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true })!; p.WaitForExit(); }
}
