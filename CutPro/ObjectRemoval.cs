using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// Object removal (After Effects' Content-Aware Fill, simply; no neural network): the pixels under a tracked object mask are filled with the
/// background from other frames — where the object had moved away — after lining those frames up with the camera's movement (phase correlation
/// of small grey copies, the objects left out). Each pixel takes the median of the nearest frames that saw the background there; what no frame
/// ever saw is filled from its edges inwards. The result is a cleaned copy of the clip's stretch of the file (with a second of handles), which
/// replaces the clip's picture — preview and export simply play a file. A clean plate is the same idea for one picture: every pixel the median
/// of the clip's frames lined up on one frame, the masked objects left out (moving things vanish too).
/// </summary>
public static class ObjectRemoval
{
    public sealed class Options
    {
        /// <summary>How many frames each side to look for background.</summary>
        public int Range { get; set; } = 60;
        /// <summary>Grow the mask by this many pixels of the file (motion blur, soft edges).</summary>
        public double Expand { get; set; } = 6;
        /// <summary>Frames that agree, at most, per pixel (the median of them).</summary>
        public int Candidates { get; set; } = 5;
    }

    public static string? DirOverride;                                       // (tests)
    static string Dir => DirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro", "Cleaned");

    /// <summary>The cleaned copy's file: next to the original when that folder can be written, else CutPro's own folder.</summary>
    public static string TargetOf(MediaItem m, string what, string ext)
    {
        string stem = Path.GetFileNameWithoutExtension(m.Path) + $" ({what})";
        string? folder = DirOverride ?? Path.GetDirectoryName(m.Path);
        try { if (folder == null || !Directory.Exists(folder)) folder = Dir; var probe = Path.Combine(folder, ".cutpro_write_test"); File.WriteAllText(probe, ""); File.Delete(probe); }
        catch { folder = Dir; }
        Directory.CreateDirectory(folder!);
        var f = Path.Combine(folder!, stem + ext);
        for (int i = 2; File.Exists(f); i++) f = Path.Combine(folder!, $"{stem} {i}{ext}");
        return f;
    }

    /// <summary>The masks' area on the file's frame at source time src (w × h, 1 = removed), grown by r pixels.</summary>
    public static byte[] MaskAt(IReadOnlyList<MaskTrack> tracks, double src, int w, int h, double r)
    {
        var a = new byte[w * h];
        int f = MaskGeometry.FrameOf(src);
        foreach (var tr in tracks)
        {
            if (tr.Frames.Count == 0) continue;
            var ras = MaskGeometry.Raster(tr.At(f), w, h);
            for (int i = 0; i < a.Length; i++) if (ras[i]) a[i] = 1;
        }
        int g = (int)Math.Round(r);
        return g > 0 ? Grow(a, w, h, g) : a;
    }

    /// <summary>Dilation by a square of radius g (two passes of a running maximum).</summary>
    static byte[] Grow(byte[] a, int w, int h, int g)
    {
        var t = new byte[a.Length]; var o = new byte[a.Length];
        for (int y = 0; y < h; y++)
        {
            int last = -1_000_000;
            for (int x = 0; x < w; x++) { if (a[y * w + x] != 0) last = x; if (x - last <= g) t[y * w + x] = 1; }
            last = 1_000_000;
            for (int x = w - 1; x >= 0; x--) { if (a[y * w + x] != 0) last = x; if (last - x <= g) t[y * w + x] = 1; }
        }
        for (int x = 0; x < w; x++)
        {
            int last = -1_000_000;
            for (int y = 0; y < h; y++) { if (t[y * w + x] != 0) last = y; if (y - last <= g) o[y * w + x] = 1; }
            last = 1_000_000;
            for (int y = h - 1; y >= 0; y--) { if (t[y * w + x] != 0) last = y; if (last - y <= g) o[y * w + x] = 1; }
        }
        return o;
    }

