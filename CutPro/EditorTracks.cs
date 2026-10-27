namespace CutPro;

/// <summary>
/// Track management of the active sequence (partial Editor): add tracks anywhere (Premiere's Add Track / Add Tracks…), delete tracks with
/// their clips, delete the empty ones, rename, lock and resize. A clip's <see cref="Clip.Track"/> is its row (<see cref="Tracks"/>), so adding
/// or removing a track renumbers the rows below it: <see cref="Retrack"/> moves every clip and every per-row setting — hidden / muted
/// (<see cref="Sequence.TrackOff"/>), the mixer channel (fader, pan, solo, automation, effects, submix routing) and the track's name /
/// lock / height — in one step, undone as one (a project snapshot, <see cref="ProjEdit"/>). Locking and resizing are instant settings, like
/// hiding a track. A locked track's clips can't be selected, cut, moved or rippled, and nothing is edited onto it (<see cref="Locked"/>).
/// </summary>
public partial class Editor
{
    /// <summary>The active sequence's tracks changed (added / removed / renamed / locked), or another sequence opened (its layout is active).</summary>
    public event Action? TracksChanged;
    /// <summary>Rows moved: old row → new row (−1: removed). Per-row state kept outside the sequence (the timeline's band modes) follows.</summary>
    public event Action<int[]>? TracksMoved;
    public void RaiseTracks() => Raise(TracksChanged);

    public bool Locked(int track) => seq.TrackInfo is { } l && track >= 0 && track < l.Count && l[track].Locked;
    /// <summary>The clip is on an unlocked track (it can be selected and edited).</summary>
    public bool Editable(Clip c) => !Locked(c.Track);
    List<TrackInfo> EnsureInfo()
    {
        if (seq.TrackInfo is not { } l || l.Count != seq.TrackCount)
            seq.TrackInfo = l = Enumerable.Range(0, seq.TrackCount).Select(i => seq.TrackInfo != null && i < seq.TrackInfo.Count ? seq.TrackInfo[i] : new TrackInfo()).ToList();
        Tracks.Use(seq);
        return l;
    }

    /// <summary>The active sequence gets <paramref name="video"/> + <paramref name="audio"/> tracks; row r's clips and settings move to
    /// map[r] (−1: removed with its clips). New rows start empty with default settings. (Inside an undo step.)</summary>
    void Retrack(int video, int audio, int[] map)
    {
        int n = video + audio, old = seq.TrackCount;
        var off = new bool[n];
        var ch = new TrackMix[n];
        var info = seq.TrackInfo != null ? new TrackInfo[n] : null;
        for (int r = 0; r < old; r++)
        {
            int to = r < map.Length ? map[r] : -1;
            if (to < 0) continue;
            off[to] = r < seq.TrackOff.Length && seq.TrackOff[r];
            ch[to] = r < seq.Mix.Channels.Length ? seq.Mix.Channels[r] : new TrackMix();
            if (info != null) info[to] = seq.TrackInfo![r];
        }
        int Map(int r) => r >= 0 && r < map.Length ? map[r] : -1;
        Clips.RemoveAll(c => Map(c.Track) < 0);
        foreach (var c in Clips) c.Track = Map(c.Track);
        seq.VideoTracks = video; seq.AudioTracks = audio;
        seq.TrackOff = off;
        seq.Mix.Channels = ch.Select(x => x ?? new TrackMix()).ToArray();
        seq.TrackInfo = info?.Select(x => x ?? new TrackInfo()).ToList();
        Gap = null;
        Tracks.Use(seq);
    }
    void AfterRetrack(int[] map)
    {
        TracksMoved?.Invoke(map);
        Raise(TracksChanged); Raise(MixChanged); Raise(Changed);
    }

