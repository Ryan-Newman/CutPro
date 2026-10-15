using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>CutPro.exe --captiontest dir: captions from speech.
/// - Whisper with timestamps on synthesized speech (Windows SAPI) with known sentence starts, a long silence and a tone: words (WER), language,
///   each sentence's first caption within 0.6 s of where it is said, nothing captioned in the silence / tone, listening speed;
/// - cutting into captions: line length (characters and measured width), lines, seconds on screen, no overlaps, on frames, short phrases;
/// - SRT / WebVTT out and back in, files with tags / cue settings / hours;
/// - on the timeline: caption titles on a free track (V3, or V2 when V3 is taken), one undo step;
/// - burned in: preview vs export of captions (the export stitches a track's captions into one input) incl. a fading one (the title path),
///   not burned in = the picture without them, 300 captions export in reasonable time.</summary>
public static class CaptionTest
{
    static readonly string[] Sentences =
    {
        "Good morning, church, and welcome to our service today.",
        "Please open your Bibles to the book of Psalms, chapter twenty three.",
        "The Lord is my shepherd, I shall not want.",
        "He makes me lie down in green pastures and leads me beside still waters.",
        "Even in the darkest valley, we do not have to be afraid.",
        "Let us pray together before we read the rest of the passage.",
    };

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        try
        {
            Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
            // ---------- 1. speech with known times ----------
            // sentence i starts at starts[i]; between sentences 3 and 4: 6 s of silence then 4 s of a tone (no words there)
            var starts = new List<double>();
            var speech = Path.Combine(dir, "speech.wav");
            if (!File.Exists(speech))
            {
                var pieces = new List<string>();
                for (int i = 0; i < Sentences.Length; i++)
                {
                    var w = Path.Combine(dir, $"s{i}.wav");
                    if (!File.Exists(w)) Sapi(w, Sentences[i]);
                    pieces.Add(w);
                }
                // concat: 1 s silence, s0, 1.5 s, s1, 1.5 s, s2, 6 s silence + 4 s tone, s3, 1.5 s, s4, 1.5 s, s5, 1 s
                var fg = new System.Text.StringBuilder(); var inputs = new List<string>();
                string Sil(double d) { inputs.Add($"-f lavfi -t {d.ToString(System.Globalization.CultureInfo.InvariantCulture)} -i anullsrc=r=48000:cl=mono"); return $"[{inputs.Count - 1}:a]"; }
                string Tone(double d) { inputs.Add($"-f lavfi -t {d.ToString(System.Globalization.CultureInfo.InvariantCulture)} -i sine=frequency=330:sample_rate=48000"); return $"[{inputs.Count - 1}:a]"; }
                string In(string f) { inputs.Add($"-i \"{f}\""); return $"[{inputs.Count - 1}:a]"; }
                var seq = new List<string> { Sil(1.0) };
                for (int i = 0; i < pieces.Count; i++)
                {
                    seq.Add(In(pieces[i]));
                    if (i == 2) { seq.Add(Sil(6)); seq.Add(Tone(4)); seq.Add(Sil(1)); }
                    else if (i < pieces.Count - 1) seq.Add(Sil(1.5));
                }
                seq.Add(Sil(1.0));
                var chain = string.Concat(seq.Select((s, k) => $"{s}aresample=48000,aformat=channel_layouts=mono[p{k}];"));
                var graph = chain + string.Concat(seq.Select((_, k) => $"[p{k}]")) + $"concat=n={seq.Count}:v=0:a=1[out]";
                Run(ff, $"-y -v error {string.Join(" ", inputs)} -filter_complex \"{graph}\" -map \"[out]\" -ac 2 -c:a pcm_s16le \"{speech}\"");
            }
            // the sentence starts, from the pieces' lengths
            {
                double at = 1.0;
                for (int i = 0; i < Sentences.Length; i++)
                {
                    starts.Add(at);
                    at += Duration(ff, Path.Combine(dir, $"s{i}.wav")) + (i == 2 ? 11 : 1.5);
                }
            }
            Check(System.IO.File.Exists(speech), $"synthesized speech: {Sentences.Length} sentences, starts {string.Join(", ", starts.Select(s => s.ToString("0.0")))} s, a 6 s silence and a 4 s tone after the third");
            Whisper.Result? res = null;
            if (!Whisper.Present) Check(true, "speech model not downloaded: transcription skipped");
            else
            {
                var pcm = Whisper.Pcm16k(ff, speech);
                var sw = Stopwatch.StartNew();
                using (var w = new Whisper()) res = w.TranscribeTimed(pcm, null, null, CancellationToken.None);
                double secs = pcm.Length / 16000.0;
                double wer = Wer(string.Join(" ", Sentences), res.Text);
                Check(res.Language == "en" && wer <= 0.15, $"transcribed {secs:0.0} s in {sw.Elapsed.TotalSeconds:0.0} s ({secs / sw.Elapsed.TotalSeconds:0.0}× real time), language {res.Language}, word error rate {wer:P0} (≤ 15 %): “{Short(res.Text)}”");
                Check(res.Segments.All(s => s.End >= s.Start) && res.Segments.Zip(res.Segments.Skip(1)).All(p => p.Second.Start >= p.First.Start - 0.01),
                      $"{res.Segments.Count} timed segments, in order: " + string.Join(" | ", res.Segments.Select(s => $"{s.Start:0.0}–{s.End:0.0}")));
                double gap0 = starts[2] + Duration(ff, Path.Combine(dir, "s2.wav")) + 0.3, gap1 = starts[3] - 0.3;
                Check(!res.Segments.Any(s => s.Start > gap0 && s.Start < gap1), $"nothing heard in the silence and tone ({gap0:0.0}–{gap1:0.0} s)");
            }

            // ---------- 2. cutting into captions ----------
            var segs = res?.Segments ?? Sentences.Select((s, i) => new Whisper.Segment(starts[i], starts[i] + 3.5, s)).ToList();
            var voice = new Captioning.Voice(Whisper.Pcm16k(ff, speech));
            var ed = new Editor();
            var lay = Captioning.Layouts[0];
            var (style, y) = Captioning.Look(ed, lay);
            var measure = Captioning.Measure(style);
            double maxW = Editor.W * 0.88;
            var cues = Captioning.Build(segs, lay, measure, maxW, 30, voice);
            var lines = cues.SelectMany(c => c.Text.Split('\n')).ToList();
            bool onFrames = cues.All(c => Math.Abs(c.Start * 30 - Math.Round(c.Start * 30)) < 1e-6 && Math.Abs(c.End * 30 - Math.Round(c.End * 30)) < 1e-6);
            bool noOverlap = cues.Zip(cues.Skip(1)).All(p => p.Second.Start >= p.First.End - 1e-9);
            Check(cues.Count > 0 && lines.All(l => l.Length <= 42 && measure(l) <= maxW) && cues.All(c => c.Text.Split('\n').Length <= 2) && cues.All(c => c.End - c.Start <= 6.5)
                  && cues.All(c => c.End - c.Start >= 0.69) && onFrames && noOverlap,
                  $"subtitles: {cues.Count} captions, longest line {lines.Max(l => l.Length)} chars / {lines.Max(measure):0} px of {maxW:0}, ≤ 2 lines, {cues.Min(c => c.End - c.Start):0.0}–{cues.Max(c => c.End - c.Start):0.0} s, on frames, no overlaps");
            // word times (the voice-refined ones the captions are cut from): each sentence's first word starts when it is said, its last ends with it
            var words = Captioning.Words(segs, voice);
            string Norm(string w) => Regex.Replace(w.ToLowerInvariant(), @"[^\p{L}\p{N}']", "");
            // where each sentence begins in the transcript: its first two words, in order
            var firstIdx = new int[Sentences.Length]; int from = 0;
            for (int i = 0; i < Sentences.Length; i++)
            {
                var two = FirstWords(Sentences[i], 2).ToLowerInvariant().Split(' ');
                int k2 = from; while (k2 + 1 < words.Count && !(Norm(words[k2].w) == two[0] && Norm(words[k2 + 1].w) == two[1])) k2++;
                firstIdx[i] = k2 + 1 < words.Count ? k2 : -1;
                if (firstIdx[i] >= 0) from = k2 + 1;
            }
            for (int i = 0; i < Sentences.Length; i++)
            {
                int wi = firstIdx[i];
                if (wi < 0) { Check(false, $"sentence {i + 1}: its first words “{FirstWords(Sentences[i], 2)}” not found"); continue; }
                double dt = words[wi].a - starts[i], end = starts[i] + Duration(ff, Path.Combine(dir, $"s{i}.wav"));
                int last = (i + 1 < Sentences.Length && firstIdx[i + 1] > wi ? firstIdx[i + 1] : words.Count) - 1;
                double de = words[last].b - end;
                var on = cues.FirstOrDefault(c => starts[i] + 0.3 >= c.Start && starts[i] + 0.3 < c.End);
                Check(Math.Abs(dt) <= 0.4 && Math.Abs(de) <= 0.8 && on != null && on.Text.Replace('\n', ' ').Contains(FirstWords(Sentences[i], 2).Split(' ')[0], StringComparison.OrdinalIgnoreCase),
                      $"sentence {i + 1} (“{FirstWords(Sentences[i], 4)}…”) said {starts[i]:0.00}–{end:0.00} s: words timed {words[wi].a:0.00}–{words[last].b:0.00} ({dt:+0.00;-0.00} / {de:+0.00;-0.00} s), its caption on screen as it begins");
            }
            {
                double g0 = starts[2] + Duration(ff, Path.Combine(dir, "s2.wav")) + 0.6, g1 = starts[3] - 0.3;
                var during = cues.Where(c => c.End > g0 && c.Start < g1).ToList();
                Check(during.Count == 0, $"no caption on screen during the silence and the tone ({g0:0.0}–{g1:0.0} s){(during.Count > 0 ? ": " + string.Join(", ", during.Select(c => $"{c.Start:0.0}–{c.End:0.0}")) : "")}");
            }
            var phr = Captioning.Build(segs, Captioning.Layouts[2], Captioning.Measure(Captioning.Look(ed, Captioning.Layouts[2]).style), maxW, 30, voice);
            Check(phr.Count > cues.Count && phr.All(c => !c.Text.Contains('\n') && c.Text.Length <= 20) && phr.All(c => c.End - c.Start <= 2.2 + 0.7),
                  $"short phrases: {phr.Count} captions, one line ≤ 20 chars, ≤ {phr.Max(c => c.End - c.Start):0.0} s");
            Editor.SetSequence(1080, 1920, 30, ReframeMode.Fill);
            var (vstyle, _) = Captioning.Look(ed, lay);
            var vmeasure = Captioning.Measure(vstyle);
            var vcues = Captioning.Build(segs, lay, vmeasure, Editor.W * 0.88, 30, voice);
            Check(vcues.SelectMany(c => c.Text.Split('\n')).All(l => vmeasure(l) <= Editor.W * 0.88), $"9:16: {vcues.Count} captions, every line within {Editor.W * 0.88:0} px (longest {vcues.SelectMany(c => c.Text.Split('\n')).Max(vmeasure):0})");
            Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);

