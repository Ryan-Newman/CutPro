using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// Scrub proxies: every frame of each video (at the 30 fps sequence rate) as a small JPEG (fits 640x360), built once
/// in the background into %LOCALAPPDATA%\CutPro\ScrubCache. While the playhead or a clip is being dragged, the monitor
/// shows the exact frame from here instantly (a ~2 ms JPEG decode instead of a 100–500 ms decoder start); the
/// full-resolution frame replaces it as soon as the movement stops. Partially built caches are usable.
/// The same background queue first benchmarks decoders for heavy files (<see cref="Decoders.Calibrate"/>).
/// </summary>
public static class ScrubCache
{
    public const int MaxW = 960, MaxH = 540;   // ~ the monitor's on-screen size, so the full-res swap is barely visible
    class Entry
    {
        public string Path = "", File = "";
        public int W, H;
        public readonly List<(long off, int len)> Frames = new();
        public volatile bool Done;
        public FileStream? Reader;
        public long BuildMs = -1;
    }
    static readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    static readonly BlockingCollection<(MediaItem m, Entry e)> queue = new();
    static Thread? worker;
    /// <summary>Raised (on a worker thread) when a file's decoder choice or cache status changes.</summary>
    public static event Action<MediaItem>? Updated;
    static string Dir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro", "ScrubCache");

    public static void Size(MediaItem m, out int w, out int h)
    {
        double f = Math.Min(Math.Min((double)MaxW / m.Width, (double)MaxH / m.Height), 1.0);
        w = Math.Max(2, (int)Math.Round(m.Width * f / 2) * 2); h = Math.Max(2, (int)Math.Round(m.Height * f / 2) * 2);
    }

    public static void Ensure(MediaItem m)
    {
        if (m.Type != ClipKind.Video || m.Offline || m.Width <= 0) return;
        Entry e;
        lock (entries)
        {
            if (entries.ContainsKey(m.Path)) return;
            Size(m, out int w, out int h);
            entries[m.Path] = e = new Entry { Path = m.Path, W = w, H = h };
        }
        queue.Add((m, e));
        lock (queue) if (worker == null) { worker = new Thread(Work) { IsBackground = true, Name = "ScrubCache", Priority = ThreadPriority.BelowNormal }; worker.Start(); }
    }

    static Entry? Get(string path) { lock (entries) return entries.TryGetValue(path, out var e) ? e : null; }
    public static bool IsDone(string path) => Get(path)?.Done == true;
    public static long BuildMs(string path) => Get(path)?.BuildMs ?? -1;
    public static long Bytes(string path) { var e = Get(path); if (e == null) return 0; lock (e.Frames) return e.Frames.Count == 0 ? 0 : e.Frames[^1].off + e.Frames[^1].len; }
    public static int FrameCount(string path) { var e = Get(path); if (e == null) return 0; lock (e.Frames) return e.Frames.Count; }
    public static void WaitReady(string path, int ms) { var sw = Stopwatch.StartNew(); while (!IsDone(path) && sw.ElapsedMilliseconds < ms) Thread.Sleep(20); }
    /// <summary>The cache keeps every frame at a fixed 30 fps whatever the sequence rate (its files stay valid when the rate changes).</summary>
    public const int Fps = 30;
    public static long FrameIndex(double src) => (long)Math.Floor(src * Fps + 1e-3);
    public static bool Has(string path, double src)
    {
        var e = Get(path); if (e == null) return false;
        long k = FrameIndex(src);
        lock (e.Frames) return k >= 0 && k < e.Frames.Count;
    }

    static string CacheName(string path)
    {
        var fi = new FileInfo(path);
        var key = $"{path.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        return System.IO.Path.Combine(Dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..20]);
    }

    /// <summary>Keeps the cache folder under ~20 GB by deleting the least recently used caches (runs once per session).</summary>
    static void Trim()
    {
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles().OrderBy(f => f.LastAccessTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            if (total <= 20L << 30) return;
            foreach (var f in files)
            {
                if (total < 15L << 30) break;
                lock (entries) if (entries.Values.Any(e => string.Equals(e.File, f.FullName, StringComparison.OrdinalIgnoreCase))) continue;
                total -= f.Length; f.Delete();
            }
        }
        catch { }
    }

    static void Work()
    {
        Trim();
        foreach (var (m, e) in queue.GetConsumingEnumerable())
        {
            try
            {
                var sw = Stopwatch.StartNew();
                Decoders.Calibrate(m);
                Updated?.Invoke(m);
                Build(m, e);
                e.BuildMs = sw.ElapsedMilliseconds;
                Updated?.Invoke(m);
            }
            catch { }
        }
    }

