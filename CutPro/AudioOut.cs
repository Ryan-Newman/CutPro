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
    readonly AudioDsp.FxState fxState = new(), shuttleFx = new();   // clip audio effects: their state from block to block (mixer thread only; shuttle / scrub keep their own)
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
    /// <summary>Playback rate (J / L shuttle: ±1, 2, 4, 8 — negative = backwards). The timeline span a buffer covers is mixed at its own speed
    /// and resampled to the buffer (the pitch moves with the speed, like a tape), backwards for reverse.</summary>
    double rate = 1;
    public double PlayRate => rate;
    public const int MaxShuttle = 8;
    readonly float[] bigMix = new float[(BufFrames * MaxShuttle + 4) * 2];
    readonly float[][] bigBus = Mixer.Bus(BufFrames * MaxShuttle + 4);
    readonly short[] bigTmp = new short[(BufFrames * MaxShuttle + 4) * 2];
    // audio scrubbing: short grains of the sound at the playhead while it is dragged (Premiere's audio scrub)
    volatile bool scrubbing;
    double scrubT, grainT = double.NaN; long scrubAt;
    float scrubGain;
    public bool Scrubbing => scrubbing && running;
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

    /// <summary>Starts playing the timeline from time t at a rate (1 = normal; restarts cleanly if already running).</summary>
    public void Start(double t, double rate = 1)
    {
        Stop();
        if (!Ok) return;
        startT = t; mixedFrames = 0; this.rate = Math.Abs(rate) < 1e-9 ? 1 : Math.Clamp(rate, -MaxShuttle, MaxShuttle); scrubbing = false;
        Array.Clear(queued);
        Array.Fill(meterT, double.PositiveInfinity); meterW = 0;
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "AudioOut" };
        thread.Start();
    }

    /// <summary>Scrubbing: plays a short grain of the sound at t whenever t moves (call it each frame while the playhead is dragged); silence when it
    /// stops moving. Starts the output if needed; <see cref="Stop"/> ends it.</summary>
    public void Scrub(double t)
    {
        if (!Ok) return;
        if (!running || !scrubbing)
        {
            Stop();
            startT = t; mixedFrames = 0; rate = 1; scrubbing = true; grainT = double.NaN; scrubGain = 0;
            Array.Clear(queued);
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "AudioOut" };
            thread.Start();
        }
        if (Math.Abs(t - scrubT) > 1e-6) { scrubT = t; scrubAt = Environment.TickCount64; }
    }

    public void Stop()
    {
        running = false; scrubbing = false;
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
            return startT + played / Rate * rate;
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
        if (!running || rate != 1 || scrubbing) return false;                // (meters: normal playback only)
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
        if (scrubbing) { FillScrub(); Out(i); return; }
        if (rate != 1) { FillShuttle(); Out(i); return; }
        double T0 = startT + mixedFrames / (double)Rate;
        int slot = meterW % Ring;
        Mixer.Mix(Sources, Master, T0, BufFrames, mix, bus, tmp, meterPk[slot], fxState);
        meterT[slot] = T0; meterW++;
        Out(i);
    }
    void Out(int i)
    {
        for (int k = 0; k < mix.Length; k++) outBuf[k] = (short)Math.Clamp(mix[k], short.MinValue, short.MaxValue);
        Marshal.Copy(outBuf, 0, data[i], outBuf.Length);
        mixedFrames += BufFrames;
    }

    /// <summary>A buffer at a shuttle rate: the span it covers mixed at normal speed (effects with their own state: backwards they warm up every buffer
    /// backwards), read at the rate with linear interpolation — backwards for reverse; a little quieter past 2×.</summary>
    void FillShuttle()
    {
        double T0 = startT + mixedFrames / (double)Rate * rate, span = BufFrames * rate / Rate;
        int m = (int)Math.Ceiling(BufFrames * Math.Abs(rate)) + 2;
        double from = rate > 0 ? T0 : T0 + span;
        Mixer.Mix(Sources, Master, from, m, bigMix, bigBus, bigTmp, null, shuttleFx);
        float g = Math.Abs(rate) > 2 ? 0.5f : 1;
        for (int k = 0; k < BufFrames; k++)
        {
            double x = (T0 + k * rate / Rate - from) * Rate;
            int j = Math.Clamp((int)Math.Floor(x), 0, m - 2); float f = (float)Math.Clamp(x - j, 0, 1);
            mix[k * 2] = g * (bigMix[j * 2] + (bigMix[j * 2 + 2] - bigMix[j * 2]) * f);
            mix[k * 2 + 1] = g * (bigMix[j * 2 + 1] + (bigMix[j * 2 + 3] - bigMix[j * 2 + 1]) * f);
        }
    }

    /// <summary>A scrub buffer: the sound from the playhead on (continuing the last grain while the playhead stays near it), faded in when it
    /// jumps and out when the playhead has rested for 80 ms.</summary>
    void FillScrub()
    {
        bool moving = Environment.TickCount64 - scrubAt < 80;
        double t = scrubT;
        if (double.IsNaN(grainT) || Math.Abs(grainT - t) > 0.12) { grainT = t; scrubGain = 0; }   // (a jump: start there, faded in)
        Mixer.Mix(Sources, Master, grainT, BufFrames, mix, bus, tmp, null, shuttleFx);
        float target = moving ? 1 : 0, g0 = scrubGain;
        for (int k = 0; k < BufFrames; k++)
        {
            float g = g0 + (target - g0) * (k + 1) / BufFrames;
            mix[k * 2] *= g; mix[k * 2 + 1] *= g;
        }
        scrubGain = target;
        grainT += BufFrames / (double)Rate;
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
