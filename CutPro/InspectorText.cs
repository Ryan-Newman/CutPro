using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>Effect Controls for titles: styles, font, fill, outline, shadow, glow, background box, mask. Edits apply to every
/// selected title, so several titles can be restyled at once.</summary>
public partial class MainWindow
{
    static readonly Dictionary<string, bool> sectionOpen = new() { ["Text"] = true, ["Font"] = true, ["Fill"] = true };
    static FrameworkElement? inkScope;                     // the inspector card (its palette may be dark ink on a light surface)
    static Brush Dim => inkScope != null ? ThemeManager.Brush(inkScope, "Fg.Dim") : ThemeManager.Brush("Fg.Dim");   // (rebuilt on theme change)
    static Brush Accent => inkScope != null ? ThemeManager.Brush(inkScope, "Heading") : ThemeManager.Brush("Heading");   // section headings
    static Brush Fg => inkScope != null ? ThemeManager.Brush(inkScope, "Fg") : ThemeManager.Brush("Fg");
    static readonly (int w, string name)[] Weights =
        { (100, "Thin"), (200, "Extra Light"), (300, "Light"), (400, "Regular"), (500, "Medium"), (600, "Semibold"), (700, "Bold"), (800, "Extra Bold"), (900, "Black") };
    static List<string>? fontNames;
    static readonly Dictionary<string, BitmapSource> styleThumbs = new();

    /// <summary>A collapsible inspector section; remembers whether it was open.</summary>
    StackPanel Section(string name, bool open = false)
    {
        var body = new StackPanel { Margin = new Thickness(2, 2, 0, 4) };
        var ex = new Expander
        {
            Header = new TextBlock { Text = name.ToUpperInvariant(), Foreground = Accent, FontSize = 11 },
            IsExpanded = sectionOpen.TryGetValue(name, out var o) ? o : open, Content = body, Foreground = Fg, Margin = new Thickness(0, 8, 0, 0)
        };
        ex.Expanded += (_, _) => sectionOpen[name] = true;
        ex.Collapsed += (_, _) => sectionOpen[name] = false;
        Inspector.Children.Add(ex);
        return body;
    }

