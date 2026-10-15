namespace CutPro;

/// <summary>One track's channel in the Audio Track Mixer.</summary>
public sealed class TrackMix
{
    /// <summary>Fader in dB (≤ <see cref="Mixer.MinDb"/> = −∞, silent).</summary>
    public double Gain { get; set; }
    /// <summary>Balance −100 (left) … 0 … 100 (right).</summary>
    public double Pan { get; set; }
    public bool Solo { get; set; }
    /// <summary>Automation: fader keyframes (T = timeline seconds, V = dB) and balance keyframes; none = <see cref="Gain"/> / <see cref="Pan"/>.
    /// Before the first / after the last key the value holds.</summary>
    public List<Keyframe>? GainKeys { get; set; }
    public List<Keyframe>? PanKeys { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool Automated => GainKeys is { Count: > 0 } || PanKeys is { Count: > 0 };
    public double GainAt(double t) => GainKeys is { Count: > 0 } k ? Math.Clamp(Curve.Eval(k, t), Mixer.MinDb, Mixer.MaxDb) : Gain;
    public double PanAt(double t) => PanKeys is { Count: > 0 } k ? Math.Clamp(Curve.Eval(k, t), -100, 100) : Pan;
    /// <summary>Track effects (pre-fader, AudioDsp — reverb rings on past the clips).</summary>
    public List<AudioEffect>? Fx { get; set; }
    /// <summary>Where the track goes: −1 = the master, else a submix (<see cref="MixState.Subs"/> index).</summary>
    public int Output { get; set; } = -1;
    public TrackMix Clone() => new() { Gain = Gain, Pan = Pan, Solo = Solo, Output = Output, GainKeys = GainKeys?.Select(k => k.Clone()).ToList(), PanKeys = PanKeys?.Select(k => k.Clone()).ToList(), Fx = Fx?.Select(e => e.Clone()).ToList() };
}

/// <summary>A submix bus in the Audio Track Mixer: the tracks routed to it are summed, run through its effects, then its fader / pan /
/// automation, into the master.</summary>
public sealed class SubMix
{
    public string Name { get; set; } = "Submix 1";
    public double Gain { get; set; }
    public double Pan { get; set; }
    public bool Mute { get; set; }
    public List<Keyframe>? GainKeys { get; set; }
    public List<Keyframe>? PanKeys { get; set; }
    public List<AudioEffect>? Fx { get; set; }
    public SubMix Clone() => new() { Name = Name, Gain = Gain, Pan = Pan, Mute = Mute, GainKeys = GainKeys?.Select(k => k.Clone()).ToList(), PanKeys = PanKeys?.Select(k => k.Clone()).ToList(), Fx = Fx?.Select(e => e.Clone()).ToList() };
    /// <summary>Its fader, balance and automation as a track channel (the same evaluation and tables).</summary>
    public TrackMix AsTrack() => new() { Gain = Gain, Pan = Pan, GainKeys = GainKeys, PanKeys = PanKeys, Fx = Fx };
}

/// <summary>Mixer settings of a sequence (saved with the project; fader / pan changes are undoable). Mute is <see cref="Editor.TrackOff"/>.</summary>
public sealed class MixState
{
    public TrackMix[] Channels { get; set; } = Enumerable.Range(0, Tracks.Names.Length).Select(_ => new TrackMix()).ToArray();
    /// <summary>Master (Mix) fader in dB.</summary>
    public double Master { get; set; }
    /// <summary>Master automation: fader keyframes (timeline seconds, dB); none = <see cref="Master"/>.</summary>
    public List<Keyframe>? MasterKeys { get; set; }
    public double MasterAt(double t) => MasterKeys is { Count: > 0 } k ? Math.Clamp(Curve.Eval(k, t), Mixer.MinDb, Mixer.MasterMaxDb) : Master;
    /// <summary>Submix buses (tracks routed to one are summed, through its effects and fader, into the master).</summary>
    public List<SubMix> Subs { get; set; } = new();
    public MixState Clone() => new() { Channels = Channels.Select(c => c.Clone()).ToArray(), Master = Master, MasterKeys = MasterKeys?.Select(k => k.Clone()).ToList(), Subs = Subs.Select(s => s.Clone()).ToList() };
    /// <summary>Pads / trims the channel list to the track count (older or hand-edited project files).</summary>
    public MixState Fixed()
    {
        if (Channels == null || Channels.Length != Tracks.Names.Length)
            Channels = Enumerable.Range(0, Tracks.Names.Length).Select(i => Channels != null && i < Channels.Length && Channels[i] != null ? Channels[i] : new TrackMix()).ToArray();
        return this;
    }
}

/// <summary>
/// The timeline mix: which clips sound, at what gain, through which track channel. <see cref="Mix"/> is the ONE mixer — playback
/// (<see cref="AudioOut"/>), meters and loudness measurement (<see cref="Render"/>) all run it; <see cref="Exporter"/> builds the
/// same math as ffmpeg filters (track gain + pan per clip, master after amix) and <c>--audiotest</c> compares them sample by sample.
/// </summary>
public static class Mixer
{
    public const double MinDb = -96, MaxDb = 6, MasterMaxDb = 6;
    public const int Rate = AudioCache.Rate;
    /// <summary>Meter slots: L/R per track, then master L/R.</summary>
    public const int MaxSubs = 4;
    /// <summary>Meter slots: L/R per track, then L/R per submix (<see cref="MaxSubs"/>), then master L/R.</summary>
    public static int Slots => Tracks.Names.Length * 2 + MaxSubs * 2 + 2;
    public static int SubSlot(int k) => Tracks.Names.Length * 2 + k * 2;

