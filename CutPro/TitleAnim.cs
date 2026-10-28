namespace CutPro;

public enum TextIn { None, Typewriter, FadeChars, FadeWords, RiseWords, PopWords, FadeLines }
public enum TextRoll { None, Roll, Crawl }
public enum TextUnit { Char, Word, Line }

/// <summary>A title's text animation (Premiere's animated text presets / Essential Graphics roll): each character, word or line comes in
/// (typewriter, fade, rise, pop) one after another at the start, goes out the same way at the end; or the whole text rolls up the frame
/// (credits) or crawls across it over the clip's length. The title visual evaluates <see cref="TitleAnim.State"/> per unit from the time since
/// the clip's start — the live preview and the export's per-frame title render (<see cref="TitleVisual"/>) run the same code.</summary>
public sealed class TextAnim
{
    public TextIn In { get; set; }
    public TextIn Out { get; set; }
    public double InDur { get; set; } = 1;                                  // seconds the whole text takes to come in
    public double OutDur { get; set; } = 0.6;
    public TextRoll Roll { get; set; }
}

public static class TitleAnim
{
    public static bool Active(TextAnim? a) => a != null && (a.In != TextIn.None || a.Out != TextIn.None || a.Roll != TextRoll.None);
    /// <summary>Animations that need the text split into units.</summary>
    public static bool Units(TextAnim? a) => a != null && (a.In != TextIn.None || a.Out != TextIn.None);
    /// <summary>The unit the in animation (or, without one, the out animation) works on — what a popping letter scales about.</summary>
    public static TextUnit UnitOf(TextAnim a) => Kind(a.In != TextIn.None ? a.In : a.Out);
    public static readonly (TextIn v, string name)[] Names =
    {
        (TextIn.None, "None"), (TextIn.Typewriter, "Typewriter"), (TextIn.FadeChars, "Fade in by letter"), (TextIn.FadeWords, "Fade in by word"),
        (TextIn.RiseWords, "Rise by word"), (TextIn.PopWords, "Pop by word"), (TextIn.FadeLines, "Fade in by line"),
    };

    /// <summary>One unit's look at time <paramref name="local"/> (seconds since the clip's start) of a clip <paramref name="dur"/> long:
    /// opacity 0…1, vertical offset in em, scale about the unit's centre.</summary>
    public static (double opacity, double dy, double scale) State(TextAnim a, (int c, int w, int l) idx, (int c, int w, int l) n, double local, double dur)
    {
        double op = 1, dy = 0, sc = 1;
        (int i, int count) Of(TextIn s) => Kind(s) switch { TextUnit.Char => (idx.c, n.c), TextUnit.Line => (idx.l, n.l), _ => (idx.w, n.w) };
        if (a.In != TextIn.None) { var (i, k) = Of(a.In); Apply(a.In, Progress(a.In, i, k, local, Math.Min(a.InDur, dur)), ref op, ref dy, ref sc); }
        if (a.Out != TextIn.None)
        {
            var (i, k) = Of(a.Out);
            double od = Math.Min(a.OutDur, dur);
            double p = local < dur - od - 1e-9 ? 1 : 1 - Progress(a.Out, i, k, local - (dur - od), od, leaving: true);
            Apply(a.Out, p, ref op, ref dy, ref sc);
        }
        return (op, dy, sc);
    }
    static TextUnit Kind(TextIn s) => s switch { TextIn.Typewriter or TextIn.FadeChars => TextUnit.Char, TextIn.FadeLines => TextUnit.Line, _ => TextUnit.Word };

    /// <summary>How far unit i of n is through its own part of a window of length d at time tau (0 = not started, 1 = done). Units start one
    /// after another; a fade of each takes about three units' time (typewriter: instant).</summary>
    static double Progress(TextIn s, int i, int n, double tau, double d, bool leaving = false)
    {
        if (d <= 1e-9) return 1;
        n = Math.Max(1, n);
        if (s == TextIn.Typewriter) return tau >= (leaving ? i + 1 : i) * d / n - 1e-9 ? 1 : 0;
        double f = n == 1 ? d : Math.Min(d * 0.6, Math.Max(d * 3 / n, d * 0.25));
        double start = n == 1 ? 0 : i * (d - f) / (n - 1);
        return Math.Clamp((tau - start) / f, 0, 1);
    }

    static void Apply(TextIn s, double p, ref double op, ref double dy, ref double sc)
    {
        switch (s)
        {
            case TextIn.Typewriter: op *= p >= 1 ? 1 : 0; break;
            case TextIn.RiseWords:
            {
                double e = 1 - Math.Pow(1 - p, 3);                               // (ease out)
                op *= e; dy += (1 - e) * 0.6;
                break;
            }
            case TextIn.PopWords:
            {
                const double c1 = 1.70158, c3 = c1 + 1;                         // (ease out, overshooting a little)
                double e = p <= 0 ? 0 : 1 + c3 * Math.Pow(p - 1, 3) + c1 * Math.Pow(p - 1, 2);
                op *= Math.Min(1, p * 2); sc *= 0.4 + 0.6 * e;
                break;
            }
            default: op *= p * p * (3 - 2 * p); break;                           // (fades: smooth)
        }
    }

    /// <summary>Roll (credits, bottom to top) / crawl (right to left): u = 0 … 1 over the clip — the text block's first edge enters at the frame
    /// edge and its last edge leaves at the opposite one.</summary>
    public static double RollU(double local, double dur) => dur <= 1e-9 ? 0 : Math.Clamp(local / dur, 0, 1);
}
