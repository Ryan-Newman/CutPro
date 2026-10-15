using System.Text.Json.Serialization;

namespace CutPro;

/// <summary>Clip properties that can be keyframed / driven by a formula (Premiere's stopwatch properties).</summary>
public enum AnimProp { Scale, X, Y, Rotation, Opacity, Blur, Volume, Pan, Speed, CropL, CropT, CropR, CropB, AnchorX, AnchorY, PinTLX, PinTLY, PinTRX, PinTRY, PinBLX, PinBLY, PinBRX, PinBRY, LensCurve, LensEdge, LensCX, LensCY }

/// <summary>Tangent type of one side of a keyframe. Hold (outgoing only) steps to the next key.</summary>
public enum KeyType { Linear, Bezier, Auto, Hold }

/// <summary>A keyframe on a clip property. T is SOURCE time (seconds), like mask outlines, so keys stay on the same
/// picture through trims, moves and razor cuts. Bezier tangents: slope in value units per second and influence as a
/// fraction (0..1) of the neighbouring segment, so they stay valid when keys are retimed.</summary>
public class Keyframe
{
    public double T { get; set; }
    public double V { get; set; }
    public KeyType In { get; set; } = KeyType.Linear;
    public KeyType Out { get; set; } = KeyType.Linear;
    public double InSlope { get; set; }
    public double InInfl { get; set; } = 1.0 / 3;
    public double OutSlope { get; set; }
    public double OutInfl { get; set; } = 1.0 / 3;
    public Keyframe Clone() => (Keyframe)MemberwiseClone();
}

/// <summary>Animation of one property: keyframes (sorted by T) and an optional formula (see <see cref="Formula"/>).</summary>
public class PropTrack
{
    public List<Keyframe> Keys { get; set; } = new();
    public string? Formula { get; set; }
    public bool FormulaOn { get; set; } = true;
    [JsonIgnore] public bool IsEmpty => Keys.Count == 0 && string.IsNullOrWhiteSpace(Formula);
}

/// <summary>Keyframe curve evaluation (1D cubic Bézier segments in time/value, like After Effects' value graph).</summary>
public static class Curve
{
    public static double Eval(IReadOnlyList<Keyframe> k, double t)
    {
        if (k.Count == 0) return 0;
        if (t <= k[0].T) return k[0].V;
        if (t >= k[^1].T) return k[^1].V;
        int lo = 0, hi = k.Count - 1;                               // last key with T <= t
        while (hi - lo > 1) { int mid = (lo + hi) / 2; if (k[mid].T <= t) lo = mid; else hi = mid; }
        return Segment(k, lo, t);
    }

    static double Segment(IReadOnlyList<Keyframe> k, int i, double t)
    {
        var a = k[i]; var b = k[i + 1];
        double dt = b.T - a.T;
        if (dt <= 1e-9) return b.V;
        if (a.Out == KeyType.Hold) return a.V;
        double u = (t - a.T) / dt;
        if (a.Out == KeyType.Linear && b.In == KeyType.Linear) return a.V + (b.V - a.V) * u;
        var (s0, w0) = Tangent(k, i, outgoing: true);
        var (s1, w1) = Tangent(k, i + 1, outgoing: false);
        w0 = Math.Clamp(w0, 0, 1); w1 = Math.Clamp(w1, 0, 1);
        if (w0 + w1 > 1) { double f = 1 / (w0 + w1); w0 *= f; w1 *= f; }    // keep time monotonic
        // control points in (u, value): P0 (0, a), P1 (w0, a + s0·w0·dt), P2 (1 − w1, b − s1·w1·dt), P3 (1, b)
        double y0 = a.V, y1 = a.V + s0 * w0 * dt, y2 = b.V - s1 * w1 * dt, y3 = b.V;
        double x1 = w0, x2 = 1 - w1;
        double s = u;                                                // solve x(s) = u: Newton, bisection fallback
        for (int it = 0; it < 8; it++)
        {
            double x = Bez(0, x1, x2, 1, s) - u, dx = DBez(0, x1, x2, 1, s);
            if (Math.Abs(x) < 1e-7) break;
            if (Math.Abs(dx) < 1e-6) { s = double.NaN; break; }
            s -= x / dx;
            if (s < 0 || s > 1) { s = double.NaN; break; }
        }
        if (double.IsNaN(s))
        {
            double l = 0, h = 1;
            for (int it = 0; it < 40; it++) { s = (l + h) / 2; if (Bez(0, x1, x2, 1, s) < u) l = s; else h = s; }
        }
        return Bez(y0, y1, y2, y3, s);
    }