    public static double Lin(double db) => db <= MinDb ? 0 : Math.Pow(10, db / 20);
    public static double Db(double lin) => lin <= 0 ? double.NegativeInfinity : 20 * Math.Log10(lin);

    /// <summary>Balance law: the far side drops along a quarter sine (−3 dB at half way), the near side stays at unity.</summary>
    public static (double l, double r) PanGains(double pan)
    {
        double p = Math.Clamp(pan / 100, -1, 1);
        return (p > 0 ? Math.Cos(p * Math.PI / 2) : 1, p < 0 ? Math.Cos(-p * Math.PI / 2) : 1);
    }

    public static bool AnySolo(Editor ed) => ed.Mix.Channels.Any(c => c.Solo);
    /// <summary>Track is heard: not muted, and soloed when any track is soloed (video tracks carry the sound of old unlinked clips).</summary>
    public static bool Audible(Editor ed, int track) => !ed.TrackOff[track] && (!AnySolo(ed) || ed.Mix.Channels[track].Solo);
    public static (float l, float r) TrackGains(Editor ed, int track)
    {
        var ch = ed.Mix.Channels[track];
        double g = Lin(ch.Gain); var (pl, pr) = PanGains(ch.Pan);
        return ((float)(g * pl), (float)(g * pr));
    }
    public static float MasterGain(Editor ed) => (float)Lin(ed.Mix.Master);
    /// <summary>The master as the mixer applies it: one gain, or (automated) a gain per <see cref="AutoBlock"/> block of the timeline.</summary>
    public static MasterMix MasterOf(Editor ed)
    {
        var mx = ed.Mix;
        if (mx.MasterKeys is not { Count: > 0 } keys) return new MasterMix(MasterGain(ed));
        double end = ed.End;
        var ch = new TrackMix { Gain = mx.Master, GainKeys = keys };                  // (the same signature as a track's)
        string sig = AutoSig(ch, end), key = ed.Seq.Id + "|master";
        lock (autoCache)
        {
            if (autoCache.TryGetValue(key, out var a) && a.sig == sig) return new MasterMix(MasterGain(ed), a.l);
            int n = (int)Math.Ceiling(Math.Max(0, end) / AutoStep) + 2;
            var t = new float[n];
            for (int j = 0; j < n; j++) t[j] = (float)Lin(mx.MasterAt(j * AutoStep));
            autoCache[key] = (sig, t, t);
            return new MasterMix(MasterGain(ed), t);
        }
    }

