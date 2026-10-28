namespace CutPro;

/// <summary>The timeline's tools (Premiere's): Selection (V), Ripple Edit (B), Rolling Edit (N), Rate Stretch (R), Razor (C), Slip (Y), Slide (U).</summary>
public enum TrimTool { Select, Ripple, Roll, Rate, Razor, Slip, Slide }

/// <summary>One trim-tool drag: the clips it changes as they were when it began. <see cref="Editor.ApplyTrim"/> applies the whole offset
/// again from that state on every mouse move, so a refused move leaves the last good one.</summary>
public sealed class TrimDrag
{
    public TrimTool Tool;
    public bool Left;                                                          // (ripple / rate: the clip's start edge; roll: the edit is at its start)
    public Clip Clip = null!;                                                  // (the clip the drag began on)
    internal sealed record O(Clip C, double Start, double In, double Dur, double Speed);
    internal readonly List<O> Main = new();                                    // (the clip and its linked partners at the same edit)
    internal readonly List<(O? L, O? R, int Track)> Pairs = new();             // (roll: the clips each side of the edit; slide: the neighbours)
    internal readonly List<O> After = new();                                   // (ripple: the clips after the edit on its tracks)
    internal readonly Dictionary<Clip, (double lo, double hi)> Span = new();   // (rate: each clip's source range as it was)
    internal HashSet<string> Ids = new();
    internal IEnumerable<O> All => Main.Concat(Pairs.SelectMany(p => new[] { p.L, p.R }).OfType<O>()).Concat(After);
    internal void Restore() { foreach (var o in All) { o.C.Start = o.Start; o.C.In = o.In; o.C.Dur = o.Dur; o.C.Speed = o.Speed; } }
    /// <summary>The edit's time when the drag began (the edge or the edit point; slip / slide: the clip's start).</summary>
    public double At;
    /// <summary>The clips whose frames the monitor shows while dragging: the outgoing frame (left) and the incoming one (right).</summary>
    public (Clip? a, Clip? b) Shown;
}

public partial class Editor
{
    /// <summary>True when the clip's media covers this start / in / duration (speed, reverse and time remapping through TimeMap).</summary>
    public bool SourceFits(Clip c, double start, double inPt, double dur)
    {
        if (dur < OneFrame - 1e-6 || start < -1e-6) return false;
        if (c.Kind is not (ClipKind.Video or ClipKind.Audio) || c.Hold) return true;
        double mediaDur = MediaOf(c)?.Duration ?? double.MaxValue;
        if (!TimeMap.IsRemapped(c)) return inPt >= -1e-6 && inPt + dur <= mediaDur + 1e-3;
        var (lo, hi) = TimeMap.SpanAt(c, start, inPt, dur);
        return lo >= -1e-3 && hi <= mediaDur + 1e-3;
    }

    static TrimDrag.O Keep(Clip c) => new(c, c.Start, c.In, c.Dur, c.Speed);
    IEnumerable<Clip> TrimMembers(Clip c, bool alone, Func<Clip, bool> atEdit) =>
        (alone ? new[] { c } : Linked(c)).Where(x => x == c || atEdit(x)).Where(Editable);

