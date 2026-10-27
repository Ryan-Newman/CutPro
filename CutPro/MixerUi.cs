using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace CutPro;

/// <summary>Audio Track Mixer side view: a channel strip per audio track (pan, fader, meter, mute, solo) and the master (Mix), plus loudness.</summary>
public partial class MainWindow
{
    sealed class Strip
    {
        public int Track;                       // −1 = master
        public Fader Fader = null!; public LevelMeter Meter = null!; public TextBox Value = null!;
        public PanKnob? Pan; public TextBlock? PanText; public ToggleButton? Mute, Solo; public Button? Auto, FxBtn; public ComboBox? Out;
    }
    readonly List<Strip> strips = new();
    readonly float[] levels = new float[Mixer.Slots];
    long meterTick;
    TextBlock? loudText;
    bool mixRefreshing;

    void InitMixer()
    {
        BuildStrips();
        ed.MixChanged += RefreshMixer;
        MixerScroll.SizeChanged += (_, _) => FitStrips();
        RefreshMixer();
        BuildLoudness();
    }

    /// <summary>Strip ids: tracks 0…, submixes <see cref="SubBase"/> + index, the master −1.</summary>
    const int SubBase = 100;
    void BuildStrips()
    {
        MixerStrips.Children.Clear(); strips.Clear();
        MixerStrips.Columns = 0;
        for (int r = 0; r < Tracks.Count; r++) if (!Tracks.IsVideo(r)) MixerStrips.Children.Add(MakeStrip(r));
        for (int k = 0; k < ed.Mix.Subs.Count; k++) MixerStrips.Children.Add(MakeStrip(SubBase + k));
        MixerStrips.Children.Add(MakeStrip(-1));
        AddSubBtn.IsEnabled = ed.Mix.Subs.Count < Mixer.MaxSubs;
        stripSig = StripSig();
        FitStrips();
    }

    /// <summary>The strips share the panel's width, at least 74 px each — with many tracks the row gets wider than the panel and scrolls.</summary>
    void FitStrips()
    {
        double avail = MixerScroll.ViewportWidth > 0 ? MixerScroll.ViewportWidth : MixerScroll.ActualWidth;
        if (avail <= 0) return;
        double w = Math.Max(avail - MixerStrips.Margin.Left - MixerStrips.Margin.Right, MixerStrips.Children.Count * 74);
        if (Math.Abs(MixerStrips.Width - w) > 0.5 || double.IsNaN(MixerStrips.Width)) MixerStrips.Width = w;
    }

    SubMix? SubOf(int id) => id >= SubBase && id - SubBase < ed.Mix.Subs.Count ? ed.Mix.Subs[id - SubBase] : null;
    void SetGain(int track, double db)
    {
        if (track < 0) ed.Mix.Master = db;
        else if (SubOf(track) is { } sm) sm.Gain = db;
        else ed.Mix.Channels[track].Gain = db;
    }
    void SetPan(int track, double v) { if (SubOf(track) is { } sm) sm.Pan = v; else if (track >= 0) ed.Mix.Channels[track].Pan = v; }
    /// <summary>The fader / balance values at t of a strip (automation included).</summary>
    double GainAtStrip(int track, double t) => track < 0 ? ed.Mix.MasterAt(t) : SubOf(track) is { } sm ? sm.AsTrack().GainAt(t) : ed.Mix.Channels[track].GainAt(t);
    double PanAtStrip(int track, double t) => SubOf(track) is { } sm ? sm.AsTrack().PanAt(t) : ed.Mix.Channels[track].PanAt(t);

    void AddSubmix_Click(object sender, RoutedEventArgs e) => AddSubmix();
    void AddSubmix()
    {
        if (ed.Mix.Subs.Count >= Mixer.MaxSubs) return;
        ed.MixBegin(); ed.Mix.Subs.Add(new SubMix { Name = $"Submix {ed.Mix.Subs.Count + 1}" }); ed.MixEnd();
        ed.RaiseMixChanged();
        SetTask("Submix added — send tracks to it with the → menu under their strips", 3500);
    }
    void RemoveSubmix(int k)
    {
        ed.MixBegin();
        ed.Mix.Subs.RemoveAt(k);
        foreach (var ch in ed.Mix.Channels) { if (ch.Output == k) ch.Output = -1; else if (ch.Output > k) ch.Output--; }   // (its tracks go to the master)
        ed.MixEnd(); ed.RaiseMixChanged();
    }