    /// <summary>Adds tracks (one undo step): <paramref name="video"/> video tracks placed before video row <paramref name="videoAt"/>
    /// (0 = above the highest, <see cref="Tracks.Video"/> = below V1) and <paramref name="audio"/> audio tracks before row
    /// <paramref name="audioAt"/> (<see cref="Tracks.A1"/> = above A1, <see cref="Tracks.Count"/> = after the last). False when the
    /// sequence would have more than <see cref="Tracks.MaxVideo"/> / <see cref="Tracks.MaxAudio"/>.</summary>
    public bool AddTracks(int video, int videoAt, int audio, int audioAt)
    {
        int V = seq.VideoTracks, A = seq.AudioTracks;
        if (video < 0 || audio < 0 || video + audio == 0 || V + video > Tracks.MaxVideo || A + audio > Tracks.MaxAudio) return false;
        videoAt = Math.Clamp(videoAt, 0, V);
        int aRel = Math.Clamp(audioAt - V, 0, A), nV = V + video;
        var map = new int[V + A];
        for (int r = 0; r < V; r++) map[r] = r < videoAt ? r : r + video;
        for (int r = V; r < V + A; r++) { int rel = r - V; map[r] = nV + (rel < aRel ? rel : rel + audio); }
        ProjEdit(() => Retrack(nV, A + audio, map));
        AfterRetrack(map);
        return true;
    }
    /// <summary>One track above (or below) a row, of the row's kind.</summary>
    public bool AddTrack(int row, bool below = false)
    {
        bool video = row < seq.VideoTracks;
        int at = Math.Clamp(row, 0, seq.TrackCount - 1) + (below ? 1 : 0);
        return video ? AddTracks(1, at, 0, seq.VideoTracks) : AddTracks(0, 0, 1, at);
    }

    /// <summary>Deletes tracks with their clips (one undo step). At least one video and one audio track stay: false (nothing changes) otherwise.</summary>
    public bool DeleteTracks(IEnumerable<int> rows)
    {
        int V = seq.VideoTracks, A = seq.AudioTracks, n = V + A;
        var del = rows.Where(r => r >= 0 && r < n).ToHashSet();
        int dv = del.Count(r => r < V), da = del.Count - dv;
        if (del.Count == 0 || V - dv < 1 || A - da < 1) return false;
        var map = new int[n];
        for (int r = 0, k = 0; r < n; r++) map[r] = del.Contains(r) ? -1 : k++;
        ProjEdit(() => Retrack(V - dv, A - da, map));
        AfterRetrack(map);
        return true;
    }
    /// <summary>Premiere's Delete Empty Tracks (keeping one video and one audio track). The number deleted.</summary>
    public int DeleteEmptyTracks()
    {
        int V = seq.VideoTracks;
        var empty = Enumerable.Range(0, seq.TrackCount).Where(r => !Clips.Any(c => c.Track == r)).ToList();
        var ev = empty.Where(r => r < V).ToList(); var ea = empty.Where(r => r >= V).ToList();
        if (ev.Count == V) ev.RemoveAt(ev.Count - 1);                          // (keep V1: the lowest empty one)
        if (ea.Count == seq.AudioTracks) ea.RemoveAt(0);                       // (keep A1)
        var del = ev.Concat(ea).ToList();
        return del.Count > 0 && DeleteTracks(del) ? del.Count : 0;
    }

    /// <summary>Renames a track (null / empty / its label = no name of its own). One undo step.</summary>
    public void RenameTrack(int row, string? name)
    {
        if (row < 0 || row >= seq.TrackCount) return;
        string? n = string.IsNullOrWhiteSpace(name) || name.Trim() == Tracks.Label(row) ? null : name.Trim();
        if ((seq.TrackInfo is { } l ? l[row].Name : null) == n) return;
        ProjEdit(() => EnsureInfo()[row].Name = n);
        Tracks.Use(seq);
        Raise(TracksChanged);
    }
    /// <summary>Locks / unlocks a track (an instant setting, like hiding it). Locking drops its clips from the selection.</summary>
    public void SetLocked(int row, bool locked)
    {
        if (row < 0 || row >= seq.TrackCount || Locked(row) == locked) return;
        EnsureInfo()[row].Locked = locked;
        if (locked) { foreach (var c in Clips.Where(c => c.Track == row)) Sel.Remove(c.Id); if (SelectedId != null && !Sel.Contains(SelectedId)) SelectedId = Sel.FirstOrDefault(); if (Gap?.Track == row) Gap = null; }
        Raise(TracksChanged); Raise(Changed);
    }
    /// <summary>A track's height in the timeline (0 = the default). No undo, no event: the timeline redraws itself.</summary>
    public void SetTrackHeight(int row, double h)
    {
        if (row < 0 || row >= seq.TrackCount) return;
        EnsureInfo()[row].Height = h <= 0 ? 0 : Math.Clamp(h, Tracks.MinHeight, Tracks.MaxHeight);
    }
}
