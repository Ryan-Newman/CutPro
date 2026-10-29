namespace CutPro;

/// <summary>Edits behind the analysis tools and the timeline's drop modes: scene cuts, audio sync, beat markers / cut to the beat,
/// several ranges removed at once (text-based editing), overwrite / insert drops, replace edit, fit to fill.</summary>
public partial class Editor
{
    // ---- scene edit detection ----
    /// <summary>A cut (with the linked sound) at every shot change of a clip (source times), or a clip marker there. Returns how many.</summary>
    public int SceneEdits(Clip c, IReadOnlyList<double> srcTimes, bool cut)
    {
        var at = srcTimes.Select(s => Frame(TimeMap.Out(c, s))).Where(t => t > c.Start + OneFrame / 2 && t < c.End - OneFrame / 2).Distinct().OrderByDescending(t => t).ToList();
        if (at.Count == 0 || !Editable(c)) return 0;
        if (cut)
        {
            var group = WithPartners(new[] { c });
            Edit(() => { foreach (var t in at) CutClips(group, t); }, "Scene Edit Detection");     // (latest first: the clips stay the left pieces)
        }
        else
            Edit(() =>
            {
                foreach (var t in at)
                {
                    double src = TimeMap.Src(c, t);
                    if (c.Markers?.Any(m => Math.Abs(m.T - src) < OneFrame / 2) == true) continue;
                    (c.Markers ??= new()).Add(new Marker { T = src, Color = MarkerColor.Cyan, Name = "Shot", Chapter = false });
                }
            }, "Scene Edit Detection");
        return at.Count;
    }

    // ---- synchronize by sound ----
    /// <summary>Moves each clip (with its linked partners) so its sound lines up with the reference's: lag = where the reference's source
    /// time s sounds in the other's media (s + lag). The start stays on a frame; the sub-frame rest goes into the source In. Clips that would
    /// overlap another clip move to a free track of their kind. Returns the clips that could not be placed.</summary>
    public List<Clip> SyncClips(Clip reference, IReadOnlyList<(Clip c, double lag)> others)
    {
        var failed = new List<Clip>();
        Edit(() =>
        {
            double k = reference.Start - reference.In;                            // the reference: timeline = source + k (100 %)
            foreach (var (c, lag) in others)
            {
                var group = WithPartners(new[] { c }).Where(x => x.MediaId == c.MediaId).ToList();
                double K = k - lag;                                                // c must show source s at timeline s + K
                double start = Math.Max(0, Math.Ceiling((K + c.In) * TimeUtil.Fps - 1e-6) / TimeUtil.Fps);   // (on a frame, never before its own In)
                var ids = group.Select(x => x.Id).ToHashSet();
                foreach (var x in group)
                {
                    double newIn = start - K, head = newIn - x.In;
                    x.Start = start; x.In = newIn;
                    if (head > OneFrame / 2) x.Dur = Math.Max(OneFrame, Frame(x.Dur - head));      // (it would have started before 0: its head goes)
                    // a clash on its track: the next free track of the kind (unlocked) — else it can't go
                    if (Clips.Any(o => !ids.Contains(o.Id) && o.Track == x.Track && o.Start < x.End - Eps && o.End > x.Start + Eps))
                    {
                        var rows = Tracks.IsVideo(x.Track) ? Tracks.VideoRows : Tracks.AudioRows;             // (V1 upwards, A1 downwards)
                        int free = rows.FirstOrDefault(r => !Locked(r) && !Clips.Any(o => !ids.Contains(o.Id) && o.Track == r && o.Start < x.End - Eps && o.End > x.Start + Eps), -1);
                        if (free < 0) failed.Add(x); else x.Track = free;
                    }
                }
            }
        }, "Synchronize");
        if (failed.Count > 0) Undo();                                             // (all or nothing)
        return failed;
    }

