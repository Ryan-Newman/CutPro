using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CutPro;

/// <summary>Stabilization of a video clip (null = off): Effect Controls → Stabilize.</summary>
public sealed class Stabilize
{
    /// <summary>How smooth the camera path becomes, % (50 ≈ a second of path averaged around each frame).</summary>
    public double Smooth { get; set; } = 50;
    /// <summary>No motion: the frame is locked to the first one (a tripod shot).</summary>
    public bool Tripod { get; set; }
    /// <summary>0 = zoom in just enough for the whole clip (no edges), 1 = zoom as needed (moment by moment), 2 = show the edges (black).</summary>
    public int Edges { get; set; }
    /// <summary>How shaky the footage is (motion detection, 1–10).</summary>
    public int Shake { get; set; } = 5;
    public Stabilize Clone() => (Stabilize)MemberwiseClone();
}

/// <summary>Video noise reduction (null = off): Effect Controls → Noise reduction. Fast = hqdn3d (spatial + temporal), High quality = nlmeans
/// (spatial, slow), Temporal = atadenoise (averages similar pixels over 9 frames).</summary>
public sealed class VideoDenoise
{
    public int Method { get; set; }
    public double Strength { get; set; } = 40;                              // 0 … 100 (50 ≈ twice the filters' defaults)
    public VideoDenoise Clone() => (VideoDenoise)MemberwiseClone();
}

/// <summary>How a slowed-down clip fills the frames between the media's own (Premiere's Time Interpolation): repeat the nearest frame,
/// blend the two around it, or make new in-between frames from the motion (optical flow, ffmpeg minterpolate).</summary>
public enum FrameInterp { Sample, Blend, OpticalFlow }

/// <summary>
/// Stabilized, denoised or interpolated (slow motion with frame blending / optical flow) video, rendered like <see cref="Remap"/> and read
/// the same way: frame k of the file is the stabilized media frame
/// <see cref="TimeMap.NativeIndex"/> picks for output frame k of the clip's range (transition handles included), at the media's display
/// size, lossless H.264 at the sequence rate. Two ffmpeg passes over the native frames the range uses (vid.stab: vidstabdetect →
/// motion file, vidstabtransform → smoothed camera path), on the same frame grid as Remap. The preview shows the unstabilized clip until the
/// file is ready (in the background, like a render bar), then plays the file; the export reads the same file — identical frames.
/// Speed / reverse / hold clips aren't stabilized (their own render), nor nested sequences. Files: %LOCALAPPDATA%\CutPro\Stab (20 GB).
/// </summary>
public static class Stab
{
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro", "Stab");
    const long CacheCap = 20L << 30;
    const string Version = "s3";

    /// <summary>Video rendered here (speed changes too: the media's frames are processed first, then picked like Remap does): stabilized,
    /// denoised, or slowed down with frame blending / optical flow.</summary>
    public static bool Active(Clip c) => c.Kind == ClipKind.Video && (c.Stab != null || Denoises(c) || Interpolates(c));
    public static bool Denoises(Clip c) => c.Denoise is { Strength: > 0 };
    /// <summary>Slower than the media plays (or time remapped) with blending / optical flow chosen.</summary>
    public static bool Interpolates(Clip c) => c.Interp != FrameInterp.Sample && !c.Hold && (TimeMap.Ramped(c) || TimeMap.IsRemapped(c) && c.Speed < 100 - 1e-9);

