using System.Runtime.InteropServices;

namespace CutPro;

/// <summary>
/// Timeline audio output and the playback master clock. A background thread mixes the conformed PCM of every
/// sounding clip (volume + fades, same math as export) into small waveOut buffers. <see cref="Clock"/> is the
/// timeline time of the sample the sound card is playing right now, so video follows what you hear.
/// </summary>
public sealed class AudioOut : IDisposable
{
    /// <summary>Gain: animated volume (multiplier per GainStep seconds from Start; see Anim.GainTable) — null = Volume.</summary>
    /// <summary>XIn / XOut: audio-transition crossfade windows (start, end, constant power) on the clip's head / tail.</summary>
    /// <summary>Track / GL / GR: the mixer channel (meters) and its fader × pan gains per side (see <see cref="Mixer"/>).</summary>
    public readonly record struct Src(string Path, double Start, double End, double In, double Volume, double FadeIn, double FadeOut, float[]? Gain = null,
                                      (double t0, double t1, bool power)? XIn = null, (double t0, double t1, bool power)? XOut = null,
                                      int Track = 0, float GL = 1, float GR = 1, IReadOnlyList<AudioEffect>? Fx = null, string? FxKey = null,   // Fx: clip audio effects (AudioDsp), FxKey = clip + settings
                                      float[]? AL = null, float[]? AR = null,   // AL / AR: track automation, L / R gains per Mixer.AutoStep block of the timeline (instead of GL / GR)
                                      AudioDsp.TrackBus? Bus = null,            // Bus: its track has effects (summed before the fader, which the bus applies)
                                      Mixer.SubBus? Sub = null);                // Sub: its track goes to this submix instead of the master
    public const double GainStep = 0.005;
    readonly AudioDsp.FxState fxState = new();          // clip audio effects: their state from block to block (mixer thread only)
    /// <summary>Replaced (not mutated) by the UI thread whenever the timeline changes.</summary>
    public volatile Src[] Sources = Array.Empty<Src>();
    /// <summary>Master fader (linear).</summary>
    public volatile MasterMix Master = new(1);

