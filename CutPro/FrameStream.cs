using System.Diagnostics;
using System.Globalization;

namespace CutPro;

/// <summary>Shared pool of frame buffers so decoders don't churn the large-object heap (GC pauses, memory growth).</summary>
public static class FramePool
{
    static readonly Dictionary<int, Stack<byte[]>> pools = new();
    static long pooled;
    const long Cap = 256L << 20;
    public static byte[] Rent(int size)
    {
        lock (pools) if (pools.TryGetValue(size, out var s) && s.Count > 0) { pooled -= size; return s.Pop(); }
        return GC.AllocateUninitializedArray<byte>(size);
    }
    public static void Return(byte[] b)
    {
        lock (pools)
        {
            if (pooled + b.Length > Cap) return;
            if (!pools.TryGetValue(b.Length, out var s)) pools[b.Length] = s = new();
            s.Push(b); pooled += b.Length;
        }
    }
}

/// <summary>
/// Decodes a video file from a source time into fixed-size BGR32 frames at the sequence frame rate through an
/// ffmpeg pipe. Frame k shows source time Start + k/fps. A reader thread decodes up to Depth frames past the last
/// requested one; KeepBehind recent frames stay cached (paused: stepping back and dragging a clip under the
/// playhead are served from memory; playing: 0).
/// </summary>
public sealed class FrameStream : IDisposable
{
    public readonly string Path;
    public readonly int W, H;
    public readonly double Start;
    const int Window = 45;              // how far ahead (frames) we decode forward instead of restarting
    readonly string ffmpeg;
    readonly List<(long idx, byte[] buf)> frames = new();   // contiguous, ascending idx
    readonly long early, born;
    Process? proc;
    long produced, lastWant, lastTaken = -1;
    int depth, keep;
    volatile bool disposed;
    public volatile bool Delivered;
    /// <summary>True once the decoder has no more frames (end of media or error).</summary>
    public volatile bool Ended;
    /// <summary>Milliseconds from creation to the first decoded frame (-1 until then).</summary>
    public long FirstFrameMs = -1;

    /// <param name="src">source time the caller wants first</param>
    /// <param name="offset">start decoding this many seconds after src (&gt;0: playback restarts begin ahead of the playhead by
    /// the decoder's startup time so frames arrive on time) or before it (&lt;0: paused, cache a little history)</param>
    public readonly Decoder Dec;

    /// <summary>Frames per second of source time (0 = the sequence rate). Remapped clips that aren't rendered yet decode the media on its
    /// native frame grid (<see cref="TimeMap.NativeIndex"/>): the stream then starts just before a native frame, like <see cref="Remap"/>'s decoder.</summary>
    public readonly double Rate;

    /// <summary>Extra filter graph on the decoded frames (a clip's drawn look; part of the stream's identity).</summary>
    public readonly string Vf;

    /// <summary>Frames carry a matte (chroma key): straight BGRA instead of BGR0.</summary>
    public readonly bool Alpha;

    public FrameStream(string ffmpeg, string path, double src, int w, int h, int depth, int keepBehind, double offset = 0, Decoder dec = Decoder.SwFrame, double rate = 0, string? vf = null, bool alpha = false)
    {
        this.ffmpeg = ffmpeg; Path = path; W = w; H = h; this.depth = depth; keep = keepBehind; Dec = dec;
        Rate = rate > 0 ? rate : TimeUtil.Fps; Vf = vf ?? ""; Alpha = alpha;
        Start = rate > 0 ? Math.Max(0, Math.Floor((src + offset) * rate + 1e-3) / rate - 1e-4) : Math.Max(0, src + offset);
        early = offset > 0 ? (long)Math.Ceiling(offset * Rate) + 1 : 0;
        lastWant = Math.Max(0, Index(src));
        born = Environment.TickCount64;
        new Thread(ReadLoop) { IsBackground = true, Name = "FrameStream" }.Start();   // process start stays off the UI thread
    }

