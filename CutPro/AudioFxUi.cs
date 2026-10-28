using System.Windows;
using System.Windows.Controls;

namespace CutPro;

/// <summary>Effect Controls → Audio effects (partial MainWindow): Voice clean-up preset, add / remove / reorder / switch effects, their
/// settings — for a clip, or for a track (the mixer strip's fx button: the chain runs on the track's clips summed, before its fader).
/// Adding and removing replace the list (the mixer thread reads frozen copies — AudioDsp.Snapshot).</summary>
public partial class MainWindow
{
    List<Clip> SoundTargets(Clip c)
    {
        var l = ed.Clips.Where(x => ed.IsSel(x) && Mixer.HasSound(x)).ToList();
        if (!l.Contains(c)) l.Add(c);
        return l;
    }

    void BuildAudioFxInspector(Clip c)
    {
        if (!Mixer.HasSound(c)) return;
        var body = Section("Audio effects", open: c.AudioFx is { Count: > 0 });
        var bar = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        var voice = new Button { Content = "Voice clean-up", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4),
                                 ToolTip = "Rumble cut, a little presence, gentle gate, compressor and de-esser — a good start for speech" };
        voice.Click += (_, _) => { ed.Edit(() => { foreach (var x in SoundTargets(c)) x.AudioFx = AudioDsp.VoicePreset(); }); BuildInspector(); };
        bar.Children.Add(voice);
        bar.Children.Add(AddFxBox(kind => { ed.Edit(() => { foreach (var x in SoundTargets(c)) x.AudioFx = (x.AudioFx ?? new()).Append(AudioDsp.New(kind)).ToList(); }); BuildInspector(); }));
        body.Children.Add(bar);
        if (c.AudioFx is not { Count: > 0 } list)
        {
            body.Children.Add(new TextBlock { Text = "Effects run on this clip's sound before its volume and pan, in the order listed — the same code in playback and the export.",
                                              TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim });
            return;
        }
        FxRows(body, list, l => c.AudioFx = l, a => ed.Edit(a), () => { });
    }

    /// <summary>Audio → Pitch: semitones up / down (the sound is rendered once, like a speed change, and kept in sync), voice character kept or not.
    /// Applies to every selected clip with sound; the slider re-renders once it rests.</summary>
    void BuildPitchRow(Clip c)
    {
        var targets = SoundTargets(c);
        void Later(Action<Clip> f)
        {
            toonPending = () => ed.Edit(() => { foreach (var x in targets) f(x); });
            toonDebounce.Stop(); toonDebounce.Start();
        }
        toonDebounce.Tick -= ToonTick; toonDebounce.Tick += ToonTick;
        Slider("Pitch (semitones)", -12, 12, 0.5, c.Pitch, v => Later(x => x.Pitch = v));
        var fm = new CheckBox { Content = "Keep the voice's character (formants)", IsChecked = c.Formant, Margin = new Thickness(0, 2, 0, 2),
                                ToolTip = "On: a shifted voice still sounds like the same person (no chipmunk / giant). Off: the timbre moves with the pitch" };
        fm.Click += (_, _) => ed.Edit(() => { foreach (var x in targets) x.Formant = fm.IsChecked == true; });
        Inspector.Children.Add(fm);
        if (Math.Abs(c.Pitch) > 1e-9 && ed.MediaOf(c) is { } m && !AudioCache.IsDone(AudioCache.EnsureDerived(c, m)))
            Inspector.Children.Add(new TextBlock { Text = "Rendering the shifted sound…", FontSize = 11, Foreground = Dim });
        // channel map (Premiere's Modify ▸ Audio Channels)
        var ch = new ComboBox { ItemsSource = new[] { "Stereo (as recorded)", "Left channel on both sides", "Right channel on both sides", "Mono (left + right)", "Swap left and right" },
                                SelectedIndex = (int)c.Channels, ToolTip = "A microphone recorded on one channel: play that channel on both sides" };
        ch.SelectionChanged += (_, _) => { ed.Edit(() => { foreach (var x in targets) x.Channels = (ChannelMap)ch.SelectedIndex; }, "Audio Channels"); };
        Row(Inspector, "Channels", ch);
        // clean-up (Enhance Speech)
        var vc = new ComboBox { ItemsSource = new[] { "None", "Reduce background noise", "Isolate the voice" }, SelectedIndex = (int)c.Voice,
                                ToolTip = "Reduce noise: hiss, hum and room tone go down. Isolate the voice: only the voice's range is kept and everything else pushed down (rendered once, like a speed change)" };
        vc.SelectionChanged += (_, _) => { ed.Edit(() => { foreach (var x in targets) x.Voice = (VoiceClean)vc.SelectedIndex; }, "Clean Up Sound"); BuildInspector(); };
        Row(Inspector, "Clean up", vc);
        if (c.Voice != VoiceClean.None)
            Slider("Amount", 0, 100, 1, c.VoiceAmount, v => Later(x => x.VoiceAmount = v));
    }

    ComboBox AddFxBox(Action<string> add)
    {
        var cb = new ComboBox { Width = 150, ToolTip = "Add an effect to the end of the chain", Margin = new Thickness(0, 0, 0, 4) };
        cb.Items.Add(new ComboBoxItem { Content = "Add effect…", IsEnabled = false });
        foreach (var k in AudioDsp.Kinds) cb.Items.Add(new ComboBoxItem { Content = k.Name, Tag = k.Kind, ToolTip = k.Help });
        cb.SelectedIndex = 0;
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem { Tag: string kind }) add(kind); };
        return cb;
    }

    /// <summary>One row group per effect: switch, move up, remove, its settings. setList replaces the list (null = none) inside edit(...);
    /// touch follows a setting change (clips: the slider's own Touch is enough; tracks: the mixer's undo).</summary>
    void FxRows(Panel body, List<AudioEffect> list, Action<List<AudioEffect>?> setList, Action<Action> edit, Action touch)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i]; int idx = i;
            var info = AudioDsp.Info(e.Kind);
            var head = new DockPanel { Margin = new Thickness(0, 8, 0, 2) };
            Button Tiny(string glyph, string tip, Action a)
            {
                var b = new Button { Content = glyph, Style = (Style)FindResource("Icon"), Width = 24, Height = 22, FontSize = 10, ToolTip = tip };
                b.Click += (_, _) => a(); DockPanel.SetDock(b, Dock.Right); head.Children.Add(b); return b;
            }
            Tiny("", "Remove", () => { edit(() => setList(list.Where(x => !ReferenceEquals(x, e)).ToList() is { Count: > 0 } l ? l : null)); BuildInspector(); });
            if (idx > 0) Tiny("", "Move up (earlier in the chain)", () => { edit(() => { var l = list.ToList(); l.RemoveAt(idx); l.Insert(idx - 1, e); setList(l); }); BuildInspector(); });
            if (idx < list.Count - 1) Tiny("", "Move down (later in the chain)", () => { edit(() => { var l = list.ToList(); l.RemoveAt(idx); l.Insert(idx + 1, e); setList(l); }); BuildInspector(); });
            var on = new CheckBox { Content = info.Name, IsChecked = e.On, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, ToolTip = info.Help };
            on.Click += (_, _) => edit(() => e.On = on.IsChecked == true);
            head.Children.Add(on);
            body.Children.Add(head);
            foreach (var p in info.Ps)
            {
                string key = p.Key;
                Slider(p.Label, p.Min, p.Max, p.Step, AudioDsp.Get(e, key), v => { e.P[key] = v; touch(); }, body);
            }
        }
    }

    // ---------------- track effects ----------------
    int? fxTrack; string? fxTrackSel;
    /// <summary>Shows a track's effect chain in Effect Controls (until another clip is selected).</summary>
    void ShowTrackFx(int track)
    {
        fxTrack = track; fxTrackSel = ed.SelectedId;
        ShowDock("Inspector");
        BuildInspector();
    }

    /// <summary>Effect Controls for a track's chain. True when it took the panel.</summary>
    bool BuildTrackFxInspector()
    {
        if (fxTrack is not int track) return false;
        var sm = SubOf(track);                                             // (a submix's effects: the mixer strip's fx)
        if (ed.SelectedId != fxTrackSel || (sm == null && track >= ed.Mix.Channels.Length)) { fxTrack = null; return false; }
        Func<List<AudioEffect>?> get = sm != null ? () => sm.Fx : () => ed.Mix.Channels[track].Fx;
        Action<List<AudioEffect>?> set = sm != null ? l => sm.Fx = l : l => ed.Mix.Channels[track].Fx = l;
        Inspector.Children.Add(new TextBlock { Text = sm != null ? $"Submix effects — {sm.Name}" : $"Track effects — {Tracks.Name(track)}", FontWeight = FontWeights.SemiBold });
        Inspector.Children.Add(new TextBlock { Text = sm != null ? "They run on everything sent to this submix, before its fader and pan. Select a clip to see its own controls." : "They run on the track's clips summed, before its fader and pan; a reverb's tail rings on after the clips. Select a clip to see its own controls.",
                                               Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) });
        void Edit(Action a) { ed.MixBegin(); a(); ed.MixEnd(); ed.RaiseMixChanged(); }
        var bar = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        var room = new Button { Content = "Room reverb", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4), ToolTip = "A small room's reverb — a little space around a dry voice" };
        room.Click += (_, _) => { Edit(() => set((get() ?? new()).Append(AudioDsp.New("Reverb", ("size", 45), ("damp", 50), ("wet", 18))).ToList())); BuildInspector(); };
        var hall = new Button { Content = "Hall reverb", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4), ToolTip = "A large hall — worship music, a sanctuary" };
        hall.Click += (_, _) => { Edit(() => set((get() ?? new()).Append(AudioDsp.New("Reverb", ("size", 85), ("damp", 30), ("pre", 35), ("wet", 30))).ToList())); BuildInspector(); };
        bar.Children.Add(room); bar.Children.Add(hall);
        bar.Children.Add(AddFxBox(kind => { Edit(() => set((get() ?? new()).Append(AudioDsp.New(kind)).ToList())); BuildInspector(); }));
        Inspector.Children.Add(bar);
        if (get() is { Count: > 0 } list) FxRows(Inspector, list, set, Edit, ed.MixTouch);
        else Inspector.Children.Add(new TextBlock { Text = "No effects on this track.", Foreground = Dim, FontSize = 11 });
        return true;
    }
}
