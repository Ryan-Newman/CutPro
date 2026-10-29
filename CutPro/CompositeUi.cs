using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CutPro;

/// <summary>Effect Controls → Opacity: Blend mode; Crop and flip; Ultra key (partial MainWindow). Edits apply to every selected clip that has the
/// property (crop / key: video and pictures; blend: also titles).</summary>
public partial class MainWindow
{
    List<Clip> BlendTargets(Clip c)
    {
        var l = ed.Clips.Where(x => ed.IsSel(x) && (x.Kind is ClipKind.Video or ClipKind.Image || x.Kind == ClipKind.Text && x.Mask == MaskMode.None)).ToList();
        if (!l.Contains(c)) l.Add(c);
        return l;
    }

    /// <summary>Opacity group: how the clip mixes with the tracks below.</summary>
    void BuildBlendRow(Clip c)
    {
        if (c.Kind == ClipKind.Audio || c.Kind == ClipKind.Text && c.Mask != MaskMode.None) return;
        var cb = new ComboBox { ToolTip = "How this clip mixes with the tracks below it (the export uses the same formulas)" };
        int group = 0;
        foreach (var (mode, name, g) in Blend.Modes)
        {
            if (g != group) { cb.Items.Add(new ComboBoxItem { Content = new Separator { Margin = new Thickness(0, 1, 0, 1) }, IsEnabled = false, Focusable = false, Padding = new Thickness(0) }); group = g; }
            var it = new ComboBoxItem { Content = name, Tag = mode };
            cb.Items.Add(it);
            if (mode == c.Blend) cb.SelectedItem = it;
        }
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is not ComboBoxItem { Tag: BlendMode m }) return;
            ed.Edit(() => { foreach (var x in BlendTargets(c)) x.Blend = m; });
        };
        Row(Inspector, "Blend mode", cb);
        if (c.Blend != BlendMode.Normal && Blend.Shader(c.Blend) == null)
            Inspector.Children.Add(new TextBlock { Text = "This PC can't show blend modes in the preview (" + (Blend.CompileError ?? "no shader compiler") + "); the export still uses them.",
                                                   TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim });
    }

    /// <summary>Opacity group: use the clip as a track matte for everything below it (it isn't drawn then).</summary>
    void BuildMatteRow(Clip c)
    {
        if (c.Kind is not (ClipKind.Video or ClipKind.Image)) return;
        string[] items = { "Off", "Reveal by brightness", "Cut out by brightness", "Reveal by alpha", "Cut out by alpha" };
        int cur = c.Mask == MaskMode.None ? 0 : (c.MatteLuma ? 0 : 2) + (c.Mask == MaskMode.Reveal ? 1 : 2);
        var cb = new ComboBox { ItemsSource = items, SelectedIndex = cur,
                                ToolTip = "Use this clip as a matte for the tracks below it: white (or opaque) shows them, black (or transparent) hides them — the clip itself isn't drawn" };
        cb.SelectionChanged += (_, _) =>
        {
            int i = cb.SelectedIndex;
            ed.Edit(() =>
            {
                foreach (var x in GradeTargets(c))
                {
                    x.Mask = i == 0 ? MaskMode.None : i is 1 or 3 ? MaskMode.Reveal : MaskMode.CutOut;
                    x.MatteLuma = i is 1 or 2 || i == 0;
                    if (x.Mask != MaskMode.None) { x.TransIn = x.TransOut = null; }      // (track mattes take no transitions)
                }
            });
        };
        Row(Inspector, "Track matte", cb);
    }

    /// <summary>Corner pin: the picture's four corners anywhere in the frame (% of the frame), keyframable; drag them on the monitor
    /// (PinOverlay). Starts at the clip's current corners.</summary>
    void BuildPinInspector(Clip c)
    {
        if (c.Kind is not (ClipKind.Video or ClipKind.Image)) return;
        var body = Section("Corner pin", open: c.Pin != null);
        var targets = GradeTargets(c);
        var on = new CheckBox { Content = "Pin the corners (replaces Motion)", IsChecked = c.Pin != null, Margin = new Thickness(0, 2, 0, 4) };
        on.Click += (_, _) =>
        {
            ed.Edit(() =>
            {
                foreach (var x in targets)
                {
                    if (on.IsChecked != true) { x.Pin = null; ClearPinKeys(x); continue; }
                    var (px, py, pw, ph, _) = Editor.Placement(ed.MediaOf(x), x, Math.Clamp(ed.Time, x.Start, x.End));
                    double l = px / Editor.W * 100, t = py / Editor.H * 100, r = (px + pw) / Editor.W * 100, b = (py + ph) / Editor.H * 100;
                    x.Pin = new CornerPin { TLX = l, TLY = t, TRX = r, TRY = t, BLX = l, BLY = b, BRX = r, BRY = b };
                }
            });
            BuildInspector(); Po.InvalidateVisual();
        };
        body.Children.Add(on);
        if (c.Pin == null) return;
        foreach (var p in Pin.Props) PropRow(c, p, body);
        if (c.Mask != MaskMode.None && Anim.AnyAnimated(c, Pin.Props))
            body.Children.Add(new TextBlock { Text = "A track matte uses the base corners (its keyframes are kept for when it isn't a matte).", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim });
        else if (Pin.Shader() == null)
            body.Children.Add(new TextBlock { Text = "This PC can't warp in the preview with a shader (" + (Pin.CompileError ?? "no shader compiler") + "): the preview shows the base corners; the export animates them.",
                                              TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim });
        body.Children.Add(new TextBlock { Text = "Drag the corners on the monitor (Shift: horizontally or vertically only). With keyframes on, a drag sets a key at the playhead.",
                                          TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim, Margin = new Thickness(0, 4, 0, 0) });
        var reset = new Button { Content = "Reset to the full frame", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => { ed.Edit(() => { foreach (var x in targets) if (x.Pin != null) { x.Pin = new CornerPin(); ClearPinKeys(x); } }); BuildInspector(); Po.InvalidateVisual(); };
        body.Children.Add(reset);
    }

    /// <summary>Lens distortion: curvature (negative straightens a wide-angle / action camera's curved lines, positive bulges the picture), an edge
    /// term and the centre — keyframable; presets.</summary>
    void BuildLensInspector(Clip c)
    {
        if (c.Kind is not (ClipKind.Video or ClipKind.Image)) return;
        var body = Section("Lens distortion", open: c.Lens != null);
        var targets = GradeTargets(c);
        void Use(LensDistort? l)
        {
            ed.Edit(() => { foreach (var x in targets) { x.Lens = l?.Clone(); ClearLensKeys(x); } });
            BuildInspector();
        }
        var on = new CheckBox { Content = "Distort like a lens", IsChecked = c.Lens != null, Margin = new Thickness(0, 2, 0, 4) };
        on.Click += (_, _) => Use(on.IsChecked == true ? new LensDistort { Curvature = -30 } : null);
        body.Children.Add(on);
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (name, tip, l) in new (string, string, LensDistort)[]
        {
            ("Fix wide-angle", "Straightens the bowed lines of an action camera / phone ultra-wide (GoPro look)", new() { Curvature = -35, Edge = 8 }),
            ("Subtle fix", "A gentle correction for a slightly curved horizon", new() { Curvature = -15 }),
            ("Fisheye", "Bulges the picture like a fisheye lens (the corners become transparent)", new() { Curvature = 55, Edge = 10 }),
            ("Pincushion", "Pinches the middle and stretches the edges", new() { Curvature = -70, Edge = -10 }),
        })
        {
            var b = new Button { Content = name, ToolTip = tip, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4) };
            b.Click += (_, _) => Use(l);
            bar.Children.Add(b);
        }
        body.Children.Add(bar);
        if (c.Lens == null) return;
        foreach (var p in Lens.Props) PropRow(c, p, body);
        if (c.Mask != MaskMode.None && Anim.AnyAnimated(c, Lens.Props))
            body.Children.Add(new TextBlock { Text = "A track matte uses the base values (its keyframes are kept for when it isn't a matte).", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim });
        else if (Pin.Shader() == null)
            body.Children.Add(new TextBlock { Text = "This PC can't warp in the preview with a shader (" + (Pin.CompileError ?? "no shader compiler") + "): the preview shows the base values; the export animates them.",
                                              TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim });
        body.Children.Add(new TextBlock { Text = "Curvature below 0 straightens a wide-angle lens's curved lines; above 0 bulges the picture. Runs before the corner pin; crop and masks act on the distorted picture.",
                                          TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim, Margin = new Thickness(0, 4, 0, 0) });
    }

    /// <summary>Effect Controls → Distort: twirl, wave, spherize or a mesh warp (its points dragged on the monitor).</summary>
    void BuildDistortInspector(Clip c)
    {
        if (c.Kind is not (ClipKind.Video or ClipKind.Image)) return;
        var body = Section("Distort", open: c.Distort != null);
        var targets = GradeTargets(c).Where(x => x.Kind is ClipKind.Video or ClipKind.Image).ToList();
        if (c.Pin != null || c.Lens != null)
        {
            body.Children.Add(new TextBlock { Text = "Turn off the corner pin and lens distortion to twirl, wave, spherize or mesh-warp this clip.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim });
            return;
        }
        var kinds = new ComboBox { ItemsSource = new[] { "None", "Twirl", "Wave", "Spherize (bulge / pinch)", "Mesh warp (drag the points)" }, SelectedIndex = c.Distort == null ? 0 : (int)c.Distort.Kind + 1 };
        kinds.SelectionChanged += (_, _) =>
        {
            int i = kinds.SelectedIndex;
            ed.Edit(() =>
            {
                foreach (var x in targets)
                {
                    if (i == 0) { x.Distort = null; if (x.Anim != null) { foreach (var p in Distorts.TwirlProps.Concat(Distorts.WaveProps)) x.Anim.Remove(p); Anim.Prune(x); } }
                    else x.Distort = new Distort
                    {
                        Kind = (DistortKind)(i - 1),
                        Amount = (DistortKind)(i - 1) switch { DistortKind.Twirl => 180, DistortKind.Wave => 3, DistortKind.Spherize => 50, _ => 0 },
                    };
                }
            }, "Distort");
            BuildInspector();
        };
        Row(body, "Effect", kinds);
        if (c.Distort is not { } d) return;
        switch (d.Kind)
        {
            case DistortKind.Twirl:
                PropRow(c, AnimProp.DistAmount, body);
                PropRow(c, AnimProp.DistRadius, body);
                break;
            case DistortKind.Spherize:
                PropRow(c, AnimProp.DistAmount, body);
                PropRow(c, AnimProp.DistRadius, body);
                break;
            case DistortKind.Wave:
                PropRow(c, AnimProp.DistAmount, body);
                PropRow(c, AnimProp.DistWave, body);
                PropRow(c, AnimProp.DistDir, body);
                PropRow(c, AnimProp.DistSpeed, body);
                break;
            default:
                var reset = new Button { Content = "Reset the mesh", Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) };
                reset.Click += (_, _) => { ed.Edit(() => { foreach (var x in targets) if (x.Distort != null) x.Distort.Mesh = new double[32]; }, "Mesh Warp"); Pv.InvalidateVisual(); meshOverlay?.InvalidateVisual(); };
                body.Children.Add(reset);
                body.Children.Add(new TextBlock { Text = "Pause on the clip and drag the 16 points on the Program monitor: the picture moves with them.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim });
                break;
        }
        if (d.Kind != DistortKind.Mesh)
        {
            PropRow(c, AnimProp.DistCX, body);
            PropRow(c, AnimProp.DistCY, body);
        }
        if (Distorts.Shader(d.Kind) == null)
            body.Children.Add(new TextBlock { Text = "This PC can't distort in the preview with a shader (" + (Distorts.CompileError ?? "no shader compiler") + "): the preview's decoder does it (slower).", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim });
    }

    static void ClearLensKeys(Clip x)
    {
        if (x.Anim == null) return;
        foreach (var p in Lens.Props) x.Anim.Remove(p);
        Anim.Prune(x);
    }

    static void ClearPinKeys(Clip x)
    {
        if (x.Anim == null) return;
        foreach (var p in Pin.Props) x.Anim.Remove(p);
        Anim.Prune(x);
    }

    /// <summary>Crop (keyframable sides, soft edge) and horizontal / vertical flip.</summary>
    void BuildCropInspector(Clip c)
    {
        if (c.Kind is not (ClipKind.Video or ClipKind.Image)) return;
        var body = Section("Crop and flip", open: Crop.Applied(c) || c.FlipH || c.FlipV);
        foreach (var p in Anim.Crop) PropRow(c, p, body);
        Slider("Edge feather", 0, 100, 1, c.CropFeather, v => { foreach (var x in GradeTargets(c)) x.CropFeather = v; }, body);
        var flips = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        CheckBox Flip(string text, bool on, Action<Clip, bool> set)
        {
            var cbx = new CheckBox { Content = text, IsChecked = on, Margin = new Thickness(0, 0, 14, 0) };
            cbx.Click += (_, _) => ed.Edit(() => { foreach (var x in GradeTargets(c)) set(x, cbx.IsChecked == true); });
            flips.Children.Add(cbx); return cbx;
        }
        Flip("Flip horizontal", c.FlipH, (x, v) => x.FlipH = v);
        Flip("Flip vertical", c.FlipV, (x, v) => x.FlipV = v);
        body.Children.Add(flips);
    }

    /// <summary>Ultra key: green / blue screen keying with the colour, tolerance, softness, spill, choke and soften.</summary>
    void BuildKeyInspector(Clip c)
    {
        if (c.Kind is not (ClipKind.Video or ClipKind.Image)) return;
        var body = Section("Ultra key — green / blue screen", open: Keyer.Active(c.Key));
        var targets = GradeTargets(c);
        var mode = new ComboBox { ItemsSource = new[] { "None", "Green screen", "Blue screen", "Black background (luma)", "White background (luma)" },
                                  SelectedIndex = c.Key == null ? 0 : c.Key.Luma ? (c.Key.Level < 50 ? 3 : 4) : Keyer.BlueScreen(c.Key) ? 2 : 1 };
        mode.SelectionChanged += (_, _) =>
        {
            int i = mode.SelectedIndex;
            ed.Edit(() => { foreach (var x in targets) x.Key = i <= 0 ? null : i >= 3 ? new ChromaKey { Luma = true, Level = i == 3 ? 0 : 100, Tolerance = 25, Softness = 20, Spill = 0 }
                                                                          : new ChromaKey { Color = i == 2 ? "#0047BB" : "#00B140" }; });
            BuildInspector();
            if (i is 1 or 2) _ = AutoKeyAsync(c, quiet: true);            // the actual screen colour of this footage
        };
        Row(body, "Key", mode);
        if (c.Key is not { } k) return;
        void Later(Action<ChromaKey> f)
        {
            // the decoder restarts when the key changes: apply slider changes once the slider rests
            toonPending = () => { foreach (var x in targets) if (x.Key != null) f(x.Key); ed.Touch(); };
            toonDebounce.Stop(); toonDebounce.Start();
        }
        toonDebounce.Tick -= ToonTick; toonDebounce.Tick += ToonTick;
        if (k.Luma)
        {
            Slider("Remove level", 0, 100, 1, k.Level, v => Later(x => x.Level = v), body);   // (0 = black, 100 = white)
            Slider("Tolerance", 0, 100, 1, k.Tolerance, v => Later(x => x.Tolerance = v), body);
            Slider("Softness", 0, 100, 1, k.Softness, v => Later(x => x.Softness = v), body);
            Slider("Choke", 0, 10, 0.5, k.Choke, v => Later(x => x.Choke = v), body);
            Slider("Soften", 0, 10, 0.5, k.Soften, v => Later(x => x.Soften = v), body);
            body.Children.Add(new TextBlock { Text = "Removes one brightness (0 = black, 100 = white) — for graphics, light leaks or text filmed on a plain background.",
                                              TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim, Margin = new Thickness(0, 4, 0, 0) });
            return;
        }
        Row(body, "Key color", new ColorField(k.Color, hex => { foreach (var x in targets) if (x.Key != null) x.Key.Color = hex; ed.Touch(); }));
        var bar = new WrapPanel { Margin = new Thickness(86, 0, 0, 4) };
        var pick = new Button { Content = "Pick from monitor", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4), ToolTip = "Click the screen colour in the program monitor" };
        pick.Click += (_, _) =>
        {
            ed.Seek(Math.Clamp(ed.Time, c.Start, c.End - 1 / TimeUtil.Fps));
            pick.Content = "Click the screen in the monitor…";
            Mo.Cursor = Cursors.Cross;
            Mo.PickColor = (tc, nx, ny) => { if (tc.Id == c.Id) _ = SampleKeyAsync(tc, tc.FlipH ? 1 - nx : nx, tc.FlipV ? 1 - ny : ny); else BuildInspector(); };
        };
        var auto = new Button { Content = "Auto", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4), ToolTip = "Find the screen colour in the frame at the playhead" };
        auto.Click += (_, _) => _ = AutoKeyAsync(c, quiet: false);
        bar.Children.Add(pick); bar.Children.Add(auto);
        body.Children.Add(bar);
        Slider("Tolerance", 0, 100, 1, k.Tolerance, v => Later(x => x.Tolerance = v), body);
        Slider("Softness", 0, 100, 1, k.Softness, v => Later(x => x.Softness = v), body);
        Slider("Spill", 0, 100, 1, k.Spill, v => Later(x => x.Spill = v), body);
        Slider("Choke", 0, 10, 0.5, k.Choke, v => Later(x => x.Choke = v), body);
        Slider("Soften", 0, 10, 0.5, k.Soften, v => Later(x => x.Soften = v), body);
        body.Children.Add(new TextBlock { Text = "Keyed by ffmpeg in the preview's decoder and in the export, so both cut out the same pixels. Put the clip on V2 or V3 with the background below it.",
                                          TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim, Margin = new Thickness(0, 4, 0, 0) });
    }

    /// <summary>Look effects: sharpen, glow, film grain (run in the decoder like the drawn looks: applied once a slider rests).</summary>
    void BuildLooksInspector(Clip c)
    {
        if (c.Kind is not (ClipKind.Video or ClipKind.Image)) return;
        var body = Section("Look effects — sharpen, glow, grain", open: Looks.Active(c.Looks));
        var targets = GradeTargets(c);
        var l = c.Looks ?? new LookFx();
        void Later(Action<LookFx> f)
        {
            toonPending = () => { foreach (var x in targets) { x.Looks ??= new LookFx(); f(x.Looks); if (!Looks.Active(x.Looks)) x.Looks = null; } ed.Touch(); };
            toonDebounce.Stop(); toonDebounce.Start();
        }
        toonDebounce.Tick -= ToonTick; toonDebounce.Tick += ToonTick;
        Slider("Sharpen", 0, 100, 1, l.Sharpen, v => Later(x => x.Sharpen = v), body);
        Slider("Glow", 0, 100, 1, l.Glow, v => Later(x => x.Glow = v), body);
        Slider("Glow size", 2, 60, 1, l.GlowRadius, v => Later(x => x.GlowRadius = v), body);
        Slider("Glow from %", 0, 95, 1, l.GlowThreshold, v => Later(x => x.GlowThreshold = v), body);
        Slider("Film grain", 0, 100, 1, l.Grain, v => Later(x => x.Grain = v), body);
        Slider("VHS", 0, 100, 1, l.Vhs, v => Later(x => x.Vhs = v), body);
        Slider("Glitch", 0, 100, 1, l.Glitch, v => Later(x => x.Glitch = v), body);
    }

    /// <summary>Drop shadow under the clip's picture (its outline: crop, key and masks included).</summary>
    void BuildShadowInspector(Clip c)
    {
        if (c.Kind is not (ClipKind.Video or ClipKind.Image)) return;
        var body = Section("Drop shadow", open: c.Shadow != null);
        var targets = GradeTargets(c);
        var on = new CheckBox { Content = "Shadow under the picture", IsChecked = c.Shadow != null, Margin = new Thickness(0, 2, 0, 4) };
        on.Click += (_, _) => { ed.Edit(() => { foreach (var x in targets) x.Shadow = on.IsChecked == true ? (c.Shadow?.Clone() ?? new DropShadow()) : null; }); BuildInspector(); };
        body.Children.Add(on);
        if (c.Shadow is not { } s) return;
        void Set(Action<DropShadow> f) { foreach (var x in targets) if (x.Shadow != null) f(x.Shadow); }
        Slider("Opacity %", 0, 100, 1, s.Opacity, v => Set(x => x.Opacity = v), body);
        Slider("Direction °", 0, 360, 1, s.Angle, v => Set(x => x.Angle = v), body);
        Slider("Distance", 0, 100, 0.5, s.Distance, v => Set(x => x.Distance = v), body);
        Slider("Softness", 0, 100, 0.5, s.Softness, v => Set(x => x.Softness = v), body);
        Row(body, "Color", new ColorField(s.Color, hex => { Set(x => x.Color = hex); ed.Touch(); }));
    }

    /// <summary>The clip's own frame at the playhead (no key, look or colour), BGRA.</summary>
    (byte[] px, int w, int h)? KeySourceFrame(Clip c, int maxW)
    {
        if (ed.MediaOf(c) is not { Offline: false } m || Exporter.FindFfmpeg() is not { } ff) return null;
        double src = m.Type == ClipKind.Image ? 0 : Math.Clamp(TimeMap.Src(c, Math.Clamp(ed.Time, c.Start, c.End - 1e-3)), 0, Math.Max(0, m.Duration - 0.05));
        int w = Math.Min(maxW, m.Width > 0 ? m.Width : maxW); w -= w % 2;
        int h = m.Width > 0 ? (int)Math.Round(w * m.Height / (double)m.Width / 2) * 2 : w * 9 / 16 / 2 * 2;
        if (Nest.SourceOf(m, src) is not var (path, fsrc, _)) return null;                // (a nested sequence: its render)
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string> { "-v", "error", "-nostdin" };
        if (m.Type != ClipKind.Image) args.AddRange(new[] { "-ss", fsrc.ToString("0.###", CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-i", path, "-frames:v", "1", "-vf", $"scale={w}:{h}{(path == m.Path ? Hdr.Post(m) : "")},format=bgra", "-f", "rawvideo", "pipe:1" });
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
            var buf = new byte[w * h * 4]; int got = 0; var s = p.StandardOutput.BaseStream;
            while (got < buf.Length) { int n = s.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
            p.WaitForExit();
            return got == buf.Length ? (buf, w, h) : null;
        }
        catch { return null; }
    }

    async Task SampleKeyAsync(Clip c, float nx, float ny)
    {
        var f = await Task.Run(() => KeySourceFrame(c, 960));
        if (f is { } fr)
        {
            // a 7×7 average around the click (grain)
            int cx = Math.Clamp((int)(nx * fr.w), 3, fr.w - 4), cy = Math.Clamp((int)(ny * fr.h), 3, fr.h - 4);
            long r = 0, g = 0, b = 0; int n = 0;
            for (int y = cy - 3; y <= cy + 3; y++) for (int x = cx - 3; x <= cx + 3; x++) { int o = (y * fr.w + x) * 4; b += fr.px[o]; g += fr.px[o + 1]; r += fr.px[o + 2]; n++; }
            string hex = $"#{r / n:X2}{g / n:X2}{b / n:X2}";
            ed.Edit(() => { foreach (var x in GradeTargets(c)) if (x.Key != null) x.Key.Color = hex; });
        }
        BuildInspector();
    }

    async Task AutoKeyAsync(Clip c, bool quiet)
    {
        var f = await Task.Run(() => KeySourceFrame(c, 640));
        string? hex = f is { } fr ? Keyer.AutoColor(fr.px, fr.w, fr.h) : null;
        if (hex == null) { if (!quiet) Alert.Show(this, "No green or blue screen colour stands out in the frame at the playhead. Use Pick from monitor instead.", "Ultra key"); return; }
        ed.Edit(() => { foreach (var x in GradeTargets(c)) if (x.Key != null) x.Key.Color = hex; });
        BuildInspector();
    }
}
