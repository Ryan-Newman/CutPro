using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CutPro;

/// <summary>A timeline read from another editor's file, before it becomes a sequence: clips on numbered tracks (0 = V1 / A1, upwards),
/// times in seconds, the media by path.</summary>
public sealed class ImpTimeline
{
    public string Name = "Imported";
    public double Fps = 30;
    public int W, H;                                                           // (0 = the current sequence's)
    public List<ImpClip> Clips = new();
    public List<Marker> Markers = new();
    /// <summary>What could not be brought over (shown to the user).</summary>
    public List<string> Notes = new();
}
public sealed class ImpClip
{
    public bool Video;
    public int Track;
    public string? Path;
    public string Name = "";
    public double Start, In, Dur;
    /// <summary>Source timecode frames instead of seconds (EDL): <see cref="In"/> becomes seconds once the media's own start timecode is known.</summary>
    public long? SrcTc;
    public double Speed = 1;                                                   // (negative = reversed)
    public bool Hold;
    public Transition? TransIn;
    public List<Marker>? Markers;
    public ClipKind? Kind;                                                     // (titles and pictures by their metadata / extension)
    public string? Link;                                                       // (clips with the same link: picture and sound of one shot)
    public double Volume = 100, Opacity = 100;
}

/// <summary>
/// Reads timelines made elsewhere: OpenTimelineIO (.otio, also CutPro's own export), Final Cut Pro 7 XML (.xml — Premiere Pro's and Resolve's
/// "XML" export) and CMX 3600 EDL (.edl). Cuts, gaps, tracks, linear speed / reverse / freeze, cross dissolves and dips, sequence and clip
/// markers and the media's paths come over; effects, titles' looks and keyframes don't (they are listed in <see cref="ImpTimeline.Notes"/>).
/// AAF (a binary container) isn't read — export XML or OTIO from the other editor instead.
/// </summary>
public static class InterchangeImport
{
    static readonly CultureInfo inv = CultureInfo.InvariantCulture;
    public const string DialogFilter = "Timelines (*.otio;*.xml;*.edl)|*.otio;*.xml;*.edl|OpenTimelineIO (*.otio)|*.otio|Final Cut Pro 7 / Premiere XML (*.xml)|*.xml|CMX 3600 EDL (*.edl)|*.edl";

    public static ImpTimeline Read(string file, double fps)
    {
        var text = File.ReadAllText(file);
        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        var t = ext switch
        {
            ".otio" => Otio(text),
            ".xml" or ".fcpxml" => Fcp7(text),
            ".edl" => Edl(text, fps),
            ".aaf" => throw new NotSupportedException("AAF files aren't supported — export the timeline as XML (Final Cut Pro 7) or OpenTimelineIO from the other editor."),
            _ => text.TrimStart().StartsWith("{") ? Otio(text) : text.TrimStart().StartsWith("<") ? Fcp7(text) : Edl(text, fps),
        };
        if (t.Name is "" or "Imported") t.Name = System.IO.Path.GetFileNameWithoutExtension(file);
        return t;
    }

    static readonly string[] stills = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic" };
    public static bool IsStill(string? path) => path != null && stills.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    /// <summary>A file URL / path from another program as a local path (file://localhost/C%3a/… too).</summary>
    public static string? LocalPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (!url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return Uri.UnescapeDataString(url);
        var rest = url[5..];
        if (rest.StartsWith("//localhost/", StringComparison.OrdinalIgnoreCase)) rest = rest["//localhost".Length..];   // → "/C%3a/…"
        else if (rest.StartsWith("///")) rest = rest[2..];
        else if (rest.StartsWith("//")) return @"\\" + Uri.UnescapeDataString(rest[2..]).Replace('/', '\\');           // (a network share)
        rest = Uri.UnescapeDataString(rest);
        if (rest.Length >= 3 && rest[0] == '/' && rest[2] == ':') rest = rest[1..];
        return rest.Replace('/', '\\');
    }

