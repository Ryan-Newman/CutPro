using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace CutPro;

public enum TransAlign { Center, Start, End }

/// <summary>A transition at an edit point (stored on the clip: <see cref="Clip.TransIn"/> / <see cref="Clip.TransOut"/>).</summary>
public class Transition
{
    public string Kind { get; set; } = "Cross Dissolve";
    public double Dur { get; set; } = 1;
    /// <summary>Centre on the cut, start at the cut (all inside the incoming clip) or end at the cut (inside the outgoing one).</summary>
    public TransAlign Align { get; set; } = TransAlign.Center;
    public Transition Clone() => (Transition)MemberwiseClone();
}

/// <summary>
/// Transitions between clips (Premiere-style, at edit points). <see cref="Compute"/> resolves every transition on the timeline
/// into a window [T0, T1] on its track with the outgoing clip A and incoming clip B (either may be missing: a transition from /
/// to nothing reveals the tracks below). During the window A is shown past its out point and B before its in point (media
/// handles; frames repeat at the ends of the media), and <see cref="Look"/> gives each clip's opacity, clip region, opacity
/// mask and offset — the same numbers the exporter turns into ffmpeg (xfade on the track composite for dissolve / wipe / iris,
/// a colour layer for dips, per-frame positions for push). Audio crossfades lengthen both clips and fade them.
/// </summary>
public static class Transitions
{
    /// <summary>Mask = the incoming shot shows inside a shape that grows (<see cref="Shape"/>); Move = the shots move, scale, turn and fade
    /// (<see cref="Xform"/>) — both computed here for the preview and the export.</summary>
    public enum Fx { Dissolve, Dip, Wipe, Push, Iris, Audio, Mask, Move }
    public sealed record Def(string Name, Fx Fx, string? XFade, int Dx, int Dy, string Help, string? Color = null, bool Power = false);

