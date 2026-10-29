using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CutPro;

/// <summary>Premiere's marker colours.</summary>
public enum MarkerColor { Green, Red, Purple, Orange, Yellow, White, Blue, Cyan }

/// <summary>A marker: on a sequence (<see cref="Sequence.Markers"/>, timeline seconds) or on a clip (<see cref="Clip.Markers"/>, SOURCE seconds,
/// so it stays with the content when the clip is trimmed or moved — like keyframes). Sequence markers marked <see cref="Chapter"/> become the
/// export's MP4 / MOV / MKV chapters and the YouTube chapter list.</summary>
public class Marker
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double T { get; set; }
    /// <summary>Length (0 = a point marker).</summary>
    public double Dur { get; set; }
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    public MarkerColor Color { get; set; }
    public bool Chapter { get; set; } = true;
    public Marker Clone() => (Marker)MemberwiseClone();

    /// <summary>The marker colours as drawn (content colours chosen by the user, like Premiere's — not theme colours).</summary>
    public static System.Windows.Media.Color Rgb(MarkerColor c) => c switch
    {
        MarkerColor.Red => System.Windows.Media.Color.FromRgb(0xE0, 0x4B, 0x4B),
        MarkerColor.Purple => System.Windows.Media.Color.FromRgb(0xB0, 0x6B, 0xE0),
        MarkerColor.Orange => System.Windows.Media.Color.FromRgb(0xF0, 0x93, 0x3A),
        MarkerColor.Yellow => System.Windows.Media.Color.FromRgb(0xE8, 0xD2, 0x3A),
        MarkerColor.White => System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2),
        MarkerColor.Blue => System.Windows.Media.Color.FromRgb(0x4A, 0x86, 0xE8),
        MarkerColor.Cyan => System.Windows.Media.Color.FromRgb(0x3E, 0xC8, 0xD8),
        _ => System.Windows.Media.Color.FromRgb(0x5C, 0xC0, 0x5A),
    };
}

/// <summary>Chapters from sequence markers: the export's container chapters (ffmetadata) and the text YouTube reads from a description.</summary>
public static class Chapters
{
    /// <summary>The chapter markers within [t0, t1) as times from t0 (an In–Out export starts its chapters at the In point), sorted, one per frame.</summary>
    public static List<(double t, string name)> Of(IEnumerable<Marker> markers, double t0, double t1)
    {
        var list = new List<(double t, string name)>();
        foreach (var m in markers.Where(m => m.Chapter && m.T >= t0 - 1e-6 && m.T < t1 - 1e-6).OrderBy(m => m.T))
        {
            double t = Math.Max(0, m.T - t0);
            if (list.Count > 0 && t - list[^1].t < 0.5 / TimeUtil.Fps) continue;
            list.Add((t, m.Name.Trim().Length > 0 ? m.Name.Trim() : $"Chapter {list.Count + 1}"));
        }
        return list;
    }

