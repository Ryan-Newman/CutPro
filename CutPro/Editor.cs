using System.Text.Json;
using System.Windows.Threading;

namespace CutPro;

/// <summary>A sequence format: output size (even, 16 … 16384), frame rate (exact NTSC fractions), how pictures of another shape fill the frame —
/// and its track counts (what a clip's row means: <see cref="Tracks"/>; a render thread's <see cref="SeqScope"/> carries them too).</summary>
public sealed record SeqFormat(int OutW, int OutH, double Fps, ReframeMode Reframe, int Video = Tracks.DefaultVideo, int Audio = Tracks.DefaultAudio)
{
    /// <summary>Logical canvas width (the height is always <see cref="Editor.H"/>).</summary>
    public int W => Math.Max(2, (int)Math.Round(Editor.H * OutW / (double)OutH));
    public static SeqFormat Of(int outW, int outH, double fps, ReframeMode reframe) =>
        new(Math.Clamp((int)Math.Round(outW / 2.0) * 2, 16, 16384), Math.Clamp((int)Math.Round(outH / 2.0) * 2, 16, 16384), TimeUtil.Exact(Math.Clamp(fps, 1, 240)), reframe);
}

/// <summary>Another sequence's format for the code running on this thread: a nested sequence rendering in the background reads
/// <see cref="Editor.W"/> / <see cref="Editor.OutW"/> / <see cref="Editor.OutH"/> / <see cref="Editor.Reframe"/> / <see cref="TimeUtil.Fps"/>
/// through it, so the whole export pipeline renders that sequence while the UI shows another. Per thread (never flows into UI callbacks);
/// the UI thread never sets it. Code that starts its own threads inside a render passes it on (<see cref="Exporter"/>'s STA helper).</summary>
public static class SeqScope
{
    [ThreadStatic] static SeqFormat? current;
    public static SeqFormat? Current => current;
    public static IDisposable Use(SeqFormat? f) { var prev = current; current = f; return new Restore(prev); }
    sealed class Restore(SeqFormat? prev) : IDisposable { public void Dispose() => current = prev; }
}

/// <summary>Project state + editing operations + undo/redo. UI-agnostic.</summary>
public partial class Editor
{
    /// <summary>The working canvas: always H = 720 logical lines tall, W follows the sequence aspect (1280 for 16:9, 405 for
    /// 9:16, 720 for 1:1). Preview, titles, masks, blur and positions live here; the export renders the same picture at
    /// OutW × OutH (every coordinate and radius × OutScale), so layouts look the same at any output resolution.
    /// These are the active sequence's (or the <see cref="SeqScope"/> sequence's on a thread rendering another one).</summary>
    public static int W => SeqScope.Current?.W ?? w;
    public const int H = 720;
    public static int OutW => SeqScope.Current?.OutW ?? outW;
    public static int OutH => SeqScope.Current?.OutH ?? outH;
    public static double OutScale => OutH / (double)H;
    /// <summary>How pictures of another shape fill the frame (Shorts: Fill crops the sides, FitBlur adds a blurred copy behind).</summary>
    public static ReframeMode Reframe => SeqScope.Current?.Reframe ?? reframe;
    static int w = 1280, outW = 1280, outH = 720;
    static ReframeMode reframe;
    public static event Action? SequenceChanged;
    const double Eps = 1e-4;
    static double OneFrame => 1.0 / TimeUtil.Fps;

    /// <summary>Sets the active sequence's format (static: one sequence is edited at a time). Use ApplySequence for an undoable change
    /// that also conforms clips and mask frames to a new frame rate.</summary>
    public static void SetSequence(int outW, int outH, double fps, ReframeMode reframe)
    {
        var f = SeqFormat.Of(outW, outH, fps, reframe);
        Editor.outW = f.OutW; Editor.outH = f.OutH; w = f.W; Editor.reframe = reframe;
        TimeUtil.SetFps(fps);
        Gen++;
        SequenceChanged?.Invoke();
    }

    public List<MediaItem> Media = new();
    /// <summary>Every sequence of the project. The editor edits one at a time (<see cref="Seq"/>): <see cref="Clips"/>, <see cref="Mix"/>,
    /// <see cref="TrackOff"/>, <see cref="Time"/> and the In / Out marks are that sequence's.</summary>
    public readonly List<Sequence> Sequences = new();
    Sequence seq;
    public Sequence Seq => seq;
    public List<Clip> Clips { get => seq.Clips; set => seq.Clips = value; }
    public bool[] TrackOff { get => seq.TrackOff; set => seq.TrackOff = value; }
    /// <summary>Audio Track Mixer: fader / pan / solo per track and the master fader (mute = <see cref="TrackOff"/>). See <see cref="Mixer"/>.</summary>
    public MixState Mix { get => seq.Mix; set => seq.Mix = value; }
    /// <summary>Mixer settings or a track's mute / solo changed (mixer panel, track headers, undo).</summary>
    public event Action? MixChanged;
    public void RaiseMixChanged() => Raise(MixChanged);
    /// <summary>Mixer and track mute / hide state from a project file (defaults for older projects).</summary>
    public void UseMix(MixState? mix, bool[]? off)
    {
        Mix = (mix ?? new MixState()).Fixed(seq.TrackCount);
        TrackOff = off != null && off.Length == seq.TrackCount ? off : new bool[seq.TrackCount];
        Raise(MixChanged);
    }
    public double Time { get => seq.Time; set => seq.Time = value; }

    /// <summary>Selected clip ids. SelectedId is the clicked ("primary") clip shown in Effect Controls.</summary>
    public HashSet<string> Sel = new();
    public string? SelectedId { get; set; }
    /// <summary>A selected empty gap on one track (Premiere: click a gap, press Delete to ripple it closed).</summary>
    public (int Track, double A, double B)? Gap;

    public event Action? Changed;       // structure/selection changed -> rebuild inspector, bin
    public event Action? PropsChanged;  // a property of a clip changed -> redraw only
    public event Action? Seeked;        // playhead moved by the user

    readonly Stack<Hist> undo = new(), redo = new();
    string? pending;
    readonly DispatcherTimer? debounce;

    /// <param name="timers">false: an editor for a background render (no undo debounce timer on that thread)</param>
    public Editor(bool timers = true)
    {
        seq = new Sequence { SeqW = OutW, SeqH = OutH, SeqFps = TimeUtil.Fps, Reframe = Reframe };
        if (timers) Tracks.Use(seq);                                      // (a UI editor: its sequence's tracks are the active ones; side editors keep theirs)
        Sequences.Add(seq);
        if (!timers) return;
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        debounce.Tick += (_, _) => { debounce.Stop(); Commit(); };
    }

    public Clip? Selected => Clips.FirstOrDefault(c => c.Id == SelectedId);
    public double End => Clips.Count == 0 ? 0 : Clips.Max(c => c.End);
    public MediaItem? MediaOf(Clip c) => c.MediaId == null ? null : Media.FirstOrDefault(m => m.Id == c.MediaId) ?? SeqItem(c.MediaId);
    public bool IsSel(Clip c) => Sel.Contains(c.Id);
    public IEnumerable<Clip> Linked(Clip c) => c.LinkId == null ? new[] { c } : Clips.Where(x => x.LinkId == c.LinkId);
    static string NewId() => Guid.NewGuid().ToString("N");
    /// <summary>Snap a time to the nearest frame boundary (all edits are frame-accurate, like Premiere).</summary>
    public static double Frame(double t) => Math.Round(Math.Max(0, t) * TimeUtil.Fps) / TimeUtil.Fps;
    /// <summary>Audio Time Units (Premiere's): audio clips without a linked picture edit at single samples (<see cref="Sample"/>) instead of frames.</summary>
    public static bool AudioUnits;
    public static double Sample(double t) => Math.Round(Math.Max(0, t) * AudioCache.Rate) / AudioCache.Rate;
    /// <summary>True when the clip's edits go to samples (audio time units on, an audio clip with no picture linked to it).</summary>
    public bool SampleEdit(Clip c) => AudioUnits && c.Kind == ClipKind.Audio && (c.LinkId == null || Linked(c).All(x => x.Kind == ClipKind.Audio));

    public void RaiseChanged() => Raise(Changed);
    public void RaiseProps() => Raise(PropsChanged);
    /// <summary>Counts changes to any editor (every change event bumps it): nested-sequence snapshots taken at one count are still current
    /// (<see cref="Nest"/> asks every frame).</summary>
    public static int Gen;
    void Raise(Action? ev) { Gen++; ev?.Invoke(); }
    /// <summary>Nested-sequence snapshots of this editor's sequences (<see cref="Nest"/>; locked by itself).</summary>
    internal readonly Dictionary<string, (int gen, Nest.Snap snap)> NestSnaps = new();
    /// <summary>A render's editor: the snapshots its sequences came from (they are what they show).</summary>
    internal Dictionary<string, Nest.Snap>? NestKnown;

    /// <summary>Sequences for a background render (no statics, no events: the render thread's <see cref="SeqScope"/> holds the format).</summary>
    internal void LoadForRender(List<Sequence> list, string active, Dictionary<string, Nest.Snap> known)
    {
        Sequences.Clear(); Sequences.AddRange(list);
        seq = Sequences.First(s => s.Id == active);
        NestKnown = known;
        RefreshSeqItems();
    }
    public void Seek(double t) { Time = Frame(t); Seeked?.Invoke(); }

