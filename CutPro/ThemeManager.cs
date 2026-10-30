using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CutPro;

/// <summary>An editor theme: resource key → colour ("#AARRGGBB"), gradient "c1>c2>c3" ("v:" vertical, "r:" radial), or a
/// material "tex:name" (tiled) / "img:name" (stretched) from <see cref="Materials"/>. Material themes also shape the panels
/// (radius, gap, edge, shadow) and give individual panels their own text colours (Light = light surface → dark text).</summary>
public sealed class EditorTheme
{
    public string Name = "";
    public bool Dark;
    public Dictionary<string, string> P = new();
    public string Description = "";
    public double Radius, Gap, Edge, Shadow;
    /// <summary>Card regions with a light surface (they get dark text); the rest use the theme's main (light) text.</summary>
    public HashSet<string> LightCards = new();
    /// <summary>Text colours for light surfaces in this theme (dark ink).</summary>
    public Dictionary<string, string>? OnLight;
    public bool Material => Radius > 0 || Gap > 0;
}

/// <summary>
/// Editor (UI) themes. Every colour in the UI is a DynamicResource "B.&lt;key&gt;" (brush) / "C.&lt;key&gt;" (colour), so
/// <see cref="Apply"/> repaints the whole window live; custom-drawn controls (timeline) listen to <see cref="Changed"/>.
/// </summary>
public static class ThemeManager
{
    static Dictionary<string, string> Merge(Dictionary<string, string> b, Dictionary<string, string> over)
    { var d = new Dictionary<string, string>(b); foreach (var (k, v) in over) d[k] = v; return d; }

    // VS Code "Dark Modern"-like
    static readonly Dictionary<string, string> DarkP = new()
    {
        ["Win.Bg"] = "#1F1F1F", ["TitleBar.Bg"] = "#181818", ["TitleBar.Fg"] = "#CCCCCC",
        ["ActivityBar.Bg"] = "#181818", ["ActivityBar.Fg"] = "#868686", ["ActivityBar.Active"] = "#E7E7E7", ["ActivityBar.Indicator"] = "#0078D4",
        ["SideBar.Bg"] = "#181818", ["Header.Fg"] = "#BBBBBB", ["Editor.Bg"] = "#1F1F1F", ["Tab.Active"] = "#1F1F1F", ["Tab.Border"] = "#0078D4",
        ["Panel.Bg"] = "#181818", ["Border"] = "#2B2B2B", ["Fg"] = "#CCCCCC", ["Fg.Dim"] = "#9D9D9D",
        ["Accent"] = "#0078D4", ["Accent.Fg"] = "#FFFFFF", ["Accent2"] = "#4DAAFC",
        ["Hover"] = "#1AFFFFFF", ["Selected"] = "#04395E", ["Input.Bg"] = "#313131", ["Input.Border"] = "#3C3C3C",
        ["Button.Bg"] = "#313131", ["Button.Fg"] = "#CCCCCC", ["Button.Border"] = "#3C3C3C",
        ["Popup.Bg"] = "#1F1F1F", ["Popup.Border"] = "#454545", ["Menu.Selected"] = "#0078D4", ["Menu.Selected.Fg"] = "#FFFFFF",
        ["Scroll.Thumb"] = "#66797979", ["Scroll.ThumbHover"] = "#B3646464", ["Slider.Track"] = "#3C3C3C", ["Slider.Thumb"] = "#CCCCCC",
        ["StatusBar.Bg"] = "#0078D4", ["StatusBar.Fg"] = "#FFFFFF", ["StatusBar.Hover"] = "#26FFFFFF",
        ["Monitor.Bg"] = "#000000", ["Timecode"] = "#4DAAFC", ["Thumb.Bg"] = "#111111", ["Shadow"] = "#000000",
        ["Tl.Bg"] = "#1A1A1A", ["Tl.LaneV"] = "#1E2227", ["Tl.LaneA"] = "#1C221F", ["Tl.RowLine"] = "#111111", ["Tl.Ruler"] = "#202020",
        ["Tl.RulerText"] = "#8B8B8B", ["Tl.Tick"] = "#4A4A4A", ["Tl.Header"] = "#181818", ["Tl.HeaderText"] = "#CCCCCC",
        ["Tl.Btn"] = "#313131", ["Tl.BtnText"] = "#CCCCCC", ["Tl.BtnOff"] = "#8B2E2E",
        ["Clip.Video"] = "#3B6FB0", ["Clip.Audio"] = "#2F9E72", ["Clip.Image"] = "#B07A2F", ["Clip.Text"] = "#A5559C", ["Clip.Nest"] = "#8C7A2B", ["Clip.Adjust"] = "#5A6B7E", ["Clip.Band"] = "#F2D23C",
        ["Clip.Wave"] = "#B3E6FFF2", ["Tl.Solo"] = "#E2B93D", ["Tl.SoloText"] = "#1A1A1A",
        ["Meter.Bg"] = "#0C0C0C", ["Meter.Low"] = "#2FC46B", ["Meter.Mid"] = "#E2C43A", ["Meter.High"] = "#F14C4C", ["Meter.Scale"] = "#8B8B8B",
        ["Playhead"] = "#F14C4C", ["Snap"] = "#FFD84A",
        ["Glass.Tint"] = "#B81F1F1F", ["Glass.Footer"] = "#33000000", ["Glass.Edge"] = "v:#47FFFFFF>#0FFFFFFF", ["Glass.Sheen"] = "v:#17FFFFFF>#00FFFFFF", ["Scrim"] = "#59000000", ["Warn"] = "#E2B93D", ["Error"] = "#F14C4C",
    };