    // ---------------- OpenTimelineIO ----------------
    static double Sec(JsonNode? rt) => rt == null ? 0 : (double)(rt["value"] ?? 0) / Math.Max(1e-9, (double)(rt["rate"] ?? 1));
    static double Rate(JsonNode? rt) => rt?["rate"] is { } r ? (double)r : 0;
    static MarkerColor OtioColor(string? c) => c?.ToUpperInvariant() switch
    {
        "RED" => MarkerColor.Red, "PURPLE" or "MAGENTA" or "PINK" => MarkerColor.Purple, "ORANGE" => MarkerColor.Orange, "YELLOW" => MarkerColor.Yellow,
        "WHITE" or "BLACK" => MarkerColor.White, "BLUE" => MarkerColor.Blue, "CYAN" => MarkerColor.Cyan, _ => MarkerColor.Green,
    };
    static Marker OtioMarker(JsonNode m) => new()
    {
        Name = (string?)m["name"] ?? "", Note = (string?)m["comment"] ?? (string?)m["metadata"]?["comment"] ?? "", Color = OtioColor((string?)m["color"]),
        T = Sec(m["marked_range"]?["start_time"]), Dur = Sec(m["marked_range"]?["duration"]), Chapter = (bool?)m["metadata"]?["cutpro"]?["chapter"] ?? false,
    };

    public static ImpTimeline Otio(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new FormatException("Not an OpenTimelineIO file.");
        var tl = new ImpTimeline { Name = (string?)root["name"] ?? "Imported" };
        var stack = root["tracks"] ?? (((string?)root["OTIO_SCHEMA"])?.StartsWith("Stack") == true ? root : null) ?? throw new FormatException("The file has no tracks.");
        if (root["metadata"]?["cutpro"] is { } cp)
        {
            tl.Fps = (double?)cp["fps"] ?? tl.Fps; tl.W = (int?)cp["width"] ?? 0; tl.H = (int?)cp["height"] ?? 0;
        }
        else if (FirstRate(stack) is double r && r > 0) tl.Fps = r;
        foreach (var m in stack["markers"]?.AsArray() ?? new JsonArray()) if (m != null) tl.Markers.Add(OtioMarker(m));
        int vi = 0, ai = 0;
        foreach (var track in stack["children"]?.AsArray() ?? new JsonArray())
        {
            if (track == null || !((string?)track["OTIO_SCHEMA"] ?? "").StartsWith("Track")) continue;
            bool video = !string.Equals((string?)track["kind"], "Audio", StringComparison.OrdinalIgnoreCase);
            int index = video ? vi++ : ai++;
            double at = 0; Transition? pending = null;
            foreach (var it in track["children"]?.AsArray() ?? new JsonArray())
            {
                if (it == null) continue;
                string schema = (string?)it["OTIO_SCHEMA"] ?? "";
                double dur = Sec(it["source_range"]?["duration"]);
                if (schema.StartsWith("Transition"))
                {
                    double a = Sec(it["in_offset"]), b = Sec(it["out_offset"]);
                    string name = (string?)it["name"] ?? "", type = (string?)it["transition_type"] ?? "";
                    pending = new Transition
                    {
                        Kind = Transitions.VideoDefs.Any(d => d.Name == name) ? name : type == "SMPTE_Dissolve" || name.Contains("issolve", StringComparison.OrdinalIgnoreCase) ? "Cross Dissolve" : "Cross Dissolve",
                        Dur = a + b, Align = a < 1e-9 ? TransAlign.Start : b < 1e-9 ? TransAlign.End : TransAlign.Center,
                    };
                    if (!video) pending.Kind = "Constant Power";
                    continue;
                }
                if (schema.StartsWith("Gap")) { at += dur; pending = null; continue; }
                if (schema.StartsWith("Stack")) { tl.Notes.Add($"A nested stack (compound clip) “{(string?)it["name"]}” became a gap."); at += dur; pending = null; continue; }
                if (!schema.StartsWith("Clip")) { at += dur; continue; }
                var refNode = it["media_references"] is JsonObject refs ? refs[(string?)it["active_media_reference_key"] ?? "DEFAULT_MEDIA"] ?? refs.FirstOrDefault().Value : it["media_reference"];
                string? path = LocalPath((string?)refNode?["target_url"]);
                var c = new ImpClip { Video = video, Track = index, Path = path, Name = (string?)it["name"] ?? "", Start = at, Dur = dur, In = Sec(it["source_range"]?["start_time"]), TransIn = pending };
                pending = null;
                foreach (var e in it["effects"]?.AsArray() ?? new JsonArray())
                {
                    string es = (string?)e?["OTIO_SCHEMA"] ?? "";
                    if (es.StartsWith("FreezeFrame")) c.Hold = true;
                    else if (es.StartsWith("LinearTimeWarp")) c.Speed = (double?)e?["time_scalar"] ?? 1;
                    else if (e != null) tl.Notes.Add($"Effect “{(string?)e["effect_name"] ?? (string?)e["name"]}” on “{c.Name}” isn't supported.");
                }
                if (Math.Abs(c.Speed) < 1e-9) { c.Hold = true; c.Speed = 1; }
                if (c.Speed < 0) c.In -= Math.Abs(c.Speed) / tl.Fps;                   // (reversed: In = the frame shown first, the source range's last)
                if (it["markers"] is JsonArray ms && ms.Count > 0) c.Markers = ms.Where(x => x != null).Select(x => OtioMarker(x!)).ToList();
                if (it["metadata"]?["cutpro"] is { } meta)
                {
                    if (Enum.TryParse<ClipKind>((string?)meta["kind"], out var k)) c.Kind = k;
                    c.Volume = (double?)meta["volume"] ?? 100; c.Opacity = (double?)meta["opacity"] ?? 100;
                }
                if (c.Kind == null && path == null && video) c.Kind = ClipKind.Text;
                tl.Clips.Add(c);
                at += dur;
            }
        }
        LinkPairs(tl);
        return tl;
    }
    static double? FirstRate(JsonNode stack)
    {
        foreach (var track in stack["children"]?.AsArray() ?? new JsonArray())
            foreach (var it in track?["children"]?.AsArray() ?? new JsonArray())
                if (Rate(it?["source_range"]?["duration"]) is double r && r > 0) return r;
        return null;
    }