    // ---- beats ----
    /// <summary>Sequence markers on beats (timeline times): every `every`-th beat counted from the downbeat phase; downbeats get the
    /// "Beat 1" name. Old beat markers are replaced. One undo step.</summary>
    public int BeatMarkers(IReadOnlyList<double> times, int every, int downbeat)
    {
        int n = 0;
        MarkEdit(() =>
        {
            Markers.RemoveAll(m => m.Name.StartsWith("Beat", StringComparison.Ordinal) && m.Color == MarkerColor.Blue);
            for (int i = 0; i < times.Count; i++)
            {
                int k = ((i - downbeat) % 4 + 4) % 4;
                if (every > 1 && ((i - downbeat) % every + every) % every != 0) continue;
                Markers.Add(new Marker { T = Frame(times[i]), Name = k == 0 ? "Beat 1" : "Beat", Color = MarkerColor.Blue });
                n++;
            }
            Markers.Sort((a, b) => a.T.CompareTo(b.T));
        }, "Beat Markers");
        return n;
    }
    public List<double> BeatTimes() => Markers.Where(m => m.Name.StartsWith("Beat", StringComparison.Ordinal) && m.Color == MarkerColor.Blue).Select(m => m.T).OrderBy(t => t).ToList();

    /// <summary>Cut to the beat: the clips (one track, in order) are laid end to end from the first one's start, each ending on the beat
    /// marker nearest its current end (at least one beat long; no further than its media allows). Linked sound follows; the clips after them
    /// on the track move with the change. Returns false when there are no beat markers.</summary>
    public bool SnapToBeats(IReadOnlyList<Clip> clips)
    {
        var beats = BeatTimes();
        if (beats.Count < 2 || clips.Count == 0) return false;
        var order = clips.Where(Editable).OrderBy(c => c.Start).ToList();
        if (order.Count == 0) return false;
        int track = order[0].Track;
        order = order.Where(c => c.Track == track).ToList();
        Edit(() =>
        {
            var ids = WithPartners(order).Select(c => c.Id).ToHashSet();
            double oldEnd = order[^1].End;
            double t = order[0].Start;
            foreach (var c in order)
            {
                var m = MediaOf(c);
                double room = c.Kind is ClipKind.Video or ClipKind.Audio && m != null && !c.Hold && m.Seq == null ? TimeMap.TailRoom(c, m.Duration) : double.MaxValue;
                double want = t + c.Dur;
                var after = beats.Where(b => b > t + OneFrame / 2).ToList();
                if (after.Count == 0) { Place(c, t, c.Dur); t += c.Dur; continue; }
                double end = after.OrderBy(b => Math.Abs(b - want)).First();
                if (end - t > c.Dur + room + Eps) end = after.Where(b => b - t <= c.Dur + room + Eps).DefaultIfEmpty(t + c.Dur).Max();
                Place(c, t, Frame(end - t));
                t = c.End;
            }
            double delta = t - oldEnd;
            if (Math.Abs(delta) > Eps)
                foreach (var x in Clips.Where(x => !ids.Contains(x.Id) && x.Track == track && x.Start >= oldEnd - Eps)) x.Start = Math.Max(0, x.Start + delta);
            void Place(Clip c, double start, double dur)
            {
                foreach (var x in Linked(c)) { x.Start = start; x.Dur = Math.Max(OneFrame, dur); }
            }
        }, "Cut to the Beat");
        return true;
    }

    // ---- text-based editing ----
    /// <summary>Removes several time ranges from every unlocked track and closes them up (latest first) — one undo step.</summary>
    public void RemoveRanges(IEnumerable<(double A, double B)> ranges, string label = "Delete Words")
    {
        var list = Merge(ranges.Select(r => (Frame(r.A), Frame(r.B))).Where(r => r.Item2 - r.Item1 >= OneFrame / 2)).OrderByDescending(r => r.A).ToList();
        if (list.Count == 0) return;
        Edit(() =>
        {
            foreach (var (a, b) in list)
            {
                CutClips(Clips.ToList(), b);
                CutClips(Clips.ToList(), a);
                Clips.RemoveAll(c => Editable(c) && c.Start >= a - Eps && c.End <= b + Eps);
                ShiftLeft(null, b, b - a);
                if (Time > b) Time -= b - a; else if (Time > a) Time = a;
            }
        }, label);
        Seeked?.Invoke();
    }