    // VS Code "Light Modern"-like
    static readonly Dictionary<string, string> LightP = Merge(DarkP, new()
    {
        ["Win.Bg"] = "#FFFFFF", ["TitleBar.Bg"] = "#F8F8F8", ["TitleBar.Fg"] = "#1E1E1E",
        ["ActivityBar.Bg"] = "#F8F8F8", ["ActivityBar.Fg"] = "#616161", ["ActivityBar.Active"] = "#1F1F1F", ["ActivityBar.Indicator"] = "#005FB8",
        ["SideBar.Bg"] = "#F8F8F8", ["Header.Fg"] = "#3B3B3B", ["Editor.Bg"] = "#FFFFFF", ["Tab.Active"] = "#FFFFFF", ["Tab.Border"] = "#005FB8",
        ["Panel.Bg"] = "#F8F8F8", ["Border"] = "#E5E5E5", ["Fg"] = "#3B3B3B", ["Fg.Dim"] = "#6E6E6E",
        ["Accent"] = "#005FB8", ["Accent.Fg"] = "#FFFFFF", ["Accent2"] = "#0078D4",
        ["Hover"] = "#12000000", ["Selected"] = "#CCE4F7", ["Input.Bg"] = "#FFFFFF", ["Input.Border"] = "#CECECE",
        ["Button.Bg"] = "#F3F3F3", ["Button.Fg"] = "#3B3B3B", ["Button.Border"] = "#D4D4D4",
        ["Popup.Bg"] = "#FFFFFF", ["Popup.Border"] = "#CECECE", ["Menu.Selected"] = "#005FB8", ["Menu.Selected.Fg"] = "#FFFFFF",
        ["Scroll.Thumb"] = "#66646464", ["Scroll.ThumbHover"] = "#B3646464", ["Slider.Track"] = "#D4D4D4", ["Slider.Thumb"] = "#FFFFFF",
        ["StatusBar.Bg"] = "#005FB8", ["StatusBar.Fg"] = "#FFFFFF", ["StatusBar.Hover"] = "#26FFFFFF",
        ["Monitor.Bg"] = "#000000", ["Timecode"] = "#005FB8", ["Thumb.Bg"] = "#E5E5E5",
        ["Tl.Bg"] = "#FFFFFF", ["Tl.LaneV"] = "#F1F5FB", ["Tl.LaneA"] = "#F0F8F3", ["Tl.RowLine"] = "#E1E1E1", ["Tl.Ruler"] = "#F3F3F3",
        ["Tl.RulerText"] = "#6E6E6E", ["Tl.Tick"] = "#C4C4C4", ["Tl.Header"] = "#F8F8F8", ["Tl.HeaderText"] = "#3B3B3B",
        ["Tl.Btn"] = "#E5E5E5", ["Tl.BtnText"] = "#3B3B3B", ["Tl.BtnOff"] = "#D13438",
        ["Clip.Video"] = "#4A86D8", ["Clip.Audio"] = "#2FA774", ["Clip.Image"] = "#D08A2E", ["Clip.Text"] = "#B55CB0", ["Clip.Nest"] = "#A8912E", ["Clip.Adjust"] = "#7F93A8", ["Clip.Band"] = "#E0B000",
        ["Playhead"] = "#E51400", ["Snap"] = "#D4A017",
        ["Glass.Tint"] = "#C7FBFBFB", ["Glass.Footer"] = "#0D000000", ["Glass.Edge"] = "v:#FFFFFFFF>#26000000", ["Glass.Sheen"] = "v:#80FFFFFF>#00FFFFFF", ["Scrim"] = "#29000000", ["Warn"] = "#B58407", ["Error"] = "#D13438",
    });

