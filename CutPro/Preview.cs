using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// 1280x720 program monitor. Video clips show frames decoded by <see cref="FrameStream"/> (ffmpeg) into a
/// WriteableBitmap: the frame for exactly the requested time, so picture follows the audio master clock.
/// Display layers (image + decoder) are pooled and handed from clip to clip: a piece created by a razor cut,
/// undo/redo or a through-edit takes over the decoder that is already positioned on its file, and upcoming clips
/// are pre-rolled before the cut. Titles/images are plain WPF elements. Also publishes the audio mix list to
/// <see cref="AudioOut"/>.
/// </summary>
public class Preview : Canvas
{
    class Layer
    {
        public FrameworkElement El = null!; public TitleVisual? Tv; public MaskFxHost? Fx; public bool HadMasks; public Image? Bg;
        public BitmapSource? Original, Processed;          // a picture, and its graded / drawn-look version
        public WriteableBitmap? MatteWb; public string MatteKey = "";
        public string ProcKey = "", ProcBusy = "";
    }
    class VLayer
    {
        public readonly MaskFxHost Fx = new();       // layer = Grid(host) with the image + object-mask effect overlays
        public Image Img => Fx.Img;
        public Grid Host => Fx.Host;
        public long Stamp;                            // bumps whenever the shown pixels change
        public bool HadMasks;
        public Image? Bg;                             // Shorts "Fit + blur" backdrop
        public WriteableBitmap? Wb;
        public FrameStream? Fs;
        public string? ClipId;
        public bool HasFrame, Measured;
        public int LastTrack = -1;
        public long LastRestart, LastShow, ShownIdx = -1;
        public FrameStream? ShownFs;
        public WriteableBitmap? ProxyWb;
        public long ProxyIdx = -1, SrcMovedAt;
        public double PrevSrc = double.NaN;
        public bool ShownFull;
        public double ShownSrc = double.NaN;
        public Grade.Lut? ShownLut, ProxyLut;          // colour applied to what is on screen (redraw when the grade changes)
        public byte[]? GradeBuf;
        public WriteableBitmap? MatteWb;                  // track matte: the shown frame as a matte (white, alpha = brightness or alpha)
    }
    /// <summary>A clip's video for the decoders: Path / Src = the file and time to decode (the media, or the clip's rendered remap), Rate = its
    /// frame grid (0 = sequence), MapSrc = the media time shown (masks, scrub cache), Backward = reversed and not rendered yet.</summary>
    readonly record struct Need(Clip C, MediaItem M, bool Active, double Src, int W, int H, string Path, double Rate, double MapSrc, bool Backward, double PathDur, string Vf = "", bool Alpha = false);

    const double PreRoll = 1.0;   // seconds before a cut that the next clip starts decoding
    const int MaxSpare = 3;       // idle display layers kept for reuse
    const int DepthPlaying = 12, DepthPaused = 4, KeepPaused = 30;   // frames
    const double PausedMargin = 0.25;                                  // seconds cached before the frame when paused
    const long StableMs = 25;     // paused: the wanted frame counts as "stopped" after this long without moving
    double syncT = double.NaN; long syncTMovedAt;   // the playhead's last position and when it last moved (paused pre-roll)
    readonly Dictionary<string, Layer> layers = new();
    readonly List<VLayer> vpool = new();
    readonly string? ffmpeg = Exporter.FindFfmpeg();
    public Editor Ed = null!;
    public AudioOut? Audio;
    /// <summary>Playback resolution divisor (1 = full 1280x720, 2 = half, 4 = quarter), like Premiere's 1/2, 1/4.</summary>
    public int ResDiv = 1;
    public int Restarts;          // diagnostics: decoder (re)starts
    public List<string>? Trace;   // diagnostics: decoder restart reasons (playtest with CUTPRO_PTDUMP)
    double startupMs = 150, startupVfMs = 300;   // running estimates of decoder time-to-first-frame (plain / with a drawn look: ~2×)
    /// <summary>Set by the main window: false while playback waits for the first frame (no lead then).</summary>
    public bool ClockRunning;

    static void Set(DependencyObject o, DependencyProperty p, double v)
    {
        var cur = (double)o.GetValue(p);
        if (double.IsNaN(cur) || Math.Abs(cur - v) > 1e-6) o.SetValue(p, v);
    }
    static void Vis(UIElement e, Visibility v) { if (e.Visibility != v) e.Visibility = v; }
    static void Z(UIElement e, int z) { if (GetZIndex(e) != z) SetZIndex(e, z); }
    // (Covers: see transitions below)
    static double Src(Clip c, double t) => c.In + (t - c.Start);
    static double SrcAt(Clip c, MediaItem m, double t) => Math.Clamp(Src(c, t), 0, Math.Max(0, m.Duration - 0.001));
    VLayer? LayerOf(string id) => vpool.FirstOrDefault(v => v.ClipId == id);
    Transitions.Plan plan = new();
    bool Covers(Clip c, double t) => plan.Shows(c, t);                   // on screen, transitions included
    /// <summary>Clip fade, except on sides that have a transition (the transition replaces the fade).</summary>
    double FadeOf(Clip c, double t)
    {
        double f = 1;
        if (c.FadeIn > 0 && plan.HeadOf(c) == null) f = Math.Min(f, (t - c.Start) / c.FadeIn);
        if (c.FadeOut > 0 && plan.TailOf(c) == null) f = Math.Min(f, (c.End - t) / c.FadeOut);
        return Math.Clamp(f, 0, 1);
    }
    /// <summary>Frame rectangle of an unrotated clip (null when rotated) — decides which dissolve blend is exact.</summary>
    Rect? RectOf(Clip c, double t)
    {
        if (c.Kind == ClipKind.Text) return null;
        var (x, y, w, h, rot) = Editor.Placement(Ed.MediaOf(c), c, t);
        return Math.Abs(rot) < 1e-6 ? new Rect(x, y, w, h) : null;
    }

    /// <summary>A transition's clip region / opacity mask, given in frame coordinates, mapped onto a layer at (left, top) size
    /// w×h rotated about its centre. A cut-out object mask keeps the opacity mask (it wins over an iris).</summary>
    static void ApplyLook(FrameworkElement el, MaskFxHost? fx, Transitions.LookInfo look, double left, double top, double w, double h, double rot)
    {
        Transform? xf = null;
        if (look.Region != null || look.Mask != null)
        {
            var g = new TransformGroup();
            g.Children.Add(new TranslateTransform(-left, -top));
            if (Math.Abs(rot) > 1e-6) g.Children.Add(new RotateTransform(-rot, w / 2, h / 2));
            g.Freeze(); xf = g;
        }
        Geometry? clip = null;
        if (look.Region != null) { clip = look.Region.Clone(); clip.Transform = xf; clip.Freeze(); }
        if (!Equals(el.Clip, clip)) el.Clip = clip;
        Brush? mask = null;
        if (look.Mask != null) { mask = look.Mask.Clone(); mask.Transform = xf; mask.Freeze(); }
        if (fx != null) { fx.Extra = mask; var want = fx.CutBrush ?? mask; if (!ReferenceEquals(el.OpacityMask, want)) el.OpacityMask = want; }
        else if (!ReferenceEquals(el.OpacityMask, mask)) el.OpacityMask = mask;
    }

