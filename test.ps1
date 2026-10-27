# Build + headless tests for CutPro. Prints only failures + summaries.
#   powershell -File test.ps1          build, selftest, export, format import/decode
#   powershell -File test.ps1 -play    + playback measurements (opens a window; basic + edits scenarios)
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot; $d = Join-Path $env:TEMP 'cutpro_test'; $fmt = Join-Path $env:TEMP 'cutpro_formats'
Get-Process CutPro -ErrorAction SilentlyContinue | Stop-Process -Force
Push-Location "$root\CutPro"
$b = dotnet build -c Release 2>&1 | Select-String 'error CS|warning CS|error MSB' | % { ($_.Line -split '\[C:')[0] -replace '^.*\\CutPro\\', '' } | Select -Unique
Pop-Location
if ($b) { "BUILD:"; $b; exit 1 } else { "build ok" }
$exe = (Get-ChildItem "$root\CutPro\bin\Release" -Recurse -Filter CutPro.exe | Select -First 1).FullName
$ff = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue | Select -First 1).FullName

# test media (generated once with ffmpeg lavfi sources)
if (-not (Test-Path "$d\a.mp4") -and $ff) {
  New-Item -ItemType Directory -Force $d | Out-Null
  & $ff -y -v error -f lavfi -i "testsrc=size=1280x720:rate=30:duration=6" -f lavfi -i "sine=frequency=440:duration=6" -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$d\a.mp4"
  & $ff -y -v error -f lavfi -i "smptebars=size=640x480:rate=30:duration=4" -f lavfi -i "sine=frequency=880:duration=4" -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$d\b.mp4"
}
if (-not (Test-Path "$fmt\uhd_hevc10.mkv") -and $ff) {   # (a file, not the folder: Windows Storage Sense empties old temp folders but leaves them)
  New-Item -ItemType Directory -Force $fmt | Out-Null
  $src = "testsrc2=size=1920x1080:rate=30:duration=6"; $x265 = @('-preset', 'ultrafast', '-x265-params', 'log-level=error')
  & $ff -y -v error -f lavfi -i $src -f lavfi -i "sine=frequency=440:duration=6" -c:v libx265 @x265 -c:a libopus -shortest "$fmt\hevc_opus.mkv"
  & $ff -y -v error -f lavfi -i $src -f lavfi -i "sine=frequency=550:duration=6" -c:v libx265 @x265 -tag:v hvc1 -c:a aac -shortest "$fmt\tmp.mp4"
  & $ff -y -v error -display_rotation 90 -i "$fmt\tmp.mp4" -c copy "$fmt\phone_hevc_rotated.mp4"; Remove-Item "$fmt\tmp.mp4"
  & $ff -y -v error -f lavfi -i "testsrc2=size=3840x2160:rate=30:duration=5" -f lavfi -i "sine=frequency=660:duration=5" -c:v libx265 @x265 -pix_fmt yuv420p10le -c:a aac -shortest "$fmt\uhd_hevc10.mkv"
  & $ff -y -v error -f lavfi -i "testsrc2=size=1280x720:rate=30:duration=4" -f lavfi -i "sine=frequency=330:duration=4" -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -b:v 2M -c:a libopus -shortest "$fmt\vp9.webm"
  & $ff -y -v error -f lavfi -i "sine=frequency=220:duration=5" -c:a flac "$fmt\tone.flac"
  & $ff -y -v error -f lavfi -i "sine=frequency=880:duration=5" -c:a libopus "$fmt\tone.opus"
}

# 1. editing-model self test
$p = Start-Process $exe -ArgumentList '--selftest', "$d\selftest.txt" -PassThru -Wait
Get-Content "$d\selftest.txt" | ? { $_ -notmatch '^PASS' }

# 2. export test (legacy project in %TEMP%\cutpro_test\test.cutpro, if present)
if (Test-Path "$d\test.cutpro") {
  Get-ChildItem "$d\out.mp4*" -ErrorAction SilentlyContinue | Remove-Item
  $p = Start-Process $exe -ArgumentList '--export', "$d\test.cutpro", "$d\out.mp4" -PassThru -Wait
  if ($p.ExitCode -eq 0) { "export ok ($((Get-Item "$d\out.mp4").Length) bytes)" } else { "EXPORT FAILED:"; Get-Content "$d\out.mp4.error.txt" -Tail 15 }
}