    // vivid synthwave: deep violet, pink → purple → cyan gradients
    static readonly Dictionary<string, string> ColorfulP = Merge(DarkP, new()
    {
        ["Win.Bg"] = "#221A40", ["TitleBar.Bg"] = "#1A1333>#261A4A", ["TitleBar.Fg"] = "#EDE7FF",
        ["ActivityBar.Bg"] = "#140F29", ["ActivityBar.Fg"] = "#8D80C4", ["ActivityBar.Active"] = "#FFFFFF", ["ActivityBar.Indicator"] = "v:#FF4FB8>#00E5FF",
        ["SideBar.Bg"] = "#1B1535", ["Header.Fg"] = "#C9BDF5", ["Editor.Bg"] = "#221A40", ["Tab.Active"] = "#221A40", ["Tab.Border"] = "#FF4FB8>#9B5CFF>#00E5FF",
        ["Panel.Bg"] = "#1B1535", ["Border"] = "#342A5C", ["Fg"] = "#EDE7FF", ["Fg.Dim"] = "#A79BD1",
        ["Accent"] = "#FF4FB8", ["Accent.Fg"] = "#FFFFFF", ["Accent2"] = "#00E5FF",
        ["Hover"] = "#1FFFFFFF", ["Selected"] = "#4B2A7A", ["Input.Bg"] = "#140F29", ["Input.Border"] = "#3D3170",
        ["Button.Bg"] = "#2B2250", ["Button.Fg"] = "#EDE7FF", ["Button.Border"] = "#3D3170",
        ["Popup.Bg"] = "#221A40", ["Popup.Border"] = "#4B3D85", ["Menu.Selected"] = "#FF4FB8>#9B5CFF", ["Menu.Selected.Fg"] = "#FFFFFF",
        ["Scroll.Thumb"] = "#669B5CFF", ["Scroll.ThumbHover"] = "#B39B5CFF", ["Slider.Track"] = "#3D3170", ["Slider.Thumb"] = "#FFFFFF",
        ["StatusBar.Bg"] = "#FF4FB8>#9B5CFF>#00C2FF", ["StatusBar.Fg"] = "#FFFFFF", ["StatusBar.Hover"] = "#33FFFFFF",
        ["Monitor.Bg"] = "#000000", ["Timecode"] = "#00E5FF", ["Thumb.Bg"] = "#140F29",
        ["Tl.Bg"] = "#16112B", ["Tl.LaneV"] = "#1F1840", ["Tl.LaneA"] = "#152236", ["Tl.RowLine"] = "#0E0A1E", ["Tl.Ruler"] = "#1B1535",
        ["Tl.RulerText"] = "#A79BD1", ["Tl.Tick"] = "#4B3D85", ["Tl.Header"] = "#1B1535", ["Tl.HeaderText"] = "#EDE7FF",
        ["Tl.Btn"] = "#2E2555", ["Tl.BtnText"] = "#EDE7FF", ["Tl.BtnOff"] = "#C2185B",
        ["Clip.Video"] = "#7C4DFF", ["Clip.Audio"] = "#00BFA5", ["Clip.Image"] = "#FF9F1C", ["Clip.Text"] = "#FF4FB8", ["Clip.Nest"] = "#E0B400", ["Clip.Adjust"] = "#9AA7B8", ["Clip.Band"] = "#FFE14D",
        ["Playhead"] = "#00E5FF", ["Snap"] = "#FFE14A",
        ["Glass.Tint"] = "#B0221A40", ["Glass.Footer"] = "#40140F29", ["Glass.Edge"] = "v:#80FF4FB8>#4D00E5FF", ["Glass.Sheen"] = "v:#1FFFFFFF>#00FFFFFF", ["Scrim"] = "#660B0718", ["Warn"] = "#FFE14A", ["Error"] = "#FF5C8A",
    });

    /// <summary>Flat themes: each panel's surface is its own background colour, no card edges, divider lines between panels.</summary>
    static Dictionary<string, string> Flat(Dictionary<string, string> p) => Merge(p, new()
    {
        ["Surf.Win"] = p["Win.Bg"], ["Surf.Title"] = p["TitleBar.Bg"], ["Surf.Activity"] = p["ActivityBar.Bg"], ["Surf.Side"] = p["SideBar.Bg"],
        ["Surf.Editor"] = p["Editor.Bg"], ["Surf.Panel"] = p["Panel.Bg"], ["Surf.Insp"] = p["SideBar.Bg"], ["Surf.Status"] = p["StatusBar.Bg"],
        ["Card.Edge"] = "#00000000", ["Card.Sheen"] = "#00000000", ["Divider"] = p["Border"], ["Strip"] = p["SideBar.Bg"], ["Heading"] = p["Accent"],
    });