    // ---- selection ----
    public void Select(Clip? c, bool additive = false, bool withLinks = true)
    {
        if (c != null && !Editable(c)) return;                           // (a locked track's clips can't be selected)
        Gap = null; SelTrans = null;
        if (!additive) Sel.Clear();
        if (c != null) { foreach (var x in (withLinks ? Members(c) : new[] { c }).Where(Editable)) Sel.Add(x.Id); SelectedId = c.Id; }
        else SelectedId = null;
        Raise(Changed);
    }
    public void ToggleSelect(Clip c, bool withLinks)
    {
        if (!Editable(c)) return;
        Gap = null; SelTrans = null;
        var group = (withLinks ? Members(c) : new[] { c }).Where(Editable).ToList();
        if (IsSel(c)) { foreach (var x in group) Sel.Remove(x.Id); if (SelectedId == c.Id) SelectedId = Sel.FirstOrDefault(); }
        else { foreach (var x in group) Sel.Add(x.Id); SelectedId = c.Id; }
        Raise(Changed);
    }
    public void SetSelection(IEnumerable<Clip> clips)
    {
        Gap = null; SelTrans = null; Sel = clips.SelectMany(Members).Where(Editable).Select(c => c.Id).ToHashSet();
        if (SelectedId == null || !Sel.Contains(SelectedId)) SelectedId = Sel.FirstOrDefault();
        Raise(Changed);
    }
    public void SelectAll() => SetSelection(Clips.ToList());
    public void SelectGap(int track, double a, double b) { Sel.Clear(); SelectedId = null; SelTrans = null; Gap = (track, a, b); Raise(Changed); }
    public void ClearSelection() { Sel.Clear(); SelectedId = null; Gap = null; SelTrans = null; Raise(Changed); }
    void PruneSelection()
    {
        Sel.RemoveWhere(id => !Clips.Any(c => c.Id == id));
        if (SelectedId != null && !Sel.Contains(SelectedId)) SelectedId = Sel.FirstOrDefault();
        Gap = null; SelTrans = null;
    }

    // ---- history ----
    // An entry is a snapshot of a sequence's Clips (JSON), of one mask's outlines (MaskTrack.State, shallow), for format changes
    // clips + every mask + the format, for mixer moves the levels, or — for changes to the project's sequences (new / delete / rename /
    // nest) — every sequence. Entries of one sequence carry its id: undoing one while another sequence is open switches to it first
    // (one history for the project, like Premiere).
    sealed record SeqState(int W, int H, double Fps, ReframeMode R, Dictionary<string, MaskTrack.State> Masks);
    sealed record Hist(string? Clips, string? MaskId = null, MaskTrack.State? Mask = null, SeqState? Seq = null, string? Mix = null, string? SeqId = null, string? Proj = null,
                       string? Marks = null, string? Label = null);
    sealed record ProjState(List<Sequence> Seqs, string Active);

