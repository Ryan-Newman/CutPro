using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;

namespace CutPro;

/// <summary>
/// Premiere-style "conformed audio": each file's sound is decoded once (ffmpeg) to 48 kHz stereo s16 PCM in
/// %LOCALAPPDATA%\CutPro\AudioCache and memory-mapped, so playback mixing is sample-accurate and needs no seeking.
/// </summary>
public static class AudioCache
{
    public const int Rate = 48000;
    class Entry { public MemoryMappedViewAccessor? View; public long Frames; public volatile bool Failed; }
    static readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> derivedNames = new(StringComparer.OrdinalIgnoreCase);
    internal static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro", "AudioCache");

    static string MediaKey(string path)
    {
        var fi = new FileInfo(path);
        return $"{path.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
    }
    internal static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..20];
    internal static string CacheFile(string path) => Path.Combine(Dir, Hash(MediaKey(path)) + ".pcm");

    /// <summary>Starts conforming in the background (no-op if already done or in progress).</summary>
    public static void Ensure(MediaItem m)
    {
        if (!m.HasAudio || m.Offline) return;
        Entry e;
        lock (entries) { if (entries.ContainsKey(m.Path)) return; entries[m.Path] = e = new Entry(); }
        var path = m.Path;
        Task.Run(() => Conform(path, e, null, null));
    }

    /// <summary>The sound of a clip that plays at another speed or backwards (<see cref="TimeMap.SoundRemapped"/>): rendered once by
    /// ffmpeg from the media's source range (reverse, then atempo — or asetrate when the pitch may follow the speed) into a cached
    /// 48 kHz stereo file. Returns the key the mixer reads it by; the export reads the same file (<see cref="DerivedFile"/>).</summary>
    public static string EnsureDerived(Clip x, MediaItem m)
    {
        var (key, file, af) = Derived(x, m);
        Entry e;
        lock (entries) { if (entries.ContainsKey(key)) return key; entries[key] = e = new Entry(); }
        var path = m.Path;
        Task.Run(() => Conform(path, e, file, af));
        return key;
    }
    public static string DerivedFile(Clip x, MediaItem m) => Derived(x, m).file;
    /// <summary>The clip's sound is a rendered file: another speed, backwards, or a pitch shift.</summary>
    public static bool Derives(Clip c) => !c.Hold && (TimeMap.SoundRemapped(c) || Math.Abs(c.Pitch) > 1e-9);
    /// <summary>rubberband's pitch shifter for semitones (formants kept or shifted).</summary>
    static string PitchFilter(double semis, bool formant) =>
        FormattableString.Invariant($"rubberband=pitch={Math.Pow(2, semis / 12):0.#########}:formant={(formant ? "preserved" : "shifted")}");
    static readonly Dictionary<string, int> pitchLag = new();
    /// <summary>How many samples rubberband's pitch shifter moves the sound later (negative: earlier) for this filter — the energy centre of a short
    /// click through it vs the click (it depends on the ratio, not on the material). Cached; runs ffmpeg once per setting.</summary>
    static int PitchLag(string ff, string filter)
    {
        lock (pitchLag) if (pitchLag.TryGetValue(filter, out var known)) return known;
        int lag = 0;
        try
        {
            var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-v", "error", "-nostdin", "-f", "lavfi", "-i", "aevalsrc=exprs='if(between(t,1,1.002),0.9*sin(2*PI*1000*t),0)|if(between(t,1,1.002),0.9*sin(2*PI*1000*t),0)':s=48000:d=2",
                                      "-af", "aformat=sample_rates=48000:channel_layouts=stereo," + filter, "-ac", "1", "-f", "s16le", "pipe:1" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
            using var ms = new MemoryStream(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
            var b = ms.ToArray(); double s = 0, w = 0;
            for (int i = 0; i + 1 < b.Length; i += 2) { double v = BitConverter.ToInt16(b, i); s += v * v; w += v * v * (i / 2); }
            if (s > 0) lag = (int)Math.Round(w / s - (Rate + 0.001 * Rate));   // (the click's own centre: 1 s + 1 ms)
        }
        catch { }
        lock (pitchLag) pitchLag[filter] = lag;
        return lag;
    }
    /// <summary>Moves a rendered s16 stereo file's sound by −lag samples in place (same length: silence fills the gap).</summary>
    static void Unshift(string file, int lag)
    {
        if (lag == 0) return;
        var b = File.ReadAllBytes(file); int n = b.Length / 4;
        var o = new byte[b.Length];
        int d = Math.Clamp(lag, -n, n);
        if (d > 0) Buffer.BlockCopy(b, d * 4, o, 0, (n - d) * 4);          // (sound came late: pull it earlier)
        else Buffer.BlockCopy(b, 0, o, -d * 4, (n + d) * 4);               // (sound came early: push it later)
        File.WriteAllBytes(file, o);
    }
    static (string key, string file, string af) Derived(Clip x, MediaItem m)
    {
        var (lo, hi) = TimeMap.Span(x);
        long s0 = (long)Math.Round(lo * Rate), s1 = Math.Max(s0 + 1, (long)Math.Round(hi * Rate));
        double r = TimeMap.Ratio(x);
        // exact length before reversing (silence where the range runs past the media), so the frames stay in place
        var af = new StringBuilder($"aformat=sample_rates={Rate}:channel_layouts=stereo,atrim=start_sample={Math.Max(0, s0)}:end_sample={Math.Max(1, s1)},asetpts=PTS-STARTPTS");
        if (s0 < 0) af.Append($",adelay=delays={-s0}S:all=1");
        af.Append($",apad=whole_len={s1 - s0}");
        if (x.Reverse) af.Append(",areverse");
        if (Math.Abs(r - 1) > 1e-9)
        {
            if (x.PitchLock)
            {
                double left = r;
                while (left < 0.5) { af.Append(",atempo=0.5"); left /= 0.5; }
                while (left > 100) { af.Append(",atempo=100"); left /= 100; }
                af.Append(FormattableString.Invariant($",atempo={left:0.#########}"));
            }
            else af.Append(FormattableString.Invariant($",asetrate={Math.Round(Rate * r)},aresample={Rate}"));
        }
        if (Math.Abs(x.Pitch) > 1e-9) af.Append("," + PitchFilter(x.Pitch, x.Formant));   // (pitch shift: rubberband, its delay taken out after rendering)
        var afs = af.ToString();
        string name = m.Path + "|" + afs, file;
        lock (derivedNames)        // (the mixer asks every frame: the file name is worked out once — no disk access per frame)
        {
            if (!derivedNames.TryGetValue(name, out var known)) derivedNames[name] = known = Path.Combine(Dir, Hash(MediaKey(m.Path) + "|" + afs) + ".pcm");
            file = known;
        }
        return ("derived|" + file, file, afs);
    }

    /// <summary>Makes an already rendered 48 kHz stereo s16 file readable under a key (a nested sequence's sound). True when it is.</summary>
    public static bool Register(string key, string file)
    {
        lock (entries) if (entries.TryGetValue(key, out var have) && have.View != null) return true;
        try
        {
            long len = new FileInfo(file).Length;
            if (len < 4) return false;
            var mmf = MemoryMappedFile.CreateFromFile(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), null, 0,
                MemoryMappedFileAccess.Read, HandleInheritability.None, false);
            var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            lock (entries)
            {
                if (entries.TryGetValue(key, out var e2) && e2.View != null) { view.Dispose(); return true; }
                entries[key] = new Entry { Frames = len / 4, View = view };
            }
            return true;
        }
        catch { return false; }
    }

    public static bool IsReady(string path) { lock (entries) return entries.TryGetValue(path, out var e) && e.View != null; }
    /// <summary>Conformed, or failed (the file has no decodable sound) — nothing more will happen.</summary>
    public static bool IsDone(string path) { lock (entries) return !entries.TryGetValue(path, out var e) || e.View != null || e.Failed; }
    /// <summary>Conformed length in frames (0 while not ready).</summary>
    public static long Frames(string path) { lock (entries) return entries.TryGetValue(path, out var e) && e.View != null ? e.Frames : 0; }

    /// <summary>Blocks until the file is conformed (used by headless tests).</summary>
    public static void WaitReady(string path, int ms) { var sw = Stopwatch.StartNew(); while (!IsReady(path) && sw.ElapsedMilliseconds < ms) Thread.Sleep(20); }

    static void Conform(string path, Entry e, string? derivedFile, string? af)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var f = derivedFile ?? CacheFile(path);
            if (!File.Exists(f) || new FileInfo(f).Length == 0)
            {
                var ff = Exporter.FindFfmpeg(); if (ff == null) return;
                var tmp = f + ".part";
                var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var a in new[] { "-y", "-v", "error", "-nostdin", "-i", path, "-vn" }) psi.ArgumentList.Add(a);
                if (af != null) { psi.ArgumentList.Add("-af"); psi.ArgumentList.Add(af); }
                foreach (var a in new[] { "-ac", "2", "-ar", Rate.ToString(), "-f", "s16le", tmp }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                p.StandardError.ReadToEnd(); p.WaitForExit();
                if (p.ExitCode != 0 || !File.Exists(tmp)) return;
                if (af != null && af.IndexOf("rubberband=pitch=", StringComparison.Ordinal) is var ri and >= 0)
                {   // the pitch shifter moves the sound by a ratio-dependent amount: put it back in sync with the picture
                    int re = af.IndexOf(',', ri); var filter = re < 0 ? af[ri..] : af[ri..re];
                    Unshift(tmp, PitchLag(ff, filter));
                }
                File.Move(tmp, f, true);
            }
            long len = new FileInfo(f).Length;
            if (len < 4) return;
            var mmf = MemoryMappedFile.CreateFromFile(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read), null, 0,
                MemoryMappedFileAccess.Read, HandleInheritability.None, false);
            var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            lock (entries) { e.Frames = len / 4; e.View = view; }
        }
        catch { /* silence rather than crash */ }
        finally { if (e.View == null) e.Failed = true; }
    }

    /// <summary>Reads `frames` stereo frames starting at source frame `from` into dst (interleaved). Out-of-range = silence.</summary>
    public static bool Read(string path, long from, short[] dst, int frames)
    {
        Entry? e; lock (entries) entries.TryGetValue(path, out e);
        var v = e?.View; if (v == null) return false;
        Array.Clear(dst, 0, frames * 2);
        long a = Math.Max(0, from), b = Math.Min(e!.Frames, from + frames);
        if (b > a) v.ReadArray(a * 4, dst, (int)(a - from) * 2, (int)(b - a) * 2);
        return true;
    }
}