    /// <summary>Dip to black / white: a full-frame colour layer on the track, above its clips.</summary>
    System.Windows.Shapes.Rectangle?[] dipRect = Array.Empty<System.Windows.Shapes.Rectangle?>();
    void SyncDips(double t)
    {
        for (int k = 0; k < trackCv.Length; k++)
        {
            var dip = Ed.TrackOff[k] ? null : Transitions.DipAt(plan, k, t);
            if (dip == null) { if (dipRect[k] != null) Vis(dipRect[k]!, Visibility.Collapsed); continue; }
            var r = dipRect[k] ??= new System.Windows.Shapes.Rectangle { IsHitTestVisible = false };
            if (r.Parent != trackCv[k]) { Detach(r); trackCv[k].Children.Add(r); }
            Z(r, 3);
            Set(r, WidthProperty, Editor.W); Set(r, HeightProperty, Editor.H);
            if (r.Fill is not SolidColorBrush sb || sb.Color != dip.Value.color) r.Fill = new SolidColorBrush(dip.Value.color);
            Set(r, OpacityProperty, dip.Value.alpha);
            Vis(r, Visibility.Visible);
        }
    }

    void DecodeSize(MediaItem m, Clip c, out int w, out int h)
    {
        if (Pin.Active(c)) { w = Math.Max(2, Editor.W / Math.Max(1, ResDiv) / 2 * 2); h = Math.Max(2, Editor.H / Math.Max(1, ResDiv) / 2 * 2); return; }   // (corner pin: a frame-sized warp)
        double mw = m.Width > 0 ? m.Width : Editor.W, mh = m.Height > 0 ? m.Height : Editor.H;
        double fw = Editor.W / mw, fh = Editor.H / mh;
        double f = Math.Min(Editor.CoversFrame(c) ? Math.Max(fw, fh) : Math.Min(fw, fh), 1.0) / Math.Max(1, ResDiv);   // never upscale in the decoder
        w = Math.Max(2, (int)Math.Round(mw * f / 2) * 2); h = Math.Max(2, (int)Math.Round(mh * f / 2) * 2);
    }

    /// <summary>Clips whose video should be on screen at t (in range, track visible, media online).</summary>
    bool Shows(Clip c, double t) => c.Kind == ClipKind.Video && Covers(c, t) && !Ed.TrackOff[c.Track] && Ed.MediaOf(c) is { Offline: false };

    /// <summary>n starts exactly where an on-screen clip of the same file ends, in source too (razor cut / through-edit).</summary>
    bool Continues(Need n, Dictionary<string, Need> need)
    {
        double tol = 0.5 / TimeUtil.Fps;
        if (TimeMap.IsRemapped(n.C) || Stab.Active(n.C)) return false;          // (their own files)
        return need.Values.Any(p => p.Active && p.M.Path == n.M.Path && p.Vf == n.Vf && !TimeMap.IsRemapped(p.C) && !Stab.Active(p.C) && Math.Abs(p.C.End - n.C.Start) < tol
                                    && Math.Abs(p.C.In + p.C.Dur - n.C.In) < tol && LayerOf(p.C.Id) != null);
    }

    /// <summary>What to decode for clip c at t. Plain clips: the media at its source time. Remapped clips: their rendered file at the time
    /// since the start of their range; until it is rendered, the media's native frame for t (forwards / held), or — reversed, while
    /// playing — the scrub cache. Nested sequences: their render (null while there is none yet — <see cref="Nest"/> queues it).</summary>
    Need? NeedFor(Clip c, MediaItem m, bool active, double t, int w, int h)
    {
        string vf = DecoderGraph(c, w, h, m.Seq != null); bool alpha = Keyer.Active(c.Key) || Warp.InDecoder(c) || m.Seq != null;   // key / looks / (matte) corner pin: the decoder runs the export's graph
        if (m.Seq != null)
        {
            var (ns, ne) = plan.Range(c);
            if (Nest.Ready(Ed, c, ns, ne) is not var (nf, offset, dur, _)) return null;
            // the render's frame for the clip's frame at t (Nest.FrameAt — the export's rule, rate conversion included), decoded on the render's
            // own frame grid; held past its end
            double rIn = m.Fps > 0 ? m.Fps : TimeUtil.Fps;
            long k = Math.Max(0, (long)Math.Floor((t - c.Start) * TimeUtil.Fps + 1e-6));
            long fi = Math.Clamp(Nest.FrameAt(c, offset, rIn, TimeUtil.Fps, k), 0, Math.Max(0, (long)Math.Round(dur * rIn) - 1));
            return new Need(c, m, active, (fi + 0.5) / rIn, w, h, nf, rIn, offset + (fi + 0.5) / rIn, false, dur, vf, alpha);
        }
        if (Stab.Active(c))
        {   // stabilized: its rendered file (Remap's layout: the clip's range on the sequence grid), the plain media while it renders
            var (ss, se) = plan.Range(c);
            if (Stab.Ready(c, m, ss, se) is { } sf)
                return new Need(c, m, active, Math.Clamp(t - ss, 0, Math.Max(0, se - ss - 0.5 / TimeUtil.Fps)), w, h, sf, 0, SrcAt(c, m, t), false, se - ss, vf, alpha);
        }
        // (HDR footage: tone-mapped right after scaling when the original file is read — the export does the same; proxies and renders are SDR already)
        string withHdr = Hdr.Of(m) is { Length: > 0 } hdr ? (vf == "" ? hdr : hdr + "," + vf) : vf;
        if (!TimeMap.IsRemapped(c)) { double s = SrcAt(c, m, t); var pth = Proxies.PathOf(m); return new Need(c, m, active, s, w, h, pth, 0, s, false, m.Duration, pth == m.Path ? withHdr : vf, alpha); }   // (proxies on: the small copy)
        var (rs, re) = plan.Range(c);
        double fps = TimeMap.NativeFps(m);
        long i = TimeMap.NativeIndex(c, m, t);
        double map = (i + 0.5) / fps;                                  // (the middle of the native frame: no rounding at its edges)
        if (Remap.Ready(c, m, rs, re) is { } file)
            return new Need(c, m, active, Math.Clamp(t - rs, 0, Math.Max(0, re - rs - 0.5 / TimeUtil.Fps)), w, h, file, 0, map, false, re - rs, vf, alpha);
        return new Need(c, m, active, map, w, h, m.Path, fps, map, c.Reverse, m.Duration, withHdr, alpha);
    }
    readonly Dictionary<(string, string, int), string> decGraphs = new();
    string DecoderGraph(Clip c, int w, int h, bool srcAlpha = false)
    {
        bool pin = Warp.InDecoder(c);                                       // (otherwise the pin is the picture's shader: MaskFxHost.Pin)
        if (!Looks.InDecoder(c, pin)) return "";
        var key = (Keyer.Sig(c.Key), Toon.Sig(c.Toon) + "/" + Looks.Sig(c.Looks) + "/" + (pin ? Pin.Sig(c.Pin) + Lens.Sig(Lens.At(c, c.Start)) : "") + "/" + w + "/" + srcAlpha, h);
        if (!decGraphs.TryGetValue(key, out var g)) { if (decGraphs.Count > 64) decGraphs.Clear(); decGraphs[key] = g = Looks.DecoderGraph(c, w, h, srcAlpha, pin); }
        return g;
    }
    static double RateOf(Need n) => n.Rate > 0 ? n.Rate : TimeUtil.Fps;

