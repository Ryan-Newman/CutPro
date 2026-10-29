using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// --gputest: GPU scaling and compositing in the export (NVIDIA). Big files that the export shrinks are decoded and scaled on the GPU;
/// full-frame 8-bit SDR cuts stay there and overlay_cuda, and picture-in-picture is baked into those cuts (titles / effects / transitions
/// download and finish on the CPU). Checks: calibration finds it (not for a rotated file or one the GPU can't decode), the graph uses it
/// for exactly the clips it should, the picture = the CPU path's (ProRes 4444 masters) for plain, scaled, graded / cropped, dissolving and
/// HDR clips, overlay_cuda for a 4:2:0 cuts + PiP timeline, preview vs export no worse than the CPU path's, the fallback when the GPU chain
/// fails, the speed-up. A PC without a CUDA decoder skips it (and passes).
/// </summary>
public static class GpuTest
{
    const int W = 1920, H = 1080;                      // the sequence: 4K files shrink into it
    static readonly List<string> log = new();
    static int fails;
    static string ff = "";
    static void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
    static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir); log.Clear(); fails = 0;
        ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        Editor.SetSequence(W, H, 30, ReframeMode.Fit);
        try { await Body(dir); } catch (Exception ex) { Check(false, "exception: " + ex); }
        finally { Exporter.GpuScale = true; Exporter.GpuBreak = false; Editor.SetSequence(1280, 720, 30, ReframeMode.Fit); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "gputest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static void Exec(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
        var err = p.StandardError.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception($"ffmpeg {args}: {err}");
    }

    /// <summary>A 4K test file (testsrc2: fine lines and text, colour bars, motion), generated once (into a .tmp, then moved).</summary>
    static string Gen(string dir, string name, double dur, string venc, string vf = "")
    {
        var f = Path.Combine(dir, name);
        if (File.Exists(f)) return f;
        var tmp = Path.Combine(dir, "gen_" + name);
        Exec($"-y -v error -f lavfi -i \"testsrc2=s=3840x2160:r=30:d={F(dur)}{vf}\" {venc} -an \"{tmp}\"");
        File.Move(tmp, f, true);
        return f;
    }

    static byte[] Frame(string file, double t, int w, int h)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-ss", F(t), "-i", file, "-frames:v", "1", "-vf", $"scale={w}:{h}:flags=area", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!; p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var ms = new MemoryStream(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        var b = ms.ToArray();
        if (b.Length < w * h * 4) throw new Exception($"no frame at {t:0.###} s in {Path.GetFileName(file)}");
        return b;
    }

    static double Psnr(byte[] a, byte[] b)
    {
        double se = 0; long n = 0;
        for (int i = 0; i + 3 < Math.Min(a.Length, b.Length); i += 4)
            for (int c = 0; c < 3; c++) { double e = a[i + c] - b[i + c]; se += e * e; n++; }
        return se < 1e-9 ? 99 : 10 * Math.Log10(255.0 * 255.0 * n / se);
    }

    static async Task<byte[]> PreviewFrame(Preview pv, double t)
    {
        var sw = Stopwatch.StartNew();
        do { pv.Sync(t, false); await Task.Delay(20); } while ((!pv.Settled(t) || sw.ElapsedMilliseconds < 400) && sw.ElapsedMilliseconds < 20000);
        pv.Sync(t, false);
        pv.Measure(new Size(Editor.W, Editor.H)); pv.Arrange(new Rect(0, 0, Editor.W, Editor.H)); pv.UpdateLayout();
        var rtb = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
        var px = new byte[Editor.W * Editor.H * 4]; rtb.CopyPixels(px, Editor.W * 4, 0);
        return px;
    }

    static async Task<(string? err, double s)> Export(Editor ed, string file, ExportSettings o, string titleDir, bool gpu)
    {
        Exporter.GpuScale = gpu;
        var titles = TitleVisual.PrepareExport(ed, titleDir);
        var sw = Stopwatch.StartNew();
        var err = await Task.Run(() => Exporter.RunAsync(ff, ed, file, new Progress<double>(), CancellationToken.None, titles, null, o));
        return (err, sw.Elapsed.TotalSeconds);
    }

    static async Task Body(string dir)
    {
        // ---------- media ----------
        var f8 = Gen(dir, "uhd_h264.mp4", 6, "-c:v libx264 -preset ultrafast -crf 16 -pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709");
        var f10 = Gen(dir, "uhd_hevc10.mp4", 4, "-c:v libx265 -preset ultrafast -crf 16 -pix_fmt yuv420p10le -x265-params log-level=error:colorprim=bt709:transfer=bt709:colormatrix=bt709 -tag:v hvc1");
        var fHlg = Gen(dir, "uhd_hlg.mp4", 3, "-c:v libx265 -preset ultrafast -crf 16 -x265-params log-level=error:colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc " +
                       "-color_primaries bt2020 -color_trc arib-std-b67 -colorspace bt2020nc -tag:v hvc1",
                       ",format=gbrpf32le,zscale=tin=bt709:min=bt709:rin=tv:t=linear:npl=203,zscale=pin=bt709:p=bt2020,zscale=t=arib-std-b67:npl=203:m=bt2020nc:r=tv,format=yuv420p10le");
        var f444 = Gen(dir, "uhd_h264_444.mp4", 2, "-c:v libx264 -preset ultrafast -crf 16 -pix_fmt yuv444p");
        var fLong = Gen(dir, "uhd_long_hevc10.mp4", 20, "-c:v libx265 -preset ultrafast -crf 20 -pix_fmt yuv420p10le -x265-params log-level=error:colorprim=bt709:transfer=bt709:colormatrix=bt709:keyint=60 -tag:v hvc1");
        async Task<MediaItem> Probe(string f) => await MediaProbe.ProbeAsync(f) ?? throw new Exception($"import {Path.GetFileName(f)}: {MediaProbe.LastError}");
        var m8 = await Probe(f8); var m10 = await Probe(f10); var mHlg = await Probe(fHlg); var m444 = await Probe(f444); var mLong = await Probe(fLong);

        // ---------- calibration ----------
        foreach (var m in new[] { m8, m10, mHlg, m444, mLong }) await Task.Run(() => Decoders.Calibrate(m));
        if (!m8.GpuScaleOk)
        {
            log.Add($"SKIP no CUDA decoder on this PC — nothing to test ({m8.DecodeNote})");
            return;
        }
        var rot = new MediaItem { Path = m8.Path, Type = ClipKind.Video, Width = m8.Width, Height = m8.Height, Duration = m8.Duration, Rotation = 90 };
        await Task.Run(() => Decoders.Calibrate(rot));
        Check(m8.GpuScaleOk && m10.GpuScaleOk && mHlg.GpuScaleOk && mLong.GpuScaleOk && !rot.GpuScaleOk && !m444.GpuScaleOk && mHlg.PlayDecoder != Decoder.CudaScale,
              $"calibration: the GPU decodes and scales the 4K H.264 / 10-bit HEVC / HLG files; not a rotated one, nor H.264 4:4:4 (the GPU can't decode it); " +
              $"HDR never plays through the 8-bit CudaScale (HLG plays with {mHlg.PlayDecoder}; H.264 {m8.PlayDecoder}, HEVC {m10.PlayDecoder})");
        // export only scales on the GPU when CUDA is the play decoder (a fast CPU wins these easy files). Force it so the rest of the test
        // actually runs the CUDA path — HDR keeps Cuda, not CudaScale (8-bit nv12 would flatten the signal before the tone map)
        foreach (var m in new[] { m8, m10, mLong }) if (m.GpuScaleOk) m.PlayDecoder = Decoder.CudaScale;

        // ---------- the timeline ----------
        var ed = new Editor(); ed.Media.AddRange(new[] { m8, m10, mHlg, m444 });
        Clip V(MediaItem m, int track, double start, double inPt, double dur, Action<Clip>? f = null)
        {
            var c = new Clip { MediaId = m.Id, Kind = ClipKind.Video, Track = track, Start = start, In = inPt, Dur = dur, NoAudio = true };
            f?.Invoke(c); ed.Clips.Add(c); return c;
        }
        V(m8, Tracks.V1, 0, 0.5, 3);
        V(m10, Tracks.V1, 3, 0.2, 3, c => c.TransIn = new Transition { Kind = "Cross Dissolve", Dur = 1 });
        V(mHlg, Tracks.V1, 6, 0.5, 2);
        V(m444, Tracks.V1, 8, 0.2, 1.5);
        // picture-in-picture on the 10-bit file: grade / crop after the GPU scale (H.264 often plays on the CPU — easy testsrc2 — and would skip it)
        V(m10, Tracks.V(2), 1, 2, 3, c => { c.Scale = 35; c.X = 300; c.Y = -150; c.Grade = new ColorGrade { Look = "Teal & Orange", LookAmount = 100 }; c.CropL = 10; });
        ed.Clips.Add(new Clip { Kind = ClipKind.Text, Track = Tracks.V(3), Start = 0.5, Dur = 4.5, Text = "GPU", FadeIn = 0.3, Style = new TextStyle { Size = 90 } });
        int wantGpu = 4;   // V1 H.264, V1 HEVC, V1 HLG, V2 PiP; not the 4:4:4 file
        var o = new ExportSettings { Video = "prores4444", Container = "mov", Audio = "none" };
        var (_, gpuScript, gt) = Exporter.Build(ed, Path.Combine(dir, "x.mov"), null, null, o);
        var (_, cpuScript, ct) = Exporter.Build(ed, Path.Combine(dir, "x.mov"), null, null, o, gpu: false);
        foreach (var t in gt.Concat(ct)) try { File.Delete(t); } catch { }
        int nGpu = System.Text.RegularExpressions.Regex.Matches(gpuScript, "scale_cuda=").Count;
        Check(nGpu == wantGpu && !cpuScript.Contains("scale_cuda") && gpuScript.Contains("format=p010le"),
              $"the graph scales {nGpu} clips on the GPU (the 4K H.264, 10-bit HEVC (p010) and HLG pieces and the picture-in-picture; not the 4:4:4 file); none with it off");

        var fGpu = Path.Combine(dir, "out_gpu.mov"); var fCpu = Path.Combine(dir, "out_cpu.mov");
        var (eg, tg) = await Export(ed, fGpu, o, Path.Combine(dir, "t_g"), true);
        string? report = o.GpuReport;
        var (ec, tc) = await Export(ed, fCpu, o, Path.Combine(dir, "t_c"), false);
        string wantReport = $"{wantGpu} clip{(wantGpu == 1 ? "" : "s")} scaled on the GPU";
        Check(eg == null && ec == null && report == wantReport && o.GpuReport == null,
              $"exports with ({tg:0.0} s, “{report}”) and without GPU scaling ({tc:0.0} s)" + ((eg ?? ec) is { } x ? ": " + x : ""));
        if (eg == null && ec == null)
        {
            var times = new[] { (0.5, "4K H.264"), (1.5, "+ graded, cropped picture-in-picture + title"), (3.0, "inside the dissolve into 10-bit HEVC"), (4.5, "10-bit HEVC"),
                                (6.5, "HLG (tone-mapped after the GPU's p010)"), (8.5, "4:4:4 file (CPU)") };
            var pv = new Preview { Ed = ed, Width = Editor.W, Height = Editor.H, Background = Brushes.Black };
            double worst = 99; var lines = new List<string>(); bool noWorse = true;
            foreach (var (t, what) in times)
            {
                var a = Frame(fGpu, t, W, H); var b = Frame(fCpu, t, W, H);
                double d = Psnr(a, b); worst = Math.Min(worst, d);
                var p = await PreviewFrame(pv, t);
                double pg = Psnr(p, Frame(fGpu, t, Editor.W, Editor.H)), pc = Psnr(p, Frame(fCpu, t, Editor.W, Editor.H));
                noWorse &= pg >= pc - 0.5;
                lines.Add($"{what} {d:0.0} dB (preview vs GPU / CPU export {pg:0.0} / {pc:0.0})");
            }
            Check(worst >= 28 && noWorse, $"the GPU-scaled export = the CPU path's, worst {worst:0.0} dB (CUDA bicubic ≠ swscale on testsrc2; 4:4:4 CPU clip 99 dB); preview vs export no worse: " + string.Join("; ", lines));
        }

        // ---------- GPU compositing (overlay_cuda): plain 8-bit SDR cuts, 4:2:0 composite ----------
        {
            var eo = new Editor(); eo.Media.Add(m8);
            Clip C(int track, double start, double inPt, double dur, Action<Clip>? f = null)
            {
                var c = new Clip { MediaId = m8.Id, Kind = ClipKind.Video, Track = track, Start = start, In = inPt, Dur = dur, NoAudio = true };
                f?.Invoke(c); eo.Clips.Add(c); return c;
            }
            C(Tracks.V1, 0, 0.5, 2);
            C(Tracks.V1, 2, 2.5, 2);
            C(Tracks.V(2), 0.5, 1, 2.5, c => { c.Scale = 40; c.X = -25; c.Y = -20; });
            var oc = new ExportSettings { Video = "prores4444", Container = "mov", Audio = "none", Full444 = false };
            Exporter.GpuScale = true;
            var (_, ovScript, ot) = Exporter.Build(eo, Path.Combine(dir, "ov.mov"), null, null, oc);
            var (_, ovCpu, otc) = Exporter.Build(eo, Path.Combine(dir, "ov.mov"), null, null, oc, gpu: false);
            foreach (var t in ot.Concat(otc)) try { File.Delete(t); } catch { }
            int nScale = System.Text.RegularExpressions.Regex.Matches(ovScript, "scale_cuda=").Count;
            int nOv = System.Text.RegularExpressions.Regex.Matches(ovScript, "overlay_cuda=").Count;
            Check(nScale >= 3 && nOv >= 2 && ovScript.Contains("hwdownload") && !ovCpu.Contains("overlay_cuda"),
                  $"plain 4K H.264 cuts: the graph scales {nScale} on the GPU and composites {nOv} there (picture-in-picture baked into each V1 cut); none with it off");
            var fOvG = Path.Combine(dir, "out_ov_gpu.mov"); var fOvC = Path.Combine(dir, "out_ov_cpu.mov");
            var (eog, tog) = await Export(eo, fOvG, oc, Path.Combine(dir, "t_og"), true);
            string ovReport = oc.GpuReport ?? "";
            var (eoc, toc) = await Export(eo, fOvC, oc, Path.Combine(dir, "t_oc"), false);
            Check(eog == null && eoc == null && ovReport != null && ovReport.Contains("composited"),
                  $"overlay_cuda export ({tog:0.0} s, “{ovReport}”) and CPU ({toc:0.0} s)" + ((eog ?? eoc) is { } xe ? ": " + xe : ""));
            if (eog == null && eoc == null)
            {
                double worst = 99; var lines = new List<string>();
                foreach (var (t, what) in new[] { (0.3, "first V1 cut"), (1.2, "picture-in-picture over V1"), (2.5, "second V1 cut") })
                {
                    double d = Psnr(Frame(fOvG, t, W, H), Frame(fOvC, t, W, H)); worst = Math.Min(worst, d);
                    lines.Add($"{what} {d:0.0} dB");
                }
                Check(worst >= 28, $"overlay_cuda = the CPU path's, worst {worst:0.0} dB (CUDA bicubic ≠ swscale): " + string.Join("; ", lines));
            }
            // a title on top downloads the GPU composite and finishes on the CPU
            eo.Clips.Add(new Clip { Kind = ClipKind.Text, Track = Tracks.V(3), Start = 0.2, Dur = 3, Text = "GPU", Style = new TextStyle { Size = 80 } });
            Exporter.GpuScale = true;
            var hyTitles = TitleVisual.PrepareExport(eo, Path.Combine(dir, "t_hy"));
            var (_, hyScript, ht) = Exporter.Build(eo, Path.Combine(dir, "hy.mov"), hyTitles, null, oc);
            foreach (var t in ht) try { File.Delete(t); } catch { }
            int hyOv = System.Text.RegularExpressions.Regex.Matches(hyScript, "overlay_cuda=").Count;
            Check(hyOv >= 2 && hyScript.Contains("hwdownload"),
                  $"a title over GPU-composited clips: {hyOv} overlay_cuda then a download into the CPU title overlay");
        }

        // ---------- the fallback ----------
        {
            Exporter.GpuBreak = true;
            var fb = Path.Combine(dir, "out_fallback.mov");
            var (ef, _) = await Export(ed, fb, o, Path.Combine(dir, "t_f"), true);
            Exporter.GpuBreak = false;
            bool same = ef == null && ec == null && new[] { 0.5, 4.5, 6.5 }.All(t => Psnr(Frame(fb, t, W, H), Frame(fCpu, t, W, H)) >= 99);
            Check(same && o.GpuReport?.StartsWith("GPU scaling failed (") == true && o.GpuReport.EndsWith("exported without it"),
                  $"a GPU chain ffmpeg refuses: exported again without it — frames = the CPU export's; “{o.GpuReport}”" + (ef is { } err ? ": " + err : ""));
        }

        // ---------- the speed-up ----------
        {
            var el = new Editor(); el.Media.Add(mLong);
            el.Clips.Add(new Clip { MediaId = mLong.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = 0, In = 0, Dur = 20, NoAudio = true });
            el.Clips.Add(new Clip { Kind = ClipKind.Text, Track = Tracks.V(2), Start = 2, Dur = 4, Text = "TITLE", Style = new TextStyle { Size = 70 } });
            var usable = await ExportSettings.UsableEncodersAsync();
            bool nvenc = usable.Contains("h264_nvenc");
            var os = nvenc ? new ExportSettings { Video = "h264", Encoder = "nvenc", Quality = 20, Audio = "none" } : new ExportSettings { Video = "h264", Speed = "ultrafast", Quality = 20, Audio = "none" };
            var (e1, s1) = await Export(el, Path.Combine(dir, "out_long_gpu.mp4"), os, Path.Combine(dir, "t_l1"), true);
            var (e2, s2) = await Export(el, Path.Combine(dir, "out_long_cpu.mp4"), os, Path.Combine(dir, "t_l2"), false);
            double gain = s2 / Math.Max(0.01, s1);
            Check(e1 == null && e2 == null && gain >= 0.9,
                  $"20 s of 4K HEVC into 1080p ({(nvenc ? "NVENC" : "x264 ultrafast")}): {600 / s1:0} fps with GPU scaling, {600 / s2:0} fps without ({gain:0.00}×) — not slower" + ((e1 ?? e2) is { } err ? ": " + err : ""));
            if (nvenc)
            {
                var en = new Editor(); en.Media.Add(m8);
                en.Clips.Add(new Clip { MediaId = m8.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = 0, In = 0, Dur = 6, NoAudio = true });
                var on = new ExportSettings { Video = "h264", Encoder = "nvenc", Quality = 18, Audio = "none", Full444 = false };
                Exporter.GpuScale = true;
                var (_, nvScript, nvt) = Exporter.Build(en, Path.Combine(dir, "nv.mp4"), null, null, on);
                foreach (var t in nvt) try { File.Delete(t); } catch { }
                Check(nvScript.Contains("overlay_cuda=") && !nvScript.Contains("hwdownload"),
                      "NVENC of a full-frame GPU cut: overlay_cuda, frames stay on the GPU (no hwdownload)");
                var fNvG = Path.Combine(dir, "out_nv_gpu.mp4"); var fNvC = Path.Combine(dir, "out_nv_cpu.mp4");
                var (eng, tng) = await Export(en, fNvG, on, Path.Combine(dir, "t_nvg"), true);
                var (enc, tnc) = await Export(en, fNvC, on, Path.Combine(dir, "t_nvc"), false);
                double nvP = eng == null && enc == null ? Psnr(Frame(fNvG, 1, W, H), Frame(fNvC, 1, W, H)) : 0;
                Check(eng == null && enc == null && nvP >= 28,
                      $"NVENC stays on the GPU ({tng:0.0} s) vs CPU ({tnc:0.0} s), {nvP:0.0} dB" + ((eng ?? enc) is { } nerr ? ": " + nerr : ""));
            }
        }
    }
}
