using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace CutPro;

/// <summary>Publishing glue: which platforms auto-upload, the per-project post details, the upload run.</summary>
public static class Publishing
{
    /// <summary>Platforms set to upload automatically after every export (connected API platforms only).</summary>
    public static List<PlatformDef> AutoTargets() => Platforms.All.Where(p => p.Upload == "api" && Accounts.Get(p.Id).AutoUpload && Accounts.Get(p.Id).Connected).ToList();

    /// <summary>The post's values for a platform: the project's details over the account's defaults over the field defaults.</summary>
    public static Dictionary<string, string> Values(Editor ed, PlatformDef p)
    {
        var v = p.Fields.ToDictionary(f => f.Id, f => f.Default);
        foreach (var (k, val) in Accounts.Get(p.Id).Defaults) v[k] = val;
        if (ed.Publish.TryGetValue(p.Id, out var mine)) foreach (var (k, val) in mine) v[k] = val;
        return v;
    }

    /// <summary>The field that carries the hashtags on this platform (description / caption / text).</summary>
    public static string? HashtagField(PlatformDef p) => p.HashtagWhere switch
    {
        "tags" => null,
        _ => p.Fields.FirstOrDefault(f => f.Id is "description" or "caption" or "text")?.Id,
    };

    public static string MainText(PlatformDef p, IReadOnlyDictionary<string, string> v)
    {
        var parts = new List<string>();
        foreach (var id in new[] { "title", "caption", "text", "description" }) if (v.TryGetValue(id, out var s) && s.Length > 0 && p.Fields.Any(f => f.Id == id)) parts.Add(s);
        return string.Join("\n\n", parts);
    }

