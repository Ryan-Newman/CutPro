namespace CutPro;

/// <summary>Whoosh, hit, flash and similar transition sounds, synthesised as 48 kHz mono WAV (cached in
/// %LOCALAPPDATA%\CutPro\Library) so a transition can carry its own effect the way Sonduck packs do — preview and export both
/// play the file as a normal audio clip.</summary>
public enum SfxKind { None, Whoosh, WhooshAir, Hit, Punch, Flash, Scratch, Riser, Glitch, Boom, Tape, Paper, Swipe, Click }

public static class Sfx
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro", "Library");

    public static string Ensure(SfxKind kind, double seconds)
    {
        if (kind == SfxKind.None) return "";
        seconds = Math.Clamp(seconds, 0.08, 4);
        string path = Path.Combine(Dir, $"{kind}_{seconds:0.00}s.wav");
        if (File.Exists(path) && new FileInfo(path).Length > 200) return path;
        const int rate = 48000;
        int n = Math.Max(64, (int)Math.Round(seconds * rate));
        var pcm = new byte[n * 2];
        var rng = new Random((int)kind * 9176 + (int)(seconds * 1000));
        double prev = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)rate, u = n == 1 ? 1 : i / (double)(n - 1);
            double noise = rng.NextDouble() * 2 - 1;
            double s = Sample(kind, t, u, seconds, noise, ref prev);
            short v = (short)(Math.Clamp(s, -1, 1) * 22000);
            pcm[i * 2] = (byte)v; pcm[i * 2 + 1] = (byte)(v >> 8);
        }
        Directory.CreateDirectory(Dir);
        string part = path + ".part";
        using (var w = new WavWriter(part, rate, 1)) w.Write(pcm);
        File.Move(part, path, true);
        return path;
    }

    /// <summary>A project-panel item for the generated file (no probe — duration is exact).</summary>
    public static MediaItem Media(SfxKind kind, double seconds)
    {
        string path = Ensure(kind, seconds);
        return new MediaItem { Path = path, Type = ClipKind.Audio, Duration = seconds, HasAudio = true, DisplayName = kind + " " + seconds.ToString("0.##") + "s", Bin = "Library", Fps = 0 };
    }

    static double Sample(SfxKind k, double t, double u, double dur, double noise, ref double prev)
    {
        double env = u < 0.08 ? u / 0.08 : u > 0.75 ? (1 - u) / 0.25 : 1;
        return k switch
        {
            SfxKind.Whoosh => Whoosh(t, u, dur, noise, 900, 180) * env,
            SfxKind.WhooshAir => Whoosh(t, u, dur, noise, 2400, 400) * env * 0.7,
            SfxKind.Swipe => Whoosh(t, u, dur, noise, 1400, 220) * env,
            SfxKind.Hit => Hit(t, noise, 90) + Click(t, 0.012, noise) * 0.5,
            SfxKind.Punch => Hit(t, noise, 55) + Click(t, 0.008, noise),
            SfxKind.Flash => Click(t, 0.04, noise) + Hit(t, noise, 400) * 0.35,
            SfxKind.Click => Click(t, 0.018, noise),
            SfxKind.Scratch => (noise * 0.55 + Math.Sin(t * 3400) * 0.12) * env * (0.4 + 0.6 * Math.Abs(Math.Sin(u * 17))),
            SfxKind.Riser => Math.Sin(2 * Math.PI * (180 + 1600 * u * u) * t) * (0.15 + 0.85 * u) * env,
            SfxKind.Glitch => Glitch(t, u, noise, ref prev),
            SfxKind.Boom => Hit(t, noise, 38) * 1.2 + Math.Sin(2 * Math.PI * 48 * t) * Math.Exp(-t * 6) * 0.5,
            SfxKind.Tape => (noise * 0.4 + Math.Sin(t * (700 + 9000 * u)) * 0.2) * env * (u < 0.2 ? u / 0.2 : 1),
            SfxKind.Paper => (noise * 0.7) * env * (0.3 + 0.7 * Math.Abs(Math.Sin(u * 40))),
            _ => 0
        };
    }

    static double Whoosh(double t, double u, double dur, double noise, double hi, double lo)
    {
        double f = hi + (lo - hi) * u;
        double tone = Math.Sin(2 * Math.PI * f * t) * 0.15;
        double air = noise * (0.25 + 0.55 * (1 - u));
        return (tone + air) * (0.35 + 0.65 * Math.Sin(Math.PI * u));
    }
    static double Hit(double t, double noise, double hz) => (Math.Sin(2 * Math.PI * hz * t) + noise * 0.35) * Math.Exp(-t * 14);
    static double Click(double t, double w, double noise) => t < w ? noise * (1 - t / w) : 0;
    static double Glitch(double t, double u, double noise, ref double prev)
    {
        if (u % 0.07 < 0.012) prev = noise;
        return (prev * 0.6 + noise * 0.4) * (0.4 + 0.6 * envSaw(u));
        static double envSaw(double x) => 1 - (x * 11 % 1);
    }
}
