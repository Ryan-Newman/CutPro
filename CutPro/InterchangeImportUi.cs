using System.Windows;
using Microsoft.Win32;

namespace CutPro;

public partial class Editor
{
    /// <summary>An imported timeline as a new sequence of the project (opened; one undo step). Media: each imported clip's item (null = left out).
    /// Rows follow the file's tracks (at least the usual 3 + 2); times are put on the new sequence's frames; pictures and sound of one shot are
    /// linked; titles become plain titles with their text.</summary>
    public Sequence ImportTimeline(ImpTimeline t, IReadOnlyDictionary<ImpClip, MediaItem?> media)
    {
        var f = FormatOf(seq);
        double fps = TimeUtil.Exact(t.Fps > 0 ? t.Fps : f.Fps);
        int vt = Math.Clamp(Math.Max(Tracks.DefaultVideo, t.Clips.Where(c => c.Video).Select(c => c.Track + 1).DefaultIfEmpty(1).Max()), 1, Tracks.MaxVideo);
        int at = Math.Clamp(Math.Max(Tracks.DefaultAudio, t.Clips.Where(c => !c.Video).Select(c => c.Track + 1).DefaultIfEmpty(1).Max()), 1, Tracks.MaxAudio);
        var s = new Sequence
        {
            Name = t.Name.Length > 0 && !Sequences.Any(x => x.Name == t.Name) ? t.Name : FreeName(t.Name.Length > 0 ? t.Name : "Imported"), SeqW = t.W > 0 ? t.W : f.OutW, SeqH = t.H > 0 ? t.H : f.OutH, SeqFps = fps, Reframe = f.Reframe,
            VideoTracks = vt, AudioTracks = at, Markers = t.Markers.Count > 0 ? t.Markers.Select(m => m.Clone()).ToList() : null,
        };
        double Fr(double x) => Math.Round(Math.Max(0, x) * fps) / fps;
        var links = new Dictionary<string, string>();
        foreach (var ic in t.Clips.Where(c => c.Track < (c.Video ? vt : at)))
        {
            var m = media.GetValueOrDefault(ic);
            var kind = ic.Kind ?? (ic.Video ? (m?.Type == ClipKind.Image ? ClipKind.Image : ClipKind.Video) : ClipKind.Audio);
            int row = ic.Video ? vt - 1 - ic.Track : vt + ic.Track;
            var c = new Clip { Kind = kind, Track = row, Start = Fr(ic.Start), Dur = Math.Max(1 / fps, Fr(ic.Dur)), Volume = ic.Volume, Opacity = ic.Opacity };
            if (kind == ClipKind.Text) { c.Text = ic.Name.Length > 0 ? ic.Name : "Title"; c.Role = TextRole.Title; c.Style = Themes.Get(ThemeName).For(TextRole.Title).Clone(); }
            else if (kind == ClipKind.Adjustment) { }
            else
            {
                if (m == null) continue;
                c.MediaId = m.Id;
                double inPt = ic.In;
                if (ic.SrcTc is long tc) inPt = tc / (double)Math.Round(fps) - MediaTcSeconds(m);   // (an EDL's source timecode, minus the file's own start)
                if (ic.SrcTc != null && ic.Speed < 0) inPt -= Math.Abs(ic.Speed) / fps;
                c.In = kind == ClipKind.Image ? 0 : Math.Max(0, inPt);
                c.Hold = ic.Hold;
                if (kind is ClipKind.Video or ClipKind.Audio && !ic.Hold)
                {
                    c.Speed = Math.Round(Math.Clamp(Math.Abs(ic.Speed) * 100, TimeMap.MinSpeed, TimeMap.MaxSpeed), 4);
                    c.Reverse = ic.Speed < 0;
                }
                c.NoAudio = kind == ClipKind.Video;                                   // (the file lists the sound it uses as its own clips)
                c.Markers = ic.Markers?.Select(x => x.Clone()).ToList();
            }
            if (ic.TransIn is { } tr) c.TransIn = new Transition { Kind = tr.Kind, Dur = Fr(tr.Dur), Align = tr.Align };
            if (ic.Link != null) c.LinkId = links.TryGetValue(ic.Link, out var id) ? id : links[ic.Link] = NewId();
            s.Clips.Add(c);
        }
        // overlapping clips on one track (an EDL's dissolve events, rounding): each keeps the part before the next one
        foreach (var g in s.Clips.GroupBy(c => c.Track))
        {
            var list = g.OrderBy(c => c.Start).ToList();
            for (int i = 0; i + 1 < list.Count; i++)
                if (list[i].End > list[i + 1].Start + 1e-6) list[i].Dur = Math.Max(1 / fps, list[i + 1].Start - list[i].Start);
        }
        s.Fix();
        ProjEdit(() => { Sequences.Add(s); Activate(s); });
        return s;
    }

    /// <summary>A file's start timecode in seconds (0 without one) — EDL source times count from it.</summary>
    static double MediaTcSeconds(MediaItem m)
    {
        if (m.Timecode is not { } tc) return 0;
        var p = tc.Split(':', ';', '.');
        if (p.Length != 4 || !p.All(x => int.TryParse(x, out _))) return 0;
        double nom = Math.Round(m.Fps > 1 ? m.Fps : TimeUtil.Fps);
        return ((int.Parse(p[0]) * 60 + int.Parse(p[1])) * 60 + int.Parse(p[2])) + int.Parse(p[3]) / nom;
    }
}