    public static double Seconds(string file)
    {
        var ff = Exporter.FindFfmpeg();
        if (ff == null) return 0;
        var fp = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe");
        if (!File.Exists(fp)) return 0;
        try
        {
            var psi = new ProcessStartInfo(fp, $"-v error -show_entries format=duration -of csv=p=0 \"{file}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var pr = Process.Start(psi)!; var o = pr.StandardOutput.ReadToEnd(); pr.WaitForExit();
            return double.TryParse(o.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
        catch { return 0; }
    }
}

public partial class MainWindow
{
    /// <summary>The last full analysis (speech + scenes) — forgotten once the timeline has changed since.</summary>
    internal ContentInfo? LastAnalysis
    {
        get => lastAnalysis != null && analysisSig == TimelineSig() ? lastAnalysis : null;
        set { lastAnalysis = value; analysisSig = TimelineSig(); }
    }
    ContentInfo? lastAnalysis; string? analysisSig;
    string TimelineSig() => string.Join(";", ed.Clips.OrderBy(c => c.Start).Select(c => FormattableString.Invariant($"{c.MediaId}|{c.Start:0.###}|{c.Dur:0.###}|{c.In:0.###}|{c.Text}|{c.Track}|{c.Speed}|{c.Reverse}|{c.Hold}")))
                            + $"|{Editor.OutW}x{Editor.OutH}";
    internal string ProjectTitle => ProjectName();

    /// <summary>Opens the Publish window (optionally on one platform, with a file).</summary>
    void OpenPublish(string? file, string? platform = null)
    {
        var w = new PublishWindow(this, ed, file ?? lastExportPath, platform) { Owner = this };
        Alert.Hook(w);
        w.ShowDialog();
    }

    /// <summary>Uploads a file to platforms (after an export with auto-upload on, or from the Publish window). Fields left
    /// empty are filled with suggestions first; a post that doesn't pass the platform's checks opens the Publish window.</summary>
    internal async Task PublishFileAsync(string file, List<PlatformDef> targets, bool automatic)
    {
        if (automatic)
        {
            ContentInfo? info = LastAnalysis;
            foreach (var p in targets)
            {
                var v = Publishing.Values(ed, p);
                bool empty = p.Fields.Where(f => f.Kind is FieldKind.Text or FieldKind.Multiline && f.Max > 0 && f.Id != "playlist" && f.Id != "password" && f.Id != "location" && f.Id != "link")
                                     .All(f => string.IsNullOrWhiteSpace(v.GetValueOrDefault(f.Id)));
                if (!empty) continue;
                info ??= LastAnalysis = await Analyzer.AnalyzeAsync(ed, ProjectName(), null, CancellationToken.None);
                var s = Captions.Suggest(info, p);
                if (!ed.Publish.TryGetValue(p.Id, out var mine)) ed.Publish[p.Id] = mine = new();
                foreach (var (k, val) in s.Fields) mine[k] = val;
            }
            double secs = Publishing.Seconds(file); long bytes = new FileInfo(file).Length;
            var bad = targets.Select(p => (p, errs: Platforms.Validate(p, Publishing.Values(ed, p), secs, bytes, Editor.W / (double)Editor.H))).Where(x => x.errs.Count > 0).ToList();
            if (bad.Count > 0)
            {
                Alert.Show(this, string.Join("\n", bad.Select(b => $"{b.p.Name}: {string.Join(" ", b.errs)}")), "Check the post before it uploads", AlertKind.Warning);
                OpenPublish(file, bad[0].p.Id);
                return;
            }
        }
        var cts = new CancellationTokenSource();
        var dlg = new Alert.ProgressDialog(this, "Uploading " + Path.GetFileName(file), "Starting…", () => cts.Cancel());
        var results = new List<string>();
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var p = targets[i];
                dlg.Text = $"{p.Name} ({i + 1} of {targets.Count})…"; dlg.Value = 0;
                try
                {
                    string link = await Uploaders.UploadAsync(p, file, Publishing.Values(ed, p),
                        new Progress<double>(x => { dlg.Value = x; dlg.Text = $"{p.Name} ({i + 1} of {targets.Count}) — {x * 100:0}%"; }), cts.Token);
                    results.Add($"✓ {p.Name}: {link}");
                }
                catch (OperationCanceledException) { results.Add($"– {p.Name}: cancelled"); break; }
                catch (Exception ex) { results.Add($"✗ {p.Name}: {ex.Message}"); }
            }
        }
        finally { dlg.Close(); }
        Alert.Show(this, string.Join("\n\n", results), results.All(r => r.StartsWith("✓")) ? "Published" : "Publishing finished with problems",
                   results.All(r => r.StartsWith("✓")) ? AlertKind.Info : AlertKind.Warning);
    }

    string ProjectName() => projectPath != null ? Path.GetFileNameWithoutExtension(projectPath) : "Untitled";
    internal string? ProjectFolder() => projectPath != null ? Path.GetDirectoryName(projectPath) : null;

    /// <summary>Accounts & upload settings: your developer app's credentials per platform, connect / disconnect, auto-upload.</summary>
    internal void AccountsDialog(string? focus)
    {
        var form = new StackPanel { Width = 500 };
        var scroll = new ScrollViewer { Content = form, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) };
        FrameworkElement? focusEl = null;
        foreach (var p in Platforms.All)
        {
            var a = Accounts.Get(p.Id);
            var box = new StackPanel();
            var card = new Border { Child = box, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 10), Margin = new Thickness(0, 0, 8, 8) };
            card.SetResourceReference(Border.BorderBrushProperty, p.Id == focus ? "B.Accent" : "B.Border");
            if (p.Id == focus) focusEl = card;
            var head = new DockPanel();
            var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 8, 0), Fill = (Brush)new BrushConverter().ConvertFromString(p.Color.Replace("#FF", "#"))! };
            var name = new TextBlock { Text = p.Name, FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg");
            var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontSize = 12 };
            void Status()
            {
                status.Text = p.Upload == "intent" ? "no connection needed" : a.Connected ? $"✓ {a.UserName}" : "not connected";
                status.SetResourceReference(TextBlock.ForegroundProperty, a.Connected ? "B.Accent" : "B.Fg.Dim");
            }
            Status();
            head.Children.Add(dot); head.Children.Add(name); head.Children.Add(status);
            box.Children.Add(head);
            var setup = new TextBlock { Text = p.Setup, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 4) };
            setup.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
            box.Children.Add(setup);
            if (p.Upload == "intent") { form.Children.Add(card); continue; }
            Grid Field(string label, Control c)
            {
                var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) }); g.ColumnDefinitions.Add(new ColumnDefinition());
                var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; l.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
                Grid.SetColumn(c, 1); g.Children.Add(l); g.Children.Add(c); box.Children.Add(g); return g;
            }
            var id = new TextBox { Text = a.ClientId }; var secret = new PasswordBox { Password = a.ClientSecret };
            id.TextChanged += (_, _) => { a.ClientId = id.Text.Trim(); Accounts.Save(); };
            secret.PasswordChanged += (_, _) => { a.ClientSecret = secret.Password.Trim(); Accounts.Save(); };
            Field(p.Auth?.ClientIdParam == "client_key" ? "Client key" : "Client ID / App ID", id);
            Field("Client secret", secret);
            var row = new WrapPanel { Margin = new Thickness(110, 4, 0, 0) };
            var connect = new Button { Content = a.Connected ? "Reconnect" : "Connect…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
            connect.SetResourceReference(StyleProperty, "Primary");
            var disconnect = new Button { Content = "Disconnect", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), IsEnabled = a.Connected };
            var portal = new Button { Content = "Developer portal ↗", Padding = new Thickness(10, 3, 10, 3) };
            portal.Click += (_, _) => Process.Start(new ProcessStartInfo(p.DevPortal) { UseShellExecute = true });
            connect.Click += async (_, _) =>
            {
                connect.IsEnabled = false; status.Text = "waiting for the browser…";
                try { await Accounts.ConnectAsync(p, CancellationToken.None); }
                catch (Exception ex) { Alert.Show(this, ex.Message, $"Couldn't connect {p.Name}", AlertKind.Error); }
                finally { connect.IsEnabled = true; connect.Content = a.Connected ? "Reconnect" : "Connect…"; disconnect.IsEnabled = a.Connected; Status(); }
            };
            disconnect.Click += (_, _) => { Accounts.Disconnect(p.Id); disconnect.IsEnabled = false; connect.Content = "Connect…"; Status(); };
            row.Children.Add(connect); row.Children.Add(disconnect); row.Children.Add(portal);
            box.Children.Add(row);
            var auto = new CheckBox { Content = "Upload automatically after every export", IsChecked = a.AutoUpload, Margin = new Thickness(110, 8, 0, 0) };
            auto.Click += (_, _) => { a.AutoUpload = auto.IsChecked == true; Accounts.Save(); };
            box.Children.Add(auto);
            var note = new TextBlock { Text = p.Notes, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(110, 6, 0, 0) };
            note.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
            box.Children.Add(note);
            form.Children.Add(card);
        }
        if (focusEl != null) scroll.Loaded += (_, _) => focusEl.BringIntoView();
        Alert.Show(this, $"Each platform needs your own (free) developer app — the platforms don't allow apps to share one. Sign-in happens in your browser; tokens are stored encrypted for your Windows account only. Redirect URI for every app: http://127.0.0.1:{Accounts.Port}/callback (Facebook/Instagram: http://localhost:{Accounts.Port}/callback).",
                   "Accounts & upload settings", AlertKind.Info, new[] { "Done" }, scroll);
    }
}

