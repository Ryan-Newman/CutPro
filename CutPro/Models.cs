using System.Text.Json.Serialization;
using System.Windows.Media;

namespace CutPro;

public enum ClipKind { Video, Audio, Image, Text, Adjustment }
/// <summary>How a picture of another shape fills the sequence frame: Fit (letterbox), Fill (crop; Pan picks the part), FitBlur (fit over a blurred, filled copy — the usual landscape-to-Shorts look).</summary>
public enum ReframeMode { Fit, Fill, FitBlur }

/// <summary>
/// The track layout of the active sequence (or, on a thread rendering another sequence, that one's: <see cref="SeqScope"/>). A clip's
/// <see cref="Clip.Track"/> is its timeline row, top to bottom: the video tracks first — row 0 is the highest (drawn on top), row
/// <see cref="V1"/> = Video − 1 the lowest — then the audio tracks, A1 at row <see cref="A1"/> = Video. Projects from before were 3 + 2
/// (rows V3 V2 V1 A1 A2), which is the default. Each <see cref="Sequence"/> keeps its own counts and per-track settings
/// (<see cref="TrackInfo"/>: name, lock, height); <see cref="Use"/> makes a sequence's layout the active one.
/// </summary>
public static class Tracks
{
    public const int DefaultVideo = 3, DefaultAudio = 2, MaxVideo = 32, MaxAudio = 32;
    /// <summary>Most rows a sequence can have (mixer buses and meter slots are sized for it).</summary>
    public const int Max = MaxVideo + MaxAudio;
    /// <summary>Timeline row height in pixels (a track's own height when it has none).</summary>
    public const double DefaultHeight = 54, MinHeight = 28, MaxHeight = 180;
    static int video = DefaultVideo, audio = DefaultAudio;
    static List<TrackInfo>? info;
    public static int Video => SeqScope.Current?.Video ?? video;
    public static int Audio => SeqScope.Current?.Audio ?? audio;
    public static int Count => Video + Audio;
    public static bool IsVideo(int row) => row < Video;
    /// <summary>Row of the lowest video track (V1) and of the first audio track (A1).</summary>
    public static int V1 => Video - 1;
    public static int A1 => Video;
    /// <summary>Row of track Vn / An (n from 1), or −1 when the sequence has fewer tracks.</summary>
    public static int V(int n) => n >= 1 && n <= Video ? Video - n : -1;
    public static int A(int n) => n >= 1 && n <= Audio ? Video + n - 1 : -1;
    /// <summary>The track's number: 1 for V1 and A1.</summary>
    public static int Number(int row) => IsVideo(row) ? Video - row : row - Video + 1;
    /// <summary>"V1", "A2" — the position, whatever the track is called.</summary>
    public static string Label(int row) => (IsVideo(row) ? "V" : "A") + Number(row);
    /// <summary>The track's name (its own, or its label).</summary>
    public static string Name(int row) => info is { } l && row >= 0 && row < l.Count && !string.IsNullOrWhiteSpace(l[row].Name) ? l[row].Name! : Label(row);
    public static bool Locked(int row) => info is { } l && row >= 0 && row < l.Count && l[row].Locked;
    public static double Height(int row) => info is { } l && row >= 0 && row < l.Count && l[row].Height > 0 ? Math.Clamp(l[row].Height, MinHeight, MaxHeight) : DefaultHeight;
    /// <summary>The video rows from the lowest (V1) up, and the audio rows from A1 down.</summary>
    public static IEnumerable<int> VideoRows => Enumerable.Range(0, Video).Reverse();
    public static IEnumerable<int> AudioRows => Enumerable.Range(Video, Audio);
    /// <summary>Makes a sequence's layout the active one (UI thread: the editor's active sequence).</summary>
    public static void Use(Sequence s) { s.Fix(); video = s.VideoTracks; audio = s.AudioTracks; info = s.TrackInfo; }
}

