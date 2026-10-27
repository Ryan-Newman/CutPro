# CutPro roadmap — features the major editors have that CutPro doesn't (yet)

Compiled from Adobe Premiere Pro, DaVinci Resolve, Final Cut Pro, Avid Media Composer, VEGAS Pro, Kdenlive / Shotcut
and CapCut / Filmora (2026 versions). **Editors:** P Premiere · R Resolve · F Final Cut · A Avid · V VEGAS · K Kdenlive/Shotcut · C CapCut/Filmora.
**Effort** for CutPro's architecture (WPF preview + ffmpeg export, see Agent.md): **S** ≤ 1 session · **M** 2–3 sessions · **L** larger / needs a new subsystem.
"Partial" = CutPro has part of it.

## What CutPro has today (for reference)
Import through ffmpeg (MKV/MP4/MOV, H.264/HEVC 4K 10-bit/VP9, rotated phone video, FLAC/Opus/WAV, images) · project bin with thumbnails, offline
detection · up to 32 video + 32 audio tracks per sequence (names, locks, heights, hide/mute/solo) · **audio waveforms, Audio Track Mixer (faders, pan, mute, solo, master, peak meters), loudness (BS.1770 meter, normalize on export, match clips)** · select + razor tools, Add Edit, delete / ripple delete, lift / extract, in/out marks, ripple
trim (Q/W), link/unlink, snapping, marquee, gap selection, undo/redo · audio-clock playback with hardware decoding and a scrub cache · motion
(scale, position, rotation), opacity, Gaussian blur, fade in/out, volume, **speed / reverse / frame hold / time remapping**, **colour correction (Lumetri-style basic, looks + .cube LUTs, wheels, curves, vignette) + Lumetri Scopes**, **cartoon / cel / anime looks**, **transitions** (dissolve, dips, wipes, push, iris; audio crossfades) · **keyframes + formulas** on all of those (dope sheet, graph editor,
easing, timeline diamonds) · titles (fonts, outline, shadow, glow, box, 16 styles, 7 themes, text masks) · **object masks** (AI click-to-select
with MobileSAM, ellipse/rectangle/freehand, tracking, keyframe corrections; blur/pixelate/redact/tint/focus/spotlight/colour pop/cut-out) ·
**sequence settings** (any size 16–16384 px, 23.976–60 fps, 16:9 / Shorts 9:16 / 1:1 / 4:5 toggle, Fill + pan / Fit + blur / Fit layouts, auto
reframe) · **export settings** (18 platform presets, H.264/HEVC/AV1/VP9/ProRes, NVENC/QSV/AMF by test, CRF or bitrate, In/Out range, WAV/MP3) ·
**publishing** (YouTube, TikTok, Instagram, Facebook, X, LinkedIn, Pinterest, Vimeo uploads + Threads share; auto-upload after export;
captions/hashtags suggested from speech (Whisper) + scenes (CLIP) + titles + Bible references) · **Thumbnail Studio** (YouTube / Shorts covers, AI subject cut-out) · **Intro & Hook** (best opening moments, 5 intro templates) · VS Code-style UI with 3 themes, command palette, glass dialogs.

