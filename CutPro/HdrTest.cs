using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// --hdrtest: HDR footage tone-mapped to SDR the same way wherever it is decoded (detection, the curve, preview = export, the scrub cache,
/// thumbnails, speed renders, Show as is), and 10-bit exports (the composite keeps 10 bits, a timeline full of effects exports the same as
/// in 8 bits, smart rendering copies 10-bit HEVC). Media: an SDR test pattern graded to HLG / PQ the way cameras grade (BT.2408: SDR white =
/// HDR reference white, 203 cd/m²), 10-bit HEVC like phone footage.
/// </summary>
public static class HdrTest
{
    const int W = 1280, H = 720;
    static readonly List<string> log = new();
    static int fails;
    static string ff = "", fp = "";
    static void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
    static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir); log.Clear(); fails = 0;
        ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        fp = MediaProbe.FindFfprobe() ?? throw new Exception("ffprobe not found");
        Editor.SetSequence(W, H, 30, ReframeMode.Fit);
        try { await Body(dir); } catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "hdrtest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static void Exec(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
        var err = p.StandardError.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0) { var o = args.LastIndexOf(" \"") is int q && q >= 0 ? args[(q + 2)..].TrimEnd('"') : ""; if (o != "" && File.Exists(o)) File.Delete(o); throw new Exception($"ffmpeg {args}: {err}"); }   // (no half-written file left to be reused)
    }

    static string Out(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!; p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); return o.Trim();
    }

    static string Gen(string dir, string name, string lavfi, double dur, int w = W, int h = H)
    {
        var f = Path.Combine(dir, name);
        if (!File.Exists(f)) Exec($"-y -v error -f lavfi -i \"{lavfi}=s={w}x{h}:r=30:d={F(dur)}\" -c:v ffv1 -pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709 \"{f}\"");
        return f;
    }

    /// <summary>SDR → HDR as a camera grades it: linear light, BT.2020, the HDR transfer with SDR white at 203 cd/m², 10-bit HEVC with closed
    /// GOPs (tags: all three, or only the transfer as some files carry; extra: more x265 parameters, e.g. HDR10 metadata).</summary>
    static string ToHdr(string dir, string name, string src, string kind, bool allTags = true, string extra = "")
    {
        var f = Path.Combine(dir, name);
        if (File.Exists(f)) return f;
        string t = kind == "pq" ? "smpte2084" : "arib-std-b67";
        string vf = $"zscale=tin=bt709:min=bt709:rin=tv:t=linear:npl={Hdr.White},format=gbrpf32le,zscale=pin=bt709:p=bt2020,zscale=t={t}:npl={Hdr.White}:m=bt2020nc:r=tv,format=yuv420p10le"
                    + (allTags ? "" : ",setparams=color_primaries=unknown:colorspace=unknown");   // (the encoder would take the frames' tags)
        string tags = allTags ? $"colorprim=bt2020:transfer={t}:colormatrix=bt2020nc" : $"transfer={t}";
        string ctx = allTags ? $"-color_primaries bt2020 -color_trc {t} -colorspace bt2020nc" : $"-color_trc {t}";
        Exec($"-y -v error -i \"{src}\" -vf \"{vf}\" -c:v libx265 -crf 6 -x265-params \"{tags}:keyint=30:min-keyint=30:scenecut=0:open-gop=0:log-level=error{extra}\" {ctx} -tag:v hvc1 \"{f}\"");
        return f;
    }

    /// <summary>One frame (the first at or after t) through vf as raw pixels: bgra, or gray / gray10le planes.</summary>
    static byte[] Raw(string file, double t, string vf, int w, int h, string pix = "bgra")
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var a = new List<string> { "-v", "error", "-ss", F(Math.Max(0, t)), "-i", file, "-frames:v", "1" };
        if (vf != "") a.AddRange(new[] { "-vf", vf });
        a.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", pix, "pipe:1" });
        foreach (var x in a) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!; p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var ms = new MemoryStream(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        int need = w * h * (pix == "bgra" ? 4 : pix == "gray10le" ? 2 : 1);
        var b = ms.ToArray();
        if (b.Length < need) throw new Exception($"decoding {Path.GetFileName(file)} at {t:0.###} s through “{vf}” gave {b.Length} of {need} bytes");
        return b;
    }

    /// <summary>PSNR over the colour channels of two 4-byte-per-pixel pictures of the same size.</summary>
    static double Psnr(byte[] a, byte[] b)
    {
        double se = 0; long n = 0;
        for (int i = 0; i + 3 < Math.Min(a.Length, b.Length); i += 4)
            for (int c = 0; c < 3; c++) { double e = a[i + c] - b[i + c]; se += e * e; n++; }
        return se < 1e-9 ? 99 : 10 * Math.Log10(255.0 * 255.0 * n / se);
    }

    static byte[] Pixels(BitmapSource s)
    {
        var c = new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
        var b = new byte[c.PixelWidth * c.PixelHeight * 4]; c.CopyPixels(b, c.PixelWidth * 4, 0); return b;
    }

    static async Task<byte[]> PreviewFrame(Preview pv, double t)
    {
        var sw = Stopwatch.StartNew();
        do { pv.Sync(t, false); await Task.Delay(20); } while ((!pv.Settled(t) || sw.ElapsedMilliseconds < 400) && sw.ElapsedMilliseconds < 15000);
        pv.Sync(t, false);
        pv.Measure(new Size(Editor.W, Editor.H)); pv.Arrange(new Rect(0, 0, Editor.W, Editor.H)); pv.UpdateLayout();
        var rtb = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
        var px = new byte[Editor.W * Editor.H * 4]; rtb.CopyPixels(px, Editor.W * 4, 0);
        return px;
    }

    static async Task<string?> Export(Editor ed, string file, ExportSettings o, string titleDir)
    {
        var titles = TitleVisual.PrepareExport(ed, titleDir);
        return await Task.Run(() => Exporter.RunAsync(ff, ed, file, new Progress<double>(), CancellationToken.None, titles, null, o));
    }

    static async Task Body(string dir)
    {
        // ---------- media ----------
        var sdr = Gen(dir, "sdr.mkv", "testsrc2", 3);
        var sdr1080 = Gen(dir, "sdr1080.mkv", "testsrc2", 3, 1920, 1080);
        var fHlg = ToHdr(dir, "hlg.mp4", sdr, "hlg");
        var fPq = ToHdr(dir, "pq.mp4", sdr, "pq");
        var fBare = ToHdr(dir, "hlg_transfer_only.mp4", sdr, "hlg", allTags: false);
        var fHlg1080 = ToHdr(dir, "hlg1080.mp4", sdr1080, "hlg");
        var fPqMeta = ToHdr(dir, "pq_hdr10.mp4", sdr, "pq", extra: ":hdr10=1:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1600,400");
        var fRamp = Path.Combine(dir, "hlg_ramp.mp4");             // the whole HLG signal range, 0 … 100 %, left to right
        if (!File.Exists(fRamp))
            Exec($"-y -v error -f lavfi -i \"nullsrc=s=1024x16:r=30:d=1,format=yuv420p10le,geq=lum='64+X*876/1023':cb=512:cr=512\" -frames:v 1 -c:v libx265 " +
                 $"-x265-params \"lossless=1:colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc:log-level=error\" -color_primaries bt2020 -color_trc arib-std-b67 -colorspace bt2020nc \"{fRamp}\"");
        var fGrad = Path.Combine(dir, "ramp10.mkv");                // SDR, 10 bits: 256 levels of luma where 8 bits have 64
        if (!File.Exists(fGrad))
            Exec($"-y -v error -f lavfi -i \"nullsrc=s={W}x{H}:r=30:d=2,format=yuv420p10le,geq=lum='256+256*X/W':cb=512:cr=512\" -c:v ffv1 -pix_fmt yuv420p10le \"{fGrad}\"");
        var fSdr10 = Path.Combine(dir, "sdr10.mp4");                // 10-bit SDR HEVC (closed GOPs of 30): what smart rendering copies
        if (!File.Exists(fSdr10))
            Exec($"-y -v error -f lavfi -i \"testsrc2=s={W}x{H}:r=30:d=6\" -c:v libx265 -crf 16 -pix_fmt yuv420p10le " +
                 $"-x265-params \"keyint=30:min-keyint=30:scenecut=0:open-gop=0:colorprim=bt709:transfer=bt709:colormatrix=bt709:log-level=error\" -color_primaries bt709 -color_trc bt709 -colorspace bt709 -tag:v hvc1 \"{fSdr10}\"");

        // ---------- detection ----------
        async Task<MediaItem> Probe(string f) => await MediaProbe.ProbeAsync(f) ?? throw new Exception($"import {Path.GetFileName(f)}: {MediaProbe.LastError}");
        var mSdr = await Probe(sdr); var mHlg = await Probe(fHlg); var mPq = await Probe(fPq); var mBare = await Probe(fBare);
        var mHlg1080 = await Probe(fHlg1080); var mPqMeta = await Probe(fPqMeta); var mGrad = await Probe(fGrad); var mSdr10 = await Probe(fSdr10);
        Check(mHlg.Hdr == "hlg" && mHlg.TenBit && mPq.Hdr == "pq" && mBare.Hdr == "hlg" && mSdr.Hdr == null && mSdr10.Hdr == null && mGrad.Hdr == null && mGrad.TenBit,
              $"detected by the colour transfer: HLG “{mHlg.Info.Trim()}”, PQ, HLG with only the transfer tagged; SDR 8 / 10-bit files stay SDR");
        Check(Math.Abs(mPq.HdrPeak - 1000) < 1e-9 && Math.Abs(mPqMeta.HdrPeak - 1600) < 1e-9 && Math.Abs(mHlg.HdrPeak - 1000) < 1e-9,
              $"PQ peak: {mPq.HdrPeak:0} cd/m² without metadata, {mPqMeta.HdrPeak:0} from HDR10 metadata (MaxCLL 1600)");

        // ---------- the curve ----------
        // (RGB PSNR. The pattern's full-strength primaries sit above the knee and are compressed on purpose; the same pattern at 70 % — 0.53 of
        // white in linear light, all below the knee — must come back as it was)
        var sdrRef = Raw(sdr, 0.499, "", W, H);
        foreach (var (m, name) in new[] { (mHlg, "HLG"), (mPq, "PQ"), (mBare, "HLG, transfer tag only"), (mPqMeta, "PQ with HDR10 metadata") })
        {
            double tm = Psnr(Raw(m.Path, 0.499, Hdr.Of(m), W, H), sdrRef), asis = Psnr(Raw(m.Path, 0.499, "", W, H), sdrRef);
            Check(tm >= 19 && tm >= asis + 8, $"{name}: tone-mapped vs the SDR original {tm:0.0} dB (full-strength colours: compressed toward the top); shown as is {asis:0.0} dB");
        }
        {
            var mid = Path.Combine(dir, "sdr_mid.mkv");
            if (!File.Exists(mid)) Exec($"-y -v error -f lavfi -i \"testsrc2=s={W}x{H}:r=30:d=1,lutrgb=r=val*0.7:g=val*0.7:b=val*0.7\" -c:v ffv1 -pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709 \"{mid}\"");
            var midRef = Raw(mid, 0.499, "", W, H);
            // (the curve on its own: the chain with it vs the same chain without it — equal below the knee; against the original the hard-edged
            // pattern's chroma, resampled four times on the way to HDR and back, costs a few dB whatever the curve; encoder ringing at its edges pokes
            // a few pixels above the knee)
            static string NoCurve(string chain) => System.Text.RegularExpressions.Regex.Replace(chain, "tonemap=[^,]*,", "");
            foreach (var kind in new[] { "hlg", "pq" })
            {
                var m = await Probe(ToHdr(dir, $"{kind}_mid.mp4", mid, kind));
                var full = kind == "hlg" ? mHlg : mPq;
                var px = Raw(m.Path, 0.499, Hdr.Of(m), W, H);
                double same = Psnr(px, Raw(m.Path, 0.499, NoCurve(Hdr.Of(m)), W, H)), sameFull = Psnr(Raw(full.Path, 0.499, Hdr.Of(full), W, H), Raw(full.Path, 0.499, NoCurve(Hdr.Of(full)), W, H));
                double tm = Psnr(px, midRef), asis = Psnr(Raw(m.Path, 0.499, "", W, H), midRef);
                Check(same >= 55 && sameFull < 30 && tm >= 28 && tm >= asis + 12,
                      $"{kind.ToUpperInvariant()}, the pattern at 70 % (all below the knee): the curve leaves it alone ({(same >= 99 ? "identical" : $"{same:0.0} dB")}; full strength it compresses: {sameFull:0.0} dB), " +
                      $"close to the SDR original ({tm:0.0} dB; as is {asis:0.0} dB)");
            }
        }
        {
            var ramp = Raw(fRamp, 0, Hdr.Of(mHlg), 1024, 16, "yuv420p").AsSpan(8 * 1024, 1024).ToArray();   // (the luma plane as the chain leaves it: limited range)
            int At(double signal) => ramp[(int)Math.Round(signal * 1023)];
            double Pct(int v) => (v - 16) / 219.0 * 100;
            bool mono = true; for (int x = 1; x < 1024; x++) mono &= ramp[x] >= ramp[x - 1];
            int levels = ramp.Skip(767).Distinct().Count();
            Check(mono && Math.Abs(Pct(At(0.38)) - 43) < 4 && Pct(At(0.75)) is > 87 and < 95 && Pct(At(1)) >= 98 && levels >= 15,
                  $"HLG signal → SDR: 18 % grey (38 % HLG) at {Pct(At(0.38)):0} % (SDR's own ≈ 41–43 %), reference white (75 % HLG) at {Pct(At(0.75)):0} %, the peak at {Pct(At(1)):0} %; " +
                  $"{levels} levels from white to the peak (highlights compressed, not clipped), never darker as the signal rises");
        }

        // ---------- preview = export ----------
        var ed = new Editor();
        ed.Media.AddRange(new[] { mHlg, mPq, mBare, mHlg1080, mPqMeta });
        Clip V(MediaItem m, double start, double inPt, Action<Clip>? f = null)
        {
            var c = new Clip { MediaId = m.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = start, In = inPt, Dur = 1, NoAudio = true };
            f?.Invoke(c); ed.Clips.Add(c); return c;
        }
        var cases = new List<(string name, Clip c, double min)>
        {
            ("HLG", V(mHlg, 0, 0), 38), ("PQ", V(mPq, 1, 0.5), 38), ("HLG, transfer tag only", V(mBare, 2, 1), 38),
            ("HLG 1080p in a 720p sequence", V(mHlg1080, 3, 0.5), 33), ("PQ with HDR10 metadata", V(mPqMeta, 5, 1), 38),
        };
        var slow = V(mHlg, 4, 0.5, c => c.Speed = 50);                // a speed change: rendered (Remap) from the tone-mapped frames
        var mp4 = Path.Combine(dir, "out_hdr.mp4");
        var sw = Stopwatch.StartNew();
        var err = await Export(ed, mp4, new ExportSettings { Quality = 10, Audio = "none" }, Path.Combine(dir, "t_hdr"));
        Check(err == null, $"export of HLG / PQ clips, one scaled, one at 50 % ({sw.Elapsed.TotalSeconds:0.0} s)" + (err == null ? "" : ": " + err));
        var pv = new Preview { Ed = ed, Width = Editor.W, Height = Editor.H, Background = Brushes.Black };
        if (err == null)
        {
            foreach (var (name, c, min) in cases)
            {
                double t = c.Start + 0.5, src = c.In + 0.5;
                var pa = await PreviewFrame(pv, t);
                var pb = FxTest.Frame(ff, mp4, t - 0.001);
                var m = ed.MediaOf(c)!;
                var want = Raw(m.Path, src - 0.001, $"scale={W}:{H}" + Hdr.Post(m), W, H);
                double pe = FxTest.Compare(pa, pb).psnr, pw = Psnr(pa, want), raw = Psnr(pa, Raw(m.Path, src - 0.001, $"scale={W}:{H}", W, H));
                var (sx, sy) = SeqTest.Shift(pa, pb, W, H);
                Check(pe >= min && Math.Abs(sx) <= 1 && Math.Abs(sy) <= 1 && pw >= 33 && pw > raw + 6,
                      $"{name}: preview vs export {pe:0.0} dB (≥ {min}), shift ({sx},{sy}); the preview is the tone-mapped frame ({pw:0.0} dB; the raw signal {raw:0.0} dB)");
                FxTest.Save(FxTest.SideBySide(pa, pb), Path.Combine(dir, $"hdr_{name.Split(',')[0].Replace(' ', '_')}.png"));
            }
            {
                double t = 4.5;
                long k = TimeMap.NativeIndex(slow, mHlg, t);
                var want = Raw(mHlg.Path, k / 30.0 - 0.001, Hdr.Of(mHlg), W, H);
                double pe = Psnr(FxTest.Frame(ff, mp4, t - 0.001), want);
                Check(pe >= 36, $"HLG at 50 %: the exported frame = source frame {k} tone-mapped ({pe:0.0} dB) — the speed render reads tone-mapped frames");
            }
        }

        // ---------- the scrub cache and thumbnails ----------
        {
            ScrubCache.Size(mHlg, out int sw2, out int sh2);
            ScrubCache.WaitReady(mHlg.Path, 60000);
            WriteableBitmap? wb = null;
            long k = ScrubCache.Show(mHlg.Path, 0.5, ref wb);
            var cache = k >= 0 && wb != null ? Pixels(wb) : Array.Empty<byte>();
            string scale = $"scale={sw2}:{sh2}:flags=bilinear";
            double tm = cache.Length > 0 ? Psnr(cache, Raw(mHlg.Path, 0.499, scale + Hdr.Post(mHlg), sw2, sh2)) : 0;
            double asis = cache.Length > 0 ? Psnr(cache, Raw(mHlg.Path, 0.499, scale, sw2, sh2)) : 0;
            Check(k == 15 && tm >= 30 && tm > asis + 6, $"scrub cache: frame {k} of the HLG file is the tone-mapped picture ({tm:0.0} dB; as is {asis:0.0} dB)");

            var thumbSdr = Pixels((BitmapSource)mSdr.Thumb!); var thumbHlg = Pixels((BitmapSource)mHlg.Thumb!);
            double th = Psnr(thumbHlg, thumbSdr);

            // ---------- Show as is ----------
            var spec0 = Remap.Spec(slow, mHlg, 4, 5); var sig0 = Nest.Take(ed, ed.Seq).VSig; string chain0 = Hdr.Of(mHlg);
            mHlg.KeepHdr = true;
            ScrubCache.Rebuild(mHlg); await MediaProbe.FillAsync(mHlg);
            bool sigs = Hdr.Of(mHlg) == "" && chain0 != "" && Remap.Spec(slow, mHlg, 4, 5) != spec0 && Nest.Take(ed, ed.Seq).VSig != sig0 && mHlg.Info.Contains("(as is)");
            double thAsis = Psnr(Pixels((BitmapSource)mHlg.Thumb!), thumbSdr);
            Check(th >= 19 && th > thAsis + 6, $"thumbnails: the HLG file's = the SDR original's ({th:0.0} dB), shown as is {thAsis:0.0} dB");
            ScrubCache.WaitReady(mHlg.Path, 60000);
            wb = null; k = ScrubCache.Show(mHlg.Path, 0.5, ref wb);
            double cacheAsis = k >= 0 && wb != null ? Psnr(Pixels(wb), Raw(mHlg.Path, 0.499, scale, sw2, sh2)) : 0;
            var pa = await PreviewFrame(pv, 0.5);
            double pvAsis = Psnr(pa, Raw(mHlg.Path, 0.499, "", W, H));
            Check(sigs && cacheAsis >= 30 && pvAsis >= 36,
                  $"HDR ▸ Show as is: no tone mapping; speed-render and nested-render signatures change; the scrub cache is rebuilt as is ({cacheAsis:0.0} dB), the preview shows the signal ({pvAsis:0.0} dB)");
            mHlg.KeepHdr = false; ScrubCache.Rebuild(mHlg);
        }

        // ---------- 10 bits ----------
        var o10 = new ExportSettings { Video = "hevc", TenBit = true, Quality = 4, Audio = "none" };
        Check(o10.Deep && o10.OutPixFmt == "yuv420p10le" && !new ExportSettings { Video = "h264", TenBit = true }.Deep && new ExportSettings { Video = "h264", TenBit = true }.OutPixFmt == "yuv420p"
              && new ExportSettings { Video = "av1", TenBit = true }.OutPixFmt == "yuv420p10le" && new ExportSettings { Video = "vp9", TenBit = true }.OutPixFmt == "yuv420p10le"
              && new ExportSettings { Video = "hevc", Encoder = "nvenc", TenBit = true }.OutPixFmt == "p010le" && new ExportSettings { Video = "prores4444", TenBit = true }.Deep,
              "10-bit: HEVC Main 10 / AV1 / VP9 profile 2 (p010 on GPU encoders), ProRes gets the 10-bit composite; H.264 stays 8-bit");
        {
            var eg = new Editor(); eg.Media.Add(mGrad);
            eg.Clips.Add(new Clip { MediaId = mGrad.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = 0, In = 0, Dur = 2, NoAudio = true });
            int Levels(string f)
            {
                var px = Raw(f, 0.999, "", W, H, "gray10le");
                var row = new HashSet<int>(); int y = H / 2;
                for (int x = 64; x < W - 64; x++) row.Add(px[(y * W + x) * 2] | px[(y * W + x) * 2 + 1] << 8);
                return row.Count;
            }
            var f10 = Path.Combine(dir, "out_ramp10.mp4"); var f8 = Path.Combine(dir, "out_ramp8.mp4");
            var e10 = await Export(eg, f10, o10, Path.Combine(dir, "t_r10"));
            var e8 = await Export(eg, f8, new ExportSettings { Video = "hevc", Quality = 4, Audio = "none" }, Path.Combine(dir, "t_r8"));
            if (e10 != null || e8 != null) Check(false, "gradient exports: " + (e10 ?? e8));
            else
            {
                var info = Out(fp, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=pix_fmt,profile", "-of", "csv=p=0", f10);
                int l10 = Levels(f10), l8 = Levels(f8);
                Check(info.Contains("yuv420p10le") && info.Contains("Main 10") && l10 >= 180 && l8 <= 70,
                      $"a 10-bit gradient (256 levels of luma across the frame): the 10-bit export keeps {l10} of them ({info.Replace(',', ' ')}), the 8-bit export {l8}");
            }
        }
        {
            // a timeline full of effects: deep = 8-bit (both composites, 4:4:4 and 4:2:0)
            var ex = new Editor(); ex.Media.AddRange(new[] { mSdr, mHlg });
            ex.Clips.Add(new Clip { MediaId = mSdr.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = 0, In = 0, Dur = 2, NoAudio = true });
            ex.Clips.Add(new Clip { MediaId = mHlg.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = 2, In = 0.5, Dur = 2, NoAudio = true, TransIn = new Transition { Kind = "Cross Dissolve", Dur = 1 } });
            ex.Clips.Add(new Clip { Kind = ClipKind.Text, Track = Tracks.V(2), Start = 0.5, Dur = 3, Text = "TEN BITS", FadeIn = 0.3, Style = new TextStyle { Size = 90 } });
            var bl = new Clip { MediaId = mSdr.Id, Kind = ClipKind.Video, Track = Tracks.V(3), Start = 1, In = 1, Dur = 1, NoAudio = true, Blend = BlendMode.Multiply, Scale = 60 };
            bl.CropL = 10; bl.CropT = 5; bl.CropR = 20; bl.CropB = 10; ex.Clips.Add(bl);
            ex.Clips.Add(new Clip { Kind = ClipKind.Adjustment, Track = Tracks.V(3), Start = 2.5, Dur = 1, Text = "Adjustment layer", Grade = new ColorGrade { Saturation = 140, Exposure = 0.3 } });
            ex.Clips.Add(new Clip { MediaId = mSdr.Id, Kind = ClipKind.Video, Track = Tracks.V(2), Start = 3.5, In = 2, Dur = 0.5, NoAudio = true, Scale = 40, X = 200, Grade = new ColorGrade { Look = "Teal & Orange", LookAmount = 100 } });
            // (compared through ProRes 4444 — 10-bit 4:4:4 and near lossless either way — so what differs is the composite, not the encoder's noise)
            ExportSettings P(bool deep, bool full) => new() { Video = "prores4444", Container = "mov", TenBit = deep, Full444 = full, Audio = "none" };
            var f8 = Path.Combine(dir, "out_fx8.mov"); var f10 = Path.Combine(dir, "out_fx10.mov"); var f8s = Path.Combine(dir, "out_fx8_420.mov"); var f10s = Path.Combine(dir, "out_fx10_420.mov");
            var fh = Path.Combine(dir, "out_fx10.mp4");
            sw.Restart(); var e8 = await Export(ex, f8, P(false, true), Path.Combine(dir, "t_x8")); double t8 = sw.Elapsed.TotalSeconds;
            sw.Restart(); var e10 = await Export(ex, f10, P(true, true), Path.Combine(dir, "t_x10")); double t10 = sw.Elapsed.TotalSeconds;
            var e8s = await Export(ex, f8s, P(false, false), Path.Combine(dir, "t_x8s"));
            var e10s = await Export(ex, f10s, P(true, false), Path.Combine(dir, "t_x10s"));
            var eh = await Export(ex, fh, new ExportSettings { Video = "hevc", Quality = 6, TenBit = true, Audio = "none" }, Path.Combine(dir, "t_xh"));
            var errs = new[] { e8, e10, e8s, e10s, eh }.Where(x => x != null).ToList();
            Check(errs.Count == 0, $"effects timeline (title fade, cross dissolve into HLG, Multiply + crop, adjustment layer, a graded inset) exports with an 8-bit ({t8:0.0} s) and a 10-bit composite ({t10:0.0} s), 4:4:4 and 4:2:0, and as 10-bit HEVC" + (errs.Count > 0 ? ": " + errs[0] : ""));
            if (errs.Count == 0)
            {
                double worst = 99, worst420 = 99;
                foreach (var t in new[] { 0.25, 0.75, 1.5, 2.5, 3.0, 3.75 })
                {
                    worst = Math.Min(worst, Psnr(FxTest.Frame(ff, f8, t), FxTest.Frame(ff, f10, t)));
                    worst420 = Math.Min(worst420, Psnr(FxTest.Frame(ff, f8s, t), FxTest.Frame(ff, f10s, t)));
                }
                Check(worst >= 40 && worst420 >= 40, $"the 10-bit composite shows what the 8-bit one does: worst frame {worst:0.0} dB (4:4:4), {worst420:0.0} dB (4:2:0)");
            }
        }
        {
            // smart rendering copies 10-bit HEVC into a 10-bit export
            var es = new Editor(); es.Media.Add(mSdr10);
            es.Clips.Add(new Clip { MediaId = mSdr10.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = 0, In = 0, Dur = 6, NoAudio = true });
            es.Clips.Add(new Clip { Kind = ClipKind.Text, Track = Tracks.V(2), Start = 2, Dur = 1, Text = "TITLE", Style = new TextStyle { Size = 60 } });
            var os = new ExportSettings { Video = "hevc", Quality = 16, TenBit = true, Smart = true, Audio = "none" };
            var fs = Path.Combine(dir, "out_smart10.mp4");
            var es1 = await Export(es, fs, os, Path.Combine(dir, "t_s10"));
            var o8 = new ExportSettings { Video = "hevc", Quality = 16, Smart = true, Audio = "none" };
            var es2 = await Export(es, Path.Combine(dir, "out_smart8.mp4"), o8, Path.Combine(dir, "t_s8"));
            var eh = new Editor(); eh.Media.Add(mHlg);
            eh.Clips.Add(new Clip { MediaId = mHlg.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = 0, In = 0, Dur = 3, NoAudio = true });
            var oh = new ExportSettings { Video = "hevc", Quality = 16, TenBit = true, Smart = true, Audio = "none" };
            var es3 = await Export(eh, Path.Combine(dir, "out_smart_hlg.mp4"), oh, Path.Combine(dir, "t_sh"));
            bool exact = false; string info = "";
            if (es1 == null)
            {
                byte[] Yuv(string f, int n) => Raw(f, 0, $"select=eq(n\\,{n})", W, H * 3 / 2, "yuv420p10le");
                exact = Yuv(fSdr10, 15).AsSpan().SequenceEqual(Yuv(fs, 15)) && !Yuv(fSdr10, 75).AsSpan().SequenceEqual(Yuv(fs, 75));
                info = Out(fp, "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=pix_fmt,nb_read_frames", "-of", "csv=p=0", fs);
            }
            Check(es1 == null && os.SmartReport?.Contains("copied (") == true && exact && info.Contains("yuv420p10le") && info.Contains("180"),
                  $"smart render, 10-bit HEVC into a 10-bit export: “{os.SmartReport}”; a copied frame is the file's own bit for bit, the title's re-encoded ({info.Replace(',', ' ')})" + (es1 is { } x1 ? ": " + x1 : ""));
            Check(es2 == null && es3 == null && o8.SmartReport?.Contains("yuv420p10le") == true && oh.SmartReport?.Contains("HDR") == true,
                  $"nothing is copied into an 8-bit export (“{o8.SmartReport}”) or from HDR footage (“{oh.SmartReport}”)");
        }
    }
}
