namespace CutPro;

/// <summary>--previewtest: the render-preview file is the same picture as a normal export (FFV1, no sound), and an edit drops it.</summary>
public static class PreviewTest
{
    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        bool was = Previews.Enabled;
        bool gpu = Exporter.GpuScale;
        try
        {
            Exporter.GpuScale = false;   // both renders on the CPU path, so a missing GPU can't make one of them fall back
            Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
            var pattern = Path.Combine(dir, "pattern.png");
            FxTest.Save(FxTest.Pattern(), pattern);
            var img = await MediaProbe.ProbeAsync(pattern) ?? throw new Exception("pattern import failed");
            var ed = new Editor();
            ed.Media.Add(img);
            var c = new Clip { MediaId = img.Id, Kind = ClipKind.Image, Track = Tracks.V1, Start = 0, Dur = 1, Scale = 70, X = 40, Y = -20 };
            ed.Clips.Add(c);
            ed.Clips.Add(new Clip { Kind = ClipKind.Text, Track = Tracks.V(2), Start = 0.2, Dur = 0.6, Text = "Preview", Style = StyleLibrary.Get("Bold Impact").With(s => s.Size = 80) });

            var err = Previews.RenderNow(ed);
            Check(err == null, "render preview wrote a file" + (err == null ? "" : ": " + err));
            Previews.Enabled = true;
            var hit = Previews.Picture(ed, 0.4);
            Check(hit != null && File.Exists(hit.Value.File), "playback can see the preview at 0.4 s");

            var opts = Previews.Options();
            var plain = Path.Combine(dir, "plain.nut");
            var tmp = Path.Combine(dir, "tmp_plain");
            var titles = TitleVisual.PrepareExport(ed, tmp, opts.OutW, opts.OutH);
            var masks = MaskExport.Prepare(ed, tmp, null, opts.Scale);
            var exp = await Exporter.RunAsync(ff, ed, plain, new Progress<double>(), CancellationToken.None, titles, masks, opts);
            Check(exp == null && File.Exists(plain), "normal export with the preview settings" + (exp == null ? "" : ": " + exp));
            if (hit != null && File.Exists(plain))
            {
                var a = SeqTest.Frame(ff, hit.Value.File, 0.4, 640, 360);
                var b = SeqTest.Frame(ff, plain, 0.4, 640, 360);
                double p = Psnr(a, b);
                Check(p >= 50, $"preview file = export at 0.4 s ({p:0.0} dB)");
            }

            c.X = -80;
            ed.Touch();
            var after = Previews.Picture(ed, 0.4);
            Check(after == null || hit == null || after.Value.File != hit.Value.File, "an edit stops playback using the old preview");
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        finally { Previews.Enabled = was; Exporter.GpuScale = gpu; Editor.SetSequence(1280, 720, 30, ReframeMode.Fit); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "previewtest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    static double Psnr(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length) / 4;
        double se = 0;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            double dr = a[o] - b[o], dg = a[o + 1] - b[o + 1], db = a[o + 2] - b[o + 2];
            se += dr * dr + dg * dg + db * db;
        }
        if (se <= 0 || n == 0) return 99;
        return 10 * Math.Log10(255.0 * 255 * n * 3 / se);
    }
}