/// <summary>A track's own settings (per row of a <see cref="Sequence"/>): the name shown in its header (null = V1, A2 …), whether it is
/// locked (its clips can't be selected, moved, cut or rippled, and nothing is edited onto it) and its height in the timeline (0 = the default).</summary>
public sealed class TrackInfo
{
    public string? Name { get; set; }
    public bool Locked { get; set; }
    public double Height { get; set; }
    public TrackInfo Clone() => (TrackInfo)MemberwiseClone();
}

public class MediaItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public ClipKind Type { get; set; }
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool HasAudio { get; set; }
    /// <summary>Video frame rate (0 = unknown / still image).</summary>
    public double Fps { get; set; }
    /// <summary>Source monitor marks (seconds of the media; null = its start / end), kept with the project like Premiere's.</summary>
    public double? SrcIn { get; set; }
    public double? SrcOut { get; set; }
    /// <summary>Where the source monitor's playhead was (reopening the clip goes back there).</summary>
    public double SrcTime { get; set; }

    [JsonIgnore] public string Name => SeqName ?? System.IO.Path.GetFileName(Path);
    [JsonIgnore] public ImageSource? Thumb { get; set; }
    /// <summary>Set on the item that stands for a sequence of the project (a nested sequence used as a clip: <see cref="Nest"/>). It has no
    /// file of its own — Width / Height / Duration / HasAudio / Fps describe the sequence (<see cref="Editor.SeqItem"/>).</summary>
    [JsonIgnore] public string? Seq { get; set; }
    [JsonIgnore] public string? SeqName { get; set; }
    /// <summary>Cached "file missing" flag (checking the disk every frame made playback slower as clips were added).
    /// Refreshed on import/open and periodically by the main window.</summary>
    [JsonIgnore] public bool Offline { get; private set; }
    /// <summary>Re-checks the file; returns true if the online/offline state changed.</summary>
    public bool RefreshOnline() { if (Seq != null) return false; bool o = !File.Exists(Path); if (o == Offline) return false; Offline = o; return true; }
    // filled by MediaProbe (ffprobe) / Decoders.Calibrate on import and project open
    [JsonIgnore] public int Rotation { get; set; }
    [JsonIgnore] public string Codec { get; set; } = "";
    [JsonIgnore] public bool TenBit { get; set; }
    /// <summary>Decoder used when a frame is needed now (scrubbing, stopping, stepping).</summary>
    [JsonIgnore] public Decoder SeekDecoder { get; set; } = Decoder.SwFrame;
    /// <summary>Decoder used for playback (sustained throughput).</summary>
    [JsonIgnore] public Decoder PlayDecoder { get; set; } = Decoder.SwFrame;
    [JsonIgnore] public string DecodeNote { get; set; } = "";
    /// <summary>What the file says about itself (container, streams, timecode, reel, camera …; <see cref="MediaProbe"/>), its start timecode and reel name.</summary>
    [JsonIgnore] public List<KeyValuePair<string, string>> Meta { get; set; } = new();
    [JsonIgnore] public string? Timecode { get; set; }
    [JsonIgnore] public string? Reel { get; set; }
    /// <summary>The Project panel's tooltip: the path and the metadata.</summary>
    [JsonIgnore] public string Tip => Seq != null ? "Sequence — double-click to open it, drag it into another sequence to nest it" :
        Path + (Offline ? "\n(offline — right-click: Relink…)" : "") + string.Concat(Meta.Select(kv => $"\n{kv.Key}: {kv.Value}"));

    [JsonIgnore] public string Info => Seq != null ? $"Sequence  {TimeUtil.Format(Duration, false)}  {Width}×{Height}  {TimeUtil.Label(Fps)} fps" :
        (Offline ? "OFFLINE  " : "") + Type + (Type == ClipKind.Image ? "" : "  " + TimeUtil.Format(Duration, false))
        + (Type == ClipKind.Video && Width > 0 ? $"  {Width}×{Height} {Codec.ToUpperInvariant()}{(TenBit ? " 10-bit" : "")}" : "")
        + (Timecode != null ? "  TC " + Timecode : "") + (Reel != null ? "  Reel " + Reel : "")
        + (Decoders.IsHardware(PlayDecoder) || Decoders.IsHardware(SeekDecoder) ? $"  · {Decoders.Label(Decoders.IsHardware(PlayDecoder) ? PlayDecoder : SeekDecoder)} decode" : "");
}

