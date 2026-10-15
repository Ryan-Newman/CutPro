# CutPro

A Premiere Pro–style video editor for Windows, built with WPF and .NET 9. Preview and export use ffmpeg, and every effect is checked against the export by automated preview-vs-export tests.

![CutPro in the Nature theme](ThemeShots/Nature.png)

## Features

- **Editing:** 3 video + 2 audio tracks, select / razor tools, ripple trim, lift / extract, linking, snapping, multiple and nested sequences, a source monitor with Insert / Overwrite and Match Frame.
- **Playback:** an audio-clock player with hardware decoding (chosen per file by measurement) and a scrub cache.
- **Picture:** motion with keyframes and formulas, blend modes, crop / flip, chroma and luma keys, track mattes, corner pin, lens distortion, adjustment layers, Lumetri-style colour (wheels, curves, .cube LUTs) with scopes, cartoon / anime looks, transitions, speed / reverse / time remapping, stabilization.
- **Titles and captions:** styled titles with themes and text masks, and captions from speech (Whisper) with SRT / VTT.
- **AI object masks:** click to select with MobileSAM, then track; blur, pixelate, colour pop and more.
- **Audio:** track mixer with automation, submixes, clip and track effects (EQ, compressor, gate, de-esser, reverb), pitch shift, BS.1770 loudness normalization.
- **Export:** platform presets (YouTube, Shorts, TikTok, Instagram, …), H.264 / HEVC / AV1 / VP9 / ProRes (including 4444), GPU encoders, a full-colour 4:4:4 composite.
- **Publishing:** uploads to the major platforms with your own developer app credentials; caption / hashtag suggestions; thumbnail and intro studios.
- **Interface:** dockable panels and workspaces, a command palette, and flat and material themes.

## Requirements

- Windows 10 / 11
- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- ffmpeg + ffprobe 7 or newer on the PATH (for example `winget install Gyan.FFmpeg`)

The AI models (MobileSAM, Whisper base, CLIP B/32) are not in the repository. CutPro offers to download them the first time a feature needs one.

## Build and run

```bash
dotnet run -c Release --project CutPro
```

Tests (build, self test, export, formats; `-play` adds the playback measurements):

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File ./test.ps1 -play
```

`Agent.md` describes the architecture, the design rules and every test command; `ROADMAP.md` lists what's planned and `PROGRESS.md` what was done when.
