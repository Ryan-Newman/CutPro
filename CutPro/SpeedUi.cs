using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CutPro;

/// <summary>Clip Speed/Duration dialog (Ctrl+R), the Time section of Effect Controls, and the render status (partial MainWindow).</summary>
public partial class MainWindow
{
    /// <summary>Premiere's Clip Speed / Duration: speed and duration are linked (the source range stays), reverse, maintain pitch, ripple.</summary>
    void SpeedDialog()
    {
        var targets = ed.Clips.Where(ed.IsSel).ToList();
        if (targets.Count == 0) { Alert.Show(this, "Select one or more clips first.", "Speed/Duration"); return; }
        if (targets.Any(x => ed.SeqOf(x) != null))
        {
            Alert.Show(this, "Nested sequences play at 100 %. To change their speed, open the nested sequence (double-click it) and change the speed of the clips inside.", "Speed/Duration");
            return;
        }
        var c = ed.Selected ?? targets[0];
        bool timed = c.Kind is ClipKind.Video or ClipKind.Audio && !c.Hold, ramped = TimeMap.Ramped(c);

        var form = new StackPanel { Width = 380, Margin = new Thickness(0, 8, 0, 0) };
        Grid Row(string label, UIElement e)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; l.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
            Grid.SetColumn(e, 1); g.Children.Add(l); g.Children.Add(e); form.Children.Add(g); return g;
        }
        var speed = new TextBox { Text = c.Speed.ToString("0.##", CultureInfo.InvariantCulture), Width = 80, IsEnabled = timed && !ramped };
        var speedRow = new StackPanel { Orientation = Orientation.Horizontal }; speedRow.Children.Add(speed); speedRow.Children.Add(new TextBlock { Text = "  %", VerticalAlignment = VerticalAlignment.Center });
        var dur = new TextBox { Text = TimeUtil.Format(c.Dur), Width = 110, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "hh:mm:ss:ff, or seconds (e.g. 2.5)" };
        var reverse = new CheckBox { Content = "Reverse speed", IsChecked = c.Reverse, IsEnabled = timed, Margin = new Thickness(96, 4, 0, 2) };
        var pitch = new CheckBox { Content = "Maintain audio pitch", IsChecked = c.PitchLock, IsEnabled = timed, Margin = new Thickness(96, 2, 0, 2) };
        var ripple = new CheckBox { Content = "Ripple edit, shifting trailing clips", Margin = new Thickness(96, 2, 0, 2),
                                    ToolTip = "Off: a longer clip stops at the next clip on its track (it shows less of its source)" };
        Row("Speed", speedRow); Row("Duration", dur);
        form.Children.Add(reverse); form.Children.Add(pitch); form.Children.Add(ripple);
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 10, 0, 0),
                                   Text = ramped ? "Time remapping keyframes set this clip's speed — the duration, reverse and pitch still apply."
                                        : c.Hold ? "A frame hold shows one frame: only its duration applies."
                                        : targets.Count > 1 ? $"Applies to the {targets.Count} selected clips (and the clips linked to them)."
                                        : c.LinkId != null ? "The linked clip changes with it, so picture and sound stay together." : "" };
        note.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        form.Children.Add(note);
        // speed and duration are linked: editing one shows the other
        bool busy = false, durEdited = false;
        speed.TextChanged += (_, _) =>
        {
            if (busy || !double.TryParse(speed.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) || s <= 0) return;
            busy = true; durEdited = false; dur.Text = TimeUtil.Format(c.Dur * c.Speed / Math.Clamp(s, TimeMap.MinSpeed, TimeMap.MaxSpeed)); busy = false;
        };
        dur.TextChanged += (_, _) =>
        {
            if (busy || ParseTime(dur.Text) is not double d || d <= 0) return;
            busy = true; durEdited = true; if (timed && !ramped) speed.Text = (c.Speed * c.Dur / d).ToString("0.##", CultureInfo.InvariantCulture); busy = false;
        };
        if (Alert.Show(this, "", "Clip Speed / Duration", AlertKind.Info, new[] { "OK", "Cancel" }, form) != 0) return;
        double? sp = !durEdited && double.TryParse(speed.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sv) && sv > 0 ? sv : null;
        double? du = durEdited ? ParseTime(dur.Text) : null;
        ed.SetSpeed(targets, sp, du, reverse.IsChecked == true, pitch.IsChecked == true, ripple.IsChecked == true);
    }

    /// <summary>"hh:mm:ss:ff" (sequence frames), "mm:ss", or plain seconds.</summary>
    static double? ParseTime(string s)
    {
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) return sec;
        var p = s.Split(':');
        if (p.Length < 2 || p.Any(x => !int.TryParse(x, out _))) return null;
        var n = p.Select(int.Parse).ToArray();
        return n.Length switch
        {
            2 => n[0] * 60 + n[1],
            3 => n[0] * 60 + n[1] + n[2] / (double)TimeUtil.Nominal,
            _ => n[^4] * 3600 + n[^3] * 60 + n[^2] + n[^1] / (double)TimeUtil.Nominal,
        };
    }

    /// <summary>Effect Controls → Time: speed (keyframes = time remapping), reverse, freeze frame, pitch, and how the clip is rendered.</summary>
    void BuildTimeInspector(Clip c)
    {
        if (c.Kind is not (ClipKind.Video or ClipKind.Audio) || ed.SeqOf(c) != null) return;   // (nested sequences play at 100 %)
        var m = ed.MediaOf(c);
        Group("Time");
        if (c.Kind == ClipKind.Video && !c.Hold) PropRow(c, AnimProp.Speed);
        else if (!c.Hold) Inspector.Children.Add(new TextBlock { Text = $"Speed {c.Speed:0.##} %{(c.Reverse ? ", reversed" : "")}", Foreground = Dim, Margin = new Thickness(22, 2, 0, 2) });
        var checks = new WrapPanel { Margin = new Thickness(22, 2, 0, 0) };
        CheckBox Check(string text, bool on, string tip, Action<bool> set)
        {
            var cb = new CheckBox { Content = text, IsChecked = on, Margin = new Thickness(0, 2, 14, 2), ToolTip = tip };
            cb.Click += (_, _) => { set(cb.IsChecked == true); BuildInspector(); Tl.InvalidateVisual(); };
            checks.Children.Add(cb); return cb;
        }
        if (!c.Hold) Check("Reverse", c.Reverse, "Play the clip backwards (linked clips too)", on => ed.SetSpeed(new[] { c }, null, null, on, c.PitchLock, false));
        if (c.Kind == ClipKind.Video) Check("Freeze frame", c.Hold, "Hold one frame for the whole clip (the one at the playhead)", on => ed.SetHold(c, on));
        if (!c.Hold && Mixer.HasSound(c) || c.LinkId != null && c.Kind == ClipKind.Video)
            Check("Keep pitch", c.PitchLock, "Faster or slower sound keeps its pitch (off: it rises or falls like a tape)",
                  on => ed.SetSpeed(new[] { c }, null, null, c.Reverse, on, false));
        Inspector.Children.Add(checks);
        if (c.Kind == ClipKind.Video && !c.Hold && ed.SeqOf(c) == null)
        {   // Premiere's Time Interpolation: how slow motion fills the frames between the media's own
            var ti = new ComboBox { ItemsSource = new[] { "Frame sampling", "Frame blending", "Optical flow" }, SelectedIndex = (int)c.Interp, Margin = new Thickness(0, 2, 0, 2),
                                    ToolTip = "Slow motion: repeat frames (sampling), cross-fade neighbours (blending) or make new in-between frames from the motion (optical flow, rendered in the background)" };
            ti.SelectionChanged += (_, _) => { ed.Edit(() => { foreach (var x in ed.WithPartners(new[] { c }).Where(x => x.Kind == ClipKind.Video)) x.Interp = (FrameInterp)ti.SelectedIndex; }, "Time Interpolation"); BuildInspector(); Tl.InvalidateVisual(); };
            Row(Inspector, "Interpolation", ti);
        }
        var btn = new Button { Content = "Speed/Duration…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(22, 4, 0, 2),
                               ToolTip = "Ctrl+R: speed and duration together (the clip keeps its source range), ripple" };
        btn.Click += (_, _) => SpeedDialog();
        Inspector.Children.Add(btn);
        var notes = new List<string>();
        if (c.Kind == ClipKind.Video && !c.Hold) notes.Add(c.LinkId != null ? "Speed here is time remapping: it changes the picture only and the clip keeps its length (add keyframes for ramps). Speed/Duration changes the linked sound too."
                                                                             : "Speed here keeps the clip's length (add keyframes for speed ramps); Speed/Duration keeps its source instead.");
        if (m != null && !c.Hold && TimeMap.IsRemapped(c))
        {
            var (lo, hi) = TimeMap.Span(c);
            if (hi > m.Duration + 1e-3) notes.Add($"It runs {hi - m.Duration:0.##} s past the end of its media there: the last frame holds.");
            if (lo < -1e-3) notes.Add($"It needs {-lo:0.##} s before the start of its media: the first frame holds.");
        }
        if (c.Hold) notes.Add($"Frozen on the frame at {TimeUtil.Format(c.In)} of the media.");
        foreach (var n in notes) Inspector.Children.Add(new TextBlock { Text = n, Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 4, 0, 0) });
    }

    /// <summary>The video clip a frame hold applies to: the selected one under the playhead, else the top video clip there.</summary>
    Clip? HoldTarget() =>
        ed.Clips.Where(c => c.Kind == ClipKind.Video && ed.Time >= c.Start && ed.Time < c.End && !ed.TrackOff[c.Track] && ed.SeqOf(c) == null)
                .OrderByDescending(ed.IsSel).ThenBy(c => c.Track).FirstOrDefault();
    void FrameHold(bool insert)
    {
        if (HoldTarget() is not { } c) { SetTask("Put the playhead over a video clip first", 2500); return; }
        if (insert) ed.InsertFrameHold(c, ed.Time); else ed.AddFrameHold(c, ed.Time);
    }


    /// <summary>Thumbnail Studio / Intro &amp; Hook windows (one each).</summary>
    ThumbnailWindow? thumbWin; HookWindow? hookWin;
    internal void OpenThumbnails()
    {
        if (ed.Clips.Count(c => c.Kind is ClipKind.Video or ClipKind.Image) == 0) { Alert.Show(this, "Add a video or picture to the timeline first.", "Thumbnail Studio"); return; }
        Pause();
        if (thumbWin is { IsLoaded: true }) { thumbWin.Activate(); return; }
        thumbWin = new ThumbnailWindow(this, ed) { Owner = this }; thumbWin.Show();
    }
    internal void OpenHook()
    {
        if (ed.Clips.Count == 0) { Alert.Show(this, "Add your video to the timeline first.", "Intro & Hook"); return; }
        Pause();
        if (hookWin is { IsLoaded: true }) { hookWin.Activate(); return; }
        hookWin = new HookWindow(this, ed) { Owner = this }; hookWin.Show();
    }
    /// <summary>Background renders of speed changes show in the status bar.</summary>
    void InitSpeed()
    {
        Tl.SpeedRequested += SpeedDialog;
        Remap.StatusChanged += () => Dispatcher.BeginInvoke(() => { if (Remap.Status is { } s) SetTask(s, 0); else SetTask("Speed changes rendered", 1500); });
    }
}
