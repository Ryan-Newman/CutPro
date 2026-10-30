using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

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
        new("Film Dissolve", Fx.Mask, null, 0, 0, "A dithered dissolve, like a cut on film"),
        new("Morph Cut", Fx.Move, null, 0, 0, "The shots ease into each other (a zoom-match dissolve)"),
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
        new("Cube Spin", Fx.Move, null, 1, 0, "The shots turn like two faces of a cube"),
        new("Page Peel", Fx.Mask, null, 1, 1, "The current shot peels away from the bottom right, like a page"),
        new("Bible Page Turn", Fx.Mask, null, 1, 1, "A parchment page with Scripture turns over, revealing the next shot"),
        new("Zoom Punch", Fx.Move, null, 0, 0, "A smash zoom through the cut"),
        new("Shake Cut", Fx.Move, null, 0, 0, "The outgoing shot shakes, then the next one hits"),
        new("Heart Iris", Fx.Mask, null, 0, 0, "A heart opens from the centre"),
        new("Star Iris", Fx.Mask, null, 0, 0, "A star opens from the centre"),
        new("Block Glitch", Fx.Mask, null, 0, 0, "Blocks of the next shot slam in"),
        new("Paint Stroke", Fx.Mask, null, 1, 1, "A diagonal brush stroke reveals the next shot"),
        new("Film Burn", Fx.Dip, null, 0, 0, "A warm film-burn dip between shots", "#E85A20"),
        new("Light Leak", Fx.Dip, null, 0, 0, "A gold light-leak dip between shots", "#FFC078"),
    };
    /// <summary>Effects panel group of a transition.</summary>
    public static string GroupOf(Def d) => d.Fx switch
    {
        Fx.Dissolve or Fx.Dip => "Dissolve",
        Fx.Wipe => "Wipe",
        Fx.Push => "Slide",
        Fx.Mask => d.Name is "Clock Wipe" or "Diagonal Wipe" or "Venetian Blinds" or "Inset" or "Block Glitch" or "Paint Stroke" ? "Wipe"
                 : d.Name is "Page Peel" or "Bible Page Turn" ? "3D" : d.Name == "Film Dissolve" ? "Dissolve"
                 : d.Name is "Heart Iris" or "Star Iris" ? "Iris" : "Iris",
        Fx.Move => d.Name.StartsWith("Slide") ? "Slide" : d.Name == "Morph Cut" ? "Dissolve" : d.Name == "Cube Spin" ? "3D"
                 : d.Name == "Shake Cut" ? "Zoom" : "Zoom",
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
    public readonly record struct LookInfo(double Opacity, Geometry? Region, Brush? Mask, double Dx, double Dy, bool Top, double S = 1, double Rot = 0, bool Moves = false, double Sx = double.NaN, double Sy = double.NaN, int FilmLim = -1)
    {
        public double SX => double.IsNaN(Sx) ? S : Sx;
        public double SY => double.IsNaN(Sy) ? S : Sy;
        /// <summary>A clip's placement (left, top, size, rotation about its centre) moved by this look: offset, plus for Move transitions a
        /// scale and turn about the frame's centre — what the preview draws and LayerPlan exports.</summary>
        public (double x, double y, double w, double h, double rot) Apply((double x, double y, double w, double h, double rot) p)
        {
            if (!Moves) return (p.x + Dx, p.y + Dy, p.w, p.h, p.rot);
            double sx = SX, sy = SY, cx = Editor.W / 2.0, cy = Editor.H / 2.0, a = Rot * Math.PI / 180, cs = Math.Cos(a), sn = Math.Sin(a);
            double vx = p.x + p.w / 2 - cx, vy = p.y + p.h / 2 - cy;
            double nx = cx + sx * (vx * cs - vy * sn) + Dx, ny = cy + sy * (vx * sn + vy * cs) + Dy, w = p.w * sx, h = p.h * sy;
            return (nx - w / 2, ny - h / 2, w, h, p.rot + Rot);
        }
        /// <summary>A title's motion (scale and turn about its anchor, then placed at X / Y) moved the same way.</summary>
        public TitleVisual.Motion Apply(TitleVisual.Motion m)
        {
            if (!Moves) return m;
            double sx = SX, sy = SY, W = Editor.W, H = Editor.H, a = Rot * Math.PI / 180, cs = Math.Cos(a), sn = Math.Sin(a);
            double vx = m.X * W / 100, vy = m.Y * H / 100;                    // anchor relative to the centre
            double nx = sx * (vx * cs - vy * sn) + Dx, ny = sy * (vx * sn + vy * cs) + Dy;
            return m with { Scale = m.Scale * (sx + sy) / 2, Rotation = m.Rotation + Rot, X = nx / W * 100, Y = ny / H * 100 };
        }
    }
    static readonly LookInfo Plain = new(1, null, null, 0, 0, false);

    /// <summary>Move transitions: the clip's offset (logical px), scale and turn about the frame centre, opacity, and whether it is on top.</summary>
    public readonly record struct Xf(double Dx, double Dy, double S, double Rot, double Op, bool Top, double Sx = double.NaN, double Sy = double.NaN)
    {
        public double SX => double.IsNaN(Sx) ? S : Sx;
        public double SY => double.IsNaN(Sy) ? S : Sy;
    }
    static double Ease(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
    /// <summary>True for Move transitions that move the outgoing shot (drawn on top of the incoming one).</summary>
    public static bool ATop(Def d) => d.Name is "Zoom Out" or "Spin Away";
    public static Xf XformOf(Win w, bool isB, double q)
    {
        double W = Editor.W, H = Editor.H;
        var d = w.D;
        if (d.Name == "Cross Zoom" || d.Name == "Zoom Punch")
        {
            double punch = d.Name == "Zoom Punch" ? 5 : 3, blend = d.Name == "Zoom Punch" ? 0.28 : 0.4;
            if (!isB) return new(0, 0, 1 + punch * Ease(q / 0.6), 0, w.B == null ? 1 - q : 1, false);
            return new(0, 0, 1 + punch * (1 - Ease((q - blend) / (1 - blend))), 0, w.A == null ? q : Math.Clamp((q - blend) / 0.18, 0, 1), true);
        }
        if (d.Name == "Shake Cut")
        {
            double shake = (1 - q) * 22 * Math.Sin(q * 51);
            if (!isB) return new(shake, shake * 0.35, 1, 0, 1, false);
            return new(0, 0, 1, 0, w.A == null ? q : Ease(q), true);
        }
        if (d.Name == "Morph Cut")
        {
            // both shots dissolve while easing a little toward a matched scale (optical-flow morph needs a model)
            if (!isB) return new(0, 0, 1 + 0.12 * Ease(q), 0, w.B == null ? 1 - q : 1 - q, false);
            return new(0, 0, 1 + 0.12 * (1 - Ease(q)), 0, w.A == null ? q : q, true);
        }
        if (d.Name == "Cube Spin")
        {
            double th = q * Math.PI / 2, c = Math.Max(0.001, Math.Cos(th)), s = Math.Max(0.001, Math.Sin(th));
            if (!isB) return new(W / 2 * (c - 1), 0, c, 0, w.B == null ? 1 - q : 1, false, c, 1);
            return new(W / 2 * (1 - s), 0, s, 0, w.A == null ? q : 1, true, s, 1);
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
        return new(l.Dx, l.Dy, l.S, l.Rot, l.Moves ? l.Opacity : 1, l.Top, l.SX, l.SY);
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
            case "Heart Iris":
            {
                double s = q * Math.Min(W, H) * 0.055;
                var sg = new StreamGeometry();
                using (var sc = sg.Open())
                {
                    bool first = true;
                    for (int i = 0; i <= 48; i++)
                    {
                        double a = i / 48.0 * Math.PI * 2;
                        double x = cx + s * 16 * Math.Pow(Math.Sin(a), 3);
                        double y = cy - s * (13 * Math.Cos(a) - 5 * Math.Cos(2 * a) - 2 * Math.Cos(3 * a) - Math.Cos(4 * a));
                        if (first) { sc.BeginFigure(new Point(x, y), true, true); first = false; }
                        else sc.LineTo(new Point(x, y), true, false);
                    }
                }
                g = sg; break;
            }
            case "Star Iris":
            {
                double r = q * Math.Sqrt(W * W + H * H) * 0.55, inner = r * 0.42;
                var sg = new StreamGeometry();
                using (var sc = sg.Open())
                {
                    for (int i = 0; i < 10; i++)
                    {
                        double a = -Math.PI / 2 + i * Math.PI / 5, rr = (i & 1) == 0 ? r : inner;
                        var p = new Point(cx + rr * Math.Cos(a), cy + rr * Math.Sin(a));
                        if (i == 0) sc.BeginFigure(p, true, true); else sc.LineTo(p, true, false);
                    }
                }
                g = sg; break;
            }
            case "Block Glitch":
            {
                var gg = new GeometryGroup(); const int cols = 8, rows = 5;
                double cw = W / cols, ch = H / rows;
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < cols; x++)
                    {
                        double h = ((x * 17 + y * 31) % 10) / 10.0;
                        if (q >= h * 0.85) gg.Children.Add(new RectangleGeometry(new Rect(x * cw, y * ch, cw + 0.5, ch + 0.5)));
                    }
                g = gg; break;
            }
            case "Paint Stroke":
            {
                double t = q * 1.4;
                var sg = new StreamGeometry();
                using (var s = sg.Open())
                {
                    s.BeginFigure(new Point(-W * 0.1, -H * 0.2), true, true);
                    s.LineTo(new Point(W * t, -H * 0.15), true, false);
                    s.LineTo(new Point(W * t + W * 0.18, H * 1.15), true, false);
                    s.LineTo(new Point(-W * 0.05, H * 1.1), true, false);
                }
                g = sg; break;
            }
            case "Film Dissolve":
                // preview / export use WriteFilmMask bytes (a 1 px Clip geometry antialiases into a dissolve on RenderTargetBitmap)
                g = q >= 1 ? new RectangleGeometry(new Rect(-W, -H, 3 * W, 3 * H)) : Geometry.Empty;
                break;
            case "Page Peel":
            case "Bible Page Turn":
            {
                // incoming shot revealed from the bottom-right: x/W + y/H ≥ 2 − 2q (a page turning toward the top left)
                double t = 2 - 2 * q;
                var sg = new StreamGeometry();
                using (var s = sg.Open())
                {
                    if (q <= 0) { s.BeginFigure(new Point(W, H), true, true); s.LineTo(new Point(W, H), true, false); }
                    else if (q < 0.5)
                    {
                        s.BeginFigure(new Point(W, H), true, true);
                        s.LineTo(new Point((t - 1) * W, H), true, false);
                        s.LineTo(new Point(W, (t - 1) * H), true, false);
                    }
                    else if (q < 1)
                    {
                        s.BeginFigure(new Point(W, H), true, true);
                        s.LineTo(new Point(W, 0), true, false);
                        s.LineTo(new Point(t * W, 0), true, false);
                        s.LineTo(new Point(0, t * H), true, false);
                        s.LineTo(new Point(0, H), true, false);
                    }
                    else { s.BeginFigure(new Point(0, 0), true, true); s.LineTo(new Point(W, 0), true, false); s.LineTo(new Point(W, H), true, false); s.LineTo(new Point(0, H), true, false); }
                }
                g = sg; break;
            }
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
                if (w.D.Name == "Film Dissolve")
                    return new(1, null, null, 0, 0, isB, FilmLim: (int)Math.Round(Math.Clamp(q, 0, 1) * 64));
                var shape = Shape(w.D, q, W, H);
                Geometry region = isB ? shape : new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(-W, -H, 3 * W, 3 * H)), shape);
                region.Freeze();
                return new(1, region, null, 0, 0, isB);
            }
            case Fx.Move:
            {
                var x = XformOf(w, isB, q);
                return new(x.Op, null, null, x.Dx, x.Dy, x.Top, x.S, x.Rot, Moves: true, Sx: x.SX, Sy: x.SY);
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

    /// <summary>8×8 Bayer threshold (0…63). Incoming shows where the cell is below progress. Cells are <see cref="FilmCell"/> px so H.264
    /// keeps the pattern (1 px Bayer was ~18 dB vs the export).</summary>
    public const int FilmCell = 4;
    internal static readonly byte[] Bayer8 =
    {
        0, 32, 8, 40, 2, 34, 10, 42, 48, 16, 56, 24, 50, 18, 58, 26,
        12, 44, 4, 36, 14, 46, 6, 38, 60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41, 51, 19, 59, 27, 49, 17, 57, 25,
        15, 47, 7, 39, 13, 45, 5, 37, 63, 31, 55, 23, 61, 29, 53, 21
    };
    public static void WriteFilmMask(byte[] gray, int W, int H, double q)
    {
        int lim = (int)Math.Round(Math.Clamp(q, 0, 1) * 64);
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++) gray[y * W + x] = Bayer8[((y / FilmCell) & 7) * 8 + ((x / FilmCell) & 7)] < lim ? (byte)255 : (byte)0;
        });
    }

    const string FilmHlsl = @"