    public void Sync(double t, bool playing)
    {
        long now = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;   // ms, high resolution (TickCount is ~16 ms coarse)
        EnsureTracks();                                                     // (the layout may have changed: tracks added / removed)
        plan = Transitions.Compute(Ed);
        SyncLayers(t);
        SyncAdjust(t);
        SyncDips(t);
        SyncAudio();
        if (ffmpeg == null) { SyncBlendHosts(); SyncShadows(); return; }

        // 1. video clips that need a decoder: on screen now, or starting within PreRoll — while playing, and also while paused (playhead
        //    at rest) for clips with a drawn look: their decoder needs ~2× as long for its first frame and catches up slowly, so pressing
        //    play just before such a cut would show trailing frames
        if (t != syncT) { syncT = t; syncTMovedAt = now; }
        bool atRest = !playing && now - syncTMovedAt >= StableMs;
        var need = new Dictionary<string, Need>();
        bool nestWait = false;
        foreach (var c in Ed.Clips)
        {
            if (c.Kind != ClipKind.Video || Ed.TrackOff[c.Track]) continue;
            if (Ed.MediaOf(c) is not { Offline: false } m) continue;
            bool active = Covers(c, t);
            double first = plan.Range(c).s;                                   // (a transition shows it before its start)
            if (!active && !(first > t && first - t <= PreRoll && (playing || atRest && (Looks.InDecoder(c, Warp.InDecoder(c)) || m.Seq != null)))) continue;
            DecodeSize(m, c, out int w, out int h);
            if (NeedFor(c, m, active, active ? t : first, w, h) is { } nd) need[c.Id] = nd;
            else if (active) nestWait = true;                                  // (a nested sequence still rendering)
        }
        NestBadge(nestWait);

        // 2. release layers whose clip no longer needs one (their decoder stays alive this frame for a hand-over)
        foreach (var L in vpool) if (L.ClipId != null && !need.ContainsKey(L.ClipId)) L.ClipId = null;

        // 3. assign layers, on-screen clips first
        foreach (var n in need.Values.OrderByDescending(n => n.Active).ThenBy(n => n.C.Start))
        {
            if (LayerOf(n.C.Id) != null) continue;
            if (!n.Active && Continues(n, need)) continue;          // will take over the current piece's decoder at the cut
            // a free decoder already positioned on this file: no restart, no black frame (razor cut, undo/redo, through-edit)
            var L = vpool.FirstOrDefault(v => v.ClipId == null && v.Fs is { } f && f.Path == n.Path && f.Rate == RateOf(n) && f.Vf == n.Vf && f.Alpha == n.Alpha && f.W == n.W && f.H == n.H && f.CanServe(n.Src));
            if (L == null)
            {
                // otherwise reuse a free layer, preferring the one that was showing on this track (holds its picture until the new frame arrives)
                L = vpool.Where(v => v.ClipId == null).OrderByDescending(v => v.HasFrame && v.LastTrack == n.C.Track).FirstOrDefault();
                if (L == null) { L = new VLayer(); vpool.Add(L); }
                if (!(n.Active && L.LastTrack == n.C.Track)) L.HasFrame = false;
                L.ShownSrc = double.NaN;
            }
            L.ClipId = n.C.Id; L.PrevSrc = double.NaN;
        }

        // 4. drive each layer
        foreach (var L in vpool)
        {
            if (L.ClipId == null || !need.TryGetValue(L.ClipId, out var n)) { Vis(L.Host, Visibility.Collapsed); if (L.Bg != null) Vis(L.Bg, Visibility.Collapsed); continue; }
            var fs = L.Fs;
            if (fs != null && fs.FirstFrameMs >= 0 && !L.Measured) { if (fs.Vf.Length > 0) startupVfMs = startupVfMs * 0.7 + fs.FirstFrameMs * 0.3; else startupMs = startupMs * 0.7 + fs.FirstFrameMs * 0.3; L.Measured = true; }
            if (double.IsNaN(L.PrevSrc) || Math.Abs(n.Src - L.PrevSrc) > 1e-6) { L.PrevSrc = n.Src; L.SrcMovedAt = now; }
            // paused and the wanted frame is still moving (playhead scrub, clip drag): the scrub cache shows it instantly
            bool moving = !playing && now - L.SrcMovedAt < StableMs;
            bool proxy = (!playing || n.Backward) && n.Active && (n.Vf == "" || n.Vf == Hdr.Of(n.M)) &&   // (the scrub cache is tone-mapped too)
                         !(Stab.Active(n.C) && n.Path != n.M.Path) && ScrubCache.Has(n.M.Path, n.MapSrc);   // (a drawn look comes from the decoder only; the proxy isn't stabilized)
            bool same = fs != null && fs.Path == n.Path && fs.Rate == RateOf(n) && fs.Vf == n.Vf && fs.Alpha == n.Alpha && fs.W == n.W && fs.H == n.H;
            bool fits = same && fs!.CanServe(n.Src);
            bool restart = !fits && !(playing && n.Backward) && (     // (reversed, not rendered yet: the scrub cache plays it)
                fs == null ? true
                : playing ? now - L.LastRestart >= 100 && (fs.Delivered || now - L.LastRestart > 400)   // throttled
                : !moving ? true                                     // stopped on a frame: go straight for it, drop stale decodes
                : !proxy && now - L.LastRestart >= 100 && (fs.Delivered || now - L.LastRestart > 400)); // no cache yet: throttled
            if (restart && Trace != null) Trace.Add($"{now} restart src={n.Src:0.000} fits={fits} same={same} moving={moving} proxy={proxy} w={fs?.Index(n.Src)} {fs?.DebugState}");
            if (restart)
            {
                fs?.Dispose();
                // playing: start ahead of the playhead by the decoder's startup time, so the held picture is replaced by an
                //          in-sync frame instead of frames that trail the audio while the decoder catches up.
                // paused:  start a little before, so small backward moves (drags, frame steps) come from the cache.
                // paused, moving backwards past the cache (frame-stepping back): start early so further steps back are cached;
                // otherwise (stopped after a scrub, clicked) start exactly at the frame: fastest time to the sharp picture
                bool backward = !playing && same && fs != null && n.Src < fs.Start;
                double offset = playing && ClockRunning && n.Active ? Math.Min(0.6, (n.Vf.Length > 0 ? startupVfMs : startupMs) * 1.3 / 1000) : backward ? -PausedMargin : 0;
                if (n.Src + offset > n.PathDur - 0.05) offset = 0;
                L.Fs = fs = new FrameStream(ffmpeg, n.Path, n.Src, n.W, n.H, playing ? DepthPlaying : DepthPaused, playing ? 0 : KeepPaused,
                                            offset, n.Path != n.M.Path ? Decoder.SwFrame : playing ? n.M.PlayDecoder : n.M.SeekDecoder, n.Rate, n.Vf, n.Alpha);
                L.LastRestart = now; L.Measured = false; Restarts++;
                if (offset > 0) L.ShownSrc = double.NaN;    // what's on screen now is a held frame
                same = true;
            }
            if (fs == null && !proxy) continue;                         // (reversed while playing: no decoder, the scrub cache shows it)
            if (fs != null) { fs.Depth = playing ? DepthPlaying : DepthPaused; fs.KeepBehind = playing ? 0 : KeepPaused; }
            if (!n.Active) { Vis(L.Host, Visibility.Collapsed); if (L.Bg != null) Vis(L.Bg, Visibility.Collapsed); continue; }

            var (idx, buf) = same && fs != null ? fs.Take(n.Src) : (-1, null);      // never show another file's frames
            bool exact = buf != null && idx == fs!.Index(n.Src);
            // while playing, hold the current picture rather than show a frame that trails the clock (decoder catching up
            // after an edit); if the decoder really can't keep up, show what it has at least every 150 ms
            if (buf != null && playing && L.HasFrame && fs!.Index(n.Src) - idx > 2 && now - L.LastShow < 150) { buf = null; L.ShownSrc = double.NaN; }
            var gl = Grade.For(n.C.Grade);                                      // colour: the export's LUT on what is shown
            if (!exact && proxy)
            {
                // exact frame from the scrub cache now; the full-resolution frame replaces it as soon as it is decoded
                long k = ScrubCache.FrameIndex(n.MapSrc);
                if (k != L.ProxyIdx || L.Img.Source != L.ProxyWb || L.ProxyLut != gl)
                {
                    var pwb = L.ProxyWb;
                    if (ScrubCache.Show(n.M.Path, n.MapSrc, ref pwb, gl) >= 0)
                    {
                        L.ProxyWb = pwb; L.ProxyIdx = k; L.ProxyLut = gl; L.Stamp++;
                        if (L.Img.Source != L.ProxyWb) L.Img.Source = L.ProxyWb;
                        L.HasFrame = true; L.ShownFull = false; L.ShownSrc = k / (double)ScrubCache.Fps; L.LastShow = now;
                    }
                }
            }
            else if (buf != null && fs != null)
            {
                var fmt = fs.Alpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
                if (L.Wb == null || L.Wb.PixelWidth != fs.W || L.Wb.PixelHeight != fs.H || L.Wb.Format != fmt)
                    L.Wb = new WriteableBitmap(fs.W, fs.H, 96, 96, fmt, null);
                if (idx != L.ShownIdx || fs != L.ShownFs || L.Img.Source != L.Wb || L.ShownLut != gl)
                {
                    var pix = buf;
                    if (gl != null || fs.Alpha)
                    {   // (the stream keeps its frame: grade a copy, so a changed grade can redraw the same frame; a keyed frame is graded on
                        //  its straight colours like the export, then premultiplied for WPF)
                        int len = fs.W * fs.H * 4;
                        if (L.GradeBuf == null || L.GradeBuf.Length != len) L.GradeBuf = new byte[len];
                        Buffer.BlockCopy(buf, 0, L.GradeBuf, 0, len);
                        if (gl != null) Grade.Apply(gl, L.GradeBuf, fs.W, fs.H, fs.W * 4);
                        if (fs.Alpha) Premultiply(L.GradeBuf, fs.W, fs.H);
                        pix = L.GradeBuf;
                    }
                    L.Wb.WritePixels(new Int32Rect(0, 0, fs.W, fs.H), pix, fs.W * 4, 0); L.Stamp++; L.ShownLut = gl;
                    if (n.C.Mask != MaskMode.None) WriteMatte(ref L.MatteWb, pix, fs.W, fs.H, n.C, t, fs.Alpha);   // (alpha: a key, a pin or a nested sequence's own)
                }
                if (L.Img.Source != L.Wb) L.Img.Source = L.Wb;
                L.ShownIdx = idx; L.ShownFs = fs; L.LastShow = now;
                L.HasFrame = true; L.ShownFull = true; L.ShownSrc = fs.Start + idx / fs.Rate;
            }
            var c = n.C;
            var look = Transitions.Look(plan, c, t, x => RectOf(x, t));
            var (px, py, vw, vh, rot) = Moved(look, Editor.Placement(n.M, c, t));
            Set(L.Host, WidthProperty, vw); Set(L.Host, HeightProperty, vh);
            Set(L.Host, LeftProperty, px); Set(L.Host, TopProperty, py);
            SetRot(L.Host, rot, vw, vh);

            Set(L.Host, OpacityProperty, Anim.Value(c, AnimProp.Opacity, t) / 100 * FadeOf(c, t) * look.Opacity);
            Z(L.Host, look.Top ? 2 : 0);
            Place(L.Host, c.Track, c);
            L.Fx.Flip(c.FlipH, c.FlipV);
            L.Fx.Pin(c, t, n.M);
            if (c.Masks.Count > 0 || L.HadMasks) { L.Fx.Update(c, Ed, MaskGeometry.FrameOf(n.MapSrc), vw, vh, L.Stamp); L.HadMasks = c.Masks.Count > 0; }
            L.Fx.Vignette(c.Grade, vw, vh);
            L.Fx.Crop(c, t, vw, vh);
            ApplyLook(L.Host, L.Fx, look, px, py, vw, vh, rot);
            SetBlur(L.Host, Anim.Value(c, AnimProp.Blur, t));
            Shadow(L.Host, c, px, py, vw, vh, rot, c.Track);
            L.LastTrack = c.Track;
            Vis(L.Host, L.HasFrame ? Visibility.Visible : Visibility.Collapsed);
            if (c.Mask != MaskMode.None) Vis(L.Host, Visibility.Collapsed);      // a track matte isn't drawn: its frame masks the tracks below (SyncMasks)
            Backdrop(ref L.Bg, L.Img.Source, n.M, c, t, L.HasFrame);
        }

        // 5. free layers: stop their decoders, keep a few for reuse
        foreach (var L in vpool.Where(v => v.ClipId == null)) { L.Fs?.Dispose(); L.Fs = null; }
        foreach (var L in vpool.Where(v => v.ClipId == null).Skip(MaxSpare).ToList()) { Unplace(L.Host); vpool.Remove(L); }
        SyncBlendHosts(); SyncShadows();
    }

