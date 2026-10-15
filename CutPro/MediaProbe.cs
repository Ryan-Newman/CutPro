using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// Import metadata via ffprobe and thumbnails via ffmpeg, so anything ffmpeg can decode imports
/// (MKV, HEVC/H.265, VP9/AV1 WebM, ProRes MOV, MTS, FLAC, Opus, ...), independent of Windows codecs.
/// Still images (png/jpg/...) are loaded with WPF.
/// </summary>
public static class MediaProbe
{
    static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
    public static bool IsImage(string path) => ImageExt.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Filter for the import dialog.</summary>
    public const string DialogFilter =
        "All media|*.mp4;*.mov;*.m4v;*.mkv;*.webm;*.avi;*.wmv;*.flv;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.3gp;*.mxf;*.hevc;*.h265;*.h264;*.vob;*.ogv;" +
        "*.mp3;*.wav;*.aac;*.m4a;*.wma;*.flac;*.ogg;*.opus;*.aif;*.aiff;*.ac3;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*";

    public static string? LastError;

    public static string? FindFfprobe()
    {
        var ff = Exporter.FindFfmpeg();
        if (ff == null) return null;
        var p = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Creates a MediaItem with duration, size, audio flag and thumbnail. Returns null (see LastError) on failure.</summary>
    public static async Task<MediaItem?> ProbeAsync(string path)
    {
        var m = new MediaItem { Path = path, Type = IsImage(path) ? ClipKind.Image : ClipKind.Video };
        return await FillAsync(m) ? m : null;
    }

    /// <summary>(Re)computes metadata + thumbnail for an item (import, project open, headless export).</summary>
    public static async Task<bool> FillAsync(MediaItem m)
    {
        LastError = null;
        m.RefreshOnline();
        if (m.Offline) { LastError = "file not found"; return false; }
        try
        {
            if (IsImage(m.Path))
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.UriSource = new Uri(m.Path); bi.DecodePixelWidth = 320; bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
                var full = BitmapDecoder.Create(new Uri(m.Path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                m.Type = ClipKind.Image; m.Width = full.PixelWidth; m.Height = full.PixelHeight; m.Duration = 5; m.Thumb = bi;
                return true;
            }
            var info = await Task.Run(() => Probe(m.Path));
            if (info == null) return false;
            m.Duration = info.Value.Duration; m.Width = info.Value.W; m.Height = info.Value.H; m.HasAudio = info.Value.HasAudio;
            m.Rotation = info.Value.Rotation; m.Codec = info.Value.Codec; m.TenBit = info.Value.TenBit; m.Fps = info.Value.Fps;
            m.Type = info.Value.HasVideo ? ClipKind.Video : ClipKind.Audio;
            if (m.Type == ClipKind.Video) m.Thumb = await Task.Run(() => Thumb(m));
            if (m.HasAudio) AudioCache.Ensure(m);
            if (m.Type == ClipKind.Video)
            {
                // background: pick CPU/GPU decoders for heavy (above 1080p) files, then build the scrub cache
                ScrubCache.Ensure(m);
            }
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    readonly record struct Info(double Duration, int W, int H, bool HasVideo, bool HasAudio, int Rotation, string Codec, bool TenBit, double Fps);

    static Info? Probe(string path)
    {
        var fp = FindFfprobe();
        if (fp == null) { LastError = "ffprobe.exe not found (install ffmpeg: winget install Gyan.FFmpeg)"; return null; }
        var (code, output, err) = Run(fp, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }, 20000);
        if (code != 0 || output.Length == 0) { LastError = FirstLine(err) ?? "ffprobe could not read the file"; return null; }

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        double dur = Num(root, "format", "duration");
        int w = 0, h = 0, rot = 0; bool hasV = false, hasA = false, ten = false; string codec = ""; double fps = 0;
        if (root.TryGetProperty("streams", out var streams))
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;
                bool cover = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "audio") { hasA = true; if (dur <= 0) dur = Math.Max(dur, Num(s, "duration")); }
                else if (type == "video" && !cover && !hasV)
                {
                    hasV = true;
                    w = s.TryGetProperty("width", out var sw) ? sw.GetInt32() : 0;
                    h = s.TryGetProperty("height", out var sh) ? sh.GetInt32() : 0;
                    rot = Rotation(s);
                    if (Math.Abs(rot) % 180 == 90) (w, h) = (h, w);                  // phone footage: ffmpeg auto-rotates on decode
                    codec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() ?? "" : "";
                    ten = s.TryGetProperty("pix_fmt", out var pf) && (pf.GetString() ?? "").Contains("10");
                    fps = Rate(s, "avg_frame_rate"); if (fps <= 0 || fps > 240) fps = Rate(s, "r_frame_rate");
                    if (dur <= 0) dur = Num(s, "duration");
                }
            }
        if (!hasV && !hasA) { LastError = "no video or audio stream"; return null; }
        if (dur <= 0) { LastError = "unknown duration (raw stream?) — remux it into MP4/MKV first"; return null; }
        return new Info(dur, w, h, hasV, hasA, rot, codec, ten, fps);
    }

    static double Rate(JsonElement s, string key)
    {
        if (!s.TryGetProperty(key, out var r) || r.GetString() is not { } txt) return 0;
        var p = txt.Split('/');
        return p.Length == 2 && double.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a)
               && double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) && b > 0 ? a / b : 0;
    }

    static int Rotation(JsonElement s)
    {
        if (s.TryGetProperty("side_data_list", out var sd))
            foreach (var d in sd.EnumerateArray())
                if (d.TryGetProperty("rotation", out var r)) return (int)Math.Round(r.GetDouble());
        if (s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rt) && int.TryParse(rt.GetString(), out var deg)) return deg;
        return 0;
    }

    static double Num(JsonElement e, params string[] path)
    {
        foreach (var p in path) if (!e.TryGetProperty(p, out e)) return 0;
        return e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
             : e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;
    }

    /// <summary>320-px-wide thumbnail from ~1 s in (or the middle of short clips), decoded by ffmpeg.</summary>
    static ImageSource? Thumb(MediaItem m)
    {
        var ff = Exporter.FindFfmpeg(); if (ff == null || m.Width <= 0 || m.Height <= 0) return null;
        int tw = 320, th = Math.Max(2, (int)Math.Round(320.0 * m.Height / m.Width / 2) * 2);
        var at = Math.Min(1, m.Duration / 2).ToString("0.###", CultureInfo.InvariantCulture);
        var (code, px, _) = Run(ff, new[] { "-v", "error", "-nostdin", "-ss", at, "-i", m.Path, "-frames:v", "1", "-an",
            "-vf", $"scale={tw}:{th}:force_original_aspect_ratio=decrease,pad={tw}:{th}:(ow-iw)/2:(oh-ih)/2,format=bgr0", "-f", "rawvideo", "pipe:1" }, 20000);
        if (code != 0 || px.Length < tw * th * 4) return null;
        var bmp = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Bgr32, null, px, tw * 4);
        bmp.Freeze();
        return bmp;
    }

    static string? FirstLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    static (int code, byte[] stdout, string stderr) Run(string exe, IEnumerable<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return (-1, Array.Empty<byte>(), "timed out"); }
        copy.Wait(); p.WaitForExit();
        return (p.ExitCode, ms.ToArray(), err.Result);
    }
}
