namespace CutPro;

/// <summary>Headless checks of the editing model: CutPro.exe --selftest out.txt (exit code 0 = all passed).</summary>
public static class SelfTest
{
    public static int Run(string outFile)
    {
        var log = new List<string>(); int fails = 0;
        void Check(string name, bool ok) { log.Add((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-3;
        static bool NearT(double a, double b, double tol) => Math.Abs(a - b) < tol;

        Editor Fresh(out MediaItem m)
        {
            var ed = new Editor();
            m = new MediaItem { Path = "x.mp4", Type = ClipKind.Video, Duration = 10, Width = 1280, Height = 720, HasAudio = true };
            ed.Media.Add(m); ed.AddMedia(m, null, 0); return ed;
        }

        // linked import
        var ed = Fresh(out var media);
        var v = ed.Clips.Single(c => c.Kind == ClipKind.Video); var a = ed.Clips.Single(c => c.Kind == ClipKind.Audio);
        Check("video+audio imported as linked pair on V1/A1", v.LinkId != null && v.LinkId == a.LinkId && v.Track == 2 && a.Track == 3 && v.NoAudio);
        Check("clicking selects linked partner", ed.Sel.Count == 2);

        // razor cut twice -> three linked pairs
        ed.Edit(() => ed.CutClips(ed.Linked(v), 3.01));
        Check("razor cut is frame-accurate", ed.Clips.Any(c => Near(c.Start, 3.0)));
        var right = ed.Clips.Where(c => Near(c.Start, 3.0)).ToList();
        Check("cut splits both linked clips", ed.Clips.Count == 4 && right.Count == 2);
        Check("right pieces linked to each other, not to left", right[0].LinkId == right[1].LinkId && right[0].LinkId != v.LinkId);
        Check("right piece source In advanced", right.All(c => Near(c.In, 3.0)));
        ed.Edit(() => ed.CutClips(ed.Linked(right[0]), 6));
        Check("second cut -> 6 clips", ed.Clips.Count == 6);

        // select the middle piece and ripple delete -> gap closes on both tracks
        var mid = ed.Clips.First(c => c.Kind == ClipKind.Video && Near(c.Start, 3));
        ed.Select(mid);
        Check("middle selection includes its audio", ed.Sel.Count == 2);
        ed.DeleteSelection(true);
        Check("ripple delete removes pair", ed.Clips.Count == 4);
        Check("ripple delete closes gap (end 7s)", Near(ed.End, 7));
        Check("following piece keeps source In 6s", ed.Clips.Where(c => Near(c.Start, 3)).All(c => Near(c.In, 6)));

        ed.Undo();
        Check("undo restores 6 clips / 10s", ed.Clips.Count == 6 && Near(ed.End, 10));
        ed.Redo();
        Check("redo re-applies ripple delete", ed.Clips.Count == 4 && Near(ed.End, 7));
        ed.Undo();

        // plain delete leaves a gap; selecting the gap and pressing Delete ripples it
        ed.Select(ed.Clips.First(c => c.Kind == ClipKind.Video && Near(c.Start, 3)));
        ed.DeleteSelection(false);
        Check("delete leaves gap (end stays 10s)", ed.Clips.Count == 4 && Near(ed.End, 10));
        ed.SelectGap(2, 3, 6);
        ed.DeleteSelection(false);
        Check("deleting a gap ripples all tracks", Near(ed.End, 7) && ed.Clips.Count(c => Near(c.Start, 3)) == 2);

        // Alt-style single-sided cut (only the clicked clip)
        ed = Fresh(out media);
        v = ed.Clips.Single(c => c.Kind == ClipKind.Video);
        ed.Edit(() => ed.CutClips(new[] { v }, 4));
        Check("alt-cut splits only the video", ed.Clips.Count(c => c.Kind == ClipKind.Video) == 2 && ed.Clips.Count(c => c.Kind == ClipKind.Audio) == 1);

        // Ctrl+K with no selection cuts every track at the playhead
        ed = Fresh(out media); ed.ClearSelection(); ed.Time = 2;
        ed.AddEdit(false);
        Check("Add Edit (Ctrl+K) cuts all tracks at playhead", ed.Clips.Count == 4 && ed.Clips.Count(c => Near(c.Start, 2)) == 2);

        // Q / W ripple trims
        ed.Time = 5; ed.RippleTrim(true);   // remove 2..5
        Check("Q removes previous edit..playhead and ripples", Near(ed.End, 7) && Near(ed.Time, 2));
        ed.Time = 4; ed.RippleTrim(false);  // remove 4..7
        Check("W removes playhead..next edit and ripples", Near(ed.End, 4));

        // In/Out extract and lift
        ed = Fresh(out media); ed.Time = 1; ed.SetMark(true); ed.Time = 2.5; ed.SetMark(false);
        ed.LiftExtract(true);
        Check("Extract removes In..Out from all tracks and closes gap", Near(ed.End, 8.5) && ed.Clips.Count == 4);
        ed = Fresh(out media); ed.Time = 1; ed.SetMark(true); ed.Time = 2.5; ed.SetMark(false);
        ed.LiftExtract(false);
        Check("Lift removes In..Out and leaves a gap", Near(ed.End, 10) && ed.Clips.Count == 4);

        // unlink / relink
        ed = Fresh(out media); ed.SelectAll(); ed.ToggleLink();
        Check("Ctrl+L unlinks", ed.Clips.All(c => c.LinkId == null));
        ed.ToggleLink();
        Check("Ctrl+L relinks", ed.Clips.Select(c => c.LinkId).Distinct().Count() == 1 && ed.Clips[0].LinkId != null);

        // titles: legacy migration, roles/themes, styles
        var legacy = new Clip { Kind = ClipKind.Text, Color = "#FFE14A", Size = 70 };
        Check("legacy title colour/size migrate into its style", legacy.Look.Fill == "#FFE14A" && Near(legacy.Look.Size, 70));
        ed = Fresh(out media); ed.ThemeName = "Modern"; ed.Time = 1;
        ed.AddTitle(TextRole.LowerThird);
        var lt = ed.Selected!;
        Check("lower third: role, theme style, left anchored", lt.Role == TextRole.LowerThird && lt.Look.Align == "Left" && Near(lt.X, -42) && lt.Look.Box);
        lt.Look.Size = 99;
        Check("editing a title never changes the theme/library style", !Near(Themes.Get("Modern").LowerThird.Size, 99));
        ed.AddTitle(TextRole.Title);
        var ti = ed.Selected!;
        ed.SetSelection(new[] { ti, ed.Clips.First(c => c.Kind == ClipKind.Video) });
        ed.ApplyStyle(StyleLibrary.Get("Neon Glow"));
        Check("apply style changes selected titles only", ti.Look.Glow && !lt.Look.Glow && ed.Clips.First(c => c.Kind == ClipKind.Video).Style == null);
        ed.ApplyTheme("Retro 80s");
        Check("apply theme restyles every title by role", ed.ThemeName == "Retro 80s" && ti.Look.Gradient && lt.Look.Italic && lt.Look.Align == "Left");
        ed.Undo();
        var tiU = ed.Clips.First(c => c.Id == ti.Id);
        Check("undo restores styles before the theme", tiU.Look.Glow && !tiU.Look.Gradient);
        var before = ed.Clips.Count;
        ed.Edit(() => ed.CutClips(new[] { tiU }, tiU.Start + 1));
        var pieces = ed.Clips.Where(c => c.Kind == ClipKind.Text && c.Role == TextRole.Title).ToList();
        pieces[0].Look.Size = 11;
        Check("razor cut of a title keeps its style, pieces independent", ed.Clips.Count == before + 1 && pieces.Count == 2 && pieces[1].Look.Glow && !Near(pieces[1].Look.Size, 11));

        // object masks
        var mv = ed.Clips.First(c => c.Kind == ClipKind.Video);
        ed.Time = mv.Start + 0.5;
        var mk = ed.AddMask(mv, MaskKind.Ellipse, new() { MaskGeometry.Shape(MaskKind.Ellipse, new System.Windows.Rect(0.4, 0.4, 0.2, 0.2)) }, Editor.SourceFrame(mv, ed.Time));
        Check("add mask: on the clip, outline keyed at the source frame", mv.Masks.Count == 1 && ed.MaskTracks[mk.Id].At(Editor.SourceFrame(mv, ed.Time)) != null);
        ed.Undo();
        Check("undo removes the mask", ed.Clips.First(c => c.Id == mv.Id).Masks.Count == 0);
        ed.Redo();
        mv = ed.Clips.First(c => c.Id == mv.Id);
        ed.Edit(() => ed.CutClips(new[] { mv }, mv.Start + 0.25));
        var halves = ed.Clips.Where(c => c.MediaId == mv.MediaId && c.Kind == ClipKind.Video && c.Masks.Count == 1).ToList();
        halves[0].Masks[0].Amount = 77;
        Check("razor cut keeps the mask on both halves (shared outline, independent settings)",
              halves.Count == 2 && halves[0].Masks[0].Id == halves[1].Masks[0].Id && !Near(halves[1].Masks[0].Amount, 77));
        ed.RemoveMask(halves[0], mk.Id);
        Check("deleting one half's mask keeps the other's outline", halves[1].Masks.Count == 1 && ed.MaskTracks.ContainsKey(mk.Id));

        // mask keyframes
        var kt = ed.MaskTracks[mk.Id];
        int k0 = kt.Keys.Min;
        var moved = new List<float[]> { MaskGeometry.Shape(MaskKind.Ellipse, new System.Windows.Rect(0.6, 0.2, 0.2, 0.2)) };
        ed.SetMaskFrame(mk.Id, k0 + 30, moved);
        var half = kt.At(k0 + 15)!; var (mcx, mcy) = MaskGeometry.Centroid(half);
        Check("keyframes interpolate: halfway outline sits halfway", NearT(mcx, 0.6, 0.01) && NearT(mcy, 0.4, 0.01) && ReferenceEquals(half, kt.At(k0 + 15)));
        var shaped = new List<float[]> { MaskGeometry.Shape(MaskKind.Rectangle, new System.Windows.Rect(0.1, 0.1, 0.3, 0.1)) };
        ed.SetMaskFrame(mk.Id, k0 + 60, shaped);
        var (rcx, _) = MaskGeometry.Centroid(kt.At(k0 + 45)!);
        Check("keyframes of different shapes (vertex counts) interpolate", NearT(rcx, (0.7 + 0.25) / 2, 0.02) && kt.At(k0 + 45)!.Count == 1);
        ed.Undo();
        Check("undo removes a keyframe", !kt.IsKey(k0 + 60) && kt.Keys.Count == 2);
        ed.Redo();
        Check("redo restores it", kt.IsKey(k0 + 60) && ReferenceEquals(kt.Frames[k0 + 60], shaped));
        kt.Set(k0 + 31, moved); kt.Set(k0 + 32, moved);                       // "tracked" frames
        ed.ClearTracking(mk.Id);
        Check("clear tracking keeps keyframes only", kt.Frames.Count == 3 && kt.Keys.Count == 3);
        ed.MaskBegin(mk.Id);
        for (int i = 0; i < 5; i++) kt.Set(k0 + 30, MaskGeometry.Scale(moved, 1 + i * 0.1, 0.7, 0.3), key: true);    // a drag
        ed.MaskEnd();
        ed.Undo();
        Check("a monitor drag is one undo step", ReferenceEquals(kt.Frames[k0 + 30], moved));
        ed.ToggleKey(mk.Id, k0 + 20);
        Check("add key at a frame keeps the shown (interpolated) outline", kt.IsKey(k0 + 20) && kt.Frames[k0 + 20].Count == 1);
        ed.ToggleKey(mk.Id, k0 + 20);
        Check("remove key", !kt.IsKey(k0 + 20) && !kt.Frames.ContainsKey(k0 + 20));
        var json = System.Text.Json.JsonSerializer.Serialize(kt);
        var back = System.Text.Json.JsonSerializer.Deserialize<MaskTrack>(json)!;
        var oldTrack = System.Text.Json.JsonSerializer.Deserialize<MaskTrack>(json.Replace("\"Keys\":[" + string.Join(",", kt.Keys) + "]", "\"Keys\":[]"))!;
        Check("keyframes save/load; old projects key their seed frame", back.Keys.SetEquals(kt.Keys) && oldTrack.Keys.Count == 1 && oldTrack.IsKey(oldTrack.SeedFrame));

        // ---- clip keyframes + formulas ----
        var kc = new Clip { Kind = ClipKind.Image, Track = 2, Start = 10, In = 2, Dur = 4 };
        Anim.SetKey(kc, AnimProp.Opacity, 2, 0); Anim.SetKey(kc, AnimProp.Opacity, 4, 100);
        Check("linear keyframes: halfway = 50 (source-anchored: clip 11 s = source 3 s)", NearT(Anim.Value(kc, AnimProp.Opacity, 11), 50, 1e-6));
        var ka = Anim.Track(kc, AnimProp.Opacity)!.Keys[0]; var kb = Anim.Track(kc, AnimProp.Opacity)!.Keys[1];
        Anim.ApplyInterp(ka, "Easy Ease"); Anim.ApplyInterp(kb, "Easy Ease");
        double e25 = Anim.Value(kc, AnimProp.Opacity, 10.5), e50 = Anim.Value(kc, AnimProp.Opacity, 11), e75 = Anim.Value(kc, AnimProp.Opacity, 11.5);
        Check("easy ease: slow start, symmetric, halfway = 50", e25 < 20 && NearT(e50, 50, 0.5) && NearT(e75, 100 - e25, 0.5));
        Anim.ApplyInterp(ka, "Hold");
        Check("hold: value stays until the next key", NearT(Anim.Value(kc, AnimProp.Opacity, 11.9), 0, 1e-9) && NearT(Anim.Value(kc, AnimProp.Opacity, 12), 100, 1e-9));
        Anim.SetKey(kc, AnimProp.Opacity, 3, 80); foreach (var k in Anim.Track(kc, AnimProp.Opacity)!.Keys) Anim.ApplyInterp(k, "Auto Bezier");
        Check("auto Bézier passes through its keys", NearT(Anim.Value(kc, AnimProp.Opacity, 11), 80, 1e-6) && Anim.Value(kc, AnimProp.Opacity, 11.9) <= 100);
        double F(string f, double t = 11) { var c2 = new Clip { Kind = ClipKind.Image, Start = 10, Dur = 4, Scale = 100 }; Anim.Ensure(c2, AnimProp.X).Formula = f; return Anim.Value(c2, AnimProp.X, t); }
        Check("formula: precedence and power", NearT(F("2 + 3 * 4 ^ 2"), 50, 1e-9) && NearT(F("-2^2"), -4, 1e-9) && NearT(F("2^-1"), 0.5, 1e-9) && NearT(F("7 % 3"), 1, 1e-9));
        Check("formula: variables t / time / frame / dur / value and other properties", NearT(F("t"), 1, 1e-9) && NearT(F("time"), 11, 1e-9) && NearT(F("frame"), 30, 1e-9)
              && NearT(F("dur"), 4, 1e-9) && NearT(F("value + 5"), 5, 1e-9) && NearT(F("scale / 2"), 50, 1e-9));
        Check("formula: functions and ternary", NearT(F("max(1, 5, 3) + clamp(9, 0, 4)"), 9, 1e-9) && NearT(F("t > 0.5 ? 10 : 20"), 10, 1e-9)
              && NearT(F("linear(t, 0, 2, 0, 100)"), 50, 1e-9) && NearT(F("round(3.14159, 2)"), 3.14, 1e-9) && NearT(F("sin(pi / 2)"), 1, 1e-9));
        Check("formula errors say where", Formula.Check("sin(") is { } e1 && e1.Contains("column") && Formula.Check("foo + 1")!.Contains("unknown name 'foo'")
              && Formula.Check("max(1,,2)") != null && Formula.Check("1 +") != null && Formula.Check("wiggle(2, 10)") == null);
        var wc = new Clip { Kind = ClipKind.Image, Start = 0, Dur = 10 };
        Anim.Ensure(wc, AnimProp.X).Formula = "wiggle(2, 10)"; Anim.Ensure(wc, AnimProp.Y).Formula = "wiggle(2, 10)";
        var wx = Enumerable.Range(0, 300).Select(i => Anim.Value(wc, AnimProp.X, i / 30.0)).ToList();
        Check("wiggle: deterministic, within ±amount, X and Y differ", wx.SequenceEqual(Enumerable.Range(0, 300).Select(i => Anim.Value(wc, AnimProp.X, i / 30.0)))
              && wx.All(v => Math.Abs(v) <= 10.0001) && wx.Max() - wx.Min() > 5 && Enumerable.Range(0, 30).Any(i => Math.Abs(Anim.Value(wc, AnimProp.Y, i / 10.0) - Anim.Value(wc, AnimProp.X, i / 10.0)) > 1));
        var cy = new Clip { Kind = ClipKind.Image, Start = 0, Dur = 10 };
        Anim.SetKey(cy, AnimProp.Rotation, 0, 0); Anim.SetKey(cy, AnimProp.Rotation, 1, 90);
        Anim.Ensure(cy, AnimProp.Rotation).Formula = "cycle()";
        Check("cycle(): keyframes repeat after the last key", NearT(Anim.Value(cy, AnimProp.Rotation, 1.5), 45, 1e-6) && NearT(Anim.Value(cy, AnimProp.Rotation, 2.5), 45, 1e-6));
        Anim.Ensure(cy, AnimProp.Rotation).Formula = "pingpong()";
        Check("pingpong(): keyframes play back and forth", NearT(Anim.Value(cy, AnimProp.Rotation, 1.25), 67.5, 1e-6));
        var cl = new Clip { Kind = ClipKind.Image, Start = 0, Dur = 1 }; Anim.SetKey(cl, AnimProp.Opacity, 0, 150); Anim.Ensure(cl, AnimProp.Opacity).Formula = "value * 3";
        Check("values are clamped to the property's range", NearT(Anim.Value(cl, AnimProp.Opacity, 0.5), 100, 1e-9));
        // keyframes are source-anchored: a razor cut keeps the animation on the picture
        var ed3 = new Editor();
        var ac = new Clip { Kind = ClipKind.Image, Track = 2, Start = 0, Dur = 4 }; ed3.Clips.Add(ac);
        Anim.SetKey(ac, AnimProp.Scale, 0, 50); Anim.SetKey(ac, AnimProp.Scale, 4, 150);
        double sBefore = Anim.Value(ac, AnimProp.Scale, 3);
        ed3.Edit(() => ed3.CutClips(new[] { ac }, 2));
        var rPiece = ed3.Clips.OrderBy(c => c.Start).Last();
        Check("razor cut: both pieces keep the keyframes, the animation continues", NearT(Anim.Value(rPiece, AnimProp.Scale, 3), sBefore, 1e-6) && Anim.Track(rPiece, AnimProp.Scale)!.Keys.Count == 2);
        ed3.Edit(() => Anim.SetKey(rPiece, AnimProp.Scale, 3, 999));
        ed3.Undo();
        Check("keyframe edits undo", NearT(Anim.Value(ed3.Clips.OrderBy(c => c.Start).Last(), AnimProp.Scale, 3), sBefore, 1e-6));
        var cJson = System.Text.Json.JsonSerializer.Serialize(ed3.Clips.OrderBy(c => c.Start).Last());      // (undo restored new objects)
        var cBack = System.Text.Json.JsonSerializer.Deserialize<Clip>(cJson)!;
        Check("keyframes + formulas save and load", cJson.Contains("\"Scale\"") && NearT(Anim.Value(cBack, AnimProp.Scale, 3), sBefore, 1e-6));

        // transitions: Ctrl+D at the cut nearest the playhead, one per edit point, delete + undo, Shift+D on a selection
        var et = new Editor();
        var x1 = new Clip { Kind = ClipKind.Image, Track = 2, Start = 0, Dur = 3 }; var x2 = new Clip { Kind = ClipKind.Image, Track = 2, Start = 3, Dur = 3 };
        var xa = new Clip { Kind = ClipKind.Audio, Track = 3, Start = 0, Dur = 3, In = 2 }; var xb = new Clip { Kind = ClipKind.Audio, Track = 3, Start = 3, Dur = 3, In = 2 };
        et.Clips.AddRange(new[] { x1, x2, xa, xb });
        et.Time = 3.4;
        int nv = et.ApplyDefaultTransitions(video: true, audio: false);
        Check("Ctrl+D: the default video transition at the cut nearest the playhead (video track only)", nv == 1 && x2.TransIn?.Kind == Transitions.DefaultVideo && xb.TransIn == null);
        int na = et.ApplyDefaultTransitions(video: false, audio: true);
        var plan = Transitions.Compute(et);
        Check("Ctrl+Shift+D: the default audio crossfade, centred on the cut (2 windows resolved)", na == 1 && xb.TransIn?.Kind == Transitions.DefaultAudio && plan.Wins.Count == 2
              && plan.Wins.All(w => NearT(w.T0, 2.5, 1e-6) && NearT(w.T1, 3.5, 1e-6)));
        et.Edit(() => et.PlaceTransition(x1, false, "Wipe Left"));
        Check("a tail transition on a clip followed by another lands on the next clip's head (one per cut)", x2.TransIn?.Kind == "Wipe Left" && x1.TransOut == null);
        et.SelectTransition(x2, true); et.DeleteSelection(false);
        Check("Delete removes the selected transition", x2.TransIn == null && et.SelTrans == null);
        et.Undo();
        Check("undo brings it back", et.Clips.First(c => c.Start == 3 && c.Track == 2).TransIn?.Kind == "Wipe Left");
        et.Select(et.Clips.First(c => c.Start == 0 && c.Track == 2), withLinks: false);
        et.ApplyDefaultTransitions(true, false);
        var first = et.Clips.First(c => c.Start == 0 && c.Track == 2);
        Check("Shift+D on a selected clip: transitions at its head (from nothing) and tail (the cut)", first.TransIn != null && Transitions.Compute(et).HeadOf(first) is { A: null });

        // audio mixer: pan law, solo / mute, the export graph carries fader × pan per clip, master and normalization
        var (pl, pr) = Mixer.PanGains(-50);
        Check("pan law: centre unity, half left = right −3 dB, full right = left silent",
              Near(Mixer.PanGains(0).l, 1) && Near(Mixer.PanGains(0).r, 1) && Near(pl, 1) && Near(pr, Math.Sqrt(0.5)) && Mixer.PanGains(100).l < 1e-9);
        var ea = new Editor(); var am = new MediaItem { Path = "x.wav", Type = ClipKind.Audio, Duration = 10, HasAudio = true }; ea.Media.Add(am);
        ea.Clips.Add(new Clip { MediaId = am.Id, Kind = ClipKind.Audio, Track = 3, Start = 0, Dur = 4 });
        ea.Clips.Add(new Clip { MediaId = am.Id, Kind = ClipKind.Audio, Track = 4, Start = 1, Dur = 4 });
        ea.Mix.Channels[3].Gain = -6; ea.Mix.Channels[3].Pan = 100; ea.Mix.Master = 3;
        var wavO = new ExportSettings { Video = "none", Container = "wav", Audio = "pcm" };
        static int Count(string s, string what) => s.Split(what).Length - 1;
        static string Gs(double v) => v.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture);
        var g1 = Exporter.Build(ea, "o.wav", null, null, wavO).script;
        Check("export graph: fader × pan per clip, master after the mix, positions in whole samples",
              g1.Contains($"pan=stereo|c0=0*c0|c1={Gs(Mixer.TrackGains(ea, 3).r)}*c1") && g1.Contains($"volume={Gs(Mixer.MasterGain(ea))}") && g1.Contains("adelay=delays=48000S") && g1.Contains("atrim=start_sample=0:end_sample=192000"));
        ea.Mix.Channels[4].Solo = true;
        var g2 = Exporter.Build(ea, "o.wav", null, null, wavO).script;
        Check("solo A2: only A2 is heard (and exported)", Count(g2, "adelay=") == 1 && g2.Contains("delays=48000S") && Mixer.Audible(ea, 4) && !Mixer.Audible(ea, 3));
        ea.Mix.Channels[4].Solo = false; ea.TrackOff[4] = true;
        var g3 = Exporter.Build(ea, "o.wav", null, null, wavO).script;
        Check("mute A2: A2 isn't exported", Count(g3, "adelay=") == 1 && g3.Contains("delays=0S") && !Mixer.Audible(ea, 4));
        ea.TrackOff[4] = false;
        var g4 = Exporter.Build(ea, "o.wav", null, null, new ExportSettings { Video = "none", Container = "wav", Audio = "pcm", Normalize = true, NormGainDb = 6, NormLimit = true }).script;
        var g5 = Exporter.Build(ea, "o.wav", null, null, new ExportSettings { Video = "none", Container = "wav", Audio = "pcm", Normalize = false, NormGainDb = 6, NormLimit = true }).script;
        Check("normalization: one gain with the master (+ the limiter when peaks need it); ignored when switched off",
              g4.Contains($"volume={Gs(Mixer.MasterGain(ea) * Math.Pow(10, 6 / 20.0))}") && g4.Contains("alimiter=") && !g5.Contains("alimiter") && g5.Contains($"volume={Gs(Mixer.MasterGain(ea))}"));

        // speed / reverse / hold (TimeMap; frame-exact checks with real media are in --speedtest)
        var sm = new MediaItem { Path = "s.mp4", Type = ClipKind.Video, Duration = 20, Width = 1280, Height = 720, HasAudio = true, Fps = 30 };
        var es = new Editor(); es.Media.Add(sm);
        es.AddMedia(sm, null, 0);
        var sv = es.Clips.First(c => c.Kind == ClipKind.Video); var sa = es.Clips.First(c => c.Kind == ClipKind.Audio);
        es.Select(sv); es.SetSpeed(es.Clips.Where(es.IsSel), 200, null, false, true, false);
        Check("Speed/Duration 200 %: half as long, linked sound follows, source runs twice as fast",
              Near(sv.Dur, 10) && Near(sa.Dur, 10) && Near(sa.Speed, 200) && Near(TimeMap.Src(sv, 1), 2) && TimeMap.Label(sv).Contains("200%"));
        es.SetSpeed(es.Clips.Where(es.IsSel), null, null, true, true, false);
        Check("reverse: starts on the last frame and runs back", Near(TimeMap.Src(sv, 0), 20 - 2.0 / 30) && TimeMap.Src(sv, 1) < TimeMap.Src(sv, 0) && TimeMap.Label(sv).Contains("-200%"));
        es.Undo(); es.Undo();
        sv = es.Clips.First(c => c.Kind == ClipKind.Video);
        es.AddFrameHold(sv, 0);
        Check("Add Frame Hold at the clip's start freezes the whole clip (no cut)", es.Clips.Count == 2 && es.Clips.First(c => c.Kind == ClipKind.Video).Hold && TimeMap.Label(es.Clips.First(c => c.Kind == ClipKind.Video)).Contains("hold"));

        // blend mode / key / crop / flip ride along with the clip: razor cut, undo, project file (preview / export checks are in --compositetest)
        var ek = new Editor(); ek.Media.Add(sm);
        ek.AddMedia(sm, 1, 0);
        var kv = ek.Clips.First(c => c.Kind == ClipKind.Video);
        ek.Edit(() =>
        {
            kv.Blend = BlendMode.Screen; kv.Key = new ChromaKey { Color = "#00B140", Tolerance = 40, GarbageL = 20 }; kv.FlipH = true; kv.CropFeather = 12;
            Anim.SetKey(kv, AnimProp.CropL, 2, 0); Anim.SetKey(kv, AnimProp.CropL, 8, 50);
        });
        string kBefore = System.Text.Json.JsonSerializer.Serialize(ek.Clips);
        ek.Edit(() => ek.CutClips(new[] { kv }, 5));
        var kHalves = ek.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).ToList();
        Check("razor cut: both pieces keep the blend mode, key, flip and crop; the crop keys stay on their pictures",
              kHalves.Count == 2 && kHalves.All(h => h.Blend == BlendMode.Screen && h.Key?.Tolerance == 40 && h.FlipH && h.CropFeather == 12)
              && !ReferenceEquals(kHalves[0].Key, kHalves[1].Key) && Near(Anim.Value(kHalves[1], AnimProp.CropL, 5), 25) && Crop.Animated(kHalves[1])
              && Anim.PropsOf(kHalves[0]).Contains(AnimProp.CropL));
        ek.Undo();
        Check("undo restores the clip with its composite settings", System.Text.Json.JsonSerializer.Serialize(ek.Clips) == kBefore);
        var rt = System.Text.Json.JsonSerializer.Deserialize<List<Clip>>(kBefore)!.First(c => c.Kind == ClipKind.Video);
        Check("project file round trip: blend mode, key, flips, crop keys", rt.Blend == BlendMode.Screen && rt.Key?.Color == "#00B140" && rt.FlipH && Crop.Animated(rt)
              && !Anim.PropsOf(new Clip { Kind = ClipKind.Video }).Contains(AnimProp.CropL));
        var (_, gk, gkTemps) = Exporter.Build(ek, "o.mp4");
        foreach (var f in gkTemps) try { File.Delete(f); } catch { }
        Check("export graph: key graph + matte multiply, lut2 blend, flip", gk.Contains("chromakey=color=0x00B140") && gk.Contains("lut2=") && gk.Contains("hflip") && gk.Contains("alphamerge"));
        Check("garbage matte is in the key graph (preview decoder and export share it)", Keyer.HasGarbage(kv.Key!) && gk.Contains("pad=") && gk.Contains("black@0"));

        // transitions (shape / move), audio effects, look effects, track matte, anchor point, corner pin: they ride with the clip and reach the export graph
        var en = new Editor(); en.Media.Add(sm);
        en.AddMedia(sm, 2, 0); en.AddMedia(sm, 2, 20);
        var tv5 = en.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).ToList();
        var ta5 = en.Clips.First(c => c.Kind == ClipKind.Audio);
        en.Edit(() =>
        {
            tv5[1].TransIn = new Transition { Kind = "Clock Wipe", Dur = 1 };
            tv5[0].Looks = new LookFx { Sharpen = 40 }; tv5[0].Shadow = new DropShadow(); tv5[0].AnchorX = -50;
            tv5[1].Pin = new CornerPin { TLX = 10 };
            ta5.AudioFx = AudioDsp.VoicePreset();
        });
        var np = Transitions.Compute(en);
        Check("shape transition resolved (mask merge) and Move transitions known", np.Wins.Any(w => w.D.Fx == Transitions.Fx.Mask) && Transitions.Get("Spin In").Fx == Transitions.Fx.Move && Transitions.VideoDefs.Length == 34);
        Check("Film Dissolve / Morph Cut / Cube Spin / Page Peel / Bible Page Turn",
              Transitions.Get("Film Dissolve").Fx == Transitions.Fx.Mask && Transitions.Get("Morph Cut").Fx == Transitions.Fx.Move
              && Transitions.Get("Cube Spin").Fx == Transitions.Fx.Move && Transitions.Get("Page Peel").Fx == Transitions.Fx.Mask
              && Transitions.Get("Bible Page Turn").Fx == Transitions.Fx.Mask && Transitions.GroupOf(Transitions.Get("Bible Page Turn")) == "3D"
              && TransPresets.BuiltIns.Length >= 7 && Transitions.FilmShader() != null);
        Check("VEdiT name and Gospel verses", AppInfo.Name == "VEdiT" && Gospel.Verses.Length >= 8 && Gospel.Of("John 3:16").Text.Contains("loved the world")
              && GraphicLibrary.BuiltIn.Count(t => t.Category == "Gospel") >= 4 && Shapes.Stickers.Any(s => s.name == "Cross")
              && Gospel.PageBitmap(64, 36, Gospel.Verses[0]).PixelWidth == 64);
        Check("Film Dissolve shader compiles" + (Transitions.FilmShaderError != null ? ": " + Transitions.FilmShaderError : ""), Transitions.FilmShader() != null);
        {
            var cube = Path.Combine(Path.GetTempPath(), "cutpro_grade.cube");
            Grade.WriteCube(Grade.For(new ColorGrade { Exposure = 0.5 })!, cube);
            var cubeFile = Grade.Cube(cube);
            Check("Lumetri Save .cube writes a 33³ LUT other editors can open", cubeFile != null && cubeFile.Size == Grade.N && File.ReadAllText(cube).Contains("LUT_3D_SIZE"));
            try { File.Delete(cube); } catch { }
        }
        var xdcam = ExportSettings.Presets.First(p => p.Name.Contains("XDCAM"));
        var imx = ExportSettings.Presets.First(p => p.Name.Contains("IMX"));
        var pod = ExportSettings.Presets.First(p => p.Name.Contains("Podcast"));
        Check("broadcast XDCAM / IMX MXF + podcast −16 LUFS presets",
              xdcam.VideoEncoder == "mpeg2video" && xdcam.Container == "mxf" && xdcam.OutH == 1080
              && imx.Video == "mpeg2_imx" && imx.OutW == 720 && imx.OutH == 576
              && pod.AudioOnly && pod.Normalize && Math.Abs(pod.Lufs + 16) < 0.01);
        {
            var liveM = new Loudness.Meter();
            int liveN = Mixer.Rate; var liveBuf = new float[liveN * 2];
            double liveAmp = Math.Pow(10, -23 / 20.0);
            for (int sec = 0; sec < 4; sec++)
            {
                for (int si = 0; si < liveN; si++)
                {
                    float samp = (float)(liveAmp * Math.Sin(2 * Math.PI * 1000 * (sec * liveN + si) / (double)liveN));
                    liveBuf[si * 2] = liveBuf[si * 2 + 1] = samp;
                }
                liveM.Add(liveBuf, liveN, 1);
            }
            var lv = liveM.Live();
            Check($"live LUFS M/S/I of −23 dBFS 1 kHz: M {lv.Momentary:0.00} S {lv.ShortTerm:0.00} I {lv.Integrated:0.00}",
                  Math.Abs(lv.Momentary + 23) <= 0.25 && Math.Abs(lv.ShortTerm + 23) <= 0.25 && Math.Abs(lv.Integrated + 23) <= 0.25);
        }
        {
            string? filmWhy = TransTest.FilmLookProbe();
            Check("Film Dissolve preview = WriteFilmMask merge" + (filmWhy == null ? "" : ": " + filmWhy), filmWhy == null);
        }
        var (_, ng, ngTemps) = Exporter.Build(en, "o.mp4");
        foreach (var f in ngTemps) try { File.Delete(f); } catch { }
        Check("export graph: shape transition (alphamerge through a mask video), look effects, drop shadow, corner pin, audio effects file",
              ng.Contains("concat=n=") && ng.Contains("unsharp=") && ng.Contains("lutrgb=r=0:g=0:b=0") && ng.Contains("perspective=") && ngTemps.Any(t => t.Contains("cutpro_tmask_")));
        Check("VHS / glitch look graphs", Looks.Graph(new LookFx { Vhs = 60 }, 720).Contains("geq=lum=") && Looks.Graph(new LookFx { Glitch = 50 }, 720).Contains("rgbashift="));
        var nm = new Clip { Kind = ClipKind.Video, Mask = MaskMode.Reveal };
        Check("a track matte takes no transitions and a pinned clip covers the frame layer",
              !Transitions.Compute(new[] { new Clip { Kind = ClipKind.Video, Start = 0, Dur = 2 }, new Clip { Kind = ClipKind.Video, Start = 2, Dur = 2, Mask = MaskMode.Reveal, TransIn = new Transition() } }).Wins.Any()
              && Editor.Placement(sm, tv5[1], tv5[1].Start) == (0, 0, Editor.W, Editor.H, 0));
        // keyframed corner pin: the export's per-frame expression = the values at every frame; razor cut keeps both halves' corners; a track
        // matte keeps the base corners; the property lanes appear with the pin
        {
            var pc = new Clip { Kind = ClipKind.Video, Start = 1, Dur = 3, In = 2, Pin = new CornerPin { TLX = 10, BRY = 90 } };
            Anim.SetKey(pc, AnimProp.PinTLX, 2, 0); var k1 = Anim.SetKey(pc, AnimProp.PinTLX, 3.5, 40); k1.In = KeyType.Bezier; Anim.SetKey(pc, AnimProp.PinTLX, 4.5, 40);
            Anim.SetKey(pc, AnimProp.PinBRY, 2.5, 100); Anim.SetKey(pc, AnimProp.PinBRY, 4, 70);
            int nf = (int)Math.Round(pc.Dur * TimeUtil.Fps) + 1; double worst = 0;
            foreach (var (p, w) in new[] { (AnimProp.PinTLX, 1920.0), (AnimProp.PinBRY, 1080.0) })
            {
                var vals = Enumerable.Range(0, nf).Select(k => Anim.Value(pc, p, pc.Start + k / TimeUtil.Fps) / 100 * w + 1).ToArray();
                var seg = Pin.Segments(vals);
                for (int k = 0; k < nf; k++) worst = Math.Max(worst, Math.Abs(Pin.SegmentValue(seg, k) - vals[k]));
            }
            var g = Pin.Warp(pc, 1920, 1080);
            var pmid = Pin.At(pc, 2.0);
            Check($"keyframed corner pin: per-frame expressions within {worst:0.0000} px of every frame, eval=frame, in-between corners",
                  worst < 0.002 && g.Contains(":eval=frame") && g.Contains("if(lt(in,") && pmid.TLX is > 0 and < 40 && Math.Abs(pmid.TRX - 100) < 1e-9 && Pin.Animated(pc));
            var ce = new Editor(); ce.Media.Add(sm); ce.AddMedia(sm, 1, 0);
            var cv = ce.Clips.First(c => c.Kind == ClipKind.Video);
            ce.Edit(() => { cv.Pin = new CornerPin(); Anim.SetKey(cv, AnimProp.PinTRY, cv.In, 0); Anim.SetKey(cv, AnimProp.PinTRY, cv.In + 2, 30); });
            double pbefore = Pin.At(cv, 1.5).TRY;
            ce.CutClips(new[] { cv }, 1);
            var pright = ce.Clips.Where(c => c.Kind == ClipKind.Video).OrderBy(c => c.Start).Last();
            var pm = new Clip { Kind = ClipKind.Video, Dur = 2, Pin = new CornerPin { TLX = 5 }, Mask = MaskMode.Reveal }; Anim.SetKey(pm, AnimProp.PinTLX, 0, 50);
            // monitor handles: a drag keys an animated coordinate at the playhead and sets the base of a still one
            var oe = new Editor(); oe.Media.Add(sm); oe.AddMedia(sm, 1, 0);
            var oc = oe.Clips.First(c => c.Kind == ClipKind.Video);
            oc.Pin = new CornerPin(); Anim.SetKey(oc, AnimProp.PinTLX, oc.In, 0); Anim.SetKey(oc, AnimProp.PinTLX, oc.In + 2, 20);
            oe.Select(oc); oe.Time = 1;
            var po = new PinOverlay { Ed = oe };
            bool seen = po.Target == oc;
            po.MoveCorner(oc, 0, 33, 7);
            var tk = Anim.KeyAt(oc, AnimProp.PinTLX, oc.In + 1);
            Check("pin handles: the selected pinned clip is the target; a drag keys the animated X at the playhead (3 keys) and moves the still Y's base",
                  seen && tk is { V: 33 } && Anim.Track(oc, AnimProp.PinTLX)!.Keys.Count == 3 && oc.Pin.TLY == 7 && !Anim.HasKeys(oc, AnimProp.PinTLY)
                  && Math.Abs(Pin.At(oc, 1).TLX - 33) < 1e-9);
            // track automation recording (touch): grab at 1 s over existing keys, hold −6 dB, move to −12 at 2 s, let go at 2.5 s; the key at 3 s stays
            {
                var keys = new List<Keyframe> { new() { T = 0, V = 0 }, new() { T = 1.5, V = 3 }, new() { T = 3, V = -3 } };
                var tm = new TrackMix { GainKeys = keys };
                var rec = AutoRecorder.Grab(keys, 1.0, tm.GainAt(1.0), 0.05);
                rec.Value = -6; for (double t = 1.0; t < 2.0; t += 1 / 60.0) rec.Tick(keys, t);
                rec.Value = -12; for (double t = 2.0; t < 2.5; t += 1 / 60.0) rec.Tick(keys, t);
                rec.Release(keys, 2.5);
                bool overwrote = !keys.Any(k => Math.Abs(k.T - 1.5) < 1e-9);
                double abefore = tm.GainAt(0.5), during = tm.GainAt(1.6), after = tm.GainAt(2.3), tail = tm.GainAt(3);
                Check($"automation recording: the curve before the grab stays ({abefore:0.0} dB at 0.5 s), what was under the playhead is replaced ({during:0.0} at 1.6 s, {after:0.0} at 2.3 s), the key after the release stays ({tail:0.0} at 3 s), {keys.Count} keys",
                      overwrote && Math.Abs(abefore - 1) < 0.01 && Math.Abs(during + 6) < 0.01 && Math.Abs(after + 12) < 0.01 && Math.Abs(tail + 3) < 1e-9 && keys.Count < 40);
                // export graph: an automated track's clips are summed, then each side gets its block gains; other tracks keep their per-clip pan
                var ae = new Editor(); ae.Media.Add(sm); ae.AddMedia(sm, 3, 0); ae.AddMedia(sm, 3, 3);
                ae.Mix.Channels[3].GainKeys = new() { new() { T = 0, V = -10 }, new() { T = 2, V = 0 } };
                var (_, agr, aTemps) = Exporter.Build(ae, "o.mp4");
                foreach (var f in aTemps) try { File.Delete(f); } catch { }
                Check("export graph: automated track = its clips summed, asetnsamples 240 + asendcmd volume per side, joined",
                      agr.Contains("asetnsamples=n=240:p=0,channelsplit") && agr.Contains("volume@tk3vl") && agr.Contains("join=inputs=2") && aTemps.Any(t => t.Contains("cutpro_auto_")));
                ae.Mix.MasterKeys = new() { new() { T = 0, V = -6 }, new() { T = 3, V = 0 } };
                var (_, mgr, mTemps) = Exporter.Build(ae, "o.mp4");
                foreach (var f in mTemps) try { File.Delete(f); } catch { }
                Check("export graph: master automation = block gains after the sum (asetnsamples 240 + asendcmd volume@mst)", mgr.Contains("volume@mst=volume=") && mgr.Contains("asendcmd=f=cutpro_auto_") && Mixer.MasterOf(ae).Table != null && Mixer.MasterOf(new Editor()).Table == null);
            }
            Check("corner pin keys ride a razor cut; a track matte uses its base corners; pin lanes only with a pin",
                  Math.Abs(Pin.At(pright, 1.5).TRY - pbefore) < 1e-6 && !Pin.Animated(pm) && Pin.At(pm, 0).TLX == 5
                  && Anim.PropsOf(cv).Contains(AnimProp.PinBRY) && !Anim.PropsOf(new Clip { Kind = ClipKind.Video }).Contains(AnimProp.PinTLX));
            {
                // lens distortion: lenscorrection on all four planes (gbrap — a grey plane's fill would be 16), sendcmd per frame when keyframed,
                // before the corner pin; lanes only with a lens
                var lc = new Clip { Kind = ClipKind.Video, Dur = 1, Lens = new LensDistort { Curvature = -30, CenterX = 10 } };
                string lg1 = Warp.Graph(lc, 640, 360, "t", null);
                Anim.SetKey(lc, AnimProp.LensCurve, 0, -30); Anim.SetKey(lc, AnimProp.LensCurve, 1, 30);
                var ltm = new List<string>();
                string lg2 = Warp.Graph(lc, 640, 360, "t", ltm), cmds = ltm.Count > 0 ? File.ReadAllText(ltm[0]) : "";
                foreach (var f in ltm) try { File.Delete(f); } catch { }
                lc.Pin = new CornerPin(); string lg3 = Warp.Graph(lc, 640, 360, "t", null);
                Check("lens distortion: gbrap lenscorrection (transparent fill), per-frame commands when keyframed, before the pin; lanes only with a lens",
                      lg1 == "scale=640:360,setsar=1,format=rgba,format=gbrap,lenscorrection@lct=cx=0.6:cy=0.5:k1=-0.3:k2=0:i=bilinear:fc=black@0,format=rgba"
                      && lg2.Contains(",sendcmd=f=cutpro_lens_") && cmds.Contains("lenscorrection@lct k1 ") && cmds.Split('\n').Length > 25
                      && lg3.IndexOf("lenscorrection", StringComparison.Ordinal) < lg3.IndexOf("perspective", StringComparison.Ordinal) && Math.Abs(Lens.At(lc, 0.5).k1) < 0.01
                      && Anim.PropsOf(lc).Contains(AnimProp.LensCurve) && !Anim.PropsOf(new Clip { Kind = ClipKind.Video }).Contains(AnimProp.LensCurve));
            }
            {
                var dc = new Clip { Kind = ClipKind.Video, Dur = 2, Distort = new Distort { Kind = DistortKind.Twirl, Amount = 0 } };
                Anim.SetKey(dc, AnimProp.DistAmount, 0, 0); Anim.SetKey(dc, AnimProp.DistAmount, 2, 180);
                Check("distort: live keys on amount (90° at 1 s), lanes only with a twirl",
                      Distorts.Animated(dc) && Math.Abs(Distorts.At(dc, 1).Amount - 90) < 1e-6
                      && Anim.PropsOf(dc).Contains(AnimProp.DistAmount) && !Anim.PropsOf(new Clip { Kind = ClipKind.Video }).Contains(AnimProp.DistAmount)
                      && Warp.Graph(dc, 640, 360, "t", null).Contains("geq="));
            }
            {
                // tracks: the row of each track in a 5 + 3 layout, a clip staying with its track when tracks are added above it, undo
                var te = new Editor();
                var tc = new Clip { Kind = ClipKind.Text, Track = Tracks.V(2), Start = 0, Dur = 1, Text = "x" }; te.Clips.Add(tc);
                te.AddTracks(2, 0, 1, Tracks.Count);
                var tmoved = te.Clips[0];
                Check("tracks: 5 + 3 rows are V5 … V1 A1 … A3 (V(n), A(n), Number, Label agree); a clip on V2 is still on V2 after two tracks went on top",
                      Tracks.Video == 5 && Tracks.Audio == 3 && Tracks.V1 == 4 && Tracks.A1 == 5 && Tracks.V(5) == 0 && Tracks.A(3) == 7 && Tracks.V(6) == -1 && Tracks.Number(7) == 3
                      && Tracks.Label(0) == "V5" && Tracks.IsVideo(4) && !Tracks.IsVideo(5) && Tracks.Label(tmoved.Track) == "V2" && te.TrackOff.Length == 8 && te.Mix.Channels.Length == 8);
                te.Undo();
                Check("tracks: one undo → 3 + 2, the clip back on row 1", Tracks.Video == 3 && Tracks.Audio == 2 && te.Clips[0].Track == 1);
            }
        }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(outFile, log);
        return fails == 0 ? 0 : 1;
    }
}
