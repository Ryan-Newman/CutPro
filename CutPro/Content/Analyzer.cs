using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CutPro;

/// <summary>What CutPro knows about a video's contents: titles on the timeline, clip names, the spoken words (Whisper),
/// the scenes (CLIP), Bible references, chapter points.</summary>
public sealed class ContentInfo
{
    public string Name = "";
    public double Duration;
    public bool Vertical;
    public List<(double t, string text)> Titles = new();
    public List<string> ClipNames = new();
    public List<string> MaskNames = new();
    public string? Language;
    public List<Whisper.Segment> Transcript = new();
    public List<(string phrase, string[] tags, double score)> Scenes = new();
    public List<string> Scripture = new();
    public List<(double t, string label)> Chapters = new();
    public bool HasSpeech => Transcript.Count > 0;
    public string TranscriptText => string.Join(" ", Transcript.Select(s => s.Text.Trim()));
}

public static class Analyzer
{
    /// <summary>Gathers everything (speech and scenes only when their models are downloaded). Audio: the timeline mix rendered
    /// to WAV; frames: sampled from the clips on screen.</summary>
    public static async Task<ContentInfo> AnalyzeAsync(Editor ed, string name, IProgress<(double p, string what)>? progress, CancellationToken ct,
                                                       double maxSpeechSeconds = 15 * 60, bool models = true)
    {
        var info = new ContentInfo { Name = name, Duration = ed.End, Vertical = Editor.H > Editor.W };
        foreach (var c in ed.Clips.Where(c => c.Kind == ClipKind.Text).OrderBy(c => c.Start))
            if (!Themes.IsPlaceholder(c.Text) && c.Text.Replace('\n', ' ').Trim() is { Length: > 1 } t) info.Titles.Add((c.Start, t));   // not untouched template text
        info.ClipNames = ed.Clips.Select(c => ed.MediaOf(c)).Where(m => m != null).Select(m => CleanName(Path.GetFileNameWithoutExtension(m!.Path)))
                           .Where(s => s.Length > 2).Distinct().ToList();
        info.MaskNames = ed.Clips.SelectMany(c => c.Masks).Select(m => m.Name).Where(n => !Regex.IsMatch(n, @"^(Object|Ellipse|Rectangle|Freehand) \d+$")).Distinct().ToList();
        var ff = Exporter.FindFfmpeg();
        // speech
        if (models && Whisper.Present && ff != null && ed.Clips.Any(c => c.Kind == ClipKind.Audio || (c.Kind == ClipKind.Video && !c.NoAudio)))
        {
            progress?.Report((0.02, "Mixing the sound…"));
            var wav = Path.Combine(Path.GetTempPath(), $"cutpro_speech_{Guid.NewGuid().ToString("N")[..8]}.wav");
            try
            {
                var err = await Exporter.RunAsync(ff, ed, wav, new Progress<double>(), ct, null, null,
                                                  new ExportSettings { Video = "none", Container = "wav", Audio = "pcm" });
                if (err == null)
                {
                    var pcm = Whisper.Pcm16k(ff, wav, maxSpeechSeconds);
                    progress?.Report((0.05, "Loading the speech model…"));
                    var res = await Task.Run(() =>
                    {
                        using var w = new Whisper();
                        return w.Transcribe(pcm, maxSpeechSeconds, new Progress<double>(p => progress?.Report((0.05 + p * 0.65, $"Listening… {p * 100:0}%"))), ct);
                    }, ct);
                    info.Language = res.Language; info.Transcript = res.Segments;
                }
            }
            finally { try { File.Delete(wav); } catch { } }
        }
        // scenes
        if (models && ClipTagger.Present && ff != null)
        {
            progress?.Report((0.72, "Looking at the pictures…"));
            var times = new List<(MediaItem m, double src)>();
            double step = Math.Max(2, info.Duration / 20);
            for (double t = Math.Min(0.5, info.Duration / 2); t < info.Duration; t += step)
            {
                var c = ed.Clips.Where(x => x.Kind is ClipKind.Video or ClipKind.Image && t >= x.Start && t < x.End && !ed.TrackOff[x.Track]).OrderBy(x => x.Track).FirstOrDefault();
                if (c != null && ed.MediaOf(c) is { Offline: false } m) times.Add((m, m.Type == ClipKind.Image ? 0 : TimeMap.Src(c, t)));
            }
            var sum = new double[ClipTagger.Scenes.Length];
            int frames = 0;
            await Task.Run(() =>
            {
                using var tagger = new ClipTagger();
                for (int i = 0; i < times.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (m, src) = times[i];
                    int w = 320, h = Math.Max(2, (int)Math.Round(320.0 * m.Height / Math.Max(1, m.Width) / 2) * 2);
                    if (Grab(ff, m, src, w, h) is { } px)
                    {
                        var emb = tagger.Embed(px, w, h);
                        if (tagger.TextLikelihood(emb) < ClipTagger.TextCutoff)   // slides / lyrics: CLIP would read their words as scenes
                        {
                            var s = tagger.Scores(emb);
                            for (int k = 0; k < s.Length; k++) sum[k] += s[k];
                            frames++;
                        }
                    }
                    progress?.Report((0.72 + 0.26 * (i + 1) / times.Count, $"Looking at the pictures… {i + 1}/{times.Count}"));
                }
            }, ct);
            if (frames > 0)
                info.Scenes = ClipTagger.Scenes.Select((s, k) => (s.phrase, s.tags, score: sum[k] / frames))
                    .Where(s => s.score > 0.12 && s.phrase.Length > 0).OrderByDescending(s => s.score).Take(6).ToList();   // a confident share of the frames
        }
        // Bible references in the speech and on screen
        info.Scripture = Bible.Find(info.TranscriptText + "\n" + string.Join("\n", info.Titles.Select(t => t.text)));
        info.Chapters = Chapters(ed, info);
        progress?.Report((1, "Done"));
        return info;
    }