/// <summary>The Publish window: one form per platform with its real fields, suggestions from the video's contents, hashtag
/// chips, live limits, connection status, and upload.</summary>
sealed class PublishWindow : Window
{
    readonly MainWindow main; readonly Editor ed;
    string? file;
    string current;
    readonly StackPanel list = new(), form = new();
    readonly TextBlock fileText = new(), status = new(), analysis = new();
    readonly ProgressBar bar = new() { Height = 4, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    readonly Button upload = new() { Padding = new Thickness(14, 5, 14, 5) };
    readonly Border root;
    readonly Dictionary<string, CheckBox> checks = new();
    CancellationTokenSource? analyzeCts;

    Dictionary<string, string> Post(string id) { if (!ed.Publish.TryGetValue(id, out var d)) ed.Publish[id] = d = new(); return d; }
    HashSet<string> Targets
    {
        get => Post("_").GetValueOrDefault("targets", "youtube").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        set => Post("_")["targets"] = string.Join(",", value);
    }

    public PublishWindow(MainWindow main, Editor ed, string? file, string? platform)
    {
        this.main = main; this.ed = ed; this.file = file != null && File.Exists(file) ? file : null;
        current = platform ?? Targets.FirstOrDefault() ?? "youtube";
        if (platform != null) { var t = Targets; t.Add(platform); Targets = t; }
        Title = "Publish"; Width = 980; Height = 720; MinWidth = 820; MinHeight = 560;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 13; UseLayoutRounding = true;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 44, GlassFrameThickness = new Thickness(-1),
                                                                    ResizeBorderThickness = new Thickness(6), CornerRadius = new CornerRadius(8), UseAeroCaptionButtons = false });
        root = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        root.SetResourceReference(Border.BackgroundProperty, "B.Glass.Tint");
        root.SetResourceReference(Border.BorderBrushProperty, "B.Glass.Edge");
        Content = root;
        SourceInitialized += (_, _) => AlertWindow.Backdrop(this, root);
        Build();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    static T Themed<T>(T e, string key, DependencyProperty? prop = null) where T : FrameworkElement { e.SetResourceReference(prop ?? TextBlock.ForegroundProperty, key); return e; }

