using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>A moment of the video worth opening with (timeline times), and why.</summary>
public sealed record HookMoment(double Start, double End, string Text, double Score, string Why);

/// <summary>Finding hook moments: the transcript's punchiest lines (questions, "you", strong and faith words, numbers, Scripture), or the
/// loudest stretches of the mix when there is no transcript.</summary>
public static class Hooks
{
    static readonly string[] Pull = { "why", "how", "what if", "secret", "truth", "never", "always", "nobody", "everyone", "imagine", "stop", "listen", "remember", "today", "change", "free" };
    static readonly string[] Faith = { "god", "jesus", "lord", "christ", "holy", "spirit", "grace", "hope", "love", "pray", "prayer", "faith", "heaven", "miracle", "promise", "bless", "saved", "gospel" };

    public static double ScoreLine(string text, double t)
    {
        var s = text.Trim(); var low = s.ToLowerInvariant();
        int words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        double sc = words is >= 4 and <= 16 ? 1 : words < 4 ? -0.5 : -0.05 * (words - 16);
        if (s.EndsWith('?')) sc += 1.2;
        if (s.Contains('!')) sc += 0.6;
        sc += Math.Min(0.8, Regex.Matches(low, @"\b(you|your|you're)\b").Count * 0.4);
        sc += Math.Min(1.0, Pull.Count(w => Regex.IsMatch(low, $@"\b{Regex.Escape(w)}\b")) * 0.5);
        sc += Math.Min(0.8, Faith.Count(w => Regex.IsMatch(low, $@"\b{w}")) * 0.4);
        if (Regex.IsMatch(s, @"\d")) sc += 0.3;
        if (Bible.Find(s).Count > 0) sc += 0.8;
        if (t < 3) sc -= 0.5;                                              // already at the start
        return sc;
    }

    /// <summary>Sentences of the transcript (short pieces joined until . ? !), scored, 2–8 s long, best first.</summary>
    public static List<HookMoment> FromTranscript(ContentInfo info)
    {
        var res = new List<HookMoment>();
        var cur = new List<Whisper.Segment>();
        void Flush()
        {
            if (cur.Count == 0) return;
            double a = cur[0].Start, b = cur[^1].End;
            var text = string.Join(" ", cur.Select(x => x.Text.Trim())).Trim();
            cur.Clear();
            if (text.Length < 6) return;
            if (b - a < 2) b = a + 2;
            if (b - a > 8) b = a + 8;
            res.Add(new HookMoment(a, b, text, ScoreLine(text, a), text.EndsWith('?') ? "a question" : Bible.Find(text).Count > 0 ? "Scripture" : "a strong line"));
        }
        foreach (var s in info.Transcript)
        {
            cur.Add(s);
            var t = s.Text.TrimEnd();
            if (t.EndsWith('.') || t.EndsWith('?') || t.EndsWith('!') || cur[^1].End - cur[0].Start > 7) Flush();
        }
        Flush();
        return res.OrderByDescending(m => m.Score).ToList();
    }

