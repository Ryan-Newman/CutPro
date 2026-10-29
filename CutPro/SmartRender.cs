using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CutPro;

/// <summary>
/// Smart rendering (Premiere's / Resolve's "smart render"): an H.264 / HEVC export copies the stretches of the timeline that show one plain clip
/// of a matching file — whole GOPs of its compressed video, untouched (no re-encode, no quality loss, much faster) — and encodes only the rest
/// (around cuts, titles, effects, transitions, fades), each such part as a small timeline of its own through the normal exporter. The parts are
/// joined (ffmpeg concat, parameter sets in every key frame: the encoder's have their own ids), the sound (exported separately, normalization
/// and all) and chapters muxed in, and the result is checked — every frame there and decodable — or the export falls back to a normal one.
/// A file can be copied when it has the output's codec, size, frame rate and pixel format, a constant frame rate, closed GOPs and no rotation.
/// </summary>
public static class SmartRender
{
    /// <summary>What smart rendering knows about a file's video stream (probed once per file version).</summary>
    public sealed class Info
    {
        public string Codec = "", PixFmt = "", Rate = "";
        public int W, H, Frames;
        public double Fps, Start;
        /// <summary>Key frames where a copy may start (presentation index; closed GOPs only).</summary>
        public int[] Keys = Array.Empty<int>();
        public string? Primaries, Trc, Space, Range;
        /// <summary>Why it can't be copied ("" = it can).</summary>
        public string Why = "";
    }

    static readonly Dictionary<string, Info> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The settings ask for smart rendering and it can apply (H.264 / HEVC in MP4 / MOV / MKV, no alpha).</summary>
    public static bool Applies(ExportSettings o) => o.Smart && !o.AudioOnly && !o.WantsAlpha && !o.Alpha && o.Video is "h264" or "hevc" && o.Container is "mp4" or "mov" or "mkv";

    static string Family(ExportSettings o) => o.Video == "hevc" ? "hevc" : "h264";

    /// <summary>A file's stream facts, key frames and copy eligibility (ffprobe; cached by path, size and time).</summary>
    public static Info Probe(MediaItem m)
    {
        var fi = new FileInfo(m.Path);
        string key = $"{m.Path}|{(fi.Exists ? fi.Length : 0)}|{(fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0)}";
        lock (cache) if (cache.TryGetValue(key, out var hit)) return hit;
        var info = new Info();
        try { ProbeInto(m, info); } catch (Exception ex) { info.Why = "can't be read: " + ex.Message; }
        lock (cache) cache[key] = info;
        return info;
    }