    public static readonly Def[] VideoDefs =
    {
        new("Cross Dissolve", Fx.Dissolve, "fade", 0, 0, "Fades from one shot to the next"),
        new("Dip to Black", Fx.Dip, null, 0, 0, "Fades to black, then up into the next shot", "#000000"),
        new("Dip to White", Fx.Dip, null, 0, 0, "Fades to white, then down into the next shot", "#FFFFFF"),
        new("Wipe Left", Fx.Wipe, "wipeleft", -1, 0, "A straight edge sweeps from right to left"),
        new("Wipe Right", Fx.Wipe, "wiperight", 1, 0, "A straight edge sweeps from left to right"),
        new("Wipe Up", Fx.Wipe, "wipeup", 0, -1, "A straight edge sweeps from bottom to top"),
        new("Wipe Down", Fx.Wipe, "wipedown", 0, 1, "A straight edge sweeps from top to bottom"),
        new("Clock Wipe", Fx.Mask, null, 0, 0, "A hand sweeps round from twelve o'clock, revealing the next shot"),
        new("Diagonal Wipe", Fx.Mask, null, 1, 1, "A diagonal edge sweeps from the top left to the bottom right"),
        new("Venetian Blinds", Fx.Mask, null, 0, 1, "Horizontal slats open onto the next shot"),
        new("Inset", Fx.Mask, null, 1, 1, "The next shot grows from the top-left corner"),
        new("Push Left", Fx.Push, null, -1, 0, "The next shot pushes the current one off to the left"),
        new("Push Right", Fx.Push, null, 1, 0, "The next shot pushes the current one off to the right"),
        new("Push Up", Fx.Push, null, 0, -1, "The next shot pushes the current one up"),
        new("Push Down", Fx.Push, null, 0, 1, "The next shot pushes the current one down"),
        new("Slide Left", Fx.Move, null, -1, 0, "The next shot slides in from the right, over the current one"),
        new("Slide Right", Fx.Move, null, 1, 0, "The next shot slides in from the left, over the current one"),
        new("Slide Up", Fx.Move, null, 0, -1, "The next shot slides up from the bottom, over the current one"),
        new("Slide Down", Fx.Move, null, 0, 1, "The next shot slides down from the top, over the current one"),
        new("Iris Round", Fx.Iris, "circleopen", 0, 0, "A soft circle opens from the centre"),
        new("Iris Box", Fx.Mask, null, 0, 0, "A rectangle opens from the centre"),
        new("Iris Diamond", Fx.Mask, null, 0, 0, "A diamond opens from the centre"),
        new("Barn Doors", Fx.Mask, null, 1, 0, "Doors open from the middle to the left and right"),
        new("Barn Doors Horizontal", Fx.Mask, null, 0, 1, "Doors open from the middle up and down"),
        new("Cross Zoom", Fx.Move, null, 0, 0, "Zooms into the current shot and out of the next one, with a quick blend"),
        new("Zoom In", Fx.Move, null, 0, 0, "The next shot grows from the centre"),
        new("Zoom Out", Fx.Move, null, 0, 0, "The current shot shrinks into the centre, revealing the next one"),
        new("Spin In", Fx.Move, null, 0, 0, "The next shot spins in from the centre"),
        new("Spin Away", Fx.Move, null, 0, 0, "The current shot spins away into the centre"),
    };
    /// <summary>Effects panel group of a transition.</summary>
    public static string GroupOf(Def d) => d.Fx switch
    {
        Fx.Dissolve or Fx.Dip => "Dissolve",
        Fx.Wipe => "Wipe",
        Fx.Push => "Slide",
        Fx.Mask => d.Name is "Clock Wipe" or "Diagonal Wipe" or "Venetian Blinds" or "Inset" ? "Wipe" : "Iris",
        Fx.Move => d.Name.StartsWith("Slide") ? "Slide" : "Zoom",
        _ => "Iris",
    };
    public static readonly Def[] AudioDefs =
    {
        new("Constant Power", Fx.Audio, null, 0, 0, "Smooth crossfade (equal loudness) — Premiere's default", Power: true),
        new("Constant Gain", Fx.Audio, null, 0, 0, "Linear crossfade (can dip slightly in the middle)"),
    };
    /// <summary>Default transitions (Ctrl+D / Ctrl+Shift+D), set from the Effects panel; saved in the app settings.</summary>
    public static string DefaultVideo => AppSettings.Current.DefaultVideoTransition;
    public static string DefaultAudio => AppSettings.Current.DefaultAudioTransition;
    public static double DefaultDur => Math.Max(0.1, AppSettings.Current.DefaultTransitionDur);
    public static Def Get(string kind) => VideoDefs.Concat(AudioDefs).FirstOrDefault(d => d.Name == kind) ?? VideoDefs[0];
    public static bool IsAudio(Clip c) => c.Kind == ClipKind.Audio;

    /// <summary>A resolved transition: clips, track, window, cut.</summary>
    public sealed record Win(Clip? A, Clip? B, Transition T, Def D, int Track, double T0, double T1, double Cut, Clip Owner, bool Head)
    {
        public double Q(double t) => T1 - T0 < 1e-9 ? 1 : Math.Clamp((t - T0) / (T1 - T0), 0, 1);
        public double Mid => (T0 + T1) / 2;
        public double Dur => T1 - T0;
        /// <summary>When the outgoing clip stops showing / the incoming one starts (dips switch at the middle).</summary>
        public double AEnd => D.Fx == Fx.Dip && B != null ? Mid : T1;
        public double BStart => D.Fx == Fx.Dip && A != null ? Mid : T0;
    }

    /// <summary>All transitions of the timeline, resolved: windows clamped so that a clip's head and tail transitions fit in it.</summary>
    public sealed class Plan
    {
        public readonly List<Win> Wins = new();
        readonly Dictionary<string, Win> head = new(), tail = new();
        public Win? HeadOf(Clip c) => head.GetValueOrDefault(c.Id);
        public Win? TailOf(Clip c) => tail.GetValueOrDefault(c.Id);
        public void Add(Win w)
        {
            Wins.Add(w);
            if (w.B != null) head[w.B.Id] = w;
            if (w.A != null) tail[w.A.Id] = w;
        }
        /// <summary>The time span a clip is on screen (or audible): its own range plus what transitions add.</summary>
        public (double s, double e) Range(Clip c)
        {
            double s = c.Start, e = c.End;
            if (HeadOf(c) is { } h) s = Math.Min(s, h.BStart);
            if (TailOf(c) is { } t) e = Math.Max(e, t.AEnd);
            if (HeadOf(c) is { D.Fx: Fx.Dip, A: not null } hd) s = hd.Mid;     // a dip shows the incoming clip only after the middle
            if (TailOf(c) is { D.Fx: Fx.Dip, B: not null } td) e = td.Mid;
            return (s, e);
        }
        public bool Shows(Clip c, double t) { var (s, e) = Range(c); return t >= s - 1e-7 && t < e - 1e-7; }   // (Start + Dur can land one rounding step past the next clip's start: both would show on that frame)
    }