public class Clip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? MediaId { get; set; }
    public ClipKind Kind { get; set; }
    public int Track { get; set; }
    public double Start { get; set; }
    public double In { get; set; }
    public double Dur { get; set; }

    public double Opacity { get; set; } = 100;
    public double Scale { get; set; } = 100;
    public double X { get; set; }
    public double Y { get; set; }
    public double Volume { get; set; } = 100;
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }

    public string Text { get; set; } = "Title";
    /// <summary>Legacy (projects before text styles); read once into <see cref="Style"/>.</summary>
    public string Color { get; set; } = "#FFFFFF";
    public double Size { get; set; } = 90;

    // titles
    public TextStyle? Style { get; set; }
    public TextRole Role { get; set; }
    public MaskMode Mask { get; set; }                   // track matte: a title (its text) or a video / picture (MatteLuma) masks everything below it
    /// <summary>A video / picture used as a track matte: its brightness (true) or its alpha (false) is the matte.</summary>
    public bool MatteLuma { get; set; } = true;
    public double MaskFeather { get; set; }
    public double Rotation { get; set; }                 // degrees, about the clip centre (or the anchor point)
    /// <summary>Anchor point (pivot of scale and rotation) as an offset from the picture's centre, % of its size at 100 % (keyframable).</summary>
    public double AnchorX { get; set; }
    public double AnchorY { get; set; }
    /// <summary>Shorts "Fill": which part of a cropped picture shows (−100 left edge … 0 centre … 100 right edge).</summary>
    public double Pan { get; set; }
    /// <summary>Keyframes / formulas per property (null = none). See Anim.</summary>
    public Dictionary<AnimProp, PropTrack>? Anim { get; set; }
    /// <summary>Gaussian blur radius in sequence pixels (video, image and title clips).</summary>
    public double Blur { get; set; }
    /// <summary>Object masks (selected people / objects / regions) with their effects — video and image clips.</summary>
    public List<ObjectMask> Masks { get; set; } = new();
    [JsonIgnore] public TextStyle Look => Style ??= new TextStyle { Fill = Color, Size = Size };

    /// <summary>Clips sharing a LinkId (e.g. a video and its audio) are selected, moved, trimmed and cut together.</summary>
    public string? LinkId { get; set; }
    /// <summary>Clips sharing a GroupId are selected and moved together (Ctrl+G), without linking their edits.</summary>
    public string? GroupId { get; set; }
    /// <summary>Clip markers (source seconds: they stay with the content).</summary>
    public List<Marker>? Markers { get; set; }
    /// <summary>Transition at the clip's head (with the clip that ends where this one starts, or from nothing) and at its tail
    /// (only used when no adjacent clip follows with its own head transition). See <see cref="Transitions"/>.</summary>
    public Transition? TransIn { get; set; }
    public Transition? TransOut { get; set; }
    /// <summary>True for video clips whose sound lives in a separate (linked) audio clip.</summary>
    public bool NoAudio { get; set; }
    /// <summary>Playback speed in % (Speed keyframes on video clips = time remapping), played backwards, frozen on the frame at
    /// <see cref="In"/>, and whether sped-up / slowed sound keeps its pitch. <see cref="In"/> is always the source time shown at the
    /// clip's start (the high end when reversed). See <see cref="TimeMap"/>.</summary>
    /// <summary>Colour correction (Lumetri-style) and a drawn look (cartoon / cel / anime) — video and image clips; null = none.
    /// See <see cref="CutPro.Grade"/> and <see cref="CutPro.Toon"/>.</summary>
    public ColorGrade? Grade { get; set; }
    public ToonStyle? Toon { get; set; }
    /// <summary>Opacity → Blend Mode with the tracks below (<see cref="CutPro.Blend"/>).</summary>
    public BlendMode Blend { get; set; }
    /// <summary>Crop: % of the picture taken off each side as it is shown (after flips; keyframable as AnimProp.CropL…), and the soft edge
    /// inwards in logical px of the picture's height at 720 (<see cref="CutPro.Crop"/>).</summary>
    public double CropL { get; set; }
    public double CropT { get; set; }
    public double CropR { get; set; }
    public double CropB { get; set; }
    public double CropFeather { get; set; }
    public bool FlipH { get; set; }
    public bool FlipV { get; set; }
    /// <summary>Chroma key (null = none): <see cref="Keyer"/>.</summary>
    public ChromaKey? Key { get; set; }
    /// <summary>Stabilization (null = off): <see cref="CutPro.Stab"/> renders it in the background; preview and export read the file.</summary>
    public Stabilize? Stab { get; set; }
    /// <summary>Lens distortion (null = off): <see cref="CutPro.Lens"/>.</summary>
    public LensDistort? Lens { get; set; }
    /// <summary>Look effects: sharpen, glow, grain (null = none) — <see cref="CutPro.Looks"/>.</summary>
    public LookFx? Looks { get; set; }
    /// <summary>Drop shadow under the clip's picture (null = none).</summary>
    public DropShadow? Shadow { get; set; }
    /// <summary>Corner pin (null = off): <see cref="CutPro.Pin"/>.</summary>
    public CornerPin? Pin { get; set; }
    /// <summary>Audio effects on the clip's sound, in order (null = none): <see cref="AudioDsp"/>.</summary>
    public List<AudioEffect>? AudioFx { get; set; }
    public double Speed { get; set; } = 100;
    public bool Reverse { get; set; }
    public bool Hold { get; set; }
    public bool PitchLock { get; set; } = true;
    /// <summary>Pitch shift in semitones (0 = none; the sound is rendered like a speed change: <see cref="AudioCache.Derives"/>).</summary>
    public double Pitch { get; set; }
    /// <summary>Keep the voice's character (formants) when shifting the pitch.</summary>
    public bool Formant { get; set; } = true;

    [JsonIgnore] public double End => Start + Dur;

    public double Fade(double t)
    {
        double f = 1;
        if (FadeIn > 0) f = Math.Min(f, (t - Start) / FadeIn);
        if (FadeOut > 0) f = Math.Min(f, (End - t) / FadeOut);
        return Math.Clamp(f, 0, 1);
    }
}

