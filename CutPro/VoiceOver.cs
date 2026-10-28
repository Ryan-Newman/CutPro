using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CutPro;

/// <summary>A 16-bit PCM WAV file written as it grows (the header is finished on Close).</summary>
public sealed class WavWriter : IDisposable
{
    readonly FileStream fs; readonly int rate, ch; long bytes;
    public WavWriter(string path, int rate, int channels)
    {
        this.rate = rate; ch = channels;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        fs.Write(new byte[44]);
    }
    public double Seconds => bytes / 2.0 / ch / rate;
    public void Write(ReadOnlySpan<byte> pcm) { fs.Write(pcm); bytes += pcm.Length; }
    public void Dispose()
    {
        fs.Position = 0;
        var w = new BinaryWriter(fs, System.Text.Encoding.ASCII, true);
        w.Write("RIFF"u8); w.Write((int)(36 + bytes)); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)ch); w.Write(rate); w.Write(rate * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write((int)bytes);
        w.Flush(); fs.Dispose();
    }
}

/// <summary>Records a microphone (WinMM waveIn, 48 kHz mono 16-bit) into a WAV. <see cref="Started"/> = the high-resolution clock when capture
/// began (the first sample), so the caller knows which timeline moment it belongs to.</summary>
public sealed class VoiceRecorder : IDisposable
{
    public static int Devices => waveInGetNumDevs();
    public static List<string> DeviceNames()
    {
        var res = new List<string>();
        for (int i = 0; i < Devices; i++) { var caps = new WaveInCaps(); res.Add(waveInGetDevCapsW((IntPtr)i, ref caps, Marshal.SizeOf<WaveInCaps>()) == 0 ? caps.szPname : $"Input {i + 1}"); }
        return res;
    }

    const int Rate = 48000, NBuf = 8, BufBytes = Rate / 20 * 2;            // 50 ms buffers
    readonly WavWriter wav;
    IntPtr hwi; readonly IntPtr[] hdr = new IntPtr[NBuf], data = new IntPtr[NBuf];
    Thread? pump; volatile bool stop;
    public long Started { get; private set; }
    public double Seconds => wav.Seconds;
    /// <summary>Peak of the last buffer (0 … 1) for a level meter.</summary>
    public volatile float Level;

    public VoiceRecorder(int device, string path)
    {
        var fmt = new WaveFormat { wFormatTag = 1, nChannels = 1, nSamplesPerSec = Rate, nAvgBytesPerSec = Rate * 2, nBlockAlign = 2, wBitsPerSample = 16 };
        int r = waveInOpen(out hwi, (IntPtr)device, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (r != 0) throw new Exception($"The microphone could not be opened (error {r}). Check Windows' privacy settings: Microphone access for desktop apps.");
        wav = new WavWriter(path, Rate, 1);
        for (int i = 0; i < NBuf; i++)
        {
            data[i] = Marshal.AllocHGlobal(BufBytes);
            hdr[i] = Marshal.AllocHGlobal(HdrSize);
            Marshal.StructureToPtr(new WaveHdr { lpData = data[i], dwBufferLength = BufBytes }, hdr[i], false);
            waveInPrepareHeader(hwi, hdr[i], HdrSize);
            waveInAddBuffer(hwi, hdr[i], HdrSize);
        }
    }
    static int HdrSize => Marshal.SizeOf<WaveHdr>();

    public void Start()
    {
        waveInStart(hwi);
        Started = System.Diagnostics.Stopwatch.GetTimestamp();
        pump = new Thread(Pump) { IsBackground = true, Name = "voice-over" };
        pump.Start();
    }

    void Pump()
    {
        var buf = new byte[BufBytes];
        int next = 0;
        while (true)
        {
            var h = Marshal.PtrToStructure<WaveHdr>(hdr[next]);
            if ((h.dwFlags & WHDR_DONE) == 0) { if (stop) break; Thread.Sleep(5); continue; }
            int n = h.dwBytesRecorded;
            Marshal.Copy(data[next], buf, 0, n);
            lock (wav) wav.Write(buf.AsSpan(0, n));
            int peak = 0; for (int i = 0; i + 1 < n; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(buf, i)));
            Level = peak / 32768f;
            if (!stop) { h.dwFlags &= ~WHDR_DONE; Marshal.StructureToPtr(h, hdr[next], false); waveInAddBuffer(hwi, hdr[next], HdrSize); }
            next = (next + 1) % NBuf;
        }
    }