    /// <summary>"0:00 Intro" lines (h:mm:ss past an hour). YouTube wants the first at 0:00 (one is added if missing), at least three, each at
    /// least 10 seconds long — what is wrong is listed in <paramref name="problems"/> (the text is still made).</summary>
    public static string YouTube(List<(double t, string name)> ch, double end, out List<string> problems)
    {
        problems = new List<string>();
        var l = ch.ToList();
        if (l.Count == 0) { problems.Add("There are no chapter markers (add markers with M; their names become the chapters)."); return ""; }
        if (l[0].t >= 1) { l.Insert(0, (0, "Intro")); problems.Add("YouTube's first chapter must start at 0:00 — “0:00 Intro” was added."); }
        else l[0] = (0, l[0].name);
        if (l.Count < 3) problems.Add($"YouTube needs at least 3 chapters (there are {l.Count}).");
        for (int i = 0; i < l.Count; i++)
        {
            double len = (i + 1 < l.Count ? l[i + 1].t : end) - l[i].t;
            if (len < 10) problems.Add($"“{l[i].name}” is {len:0.#} s long — YouTube chapters must be at least 10 s.");
        }
        bool hours = end >= 3600;
        return string.Join("\n", l.Select(c => $"{Stamp(c.t, hours)} {c.name}"));
    }
    public static string Stamp(double t, bool hours)
    {
        int s = (int)Math.Floor(t + 1e-6);
        return hours ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    /// <summary>ffmpeg's metadata file with one [CHAPTER] per chapter (milliseconds; each ends where the next starts, the last at the end).</summary>
    public static string FfMetadata(List<(double t, string name)> ch, double end)
    {
        static string Esc(string s) => new StringBuilder(s).Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\n", "\\\n").ToString();
        var sb = new StringBuilder(";FFMETADATA1\n");
        for (int i = 0; i < ch.Count; i++)
        {
            long a = (long)Math.Round(ch[i].t * 1000), b = (long)Math.Round((i + 1 < ch.Count ? ch[i + 1].t : end) * 1000);
            if (b <= a) continue;
            sb.Append("[CHAPTER]\nTIMEBASE=1/1000\n").Append(CultureInfo.InvariantCulture, $"START={a}\nEND={b}\n").Append("title=").Append(Esc(ch[i].name)).Append('\n');
        }
        return sb.ToString();
    }
    /// <summary>The chapters a file gets: an MP4 chapter track always starts at 0 (a first chapter later than that is pulled to 0), so a range whose
    /// first chapter starts later gets an “Intro” chapter before it (YouTube asks for the same) and every other keeps its time.</summary>
    public static List<(double t, string name)> ForFile(List<(double t, string name)> ch) =>
        ch.Count > 0 && ch[0].t >= 0.5 / TimeUtil.Fps ? ch.Prepend((0.0, "Intro")).ToList() : ch;
    /// <summary>Containers that keep chapters.</summary>
    public static bool Supported(string container) => container is "mp4" or "mov" or "mkv" or "m4a";
}

/// <summary>Names of undo steps for the History panel: the editor method (or the panel's file) that made the change.</summary>
public static class HistoryLabels
{
    static readonly Dictionary<string, string> names = new()
    {
        ["AddEdit"] = "Add Edit", ["CutClips"] = "Razor", ["DeleteSelection"] = "Delete", ["RemoveRange"] = "Remove Range", ["RippleTrim"] = "Ripple Trim",
        ["ToggleLink"] = "Link / Unlink", ["AddMedia"] = "Add Clip", ["PlaceSource"] = "Insert / Overwrite", ["AddTitle"] = "Add Title", ["AddAdjustment"] = "Add Adjustment Layer",
        ["ApplyTheme"] = "Apply Title Theme", ["SetSpeed"] = "Speed/Duration", ["SetHold"] = "Freeze Frame", ["AddMask"] = "Add Object Mask", ["RemoveMask"] = "Remove Object Mask",
        ["NestSelection"] = "Nest", ["CreateMultiCam"] = "Create Multicam Sequence", ["AddTrack"] = "Add Track", ["ApplyDefaultTransitions"] = "Apply Transitions", ["Touch"] = "Change Property",
        ["OnMouseLeftButtonDown"] = "Move / Trim", ["OnDrop"] = "Drop", ["BeginKeyDrag"] = "Move Keyframe", ["BandBegin"] = "Volume Band", ["OnMouseRightButtonDown"] = "Transition",
        ["LiftExtract"] = "Lift / Extract", ["Paste"] = "Paste", ["PasteAttributes"] = "Paste Attributes", ["RemoveAttributes"] = "Remove Attributes",
        ["GroupSelection"] = "Group", ["UngroupSelection"] = "Ungroup", ["ApplyLayout"] = "Layout Preset", ["Relink"] = "Relink Media",
        ["BuildInspectorCore"] = "Change Property", ["Slider"] = "Change Property", ["Num"] = "Change Property", ["UiShot"] = "Edit", ["StartPlaytest"] = "Edit",
    };
    static readonly Dictionary<string, string> files = new()
    {
        ["ColorUi"] = "Lumetri Color", ["ColorControls"] = "Lumetri Color", ["CompositeUi"] = "Composite", ["AudioFxUi"] = "Audio Effects", ["InspectorText"] = "Title",
        ["KeyframeEditor"] = "Keyframes", ["KeyframeUi"] = "Keyframes", ["ObjectUi"] = "Object Mask", ["PinUi"] = "Corner Pin", ["StabUi"] = "Stabilize",
        ["CaptionsUi"] = "Captions", ["Captioning"] = "Captions", ["EffectsUi"] = "Transition", ["HookStudio"] = "Intro & Hook", ["Reframe"] = "Auto Reframe",
        ["TimelineBands"] = "Volume Band", ["MixerUi"] = "Audio Track Mixer", ["SpeedUi"] = "Speed", ["Lens"] = "Lens Distortion",
    };
    /// <summary>A known method's name, else the panel's (its source file), else the method name with spaces ("ApplyStyle" → "Apply Style").</summary>
    public static string Of(string member, string file)
    {
        if (names.TryGetValue(member, out var n)) return n;
        if (files.TryGetValue(Path.GetFileNameWithoutExtension(file), out var f)) return f;
        var sb = new StringBuilder();
        foreach (var ch in member.TrimEnd('_'))
        {
            if (char.IsUpper(ch) && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            sb.Append(ch);
        }
        var s = sb.ToString().Replace(" Click", "").Replace("_", " ").Trim();
        return s.Length == 0 || s.StartsWith('<') ? "Edit" : s;
    }
}

/// <summary>Markers (partial Editor): sequence markers have their own undo entries (a snapshot of the sequence's list); clip markers live on the
/// clips and so in the Clips snapshots.</summary>
public partial class Editor
{
    /// <summary>The active sequence's markers (timeline seconds).</summary>
    public List<Marker> Markers => seq.Markers ??= new();
    public event Action? MarkersChanged;
    public void RaiseMarkers() => Raise(MarkersChanged);
    string SnapMarks() => JsonSerializer.Serialize(seq.Markers ?? new List<Marker>());
    void LoadMarks(string json) { seq.Markers = JsonSerializer.Deserialize<List<Marker>>(json); Raise(MarkersChanged); Raise(PropsChanged); }

