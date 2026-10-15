using System.Text.Json;

namespace CutPro;

/// <summary>One audio effect on a clip (Effect Controls → Audio effects). Parameters by name; missing ones take the kind's default.</summary>
public sealed class AudioEffect
{
    public string Kind { get; set; } = "EQ";
    public bool On { get; set; } = true;
    public Dictionary<string, double> P { get; set; } = new();
    public AudioEffect Clone() => new() { Kind = Kind, On = On, P = new(P) };
}

/// <summary>
/// Clip audio effects: parametric EQ, compressor, noise gate, de-esser, hum removal. One C# implementation, per sample, on the clip's
/// sound before its volume / fades / pan: the playback mixer runs it live (<see cref="FxState"/>, state per clip, warmed up after a jump),
/// and the export renders the clip's processed sound to a file with the same code from the clip's first sample (<see cref="EnsureFile"/>)
/// — the exporter then reads that file instead of the source. Output is rounded to 16-bit at the point the source is read, so the mixer
/// rendering from before the clip and the file are identical sample for sample (--audiotest).
/// </summary>
public static class AudioDsp
{
    public sealed record Param(string Key, string Label, double Min, double Max, double Step, double Def);
    public sealed record KindInfo(string Kind, string Name, string Help, Param[] Ps);
    public static readonly KindInfo[] Kinds =
    {
        new("EQ", "Parametric EQ", "Low cut, low / mid / high bands, high cut", new Param[]
        {
            new("lowcut", "Low cut (Hz)", 0, 500, 5, 0), new("low", "Low (dB)", -12, 12, 0.5, 0), new("lowf", "Low freq (Hz)", 40, 400, 5, 120),
            new("mid", "Mid (dB)", -12, 12, 0.5, 0), new("midf", "Mid freq (Hz)", 200, 8000, 10, 1000), new("midq", "Mid width (Q)", 0.3, 5, 0.1, 1),
            new("high", "High (dB)", -12, 12, 0.5, 0), new("highf", "High freq (Hz)", 2000, 16000, 100, 8000), new("highcut", "High cut (Hz)", 0, 20000, 100, 0),
        }),
        new("Compressor", "Compressor", "Evens out loud and quiet parts", new Param[]
        {
            new("thr", "Threshold (dB)", -60, 0, 0.5, -20), new("ratio", "Ratio", 1, 20, 0.1, 3), new("att", "Attack (ms)", 0.1, 100, 0.1, 10),
            new("rel", "Release (ms)", 10, 1000, 5, 150), new("makeup", "Makeup (dB)", 0, 24, 0.5, 3),
        }),
        new("Gate", "Noise gate", "Turns the sound down between words", new Param[]
        {
            new("thr", "Threshold (dB)", -80, 0, 0.5, -45), new("range", "Range (dB)", -80, 0, 1, -30), new("att", "Attack (ms)", 0.1, 50, 0.1, 2),
            new("hold", "Hold (ms)", 0, 500, 5, 50), new("rel", "Release (ms)", 5, 1000, 5, 120),
        }),
        new("DeEsser", "De-esser", "Softens harsh S and T sounds", new Param[]
        {
            new("freq", "Frequency (Hz)", 3000, 10000, 100, 6000), new("thr", "Threshold (dB)", -50, 0, 0.5, -28), new("amount", "Reduction (dB)", 0, 24, 0.5, 8),
        }),
        new("DeHum", "Hum removal", "Removes mains hum (50 / 60 Hz and its harmonics)", new Param[]
        {
            new("freq", "Mains (Hz)", 50, 60, 10, 50), new("harm", "Harmonics", 1, 8, 1, 4),
        }),
        new("Reverb", "Reverb", "Room sound: on a track the tail rings on after the clips; on a clip it ends with the clip", new Param[]
        {
            new("size", "Room size %", 0, 100, 1, 60), new("damp", "Damping %", 0, 100, 1, 40), new("pre", "Pre-delay (ms)", 0, 200, 1, 20),
            new("lowcut", "Low cut (Hz)", 0, 500, 5, 150), new("width", "Width %", 0, 100, 1, 100), new("wet", "Wet %", 0, 100, 1, 25), new("dry", "Dry %", 0, 100, 1, 100),
        }),
    };
    public static KindInfo Info(string kind) => Kinds.FirstOrDefault(k => k.Kind == kind) ?? Kinds[0];
    public static double Get(AudioEffect e, string key) => e.P.TryGetValue(key, out var v) ? v : Info(e.Kind).Ps.First(p => p.Key == key).Def;
    public static AudioEffect New(string kind, params (string k, double v)[] set)
    {
        var e = new AudioEffect { Kind = kind };
        foreach (var p in Info(kind).Ps) e.P[p.Key] = p.Def;
        foreach (var (k, v) in set) e.P[k] = v;
        return e;
    }
    /// <summary>Voice clean-up: rumble cut, a little presence, gentle gate, compressor, de-esser.</summary>
    public static List<AudioEffect> VoicePreset() => new()
    {
        New("EQ", ("lowcut", 80), ("mid", 2), ("midf", 3000), ("midq", 0.8), ("high", 1.5)),
        New("Gate", ("thr", -50), ("range", -18)),
        New("Compressor", ("thr", -22), ("ratio", 3), ("att", 8), ("rel", 160), ("makeup", 4)),
        New("DeEsser", ("thr", -30), ("amount", 6)),
    };

