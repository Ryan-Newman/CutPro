namespace CutPro;

/// <summary>
/// Render previews: a lossless picture of the sequence (the same export the program would write, FFV1, no sound) cached by what the
/// sequence shows. Playback can play that file instead of compositing every clip (<see cref="Preview"/>); sound stays the live mixer.
/// An edit changes the signature, so the old file is left on disk and the live composite shows until the new one is rendered. Files live in
/// %LOCALAPPDATA%\CutPro\Previews. Off unless the Sequence menu turns it on, so ordinary playback is unchanged.
/// </summary>
public static class Previews
{
    /// <summary>In the file name. Bump when the preview render stops matching a normal export of the same sequence.</summary>
    const string Version = "p1";
    const long CacheCap = 20L << 30;
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro", "Previews");

    /// <summary>Sequence ▸ Playback Uses Render Previews. While this is off, nothing is rendered and playback is the live composite.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Status-bar text while a preview renders, or a short "ready" line, or null.</summary>
    public static string? Status { get; private set; }
    public static event Action? Changed;

    static readonly object gate = new();
    static int seenGen = -1;
    static string seenId = "";
    static string sig = "";
    static double end;
    static string? file;
    static bool pending;
    static string wanted = "";
    static string failed = "";
    static long dirtyAt;
    static Nest.Snap? job;
    static Thread? worker;
    static readonly AutoResetEvent wake = new(false);

    /// <summary>A ready preview file and the time in it (the sequence time: the file starts at 0).</summary>
    public readonly record struct Hit(string File, double T);

    /// <summary>The preview covering t, or null (live composite). Queues a render when previews are on and this sequence has none yet.</summary>
    public static Hit? Picture(Editor ed, double t)
    {
        if (!Enabled) return null;
        Look(ed);
        string? ready; double len; bool busy; string miss, have;
        lock (gate) { ready = file; len = end; busy = pending; miss = failed; have = sig; }
        if (ready != null && t >= -1e-4 && t < len - 1e-4) return new Hit(ready, Math.Max(0, t));
        if (len > 0.05 && !busy && miss != have) Queue(ed, immediate: false);
        return null;
    }

    /// <summary>Timeline bar: the sequence length, whether its preview is on disk, and whether one is rendering. Empty when previews are off.</summary>
    public static (double end, bool ready, bool pending) Bar(Editor ed)
    {
        if (!Enabled) return (0, false, false);
        Look(ed);
        lock (gate) return (end, file != null, pending);
    }

    /// <summary>The export a preview is. A normal export with these settings is the same picture (the test compares them).</summary>
    public static ExportSettings Options() => new()
    {
        Name = "Render preview", Container = "nut", Video = "ffv1", Audio = "none", Full444 = true, Background = true,
    };

    /// <summary>Sequence ▸ Render Previews: turn playback over to the cache and render this sequence now.</summary>
    public static void Request(Editor ed)
    {
        Enabled = true;
        lock (gate) seenGen = -1;
        Look(ed);
        if (file != null) { Set("Render previews ready"); return; }
        Queue(ed, immediate: true);
    }

    /// <summary>Renders the sequence on this thread (tests). Null when the file is ready.</summary>
    public static string? RenderNow(Editor ed)
    {
        var snap = Nest.Take(ed, ed.Seq);
        if (snap.Frames <= 0) return "Nothing to render.";
        string s = Sig(snap);
        return Render(snap, s, CancellationToken.None);
    }

    static void Look(Editor ed)
    {
        string id = ed.Seq.Id;
        lock (gate) if (Editor.Gen == seenGen && id == seenId) return;
        Nest.Snap snap;
        try { snap = Nest.Take(ed, ed.Seq); }
        catch { lock (gate) { seenGen = Editor.Gen; seenId = id; sig = ""; end = 0; file = null; } return; }
        string s = Sig(snap);
        var path = PathOf(s);
        bool exists = File.Exists(path);
        lock (gate)
        {
            seenGen = Editor.Gen; seenId = id; sig = s; end = snap.End;
            file = exists ? path : null;
            if (exists) pending = false;
        }
    }