    void Build()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        // title bar
        var head = new DockPanel { Margin = new Thickness(16, 0, 8, 0) };
        Grid.SetColumnSpan(head, 2);
        var close = new Button { Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Width = 40, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        close.SetResourceReference(StyleProperty, "Icon");
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(Themed(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }, "B.Accent"));
        heading.Children.Add(Themed(new TextBlock { Text = "Publish", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }, "B.Fg"));
        head.Children.Add(heading);
        g.Children.Add(head);
        // video row
        var vrow = new DockPanel { Margin = new Thickness(16, 0, 16, 10) };
        Grid.SetRow(vrow, 1); Grid.SetColumnSpan(vrow, 2);
        var choose = new Button { Content = "Choose video…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        choose.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "Video|*.mp4;*.mov;*.webm;*.mkv" };
            if (dlg.ShowDialog(this) == true) { file = dlg.FileName; RefreshFile(); }
        };
        var export = new Button { Content = "Export first…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Export with the preset for the selected platform" };
        export.Click += async (_, _) =>
        {
            Hide();
            var preset = ExportSettings.Presets.FirstOrDefault(p => p.Name.StartsWith(PresetFor(current))) ?? ExportSettings.Presets[0];
            var f = await main.ExportForPublishAsync(preset);
            if (f != null) { file = f; RefreshFile(); }
            ShowDialogAgain();
        };
        DockPanel.SetDock(export, Dock.Right); DockPanel.SetDock(choose, Dock.Right);
        vrow.Children.Add(export); vrow.Children.Add(choose);
        Themed(fileText, "B.Fg.Dim"); fileText.VerticalAlignment = VerticalAlignment.Center; fileText.TextTrimming = TextTrimming.CharacterEllipsis;
        vrow.Children.Add(fileText);
        g.Children.Add(vrow);
        // platform list
        var left = new DockPanel { Margin = new Thickness(12, 0, 6, 12) };
        Grid.SetRow(left, 2);
        var analyze = new Button { Content = "✨ Analyze video", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 10, 0, 0),
                                   ToolTip = "Listen to the speech and look at the scenes (on this PC), then suggest titles, descriptions and hashtags" };
        analyze.SetResourceReference(StyleProperty, "Primary");
        analyze.Click += async (_, _) => await AnalyzeAsync(auto: false);
        var accounts = new Button { Content = "Accounts & settings…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 6, 0, 0) };
        accounts.Click += (_, _) => { main.AccountsDialog(current); RefreshList(); ShowPlatform(current); };
        var bottom = new StackPanel(); bottom.Children.Add(analyze); bottom.Children.Add(accounts);
        analysis.TextWrapping = TextWrapping.Wrap; analysis.FontSize = 11.5; analysis.Margin = new Thickness(2, 8, 2, 0); Themed(analysis, "B.Fg.Dim");
        bottom.Children.Add(analysis);
        DockPanel.SetDock(bottom, Dock.Bottom);
        left.Children.Add(bottom);
        left.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        g.Children.Add(left);
        // form
        var formBox = new Border { CornerRadius = new CornerRadius(6), Margin = new Thickness(6, 0, 12, 12), Padding = new Thickness(14, 10, 14, 10) };
        formBox.SetResourceReference(Border.BackgroundProperty, "B.Glass.Footer");
        formBox.Child = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(formBox, 2); Grid.SetColumn(formBox, 1);
        g.Children.Add(formBox);
        // footer
        var foot = new Border { Padding = new Thickness(16, 10, 16, 12), CornerRadius = new CornerRadius(0, 0, 8, 8) };
        foot.SetResourceReference(Border.BackgroundProperty, "B.Glass.Footer");
        Grid.SetRow(foot, 3); Grid.SetColumnSpan(foot, 2);
        var fp = new DockPanel();
        var closeB = new Button { Content = "Close", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
        closeB.Click += (_, _) => Close();
        upload.SetResourceReference(StyleProperty, "Primary");
        upload.Click += async (_, _) => await UploadAsync();
        var copy = new Button { Content = "Copy text", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Copy this platform's caption / description with hashtags" };
        copy.Click += (_, _) => { var p = Platforms.Get(current); Clipboard.SetText(Publishing.MainText(p, Publishing.Values(ed, p))); status.Text = $"Copied the {p.Name} text"; };
        foreach (var b in new[] { closeB, upload, copy }) DockPanel.SetDock(b, Dock.Right);
        fp.Children.Add(closeB); fp.Children.Add(upload); fp.Children.Add(copy);
        var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Themed(status, "B.Fg.Dim"); status.TextTrimming = TextTrimming.CharacterEllipsis;
        st.Children.Add(status); st.Children.Add(bar);
        fp.Children.Add(st);
        foot.Child = fp;
        g.Children.Add(foot);
        root.Child = g;
        RefreshFile(); RefreshList(); ShowPlatform(current);
        if (main.LastAnalysis is { } a) ShowAnalysis(a);
        Loaded += async (_, _) => await AutoFillAsync();
    }

    /// <summary>On opening: every platform's post is filled in at once from the titles and names; then, when the speech and
    /// scene models are on this PC, the video is analysed and the suggestions improve by themselves.</summary>
    async Task AutoFillAsync()
    {
        if (main.LastAnalysis is { } full) { Fill(full); Refresh(); return; }
        Fill(await main.QuickAnalysisAsync()); Refresh();
        if (Whisper.Present && ClipTagger.Present) await AnalyzeAsync(auto: true);
        else status.Text = "Filled in from your titles and clip names — ✨ Analyze video also listens to the speech and looks at the scenes.";
    }

    /// <summary>Writes suggestions into every platform's post: empty fields, and fields still holding an earlier suggestion —
    /// anything the user typed stays. What was written is remembered (post "~auto") so a better analysis can replace it.</summary>
    void Fill(ContentInfo info)
    {
        var marks = Post("~auto");
        foreach (var p in Platforms.All)
        {
            var post = Post(p.Id);
            foreach (var (k, val) in Captions.Suggest(info, p).Fields)
            {
                string key = p.Id + "/" + k;
                bool untouched = !post.TryGetValue(k, out var cur) || cur.Length == 0 || (marks.TryGetValue(key, out var was) && was == cur);
                if (untouched) { post[k] = val; marks[key] = val; }
            }
        }
    }

    /// <summary>Shows the current platform's (possibly new) values — but not while the user is typing in the form: then once
    /// they leave it.</summary>
    void Refresh()
    {
        if (!form.IsKeyboardFocusWithin) { ShowPlatform(current); return; }
        DependencyPropertyChangedEventHandler? h = null;
        h = (_, _) => { if (!form.IsKeyboardFocusWithin) { form.IsKeyboardFocusWithinChanged -= h; ShowPlatform(current); } };
        form.IsKeyboardFocusWithinChanged += h;
    }

    void ShowDialogAgain() { Show(); Activate(); }

    static string PresetFor(string id) => id switch
    {
        "youtube" => Editor.H > Editor.W ? "YouTube Shorts" : "YouTube 1080p", "tiktok" => "TikTok", "instagram" => "Instagram Reels",
        "facebook" => Editor.H > Editor.W ? "Facebook Reels" : "Facebook (1080p)", "x" => "X (Twitter)", "linkedin" => "LinkedIn", "vimeo" => "Vimeo", _ => "Match sequence",
    };

    void RefreshFile()
    {
        if (file == null) { fileText.Text = "No video yet — export one, or choose a file."; UpdateUpload(); return; }
        var fi = new FileInfo(file);
        double secs = Publishing.Seconds(file);
        fileText.Text = $"{fi.Name} · {(secs > 0 ? TimeUtil.Format(secs, false) + " · " : "")}{fi.Length / 1048576.0:0.#} MB";
        UpdateUpload();
    }

    void RefreshList()
    {
        list.Children.Clear(); checks.Clear();
        var targets = Targets;
        foreach (var p in Platforms.All)
        {
            var a = Accounts.Get(p.Id);
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand };
            var bg = new Border { CornerRadius = new CornerRadius(5) };
            bg.SetResourceReference(Border.BackgroundProperty, p.Id == current ? "B.Selected" : "B.Hover");
            if (p.Id != current) bg.Background = Brushes.Transparent;
            row.Children.Add(bg);
            var dp = new DockPanel { Margin = new Thickness(6, 6, 8, 6) };
            var cb = new CheckBox { IsChecked = targets.Contains(p.Id), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Include in the upload" };
            cb.Click += (_, _) => { var t = Targets; if (cb.IsChecked == true) t.Add(p.Id); else t.Remove(p.Id); Targets = t; UpdateUpload(); };
            checks[p.Id] = cb;
            var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Margin = new Thickness(6, 0, 8, 0), Fill = (Brush)new BrushConverter().ConvertFromString(p.Color.Replace("#FF", "#"))! };
            var name = Themed(new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center }, "B.Fg");
            var state = new TextBlock { Text = p.Upload == "intent" ? "share" : a.Connected ? "●" : "○", VerticalAlignment = VerticalAlignment.Center, FontSize = 11,
                                        ToolTip = p.Upload == "intent" ? "Opens the site with your text" : a.Connected ? $"Connected: {a.UserName}" + (a.AutoUpload ? " · auto-upload on" : "") : "Not connected" };
            Themed(state, a.Connected || p.Upload == "intent" ? "B.Accent" : "B.Fg.Dim");
            DockPanel.SetDock(state, Dock.Right);
            dp.Children.Add(state); dp.Children.Add(cb); dp.Children.Add(dot); dp.Children.Add(name);
            row.Children.Add(dp);
            row.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not CheckBox) { current = p.Id; RefreshList(); ShowPlatform(p.Id); } };
            list.Children.Add(row);
        }
        UpdateUpload();
    }

    void UpdateUpload()
    {
        int n = Targets.Count;
        upload.Content = n == 0 ? "Upload" : $"Upload to {n} platform{(n == 1 ? "" : "s")}";
        upload.IsEnabled = file != null && n > 0;
    }

    // ---------------- the form ----------------
    TextBox? mainBox;
    void ShowPlatform(string id)
    {
        var p = Platforms.Get(id);
        var a = Accounts.Get(id);
        var v = Publishing.Values(ed, p);
        var post = Post(id);
        form.Children.Clear();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var suggest = new Button { Content = "✨ Suggest", Padding = new Thickness(10, 3, 10, 3), ToolTip = "Write the title, description and hashtags from the video's contents" };
        suggest.Click += async (_, _) =>
        {
            var info = main.LastAnalysis ?? await main.QuickAnalysisAsync();
            bool written = p.Fields.Any(f => f.Kind is FieldKind.Text or FieldKind.Multiline or FieldKind.Tags && post.TryGetValue(f.Id, out var s) && s.Length > 0);
            if (written && !Alert.Confirm(this, "Replace the text you've written for " + p.Name + " with suggestions?", "Suggest", "Replace", "Keep mine")) return;
            var marks = Post("~auto");
            foreach (var (k, val) in Captions.Suggest(info, p).Fields) { post[k] = val; marks[p.Id + "/" + k] = val; }
            ShowPlatform(id);
        };
        DockPanel.SetDock(suggest, Dock.Right);
        top.Children.Add(suggest);
        var title = new StackPanel();
        title.Children.Add(Themed(new TextBlock { Text = p.Name, FontSize = 18, FontWeight = FontWeights.SemiBold }, "B.Fg"));
        title.Children.Add(Themed(new TextBlock { Text = p.Upload == "intent" ? "Shared through the Threads website (its API can't take a local video)" :
                                                       a.Connected ? $"Connected as {a.UserName}" + (a.AutoUpload ? " · uploads automatically after export" : "") : "Not connected — Accounts & settings to connect",
                                                  FontSize = 12, TextWrapping = TextWrapping.Wrap }, a.Connected || p.Upload == "intent" ? "B.Accent" : "B.Fg.Dim"));
        title.Children.Add(Themed(new TextBlock { Text = p.Formats, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }, "B.Fg.Dim"));
        top.Children.Add(title);
        form.Children.Add(top);
        string? hashField = Publishing.HashtagField(p);
        var errors = Themed(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) }, "B.Error");
        void Validate()
        {
            var vals = Publishing.Values(ed, p);
            double secs = file != null ? Publishing.Seconds(file) : ed.End; long bytes = file != null ? new FileInfo(file).Length : 0;
            var errs = Platforms.Validate(p, vals, secs, bytes, Editor.W / (double)Editor.H);
            errors.Text = errs.Count == 0 ? "" : "⚠ " + string.Join("\n⚠ ", errs);
        }
        foreach (var f in p.Fields)
        {
            var val = v.GetValueOrDefault(f.Id, f.Default) ?? "";
            var label = Themed(new TextBlock { Text = f.Label + (f.Required ? " *" : ""), FontSize = 12, Margin = new Thickness(0, 8, 0, 3) }, "B.Fg.Dim");
            if (f.Help != null) label.ToolTip = f.Help;
            void Set(string s) { post[f.Id] = s; Validate(); }
            switch (f.Kind)
            {
                case FieldKind.Text or FieldKind.Multiline or FieldKind.Tags or FieldKind.Number or FieldKind.DateTime:
                {
                    // a few fields get pickers from the account (pages, boards)
                    if ((f.Id == "page" && a.Extra.TryGetValue("pages", out var pages) && pages.Length > 0) || (f.Id == "board" && a.Extra.TryGetValue("boards", out pages) && pages.Length > 0))
                    {
                        var items = pages.Split('\n').Select(x => (id: x.Split('|')[0], name: x.Contains('|') ? x[(x.IndexOf('|') + 1)..] : x)).ToList();
                        var pick = new ComboBox { ItemsSource = items.Select(x => x.name).ToList(), SelectedIndex = Math.Max(0, items.FindIndex(x => x.id == val)) };
                        pick.SelectionChanged += (_, _) => { if (pick.SelectedIndex >= 0) Set(items[pick.SelectedIndex].id); };
                        if (!post.ContainsKey(f.Id) && items.Count > 0) post[f.Id] = items[Math.Max(0, pick.SelectedIndex)].id;
                        form.Children.Add(label); form.Children.Add(pick);
                        break;
                    }
                    bool multi = f.Kind == FieldKind.Multiline;
                    var tb = new TextBox { Text = val, AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
                                           Height = multi ? (f.Max > 1000 ? 150 : 96) : double.NaN, VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                                           ToolTip = f.Help };
                    if (f.Kind == FieldKind.DateTime && string.IsNullOrEmpty(val)) tb.Tag = "yyyy-mm-dd hh:mm";
                    var count = Themed(new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right }, "B.Fg.Dim");
                    void Count()
                    {
                        int tags = Hashtags.Count(tb.Text), len = Platforms.Length(p, f.Id, tb.Text);
                        bool longOk = p.Id == "x" && f.Id == "text" && post.GetValueOrDefault("premium") == "true";
                        count.Text = (f.Max > 0 ? $"{len} / {f.Max}" : "") + (f.Id == hashField ? $"   #  {tags} / {p.HashtagLimit}" : "");
                        count.SetResourceReference(TextBlock.ForegroundProperty, (f.Max > 0 && len > f.Max && !longOk) || (f.Id == hashField && tags > p.HashtagLimit) ? "B.Error" : "B.Fg.Dim");
                    }
                    tb.TextChanged += (_, _) => { Set(tb.Text); Count(); Chips(); };
                    Count();
                    form.Children.Add(label); form.Children.Add(tb); form.Children.Add(count);
                    if (f.Kind == FieldKind.DateTime) form.Children.Add(Themed(new TextBlock { Text = "e.g. 2026-10-04 09:00 (your time)", FontSize = 11 }, "B.Fg.Dim"));
                    if (f.Id == hashField) { mainBox = tb; form.Children.Add(chipPanel); }
                    break;
                }
                case FieldKind.Choice:
                {
                    var opts = f.Options!.Select(Platforms.Opt).ToList();
                    if (p.Id == "tiktok" && f.Id == "privacy" && a.Extra.TryGetValue("privacyOptions", out var allowed) && allowed.Length > 0)
                        opts = opts.Where(o => allowed.Split(',').Contains(o.value)).ToList();
                    var cb = new ComboBox { ItemsSource = opts.Select(o => o.label).ToList(), SelectedIndex = Math.Max(0, opts.FindIndex(o => o.value == val)), ToolTip = f.Help };
                    cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex >= 0) Set(opts[cb.SelectedIndex].value); };
                    form.Children.Add(label); form.Children.Add(cb);
                    break;
                }
                case FieldKind.Bool:
                {
                    var c = new CheckBox { Content = f.Label, IsChecked = val == "true", Margin = new Thickness(0, 8, 0, 0), ToolTip = f.Help };
                    c.Click += (_, _) => Set(c.IsChecked == true ? "true" : "false");
                    form.Children.Add(c);
                    break;
                }
                case FieldKind.Image:
                {
                    var dp = new DockPanel();
                    var tb = new TextBox { Text = val, IsReadOnly = true };
                    var browse = new Button { Content = "Choose…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
                    var frame = new Button { Content = "Use the frame at the playhead", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
                    var clear = new Button { Content = "✕", Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0), ToolTip = "No custom thumbnail" };
                    var design = new Button { Content = "Design…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Thumbnail Studio: text, cut-out subject, templates" };
                    design.Click += (_, _) => { main.OpenThumbnails(); Close(); };
                    browse.Click += (_, _) => { var d = new OpenFileDialog { Filter = "Image|*.jpg;*.jpeg;*.png" }; if (d.ShowDialog(this) == true) { tb.Text = d.FileName; Set(d.FileName); } };
                    frame.Click += (_, _) => { var jpg = main.SaveThumbnail(); if (jpg != null) { tb.Text = jpg; Set(jpg); } };
                    clear.Click += (_, _) => { tb.Text = ""; Set(""); };
                    foreach (var b in new[] { clear, frame, design, browse }) { DockPanel.SetDock(b, Dock.Right); dp.Children.Add(b); }
                    dp.Children.Add(tb);
                    form.Children.Add(label); form.Children.Add(dp);
                    break;
                }
            }
        }
        form.Children.Add(errors);
        var notes = Themed(new TextBlock { Text = p.Notes, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 10, 0, 0) }, "B.Fg.Dim");
        form.Children.Add(notes);
        Chips(); Validate();
    }

    // hashtag suggestions as toggle chips under the main text
    readonly WrapPanel chipPanel = new() { Margin = new Thickness(0, 4, 0, 0) };
    void Chips()
    {
        chipPanel.Children.Clear();
        if (mainBox == null) return;
        var p = Platforms.Get(current);
        var info = main.LastAnalysis;
        var pool = info != null ? Captions.HashtagPool(info, p).Take(16).ToList() : new List<string> { "faith", "jesus", "christian", "god", "church", "hope", "prayer", "worship" };
        var present = Hashtags.In(mainBox.Text).Select(t => t.ToLowerInvariant()).ToHashSet();
        chipPanel.Children.Add(Themed(new TextBlock { Text = "Hashtags:", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, "B.Fg.Dim"));
        foreach (var tag in pool)
        {
            bool on = present.Contains(tag);
            var chip = new ToggleButton { Content = "#" + tag, IsChecked = on, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 2, 4, 2), FontSize = 12 };
            chip.SetResourceReference(StyleProperty, "Tool");
            chip.Click += (_, _) => ToggleTag(tag, chip.IsChecked == true);
            chipPanel.Children.Add(chip);
        }
    }

    void ToggleTag(string tag, bool add)
    {
        if (mainBox == null) return;
        var text = mainBox.Text;
        if (add)
        {
            if (Hashtags.In(text).Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))) return;
            var lines = text.TrimEnd().Split('\n').ToList();
            if (lines.Count > 0 && Hashtags.Count(lines[^1]) > 0 && lines[^1].Trim().Split(' ').All(w => w.StartsWith('#'))) lines[^1] = lines[^1].TrimEnd() + " #" + tag;
            else lines.Add((text.Length > 0 ? "\n" : "") + "#" + tag);
            mainBox.Text = string.Join("\n", lines);
        }
        else mainBox.Text = System.Text.RegularExpressions.Regex.Replace(text, $@"\s?#{System.Text.RegularExpressions.Regex.Escape(tag)}(?![\p{{L}}\p{{N}}_])", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).TrimEnd();
    }

    // ---------------- analysis ----------------
    /// <param name="auto">started by the window itself (models already downloaded): no download offer</param>
    async Task AnalyzeAsync(bool auto = false)
    {
        if (analyzeCts != null) { analyzeCts.Cancel(); return; }
        if (!auto && !await main.EnsureContentModelsAsync(this)) return;
        analyzeCts = new CancellationTokenSource();
        bar.Visibility = Visibility.Visible;
        try
        {
            var info = await Analyzer.AnalyzeAsync(ed, main.ProjectTitle, new Progress<(double p, string what)>(x => { bar.Value = x.p; status.Text = x.what + "  (click Analyze again to stop)"; }), analyzeCts.Token);
            main.LastAnalysis = info;
            ShowAnalysis(info);
            Fill(info); Refresh();
            status.Text = "Suggestions written from the speech and the scenes — edit anything, then upload.";
        }
        catch (OperationCanceledException) { status.Text = "Analysis stopped."; }
        catch (Exception ex) { Alert.Show(this, ex.Message, "Analysis failed", AlertKind.Error); }
        finally { analyzeCts = null; bar.Visibility = Visibility.Collapsed; }
    }

    void ShowAnalysis(ContentInfo info)
    {
        var parts = new List<string>();
        if (info.HasSpeech) parts.Add($"Speech ({info.Language}): {info.TranscriptText.Split(' ').Length} words");
        else parts.Add(Whisper.Present ? "No speech found" : "Speech: model not downloaded");
        if (info.Scenes.Count > 0) parts.Add("Scenes: " + string.Join(", ", info.Scenes.Select(s => s.phrase).Distinct().Take(4)));
        if (info.Scripture.Count > 0) parts.Add("Scripture: " + string.Join("; ", info.Scripture.Take(4)));
        if (info.Titles.Count > 0) parts.Add($"Titles: {info.Titles.Count}");
        if (info.Chapters.Count >= 3) parts.Add($"{info.Chapters.Count} chapters");
        analysis.Text = string.Join("\n", parts);
        Chips();
    }

    // ---------------- upload ----------------
    async Task UploadAsync()
    {
        if (file == null) return;
        var targets = Platforms.All.Where(p => Targets.Contains(p.Id)).ToList();
        double secs = Publishing.Seconds(file); long bytes = new FileInfo(file).Length;
        var problems = targets.Select(p => (p, errs: Platforms.Validate(p, Publishing.Values(ed, p), secs, bytes, Editor.W / (double)Editor.H))).Where(x => x.errs.Count > 0).ToList();
        if (problems.Count > 0)
        {
            current = problems[0].p.Id; RefreshList(); ShowPlatform(current);
            Alert.Show(this, string.Join("\n", problems.Select(x => $"{x.p.Name}: {string.Join(" ", x.errs)}")), "Fix these first", AlertKind.Warning);
            return;
        }
        var notConnected = targets.Where(p => p.Upload == "api" && !Accounts.Get(p.Id).Connected).ToList();
        if (notConnected.Count > 0)
        {
            Alert.Show(this, "Connect " + string.Join(", ", notConnected.Select(p => p.Name)) + " first (Accounts & settings).", "Not connected", AlertKind.Warning);
            main.AccountsDialog(notConnected[0].Id); RefreshList(); return;
        }
        string Vis(PlatformDef p)
        {
            var v = Publishing.Values(ed, p);
            var f = p.Fields.FirstOrDefault(x => x.Id is "privacy" or "visibility" or "publish");
            return f == null ? "" : " — " + Platforms.Opt(f.Options!.FirstOrDefault(o => Platforms.Opt(o).value == v.GetValueOrDefault(f.Id, f.Default)) ?? "").label;
        }
        if (!Alert.Confirm(this, $"{Path.GetFileName(file)} will be posted to:\n\n" + string.Join("\n", targets.Select(p => "• " + p.Name + Vis(p))),
                           "Upload now?", "Upload", "Cancel", AlertKind.Question)) return;
        foreach (var p in targets.Where(p => p.Upload == "intent"))
        {
            var text = Publishing.MainText(p, Publishing.Values(ed, p));
            Clipboard.SetText(text);
            Process.Start(new ProcessStartInfo("https://www.threads.net/intent/post?text=" + Uri.EscapeDataString(text)) { UseShellExecute = true });
            Process.Start("explorer.exe", $"/select,\"{file}\"");
        }
        var api = targets.Where(p => p.Upload == "api").ToList();
        if (api.Count > 0) { Hide(); await main.PublishFileAsync(file, api, automatic: false); ShowDialogAgain(); }
    }
}
