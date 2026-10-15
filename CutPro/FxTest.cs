using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// WYSIWYG check for titles, text masks and Gaussian blur: CutPro.exe --fxtest outDir (exit code 0 = all passed).
/// Builds a project over a test pattern, exports it, renders the Preview at the same instants and compares pixels
/// (PSNR + worst 16x16 block). Writes compare_*.png (preview | export | difference x4) for eyeballing.
/// </summary>
public static class FxTest
{
    static int W => Editor.W;
    const int H = Editor.H;

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");

        var pattern = Path.Combine(dir, "pattern.png");
        Save(Pattern(), pattern);
        var ed = new Editor();
        var m = await MediaProbe.ProbeAsync(pattern) ?? throw new Exception("pattern import failed: " + MediaProbe.LastError);
        ed.Media.Add(m);
        void Img(double start, double dur, double blur) => ed.Clips.Add(new Clip { MediaId = m.Id, Kind = ClipKind.Image, Track = 2, Start = start, Dur = dur, Blur = blur });
        Img(0, 1, 0); Img(1, 1, 20); Img(2, 4, 0);
        void Title(double start, string text, TextStyle s, Action<Clip>? f = null)
        {
            var c = new Clip { Kind = ClipKind.Text, Track = 1, Start = start, Dur = 1, Text = text, Style = s.Clone() };
            f?.Invoke(c); ed.Clips.Add(c);
        }
        Title(0, "Styled Title\nsecond line", new TextStyle
        {
            Font = "Georgia", Size = 100, Weight = 700, Italic = true, Tracking = 80, Leading = 110, Gradient = true, Fill = "#FFFFF1B8", Fill2 = "#FFFF3D7F",
            Stroke = true, StrokeColor = "#FF1A0033", StrokeWidth = 5, ShadowDistance = 10, ShadowBlur = 12, ShadowOpacity = 80,
            Glow = true, GlowColor = "#FF2EF2FF", GlowSize = 14, GlowOpacity = 60, Box = true, BoxColor = "#FF000000", BoxOpacity = 40, BoxPadding = 24, BoxRadius = 20,
        }, c => c.Rotation = 6);
        Title(2, "MASK", StyleLibrary.Get("Bold Impact").With(s => { s.Size = 330; }), c => c.Mask = MaskMode.Reveal);
        Title(3, "CUT", StyleLibrary.Get("Bold Impact").With(s => { s.Size = 330; }), c => { c.Mask = MaskMode.CutOut; c.MaskFeather = 12; });
        Title(4, "Name Surname\nTitle or role", StyleLibrary.Get("Lower Third"), c => { c.X = -42; c.Y = 30; c.Rotation = -4; c.Blur = 2; });
        Title(5, "Retro", StyleLibrary.Get("Retro Sunset"), c => c.Scale = 150);

        var titles = TitleVisual.PrepareExport(ed, Path.Combine(dir, "titles"));
        var mp4 = Path.Combine(dir, "fx.mp4");
        var err = await Exporter.RunAsync(ff, ed, mp4, new Progress<double>(), CancellationToken.None, titles);
        if (err != null) { File.WriteAllText(Path.Combine(dir, "fxtest.txt"), "FAIL export: " + err); return 1; }

