using System.Numerics;
using System.Runtime.InteropServices;

namespace CutPro;

/// <summary>
/// ITU-R BS.1770-4 / EBU R128 loudness: integrated loudness (K-weighted, 400 ms blocks with 75 % overlap, absolute gate −70 LUFS,
/// relative gate −10 LU) and true peak (4× oversampled). <c>--audiotest</c> checks it against ffmpeg's ebur128 filter.
/// </summary>
public static class Loudness
{
    /// <summary>Integrated loudness (LUFS, −∞ for silence), true peak and sample peak (dBTP / dBFS), measured seconds.</summary>
    public readonly record struct Result(double Integrated, double TruePeak, double SamplePeak, double Seconds)
    {
        public bool Silent => !double.IsFinite(Integrated);
        public override string ToString() => Silent ? "silent" : $"{Integrated:0.0} LUFS · true peak {TruePeak:0.0} dBTP";
    }

    /// <summary>Live EBU R128 readings (LUFS / dBTP; −∞ when there isn't enough sound yet). Momentary = last 400 ms,
    /// short-term = last 3 s, ungated; integrated = the gated programme loudness since <see cref="Meter.Reset"/>.</summary>
    public readonly record struct LiveReading(double Momentary, double ShortTerm, double Integrated, double TruePeak);

    /// <summary>Normalization targets offered in the export dialog.</summary>
    public static readonly (double lufs, string label)[] Targets =
    {
        (-14, "−14 LUFS · YouTube and social video"), (-16, "−16 LUFS · podcasts"),
        (-23, "−23 LUFS · EBU R128 broadcast"), (-24, "−24 LKFS · US broadcast (ATSC)"),
    };

    /// <summary>Gain (dB) that brings <paramref name="r"/> to the target, and whether peaks then need the limiter to stay under the ceiling.</summary>
    public static (double gainDb, bool limit) Normalize(Result r, double target, double ceiling)
    {
        if (r.Silent) return (0, false);
        double g = Math.Clamp(target - r.Integrated, -40, 40);
        return (g, r.TruePeak + g > ceiling);
    }

    public sealed class Meter
    {
        // K-weighting at 48 kHz (BS.1770-4 table 1 and 2): high-shelf pre-filter, then RLB high-pass
        const double B0 = 1.53512485958697, B1 = -2.69169618940638, B2 = 1.19839281085285, A1 = -1.69065929318241, A2 = 0.73248077421585;
        const double C1 = -2.0, D1 = -1.99004745483398, D2 = 0.99007225036621;
        const int Sub = Mixer.Rate / 10;                       // 100 ms sub-blocks; a gating block = 4 of them
        readonly object gate = new();
        readonly double[] s = new double[8];                    // filter state: per channel 2 stages × (s1, s2)
        readonly List<double> subs = new();                     // sum of squares (L + R) per completed sub-block
        double acc; int accN; long frames;
        double samplePeak, truePeak;
        // true peak: 4× oversampling by windowed-sinc interpolation (16 taps per phase) between x[n] and x[n+1]
        const int Half = 8, Taps = 2 * Half;
        static readonly float[][] Kernel = MakeKernel();
        readonly float[][] hist = { new float[Taps * 2], new float[Taps * 2] };   // each sample written twice (w, w + Taps): taps are contiguous
        int hpos;

        static float[][] MakeKernel()
        {
            static double I0(double x) { double sum = 1, t = 1; for (int k = 1; k < 30; k++) { t *= x / (2 * k); sum += t * t; } return sum; }
            const double Beta = 7;
            var k = new float[3][];
            for (int p = 0; p < 3; p++)
            {
                double phi = (p + 1) / 4.0, sum = 0;
                var d0 = new double[Taps];
                for (int i = 0; i < Taps; i++)
                {
                    double d = phi - (i - (Half - 1));           // taps x[n-7] … x[n+8]
                    double sinc = Math.Abs(d) < 1e-12 ? 1 : Math.Sin(Math.PI * d) / (Math.PI * d);
                    double w = Math.Abs(d) >= Half ? 0 : I0(Beta * Math.Sqrt(1 - d * d / (Half * Half))) / I0(Beta);
                    d0[i] = sinc * w; sum += d0[i];
                }
                k[p] = d0.Select(v => (float)(v / sum)).ToArray();   // unity gain at DC
            }
            return k;
        }