    /// <summary>The loudest 3-second stretches of the mix (energy in speech often marks the moment). Snapshot the sources on the UI thread
    /// (<see cref="Mixer.Sources"/>), then run this on any thread.</summary>
    public static List<HookMoment> FromLoudness(AudioOut.Src[] srcs, MasterMix master, double end)
    {
        foreach (var p in srcs.Select(s => s.Path).Distinct()) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (!AudioCache.IsDone(p) && sw.ElapsedMilliseconds < 20000) Thread.Sleep(20); }
        var rms = new List<double>(); double acc = 0; int n = 0; const int Win = Mixer.Rate / 4;
        Mixer.Render(srcs, master, 0, end, (buf, k) =>
        {
            for (int i = 0; i < k; i++) { double v = (buf[i * 2] + buf[i * 2 + 1]) / 65536.0; acc += v * v; if (++n == Win) { rms.Add(Math.Sqrt(acc / n)); acc = 0; n = 0; } }
        });
        var res = new List<HookMoment>();
        if (rms.Count < 16) return res;
        var used = new bool[rms.Count];
        for (int pick = 0; pick < 6; pick++)
        {
            double best = 0; int at = -1;
            for (int i = 8; i + 12 <= rms.Count; i++)
            {
                if (Enumerable.Range(i, 12).Any(j => used[j])) continue;
                double m = 0; for (int j = i; j < i + 12; j++) m += rms[j];
                if (m > best) { best = m; at = i; }
            }
            if (at < 0) break;
            for (int j = Math.Max(0, at - 8); j < Math.Min(rms.Count, at + 20); j++) used[j] = true;
            double a = at * 0.25;
            res.Add(new HookMoment(a, a + 3, "", best / 12 * 10, "a loud moment"));
        }
        return res;
    }
}

/// <summary>Builds an intro at the start of the timeline (one undo step): everything moves later by the intro's length.</summary>
public static class HookBuilder
{
    public static readonly string[] Templates = { "Quote hook", "Question hook", "Teaser", "Title card", "Hook + title card" };
    public static string Describe(string t) => t switch
    {
        "Quote hook" => "The best line, as it was said, with its words on screen — then a white flash into the video.",
        "Question hook" => "A frozen frame with a big question (or your headline) — then a dissolve into the video.",
        "Teaser" => "Three short highlights from across the video, \"Coming up\" on the first — quick flashes between.",
        "Title card" => "A blurred, darkened frame with the title and a subtitle — then a dissolve into the video.",
        _ => "The quote hook first, then the title card.",
    };

    public sealed record Plan(string Template, List<HookMoment> Moments, string Headline, string Subline, bool Slow, string Transition, double HookSeconds);

    static Clip Copy(Clip c) => JsonSerializer.Deserialize<Clip>(JsonSerializer.Serialize(c))!;

