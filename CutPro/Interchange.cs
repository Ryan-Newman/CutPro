using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CutPro;

/// <summary>Sequences for other editors: CMX 3600 EDL (one picture track + up to two audio tracks, cuts, speed changes as M2, cross
/// dissolves as D events, source timecode from the media's tag) and OpenTimelineIO (.otio: every track, gaps, transitions, linear speed,
/// sequence and clip markers, the media's paths).</summary>
public static class Interchange
{
    static int Nom => TimeUtil.Nominal;
    static long Frames(double t) => (long)Math.Round(t * TimeUtil.Fps);
    /// <summary>Non-drop timecode of a frame count at the sequence's nominal rate.</summary>
    public static string Tc(long f)
    {
        f = Math.Max(0, f); int n = Nom;
        return $"{f / (n * 3600L) % 24:00}:{f / (n * 60L) % 60:00}:{f / n % 60:00}:{f % n:00}";
    }
    /// <summary>A media file's start timecode as a frame count (its tag; 0 without one).</summary>
    static long TagFrames(MediaItem? m)
    {
        if (m?.Timecode is not { } tc) return 0;
        var p = tc.Split(':', ';', '.');
        if (p.Length != 4 || !p.All(x => int.TryParse(x, out _))) return 0;
        return ((long.Parse(p[0]) * 60 + long.Parse(p[1])) * 60 + long.Parse(p[2])) * Nom + long.Parse(p[3]);
    }
    /// <summary>An EDL reel name: the media's reel tag, else its file name (8 characters, A–Z 0–9 _).</summary>
    public static string Reel(MediaItem? m)
    {
        if (m == null) return "BL";
        var src = m.Reel ?? Path.GetFileNameWithoutExtension(m.Path);
        var s = new string(src.ToUpperInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_').ToArray());
        return (s.Length == 0 ? "AX" : s.Length > 8 ? s[..8] : s);
    }

    /// <summary>CMX 3600 EDL of a sequence (the editor's active one): picture from the lowest video track that has clips (V1 first), sound from A1 / A2.
    /// Record timecode starts at 01:00:00:00 (start).</summary>
    public static string Edl(Editor ed, string title, long recStart = -1)
    {
        if (recStart < 0) recStart = 3600L * Nom;
        var sb = new StringBuilder();
        sb.Append("TITLE: ").Append(title).Append("\r\nFCM: NON-DROP FRAME\r\n\r\n");
        int ev = 1;
        var vrow = Tracks.VideoRows.FirstOrDefault(r => ed.Clips.Any(c => c.Track == r && c.Kind is ClipKind.Video or ClipKind.Image), Tracks.V1);
        var tracks = new List<(int row, string chan)> { (vrow, "V") };
        if (Tracks.Audio >= 1) tracks.Add((Tracks.A1, "A"));
        if (Tracks.Audio >= 2) tracks.Add((Tracks.A(2), "A2"));
        foreach (var (row, chan) in tracks)
        {
            Clip? prev = null;
            foreach (var c in ed.Clips.Where(c => c.Track == row && c.Kind is not ClipKind.Text and not ClipKind.Adjustment).OrderBy(c => c.Start))
            {
                var m = ed.MediaOf(c);
                long r0 = recStart + Frames(c.Start), r1 = recStart + Frames(c.End);
                double ratio = TimeMap.Ratio(c) * (c.Reverse ? -1 : 1);
                var (lo, hi) = c.Kind == ClipKind.Image ? (0.0, c.Dur) : TimeMap.Span(c);
                long s0 = TagFrames(m) + Frames(c.Reverse ? hi : lo), s1 = s0 + Math.Max(1, Frames(hi - lo));
                if (c.Hold) s1 = s0 + 1;
                string reel = Reel(m);
                bool dissolve = c.TransIn is { } ti && prev != null && Math.Abs(prev.End - c.Start) < 1e-6 && ti.Kind is "Cross Dissolve" or "Film Dissolve" or "Additive Dissolve";
                if (dissolve)
                {   // the outgoing clip's last frame as a zero-length cut, then D with the length in frames
                    var pm = ed.MediaOf(prev!);
                    long ps = TagFrames(pm) + Frames(TimeMap.Src(prev!, prev!.End - 1.0 / TimeUtil.Fps)) + 1;
                    Line(Reel(pm), chan, "C   ", ps, ps, r0, r0);
                    Line(reel, chan, $"D {Frames(c.TransIn!.Dur):000}", s0, s1, r0, r1, keepNumber: true);
                }
                else Line(reel, chan, "C   ", s0, s1, r0, r1);
                if (Math.Abs(ratio - 1) > 1e-6 || c.Hold)
                    sb.Append($"M2   {reel,-8} {(c.Hold ? 0 : ratio * TimeUtil.Fps).ToString("000.0", CultureInfo.InvariantCulture).PadLeft(6)}                {Tc(s0)}\r\n");
                sb.Append("* FROM CLIP NAME: ").Append(m?.Name ?? c.Kind.ToString()).Append("\r\n");
                if (m?.Seq == null && m != null) sb.Append("* SOURCE FILE: ").Append(m.Path).Append("\r\n");
                if (c.TransIn is { } t && !dissolve) sb.Append($"* TRANSITION: {t.Kind} {t.Dur.ToString("0.00", CultureInfo.InvariantCulture)} s (as a cut)\r\n");
                sb.Append("\r\n");
                ev++;
                prev = c;
            }
        }
        foreach (var mk in ed.Markers)
            sb.Append($"* MARKER: {Tc(recStart + Frames(mk.T))} {mk.Color} {mk.Name}\r\n");
        return sb.ToString();

        void Line(string reel, string chan, string kind, long s0, long s1, long r0, long r1, bool keepNumber = false)
        {
            int n = keepNumber ? ev : ev;
            sb.Append($"{n:000}  {reel,-8} {chan,-5} {kind,-8} {Tc(s0)} {Tc(s1)} {Tc(r0)} {Tc(r1)}\r\n");
        }
    }

    // ---------------- OpenTimelineIO ----------------
    static JsonObject Rt(double frames, double rate) => new() { ["OTIO_SCHEMA"] = "RationalTime.1", ["rate"] = rate, ["value"] = frames };
    static JsonObject Range(double start, double dur, double rate) => new() { ["OTIO_SCHEMA"] = "TimeRange.1", ["start_time"] = Rt(start, rate), ["duration"] = Rt(dur, rate) };
    static string Url(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
    static string OtioColor(MarkerColor c) => c switch
    {
        MarkerColor.Red => "RED", MarkerColor.Purple => "PURPLE", MarkerColor.Orange => "ORANGE", MarkerColor.Yellow => "YELLOW", MarkerColor.White => "WHITE",
        MarkerColor.Blue => "BLUE", MarkerColor.Cyan => "CYAN", _ => "GREEN",
    };
    static JsonObject OtioMarker(Marker mk, double t, double rate) => new()
    {
        ["OTIO_SCHEMA"] = "Marker.2", ["name"] = mk.Name, ["color"] = OtioColor(mk.Color), ["comment"] = mk.Note ?? "",
        ["marked_range"] = Range(Math.Round(t * rate), Math.Max(0, Math.Round(mk.Dur * rate)), rate),
        ["metadata"] = new JsonObject { ["cutpro"] = new JsonObject { ["chapter"] = mk.Chapter } },
    };

    /// <summary>The active sequence as an OpenTimelineIO timeline (JSON). Times are frames at the sequence rate; clips' source ranges at it too.</summary>
    public static string Otio(Editor ed, string name)
    {
        double rate = TimeUtil.Fps;
        var stack = new JsonArray();
        IEnumerable<int> order = Tracks.VideoRows.Concat(Tracks.AudioRows);                 // (OTIO stacks bottom first: V1, V2 …; then the audio)
        foreach (int row in order)
        {
            bool video = Tracks.IsVideo(row);
            var items = new JsonArray();
            double at = 0;
            foreach (var c in ed.Clips.Where(c => c.Track == row).OrderBy(c => c.Start))
            {
                double f0 = Math.Round(c.Start * rate), f1 = Math.Round(c.End * rate);
                if (f0 > at) items.Add(new JsonObject { ["OTIO_SCHEMA"] = "Gap.1", ["name"] = "", ["source_range"] = Range(0, f0 - at, rate) });
                if (c.TransIn is { } ti && f0 <= at + 0.5 && items.Count > 0)
                {
                    double tf = Math.Round(ti.Dur * rate);
                    double inOff = ti.Align switch { TransAlign.Start => 0, TransAlign.End => tf, _ => Math.Round(tf / 2) };
                    items.Add(new JsonObject
                    {
                        ["OTIO_SCHEMA"] = "Transition.1", ["name"] = ti.Kind, ["transition_type"] = ti.Kind.Contains("Dissolve") ? "SMPTE_Dissolve" : "Custom_Transition",
                        ["in_offset"] = Rt(inOff, rate), ["out_offset"] = Rt(tf - inOff, rate),
                    });
                }
                var m = ed.MediaOf(c);
                var (lo, hi) = c.Kind is ClipKind.Video or ClipKind.Audio ? TimeMap.Span(c) : (0.0, c.Dur);
                JsonNode reference = m != null && m.Seq == null
                    ? new JsonObject { ["OTIO_SCHEMA"] = "ExternalReference.1", ["target_url"] = Url(m.Path),
                                       ["available_range"] = Range(0, Math.Round((m.Type == ClipKind.Image ? c.Dur : m.Duration) * rate), rate) }
                    : new JsonObject { ["OTIO_SCHEMA"] = "MissingReference.1", ["name"] = m?.Name ?? c.Kind.ToString() };
                var effects = new JsonArray();
                double ratio = TimeMap.Ratio(c) * (c.Reverse ? -1 : 1);
                if (c.Hold) effects.Add(new JsonObject { ["OTIO_SCHEMA"] = "FreezeFrame.1", ["name"] = "Frame Hold", ["effect_name"] = "FreezeFrame", ["time_scalar"] = 0.0 });
                else if (Math.Abs(ratio - 1) > 1e-9) effects.Add(new JsonObject { ["OTIO_SCHEMA"] = "LinearTimeWarp.1", ["name"] = "Speed", ["effect_name"] = "LinearTimeWarp", ["time_scalar"] = ratio });
                var markers = new JsonArray();
                foreach (var mk in c.Markers ?? new()) markers.Add(OtioMarker(mk, mk.T, rate));
                var clip = new JsonObject
                {
                    ["OTIO_SCHEMA"] = "Clip.2", ["name"] = c.Kind == ClipKind.Text ? c.Text.Split('\n')[0] : m?.Name ?? c.Kind.ToString(),
                    ["source_range"] = Range(Math.Round((c.Reverse ? hi : lo) * rate), f1 - f0, rate),
                    ["media_references"] = new JsonObject { ["DEFAULT_MEDIA"] = reference }, ["active_media_reference_key"] = "DEFAULT_MEDIA",
                    ["effects"] = effects, ["markers"] = markers,
                    ["metadata"] = new JsonObject { ["cutpro"] = new JsonObject { ["kind"] = c.Kind.ToString(), ["volume"] = c.Volume, ["opacity"] = c.Opacity } },
                };
                items.Add(clip);
                at = f1;
            }
            stack.Add(new JsonObject { ["OTIO_SCHEMA"] = "Track.1", ["name"] = Tracks.Name(row), ["kind"] = video ? "Video" : "Audio", ["children"] = items,
                                       ["source_range"] = null, ["effects"] = new JsonArray(), ["markers"] = new JsonArray() });
        }
        var smarkers = new JsonArray();
        foreach (var mk in ed.Markers) smarkers.Add(OtioMarker(mk, mk.T, rate));
        var tl = new JsonObject
        {
            ["OTIO_SCHEMA"] = "Timeline.1", ["name"] = name, ["global_start_time"] = Rt(3600 * Nom, rate),
            ["metadata"] = new JsonObject { ["cutpro"] = new JsonObject { ["width"] = Editor.OutW, ["height"] = Editor.OutH, ["fps"] = rate } },
            ["tracks"] = new JsonObject { ["OTIO_SCHEMA"] = "Stack.1", ["name"] = "tracks", ["children"] = stack, ["markers"] = smarkers, ["effects"] = new JsonArray() },
        };
        return tl.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