sampler2D input : register(s0);
float4 c0 : register(c0);
float4 c1 : register(c1);
float Bayer(float x, float y)
{
    float x0 = x - 2 * floor(x / 2), y0 = y - 2 * floor(y / 2);
    float x1 = floor(x / 2); x1 = x1 - 2 * floor(x1 / 2);
    float y1 = floor(y / 2); y1 = y1 - 2 * floor(y1 / 2);
    float x2 = floor(x / 4); x2 = x2 - 2 * floor(x2 / 2);
    float y2 = floor(y / 4); y2 = y2 - 2 * floor(y2 / 2);
    return 16 * (2 * x0 + 3 * y0 - 4 * x0 * y0)
         +  4 * (2 * x1 + 3 * y1 - 4 * x1 * y1)
         +      (2 * x2 + 3 * y2 - 4 * x2 * y2);
}
float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 c = tex2D(input, uv);
    float fx = c1.x + uv.x * c1.z, fy = c1.y + uv.y * c1.w;
    float on = Bayer(floor(fx), floor(fy)) < c0.x ? 1 : 0;
    if (c0.y > 0.5) on = 1 - on;
    return c * on;
}";
    static PixelShader? filmPs; static bool filmTried; public static string? FilmShaderError;
    /// <summary>ps_2_0 dither for Film Dissolve. ImageBrush / Clip antialiased the Bayer into a dissolve on RenderTargetBitmap;
    /// this is the same 8×8 threshold as <see cref="WriteFilmMask"/>, nearest-neighbour like the blend shaders.</summary>
    public static PixelShader? FilmShader()
    {
        if (filmTried) return filmPs;
        filmTried = true;
        try
        {
            var code = Blend.Compile(FilmHlsl, "ps_2_0");
            var ps = new PixelShader(); ps.SetStreamSource(new MemoryStream(code)); ps.Freeze();
            filmPs = ps;
        }
        catch (Exception ex) { FilmShaderError = ex.Message; }
        return filmPs;
    }
    /// <summary>Null if the preview shader's 8×8 Bayer on a white square matches <see cref="WriteFilmMask"/>; otherwise the mismatch.</summary>
    public static string? FilmShaderProbe()
    {
        if (FilmShader() is not { } sh) return FilmShaderError ?? "shader missing";
        const int S = 64, lim = 32;
        var r = new System.Windows.Shapes.Rectangle { Width = S, Height = S, Fill = Brushes.White };
        var e = new FilmEffect(sh); e.Set(lim, false, 0, 0, S, S); r.Effect = e;
        var cv = new System.Windows.Controls.Canvas { Width = S, Height = S, Background = Brushes.Black };
        cv.Children.Add(r);
        cv.Measure(new Size(S, S)); cv.Arrange(new Rect(0, 0, S, S)); cv.UpdateLayout();
        var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32); rtb.Render(cv);
        var px = new byte[S * S * 4]; rtb.CopyPixels(px, S * 4, 0);
        var gray = new byte[S * S]; WriteFilmMask(gray, S, S, lim / 64.0);
        int match = 0, white = 0, want = 0;
        for (int i = 0; i < gray.Length; i++)
        {
            bool on = px[i * 4 + 1] > 127; if (on) white++;
            if (gray[i] > 127) want++;
            if (on == gray[i] > 127) match++;
        }
        return match == gray.Length ? null : $"{match}/{gray.Length} pixels, {white} white (mask {want})";
    }
    /// <summary>Null if a blue square with the film shader over a red square matches WriteFilmMask (incoming = blue).</summary>
    public static string? FilmCompositeProbe()
    {
        if (FilmShader() is not { } sh) return FilmShaderError ?? "shader missing";
        const int S = 64, lim = 32;
        var under = new System.Windows.Shapes.Rectangle { Width = S, Height = S, Fill = Brushes.Red };
        var over = new System.Windows.Shapes.Rectangle { Width = S, Height = S, Fill = Brushes.Blue };
        var e = new FilmEffect(sh); e.Set(lim, false, 0, 0, S, S); over.Effect = e;
        var cv = new System.Windows.Controls.Canvas { Width = S, Height = S };
        cv.Children.Add(under); cv.Children.Add(over);
        cv.Measure(new Size(S, S)); cv.Arrange(new Rect(0, 0, S, S)); cv.UpdateLayout();
        var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32); rtb.Render(cv);
        var px = new byte[S * S * 4]; rtb.CopyPixels(px, S * 4, 0);
        var gray = new byte[S * S]; WriteFilmMask(gray, S, S, lim / 64.0);
        int match = 0;
        for (int i = 0; i < gray.Length; i++)
        {
            bool incoming = gray[i] > 127;
            byte wantB = incoming ? (byte)255 : (byte)0, wantR = incoming ? (byte)0 : (byte)255;
            if (px[i * 4] == wantB && px[i * 4 + 2] == wantR) match++;
        }
        return match == gray.Length ? null : $"{match}/{gray.Length} pixels match red/blue film merge";
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
    /// <summary>Bible Page Turn on this track at t — the parchment overlay covers the outgoing shot, clipped to the unpeeled side.</summary>
    public static Win? BiblePageAt(Plan plan, int track, double t)
    {
        foreach (var w in plan.Wins)
            if (w.Track == track && w.D.Name == "Bible Page Turn" && t >= w.T0 && t < w.T1) return w;
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

/// <summary>Nearest-neighbour 8×8 Bayer threshold on a clip layer (Film Dissolve). OpacityMask / Clip bilinear-filter the same bits
/// into a dissolve on the software renderer; this matches <see cref="Transitions.WriteFilmMask"/> pixel for pixel.</summary>
public sealed class FilmEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(FilmEffect), 0, SamplingMode.NearestNeighbor);
    public static readonly DependencyProperty C0Property = DependencyProperty.Register("C0", typeof(Point4D), typeof(FilmEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty C1Property = DependencyProperty.Register("C1", typeof(Point4D), typeof(FilmEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(1)));
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Point4D C0 { get => (Point4D)GetValue(C0Property); set => SetValue(C0Property, value); }
    public Point4D C1 { get => (Point4D)GetValue(C1Property); set => SetValue(C1Property, value); }
    public FilmEffect(PixelShader shader)
    {
        PixelShader = shader;
        UpdateShaderValue(InputProperty); UpdateShaderValue(C0Property); UpdateShaderValue(C1Property);
    }
    public void Set(int lim, bool invert, double left, double top, double w, double h)
    {
        C0 = new Point4D(lim, invert ? 1 : 0, Editor.W, Editor.H);
        C1 = new Point4D(left, top, w, h);
    }
}

/// <summary>A named transition (kind, duration, alignment) shown in the Effects panel — built-ins plus the user's, like effect presets.</summary>
public sealed class TransPreset
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Cross Dissolve";
    public double Dur { get; set; } = 1;
    public TransAlign Align { get; set; } = TransAlign.Center;
    public bool BuiltIn { get; set; }
}