public partial class MainWindow
{
    /// <summary>File ▸ Import Sequence (XML / OTIO / EDL): the timeline becomes a new sequence; its media are found by path (or, for an EDL, by clip
    /// name among the project's media and next to the file), probed and added to a bin named after it; missing files come in offline.</summary>
    async Task ImportSequenceAsync(string? file = null)
    {
        if (file == null)
        {
            var dlg = new OpenFileDialog { Filter = InterchangeImport.DialogFilter, Title = "Import a sequence from another editor" };
            if (dlg.ShowDialog(this) != true) return;
            file = dlg.FileName;
        }
        ImpTimeline t;
        try { t = InterchangeImport.Read(file, TimeUtil.Fps); }
        catch (Exception ex) { Alert.Show(this, ex.Message, "Import Sequence", AlertKind.Error); return; }
        SetTask($"Importing “{t.Name}”…", 0);
        var (media, added, offline) = await ResolveImportMedia(t, Path.GetDirectoryName(file)!, t.Name);
        Pause();
        var s = ed.ImportTimeline(t, media);
        binRef = null; ed.RaiseChanged(); RefreshBin();
        SetTask($"“{s.Name}”: {s.Clips.Count} clip{(s.Clips.Count == 1 ? "" : "s")} imported" + (added > 0 ? $", {added} media file{(added == 1 ? "" : "s")} added" : ""), 4000);
        var notes = t.Notes.Distinct().Take(12).ToList();
        if (offline.Count > 0) notes.Insert(0, $"{offline.Count} file{(offline.Count == 1 ? " is" : "s are")} missing (offline — right-click ▸ Relink…): " + string.Join(", ", offline.Take(5).Select(Path.GetFileName)));
        if (t.Clips.Any(c => c.Path == null && c.Kind == null)) notes.Add("Some clips had no media file and were left out.");
        if (notes.Count > 0) Alert.Show(this, string.Join("\n• ", notes.Prepend("Imported, with these notes:")), "Import Sequence", AlertKind.Warning);
    }

    /// <summary>The project item for each imported clip: the project's own item for the same path (or, without a path, the same file name),
    /// a file of that name next to the imported file, else a new item (probed; offline when missing).</summary>
    internal async Task<(Dictionary<ImpClip, MediaItem?> map, int added, List<string> offline)> ResolveImportMedia(ImpTimeline t, string folder, string binName)
    {
        var map = new Dictionary<ImpClip, MediaItem?>();
        var byPath = new Dictionary<string, MediaItem?>(StringComparer.OrdinalIgnoreCase);
        int added = 0; var offline = new List<string>();
        string bin = binName.Length > 0 ? ed.NewBin(binPath, binName) : binPath;
        bool binUsed = false;
        foreach (var ic in t.Clips)
        {
            if (ic.Kind is ClipKind.Text or ClipKind.Adjustment) { map[ic] = null; continue; }
            string? path = ic.Path;
            if (path == null && ic.Name.Length > 0)
            {   // EDL: the clip name — the project's media, then the folder of the EDL
                var known = ed.Media.FirstOrDefault(m => m.Seq == null && string.Equals(Path.GetFileName(m.Path), ic.Name, StringComparison.OrdinalIgnoreCase));
                path = known?.Path ?? (File.Exists(Path.Combine(folder, ic.Name)) ? Path.Combine(folder, ic.Name) : null);
            }
            if (path == null) { map[ic] = null; continue; }
            if (!byPath.TryGetValue(path, out var m))
            {
                m = ed.Media.FirstOrDefault(x => x.Seq == null && string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
                if (m == null && !File.Exists(path) && File.Exists(Path.Combine(folder, Path.GetFileName(path))))
                {
                    var near = Path.Combine(folder, Path.GetFileName(path));
                    m = ed.Media.FirstOrDefault(x => string.Equals(x.Path, near, StringComparison.OrdinalIgnoreCase)) ?? (await MediaProbe.ProbeAsync(near));
                    path = near;
                }
                else if (m == null && File.Exists(path)) m = await MediaProbe.ProbeAsync(path);
                if (m == null)
                {   // missing: an offline item that can be relinked later
                    m = new MediaItem { Path = path, Type = InterchangeImport.IsStill(path) ? ClipKind.Image : ic.Video ? ClipKind.Video : ClipKind.Audio, HasAudio = !ic.Video,
                                        Duration = t.Clips.Where(c => c.Path == ic.Path).Select(c => c.In + c.Dur * Math.Abs(c.Speed)).DefaultIfEmpty(0).Max() + 1, Width = Editor.OutW, Height = Editor.OutH };
                    m.RefreshOnline();
                    offline.Add(path);
                }
                if (!ed.Media.Contains(m)) { m.Bin = bin; ed.Media.Add(m); added++; binUsed = true; }
                byPath[path] = m;
            }
            map[ic] = m;
        }
        if (!binUsed && bin != binPath) ed.DeleteBin(bin);
        return (map, added, offline);
    }
}