# 3. import/decode/export of MKV, HEVC (incl. 10-bit 4K, rotated phone), VP9, FLAC, Opus
if (Test-Path $fmt) {
  Remove-Item "$fmt\export_all.mp4" -ErrorAction SilentlyContinue
  $p = Start-Process $exe -ArgumentList '--probetest', $fmt, "$fmt\probetest.txt" -PassThru
  if (-not $p.WaitForExit(180000)) { $p.Kill(); "PROBETEST TIMEOUT" } else { Get-Content "$fmt\probetest.txt" | ? { $_ -notmatch '^PASS' } }
}

# 4. playback measurements (opens a window): basic = start/pause/seek/scrub; edits = cuts/ripple/undo/drag while playing;
#    scrub = playhead drags, stopping, frame steps (heavy 4K set: CutPro.exe --playtest out.txt %TEMP%\cutpro_uhdheavy scrub)
#    speed = slow / fast / reversed / held / ramped clips, played before and after their background render
#    looks = the basic session with colour grades + cartoon / anime looks on every clip and the Lumetri Scopes open
#    composite = the basic session with a blended (Screen), cropped, flipped, keyed clip on V2 and a blended title
#    nest = the basic session with its first clips nested: plays the nested render (BGRA, its own frame grid) then the media after it
#    float = the basic session with the Program monitor in its own window (the "Second screen" workspace: on the other screen if there is one)
#    tracks = the basic session in a 12 + 4 track sequence with a picture-in-picture clip on each of V3 … V12 (ten decoders, 12 track canvases)
if ($args -contains '-play') {
  foreach ($run in 'basic', 'edits', 'scrub', 'speed', 'looks', 'composite', 'nest', 'float', 'tracks') {
    $sc = if ($run -eq 'float') { 'basic' } else { $run }
    $env:CUTPRO_WORKSPACE = if ($run -eq 'float') { 'Second screen' } else { $null }
    $env:CUTPRO_SIDEVIEW = if ($sc -eq 'looks') { 'Scopes' } else { $null }
    $o = "$d\playtest_$run.txt"; Remove-Item $o -ErrorAction SilentlyContinue
    $p = Start-Process $exe -ArgumentList '--playtest', $o, '-', $sc -PassThru
    if (-not $p.WaitForExit(90000)) { $p.Kill(); "PLAYTEST $run TIMEOUT" } else { "-- $run"; Get-Content $o }
  }
  $env:CUTPRO_WORKSPACE = $null; $env:CUTPRO_SIDEVIEW = $null
}

# 5. titles / text masks / Gaussian blur: preview vs export pixel comparison (writes compare_*.png)
$fx = Join-Path $env:TEMP 'cutpro_fx'; Remove-Item $fx -Recurse -Force -ErrorAction SilentlyContinue
$p = Start-Process $exe -ArgumentList '--fxtest', $fx -PassThru
if (-not $p.WaitForExit(180000)) { $p.Kill(); "FXTEST TIMEOUT" } else { Get-Content "$fx\fxtest.txt" | ? { $_ -notmatch '^PASS' } }

# 6. object selection: AI click-select + tracking accuracy, shape tracking, tracked-mask export timing, preview vs export of every mask effect
if (Test-Path "$env:LOCALAPPDATA\CutPro\Models\mobile_sam_image_encoder.onnx") {
  $ob = Join-Path $env:TEMP 'cutpro_objtest'; Get-ChildItem $ob -Exclude 'ball.mp4' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
  $p = Start-Process $exe -ArgumentList '--objtest', $ob -PassThru
  if (-not $p.WaitForExit(240000)) { $p.Kill(); "OBJTEST TIMEOUT" } else { Get-Content "$ob\objtest.txt" | ? { $_ -notmatch '^PASS' } }
  # the real window code: select / track / delete / undo / re-add / clip end / undo a tracking run / keyframes (4K MKV: container longer than its video)
  $ou = "$ob\objui.txt"; $p = Start-Process $exe -ArgumentList '--objui', $ou, "$env:TEMP\cutpro_formats\uhd_hevc10.mkv" -PassThru
  if (-not $p.WaitForExit(240000)) { $p.Kill(); "OBJUI TIMEOUT" } else { $l = Get-Content $ou; $bad = $l | ? { $_ -match 'ALERT|EXCEPTION|FAIL|decode' }; if ($bad) { $bad } else { "objui: $($l.Count - 1) steps ok" } }
} else { "OBJTEST skipped (AI model not downloaded yet: use Select object once in the app)" }