    /// <summary>Max frames decoded past the last requested one.</summary>
    public int Depth { set { if (value == depth) return; lock (frames) { depth = value; Monitor.PulseAll(frames); } } }
    /// <summary>Frames older than the one on screen that stay cached.</summary>
    public int KeepBehind { set { lock (frames) keep = value; } }

    public long Index(double src) => (long)Math.Floor((src - Start) * Rate + 1e-3);

    void ReadLoop()
    {
        byte[]? buf = null;
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-v", "error", "-nostdin" }.Concat(Decoders.InputArgs(Dec)).Concat(new[] {
                "-ss", Start.ToString("0.######", CultureInfo.InvariantCulture), "-i", Path, "-an", "-sn", "-dn",
                "-vf", Decoders.Filter(Dec, W, H, Alpha ? "bgra" : "bgr0", Math.Abs(Rate - TimeUtil.Fps) < 1e-9 ? null : TimeUtil.Rational(Rate), Vf, Alpha && Nest.IsRender(Path)),
                "-f", "rawvideo", "pipe:1" })) psi.ArgumentList.Add(a);
            var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            proc = p;
            if (disposed) { Kill(p); return; }
            var s = p.StandardOutput.BaseStream;
            int size = W * H * 4;
            while (!disposed)
            {
                lock (frames) while (!disposed && produced - 1 - lastWant >= depth) Monitor.Wait(frames);
                if (disposed) return;
                buf = FramePool.Rent(size);
                int got = 0;
                while (got < size) { int n = s.Read(buf, got, size - got); if (n <= 0) return; got += n; }
                lock (frames)
                {
                    if (disposed) return;
                    frames.Add((produced++, buf)); buf = null;
                    if (!Delivered) FirstFrameMs = Environment.TickCount64 - born;
                    Delivered = true; Monitor.PulseAll(frames);
                }
            }
        }
        catch { }
        finally { Ended = true; if (buf != null) FramePool.Return(buf); }
    }

    /// <summary>The newest decoded frame at or before src (null if none yet). The buffer stays owned by the stream:
    /// copy it before the next Take/Dispose. Frames older than the result minus KeepBehind are released.</summary>
    public (long idx, byte[]? buf) Take(double src)
    {
        long want = Index(src);
        lock (frames)
        {
            long lw = Math.Max(want, 0);
            if (lw != lastWant) { lastWant = lw; Monitor.PulseAll(frames); }
            int bi = -1;
            for (int i = 0; i < frames.Count && frames[i].idx <= want; i++) bi = i;
            if (bi < 0) return (-1, null);
            var best = frames[bi];
            int drop = 0;
            while (drop < frames.Count && frames[drop].idx < best.idx - keep) FramePool.Return(frames[drop++].buf);
            if (drop > 0) frames.RemoveRange(0, drop);
            lastTaken = best.idx;
            return best;
        }
    }

    public string DebugState { get { lock (frames) return $"start={Start:0.000} frames={(frames.Count > 0 ? $"{frames[0].idx}..{frames[^1].idx}" : "-")} lastTaken={lastTaken} lastWant={lastWant} produced={produced} keep={keep}"; } }

    /// <summary>True if src is cached or reachable by decoding forward (no restart needed).</summary>
    public bool CanServe(double src)
    {
        long w = Index(src);
        lock (frames)
        {
            long floor = frames.Count > 0 ? frames[0].idx : Math.Max(lastTaken, 0);
            if (lastTaken < 0) floor = Math.Min(floor, 0) - early;   // nothing shown yet: everything from Start - lead is coming
            return w >= floor && w <= produced + Window;
        }
    }

    /// <summary>True if the frame for src is decoded (cached or queued).</summary>
    public bool HasFrameFor(double src)
    {
        long w = Index(src);
        lock (frames) return frames.Count > 0 && frames[0].idx <= w && frames[^1].idx >= w;
    }

    static void Kill(Process p) { try { if (!p.HasExited) p.Kill(); } catch { } p.Dispose(); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (frames) { foreach (var f in frames) FramePool.Return(f.buf); frames.Clear(); Monitor.PulseAll(frames); }
        var p = proc;
        if (p != null) Task.Run(() => Kill(p));
    }
}
