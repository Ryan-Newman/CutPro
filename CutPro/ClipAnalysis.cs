using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace CutPro;

/// <summary>Reading a media file's sound for analysis: the conformed 48 kHz stereo PCM (<see cref="AudioCache"/>) as mono floats.</summary>
public static class Pcm
{
    /// <summary>Mono samples of m from source second `from` for `len` seconds at `rate` Hz (48000 / rate must be whole — 24000, 16000, 12000, 8000, 4000 …;
    /// box-averaged, i.e. low-passed).
    /// Waits up to 2 minutes for the file to be conformed. Null when the media has no sound.</summary>
    public static float[]? Mono(MediaItem m, double from, double len, int rate)
    {
        if (!m.HasAudio || m.Offline || m.Seq != null) return null;
        AudioCache.Ensure(m);
        AudioCache.WaitReady(m.Path, 120000);
        if (!AudioCache.IsReady(m.Path)) return null;
        if (48000 % rate != 0) throw new ArgumentException("48000 / rate must be whole", nameof(rate));
        int step = 48000 / rate;
        long a = (long)Math.Round(from * 48000);
        int n = (int)Math.Max(0, Math.Round(len * 48000) / step);
        var res = new float[n];
        const int chunk = 48000;
        var buf = new short[chunk * 2];
        for (int o = 0; o < n; o += chunk / step)
        {
            int frames = Math.Min(chunk / step, n - o) * step;
            AudioCache.Read(m.Path, a + (long)o * step, buf, frames);
            for (int i = 0; i < frames / step; i++)
            {
                float s = 0;
                for (int k = 0; k < step; k++) { int j = (i * step + k) * 2; s += buf[j] + buf[j + 1]; }
                res[o + i] = s / (2f * step * 32768f);
            }
        }
        return res;
    }
    /// <summary>The conformed sound's length in seconds (conforms it first, waiting up to 2 minutes; 0 = no sound).</summary>
    public static double Length(MediaItem m)
    {
        if (!m.HasAudio || m.Offline || m.Seq != null) return 0;
        AudioCache.Ensure(m);
        AudioCache.WaitReady(m.Path, 120000);
        return AudioCache.Frames(m.Path) / 48000.0;
    }

    /// <summary>In-place radix-2 FFT (n a power of two; inverse = conjugate trick, not scaled).</summary>
    public static void Fft(Complex[] a, bool inverse)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = a[i + k]; var v = a[i + k + len / 2] * w;
                    a[i + k] = u + v; a[i + k + len / 2] = u - v;
                    w *= wl;
                }
            }
        }
    }
    public static int Pow2(int n) { int p = 1; while (p < n) p <<= 1; return p; }
}

/// <summary>Scene edit detection: ffmpeg's scene score on a small copy of the picture; a shot starts where the score passes the threshold.</summary>
public static class SceneDetect
{
    /// <summary>Sensitivity 0 … 100 → ffmpeg's scene threshold (50 → 0.3).</summary>
    public static double Threshold(double sensitivity) => Math.Clamp(0.55 - sensitivity * 0.005, 0.05, 0.6);

    /// <summary>Source times (seconds, on the media's frame grid) where a new shot starts, inside (a, b). minShot drops flashes.</summary>
    public static List<double> Find(string ff, MediaItem m, double a, double b, double sensitivity, double minShot, CancellationToken ct, IProgress<double>? progress = null)
    {
        var res = new List<double>();
        if (m.Type != ClipKind.Video || m.Offline || b - a < 0.05) return res;
        double fps = TimeMap.NativeFps(m);
        string args = $"-hide_banner -nostats -ss {a.ToString(CultureInfo.InvariantCulture)} -t {(b - a).ToString(CultureInfo.InvariantCulture)} -i \"{m.Path}\" -an -sn " +
                      $"-vf \"scale=256:-2:flags=area,select='gt(scene\\,{Threshold(sensitivity).ToString("0.###", CultureInfo.InvariantCulture)})',showinfo\" -f null -";
        var psi = new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(); } catch { } });
        var pts = new Regex(@"pts_time:\s*([0-9.]+)");
        var time = new Regex(@"time=(\d+):(\d+):([0-9.]+)");
        string? line;
        while ((line = p.StandardError.ReadLine()) != null)
        {
            if (line.Contains("Parsed_showinfo") && pts.Match(line) is { Success: true } mt)
            {
                double s = a + double.Parse(mt.Groups[1].Value, CultureInfo.InvariantCulture);
                s = Math.Round(s * fps) / fps;                                   // (the first frame of the new shot)
                if (s > a + minShot - 1e-6 && s < b - minShot / 2 && (res.Count == 0 || s - res[^1] >= minShot - 1e-6)) res.Add(s);
                progress?.Report(Math.Clamp((s - a) / (b - a), 0, 1));
            }
        }
        p.WaitForExit();
        ct.ThrowIfCancellationRequested();
        return res;
    }
}