    static string CleanName(string s)
    {
        s = Regex.Replace(s, @"[_\-\.]+", " ").Trim();
        if (Regex.IsMatch(s, @"^(IMG|VID|DSC|MVI|GOPR|GX|PXL|MOV|DJI|C\d{4}|clip|video|export)\s*\d*", RegexOptions.IgnoreCase) || Regex.IsMatch(s, @"^\d+$")) return "";
        return Regex.Replace(s, @"\s+\d{3,}$", "");
    }

    static byte[]? Grab(string ff, MediaItem m, double t, int w, int h)
    {
        if (Nest.SourceOf(m, t) is not var (path, ft, _)) return null;                   // (a nested sequence: its render)
        using var fs = new FrameStream(ff, path, ft, w, h, 2, 0, 0, m.Seq != null ? Decoder.SwFrame : m.SeekDecoder);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10000)
        {
            bool ended = fs.Ended;
            var (_, buf) = fs.Take(fs.Start);
            if (buf != null) return (byte[])buf.Clone();
            if (ended) return null;
            Thread.Sleep(3);
        }
        return null;
    }

    /// <summary>YouTube chapters: title cards and edits on V1 at least 10 s apart (YouTube needs ≥ 3 chapters, the first at 0:00).</summary>
    static List<(double t, string label)> Chapters(Editor ed, ContentInfo info)
    {
        if (info.Duration < 60) return new();
        var marks = new SortedDictionary<double, string>();
        foreach (var (t, text) in info.Titles) marks[Math.Round(t)] = Shorten(text, 50);
        foreach (var c in ed.Clips.Where(c => c.Track == Tracks.V1 && c.Kind is ClipKind.Video or ClipKind.Image))
            if (!marks.Keys.Any(k => Math.Abs(k - c.Start) < 10)) marks[Math.Round(c.Start)] = "";
        var list = new List<(double, string)>();
        foreach (var (t, label) in marks)
        {
            if (list.Count > 0 && t - list[^1].Item1 < 10) continue;
            string l = label.Length > 0 ? label : SpokenAt(info, t) ?? $"Part {list.Count + 1}";
            list.Add((t, l));
        }
        if (list.Count == 0 || list[0].Item1 > 0) list.Insert(0, (0, info.Titles.FirstOrDefault().text is { Length: > 0 } t0 ? Shorten(t0, 50) : "Intro"));
        return list.Count >= 3 ? list : new();
    }

    static string? SpokenAt(ContentInfo info, double t)
    {
        var seg = info.Transcript.FirstOrDefault(s => s.Start <= t && s.End > t);
        if (seg == null) return null;
        var sentence = Regex.Split(seg.Text, @"(?<=[.!?])\s+").FirstOrDefault(s => s.Length > 12);
        return sentence != null ? Shorten(sentence, 50) : null;
    }

    public static string Shorten(string s, int max)
    {
        s = Regex.Replace(s.Replace('\n', ' '), @"\s+", " ").Trim();
        if (s.Length <= max) return s;
        int cut = s.LastIndexOf(' ', Math.Max(1, max - 1));
        return (cut > max / 2 ? s[..cut] : s[..(max - 1)]).TrimEnd(',', ';', ':', '-') + "…";
    }
}

