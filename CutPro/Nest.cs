using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CutPro;

/// <summary>
/// Nested sequences: a sequence used as a clip in another is rendered by the export pipeline itself — run for that sequence on a background
/// thread, where <see cref="SeqScope"/> gives it the sequence's format — into a picture with transparency (FFV1 yuva444p — yuva420p with the 4:2:0 option: lossless, every
/// frame a key frame, so seeks are instant) and its sound (the playback mixer's mix of it, 48 kHz stereo s16, read through
/// <see cref="AudioCache"/>). The preview plays the picture like a keyed clip (straight BGRA, scaled premultiplied) and the export reads the
/// same file, so the two match exactly and every clip effect works on a nested clip. Files are named by what they show (the sequence's clips,
/// format, media files, object masks and the sequences nested further in) and the frame range: an edit inside the sequence renders it again
/// (the last render plays meanwhile), a trim within the rendered range doesn't. Cached in %LOCALAPPDATA%\CutPro\Nest, trimmed above 30 GB.
/// </summary>
public static class Nest
{
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro", "Nest");
    const long CacheCap = 30L << 30;
    /// <summary>In every file name: bump it when the render pipeline changes what a sequence looks or sounds like.</summary>
    const string Version = "n4";                                         // (n4: 4:4:4 renders)
    /// <summary>Tests: false = nothing renders in the background (exports render what they need).</summary>
    public static bool Background = true;

    // ---------------- snapshots: everything a render needs, copied on the calling thread ----------------
    /// <summary>A sequence as it is now (and the sequences nested in it, any depth), with the signatures of its picture and its sound.</summary>
    public sealed class Snap
    {
        public string Id = "", Name = "";
        public SeqFormat Fmt = null!;
        public string ClipsJson = "";
        public bool[] TrackOff = Array.Empty<bool>();
        public MixState Mix = new();
        public List<MediaItem> Media = new();
        public Dictionary<string, MaskTrack.State> Masks = new();
        /// <summary>Every sequence nested in this one (any depth), by id.</summary>
        public Dictionary<string, Snap> Inner = new();
        public double End;
        public bool Sound;
        public string VSig = "", ASig = "";
        /// <summary>Frames the sequence has (its last clip's end, rounded up to a frame).</summary>
        public int Frames => End <= 0 ? 0 : (int)Math.Ceiling(End * Fmt.Fps - 1e-6);
    }

    /// <summary>Snapshot of a sequence (calling thread: the UI, or an export reading a modal editor).</summary>
    public static Snap Take(Editor ed, Sequence s) => Take(ed, s, new Dictionary<string, Snap>(), new HashSet<string>());