    /// <summary>Shared by the material themes: no divider lines (the gaps separate the panels), translucent strips and timeline
    /// lanes so the surface shows through, light text on dark surfaces.</summary>
    static Dictionary<string, string> MaterialBase(string accent, string accent2) => Merge(DarkP, new()
    {
        ["Divider"] = "#00000000", ["Strip"] = "#1A000000", ["Tab.Active"] = "#14FFFFFF", ["Tab.Border"] = accent2, ["Heading"] = accent2,
        ["Fg"] = "#F1F2F4", ["Fg.Dim"] = "#B4B8C0", ["Header.Fg"] = "#DADDE3", ["TitleBar.Fg"] = "#F1F2F4",
        ["ActivityBar.Fg"] = "#B4B8C0", ["ActivityBar.Active"] = "#FFFFFF", ["ActivityBar.Indicator"] = accent2,
        ["Accent"] = accent, ["Accent.Fg"] = "#FFFFFF", ["Accent2"] = accent2,
        ["Hover"] = "#1FFFFFFF", ["Selected"] = "#59" + accent[1..], ["Input.Bg"] = "#59000000", ["Input.Border"] = "#33FFFFFF",
        ["Button.Bg"] = "#26FFFFFF", ["Button.Fg"] = "#F1F2F4", ["Button.Border"] = "#33FFFFFF",
        ["Scroll.Thumb"] = "#59FFFFFF", ["Scroll.ThumbHover"] = "#99FFFFFF", ["Slider.Track"] = "#40FFFFFF", ["Slider.Thumb"] = "#FFFFFF",
        ["StatusBar.Fg"] = "#F1F2F4", ["StatusBar.Hover"] = "#26FFFFFF", ["Thumb.Bg"] = "#40000000", ["Timecode"] = accent2,
        ["Menu.Selected"] = accent, ["Menu.Selected.Fg"] = "#FFFFFF",
        ["Tl.Bg"] = "#00000000", ["Tl.LaneV"] = "#1A000000", ["Tl.LaneA"] = "#1A000000", ["Tl.RowLine"] = "#26000000", ["Tl.Ruler"] = "#26000000",
        ["Tl.RulerText"] = "#C9CDD4", ["Tl.Tick"] = "#40FFFFFF", ["Tl.Header"] = "#1F000000", ["Tl.HeaderText"] = "#F1F2F4",
        ["Tl.Btn"] = "#33FFFFFF", ["Tl.BtnText"] = "#F1F2F4", ["Tl.BtnOff"] = "#C62828",
    });

    /// <summary>Dark ink for light surfaces (marble, silver, sand …): applied to those cards as local resources.</summary>
    static Dictionary<string, string> Ink(string fg, string dim, string accent) => new()
    {
        ["Fg"] = fg, ["Fg.Dim"] = dim, ["Header.Fg"] = fg, ["TitleBar.Fg"] = fg, ["StatusBar.Fg"] = fg, ["Button.Fg"] = fg, ["Heading"] = accent, ["Timecode"] = accent,
        ["ActivityBar.Fg"] = dim, ["ActivityBar.Active"] = fg, ["Hover"] = "#1A000000", ["Selected"] = "#47" + accent[1..],
        ["Input.Bg"] = "#B8FFFFFF", ["Input.Border"] = "#40000000", ["Button.Bg"] = "#66FFFFFF", ["Button.Border"] = "#38000000",
        ["Thumb.Bg"] = "#26000000", ["Slider.Track"] = "#40000000", ["Slider.Thumb"] = "#FFFFFF",
        ["Scroll.Thumb"] = "#55000000", ["Scroll.ThumbHover"] = "#8C000000", ["Strip"] = "#14000000", ["Tab.Active"] = "#33FFFFFF",
        ["StatusBar.Hover"] = "#1F000000", ["Border"] = "#33000000",
        ["Tl.RulerText"] = dim, ["Tl.HeaderText"] = fg, ["Tl.BtnText"] = fg, ["Tl.Btn"] = "#26000000", ["Tl.Tick"] = "#4D000000",
    };

