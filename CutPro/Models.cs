using System.Text.Json.Serialization;
using System.Windows.Media;

namespace CutPro;

public enum ClipKind { Video, Audio, Image, Text, Adjustment }
/// <summary>How a picture of another shape fills the sequence frame: Fit (letterbox), Fill (crop; Pan picks the part), FitBlur (fit over a blurred, filled copy — the usual landscape-to-Shorts look).</summary>
public enum ReframeMode { Fit, Fill, FitBlur }

public static class Tracks
{
    // Row order top->bottom. V3 draws on top of V2 on top of V1.
    public static readonly string[] Names = { "V3", "V2", "V1", "A1", "A2" };
    public static bool IsVideo(int i) => i < 3;
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

    [JsonIgnore] public string Info => Seq != null ? $"Sequence  {TimeUtil.Format(Duration, false)}  {Width}×{Height}  {TimeUtil.Label(Fps)} fps" :
        (Offline ? "OFFLINE  " : "") + Type + (Type == ClipKind.Image ? "" : "  " + TimeUtil.Format(Duration, false))
        + (Type == ClipKind.Video && Width > 0 ? $"  {Width}×{Height} {Codec.ToUpperInvariant()}{(TenBit ? " 10-bit" : "")}" : "")
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
    public MixState Mix { get; set; } = new();
    public bool[] TrackOff { get; set; } = new bool[5];
    public double Time { get; set; }
    public double? MarkIn { get; set; }
    public double? MarkOut { get; set; }
    [JsonIgnore] public double End => Clips.Count == 0 ? 0 : Clips.Max(c => c.End);
}

public class ProjectFile
{
    /// <summary>1 = one timeline in <see cref="Clips"/> and the Seq* fields; 2 = <see cref="Sequences"/> (the old fields stay empty).</summary>
    public int Version { get; set; } = 2;
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
        if (Sequences is { Count: > 0 }) { foreach (var s in Sequences) { s.Mix = (s.Mix ?? new MixState()).Fixed(); if (s.TrackOff is not { Length: 5 }) s.TrackOff = new bool[5]; } return Sequences; }
        return new List<Sequence> { new() { Name = "Sequence 01", Clips = Clips, SeqW = SeqW, SeqH = SeqH, SeqFps = SeqFps, Reframe = Reframe,
                                            Mix = (Mix ?? new MixState()).Fixed(), TrackOff = TrackOff is { Length: 5 } ? TrackOff : new bool[5] } };
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
}
