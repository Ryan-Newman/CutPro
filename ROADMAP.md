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
1. AI voice isolation / upscaling / interpolation models (ask first), VST3 hosting. (Every "extra high" item is done, face blur included. Sync lock, track targeting, live distort keyframes, the Markers list, HDR output, DNxHR, glitch / VHS and 4-point fit-to-fill are done.)
2. **Max-effort items**: GPU export compositing, HDR output and DNxHR are done.
3. Morph cut, film dissolve, 3D transitions.

## 1. Project, media and sequences
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| Multiple sequences in a project; nested sequences / compound clips | P R F A V K | done: sequences with tabs, per-sequence format / mixer / playhead, Nest, drag a sequence in; nested clips rendered in the background (FFV1 with alpha + mix) and read by preview and export alike (`Nest`) | L |
| Source monitor with in/out marking; 3-point / 4-point editing | P R A V K | done: Source panel (own Preview + AudioOut), I / O marks per clip, Insert (,) / Overwrite (.) at the playhead with V / A track targets, video-only / audio-only drags, Match Frame (F); 4-point Fit to Fill when source In–Out and sequence In–Out are all set | M |
| Bins / folders, labels (colour), ratings, keywords, search & filters, list view with metadata | all | done: bins (folders in folders), colour labels, ratings (0–5 keys), keywords, search (name, keywords, label, metadata, transcript), filters, sort, icon / list view | M |
| Proxy workflow (generate low-res proxies, toggle, relink) | P R F A V K | done: Project panel ▸ Proxy ▸ Create Proxies (540p / 720p, background) / Attach / Detach, the Proxies button under the monitor; frame-exact; exports and renders always read the originals; not RAW (ffmpeg can't decode BRAW / R3D) | M |
| Relink / replace media, consolidate / archive project (collect files) | all | done: Relink (offline files; the others found in the same folder), Replace Footage, Collect Files (project + media + .cube looks copied, the copy's paths rewritten) | S |
| Auto-save with versions, crash recovery | all | done: recovery copy every 30 s + from the crash handler, offered on the next start; versions every 5 min (20 kept, File ▸ Auto-Save Versions…); "save changes?" on open / close | S |
| Interchange: XML (FCP7/FCPXML), EDL, AAF, OpenTimelineIO | P R F A V K | done: EDL (CMX 3600) and OpenTimelineIO export; import of OpenTimelineIO, Final Cut Pro 7 / Premiere XML and EDL as a new sequence (cuts, gaps, linear speed, dissolves, markers, media by path); not FCPXML or AAF (binary — export XML or OTIO instead) | M |
| Scene edit detection (cut a clip at its shot changes) | P R C | done: Clip ▸ Scene Edit Detection (ffmpeg scene score, sensitivity, shortest shot) → cuts with the linked sound, or clip markers | S |
| Multicam (sync angles by audio/timecode, live angle switching) | P R F A V | done: Create Multi-Camera Source Sequence (sound / timecode / In points / starts; sound follows the camera or a fixed one, recorders included), the Program monitor's Multi-Camera view (Shift+0), click / 1–9 to switch, switches while playing become cuts, Clip ▸ Multi-Camera (Camera n, Flatten); multicam clips are plain clips (no render) | L |
| Sync / merge clips by audio waveform (external recorder) | P R F A | done: Clip ▸ Synchronize (GCC-PHAT on the conformed PCM, 4 kHz then 48 kHz; ±0.03 ms on the test), start on a frame + sub-frame In | M |
| Timecode, reel names, metadata display | P R F A | done: timecode / reel / camera / streams from the file's tags (Properties…, Project panel tooltip and info line), the source monitor shows the media's timecode (drop-frame aware) | S |
| Screen / webcam / voice-over recording | P F V C | partial: voice-over done (File ▸ Record Voice-over…: waveIn, plays the sequence, lands on the track where spoken); screen / webcam later | M |
| Stock media / templates browser | C (P) | | L |

## 2. Timeline editing
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| Add / remove tracks, track names, heights, lock, solo, target, sync-lock | all | done: 1 … 32 video + 1 … 32 audio tracks per sequence (add above / below / several, delete with clips, delete empty), names, locks, heights, vertical scrolling, hide / mute / solo, sync lock (header toggle: off, the track stays put on a ripple / insert), track targeting (header patch + source monitor Insert / Overwrite) | M |
| Overwrite vs insert edits, replace edit, fit-to-fill | P R F A V K | done: drop = overwrite, Ctrl+drop = insert, Alt+drop onto a clip = replace; Clip ▸ Replace with Clip from Project; Fit to Fill (source In–Out into the sequence In–Out at the fitting speed) | M |
| Rolling, slip, slide, rate-stretch tools | P R F A V K | done: Ripple Edit (B), Rolling Edit (N), Rate Stretch (R), Slip (Y), Slide (U) — toolbar Trim menu, edits from the drag's start clamped to what the media allows, linked sound along (Alt: alone), the monitor's two-up view, keyboard trimming (Ctrl+Shift+, / . = In / Out 1 frame, Ctrl+Alt+Shift+, / . = 5 frames; uses the current trim tool, Ripple when Selection / Razor is out); ripple / insert follow each track's sync lock | M |
| Markers (sequence + clip), marker comments, chapter markers | all | done: sequence markers (name, comment, colour, length, chapter; drag / double-click / menu) and clip markers (source time); Markers list panel (Window menu; click seeks, double-click edits); chapters into MP4 / MOV / MKV, YouTube chapter list (copy, Publish description) | S |
| Clip speed / duration, reverse, frame blending / optical flow | all | done: Speed/Duration (Ctrl+R, linked speed / duration, ripple), reverse, pitch-kept or tape-style sound; rendered frame-exact (`Remap`). Next: frame blending / optical flow for slow motion (`minterpolate`), Rate Stretch tool (R) | M |
| Time remapping / speed ramps (keyframed speed) | P R F V C | done: Speed keyframes (Effect Controls → Time; keyed by source time, eased like any key), picture only like Premiere | M |
| Freeze frame / frame hold, still from frame | all | done: Add Frame Hold, Insert Frame Hold Segment, Freeze frame toggle | S |
| Audio waveforms on clips | all | done: rectified, dB scale, from the AudioCache PCM (`Peaks`: 256-frame max + ×4 levels, `.peaks` file per conformed file) | S |
| Volume / opacity rubber bands on the clip (keyframes drawn as a line) | P R F V K | done: clip volume band on audio clips, track volume / pan automation bands, opacity bands on video clips (the header's Opacity button) — drag the line / keys, Ctrl+click adds, Alt+click removes | S |
| Magnetic timeline, connected clips, auditions | F | different editing model — optional | L |
| Clip groups, copy / paste attributes (effects, motion, keyframes) | all | done: groups (Ctrl+G), copy / cut / paste / paste insert, Paste / Remove Attributes (keyframes moved to the target's source times) | S |
| Match frame, reveal in project, go to next/previous edit or gap | P R A V | done: Reveal in Project / File Explorer, Go to Gap (sequence / track) | S |
| Undo history panel | P R V | done: History panel (named steps, click to go back / forward) | S |
| Keyboard shortcut editor | all | done: every command in a keymap; Settings lists them (search, change, add, remove, reset) | S |
| JKL shuttle at 2×/4×/8×, reverse playback | all | done: J / L 1×, 2×, 4×, 8× (each press doubles), backwards too, K stops on a frame — the audio clock runs at the rate (tape-style sound), the picture via the scrub cache | M |
| Timeline zoom to sequence / to selection, scroll with playback modes | all | done:  zoom to sequence, zoom to selection, = / -, log zoom slider (an hour fits); Page / Smooth / None while playing | S |

## 3. Effects, compositing and motion graphics
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| More transitions: zoom, slide (one shot over the other), clock wipe, morph cut, film dissolve, 3D (cube spin, page peel), transition presets | all | done (29): dissolve, dips, wipes, push, iris (xfade) + Clock Wipe, Diagonal Wipe, Venetian Blinds, Inset, Iris Box / Diamond, Barn Doors ×2 (shape masks) + Slide ×4, Cross Zoom, Zoom In / Out, Spin In / Away (moved clips); not yet morph cut, film dissolve, 3D, presets | M |
| Blend modes (multiply, screen, overlay, add, …) | P R F V K C | done: 19 per-channel modes (Opacity → Blend mode; preview = ps_2_0 shader over a brush of the tracks below, export = lut2, one formula); not yet Dissolve, Darker / Lighter Color, Hue / Saturation / Color / Luminosity | M |
| Crop, flip / mirror, anchor point, corner pin, distort | all | done: crop (keyframable sides, edge feather), horizontal / vertical flip, anchor point (keyframable pivot), corner pin (keyframable corners, drag them on the monitor; preview = a perspective shader equal to the export's ffmpeg perspective, keyframes = per-frame expressions), twirl / wave / spherize / 4 × 4 mesh warp (Effect Controls ▸ Distort, mesh points on the monitor; amount / radius / centre / wave keyframable; export geq = the preview's shader), lens distortion (keyframable; preview shader = lenscorrection) | S–M |
| Adjustment layers | P R V K | done: Opacity (keyframes), Gaussian blur, Lumetri (grade, look, curves, wheels, vignette) and fades applied to everything below on V3 / V2 (preview = a ps_2_0 LUT shader over the tracks below, export = lut3d on the same 17³ cube); not yet masks, looks, keys or transforms on an adjustment layer | M |
| Chroma key (green screen), luma key, spill suppression | all | done: Ultra key (green / blue screen, eyedropper, auto colour, tolerance, softness, spill, choke, soften) and luma key (black / white backgrounds) — ffmpeg in the preview's decoder and the export; not yet garbage matte, matte view | M |
| Track matte key from any clip (video as matte) | P R F V | done: any video / picture as a brightness or alpha matte, reveal or cut-out, for everything below it (plus text and object masks) | M |
| Stabilization (warp stabilizer), rolling-shutter repair | P R F V K C | done: Effect Controls → Stabilize (smooth motion / tripod, smoothness, shakiness, zoom in / zoom as needed / show edges) — vid.stab in two passes, rendered in the background like speed changes, speed-changed and reversed clips too; preview and export read the same file; not yet rolling-shutter repair | M |
| Attach titles / graphics / effects to a tracked object (point / planar tracking) | P R F V C | done: Effect Controls ▸ Motion ▸ Attach to object… (position, or position and size) — the object's path baked into keys, Update after re-tracking, Detach; effects follow objects through object masks | M |
| Effect stack: reorder, enable/disable, presets, copy between clips | all | partial: effect presets (Save Preset…, built-ins, Effects panel, drag onto clips), copy between clips (Paste Attributes), audio effects move up / down, on / off; the video chain keeps its fixed (parity) order | M |
| Generators: colour matte, solid, gradient, noise, bars & tone, countdown | all | done: File ▸ New Item ▸ Colour Matte, Gradient (linear / radial), Bars and Tone (SMPTE HD bars + reference tone), Counting Leader (8 … 2, beep at 2) — rendered once to media files, editable | S |
| Look effects: sharpen, vignette, film grain, glow, drop shadow on clips, lens distortion, glitch, VHS | P R F V K C | done: sharpen, glow, film grain, VHS (chroma smear + scanlines), glitch (RGB split + slice) — ffmpeg in the decoder and the export; drop shadow (composited from the clip's alpha), vignette (Lumetri), lens distortion | M |
| Noise reduction (spatial / temporal) | R P V | done: Effect Controls ▸ Noise reduction (fast hqdn3d / high quality nlmeans / temporal atadenoise), rendered in the background with the stabilizer's machinery so preview and export show the same frames | M |
| Animated title presets (fly in, typewriter, fade per word), rolling / crawling credits | P R F V C | done: Effect Controls ▸ Text animation — typewriter, fade by letter / word / line, rise and pop by word, in and out separately; roll (credits) and crawl (ticker) over the clip | M |
| Motion graphics templates, shape layers, arrows, stickers | P F C | done: shape layers on titles (box, ellipse, polygon, star, line, arrow, speech bubble, extra texts, 14 vector stickers) with in / out animations, delays and loops, dragged / resized on the monitor; Titles view ▸ Graphics: 15 built-in templates + Save as Graphic Template… (your own); drawn by the title renderer (preview = export ~50 dB); not Premiere .mogrt files | L |
| 3D titles, particles, Fusion-style node compositing | R (F) | | L |
| Picture-in-picture / split-screen layout presets | V C F | done: picture in picture (4 corners), side by side, top / bottom, three across, 2 × 2 grid, full frame | S |

## 4. AI features
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| Speech-to-text transcription, captions, text-based editing | P R F C | done: captions from speech; text-based editing (Transcript panel: media transcribed on this PC, words follow cuts, Delete ripples, fillers, pauses, search) | M |
| Auto reframe (smart crop for vertical / square) | P R C | done: Fill reframe (tracked mask or saliency → Pan keys) and Auto Reframe with Push-in (Scale keys, pan for the zoomed width) | S |
| Background removal for people, automatically for the whole clip | R C (P) | done: Clip ▸ Remove Background (AI picks the subject in the middle, cut-out mask tracked through the clip); click selection for anything else | S |
| Automatic face detection → blur all faces | R C | done: Clip ▸ Blur Faces… — YuNet (OpenCV, MIT, downloaded on first use with consent) finds every face, they are tracked through the clip and each gets an ellipse mask with blur / pixelate / black out (editable like any object mask) | M |
| Object removal / inpainting, clean plate | R (P) | done without a model: Effect Controls ▸ Object masks ▸ Remove Object… fills the tracked object with the background from frames where it had moved away (camera movement followed), the rest from its edges, as a cleaned copy the clip then plays; Clean Plate = a still of the background. An AI inpainting model (LaMa / MI-GAN) would fill objects that never move — a download | L |
| Voice isolation, dialogue enhance, AI noise removal | P R F C | partial: Audio ▸ Clean up — reduce background noise / isolate the voice (FFT denoiser with the clip's measured noise floor, voice band; latency compensated); an AI model (DeepFilterNet / DTLN) would do better — needs a model download | M |
| Auto-ducking music under dialogue | P R F C | done: Clip ▸ Auto Ducking (voice activity of the speech tracks → volume keys on the music: amount, fades) | S |
| Scene / shot detection, auto-tagging, search by content | P R F | | M |
| Super scale (upscaling), frame interpolation (slow motion) | R C | partial: Time ▸ Interpolation = frame sampling / frame blending / optical flow (minterpolate, rendered) for slow motion; Create Upscaled Copy 2× / 4× (Lanczos + sharpening); AI models (RIFE, Real-ESRGAN) not added | M |
| Smart colour match / auto colour | P R F | done: Auto Colour (exposure, white / black points, grey world) and Colour Match (to another clip's frame: R / G / B curves) | M |
| Beat detection → markers / auto-cut to music | C (P R) | done: Detect Beats (onsets, tempo, downbeats → sequence markers every beat / bar) and Cut to the Beat (edits moved onto them) | S |

## 5. Colour
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| Basic correction: exposure, contrast, highlights, shadows, whites, blacks, temperature, tint, saturation, vibrance | all | done: the whole grade bakes into one 33³ LUT (preview = lut3d arithmetic in C#, export = `lut3d`) | M |
| LUT import (.cube), creative looks | all | done: 10 built-in looks + .cube files with intensity; no .cube export of a grade from the UI yet | S |
| Curves (RGB, hue vs sat, luma vs sat), colour wheels (lift / gamma / gain) | all | done: RGB + R/G/B curves, lift / gamma / gain wheels; not yet hue vs sat / luma vs sat (need per-pixel HSL, outside a 3D LUT only partly) | M |
| Secondary correction: HSL qualifier, power windows | P R F A V | done: Lumetri ▸ HSL Secondary (hue / saturation / luma ranges with softness, eyedropper, skin / sky / leaves, show mask, invert; correction baked into the LUT) and power windows (object mask effect "Colour-correct object / background" — tracked shapes) | M |
| Scopes: waveform, vectorscope, histogram, RGB parade | all | done (Lumetri Scopes side view) | S–M |
| HDR (PQ / HLG), colour management (Rec.709 / 2020, ACES), 10-bit pipeline | P R F A | done: HLG / PQ footage tone-mapped to SDR everywhere it is decoded (Hdr.cs: BT.2408 reference white, Mobius knee, PQ peak from HDR10 metadata; Show as is), 10-bit exports (10-bit composite; HEVC Main 10 / AV1 / VP9 / ProRes), HDR output (export dialog Colour: HLG / HDR10 — Rec.709 composite inverse-mapped, SDR white = 203 cd/m², tagged HEVC 10-bit); not done: ACES / wide-gamut working space | L |
| Node-based grading, grade versions, stills gallery, shot matching | R | | L |

## 6. Audio
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| Audio track mixer: faders, pan, solo, mute, master bus, peak meters | all | done: Audio Track Mixer side view + M/S in the track headers; `Mixer.Mix` is the one mixer (playback, meters, loudness), export = the same math in ffmpeg (checked sample by sample). Automation done: fader / balance keyframes per track (Write records them while playing — touch; bands on the timeline; export = the same 240-sample block gains). Master automation and submixes done (up to 4 buses: routing per track, effects, fader, pan, mute, automation; export = the mixer's own render of each bus) | M |
| Loudness meter (LUFS) and normalize to a target (−14 YouTube / −23 broadcast) | P R F A V | done: BS.1770 integrated loudness + true peak in-app (= ffmpeg ebur128 within 0.05 LU), export normalization (one gain + 4× oversampled limiter only when peaks need it), Match Clips. Next: live momentary / short-term LUFS meter, loudness range | S |
| Audio crossfades: exponential fade, crossfade over gaps | all | done: constant power / constant gain crossfades (afade qsin / tri, preview mixer uses the same curve) | S |
| Audio effects: parametric EQ, compressor, limiter, de-esser, reverb, pitch shift, noise gate | all | done: parametric EQ, compressor, noise gate, de-esser, hum removal, reverb (Freeverb), pitch shift (rubberband, formants kept or not, kept in sync), Voice clean-up / Room / Hall presets — on clips, tracks and submixes (pre-fader, reverb tails ring on past the clips); one C# chain run live by the mixer and rendered for the export; not yet a limiter as an effect | M–L |
| Channel mapping, stereo / mono / 5.1 | P R A V | done: Audio ▸ Channels per clip (left or right on both, mono, swap), export Stereo / Mono / 5.1 (each audio track's header ▸ 5.1 Output: front, centre, surround, LFE); monitoring stays stereo | M |
| VST3 / AU plugin hosting | P R F V (K) | | L |
| Audio scrubbing, sample-accurate audio edits | P R F A V | done: sound under the playhead while dragging (Shift+S), Sequence ▸ Audio Time Units (audio-only clips trim / move / cut at single samples, deeper zoom) | M |
| Music remix / retime to a duration | P (R) | | L |

## 7. Export and delivery
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| Export settings: DNxHR, 10-bit (HEVC Main10 / ProRes 4444) | all | done: resolution, frame rate, CRF / bitrate, H.264, HEVC, AV1, VP9, ProRes 422 HQ / 4444, DNxHR HQ / HQX / 444 (ExportSettings.cs); 10-bit done (export dialog ▸ Colour ▸ 10-bit) | M |
| Presets: broadcast (XDCAM / IMX), podcast (audio loudness) | P R F V C | done: YouTube 1080p/4K/Shorts, TikTok, Reels, IG feed, Facebook, X, LinkedIn, Vimeo, HEVC, AV1, ProRes, WebM, WAV, MP3 | S |
| Export a frame (PNG), image sequence, GIF | all | done: In/Out range, audio only (WAV / MP3); the publish window already grabs the playhead frame as a thumbnail | S |
| Alpha-channel export (ProRes 4444, QT-RLE, PNG sequence, WebM VP9 alpha) | P R F A V | done: presets ProRes 4444 with alpha, WebM VP9 with alpha, QuickTime Animation, PNG sequence — transparent where nothing is drawn | S |
| Captions: SRT / VTT sidecar, burn-in, embedded 608/708 | all | done: burn-in + .srt beside the export, SRT / VTT export / import; embedded 608/708 not yet | S |
| Render queue / batch export, background export while editing | P R F A V | done: export dialog ▸ Add to Queue… renders a snapshot of the sequence in the background while editing goes on (Render Queue panel: progress, cancel, pause, render again, show the file) | M |
| Render previews / timeline render cache, smart rendering (no re-encode of unchanged parts) | P R F A V | both done. Smart render: export dialog ▸ Smart render copies whole GOPs of H.264 / HEVC where one plain clip shows. Render previews: Sequence ▸ Render Previews writes a lossless FFV1 picture of the sequence; Playback Uses Render Previews plays that file (sound stays the live mixer) until an edit, then the live composite until the new one is ready. A bar on the ruler is solid when the preview is ready | L |
| Upload: resumable after a network drop, upload queue in the background, post analytics | P R F C | done: direct upload to YouTube, TikTok, Instagram, Facebook, X, LinkedIn, Pinterest, Vimeo (+ Threads share), auto-upload after export | M |

## 8. Workspace, collaboration and platform
| Feature | Who has it | Notes for CutPro | Effort |
|---|---|---|---|
| Dockable / rearrangeable panels, saved workspaces, second monitor, full-screen playback | all | done: side views and Effect Controls dock left / right / bottom or float (drag a header / tab, or right-click), the Program monitor and the timeline float (second screen), Window menu workspaces (Editing, Color, Audio, Effects, Captions, Second screen + saved), full-screen playback (F11), maximize the panel under the mouse (`); not yet split panels side by side in one zone, tabbing panels into the monitor area | M |
| External video output (Blackmagic / AJA) for client monitors | P R F A V | | L |
| Team projects / shared bins / bin locking | P R A F | | L |
| Review & approval with timecoded comments (Frame.io-style) | P R F | | L |
| Plugins (OFX effects), scripting API (Python / Lua / JS) | P R F A V K | formulas are a small start | L |
| Localization (languages), accessibility (screen readers, high contrast) | all | done (partial): German / Spanish / French for menus, panels, dialogs, palette, shortcuts, history (run-time messages stay English); screen-reader names, timeline peer, interface size 100–150 % | M |
| macOS / Linux builds | P R K C (F macOS only) | WPF is Windows-only; would need Avalonia / MAUI | L |
| GPU compositing for export (faster, identical to the preview) | P R F V C | done for plain 8-bit SDR: full-frame cuts stay on the GPU (`overlay_cuda`); picture-in-picture is baked into each underlay's own `scale_cuda` (same-start overlay, then the slice is downloaded and joined — overlay_cuda onto an already-composited CUDA stream shifts the frames underneath). NVENC encodes CUDA nv12 with no download. Titles / effects / transitions / 10-bit / HDR still download and finish on the CPU | L |

## Recently done (removed from this list)
- 2026-09-30: clip keyframes + formulas (graph editor, dope sheet, easing, timeline diamonds); mask keyframes + manual correction; AI object selection + tracking; themed glass dialogs.
- 2026-10-01: sequence settings (any size / frame rate, conform + undo), Shorts / 1:1 / 4:5 format toggle with Fill + pan, Fit + blur, Fit and auto reframe; export settings (presets, codecs, GPU encoders, bitrate, In/Out, audio only); publishing to 8 platforms + Threads share with OAuth sign-in and auto-upload; suggested titles / descriptions / hashtags from speech (Whisper), scenes (CLIP), titles and Bible references; app icon.
- 2026-10-02: transitions (12 video: dissolve, dips, wipes, pushes, iris; 2 audio crossfades) with an Effects panel, timeline boxes, Ctrl+D / Ctrl+Shift+D / Shift+D; material themes (Frosted Glass, Stone, Metals, Nature) with themed controls (play button, slider thumbs / tracks, separators, screws) and ambient animation.
- 2026-10-11: audio waveforms on clips; Audio Track Mixer (fader, pan, mute, solo per track, master, peak meters; undo; saved with the project); loudness (BS.1770 meter, export normalization to −14 / −16 / −23 / −24 with a true-peak limiter, Match Clips); export audio placed in whole samples.
- 2026-10-12: clip speed / duration (Ctrl+R), reverse, frame holds, time remapping (speed keyframes); preview and export frame-exact through rendered remaps; pitch-kept or tape-style sound; exports without sound fixed.
- 2026-10-12 (later): colour correction (Lumetri Color section: basic correction, looks + .cube LUTs, faded film, lift / gamma / gain wheels, RGB curves, vignette, Auto, copy / paste) with preview = export through one LUT; Lumetri Scopes (waveform, RGB parade, vectorscope, histogram); cartoon / cel shading / anime looks (Stylize section; the same ffmpeg graph in preview and export); Thumbnail Studio (16:9 / 9:16, templates, suggested frames, AI subject cut-out with outline, 8 text styles, arrows / circles, JPG under 2 MB, "Use for publishing"); Intro & Hook (opening moments from speech or loudness, Quote / Question / Teaser / Title card / Hook + title card, one undo step).
- 2026-10-12 (evening): blend modes (19 per-channel modes, also on titles), crop (keyframable, soft edge) and flips, Ultra key chroma key (green / blue screen, eyedropper, auto colour, spill, choke, soften); static clips now placed on the export's even-pixel grid in the preview too.
- 2026-10-13: 17 more transitions (shape masks: clock wipe, barn doors, iris box / diamond, diagonal wipe, venetian blinds, inset; moved clips: slides, cross zoom, zoom in / out, spin in / away); clip audio effects (EQ, compressor, noise gate, de-esser, hum removal, voice clean-up); look effects (sharpen, glow, grain) and drop shadow; luma key and track mattes from any clip; anchor point and corner pin.