    static void ProbeInto(MediaItem m, Info info)
    {
        var fp = MediaProbe.FindFfprobe() ?? throw new Exception("ffprobe not found");
        if (m.Type != ClipKind.Video || m.Seq != null || m.Offline) { info.Why = "not a video file"; return; }
        var json = Run(fp, new[] { "-v", "error", "-select_streams", "v:0", "-show_entries",
                                   "stream=codec_name,pix_fmt,width,height,r_frame_rate,avg_frame_rate,time_base,start_time,field_order,sample_aspect_ratio,color_primaries,color_transfer,color_space,color_range:stream_side_data=rotation:format=start_time",
                                   "-of", "json", m.Path });
        using var doc = JsonDocument.Parse(json);
        var st = doc.RootElement.GetProperty("streams")[0];
        double fileStart = doc.RootElement.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("start_time", out var fs) && double.TryParse(fs.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fsv) ? fsv : 0;
        string S(string n) => st.TryGetProperty(n, out var v) ? v.ToString() : "";
        info.Codec = S("codec_name"); info.PixFmt = S("pix_fmt"); info.Rate = S("r_frame_rate");
        info.W = int.TryParse(S("width"), out var w) ? w : 0; info.H = int.TryParse(S("height"), out var h) ? h : 0;
        info.Fps = Ratio(info.Rate);
        info.Start = double.TryParse(S("start_time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s0) ? s0 : 0;
        double tb = Ratio(S("time_base"));
        string? Tag(string n) { var v = S(n); return v is "" or "unknown" ? null : v; }
        info.Primaries = Tag("color_primaries"); info.Trc = Tag("color_transfer"); info.Space = Tag("color_space"); info.Range = Tag("color_range");
        if (Math.Abs(Ratio(S("avg_frame_rate")) - info.Fps) > 1e-3 * Math.Max(1, info.Fps)) { info.Why = "a variable frame rate"; }
        if (S("field_order") is not ("" or "progressive" or "unknown")) info.Why = "interlaced";
        if (S("sample_aspect_ratio") is not ("" or "1:1" or "0:1" or "N/A")) info.Why = "non-square pixels";
        if (m.Rotation != 0) info.Why = "rotated";
        if (info.Why != "" || tb <= 0 || info.Fps <= 0) { if (info.Why == "") info.Why = "no frame rate"; return; }
        // every packet: presentation / decoding times, key frame flag (decode order)
        var lines = Run(fp, new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts,dts,flags", "-of", "csv=p=0", m.Path })
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pts = new List<long>(lines.Length); var key = new List<bool>(lines.Length);
        foreach (var ln in lines)
        {
            var p = ln.Split(',');
            if (p.Length < 3 || !long.TryParse(p[0], out var pv)) { info.Why = "packets without times"; return; }
            pts.Add(pv); key.Add(p[2].StartsWith('K'));
        }
        if (pts.Count < 2) { info.Why = "too short"; return; }
        // presentation order: a constant step (one frame) all the way
        var sorted = pts.OrderBy(x => x).ToArray();
        double step = 1 / info.Fps / tb;
        for (int i = 1; i < sorted.Length; i++) if (Math.Abs(sorted[i] - sorted[i - 1] - step) > Math.Max(1.01, step * 0.02)) { info.Why = "an uneven frame rate (dropped or doubled frames)"; return; }
        info.Frames = sorted.Length;
        // closed GOPs: nothing before a key frame (decode order) shows after it, nothing after it shows before it
        var maxBefore = new long[pts.Count]; long mx = long.MinValue;
        for (int i = 0; i < pts.Count; i++) { maxBefore[i] = mx; mx = Math.Max(mx, pts[i]); }
        var minAfter = new long[pts.Count]; long mn = long.MaxValue;
        for (int i = pts.Count - 1; i >= 0; i--) { minAfter[i] = mn; mn = Math.Min(mn, pts[i]); }
        var keys = new List<int>(); bool open = false;
        for (int i = 0; i < pts.Count; i++)
        {
            if (!key[i]) continue;
            if (maxBefore[i] > pts[i] || minAfter[i] < pts[i]) { open = true; continue; }
            keys.Add((int)Math.Round((pts[i] - sorted[0]) / step));
        }
        info.Keys = keys.OrderBy(x => x).ToArray();
        info.Start = sorted[0] * tb - fileStart;                                     // (where the first frame is on the timeline ffmpeg gives the file: from its start)
        if (info.Keys.Length == 0) info.Why = open ? "open GOPs (key frames that later frames reach back across)" : "no key frames";
    }

    static double Ratio(string r)
    {
        var p = r.Split('/');
        if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b != 0) return a / b;
        return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    static string Run(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit(); _ = err.Result;
        return outp;
    }

    // ---------------- the plan ----------------
    /// <summary>A part of the export (timeline frames [K0, K1) of the range): copied from a file's source frames [I0, I0 + K1 − K0), or encoded.</summary>
    public sealed record Part(bool Copy, int K0, int K1, MediaItem? M = null, int I0 = 0);

    /// <summary>Why a frame can't come straight from a file (null = it can: the clip, the file, the frame of it).</summary>
    static (Clip c, MediaItem m, int i)? Source(Editor ed, Transitions.Plan plan, List<Clip> pics, double t, ExportSettings o, Func<MediaItem, Info> probe)
    {
        Clip? only = null;
        foreach (var c in pics)
        {
            if (!plan.Shows(c, t)) continue;
            if (only != null) return null;                                       // (two pictures at once)
            only = c;
        }
        if (only is not { Kind: ClipKind.Video } c1 || !Plain(c1, t)) return null;
        if (plan.Wins.Any(w => (w.A?.Id == c1.Id || w.B?.Id == c1.Id || w.Owner.Id == c1.Id) && t >= w.T0 - 1e-9 && t < w.T1 - 1e-9)) return null;   // (inside a transition)
        if (ed.MediaOf(c1) is not { Seq: null, Offline: false } m) return null;
        var inf = probe(m);
        if (inf.Why != "" || inf.Codec != Family(o) || inf.PixFmt != (o.Deep ? "yuv420p10le" : "yuv420p") || Hdr.Of(m) != "" || inf.W != o.OutW || inf.H != o.OutH || o.OutW != Editor.OutW || o.OutH != Editor.OutH
            || Math.Abs(inf.Fps - TimeUtil.Fps) > 1e-6 * TimeUtil.Fps) return null;
        int i = (int)Math.Ceiling((c1.In + (t - c1.Start) - inf.Start) * inf.Fps - 1e-3);   // (a plain clip shows its frames from its In on)
        return i >= 0 && i < inf.Frames ? (c1, m, i) : null;
    }

    /// <summary>A clip the exporter shows exactly as the file's frames: full frame, plain speed, no effect of any kind.</summary>
    static bool Plain(Clip c, double t) =>
        Math.Abs(c.Speed - 100) < 1e-9 && !c.Reverse && !c.Hold && !(c.Anim != null && Anim.AnyAnimated(c, c.Anim.Keys.Where(k => k != AnimProp.Volume))) && c.Opacity >= 100 && c.FadeIn <= 0 && c.FadeOut <= 0 && c.Blur <= 0
        && Math.Abs(c.Scale - 100) < 1e-9 && c.X == 0 && c.Y == 0 && c.Rotation == 0 && c.AnchorX == 0 && c.AnchorY == 0 && c.Pan == 0 && c.Masks.Count == 0
        && c.Grade == null && c.Toon == null && c.Blend == BlendMode.Normal && c.Mask == MaskMode.None && c.CropL == 0 && c.CropT == 0 && c.CropR == 0 && c.CropB == 0
        && !c.FlipH && !c.FlipV && c.Key == null && c.Stab == null && c.Lens == null && c.Looks == null && c.Shadow == null && c.Pin == null && c.Distort == null
        && c.Denoise == null && c.Interp == FrameInterp.Sample;

    /// <summary>The parts of the range [t0, t1): copies of whole GOPs where single plain clips of matching files show, encoded parts between.
    /// minCopy: shortest copy worth its seams (frames).</summary>
    public static List<Part> Plan(Editor ed, ExportSettings o, double t0, double t1, Func<MediaItem, Info>? probe = null, int minCopy = 0)
    {
        probe ??= Probe;
        double F = TimeUtil.Fps;
        int k0 = (int)Math.Round(t0 * F), n = Math.Max(0, (int)Math.Round(t1 * F) - k0);
        if (minCopy <= 0) minCopy = (int)Math.Ceiling(F);                       // (a second at least)
        var plan = Transitions.Compute(ed);
        var pics = Transitions.Extended(ed.Clips, plan).Where(c => c.Kind != ClipKind.Audio && !(c.Track < ed.TrackOff.Length && ed.TrackOff[c.Track]))
                                                    .OrderBy(c => c.Start).ToList();
        // frame by frame: the clip / file / source frame (null = must be encoded); a sweep keeps the clips near t
        var src = new (Clip c, MediaItem m, int i)?[n];
        int next = 0; var near = new List<Clip>();
        for (int k = 0; k < n; k++)
        {
            double t = (k0 + k) / F;
            while (next < pics.Count && plan.Range(pics[next]).s <= t + 1e-6) near.Add(pics[next++]);
            near.RemoveAll(c => plan.Range(c).e <= t - 1e-6);
            src[k] = Source(ed, plan, near, t, o, probe);
        }
        // runs of consecutive source frames of one clip → whole GOPs inside them
        var parts = new List<Part>();
        int at = 0;
        for (int k = 0; k < n;)
        {
            if (src[k] is not { } s) { k++; continue; }
            int e = k + 1;
            while (e < n && src[e] is { } s2 && s2.m == s.m && s2.i == s.i + (e - k)) e++;   // (across a cut between consecutive frames of the file too)
            var inf = probe(s.m);
            int a = s.i, b = s.i + (e - k);                                     // (source frames [a, b))
            int f1 = Array.Find(inf.Keys, x => x >= a);
            int fEnd = b >= inf.Frames ? inf.Frames : inf.Keys.LastOrDefault(x => x <= b);
            if (inf.Keys.Any(x => x >= a) && fEnd - f1 >= minCopy)
            {
                int c0 = k + (f1 - a), c1 = k + (fEnd - a);
                if (c0 > at) parts.Add(new Part(false, at, c0));
                parts.Add(new Part(true, c0, c1, s.m, f1));
                at = c1;
            }
            k = e;
        }
        if (at < n) parts.Add(new Part(false, at, n));
        return parts;
    }

    // ---------------- the export ----------------
    /// <summary>Exports with copies where possible (see the class). Returns the exporter's error (null = done); the report lands in <see cref="ExportSettings.SmartReport"/>.
    /// Falls back to a normal export when nothing can be copied or the joined file doesn't check out.</summary>
    public static async Task<string?> RunAsync(string ff, Editor ed, string output, IProgress<double> progress, CancellationToken ct, IReadOnlyDictionary<string, string>? titles,
                                               IReadOnlyDictionary<string, string>? masks, ExportSettings o, IProgress<string>? status, IReadOnlyDictionary<string, Nest.Picture>? nests)
    {
        var normal = o.Clone(); normal.Smart = false;
        async Task<string?> Normal(string why)
        {
            o.SmartReport = why;
            return await Exporter.RunAsync(ff, ed, output, progress, ct, titles, masks, normal, status, nests);
        }
        var (t0, t1) = Exporter.Range(ed, o);
        status?.Report("Smart render: reading the files' key frames…");
        List<Part> parts;
        try { parts = await Task.Run(() => Plan(ed, o, t0, t1), ct); }
        catch (OperationCanceledException) { return "Cancelled"; }
        catch (Exception ex) { return await Normal("Smart render: " + ex.Message + " — exported normally"); }
        int total = parts.Count == 0 ? 0 : parts[^1].K1, copied = parts.Where(p => p.Copy).Sum(p => p.K1 - p.K0);
        if (copied == 0) return await Normal("Smart render: nothing could be copied (" + WhyNot(ed, o) + ") — exported normally");

        var dir = Path.Combine(Path.GetTempPath(), "cutpro_smart_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var snap = Nest.Take(ed, ed.Seq);
            double F = TimeUtil.Fps; int k0 = (int)Math.Round(t0 * F);
            var files = new List<string>(); int done = 0;
            bool mixed = parts.Any(p => !p.Copy) || parts.Where(p => p.Copy).Select(p => p.M!.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            for (int pi = 0; pi < parts.Count; pi++)
            {
                ct.ThrowIfCancellationRequested();
                var p = parts[pi];
                var file = Path.Combine(dir, $"p{pi:00000}.mp4");
                status?.Report(p.Copy ? $"Smart render: copying {p.M!.Name} ({TimeUtil.Format((k0 + p.K0) / F)} – {TimeUtil.Format((k0 + p.K1) / F)})…"
                                      : $"Smart render: encoding {TimeUtil.Format((k0 + p.K0) / F)} – {TimeUtil.Format((k0 + p.K1) / F)}…");
                int n = p.K1 - p.K0;
                var sub = new Progress<double>(x => progress.Report(Math.Clamp((done + x * n) / (double)Math.Max(1, total), 0, 1) * 0.92));
                string? err = p.Copy ? await Task.Run(() => CopyPart(ff, p, file), ct)
                                     : await EncodePart(ff, snap, (k0 + p.K0) / F, (k0 + p.K1) / F, file, o, titles, masks, nests, sub, ct);
                if (err != null) return err == "Cancelled" ? err : await Normal($"Smart render: a part failed ({err}) — exported normally");
                int got = Frames(file);
                if (got != n) return await Normal($"Smart render: a part had {got} frames, not {n} — exported normally");
                files.Add(file); done += n;
                progress.Report(Math.Clamp(done / (double)Math.Max(1, total), 0, 1) * 0.92);
            }
            // the sound (normalization, effects, everything) and the chapters, as a normal export makes them
            string? audio = null;
            if (o.Audio != "none")
            {
                status?.Report("Smart render: mixing the sound…");
                // (in the output's own container family: AAC's encoder delay is an MP4 edit list, Matroska's a codec delay — remuxing across them shifts the sound)
                string ext = o.Container switch { "mkv" => "mka", "mov" => "mov", _ => "m4a" };
                var ao = o.Clone(); ao.Smart = false; ao.Video = "none"; ao.Container = ext; ao.Chapters = false;
                audio = Path.Combine(dir, "sound." + ext);
                var err = await Exporter.RunAsync(ff, ed, audio, new Progress<double>(x => progress.Report(0.92 + x * 0.05)), ct, null, null, ao, null, nests);
                if (err != null) return err == "Cancelled" ? err : await Normal($"Smart render: the sound failed ({err}) — exported normally");
            }
            var chapters = o.Chapters && Chapters.Supported(o.Container) ? ed.ChaptersFor(t0, t1) : new();
            string? meta = null;
            if (chapters.Count > 0) { meta = Path.Combine(dir, "chapters.txt"); File.WriteAllText(meta, Chapters.FfMetadata(Chapters.ForFile(chapters), t1 - t0), new UTF8Encoding(false)); }
            var list = Path.Combine(dir, "parts.txt");
            File.WriteAllText(list, "ffconcat version 1.0\n" + string.Concat(files.Select(f => $"file '{f.Replace('\\', '/').Replace("'", "'\\''")}'\n")), new UTF8Encoding(false));
            status?.Report("Smart render: joining the parts…");
            var args = new List<string> { "-y", "-v", "error", "-f", "concat", "-safe", "0", "-i", list };
            int inputs = 1, ai = -1, mi = -1;
            if (audio != null) { args.AddRange(new[] { "-i", audio }); ai = inputs++; }
            if (meta != null) { args.AddRange(new[] { "-f", "ffmetadata", "-i", meta }); mi = inputs++; }
            args.AddRange(new[] { "-map", "0:v:0" });
            if (ai >= 0) args.AddRange(new[] { "-map", $"{ai}:a:0" });
            args.AddRange(new[] { "-map_chapters", mi >= 0 ? mi.ToString() : "-1", "-map_metadata", "-1", "-c", "copy" });
            // parameter sets in the key frames: the copies' and the encoder's differ (avc3 / hev1: "parameter sets in the stream")
            if (o.Container is "mp4" or "mov") args.AddRange(new[] { "-tag:v", Family(o) == "hevc" ? (mixed ? "hev1" : "hvc1") : (mixed ? "avc3" : "avc1"), "-movflags", "+faststart" });
            args.AddRange(new[] { "-t", (total / F).ToString("0.######", CultureInfo.InvariantCulture), o.OutputPath(output) });
            var mux = RunErr(ff, args);
            if (mux.code != 0) return await Normal("Smart render: joining failed (" + Last(mux.err) + ") — exported normally");
            // the check: every frame there, and the whole thing decodes without an error
            status?.Report("Smart render: checking the file…");
            int frames = Frames(o.OutputPath(output));
            // decode where it can go wrong: around every join (the copies' insides are the files' own frames); a short file whole
            var joins = parts.Skip(1).Select(p => p.K0 / F).ToList();
            const double half = 2.5;
            var windows = joins.Count * 2 * half * 3 < total / F ? joins.Select(j => (Math.Max(0, j - half), 2 * half)).ToList() : new() { (0.0, total / F + 1) };
            (int code, string err) dec = (0, "");
            foreach (var (ws, wl) in windows)
            {
                var r = RunErr(ff, new[] { "-v", "error", "-ss", ws.ToString("0.###", CultureInfo.InvariantCulture), "-i", o.OutputPath(output), "-t", wl.ToString("0.###", CultureInfo.InvariantCulture), "-map", "0:v:0", "-f", "null", "-" });
                if (r.code != 0 || Problems(r.err).Length > 0) { dec = r; break; }
            }
            var problems = Problems(dec.err);
            if (frames != total || dec.code != 0 || problems.Length > 0)
                return await Normal($"Smart render: the joined file didn't check out ({frames} of {total} frames{(problems.Length > 0 ? ", " + problems : "")}) — exported normally");
            progress.Report(1);
            o.SmartReport = $"Smart render: {copied * 100.0 / Math.Max(1, total):0} % of the frames copied ({copied} of {total}), {total - copied} encoded";
            return null;
        }
        catch (OperationCanceledException) { return "Cancelled"; }
        finally { if (Environment.GetEnvironmentVariable("CUTPRO_SMARTKEEP") == null) try { Directory.Delete(dir, true); } catch { } }   // (CUTPRO_SMARTKEEP=1 keeps the parts: debugging)
    }

    /// <summary>Why the sequence's files can't be copied (the first reason found), for the report.</summary>
    static string WhyNot(Editor ed, ExportSettings o)
    {
        foreach (var m in ed.Clips.Where(c => c.Kind == ClipKind.Video).Select(ed.MediaOf).Where(m => m is { Seq: null, Offline: false }).Distinct())
        {
            var inf = Probe(m!);
            if (inf.Why != "") return $"{m!.Name}: {inf.Why}";
            if (inf.Codec != Family(o)) return $"{m!.Name} is {inf.Codec.ToUpperInvariant()}, the export {Family(o).ToUpperInvariant()}";
            if (inf.W != Editor.OutW || inf.H != Editor.OutH) return $"{m!.Name} is {inf.W}×{inf.H}, the export {Editor.OutW}×{Editor.OutH}";
            if (Math.Abs(inf.Fps - TimeUtil.Fps) > 1e-6 * TimeUtil.Fps) return $"{m!.Name} is {inf.Fps:0.###} fps, the sequence {TimeUtil.Fps:0.###}";
            if (inf.PixFmt != (o.Deep ? "yuv420p10le" : "yuv420p")) return $"{m!.Name} is {inf.PixFmt}, the export {(o.Deep ? "yuv420p10le" : "yuv420p")}";
            if (Hdr.Of(m!) != "") return $"{m!.Name} is HDR (tone-mapped: every frame changes)";
        }
        return "every stretch has effects, titles or transitions, or is shorter than a GOP";
    }

    /// <summary>Whole GOPs of the file: source frames [I0, I0 + n) from the key frame at I0, untouched; the parameter sets put into each key frame.</summary>
    static string? CopyPart(string ff, Part p, string file)
    {
        var inf = Probe(p.M!);
        int n = p.K1 - p.K0;
        double kt = inf.Start + p.I0 / inf.Fps, ss = kt + 0.25 / inf.Fps;           // (a seek lands on the key frame at or before: this one)
        var r = RunErr(ff, new[] { "-y", "-v", "error", "-ss", ss.ToString("0.######", CultureInfo.InvariantCulture), "-i", p.M!.Path, "-map", "0:v:0", "-c:v", "copy",
                                   // (the file's parameter sets before each key frame — the muxer keeps them in the samples, so they travel with the copy)
                                   "-frames:v", n.ToString(), "-bsf:v", inf.Codec == "hevc" ? "hevc_mp4toannexb" : "h264_mp4toannexb", "-an", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
                                   "-output_ts_offset", (0.25 / inf.Fps).ToString("0.########", CultureInfo.InvariantCulture), "-f", "mp4", file });
        return r.code == 0 ? null : Last(r.err);
    }

    /// <summary>Timeline [ta, tb) encoded on its own: the clips there, cut to it and moved to 0 (only plain clips cross its ends, by the plan), the
    /// exporter's whole picture pipeline, the output's codec with parameter sets in every key frame (x264: their own ids), closed GOPs, the copies' pixel format.</summary>
    static async Task<string?> EncodePart(string ff, Nest.Snap snap, double ta, double tb, string file, ExportSettings o, IReadOnlyDictionary<string, string>? titles,
                                          IReadOnlyDictionary<string, string>? masks, IReadOnlyDictionary<string, Nest.Picture>? nests, IProgress<double> progress, CancellationToken ct)
    {
        var e = Nest.EditorFor(snap);
        double len = tb - ta, eps = 1e-6;
        var keep = new List<Clip>();
        foreach (var c in e.Clips)
        {
            if (c.Kind == ClipKind.Audio || c.End <= ta + eps || c.Start >= tb - eps) continue;
            if (c.Start < ta - eps) { double cut = ta - c.Start; c.In += cut; c.Dur -= cut; c.Start = ta; c.TransIn = null; }   // (a plain clip running in: its head goes)
            if (c.End > tb + eps) { c.Dur = tb - c.Start; c.TransOut = null; }
            c.Start -= ta;
            keep.Add(c);
        }
        if (keep.Count == 0 || keep.Max(c => c.End) < len - eps)                 // (the part ends in black: something there so the export runs to its end)
            keep.Add(new Clip { Kind = ClipKind.Text, Text = "", Track = Tracks.V1, Start = keep.Count == 0 ? 0 : keep.Max(c => c.End), Dur = len - (keep.Count == 0 ? 0 : keep.Max(c => c.End)), Style = new TextStyle { Shadow = false } });
        e.Clips = keep;
        var po = o.Clone();
        po.Smart = false; po.Audio = "none"; po.Container = "mp4"; po.InOutOnly = false; po.Normalize = false; po.Chapters = false; po.Encoder = "software";
        po.PartParams = Family(o) == "hevc" ? "repeat-headers=1:open-gop=0:info=0" : "sps-id=1:repeat-headers=1";
        var parts = new Dictionary<string, string>(titles ?? new Dictionary<string, string>());
        foreach (var c in keep.Where(c => c.Kind == ClipKind.Text && !parts.ContainsKey(c.Id)))
        {   // (the spacer: an empty, transparent picture)
            var png = Path.Combine(Path.GetDirectoryName(file)!, c.Id + ".png");
            RunErr(ff, new[] { "-y", "-v", "error", "-f", "lavfi", "-i", $"color=c=black@0:s={po.OutW}x{po.OutH},format=rgba", "-frames:v", "1", png });
            parts[c.Id] = png;
        }
        var err = await Exporter.RunAsync(ff, e, file, progress, ct, parts, masks, po, null, nests);
        return err;
    }

    /// <summary>A file's video frame count (packets).</summary>
    public static int Frames(string file)
    {
        var fp = MediaProbe.FindFfprobe();
        if (fp == null || !File.Exists(file)) return -1;
        var s = Run(fp, new[] { "-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", file }).Trim();
        return int.TryParse(s.Split('\n')[0].Trim().TrimEnd(','), out var n) ? n : -1;
    }

    static (int code, string err) RunErr(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(); _ = o.Result;
        return (p.ExitCode, err);
    }
    /// <summary>A decode's complaints that matter (not the MP4 demuxer's note about chapter tracks, which every file with chapters gets).</summary>
    public static string Problems(string err) =>
        string.Join(" / ", err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !l.Contains("chapter track not found", StringComparison.OrdinalIgnoreCase)).Take(3));
    static string Last(string err) { var l = err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); return l.Length == 0 ? "no message" : l[^1]; }
}