    /// <summary>Stops, writes what was captured and closes the file.</summary>
    public void Dispose()
    {
        if (hwi == IntPtr.Zero) return;
        stop = true;
        waveInStop(hwi);
        waveInReset(hwi);                                                     // (returns the queued buffers as done)
        pump?.Join(2000);
        for (int i = 0; i < NBuf; i++) { waveInUnprepareHeader(hwi, hdr[i], HdrSize); Marshal.FreeHGlobal(hdr[i]); Marshal.FreeHGlobal(data[i]); }
        waveInClose(hwi); hwi = IntPtr.Zero;
        lock (wav) wav.Dispose();
    }

    // ---- winmm ----
    const int WHDR_DONE = 1;
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormat { public short wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public short nBlockAlign, wBitsPerSample, cbSize; }
    [StructLayout(LayoutKind.Sequential)]
    struct WaveHdr { public IntPtr lpData; public int dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public int dwFlags, dwLoops; public IntPtr lpNext, reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WaveInCaps { public short wMid, wPid; public int vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public int dwFormats; public short wChannels, wReserved1; }
    [DllImport("winmm.dll")] static extern int waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int waveInGetDevCapsW(IntPtr id, ref WaveInCaps caps, int size);
    [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr hwi, IntPtr device, ref WaveFormat fmt, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr hwi, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr hwi, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInStart(IntPtr hwi);
    [DllImport("winmm.dll")] static extern int waveInStop(IntPtr hwi);
    [DllImport("winmm.dll")] static extern int waveInReset(IntPtr hwi);
    [DllImport("winmm.dll")] static extern int waveInClose(IntPtr hwi);
}

/// <summary>Voice-over (Premiere's track record button): the sequence plays from the playhead while the microphone records; stopping
/// (Space / Esc) puts the recording on the chosen audio track where it was spoken (overwrite) and into the Project panel's "Voice-over" bin.</summary>
public partial class MainWindow
{
    VoiceRecorder? voRec;
    string? voFile;
    int voTrack;
    double voT0;
    bool voWaiting;
    readonly DispatcherTimer voTimer = new() { Interval = TimeSpan.FromMilliseconds(10) };

    /// <summary>The folder recordings go to: next to the project, else Documents\CutPro\Voice-over.</summary>
    string VoiceDir() => projectPath != null ? Path.Combine(Path.GetDirectoryName(projectPath)!, "Voice-over")
                                             : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CutPro", "Voice-over");

