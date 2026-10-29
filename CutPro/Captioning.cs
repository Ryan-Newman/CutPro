using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace CutPro;

/// <summary>A caption: its time on the timeline and its text (lines separated by \n).</summary>
public sealed record Cue(double Start, double End, string Text);

/// <summary>How speech is cut into captions: at most MaxLines lines of at most MaxChars characters (and the frame's width), at most
/// MaxSeconds on screen; Phrases = short punchy pieces (Shorts / Reels style, big text).</summary>
public sealed record CaptionLayout(string Name, int MaxChars, int MaxLines, double MaxSeconds, bool Phrases = false);

/// <summary>
/// Captions from speech: Whisper's timed segments (<see cref="Whisper.TranscribeTimed"/>) → cues that read well (words timed by their length
/// inside each segment — pauses after punctuation weigh more —, lines balanced and measured with the caption style's font against the frame,
/// sentence ends preferred as breaks, short gaps closed, everything on frames) → caption titles (<see cref="TextRole.Caption"/> clips on a
/// free top video track, styled by the title theme), which the preview draws and the export burns in like any title. SRT / WebVTT out and in.
/// CEA-608 closed captions: <see cref="ToScc"/> writes Scenarist SCC (pop-on, channel 1); the export muxes that as a c608 track in MOV
/// (MP4 is remuxed as QuickTime so the same track fits) and as SMPTE 436M ANC in MXF (the broadcast 608/708 packet).
/// </summary>
public static class Captioning
{
    public static readonly CaptionLayout[] Layouts =
    {
        new("Subtitles — up to 2 lines", 42, 2, 6),
        new("One line", 42, 1, 4),
        new("Short phrases — big (Shorts, Reels)", 20, 1, 2.2, Phrases: true),
    };

    /// <summary>Clips that are captions (caption-role titles), in time order.</summary>
    public static List<Clip> Clips(Editor ed) => ed.Clips.Where(IsCaption).OrderBy(c => c.Start).ThenBy(c => c.Track).ToList();
    public static bool IsCaption(Clip c) => c.Kind == ClipKind.Text && c.Role == TextRole.Caption;

    // ---------------- segments → cues ----------------
    /// <summary>Where someone is speaking: 20 ms frames of 16 kHz sound louder than the noise floor (its 10th percentile) by 10 dB, gaps under
    /// 0.25 s between words filled, blips under 0.06 s dropped.</summary>
    public sealed class Voice
    {
        public const double Step = 0.02;
        public readonly bool[] On;
        public Voice(float[] pcm16k)
        {
            int n = pcm16k.Length / 320;
            var db = new double[n];
            for (int f = 0; f < n; f++) { double s = 0; for (int i = f * 320; i < f * 320 + 320; i++) s += pcm16k[i] * pcm16k[i]; db[f] = 10 * Math.Log10(s / 320 + 1e-10); }
            double floor = n == 0 ? -90 : db.OrderBy(x => x).ElementAt(n / 10), thr = Math.Max(floor + 10, -55);
            On = db.Select(x => x > thr).ToArray();
            Fill(On, true, 12); Fill(On, false, 3);
        }
        static void Fill(bool[] a, bool gapsOf, int max)
        {
            // runs of !gapsOf shorter than max between runs of gapsOf become gapsOf
            for (int i = 0; i < a.Length;)
            {
                if (a[i] == gapsOf) { i++; continue; }
                int j = i; while (j < a.Length && a[j] != gapsOf) j++;
                if (i > 0 && j < a.Length && j - i < max) for (int k = i; k < j; k++) a[k] = gapsOf;
                i = j;
            }
        }
        public bool At(double t) { int f = (int)(t / Step); return f >= 0 && f < On.Length && On[f]; }
        /// <summary>The pauses (no voice for at least 0.15 s) as [start, end) times.</summary>
        public List<(double a, double b)> Pauses()
        {
            var r = new List<(double, double)>();
            for (int i = 0; i < On.Length;)
            {
                if (On[i]) { i++; continue; }
                int j = i; while (j < On.Length && !On[j]) j++;
                if (j - i >= 8) r.Add((i * Step, j * Step));
                i = j;
            }
            return r;
        }
        /// <summary>Speaking time between a and b.</summary>
        public double Voiced(double a, double b) { double v = 0; for (double t = a; t < b; t += Step) if (At(t)) v += Step; return v; }
    }