    static Snap Take(Editor ed, Sequence s, Dictionary<string, Snap> memo, HashSet<string> path)
    {
        if (ed.NestKnown != null && ed.NestKnown.TryGetValue(s.Id, out var known)) return known;   // (a render's editor: its own snapshot)
        if (memo.TryGetValue(s.Id, out var done)) return done;
        if (!path.Add(s.Id)) throw new InvalidOperationException("A sequence can't contain itself.");
        var sn = new Snap { Id = s.Id, Name = s.Name, Fmt = ed.FormatOf(s), ClipsJson = JsonSerializer.Serialize(s.Clips), TrackOff = (bool[])s.TrackOff.Clone(),
                            Mix = s.Mix.Clone(), Media = ed.Media.ToList(), End = s.End };
        var f = sn.Fmt;
        var v = new StringBuilder(sn.ClipsJson.Length + 512).Append(Version).Append("|v|").Append(FormattableString.Invariant($"{f.OutW}x{f.OutH}|{f.Fps:R}|{f.Reframe}|{f.Video}v{f.Audio}a|"));   // (the layout: what each clip's row means)
        var a = new StringBuilder(sn.ClipsJson.Length + 512).Append(Version).Append("|a|").Append(FormattableString.Invariant($"{f.Fps:R}|{f.Video}v{f.Audio}a|")).Append(JsonSerializer.Serialize(sn.Mix)).Append('|');
        foreach (var off in sn.TrackOff) { v.Append(off ? '1' : '0'); a.Append(off ? '1' : '0'); }
        v.Append('|').Append(sn.ClipsJson); a.Append('|').Append(sn.ClipsJson);
        foreach (var id in s.Clips.Select(c => c.MediaId).Where(id => id != null).Distinct().OrderBy(id => id, StringComparer.Ordinal))
        {
            if (ed.Media.FirstOrDefault(m => m.Id == id) is { } m)
            {
                string stamp;
                try { var fi = new FileInfo(m.Path); stamp = fi.Exists ? $"|{m.Path.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : $"|{m.Path.ToLowerInvariant()}|offline"; }
                catch { stamp = "|" + m.Path; }
                v.Append(stamp); a.Append(stamp);
            }
            else if (ed.SeqById(id) is { } inner)
            {
                var isn = Take(ed, inner, memo, path);
                sn.Inner[isn.Id] = isn;
                foreach (var (k, x) in isn.Inner) sn.Inner[k] = x;
                v.Append("|seq:").Append(isn.VSig); a.Append("|seq:").Append(isn.ASig);
            }
        }
        foreach (var mid in s.Clips.SelectMany(c => c.Masks).Select(m => m.Id).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            if (ed.MaskTracks.TryGetValue(mid, out var tr)) { var st = tr.Save(); sn.Masks[mid] = st; v.Append("|mask:").Append(mid).Append(':').Append(MaskHash(st)); }
        sn.Sound = s.Clips.Any(c => c.Kind == ClipKind.Audio || (c.Kind == ClipKind.Video && !c.NoAudio && (ed.MediaOf(c)?.HasAudio ?? false)));
        sn.VSig = Hash(v.ToString()); sn.ASig = Hash(a.ToString());
        path.Remove(s.Id); memo[s.Id] = sn;
        return sn;
    }

    static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..24];
    static string MaskHash(MaskTrack.State st)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var (fr, polys) in st.Frames)
        {
            h.AppendData(BitConverter.GetBytes(fr)); h.AppendData(BitConverter.GetBytes(st.Keys.Contains(fr)));
            foreach (var p in polys) { h.AppendData(BitConverter.GetBytes(p.Length)); h.AppendData(MemoryMarshal.AsBytes(p.AsSpan())); }
        }
        return Convert.ToHexString(h.GetHashAndReset())[..16];
    }

    /// <summary>The snapshot of a sequence for the per-frame calls (taken again only after the editor changed: <see cref="Editor.Gen"/>).</summary>
    static Snap Cached(Editor ed, Sequence s)
    {
        if (ed.NestKnown != null && ed.NestKnown.TryGetValue(s.Id, out var known)) return known;
        lock (ed.NestSnaps)
        {
            if (ed.NestSnaps.TryGetValue(s.Id, out var x) && x.gen == Editor.Gen) return x.snap;
            var sn = Take(ed, s);
            ed.NestSnaps[s.Id] = (Editor.Gen, sn);
            return sn;
        }
    }

    // ---------------- rendered files ----------------
    /// <summary>A rendered picture: frames [F0, F1) of a sequence at Fps.</summary>
    sealed record VFile(string Sig, int F0, int F1, double Fps, string File, string SeqId);
    /// <summary>A nested clip's picture for the exporter: the file and the sequence time at its first frame.</summary>
    public sealed record Picture(string File, double Offset);

    static string VideoFile(string sig, int f0, int f1) => Path.Combine(Dir, $"{sig}_{f0}_{f1}.nut");
    static string AudioFile(string sig) => Path.Combine(Dir, sig + ".pcm");
    /// <summary>The key the mixer (and <see cref="AudioCache"/>) reads a sequence's sound by.</summary>
    static string SoundKey(string file) => "nest|" + file;
    /// <summary>True for a file this class rendered (the preview scales its pictures premultiplied).</summary>
    public static bool IsRender(string path) => path.StartsWith(Dir, StringComparison.OrdinalIgnoreCase);

    static readonly object gate = new();
    static Dictionary<string, List<VFile>>? videos;            // picture signature → rendered files (scanned from the folder once)
    static HashSet<string>? sounds;                             // sound signatures with a file
    static readonly Dictionary<string, List<VFile>> lastOf = new();   // sequence id → this session's renders (played while a new one renders)
    static readonly Dictionary<string, string> lastSound = new();     // sequence id → this session's last sound key

    static void Index()
    {
        if (videos != null) return;
        videos = new(); sounds = new();
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in Directory.EnumerateFiles(Dir))
            {
                var n = Path.GetFileName(f);
                if (n.Contains(".part")) { try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f); } catch { } continue; }   // (left by a crash; recent ones may be another CutPro's)
                if (n.EndsWith(".pcm", StringComparison.OrdinalIgnoreCase)) { sounds.Add(n[..^4]); continue; }
                if (!n.EndsWith(".nut", StringComparison.OrdinalIgnoreCase)) continue;
                var p = n[..^4].Split('_');
                if (p.Length == 3 && int.TryParse(p[1], out int f0) && int.TryParse(p[2], out int f1))
                    (videos.TryGetValue(p[0], out var l) ? l : videos[p[0]] = new()).Add(new VFile(p[0], f0, f1, 0, f, ""));
            }
        }
        catch { }
    }
    static VFile? Covering(string sig, int f0, int f1)
    {
        Index();
        return videos!.TryGetValue(sig, out var l) ? l.Where(v => v.F0 <= f0 && v.F1 >= f1).OrderBy(v => v.F1 - v.F0).FirstOrDefault() : null;
    }

    /// <summary>The sequence frames a nested clip shows over its on-screen range [rs, re) (transitions included; nested clips play at 100 %).
    /// Past the sequence's end: its last frame (held, like media).</summary>
    static (int f0, int f1)? FramesOf(Snap s, Clip c, double rs, double re)
    {
        int total = s.Frames;
        if (total <= 0) return null;
        double fps = s.Fmt.Fps, a = c.In + (rs - c.Start), b = c.In + (re - c.Start);
        int f0 = Math.Clamp((int)Math.Floor(a * fps + 1e-6), 0, total), f1 = Math.Clamp((int)Math.Ceiling(b * fps - 1e-6) + 1, 0, total);   // (+1: the rate conversion may look one ahead)
        if (f0 >= total) return (total - 1, total);
        return f1 > f0 ? (f0, f1) : null;
    }

    /// <summary>THE frame rule of a nested clip, shared by the preview and the export: the render's frame (counted from its first) shown at the
    /// clip's frame k (k = 0 at its start). The clip begins on the render frame at or after its In; from there the export's fps filter converts
    /// the sequence's rate to the outer one — output frame k shows the last input frame j whose time, rounded to the outer grid, is ≤ k, i.e.
    /// j = ⌈(k + ½)·rIn / rOut⌉ − 1 (one to one at equal rates). The renders carry exact frame times (NUT), the export trims by frame number.</summary>
    public static long FrameAt(Clip c, double offset, double rIn, double rOut, long k)
    {
        long j = Math.Abs(rIn - rOut) < 1e-9 ? k : (long)Math.Ceiling((k + 0.5) * rIn / rOut - 1e-9) - 1;
        return FirstFrame(c, offset, rIn) + Math.Max(0, j);
    }
    /// <summary>The render frame a nested clip starts on: the first at or after its In.</summary>
    public static long FirstFrame(Clip c, double offset, double rIn) => Math.Max(0, (long)Math.Ceiling((c.In - offset) * rIn - 1e-6));

    // ---------------- jobs ----------------
    sealed class Job
    {
        public Snap S = null!;
        public bool Audio;
        public int F0, F1;
        public volatile int Status;          // 0 waiting, 1 rendering, 2 done, 3 failed
        public long Wanted;                  // last time (ms) the preview asked for it
        public int Export;                   // exports waiting for it (never cancelled then)
        public double Progress;
        public string? Error;
    }
    static readonly List<Job> jobs = new();
    static Thread? worker;
    static readonly AutoResetEvent wake = new(false);

    /// <summary>"Rendering nested sequence “Intro”… 40%" while something renders (null when idle); <see cref="StatusChanged"/> on change.</summary>
    public static string? Status { get; private set; }
    /// <summary>The last render finished (false: it failed or was dropped) — the status bar says "rendered" only then.</summary>
    public static bool Rendered { get; private set; }
    static string? lastError;
    public static event Action? StatusChanged;
    static void SetStatus(string? s) { if (s == Status) return; Status = s; StatusChanged?.Invoke(); }

    /// <summary>Queues (or finds) the render of frames [f0, f1) of s, grown to whole 2-second blocks so small trims stay inside it. Under gate.</summary>
    static Job WantVideo(Snap s, int f0, int f1)
    {
        int q = Math.Max(1, (int)Math.Round(s.Fmt.Fps * 2)), total = s.Frames;
        int q0 = f0 / q * q, q1 = Math.Min(total, (f1 + q - 1) / q * q);
        var j = jobs.FirstOrDefault(x => !x.Audio && x.S.VSig == s.VSig && x.Status != 2 && x.F0 <= f0 && x.F1 >= f1)
             ?? jobs.FirstOrDefault(x => !x.Audio && x.S.VSig == s.VSig && x.Status == 0 && x.F0 <= q1 + q && x.F1 >= q0 - q);
        if (j == null) { j = new Job { S = s, F0 = q0, F1 = q1 }; jobs.Add(j); }
        else if (j.Status == 0) { j.F0 = Math.Min(j.F0, q0); j.F1 = Math.Max(j.F1, q1); }
        j.Wanted = Environment.TickCount64;
        return j;
    }
    static Job WantAudio(Snap s)
    {
        var j = jobs.FirstOrDefault(x => x.Audio && x.S.ASig == s.ASig && x.Status != 2);
        if (j == null) { j = new Job { S = s, Audio = true }; jobs.Add(j); }
        j.Wanted = Environment.TickCount64;
        return j;
    }
    static void Kick() { if (!Background) return; StartWorker(); wake.Set(); }

    // ---------------- per-frame calls (UI thread) ----------------
    /// <summary>The picture of nested clip c over its on-screen range [rs, re): file, sequence time at its first frame, its length — or null while it
    /// renders (queued now). Meanwhile the sequence's last render plays if it covers the clip (stale = true). Cheap enough per frame.</summary>
    public static (string file, double offset, double dur, bool stale)? Ready(Editor ed, Clip c, double rs, double re)
    {
        if (ed.SeqOf(c) is not { } seq) return null;
        var s = Cached(ed, seq);
        if (FramesOf(s, c, rs, re) is not var (f0, f1)) return null;
        lock (gate)
        {
            if (Covering(s.VSig, f0, f1) is { } hit)
            {
                var last = lastOf.TryGetValue(seq.Id, out var known) ? known : lastOf[seq.Id] = new();   // (remembered: plays while a newer render is made, frame grabs read it)
                if (!last.Any(v => v.File == hit.File)) { last.Add(hit with { Fps = s.Fmt.Fps, SeqId = seq.Id }); if (last.Count > 8) last.RemoveAt(0); }
                return (hit.File, hit.F0 / s.Fmt.Fps, (hit.F1 - hit.F0) / s.Fmt.Fps, false);
            }
            var j = WantVideo(s, f0, f1);
            if (j.Status == 0) Kick();
            if (lastOf.TryGetValue(seq.Id, out var l) && l.LastOrDefault(v => v.F0 / v.Fps <= f0 / s.Fmt.Fps + 1e-6 && v.F1 / v.Fps >= f1 / s.Fmt.Fps - 1e-6) is { } old)
                return (old.File, old.F0 / old.Fps, (old.F1 - old.F0) / old.Fps, true);
            return null;
        }
    }

    /// <summary>The mixer key of a nested sequence's sound, or null while it renders (queued now; the last render plays meanwhile).</summary>
    public static string? Sound(Editor ed, Sequence seq)
    {
        var s = Cached(ed, seq);
        if (!s.Sound) return null;
        var file = AudioFile(s.ASig); var key = SoundKey(file);
        if (AudioCache.IsReady(key)) return key;
        lock (gate)
        {
            Index();
            if (sounds!.Contains(s.ASig) && AudioCache.Register(key, file)) { lastSound[seq.Id] = key; return key; }
            var j = WantAudio(s);
            if (j.Status == 0) Kick();
            return lastSound.TryGetValue(seq.Id, out var old) && AudioCache.IsReady(old) ? old : null;
        }
    }
    /// <summary>Where to read one frame of a media item at source time t: the media itself, or — for a nested sequence — its latest render that
    /// covers t (file, time in it, true: it has alpha). Null while the sequence has no render there (the preview queues one when it shows the clip).</summary>
    public static (string path, double t, bool alpha)? SourceOf(MediaItem m, double t)
    {
        if (m.Seq == null) return (m.Path, t, false);
        lock (gate)
            if (lastOf.TryGetValue(m.Seq, out var l))
                for (int i = l.Count - 1; i >= 0; i--)
                {
                    var v = l[i];
                    if (t >= v.F0 / v.Fps - 1e-6 && t < v.F1 / v.Fps + 0.5 / v.Fps && File.Exists(v.File)) return (v.File, Math.Max(0, t - v.F0 / v.Fps), true);
                }
        return null;
    }

    /// <summary>The file behind a mixer key from <see cref="Sound"/> (the export reads it).</summary>
    public static string? FileOfKey(string key) => key.StartsWith("nest|") ? key[5..] : null;

    // ---------------- export ----------------
    /// <summary>Renders what the export of ed needs from its nested sequences (waits for the background renderer, or — on the render thread
    /// itself, for sequences nested further in — renders them here): pictures (clip id → file + offset) and sounds. Throws when one fails.</summary>
    public static Dictionary<string, Picture> PrepareExport(Editor ed, bool video, bool audio, CancellationToken ct, IProgress<double>? progress = null)
    {
        var res = new Dictionary<string, Picture>();
        bool here = Thread.CurrentThread == worker;
        var plan = Transitions.Compute(ed);
        var memo = new Dictionary<string, Snap>();
        var waits = new List<(Clip? c, Job j, int f0, int f1)>();
        try
        {
            foreach (var c in ed.Clips)
            {
                if (ed.TrackOff[c.Track] || ed.SeqOf(c) is not { } seq) continue;
                var s = Take(ed, seq, memo, new HashSet<string>());
                if (video && c.Kind == ClipKind.Video)
                {
                    var (rs, re) = plan.Range(c);
                    if (FramesOf(s, c, rs, re) is var (f0, f1))
                    {
                        VFile? hit; lock (gate) hit = Covering(s.VSig, f0, f1);
                        if (hit == null && here) hit = RenderNow(s, f0, f1, ct);
                        if (hit != null) res[c.Id] = new Picture(hit.File, hit.F0 / s.Fmt.Fps);
                        else lock (gate) { var j = WantVideo(s, f0, f1); j.Export++; waits.Add((c, j, f0, f1)); }
                    }
                }
                if (audio && Mixer.HasSound(c) && s.Sound)
                {
                    bool ready; lock (gate) { Index(); ready = sounds!.Contains(s.ASig); }
                    if (!ready && here) { var j = new Job { S = s, Audio = true, Status = 1 }; RenderAudio(j, ct); lock (gate) sounds!.Add(s.ASig); ready = true; }
                    if (ready) AudioCache.Register(SoundKey(AudioFile(s.ASig)), AudioFile(s.ASig));
                    else lock (gate) { var j = WantAudio(s); j.Export++; waits.Add((null, j, 0, 0)); }
                }
            }
            if (waits.Count > 0) { StartWorker(); wake.Set(); }
            while (waits.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (waits.All(w => w.j.Status is 2 or 3)) break;
                    progress?.Report(waits.Average(w => w.j.Status == 2 ? 1 : w.j.Status == 1 ? w.j.Progress : 0));
                }
                Thread.Sleep(50);
            }
            foreach (var (c, j, f0, f1) in waits)
            {
                if (j.Status == 3) throw new Exception($"Could not render the nested sequence “{j.S.Name}”: {j.Error}");
                if (c == null) { AudioCache.Register(SoundKey(AudioFile(j.S.ASig)), AudioFile(j.S.ASig)); continue; }
                VFile? hit; lock (gate) hit = Covering(j.S.VSig, f0, f1);
                if (hit == null) throw new Exception($"Could not render the nested sequence “{j.S.Name}”.");
                res[c.Id] = new Picture(hit.File, hit.F0 / j.S.Fmt.Fps);
            }
            return res;
        }
        finally { lock (gate) foreach (var w in waits) w.j.Export--; }
    }

    /// <summary>Renders frames [f0, f1) of s on this thread (a sequence nested in the one being rendered).</summary>
    static VFile? RenderNow(Snap s, int f0, int f1, CancellationToken ct)
    {
        Job j; lock (gate) { j = WantVideo(s, f0, f1); if (j.Status == 0) j.Status = 1; else j = new Job { S = s, F0 = j.F0, F1 = j.F1, Status = 1 }; j.Export++; }
        try { Run(j, ct); } finally { lock (gate) j.Export--; }
        lock (gate) return Covering(s.VSig, f0, f1);
    }

    // ---------------- the worker ----------------
    static void StartWorker()
    {
        lock (gate)
        {
            if (worker != null) return;
            worker = new Thread(Loop) { IsBackground = true, Name = "Nest", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }
    }

    static void Loop()
    {
        while (true)
        {
            wake.WaitOne(500);
            while (true)
            {
                Job? j;
                lock (gate)
                {
                    long now = Environment.TickCount64;
                    jobs.RemoveAll(x => x.Status == 2 || (x.Status == 3 && now - x.Wanted > 10000 && x.Export == 0));   // (failed: retried when wanted again later)
                    j = jobs.Where(x => x.Status == 0 && (x.Export > 0 || now - x.Wanted < 5000))
                            .OrderByDescending(x => x.Export > 0).ThenByDescending(x => x.Audio).ThenByDescending(x => x.Wanted).FirstOrDefault();
                    if (j == null) break;
                    j.Status = 1;
                }
                Run(j, CancellationToken.None);
            }
            SetStatus(lastError);                                         // (idle: a failure stays in the status bar until a render succeeds)
        }
    }

    static void Run(Job j, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        try
        {
            SetStatus($"Rendering nested sequence “{j.S.Name}”…");
            if (j.Audio) RenderAudio(j, cts.Token);
            else RenderVideo(j, cts, outer);
            lock (gate)
            {
                Index();
                if (j.Audio)
                {
                    sounds!.Add(j.S.ASig);
                    var key = SoundKey(AudioFile(j.S.ASig));
                    if (AudioCache.Register(key, AudioFile(j.S.ASig))) lastSound[j.S.Id] = key;
                }
                else
                {
                    var v = new VFile(j.S.VSig, j.F0, j.F1, j.S.Fmt.Fps, VideoFile(j.S.VSig, j.F0, j.F1), j.S.Id);
                    (videos!.TryGetValue(v.Sig, out var l) ? l : videos[v.Sig] = new()).Add(v);
                    var last = lastOf.TryGetValue(v.SeqId, out var ll) ? ll : lastOf[v.SeqId] = new();
                    last.Add(v); if (last.Count > 8) last.RemoveAt(0);
                }
                j.Status = 2; Rendered = true; lastError = null;
            }
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested) { lock (gate) { j.Status = 0; Rendered = false; } }   // no longer wanted: queued again if it is
        catch (OperationCanceledException) { lock (gate) { j.Status = 0; Rendered = false; } throw; }
        catch (Exception ex)
        {
            lock (gate) { j.Status = 3; j.Error = ex.Message; Rendered = false; lastError = $"Nested sequence “{j.S.Name}” could not be rendered: {ex.Message.Split('\n')[0].Trim()}"; }
        }
        finally { Trim(); }
    }

    /// <summary>An editor for rendering a snapshot (render queue jobs; the caller sets the sequence's <see cref="SeqScope"/>).</summary>
    public static Editor EditorFor(Snap s) => MakeEditor(s);
    /// <summary>An editor for rendering a snapshot (this thread; the caller has set the sequence's <see cref="SeqScope"/>).</summary>
    static Editor MakeEditor(Snap s)
    {
        static Sequence Seq(Snap x) => new()
        {
            Id = x.Id, Name = x.Name, Clips = JsonSerializer.Deserialize<List<Clip>>(x.ClipsJson)!, SeqW = x.Fmt.OutW, SeqH = x.Fmt.OutH, SeqFps = x.Fmt.Fps,
            Reframe = x.Fmt.Reframe, Mix = x.Mix.Clone(), TrackOff = (bool[])x.TrackOff.Clone(), VideoTracks = x.Fmt.Video, AudioTracks = x.Fmt.Audio,
        };
        var e = new Editor(timers: false) { Media = s.Media.ToList() };
        var known = new Dictionary<string, Snap>(s.Inner) { [s.Id] = s };
        e.LoadForRender(new[] { s }.Concat(s.Inner.Values).Select(Seq).ToList(), s.Id, known);
        foreach (var (id, st) in s.Masks) { var tr = new MaskTrack(); tr.Load(st); e.MaskTracks[id] = tr; }
        return e;
    }

    sealed class Prog(Action<double> report) : IProgress<double> { public void Report(double v) => report(v); }

    /// <summary>Renders the picture of frames [F0, F1) of the job's sequence with the exporter (transparent background, FFV1 yuva444p, no sound).</summary>
    static void RenderVideo(Job j, CancellationTokenSource cts, CancellationToken outer)
    {
        var s = j.S;
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg was not found.");
        Directory.CreateDirectory(Dir);
        var file = VideoFile(s.VSig, j.F0, j.F1); var part = file + "." + Guid.NewGuid().ToString("N")[..8] + ".part.nut";
        if (File.Exists(file)) return;
        var tmp = Path.Combine(Path.GetTempPath(), "cutpro_nest_" + Guid.NewGuid().ToString("N")[..8]);
        using var scope = SeqScope.Use(s.Fmt);
        try
        {
            var e = MakeEditor(s);
            var ct = cts.Token;
            var inner = PrepareExport(e, video: true, audio: false, ct);   // sequences nested further in: rendered here first
            double fps = s.Fmt.Fps, t0 = j.F0 / fps, t1 = j.F1 / fps;
            // clips that show nowhere in the range and take part in no transition change nothing there: leave them out (the export composites
            // from the timeline's start, so a range late in a long sequence would otherwise process everything before it)
            var plan = Transitions.Compute(e);
            var inWin = plan.Wins.SelectMany(w => new[] { w.A?.Id, w.B?.Id }).Where(id => id != null).ToHashSet();
            e.Clips.RemoveAll(c => !inWin.Contains(c.Id) && (plan.Range(c).e <= t0 + 1e-9 || plan.Range(c).s >= t1 - 1e-9));
            e.MarkIn = t0; e.MarkOut = t1;
            var opts = new ExportSettings { Name = "Nested sequence", Container = "nut", Video = "ffv1", Alpha = true, Audio = "none", InOutOnly = true, Background = j.Export == 0,
                                            Height = s.Fmt.OutH > 2160 ? 2160 : 0 };
            Dictionary<string, string> titles = new(), masks = new();
            Exporter.Sta(() => { titles = TitleVisual.PrepareExport(e, tmp, opts.OutW, opts.OutH); masks = MaskExport.Prepare(e, tmp, null, opts.Scale); });
            var prog = new Prog(p =>
            {
                j.Progress = p;
                SetStatus($"Rendering nested sequence “{s.Name}”… {p * 100:0}%");
                if (j.Export == 0 && Environment.TickCount64 - j.Wanted > 8000) { try { cts.Cancel(); } catch { } }   // nobody wants it any more
            });
            var err = Task.Run(() => { using (SeqScope.Use(s.Fmt)) return Exporter.RunAsync(ff, e, part, prog, ct, titles, masks, opts, null, inner); }).GetAwaiter().GetResult();
            if (err == "Cancelled" || ct.IsCancellationRequested) throw new OperationCanceledException();
            if (err != null) throw new Exception(err.Length > 600 ? err[^600..] : err);
            if (!File.Exists(part)) throw new Exception("The render produced no file.");
            File.Move(part, file, true);
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            try { if (File.Exists(part)) File.Delete(part); } catch { }
        }
    }

    /// <summary>Renders the sequence's sound from its start with the playback mixer (48 kHz stereo s16, the format <see cref="AudioCache"/> reads).</summary>
    static void RenderAudio(Job j, CancellationToken ct)
    {
        var s = j.S;
        Directory.CreateDirectory(Dir);
        var file = AudioFile(s.ASig); var part = file + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
        if (File.Exists(file)) return;
        using var scope = SeqScope.Use(s.Fmt);
        try
        {
            var e = MakeEditor(s);
            PrepareExport(e, video: false, audio: true, ct);                // sounds of the sequences nested further in
            var srcs = Mixer.Sources(e, Transitions.Compute(e), new()).ToArray();
            foreach (var p in srcs.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                while (!AudioCache.IsDone(p)) { ct.ThrowIfCancellationRequested(); Thread.Sleep(20); }
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var bytes = new byte[Mixer.Rate / 10 * 4];
                double total = Math.Max(1e-6, s.End), done = 0;
                Mixer.Render(srcs, Mixer.MasterOf(e), 0, s.End, (buf, n) =>
                {
                    for (int k = 0; k < n * 2; k++)
                    {
                        int v = (int)Math.Clamp(Math.Round(buf[k]), -32768, 32767);
                        bytes[k * 2] = (byte)v; bytes[k * 2 + 1] = (byte)(v >> 8);
                    }
                    fs.Write(bytes, 0, n * 4);
                    done += n / (double)Mixer.Rate; j.Progress = Math.Min(1, done / total);
                }, ct);
            }
            File.Move(part, file, true);
        }
        finally { try { if (File.Exists(part)) File.Delete(part); } catch { } }
    }

    /// <summary>Keeps the folder under 30 GB (oldest files go first; never one in use by a running job).</summary>
    static void Trim()
    {
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles().Where(f => !f.Name.Contains(".part")).OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= CacheCap) break;
                try { f.Delete(); } catch { continue; }
                total -= f.Length;
                lock (gate)
                {
                    if (videos != null) foreach (var l in videos.Values) l.RemoveAll(v => string.Equals(v.File, f.FullName, StringComparison.OrdinalIgnoreCase));
                    if (f.Name.EndsWith(".pcm")) sounds?.Remove(f.Name[..^4]);
                    foreach (var l in lastOf.Values) l.RemoveAll(v => string.Equals(v.File, f.FullName, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        catch { }
    }
}
