using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CutPro;

/// <summary>
/// Tracks in the main window (partial MainWindow): the timeline's vertical scrolling (scroll bar, the rows as tall as they are), the track
/// header's requests (rename, Add Tracks…), the Sequence menu's Add Tracks… / Delete Empty Tracks, and keeping the panels that list tracks
/// current when the layout changes (mixer strips, the source monitor's patching, the band modes moving with their tracks).
/// </summary>
public partial class MainWindow
{
    void InitTracks()
    {
        Tl.RenameTrackRequested += RenameTrack;
        Tl.AddTracksRequested += row => AddTracksDialog(row);
        Tl.Status += msg => SetTask(msg, 2500);
        ed.TracksMoved += Tl.MoveRows;
        ed.TracksMoved += map =>
        {   // a track's effects in Effect Controls follow the track (or close with it)
            if (fxTrack is int ft && ft < SubBase) { fxTrack = ft < map.Length && map[ft] >= 0 ? map[ft] : null; BuildInspector(); }
        };
        ed.TracksChanged += () =>
        {
            Tl.InvalidateVisual(); Ph.InvalidateVisual(); UpdateVScroll();
            if (stripSig != StripSig()) BuildStrips();                        // (audio tracks added / removed / renamed)
            RefreshMixer();
            SrcPatchLabels();
        };
        Tl.SizeChanged += (_, _) => UpdateVScroll();
        VScroll.Scroll += (_, e) => Tl.ScrollY = e.NewValue;
        UpdateVScroll();
    }

    /// <summary>The vertical scroll bar: shown while the tracks are taller than the panel; the timeline keeps clear of it.</summary>
    void UpdateVScroll()
    {
        if (!ready && !IsLoaded) return;
        double extra = Tl.ContentHeight - Tl.TrackViewport;
        bool show = extra > 0.5;
        var vis = show ? Visibility.Visible : Visibility.Collapsed;
        if (VScroll.Visibility != vis)
        {
            VScroll.Visibility = vis;
            var m = new Thickness(0, 0, show ? VScroll.Width : 0, 0);
            Tl.Margin = m; Ph.Margin = m;
        }
        if (!show) { if (Tl.ScrollY > 0) Tl.ScrollY = 0; return; }
        VScroll.Maximum = extra; VScroll.ViewportSize = Tl.TrackViewport;
        VScroll.LargeChange = Tl.TrackViewport * 0.8; VScroll.SmallChange = 30;
        VScroll.Value = Tl.ScrollY;
    }

    void RenameTrack(int row)
    {
        if (row < 0 || row >= Tracks.Count) return;
        var name = AskName("Rename track", $"Name of {Tracks.Label(row)} (empty: “{Tracks.Label(row)}”):", Tracks.Name(row), "Rename");
        if (name != null) ed.RenameTrack(row, name);
    }

    /// <summary>Premiere's Add Tracks dialog: how many video and audio tracks, and where (the highest / above or below the track the menu was opened
    /// on / below V1; after the last audio track / below the track / above A1). One undo step.</summary>
    void AddTracksDialog(int row = -1)
    {
        TextBox Count(string v) => new() { Text = v, Width = 44, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var vCount = Count(row >= 0 && Tracks.IsVideo(row) || row < 0 ? "1" : "0");
        var aCount = Count(row >= 0 && !Tracks.IsVideo(row) || row < 0 ? "1" : "0");
        int vTarget = row >= 0 && Tracks.IsVideo(row) ? row : -1, aTarget = row >= 0 && !Tracks.IsVideo(row) ? row : -1;
        // placements: (label, row to insert before)
        var vPlaces = new List<(string, int)> { ($"Above {Tracks.Name(0)} (the highest)", 0) };
        if (vTarget > 0) vPlaces.Add(($"Above {Tracks.Name(vTarget)}", vTarget));
        if (vTarget >= 0 && vTarget < Tracks.V1) vPlaces.Add(($"Below {Tracks.Name(vTarget)}", vTarget + 1));
        vPlaces.Add(($"Below {Tracks.Name(Tracks.V1)} (the new ones become V1 …)", Tracks.Video));
        var aPlaces = new List<(string, int)> { ($"After {Tracks.Name(Tracks.Count - 1)} (the last)", Tracks.Count) };
        if (aTarget >= 0 && aTarget < Tracks.Count - 1) aPlaces.Add(($"Below {Tracks.Name(aTarget)}", aTarget + 1));
        if (aTarget > Tracks.A1) aPlaces.Add(($"Above {Tracks.Name(aTarget)}", aTarget));
        aPlaces.Add(($"Above {Tracks.Name(Tracks.A1)} (the new ones become A1 …)", Tracks.A1));
        ComboBox Places(List<(string label, int at)> l, int pick) { var cb = new ComboBox { MinWidth = 260 }; foreach (var (s, _) in l) cb.Items.Add(s); cb.SelectedIndex = pick; return cb; }
        var vPlace = Places(vPlaces, vTarget >= 0 ? 1 : 0);
        var aPlace = Places(aPlaces, aTarget >= 0 ? 1 : 0);
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(int r, string label, TextBox count, ComboBox place)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var tb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 4) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg");
            Grid.SetRow(tb, r); Grid.SetRow(count, r); Grid.SetRow(place, r);
            Grid.SetColumn(count, 1); Grid.SetColumn(place, 2);
            place.Margin = new Thickness(0, 4, 0, 4);
            grid.Children.Add(tb); grid.Children.Add(count); grid.Children.Add(place);
        }
        Row(0, "Video tracks", vCount, vPlace);
        Row(1, "Audio tracks", aCount, aPlace);
        int r = Alert.Show(this, $"The sequence has {Tracks.Video} video and {Tracks.Audio} audio tracks (at most {Tracks.MaxVideo} of each).", "Add tracks",
                           AlertKind.Question, new[] { "Add", "Cancel" }, grid);
        if (r != 0) return;
        int Parse(TextBox t) => int.TryParse(t.Text.Trim(), out var n) ? Math.Max(0, n) : 0;
        int nv = Math.Min(Parse(vCount), Tracks.MaxVideo - Tracks.Video), na = Math.Min(Parse(aCount), Tracks.MaxAudio - Tracks.Audio);
        if (nv + na == 0) { SetTask("No tracks added (a sequence has at most 32 video and 32 audio tracks)", 3000); return; }
        int vAt = vPlaces[Math.Max(0, vPlace.SelectedIndex)].Item2, aAt = aPlaces[Math.Max(0, aPlace.SelectedIndex)].Item2;
        if (ed.AddTracks(nv, vAt, na, aAt)) SetTask($"Added {Plural(nv, "video track")}{(nv > 0 && na > 0 ? " and " : "")}{Plural(na, "audio track")}", 2500);
    }
    static string Plural(int n, string what) => n == 0 ? "" : $"{n} {what}{(n == 1 ? "" : "s")}";

    void DeleteEmptyTracks()
    {
        int k = ed.DeleteEmptyTracks();
        SetTask(k > 0 ? $"{Plural(k, "empty track")} deleted" : "No empty tracks to delete", 2500);
    }
}