# 7. keyframes + formulas: animated image clips / titles / volume, preview vs export (anim_*.png)
$an = Join-Path $env:TEMP 'cutpro_anim'; Get-ChildItem $an -Exclude 'tone.wav' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$p = Start-Process $exe -ArgumentList '--animtest', $an -PassThru
if (-not $p.WaitForExit(240000)) { $p.Kill(); "ANIMTEST TIMEOUT" } else { Get-Content "$an\animtest.txt" | ? { $_ -notmatch '^PASS' } }

# 8. sequence formats + export settings: 1080p / Shorts Fill + pan / Fit + blur vs preview, 25/29.97/23.976/60 fps, frame-rate
#    conform + undo, In/Out, every codec preset and working GPU encoder (seq_*.png)
$sq = Join-Path $env:TEMP 'cutpro_seq'; Get-ChildItem $sq -Exclude 'tone.wav' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$p = Start-Process $exe -ArgumentList '--seqtest', $sq -PassThru
if (-not $p.WaitForExit(300000)) { $p.Kill(); "SEQTEST TIMEOUT" } else { Get-Content "$sq\seqtest.txt" | ? { $_ -notmatch '^(PASS|INFO)' } }

# 9. publish suggestions: Bible references, captions/hashtags within every platform's limits; Whisper (SAPI speech) + CLIP when
#    the models are downloaded (suggestions.txt = what the publish window would fill in)
$ct = Join-Path $env:TEMP 'cutpro_content'; Get-ChildItem $ct -Exclude 'speech.wav' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$p = Start-Process $exe -ArgumentList '--contenttest', $ct -PassThru
if (-not $p.WaitForExit(300000)) { $p.Kill(); "CONTENTTEST TIMEOUT" } else { Get-Content "$ct\contenttest.txt" | ? { $_ -notmatch '^PASS' } }

# 10. sign-in + upload for every platform against a local fake API (nothing is sent anywhere; accounts stay in the test folder)
$ut = Join-Path $env:TEMP 'cutpro_upload'; Remove-Item $ut -Recurse -Force -ErrorAction SilentlyContinue
$p = Start-Process $exe -ArgumentList '--uploadtest', $ut -PassThru
if (-not $p.WaitForExit(180000)) { $p.Kill(); "UPLOADTEST TIMEOUT" } else { Get-Content "$ut\uploadtest.txt" | ? { $_ -notmatch '^PASS' } }

# 11. transitions: every kind, preview vs export at 1/4, 1/2, 3/4 (trans_*.png), handles, audio crossfades, razor cuts
$tt = Join-Path $env:TEMP 'cutpro_trans'; Get-ChildItem $tt -Exclude 'tone*.wav' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$p = Start-Process $exe -ArgumentList '--transtest', $tt -PassThru
if (-not $p.WaitForExit(300000)) { $p.Kill(); "TRANSTEST TIMEOUT" } else { Get-Content "$tt\transtest.txt" | ? { $_ -notmatch '^PASS' } }

# 12. audio mixer + loudness: in-app mix vs exported WAV sample by sample (fader, pan, master, solo, mute, In/Out, 44.1 kHz / mono),
#     BS.1770 loudness + true peak vs ffmpeg ebur128, normalization to -14 LUFS (with / without the limiter), waveform + meter peaks, undo
$at = Join-Path $env:TEMP 'cutpro_audio'; Get-ChildItem $at -Exclude 'voice.wav','music44k.wav','mono.wav','clicks.wav','ref_m23.wav' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$p = Start-Process $exe -ArgumentList '--audiotest', $at -PassThru
if (-not $p.WaitForExit(300000)) { $p.Kill(); "AUDIOTEST TIMEOUT" } else { Get-Content "$at\audiotest.txt" | ? { $_ -notmatch '^PASS' } }