    static double Bez(double p0, double p1, double p2, double p3, double s)
    { double r = 1 - s; return r * r * r * p0 + 3 * r * r * s * p1 + 3 * r * s * s * p2 + s * s * s * p3; }
    static double DBez(double p0, double p1, double p2, double p3, double s)
    { double r = 1 - s; return 3 * r * r * (p1 - p0) + 6 * r * s * (p2 - p1) + 3 * s * s * (p3 - p2); }

    /// <summary>(slope value/s, influence) of key i's outgoing or incoming side.</summary>
    public static (double slope, double infl) Tangent(IReadOnlyList<Keyframe> k, int i, bool outgoing)
    {
        var key = k[i];
        var type = outgoing ? key.Out : key.In;
        int j = outgoing ? i + 1 : i - 1;
        double straight = j >= 0 && j < k.Count && Math.Abs(k[j].T - key.T) > 1e-9 ? (k[j].V - key.V) / (k[j].T - key.T) : 0;
        return type switch
        {
            KeyType.Bezier => outgoing ? (key.OutSlope, key.OutInfl) : (key.InSlope, key.InInfl),
            KeyType.Auto => (AutoSlope(k, i), 1.0 / 3),
            _ => (straight, 1.0 / 3),
        };
    }

    /// <summary>Auto Bézier: Catmull-Rom slope through the neighbours; flat at the ends and at local peaks (no overshoot).</summary>
    public static double AutoSlope(IReadOnlyList<Keyframe> k, int i)
    {
        if (i <= 0 || i >= k.Count - 1) return 0;
        double a = k[i - 1].V, b = k[i].V, c = k[i + 1].V;
        if ((b - a) * (c - b) <= 0) return 0;
        double dt = k[i + 1].T - k[i - 1].T;
        return dt > 1e-9 ? (c - a) / dt : 0;
    }
}