    /// <summary>True when every on-screen video has its frame for t decoded and all sound is conformed.</summary>
    /// <summary>Tests: every on-screen video shows its exact full-resolution frame for t (paused).</summary>
    public bool Settled(double t)
    {
        foreach (var c in Ed.Clips.Where(c => Shows(c, t)))
        {
            if (NeedFor(c, Ed.MediaOf(c)!, true, t, 0, 0) is not { } n) return false;     // (a nested sequence still rendering)
            if (LayerOf(c.Id) is not { Fs: { } fs } L || fs.Path != n.Path || !L.ShownFull || L.ShownFs != fs || L.ShownIdx != fs.Index(n.Src)) return false;
        }
        return true;
    }

    public bool ReadyToPlay(double t)
    {
        foreach (var c in Ed.Clips.Where(c => Shows(c, t)))
        {
            if (NeedFor(c, Ed.MediaOf(c)!, true, t, 0, 0) is not { } n) continue;      // (a nested sequence still rendering: it appears when done)
            if (n.Backward) continue;                                     // (played from the scrub cache)
            if (LayerOf(c.Id)?.Fs is not { } fs || fs.Path != n.Path || !fs.HasFrameFor(n.Src)) return false;
        }
        foreach (var s in Audio?.Sources ?? Array.Empty<AudioOut.Src>())
            if (s.End > t && s.Start < t + 1 && !AudioCache.IsReady(s.Path)) return false;
        return true;
    }