    /// <summary>Automation step: one gain per 240-sample block of the timeline (block j = samples [240·j, 240·j + 240), its value at j·step) —
    /// the playback mixer and the export (asetnsamples + asendcmd on the track's sum) use the same blocks.</summary>
    public const double AutoStep = AudioOut.GainStep;
    public const int AutoBlock = 240;
    /// <summary>A track's automation as L / R gains (fader × balance) per <see cref="AutoStep"/> block up to end (+ a little past it).</summary>
    public static (float[] l, float[] r) AutoTable(TrackMix ch, double end)
    {
        int n = (int)Math.Ceiling(Math.Max(0, end) / AutoStep) + 2;
        var l = new float[n]; var r = new float[n];
        for (int j = 0; j < n; j++)
        {
            double t = j * AutoStep, g = Lin(ch.GainAt(t)); var (pl, pr) = PanGains(ch.PanAt(t));
            l[j] = (float)(g * pl); r[j] = (float)(g * pr);
        }
        return (l, r);
    }
    static readonly Dictionary<string, (string sig, float[] l, float[] r)> autoCache = new();
    /// <summary>A submix as the mixer sees it (shared by the sources of the tracks routed to it): index, a frozen copy of its effects, its fader /
    /// pan gains or automation tables.</summary>
    public sealed record SubBus(int Index, IReadOnlyList<AudioEffect>? Fx, string Key, float GL, float GR, float[]? AL, float[]? AR)
    {
        /// <summary>History run before a jump (its effects): long enough for a reverb's tail.</summary>
        public int WarmFrames => Fx == null ? 0 : Fx.Any(e => e.Kind == "Reverb") ? AudioCache.Rate * 4 : AudioCache.Rate / 2;
    }
    public static SubBus SubOf(Editor ed, int k)
    {
        var s = ed.Mix.Subs[k]; var t = s.AsTrack();
        double g = Lin(s.Gain); var (pl, pr) = PanGains(s.Pan);
        (float[] l, float[] r)? auto = null;
        if (t.Automated)
        {
            double end = ed.End; string sig = AutoSig(t, end), key = ed.Seq.Id + "|sub" + k;
            lock (autoCache)
            {
                if (autoCache.TryGetValue(key, out var a) && a.sig == sig) auto = (a.l, a.r);
                else { var (al, ar) = AutoTable(t, end); autoCache[key] = (sig, al, ar); auto = (al, ar); }
            }
        }
        bool fx = s.Fx?.Any(e => e.On) == true;
        string fk = "sub" + k + "|" + AudioDsp.Sig(s.Fx);
        return new SubBus(k, fx ? AudioDsp.Snapshot(fk, s.Fx!) : null, fk, auto != null ? 1 : (float)(g * pl), auto != null ? 1 : (float)(g * pr), auto?.l, auto?.r);
    }
    [ThreadStatic] static float[][]? subBufs;
    static float[] SubBuf(int k, int n)
    {
        subBufs ??= new float[MaxSubs][];
        if (subBufs[k] == null || subBufs[k].Length < n * 2) subBufs[k] = new float[n * 2];
        return subBufs[k];
    }
    /// <summary>A submix's input over [f0, f0 + n): its tracks' own mix (faders, effects, automation; master 1) — warm-ups and the export's file.</summary>
    public static void SubInput(AudioOut.Src[] srcs, int k, long f0, int n, float[] outBuf)
    {
        var mine = srcs.Where(s => s.Sub?.Index == k).Select(s => s with { Sub = null }).ToArray();
        long done = 0;
        Render(mine, new MasterMix(1), f0 / (double)Rate, (f0 + n) / (double)Rate, (b, m) => { int c = (int)Math.Min(m, n - done); Array.Copy(b, 0, outBuf, done * 2, c * 2); done += c; });
    }