    /// <summary>Each word's time. Whisper's segment times are coarse (a segment often starts before the speech and ends at the next one, across a
    /// pause or music), so with the sound at hand: a segment starts where its voice starts, lasts at most what its words take at a slow speaking
    /// rate, ends where its voice ends, and its words share its voiced time only (pauses get none). Without the sound: shared by length, with a
    /// little pause after punctuation.</summary>
    public static List<(string w, double a, double b)> Words(IEnumerable<Whisper.Segment> segments, Voice? voice)
    {
        var words = new List<(string w, double a, double b)>();
        var list = segments.ToList();
        if (voice != null && list.Count > 1)
        {
            // a boundary between two segments goes into the clearest pause near it (Whisper's lands inside the next sentence or before a pause):
            // the earlier segment ends where the pause starts, the next starts where it ends
            var pauses = voice.Pauses();
            for (int i = 0; i + 1 < list.Count; i++)
            {
                double B = list[i + 1].Start;
                var best = pauses.Where(p => p.b > B - 1.6 && p.a < B + 0.6 && p.a > list[i].Start + 0.2 && p.b < list[i + 1].End - 0.1)
                                 .OrderByDescending(p => Math.Min(p.b - p.a, 2.0) - 0.6 * Math.Max(0, Math.Max(p.a - B, B - p.b))).FirstOrDefault();
                if (best.b <= best.a) continue;
                list[i] = list[i] with { End = best.a };
                list[i + 1] = list[i + 1] with { Start = best.b };
            }
        }
        for (int si = 0; si < list.Count; si++)
        {
            var s = list[si];
            var ws = Regex.Split(s.Text.Trim(), @"\s+").Where(w => w.Length > 0).ToArray();
            if (ws.Length == 0) continue;
            double a = s.Start, b = Math.Max(s.End, s.Start + 0.05);
            if (voice != null)
            {
                int chars = ws.Sum(w => w.Length + 1);
                double first = a; while (first < b && !voice.At(first)) first += Voice.Step;
                if (first < b) a = first;
                b = Math.Min(b, a + Math.Max(1.0, chars / 9.0));                   // (9 chars/s ≈ a slow speaker: Whisper's end past that is a pause)
                double last = b; while (last > a && !voice.At(last - Voice.Step)) last -= Voice.Step;
                if (last > a + 0.1) b = last;
                double voiced = voice.Voiced(a, b);
                if (voiced > 0.2)
                {
                    double[] wt = ws.Select(w => w.Length + 1.0).ToArray();
                    double sum = wt.Sum(), acc = 0, need = 0, t = a;
                    for (int i = 0; i < ws.Length; i++)
                    {
                        need += voiced * wt[i] / sum;
                        while (t < b && !voice.At(t)) t += Voice.Step;               // a word starts on voice
                        double start = t;
                        while (t < b && acc < need - 1e-9) { if (voice.At(t)) acc += Voice.Step; t += Voice.Step; }
                        words.Add((ws[i], start, Math.Max(start + Voice.Step, Math.Min(t, b))));
                    }
                    continue;
                }
            }
            {
                double[] wt = ws.Select(w => w.Length + 1.0 + (Regex.IsMatch(w, @"[.!?]$") ? 4 : Regex.IsMatch(w, @"[,;:]$") ? 2 : 0)).ToArray();
                double sum = wt.Sum(), t = a, span = b - a;
                for (int i = 0; i < ws.Length; i++)
                {
                    double d = span * wt[i] / sum;
                    double tail = Regex.IsMatch(ws[i], @"[.!?]$") ? d * 4 / wt[i] : Regex.IsMatch(ws[i], @"[,;:]$") ? d * 2 / wt[i] : 0;   // (the pause isn't the word)
                    words.Add((ws[i], t, t + d - tail));
                    t += d;
                }
            }
        }
        return words;
    }

