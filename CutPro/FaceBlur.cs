using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CutPro;

/// <summary>
/// YuNet face detection (OpenCV's model zoo, MIT, ~230 KB, downloaded on first use with consent): BGR 0…255 NCHW in (padded to a multiple of
/// 32), per stride 8 / 16 / 32 a grid of class / objectness scores and box offsets out — decoded exactly like OpenCV's FaceDetectorYN
/// (score = √(cls · obj), centre = (cell + offset) · stride, size = e^v · stride), then non-maximum suppression.
/// </summary>
public sealed class FaceDetector : IDisposable
{
    public const string File = "face_detection_yunet_2023mar.onnx";
    public static readonly (string file, string url)[] Files =
        { (File, "https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx") };
    public static bool Present => ModelStore.Present(Files);
    /// <summary>A face in the image's pixels.</summary>
    public readonly record struct Face(float X, float Y, float W, float H, float Score);

    readonly InferenceSession s;
    readonly string input;
    readonly int fixedW, fixedH;
    static readonly int[] Strides = { 8, 16, 32 };

    public FaceDetector()
    {
        s = new InferenceSession(ModelStore.PathOf(File));
        var im = s.InputMetadata.First();
        input = im.Key;
        var d = im.Value.Dimensions;
        if (d.Length == 4 && d[2] > 0 && d[3] > 0) { fixedH = d[2]; fixedW = d[3]; }
    }
    public string Describe() =>
        $"in: {string.Join(", ", s.InputMetadata.Select(kv => $"{kv.Key}[{string.Join("x", kv.Value.Dimensions)}]"))}; out: {string.Join(", ", s.OutputMetadata.Select(kv => $"{kv.Key}[{string.Join("x", kv.Value.Dimensions)}]"))}";

    /// <summary>Faces in a w × h BGR image (3 bytes a pixel), scores ≥ threshold.</summary>
    public List<Face> Detect(byte[] bgr, int w, int h, float threshold = 0.8f, float nms = 0.3f)
    {
        int pw = fixedW > 0 ? fixedW : ((w - 1) / 32 + 1) * 32, ph = fixedH > 0 ? fixedH : ((h - 1) / 32 + 1) * 32;
        double k = fixedW > 0 ? Math.Min(1, Math.Min(pw / (double)w, ph / (double)h)) : 1;     // (a fixed-size model: the image scaled into it)
        var t = new DenseTensor<float>(new[] { 1, 3, ph, pw });
        var buf = t.Buffer.Span; int plane = pw * ph;
        int iw = (int)(w * k), ih = (int)(h * k);
        for (int y = 0; y < ih; y++)
        {
            int sy = Math.Min(h - 1, (int)(y / k));
            for (int x = 0; x < iw; x++)
            {
                int sx = Math.Min(w - 1, (int)(x / k)), si = (sy * w + sx) * 3, di = y * pw + x;
                buf[di] = bgr[si]; buf[plane + di] = bgr[si + 1]; buf[2 * plane + di] = bgr[si + 2];
            }
        }
        using var res = s.Run(new[] { NamedOnnxValue.CreateFromTensor(input, t) });
        var outs = res.ToDictionary(r => r.Name, r => r.AsEnumerable<float>().ToArray());
        float[] Out(string kind, int stride) => outs.TryGetValue($"{kind}_{stride}", out var a) ? a : throw new InvalidOperationException($"YuNet output {kind}_{stride} missing ({Describe()})");
        var faces = new List<Face>();
        foreach (int st in Strides)
        {
            float[] cls = Out("cls", st), obj = Out("obj", st), box = Out("bbox", st);
            int cols = pw / st, rows = ph / st;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    if (i >= cls.Length) break;
                    float score = MathF.Sqrt(Math.Clamp(cls[i], 0, 1) * Math.Clamp(obj[i], 0, 1));
                    if (score < threshold) continue;
                    float cx = (c + box[i * 4]) * st, cy = (r + box[i * 4 + 1]) * st, bw = MathF.Exp(box[i * 4 + 2]) * st, bh = MathF.Exp(box[i * 4 + 3]) * st;
                    faces.Add(new Face((float)((cx - bw / 2) / k), (float)((cy - bh / 2) / k), (float)(bw / k), (float)(bh / k), score));
                }
        }
        return Nms(faces, nms);
    }

    public static float IoU(Face a, Face b)
    {
        float x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y), x1 = Math.Min(a.X + a.W, b.X + b.W), y1 = Math.Min(a.Y + a.H, b.Y + b.H);
        float i = Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0), u = a.W * a.H + b.W * b.H - i;
        return u > 0 ? i / u : 0;
    }
    static List<Face> Nms(List<Face> f, float thr)
    {
        var keep = new List<Face>();
        foreach (var a in f.OrderByDescending(x => x.Score)) if (keep.All(b => IoU(a, b) < thr)) keep.Add(a);
        return keep;
    }
    public void Dispose() => s.Dispose();
}

