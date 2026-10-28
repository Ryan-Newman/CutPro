using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace CutPro;

/// <summary>Clip commands in the main window (partial MainWindow): copy / cut / paste / paste insert, Paste Attributes and Remove Attributes
/// (Premiere's dialog of checkboxes), split-screen / picture-in-picture layouts, Reveal in Project / File Explorer.</summary>
public partial class MainWindow
{
    void CopyClips(bool cut)
    {
        int n = ed.CopySelection(cut);
        SetTask(n == 0 ? "Select clips to copy" : $"{(cut ? "Cut" : "Copied")} {n} clip{(n == 1 ? "" : "s")} — {K("edit.paste")} pastes at the playhead, {K("edit.pasteAttr")} pastes their effects", 3000);
    }
    void PasteClips(bool insert)
    {
        if (!Editor.CanPaste) { SetTask("Nothing copied yet", 2000); return; }
        var made = ed.Paste(insert);
        if (made.Count == 0) SetTask("Nothing could be pasted here (locked tracks, or a sequence into itself)", 3000);
        else EnsureTimeVisible(ed.Time);
    }

    /// <summary>A dialog of checkboxes, one per attribute (ticked: the ones <paramref name="tick"/> has); null = cancelled.</summary>
    ClipAttr? AttrDialog(string title, string message, string ok, ClipAttr tick, ClipAttr enabled)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var boxes = new List<(CheckBox b, ClipAttr a)>();
        foreach (var (a, name) in Editor.AttrNames)
        {
            var cb = new CheckBox { Content = name, IsChecked = tick.HasFlag(a), IsEnabled = enabled.HasFlag(a), Margin = new Thickness(0, 2, 0, 2) };
            boxes.Add((cb, a)); panel.Children.Add(cb);
        }
        var all = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (label, on) in new[] { ("All", true), ("None", false) })
        {
            var b = new Button { Content = label, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (_, _) => { foreach (var (cb, _) in boxes) if (cb.IsEnabled) cb.IsChecked = on; };
            all.Children.Add(b);
        }
        panel.Children.Add(all);
        if (Alert.Show(this, message, title, AlertKind.Question, new[] { ok, "Cancel" }, panel) != 0) return null;
        return boxes.Where(x => x.b.IsChecked == true).Aggregate(ClipAttr.None, (s, x) => s | x.a);
    }

    /// <summary>Edit ▸ Paste Attributes (Ctrl+Alt+V): the first copied clip's motion / opacity / speed / volume / effects onto the selected clips.</summary>
    void PasteAttributesDialog()
    {
        if (Editor.Copied is not { Count: > 0 } copied) { SetTask("Copy a clip first (Ctrl+C), then select the clips to paste its attributes onto", 3000); return; }
        if (ed.Sel.Count == 0) { SetTask("Select the clips to paste the attributes onto", 2500); return; }
        var src = copied.FirstOrDefault(c => c.Kind != ClipKind.Audio) ?? copied[0];
        var has = Editor.AttrsOf(src);
        if (src.Kind != ClipKind.Audio && copied.FirstOrDefault(c => c.Kind == ClipKind.Audio && c.LinkId != null && c.LinkId == src.LinkId) is { } snd)
            has |= Editor.AttrsOf(snd) & (ClipAttr.Volume | ClipAttr.AudioFx);
        if (has == ClipAttr.None) { SetTask("The copied clip has no attributes to paste (all at their defaults)", 3000); return; }
        var which = AttrDialog("Paste Attributes", $"From “{ed.MediaOf(src)?.Name ?? src.Text}” onto the {ed.Sel.Count} selected clip{(ed.Sel.Count == 1 ? "" : "s")}:", "Paste", has, has);
        if (which is not { } w || w == ClipAttr.None) return;
        // a linked picture + sound pair: the sound's volume / effects come from the copied sound clip
        var audioSrc = copied.FirstOrDefault(c => c.Kind == ClipKind.Audio && (c.LinkId == src.LinkId || src.Kind == ClipKind.Audio));
        int n = ed.PasteAttributes(src, w & ~(ClipAttr.Volume | ClipAttr.AudioFx));
        if (audioSrc != null && (w & (ClipAttr.Volume | ClipAttr.AudioFx)) != 0) n = Math.Max(n, ed.PasteAttributes(audioSrc, w & (ClipAttr.Volume | ClipAttr.AudioFx)));
        SetTask($"Attributes pasted onto {n} clip{(n == 1 ? "" : "s")}", 2500);
    }

    /// <summary>Edit ▸ Remove Attributes: the selected clips' chosen attributes back to their defaults.</summary>
    void RemoveAttributesDialog()
    {
        var sel = ed.Clips.Where(ed.IsSel).ToList();
        if (sel.Count == 0) { SetTask("Select the clips to remove attributes from", 2500); return; }
        var has = sel.Aggregate(ClipAttr.None, (s, c) => s | Editor.AttrsOf(c));
        if (has == ClipAttr.None) { SetTask("The selected clips have no attributes to remove", 2500); return; }
        var which = AttrDialog("Remove Attributes", $"Back to the defaults on the {sel.Count} selected clip{(sel.Count == 1 ? "" : "s")}:", "Remove", has, has);
        if (which is not { } w || w == ClipAttr.None) return;
        int n = ed.RemoveAttributes(w);
        SetTask($"Attributes removed from {n} clip{(n == 1 ? "" : "s")}", 2500);
    }

    // ---------------- layouts ----------------
    static readonly (LayoutPreset p, string name)[] LayoutNames =
    {
        (LayoutPreset.PipTopLeft, "Picture in picture: top left"), (LayoutPreset.PipTopRight, "Picture in picture: top right"),
        (LayoutPreset.PipBottomLeft, "Picture in picture: bottom left"), (LayoutPreset.PipBottomRight, "Picture in picture: bottom right"),
        (LayoutPreset.SideBySide, "Split: side by side (2)"), (LayoutPreset.TopBottom, "Split: top and bottom (2)"),
        (LayoutPreset.ThreeAcross, "Split: three across (3)"), (LayoutPreset.Grid2x2, "Grid: 2 × 2 (4)"), (LayoutPreset.Full, "Full frame (reset)"),
    };
    MenuItem LayoutMenu()
    {
        var m = new MenuItem { Header = "Layout (split screen / picture in picture)" };
        foreach (var (p, name) in LayoutNames)
        {
            var mi = new MenuItem { Header = name, IsEnabled = ed.Clips.Any(c => ed.IsSel(c) && c.Kind is ClipKind.Video or ClipKind.Image) };
            mi.Click += (_, _) => ApplyLayout(p);
            if (p == LayoutPreset.Full) m.Items.Add(new Separator());
            m.Items.Add(mi);
        }
        return m;
    }
    void ApplyLayout(LayoutPreset p)
    {
        int n = ed.ApplyLayout(p);
        int need = p switch { LayoutPreset.SideBySide or LayoutPreset.TopBottom => 2, LayoutPreset.ThreeAcross => 3, LayoutPreset.Grid2x2 => 4, _ => 1 };
        SetTask(n == 0 ? "Select the video or picture clips to arrange (on different tracks, highest first)"
              : n < need ? $"Placed {n} of {need} — select {need} clips (on different tracks) for the whole layout" : $"Layout applied to {n} clip{(n == 1 ? "" : "s")}", 3500);
    }

    // ---------------- reveal ----------------
    /// <summary>Clip ▸ Reveal in Project: the Project panel shows and selects the clip's media (or its sequence).</summary>
    void RevealInProject(Clip? c = null)
    {
        c ??= ed.Selected;
        if (c == null || ed.MediaOf(c) is not { } m) { SetTask("Select a clip to find its media in the Project panel", 2500); return; }
        ShowSideView("Project");
        var item = BinList.Items.Cast<object>().FirstOrDefault(x => x is MediaItem mi && (mi.Id == m.Id || m.Seq != null && mi.Seq == m.Seq));
        if (item == null) return;
        BinList.SelectedItem = item;
        BinList.ScrollIntoView(item);
        Dispatcher.BeginInvoke(() => (BinList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem)?.Focus(), System.Windows.Threading.DispatcherPriority.Background);
    }
    /// <summary>Clip ▸ Reveal in File Explorer: Explorer opens the folder with the file selected.</summary>
    void RevealInExplorer()
    {
        var m = ed.Selected is { } c ? ed.MediaOf(c) : BinList.SelectedItem as MediaItem;
        if (m is not { Seq: null }) { SetTask("Select a clip (or a file in the Project panel)", 2500); return; }
        if (!File.Exists(m.Path)) { SetTask($"“{m.Name}” is offline — relink it first", 3000); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{m.Path}\"") { UseShellExecute = true }); } catch { }
    }
}
