using System.Text.Json;

namespace CutPro;

/// <summary>What Paste Attributes / Remove Attributes take over (Premiere's dialog: motion, opacity, time remapping, volume, the effects).</summary>
[Flags]
public enum ClipAttr
{
    None = 0, Motion = 1, Opacity = 2, Speed = 4, Volume = 8, Color = 16, Looks = 32, Crop = 64, Key = 128, Pin = 256, Lens = 512, Shadow = 1024,
    Blur = 2048, Stabilize = 4096, AudioFx = 8192,
}

/// <summary>Split-screen and picture-in-picture layouts for the selected clips (Motion + Crop values).</summary>
public enum LayoutPreset { Full, PipTopLeft, PipTopRight, PipBottomLeft, PipBottomRight, SideBySide, TopBottom, Grid2x2, ThreeAcross }

/// <summary>Clip groups, the clip clipboard (copy / cut / paste / paste insert), Paste / Remove Attributes, layout presets and gaps (partial Editor).</summary>
public partial class Editor
{
    static T? Copy<T>(T? v) => v == null ? default : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v));

    // ---- groups ----
    /// <summary>What selecting a clip selects: its linked clips and its group (with their links).</summary>
    public IEnumerable<Clip> Members(Clip c)
    {
        var set = new HashSet<Clip>(Linked(c));
        if (c.GroupId != null) foreach (var g in Clips.Where(x => x.GroupId == c.GroupId)) foreach (var l in Linked(g)) set.Add(l);
        return set;
    }
    /// <summary>Ctrl+G: the selected clips become one group (selected and moved together; each keeps its own edits).</summary>
    public bool GroupSelection()
    {
        var s = Clips.Where(IsSel).ToList();
        if (s.Count < 2) return false;
        var id = NewId();
        Edit(() => { foreach (var c in s) c.GroupId = id; });
        return true;
    }
    /// <summary>Ctrl+Shift+G: the selected clips leave their groups.</summary>
    public bool UngroupSelection()
    {
        var s = Clips.Where(c => IsSel(c) && c.GroupId != null).ToList();
        if (s.Count == 0) return false;
        Edit(() => { foreach (var c in s) c.GroupId = null; });
        return true;
    }

    // ---- clipboard (shared by every sequence of the project: copy in one, paste in another) ----
    static string? clipboard;
    static Dictionary<string, MaskTrack.State>? clipMasks;
    public static bool CanPaste => clipboard != null;
    /// <summary>The copied clips (copies; null = nothing copied).</summary>
    public static List<Clip>? Copied => clipboard == null ? null : JsonSerializer.Deserialize<List<Clip>>(clipboard);

    /// <summary>Ctrl+C / Ctrl+X: the selected clips onto the clipboard (with their object-mask outlines); cut deletes them (leaving gaps).</summary>
    public int CopySelection(bool cut = false)
    {
        var sel = Clips.Where(IsSel).OrderBy(c => c.Start).ToList();
        if (sel.Count == 0) return 0;
        clipboard = JsonSerializer.Serialize(sel);
        clipMasks = sel.SelectMany(c => c.Masks).Where(m => MaskTracks.ContainsKey(m.Id)).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => MaskTracks[g.Key].Save());
        if (cut) DeleteSelection(false);
        return sel.Count;
    }

    /// <summary>Ctrl+V (overwrite) / Ctrl+Shift+V (insert): the copied clips at the playhead on their own tracks (the nearest unlocked track of
    /// the kind when theirs is locked or missing), keeping their spacing; links and groups among them are kept with new ids, object masks get
    /// their own copies. Overwrite replaces what is under each pasted clip on its track; insert moves everything from the playhead on later by
    /// the pasted span (every unlocked track, like an insert edit). The playhead goes to the end. One undo step.</summary>
    public List<Clip> Paste(bool insert)
    {
        var list = Copied;
        if (list == null || list.Count == 0) return new();
        list.RemoveAll(c => MediaOf(c) is { } m ? !CanAdd(m) : c.MediaId != null);         // (a sequence into itself; media no longer in the project)
        if (list.Count == 0) return list;
        double t0 = list.Min(c => c.Start), span = list.Max(c => c.End) - t0, at = Frame(Time);
        int Row(Clip c)
        {
            bool video = c.Kind != ClipKind.Audio;
            if (c.Track < Tracks.Count && Tracks.IsVideo(c.Track) == video && !Locked(c.Track)) return c.Track;
            var rows = video ? Tracks.VideoRows.Reverse() : Tracks.AudioRows;                 // (V1 up / A1 down)
            return rows.FirstOrDefault(r => !Locked(r), -1);
        }
        var links = new Dictionary<string, string>(); var groups = new Dictionary<string, string>();
        foreach (var c in list)
        {
            c.Id = NewId(); c.Track = Row(c); c.Start = Frame(at + c.Start - t0);
            if (c.LinkId != null) c.LinkId = links.TryGetValue(c.LinkId, out var l) ? l : links[c.LinkId] = NewId();
            if (c.GroupId != null) c.GroupId = groups.TryGetValue(c.GroupId, out var g) ? g : groups[c.GroupId] = NewId();
            foreach (var m in c.Masks)
            {
                var nid = NewId();
                if (clipMasks != null && clipMasks.TryGetValue(m.Id, out var st)) { var tr = new MaskTrack(); tr.Load(st); MaskTracks[nid] = tr; }
                m.Id = nid;
            }
        }
        list.RemoveAll(c => c.Track < 0);
        if (list.Count == 0) return list;
        Edit(() =>
        {
            if (insert)
            {
                CutClips(Clips.ToList(), at);
                foreach (var c in Clips.Where(c => Editable(c) && c.Start >= at - Eps)) c.Start += span;
            }
            else
                foreach (var p in list)
                {
                    CutClips(Clips.Where(c => c.Track == p.Track).ToList(), p.End);
                    CutClips(Clips.Where(c => c.Track == p.Track).ToList(), p.Start);
                    Clips.RemoveAll(c => c.Track == p.Track && c.Start >= p.Start - Eps && c.End <= p.End + Eps);
                }
            Clips.AddRange(list);
            Time = at + span;
            Sel = list.Select(c => c.Id).ToHashSet(); SelectedId = list[0].Id;
        }, insert ? "Paste Insert" : "Paste");
        Seeked?.Invoke();
        return list;
    }

    // ---- Paste Attributes / Remove Attributes ----
    static readonly AnimProp[] motionProps = { AnimProp.Scale, AnimProp.X, AnimProp.Y, AnimProp.Rotation, AnimProp.AnchorX, AnimProp.AnchorY, AnimProp.Pan };
    static readonly AnimProp[] cropProps = { AnimProp.CropL, AnimProp.CropT, AnimProp.CropR, AnimProp.CropB };
    static AnimProp[] PropsOf(ClipAttr a) => a switch
    {
        ClipAttr.Motion => motionProps, ClipAttr.Opacity => new[] { AnimProp.Opacity }, ClipAttr.Volume => new[] { AnimProp.Volume }, ClipAttr.Blur => new[] { AnimProp.Blur },
        ClipAttr.Crop => cropProps, ClipAttr.Pin => Pin.Props, ClipAttr.Lens => Lens.Props, ClipAttr.Speed => new[] { AnimProp.Speed }, _ => Array.Empty<AnimProp>(),
    };
    public static readonly (ClipAttr a, string name)[] AttrNames =
    {
        (ClipAttr.Motion, "Motion (scale, position, rotation, anchor)"), (ClipAttr.Opacity, "Opacity and blend mode"), (ClipAttr.Speed, "Speed / reverse"),
        (ClipAttr.Volume, "Volume"), (ClipAttr.Color, "Lumetri Color"), (ClipAttr.Looks, "Stylize (drawn look, sharpen, glow, grain)"), (ClipAttr.Crop, "Crop and flip"),
        (ClipAttr.Key, "Ultra key"), (ClipAttr.Pin, "Corner pin"), (ClipAttr.Lens, "Lens distortion"), (ClipAttr.Shadow, "Drop shadow"), (ClipAttr.Blur, "Gaussian blur"),
        (ClipAttr.Stabilize, "Stabilize"), (ClipAttr.AudioFx, "Audio effects"),
    };
    /// <summary>The attributes a clip has that differ from a fresh clip (what the dialog ticks).</summary>
    public static ClipAttr AttrsOf(Clip c)
    {
        var a = ClipAttr.None;
        bool Keys(ClipAttr x) => PropsOf(x).Any(p => Anim.IsAnimated(c, p));
        if (c.Scale != 100 || c.X != 0 || c.Y != 0 || c.Rotation != 0 || c.AnchorX != 0 || c.AnchorY != 0 || c.Pan != 0 || Keys(ClipAttr.Motion)) a |= ClipAttr.Motion;
        if (c.Opacity != 100 || c.Blend != BlendMode.Normal || Keys(ClipAttr.Opacity)) a |= ClipAttr.Opacity;
        if (c.Speed != 100 || c.Reverse || Keys(ClipAttr.Speed)) a |= ClipAttr.Speed;
        if (c.Volume != 100 || Keys(ClipAttr.Volume)) a |= ClipAttr.Volume;
        if (c.Grade != null) a |= ClipAttr.Color;
        if (c.Toon != null || c.Looks != null) a |= ClipAttr.Looks;
        if (c.CropL != 0 || c.CropT != 0 || c.CropR != 0 || c.CropB != 0 || c.CropFeather != 0 || c.FlipH || c.FlipV || Keys(ClipAttr.Crop)) a |= ClipAttr.Crop;
        if (c.Key != null) a |= ClipAttr.Key;
        if (c.Pin != null) a |= ClipAttr.Pin;
        if (c.Lens != null) a |= ClipAttr.Lens;
        if (c.Shadow != null) a |= ClipAttr.Shadow;
        if (c.Blur != 0 || Keys(ClipAttr.Blur)) a |= ClipAttr.Blur;
        if (c.Stab != null) a |= ClipAttr.Stabilize;
        if (c.AudioFx is { Count: > 0 }) a |= ClipAttr.AudioFx;
        return a;
    }
    static bool Fits(ClipAttr a, Clip c) => c.Kind == ClipKind.Audio
        ? a is ClipAttr.Volume or ClipAttr.AudioFx or ClipAttr.Speed
        : a switch
        {
            ClipAttr.Volume or ClipAttr.AudioFx => false,
            ClipAttr.Speed or ClipAttr.Stabilize => c.Kind == ClipKind.Video,
            ClipAttr.Color or ClipAttr.Looks or ClipAttr.Key or ClipAttr.Lens => c.Kind is ClipKind.Video or ClipKind.Image or ClipKind.Adjustment,
            _ => true,
        };

    /// <summary>Copies the chosen attributes of <paramref name="src"/> (keyframes too, moved to the target's own source times) onto every selected
    /// clip they apply to. One undo step; returns how many clips changed.</summary>
    public int PasteAttributes(Clip src, ClipAttr which)
    {
        var targets = Clips.Where(c => IsSel(c) && Editable(c)).ToList();
        if (targets.Count == 0 || which == ClipAttr.None) return 0;
        int n = 0;
        Edit(() =>
        {
            foreach (var c in targets)
            {
                bool any = false;
                foreach (ClipAttr a in Enum.GetValues<ClipAttr>())
                {
                    if (a == ClipAttr.None || !which.HasFlag(a) || !Fits(a, c)) continue;
                    if (a == ClipAttr.Speed && !TimeMap.Ramped(src))
                    {   // a constant speed: the Speed/Duration rule (same source range, new length; no ripple)
                        c.Anim?.Remove(AnimProp.Speed); if (c.Anim is { Count: 0 }) c.Anim = null;
                        ApplySpeed(new[] { c }, src.Speed, null, src.Reverse, src.PitchLock, false); any = true; continue;
                    }
                    Take(a, src, c);
                    // keyframes: the same times relative to each clip's start (source time moves with In)
                    double shift = (TimeMap.IsRemapped(c) ? TimeMap.Src(c, c.Start) : c.In) - (TimeMap.IsRemapped(src) ? TimeMap.Src(src, src.Start) : src.In);
                    foreach (var p in PropsOf(a))
                    {
                        c.Anim?.Remove(p);
                        if (src.Anim?.TryGetValue(p, out var tr) == true && !tr.IsEmpty)
                        {
                            var copy = Copy(tr)!;
                            foreach (var k in copy.Keys) k.T += shift;
                            (c.Anim ??= new())[p] = copy;
                        }
                    }
                    if (c.Anim is { Count: 0 }) c.Anim = null;
                    any = true;
                }
                if (any) n++;
            }
        });
        return n;
    }

    /// <summary>Resets the chosen attributes of the selected clips to a fresh clip's (keyframes removed). One undo step.</summary>
    public int RemoveAttributes(ClipAttr which)
    {
        var targets = Clips.Where(c => IsSel(c) && Editable(c)).ToList();
        if (targets.Count == 0 || which == ClipAttr.None) return 0;
        var fresh = new Clip();
        int n = 0;
        Edit(() =>
        {
            foreach (var c in targets)
            {
                var had = AttrsOf(c) & which;
                if (had == ClipAttr.None) continue;
                foreach (ClipAttr a in Enum.GetValues<ClipAttr>())
                {
                    if (a == ClipAttr.None || !had.HasFlag(a)) continue;
                    if (a == ClipAttr.Speed) { if (!TimeMap.Ramped(c)) ApplySpeed(new[] { c }, 100, null, false, c.PitchLock, false); else c.Anim?.Remove(AnimProp.Speed); continue; }
                    Take(a, fresh, c);
                    foreach (var p in PropsOf(a)) c.Anim?.Remove(p);
                }
                if (c.Anim is { Count: 0 }) c.Anim = null;
                n++;
            }
        });
        return n;
    }

    /// <summary>One attribute's static values from a to b (copies, never shared objects).</summary>
    static void Take(ClipAttr a, Clip s, Clip c)
    {
        switch (a)
        {
            case ClipAttr.Motion: c.Scale = s.Scale; c.X = s.X; c.Y = s.Y; c.Rotation = s.Rotation; c.AnchorX = s.AnchorX; c.AnchorY = s.AnchorY; c.Pan = s.Pan; break;
            case ClipAttr.Opacity: c.Opacity = s.Opacity; c.Blend = s.Blend; break;
            case ClipAttr.Volume: c.Volume = s.Volume; break;
            case ClipAttr.Color: c.Grade = s.Grade?.Clone(); break;
            case ClipAttr.Looks: c.Toon = s.Toon?.Clone(); c.Looks = s.Looks?.Clone(); break;
            case ClipAttr.Crop: c.CropL = s.CropL; c.CropT = s.CropT; c.CropR = s.CropR; c.CropB = s.CropB; c.CropFeather = s.CropFeather; c.FlipH = s.FlipH; c.FlipV = s.FlipV; break;
            case ClipAttr.Key: c.Key = s.Key?.Clone(); break;
            case ClipAttr.Pin: c.Pin = s.Pin?.Clone(); break;
            case ClipAttr.Lens: c.Lens = s.Lens?.Clone(); break;
            case ClipAttr.Shadow: c.Shadow = s.Shadow?.Clone(); break;
            case ClipAttr.Blur: c.Blur = s.Blur; break;
            case ClipAttr.Stabilize: c.Stab = s.Stab?.Clone(); break;
            case ClipAttr.AudioFx: c.AudioFx = s.AudioFx?.Select(e => e.Clone()).ToList(); break;
        }
    }

    // ---- split screen / picture in picture ----
    /// <summary>Puts the selected picture clips into a layout: the slots are filled in track order (highest first), then by start; the picture-in-
    /// picture presets put every selected clip in that corner; Full puts them back to full frame. Scale / position / crop keyframes on the clips
    /// are replaced. One undo step; returns how many clips were placed.</summary>
    public int ApplyLayout(LayoutPreset p)
    {
        var clips = Clips.Where(c => IsSel(c) && Editable(c) && c.Kind is ClipKind.Video or ClipKind.Image).OrderBy(c => c.Track).ThenBy(c => c.Start).ToList();
        if (clips.Count == 0) return 0;
        const double pip = 33, margin = 3;
        double off = 50 - pip / 2 - margin;
        (double s, double x, double y, double cl, double ct, double cr, double cb)[] slots = p switch
        {
            LayoutPreset.PipTopLeft => new[] { (pip, -off, -off, 0.0, 0.0, 0.0, 0.0) },
            LayoutPreset.PipTopRight => new[] { (pip, off, -off, 0.0, 0.0, 0.0, 0.0) },
            LayoutPreset.PipBottomLeft => new[] { (pip, -off, off, 0.0, 0.0, 0.0, 0.0) },
            LayoutPreset.PipBottomRight => new[] { (pip, off, off, 0.0, 0.0, 0.0, 0.0) },
            LayoutPreset.SideBySide => new[] { (100.0, -25.0, 0.0, 25.0, 0.0, 25.0, 0.0), (100.0, 25.0, 0.0, 25.0, 0.0, 25.0, 0.0) },
            LayoutPreset.TopBottom => new[] { (100.0, 0.0, -25.0, 0.0, 25.0, 0.0, 25.0), (100.0, 0.0, 25.0, 0.0, 25.0, 0.0, 25.0) },
            LayoutPreset.Grid2x2 => new[] { (50.0, -25.0, -25.0, 0.0, 0.0, 0.0, 0.0), (50.0, 25.0, -25.0, 0.0, 0.0, 0.0, 0.0), (50.0, -25.0, 25.0, 0.0, 0.0, 0.0, 0.0), (50.0, 25.0, 25.0, 0.0, 0.0, 0.0, 0.0) },
            LayoutPreset.ThreeAcross => new[] { (100.0, -100 / 3.0, 0.0, 100 / 3.0, 0.0, 100 / 3.0, 0.0), (100.0, 0.0, 0.0, 100 / 3.0, 0.0, 100 / 3.0, 0.0), (100.0, 100 / 3.0, 0.0, 100 / 3.0, 0.0, 100 / 3.0, 0.0) },
            _ => new[] { (100.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0) },
        };
        bool repeat = slots.Length == 1;                                     // (a corner / full frame: every clip)
        int n = 0;
        Edit(() =>
        {
            for (int i = 0; i < clips.Count && (repeat || i < slots.Length); i++)
            {
                var c = clips[i]; var s = slots[repeat ? 0 : i];
                c.Scale = s.s; c.X = s.x; c.Y = s.y; c.Rotation = 0; c.AnchorX = c.AnchorY = 0; c.Pan = 0;
                c.CropL = s.cl; c.CropT = s.ct; c.CropR = s.cr; c.CropB = s.cb;
                foreach (var pr in motionProps.Concat(cropProps)) c.Anim?.Remove(pr);
                if (c.Anim is { Count: 0 }) c.Anim = null;
                n++;
            }
        });
        return n;
    }

    // ---- gaps ----
    /// <summary>Empty spans before the end: on one track, or (track null) where no track has a clip (Premiere's "in sequence").</summary>
    public List<(double a, double b)> Gaps(int? track)
    {
        var cover = Merge(Clips.Where(c => track == null || c.Track == track).Select(c => (c.Start, c.End)));
        var res = new List<(double a, double b)>();
        double t = 0;
        foreach (var (a, b) in cover) { if (a - t > OneFrame / 2) res.Add((t, a)); t = Math.Max(t, b); }
        return res;
    }
    /// <summary>Sequence ▸ Go to Gap: the start of the next / previous gap (in the sequence, or on one track).</summary>
    public double? NextGap(double t, int? track) => Gaps(track).Select(g => g.a).Where(a => a > t + Eps).Cast<double?>().FirstOrDefault();
    public double? PrevGap(double t, int? track) => Gaps(track).Select(g => g.a).Where(a => a < t - Eps).Cast<double?>().LastOrDefault();
}
