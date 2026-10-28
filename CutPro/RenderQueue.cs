using System.Diagnostics;

namespace CutPro;

/// <summary>
/// The render queue (Premiere's Media Encoder queue, inside CutPro): an export added to the queue renders in the background while editing goes
/// on. Each job is a snapshot of its sequence taken when it was queued (<see cref="Nest.Take"/>: clips, mixer, masks, media, nested sequences),
/// rendered on a worker thread with that sequence's format (<see cref="SeqScope"/>) by the exporter — the same path as Export, so the file is
/// what Export would have written then. Jobs run one at a time, in order, below normal priority (playback keeps its CPU); a job can be
/// cancelled, the queue paused. Jobs live while CutPro runs.
/// </summary>
public static class RenderQueue
{
    public enum State { Queued, Rendering, Done, Failed, Cancelled }
    public sealed class Job
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "";
        public Nest.Snap Snap = null!;
        public ExportSettings Opts = null!;
        public string Output = "";
        public double? MarkIn, MarkOut;
        /// <summary>The captions as .srt next to the file (times from the export's start), when the settings ask for it.</summary>
        public string? Srt;
        public State Status;
        public double Progress;
        public string Step = "", Error = "";
        public DateTime Added = DateTime.Now; public DateTime? Started, Finished;
        internal CancellationTokenSource Cts = new();
    }

    static readonly List<Job> jobs = new();
    static readonly object gate = new();
    static Thread? worker;
    static bool paused;
    /// <summary>Something changed (a job added, started, progressed, finished): the panel redraws. Raised on the worker thread too.</summary>
    public static event Action? Changed;

    public static List<Job> Jobs { get { lock (gate) return jobs.ToList(); } }
    public static bool Paused { get => paused; set { paused = value; Changed?.Invoke(); if (!value) Kick(); } }
    public static bool Busy { get { lock (gate) return jobs.Any(j => j.Status is State.Queued or State.Rendering); } }
    public static Job? Current { get { lock (gate) return jobs.FirstOrDefault(j => j.Status == State.Rendering); } }

    /// <summary>Queues the editor's active sequence as it is now (UI thread: the snapshot reads the editor).</summary>
    public static Job Add(Editor ed, ExportSettings opts, string output)
    {
        var o = opts.Clone(); o.Background = true;
        var j = new Job
        {
            Name = ed.Seq.Name, Snap = Nest.Take(ed, ed.Seq), Opts = o, Output = output, MarkIn = ed.MarkIn, MarkOut = ed.MarkOut,
            Srt = o.CaptionFile && ed.Clips.Any(Captioning.IsCaption) ? CaptionText(ed, o) : null,
        };
        lock (gate) jobs.Add(j);
        Changed?.Invoke();
        Kick();
        return j;
    }
    static string CaptionText(Editor ed, ExportSettings o)
    {
        var (t0, t1) = Exporter.Range(ed, o);
        return Captioning.ToSrt(Captioning.InRange(Captioning.CuesOf(ed), t0, t1));
    }

    public static void Cancel(Job j)
    {
        lock (gate) { if (j.Status == State.Queued) j.Status = State.Cancelled; }
        try { j.Cts.Cancel(); } catch { }
        Changed?.Invoke();
    }
    public static void Remove(Job j) { if (j.Status is State.Queued or State.Rendering) Cancel(j); lock (gate) jobs.Remove(j); Changed?.Invoke(); }
    public static void ClearFinished() { lock (gate) jobs.RemoveAll(j => j.Status is State.Done or State.Failed or State.Cancelled); Changed?.Invoke(); }
    /// <summary>A finished, failed or cancelled job queued again (the same snapshot).</summary>
    public static void Retry(Job j)
    {
        lock (gate) { if (j.Status is State.Queued or State.Rendering) return; j.Status = State.Queued; j.Progress = 0; j.Error = ""; j.Cts = new(); j.Started = j.Finished = null; }
        Changed?.Invoke(); Kick();
    }
    /// <summary>Waits until nothing is queued or rendering (tests).</summary>
    public static bool WaitIdle(int ms) { var sw = Stopwatch.StartNew(); while (Busy && sw.ElapsedMilliseconds < ms) Thread.Sleep(50); return !Busy; }

    static void Kick()
    {
        lock (gate)
        {
            if (worker is { IsAlive: true }) return;
            worker = new Thread(Work) { IsBackground = true, Name = "Render queue", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }
    }

    static void Work()
    {
        while (true)
        {
            Job? j;
            lock (gate)
            {
                j = paused ? null : jobs.FirstOrDefault(x => x.Status == State.Queued);
                if (j == null) { worker = null; return; }
                j.Status = State.Rendering; j.Started = DateTime.Now; j.Progress = 0; j.Step = "";
            }
            Changed?.Invoke();
            string? err;
            try { err = Render(j); }
            catch (OperationCanceledException) { err = "Cancelled"; }
            catch (Exception ex) { err = ex.Message; }
            lock (gate)
            {
                j.Finished = DateTime.Now;
                j.Status = err == null ? State.Done : err == "Cancelled" || j.Cts.IsCancellationRequested ? State.Cancelled : State.Failed;
                j.Error = err == null || j.Status == State.Cancelled ? "" : err.Length > 800 ? err[^800..] : err;
                if (j.Status == State.Done) j.Progress = 1;
            }
            Changed?.Invoke();
        }
    }

    sealed class Prog(Action<double> f) : IProgress<double> { public void Report(double v) => f(v); }
    sealed class Steps(Action<string> f) : IProgress<string> { public void Report(string v) => f(v); }

    /// <summary>One job: an editor made from the snapshot on this thread, titles and masks drawn on an STA helper, the exporter run with the
    /// sequence's format in scope. Null = done.</summary>
    static string? Render(Job j)
    {
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg was not found.");
        var s = j.Snap;
        var tmp = Path.Combine(Path.GetTempPath(), "cutpro_queue_" + j.Id[..8]);
        using var scope = SeqScope.Use(s.Fmt);
        try
        {
            var e = Nest.EditorFor(s);
            e.MarkIn = j.MarkIn; e.MarkOut = j.MarkOut;
            var ct = j.Cts.Token;
            Dictionary<string, string> titles = new(), masks = new();
            if (!j.Opts.AudioOnly)
                Exporter.Sta(() => { titles = TitleVisual.PrepareExport(e, tmp, j.Opts.OutW, j.Opts.OutH); masks = MaskExport.Prepare(e, tmp, null, j.Opts.Scale); });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(j.Output))!);
            var prog = new Prog(p => { j.Progress = p; Changed?.Invoke(); });
            var steps = new Steps(t => { j.Step = t; Changed?.Invoke(); });
            var err = Task.Run(() => { using (SeqScope.Use(s.Fmt)) return Exporter.RunAsync(ff, e, j.Output, prog, ct, titles, masks, j.Opts, steps); }).GetAwaiter().GetResult();
            if (err == null && j.Srt != null) File.WriteAllText(Path.ChangeExtension(j.Output, ".srt"), j.Srt, new System.Text.UTF8Encoding(false));
            return ct.IsCancellationRequested ? "Cancelled" : err;
        }
        finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } }
    }
}
