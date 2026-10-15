using System.Text.Json;

namespace CutPro;

public enum TextRole { Title, Subtitle, LowerThird, Caption }

/// <summary>Track matte behaviour of a title: Reveal = layers below show only inside the letters; CutOut = letters are punched out.</summary>
public enum MaskMode { None, Reveal, CutOut }

/// <summary>Everything about how a title looks (not its words, timing or placement). Styles and themes copy these.</summary>
public class TextStyle
{
    // font
    public string Font { get; set; } = "Segoe UI";
    public double Size { get; set; } = 90;
    public int Weight { get; set; } = 700;               // OpenType weight 100..900
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool AllCaps { get; set; }
    public double Tracking { get; set; }                 // letter spacing in 1/1000 em (Premiere's unit)
    public double Leading { get; set; } = 115;           // line height, % of font size
    public string Align { get; set; } = "Center";        // Left | Center | Right
    // fill
    public string Fill { get; set; } = "#FFFFFFFF";
    public bool Gradient { get; set; }
    public string Fill2 { get; set; } = "#FF4A7CFF";
    public double GradientAngle { get; set; } = 90;      // 0 = left→right, 90 = top→bottom
    // outline
    public bool Stroke { get; set; }
    public string StrokeColor { get; set; } = "#FF000000";
    public double StrokeWidth { get; set; } = 4;
    // drop shadow
    public bool Shadow { get; set; } = true;
    public string ShadowColor { get; set; } = "#FF000000";
    public double ShadowOpacity { get; set; } = 60;
    public double ShadowAngle { get; set; } = 135;       // 135 = down-right
    public double ShadowDistance { get; set; } = 4;
    public double ShadowBlur { get; set; } = 8;
    // outer glow
    public bool Glow { get; set; }
    public string GlowColor { get; set; } = "#FF4AE3FF";
    public double GlowSize { get; set; } = 20;
    public double GlowOpacity { get; set; } = 80;
    // background box
    public bool Box { get; set; }
    public string BoxColor { get; set; } = "#FF000000";
    public double BoxOpacity { get; set; } = 60;
    public double BoxPadding { get; set; } = 20;
    public double BoxRadius { get; set; } = 8;

    public TextStyle Clone() => (TextStyle)MemberwiseClone();
    public TextStyle With(Action<TextStyle> change) { var c = Clone(); change(c); return c; }
}

public class NamedStyle
{
    public string Name { get; set; } = "";
    public TextStyle Style { get; set; } = new();
    public bool BuiltIn { get; set; }
}

/// <summary>Built-in and user-saved text styles. User styles live in %APPDATA%\CutPro\styles.json (shared by all projects).</summary>
public static class StyleLibrary
{
    static readonly TextStyle Base = new();

