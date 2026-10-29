namespace CutPro;

/// <summary>A multi-camera source sequence (Premiere's): camera n's clips on video track n (V1 = camera 1), their sound linked on the audio
/// tracks, lined up so one sequence time ("multicam time") shows the same moment on every camera — synced by sound, timecode, In points or the
/// clips' starts. Placed on a timeline it becomes ordinary clips that remember their camera (<see cref="CamRef"/>): switching a camera gives a
/// clip another camera's media and In (nothing is rendered, so preview and export show plain clips); switches made while playing become cuts
/// when playback stops (<see cref="Editor.ApplyCamCuts"/>).</summary>
public sealed class MultiCamInfo
{
    /// <summary>The sound multicam clips play: −1 = the shown camera's (audio follows video), else this audio track (0 = A1) of the source sequence, always.</summary>
    public int Audio { get; set; } = -1;
    /// <summary>How the cameras were lined up (Sound / Timecode / In points / Starts) — shown, not used.</summary>
    public string Sync { get; set; } = "Sound";
    public MultiCamInfo Clone() => new() { Audio = Audio, Sync = Sync };
}

/// <summary>A timeline clip cut from a multi-camera source sequence: which one, and which camera it shows (0 = camera 1). −1 on a sound clip
/// that plays the fixed sound (it never switches).</summary>
public sealed class CamRef
{
    public string Seq { get; set; } = "";
    public int Angle { get; set; }
    public CamRef With(int angle) => new() { Seq = Seq, Angle = angle };
}

public partial class Editor
{
    // ---- the source sequence ----
    /// <summary>Camera a's track in a multi-camera source sequence (camera 1 = V1).</summary>
    public static int CamRow(Sequence m, int angle) => m.VideoTracks - 1 - angle;
    public static int CamCount(Sequence m) => m.VideoTracks;
    /// <summary>A camera's name: its track's (rename the track to rename the camera), else "Camera n".</summary>
    public static string CamName(Sequence m, int angle)
    {
        int r = CamRow(m, angle);
        var n = m.TrackInfo is { } ti && r >= 0 && r < ti.Count ? ti[r]?.Name : null;
        return string.IsNullOrWhiteSpace(n) ? $"Camera {angle + 1}" : n!;
    }
    static IEnumerable<Clip> CamClips(Sequence m, int angle) { int r = CamRow(m, angle); return m.Clips.Where(c => c.Track == r && c.Kind == ClipKind.Video).OrderBy(c => c.Start); }
    /// <summary>The camera's clip showing multicam time τ (null: that camera has no picture then).</summary>
    public static Clip? CamClipAt(Sequence m, int angle, double tau) => CamClips(m, angle).FirstOrDefault(c => tau >= c.Start - 1e-6 && tau < c.End - 1e-6);
    /// <summary>The sound that goes with a camera's clip in the source sequence (its linked audio clip).</summary>
    static Clip? CamSound(Sequence m, Clip v) => v.LinkId == null ? null : m.Clips.FirstOrDefault(c => c.LinkId == v.LinkId && c.Kind == ClipKind.Audio);
    /// <summary>The multi-camera source sequence a media item stands for (null: not one).</summary>
    public Sequence? MultiCamOf(MediaItem m) => m.Seq != null && SeqById(m.Seq) is { MultiCam: not null } s ? s : null;

