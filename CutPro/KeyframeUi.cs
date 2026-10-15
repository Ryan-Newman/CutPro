using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>Keyframes + formulas UI: Effect Controls property rows (stopwatch, ◀ ◆ ▶, ƒx formula editor), the KEYFRAMES
/// panel tab (dope sheet / graph editor) and its toolbar.</summary>
public partial class MainWindow
{
    void InitKeyframes()
    {
        Kfe.Ed = ed;
        ed.Changed += () => { if (Kfe.IsVisible) Kfe.InvalidateVisual(); };
        ed.PropsChanged += () => { if (Kfe.IsVisible) Kfe.InvalidateVisual(); RefreshProps(); };
        ed.MasksChanged += () => { if (Kfe.IsVisible) Kfe.InvalidateVisual(); };
        Kfe.Edited += () => { RefreshProps(); Tl.InvalidateVisual(); };
    }

    // ---------------- panel tabs ----------------
    void PanelTab_Click(object s, RoutedEventArgs e) => ShowPanel((string)((FrameworkElement)s).Tag);

    void ShowPanel(string which)
    {
        // a panel docked into the bottom panel (DockUi) takes the timeline's place while its tab is chosen
        if (which is not ("Timeline" or "Keys") && docks.Count > 0 && DockOf(which) is { Zone: "Bottom", Fixed: false })
        {
            if (bottomActive != which) { bottomActive = which; RefreshDock(); return; }
            Tl.Visibility = Ph.Visibility = HScroll.Visibility = TlBar.Visibility = Kfe.Visibility = KeyBar.Visibility = Visibility.Collapsed;
            BottomHost.Visibility = Visibility.Visible;
            foreach (var d in In("Bottom").Where(d => !d.Fixed)) d.El.Visibility = d.Id == which ? Visibility.Visible : Visibility.Collapsed;
            TabTimelineLine.Visibility = TabKeysLine.Visibility = Visibility.Hidden;
            TabTimelineText.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
            TabKeysText.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
            return;
        }
        if (which is not ("Timeline" or "Keys")) which = "Timeline";
        if (docks.Count > 0 && bottomActive != which && DockTabs.Children.Count > 0) { bottomActive = which; RefreshDock(); return; }
        bottomActive = which;
        BottomHost.Visibility = Visibility.Collapsed;
        bool keys = which == "Keys";
        Tl.Visibility = Ph.Visibility = HScroll.Visibility = TlBar.Visibility = keys ? Visibility.Collapsed : Visibility.Visible;
        Kfe.Visibility = KeyBar.Visibility = keys ? Visibility.Visible : Visibility.Collapsed;
        TabTimelineLine.Visibility = keys ? Visibility.Hidden : Visibility.Visible;
        TabKeysLine.Visibility = keys ? Visibility.Visible : Visibility.Hidden;
        TabTimelineText.SetResourceReference(TextBlock.ForegroundProperty, keys ? "B.Fg.Dim" : "B.Fg");
        TabKeysText.SetResourceReference(TextBlock.ForegroundProperty, keys ? "B.Fg" : "B.Fg.Dim");
        if (keys) { Kfe.InvalidateVisual(); Kfe.Focus(); }
        if (IsLoaded) FitPanelTools();
    }

    void KfMode_Click(object s, RoutedEventArgs e)
    {
        bool graph = (string)((FrameworkElement)s).Tag == "Graph";
        Kfe.GraphMode = graph; KfGraph.IsChecked = graph; KfDope.IsChecked = !graph;
    }
    void KfInterp_Click(object s, RoutedEventArgs e)
    {
        if (Kfe.SelectedCount == 0) { SetTask("Select keyframes first (click, Shift+click or drag a box around them)", 3500); return; }
        Kfe.ApplyInterp((string)((FrameworkElement)s).Tag);
    }
    void KfDelete_Click(object s, RoutedEventArgs e) => Kfe.DeleteSelected();
    void KfFit_Click(object s, RoutedEventArgs e) => Kfe.Fit();

