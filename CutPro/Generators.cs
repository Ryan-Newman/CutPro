using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

public enum GenKind { ColorMatte, Gradient, Bars, Countdown }

/// <summary>A generated project item (Premiere's New Item): colour matte, gradient, bars and tone, counting leader. It is rendered once to an
/// ordinary media file (PNG, or lossless H.264 + PCM in a MOV) named by its settings, so preview and export read the same pixels and sound
/// through the normal media path; a missing file is rendered again (<see cref="Generators.EnsureAsync"/>).</summary>
public sealed class Generator
{
    public GenKind Kind { get; set; }
    public string Color1 { get; set; } = "#000000";
    public string Color2 { get; set; } = "#FFFFFF";
    /// <summary>Gradient: radial (from the centre) instead of linear; the linear direction in degrees (0 = left → right, 90 = top → bottom).</summary>
    public bool Radial { get; set; }
    public double Angle { get; set; } = 90;
    /// <summary>Length (bars and tone; the leader counts down from this many seconds).</summary>
    public double Seconds { get; set; } = 30;
    /// <summary>Tone / beeps: level (dBFS peak) and pitch.</summary>
    public double ToneDb { get; set; } = -20;
    public double ToneHz { get; set; } = 1000;
    /// <summary>Counting leader: a beep at every number (otherwise only the "2-pop").</summary>
    public bool BeepAll { get; set; }
    public int W { get; set; } = 1920;
    public int H { get; set; } = 1080;
    public double Fps { get; set; } = 30;

    public string Sig()
    {
        var json = JsonSerializer.Serialize(this);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json + "|g1")))[..12].ToLowerInvariant();
    }
    public bool Still => Kind is GenKind.ColorMatte or GenKind.Gradient;
    public string Title => Kind switch { GenKind.ColorMatte => "Colour Matte", GenKind.Gradient => "Gradient", GenKind.Bars => "Bars and Tone", _ => "Counting Leader" };
}