            // ---------- 3. SRT / WebVTT ----------
            var srt = Captioning.ToSrt(cues); var back = Captioning.Parse(srt);
            Check(back.Count == cues.Count && back.Zip(cues).All(p => Math.Abs(p.First.Start - p.Second.Start) < 0.0011 && Math.Abs(p.First.End - p.Second.End) < 0.0011 && p.First.Text == p.Second.Text),
                  $"SRT round trip: {back.Count} captions, times within 1 ms, text and line breaks kept");
            var vtt = Captioning.ToVtt(cues); var back2 = Captioning.Parse(vtt);
            Check(vtt.StartsWith("WEBVTT") && back2.Count == cues.Count && back2.Zip(cues).All(p => Math.Abs(p.First.Start - p.Second.Start) < 0.0011 && p.First.Text == p.Second.Text), "WebVTT round trip");
            var foreign = Captioning.Parse("WEBVTT\n\nNOTE made elsewhere\n\n00:01.500 --> 00:03.250 align:start line:90%\n<v Pastor>Grace and <b>peace</b></v>\n\n1:02:03.000 --> 1:02:04.5\nto you\n");
            Check(foreign.Count == 2 && foreign[0].Text == "Grace and peace" && Math.Abs(foreign[0].Start - 1.5) < 1e-9 && Math.Abs(foreign[1].Start - 3723) < 1e-9 && Math.Abs(foreign[1].End - 3724.5) < 1e-9,
                  "a WebVTT file with a note, cue settings, voice / bold tags, hours and short milliseconds");
            var inr = Captioning.InRange(cues, 5, 20);
            Check(inr.All(c => c.Start >= 0 && c.End <= 15 + 1e-9) && inr.Count > 0, $"In 5 s to Out 20 s: {inr.Count} captions, timed from 0");

