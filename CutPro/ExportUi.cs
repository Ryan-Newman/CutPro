using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CutPro;

/// <summary>Export dialog (glass): presets for every platform, container / codec / encoder (CPU or GPU, only the ones that
/// work on this PC), resolution, quality or bitrate, speed, audio, In–Out range, size estimate. Then the export itself.</summary>
public partial class MainWindow
{
    ExportSettings exportChoice = ExportSettings.Presets[0];
    bool exportToQueue;
    string? lastExportPath;

    async void Export_Click(object s, RoutedEventArgs e) => await ExportFlowAsync();

    /// <summary>Dialog → file name → render. Returns the exported file (null if cancelled / failed).</summary>
    async Task<string?> ExportFlowAsync(ExportSettings? preset = null)
    {
        if (ed.Clips.Count == 0) { Alert.Show(this, "Add clips to the timeline first.", "Nothing to export"); return null; }
        var ff = Exporter.FindFfmpeg();
        if (ff == null)
        {
            bool locate = Alert.Confirm(this,
                "Exporting needs ffmpeg.exe, which wasn't found.\n\nInstall it with:  winget install Gyan.FFmpeg\n(or place ffmpeg.exe next to CutPro.exe).\n\nOr locate ffmpeg.exe manually.",
                "ffmpeg required", "Locate ffmpeg.exe…", "Cancel", AlertKind.Warning);
            if (!locate) return null;
            var pick = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe", Title = "Locate ffmpeg.exe" };
            if (pick.ShowDialog(this) != true) return null;
            ff = pick.FileName; Exporter.RememberFfmpeg(ff);
        }
        Pause();
        var opts = await ExportDialogAsync(preset ?? exportChoice);
        if (opts == null) return null;
        exportChoice = opts;
        var name = (projectPath != null ? Path.GetFileNameWithoutExtension(projectPath) : "export") + (ed.Sequences.Count > 1 ? " - " + string.Concat(ed.Seq.Name.Split(Path.GetInvalidFileNameChars())) : "");   // (several sequences: which one)
        var save = new SaveFileDialog
        {
            Filter = $"{opts.Container.ToUpperInvariant()} file|*{opts.Extension}", DefaultExt = opts.Extension,
            FileName = name + (opts.Aspect is > 0 and < 1 ? "_short" : "") + opts.Extension,
            InitialDirectory = lastExportPath != null ? Path.GetDirectoryName(lastExportPath) : null,
        };
        if (save.ShowDialog(this) != true) return null;
        if (exportToQueue)
        {   // the sequence as it is now renders in the background; editing goes on
            lastExportPath = save.FileName;
            var job = RenderQueue.Add(ed, opts, save.FileName);
            ShowDock("Queue");
            SetTask($"“{job.Name}” added to the render queue — it renders in the background while you edit", 4000);
            return null;
        }
        var err = await RenderAsync(ff, opts, save.FileName);
        if (err == null)
        {
            lastExportPath = save.FileName;
            if (opts.CaptionFile && ed.Clips.Any(Captioning.IsCaption))
            {   // the captions as an .srt beside the video (times from the export's start)
                var (t0, t1) = Exporter.Range(ed, opts);
                try { File.WriteAllText(Path.ChangeExtension(save.FileName, ".srt"), Captioning.ToSrt(Captioning.InRange(Captioning.CuesOf(ed), t0, t1)), new System.Text.UTF8Encoding(false)); }
                catch (Exception ex) { Alert.Show(this, ex.Message, "Could not save the captions file", AlertKind.Warning); }
            }
            await AfterExportAsync(save.FileName, opts);
            return save.FileName;
        }
        if (err != "Cancelled") Alert.Show(this, err, "Export failed", AlertKind.Error);
        return null;
    }

