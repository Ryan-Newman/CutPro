using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;

namespace CutPro;

/// <summary>
/// CutPro.exe --featuretest dir: the editing conveniences (the "medium" features).
/// - markers: sequence markers (one per frame, undo / redo, a drag = one step, next / previous, clear, duplicate + project round trip), clip
///   markers by source time (they move with the clip, a razor cut keeps each on its piece);
/// - chapters: from chapter markers (In–Out range shifts them), YouTube's rules, ffmetadata, and an exported MP4's chapters read back by ffprobe;
/// - clipboard: copy / paste (overwrite) / paste insert — new ids, links kept among the copies, own object-mask copies, locked tracks avoided;
///   Paste Attributes (keyframes moved to the target's source times, copies not shared objects, only what was ticked), Remove Attributes;
/// - groups (select / move together, ungroup), split-screen / picture-in-picture layouts (where the clips land), gaps (in the sequence / track);
/// - history: entry names, stepping several steps back and forth; keyboard shortcuts: gesture text, lookup, reassigning (taken from the other
///   command), reset; settings stay untouched;
/// - media: timecode / reel from a file's tags, drop-frame timecode, relinking moved files by folder, Collect Files (the copy opens with every
///   file present, same-name files kept apart); the language table.
/// </summary>
public static class FeatureTest
{
    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        AppSettings.ReadOnly = true;
        try
        {
            Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
            string Gen(string name, string args)
            {
                var f = Path.Combine(dir, name);
                if (!File.Exists(f)) Exec(ff, $"-y -v error {args} \"{f}\"");
                return f;
            }
            var patF = Gen("pat.mp4", "-f lavfi -i testsrc2=s=640x360:r=30:d=30 -f lavfi -i sine=frequency=440:duration=30 -c:v libx264 -crf 20 -pix_fmt yuv420p -c:a aac -shortest");
            var tcF = Gen("tc.mov", "-f lavfi -i testsrc2=s=320x180:r=25:d=2 -c:v libx264 -pix_fmt yuv420p -timecode 10:00:00:00 -metadata:s:v:0 reel_name=REEL01");
            async Task<MediaItem> Imp(string f) => await MediaProbe.ProbeAsync(f) ?? throw new Exception("import " + f + ": " + MediaProbe.LastError);
            var mPat = await Imp(patF);
            const double fr = 1 / 30.0;

            // ---------- 1. markers ----------
            {
                var ed = new Editor(); ed.Media.Add(mPat);
                ed.AddMedia(mPat, Tracks.V1, 0);
                var m1 = ed.AddMarker(1.01);
                var again = ed.AddMarker(1.0 + fr * 0.2);
                Check(ed.Markers.Count == 1 && ReferenceEquals(m1, again) && Math.Abs(m1.T - 1.0) < 1e-9, "M on a frame: one marker, on the frame (pressing M there again finds it)");
                Check(ed.UndoLabels.First() == "Add Marker", $"its undo step is “Add Marker” (got “{ed.UndoLabels.First()}”)");
                ed.Undo(); bool goneU = ed.Markers.Count == 0; ed.Redo();
                Check(goneU && ed.Markers.Count == 1 && ed.Markers[0].Id == m1.Id, "undo removes it, redo brings it back");
                var mk = ed.Markers[0];
                ed.MarkBegin(); mk.T = 2; mk.T = 3; ed.MarkEnd();
                Check(ed.UndoLabels.First() == "Move Marker" && ed.UndoLabels.Count(l => l == "Move Marker") == 1, "a marker drag is one “Move Marker” step");
                ed.Undo();
                Check(Math.Abs(ed.Markers[0].T - 1) < 1e-9, "undo puts it back at 1 s");
                ed.AddMarker(5); ed.AddMarker(8);
                Check(ed.NextMarker(1) == 5 && ed.PrevMarker(8) == 5 && ed.NextMarker(8) == null && ed.PrevMarker(1) == null, "next / previous marker");
                // clip markers: source time
                var v = ed.Clips.First(c => c.Kind == ClipKind.Video);
                ed.Edit(() => { v.Start = 2; v.In = 1; v.Dur = 6; });
                var cm = ed.AddClipMarker(v, 3)!;
                Check(cm != null && Math.Abs(cm.T - 2) < 1e-9 && Editor.TimelineTime(v, cm) is double ct && Math.Abs(ct - 3) < 1e-9, "a clip marker at 3 s on a clip starting at 2 s with In 1 s sits at source 2 s");
                ed.Edit(() => { foreach (var c in ed.Clips) c.Start += 3; });
                Check(Editor.TimelineTime(v, cm!) is double ct2 && Math.Abs(ct2 - 6) < 1e-9, "the clip moves 3 s later: its marker shows at 6 s");
                ed.CutClips(ed.Clips.Where(c => c.Kind == ClipKind.Video).ToList(), 7);
                var pieces = ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).ToList();
                Check(pieces.Count == 2 && Editor.TimelineTime(pieces[0], pieces[0].Markers![0]) != null && Editor.TimelineTime(pieces[1], pieces[1].Markers![0]) == null,
                      "a razor cut after it: the marker shows on the left piece only");
                Check(ed.AllMarkers().Count() == 4, $"every marker on the timeline: 3 sequence + 1 clip (got {ed.AllMarkers().Count()})");
                var dup = ed.DuplicateSequence(ed.Seq);
                Check(dup.Markers is { Count: 3 } && !ReferenceEquals(dup.Markers[0], ed.Sequences[0].Markers![0]), "Duplicate Sequence copies the markers (its own)");
                var pf = new ProjectFile { Media = ed.Media, Sequences = ed.Sequences, Active = ed.Seq.Id };
                var back = JsonSerializer.Deserialize<ProjectFile>(JsonSerializer.Serialize(pf))!;
                Check(back.Sequences![0].Markers!.Count == 3 && back.Sequences[0].Clips.Any(c => c.Markers is { Count: 1 }), "the project file keeps sequence and clip markers");
                ed.Activate(ed.Sequences[0]);
                ed.ClearMarkers();
                Check(ed.Markers.Count == 0, "Clear All Markers");
            }