    /// <summary>One undoable change to the sequence's markers.</summary>
    public void MarkEdit(Action a, string label)
    {
        Commit(); MixEnd(); MarkEnd();
        var before = SnapMarks();
        a();
        if (before != SnapMarks()) Push(new Hist(null, Marks: before, SeqId: seq.Id), label);
        Raise(MarkersChanged); Raise(PropsChanged);
    }
    string? pendingMarks;
    /// <summary>Dragging a marker on the ruler: one undo step until <see cref="MarkEnd"/>.</summary>
    public void MarkBegin() { Commit(); pendingMarks ??= SnapMarks(); }
    public void MarkEnd()
    {
        if (pendingMarks != null && pendingMarks != SnapMarks()) Push(new Hist(null, Marks: pendingMarks, SeqId: seq.Id), "Move Marker");
        pendingMarks = null;
    }

    /// <summary>M: a sequence marker at t (the one already on that frame if there is one).</summary>
    public Marker AddMarker(double t, string? name = null)
    {
        t = Frame(t);
        if (Markers.FirstOrDefault(m => Math.Abs(m.T - t) < OneFrame / 2) is { } had) return had;
        var mk = new Marker { T = t, Name = name ?? "" };
        MarkEdit(() => Markers.Add(mk), "Add Marker");
        return mk;
    }
    /// <summary>A marker on a clip at timeline time t (stored at the source time shown there).</summary>
    public Marker? AddClipMarker(Clip c, double t)
    {
        if (!Editable(c) || t < c.Start - Eps || t >= c.End) return null;
        double src = TimeMap.Src(c, Frame(t));
        if (c.Markers?.FirstOrDefault(m => Math.Abs(m.T - src) < OneFrame / 2) is { } had) return had;
        var mk = new Marker { T = src, Color = MarkerColor.Orange, Chapter = false };
        Edit(() => (c.Markers ??= new()).Add(mk), "Add Clip Marker");
        return mk;
    }
    /// <summary>Changes a marker's fields (name, note, colour, chapter, length) — c = the clip it is on (null: a sequence marker).</summary>
    public void UpdateMarker(Marker mk, Clip? c, Action<Marker> change)
    {
        if (c == null) MarkEdit(() => change(mk), "Edit Marker");
        else Edit(() => change(mk), "Edit Clip Marker");
    }
    public void DeleteMarker(Marker mk, Clip? c)
    {
        if (c == null) MarkEdit(() => Markers.RemoveAll(m => m.Id == mk.Id), "Delete Marker");
        else Edit(() => c.Markers?.RemoveAll(m => m.Id == mk.Id), "Delete Clip Marker");
    }
    /// <summary>Ctrl+Alt+Shift+M: every sequence marker (and the selected clips' markers).</summary>
    public void ClearMarkers()
    {
        var clips = Clips.Where(c => IsSel(c) && c.Markers is { Count: > 0 }).ToList();
        if (clips.Count > 0) Edit(() => { foreach (var c in clips) c.Markers = null; }, "Clear Clip Markers");
        if (Markers.Count > 0) MarkEdit(() => Markers.Clear(), "Clear Markers");
    }

    /// <summary>A clip marker's time on the timeline (null when its source time isn't shown by the clip).</summary>
    public static double? TimelineTime(Clip c, Marker m)
    {
        double t = Anim.TimelineTime(c, m.T);
        return t >= c.Start - Eps && t < c.End - Eps ? t : null;
    }
    /// <summary>Every marker on the timeline: the sequence's and the clips' (at their timeline times).</summary>
    public IEnumerable<(double t, Marker m, Clip? c)> AllMarkers()
    {
        foreach (var m in Markers) yield return (m.T, m, null);
        foreach (var c in Clips) if (c.Markers != null) foreach (var m in c.Markers) if (TimelineTime(c, m) is double t) yield return (t, m, c);
    }
    /// <summary>Shift+M / Ctrl+Shift+M: the next / previous marker (sequence or clip).</summary>
    public double? NextMarker(double t) { var l = AllMarkers().Select(x => x.t).Where(x => x > t + Eps).ToList(); return l.Count > 0 ? l.Min() : null; }
    public double? PrevMarker(double t) { var l = AllMarkers().Select(x => x.t).Where(x => x < t - Eps).ToList(); return l.Count > 0 ? l.Max() : null; }

    /// <summary>The chapters an export of this range gets (the sequence's chapter markers).</summary>
    public List<(double t, string name)> ChaptersFor(double t0, double t1) => Chapters.Of(Markers, t0, t1);
}