    // ---------------- camera movement: phase correlation of small grey copies ----------------
    public const int Sw = 256, Sh = 256;
    /// <summary>A frame as a small grey picture for lining up (Hann-windowed, masked pixels as the mean, zero-padded to Sw × Sh).</summary>
    public static float[] Thumb(byte[] bgr, byte[]? mask, int w, int h)
    {
        int tw = Sw, th = Math.Max(2, Math.Min(Sh, (int)Math.Round(Sw * h / (double)w)));
        double fx = w / (double)tw, fy = h / (double)th;
        var g = new float[Sw * Sh]; var ok = new bool[Sw * Sh];
        double sum = 0; int cnt = 0;
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                int sx = Math.Min(w - 1, (int)((x + 0.5) * fx)), sy = Math.Min(h - 1, (int)((y + 0.5) * fy)), i = (sy * w + sx);
                if (mask != null && mask[i] != 0) continue;
                float v = 0.114f * bgr[i * 3] + 0.587f * bgr[i * 3 + 1] + 0.299f * bgr[i * 3 + 2];
                g[y * Sw + x] = v; ok[y * Sw + x] = true; sum += v; cnt++;
            }
        float mean = cnt > 0 ? (float)(sum / cnt) : 0;
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                double win = (0.5 - 0.5 * Math.Cos(2 * Math.PI * (x + 0.5) / tw)) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * (y + 0.5) / th));
                int i = y * Sw + x;
                g[i] = (float)(((ok[i] ? g[i] : mean) - mean) * win);
            }
        return g;
    }

    /// <summary>How far the picture moved from a to b (in a's pixels of the full frame): content at p in a is at p + (dx, dy) in b.</summary>
    public static (double dx, double dy) Shift(float[] a, float[] b, int w, int h)
    {
        var A = Fft2(a, false); var B = Fft2(b, false);
        for (int i = 0; i < A.Length; i++)
        {
            var c = B[i] * Complex.Conjugate(A[i]);
            double mag = c.Magnitude;
            A[i] = mag > 1e-9 ? c / mag : Complex.Zero;
        }
        var r = Fft2Inverse(A);
        int best = 0; double bv = double.MinValue;
        for (int i = 0; i < r.Length; i++) if (r[i] > bv) { bv = r[i]; best = i; }
        int px = best % Sw, py = best / Sw;
        // sub-pixel: the phase-correlation peak is a sinc, not a parabola (Foroosh): the larger neighbour's share of it
        double Sub(int i0, int im, int ip) { double c = r[i0], a = r[im], b = r[ip]; return b >= a ? (b > 0 ? b / (b + c) : 0) : (a > 0 ? -a / (a + c) : 0); }
        double sx = px + Sub(best, py * Sw + (px - 1 + Sw) % Sw, py * Sw + (px + 1) % Sw);
        double sy = py + Sub(best, ((py - 1 + Sh) % Sh) * Sw + px, ((py + 1) % Sh) * Sw + px);
        if (sx > Sw / 2.0) sx -= Sw; if (sy > Sh / 2.0) sy -= Sh;
        int th = Math.Max(2, Math.Min(Sh, (int)Math.Round(Sw * h / (double)w)));
        return (sx * w / Sw, sy * h / th);
    }

    static Complex[] Fft2(float[] g, bool inverse)
    {
        var a = new Complex[Sw * Sh];
        for (int i = 0; i < a.Length; i++) a[i] = g[i];
        var row = new Complex[Sw]; var col = new Complex[Sh];
        for (int y = 0; y < Sh; y++) { Array.Copy(a, y * Sw, row, 0, Sw); Pcm.Fft(row, inverse); Array.Copy(row, 0, a, y * Sw, Sw); }
        for (int x = 0; x < Sw; x++) { for (int y = 0; y < Sh; y++) col[y] = a[y * Sw + x]; Pcm.Fft(col, inverse); for (int y = 0; y < Sh; y++) a[y * Sw + x] = col[y]; }
        return a;
    }
    static double[] Fft2Inverse(Complex[] a)
    {
        var row = new Complex[Sw]; var col = new Complex[Sh];
        for (int y = 0; y < Sh; y++) { Array.Copy(a, y * Sw, row, 0, Sw); Pcm.Fft(row, true); Array.Copy(row, 0, a, y * Sw, Sw); }
        for (int x = 0; x < Sw; x++) { for (int y = 0; y < Sh; y++) col[y] = a[y * Sw + x]; Pcm.Fft(col, true); for (int y = 0; y < Sh; y++) a[y * Sw + x] = col[y]; }
        return a.Select(c => c.Real).ToArray();
    }

    // ---------------- filling ----------------
    /// <summary>Frame t's masked pixels from the frames in the window (pos = each frame's camera offset), the median of up to k that saw the
    /// background there (bilinear, nearest frames first); what none saw from the edges inwards. In place.</summary>
    public static int Fill(byte[] cur, byte[] mask, Func<int, (byte[] px, byte[] m)?> frame, int t, (double x, double y)[] pos, int range, int k, int w, int h)
    {
        var holes = new List<int>();
        var cand = new int[3 * Math.Max(1, k)];
        var vals = new int[Math.Max(1, k)];
        int filled = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (mask[i] == 0) continue;
                int n = 0;
                for (int d = 1; d <= range && n < k; d++)
                    for (int s = -1; s <= 1 && n < k; s += 2)
                    {
                        int u = t + s * d;
                        if (u < 0 || u >= pos.Length || frame(u) is not { } f) continue;
                        double qx = x + pos[u].x - pos[t].x, qy = y + pos[u].y - pos[t].y;
                        int x0 = (int)Math.Floor(qx), y0 = (int)Math.Floor(qy);
                        if (x0 < 0 || y0 < 0 || x0 + 1 >= w || y0 + 1 >= h) continue;
                        int j = y0 * w + x0;
                        if (f.m[j] != 0 || f.m[j + 1] != 0 || f.m[j + w] != 0 || f.m[j + w + 1] != 0) continue;
                        double ax = qx - x0, ay = qy - y0;
                        for (int ch = 0; ch < 3; ch++)
                        {
                            double v = (f.px[j * 3 + ch] * (1 - ax) + f.px[(j + 1) * 3 + ch] * ax) * (1 - ay) + (f.px[(j + w) * 3 + ch] * (1 - ax) + f.px[(j + w + 1) * 3 + ch] * ax) * ay;
                            cand[n * 3 + ch] = (int)Math.Round(v);
                        }
                        n++;
                    }
                if (n == 0) { holes.Add(i); continue; }
                for (int ch = 0; ch < 3; ch++)
                {
                    for (int m = 0; m < n; m++) vals[m] = cand[m * 3 + ch];
                    Array.Sort(vals, 0, n);
                    cur[i * 3 + ch] = (byte)vals[n / 2];
                }
                filled++;
            }
        if (holes.Count > 0) Spatial(cur, holes, w, h);
        return filled;
    }

    /// <summary>Holes filled from their edges inwards: each ring takes the mean of its already known 8 neighbours (onion peel), then a light smoothing.</summary>
    static void Spatial(byte[] px, List<int> holes, int w, int h)
    {
        var known = new bool[w * h]; Array.Fill(known, true);
        foreach (var i in holes) known[i] = false;
        var left = new HashSet<int>(holes);
        int guard = 0;
        while (left.Count > 0 && guard++ < w + h)
        {
            var ring = new List<(int i, int b, int g, int r)>();
            foreach (var i in left)
            {
                int x = i % w, y = i / w, n = 0, sb = 0, sg = 0, sr = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if ((dx | dy) == 0 || xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int j = yy * w + xx;
                        if (!known[j]) continue;
                        sb += px[j * 3]; sg += px[j * 3 + 1]; sr += px[j * 3 + 2]; n++;
                    }
                if (n > 0) ring.Add((i, sb / n, sg / n, sr / n));
            }
            if (ring.Count == 0) break;
            foreach (var (i, b, g, r) in ring) { px[i * 3] = (byte)b; px[i * 3 + 1] = (byte)g; px[i * 3 + 2] = (byte)r; known[i] = true; left.Remove(i); }
        }
        // smooth the filled area once (3 × 3 mean of itself and its neighbours) — rings leave streaks
        var copy = (byte[])px.Clone();
        foreach (var i in holes)
        {
            int x = i % w, y = i / w; int n = 0, sb = 0, sg = 0, sr = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = Math.Clamp(x + dx, 0, w - 1), yy = Math.Clamp(y + dy, 0, h - 1), j = yy * w + xx;
                    sb += copy[j * 3]; sg += copy[j * 3 + 1]; sr += copy[j * 3 + 2]; n++;
                }
            px[i * 3] = (byte)(sb / n); px[i * 3 + 1] = (byte)(sg / n); px[i * 3 + 2] = (byte)(sr / n);
        }
    }

    // ---------------- rendering ----------------
    /// <summary>The cleaned copy of the file's [lo, hi) with a second of handles: (its file, the source time its frame 0 shows, frames, the share that came
    /// from other frames, the share filled from the edges).</summary>
    public sealed record Result(string File, double Start, int Frames, double Temporal, double SpatialShare);

    public static async Task<Result> RenderAsync(MediaItem m, IReadOnlyList<MaskTrack> tracks, double lo, double hi, Options o, IProgress<double>? progress, CancellationToken ct)
    {
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        double fps = TimeMap.NativeFps(m);
        int n0 = (int)Math.Max(0, Math.Floor((lo - 1.0) * fps + 1e-6));
        double d = n0 / fps, s1 = Math.Min(m.Duration, hi + 1.0);
        int n = Math.Max(1, (int)Math.Floor((s1 - d) * fps + 1e-6));
        int w = m.Width / 2 * 2, h = m.Height / 2 * 2, size = w * h * 3;
        var outF = TargetOf(m, "cleaned", ".mp4");
        // window: as many frames each side as fit in ~700 MB (picture + mask)
        int range = Math.Clamp(Math.Min(o.Range, 700_000_000 / Math.Max(1, (size + w * h) * 2)), 2, 300);
        var dec = Start(ff, new[] { "-v", "error", "-ss", (d - 0.25 / fps).ToString("0.######", CultureInfo.InvariantCulture), "-i", m.Path, "-map", "0:v:0", "-frames:v", n.ToString(),
                                    "-vf", $"scale={w}:{h}{Hdr.Post(m)}", "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1" }, read: true);
        var enc = Start(ff, new[] { "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", $"{w}x{h}", "-r", TimeUtil.Rational(fps), "-i", "pipe:0",
                                    "-c:v", "libx264", "-crf", "10", "-preset", "medium", "-pix_fmt", "yuv420p", "-movflags", "+faststart", outF }, read: false);
        var frames = new Dictionary<int, (byte[] px, byte[] m)>();
        var pos = new (double x, double y)[n];
        float[]? anchorThumb = null; int anchor = 0;
        int loaded = 0; long temporal = 0, spatial = 0;
        bool Load()
        {
            if (loaded >= n) return false;
            var buf = new byte[size];
            int got = 0; var s = dec.StandardOutput.BaseStream;
            while (got < size) { int r = s.Read(buf, got, size - got); if (r <= 0) break; got += r; }
            if (got < size) { n = loaded; Array.Resize(ref pos, n); return false; }
            var mk = MaskAt(tracks, d + loaded / fps, w, h, o.Expand);
            var th = Thumb(buf, mk, w, h);
            // each frame measured against an anchor frame (a new one every half second): small errors don't pile up frame after frame
            if (anchorThumb != null) { var (dx, dy) = Shift(anchorThumb, th, w, h); pos[loaded] = (pos[anchor].x + dx, pos[anchor].y + dy); }
            if (anchorThumb == null || loaded - anchor >= 15) { anchorThumb = th; anchor = loaded; }
            frames[loaded++] = (buf, mk);
            return true;
        }
        try
        {
            await Task.Run(() =>
            {
                var es = enc.StandardInput.BaseStream;
                for (int t = 0; t < n; t++)
                {
                    ct.ThrowIfCancellationRequested();
                    while (loaded < n && loaded <= t + range && Load()) { }
                    if (t >= n) break;
                    foreach (var old in frames.Keys.Where(k => k < t - range).ToList()) frames.Remove(old);
                    var (px, mk) = frames[t];
                    var outPx = (byte[])px.Clone();
                    int masked = 0; foreach (var b in mk) masked += b;
                    if (masked > 0)
                    {
                        int got = Fill(outPx, mk, u => frames.TryGetValue(u, out var f) ? f : null, t, pos, range, Math.Max(1, o.Candidates), w, h);
                        temporal += got; spatial += masked - got;
                    }
                    es.Write(outPx, 0, outPx.Length);
                    progress?.Report((t + 1) / (double)n);
                }
                es.Close();
            }, ct);
            await enc.WaitForExitAsync(ct);
            if (enc.ExitCode != 0 || !File.Exists(outF)) throw new Exception("the cleaned copy could not be written");
        }
        catch { try { if (!enc.HasExited) enc.Kill(); } catch { } try { File.Delete(outF); } catch { } throw; }
        finally { try { if (!dec.HasExited) dec.Kill(); } catch { } }
        long all = Math.Max(1, temporal + spatial);
        return new Result(outF, d, n, temporal / (double)all, spatial / (double)all);
    }

    /// <summary>A clean plate: the background of the frame at source time at (the median of up to 40 frames of [lo, hi), each lined up on it,
    /// masked pixels left out), as a PNG next to the file.</summary>
    public static async Task<string> CleanPlateAsync(MediaItem m, IReadOnlyList<MaskTrack> tracks, double lo, double hi, double at, IProgress<double>? progress, CancellationToken ct)
    {
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        int w = m.Width / 2 * 2, h = m.Height / 2 * 2, size = w * h * 3;
        int count = Math.Clamp((int)((hi - lo) * 4), 3, 40);
        var times = Enumerable.Range(0, count).Select(i => lo + (hi - lo) * (i + 0.5) / count).Prepend(at).ToList();
        var shots = new List<(byte[] px, byte[] mk)>();
        await Task.Run(() =>
        {
            foreach (var t in times)
            {
                ct.ThrowIfCancellationRequested();
                var p = Start(ff, new[] { "-v", "error", "-ss", t.ToString("0.######", CultureInfo.InvariantCulture), "-i", m.Path, "-map", "0:v:0", "-frames:v", "1", "-vf", $"scale={w}:{h}{Hdr.Post(m)}", "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1" }, read: true);
                var buf = new byte[size]; int got = 0; var s = p.StandardOutput.BaseStream;
                while (got < size) { int r = s.Read(buf, got, size - got); if (r <= 0) break; got += r; }
                p.WaitForExit();
                if (got == size) shots.Add((buf, MaskAt(tracks, t, w, h, 4)));
                progress?.Report(shots.Count / (double)(times.Count + 2));
            }
        }, ct);
        if (shots.Count < 2) throw new Exception("the clip's frames could not be read");
        // every shot lined up on the first (the frame at the playhead)
        var refThumb = Thumb(shots[0].px, shots[0].mk, w, h);
        var off = shots.Select(sh => Shift(refThumb, Thumb(sh.px, sh.mk, w, h), w, h)).ToList();
        var plate = new byte[size]; var vals = new int[shots.Count];
        var holes = new List<int>();
        await Task.Run(() =>
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        int k = 0;
                        for (int sIdx = 0; sIdx < shots.Count; sIdx++)
                        {
                            int qx = (int)Math.Round(x + off[sIdx].dx), qy = (int)Math.Round(y + off[sIdx].dy);
                            if (qx < 0 || qy < 0 || qx >= w || qy >= h) continue;
                            int j = qy * w + qx;
                            if (shots[sIdx].mk[j] != 0) continue;
                            vals[k++] = shots[sIdx].px[j * 3 + ch];
                        }
                        if (k == 0) { if (ch == 0) holes.Add(i); continue; }
                        Array.Sort(vals, 0, k);
                        plate[i * 3 + ch] = (byte)vals[k / 2];
                    }
                }
            if (holes.Count > 0) Spatial(plate, holes, w, h);
        }, ct);
        var outF = TargetOf(m, "clean plate", ".png");
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, plate, w * 3);
        var encd = new PngBitmapEncoder(); encd.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(outF)) encd.Save(fs);
        progress?.Report(1);
        return outF;
    }

    static Process Start(string ff, string[] args, bool read)
    {
        var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = read, RedirectStandardInput = !read, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        return p;
    }
}