public static class Generators
{
    public static string? DirOverride;                                         // (tests)
    public static string Dir => DirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro", "Generated");
    public static string FileOf(Generator g) => Path.Combine(Dir, $"{g.Kind.ToString().ToLowerInvariant()}-{g.Sig()}{(g.Still ? ".png" : ".mov")}");
    static readonly CultureInfo inv = CultureInfo.InvariantCulture;
    static Color C(string hex) { try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Colors.Black; } }

    /// <summary>A generator for the current sequence's output size and rate.</summary>
    public static Generator For(GenKind k) => new() { Kind = k, W = Editor.OutW, H = Editor.OutH, Fps = TimeUtil.Fps, Seconds = k == GenKind.Countdown ? 8 : 30 };

    /// <summary>Renders the generator's file if it isn't there yet; returns its path.</summary>
    public static async Task<string> EnsureAsync(Generator g, IProgress<double>? progress = null)
    {
        var f = FileOf(g);
        if (File.Exists(f)) return f;
        Directory.CreateDirectory(Dir);
        var tmp = f + ".part" + Path.GetExtension(f);
        if (g.Still) await Sta(() => SavePng(Draw(g, 0), tmp));
        else if (g.Kind == GenKind.Bars) await Task.Run(() => Bars(g, tmp));
        else await Countdown(g, tmp, progress);
        File.Move(tmp, f, true);
        return f;
    }

    /// <summary>A new project item for the generator (its file rendered first).</summary>
    public static async Task<MediaItem?> MakeAsync(Generator g, IProgress<double>? progress = null)
    {
        var f = await EnsureAsync(g, progress);
        var m = await MediaProbe.ProbeAsync(f);
        if (m == null) return null;
        m.Gen = g; m.DisplayName = g.Title;
        return m;
    }

    /// <summary>Runs on a fresh STA thread (WPF drawing off the UI thread).</summary>
    internal static Task Sta(Action a)
    {
        var tcs = new TaskCompletionSource();
        var th = new Thread(() => { try { a(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } }) { IsBackground = true };
        th.SetApartmentState(ApartmentState.STA); th.Start();
        return tcs.Task;
    }
    static void SavePng(BitmapSource b, string f)
    {
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(b));
        using var fs = File.Create(f); enc.Save(fs);
    }

    /// <summary>One frame of a drawn generator (matte, gradient, leader frame k).</summary>
    public static BitmapSource Draw(Generator g, int k)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var r = new Rect(0, 0, g.W, g.H);
            switch (g.Kind)
            {
                case GenKind.ColorMatte: dc.DrawRectangle(new SolidColorBrush(C(g.Color1)), null, r); break;
                case GenKind.Gradient:
                {
                    Brush b;
                    double rad = Math.Sqrt(g.W * g.W + g.H * g.H) / 2;                       // (a circle reaching the corners)
                    if (g.Radial) b = new RadialGradientBrush(C(g.Color1), C(g.Color2)) { MappingMode = BrushMappingMode.Absolute, Center = new Point(g.W / 2.0, g.H / 2.0), GradientOrigin = new Point(g.W / 2.0, g.H / 2.0), RadiusX = rad, RadiusY = rad };
                    else
                    {
                        double a = g.Angle * Math.PI / 180, dx = Math.Cos(a) / 2, dy = Math.Sin(a) / 2;
                        b = new LinearGradientBrush(C(g.Color1), C(g.Color2), new Point(0.5 - dx, 0.5 - dy), new Point(0.5 + dx, 0.5 + dy));
                    }
                    dc.DrawRectangle(b, null, r);
                    break;
                }
                case GenKind.Countdown: DrawLeader(dc, g, k); break;
            }
        }
        var rtb = new RenderTargetBitmap(g.W, g.H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv); rtb.Freeze();
        return rtb;
    }

    /// <summary>The counting leader: the number of whole seconds left down to 2, a sweep going round once a second, cross hairs and rings;
    /// at "2" one frame, then black for the last two seconds (the program starts when it ends).</summary>
    public static int LeaderNumber(Generator g, int k, out double frac)
    {
        double t = k / g.Fps; int n = (int)Math.Ceiling(g.Seconds - 1e-9);
        int sec = (int)Math.Floor(t + 1e-9);
        frac = t - sec;
        int num = n - sec;
        if (num > 2) return num;
        return num == 2 && k == (int)Math.Round((n - 2) * g.Fps) ? 2 : 0;    // (the "2" is a single frame)
    }
    static void DrawLeader(DrawingContext dc, Generator g, int k)
    {
        double W = g.W, H = g.H, s = H / 1080.0;
        int num = LeaderNumber(g, k, out double frac);
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, W, H));
        if (num == 0) return;
        var cx = W / 2; var cy = H / 2; double R = H * 0.42;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(150, 150, 150)), null, new Rect(0, 0, W, H));
        if (num > 2)
        {   // the sweep: dark behind the hand, clockwise from 12 o'clock
            double a = frac * 2 * Math.PI;
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(new Point(cx, cy), true, true);
                c.LineTo(new Point(cx, cy - R * 2), false, false);
                c.ArcTo(new Point(cx + Math.Sin(a) * R * 2, cy - Math.Cos(a) * R * 2), new Size(R * 2, R * 2), 0, a > Math.PI, SweepDirection.Clockwise, false, false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(90, 90, 90)), null, geo);
        }
        var pen = new Pen(Brushes.White, 6 * s); var thin = new Pen(Brushes.Black, 3 * s);
        dc.DrawLine(thin, new Point(0, cy), new Point(W, cy)); dc.DrawLine(thin, new Point(cx, 0), new Point(cx, H));
        dc.DrawEllipse(null, pen, new Point(cx, cy), R, R);
        dc.DrawEllipse(null, pen, new Point(cx, cy), R * 0.8, R * 0.8);
        var ft = new FormattedText(num.ToString(inv), inv, FlowDirection.LeftToRight, new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                                   H * 0.5, Brushes.Black, 1.0);
        dc.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
    }

    static void Bars(Generator g, string f)
    {
        double amp = Math.Pow(10, g.ToneDb / 20);
        string tone = $"{amp.ToString("0.000000", inv)}*sin(2*PI*{g.ToneHz.ToString(inv)}*t)";
        var args = $"-y -v error -f lavfi -i smptehdbars=s={g.W}x{g.H}:r={TimeUtil.Rational(g.Fps)}:d={g.Seconds.ToString(inv)} " +
                   $"-f lavfi -i \"aevalsrc={tone}|{tone}:s=48000:d={g.Seconds.ToString(inv)}\" " +
                   $"-c:v libx264 -qp 0 -preset ultrafast -pix_fmt yuv420p -g {Math.Max(1, (int)Math.Round(g.Fps))} -c:a pcm_s16le -shortest \"{f}\"";
        Run(args, null);
    }

    static async Task Countdown(Generator g, string f, IProgress<double>? progress)
    {
        int frames = (int)Math.Round(g.Seconds * g.Fps);
        var wav = f + ".wav";
        WriteBeeps(g, wav, frames);
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        var psi = new ProcessStartInfo(ff, $"-y -v error -f rawvideo -pix_fmt bgra -s {g.W}x{g.H} -r {TimeUtil.Rational(g.Fps)} -i - -i \"{wav}\" " +
                                           $"-c:v libx264 -qp 0 -preset ultrafast -pix_fmt yuv420p -g {Math.Max(1, (int)Math.Round(g.Fps))} -c:a pcm_s16le -shortest \"{f}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        await Sta(() =>
        {
            var buf = new byte[g.W * g.H * 4];
            var stdin = p.StandardInput.BaseStream;
            BitmapSource? last = null; int lastNum = -1; double lastFrac = -1;
            for (int k = 0; k < frames; k++)
            {
                int num = LeaderNumber(g, k, out double frac);
                if (last == null || num != lastNum || (num > 2 && frac != lastFrac)) { last = Draw(g, k); last.CopyPixels(buf, g.W * 4, 0); }
                lastNum = num; lastFrac = frac;
                stdin.Write(buf);
                if (k % 15 == 0) progress?.Report((double)k / frames);
            }
            stdin.Close();
        });
        await p.WaitForExitAsync();
        File.Delete(wav);
        if (p.ExitCode != 0 || !File.Exists(f)) throw new Exception("countdown: " + await err);
    }

    /// <summary>The leader's sound: a one-frame tone at the "2" (and at every number when <see cref="Generator.BeepAll"/>).</summary>
    static void WriteBeeps(Generator g, string wav, int frames)
    {
        int n = (int)Math.Round(frames / g.Fps * 48000);
        var pcm = new byte[n * 4];
        double amp = Math.Pow(10, g.ToneDb / 20) * 32767;
        int secs = (int)Math.Ceiling(g.Seconds - 1e-9);
        for (int num = secs; num >= 2; num--)
        {
            if (num != 2 && !g.BeepAll) continue;
            int k = (int)Math.Round((secs - num) * g.Fps);
            int a = (int)Math.Round(k / g.Fps * 48000), b = Math.Min(n, (int)Math.Round((k + 1) / g.Fps * 48000));
            for (int i = a; i < b; i++)
            {
                short v = (short)Math.Round(amp * Math.Sin(2 * Math.PI * g.ToneHz * (i - a) / 48000.0));
                BitConverter.TryWriteBytes(pcm.AsSpan(i * 4), v); BitConverter.TryWriteBytes(pcm.AsSpan(i * 4 + 2), v);
            }
        }
        using var w = new WavWriter(wav, 48000, 2);
        w.Write(pcm);
    }

    static void Run(string args, string? cwd)
    {
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = cwd ?? "" })!;
        var err = p.StandardError.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception("ffmpeg: " + err);
    }
}
