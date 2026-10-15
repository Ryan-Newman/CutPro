using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>CutPro.exe --contenttest dir: what the publish window suggests. Bible references, hashtag counting, captions for
/// every platform within its limits, and — when the models are downloaded — Whisper on synthesized speech (Windows SAPI),
/// CLIP tokenizer + zero-shot tags on drawn pictures, and the whole analysis of a small timeline.</summary>
public static class ContentTest
{
    const string Speech = "Good morning, church. Today we read John chapter 3, verse 16. For God so loved the world that he gave his one and only Son. " +
                          "His grace is enough for every one of us, and his love never fails.";

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        try
        {
            // ---- Bible references ----
            foreach (var (text, want) in new (string, string)[]
            {
                ("For God so loved the world, John 3:16 says.", "John 3:16"),
                ("Turn with me to Romans chapter 8 verse 28.", "Romans 8:28"),
                ("Love is patient, 1 Corinthians 13:4-7.", "1 Corinthians 13:4–7"),
                ("First Corinthians 13 is the love chapter.", "1 Corinthians 13"),
                ("The Lord is my shepherd, Psalm 23.", "Psalms 23"),
                ("II Timothy 3:16 and Gen 1:1", "2 Timothy 3:16|Genesis 1:1"),
                ("Whisper writes John 3.16 like this", "John 3:16"),
                ("Song of Songs 2:4; Revelation 21:4", "Song of Solomon 2:4|Revelation 21:4"),
                ("Mark 10:45 and I John 4:8", "Mark 10:45|1 John 4:8"),
                ("He made his mark 2 times, numbers 6 through 10 were sold, my ex 2 years ago, I bought a PS 5.", ""),
            })
            {
                var got = string.Join("|", Bible.Find(text));
                Check(got == want, $"Bible.Find(\"{text}\") = \"{got}\" (want \"{want}\")");
            }
            Check(Bible.Tag("1 Corinthians 13:4–7") == "1Corinthians1347" && Hashtags.Count("Grace #faith #Jesus, #John316 and C# code") == 3,
                  "hashtag forms: #1Corinthians1347; '#' inside words (C#) isn't a hashtag");
            Check(Whisper.Clean("(crickets chirping)") == "" && Whisper.Clean("Amen. [Music] Praise God ♪") == "Amen. Praise God", "sound captions are removed from transcripts");
            Check(Themes.IsPlaceholder("Name Surname\nTitle or role") && Themes.IsPlaceholder("Your Title") && !Themes.IsPlaceholder("Pastor Dan\nLead pastor"),
                  "untouched template titles are recognised (not used as the video's title)");

            // ---- captions for every platform ----
            var shortInfo = new ContentInfo
            {
                Name = "Sunday_Service", Duration = 45, Vertical = true,
                Titles = { (0, "Grace Upon Grace") },
                Transcript = { new Whisper.Segment(0, 30, Speech + " Share this with someone who needs hope today.") },
                Scenes = { ("worship", new[] { "worship", "praise", "worshipmusic" }, 0.31), ("the cross", new[] { "jesus", "cross" }, 0.12) },
                ClipNames = { "Sunday Service" },
            };
            shortInfo.Scripture = Bible.Find(shortInfo.TranscriptText);
            var longInfo = new ContentInfo
            {
                Name = "sermon", Duration = 1900, Vertical = false,
                Transcript = { new Whisper.Segment(0, 30, Speech) },
                Chapters = { (0, "Welcome"), (95, "Worship"), (610, "The message: John 3:16"), (1700, "Prayer") },
            };
            longInfo.Scripture = Bible.Find(longInfo.TranscriptText);
            foreach (var p in Platforms.All)
                foreach (var (info, kind) in new[] { (shortInfo, "short vertical"), (longInfo, "long 16:9") })
                {
                    var s = Captions.Suggest(info, p);
                    var v = new Dictionary<string, string>(s.Fields) { ["page"] = "123", ["board"] = "456" };
                    if (p.Id == "facebook" && info.Duration > 90) v["type"] = "video";
                    var errs = Platforms.Validate(p, v, Math.Min(info.Duration, p.MaxSeconds), 50L << 20, info.Vertical ? 9 / 16.0 : 16 / 9.0);
                    string main = Publishing.MainText(p, v);
                    int tags = Hashtags.Count(string.Join(" ", v.Values));
                    bool ok = errs.Count == 0 && tags <= p.HashtagLimit && (p.Id == "vimeo" || main.Length > 0);
                    if (p.Id == "youtube") ok &= v["title"].Length is > 0 and <= 100 && v["description"].Contains("John 3:16") && Platforms.Length(p, "tags", v["tags"]) <= 500
                                              && (info.Vertical ? s.Hashtags.FirstOrDefault() == "shorts" : !s.Hashtags.Contains("shorts") && v["description"].Contains("0:00 Welcome"));
                    if (p.Id == "instagram") ok &= tags <= 5;
                    if (p.Id == "threads") ok &= tags <= 1 && main.Length <= 500;
                    if (p.Id == "x") ok &= Platforms.Length(p, "text", v["text"]) <= 280;
                    if (p.Id == "facebook" && info.Vertical) ok &= v["type"] == "reel";
                    Check(ok, $"{p.Name} ({kind}): {tags} hashtag(s) [{string.Join(" ", s.Hashtags.Take(6))}], {(errs.Count == 0 ? "valid" : string.Join("; ", errs))}; " +
                              $"\"{Analyzer.Shorten(main.Replace('\n', ' '), 90)}\"");
                }
            var ig = Platforms.Get("instagram");
            Check(Platforms.Validate(ig, new Dictionary<string, string> { ["caption"] = "#a1 #b2 #c3 #d4 #e5 #f6" }, 30, 1, 0.56).Any(e => e.Contains("at most 5")), "Instagram: 6 hashtags are rejected");
            var yt = Platforms.Get("youtube");
            Check(Platforms.Validate(yt, new Dictionary<string, string> { ["title"] = "Faith <3" }, 30, 1, 1.7).Any(e => e.Contains("< or >")), "YouTube: < > in the title are rejected");
            Check(Platforms.Validate(yt, new Dictionary<string, string> { ["title"] = "T", ["publishAt"] = "2026-12-24 18:00", ["privacy"] = "public" }, 30, 1, 1.7).Any(e => e.Contains("Private")),
                  "YouTube: a scheduled video must be private");
            Check(Platforms.Length(Platforms.Get("x"), "text", "Hope 🙏 https://example.org/a/very/long/link/that/is/counted/as/twenty/three") == 5 + 2 + 1 + 23
                  && Platforms.Length(yt, "tags", "faith, holy spirit") == 5 + 13 + 1, "X weighs emoji 2 and links 23; YouTube counts quotes around multi-word tags");
            Check(Platforms.Validate(Platforms.Get("facebook"), new Dictionary<string, string> { ["page"] = "1", ["type"] = "reel" }, 120, 1, 0.56).Any(e => e.Contains("Reels")),
                  "Facebook: Reels over 90 s are rejected");

            // ---- models (downloaded by the user from the publish window; test.ps1 also fetches nothing) ----
            var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
            var wav = Path.Combine(dir, "speech.wav");
            if (Whisper.Present)
            {
                if (!File.Exists(wav)) Sapi(wav, Speech);
                var pcm = Whisper.Pcm16k(ff, wav);
                var sw = Stopwatch.StartNew();
                using var w = new Whisper();
                var res = w.Transcribe(pcm, 600, null, CancellationToken.None);
                double wer = Wer(Speech, res.Text);
                Check(res.Language == "en" && wer <= 0.2, $"Whisper: {pcm.Length / 16000.0:0.0} s of speech in {sw.Elapsed.TotalSeconds:0.0} s, language {res.Language}, word error rate {wer:P0}: \"{res.Text}\"");
                Check(Bible.Find(res.Text).Contains("John 3:16"), $"Bible reference found in the transcript: {string.Join(", ", Bible.Find(res.Text))}");
                var silence = new float[16000 * 5];
                Check(w.Transcribe(silence, 600, null, CancellationToken.None).Segments.Count == 0, "Whisper: silence gives no text");
                var tone = new float[16000 * 10];
                for (int i = 0; i < tone.Length; i++) tone[i] = 0.25f * MathF.Sin(2 * MathF.PI * 440 * i / 16000f);
                var tr = w.Transcribe(tone, 600, null, CancellationToken.None);
                Check(tr.Segments.Count == 0, $"Whisper: a test tone gives no words (no \"(crickets chirping)\" / \"Thank you.\"): \"{tr.Text}\"");
            }
            else log.Add("SKIP Whisper (model not downloaded)");

            var pics = new (string file, string want, Action<DrawingContext> draw)[]
            {
                ("cross.png", "a cross", DrawCross),
                ("sunset.png", "a sunset sky", DrawSunset),
                ("slide.png", "text", DrawSlide),
                ("bars.png", "neutral", DrawBars),
            };
            foreach (var (file, _, draw) in pics) Save(Draw(640, 360, draw), Path.Combine(dir, file));
            if (ClipTagger.Present)
            {
                using var tagger = new ClipTagger();
                var ids = tagger.Tokenize("a photo of a cat");
                Check(string.Join(",", ids) == "49406,320,1125,539,320,2368,49407", $"CLIP tokenizer: \"a photo of a cat\" → {string.Join(",", ids)}");
                foreach (var (file, want, _) in pics)
                {
                    var (px, pw, ph) = Pixels(Path.Combine(dir, file));
                    var sw = Stopwatch.StartNew();
                    var emb = tagger.Embed(px, pw, ph);
                    double textness = tagger.TextLikelihood(emb);
                    var top = tagger.Scores(emb).Select((v, i) => (v, i)).OrderByDescending(x => x.v).Take(3).ToList();
                    string tops = string.Join(", ", top.Select(x => $"{ClipTagger.Scenes[x.i].prompt} {x.v:P0}"));
                    if (want == "neutral") Check(ClipTagger.IsNeutral(top[0].i), $"CLIP {file}: a test pattern adds no scene tags: top {tops}");
                    else if (want == "text") Check(textness > ClipTagger.TextCutoff, $"CLIP {file}: mostly text {textness:P0} → left out of scene tags (scenes alone would say: {tops})");
                    else Check(textness < ClipTagger.TextCutoff && ClipTagger.Scenes[top[0].i].prompt == want, $"CLIP {file}: text {textness:P0}; top {tops} ({sw.ElapsedMilliseconds} ms)");
                }
            }
            else log.Add("SKIP CLIP (model not downloaded)");

            // ---- whole analysis of a timeline: speech on A1, the cross on V1, a title ----
            if (Whisper.Present && ClipTagger.Present)
            {
                Editor.SetSequence(1080, 1920, 30, ReframeMode.FitBlur);
                var ed = new Editor();
                var snd = await MediaProbe.ProbeAsync(wav) ?? throw new Exception("speech import failed");
                var pic = await MediaProbe.ProbeAsync(Path.Combine(dir, "cross.png")) ?? throw new Exception("picture import failed");
                ed.Media.Add(snd); ed.Media.Add(pic);
                ed.Clips.Add(new Clip { MediaId = snd.Id, Kind = ClipKind.Audio, Track = 3, Start = 0, Dur = snd.Duration });
                ed.Clips.Add(new Clip { MediaId = pic.Id, Kind = ClipKind.Image, Track = 2, Start = 0, Dur = snd.Duration });
                ed.Clips.Add(new Clip { Kind = ClipKind.Text, Track = 1, Start = 0, Dur = 3, Text = "Grace Upon Grace", Style = StyleLibrary.Get("Bold Impact") });
                var sw = Stopwatch.StartNew();
                var info = await Analyzer.AnalyzeAsync(ed, "sunday", null, CancellationToken.None);
                Check(info.HasSpeech && info.Language == "en" && info.Scripture.Contains("John 3:16") && info.Scenes.Any(s => s.phrase == "the cross") && info.Vertical,
                      $"timeline analysis in {sw.Elapsed.TotalSeconds:0.0} s: speech {info.HasSpeech}, scripture [{string.Join(", ", info.Scripture)}], " +
                      $"scenes [{string.Join(", ", info.Scenes.Select(s => $"{s.phrase} {s.score:P0}"))}], vertical {info.Vertical}");
                var s = Captions.Suggest(info, Platforms.Get("youtube"));
                Check(s.Fields["title"] == "Grace Upon Grace | John 3:16" && s.Hashtags.FirstOrDefault() == "shorts" && s.Hashtags.Contains("John316", StringComparer.OrdinalIgnoreCase),
                      $"YouTube suggestion: title \"{s.Fields["title"]}\", hashtags {string.Join(" ", s.Hashtags.Select(h => "#" + h))}");
                File.WriteAllText(Path.Combine(dir, "suggestions.txt"), string.Join("\n\n", Platforms.All.Select(p =>
                    $"== {p.Name}\n" + string.Join("\n", Captions.Suggest(info, p).Fields.Select(kv => $"[{kv.Key}] {kv.Value}")))));
            }
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        finally { Editor.SetSequence(1280, 720, 30, ReframeMode.Fit); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "contenttest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>Speech from the Windows speech synthesizer (Windows PowerShell has System.Speech; .NET 9 doesn't ship it).</summary>
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

    /// <summary>Word error rate (edit distance over words, punctuation and case ignored; digits and number words both count).</summary>
    static double Wer(string reference, string hyp)
    {
        static string[] Words(string s) => Regex.Replace(s.ToLowerInvariant().Replace("three", "3").Replace("sixteen", "16"), @"[^\p{L}\p{N}' ]", " ")
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

    static BitmapSource Draw(int w, int h, Action<DrawingContext> draw)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) { dc.PushTransform(new ScaleTransform(w / 640.0, h / 360.0)); draw(dc); dc.Pop(); }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv); rtb.Freeze();
        return rtb;
    }
    static void Save(BitmapSource bmp, string path) => FxTest.Save(bmp, path);

    static (byte[] px, int w, int h) Pixels(string png)
    {
        var bf = BitmapFrame.Create(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var src = new FormatConvertedBitmap(bf, PixelFormats.Bgr32, null, 0);
        var px = new byte[src.PixelWidth * src.PixelHeight * 4];
        src.CopyPixels(px, src.PixelWidth * 4, 0);
        return (px, src.PixelWidth, src.PixelHeight);
    }

    // drawn test pictures (640 × 360 design space)
    static void DrawCross(DrawingContext dc)
    {
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(120, 170, 225), Color.FromRgb(215, 230, 245), 90), null, new Rect(0, 0, 640, 360));
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(70, 120, 60)), null, Geometry.Parse("M0,300 Q320,250 640,300 L640,360 L0,360 Z"));
        var wood = new LinearGradientBrush(Color.FromRgb(120, 80, 45), Color.FromRgb(85, 55, 30), 0);
        dc.DrawRectangle(wood, null, new Rect(300, 40, 40, 250));
        dc.DrawRectangle(wood, null, new Rect(240, 95, 160, 36));
    }
    static void DrawSunset(DrawingContext dc)
    {
        var sky = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        sky.GradientStops.Add(new GradientStop(Color.FromRgb(60, 40, 110), 0));
        sky.GradientStops.Add(new GradientStop(Color.FromRgb(230, 90, 70), 0.55));
        sky.GradientStops.Add(new GradientStop(Color.FromRgb(255, 180, 80), 0.8));
        dc.DrawRectangle(sky, null, new Rect(0, 0, 640, 360));
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 225, 140)), null, new Point(320, 285), 55, 55);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(35, 25, 45)), null, new Rect(0, 290, 640, 70));
    }
    static void DrawBars(DrawingContext dc)
    {
        string[] cols = { "#C0C0C0", "#C0C000", "#00C0C0", "#00C000", "#C000C0", "#C00000", "#0000C0" };
        for (int i = 0; i < 7; i++) dc.DrawRectangle(new SolidColorBrush((Color)ColorConverter.ConvertFromString(cols[i])), null, new Rect(i * 640 / 7.0, 0, 640 / 7.0 + 1, 250));
        for (int i = 0; i < 7; i++) dc.DrawRectangle(new SolidColorBrush((Color)ColorConverter.ConvertFromString(cols[6 - i])), null, new Rect(i * 640 / 7.0, 250, 640 / 7.0 + 1, 30));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 16, 16)), null, new Rect(0, 280, 640, 80));
        dc.DrawRectangle(Brushes.White, null, new Rect(110, 290, 110, 60));
    }
    static void DrawSlide(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 40, 90)), null, new Rect(0, 0, 640, 360));
        var tf = new Typeface("Segoe UI Semibold");
        void T(string s, double size, double y) => dc.DrawText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, Brushes.White, 1.0), new Point(50, y));
        T("Welcome to Sunday Service", 40, 70);
        T("• Announcements", 26, 160); T("• Youth group meets Wednesday at 7 pm", 26, 205); T("• Potluck after the second service", 26, 250);
    }
}