/// <summary>Property values of clips over time: base value, keyframes, formula. Preview, audio and export all read values
/// here, so they match.</summary>
public static class Anim
{
    public sealed record Info(AnimProp P, string Label, double Min, double Max, double Step, double SoftMin, double SoftMax, string Name);
    public static readonly Info[] Props =
    {
        new(AnimProp.Scale, "Scale %", 0, 10000, 1, 5, 400, "scale"),
        new(AnimProp.X, "Position X %", -100000, 100000, 0.5, -100, 100, "x"),
        new(AnimProp.Y, "Position Y %", -100000, 100000, 0.5, -100, 100, "y"),
        new(AnimProp.Rotation, "Rotation °", -100000, 100000, 0.5, -180, 180, "rotation"),
        new(AnimProp.Opacity, "Opacity %", 0, 100, 1, 0, 100, "opacity"),
        new(AnimProp.Blur, "Blurriness", 0, 1000, 0.5, 0, 200, "blur"),
        new(AnimProp.Volume, "Volume %", 0, 400, 1, 0, 100, "volume"),
        new(AnimProp.Pan, "Reframe pan", -100, 100, 0.5, -100, 100, "pan"),
        new(AnimProp.Speed, "Speed %", TimeMap.MinSpeed, TimeMap.MaxSpeed, 1, 10, 400, "speed"),
        new(AnimProp.CropL, "Crop left %", 0, 100, 0.5, 0, 100, "cropl"),
        new(AnimProp.CropT, "Crop top %", 0, 100, 0.5, 0, 100, "cropt"),
        new(AnimProp.CropR, "Crop right %", 0, 100, 0.5, 0, 100, "cropr"),
        new(AnimProp.CropB, "Crop bottom %", 0, 100, 0.5, 0, 100, "cropb"),
        new(AnimProp.AnchorX, "Anchor X %", -1000, 1000, 0.5, -50, 50, "anchorx"),
        new(AnimProp.AnchorY, "Anchor Y %", -1000, 1000, 0.5, -50, 50, "anchory"),
        new(AnimProp.PinTLX, "Pin ↖ X %", -1000, 1000, 0.5, -50, 150, "pintlx"),
        new(AnimProp.PinTLY, "Pin ↖ Y %", -1000, 1000, 0.5, -50, 150, "pintly"),
        new(AnimProp.PinTRX, "Pin ↗ X %", -1000, 1000, 0.5, -50, 150, "pintrx"),
        new(AnimProp.PinTRY, "Pin ↗ Y %", -1000, 1000, 0.5, -50, 150, "pintry"),
        new(AnimProp.PinBLX, "Pin ↙ X %", -1000, 1000, 0.5, -50, 150, "pinblx"),
        new(AnimProp.PinBLY, "Pin ↙ Y %", -1000, 1000, 0.5, -50, 150, "pinbly"),
        new(AnimProp.PinBRX, "Pin ↘ X %", -1000, 1000, 0.5, -50, 150, "pinbrx"),
        new(AnimProp.PinBRY, "Pin ↘ Y %", -1000, 1000, 0.5, -50, 150, "pinbry"),
        new(AnimProp.LensCurve, "Curvature", -100, 100, 0.5, -100, 100, "lenscurve"),
        new(AnimProp.LensEdge, "Edge curve", -100, 100, 0.5, -100, 100, "lensedge"),
        new(AnimProp.LensCX, "Centre X %", -50, 50, 0.5, -50, 50, "lenscx"),
        new(AnimProp.LensCY, "Centre Y %", -50, 50, 0.5, -50, 50, "lenscy"),
    };
    public static Info Of(AnimProp p) => Props[(int)p];
    public static readonly AnimProp[] Visual = { AnimProp.Scale, AnimProp.X, AnimProp.Y, AnimProp.Rotation, AnimProp.Opacity, AnimProp.Blur, AnimProp.Pan, AnimProp.AnchorX, AnimProp.AnchorY };
    /// <summary>Crop sides (a mask on the picture, not placement: animated crop doesn't need the per-frame layer path).</summary>
    public static readonly AnimProp[] Crop = { AnimProp.CropL, AnimProp.CropT, AnimProp.CropR, AnimProp.CropB };

    /// <summary>Properties a clip has (Effect Controls rows, keyframe editor lanes).</summary>
    public static IEnumerable<AnimProp> PropsOf(Clip c)
    {
        if (c.Kind != ClipKind.Audio) foreach (var p in Visual) if ((p != AnimProp.Pan || Editor.CoversFrame(c)) && !(p is AnimProp.AnchorX or AnimProp.AnchorY && c.Kind == ClipKind.Text)) yield return p;
        if (c.Kind == ClipKind.Audio || (c.Kind == ClipKind.Video && !c.NoAudio)) yield return AnimProp.Volume;
        if (c.Kind == ClipKind.Video && !c.Hold) yield return AnimProp.Speed;   // time remapping (keys only, no formula)
        if (c.Kind is ClipKind.Video or ClipKind.Image && CutPro.Crop.Applied(c)) foreach (var p in Crop) yield return p;   // (lanes only once the clip is cropped)
        if (CutPro.Pin.Active(c)) foreach (var p in CutPro.Pin.Props) yield return p;                                      // (lanes only once the clip is pinned)
        if (CutPro.Lens.Active(c)) foreach (var p in CutPro.Lens.Props) yield return p;                                    // (lanes only with a lens distortion)
    }