    /// <summary>A multi-camera source sequence: each file placed so its source time 0 is at multicam time k − min k (on a frame, the sub-frame rest
    /// in its In, like <see cref="SyncClips"/>). Cameras on V1, V2 … ("Camera n"), their sound linked on the first audio tracks, sound-only files
    /// (an external recorder) on the audio tracks after them. audioFrom: −1 = the sound follows the camera, 0 … cams − 1 = that camera's sound,
    /// cams + j = sound file j — always. The active sequence's format; it isn't opened. One undo step.</summary>
    public Sequence CreateMultiCam(string name, IReadOnlyList<(MediaItem m, double k)> cams, IReadOnlyList<(MediaItem m, double k)> sounds, int audioFrom, string sync)
    {
        var f = FormatOf(seq);
        double F = f.Fps, k0 = cams.Concat(sounds).Select(x => x.k).DefaultIfEmpty(0).Min();
        int nA = cams.Count(x => x.m.HasAudio) + sounds.Count;
        var S = new Sequence
        {
            Name = name, SeqW = f.OutW, SeqH = f.OutH, SeqFps = f.Fps, Reframe = f.Reframe,
            VideoTracks = Math.Clamp(cams.Count, 1, Tracks.MaxVideo), AudioTracks = Math.Clamp(nA, 1, Tracks.MaxAudio),
            MultiCam = new MultiCamInfo { Sync = sync },
        };
        S.Fix();
        S.TrackInfo = Enumerable.Range(0, S.TrackCount).Select(_ => new TrackInfo()).ToList();
        // on the next frame (a measured offset within 1 ms past a frame stays on it — the sync can't tell such a difference)
        (double start, double inPt) Place(double k) { double at = k - k0, s = Math.Ceiling((at - 0.001) * F) / F; return (s, Math.Max(0, s - at)); }
        int ai = 0, audioRow = -1;
        for (int i = 0; i < cams.Count && i < Tracks.MaxVideo; i++)
        {
            var (m, k) = cams[i];
            var (s, inPt) = Place(k);
            double dur = m.Duration - inPt;
            int vr = S.VideoTracks - 1 - i;
            S.TrackInfo[vr].Name = $"Camera {i + 1}";
            if (dur < 1 / F) continue;
            bool snd = m.HasAudio && ai < S.AudioTracks;
            string? link = snd ? NewId() : null;
            S.Clips.Add(new Clip { MediaId = m.Id, Kind = ClipKind.Video, Track = vr, Start = s, In = inPt, Dur = dur, LinkId = link, NoAudio = m.HasAudio });
            if (!snd) continue;
            int ar = S.VideoTracks + ai;
            S.TrackInfo[ar].Name = $"Camera {i + 1}";
            if (audioFrom == i) audioRow = ai;
            S.Clips.Add(new Clip { MediaId = m.Id, Kind = ClipKind.Audio, Track = ar, Start = s, In = inPt, Dur = dur, LinkId = link });
            ai++;
        }
        for (int j = 0; j < sounds.Count && ai < S.AudioTracks; j++, ai++)
        {
            var (m, k) = sounds[j];
            var (s, inPt) = Place(k);
            int ar = S.VideoTracks + ai;
            S.TrackInfo[ar].Name = System.IO.Path.GetFileNameWithoutExtension(m.Name);
            if (audioFrom == cams.Count + j) audioRow = ai;
            if (m.Duration - inPt >= 1 / F) S.Clips.Add(new Clip { MediaId = m.Id, Kind = ClipKind.Audio, Track = ar, Start = s, In = inPt, Dur = m.Duration - inPt });
        }
        S.MultiCam.Audio = audioFrom < 0 ? -1 : audioRow;
        ProjEdit(() => Sequences.Insert(Sequences.IndexOf(seq) + 1, S));
        return S;
    }

    // ---- multicam clips on a timeline ----
    /// <summary>Plain time (100 %, forward, no hold or ramp): only such clips can switch cameras (the cameras line up second for second).</summary>
    static bool CamPlain(Clip c) => Math.Abs(c.Speed - 100) < 1e-9 && !c.Reverse && !c.Hold && !(c.Anim?.ContainsKey(AnimProp.Speed) ?? false);

    /// <summary>The source sequence's clip a multicam clip shows: same media, on the camera's track (video) or holding the sound (audio), its source
    /// range containing the clip's In.</summary>
    static Clip? CamSource(Sequence m, Clip c)
    {
        bool vid = c.Kind != ClipKind.Audio;
        foreach (var x in m.Clips)
        {
            if (x.MediaId != c.MediaId || (x.Kind != ClipKind.Audio) != vid) continue;
            if (vid && c.Cam is { Angle: >= 0 } r && x.Track != CamRow(m, r.Angle)) continue;
            if (c.In >= x.In - 1e-4 && c.In < x.In + x.Dur + 1e-4) return x;
        }
        return null;
    }

    /// <summary>Multicam time at timeline time t of a clip cut from a multi-camera source (null: it isn't one, or its camera's clip is gone).</summary>
    public double? CamTau(Clip c, double t)
    {
        if (c.Cam is not { } r || SeqById(r.Seq) is not { MultiCam: not null } m || CamSource(m, c) is not { } x) return null;
        return x.Start + (c.In - x.In) + (t - c.Start);
    }

    /// <summary>The multicam clip under t on the highest visible video track (the one the Multi-Camera view follows and switches).</summary>
    public Clip? MultiCamClipAt(double t) =>
        Clips.Where(c => c.Cam != null && c.Kind != ClipKind.Audio && t >= c.Start - Eps && t < c.End - Eps && !(c.Track < TrackOff.Length && TrackOff[c.Track]))
             .OrderBy(c => c.Track).FirstOrDefault();