/// <summary>Bible references in text: "John 3:16", "1 Corinthians 13:4-7", "Psalm 23", "Romans chapter 8 verse 28".</summary>
public static class Bible
{
    public static readonly string[] Books =
    {
        "Genesis","Exodus","Leviticus","Numbers","Deuteronomy","Joshua","Judges","Ruth","1 Samuel","2 Samuel","1 Kings","2 Kings","1 Chronicles","2 Chronicles",
        "Ezra","Nehemiah","Esther","Job","Psalms","Proverbs","Ecclesiastes","Song of Solomon","Isaiah","Jeremiah","Lamentations","Ezekiel","Daniel","Hosea",
        "Joel","Amos","Obadiah","Jonah","Micah","Nahum","Habakkuk","Zephaniah","Haggai","Zechariah","Malachi","Matthew","Mark","Luke","John","Acts","Romans",
        "1 Corinthians","2 Corinthians","Galatians","Ephesians","Philippians","Colossians","1 Thessalonians","2 Thessalonians","1 Timothy","2 Timothy","Titus",
        "Philemon","Hebrews","James","1 Peter","2 Peter","1 John","2 John","3 John","Jude","Revelation",
    };
    static readonly Dictionary<string, string> alias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["psalm"] = "Psalms", ["ps"] = "Psalms", ["prov"] = "Proverbs", ["gen"] = "Genesis", ["ex"] = "Exodus", ["deut"] = "Deuteronomy", ["isa"] = "Isaiah",
        ["jer"] = "Jeremiah", ["matt"] = "Matthew", ["mt"] = "Matthew", ["rom"] = "Romans", ["gal"] = "Galatians", ["eph"] = "Ephesians", ["phil"] = "Philippians",
        ["col"] = "Colossians", ["heb"] = "Hebrews", ["rev"] = "Revelation", ["revelations"] = "Revelation", ["song of songs"] = "Song of Solomon",
    };
    static readonly Regex rx = Build();
    static Regex Build()
    {
        string Ord(string b) => Regex.Replace(Regex.Escape(b), @"^([123])\\ ", m => $"(?:{m.Groups[1].Value}|{new[] { "", "I|First|1st", "II|Second|2nd", "III|Third|3rd" }[int.Parse(m.Groups[1].Value)]})\\s*");
        var names = Books.Select(Ord).Concat(alias.Keys.Select(Regex.Escape)).OrderByDescending(s => s.Length);
        return new Regex($@"\b(?<book>{string.Join("|", names)})\.?\s+(?:chapter\s+)?(?<ch>\d{{1,3}})(?:\s*(?:[:.]\s*|,?\s*verses?\s+)(?<v>\d{{1,3}}(?:\s*[-–]\s*\d{{1,3}})?))?",
                         RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    static readonly HashSet<string> Ambiguous = new() { "Numbers", "Mark", "Job", "Acts", "James", "Jude", "Ruth", "Titus", "Daniel", "Joel", "Amos", "Esther", "John", "Luke" };

    public static List<string> Find(string text)
    {
        var found = new List<string>();
        foreach (Match m in rx.Matches(text))
        {
            string raw = Regex.Replace(m.Groups["book"].Value.Trim(), @"\s+", " "), book = Canon(raw);
            int ch = int.Parse(m.Groups["ch"].Value);
            if (ch == 0 || ch > 150) continue;
            bool verse = m.Groups["v"].Success, chapterWord = m.Value.Contains("chapter", StringComparison.OrdinalIgnoreCase);
            // "numbers 6", "mark 2", "my ex 2", "PS 5" in normal speech: names that are also words, and abbreviations, need a verse
            bool abbrev = alias.ContainsKey(raw) && raw.Length <= 5 && !raw.Equals("psalm", StringComparison.OrdinalIgnoreCase);
            if (!verse && !chapterWord && (Ambiguous.Contains(book) || abbrev)) continue;
            string r = verse ? $"{book} {ch}:{Regex.Replace(m.Groups["v"].Value, @"\s*[-–]\s*", "–")}" : $"{book} {ch}";
            if (!found.Contains(r)) found.Add(r);
        }
        return found;
    }

    static string Canon(string b)
    {
        b = Regex.Replace(b.Trim(), @"\s+", " ");
        if (alias.TryGetValue(b, out var a)) return a;
        var ord = Regex.Match(b, @"^(I{1,3}|First|Second|Third|1st|2nd|3rd|[123])\s*(.+)$", RegexOptions.IgnoreCase);
        if (ord.Success)
        {
            string n = ord.Groups[1].Value.ToLowerInvariant() switch { "i" or "first" or "1st" or "1" => "1", "ii" or "second" or "2nd" or "2" => "2", _ => "3" };
            b = $"{n} {ord.Groups[2].Value}";
        }
        return Books.FirstOrDefault(x => x.Equals(b, StringComparison.OrdinalIgnoreCase)) ?? CultureInfo.CurrentCulture.TextInfo.ToTitleCase(b.ToLowerInvariant());
    }

    /// <summary>"John 3:16" → "John316" (the common hashtag form).</summary>
    public static string Tag(string reference) => Regex.Replace(reference, @"[^\p{L}\p{N}]", "");
}

/// <summary>Hashtag helpers.</summary>
public static class Hashtags
{
    static readonly Regex rx = new(@"(?<![\p{L}\p{N}_])#([\p{L}\p{N}_]+)", RegexOptions.Compiled);
    public static int Count(string text) => rx.Matches(text).Count;
    public static List<string> In(string text) => rx.Matches(text).Select(m => m.Groups[1].Value).ToList();
    public static string Clean(string tag) => Regex.Replace(tag.Trim().TrimStart('#'), @"[^\p{L}\p{N}_]", "");
}