    void VoiceOverDialog(int? track = null)
    {
        if (voRec != null || voWaiting) { StopVoiceOver(); return; }
        var names = VoiceRecorder.DeviceNames();
        if (names.Count == 0) { Alert.Show(this, "No microphone was found. Connect one (or allow microphone access in Windows' privacy settings) and try again.", "Record Voice-over", AlertKind.Warning); return; }
        var rows = Tracks.AudioRows.Where(r => !ed.Locked(r)).ToList();
        if (rows.Count == 0) { Alert.Show(this, "Every audio track is locked.", "Record Voice-over"); return; }
        int pick = track is int t0 && rows.Contains(t0) ? t0 : rows.FirstOrDefault(r => !ed.Clips.Any(c => c.Track == r && c.End > ed.Time), rows[^1]);
        var dev = new ComboBox { ItemsSource = names, SelectedIndex = 0, Margin = new Thickness(0, 10, 0, 0) };
        var tr = new ComboBox { ItemsSource = rows.Select(r => Tracks.Name(r)).ToList(), SelectedIndex = rows.IndexOf(pick), Margin = new Thickness(0, 6, 0, 0) };
        var p = new StackPanel(); p.Children.Add(new TextBlock { Text = "Microphone" }); p.Children.Add(dev); p.Children.Add(new TextBlock { Text = "Record onto", Margin = new Thickness(0, 8, 0, 0) }); p.Children.Add(tr);
        if (Alert.Show(this, "The sequence plays from the playhead while you speak. Press Space or Esc to stop — the recording lands on the track where you spoke it.",
                       "Record Voice-over", AlertKind.Question, new[] { "Record", "Cancel" }, p) != 0) return;
        StartVoiceOver(Math.Max(0, dev.SelectedIndex), rows[Math.Max(0, tr.SelectedIndex)]);
    }

    void StartVoiceOver(int device, int track)
    {
        Pause();
        voTrack = track;
        voFile = Path.Combine(VoiceDir(), $"Voice-over {DateTime.Now:yyyy-MM-dd HH-mm-ss}.wav");
        try { voRec = new VoiceRecorder(device, voFile); }
        catch (Exception ex) { voRec = null; Alert.Show(this, ex.Message, "Record Voice-over", AlertKind.Error); return; }
        voTimer.Tick -= VoiceTick; voTimer.Tick += VoiceTick;
        if (ed.Clips.Count == 0) { voT0 = ed.Time; voRec.Start(); }               // (nothing to play: just record)
        else { voWaiting = true; Play(); }                                         // (recording starts once the sound is heard)
        voTimer.Start();
    }

    void VoiceTick(object? s, EventArgs e)
    {
        if (voRec == null) { voTimer.Stop(); return; }
        if (voWaiting)
        {
            if (!playing) { voWaiting = false; voT0 = ed.Time; voRec.Start(); }
            else if (clockRunning) { voRec.Start(); voT0 = audio.Clock; voWaiting = false; }        // (the sound heard at the first recorded sample)
            return;
        }
        SetTask($"● Recording {TimeUtil.Format(voRec.Seconds, false)}  {new string('▮', (int)Math.Round(voRec.Level * 12))}{new string('▯', 12 - (int)Math.Round(voRec.Level * 12))}  — Space / Esc stops", 0);
    }

    /// <summary>Space / Esc while recording.</summary>
    bool VoiceKey(Key k)
    {
        if (voRec == null || k is not (Key.Space or Key.Escape)) return false;
        StopVoiceOver();
        return true;
    }

    async void StopVoiceOver()
    {
        if (voRec == null) return;
        voTimer.Stop();
        var rec = voRec; voRec = null; bool never = voWaiting; voWaiting = false;
        double secs = rec.Seconds;
        rec.Dispose();
        Pause();
        if (never || secs < 0.2 || voFile == null) { try { if (voFile != null) File.Delete(voFile); } catch { } SetTask("Nothing recorded", 2000); return; }
        var m = await MediaProbe.ProbeAsync(voFile);
        if (m == null) { Alert.Show(this, MediaProbe.LastError ?? "The recording could not be read.", "Record Voice-over", AlertKind.Error); return; }
        if (!ed.Bins.Contains("Voice-over")) ed.Bins.Add("Voice-over");
        m.Bin = "Voice-over";
        ed.Media.Add(m);
        double start = Math.Ceiling(voT0 * TimeUtil.Fps - 1e-6) / TimeUtil.Fps;   // (on a frame; the part before it is skipped, so the words stay in sync)
        ed.DropMedia(m, voTrack, start, false, true, false, start - voT0);
        SetTask($"Voice-over {secs:0.0} s on {Tracks.Name(voTrack)} — {Path.GetFileName(voFile)}", 5000);
    }
}