    /// <param name="width">width of one line of text in logical px (the caption style's font)</param>
    /// <param name="maxWidth">the widest a line may be (the frame minus margins)</param>
    /// <param name="voice">where the sound has speech (better times), or null</param>
    public static List<Cue> Build(IEnumerable<Whisper.Segment> segments, CaptionLayout lay, Func<string, double> width, double maxWidth, double fps, Voice? voice = null)
    {
        var words = Words(segments, voice);
        var cues = new List<Cue>();
        int k = 0;
        while (k < words.Count)
        {
            int j = k;                                                   // the cue holds words k..j
            while (j + 1 < words.Count)
            {
                var next = words[j + 1];
                if (next.b - words[k].a > lay.MaxSeconds) break;
                if (next.a - words[j].b > 1.2) break;                    // a long pause: a new caption
                if (Wrap(words.Skip(k).Take(j - k + 2).Select(x => x.w).ToList(), lay, width, maxWidth) == null) break;
                // a sentence ends here and the caption is already well filled: break at it
                bool end = Regex.IsMatch(words[j].w, @"[.!?]$"), comma = Regex.IsMatch(words[j].w, @"[,;:]$");
                int len = words.Skip(k).Take(j - k + 1).Sum(x => x.w.Length + 1);
                double cap = lay.MaxChars * lay.MaxLines;
                if (end && (len > cap * 0.45 || words[j].b - words[k].a > 1.4)) break;
                if (lay.Phrases && comma && len > cap * 0.4) break;
                j++;
            }
            var lines = Wrap(words.Skip(k).Take(j - k + 1).Select(x => x.w).ToList(), lay, width, maxWidth) ?? new List<string> { string.Join(" ", words.Skip(k).Take(j - k + 1).Select(x => x.w)) };
            cues.Add(new Cue(words[k].a, words[j].b, string.Join("\n", lines)));
            k = j + 1;
        }
        return Tidy(cues, fps);
    }

    /// <summary>Lines for these words (balanced, each within the width and MaxChars), or null when they don't fit in MaxLines.</summary>
    static List<string>? Wrap(List<string> ws, CaptionLayout lay, Func<string, double> width, double maxWidth)
    {
        string all = string.Join(" ", ws);
        bool Fits(string line) => line.Length <= lay.MaxChars && width(line) <= maxWidth;
        if (Fits(all)) return new List<string> { all };
        if (lay.MaxLines < 2 || ws.Count < 2) return null;
        // two lines: the split with the most even lengths (a bit of preference for breaking after punctuation, and a shorter first line)
        List<string>? best = null; double score = double.MaxValue;
        for (int i = 1; i < ws.Count; i++)
        {
            string a = string.Join(" ", ws.Take(i)), b = string.Join(" ", ws.Skip(i));
            if (!Fits(a) || !Fits(b)) continue;
            double sc = Math.Abs(a.Length - b.Length) - (Regex.IsMatch(ws[i - 1], @"[,.;:!?]$") ? 6 : 0) + (a.Length > b.Length ? 1 : 0);
            if (sc < score) { score = sc; best = new List<string> { a, b }; }
        }
        return best;
    }

    /// <summary>Readable timing: at least 0.7 s (into the gap after it), short gaps closed (no flicker), on frames, never overlapping.</summary>
    public static List<Cue> Tidy(List<Cue> cues, double fps)
    {
        var r = new List<Cue>();
        for (int i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            double next = i + 1 < cues.Count ? cues[i + 1].Start : double.MaxValue;
            double end = Math.Max(c.End + 0.25, Math.Min(c.Start + 0.7, next));      // (a moment to finish reading)
            if (next - end < 0.4) end = Math.Max(end, next);                // close short gaps
            r.Add(c with { End = Math.Min(end, next) });
        }
        double F(double t) => Math.Round(Math.Max(0, t) * fps) / fps;
        var snapped = new List<Cue>();
        foreach (var c in r)
        {
            double a = F(c.Start), b = F(c.End);
            if (snapped.Count > 0 && a < snapped[^1].End) a = snapped[^1].End;
            if (b < a + 1 / fps) b = a + 1 / fps;
            snapped.Add(c with { Start = a, End = b });
        }
        return snapped;
    }

