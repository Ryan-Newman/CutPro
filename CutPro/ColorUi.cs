using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CutPro;

/// <summary>Effect Controls → Lumetri Color and Stylize (partial MainWindow). Edits apply to every selected video / picture clip.</summary>
public partial class MainWindow
{
    static ColorGrade? gradeClipboard;
    static readonly List<string> recentCubes = new();

    List<Clip> GradeTargets(Clip c)
    {
        var l = ed.Clips.Where(x => ed.IsSel(x) && x.Kind is ClipKind.Video or ClipKind.Image).ToList();
        if (!l.Contains(c)) l.Add(c);
        return l;
    }
    void SetGrade(Clip c, Action<ColorGrade> f) { foreach (var x in GradeTargets(c)) f(x.Grade ??= new ColorGrade()); ed.Touch(); }

    TextBlock SubHead(string text, Panel into)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg");
        into.Children.Add(t); return t;
    }

    void BuildColorInspector(Clip c)
    {
        if (c.Kind is not (ClipKind.Video or ClipKind.Image or ClipKind.Adjustment)) return;   // (an adjustment layer grades the tracks below)
        var g = c.Grade ?? new ColorGrade();
        var body = Section("Lumetri color", open: c.Grade != null);
        var bar = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        Button Btn(string text, string tip, Action a) { var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4) }; b.Click += (_, _) => a(); bar.Children.Add(b); return b; }
        Btn("Auto", "Exposure, white and black points and white balance from the frame at the playhead", () => AutoGrade(c));
        Btn("Reset", "Back to the original colours", () => { foreach (var x in GradeTargets(c)) x.Grade = null; ed.Touch(); BuildInspector(); });
        Btn("Copy", "Copy this clip's colour", () => gradeClipboard = c.Grade?.Clone());
        Btn("Paste", "Paste the copied colour onto the selected clips", () => { if (gradeClipboard == null) return; foreach (var x in GradeTargets(c)) x.Grade = gradeClipboard.Clone(); ed.Touch(); BuildInspector(); });
        body.Children.Add(bar);

        SubHead("Basic correction", body);
        Slider("Temperature", -100, 100, 1, g.Temperature, v => SetGrade(c, x => x.Temperature = v), body);
        Slider("Tint", -100, 100, 1, g.Tint, v => SetGrade(c, x => x.Tint = v), body);
        Slider("Exposure", -4, 4, 0.05, g.Exposure, v => SetGrade(c, x => x.Exposure = v), body);
        Slider("Contrast", -100, 100, 1, g.Contrast, v => SetGrade(c, x => x.Contrast = v), body);
        Slider("Highlights", -100, 100, 1, g.Highlights, v => SetGrade(c, x => x.Highlights = v), body);
        Slider("Shadows", -100, 100, 1, g.Shadows, v => SetGrade(c, x => x.Shadows = v), body);
        Slider("Whites", -100, 100, 1, g.Whites, v => SetGrade(c, x => x.Whites = v), body);
        Slider("Blacks", -100, 100, 1, g.Blacks, v => SetGrade(c, x => x.Blacks = v), body);
        Slider("Saturation", 0, 200, 1, g.Saturation, v => SetGrade(c, x => x.Saturation = v), body);
        Slider("Vibrance", -100, 100, 1, g.Vibrance, v => SetGrade(c, x => x.Vibrance = v), body);

        SubHead("Creative", body);
        var look = new ComboBox();
        var items = new List<string> { "None" }; items.AddRange(Grade.Looks);
        foreach (var r in recentCubes) if (!items.Contains(r)) items.Add(r);
        if (Grade.IsCube(g.Look) && !items.Contains(g.Look!)) items.Add(g.Look!);
        items.Add("Browse .cube LUT…");
        look.ItemsSource = items.Select(i => Grade.IsCube(i) ? System.IO.Path.GetFileName(i) : i).ToList();
        look.SelectedIndex = string.IsNullOrEmpty(g.Look) ? 0 : Math.Max(0, items.IndexOf(g.Look!));
        look.SelectionChanged += (_, _) =>
        {
            if (look.SelectedIndex < 0) return;
            string pick = items[look.SelectedIndex];
            if (pick.StartsWith("Browse"))
            {
                var dlg = new OpenFileDialog { Filter = "3D LUT (*.cube)|*.cube|All files|*.*", Title = "Choose a .cube LUT" };
                if (dlg.ShowDialog(this) != true) { BuildInspector(); return; }
                if (Grade.Cube(dlg.FileName) == null) { Alert.Show(this, "That file isn't a .cube LUT CutPro can read.", "Look", AlertKind.Warning); BuildInspector(); return; }
                pick = dlg.FileName; if (!recentCubes.Contains(pick)) recentCubes.Insert(0, pick);
                Grade.Forget();
            }
            SetGrade(c, x => x.Look = pick == "None" ? null : pick);
            BuildInspector();
        };
        Row(body, "Look", look);
        Slider("Intensity", 0, 100, 1, g.LookAmount, v => SetGrade(c, x => x.LookAmount = v), body);
        Slider("Faded film", 0, 100, 1, g.Faded, v => SetGrade(c, x => x.Faded = v), body);

        SubHead("Color wheels", body);
        var wheels = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var (label, get) in new (string, Func<ColorGrade, Wheel>)[] { ("Shadows", x => x.Lift), ("Midtones", x => x.Gamma), ("Highlights", x => x.Gain) })
        {
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var tl = new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, Foreground = Dim };
            var w = new ColorWheelControl(); var wv = get(g); w.Set(wv.Hue, wv.Amount);
            w.Moved += (h, a) => SetGrade(c, x => { var wh = get(x); wh.Hue = h; wh.Amount = a; });
            var luma = new Slider { Minimum = -100, Maximum = 100, Value = wv.Luma, Width = 72, Margin = new Thickness(0, 2, 0, 0), ToolTip = "Brightness of this range (double-click the wheel to reset its colour)" };
            luma.ValueChanged += (_, e) => SetGrade(c, x => get(x).Luma = Math.Round(e.NewValue));
            col.Children.Add(tl); col.Children.Add(w); col.Children.Add(luma);
            wheels.Children.Add(col);
        }
        body.Children.Add(wheels);

        SubHead("Curves", body);
        var curve = new CurveEditor(); curve.Set(g.Curve, g.CurveR, g.CurveG, g.CurveB);
        var chans = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        var chButtons = new List<System.Windows.Controls.Primitives.ToggleButton>();
        foreach (var (name, i) in new[] { ("RGB", 0), ("R", 1), ("G", 2), ("B", 3) })
        {
            var tb = new System.Windows.Controls.Primitives.ToggleButton { Content = name, IsChecked = i == 0, Style = (Style)FindResource("Tool"), Padding = new Thickness(8, 1, 8, 1) };
            tb.Click += (_, _) => { curve.Channel = i; foreach (var o in chButtons) o.IsChecked = o == tb; };
            chans.Children.Add(tb); chButtons.Add(tb);
        }
        curve.Changed += (ch, pts) => SetGrade(c, x => { if (ch == 0) x.Curve = pts; else if (ch == 1) x.CurveR = pts; else if (ch == 2) x.CurveG = pts; else x.CurveB = pts; });
        body.Children.Add(chans); body.Children.Add(curve);

        SubHead("Vignette", body);
        Slider("Amount", -100, 100, 1, g.Vignette, v => SetGrade(c, x => x.Vignette = v), body);
        Slider("Midpoint", 0, 100, 1, g.VigMidpoint, v => SetGrade(c, x => x.VigMidpoint = v), body);
        Slider("Feather", 0, 100, 1, g.VigFeather, v => SetGrade(c, x => x.VigFeather = v), body);
        Slider("Roundness", 0, 100, 1, g.VigRoundness, v => SetGrade(c, x => x.VigRoundness = v), body);
        BuildHslSecondary(c, g, body);
    }

    /// <summary>Lumetri's HSL Secondary: pick a colour range (eyedropper on the monitor, or a preset), see it as a mask, correct only it.</summary>
    void BuildHslSecondary(Clip c, ColorGrade g, Panel body)
    {
        SubHead("HSL Secondary", body);
        var k = g.Hsl;
        var on = new CheckBox { Content = "Correct one colour range (skin, sky, leaves…)", IsChecked = k != null, Margin = new Thickness(0, 2, 0, 4) };
        on.Click += (_, _) => { SetGrade(c, x => x.Hsl = on.IsChecked == true ? x.Hsl ?? new HslKey() : null); Dispatcher.BeginInvoke(BuildInspector); };
        body.Children.Add(on);
        if (k == null) return;
        void H(Action<HslKey> f) => SetGrade(c, x => f(x.Hsl ??= new HslKey()));
        var bar = new WrapPanel { Margin = new Thickness(86, 0, 0, 4) };
        Button B(string text, string tip, Action a) { var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4) }; b.Click += (_, _) => a(); bar.Children.Add(b); return b; }
        var pick = B("Pick from monitor", "Click the colour in the program monitor", () => { });
        pick.Click += (_, _) =>
        {
            ed.Seek(Math.Clamp(ed.Time, c.Start, c.End - 1 / TimeUtil.Fps));
            pick.Content = "Click a colour in the monitor…";
            Mo.Cursor = System.Windows.Input.Cursors.Cross;
            Mo.PickColor = (tc, nx, ny) => { if (tc.Id == c.Id) _ = SampleHslAsync(tc, tc.FlipH ? 1 - nx : nx, tc.FlipV ? 1 - ny : ny); else BuildInspector(); };
        };
        foreach (var (name, hue, width, s0, s1, l0, l1) in new[] { ("Skin", 25.0, 18.0, 10.0, 65.0, 15.0, 92.0), ("Sky", 212.0, 25.0, 12.0, 100.0, 25.0, 100.0), ("Leaves", 100.0, 35.0, 12.0, 100.0, 5.0, 95.0) })
            B(name, $"Select {name.ToLowerInvariant()} tones", () => { H(x => { x.Hue = hue; x.HueWidth = width; x.SatMin = s0; x.SatMax = s1; x.LumMin = l0; x.LumMax = l1; }); BuildInspector(); });
        body.Children.Add(bar);
        Slider("Hue", 0, 360, 1, k.Hue, v => H(x => x.Hue = v), body);
        Slider("Hue range", 0, 180, 1, k.HueWidth, v => H(x => x.HueWidth = v), body);
        Slider("Sat. min", 0, 100, 1, k.SatMin, v => H(x => x.SatMin = v), body);
        Slider("Sat. max", 0, 100, 1, k.SatMax, v => H(x => x.SatMax = v), body);
        Slider("Luma min", 0, 100, 1, k.LumMin, v => H(x => x.LumMin = v), body);
        Slider("Luma max", 0, 100, 1, k.LumMax, v => H(x => x.LumMax = v), body);
        Slider("Softness", 0, 100, 1, k.Soft, v => H(x => x.Soft = v), body);
        var flags = new WrapPanel { Margin = new Thickness(86, 2, 0, 4) };
        var inv = new CheckBox { Content = "Invert", IsChecked = k.Invert, Margin = new Thickness(0, 0, 12, 0) };
        inv.Click += (_, _) => H(x => x.Invert = inv.IsChecked == true);
        var mask = new CheckBox { Content = "Show mask", IsChecked = k.ShowMask, ToolTip = "White = selected (turn it off before exporting)" };
        mask.Click += (_, _) => H(x => x.ShowMask = mask.IsChecked == true);
        flags.Children.Add(inv); flags.Children.Add(mask); body.Children.Add(flags);
        Slider("Temperature", -100, 100, 1, k.Temperature, v => H(x => x.Temperature = v), body);
        Slider("Tint", -100, 100, 1, k.Tint, v => H(x => x.Tint = v), body);
        Slider("Exposure", -4, 4, 0.05, k.Exposure, v => H(x => x.Exposure = v), body);
        Slider("Contrast", -100, 100, 1, k.Contrast, v => H(x => x.Contrast = v), body);
        Slider("Saturation", 0, 200, 1, k.Saturation, v => H(x => x.Saturation = v), body);
        Slider("Hue shift", -180, 180, 1, k.HueShift, v => H(x => x.HueShift = v), body);
    }

    /// <summary>The eyedropper: the colour under the click as the grade so far shows it (the clip's grade without the secondary) → a range around it.</summary>
    async Task SampleHslAsync(Clip c, float nx, float ny)
    {
        var f = await Task.Run(() => KeySourceFrame(c, 960));
        if (f is { } fr)
        {
            int cx = Math.Clamp((int)(nx * fr.w), 3, fr.w - 4), cy = Math.Clamp((int)(ny * fr.h), 3, fr.h - 4);
            long r = 0, gs = 0, b = 0; int n = 0;
            for (int y = cy - 3; y <= cy + 3; y++) for (int x = cx - 3; x <= cx + 3; x++) { int o = (y * fr.w + x) * 4; b += fr.px[o]; gs += fr.px[o + 1]; r += fr.px[o + 2]; n++; }
            var px = new byte[] { (byte)(b / n), (byte)(gs / n), (byte)(r / n), 255 };
            var primary = c.Grade?.Clone(); if (primary != null) primary.Hsl = null;
            if (Grade.For(primary) is { } lut) Grade.Apply(lut, px, 1, 1, 4);
            var (h, s, l) = Grade.Hsl(px[2] / 255.0, px[1] / 255.0, px[0] / 255.0);
            ed.Edit(() =>
            {
                foreach (var x in GradeTargets(c))
                {
                    var k = (x.Grade ??= new ColorGrade()).Hsl ??= new HslKey();
                    k.Hue = Math.Round(h); k.HueWidth = 15;
                    k.SatMin = Math.Max(0, Math.Round(s * 100 - 20)); k.SatMax = Math.Min(100, Math.Round(s * 100 + 30));
                    k.LumMin = Math.Max(0, Math.Round(l * 100 - 30)); k.LumMax = Math.Min(100, Math.Round(l * 100 + 30));
                }
            });
        }
        BuildInspector();
    }

    /// <summary>Auto: from the frame at the playhead (graded basics reset first) — exposure to a mid-grey average, white and black points to
    /// the 99th / 1st percentile of brightness, white balance by grey world.</summary>
    void AutoGrade(Clip c)
    {
        foreach (var x in GradeTargets(c)) { var gr = x.Grade ??= new ColorGrade(); gr.Exposure = gr.Whites = gr.Blacks = gr.Temperature = gr.Tint = 0; }
        Pv.Sync(ed.Time, false);
        Pv.Measure(new Size(Editor.W, Editor.H)); Pv.Arrange(new Rect(0, 0, Editor.W, Editor.H)); Pv.UpdateLayout();
        int w = Editor.W / 2, h = Editor.H / 2;
        var rtb = new RenderTargetBitmap(w, h, 48, 48, PixelFormats.Pbgra32); rtb.Render(Pv);
        var px = new byte[w * h * 4]; rtb.CopyPixels(px, w * 4, 0);
        var lum = new List<double>(w * h / 4); double sr = 0, sg = 0, sb = 0; int n = 0;
        static double Lin(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        for (int i = 0; i < px.Length; i += 16)
        {
            double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
            lum.Add(0.2126 * r + 0.7152 * g + 0.0722 * b); sr += Lin(r); sg += Lin(g); sb += Lin(b); n++;
        }
        if (n == 0) return;
        lum.Sort();
        double p1 = lum[(int)(lum.Count * 0.01)], p99 = lum[(int)(lum.Count * 0.99)], mean = lum.Average();
        double ev = Math.Clamp(Math.Log2(Lin(0.45) / Math.Max(1e-4, Lin(mean))), -2, 2);
        double Up(double v) => Math.Min(1, Math.Pow(Lin(v) * Math.Pow(2, ev), 1 / 2.4) * 1.055 - 0.055);
        double whites = Math.Clamp((1 - Math.Max(0.5, Up(p99) / 0.97 * 0.97)) / 0.25 * 100, -60, 60);
        double blacks = Math.Clamp(-(Up(p1) - 0.02) / 0.12 * 100, -60, 60);
        double mr = sr / n, mg = sg / n, mb = sb / n;
        double temp = Math.Clamp((mb - mr) / Math.Max(1e-4, mr + mb) * 300, -60, 60), tint = Math.Clamp((mg - (mr + mb) / 2) / Math.Max(1e-4, mg) * 300, -60, 60);
        SetGrade(c, x => { x.Exposure = Math.Round(ev, 2); x.Whites = Math.Round(whites); x.Blacks = Math.Round(blacks); x.Temperature = Math.Round(temp); x.Tint = Math.Round(tint); });
        BuildInspector();
    }

    readonly DispatcherTimer toonDebounce = new() { Interval = TimeSpan.FromMilliseconds(280) };
    Action? toonPending;

    void BuildToonInspector(Clip c)
    {
        if (c.Kind is not (ClipKind.Video or ClipKind.Image)) return;
        var body = Section("Stylize — cartoon, cel, anime", open: Toon.Active(c.Toon));
        var targets = GradeTargets(c);
        var mode = new ComboBox { ItemsSource = new[] { "None", "Cartoon", "Cel shading", "Anime" }, SelectedIndex = (int)(c.Toon?.Mode ?? ToonMode.None) };
        mode.SelectionChanged += (_, _) =>
        {
            var m = (ToonMode)Math.Max(0, mode.SelectedIndex);
            ed.Edit(() => { foreach (var x in targets) x.Toon = m == ToonMode.None ? null : ToonStyle.Preset(m); });
            BuildInspector();
        };
        Row(body, "Style", mode);
        if (!Toon.Active(c.Toon)) return;
        var s = c.Toon!;
        // the decoder restarts when the look changes: apply slider changes once the slider rests
        void Later(Action<ToonStyle> f)
        {
            toonPending = () => { foreach (var x in targets) if (x.Toon != null) f(x.Toon); ed.Touch(); };
            toonDebounce.Stop(); toonDebounce.Start();
        }
        toonDebounce.Tick -= ToonTick; toonDebounce.Tick += ToonTick;
        Slider("Smoothing", 0, 100, 1, s.Smooth, v => Later(x => x.Smooth = v), body);
        Slider("Bands", 2, 12, 1, s.Levels, v => Later(x => x.Levels = v), body);
        Slider("Outlines", 0, 4, 0.25, s.Lines, v => Later(x => x.Lines = v), body);
        Slider("Ink", 0, 100, 1, s.Ink, v => Later(x => x.Ink = v), body);
        Slider("Saturation", 0, 200, 1, s.Saturation, v => Later(x => x.Saturation = v), body);
        var note = new TextBlock { Text = "Rendered with ffmpeg in the preview's decoder and in the export, so both look the same. Outlines scale with the export size.",
                                   TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim, Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(note);
    }
    void ToonTick(object? s, EventArgs e) { toonDebounce.Stop(); var a = toonPending; toonPending = null; a?.Invoke(); }
}
