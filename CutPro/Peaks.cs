using System.Runtime.InteropServices;

namespace CutPro;

/// <summary>
/// Waveform data for the timeline: per conformed file (<see cref="AudioCache"/>), the loudest sample (left or right) of every
/// 256-frame block, plus coarser levels (×4 each) so any zoom reads only a few values per pixel column. Built in the background from
/// the PCM and kept next to it as <c>.peaks</c> (level 0), so it is instant the next time.
/// </summary>
public static class Peaks
{
    public const int Block = 256, Factor = 4;

    public sealed class Data
    {
        public readonly ushort[][] Levels;
        public readonly long Frames;
        public Data(ushort[] l0, long frames)
        {
            Frames = frames;
            var lv = new List<ushort[]> { l0 };
            while (lv[^1].Length > 64)
            {
                var a = lv[^1]; var b = new ushort[(a.Length + Factor - 1) / Factor];
                for (int i = 0; i < a.Length; i++) if (a[i] > b[i / Factor]) b[i / Factor] = a[i];
                lv.Add(b);
            }
            Levels = lv.ToArray();
        }

        /// <summary>Peak of source frames [f0, f1) (1 = full scale), from the coarsest level that still resolves the span.</summary>
        public float Max(long f0, long f1)
        {
            if (f1 <= f0) f1 = f0 + 1;
            long span = f1 - f0, bs = Block; int lv = 0;
            while (lv + 1 < Levels.Length && bs * Factor <= span) { lv++; bs *= Factor; }
            var a = Levels[lv];
            long i0 = Math.Max(0, f0 / bs), i1 = Math.Min(a.Length, (f1 + bs - 1) / bs);
            int m = 0;
            for (long i = i0; i < i1; i++) if (a[i] > m) m = a[i];
            return m / 32768f;
        }
    }

    static readonly Dictionary<string, Data?> cache = new(StringComparer.OrdinalIgnoreCase);   // null = being built (or no sound)
    /// <summary>Raised on a worker thread when a file's waveform becomes available.</summary>
    public static event Action? Ready;

    /// <summary>The file's waveform, or null while it is being built (then <see cref="Ready"/> fires).</summary>
    public static Data? Get(MediaItem m)
    {
        if (!m.HasAudio || m.Offline) return null;
        lock (cache)
        {
            if (cache.TryGetValue(m.Path, out var d)) return d;
            cache[m.Path] = null;
        }
        AudioCache.Ensure(m);
        var path = m.Path;
        Task.Run(() => Build(path, Path.ChangeExtension(AudioCache.CacheFile(path), ".peaks")));
        return null;
    }

    /// <summary>The waveform of sound already readable under an <see cref="AudioCache"/> key (a nested sequence's mix), kept in peaksFile.</summary>
    public static Data? Get(string key, string peaksFile)
    {
        lock (cache)
        {
            if (cache.TryGetValue(key, out var d)) return d;
            cache[key] = null;
        }
        Task.Run(() => Build(key, peaksFile));
        return null;
    }

    static void Build(string path, string file)
    {
        try
        {
            while (!AudioCache.IsDone(path)) Thread.Sleep(50);
            long frames = AudioCache.Frames(path);
            if (frames <= 0) return;
            long n0 = (frames + Block - 1) / Block;
            ushort[]? l0 = null;
            try { if (File.Exists(file) && new FileInfo(file).Length == n0 * 2) l0 = MemoryMarshal.Cast<byte, ushort>(File.ReadAllBytes(file)).ToArray(); } catch { }
            if (l0 == null)
            {
                l0 = new ushort[n0];
                const int Chunk = Block * 2048;
                var buf = new short[Chunk * 2];
                for (long f = 0; f < frames; f += Chunk)
                {
                    int n = (int)Math.Min(Chunk, frames - f);
                    if (!AudioCache.Read(path, f, buf, n)) return;
                    for (int k = 0; k < n; k++)
                    {
                        int v = Math.Max(Math.Abs((int)buf[k * 2]), Math.Abs((int)buf[k * 2 + 1]));
                        long i = (f + k) / Block;
                        if (v > l0[i]) l0[i] = (ushort)v;
                    }
                }
                try { File.WriteAllBytes(file, MemoryMarshal.AsBytes(l0.AsSpan()).ToArray()); } catch { }
            }
            lock (cache) cache[path] = new Data(l0, frames);
            Ready?.Invoke();
        }
        catch { /* no waveform rather than a crash */ }
    }
}