    /// <summary>The track's effect bus (null without effects): a frozen copy of its chain, its fader / pan gains or automation tables.</summary>
    public static AudioDsp.TrackBus? BusOf(Editor ed, int track, float gl, float gr, (float[] l, float[] r)? auto)
    {
        var ch = ed.Mix.Channels[track];
        if (!AudioDsp.Active(ch)) return null;
        string key = "track" + track + "|" + AudioDsp.Sig(ch.Fx);
        return new AudioDsp.TrackBus(track, AudioDsp.Snapshot(key, ch.Fx!), key, auto != null ? 1 : gl, auto != null ? 1 : gr, auto?.l, auto?.r);
    }
    /// <summary>64-bit FNV-1a over everything the tables depend on.</summary>
    static string AutoSig(TrackMix ch, double end)
    {
        ulong h = 14695981039346656037;
        void Add(double v) { ulong b = (ulong)BitConverter.DoubleToInt64Bits(v); for (int i = 0; i < 8; i++) { h ^= (b >> (i * 8)) & 0xFF; h *= 1099511628211; } }
        Add(ch.Gain); Add(ch.Pan); Add(end);
        foreach (var keys in new[] { ch.GainKeys, ch.PanKeys })
        {
            Add(keys?.Count ?? -1);
            if (keys != null) foreach (var k in keys) { Add(k.T); Add(k.V); Add((int)k.In * 8 + (int)k.Out); Add(k.InSlope); Add(k.InInfl); Add(k.OutSlope); Add(k.OutInfl); }
        }
        return h.ToString("x16");
    }
    /// <summary>The track's tables (cached by its keys, values and length; any thread — nested renders mix in the background).</summary>
    public static (float[] l, float[] r)? Automation(Editor ed, int track)
    {
        var ch = ed.Mix.Channels[track];
        if (!ch.Automated) return null;
        double end = ed.End;
        string sig = AutoSig(ch, end);                                      // (cheap: Sources runs every frame)
        string key = ed.Seq.Id + "|" + track;
        lock (autoCache)
        {
            if (autoCache.TryGetValue(key, out var a) && a.sig == sig) return (a.l, a.r);
            var (l, r) = AutoTable(ch, end); autoCache[key] = (sig, l, r);
            return (l, r);
        }
    }
    public static bool HasSound(Clip c) => c.Kind == ClipKind.Audio || (c.Kind == ClipKind.Video && !c.NoAudio);

    /// <summary>Every clip that sounds, as mixer sources (volume / fades / crossfades / track channel). Starts conforming their audio.</summary>
    public static List<AudioOut.Src> Sources(Editor ed, Transitions.Plan plan, Dictionary<string, (string sig, float[] table)> gainCache)
    {
        var list = new List<AudioOut.Src>();
        bool solo = AnySolo(ed);
        foreach (var c in ed.Clips)
        {
            if (!HasSound(c) || ed.TrackOff[c.Track] || (solo && !ed.Mix.Channels[c.Track].Solo)) continue;
            if (ed.MediaOf(c) is not { Offline: false, HasAudio: true } m || c.Hold) continue;     // (a frozen frame is silent)
            // a nested sequence: its rendered mix (silent until it is rendered — Nest queues it)
            string? nestKey = null;
            if (m.Seq != null) { if (ed.SeqById(m.Seq) is not { } ns || (nestKey = Nest.Sound(ed, ns)) == null) continue; }
            else AudioCache.Ensure(m);
            // audio transitions lengthen the clip over the crossfade (video transitions leave the sound alone)
            var h = c.Kind == ClipKind.Audio && plan.HeadOf(c) is { D.Fx: Transitions.Fx.Audio } hw ? hw : null;
            var tl = c.Kind == ClipKind.Audio && plan.TailOf(c) is { D.Fx: Transitions.Fx.Audio } tw ? tw : null;
            var x = h != null || tl != null ? Transitions.Extended(new[] { c }, plan)[0] : c;
            float[]? gain = null;
            if (Anim.IsAnimated(c, AnimProp.Volume))
            {
                var sig = System.Text.Json.JsonSerializer.Serialize(x);
                if (!gainCache.TryGetValue(c.Id, out var gc) || gc.sig != sig) gainCache[c.Id] = gc = (sig, Anim.GainTable(x));
                gain = gc.table;
            }
            var (gl, gr) = TrackGains(ed, c.Track);
            var auto = Automation(ed, c.Track);                              // (automated: gains per block of the timeline instead)
            var tbus = BusOf(ed, c.Track, gl, gr, auto);                       // (track effects: summed before the fader, which the bus applies)
            int route = ed.Mix.Channels[c.Track].Output;
            var sub = route >= 0 && route < ed.Mix.Subs.Count ? SubOf(ed, route) : null;   // (routed to a submix)
            if (sub != null && ed.Mix.Subs[route].Mute) continue;
            // another speed / reversed: its own rendered sound, which starts with the clip (the export reads the same file)
            bool derived = nestKey == null && AudioCache.Derives(x);
            bool fx = AudioDsp.Active(c);
            list.Add(new AudioOut.Src(nestKey ?? (derived ? AudioCache.EnsureDerived(x, m) : m.Path), x.Start, x.End, derived ? 0 : x.In, c.Volume / 100, x.FadeIn, x.FadeOut, gain,
                                      h != null ? (h.T0, h.T1, h.D.Power) : null, tl != null ? (tl.T0, tl.T1, tl.D.Power) : null, c.Track, tbus != null ? 1 : gl, tbus != null ? 1 : gr,
                                      fx ? AudioDsp.Snapshot(c.Id + "|" + AudioDsp.Sig(c.AudioFx), c.AudioFx!) : null, fx ? c.Id + "|" + AudioDsp.Sig(c.AudioFx) : null,
                                      tbus != null ? null : auto?.l, tbus != null ? null : auto?.r, tbus, sub));
        }
        return list;
    }