/// <summary>One timeline of a project: its clips, format (output size, frame rate, reframe), mixer, hidden / muted tracks, playhead and
/// In / Out. A project has one or more; any sequence can be used as a clip in another (a nested sequence, rendered by <see cref="Nest"/>).
/// While a sequence is the editor's active one, its format lives in the static <see cref="Editor.OutW"/> … (<see cref="Editor.FormatOf"/>).</summary>
public class Sequence
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Sequence 01";
    public List<Clip> Clips { get; set; } = new();
    public int SeqW { get; set; } = 1280;
    public int SeqH { get; set; } = 720;
    public double SeqFps { get; set; } = 30;
    public ReframeMode Reframe { get; set; }
    /// <summary>How many video and audio tracks it has (projects from before: 3 + 2). See <see cref="Tracks"/> for the row order.</summary>
    public int VideoTracks { get; set; } = Tracks.DefaultVideo;
    public int AudioTracks { get; set; } = Tracks.DefaultAudio;
    /// <summary>Per row: name, lock, height (null = all default).</summary>
    public List<TrackInfo>? TrackInfo { get; set; }
    public MixState Mix { get; set; } = new();
    /// <summary>Per row: hidden (video) / muted (audio).</summary>
    public bool[] TrackOff { get; set; } = new bool[Tracks.DefaultVideo + Tracks.DefaultAudio];
    public double Time { get; set; }
    public double? MarkIn { get; set; }
    public double? MarkOut { get; set; }
    /// <summary>Sequence markers (timeline seconds; chapters for the export / YouTube). See <see cref="Marker"/>.</summary>
    public List<Marker>? Markers { get; set; }
    [JsonIgnore] public double End => Clips.Count == 0 ? 0 : Clips.Max(c => c.End);
    [JsonIgnore] public int TrackCount => VideoTracks + AudioTracks;

    /// <summary>Keeps the per-row lists as long as the track count (older or hand-edited files; a count within 1 … the maximum).</summary>
    public Sequence Fix()
    {
        VideoTracks = Math.Clamp(VideoTracks, 1, Tracks.MaxVideo); AudioTracks = Math.Clamp(AudioTracks, 1, Tracks.MaxAudio);
        int n = TrackCount;
        if (TrackOff is not { } off || off.Length != n) TrackOff = Enumerable.Range(0, n).Select(i => TrackOff != null && i < TrackOff.Length && TrackOff[i]).ToArray();
        Mix = (Mix ?? new MixState()).Fixed(n);
        if (TrackInfo != null && TrackInfo.Count != n)
            TrackInfo = Enumerable.Range(0, n).Select(i => i < TrackInfo.Count && TrackInfo[i] != null ? TrackInfo[i] : new TrackInfo()).ToList();
        return this;
    }
    /// <summary>A copy of the layout (counts, per-row settings) onto another sequence — its clips use the same rows.</summary>
    public void CopyLayout(Sequence to)
    {
        to.VideoTracks = VideoTracks; to.AudioTracks = AudioTracks;
        to.TrackInfo = TrackInfo?.Select(t => t.Clone()).ToList();
        to.Fix();
    }
}