public static class TransPresets
{
    public static string? FileOverride;
    static string File_ => FileOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CutPro", "trans-presets.json");
    static List<TransPreset>? user;

    public static readonly TransPreset[] BuiltIns =
    {
        new() { Name = "Quick dissolve", Kind = "Cross Dissolve", Dur = 0.5, BuiltIn = true },
        new() { Name = "Film 12 frames", Kind = "Film Dissolve", Dur = 0.4, BuiltIn = true },
        new() { Name = "Morph", Kind = "Morph Cut", Dur = 1.2, BuiltIn = true },
        new() { Name = "Cube", Kind = "Cube Spin", Dur = 1, BuiltIn = true },
        new() { Name = "Page peel", Kind = "Page Peel", Dur = 1, BuiltIn = true },
        new() { Name = "Bible page", Kind = "Bible Page Turn", Dur = 1, BuiltIn = true },
        new() { Name = "Dip 2 s", Kind = "Dip to Black", Dur = 2, BuiltIn = true },
    };
    public static List<TransPreset> User
    {
        get
        {
            if (user != null) return user;
            try { user = File.Exists(File_) ? System.Text.Json.JsonSerializer.Deserialize<List<TransPreset>>(File.ReadAllText(File_)) ?? new() : new(); }
            catch { user = new(); }
            return user;
        }
    }
    public static IEnumerable<TransPreset> All => User.Concat(BuiltIns);
    public static void Save()
    {
        if (AppSettings.ReadOnly && FileOverride == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, System.Text.Json.JsonSerializer.Serialize(User, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    public static void Add(TransPreset p) { User.RemoveAll(x => x.Name.Equals(p.Name, StringComparison.CurrentCultureIgnoreCase)); User.Insert(0, p); Save(); }
    public static void Remove(TransPreset p) { User.Remove(p); Save(); }
    internal static void Reload() => user = null;
}
