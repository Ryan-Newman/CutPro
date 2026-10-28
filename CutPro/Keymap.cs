using System.Windows.Input;

namespace CutPro;

/// <summary>A command that can have keyboard shortcuts. Label = "Group: Name" (the command palette's name too). Global ones work while typing
/// in a field (VS Code's shell keys).</summary>
public sealed record KeyCmd(string Id, string Label, string[] Defaults, bool Global = false)
{
    public string Group => Label.Contains(':') ? Label[..Label.IndexOf(':')] : "";
    public string Name => Label.Contains(':') ? Label[(Label.IndexOf(':') + 1)..].Trim() : Label;
}

/// <summary>Keyboard shortcuts: Premiere's defaults, changed by the user in Settings (saved as overrides in <see cref="AppSettings.Shortcuts"/>).
/// Gestures are text like "Ctrl+Shift+K" (modifiers in the order Ctrl, Alt, Shift) — the same text menus and the palette show.</summary>
public static class Keymap
{
    public static readonly KeyCmd[] All =
    {
        new("view.palette", "View: Command Palette…", new[] { "Ctrl+Shift+P", "F1" }, true),
        new("view.sidebar", "View: Toggle Side Bar", new[] { "Ctrl+B" }, true),
        new("view.inspector", "View: Toggle Effect Controls", new[] { "Ctrl+Alt+B" }, true),
        new("view.fullscreen", "View: Full-Screen Playback", new[] { "F11" }, true),
        new("view.maximize", "View: Maximize Panel Under the Mouse", new[] { "`" }),
        new("view.history", "View: Show History", Array.Empty<string>()),
        new("file.import", "File: Import Media…", new[] { "Ctrl+I" }),
        new("file.open", "File: Open Project…", new[] { "Ctrl+O" }),
        new("file.save", "File: Save Project", new[] { "Ctrl+S" }),
        new("file.export", "File: Export MP4…", new[] { "Ctrl+E" }),
        new("file.publish", "File: Publish…", new[] { "Ctrl+Shift+U" }),
        new("file.collect", "File: Collect Files…", Array.Empty<string>()),
        new("seq.new", "Sequence: New Sequence…", new[] { "Ctrl+N" }),
        new("edit.undo", "Edit: Undo", new[] { "Ctrl+Z" }),
        new("edit.redo", "Edit: Redo", new[] { "Ctrl+Y", "Ctrl+Shift+Z" }),
        new("edit.cut", "Edit: Cut", new[] { "Ctrl+X" }),
        new("edit.copy", "Edit: Copy", new[] { "Ctrl+C" }),
        new("edit.paste", "Edit: Paste", new[] { "Ctrl+V" }),
        new("edit.pasteInsert", "Edit: Paste Insert", new[] { "Ctrl+Shift+V" }),
        new("edit.pasteAttr", "Edit: Paste Attributes…", new[] { "Ctrl+Alt+V" }),
        new("edit.removeAttr", "Edit: Remove Attributes…", Array.Empty<string>()),
        new("edit.addEdit", "Edit: Add Edit (cut at playhead)", new[] { "Ctrl+K" }),
        new("edit.addEditAll", "Edit: Add Edit to All Tracks", new[] { "Ctrl+Shift+K" }),
        new("edit.delete", "Edit: Delete", new[] { "Del", "Backspace" }),
        new("edit.rippleDelete", "Edit: Ripple Delete", new[] { "Shift+Del", "Shift+Backspace" }),
        new("edit.selectAll", "Edit: Select All", new[] { "Ctrl+A" }),
        new("edit.deselect", "Edit: Deselect All", new[] { "Esc", "Ctrl+Shift+A" }),
        new("edit.link", "Edit: Link / Unlink Clips", new[] { "Ctrl+L" }),
        new("clip.group", "Clip: Group", new[] { "Ctrl+G" }),
        new("clip.ungroup", "Clip: Ungroup", new[] { "Ctrl+Shift+G" }),
        new("edit.markIn", "Edit: Mark In", new[] { "I" }),
        new("edit.markOut", "Edit: Mark Out", new[] { "O" }),
        new("edit.clearMarks", "Edit: Clear Marks", new[] { "X" }),
        new("edit.lift", "Edit: Lift In–Out", new[] { ";" }),
        new("edit.extract", "Edit: Extract In–Out", new[] { "'" }),
        new("edit.trimPrev", "Edit: Ripple Trim Previous Edit to Playhead", new[] { "Q" }),
        new("edit.trimNext", "Edit: Ripple Trim Next Edit to Playhead", new[] { "W" }),
        new("src.insert", "Source: Insert at the Playhead", new[] { "," }),
        new("src.overwrite", "Source: Overwrite at the Playhead", new[] { "." }),
        new("src.matchFrame", "Source: Match Frame", new[] { "F" }),
        new("clip.reveal", "Clip: Reveal in Project", Array.Empty<string>()),
        new("clip.revealFile", "Clip: Reveal in File Explorer", Array.Empty<string>()),
        new("clip.speed", "Clip: Speed/Duration…", new[] { "Ctrl+R" }),
        new("trans.video", "Sequence: Apply Video Transition", new[] { "Ctrl+D" }),
        new("trans.audio", "Sequence: Apply Audio Transition", new[] { "Ctrl+Shift+D" }),
        new("trans.both", "Sequence: Apply Default Transitions to Selection", new[] { "Shift+D" }),
        new("marker.add", "Marker: Add Marker", new[] { "M" }),
        new("marker.addClip", "Marker: Add Clip Marker", Array.Empty<string>()),
        new("marker.edit", "Marker: Edit Marker at the Playhead…", Array.Empty<string>()),
        new("marker.next", "Marker: Go to Next Marker", new[] { "Shift+M" }),
        new("marker.prev", "Marker: Go to Previous Marker", new[] { "Ctrl+Shift+M" }),
        new("marker.clear", "Marker: Clear All Markers", new[] { "Ctrl+Alt+Shift+M" }),
        new("marker.chapters", "Marker: Copy YouTube Chapters", Array.Empty<string>()),
        new("gap.next", "Sequence: Go to Next Gap in Sequence", new[] { "Shift+;" }),
        new("gap.prev", "Sequence: Go to Previous Gap in Sequence", new[] { "Ctrl+Shift+;" }),
        new("gap.nextTrack", "Sequence: Go to Next Gap in Track", Array.Empty<string>()),
        new("gap.prevTrack", "Sequence: Go to Previous Gap in Track", Array.Empty<string>()),
        new("play.toggle", "Playback: Play / Pause", new[] { "Space" }),
        new("play.stop", "Playback: Stop", new[] { "K" }),
        new("play.forward", "Playback: Play", new[] { "L" }),
        new("play.back", "Playback: Back One Second", new[] { "J" }),
        new("play.prevFrame", "Playback: Previous Frame", new[] { "←" }),
        new("play.nextFrame", "Playback: Next Frame", new[] { "→" }),
        new("play.back1s", "Playback: Back 1 Second", new[] { "Shift+←" }),
        new("play.fwd1s", "Playback: Forward 1 Second", new[] { "Shift+→" }),
        new("play.prevEdit", "Playback: Go to Previous Edit", new[] { "↑" }),
        new("play.nextEdit", "Playback: Go to Next Edit", new[] { "↓" }),
        new("play.start", "Playback: Go to Start", new[] { "Home" }),
        new("play.end", "Playback: Go to End", new[] { "End" }),
        new("tool.select", "Tools: Selection Tool", new[] { "V" }),
        new("tool.razor", "Tools: Razor Tool", new[] { "C" }),
        new("tl.snap", "Timeline: Toggle Snapping", new[] { "S" }),
        new("tl.zoomIn", "Timeline: Zoom In", new[] { "=", "Num +" }),
        new("tl.zoomOut", "Timeline: Zoom Out", new[] { "-", "Num -" }),
        new("tl.zoomSeq", "Timeline: Zoom to Sequence", new[] { "\\" }),
        new("tl.zoomSel", "Timeline: Zoom to Selection", Array.Empty<string>()),
    };
    static readonly Dictionary<string, KeyCmd> byId = All.ToDictionary(c => c.Id);
    static Dictionary<string, string>? byGesture;
    /// <summary>Shortcuts changed (menus, the palette and the Settings list show the new ones).</summary>
    public static event Action? Changed;