        /// <summary>Adds n interleaved stereo frames (scaled by <paramref name="scale"/> to ±1 full scale).</summary>
        public void Add(float[] buf, int n, float scale = 1)
        {
            lock (gate)
            {
                for (int i = 0; i < n; i++)
                {
                    double e = 0;
                    for (int ch = 0; ch < 2; ch++)
                    {
                        double x = buf[i * 2 + ch] * (double)scale;
                        int o = ch * 4;
                        double y1 = B0 * x + s[o]; s[o] = B1 * x - A1 * y1 + s[o + 1]; s[o + 1] = B2 * x - A2 * y1;
                        double y2 = y1 + s[o + 2]; s[o + 2] = C1 * y1 - D1 * y2 + s[o + 3]; s[o + 3] = y1 - D2 * y2;
                        e += y2 * y2;
                        float ax = (float)Math.Abs(x);
                        if (ax > samplePeak) samplePeak = ax;
                        var h = hist[ch]; h[hpos] = h[hpos + Taps] = (float)x;
                    }
                    hpos = (hpos + 1) % Taps;
                    Peaks();
                    acc += e;
                    if (++accN == Sub) { subs.Add(acc); acc = 0; accN = 0; }
                }
                frames += n;
            }
        }

        // after a frame is added the 16 newest samples are h[hpos … hpos+15] (oldest first): interpolate between x[n] = tap 7 and x[n+1] = tap 8.
        // Only near the loudest samples so far (an in-between peak can't top the sample peak where the neighbours are 6 dB below it).
        static readonly bool Simd = Taps % Vector<float>.Count == 0;
        void Peaks()
        {
            float gate = (float)(samplePeak * 0.5);
            for (int ch = 0; ch < 2; ch++)
            {
                var t = new ReadOnlySpan<float>(hist[ch], hpos, Taps);
                if (Math.Max(Math.Max(Math.Abs(t[Half - 1]), Math.Abs(t[Half])), Math.Max(Math.Abs(t[Half - 2]), Math.Abs(t[Half + 1]))) < gate) continue;
                for (int p = 0; p < 3; p++)
                {
                    var k = Kernel[p]; float v = 0;
                    if (Simd)
                    {
                        var tv = MemoryMarshal.Cast<float, Vector<float>>(t); var kv = MemoryMarshal.Cast<float, Vector<float>>(k);
                        var sum = Vector<float>.Zero;
                        for (int i = 0; i < tv.Length; i++) sum += tv[i] * kv[i];
                        v = Vector.Sum(sum);
                    }
                    else for (int i = 0; i < Taps; i++) v += k[i] * t[i];
                    if (Math.Abs(v) > truePeak) truePeak = Math.Abs(v);
                }
            }
        }

        /// <summary>Clears the filters, the programme and the peaks (a new playback pass).</summary>
        public void Reset()
        {
            lock (gate)
            {
                Array.Clear(s); Array.Clear(hist[0]); Array.Clear(hist[1]);
                subs.Clear(); acc = 0; accN = 0; frames = 0; samplePeak = 0; truePeak = 0; hpos = 0;
            }
        }

        public Result Result() { lock (gate) return ResultUnlocked(); }

        /// <summary>Momentary / short-term / integrated / true peak of the samples added since <see cref="Reset"/> (the mixer UI while playing).</summary>
        public LiveReading Live()
        {
            lock (gate)
            {
                var r = ResultUnlocked();
                return new LiveReading(Window(4), Window(30), r.Integrated, r.TruePeak);
            }
        }

        // ungated mean of the last n 100 ms sub-blocks (4 = 400 ms momentary, 30 = 3 s short-term)
        double Window(int nSubs)
        {
            if (subs.Count == 0) return double.NegativeInfinity;
            int n = Math.Min(nSubs, subs.Count);
            double z = 0;
            for (int i = subs.Count - n; i < subs.Count; i++) z += subs[i];
            z /= n * (double)Sub;
            return z <= 0 ? double.NegativeInfinity : -0.691 + 10 * Math.Log10(z);
        }

        Result ResultUnlocked()
        {
            // gating blocks: 400 ms, a new one every 100 ms
            int nb = subs.Count - 3;
            var blocks = new double[Math.Max(0, nb)];
            for (int j = 0; j < nb; j++) blocks[j] = (subs[j] + subs[j + 1] + subs[j + 2] + subs[j + 3]) / (4.0 * Sub);
            static double L(double z) => -0.691 + 10 * Math.Log10(z);
            double absZ = Math.Pow(10, (-70 + 0.691) / 10);
            var g1 = blocks.Where(z => z > absZ).ToArray();
            double integrated = double.NegativeInfinity;
            if (g1.Length > 0)
            {
                double rel = L(g1.Average()) - 10, relZ = Math.Pow(10, (rel + 0.691) / 10);
                var g2 = g1.Where(z => z > relZ).ToArray();
                if (g2.Length > 0) integrated = L(g2.Average());
            }
            double tp = Math.Max(truePeak, samplePeak);
            return new Result(integrated, Mixer.Db(tp), Mixer.Db(samplePeak), frames / (double)Mixer.Rate);
        }
    }
}