public partial class Editor
{
    /// <summary>The clip shows another file whose time t is this one's t + d (a cleaned copy of a stretch): its picture moves to it — In, keyframes,
    /// clip markers re-based by d, its object masks onto shifted copies (the old tracks stay for other pieces / undo), the mask that did the
    /// removal gone. One undo step.</summary>
    public void UseCleaned(Clip c, MediaItem cleaned, double d, string removedMask)
    {
        int shift = MaskGeometry.FrameOf(d);
        Edit(() =>
        {
            if (Clips.FirstOrDefault(x => x.Id == c.Id) is not { } x) return;
            x.MediaId = cleaned.Id; x.In -= d;
            x.NoAudio = cleaned.HasAudio || x.NoAudio;
            if (x.Anim != null) foreach (var tr in x.Anim.Values) foreach (var k in tr.Keys) k.T -= d;
            if (x.Markers != null) foreach (var mk in x.Markers) mk.T -= d;
            x.Masks.RemoveAll(mk => mk.Id == removedMask);
            foreach (var mk in x.Masks)
            {
                var nid = Guid.NewGuid().ToString("N");
                if (MaskTracks.TryGetValue(mk.Id, out var tr))
                {
                    var st = tr.Save(); var copy = new MaskTrack();
                    copy.Load(new MaskTrack.State(new SortedDictionary<int, List<float[]>>(st.Frames.ToDictionary(kv => kv.Key - shift, kv => kv.Value)), new SortedSet<int>(st.Keys.Select(k => k - shift)), st.SeedFrame - shift));
                    MaskTracks[nid] = copy;
                }
                mk.Id = nid;
            }
            x.Follow = null;
        }, "Remove Object");
    }
}

