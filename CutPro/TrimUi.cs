using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>The trim tools in the window: the toolbar's tool menu, SetTool, and the Program monitor's two-up view while a trim-tool drag
/// runs (the outgoing frame on the left, the incoming one on the right — scrub-cache frames with the clip's colour, like scrubbing).</summary>
public partial class MainWindow
{
    Grid? trimView;
    Image? trimA, trimB;
    TextBlock? trimTa, trimTb;
    WriteableBitmap? trimWa, trimWb;

    static readonly (TrimTool t, string name, string key)[] TrimTools =
    {
        (TrimTool.Ripple, "Ripple Edit Tool", "tool.ripple"), (TrimTool.Roll, "Rolling Edit Tool", "tool.roll"), (TrimTool.Rate, "Rate Stretch Tool", "tool.rate"),
        (TrimTool.Slip, "Slip Tool", "tool.slip"), (TrimTool.Slide, "Slide Tool", "tool.slide"),
    };

    /// <summary>Premiere's keyboard trim: the selected clip's In or Out by n frames, using the current trim tool (Ripple when Selection or
    /// Razor is out). Shortens: the In point later, the Out point earlier. Linked sound along (the tools' usual rule).</summary>
    void KeyboardTrim(bool left, int frames)
    {
        Pause();
        var c = ed.Selected is { } s && ed.Editable(s) ? s : ed.Clips.FirstOrDefault(x => ed.IsSel(x) && ed.Editable(x));
        if (c == null) { SetTask("Select a clip to trim", 2000); return; }
        var tool = Tl.Tool is TrimTool.Ripple or TrimTool.Roll or TrimTool.Rate or TrimTool.Slip or TrimTool.Slide ? Tl.Tool : TrimTool.Ripple;
        ed.TrimEdit(tool, c, left, (left ? frames : -frames) / TimeUtil.Fps);
    }

    void SetTool(TrimTool t)
    {
        Tl.Tool = t;
        ToolSelect.IsChecked = t == TrimTool.Select;
        ToolRazor.IsChecked = t == TrimTool.Razor;
        bool trimTool = t is not (TrimTool.Select or TrimTool.Razor);
        ToolTrim.IsChecked = trimTool;
        TrimLabel.Text = trimTool ? TrimTools.First(x => x.t == t).name.Replace(" Tool", "").Replace(" Edit", "") : "Trim";
        Loc.Apply(TrimLabel);
        if (trimTool) SetTask(TrimHint(t), 4000);
    }

    static string TrimHint(TrimTool t) => t switch
    {
        TrimTool.Ripple => "Ripple Edit: drag a clip's edge — the clips after it move with it",
        TrimTool.Roll => "Rolling Edit: drag an edit point — one clip gets longer, the next one shorter",
        TrimTool.Rate => "Rate Stretch: drag a clip's edge — its speed changes to fill the new length",
        TrimTool.Slip => "Slip: drag a clip — its content moves, the clip stays where it is",
        TrimTool.Slide => "Slide: drag a clip — it moves, the clips next to it trim to make room",
        _ => "",
    };

    void ToolTrim_Click(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ToolTrim, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (t, name, key) in TrimTools)
        {
            var mi = new MenuItem { Header = name, InputGestureText = K(key), IsChecked = Tl.Tool == t, ToolTip = TrimHint(t) };
            mi.Click += (_, _) => SetTool(t);
            menu.Items.Add(mi);
        }
        menu.Closed += (_, _) => ToolTrim.IsChecked = Tl.Tool is not (TrimTool.Select or TrimTool.Razor);
        menu.IsOpen = true;
        lastMenu = menu;
    }

    void InitTrimUi()
    {
        Tl.TrimChanged += ShowTrim;
        Editor.AudioUnits = S.AudioUnits;
        if (S.AudioUnits) ZoomSlider.Maximum = Math.Log2(TimelineView.MaxPps);
    }

    /// <summary>The two-up view: the frame each side of the edit (slip: the clip's first and last frame).</summary>
    void ShowTrim(TrimDrag? s)
    {
        if (s == null) { if (trimView != null) trimView.Visibility = Visibility.Collapsed; return; }
        if (trimView == null)
        {
            trimView = new Grid { Background = Brushes.Black, IsHitTestVisible = false };
            trimView.ColumnDefinitions.Add(new ColumnDefinition()); trimView.ColumnDefinitions.Add(new ColumnDefinition());
            (Image, TextBlock) Half(int col)
            {
                var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(6) };
                var tb = new TextBlock { Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center,
                                         VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 40), Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), Padding = new Thickness(6, 2, 6, 2) };
                Grid.SetColumn(img, col); Grid.SetColumn(tb, col);
                trimView.Children.Add(img); trimView.Children.Add(tb);
                return (img, tb);
            }
            (trimA, trimTa) = Half(0); (trimB, trimTb) = Half(1);
            ((Grid)Pv.Parent).Children.Add(trimView);
        }
        trimView.Width = Pv.Width; trimView.Height = Pv.Height;
        trimView.Visibility = Visibility.Visible;
        var (a, b) = s.Shown;
        double fr = 1 / TimeUtil.Fps;
        // ripple / rate stretch / roll: the outgoing clip's last frame and the incoming clip's first; slip: the clip's first and last
        TrimFrame(a, a == null ? 0 : s.Tool == TrimTool.Slip ? TimeMap.Src(a, a.Start) : TimeMap.Src(a, a.End - fr), trimA!, trimTa!, ref trimWa, s.Tool == TrimTool.Slip ? "In" : "Out");
        TrimFrame(b, b == null ? 0 : s.Tool == TrimTool.Slip ? TimeMap.Src(b, b.End - fr) : TimeMap.Src(b, b.Start), trimB!, trimTb!, ref trimWb, s.Tool == TrimTool.Slip ? "Out" : "In");
    }

    void TrimFrame(Clip? c, double src, Image img, TextBlock label, ref WriteableBitmap? wb, string what)
    {
        img.Source = null; label.Text = "";
        if (c == null) return;
        label.Text = $"{what} {TimeUtil.Format(Math.Max(0, src))}";
        if (c.Kind != ClipKind.Video || ed.MediaOf(c) is not { } m || ed.SeqOf(c) != null) return;
        if (ScrubCache.Show(m.Path, src, ref wb, Grade.For(c.Grade)) >= 0) img.Source = wb;
    }
}
