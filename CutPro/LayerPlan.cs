using System.Globalization;
using System.IO;
using System.Text;

namespace CutPro;

/// <summary>
/// Export plan for a video / image clip whose motion, rotation, opacity or blur is animated (or that is rotated): every
/// frame's placement from <see cref="Editor.Placement"/> and <see cref="Anim.Value"/> — the numbers the preview uses —
/// turned into ffmpeg sendcmd files. The clip is decoded at a fixed base size (its largest scale, so nothing is upscaled),
/// rotated there, scaled per frame and placed on a transparent canvas, which is then blurred and faded.
/// </summary>
public sealed class LayerPlan
{
    public int Bw, Bh, D, Pad, N;
    public double S = 1;
    public bool Rotates, Blurs, Fades;
    public double[] R = Array.Empty<double>(), O = Array.Empty<double>(), B = Array.Empty<double>();
    double[] w = Array.Empty<double>(), h = Array.Empty<double>(), px = Array.Empty<double>(), py = Array.Empty<double>();

    public static bool Needed(Clip c) => c.Kind is ClipKind.Video or ClipKind.Image && (Anim.AnyAnimated(c, Anim.Visual) || Math.Abs(c.Rotation) > 1e-6);

    /// <param name="scale">output pixels per logical (preview) pixel — positions, sizes and blur are rendered at the output size</param>
    /// <param name="look">a transition's effect per timeline time: push offset, or a Move transition's offset / scale / turn about the frame
    /// centre / opacity (<see cref="Transitions.LookInfo.Apply"/> — the preview applies the same)</param>
    public static LayerPlan For(MediaItem? m, Clip c, double scale = 1, Func<double, Transitions.LookInfo>? look = null)
    {
        var p = new LayerPlan { S = scale };
        p.N = Math.Max(1, (int)Math.Round(c.Dur * TimeUtil.Fps));
        p.R = new double[p.N]; p.O = new double[p.N]; p.B = new double[p.N];
        p.w = new double[p.N]; p.h = new double[p.N]; p.px = new double[p.N]; p.py = new double[p.N];
        double smax = 0;
        for (int k = 0; k < p.N; k++)
        {
            double t = c.Start + k / (double)TimeUtil.Fps;
            var pl = Editor.Placement(m, c, t);
            var lk = look?.Invoke(t);
            if (lk is { } l) pl = l.Apply(pl);
            var (x, y, ww, hh, rot) = pl;
            p.px[k] = x * scale; p.py[k] = y * scale; p.w[k] = ww * scale; p.h[k] = hh * scale; p.R[k] = rot;
            p.O[k] = Anim.Value(c, AnimProp.Opacity, t) * (lk is { Moves: true } lm ? lm.Opacity : 1);
            p.B[k] = Anim.Value(c, AnimProp.Blur, t);
            smax = Math.Max(smax, Anim.Value(c, AnimProp.Scale, t));
        }
        Editor.Fit(m, Math.Max(smax, 0.5), false, out double bw, out double bh, Editor.CoversFrame(c));
        p.Bw = ExportSettings.Even(bw * scale); p.Bh = ExportSettings.Even(bh * scale);
        p.Rotates = p.R.Any(r => Math.Abs(r) > 1e-6);
        p.D = p.Rotates ? (int)Math.Ceiling(Math.Sqrt((double)p.Bw * p.Bw + (double)p.Bh * p.Bh) / 2 + 1) * 2 : 0;   // in base (output-scaled) pixels
        double bmax = p.B.Max();
        p.Blurs = bmax > 0.05;
        p.Pad = p.Blurs ? 2 * (int)Math.Ceiling((3 * Exporter.BlurSigma(bmax * scale) + 2) / 2) : 0;
        p.Fades = Anim.IsAnimated(c, AnimProp.Opacity) || c.Opacity < 100 || p.O.Any(o => o < 99.999);
        return p;
    }

    /// <summary>Base-size pixels per displayed pixel at frame k (mask feather / expand are in displayed pixels).</summary>
    public double PxScale(int k) => w[Math.Clamp(k, 0, N - 1)] > 1e-6 ? Bw / w[Math.Clamp(k, 0, N - 1)] : 1;

    /// <summary>Layer size and canvas position (integer pixels) at frame k: the (rotated) base frame scaled to the displayed size,
    /// centred where the preview centres the clip.</summary>
    public (int w, int h, int x, int y) Box(int k)
    {
        double f = w[k] / Math.Max(1, Bw);
        double lw = Rotates ? D * f : w[k], lh = Rotates ? D * f : h[k];
        int iw = Math.Max(1, (int)Math.Round(lw)), ih = Math.Max(1, (int)Math.Round(lh));
        double cx = px[k] + w[k] / 2 + Pad, cy = py[k] + h[k] / 2 + Pad;
        return (iw, ih, (int)Math.Round(cx - iw / 2.0), (int)Math.Round(cy - ih / 2.0));
    }

    public double Sigma(int k) => B[k] > 0.05 ? Exporter.BlurSigma(B[k] * S) : 0;

    /// <summary>Writes the two sendcmd files (before the layer / after the canvas) into the temp folder, where ffmpeg runs, and
    /// returns their bare names (no drive colon to escape in the filter script).</summary>
    public (string a, string b) WriteCommands(string v, List<string> temps)
    {
        static string F(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
        var id = Guid.NewGuid().ToString("N")[..8];
        string na = $"cutpro_cmd_{id}_{v}a.txt", nb = $"cutpro_cmd_{id}_{v}b.txt";
        var a = new StringBuilder(); var b = new StringBuilder();
        var prev = Box(0);
        for (int k = 1; k < N; k++)
        {
            string time = F(k / (double)TimeUtil.Fps - 0.0005);
            var box = Box(k);
            var ca = new List<string>();
            if (Rotates && R[k] != R[k - 1]) ca.Add($"[enter] rotate@r{v} a {F(R[k] * Math.PI / 180)}");
            if (box.w != prev.w) ca.Add($"[enter] scale@s{v} w {box.w}");
            if (box.h != prev.h) ca.Add($"[enter] scale@s{v} h {box.h}");
            if (box.x != prev.x) ca.Add($"[enter] overlay@o{v} x {box.x}");
            if (box.y != prev.y) ca.Add($"[enter] overlay@o{v} y {box.y}");
            if (ca.Count > 0) a.Append(time).Append(' ').Append(string.Join(", ", ca)).Append(";\n");
            prev = box;
            var cb = new List<string>();
            if (Blurs && Sigma(k) != Sigma(k - 1)) cb.Add($"[enter] gblur@g{v} sigma {F(Sigma(k))}");
            if (Fades && O[k] != O[k - 1]) cb.Add($"[enter] colorchannelmixer@c{v} aa {F(Math.Clamp(O[k] / 100, 0, 1))}");
            if (cb.Count > 0) b.Append(time).Append(' ').Append(string.Join(", ", cb)).Append(";\n");
        }
        // sendcmd needs at least one entry
        if (a.Length == 0) a.Append($"0 [enter] scale@s{v} w {prev.w};\n");
        if (b.Length == 0) b.Append($"{F(N / (double)TimeUtil.Fps + 1)} [enter] overlay@o{v} x {Box(N - 1).x};\n");
        string dir = Path.GetTempPath();
        File.WriteAllText(Path.Combine(dir, na), a.ToString()); File.WriteAllText(Path.Combine(dir, nb), b.ToString());
        temps.Add(Path.Combine(dir, na)); temps.Add(Path.Combine(dir, nb));
        return (na, nb);
    }
}
