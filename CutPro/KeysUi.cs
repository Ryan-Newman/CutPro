using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>
/// Keyboard shortcuts in the main window (partial MainWindow): every key goes through <see cref="Keymap"/> (Premiere's defaults or the user's
/// own) to a command here; the Settings view lists them with a search box and lets each be changed (press the new keys), added to, removed or
/// reset. Also the timeline's zoom commands and how it follows the playhead while playing.
/// </summary>
public partial class MainWindow
{
    Dictionary<string, Action>? keyActions;
    /// <summary>What each command id does (the palette runs these too).</summary>
    Dictionary<string, Action> KeyActions() => keyActions ??= new()
    {
        ["view.palette"] = () => OpenPalette(),
        ["view.sidebar"] = () => SetSideBar(!sideVisible),
        ["view.inspector"] = () => { if (DockOf("Inspector") is { Zone: "Right" }) SetInspector(!inspVisible); else ShowDock("Inspector"); },
        ["view.fullscreen"] = ToggleFullScreen,
        ["view.maximize"] = () => ToggleMaximizePanel(),
        ["view.history"] = () => ShowDock("History"),
        ["file.import"] = () => Import_Click(this, new RoutedEventArgs()),
        ["file.open"] = () => Open_Click(this, new RoutedEventArgs()),
        ["file.save"] = () => Save_Click(this, new RoutedEventArgs()),
        ["file.export"] = () => Export_Click(this, new RoutedEventArgs()),
        ["file.publish"] = () => OpenPublish(null),
        ["file.collect"] = () => _ = CollectFilesAsync(),
        ["seq.new"] = NewSequence,
        ["edit.undo"] = ed.Undo, ["edit.redo"] = ed.Redo,
        ["edit.cut"] = () => CopyClips(true), ["edit.copy"] = () => CopyClips(false),
        ["edit.paste"] = () => PasteClips(false), ["edit.pasteInsert"] = () => PasteClips(true),
        ["edit.pasteAttr"] = PasteAttributesDialog, ["edit.removeAttr"] = RemoveAttributesDialog,
        ["edit.addEdit"] = () => ed.AddEdit(false), ["edit.addEditAll"] = () => ed.AddEdit(true),
        ["edit.delete"] = () => ed.DeleteSelection(false), ["edit.rippleDelete"] = () => ed.DeleteSelection(true),
        ["edit.selectAll"] = ed.SelectAll,
        ["edit.deselect"] = () => { if (trackCts != null) trackCts.Cancel(); else if (Mo.Tool != ObjectTool.None) SetObjectTool(ObjectTool.None); else ed.ClearSelection(); },
        ["edit.link"] = ed.ToggleLink,
        ["clip.group"] = () => SetTask(ed.GroupSelection() ? "Grouped — the clips are selected and moved together" : "Select two or more clips to group them", 2500),
        ["clip.ungroup"] = () => SetTask(ed.UngroupSelection() ? "Ungrouped" : "No grouped clip is selected", 2500),
        ["edit.markIn"] = () => ed.SetMark(true), ["edit.markOut"] = () => ed.SetMark(false), ["edit.clearMarks"] = ed.ClearMarks,
        ["edit.lift"] = () => { Pause(); ed.LiftExtract(false); }, ["edit.extract"] = () => { Pause(); ed.LiftExtract(true); },
        ["edit.trimPrev"] = () => { Pause(); ed.RippleTrim(true); }, ["edit.trimNext"] = () => { Pause(); ed.RippleTrim(false); },
        ["src.insert"] = () => SourceEdit(true), ["src.overwrite"] = () => SourceEdit(false), ["src.matchFrame"] = MatchFrame,
        ["clip.reveal"] = () => RevealInProject(), ["clip.revealFile"] = RevealInExplorer,
        ["clip.speed"] = () => SpeedDialog(),
        ["file.autosaves"] = AutoSaveVersions, ["file.exportEdl"] = () => ExportInterchange(false), ["file.exportOtio"] = () => ExportInterchange(true),
        ["bin.new"] = () => { ShowSideView("Project"); NewBinHere(); }, ["view.transcript"] = () => ShowDock("Transcript"),
        ["clip.replace"] = ReplaceFromBin, ["clip.fitFill"] = FitToFill, ["clip.scenes"] = SceneDetectDialog, ["clip.sync"] = () => _ = SyncSelected(),
        ["clip.beatCut"] = CutToBeat, ["clip.savePreset"] = SavePresetDialog, ["clip.removeBg"] = () => _ = RemoveBackground(), ["clip.blurFaces"] = () => _ = BlurFacesDialog(),
        ["color.auto"] = () => { if (ed.Selected is { } c) AutoGrade(c); }, ["color.match"] = ColorMatchDialog,
        ["audio.beats"] = BeatsDialog, ["audio.ducking"] = DuckingDialog, ["audio.voiceover"] = () => VoiceOverDialog(), ["seq.reframeZoom"] = ReframeZoomDialog,
        ["trans.video"] = () => ApplyTransitions(true, false), ["trans.audio"] = () => ApplyTransitions(false, true), ["trans.both"] = () => ApplyTransitions(true, true),
        ["marker.add"] = AddMarkerAtPlayhead, ["marker.addClip"] = AddClipMarkerAtPlayhead, ["marker.edit"] = EditMarkerAtPlayhead,
        ["marker.next"] = () => { if (ed.NextMarker(ed.Time) is double t) ed.Seek(t); },
        ["marker.prev"] = () => { if (ed.PrevMarker(ed.Time) is double t) ed.Seek(t); },
        ["marker.clear"] = ed.ClearMarkers, ["marker.chapters"] = CopyChapters,
        ["gap.next"] = () => GoToGap(true, false), ["gap.prev"] = () => GoToGap(false, false),
        ["gap.nextTrack"] = () => GoToGap(true, true), ["gap.prevTrack"] = () => GoToGap(false, true),
        ["play.toggle"] = TogglePlay, ["play.stop"] = Pause, ["play.forward"] = () => Shuttle(1), ["play.back"] = () => Shuttle(-1),
            ["tl.audioScrub"] = () => { S.AudioScrub = !S.AudioScrub; AppSettings.Save(); SetTask(S.AudioScrub ? "Audio scrubbing on" : "Audio scrubbing off", 1500); },
            ["tl.audioUnits"] = ToggleAudioUnits,
            ["view.proxies"] = () => SetProxies(!Proxies.Use),
        ["play.prevFrame"] = () => Step(-1.0 / TimeUtil.Fps), ["play.nextFrame"] = () => Step(1.0 / TimeUtil.Fps),
        ["play.back1s"] = () => Step(-1), ["play.fwd1s"] = () => Step(1),
        ["play.prevEdit"] = () => { if (ed.PrevEdit(ed.Time) is double pe) ed.Seek(pe); },
        ["play.nextEdit"] = () => { if (ed.NextEdit(ed.Time) is double ne) ed.Seek(ne); },
        ["play.start"] = () => ed.Seek(0), ["play.end"] = () => ed.Seek(ed.End),
        ["tool.select"] = () => SetTool(false), ["tool.razor"] = () => SetTool(true),
            ["tool.ripple"] = () => SetTool(TrimTool.Ripple), ["tool.roll"] = () => SetTool(TrimTool.Roll), ["tool.rate"] = () => SetTool(TrimTool.Rate),
            ["tool.slip"] = () => SetTool(TrimTool.Slip), ["tool.slide"] = () => SetTool(TrimTool.Slide),
        ["tl.snap"] = () => SnapChk.IsChecked = SnapChk.IsChecked != true,
        ["tl.zoomIn"] = () => Tl.ZoomAround(1.5, ed.Time), ["tl.zoomOut"] = () => Tl.ZoomAround(1 / 1.5, ed.Time),
        ["tl.zoomSeq"] = () => Tl.ZoomTo(0, Math.Max(ed.End, 1)), ["tl.zoomSel"] = ZoomToSelection,
    };
    /// <summary>Runs a command by id (keys, palette, menus).</summary>
    void RunCommand(string id) { if (KeyActions().TryGetValue(id, out var a)) a(); }
    /// <summary>The menus' gesture text for a command (the first of its shortcuts).</summary>
    static string K(string id) => Keymap.Text(id);