/// <summary>Synchronize clips by their sound (an external recorder with a camera): GCC-PHAT cross-correlation, coarse at 4 kHz over the
/// whole files, then refined at 48 kHz around the loudest stretch — the lag in seconds (sound at reference source time s is at s + lag in the other).</summary>
public static class AudioSync
{
    public sealed record Result(double Lag, double Confidence);

    public static Result? Lag(float[] refPcm, float[] other, int rate, double maxLag)
    {
        if (refPcm.Length < rate / 4 || other.Length < rate / 4) return null;
        int n = Pcm.Pow2(refPcm.Length + other.Length);
        var A = new Complex[n]; var B = new Complex[n];
        double ma = refPcm.Average(), mb = other.Average();
        for (int i = 0; i < refPcm.Length; i++) A[i] = refPcm[i] - ma;
        for (int i = 0; i < other.Length; i++) B[i] = other[i] - mb;
        Pcm.Fft(A, false); Pcm.Fft(B, false);
        for (int i = 0; i < n; i++)
        {
            var c = B[i] * Complex.Conjugate(A[i]);
            double mag = c.Magnitude;
            A[i] = mag > 1e-12 ? c / mag : Complex.Zero;                         // PHAT: every frequency weighs the same — a sharp peak
        }
        Pcm.Fft(A, true);
        int lim = (int)Math.Min(n / 2 - 1, maxLag * rate);
        int best = 0; double bv = double.MinValue, sum = 0, sum2 = 0; int cnt = 0;
        for (int k = -lim; k <= lim; k++)
        {
            double v = A[(k + n) % n].Real;
            sum += v; sum2 += v * v; cnt++;
            if (v > bv) { bv = v; best = k; }
        }
        double mean = sum / cnt, sd = Math.Sqrt(Math.Max(1e-30, sum2 / cnt - mean * mean));
        // sub-sample: parabola through the peak and its neighbours
        double y0 = A[(best - 1 + n) % n].Real, y1 = bv, y2 = A[(best + 1) % n].Real;
        double den = y0 - 2 * y1 + y2, frac = Math.Abs(den) > 1e-12 ? Math.Clamp(0.5 * (y0 - y2) / den, -0.5, 0.5) : 0;
        return new Result((best + frac) / rate, (bv - mean) / sd);
    }

    /// <summary>The lag between two media files' sound (null: no sound, or nothing matched — confidence below 8).</summary>
    public static Result? Between(MediaItem reference, MediaItem other, double maxLag = 600, double maxLen = 1200)
    {
        const int coarse = 4000;
        var a = Pcm.Mono(reference, 0, Math.Min(maxLen, Pcm.Length(reference)), coarse);
        var b = Pcm.Mono(other, 0, Math.Min(maxLen, Pcm.Length(other)), coarse);
        if (a == null || b == null) return null;
        var r = Lag(a, b, coarse, maxLag);
        if (r == null || r.Confidence < 8) return r;
        // refine at 48 kHz: 4 s of the reference around its loudest second, against the same stretch of the other
        int win = 4;
        double best = 0, loud = -1;
        for (int s = 0; s + coarse <= a.Length; s += coarse / 2)
        {
            double e = 0; for (int i = s; i < s + coarse; i++) e += a[i] * a[i];
            double t = s / (double)coarse;
            if (e > loud && t + r.Lag >= 0 && t + win + r.Lag <= b.Length / (double)coarse) { loud = e; best = t; }
        }
        double t0 = Math.Max(0, best - win / 2.0);
        var fa = Pcm.Mono(reference, t0, win, 48000);
        var fb = Pcm.Mono(other, t0 + r.Lag - 0.05, win + 0.1, 48000);
        if (fa == null || fb == null) return r;
        var fine = Lag(fa, fb, 48000, 0.1);
        return fine == null || fine.Confidence < 6 ? r : new Result(r.Lag - 0.05 + fine.Lag, r.Confidence);
    }
}

/// <summary>Beat detection: an onset envelope (spectral flux, 100 per second), the tempo from its autocorrelation (70 … 180 BPM, octave
/// errors folded), the grid's phase that collects the most onsets, each beat pulled to its nearby onset.</summary>
public static class Beats
{
    public sealed record Result(double Bpm, List<double> Times, int DownbeatPhase);
    const int Rate = 100;
    /// <summary>Where in the 1024-sample window an onset registers (fraction of the window; measured on click tracks by --hightest).</summary>
    public static double Lead = 0.72;