/// <summary>
/// Faces through a clip: detections on every few source frames (normalised boxes) joined into tracks by overlap, smoothed, and turned into
/// ellipse mask outlines (a little larger than the box: hair, ears, chin) keyed by source frame — with empty outlines just before a face
/// appears and just after it leaves, so its mask covers only the frames it is in (<see cref="MaskTrack.At"/> holds outlines at the ends).
/// </summary>
public static class FaceTracks
{
    /// <summary>A box in 0…1 of the frame.</summary>
    public readonly record struct Box(double X, double Y, double W, double H)
    {
        public double Cx => X + W / 2; public double Cy => Y + H / 2;
        public double IoU(Box b)
        {
            double x0 = Math.Max(X, b.X), y0 = Math.Max(Y, b.Y), x1 = Math.Min(X + W, b.X + b.W), y1 = Math.Min(Y + H, b.Y + b.H);
            double i = Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0), u = W * H + b.W * b.H - i;
            return u > 0 ? i / u : 0;
        }
    }
    public sealed class Track { public readonly SortedDictionary<int, Box> At = new(); public int Last => At.Keys.Last(); }

    /// <summary>Detections (source frame → boxes) into tracks: each box joins the open track it overlaps most (IoU ≥ 0.2, or its centre within the
    /// track's last box); a track closes after <paramref name="gap"/> frames without its face; tracks seen fewer than <paramref name="minHits"/>
    /// times are dropped (a stray detection).</summary>
    public static List<Track> Build(IEnumerable<(int frame, List<Box> boxes)> detections, int gap = 20, int minHits = 3)
    {
        var open = new List<Track>(); var all = new List<Track>();
        foreach (var (f, boxes) in detections.OrderBy(d => d.frame))
        {
            open.RemoveAll(t => f - t.Last > gap);
            var taken = new HashSet<Track>();
            foreach (var b in boxes.OrderByDescending(b => b.W * b.H))
            {
                Track? best = null; double bestV = 0;
                foreach (var t in open.Where(t => !taken.Contains(t)))
                {
                    var l = t.At[t.Last];
                    double v = Math.Max(b.IoU(l), Math.Abs(b.Cx - l.Cx) < l.W / 2 && Math.Abs(b.Cy - l.Cy) < l.H / 2 ? 0.2 : 0);
                    if (v >= 0.2 && v > bestV) { bestV = v; best = t; }
                }
                if (best == null) { best = new Track(); open.Add(best); all.Add(best); }
                best.At[f] = b; taken.Add(best);
            }
        }
        return all.Where(t => t.At.Count >= minHits).ToList();
    }

    /// <summary>A track's boxes averaged with their neighbours (detections jitter by a few pixels from frame to frame).</summary>
    public static List<(int frame, Box box)> Smooth(Track t)
    {
        var l = t.At.ToList(); var r = new List<(int, Box)>();
        for (int i = 0; i < l.Count; i++)
        {
            int a = Math.Max(0, i - 1), b = Math.Min(l.Count - 1, i + 1); double n = b - a + 1;
            double x = 0, y = 0, w = 0, h = 0;
            for (int j = a; j <= b; j++) { var v = l[j].Value; x += v.Cx; y += v.Cy; w += v.W; h += v.H; }
            r.Add((l[i].Key, new Box(x / n - w / n / 2, y / n - h / n / 2, w / n, h / n)));
        }
        return r;
    }

    /// <summary>The ellipse covering a face box (larger than the box, nudged up for the forehead and hair), as a mask polygon.</summary>
    public static List<float[]> Outline(Box b, double grow = 1.0)
    {
        double rx = b.W * 0.66 * grow, ry = b.H * 0.78 * grow, cx = b.Cx, cy = b.Cy - b.H * 0.08;
        var p = new float[64];
        for (int i = 0; i < 32; i++) { double a = i * Math.PI * 2 / 32; p[i * 2] = (float)(cx + rx * Math.Cos(a)); p[i * 2 + 1] = (float)(cy + ry * Math.Sin(a)); }
        return new List<float[]> { p };
    }

    /// <summary>A track as a mask track: its outlines on its frames, nothing before its first or after its last (within the clip's frames f0…f1).</summary>
    public static MaskTrack ToMask(Track t, int f0, int f1, double grow = 1.0)
    {
        var tr = new MaskTrack();
        var sm = Smooth(t);
        foreach (var (f, b) in sm) tr.Set(f, Outline(b, grow));
        int first = sm[0].frame, last = sm[^1].frame;
        if (first > f0) tr.Set(first - 1, new List<float[]>());
        if (last < f1) tr.Set(last + 1, new List<float[]>());
        tr.SeedFrame = first;
        return tr;
    }

    /// <summary>Finds the faces in a clip's source frames f0…f1: frames decoded on the sequence's frame grid at ≤ 640 px wide, detected every
    /// <paramref name="step"/> frames. Background thread.</summary>
    public static List<Track> Find(MediaItem m, int f0, int f1, float threshold, int step, IProgress<double>? progress, CancellationToken ct, int width = 640)
    {
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        int w = Math.Min(width, m.Width) / 2 * 2, h = Math.Max(2, (int)Math.Round(m.Height * (double)w / Math.Max(1, m.Width) / 2) * 2);
        int n = f1 - f0 + 1;
        var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-nostdin", "-ss", Math.Max(0, f0 / TimeUtil.Fps - 1e-4).ToString("0.######", CultureInfo.InvariantCulture), "-i", m.Path, "-an", "-sn",
                                  "-vf", $"fps={TimeUtil.Rate},scale={w}:{h}:flags=bilinear", "-frames:v", n.ToString(), "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var det = new FaceDetector();
        var frame = new byte[w * h * 3]; var src = p.StandardOutput.BaseStream;
        var found = new List<(int, List<Box>)>();
        try
        {
            for (int k = 0; k < n; k++)
            {
                ct.ThrowIfCancellationRequested();
                int got = 0; while (got < frame.Length) { int r = src.Read(frame, got, frame.Length - got); if (r <= 0) break; got += r; }
                if (got < frame.Length) break;
                if (k % step != 0 && k != n - 1) continue;
                var faces = det.Detect(frame, w, h, threshold);
                found.Add((f0 + k, faces.Select(fc => new Box(fc.X / w, fc.Y / h, fc.W / w, fc.H / h)).ToList()));
                if ((k & 7) == 0) progress?.Report((double)k / n);
            }
        }
        finally { try { if (!p.HasExited) p.Kill(); } catch { } }
        return Build(found, gap: Math.Max(10, step * 6), minHits: Math.Min(3, Math.Max(1, found.Count / 4)));
    }
}

