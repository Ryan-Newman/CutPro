using System.Windows;
using System.Windows.Controls;

namespace CutPro;

/// <summary>
/// Track automation in the Audio Track Mixer (partial MainWindow). A track with fader / balance keys plays them (the strip follows the
/// playhead); moving its fader or knob while paused sets a key at the playhead (like any keyframed property). With ● Write on, moving a
/// fader or knob while playing records keys from the moment it is grabbed until it is let go (touch: what was there is overwritten, keys
/// after the release continue) — one undo step. The strip's ◆ menu adds / clears keys; the timeline shows them (track header ▸ Track volume
/// / Track pan) as rubber bands.
/// </summary>
public partial class MainWindow
{
    sealed class Touch { public int Track; public bool Gain; public AutoRecorder Rec = null!; }
    Touch? touch;

    List<Keyframe>? KeysOf(int track, bool gain) => track < 0 ? (gain ? ed.Mix.MasterKeys : null) : SubOf(track) is { } sm ? (gain ? sm.GainKeys : sm.PanKeys)
                                                  : gain ? ed.Mix.Channels[track].GainKeys : ed.Mix.Channels[track].PanKeys;   // (track −1 = the master, SubBase + k = a submix)
    List<Keyframe> EnsureKeys(int track, bool gain)
    {
        if (track < 0) return ed.Mix.MasterKeys ??= new();
        if (SubOf(track) is { } sm) return gain ? sm.GainKeys ??= new() : sm.PanKeys ??= new();
        var ch = ed.Mix.Channels[track];
        return gain ? ch.GainKeys ??= new() : ch.PanKeys ??= new();
    }
    double AutoValue(int track, bool gain, double t) => gain ? GainAtStrip(track, t) : PanAtStrip(track, t);

    /// <summary>Inserts a key at t (frame-snapped) or updates the one there.</summary>
    static Keyframe SetAutoKey(List<Keyframe> keys, double t, double v)
    {
        t = Math.Max(0, Math.Round(t * TimeUtil.Fps) / TimeUtil.Fps);
        var k = keys.FirstOrDefault(x => Math.Abs(x.T - t) < 0.5 / TimeUtil.Fps);
        if (k == null) { k = new Keyframe { T = t, V = v }; keys.Add(k); keys.Sort((a, b) => a.T.CompareTo(b.T)); }
        k.V = v;
        return k;
    }

    /// <summary>A fader / knob was grabbed: with Write on and playing, start recording that control.</summary>
    void AutoGrab(int track, bool gain)
    {
        if ((track < 0 && !gain) || AutoWrite.IsChecked != true || !playing) return;
        double t = ed.Time;
        touch = new Touch { Track = track, Gain = gain, Rec = AutoRecorder.Grab(EnsureKeys(track, gain), t, AutoValue(track, gain, t), gain ? 0.05 : 0.25) };
        ed.RaiseMixChanged();
    }

    /// <summary>A fader / knob moved. True when it was automation (recorded, or a key at the playhead on an automated control).</summary>
    bool AutoMove(int track, bool gain, double v)
    {
        if (track < 0 && !gain) return false;
        if (touch is { } tc && tc.Track == track && tc.Gain == gain) { tc.Rec.Value = v; return true; }    // (AutoTick records it)
        if (KeysOf(track, gain) is not { Count: > 0 } keys) return false;
        SetAutoKey(keys, ed.Time, v);
        return true;
    }

    /// <summary>Per frame while recording (render loop).</summary>
    void AutoTick()
    {
        if (touch is not { } tc) return;
        if (!playing) { AutoRelease(tc.Track, tc.Gain); return; }
        tc.Rec.Tick(EnsureKeys(tc.Track, tc.Gain), ed.Time);
        ed.RaiseMixChanged();
    }

    /// <summary>The control was let go.</summary>
    void AutoRelease(int track, bool gain)
    {
        if (touch is not { } tc || tc.Track != track || tc.Gain != gain) return;
        touch = null;
        tc.Rec.Release(EnsureKeys(track, gain), ed.Time);
        ed.RaiseMixChanged();
    }