    /// <summary>Starts a trim-tool drag on clip c. Ripple / Rate: <paramref name="left"/> = its start edge. Roll: the edit at its start
    /// (left) or end. Slip / Slide: the whole clip. Alone = without its linked partners (Alt). Null when there is nothing to trim.</summary>
    public TrimDrag? BeginTrim(TrimTool tool, Clip c, bool left, bool alone = false)
    {
        if (!Editable(c)) return null;
        var s = new TrimDrag { Tool = tool, Left = left, Clip = c };
        switch (tool)
        {
            case TrimTool.Ripple:
            case TrimTool.Rate:
            {
                double e = left ? c.Start : c.End;
                foreach (var x in TrimMembers(c, alone, x => Math.Abs((left ? x.Start : x.End) - e) < Eps)) s.Main.Add(Keep(x));
                s.At = e;
                if (tool == TrimTool.Ripple)
                {
                    var tracks = s.Main.Select(o => o.C.Track).ToHashSet();
                    var ids0 = s.Main.Select(o => o.C.Id).ToHashSet();
                    double from = c.End;
                    foreach (var x in Clips.Where(x => tracks.Contains(x.Track) && !ids0.Contains(x.Id) && x.Start >= from - Eps && Editable(x))) s.After.Add(Keep(x));
                }
                else foreach (var o in s.Main) s.Span[o.C] = TimeMap.Span(o.C);
                s.Shown = left ? (null, c) : (c, null);
                break;
            }
            case TrimTool.Roll:
            {
                double e = left ? c.Start : c.End;
                s.At = e;
                var tracks = TrimMembers(c, alone, x => Math.Abs(x.Start - e) < Eps || Math.Abs(x.End - e) < Eps).Select(x => x.Track).Distinct();
                foreach (int tr in tracks)
                {
                    var l = Clips.FirstOrDefault(x => x.Track == tr && Math.Abs(x.End - e) < Eps && Editable(x));
                    var r = Clips.FirstOrDefault(x => x.Track == tr && Math.Abs(x.Start - e) < Eps && Editable(x));
                    if (l != null || r != null) s.Pairs.Add((l == null ? null : Keep(l), r == null ? null : Keep(r), tr));
                }
                if (s.Pairs.Count == 0) return null;
                var main = s.Pairs.FirstOrDefault(p => p.Track == c.Track);
                s.Shown = (main.L?.C, main.R?.C);
                break;
            }
            case TrimTool.Slip:
                foreach (var x in alone ? new[] { c } : Linked(c).Where(Editable)) s.Main.Add(Keep(x));
                s.At = c.Start; s.Shown = (c, c);
                break;
            case TrimTool.Slide:
            {
                foreach (var x in alone ? new[] { c } : Linked(c).Where(Editable)) s.Main.Add(Keep(x));
                var ids0 = s.Main.Select(o => o.C.Id).ToHashSet();
                foreach (var o in s.Main)
                {
                    var l = Clips.FirstOrDefault(x => x.Track == o.C.Track && !ids0.Contains(x.Id) && Math.Abs(x.End - o.Start) < Eps && Editable(x));
                    var r = Clips.FirstOrDefault(x => x.Track == o.C.Track && !ids0.Contains(x.Id) && Math.Abs(x.Start - (o.Start + o.Dur)) < Eps && Editable(x));
                    s.Pairs.Add((l == null ? null : Keep(l), r == null ? null : Keep(r), o.C.Track));
                }
                s.At = c.Start;
                var main = s.Pairs.FirstOrDefault(p => p.Track == c.Track);
                s.Shown = (main.L?.C, main.R?.C);
                break;
            }
            default: return null;
        }
        if (s.Main.Count == 0 && s.Pairs.Count == 0) return null;
        s.Ids = s.All.Select(o => o.C.Id).ToHashSet();
        return s;
    }

    /// <summary>Applies a trim-tool drag by d seconds (from its starting state). False (and the starting state) when the media, a minimum
    /// length of one frame, a neighbour or the speed limits refuse it.</summary>
    public bool ApplyTrim(TrimDrag s, double d)
    {
        s.Restore();
        if (Math.Abs(d) < 1e-9) return true;
        bool ok = s.Tool switch
        {
            TrimTool.Ripple => Ripple(s, d),
            TrimTool.Roll => Roll(s, d),
            TrimTool.Rate => Rate(s, d),
            TrimTool.Slip => Slip(s, d),
            TrimTool.Slide => Slide(s, d),
            _ => false,
        };
        if (!ok) s.Restore();
        return ok;
    }

    /// <summary>The largest offset toward d that works (whole frames, searched by halves: what works is a range around 0). Applies it.</summary>
    public double ApplyTrimClamped(TrimDrag s, double d)
    {
        if (ApplyTrim(s, d)) return d;
        long n = (long)Math.Round(d * TimeUtil.Fps), lo = 0, hi = n;                // lo works, hi doesn't
        while (Math.Abs(hi - lo) > 1)
        {
            long mid = lo + (hi - lo) / 2;
            if (ApplyTrim(s, mid / TimeUtil.Fps)) lo = mid; else hi = mid;
        }
        double best = lo / TimeUtil.Fps;
        ApplyTrim(s, best);
        return best;
    }

    // Ripple Edit: the edge moves, the clip's other end stays where it is, everything after it on its tracks moves by the change.
    bool Ripple(TrimDrag s, double d)
    {
        double change = s.Left ? -d : d;
        foreach (var o in s.Main)
        {
            var c = o.C;
            double inPt = s.Left ? TimeMap.SrcAt(c, o.Start, o.In, o.Start + d) : o.In;
            if (!SourceFits(c, o.Start, inPt, o.Dur + change)) return false;
            c.In = Math.Max(0, inPt); c.Dur = o.Dur + change;
        }
        foreach (var o in s.After)
        {
            if (o.Start + change < -1e-6) return false;
            o.C.Start = o.Start + change;
        }
        return true;
    }