public partial class MainWindow
{
    /// <summary>Effect Controls ▸ Object masks ▸ Remove Object…: the mask's object filled from other frames in a cleaned copy that the clip then shows.</summary>
    async Task RemoveObjectDialog(Clip c, ObjectMask mk)
    {
        if (ed.MediaOf(c) is not { Type: ClipKind.Video, Seq: null, Offline: false } media) { SetTask("Object removal works on video clips", 3000); return; }
        if (!ed.MaskTracks.TryGetValue(mk.Id, out var track) || track.Frames.Count == 0) { SetTask("Select and track the object first (Track ▶)", 3000); return; }
        var panel = new StackPanel();
        var (rr, range) = ToolSlider("Look how far (frames each side)", 5, 150, 60, "0"); panel.Children.Add(rr);
        var (er, grow) = ToolSlider("Grow the mask (pixels)", 0, 40, 6, "0"); panel.Children.Add(er);
        if (Alert.Show(this, "Fills the object with the background from frames where it had moved away (lined up with the camera's movement); what no frame ever " +
                             "shows is filled from its edges. The clip then plays a cleaned copy of its footage — Ctrl+Z goes back. Works best when the object moves and the background doesn't.",
                       "Remove Object", AlertKind.Question, new[] { "Remove", "Cancel" }, panel) != 0) return;
        var o = new ObjectRemoval.Options { Range = (int)range.Value, Expand = grow.Value };
        var (lo, hi) = TimeMap.Span(c);
        await RunTool("Remove object", async ct =>
        {
            var prog = new Progress<double>(x => SetTask($"Removing “{mk.Name}”… {x:0%}", 0));
            var res = await ObjectRemoval.RenderAsync(media, new[] { track }, lo, hi, o, prog, ct);
            var item = await MediaProbe.ProbeAsync(res.File) ?? throw new Exception(MediaProbe.LastError ?? "the cleaned copy could not be read");
            item.Bin = media.Bin; item.Label = media.Label; item.Keywords = media.Keywords;
            ed.Media.Add(item); binRef = null;
            if (ed.Clips.FirstOrDefault(x => x.Id == c.Id) is { } live) ed.UseCleaned(live, item, res.Start, mk.Id);
            if (Mo.ActiveMaskId == mk.Id) Mo.ActiveMaskId = null;
            RefreshBin(); BuildInspector();
            SetTask($"Removed “{mk.Name}”: {res.Temporal:0%} of it from other frames{(res.SpatialShare > 0.005 ? $", {res.SpatialShare:0%} filled from its edges" : "")} — the clip plays “{item.Name}” (Ctrl+Z goes back)", 8000);
        });
    }