public partial class Editor
{
    /// <summary>A mask per face track on the clip (one undo step): blur / pixelate / black out with the given strength.</summary>
    public List<ObjectMask> AddFaceMasks(Clip c, List<FaceTracks.Track> tracks, MaskFx fx, double amount, double grow = 1.0)
    {
        var (f0, f1) = SourceFrames(c);
        var made = new List<ObjectMask>();
        foreach (var t in tracks.OrderBy(t => t.At.Keys.First()))
        {
            var m = new ObjectMask { Kind = MaskKind.Ellipse, Name = $"Face {made.Count + 1}", Effect = fx, Amount = amount, Feather = 6 };
            MaskTracks[m.Id] = FaceTracks.ToMask(t, f0, f1, grow);
            made.Add(m);
        }
        if (made.Count > 0) Edit(() => c.Masks.AddRange(made), "Blur Faces");
        return made;
    }
}

public partial class MainWindow
{
    async Task<bool> EnsureFaceModelAsync()
    {
        if (FaceDetector.Present) return true;
        if (!Alert.Confirm(this, "Face blur uses YuNet, a small face-detection model from OpenCV's model collection (github.com/opencv/opencv_zoo, MIT licence, about 230 KB). " +
                                 "It is downloaded once into CutPro's model folder; faces are then found on this PC — nothing is uploaded.\n\nDownload it now?",
                           "Blur Faces", "Download", "Cancel")) return false;
        try
        {
            SetTask("Downloading the face model…", 0);
            await ModelStore.DownloadAsync(FaceDetector.Files, new Progress<double>(x => SetTask($"Downloading the face model… {x:0%}", 0)), CancellationToken.None);
            SetTask("", 1);
            return true;
        }
        catch (Exception ex) { SetTask("", 1); Alert.Show(this, ex.Message, "Could not download the face model", AlertKind.Error); return false; }
    }