    // ---- drops from the Project panel / source monitor (Premiere: overwrite; Ctrl = insert; Alt onto a clip = replace) ----
    /// <summary>The clips a media item becomes at t on a video row and / or an audio row (no tracks checked).</summary>
    List<Clip> Pieces(MediaItem m, double t, int? vRow, int? aRow)
    {
        var (s0, s1) = SourceRange(m);
        if (m.Type is ClipKind.Video or ClipKind.Audio) { s0 = Math.Round(s0 * TimeUtil.Fps) / TimeUtil.Fps; s1 = Math.Round(s1 * TimeUtil.Fps) / TimeUtil.Fps; }
        double dur = Math.Max(OneFrame, Frame(s1 - s0)), inPt = m.Type == ClipKind.Image ? 0 : s0;
        bool snd = m.Type == ClipKind.Audio || m.Type == ClipKind.Video && m.HasAudio;
        var link = vRow != null && aRow != null ? NewId() : null;
        var made = new List<Clip>();
        if (vRow is int v) made.Add(new Clip { MediaId = m.Id, Kind = m.Type, Track = v, Start = t, Dur = dur, In = inPt, LinkId = link, NoAudio = snd });
        if (aRow is int a) made.Add(new Clip { MediaId = m.Id, Kind = ClipKind.Audio, Track = a, Start = t, Dur = dur, In = inPt, LinkId = link });
        return made;
    }
    /// <summary>Overwrite: whatever is on the clips' tracks over their span goes. Insert: every unlocked track is cut at t and moves later.</summary>
    void Lay(List<Clip> made, double t, bool insert)
    {
        double dur = made.Max(c => c.Dur);
        if (insert)
        {
            CutClips(Clips.ToList(), t);
            foreach (var c in Clips.Where(c => Editable(c) && c.Start >= t - Eps)) c.Start += dur;
        }
        else
            foreach (var x in made)
            {
                CutClips(Clips.Where(c => c.Track == x.Track).ToList(), x.End);
                CutClips(Clips.Where(c => c.Track == x.Track).ToList(), t);
                Clips.RemoveAll(c => c.Track == x.Track && Editable(c) && c.Start >= t - Eps && c.End <= x.End + Eps);
            }
        Clips.AddRange(made);
    }

    /// <summary>A drop on the timeline at t (frame-snapped) on a row: overwrite, or insert (everything after t moves later). A video drop
    /// puts the sound on the matching audio track (A1 for V1, …, the lowest unlocked one beyond). Returns the new clips (none: wrong kind / locked).</summary>
    public List<Clip> DropMedia(MediaItem m, int row, double t, bool video, bool audio, bool insert, double inOffset = 0)
    {
        if (!CanAdd(m) || Locked(row)) return new();
        t = Frame(Math.Max(0, t));
        bool pic = m.Type is ClipKind.Video or ClipKind.Image, snd = m.Type == ClipKind.Audio || m.Type == ClipKind.Video && m.HasAudio;
        int? vRow = null, aRow = null;
        if (Tracks.IsVideo(row))
        {
            if (!pic || !video) return new();
            vRow = row;
            if (snd && audio)
            {
                int want = Tracks.A(Math.Min(Tracks.Audio, Tracks.Number(row)));
                aRow = !Locked(want) ? want : Tracks.AudioRows.Where(r => !Locked(r)).Cast<int?>().FirstOrDefault();
            }
        }
        else
        {
            if (!snd || !audio) return new();
            aRow = row;
            if (pic && video && m.Type == ClipKind.Video)
            {
                int want = Tracks.V(Math.Min(Tracks.Video, Tracks.Number(row)));
                vRow = !Locked(want) ? want : Tracks.VideoRows.Where(r => !Locked(r)).Cast<int?>().FirstOrDefault();
            }
        }
        var mc = MultiCamOf(m);
        var made = mc != null ? CamPieces(mc, t, vRow, aRow) : Pieces(m, t, vRow, aRow);   // (a multi-camera source: switchable clips, not a nested one)
        foreach (var c in made) c.In += inOffset;                               // (a recording that began between frames)
        if (made.Count == 0) return made;
        Edit(() => Lay(made, t, insert), mc != null ? "Add Multicam Clip" : insert ? "Insert" : "Overwrite");
        Select(made[0]);
        return made;
    }