            // ---------- 4. on the timeline ----------
            var fgv = Path.Combine(dir, "fg50.mp4");
            if (!System.IO.File.Exists(fgv)) Run(ff, $"-y -v error -f lavfi -i testsrc2=s=1280x720:r=30:d=50 -c:v libx264 -crf 12 -pix_fmt yuv420p \"{fgv}\"");
            var mv = await MediaProbe.ProbeAsync(fgv) ?? throw new Exception("import video");
            var ma = await MediaProbe.ProbeAsync(speech) ?? throw new Exception("import speech");
            ed.Media.AddRange(new[] { mv, ma });
            double len = Math.Min(mv.Duration, ma.Duration);
            ed.Clips.Add(new Clip { MediaId = mv.Id, Kind = ClipKind.Video, Track = 2, Start = 0, Dur = len, NoAudio = true });
            ed.Clips.Add(new Clip { MediaId = ma.Id, Kind = ClipKind.Audio, Track = 3, Start = 0, Dur = len });
            int track = Captioning.FreeTrack(ed, cues);
            Captioning.Place(ed, cues, track, style, y);
            var caps = Captioning.Clips(ed);
            Check(track == 0 && caps.Count == cues.Count && caps.All(c => c.Track == 0 && c.Role == TextRole.Caption), $"captions on V3: {caps.Count} caption titles");
            ed.Undo();
            Check(Captioning.Clips(ed).Count == 0, "one undo removes them all");
            ed.Redo();
            var blocker = new Clip { Kind = ClipKind.Text, Track = 0, Start = cues[2].Start, Dur = 1, Text = "TITLE", Style = Themes.Get(ed.ThemeName).Title.Clone() };
            ed.Clips.Add(blocker);
            Check(Captioning.FreeTrack(ed, cues) == 1, "a title on V3 where captions go: they go on V2");
            ed.Clips.Remove(blocker);