    /// <summary>Scratch buffers for <see cref="Mix"/> (one per track, n stereo frames).</summary>
    public static float[][] Bus(int n) => Enumerable.Range(0, Tracks.Names.Length).Select(_ => new float[n * 2]).ToArray();

    static bool Overlaps(AudioOut.Src s, long b0, int n) => Math.Min((long)Math.Round(s.End * Rate), b0 + n) > Math.Max((long)Math.Round(s.Start * Rate), b0);

    /// <summary>Adds one source's samples in [b0, b0 + n) to a track bus: its clip effects, gain table / volume, fades, crossfades, × its GL / GR
    /// (or automation tables) — 1 on a track with effects, whose fader comes after them.</summary>
    static void Accumulate(AudioOut.Src s, long b0, int n, float[] tb, short[] tmp, ref AudioDsp.FxState? fx)
    {
        long sS = (long)Math.Round(s.Start * Rate), sE = (long)Math.Round(s.End * Rate);
        long lo = Math.Max(sS, b0), hi = Math.Min(sE, b0 + n);
        if (hi <= lo) return;
        int o0 = (int)(lo - b0), m = (int)(hi - lo);
        long srcFrame = (long)Math.Round(s.In * Rate) + (lo - sS);
        if (!AudioCache.Read(s.Path, srcFrame, tmp, m)) return;
        if (s.Fx != null) (fx ??= new AudioDsp.FxState()).Process(s, srcFrame, tmp, m);    // clip audio effects (the export reads the same, rendered)
        for (int k = 0; k < m; k++)
        {
            double t = (b0 + o0 + k) / (double)Rate, g = s.Gain is { } gt ? gt[Math.Clamp((int)((t - s.Start) / AudioOut.GainStep), 0, gt.Length - 1)] : s.Volume;
            if (s.FadeIn > 0) g *= Math.Clamp((t - s.Start) / s.FadeIn, 0, 1);
            if (s.FadeOut > 0) g *= Math.Clamp((s.End - t) / s.FadeOut, 0, 1);
            if (s.XIn is { } xi && t < xi.t1) g *= Transitions.Curve(Math.Clamp((t - xi.t0) / (xi.t1 - xi.t0), 0, 1), xi.power, fadeIn: true);
            if (s.XOut is { } xo && t >= xo.t0) g *= Transitions.Curve(Math.Clamp((t - xo.t0) / (xo.t1 - xo.t0), 0, 1), xo.power, fadeIn: false);
            float gl = s.GL, gr = s.GR;
            if (s.AL != null) { int j = (int)Math.Clamp((b0 + o0 + k) / AutoBlock, 0, s.AL.Length - 1); gl = s.AL[j]; gr = s.AR![j]; }
            tb[(o0 + k) * 2] += (float)(tmp[k * 2] * (g * gl));
            tb[(o0 + k) * 2 + 1] += (float)(tmp[k * 2 + 1] * (g * gr));
        }
    }

