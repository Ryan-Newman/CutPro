using System.Windows;

namespace CutPro;

/// <summary>J / K / L shuttle (partial MainWindow): L plays forward, L again 2×, 4×, 8×; J the same backwards; K stops. Normal speed plays with
/// the decoders and the audio clock as always; other rates run the audio clock at the rate (<see cref="AudioOut.Start"/>: the sound sped up or
/// backwards, like a tape) and the picture through the scrub machinery (cached frames while the time moves, the exact frame when it stops).
/// Also: Audio Time Units (sample-accurate audio edits).</summary>
public partial class MainWindow
{
    double playRate = 1;
    void ProxyBtn_Click(object s, RoutedEventArgs e) => SetProxies(ProxyBtn.IsChecked == true);

    void Shuttle(int dir)
    {
        if (ed.Clips.Count == 0) return;
        double next = !playing || Math.Sign(playRate) != dir ? dir : Math.Min(AudioOut.MaxShuttle, Math.Abs(playRate) * 2) * dir;
        if (next == 1 && (!playing || playRate != 1)) { if (playing) Pause(); Play(); return; }
        if (next == 1) return;                                              // (already playing forward at normal speed: L again → 2×)
        if (dir < 0 && ed.Time <= 1e-3) return;
        if (dir > 0 && ed.Time >= ed.End - 0.01) ed.Time = 0;
        SrcPause();
        playRate = next;
        playing = true; clockRunning = true; playWall = Environment.TickCount64; t0 = ed.Time; sw.Restart();
        audio.Start(ed.Time, next);
        UpdatePlayArt(); ShuttleBadge();
    }

    /// <summary>The rate while shuttling, next to the timecode ("2×", "◀ 4×").</summary>
    void ShuttleBadge()
    {
        if (playing && playRate != 1) SetTask((playRate < 0 ? "◀ " : "▶ ") + $"{Math.Abs(playRate):0.#}× — K stops, J / L change speed", 0);
        else if (lastShuttleBadge) SetTask("", 1);
        lastShuttleBadge = playing && playRate != 1;
    }
    bool lastShuttleBadge;
    /// <summary>The mesh warp's points on the Program monitor (see <see cref="MeshOverlay"/>).</summary>
    MeshOverlay? meshOverlay;

    /// <summary>Timeline ▸ Audio Time Units: audio clips without a picture snap to samples instead of frames (trims, moves, razor cuts), and the
    /// timeline zooms in further.</summary>
    void ToggleAudioUnits()
    {
        S.AudioUnits = !S.AudioUnits; Editor.AudioUnits = S.AudioUnits; AppSettings.Save();
        ZoomSlider.Maximum = Math.Log2(TimelineView.MaxPps);
        if (Tl.Pps > TimelineView.MaxPps) Tl.Pps = TimelineView.MaxPps;
        Tl.InvalidateVisual();
        SetTask(S.AudioUnits ? "Audio time units: audio-only clips edit at single samples (zoom in with = or Ctrl+wheel)" : "Audio time units off: edits snap to frames", 3000);
    }
}