            // ---------- 2. chapters ----------
            {
                var ms = new List<Marker> { new() { T = 0, Name = "Intro" }, new() { T = 12, Name = "Part = one; #1" }, new() { T = 25.5 }, new() { T = 40, Chapter = false } };
                var ch = Chapters.Of(ms, 0, 50);
                Check(ch.Count == 3 && ch[2].name == "Chapter 3", "chapters: the chapter markers, a name for an unnamed one, not the non-chapter marker");
                var shifted = Chapters.Of(ms, 10, 30);
                Check(shifted.Count == 2 && Math.Abs(shifted[0].t - 2) < 1e-9 && Math.Abs(shifted[1].t - 15.5) < 1e-9, "an In–Out range (10 … 30 s): the chapters inside, from its start");
                var yt = Chapters.YouTube(ch, 50, out var probs);
                Check(yt == "0:00 Intro\n0:12 Part = one; #1\n0:25 Chapter 3" && probs.Count == 0, "YouTube text: 0:00 Intro / 0:12 … / 0:25 …, nothing wrong");
                Chapters.YouTube(Chapters.Of(new List<Marker> { new() { T = 5, Name = "A" } }, 0, 30), 30, out var p2);
                Check(p2.Any(p => p.Contains("0:00")) && p2.Any(p => p.Contains("at least 3")) && p2.Any(p => p.Contains("10 s")), "YouTube's rules: a 0:00 chapter added, at least 3, each 10 s or longer");
                var meta = Chapters.FfMetadata(ch, 50);
                Check(meta.StartsWith(";FFMETADATA1") && meta.Contains("START=12000\nEND=25500") && meta.Contains("title=Part \\= one\\; \\#1"), "ffmetadata: millisecond chapters, special characters escaped");

                // an exported MP4's chapters
                var ed = new Editor(); ed.Media.Add(mPat);
                ed.AddMedia(mPat, Tracks.V1, 0);
                ed.Edit(() => { foreach (var c in ed.Clips) c.Dur = 6; });
                ed.AddMarker(0, "Start"); ed.AddMarker(2, "Middle"); ed.AddMarker(4.5, "End"); ed.AddMarker(5.4).Chapter = false;
                var mp4 = Path.Combine(dir, "chapters.mp4");
                var err = await Exporter.RunAsync(ff, ed, mp4, new Progress<double>(), CancellationToken.None, null, null, new ExportSettings { Speed = "ultrafast", Height = 360 });
                var got = err == null ? ReadChapters(mp4) : new();
                Check(err == null && got.Count == 3 && Math.Abs(got[0].start) < 0.002 && Math.Abs(got[1].start - 2) < 0.002 && Math.Abs(got[2].start - 4.5) < 0.002 && Math.Abs(got[2].end - 6) < 0.05
                      && got[1].title == "Middle", $"export: the MP4 has 3 chapters at 0 / 2 / 4.5 s ending at 6 s, named — ffprobe: {string.Join(", ", got.Select(g => $"{g.start:0.###}-{g.end:0.###} {g.title}"))} {err}");
                ed.MarkIn = 1; ed.MarkOut = 5;
                var mp4b = Path.Combine(dir, "chapters_inout.mp4");
                err = await Exporter.RunAsync(ff, ed, mp4b, new Progress<double>(), CancellationToken.None, null, null, new ExportSettings { Speed = "ultrafast", Height = 360, InOutOnly = true });
                var gotB = err == null ? ReadChapters(mp4b) : new();
                Check(err == null && gotB.Count == 3 && gotB[0].title == "Intro" && Math.Abs(gotB[0].start) < 0.002 && Math.Abs(gotB[1].start - 1) < 0.002 && Math.Abs(gotB[2].start - 3.5) < 0.002,
                      $"an In–Out export (1 … 5 s): Intro at 0, then the chapters at 1 / 3.5 s (an MP4's first chapter starts at 0) — {string.Join(", ", gotB.Select(g => $"{g.start:0.###} {g.title}"))}");
                var mp4c = Path.Combine(dir, "chapters_off.mp4");
                err = await Exporter.RunAsync(ff, ed, mp4c, new Progress<double>(), CancellationToken.None, null, null, new ExportSettings { Speed = "ultrafast", Height = 360, Chapters = false });
                Check(err == null && ReadChapters(mp4c).Count == 0, "chapters off: none in the file (not the source media's either)");
            }

            // ---------- 3. clipboard ----------
            {
                var ed = new Editor(); ed.Media.Add(mPat);
                ed.AddMedia(mPat, Tracks.V1, 0);                                             // linked picture + sound 0 … 30
                ed.Edit(() => { foreach (var c in ed.Clips) c.Dur = 4; });
                var v = ed.Clips.First(c => c.Kind == ClipKind.Video); var a = ed.Clips.First(c => c.Kind == ClipKind.Audio);
                v.Masks.Add(new ObjectMask { Name = "m" }); ed.MaskTracks[v.Masks[0].Id] = new MaskTrack(); ed.MaskTracks[v.Masks[0].Id].Set(0, new List<float[]> { new float[] { 0, 0, 1, 0, 1, 1 } }, true);
                ed.SetSelection(new[] { v });
                Check(ed.CopySelection() == 2, "copy a linked clip: picture and sound");
                ed.Seek(10);
                var pasted = ed.Paste(false);
                var pv = pasted.FirstOrDefault(c => c.Kind == ClipKind.Video); var pa = pasted.FirstOrDefault(c => c.Kind == ClipKind.Audio);
                Check(pasted.Count == 2 && pv != null && pa != null && pv.Id != v.Id && pv.LinkId != null && pv.LinkId == pa.LinkId && pv.LinkId != v.LinkId
                      && Math.Abs(pv.Start - 10) < 1e-9 && pv.Track == v.Track && pa.Track == a.Track && Math.Abs(ed.Time - 14) < 1e-9,
                      "paste at 10 s: new ids, linked to each other (not to the originals), same tracks, the playhead at the end");
                Check(pv!.Masks.Count == 1 && pv.Masks[0].Id != v.Masks[0].Id && ed.MaskTracks.ContainsKey(pv.Masks[0].Id) && !ReferenceEquals(ed.MaskTracks[pv.Masks[0].Id], ed.MaskTracks[v.Masks[0].Id]),
                      "the pasted clip has its own copy of the object mask");
                Check(ed.UndoLabels.First() == "Paste", "one “Paste” undo step");
                ed.Undo();
                Check(ed.Clips.Count == 2, "undo: the pasted clips are gone");
                // overwrite: paste over the middle of the clip
                ed.Seek(1);
                ed.Paste(false);
                var vs = ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).ToList();
                Check(vs.Count == 2 && Math.Abs(vs[0].End - 1) < 1e-6 && Math.Abs(vs[1].Start - 1) < 1e-6 && Math.Abs(vs[1].Dur - 4) < 1e-6 && ed.Clips.Count(c => c.Kind == ClipKind.Audio) == 2,
                      "paste (overwrite) at 1 s: the original is cut at 1 s and its rest replaced by the 4 s copy");
                ed.Undo();
                // insert: everything from the playhead moves later
                ed.Seek(2);
                ed.Paste(true);
                vs = ed.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).ToList();
                Check(vs.Count == 3 && Math.Abs(vs[1].Start - 2) < 1e-6 && Math.Abs(vs[2].Start - 6) < 1e-6 && Math.Abs(vs[2].Dur - 2) < 1e-6 && ed.UndoLabels.First() == "Paste Insert",
                      "paste insert at 2 s: the clip is cut, the copy goes in, the rest moves 4 s later");
                ed.Undo();
                ed.SetLocked(Tracks.V1, true); ed.Seek(20);
                var lp = ed.Paste(false);
                Check(lp.All(c => !ed.Locked(c.Track)) && lp.Any(c => c.Kind == ClipKind.Video && Tracks.IsVideo(c.Track)), "V1 locked: the picture goes onto the next unlocked video track");
                ed.SetLocked(Tracks.V1, false); ed.Undo();
            }

            // ---------- 4. paste / remove attributes ----------
            {
                var ed = new Editor(); ed.Media.Add(mPat);
                var s = new Clip { MediaId = mPat.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = 0, In = 2, Dur = 4, NoAudio = true, Scale = 50, X = 10, Blur = 3,
                                   Grade = new ColorGrade { Exposure = 1 }, Opacity = 70 };
                s.Anim = new() { [AnimProp.X] = new PropTrack { Keys = { new Keyframe { T = 2.5, V = -20 }, new Keyframe { T = 4, V = 20 } } } };
                var t = new Clip { MediaId = mPat.Id, Kind = ClipKind.Video, Track = Tracks.V1, Start = 5, In = 10, Dur = 4, NoAudio = true };
                var snd = new Clip { MediaId = mPat.Id, Kind = ClipKind.Audio, Track = Tracks.A1, Start = 5, In = 10, Dur = 4, Volume = 100 };
                ed.Clips.AddRange(new[] { s, t, snd });
                Check(Editor.AttrsOf(s) == (ClipAttr.Motion | ClipAttr.Opacity | ClipAttr.Color | ClipAttr.Blur), $"the source's attributes: motion, opacity, colour, blur ({Editor.AttrsOf(s)})");
                ed.SetSelection(new[] { t, snd });
                int n = ed.PasteAttributes(s, ClipAttr.Motion | ClipAttr.Color);
                Check(n == 1 && t.Scale == 50 && t.X == 10 && t.Grade != null && !ReferenceEquals(t.Grade, s.Grade) && t.Grade.Exposure == 1 && t.Opacity == 100 && t.Blur == 0 && snd.Volume == 100,
                      "Paste Attributes (motion + colour): the picture gets them (its own copy of the grade), not opacity / blur; the sound clip is left alone");
                Check(t.Anim?[AnimProp.X].Keys is { Count: 2 } ks && Math.Abs(ks[0].T - 10.5) < 1e-9 && Math.Abs(ks[1].T - 12) < 1e-9
                      && Math.Abs(Anim.Value(t, AnimProp.X, 5.5) - Anim.Value(s, AnimProp.X, 0.5)) < 1e-9,
                      "keyframes land on the target's own source times: the same value the same time into the clip");
                Check(ed.UndoLabels.First() == "Paste Attributes", "one “Paste Attributes” step");
                s.Volume = 50; ed.SetSelection(new[] { snd });
                ed.PasteAttributes(new Clip { Kind = ClipKind.Audio, Volume = 50 }, ClipAttr.Volume | ClipAttr.Motion);
                Check(snd.Volume == 50 && snd.Scale == 100, "onto a sound clip: only what sound has (volume)");
                ed.SetSelection(new[] { t });
                ed.RemoveAttributes(ClipAttr.Motion);
                Check(t.Scale == 100 && t.X == 0 && t.Anim == null && t.Grade != null, "Remove Attributes (motion): scale / position and their keys back to the defaults, the grade stays");
                ed.Undo();
                var t2 = ed.Clips.First(c => c.Id == t.Id);
                Check(t2.Scale == 50 && t2.Anim != null, "undo brings them back");
            }

            // ---------- 5. groups, layouts, gaps ----------
            {
                var ed = new Editor(); ed.Media.Add(mPat);
                Clip V(int track, double start, double dur) => new() { MediaId = mPat.Id, Kind = ClipKind.Video, Track = track, Start = start, Dur = dur, NoAudio = true };
                var c1 = V(Tracks.V1, 0, 2); var c2 = V(Tracks.V(2), 0, 4); var c3 = V(Tracks.V1, 3, 2); var c4 = V(Tracks.V(3), 0, 4);
                ed.Clips.AddRange(new[] { c1, c2, c3, c4 });
                ed.SetSelection(new[] { c1, c2 }); ed.GroupSelection();
                ed.Select(c1);
                Check(ed.IsSel(c1) && ed.IsSel(c2) && !ed.IsSel(c3), "a grouped clip selects its group");
                ed.Select(c1, false, false);
                Check(ed.IsSel(c1) && !ed.IsSel(c2), "Alt-click: just the clip");
                ed.Select(c1); ed.UngroupSelection(); ed.Select(c1);
                Check(ed.IsSel(c1) && !ed.IsSel(c2), "ungrouped: only itself again");
                // layouts
                ed.SetSelection(new[] { c4, c2, c1 });
                ed.ApplyLayout(LayoutPreset.Grid2x2);
                Check(c4.Scale == 50 && c4.X == -25 && c4.Y == -25 && c2.X == 25 && c2.Y == -25 && c1.X == -25 && c1.Y == 25, "2 × 2 grid: the highest track top left, then top right, bottom left");
                ed.SetSelection(new[] { c2, c1 });
                ed.ApplyLayout(LayoutPreset.SideBySide);
                var (x2, _, w2, _, _) = Editor.Placement(mPat, c2, 1);
                double left = x2 + w2 * c2.CropL / 100, right = x2 + w2 * (1 - c2.CropR / 100);
                Check(c2.CropL == 25 && c2.CropR == 25 && Math.Abs(left) < 1.5 && Math.Abs(right - Editor.W / 2.0) < 1.5, $"side by side: V2's middle half fills the left half of the frame ({left:0.#} … {right:0.#} of {Editor.W})");
                ed.SetSelection(new[] { c3 }); ed.ApplyLayout(LayoutPreset.PipBottomRight);
                var (x3, y3, w3, h3, _) = Editor.Placement(mPat, c3, 3.5);
                Check(c3.Scale == 33 && x3 + w3 < Editor.W && x3 + w3 > Editor.W * 0.9 && y3 + h3 > Editor.H * 0.9, "picture in picture (bottom right): a third of the size near the corner");
                ed.ApplyLayout(LayoutPreset.Full);
                Check(c3.Scale == 100 && c3.X == 0 && c3.CropL == 0, "Full frame resets it");
                // gaps: V1 0–2, 3–5; V2 0–4; V3 0–4 → no gap in the sequence until 5, V1 has one at 2
                Check(ed.NextGap(0, null) == null && ed.NextGap(0, Tracks.V1) == 2 && ed.PrevGap(4, Tracks.V1) == 2, "gaps: none in the sequence (V2 covers 2–3), one on V1 at 2 s");
                ed.Clips.Remove(c2); ed.Clips.Remove(c4);
                Check(ed.NextGap(0, null) == 2 && ed.PrevGap(2.5, null) == 2, "without V2 / V3: the sequence gap at 2 s");
            }

            // ---------- 6. history ----------
            {
                var ed = new Editor(); ed.Media.Add(mPat);
                ed.AddMedia(mPat, Tracks.V1, 0);
                ed.Seek(2); ed.AddEdit(false);
                ed.SelectAll(); ed.DeleteSelection(false);
                ed.NewSequence();
                var labels = ed.UndoLabels.Reverse().ToList();
                Check(labels.SequenceEqual(new[] { "Add Clip", "Add Edit", "Delete", "New Sequence" }), $"the History panel's names: {string.Join(" / ", labels)}");
                ed.StepHistory(-3);
                Check(ed.UndoLabels.Count() == 1 && ed.RedoLabels.First() == "Add Edit" && ed.Clips.Count == 2 && ed.Sequences.Count == 1, "three steps back: one sequence, the clip uncut");
                ed.StepHistory(2);
                Check(ed.UndoLabels.Count() == 3 && ed.Clips.Count == 0 && ed.RedoLabels.Single() == "New Sequence", "two forward: cut and deleted, New Sequence can still be redone");
                Check(HistoryLabels.Of("ApplyStyle", @"C:\x\InspectorText.cs") == "Title" && HistoryLabels.Of("SomeThing_Click", @"C:\x\MainWindow.xaml.cs") == "Some Thing", "names from the panel's file / the method");
            }

            // ---------- 7. keyboard shortcuts ----------
            {
                var saved = new Dictionary<string, string[]>(AppSettings.Current.Shortcuts);
                AppSettings.Current.Shortcuts.Clear(); Keymap.ResetAll();
                Check(Keymap.Gesture(Key.K, ModifierKeys.Control | ModifierKeys.Shift) == "Ctrl+Shift+K" && Keymap.Gesture(Key.OemSemicolon, ModifierKeys.Shift) == "Shift+;"
                      && Keymap.Gesture(Key.M, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift) == "Ctrl+Alt+Shift+M" && Keymap.Gesture(Key.LeftShift, ModifierKeys.Shift) == null,
                      "gesture text: Ctrl+Shift+K, Shift+;, Ctrl+Alt+Shift+M; a modifier alone is none");
                Check(Keymap.Find("Ctrl+K") == "edit.addEdit" && Keymap.Find("M") == "marker.add" && Keymap.Find("\\") == "tl.zoomSeq" && Keymap.Find("Backspace") == "edit.delete", "Premiere's defaults: Ctrl+K, M, \\, Backspace");
                var dupes = Keymap.All.SelectMany(c => c.Defaults.Select(g => (g, c.Id))).GroupBy(x => x.g).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                Check(dupes.Count == 0, "no default gesture is used twice" + (dupes.Count > 0 ? ": " + string.Join(", ", dupes) : ""));
                Keymap.Set("marker.add", new[] { "Ctrl+K" });
                Check(Keymap.Find("Ctrl+K") == "marker.add" && Keymap.Of("edit.addEdit").Length == 0 && Keymap.Find("M") == null, "Ctrl+K given to Add Marker: taken from Add Edit, M is free");
                Keymap.Reset("edit.addEdit");
                Check(Keymap.Find("Ctrl+K") == "edit.addEdit" && Keymap.Of("marker.add").Length == 0, "Add Edit reset: it takes Ctrl+K back");
                Keymap.ResetAll();
                Check(AppSettings.Current.Shortcuts.Count == 0 && Keymap.Find("M") == "marker.add", "reset all: the defaults");
                foreach (var kv in saved) AppSettings.Current.Shortcuts[kv.Key] = kv.Value;
            }

            // ---------- 8. media: timecode, relink, collect ----------
            {
                var mt = await Imp(tcF);
                Check(mt.Timecode == "10:00:00:00" && TimeUtil.SourceTimecode(mt, 1.5) == "10:00:01:12", $"a file's timecode tag: {mt.Timecode} (at 1.5 s: {TimeUtil.SourceTimecode(mt, 1.5)}), reel “{mt.Reel}”");
                Check(mt.Meta.Any(kv => kv.Key == "Container") && mt.Meta.Any(kv => kv.Key == "Video"), "Properties: container and video stream");
                var df = new MediaItem { Fps = 30000 / 1001.0, Timecode = "00:00:59;28" };
                Check(TimeUtil.SourceTimecode(df, 1 / df.Fps) == "00:00:59;29" && TimeUtil.SourceTimecode(df, 2 / df.Fps) == "00:01:00;02"
                      && TimeUtil.SourceTimecode(new MediaItem { Fps = df.Fps, Timecode = "00:09:59;29" }, 1 / df.Fps) == "00:10:00;00",
                      "drop-frame timecode: 00:00:59;29 → 00:01:00;02 (;00 and ;01 skipped), but 00:10:00;00 is kept");
                // relink: two files moved to another folder
                var moved = Path.Combine(dir, "moved"); Directory.CreateDirectory(moved);
                var a = new MediaItem { Path = Path.Combine(dir, "gone", "pat.mp4") }; var b = new MediaItem { Path = Path.Combine(dir, "gone", "tc.mov") };
                File.Copy(patF, Path.Combine(moved, "pat.mp4"), true); File.Copy(tcF, Path.Combine(moved, "tc.mov"), true);
                a.RefreshOnline(); b.RefreshOnline();
                var found = MainWindow.FindInFolder(new[] { a, b }, moved);
                Check(found.Count == 2 && !a.Offline && a.Path == Path.Combine(moved, "pat.mp4"), "relink: locating one file finds the other missing one in the same folder");
                // collect: two files of one name from different folders, an offline one, a .cube look
                var other = Path.Combine(dir, "other"); Directory.CreateDirectory(other);
                File.Copy(patF, Path.Combine(other, "pat.mp4"), true);
                var cube = Path.Combine(dir, "look.cube"); Grade.WriteCube(Grade.For(new ColorGrade { Exposure = 0.5 })!, cube);
                var ed = new Editor();
                var m1 = await Imp(patF); var m2 = await Imp(Path.Combine(other, "pat.mp4")); var m3 = new MediaItem { Path = Path.Combine(dir, "nowhere.mp4"), Type = ClipKind.Video };
                ed.Media.AddRange(new[] { m1, m2, m3 });
                ed.Clips.Add(new Clip { MediaId = m1.Id, Kind = ClipKind.Video, Track = Tracks.V1, Dur = 2, Grade = new ColorGrade { Look = cube, LookAmount = 100 } });
                var json = JsonSerializer.Serialize(new ProjectFile { Media = ed.Media, Sequences = ed.Sequences, Active = ed.Seq.Id });
                var target = Path.Combine(dir, "collected"); if (Directory.Exists(target)) Directory.Delete(target, true);
                var mediaDir = Path.Combine(target, "Untitled Media");
                var (pf, files, offline) = MainWindow.CollectPlan(json, mediaDir);
                Check(files.Count == 3 && files.Select(f => Path.GetFileName(f.to)).OrderBy(x => x).SequenceEqual(new[] { "look.cube", "pat (2).mp4", "pat.mp4" }) && offline.Count == 1,
                      $"collect: 3 files (the two pat.mp4 kept apart, the look), the offline one listed — {string.Join(", ", files.Select(f => Path.GetFileName(f.to)))}");
                Directory.CreateDirectory(mediaDir);
                foreach (var (from, to) in files) File.Copy(from, to, true);
                var projFile = Path.Combine(target, "Untitled.cutpro");
                File.WriteAllText(projFile, JsonSerializer.Serialize(pf));
                var reopened = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(projFile))!;
                Check(reopened.Media.Where(x => x.Path.StartsWith(mediaDir)).All(x => File.Exists(x.Path)) && reopened.Media.Count(x => x.Path.StartsWith(mediaDir)) == 2
                      && reopened.AllSequences()[0].Clips[0].Grade!.Look!.StartsWith(mediaDir) && File.Exists(reopened.AllSequences()[0].Clips[0].Grade!.Look),
                      "the collected project opens with every file inside its folder (the look too); the original still points where it was");
                Check(ed.Media[0].Path == patF, "the open project is unchanged");
            }

            // ---------- 9. language table ----------
            {
                Loc.Use("de");
                Check(Loc.T("File") == "Datei" && Loc.T("Edit: Undo") == "Bearbeiten: Rückgängig" && Loc.English("Datei") == "File" && Loc.T("Something else") == "Something else",
                      "German: File → Datei, “Edit: Undo” → “Bearbeiten: Rückgängig”, back to English, unknown text kept");
                Loc.Use("fr");
                bool menus = new[] { "File", "Edit", "View", "Sequence", "Markers", "Publish", "Window", "Help" }.All(x => Loc.T(x) != x || x == "Publish");
                Check(menus && Loc.T("Cancel") == "Annuler", "French: every menu-bar title, Cancel → Annuler");
                var missing = Keymap.All.Where(c => Loc.T(c.Name) == c.Name).Select(c => c.Name).ToList();
                Check(missing.Count <= Keymap.All.Length / 4, $"most shortcut commands are translated ({Keymap.All.Length - missing.Count} of {Keymap.All.Length})");
                Loc.Use("en");
            }
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "featuretest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>Chapters of a file (ffprobe): start, end (seconds) and title.</summary>
    static List<(double start, double end, string title)> ReadChapters(string file)
    {
        var fp = MediaProbe.FindFfprobe()!;
        var psi = new ProcessStartInfo(fp) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-print_format", "json", "-show_chapters", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var json = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        var res = new List<(double, double, string)>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("chapters", out var chs))
            foreach (var c in chs.EnumerateArray())
            {
                double D(string k) => double.Parse(c.GetProperty(k).GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                var title = c.TryGetProperty("tags", out var t) && t.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                res.Add((D("start_time"), D("end_time"), title));
            }
        return res;
    }
    static void Exec(string ff, string args) { using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!; p.StandardError.ReadToEnd(); p.WaitForExit(); }
}
