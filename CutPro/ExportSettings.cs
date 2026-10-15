using System.Diagnostics;
using System.Globalization;

namespace CutPro;

/// <summary>What the exporter writes: container, codecs, encoder, quality, output height (the picture is rendered at that
/// size, not upscaled), range. Presets cover the major platforms' recommended upload specs.</summary>
public sealed class ExportSettings
{
    public string Name { get; set; } = "Match sequence (H.264)";
    public string Container { get; set; } = "mp4";        // mp4 mov mkv webm | wav mp3 m4a (audio only)
    public string Video { get; set; } = "h264";           // h264 hevc av1 vp9 prores none
    public string Encoder { get; set; } = "software";     // software nvenc qsv amf
    public bool ConstantQuality { get; set; } = true;
    public int Quality { get; set; } = 18;                // CRF / CQ (lower = better)
    public double BitrateMbps { get; set; } = 12;
    public string Speed { get; set; } = "medium";         // x264/x265 preset; mapped for hardware encoders
    public string Audio { get; set; } = "aac";            // aac opus mp3 pcm flac none
    public int AudioKbps { get; set; } = 192;
    public int Height { get; set; }                       // 0 = the sequence's output height
    public bool InOutOnly { get; set; }
    /// <summary>Aspect the preset is made for (0 = any): a 9:16 preset on a 16:9 sequence asks to switch the format first.</summary>
    public double Aspect { get; set; }
    public string? Note { get; set; }
    /// <summary>Loudness normalization of the exported sound: integrated loudness (BS.1770) to <see cref="Lufs"/>, peaks limited to <see cref="TruePeak"/> dBTP.</summary>
    public bool Normalize { get; set; }
    public double Lufs { get; set; } = -14;
    public double TruePeak { get; set; } = -1;
    /// <summary>Filled by <see cref="Exporter.RunAsync"/> from the measurement (gain, limiter needed, what was measured) — not user settings.</summary>
    public double NormGainDb { get; set; }
    public bool NormLimit { get; set; }
    public Loudness.Result? Measured { get; set; }

