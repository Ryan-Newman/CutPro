namespace CutPro;

/// <summary>
/// Which source time a clip shows at a timeline time: speed (constant, or keyframed = time remapping), reverse, frame hold.
/// <see cref="Clip.In"/> is always the source time shown at the clip's start, so edits stay simple — a trim, razor cut or
/// transition handle just asks <see cref="Src"/> for the new In. Clips at 100 % forward keep the plain 1:1 map.
/// Remapped video plays from a rendered file (<see cref="Remap"/>) whose frame k is the native frame <see cref="NativeIndex"/>
/// picks for output frame k; remapped sound is rendered once (<see cref="AudioCache.EnsureDerived"/>). Preview and export read
/// the same files, so they match exactly.
/// </summary>
public static class TimeMap
{
    public const double MinSpeed = 1, MaxSpeed = 10000;          // %
    public static double Ratio(Clip c) => Math.Clamp(c.Speed, MinSpeed, MaxSpeed) / 100;
    static int Dir(Clip c) => c.Reverse ? -1 : 1;

    /// <summary>Speed keyframes (time remapping) — video clips only; a held clip ignores them.</summary>
    public static bool Ramped(Clip c) => c.Kind == ClipKind.Video && !c.Hold && Anim.HasKeys(c, AnimProp.Speed);
    /// <summary>True when the clip doesn't play its source 1:1 forward.</summary>
    public static bool IsRemapped(Clip c) =>
        c.Kind is ClipKind.Video or ClipKind.Audio && (c.Hold || c.Reverse || Math.Abs(c.Speed - 100) > 1e-9 || Ramped(c));
    /// <summary>Sound that has to be rendered (speed / reverse); a held clip is silent.</summary>
    public static bool SoundRemapped(Clip c) => !c.Hold && (c.Reverse || Math.Abs(c.Speed - 100) > 1e-9);

    /// <summary>Source time shown at timeline time t.</summary>
    public static double Src(Clip c, double t) => SrcAt(c, c.Start, c.In, t);

    /// <summary><see cref="Src"/> of the clip as if it started at <paramref name="start"/> with in point <paramref name="inPt"/> (trim drags
    /// evaluate the map of the clip as it was when the drag began).</summary>
    public static double SrcAt(Clip c, double start, double inPt, double t)
    {
        double tau = t - start;
        if (!IsRemapped(c)) return inPt + tau;
        if (c.Hold) return inPt;
        if (Ramped(c)) return Ramp.Get(c, inPt).Src(tau);
        return inPt + Dir(c) * Ratio(c) * tau;
    }

    /// <summary>Timeline time at which the clip shows source time src (keyframe diamonds, mask key jumps). Held clips: their start.</summary>
    public static double Out(Clip c, double src)
    {
        if (!IsRemapped(c)) return c.Start + (src - c.In);
        if (c.Hold) return c.Start;
        if (Ramped(c)) return c.Start + Ramp.Get(c, c.In).Tau(src);
        return c.Start + (src - c.In) / (Dir(c) * Ratio(c));
    }

    /// <summary>Source interval [lo, hi) the clip's frames come from (hi exclusive; reverse: mirrored, so a forward clip and its reverse
    /// cover the same media). Mask frame ranges, media-limit checks and rendered sound use it.</summary>
    public static (double lo, double hi) Span(Clip c) => SpanAt(c, c.Start, c.In, c.Dur);
    public static (double lo, double hi) SpanAt(Clip c, double start, double inPt, double dur)
    {
        double f = 1 / TimeUtil.Fps;
        if (!IsRemapped(c)) return (inPt, inPt + dur);
        if (c.Hold) return (inPt, inPt + f);
        if (!Ramped(c))
        {
            double r = Ratio(c);
            return c.Reverse ? (inPt + r * f - r * dur, inPt + r * f) : (inPt, inPt + r * dur);
        }
        double a = SrcAt(c, start, inPt, start), b = SrcAt(c, start, inPt, start + Math.Max(0, dur - f)), step = Math.Abs(SrcAt(c, start, inPt, start + dur) - b);
        return (Math.Min(a, b), Math.Max(a, b) + Math.Max(step, 1e-6));
    }

    /// <summary>The native frame of the media shown at timeline time t (remapped clips; past the media's ends: its first / last frame).</summary>
    public static long NativeIndex(Clip c, MediaItem m, double t)
    {
        double fps = NativeFps(m);
        long last = Math.Max(0, (long)Math.Ceiling(m.Duration * fps - 1e-3) - 1);
        return Math.Clamp((long)Math.Floor(Src(c, t) * fps + 1e-3), 0, last);
    }
    public static double NativeFps(MediaItem m) => m.Fps > 1 ? m.Fps : TimeUtil.Fps;