    /// <summary>Effect Controls ▸ Object masks ▸ Clean Plate: the background without the object(s), as a still in the Project panel.</summary>
    async Task CleanPlateAsync(Clip c, ObjectMask mk)
    {
        if (ed.MediaOf(c) is not { Type: ClipKind.Video, Seq: null, Offline: false } media) return;
        var tracks = c.Masks.Where(x => x.Enabled && ed.MaskTracks.ContainsKey(x.Id)).Select(x => ed.MaskTracks[x.Id]).ToList();
        var (lo, hi) = TimeMap.Span(c);
        double at = Math.Clamp(TimeMap.Src(c, Math.Clamp(ed.Time, c.Start, c.End - 1e-3)), lo, Math.Max(lo, hi - 1e-3));
        await RunTool("Clean plate", async ct =>
        {
            var f = await ObjectRemoval.CleanPlateAsync(media, tracks, lo, hi, at, new Progress<double>(x => SetTask($"Clean plate… {x:0%}", 0)), ct);
            var item = await MediaProbe.ProbeAsync(f) ?? throw new Exception(MediaProbe.LastError ?? "the clean plate could not be read");
            item.Bin = media.Bin;
            ed.Media.Add(item); binRef = null; ed.RaiseChanged(); RefreshBin(); BinList.SelectedItem = item;
            SetTask($"Clean plate “{item.Name}” in the Project panel — the background without the masked objects (moving things fade out too)", 6000);
        });
    }
}