    static void Build(MediaItem m, Entry e)
    {
        Directory.CreateDirectory(Dir);
        var baseName = CacheName(m.Path);
        e.File = baseName + ".jpgs";
        var idx = baseName + ".idx";
        if (File.Exists(idx) && File.Exists(e.File) && TryLoadIndex(idx, e)) { Open(e); e.Done = true; return; }

        var ff = Exporter.FindFfmpeg(); if (ff == null) return;
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-v", "error", "-nostdin" }.Concat(Decoders.InputArgs(m.PlayDecoder))
                 .Concat(new[] { "-i", m.Path, "-an", "-sn", "-dn", "-vf", Decoders.Filter(m.PlayDecoder, e.W, e.H, "yuvj420p", Fps.ToString()),
                                 "-c:v", "mjpeg", "-q:v", "5", "-f", "image2pipe", "pipe:1" }))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        using (var outFile = new FileStream(e.File, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            Open(e);
            var s = p.StandardOutput.BaseStream;
            var buf = new byte[1 << 16];
            var frame = new MemoryStream();
            long written = 0; int prev = -1;
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                int start = 0;
                for (int i = 0; i < n; i++)
                {
                    // a JPEG ends with FF D9 after its scan data (0xFF inside entropy data is byte-stuffed)
                    if (prev == 0xFF && buf[i] == 0xD9)
                    {
                        frame.Write(buf, start, i + 1 - start); start = i + 1;
                        if (!InScan(frame.GetBuffer(), (int)frame.Length)) { prev = buf[i]; continue; }
                        outFile.Write(frame.GetBuffer(), 0, (int)frame.Length); outFile.Flush();
                        lock (e.Frames) e.Frames.Add((written, (int)frame.Length));
                        written += frame.Length; frame.SetLength(0);
                        prev = -1; continue;
                    }
                    prev = buf[i];
                }
                frame.Write(buf, start, n - start);
            }
        }
        p.WaitForExit();
        if (p.ExitCode == 0 && e.Frames.Count > 0) { SaveIndex(idx, e); e.Done = true; }
    }

    /// <summary>True if the JPEG header segments (walked by their lengths) reach the start-of-scan marker before len-2.</summary>
    static bool InScan(byte[] d, int len)
    {
        int pos = 2;                                       // after SOI
        while (pos + 4 <= len - 2)
        {
            if (d[pos] != 0xFF) return false;
            if (d[pos + 1] == 0xDA) return true;          // SOS: everything after is scan data
            pos += 2 + (d[pos + 2] << 8 | d[pos + 3]);
        }
        return false;
    }

    static void Open(Entry e) => e.Reader ??= new FileStream(e.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);

    static void SaveIndex(string idx, Entry e)
    {
        using var w = new BinaryWriter(File.Create(idx));
        w.Write(e.W); w.Write(e.H);
        lock (e.Frames) { w.Write(e.Frames.Count); foreach (var (off, len) in e.Frames) { w.Write(off); w.Write(len); } }
    }

    static bool TryLoadIndex(string idx, Entry e)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(idx));
            if (r.ReadInt32() != e.W || r.ReadInt32() != e.H) return false;
            int n = r.ReadInt32();
            var list = new List<(long, int)>(n);
            for (int i = 0; i < n; i++) list.Add((r.ReadInt64(), r.ReadInt32()));
            if (n == 0 || list[^1].Item1 + list[^1].Item2 != new FileInfo(e.File).Length) return false;
            lock (e.Frames) { e.Frames.Clear(); e.Frames.AddRange(list); }
            return true;
        }
        catch { return false; }
    }

    [ThreadStatic] static byte[]? jpegBuf, pixBuf;

    /// <summary>Decodes the proxy frame for src into wb (created/resized as needed). UI thread. Returns the frame index or -1.</summary>
    public static double DecodeMsTotal; public static int DecodeCount;   // diagnostics

    public static long Show(string path, double src, ref WriteableBitmap? wb, Grade.Lut? lut = null)
    {
        var e = Get(path);
        long t0 = Stopwatch.GetTimestamp();
        if (e?.Reader == null) return -1;
        long k = FrameIndex(src);
        (long off, int len) f;
        lock (e.Frames) { if (k < 0 || k >= e.Frames.Count) return -1; f = e.Frames[(int)k]; }
        if (jpegBuf == null || jpegBuf.Length < f.len) jpegBuf = new byte[Math.Max(f.len, 256 << 10)];
        e.Reader.Position = f.off;
        int got = 0;
        while (got < f.len) { int n = e.Reader.Read(jpegBuf, got, f.len - got); if (n <= 0) return -1; got += n; }
        using var ms = new MemoryStream(jpegBuf, 0, f.len, false);
        var dec = new JpegBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.None);
        var bgr = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgr32, null, 0);
        int w = bgr.PixelWidth, h = bgr.PixelHeight, stride = w * 4;
        if (pixBuf == null || pixBuf.Length < stride * h) pixBuf = new byte[stride * h];
        bgr.CopyPixels(pixBuf, stride, 0);
        if (lut != null) Grade.Apply(lut, pixBuf, w, h, stride);              // (the clip's colour, as on its decoded frames)
        if (wb == null || wb.PixelWidth != w || wb.PixelHeight != h) wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), pixBuf, stride, 0);
        DecodeMsTotal += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency; DecodeCount++;
        return k;
    }
}