    /// <summary>A function measuring one line in the style's font at its size (logical px), as the title renderer draws it.</summary>
    public static Func<string, double> Measure(TextStyle s)
    {
        var tf = new Typeface(new FontFamily(s.Font), s.Italic ? FontStyles.Italic : FontStyles.Normal, FontWeight.FromOpenTypeWeight(Math.Clamp(s.Weight, 1, 999)), FontStretches.Normal);
        double tracking = s.Tracking / 1000.0 * s.Size;
        return line =>
        {
            var text = s.AllCaps ? line.ToUpperInvariant() : line;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, s.Size, Brushes.White, 1.0);
            return ft.WidthIncludingTrailingWhitespace + tracking * text.Length + (s.Box ? 2 * s.BoxPadding : 0) + (s.Stroke ? s.StrokeWidth : 0);
        };
    }

    // ---------------- caption titles on the timeline ----------------
    /// <summary>The caption look for a layout in this sequence: the title theme's caption style (bigger and bold, outlined, for short phrases),
    /// sized for vertical frames, and where it sits (Y, % from the centre).</summary>
    public static (TextStyle style, double y) Look(Editor ed, CaptionLayout lay)
    {
        var s = Themes.Get(ed.ThemeName).For(TextRole.Caption).Clone();
        bool vertical = Editor.W < Editor.H;
        if (lay.Phrases)
        {
            s.Size = vertical ? 46 : 64; s.Weight = 800; s.AllCaps = true; s.Box = false; s.Stroke = true; s.StrokeColor = "#FF000000"; s.StrokeWidth = vertical ? 5 : 6;
            s.Shadow = true; s.ShadowOpacity = 60; s.ShadowBlur = 6; s.ShadowDistance = 3; s.Fill = "#FFFFFFFF";
            return (s, vertical ? 18 : 24);
        }
        if (vertical) s.Size = Math.Min(s.Size, 34);
        return (s, vertical ? 24 : Themes.DefaultPosition(TextRole.Caption).y);
    }

    /// <summary>The highest video track above V1 that is free (other than captions) over every cue, or −1 (then the UI adds one on top).</summary>
    public static int FreeTrack(Editor ed, IReadOnlyList<Cue> cues)
    {
        if (cues.Count == 0) return 0;
        foreach (int tr in Enumerable.Range(0, Tracks.V1).Where(r => !ed.Locked(r)))   // (from the highest down, never V1; unlocked)
            if (!ed.Clips.Any(c => c.Track == tr && !IsCaption(c) && cues.Any(q => c.Start < q.End - 1e-4 && c.End > q.Start + 1e-4))) return tr;
        return -1;
    }

    /// <summary>Puts the cues on the timeline as caption titles on a track (the captions already on it go) — one undo step. Returns how many.</summary>
    public static int Place(Editor ed, IReadOnlyList<Cue> cues, int track, TextStyle style, double y)
    {
        ed.Edit(() =>
        {
            ed.Clips.RemoveAll(c => IsCaption(c) && c.Track == track);
            foreach (var q in cues)
                ed.Clips.Add(new Clip { Kind = ClipKind.Text, Role = TextRole.Caption, Track = track, Start = q.Start, Dur = Math.Max(1 / TimeUtil.Fps, q.End - q.Start),
                                        Text = q.Text, Style = style.Clone(), X = 0, Y = y });
        });
        return cues.Count;
    }

    public static List<Cue> CuesOf(Editor ed) => Clips(ed).Select(c => new Cue(c.Start, c.End, c.Text.Replace("\r", ""))).ToList();
    /// <summary>The cues within [t0, t1), timed from t0 (an export of In to Out).</summary>
    public static List<Cue> InRange(IEnumerable<Cue> cues, double t0, double t1) =>
        cues.Where(c => c.End > t0 + 1e-6 && c.Start < t1 - 1e-6).Select(c => new Cue(Math.Max(0, c.Start - t0), Math.Min(t1, c.End) - t0, c.Text)).ToList();

    // ---------------- SRT / WebVTT ----------------
    static string Stamp(double t, char sep)
    {
        long ms = (long)Math.Round(Math.Max(0, t) * 1000);
        return $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}{sep}{ms % 1000:000}";
    }
    public static string ToSrt(IEnumerable<Cue> cues)
    {
        var sb = new StringBuilder(); int n = 1;
        foreach (var c in cues) sb.Append(n++).Append("\r\n").Append(Stamp(c.Start, ',')).Append(" --> ").Append(Stamp(c.End, ',')).Append("\r\n").Append(c.Text.Replace("\n", "\r\n")).Append("\r\n\r\n");
        return sb.ToString();
    }
    public static string ToVtt(IEnumerable<Cue> cues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var c in cues) sb.Append(Stamp(c.Start, '.')).Append(" --> ").Append(Stamp(c.End, '.')).Append('\n').Append(c.Text).Append("\n\n");
        return sb.ToString();
    }
    /// <summary>Reads SRT or WebVTT (cue settings, styling tags and numbering ignored).</summary>
    public static List<Cue> Parse(string text)
    {
        var cues = new List<Cue>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var time = new Regex(@"(?:(\d+):)?(\d{1,2}):(\d{2})[.,](\d{1,3})\s*-->\s*(?:(\d+):)?(\d{1,2}):(\d{2})[.,](\d{1,3})");
        static double T(Match m, int g) => (m.Groups[g].Success && m.Groups[g].Value.Length > 0 ? int.Parse(m.Groups[g].Value) * 3600 : 0) + int.Parse(m.Groups[g + 1].Value) * 60
                                           + int.Parse(m.Groups[g + 2].Value) + int.Parse(m.Groups[g + 3].Value.PadRight(3, '0')) / 1000.0;
        for (int i = 0; i < lines.Length; i++)
        {
            var m = time.Match(lines[i]);
            if (!m.Success) continue;
            var body = new List<string>();
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++) body.Add(Regex.Replace(lines[i], @"<[^>]+>|\{\\[^}]+\}", "").Trim());
            var txt = string.Join("\n", body.Where(b => b.Length > 0));
            if (txt.Length > 0) cues.Add(new Cue(T(m, 1), T(m, 5), txt));
        }
        return cues.OrderBy(c => c.Start).ToList();
    }

    // ---------------- CEA-608 / SCC (embedded closed captions) ----------------
    /// <summary>MOV carries a c608 track; MXF carries SMPTE 436M ANC (608 + 708 in the CDP); MP4 is remuxed as QuickTime so c608 fits
    /// (the ISO mp4 muxer has no eia_608 tag).</summary>
    public static bool CcContainer(string container) => container is "mp4" or "mov" or "mxf";

    static byte Odd(int b)
    {
        b &= 0x7F;
        int n = 0; for (int x = b; x != 0; x >>= 1) n += x & 1;
        return (byte)((n & 1) == 0 ? b | 0x80 : b);
    }
    static string Word(int a, int b) => $"{Odd(a):X2}{Odd(b):X2}";
    static readonly int[] PacRow = { 0, 0x11, 0x11, 0x12, 0x12, 0x15, 0x15, 0x16, 0x16, 0x17, 0x17, 0x10, 0x13, 0x13, 0x14, 0x14 };
    static string Pac(int row) { row = Math.Clamp(row, 1, 15); return Word(PacRow[row], (row & 1) == 1 ? 0x40 : 0x60); }
    static string Tc(int frames) { frames = Math.Max(0, frames); return $"{frames / 108000:00}:{frames / 1800 % 60:00}:{frames / 30 % 60:00}:{frames % 30:00}"; }
    static byte CcByte(char ch) => ch switch
    {
        >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' => (byte)ch,
        ' ' or '!' or '"' or '#' or '$' or '%' or '&' or '\'' or '(' or ')' or '+' or ',' or '-' or '.' or '/'
            or ':' or ';' or '<' or '=' or '>' or '?' or '@' or '[' or ']' => (byte)ch,
        '\u2018' or '\u2019' => 0x27, '\u201C' or '\u201D' => 0x22, '\u2013' or '\u2014' => (byte)'-',
        'á' or 'Á' => 0x2A, 'é' or 'É' => 0x5C, 'í' or 'Í' => 0x5E, 'ó' or 'Ó' => 0x5F, 'ú' or 'Ú' => 0x60,
        'ñ' => 0x7E, 'Ñ' => 0x7D, 'ç' or 'Ç' => 0x7B,
        _ => (byte)'?'
    };

    /// <summary>Pop-on CEA-608 as Scenarist SCC (channel 1, white, bottom-aligned, 30 fps non-drop times). Extended Latin becomes the 608
    /// character or '?'.</summary>
    public static string ToScc(IEnumerable<Cue> cues)
    {
        var at = new SortedDictionary<int, List<string>>();
        void Add(int f, IEnumerable<string> w)
        {
            f = Math.Max(0, f);
            if (!at.TryGetValue(f, out var L)) at[f] = L = new();
            L.AddRange(w);
        }
        foreach (var c in cues.OrderBy(x => x.Start))
        {
            var lines = (c.Text ?? "").Replace("\r", "").Split('\n')
                .Select(l => l.Trim()).Where(l => l.Length > 0).Take(4)
                .Select(l => l.Length > 32 ? l[..32] : l).ToList();
            if (lines.Count == 0) continue;
            int start = (int)Math.Round(Math.Max(0, c.Start) * 30), end = Math.Max(start + 2, (int)Math.Round(Math.Max(0, c.End) * 30));
            var words = new List<string> { Word(0x14, 0x20), Word(0x14, 0x2E) };   // RCL, ENM
            int row0 = 16 - lines.Count;
            foreach (var line in lines)
            {
                words.Add(Pac(row0++));
                var bytes = line.Select(CcByte).ToList();
                if ((bytes.Count & 1) != 0) bytes.Add((byte)' ');
                for (int k = 0; k < bytes.Count; k += 2) words.Add(Word(bytes[k], bytes[k + 1]));
            }
            words.Add(Word(0x14, 0x2F));                                          // EOC
            for (int i = 0, f = start; i < words.Count; i += 16, f++) Add(f, words.Skip(i).Take(16));
            Add(end, new[] { Word(0x14, 0x2C) });                                 // EDM
        }
        var sb = new StringBuilder("Scenarist_SCC V1.0\r\n\r\n");
        foreach (var (f, w) in at) sb.Append(Tc(f)).Append('\t').Append(string.Join(' ', w)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Printable 608 characters from an SCC file (control / PAC words skipped) — for tests.</summary>
    public static string SccText(string scc)
    {
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(scc, @"\b([0-9A-Fa-f]{4})\b"))
        {
            int w = Convert.ToInt32(m.Groups[1].Value, 16), a = (w >> 8) & 0x7F, b = w & 0x7F;
            if (a is >= 0x10 and <= 0x17) continue;
            if (a >= 0x20) sb.Append((char)a);
            if (b >= 0x20) sb.Append((char)b);
        }
        return sb.ToString();
    }

    /// <summary>After a successful picture export: mux CEA-608 onto the file when the settings ask. Null = nothing to do or done.</summary>
    public static string? EmbedIfNeeded(string ffmpeg, Editor ed, string output, ExportSettings? o)
    {
        if (o is not { EmbedCc: true } || o.AudioOnly || !CcContainer(o.Container) || !File.Exists(output)) return null;
        var (t0, t1) = Exporter.Range(ed, o);
        var cues = InRange(CuesOf(ed), t0, t1);
        return cues.Count == 0 ? null : MuxCc(ffmpeg, output, cues, o.Container);
    }

    /// <summary>Remux <paramref name="video"/> in place with a CEA-608 stream from <paramref name="cues"/>.</summary>
    public static string? MuxCc(string ffmpeg, string video, IEnumerable<Cue> cues, string container)
    {
        if (!CcContainer(container)) return "This format cannot carry CEA-608 captions (use MP4, MOV or MXF).";
        var scc = Path.Combine(Path.GetTempPath(), $"cutpro_cc_{Guid.NewGuid().ToString("N")[..8]}.scc");
        var tmp = video + ".ccmux" + Path.GetExtension(video);
        try
        {
            File.WriteAllText(scc, ToScc(cues), new UTF8Encoding(false));
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-y", "-v", "error", "-i", video, "-i", scc, "-map", "0", "-map", "1:s:0", "-c", "copy", "-c:s", "copy" })
                psi.ArgumentList.Add(a);
            if (container is "mp4" or "mov") { psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart"); }
            if (container == "mp4") { psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("mov"); }
            psi.ArgumentList.Add(tmp);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(120000)) { try { p.Kill(true); } catch { } return "Embedding closed captions timed out."; }
            if (p.ExitCode != 0) return string.IsNullOrWhiteSpace(err) ? "Embedding closed captions failed." : err.Trim();
            File.Copy(tmp, video, true);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally { try { File.Delete(scc); } catch { } try { File.Delete(tmp); } catch { } }
    }
}
