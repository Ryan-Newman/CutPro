using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CutPro;

/// <summary>Media management (partial MainWindow): Relink (an offline file — the others missing from the same folder are found there too) and
/// Replace Footage (another file for an item, in every sequence), Collect Files (Premiere's Project Manager: the project and every file it uses
/// copied into one folder, with the paths rewritten in the copy), the Properties dialog (the file's metadata: timecode, reel, camera, streams).</summary>
public partial class MainWindow
{
    /// <summary>The source monitor's clock: the media's own timecode when it has one, else the time from its start.</summary>
    string SrcClock(double t) => srcItem != null && TimeUtil.SourceTimecode(srcItem, t) is { } tc ? tc : TimeUtil.Format(t);

    /// <summary>File ▸ Relink Offline Media: locate the first missing file (the rest follow when they are in the same folder).</summary>
    void RelinkOffline()
    {
        foreach (var m in ed.Media.ToList())
        {
            if (!m.Offline) continue;
            if (!RelinkMedia(m)) return;                                   // (cancelled)
        }
    }

    /// <summary>Relink (offline) / Replace Footage (online): pick another file for the item. Clips keep their In / Out times; offline items whose
    /// file names are in the chosen folder are relinked too. Returns false when cancelled.</summary>
    bool RelinkMedia(MediaItem m)
    {
        if (m.Seq != null) return false;
        var dir = Path.GetDirectoryName(m.Path);
        var dlg = new OpenFileDialog
        {
            Title = m.Offline ? $"Where is “{m.Name}”?" : $"Replace “{m.Name}” with…",
            Filter = (m.Name.IndexOfAny(new[] { '|', ';' }) < 0 ? $"{m.Name}|{m.Name}|" : "") + MediaProbe.DialogFilter, FilterIndex = m.Name.IndexOfAny(new[] { '|', ';' }) >= 0 || m.Offline ? 1 : 2,
            InitialDirectory = dir != null && Directory.Exists(dir) ? dir : null, FileName = m.Name,
        };
        if (dlg.ShowDialog(this) != true) return false;
        var newPath = dlg.FileName;
        bool replacing = !m.Offline;
        double oldDur = m.Duration;
        if (replacing && !Path.GetFileName(newPath).Equals(m.Name, StringComparison.OrdinalIgnoreCase) && ed.Sequences.Any(s => s.Clips.Any(c => c.MediaId == m.Id)) &&
            !Alert.Confirm(this, $"Every clip of “{m.Name}” in every sequence will show “{Path.GetFileName(newPath)}” instead (same In / Out times).", "Replace footage", "Replace"))
            return false;
        _ = RelinkAsync(m, newPath, oldDur, replacing);
        return true;
    }

    async Task RelinkAsync(MediaItem m, string newPath, double oldDur, bool replacing)
    {
        Pause();
        m.Path = newPath; m.Thumb = null;
        bool ok = await MediaProbe.FillAsync(m);
        var found = FindInFolder(ed.Media.Where(x => x != m), Path.GetDirectoryName(newPath)!);
        foreach (var o in found) await MediaProbe.FillAsync(o);
        binRef = null; ed.RaiseChanged(); Pv.InvalidateVisual();
        var shortClips = ok && m.Duration + 1e-3 < oldDur ? ed.Sequences.Sum(s => s.Clips.Count(c => c.MediaId == m.Id && c.Kind is ClipKind.Video or ClipKind.Audio && TimeMap.Span(c).hi > m.Duration + 1e-3)) : 0;
        string msg = !ok ? $"“{Path.GetFileName(newPath)}” could not be read: {MediaProbe.LastError}"
                   : (replacing ? $"Replaced with “{m.Name}”" : $"Relinked “{m.Name}”") + (found.Count > 0 ? $" — and {found.Count} other missing file{(found.Count == 1 ? "" : "s")} found in the same folder" : "")
                     + (shortClips > 0 ? $". {shortClips} clip{(shortClips == 1 ? " goes" : "s go")} past the end of the new file (they show its last frame)." : "");
        if (!ok || shortClips > 0) Alert.Show(this, msg, replacing ? "Replace footage" : "Relink", AlertKind.Warning);
        else SetTask(msg, 4000);
    }

    /// <summary>Offline items whose file names are in a folder: their paths move there (returns them).</summary>
    internal static List<MediaItem> FindInFolder(IEnumerable<MediaItem> media, string folder)
    {
        var found = new List<MediaItem>();
        foreach (var o in media.Where(x => x.Seq == null && x.Offline))
        {
            var cand = Path.Combine(folder, Path.GetFileName(o.Path));
            if (File.Exists(cand)) { o.Path = cand; o.RefreshOnline(); found.Add(o); }
        }
        return found;
    }

    /// <summary>Collect Files' plan: a copy of the project (from its JSON) whose media and .cube looks point into <paramref name="mediaDir"/>, the
    /// files to copy (one per source file; two files of one name from different folders get "name (2)"), the offline files left out.</summary>
    internal static (ProjectFile pf, List<(string from, string to)> files, List<string> offline) CollectPlan(string json, string mediaDir)
    {
        var pf = JsonSerializer.Deserialize<ProjectFile>(json)!;
        var files = new List<(string from, string to)>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Dest(string from)
        {
            if (files.FirstOrDefault(f => f.from.Equals(from, StringComparison.OrdinalIgnoreCase)) is { to: not null } had) return had.to;
            string stem = Path.GetFileNameWithoutExtension(from), ext = Path.GetExtension(from), to = Path.Combine(mediaDir, stem + ext);
            for (int i = 2; taken.Contains(to); i++) to = Path.Combine(mediaDir, $"{stem} ({i}){ext}");
            taken.Add(to); files.Add((from, to));
            return to;
        }
        var offline = new List<string>();
        foreach (var m in pf.Media)
        {
            if (!File.Exists(m.Path)) { offline.Add(m.Path); continue; }
            m.Path = Dest(m.Path);
        }
        foreach (var c in (pf.Sequences ?? new()).SelectMany(s => s.Clips).Concat(pf.Clips))
            if (c.Grade?.Look is { } lk && Grade.IsCube(lk)) { if (File.Exists(lk)) c.Grade.Look = Dest(lk); else offline.Add(lk); }
        return (pf, files, offline.Distinct().ToList());
    }