    // ---------------- Effect Controls rows ----------------
    sealed class PropUi
    {
        public Clip C = null!; public AnimProp P;
        public Slider Sl = null!; public TextBox Box = null!; public Button Watch = null!, Key = null!, Fx = null!;
        public TextBlock? FxStatus; public bool Busy;
    }
    readonly List<PropUi> liveProps = new();
    readonly HashSet<string> formulaOpen = new();
    static readonly FontFamily TextFont = new("Segoe UI Symbol, Segoe UI");       // (the Icon style's icon font has no ◀ ◆ ▶ ƒ ?)

    /// <summary>Playhead position clamped into the clip, as source time (where keys go).</summary>
    double KeySrc(Clip c) => Anim.SrcTime(c, Math.Clamp(ed.Time, c.Start, Math.Max(c.Start, c.End - 1.0 / TimeUtil.Fps)));

    void PropRow(Clip c, AnimProp p, Panel? into = null)
    {
        var info = Anim.Of(p);
        var ui = new PropUi { C = c, P = p };
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var w in new[] { 22.0, 86, -1, 48, 54, 26 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        UIElement At(UIElement e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); return e; }

        ui.Watch = new Button { Content = "", Style = (Style)FindResource("Icon"), Padding = new Thickness(0), FontSize = 13,
                                ToolTip = "Animate: keyframes (click again to stop animating)" };
        ui.Watch.Click += (_, _) => ToggleAnimate(c, p);
        At(ui.Watch, 0);
        var label = new TextBlock { Text = info.Label, VerticalAlignment = VerticalAlignment.Center, Foreground = Dim, Cursor = Cursors.Hand,
                                    ToolTip = "Show in the Keyframes panel (graph)", TextTrimming = TextTrimming.CharacterEllipsis };
        label.MouseLeftButtonUp += (_, _) => { ShowPanel("Keys"); KfMode_Click(KfGraph, new RoutedEventArgs()); Kfe.ShowLane(p.ToString()); };
        At(label, 1);
        double v0 = Shown(c, p);
        ui.Sl = new Slider { Minimum = Math.Min(info.SoftMin, v0), Maximum = Math.Max(info.SoftMax, v0), Value = v0, SmallChange = info.Step, LargeChange = info.Step * 10, VerticalAlignment = VerticalAlignment.Center };
        At(ui.Sl, 2);
        ui.Box = new TextBox { Text = v0.ToString("0.##", CultureInfo.InvariantCulture), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        At(ui.Box, 3);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button Tiny(string text, string tip, Action a) { var b = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource("Icon"), Padding = new Thickness(0), Width = 17, FontSize = 11, FontFamily = TextFont }; b.Click += (_, _) => a(); nav.Children.Add(b); return b; }
        Tiny("◀", "Previous keyframe", () => GoToPropKey(c, p, -1));
        ui.Key = Tiny("◇", "Add / remove a keyframe at the playhead", () => TogglePropKey(c, p));
        Tiny("▶", "Next keyframe", () => GoToPropKey(c, p, 1));
        At(nav, 4);
        ui.Fx = new Button { Content = "ƒx", Style = (Style)FindResource("Icon"), Padding = new Thickness(0), FontStyle = FontStyles.Italic, FontFamily = TextFont, FontSize = 12, ToolTip = "Formula (expression) for this property" };
        At(ui.Fx, 5);
        if (p == AnimProp.Speed) { ui.Fx.Visibility = Visibility.Hidden; label.ToolTip = "Time remapping: keyframes make speed ramps (the clip keeps its length)"; }

        void Apply(double v)
        {
            if (Anim.HasKeys(c, p)) Anim.SetKey(c, p, KeySrc(c), v);
            else Anim.SetBase(c, p, v);
            ed.Touch(); RefreshProps(); Tl.InvalidateVisual();
        }
        ui.Sl.ValueChanged += (_, e) =>
        {
            if (ui.Busy) return; ui.Busy = true;
            double v = Math.Round(e.NewValue / info.Step) * info.Step;
            ui.Box.Text = v.ToString("0.##", CultureInfo.InvariantCulture); Apply(v); ui.Busy = false;
        };
        ui.Box.TextChanged += (_, _) =>
        {
            if (ui.Busy || !double.TryParse(ui.Box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
            v = Math.Clamp(v, info.Min, info.Max);
            ui.Busy = true;
            if (v < ui.Sl.Minimum) ui.Sl.Minimum = v; if (v > ui.Sl.Maximum) ui.Sl.Maximum = v;
            ui.Sl.Value = v; Apply(v); ui.Busy = false;
        };
        (into ?? Inspector).Children.Add(g);

        // formula editor (ƒx)
        var fxPanel = new StackPanel { Margin = new Thickness(22, 0, 0, 6) };
        string key = c.Id + "|" + p;
        var tr = Anim.Track(c, p);
        bool open = formulaOpen.Contains(key) || !string.IsNullOrWhiteSpace(tr?.Formula);
        fxPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ui.Fx.Click += (_, _) =>
        {
            bool now = fxPanel.Visibility != Visibility.Visible;
            fxPanel.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
            if (now) formulaOpen.Add(key); else formulaOpen.Remove(key);
        };
        var row1 = new DockPanel();
        var on = new CheckBox { IsChecked = tr?.FormulaOn ?? true, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Formula on / off" };
        DockPanel.SetDock(on, Dock.Left);
        var text = new TextBox { Text = tr?.Formula ?? "", FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Margin = new Thickness(4, 0, 0, 0),
                                 AcceptsReturn = false, ToolTip = "e.g.  value + 20*sin(t*2*pi)   ·   wiggle(2, 10)   ·   cycle()" };
        row1.Children.Add(on); row1.Children.Add(text);
        var row2 = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        var presets = new ComboBox
        {
            Width = 132, ToolTip = "Insert a ready-made formula", SelectedIndex = 0,
            ItemsSource = new[] { new ComboBoxItem { Content = "Presets…", IsEnabled = false } }
                .Concat(Formula.Presets.Select(x => new ComboBoxItem { Content = x.name, ToolTip = $"{x.formula}\n{x.tip}" })).ToList(),
        };
        presets.SelectedIndex = 0;
        DockPanel.SetDock(presets, Dock.Left);
        var help = new Button { Content = "?", Style = (Style)FindResource("Icon"), Width = 22, FontFamily = TextFont, ToolTip = Formula.Help, Margin = new Thickness(4, 0, 0, 0) };
        help.Click += (_, _) => Alert.Show(this, Formula.Help, "Formula reference");
        DockPanel.SetDock(help, Dock.Left);
        ui.FxStatus = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        row2.Children.Add(presets); row2.Children.Add(help); row2.Children.Add(ui.FxStatus);
        fxPanel.Children.Add(row1); fxPanel.Children.Add(row2);
        (into ?? Inspector).Children.Add(fxPanel);
        presets.SelectionChanged += (_, _) => { if (presets.SelectedIndex > 0) { text.Text = Formula.Presets[presets.SelectedIndex - 1].formula; presets.SelectedIndex = 0; text.Focus(); text.CaretIndex = text.Text.Length; } };
        text.TextChanged += (_, _) =>
        {
            var t = Anim.Ensure(c, p);
            t.Formula = string.IsNullOrWhiteSpace(text.Text) ? null : text.Text;
            Anim.Prune(c);
            ed.Touch(); RefreshProps(); Tl.InvalidateVisual();
        };
        on.Click += (_, _) => { Anim.Ensure(c, p).FormulaOn = on.IsChecked == true; Anim.Prune(c); ed.Touch(); RefreshProps(); };
        liveProps.Add(ui);
        RefreshProp(ui);
    }

    /// <summary>Value shown in a row: keyed value at the playhead (the base value when not keyframed). The formula result is
    /// shown separately (ƒx status) — like After Effects, you edit the value the formula starts from.</summary>
    double Shown(Clip c, AnimProp p) => Anim.HasKeys(c, p) ? Anim.Keyed(c, p, KeySrc(c)) : Anim.Base(c, p);

    void RefreshProps() { foreach (var ui in liveProps) RefreshProp(ui); }

    void RefreshProp(PropUi ui)
    {
        var (c, p) = (ui.C, ui.P);
        bool keyed = Anim.HasKeys(c, p);
        ui.Watch.SetResourceReference(Control.ForegroundProperty, keyed ? "B.Accent" : "B.Fg.Dim");
        bool atKey = keyed && Anim.KeyAt(c, p, KeySrc(c)) != null;
        ui.Key.Content = atKey ? "◆" : "◇";
        ui.Key.SetResourceReference(Control.ForegroundProperty, atKey ? "B.Accent" : "B.Fg.Dim");
        var tr = Anim.Track(c, p);
        bool hasF = !string.IsNullOrWhiteSpace(tr?.Formula);
        ui.Fx.SetResourceReference(Control.ForegroundProperty, hasF && tr!.FormulaOn ? "B.Accent" : "B.Fg.Dim");
        if (!ui.Busy && !ui.Box.IsKeyboardFocusWithin)
        {
            double v = Shown(c, p);
            ui.Busy = true;
            if (v < ui.Sl.Minimum) ui.Sl.Minimum = v; if (v > ui.Sl.Maximum) ui.Sl.Maximum = v;
            if (Math.Abs(ui.Sl.Value - v) > 1e-9) ui.Sl.Value = v;
            var s = v.ToString("0.##", CultureInfo.InvariantCulture);
            if (ui.Box.Text != s) ui.Box.Text = s;
            ui.Busy = false;
        }
        if (ui.FxStatus != null)
        {
            if (!hasF) { ui.FxStatus.Text = "Formula: type one or pick a preset"; ui.FxStatus.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim"); }
            else if (Formula.Check(tr!.Formula!) is { } err) { ui.FxStatus.Text = "⚠ " + err; ui.FxStatus.SetResourceReference(TextBlock.ForegroundProperty, "B.Error"); ui.FxStatus.ToolTip = err; }
            else
            {
                double r = Anim.Value(c, p, Math.Clamp(ed.Time, c.Start, c.End));
                ui.FxStatus.Text = tr.FormulaOn ? $"= {r.ToString("0.##", CultureInfo.InvariantCulture)} at the playhead" : "off";
                ui.FxStatus.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim"); ui.FxStatus.ToolTip = null;
            }
        }
    }

    void ToggleAnimate(Clip c, AnimProp p)
    {
        var info = Anim.Of(p);
        if (!Anim.HasKeys(c, p))
        {
            double v = Anim.Base(c, p);
            ed.Edit(() => Anim.SetKey(c, p, KeySrc(c), v));
            SetTask($"{info.Label}: keyframe added — move the playhead and change the value to animate", 5000);
            return;
        }
        int n = Anim.Track(c, p)!.Keys.Count;
        if (!Alert.Confirm(this, $"This removes all {n} keyframe{(n == 1 ? "" : "s")} of {info.Label}. The value at the playhead is kept. (Ctrl+Z undoes it.)",
                           $"Stop animating {info.Label}?", "Remove keyframes", "Cancel", AlertKind.Warning)) return;
        double keep = Anim.Keyed(c, p, KeySrc(c));
        ed.Edit(() => { Anim.SetBase(c, p, keep); Anim.Track(c, p)!.Keys.Clear(); Anim.Prune(c); });
    }

    void TogglePropKey(Clip c, AnimProp p)
    {
        double src = KeySrc(c);
        if (Anim.KeyAt(c, p, src) is { } k) ed.Edit(() => { Anim.Track(c, p)!.Keys.Remove(k); Anim.Prune(c); });
        else { double v = Shown(c, p); ed.Edit(() => Anim.SetKey(c, p, src, v)); }
    }

    void GoToPropKey(Clip c, AnimProp p, int dir)
    {
        var keys = Anim.Track(c, p)?.Keys;
        double src = KeySrc(c), eps = 0.5 / TimeUtil.Fps;
        var k = keys == null ? null : dir < 0 ? keys.LastOrDefault(x => x.T < src - eps) : keys.FirstOrDefault(x => x.T > src + eps);
        if (k == null) { SetTask(dir < 0 ? "No earlier keyframe" : "No later keyframe", 2000); return; }
        Pause();
        ed.Seek(Math.Clamp(Anim.TimelineTime(c, k.T), c.Start, c.End - 1.0 / TimeUtil.Fps));
    }
}