    /// <summary>"Rendering nested sequence…" in the monitor while a nested clip on screen has no picture yet.</summary>
    Border? nestBadge;
    void NestBadge(bool show)
    {
        if (!show) { if (nestBadge != null) nestBadge.Visibility = Visibility.Collapsed; return; }
        if (nestBadge == null)
        {
            var tb = new TextBlock { FontSize = 15, Margin = new Thickness(12, 6, 12, 6) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg");
            nestBadge = new Border { CornerRadius = new CornerRadius(6), Child = tb, IsHitTestVisible = false, Opacity = 0.92 };
            nestBadge.SetResourceReference(Border.BackgroundProperty, "B.Surf.Panel");
            SetLeft(nestBadge, 16); SetTop(nestBadge, 16); Panel.SetZIndex(nestBadge, 1000);
            Children.Add(nestBadge);
        }
        ((TextBlock)nestBadge.Child).Text = Nest.Status ?? "Rendering nested sequence…";
        nestBadge.Visibility = Visibility.Visible;
    }

    readonly Dictionary<string, (string sig, float[] table)> gainCache = new();
    void SyncAudio()
    {
        if (Audio == null) return;
        var list = Mixer.Sources(Ed, plan, gainCache);
        var mm = Mixer.MasterOf(Ed); if (Audio.Master != mm) Audio.Master = mm;
        var old = Audio.Sources;
        if (old.Length != list.Count || !old.SequenceEqual(list)) Audio.Sources = list.ToArray();
    }

    // ---- track groups: each video track has its own canvas, nested so a mask can cover "everything below a track" ----
    // trackCv[k] = row k's clips (row 0 the highest video track). below[k] = everything under row k (the rows k+1 … V1 with their own
    // adjustments) — what a matte or an adjustment layer on row k acts on, and what a blend mode on row k mixes with (V1: black).
    Canvas[] trackCv = Array.Empty<Canvas>(), below = Array.Empty<Canvas>();
    BitmapSource?[] maskBmp = Array.Empty<BitmapSource?>();
    double[] maskF = Array.Empty<double>();
    readonly Dictionary<string, (string sig, BitmapSource bmp)> mattes = new();
    readonly List<UIElement> treeRoots = new();

    public Preview() { EnsureTracks(); }

    /// <summary>The canvas tree for the active layout's video tracks — built again when their number changes (tracks added / removed, another
    /// sequence). Layers sit on their row's canvas and are put there again by the next <see cref="Sync"/>; mattes, adjustments and blend
    /// shaders are re-applied from scratch on the new canvases.</summary>
    void EnsureTracks()
    {
        int n = Tracks.Video;
        if (trackCv.Length == n) return;
        foreach (var e in treeRoots) Children.Remove(e);
        treeRoots.Clear();
        Canvas Mk() => new() { Width = Editor.W, Height = Editor.H };
        trackCv = Enumerable.Range(0, n).Select(_ => Mk()).ToArray();
        int m = Math.Max(0, n - 1);
        below = new Canvas[m]; adjIn = new Canvas[m]; adjVig = new System.Windows.Shapes.Rectangle[m];
        for (int k = 0; k < m; k++)
        {
            below[k] = Mk(); adjIn[k] = Mk();
            adjVig[k] = new System.Windows.Shapes.Rectangle { Width = Editor.W, Height = Editor.H, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        }
        // below[k] (adjustment colour shader, matte) > adjIn[k] (adjustment blur) > the tracks below; adjVig[k] (adjustment vignette) right
        // above below[k] — built from the lowest track up
        for (int k = m - 1; k >= 0; k--)
        {
            if (k == m - 1) adjIn[k].Children.Add(trackCv[n - 1]);
            else { adjIn[k].Children.Add(below[k + 1]); adjIn[k].Children.Add(adjVig[k + 1]); adjIn[k].Children.Add(trackCv[k + 1]); }
            below[k].Children.Add(adjIn[k]);
        }
        if (m > 0) { treeRoots.Add(below[0]); treeRoots.Add(adjVig[0]); }
        treeRoots.Add(trackCv[0]);
        for (int i = 0; i < treeRoots.Count; i++) Children.Insert(i, treeRoots[i]);   // (under the nest badge)
        maskBmp = new BitmapSource?[m]; maskF = new double[m]; maskKey = Enumerable.Repeat("", m).ToArray();
        adjVigCache = new (object?, string, Brush?)[m]; belowBrush = new VisualBrush?[m];
        dipRect = new System.Windows.Shapes.Rectangle?[n];
        foreach (var (e, bh) in blendHosts.ToList()) blendHosts[e] = (bh.Host, -1);   // (their shader samples the new tree: rebuilt by Place)
    }

    // ---- adjustment layers (Adjust): the tracks below an adjustment clip (any track above V1) get its blur, colour and vignette at its strength ----
    Canvas[] adjIn = Array.Empty<Canvas>();
    System.Windows.Shapes.Rectangle[] adjVig = Array.Empty<System.Windows.Shapes.Rectangle>();
    (object? grade, string sig, Brush? brush)[] adjVigCache = Array.Empty<(object?, string, Brush?)>();
    void SyncAdjust(double t)
    {
        for (int k = 0; k < below.Length; k++)
        {
            var c = Ed.TrackOff[k] ? null : Ed.Clips.FirstOrDefault(x => Adjust.Is(x) && x.Track == k && Covers(x, t));
            double s = c != null ? Adjust.Strength(c, t) : 0;
            var lut = c != null && s > 0 ? Grade.For(c.Grade) : null;
            if (lut != null && Adjust.Shader() is { } ps)
            {
                if (below[k].Effect is not LutEffect le || le.Sig != lut.Sig)
                {
                    below[k].Effect = new LutEffect(ps) { Lut = Adjust.Texture(lut), Sig = lut.Sig, Strength = s };
                    // the LUT brush must reach the shader unsmoothed (WPF realises an effect's brush input with the element's scaling mode);
                    // what is inside keeps the usual smooth scaling
                    RenderOptions.SetBitmapScalingMode(below[k], BitmapScalingMode.NearestNeighbor);
                    RenderOptions.SetBitmapScalingMode(adjIn[k], BitmapScalingMode.Linear);
                }
                else if (Math.Abs(le.Strength - s) > 1e-6) le.Strength = s;
            }
            else if (below[k].Effect != null) { below[k].Effect = null; below[k].ClearValue(RenderOptions.BitmapScalingModeProperty); }
            double r = c != null ? Adjust.Radius(c, t) : 0;
            if (r > 0.05) { if (adjIn[k].Effect is BlurEffect be) { if (Math.Abs(be.Radius - r) > 1e-6) be.Radius = r; } else adjIn[k].Effect = new BlurEffect { Radius = r, KernelType = KernelType.Gaussian }; }
            else if (adjIn[k].Effect != null) adjIn[k].Effect = null;
            if (c?.Grade is { HasVignette: true } g && s > 0)
            {
                string sig = $"{g.Vignette}|{g.VigMidpoint}|{g.VigFeather}|{g.VigRoundness}|{Editor.W}";
                if (!ReferenceEquals(adjVigCache[k].grade, g) || adjVigCache[k].sig != sig) adjVigCache[k] = (g, sig, Grade.VignetteBrush(g, Editor.W, Editor.H));
                if (adjVig[k].Fill != adjVigCache[k].brush) adjVig[k].Fill = adjVigCache[k].brush;
                adjVig[k].Opacity = s; adjVig[k].Visibility = Visibility.Visible;
            }
            else if (adjVig[k].Visibility != Visibility.Collapsed) adjVig[k].Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Puts a layer on its track. A clip with a blend mode goes inside a frame-sized host whose pixel shader (<see cref="BlendEffect"/>)
    /// mixes it with a brush of the tracks below (V1: black, like the export's base), so both samplers line up pixel for pixel.</summary>
    void Place(FrameworkElement e, int track, Clip? c = null)
    {
        track = Math.Clamp(track, 0, trackCv.Length - 1);
        var host = trackCv[track];
        var mode = c?.Blend ?? BlendMode.Normal;
        var ps = mode != BlendMode.Normal ? Blend.Shader(mode) : null;
        if (ps == null)
        {
            if (blendHosts.Remove(e, out var old)) { old.Host.Children.Remove(e); Detach(old.Host); }
            if (e.Parent == host) return;
            Detach(e); host.Children.Add(e);
            return;
        }
        if (!blendHosts.TryGetValue(e, out var bh))
            blendHosts[e] = bh = (new Canvas { Width = Editor.W, Height = Editor.H, ClipToBounds = true, Background = Brushes.Transparent, IsHitTestVisible = false }, -1);
        if (bh.Host.Effect is not BlendEffect be || be.Mode != mode || bh.Track != track)
        {
            bh.Host.Effect = new BlendEffect(mode, ps) { Below = BelowBrush(track) };
            blendHosts[e] = bh = (bh.Host, track);
        }
        if (e.Parent != bh.Host) { Detach(e); bh.Host.Children.Add(e); }
        if (bh.Host.Parent != host) { Detach(bh.Host); host.Children.Add(bh.Host); }
        Set(bh.Host, WidthProperty, Editor.W);
        Z(bh.Host, GetZIndex(e));
    }
    readonly Dictionary<FrameworkElement, (Canvas Host, int Track)> blendHosts = new();
    VisualBrush?[] belowBrush = Array.Empty<VisualBrush?>();
    static readonly SolidColorBrush BlackBelow = MakeBlack();
    static SolidColorBrush MakeBlack() { var b = new SolidColorBrush(Colors.Black); b.Freeze(); return b; }
    Brush BelowBrush(int track)
    {
        if (track >= below.Length) return BlackBelow;                      // (V1: black, like the export's base)
        return belowBrush[track] ??= new VisualBrush(below[track]) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, Editor.W, Editor.H) };
    }
    /// <summary>Per frame: a blend host shows only while its layer does (an idle host would still run the shader and render the brush), and
    /// hosts whose layer left are removed.</summary>
    void SyncBlendHosts()
    {
        foreach (var (e, bh) in blendHosts.ToList())
        {
            if (e.Parent != bh.Host) { blendHosts.Remove(e); Detach(bh.Host); continue; }
            Vis(bh.Host, e.Visibility);
        }
    }
    /// <summary>Drop shadows: per layer, a rectangle in the shadow colour whose opacity mask is a brush of the layer (its alpha: crop, key,
    /// masks, fades), blurred and offset under it on the track — the export builds the same from the clip's alpha (Exporter.ShadowUnder).</summary>
    readonly Dictionary<FrameworkElement, (Grid Host, System.Windows.Shapes.Rectangle Rect, VisualBrush Vb)> shadows = new();
    void Shadow(FrameworkElement layer, Clip c, double x, double y, double w, double h, double rot, int track)
    {
        if (c.Shadow is not { } s) { if (shadows.TryGetValue(layer, out var o)) Vis(o.Host, Visibility.Collapsed); return; }
        if (!shadows.TryGetValue(layer, out var sh))
        {
            var vb = new VisualBrush(layer) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute };
            var rect = new System.Windows.Shapes.Rectangle { OpacityMask = vb, IsHitTestVisible = false };
            var g = new Grid { IsHitTestVisible = false }; g.Children.Add(rect);
            shadows[layer] = sh = (g, rect, vb);
        }
        var cv = trackCv[Math.Clamp(track, 0, trackCv.Length - 1)];
        if (sh.Host.Parent != cv) { Detach(sh.Host); cv.Children.Add(sh.Host); }
        var (dx, dy) = Looks.Offset(s);
        double sc = Editor.OutScale; dx = 2 * Math.Round(dx * sc / 2) / sc; dy = 2 * Math.Round(dy * sc / 2) / sc;   // (even output pixels, like the export)
        // frame-sized, moved by the offset: a brush of the layer includes where the layer sits on the track (its offset and turn), so its
        // viewbox is the frame (a layer-sized box showed the layer shifted by its own position)
        Set(sh.Host, WidthProperty, Editor.W); Set(sh.Host, HeightProperty, Editor.H);
        Set(sh.Host, LeftProperty, dx); Set(sh.Host, TopProperty, dy);
        var box = new Rect(0, 0, Editor.W, Editor.H);
        if (sh.Vb.Viewbox != box) sh.Vb.Viewbox = box;
        var col = TitleVisual.ParseColor(s.Color, Colors.Black);
        if (sh.Rect.Fill is not SolidColorBrush fb || fb.Color != col) sh.Rect.Fill = new SolidColorBrush(col);
        Set(sh.Host, OpacityProperty, Math.Clamp(s.Opacity / 100, 0, 1));
        SetBlur(sh.Host, s.Softness);
        Z(sh.Host, GetZIndex(layer) - 1);
        Vis(sh.Host, Visibility.Visible);
    }
    /// <summary>Per frame: a shadow shows only while its layer does; shadows of removed layers go.</summary>
    void SyncShadows()
    {
        foreach (var (layer, sh) in shadows.ToList())
        {
            if (layer.Parent == null) { Detach(sh.Host); shadows.Remove(layer); continue; }
            if (layer.Visibility != Visibility.Visible) Vis(sh.Host, Visibility.Collapsed);
        }
    }

    /// <summary>Removes a layer (and its blend host).</summary>
    void Unplace(FrameworkElement e)
    {
        if (blendHosts.Remove(e, out var bh)) { bh.Host.Children.Remove(e); Detach(bh.Host); }
        else Detach(e);
    }
    static void Detach(FrameworkElement e) { if (e.Parent is Panel p) p.Children.Remove(e); }

    /// <summary>Shorts "Fit + blur": a blurred, darkened copy of the picture filling the frame behind it (V1). Drawn larger than
    /// the frame by the blur's reach so its edges don't fade in — the exporter builds the same (Exporter.BlurBackdrop).</summary>
    void Backdrop(ref Image? bg, ImageSource? src, MediaItem? m, Clip c, double t, bool show)   // (instance: fades follow transitions)
    {
        if (!(show && src != null && Exporter.BlurBackdrop(m, c))) { if (bg != null) Vis(bg, Visibility.Collapsed); return; }
        if (bg == null)
        {
            bg = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false,
                             Effect = new BlurEffect { Radius = Exporter.BackdropBlur, KernelType = KernelType.Gaussian } };
            SetZIndex(bg, -100);
        }
        if (bg.Parent != trackCv[^1]) { Detach(bg); trackCv[^1].Children.Add(bg); }   // (V1)
        // cover the frame + the blur's reach, centred (as the export's scale + crop); WPF's UniformToFill would anchor top-left
        double pad = 2 * Math.Ceiling((3 * Exporter.BlurSigma(Exporter.BackdropBlur) + 2) / 2);
        double fw = Editor.W + 2 * pad, fh = Editor.H + 2 * pad, k = Math.Max(fw / m!.Width, fh / m.Height), cw = m.Width * k, ch = m.Height * k;
        Set(bg, WidthProperty, cw); Set(bg, HeightProperty, ch);
        Set(bg, LeftProperty, (Editor.W - cw) / 2); Set(bg, TopProperty, (Editor.H - ch) / 2);
        if (!ReferenceEquals(bg.Source, src)) bg.Source = src;
        Set(bg, OpacityProperty, Exporter.BackdropLevel * Anim.Value(c, AnimProp.Opacity, t) / 100 * FadeOf(c, t));
        Vis(bg, Visibility.Visible);
    }

