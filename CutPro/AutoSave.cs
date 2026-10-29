using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CutPro;

/// <summary>Auto-save and crash recovery (the files). Versions: %LOCALAPPDATA%\CutPro\AutoSave\&lt;project&gt;-&lt;path hash&gt;-yyyyMMdd-HHmmss.cutpro,
/// the newest <c>keep</c> per project. Recovery: each running CutPro writes recovery-&lt;pid&gt;.cutpro (the project as it is, every half
/// minute when it changed, and from the crash handler) next to session-&lt;pid&gt;.json; a clean exit deletes both, so files of a process
/// that is gone are a crash to offer back.</summary>
public static class AutoSave
{
    public static string? DirOverride;                                   // (tests)
    public static string Dir => DirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro", "AutoSave");

    /// <summary>The name a project's versions share: its file name and a hash of its path ("Untitled" before the first save).</summary>
    public static string Key(string? projectPath) => projectPath == null ? "Untitled"
        : Safe(Path.GetFileNameWithoutExtension(projectPath)) + "-" + AudioCache.Hash(Path.GetFullPath(projectPath).ToLowerInvariant())[..6];
    static string Safe(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == '-' ? '_' : ch));

    /// <summary>Writes a version and deletes the oldest beyond keep. Returns the file.</summary>
    public static string WriteVersion(string key, string json, int keep, DateTime now)
    {
        Directory.CreateDirectory(Dir);
        var file = Path.Combine(Dir, $"{key}-{now:yyyyMMdd-HHmmss}.cutpro");
        WriteSafe(file, json);
        foreach (var (old, _) in Versions(key).Skip(Math.Max(1, keep))) try { File.Delete(old); } catch { }
        return file;
    }
    /// <summary>A project's versions, newest first.</summary>
    public static List<(string File, DateTime Time)> Versions(string key)
    {
        if (!Directory.Exists(Dir)) return new();
        var list = new List<(string, DateTime)>();
        foreach (var f in Directory.GetFiles(Dir, key + "-*.cutpro"))
        {
            var stamp = Path.GetFileNameWithoutExtension(f)[(key.Length + 1)..];
            if (DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var t)) list.Add((f, t));
        }
        return list.OrderByDescending(x => x.Item2).ToList();
    }

    public sealed class Session
    {
        public int Pid { get; set; }
        public string? Project { get; set; }
        public DateTime Time { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string Recovery { get; set; } = "";
    }
    static string SessionFile(int pid) => Path.Combine(Dir, $"session-{pid}.json");
    static string RecoveryFile(int pid) => Path.Combine(Dir, $"recovery-{pid}.cutpro");
    public static int Pid = Environment.ProcessId;                       // (tests pretend to be another process)

    /// <summary>This process's recovery copy of the project (and what it belongs to).</summary>
    public static void WriteRecovery(string json, string? projectPath, DateTime now)
    {
        Directory.CreateDirectory(Dir);
        WriteSafe(RecoveryFile(Pid), json);
        WriteSafe(SessionFile(Pid), JsonSerializer.Serialize(new Session { Pid = Pid, Project = projectPath, Time = now }));
    }
    /// <summary>A clean exit: nothing to recover.</summary>
    public static void EndSession() { try { File.Delete(RecoveryFile(Pid)); File.Delete(SessionFile(Pid)); } catch { } }
    /// <summary>Recovery copies left by CutPro processes that are no longer running (newest first).</summary>
    public static List<Session> Pending(Func<int, bool>? alive = null)
    {
        alive ??= IsCutPro;
        var res = new List<Session>();
        if (!Directory.Exists(Dir)) return res;
        foreach (var f in Directory.GetFiles(Dir, "session-*.json"))
            try
            {
                var s = JsonSerializer.Deserialize<Session>(File.ReadAllText(f));
                if (s == null || s.Pid == Pid || alive(s.Pid)) continue;
                s.Recovery = RecoveryFile(s.Pid);
                if (File.Exists(s.Recovery)) res.Add(s); else File.Delete(f);
            }
            catch { }
        return res.OrderByDescending(s => s.Time).ToList();
    }
    /// <summary>Forgets a crash's copy (after it was recovered or declined).</summary>
    public static void Discard(Session s) { try { File.Delete(s.Recovery); File.Delete(SessionFile(s.Pid)); } catch { } }
    static bool IsCutPro(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited && p.ProcessName.StartsWith("CutPro", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    /// <summary>Write to a temporary file, then replace: a crash while writing never leaves half a project.</summary>
    static void WriteSafe(string file, string text)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, file, true);
    }
}

/// <summary>Auto-save, crash recovery and "save changes?" (the window's side).</summary>
public partial class MainWindow
{
    readonly DispatcherTimer autoTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    long histGen, savedGen; string savedOrg = "|";
    string lastRecovery = "", lastVersion = "";
    DateTime lastVersionAt = DateTime.Now;

    /// <summary>The project file's text (no pending drag is committed when commit = false).</summary>
    string ProjectJson(bool commit) => JsonSerializer.Serialize(BuildProjectFile(commit), new JsonSerializerOptions { WriteIndented = true });