    public static KeyCmd? Get(string id) => byId.GetValueOrDefault(id);
    /// <summary>A command's shortcuts (the user's, or the defaults).</summary>
    public static string[] Of(string id) => AppSettings.Current.Shortcuts.TryGetValue(id, out var g) ? g : Get(id)?.Defaults ?? Array.Empty<string>();
    /// <summary>The first shortcut (menus' gesture text).</summary>
    public static string Text(string id) => Of(id).FirstOrDefault() ?? "";
    /// <summary>The command a gesture runs (null = none).</summary>
    public static string? Find(string gesture)
    {
        byGesture ??= All.SelectMany(c => Of(c.Id).Select(g => (g, c.Id))).GroupBy(x => x.g).ToDictionary(x => x.Key, x => x.First().Id);
        return byGesture.GetValueOrDefault(gesture);
    }
    /// <summary>The command that has this gesture besides <paramref name="id"/> (null = free).</summary>
    public static KeyCmd? Conflict(string gesture, string id) => All.FirstOrDefault(c => c.Id != id && Of(c.Id).Contains(gesture));

    /// <summary>Sets a command's shortcuts; a gesture another command had is taken from it. Defaults are stored as "no override".</summary>
    public static void Set(string id, IEnumerable<string> gestures)
    {
        var g = gestures.Where(x => x.Length > 0).Distinct().ToArray();
        var map = AppSettings.Current.Shortcuts;
        foreach (var x in g)
            foreach (var other in All.Where(c => c.Id != id && Of(c.Id).Contains(x)).ToList())
                Store(map, other.Id, Of(other.Id).Where(y => y != x).ToArray());
        Store(map, id, g);
        Done();
    }
    static void Store(Dictionary<string, string[]> map, string id, string[] g)
    {
        if (Get(id) is { } c && c.Defaults.SequenceEqual(g)) map.Remove(id); else map[id] = g;
    }
    public static void Reset(string id)
    {
        // a default gesture that another command took over goes back here
        if (Get(id) is not { } c) return;
        var map = AppSettings.Current.Shortcuts;
        foreach (var x in c.Defaults)
            foreach (var other in All.Where(o => o.Id != id && Of(o.Id).Contains(x)).ToList())
                Store(map, other.Id, Of(other.Id).Where(y => y != x).ToArray());
        map.Remove(id);
        Done();
    }
    public static void ResetAll() { AppSettings.Current.Shortcuts.Clear(); Done(); }
    static void Done() { byGesture = null; AppSettings.Save(); Changed?.Invoke(); }