    void Window_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (VoiceKey(e.Key) || BinRatingKey(e.Key) || HandleKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers)) e.Handled = true;
    }
    /// <summary>A key press with its modifiers → its command (true = used). Tests call it with the modifiers they want.</summary>
    bool HandleKey(Key key, ModifierKeys mods)
    {
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        var gesture = Keymap.Gesture(key, mods);
        var id = gesture == null ? null : Keymap.Find(gesture);
        // shell shortcuts (VS Code): work even while typing in a field
        if (id != null && Keymap.Get(id)!.Global) { RunCommand(id); return true; }
        if (Palette.Visibility == Visibility.Visible) return false;           // the palette handles its own keys
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem) return false;
        if (Kfe.IsVisible && Kfe.IsKeyboardFocused && Kfe.HandleKey(key, ctrl)) return true;
        if (SourceKey(key, ctrl, shift)) return true;                        // the source monitor has the keyboard (clicked last)
        if (key is Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) { Tl.UpdateHover(); return false; }
        if (id == null) return false;
        RunCommand(id);
        return true;
    }

    /// <summary>While playing: Page = the view jumps a page when the playhead reaches the right edge (Premiere's default), Smooth = it keeps the
    /// playhead in the middle, None = it stays.</summary>
    void FollowPlayhead()
    {
        switch (S.PlaybackScroll)
        {
            case "None": return;
            case "Smooth":
                double want = ed.Time * Tl.Pps - Tl.Viewport / 2;
                if (Math.Abs(want - Tl.ScrollX) >= 1) Tl.ScrollX = want;      // (clamped at both ends)
                return;
            default:
                double x = Tl.T2X(ed.Time);
                if (x > Tl.ActualWidth - 30 || x < TimelineView.HeaderW) Tl.ScrollX = ed.Time * Tl.Pps - 20;
                return;
        }
    }
    void SetPlaybackScroll(string mode) { S.PlaybackScroll = mode; AppSettings.Save(); SetTask($"Timeline scrolling while playing: {mode}", 2000); if (ScrollModeCombo != null) ScrollModeCombo.SelectedItem = mode; }

    void ZoomToSelection()
    {
        var sel = ed.Clips.Where(ed.IsSel).ToList();
        if (sel.Count > 0) Tl.ZoomTo(sel.Min(c => c.Start), sel.Max(c => c.End));
        else if (ed.MarkIn is double a && ed.MarkOut is double b && b > a) Tl.ZoomTo(a, b);
        else SetTask("Select clips (or mark In and Out) to zoom to them", 2500);
    }

    /// <summary>Sequence ▸ Go to Gap: in the sequence (no track has a clip), or on the selected clip's track (V1 when nothing is selected).</summary>
    void GoToGap(bool next, bool inTrack)
    {
        int? track = inTrack ? ed.Selected?.Track ?? Tracks.V1 : null;
        if ((next ? ed.NextGap(ed.Time, track) : ed.PrevGap(ed.Time, track)) is double t) { ed.Seek(t); Tl.ShowRow(track ?? Tracks.V1); EnsureTimeVisible(t); }
        else SetTask(next ? "No gap after the playhead" : "No gap before the playhead", 2000);
    }
    /// <summary>Scrolls the timeline so time t shows (jumps from the keyboard / menus).</summary>
    void EnsureTimeVisible(double t)
    {
        double x = Tl.T2X(t);
        if (x < TimelineView.HeaderW + 10 || x > Tl.ActualWidth - 30) Tl.ScrollX = Math.Max(0, t * Tl.Pps - Tl.Viewport / 3);
    }

    // ---------------- Settings: keyboard shortcuts ----------------
    TextBox? shortcutFilter;
    ComboBox? ScrollModeCombo;
    /// <summary>The keyboard shortcuts list (Settings view): grouped, searchable by name or key; click a row to change its keys.</summary>
    void BuildShortcuts()
    {
        if (shortcutFilter == null)
        {
            Keymap.Changed += BuildShortcuts;
            shortcutFilter = new TextBox { Margin = new Thickness(0, 0, 0, 6), ToolTip = "Search commands or keys" };
            System.Windows.Automation.AutomationProperties.SetName(shortcutFilter, "Search keyboard shortcuts");
            shortcutFilter.TextChanged += (_, _) => FillShortcuts();
            var reset = new Button { Content = "Reset all to Premiere defaults", Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 2, 8, 2) };
            reset.Click += (_, _) => { if (Alert.Confirm(this, "Every keyboard shortcut goes back to its default.", "Reset shortcuts", "Reset")) Keymap.ResetAll(); };
            var host = (Panel)ShortcutList.Parent;
            int at = host.Children.IndexOf(ShortcutList);
            host.Children.Insert(at, reset); host.Children.Insert(at, shortcutFilter);
            // timeline behaviour next to the shortcuts
            ScrollModeCombo = new ComboBox { Width = 90, ItemsSource = new[] { "Page", "Smooth", "None" }, SelectedItem = S.PlaybackScroll };
            System.Windows.Automation.AutomationProperties.SetName(ScrollModeCombo, "Timeline scrolling while playing");
            ScrollModeCombo.SelectionChanged += (_, _) => { if (ScrollModeCombo.SelectedItem is string m && m != S.PlaybackScroll) SetPlaybackScroll(m); };
            var scroll = PrefRow("Timeline scrolling while playing", ScrollModeCombo); scroll.Margin = new Thickness(0, 4, 0, 10);
            host.Children.Insert(host.Children.IndexOf(shortcutFilter) - 1, scroll);
            // language and interface size (accessibility), above the shortcuts heading
            var lang = langCombo = new ComboBox { Width = 120, ItemsSource = Loc.Languages.Select(l => l.name).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(Loc.Languages, l => l.code == S.Language)) };
            System.Windows.Automation.AutomationProperties.SetName(lang, "Language");
            lang.SelectionChanged += (_, _) => { if (lang.SelectedIndex >= 0) SetLanguage(Loc.Languages[lang.SelectedIndex].code); };
            var size = new ComboBox { Width = 90, ItemsSource = UiScales.Select(x => $"{x * 100:0} %").ToList(), SelectedIndex = Math.Max(0, Array.IndexOf(UiScales, S.TextScale)) };
            System.Windows.Automation.AutomationProperties.SetName(size, "Interface size");
            size.SelectionChanged += (_, _) => { if (size.SelectedIndex >= 0) SetUiScale(UiScales[size.SelectedIndex]); };
            int at2 = host.Children.IndexOf(scroll);
            host.Children.Insert(at2, PrefRow("Interface size", size));
            host.Children.Insert(at2, PrefRow("Language", lang));
        }
        FillShortcuts();
    }
    static Grid PrefRow(string label, FrameworkElement control)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(control, 1); row.Children.Add(control);
        return row;
    }

    // ---------------- language / interface size ----------------
    static readonly double[] UiScales = { 1, 1.15, 1.3, 1.5 };
    ComboBox? langCombo;
    void InitLoc()
    {
        Loc.Use(S.Language);
        ApplyUiScale();
        System.Windows.Automation.AutomationProperties.SetName(Pv, "Program monitor");
        Loaded += (_, _) => { Loc.Apply(this); FitTitleBar(); };
        SizeChanged += (_, _) => FitTitleBar();
    }
    void SetLanguage(string code)
    {
        if (S.Language == code && Loc.Current == code) return;
        S.Language = code; AppSettings.Save();
        if (langCombo != null) langCombo.SelectedIndex = Math.Max(0, Array.FindIndex(Loc.Languages, l => l.code == code));
        Loc.Use(code);
        Loc.Apply(this);
        foreach (var d in docks) if (d.Win != null) Loc.Apply(d.Win);
        FillShortcuts(); BuildInspector(); histStale = true; RefreshHistory();
        UpdateLayout(); FitPanelTools(); FitTitleBar();
        SetTask(Loc.T("Language") + ": " + Loc.Languages.First(l => l.code == Loc.Current).name, 2500);
    }
    /// <summary>The menu bar keeps clear of the command center: the command center narrows (down to 180 px) when the menu titles need the room
    /// (longer words in other languages, a narrow window).</summary>
    void FitTitleBar()
    {
        if (!IsLoaded) return;
        MenuBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double total = RootGrid.ActualWidth, side = Math.Max(MenuBar.DesiredSize.Width, 150) + 12;   // (the right side: export + window buttons ≈ the same)
        CommandCenterBtn.Width = Math.Clamp(total - 2 * side, 180, 440);
    }
    /// <summary>Interface size (accessibility): the whole window's layout scaled (100 … 150 %).</summary>
    void SetUiScale(double s) { S.TextScale = s; AppSettings.Save(); ApplyUiScale(); }
    void ApplyUiScale()
    {
        double s = UiScales.Contains(S.TextScale) ? S.TextScale : 1;
        RootGrid.LayoutTransform = s == 1 ? Transform.Identity : new ScaleTransform(s, s);
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = (36 + ThemeManager.Current.Gap) * s;
    }

    void FillShortcuts()
    {
        ShortcutList.Children.Clear();
        var q = shortcutFilter?.Text.Trim() ?? "";
        string? group = null;
        foreach (var c in Keymap.All.OrderBy(c => c.Group).ThenBy(c => c.Name))
        {
            var keys = Keymap.Of(c.Id);
            if (q.Length > 0 && !c.Label.Contains(q, StringComparison.OrdinalIgnoreCase) && !Loc.T(c.Label).Contains(q, StringComparison.OrdinalIgnoreCase) && !keys.Any(k => k.Contains(q, StringComparison.OrdinalIgnoreCase))) continue;
            if (c.Group != group)
            {
                group = c.Group;
                var h = new TextBlock { Text = Loc.T(group), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) };
                h.SetResourceReference(TextBlock.ForegroundProperty, "B.Heading");
                ShortcutList.Children.Add(h);
            }
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = new TextBlock { Text = Loc.T(c.Name), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
            var chips = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var k in keys) chips.Children.Add(Chip(k));
            if (keys.Length == 0) { var none = new TextBlock { Text = "—", Opacity = 0.5, Margin = new Thickness(0, 0, 4, 0) }; chips.Children.Add(none); }
            Grid.SetColumn(chips, 1);
            g.Children.Add(t); g.Children.Add(chips);
            var row = new Button { Content = g, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4, 2, 2, 2), BorderThickness = new Thickness(0),
                                   ToolTip = $"{c.Label} — click to change its keys" + (AppSettings.Current.Shortcuts.ContainsKey(c.Id) ? " (changed from the default)" : "") };
            row.SetResourceReference(StyleProperty, "Flat");
            System.Windows.Automation.AutomationProperties.SetName(row, $"{c.Label}: {(keys.Length == 0 ? "no shortcut" : string.Join(", ", keys))}");
            row.Click += (_, _) => EditShortcut(c);
            ShortcutList.Children.Add(row);
        }
    }
    static Border Chip(string k)
    {
        var chip = new Border { CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(4, 0, 0, 0),
                                Child = new TextBlock { Text = k, FontSize = 11, FontFamily = new FontFamily("Segoe UI Semibold") } };
        chip.SetResourceReference(Border.BackgroundProperty, "B.Input.Bg");
        chip.SetResourceReference(Border.BorderBrushProperty, "B.Input.Border");
        return chip;
    }

    /// <summary>Change a command's keys: press the new combination in the box (Esc / Enter alone close the dialog), then Replace or Add.</summary>
    void EditShortcut(KeyCmd c)
    {
        string? pressed = null;
        var box = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = false, Text = "Press the new keys…", Margin = new Thickness(0, 10, 0, 4), Padding = new Thickness(6, 4, 6, 4), FontSize = 13 };
        System.Windows.Automation.AutomationProperties.SetName(box, "Press the new key combination");
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        box.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Escape or Key.Return && Keyboard.Modifiers == ModifierKeys.None) return;     // (closes / confirms the dialog)
            var gst = Keymap.Gesture(key, Keyboard.Modifiers);
            e.Handled = true;
            if (gst == null) return;                                                 // (a modifier on its own: wait for the key)
            pressed = gst; box.Text = gst;
            note.Text = Keymap.Conflict(gst, c.Id) is { } other ? $"Also used by “{other.Label}” — it moves here." : "Not used by another command.";
        };
        box.Loaded += (_, _) => box.Focus();
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Now: " + (Keymap.Of(c.Id) is { Length: > 0 } now ? string.Join(", ", now) : "no shortcut") + "   ·   Default: " +
                                                  (c.Defaults.Length > 0 ? string.Join(", ", c.Defaults) : "none"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box); panel.Children.Add(note);
        int r = Alert.Show(this, c.Label, "Keyboard shortcut", AlertKind.Question, new[] { "Replace", "Add", "Remove all", "Reset", "Cancel" }, panel);
        switch (r)
        {
            case 0 when pressed != null: Keymap.Set(c.Id, new[] { pressed }); break;
            case 1 when pressed != null: Keymap.Set(c.Id, Keymap.Of(c.Id).Append(pressed)); break;
            case 2: Keymap.Set(c.Id, Array.Empty<string>()); break;
            case 3: Keymap.Reset(c.Id); break;
        }
    }
}