    /// <summary>Picture and sound of one shot (same file, same place, same source range) are linked, like clips dropped from the Project panel.</summary>
    static void LinkPairs(ImpTimeline tl)
    {
        foreach (var v in tl.Clips.Where(c => c.Video && c.Path != null && c.Link == null))
        {
            var a = tl.Clips.FirstOrDefault(x => !x.Video && x.Link == null && string.Equals(x.Path, v.Path, StringComparison.OrdinalIgnoreCase)
                                                 && Math.Abs(x.Start - v.Start) < 1e-4 && Math.Abs(x.Dur - v.Dur) < 1e-4 && Math.Abs(x.In - v.In) < 1e-3);
            if (a != null) v.Link = a.Link = Guid.NewGuid().ToString("N");
        }
    }

    // ---------------- Final Cut Pro 7 XML (xmeml) ----------------
    static XElement? E(XElement? x, string name) => x?.Element(name);
    static string? S(XElement? x, string name) => x?.Element(name)?.Value;
    static double D(XElement? x, string name, double def = 0) => double.TryParse(S(x, name), NumberStyles.Float, inv, out var v) ? v : def;
    static double XRate(XElement? rate, double def)
    {
        if (rate == null) return def;
        double tb = D(rate, "timebase", def);
        bool ntsc = string.Equals(S(rate, "ntsc"), "TRUE", StringComparison.OrdinalIgnoreCase);
        return ntsc ? tb * 1000 / 1001 : tb;
    }

