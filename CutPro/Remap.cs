using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CutPro;

/// <summary>
/// Rendered video of clips that don't play their source 1:1 (speed, reverse, frame hold, time remapping): frame k of the file is
/// the media's native frame <see cref="TimeMap.NativeIndex"/> picks for output frame k of the clip's displayed range (transition
/// handles included), at the media's display size, lossless H.264 at the sequence rate. The preview plays it like a normal clip
/// (rendered in the background after an edit, like Premiere's render bar), the export reads the same file — so picture and timing
/// match exactly. Files live in %LOCALAPPDATA%\CutPro\Remap, named by their content (media + frame list), trimmed above 20 GB.
/// </summary>
public static class Remap
{
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro", "Remap");
    const long CacheCap = 20L << 30;

    /// <summary>Size of the rendered frames: the media's display size, at most 3840×2160 (even).</summary>
    public static (int w, int h) Size(MediaItem m)
    {
        double w = m.Width > 0 ? m.Width : 1280, h = m.Height > 0 ? m.Height : 720, f = Math.Min(1, Math.Min(3840 / w, 2160 / h));
        return (ExportSettings.Even(w * f), ExportSettings.Even(h * f));
    }

    /// <summary>Native frame index for each output frame of the clip's range [s, e) (s, e = <see cref="Transitions.Plan.Range"/>).</summary>
    public static long[] Frames(Clip c, MediaItem m, double s, double e)
    {
        int n = Math.Max(1, (int)Math.Round((e - s) * TimeUtil.Fps));
        var idx = new long[n];
        for (int k = 0; k < n; k++) idx[k] = TimeMap.NativeIndex(c, m, s + k / TimeUtil.Fps);
        return idx;
    }

    static string Sig(MediaItem m, long[] idx)
    {
        var fi = new FileInfo(m.Path);
        var (w, h) = Size(m);
        var head = Encoding.UTF8.GetBytes(FormattableString.Invariant($"{m.Path.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{w}x{h}|{TimeUtil.Rate}|{TimeMap.NativeFps(m):0.######}|v2{Hdr.Mark(m)}|"));   // (v2: BT.709 limited range — Grid709)
        var body = new byte[idx.Length * 8]; Buffer.BlockCopy(idx, 0, body, 0, body.Length);
        return Convert.ToHexString(SHA1.HashData(head.Concat(body).ToArray()))[..24];
    }
    static string FileOf(string sig) => Path.Combine(Dir, sig + ".mkv");

    // ---------------- background rendering for the preview ----------------
    sealed class Job
    {
        public string Sig = "", File = "";
        public MediaItem M = null!; public long[] Idx = Array.Empty<long>();
        public volatile int Status;              // 0 waiting, 1 rendering, 2 ready, 3 failed
        public long Wanted;                      // last time (ms) the preview asked for it
    }
    static readonly object gate = new();
    static readonly Dictionary<string, Job> jobs = new();
    static readonly Dictionary<string, string> specSig = new();
    static Thread? worker;
    static readonly AutoResetEvent wake = new(false);
    /// <summary>"Rendering speed changes… 40%" while the background worker renders (null when idle); <see cref="StatusChanged"/> on change.</summary>
    public static string? Status { get; private set; }
    public static event Action? StatusChanged;
    /// <summary>Tests: false = don't render in the background (the preview keeps its fallback).</summary>
    public static bool Background = true;

    /// <summary>The colour every render file is in: BT.709, limited range. Decoding converts the source's own (an untagged one read as BT.601,
    /// as swscale reads it in the preview and the export) — raw pipes carry no colour tags, and ffmpeg (7.1 on) converts colour spaces between
    /// filters by their tags: an untagged render was read as BT.601 and shifted the colours of speed-changed and stabilized clips.</summary>
    internal const string Grid709 = "format=pix_fmts=yuv420p:color_spaces=bt709:color_ranges=tv";
    /// <summary>Labels raw frames for the lossless encoder (the tags the file then carries).</summary>
    internal const string Tag709 = "setparams=colorspace=bt709:range=tv";