    public static bool Active(Clip c) => c.AudioFx?.Any(e => e.On) == true;
    public static string Sig(IEnumerable<AudioEffect>? fx) => fx == null ? "" : JsonSerializer.Serialize(fx.Where(e => e.On));

    static readonly Dictionary<string, IReadOnlyList<AudioEffect>> snaps = new();
    /// <summary>A frozen copy of a clip's effects for the mixer thread (the inspector edits the clip's own list), one per settings key so
    /// the mixer's source list stays equal from frame to frame.</summary>
    public static IReadOnlyList<AudioEffect> Snapshot(string key, List<AudioEffect> fx)
    {
        lock (snaps)
        {
            if (snaps.TryGetValue(key, out var s)) return s;
            if (snaps.Count > 256) snaps.Clear();
            return snaps[key] = fx.Where(e => e.On).Select(e => e.Clone()).ToList();
        }
    }

    /// <summary>The processing chain for a list of effects (only the ones switched on), fresh state.</summary>
    public static Chain Build(IEnumerable<AudioEffect> fx) => new(fx.Where(e => e.On).SelectMany(Procs).ToArray());

    // ---------------- processors (per sample, stereo, ±1 = full scale) ----------------
    public interface IProc { void Run(ref double l, ref double r); }
    const double Fs = AudioCache.Rate;
    static double Coef(double ms) => Math.Exp(-1 / (Math.Max(0.01, ms) / 1000 * Fs));
    static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-9));
    static double Lin(double db) => Math.Pow(10, db / 20);

    /// <summary>RBJ cookbook biquad, transposed direct form II, one state per channel.</summary>
    sealed class Biquad : IProc
    {
        readonly double b0, b1, b2, a1, a2; double l1, l2, r1, r2;
        public Biquad(string type, double f, double q, double gainDb = 0)
        {
            f = Math.Clamp(f, 10, Fs * 0.45);
            double w = 2 * Math.PI * f / Fs, cs = Math.Cos(w), sn = Math.Sin(w), al = sn / (2 * q), A = Math.Pow(10, gainDb / 40), sa = 2 * Math.Sqrt(A) * al;
            double B0, B1, B2, A0, A1, A2;
            switch (type)
            {
                case "lp": B0 = (1 - cs) / 2; B1 = 1 - cs; B2 = B0; A0 = 1 + al; A1 = -2 * cs; A2 = 1 - al; break;
                case "hp": B0 = (1 + cs) / 2; B1 = -(1 + cs); B2 = B0; A0 = 1 + al; A1 = -2 * cs; A2 = 1 - al; break;
                case "peak": B0 = 1 + al * A; B1 = -2 * cs; B2 = 1 - al * A; A0 = 1 + al / A; A1 = -2 * cs; A2 = 1 - al / A; break;
                case "notch": B0 = 1; B1 = -2 * cs; B2 = 1; A0 = 1 + al; A1 = -2 * cs; A2 = 1 - al; break;
                case "bp": B0 = al; B1 = 0; B2 = -al; A0 = 1 + al; A1 = -2 * cs; A2 = 1 - al; break;   // (0 dB at the centre)
                case "lowshelf":
                    B0 = A * ((A + 1) - (A - 1) * cs + sa); B1 = 2 * A * ((A - 1) - (A + 1) * cs); B2 = A * ((A + 1) - (A - 1) * cs - sa);
                    A0 = (A + 1) + (A - 1) * cs + sa; A1 = -2 * ((A - 1) + (A + 1) * cs); A2 = (A + 1) + (A - 1) * cs - sa; break;
                default: // highshelf
                    B0 = A * ((A + 1) + (A - 1) * cs + sa); B1 = -2 * A * ((A - 1) + (A + 1) * cs); B2 = A * ((A + 1) + (A - 1) * cs - sa);
                    A0 = (A + 1) - (A - 1) * cs + sa; A1 = 2 * ((A - 1) - (A + 1) * cs); A2 = (A + 1) - (A - 1) * cs - sa; break;
            }
            b0 = B0 / A0; b1 = B1 / A0; b2 = B2 / A0; a1 = A1 / A0; a2 = A2 / A0;
        }
        public double Step(double x, ref double z1, ref double z2) { double y = b0 * x + z1; z1 = b1 * x - a1 * y + z2; z2 = b2 * x - a2 * y; return y; }
        public void Run(ref double l, ref double r) { l = Step(l, ref l1, ref l2); r = Step(r, ref r1, ref r2); }
    }

    /// <summary>Feed-forward compressor: stereo-linked peak level, 6 dB soft knee, gain reduction smoothed in dB (attack / release), makeup.</summary>
    sealed class Compressor : IProc
    {
        readonly double thr, slope, att, rel, makeup; const double Knee = 6; double gr;
        public Compressor(AudioEffect e) { thr = Get(e, "thr"); slope = 1 - 1 / Math.Max(1, Get(e, "ratio")); att = Coef(Get(e, "att")); rel = Coef(Get(e, "rel")); makeup = Get(e, "makeup"); }
        public void Run(ref double l, ref double r)
        {
            double over = Db(Math.Max(Math.Abs(l), Math.Abs(r))) - thr, target;
            if (2 * over < -Knee) target = 0;
            else if (2 * Math.Abs(over) <= Knee) target = -slope * (over + Knee / 2) * (over + Knee / 2) / (2 * Knee);
            else target = -slope * over;
            gr = target < gr ? att * gr + (1 - att) * target : rel * gr + (1 - rel) * target;
            double g = Lin(gr + makeup); l *= g; r *= g;
        }
    }

    /// <summary>Noise gate: stereo-linked peak envelope (20 ms fall); open above the threshold, held open, closes to the range.</summary>
    sealed class Gate : IProc
    {
        readonly double thr, range, att, rel, envFall = Coef(20); readonly int hold; double env, gdb; int held;
        public Gate(AudioEffect e) { thr = Get(e, "thr"); range = Math.Min(0, Get(e, "range")); att = Coef(Get(e, "att")); rel = Coef(Get(e, "rel")); hold = (int)(Get(e, "hold") / 1000 * Fs); gdb = range; }
        public void Run(ref double l, ref double r)
        {
            double lv = Math.Max(Math.Abs(l), Math.Abs(r));
            env = lv > env ? lv : env * envFall;
            double target;
            if (Db(env) > thr) { held = hold; target = 0; }
            else if (held > 0) { held--; target = 0; }
            else target = range;
            gdb = target > gdb ? att * gdb + (1 - att) * target : rel * gdb + (1 - rel) * target;
            double g = Lin(gdb); l *= g; r *= g;
        }
    }

    /// <summary>De-esser as a dynamic band: a band-pass copy around the frequency (0 dB and no phase shift at its centre) is turned down
    /// while its level is over the threshold, up to the amount — y = x + (g − 1)·band. (A high-pass split subtracted a phase-shifted copy
    /// and hardly reduced anything.)</summary>
    sealed class DeEsser : IProc
    {
        readonly Biquad bp; readonly double thr, amount, att = Coef(0.5), rel = Coef(50); double l1, l2, r1, r2, env;
        public DeEsser(AudioEffect e) { bp = new Biquad("bp", Get(e, "freq"), 0.7); thr = Get(e, "thr"); amount = Math.Max(0, Get(e, "amount")); }
        public void Run(ref double l, ref double r)
        {
            double bl = bp.Step(l, ref l1, ref l2), br = bp.Step(r, ref r1, ref r2);
            double det = Math.Max(Math.Abs(bl), Math.Abs(br));
            env = det > env ? att * env + (1 - att) * det : rel * env + (1 - rel) * det;
            double over = Db(env) - thr, g = over > 0 ? Lin(-Math.Min(amount, over)) : 1;
            l += (g - 1) * bl; r += (g - 1) * br;
        }
    }

    static IEnumerable<IProc> Procs(AudioEffect e)
    {
        switch (e.Kind)
        {
            case "EQ":
                if (Get(e, "lowcut") >= 10) yield return new Biquad("hp", Get(e, "lowcut"), 0.7071);
                if (Math.Abs(Get(e, "low")) > 0.01) yield return new Biquad("lowshelf", Get(e, "lowf"), 0.7071, Get(e, "low"));
                if (Math.Abs(Get(e, "mid")) > 0.01) yield return new Biquad("peak", Get(e, "midf"), Math.Max(0.1, Get(e, "midq")), Get(e, "mid"));
                if (Math.Abs(Get(e, "high")) > 0.01) yield return new Biquad("highshelf", Get(e, "highf"), 0.7071, Get(e, "high"));
                if (Get(e, "highcut") >= 1000) yield return new Biquad("lp", Get(e, "highcut"), 0.7071);
                break;
            case "Compressor": yield return new Compressor(e); break;
            case "Gate": yield return new Gate(e); break;
            case "DeEsser": yield return new DeEsser(e); break;
            case "Reverb": yield return new Reverb(e); break;
            case "DeHum":
                double f0 = Get(e, "freq") >= 55 ? 60 : 50;
                for (int k = 1; k <= Math.Clamp((int)Get(e, "harm"), 1, 8); k++) yield return new Biquad("notch", f0 * k, 25);
                break;
        }
    }

    /// <summary>Freeverb (Jezar's public-domain design): the stereo input summed to mono → pre-delay → low cut → 8 parallel damped feedback
    /// combs per side (the right side's tunings 23 samples longer) → 4 series allpasses; out = dry·in + wet mixed across by width. Tunings
    /// scaled from 44.1 to 48 kHz. Plain doubles, so the mixer and the export file compute the same samples.</summary>
    sealed class Reverb : IProc
    {
        static readonly int[] CombT = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 }, AllT = { 556, 441, 341, 225 };
        const int Spread = 23;
        const double FixedGain = 0.015;
        readonly double[][] cl, cr, al, ar; readonly int[] ci, cj, ai, aj; readonly double[] fl, fr;
        readonly double feedback, damp, wet1, wet2, dry;
        readonly double[] pre; int pi;
        readonly Biquad? hp; double h1, h2;
        public Reverb(AudioEffect e)
        {
            static int S(int n) => (int)Math.Round(n * Fs / 44100.0);
            cl = CombT.Select(t => new double[S(t)]).ToArray(); cr = CombT.Select(t => new double[S(t + Spread)]).ToArray();
            al = AllT.Select(t => new double[S(t)]).ToArray(); ar = AllT.Select(t => new double[S(t + Spread)]).ToArray();
            ci = new int[8]; cj = new int[8]; ai = new int[4]; aj = new int[4]; fl = new double[8]; fr = new double[8];
            feedback = Math.Clamp(Get(e, "size") / 100, 0, 1) * 0.28 + 0.7;
            damp = Math.Clamp(Get(e, "damp") / 100, 0, 1) * 0.4;
            double width = Math.Clamp(Get(e, "width") / 100, 0, 1), wet = Math.Clamp(Get(e, "wet") / 100, 0, 1);
            wet1 = wet * (width / 2 + 0.5); wet2 = wet * ((1 - width) / 2); dry = Math.Clamp(Get(e, "dry") / 100, 0, 1);
            pre = new double[Math.Max(1, (int)Math.Round(Math.Clamp(Get(e, "pre"), 0, 500) / 1000 * Fs))];
            if (Get(e, "lowcut") >= 10) hp = new Biquad("hp", Get(e, "lowcut"), 0.7071);
        }
        double Comb(double x, double[] buf, ref int i, ref double f)
        {
            double y = buf[i];
            f = y * (1 - damp) + f * damp; if (Math.Abs(f) < 1e-25) f = 0;            // (no denormals)
            buf[i] = x + f * feedback;
            if (++i == buf.Length) i = 0;
            return y;
        }
        static double All(double x, double[] buf, ref int i)
        {
            double b = buf[i], y = b - x;
            buf[i] = x + b * 0.5; if (Math.Abs(buf[i]) < 1e-25) buf[i] = 0;
            if (++i == buf.Length) i = 0;
            return y;
        }
        public void Run(ref double l, ref double r)
        {
            double x = pre[pi]; pre[pi] = (l + r) * FixedGain; if (++pi == pre.Length) pi = 0;
            if (pre.Length == 1) x = pre[0];                                    // (no pre-delay)
            if (hp != null) x = hp.Step(x, ref h1, ref h2);
            double ol = 0, or = 0;
            for (int k = 0; k < 8; k++) { ol += Comb(x, cl[k], ref ci[k], ref fl[k]); or += Comb(x, cr[k], ref cj[k], ref fr[k]); }
            for (int k = 0; k < 4; k++) { ol = All(ol, al[k], ref ai[k]); or = All(or, ar[k], ref aj[k]); }
            double nl = l * dry + ol * wet1 + or * wet2, nr = r * dry + or * wet1 + ol * wet2;
            l = nl; r = nr;
        }
    }

    /// <summary>A chain of processors with its state. Processes interleaved 16-bit stereo in place (rounded, clipped).</summary>
    public sealed class Chain
    {
        readonly IProc[] procs;
        public Chain(IProc[] p) { procs = p; }
        public void Process(short[] buf, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                double l = buf[i * 2] / 32768.0, r = buf[i * 2 + 1] / 32768.0;
                foreach (var p in procs) p.Run(ref l, ref r);
                buf[i * 2] = (short)Math.Clamp(Math.Round(l * 32768), -32768, 32767);
                buf[i * 2 + 1] = (short)Math.Clamp(Math.Round(r * 32768), -32768, 32767);
            }
        }
        /// <summary>A track's bus (interleaved stereo floats, ±32768 = full scale), in place, unrounded and unclipped.</summary>
        public void ProcessFloat(float[] buf, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                double l = buf[i * 2] / 32768.0, r = buf[i * 2 + 1] / 32768.0;
                foreach (var p in procs) p.Run(ref l, ref r);
                buf[i * 2] = (float)(l * 32768); buf[i * 2 + 1] = (float)(r * 32768);
            }
        }
    }

    /// <summary>A track's effect chain as the mixer sees it (shared by the track's sources): the pre-fader sum of its clips goes through Fx,
    /// then × the fader / pan gains (GL / GR, or the automation tables AL / AR).</summary>
    public sealed record TrackBus(int Track, IReadOnlyList<AudioEffect> Fx, string Key, float GL, float GR, float[]? AL, float[]? AR)
    {
        /// <summary>History run before a jump: long enough for a reverb's tail.</summary>
        public int WarmFrames => Fx.Any(e => e.Kind == "Reverb") ? AudioCache.Rate * 4 : AudioCache.Rate / 2;
    }
    public static bool Active(TrackMix ch) => ch.Fx?.Any(e => e.On) == true;

    /// <summary>The live mixer's effect state: one chain per clip (and effect settings), carried from block to block. After a jump the chain
    /// starts fresh and first runs the half second before the jump (within the clip), so it sounds as it does when played through.</summary>
    public sealed class FxState
    {
        sealed class Entry { public Chain Chain = null!; public long Next; public long Used; }
        readonly Dictionary<string, Entry> map = new();
        long tick;
        short[] warm = new short[2];
        public const int WarmFrames = AudioCache.Rate / 2;

        public void Process(AudioOut.Src s, long srcFrame, short[] buf, int frames)
        {
            if (s.Fx == null || s.FxKey == null) return;
            tick++;
            if (!map.TryGetValue(s.FxKey, out var e) || e.Next != srcFrame)
            {
                e = new Entry { Chain = Build(s.Fx) };
                long first = (long)Math.Round(s.In * AudioCache.Rate), w0 = Math.Max(first, srcFrame - WarmFrames);
                int wn = (int)(srcFrame - w0);
                if (wn > 0)
                {
                    if (warm.Length < wn * 2) warm = new short[wn * 2];
                    if (AudioCache.Read(s.Path, w0, warm, wn)) e.Chain.Process(warm, wn);
                }
                map[s.FxKey] = e;
            }
            e.Chain.Process(buf, frames);
            e.Next = srcFrame + frames; e.Used = tick;
            if (tick % 512 == 0) foreach (var k in map.Where(kv => tick - kv.Value.Used > 2048).Select(kv => kv.Key).ToList()) map.Remove(k);
        }

        float[] warmF = new float[2];
        /// <summary>A track's pre-fader bus at timeline frame b0 through its chain, the state carried while blocks follow on. After a jump the
        /// chain starts fresh and first runs the track's own pre-fader sum of the seconds before (<see cref="TrackBus.WarmFrames"/>).</summary>
        public void ProcessBus(TrackBus tb, long b0, float[] buf, int frames, AudioOut.Src[] srcs)
        {
            tick++;
            string key = "bus|" + tb.Key;
            if (!map.TryGetValue(key, out var e) || e.Next != b0)
            {
                e = new Entry { Chain = Build(tb.Fx) };
                long w0 = Math.Max(0, b0 - tb.WarmFrames); int wn = (int)(b0 - w0);
                if (wn > 0)
                {
                    if (warmF.Length < wn * 2) warmF = new float[wn * 2];
                    Mixer.PreFader(srcs, tb.Track, w0, wn, warmF);
                    e.Chain.ProcessFloat(warmF, wn);
                }
                map[key] = e;
            }
            e.Chain.ProcessFloat(buf, frames);
            e.Next = b0 + frames; e.Used = tick;
        }

        /// <summary>A submix's summed input at timeline frame b0 through its effects; after a jump a fresh chain first runs the seconds before
        /// on the submix's own input (its tracks' mix, <see cref="Mixer.SubInput"/>).</summary>
        public void ProcessSub(Mixer.SubBus sb, long b0, float[] buf, int frames, AudioOut.Src[] srcs)
        {
            tick++;
            string key = "sub|" + sb.Key;
            if (!map.TryGetValue(key, out var e) || e.Next != b0)
            {
                e = new Entry { Chain = Build(sb.Fx!) };
                long w0 = Math.Max(0, b0 - sb.WarmFrames); int wn = (int)(b0 - w0);
                if (wn > 0)
                {
                    var wb = new float[wn * 2];                                    // (its own: the input's mix reuses this state's buffers)
                    Mixer.SubInput(srcs, sb.Index, w0, wn, wb);
                    e.Chain.ProcessFloat(wb, wn);
                }
                map[key] = e;
            }
            e.Chain.ProcessFloat(buf, frames);
            e.Next = b0 + frames; e.Used = tick;
        }
    }

    // ---------------- export: a submix as one file ----------------
    /// <summary>64-bit FNV over everything a source's sound depends on (paths, times, gains, tables, crossfades, effects, its track bus).</summary>
    static string SrcSig(IEnumerable<AudioOut.Src> srcs)
    {
        ulong h = 14695981039346656037;
        void B(ulong b) { for (int i = 0; i < 8; i++) { h ^= (b >> (i * 8)) & 0xFF; h *= 1099511628211; } }
        void D(double v) => B((ulong)BitConverter.DoubleToInt64Bits(v));
        void S(string? s) { foreach (var ch in s ?? "\0") B(ch); B(0xFF); }
        void A(float[]? a) { B(a == null ? 0 : (ulong)a.Length + 1); if (a != null) foreach (var v in a) B((ulong)BitConverter.SingleToInt32Bits(v)); }
        foreach (var s in srcs)
        {
            S(s.Path); D(s.Start); D(s.End); D(s.In); D(s.Volume); D(s.FadeIn); D(s.FadeOut); A(s.Gain); S(s.XIn?.ToString()); S(s.XOut?.ToString());
            B((ulong)s.Track); D(s.GL); D(s.GR); S(s.FxKey); A(s.AL); A(s.AR);
            if (s.Bus is { } tb) { S(tb.Key); D(tb.GL); D(tb.GR); A(tb.AL); A(tb.AR); }
        }
        return h.ToString("x16");
    }
    /// <summary>File of a submix's sound before its fader over [0, end): its tracks' own mix (the mixer's code) through its effects, from timeline
    /// 0, 48 kHz stereo f32le.</summary>
    public static string SubFileFor(AudioOut.Src[] srcs, Mixer.SubBus sb, double end) =>
        Path.Combine(AudioCache.Dir, "sub_" + AudioCache.Hash(SrcSig(srcs.Where(s => s.Sub?.Index == sb.Index)) + "|" + Sig(sb.Fx) + "|" + (long)Math.Round(end * AudioCache.Rate)) + ".f32");
    public static string EnsureSubFile(AudioOut.Src[] srcs, Mixer.SubBus sb, double end, CancellationToken ct = default)
    {
        var file = SubFileFor(srcs, sb, end);
        if (File.Exists(file)) return file;
        var mine = srcs.Where(s => s.Sub?.Index == sb.Index).Select(s => s with { Sub = null }).ToArray();
        foreach (var p in mine.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            while (!AudioCache.IsDone(p)) { ct.ThrowIfCancellationRequested(); Thread.Sleep(20); }
        var chain = sb.Fx != null ? Build(sb.Fx) : null;
        Directory.CreateDirectory(AudioCache.Dir);
        var part = file + ".part";
        byte[] bytes = Array.Empty<byte>();
        using (var fs = File.Create(part))
            Mixer.Render(mine, new MasterMix(1), 0, end, (buf, n) =>
            {
                chain?.ProcessFloat(buf, n);
                for (int i = 0; i < n * 2; i++) buf[i] /= 32768f;
                if (bytes.Length < n * 8) bytes = new byte[n * 8];
                Buffer.BlockCopy(buf, 0, bytes, 0, n * 8);
                fs.Write(bytes, 0, n * 8);
            }, ct);
        File.Move(part, file, true);
        return file;
    }

    // ---------------- export: a track with effects as one file ----------------
    /// <summary>File of a track's effected bus over [0, end): the track's clips summed before the fader (the mixer's own code) through its
    /// chain from timeline 0, 48 kHz stereo f32le (unclipped). The name carries everything it depends on.</summary>
    public static string TrackFileFor(IEnumerable<AudioOut.Src> trackSrcs, TrackBus tb, double end)
    {
        static string R(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var sig = string.Join(";", trackSrcs.Select(s => $"{s.Path}|{R(s.Start)}|{R(s.End)}|{R(s.In)}|{R(s.Volume)}|{R(s.FadeIn)}|{R(s.FadeOut)}|"
                                                           + $"{(s.Gain == null ? "" : AudioCache.Hash(string.Join(",", s.Gain.Select(g => R(g)))))}|{s.XIn}|{s.XOut}|{s.FxKey}"));
        return Path.Combine(AudioCache.Dir, "tfx_" + AudioCache.Hash(sig + "|" + Sig(tb.Fx) + "|" + (long)Math.Round(end * AudioCache.Rate)) + ".f32");
    }
    /// <summary>Renders a track's effected bus to its file (once).</summary>
    public static string EnsureTrackFile(AudioOut.Src[] srcs, TrackBus tb, double end, CancellationToken ct = default)
    {
        var mine = srcs.Where(s => s.Track == tb.Track && s.Bus != null).ToArray();
        var file = TrackFileFor(mine, tb, end);
        if (File.Exists(file)) return file;
        foreach (var p in mine.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            while (!AudioCache.IsDone(p)) { ct.ThrowIfCancellationRequested(); Thread.Sleep(20); }
        long total = (long)Math.Round(end * AudioCache.Rate);
        const int B = 4800;
        var buf = new float[B * 2]; var bytes = new byte[B * 8]; var tmp = new short[B * 2];
        var clipFx = new FxState(); var chain = Build(tb.Fx);
        Directory.CreateDirectory(AudioCache.Dir);
        var part = file + ".part";
        using (var fs = File.Create(part))
            for (long f = 0; f < total; f += B)
            {
                ct.ThrowIfCancellationRequested();
                int k = (int)Math.Min(B, total - f);
                Mixer.PreFader(mine, tb.Track, f, k, buf, clipFx, tmp);
                chain.ProcessFloat(buf, k);
                for (int i = 0; i < k * 2; i++) buf[i] /= 32768f;              // (f32le: ±1 = full scale)
                Buffer.BlockCopy(buf, 0, bytes, 0, k * 8);
                fs.Write(bytes, 0, k * 8);
            }
        File.Move(part, file, true);
        return file;
    }

    // ---------------- export: the clip's processed sound as a file ----------------
    /// <summary>File of a source's processed sound: frames [In, In + length) of its input (the media's or a speed change's conformed sound)
    /// through the chain from the first frame, 48 kHz stereo s16le.</summary>
    public static string FileFor(string inputKey, double inSec, double start, double end, IEnumerable<AudioEffect> fx)
    {
        long first = (long)Math.Round(inSec * AudioCache.Rate), n = (long)Math.Round(end * AudioCache.Rate) - (long)Math.Round(start * AudioCache.Rate);
        return Path.Combine(AudioCache.Dir, "fx_" + AudioCache.Hash(inputKey + "|" + first + "|" + n + "|" + Sig(fx)) + ".pcm");
    }

    /// <summary>Renders a mixer source's processed sound to its file (once; the name carries everything it depends on).</summary>
    public static string EnsureFile(AudioOut.Src s, CancellationToken ct = default)
    {
        var file = FileFor(s.Path, s.In, s.Start, s.End, s.Fx!);
        if (File.Exists(file)) return file;
        long first = (long)Math.Round(s.In * AudioCache.Rate), n = (long)Math.Round(s.End * AudioCache.Rate) - (long)Math.Round(s.Start * AudioCache.Rate);
        var chain = Build(s.Fx!);
        const int B = 4800;
        var buf = new short[B * 2]; var bytes = new byte[B * 4];
        Directory.CreateDirectory(AudioCache.Dir);
        var tmp = file + ".part";
        using (var fs = File.Create(tmp))
            for (long f = 0; f < n; f += B)
            {
                ct.ThrowIfCancellationRequested();
                int k = (int)Math.Min(B, n - f);
                if (!AudioCache.Read(s.Path, first + f, buf, k)) Array.Clear(buf, 0, k * 2);
                chain.Process(buf, k);
                Buffer.BlockCopy(buf, 0, bytes, 0, k * 4);
                fs.Write(bytes, 0, k * 4);
            }
        File.Move(tmp, file, true);
        return file;
    }
}