    /// <summary>The gesture text of a key press (null for a modifier key alone, or a key that has no name here).</summary>
    public static string? Gesture(Key key, ModifierKeys mods)
    {
        var name = KeyName(key);
        if (name == null) return null;
        return (mods.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (mods.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (mods.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + name;
    }
    public static string? KeyName(Key k) => k switch
    {
        >= Key.A and <= Key.Z => k.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (k - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => k.ToString(),
        Key.Space => "Space", Key.Delete => "Del", Key.Back => "Backspace", Key.Escape => "Esc", Key.Return => "Enter", Key.Tab => "Tab",
        Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓", Key.Home => "Home", Key.End => "End", Key.PageUp => "PgUp", Key.PageDown => "PgDn", Key.Insert => "Ins",
        Key.OemComma => ",", Key.OemPeriod => ".", Key.OemSemicolon => ";", Key.OemQuotes => "'", Key.Oem3 => "`", Key.OemPlus => "=", Key.OemMinus => "-",
        Key.Oem5 or Key.OemBackslash => "\\", Key.OemOpenBrackets => "[", Key.Oem6 => "]", Key.OemQuestion => "/",
        Key.Add => "Num +", Key.Subtract => "Num -", Key.Multiply => "Num *", Key.Divide => "Num /",
        _ => null,
    };
}