    /// <summary>The clips a multi-camera source becomes at t: picture segments (camera 1 wherever it has picture, else the first camera that has)
    /// on vRow, and on aRow the sound — the shown camera's (linked to each segment) or the fixed sound cut at the same places (plus where it
    /// runs past the pictures). Whole frames on the timeline. No tracks checked.</summary>
    List<Clip> CamPieces(Sequence m, double t, int? vRow, int? aRow)
    {
        double F = TimeUtil.Fps;
        double Ceil(double x) => Math.Ceiling(x * F - 1e-6) / F; double Floor(double x) => Math.Floor(x * F + 1e-6) / F;
        int n = CamCount(m);
        var cover = Enumerable.Range(0, n).Select(a => CamClips(m, a).Select(x => (x, lo: Ceil(x.Start), hi: Floor(x.End))).Where(c => c.hi - c.lo > 0.5 / F).ToList()).ToList();
        var segs = new List<(int a, Clip x, double lo, double hi)>();
        double tau = 0, last = cover.SelectMany(c => c).Select(c => c.hi).DefaultIfEmpty(0).Max();
        for (int guard = 0; tau < last - 0.5 / F && guard < 100000; guard++)
        {
            int pick = -1; (Clip x, double lo, double hi) iv = default;
            for (int a = 0; a < n && pick < 0; a++)
                foreach (var c in cover[a]) if (tau >= c.lo - 1e-6 && tau < c.hi - 1e-6) { pick = a; iv = c; break; }
            if (pick < 0)
            {   // no camera has picture here: on to the next one that starts
                var next = cover.SelectMany(c => c).Where(c => c.lo > tau + 1e-6).Select(c => c.lo).DefaultIfEmpty(double.NaN).Min();
                if (double.IsNaN(next)) break;
                tau = next; continue;
            }
            double end = iv.hi;
            if (pick > 0) end = Math.Min(end, cover[0].Where(c => c.lo > tau + 1e-6).Select(c => c.lo).DefaultIfEmpty(end).Min());   // (camera 1 takes over again)
            segs.Add((pick, iv.x!, tau, end));
            tau = end;
        }
        var made = new List<Clip>();
        var info = m.MultiCam!;
        var fixedSnd = info.Audio >= 0 ? m.Clips.Where(c => c.Kind == ClipKind.Audio && c.Track == m.VideoTracks + info.Audio).OrderBy(c => c.Start).ToList() : null;
        Clip Snd(Clip y, double lo, double hi, int angle, string? link) =>
            new() { MediaId = y.MediaId, Kind = ClipKind.Audio, Track = aRow!.Value, Start = Frame(t + lo), Dur = Frame(hi - lo), In = y.In + (lo - y.Start), LinkId = link, Cam = new CamRef { Seq = m.Id, Angle = angle } };
        foreach (var (a, x, lo, hi) in segs)
        {
            string? link = aRow != null ? NewId() : null;
            var pieces = new List<Clip>();
            if (aRow != null)
            {
                if (fixedSnd == null) { if (CamSound(m, x) is { } y) pieces.Add(Snd(y, lo, hi, a, link)); }
                else foreach (var y in fixedSnd)
                    {
                        double l = Math.Max(lo, Ceil(y.Start)), h = Math.Min(hi, Floor(y.End));
                        if (h - l > 0.5 / F) pieces.Add(Snd(y, l, h, -1, link));
                    }
            }
            if (vRow is int v)
                made.Add(new Clip { MediaId = x.MediaId, Kind = ClipKind.Video, Track = v, Start = Frame(t + lo), Dur = Frame(hi - lo), In = x.In + (lo - x.Start),
                                    LinkId = pieces.Count > 0 ? link : null, NoAudio = Media.FirstOrDefault(mm => mm.Id == x.MediaId)?.HasAudio ?? false, Cam = new CamRef { Seq = m.Id, Angle = a } });
            if (vRow == null) foreach (var p in pieces) p.LinkId = null;
            made.AddRange(pieces);
        }
        // the fixed sound where no camera has picture (a recorder that ran longer)
        if (fixedSnd != null && aRow != null)
            foreach (var y in fixedSnd)
            {
                double l0 = Ceil(y.Start), h0 = Floor(y.End);
                var gaps = new List<(double, double)> { (l0, h0) };
                foreach (var (_, _, lo, hi) in segs)
                    gaps = gaps.SelectMany(g => g.Item2 <= lo || g.Item1 >= hi ? new[] { g } : new[] { (g.Item1, lo), (hi, g.Item2) }).Where(g => g.Item2 - g.Item1 > 0.5 / F).ToList();
                foreach (var (l, h) in gaps) made.Add(Snd(y, l, h, -1, null));
            }
        return made;
    }