    static void Row(Panel into, string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var lb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Dim };
        Grid.SetColumn(control, 1);
        g.Children.Add(lb); g.Children.Add(control);
        into.Children.Add(g);
    }

    List<Clip> SelectedTitles(Clip primary)
    {
        var list = ed.Clips.Where(x => x.Kind == ClipKind.Text && ed.IsSel(x)).ToList();
        if (!list.Contains(primary)) list.Insert(0, primary);
        return list;
    }

    void BuildTitleInspector(Clip c)
    {
        var st = c.Look;
        var titles = SelectedTitles(c);
        void StyleEdit(Action<TextStyle> f) { foreach (var t in titles) f(t.Look); ed.Touch(); }
        void TitleEdit(Action<Clip> f) { foreach (var t in titles) f(t); ed.Touch(); }
        void ColorRow(Panel p, string label, string value, Action<TextStyle, string> set) => Row(p, label, new ColorField(value, hex => StyleEdit(s => set(s, hex))));
        void Check(Panel p, string label, bool value, Action<TextStyle, bool> set)
        {
            var cb = new CheckBox { IsChecked = value, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center };
            cb.Click += (_, _) => { StyleEdit(s => set(s, cb.IsChecked == true)); BuildInspector(); };
            Row(p, label, cb);
        }
        void Num(Panel p, string label, double min, double max, double step, double value, Action<TextStyle, double> set) =>
            Slider(label, min, max, step, value, v => { foreach (var t in titles) set(t.Look, v); }, p);

        // ---- text + style ----
        var text = Section("Text", true);
        var tb = new TextBox { Text = c.Text, AcceptsReturn = true, MinHeight = 52, TextWrapping = TextWrapping.Wrap };
        tb.TextChanged += (_, _) => { c.Text = tb.Text; ed.Touch(); };
        text.Children.Add(tb);
        if (titles.Count > 1) text.Children.Add(new TextBlock { Text = $"Style edits apply to all {titles.Count} selected titles.", Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });
        var roles = new ComboBox { ItemsSource = new[] { "Title", "Subtitle", "Lower third", "Caption" }, SelectedIndex = (int)c.Role };
        roles.SelectionChanged += (_, _) => TitleEdit(t => t.Role = (TextRole)roles.SelectedIndex);
        Row(text, "Role", roles);

        var styles = new ComboBox { MaxDropDownHeight = 420 };
        foreach (var ns in StyleLibrary.All)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Tag = ns };
            item.Children.Add(new Border { Background = Brushes.Black, Margin = new Thickness(0, 1, 8, 1), Child = new Image { Source = StyleThumb(ns.Style), Width = 120, Height = 34 } });
            item.Children.Add(new TextBlock { Text = ns.Name + (ns.BuiltIn ? "" : "  (mine)"), VerticalAlignment = VerticalAlignment.Center });
            styles.Items.Add(item);
        }
        styles.SelectionChanged += (_, _) =>
        {
            if (styles.SelectedItem is FrameworkElement { Tag: NamedStyle ns }) { ed.ApplyStyle(ns.Style); }
        };
        Row(text, "Apply style", styles);
        var styleBtns = new StackPanel { Orientation = Orientation.Horizontal };
        var save = new Button { Content = "Save style…", ToolTip = "Save this look as a reusable style (all projects)" };
        save.Click += (_, _) =>
        {
            var name = Prompt("Save text style", "Style name:", "My style");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (StyleLibrary.BuiltIn.Any(b => b.Name == name)) name += " (mine)";
            StyleLibrary.SaveUser(name, c.Look); BuildInspector();
        };
        styleBtns.Children.Add(save);
        if (StyleLibrary.User.Count > 0)
        {
            var del = new Button { Content = "Delete style…" };
            del.Click += (_, _) =>
            {
                var menu = new ContextMenu();
                foreach (var u in StyleLibrary.User.ToList())
                {
                    var mi = new MenuItem { Header = u.Name };
                    mi.Click += (_, _) => { StyleLibrary.DeleteUser(u.Name); BuildInspector(); };
                    menu.Items.Add(mi);
                }
                menu.PlacementTarget = del; menu.IsOpen = true;
            };
            styleBtns.Children.Add(del);
        }
        Row(text, "", styleBtns);

        // ---- font ----
        var font = Section("Font", true);
        fontNames ??= Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n).ToList();
        var fonts = new ComboBox { ItemsSource = fontNames, SelectedItem = fontNames.Contains(st.Font) ? st.Font : null, MaxDropDownHeight = 420, IsTextSearchEnabled = true };
        fonts.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        var ft = new DataTemplate();
        var fe = new FrameworkElementFactory(typeof(TextBlock));
        fe.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        fe.SetBinding(TextBlock.FontFamilyProperty, new System.Windows.Data.Binding());
        fe.SetValue(TextBlock.FontSizeProperty, 15.0);
        ft.VisualTree = fe;
        fonts.ItemTemplate = ft;
        fonts.SelectionChanged += (_, _) => { if (fonts.SelectedItem is string f) StyleEdit(s => s.Font = f); };
        Row(font, "Font", fonts);
        var weight = new ComboBox { ItemsSource = Weights.Select(w => w.name).ToList(), SelectedIndex = Array.FindIndex(Weights, w => w.w == st.Weight) };
        weight.SelectionChanged += (_, _) => { if (weight.SelectedIndex >= 0) StyleEdit(s => s.Weight = Weights[weight.SelectedIndex].w); };
        Row(font, "Weight", weight);
        var toggles = new WrapPanel();
        void Toggle(string label, bool v, Action<TextStyle, bool> set)
        {
            var cb = new CheckBox { Content = label, IsChecked = v, Foreground = Fg, Margin = new Thickness(0, 0, 12, 0) };
            cb.Click += (_, _) => StyleEdit(s => set(s, cb.IsChecked == true));
            toggles.Children.Add(cb);
        }
        Toggle("Italic", st.Italic, (s, v) => s.Italic = v);
        Toggle("Underline", st.Underline, (s, v) => s.Underline = v);
        Toggle("ALL CAPS", st.AllCaps, (s, v) => s.AllCaps = v);
        Row(font, "Style", toggles);
        var align = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var a in new[] { "Left", "Center", "Right" })
        {
            var rb = new RadioButton { Content = a, GroupName = "align" + c.Id, IsChecked = st.Align == a, Foreground = Fg, Margin = new Thickness(0, 0, 10, 0) };
            rb.Checked += (_, _) => StyleEdit(s => s.Align = a);
            align.Children.Add(rb);
        }
        Row(font, "Align", align);
        Num(font, "Size", 8, 400, 1, st.Size, (s, v) => s.Size = v);
        Num(font, "Tracking", -200, 1000, 5, st.Tracking, (s, v) => s.Tracking = v);
        Num(font, "Line height %", 60, 300, 1, st.Leading, (s, v) => s.Leading = v);

        // ---- appearance ----
        var fill = Section("Fill", true);
        ColorRow(fill, "Color", st.Fill, (s, h) => s.Fill = h);
        Check(fill, "Gradient", st.Gradient, (s, v) => s.Gradient = v);
        if (st.Gradient)
        {
            ColorRow(fill, "Color 2", st.Fill2, (s, h) => s.Fill2 = h);
            Num(fill, "Angle", 0, 360, 1, st.GradientAngle, (s, v) => s.GradientAngle = v);
        }
        var stroke = Section("Outline");
        Check(stroke, "Enabled", st.Stroke, (s, v) => s.Stroke = v);
        ColorRow(stroke, "Color", st.StrokeColor, (s, h) => s.StrokeColor = h);
        Num(stroke, "Width", 0, 40, 0.5, st.StrokeWidth, (s, v) => s.StrokeWidth = v);
        var shadow = Section("Drop shadow");
        Check(shadow, "Enabled", st.Shadow, (s, v) => s.Shadow = v);
        ColorRow(shadow, "Color", st.ShadowColor, (s, h) => s.ShadowColor = h);
        Num(shadow, "Opacity %", 0, 100, 1, st.ShadowOpacity, (s, v) => s.ShadowOpacity = v);
        Num(shadow, "Angle", 0, 360, 1, st.ShadowAngle, (s, v) => s.ShadowAngle = v);
        Num(shadow, "Distance", 0, 100, 0.5, st.ShadowDistance, (s, v) => s.ShadowDistance = v);
        Num(shadow, "Softness", 0, 100, 0.5, st.ShadowBlur, (s, v) => s.ShadowBlur = v);
        var glow = Section("Glow");
        Check(glow, "Enabled", st.Glow, (s, v) => s.Glow = v);
        ColorRow(glow, "Color", st.GlowColor, (s, h) => s.GlowColor = h);
        Num(glow, "Size", 0, 100, 0.5, st.GlowSize, (s, v) => s.GlowSize = v);
        Num(glow, "Opacity %", 0, 100, 1, st.GlowOpacity, (s, v) => s.GlowOpacity = v);
        var box = Section("Background box");
        Check(box, "Enabled", st.Box, (s, v) => s.Box = v);
        ColorRow(box, "Color", st.BoxColor, (s, h) => s.BoxColor = h);
        Num(box, "Opacity %", 0, 100, 1, st.BoxOpacity, (s, v) => s.BoxOpacity = v);
        Num(box, "Padding", 0, 200, 1, st.BoxPadding, (s, v) => s.BoxPadding = v);
        Num(box, "Corner radius", 0, 200, 1, st.BoxRadius, (s, v) => s.BoxRadius = v);

        // ---- text animation: in / out by letter, word or line; roll / crawl ----
        var fx = c.TextFx ?? new TextAnim();
        var anim = Section("Text animation", TitleAnim.Active(c.TextFx));
        ComboBox Pick(TextIn v)
        {
            var cb = new ComboBox { ItemsSource = TitleAnim.Names.Select(n => n.name).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(TitleAnim.Names, n => n.v == v)) };
            return cb;
        }
        void FxEdit(Action<TextAnim> f) { TitleEdit(t => { var a = t.TextFx ?? new TextAnim(); f(a); t.TextFx = TitleAnim.Active(a) ? a : null; }); ed.RaiseChanged(); }
        var inBox = Pick(fx.In); inBox.SelectionChanged += (_, _) => FxEdit(a => a.In = TitleAnim.Names[inBox.SelectedIndex].v);
        var outBox = Pick(fx.Out); outBox.SelectionChanged += (_, _) => FxEdit(a => a.Out = TitleAnim.Names[outBox.SelectedIndex].v);
        Row(anim, "In", inBox); Row(anim, "Out", outBox);
        Slider("In (s)", 0.1, 5, 0.1, fx.InDur, v => { foreach (var t in titles) if (t.TextFx != null) t.TextFx.InDur = v; }, anim);
        Slider("Out (s)", 0.1, 5, 0.1, fx.OutDur, v => { foreach (var t in titles) if (t.TextFx != null) t.TextFx.OutDur = v; }, anim);
        var roll = new ComboBox { ItemsSource = new[] { "None", "Roll up (credits)", "Crawl left (ticker)" }, SelectedIndex = (int)fx.Roll };
        roll.SelectionChanged += (_, _) => FxEdit(a => a.Roll = (TextRoll)roll.SelectedIndex);
        Row(anim, "Roll / crawl", roll);
        anim.Children.Add(new TextBlock
        {
            Text = "Roll moves the whole text from below the frame to above it over the clip's length (credits: one name per line); crawl moves it from the right edge out to the left. Make the clip longer to slow it down.",
            TextWrapping = TextWrapping.Wrap, Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 4, 0, 0)
        });

        // ---- mask ----
        var mask = Section("Text mask", c.Mask != MaskMode.None);
        var modes = new ComboBox { ItemsSource = new[] { "None (normal title)", "Reveal video inside text", "Cut text out of video" }, SelectedIndex = (int)c.Mask };
        modes.SelectionChanged += (_, _) => { TitleEdit(t => t.Mask = (MaskMode)modes.SelectedIndex); ed.RaiseChanged(); };
        Row(mask, "Mode", modes);
        Slider("Feather", 0, 100, 0.5, c.MaskFeather, v => { foreach (var t in titles) t.MaskFeather = v; }, mask);
        mask.Children.Add(new TextBlock
        {
            Text = "A mask title is invisible itself: it masks every track below it (V3 masks V2+V1, V2 masks V1), like Premiere's Track Matte Key. Opacity and fades blend the mask in and out.",
            TextWrapping = TextWrapping.Wrap, Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 4, 0, 0)
        });
    }

    /// <summary>Small preview of a style ("Title" drawn with it), cached.</summary>
    static BitmapSource StyleThumb(TextStyle s, int w = 120)
    {
        var key = w + System.Text.Json.JsonSerializer.Serialize(s);
        if (styleThumbs.TryGetValue(key, out var bmp)) return bmp;
        double k = 210 / Math.Max(1, s.Size);            // draw large so the 1/8-scale thumbnail is readable; scale effects with it
        var big = s.With(x => { x.Size = 210; x.StrokeWidth *= k; x.ShadowDistance *= k; x.ShadowBlur *= k; x.GlowSize *= k; x.BoxPadding *= k; x.BoxRadius *= k; x.Tracking = Math.Min(x.Tracking, 150); x.Align = "Center"; });
        var clip = new Clip { Kind = ClipKind.Text, Text = "Title", Style = big };
        var tv = new TitleVisual(); tv.Update(clip);
        tv.Measure(new Size(Editor.W, Editor.H)); tv.Arrange(new Rect(0, 0, Editor.W, Editor.H));
        var crop = new Canvas { Width = 960, Height = 272, ClipToBounds = true, Background = Brushes.Transparent };
        var host = new Grid { Width = Editor.W, Height = Editor.H };
        host.Children.Add(tv);
        Canvas.SetLeft(host, -160); Canvas.SetTop(host, -224);
        crop.Children.Add(host);
        crop.Measure(new Size(960, 272)); crop.Arrange(new Rect(0, 0, 960, 272)); crop.UpdateLayout();
        int h = (int)Math.Round(w * 272.0 / 960);
        var rtb = new RenderTargetBitmap(w, h, 96.0 * w / 960, 96.0 * w / 960, PixelFormats.Pbgra32);
        rtb.Render(crop); rtb.Freeze();
        styleThumbs[key] = rtb;
        return rtb;
    }

    /// <summary>Tiny text-input dialog.</summary>
    string? Prompt(string title, string label, string initial)
    {
        var tb = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12) };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Style = (Style)FindResource("Primary") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Fg });
        panel.Children.Add(tb); panel.Children.Add(buttons);
        var w = new Window
        {
            Title = title, Owner = this, Width = 340, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = ThemeManager.Brush("SideBar.Bg"), Content = panel
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return w.ShowDialog() == true ? tb.Text.Trim() : null;
    }
}