# 13. speed / reverse / frame hold / time remapping: every exported and previewed frame read back from a frame-number stripe,
#     pitch-kept and tape-style speed, reversed sound, export sound = mixer, edits (razor, trim, handles, Speed/Duration, holds, undo)
$st = Join-Path $env:TEMP 'cutpro_speed'; Get-ChildItem $st -Exclude 'count*.mp4','sweep.wav' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$p = Start-Process $exe -ArgumentList '--speedtest', $st -PassThru
if (-not $p.WaitForExit(400000)) { $p.Kill(); "SPEEDTEST TIMEOUT" } else { Get-Content "$st\speedtest.txt" | ? { $_ -notmatch '^PASS' } }

# 14. colour: preview LUT = ffmpeg lut3d, .cube files, preview vs export of graded video / picture + vignette / cartoon / cel / anime (color_*.png)
$ct = Join-Path $env:TEMP 'cutpro_color'; Get-ChildItem $ct -Exclude 'land.png','shape.png','land.mp4' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$p = Start-Process $exe -ArgumentList '--colortest', $ct -PassThru
if (-not $p.WaitForExit(300000)) { $p.Kill(); "COLORTEST TIMEOUT" } else { Get-Content "$ct\colortest.txt" | ? { $_ -notmatch '^PASS' } }

# 15. Intro & Hook + Thumbnail Studio: hook scoring, every intro template (ripple, titles, transitions, links, undo, export, title card
#     preview vs export), thumbnail frames / styles / outline / JPG under 2 MB (thumb_*.png, hook_titlecard.png)
$tt = Join-Path $env:TEMP 'cutpro_studio'; Get-ChildItem $tt -Exclude 'talk.mp4' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$p = Start-Process $exe -ArgumentList '--studiotest', $tt -PassThru
if (-not $p.WaitForExit(300000)) { $p.Kill(); "STUDIOTEST TIMEOUT" } else { Get-Content "$tt\studiotest.txt" | ? { $_ -notmatch '^PASS' } }

# 16. blend modes / crop / flip / chroma key: blend formulas on every value pair (lut2 = C# definition, preview shader within 1 level),
#     preview vs export for every blend mode, a blended title, soft and keyframed crops, flips, a green-screen key (comp_*.png)
$cp = Join-Path $env:TEMP 'cutpro_composite'; Get-ChildItem $cp -Exclude 'bg.png','fg.mp4','green.mp4' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$p = Start-Process $exe -ArgumentList '--compositetest', $cp -PassThru
if (-not $p.WaitForExit(300000)) { $p.Kill(); "COMPOSITETEST TIMEOUT" } else { Get-Content "$cp\compositetest.txt" | ? { $_ -notmatch '^PASS' } }

# 17. nested sequences: transparent render over black = normal export, Nest keeps picture and sound (undo / redo), preview vs export of
#     nested clips (titles over video, scaled / rotated / shadow, graded / cropped / flipped, blend, dissolve, two levels deep, 1080p25, 9:16,
#     29.97 projects), render reuse and re-render after an edit, sequence rules / switching / duplicate / delete / project round trip (nest_*.png)
$nt = Join-Path $env:TEMP 'cutpro_nesttest'; Get-ChildItem $nt -Exclude 'bg.png','fg.mp4','fga.mp4' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$p = Start-Process $exe -ArgumentList '--nesttest', $nt -PassThru
if (-not $p.WaitForExit(600000)) { $p.Kill(); "NESTTEST TIMEOUT" } else { Get-Content "$nt\nesttest.txt" | ? { $_ -notmatch '^PASS' } }

# 18. captions from speech: Whisper timestamps on synthesized speech (words, language, sentence starts ±0.4 s, nothing in silence / tone), cutting
#     into captions (line width, lines, seconds, frames), SRT / VTT round trips, caption titles on a free track + undo, burned in: preview vs export
#     (incl. caption changes on their frames and 300 captions as one stitched input), not burned in (cap_*.png). Skips Whisper without the model.
$cp2 = Join-Path $env:TEMP 'cutpro_captions'; Get-ChildItem $cp2 -Exclude 's?.wav','speech.wav','fg50.mp4' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$p = Start-Process $exe -ArgumentList '--captiontest', $cp2 -PassThru
if (-not $p.WaitForExit(600000)) { $p.Kill(); "CAPTIONTEST TIMEOUT" } else { Get-Content "$cp2\captiontest.txt" | ? { $_ -notmatch '^PASS' } }