    /// <summary>Titles and masks at the export size (UI thread, WPF renders them), then ffmpeg, with a progress dialog.</summary>
    async Task<string?> RenderAsync(string ff, ExportSettings opts, string file)
    {
        var cts = new CancellationTokenSource();
        var dlg = new Alert.ProgressDialog(this, "Exporting " + Path.GetFileName(file), "Preparing titles…", () => cts.Cancel());
        string phase = "";
        var progress = new Progress<double>(p => { dlg.Value = p; dlg.Text = phase != "" ? $"{phase} {p * 100:0}%" : $"Rendering {opts.OutW}×{opts.OutH}… {p * 100:0}%"; });
        var status = new Progress<string>(s => { phase = s; if (s != "") dlg.Text = s; });
        string? err;
        var tmp = Path.Combine(Path.GetTempPath(), "cutpro_export_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var titles = opts.AudioOnly ? new Dictionary<string, string>() : TitleVisual.PrepareExport(ed, tmp, opts.OutW, opts.OutH, !opts.BurnCaptions);
            dlg.Text = "Rendering object masks…";
            var masks = opts.AudioOnly ? new Dictionary<string, string>() : MaskExport.Prepare(ed, tmp, new Progress<double>(p => dlg.Value = p), opts.Scale);
            err = await Task.Run(() => Exporter.RunAsync(ff, ed, file, progress, cts.Token, titles, masks, opts, status));
        }
        catch (Exception ex) { err = ex.Message; }
        finally { try { Directory.Delete(tmp, true); } catch { } dlg.Close(); }
        return err;
    }