    // ---- Frosted Glass: Apple-style light glass panes over a pale iPhone sky wallpaper ----
    static readonly Dictionary<string, string> GlassP = Merge(Merge(LightP, new()
    {
        ["Divider"] = "#00000000", ["Strip"] = "#14FFFFFF", ["Heading"] = "#007AFF",
        ["Tab.Active"] = "#33FFFFFF", ["Tab.Border"] = "#007AFF",
        ["Hover"] = "#14000000", ["Selected"] = "#29007AFF",
        ["Input.Bg"] = "#B8FFFFFF", ["Input.Border"] = "#2E000000",
        ["Button.Bg"] = "#73FFFFFF", ["Button.Fg"] = "#1C1C1E", ["Button.Border"] = "#28000000",
        ["Popup.Bg"] = "#F2F5F5F7", ["Popup.Border"] = "#33000000",
        ["Tl.Bg"] = "#00000000", ["Tl.LaneV"] = "#14007AFF", ["Tl.LaneA"] = "#1434C759",
        ["Tl.RowLine"] = "#14000000", ["Tl.Ruler"] = "#18000000", ["Tl.Header"] = "#12000000",
        ["Tl.RulerText"] = "#636366", ["Tl.Tick"] = "#4D000000", ["Tl.HeaderText"] = "#1C1C1E",
        ["Tl.Btn"] = "#26FFFFFF", ["Tl.BtnText"] = "#1C1C1E",
        ["Scroll.Thumb"] = "#55000000", ["Scroll.ThumbHover"] = "#8C000000",
        ["Slider.Track"] = "#40000000", ["Slider.Thumb"] = "#FFFFFF",
        ["Thumb.Bg"] = "#26000000", ["Menu.Selected"] = "#007AFF", ["Menu.Selected.Fg"] = "#FFFFFF",
    }), new()
    {
        ["Win.Bg"] = "#C5D4E8", ["Surf.Win"] = "img:sky",
        ["Surf.Title"] = "tex:frost2", ["Surf.Activity"] = "tex:frost", ["Surf.Side"] = "tex:frost", ["Surf.Editor"] = "tex:frost",
        ["Surf.Panel"] = "tex:frost", ["Surf.Insp"] = "tex:frost", ["Surf.Status"] = "tex:frost2",
        ["Card.Edge"] = "v:#E8FFFFFF>#48FFFFFF>#1AFFFFFF", ["Card.Sheen"] = "v:#66FFFFFF>#10FFFFFF>#00FFFFFF>#12FFFFFF",
        ["Fg"] = "#1C1C1E", ["Fg.Dim"] = "#636366", ["Header.Fg"] = "#1C1C1E", ["TitleBar.Fg"] = "#1C1C1E",
        ["ActivityBar.Fg"] = "#636366", ["ActivityBar.Active"] = "#1C1C1E", ["ActivityBar.Indicator"] = "#007AFF",
        ["Accent"] = "#007AFF", ["Accent.Fg"] = "#FFFFFF", ["Accent2"] = "#5AC8FA",
        ["StatusBar.Bg"] = "#007AFF", ["StatusBar.Fg"] = "#FFFFFF", ["Timecode"] = "#007AFF",
        ["Glass.Tint"] = "#C8F5F5F7", ["Glass.Footer"] = "#0D000000", ["Glass.Edge"] = "v:#F2FFFFFF>#40FFFFFF", ["Glass.Sheen"] = "v:#99FFFFFF>#00FFFFFF", ["Scrim"] = "#33000000",
    });

    // ---- Stone: obsidian, sapphire, white marble, graphite, slate, rose quartz, lapis lazuli ----
    static readonly Dictionary<string, string> StoneP = Merge(MaterialBase("#2F6FE8", "#86AEFF"), new()
    {
        ["Win.Bg"] = "#111215", ["Surf.Win"] = "tex:basalt",
        ["Surf.Title"] = "tex:obsidian", ["Surf.Activity"] = "tex:sapphire", ["Surf.Side"] = "tex:marble", ["Surf.Editor"] = "tex:graphite",
        ["Surf.Panel"] = "tex:slate", ["Surf.Insp"] = "tex:rosequartz", ["Surf.Status"] = "tex:lapis",
        ["Card.Edge"] = "v:#59FFFFFF>#14FFFFFF>#4D000000", ["Card.Sheen"] = "v:#17FFFFFF>#00FFFFFF>#00FFFFFF>#14000000",
        ["Popup.Bg"] = "#F223252B", ["Popup.Border"] = "#4DFFFFFF", ["StatusBar.Bg"] = "#2A44A8",
        ["Tl.LaneV"] = "#1A86AEFF", ["Tl.LaneA"] = "#1486FFC0", ["Tl.Ruler"] = "#33000000",
        ["Glass.Tint"] = "#CC1E2026", ["Glass.Footer"] = "#33000000", ["Glass.Edge"] = "v:#66FFFFFF>#1AFFFFFF", ["Glass.Sheen"] = "v:#1AFFFFFF>#00FFFFFF", ["Scrim"] = "#66000000",
    });