    const double Eps = 1e-6;

    public static Plan Compute(Editor ed) => Compute(ed.Clips, c => ed.MediaOf(c)?.Duration);
    public static Plan Compute(IEnumerable<Clip> clips, Func<Clip, double?>? mediaDur = null)
    {
        var plan = new Plan();
        foreach (var track in clips.Where(c => c.Kind != ClipKind.Adjustment).GroupBy(c => c.Track))   // (adjustment layers take no transitions)
        {
            var list = track.OrderBy(c => c.Start).ToList();
            var raw = new List<(Clip? A, Clip? B, Transition T, Clip Owner, bool Head)>();
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                Clip? prev = i > 0 && Math.Abs(list[i - 1].End - c.Start) < 0.5 / TimeUtil.Fps ? list[i - 1] : null;
                Clip? next = i + 1 < list.Count && Math.Abs(list[i + 1].Start - c.End) < 0.5 / TimeUtil.Fps ? list[i + 1] : null;
                if (c.TransIn is { } ti && Fits(ti, c)) raw.Add((prev, c, ti, c, true));
                if (c.TransOut is { } to && Fits(to, c) && !(next?.TransIn is { } nt && Fits(nt, next))) raw.Add((c, next, to, c, false));
            }
            // windows, then shrink so each clip's head + tail parts fit inside it (and in the media for audio)
            var wins = raw.Select(r =>
            {
                double cut = r.B?.Start ?? r.A!.End, d = Math.Max(1 / TimeUtil.Fps, r.T.Dur);
                var al = r.A == null ? TransAlign.Start : r.B == null ? TransAlign.End : r.T.Align;
                double t0 = al switch { TransAlign.Start => cut, TransAlign.End => cut - d, _ => cut - d / 2 };
                return (r, cut, inA: cut - t0, inB: t0 + d - cut);
            }).ToList();
            var partA = new Dictionary<string, double>(); var partB = new Dictionary<string, double>();
            foreach (var w in wins) { if (w.r.A != null) partA[w.r.A.Id] = w.inA; if (w.r.B != null) partB[w.r.B.Id] = w.inB; }
            double Scale(Clip? c) => c == null ? 1 : Math.Min(1, c.Dur * 0.999 / Math.Max(Eps, partA.GetValueOrDefault(c.Id) + partB.GetValueOrDefault(c.Id)));
            foreach (var w in wins)
            {
                double k = Math.Min(Scale(w.r.A), Scale(w.r.B));
                double inA = w.inA * k, inB = w.inB * k;
                if (Get(w.r.T.Kind).Fx == Fx.Audio)
                {   // sound has no frozen frames: the extension must exist in the media
                    if (w.r.A != null && w.r.B != null)
                    {
                        double headB = TimeMap.HeadRoom(w.r.B, mediaDur?.Invoke(w.r.B) ?? 1e9), tailA = MediaLeft(w.r.A, mediaDur);
                        double s = Math.Min(1, Math.Min(headB / Math.Max(Eps, inA), tailA / Math.Max(Eps, inB)));
                        inA *= s; inB *= s;
                    }
                }
                if (inA + inB < 0.5 / TimeUtil.Fps) continue;
                var def = Get(w.r.T.Kind);
                double fps = TimeUtil.Fps, t0 = Math.Round((w.cut - inA) * fps) / fps, t1 = t0 + Math.Round((inA + inB) * fps) / fps;   // whole frames (ffmpeg switches on frames)
                if (t1 - t0 < 0.5 / fps) continue;
                plan.Add(new Win(w.r.A, w.r.B, w.r.T, def, track.Key, t0, t1, w.cut, w.r.Owner, w.r.Head));
            }
        }
        return plan;
    }

    static bool Fits(Transition t, Clip c) => (Get(t.Kind).Fx == Fx.Audio) == IsAudio(c) && c.Mask == MaskMode.None;   // track mattes (titles or clips) take none
    /// <summary>Seconds of media after the clip's out point (images: unlimited).</summary>
    static double MediaLeft(Clip c, Func<Clip, double?>? mediaDur) => c.Kind == ClipKind.Image || c.Kind == ClipKind.Text ? 1e9 : TimeMap.TailRoom(c, mediaDur?.Invoke(c) ?? 1e9);

    /// <summary>Clips as they are rendered: each extended over its transitions (same Id; In / Start / Dur shifted together, so
    /// keyframes and masks keep their source times), fades on transition sides removed.</summary>
    public static List<Clip> Extended(IEnumerable<Clip> clips, Plan plan)
    {
        var res = new List<Clip>();
        foreach (var c in clips)
        {
            var (s, e) = plan.Range(c);
            if (Math.Abs(s - c.Start) < Eps && Math.Abs(e - c.End) < Eps) { res.Add(c); continue; }
            var x = System.Text.Json.JsonSerializer.Deserialize<Clip>(System.Text.Json.JsonSerializer.Serialize(c))!;
            x.In = TimeMap.Src(c, s); x.Start = s; x.Dur = e - s;               // (speed / reverse: the frame the map shows there)
            if (plan.HeadOf(c) != null) x.FadeIn = 0;
            if (plan.TailOf(c) != null) x.FadeOut = 0;
            res.Add(x);
        }
        return res;
    }

    // ---------------- what each clip looks like during a transition ----------------
    /// <summary>A clip's look at time t: opacity factor, clip region / opacity mask in FRAME coordinates (logical pixels),
    /// offset (push), and whether it is drawn above the other clip of the transition.</summary>
    public readonly record struct LookInfo(double Opacity, Geometry? Region, Brush? Mask, double Dx, double Dy, bool Top, double S = 1, double Rot = 0, bool Moves = false)
    {
        /// <summary>A clip's placement (left, top, size, rotation about its centre) moved by this look: offset, plus for Move transitions a
        /// scale and turn about the frame's centre — what the preview draws and LayerPlan exports.</summary>
        public (double x, double y, double w, double h, double rot) Apply((double x, double y, double w, double h, double rot) p)
        {
            if (!Moves) return (p.x + Dx, p.y + Dy, p.w, p.h, p.rot);
            double cx = Editor.W / 2.0, cy = Editor.H / 2.0, a = Rot * Math.PI / 180, cs = Math.Cos(a), sn = Math.Sin(a);
            double vx = p.x + p.w / 2 - cx, vy = p.y + p.h / 2 - cy;
            double nx = cx + S * (vx * cs - vy * sn) + Dx, ny = cy + S * (vx * sn + vy * cs) + Dy, w = p.w * S, h = p.h * S;
            return (nx - w / 2, ny - h / 2, w, h, p.rot + Rot);
        }
        /// <summary>A title's motion (scale and turn about its anchor, then placed at X / Y) moved the same way.</summary>
        public TitleVisual.Motion Apply(TitleVisual.Motion m)
        {
            if (!Moves) return m;
            double W = Editor.W, H = Editor.H, cx = W / 2, cy = H / 2, a = Rot * Math.PI / 180, cs = Math.Cos(a), sn = Math.Sin(a);
            double vx = m.X * W / 100, vy = m.Y * H / 100;                    // anchor relative to the centre
            double nx = S * (vx * cs - vy * sn) + Dx, ny = S * (vx * sn + vy * cs) + Dy;
            return m with { Scale = m.Scale * S, Rotation = m.Rotation + Rot, X = nx / W * 100, Y = ny / H * 100 };
        }
    }
    static readonly LookInfo Plain = new(1, null, null, 0, 0, false);

    /// <summary>Move transitions: the clip's offset (logical px), scale and turn about the frame centre, opacity, and whether it is on top.</summary>
    public readonly record struct Xf(double Dx, double Dy, double S, double Rot, double Op, bool Top);
    static double Ease(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
    /// <summary>True for Move transitions that move the outgoing shot (drawn on top of the incoming one).</summary>
    public static bool ATop(Def d) => d.Name is "Zoom Out" or "Spin Away";
    public static Xf XformOf(Win w, bool isB, double q)
    {
        double W = Editor.W, H = Editor.H;
        var d = w.D;
        if (d.Name == "Cross Zoom")
        {
            // the outgoing shot zooms in, the incoming one zooms out from close up and blends in over it in the middle
            if (!isB) return new(0, 0, 1 + 3 * Ease(q / 0.6), 0, w.B == null ? 1 - q : 1, false);
            return new(0, 0, 1 + 3 * (1 - Ease((q - 0.4) / 0.6)), 0, w.A == null ? q : Math.Clamp((q - 0.4) / 0.2, 0, 1), true);
        }
        // one shot moves (on top): the incoming one arrives, or for Zoom Out / Spin Away the outgoing one leaves; with only one shot
        // present (from / to nothing), that one does the moving
        bool moverIsB = ATop(d) ? w.A == null : w.B != null;
        if (isB != moverIsB) return new(0, 0, 1, 0, 1, false);
        if (d.Name.StartsWith("Slide"))
            return isB ? new(-d.Dx * W * (1 - q), -d.Dy * H * (1 - q), 1, 0, 1, true)        // in from the far side
                       : new(d.Dx * W * q, d.Dy * H * q, 1, 0, 1, true);                     // (to nothing: out the way it moves)
        double e = Ease(isB ? q : 1 - q);                                                   // 1 = full size, in place
        return new(0, 0, e, d.Name.StartsWith("Spin") ? (isB ? -360 : 360) * (1 - e) : 0, 1, true);
    }
    /// <summary>The Move-transition transform of a clip at timeline time t (identity outside its windows; pushes give their offset).</summary>
    public static Xf Xform(Plan plan, Clip c, double t)
    {
        var l = Look(plan, c, t, _ => null);
        return new(l.Dx, l.Dy, l.S, l.Rot, l.Moves ? l.Opacity : 1, l.Top);
    }
    /// <summary>True when the clip has a Move transition (exported per frame through LayerPlan / the per-frame title renderer).</summary>
    public static bool Moves(Plan plan, Clip c) => plan.HeadOf(c) is { D.Fx: Fx.Move } || plan.TailOf(c) is { D.Fx: Fx.Move };

    /// <summary>Mask transitions: where the incoming shot shows at progress q, in frame coordinates (logical px). The preview clips the
    /// shots with it; the export renders the same geometry into a mask video (Exporter.TransitionMask).</summary>
    public static Geometry Shape(Def d, double q, double W, double H)
    {
        q = Math.Clamp(q, 0, 1);
        double cx = W / 2, cy = H / 2;
        Geometry g;
        switch (d.Name)
        {
            case "Clock Wipe":
            {
                if (q >= 1) { g = new RectangleGeometry(new Rect(-W, -H, 3 * W, 3 * H)); break; }
                double R = Math.Sqrt(W * W + H * H), a = 360 * q;
                int n = Math.Max(2, (int)Math.Ceiling(a / 2));
                var sg = new StreamGeometry();
                using (var s = sg.Open())
                {
                    s.BeginFigure(new Point(cx, cy), true, true);
                    for (int i = 0; i <= n; i++) { double f = a * i / n * Math.PI / 180; s.LineTo(new Point(cx + R * Math.Sin(f), cy - R * Math.Cos(f)), true, false); }
                }
                g = sg; break;
            }
            case "Diagonal Wipe":
            {
                var sg = new StreamGeometry();
                using (var s = sg.Open()) { s.BeginFigure(new Point(0, 0), true, true); s.LineTo(new Point(2 * q * W, 0), true, false); s.LineTo(new Point(0, 2 * q * H), true, false); }
                g = sg; break;
            }
            case "Venetian Blinds":
            {
                var gg = new GeometryGroup(); const int N = 10; double bh = H / N;
                for (int i = 0; i < N; i++) gg.Children.Add(new RectangleGeometry(new Rect(0, i * bh, W, bh * q)));
                g = gg; break;
            }
            case "Inset": g = new RectangleGeometry(new Rect(0, 0, W * q, H * q)); break;
            case "Iris Box": g = new RectangleGeometry(new Rect(cx - cx * q, cy - cy * q, W * q, H * q)); break;
            case "Iris Diamond":
            {
                double a = q * W, b = q * H;
                var sg = new StreamGeometry();
                using (var s = sg.Open()) { s.BeginFigure(new Point(cx, cy - b), true, true); s.LineTo(new Point(cx + a, cy), true, false); s.LineTo(new Point(cx, cy + b), true, false); s.LineTo(new Point(cx - a, cy), true, false); }
                g = sg; break;
            }
            case "Barn Doors": g = new RectangleGeometry(new Rect(cx - cx * q, 0, W * q, H)); break;
            default: g = new RectangleGeometry(new Rect(0, cy - cy * q, W, H * q)); break;       // Barn Doors Horizontal
        }
        g.Freeze();
        return g;
    }

    /// <summary>covers(a, b): clip b's picture covers clip a's (opaque, unrotated, a's rectangle inside b's) — then a plain
    /// "b over a" blend is exactly the composite dissolve the exporter makes.</summary>
    public static LookInfo Look(Plan plan, Clip c, double t, Func<Clip, Rect?> rectOf)
    {
        var w = plan.TailOf(c) is { } tw && t >= tw.T0 && t < tw.T1 ? tw : plan.HeadOf(c) is { } hw && t >= hw.T0 && t < hw.T1 ? hw : null;
        if (w == null || w.D.Fx is Fx.Audio or Fx.Dip) return Plain;
        bool isB = ReferenceEquals(w.B, c) || w.B?.Id == c.Id;
        double q = w.Q(t);
        double W = Editor.W, H = Editor.H;
        switch (w.D.Fx)
        {
            case Fx.Dissolve:
            {
                var other = isB ? w.A : w.B;
                if (other == null) return new(isB ? q : 1 - q, null, null, 0, 0, isB);
                var ra = rectOf(w.A!); var rb = rectOf(w.B!);
                bool bCoversA = ra is { } a1 && rb is { } b1 && b1.Contains(a1) && Opaque(w.B!);
                bool aCoversB = !bCoversA && ra is { } a2 && rb is { } b2 && a2.Contains(b2) && Opaque(w.A!);
                if (bCoversA) return isB ? new(q, null, null, 0, 0, true) : Plain;
                if (aCoversB) return isB ? Plain : new(1 - q, null, null, 0, 0, true);
                return isB ? new(q, null, null, 0, 0, true) : new(1 - q, null, null, 0, 0, false);
            }
            case Fx.Wipe:
            {
                // ffmpeg xfade: edge at width·progress (progress = 1 − q); the incoming side gets the part the edge has passed
                double ex = W * (1 - q), ey = H * (1 - q);
                Rect bRect = w.D.Dx < 0 ? new Rect(ex, 0, W - ex, H) : w.D.Dx > 0 ? new Rect(0, 0, W * q, H)
                           : w.D.Dy < 0 ? new Rect(0, ey, W, H - ey) : new Rect(0, 0, W, H * q);
                var full = new RectangleGeometry(new Rect(-W, -H, 3 * W, 3 * H));
                Geometry region = isB ? new RectangleGeometry(bRect) : new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(bRect));
                region.Freeze();
                return new(1, region, null, 0, 0, isB);
            }
            case Fx.Push:
            {
                double dx = w.D.Dx * W, dy = w.D.Dy * H;     // direction the pictures move
                return isB ? new(1, null, null, Snap(-dx * (1 - q)), Snap(-dy * (1 - q)), true) : new(1, null, null, Snap(dx * q), Snap(dy * q), false);
            }
            case Fx.Mask:
            {
                // the incoming shot inside the shape, the outgoing one outside it (the export merges the two composites through the same shape);
                // an outgoing shot fully covered by an opaque incoming one stays whole — clipping both doubled the soft edge pixels
                if (!isB && w.A != null && w.B != null && rectOf(w.A) is { } ma && rectOf(w.B) is { } mb && mb.Contains(ma) && Opaque(w.B)) return Plain;
                var shape = Shape(w.D, q, W, H);
                Geometry region = isB ? shape : new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(-W, -H, 3 * W, 3 * H)), shape);
                region.Freeze();
                return new(1, region, null, 0, 0, isB);
            }
            case Fx.Move:
            {
                var x = XformOf(w, isB, q);
                return new(x.Op, null, null, x.Dx, x.Dy, x.Top, x.S, x.Rot, Moves: true);
            }
            case Fx.Iris:
            {
                // ffmpeg circleopen: B weight = 1 − smoothstep(0, 1, dist / halfDiagonal + (0.5 − q)·3)
                double z = Math.Sqrt(W * W + H * H) / 2, p = (0.5 - q) * 3;
                if (!isB) return w.A != null && w.B != null && rectOf(w.A) is { } ra && rectOf(w.B) is { } rb && rb.Contains(ra) && Opaque(w.B) ? Plain
                               : new(1, null, IrisBrush(z, p, invert: true), 0, 0, false);
                return new(1, null, IrisBrush(z, p, invert: false), 0, 0, true);
            }
        }
        return Plain;
    }

    /// <summary>Like the exporter's overlay: rounded output pixels, then down to an even pixel (yuv 4:2:0 chroma).</summary>
    static double Snap(double v) { double s = Editor.OutScale; return Math.Floor(Math.Round(v * s) / 2) * 2 / s; }

    static bool Opaque(Clip c) => Math.Abs(c.Rotation) < 1e-6 && !Anim.IsAnimated(c, AnimProp.Rotation) && c.Opacity >= 99.9 && !Anim.IsAnimated(c, AnimProp.Opacity)
                                  && c.Kind is ClipKind.Video or ClipKind.Image && c.Masks.All(m => m.Effect != MaskFx.CutOut);

    /// <summary>Radial opacity mask in frame coordinates: smoothstep falloff sampled into gradient stops.</summary>
    static Brush IrisBrush(double z, double p, bool invert)
    {
        var g = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = new Point(Editor.W / 2.0, Editor.H / 2.0),
                                          GradientOrigin = new Point(Editor.W / 2.0, Editor.H / 2.0), RadiusX = z, RadiusY = z };
        for (int i = 0; i <= 24; i++)
        {
            double d = i / 24.0, x = Math.Clamp(d + p, 0, 1), s = x * x * (3 - 2 * x), b = 1 - s;
            g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round((invert ? 1 - b : b) * 255), 0, 0, 0), d));
        }
        g.Freeze();
        return g;
    }

    /// <summary>Opacity of a dip's colour layer on a track at t (0 = none), and its colour.</summary>
    public static (double alpha, Color color)? DipAt(Plan plan, int track, double t)
    {
        foreach (var w in plan.Wins)
        {
            if (w.Track != track || w.D.Fx != Fx.Dip || t < w.T0 || t >= w.T1) continue;
            double q = w.Q(t);
            double a = w.A == null ? 1 - q : w.B == null ? q : q < 0.5 ? 2 * q : 2 - 2 * q;
            return (a, (Color)ColorConverter.ConvertFromString(w.D.Color ?? "#000000"));
        }
        return null;
    }

    /// <summary>Audio gain factor of a clip at t from crossfades (1 outside them).</summary>
    public static double AudioGain(Plan plan, Clip c, double t)
    {
        double g = 1;
        if (plan.HeadOf(c) is { D.Fx: Fx.Audio } h && t < h.T1) g *= Curve(h.Q(t), h.D.Power, fadeIn: true);
        if (plan.TailOf(c) is { D.Fx: Fx.Audio } tl && t >= tl.T0) g *= Curve(tl.Q(t), tl.D.Power, fadeIn: false);
        return g;
    }
    /// <summary>Crossfade curve: constant power = sin / cos quarter wave (ffmpeg afade curve=qsin), constant gain = linear.</summary>
    public static double Curve(double q, bool power, bool fadeIn)
    {
        double x = fadeIn ? q : 1 - q;
        return power ? Math.Sin(x * Math.PI / 2) : x;
    }
}