    /// <summary>A track's clips summed before its fader over [f0, f0 + n) (warm-ups and the export's track file; clipFx: their effect state).</summary>
    public static void PreFader(AudioOut.Src[] srcs, int track, long f0, int n, float[] outBuf, AudioDsp.FxState? clipFx = null, short[]? tmp = null)
    {
        Array.Clear(outBuf, 0, n * 2);
        tmp ??= new short[n * 2];
        clipFx ??= new AudioDsp.FxState();
        foreach (var s in srcs) if (s.Track == track && s.Bus != null) Accumulate(s, f0, n, outBuf, tmp, ref clipFx);
    }

    /// <summary>Mixes timeline time [T0, T0 + n/Rate) into <paramref name="mix"/> (interleaved stereo, ±32768 = full scale): each source with its
    /// clip gain, fades and crossfades × its track's fader and pan, summed per track, then × master. A track with effects sums its clips before
    /// the fader, runs the chain on that bus (also where no clip sounds: a reverb's tail), then applies the fader / pan. <paramref name="peaks"/>
    /// (optional, <see cref="Slots"/> long) gets each track's post-fader L/R peak and the master L/R peak (1 = 0 dBFS).</summary>
    public static void Mix(AudioOut.Src[] srcs, MasterMix master, double T0, int n, float[] mix, float[][] bus, short[] tmp, float[]? peaks, AudioDsp.FxState? fx = null)
    {
        int used = 0, fxDone = 0;
        // whole samples throughout (like the export: atrim start_sample / adelay in samples) — NTSC frame times fall between samples
        long b0 = (long)Math.Round(T0 * Rate);
        foreach (var s in srcs)
        {
            var tb = bus[s.Track];
            if (!Overlaps(s, b0, n)) continue;
            if ((used & (1 << s.Track)) == 0) { Array.Clear(tb, 0, n * 2); used |= 1 << s.Track; }
            Accumulate(s, b0, n, tb, tmp, ref fx);
        }
        foreach (var s in srcs)
        {
            if (s.Bus is not { } tbus || (fxDone & (1 << tbus.Track)) != 0) continue;
            fxDone |= 1 << tbus.Track;
            var tb = bus[tbus.Track];
            if ((used & (1 << tbus.Track)) == 0) { Array.Clear(tb, 0, n * 2); used |= 1 << tbus.Track; }
            (fx ??= new AudioDsp.FxState()).ProcessBus(tbus, b0, tb, n, srcs);
            for (int k = 0; k < n; k++)
            {
                float gl = tbus.GL, gr = tbus.GR;
                if (tbus.AL != null) { int j = (int)Math.Clamp((b0 + k) / AutoBlock, 0, tbus.AL.Length - 1); gl = tbus.AL[j]; gr = tbus.AR![j]; }
                tb[k * 2] *= gl; tb[k * 2 + 1] *= gr;
            }
        }
        Array.Clear(mix, 0, n * 2);
        if (peaks != null) Array.Clear(peaks);
        // where each track goes: the master, or a submix (all of a track's sources carry the same routing)
        Span<int> route = stackalloc int[bus.Length]; route.Fill(-1);
        SubBus?[]? subs = null;
        foreach (var s in srcs) if (s.Sub is { } sb) { route[s.Track] = sb.Index; (subs ??= new SubBus?[MaxSubs])[sb.Index] = sb; }
        int subUsed = 0;
        for (int tr = 0; tr < bus.Length; tr++)
        {
            if ((used & (1 << tr)) == 0) continue;
            var tb = bus[tr]; float pl = 0, pr = 0;
            float[] dst = mix;
            if (route[tr] >= 0) { int r0 = route[tr]; dst = SubBuf(r0, n); if ((subUsed & (1 << r0)) == 0) { Array.Clear(dst, 0, n * 2); subUsed |= 1 << r0; } }
            for (int k = 0; k < n * 2; k += 2)
            {
                float l = tb[k], r = tb[k + 1];
                dst[k] += l; dst[k + 1] += r;
                if (l > pl) pl = l; else if (-l > pl) pl = -l;
                if (r > pr) pr = r; else if (-r > pr) pr = -r;
            }
            if (peaks != null) { peaks[tr * 2] = pl / 32768f; peaks[tr * 2 + 1] = pr / 32768f; }
        }
        // submixes: their effects (also where no track sounds — tails), then their fader / pan / automation, into the mix
        if (subs != null)
            for (int k = 0; k < MaxSubs; k++)
            {
                if (subs[k] is not { } sb) continue;
                var sbuf = SubBuf(k, n);
                if ((subUsed & (1 << k)) == 0) { if (sb.Fx == null) continue; Array.Clear(sbuf, 0, n * 2); }
                if (sb.Fx != null) (fx ??= new AudioDsp.FxState()).ProcessSub(sb, b0, sbuf, n, srcs);
                float pl = 0, pr = 0;
                for (int q = 0; q < n; q++)
                {
                    float gl = sb.GL, gr = sb.GR;
                    if (sb.AL != null) { int j = (int)Math.Clamp((b0 + q) / AutoBlock, 0, sb.AL.Length - 1); gl = sb.AL[j]; gr = sb.AR![j]; }
                    float l = sbuf[q * 2] * gl, r = sbuf[q * 2 + 1] * gr;
                    mix[q * 2] += l; mix[q * 2 + 1] += r;
                    if (l > pl) pl = l; else if (-l > pl) pl = -l;
                    if (r > pr) pr = r; else if (-r > pr) pr = -r;
                }
                if (peaks != null && peaks.Length >= Slots) { peaks[SubSlot(k)] = pl / 32768f; peaks[SubSlot(k) + 1] = pr / 32768f; }
            }
        float ml = 0, mr = 0;
        for (int k = 0; k < n * 2; k += 2)
        {
            float g = master.Table is { } mt ? mt[(int)Math.Clamp((b0 + k / 2) / AutoBlock, 0, mt.Length - 1)] : master.Gain;   // (master automation: per block)
            float l = mix[k] *= g, r = mix[k + 1] *= g;
            if (Math.Abs(l) > ml) ml = Math.Abs(l);
            if (Math.Abs(r) > mr) mr = Math.Abs(r);
        }
        if (peaks != null) { peaks[^2] = ml / 32768f; peaks[^1] = mr / 32768f; }
    }