    public static double Base(Clip c, AnimProp p) => p switch
    {
        AnimProp.Scale => c.Scale, AnimProp.X => c.X, AnimProp.Y => c.Y, AnimProp.Rotation => c.Rotation,
        AnimProp.Opacity => c.Opacity, AnimProp.Blur => c.Blur, AnimProp.Pan => c.Pan, AnimProp.Speed => c.Speed,
        AnimProp.CropL => c.CropL, AnimProp.CropT => c.CropT, AnimProp.CropR => c.CropR, AnimProp.CropB => c.CropB,
        AnimProp.AnchorX => c.AnchorX, AnimProp.AnchorY => c.AnchorY,
        >= AnimProp.PinTLX and <= AnimProp.PinBRY => c.Pin is { } pin ? pin[p - AnimProp.PinTLX] : new CornerPin()[p - AnimProp.PinTLX],
        AnimProp.LensCurve => c.Lens?.Curvature ?? 0, AnimProp.LensEdge => c.Lens?.Edge ?? 0, AnimProp.LensCX => c.Lens?.CenterX ?? 0, AnimProp.LensCY => c.Lens?.CenterY ?? 0,
        _ => c.Volume,
    };
    public static void SetBase(Clip c, AnimProp p, double v)
    {
        switch (p)
        {
            case AnimProp.Scale: c.Scale = v; break; case AnimProp.X: c.X = v; break; case AnimProp.Y: c.Y = v; break;
            case AnimProp.Rotation: c.Rotation = v; break; case AnimProp.Opacity: c.Opacity = v; break;
            case AnimProp.Blur: c.Blur = v; break; case AnimProp.Pan: c.Pan = v; break; case AnimProp.Speed: c.Speed = v; break;
            case AnimProp.CropL: c.CropL = v; break; case AnimProp.CropT: c.CropT = v; break; case AnimProp.CropR: c.CropR = v; break; case AnimProp.CropB: c.CropB = v; break;
            case AnimProp.AnchorX: c.AnchorX = v; break; case AnimProp.AnchorY: c.AnchorY = v; break;
            case >= AnimProp.PinTLX and <= AnimProp.PinBRY: if (c.Pin != null) c.Pin[p - AnimProp.PinTLX] = v; break;
            case AnimProp.LensCurve: if (c.Lens != null) c.Lens.Curvature = v; break; case AnimProp.LensEdge: if (c.Lens != null) c.Lens.Edge = v; break;
            case AnimProp.LensCX: if (c.Lens != null) c.Lens.CenterX = v; break; case AnimProp.LensCY: if (c.Lens != null) c.Lens.CenterY = v; break;
            default: c.Volume = v; break;
        }
    }

    public static PropTrack? Track(Clip c, AnimProp p) => c.Anim != null && c.Anim.TryGetValue(p, out var tr) ? tr : null;
    public static PropTrack Ensure(Clip c, AnimProp p)
    {
        c.Anim ??= new();
        if (!c.Anim.TryGetValue(p, out var tr)) c.Anim[p] = tr = new PropTrack();
        return tr;
    }
    /// <summary>Drops empty tracks (keeps saved projects and undo snapshots small).</summary>
    public static void Prune(Clip c)
    {
        if (c.Anim == null) return;
        foreach (var p in c.Anim.Where(kv => kv.Value.IsEmpty).Select(kv => kv.Key).ToList()) c.Anim.Remove(p);
        if (c.Anim.Count == 0) c.Anim = null;
    }

    public static bool HasKeys(Clip c, AnimProp p) => Track(c, p) is { Keys.Count: > 0 };
    public static Formula? FormulaOf(Clip c, AnimProp p) =>
        Track(c, p) is { FormulaOn: true, Formula: { } src } && !string.IsNullOrWhiteSpace(src) && Formula.TryGet(src, out var f) ? f : null;
    public static bool IsAnimated(Clip c, AnimProp p) => HasKeys(c, p) || FormulaOf(c, p) != null;
    public static bool AnyAnimated(Clip c, IEnumerable<AnimProp> ps) => c.Anim != null && ps.Any(p => IsAnimated(c, p));

    public static double SrcTime(Clip c, double t) => TimeMap.Src(c, t);
    public static double TimelineTime(Clip c, double src) => TimeMap.Out(c, src);

    /// <summary>Keyframed value at a source time (the base value when there are no keys) — no formula.</summary>
    public static double Keyed(Clip c, AnimProp p, double src) => Track(c, p) is { Keys.Count: > 0 } tr ? Curve.Eval(tr.Keys, src) : Base(c, p);

    /// <summary>Final value at timeline time t: keyframes, then the formula (value = the keyed value), clamped.</summary>
    public static double Value(Clip c, AnimProp p, double t)
    {
        if (c.Anim == null) return Base(c, p);
        double v = Keyed(c, p, SrcTime(c, t));
        if (FormulaOf(c, p) is { } f)
        {
            double r = f.Eval(new FormulaEnv(c, p, t, v));
            if (double.IsFinite(r)) v = r;
        }
        var info = Of(p);
        return Math.Clamp(v, info.Min, info.Max);
    }

