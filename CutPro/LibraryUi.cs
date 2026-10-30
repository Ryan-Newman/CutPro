using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>The Effects side view rebuilt as a Library (Sonduck-style): search, categories, drag a transition onto a cut
/// (sound effect included), double-click graphics / looks / whooshes.</summary>
public partial class MainWindow
{
    void LibraryFilter_Changed(object s, RoutedEventArgs e) => BuildEffectsList();

    void FillLibraryCats()
    {
        if (LibraryCat.Items.Count > 0) return;
        foreach (var c in Library.Categories) LibraryCat.Items.Add(c);
        LibraryCat.SelectedIndex = 0;
        LibraryCat.SelectionChanged += (_, _) => BuildEffectsList();
    }

    void ApplyLibrary(LibItem item)
    {
        switch (item.Kind)
        {
            case LibKind.Transition:
            {
                bool audio = Transitions.Get(item.TransKind!).Fx == Transitions.Fx.Audio;
                int n = ed.ApplyTransition(item.TransKind, item.Dur, null, !audio, audio);
                if (n > 0 && item.Sfx != SfxKind.None)
                {
                    var plan = Transitions.Compute(ed);
                    foreach (var w in plan.Wins.Where(x => x.D.Name == item.TransKind).OrderBy(x => Math.Abs(x.Cut - ed.Time)).Take(n))
                        Library.PlaceSfx(ed, item.Sfx, w.T0, w.Dur);
                }
                SetTask(n > 0 ? $"“{item.Name}” on {n} cut{(n == 1 ? "" : "s")}" + (item.Sfx == SfxKind.None ? "" : " (sound on the timeline)")
                              : "No cut near the playhead: park it within 2 s of a cut, or drag onto a cut", 5000);
                break;
            }
            case LibKind.Sound:
                Library.PlaceSfx(ed, item.Sfx, ed.Time, item.Dur);
                SetTask($"“{item.Name}” at the playhead", 3000);
                break;
            case LibKind.Graphic:
                if (GraphicLibrary.All.FirstOrDefault(g => g.Name == item.Graphic) is { } g) AddGraphic(g);
                break;
            case LibKind.Look:
                var clips = ed.Clips.Where(c => ed.IsSel(c) && c.Kind is ClipKind.Video or ClipKind.Image).ToList();
                if (clips.Count == 0) { SetTask("Select a video or picture clip for this look", 3000); return; }
                ed.Edit(() => { foreach (var c in clips) c.Looks = item.Look!.Clone(); }, "Look");
                SetTask($"“{item.Name}” on {clips.Count} clip{(clips.Count == 1 ? "" : "s")}", 3000);
                break;
        }
    }
}

public partial class MainWindow
{
    /// <summary>Library (replaces the old flat Effects list): categories, search, cinematic transitions with sound.</summary>
    void BuildEffectsList()
    {
        FillLibraryCats();
        EffectsList.Children.Clear();
        string q = LibrarySearch.Text ?? "";
        string cat = LibraryCat.SelectedItem as string ?? "All";
        var items = Library.All.Where(i => (cat == "All" || i.Category == cat) && i.Matches(q)).ToList();
        var note = new TextBlock
        {
            Text = Loc.T("Library — drag a transition onto a cut (it brings its sound). Double-click applies at the playhead. Graphics, looks and whooshes live here too."),
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(4, 0, 4, 8)
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        EffectsList.Children.Add(note);
        string? last = null;
        WrapPanel? wrap = null;
        foreach (var item in items)
        {
            if (item.Category != last)
            {
                last = item.Category;
                EffectsList.Children.Add(new TextBlock { Text = last, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 10, 0, 4) });
                wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                EffectsList.Children.Add(wrap);
            }
            wrap!.Children.Add(LibraryCard(item));
        }
        if (items.Count == 0)
            EffectsList.Children.Add(new TextBlock { Text = Loc.T("Nothing matches."), Margin = new Thickness(8, 8, 8, 8) });
        if (cat is "All" or "Classic")
        {
            BuildTransPresetList();
            BuildPresetList();
        }
    }

    FrameworkElement LibraryCard(LibItem item)
    {
        var row = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 4, 8, 4), Margin = new Thickness(2), Cursor = Cursors.Hand,
                               ToolTip = item.Help, MinWidth = 148, MaxWidth = 220 };
        row.SetResourceReference(Border.BackgroundProperty, "B.Hover");
        row.Background = Brushes.Transparent;
        var sp = new StackPanel();
        if (item.Kind == LibKind.Transition && item.TransKind != null)
            sp.Children.Add(new Image { Source = TransitionIcon(Transitions.Get(item.TransKind)), Width = 40, Height = 24, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 3) });
        var title = new TextBlock { Text = item.Name, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        sp.Children.Add(title);
        if (item.Sfx != SfxKind.None)
        {
            var sfx = new TextBlock { Text = item.Kind == LibKind.Sound ? $"{item.Dur:0.##} s" : Loc.T("with sound"), FontSize = 10 };
            sfx.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
            sp.Children.Add(sfx);
        }
        row.Child = sp;
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "B.Hover");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        Point? down = null;
        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { ApplyLibrary(item); e.Handled = true; return; }
            down = e.GetPosition(row);
        };
        row.PreviewMouseMove += (_, e) =>
        {
            if (down is not { } p0 || e.LeftButton != MouseButtonState.Pressed) { down = null; return; }
            if ((e.GetPosition(row) - p0).Length < 4) return;
            down = null;
            var data = new DataObject();
            data.SetData("CutPro.Library", item.Id);
            if (item.Kind == LibKind.Transition && item.TransKind != null) data.SetData("CutPro.Transition", item.TransKind);
            if (item.Kind == LibKind.Preset && item.Preset != null) data.SetData("CutPro.Preset", item.Preset);
            DragDrop.DoDragDrop(row, data, DragDropEffects.Copy);
        };
        return row;
    }
}