            // ---------- 5. burned in ----------
            var fade = Captioning.Clips(ed)[1]; fade.FadeIn = 0.4;                         // (one caption takes the title path)
            var mp4 = Path.Combine(dir, "captions.mkv");
            async Task<string?> Export(string file, ExportSettings o)
            {
                var titles = TitleVisual.PrepareExport(ed, Path.Combine(dir, "t_" + Path.GetFileNameWithoutExtension(file)), o.OutW, o.OutH, !o.BurnCaptions);
                return await Task.Run(() => Exporter.RunAsync(ff, ed, file, new Progress<double>(), CancellationToken.None, titles, null, o));
            }
            var sw2 = Stopwatch.StartNew();
            var err = await Export(mp4, new ExportSettings { Container = "mkv", Video = "ffv1", Audio = "none" });
            Check(err == null, $"export with {Captioning.Clips(ed).Count} burned-in captions ({sw2.Elapsed.TotalSeconds:0.0} s)" + (err == null ? "" : ": " + err));
            var plain = Path.Combine(dir, "nocaptions.mkv");
            var err2 = await Export(plain, new ExportSettings { Container = "mkv", Video = "ffv1", Audio = "none", BurnCaptions = false });
            Check(err2 == null, "export without burning them in" + (err2 == null ? "" : ": " + err2));
            if (err == null && err2 == null)
            {
                var pv = new Preview { Ed = ed, Width = Editor.W, Height = Editor.H, Background = Brushes.Black };
                var all = Captioning.Clips(ed);
                foreach (var (c, name) in new[] { (all[0], "first caption"), (all[1], "fading caption (title path)"), (all[all.Count / 2], "a middle caption"), (all[^1], "last caption") })
                {
                    double tt = Math.Round((c.Start + Math.Min(0.6, c.Dur / 2)) * 30) / 30;
                    var swp = Stopwatch.StartNew();
                    do { pv.Sync(tt, false); await Task.Delay(20); } while ((!pv.Settled(tt) || swp.ElapsedMilliseconds < 400) && swp.ElapsedMilliseconds < 8000);
                    pv.Sync(tt, false); pv.Measure(new Size(Editor.W, Editor.H)); pv.Arrange(new Rect(0, 0, Editor.W, Editor.H)); pv.UpdateLayout();
                    var rtb = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
                    var pa = new byte[Editor.W * Editor.H * 4]; rtb.CopyPixels(pa, Editor.W * 4, 0);
                    var pb = FxTest.Frame(ff, mp4, tt - 0.001);
                    var (psnr, worst) = FxTest.Compare(pa, pb);
                    var (sx, sy) = SeqTest.Shift(pa, pb, Editor.W, Editor.H);
                    Check(psnr >= 34 && Math.Abs(sx) <= 1 && Math.Abs(sy) <= 1, $"{name} at {tt:0.00} s: preview vs export PSNR {psnr:0.0} dB (≥ 34), worst block {worst:0.0}/255, shift ({sx},{sy})");
                    FxTest.Save(FxTest.SideBySide(pa, pb), Path.Combine(dir, $"cap_{name.Split(' ')[0]}.png"));
                    var (p2, _) = FxTest.Compare(FxTest.Frame(ff, plain, tt - 0.001), FxTest.Frame(ff, fgv, tt - 0.001));
                    if (name.StartsWith("first")) Check(p2 >= 40, $"not burned in: the picture without captions (PSNR {p2:0.0} dB vs the video itself)");
                    // the caption's frames: on at its first frame, off after its last
                    if (name == "a middle caption")
                    {   // the switch to it happens on the same frame in the preview and the export: its first frame and the frame before
                        foreach (var (bt, what) in new[] { (c.Start, "its first frame"), (c.Start - 1.0 / 30, "the frame before") })
                        {
                            double b = Math.Round(bt * 30) / 30;
                            var swb = Stopwatch.StartNew();
                            do { pv.Sync(b, false); await Task.Delay(20); } while ((!pv.Settled(b) || swb.ElapsedMilliseconds < 400) && swb.ElapsedMilliseconds < 8000);
                            pv.Sync(b, false); pv.Measure(new Size(Editor.W, Editor.H)); pv.Arrange(new Rect(0, 0, Editor.W, Editor.H)); pv.UpdateLayout();
                            var rb = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); rb.Render(pv);
                            var qa = new byte[Editor.W * Editor.H * 4]; rb.CopyPixels(qa, Editor.W * 4, 0);
                            var (pq, _) = FxTest.Compare(qa, FxTest.Frame(ff, mp4, b - 0.001));
                            Check(pq >= 34, $"a caption change, {what} ({b:0.000} s): preview vs export PSNR {pq:0.0} dB (≥ 34)");
                        }
                    }
                }
                pv.DisposeAll();
            }
            // 300 captions on one track (the export takes them as one input)
            var many = Enumerable.Range(0, 300).Select(i => new Cue(i * 0.1, i * 0.1 + 0.1, $"Caption number {i + 1}")).ToList();
            var ed2 = new Editor { Media = { mv } };
            ed2.Clips.Add(new Clip { MediaId = mv.Id, Kind = ClipKind.Video, Track = 2, Start = 0, Dur = 30, NoAudio = true });
            var sty = Themes.Get(ed2.ThemeName).For(TextRole.Caption).Clone();
            foreach (var q in many) ed2.Clips.Add(new Clip { Kind = ClipKind.Text, Role = TextRole.Caption, Track = 0, Start = Math.Round(q.Start * 30) / 30, Dur = Math.Round((q.End - q.Start) * 30) / 30, Text = q.Text, Style = sty.Clone(), Y = 38 });
            var tdir = Path.Combine(dir, "many_titles");
            var sw3 = Stopwatch.StartNew();
            var mtitles = TitleVisual.PrepareExport(ed2, tdir);
            double prep = sw3.Elapsed.TotalSeconds; sw3.Restart();
            var mmp = Path.Combine(dir, "many.mkv");
            var err3 = await Task.Run(() => Exporter.RunAsync(ff, ed2, mmp, new Progress<double>(), CancellationToken.None, mtitles, null, new ExportSettings { Container = "mkv", Video = "ffv1", Audio = "none" }));
            Check(err3 == null && sw3.Elapsed.TotalSeconds < 120, $"300 captions in 30 s: titles drawn in {prep:0.0} s, exported in {sw3.Elapsed.TotalSeconds:0.0} s" + (err3 == null ? "" : ": " + err3));
            if (err3 == null)
            {
                // captions 200 and 201 on their frames: preview vs export (the stitched input switches on the same frame)
                var pv2 = new Preview { Ed = ed2, Width = Editor.W, Height = Editor.H, Background = Brushes.Black };
                var sorted = ed2.Clips.Where(Captioning.IsCaption).OrderBy(c => c.Start).ToList();
                double worstP = 99; var each = new List<string>();
                foreach (var c in new[] { sorted[199], sorted[200], sorted[299] })
                    foreach (double b in new[] { c.Start, c.End - 1.0 / 30 })
                    {
                        double bb = Math.Round(b * 30) / 30;
                        var swb = Stopwatch.StartNew();
                        do { pv2.Sync(bb, false); await Task.Delay(20); } while ((!pv2.Settled(bb) || swb.ElapsedMilliseconds < 300) && swb.ElapsedMilliseconds < 8000);
                        pv2.Sync(bb, false); pv2.Measure(new Size(Editor.W, Editor.H)); pv2.Arrange(new Rect(0, 0, Editor.W, Editor.H)); pv2.UpdateLayout();
                        var rb = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); rb.Render(pv2);
                        var qa = new byte[Editor.W * Editor.H * 4]; rb.CopyPixels(qa, Editor.W * 4, 0);
                        double pp = FxTest.Compare(qa, FxTest.Frame(ff, mmp, bb - 0.001)).psnr; worstP = Math.Min(worstP, pp); each.Add($"{bb * 30:0}:{pp:0}");
                        if (pp < 34) { FxTest.Save(FxTest.SideBySide(qa, FxTest.Frame(ff, mmp, bb - 0.001)), Path.Combine(dir, $"many_{bb * 30:0}.png")); }
                    }
                pv2.DisposeAll();
                Check(worstP >= 34, $"300 captions: first and last frames of captions 200, 201 and 300, preview vs export ≥ {worstP:0.0} dB (≥ 34) [frame:dB {string.Join(' ', each)}]");
            }
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "captiontest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static string FirstWords(string s, int n) => string.Join(" ", Regex.Replace(s, @"[^\p{L}\p{N}' ]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(n));
    static string Short(string s) => s.Length > 140 ? s[..140] + "…" : s;

    static double Duration(string ff, string file)
    {
        var probe = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe");
        var psi = new ProcessStartInfo(File.Exists(probe) ? probe : "ffprobe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        return double.Parse(o.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Speech from the Windows speech synthesizer (Windows PowerShell has System.Speech).</summary>
    static void Sapi(string wav, string text)
    {
        string script = "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.Rate = 0; " +
                        $"$s.SetOutputToWaveFile('{wav.Replace("'", "''")}'); $s.Speak('{text.Replace("'", "''")}'); $s.Dispose()";
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(60000);
        if (!File.Exists(wav)) throw new Exception("speech synthesis failed");
    }

    static double Wer(string reference, string hyp)
    {
        static string[] Words(string s) => Regex.Replace(s.ToLowerInvariant().Replace("twenty three", "23").Replace("twenty-three", "23"), @"[^\p{L}\p{N}' ]", " ")
                                                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var r = Words(reference); var h = Words(hyp);
        var d = new int[r.Length + 1, h.Length + 1];
        for (int i = 0; i <= r.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= h.Length; j++) d[0, j] = j;
        for (int i = 1; i <= r.Length; i++)
            for (int j = 1; j <= h.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));
        return d[r.Length, h.Length] / (double)Math.Max(1, r.Length);
    }

    static void Run(string ff, string args) { using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!; p.StandardError.ReadToEnd(); p.WaitForExit(); }
}