    /// <summary>What the Project panel holds that isn't in the undo history (imports, bins, labels, ratings, keywords, transcripts, relinks).</summary>
    string OrgSig() => string.Join(";", ed.Bins) + "|" + string.Join(";", ed.Media.Select(m => $"{m.Id},{m.Path},{m.Bin},{m.Label},{m.Rating},{m.Keywords},{m.Words?.Count},{m.KeepHdr}"));
    bool Dirty => histGen != savedGen || OrgSig() != savedOrg;
    void MarkSaved(bool saved)
    {
        savedGen = saved ? histGen : -1; savedOrg = OrgSig();
        if (saved) lastVersion = lastRecovery = "";
        OnChanged();
    }

    void InitAutoSave()
    {
        ed.HistoryChanged += () => histGen++;
        savedOrg = OrgSig();
        if (AppSettings.ReadOnly) return;                                     // (automated runs: never touch the user's auto-saves)
        autoTimer.Tick += (_, _) => AutoTick(DateTime.Now);
        autoTimer.Start();
        Loaded += (_, _) => Dispatcher.BeginInvoke(OfferRecovery, DispatcherPriority.ApplicationIdle);
    }

    void AutoTick(DateTime now)
    {
        if (playing || Mouse.LeftButton == MouseButtonState.Pressed || !Dirty) return;   // (no hitch while playing; never in the middle of a drag)
        try
        {
            var json = ProjectJson(false);
            if (json != lastRecovery) { AutoSave.WriteRecovery(json, projectPath, now); lastRecovery = json; }
            if (S.AutoSaveMinutes > 0 && now - lastVersionAt >= TimeSpan.FromMinutes(S.AutoSaveMinutes) && json != lastVersion)
            {
                AutoSave.WriteVersion(AutoSave.Key(projectPath), json, S.AutoSaveKeep, now);
                lastVersion = json; lastVersionAt = now;
                SetTask($"Auto-saved {now:HH:mm}", 2000);
            }
        }
        catch (Exception ex) { SetTask("Auto-save failed: " + ex.Message, 4000); }
    }

    /// <summary>From the crash handler: the project as it is now, for the next start.</summary>
    public void EmergencySave()
    {
        if (AppSettings.ReadOnly) return;
        try { AutoSave.WriteRecovery(ProjectJson(false), projectPath, DateTime.Now); } catch { }
    }

    async void OfferRecovery()
    {
        foreach (var s in AutoSave.Pending())
        {
            string what = s.Project != null ? Path.GetFileName(s.Project) : "an unsaved project";
            int r = Alert.Show(this, $"CutPro closed unexpectedly while {what} was open (last copy {s.Time:g}).\n\nRecover the project as it was then?",
                               "Recover project", AlertKind.Question, "Recover", "Discard", "Later");
            if (r == 2) return;
            if (r == 0)
            {
                await OpenProjectFile(s.Recovery, s.Project);
                histGen++;                                                    // (recovered changes aren't saved yet)
                OnChanged();
                SetTask("Recovered — save it to keep it", 5000);
            }
            AutoSave.Discard(s);
            if (r == 0) return;
        }
    }

    /// <summary>"Save changes?" before the project goes away. False = cancel.</summary>
    bool ConfirmDiscard()
    {
        if (AppSettings.ReadOnly || !Dirty || ed.Media.Count == 0 && ed.Clips.Count == 0) return true;
        string name = projectPath != null ? Path.GetFileName(projectPath) : "Untitled";
        int r = Alert.Show(this, $"Save the changes to {name}?", "Unsaved changes", AlertKind.Question, "Save", "Don't Save", "Cancel");
        if (r == 2 || r < 0) return false;
        if (r == 0) { Save_Click(this, new RoutedEventArgs()); return !Dirty; }
        return true;
    }

    /// <summary>File ▸ Auto-Save Versions…: this project's versions, newest first; one opens as an unsaved copy.</summary>
    void AutoSaveVersions()
    {
        var list = AutoSave.Versions(AutoSave.Key(projectPath));
        var box = new ListBox { Height = 260, MinWidth = 360, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (f, t) in list) box.Items.Add(new ListBoxItem { Content = $"{t:ddd d MMM yyyy  HH:mm:ss}", Tag = f });
        if (list.Count == 0) box.Items.Add(new ListBoxItem { Content = "No auto-saves of this project yet.", IsEnabled = false });
        else box.SelectedIndex = 0;
        var every = new ComboBox { ItemsSource = new[] { "Off", "Every 2 minutes", "Every 5 minutes", "Every 10 minutes", "Every 20 minutes" }, Margin = new Thickness(0, 8, 0, 0) };
        int[] mins = { 0, 2, 5, 10, 20 };
        every.SelectedIndex = Math.Max(0, Array.IndexOf(mins, S.AutoSaveMinutes));
        var panel = new StackPanel(); panel.Children.Add(box); panel.Children.Add(every);
        int r = Alert.Show(this, $"Auto-saves keep the newest {S.AutoSaveKeep} versions of each project in\n{AutoSave.Dir}", "Auto-Save Versions", AlertKind.Info,
                           new[] { "Open Version", "Close" }, panel);
        if (every.SelectedIndex >= 0 && mins[every.SelectedIndex] != S.AutoSaveMinutes) { S.AutoSaveMinutes = mins[every.SelectedIndex]; AppSettings.Save(); }
        if (r != 0 || (box.SelectedItem as ListBoxItem)?.Tag is not string file || !ConfirmDiscard()) return;
        _ = OpenVersion(file);
    }
    async Task OpenVersion(string file)
    {
        await OpenProjectFile(file, null);
        histGen++; OnChanged();
        SetTask($"Opened the auto-save of {File.GetLastWriteTime(file):g} — Save asks where to keep it", 5000);
    }
}
