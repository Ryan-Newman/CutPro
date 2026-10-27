using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CutPro;

/// <summary>
/// Volume rubber bands on the timeline (partial TimelineView). Each audio track shows one band, chosen in its header: the clip volume of its
/// clips (keys by source time, like every clip key), or the track's own automation — fader (dB) or balance — across the whole track (keys by
/// timeline time, <see cref="TrackMix"/>). Drag the line to move it (between two keys: both), drag a key (time and value), Ctrl+click the
/// line to add a key, Alt+click a key to remove it. One undo step per gesture (clip: Clips history; track: mixer history).
/// </summary>
public partial class TimelineView
{
    public enum AutoShow { ClipVolume, TrackVolume, TrackPan }
    AutoShow[] autoShow = new AutoShow[Tracks.Max];
    Brush bandBrush = null!;
    public AutoShow BandOf(int track) => autoShow[track];
    public void ShowAuto(int track, AutoShow s) { autoShow[track] = s; InvalidateVisual(); }
    /// <summary>Tracks were added / removed: each row's band mode goes with its track (old row → new row, −1 removed).</summary>
    public void MoveRows(int[] map)
    {
        var moved = new AutoShow[Tracks.Max];
        for (int r = 0; r < map.Length && r < autoShow.Length; r++) if (map[r] >= 0 && map[r] < moved.Length) moved[map[r]] = autoShow[r];
        autoShow = moved; InvalidateVisual();
    }

    const double ClipHiDb = 12, TrackHiDb = Mixer.MaxDb, LoDb = -48;

    Rect BandModeRect(int r) => new(6, RowY(r) + RowHt(r) - 17, 38, 14);
    void CycleBand(int r) { autoShow[r] = (AutoShow)(((int)autoShow[r] + 1) % 3); InvalidateVisual(); }
    void DrawBandMode(DrawingContext dc, int r)
    {
        var rr = BandModeRect(r);
        string s = autoShow[r] switch { AutoShow.TrackVolume => "Trk vol", AutoShow.TrackPan => "Trk pan", _ => "Clip vol" };
        bool auto = autoShow[r] != AutoShow.ClipVolume;
        dc.DrawRoundedRectangle(auto ? bandBrush : btnBrush, null, rr, 3, 3);
        var ft = Txt(s, 9, auto ? Brushes.Black : btnText);
        dc.DrawText(ft, new Point(rr.X + (rr.Width - ft.Width) / 2, rr.Y + (rr.Height - ft.Height) / 2));
    }

    /// <summary>One band: what it edits and how it maps.</summary>
    sealed class Band
    {
        public int Track; public Clip? C;                                   // C: clip volume; null: track automation
        public bool Pan;                                                    // (track balance)
        public Rect R;                                                      // the band area (values top → bottom)
        public double X0, X1;                                               // visible span
    }

    IEnumerable<Band> Bands()
    {
        double W = ActualWidth;
        for (int r = 0; r < Tracks.Count; r++)
        {
            if (Tracks.IsVideo(r)) continue;
            double y = RowY(r), rh = RowHt(r);
            if (y > ActualHeight || y + rh < RulerH) continue;                 // (scrolled out of view)
            if (autoShow[r] == AutoShow.ClipVolume)
            {
                foreach (var c in Ed.Clips.Where(c => c.Track == r && c.Kind == ClipKind.Audio))
                {
                    double x1 = T2X(c.Start), x2 = T2X(c.End);
                    if (x2 < HeaderW || x1 > W || x2 - x1 < 6) continue;
                    yield return new Band { Track = r, C = c, R = new Rect(x1, y + 3 + 15, x2 - x1, Math.Max(4, rh - 6 - 18)), X0 = Math.Max(x1, HeaderW), X1 = Math.Min(x2, W) };
                }
            }
            else yield return new Band { Track = r, Pan = autoShow[r] == AutoShow.TrackPan, R = new Rect(HeaderW, y + 6, Math.Max(0, W - HeaderW), Math.Max(4, rh - 12)), X0 = HeaderW, X1 = W };
        }
    }

    // values: clip volume in % (shown in dB), track fader in dB, track balance −100 … 100
    double ValueAt(Band b, double t) => b.C != null ? Anim.Value(b.C, AnimProp.Volume, t)
                                       : b.Pan ? Ed.Mix.Channels[b.Track].PanAt(t) : Ed.Mix.Channels[b.Track].GainAt(t);
    double Y(Band b, double v)
    {
        double u;
        if (b.Pan) u = (v + 100) / 200;
        else
        {
            double db = b.C != null ? (v > 0 ? 20 * Math.Log10(v / 100) : double.NegativeInfinity) : v <= Mixer.MinDb ? double.NegativeInfinity : v;
            double hi = b.C != null ? ClipHiDb : TrackHiDb;
            u = double.IsNegativeInfinity(db) ? 1 : Math.Clamp((hi - db) / (hi - LoDb), 0, 1);
        }
        return b.R.Top + u * b.R.Height;
    }
    double ValueOf(Band b, double y)
    {
        double u = Math.Clamp((y - b.R.Top) / Math.Max(1, b.R.Height), 0, 1);
        if (b.Pan) return Math.Round(u * 200 - 100);
        double hi = b.C != null ? ClipHiDb : TrackHiDb, db = hi - u * (hi - LoDb);
        if (u >= 0.999) return b.C != null ? 0 : Mixer.MinDb;               // (the bottom: silent)
        db = Math.Round(db * 10) / 10;
        return b.C != null ? Math.Clamp(100 * Math.Pow(10, db / 20), 0, 400) : Math.Clamp(db, Mixer.MinDb, Mixer.MaxDb);
    }
    string Label(Band b, double v) => b.Pan ? PanKnob.Label(v)
                                    : b.C != null ? (v <= 0 ? "−∞ dB" : $"{20 * Math.Log10(v / 100):+0.0;−0.0;0.0} dB")
                                    : MixerElement.FormatDb(v) + " dB";