    // ---- Metals: gold, bronze, silver, platinum, copper on gunmetal ----
    static readonly Dictionary<string, string> MetalP = Merge(MaterialBase("#A87410", "#F2C94C"), new()
    {
        ["Win.Bg"] = "#1E2024", ["Surf.Win"] = "tex:gunmetal",
        ["Surf.Title"] = "tex:gold", ["Surf.Activity"] = "tex:bronze", ["Surf.Side"] = "tex:silver", ["Surf.Editor"] = "tex:bronze",
        ["Surf.Panel"] = "tex:platinum", ["Surf.Insp"] = "tex:copper", ["Surf.Status"] = "tex:gold",
        ["Card.Edge"] = "v:#D9FFFFFF>#33FFFFFF>#26000000>#73000000", ["Card.Sheen"] = "v:#33FFFFFF>#00FFFFFF>#00000000>#29000000",
        ["Popup.Bg"] = "#F2262A30", ["Popup.Border"] = "#66D9B860", ["StatusBar.Bg"] = "#C4A04A", ["Fg.Dim"] = "#D8CEC0", ["ActivityBar.Fg"] = "#D8CEC0",
        ["Tl.LaneV"] = "#1F2B4A7A", ["Tl.LaneA"] = "#1F2B7A4A", ["Tl.RowLine"] = "#26000000", ["Tl.Ruler"] = "#1A000000", ["Tl.Header"] = "#14000000",
        ["Glass.Tint"] = "#D9202328", ["Glass.Footer"] = "#33000000", ["Glass.Edge"] = "v:#B3F2D27A>#33000000", ["Glass.Sheen"] = "v:#26FFFFFF>#00FFFFFF", ["Scrim"] = "#66000000",
    });

    // ---- Nature: moss, tree bark, fallen leaves, rich soil, grass, sand, a stream ----
    static readonly Dictionary<string, string> NatureP = Merge(MaterialBase("#3E8E2A", "#A8E06C"), new()
    {
        ["Win.Bg"] = "#0B130B", ["Surf.Win"] = "tex:forest",
        ["Surf.Title"] = "tex:moss", ["Surf.Activity"] = "tex:bark", ["Surf.Side"] = "tex:leaves", ["Surf.Editor"] = "tex:dirt",
        ["Surf.Panel"] = "tex:grass", ["Surf.Insp"] = "tex:sand", ["Surf.Status"] = "tex:water",
        ["Card.Edge"] = "v:#40FFFFFF>#14FFFFFF>#59000000", ["Card.Sheen"] = "r:#00000000>#00000000>#47000000",
        ["Fg"] = "#F3F5EC", ["Fg.Dim"] = "#BFC8B0", ["Header.Fg"] = "#E2EAD2", ["Input.Bg"] = "#73000000",
        ["Popup.Bg"] = "#F21A2416", ["Popup.Border"] = "#4DA8E06C", ["StatusBar.Bg"] = "#125466",
        ["Tl.Bg"] = "#33000000", ["Tl.LaneV"] = "#26000000", ["Tl.LaneA"] = "#1F001A10", ["Tl.Ruler"] = "#40000000", ["Tl.Header"] = "#33000000",
        ["Tl.RulerText"] = "#D8E6C6", ["Tl.HeaderText"] = "#F3F5EC",
        ["Glass.Tint"] = "#D9172014", ["Glass.Footer"] = "#33000000", ["Glass.Edge"] = "v:#59A8E06C>#1AFFFFFF", ["Glass.Sheen"] = "v:#1AFFFFFF>#00FFFFFF", ["Scrim"] = "#66000000",
    });