## Suggested order (most value for the effort first)
1. **Distort**: lens distortion done (keyframable, preview shader = lenscorrection); mesh warp / twirl / wave / spherize later (geq: ffmpeg's remap is nearest-only). (Keyframed corner pin with monitor handles, adjustment layers, crop, flip, blend modes, chroma / luma key, track mattes, anchor point, captions from speech + SRT / VTT, multiple sequences and nested sequences are done.)
2. **Markers (→ YouTube chapters), slip/slide/roll/rate-stretch tools, sync-lock / track targeting**. (Source monitor with Insert / Overwrite and Match Frame, and any number of tracks — names, locks, heights — are done.)
3. **Channel mapping / 5.1**, VST3 hosting (clip EQ, compressor, gate, de-esser, hum removal, reverb, pitch shift, track effects, submixes, volume rubber bands, track and master automation are done).

## 1. Project, media and sequences
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| Multiple sequences in a project; nested sequences / compound clips | P R F A V K | done: sequences with tabs, per-sequence format / mixer / playhead, Nest, drag a sequence in; nested clips rendered in the background (FFV1 with alpha + mix) and read by preview and export alike (`Nest`) | L |
| Source monitor with in/out marking; 3-point / 4-point editing | P R A V K | done: Source panel (own Preview + AudioOut), I / O marks per clip, Insert (,) / Overwrite (.) at the playhead with V / A track targets, video-only / audio-only drags, Match Frame (F); not yet 4-point fit-to-fill | M |
| Bins / folders, labels (colour), ratings, keywords, search & filters, list view with metadata | all | partial: flat bin with name/duration/size | M |
| Proxy workflow (generate low-res proxies, toggle, relink) | P R F A V K | partial: scrub cache is a hidden proxy; add user proxies for editing 4K/8K and RAW | M |
| Relink / replace media, consolidate / archive project (collect files) | all | offline detection exists; add relink dialog and "collect" export | S |
| Auto-save with versions, crash recovery | all | crash.log exists; add timed saves + recovery prompt | S |
| Interchange: XML (FCP7/FCPXML), EDL, AAF, OpenTimelineIO | P R F A V K | OTIO/EDL export first (S), import later | M |
| Scene edit detection (cut a clip at its shot changes) | P R C | ffmpeg `select='gt(scene,x)'` → Add Edits | S |
| Multicam (sync angles by audio/timecode, live angle switching) | P R F A V | needs audio-sync + a multi-view monitor | L |
| Sync / merge clips by audio waveform (external recorder) | P R F A | cross-correlate the conformed PCM (AudioCache) | M |
| Timecode, reel names, metadata display | P R F A | | S |
| Screen / webcam / voice-over recording | P F V C | voice-over first (waveIn), screen capture later | M |
| Stock media / templates browser | C (P) | | L |

## 2. Timeline editing
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| Add / remove tracks, track names, heights, lock, solo, target, sync-lock | all | done: 1 … 32 video + 1 … 32 audio tracks per sequence (add above / below / several, delete with clips, delete empty), names, locks, heights, vertical scrolling, hide / mute / solo; not yet sync-lock toggles or track targeting | M |
| Overwrite vs insert edits, replace edit, fit-to-fill | P R F A V K | Premiere's drag = overwrite, Ctrl-drag = insert | M |
| Rolling, slip, slide, rate-stretch tools | P R F A V K | ripple trim exists (Q/W); these reuse TryTrim | M |
| Markers (sequence + clip), marker comments, chapter markers | all | also export chapters to MP4 metadata | S |
| Clip speed / duration, reverse, frame blending / optical flow | all | done: Speed/Duration (Ctrl+R, linked speed / duration, ripple), reverse, pitch-kept or tape-style sound; rendered frame-exact (`Remap`). Next: frame blending / optical flow for slow motion (`minterpolate`), Rate Stretch tool (R) | M |
| Time remapping / speed ramps (keyframed speed) | P R F V C | done: Speed keyframes (Effect Controls → Time; keyed by source time, eased like any key), picture only like Premiere | M |
| Freeze frame / frame hold, still from frame | all | done: Add Frame Hold, Insert Frame Hold Segment, Freeze frame toggle | S |
| Audio waveforms on clips | all | done: rectified, dB scale, from the AudioCache PCM (`Peaks`: 256-frame max + ×4 levels, `.peaks` file per conformed file) | S |
| Volume / opacity rubber bands on the clip (keyframes drawn as a line) | P R F V K | done (volume): the clip volume band on audio clips — drag the line / keys, Ctrl+click adds, Alt+click removes; track volume / pan automation bands too; not yet opacity bands on video clips | S |
| Magnetic timeline, connected clips, auditions | F | different editing model — optional | L |
| Clip groups, copy / paste attributes (effects, motion, keyframes) | all | keyframe copy/paste exists in the Keyframes panel | S |
| Match frame, reveal in project, go to next/previous edit or gap | P R A V | | S |
| Undo history panel | P R V | Editor history is a stack of snapshots already | S |
| Keyboard shortcut editor | all | shortcuts are hard-coded (Premiere defaults) | S |
| JKL shuttle at 2×/4×/8×, reverse playback | all | partial: J/K/L; reverse decode needs chunked decoding (like backward tracking) | M |
| Timeline zoom to sequence / to selection, scroll with playback modes | all | partial | S |

## 3. Effects, compositing and motion graphics
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| More transitions: zoom, slide (one shot over the other), clock wipe, morph cut, film dissolve, 3D (cube spin, page peel), transition presets | all | done (29): dissolve, dips, wipes, push, iris (xfade) + Clock Wipe, Diagonal Wipe, Venetian Blinds, Inset, Iris Box / Diamond, Barn Doors ×2 (shape masks) + Slide ×4, Cross Zoom, Zoom In / Out, Spin In / Away (moved clips); not yet morph cut, film dissolve, 3D, presets | M |
| Blend modes (multiply, screen, overlay, add, …) | P R F V K C | done: 19 per-channel modes (Opacity → Blend mode; preview = ps_2_0 shader over a brush of the tracks below, export = lut2, one formula); not yet Dissolve, Darker / Lighter Color, Hue / Saturation / Color / Luminosity | M |
| Crop, flip / mirror, anchor point, corner pin, distort | all | done: crop (keyframable sides, edge feather), horizontal / vertical flip, anchor point (keyframable pivot), corner pin (keyframable corners, drag them on the monitor; preview = a perspective shader equal to the export's ffmpeg perspective, keyframes = per-frame expressions), lens distortion (keyframable; preview shader = lenscorrection); not yet mesh warp | S–M |
| Adjustment layers | P R V K | done: Opacity (keyframes), Gaussian blur, Lumetri (grade, look, curves, wheels, vignette) and fades applied to everything below on V3 / V2 (preview = a ps_2_0 LUT shader over the tracks below, export = lut3d on the same 17³ cube); not yet masks, looks, keys or transforms on an adjustment layer | M |
| Chroma key (green screen), luma key, spill suppression | all | done: Ultra key (green / blue screen, eyedropper, auto colour, tolerance, softness, spill, choke, soften) and luma key (black / white backgrounds) — ffmpeg in the preview's decoder and the export; not yet garbage matte, matte view | M |
| Track matte key from any clip (video as matte) | P R F V | done: any video / picture as a brightness or alpha matte, reveal or cut-out, for everything below it (plus text and object masks) | M |
| Stabilization (warp stabilizer), rolling-shutter repair | P R F V K C | done: Effect Controls → Stabilize (smooth motion / tripod, smoothness, shakiness, zoom in / zoom as needed / show edges) — vid.stab in two passes, rendered in the background like speed changes, speed-changed and reversed clips too; preview and export read the same file; not yet rolling-shutter repair | M |
| Attach titles / graphics / effects to a tracked object (point / planar tracking) | P R F V C | object tracking exists: expose the track as a position source (e.g. formula `track(“Object 1”).x`) | M |
| Effect stack: reorder, enable/disable, presets, copy between clips | all | | M |
| Generators: colour matte, solid, gradient, noise, bars & tone, countdown | all | ffmpeg lavfi sources; preview draws them | S |
| Look effects: sharpen, vignette, film grain, glow, drop shadow on clips, lens distortion, glitch, VHS | P R F V K C | done: sharpen, glow, film grain (ffmpeg in the decoder and the export), drop shadow (composited from the clip's alpha), vignette (Lumetri), lens distortion; not yet glitch, VHS | M |
| Noise reduction (spatial / temporal) | R P V | ffmpeg `hqdn3d` / `nlmeans` (slow) | M |
| Animated title presets (fly in, typewriter, fade per word), rolling / crawling credits | P R F V C | keyframes + formulas exist; add per-character animation and presets | M |
| Motion graphics templates, shape layers, arrows, stickers | P F C | | L |
| 3D titles, particles, Fusion-style node compositing | R (F) | | L |
| Picture-in-picture / split-screen layout presets | V C F | motion keyframes exist — presets only | S |

## 4. AI features
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| Speech-to-text transcription, captions, text-based editing | P R F C | partial: captions from speech done (Whisper with timestamps, voice-refined word times, Captions view, SRT / VTT, burn-in); text-based editing (delete words → cut the video) still to do | M |
| Auto reframe (smart crop for vertical / square) | P R C | done for Fill (tracked mask or saliency → Reframe pan keys); zoom (scale) keys not yet | S |
| Background removal for people, automatically for the whole clip | R C (P) | partial: AI cut-out with a click + tracking | S |
| Automatic face detection → blur all faces | R C | a face detector ONNX model + the object-mask pipeline | M |
| Object removal / inpainting, clean plate | R (P) | video inpainting model | L |
| Voice isolation, dialogue enhance, AI noise removal | P R F C | ONNX audio denoiser | M |
| Auto-ducking music under dialogue | P R F C | voice detection on the PCM → volume keyframes (exist) | S |
| Scene / shot detection, auto-tagging, search by content | P R F | | M |
| Super scale (upscaling), frame interpolation (slow motion) | R C | ONNX models; ffmpeg `minterpolate` as a basic version | M |
| Smart colour match / auto colour | P R F | | M |
| Beat detection → markers / auto-cut to music | C (P R) | onset detection on the PCM | S |

## 5. Colour
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| Basic correction: exposure, contrast, highlights, shadows, whites, blacks, temperature, tint, saturation, vibrance | all | done: the whole grade bakes into one 33³ LUT (preview = lut3d arithmetic in C#, export = `lut3d`) | M |
| LUT import (.cube), creative looks | all | done: 10 built-in looks + .cube files with intensity; no .cube export of a grade from the UI yet | S |
| Curves (RGB, hue vs sat, luma vs sat), colour wheels (lift / gamma / gain) | all | done: RGB + R/G/B curves, lift / gamma / gain wheels; not yet hue vs sat / luma vs sat (need per-pixel HSL, outside a 3D LUT only partly) | M |
| Secondary correction: HSL qualifier, power windows | P R F A V | object masks can serve as windows | M |
| Scopes: waveform, vectorscope, histogram, RGB parade | all | done (Lumetri Scopes side view) | S–M |
| HDR (PQ / HLG), colour management (Rec.709 / 2020, ACES), 10-bit pipeline | P R F A | export composite is 8-bit 4:4:4 (ProRes 4444 master keeps it); H.264 / HEVC out 4:2:0 | L |
| Node-based grading, grade versions, stills gallery, shot matching | R | | L |

## 6. Audio
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| Audio track mixer: faders, pan, solo, mute, master bus, peak meters | all | done: Audio Track Mixer side view + M/S in the track headers; `Mixer.Mix` is the one mixer (playback, meters, loudness), export = the same math in ffmpeg (checked sample by sample). Automation done: fader / balance keyframes per track (Write records them while playing — touch; bands on the timeline; export = the same 240-sample block gains). Master automation and submixes done (up to 4 buses: routing per track, effects, fader, pan, mute, automation; export = the mixer's own render of each bus) | M |
| Loudness meter (LUFS) and normalize to a target (−14 YouTube / −23 broadcast) | P R F A V | done: BS.1770 integrated loudness + true peak in-app (= ffmpeg ebur128 within 0.05 LU), export normalization (one gain + 4× oversampled limiter only when peaks need it), Match Clips. Next: live momentary / short-term LUFS meter, loudness range | S |
| Audio crossfades: exponential fade, crossfade over gaps | all | done: constant power / constant gain crossfades (afade qsin / tri, preview mixer uses the same curve) | S |
| Audio effects: parametric EQ, compressor, limiter, de-esser, reverb, pitch shift, noise gate | all | done: parametric EQ, compressor, noise gate, de-esser, hum removal, reverb (Freeverb), pitch shift (rubberband, formants kept or not, kept in sync), Voice clean-up / Room / Hall presets — on clips, tracks and submixes (pre-fader, reverb tails ring on past the clips); one C# chain run live by the mixer and rendered for the export; not yet a limiter as an effect | M–L |
| Channel mapping, stereo / mono / 5.1 | P R A V | | M |
| VST3 / AU plugin hosting | P R F V (K) | | L |
| Audio scrubbing, sample-accurate audio edits | P R F A V | | M |
| Music remix / retime to a duration | P (R) | | L |

## 7. Export and delivery
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| Export settings: DNxHR, 10-bit (HEVC Main10 / ProRes 4444) | all | done: resolution, frame rate, CRF / bitrate, H.264, HEVC, AV1, VP9, ProRes 422 HQ (ExportSettings.cs); 10-bit needs a 16-bit compositing path | M |
| Presets: broadcast (XDCAM / IMX), podcast (audio loudness) | P R F V C | done: YouTube 1080p/4K/Shorts, TikTok, Reels, IG feed, Facebook, X, LinkedIn, Vimeo, HEVC, AV1, ProRes, WebM, WAV, MP3 | S |
| Export a frame (PNG), image sequence, GIF | all | done: In/Out range, audio only (WAV / MP3); the publish window already grabs the playhead frame as a thumbnail | S |
| Alpha-channel export (ProRes 4444, QT-RLE, PNG sequence, WebM VP9 alpha) | P R F A V | | S |
| Captions: SRT / VTT sidecar, burn-in, embedded 608/708 | all | done: burn-in + .srt beside the export, SRT / VTT export / import; embedded 608/708 not yet | S |
| Render queue / batch export, background export while editing | P R F A V | | M |
| Render previews / timeline render cache, smart rendering (no re-encode of unchanged parts) | P R F A V | | L |
| Upload: resumable after a network drop, upload queue in the background, post analytics | P R F C | done: direct upload to YouTube, TikTok, Instagram, Facebook, X, LinkedIn, Pinterest, Vimeo (+ Threads share), auto-upload after export | M |

## 8. Workspace, collaboration and platform
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| Dockable / rearrangeable panels, saved workspaces, second monitor, full-screen playback | all | done: side views and Effect Controls dock left / right / bottom or float (drag a header / tab, or right-click), the Program monitor and the timeline float (second screen), Window menu workspaces (Editing, Color, Audio, Effects, Captions, Second screen + saved), full-screen playback (F11), maximize the panel under the mouse (`); not yet split panels side by side in one zone, tabbing panels into the monitor area | M |
| External video output (Blackmagic / AJA) for client monitors | P R F A V | | L |
| Team projects / shared bins / bin locking | P R A F | | L |
| Review & approval with timecoded comments (Frame.io-style) | P R F | | L |
| Plugins (OFX effects), scripting API (Python / Lua / JS) | P R F A V K | formulas are a small start | L |
| Localization (languages), accessibility (screen readers, high contrast) | all | | M |
| macOS / Linux builds | P R K C (F macOS only) | WPF is Windows-only; would need Avalonia / MAUI | L |
| GPU compositing for export (faster, identical to the preview) | P R F V C | export composites with ffmpeg on the CPU; preview is WPF on the GPU | L |

## Recently done (removed from this list)
- 2026-09-30: clip keyframes + formulas (graph editor, dope sheet, easing, timeline diamonds); mask keyframes + manual correction; AI object selection + tracking; themed glass dialogs.
- 2026-10-01: sequence settings (any size / frame rate, conform + undo), Shorts / 1:1 / 4:5 format toggle with Fill + pan, Fit + blur, Fit and auto reframe; export settings (presets, codecs, GPU encoders, bitrate, In/Out, audio only); publishing to 8 platforms + Threads share with OAuth sign-in and auto-upload; suggested titles / descriptions / hashtags from speech (Whisper), scenes (CLIP), titles and Bible references; app icon.
- 2026-10-02: transitions (12 video: dissolve, dips, wipes, pushes, iris; 2 audio crossfades) with an Effects panel, timeline boxes, Ctrl+D / Ctrl+Shift+D / Shift+D; material themes (Frosted Glass, Stone, Metals, Nature) with themed controls (play button, slider thumbs / tracks, separators, screws) and ambient animation.
- 2026-10-11: audio waveforms on clips; Audio Track Mixer (fader, pan, mute, solo per track, master, peak meters; undo; saved with the project); loudness (BS.1770 meter, export normalization to −14 / −16 / −23 / −24 with a true-peak limiter, Match Clips); export audio placed in whole samples.
- 2026-10-12: clip speed / duration (Ctrl+R), reverse, frame holds, time remapping (speed keyframes); preview and export frame-exact through rendered remaps; pitch-kept or tape-style sound; exports without sound fixed.
- 2026-10-12 (later): colour correction (Lumetri Color section: basic correction, looks + .cube LUTs, faded film, lift / gamma / gain wheels, RGB curves, vignette, Auto, copy / paste) with preview = export through one LUT; Lumetri Scopes (waveform, RGB parade, vectorscope, histogram); cartoon / cel shading / anime looks (Stylize section; the same ffmpeg graph in preview and export); Thumbnail Studio (16:9 / 9:16, templates, suggested frames, AI subject cut-out with outline, 8 text styles, arrows / circles, JPG under 2 MB, "Use for publishing"); Intro & Hook (opening moments from speech or loudness, Quote / Question / Teaser / Title card / Hook + title card, one undo step).
- 2026-10-12 (evening): blend modes (19 per-channel modes, also on titles), crop (keyframable, soft edge) and flips, Ultra key chroma key (green / blue screen, eyedropper, auto colour, spill, choke, soften); static clips now placed on the export's even-pixel grid in the preview too.
- 2026-10-13: 17 more transitions (shape masks: clock wipe, barn doors, iris box / diamond, diagonal wipe, venetian blinds, inset; moved clips: slides, cross zoom, zoom in / out, spin in / away); clip audio effects (EQ, compressor, noise gate, de-esser, hum removal, voice clean-up); look effects (sharpen, glow, grain) and drop shadow; luma key and track mattes from any clip; anchor point and corner pin.
