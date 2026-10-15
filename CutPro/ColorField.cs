using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>Colour picker for the inspector: swatch button → popup with palette, recent colours, hex entry and opacity.</summary>
public class ColorField : Button
{
    static readonly string[] Palette =
    {
        "#FFFFFFFF", "#FFD9D9D9", "#FFA6A6A6", "#FF737373", "#FF404040", "#FF1A1A1A", "#FF000000", "#00FFFFFF",
        "#FFFF4A4A", "#FFC8102E", "#FF7F0000", "#FFFF8A00", "#FFFFB347", "#FFFFE14A", "#FFFFD400", "#FFF5E6C8",
        "#FF5BE36B", "#FF2F9E72", "#FF0B6E4F", "#FF4AE3FF", "#FF2EF2FF", "#FF00A6C8", "#FF4A7CFF", "#FF1E5AFF",
        "#FF0B1F3A", "#FF9B5CFF", "#FF3A0CA3", "#FF14002A", "#FFFF3DB8", "#FFFF5A8A", "#FFFFD6F5", "#FFFFF1B8",
        "#FFB8860B", "#FF8B5A2B",
    };
    static readonly List<string> Recent = new();
    static Brush Edge => ThemeManager.Brush("Input.Border");

    readonly Border swatch = new() { Width = 26, Height = 14, BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
    readonly TextBlock label = new() { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas"), FontSize = 11 };
    readonly Action<string> onChange;
    Color color;

    public ColorField(string hex, Action<string> onChange)
    {
        this.onChange = onChange;
        SetResourceReference(StyleProperty, typeof(Button));   // implicit styles only match the exact type
        color = TitleVisual.ParseColor(hex, Colors.White);
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(swatch); sp.Children.Add(label);
        Content = sp; HorizontalAlignment = HorizontalAlignment.Left; Padding = new Thickness(5, 3, 8, 3); Margin = new Thickness(0);
        Refresh();
        Click += (_, _) => Open();
    }

    static string Hex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    void Refresh() { swatch.Background = new SolidColorBrush(color); label.Text = Hex(color); }

    void Set(Color c, bool remember)
    {
        color = c; Refresh();
        var hex = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        if (remember) { Recent.Remove(hex); Recent.Insert(0, hex); if (Recent.Count > 8) Recent.RemoveAt(8); }
        onChange(hex);
    }

    void Open()
    {
        var alpha = new Slider { Minimum = 0, Maximum = 255, Value = color.A, Width = 130, VerticalAlignment = VerticalAlignment.Center };
        var hexBox = new TextBox { Text = Hex(color), Width = 96 };
        var wrap = new WrapPanel { Width = 8 * 24 };
        foreach (var hex in Recent.Concat(Palette).Distinct())
        {
            var c = TitleVisual.ParseColor(hex, Colors.White);
            var b = new Border
            {
                Width = 20, Height = 20, Margin = new Thickness(2), CornerRadius = new CornerRadius(3), BorderBrush = Edge, BorderThickness = new Thickness(1),
                Background = c.A == 0 ? Checker() : new SolidColorBrush(c), Cursor = Cursors.Hand, ToolTip = c.A == 0 ? "Transparent" : Hex(c)
            };
            b.MouseLeftButtonUp += (_, _) => { Set(c, true); alpha.Value = c.A; hexBox.Text = Hex(c); };
            wrap.Children.Add(b);
        }
        hexBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var t = hexBox.Text.Trim(); if (!t.StartsWith('#')) t = "#" + t;
            try { var c = (Color)ColorConverter.ConvertFromString(t); Set(c, true); alpha.Value = c.A; } catch { hexBox.Text = Hex(color); }
        };
        alpha.ValueChanged += (_, e) => { if ((byte)e.NewValue != color.A) Set(Color.FromArgb((byte)e.NewValue, color.R, color.G, color.B), false); };
        var fg = ThemeManager.Brush("Fg");
        StackPanel Row(string text, UIElement el)
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 6, 0, 0) };
            r.Children.Add(new TextBlock { Text = text, Width = 58, Foreground = fg, VerticalAlignment = VerticalAlignment.Center }); r.Children.Add(el);
            return r;
        }
        var panel = new StackPanel();
        panel.Children.Add(wrap);
        panel.Children.Add(Row("Hex", hexBox));
        panel.Children.Add(Row("Opacity", alpha));
        new Popup
        {
            PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border { Background = ThemeManager.Brush("Popup.Bg"), BorderBrush = ThemeManager.Brush("Popup.Border"), BorderThickness = new Thickness(1), Padding = new Thickness(8), CornerRadius = new CornerRadius(4), Child = panel },
            IsOpen = true,
        };
    }

    static Brush Checker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        g.Children.Add(new GeometryDrawing(Brushes.LightGray, null, new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 4, 4)), new RectangleGeometry(new Rect(4, 4, 4, 4)) } }));
        return new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
    }
}