    public static readonly EditorTheme[] All =
    {
        new() { Name = "Dark Theme", Dark = true, P = Flat(DarkP), Description = "VS Code dark" },
        new() { Name = "Light Theme", Dark = false, P = Flat(LightP), Description = "VS Code light" },
        new() { Name = "Colorful Theme", Dark = true, P = Flat(ColorfulP), Description = "Synthwave gradients" },
        new() { Name = "Frosted Glass", Dark = false, P = GlassP, Radius = 22, Gap = 12, Edge = 0.85, Shadow = 22,
                LightCards = new() { "Title", "Activity", "Side", "Editor", "Panel", "Insp", "Status" },
                OnLight = Ink("#1C1C1E", "#636366", "#007AFF"),
                Description = "Apple-style frosted glass: translucent panes over a pale iPhone sky" },
        new() { Name = "Stone", Dark = true, P = StoneP, Radius = 12, Gap = 8, Edge = 1.1, Shadow = 14, LightCards = new() { "Side", "Insp" },
                OnLight = Ink("#24211E", "#5C5751", "#2F6FE8"),
                Description = "Photographed stone: obsidian, sapphire, marble, graphite, slate, rose quartz, lapis" },
        new() { Name = "Metals", Dark = true, P = MetalP, Radius = 6, Gap = 7, Edge = 1.6, Shadow = 12, LightCards = new() { "Title", "Side", "Panel", "Status" },
                OnLight = Ink("#1F1A10", "#4F4636", "#A87410"),
                Description = "Brushed gold, bronze, silver, platinum and copper with anisotropic grain" },
        new() { Name = "Nature", Dark = true, P = NatureP, Radius = 16, Gap = 9, Edge = 1, Shadow = 14, LightCards = new() { "Insp" },
                OnLight = Ink("#2A2114", "#5E5240", "#3E8E2A"),
                Description = "Photographed moss, bark, leaves, soil, grass, sand and water" },
    };

    /// <summary>A card's own palette (local resources) — dark ink on light surfaces; null = the theme's main colours.</summary>
    public static Dictionary<string, string>? CardPalette(EditorTheme th, string region) => th.OnLight != null && th.LightCards.Contains(region) ? th.OnLight : null;
    public const string Default = "Dark Theme";

    public static EditorTheme Current { get; private set; } = All[0];
    /// <summary>Raised after a theme is applied (custom-drawn controls re-read their brushes).</summary>
    public static event Action? Changed;

    public static void Apply(string? name)
    {
        var th = All.FirstOrDefault(t => t.Name == name) ?? All[0];
        Current = th;
        var res = Application.Current.Resources;
        foreach (var (k, v) in th.P)
        {
            res["B." + k] = MakeBrush(v);
            res["C." + k] = FirstColor(v);
        }
        // panel shape: flat themes 0 → the classic edge-to-edge layout
        double g = th.Gap;
        res["Th.Radius"] = th.Radius; res["Th.CardEdge"] = th.Edge; res["Th.Shadow"] = th.Shadow;
        res["Th.CardMargin"] = new Thickness(g / 2); res["Th.RootPad"] = new Thickness(g / 2);
        res["Th.TitleH"] = new GridLength(36 + g); res["Th.StatusH"] = new GridLength(24 + g); res["Th.ActivityW"] = new GridLength(48 + g);
        ThemeArt.Apply(res, th);                                            // themed play button, slider thumbs / tracks, separators
        Changed?.Invoke();
    }

    /// <summary>Applies the current theme's per-card palettes (local resources) — call before redrawing custom-drawn panels.</summary>
    public static void ApplyCard(FrameworkElement card, string region)
    {
        var pal = CardPalette(Current, region);
        card.Resources.Clear();
        if (pal == null) return;
        foreach (var (k, v) in pal) { card.Resources["B." + k] = MakeBrush(v); card.Resources["C." + k] = FirstColor(v); }
    }

    /// <summary>A brush as seen from an element (its card's palette first), for custom-drawn controls.</summary>
    public static Brush Brush(FrameworkElement scope, string key) => scope.TryFindResource("B." + key) as Brush ?? Brush(key);
    public static Color Color(FrameworkElement scope, string key) => scope.TryFindResource("C." + key) is Color c ? c : Color(key);

    public static Brush Brush(string key) => Application.Current?.Resources["B." + key] as Brush ?? MakeBrush(Current.P.GetValueOrDefault(key, "#FF00FF"));
    public static Color Color(string key) => FirstColor(Current.P.GetValueOrDefault(key, "#FF00FF"));
    public static Brush BrushOf(EditorTheme th, string key) => MakeBrush(th.P.GetValueOrDefault(key, "#FF00FF"));

