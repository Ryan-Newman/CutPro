using System.Diagnostics;
using System.Globalization;

namespace CutPro;

/// <summary>What the exporter writes: container, codecs, encoder, quality, output height (the picture is rendered at that
/// size, not upscaled), range. Presets cover the major platforms' recommended upload specs.</summary>
public sealed class ExportSettings
{
    public string Name { get; set; } = "Match sequence (H.264)";
    public string Container { get; set; } = "mp4";        // mp4 mov mkv webm | wav mp3 m4a (audio only)
    public string Video { get; set; } = "h264";           // h264 hevc av1 vp9 prores prores4444 dnxhr_* mpeg2_xdcam mpeg2_imx | with alpha: prores4444a vp9a qtrle png | none
    public string Encoder { get; set; } = "software";     // software nvenc qsv amf
    public bool ConstantQuality { get; set; } = true;
    public int Quality { get; set; } = 18;                // CRF / CQ (lower = better)
    public double BitrateMbps { get; set; } = 12;
    public string Speed { get; set; } = "medium";         // x264/x265 preset; mapped for hardware encoders
    public string Audio { get; set; } = "aac";            // aac opus mp3 pcm flac none
    public int AudioKbps { get; set; } = 192;
    public int Height { get; set; }                       // 0 = the sequence's output height
    /// <summary>Forced output width (0 = follow the sequence aspect from <see cref="Height"/>). IMX 50 is 720×576.</summary>
    public int Width { get; set; }
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
    /// <summary>10-bit colour: the composite and the file keep 10 bits per channel (HEVC Main 10, AV1 10-bit, VP9 profile 2; ProRes is 10-bit
    /// already and gets the 10-bit composite) — smoother gradients from 10-bit and HDR footage. H.264 and the alpha codecs stay 8-bit.</summary>
    public bool TenBit { get; set; }
    /// <summary>HDR delivery: "" = Rec.709 SDR, "hlg" / "pq" = inverse-map the Rec.709 composite (SDR white = 203 cd/m²) and tag the file.</summary>
    public string Hdr { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public bool WantsHdr => Hdr is "hlg" or "pq";
    /// <summary>The export runs at 10 bits (<see cref="TenBit"/> or an HDR / 10-bit DNxHR file, with a codec that keeps them).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Deep => (TenBit || WantsHdr) && Supports10;
    [System.Text.Json.Serialization.JsonIgnore] public bool Supports10 => Video is "hevc" or "av1" or "vp9" or "prores" or "prores4444" or "dnxhr_hqx" or "dnxhr_444";
    [System.Text.Json.Serialization.JsonIgnore] public bool IsDnx => Video.StartsWith("dnxhr", StringComparison.Ordinal);
    /// <summary>The sequence's chapter markers as the file's chapters (MP4 / MOV / MKV / M4A).</summary>
    public bool Chapters { get; set; } = true;
    /// <summary>Transparent background (a nested sequence's render: <see cref="Nest"/>): the composite keeps alpha, yuva444p FFV1 (yuva420p with 4:2:0).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Alpha { get; set; }
    /// <summary>A background render (nested sequences while editing): ffmpeg runs below normal priority, so playback keeps its CPU.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Background { get; set; }
    /// <summary>Captions (caption-role titles) drawn into the picture; and / or written as an .srt next to the file; and / or muxed as CEA-608
    /// (<see cref="EmbedCc"/>) — <see cref="Captioning"/>.</summary>
    public bool BurnCaptions { get; set; } = true;
    /// <summary>Sound channels in the file: "stereo", "mono" (the stereo mix folded to one channel) or "5.1" (tracks to front / centre / surround /
    /// LFE by their 5.1 output, <see cref="TrackMix.Surround"/>).</summary>
    public string Channels { get; set; } = "stereo";
    /// <summary>Filled by <see cref="Exporter.RunAsync"/> for a 5.1 export: the rendered 6-channel float file the exporter reads.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? SurroundFile { get; set; }
    public bool CaptionFile { get; set; }
    /// <summary>CEA-608 closed captions muxed into the file (MOV c608 / MXF SMPTE 436M; MP4 remuxed as QuickTime). Preview still burns in
    /// or not by <see cref="BurnCaptions"/>.</summary>
    public bool EmbedCc { get; set; }
    /// <summary>Smart rendering (H.264 / HEVC): stretches showing one plain clip of a matching file are copied, only the rest is encoded (<see cref="SmartRender"/>).</summary>
    public bool Smart { get; set; }
    /// <summary>Filled by a smart render: how much was copied (or why nothing could be).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? SmartReport { get; set; }
    /// <summary>Filled by the export: how many clips the GPU scaled (<see cref="Exporter.GpuChain"/>), or that it failed and the export ran without it.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? GpuReport { get; set; }
    /// <summary>Set by smart rendering for the parts it encodes: the encoder's own parameters (x264: parameter set ids, headers in every key frame; x265: closed GOPs).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? PartParams { get; set; }
    public bool AudioOnly => Video == "none";
    /// <summary>A codec that keeps transparency (ProRes 4444 with alpha, VP9 with alpha, QuickTime Animation, PNG sequence): the export composites
    /// on a transparent background like a nested render (<see cref="Alpha"/>) — where nothing is drawn the file is see-through.</summary>
    public bool WantsAlpha => Video is "prores4444a" or "vp9a" or "qtrle" or "png";
    /// <summary>A numbered picture per frame (name_00000.png …) instead of one file.</summary>
    public bool ImageSequence => Video == "png";
    /// <summary>The ffmpeg output name: an image sequence gets its frame number pattern.</summary>
    public string OutputPath(string file) => ImageSequence && !file.Contains('%') ? Path.Combine(Path.GetDirectoryName(file) ?? "", Path.GetFileNameWithoutExtension(file) + "_%05d" + Path.GetExtension(file)) : file;
    public int OutH => Height > 0 ? Even(Height) : Editor.OutH;
    public int OutW => Width > 0 ? Even(Width) : Height > 0 ? Even(Editor.W * (double)Height / Editor.H) : Editor.OutW;
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
        new ExportSettings { Name = "DNxHR HQ master (.mov)", Video = "dnxhr_hq", Container = "mov", Audio = "pcm", Note = "Avid DNxHR High Quality, 8-bit 4:2:2, for editorial interchange" },
        new ExportSettings { Name = "DNxHR HQX 10-bit (.mov)", Video = "dnxhr_hqx", Container = "mov", Audio = "pcm", TenBit = true, Note = "Avid DNxHR HQX, 10-bit 4:2:2" },
        new ExportSettings { Name = "DNxHR 444 (.mov)", Video = "dnxhr_444", Container = "mov", Audio = "pcm", TenBit = true, Note = "Avid DNxHR 444, 10-bit 4:4:4" },
        new ExportSettings { Name = "HLG HDR (HEVC 10-bit)", Video = "hevc", TenBit = true, Hdr = "hlg", Quality = 18, Note = "Hybrid Log-Gamma for TVs and YouTube HDR; SDR white at 203 cd/m² (BT.2408)" },
        new ExportSettings { Name = "HDR10 / PQ (HEVC 10-bit)", Video = "hevc", TenBit = true, Hdr = "pq", Quality = 18, Note = "HDR10 (PQ) with mastering metadata; SDR white at 203 cd/m² (BT.2408)" },
        new ExportSettings { Name = "WebM (VP9)", Video = "vp9", Container = "webm", Audio = "opus", Quality = 31 },
        new ExportSettings { Name = "ProRes 4444 with alpha (.mov)", Video = "prores4444a", Container = "mov", Audio = "pcm", Note = "Transparent where nothing is drawn — titles, lower thirds, overlays for other editors" },
        new ExportSettings { Name = "WebM VP9 with alpha", Video = "vp9a", Container = "webm", Audio = "opus", Quality = 31, Note = "Transparent video for the web and OBS" },
        new ExportSettings { Name = "QuickTime Animation with alpha (.mov)", Video = "qtrle", Container = "mov", Audio = "pcm", Note = "Lossless RGB + alpha; very large files" },
        new ExportSettings { Name = "PNG sequence with alpha", Video = "png", Container = "png", Audio = "none", Note = "One picture per frame (name_00000.png …), transparent background, no sound" },
        new ExportSettings { Name = "XDCAM HD 422 50 Mbps (.mxf)", Video = "mpeg2_xdcam", Container = "mxf", Audio = "pcm", ConstantQuality = false, BitrateMbps = 50, Height = 1080, Aspect = 16 / 9.0, Note = "Sony XDCAM HD 422: MPEG-2 4:2:2 CBR, 24-bit PCM, MXF OP1a" },
        new ExportSettings { Name = "IMX 50 (.mxf)", Video = "mpeg2_imx", Container = "mxf", Audio = "pcm", ConstantQuality = false, BitrateMbps = 50, Height = 576, Width = 720, Aspect = 4 / 3.0, Note = "D-10 / IMX 50: MPEG-2 4:2:2 intra, 720×576 PAL, 24-bit PCM" },
        new ExportSettings { Name = "Podcast (−16 LUFS WAV)", Video = "none", Container = "wav", Audio = "pcm", Normalize = true, Lufs = -16, Note = "Stereo WAV normalized to −16 LUFS" },
        new ExportSettings { Name = "Audio only — WAV", Video = "none", Container = "wav", Audio = "pcm" },
        new ExportSettings { Name = "Audio only — MP3 320 kbps", Video = "none", Container = "mp3", Audio = "mp3", AudioKbps = 320 },
    };