    /// <summary>The sequence format changed: canvases follow the logical width; text mattes are re-rendered.</summary>
    public void Resize()
    {
        Width = Editor.W;
        foreach (var cv in trackCv) cv.Width = Editor.W;
        foreach (var b in below) { b.Width = Editor.W; b.OpacityMask = null; }
        foreach (var a in adjIn) a.Width = Editor.W;
        foreach (var v in adjVig) v.Width = Editor.W;
        Array.Clear(adjVigCache);
        foreach (var vb in belowBrush) if (vb != null) vb.Viewbox = new Rect(0, 0, Editor.W, Editor.H);
        mattes.Clear(); Array.Clear(maskBmp);
        InvalidateMeasure();
    }

    /// <summary>A layer's placement with its transition look applied; a Move transition's layer lands on whole pixels the way the export's
    /// per-frame layer does (LayerPlan.Box: size rounded, then the centred position).</summary>
    static (double x, double y, double w, double h, double rot) Moved(Transitions.LookInfo look, (double x, double y, double w, double h, double rot) p)
    {
        var m = look.Apply(p);
        if (!look.Moves) return m;
        double iw = Math.Max(1, Math.Round(m.w)), ih = Math.Max(1, Math.Round(m.h));
        return (Math.Round(m.x + m.w / 2 - iw / 2), Math.Round(m.y + m.h / 2 - ih / 2), iw, ih, m.rot);
    }

    /// <summary>Rotation about the layer centre (degrees); no transform when 0.</summary>
    static void SetRot(UIElement e, double deg, double w, double h)
    {
        if (Math.Abs(deg) < 1e-6) { if (e.RenderTransform is RotateTransform) e.RenderTransform = Transform.Identity; return; }
        if (e.RenderTransform is not RotateTransform r) e.RenderTransform = r = new RotateTransform();
        if (r.Angle != deg) r.Angle = deg;
        if (r.CenterX != w / 2) r.CenterX = w / 2;
        if (r.CenterY != h / 2) r.CenterY = h / 2;
    }

    /// <summary>Gaussian blur on a layer (radius in sequence pixels; the exporter uses gblur with sigma = radius / 3).</summary>
    static void SetBlur(UIElement e, double r)
    {
        if (r <= 0.05) { if (e.Effect != null) e.Effect = null; return; }
        if (e.Effect is BlurEffect b) { if (Math.Abs(b.Radius - r) > 1e-3) b.Radius = r; }
        else e.Effect = new BlurEffect { Radius = r, KernelType = KernelType.Gaussian };
    }