    /// <summary>File ▸ Collect Files: copies the project and every media file it uses (and .cube looks) into a new folder — the copy's paths point
    /// there, the open project is unchanged. Offline files are listed, not copied.</summary>
    async Task CollectFilesAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Collect the project and its media into…" };
        if (dlg.ShowDialog(this) != true) return;
        var root = dlg.FolderName;
        var name = projectPath != null ? Path.GetFileNameWithoutExtension(projectPath) : "Untitled";
        var media = Path.Combine(root, name + " Media");
        var (pf, files, offline) = CollectPlan(JsonSerializer.Serialize(BuildProjectFile()), media);
        long total = files.Sum(f => new FileInfo(f.from).Length), done = 0;
        var cts = new CancellationTokenSource();
        var prog = new Alert.ProgressDialog(this, "Collect Files", $"Copying {files.Count} file{(files.Count == 1 ? "" : "s")} ({total / 1048576.0:0} MB) to {root}", () => cts.Cancel());
        string? error = null;
        try
        {
            Directory.CreateDirectory(media);
            foreach (var (from, to) in files)
            {
                prog.Text = $"Copying {Path.GetFileName(from)}  ({files.IndexOf((from, to)) + 1} of {files.Count})";
                await Task.Run(() =>
                {
                    using var src = File.OpenRead(from);
                    using var dst = File.Create(to);
                    var buf = new byte[1 << 20];
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        dst.Write(buf, 0, n); done += n;
                        if (total > 0) { double v = done / (double)total; Dispatcher.BeginInvoke(() => prog.Value = v); }
                    }
                }, cts.Token);
                File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
            }
            var projFile = Path.Combine(root, name + ".cutpro");
            File.WriteAllText(projFile, JsonSerializer.Serialize(pf, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (OperationCanceledException) { error = "Cancelled — the folder holds what was copied so far."; }
        catch (Exception ex) { error = ex.Message; }
        prog.Close();
        if (error != null) { Alert.Show(this, error, "Collect Files", AlertKind.Warning); return; }
        Alert.Show(this, $"{name}.cutpro and {files.Count} file{(files.Count == 1 ? "" : "s")} ({total / 1048576.0:0} MB) are in\n{root}" +
                   (offline.Count > 0 ? $"\n\nNot copied (offline):\n{string.Join("\n", offline.Distinct().Take(10))}" : "") +
                   "\n\nThe open project still uses the original files.", "Files collected", offline.Count > 0 ? AlertKind.Warning : AlertKind.Info);
    }

    /// <summary>A media item's Properties: path, use, and everything the file says about itself (MediaProbe metadata).</summary>
    void ShowProperties(MediaItem m)
    {
        var rows = new List<(string k, string v)> { ("File", m.Path), ("Status", m.Offline ? "Offline (file missing)" : "Online"), ("Type", m.Type.ToString()) };
        if (m.Type != ClipKind.Image) rows.Add(("Duration", TimeUtil.Format(m.Duration) + $"  ({m.Duration:0.###} s)"));
        if (m.Width > 0) rows.Add(("Size", $"{m.Width}×{m.Height}" + (m.Rotation != 0 ? $" (rotated {m.Rotation}°)" : "")));
        foreach (var kv in m.Meta) rows.Add((kv.Key, kv.Value));
        if (m.Timecode != null) rows.Add(("Timecode at the end", TimeUtil.SourceTimecode(m, m.Duration) ?? ""));
        int clips = ed.Sequences.Sum(s => s.Clips.Count(c => c.MediaId == m.Id)), seqs = ed.Sequences.Count(s => s.Clips.Any(c => c.MediaId == m.Id));
        rows.Add(("Used", clips == 0 ? "not on a timeline" : $"{clips} clip{(clips == 1 ? "" : "s")} in {seqs} sequence{(seqs == 1 ? "" : "s")}"));
        if (m.DecodeNote.Length > 0) rows.Add(("Decoding", m.DecodeNote));
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0), MaxWidth = 560 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = rows[i].k, Margin = new Thickness(0, 2, 12, 2), FontWeight = FontWeights.SemiBold };
            k.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg.Dim");
            // (read-only boxes: values can be selected and copied)
            var v = new TextBox { Text = rows[i].v, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0), Margin = new Thickness(0, 2, 0, 2) };
            v.SetResourceReference(TextBox.ForegroundProperty, "B.Fg");
            System.Windows.Automation.AutomationProperties.SetName(v, rows[i].k);
            Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            grid.Children.Add(k); grid.Children.Add(v);
        }
        var sv = new ScrollViewer { Content = grid, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        int r = Alert.Show(this, m.Name, "Properties", AlertKind.Info, new[] { m.Offline ? "Relink…" : "Replace Footage…", "Close" }, sv);   // (closing the window = the last button)
        if (r == 0) RelinkMedia(m);
    }
}
