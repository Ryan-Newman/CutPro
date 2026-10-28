using System.Windows;
using System.Windows.Controls;

namespace CutPro;

/// <summary>Effect Controls → Stabilize (partial MainWindow): on / off, smoothness, method, edges, shakiness; the render's state. Edits apply to
/// every selected video clip; each change re-renders in the background (the preview shows the unstabilized clip meanwhile).</summary>
public partial class MainWindow
{
    void InitStab()
    {
        Stab.StatusChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (Stab.Status is { } s) SetTask(s, 0); else SetTask("Stabilized", 1500);
            if (stabState != null && ed.Selected is { } c) UpdateStabState(c);
        });
    }

    TextBlock? stabState;
    void UpdateStabState(Clip c)
    {
        if (stabState == null || ed.MediaOf(c) is not { } m) return;
        var (s, e) = Transitions.Compute(ed).Range(c);
        stabState.Text = Stab.StateOf(c, m, s, e) switch
        {
            null => "",
            { ready: true } => "✓ Rendered — preview and export play the processed frames.",
            { failed: true, error: var err } => "Could not render: " + (err ?? "ffmpeg failed"),
            _ => (Stab.Status ?? "Waiting to render…") + " The preview shows the clip as shot until it is done.",
        };
    }

    void BuildStabInspector(Clip c)
    {
        stabState = null;
        if (c.Kind != ClipKind.Video || ed.SeqOf(c) != null) return;
        var body = Section("Stabilize", open: c.Stab != null);
        var targets = ed.Clips.Where(x => ed.IsSel(x) && x.Kind == ClipKind.Video && ed.SeqOf(x) == null).Append(c).Distinct().ToList();
        var on = new CheckBox { Content = "Stabilize (rendered in the background)", IsChecked = c.Stab != null, Margin = new Thickness(0, 2, 0, 4) };
        on.Click += (_, _) => { ed.Edit(() => { foreach (var x in targets) x.Stab = on.IsChecked == true ? x.Stab ?? new Stabilize() : null; }); BuildInspector(); };
        body.Children.Add(on);
        if (c.Stab is not { } st) return;
        if (TimeMap.IsRemapped(c))
            body.Children.Add(new TextBlock { Text = "With its speed change: the media's frames are stabilized first, then slowed / sped / reversed as before.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim });
        void Set(Action<Stabilize> f) { ed.Edit(() => { foreach (var x in targets) if (x.Stab != null) f(x.Stab); }); UpdateStabState(c); }
        ComboBox Combo(string label, string[] items, int sel, Action<int> pick, string tip)
        {
            var cb = new ComboBox { ItemsSource = items, SelectedIndex = sel, ToolTip = tip };
            cb.SelectionChanged += (_, _) => pick(cb.SelectedIndex);
            Row(body, label, cb);
            return cb;
        }
        Combo("Result", new[] { "Smooth motion", "No motion (tripod)" }, st.Tripod ? 1 : 0, i => Set(x => x.Tripod = i == 1),
              "Smooth motion keeps the camera's intended movement; No motion locks the frame as if the camera were on a tripod");
        Combo("Edges", new[] { "Zoom in (whole clip)", "Zoom as needed", "Show edges" }, st.Edges, i => Set(x => x.Edges = i),
              "Stabilizing moves the picture: zoom in once just enough for the whole clip, zoom moment by moment, or leave black edges");
        var smooth = new Slider { Minimum = 5, Maximum = 100, Value = st.Smooth, SmallChange = 1, LargeChange = 10, IsEnabled = !st.Tripod,
                                  ToolTip = "How smooth the camera path becomes (50 % ≈ one second averaged around each frame)" };
        smooth.PreviewMouseLeftButtonUp += (_, _) => Set(x => x.Smooth = Math.Round(smooth.Value));
        smooth.KeyUp += (_, _) => Set(x => x.Smooth = Math.Round(smooth.Value));
        Row(body, "Smoothness %", smooth);
        var shake = new Slider { Minimum = 1, Maximum = 10, Value = st.Shake, SmallChange = 1, LargeChange = 1, IsSnapToTickEnabled = true, TickFrequency = 1,
                                 ToolTip = "How shaky the footage is: higher finds bigger, faster motion (slower analysis)" };
        shake.PreviewMouseLeftButtonUp += (_, _) => Set(x => x.Shake = (int)Math.Round(shake.Value));
        shake.KeyUp += (_, _) => Set(x => x.Shake = (int)Math.Round(shake.Value));
        Row(body, "Shakiness", shake);
        stabState = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim, Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(stabState);
        UpdateStabState(c);
    }

    /// <summary>Effect Controls → Noise reduction: method and strength (rendered in the background with the stabilizer's machinery).</summary>
    void BuildDenoiseInspector(Clip c)
    {
        if (c.Kind != ClipKind.Video || ed.SeqOf(c) != null) return;
        var targets = GradeTargets(c).Where(x => x.Kind == ClipKind.Video).ToList();
        var body = Section("Noise reduction", open: Stab.Denoises(c));
        var on = new CheckBox { Content = "Reduce noise (rendered in the background)", IsChecked = c.Denoise != null, Margin = new Thickness(0, 2, 0, 4) };
        on.Click += (_, _) => { ed.Edit(() => { foreach (var x in targets) x.Denoise = on.IsChecked == true ? x.Denoise ?? new VideoDenoise() : null; }, "Noise Reduction"); BuildInspector(); };
        body.Children.Add(on);
        if (c.Denoise is not { } dn) return;
        void Set(Action<VideoDenoise> f) { ed.Edit(() => { foreach (var x in targets) if (x.Denoise != null) f(x.Denoise); }, "Noise Reduction"); UpdateStabState(c); Tl.InvalidateVisual(); }
        var method = new ComboBox { ItemsSource = new[] { "Fast (spatial + temporal)", "High quality (slow)", "Temporal (static shots)" }, SelectedIndex = dn.Method,
                                    ToolTip = "Fast: hqdn3d. High quality: non-local means, much slower to render. Temporal: averages over 9 frames — best for steady shots" };
        method.SelectionChanged += (_, _) => Set(x => x.Method = method.SelectedIndex);
        Row(body, "Method", method);
        var str = new Slider { Minimum = 1, Maximum = 100, Value = dn.Strength, SmallChange = 1, LargeChange = 10, ToolTip = "How much (50 = the filter's usual strength)" };
        str.PreviewMouseLeftButtonUp += (_, _) => Set(x => x.Strength = Math.Round(str.Value));
        str.KeyUp += (_, _) => Set(x => x.Strength = Math.Round(str.Value));
        Row(body, "Strength", str);
        if (c.Stab == null)
        {
            stabState = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim, Margin = new Thickness(0, 4, 0, 0) };
            body.Children.Add(stabState);
            UpdateStabState(c);
        }
    }
}