    /// <summary>The export dialog. Returns the settings, or null when cancelled.</summary>
    async Task<ExportSettings?> ExportDialogAsync(ExportSettings start)
    {
        var o = start.Clone();
        var form = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Width = 470 };
        Grid Row(string label, UIElement c, string? tip = null)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3), ToolTip = tip };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; l.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
            Grid.SetColumn(c, 1); g.Children.Add(l); g.Children.Add(c); form.Children.Add(g); return g;
        }
        TextBlock Dim(string text, double size = 11.5) { var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size }; t.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim"); return t; }

        var presets = ExportSettings.Presets;
        var preset = new ComboBox { ItemsSource = presets.Select(p => p.Name).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(presets, p => p.Name == o.Name)) };
        var presetNote = Dim(""); presetNote.Margin = new Thickness(112, 0, 0, 4);
        var aspectWarn = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(112, 0, 0, 6), Visibility = Visibility.Collapsed };
        aspectWarn.SetResourceReference(Border.BackgroundProperty, "B.Selected");
        var warnRow = new DockPanel();
        var switchBtn = new Button { Content = "Switch format", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(switchBtn, Dock.Right);
        var warnText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        warnText.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg");
        warnRow.Children.Add(switchBtn); warnRow.Children.Add(warnText); aspectWarn.Child = warnRow;
        var container = new ComboBox(); var codec = new ComboBox(); var encoder = new ComboBox();
        var res = new ComboBox(); var cq = new RadioButton { Content = "Constant quality", GroupName = "rc" }; var br = new RadioButton { Content = "Target bitrate", GroupName = "rc", Margin = new Thickness(14, 0, 0, 0) };
        var rcRow = new StackPanel { Orientation = Orientation.Horizontal }; rcRow.Children.Add(cq); rcRow.Children.Add(br);
        var quality = new Slider { Minimum = 10, Maximum = 40, SmallChange = 1, LargeChange = 3, IsSnapToTickEnabled = true, TickFrequency = 1, Width = 200 };
        var qText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }; qText.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
        var qRow = new StackPanel { Orientation = Orientation.Horizontal }; qRow.Children.Add(quality); qRow.Children.Add(qText);
        var mbps = new TextBox { Width = 60 }; var mbRow = new StackPanel { Orientation = Orientation.Horizontal }; mbRow.Children.Add(mbps); mbRow.Children.Add(Dim("  Mbps", 12));
        var speed = new ComboBox { ItemsSource = new[] { "ultrafast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" } };
        var audio = new ComboBox { ItemsSource = new[] { "AAC", "Opus", "MP3", "PCM (uncompressed)", "None" } };
        var akbps = new ComboBox { ItemsSource = new[] { "128", "192", "256", "320", "384" }, Width = 80 };
        var aRow = new StackPanel { Orientation = Orientation.Horizontal }; aRow.Children.Add(audio); aRow.Children.Add(new TextBlock { Text = "  " }); aRow.Children.Add(akbps); aRow.Children.Add(Dim("  kbps", 12));
        var chans = new ComboBox { ItemsSource = new[] { "Stereo", "Mono", "5.1 surround" }, Width = 130, Margin = new Thickness(10, 0, 0, 0),
                                   ToolTip = "Mono folds the stereo mix to one channel; 5.1 puts each audio track where its header's 5.1 Output says (front, centre, surround, LFE)" };
        aRow.Children.Add(chans);
        var inout = new CheckBox { Content = "In to Out only", IsEnabled = ed.MarkIn != null && ed.MarkOut != null };
        var full = new CheckBox { Content = "Full colour composite (4:4:4)", IsChecked = o.Full444, ToolTip = "Titles, keys and overlays keep their full colour detail until the encoder; off = 4:2:0 throughout (a little faster, softer colour edges)" };
        int nChapters = ed.ChaptersFor(0, ed.End).Count;
        var chapters = new CheckBox { Content = $"Chapters from the {nChapters} chapter marker{(nChapters == 1 ? "" : "s")}", IsChecked = o.Chapters, ToolTip = "The sequence's chapter markers become the file's chapters (players show them; MP4 / MOV / MKV / M4A)" };
        bool hasCaptions = ed.Clips.Any(Captioning.IsCaption);
        var burn = new CheckBox { Content = "Burn into the video", IsChecked = o.BurnCaptions, ToolTip = "Draw the captions into the picture (they show everywhere and can't be turned off)" };
        var srt = new CheckBox { Content = "Save an .srt next to it", IsChecked = o.CaptionFile, Margin = new Thickness(14, 0, 0, 0), ToolTip = "A subtitle file viewers can turn on and off (upload it with the video on YouTube, Facebook, LinkedIn…)" };
        var capRow = new StackPanel { Orientation = Orientation.Horizontal }; capRow.Children.Add(burn); capRow.Children.Add(srt);
        var norm = new CheckBox { Content = "Normalize to", VerticalAlignment = VerticalAlignment.Center, ToolTip = "One gain for the whole export so its integrated loudness (ITU BS.1770) hits the target; peaks above −1 dBTP are limited" };
        var lufs = new ComboBox { ItemsSource = Loudness.Targets.Select(x => x.label).ToList(), Width = 262, Margin = new Thickness(8, 0, 0, 0) };
        var nRow = new StackPanel { Orientation = Orientation.Horizontal }; nRow.Children.Add(norm); nRow.Children.Add(lufs);
        var loudNote = Dim("Measuring the sequence…"); loudNote.Margin = new Thickness(112, 0, 0, 4);
        Loudness.Result? measured = null; CancellationTokenSource? mcts = null;
        var estimate = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(112, 8, 0, 0) }; estimate.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg");

        Row("Preset", preset); form.Children.Add(presetNote); form.Children.Add(aspectWarn);
        Row("Format", container); Row("Video codec", codec); Row("Encoder", encoder, "Hardware encoders are much faster; CPU encoders compress a little better");
        Row("Resolution", res); Row("Rate control", rcRow); var qR = Row("Quality", qRow, "Lower = better quality, bigger file"); var bR = Row("Bitrate", mbRow);
        Row("Speed", speed, "Slower presets make smaller files at the same quality"); Row("Audio", aRow);
        Row("Loudness", nRow, "Platforms turn loud videos down to about −14 LUFS; quieter ones stay quiet"); form.Children.Add(loudNote);
        Row("Range", inout);
        Row("Colour", full);
        if (hasCaptions) Row("Captions", capRow);
        if (nChapters > 0) Row("Chapters", chapters);
        form.Children.Add(estimate);

        bool busy = false;
        HashSet<string>? usable = null;
        var codecs = new[] { ("h264", "H.264 (AVC)"), ("hevc", "H.265 (HEVC)"), ("av1", "AV1"), ("vp9", "VP9"), ("prores", "Apple ProRes 422 HQ"), ("prores4444", "Apple ProRes 4444 (full colour)"),
                             ("prores4444a", "ProRes 4444 with alpha (transparent)"), ("vp9a", "VP9 with alpha (transparent)"), ("qtrle", "QuickTime Animation with alpha"), ("png", "PNG sequence with alpha"),
                             ("none", "None — audio only") };
        var encoders = new[] { ("software", "CPU (best compatibility)"), ("nvenc", "NVIDIA NVENC (GPU)"), ("qsv", "Intel Quick Sync (GPU)"), ("amf", "AMD AMF (GPU)") };
        int[] heights = { 0, 720, 1080, 1440, 2160 };
        string ResLabel(int hh) { var t = new ExportSettings { Height = hh }; return hh == 0 ? $"Sequence ({Editor.OutW}×{Editor.OutH})" : $"{t.OutW}×{t.OutH}"; }

        void Load(ExportSettings p)
        {
            busy = true;
            bool ao = p.AudioOnly;
            container.ItemsSource = ao ? ExportSettings.AudioContainers : ExportSettings.Containers;
            container.SelectedItem = p.Container;
            codec.ItemsSource = codecs.Select(x => x.Item2).ToList(); codec.SelectedIndex = Array.FindIndex(codecs, x => x.Item1 == p.Video);
            var enc = encoders.Where(x => x.Item1 == "software" || usable == null || usable.Contains(new ExportSettings { Video = p.Video, Encoder = x.Item1 }.VideoEncoder)).ToList();
            encoder.ItemsSource = enc.Select(x => x.Item2).ToList();
            encoder.SelectedIndex = Math.Max(0, enc.FindIndex(x => x.Item1 == p.Encoder));
            encoder.IsEnabled = !ao && p.Video is "h264" or "hevc" or "av1";
            res.ItemsSource = heights.Select(ResLabel).ToList(); res.SelectedIndex = Math.Max(0, Array.IndexOf(heights, p.Height)); res.IsEnabled = !ao;
            cq.IsChecked = p.ConstantQuality; br.IsChecked = !p.ConstantQuality;
            quality.Value = p.Quality; mbps.Text = p.BitrateMbps.ToString("0.#", CultureInfo.InvariantCulture);
            speed.SelectedItem = p.Speed;
            audio.SelectedIndex = p.Audio switch { "opus" => 1, "mp3" => 2, "pcm" => 3, "none" => 4, _ => 0 };
            akbps.SelectedItem = p.AudioKbps.ToString();
            chans.SelectedIndex = p.Channels switch { "mono" => 1, "5.1" => 2, _ => 0 };
            chans.IsEnabled = p.Audio is not ("none" or "mp3");
            inout.IsChecked = p.InOutOnly && inout.IsEnabled;
            norm.IsChecked = p.Normalize; norm.IsEnabled = lufs.IsEnabled = p.Audio != "none";
            lufs.SelectedIndex = Math.Max(0, Array.FindIndex(Loudness.Targets, x => Math.Abs(x.lufs - p.Lufs) < 0.01));
            busy = false;
            Refresh();
        }
        void Read()
        {
            o.Container = container.SelectedItem as string ?? o.Container;
            if (codec.SelectedIndex >= 0) o.Video = codecs[codec.SelectedIndex].Item1;
            var enc = encoders.Where(x => x.Item1 == "software" || usable == null || usable.Contains(new ExportSettings { Video = o.Video, Encoder = x.Item1 }.VideoEncoder)).ToList();
            o.Encoder = encoder.SelectedIndex >= 0 && encoder.SelectedIndex < enc.Count ? enc[encoder.SelectedIndex].Item1 : "software";
            o.Height = res.SelectedIndex >= 0 ? heights[res.SelectedIndex] : 0;
            o.ConstantQuality = cq.IsChecked == true;
            o.Quality = (int)quality.Value;
            if (double.TryParse(mbps.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) && mb > 0.1) o.BitrateMbps = mb;
            o.Speed = speed.SelectedItem as string ?? "medium";
            o.Audio = audio.SelectedIndex switch { 1 => "opus", 2 => "mp3", 3 => "pcm", 4 => "none", _ => "aac" };
            if (akbps.SelectedItem is string k && int.TryParse(k, out int kb)) o.AudioKbps = kb;
            o.Channels = o.Audio == "mp3" ? (chans.SelectedIndex == 1 ? "mono" : "stereo") : chans.SelectedIndex switch { 1 => "mono", 2 => "5.1", _ => "stereo" };
            o.InOutOnly = inout.IsChecked == true;
            o.Full444 = full.IsChecked == true;
            o.Chapters = chapters.IsChecked == true;
            o.BurnCaptions = burn.IsChecked == true; o.CaptionFile = srt.IsChecked == true;
            o.Normalize = norm.IsChecked == true && o.Audio != "none";
            if (lufs.SelectedIndex >= 0) o.Lufs = Loudness.Targets[lufs.SelectedIndex].lufs;
        }
        void Refresh()
        {
            presetNote.Text = o.Note ?? "";
            presetNote.Visibility = string.IsNullOrEmpty(o.Note) ? Visibility.Collapsed : Visibility.Visible;
            double seqA = Editor.W / (double)Editor.H;
            bool mismatch = o.Aspect > 0 && Math.Abs(o.Aspect - seqA) > 0.02;
            aspectWarn.Visibility = mismatch ? Visibility.Visible : Visibility.Collapsed;
            string want = o.Aspect < 0.7 ? "9:16" : o.Aspect < 0.9 ? "4:5" : o.Aspect < 1.2 ? "1:1" : "16:9";
            warnText.Text = $"This preset is {want}; the sequence is {SeqAspectName()}. Switch the format (Ctrl+Z undoes), or export as it is.";
            switchBtn.Tag = want;
            qR.Visibility = o.ConstantQuality ? Visibility.Visible : Visibility.Collapsed; bR.Visibility = o.ConstantQuality ? Visibility.Collapsed : Visibility.Visible;
            qText.Text = $"{o.Quality}  " + (o.Quality <= 16 ? "(near lossless)" : o.Quality <= 20 ? "(high)" : o.Quality <= 26 ? "(good)" : "(small file)");
            akbps.IsEnabled = o.Audio is not ("pcm" or "none");
            double len = o.InOutOnly && ed.MarkIn is double a0 && ed.MarkOut is double b0 ? b0 - a0 : ed.End;
            estimate.Text = $"{(o.AudioOnly ? "Audio" : $"{o.OutW}×{o.OutH} · {TimeUtil.Label(TimeUtil.Fps)} fps")} · {TimeUtil.Format(len, false)} · {o.EstimateSize(len)}";
            LoudNote();
        }
        void Changed() { if (busy) return; Read(); busy = true; Load(o); }
        // loudness of the sequence as it will export (before normalization), measured in the background while the dialog is open
        void LoudNote()
        {
            if (o.Audio == "none") { loudNote.Text = "No sound in this export."; return; }
            if (measured is not { } mr) { loudNote.Text = "Measuring the sequence…"; return; }
            if (mr.Silent) { loudNote.Text = "The sequence is silent."; return; }
            var (g, lim) = Loudness.Normalize(mr, o.Lufs, o.TruePeak);
            loudNote.Text = o.Normalize ? $"Sequence {Fmt(mr.Integrated)} LUFS → {Fmt(o.Lufs)} LUFS ({(g >= 0 ? "+" : "")}{Fmt(g)} dB{(lim ? ", peaks limited to −1 dBTP" : "")})"
                                        : $"Sequence measures {Fmt(mr.Integrated)} LUFS, true peak {Fmt(mr.TruePeak)} dBTP";
        }
        void Measure()
        {
            mcts?.Cancel(); mcts = new CancellationTokenSource(); var ct = mcts.Token;
            measured = null; LoudNote();
            var (t0, t1) = Exporter.Range(ed, new ExportSettings { InOutOnly = inout.IsChecked == true });
            var srcs = Mixer.Sources(ed, Transitions.Compute(ed), new()).ToArray(); var master = Mixer.MasterOf(ed);
            Task.Run(() => Mixer.Measure(srcs, master, t0, t1, ct)).ContinueWith(t =>
            {
                if (!ct.IsCancellationRequested && t.Status == TaskStatus.RanToCompletion) { measured = t.Result; LoudNote(); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        preset.SelectionChanged += (_, _) => { if (preset.SelectedIndex >= 0) { o = presets[preset.SelectedIndex].Clone(); Load(o); } };
        foreach (var cb in new[] { container, codec, encoder, res, speed, audio, akbps, chans }) cb.SelectionChanged += (_, _) => Changed();
        codec.SelectionChanged += (_, _) =>
        {
            if (busy || codec.SelectedIndex < 0) return;
            var v = codecs[codec.SelectedIndex].Item1;
            o.Container = v == "none" ? "wav" : v is "prores" or "prores4444" or "prores4444a" or "qtrle" ? "mov" : v is "vp9" or "vp9a" ? "webm" : v == "png" ? "png"
                        : o.Container is "wav" or "mp3" or "m4a" or "webm" or "png" ? "mp4" : o.Container;
            if (v == "none") o.Audio = "pcm"; else if (v == "png") o.Audio = "none"; else if (o.Container == "webm") o.Audio = "opus";
            Load(o);
        };
        cq.Checked += (_, _) => Changed(); br.Checked += (_, _) => Changed();
        quality.ValueChanged += (_, _) => Changed();
        mbps.TextChanged += (_, _) => { if (!busy) { Read(); Refresh(); } };
        inout.Click += (_, _) => { Changed(); Measure(); };
        norm.Click += (_, _) => Changed(); lufs.SelectionChanged += (_, _) => Changed();
        switchBtn.Click += (_, _) => { Format_Click(new FrameworkElement { Tag = switchBtn.Tag }, new RoutedEventArgs()); res.ItemsSource = heights.Select(ResLabel).ToList(); Load(o); };
        Load(o); Measure();
        // which GPU encoders work (tested once, in the background)
        _ = Task.Run(async () =>
        {
            var u = await ExportSettings.UsableEncodersAsync();
            await Dispatcher.InvokeAsync(() => { usable = u; Read(); Load(o); });
        });
        var buttons = quietExport ? new[] { "Export…", "Cancel" } : new[] { "Export…", "Add to Queue…", "Cancel" };
        int r = Alert.Show(this, "", "Export", AlertKind.Progress, buttons, form);
        mcts?.Cancel();
        if (r >= buttons.Length - 1) return null;
        exportToQueue = r == 1 && !quietExport;
        Read();
        if (o.Video != "none" && usable != null && !usable.Contains(o.VideoEncoder))
        {
            Alert.Show(this, $"{o.VideoEncoder} isn't available in this ffmpeg or on this PC. Pick another codec or the CPU encoder.", "Export", AlertKind.Warning);
            return null;
        }
        return o;
    }

    static string SeqAspectName()
    {
        double a = Editor.W / (double)Editor.H;
        return Math.Abs(a - 16 / 9.0) < 0.02 ? "16:9" : Math.Abs(a - 9 / 16.0) < 0.02 ? "9:16" : Math.Abs(a - 1) < 0.02 ? "1:1" : Math.Abs(a - 0.8) < 0.02 ? "4:5" : $"{Editor.OutW}×{Editor.OutH}";
    }

    /// <summary>Export from the Publish window (no "publish?" prompt afterwards).</summary>
    internal async Task<string?> ExportForPublishAsync(ExportSettings preset)
    {
        quietExport = true;
        try { return await ExportFlowAsync(preset); } finally { quietExport = false; }
    }
    bool quietExport;

    /// <summary>The frame at the playhead as a JPG (sequence aspect, 1280 px on the long side) — a thumbnail / cover.</summary>
    internal string? SaveThumbnail()
    {
        try
        {
            Pv.Sync(ed.Time, false);
            int lw = Editor.W, lh = Editor.H;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(lw, lh, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var size = new Size(lw, lh);
            Pv.Measure(size); Pv.Arrange(new Rect(size)); rtb.Render(Pv);
            double s = 1280.0 / Math.Max(lw, lh);
            var scaled = new System.Windows.Media.Imaging.TransformedBitmap(rtb, new System.Windows.Media.ScaleTransform(s, s));
            var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 92 };
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(scaled));
            var path = Path.Combine(Path.GetTempPath(), $"cutpro_thumb_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
            using (var fs = File.Create(path)) enc.Save(fs);
            InvalidateMeasure();
            return path;
        }
        catch (Exception ex) { Alert.Show(this, ex.Message, "Thumbnail", AlertKind.Error); return null; }
    }

    /// <summary>Analysis with whatever models are present (titles, names and Scripture always work).</summary>
    /// <summary>Instant suggestions from titles, clip and object names only (no speech / scene models).</summary>
    internal Task<ContentInfo> QuickAnalysisAsync() => Analyzer.AnalyzeAsync(ed, ProjectName(), null, CancellationToken.None, models: false);

    /// <summary>First use: offer to download the speech (Whisper) and scene (CLIP) models.</summary>
    internal async Task<bool> EnsureContentModelsAsync(Window owner)
    {
        var missing = new List<(string file, string url)>();
        if (!Whisper.Present) missing.AddRange(Whisper.Files);
        if (!ClipTagger.Present) missing.AddRange(ClipTagger.Files);
        if (missing.Count == 0) return true;
        string what = (!Whisper.Present ? "• Whisper base — speech to text (about 77 MB, MIT licence)\n" : "") + (!ClipTagger.Present ? "• CLIP ViT-B/32 — scene recognition (about 154 MB, MIT licence)\n" : "");
        int pick = Alert.Show(owner, $"To describe the video, CutPro listens to the speech and looks at the scenes on this PC — nothing is uploaded. It needs:\n\n{what}\nfrom huggingface.co (downloaded once).",
                              "Download the content models?", AlertKind.Question, "Download", "Use titles and names only", "Cancel");
        if (pick == 2) return false;
        if (pick == 1) return true;
        var cts = new CancellationTokenSource();
        var dlg = new Alert.ProgressDialog(this, "Downloading content models", "Starting…", () => cts.Cancel());
        try
        {
            await ModelStore.DownloadAsync(missing.ToArray(), new Progress<double>(p => { dlg.Value = p; dlg.Text = $"Downloading… {p * 100:0}%"; }), cts.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Alert.Show(owner, ex.Message, "Download failed", AlertKind.Error); return false; }
        finally { dlg.Close(); }
    }

    /// <summary>After a successful export: offer to publish (or upload automatically to the platforms set to auto-upload).</summary>
    async Task AfterExportAsync(string file, ExportSettings opts)
    {
        if (quietExport) return;
        var auto = Publishing.AutoTargets();
        if (auto.Count > 0 && !opts.AudioOnly) { await PublishFileAsync(file, auto, automatic: true); return; }
        int pick = Alert.Show(this, file, "Export complete", AlertKind.Info, opts.AudioOnly ? new[] { "OK", "Show in folder" } : new[] { "Publish…", "Show in folder", "Close" });
        if (!opts.AudioOnly && pick == 0) OpenPublish(file);
        else if (pick == 1 || (opts.AudioOnly && pick == 1)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"");
    }
}