    /// <summary>Renders [t0, t1) offline with the playback mixer, in 100 ms blocks (sink gets the block and its frame count).</summary>
    public static void Render(AudioOut.Src[] srcs, MasterMix master, double t0, double t1, Action<float[], int> sink, CancellationToken ct = default)
    {
        const int N = Rate / 10;
        var mix = new float[N * 2]; var bus = Bus(N); var tmp = new short[N * 2]; var fxs = new AudioDsp.FxState();   // (effects from the render start)
        long first = (long)Math.Round(t0 * Rate), total = (long)Math.Round(t1 * Rate) - first;
        for (long f = 0; f < total; f += N)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(N, total - f);
            Mix(srcs, master, (first + f) / (double)Rate, n, mix, bus, tmp, null, fxs);
            sink(mix, n);
        }
    }

    /// <summary>Loudness of the timeline mix over [t0, t1) as the export will sound before normalization (waits for audio to conform).</summary>
    public static Loudness.Result Measure(Editor ed, double t0, double t1, CancellationToken ct = default, IProgress<double>? progress = null) =>
        Measure(Sources(ed, Transitions.Compute(ed), new()).ToArray(), MasterOf(ed), t0, t1, ct, progress);

    /// <summary>Loudness of the given sources (snapshot them on the UI thread, measure on any thread).</summary>
    public static Loudness.Result Measure(AudioOut.Src[] srcs, MasterMix master, double t0, double t1, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        foreach (var p in srcs.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            while (!AudioCache.IsDone(p)) { ct.ThrowIfCancellationRequested(); Thread.Sleep(20); }
        var meter = new Loudness.Meter();
        double span = Math.Max(1e-6, t1 - t0), done = 0;
        Render(srcs, master, t0, t1, (buf, n) =>
        {
            meter.Add(buf, n, 1 / 32768f);
            done += n / (double)Rate; progress?.Report(Math.Min(1, done / span));
        }, ct);
        return meter.Result();
    }
}

/// <summary>The master fader as the mixer applies it: <see cref="Gain"/>, or (automated) <see cref="Table"/> = a gain per
/// <see cref="Mixer.AutoBlock"/> block of the timeline. A plain float converts to it.</summary>
public sealed record MasterMix(float Gain, float[]? Table = null)
{
    public static implicit operator MasterMix(float g) => new(g);
}