    /// <summary>Output seconds of media available before the clip's start / after its end (audio crossfades need real sound there).</summary>
    public static double HeadRoom(Clip c, double mediaDur)
    {
        if (!IsRemapped(c)) return Math.Max(0, c.In);
        if (c.Hold || Ramped(c)) return 0;
        var (lo, hi) = Span(c);
        return Math.Max(0, c.Reverse ? mediaDur - hi : lo) / Ratio(c);
    }
    public static double TailRoom(Clip c, double mediaDur)
    {
        if (!IsRemapped(c)) return Math.Max(0, mediaDur - (c.In + c.Dur));
        if (c.Hold || Ramped(c)) return 0;
        var (lo, hi) = Span(c);
        return Math.Max(0, c.Reverse ? lo : mediaDur - hi) / Ratio(c);
    }

    /// <summary>Clip label suffix like Premiere's: "[200%]", "[-100%]" (reversed), "[hold]", "[remap]".</summary>
    public static string Label(Clip c) =>
        !IsRemapped(c) ? "" : c.Hold ? "  [hold]" : Ramped(c) ? "  [time remap]" : $"  [{(c.Reverse ? "-" : "")}{c.Speed:0.##}%]";

    /// <summary>A reversed clip's in point when it is turned forward (or the reverse): the same frames, played the other way.</summary>
    public static double FlipIn(Clip c)
    {
        var (lo, hi) = Span(c);
        return c.Reverse ? lo : hi - Ratio(c) / TimeUtil.Fps;
    }

    // ---------------- time remapping: speed keyframes (keyed by SOURCE time, like every other property) ----------------
    /// <summary>Output time ↔ source time table of a ramped clip: ds/dτ = ±speed(s), integrated in small output steps (midpoint rule),
    /// from <see cref="Margin"/> s before the clip start (transition handles) to Margin s after its end.</summary>
    sealed class Ramp
    {
        const double Margin = 10;
        readonly double[] s;      // source time at τ = -Margin + i * h
        readonly double h;
        Ramp(double[] s, double h) { this.s = s; this.h = h; }

        public double Src(double tau)
        {
            double x = (tau + Margin) / h;
            if (x <= 0) return s[0] + (s[1] - s[0]) * x;
            int i = (int)x;
            if (i >= s.Length - 1) return s[^1] + (s[^1] - s[^2]) * (x - (s.Length - 1));
            return s[i] + (s[i + 1] - s[i]) * (x - i);
        }

        public double Tau(double src)
        {
            bool up = s[^1] >= s[0];
            int lo = 0, hi = s.Length - 1;
            if (up ? src <= s[0] : src >= s[0]) return -Margin;
            if (up ? src >= s[^1] : src <= s[^1]) return -Margin + (s.Length - 1) * h;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (up ? s[mid] <= src : s[mid] >= src) lo = mid; else hi = mid; }
            double d = s[hi] - s[lo], f = Math.Abs(d) < 1e-12 ? 0 : (src - s[lo]) / d;
            return -Margin + (lo + f) * h;
        }

        static readonly Dictionary<(string, long), Ramp> cache = new();
        public static Ramp Get(Clip c, double inPt)
        {
            var keys = Anim.Track(c, AnimProp.Speed)!.Keys;
            var hc = new HashCode();
            hc.Add(inPt); hc.Add(c.Dur); hc.Add(c.Reverse); hc.Add(TimeUtil.Fps);
            foreach (var k in keys) { hc.Add(k.T); hc.Add(k.V); hc.Add(k.In); hc.Add(k.Out); hc.Add(k.InSlope); hc.Add(k.OutSlope); hc.Add(k.InInfl); hc.Add(k.OutInfl); }
            var key = (c.Id, (long)hc.ToHashCode());
            lock (cache)
            {
                if (cache.TryGetValue(key, out var r)) return r;
                if (cache.Count > 64) cache.Clear();
                cache[key] = r = Build(keys, inPt, c.Dur, c.Reverse ? -1 : 1);
                return r;
            }
        }

        static Ramp Build(IReadOnlyList<Keyframe> keys, double inPt, double dur, int dir)
        {
            double h = 1 / (TimeUtil.Fps * 8);
            int before = (int)Math.Ceiling(Margin / h), after = (int)Math.Ceiling((Math.Max(0, dur) + Margin) / h);
            var s = new double[before + after + 1];
            double V(double src) => Math.Clamp(Curve.Eval(keys, src), MinSpeed, MaxSpeed) / 100;
            s[before] = inPt;
            for (int i = before; i < s.Length - 1; i++) { double mid = s[i] + dir * V(s[i]) * h / 2; s[i + 1] = s[i] + dir * V(mid) * h; }
            for (int i = before; i > 0; i--) { double mid = s[i] - dir * V(s[i]) * h / 2; s[i - 1] = s[i] - dir * V(mid) * h; }
            return new Ramp(s, h);
        }
    }
}