    /// <summary>Composite in 4:4:4 (full colour: titles, keys and overlays keep their chroma; the encoder subsamples once at the end) — false =
    /// 4:2:0 throughout (a little faster, softer colour edges).</summary>
    public bool Full444 { get; set; } = true;
    /// <summary>Transparent background (a nested sequence's render: <see cref="Nest"/>): the composite keeps alpha, yuva444p FFV1 (yuva420p with 4:2:0).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Alpha { get; set; }
    /// <summary>A background render (nested sequences while editing): ffmpeg runs below normal priority, so playback keeps its CPU.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Background { get; set; }
    /// <summary>Captions (caption-role titles) drawn into the picture; and / or written as an .srt next to the file (<see cref="Captioning"/>).</summary>
    public bool BurnCaptions { get; set; } = true;
    public bool CaptionFile { get; set; }
    public bool AudioOnly => Video == "none";
    public int OutH => Height > 0 ? Even(Height) : Editor.OutH;
    public int OutW => Height > 0 ? Even(Editor.W * (double)Height / Editor.H) : Editor.OutW;
    /// <summary>Output pixels per logical (preview) pixel.</summary>
    public double Scale => OutH / (double)Editor.H;
    public static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);
    public ExportSettings Clone() => (ExportSettings)MemberwiseClone();

    public static readonly ExportSettings Default = new();

    /// <summary>Platform presets (2026 upload recommendations: H.264 high profile, AAC 48 kHz, frame rate of the sequence).</summary>
    public static ExportSettings[] Presets => new[]
    {
        new ExportSettings { Name = "Match sequence (H.264)", Note = "High quality, plays everywhere" },
        new ExportSettings { Name = "YouTube 1080p", Normalize = true, Height = 1080, ConstantQuality = false, BitrateMbps = 12, AudioKbps = 384, Aspect = 16 / 9.0, Note = "YouTube recommends 8–12 Mbps for 1080p" },
        new ExportSettings { Name = "YouTube 4K", Normalize = true, Height = 2160, ConstantQuality = false, BitrateMbps = 45, AudioKbps = 384, Aspect = 16 / 9.0, Note = "35–68 Mbps for 2160p" },
        new ExportSettings { Name = "YouTube Shorts", Normalize = true, Height = 1920, ConstantQuality = false, BitrateMbps = 12, AudioKbps = 256, Aspect = 9 / 16.0, Note = "Vertical 1080×1920, up to 3 minutes" },
        new ExportSettings { Name = "TikTok", Normalize = true, Height = 1920, ConstantQuality = false, BitrateMbps = 10, AudioKbps = 192, Aspect = 9 / 16.0, Note = "1080×1920, up to 10 minutes" },
        new ExportSettings { Name = "Instagram Reels", Normalize = true, Height = 1920, ConstantQuality = false, BitrateMbps = 10, AudioKbps = 192, Aspect = 9 / 16.0, Note = "1080×1920; under 90 s gets the most reach" },
        new ExportSettings { Name = "Instagram feed (4:5)", Normalize = true, Height = 1350, ConstantQuality = false, BitrateMbps = 8, AudioKbps = 192, Aspect = 4 / 5.0, Note = "1080×1350 portrait post" },
        new ExportSettings { Name = "Facebook (1080p)", Normalize = true, Height = 1080, ConstantQuality = false, BitrateMbps = 10, AudioKbps = 192, Aspect = 16 / 9.0 },
        new ExportSettings { Name = "Facebook Reels", Normalize = true, Height = 1920, ConstantQuality = false, BitrateMbps = 10, AudioKbps = 192, Aspect = 9 / 16.0, Note = "1080×1920, up to 90 s" },
        new ExportSettings { Name = "X (Twitter)", Normalize = true, Height = 1080, ConstantQuality = false, BitrateMbps = 8, AudioKbps = 128, Note = "Up to 1920×1200, 2 min 20 s (longer with Premium)" },
        new ExportSettings { Name = "LinkedIn", Normalize = true, Height = 1080, ConstantQuality = false, BitrateMbps = 8, AudioKbps = 192, Note = "Up to 15 minutes" },
        new ExportSettings { Name = "Vimeo (high quality)", Quality = 16, AudioKbps = 320, Speed = "slow" },
        new ExportSettings { Name = "HEVC / H.265 (smaller file)", Video = "hevc", Quality = 22, Note = "About half the size of H.264 at the same quality" },
        new ExportSettings { Name = "AV1 (web)", Video = "av1", Quality = 30, Container = "mp4", Note = "Needs an AV1-capable ffmpeg (libsvtav1 or NVENC AV1)" },
        new ExportSettings { Name = "ProRes 422 HQ master (.mov)", Video = "prores", Container = "mov", Audio = "pcm", Note = "Large files for further editing" },
        new ExportSettings { Name = "ProRes 4444 master (.mov)", Video = "prores4444", Container = "mov", Audio = "pcm", Note = "Full colour (4:4:4, 10-bit) for mastering and further editing; very large files" },
        new ExportSettings { Name = "WebM (VP9)", Video = "vp9", Container = "webm", Audio = "opus", Quality = 31 },
        new ExportSettings { Name = "Audio only — WAV", Video = "none", Container = "wav", Audio = "pcm" },
        new ExportSettings { Name = "Audio only — MP3 320 kbps", Video = "none", Container = "mp3", Audio = "mp3", AudioKbps = 320 },
    };

    public static readonly string[] Containers = { "mp4", "mov", "mkv", "webm" };
    public static readonly string[] AudioContainers = { "wav", "mp3", "m4a" };

    /// <summary>ffmpeg video encoder name for a codec + encoder choice.</summary>
    public string VideoEncoder => (Video, Encoder) switch
    {
        ("h264", "nvenc") => "h264_nvenc", ("hevc", "nvenc") => "hevc_nvenc", ("av1", "nvenc") => "av1_nvenc",
        ("h264", "qsv") => "h264_qsv", ("hevc", "qsv") => "hevc_qsv", ("av1", "qsv") => "av1_qsv",
        ("h264", "amf") => "h264_amf", ("hevc", "amf") => "hevc_amf", ("av1", "amf") => "av1_amf",
        ("hevc", _) => "libx265", ("av1", _) => "libsvtav1", ("vp9", _) => "libvpx-vp9", ("prores", _) or ("prores4444", _) => "prores_ks", ("ffv1", _) => "ffv1",
        _ => "libx264",
    };

    /// <summary>The encoder's pixel format (OutputArgs' -pix_fmt), null when it names none.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? OutPixFmt { get { var a = OutputArgs(); int i = a.IndexOf("-pix_fmt"); return i >= 0 && i + 1 < a.Count ? a[i + 1] : null; } }

    /// <summary>Output-side ffmpeg arguments (codecs, quality, rate, container flags).</summary>
    public List<string> OutputArgs()
    {
        var a = new List<string>();
        if (!AudioOnly)
        {
            string enc = VideoEncoder;
            a.AddRange(new[] { "-c:v", enc });
            string kbps = $"{(int)(BitrateMbps * 1000)}k", max = $"{(int)(BitrateMbps * 1500)}k", buf = $"{(int)(BitrateMbps * 2000)}k";
            switch (enc)
            {
                case "libx264" or "libx265":
                    a.AddRange(new[] { "-preset", Speed });
                    if (ConstantQuality) a.AddRange(new[] { "-crf", Quality.ToString() });
                    else a.AddRange(new[] { "-b:v", kbps, "-maxrate", max, "-bufsize", buf });
                    if (enc == "libx264") a.AddRange(new[] { "-profile:v", "high" });
                    if (enc == "libx265") a.AddRange(new[] { "-tag:v", "hvc1" });
                    a.AddRange(new[] { "-pix_fmt", "yuv420p" });
                    break;
                case "libsvtav1":
                    a.AddRange(new[] { "-preset", Speed switch { "slow" or "slower" or "veryslow" => "5", "fast" or "faster" or "veryfast" or "ultrafast" => "10", _ => "8" } });
                    if (ConstantQuality) a.AddRange(new[] { "-crf", Quality.ToString() }); else a.AddRange(new[] { "-b:v", kbps });
                    a.AddRange(new[] { "-pix_fmt", "yuv420p" });
                    break;
                case "libvpx-vp9":
                    if (ConstantQuality) a.AddRange(new[] { "-crf", Quality.ToString(), "-b:v", "0" }); else a.AddRange(new[] { "-b:v", kbps });
                    a.AddRange(new[] { "-row-mt", "1", "-deadline", "good", "-cpu-used", Speed is "slow" or "slower" or "veryslow" ? "1" : "3", "-pix_fmt", "yuv420p" });
                    break;
                case "prores_ks":
                    a.AddRange(Video == "prores4444" ? new[] { "-profile:v", "4", "-vendor", "apl0", "-pix_fmt", "yuv444p10le" }     // (4444: full chroma)
                                                     : new[] { "-profile:v", "3", "-vendor", "apl0", "-pix_fmt", "yuv422p10le" });
                    break;
                case "ffv1":       // lossless, every frame a key frame (instant seeks), slices for threaded decoding
                    a.AddRange(new[] { "-level", "3", "-g", "1", "-slicecrc", "0", "-slices", OutH >= 480 ? "16" : "4", "-pix_fmt", Alpha ? (Full444 ? "yuva444p" : "yuva420p") : Full444 ? "yuv444p" : "yuv420p" });
                    break;
                default:   // hardware encoders
                    string hwPreset = Encoder == "nvenc" ? Speed switch { "slow" or "slower" or "veryslow" => "p6", "fast" or "faster" or "veryfast" or "ultrafast" => "p2", _ => "p4" }
                                    : Encoder == "qsv" ? Speed switch { "slow" or "slower" or "veryslow" => "slow", "fast" or "faster" or "veryfast" or "ultrafast" => "fast", _ => "medium" }
                                    : Speed switch { "slow" or "slower" or "veryslow" => "quality", "fast" or "faster" or "veryfast" or "ultrafast" => "speed", _ => "balanced" };
                    a.AddRange(new[] { Encoder == "amf" ? "-quality" : "-preset", hwPreset });
                    if (ConstantQuality)
                    {
                        if (Encoder == "nvenc") a.AddRange(new[] { "-rc", "vbr", "-cq", Quality.ToString(), "-b:v", "0" });
                        else if (Encoder == "qsv") a.AddRange(new[] { "-global_quality", Quality.ToString() });
                        else a.AddRange(new[] { "-rc", "cqp", "-qp_i", Quality.ToString(), "-qp_p", Quality.ToString() });
                    }
                    else a.AddRange(new[] { "-b:v", kbps, "-maxrate", max, "-bufsize", buf });
                    a.AddRange(new[] { "-pix_fmt", Encoder == "qsv" ? "nv12" : "yuv420p" });
                    break;
            }
            a.AddRange(new[] { "-r", TimeUtil.Rate });
        }
        switch (Audio)
        {
            case "none": a.Add("-an"); break;
            case "pcm": a.AddRange(new[] { "-c:a", Container == "wav" ? "pcm_s16le" : "pcm_s24le" }); break;
            case "flac": a.AddRange(new[] { "-c:a", "flac" }); break;
            case "mp3": a.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", $"{AudioKbps}k" }); break;
            case "opus": a.AddRange(new[] { "-c:a", "libopus", "-b:a", $"{AudioKbps}k" }); break;
            default: a.AddRange(new[] { "-c:a", "aac", "-b:a", $"{AudioKbps}k" }); break;
        }
        a.AddRange(new[] { "-ar", "48000" });
        if (Container is "mp4" or "mov" or "m4a") a.AddRange(new[] { "-movflags", "+faststart" });
        return a;
    }

    public string Extension => "." + Container;

    /// <summary>Rough file size for the dialog.</summary>
    public string EstimateSize(double seconds)
    {
        double vMbps = AudioOnly ? 0 : ConstantQuality ? Video switch
        {
            "prores" => 220 * Math.Pow(OutH / 1080.0, 2), "prores4444" => 330 * Math.Pow(OutH / 1080.0, 2), "hevc" => 6 * Math.Pow(OutH / 1080.0, 1.6), "av1" => 4.5 * Math.Pow(OutH / 1080.0, 1.6),
            _ => 10 * Math.Pow(OutH / 1080.0, 1.6) * Math.Pow(2, (18 - Quality) / 6.0),
        } : BitrateMbps;
        double aMbps = Audio == "pcm" ? 1.536 : Audio == "none" ? 0 : AudioKbps / 1000.0;
        double mb = (vMbps + aMbps) * seconds / 8;
        return mb >= 1000 ? $"≈ {mb / 1000:0.0} GB" : $"≈ {Math.Max(0.1, mb):0.#} MB";
    }

    // ---------------- which encoders this ffmpeg / GPU can use (measured once) ----------------
    static HashSet<string>? usable;
    /// <summary>Encoders that are both built into ffmpeg and actually work on this PC (a hardware encoder can be compiled in
    /// but have no GPU behind it) — each is tried on a tiny test clip.</summary>
    public static async Task<HashSet<string>> UsableEncodersAsync()
    {
        if (usable != null) return usable;
        var ff = Exporter.FindFfmpeg();
        var set = new HashSet<string> { "libx264" };
        if (ff == null) return usable = set;
        string list = await Task.Run(() => Run(ff, "-hide_banner -encoders").output);
        var candidates = new[] { "libx265", "libsvtav1", "libvpx-vp9", "prores_ks", "h264_nvenc", "hevc_nvenc", "av1_nvenc", "h264_qsv", "hevc_qsv", "av1_qsv", "h264_amf", "hevc_amf", "av1_amf" }
            .Where(e => list.Contains(" " + e + " ")).ToList();
        foreach (var e in candidates)
        {
            var (code, _) = await Task.Run(() => Run(ff, $"-hide_banner -v error -f lavfi -i color=black:s=256x144:d=0.2 -frames:v 3 -c:v {e} -f null -"));
            if (code == 0) set.Add(e);
        }
        return usable = set;
    }

    static (int code, string output) Run(string ff, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(ff, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            var o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return (-1, o); }
            return (p.ExitCode, o + err.Result);
        }
        catch { return (-1, ""); }
    }
}