    /// <summary>The strip's ◆ menu: keys at the playhead, clearing.</summary>
    ContextMenu AutoMenu(int track)
    {
        var cm = new ContextMenu();
        MenuItem Mi(string text, Action a, bool on = true) { var mi = new MenuItem { Header = text, IsEnabled = on }; mi.Click += (_, _) => a(); cm.Items.Add(mi); return mi; }
        void Change(Action a) { ed.MixBegin(); a(); ed.MixEnd(); ed.RaiseMixChanged(); }
        if (track < 0)
        {   // the master: volume only
            var mx = ed.Mix;
            Mi("Add a master volume key at the playhead", () => Change(() => SetAutoKey(EnsureKeys(-1, true), ed.Time, mx.MasterAt(ed.Time))));
            Mi($"Clear master automation ({mx.MasterKeys?.Count ?? 0} keys)", () => Change(() => { mx.Master = mx.MasterAt(ed.Time); mx.MasterKeys = null; }), mx.MasterKeys is { Count: > 0 });
            return cm;
        }
        if (SubOf(track) is { } subm)
        {   // a submix: volume / pan keys, clear (no timeline lane)
            Mi("Add a volume key at the playhead", () => Change(() => SetAutoKey(EnsureKeys(track, true), ed.Time, subm.AsTrack().GainAt(ed.Time))));
            Mi("Add a pan key at the playhead", () => Change(() => SetAutoKey(EnsureKeys(track, false), ed.Time, subm.AsTrack().PanAt(ed.Time))));
            cm.Items.Add(new Separator());
            Mi($"Clear volume automation ({subm.GainKeys?.Count ?? 0} keys)", () => Change(() => { subm.Gain = subm.AsTrack().GainAt(ed.Time); subm.GainKeys = null; }), subm.GainKeys is { Count: > 0 });
            Mi($"Clear pan automation ({subm.PanKeys?.Count ?? 0} keys)", () => Change(() => { subm.Pan = subm.AsTrack().PanAt(ed.Time); subm.PanKeys = null; }), subm.PanKeys is { Count: > 0 });
            return cm;
        }
        var ch = ed.Mix.Channels[track];
        Mi("Add a volume key at the playhead", () => Change(() => SetAutoKey(EnsureKeys(track, true), ed.Time, ch.GainAt(ed.Time))));
        Mi("Add a pan key at the playhead", () => Change(() => SetAutoKey(EnsureKeys(track, false), ed.Time, ch.PanAt(ed.Time))));
        cm.Items.Add(new Separator());
        Mi($"Clear volume automation ({ch.GainKeys?.Count ?? 0} keys)", () => Change(() => { ch.Gain = ch.GainAt(ed.Time); ch.GainKeys = null; }), ch.GainKeys is { Count: > 0 });
        Mi($"Clear pan automation ({ch.PanKeys?.Count ?? 0} keys)", () => Change(() => { ch.Pan = ch.PanAt(ed.Time); ch.PanKeys = null; }), ch.PanKeys is { Count: > 0 });
        cm.Items.Add(new Separator());
        Mi("Show on the timeline: volume", () => { Tl.ShowAuto(track, TimelineView.AutoShow.TrackVolume); });
        Mi("Show on the timeline: pan", () => { Tl.ShowAuto(track, TimelineView.AutoShow.TrackPan); });
        return cm;
    }

    /// <summary>Automated strips follow the playhead (render loop, while the mixer shows).</summary>
    void FollowAutomation()
    {
        foreach (var s in strips)
        {
            if (s.Track < 0)
            {   // the master strip follows its own automation
                if (ed.Mix.MasterKeys is { Count: > 0 } && !s.Fader.Dragging && !(touch is { Track: -1 }))
                {
                    double mdb = ed.Mix.MasterAt(ed.Time);
                    if (Math.Abs(s.Fader.Db - mdb) > 0.01) { s.Fader.Db = mdb; if (!s.Value.IsKeyboardFocusWithin) s.Value.Text = MixerElement.FormatDb(mdb); }
                }
                if (s.Auto != null) s.Auto.Content = ed.Mix.MasterKeys is { Count: > 0 } ? "◆" : "◇";
                continue;
            }
            var ch = SubOf(s.Track)?.AsTrack() ?? ed.Mix.Channels[s.Track];   // (a submix strip: its own automation)
            if (ch.GainKeys is { Count: > 0 } && !s.Fader.Dragging && !(touch is { Gain: true } tg && tg.Track == s.Track))
            {
                double db = ch.GainAt(ed.Time);
                if (Math.Abs(s.Fader.Db - db) > 0.01) { s.Fader.Db = db; if (!s.Value.IsKeyboardFocusWithin) s.Value.Text = MixerElement.FormatDb(db); }
            }
            if (s.Pan != null && ch.PanKeys is { Count: > 0 } && !s.Pan.Dragging && !(touch is { Gain: false } tp && tp.Track == s.Track))
            {
                double p = ch.PanAt(ed.Time);
                if (Math.Abs(s.Pan.Value - p) > 0.05) { s.Pan.Value = p; s.PanText!.Text = PanKnob.Label(p); }
            }
            if (s.Auto != null) s.Auto.Content = ch.Automated ? "◆" : "◇";
        }
    }
}

/// <summary>Recording one control's automation while playing (touch): from the grab, what was under the playhead is overwritten; a key when
/// the value changed (held until just before the change) or every half second; on release a last key, and keys after it continue.</summary>
public sealed class AutoRecorder
{
    public double LastT, LastV, Value;
    readonly double step;
    AutoRecorder(double t, double v, double step) { LastT = t; LastV = Value = v; this.step = step; }
    /// <summary>Starts at t with the value the curve has there (a key, so the curve before the grab stays). step: the smallest change recorded.</summary>
    public static AutoRecorder Grab(List<Keyframe> keys, double t, double v, double step)
    {
        keys.Add(new Keyframe { T = t, V = v }); Sort(keys);
        return new AutoRecorder(t, v, step);
    }
    static void Sort(List<Keyframe> keys) => keys.Sort((a, b) => a.T.CompareTo(b.T));
    public void Tick(List<Keyframe> keys, double t)
    {
        if (t < LastT - 1e-6) LastT = t;                                   // (jumped back: record on from here)
        keys.RemoveAll(k => k.T > LastT + 1e-6 && k.T <= t + 1e-6);
        bool changed = Math.Abs(Value - LastV) > step;
        if (changed && t - LastT > 0.06) keys.Add(new Keyframe { T = t - 0.03, V = LastV });
        if (changed || t - LastT >= 0.5)
        {
            keys.Add(new Keyframe { T = t, V = Value }); Sort(keys);
            LastT = t; LastV = Value;
        }
    }
    public void Release(List<Keyframe> keys, double t)
    {
        t = Math.Max(t, LastT);
        keys.RemoveAll(k => k.T > LastT + 1e-6 && k.T <= t + 1e-6);
        if (t > LastT + 1e-3) keys.Add(new Keyframe { T = t, V = Value });
        else if (keys.LastOrDefault(k => Math.Abs(k.T - LastT) < 1e-6) is { } last) last.V = Value;
        Sort(keys);
    }
}