public class ProjectFile
{
    /// <summary>1 = one timeline in <see cref="Clips"/> and the Seq* fields; 2 = <see cref="Sequences"/> (the old fields stay empty);
    /// 3 = sequences with their own track counts (<see cref="Sequence.VideoTracks"/> …; a version 2 sequence is 3 + 2).</summary>
    public int Version { get; set; } = 3;
    public List<MediaItem> Media { get; set; } = new();
    public List<Clip> Clips { get; set; } = new();
    public string Theme { get; set; } = "Modern";
    /// <summary>Per-frame outlines of object masks (mask id → track).</summary>
    public Dictionary<string, MaskTrack> MaskTracks { get; set; } = new();
    /// <summary>Sequence: output size, frame rate, how pictures of another shape fill the frame (projects from before: 1280×720 @ 30).</summary>
    public int SeqW { get; set; } = 1280;
    public int SeqH { get; set; } = 720;
    public double SeqFps { get; set; } = 30;
    public ReframeMode Reframe { get; set; }
    public Dictionary<string, Dictionary<string, string>> Publish { get; set; } = new();
    /// <summary>Audio Track Mixer (null in older projects: every fader at 0 dB, centred) and the hidden / muted tracks.</summary>
    public MixState? Mix { get; set; }
    public bool[]? TrackOff { get; set; }
    /// <summary>Every sequence (version 2), the one being edited and the ones open as timeline tabs.</summary>
    public List<Sequence>? Sequences { get; set; }
    public string? Active { get; set; }
    public List<string>? Open { get; set; }

    /// <summary>The project's sequences (a version 1 file becomes one sequence).</summary>
    public List<Sequence> AllSequences()
    {
        if (Sequences is { Count: > 0 }) { foreach (var s in Sequences) s.Fix(); return Sequences; }
        return new List<Sequence> { new Sequence { Name = "Sequence 01", Clips = Clips, SeqW = SeqW, SeqH = SeqH, SeqFps = SeqFps, Reframe = Reframe,
                                                   Mix = Mix ?? new MixState(), TrackOff = TrackOff ?? new bool[Tracks.DefaultVideo + Tracks.DefaultAudio] }.Fix() };
    }
}