    public static float[] Onsets(float[] pcm, int rate)
    {
        int hop = rate / Rate, win = 1024;
        int frames = Math.Max(0, (pcm.Length - win) / hop);
        var env = new float[frames];
        var prev = new double[win / 2];
        var buf = new Complex[win];
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < win; i++) buf[i] = pcm[f * hop + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / win));
            Pcm.Fft(buf, false);
            double flux = 0;
            for (int k = 1; k < win / 2; k++)
            {
                double m = Math.Log(1 + 100 * buf[k].Magnitude);
                if (m > prev[k]) flux += m - prev[k];
                prev[k] = m;
            }
            env[f] = (float)flux;
        }
        // subtract a moving average (only the rises above the local level count)
        var o = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double s = 0; int c = 0;
            for (int k = Math.Max(0, f - 25); k < Math.Min(frames, f + 25); k++) { s += env[k]; c++; }
            o[f] = (float)Math.Max(0, env[f] - s / Math.Max(1, c));
        }
        return o;
    }

    public static Result? Detect(float[] pcm, int rate, double minBpm = 70, double maxBpm = 180)
    {
        var o = Onsets(pcm, rate);
        if (o.Length < Rate * 3) return null;
        // tempo: autocorrelation of the onset envelope, a little bias towards 120 BPM (octave choice), parabolic peak
        int lo = (int)(60.0 * Rate / maxBpm), hi = (int)Math.Ceiling(60.0 * Rate / minBpm);
        double Ac(int lag) { double s = 0; for (int i = lag; i < o.Length; i++) s += o[i] * o[i - lag]; return s / (o.Length - lag); }
        var ac = new double[hi + 2];
        for (int l = lo - 1; l <= hi + 1; l++) ac[l] = Ac(l);
        int bl = lo; double bv = double.MinValue;
        for (int l = lo; l <= hi; l++)
        {
            double bpm = 60.0 * Rate / l, w = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 120) / 1.0, 2));
            double v = (ac[l] + 0.5 * (2 * l <= hi + 1 ? ac[Math.Min(hi + 1, 2 * l)] : 0)) * w;
            if (v > bv) { bv = v; bl = l; }
        }
        double den = ac[bl - 1] - 2 * ac[bl] + ac[bl + 1];
        double period = bl + (Math.Abs(den) > 1e-12 ? Math.Clamp(0.5 * (ac[bl - 1] - ac[bl + 1]) / den, -0.5, 0.5) : 0);
        // phase: the grid offset that collects the most onset energy
        double bestPh = 0, bestS = double.MinValue;
        for (double ph = 0; ph < period; ph += 0.25)
        {
            double s = 0;
            for (double t = ph; t < o.Length; t += period) s += o[(int)Math.Round(t) < o.Length ? (int)Math.Round(t) : o.Length - 1];
            if (s > bestS) { bestS = s; bestPh = ph; }
        }
        // beats: on the grid, each pulled to the strongest onset within ±40 ms (a steady grid where there is none), then re-fitted
        var times = new List<double>();
        for (double t = bestPh; t < o.Length - 1; t += period)
        {
            int c = (int)Math.Round(t), bi = c; float bo = o[Math.Min(o.Length - 1, c)];
            for (int k = Math.Max(0, c - 4); k <= Math.Min(o.Length - 1, c + 4); k++) if (o[k] > bo * 1.2f) { bo = o[k]; bi = k; }
            double sub = bi;
            if (bi > 0 && bi < o.Length - 1)
            {
                double d2 = o[bi - 1] - 2 * o[bi] + o[bi + 1];
                if (Math.Abs(d2) > 1e-9) sub = bi + Math.Clamp(0.5 * (o[bi - 1] - o[bi + 1]) / d2, -0.5, 0.5);
            }
            times.Add(sub / Rate);
        }
        // the onset frame f covers samples f·hop … f·hop + win: a sound starting at sample s shows as soon as the window reaches it
        double lead = Lead * 1024.0 / rate;
        times = times.Select(t => Math.Max(0, t + lead)).ToList();
        // downbeats: the phase (of 4) whose beats are the loudest
        int dp = 0; double ds = double.MinValue;
        for (int k = 0; k < 4; k++)
        {
            double s = 0;
            for (int i = k; i < times.Count; i += 4) s += o[Math.Clamp((int)Math.Round((times[i] - lead) * Rate), 0, o.Length - 1)];
            if (s > ds) { ds = s; dp = k; }
        }
        return new Result(60.0 * Rate / period, times, dp);
    }
}

