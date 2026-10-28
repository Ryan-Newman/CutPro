namespace CutPro;

/// <summary>A word said in a media file: its text and SOURCE seconds (<see cref="MediaItem.Words"/>).</summary>
public sealed record Word(string W, double A, double B);

/// <summary>Text-based editing: the sequence's transcript is the transcribed media's words mapped through every sound clip that plays them
/// (<see cref="TimeMap.Out"/>), so it follows cuts, moves and trims without being transcribed again. Deleting words removes their time
/// from every unlocked track (<see cref="Editor.RemoveRanges"/>).</summary>
public static class Transcript
{
    /// <summary>A word on the timeline (timeline seconds), the clip that plays it.</summary>
    public sealed record TWord(string W, double T0, double T1, string ClipId, string MediaId);

    /// <summary>The words of the sequence in time order: every sound clip (on a track that plays) whose media is transcribed — plain forward
    /// clips (reversed, ramped and held clips are left out).</summary>
    public static List<TWord> Of(Editor ed)
    {
        var res = new List<TWord>();
        foreach (var c in ed.Clips.Where(c => Mixer.HasSound(c) && c.Track < ed.TrackOff.Length && !ed.TrackOff[c.Track] && !c.Reverse && !c.Hold && !TimeMap.Ramped(c)))
        {
            if (ed.MediaOf(c) is not { Words: { Count: > 0 } words } m) continue;
            var (lo, hi) = TimeMap.Span(c);
            foreach (var w in words)
            {
                double mid = (w.A + w.B) / 2;
                if (mid < lo || mid >= hi) continue;                           // (a word belongs to the clip that plays its middle)
                double t0 = Math.Max(c.Start, TimeMap.Out(c, w.A)), t1 = Math.Min(c.End, TimeMap.Out(c, w.B));
                if (t1 - t0 > 0.01) res.Add(new TWord(w.W, t0, t1, c.Id, m.Id));
            }
        }
        res.Sort((a, b) => a.T0 != b.T0 ? a.T0.CompareTo(b.T0) : string.CompareOrdinal(a.ClipId, b.ClipId));
        // the same media on two tracks at once (a dual-mono split): one copy of each word
        return res.Where((w, i) => i == 0 || !(Math.Abs(res[i - 1].T0 - w.T0) < 0.02 && res[i - 1].W == w.W && res[i - 1].MediaId == w.MediaId)).ToList();
    }

    static readonly HashSet<string> fillers = new(StringComparer.OrdinalIgnoreCase) { "um", "umm", "uh", "uhh", "erm", "er", "ah", "hmm", "mm", "eh", "ähm", "äh", "euh", "eh" };
    public static bool IsFiller(string w) => fillers.Contains(w.Trim().Trim(',', '.', '!', '?', '…', '-', '"').ToLowerInvariant());

    /// <summary>The time to remove for chosen words (indexes into all): each run of neighbours from a little before its first word to a little
    /// after its last, never into the words kept on either side.</summary>
    public static List<(double A, double B)> Ranges(IReadOnlyList<TWord> all, IEnumerable<int> chosen, double pad = 0.04)
    {
        var set = chosen.Where(i => i >= 0 && i < all.Count).Distinct().OrderBy(i => i).ToList();
        var res = new List<(double, double)>();
        for (int k = 0; k < set.Count;)
        {
            int i = set[k], j = i;
            while (k + 1 < set.Count && set[k + 1] == j + 1) { k++; j = set[k]; }
            k++;
            double prevEnd = i > 0 ? all[i - 1].T1 : 0, nextStart = j + 1 < all.Count ? all[j + 1].T0 : double.MaxValue;
            double a = Math.Max(prevEnd, all[i].T0 - pad), b = Math.Min(nextStart, all[j].T1 + pad);
            if (b > a) res.Add((a, b));
        }
        return res;
    }

    /// <summary>Pauses between words longer than `longer` seconds, shortened to `keep` (half of it stays on each side).</summary>
    public static List<(double A, double B)> Pauses(IReadOnlyList<TWord> all, double longer, double keep)
    {
        var res = new List<(double, double)>();
        for (int i = 0; i + 1 < all.Count; i++)
        {
            double a = all[i].T1, b = all[i + 1].T0;
            if (b - a > longer) res.Add((a + keep / 2, b - keep / 2));
        }
        return res;
    }

    /// <summary>Transcribes a media file's sound (Whisper with timestamps, words refined by the voice) — this PC only.</summary>
    public static List<Word> Transcribe(string ff, MediaItem m, string? language, IProgress<double>? progress, CancellationToken ct)
    {
        var pcm = Whisper.Pcm16k(ff, m.Path);
        ct.ThrowIfCancellationRequested();
        using var w = new Whisper();
        var res = w.TranscribeTimed(pcm, language, progress, ct);
        return Captioning.Words(res.Segments, new Captioning.Voice(pcm)).Select(x => new Word(x.w, Math.Round(x.a, 3), Math.Round(x.b, 3))).ToList();
    }
}