    // Rolling Edit: the edit point moves; the clip before it gets longer by what the one after it loses (the sequence keeps its length).
    bool Roll(TrimDrag s, double d)
    {
        foreach (var (l, r, tr) in s.Pairs)
        {
            if (l != null)
            {
                if (!SourceFits(l.C, l.Start, l.In, l.Dur + d)) return false;
                l.C.Dur = l.Dur + d;
                if (r == null && Collides(tr, l.Start, l.Dur + d, s.Ids)) return false;
            }
            if (r != null)
            {
                double start = r.Start + d, inPt = TimeMap.SrcAt(r.C, r.Start, r.In, start);
                if (!SourceFits(r.C, start, inPt, r.Dur - d)) return false;
                r.C.Start = start; r.C.In = Math.Max(0, inPt); r.C.Dur = r.Dur - d;
                if (l == null && Collides(tr, start, r.Dur - d, s.Ids)) return false;
            }
        }
        return true;
    }

    // Rate Stretch: the edge moves and the clip's speed changes so the same source range fills the new length.
    bool Rate(TrimDrag s, double d)
    {
        foreach (var o in s.Main)
        {
            var c = o.C;
            double dur = o.Dur + (s.Left ? -d : d), start = s.Left ? o.Start + d : o.Start;
            if (dur < OneFrame - 1e-6 || start < -1e-6) return false;
            bool timed = c.Kind is ClipKind.Video or ClipKind.Audio && !c.Hold;
            if (timed && !TimeMap.Ramped(c))
            {
                double speed = Math.Round(o.Speed * o.Dur / dur, 4);
                if (speed < TimeMap.MinSpeed || speed > TimeMap.MaxSpeed) return false;
                var (lo, hi) = s.Span[c];
                c.Speed = speed;
                c.In = c.Reverse ? hi - TimeMap.Ratio(c) / TimeUtil.Fps : lo;
            }
            else if (timed) c.In = s.Left ? Math.Max(0, TimeMap.SrcAt(c, o.Start, o.In, start)) : o.In;   // (time remapping: a plain trim)
            c.Start = start; c.Dur = dur;
            if (!SourceFits(c, c.Start, c.In, c.Dur)) return false;
            if (Collides(c.Track, c.Start, c.Dur, s.Ids)) return false;
        }
        return true;
    }

    // Slip: the clip stays where it is; its content moves under it (dragging right shows earlier frames).
    bool Slip(TrimDrag s, double d)
    {
        foreach (var o in s.Main)
        {
            var c = o.C;
            if (c.Kind is not (ClipKind.Video or ClipKind.Audio) || c.Hold) continue;
            double inPt = TimeMap.SrcAt(c, o.Start, o.In, o.Start - d);
            if (!SourceFits(c, o.Start, inPt, o.Dur)) return false;
            c.In = Math.Max(0, inPt);
        }
        return true;
    }

    // Slide: the clip keeps its content and moves; the clip before it ends later (or earlier) and the one after it starts later (or earlier).
    bool Slide(TrimDrag s, double d)
    {
        for (int i = 0; i < s.Main.Count; i++)
        {
            var o = s.Main[i]; var (l, r, tr) = s.Pairs[i];
            double start = o.Start + d;
            if (start < -1e-6) return false;
            o.C.Start = start;
            if (l != null)
            {
                if (!SourceFits(l.C, l.Start, l.In, l.Dur + d)) return false;
                l.C.Dur = l.Dur + d;
            }
            if (r != null)
            {
                double rs = r.Start + d, inPt = TimeMap.SrcAt(r.C, r.Start, r.In, rs);
                if (!SourceFits(r.C, rs, inPt, r.Dur - d)) return false;
                r.C.Start = rs; r.C.In = Math.Max(0, inPt); r.C.Dur = r.Dur - d;
            }
            if (Collides(tr, start, o.Dur, s.Ids)) return false;
        }
        return true;
    }

    /// <summary>One trim-tool edit as a single undo step (tests, keyboard trims): begin, apply (clamped to what works), done.
    /// Returns the offset applied.</summary>
    public double TrimEdit(TrimTool tool, Clip c, bool left, double d, bool alone = false)
    {
        double got = 0;
        Edit(() => { if (BeginTrim(tool, c, left, alone) is { } s) got = ApplyTrimClamped(s, d); }, tool switch
        {
            TrimTool.Ripple => "Ripple Trim", TrimTool.Roll => "Rolling Edit", TrimTool.Rate => "Rate Stretch",
            TrimTool.Slip => "Slip", TrimTool.Slide => "Slide", _ => "Trim",
        });
        return got;
    }
}