    /// <summary>Camera b for a multicam picture clip (and the sound that follows it) wherever b has picture inside it: the clip is cut where b's
    /// picture starts / ends within it and the pieces b covers show b — same place, length and effects, b's media and In (object masks and an
    /// attached object go: they followed the other picture). Inside an edit. Returns how many pieces switched.</summary>
    int SwitchCore(Clip v, int b)
    {
        if (v.Kind == ClipKind.Audio || v.Cam is not { } r || SeqById(r.Seq) is not { MultiCam: { } info } m || b < 0 || b >= CamCount(m) || !CamPlain(v) || !Editable(v)) return 0;
        if (CamTau(v, v.Start) is not double tau0) return 0;
        double F = TimeUtil.Fps, s0 = v.Start, e0 = v.End;
        double Ceil(double x) => Math.Ceiling(x * F - 1e-6) / F; double Floor(double x) => Math.Floor(x * F + 1e-6) / F;
        var cover = CamClips(m, b).Select(x => (x, lo: s0 + (x.Start - tau0), hi: s0 + (x.End - tau0))).ToList();   // (timeline times)
        foreach (var t in cover.SelectMany(c => new[] { Ceil(c.lo), Floor(c.hi) }).Where(t => t > s0 + 0.5 / F && t < e0 - 0.5 / F).Distinct().OrderByDescending(t => t).ToList())
            CutClips(WithPartners(new[] { v }), t);                           // (latest first: v stays the left piece)
        int n = 0, track = v.Track;
        foreach (var p in Clips.Where(c => c.Track == track && c.Cam?.Seq == r.Seq && c.Kind != ClipKind.Audio && c.Start >= s0 - Eps && c.End <= e0 + Eps).ToList())
        {
            var hit = cover.FirstOrDefault(c => p.Start >= c.lo - 1e-6 && p.End <= c.hi + 0.5 / F);   // (its last frame may end inside a frame)
            if (hit.x == null || p.Cam!.Angle == b && p.MediaId == hit.x.MediaId) continue;
            SetCam(m, info, p, b, hit.x);
            n++;
        }
        return n;
    }

    /// <summary>One piece shows camera b (its clip x in the source sequence); the sound follows unless it is fixed.</summary>
    void SetCam(Sequence m, MultiCamInfo info, Clip p, int b, Clip x)
    {
        double tau = CamTau(p, p.Start) ?? x.Start;
        if (p.MediaId != x.MediaId) { p.Masks.Clear(); p.Follow = null; }
        p.MediaId = x.MediaId; p.In = x.In + (tau - x.Start); p.Cam = p.Cam!.With(b);
        p.NoAudio = Media.FirstOrDefault(mm => mm.Id == x.MediaId)?.HasAudio ?? false;
        if (info.Audio >= 0) return;                                         // (fixed sound: it stays)
        var q = p.LinkId == null ? null : Clips.FirstOrDefault(c => c != p && c.LinkId == p.LinkId && c.Kind == ClipKind.Audio && c.Cam != null);
        if (q?.Cam?.Angle < 0) return;
        var y = CamSound(m, x);
        if (y == null) { if (q != null && Editable(q)) Clips.Remove(q); return; }   // (a camera without sound: silence)
        if (q == null)
        {
            int row = CamSoundRow(p);
            if (row < 0) return;
            q = new Clip { Kind = ClipKind.Audio, Track = row, Start = p.Start, Dur = p.Dur, LinkId = p.LinkId ??= NewId() };
            Clips.Add(q);
        }
        if (!Editable(q)) return;
        q.MediaId = y.MediaId; q.In = y.In + (tau - y.Start); q.Cam = new CamRef { Seq = m.Id, Angle = b };
    }

