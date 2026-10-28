using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>An effect preset (Premiere's Save Preset): chosen attributes of a clip — Lumetri, looks, crop, key, motion, audio effects … with their
/// keyframes — applied to other clips through Paste Attributes' rule (keys move to each clip's own source times).</summary>
public sealed class EffectPreset
{
    public string Name { get; set; } = "";
    public ClipAttr Attrs { get; set; }
    public string Clip { get; set; } = "";                                    // (the source clip's JSON)
    public bool BuiltIn { get; set; }
    public Clip Source() => JsonSerializer.Deserialize<Clip>(Clip)!;
}

public static class EffectPresets
{
    public static string? FileOverride;                                       // (tests)
    static string File_ => FileOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CutPro", "presets.json");
    static List<EffectPreset>? user;

    public static EffectPreset Make(string name, Clip from, ClipAttr attrs) => new() { Name = name, Attrs = attrs, Clip = JsonSerializer.Serialize(from) };

    /// <summary>A few looks to start from.</summary>
    public static readonly EffectPreset[] BuiltIns =
    {
        Built("Black & white", c => c.Grade = new ColorGrade { Saturation = 0, Contrast = 15 }, ClipAttr.Color),
        Built("Warm", c => c.Grade = new ColorGrade { Temperature = 25, Vibrance = 15 }, ClipAttr.Color),
        Built("Cool", c => c.Grade = new ColorGrade { Temperature = -25, Tint = -5 }, ClipAttr.Color),
        Built("Faded film", c => c.Grade = new ColorGrade { Faded = 35, Contrast = -10, Saturation = 80, Vignette = -25 }, ClipAttr.Color),
        Built("Punchy", c => c.Grade = new ColorGrade { Contrast = 25, Vibrance = 30, Whites = 10, Blacks = -10 }, ClipAttr.Color),
        Built("Clean voice", c => { c.Kind = ClipKind.Audio; c.AudioFx = AudioDsp.VoicePreset(); }, ClipAttr.AudioFx),
    };
    static EffectPreset Built(string name, Action<Clip> f, ClipAttr a) { var c = new Clip { Kind = ClipKind.Video }; f(c); var p = Make(name, c, a); p.BuiltIn = true; return p; }

    public static List<EffectPreset> User
    {
        get
        {
            if (user != null) return user;
            try { user = File.Exists(File_) ? JsonSerializer.Deserialize<List<EffectPreset>>(File.ReadAllText(File_)) ?? new() : new(); }
            catch { user = new(); }
            return user;
        }
    }
    public static IEnumerable<EffectPreset> All => User.Concat(BuiltIns);
    public static void Save()
    {
        if (AppSettings.ReadOnly && FileOverride == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, JsonSerializer.Serialize(User, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static void Add(EffectPreset p) { User.RemoveAll(x => x.Name.Equals(p.Name, StringComparison.CurrentCultureIgnoreCase)); User.Insert(0, p); Save(); }
    public static void Remove(EffectPreset p) { User.Remove(p); Save(); }
    public static EffectPreset? Get(string name) => All.FirstOrDefault(p => p.Name == name);
    internal static void Reload() => user = null;
}

public partial class MainWindow
{
    /// <summary>Clip ▸ Save Preset…: the selected clip's attributes (ticked as it has them) under a name.</summary>
    void SavePresetDialog()
    {
        if (ed.Selected is not { } c) { SetTask("Select a clip whose effects you want to keep", 2500); return; }
        var have = Editor.AttrsOf(c);
        if (have == ClipAttr.None) { Alert.Show(this, "This clip has no effects or changes to keep yet.", "Save Preset"); return; }
        var name = new TextBox { Text = (ed.MediaOf(c)?.Name is { } n ? Path.GetFileNameWithoutExtension(n) : "My preset") + " look", Margin = new Thickness(0, 10, 0, 6) };
        var p = new StackPanel(); p.Children.Add(name);
        var boxes = new List<(CheckBox b, ClipAttr a)>();
        foreach (var (a, label) in Editor.AttrNames.Where(x => have.HasFlag(x.a)))
        {
            var cb = new CheckBox { Content = label, IsChecked = a is not (ClipAttr.Motion or ClipAttr.Speed), Margin = new Thickness(0, 2, 0, 2) };
            boxes.Add((cb, a)); p.Children.Add(cb);
        }
        name.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        if (Alert.Show(this, "Name the preset and choose what it keeps. It shows in the Effects panel: double-click it or drag it onto clips.", "Save Preset",
                       AlertKind.Question, new[] { "Save", "Cancel" }, p) != 0) return;
        var attrs = boxes.Where(x => x.b.IsChecked == true).Aggregate(ClipAttr.None, (s, x) => s | x.a);
        if (attrs == ClipAttr.None || name.Text.Trim().Length == 0) return;
        EffectPresets.Add(EffectPresets.Make(name.Text.Trim(), c, attrs));
        BuildEffectsList();
        SetTask($"Preset “{name.Text.Trim()}” saved — Effects panel ▸ Presets", 3000);
    }

    /// <summary>Applies a preset to the selected clips (or the one given). Returns how many changed.</summary>
    int ApplyPreset(EffectPreset p, Clip? onto = null)
    {
        if (onto != null && !ed.IsSel(onto)) ed.Select(onto);
        if (ed.Sel.Count == 0) { SetTask("Select clips first (or drag the preset onto one)", 2500); return 0; }
        int n = ed.PasteAttributes(p.Source(), p.Attrs);
        SetTask(n > 0 ? $"“{p.Name}” on {n} clip{(n == 1 ? "" : "s")}" : $"“{p.Name}” doesn't apply to the selected clips", 2500);
        BuildInspector();
        return n;
    }

    /// <summary>The Effects panel's Presets section (called by BuildEffectsList).</summary>
    void BuildPresetList()
    {
        var head = new TextBlock { Text = "Presets", FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 10, 0, 4) };
        EffectsList.Children.Insert(0, head);
        int at = 1;
        var note = new TextBlock { Text = "Double-click to apply to the selected clips, or drag onto a clip. Clip ▸ Save Preset… adds your own.", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 4) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        EffectsList.Children.Insert(at++, note);
        foreach (var p in EffectPresets.All)
        {
            var row = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3, 6, 3), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent,
                                   ToolTip = string.Join(", ", Editor.AttrNames.Where(x => p.Attrs.HasFlag(x.a)).Select(x => x.name)) };
            var dp = new DockPanel();
            var glyph = new TextBlock { Text = p.Attrs == ClipAttr.AudioFx ? "" : "", FontFamily = (FontFamily)FindResource("IconFont"), Width = 22, VerticalAlignment = VerticalAlignment.Center };
            dp.Children.Add(glyph);
            if (!p.BuiltIn)
            {
                var del = new Button { Content = "", ToolTip = "Delete this preset", FontSize = 9, Width = 20, Height = 18 };
                del.SetResourceReference(StyleProperty, "Icon");
                AutomationProperties_SetName(del, "Delete preset " + p.Name);
                del.Click += (_, e) => { EffectPresets.Remove(p); BuildEffectsList(); e.Handled = true; };
                DockPanel.SetDock(del, Dock.Right); dp.Children.Add(del);
            }
            dp.Children.Add(new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Child = dp;
            row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "B.Hover");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            Point down = default; bool pressed = false;
            row.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { ApplyPreset(p); pressed = false; } else { down = e.GetPosition(null); pressed = true; } };
            row.MouseLeftButtonUp += (_, _) => pressed = false;
            row.MouseMove += (_, e) =>
            {
                if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
                var d = e.GetPosition(null) - down;
                if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                pressed = false;
                DragDrop.DoDragDrop(row, new DataObject("CutPro.Preset", p.Name), DragDropEffects.Copy);
            };
            EffectsList.Children.Insert(at++, row);
        }
    }
    static void AutomationProperties_SetName(DependencyObject d, string n) => System.Windows.Automation.AutomationProperties.SetName(d, n);
}