    void SyncLayers(double t)
    {
        foreach (var k in layers.Keys.Where(k => !Ed.Clips.Any(c => c.Id == k)).ToList()) { Unplace(layers[k].El); layers.Remove(k); mattes.Remove(k); }
        foreach (var c in Ed.Clips)
        {
            if (c.Kind is not (ClipKind.Text or ClipKind.Image)) continue;
            if (!layers.TryGetValue(c.Id, out var L))
            {
                L = new Layer();
                if (c.Kind == ClipKind.Text) L.El = L.Tv = new TitleVisual();
                else
                {
                    if (Ed.MediaOf(c) is not { Offline: false } m) continue;
                    var bi = new BitmapImage(); bi.BeginInit(); bi.UriSource = new Uri(m.Path); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit();
                    L.Fx = new MaskFxHost(); L.Fx.Img.Source = L.Original = bi; L.Fx.Host.Visibility = Visibility.Visible; L.El = L.Fx.Host;
                }
                layers[c.Id] = L;
            }
            // a masking title is a matte for the tracks below it, not something drawn
            bool active = Covers(c, t) && !Ed.TrackOff[c.Track] && c.Mask == MaskMode.None;   // (a track matte isn't drawn: SyncMasks)
            Vis(L.El, active ? Visibility.Visible : Visibility.Collapsed);
            if (L.Fx != null) Backdrop(ref L.Bg, L.Fx.Img.Source, Ed.MediaOf(c), c, t, active);
            if (!active) continue;
            var look = Transitions.Look(plan, c, t, x => RectOf(x, t));
            Set(L.El, OpacityProperty, Anim.Value(c, AnimProp.Opacity, t) / 100 * FadeOf(c, t) * look.Opacity);
            Z(L.El, look.Top ? 2 : 0);
            Place(L.El, c.Track, c);
            if (L.Tv != null)
            {
                L.Tv.Update(c, false, look.Apply(TitleVisual.MotionAt(c, t)), t - c.Start);   // (a Move transition moves the title's own motion; text animation by clip time)
                double lx = look.Moves ? 0 : look.Dx, ly = look.Moves ? 0 : look.Dy;
                Set(L.El, LeftProperty, lx); Set(L.El, TopProperty, ly);                 // a title is a full-frame canvas
                ApplyLook(L.El, null, look, lx, ly, Editor.W, Editor.H, 0);
            }
            else
            {
                var (ix, iy, w, h, irot) = Moved(look, Editor.Placement(Ed.MediaOf(c), c, t));
                Set(L.El, WidthProperty, w); Set(L.El, HeightProperty, h);
                Set(L.El, LeftProperty, ix); Set(L.El, TopProperty, iy);
                SetRot(L.El, irot, w, h);

                SetBlur(L.El, Anim.Value(c, AnimProp.Blur, t));
                L.Fx?.Flip(c.FlipH, c.FlipV);
                L.Fx?.Pin(c, t, Ed.MediaOf(c));
                if (L.Fx != null && (c.Masks.Count > 0 || L.HadMasks)) { L.Fx.Update(c, Ed, MaskGeometry.FrameOf(c.In + (t - c.Start)), w, h, 0); L.HadMasks = c.Masks.Count > 0; }
                if (L.Fx != null && Ed.MediaOf(c) is { } im)
                {
                    var src = ImageFor(L, c, im, w, h);
                    if (!ReferenceEquals(L.Fx.Img.Source, src)) { L.Fx.Img.Source = src; }
                    L.Fx.Vignette(c.Grade, w, h);
                    L.Fx.Crop(c, t, w, h);
                }
                ApplyLook(L.El, L.Fx, look, ix, iy, w, h, irot);
                Shadow(L.El, c, ix, iy, w, h, irot, c.Track);
            }
        }
        SyncMasks(t);
    }

    /// <summary>A picture as the export draws it: graded with the clip's LUT; with a drawn look, rendered by ffmpeg at the size it is shown
    /// (the export's size at 720 lines) through the same graph, in the background (the plain picture shows meanwhile).</summary>
    ImageSource? ImageFor(Layer L, Clip c, MediaItem m, double w, double h)
    {
        var lut = Grade.For(c.Grade);
        bool pin = Warp.InDecoder(c), toon = Looks.InDecoder(c, pin);      // (key / drawn look / look effects / matte pin: through ffmpeg, the export's graph)
        if (lut == null && !toon) return L.Original;
        int pw = ExportSettings.Even(w), ph = ExportSettings.Even(h);
        string key = (lut?.Sig ?? "") + "|" + (toon ? Toon.Sig(c.Toon) + "|" + Keyer.Sig(c.Key) + "|" + Looks.Sig(c.Looks) + "|" + (pin ? Pin.Sig(c.Pin) + Lens.Sig(Lens.At(c, c.Start)) : "") + $"|{pw}x{ph}" : "");
        if (key == L.ProcKey) return L.Processed ?? L.Original;
        if (!toon) { L.Processed = Grade.ApplyTo(L.Original!, lut!); L.ProcKey = key; return L.Processed; }
        if (L.ProcBusy == key || ffmpeg == null) return L.Processed ?? L.Original;
        L.ProcBusy = key;
        string path = m.Path, graph = Looks.DecoderGraph(c, pw, ph, false, pin), ff = ffmpeg;
        Task.Run(() => ToonStill(ff, path, pw, ph, graph)).ContinueWith(t =>
        {
            if (L.ProcBusy != key) return;
            L.ProcBusy = "";
            if (t.Result is not { } px) return;
            if (lut != null) Grade.Apply(lut, px, pw, ph, pw * 4);
            var wb = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, pw, ph), px, pw * 4, 0); wb.Freeze();
            L.Processed = wb; L.ProcKey = key;
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return L.Processed ?? L.Original;
    }

    /// <summary>Straight BGRA → premultiplied (WPF's Pbgra32), in place.</summary>
    static void Premultiply(byte[] px, int w, int h)
    {
        Parallel.For(0, h, y =>
        {
            for (int i = y * w * 4, e = i + w * 4; i < e; i += 4)
            {
                int a = px[i + 3];
                if (a == 255) continue;
                if (a == 0) { px[i] = px[i + 1] = px[i + 2] = 0; continue; }
                px[i] = (byte)((px[i] * a + 127) / 255); px[i + 1] = (byte)((px[i + 1] * a + 127) / 255); px[i + 2] = (byte)((px[i + 2] * a + 127) / 255);
            }
        });
    }

