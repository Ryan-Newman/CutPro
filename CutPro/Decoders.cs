using System.Diagnostics;
using System.Globalization;

namespace CutPro;

/// <summary>How ffmpeg decodes a file: software (frame or slice threads) or hardware (NVIDIA CUDA, D3D11VA).</summary>
public enum Decoder { SwFrame, SwSlice, Cuda, CudaScale, D3D11 }

/// <summary>
/// Decoder selection. Files above 1080p are benchmarked once at import (<see cref="Calibrate"/>): every candidate
/// decodes 31 frames from the middle of the file and we time the first frame (what scrubbing / stopping on a frame
/// costs) and the rest (playback throughput). Measured on an RTX 4090 + Ryzen 7800X3D with 4K60 10-bit HEVC:
/// default software needs ~250 ms of frame-thread setup before the first frame; slice threads or CUDA avoid it,
/// and CUDA with GPU scaling has the best throughput. For 1080p and below plain software is fastest to start.
/// </summary>
public static class Decoders
{
    public static string Label(Decoder d) => d switch
    {
        Decoder.Cuda or Decoder.CudaScale => "NVIDIA GPU",
        Decoder.D3D11 => "GPU (D3D11)",
        Decoder.SwSlice => "CPU (slice threads)",
        _ => "CPU",
    };

    public static bool IsHardware(Decoder d) => d is Decoder.Cuda or Decoder.CudaScale or Decoder.D3D11;

    /// <summary>ffmpeg options placed before -i.</summary>
    public static string[] InputArgs(Decoder d) => d switch
    {
        Decoder.SwSlice => new[] { "-thread_type", "slice" },
        Decoder.Cuda => new[] { "-hwaccel", "cuda" },
        Decoder.CudaScale => new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" },
        Decoder.D3D11 => new[] { "-hwaccel", "d3d11va" },
        _ => Array.Empty<string>(),
    };

    /// <summary>Input options for the exporter (its filters run on the CPU, so frames must come back from the GPU).</summary>
    public static string[] ExportInputArgs(Decoder d) => InputArgs(d == Decoder.CudaScale ? Decoder.Cuda : d);

    /// <summary>Filter chain producing exactly w x h frames (letterboxed) at the sequence rate, in pixel format pixFmt.</summary>
    /// <param name="rate">frame rate (default: the sequence rate; the scrub cache keeps its own fixed rate)</param>
    /// <param name="extra">a filter graph run on the scaled frame before the letterbox (a clip's cartoon / cel / anime look)</param>
    /// <param name="premul">the picture has transparent parts (a nested sequence's render): scale it premultiplied, like the export</param>
    public static string Filter(Decoder d, int w, int h, string pixFmt, string? rate = null, string? extra = null, bool premul = false)   // (bgra: the padding is transparent)
    {
        string fps = $"fps={rate ?? TimeUtil.Rate}", pad = $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2" + (pixFmt == "bgra" ? ":color=black@0" : ""), x = string.IsNullOrEmpty(extra) ? "" : extra + ",";
        string scale = $"scale={w}:{h}:force_original_aspect_ratio=decrease:flags=bilinear";
        if (premul) scale = $"format=gbrap,premultiply=inplace=1,{scale},unpremultiply=inplace=1";
        return d == Decoder.CudaScale
            ? $"scale_cuda={w}:{h}:force_original_aspect_ratio=decrease:format=nv12,hwdownload,format=nv12,{fps},{x}{pad},format={pixFmt}"
            : $"{fps},{scale},{x}{pad},format={pixFmt}";
    }

    /// <summary>True for media worth benchmarking (above 1080p).</summary>
    public static bool IsHeavy(MediaItem m) => (long)m.Width * m.Height > 2_500_000;

