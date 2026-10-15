namespace CutPro;

/// <summary>
/// Headless import/decode check: CutPro.exe --probetest mediaDir out.txt (exit code 0 = all passed).
/// For every file: ffprobe import, a decoded preview frame, conformed audio, then an MP4 export of everything.
/// </summary>
public static class ProbeTest
{
    public static async Task<int> Run(string dir, string outFile)
    {
        var log = new List<string>(); int fails = 0;
        void Line(bool ok, string s) { log.Add((ok ? "PASS " : "FAIL ") + s); if (!ok) fails++; }
        var ff = Exporter.FindFfmpeg();
        var ed = new Editor();

        foreach (var path in Directory.GetFiles(dir).Where(p => !p.EndsWith(".txt") && !p.EndsWith(".mp4.error.txt") && !Path.GetFileName(p).StartsWith("export")).OrderBy(p => p))
        {
            var name = Path.GetFileName(path);
            var m = await MediaProbe.ProbeAsync(path);
            if (m == null) { Line(false, $"{name}: import failed: {MediaProbe.LastError}"); continue; }
            var info = $"{name}: {m.Type} {m.Duration:0.00}s {m.Width}x{m.Height} audio={m.HasAudio} thumb={(m.Thumb != null ? "yes" : "no")}";
            bool ok = m.Duration > 0 && (m.Type != ClipKind.Video || (m.Width > 0 && m.Thumb != null));
            if (name.Contains("rotated")) ok &= m.Height > m.Width;                       // phone portrait must come out portrait
            if (m.Type == ClipKind.Video)
            {
                ScrubCache.WaitReady(m.Path, 120000);
                int want = (int)Math.Floor(m.Duration * ScrubCache.Fps), got = ScrubCache.FrameCount(m.Path);
                bool cacheOk = Math.Abs(got - want) <= 2;
                info += $" decode={m.SeekDecoder}/{m.PlayDecoder} scrubCache={got}/{want}f";
                ok &= cacheOk;
            }

            if (m.Type == ClipKind.Video && ff != null)
            {
                int w = 640, h = Math.Max(2, (int)Math.Round(640.0 * m.Height / m.Width / 2) * 2);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var fs = new FrameStream(ff, path, Math.Min(1, m.Duration / 2), w, h, 3, 0);
                (long idx, byte[]? buf) f = (-1, null);
                while (sw.ElapsedMilliseconds < 10000 && (f = fs.Take(fs.Start)).buf == null) await Task.Delay(10);
                bool frameOk = f.buf != null && Varied(f.buf);
                info += $" firstFrame={(f.buf != null ? sw.ElapsedMilliseconds + "ms" : "none")}";
                ok &= frameOk;

            }
            if (m.HasAudio)
            {
                AudioCache.WaitReady(m.Path, 20000);
                var pcm = new short[4800 * 2];
                bool audioOk = AudioCache.Read(m.Path, AudioCache.Rate, pcm, 4800) && pcm.Max(x => Math.Abs((int)x)) > 1000;
                info += $" audio={(audioOk ? "ok" : "SILENT/MISSING")}";
                ok &= audioOk;
            }
            Line(ok, info);
            ed.Media.Add(m);
        }

        // export everything back to back (video/image on V1 with linked audio, audio-only on A2)
        double t = 0;
        foreach (var m in ed.Media)
        {
            if (m.Type == ClipKind.Audio) { ed.AddMedia(m, 4, 0); continue; }
            ed.AddMedia(m, null, t); t = ed.End;
        }
        if (ff != null && ed.Clips.Count > 0)
        {
            var outMp4 = Path.Combine(dir, "export_all.mp4");
            var err = await Exporter.RunAsync(ff, ed, outMp4, new Progress<double>(), CancellationToken.None);
            Line(err == null && File.Exists(outMp4) && new FileInfo(outMp4).Length > 10000, $"export of all formats ({ed.End:0.0}s) {(err == null ? "" : err.Split('\n').LastOrDefault(l => l.Length > 0))}");
        }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(outFile, log);
        return fails == 0 ? 0 : 1;
    }

    static bool Varied(byte[] px)
    {
        int min = 255, max = 0;
        for (int i = 0; i < px.Length; i += 4 * 97) { min = Math.Min(min, px[i + 1]); max = Math.Max(max, px[i + 1]); }
        return max - min > 40;
    }
}