    static byte[]? ToonStill(string ff, string path, int w, int h, string graph)
    {
        try
        {
            var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-v", "error", "-nostdin", "-i", path, "-vf", $"scale={w}:{h},{graph},format=bgra", "-frames:v", "1", "-f", "rawvideo", "pipe:1" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
            var buf = new byte[w * h * 4]; int got = 0; var s = p.StandardOutput.BaseStream;
            while (got < buf.Length) { int n = s.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
            p.WaitForExit();
            return got == buf.Length ? buf : null;
        }
        catch { return null; }
    }

    string[] maskKey = Array.Empty<string>();
    /// <summary>A video / picture track matte on track k: its matte bitmap (brightness or alpha of what it shows, crop included, inverted for a
    /// cut-out) at its placement masks everything below; outside the clip a reveal hides, a cut-out keeps. Fades blend it like a title's.
    /// The export builds the same matte from the clip (Exporter: track matte).</summary>
    void ClipMatte(int k, Clip c, double t)
    {
        var mb = c.Kind == ClipKind.Video ? LayerOf(c.Id)?.MatteWb : ImageMatte(c, t);
        if (mb == null) return;                                           // (frame not decoded yet: keep what is there)
        double f = Math.Clamp(Anim.Value(c, AnimProp.Opacity, t) / 100 * c.Fade(t), 0, 1);
        var (x, y, w, h, rot) = Editor.Placement(Ed.MediaOf(c), c, t);
        string key = FormattableString.Invariant($"{x:0.###}|{y:0.###}|{w:0.###}|{h:0.###}|{rot:0.###}|{c.Mask}|{f:0.000}|{Editor.W}");
        if (ReferenceEquals(mb, maskBmp[k]) && maskKey[k] == key) return;   // (the bitmap itself updates with every frame)
        maskBmp[k] = mb; maskKey[k] = key;
        var full = new Rect(0, 0, Editor.W, Editor.H); var r = new Rect(x, y, w, h);
        Transform? rt = Math.Abs(rot) > 1e-6 ? new RotateTransform(rot, x + w / 2, y + h / 2) : null;
        var dg = new DrawingGroup();
        if (f < 0.999) dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Colors.White) { Opacity = 1 - f }, null, new RectangleGeometry(full)));
        if (c.Mask == MaskMode.CutOut) dg.Children.Add(new GeometryDrawing(Brushes.White, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(r, 0, 0, rt))));
        var img = new DrawingGroup { Transform = rt }; img.Children.Add(new ImageDrawing(mb, r));
        dg.Children.Add(img);
        below[k].OpacityMask = new DrawingBrush(dg) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = full, ViewportUnits = BrushMappingMode.Absolute, Viewport = full };
    }

    /// <summary>A picture's matte bitmap (made once per setting; ≤ 1920 px wide).</summary>
    WriteableBitmap? ImageMatte(Clip c, double t)
    {
        if (!layers.TryGetValue(c.Id, out var L) || L.Original is not { } src) return null;
        string key = $"{c.MatteLuma}|{c.Mask}|" + (Crop.Applied(c) ? Crop.Sig(Crop.At(c, t), c.CropFeather, 0, 0) : "");
        if (key == L.MatteKey && L.MatteWb != null) return L.MatteWb;
        BitmapSource b = src.PixelWidth > 1920 ? new TransformedBitmap(src, new ScaleTransform(1920.0 / src.PixelWidth, 1920.0 / src.PixelWidth)) : src;
        var conv = new FormatConvertedBitmap(b, PixelFormats.Pbgra32, null, 0);           // (premultiplied = over black, like the export)
        int w = conv.PixelWidth, h = conv.PixelHeight; var px = new byte[w * h * 4]; conv.CopyPixels(px, w * 4, 0);
        WriteMatte(ref L.MatteWb, px, w, h, c, t, true);
        L.MatteKey = key;
        return L.MatteWb;
    }

    /// <summary>Matte of a frame (BGRA; colour over black): white with alpha = its brightness (0.299 R + 0.587 G + 0.114 B, the export's
    /// colorchannelmixer) or its alpha, times the crop, inverted for a cut-out. Premultiplied, ready for an opacity mask.</summary>
    static void WriteMatte(ref WriteableBitmap? wb, byte[] pix, int w, int h, Clip c, double t, bool hasAlpha = false)
    {
        if (wb == null || wb.PixelWidth != w || wb.PixelHeight != h) wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        var m = FramePool.Rent(w * h * 4);                                // (pooled: video mattes run every frame)
        byte[]? crop = null;
        if (Crop.Applied(c)) { crop = FramePool.Rent(w * h); Crop.Fill(Crop.At(c, t), c.CropFeather, w, h, crop, 1); }
        bool luma = c.MatteLuma, alpha = hasAlpha || Keyer.Active(c.Key), invert = c.Mask == MaskMode.CutOut;
        Parallel.For(0, h, y =>
        {
            for (int x = 0, i = y * w * 4; x < w; x++, i += 4)
            {
                int v = luma ? (int)Math.Round(0.299 * pix[i + 2] + 0.587 * pix[i + 1] + 0.114 * pix[i]) : alpha ? pix[i + 3] : 255;
                if (crop != null) v = (v * crop[y * w + x] + 127) / 255;
                if (invert) v = 255 - v;
                m[i] = m[i + 1] = m[i + 2] = m[i + 3] = (byte)Math.Clamp(v, 0, 255);
            }
        });
        wb.WritePixels(new Int32Rect(0, 0, w, h), m, w * 4, 0);
        FramePool.Return(m); if (crop != null) FramePool.Return(crop);
    }

    /// <summary>Text masks (track mattes): a masking title on any track above V1 sets the opacity mask of everything below it.
    /// Opacity and fades blend the effect in and out: alpha = matte + (1 - strength)(1 - matte), same as the exporter.</summary>
    void SyncMasks(double t)
    {
        for (int k = 0; k < below.Length; k++)
        {
            var c = Ed.Clips.FirstOrDefault(x => x.Mask != MaskMode.None && x.Kind is ClipKind.Text or ClipKind.Video or ClipKind.Image && x.Track == k && Covers(x, t) && !Ed.TrackOff[k]);
            if (c == null) { if (below[k].OpacityMask != null) below[k].OpacityMask = null; maskBmp[k] = null; maskKey[k] = ""; continue; }
            if (c.Kind != ClipKind.Text) { ClipMatte(k, c, t); continue; }
            maskKey[k] = "";
            bool animT = Anim.AnyAnimated(c, Anim.Visual), animS = Shapes.Animated(c);
            var mo = animT ? TitleVisual.MotionAt(c, t) : TitleVisual.MotionOf(c);
            double? local = animS ? t - c.Start : null;                               // (moving shape layers: the matte at this moment, like the export's per-frame render)
            var sig = TitleVisual.Signature(c, true) + $"|{mo}|{local}";               // (motion isn't in Signature)
            if (!mattes.TryGetValue(c.Id, out var mt) || mt.sig != sig) mattes[c.Id] = mt = (sig, TitleVisual.Render(c, matte: true, motion: mo, local: local));
            double f = Math.Clamp(Anim.Value(c, AnimProp.Opacity, t) / 100 * c.Fade(t), 0, 1);
            if (ReferenceEquals(mt.bmp, maskBmp[k]) && Math.Abs(f - maskF[k]) < 0.002) continue;
            maskBmp[k] = mt.bmp; maskF[k] = f;
            var full = new Rect(0, 0, Editor.W, Editor.H);
            var dg = new DrawingGroup();
            if (f < 0.999) dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Colors.White) { Opacity = 1 - f }, null, new RectangleGeometry(full)));
            dg.Children.Add(new ImageDrawing(mt.bmp, full));
            // absolute viewport: map the matte to frame coordinates (a relative mask would stretch to the content bounds,
            // which grow with effects such as a blurred clip on the track)
            var brush = new DrawingBrush(dg) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = full,
                                               ViewportUnits = BrushMappingMode.Absolute, Viewport = full };
            brush.Freeze();
            below[k].OpacityMask = brush;
        }
    }

    /// <summary>Diagnostics: source time of the frame on screen for the top visible video, and where it should be.</summary>
    public (double shown, double expected, string clip, bool full)? ActiveVideo(double t)
    {
        var c = Ed.Clips.Where(c => Shows(c, t)).OrderBy(c => c.Track).FirstOrDefault();
        if (c == null) return null;
        double want = Ed.MediaOf(c) is { } m && NeedFor(c, m, true, t, 0, 0) is { } nd ? nd.Src : Src(c, t);   // (remapped: the time in its rendered file / native grid)
        if (LayerOf(c.Id) is not { } L || double.IsNaN(L.ShownSrc)) return (double.NaN, want, c.Id, false);   // video due but nothing valid shown
        return (L.ShownSrc, want, c.Id, L.ShownFull);
    }

    public void DisposeAll()
    {
        foreach (var L in vpool) { L.Fs?.Dispose(); Unplace(L.Host); }
        vpool.Clear();
    }
}
