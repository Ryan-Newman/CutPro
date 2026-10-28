using System.Windows;
using System.Windows.Controls;

namespace CutPro;

/// <summary>File ▸ New Item (and the Project panel's menu): colour matte, gradient, bars and tone, counting leader — a settings dialog,
/// the file rendered in the background, the item added to the open bin. Editing a generated item renders a new file and swaps the item's
/// path (its clips keep their times, like Replace Footage).</summary>
public partial class MainWindow
{
    MenuItem NewItemMenu()
    {
        var mi = new MenuItem { Header = "New Item" };
        void Item(string h, GenKind k) { var x = new MenuItem { Header = h }; x.Click += (_, _) => _ = NewGenerated(k); mi.Items.Add(x); }
        Item("Colour Matte…", GenKind.ColorMatte);
        Item("Gradient…", GenKind.Gradient);
        Item("Bars and Tone…", GenKind.Bars);
        Item("Counting Leader…", GenKind.Countdown);
        return mi;
    }

    /// <summary>The settings dialog of a generator (false = cancelled).</summary>
    bool GeneratorDialog(Generator g, string title)
    {
        var p = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        void Row(string label, UIElement e)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var tb = new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(tb, Dock.Left); dp.Children.Add(tb); dp.Children.Add(e); p.Children.Add(dp);
        }
        TextBox Num(double v, Action<double> set)
        {
            var t = new TextBox { Text = v.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture), Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
            t.TextChanged += (_, _) => { if (double.TryParse(t.Text, out var x)) set(x); };
            return t;
        }
        switch (g.Kind)
        {
            case GenKind.ColorMatte: Row("Colour", new ColorField(g.Color1, h => g.Color1 = h)); break;
            case GenKind.Gradient:
            {
                Row("Start colour", new ColorField(g.Color1, h => g.Color1 = h));
                Row("End colour", new ColorField(g.Color2, h => g.Color2 = h));
                var shape = new ComboBox { ItemsSource = new[] { "Linear", "Radial" }, SelectedIndex = g.Radial ? 1 : 0, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
                shape.SelectionChanged += (_, _) => g.Radial = shape.SelectedIndex == 1;
                Row("Shape", shape);
                Row("Angle (°)", Num(g.Angle, v => g.Angle = v));
                break;
            }
            case GenKind.Bars:
                Row("Length (s)", Num(g.Seconds, v => g.Seconds = Math.Clamp(v, 1, 3600)));
                Row("Tone (dBFS)", Num(g.ToneDb, v => g.ToneDb = Math.Clamp(v, -60, 0)));
                Row("Tone (Hz)", Num(g.ToneHz, v => g.ToneHz = Math.Clamp(v, 20, 20000)));
                break;
            case GenKind.Countdown:
            {
                Row("Count from", Num(g.Seconds, v => g.Seconds = Math.Clamp(Math.Round(v), 3, 30)));
                Row("Beep (dBFS)", Num(g.ToneDb, v => g.ToneDb = Math.Clamp(v, -60, 0)));
                var all = new CheckBox { Content = "Beep at every number (otherwise only at 2)", IsChecked = g.BeepAll };
                all.Click += (_, _) => g.BeepAll = all.IsChecked == true;
                p.Children.Add(all);
                break;
            }
        }
        if (!g.Still || g.Kind == GenKind.Gradient)
            p.Children.Add(new TextBlock { Text = $"{g.W}×{g.H}, {TimeUtil.Label(g.Fps)} fps (the sequence's)", Margin = new Thickness(0, 8, 0, 0), Opacity = 0.7 });
        return Alert.Show(this, g.Kind switch
        {
            GenKind.ColorMatte => "A full-frame colour (backgrounds, title cards).",
            GenKind.Gradient => "A full-frame colour ramp.",
            GenKind.Bars => "SMPTE HD colour bars with a reference tone.",
            _ => "A universal counting leader: the seconds count down to 2 with a sweep, a beep at 2, then black until the program starts.",
        }, title, AlertKind.Question, new[] { "OK", "Cancel" }, p) == 0;
    }

    async Task NewGenerated(GenKind k)
    {
        var g = Generators.For(k);
        if (k == GenKind.Gradient) { g.Color1 = "#1E5AFF"; g.Color2 = "#0B1F3A"; }
        if (!GeneratorDialog(g, g.Title)) return;
        SetTask($"Rendering {g.Title}…", 0);
        try
        {
            var m = await Generators.MakeAsync(g, new Progress<double>(f => SetTask($"Rendering {g.Title}… {f:0%}", 0)));
            if (m == null) { Alert.Show(this, MediaProbe.LastError ?? "The file could not be read.", g.Title, AlertKind.Error); return; }
            m.Bin = binPath;
            ed.Media.Add(m); binRef = null; ed.RaiseChanged(); RefreshBin();
            BinList.SelectedItem = m;
            SetTask($"{g.Title} added to the Project panel — drag it onto the timeline", 3500);
        }
        catch (Exception ex) { SetTask("", 1); Alert.Show(this, ex.Message, g.Title, AlertKind.Error); }
    }

    /// <summary>Project panel ▸ Edit Generator…: new settings → a new file; the item (and every clip of it) shows it.</summary>
    async Task EditGenerated(MediaItem m)
    {
        if (m.Gen == null) return;
        var g = JsonSerializerClone(m.Gen);
        if (!GeneratorDialog(g, "Edit " + g.Title)) return;
        SetTask($"Rendering {g.Title}…", 0);
        try
        {
            var f = await Generators.EnsureAsync(g, new Progress<double>(x => SetTask($"Rendering {g.Title}… {x:0%}", 0)));
            Pause();
            m.Gen = g; m.Path = f; m.Thumb = null;
            await MediaProbe.FillAsync(m);
            binRef = null; ed.RaiseChanged(); Pv.InvalidateVisual(); RefreshBin();
            SetTask($"{g.Title} updated", 2500);
        }
        catch (Exception ex) { SetTask("", 1); Alert.Show(this, ex.Message, g.Title, AlertKind.Error); }
    }
    static T JsonSerializerClone<T>(T x) => System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(x))!;
}