/// <summary>Auto-ducking: the music's volume dips under speech. Speech = voice activity of the dialogue clips' sound (20 ms frames,
/// <see cref="Captioning.Voice"/>), short pauses bridged; the dip is written as the music clips' Volume keyframes (by source time, like every key).</summary>
public static class Ducking
{
    public sealed record Settings(double Db = 15, double Attack = 0.3, double Release = 0.8, double Hold = 0.6, double Sensitivity = 50);

    /// <summary>Speech regions (timeline seconds) of the dialogue clips over [t0, t1).</summary>
    public static List<(double A, double B)> Speech(Editor ed, IEnumerable<Clip> dialogue, double t0, double t1, Settings s)
    {
        const int rate = 16000;
        int n = (int)Math.Max(0, (t1 - t0) * rate);
        var mix = new float[n];
        foreach (var c in dialogue)
        {
            var m = ed.MediaOf(c);
            if (m == null || !Mixer.HasSound(c)) continue;
            double a = Math.Max(t0, c.Start), b = Math.Min(t1, c.End);
            if (b <= a) continue;
            double src = TimeMap.Src(c, a);
            var pcm = Pcm.Mono(m, src, (b - a) * TimeMap.Ratio(c), rate);         // (plain speed: Ratio 1)
            if (pcm == null) continue;
            double g = c.Volume / 100;
            int o = (int)((a - t0) * rate);
            for (int i = 0; i < pcm.Length && o + i < n; i++) mix[o + i] += (float)(pcm[i] * g);
        }
        var v = new Captioning.Voice(mix);
        var res = new List<(double, double)>();
        double step = Captioning.Voice.Step;
        int hold = (int)Math.Round(s.Hold / step);
        int f = 0;
        while (f < v.On.Length)
        {
            if (!v.On[f]) { f++; continue; }
            int g = f; while (g < v.On.Length && v.On[g]) g++;
            double a = t0 + f * step, b = t0 + g * step;
            if (b - a >= 0.12)
            {
                if (res.Count > 0 && a - res[^1].Item2 <= s.Hold + s.Attack + s.Release) res[^1] = (res[^1].Item1, b);
                else res.Add((a, b));
            }
            f = g;
        }
        return res;
    }

    /// <summary>The Volume keyframes (timeline time, %) that duck a clip at base volume under the regions.</summary>
    public static List<(double t, double v)> Keys(Clip music, List<(double A, double B)> speech, Settings s)
    {
        double base0 = music.Volume, low = base0 * Math.Pow(10, -s.Db / 20);
        var keys = new List<(double, double)>();
        foreach (var (a, b) in speech)
        {
            double k0 = a - s.Attack, k3 = b + s.Release;
            if (k3 <= music.Start || k0 >= music.End) continue;
            keys.Add((k0, base0)); keys.Add((a, low)); keys.Add((b, low)); keys.Add((k3, base0));
        }
        return keys.Where(k => k.Item1 >= music.Start - 1e-9 && k.Item1 <= music.End + 1e-9).ToList();
    }

    /// <summary>The dips for each music clip (reads the sound: run it off the UI thread).</summary>
    public static List<(Clip c, List<(double t, double v)> keys)> Plan(Editor ed, IReadOnlyList<Clip> music, IReadOnlyList<Clip> dialogue, Settings s) =>
        music.Select(c => (c, Keys(c, Speech(ed, dialogue.Where(d => d.Id != c.Id), c.Start - s.Release, c.End + s.Attack, s), s))).ToList();

    /// <summary>Plans and writes the dips (<see cref="Write"/>). Returns the number of dips.</summary>
    public static int Apply(Editor ed, IReadOnlyList<Clip> music, IReadOnlyList<Clip> dialogue, Settings s) => Write(ed, Plan(ed, music, dialogue, s));

    /// <summary>Writes the dips on the music clips (their volume keys replaced) — one undo step. Returns the number of dips.</summary>
    public static int Write(Editor ed, List<(Clip c, List<(double t, double v)> keys)> plans)
    {
        int dips = plans.Sum(p => p.keys.Count / 4);
        ed.Edit(() =>
        {
            foreach (var (c, keys) in plans)
            {
                Anim.Ensure(c, AnimProp.Volume).Keys.Clear();
                foreach (var (t, v) in keys) Anim.SetKey(c, AnimProp.Volume, TimeMap.Src(c, t), v);
                Anim.Prune(c);
            }
        }, "Auto Ducking");
        return dips;
    }
}
