using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Path = System.Windows.Shapes.Path;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace CutPro;

/// <summary>
/// Object-mask effects on one clip layer in the preview. The layer is a Grid: the clip image, then one overlay per mask
/// (blurred / pixelated / grey / darkened / black / tinted version of the frame) whose opacity mask is the object's
/// feathered outline for the current source frame. "Cut out" masks the whole layer instead. The exporter renders the
/// same outlines (MaskExport) and does the same maths with ffmpeg (maskedmerge / alphamerge).
/// </summary>
public sealed class MaskFxHost
{
    public readonly Grid Host = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    public readonly Image Img = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    readonly Grid fx = new() { IsHitTestVisible = false };
    readonly Dictionary<string, Overlay> overlays = new();
    WriteableBitmap? pixBmp, grayBmp;
    object? derivedFrom; long derivedStamp = -1; int derivedBlock;
    byte[]? srcPx;

    sealed class Overlay
    {
        public FrameworkElement El = null!;
        public Image? Pic;                                                  // (picture overlays: El = a wrapper with the mask and blur; Pic may carry the pin)
        public string Key = "";
        public MaskFx Fx;
    }

    readonly Rectangle vig = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    string vigKey = "";

    // Host > crop box (crop opacity mask, in the shown picture's space) > flip box (mirrors the picture, its mask effects and vignette)
    readonly Grid cropBox = new() { IsHitTestVisible = false }, flipBox = new() { IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5) };
    public MaskFxHost()
    {
        fx.ClipToBounds = true;
        flipBox.Children.Add(Img); flipBox.Children.Add(fx); flipBox.Children.Add(vig);
        cropBox.Children.Add(flipBox); Host.Children.Add(cropBox);
    }

    bool flipX, flipY;
    /// <summary>Horizontal / vertical flip of the picture (the export flips after masks and the vignette, before the crop).</summary>
    public void Flip(bool h, bool v)
    {
        if (h == flipX && v == flipY) return;
        flipX = h; flipY = v;
        flipBox.RenderTransform = h || v ? new ScaleTransform(h ? -1 : 1, v ? -1 : 1) : Transform.Identity;
    }

    string pinKey = ""; double[]? pinM; int pinW, pinH; Transform? pinTurn; bool pinPin; (double, double, double, double)? pinLens;
    /// <summary>The picture's warps at t (lens distortion, corner pin) as its shader (<see cref="PinEffect"/>, the export's geometry on its output
    /// pixel grid w×h — a pinned clip covers the frame: default the sequence's output size; a lens alone works at the clip's export size), also on
    /// picture mask overlays (the export masks the warped frame); off when the clip has none or its decoder warps it (<see cref="Warp.InDecoder"/>).
    /// A flip is done by the shader (the picture is turned back inside the flip box): WPF's software renderer resamples an effect drawn under a mirror.</summary>
    public void Pin(Clip c, double t, MediaItem? m, int w = 0, int h = 0)
    {
        if (Distorts.Active(c) && Warp.InShader(c)) { DistortOn(c, t, m); return; }
        if (distortOn) DistortOff();
        if (!Warp.InShader(c))
        {
            if (pinM == null) return;
            pinM = null; pinKey = "";
            PinOff(Img); foreach (var o in overlays.Values) if (o.Pic != null) PinOff(o.Pic);
            return;
        }
        bool pinned = CutPro.Pin.Active(c);
        if (w <= 0)
        {
            if (pinned) { w = Editor.OutW; h = Editor.OutH; }
            else (w, h) = Exporter.ClipSize(m, c, Editor.OutH / 720.0);
        }
        w = Math.Max(2, w); h = Math.Max(2, h);
        var p = pinned ? CutPro.Pin.At(c, t) : null;
        (double, double, double, double)? lens = CutPro.Lens.Active(c) ? CutPro.Lens.At(c, t) : null;
        double half = CutPro.Pin.Half;
        string key = (p != null ? CutPro.Pin.Sig(p) : "-") + "|" + (lens is { } lv ? CutPro.Lens.Sig(lv) : "-") + FormattableString.Invariant($"|{w}x{h}|{flipX}{flipY}|{half}");
        if (key == pinKey) return;
        pinKey = key; pinW = w; pinH = h; pinPin = pinned; pinLens = lens;
        pinM = CutPro.Pin.Mirror(p != null ? CutPro.Pin.Inverse(p, w, h, 1 - half) : PinEffect.Plain(w, h, half), flipX, flipY, w, h, half);
        pinTurn = flipX || flipY ? new ScaleTransform(flipX ? -1 : 1, flipY ? -1 : 1) : null;
        pinTurn?.Freeze();
        PinOn(Img); foreach (var o in overlays.Values) if (o.Pic != null) PinOn(o.Pic);
    }
    bool distortOn;
    /// <summary>The picture's distortion (twirl / wave / spherize / mesh) as its shader at the clip's export size — on the picture and on picture mask
    /// overlays, like the pin; t − start drives the wave.</summary>
    void DistortOn(Clip c, double t, MediaItem? m)
    {
        if (pinM != null) { pinM = null; pinKey = ""; PinOff(Img); foreach (var o in overlays.Values) if (o.Pic != null) PinOff(o.Pic); }
        var (w, h) = Exporter.ClipSize(m, c, Editor.OutH / 720.0);
        w = Math.Max(2, w); h = Math.Max(2, h);
        var d = Distorts.At(c, t);
        void On(Image im)
        {
            var e = im.Effect as DistortEffect;
            if (e == null || e.Kind != d.Kind) e = new DistortEffect(d.Kind, Distorts.Shader(d.Kind)!);
            e.Set(d, w, h, CutPro.Pin.Half, t - c.Start);
            if (!ReferenceEquals(im.Effect, e)) im.Effect = e;
        }
        On(Img); foreach (var o in overlays.Values) if (o.Pic != null) On(o.Pic);
        distortOn = true;
    }
    void DistortOff()
    {
        distortOn = false;
        if (Img.Effect is DistortEffect) Img.Effect = null;
        foreach (var o in overlays.Values) if (o.Pic?.Effect is DistortEffect) o.Pic.Effect = null;
    }
    void PinOn(Image im)
    {
        var e = im.Effect as PinEffect ?? new PinEffect(CutPro.Pin.Shader()!);
        e.SetMatrix(pinM!, pinW, pinH, CutPro.Pin.Half, pinPin, pinLens);
        if (!ReferenceEquals(im.Effect, e)) im.Effect = e;
        im.RenderTransformOrigin = new Point(0.5, 0.5);
        im.RenderTransform = pinTurn ?? Transform.Identity;
    }
    static void PinOff(Image im) { if (im.Effect is PinEffect) im.Effect = null; im.RenderTransform = Transform.Identity; }

    WriteableBitmap? cropBmp; byte[]? cropPx; string cropKey = ""; ImageBrush? cropBrush;
    /// <summary>The clip's crop at t on a layer drawn at w×h (logical px): an opacity mask from <see cref="CutPro.Crop.Fill"/>, rebuilt only
    /// when the crop or the size changes.</summary>
    public void Crop(Clip c, double t, double w, double h)
    {
        if (!CutPro.Crop.Applied(c)) { if (cropKey != "") { cropKey = ""; cropBox.OpacityMask = null; } return; }
        int mw = Math.Clamp((int)Math.Round(w), 2, 4096), mh = Math.Clamp((int)Math.Round(h), 2, 4096);
        var s = CutPro.Crop.At(c, t);
        string key = CutPro.Crop.Sig(s, c.CropFeather, mw, mh) + FormattableString.Invariant($"|{w:0.##}x{h:0.##}");
        if (key == cropKey) return;
        cropKey = key;
        if (cropBmp == null || cropBmp.PixelWidth != mw || cropBmp.PixelHeight != mh)
        {
            cropBmp = new WriteableBitmap(mw, mh, 96, 96, PixelFormats.Pbgra32, null); cropPx = new byte[mw * mh * 4];
            cropBrush = new ImageBrush(cropBmp) { Stretch = Stretch.Fill, ViewportUnits = BrushMappingMode.Absolute };
        }
        cropBrush!.Viewport = new Rect(0, 0, w, h);
        CutPro.Crop.Fill(s, c.CropFeather, mw, mh, cropPx!, 4);
        cropBmp.WritePixels(new Int32Rect(0, 0, mw, mh), cropPx, mw * 4, 0);
        if (!ReferenceEquals(cropBox.OpacityMask, cropBrush)) cropBox.OpacityMask = cropBrush;
    }

    /// <summary>The Lumetri vignette over the clip (above the mask effects; the export overlays the same picture last).</summary>
    public void Vignette(ColorGrade? g, double w, double h)
    {
        var key = g is { HasVignette: true } ? FormattableString.Invariant($"{g.Vignette}|{g.VigMidpoint}|{g.VigFeather}|{g.VigRoundness}|{w:0.#}|{h:0.#}") : "";
        if (key == vigKey) return;
        vigKey = key;
        vig.Fill = key == "" ? null : Grade.VignetteBrush(g, w, h);
        vig.Visibility = key == "" ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Applies the clip's masks for source frame `frame` on a layer drawn at vw×vh. stamp changes whenever the
    /// image content changes (for the CPU-derived pixelate / grey images).</summary>
    public void Update(Clip c, Editor ed, int frame, double vw, double vh, long stamp)
    {
        var masks = c.Masks.Where(m => m.Enabled).ToList();
        var live = new HashSet<string>();
        bool cutDone = false;
        foreach (var m in masks)
        {
            var track = ed.MaskTracks.GetValueOrDefault(m.Id);
            var polys = track?.At(frame);
            // the outline brush is rebuilt only when this frame's outline or the mask parameters change
            string key = $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(polys)}|{vw:0.#}|{vh:0.#}|{m.Expand}|{m.Invert}|{m.Feather}|{flipX}{flipY}";
            Brush MaskBrush() => Brush(MaskGeometry.Area(MaskGeometry.Outline(polys, vw, vh, m.Expand), vw, vh, m.Invert, m.Feather), vw, vh, m.Feather);
            if (m.Effect == MaskFx.CutOut)
            {
                if (cutDone) continue;                                             // only the first cut-out applies (same in export)
                cutDone = true;
                if (key != cutKey) { cutKey = key; cutBrush = Flipped(MaskBrush()); }   // (on the host: outside the flip box)
                continue;
            }
            live.Add(m.Id);
            if (!overlays.TryGetValue(m.Id, out var o) || o.Fx != m.Effect)
            {
                if (o != null) fx.Children.Remove(o.El);
                o = new Overlay { Fx = m.Effect };
                o.El = m.Effect switch
                {
                    MaskFx.Blur or MaskFx.Pixelate or MaskFx.Desaturate or MaskFx.Grade => new Grid { Children = { (o.Pic = new Image { Stretch = Stretch.Fill }) } },
                    _ => new Rectangle(),
                };
                overlays[m.Id] = o;
                if (o.Pic != null && pinM != null) PinOn(o.Pic);              // (a pinned picture: the overlay shows it pinned too, like the export's masks)
                fx.Children.Add(o.El);
            }
            // effect appearance
            switch (m.Effect)
            {
                case MaskFx.Blur:
                    o.Pic!.Source = Img.Source;
                    if (o.El.Effect is BlurEffect b) b.Radius = m.Amount; else o.El.Effect = new BlurEffect { Radius = m.Amount, KernelType = KernelType.Gaussian };
                    break;
                case MaskFx.Pixelate:
                    o.Pic!.Source = Derived(true, stamp, vw, m.Amount);
                    RenderOptions.SetBitmapScalingMode(o.Pic, BitmapScalingMode.NearestNeighbor);
                    break;
                case MaskFx.Desaturate:
                    o.Pic!.Source = Derived(false, stamp, vw, 0);
                    break;
                case MaskFx.Grade:
                    o.Pic!.Source = Graded(m.Id, CutPro.Grade.For(m.Grade), stamp);
                    break;
                case MaskFx.Darken: ((Rectangle)o.El).Fill = Brushes.Black; o.El.Opacity = Math.Clamp(m.Amount / 100, 0, 1); break;
                case MaskFx.Redact: ((Rectangle)o.El).Fill = Brushes.Black; o.El.Opacity = 1; break;
                case MaskFx.Tint:
                    ((Rectangle)o.El).Fill = new SolidColorBrush(TitleVisual.ParseColor(m.TintColor, Colors.Yellow));
                    o.El.Opacity = Math.Clamp(m.Amount / 100, 0, 1);
                    break;
            }
            if (key != o.Key) { o.Key = key; o.El.OpacityMask = MaskBrush(); }
        }
        foreach (var id in overlays.Keys.Where(k => !live.Contains(k)).ToList()) { fx.Children.Remove(overlays[id].El); overlays.Remove(id); graded.Remove(id); }
        if (!cutDone) { cutKey = null; cutBrush = null; }
        var want = cutBrush ?? Extra;                                          // a cut-out wins over a transition mask
        if (!ReferenceEquals(Host.OpacityMask, want)) Host.OpacityMask = want;
    }
    string? cutKey; Brush? cutBrush;
    /// <summary>A cut-out outline (frame coordinates of the unflipped picture) mirrored like the picture.</summary>
    Brush Flipped(Brush b)
    {
        if (!flipX && !flipY) return b;
        var f = b.Clone(); f.RelativeTransform = new ScaleTransform(flipX ? -1 : 1, flipY ? -1 : 1, 0.5, 0.5); f.Freeze();
        return f;
    }
    public Brush? CutBrush => cutBrush;
    /// <summary>Opacity mask from a transition (iris), used when there is no cut-out.</summary>
    public Brush? Extra;
    WriteableBitmap? filmOut; byte[]? filmPx;
    /// <summary>Film Dissolve: punch WriteFilmMask into the picture's own pixels (premultiplied alpha). OpacityMask and a
    /// shader on Image did not punch holes; a shader on a Rectangle (the probe) did — baked pixels do the same.</summary>
    public void SetFilm(int lim, bool invert, double left, double top, double w, double h)
    {
        if (Img.Effect is FilmEffect) Img.Effect = null;
        if (cropBox.Effect is FilmEffect) cropBox.Effect = null;
        if (Img.OpacityMask != null) Img.OpacityMask = null;
        if (lim < 0) return;
        if (Img.Source is not BitmapSource src) return;
        int W = Editor.W, H = Editor.H, sw = src.PixelWidth, sh = src.PixelHeight, stride = sw * 4;
        if (filmOut == null || filmOut.PixelWidth != sw || filmOut.PixelHeight != sh)
        { filmOut = new WriteableBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32, null); filmPx = new byte[stride * sh]; }
        var conv = src.Format == PixelFormats.Pbgra32 || src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Bgr32 ? src : new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
        conv.CopyPixels(filmPx, stride, 0);
        var gray = new byte[W * H]; Transitions.WriteFilmMask(gray, W, H, lim / 64.0);
        double sx = w / Math.Max(1, sw), sy = h / Math.Max(1, sh);
        Parallel.For(0, sh, y =>
        {
            int fy = (int)Math.Clamp(Math.Floor(top + y * sy), 0, H - 1);
            for (int x = 0, i = y * stride; x < sw; x++, i += 4)
            {
                int fx = (int)Math.Clamp(Math.Floor(left + x * sx), 0, W - 1);
                byte m = gray[fy * W + fx]; if (invert) m = (byte)(255 - m);
                if (m == 255) continue;
                if (m == 0) { filmPx![i] = filmPx[i + 1] = filmPx[i + 2] = filmPx[i + 3] = 0; continue; }
                filmPx![i] = (byte)(filmPx[i] * m / 255); filmPx[i + 1] = (byte)(filmPx[i + 1] * m / 255);
                filmPx[i + 2] = (byte)(filmPx[i + 2] * m / 255); filmPx[i + 3] = (byte)(filmPx[i + 3] * m / 255);
            }
        });
        filmOut.WritePixels(new Int32Rect(0, 0, sw, sh), filmPx, stride, 0);
        if (!ReferenceEquals(Img.Source, filmOut)) Img.Source = filmOut;
    }

    /// <summary>Feathered outline as an opacity mask for a vw×vh element (absolute mapping, so effects/bounds don't stretch it).</summary>
    static Brush Brush(Geometry? area, double vw, double vh, double feather)
    {
        var path = new Path { Data = area ?? Geometry.Empty, Fill = Brushes.White, Effect = TitleVisual.Blur(feather) };
        var host = new Canvas { Width = vw, Height = vh };
        host.Children.Add(path);
        host.Measure(new Size(vw, vh)); host.Arrange(new Rect(0, 0, vw, vh));
        var r = new Rect(0, 0, vw, vh);
        var b = new VisualBrush(host) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = r,
                                        ViewportUnits = BrushMappingMode.Absolute, Viewport = r };
        return b;
    }

    /// <summary>CPU-derived versions of the current image: pixelated (block average, block = amount in sequence px)
    /// or greyscale (Rec.601 luma, same weights as the exporter).</summary>
    BitmapSource? Derived(bool pixelate, long stamp, double vw, double amount)
    {
        if (Img.Source is not BitmapSource src) return null;
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        int block = Math.Max(1, (int)Math.Round(amount * w / Math.Max(1, vw)));
        if (!ReferenceEquals(derivedFrom, src) || derivedStamp != stamp || (pixelate && block != derivedBlock) || srcPx == null)
        {
            if (srcPx == null || srcPx.Length != stride * h) srcPx = new byte[stride * h];
            var bgr = src.Format == PixelFormats.Bgr32 || src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Pbgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
            bgr.CopyPixels(srcPx, stride, 0);
            derivedFrom = src; derivedStamp = stamp; derivedBlock = block;
            pixBmp = grayBmp = null;
        }
        if (pixelate)
        {
            if (pixBmp != null) return pixBmp;
            var o = new byte[srcPx.Length];
            for (int by = 0; by < h; by += block)
                for (int bx = 0; bx < w; bx += block)
                {
                    int x1 = Math.Min(w, bx + block), y1 = Math.Min(h, by + block), n = (x1 - bx) * (y1 - by);
                    long r = 0, g = 0, bl = 0;
                    for (int y = by; y < y1; y++) for (int x = bx; x < x1; x++) { int i = y * stride + x * 4; bl += srcPx[i]; g += srcPx[i + 1]; r += srcPx[i + 2]; }
                    byte R = (byte)(r / n), G = (byte)(g / n), B = (byte)(bl / n);
                    for (int y = by; y < y1; y++) for (int x = bx; x < x1; x++) { int i = y * stride + x * 4; o[i] = B; o[i + 1] = G; o[i + 2] = R; o[i + 3] = 255; }
                }
            pixBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            pixBmp.WritePixels(new Int32Rect(0, 0, w, h), o, stride, 0);
            return pixBmp;
        }
        if (grayBmp != null) return grayBmp;
        var gpx = new byte[srcPx.Length];
        for (int i = 0; i < gpx.Length; i += 4)
        {
            byte y = (byte)Math.Clamp(0.299 * srcPx[i + 2] + 0.587 * srcPx[i + 1] + 0.114 * srcPx[i] + 0.5, 0, 255);
            gpx[i] = gpx[i + 1] = gpx[i + 2] = y; gpx[i + 3] = 255;
        }
        grayBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
        grayBmp.WritePixels(new Int32Rect(0, 0, w, h), gpx, stride, 0);
        return grayBmp;
    }

    /// <summary>A power window's picture: the shown frame through the window's LUT (lut3d's arithmetic, as the export applies it after the
    /// clip's grade). One reused bitmap and buffer per mask; recomputed only when the frame or the grade changes.</summary>
    sealed class GradedPic { public WriteableBitmap? Wb; public byte[]? Buf; public object? From; public long Stamp = -1; public string Sig = ""; }
    readonly Dictionary<string, GradedPic> graded = new();
    BitmapSource? Graded(string id, Grade.Lut? lut, long stamp)
    {
        if (Img.Source is not BitmapSource src) return null;
        if (lut == null) return src;
        if (!graded.TryGetValue(id, out var gp)) graded[id] = gp = new GradedPic();
        if (ReferenceEquals(gp.From, src) && gp.Stamp == stamp && gp.Sig == lut.Sig && gp.Wb != null) return gp.Wb;
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        if (gp.Buf == null || gp.Buf.Length != stride * h) gp.Buf = new byte[stride * h];
        var bgr = src.Format == PixelFormats.Bgr32 || src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Pbgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
        bgr.CopyPixels(gp.Buf, stride, 0);
        CutPro.Grade.Apply(lut, gp.Buf, w, h, stride);
        if (gp.Wb == null || gp.Wb.PixelWidth != w || gp.Wb.PixelHeight != h) gp.Wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
        gp.Wb.WritePixels(new Int32Rect(0, 0, w, h), gp.Buf, stride, 0);
        gp.From = src; gp.Stamp = stamp; gp.Sig = lut.Sig;
        return gp.Wb;
    }

    public void Clear() { fx.Children.Clear(); overlays.Clear(); graded.Clear(); Host.OpacityMask = Extra; }
}
