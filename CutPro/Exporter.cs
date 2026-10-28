using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CutPro;

/// <summary>Renders the timeline to MP4 (H.264/AAC) by generating an ffmpeg filter graph.</summary>
public static class Exporter
{
    static string F(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
    /// <summary>ffmpeg gblur sigma equivalent to a WPF BlurEffect radius (measured against WPF renders, see FxTest).</summary>
    public static double BlurSigma(double radius) => 0.37 * radius + 0.9;

    public static string? FindFfmpeg()
    {
        var saved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CutPro", "ffmpeg.txt");
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") };
        if (File.Exists(saved)) candidates.Add(File.ReadAllText(saved).Trim());
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links\ffmpeg.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), "ffmpeg.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    public static void RememberFfmpeg(string path)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CutPro");
        Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "ffmpeg.txt"), path);
    }


    /// <summary>Export size of a video/image clip's picture (output pixels): the layer base size for animated / rotated clips,
    /// else its placed size × the output scale. MaskExport renders the masks at this size.</summary>
    public static (int w, int h) ClipSize(MediaItem? m, Clip c, double scale, LayerPlan? lp = null)
    {
        if (lp != null) return (lp.Bw, lp.Bh);
        var (_, _, pw, ph, _) = Editor.Placement(m, c, c.Start);
        return (ExportSettings.Even(pw * scale), ExportSettings.Even(ph * scale));
    }

    /// <summary>True when a clip gets the blurred, filled copy behind it (Shorts "Fit + blur": V1 pictures of another shape).</summary>
    public static bool BlurBackdrop(MediaItem? m, Clip c) =>
        Editor.Reframe == ReframeMode.FitBlur && c.Track == Tracks.V1 && c.Kind is ClipKind.Video or ClipKind.Image && m is { Width: > 0, Height: > 0 }
        && Math.Abs(m.Width / (double)m.Height - Editor.W / (double)Editor.H) > 0.02;
    /// <summary>Blur radius (logical px) and brightness of the Fit + blur backdrop (the preview uses the same numbers).</summary>
    public const double BackdropBlur = 40, BackdropLevel = 0.8;

    /// <summary>Runs WPF drawing on an STA thread (Build runs on a worker thread during export). The sequence format of a nested render
    /// (<see cref="SeqScope"/>) goes along.</summary>
    internal static void Sta(Action a)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) { a(); return; }
        Exception? err = null;
        var scope = SeqScope.Current;
        var t = new Thread(() => { try { using (SeqScope.Use(scope)) a(); } catch (Exception ex) { err = ex; } });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (err != null) throw err;
    }

    /// <summary>A clip's crop as a grey mask at its export size (<see cref="Crop.Fill"/>, the preview's mask): a PGM when it doesn't move, else
    /// a lossless FFV1 video with one frame per sequence frame.</summary>
    static string CropMask(Clip c, int w, int h, bool moving, List<string> temps)
    {
        string file = Path.Combine(Path.GetTempPath(), $"cutpro_crop_{Guid.NewGuid().ToString("N")[..8]}" + (moving ? ".mkv" : ".pgm"));
        temps.Add(file);
        var g = new byte[w * h];
        if (!moving) { Crop.Fill(Crop.At(c, c.Start), c.CropFeather, w, h, g, 1); Crop.WritePgm(file, g, w, h); return file; }
        var psi = new ProcessStartInfo(FindFfmpeg()!) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "gray", "-s", $"{w}x{h}", "-r", TimeUtil.Rate, "-i", "-", "-c:v", "ffv1", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        var stdin = p.StandardInput.BaseStream;
        int n = Math.Max(1, (int)Math.Round(c.Dur * TimeUtil.Fps));
        string last = "";
        for (int k = 0; k < n; k++)
        {
            var s = Crop.At(c, c.Start + k / TimeUtil.Fps);                // (the preview's crop at that frame's time)
            var sig = Crop.Sig(s, c.CropFeather, w, h);
            if (sig != last) { Crop.Fill(s, c.CropFeather, w, h, g, 1); last = sig; }
            stdin.Write(g, 0, g.Length);
        }
        stdin.Close(); p.WaitForExit();
        return file;
    }

    /// <summary>A shape transition's mask video at the output size (W×H output px, s = output px per logical px): frame j of the window =
    /// <see cref="Transitions.Shape"/> at progress j / n, drawn by WPF like the preview's clip region (white = the incoming composite).</summary>
    static string TransitionMask(Transitions.Win w, int n, int W, int H, double s, List<string> temps)
    {
        string file = Path.Combine(Path.GetTempPath(), $"cutpro_tmask_{Guid.NewGuid().ToString("N")[..8]}.mkv");
        temps.Add(file);
        var psi = new ProcessStartInfo(FindFfmpeg()!) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "gray", "-s", $"{W}x{H}", "-r", TimeUtil.Rate, "-i", "-", "-c:v", "ffv1", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        var stdin = p.StandardInput.BaseStream;
        Sta(() =>
        {
            var px = new byte[W * H * 4]; var g = new byte[W * H];
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(W, H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            for (int j = 0; j < n; j++)
            {
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new System.Windows.Rect(0, 0, W, H));
                    dc.PushTransform(new System.Windows.Media.ScaleTransform(s, s));
                    dc.DrawGeometry(System.Windows.Media.Brushes.White, null, Transitions.Shape(w.D, j / (double)n, Editor.W, Editor.H));
                    dc.Pop();
                }
                rtb.Clear(); rtb.Render(dv); rtb.CopyPixels(px, W * 4, 0);
                for (int i = 0; i < g.Length; i++) g[i] = px[i * 4 + 1];
                stdin.Write(g, 0, g.Length);
            }
        });
        stdin.Close(); p.WaitForExit();
        return file;
    }

    /// <summary>The exported span: the whole timeline, or In to Out.</summary>
    /// <summary>An ffconcat list showing captions' PNGs (full frame, from <see cref="TitleVisual.PrepareExport"/>) at their frames on the timeline,
    /// a transparent frame between them: one input for a whole track of captions. Durations are whole frames (the fps filter rounds to them).</summary>
    static string CaptionConcat(List<Clip> caps, IReadOnlyDictionary<string, string> titles, int w, int h, List<string> temps)
    {
        var dir = Path.GetTempPath(); string id = Guid.NewGuid().ToString("N")[..8];
        var blank = Path.Combine(dir, $"cutpro_blank_{id}.png");
        var ff = FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-y", "-v", "error", "-f", "lavfi", "-i", $"color=c=black@0:s={w}x{h},format=rgba", "-frames:v", "1", blank }) psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!) { p.StandardError.ReadToEnd(); p.WaitForExit(); }
        temps.Add(blank);
        double fps = TimeUtil.Fps;
        var sb = new StringBuilder("ffconcat version 1.0\n");
        long k = 0;
        void Entry(string file, long frames)
        {
            if (frames <= 0) return;
            // (option framerate: the stream's time base becomes one frame — the image reader's default 1/25 s rounded starts to the wrong frame)
            sb.Append("file '").Append(file.Replace('\\', '/').Replace("'", "'\\''")).Append("'\noption framerate ").Append(TimeUtil.Rate)
              .Append("\nduration ").Append((frames / fps).ToString("0.########", CultureInfo.InvariantCulture)).Append('\n');
            k += frames;
        }
        foreach (var c in caps)
        {
            long a = Math.Max(k, (long)Math.Round(c.Start * fps)), b = (long)Math.Round(c.End * fps);
            Entry(blank, a - k);
            Entry(titles[c.Id], b - a);
        }
        Entry(blank, 1);
        sb.Append("file '").Append(blank.Replace('\\', '/')).Append("'\noption framerate ").Append(TimeUtil.Rate).Append('\n');   // (so the entry before keeps its duration)
        var list = Path.Combine(dir, $"cutpro_captions_{id}.ffconcat");
        File.WriteAllText(list, sb.ToString(), new UTF8Encoding(false)); temps.Add(list);
        return list;
    }

    public static (double t0, double t1) Range(Editor ed, ExportSettings o)
    {
        double total = ed.End, t0 = 0, t1 = total;
        if (o.InOutOnly && ed.MarkIn is double mi0 && ed.MarkOut is double mo0 && mo0 > mi0) { t0 = Math.Max(0, mi0); t1 = Math.Min(total, mo0); }
        return (t0, t1);
    }

    /// <summary>Builds ffmpeg arguments + filter script for the current timeline.</summary>
    /// <param name="titles">clip id → PNG / FFV1 from <see cref="TitleVisual.PrepareExport"/> (rendered at the same scale)</param>
    /// <param name="masks">"clipId|maskId" → mask video from <see cref="MaskExport.Prepare"/> (same scale)</param>
    /// <param name="nests">nested clips: clip id → the sequence's rendered picture (<see cref="Nest.PrepareExport"/>)</param>
    public static (List<string> args, string script, List<string> temps) Build(Editor ed, string output, IReadOnlyDictionary<string, string>? titles = null,
                                                                               IReadOnlyDictionary<string, string>? masks = null, ExportSettings? opts = null,
                                                                               IReadOnlyDictionary<string, string>? remaps = null,
                                                                               IReadOnlyDictionary<string, Nest.Picture>? nests = null)
    {
        var o = opts ?? ExportSettings.Default;
        double S = o.Scale;                               // output pixels per logical pixel
        int W = o.OutW, H = o.OutH;
        string fps = TimeUtil.Rate, tb = TimeUtil.Tb;
        // transparent background (a nested sequence's render): the composite carries alpha (PA); cross-fades and shape wipes mix
        // premultiplied, mattes multiply the alpha instead of putting the result over black
        bool alpha = o.Alpha || o.WantsAlpha;
        // the composite: 4:4:4 (full colour: every overlay keeps its chroma, the encoder subsamples once at the end) or 4:2:0 (faster);
        // overlays default to yuv420, so every one onto the composite names the format (OV)
        bool full = o.Full444;
        string P = full ? "yuv444p" : "yuv420p", PA = full ? "yuva444p" : "yuva420p", OV = full ? ":format=yuv444" : "";
        string clear = alpha ? "black@0" : "black", cfmt = alpha ? PA : P;
        // a 4:2:0 overlay puts layers on even pixels ((int)x & ~1, y too) — the grid the preview places on (Editor.Placement, push offsets);
        // the 4:4:4 composite keeps it: positions truncated the same way (Ex: expressions, Ei: whole numbers)
        string Ex(string e) => full ? $"2*floor(trunc({e})/2)" : e;
        int Ei(int v) => full ? v & ~1 : v;
        // the output subsampled (H.264 / HEVC / ProRes 422 …): the composite leaves by an area average, so 4:2:0 footage that entered by chroma
        // repetition (see Chroma) comes back exactly
        string? outFmt = o.AudioOnly ? null : o.OutPixFmt;
        bool subOut = outFmt != null && (outFmt.Contains("420") || outFmt.Contains("422") || outFmt is "nv12" or "p010le");
        // same-size clips: the chroma upsampled the way the preview's decoder does it — a plain clip goes to BGR with repeated chroma (swscale's
        // unscaled converter), one whose decoder runs a graph (key, looks, warp) through its bilinear scale, a picture through Windows' decoder
        string Chroma(Clip c) => c.Kind == ClipKind.Image || Looks.InDecoder(c, Warp.InDecoder(c)) ? "bilinear" : "neighbor";
        output = Path.GetFullPath(o.OutputPath(output));  // ffmpeg runs in the temp folder (an image sequence: its frame number pattern)
        double total = ed.End;
        var args = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1", "-nostats" };
        var temps = new List<string>();
        var sb = new StringBuilder();
        int inputs = 0;
        var inputIndex = new Dictionary<string, int>();

        int AddInput(Clip c, MediaItem m)
        {
            if (inputIndex.TryGetValue(c.Id, out var i)) return i;
            if (c.Kind == ClipKind.Image) args.AddRange(new[] { "-loop", "1", "-framerate", fps, "-t", F(c.Dur) });
            if (remaps != null && c.Kind == ClipKind.Video && remaps.TryGetValue(c.Id, out var rf)) { args.AddRange(new[] { "-i", rf }); inputIndex[c.Id] = inputs; return inputs++; }   // speed / reverse / hold: its rendered frames
            if (nests != null && c.Kind == ClipKind.Video && nests.TryGetValue(c.Id, out var np)) { args.AddRange(new[] { "-i", np.File }); inputIndex[c.Id] = inputs; return inputs++; }   // nested sequence: its render
            if (m.Type == ClipKind.Video && c.Kind == ClipKind.Video) args.AddRange(Decoders.ExportInputArgs(m.PlayDecoder));   // GPU decode for heavy files
            args.AddRange(new[] { "-i", m.Path });
            inputIndex[c.Id] = inputs; return inputs++;
        }
        int AddMediaInput(MediaItem m) { args.AddRange(new[] { "-i", m.Path }); return inputs++; }   // (sound of a video clip whose picture is rendered)

        string cur = "b0";
        // transitions: clips extended over them (same ids), windows per track
        var plan = Transitions.Compute(ed);
        var xclips = Transitions.Extended(ed.Clips, plan);
        if (!o.AudioOnly)
        {
            sb.AppendLine($"color=c={clear}:s={W}x{H}:r={fps}:d={F(total)},format={cfmt}[b0]");
            int bn = 0, vn = 0, xn = 0;
            // video: bottom track (V1 = index 2) first
            var vclips = xclips.Where(c => c.Kind != ClipKind.Audio && !ed.TrackOff[c.Track] && (o.BurnCaptions || !Captioning.IsCaption(c)))
                               .OrderByDescending(c => c.Track).ThenBy(c => c.Start).ToList();
            // plain captions (no fades, opacity, motion, blend, matte or transition): each track's go in as ONE input — their PNGs one after another
            // through the concat demuxer, transparent between them — instead of one input and one overlay per caption (a sermon has hundreds)
            var capGroups = new Dictionary<string, List<Clip>>();
            if (titles != null)
                foreach (var g in vclips.Where(c => Captioning.IsCaption(c) && c.FadeIn <= 0 && c.FadeOut <= 0 && c.Opacity >= 100 && c.Blend == BlendMode.Normal && c.Mask == MaskMode.None
                                                    && plan.HeadOf(c) == null && plan.TailOf(c) == null && !Anim.AnyAnimated(c, Anim.Visual)
                                                    && titles.TryGetValue(c.Id, out var tp) && tp.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                                        .GroupBy(c => c.Track).Where(g => g.Count() >= 3))
                {
                    var list = g.OrderBy(c => c.Start).ToList();
                    foreach (var c in list) capGroups[c.Id] = list;
                }
            var capDone = new HashSet<int>();
            // Zoom Out / Spin Away move the outgoing shot over the incoming one: composite the incoming one first
            foreach (var w in plan.Wins.Where(w => w.D.Fx == Transitions.Fx.Move && Transitions.ATop(w.D) && w.A != null && w.B != null))
            {
                int ia = vclips.FindIndex(x => x.Id == w.A!.Id), ib = vclips.FindIndex(x => x.Id == w.B!.Id);
                if (ia >= 0 && ib > ia) { var bc = vclips[ib]; vclips.RemoveAt(ib); vclips.Insert(ia, bc); }
            }
            // dissolve / wipe / iris = ffmpeg xfade between the track composite with the outgoing clip and the composite of the
            // tracks below + the incoming clip ("lower" = the composite before this track, split once per transition); shape transitions
            // (clock wipe, barn doors, …) merge the same two composites through a mask video of the preview's shape
            var xwins = plan.Wins.Where(w => (w.D.XFade != null || w.D.Fx == Transitions.Fx.Mask) && !ed.TrackOff[w.Track]).ToList();
            var lowerOf = new Dictionary<Transitions.Win, string>();
            var usedLower = new HashSet<string>();
            var bWin = xwins.Where(w => w.B != null).ToDictionary(w => w.B!.Id);
            var aOnly = xwins.Where(w => w.B == null).ToDictionary(w => w.A!.Id);
            var dipAfter = plan.Wins.Where(w => w.D.Fx == Transitions.Fx.Dip && !ed.TrackOff[w.Track]).GroupBy(w => (w.B ?? w.A)!.Id).ToDictionary(g => g.Key, g => g.ToList());
            int curTrack = -1;
            string XFade(string a, string lowerB, Transitions.Win w)
            {
                if (w.D.Fx == Transitions.Fx.Mask)
                {
                    // A's composite until the window, the two merged through the shape's mask video during it, then B's composite
                    // (segments: frames before the window are dropped by the trims as they pass, so nothing waits in memory)
                    int k0 = (int)Math.Round(w.T0 * TimeUtil.Fps), k1 = Math.Max(k0 + 1, (int)Math.Round(w.T1 * TimeUtil.Fps));
                    var mfile = TransitionMask(w, k1 - k0, W, H, S, temps);
                    args.AddRange(new[] { "-i", mfile }); int mi = inputs++;
                    string x = $"xm{xn}";
                    sb.AppendLine($"[{a}]split[{x}a1][{x}a2]");
                    sb.AppendLine($"[{lowerB}]split[{x}b2][{x}b3]");
                    if (k0 > 0) sb.AppendLine($"[{x}a1]trim=end_frame={k0},setpts=PTS-STARTPTS[{x}s1]"); else sb.AppendLine($"[{x}a1]nullsink");
                    // (the mask becomes B's alpha and B is overlaid in 4:2:0 like any clip — no round trip through RGB)
                    sb.AppendLine($"[{x}a2]trim=start_frame={k0}:end_frame={k1},setpts=PTS-STARTPTS[{x}ma]");
                    sb.AppendLine($"[{x}b2]trim=start_frame={k0}:end_frame={k1},setpts=PTS-STARTPTS[{x}mb]");
                    sb.AppendLine($"[{x}b3]trim=start_frame={k1},setpts=PTS-STARTPTS[{x}s3]");
                    if (alpha)
                    {
                        // transparent background: a select, premultiplied — A·(1 − m) + B·m (B's empty parts must not show A, as in the preview)
                        sb.AppendLine($"[{x}ma]format=yuva444p,premultiply=inplace=1[{x}pa]");
                        sb.AppendLine($"[{x}mb]format=yuva444p,premultiply=inplace=1[{x}pb]");
                        sb.AppendLine($"[{mi}:v]format=gray,settb={tb},setpts=N,split=4[{x}k0][{x}k1][{x}k2][{x}k3]");
                        sb.AppendLine($"[{x}k0][{x}k1][{x}k2][{x}k3]mergeplanes=0x00102030:yuva444p[{x}mm]");   // (the mask in every plane, values untouched)
                        sb.AppendLine($"[{x}pa][{x}pb][{x}mm]maskedmerge,unpremultiply=inplace=1,format={PA}[{x}s2]");
                    }
                    else
                    {
                        sb.AppendLine($"[{mi}:v]format=gray,settb={tb},setpts=N[{x}mk]");
                        sb.AppendLine($"[{x}mb][{x}mk]alphamerge[{x}mba]");
                        sb.AppendLine($"[{x}ma][{x}mba]overlay=eof_action=pass{OV},format={P}[{x}s2]");
                    }
                    sb.AppendLine((k0 > 0 ? $"[{x}s1]" : "") + $"[{x}s2][{x}s3]concat=n={(k0 > 0 ? 3 : 2)}:v=1:a=0,settb={tb},setpts=N[xo{xn}]");
                    return $"xo{xn++}";
                }
                if (alpha)
                {
                    // transparent background: the two composites mix premultiplied (straight colours would darken whatever fades against
                    // nothing), over the window's frames only (segments, like the shape wipes: the rest passes untouched)
                    int k0 = (int)Math.Round(w.T0 * TimeUtil.Fps), k1 = Math.Max(k0 + 1, (int)Math.Round(w.T1 * TimeUtil.Fps));
                    string x = $"xf{xn}";
                    sb.AppendLine($"[{a}]split[{x}a1][{x}a2]");
                    sb.AppendLine($"[{lowerB}]split[{x}b2][{x}b3]");
                    if (k0 > 0) sb.AppendLine($"[{x}a1]trim=end_frame={k0},setpts=PTS-STARTPTS[{x}s1]"); else sb.AppendLine($"[{x}a1]nullsink");
                    sb.AppendLine($"[{x}a2]trim=start_frame={k0}:end_frame={k1},setpts=PTS-STARTPTS,format=yuva444p,premultiply=inplace=1[{x}wa]");
                    sb.AppendLine($"[{x}b2]trim=start_frame={k0}:end_frame={k1},setpts=PTS-STARTPTS,format=yuva444p,premultiply=inplace=1[{x}wb]");
                    sb.AppendLine($"[{x}b3]trim=start_frame={k1},setpts=PTS-STARTPTS[{x}s3]");
                    sb.AppendLine($"[{x}wa][{x}wb]xfade=transition={w.D.XFade}:duration={F(w.Dur)}:offset=0,unpremultiply=inplace=1,format={PA}[{x}s2]");
                    sb.AppendLine((k0 > 0 ? $"[{x}s1]" : "") + $"[{x}s2][{x}s3]concat=n={(k0 > 0 ? 3 : 2)}:v=1:a=0,settb={tb},setpts=N[xo{xn}]");
                    return $"xo{xn++}";
                }
                // the incoming composite from the window's start; xfade: A until offset, the blend, then B
                sb.AppendLine($"[{lowerB}]trim=start={F(w.T0)},setpts=PTS-STARTPTS[xb{xn}]");
                sb.AppendLine($"[{a}][xb{xn}]xfade=transition={w.D.XFade}:duration={F(w.Dur)}:offset={F(w.T0)},settb={tb},setpts=N[xo{xn}]");   // exact frame times after the blend
                return $"xo{xn++}";
            }
            void Done(Clip c, string next)
            {
                cur = bWin.TryGetValue(c.Id, out var w) ? XFade(cur, next, w) : next;
                if (aOnly.TryGetValue(c.Id, out var w2)) { cur = XFade(cur, lowerOf[w2], w2); usedLower.Add(lowerOf[w2]); }
                foreach (var d in dipAfter.GetValueOrDefault(c.Id) ?? new())
                {
                    // dip to a colour: a full-frame colour layer above the track, alpha up then down (from / to nothing: one way)
                    string col = d.D.Color == "#FFFFFF" ? "white" : "black", h = F(d.Dur / 2);
                    string ramp = d.A == null ? $"fade=t=out:st=0:d={F(d.Dur)}:alpha=1" : d.B == null ? $"fade=t=in:st=0:d={F(d.Dur)}:alpha=1"
                                : $"fade=t=in:st=0:d={h}:alpha=1,fade=t=out:st={h}:d={h}:alpha=1";
                    sb.AppendLine($"color=c={col}:s={W}x{H}:r={fps}:d={F(d.Dur)},format={PA},{ramp},setpts=PTS+round({F(d.T0)}/TB)[dip{xn}]");
                    sb.AppendLine($"[{cur}][dip{xn}]overlay=eof_action=pass{OV}[dpo{xn}]");
                    cur = $"dpo{xn++}";
                }
            }
            /// push: overlay position follows the window (output pixels, t = timeline time)
            string PushX(Clip c, bool vertical)
            {
                var terms = new List<string>();
                foreach (var w in new[] { plan.HeadOf(c), plan.TailOf(c) })
                {
                    if (w is not { D.Fx: Transitions.Fx.Push }) continue;
                    double sign = vertical ? w.D.Dy : w.D.Dx, span = vertical ? H : W;
                    if (sign == 0) continue;
                    bool isB = w.B?.Id == c.Id;
                    string q = $"(t-{F(w.T0)})/{F(w.Dur)}";
                    terms.Add($"if(between(t,{F(w.T0)},{F(w.T1 - 0.5 / TimeUtil.Fps)}),{(isB ? $"{F(-sign * span)}*(1-{q})" : $"{F(sign * span)}*{q}")},0)");
                }
                return terms.Count == 0 ? "" : "+round(" + string.Join("+", terms) + ")";   // (overlay truncates: 167.9999 would become 166)
            }
            // drop shadow: the clip's alpha (crop, key, masks, fades included) in the shadow colour × opacity, blurred like the preview's BlurEffect,
            // overlaid under the clip at the offset (even output pixels, like every 4:2:0 overlay). Returns the new base and the clip's label.
            (string bse, string clip) ShadowUnder(string bse, string v, Clip c, double x0, double y0, string pushX, string pushY)
            {
                var s = c.Shadow!; var (dx, dy) = Looks.Offset(s);
                double sdx = 2 * Math.Round(dx * S / 2), sdy = 2 * Math.Round(dy * S / 2), sigma = BlurSigma(s.Softness * S);
                int sp = s.Softness > 0.05 ? 2 * (int)Math.Ceiling((3 * sigma + 2) / 2) : 0;
                var col = TitleVisual.ParseColor(s.Color, System.Windows.Media.Colors.Black);
                sb.AppendLine($"[{v}]split[{v}k][{v}q]");
                sb.AppendLine($"[{v}q]format=rgba,lutrgb=r={col.R}:g={col.G}:b={col.B}:a='val*{F(Math.Clamp(s.Opacity / 100, 0, 1))}'"
                              + (sp > 0 ? $",pad=iw+{2 * sp}:ih+{2 * sp}:{sp}:{sp}:color=0x{col.R:X2}{col.G:X2}{col.B:X2}@0,format=gbrap,gblur=sigma={F(sigma)}" : "") + $"[{v}h]");
                sb.AppendLine($"[{bse}][{v}h]overlay=x='{Ex(F(x0 + sdx - sp) + pushX)}':y='{Ex(F(y0 + sdy - sp) + pushY)}':eof_action=pass{OV}[{v}u]");
                return ($"{v}u", $"{v}k");
            }
            // track matte from a video / picture: the clip (0-based frames, no fades) placed on a black frame (luma: brightness 0.299 R + 0.587 G
            // + 0.114 B, as the preview computes it; alpha: on a transparent frame, its alpha), inverted for a cut-out, faded like a title matte
            // (alpha over white: m' = 1 − f + f·m), then multiplied into everything composited so far, over black — kept in RGB channels until
            // one plane is taken (gray ↔ RGB conversions could change the range)
            void TrackMatte(string v, double x0, double y0, Clip c, string next)
            {
                string mm = $"tm{v}";
                bool alpha = !c.MatteLuma;
                string pick = alpha ? "colorchannelmixer=rr=0:gg=0:bb=0:ra=1:ga=1:ba=1,format=rgb24,format=rgba"
                                    : "colorchannelmixer=rr=0.299:rg=0.587:rb=0.114:gr=0.299:gg=0.587:gb=0.114:br=0.299:bg=0.587:bb=0.114";
                string fades = (c.FadeIn > 0 ? $",fade=t=in:st=0:d={F(c.FadeIn)}:alpha=1" : "") + (c.FadeOut > 0 ? $",fade=t=out:st={F(Math.Max(0, c.Dur - c.FadeOut))}:d={F(c.FadeOut)}:alpha=1" : "")
                             + (c.Opacity < 100 ? $",colorchannelmixer=aa={F(c.Opacity / 100)}" : "");
                sb.AppendLine($"color=c=black{(alpha ? "@0" : "")}:s={W}x{H}:r={fps}:d={F(c.Dur)},format=rgba[{mm}c]");
                sb.AppendLine($"[{mm}c][{v}]overlay=x={F(x0)}:y={F(y0)}:format=rgb:eof_action=pass,{pick}{(c.Mask == MaskMode.CutOut ? ",negate" : "")}{fades},setpts=PTS+round({F(c.Start)}/TB)[{mm}i]");
                sb.AppendLine($"color=white:s={W}x{H}:r={fps}:d={F(total)},format=rgba[{mm}w]");
                sb.AppendLine($"[{mm}w][{mm}i]overlay=format=rgb:eof_action=pass,extractplanes=r[{mm}k]");
                MatteBelow(mm, $"{mm}k", next);
            }
            // a matte (gray, white = keep) applied to everything composited so far: over black — or, with a transparent background, multiplied
            // into its alpha
            void MatteBelow(string tag, string matte, string next)
            {
                if (alpha)
                {
                    sb.AppendLine($"[{cur}]format={PA},split[{tag}a][{tag}q]");
                    sb.AppendLine($"[{tag}q]alphaextract[{tag}qa]");
                    sb.AppendLine($"[{tag}qa][{matte}]blend=all_mode=multiply[{tag}ka]");
                    sb.AppendLine($"[{tag}a][{tag}ka]alphamerge[{next}]");
                    return;
                }
                sb.AppendLine($"[{cur}]format={PA}[{tag}a]");
                sb.AppendLine($"[{tag}a][{matte}]alphamerge[{tag}m]");
                sb.AppendLine($"color=black:s={W}x{H}:r={fps}:d={F(total)},format={P}[{tag}b]");
                sb.AppendLine($"[{tag}b][{tag}m]overlay=eof_action=pass{OV},format={P}[{next}]");
            }
            int bmn = 0;
            // blend mode: the clip's layer (timeline times; alpha = fades, opacity, key, crop, masks) is placed on a frame-sized transparent canvas,
            // mixed with the composite below over the clip's frames only (lut2 = Blend.Expr, the preview shader's formula) and alpha-mixed over
            // it — b + (B(t, b) − b)·α — then put back into the composite
            void BlendInto(string bse, string top, string x, string y, Clip c, string next)
            {
                int k0 = (int)Math.Round(c.Start * TimeUtil.Fps), k1 = Math.Max(k0 + 1, (int)Math.Round(c.End * TimeUtil.Fps));
                string b = $"bm{bmn++}", e = Blend.Expr(c.Blend);
                sb.AppendLine($"color=c=black@0:s={W}x{H}:r={fps}:d={F((k1 - k0 + 1) / TimeUtil.Fps)},format=rgba,trim=end_frame={k1 - k0},setpts=PTS+round({F(c.Start)}/TB)[{b}c]");
                // (overlay on a transparent canvas leaves premultiplied colour: un-premultiply, as the layer path does)
                sb.AppendLine($"[{b}c][{top}]overlay=x='{x}':y='{y}':format=rgb:eof_action=pass,format=gbrap,unpremultiply=inplace=1,setpts=PTS-STARTPTS,split[{b}t][{b}u]");
                sb.AppendLine($"[{b}t]format=gbrp[{b}tg]");
                sb.AppendLine($"[{b}u]alphaextract[{b}a]");
                if (alpha)
                {
                    // transparent background: the formula sees the composite premultiplied (what the preview's shader samples), the layer goes
                    // over it, and the result replaces the clip's frames (segments: over the composite again would add it twice where it is clear)
                    sb.AppendLine($"[{bse}]split=3[{b}m][{b}p][{b}n]");
                    if (k0 > 0) sb.AppendLine($"[{b}m]trim=end_frame={k0},setpts=PTS-STARTPTS[{b}s1]"); else sb.AppendLine($"[{b}m]nullsink");
                    sb.AppendLine($"[{b}p]trim=start_frame={k0}:end_frame={k1},setpts=PTS-STARTPTS,format=gbrap,split[{b}g0][{b}h]");
                    sb.AppendLine($"[{b}g0]premultiply=inplace=1,format=gbrp[{b}g]");
                    sb.AppendLine($"[{b}tg][{b}g]lut2=c0='{e}':c1='{e}':c2='{e}'[{b}x]");
                    sb.AppendLine($"[{b}x][{b}a]alphamerge[{b}xa]");
                    sb.AppendLine($"[{b}h][{b}xa]overlay=format=gbrp,format={PA}[{b}s2]");
                    sb.AppendLine($"[{b}n]trim=start_frame={k1},setpts=PTS-STARTPTS[{b}s3]");
                    sb.AppendLine((k0 > 0 ? $"[{b}s1]" : "") + $"[{b}s2][{b}s3]concat=n={(k0 > 0 ? 3 : 2)}:v=1:a=0,settb={tb},setpts=N[{next}]");
                    return;
                }
                sb.AppendLine($"[{bse}]split[{b}m][{b}p]");
                sb.AppendLine($"[{b}p]trim=start_frame={k0}:end_frame={k1},setpts=PTS-STARTPTS,format=gbrp,split[{b}g][{b}h]");
                sb.AppendLine($"[{b}tg][{b}g]lut2=c0='{e}':c1='{e}':c2='{e}'[{b}x]");
                sb.AppendLine($"[{b}x][{b}a]alphamerge[{b}xa]");
                // the blended frames, led by transparent padding up to the clip's first frame: the composite's overlay below gets a frame for
                // every time straight away, so neither branch of the split waits (re-attaching a trimmed part held every earlier frame in memory)
                sb.AppendLine($"[{b}h][{b}xa]overlay=format=gbrp,format={PA},tpad=start={k0}:start_mode=add:color=black@0[{b}r]");
                sb.AppendLine($"[{b}m][{b}r]overlay=eof_action=pass{OV}:enable='between(n,{k0},{k1 - 1})'[{next}]");
            }
            foreach (var c in vclips)
            {
                if (c.Track != curTrack)
                {
                    curTrack = c.Track;
                    var tw = xwins.Where(w => w.Track == curTrack).ToList();
                    if (tw.Count > 0)
                    {
                        var outs = Enumerable.Range(0, tw.Count + 1).Select(k => $"lw{curTrack}_{k}").ToList();
                        sb.AppendLine($"[{cur}]split={outs.Count}" + string.Concat(outs.Select(l => $"[{l}]")));
                        cur = outs[0];
                        for (int k = 0; k < tw.Count; k++) lowerOf[tw[k]] = outs[k + 1];
                    }
                }
                string bse = bWin.TryGetValue(c.Id, out var inW) ? lowerOf[inW] : cur;     // incoming clip of a cross-fade: over the tracks below
                if (bse != cur) usedLower.Add(bse);
                string next = $"b{++bn}";
                if (Adjust.Is(c))
                {
                    // adjustment layer: the composite's frames under it → blur / colour / vignette at its strength (Adjust.Graph, the preview's rule) →
                    // over black (WPF's canvas below has nothing under it) → back in place of those frames
                    if (!Adjust.Works(c)) continue;
                    string? cube = null;
                    if (Grade.For(c.Grade) is { } alut)
                    {
                        cube = $"cutpro_lut_{Guid.NewGuid().ToString("N")[..8]}.cube";
                        Grade.WriteCube(Adjust.Small(alut), Path.Combine(Path.GetTempPath(), cube)); temps.Add(Path.Combine(Path.GetTempPath(), cube));   // (the preview shader's 17³)
                    }
                    string? avig = null; int avi = -1;
                    if (c.Grade is { HasVignette: true } avg)
                    {
                        avig = Path.Combine(Path.GetTempPath(), $"cutpro_vig_{Guid.NewGuid().ToString("N")[..8]}.png");
                        Sta(() => Grade.VignettePng(avg, W, H, avig)); temps.Add(avig);
                        args.AddRange(new[] { "-loop", "1", "-framerate", fps, "-t", F(c.Dur + 1), "-i", avig }); avi = inputs++;
                    }
                    if (cube == null && avig == null && !Adjust.Blurs(c)) continue;
                    int ak0 = (int)Math.Round(c.Start * TimeUtil.Fps), ak1 = Math.Max(ak0 + 1, (int)Math.Round(c.End * TimeUtil.Fps));
                    string aj = $"aj{bn}";
                    if (alpha)
                    {   // transparent background: the adjusted frames replace the clip's frames (segments), alpha kept
                        sb.AppendLine($"[{bse}]split=3[{aj}m][{aj}p][{aj}n]");
                        if (ak0 > 0) sb.AppendLine($"[{aj}m]trim=end_frame={ak0},setpts=PTS-STARTPTS[{aj}s1]"); else sb.AppendLine($"[{aj}m]nullsink");
                        sb.AppendLine($"[{aj}p]trim=start_frame={ak0}:end_frame={ak1},setpts=PTS-STARTPTS[{aj}i]");
                        sb.Append(Adjust.Graph(c, aj, $"{aj}i", $"{aj}o", W, H, S, cube, avig, avi, temps));
                        sb.AppendLine($"[{aj}o]format={PA}[{aj}s2]");
                        sb.AppendLine($"[{aj}n]trim=start_frame={ak1},setpts=PTS-STARTPTS[{aj}s3]");
                        sb.AppendLine((ak0 > 0 ? $"[{aj}s1]" : "") + $"[{aj}s2][{aj}s3]concat=n={(ak0 > 0 ? 3 : 2)}:v=1:a=0,settb={tb},setpts=N[{next}]");
                    }
                    else
                    {
                        sb.AppendLine($"[{bse}]split[{aj}m][{aj}p]");
                        sb.AppendLine($"[{aj}p]trim=start_frame={ak0}:end_frame={ak1},setpts=PTS-STARTPTS[{aj}i]");
                        sb.Append(Adjust.Graph(c, aj, $"{aj}i", $"{aj}o", W, H, S, cube, avig, avi, temps));
                        sb.AppendLine($"color=c=black:s={W}x{H}:r={fps}:d={F((ak1 - ak0 + 1) / TimeUtil.Fps)},format=gbrp,trim=end_frame={ak1 - ak0}[{aj}k]");
                        // (led by black frames up to the clip: the overlay below gets a frame for every time straight away — see BlendInto)
                        sb.AppendLine($"[{aj}k][{aj}o]overlay=format=rgb:eof_action=pass,format={P},tpad=start={ak0}:start_mode=add:color=black[{aj}r]");
                        sb.AppendLine($"[{aj}m][{aj}r]overlay=eof_action=pass{OV}:enable='between(n,{ak0},{ak1 - 1})'[{next}]");
                    }
                    Done(c, next);
                    continue;
                }
                if (c.Kind == ClipKind.Text)
                {
                    // titles are rasterised by TitleVisual (the same renderer as the preview) → PNG per title, or a frame sequence
                    // (FFV1 video, motion and opacity baked in) when the title is animated
                    if (titles == null || !titles.TryGetValue(c.Id, out var png)) continue;
                    if (capGroups.TryGetValue(c.Id, out var caps))
                    {
                        if (!capDone.Add(c.Track)) continue;                       // (the track's captions went in with its first one)
                        int ci = inputs++;
                        args.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", CaptionConcat(caps, titles, W, H, temps) });
                        sb.AppendLine($"[{ci}:v]fps={fps},format={PA}[cap{ci}]");
                        sb.AppendLine($"[{bse}][cap{ci}]overlay=0:0:eof_action=pass{OV}[{next}]");
                        Done(c, next);
                        continue;
                    }
                    bool seq = png.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);
                    if (seq) args.AddRange(new[] { "-i", png });
                    else args.AddRange(new[] { "-loop", "1", "-framerate", fps, "-t", F(c.Dur), "-i", png });
                    int i = inputs++;
                    string fades = (c.FadeIn > 0 ? $",fade=t=in:st=0:d={F(c.FadeIn)}:alpha=1" : "")
                                 + (c.FadeOut > 0 ? $",fade=t=out:st={F(Math.Max(0, c.Dur - c.FadeOut))}:d={F(c.FadeOut)}:alpha=1" : "")
                                 + (c.Opacity < 100 && !seq ? $",colorchannelmixer=aa={F(c.Opacity / 100)}" : "");
                    string head = seq ? $"[{i}:v]settb={tb},setpts=N," : $"[{i}:v]";       // (mkv stores ms: frame times round)
                    if (c.Mask == MaskMode.None)
                    {
                        sb.AppendLine($"{head}format={(c.Blend != BlendMode.Normal ? "rgba" : PA)}{fades},setpts=PTS+round({F(c.Start)}/TB)[t{i}]");
                        if (c.Blend != BlendMode.Normal) BlendInto(bse, $"t{i}", $"0{PushX(c, false)}", $"0{PushX(c, true)}", c, next);
                        else sb.AppendLine($"[{bse}][t{i}]overlay=x='{Ex("0" + PushX(c, false))}':y='{Ex("0" + PushX(c, true))}':eof_action=pass{OV}[{next}]");
                    }
                    else
                    {
                        // text mask (track matte): while the title is on screen its matte (white = keep, faded/opacity-blended
                        // towards white) becomes the alpha of everything composited so far (the layers below), over black
                        sb.AppendLine($"color=white:s={W}x{H}:r={fps}:d={F(total)},format=rgba[mw{i}]");
                        sb.AppendLine($"{head}format=rgba{fades},setpts=PTS+round({F(c.Start)}/TB)[mi{i}]");
                        sb.AppendLine($"[mw{i}][mi{i}]overlay=format=rgb:eof_action=pass,format=gray[mk{i}]");
                        MatteBelow($"tx{i}", $"mk{i}", next);
                    }
                }
                else
                {
                    var m = ed.MediaOf(c); if (m == null || m.Offline) continue;
                    Nest.Picture? np = null;
                    if (m.Seq != null && (c.Kind != ClipKind.Video || nests == null || !nests.TryGetValue(c.Id, out np))) continue;   // (a nested sequence with nothing to show)
                    int i = AddInput(c, m);
                    string v = $"v{vn++}";
                    // animated or rotated clips are composited per frame (LayerPlan); static ones keep the plain placement
                    bool pushes = plan.HeadOf(c) is { D.Fx: Transitions.Fx.Push } || plan.TailOf(c) is { D.Fx: Transitions.Fx.Push }, moves = Transitions.Moves(plan, c);
                    var lp = LayerPlan.Needed(c) || moves ? LayerPlan.For(m, c, S, pushes || moves ? t => Transitions.Look(plan, c, t, _ => null) : null) : null;   // (Move transitions: per frame)
                    var (ow, oh) = ClipSize(m, c, S, lp);
                    // a transition can show the clip past its media (handles): the first / last frame repeats there
                    bool still = c.Kind == ClipKind.Image;                 // looped picture input: starts at 0, as long as the clip
                    bool remapped = np == null && remaps != null && remaps.ContainsKey(c.Id); // rendered frames of the clip's whole range (handles included)
                    double cin = np != null ? c.In - np.Offset : c.In;      // (a nested sequence's render starts at its first rendered frame)
                    double pre = still || remapped ? 0 : Math.Max(0, -cin), post = c.Kind == ClipKind.Video && m.Duration > 0 && !remapped ? Math.Max(0, c.In + c.Dur - m.Duration) : 0;
                    string src = $"[{i}:v]trim=start={F(still || remapped ? 0 : Math.Max(0, cin))}:duration={F(c.Dur - pre)},setpts=PTS-STARTPTS"
                               + (pre > 0 || post > 0 ? $",tpad=start_mode=clone:start_duration={F(pre)}:stop_mode=clone:stop_duration={F(post + 1 / TimeUtil.Fps)},trim=duration={F(c.Dur)}" : "");
                    if (np != null)   // a nested sequence: from its first frame by number, exact frame times (Nest.FrameAt: the preview shows the same frames)
                        src = $"[{i}:v]trim=start_frame={Nest.FirstFrame(c, np.Offset, m.Fps)},setpts=N/(({TimeUtil.Rational(m.Fps)})*TB),trim=duration={F(c.Dur)}"
                            + (post > 0 ? $",tpad=stop_mode=clone:stop_duration={F(post + 1 / TimeUtil.Fps)},trim=duration={F(c.Dur)}" : "");
                    if (BlurBackdrop(m, c))
                    {
                        // Shorts "Fit + blur": a blurred copy filling the frame behind the picture (larger than the frame by the
                        // blur's reach, so its edges don't fade in), darkened — the preview draws the same
                        double sigma = BlurSigma(BackdropBlur * S);
                        int pad = 2 * (int)Math.Ceiling((3 * sigma + 2) / 2);
                        sb.AppendLine($"{src},split[src{v}][bgs{v}]");
                        src = $"[src{v}]null";
                        var bg = new StringBuilder($"[bgs{v}]scale={W + 2 * pad}:{H + 2 * pad}:force_original_aspect_ratio=increase,crop={W + 2 * pad}:{H + 2 * pad},setsar=1,format=gbrp," +
                                                   $"gblur=sigma={F(sigma)},crop={W}:{H}:{pad}:{pad},colorchannelmixer=rr={F(BackdropLevel)}:gg={F(BackdropLevel)}:bb={F(BackdropLevel)},format={PA}");
                        if (c.FadeIn > 0) bg.Append($",fade=t=in:st=0:d={F(c.FadeIn)}:alpha=1");
                        if (c.FadeOut > 0) bg.Append($",fade=t=out:st={F(Math.Max(0, c.Dur - c.FadeOut))}:d={F(c.FadeOut)}:alpha=1");
                        if (c.Opacity < 100) bg.Append($",colorchannelmixer=aa={F(c.Opacity / 100)}");
                        bg.Append($",setpts=PTS+round({F(c.Start)}/TB)[bg{v}]");
                        sb.AppendLine(bg.ToString());
                        sb.AppendLine($"[{bse}][bg{v}]overlay=0:0:eof_action=pass{OV}[bgo{v}]");
                        bse = $"bgo{v}";
                    }
                    // (a nested sequence has transparent parts: scaled premultiplied, so its empty black doesn't creep into the edges — the preview's
                    // decoder does the same)
                    var chain = new StringBuilder(np != null ? $"{src},format=gbrap,premultiply=inplace=1,scale={ow}:{oh},unpremultiply=inplace=1,setsar=1" : $"{src},scale={ow}:{oh}{(m.Width == ow && m.Height == oh ? ":flags=" + Chroma(c) : "")},setsar=1");   // (same size: only the chroma is converted — as the preview does)
                    bool framed = false;                      // fps= applied (exact frame timestamps)
                    // a nested sequence's own alpha: taken off like a key's matte (and multiplied with it), multiplied back once below
                    string? keyAlpha = null;
                    if (np != null)
                    {
                        sb.AppendLine(chain + $",fps={fps},format=gbrap,split[nc{v}][ns{v}]");
                        sb.AppendLine($"[ns{v}]alphaextract[na{v}]");
                        keyAlpha = $"na{v}"; framed = true;
                        chain = new StringBuilder($"[nc{v}]format=gbrp");
                    }
                    // chroma key (the preview's decoder runs the same graph): the matte is taken off here and multiplied back after the colour
                    // work below, which sees the keyed clip's colours just as the preview's does
                    if (Keyer.Active(c.Key))
                    {
                        sb.AppendLine(chain + $"{(framed ? "" : $",fps={fps}")},{Keyer.Graph(c.Key!, oh)},split[kc{v}][ks{v}]");
                        sb.AppendLine($"[ks{v}]alphaextract[ka{v}]");
                        if (keyAlpha != null) { sb.AppendLine($"[ka{v}][{keyAlpha}]blend=all_mode=multiply:shortest=1[kn{v}]"); keyAlpha = $"kn{v}"; }
                        else keyAlpha = $"ka{v}";
                        framed = true;
                        chain = new StringBuilder($"[kc{v}]format=gbrp");   // (the colour branch: its alpha must go — the matte is multiplied back once, below)
                    }
                    // drawn look (cartoon / cel / anime: the preview's decoder runs the same graph) and colour (the LUT the preview applies)
                    if (Looks.ColourGraph(c, oh, v) is { Length: > 0 } cg) chain.Append(",").Append(cg);   // (drawn look + look effects)
                    // lens distortion, then corner pin: warp the picture (the preview's shader does the same after the colour work); the coverage is
                    // alpha, multiplied in once below with the key's matte (warped the same way) — the colour branch drops its alpha
                    string? pinAlpha = null;
                    if (Warp.Active(c))
                    {
                        if (!framed) { chain.Append($",fps={fps}"); framed = true; }
                        if (keyAlpha != null)
                        {   // the key's matte rides along as the picture's alpha: warped with it, it comes out multiplied by the coverage
                            sb.AppendLine(chain + $",format=gbrap[wk{v}]");
                            chain = new StringBuilder($"[wk{v}][{keyAlpha}]alphamerge");
                            keyAlpha = null;
                        }
                        sb.AppendLine(chain + $",{Warp.Graph(c, ow, oh, v, temps)},split[pn{v}][pq{v}]");   // (keyframed: per-frame expressions / commands)
                        sb.AppendLine($"[pq{v}]alphaextract[pa{v}]");
                        pinAlpha = $"pa{v}";
                        chain = new StringBuilder($"[pn{v}]format=gbrp");
                    }
                    if (Grade.For(c.Grade) is { } lut)
                    {
                        var cube = $"cutpro_lut_{Guid.NewGuid().ToString("N")[..8]}.cube";       // (bare name: ffmpeg runs in %TEMP%)
                        Grade.WriteCube(lut, Path.Combine(Path.GetTempPath(), cube)); temps.Add(Path.Combine(Path.GetTempPath(), cube));
                        chain.Append($",lut3d=file={cube}:interp=tetrahedral");
                    }
                    // object masks: an effected copy merged back through each mask video (see MaskExport)
                    if (masks != null)
                    {
                        var mlist = MaskExport.Exported(c).Where(mk => masks.ContainsKey($"{c.Id}|{mk.Id}")).ToList();
                        if (mlist.Count > 0)
                        {
                            string mc = $"mb{v}";
                            sb.AppendLine(chain + $",fps={fps},format=gbrp[{mc}]");      // exact frame timestamps: frame k pairs with mask frame k
                            framed = true;
                            int step = 0;
                            foreach (var mk in mlist)
                            {
                                args.AddRange(new[] { "-i", masks[$"{c.Id}|{mk.Id}"] });
                                int mi = inputs++;
                                string tag = $"{v}_{step++}";
                                if (mk.Effect == MaskFx.CutOut)
                                {
                                    sb.AppendLine($"[{mi}:v]format=gray,settb={tb},setpts=N[mk{tag}]");
                                    sb.AppendLine($"[{mc}]format=gbrap[mca{tag}]");
                                    sb.AppendLine($"[mca{tag}][mk{tag}]alphamerge[mo{tag}]");
                                }
                                else
                                {
                                    sb.AppendLine($"[{mi}:v]format=gray,format=gbrp,settb={tb},setpts=N[mk{tag}]");      // (mkv stores ms: frame times round)
                                    sb.AppendLine($"[{mc}]split[ma{tag}][mf{tag}]");
                                    if (mk.Effect == MaskFx.Grade && Grade.For(mk.Grade) is { } mlut)
                                    {   // a power window: its own LUT, after the clip's
                                        var cube = Path.Combine(Path.GetTempPath(), MaskExport.CubeName(mk));
                                        Grade.WriteCube(mlut, cube); temps.Add(cube);
                                    }
                                    sb.AppendLine(MaskExport.Graph(mk, $"mf{tag}", $"mx{tag}", S));
                                    sb.AppendLine($"[ma{tag}][mx{tag}][mk{tag}]maskedmerge[mo{tag}]");
                                }
                                mc = $"mo{tag}";
                            }
                            chain = new StringBuilder($"[{mc}]null");
                        }
                    }
                    // vignette: the picture WPF draws over the clip in the preview, as a PNG at the clip's output size
                    if (c.Grade is { HasVignette: true } vg)
                    {
                        var vfile = Path.Combine(Path.GetTempPath(), $"cutpro_vig_{Guid.NewGuid().ToString("N")[..8]}.png");
                        Sta(() => Grade.VignettePng(vg, ow, oh, vfile)); temps.Add(vfile);
                        args.AddRange(new[] { "-loop", "1", "-framerate", fps, "-t", F(c.Dur + 1), "-i", vfile });
                        int vi = inputs++;
                        sb.AppendLine(chain + $"[vgi{v}]");
                        sb.AppendLine($"[vgi{v}][{vi}:v]overlay=0:0:format=rgb:shortest=1[vgo{v}]");      // (packed RGBA: a cut-out mask keeps its alpha)
                        chain = new StringBuilder($"[vgo{v}]null");
                    }
                    // flips (after the masks and the vignette, like the preview's flip box), then the crop and the key's matte (both in the
                    // shown picture's space) multiplied into the clip's alpha
                    string flips = (c.FlipH ? ",hflip" : "") + (c.FlipV ? ",vflip" : "");
                    chain.Append(flips);
                    string? alphaIn = null;
                    if (keyAlpha != null && flips != "") { sb.AppendLine($"[{keyAlpha}]{flips[1..]}[kaf{v}]"); keyAlpha = $"kaf{v}"; }
                    if (pinAlpha != null && flips != "") { sb.AppendLine($"[{pinAlpha}]{flips[1..]}[paf{v}]"); pinAlpha = $"paf{v}"; }
                    if (Crop.Applied(c))
                    {
                        bool moving = Crop.Animated(c);
                        if (moving && !framed) { chain.Append($",fps={fps}"); framed = true; }   // (mask frame k pairs with clip frame k)
                        var file = CropMask(c, ow, oh, moving, temps);
                        if (moving) args.AddRange(new[] { "-i", file });
                        else args.AddRange(new[] { "-loop", "1", "-framerate", fps, "-t", F(c.Dur), "-i", file });
                        int ci = inputs++;
                        sb.AppendLine($"[{ci}:v]format=gray{(moving ? $",settb={tb},setpts=N" : "")}[cm{v}]");
                        alphaIn = $"cm{v}";
                    }
                    if (keyAlpha != null)
                    {
                        if (alphaIn != null) { sb.AppendLine($"[{keyAlpha}][{alphaIn}]blend=all_mode=multiply:shortest=1[kcm{v}]"); alphaIn = $"kcm{v}"; }
                        else alphaIn = keyAlpha;
                    }
                    if (pinAlpha != null)
                    {
                        if (alphaIn != null) { sb.AppendLine($"[{pinAlpha}][{alphaIn}]blend=all_mode=multiply:shortest=1[pcm{v}]"); alphaIn = $"pcm{v}"; }
                        else alphaIn = pinAlpha;
                    }
                    if (alphaIn != null)
                    {
                        sb.AppendLine(chain + $",format=gbrap,split[ax{v}][ay{v}]");
                        sb.AppendLine($"[ay{v}]alphaextract[aa{v}]");
                        sb.AppendLine($"[aa{v}][{alphaIn}]blend=all_mode=multiply:shortest=1[ab{v}]");
                        sb.AppendLine($"[ax{v}][ab{v}]alphamerge[ac{v}]");
                        chain = new StringBuilder($"[ac{v}]null");
                    }
                    if (lp != null)
                    {
                        // per-frame layer: rotate at the base size, scale, place on a transparent canvas (premultiplied), blur,
                        // un-premultiply, opacity — every value from Anim.Value for that frame (sendcmd), like the preview
                        if (!framed) chain.Append($",fps={fps}");
                        var (cmdA, cmdB) = lp.WriteCommands(v, temps);
                        chain.Append($",sendcmd=f={cmdA},format=gbrap");
                        if (lp.Rotates) chain.Append($",rotate@r{v}=a={F(lp.R[0] * Math.PI / 180)}:ow={lp.D}:oh={lp.D}:c=black@0");     // (c=none leaves the corners unpainted, not transparent)
                        var (w0, h0, x0, y0) = lp.Box(0);
                        chain.Append($",scale@s{v}=w={w0}:h={h0}:flags=bilinear[L{v}]");
                        sb.AppendLine(chain.ToString());
                        sb.AppendLine($"color=c=black@0:s={W + 2 * lp.Pad}x{H + 2 * lp.Pad}:r={fps}:d={F(c.Dur)},format=rgba[cv{v}]");     // packed RGBA: overlay keeps the alpha (gbrp mode drops it)
                        var lay = new StringBuilder($"[cv{v}][L{v}]overlay@o{v}=x={x0}:y={y0}:format=rgb:eof_action=pass,sendcmd=f={cmdB},format=gbrap");
                        if (lp.Blurs) lay.Append($",gblur@g{v}=sigma={F(lp.Sigma(0))}");
                        if (c.Mask != MaskMode.None)
                        {   // a track matte: its frames (0-based, no fades) make the matte for everything below
                            lay.Append($",unpremultiply=inplace=1,format=rgba[{v}]");
                            sb.AppendLine(lay.ToString());
                            TrackMatte(v, -lp.Pad, -lp.Pad, c, next);
                            Done(c, next);
                            continue;
                        }
                        lay.Append($",unpremultiply=inplace=1,format={(c.Blend != BlendMode.Normal ? "gbrap" : PA)}");   // (a blended layer stays RGB: the blend works in RGB)
                        if (c.FadeIn > 0) lay.Append($",fade=t=in:st=0:d={F(c.FadeIn)}:alpha=1");
                        if (c.FadeOut > 0) lay.Append($",fade=t=out:st={F(Math.Max(0, c.Dur - c.FadeOut))}:d={F(c.FadeOut)}:alpha=1");
                        if (lp.Fades) lay.Append($",colorchannelmixer@c{v}=aa={F(lp.O[0] / 100)}");
                        lay.Append($",setpts=PTS+round({F(c.Start)}/TB)[{v}]");
                        sb.AppendLine(lay.ToString());
                        string vl = v;
                        if (c.Shadow != null) (bse, vl) = ShadowUnder(bse, v, c, -lp.Pad, -lp.Pad, "", "");
                        if (c.Blend != BlendMode.Normal) BlendInto(bse, vl, $"{-lp.Pad}", $"{-lp.Pad}", c, next);
                        else sb.AppendLine($"[{bse}][{vl}]overlay=x={Ei(-lp.Pad)}:y={Ei(-lp.Pad)}:eof_action=pass{OV}[{next}]");
                        Done(c, next);
                        continue;
                    }
                    // Gaussian blur matching the preview's WPF BlurEffect(radius): measured sigma ≈ 0.37·radius + 0.9. WPF blurs
                    // premultiplied colour with transparency outside the clip, so: RGB + alpha, transparent padding (the blur spreads
                    // past the clip like the preview), blur, un-premultiply. (Straight-alpha blurring darkened the edges twice.)
                    int bp = 0;
                    if (c.Blur > 0.05)
                    {
                        double sigma = BlurSigma(c.Blur * S);
                        bp = 2 * (int)Math.Ceiling((3 * sigma + 2) / 2);
                        chain.Append($",format=gbrap,premultiply=inplace=1,pad=iw+{2 * bp}:ih+{2 * bp}:{bp}:{bp}:color=black@0,gblur=sigma={F(sigma)},unpremultiply=inplace=1");   // (premultiplied first: keyed / cropped / cut-out edges don't bleed colour)
                    }
                    if (c.Mask != MaskMode.None)
                    {   // a track matte: its frames (0-based, no fades) make the matte for everything below
                        sb.AppendLine(chain + $",format=rgba[{v}]");
                        var (mx, my, mw, mh, _) = Editor.Placement(m, c, c.Start);
                        TrackMatte(v, 2 * Math.Round((mx * S + (mw * S - ow) / 2) / 2) - bp, 2 * Math.Round((my * S + (mh * S - oh) / 2) / 2) - bp, c, next);
                        Done(c, next);
                        continue;
                    }
                    chain.Append(c.Blend != BlendMode.Normal ? ",format=gbrap" : ",format=" + PA);   // (a blended clip stays RGB: the blend works in RGB)
                    if (c.FadeIn > 0) chain.Append($",fade=t=in:st=0:d={F(c.FadeIn)}:alpha=1");
                    if (c.FadeOut > 0) chain.Append($",fade=t=out:st={F(Math.Max(0, c.Dur - c.FadeOut))}:d={F(c.FadeOut)}:alpha=1");
                    if (c.Opacity < 100) chain.Append($",colorchannelmixer=aa={F(c.Opacity / 100)}");
                    chain.Append($",setpts=PTS+round({F(c.Start)}/TB)[{v}]");
                    sb.AppendLine(chain.ToString());
                    var (px, py, pw, ph, _) = Editor.Placement(m, c, c.Start);
                    double x = 2 * Math.Round((px * S + (pw * S - ow) / 2) / 2), y = 2 * Math.Round((py * S + (ph * S - oh) / 2) / 2);   // (even pixels: a 4:2:0 overlay can only place there; the preview snaps the same way)
                    string vc = v;
                    if (c.Shadow != null) (bse, vc) = ShadowUnder(bse, v, c, x - bp, y - bp, PushX(c, false), PushX(c, true));
                    if (c.Blend != BlendMode.Normal) BlendInto(bse, vc, $"{F(x - bp)}{PushX(c, false)}", $"{F(y - bp)}{PushX(c, true)}", c, next);
                    else sb.AppendLine($"[{bse}][{vc}]overlay=x='{Ex(F(x - bp) + PushX(c, false))}':y='{Ex(F(y - bp) + PushX(c, true))}':eof_action=pass{OV}[{next}]");
                }
                Done(c, next);
            }
            foreach (var l in lowerOf.Values.Where(l => !usedLower.Contains(l))) sb.AppendLine($"[{l}]nullsink");   // (incoming clip skipped)
        }
        string vout = cur;

        // audio = the playback mixer (Mixer.Mix) as filters: clip volume / fades / crossfades, track fader × pan, sum, master (+ loudness normalization).
        // Positions are whole 48 kHz samples (resampled first, like the conformed audio the preview plays), so the two match sample for sample.
        const int AR = Mixer.Rate;
        static long Smp(double t) => (long)Math.Round(t * AR);
        static string G(double v) => v.ToString("0.#########", CultureInfo.InvariantCulture);
        var alabels = new List<string>(); int an = 0;
        var autoLabels = new SortedDictionary<int, List<string>>();       // automated tracks: their clips, summed before the automation
        // tracks with effects: their clips come as one rendered file (AudioDsp.EnsureTrackFile — RunAsync renders it first)
        var msrcs = o.Audio == "none" ? Array.Empty<AudioOut.Src>() : Mixer.Sources(ed, plan, new()).ToArray();
        var fxTracks = msrcs.Where(s => s.Bus != null && s.Sub == null).Select(s => s.Bus!).GroupBy(b => b.Track).ToDictionary(g => g.Key, g => g.First());
        // tracks routed to a submix: their sound comes in the submix's file (AudioDsp.EnsureSubFile)
        bool Routed(int track) => ed.Mix.Channels[track].Output is var r && r >= 0 && r < ed.Mix.Subs.Count;
        var subsUsed = msrcs.Where(s => s.Sub != null).Select(s => s.Sub!).GroupBy(b => b.Index).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.First());
        var xById = xclips.ToDictionary(x => x.Id);
        bool solo = Mixer.AnySolo(ed);
        foreach (var c0 in ed.Clips.Where(c => o.Audio != "none" && Mixer.HasSound(c) && !ed.TrackOff[c.Track] && (!solo || ed.Mix.Channels[c.Track].Solo)))   // (no sound wanted: no audio graph)
        {
            if (fxTracks.ContainsKey(c0.Track) || Routed(c0.Track)) continue;   // (in its track's effected file / its submix's file, below)
            var c = c0.Kind == ClipKind.Audio ? xById[c0.Id] : c0;          // (video transitions leave the sound alone; audio ones lengthen it)
            var m = ed.MediaOf(c); if (m == null || m.Offline || !m.HasAudio || c.Hold) continue;    // (a frozen frame is silent)
            // a nested sequence: its rendered mix (the file the mixer reads — RunAsync renders it first), from the sequence's start
            string? nestKey = m.Seq != null && ed.SeqById(m.Seq) is { } nseq ? Nest.Sound(ed, nseq) : null;
            if (m.Seq != null && nestKey == null) continue;
            // another speed / reversed: the rendered sound the preview plays (AudioCache.EnsureDerived — RunAsync waits for it)
            bool derived = nestKey == null && AudioCache.Derives(c), effected = AudioDsp.Active(c);
            int i;
            if (effected)
            {   // audio effects: the clip's sound processed by the mixer's own effect code (AudioDsp.EnsureFile — RunAsync renders it first)
                string key = nestKey ?? (derived ? "derived|" + AudioCache.DerivedFile(c, m) : m.Path);
                args.AddRange(new[] { "-f", "s16le", "-ar", AR.ToString(), "-ch_layout", "stereo", "-i", AudioDsp.FileFor(key, derived ? 0 : c.In, c.Start, c.End, c.AudioFx!) }); i = inputs++;
            }
            else if (derived) { args.AddRange(new[] { "-f", "s16le", "-ar", AR.ToString(), "-ch_layout", "stereo", "-i", AudioCache.DerivedFile(c, m) }); i = inputs++; }
            else if (nestKey != null) { args.AddRange(new[] { "-f", "s16le", "-ar", AR.ToString(), "-ch_layout", "stereo", "-i", Nest.FileOfKey(nestKey)! }); i = inputs++; }
            else i = c.Kind == ClipKind.Video && remaps?.ContainsKey(c.Id) == true ? AddMediaInput(m) : AddInput(c, m);
            string a = $"a{an++}";
            long s0 = derived || effected ? 0 : Smp(c.In), len = Smp(c.End) - Smp(c.Start);
            var chain = new StringBuilder($"[{i}:a]aformat=sample_rates={AR}:channel_layouts=stereo,atrim=start_sample={s0}:end_sample={s0 + len},asetpts=PTS-STARTPTS");
            if (Anim.IsAnimated(c, AnimProp.Volume))
            {
                // animated volume: the preview mixer's gain table, one value per 240-sample block (AudioOut.GainStep)
                var g = Anim.GainTable(c);
                var cmd = new StringBuilder(); float last = g[0];
                for (int k = 1; k < g.Length; k++)
                    if (Math.Abs(g[k] - last) > 0.0005 || (k == g.Length - 1 && g[k] != last)) { cmd.Append($"{F(k * AudioOut.GainStep - 0.0001)} [enter] volume@av{a} volume {F(g[k])};\n"); last = g[k]; }
                if (cmd.Length == 0) cmd.Append($"{F(c.Dur + 1)} [enter] volume@av{a} volume {F(g[0])};\n");
                var name = $"cutpro_cmd_{Guid.NewGuid().ToString("N")[..8]}_{a}.txt";
                File.WriteAllText(Path.Combine(Path.GetTempPath(), name), cmd.ToString()); temps.Add(Path.Combine(Path.GetTempPath(), name));
                chain.Append($",asetnsamples=n={(int)Math.Round(AudioOut.GainStep * 48000)}:p=0,asendcmd=f={name},volume@av{a}=volume={F(g[0])}");
            }
            else if (Math.Abs(c.Volume - 100) > 0.01) chain.Append($",volume={F(c.Volume / 100)}");
            if (c.FadeIn > 0) chain.Append($",afade=t=in:st=0:d={F(c.FadeIn)}");
            if (c.FadeOut > 0) chain.Append($",afade=t=out:st={F(Math.Max(0, c.Dur - c.FadeOut))}:d={F(c.FadeOut)}");
            // audio crossfades (audio transitions): the clip is lengthened over the window and faded with the curve
            if (plan.HeadOf(c) is { D.Fx: Transitions.Fx.Audio } xh) chain.Append($",afade=t=in:st=0:d={F(xh.Dur)}:curve={(xh.D.Power ? "qsin" : "tri")}");
            if (plan.TailOf(c) is { D.Fx: Transitions.Fx.Audio } xt) chain.Append($",afade=t=out:st={F(xt.T0 - c.Start)}:d={F(xt.Dur)}:curve={(xt.D.Power ? "qsin" : "tri")}");
            var (gl, gr) = Mixer.TrackGains(ed, c.Track);                   // track fader × pan (linear, so per clip = per track)
            bool automated = ed.Mix.Channels[c.Track].Automated;            // (automation: applied to the track's sum below)
            if (!automated && (Math.Abs(gl - 1) > 1e-7 || Math.Abs(gr - 1) > 1e-7)) chain.Append($",pan=stereo|c0={G(gl)}*c0|c1={G(gr)}*c1");
            chain.Append($",adelay=delays={Smp(c.Start)}S:all=1[{a}]");
            sb.AppendLine(chain.ToString());
            if (automated) { if (!autoLabels.TryGetValue(c.Track, out var al)) autoLabels[c.Track] = al = new(); al.Add(a); }
            else alabels.Add(a);
        }
        // tracks with effects: the rendered bus (pre-fader, from timeline 0), then the fader / pan — static here, or the automation below
        foreach (var (tr, tbus) in fxTracks)
        {
            args.AddRange(new[] { "-f", "f32le", "-ar", AR.ToString(), "-ch_layout", "stereo", "-i", AudioDsp.TrackFileFor(msrcs.Where(s => s.Track == tr && s.Bus != null), tbus, ed.End) });
            int ti = inputs++;
            string a = $"a{an++}";
            var (gl, gr) = Mixer.TrackGains(ed, tr);
            bool automated = ed.Mix.Channels[tr].Automated;
            sb.AppendLine($"[{ti}:a]aformat=sample_rates={AR}:channel_layouts=stereo,asetpts=PTS-STARTPTS"
                          + (!automated && (Math.Abs(gl - 1) > 1e-7 || Math.Abs(gr - 1) > 1e-7) ? $",pan=stereo|c0={G(gl)}*c0|c1={G(gr)}*c1" : "") + $"[{a}]");
            if (automated) autoLabels[tr] = new() { a }; else alabels.Add(a);
        }
        // track automation (fader / balance keyframes): the track's clips summed, then each side × its gain per 240-sample block of the timeline
        // (the sum starts at timeline 0, so asetnsamples' blocks are the mixer's Mixer.AutoBlock blocks; asendcmd changes the gain just before
        // the block that needs it) — the tables are the mixer's own (Mixer.Automation); submixes use the same block (their own tables)
        void Automate(List<string> labels, float[] tl, float[] trr, string t)
        {
            string sum = labels.Count == 1 ? $"[{labels[0]}]anull" : string.Concat(labels.Select(l => $"[{l}]")) + $"amix=inputs={labels.Count}:normalize=0:duration=longest";
            string Cmds(float[] g, string target)
            {
                var cmd = new StringBuilder(); float last = g[0];
                for (int k = 1; k < g.Length; k++)
                    if (g[k] != last) { cmd.Append($"{F(k * Mixer.AutoStep - 0.0001)} [enter] {target} volume {G(g[k])};\n"); last = g[k]; }
                if (cmd.Length == 0) cmd.Append($"{F(total + 1)} [enter] {target} volume {G(g[0])};\n");
                var name = $"cutpro_auto_{Guid.NewGuid().ToString("N")[..8]}_{target.Replace('@', '_')}.txt";
                File.WriteAllText(Path.Combine(Path.GetTempPath(), name), cmd.ToString()); temps.Add(Path.Combine(Path.GetTempPath(), name));
                return name;
            }
            sb.AppendLine($"{sum},asetnsamples=n={Mixer.AutoBlock}:p=0,channelsplit=channel_layout=stereo[{t}l][{t}r]");
            sb.AppendLine($"[{t}l]asendcmd=f={Cmds(tl, $"volume@{t}vl")},volume@{t}vl=volume={G(tl[0])}:precision=float[{t}l2]");
            sb.AppendLine($"[{t}r]asendcmd=f={Cmds(trr, $"volume@{t}vr")},volume@{t}vr=volume={G(trr[0])}:precision=float[{t}r2]");
            sb.AppendLine($"[{t}l2][{t}r2]join=inputs=2:channel_layout=stereo:map=0.FL-FL|1.FR-FR[{t}]");
            alabels.Add(t);
        }
        foreach (var (tr, labels) in autoLabels)
            if (Mixer.Automation(ed, tr) is var (tl, trr)) Automate(labels, tl, trr, $"tk{tr}");
        // submixes: their tracks' mix through their effects, rendered by the mixer's own code (AudioDsp.EnsureSubFile — RunAsync first), then
        // their fader / pan (or automation)
        foreach (var (k, sbus) in subsUsed)
        {
            args.AddRange(new[] { "-f", "f32le", "-ar", AR.ToString(), "-ch_layout", "stereo", "-i", AudioDsp.SubFileFor(msrcs, sbus, ed.End) });
            int si = inputs++;
            string a = $"a{an++}";
            bool gains = sbus.AL == null && (Math.Abs(sbus.GL - 1) > 1e-7 || Math.Abs(sbus.GR - 1) > 1e-7);
            sb.AppendLine($"[{si}:a]aformat=sample_rates={AR}:channel_layouts=stereo,asetpts=PTS-STARTPTS" + (gains ? $",pan=stereo|c0={G(sbus.GL)}*c0|c1={G(sbus.GR)}*c1" : "") + $"[{a}]");
            if (sbus.AL != null) Automate(new() { a }, sbus.AL, sbus.AR!, $"sb{k}"); else alabels.Add(a);
        }
        // master fader, then loudness normalization (gain measured by Mixer.Measure before the export; limiter at 4× rate ≈ true peak)
        var apost = new List<string>();
        double norm = o.Normalize ? Math.Pow(10, o.NormGainDb / 20) : 1;
        double mg = Mixer.MasterGain(ed) * norm;
        if (Mixer.MasterOf(ed).Table is { } mtab)
        {   // master automation: the mixer's gain per 240-sample block of the timeline (the sum starts at timeline 0), × the normalization gain
            var cmd = new StringBuilder(); float last = (float)(mtab[0] * norm);
            for (int k = 1; k < mtab.Length; k++)
            {
                float g = (float)(mtab[k] * norm);
                if (g != last) { cmd.Append($"{F(k * Mixer.AutoStep - 0.0001)} [enter] volume@mst volume {G(g)};\n"); last = g; }
            }
            if (cmd.Length == 0) cmd.Append($"{F(total + 1)} [enter] volume@mst volume {G(mtab[0] * norm)};\n");
            var name = $"cutpro_auto_{Guid.NewGuid().ToString("N")[..8]}_master.txt";
            File.WriteAllText(Path.Combine(Path.GetTempPath(), name), cmd.ToString()); temps.Add(Path.Combine(Path.GetTempPath(), name));
            apost.Add($"asetnsamples=n={Mixer.AutoBlock}:p=0,asendcmd=f={name},volume@mst=volume={G(mtab[0] * norm)}:precision=float");
        }
        else if (Math.Abs(mg - 1) > 1e-7) apost.Add($"volume={G(mg)}");
        if (o.Normalize && o.NormLimit) apost.Add($"aresample={AR * 4},alimiter=limit={G(Math.Pow(10, (o.TruePeak - 0.3) / 20))}:attack=1:release=50:level=0:latency=1,aresample={AR}");
        string postChain = apost.Count > 0 ? "," + string.Join(",", apost) : "";
        if (o.Audio == "none") { /* no sound in the file: no audio graph (an unmapped [aout] makes ffmpeg fail) */ }
        else if (alabels.Count == 0) sb.AppendLine($"anullsrc=r={AR}:cl=stereo,atrim=duration={F(total)}[aout]");
        else sb.AppendLine(string.Concat(alabels.Select(a => $"[{a}]")) + $"amix=inputs={alabels.Count}:normalize=0:duration=longest{postChain}[aout]");

        // range (In to Out): trim the finished picture and sound
        string aout = "aout";
        var (t0, t1) = Range(ed, o);
        if (t0 > 0 || t1 < total)
        {
            if (!o.AudioOnly) { sb.AppendLine($"[{vout}]trim=start={F(t0)}:end={F(t1)},setpts=PTS-STARTPTS[vr]"); vout = "vr"; }
            if (o.Audio != "none") { sb.AppendLine($"[aout]atrim=start_sample={Smp(t0)}:end_sample={Smp(t1)},asetpts=PTS-STARTPTS[ar]"); aout = "ar"; }
        }
        if (o.Audio != "none" && o.Channels == "mono") { sb.AppendLine($"[{aout}]pan=mono|c0=0.5*c0+0.5*c1[amono]"); aout = "amono"; }   // (the stereo mix folded down)
        else if (o.Audio != "none" && o.SurroundFile is { } sf)
        {   // 5.1: the mixer's own render of each group (range and normalization included), peaks limited like the stereo mix
            args.AddRange(new[] { "-f", "f32le", "-ar", AR.ToString(), "-ch_layout", "5.1", "-i", sf }); int si = inputs++;
            string lim = o.Normalize && o.NormLimit ? $",aresample={AR * 4},alimiter=limit={G(Math.Pow(10, (o.TruePeak - 0.3) / 20))}:attack=1:release=50:level=0:latency=1,aresample={AR}" : "";
            sb.AppendLine($"[{aout}]anullsink"); sb.AppendLine($"[{si}:a]anull{lim}[a51]"); aout = "a51";   // (the stereo mix isn't used)
        }
        if (alpha && !o.AudioOnly) { sb.AppendLine($"[{vout}]format={PA}[va]"); vout = "va"; }
        else if (full && subOut && outFmt is { } pf)
        {   // 4:4:4 → the encoder's subsampled format by area averaging: a 4:2:0 source's repeated chroma comes back exactly
            sb.AppendLine($"[{vout}]scale=flags=area,format={pf}[vp]"); vout = "vp";
        }
        var script = Path.Combine(Path.GetTempPath(), $"cutpro_filter_{Guid.NewGuid().ToString("N")[..8]}.txt");
        temps.Add(script);
        args.AddRange(new[] { "-/filter_complex", script });
        // chapters: the sequence's chapter markers in the range (times from its start) as an ffmetadata input; otherwise none (not the media's)
        var chapters = !alpha && o.Chapters && Chapters.Supported(o.Container) ? ed.ChaptersFor(t0, t1) : new();
        if (chapters.Count > 0)
        {
            var meta = Path.Combine(Path.GetTempPath(), $"cutpro_chapters_{Guid.NewGuid().ToString("N")[..8]}.txt");
            File.WriteAllText(meta, Chapters.FfMetadata(Chapters.ForFile(chapters), t1 - t0), new UTF8Encoding(false));
            temps.Add(meta);
            args.AddRange(new[] { "-f", "ffmetadata", "-i", meta, "-map_chapters", inputs.ToString() }); inputs++;
        }
        else args.AddRange(new[] { "-map_chapters", "-1" });
        if (!o.AudioOnly) args.AddRange(new[] { "-map", $"[{vout}]" });
        if (o.Audio != "none") args.AddRange(new[] { "-map", $"[{aout}]" });
        args.AddRange(new[] { "-t", F(t1 - t0) });
        args.AddRange(o.OutputArgs());
        args.Add(output);
        var chains = sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (args, string.Join(";\n", chains), temps);
    }

    /// <summary>Runs ffmpeg. Reports progress 0..1. Returns null on success, otherwise an error text.</summary>
    /// <param name="nests">nested clips' pictures (null: rendered here first — <see cref="Nest.PrepareExport"/>, which also renders their sound)</param>
    public static async Task<string?> RunAsync(string ffmpeg, Editor ed, string output, IProgress<double> progress, CancellationToken ct,
                                               IReadOnlyDictionary<string, string>? titles = null, IReadOnlyDictionary<string, string>? masks = null,
                                               ExportSettings? opts = null, IProgress<string>? status = null,
                                               IReadOnlyDictionary<string, Nest.Picture>? nests = null)
    {
        // nested sequences first: their pictures and their sound (the loudness measurement below mixes it)
        if (nests == null && ed.Clips.Any(c => !ed.TrackOff[c.Track] && ed.SeqOf(c) != null))
        {
            status?.Report("Rendering nested sequences…");
            try { nests = Nest.PrepareExport(ed, opts?.AudioOnly != true, opts?.Audio != "none", ct, progress); }
            catch (OperationCanceledException) { return "Cancelled"; }
            catch (Exception ex) { return ex.Message; }
            status?.Report("");
        }
        if (opts is { Normalize: true } && opts.Audio != "none")
        {
            // loudness normalization: measure the mix first (the playback mixer, offline), then apply one gain (+ limiter when peaks need it)
            status?.Report("Measuring loudness…");
            try
            {
                var (t0, t1) = Range(ed, opts);
                var r = Mixer.Measure(ed, t0, t1, ct, progress);
                (opts.NormGainDb, opts.NormLimit) = Loudness.Normalize(r, opts.Lufs, opts.TruePeak);
                opts.Measured = r;
            }
            catch (OperationCanceledException) { return "Cancelled"; }
            status?.Report("");
        }
        // speed / reverse / hold: the rendered frames and sound the preview plays (rendered now if the background hasn't yet)
        Dictionary<string, string>? remaps = null;
        try
        {
            if (ed.Clips.Any(c => TimeMap.IsRemapped(c) || AudioCache.Derives(c)))   // (pitch-shifted sound is rendered too)
            {
                if (opts?.AudioOnly != true) { status?.Report("Rendering speed changes…"); remaps = Remap.PrepareExport(ed, ct, progress); status?.Report(""); }
                foreach (var key in Mixer.Sources(ed, Transitions.Compute(ed), new()).Select(s => s.Path).Distinct())
                    while (!AudioCache.IsDone(key)) { ct.ThrowIfCancellationRequested(); Thread.Sleep(20); }
            }
            // stabilized clips: their rendered files, read like speed changes' (the same layout)
            if (opts?.AudioOnly != true && ed.Clips.Any(c => Stab.Active(c) && !ed.TrackOff[c.Track]))
            {
                status?.Report("Stabilizing…");
                var st = Stab.PrepareExport(ed, ct, progress);
                remaps ??= new();
                foreach (var (id, f) in st) remaps[id] = f;
                status?.Report("");
            }
            // audio effects: each clip's processed sound, rendered by the playback mixer's effect code from the clip's first sample
            var fxSrcs = opts?.Audio == "none" ? new List<AudioOut.Src>() : Mixer.Sources(ed, Transitions.Compute(ed), new()).Where(s => s.Fx != null).ToList();
            if (fxSrcs.Count > 0)
            {
                status?.Report("Rendering audio effects…");
                foreach (var s in fxSrcs)
                {
                    while (!AudioCache.IsDone(s.Path)) { ct.ThrowIfCancellationRequested(); Thread.Sleep(20); }
                    AudioDsp.EnsureFile(s, ct);
                }
                status?.Report("");
            }
            // track effects: each such track's bus through its chain, as one file (the mixer's own code — Build reads it)
            if (opts?.Audio != "none")
            {
                var all = Mixer.Sources(ed, Transitions.Compute(ed), new()).ToArray();
                var buses = all.Where(s => s.Bus != null && s.Sub == null).Select(s => s.Bus!).GroupBy(b => b.Track).Select(g => g.First()).ToList();
                var subBuses = all.Where(s => s.Sub != null).Select(s => s.Sub!).GroupBy(b => b.Index).Select(g => g.First()).ToList();
                if (buses.Count > 0) status?.Report("Rendering track effects…");
                foreach (var tb in buses) AudioDsp.EnsureTrackFile(all, tb, ed.End, ct);
                if (subBuses.Count > 0) status?.Report("Mixing submixes…");
                foreach (var sbus in subBuses) AudioDsp.EnsureSubFile(all, sbus, ed.End, ct);
                if (buses.Count + subBuses.Count > 0) status?.Report("");
            }
        }
        catch (OperationCanceledException) { return "Cancelled"; }
        catch (Exception ex) { return ex.Message; }
        string? surround = null;
        if (opts is { Channels: "5.1" } && opts.Audio != "none")
        {   // 5.1: the playback mixer renders each group of tracks; the exporter reads the 6 channels
            status?.Report("Mixing 5.1…");
            try
            {
                var (t0, t1) = Range(ed, opts);
                var srcs = Mixer.Sources(ed, Transitions.Compute(ed), new()).ToArray();
                foreach (var pth in srcs.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase)) while (!AudioCache.IsDone(pth)) { ct.ThrowIfCancellationRequested(); Thread.Sleep(20); }
                surround = Path.Combine(Path.GetTempPath(), $"cutpro_51_{Guid.NewGuid().ToString("N")[..8]}.f32");
                var mix = ed.Mix;
                Mixer.RenderSurround(srcs, Mixer.MasterOf(ed), tr => tr >= 0 && tr < mix.Channels.Length ? mix.Channels[tr].Surround : SurroundOut.Front, t0, t1,
                                     opts.Normalize ? Math.Pow(10, opts.NormGainDb / 20) : 1, surround, ct);
            }
            catch (OperationCanceledException) { return "Cancelled"; }
            status?.Report("");
        }
        if (opts != null) opts.SurroundFile = surround;
        var (args, script, temps) = Build(ed, output, titles, masks, opts, remaps, nests);
        if (surround != null) temps.Add(surround);
        File.WriteAllText(args[args.IndexOf("-/filter_complex") + 1], script, new UTF8Encoding(false));
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                                             WorkingDirectory = Path.GetTempPath() };      // sendcmd files are referenced by bare name
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (err) { err.AppendLine(e.Data); if (err.Length > 6000) err.Remove(0, err.Length - 6000); } } };
        try
        {
            p.Start(); p.BeginErrorReadLine();
            if (opts?.Background == true) try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
            double total = Math.Max(0.1, double.Parse(args[args.IndexOf("-t") + 1], CultureInfo.InvariantCulture));
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
                if (line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan(12), out var us)) progress.Report(Math.Clamp(us / 1e6 / total, 0, 1));
            await p.WaitForExitAsync();
            if (ct.IsCancellationRequested) return "Cancelled";
            if (p.ExitCode != 0) { lock (err) return err.ToString(); }
            return null;
        }
        finally { foreach (var t in temps) try { File.Delete(t); } catch { } }
    }
}
