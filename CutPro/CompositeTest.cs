using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>CutPro.exe --compositetest dir: blend modes, crop / flip, chroma key.
/// - blend formulas: ffmpeg lut2 (export) = the C# definition on all 256×256 value pairs, and the preview's pixel shader (rendered by WPF)
///   within 1 level, opaque and half transparent;</summary>
public static class CompositeTest
{
    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        var ff = Exporter.FindFfmpeg() ?? throw new Exception("ffmpeg not found");
        try
        {
            // ---------- 1. blend formulas on every value pair: x = the clip (columns), y = the picture below (rows) ----------
            string top = Path.Combine(dir, "grad_x.ppm"), bot = Path.Combine(dir, "grad_y.ppm");
            Ppm(top, (x, y) => (byte)x); Ppm(bot, (x, y) => (byte)y);
            var topPx = new byte[256 * 256 * 4]; var botPx = new byte[256 * 256 * 4]; var halfPx = new byte[256 * 256 * 4];
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    int o = (y * 256 + x) * 4;
                    topPx[o] = topPx[o + 1] = topPx[o + 2] = (byte)x; topPx[o + 3] = 255;
                    botPx[o] = botPx[o + 1] = botPx[o + 2] = (byte)y; botPx[o + 3] = 255;
                    halfPx[o] = halfPx[o + 1] = halfPx[o + 2] = (byte)Math.Round(x * 128 / 255.0); halfPx[o + 3] = 128;   // premultiplied, alpha 128
                }
            bool pinOnly = Environment.GetEnvironmentVariable("CUTPRO_PINONLY") == "1";     // (calibrating the pin shader: section 1b only)
            foreach (var (mode, name, _) in Blend.Modes.Skip(1).Where(_ => !pinOnly))
            {
                // export: lut2
                var outRaw = Path.Combine(dir, $"lut2_{mode}.raw");
                var e = Blend.Expr(mode);
                Run(ff, $"-y -v error -i \"{top}\" -i \"{bot}\" -filter_complex \"[0]format=gbrp[a];[1]format=gbrp[b];[a][b]lut2=c0='{e}':c1='{e}':c2='{e}',format=gbrp\" -frames:v 1 -f rawvideo \"{outRaw}\"");
                var lut = File.Exists(outRaw) ? File.ReadAllBytes(outRaw) : Array.Empty<byte>();
                int lutBad = 0, lutMax = 0;
                if (lut.Length >= 256 * 256 * 3)
                    for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++)
                    {
                        int d = Math.Abs(lut[y * 256 + x] - Blend.Reference(mode, x, y));
                        if (d > 0) lutBad++; lutMax = Math.Max(lutMax, d);
                    }
                else lutBad = -1;
                // preview: the pixel shader, rendered by WPF (software, like every RenderTargetBitmap)
                var (shMax, shBad) = ShaderDiff(mode, topPx, botPx, 255);
                var (hMax, hBad) = ShaderDiff(mode, halfPx, botPx, 128);
                Check(lutBad == 0 && shMax <= 1 && hMax <= 1, $"{name}: lut2 = formula on {(lutBad < 0 ? "NO OUTPUT" : $"{65536 - lutBad}/65536 pairs (max off {lutMax})")}; shader max off {shMax} ({shBad} pairs off by 1+), half transparent max off {hMax}");
            }
            if (Blend.CompileError != null) Check(false, "shader compile: " + Blend.CompileError);
            // ---------- 1b. the preview's corner pin shader vs ffmpeg perspective (the export's warp) on a still: geometry, interpolation, edges ----------
            // WPF puts pixel / texel centres at ½ on the GPU and at 0 in its software renderer (RenderTargetBitmap) — Pin.Half; both paths measured
            {
                var still = Path.Combine(dir, "pinsrc.png");
                if (!File.Exists(still)) Run(ff, $"-y -v error -f lavfi -i testsrc2=s=320x180 -frames:v 1 \"{still}\"");
                var (swId, swIdMean, _) = await PinDiff(ff, dir, still, null, false, false, 0);
                bool gpu = (RenderCapability.Tier >> 16) > 0;
                var (scrHalf, _, _) = await PinDiff(ff, dir, still, null, false, true, 0.5);
                var (scrZero, _, _) = await PinDiff(ff, dir, still, null, false, true, 0);
                double screenHalf = scrHalf <= scrZero ? 0.5 : 0;
                Check(swIdMean < 0.3 && (scrHalf <= 1 || scrZero <= 1) && screenHalf == (gpu ? 0.5 : 0),
                      $"pin shader pixel centres: software renderer at 0 (identity max off {swId}, mean {swIdMean:0.000} — its own sampling); on screen ({(gpu ? "GPU" : "software")}) max off {scrHalf} at ½, {scrZero} at 0");
                var quads = new[] { new CornerPin { TLX = 12, TLY = 8, TRX = 85, TRY = 15, BLX = 4, BLY = 92, BRX = 96, BRY = 80 },
                                    new CornerPin { TLX = -10, TLY = 20, TRX = 70, TRY = -5, BLX = 30, BLY = 110, BRX = 105, BRY = 70 } };
                foreach (var screen in new[] { false, true })
                    foreach (var flip in new[] { false, true })
                        for (int qi = 0; qi < quads.Length; qi++)
                        {
                            var (mx, mean, bad) = await PinDiff(ff, dir, still, quads[qi], flip, screen, screen ? screenHalf : 0);
                            // (on screen: exact but for rounding; the software renderer samples a little off by itself — see the identity above)
                            Check(screen ? mx <= 3 && mean < 0.05 : mean < 0.1 && bad < 0.015, $"corner pin shader = perspective ({(screen ? "on screen" : "software")}, quad {qi + 1}{(flip ? ", flipped" : "")}): max off {mx}, mean {mean:0.000}, pixels off by 3+ {bad:P2}");
                        }
                // lens distortion in the same shader: alone (one bilinear lookup each side: lenscorrection's), and before a pin (the export resamples
                // twice — lens, then perspective — the shader once)
                var lenses = new[] { new LensDistort { Curvature = -35, Edge = 10 }, new LensDistort { Curvature = 45, Edge = -20, CenterX = 8, CenterY = -6 } };
                foreach (var screen in new[] { false, true })
                {
                    double hf = screen ? screenHalf : 0;
                    for (int li = 0; li < lenses.Length; li++)
                    {
                        var (mx, mean, bad) = await PinDiff(ff, dir, still, null, li == 1, screen, hf, lenses[li]);
                        Check(screen ? mean < 0.1 && bad < 0.01 : mean < swIdMean + 0.15 && bad < 0.05, $"lens shader = lenscorrection ({(screen ? "on screen" : "software")}, {(lenses[li].Curvature < 0 ? "barrel fix" : "bulge, off-centre, flipped")}): max off {mx}, mean {mean:0.000}, pixels off by 3+ {bad:P2}");
                    }
                    var (pm, pmean, pbad) = await PinDiff(ff, dir, still, quads[0], false, screen, hf, lenses[0]);
                    Check(pmean < 1.5 && pbad < 0.12, $"lens + pin shader = lenscorrection → perspective ({(screen ? "on screen" : "software")}; one resampling vs two): max off {pm}, mean {pmean:0.000}, pixels off by 3+ {pbad:P2}");
                }
                if (Pin.CompileError != null) Check(false, "pin shader compile: " + Pin.CompileError);
                if (pinOnly) { File.WriteAllLines(Path.Combine(dir, "compositetest.txt"), log); return fails == 0 ? 0 : 1; }
            }

            // ---------- 2. preview vs export on a timeline ----------
            Editor.SetSequence(1280, 720, 30, ReframeMode.Fit);
            string Gen(string name, string lavfi)
            {
                var f = Path.Combine(dir, name);
                if (!File.Exists(f)) Run(ff, $"-y -v error {lavfi} \"{f}\"");
                return f;
            }
            var landSrc = @"C:\Windows\Web\Wallpaper\ThemeC\img29.jpg";
            var bgPic = Gen("bg.png", File.Exists(landSrc) ? $"-i \"{landSrc}\" -vf scale=1280:-2,crop=1280:720 -frames:v 1" : "-f lavfi -i testsrc2=s=1280x720 -frames:v 1");
            var fg = Gen("fg.mp4", "-f lavfi -i testsrc2=s=960x540:r=30:d=8 -c:v libx264 -crf 10 -pix_fmt yuv420p");
            // a green screen: the key colour with grain, a test pattern moving across it, and a soft greenish edge (spill)
            var green = Gen("green.mp4", "-f lavfi -i color=c=0x00B140:s=1280x720:r=30:d=6 -f lavfi -i testsrc2=s=420x320:r=30:d=6 " +
                            "-filter_complex \"[1]format=yuva420p,pad=iw+16:ih+16:8:8:color=0x40A050@1,gblur=sigma=3[f];[0][f]overlay=x='300+200*sin(t)':y=180,noise=alls=6:allf=t\" " +
                            "-c:v libx264 -crf 12 -pix_fmt yuv420p");
            var ed = new Editor();
            var mBg = await MediaProbe.ProbeAsync(bgPic) ?? throw new Exception("import bg");
            var mFg = await MediaProbe.ProbeAsync(fg) ?? throw new Exception("import fg");
            var mGr = await MediaProbe.ProbeAsync(green) ?? throw new Exception("import green");
            ed.Media.AddRange(new[] { mBg, mFg, mGr });
            var cases = new List<(string name, double t, double min)>();
            double t0 = 0;
            Clip V(MediaItem m, int track, double start, double dur, double inPt = 0) { var c = new Clip { MediaId = m.Id, Kind = m.Type, Track = track, Start = start, Dur = dur, In = m.Type == ClipKind.Video ? inPt : 0, NoAudio = true }; ed.Clips.Add(c); return c; }
            // every blend mode: a scaled, 85 % opaque test pattern on V2 over the photo on V1 (Normal first: what plain compositing reaches here)
            foreach (var (mode, name, _) in Blend.Modes)
            {
                var c = V(mFg, 1, t0, 1, t0 % 6); c.Blend = mode; c.Scale = 80; c.X = 6; c.Opacity = 85;
                // (the export composites in 4:2:0: Normal reaches ~33 dB on this hard-edged pattern; modes that double the clip's contrast double
                //  that error, and Hard Mix's threshold flips pixels on it — the formulas themselves are exact, part 1)
                double min = mode switch { BlendMode.HardMix => 19, BlendMode.HardLight or BlendMode.VividLight or BlendMode.LinearLight or BlendMode.PinLight => 27.5, BlendMode.Difference => 28, _ => 30 };
                cases.Add(($"blend {name}", t0 + 0.5, min));
                t0 += 1;
            }
            // a title in Screen mode
            var tt = new Clip { Kind = ClipKind.Text, Track = 0, Start = t0, Dur = 1, Role = TextRole.Title, Text = "SCREEN", Style = Themes.Get(ed.ThemeName).For(TextRole.Title).Clone(), Blend = BlendMode.Screen };
            tt.Style!.Size = 160; ed.Clips.Add(tt); V(mFg, 1, t0, 1, 2).Blend = BlendMode.Multiply;
            cases.Add(("blended title over a blended clip", t0 + 0.5, 30)); t0 += 1;
            // crop: static with a soft edge on a scaled clip; keyframed (a wipe); flips with a crop
            var cr = V(mFg, 1, t0, 1, 1); cr.CropL = 10; cr.CropT = 5; cr.CropR = 25; cr.CropB = 15; cr.CropFeather = 20; cr.Scale = 90;
            cases.Add(("crop (soft edge)", t0 + 0.5, 32)); t0 += 1;
            var ca = V(mFg, 1, t0, 2, 2);
            Anim.SetKey(ca, AnimProp.CropR, ca.In, 0); Anim.SetKey(ca, AnimProp.CropR, ca.In + 2, 100); Anim.SetKey(ca, AnimProp.CropT, ca.In, 0); Anim.SetKey(ca, AnimProp.CropT, ca.In + 2, 40);
            cases.Add(("keyframed crop (wipe), 1/3", t0 + 0.7, 32)); cases.Add(("keyframed crop (wipe), 2/3", t0 + 1.3, 32)); t0 += 2;
            var cf = V(mFg, 1, t0, 1, 3); cf.FlipH = true; cf.FlipV = true; cf.CropL = 30; cf.Scale = 70; cf.Rotation = 12;
            cases.Add(("flipped, cropped, rotated", t0 + 0.5, 28)); t0 += 1;   // (28: rotation resamples the 1 px pattern)
            // chroma key over the photo, then a clip with everything: key, cartoon look, grade, flip, crop and a blend mode
            var ck = V(mGr, 1, t0, 1, 1); ck.Key = new ChromaKey { Color = "#00B140", Tolerance = 35, Softness = 25, Spill = 60 };
            double keyT = t0 + 0.5;
            cases.Add(("chroma key", keyT, 32)); t0 += 1;
            var gm = V(mGr, 1, t0, 1, 1); gm.Key = new ChromaKey { Color = "#00B140", Tolerance = 35, Softness = 25, Spill = 60, GarbageL = 40, GarbageR = 15 };
            cases.Add(("garbage matte", t0 + 0.5, 32)); t0 += 1;
            var all = V(mGr, 1, t0, 1, 2); all.Key = new ChromaKey { Color = "#00B140", Tolerance = 40, Softness = 20, Spill = 80, Choke = 1, Soften = 1.5 };
            all.Toon = ToonStyle.Preset(ToonMode.Cartoon); all.Grade = new ColorGrade { Contrast = 20, Saturation = 120 }; all.FlipH = true; all.CropB = 20; all.Blend = BlendMode.Screen; all.Scale = 85;
            cases.Add(("key + look + grade + flip + crop + Screen", t0 + 0.5, 30)); t0 += 1;
            // look effects (the decoder runs the export's graph) and drop shadows (composited from the clip's alpha on both sides)
            var sh = V(mFg, 1, t0, 1, 1); sh.Looks = new LookFx { Sharpen = 60 }; sh.Scale = 70;
            cases.Add(("sharpen", t0 + 0.5, 28)); t0 += 1;   // (28: the preview sharpens at the decode size, then scales to 70 %; the export scales first)
            var gl = V(mBg, 1, t0, 1); gl.Looks = new LookFx { Glow = 70, GlowRadius = 16, GlowThreshold = 55 };
            cases.Add(("glow", t0 + 0.5, 30)); t0 += 1;
            var ds = V(mFg, 1, t0, 1, 2); ds.Scale = 60; ds.X = -10; ds.Shadow = new DropShadow { Opacity = 75, Distance = 22, Softness = 16 };
            cases.Add(("drop shadow", t0 + 0.5, 30)); t0 += 1;
            var df = V(mFg, 1, t0, 1, 3); df.Scale = 55; df.FadeOut = 1; df.Shadow = new DropShadow { Opacity = 90, Distance = 18, Softness = 10, Angle = 200, Color = "#2040A0" };
            cases.Add(("drop shadow on a fading clip", t0 + 0.5, 30)); t0 += 1;
            var dr = V(mFg, 1, t0, 1, 4); dr.Scale = 50; dr.Rotation = -15; dr.Shadow = new DropShadow { Opacity = 70, Distance = 16, Softness = 12 };
            cases.Add(("drop shadow on a rotated clip", t0 + 0.5, 28)); t0 += 1;    // (28: rotation resamples the pattern)
            var grayPic = Gen("gray.png", "-f lavfi -i color=c=0x808080:s=1280x720 -frames:v 1");
            var mGray = await MediaProbe.ProbeAsync(grayPic) ?? throw new Exception("import gray");
            ed.Media.Add(mGray);
            var gr = V(mGray, 1, t0, 1); gr.Looks = new LookFx { Grain = 50 };
            double grainT = t0 + 0.5; t0 += 1;
            var vhs = V(mBg, 1, t0, 1); vhs.Looks = new LookFx { Vhs = 70 };
            cases.Add(("VHS", t0 + 0.5, 28)); t0 += 1;
            var gli = V(mBg, 1, t0, 1); gli.Looks = new LookFx { Glitch = 55 };
            cases.Add(("glitch", t0 + 0.5, 28)); t0 += 1;
            // luma key (a pattern on black: the black goes) and track mattes on V2 masking the photo on V1
            var lumaVid = Gen("lumabg.mp4", "-f lavfi -i testsrc2=s=640x360:r=30:d=6 -vf pad=1280:720:320:180:black -c:v libx264 -crf 10 -pix_fmt yuv420p");
            var mLu = await MediaProbe.ProbeAsync(lumaVid) ?? throw new Exception("import luma");
            ed.Media.Add(mLu);
            var lk = V(mLu, 1, t0, 1, 1); lk.Key = new ChromaKey { Luma = true, Level = 0, Tolerance = 12, Softness = 8, Spill = 0 };
            cases.Add(("luma key (black)", t0 + 0.5, 30)); double lumaT = t0 + 0.5; t0 += 1;
            var m1 = V(mFg, 1, t0, 1, 2); m1.Mask = MaskMode.Reveal; m1.Scale = 80;
            cases.Add(("track matte: brightness, reveal", t0 + 0.5, 30)); t0 += 1;
            var m2 = V(mFg, 1, t0, 1, 3); m2.Mask = MaskMode.CutOut;
            cases.Add(("track matte: brightness, cut-out", t0 + 0.5, 30)); t0 += 1;
            var m3 = V(mGr, 1, t0, 1, 2); m3.Mask = MaskMode.Reveal; m3.MatteLuma = false; m3.Key = new ChromaKey { Color = "#00B140", Tolerance = 35, Softness = 25 }; m3.FadeOut = 1;
            cases.Add(("track matte: alpha of a keyed clip, fading", t0 + 0.5, 30)); t0 += 1;
            // anchor point (pivot of scale and rotation) and corner pin
            var an = V(mFg, 1, t0, 1, 2); an.Scale = 60; an.Rotation = 25; an.AnchorX = -50; an.AnchorY = -50;
            {
                // the pivot (the picture's top-left corner) stays where it is at 100 % / 0°
                var (x0, y0, w0, h0, _) = Editor.Placement(mFg, new Clip { MediaId = mFg.Id, Kind = ClipKind.Video, Track = 1, Dur = 1 }, 0);
                var (x1, y1, w1, h1, r1) = Editor.Placement(mFg, an, an.Start);
                double a = r1 * Math.PI / 180, cx = x1 + w1 / 2, cy = y1 + h1 / 2;
                double tlx = cx + (-w1 / 2) * Math.Cos(a) - (-h1 / 2) * Math.Sin(a), tly = cy + (-w1 / 2) * Math.Sin(a) + (-h1 / 2) * Math.Cos(a);
                Check(Math.Abs(tlx - x0) < 1.5 && Math.Abs(tly - y0) < 1.5 && Math.Abs(w1 - w0 * 0.6) < 1, $"anchor at the top-left corner: the corner stays at ({x0:0},{y0:0}) → ({tlx:0.0},{tly:0.0}) at 60 % and 25°");
            }
            cases.Add(("anchor point (top-left pivot), scaled and rotated", t0 + 0.5, 28)); t0 += 1;   // (28: rotation resamples the pattern)
            var an2 = V(mFg, 1, t0, 1, 3); an2.Scale = 70; an2.Rotation = 10;
            Anim.SetKey(an2, AnimProp.AnchorX, an2.In, 50); Anim.SetKey(an2, AnimProp.AnchorX, an2.In + 1, -50);
            cases.Add(("keyframed anchor point", t0 + 0.5, 28)); t0 += 1;
            var pn = V(mFg, 1, t0, 1, 4); pn.Pin = new CornerPin { TLX = 20, TLY = 15, TRX = 75, TRY = 5, BLX = 25, BLY = 85, BRX = 85, BRY = 70 };
            cases.Add(("corner pin", t0 + 0.5, 30)); t0 += 1;
            var pk = V(mGr, 1, t0, 1, 2); pk.Key = new ChromaKey { Color = "#00B140", Tolerance = 35, Softness = 25 }; pk.Pin = new CornerPin { TLX = 10, TLY = 20, TRX = 60, TRY = 10, BLX = 15, BLY = 90, BRX = 70, BRY = 95 };
            cases.Add(("corner pin on a keyed clip", t0 + 0.5, 30)); t0 += 1;
            // keyframed corner pin: the preview's shader vs the export's per-frame perspective expressions — eased and linear keys, a flip
            var pkf = V(mFg, 1, t0, 2, 1); pkf.Pin = new CornerPin { TLX = 10, TLY = 10, TRX = 90, TRY = 5, BLX = 5, BLY = 95, BRX = 95, BRY = 90 };
            Anim.SetKey(pkf, AnimProp.PinTLX, pkf.In, 0).Out = KeyType.Bezier; Anim.SetKey(pkf, AnimProp.PinTLX, pkf.In + 2, 45).In = KeyType.Bezier;
            Anim.SetKey(pkf, AnimProp.PinBRY, pkf.In + 0.3, 100); Anim.SetKey(pkf, AnimProp.PinBRY, pkf.In + 1.7, 60);
            Anim.SetKey(pkf, AnimProp.PinTRX, pkf.In, 70); Anim.SetKey(pkf, AnimProp.PinTRX, pkf.In + 2, 110);
            cases.Add(("keyframed corner pin, first frame", t0, 30));
            cases.Add(("keyframed corner pin, eased middle", t0 + 0.9, 30));
            cases.Add(("keyframed corner pin, near the end", t0 + 1.8, 30)); t0 += 2;
            var pf = V(mFg, 1, t0, 1, 2); pf.FlipH = true; pf.Pin = new CornerPin { TLX = 20, TLY = 20, TRX = 80, TRY = 10, BLX = 20, BLY = 80, BRX = 70, BRY = 95 };
            Anim.SetKey(pf, AnimProp.PinTLY, pf.In, 0); Anim.SetKey(pf, AnimProp.PinTLY, pf.In + 1, 50);
            cases.Add(("keyframed corner pin, flipped", t0 + 0.4, 30)); t0 += 1;
            // lens distortion: the preview's shader (lenscorrection's model) vs lenscorrection — scaled, on a keyed clip (the matte warped with
            // the picture), keyframed (sendcmd per frame), before a corner pin
            var ln = V(mFg, 1, t0, 1, 2); ln.Scale = 80; ln.Lens = new LensDistort { Curvature = -30, Edge = 8 };
            cases.Add(("lens distortion (barrel fix), scaled", t0 + 0.5, 30)); t0 += 1;
            var lk2 = V(mGr, 1, t0, 1, 3); lk2.Key = new ChromaKey { Color = "#00B140", Tolerance = 35, Softness = 25 }; lk2.Lens = new LensDistort { Curvature = 40, CenterX = 10 };
            cases.Add(("lens distortion (bulge, off-centre) on a keyed clip", t0 + 0.5, 30)); t0 += 1;
            var patPic = Gen("pattern.png", "-f lavfi -i testsrc2=s=1280x720 -frames:v 1");
            var mPat = await MediaProbe.ProbeAsync(patPic) ?? throw new Exception("import pattern");
            ed.Media.Add(mPat);
            var lkf = V(mPat, 1, t0, 2); lkf.Lens = new LensDistort();       // (a still: any mismatch is the lens's timing or shape, not the video's)
            Anim.SetKey(lkf, AnimProp.LensCurve, lkf.In, -50).Out = KeyType.Bezier; Anim.SetKey(lkf, AnimProp.LensCurve, lkf.In + 2, 50).In = KeyType.Bezier;
            Anim.SetKey(lkf, AnimProp.LensCY, lkf.In + 0.5, -15); Anim.SetKey(lkf, AnimProp.LensCY, lkf.In + 1.5, 15);
            cases.Add(("keyframed lens distortion, first frame", t0, 30));
            cases.Add(("keyframed lens distortion, eased middle", t0 + 0.9, 30));
            cases.Add(("keyframed lens distortion, near the end", t0 + 1.8, 29)); t0 += 2;   // (29: a 48 % bulge minifies the 1 px checker — the software renderer samples a little off by itself; on screen the shader is within 1 level)
            var lp2 = V(mFg, 1, t0, 1, 2); lp2.Lens = new LensDistort { Curvature = -25 }; lp2.FlipH = true;
            lp2.Pin = new CornerPin { TLX = 15, TLY = 10, TRX = 80, TRY = 15, BLX = 10, BLY = 90, BRX = 90, BRY = 80 };
            cases.Add(("lens distortion + corner pin, flipped", t0 + 0.5, 28)); t0 += 1;   // (28: the export resamples twice, the shader once)
            V(mBg, 2, 0, t0);                                                // the photo under everything (V1)

            var mp4 = Path.Combine(dir, "composite.mp4");
            var opts = new ExportSettings { Quality = 10, Audio = "none" };
            var titles = TitleVisual.PrepareExport(ed, Path.Combine(dir, "titles"), opts.OutW, opts.OutH);
            var sw = Stopwatch.StartNew();
            var err = await Exporter.RunAsync(ff, ed, mp4, new Progress<double>(), CancellationToken.None, titles, null, opts);
            double tMain = sw.Elapsed.TotalSeconds;
            Check(err == null, $"export with blend modes, crops, flips and keys ({sw.Elapsed.TotalSeconds:0.0} s for {t0:0} s)" + (err == null ? "" : ": " + err));
            if (err == null)
            {
                var pv = new Preview { Ed = ed, Width = Editor.W, Height = Editor.H, Background = Brushes.Black };
                var shots = new List<(string name, double t, double psnr, byte[] pa)>();
                foreach (var (name, t1, min) in cases)
                {
                    double t = Math.Round(t1 * 30) / 30;
                    var swp = Stopwatch.StartNew();
                    do { pv.Sync(t, false); await Task.Delay(20); } while ((!pv.Settled(t) || swp.ElapsedMilliseconds < 400) && swp.ElapsedMilliseconds < 8000);
                    pv.Sync(t, false);
                    pv.Measure(new Size(Editor.W, Editor.H)); pv.Arrange(new Rect(0, 0, Editor.W, Editor.H)); pv.UpdateLayout();
                    var rtb = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
                    var pa = new byte[Editor.W * Editor.H * 4]; rtb.CopyPixels(pa, Editor.W * 4, 0);
                    var pb = FxTest.Frame(ff, mp4, t - 0.001);
                    var (psnr, worst) = FxTest.Compare(pa, pb);
                    shots.Add((name, t, psnr, pa));
                    var (sx, sy) = SeqTest.Shift(pa, pb, Editor.W, Editor.H);
                    string extra = "";
                    bool ok = psnr >= min && Math.Abs(sx) <= 1 && Math.Abs(sy) <= 1;
                    if (name.StartsWith("keyframed lens"))
                    {   // the export's frame at t matches best: the commands land on the frames the preview evaluates
                        double before = FxTest.Compare(pa, FxTest.Frame(ff, mp4, t - 1 / 30.0 - 0.001)).psnr, after = FxTest.Compare(pa, FxTest.Frame(ff, mp4, t + 1 / 30.0 - 0.001)).psnr;
                        extra = $"; a frame earlier {before:0.0} dB, later {after:0.0} dB";
                        ok &= t1 == lkf.Start || psnr >= before && psnr >= after;
                    }
                    if (name == "chroma key")
                    {
                        // the screen is gone: strongly green pixels before (the source frame) and after (the export)
                        var src = FxTest.Frame(ff, green, 1.5);
                        double before = Greenish(src), after = Greenish(pb), photo = Greenish(FxTest.Frame(ff, bgPic, 0));   // (the photo has its own greens)
                        extra = $"; green screen pixels {before:P0} → {after:P1} (the photo alone {photo:P1})";
                        ok &= before > 0.4 && after < photo + 0.01;
                    }
                    Check(ok, $"{name}: preview vs export PSNR {psnr:0.0} dB (≥ {min}), worst block {worst:0.0}/255, shift ({sx},{sy}){extra}");
                    FxTest.Save(FxTest.SideBySide(pa, pb), Path.Combine(dir, $"comp_{string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_').Replace("+", "")}.png"));
                }
                // grain: random by nature (its pattern differs) — the same strength on a flat grey picture
                {
                    double t = Math.Round(grainT * 30) / 30;
                    var swp = Stopwatch.StartNew();
                    do { pv.Sync(t, false); await Task.Delay(20); } while ((!pv.Settled(t) || swp.ElapsedMilliseconds < 2500) && swp.ElapsedMilliseconds < 8000);
                    pv.Sync(t, false); pv.Measure(new Size(Editor.W, Editor.H)); pv.Arrange(new Rect(0, 0, Editor.W, Editor.H)); pv.UpdateLayout();
                    var rtb = new RenderTargetBitmap(Editor.W, Editor.H, 96, 96, PixelFormats.Pbgra32); rtb.Render(pv);
                    var pa = new byte[Editor.W * Editor.H * 4]; rtb.CopyPixels(pa, Editor.W * 4, 0);
                    var pb = FxTest.Frame(ff, mp4, t - 0.001);
                    double na = Noise(pa), nb = Noise(pb), plain = 0.5;
                    Check(na > 3 && nb > 3 && na / nb is > 0.7 and < 1.4, $"film grain: noise {na:0.0} in the preview, {nb:0.0} in the export (flat grey: {plain} without)");
                }
                // the 4:4:4 composite: the same timeline with a 4:2:0 composite (H.264 out: what it costs and gains there) and as a ProRes 4444
                // master (no subsampling anywhere: what the full-colour composite delivers) — every case against the same preview frames
                {
                    var mp4b = Path.Combine(dir, "composite420.mp4"); var mov = Path.Combine(dir, "composite4444.mov");
                    var sw4 = Stopwatch.StartNew();
                    var e420 = await Exporter.RunAsync(ff, ed, mp4b, new Progress<double>(), CancellationToken.None, titles, null, new ExportSettings { Quality = 10, Audio = "none", Full444 = false });
                    double t420 = sw4.Elapsed.TotalSeconds; sw4.Restart();
                    var e444 = await Exporter.RunAsync(ff, ed, mov, new Progress<double>(), CancellationToken.None, titles, null, new ExportSettings { Video = "prores4444", Container = "mov", Audio = "none" });
                    double tPro = sw4.Elapsed.TotalSeconds;
                    if (e420 != null || e444 != null) Check(false, "4:2:0 / ProRes 4444 exports: " + (e420 ?? e444));
                    else
                    {
                        double sMain = 0, s420 = 0, sPro = 0; int better = 0, worse = 0; var rows = new List<string>();
                        foreach (var (name, t, psnr, pa) in shots)
                        {
                            double a420 = FxTest.Compare(pa, FxTest.Frame(ff, mp4b, t - 0.001)).psnr, aPro = FxTest.Compare(pa, FxTest.Frame(ff, mov, t - 0.001)).psnr;
                            sMain += psnr; s420 += a420; sPro += aPro;
                            if (aPro > a420 + 0.3) better++; if (aPro < a420 - 0.3) worse++;
                            rows.Add($"  {name}: 4:2:0 composite {a420:0.0}, 4:4:4 composite (H.264) {psnr:0.0}, ProRes 4444 master {aPro:0.0} dB");
                        }
                        int n = Math.Max(1, shots.Count);
                        Check(sPro / n > s420 / n + 1 && sMain / n >= s420 / n - 0.1 && worse <= shots.Count / 10,
                              $"4:4:4 composite: mean preview-vs-export PSNR {s420 / n:0.0} dB (4:2:0 composite) → {sMain / n:0.0} (4:4:4, H.264 4:2:0 out) → {sPro / n:0.0} (ProRes 4444 master); " +
                              $"better in {better} of {shots.Count} cases, worse in {worse}; export {t420:0.0} s (4:2:0) vs {tMain:0.0} s (4:4:4), ProRes 4444 {tPro:0.0} s");
                        log.AddRange(rows);
                    }
                }
            }
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "compositetest.txt"), log);
        await Task.CompletedTask;
        return fails == 0 ? 0 : 1;
    }

    /// <summary>Renders the clip pixels over the picture below through the blend shader (256×256), returns the worst channel difference from
    /// b + (B(t, b) − b)·α and how many pixels were off.</summary>
    /// <summary>A 320×180 still pinned by the preview's shader (in a flip box, as the preview's layer) — rendered by the software renderer
    /// (RenderTargetBitmap) or shown in a window and captured from the screen (GPU) — against ffmpeg (Pin.Graph, hflip), colours over black:
    /// worst channel difference, mean difference, share of pixels off by 3 or more. pin null: the identity warp against the picture itself.</summary>
    static async Task<(int max, double mean, double bad)> PinDiff(string ff, string dir, string still, CornerPin? pin, bool flip, bool screen, double half, LensDistort? lens = null)
    {
        const int w = 320, h = 180;
        if (Pin.Shader() is not { } ps) return (255, 255, 1);
        var src = new BitmapImage(); src.BeginInit(); src.UriSource = new Uri(still); src.CacheOption = BitmapCacheOption.OnLoad; src.EndInit(); src.Freeze();
        byte[] want;
        var pc = new Clip { Kind = ClipKind.Image, Dur = 1, Pin = pin, Lens = lens, FlipH = flip };
        if (pin == null && lens == null) { want = new byte[w * h * 4]; new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0).CopyPixels(want, w * 4, 0); }
        else
        {
            var raw = Path.Combine(dir, "pin_ff.raw");
            try { File.Delete(raw); } catch { }
            Run(ff, $"-y -v error -i \"{still}\" -filter_complex \"[0]format=rgba,{Warp.Graph(pc, w, h, "", null)}{(flip ? ",hflip" : "")},format=rgba\" -frames:v 1 -f rawvideo \"{raw}\"");
            var fx = File.Exists(raw) ? File.ReadAllBytes(raw) : Array.Empty<byte>();
            if (fx.Length < w * h * 4) return (255, 255, 1);
            want = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                int a = fx[i * 4 + 3];
                for (int ch = 0; ch < 3; ch++) want[i * 4 + 2 - ch] = (byte)((fx[i * 4 + ch] * a + 127) / 255);   // (rgba → BGRA over black)
                want[i * 4 + 3] = 255;
            }
        }
        var box = new Grid { Width = w, Height = h, Background = Brushes.Black };
        if (pin == null && lens == null)
        {   // the shader alone, identity warp
            var img = new Image { Source = src, Width = w, Height = h, Stretch = Stretch.Fill };
            var eff = new PinEffect(ps); eff.SetMatrix(new double[] { w, 0, -half, 0, h, -half, 0, 0, 1 }, w, h, half);
            img.Effect = eff; box.Children.Add(img);
        }
        else
        {   // the preview's own layer (MaskFxHost: flip box, picture, pin)
            var fxh = new MaskFxHost(); fxh.Img.Source = src; fxh.Host.Visibility = Visibility.Visible; fxh.Host.Width = w; fxh.Host.Height = h;

            fxh.Flip(flip, false);
            double keep = Pin.Half; Pin.Half = half; fxh.Pin(pc, 0, null, w, h); Pin.Half = keep;
            box.Children.Add(fxh.Host);
        }
        byte[]? pv;
        if (screen) pv = await OnScreen(box, w, h);
        else
        {
            box.Measure(new Size(w, h)); box.Arrange(new Rect(0, 0, w, h)); box.UpdateLayout();
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); rtb.Render(box);
            pv = new byte[w * h * 4]; rtb.CopyPixels(pv, w * 4, 0);
        }
        if (pv == null) return (255, 255, 1);
        int max = 0; long sum = 0, bad = 0;
        for (int i = 0; i < w * h; i++)
        {
            int worst = 0;
            for (int ch = 0; ch < 3; ch++) { int d = Math.Abs(pv[i * 4 + ch] - want[i * 4 + ch]); sum += d; worst = Math.Max(worst, d); }
            max = Math.Max(max, worst); if (worst >= 3) bad++;
        }
        // picture: preview | export | difference ×8
        var sheet = new byte[w * 3 * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4, o = (y * w * 3 + x) * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    sheet[o + ch] = pv[i + ch]; sheet[o + w * 4 + ch] = want[i + ch];
                    sheet[o + w * 8 + ch] = (byte)Math.Min(255, Math.Abs(pv[i + ch] - want[i + ch]) * 8);
                }
                sheet[o + 3] = sheet[o + w * 4 + 3] = sheet[o + w * 8 + 3] = 255;
            }
        FxTest.Save(BitmapSource.Create(w * 3, h, 96, 96, PixelFormats.Bgra32, null, sheet, w * 12),
                    Path.Combine(dir, $"pin_{(screen ? "screen" : "sw")}_{(pin == null && lens == null ? $"identity{half}" : $"{(flip ? "flip_" : "")}{(lens != null ? $"lens{lens.Curvature:0}_" : "")}{pin?.TLX ?? 0:0}")}.png"));
        return (max, sum / (3.0 * w * h), bad / (double)(w * h));
    }

    /// <summary>Shows the element in a borderless window at 1 device pixel per unit, captures it from the screen (what the GPU drew), closes it.</summary>
    internal static async Task<byte[]?> OnScreen(FrameworkElement el, int w, int h)
    {
        var win = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, ShowActivated = false,
                               Left = 64, Top = 64, Width = 50, Height = 50, Background = Brushes.Black, Title = "CutPro pin probe" };
        var mode = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;          // (closing the only window must not end the test)
        try
        {
            win.Show();
            var dpi = VisualTreeHelper.GetDpi(win);
            var root = new Grid { Background = Brushes.Black, LayoutTransform = new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            root.Children.Add(el);
            win.Content = root; win.SizeToContent = SizeToContent.WidthAndHeight;
            win.UpdateLayout();
            await Task.Delay(500);
            var cap = new FormatConvertedBitmap(ScreenGrab.Window(win), PixelFormats.Bgra32, null, 0);
            if (cap.PixelWidth < w || cap.PixelHeight < h) return null;
            var px = new byte[w * h * 4];
            cap.CopyPixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
            return px;
        }
        finally { win.Close(); Application.Current.ShutdownMode = mode; }
    }

    static (int max, int bad) ShaderDiff(BlendMode mode, byte[] topPx, byte[] botPx, int alpha)
    {
        var ps = Blend.Shader(mode);
        if (ps == null) return (255, 65536);
        BitmapSource Bmp(byte[] px) { var wb = new WriteableBitmap(256, 256, 96, 96, PixelFormats.Pbgra32, null); wb.WritePixels(new Int32Rect(0, 0, 256, 256), px, 1024, 0); wb.Freeze(); return wb; }
        var root = new Canvas { Width = 256, Height = 256, Background = Brushes.Black };
        var below = new Image { Source = Bmp(botPx), Width = 256, Height = 256, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(below, BitmapScalingMode.NearestNeighbor);
        root.Children.Add(below);
        var host = new Canvas { Width = 256, Height = 256, ClipToBounds = true, Background = Brushes.Transparent };
        var img = new Image { Source = Bmp(topPx), Width = 256, Height = 256, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        host.Children.Add(img);
        var vb = new VisualBrush(below) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 256, 256) };
        host.Effect = new BlendEffect(mode, ps) { Below = vb };
        root.Children.Add(host);
        root.Measure(new Size(256, 256)); root.Arrange(new Rect(0, 0, 256, 256)); root.UpdateLayout();
        var rtb = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32); rtb.Render(root);
        var got = new byte[256 * 256 * 4]; rtb.CopyPixels(got, 1024, 0);
        int max = 0, bad = 0;
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                int o = (y * 256 + x) * 4;
                // (half transparent: WPF hands the shader premultiplied 8-bit pixels, so the clip's own colour comes back ±1 — threshold modes
                // such as Hard Mix may flip there; compare with the range over t ± 1)
                int t = alpha == 255 ? x : (int)Math.Round(topPx[o] * 255.0 / alpha), dt = alpha == 255 ? 0 : 1;
                double lo = double.MaxValue, hi = double.MinValue;
                for (int tt = Math.Max(0, t - dt); tt <= Math.Min(255, t + dt); tt++)
                {
                    double w = y + (Blend.Reference(mode, tt, y) - y) * alpha / 255.0;
                    lo = Math.Min(lo, w); hi = Math.Max(hi, w);
                }
                double g = got[o + 1];
                int d = g < lo ? (int)Math.Ceiling(lo - g - 0.5001) : g > hi ? (int)Math.Ceiling(g - hi - 0.5001) : 0;
                if (d > 0) bad++; max = Math.Max(max, d);
            }
        return (max, bad);
    }

    /// <summary>Grain strength: RMS of the green channel's difference from its 3×3 neighbourhood mean, in the middle of the frame (BGRA).</summary>
    static double Noise(byte[] px)
    {
        int W = Editor.W, H = Editor.H; double s = 0; int n = 0;
        for (int y = H / 4; y < 3 * H / 4; y += 2)
            for (int x = W / 4; x < 3 * W / 4; x += 2)
            {
                double m = 0;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) m += px[((y + dy) * W + x + dx) * 4 + 1];
                double d = px[(y * W + x) * 4 + 1] - m / 9; s += d * d; n++;
            }
        return Math.Sqrt(s / n);
    }

    /// <summary>Share of pixels that are clearly green-screen green (BGRA).</summary>
    static double Greenish(byte[] px)
    {
        int n = 0, all = px.Length / 4;
        for (int i = 0; i < px.Length; i += 4) { int b = px[i], g = px[i + 1], r = px[i + 2]; if (g > 110 && g > r + 60 && g > b + 40) n++; }
        return n / (double)all;
    }

    static void Ppm(string file, Func<int, int, byte> v)
    {
        using var fs = File.Create(file);
        var head = System.Text.Encoding.ASCII.GetBytes("P6\n256 256\n255\n"); fs.Write(head);
        var px = new byte[256 * 256 * 3];
        for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++) { byte b = v(x, y); int o = (y * 256 + x) * 3; px[o] = px[o + 1] = px[o + 2] = b; }
        fs.Write(px);
    }

    static void Run(string ff, string args) { using var p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!; p.StandardError.ReadToEnd(); p.WaitForExit(); }
}