    /// <summary>Clip ▸ Blur Faces…: every face in the selected video clips gets a tracked ellipse mask with the chosen effect.</summary>
    async Task BlurFacesDialog()
    {
        var clips = SelectedClips(c => c.Kind == ClipKind.Video && ed.MediaOf(c) is { Seq: null, Offline: false });
        if (clips.Count == 0) { SetTask("Select video clips first", 2500); return; }
        var panel = new StackPanel();
        var fx = new ComboBox { ItemsSource = new[] { "Blur", "Pixelate", "Black out" }, SelectedIndex = 0, Margin = new Thickness(0, 6, 0, 4) };
        panel.Children.Add(fx);
        var (sr, strength) = ToolSlider("Strength", 5, 100, 35, "0"); panel.Children.Add(sr);
        var (gr, grow) = ToolSlider("Mask size (%)", 80, 160, 100, "0"); panel.Children.Add(gr);
        var (tr, sens) = ToolSlider("Sensitivity", 0, 100, 60, "0"); panel.Children.Add(tr);
        var small = new CheckBox { Content = "Find small faces too (slower)", Margin = new Thickness(0, 6, 0, 0) }; panel.Children.Add(small);
        if (Alert.Show(this, "Finds every face in the selected clips and covers each with a tracked mask. Wrong ones can be deleted in Effect Controls ▸ Object masks.",
                       "Blur Faces", AlertKind.Question, new[] { "Blur Faces", "Cancel" }, panel) != 0) return;
        if (!await EnsureFaceModelAsync()) return;
        var effect = fx.SelectedIndex switch { 1 => MaskFx.Pixelate, 2 => MaskFx.Redact, _ => MaskFx.Blur };
        double amount = effect == MaskFx.Pixelate ? Math.Max(4, strength.Value / 2) : strength.Value;
        float threshold = (float)(0.95 - sens.Value / 100 * 0.35);                  // (sensitivity 60 → 0.74)
        int width = small.IsChecked == true ? 1280 : 640, total = 0;
        await RunTool("Finding faces", async ct =>
        {
            for (int i = 0; i < clips.Count; i++)
            {
                var c = clips[i]; var m = ed.MediaOf(c)!;
                var (f0, f1) = Editor.SourceFrames(c);
                int step = Math.Max(1, (int)Math.Round(TimeUtil.Fps / 15));             // (15 detections a second)
                int ii = i;
                var prog = new Progress<double>(x => SetTask($"Finding faces in clip {ii + 1} / {clips.Count}… {x:0%}", 0));   // (made here: reports on the UI thread)
                var tracks = await Task.Run(() => FaceTracks.Find(m, f0, f1, threshold, step, prog, ct, width), ct);
                var live = ed.Clips.FirstOrDefault(x => x.Id == c.Id);
                if (live != null) total += ed.AddFaceMasks(live, tracks, effect, amount, grow.Value / 100).Count;
            }
            SetTask(total == 0 ? "No faces found" : $"{total} face{(total == 1 ? "" : "s")} covered — Effect Controls ▸ Object masks lists them (delete any that aren't faces)", 5000);
            BuildInspector(); Pv.InvalidateVisual();
        });
    }
}