    /// <summary>Where a multicam clip's new sound goes: the track the neighbouring multicam sound is on (free there), else the first free unlocked audio track.</summary>
    int CamSoundRow(Clip p)
    {
        bool Free(int r) => !Locked(r) && !Clips.Any(c => c.Track == r && c.Start < p.End - Eps && c.End > p.Start + Eps);
        var near = Clips.Where(c => c.Kind == ClipKind.Audio && c.Cam?.Seq == p.Cam!.Seq).OrderBy(c => Math.Min(Math.Abs(c.End - p.Start), Math.Abs(c.Start - p.End))).Select(c => c.Track);
        return near.Concat(Tracks.AudioRows).FirstOrDefault(Free, -1);
    }

    /// <summary>Camera b for each multicam clip (Premiere's click on a camera while stopped, Clip ▸ Multi-Camera ▸ Camera n). One undo step;
    /// returns the pieces switched (0: b has no picture there, or none of them is a multicam clip at plain speed).</summary>
    public int SwitchCamera(IEnumerable<Clip> clips, int b)
    {
        int n = 0;
        var list = clips.Where(c => c.Cam != null && c.Kind != ClipKind.Audio).Distinct().ToList();
        if (list.Count == 0) return 0;
        Edit(() => { foreach (var c in list) if (Clips.Contains(c)) n += SwitchCore(c, b); }, "Multicam Switch");
        return n;
    }

    /// <summary>Cuts the multicam clip at t and shows camera b from there to the end of that piece (Ctrl+click on a camera). One undo step.</summary>
    public int CutToCamera(Clip v, double t, int b) => ApplyCamCuts(v.Track, new[] { (t, b) });

    /// <summary>Camera switches recorded while playing (time, camera): at each, the multicam clip on that track is cut and shows the camera until the
    /// next switch or the end of its piece. A switch to the camera already showing makes no cut; several in one frame: the last one. One undo step;
    /// returns how many switches made a change.</summary>
    public int ApplyCamCuts(int track, IReadOnlyList<(double t, int cam)> cuts)
    {
        int n = 0;
        var list = cuts.Select(x => (t: Frame(x.t), x.cam)).GroupBy(x => Math.Round(x.t * TimeUtil.Fps)).Select(g => g.Last()).OrderBy(x => x.t).ToList();
        Edit(() =>
        {
            foreach (var (t, b) in list)
            {
                var v = Clips.FirstOrDefault(c => c.Track == track && c.Cam != null && c.Kind != ClipKind.Audio && t >= c.Start - Eps && t < c.End - Eps);
                if (v == null || !Editable(v) || !CamPlain(v) || v.Cam!.Angle == b) continue;
                if (t > v.Start + OneFrame / 2)
                {
                    CutClips(WithPartners(new[] { v }), t);
                    v = Clips.FirstOrDefault(c => c.Track == track && c.Cam != null && c.Kind != ClipKind.Audio && Math.Abs(c.Start - t) < Eps);
                    if (v == null) continue;
                }
                if (SwitchCore(v, b) > 0) n++;
            }
        }, "Multicam Cuts");
        return n;
    }

    /// <summary>Clip ▸ Multi-Camera ▸ Flatten: the clips forget their multi-camera source (they stay as they are). One undo step.</summary>
    public int FlattenMultiCam(IEnumerable<Clip> clips)
    {
        var list = clips.Where(c => c.Cam != null).SelectMany(c => WithPartners(new[] { c })).Where(c => c.Cam != null).Distinct().ToList();
        if (list.Count > 0) Edit(() => { foreach (var c in list) c.Cam = null; }, "Flatten Multicam");
        return list.Count;
    }
}

public static class MultiCamSync
{
    /// <summary>A file's start timecode as seconds of real time (drop-frame counted; null without a timecode tag).</summary>
    public static double? TimecodeSeconds(MediaItem m)
    {
        if (m.Timecode is not { } tc) return null;
        var p = tc.Split(':', ';', '.');
        if (p.Length != 4 || !p.All(x => int.TryParse(x, out _))) return null;
        double rate = m.Fps > 0 ? m.Fps : TimeUtil.Fps;
        int nom = (int)Math.Round(rate);
        if (nom <= 0) return null;
        bool df = tc.Contains(';') && Math.Abs(rate * 1001 / 1000 - nom) < 0.05 && nom % 30 == 0;
        int drop = df ? nom / 15 : 0;
        int h = int.Parse(p[0]), mi = int.Parse(p[1]), s = int.Parse(p[2]), f = int.Parse(p[3]);
        int totalMin = h * 60 + mi;
        long frames = ((long)totalMin * 60 + s) * nom + f - drop * (totalMin - totalMin / 10);
        return frames / rate;
    }
}