    public static readonly List<NamedStyle> BuiltIn = new()
    {
        B("Clean White", Base),
        B("Bold Impact", new TextStyle { Font = "Impact", Size = 110, Weight = 400, Fill = "#FFFFE14A", Stroke = true, StrokeWidth = 6, ShadowDistance = 6, ShadowBlur = 4, ShadowOpacity = 70 }),
        B("Cinematic", new TextStyle { Font = "Georgia", Size = 76, Weight = 400, AllCaps = true, Tracking = 300, Fill = "#FFF5E6C8", ShadowBlur = 14, ShadowDistance = 2, ShadowOpacity = 70 }),
        B("Neon Glow", new TextStyle { Size = 96, Weight = 600, Fill = "#FFFFFFFF", Shadow = false, Glow = true, GlowColor = "#FF2EF2FF", GlowSize = 26, GlowOpacity = 95 }),
        B("Neon Pink", new TextStyle { Font = "Bahnschrift", Size = 96, Weight = 600, Fill = "#FFFFE3F6", Shadow = false, Glow = true, GlowColor = "#FFFF3DB8", GlowSize = 26, GlowOpacity = 95 }),
        B("Subtitle", new TextStyle { Size = 44, Weight = 600, Shadow = false, Box = true, BoxOpacity = 55, BoxPadding = 14, BoxRadius = 6 }),
        B("Lower Third", new TextStyle { Size = 52, Weight = 700, Align = "Left", Leading = 125, Shadow = false, Box = true, BoxColor = "#FF1E5AFF", BoxOpacity = 92, BoxPadding = 18, BoxRadius = 4 }),
        B("News Flash", new TextStyle { Font = "Bahnschrift", Size = 56, Weight = 700, AllCaps = true, Fill = "#FF111111", Shadow = false, Box = true, BoxColor = "#FFFFD400", BoxOpacity = 100, BoxPadding = 14, BoxRadius = 0 }),
        B("Outline", new TextStyle { Font = "Arial Black", Size = 110, Weight = 900, Fill = "#00FFFFFF", Stroke = true, StrokeColor = "#FFFFFFFF", StrokeWidth = 3, Shadow = false }),
        B("Retro Sunset", new TextStyle { Size = 120, Weight = 900, Italic = true, Gradient = true, Fill = "#FFFFD34A", Fill2 = "#FFFF3D7F", Stroke = true, StrokeColor = "#FF3A0CA3", StrokeWidth = 5,
                                          ShadowColor = "#FF3A0CA3", ShadowOpacity = 100, ShadowDistance = 9, ShadowBlur = 0 }),
        B("Handwritten", new TextStyle { Font = "Segoe Script", Size = 80, Weight = 400, ShadowBlur = 10, ShadowOpacity = 50 }),
        B("Typewriter", new TextStyle { Font = "Courier New", Size = 60, Weight = 400, Fill = "#FF111111", Shadow = false, Box = true, BoxColor = "#FFF7F3E8", BoxOpacity = 95, BoxPadding = 16, BoxRadius = 2 }),
        B("Minimal Thin", new TextStyle { Size = 96, Weight = 300, Tracking = 120, Shadow = false }),
        B("Gold Luxe", new TextStyle { Font = "Georgia", Size = 92, Weight = 700, Gradient = true, Fill = "#FFFFF1B8", Fill2 = "#FFB8860B", ShadowBlur = 12, ShadowOpacity = 55 }),
        B("Comic Pop", new TextStyle { Font = "Comic Sans MS", Size = 90, Weight = 700, Fill = "#FFFFFFFF", Stroke = true, StrokeWidth = 7, ShadowDistance = 7, ShadowBlur = 0, ShadowOpacity = 100 }),
        B("Glass Caption", new TextStyle { Size = 40, Weight = 400, ShadowBlur = 6, ShadowOpacity = 40, ShadowDistance = 1, Box = true, BoxColor = "#FFFFFFFF", BoxOpacity = 18, BoxPadding = 16, BoxRadius = 14 }),
    };

    static NamedStyle B(string name, TextStyle s) => new() { Name = name, Style = s, BuiltIn = true };
    public static TextStyle Get(string name) => All.FirstOrDefault(s => s.Name == name)?.Style ?? Base;

    static string UserFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CutPro", "styles.json");
    static List<NamedStyle>? user;
    public static List<NamedStyle> User
    {
        get
        {
            if (user != null) return user;
            try { user = File.Exists(UserFile) ? JsonSerializer.Deserialize<List<NamedStyle>>(File.ReadAllText(UserFile)) ?? new() : new(); }
            catch { user = new(); }
            return user;
        }
    }
    public static IEnumerable<NamedStyle> All => BuiltIn.Concat(User);