        if (Environment.GetEnvironmentVariable("CUTPRO_FXDEBUG") != null)
        {
            var mc = ed.Clips.First(c => c.Mask == MaskMode.Reveal);
            string Box(BitmapSource bmp)
            {
                var px = new byte[W * H * 4]; new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0).CopyPixels(px, W * 4, 0);
                int x0 = W, x1 = 0, y0 = H, y1 = 0;
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i = (y * W + x) * 4; if (px[i + 3] > 128 && px[i + 1] > 128) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); } }
                return $"x {x0}..{x1} y {y0}..{y1}";
            }
            log.Add("matte (preview render): " + Box(TitleVisual.Render(mc, true)));
            log.Add("matte (export render):  " + Box(TitleVisual.Render(mc, true, opaque: true)));
            log.Add("matte (export png):     " + Box(new BitmapImage(new Uri(titles[mc.Id]))));
            log.Add("look (no mask) render:  " + Box(TitleVisual.Render(new Clip { Kind = ClipKind.Text, Text = mc.Text, Style = mc.Style }, false)));
            foreach (var r in new[] { 10.0, 20.0, 40.0 })   // WPF BlurEffect reference renders for calibrating ffmpeg gblur sigma
            {
                var g = new System.Windows.Controls.Grid { Width = W, Height = H, Background = Brushes.Black };
                g.Children.Add(new System.Windows.Controls.Image { Source = new BitmapImage(new Uri(pattern)), Effect = TitleVisual.Blur(r) });
                g.Measure(new Size(W, H)); g.Arrange(new Rect(0, 0, W, H)); g.UpdateLayout();
                var rb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32); rb.Render(g);
                Save(rb, Path.Combine(dir, $"wpf_blur{r}.png"));
            }
        }
        var pv = new Preview { Ed = ed, Width = W, Height = H, Background = Brushes.Black };
        var what = new Dictionary<double, string>
        {
            [0.5] = "styled title (gradient, outline, shadow, glow, box, tracking, rotation)",
            [1.5] = "Gaussian blur 20 on image",
            [2.5] = "text mask: reveal",
            [3.5] = "text mask: cut-out, feather 12",
            [4.5] = "lower third style + rotation + title blur",
            [5.5] = "Retro Sunset style, scale 150%",
        };
        foreach (var (t, name) in what)
        {
            pv.Sync(t, false);
            pv.Measure(new Size(W, H)); pv.Arrange(new Rect(0, 0, W, H)); pv.UpdateLayout();
            var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
            var a = new byte[W * H * 4]; rtb.CopyPixels(a, W * 4, 0);
            var b = Frame(ff, mp4, t);
            var (psnr, worst) = Compare(a, b);
            bool ok = psnr >= 30 && worst <= 24;
            if (!ok) fails++;
            log.Add($"{(ok ? "PASS" : "FAIL")} t={t:0.0}s {name}: PSNR {psnr:0.0} dB, worst 16x16 block diff {worst:0.0}/255");
            Save(SideBySide(a, b), Path.Combine(dir, $"compare_{t:0.0}.png".Replace(',', '.')));
        }
        // contact sheets: every built-in style, and every theme's four title roles
        var bg = new LinearGradientBrush(Color.FromRgb(40, 70, 110), Color.FromRgb(150, 110, 90), 30);
        Save(Sheet(StyleLibrary.BuiltIn.Select(s => (s.Name, new Clip { Kind = ClipKind.Text, Text = "Title Style", Style = s.Style.Clone() })).ToList(), 4, 640, 220, bg, crop: true),
             Path.Combine(dir, "styles.png"));
        var themeCells = new List<(string, Clip)>();
        foreach (var th in Themes.All)
            foreach (var role in Enum.GetValues<TextRole>())
            {
                var (x, y) = Themes.DefaultPosition(role);
                themeCells.Add(($"{th.Name} · {role}", new Clip { Kind = ClipKind.Text, Role = role, Text = Themes.DefaultText(role), X = x, Y = y, Style = th.For(role).Clone() }));
            }
        Save(Sheet(themeCells, 4, 400, 225, bg, crop: false), Path.Combine(dir, "themes.png"));

        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "fxtest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    internal static BitmapSource Pattern()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(20, 60, 160), Color.FromRgb(240, 140, 40), 0), null, new Rect(0, 0, W, H));
            var pen = new Pen(Brushes.White, 2);
            for (int x = 0; x <= W; x += 64) dc.DrawLine(pen, new Point(x, 0), new Point(x, H));
            for (int y = 0; y <= H; y += 64) dc.DrawLine(pen, new Point(0, y), new Point(W, y));
            var cols = new[] { Colors.Red, Colors.Lime, Colors.Yellow, Colors.Magenta, Colors.Cyan };
            for (int i = 0; i < 5; i++) dc.DrawEllipse(new SolidColorBrush(cols[i]), null, new Point(160 + i * 240, 520), 70, 70);
            for (int i = 0; i < 16; i++) for (int j = 0; j < 4; j++)
                if ((i + j) % 2 == 0) dc.DrawRectangle(Brushes.Black, null, new Rect(64 + i * 16, 64 + j * 16, 16, 16));
            dc.DrawText(new FormattedText("CutPro FX test pattern 0123456789", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 36, Brushes.White, 1.0), new Point(420, 70));
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv); rtb.Freeze();
        return rtb;
    }

    internal static byte[] Frame(string ff, string mp4, double t)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-v", "error", "-i", mp4, "-ss", t.ToString("0.###", CultureInfo.InvariantCulture), "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" })
            psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        var b = ms.ToArray();
        if (b.Length < W * H * 4) throw new Exception($"frame extract at {t}s failed");
        return b;
    }

    internal static (double psnr, double worst) Compare(byte[] a, byte[] b)
    {
        double se = 0; long n = 0;
        int bw = (W + 15) / 16;                                   // canvas widths follow the aspect (405 for 9:16)
        var block = new double[bw * ((H + 15) / 16)];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4; double d = 0;
                for (int ch = 0; ch < 3; ch++) { double e = a[i + ch] - b[i + ch]; se += e * e; d += Math.Abs(e); n++; }
                block[(y / 16) * bw + x / 16] += d / 3;
            }
        double mse = se / n;
        return (mse < 1e-9 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse), block.Max() / 256);
    }

    internal static BitmapSource SideBySide(byte[] a, byte[] b)
    {
        var diff = new byte[a.Length];
        for (int i = 0; i < a.Length; i += 4)
        {
            for (int ch = 0; ch < 3; ch++) diff[i + ch] = (byte)Math.Min(255, Math.Abs(a[i + ch] - b[i + ch]) * 4);
            diff[i + 3] = 255;
        }
        BitmapSource Bmp(byte[] px) => BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgr32, null, px, W * 4);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(Bmp(a), new Rect(0, 0, W / 2, H / 2));
            dc.DrawImage(Bmp(b), new Rect(W / 2, 0, W / 2, H / 2));
            dc.DrawImage(Bmp(diff), new Rect(W, 0, W / 2, H / 2));
        }
        var rtb = new RenderTargetBitmap(W * 3 / 2, H / 2, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv); rtb.Freeze();
        return rtb;
    }

    /// <summary>Grid of rendered titles with captions. crop: show the middle band of the frame (styles), else the whole frame (themes).</summary>
    static BitmapSource Sheet(List<(string name, Clip clip)> cells, int cols, int cw, int ch, Brush bg, bool crop)
    {
        int rows = (cells.Count + cols - 1) / cols;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, cols * cw, rows * ch));
            for (int i = 0; i < cells.Count; i++)
            {
                var r = new Rect(i % cols * cw, i / cols * ch, cw, ch);
                var img = TitleVisual.Render(cells[i].clip, false);
                Rect src = crop ? new Rect(0, 360 - 640.0 * ch / cw, 1280, 1280.0 * ch / cw) : new Rect(0, 0, 1280, 720);   // middle band / whole frame
                dc.PushClip(new RectangleGeometry(r));
                dc.DrawRectangle(bg, null, r);
                double s = cw / src.Width;
                dc.DrawImage(img, new Rect(r.X - src.X * s, r.Y - src.Y * s, 1280 * s, 720 * s));
                dc.DrawRectangle(null, new Pen(Brushes.Black, 2), r);
                dc.DrawText(new FormattedText(cells[i].name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.White, 1.0), new Point(r.X + 6, r.Y + 4));
                dc.Pop();
            }
        }
        var rtb = new RenderTargetBitmap(cols * cw, rows * ch, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv); rtb.Freeze();
        return rtb;
    }

    internal static void Save(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path); enc.Save(fs);
    }
}