    static string Sig(MediaItem m, long[] idx, Stabilize? s, VideoDenoise? d, FrameInterp ip, double[]? src)
    {
        var fi = new FileInfo(m.Path);
        var (w, h) = Remap.Size(m);
        var head = Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"{m.Path.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{w}x{h}|{TimeUtil.Rate}|{TimeMap.NativeFps(m):0.######}|{s?.Smooth:0.###}|{s?.Tripod}|{s?.Edges}|{s?.Shake}|{s != null}|{d?.Method}|{d?.Strength:0.###}|{ip}|{Version}|"));
        var body = new byte[idx.Length * 8]; Buffer.BlockCopy(idx, 0, body, 0, body.Length);
        var tail = src == null ? Array.Empty<byte>() : new byte[src.Length * 8];
        if (src != null) Buffer.BlockCopy(src, 0, tail, 0, tail.Length);
        return Convert.ToHexString(SHA1.HashData(head.Concat(body).Concat(tail).ToArray()))[..24];
    }
    static string FileOf(string sig) => Path.Combine(Dir, sig + ".mkv");

    // ---------------- background rendering for the preview ----------------
    sealed class Job
    {
        public string Sig = "", File = "";
        public MediaItem M = null!; public long[] Idx = Array.Empty<long>(); public Stabilize? S; public VideoDenoise? D; public FrameInterp I; public double[]? Src;
        public volatile int Status;              // 0 waiting, 1 rendering, 2 ready, 3 failed
        public long Wanted;
        public string? Error;
    }
    static readonly object gate = new();
    static readonly Dictionary<string, Job> jobs = new();
    static readonly Dictionary<string, string> specSig = new();
    static Thread? worker;
    static readonly AutoResetEvent wake = new(false);
    /// <summary>"Stabilizing… 40%" while the worker renders (null when idle); <see cref="StatusChanged"/> on change.</summary>
    public static string? Status { get; private set; }
    public static event Action? StatusChanged;
    /// <summary>Tests: false = no background rendering.</summary>
    public static bool Background = true;

    static string Spec(Clip c, MediaItem m, double s, double e) => string.Join("|", Remap.Spec(c, m, s, e), c.In.ToString("R", CultureInfo.InvariantCulture), c.Start.ToString("R", CultureInfo.InvariantCulture),
        s.ToString("R", CultureInfo.InvariantCulture), e.ToString("R", CultureInfo.InvariantCulture), TimeUtil.Fps.ToString("R", CultureInfo.InvariantCulture),
        c.Stab?.Smooth.ToString("R", CultureInfo.InvariantCulture), c.Stab?.Tripod, c.Stab?.Edges, c.Stab?.Shake, c.Stab != null,
        Denoises(c) ? $"{c.Denoise!.Method}/{c.Denoise.Strength.ToString("R", CultureInfo.InvariantCulture)}" : "", Interpolates(c) ? c.Interp.ToString() : "");


    /// <summary>The stabilized file for the clip's range [s, e) (<see cref="Transitions.Plan.Range"/>), or null while it is rendered (queued).</summary>
    public static string? Ready(Clip c, MediaItem m, double s, double e)
    {
        var spec = Spec(c, m, s, e);
        lock (gate)
        {
            var j = Get(c, m, s, e, spec);
            j.Wanted = Environment.TickCount64;
            if (j.Status == 0 && Background) { StartWorker(); wake.Set(); }
            return j.Status == 2 ? j.File : null;
        }
    }
    /// <summary>Render state of the clip's current file: null = not stabilized, else (ready, failed, error).</summary>
    public static (bool ready, bool failed, string? error)? StateOf(Clip c, MediaItem m, double s, double e)
    {
        if (!Active(c)) return null;
        lock (gate) { var j = Get(c, m, s, e, Spec(c, m, s, e)); return (j.Status == 2, j.Status == 3, j.Error); }
    }
    static Job Get(Clip c, MediaItem m, double s, double e, string spec)
    {
        if (!specSig.TryGetValue(spec, out var sig))
        {
            var idx = Remap.Frames(c, m, s, e);
            double[]? src = Interpolates(c) ? Sources(c, m, s, e) : null;
            var dn = Denoises(c) ? c.Denoise!.Clone() : null;
            var ip = src != null ? c.Interp : FrameInterp.Sample;
            sig = Sig(m, idx, c.Stab, dn, ip, src);
            if (specSig.Count > 2000) specSig.Clear();
            specSig[spec] = sig;
            if (!jobs.ContainsKey(sig)) jobs[sig] = new Job { Sig = sig, File = FileOf(sig), M = m, Idx = idx, S = c.Stab?.Clone(), D = dn, I = ip, Src = src, Status = File.Exists(FileOf(sig)) ? 2 : 0 };
        }
        return jobs[sig];
    }

    static void StartWorker()
    {
        if (worker != null) return;
        worker = new Thread(WorkLoop) { IsBackground = true, Name = "Stab", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }
    static void WorkLoop()
    {
        while (true)
        {
            wake.WaitOne(1000);
            Thread.Sleep(400);                                      // let an edit settle first
            Job? j;
            lock (gate)
            {
                long now = Environment.TickCount64;
                j = jobs.Values.Where(x => x.Status == 0 && now - x.Wanted < 5000).OrderByDescending(x => x.Wanted).FirstOrDefault();
                if (j == null) continue;
                j.Status = 1;
            }
            string what = j.S != null ? "Stabilizing" : j.D != null ? "Reducing noise" : "Making in-between frames";
            Run(j, CancellationToken.None, new Progress<double>(p => SetStatus($"{what}… {p * 100:0}%")), background: true);
            lock (gate) if (!jobs.Values.Any(x => x.Status == 0)) SetStatus(null);
        }
    }
    static void SetStatus(string? s) { if (s == Status) return; Status = s; StatusChanged?.Invoke(); }

    static void Run(Job j, CancellationToken ct, IProgress<double>? progress, bool background)
    {
        try
        {
            if (!File.Exists(j.File)) Render(j.M, j.Idx, j.S, j.File, ct, progress, background, j.D, j.I, j.Src);
            j.Status = File.Exists(j.File) ? 2 : 3;
        }
        catch (OperationCanceledException) { j.Status = 0; throw; }
        catch (Exception ex) { j.Error = ex.Message; j.Status = 3; }
        StatusChanged?.Invoke();
    }

    // ---------------- export ----------------
    /// <summary>Stabilized files of every stabilized video clip the export shows (clip id → file); renders the missing ones (or waits for
    /// the background worker). Throws when one can't be rendered.</summary>
    public static Dictionary<string, string> PrepareExport(Editor ed, CancellationToken ct, IProgress<double>? progress = null)
    {
        var plan = Transitions.Compute(ed);
        var res = new Dictionary<string, string>();
        var todo = new List<(Clip c, Job j)>();
        foreach (var c in ed.Clips)
        {
            if (!Active(c) || ed.TrackOff[c.Track] || ed.MediaOf(c) is not { Offline: false, Seq: null } m) continue;
            var (s, e) = plan.Range(c);
            Job j;
            lock (gate) j = Get(c, m, s, e, Spec(c, m, s, e));
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
                Thread.Sleep(50);
            }
            if (j.Status != 2) throw new Exception($"Could not stabilize {ed.MediaOf(c)?.Name ?? "a clip"}" + (j.Error != null ? ": " + j.Error : "."));
            res[c.Id] = j.File;
        }
        return res;
    }

    // ---------------- rendering ----------------
    /// <summary>Pass 1 measures the motion of the native frames idx spans (vidstabdetect), pass 2 renders them stabilized (vidstabtransform)
    /// and the frames are written in idx order (non-decreasing for a 100 % clip: repeats where the sequence rate is higher) into a lossless
    /// file at the sequence rate — Remap's encoder and frame grid.</summary>
    public static void Render(MediaItem m, long[] idx, Stabilize? st, string file, CancellationToken ct, IProgress<double>? progress = null, bool background = false,
                              VideoDenoise? dn = null, FrameInterp ip = FrameInterp.Sample, double[]? src = null)
    {
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        Directory.CreateDirectory(Dir);
        var (w, h) = Remap.Size(m);
        double fpsN = TimeMap.NativeFps(m);
        string rate = TimeUtil.Rational(fpsN);
        long j0 = idx.Min(), j1 = idx.Max();
        bool interp = src != null && ip != FrameInterp.Sample;
        long lastN = Math.Max(0, (long)Math.Ceiling(m.Duration * fpsN - 1e-3) - 1);
        if (interp) { j0 = Math.Max(0, (long)Math.Floor(src!.Min() * fpsN) - 1); j1 = Math.Min(lastN, (long)Math.Ceiling(src!.Max() * fpsN) + 1); }
        int n = (int)(j1 - j0 + 1);
        string seek = Math.Max(0, j0 / fpsN - 1e-4).ToString("0.######", CultureInfo.InvariantCulture);
        string trf = Path.GetFileName(file) + ".trf";                       // (bare name: ffmpeg runs in the cache folder — no drive colon to escape)
        string grid = $"fps={rate},scale={w}:{h}:flags=bicubic,format=yuv420p";
        int smooth = st == null ? 1 : Math.Clamp((int)Math.Round(st.Smooth / 100 * fpsN), 1, 500);
        string zoom = st?.Edges switch { 2 => ":optzoom=0:crop=black", 1 => ":optzoom=2:zoomspeed=0.25", _ => ":optzoom=1" };
        string tripodD = st?.Tripod == true ? ":tripod=1" : "", tripodT = st?.Tripod == true ? ":tripod=1" : "";
        // the processing chain on the native grid: noise reduction first (on the pixels as shot), then the stabilizing warp
        string chain = grid + (dn != null ? "," + DenoiseFilter(dn, h) : "")
                     + (st != null ? $",vidstabtransform=input={trf}:smoothing={smooth}{(st.Tripod ? "" : ":optalgo=gauss")}{zoom}{tripodT}:interpol=bicubic" : "");
        var tmp = file + ".part.mkv";
        try
        {
            // pass 1: motion
            if (st != null)
            {
                RunPass(ff, background, ct, p => progress?.Report(p * 0.45), n, "-hide_banner", "-v", "error", "-nostdin", "-ss", seek, "-i", m.Path, "-an", "-sn", "-dn",
                        "-vf", $"{grid},vidstabdetect=shakiness={Math.Clamp(st.Shake, 1, 10)}:accuracy=15:stepsize=6:result={trf}{tripodD}", "-frames:v", n.ToString(), "-f", "null", "-");
                if (!File.Exists(Path.Combine(Dir, trf))) throw new Exception("motion analysis failed");
            }
            if (interp)
            {   // slow motion: the processed native frames → new frames at mult × the native rate (blended / optical flow) → a lossless segment that
                // Remap reads like media, picking for each output frame the in-between frame nearest its source time
                int mult = Mult(src!, fpsN);
                double hi = fpsN * mult;
                string iseg = file + ".iseg.mkv";
                string make = ip == FrameInterp.OpticalFlow ? $"minterpolate=fps={TimeUtil.Rational(hi)}:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1"
                                                            : $"framerate=fps={TimeUtil.Rational(hi)}:interp_start=0:interp_end=255:scene=100";
                RunPass(ff, background, ct, p => progress?.Report(0.45 + 0.35 * p), n * mult, "-hide_banner", "-v", "error", "-nostdin", "-ss", seek, "-i", m.Path, "-an", "-sn", "-dn",
                        "-vf", $"{chain},{make},format=yuv420p", "-frames:v", ((n - 1) * mult + 1).ToString(), "-c:v", "libx264", "-qp", "0", "-preset", "ultrafast", "-g", "15", "-y", iseg);
                if (!File.Exists(iseg)) throw new Exception("interpolation failed");
                int count = (n - 1) * mult + 1;
                var pick = src!.Select(sv => (long)Math.Clamp(Math.Round((sv - j0 / fpsN) * hi), 0, count - 1)).ToArray();
                var segMedia = new MediaItem { Path = iseg, Width = w, Height = h, Fps = hi, Duration = count / hi, Type = ClipKind.Video };
                var sub = progress == null ? null : new Progress<double>(p => progress.Report(0.8 + 0.2 * p));
                try { Remap.Render(segMedia, pick, file, ct, sub, background); }
                finally { try { File.Delete(iseg); } catch { } }
                progress?.Report(1);
                return;
            }
            // pass 2: the stabilized native frames → idx order → encoder; a speed change / reverse / hold (frames out of order): the stabilized
            // native frames in order into a lossless segment on the native grid, which Remap then reads like media (its frame list, its encoder)
            bool inOrder = idx.Zip(idx.Skip(1)).All(p => p.Second >= p.First);
            long[] order = inOrder ? idx : Enumerable.Range(0, n).Select(i => j0 + i).ToArray();
            string seg = file + ".seg.mkv", outFile = inOrder ? tmp : seg;
            var enc = Remap.Start(ff, background, "-y", "-hide_banner", "-v", "error", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-s", $"{w}x{h}", "-r", inOrder ? TimeUtil.Rate : rate,
                                  "-i", "-", "-an", "-c:v", "libx264", "-qp", "0", "-preset", "ultrafast", "-tune", "fastdecode", "-g", "15", "-pix_fmt", "yuv420p", outFile);
            var dec = StartIn(ff, background, "-hide_banner", "-v", "error", "-nostdin", "-ss", seek, "-i", m.Path, "-an", "-sn", "-dn",
                              "-vf", chain, "-frames:v", n.ToString(), "-f", "rawvideo", "pipe:1");
            try
            {
                int fb = w * h * 3 / 2;
                var cur = new byte[fb]; var next = new byte[fb]; long have = j0 - 1; bool any = false;
                var pipe = dec.StandardOutput.BaseStream; var dst = enc.StandardInput.BaseStream;
                bool Read(byte[] b) { int got = 0; while (got < fb) { int r = pipe.Read(b, got, fb - got); if (r <= 0) return false; got += r; } return true; }
                for (int k = 0; k < order.Length; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    while (have < order[k])
                    {
                        if (!Read(next)) break;                                  // (past the media's end: its last frame)
                        (cur, next) = (next, cur); have++; any = true;
                    }
                    if (!any) throw new Exception("no stabilized frames");
                    dst.Write(cur, 0, fb);
                    if ((k & 15) == 0) progress?.Report(0.45 + (inOrder ? 0.55 : 0.3) * k / order.Length);
                }
                dst.Close();
                enc.WaitForExit();
                if (enc.ExitCode != 0 || !File.Exists(outFile)) throw new Exception("encoder failed");
            }
            finally
            {
                try { if (!dec.HasExited) dec.Kill(); } catch { }
                try { if (!enc.HasExited) enc.Kill(); } catch { }
                dec.Dispose(); enc.Dispose();
            }
            if (inOrder) File.Move(tmp, file, true);
            else
            {
                var segMedia = new MediaItem { Path = seg, Width = w, Height = h, Fps = fpsN, Duration = n / fpsN, Type = ClipKind.Video };
                var sub = progress == null ? null : new Progress<double>(p => progress.Report(0.75 + 0.25 * p));
                try { Remap.Render(segMedia, idx.Select(i => i - j0).ToArray(), file, ct, sub, background); }
                finally { try { File.Delete(seg); } catch { } }
            }
            progress?.Report(1);
        }
        finally
        {
            try { File.Delete(Path.Combine(Dir, trf)); } catch { }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            Trim();
        }
    }

    /// <summary>The source time each output frame of the clip's range [s, e) shows (interpolation picks the in-between frame nearest it).</summary>
    static double[] Sources(Clip c, MediaItem m, double s, double e)
    {
        int n = Math.Max(1, (int)Math.Round((e - s) * TimeUtil.Fps));
        double last = Math.Max(0, m.Duration - 1 / TimeMap.NativeFps(m));
        var r = new double[n];
        for (int k = 0; k < n; k++) r[k] = Math.Round(Math.Clamp(TimeMap.Src(c, s + k / TimeUtil.Fps), 0, last), 9);
        return r;
    }
    /// <summary>How many in-between frames per native frame: enough that the nearest one is within ~1/12 frame of every wanted time (2 … 8).</summary>
    static int Mult(double[] src, double fpsN)
    {
        double step = double.MaxValue;
        for (int k = 1; k < src.Length; k++) { double d = Math.Abs(src[k] - src[k - 1]) * fpsN; if (d > 1e-6) step = Math.Min(step, d); }
        return step == double.MaxValue ? 2 : Math.Clamp((int)Math.Ceiling(1 / Math.Min(1, step)) * 2, 2, 8);
    }
    /// <summary>The ffmpeg filter of a noise reduction (sizes scale with the picture's height like every effect: × h / 720).</summary>
    public static string DenoiseFilter(VideoDenoise d, int h)
    {
        double k = Math.Clamp(d.Strength, 0, 100) / 100, z = Math.Max(0.5, h / 720.0);
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        return d.Method switch
        {
            1 => $"nlmeans=s={F(1 + 15 * k)}:p={(z > 1.2 ? 9 : 7)}:pc=5:r={(z > 1.2 ? 11 : 9)}:rc=7",
            2 => $"atadenoise=0a={F(0.3 * k)}:0b={F(0.6 * k)}:1a={F(0.3 * k)}:1b={F(0.6 * k)}:2a={F(0.3 * k)}:2b={F(0.6 * k)}:s=9",
            _ => $"hqdn3d={F(16 * k * z)}:{F(12 * k * z)}:{F(24 * k)}:{F(18 * k)}",
        };
    }

    static Process StartIn(string ff, bool background, params string[] args)
    {
        var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Dir };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        if (background) try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        return p;
    }

    /// <summary>A pass with no output but its side file; progress from ffmpeg's frame counter.</summary>
    static void RunPass(string ff, bool background, CancellationToken ct, Action<double> progress, int frames, params string[] args)
    {
        var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Dir };
        foreach (var a in args.Take(3)) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-progress", "pipe:1", "-nostats" }) psi.ArgumentList.Add(a);
        foreach (var a in args.Skip(3)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); }; p.BeginErrorReadLine();
        if (background) try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        try
        {
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                ct.ThrowIfCancellationRequested();
                if (line.StartsWith("frame=") && int.TryParse(line[6..], out var f)) progress(Math.Min(1, f / (double)Math.Max(1, frames)));
            }
            p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception(err.ToString().Trim() is { Length: > 0 } msg ? msg.Split('\n')[^1] : "ffmpeg failed");
        }
        finally { try { if (!p.HasExited) p.Kill(); } catch { } }
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