    public static void SaveUser(string name, TextStyle s)
    {
        User.RemoveAll(x => x.Name == name);
        User.Add(new NamedStyle { Name = name, Style = s.Clone() });
        Persist();
    }
    public static void DeleteUser(string name) { User.RemoveAll(x => x.Name == name); Persist(); }
    static void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserFile)!);
        File.WriteAllText(UserFile, JsonSerializer.Serialize(User, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>A theme is a coordinated look for every kind of title in a project.</summary>
public class TextTheme
{
    public string Name = "";
    public TextStyle Title = new(), Subtitle = new(), LowerThird = new(), Caption = new();
    public TextStyle For(TextRole r) => r switch { TextRole.Subtitle => Subtitle, TextRole.LowerThird => LowerThird, TextRole.Caption => Caption, _ => Title };
}

public static class Themes
{
    static TextStyle S(string n) => StyleLibrary.Get(n);

    public static readonly List<TextTheme> All = new()
    {
        new() { Name = "Modern", Title = S("Clean White"), Subtitle = S("Clean White").With(s => { s.Size = 50; s.Weight = 400; }),
                LowerThird = S("Lower Third"), Caption = S("Subtitle") },
        new() { Name = "Cinematic", Title = S("Cinematic"), Subtitle = S("Cinematic").With(s => { s.Size = 40; s.AllCaps = false; s.Italic = true; s.Tracking = 40; }),
                LowerThird = S("Cinematic").With(s => { s.Size = 40; s.Align = "Left"; s.Tracking = 150; s.Shadow = false; s.Box = true; s.BoxOpacity = 55; s.BoxPadding = 18; s.BoxRadius = 0; }),
                Caption = S("Subtitle").With(s => { s.Font = "Georgia"; s.Weight = 400; s.Fill = "#FFF5E6C8"; s.BoxOpacity = 45; }) },
        new() { Name = "Neon Night", Title = S("Neon Glow"), Subtitle = S("Neon Pink").With(s => { s.Size = 52; s.GlowSize = 16; }),
                LowerThird = S("Lower Third").With(s => { s.BoxColor = "#FF14002A"; s.BoxOpacity = 88; s.Glow = true; s.GlowColor = "#FF2EF2FF"; s.GlowSize = 12; }),
                Caption = S("Subtitle").With(s => { s.BoxColor = "#FF14002A"; s.BoxOpacity = 75; s.Fill = "#FFE9FDFF"; }) },
        new() { Name = "Newsroom", Title = S("News Flash").With(s => { s.Size = 84; s.Fill = "#FFFFFFFF"; s.BoxColor = "#FFC8102E"; }),
                Subtitle = S("Clean White").With(s => { s.Font = "Bahnschrift"; s.Size = 48; s.Weight = 400; }),
                LowerThird = S("News Flash").With(s => { s.Align = "Left"; s.AllCaps = false; s.Size = 46; s.Leading = 125; }),
                Caption = S("Subtitle").With(s => { s.Font = "Bahnschrift"; s.BoxColor = "#FF0B1F3A"; s.BoxOpacity = 85; s.BoxRadius = 0; }) },
        new() { Name = "Retro 80s", Title = S("Retro Sunset"), Subtitle = S("Neon Pink").With(s => { s.Size = 50; s.Italic = true; }),
                LowerThird = S("Lower Third").With(s => { s.Gradient = true; s.Fill = "#FFFFD34A"; s.Fill2 = "#FFFF3D7F"; s.BoxColor = "#FF2B0B4F"; s.Italic = true; }),
                Caption = S("Subtitle").With(s => { s.BoxColor = "#FF2B0B4F"; s.BoxOpacity = 80; s.Fill = "#FFFFD34A"; }) },
        new() { Name = "Minimal", Title = S("Minimal Thin"), Subtitle = S("Minimal Thin").With(s => { s.Size = 44; s.Tracking = 200; s.AllCaps = true; }),
                LowerThird = S("Minimal Thin").With(s => { s.Size = 44; s.Align = "Left"; s.Tracking = 60; s.Leading = 130; }),
                Caption = S("Glass Caption") },
        new() { Name = "Playful", Title = S("Comic Pop").With(s => { s.Fill = "#FFFFE14A"; }), Subtitle = S("Comic Pop").With(s => { s.Size = 50; s.StrokeWidth = 5; s.ShadowDistance = 4; }),
                LowerThird = S("Lower Third").With(s => { s.Font = "Comic Sans MS"; s.BoxColor = "#FFFF5A8A"; s.BoxRadius = 18; }),
                Caption = S("Subtitle").With(s => { s.Font = "Comic Sans MS"; s.BoxColor = "#FF2F9E72"; s.BoxOpacity = 90; s.BoxRadius = 18; }) },
    };

    public static TextTheme Get(string? name) => All.FirstOrDefault(t => t.Name == name) ?? All[0];

    public static string DefaultText(TextRole r) => r switch
    {
        TextRole.Subtitle => "Subtitle",
        TextRole.LowerThird => "Name Surname\nTitle or role",
        TextRole.Caption => "Caption text goes here",
        _ => "Your Title",
    };

    /// <summary>A title still showing its template text (not written by the user) — content analysis ignores it.</summary>
    public static bool IsPlaceholder(string text) =>
        Enum.GetValues<TextRole>().Any(r => string.Equals(DefaultText(r).Replace("\r", ""), text.Replace("\r", "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Default placement (X/Y in % of the frame from the centre; left-aligned text anchors at its left edge).</summary>
    public static (double x, double y) DefaultPosition(TextRole r) => r switch
    {
        TextRole.Subtitle => (0, 14),
        TextRole.LowerThird => (-42, 30),
        TextRole.Caption => (0, 38),
        _ => (0, 0),
    };
}