    const int Rate = AudioCache.Rate, BufFrames = 960, NBuf = 6;   // 6 x 20 ms queued
    readonly IntPtr hwo;
    readonly IntPtr[] hdr = new IntPtr[NBuf], data = new IntPtr[NBuf];
    readonly bool[] queued = new bool[NBuf];
    readonly float[] mix = new float[BufFrames * 2];
    readonly float[][] bus = Mixer.Bus(BufFrames);
    readonly short[] tmp = new short[BufFrames * 2], outBuf = new short[BufFrames * 2];
    // meters: peaks of every mixed buffer with its timeline time; the UI reads the one being heard (the queue runs ~100 ms ahead)
    const int Ring = 64;
    readonly float[][] meterPk = Enumerable.Range(0, Ring).Select(_ => new float[Mixer.Slots]).ToArray();
    readonly double[] meterT = new double[Ring];
    volatile int meterW;
    static readonly int HdrSize = Marshal.SizeOf<WaveHdr>(), FlagsOff = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwFlags));
    Thread? thread;
    volatile bool running;
    double startT;
    long mixedFrames;
    public bool Ok { get; }
    public bool Running => running;

    public AudioOut()
    {
        var fmt = new WaveFormat { wFormatTag = 1, nChannels = 2, nSamplesPerSec = Rate, nAvgBytesPerSec = Rate * 4, nBlockAlign = 4, wBitsPerSample = 16 };
        Ok = waveOutOpen(out hwo, new IntPtr(-1), ref fmt, IntPtr.Zero, IntPtr.Zero, 0) == 0;
        if (!Ok) return;
        for (int i = 0; i < NBuf; i++)
        {
            data[i] = Marshal.AllocHGlobal(BufFrames * 4);
            hdr[i] = Marshal.AllocHGlobal(HdrSize);
            Marshal.StructureToPtr(new WaveHdr { lpData = data[i], dwBufferLength = BufFrames * 4 }, hdr[i], false);
            waveOutPrepareHeader(hwo, hdr[i], HdrSize);
        }
    }

    /// <summary>Starts playing the timeline from time t (restarts cleanly if already running).</summary>
    public void Start(double t)
    {
        Stop();
        if (!Ok) return;
        startT = t; mixedFrames = 0;
        Array.Clear(queued);
        Array.Fill(meterT, double.PositiveInfinity); meterW = 0;
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "AudioOut" };
        thread.Start();
    }

    public void Stop()
    {
        running = false;
        thread?.Join(); thread = null;
        if (Ok) waveOutReset(hwo);
    }

    /// <summary>Timeline time of the sample currently coming out of the speakers.</summary>
    public double Clock
    {
        get
        {
            if (!Ok) return startT;
            var mt = new MmTime { wType = TIME_SAMPLES };
            waveOutGetPosition(hwo, ref mt, Marshal.SizeOf<MmTime>());
            double played = mt.wType == TIME_BYTES ? mt.u / 4.0 : mt.u;
            return startT + played / Rate;
        }
    }

    void Loop()
    {
        while (running)
        {
            for (int i = 0; i < NBuf && running; i++)
            {
                int flags = Marshal.ReadInt32(hdr[i], FlagsOff);
                if (queued[i] && (flags & WHDR_DONE) == 0) continue;
                Fill(i);
                Marshal.WriteInt32(hdr[i], FlagsOff, flags & ~WHDR_DONE);
                waveOutWrite(hwo, hdr[i], HdrSize);
                queued[i] = true;
            }
            Thread.Sleep(3);
        }
    }

    /// <summary>Peak levels of the sound being heard now (<see cref="Mixer.Slots"/>: track L/R pairs, then master; 1 = 0 dBFS). False when stopped.</summary>
    public bool Levels(float[] dst)
    {
        if (!running) return false;
        double now = Clock; int w = meterW;
        for (int i = 1; i < Ring - NBuf; i++)
        {
            int j = ((w - i) % Ring + Ring) % Ring;
            if (meterT[j] <= now) { Array.Copy(meterPk[j], dst, Math.Min(dst.Length, Mixer.Slots)); return true; }
        }
        return false;
    }

    void Fill(int i)
    {
        double T0 = startT + mixedFrames / (double)Rate;
        int slot = meterW % Ring;
        Mixer.Mix(Sources, Master, T0, BufFrames, mix, bus, tmp, meterPk[slot], fxState);
        meterT[slot] = T0; meterW++;
        for (int k = 0; k < mix.Length; k++) outBuf[k] = (short)Math.Clamp(mix[k], short.MinValue, short.MaxValue);
        Marshal.Copy(outBuf, 0, data[i], outBuf.Length);
        mixedFrames += BufFrames;
    }

    public void Dispose()
    {
        Stop();
        if (!Ok) return;
        for (int i = 0; i < NBuf; i++) { waveOutUnprepareHeader(hwo, hdr[i], HdrSize); Marshal.FreeHGlobal(hdr[i]); Marshal.FreeHGlobal(data[i]); }
        waveOutClose(hwo);
    }

    // ---- winmm ----
    const int WHDR_DONE = 1, TIME_BYTES = 4, TIME_SAMPLES = 2;
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormat { public short wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public short nBlockAlign, wBitsPerSample, cbSize; }
    [StructLayout(LayoutKind.Sequential)]
    struct WaveHdr { public IntPtr lpData; public int dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public int dwFlags, dwLoops; public IntPtr lpNext, reserved; }
    [StructLayout(LayoutKind.Explicit, Size = 12)]
    struct MmTime { [FieldOffset(0)] public int wType; [FieldOffset(4)] public uint u; }
    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, IntPtr device, ref WaveFormat fmt, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr hwo, ref MmTime mt, int size);
}
