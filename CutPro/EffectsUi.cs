using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>Transitions UI (partial MainWindow): the Effects side view (drag a transition onto a cut, double-click to apply at
/// the cut nearest the playhead, right-click to make it the default), Ctrl+D / Ctrl+Shift+D / Shift+D, and the Effect Controls
/// of a selected transition (type, duration, alignment, what it needs from the media).</summary>
public partial class MainWindow
{
    void InitEffects() { FillLibraryCats(); BuildEffectsList(); }

    /// <summary>An adjustment layer at the playhead (V3, else V2), selected so its Lumetri / blur sections show.</summary>
    void AddAdjustment() => AddAdjustmentClip();
    Clip? AddAdjustmentClip()
    {
        var c = ed.AddAdjustment();
        if (c == null) Alert.Show(this, "V3 and V2 both have clips at the playhead. Move the playhead, or free one of those tracks there.", "Adjustment layer");
        else ShowDock("Inspector");
        return c;
    }

    // BuildEffectsList lives in LibraryUi.cs (the Library replaces the old flat Effects list).

    /// <summary>One draggable transition row: icon, name, "default" badge.</summary>
    FrameworkElement EffectItem(Transitions.Def d, bool isDefault)
    {
        var row = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, ToolTip = d.Help,
                               Background = Brushes.Transparent };
        var dp = new DockPanel();
        var icon = new Image { Source = TransitionIcon(d), Width = 40, Height = 24, Margin = new Thickness(0, 0, 8, 0) };
        dp.Children.Add(icon);
        if (isDefault)
        {
            var badge = new TextBlock { Text = "default", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            badge.SetResourceReference(TextBlock.ForegroundProperty, "B.Accent2");
            DockPanel.SetDock(badge, Dock.Right); dp.Children.Insert(0, badge);
        }
        dp.Children.Add(new TextBlock { Text = d.Name, VerticalAlignment = VerticalAlignment.Center });
        row.Child = dp;
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "B.Hover");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        Point? down = null;
        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { ApplyTransitions(d.Fx != Transitions.Fx.Audio, d.Fx == Transitions.Fx.Audio, d.Name); e.Handled = true; return; }
            down = e.GetPosition(row);
        };
        row.PreviewMouseMove += (_, e) =>
        {
            if (down is not { } p0 || e.LeftButton != MouseButtonState.Pressed) { down = null; return; }
            if ((e.GetPosition(row) - p0).Length < 4) return;
            down = null;
            DragDrop.DoDragDrop(row, new DataObject("CutPro.Transition", d.Name), DragDropEffects.Copy);
        };
        var menu = new ContextMenu();
        var setDef = new MenuItem { Header = d.Fx == Transitions.Fx.Audio ? "Set as Default Audio Transition" : "Set as Default Video Transition" };
        setDef.Click += (_, _) =>
        {
            if (d.Fx == Transitions.Fx.Audio) S.DefaultAudioTransition = d.Name; else S.DefaultVideoTransition = d.Name;
            AppSettings.Save(); BuildEffectsList();
        };
        var apply = new MenuItem { Header = "Apply at the Cut Nearest the Playhead" };
        apply.Click += (_, _) => ApplyTransitions(d.Fx != Transitions.Fx.Audio, d.Fx == Transitions.Fx.Audio, d.Name);
        menu.Items.Add(apply); menu.Items.Add(setDef);
        row.ContextMenu = menu;
        return row;
    }

    /// <summary>A small picture of the transition halfway through: shot A (blue) becoming shot B (amber).</summary>
    static DrawingImage TransitionIcon(Transitions.Def d)
    {
        var g = new DrawingGroup();
        var A = new SolidColorBrush(Color.FromRgb(70, 130, 220)); var Bb = new SolidColorBrush(Color.FromRgb(240, 170, 60));
        var frame = new Rect(0, 0, 40, 24);
        using (var dc = g.Open())
        {
            dc.PushClip(new RectangleGeometry(frame, 3, 3));
            switch (d.Fx)
            {
                case Transitions.Fx.Dissolve:
                    dc.DrawRectangle(new LinearGradientBrush(A.Color, Bb.Color, 0), null, frame); break;
                case Transitions.Fx.Dip:
                    var col = (Color)ColorConverter.ConvertFromString(d.Color ?? "#000000");
                    var lg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                    lg.GradientStops.Add(new GradientStop(A.Color, 0)); lg.GradientStops.Add(new GradientStop(col, 0.5)); lg.GradientStops.Add(new GradientStop(Bb.Color, 1));
                    dc.DrawRectangle(lg, null, frame); break;
                case Transitions.Fx.Wipe:
                    dc.DrawRectangle(A, null, frame);
                    Rect half = d.Dx < 0 ? new Rect(20, 0, 20, 24) : d.Dx > 0 ? new Rect(0, 0, 20, 24) : d.Dy < 0 ? new Rect(0, 12, 40, 12) : new Rect(0, 0, 40, 12);
                    dc.DrawRectangle(Bb, null, half);
                    dc.DrawRectangle(Brushes.White, null, d.Dx != 0 ? new Rect(19.5, 0, 1, 24) : new Rect(0, 11.5, 40, 1));
                    break;
                case Transitions.Fx.Push:
                    double ox = d.Dx * 20, oy = d.Dy * 12;
                    dc.DrawRectangle(A, null, new Rect(ox, oy, 40, 24));
                    dc.DrawRectangle(Bb, null, new Rect(ox - d.Dx * 40, oy - d.Dy * 24, 40, 24));
                    var arrow = new StreamGeometry();
                    using (var s = arrow.Open())
                    {
                        Point c = new(20, 12); Vector dir = new(d.Dx, d.Dy), nrm = new(-d.Dy, d.Dx);
                        s.BeginFigure(c + dir * 7, true, true); s.LineTo(c - dir * 3 + nrm * 6, true, false); s.LineTo(c - dir * 3 - nrm * 6, true, false);
                    }
                    dc.DrawGeometry(Brushes.White, null, arrow);
                    break;
                case Transitions.Fx.Iris:
                    dc.DrawRectangle(A, null, frame);
                    var rg = new RadialGradientBrush(Bb.Color, Color.FromArgb(0, Bb.Color.R, Bb.Color.G, Bb.Color.B)) { RadiusX = 0.5, RadiusY = 0.85 };
                    dc.DrawEllipse(rg, null, new Point(20, 12), 18, 18);
                    break;
                case Transitions.Fx.Mask:
                    if (d.Name == "Film Dissolve")
                    {
                        dc.DrawRectangle(A, null, frame);
                        var rnd = new Random(4);
                        for (int i = 0; i < 70; i++)
                        {
                            double x = rnd.NextDouble() * 40, y = rnd.NextDouble() * 24;
                            dc.DrawRectangle(x > 20 ? Bb : A, null, new Rect(x, y, 2.2, 2.2));
                        }
                    }
                    else
                    {
                        dc.DrawRectangle(A, null, frame);
                        if (d.Name == "Bible Page Turn")
                        {
                            var page = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(frame), Transitions.Shape(d, 0.5, 40, 24));
                            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(246, 228, 193)), null, page);
                            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(184, 134, 11)), null, new CombinedGeometry(GeometryCombineMode.Union, new RectangleGeometry(new Rect(18, 6, 4, 8), 0.5, 0.5), new RectangleGeometry(new Rect(14, 8, 12, 3), 0.5, 0.5)));
                        }
                        dc.PushClip(Transitions.Shape(d, 0.5, 40, 24));
                        dc.DrawRectangle(Bb, null, frame);
                        dc.Pop();
                    }
                    break;
                case Transitions.Fx.Move:
                {
                    // both shots halfway through, the one on top drawn last
                    var win = new Transitions.Win(new Clip(), new Clip(), new Transition(), d, 0, 0, 1, 0.5, new Clip(), true);
                    var shots = new[] { (b: false, x: Transitions.XformOf(win, false, 0.5)), (b: true, x: Transitions.XformOf(win, true, 0.5)) }.OrderBy(s => s.x.Top);
                    foreach (var (isB, x) in shots)
                    {
                        dc.PushTransform(new TranslateTransform(x.Dx * 40 / Editor.W, x.Dy * 24 / Editor.H));
                        dc.PushTransform(new RotateTransform(x.Rot, 20, 12));
                        dc.PushTransform(new ScaleTransform(Math.Max(0.05, x.SX), Math.Max(0.05, x.SY), 20, 12));
                        dc.PushOpacity(x.Op);
                        dc.DrawRectangle(isB ? Bb : A, new Pen(Brushes.White, 0.6), frame);
                        dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
                    }
                    if (d.Name.StartsWith("Slide"))
                    {   // the direction the incoming shot moves (like the push icons)
                        var arr = new StreamGeometry();
                        using (var s = arr.Open())
                        {
                            Point c = new(20, 12); Vector dir = new(d.Dx, d.Dy), nrm = new(-d.Dy, d.Dx);
                            s.BeginFigure(c + dir * 7, true, true); s.LineTo(c - dir * 3 + nrm * 6, true, false); s.LineTo(c - dir * 3 - nrm * 6, true, false);
                        }
                        dc.DrawGeometry(Brushes.White, null, arr);
                    }
                    break;
                }
                case Transitions.Fx.Audio:
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 44, 52)), null, frame);
                    var pa = new Pen(A, 2); var pb = new Pen(Bb, 2);
                    var fo = new StreamGeometry(); var fi = new StreamGeometry();
                    using (var s = fo.Open()) { s.BeginFigure(new Point(2, 4), false, false); for (int i = 1; i <= 12; i++) { double x = i / 12.0; s.LineTo(new Point(2 + 36 * x, 20 - 16 * (d.Power ? Math.Cos(x * Math.PI / 2) : 1 - x)), true, true); } }
                    using (var s = fi.Open()) { s.BeginFigure(new Point(2, 20), false, false); for (int i = 1; i <= 12; i++) { double x = i / 12.0; s.LineTo(new Point(2 + 36 * x, 20 - 16 * (d.Power ? Math.Sin(x * Math.PI / 2) : x)), true, true); } }
                    dc.DrawGeometry(null, pa, fo); dc.DrawGeometry(null, pb, fi);
                    break;
            }
            dc.Pop();
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1), frame, 3, 3);
        }
        g.Freeze();
        var img = new DrawingImage(g); img.Freeze();
        return img;
    }

    /// <summary>Applies a transition (or the defaults) at the selected clips' edit points / the cut nearest the playhead.</summary>
    void ApplyTransitions(bool video, bool audio, string? kind = null)
    {
        int n = kind == null ? ed.ApplyDefaultTransitions(video, audio) : ed.ApplyTransition(kind, null, null, video, audio);
        SetTask(n > 0 ? $"Added {n} transition{(n == 1 ? "" : "s")} — drag its edges on the timeline to lengthen it; Ctrl+Z undoes"
                      : "No cut near the playhead: park it within 2 s of a cut, select clips, or drag a transition onto a cut", 5000);
    }

    /// <summary>Effect Controls for the selected transition.</summary>
    bool BuildTransitionInspector()
    {
        if (ed.SelTrans is not { } st || ed.Clips.FirstOrDefault(c => c.Id == st.ClipId) is not { } owner || ed.SelectedTransition is not { } tr) return false;
        var plan = Transitions.Compute(ed);
        var w = plan.Wins.FirstOrDefault(x => x.Owner.Id == owner.Id && x.Head == st.Head);
        var def = Transitions.Get(tr.Kind);
        Inspector.Children.Add(new TextBlock { Text = tr.Kind, FontWeight = FontWeights.SemiBold });
        string between = w == null ? "" : w.A != null && w.B != null ? $"{ClipName(w.A)}  →  {ClipName(w.B)}" : w.A == null ? $"into {ClipName(w.B!)} (from the tracks below)" : $"out of {ClipName(w.A)} (to the tracks below)";
        Inspector.Children.Add(new TextBlock { Text = between, Foreground = Dim, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        void Change(Action<Transition> f) { ed.Edit(() => f(tr)); BuildInspector(); }
        var body = Section("Transition", open: true);
        var kinds = (def.Fx == Transitions.Fx.Audio ? Transitions.AudioDefs : Transitions.VideoDefs).Select(d => d.Name).ToList();
        var kindBox = new ComboBox { ItemsSource = kinds, SelectedItem = tr.Kind, ToolTip = def.Help };
        kindBox.SelectionChanged += (_, _) => { if (kindBox.SelectedItem is string k && k != tr.Kind) Change(x => x.Kind = k); };
        Row(body, "Type", kindBox);
        int frames = (int)Math.Round(tr.Dur * TimeUtil.Fps);
        var durBox = new TextBox { Text = tr.Dur.ToString("0.###"), Width = 70, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Seconds (Enter to apply)" };
        void ApplyDur() { if (double.TryParse(durBox.Text, out var s) && s > 0 && Math.Abs(s - tr.Dur) > 1e-6) Change(x => x.Dur = Math.Round(s * TimeUtil.Fps) / TimeUtil.Fps); }
        durBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyDur(); };
        durBox.LostKeyboardFocus += (_, _) => ApplyDur();
        var durRow = new StackPanel { Orientation = Orientation.Horizontal };
        durRow.Children.Add(durBox); durRow.Children.Add(new TextBlock { Text = $"  s  ({frames} frames)", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center });
        Row(body, "Duration", durRow);
        if (w is { A: not null, B: not null })
        {
            var al = new ComboBox { ItemsSource = new[] { "Center at Cut", "Start at Cut", "End at Cut" }, SelectedIndex = (int)tr.Align };
            al.SelectionChanged += (_, _) => { if (al.SelectedIndex >= 0 && al.SelectedIndex != (int)tr.Align) Change(x => x.Align = (TransAlign)al.SelectedIndex); };
            Row(body, "Alignment", al);
        }
        if (w != null)
        {
            double actual = w.Dur;
            var notes = new List<string>();
            if (Math.Abs(actual - tr.Dur) > 0.5 / TimeUtil.Fps) notes.Add($"Shortened to {actual:0.##} s to fit the clips.");
            if (w.A != null && w.A.Kind == ClipKind.Video && !TimeMap.IsRemapped(w.A) && ed.MediaOf(w.A) is { } ma && w.A.In + (w.AEnd - w.A.Start) > ma.Duration + 1e-3)
                notes.Add($"{ClipName(w.A)} has no more media after its out point — its last frame repeats for {w.A.In + (w.AEnd - w.A.Start) - ma.Duration:0.##} s.");
            if (w.B != null && w.B.Kind == ClipKind.Video && !TimeMap.IsRemapped(w.B) && w.B.In - (w.B.Start - w.BStart) < -1e-3)
                notes.Add($"{ClipName(w.B)} has no media before its in point — its first frame repeats for {-(w.B.In - (w.B.Start - w.BStart)):0.##} s.");
            if (def.Fx == Transitions.Fx.Audio) notes.Add("Crossfades use media beyond the clips' edges (handles); without them the fade gets shorter.");
            notes.Add(def.Help + ".");
            foreach (var n in notes) body.Children.Add(new TextBlock { Text = n, Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        var save = new Button { Content = "Save as Transition Preset…", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 3, 10, 3) };
        save.Click += (_, _) => SaveTransPreset(tr);
        body.Children.Add(save);
        var del = new Button { Content = "Delete transition", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 3, 10, 3) };
        del.Click += (_, _) => ed.DeleteSelection(false);
        body.Children.Add(del);
        return true;
    }

    void SaveTransPreset(Transition tr)
    {
        var name = new TextBox { Text = tr.Kind, Margin = new Thickness(0, 10, 0, 0) };
        name.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        if (Alert.Show(this, "Name this transition (type, duration and alignment). It shows in the Effects panel.", "Save as Transition Preset",
                       AlertKind.Question, new[] { "Save", "Cancel" }, name) != 0) return;
        if (name.Text.Trim().Length == 0) return;
        TransPresets.Add(new TransPreset { Name = name.Text.Trim(), Kind = tr.Kind, Dur = tr.Dur, Align = tr.Align });
        BuildEffectsList();
        SetTask($"Transition preset “{name.Text.Trim()}” saved", 3000);
    }

    void BuildTransPresetList()
    {
        Header("Transition presets");
        var note = new TextBlock { Text = "Double-click to apply at the cut nearest the playhead. Effect Controls ▸ Save as Transition Preset… adds your own.", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 4) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        EffectsList.Children.Add(note);
        foreach (var p in TransPresets.All)
        {
            var row = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3, 6, 3), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent,
                                   ToolTip = $"{p.Kind}, {p.Dur:0.###} s" };
            var dp = new DockPanel();
            dp.Children.Add(new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center });
            if (!p.BuiltIn)
            {
                var del = new Button { Content = "", ToolTip = "Delete this preset", FontSize = 9, Width = 20, Height = 18 };
                del.SetResourceReference(StyleProperty, "Icon");
                del.Click += (_, e) => { TransPresets.Remove(p); BuildEffectsList(); e.Handled = true; };
                DockPanel.SetDock(del, Dock.Right); dp.Children.Insert(0, del);
            }
            row.Child = dp;
            row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "B.Hover");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    bool audio = Transitions.Get(p.Kind).Fx == Transitions.Fx.Audio;
                    int n = ed.ApplyTransition(p.Kind, p.Dur, p.Align, !audio, audio);
                    SetTask(n > 0 ? $"“{p.Name}” on {n} cut{(n == 1 ? "" : "s")}" : "No cut near the playhead", 4000);
                }
            };
            EffectsList.Children.Add(row);
        }
        void Header(string text) => EffectsList.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 10, 0, 4) });
    }

    string ClipName(Clip c) => c.Kind == ClipKind.Text ? $"\"{c.Text.Split('\n')[0]}\"" : ed.MediaOf(c)?.Name ?? "clip";
}