    /// <summary>Inserts the intro. Returns its length (0 = nothing to insert).</summary>
    public static double Insert(Editor ed, Plan p)
    {
        var moments = p.Moments.Count > 0 ? p.Moments : new List<HookMoment> { new(Math.Min(ed.End * 0.3, Math.Max(0, ed.End - 3)), Math.Min(ed.End, ed.End * 0.3 + 3), "", 0, "") };
        if (ed.End < 0.5 || Tracks.Video < 3 || Enumerable.Range(0, Tracks.Count).Any(ed.Locked)) return 0;   // (V1–V3 needed; everything moves: no locked track)
        var th = Themes.Get(ed.ThemeName);
        double fps = TimeUtil.Fps;
        double Fr(double t) => Math.Round(t * fps) / fps;
        double total = 0;
        ed.Edit(() =>
        {
            var orig = ed.Clips.Select(Copy).ToList();
            var parts = new List<(string kind, HookMoment m, double dur, string? caption, TextRole role)>();
            string Short(string s, int max) => Analyzer.Shorten(s.Replace('\n', ' '), max);
            var best = moments[0];
            string head = p.Headline.Trim().Length > 0 ? p.Headline.Trim() : best.Text.EndsWith('?') ? Short(best.Text, 70) : "What if today changes everything?";
            switch (p.Template)
            {
                case "Question hook": parts.Add(("hold", best, 3, head, TextRole.Title)); break;
                case "Teaser":
                    for (int i = 0; i < Math.Min(3, moments.Count); i++) parts.Add(("clip", moments[i] with { End = moments[i].Start + 1.3 }, 1.3, i == 0 ? (p.Headline.Trim().Length > 0 ? p.Headline.Trim() : "Coming up…") : null, TextRole.Caption));
                    break;
                case "Title card": parts.Add(("card", best, 3, null, TextRole.Title)); break;
                case "Hook + title card":
                    parts.Add(("clip", best with { End = best.Start + Math.Min(p.HookSeconds, best.End - best.Start) }, 0, best.Text.Length > 0 ? Short(best.Text, 70) : null, TextRole.Caption));
                    parts.Add(("card", best, 3, null, TextRole.Title));
                    break;
                default:            // Quote hook
                    parts.Add(("clip", best with { End = best.Start + Math.Min(p.HookSeconds, Math.Max(2, best.End - best.Start)) }, 0,
                               best.Text.Length > 0 ? Short(best.Text, 70) : (p.Headline.Trim().Length > 0 ? p.Headline.Trim() : null), TextRole.Caption));
                    break;
            }
            double sf = p.Slow ? 0.8 : 1;
            double Len((string kind, HookMoment m, double dur, string? caption, TextRole role) x) => Fr(x.kind == "clip" ? (x.m.End - x.m.Start) / sf : x.dur);
            total = parts.Sum(Len);
            foreach (var c in ed.Clips) c.Start += total;                     // the video moves later; the intro goes before it
            double pos = 0;
            var firstOfPart = new List<Clip>();
            foreach (var part in parts)
            {
                double len = Len(part); Clip? first = null;
                if (part.kind == "clip")
                {
                    // the pieces of every track under the moment (sound included, titles left out), slowed down if asked
                    var links = new Dictionary<string, string>();
                    double a = part.m.Start, b = part.m.End;
                    foreach (var o in orig.Where(o => o.Kind != ClipKind.Text && !ed.TrackOff[o.Track] && o.Start < b - 1e-6 && o.End > a + 1e-6).OrderBy(o => o.Track))
                    {
                        double s = Math.Max(a, o.Start), e = Math.Min(b, o.End);
                        var n = Copy(o); n.Id = Guid.NewGuid().ToString("N");
                        n.LinkId = o.LinkId == null ? null : links.TryGetValue(o.LinkId, out var nl) ? nl : links[o.LinkId] = Guid.NewGuid().ToString("N");
                        n.TransIn = n.TransOut = null; n.FadeIn = n.FadeOut = 0;
                        n.In = TimeMap.Src(o, s); n.Start = Fr(pos + (s - a) / sf); n.Dur = Math.Max(1 / fps, Fr((e - s) / sf));
                        if (sf != 1 && o.Kind is ClipKind.Video or ClipKind.Audio && !o.Hold) n.Speed = Math.Round(o.Speed * sf, 4);
                        ed.Clips.Add(n);
                        if (first == null && Tracks.IsVideo(n.Track)) first = n;
                    }
                }
                else
                {
                    // a still of the best frame: the top picture at that time
                    double t = part.m.Start + Math.Min(0.5, (part.m.End - part.m.Start) / 2);
                    var o = orig.Where(x => x.Kind is ClipKind.Video or ClipKind.Image && !ed.TrackOff[x.Track] && t >= x.Start && t < x.End).OrderBy(x => x.Track).FirstOrDefault();
                    if (o != null)
                    {
                        var n = Copy(o); n.Id = Guid.NewGuid().ToString("N"); n.LinkId = null; n.TransIn = n.TransOut = null; n.FadeIn = n.FadeOut = 0;
                        n.Track = Tracks.V1; n.Start = pos; n.Dur = len; n.NoAudio = true; n.Anim = null;
                        if (n.Kind == ClipKind.Video) { n.In = TimeMap.Src(o, t); n.Hold = true; }
                        if (part.kind == "card") { n.Blur = 28; n.Grade = new ColorGrade { Exposure = -1.1, Saturation = 80 }; }
                        else n.Grade = new ColorGrade { Exposure = -0.5, Contrast = 15 };
                        ed.Clips.Add(n); first = n;
                    }
                }
                // words on screen (V2; a subtitle goes on V3 — one clip per track at a time)
                void Title(string text, TextRole role, int track)
                {
                    var (x0, y0) = Themes.DefaultPosition(role);
                    ed.Clips.Add(new Clip { Kind = ClipKind.Text, Track = track, Start = pos, Dur = len, Role = role, Text = text, X = x0, Y = y0,
                                            Style = th.For(role).Clone(), FadeIn = Math.Min(0.25, len / 4), FadeOut = Math.Min(0.25, len / 4) });
                }
                if (part.kind == "card")
                {
                    Title(p.Headline.Trim().Length > 0 ? p.Headline.Trim() : head, TextRole.Title, Tracks.V(2));
                    if (p.Subline.Trim().Length > 0) Title(p.Subline.Trim(), TextRole.Subtitle, Tracks.V(3));
                }
                else if (part.caption != null) Title(part.caption, part.role, Tracks.V(2));
                if (first != null) firstOfPart.Add(first);
                pos += len;
            }
            // transitions: between the intro's parts and into the video
            for (int i = 1; i < firstOfPart.Count; i++) firstOfPart[i].TransIn = new Transition { Kind = p.Template == "Teaser" ? "Dip to White" : "Cross Dissolve", Dur = 0.3 };
            var mainFirst = ed.Clips.Where(c => Tracks.IsVideo(c.Track) && c.Kind != ClipKind.Text && Math.Abs(c.Start - total) < 0.5 / fps).OrderByDescending(c => c.Track).FirstOrDefault();
            if (mainFirst != null) mainFirst.TransIn = new Transition { Kind = p.Transition, Dur = p.Transition == "Dip to White" ? 0.4 : 0.6 };
        });
        return total;
    }
}