    /// <summary>Inserts a key at source time src (frame-snapped) or updates the one there. Returns it.</summary>
    public static Keyframe SetKey(Clip c, AnimProp p, double src, double v)
    {
        src = Math.Round(src * TimeUtil.Fps) / TimeUtil.Fps;
        var keys = Ensure(c, p).Keys;
        var k = keys.FirstOrDefault(x => Math.Abs(x.T - src) < 0.5 / TimeUtil.Fps);
        if (k == null)
        {
            // new keys take the neighbours' interpolation style (so an eased curve stays eased)
            var near = keys.OrderBy(x => Math.Abs(x.T - src)).FirstOrDefault();
            k = new Keyframe { T = src, V = v, In = near?.In ?? KeyType.Linear, Out = near?.Out == KeyType.Hold ? KeyType.Hold : near?.Out ?? KeyType.Linear,
                               InInfl = near?.InInfl ?? 1.0 / 3, OutInfl = near?.OutInfl ?? 1.0 / 3 };
            keys.Add(k); keys.Sort((a, b) => a.T.CompareTo(b.T));
        }
        k.V = v;
        return k;
    }

    public static Keyframe? KeyAt(Clip c, AnimProp p, double src) =>
        Track(c, p)?.Keys.FirstOrDefault(x => Math.Abs(x.T - src) < 0.5 / TimeUtil.Fps);

    /// <summary>Source times of every property keyframe of the clip (timeline diamonds).</summary>
    public static IEnumerable<(AnimProp p, Keyframe k)> AllKeys(Clip c) =>
        c.Anim == null ? Enumerable.Empty<(AnimProp, Keyframe)>() : c.Anim.SelectMany(kv => kv.Value.Keys.Select(k => (kv.Key, k)));

    /// <summary>Per-frame values over a clip (frame k at timeline c.Start + k/fps).</summary>
    public static double[] Sample(Clip c, AnimProp p)
    {
        int n = Math.Max(1, (int)Math.Round(c.Dur * TimeUtil.Fps));
        var v = new double[n];
        for (int k = 0; k < n; k++) v[k] = Value(c, p, c.Start + k / (double)TimeUtil.Fps);
        return v;
    }


    /// <summary>Volume multiplier every AudioOut.GainStep seconds over the clip (preview mixer and export use the same steps).</summary>
    public static float[] GainTable(Clip c)
    {
        int n = (int)Math.Ceiling(c.Dur / AudioOut.GainStep) + 1;
        var g = new float[n];
        for (int k = 0; k < n; k++) g[k] = (float)(Value(c, AnimProp.Volume, c.Start + k * AudioOut.GainStep) / 100);
        return g;
    }
    /// <summary>Linear interpolation style presets for selected keys (Premiere's keyframe context menu).</summary>
    public static void ApplyInterp(Keyframe k, string preset)
    {
        switch (preset)
        {
            case "Linear": k.In = k.Out = KeyType.Linear; break;
            case "Hold": k.Out = KeyType.Hold; break;
            case "Auto Bezier": k.In = k.Out = KeyType.Auto; break;
            case "Ease In": k.In = KeyType.Bezier; k.InSlope = 0; k.InInfl = 0.33; if (k.Out == KeyType.Hold) k.Out = KeyType.Linear; break;
            case "Ease Out": k.Out = KeyType.Bezier; k.OutSlope = 0; k.OutInfl = 0.33; break;
            case "Easy Ease": k.In = k.Out = KeyType.Bezier; k.InSlope = k.OutSlope = 0; k.InInfl = k.OutInfl = 0.33; break;
            case "Strong Ease": k.In = k.Out = KeyType.Bezier; k.InSlope = k.OutSlope = 0; k.InInfl = k.OutInfl = 0.66; break;
        }
    }
    public static readonly string[] InterpPresets = { "Linear", "Hold", "Auto Bezier", "Ease In", "Ease Out", "Easy Ease", "Strong Ease" };
}