    /// <summary>Replace edit (Alt-drag onto a clip / Replace with Clip): the clip keeps its place, length, effects and keyframes and shows
    /// another media from its source In (else where the old one started); the linked sound is replaced too (or removed when the new media is silent).
    /// Returns false when the media is of another kind or too short.</summary>
    public bool ReplaceClip(Clip c, MediaItem m)
    {
        if (!CanAdd(m) || !Editable(c) || c.Kind is ClipKind.Text or ClipKind.Adjustment) return false;
        bool pic = m.Type is ClipKind.Video or ClipKind.Image;
        if (c.Kind == ClipKind.Audio ? !(m.Type == ClipKind.Audio || m.Type == ClipKind.Video && m.HasAudio) : !pic) return false;
        double inPt = m.Type == ClipKind.Image ? 0 : m.SrcIn ?? 0;
        if (m.Type != ClipKind.Image && m.Seq == null)
        {
            double need = TimeMap.Span(c).hi - TimeMap.Span(c).lo;
            if (inPt + need > m.Duration + OneFrame) inPt = Math.Max(0, m.Duration - need);
            if (need > m.Duration + OneFrame) return false;
        }
        Edit(() =>
        {
            foreach (var x in WithPartners(new[] { c }).Where(x => x.MediaId == c.MediaId).ToList())
            {
                if (x.Kind == ClipKind.Audio && x != c && !(m.Type == ClipKind.Video && m.HasAudio)) { Clips.Remove(x); continue; }
                x.MediaId = m.Id;
                if (x.Kind != ClipKind.Audio) x.Kind = m.Type;
                x.In = x.Reverse ? inPt + (TimeMap.Span(x).hi - TimeMap.Span(x).lo) - 1.0 / TimeUtil.Fps : inPt;
                if (x.Kind == ClipKind.Video) x.NoAudio = m.HasAudio && Clips.Any(o => o != x && o.LinkId != null && o.LinkId == x.LinkId && o.Kind == ClipKind.Audio);
                x.Masks.Clear();                                                  // (object masks follow the old picture's content)
            }
        }, "Replace Edit");
        return true;
    }

    /// <summary>Fit to Fill: the source range (marks) into the sequence's In–Out (or from the playhead for the source's length) at whatever
    /// speed fits it, overwriting. Returns the new clips.</summary>
    public List<Clip> FitToFill(MediaItem m, int? vTrack, int? aTrack)
    {
        if (MarkIn is not double a || MarkOut is not double b || b - a < OneFrame / 2 || !CanAdd(m) || m.Seq != null || m.Type == ClipKind.Image) return new();
        int? vt = vTrack is int v && Tracks.IsVideo(v) && !Locked(v) && m.Type == ClipKind.Video ? v : null;
        int? at = aTrack is int x && !Tracks.IsVideo(x) && !Locked(x) && (m.Type == ClipKind.Audio || m.HasAudio) ? x : null;
        if (vt == null && at == null) return new();
        var made = Pieces(m, Frame(a), vt, at);
        double src = made[0].Dur, dur = Frame(b) - Frame(a);
        foreach (var c in made) { c.Dur = dur; c.Speed = Math.Round(100 * src / dur, 4); c.PitchLock = true; }
        Edit(() => { Lay(made, Frame(a), false); Time = Frame(b); }, "Fit to Fill");
        Select(made[0]);
        Seeked?.Invoke();
        return made;
    }
}