/// <summary>Intro &amp; Hook: pick the moment to open with and an intro template, then insert it at the start of the timeline.</summary>
sealed class HookWindow : Window
{
    readonly MainWindow main; readonly Editor ed;
    readonly StackPanel momentsPanel = new(), right = new() { Margin = new Thickness(14, 4, 16, 16) };
    readonly TextBlock status = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    List<HookMoment> moments = new();
    readonly HashSet<HookMoment> chosen = new();
    string template = "Quote hook";
    readonly TextBox headline = new() { AcceptsReturn = false }, subline = new();
    readonly CheckBox slow = new() { Content = "Slow motion (80 %)", Margin = new Thickness(0, 6, 0, 0) };
    readonly ComboBox trans = new() { ItemsSource = new[] { "Dip to White", "Cross Dissolve", "Dip to Black", "Iris Round", "Push Left" }, SelectedIndex = 0 };
    readonly Slider len = new() { Minimum = 2, Maximum = 8, Value = 5 };

    public HookWindow(MainWindow main, Editor ed)
    {
        this.main = main; this.ed = ed;
        status.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        Title = "Intro & Hook"; Width = 1000; Height = 700; MinWidth = 820; MinHeight = 520;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 13; UseLayoutRounding = true;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 44, GlassFrameThickness = new Thickness(-1),
                                                                    ResizeBorderThickness = new Thickness(6), CornerRadius = new CornerRadius(8), UseAeroCaptionButtons = false });
        var root = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        root.SetResourceReference(Border.BackgroundProperty, "B.Glass.Tint");
        root.SetResourceReference(Border.BorderBrushProperty, "B.Glass.Edge");
        Content = root;
        SourceInitialized += (_, _) => AlertWindow.Backdrop(this, root);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) }); grid.RowDefinitions.Add(new RowDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
        var head = new DockPanel { Margin = new Thickness(16, 0, 8, 0) };
        var close = new Button { Content = "", FontSize = 10, Style = (Style)main.FindResource("Icon"), Width = 36, Height = 30, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close); head.Children.Add(Th(new TextBlock { Text = "Intro & Hook", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }, "B.Fg"));
        Grid.SetColumnSpan(head, 2); grid.Children.Add(head);
        var left = new DockPanel { Margin = new Thickness(16, 4, 8, 16) };
        var lh = new StackPanel(); DockPanel.SetDock(lh, Dock.Top);
        lh.Children.Add(Th(new TextBlock { Text = "Moments to open with", FontWeight = FontWeights.SemiBold }, "B.Fg"));
        lh.Children.Add(Dim("The strongest first seconds decide whether people keep watching. Tick the moments to use (the first is the main hook)."));
        var lb = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        lb.Children.Add(Btn("Listen to the speech", () => _ = AnalyzeAsync(true), "Transcribes the video on this PC (Whisper) and ranks its lines"));
        lb.Children.Add(Btn("Find loud moments", () => _ = LoudAsync(), "Without speech: the most energetic stretches of the sound"));
        lh.Children.Add(lb);
        left.Children.Add(lh);
        left.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = momentsPanel });
        Grid.SetRow(left, 1); grid.Children.Add(left);
        var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = right };
        Grid.SetRow(sv, 1); Grid.SetColumn(sv, 1); grid.Children.Add(sv);
        root.Child = grid;
        BuildRight();
        Loaded += async (_, _) =>
        {
            if (main.LastAnalysis is { HasSpeech: true } a) Show(Hooks.FromTranscript(a), "from the speech");
            else if (main.LastAnalysis == null && Whisper.Present) await AnalyzeAsync(false);
            else await LoudAsync();
        };
    }

    static T Th<T>(T e, string key) where T : FrameworkElement { e.SetResourceReference(TextBlock.ForegroundProperty, key); return e; }
    TextBlock Dim(string s) => Th(new TextBlock { Text = s, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }, "B.Fg.Dim");
    TextBlock Head(string s) => Th(new TextBlock { Text = s, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) }, "B.Fg");
    Button Btn(string text, Action a, string? tip = null) { var b = new Button { Content = text, Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 6), ToolTip = tip }; b.Click += (_, _) => a(); return b; }

    void BuildRight()
    {
        right.Children.Clear();
        right.Children.Add(Head("Intro"));
        foreach (var t in HookBuilder.Templates)
        {
            var rb = new RadioButton { Content = t, GroupName = "tpl", IsChecked = t == template, Margin = new Thickness(0, 4, 0, 0), FontWeight = FontWeights.SemiBold };
            rb.Checked += (_, _) => { template = t; BuildRight(); };
            right.Children.Add(rb);
            var dsc = Dim(HookBuilder.Describe(t)); dsc.Margin = new Thickness(22, 0, 0, 2);
            right.Children.Add(dsc);
        }
        right.Children.Add(Head(template is "Title card" or "Hook + title card" ? "Title" : template == "Teaser" ? "Text on the first highlight" : "Headline (optional)"));
        right.Children.Add(headline);
        if (template is "Title card" or "Hook + title card") { right.Children.Add(Head("Subtitle")); right.Children.Add(subline); if (subline.Text.Length == 0) subline.Text = main.ProjectTitle.StartsWith("Untitled") ? "" : main.ProjectTitle; }
        if (template is "Quote hook" or "Hook + title card")
        {
            right.Children.Add(Head("Hook length (at most)"));
            right.Children.Add(len);
            right.Children.Add(slow);
        }
        right.Children.Add(Head("Into the video"));
        right.Children.Add(trans);
        var go = new Button { Content = "Insert intro", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        go.SetResourceReference(StyleProperty, "Primary");
        go.Click += (_, _) => Insert();
        right.Children.Add(go);
        right.Children.Add(Dim("The intro goes at the start of the timeline and everything else moves later. Ctrl+Z takes it out again."));
        right.Children.Add(status);
    }

    void Show(List<HookMoment> ms, string source)
    {
        moments = ms.Take(12).ToList(); chosen.Clear();
        foreach (var m in moments.Take(3)) chosen.Add(m);
        momentsPanel.Children.Clear();
        if (moments.Count == 0) { momentsPanel.Children.Add(Dim("No moments found yet.")); return; }
        foreach (var m in moments)
        {
            var card = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 6), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "B.Input.Bg"); card.SetResourceReference(Border.BorderBrushProperty, "B.Input.Border");
            var dp = new DockPanel();
            var cb = new CheckBox { IsChecked = chosen.Contains(m), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
            cb.Click += (_, _) => { if (cb.IsChecked == true) chosen.Add(m); else chosen.Remove(m); };
            DockPanel.SetDock(cb, Dock.Left); dp.Children.Add(cb);
            var play = new Button { Content = "", FontSize = 12, Style = (Style)main.FindResource("Icon"), Width = 28, ToolTip = "Go there in the timeline" };
            play.Click += (_, _) => ed.Seek(m.Start);
            DockPanel.SetDock(play, Dock.Right); dp.Children.Add(play);
            var txt = new StackPanel();
            txt.Children.Add(Th(new TextBlock { Text = m.Text.Length > 0 ? "“" + m.Text + "”" : "(no words)", TextWrapping = TextWrapping.Wrap }, "B.Fg"));
            txt.Children.Add(Dim($"{TimeUtil.Format(m.Start, false)} – {TimeUtil.Format(m.End, false)} · {m.Why}"));
            dp.Children.Add(txt);
            card.Child = dp;
            momentsPanel.Children.Add(card);
        }
        status.Text = $"{moments.Count} moment{(moments.Count == 1 ? "" : "s")} {source}.";
        var q = moments.FirstOrDefault(x => x.Text.EndsWith('?'));
        if (headline.Text.Length == 0 && q != null && template == "Question hook") headline.Text = q.Text;
    }

    async Task AnalyzeAsync(bool ask)
    {
        if (!Whisper.Present && (!ask || !await main.EnsureContentModelsAsync(this))) { if (ask) status.Text = "Speech isn't available without the speech model."; return; }
        status.Text = "Listening to the video…";
        try
        {
            var info = await Analyzer.AnalyzeAsync(ed, main.ProjectTitle, new Progress<(double p, string what)>(x => status.Text = x.what), CancellationToken.None);
            main.LastAnalysis = info;
            if (info.HasSpeech) Show(Hooks.FromTranscript(info), "from the speech");
            else { status.Text = "No speech found — using the loudest moments."; await LoudAsync(); }
        }
        catch (Exception ex) { status.Text = "Couldn't listen: " + ex.Message; }
    }

    async Task LoudAsync()
    {
        status.Text = "Finding the loudest moments…";
        var srcs = Mixer.Sources(ed, Transitions.Compute(ed), new()).ToArray(); var master = Mixer.MasterOf(ed); double end = ed.End;
        var ms = await Task.Run(() => Hooks.FromLoudness(srcs, master, end));
        Show(ms, "from the sound");
    }

    void Insert()
    {
        if (Enumerable.Range(0, Tracks.Count).FirstOrDefault(ed.Locked, -1) is int lk and >= 0) { status.Text = $"{Tracks.Name(lk)} is locked — the intro moves everything later, so unlock every track first."; return; }
        if (Tracks.Video < 3 && !ed.AddTracks(3 - Tracks.Video, 0, 0, Tracks.Count)) return;   // (stills on V1, words on V2, a subtitle on V3)
        var picked = moments.Where(chosen.Contains).ToList();
        var plan = new HookBuilder.Plan(template, picked, headline.Text, subline.Text, slow.IsChecked == true, trans.SelectedItem as string ?? "Cross Dissolve", len.Value);
        double total = HookBuilder.Insert(ed, plan);
        if (total <= 0) { status.Text = "Add a video to the timeline first."; return; }
        ed.Seek(0);
        status.Text = $"Inserted a {total:0.#} s intro at the start. Press play in CutPro to watch it (Ctrl+Z removes it).";
    }
}