    static string Sig(Nest.Snap s) => Version + s.VSig;
    static string PathOf(string s) => Path.Combine(Dir, s + ".nut");

    static void Queue(Editor ed, bool immediate)
    {
        Nest.Snap snap;
        try { snap = Nest.Take(ed, ed.Seq); } catch { return; }
        if (snap.Frames <= 0) return;
        string s = Sig(snap);
        lock (gate)
        {
            if (file != null && sig == s) return;
            if (pending && wanted == s && job != null) { if (immediate) dirtyAt = 0; return; }
            job = snap; wanted = s; pending = true; failed = "";
            dirtyAt = immediate ? 0 : Environment.TickCount64;
        }
        Set("Rendering previews…");
        Start();
        wake.Set();
    }

    static void Start()
    {
        lock (gate)
        {
            if (worker != null) return;
            worker = new Thread(Loop) { IsBackground = true, Name = "Previews", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }
    }

    static void Loop()
    {
        while (true)
        {
            wake.WaitOne(300);
            Nest.Snap? snap; string s; long dirty;
            lock (gate)
            {
                if (!pending || job == null) continue;
                dirty = dirtyAt; snap = job; s = wanted;
            }
            if (dirty != 0 && Environment.TickCount64 - dirty < 400) continue;   // a drag is still moving the sequence: wait until it settles
            try
            {
                var err = Render(snap, s, CancellationToken.None);
                if (err != null) Fail(s);
            }
            catch (OperationCanceledException) { }
            catch { Fail(s); }
            lock (gate) if (wanted == s) pending = false;
        }
    }

    static string? Render(Nest.Snap snap, string s, CancellationToken ct)
    {
        var ff = Exporter.FindFfmpeg();
        if (ff == null) return "ffmpeg was not found.";
        Directory.CreateDirectory(Dir);
        var dest = PathOf(s);
        if (File.Exists(dest)) { Remember(s, dest, snap.End); return null; }
        var part = dest + "." + Guid.NewGuid().ToString("N")[..8] + ".part.nut";
        var tmp = Path.Combine(Path.GetTempPath(), "cutpro_pv_" + Guid.NewGuid().ToString("N")[..8]);
        using var scope = SeqScope.Use(snap.Fmt);
        try
        {
            var e = Nest.EditorFor(snap);
            var opts = Options();
            Dictionary<string, string> titles = new(), masks = new();
            Exporter.Sta(() =>
            {
                using (SeqScope.Use(snap.Fmt))
                {
                    titles = TitleVisual.PrepareExport(e, tmp, opts.OutW, opts.OutH);
                    masks = MaskExport.Prepare(e, tmp, null, opts.Scale);
                }
            });
            ct.ThrowIfCancellationRequested();
            var prog = new Progress<double>(p => Set($"Rendering previews… {p * 100:0}%"));
            var err = Task.Run(() =>
            {
                using (SeqScope.Use(snap.Fmt))
                    return Exporter.RunAsync(ff, e, part, prog, ct, titles, masks, opts).GetAwaiter().GetResult();
            }).GetAwaiter().GetResult();
            if (ct.IsCancellationRequested) return "Cancelled";
            if (err != null) return err.Length > 400 ? err[^400..] : err;
            if (!File.Exists(part)) return "The preview produced no file.";
            Directory.CreateDirectory(Dir);
            File.Move(part, dest, true);
            Trim();
            Remember(s, dest, snap.End);
            return null;
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            try { if (File.Exists(part)) File.Delete(part); } catch { }
        }
    }

    static void Fail(string s)
    {
        lock (gate) if (wanted == s) failed = s;
        Set("Render previews failed");
    }

    static void Remember(string s, string path, double e)
    {
        bool current;
        lock (gate)
        {
            current = s == sig;
            if (current) { file = path; end = e; pending = false; }
        }
        Set(current ? "Render previews ready" : null);
    }

    static void Set(string? s)
    {
        if (s == Status) return;
        Status = s;
        Changed?.Invoke();
    }

    static void Trim()
    {
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles("*.nut").Where(f => !f.Name.Contains(".part")).OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= CacheCap) break;
                try { f.Delete(); } catch { continue; }
                total -= f.Length;
            }
        }
        catch { }
    }
}