    internal static string Spec(Clip c, MediaItem m, double s, double e)
    {
        var sb = new StringBuilder(160).Append(m.Path).Append('|').Append(c.In.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(c.Start.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(s.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(e.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(c.Speed.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(c.Reverse).Append(c.Hold).Append('|').Append(TimeUtil.Fps.ToString("R", CultureInfo.InvariantCulture)).Append(Hdr.Mark(m));
        if (TimeMap.Ramped(c)) foreach (var k in Anim.Track(c, AnimProp.Speed)!.Keys) sb.Append('|').Append(k.T).Append(',').Append(k.V).Append(',').Append(k.In).Append(k.Out).Append(k.InSlope).Append(k.OutSlope).Append(k.InInfl).Append(k.OutInfl);
        return sb.ToString();
    }

    /// <summary>The rendered file for the clip's range, or null while it is still being rendered (it is queued then). Cheap enough per frame:
    /// the frame list is only computed when the clip changes.</summary>
    public static string? Ready(Clip c, MediaItem m, double s, double e)
    {
        var spec = Spec(c, m, s, e);
        lock (gate)
        {
            if (!specSig.TryGetValue(spec, out var sig))
            {
                var idx = Frames(c, m, s, e);
                sig = Sig(m, idx);
                if (specSig.Count > 2000) specSig.Clear();
                specSig[spec] = sig;
                if (!jobs.ContainsKey(sig)) jobs[sig] = new Job { Sig = sig, File = FileOf(sig), M = m, Idx = idx };
            }
            var j = jobs[sig];
            j.Wanted = Environment.TickCount64;
            if (j.Status == 0 && Background) { StartWorker(); wake.Set(); }
            return j.Status == 2 ? j.File : null;
        }
    }

    static void StartWorker()
    {
        if (worker != null) return;
        worker = new Thread(WorkLoop) { IsBackground = true, Name = "Remap", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    static void WorkLoop()
    {
        while (true)
        {
            wake.WaitOne(1000);
            Thread.Sleep(400);                                      // let an edit (trim drag) settle first
            Job? j;
            lock (gate)
            {
                long now = Environment.TickCount64;
                j = jobs.Values.Where(x => x.Status == 0 && now - x.Wanted < 5000).OrderByDescending(x => x.Wanted).FirstOrDefault();
                if (j == null) continue;
                j.Status = 1;
            }
            Run(j, CancellationToken.None, new Progress<double>(p => SetStatus($"Rendering speed changes… {p * 100:0}%")), background: true);
            lock (gate) if (!jobs.Values.Any(x => x.Status == 0)) SetStatus(null);
        }
    }
    static void SetStatus(string? s) { if (s == Status) return; Status = s; StatusChanged?.Invoke(); }

    static void Run(Job j, CancellationToken ct, IProgress<double>? progress, bool background)
    {
        try
        {
            if (!File.Exists(j.File)) Render(j.M, j.Idx, j.File, ct, progress, background);
            j.Status = File.Exists(j.File) ? 2 : 3;
        }
        catch (OperationCanceledException) { j.Status = 0; throw; }
        catch { j.Status = 3; }
    }

    // ---------------- export ----------------
    /// <summary>Rendered files of every remapped video clip the export shows (clip id → file); renders the missing ones (or waits for the
    /// background worker). Throws when one can't be rendered.</summary>
    public static Dictionary<string, string> PrepareExport(Editor ed, CancellationToken ct, IProgress<double>? progress = null)
    {
        var plan = Transitions.Compute(ed);
        var res = new Dictionary<string, string>();
        var todo = new List<(Clip c, Job j)>();
        foreach (var c in ed.Clips)
        {
            if (c.Kind != ClipKind.Video || !TimeMap.IsRemapped(c) || Stab.Active(c) || ed.TrackOff[c.Track] || ed.MediaOf(c) is not { Offline: false, Seq: null } m) continue;   // (nested sequences play at 100 %; stabilized clips: Stab renders them)
            var (s, e) = plan.Range(c);
            var idx = Frames(c, m, s, e);
            var sig = Sig(m, idx);
            Job j;
            lock (gate) { if (!jobs.TryGetValue(sig, out j!)) jobs[sig] = j = new Job { Sig = sig, File = FileOf(sig), M = m, Idx = idx }; }
            todo.Add((c, j));
        }
        for (int i = 0; i < todo.Count; i++)
        {
            var (c, j) = todo[i];
            var p = progress == null ? null : new Progress<double>(x => progress.Report((i + x) / todo.Count));
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                bool mine = false;
                lock (gate) { if (j.Status == 0 || j.Status == 3) { j.Status = 1; mine = true; } }
                if (mine) { Run(j, ct, p, background: false); break; }
                if (j.Status == 2) break;
                Thread.Sleep(50);                                   // the background worker is on it
            }
            if (j.Status != 2) throw new Exception($"Could not render the speed change of {ed.MediaOf(c)?.Name ?? "a clip"}.");
            res[c.Id] = j.File;
        }
        return res;
    }

    // ---------------- rendering ----------------
    /// <summary>Writes the frames idx (native indices, any order: forward, backwards, repeated) into a lossless file. Decodes the media in
    /// chunks of consecutive frames (fps filter at the native rate from a seek just before the chunk's first frame — the same frame grid
    /// the preview's fallback stream uses), forwards or backwards as the list goes.</summary>
    public static void Render(MediaItem m, long[] idx, string file, CancellationToken ct, IProgress<double>? progress = null, bool background = false)
    {
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        Directory.CreateDirectory(Dir);
        var (w, h) = Size(m);
        double fpsN = TimeMap.NativeFps(m);
        string rate = TimeUtil.Rational(fpsN);
        int fb = w * h * 3 / 2;
        int chunk = (int)Math.Clamp(300_000_000L / fb, 4, 120);
        var tmp = file + ".part.mkv";
        var enc = Start(ff, background, "-y", "-hide_banner", "-v", "error", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-s", $"{w}x{h}", "-r", TimeUtil.Rate,
                        "-i", "-", "-an", "-vf", Tag709, "-c:v", "libx264", "-qp", "0", "-preset", "ultrafast", "-tune", "fastdecode", "-g", "15", "-pix_fmt", "yuv420p", tmp);
        var cache = new SortedDictionary<long, byte[]>();
        var stdin = enc.StandardInput.BaseStream;
        byte[]? last = null;
        try
        {
            for (int k = 0; k < idx.Length; k++)
            {
                ct.ThrowIfCancellationRequested();
                long want = idx[k];
                if (!cache.TryGetValue(want, out var buf))
                {
                    // which way does the list go from here? backwards: decode the chunk that ends at want, else the one that starts there
                    int dir = 0;
                    for (int q = k + 1; q < idx.Length && dir == 0; q++) dir = Math.Sign(idx[q] - want);
                    long a = dir < 0 ? Math.Max(0, want - chunk + 1) : want;
                    cache.Clear();
                    Decode(ff, m, a, chunk, w, h, rate, fpsN, cache, ct, background);
                    buf = cache.TryGetValue(want, out var b) ? b : cache.Where(kv => kv.Key <= want).Select(kv => kv.Value).LastOrDefault() ?? last;   // past the media's end: its last frame
                    buf ??= cache.Values.FirstOrDefault() ?? new byte[fb];
                }
                stdin.Write(buf, 0, fb);
                last = buf;
                if ((k & 15) == 0) progress?.Report(k / (double)idx.Length);
            }
            stdin.Close();
            enc.WaitForExit();
            if (enc.ExitCode != 0 || !File.Exists(tmp)) throw new Exception("encoder failed");
            File.Move(tmp, file, true);
            progress?.Report(1);
        }
        finally
        {
            try { if (!enc.HasExited) enc.Kill(); } catch { }
            enc.Dispose();
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            Trim();
        }
    }

    static void Decode(string ff, MediaItem m, long a, int count, int w, int h, string rate, double fpsN, SortedDictionary<long, byte[]> into, CancellationToken ct, bool background)
    {
        double seek = Math.Max(0, a / fpsN - 1e-4);
        var p = Start(ff, background, "-hide_banner", "-v", "error", "-nostdin", "-ss", seek.ToString("0.######", CultureInfo.InvariantCulture), "-i", m.Path,
                      "-an", "-sn", "-dn", "-vf", $"fps={rate},scale={w}:{h}:flags=bicubic{Hdr.Post(m)},{Grid709}", "-frames:v", count.ToString(), "-f", "rawvideo", "pipe:1");
        try
        {
            var s = p.StandardOutput.BaseStream;
            int fb = w * h * 3 / 2;
            for (int j = 0; j < count; j++)
            {
                ct.ThrowIfCancellationRequested();
                var buf = new byte[fb]; int got = 0;
                while (got < fb) { int n = s.Read(buf, got, fb - got); if (n <= 0) break; got += n; }
                if (got < fb) break;
                into[a + j] = buf;
            }
        }
        finally { try { if (!p.HasExited) p.Kill(); } catch { } p.Dispose(); }
    }

    internal static Process Start(string ff, bool background, params string[] args)
    {
        var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        if (background) try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        return p;
    }

    /// <summary>Keeps the cache under 20 GB (oldest files go first).</summary>
    static void Trim()
    {
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles("*.mkv").OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files) { if (total <= CacheCap) break; if (f.Name.Contains(".part")) continue; total -= f.Length; try { f.Delete(); } catch { } }
        }
        catch { }
    }
}