    FrameworkElement MakeStrip(int track)
    {
        bool master = track < 0, isSub = track >= SubBase;
        int sk = track - SubBase;
        var s = new Strip { Track = track };
        var g = new Grid { Margin = new Thickness(2, 0, 2, 0), MinWidth = 62 };   // (many tracks: the strips keep their width and the mixer scrolls)
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            g.RowDefinitions.Add(new RowDefinition { Height = h });
        void Put(UIElement e, int row) { Grid.SetRow(e, row); g.Children.Add(e); }

        var name = new TextBlock { Text = master ? "Mix" : isSub ? ed.Mix.Subs[sk].Name : Tracks.Name(track), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                                   Margin = new Thickness(0, 0, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = isSub ? "Submix — double-click to rename" : null };
        if (isSub)
        {
            name.SetResourceReference(TextBlock.ForegroundProperty, "B.Accent2");
            name.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2) return;
                var n = AskName("Rename submix", "Name of this submix:", ed.Mix.Subs[sk].Name, "Rename");
                if (!string.IsNullOrWhiteSpace(n)) { ed.MixBegin(); ed.Mix.Subs[sk].Name = n.Trim(); ed.MixEnd(); BuildStrips(); RefreshMixer(); }
            };
        }
        Put(name, 0);

        var panBox = new StackPanel { Height = 52, HorizontalAlignment = HorizontalAlignment.Center };
        if (!master)
        {
            s.Pan = new PanKnob();
            s.PanText = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11 };
            s.PanText.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
            panBox.Children.Add(s.Pan); panBox.Children.Add(s.PanText);
            s.Pan.DragStarted += () => { ed.MixBegin(); AutoGrab(track, false); };
            s.Pan.DragEnded += () => { AutoRelease(track, false); ed.MixEnd(); };
            s.Pan.Moved += v => { if (!AutoMove(track, false, v)) SetPan(track, v); if (!s.Pan.Dragging) ed.MixTouch(); else ed.RaiseMixChanged(); };
        }
        Put(panBox, 1);

        var fm = new Grid { MinHeight = 150 };
        fm.ColumnDefinitions.Add(new ColumnDefinition()); fm.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        s.Fader = new Fader { MaxDb = master ? Mixer.MasterMaxDb : Mixer.MaxDb };
        s.Meter = new LevelMeter { Margin = new Thickness(2, 0, 0, 0) };
        Grid.SetColumn(s.Meter, 1);
        fm.Children.Add(s.Fader); fm.Children.Add(s.Meter);
        s.Fader.DragStarted += () => { ed.MixBegin(); AutoGrab(track, true); };
        s.Fader.DragEnded += () => { AutoRelease(track, true); ed.MixEnd(); };
        s.Fader.Moved += v => { if (!AutoMove(track, true, v)) SetGain(track, v); if (!s.Fader.Dragging) ed.MixTouch(); else ed.RaiseMixChanged(); };
        Put(fm, 2);

        s.Value = new TextBox { Width = 58, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 4),
                                ToolTip = "Volume in dB (type a value, e.g. −6; −inf = silent)" };
        void Apply()
        {
            var t = s.Value.Text.Trim().Replace('−', '-').Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
            double? v = t is "-inf" or "-∞" or "∞" or "inf" ? Mixer.MinDb : double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            if (v is double nv) { nv = Math.Clamp(Math.Round(nv, 1), Mixer.MinDb, s.Fader.MaxDb); if (!AutoMove(track, true, nv)) SetGain(track, nv); ed.MixTouch(); }
            else RefreshMixer();
        }
        s.Value.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); Keyboard.ClearFocus(); e.Handled = true; } else if (e.Key == Key.Escape) { RefreshMixer(); Keyboard.ClearFocus(); e.Handled = true; } };
        s.Value.LostKeyboardFocus += (_, _) => Apply();
        Put(s.Value, 3);

        var btns = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, MinHeight = 26 };          // (M S ◇ fx: two lines when the strip is narrow)
        ToggleButton Tb(string text, string tip) => new() { Content = text, Width = 26, Height = 24, Margin = new Thickness(1), ToolTip = tip, Style = (Style)FindResource("MixBtn"), Tag = text };
        Button Small(string text, string tip, double size = 13, bool italic = false) => new() { Content = text, Width = 26, Height = 24, Margin = new Thickness(1), Padding = new Thickness(0), Style = (Style)FindResource("Icon"),
                                                                                              FontFamily = TextFont, FontSize = size, FontStyle = italic ? FontStyles.Italic : FontStyles.Normal, ToolTip = tip };
        if (!master && !isSub)
        {
            s.Mute = Tb("M", "Mute this track"); s.Solo = Tb("S", "Solo: hear only the soloed tracks");
            s.Mute.Click += (_, _) => { ed.TrackOff[track] = s.Mute.IsChecked == true; ed.RaiseMixChanged(); };
            s.Solo.Click += (_, _) => { ed.Mix.Channels[track].Solo = s.Solo.IsChecked == true; ed.RaiseMixChanged(); };
            btns.Children.Add(s.Mute); btns.Children.Add(s.Solo);
            s.Auto = Small("◇", "Automation: volume / pan keys at the playhead, clear them, show them on the timeline (● Write records them while playing)");
            s.Auto.Click += (_, _) => { var cm = AutoMenu(track); cm.PlacementTarget = s.Auto; cm.IsOpen = true; };
            btns.Children.Add(s.Auto);
            s.FxBtn = Small("fx", "Track effects (EQ, compressor, reverb… on the track's clips summed, before the fader) — shown in Effect Controls", 12, true);
            s.FxBtn.Click += (_, _) => ShowTrackFx(track);
            btns.Children.Add(s.FxBtn);
            // output: the master or a submix
            if (ed.Mix.Subs.Count > 0)
            {
                s.Out = new ComboBox { FontSize = 11, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch, ToolTip = "Where this track goes: the master (Mix) or a submix" };
                s.Out.Items.Add("→ Mix");
                foreach (var sm in ed.Mix.Subs) s.Out.Items.Add("→ " + sm.Name);
                s.Out.SelectionChanged += (_, _) =>
                {
                    if (mixRefreshing || s.Out.SelectedIndex < 0) return;
                    int to = s.Out.SelectedIndex - 1;
                    if (ed.Mix.Channels[track].Output == to) return;
                    ed.MixBegin(); ed.Mix.Channels[track].Output = to; ed.MixEnd(); ed.RaiseMixChanged();
                };
                Put(s.Out, 5);
            }
        }
        else if (isSub)
        {
            s.Mute = Tb("M", "Mute this submix (and every track sent to it)");
            s.Mute.Click += (_, _) => { ed.Mix.Subs[sk].Mute = s.Mute.IsChecked == true; ed.RaiseMixChanged(); };
            btns.Children.Add(s.Mute);
            s.Auto = Small("◇", "Submix automation: volume / pan keys at the playhead, clear (● Write records them while playing)");
            s.Auto.Click += (_, _) => { var cm = AutoMenu(track); cm.PlacementTarget = s.Auto; cm.IsOpen = true; };
            btns.Children.Add(s.Auto);
            s.FxBtn = Small("fx", "Submix effects (on everything sent to it, before its fader) — shown in Effect Controls", 12, true);
            s.FxBtn.Click += (_, _) => ShowTrackFx(track);
            btns.Children.Add(s.FxBtn);
            var del = Small("✕", "Remove this submix (its tracks go to the master)", 11);
            del.Click += (_, _) => RemoveSubmix(sk);
            btns.Children.Add(del);
        }
        else
        {   // the master: automation only (● Write records its fader too)
            s.Auto = Small("◇", "Master automation: a volume key at the playhead, clear (● Write records the master fader while playing)");
            s.Auto.Click += (_, _) => { var cm = AutoMenu(-1); cm.PlacementTarget = s.Auto; cm.IsOpen = true; };
            btns.Children.Add(s.Auto);
        }
        Put(btns, 4);
        strips.Add(s);
        return g;
    }

    /// <summary>Shows the mixer state (after a change anywhere: strips, track headers, undo, project open).</summary>
    void RefreshMixer()
    {
        if (mixRefreshing) return;
        if (stripSig != StripSig()) BuildStrips();                          // (submixes added / removed / renamed, also by undo)
        mixRefreshing = true;
        foreach (var s in strips)
        {
            double db = GainAtStrip(s.Track, ed.Time);                         // (automated: the value at the playhead)
            if (!s.Fader.Dragging) s.Fader.Db = db;
            if (!s.Value.IsKeyboardFocusWithin) s.Value.Text = MixerElement.FormatDb(db);
            if (s.Pan != null) { double p = PanAtStrip(s.Track, ed.Time); if (!s.Pan.Dragging) s.Pan.Value = p; s.PanText!.Text = PanKnob.Label(p); }
            var sm = SubOf(s.Track);
            bool automated = s.Track < 0 ? ed.Mix.MasterKeys is { Count: > 0 } : sm != null ? sm.AsTrack().Automated : ed.Mix.Channels[s.Track].Automated;
            if (s.Auto != null) s.Auto.Content = automated ? "◆" : "◇";
            if (s.FxBtn != null) s.FxBtn.FontWeight = (sm != null ? sm.Fx?.Any(e => e.On) == true : AudioDsp.Active(ed.Mix.Channels[s.Track])) ? FontWeights.Bold : FontWeights.Normal;
            if (s.Mute != null) s.Mute.IsChecked = sm != null ? sm.Mute : ed.TrackOff[s.Track];
            if (s.Solo != null) s.Solo.IsChecked = ed.Mix.Channels[s.Track].Solo;
            if (s.Out != null) s.Out.SelectedIndex = Math.Clamp(ed.Mix.Channels[s.Track].Output + 1, 0, s.Out.Items.Count - 1);
        }
        AddSubBtn.IsEnabled = ed.Mix.Subs.Count < Mixer.MaxSubs;
        stripSig = StripSig();
        FitStrips();
        Tl.InvalidateVisual();
        mixRefreshing = false;
    }

    string stripSig = "";
    string StripSig() => string.Join("|", ed.Mix.Subs.Select(x => x.Name)) + "#" + ed.Mix.Subs.Count + "#" + string.Join(",", Tracks.AudioRows.Select(r => r + ":" + Tracks.Name(r)));   // (tracks added / removed / renamed: rows move)

    /// <summary>Per frame (render loop): meters follow what is being heard, ~30 updates a second, only while the mixer is showing.</summary>
    void UpdateMeters()
    {
        if (strips.Count == 0 || !Shows(MixerView)) return;                     // (wherever the mixer is docked)
        long now = Environment.TickCount64;
        if (now - meterTick < 30) return;
        double dt = Math.Min(0.2, (now - meterTick) / 1000.0); meterTick = now;
        AutoTick(); FollowAutomation();
        bool on = playing && audio.Levels(levels);
        foreach (var s in strips)
        {
            int k = s.Track < 0 ? Mixer.Slots - 2 : s.Track >= SubBase ? Mixer.SubSlot(s.Track - SubBase) : s.Track * 2;
            s.Meter.Push(on ? levels[k] : 0, on ? levels[k + 1] : 0, dt);
        }
    }

    void BuildLoudness()
    {
        LoudnessPanel.Children.Clear();
        LoudnessPanel.Children.Add(new TextBlock { Text = "Loudness", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        loudText = new TextBlock { Text = "Measure the sequence to see how loud it is (ITU BS.1770 / EBU R128).", TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 6) };
        loudText.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        LoudnessPanel.Children.Add(loudText);
        var row = new WrapPanel();
        var measure = new Button { Content = "Measure Loudness", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 4),
                                   ToolTip = "Integrated loudness and true peak of the whole sequence, as it will export (before normalization)" };
        measure.Click += async (_, _) =>
        {
            if (ed.Clips.Count == 0) { loudText!.Text = "The sequence is empty."; return; }
            measure.IsEnabled = false;
            var (srcs, master) = (Mixer.Sources(ed, Transitions.Compute(ed), new()).ToArray(), Mixer.MasterOf(ed));   // snapshot on the UI thread
            double end = ed.End;
            var prog = new Progress<double>(p => measure.Content = $"Measuring… {p * 100:0}%");
            try
            {
                var r = await Task.Run(() => Mixer.Measure(srcs, master, 0, end, default, prog));
                var (gain, limit) = Loudness.Normalize(r, -14, -1);
                loudText!.Text = r.Silent ? "No sound in the sequence." :
                    $"Integrated {Fmt(r.Integrated)} LUFS · true peak {Fmt(r.TruePeak)} dBTP\n" +
                    $"Platform presets (−14 LUFS) change it by {(gain >= 0 ? "+" : "")}{Fmt(gain)} dB{(limit ? ", limiting the peaks" : "")}.";
            }
            catch (Exception ex) { loudText!.Text = "Could not measure: " + ex.Message; }
            measure.Content = "Measure Loudness"; measure.IsEnabled = true;
        };
        var match = new Button { Content = "Match Clips", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 0, 4),
                                 ToolTip = "Sets the volume of each selected clip so it measures −14 LUFS on its own (e.g. several speakers)" };
        match.Click += async (_, _) => await MatchLoudnessAsync();
        row.Children.Add(measure); row.Children.Add(match);
        LoudnessPanel.Children.Add(row);
        var hint = new TextBlock { Text = "YouTube, TikTok, Instagram, Facebook, X and LinkedIn export presets normalize to −14 LUFS with peaks under −1 dBTP (Export → Loudness).",
                                   TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        LoudnessPanel.Children.Add(hint);
    }
    static string Fmt(double v) => v.ToString("0.0", CultureInfo.InvariantCulture).Replace('-', '−');

    /// <summary>Clip loudness matching: each selected clip with sound gets the volume that makes it −14 LUFS on its own (one undo step).</summary>
    async Task MatchLoudnessAsync(double target = -14)
    {
        var all = ed.Clips.Where(c => ed.Sel.Contains(c.Id) && Mixer.HasSound(c) && ed.MediaOf(c) is { Offline: false, HasAudio: true }).ToList();
        if (all.Count == 0) { Alert.Show(this, "Select the clips to match first (audio clips, or video clips with their own sound).", "Match Loudness"); return; }
        var clips = all.Where(c => !Anim.IsAnimated(c, AnimProp.Volume)).ToList();     // keyframed volume: the user's own levels — left alone
        int keyed = all.Count - clips.Count;
        var jobs = clips.Where(c => !c.Hold).Select(c => (c, src: AudioCache.Derives(c) ? new AudioOut.Src(AudioCache.EnsureDerived(c, ed.MediaOf(c)!), c.Start, c.End, 0, 1, 0, 0)
                                                                       : new AudioOut.Src(ed.MediaOf(c)!.Path, c.Start, c.End, c.In, 1, 0, 0))).ToList();
        foreach (var j in jobs) AudioCache.Ensure(ed.MediaOf(j.c)!);
        var results = await Task.Run(() => jobs.Select(j => Mixer.Measure(new[] { j.src }, 1, j.c.Start, j.c.End)).ToList());
        int changed = 0, capped = 0;
        ed.Edit(() =>
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                if (results[i].Silent) continue;
                double vol = 100 * Math.Pow(10, (target - results[i].Integrated) / 20);
                if (vol > 400) { vol = 400; capped++; }
                jobs[i].c.Volume = Math.Round(vol, 1); changed++;
            }
        });
        if (loudText != null)
            loudText.Text = $"Matched {changed} clip{(changed == 1 ? "" : "s")} to {Fmt(target)} LUFS"
                            + (capped > 0 ? $"; {capped} needed more than +12 dB and stopped at 400 %" : "")
                            + (keyed > 0 ? $"; {keyed} with volume keyframes left as they are" : "") + ".";
    }
}