public static class TimeUtil
{
    /// <summary>Sequence frame rate (the active sequence's — or, on a thread rendering another sequence, that one's: <see cref="SeqScope"/>;
    /// NTSC rates are the exact fractions, e.g. 30000/1001).</summary>
    public static double Fps => SeqScope.Current?.Fps ?? fps;
    static double fps = 30;
    /// <summary>Frames per timecode second (30 for 29.97 — non-drop-frame timecode).</summary>
    public static int Nominal => (int)Math.Round(Fps);
    /// <summary>The rate as ffmpeg wants it ("30000/1001", "25").</summary>
    public static string Rate => Rational(Fps);
    /// <summary>One frame as a time base ("1001/30000", "1/25").</summary>
    public static string Tb => Rate.Contains('/') ? $"{Rate.Split('/')[1]}/{Rate.Split('/')[0]}" : $"1/{Rate}";
    public static readonly double[] Standard = { 23.976, 24, 25, 29.97, 30, 50, 59.94, 60 };

    public static double Exact(double fps) =>
        Math.Abs(fps - 23.976) < 0.01 ? 24000.0 / 1001 : Math.Abs(fps - 29.97) < 0.01 ? 30000.0 / 1001 : Math.Abs(fps - 59.94) < 0.01 ? 60000.0 / 1001 : fps;
    public static string Rational(double fps) =>
        Math.Abs(fps - 24000.0 / 1001) < 0.001 ? "24000/1001" : Math.Abs(fps - 30000.0 / 1001) < 0.001 ? "30000/1001" : Math.Abs(fps - 60000.0 / 1001) < 0.001 ? "60000/1001"
        : Math.Abs(fps - Math.Round(fps)) < 1e-6 ? ((int)Math.Round(fps)).ToString() : fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    public static string Label(double fps) => Math.Abs(fps - Math.Round(fps)) < 1e-6 ? ((int)Math.Round(fps)).ToString() : fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    internal static void SetFps(double v) => fps = Exact(Math.Clamp(v, 1, 240));

    public static string Format(double t, bool frames = true)
    {
        t = Math.Max(0, t);
        long f = (long)Math.Floor(t * Fps + 1e-6);
        int n = Nominal;
        long s = f / n, fr = f % n;
        string core = $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}";
        return frames ? $"{core}:{fr:00}" : core.Substring(3);
    }

    /// <summary>The media's own timecode at source time t (its start timecode tag + the frames since; null when the file has none). "HH:MM:SS;FF"
    /// is drop-frame (29.97 / 59.94: frame numbers 0 and 1 — 0 to 3 at 59.94 — are skipped each minute except every tenth).</summary>
    public static string? SourceTimecode(MediaItem m, double t)
    {
        if (m.Timecode is not { } tc) return null;
        var p = tc.Split(':', ';', '.');
        if (p.Length != 4 || !p.All(x => int.TryParse(x, out _))) return null;
        double rate = m.Fps > 0 ? m.Fps : Fps;
        int nom = (int)Math.Round(rate);
        if (nom <= 0) return null;
        bool df = tc.Contains(';') && Math.Abs(rate * 1001 / 1000 - nom) < 0.05 && nom % 30 == 0;
        int drop = df ? nom / 15 : 0;                                       // 2 at 29.97, 4 at 59.94
        int h = int.Parse(p[0]), mi = int.Parse(p[1]), s = int.Parse(p[2]), f = int.Parse(p[3]);
        int totalMin = h * 60 + mi;
        long start = ((long)totalMin * 60 + s) * nom + f - drop * (totalMin - totalMin / 10);
        long n = start + (long)Math.Floor(Math.Max(0, t) * rate + 1e-6);
        if (df)
        {   // frame count → drop-frame label
            long perMin = nom * 60 - drop, per10 = perMin * 10 + drop;
            long d = n / per10, r = n % per10;
            n += drop * 9 * d + (r > drop ? drop * ((r - drop) / perMin) : 0);
        }
        return $"{n / (nom * 3600L) % 24:00}:{n / (nom * 60L) % 60:00}:{n / nom % 60:00}{(df ? ';' : ':')}{n % nom:00}";
    }
}