    public static readonly string[] Containers = { "mp4", "mov", "mkv", "webm", "mxf", "png" };
    public static readonly string[] AudioContainers = { "wav", "mp3", "m4a" };

    /// <summary>ffmpeg video encoder name for a codec + encoder choice.</summary>
    public string VideoEncoder => (Video, Encoder) switch
    {
        ("h264", "nvenc") => "h264_nvenc", ("hevc", "nvenc") => "hevc_nvenc", ("av1", "nvenc") => "av1_nvenc",
        ("h264", "qsv") => "h264_qsv", ("hevc", "qsv") => "hevc_qsv", ("av1", "qsv") => "av1_qsv",
        ("h264", "amf") => "h264_amf", ("hevc", "amf") => "hevc_amf", ("av1", "amf") => "av1_amf",
        ("hevc", _) => "libx265", ("av1", _) => "libsvtav1", ("vp9", _) or ("vp9a", _) => "libvpx-vp9", ("prores", _) or ("prores4444", _) or ("prores4444a", _) => "prores_ks", ("ffv1", _) => "ffv1",
        ("dnxhr_lb", _) or ("dnxhr_sq", _) or ("dnxhr_hq", _) or ("dnxhr_hqx", _) or ("dnxhr_444", _) => "dnxhd",
        ("mpeg2_xdcam", _) or ("mpeg2_imx", _) => "mpeg2video",
        ("qtrle", _) => "qtrle", ("png", _) => "png",
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
                    string xp = PartParams ?? "";
                    if (enc == "libx265" && WantsHdr) xp = string.IsNullOrEmpty(xp) ? CutPro.Hdr.X265(Hdr) : CutPro.Hdr.X265(Hdr) + ":" + xp;
                    if (xp != "") a.AddRange(new[] { enc == "libx265" ? "-x265-params" : "-x264-params", xp });
                    a.AddRange(new[] { "-pix_fmt", Deep ? "yuv420p10le" : "yuv420p" });            // (x265: Main 10 then)
                    break;
                case "libsvtav1":
                    a.AddRange(new[] { "-preset", Speed switch { "slow" or "slower" or "veryslow" => "5", "fast" or "faster" or "veryfast" or "ultrafast" => "10", _ => "8" } });
                    if (ConstantQuality) a.AddRange(new[] { "-crf", Quality.ToString() }); else a.AddRange(new[] { "-b:v", kbps });
                    a.AddRange(new[] { "-pix_fmt", Deep ? "yuv420p10le" : "yuv420p" });
                    break;
                case "libvpx-vp9":
                    if (ConstantQuality) a.AddRange(new[] { "-crf", Quality.ToString(), "-b:v", "0" }); else a.AddRange(new[] { "-b:v", kbps });
                    a.AddRange(new[] { "-row-mt", "1", "-deadline", "good", "-cpu-used", Speed is "slow" or "slower" or "veryslow" ? "1" : "3", "-pix_fmt", Video == "vp9a" ? "yuva420p" : Deep ? "yuv420p10le" : "yuv420p" });   // (10-bit: profile 2)
                    if (Video == "vp9a") a.AddRange(new[] { "-auto-alt-ref", "0" });           // (libvpx: alt-ref frames and alpha don't mix)
                    break;
                case "qtrle": a.AddRange(new[] { "-pix_fmt", "argb" }); break;
                case "png":
                    a.AddRange(new[] { "-pix_fmt", "rgba", "-start_number", "0", "-f", "image2" });
                    break;
                case "prores_ks":
                    a.AddRange(Video == "prores4444a" ? new[] { "-profile:v", "4", "-vendor", "apl0", "-pix_fmt", "yuva444p10le", "-alpha_bits", "16" }
                             : Video == "prores4444" ? new[] { "-profile:v", "4", "-vendor", "apl0", "-pix_fmt", "yuv444p10le" }     // (4444: full chroma)
                                                     : new[] { "-profile:v", "3", "-vendor", "apl0", "-pix_fmt", "yuv422p10le" });
                    break;
                case "dnxhd":
                    a.AddRange(new[] { "-profile:v", Video is "dnxhr_lb" or "dnxhr_sq" or "dnxhr_hqx" or "dnxhr_444" ? Video : "dnxhr_hq",
                                       "-pix_fmt", Video == "dnxhr_444" ? "yuv444p10le" : Video == "dnxhr_hqx" ? "yuv422p10le" : "yuv422p" });
                    break;
                case "mpeg2video":
                    a.AddRange(new[] { "-b:v", kbps, "-minrate", kbps, "-maxrate", kbps, "-bufsize", buf, "-pix_fmt", "yuv422p" });
                    if (Video == "mpeg2_imx") a.AddRange(new[] { "-g", "1", "-bf", "0", "-dc", "10", "-flags", "+ildct+low_delay" });
                    else a.AddRange(new[] { "-g", "12", "-bf", "2", "-dc", "10" });
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
                    a.AddRange(new[] { "-pix_fmt", Deep ? "p010le" : Encoder == "qsv" ? "nv12" : "yuv420p" });   // (10-bit: HEVC / AV1 only)
                    break;
            }
            a.AddRange(new[] { "-r", TimeUtil.Rate });
            if (WantsHdr) a.AddRange(CutPro.Hdr.FileTags(Hdr));
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
            "prores" => 220 * Math.Pow(OutH / 1080.0, 2), "prores4444" or "prores4444a" => 330 * Math.Pow(OutH / 1080.0, 2),
            "dnxhr_lb" => 45 * Math.Pow(OutH / 1080.0, 2), "dnxhr_sq" => 90 * Math.Pow(OutH / 1080.0, 2), "dnxhr_hq" => 145 * Math.Pow(OutH / 1080.0, 2),
            "dnxhr_hqx" => 220 * Math.Pow(OutH / 1080.0, 2), "dnxhr_444" => 330 * Math.Pow(OutH / 1080.0, 2),
            "qtrle" => 400 * Math.Pow(OutH / 1080.0, 2), "png" => 600 * Math.Pow(OutH / 1080.0, 2), "hevc" => 6 * Math.Pow(OutH / 1080.0, 1.6), "av1" => 4.5 * Math.Pow(OutH / 1080.0, 1.6),
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
        var candidates = new[] { "libx265", "libsvtav1", "libvpx-vp9", "prores_ks", "dnxhd", "mpeg2video", "h264_nvenc", "hevc_nvenc", "av1_nvenc", "h264_qsv", "hevc_qsv", "av1_qsv", "h264_amf", "hevc_amf", "av1_amf" }
            .Where(e => list.Contains(" " + e + " ")).ToList();
        foreach (var e in candidates)
        {
            string extra = e == "dnxhd" ? "-profile:v dnxhr_hq -pix_fmt yuv422p" : e == "mpeg2video" ? "-b:v 2M -pix_fmt yuv422p" : "";
            var (code, _) = await Task.Run(() => Run(ff, $"-hide_banner -v error -f lavfi -i color=black:s=256x144:d=0.2 -frames:v 3 -c:v {e} {extra} -f null -"));
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