    /// <summary>Benchmarks decoders for a heavy file and sets m.SeekDecoder / m.PlayDecoder. Blocking; call off the UI thread.</summary>
    public static void Calibrate(MediaItem m)
    {
        var ff = Exporter.FindFfmpeg();
        if (ff == null || !IsHeavy(m)) return;
        int w = Math.Max(2, (int)Math.Round(Math.Min(1280.0 / m.Width, 720.0 / m.Height) * m.Width / 2) * 2);
        int h = Math.Max(2, (int)Math.Round(Math.Min(1280.0 / m.Width, 720.0 / m.Height) * m.Height / 2) * 2);
        double at = m.Duration * 0.4;
        var cands = new List<Decoder> { Decoder.SwFrame, Decoder.SwSlice, Decoder.Cuda, Decoder.D3D11 };
        if (m.Rotation % 360 == 0) cands.Add(Decoder.CudaScale);         // GPU scaling can't follow ffmpeg's auto-rotate
        var results = new List<(Decoder d, double first, double fps)>();
        foreach (var d in cands)
        {
            var r = Measure(ff, m.Path, d, at, w, h, 31);
            if (r != null) results.Add((d, r.Value.first, r.Value.fps));
        }
        // (a hardware decoder that can't take the file falls back to software, and then scale_cuda fails: success here means the GPU decodes
        // and scales it — the export shrinks such files on the GPU. The preview's CudaScale downloads nv12, which would cut HDR to 8 bits
        // before the tone mapping, so HDR files never play through it)
        m.GpuScaleOk = results.Any(r => r.d == Decoder.CudaScale);
        if (m.Hdr != null) results.RemoveAll(r => r.d == Decoder.CudaScale);
        if (results.Count == 0) return;
        // scrub/stop: fastest first frame (but must decode forward at least ~realtime for stepping)
        var seek = results.Where(r => r.fps >= 20).DefaultIfEmpty(results[0]).MinBy(r => r.first);
        // playback: must sustain 1.5x realtime; among those, quickest to start; otherwise the fastest decoder
        var okPlay = results.Where(r => r.fps >= TimeUtil.Fps * 1.5).ToList();
        // playback weights sustained throughput (CPU headroom) over start time: GPU decoders win for heavy 4K
        var play = okPlay.Count > 0 ? okPlay.MinBy(r => r.first + 12000.0 / r.fps) : results.MaxBy(r => r.fps);
        m.SeekDecoder = seek.d; m.PlayDecoder = play.d;
        m.DecodeNote = string.Join("  ", results.Select(r => $"{r.d}:{r.first:0}ms/{r.fps:0}fps"));
    }

    /// <summary>Decodes n frames from `at`; returns ms to the first frame and fps of the rest, or null if the decoder fails.</summary>
    static (double first, double fps)? Measure(string ff, string path, Decoder d, double at, int w, int h, int n)
    {
        try
        {
            var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-v", "error", "-nostdin" }.Concat(InputArgs(d))
                     .Concat(new[] { "-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-i", path, "-an", "-sn", "-dn",
                                     "-vf", Filter(d, w, h, "bgr0"), "-frames:v", n.ToString(), "-f", "rawvideo", "pipe:1" }))
                psi.ArgumentList.Add(a);
            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            var s = p.StandardOutput.BaseStream;
            int size = w * h * 4, frames = 0; long got = 0; double first = -1;
            var buf = new byte[1 << 20];
            while (true)
            {
                int k = s.Read(buf, 0, buf.Length);
                if (k <= 0) break;
                got += k;
                if (first < 0 && got >= size) first = sw.Elapsed.TotalMilliseconds;
                frames = (int)(got / size);
                if (sw.ElapsedMilliseconds > 8000) { try { p.Kill(); } catch { } return null; }
            }
            p.WaitForExit();
            double total = sw.Elapsed.TotalMilliseconds;
            if (p.ExitCode != 0 || frames < n || first < 0) return null;
            return (first, (n - 1) / Math.Max(0.001, (total - first) / 1000));
        }
        catch { return null; }
    }
}
