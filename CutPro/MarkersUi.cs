using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CutPro;

/// <summary>Markers in the main window (partial MainWindow): M / Add Clip Marker, the marker dialog (name, comment, colour, length, chapter),
/// YouTube chapters to the clipboard (the Publish window also puts them into the YouTube description).</summary>
public partial class MainWindow
{
    void InitMarkers()
    {
        ed.MarkersChanged += () => { Tl.InvalidateVisual(); Ph.InvalidateVisual(); };
        Tl.MarkerEditRequested += (m, c) => EditMarker(m, c);
        Tl.ClipMenuOpening += ClipMenuExtras;
    }

    /// <summary>The sequence marker on the playhead's frame.</summary>
    Marker? MarkerAtPlayhead() => ed.Markers.FirstOrDefault(m => Math.Abs(m.T - ed.Time) < 0.5 / TimeUtil.Fps);

    /// <summary>M: a sequence marker at the playhead (pressed twice on the same frame: its dialog, like Premiere).</summary>
    void AddMarkerAtPlayhead()
    {
        if (MarkerAtPlayhead() is { } had) { EditMarker(had, null); return; }
        ed.AddMarker(ed.Time);
        SetTask($"Marker at {TimeUtil.Format(ed.Time)} — M again opens it, Shift+M goes to the next", 2500);
    }
    /// <summary>A marker on the selected clip under the playhead (else the topmost clip there).</summary>
    void AddClipMarkerAtPlayhead()
    {
        var c = ed.Clips.Where(x => ed.IsSel(x) && ed.Time >= x.Start && ed.Time < x.End).OrderBy(x => x.Track).FirstOrDefault()
             ?? ed.ClipsAt(ed.Time).Where(ed.Editable).OrderBy(x => x.Track).FirstOrDefault();
        if (c == null || ed.AddClipMarker(c, ed.Time) == null) SetTask("No clip under the playhead for a clip marker", 2500);
    }
    void EditMarkerAtPlayhead()
    {
        if (MarkerAtPlayhead() is { } m) { EditMarker(m, null); return; }
        foreach (var c in ed.ClipsAt(ed.Time).Where(x => x.Markers != null))
            if (c.Markers!.FirstOrDefault(m => Editor.TimelineTime(c, m) is double t && Math.Abs(t - ed.Time) < 0.5 / TimeUtil.Fps) is { } cm) { EditMarker(cm, c); return; }
        SetTask("No marker at the playhead", 2000);
    }

    /// <summary>Premiere's Marker dialog: name, comments, colour, duration; sequence markers also "Chapter" (export / YouTube chapters).</summary>
    void EditMarker(Marker m, Clip? c)
    {
        TextBlock L(string s) { var t = new TextBlock { Text = s, Margin = new Thickness(0, 8, 0, 2) }; t.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg"); return t; }
        var name = new TextBox { Text = m.Name };
        var note = new TextBox { Text = m.Note, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dur = new TextBox { Text = m.Dur.ToString("0.###", CultureInfo.InvariantCulture), Width = 70, HorizontalAlignment = HorizontalAlignment.Left };
        var colour = new ComboBox { Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var mc in Enum.GetValues<MarkerColor>())
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Rectangle { Width = 12, Height = 12, Fill = new SolidColorBrush(Marker.Rgb(mc)), Margin = new Thickness(0, 0, 6, 0) });
            sp.Children.Add(new TextBlock { Text = mc.ToString() });
            colour.Items.Add(new ComboBoxItem { Content = sp, Tag = mc });
        }
        colour.SelectedIndex = (int)m.Color;
        var chapter = new CheckBox { Content = "Chapter (MP4 / MOV / MKV chapters, YouTube chapter list)", IsChecked = m.Chapter, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (el, n) in new (FrameworkElement, string)[] { (name, "Marker name"), (note, "Comments"), (dur, "Duration in seconds"), (colour, "Marker colour") })
            System.Windows.Automation.AutomationProperties.SetName(el, n);
        var p = new StackPanel { MinWidth = 340 };
        p.Children.Add(L("Name")); p.Children.Add(name);
        p.Children.Add(L("Comments")); p.Children.Add(note);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var dcol = new StackPanel { Margin = new Thickness(0, 0, 16, 0) }; dcol.Children.Add(L("Duration (s)")); dcol.Children.Add(dur);
        var ccol = new StackPanel(); ccol.Children.Add(L("Colour")); ccol.Children.Add(colour);
        row.Children.Add(dcol); row.Children.Add(ccol); p.Children.Add(row);
        if (c == null) p.Children.Add(chapter);
        name.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        double at = c == null ? m.T : Editor.TimelineTime(c, m) ?? c.Start;
        int r = Alert.Show(this, (c == null ? "Sequence marker" : $"Clip marker on “{ed.MediaOf(c)?.Name ?? c.Text}”") + $" at {TimeUtil.Format(at)}", "Marker",
                           AlertKind.Info, new[] { "OK", "Delete", "Cancel" }, p);
        if (r == 1) { ed.DeleteMarker(m, c); return; }
        if (r != 0) return;
        double d = double.TryParse(dur.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var dv) ? Math.Max(0, Editor.Frame(dv)) : m.Dur;
        var col = colour.SelectedItem is ComboBoxItem { Tag: MarkerColor mc2 } ? mc2 : m.Color;
        ed.UpdateMarker(m, c, x => { x.Name = name.Text.Trim(); x.Note = note.Text.Trim(); x.Dur = d; x.Color = col; if (c == null) x.Chapter = chapter.IsChecked == true; });
    }