    static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex.Trim());
    static Color FirstColor(string v) => v.StartsWith("tex:") || v.StartsWith("img:") ? Materials.Average(v[4..]) : Parse(v.Replace("v:", "").Replace("r:", "").Split('>')[0]);

    /// <summary>"#hex" → solid; "a>b>c" → horizontal gradient; "v:a>b" → vertical; "r:centre>…>edge" → radial;
    /// "tex:name" / "img:name" → material texture (tiled / stretched).</summary>
    public static Brush MakeBrush(string v)
    {
        if (v.StartsWith("tex:") || v.StartsWith("img:")) return Materials.Brush(v);
        bool vertical = v.StartsWith("v:"), radial = v.StartsWith("r:");
        var parts = v.Replace("v:", "").Replace("r:", "").Split('>');
        Brush b;
        if (parts.Length == 1) b = new SolidColorBrush(Parse(parts[0]));
        else
        {
            GradientBrush g = radial ? new RadialGradientBrush { RadiusX = 0.75, RadiusY = 0.75 }
                                     : new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = vertical ? new Point(0, 1) : new Point(1, 0) };
            for (int i = 0; i < parts.Length; i++) g.GradientStops.Add(new GradientStop(Parse(parts[i]), (double)i / (parts.Length - 1)));
            b = g;
        }
        b.Freeze();
        return b;
    }
}

/// <summary>Persisted UI preferences (%APPDATA%\CutPro\settings.json).</summary>
public class AppSettings
{
    public string Theme { get; set; } = ThemeManager.Default;
    public double SideBarWidth { get; set; } = 280;
    public double InspectorWidth { get; set; } = 320;
    public double TimelineHeight { get; set; } = 300;
    public bool SideBarVisible { get; set; } = true;
    public bool InspectorVisible { get; set; } = true;
    public string SideView { get; set; } = "Project";
    public string DefaultVideoTransition { get; set; } = "Cross Dissolve";
    public string DefaultAudioTransition { get; set; } = "Constant Power";
    public double DefaultTransitionDur { get; set; } = 1.0;
    public bool ThemeEffects { get; set; }
    /// <summary>Where the panels are (DockUi) — null: the Editing workspace with the sizes above.</summary>
    public DockLayout? Layout { get; set; }
    /// <summary>Saved workspaces by name; the last one chosen.</summary>
    public Dictionary<string, DockLayout> Workspaces { get; set; } = new();
    public string Workspace { get; set; } = "Editing";
    /// <summary>Keyboard shortcuts the user changed (command id → gestures; <see cref="Keymap"/>).</summary>
    public Dictionary<string, string[]> Shortcuts { get; set; } = new();
    /// <summary>How the timeline follows the playhead while playing: Page (jumps a page, Premiere's default), Smooth (the playhead stays in the
    /// middle), None.</summary>
    public string PlaybackScroll { get; set; } = "Page";
    /// <summary>Dragging the playhead plays the sound under it (Premiere's audio scrubbing, Shift+S).</summary>
    public bool AudioScrub { get; set; } = true;
    /// <summary>Audio clips without a linked picture trim, move and cut at single samples (Premiere's Show Audio Time Units).</summary>
    public bool AudioUnits { get; set; }
    /// <summary>Project panel: icon or list view, sort order.</summary>
    public bool BinListView { get; set; }
    public string BinSort { get; set; } = "Name";
    /// <summary>Auto-save every N minutes (0 = off), how many versions to keep per project.</summary>
    public int AutoSaveMinutes { get; set; } = 5;
    public int AutoSaveKeep { get; set; } = 20;
    /// <summary>Interface language (<see cref="Loc"/>: "en", "de", "es", "fr").</summary>
    public string Language { get; set; } = "en";
    /// <summary>Larger interface text (accessibility): 1 = normal.</summary>
    public double TextScale { get; set; } = 1;

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CutPro", "settings.json");
    static AppSettings? current;
    public static AppSettings Current
    {
        get
        {
            if (current != null) return current;
            try { current = File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) : null; } catch { }
            return current ??= new AppSettings();
        }
    }
    /// <summary>Automated runs (--uishot, --objui, --playtest) must not change the user's saved settings.</summary>
    public static bool ReadOnly;
    public static void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>Animates a GridLength (panel show/hide) — WPF has no built-in one.</summary>
public class GridLengthAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(nameof(From), typeof(double), typeof(GridLengthAnimation));
    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(nameof(To), typeof(double), typeof(GridLengthAnimation));
    public static readonly DependencyProperty EasingProperty = DependencyProperty.Register(nameof(Easing), typeof(IEasingFunction), typeof(GridLengthAnimation));
    public double From { get => (double)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public double To { get => (double)GetValue(ToProperty); set => SetValue(ToProperty, value); }
    public IEasingFunction? Easing { get => (IEasingFunction?)GetValue(EasingProperty); set => SetValue(EasingProperty, value); }
    public override Type TargetPropertyType => typeof(GridLength);
    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();
    public override object GetCurrentValue(object origin, object destination, AnimationClock clock)
    {
        double p = clock.CurrentProgress ?? 1;
        if (Easing != null) p = Easing.Ease(p);
        return new GridLength(Math.Max(0, From + (To - From) * p));
    }
}