    List<Keyframe>? KeysOf(Band b) => b.C != null ? Anim.Track(b.C, AnimProp.Volume)?.Keys
                                    : b.Pan ? Ed.Mix.Channels[b.Track].PanKeys : Ed.Mix.Channels[b.Track].GainKeys;
    double KeyTime(Band b, Keyframe k) => b.C != null ? Anim.TimelineTime(b.C, k.T) : k.T;

    void DrawBands(DrawingContext dc)
    {
        var pen = new Pen(bandBrush, 1.5); pen.Freeze();
        var shadow = new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 3); shadow.Freeze();
        foreach (var b in Bands())
        {
            dc.PushClip(new RectangleGeometry(new Rect(b.X0, b.R.Top - 6, Math.Max(0, b.X1 - b.X0), b.R.Height + 12)));
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                bool first = true;
                for (double x = b.X0; ; x += 3)
                {
                    double xx = Math.Min(x, b.X1), v = ValueAt(b, X2T(xx));
                    var pt = new Point(xx, Y(b, v));
                    if (first) { ctx.BeginFigure(pt, false, false); first = false; } else ctx.LineTo(pt, true, false);
                    if (xx >= b.X1) break;
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, shadow, g); dc.DrawGeometry(null, pen, g);
            if (KeysOf(b) is { Count: > 0 } keys)
                foreach (var k in keys)
                {
                    double x = T2X(KeyTime(b, k));
                    if (x < b.X0 - 4 || x > b.X1 + 4) continue;
                    bool hot = band?.Key == k;
                    dc.DrawRectangle(hot ? white : bandBrush, new Pen(Brushes.Black, 1), new Rect(x - 3, Y(b, k.V) - 3, 6, 6));
                }
            dc.Pop();
        }
        if (band is { Moved: true } bd)
        {
            var b = bd.B; double v = bd.Key != null ? bd.Key.V : ValueAt(b, X2T(lastMouse.X));
            var ft = Txt(Label(b, v), 11, Brushes.White, true);
            double x = Math.Min(lastMouse.X + 12, ActualWidth - ft.Width - 8), y = Math.Max(RulerH, lastMouse.Y - 24);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)), null, new Rect(x - 4, y - 1, ft.Width + 8, ft.Height + 2), 3, 3);
            dc.DrawText(ft, new Point(x, y));
        }
    }

    // ---------- editing ----------
    sealed class BandDrag
    {
        public Band B = null!; public Keyframe? Key; public List<(Keyframe k, double v)>? Seg; public double Base0; public Point Start; public bool Moved;
        public double Lo, Hi;                                               // key time limits (timeline)
    }
    BandDrag? band;

    (Band b, Keyframe? key)? BandHit(Point p)
    {
        foreach (var b in Bands())
        {
            if (p.X < b.X0 || p.X > b.X1 || p.Y < b.R.Top - 6 || p.Y > b.R.Bottom + 6 || Ed.Locked(b.Track)) continue;   // (a locked track's bands are shown, not edited)
            if (KeysOf(b) is { Count: > 0 } keys)
                foreach (var k in keys)
                {
                    double x = T2X(KeyTime(b, k));
                    if (Math.Abs(x - p.X) <= 5 && Math.Abs(Y(b, k.V) - p.Y) <= 5) return (b, k);
                }
            if (Math.Abs(Y(b, ValueAt(b, X2T(p.X))) - p.Y) <= 4) return (b, null);
        }
        return null;
    }
    Cursor? BandCursor(Point p) => BandHit(p) is { } h ? h.key != null ? Cursors.Hand : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? Cursors.Cross : Cursors.SizeNS : null;

    void BandBegin(Band b) { if (b.C != null) Ed.Checkpoint(); else Ed.MixBegin(); }
    void BandChanged(Band b) { if (b.C != null) Ed.RaiseProps(); else Ed.RaiseMixChanged(); InvalidateVisual(); }

    bool BeginBand(Point p, bool ctrl, bool alt)
    {
        if (BandHit(p) is not var (b, key)) return false;
        if (b.C != null && !Ed.IsSel(b.C)) Ed.Select(b.C);
        BandBegin(b);
        if (key != null && alt)
        {   // Alt+click a key: remove it
            KeysOf(b)!.Remove(key);
            if (b.C != null) Anim.Prune(b.C);
            else if (KeysOf(b) is { Count: 0 }) { if (b.Pan) Ed.Mix.Channels[b.Track].PanKeys = null; else Ed.Mix.Channels[b.Track].GainKeys = null; }
            EndBandCommit(b); BandChanged(b); return true;
        }
        double t = X2T(p.X);
        if (key == null && ctrl)
        {   // Ctrl+click the line: a key there with the value the line has, then drag it
            double v = ValueAt(b, t);
            key = b.C != null ? Anim.SetKey(b.C, AnimProp.Volume, Anim.SrcTime(b.C, Math.Clamp(t, b.C.Start, b.C.End)), v)
                              : AddTrackKey(b, Math.Max(0, Math.Round(t * TimeUtil.Fps) / TimeUtil.Fps), v);
        }
        band = new BandDrag { B = b, Key = key, Start = p };
        if (key != null)
        {
            var keys = KeysOf(b)!; int i = keys.IndexOf(key);
            double f = 1 / TimeUtil.Fps;
            band.Lo = i > 0 ? KeyTime(b, keys[i - 1]) + f : b.C?.Start ?? 0;
            band.Hi = i < keys.Count - 1 ? KeyTime(b, keys[i + 1]) - f : b.C?.End ?? double.MaxValue;
            if (b.C != null && b.C.Reverse) (band.Lo, band.Hi) = (b.C.Start, b.C.End);   // (reversed: source order runs backwards — keep inside the clip)
        }
        else if (KeysOf(b) is { Count: > 0 } keys)
        {   // the line between two keys: both move (before the first / after the last: that one)
            int i = keys.FindLastIndex(k => KeyTime(b, k) <= t);
            var seg = new List<Keyframe>();
            if (i >= 0) seg.Add(keys[i]);
            if (i + 1 < keys.Count) seg.Add(keys[i + 1]);
            band.Seg = seg.Select(k => (k, k.V)).ToList();
        }
        else band.Base0 = ValueAt(b, t);
        mode = Mode.Band; CaptureMouse(); InvalidateVisual();
        return true;
    }

    Keyframe AddTrackKey(Band b, double t, double v)
    {
        var ch = Ed.Mix.Channels[b.Track];
        var keys = b.Pan ? ch.PanKeys ??= new() : ch.GainKeys ??= new();
        var k = keys.FirstOrDefault(x => Math.Abs(x.T - t) < 0.5 / TimeUtil.Fps);
        if (k == null) { k = new Keyframe { T = t, V = v }; keys.Add(k); keys.Sort((a, c) => a.T.CompareTo(c.T)); }
        k.V = v;
        return k;
    }

    void BandDragTo(Point p)
    {
        if (band is not { } d) return;
        var b = d.B;
        if (!d.Moved && (p - d.Start).Length < 2) return;
        d.Moved = true;
        if (d.Key is { } k)
        {
            double t = Math.Clamp(Math.Round(X2T(p.X) * TimeUtil.Fps) / TimeUtil.Fps, d.Lo, Math.Max(d.Lo, d.Hi));
            k.T = b.C != null ? Math.Round(Anim.SrcTime(b.C, t) * TimeUtil.Fps) / TimeUtil.Fps : t;
            k.V = ValueOf(b, p.Y);
        }
        else if (d.Seg != null)
        {
            // move the segment by the value change under the mouse (in the band's own scale: dB for volume)
            double v0 = ValueOf(b, d.Start.Y), v1 = ValueOf(b, p.Y);
            foreach (var (key, v) in d.Seg) key.V = Shift(b, v, v0, v1);
        }
        else
        {
            double v = ValueOf(b, p.Y);
            if (b.C != null) b.C.Volume = v;
            else if (b.Pan) Ed.Mix.Channels[b.Track].Pan = v;
            else Ed.Mix.Channels[b.Track].Gain = v;
        }
        BandChanged(b);
    }
    /// <summary>v moved like v0 → v1: in dB for volume (so a quiet key moves as much as a loud one), plain for balance.</summary>
    static double Shift(Band b, double v, double v0, double v1)
    {
        if (b.Pan) return Math.Clamp(v + v1 - v0, -100, 100);
        if (b.C == null) return Math.Clamp(v + v1 - v0, Mixer.MinDb, Mixer.MaxDb);
        if (v0 <= 0 || v1 <= 0 || v <= 0) return v1 <= 0 ? 0 : v;
        return Math.Clamp(v * v1 / v0, 0, 400);
    }

    void EndBand()
    {
        if (band is not { } d) return;
        band = null;
        if (d.Key != null) KeysOf(d.B)?.Sort((a, c) => a.T.CompareTo(c.T));
        EndBandCommit(d.B);
        BandChanged(d.B);
    }
    void EndBandCommit(Band b) { if (b.C != null) Ed.Commit(); else Ed.MixEnd(); }
}
