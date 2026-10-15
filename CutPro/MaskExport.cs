using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Path = System.Windows.Shapes.Path;

namespace CutPro;

/// <summary>
/// Export side of object masks: renders each enabled mask's feathered outline for every frame of its clip (same geometry
/// and WPF blur as the preview) into a lossless grey mask video (FFV1) at the clip's placed size. The exporter then applies
/// the effect with ffmpeg: maskedmerge(original, effected, mask) — or alphamerge for cut-outs.
/// </summary>
public static class MaskExport
{
    /// <summary>Masks that export for a clip, in order: every non-cut-out mask, then the first cut-out.</summary>
    public static List<ObjectMask> Exported(Clip c)
    {
        var on = c.Masks.Where(m => m.Enabled).ToList();
        var list = on.Where(m => m.Effect != MaskFx.CutOut).ToList();
        if (on.FirstOrDefault(m => m.Effect == MaskFx.CutOut) is { } cut) list.Add(cut);
        return list;
    }

    /// <summary>Renders mask videos. Returns "clipId|maskId" → file. UI thread (WPF rendering).</summary>
    /// <param name="scale">output pixels per logical pixel (masks are rendered at the clip's export size)</param>
    public static Dictionary<string, string> Prepare(Editor ed, string dir, IProgress<double>? progress = null, double scale = 0)
    {
        if (scale <= 0) scale = Editor.OutScale;
        var map = new Dictionary<string, string>();
        var ff = Exporter.FindFfmpeg();
        if (ff == null) return map;
        var tplan = Transitions.Compute(ed);
        var jobs = Transitions.Extended(ed.Clips, tplan).Where(   // (lengthened by transitions)
            c => c.Kind is ClipKind.Video or ClipKind.Image && !ed.TrackOff[c.Track] && ed.MediaOf(c) is { Offline: false })
                           .SelectMany(c => Exported(c).Select(m => (c, m))).ToList();
        if (jobs.Count == 0) return map;
        Directory.CreateDirectory(dir);
        int jobIndex = 0;
        foreach (var (c, m) in jobs)
        {
            // animated / rotated clips: masks at the layer's base size, feather and expand scaled to the displayed size per frame
            var lp = LayerPlan.Needed(c) || Transitions.Moves(tplan, c) ? LayerPlan.For(ed.MediaOf(c), c, scale) : null;   // (the exporter's choice: masks at its layer size)
            var (w, h) = Exporter.ClipSize(ed.MediaOf(c), c, scale, lp);

            int n = Math.Max(1, (int)Math.Round(c.Dur * TimeUtil.Fps));
            var file = System.IO.Path.Combine(dir, $"mask_{c.Id}_{m.Id}.mkv");
            var psi = new ProcessStartInfo(ff) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "gray", "-s", $"{w}x{h}", "-r", TimeUtil.Rate,
                                      "-i", "-", "-c:v", "ffv1", file }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
            var stdin = p.StandardInput.BaseStream;
            var track = ed.MaskTracks.GetValueOrDefault(m.Id);
            List<float[]>? last = null; byte[]? lastFrame = null; double lastPx = 1;
            for (int k = 0; k < n; k++)
            {
                var polys = track?.At(Editor.SourceFrame(c, c.Start + k / TimeUtil.Fps));   // (the preview's frame for this time: speed / reverse / hold included)
                double pxs = lp != null ? lp.PxScale(k) * scale : scale;     // feather / expand: logical px → mask px
                if (lastFrame == null || !ReferenceEquals(polys, last) || Math.Abs(pxs - lastPx) > 1e-4) { lastFrame = Render(polys, m, w, h, pxs); last = polys; lastPx = pxs; }
                stdin.Write(lastFrame, 0, lastFrame.Length);
                if (k % 10 == 0) progress?.Report((jobIndex + (double)k / n) / jobs.Count);
            }
            stdin.Close();
            p.WaitForExit();
            if (p.ExitCode == 0) map[$"{c.Id}|{m.Id}"] = file;
            jobIndex++;
        }
        progress?.Report(1);
        return map;
    }

    /// <summary>One mask frame, w×h grey (255 = effect applies), rendered like the preview's opacity mask.</summary>
    public static byte[] Render(List<float[]>? polys, ObjectMask m, int w, int h, double unit = 1)
    {
        var area = MaskGeometry.Area(MaskGeometry.Outline(polys, w, h, m.Expand * unit), w, h, m.Invert, m.Feather * unit);
        var root = new Grid { Width = w, Height = h, Background = Brushes.Black };
        var canvas = new Canvas { Width = w, Height = h };
        canvas.Children.Add(new Path { Data = area ?? Geometry.Empty, Fill = Brushes.White, Effect = TitleVisual.Blur(m.Feather * unit) });
        root.Children.Add(canvas);
        root.Measure(new Size(w, h)); root.Arrange(new Rect(0, 0, w, h)); root.UpdateLayout();
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        var g = new byte[w * h];
        for (int i = 0; i < g.Length; i++) g[i] = px[i * 4 + 1];
        return g;
    }

    /// <summary>Filter-graph lines turning [inL] (gbrp) into the effected [outL] (gbrp). Blur matches the preview's
    /// BlurEffect overlay: blurred with transparent surroundings (edges fade) and laid over the sharp original.</summary>
    public static string Graph(ObjectMask m, string inL, string outL, double scale = 1)
    {
        if (m.Effect != MaskFx.Blur) return $"[{inL}]{Filter(m, scale)}[{outL}]";
        double sigma = Exporter.BlurSigma(Math.Max(0.1, m.Amount) * scale);
        int bp = (int)Math.Ceiling(sigma * 3) + 2;
        return $"[{inL}]split[{inL}a][{inL}b]\n" +
               $"[{inL}b]format=gbrap,pad=iw+{2 * bp}:ih+{2 * bp}:{bp}:{bp}:color=black@0,gblur=sigma={sigma.ToString("0.####", CultureInfo.InvariantCulture)}," +
               $"unpremultiply=inplace=1,crop=iw-{2 * bp}:ih-{2 * bp}:{bp}:{bp}[{inL}c]\n" +
               $"[{inL}a][{inL}c]overlay=format=gbrp,format=gbrp[{outL}]";
    }

    /// <summary>ffmpeg filter that makes the "effected" version of a clip for a mask (input/output gbrp).</summary>
    public static string Filter(ObjectMask m, double scale = 1)
    {
        static string F(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
        switch (m.Effect)
        {
            case MaskFx.Blur: return $"gblur=sigma={F(Exporter.BlurSigma(Math.Max(0.1, m.Amount) * scale))}";
            case MaskFx.Pixelate: int b = Math.Max(2, (int)Math.Round(m.Amount * scale)); return $"pixelize=w={b}:h={b}:m=avg";
            case MaskFx.Darken: var k = F(Math.Clamp(1 - m.Amount / 100, 0, 1)); return $"colorchannelmixer=rr={k}:gg={k}:bb={k}";
            case MaskFx.Desaturate: return "colorchannelmixer=rr=0.299:rg=0.587:rb=0.114:gr=0.299:gg=0.587:gb=0.114:br=0.299:bg=0.587:bb=0.114";
            case MaskFx.Redact: return "colorchannelmixer=rr=0:gg=0:bb=0";
            case MaskFx.Tint:
                var col = TitleVisual.ParseColor(m.TintColor, Colors.Yellow);
                var a = F(Math.Clamp(m.Amount / 100, 0, 1));
                return $"format=rgb24,lutrgb=r='val*(1-{a})+{col.R}*{a}':g='val*(1-{a})+{col.G}*{a}':b='val*(1-{a})+{col.B}*{a}',format=gbrp";
            default: return "null";
        }
    }
}