# 19. adjustment layers: the preview's LUT shader = ffmpeg lut3d (17³ trilinear) on 262 144 colours; preview vs export: colour, B&W + vignette,
#     blur, blur + colour at 60 %, fade, keyframed opacity, on V2 under a V3 title, on V1 (nothing) (adj_*.png)
$at = Join-Path $env:TEMP 'cutpro_adjust'; Get-ChildItem $at -Exclude 'photo.png','video.mp4' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$p = Start-Process $exe -ArgumentList '--adjusttest', $at -PassThru
if (-not $p.WaitForExit(300000)) { $p.Kill(); "ADJUSTTEST TIMEOUT" } else { Get-Content "$at\adjusttest.txt" | ? { $_ -notmatch '^PASS' } }

# 20. stabilization: a synthetic shaky shot (pan + jitter) → jitter before / after, the pan kept, tripod mode, the render on the sequence grid,
#     preview vs export of the stabilized clip, razor cut pieces, show edges (stab_*.png)
$sb = Join-Path $env:TEMP 'cutpro_stab'; Get-ChildItem $sb -Exclude 'big.png','shaky.mp4','held.mp4' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$p = Start-Process $exe -ArgumentList '--stabtest', $sb -PassThru
if (-not $p.WaitForExit(600000)) { $p.Kill(); "STABTEST TIMEOUT" } else { Get-Content "$sb\stabtest.txt" | ? { $_ -notmatch '^PASS' } }

# 21. dockable panels through the real window: left / right / bottom / own window, Effect Controls on the left, the Program monitor and
#     the timeline in their own windows (frames settle, it plays), full screen, maximize, closing a window docks back, workspaces + JSON
$dk = "$env:TEMP\cutpro_docktest.txt"; Remove-Item $dk -ErrorAction SilentlyContinue
$p = Start-Process $exe -ArgumentList '--docktest', $dk -PassThru
if (-not $p.WaitForExit(240000)) { $p.Kill(); "DOCKTEST TIMEOUT" } else { Get-Content $dk | ? { $_ -notmatch '^PASS' } }
$env:CUTPRO_WORKSPACE = $null
# 22. source monitor through the real window: the frame shown = ffmpeg's, keys go to the monitor clicked last, marks, Insert / Overwrite
#     (ripple, target tracks, video only, one undo), video-only / audio-only drags, one monitor plays at a time, Match Frame, docking
$sr = "$env:TEMP\cutpro_srctest.txt"; Remove-Item $sr -ErrorAction SilentlyContinue
$p = Start-Process $exe -ArgumentList '--srctest', $sr -PassThru
if (-not $p.WaitForExit(240000)) { $p.Kill(); "SRCTEST TIMEOUT" } else { Get-Content $sr | ? { $_ -notmatch '^PASS' } }
# 23. more tracks: the layout model (add / delete / undo, locks, project file, sequences, 40 tracks in the mixer), preview vs export with 7 video
#     tracks (stacking, adjustment layer, blend, matte, dissolve), 4 audio tracks, a 7-track sequence nested in a 3 + 2 one
$tt = Join-Path $env:TEMP 'cutpro_tracks'
$p = Start-Process $exe -ArgumentList '--tracktest', $tt -PassThru
if (-not $p.WaitForExit(600000)) { $p.Kill(); "TRACKTEST TIMEOUT" } else { Get-Content "$tt\tracktest.txt" | ? { $_ -notmatch '^PASS' } }
# 24. tracks through the real window: scrolling, the preview's canvases, mixer strips, the source monitor and band modes follow the layout,
#     locks, heights, playing 10 picture-in-picture tracks, undo
$tu = "$env:TEMP\cutpro_trackui.txt"; Remove-Item $tu -ErrorAction SilentlyContinue
$p = Start-Process $exe -ArgumentList '--trackui', $tu -PassThru
if (-not $p.WaitForExit(240000)) { $p.Kill(); "TRACKUI TIMEOUT" } else { Get-Content $tu | ? { $_ -notmatch '^PASS' } }