    /// <summary>YouTube's chapter text when the markers make valid chapters (null otherwise) — the Publish window adds it to the description.</summary>
    internal string? ChapterText()
    {
        var ch = ed.ChaptersFor(0, ed.End);
        if (ch.Count == 0) return null;
        var text = Chapters.YouTube(ch, ed.End, out var problems);
        return problems.All(p => p.Contains("was added")) ? text : null;
    }

    /// <summary>Marker ▸ Copy YouTube Chapters: the list to paste into a YouTube description, with what YouTube would refuse.</summary>
    void CopyChapters()
    {
        var text = Chapters.YouTube(ed.ChaptersFor(0, ed.End), ed.End, out var problems);
        if (text.Length == 0) { Alert.Show(this, problems[0], "YouTube chapters", AlertKind.Info); return; }
        try { Clipboard.SetText(text); } catch { }
        Alert.Show(this, text + (problems.Count > 0 ? "\n\n" + string.Join("\n", problems) : ""), "Copied — paste it into the YouTube description",
                   problems.Any(p => !p.Contains("was added")) ? AlertKind.Warning : AlertKind.Info);
    }

    /// <summary>The window's items in a clip's context menu: clip marker, clipboard, attributes, groups, layouts, reveal, relink.</summary>
    void ClipMenuExtras(ContextMenu menu, Clip c, double t)
    {
        MenuItem Item(string text, Action a, string gesture = "", bool enabled = true, ItemsControl? into = null)
        {
            var mi = new MenuItem { Header = text, InputGestureText = gesture, IsEnabled = enabled };
            mi.Click += (_, _) => a(); (into ?? menu).Items.Add(mi); return mi;
        }
        menu.Items.Add(new Separator());
        Item("Add Clip Marker", () => ed.AddClipMarker(c, ed.Time >= c.Start && ed.Time < c.End ? ed.Time : t), K("marker.addClip"));
        menu.Items.Add(new Separator());
        Item("Cut", () => CopyClips(true), K("edit.cut")); Item("Copy", () => CopyClips(false), K("edit.copy"));
        Item("Paste Attributes…", PasteAttributesDialog, K("edit.pasteAttr"), Editor.CanPaste);
        Item("Remove Attributes…", RemoveAttributesDialog, K("edit.removeAttr"));
        if (c.Kind is ClipKind.Video or ClipKind.Image) menu.Items.Add(LayoutMenu());
        menu.Items.Add(new Separator());
        Item("Group", () => RunCommand("clip.group"), K("clip.group"), ed.Sel.Count > 1);
        Item("Ungroup", () => RunCommand("clip.ungroup"), K("clip.ungroup"), ed.Clips.Any(x => ed.IsSel(x) && x.GroupId != null));
        if (ed.MediaOf(c) is { Seq: null } m)
        {
            menu.Items.Add(new Separator());
            Item("Reveal in Project", () => RevealInProject(c), K("clip.reveal"));
            Item("Reveal in File Explorer", RevealInExplorer, K("clip.revealFile"));
            Item("Replace / Relink Media…", () => RelinkMedia(m));
            Item("Properties…", () => ShowProperties(m));
        }
        ClipMenuHigh(menu, c);
    }
}