    // ---- mixer history: fader / pan / master levels and track automation keys (mute and solo are instant toggles, like hiding a track) ----
    sealed record MixLevels(double[] G, double[] P, double M, List<Keyframe>?[]? GK = null, List<Keyframe>?[]? PK = null, List<AudioEffect>?[]? FX = null, List<Keyframe>? MK = null,
                            List<SubMix>? SB = null, int[]? OUT = null);
    string SnapMix() => JsonSerializer.Serialize(new MixLevels(Mix.Channels.Select(c => c.Gain).ToArray(), Mix.Channels.Select(c => c.Pan).ToArray(), Mix.Master,
                                                               Mix.Channels.Select(c => c.GainKeys).ToArray(), Mix.Channels.Select(c => c.PanKeys).ToArray(), Mix.Channels.Select(c => c.Fx).ToArray(), Mix.MasterKeys, Mix.Subs, Mix.Channels.Select(c => c.Output).ToArray()));
    void LoadMix(string json)
    {
        var l = JsonSerializer.Deserialize<MixLevels>(json)!;
        for (int i = 0; i < Mix.Channels.Length && i < l.G.Length; i++)
        {
            Mix.Channels[i].Gain = l.G[i]; Mix.Channels[i].Pan = l.P[i];
            Mix.Channels[i].GainKeys = l.GK != null && i < l.GK.Length ? l.GK[i] : null; Mix.Channels[i].PanKeys = l.PK != null && i < l.PK.Length ? l.PK[i] : null;
            Mix.Channels[i].Fx = l.FX != null && i < l.FX.Length ? l.FX[i] : null;
        }
        if (l.OUT != null) for (int i = 0; i < Mix.Channels.Length && i < l.OUT.Length; i++) Mix.Channels[i].Output = l.OUT[i];
        if (l.SB != null) Mix.Subs = l.SB;
        Mix.Master = l.M; Mix.MasterKeys = l.MK; Raise(MixChanged);
    }
    string? pendingMix;
    DispatcherTimer? mixDebounce;
    /// <summary>Before a fader / pan drag (one undo step until <see cref="MixEnd"/>).</summary>
    public void MixBegin() { Commit(); pendingMix ??= SnapMix(); }
    public void MixEnd()
    {
        mixDebounce?.Stop();
        if (pendingMix != null && pendingMix != SnapMix()) Push(new Hist(null, Mix: pendingMix, SeqId: seq.Id), "Audio Track Mixer");
        pendingMix = null;
    }
    /// <summary>After a single mixer change (wheel, typed value): undo entry once the changes pause.</summary>
    public void MixTouch()
    {
        MixBegin();
        if (mixDebounce == null) { mixDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) }; mixDebounce.Tick += (_, _) => MixEnd(); }
        mixDebounce.Stop(); mixDebounce.Start();
        Raise(MixChanged);
    }
    SeqState CaptureSeq() => new(OutW, OutH, TimeUtil.Fps, Reframe, MaskTracks.ToDictionary(kv => kv.Key, kv => kv.Value.Save()));

    /// <summary>Undoable change of the sequence format. A new frame rate conforms clip edges, keyframes and mask frames to
    /// its frame grid (like changing a Premiere sequence's timebase).</summary>
    public void ApplySequence(int w, int h, double fps, ReframeMode reframe)
    {
        Commit();
        var before = new Hist(Snap(), null, null, CaptureSeq(), SeqId: seq.Id);
        double old = TimeUtil.Fps;
        SetSequence(w, h, fps, reframe);
        CaptureFormat();
        if (Math.Abs(old - TimeUtil.Fps) > 1e-9) Conform(old);
        Push(before, "Sequence Settings");
        PruneSelection(); RefreshSeqItems(); Raise(Changed); Raise(MasksChanged);
    }

    void Conform(double oldFps)
    {
        double f = TimeUtil.Fps;
        double G(double t) => Math.Round(t * f) / f;
        foreach (var c in Clips)
        {
            double end = G(c.Start + c.Dur);
            c.Start = G(c.Start); if (!TimeMap.IsRemapped(c)) c.In = G(c.In); c.Dur = Math.Max(1 / f, end - c.Start);
            if (c.Anim == null) continue;
            foreach (var tr in c.Anim.Values)
            {
                foreach (var k in tr.Keys) k.T = G(k.T);
                tr.Keys.Sort((a, b) => a.T.CompareTo(b.T));
                for (int i = tr.Keys.Count - 1; i > 0; i--) if (Math.Abs(tr.Keys[i].T - tr.Keys[i - 1].T) < 0.5 / f) tr.Keys.RemoveAt(i);
            }
        }
        foreach (var tr in MaskTracks.Values)
        {
            var st = tr.Save();
            var frames = new SortedDictionary<int, List<float[]>>(); var keys = new SortedSet<int>();
            foreach (var (fr, p) in st.Frames)
            {
                int nf = (int)Math.Round(fr / oldFps * f);
                bool key = st.Keys.Contains(fr);
                if (key || !frames.ContainsKey(nf)) frames[nf] = p;
                if (key) keys.Add(nf);
            }
            tr.Load(new MaskTrack.State(frames, keys, (int)Math.Round(st.SeedFrame / oldFps * f)));
        }
    }
    string Snap() => JsonSerializer.Serialize(Clips);
    string? pendingLabel;
    /// <summary>Before a multi-step change (drag, inspector edits): one undo step until <see cref="Commit"/>. The entry's name in the History
    /// panel comes from the caller (<see cref="HistoryLabels"/>) unless given.</summary>
    public void Checkpoint([CallerMemberName] string name = "", [CallerFilePath] string file = "")
    {
        if (pending != null) return;
        pending = Snap(); pendingLabel = HistoryLabels.Of(name, file);
    }
    public void Commit()
    {
        if (pending != null && pending != Snap()) Push(new Hist(pending, SeqId: seq.Id), pendingLabel ?? "Edit");
        else if (pending != null) Settle();
        pending = null; pendingLabel = null;
    }
    /// <summary>Call after mutating a clip property from the inspector (debounced undo entry). The change was made before this is called, so the
    /// step goes back to the clips as the last history step left them (<see cref="Settle"/>) — a Snap() here would already hold the change, and a
    /// single change (a check box, a colour, a button) would not be undoable at all (Ctrl+Z then undid the step before it).</summary>
    public void Touch([CallerMemberName] string name = "", [CallerFilePath] string file = "")
    {
        if (pending == null) { pending = settled != null && settledSeq == seq.Id ? settled : Snap(); pendingLabel = HistoryLabels.Of(name, file); }
        debounce?.Stop(); debounce?.Start(); Raise(PropsChanged);
    }
    /// <summary>The clips after the last history step (an edit, undo / redo, switching or opening sequences) — <see cref="Touch"/>'s "before".</summary>
    string? settled; string? settledSeq;
    void Settle() { settled = Snap(); settledSeq = seq.Id; }

    public void Edit(Action a, [CallerMemberName] string name = "", [CallerFilePath] string file = "")
    {
        Commit();
        var before = Snap();
        a();
        if (before != Snap()) Push(new Hist(before, SeqId: seq.Id), HistoryLabels.Of(name, file));
        PruneSelection(); RefreshSeqItems();
        Raise(Changed);
    }
    void Push(Hist h, string label) { undo.Push(h with { Label = label }); redo.Clear(); Settle(); HistoryChanged?.Invoke(); }
    /// <summary>An entry was added, undone or redone (the History panel).</summary>
    public event Action? HistoryChanged;
    /// <summary>The names of the undo steps, most recent first, and of the redo steps, next first (History panel).</summary>
    public IEnumerable<string> UndoLabels => undo.Select(h => h.Label ?? "Edit");
    public IEnumerable<string> RedoLabels => redo.Select(h => h.Label ?? "Edit");
    /// <summary>History panel: undo (n &lt; 0) or redo (n &gt; 0) that many steps.</summary>
    public void StepHistory(int n)
    {
        for (; n < 0 && undo.Count > 0; n++) Undo();
        for (; n > 0 && redo.Count > 0; n--) Redo();
    }
    void Restore(string json)
    {
        Clips = JsonSerializer.Deserialize<List<Clip>>(json)!;
        PruneSelection(); RefreshSeqItems();
        Raise(Changed);
    }
    /// <summary>Raised before undo/redo (running mask tracking stops first).</summary>
    public event Action? HistoryMoving;
    public void Undo() { HistoryMoving?.Invoke(); Commit(); MixEnd(); MarkEnd(); if (undo.Count == 0) return; var h = undo.Pop(); Visit(h); redo.Push(Now(h)); Apply(h); Settle(); HistoryChanged?.Invoke(); }
    public void Redo() { HistoryMoving?.Invoke(); Commit(); MixEnd(); MarkEnd(); if (redo.Count == 0) return; var h = redo.Pop(); Visit(h); undo.Push(Now(h)); Apply(h); Settle(); HistoryChanged?.Invoke(); }
    public void ResetHistory() { undo.Clear(); redo.Clear(); pending = null; pendingMix = null; pendingMarks = null; Settle(); HistoryChanged?.Invoke(); }
    /// <summary>An entry of another sequence: open that sequence first (its state is what the entry restores).</summary>
    void Visit(Hist h) { if (h.SeqId != null && h.SeqId != seq.Id && Sequences.FirstOrDefault(s => s.Id == h.SeqId) is { } s) Activate(s); }
    Hist Now(Hist h) => (h.Proj != null ? new Hist(null, Proj: SnapProj()) : h.Mix != null ? new Hist(null, Mix: SnapMix(), SeqId: seq.Id)
                      : h.Marks != null ? new Hist(null, Marks: SnapMarks(), SeqId: seq.Id)
                      : h.Seq != null ? new Hist(Snap(), null, null, CaptureSeq(), SeqId: seq.Id)
                      : h.MaskId is { } id ? new Hist(null, id, Track(id).Save()) : new Hist(Snap(), SeqId: seq.Id)) with { Label = h.Label };
    void Apply(Hist h)
    {
        if (h.Proj != null) LoadProj(h.Proj);
        else if (h.Mix != null) LoadMix(h.Mix);
        else if (h.Marks != null) LoadMarks(h.Marks);
        else if (h.Seq is { } s)
        {
            SetSequence(s.W, s.H, s.Fps, s.R); CaptureFormat();
            foreach (var (mid, st) in s.Masks) Track(mid).Load(st);
            Raise(MasksChanged); Restore(h.Clips!);
        }
        else if (h.MaskId is { } id) { Track(id).Load(h.Mask!); Raise(MasksChanged); Raise(Changed); }
        else Restore(h.Clips!);
    }

    // ---- sequences ----
    /// <summary>The active sequence changed (timeline tabs, format, mixer, monitor follow).</summary>
    public event Action? Switched;
    /// <summary>Sequences were added, removed or renamed.</summary>
    public event Action? SequencesChanged;

    /// <summary>A sequence's format: the active one's lives in the statics (<see cref="SetSequence"/>), the others' in their fields.</summary>
    public SeqFormat FormatOf(Sequence s) => (s == seq ? new SeqFormat(OutW, OutH, TimeUtil.Fps, Reframe) : SeqFormat.Of(s.SeqW, s.SeqH, s.SeqFps, s.Reframe)) with { Video = s.VideoTracks, Audio = s.AudioTracks };
    /// <summary>Copies the active format into the active sequence's fields (before switching, saving, snapshots).</summary>
    public void CaptureFormat() { seq.SeqW = OutW; seq.SeqH = OutH; seq.SeqFps = TimeUtil.Fps; seq.Reframe = Reframe; }

    /// <summary>Opens another sequence in the timeline.</summary>
    public void Activate(Sequence s)
    {
        if (s == seq || !Sequences.Contains(s)) return;
        Commit(); MixEnd(); MaskEnd();
        CaptureFormat();
        seq = s;
        SetSequence(s.SeqW, s.SeqH, s.SeqFps, s.Reframe); Tracks.Use(s);
        Sel.Clear(); SelectedId = null; Gap = null; SelTrans = null;
        RefreshSeqItems(); Settle();
        Raise(Switched); Raise(TracksChanged); Raise(MixChanged); Raise(Changed);
    }

    /// <summary>A project's sequences (on open): the active one becomes the editor's.</summary>
    public void UseSequences(List<Sequence> list, string? active)
    {
        Sequences.Clear(); Sequences.AddRange(list);
        seq = Sequences.FirstOrDefault(s => s.Id == active) ?? Sequences[0];
        SetSequence(seq.SeqW, seq.SeqH, seq.SeqFps, seq.Reframe); Tracks.Use(seq);
        Sel.Clear(); SelectedId = null; Gap = null; SelTrans = null;
        RefreshSeqItems(); Settle();
        Raise(SequencesChanged); Raise(Switched); Raise(TracksChanged); Raise(MixChanged);
    }

    string SnapProj() { CaptureFormat(); return JsonSerializer.Serialize(new ProjState(Sequences, seq.Id)); }
    void LoadProj(string json)
    {
        var st = JsonSerializer.Deserialize<ProjState>(json)!;
        var was = seq.Id;
        Sequences.Clear(); Sequences.AddRange(st.Seqs);
        seq = Sequences.FirstOrDefault(s => s.Id == st.Active) ?? Sequences[0];
        SetSequence(seq.SeqW, seq.SeqH, seq.SeqFps, seq.Reframe); Tracks.Use(seq);
        PruneSelection(); RefreshSeqItems();
        Raise(SequencesChanged);
        if (seq.Id != was) Raise(Switched);
        Raise(TracksChanged); Raise(MixChanged); Raise(Changed);
    }
    /// <summary>One undo step for a change to the project's sequences (new / duplicate / rename / delete / nest).</summary>
    public void ProjEdit(Action a, [CallerMemberName] string name = "", [CallerFilePath] string file = "")
    {
        Commit(); MixEnd(); MaskEnd();
        var before = SnapProj();
        a();
        Push(new Hist(null, Proj: before), HistoryLabels.Of(name, file));
        PruneSelection(); RefreshSeqItems();
        Raise(SequencesChanged); Raise(Changed);
    }

    /// <summary>A name not used by another sequence ("Sequence 02", "Nested Sequence 01" …).</summary>
    public string FreeName(string stem)
    {
        for (int i = 1; ; i++) { var n = $"{stem} {i:00}"; if (!Sequences.Any(s => s.Name == n)) return n; }
    }

    /// <summary>A new empty sequence (the active one's format unless given), opened in the timeline. One undo step.</summary>
    public Sequence NewSequence(string? name = null, SeqFormat? fmt = null)
    {
        var f = fmt ?? FormatOf(seq);
        var s = new Sequence { Name = name ?? FreeName("Sequence"), SeqW = f.OutW, SeqH = f.OutH, SeqFps = f.Fps, Reframe = f.Reframe };
        ProjEdit(() => { Sequences.Add(s); Activate(s); });
        return s;
    }

    /// <summary>A copy of a sequence (clips with new ids; object masks copied, so the copy's masks can change on their own), opened. One undo step.</summary>
    public Sequence DuplicateSequence(Sequence src)
    {
        if (src == seq) CaptureFormat();
        var s = new Sequence { Name = FreeName(src.Name.TrimEnd(' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9') is { Length: > 0 } stem ? stem : "Sequence"),
                               SeqW = src.SeqW, SeqH = src.SeqH, SeqFps = src.SeqFps, Reframe = src.Reframe, Mix = src.Mix.Clone(), TrackOff = (bool[])src.TrackOff.Clone(),
                               Markers = src.Markers?.Select(m => m.Clone()).ToList(), MultiCam = src.MultiCam?.Clone(),
                               Clips = JsonSerializer.Deserialize<List<Clip>>(JsonSerializer.Serialize(src.Clips))! };
        src.CopyLayout(s);                                                // (its clips keep their rows)
        var links = new Dictionary<string, string>();
        foreach (var c in s.Clips)
        {
            c.Id = NewId();
            if (c.LinkId != null) c.LinkId = links.TryGetValue(c.LinkId, out var l) ? l : links[c.LinkId] = NewId();
            foreach (var m in c.Masks)
            {
                var nid = NewId();
                if (MaskTracks.TryGetValue(m.Id, out var tr)) { var copy = new MaskTrack(); copy.Load(tr.Save()); MaskTracks[nid] = copy; }
                m.Id = nid;
            }
        }
        ProjEdit(() => { Sequences.Insert(Sequences.IndexOf(src) + 1, s); Activate(s); });
        return s;
    }

    public void RenameSequence(Sequence s, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == s.Name) return;
        ProjEdit(() => s.Name = name);
    }

    /// <summary>Removes a sequence and every clip that uses it in the other sequences (the last sequence stays). One undo step.</summary>
    public void DeleteSequence(Sequence s)
    {
        if (Sequences.Count < 2 || !Sequences.Contains(s)) return;
        ProjEdit(() =>
        {
            if (s == seq) Activate(Sequences.First(x => x != s));
            Sequences.Remove(s);
            foreach (var o in Sequences) o.Clips.RemoveAll(c => c.MediaId == s.Id);
        });
    }

    /// <summary>Clips in other sequences that use s as a nested sequence.</summary>
    public int UsesOf(Sequence s) => Sequences.Where(o => o != s).Sum(o => o.Clips.Count(c => c.MediaId == s.Id));

    /// <summary>True when a is b or uses b, directly or through the sequences nested in it (b inside a would show a inside itself).</summary>
    public bool Contains(Sequence a, Sequence b)
    {
        var seen = new HashSet<string>();
        bool Walk(Sequence x)
        {
            if (x == b) return true;
            if (!seen.Add(x.Id)) return false;
            foreach (var id in x.Clips.Select(c => c.MediaId).Where(id => id != null).Distinct())
                if (Sequences.FirstOrDefault(o => o.Id == id) is { } inner && Walk(inner)) return true;
            return false;
        }
        return Walk(a);
    }

    // stand-ins for the sequences in MediaOf / the bin (Width / Height / Duration / sound of each sequence)
    readonly Dictionary<string, MediaItem> seqItems = new();
    /// <summary>The item that stands for a sequence (null when the id isn't one).</summary>
    public MediaItem? SeqItem(string id) => seqItems.TryGetValue(id, out var m) ? m : null;
    public IEnumerable<MediaItem> SeqItems => Sequences.Select(s => SeqItem(s.Id)).Where(m => m != null)!;
    /// <summary>The sequence a clip shows (a nested sequence), or null.</summary>
    public Sequence? SeqOf(Clip c) => c.MediaId != null && seqItems.ContainsKey(c.MediaId) ? Sequences.FirstOrDefault(s => s.Id == c.MediaId) : null;
    public Sequence? SeqById(string? id) => id == null ? null : Sequences.FirstOrDefault(s => s.Id == id);
    /// <summary>Brings the sequence stand-ins up to date (after edits, switching, undo).</summary>
    public void RefreshSeqItems()
    {
        foreach (var s in Sequences)
        {
            if (!seqItems.TryGetValue(s.Id, out var m)) seqItems[s.Id] = m = new MediaItem { Id = s.Id, Seq = s.Id, Type = ClipKind.Video, Thumb = SeqIcon };
            var f = FormatOf(s);
            m.SeqName = s.Name; m.Width = f.OutW; m.Height = f.OutH; m.Fps = f.Fps; m.Duration = s.End;
            m.HasAudio = s.Clips.Any(c => c.Kind == ClipKind.Audio || (c.Kind == ClipKind.Video && !c.NoAudio && (Media.FirstOrDefault(x => x.Id == c.MediaId)?.HasAudio ?? false)));
        }
        foreach (var id in seqItems.Keys.Where(k => !Sequences.Any(s => s.Id == k)).ToList()) seqItems.Remove(id);
    }
    /// <summary>Bin picture of a sequence (set by the UI).</summary>
    public static System.Windows.Media.ImageSource? SeqIcon;

    /// <summary>Premiere's Clip ▸ Nest: the selected clips (with their links) move into a new sequence (same format, starting at 0) and one
    /// nested clip takes their place — picture on the lowest video track they used, sound on the first audio track. A transition with a clip
    /// that stays outside is left out. Returns null (and why) when the nested clip has no free track to go on. One undo step.</summary>
    public (Sequence? seq, string? error) NestSelection(string? name = null)
    {
        var K = Clips.Where(IsSel).ToList();
        if (K.Count == 0) return (null, "Select the clips to nest first.");
        double t0 = K.Min(c => c.Start), t1 = K.Max(c => c.End);
        var kset = K.Select(c => c.Id).ToHashSet();
        var f = FormatOf(seq);
        var S = new Sequence { Name = name ?? FreeName("Nested Sequence"), SeqW = f.OutW, SeqH = f.OutH, SeqFps = f.Fps, Reframe = f.Reframe };
        seq.CopyLayout(S);                                                // (the nested clips keep their rows)
        var links = new Dictionary<string, string>();
        foreach (var c in K.OrderBy(c => c.Track).ThenBy(c => c.Start))
        {
            var x = JsonSerializer.Deserialize<Clip>(JsonSerializer.Serialize(c))!;
            x.Id = NewId(); x.Start = Frame(c.Start - t0);
            if (c.LinkId != null) x.LinkId = links.TryGetValue(c.LinkId, out var l) ? l : links[c.LinkId] = NewId();
            if (x.TransIn != null && Before(c) is { } pv && !kset.Contains(pv.Id)) x.TransIn = null;   // (it would become a fade from nothing)
            S.Clips.Add(x);
        }
        bool sound = S.Clips.Any(Mixer.HasSound);
        // where the nested clip goes: the lowest video track the clips used, else the first free one from V1 up; the sound likewise (from A1 down)
        bool Free(int tr) => !Locked(tr) && !Clips.Any(c => !kset.Contains(c.Id) && c.Track == tr && c.Start < t1 - Eps && c.End > t0 + Eps);
        var vRows = K.Where(c => c.Kind != ClipKind.Audio).Select(c => c.Track).Distinct().OrderByDescending(r => r).ToList();
        var aRows = K.Where(c => c.Kind == ClipKind.Audio).Select(c => c.Track).Distinct().OrderBy(r => r).ToList();
        int vt = vRows.Concat(Tracks.VideoRows).FirstOrDefault(Free, -1);
        int at = !sound ? -1 : aRows.Concat(Tracks.AudioRows).FirstOrDefault(Free, -1);
        if (vt < 0) return (null, "There is no free video track for the nested sequence over that time.");
        if (sound && at < 0) return (null, "There is no free audio track for the nested sequence's sound over that time.");
        var link = sound ? NewId() : null;
        var v = new Clip { MediaId = S.Id, Kind = ClipKind.Video, Track = vt, Start = t0, Dur = t1 - t0, LinkId = link, NoAudio = sound };
        var a = sound ? new Clip { MediaId = S.Id, Kind = ClipKind.Audio, Track = at, Start = t0, Dur = t1 - t0, LinkId = link } : null;
        ProjEdit(() =>
        {
            Sequences.Insert(Sequences.IndexOf(seq) + 1, S);
            Clips.RemoveAll(c => kset.Contains(c.Id));
            Clips.Add(v); if (a != null) Clips.Add(a);
            Sel.Clear(); Sel.Add(v.Id); if (a != null) Sel.Add(a.Id); SelectedId = v.Id;
        });
        return (S, null);
    }

    // ---- placement ----
    public bool Collides(int track, double start, double dur, ICollection<string>? ignore) =>
        Clips.Any(c => (ignore == null || !ignore.Contains(c.Id)) && c.Track == track && start < c.End - Eps && start + dur > c.Start + Eps);

    public double FreeStart(int track, double start, double dur)
    {
        double s = Math.Max(0, start);
        for (int guard = 0; guard < 500; guard++)
        {
            var hit = Clips.FirstOrDefault(c => c.Track == track && s < c.End - Eps && s + dur > c.Start + Eps);
            if (hit == null) return s;
            s = hit.End;
        }
        return s;
    }

    /// <summary>Adds media at a time: its source monitor In–Out range (<see cref="SourceRange"/>; all of it when unmarked). Video with sound
    /// becomes a linked video + audio clip pair (like Premiere); video / audio false = that part left out (the source monitor's drag-only handles).</summary>
    /// <summary>The first video clip went onto an empty timeline (the window offers to match the sequence to it).</summary>
    public event Action<MediaItem>? FirstVideo;
    public void AddMedia(MediaItem m, int? track, double start, bool video = true, bool audio = true)
    {
        if (!CanAdd(m)) return;
        if (MultiCamOf(m) != null)
        {   // a multi-camera source: its switchable clips, overwriting from start (on V1 / A1 unless a track is given)
            int row = track is int r && !Locked(r) ? r : Tracks.VideoRows.Concat(Tracks.AudioRows).FirstOrDefault(x => !Locked(x), -1);
            if (row >= 0) DropMedia(m, row, start, video, audio, false);
            return;
        }
        bool wasEmpty = Clips.Count == 0;
        var (s0, s1) = SourceRange(m);
        bool marked = m.Seq == null && (m.SrcIn != null || m.SrcOut != null);
        if (marked && m.Type is ClipKind.Video or ClipKind.Audio) { s0 = Math.Round(s0 * TimeUtil.Fps) / TimeUtil.Fps; s1 = Math.Round(s1 * TimeUtil.Fps) / TimeUtil.Fps; }
        double dur = marked ? Math.Max(OneFrame, Frame(s1 - s0)) : s1 - s0;
        double inPt = m.Type == ClipKind.Image ? 0 : s0;
        if (dur <= 0) return;
        start = Frame(start);
        // the track asked for (of the right kind, unlocked), else the first unlocked one from V1 up / from A1 down
        int VTrack(int? want) => want is int r && Tracks.IsVideo(r) && !Locked(r) ? r : Tracks.VideoRows.FirstOrDefault(x => !Locked(x), -1);
        int ATrack(int? want) => want is int r && !Tracks.IsVideo(r) && r < Tracks.Count && !Locked(r) ? r : Tracks.AudioRows.FirstOrDefault(x => !Locked(x), -1);
        var made = new List<Clip>();
        if (m.Type == ClipKind.Video && m.HasAudio && video && audio)
        {
            int vt = VTrack(track), at = ATrack(track);
            if (vt < 0 || at < 0) return;
            double s = start;
            for (int g = 0; g < 500; g++)
            {
                double s1f = FreeStart(vt, s, dur), s2 = FreeStart(at, s1f, dur);
                s = s2; if (Math.Abs(s2 - s1f) < Eps) break;
            }
            var link = NewId();
            made.Add(new Clip { MediaId = m.Id, Kind = ClipKind.Video, Track = vt, Start = s, Dur = dur, In = inPt, LinkId = link, NoAudio = true });
            made.Add(new Clip { MediaId = m.Id, Kind = ClipKind.Audio, Track = at, Start = s, Dur = dur, In = inPt, LinkId = link });
        }
        else if (m.Type == ClipKind.Video && m.HasAudio && !video)
        {
            int at = ATrack(track);
            if (!audio || at < 0) return;
            made.Add(new Clip { MediaId = m.Id, Kind = ClipKind.Audio, Track = at, Start = FreeStart(at, start, dur), Dur = dur, In = inPt });
        }
        else
        {
            if (m.Type == ClipKind.Audio ? !audio : !video) return;
            int tr = m.Type == ClipKind.Audio ? ATrack(track) : VTrack(track);
            if (tr < 0) return;
            made.Add(new Clip { MediaId = m.Id, Kind = m.Type, Track = tr, Dur = dur, In = inPt, Start = FreeStart(tr, start, dur), NoAudio = m.Type == ClipKind.Video && m.HasAudio });
        }
        Edit(() => Clips.AddRange(made));
        if (wasEmpty && m.Type == ClipKind.Video && made[0].Kind == ClipKind.Video) FirstVideo?.Invoke(m);
        Select(made[0]);
    }

    /// <summary>Default length of a still picture on the timeline (and its range in the source monitor).</summary>
    public const double StillDur = 5;
    /// <summary>The source range a media item puts on the timeline: its source monitor marks (whole frames), else all of it.</summary>
    public static (double a, double b) SourceRange(MediaItem m)
    {
        double len = m.Type == ClipKind.Image ? StillDur : m.Duration;
        double a = Math.Clamp(m.SrcIn ?? 0, 0, len), b = Math.Clamp(m.SrcOut ?? len, 0, len);
        return b - a >= OneFrame / 2 ? (a, b) : (0, len);
    }

    /// <summary>Source monitor → timeline at t (Premiere's Insert "," / Overwrite "."): the source's In–Out range as a video clip on vTrack and
    /// its sound on aTrack (either null = left out; linked when both). Overwrite replaces whatever is on those tracks over the range; Insert
    /// cuts every sync-locked track at t and moves those clips later by the range's length (tracks with sync lock off stay put), leaving the
    /// clips' tracks empty there. Four-point: source In and Out and sequence In and Out all set → the source range is speed-changed to fit the
    /// sequence In–Out (Fit to Fill) and lands at sequence In, not the playhead. One undo step; the playhead goes to the end of the new clip.
    /// Returns the new clips.</summary>
    public List<Clip> PlaceSource(MediaItem m, double t, int? vTrack, int? aTrack, bool insert)
    {
        var made = new List<Clip>();
        if (!CanAdd(m) || m.Seq != null) return made;
        bool pic = m.Type is ClipKind.Video or ClipKind.Image, snd = m.Type == ClipKind.Audio || m.Type == ClipKind.Video && m.HasAudio;
        int? vt = pic && vTrack is int v && Tracks.IsVideo(v) && !Locked(v) ? v : null, at = snd && aTrack is int a && !Tracks.IsVideo(a) && a < Tracks.Count && !Locked(a) ? a : null;   // (nothing goes onto a locked track)
        if (vt == null && at == null) return made;
        var (s0, s1) = SourceRange(m);
        if (m.Type is ClipKind.Video or ClipKind.Audio) { s0 = Math.Round(s0 * TimeUtil.Fps) / TimeUtil.Fps; s1 = Math.Round(s1 * TimeUtil.Fps) / TimeUtil.Fps; }
        double src = Math.Max(OneFrame, Frame(s1 - s0)), inPt = m.Type == ClipKind.Image ? 0 : s0;
        t = Frame(Math.Max(0, t));
        double dur = src, speed = 100;
        if (m.SrcIn != null && m.SrcOut != null && MarkIn is double mi && MarkOut is double mo)
        {
            double tSeq = Frame(Math.Max(0, mi)), tEnd = Frame(mo);
            if (tEnd - tSeq >= OneFrame / 2)
            {
                t = tSeq; dur = tEnd - tSeq;
                if (m.Type != ClipKind.Image && Math.Abs(dur - src) >= OneFrame / 2) speed = Math.Round(100 * src / dur, 4);
            }
        }
        var link = vt != null && at != null ? NewId() : null;
        if (vt != null) made.Add(new Clip { MediaId = m.Id, Kind = m.Type, Track = vt.Value, Start = t, Dur = dur, In = inPt, Speed = speed, PitchLock = true, LinkId = link, NoAudio = snd });   // (its sound: the linked clip, or left out)
        if (at != null) made.Add(new Clip { MediaId = m.Id, Kind = ClipKind.Audio, Track = at.Value, Start = t, Dur = dur, In = inPt, Speed = speed, PitchLock = true, LinkId = link });
        Edit(() =>
        {
            if (insert)
            {
                CutClips(Clips.Where(c => Editable(c) && SyncLocked(c.Track)).ToList(), t);
                foreach (var c in Clips.Where(c => Editable(c) && SyncLocked(c.Track) && c.Start >= t - Eps)) c.Start += dur;   // (locked / sync-unlocked tracks stay)
            }
            else
                foreach (int tr in made.Select(c => c.Track))
                {
                    CutClips(Clips.Where(c => c.Track == tr).ToList(), t + dur);
                    CutClips(Clips.Where(c => c.Track == tr).ToList(), t);
                    Clips.RemoveAll(c => c.Track == tr && c.Start >= t - Eps && c.End <= t + dur + Eps);
                }
            Clips.AddRange(made);
            Time = t + dur;
        });
        Select(made[0]);
        Seeked?.Invoke();
        return made;
    }

    /// <summary>False for a sequence that is (or contains) the open one — it would show itself.</summary>
    public bool CanAdd(MediaItem m) => m.Seq == null || SeqById(m.Seq) is not { } inner || !Contains(inner, seq);

    /// <summary>Project theme: the look of new titles, and what "apply theme" restyles every title with.</summary>
    public string ThemeName = "Modern";

    /// <summary>Adds a title of a role (Title / Subtitle / Lower third / Caption) styled and placed by the project theme.</summary>
    public void AddTitle(TextRole role = TextRole.Title)
    {
        var (x, y) = Themes.DefaultPosition(role);
        // V2 (the first above the pictures on V1), else the next one up, V1 as the last resort — unlocked
        int track = Enumerable.Range(2, Math.Max(0, Tracks.Video - 1)).Select(Tracks.V).Append(Tracks.V1).FirstOrDefault(r => !Locked(r), -1);
        if (track < 0) return;
        var c = new Clip { Kind = ClipKind.Text, Track = track, Dur = 5, Role = role, Text = Themes.DefaultText(role), X = x, Y = y,
                           Style = Themes.Get(ThemeName).For(role).Clone() };
        c.Start = FreeStart(track, Frame(Time), c.Dur);
        Edit(() => Clips.Add(c));
        Select(c);
    }

    /// <summary>An adjustment layer (no picture: its colour / blur apply to the tracks below) at the playhead on the highest video track that is
    /// free there (never V1: nothing would be below it; unlocked).</summary>
    public Clip? AddAdjustment(double dur = 5)
    {
        double start = Frame(Time);
        int track = Enumerable.Range(0, Tracks.V1).FirstOrDefault(tr => !Locked(tr) && !Collides(tr, start, dur, null), -1);
        if (track < 0) return null;
        var c = new Clip { Kind = ClipKind.Adjustment, Track = track, Start = start, Dur = dur, Text = "Adjustment layer", Grade = new ColorGrade() };
        Edit(() => Clips.Add(c));
        Select(c);
        return c;
    }

    /// <summary>Applies a text style to every selected title (one undo step).</summary>
    public void ApplyStyle(TextStyle s)
    {
        var targets = Clips.Where(c => IsSel(c) && c.Kind == ClipKind.Text).ToList();
        if (targets.Count > 0) Edit(() => { foreach (var c in targets) c.Style = s.Clone(); });
    }

    /// <summary>Switches the project theme and restyles every title by its role (one undo step).</summary>
    public void ApplyTheme(string name)
    {
        ThemeName = Themes.Get(name).Name;
        var th = Themes.Get(ThemeName);
        Edit(() => { foreach (var c in Clips.Where(c => c.Kind == ClipKind.Text)) c.Style = th.For(c.Role).Clone(); });
    }

    // ---- cutting ----
    Clip? SplitClip(Clip c, double t)
    {
        if (t <= c.Start + OneFrame / 2 || t >= c.End - OneFrame / 2) return null;
        var c2 = JsonSerializer.Deserialize<Clip>(JsonSerializer.Serialize(c))!;
        c2.Id = NewId();
        c2.Start = t; c2.In = TimeMap.Src(c, t); c2.Dur = c.End - t;          // (speed / reverse / hold: the frame shown at the cut)
        c.Dur = t - c.Start; c.FadeOut = 0; c2.FadeIn = 0;
        c.TransOut = null; c2.TransIn = null;              // the head transition stays on the left piece, the tail one on the right
        Clips.Add(c2);
        if (IsSel(c)) Sel.Add(c2.Id);
        return c2;
    }

    // ---- transitions (at edit points; stored on the clips, so they are part of undo) ----
    /// <summary>The selected transition: the clip it's stored on and whether it is that clip's head (TransIn) or tail.</summary>
    public (string ClipId, bool Head)? SelTrans;
    public Transition? SelectedTransition => SelTrans is { } s && Clips.FirstOrDefault(c => c.Id == s.ClipId) is { } c ? (s.Head ? c.TransIn : c.TransOut) : null;
    public void SelectTransition(Clip c, bool head) { Sel.Clear(); SelectedId = null; Gap = null; SelTrans = (c.Id, head); Raise(Changed); }

    /// <summary>The clip on the same track that ends exactly where c starts / starts where c ends.</summary>
    public Clip? Before(Clip c) => Clips.FirstOrDefault(x => x.Track == c.Track && x != c && Math.Abs(x.End - c.Start) < OneFrame / 2);
    public Clip? After(Clip c) => Clips.FirstOrDefault(x => x.Track == c.Track && x != c && Math.Abs(x.Start - c.End) < OneFrame / 2);

    /// <summary>Puts a transition at a clip's head or tail. The tail of a clip followed by another becomes that clip's head
    /// (one transition per edit point). Returns where it went.</summary>
    public (Clip clip, bool head) PlaceTransition(Clip c, bool head, string kind, double? dur = null, TransAlign? align = null)
    {
        if (!head && After(c) is { } nx) { c = nx; head = true; }
        var t = new Transition { Kind = kind, Dur = dur ?? Transitions.DefaultDur, Align = align ?? (head && Before(c) == null ? TransAlign.Start : !head ? TransAlign.End : TransAlign.Center) };
        if (head) { c.TransIn = t; if (Before(c) is { } pv) pv.TransOut = null; } else c.TransOut = t;
        return (c, head);
    }

    /// <summary>Premiere's Ctrl+D / Ctrl+Shift+D / Shift+D: default transitions at the selected clips' edit points, or else at the
    /// edit point nearest the playhead on each track (within 2 s).</summary>
    public int ApplyDefaultTransitions(bool video, bool audio) => ApplyTransition(null, null, null, video, audio);
    /// <summary>A named transition (or a preset: kind / duration / alignment) at the same targets as the default command.</summary>
    public int ApplyTransition(string? kind, double? dur = null, TransAlign? align = null, bool video = true, bool audio = false)
    {
        var targets = new List<(Clip c, bool head)>();
        bool Want(Clip c) => c.Kind == ClipKind.Audio ? audio : video && !(c.Kind == ClipKind.Text && c.Mask != MaskMode.None);
        var sel = Clips.Where(IsSel).Where(Want).ToList();
        if (sel.Count > 0)
            foreach (var c in sel)
            {
                targets.Add((c, true));
                if (After(c) is not { } nx || !IsSel(nx)) targets.Add((c, false));
            }
        else
            foreach (var track in Clips.Where(Want).Where(Editable).GroupBy(c => c.Track))
            {
                var points = track.SelectMany(c => new[] { (c, head: true, t: c.Start), (c, head: false, t: c.End) })
                                  .Where(p => p.head || After(p.c) == null).OrderBy(p => Math.Abs(p.t - Time)).ToList();
                if (points.Count > 0 && Math.Abs(points[0].t - Time) <= 2) targets.Add((points[0].c, points[0].head));
            }
        if (targets.Count == 0) return 0;
        Edit(() =>
        {
            foreach (var (c, head) in targets)
            {
                string k = kind ?? (c.Kind == ClipKind.Audio ? Transitions.DefaultAudio : Transitions.DefaultVideo);
                PlaceTransition(c, head, k, dur, align);
            }
        });
        return targets.Count;
    }

    /// <summary>Razor cut at t. The right-hand pieces of linked clips stay linked to each other.</summary>
    public void CutClips(IEnumerable<Clip> targets, double t)
    {
        var list = targets.ToList();
        t = list.Count > 0 && list.All(SampleEdit) ? Sample(t) : Frame(t);
        targets = list;
        var newLinks = new Dictionary<string, string>();
        foreach (var c in targets.Where(Editable).Distinct().ToList())          // (locked tracks are never cut)
        {
            var r = SplitClip(c, t);
            if (r?.LinkId == null) continue;
            if (!newLinks.TryGetValue(r.LinkId, out var nl)) newLinks[r.LinkId] = nl = NewId();
            r.LinkId = nl;
        }
    }

    public List<Clip> ClipsAt(double t) => Clips.Where(c => t > c.Start + Eps && t < c.End - Eps).ToList();

    /// <summary>Ctrl+K "Add Edit": cut selected clips under the playhead, or every track if none are selected. Ctrl+Shift+K: all tracks.</summary>
    public void AddEdit(bool allTracks)
    {
        double t = Frame(Time);
        var under = ClipsAt(t).Where(Editable).ToList();
        var sel = under.Where(IsSel).ToList();
        var targets = !allTracks && sel.Count > 0 ? sel : under;
        if (targets.Count > 0) Edit(() => CutClips(targets, t));
    }

    // ---- ripple helpers ----
    bool EmptyOn(int track, double a, double b) => !Clips.Any(c => c.Track == track && c.Start < b - Eps && c.End > a + Eps);
    bool EmptyAll(double a, double b) => !Clips.Any(c => Editable(c) && c.Start < b - Eps && c.End > a + Eps);   // (locked tracks don't take part)
    void ShiftLeft(int? track, double from, double by)
    {
        foreach (var c in Clips.Where(c => (track == null ? SyncLocked(c.Track) : c.Track == track) && Editable(c) && c.Start >= from - Eps)) c.Start = Math.Max(0, c.Start - by);   // (locked tracks stay; sync lock off: other tracks stay)
    }
    bool EmptySync(double a, double b) => !Clips.Any(c => Editable(c) && SyncLocked(c.Track) && c.Start < b - Eps && c.End > a + Eps);
    static List<(double A, double B)> Merge(IEnumerable<(double A, double B)> iv)
    {
        var res = new List<(double A, double B)>();
        foreach (var x in iv.OrderBy(i => i.A))
            if (res.Count > 0 && x.A <= res[^1].B + Eps) res[^1] = (res[^1].A, Math.Max(res[^1].B, x.B)); else res.Add(x);
        return res;
    }
    /// <summary>Closes an empty span. If nothing on a sync-locked track sits in it, those tracks move left (Premiere's sync lock);
    /// otherwise only the given tracks ripple. A track with sync lock off never moves here.</summary>
    void CloseGap(double a, double b, IEnumerable<int> tracks)
    {
        if (b - a < Eps) return;
        if (EmptySync(a, b)) { ShiftLeft(null, b, b - a); return; }
        foreach (var tr in tracks.Distinct()) if (EmptyOn(tr, a, b)) ShiftLeft(tr, b, b - a);
    }

    /// <summary>Delete (leave gap) or Ripple Delete (close gap) the selection. A selected gap is always ripple-deleted.</summary>
    public void DeleteSelection(bool ripple)
    {
        if (SelTrans is { } st && Clips.FirstOrDefault(c => c.Id == st.ClipId) is { } tc)
        {
            Edit(() => { if (st.Head) tc.TransIn = null; else tc.TransOut = null; });
            SelTrans = null; Raise(Changed); return;
        }
        if (Gap is { } g) { if (!Locked(g.Track)) Edit(() => CloseGap(g.A, g.B, new[] { g.Track })); return; }
        var dead = Clips.Where(IsSel).ToList();
        if (dead.Count == 0) return;
        Edit(() =>
        {
            foreach (var c in dead) Clips.Remove(c);
            if (!ripple) return;
            foreach (var iv in Merge(dead.Select(c => (c.Start, c.End))).OrderByDescending(i => i.A))
            {
                if (EmptySync(iv.A, iv.B)) { ShiftLeft(null, iv.B, iv.B - iv.A); continue; }
                foreach (var tr in dead.Select(c => c.Track).Distinct())
                    foreach (var sub in Merge(dead.Where(c => c.Track == tr && c.Start < iv.B && c.End > iv.A).Select(c => (c.Start, c.End))).OrderByDescending(i => i.A))
                        if (EmptyOn(tr, sub.A, sub.B)) ShiftLeft(tr, sub.B, sub.B - sub.A);
            }
        });
    }

    /// <summary>Removes a time range from every track (cutting clips at both ends). Ripple closes the gap.</summary>
    public void RemoveRange(double a, double b, bool ripple, double? newTime = null)
    {
        a = Frame(a); b = Frame(b);
        if (b - a < OneFrame / 2) return;
        Edit(() =>
        {
            CutClips(Clips.ToList(), b);
            CutClips(Clips.ToList(), a);
            Clips.RemoveAll(c => Editable(c) && c.Start >= a - Eps && c.End <= b + Eps);
            if (ripple) ShiftLeft(null, b, b - a);
            Time = newTime ?? a;
        });
        Seeked?.Invoke();
    }

    public List<double> EditPoints() => Clips.SelectMany(c => new[] { c.Start, c.End }).Append(0).Distinct().OrderBy(x => x).ToList();
    public double? PrevEdit(double t) { var l = EditPoints().Where(x => x < t - Eps).ToList(); return l.Count > 0 ? l.Max() : null; }
    public double? NextEdit(double t) { var l = EditPoints().Where(x => x > t + Eps).ToList(); return l.Count > 0 ? l.Min() : null; }

    /// <summary>Q: ripple-trim from the previous edit to the playhead. W: from the playhead to the next edit.</summary>
    public void RippleTrim(bool previous)
    {
        double t = Frame(Time);
        if (previous) { if (PrevEdit(t) is double p) RemoveRange(p, t, true, p); }
        else if (NextEdit(t) is double n) RemoveRange(t, n, true, t);
    }

    /// <summary>Ctrl+L: unlink the selection if any of it is linked, otherwise link the selected clips together.</summary>
    public void ToggleLink()
    {
        var s = Clips.Where(IsSel).ToList();
        if (s.Count == 0) return;
        Edit(() =>
        {
            if (s.Any(c => c.LinkId != null)) foreach (var c in s) c.LinkId = null;
            else if (s.Count > 1) { var id = NewId(); foreach (var c in s) c.LinkId = id; }
        });
    }

    // ---- speed / duration, reverse, frame hold (see TimeMap) ----
    /// <summary>The clips plus their linked partners (speed changes keep picture and sound together).</summary>
    public List<Clip> WithPartners(IEnumerable<Clip> cs)
    {
        var set = cs.ToList();
        var links = set.Where(c => c.LinkId != null).Select(c => c.LinkId).ToHashSet();
        foreach (var c in Clips) if (c.LinkId != null && links.Contains(c.LinkId) && !set.Contains(c) && Editable(c)) set.Add(c);   // (a partner on a locked track stays as it is)
        return set;
    }

    /// <summary>Clip Speed/Duration (Ctrl+R). A new speed keeps the clip's source range, so a faster clip gets shorter; a typed duration
    /// sets the speed that fits the same range into it (Premiere's linked speed / duration). Reverse plays the same frames backwards.
    /// Without ripple a longer clip stops at the next clip on its track (it then shows less of its source); with ripple the clips after
    /// it on its track move by the change. Speed keyframes (time remapping) keep their speed; titles and pictures only change duration.</summary>
    public void SetSpeed(IEnumerable<Clip> targets, double? speed, double? duration, bool reverse, bool pitchLock, bool ripple) =>
        Edit(() => ApplySpeed(targets, speed, duration, reverse, pitchLock, ripple));
    void ApplySpeed(IEnumerable<Clip> targets, double? speed, double? duration, bool reverse, bool pitchLock, bool ripple)
    {
        {
            var all = WithPartners(targets);
            var ids = all.Select(c => c.Id).ToHashSet();
            foreach (var c in all.OrderByDescending(c => c.Start))
            {
                double oldEnd = c.End, dur = c.Dur;
                bool timed = c.Kind is ClipKind.Video or ClipKind.Audio && !c.Hold;
                if (timed)
                {
                    if (c.Reverse != reverse) { c.In = TimeMap.FlipIn(c); c.Reverse = reverse; }
                    c.PitchLock = pitchLock;
                    if (!TimeMap.Ramped(c))
                    {
                        double r0 = TimeMap.Ratio(c);
                        var (lo, hi) = TimeMap.Span(c);
                        double s1 = Math.Clamp(duration is double d1 && d1 > 0 ? c.Speed * c.Dur / d1 : speed ?? c.Speed, TimeMap.MinSpeed, TimeMap.MaxSpeed);
                        c.Speed = Math.Round(s1, 4);
                        double r1 = TimeMap.Ratio(c);
                        dur = duration ?? c.Dur * r0 / r1;
                        if (c.Reverse) c.In = hi - r1 / TimeUtil.Fps;                 // same frames: the start shows the last one
                        else c.In = lo;
                    }
                    else if (duration is double d2) dur = d2;
                }
                else if (duration is double d3) dur = d3;
                dur = Math.Max(OneFrame, Math.Round(dur * TimeUtil.Fps) / TimeUtil.Fps);
                if (!ripple)
                {
                    var next = Clips.Where(x => x != c && x.Track == c.Track && x.Start >= oldEnd - Eps && !ids.Contains(x.Id)).OrderBy(x => x.Start).FirstOrDefault();
                    if (next != null && c.Start + dur > next.Start + Eps) dur = Math.Max(OneFrame, next.Start - c.Start);
                }
                double delta = dur - c.Dur;
                c.Dur = dur;
                if (ripple && Math.Abs(delta) > Eps)
                    foreach (var x in Clips.Where(x => x != c && x.Track == c.Track && x.Start >= oldEnd - Eps)) x.Start = Math.Max(0, x.Start + delta);
            }
        }
    }

    /// <summary>Freezes the clip on the frame at t from there to its end (Premiere's Add Frame Hold); at its start the whole clip
    /// freezes. Linked sound keeps playing.</summary>
    public void AddFrameHold(Clip c, double t)
    {
        if (c.Kind != ClipKind.Video) return;
        t = Math.Clamp(Frame(t), c.Start, c.End - OneFrame);
        Edit(() =>
        {
            double src = TimeMap.Src(c, t);
            CutClips(WithPartners(new[] { c }), t);
            var held = t > c.Start + OneFrame / 2 ? Clips.First(x => x.Track == c.Track && Math.Abs(x.Start - t) < Eps && x.MediaId == c.MediaId) : c;
            held.Hold = true; held.In = src;
        });
    }

    /// <summary>Inserts a still of the frame at t (Premiere's Insert Frame Hold Segment): the clip is cut there and everything from t
    /// on moves later by the hold's duration.</summary>
    public void InsertFrameHold(Clip c, double t, double dur = 2)
    {
        if (c.Kind != ClipKind.Video) return;
        t = Math.Clamp(Frame(t), c.Start, c.End - OneFrame);
        Edit(() =>
        {
            double src = TimeMap.Src(c, t);
            CutClips(WithPartners(new[] { c }), t);
            foreach (var x in Clips.Where(x => x.Start >= t - Eps)) x.Start += dur;
            var h = JsonSerializer.Deserialize<Clip>(JsonSerializer.Serialize(c))!;
            h.Id = NewId(); h.LinkId = null; h.Start = t; h.Dur = dur; h.In = src; h.Hold = true;
            h.FadeIn = h.FadeOut = 0; h.TransIn = h.TransOut = null; h.NoAudio = true;
            Clips.Add(h);
            Sel.Clear(); Sel.Add(h.Id); SelectedId = h.Id;
        });
    }

    /// <summary>Freeze frame on / off (Effect Controls): on = the frame at the playhead (or the clip's first frame) for the whole clip.</summary>
    public void SetHold(Clip c, bool on)
    {
        if (c.Kind != ClipKind.Video || c.Hold == on) return;
        Edit(() =>
        {
            if (on) c.In = TimeMap.Src(c, Math.Clamp(Frame(Time), c.Start, c.End - OneFrame));
            c.Hold = on;
        });
    }

    // ---- In/Out range (lift / extract) ----
    public double? MarkIn { get => seq.MarkIn; set => seq.MarkIn = value; }
    public double? MarkOut { get => seq.MarkOut; set => seq.MarkOut = value; }

    public void SetMark(bool isIn)
    {
        double t = Frame(Time);
        if (isIn) { MarkIn = t; if (MarkOut <= t) MarkOut = null; }
        else { MarkOut = t; if (MarkIn >= t) MarkIn = null; }
        Raise(PropsChanged);
    }
    public void ClearMarks() { MarkIn = MarkOut = null; Raise(PropsChanged); }
    public void LiftExtract(bool ripple)
    {
        if (MarkIn is not double a || MarkOut is not double b) return;
        MarkIn = MarkOut = null;
        RemoveRange(a, b, ripple);
    }

    // ---- object masks ----
    /// <summary>Per-frame outlines of object masks (mask id → track). Kept out of the Clips snapshots; outline changes have
    /// their own undo entries (MaskEdit / MaskBegin–MaskEnd).</summary>
    public Dictionary<string, MaskTrack> MaskTracks = new();
    /// <summary>Publish details per platform for this project (platform id → field id → value; "_" holds the chosen platforms).</summary>
    public Dictionary<string, Dictionary<string, string>> Publish = new();
    /// <summary>Raised when outlines change (selection / tracking progress / corrections) so the preview and overlay redraw.</summary>
    public event Action? MasksChanged;
    public void RaiseMasks() => Raise(MasksChanged);
    MaskTrack Track(string id) => MaskTracks.TryGetValue(id, out var t) ? t : MaskTracks[id] = new MaskTrack();

    /// <summary>Source frame (30 fps index) of a clip at timeline time t.</summary>
    public static int SourceFrame(Clip c, double t) => MaskGeometry.FrameOf(TimeMap.Src(c, t));
    /// <summary>First and last source frame the clip shows (speed / reverse included).</summary>
    public static (int f0, int f1) SourceFrames(Clip c) { var (lo, hi) = TimeMap.Span(c); int a = MaskGeometry.FrameOf(lo); return (a, Math.Max(a, MaskGeometry.FrameOf(hi) - 1)); }
    /// <summary>Timeline time where the clip shows source frame f (within the clip).</summary>
    public static double TimeOfFrame(Clip c, int f) => Math.Clamp(Math.Round(TimeMap.Out(c, f / TimeUtil.Fps) * TimeUtil.Fps) / TimeUtil.Fps, c.Start, Math.Max(c.Start, c.End - 1.0 / TimeUtil.Fps));

    public ObjectMask AddMask(Clip c, MaskKind kind, List<float[]> polys, int frame)
    {
        int n = c.Masks.Count + 1;
        var m = new ObjectMask { Kind = kind, Name = (kind == MaskKind.AI ? "Object " : kind + " ") + n };
        var tr = new MaskTrack { SeedFrame = frame };
        tr.Set(frame, polys, key: true);
        MaskTracks[m.Id] = tr;
        Edit(() => c.Masks.Add(m));
        return m;
    }

    public void RemoveMask(Clip c, string id) => Edit(() => c.Masks.RemoveAll(m => m.Id == id));

    /// <summary>One undoable change to a mask's outlines.</summary>
    public void MaskEdit(string id, Action<MaskTrack> change)
    {
        Commit();
        var tr = Track(id); var before = tr.Save(); int v = tr.Version;
        change(tr);
        if (tr.Version != v) Push(new Hist(null, id, before), "Object Mask");
        Raise(MasksChanged);
    }
    // multi-step changes (monitor drags, tracking runs) become one undo entry
    string? maskOpen; MaskTrack.State? maskBefore; int maskVersion;
    public void MaskBegin(string id) { MaskEnd(); Commit(); var tr = Track(id); maskOpen = id; maskBefore = tr.Save(); maskVersion = tr.Version; }
    public void MaskEnd()
    {
        if (maskOpen is not { } id) return;
        if (Track(id).Version != maskVersion) Push(new Hist(null, id, maskBefore), "Object Mask");
        maskOpen = null; maskBefore = null;
        Raise(MasksChanged);
    }

    /// <summary>Set the outline at one frame as a keyframe (AI refinement, redraw, correction).</summary>
    public void SetMaskFrame(string id, int frame, List<float[]> polys) => MaskEdit(id, tr => tr.Set(frame, polys, key: true));

    /// <summary>Add a keyframe with what is shown at the frame, or remove the keyframe there (its outline goes; the frame then
    /// interpolates / holds). The last outline can't be removed.</summary>
    public void ToggleKey(string id, int frame)
    {
        var tr = Track(id);
        if (tr.IsKey(frame)) { if (tr.Frames.Count > 1) MaskEdit(id, t => t.Remove(frame)); }
        else if (tr.At(frame) is { } shown) MaskEdit(id, t => t.Set(frame, shown, key: true));
    }

    /// <summary>Forget tracking: keep only the keyframes.</summary>
    public void ClearTracking(string id) => MaskEdit(id, tr => tr.ClearTracked());

    // ---- shared geometry (preview + export use the same math => WYSIWYG) ----
    /// <summary>True when a clip fills the frame (Shorts "Fill"): V1 pictures crop to cover instead of fitting.</summary>
    public static bool CoversFrame(Clip c) => Reframe == ReframeMode.Fill && c.Track == Tracks.V1 && c.Kind is ClipKind.Video or ClipKind.Image;

    public static void Fit(MediaItem? m, Clip c, out double w, out double h) => Fit(m, c.Scale, false, out w, out h, CoversFrame(c));

    /// <summary>Placed size at a given scale %. exact: fractional (animated scale — smooth); else rounded to even pixels.
    /// cover: fill the frame (crop) instead of fitting inside it.</summary>
    public static void Fit(MediaItem? m, double scale, bool exact, out double w, out double h, bool cover = false)
    {
        double mw = m?.Width > 0 ? m.Width : W, mh = m?.Height > 0 ? m.Height : H;
        double fw = (double)W / mw, fh = (double)H / mh;
        double f = (cover ? Math.Max(fw, fh) : Math.Min(fw, fh)) * Math.Max(0, scale) / 100.0;
        if (exact) { w = mw * f; h = mh * f; return; }
        w = Math.Max(2, Math.Round(mw * f / 2) * 2);
        h = Math.Max(2, Math.Round(mh * f / 2) * 2);
    }

    /// <summary>Where a visual clip sits at timeline time t (animated scale / position / rotation applied): top-left, size,
    /// rotation (degrees about its centre). Preview, monitor overlay and export share it. In Fill, Pan slides the picture
    /// across the frame: −100 shows its left edge, +100 its right edge.</summary>
    public static (double x, double y, double w, double h, double rot) Placement(MediaItem? m, Clip c, double t)
    {
        if (Pin.Active(c)) return (0, 0, W, H, 0);                       // corner pin: the clip is warped onto a frame-sized layer
        bool animScale = Anim.IsAnimated(c, AnimProp.Scale), cover = CoversFrame(c);
        Fit(m, animScale ? Anim.Value(c, AnimProp.Scale, t) : c.Scale, animScale, out double w, out double h, cover);
        double x = (W - w) / 2 + Anim.Value(c, AnimProp.X, t) * W / 100, y = (H - h) / 2 + Anim.Value(c, AnimProp.Y, t) * H / 100;
        if (cover && w > W) x -= Anim.Value(c, AnimProp.Pan, t) / 100 * (w - W) / 2;
        double rotNow = Anim.Value(c, AnimProp.Rotation, t), ax = Anim.Value(c, AnimProp.AnchorX, t), ay = Anim.Value(c, AnimProp.AnchorY, t);
        if (Math.Abs(ax) > 1e-9 || Math.Abs(ay) > 1e-9)
        {
            // anchor point: scale and rotation pivot about it (the pivot stays where it is on the picture at 100 % / 0°, so moving the anchor
            // doesn't move the clip); the layer then turns about its own centre by the same angle
            Fit(m, 100, false, out double w0, out double h0, cover);
            double ox = ax / 100 * w0, oy = ay / 100 * h0, s = w0 > 0 ? w / w0 : 1, a = rotNow * Math.PI / 180;
            double px = x + w / 2 + ox, py = y + h / 2 + oy, vx = -ox * s, vy = -oy * s;
            x = px + vx * Math.Cos(a) - vy * Math.Sin(a) - w / 2; y = py + vx * Math.Sin(a) + vy * Math.Cos(a) - h / 2;
        }
        if (LayerPlan.Needed(c))
        {
            // animated / rotated clips export on whole pixels (ffmpeg overlay + scale): the preview snaps the same way
            if (animScale) { w = Math.Max(1, Math.Round(w)); h = Math.Max(1, Math.Round(h)); }
            x = Math.Round(x); y = Math.Round(y);
        }
        else if (c.Kind is ClipKind.Video or ClipKind.Image)
        {
            // static clips: the export's 4:2:0 overlay places on even output pixels (it rounds odd ones down), so the export rounds to the
            // nearest even pixel and the preview snaps to the same grid
            double s = OutScale;
            x = 2 * Math.Round(x * s / 2) / s; y = 2 * Math.Round(y * s / 2) / s;
        }
        return (x, y, w, h, rotNow);
    }
}