    public static ImpTimeline Fcp7(string xml)
    {
        var doc = XDocument.Parse(xml);
        var seq = doc.Descendants("sequence").FirstOrDefault(s => s.Element("media") != null) ?? throw new FormatException("No sequence in this XML (Final Cut Pro 7 / Premiere XML expected).");
        var tl = new ImpTimeline { Name = S(seq, "name") ?? "Imported", Fps = XRate(E(seq, "rate"), 30) };
        var media = E(seq, "media")!;
        var sc = E(E(E(E(media, "video"), "format"), "samplecharacteristics"), "width")?.Parent;
        tl.W = (int)D(sc, "width"); tl.H = (int)D(sc, "height");
        double fps = tl.Fps;
        var files = new Dictionary<string, XElement>();
        foreach (var f in doc.Descendants("file")) if (f.Attribute("id")?.Value is { } id && f.Elements().Any()) files.TryAdd(id, f);
        // links: every clipitem id that lists another as its link
        var linkOf = new Dictionary<string, string>();
        foreach (var ci in seq.Descendants("clipitem"))
        {
            var id = ci.Attribute("id")?.Value; if (id == null) continue;
            var refs = ci.Elements("link").Select(l => S(l, "linkclipref")).Where(r => r != null).Cast<string>().Append(id).ToList();
            string group = refs.Select(r => linkOf.GetValueOrDefault(r)).FirstOrDefault(g => g != null) ?? Guid.NewGuid().ToString("N");
            foreach (var r in refs) linkOf[r] = group;
        }
        foreach (var m in seq.Elements("marker")) tl.Markers.Add(XMarker(m, fps));
        void Tracks(XElement? kind, bool video)
        {
            int index = 0;
            foreach (var track in kind?.Elements("track") ?? Enumerable.Empty<XElement>())
            {
                var items = track.Elements().Where(e => e.Name == "clipitem" || e.Name == "transitionitem" || e.Name == "generatoritem").ToList();
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it.Name == "transitionitem") continue;
                    if (string.Equals(S(it, "enabled"), "FALSE", StringComparison.OrdinalIgnoreCase)) continue;
                    if (it.Name == "generatoritem") { tl.Notes.Add($"Generator “{S(it, "name")}” isn't supported."); continue; }
                    double st = D(it, "start", -1), en = D(it, "end", -1);
                    var before = i > 0 && items[i - 1].Name == "transitionitem" ? items[i - 1] : null;
                    var after = i + 1 < items.Count && items[i + 1].Name == "transitionitem" ? items[i + 1] : null;
                    if (st < 0 && before != null) st = Cut(before);
                    if (en < 0 && after != null) en = Cut(after);
                    if (st < 0 || en <= st) continue;
                    var fe = E(it, "file");
                    if (fe != null && !fe.Elements().Any() && fe.Attribute("id")?.Value is { } fid && files.TryGetValue(fid, out var full)) fe = full;
                    double crate = XRate(E(it, "rate") ?? E(fe, "rate"), fps);
                    double speed = 1; bool rev = false;
                    foreach (var eff in it.Descendants("effect").Where(x => S(x, "effectid") == "timeremap"))
                        foreach (var p in eff.Elements("parameter"))
                        {
                            if (S(p, "parameterid") == "speed") speed = D(p, "value", 100) / 100;
                            if (S(p, "parameterid") == "reverse") rev = string.Equals(S(p, "value"), "TRUE", StringComparison.OrdinalIgnoreCase);
                        }
                    foreach (var eff in it.Descendants("effect").Where(x => S(x, "effectid") is { } eid && eid != "timeremap" && S(x, "effecttype") != "motion" && !(eid is "basic" or "opacity" or "audiolevels" or "audiopan")))
                        tl.Notes.Add($"Effect “{S(eff, "name")}” on “{S(it, "name")}” isn't supported.");
                    var c = new ImpClip
                    {
                        Video = video, Track = index, Path = LocalPath(S(fe, "pathurl")), Name = S(it, "name") ?? S(fe, "name") ?? "",
                        Start = st / fps, Dur = (en - st) / fps, In = D(it, "in") / crate, Speed = rev ? -speed : speed,
                        Link = it.Attribute("id")?.Value is { } cid && linkOf.TryGetValue(cid, out var g) && it.Elements("link").Any() ? g : null,
                    };
                    if (rev) c.In = (D(it, "out") - 1) / crate;                         // (reversed: the first frame shown is the source range's last)
                    if (Math.Abs(speed) < 1e-9) { c.Hold = true; c.Speed = 1; }
                    if (before != null && Math.Abs(Cut(before) - st) < 0.5)
                    {
                        double ts = D(before, "start"), te = D(before, "end");
                        var al = (S(before, "alignment") ?? "center").ToLowerInvariant();
                        string name = S(E(before, "effect"), "name") ?? "Cross Dissolve";
                        c.TransIn = new Transition
                        {
                            Kind = !video ? "Constant Power" : name.Contains("Dip", StringComparison.OrdinalIgnoreCase) ? (name.Contains("White", StringComparison.OrdinalIgnoreCase) ? "Dip to White" : "Dip to Black")
                                 : Transitions.VideoDefs.Any(d => d.Name == name) ? name : "Cross Dissolve",
                            Dur = (te - ts) / fps, Align = al.StartsWith("start") ? TransAlign.Start : al.StartsWith("end") ? TransAlign.End : TransAlign.Center,
                        };
                    }
                    var markers = it.Elements("marker").Select(m => XMarker(m, crate)).ToList();
                    if (markers.Count > 0) c.Markers = markers;
                    if (video && IsStill(c.Path)) { c.Kind = ClipKind.Image; c.In = 0; }
                    tl.Clips.Add(c);
                }
                index++;
            }
        }
        double Cut(XElement tr)
        {
            double s = D(tr, "start"), e = D(tr, "end");
            var al = (S(tr, "alignment") ?? "center").ToLowerInvariant();
            return al.StartsWith("start") ? s : al.StartsWith("end") ? e : (s + e) / 2;
        }
        Tracks(E(media, "video"), true);
        Tracks(E(media, "audio"), false);
        // a clip's link group shared with no other imported clip is no link
        foreach (var grp in tl.Clips.Where(c => c.Link != null).GroupBy(c => c.Link).Where(g => g.Count() < 2)) foreach (var c in grp) c.Link = null;
        return tl;
    }
    static Marker XMarker(XElement m, double fps)
    {
        double a = D(m, "in"), b = D(m, "out", -1);
        return new Marker { Name = S(m, "name") ?? "", Note = S(m, "comment") ?? "", T = a / fps, Dur = b > a ? (b - a) / fps : 0, Chapter = false };
    }

    // ---------------- CMX 3600 EDL ----------------
    static readonly Regex evRx = new(@"^\s*(\d{1,6})\s+(\S+)\s+(\S+)\s+(C|D|W\d+|K\s*[BO]?)\s*(\d{1,4})?\s+(\d\d[:;.]\d\d[:;.]\d\d[:;.]\d\d)\s+(\d\d[:;.]\d\d[:;.]\d\d[:;.]\d\d)\s+(\d\d[:;.]\d\d[:;.]\d\d[:;.]\d\d)\s+(\d\d[:;.]\d\d[:;.]\d\d[:;.]\d\d)", RegexOptions.Compiled);
    static readonly Regex m2Rx = new(@"^\s*M2\s+(\S+)\s+(-?\d+(?:\.\d+)?)\s+(\d\d[:;.]\d\d[:;.]\d\d[:;.]\d\d)", RegexOptions.Compiled);
    static long TcFrames(string tc, int nom)
    {
        var p = tc.Split(':', ';', '.');
        return ((long.Parse(p[0]) * 60 + long.Parse(p[1])) * 60 + long.Parse(p[2])) * nom + long.Parse(p[3]);
    }

    /// <summary>A CMX 3600 EDL at the given rate (EDLs don't say theirs). Record times start the timeline at the first event's record in.
    /// The media: "* SOURCE FILE:" (CutPro's EDLs) or "* FROM CLIP NAME:" (matched to the project's media by the caller).</summary>
    public static ImpTimeline Edl(string text, double fps)
    {
        var tl = new ImpTimeline { Fps = fps };
        int nom = (int)Math.Round(fps);
        var evs = new List<(ImpClip c, string reel, long r0)>();
        ImpClip? last = null; string lastReel = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase)) { tl.Name = line[6..].Trim(); continue; }
            if (evRx.Match(line) is { Success: true } m)
            {
                string reel = m.Groups[2].Value, chan = m.Groups[3].Value.ToUpperInvariant(), kind = m.Groups[4].Value;
                long s0 = TcFrames(m.Groups[6].Value, nom), s1 = TcFrames(m.Groups[7].Value, nom), r0 = TcFrames(m.Groups[8].Value, nom), r1 = TcFrames(m.Groups[9].Value, nom);
                if (reel is "BL" or "BLK" or "BLACK") { last = null; continue; }
                bool video = chan.Contains('V'), audio = chan.StartsWith("A") || chan == "B" || chan.Contains("/A") || chan == "AA";
                if (r1 <= r0) { last = null; continue; }                            // (a zero-length cut: the outgoing side of a dissolve)
                foreach (var (v, track) in new[] { (true, 0), (false, chan.Contains("A2") && !chan.StartsWith("AA") ? 1 : 0) })
                {
                    if (v && !video) continue;
                    if (!v && !audio) continue;
                    var c = new ImpClip { Video = v, Track = track, Start = r0 / fps, Dur = (r1 - r0) / fps, SrcTc = s0, Name = reel };
                    if (kind.StartsWith("D")) c.TransIn = new Transition { Kind = v ? "Cross Dissolve" : "Constant Power", Dur = (m.Groups[5].Success ? int.Parse(m.Groups[5].Value) : 0) / fps, Align = TransAlign.Start };
                    else if (kind.StartsWith("W") || kind.StartsWith("K")) tl.Notes.Add($"Event {m.Groups[1].Value}: {kind} became a cut.");
                    evs.Add((c, reel, r0)); tl.Clips.Add(c);
                    last = c;
                }
                lastReel = reel;
                continue;
            }
            if (m2Rx.Match(line) is { Success: true } m2 && last != null)
            {
                double r = double.Parse(m2.Groups[2].Value, inv) / fps;
                foreach (var c in evs.Where(e => e.reel == m2.Groups[1].Value && Math.Abs(e.c.Start - last.Start) < 1e-6).Select(e => e.c))
                    if (Math.Abs(r) < 1e-9) c.Hold = true; else c.Speed = r;          // (reversed: In is worked out with the media's timecode)
                continue;
            }
            var cm = line.TrimStart();
            if (cm.StartsWith("* FROM CLIP NAME:", StringComparison.OrdinalIgnoreCase) && last != null)
                foreach (var c in tl.Clips.Where(c => Math.Abs(c.Start - last.Start) < 1e-6 && c.Name == lastReel)) c.Name = cm["* FROM CLIP NAME:".Length..].Trim();
            else if (cm.StartsWith("* SOURCE FILE:", StringComparison.OrdinalIgnoreCase) && last != null)
                foreach (var c in tl.Clips.Where(c => Math.Abs(c.Start - last.Start) < 1e-6 && c.Path == null)) c.Path = cm["* SOURCE FILE:".Length..].Trim();
            else if (cm.StartsWith("* MARKER:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = cm["* MARKER:".Length..].Trim().Split(' ', 3);
                if (parts.Length >= 1 && Regex.IsMatch(parts[0], @"^\d\d:\d\d:\d\d:\d\d$"))
                    tl.Markers.Add(new Marker { T = TcFrames(parts[0], nom) / fps, Color = parts.Length > 1 && Enum.TryParse<MarkerColor>(parts[1], out var col) ? col : MarkerColor.Green, Name = parts.Length > 2 ? parts[2] : "", Chapter = false });
            }
        }
        // record times from the first event (usually 01:00:00:00)
        double origin = evs.Count > 0 ? evs.Min(e => e.r0) / fps : 0;
        foreach (var c in tl.Clips) c.Start -= origin;
        foreach (var mk in tl.Markers) mk.T = Math.Max(0, mk.T - origin);
        foreach (var c in tl.Clips) if (IsStill(c.Path) && c.Video) c.Kind = ClipKind.Image;
        // the audio tracks after A2 came in as A2 — an EDL has at most 4 channels; picture and sound of one event are one shot
        foreach (var v in tl.Clips.Where(c => c.Video))
            foreach (var a in tl.Clips.Where(c => !c.Video && c.Link == null && Math.Abs(c.Start - v.Start) < 1e-6 && Math.Abs(c.Dur - v.Dur) < 1e-6 && c.SrcTc == v.SrcTc && c.Name == v.Name).Take(1))
                v.Link = a.Link = v.Link ?? Guid.NewGuid().ToString("N");
        return tl;
    }
}
