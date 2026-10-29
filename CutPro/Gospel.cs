using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>A short public-domain (KJV) verse for graphics and the Bible Page Turn overlay.</summary>
public readonly record struct Verse(string Ref, string Text);

/// <summary>Gospel / Bible graphics: verses, a parchment page bitmap (preview = export), and the turning-page overlay.</summary>
public static class Gospel
{
    public static readonly Verse[] Verses =
    {
        new("John 3:16", "For God so loved the world, that he gave his only begotten Son, that whosoever believeth in him should not perish, but have everlasting life."),
        new("John 14:6", "I am the way, the truth, and the life: no man cometh unto the Father, but by me."),
        new("John 1:1", "In the beginning was the Word, and the Word was with God, and the Word was God."),
        new("Romans 10:9", "If thou shalt confess with thy mouth the Lord Jesus, and shalt believe in thine heart that God hath raised him from the dead, thou shalt be saved."),
        new("Romans 8:28", "And we know that all things work together for good to them that love God, to them who are the called according to his purpose."),
        new("Ephesians 2:8", "For by grace are ye saved through faith; and that not of yourselves: it is the gift of God."),
        new("Matthew 11:28", "Come unto me, all ye that labour and are heavy laden, and I will give you rest."),
        new("Philippians 4:13", "I can do all things through Christ which strengtheneth me."),
        new("Psalm 23:1", "The Lord is my shepherd; I shall not want."),
        new("Isaiah 40:31", "They that wait upon the Lord shall renew their strength; they shall mount up with wings as eagles."),
        new("Genesis 1:1", "In the beginning God created the heaven and the earth."),
        new("2 Timothy 3:16", "All scripture is given by inspiration of God, and is profitable for doctrine, for reproof, for correction, for instruction in righteousness."),
    };

    public static Verse Of(string reference) => Verses.FirstOrDefault(v => v.Ref.Equals(reference, StringComparison.OrdinalIgnoreCase), Verses[0]);

    /// <summary>John 3:16 unless the timeline already names another verse from <see cref="Verses"/> (a title, a graphic, the publishing scripture).</summary>
    public static Verse For(Editor? ed)
    {
        if (ed != null)
            foreach (var c in ed.Clips)
            {
                foreach (var s in new[] { c.Text }.Concat(c.Shapes?.Select(x => x.Text) ?? Array.Empty<string>()))
                    if (!string.IsNullOrWhiteSpace(s))
                        foreach (var v in Verses)
                            if (s.Contains(v.Ref, StringComparison.OrdinalIgnoreCase)) return v;
            }
        return Verses[0];
    }

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static SolidColorBrush B(string hex) { var b = new SolidColorBrush(C(hex)); b.Freeze(); return b; }

    /// <summary>A parchment page with the verse, logical canvas size. Frozen; reused while the verse and size stay the same.</summary>
    public static BitmapSource PageBitmap(int w, int h, Verse verse)
    {
        lock (pageGate)
        {
            if (pageBmp != null && pageW == w && pageH == h && pageRef == verse.Ref) return pageBmp;
            pageBmp = DrawPage(w, h, verse); pageW = w; pageH = h; pageRef = verse.Ref;
            return pageBmp;
        }
    }
    static readonly object pageGate = new();
    static BitmapSource? pageBmp; static int pageW, pageH; static string pageRef = "";

    static BitmapSource DrawPage(int w, int h, Verse verse)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(B("#FF1A140C"), null, new Rect(0, 0, w, h));
            double m = Math.Min(w, h) * 0.08, pw = w - 2 * m, ph = h - 2 * m;
            var page = new RectangleGeometry(new Rect(m, m, pw, ph), 10, 10);
            var fill = new LinearGradientBrush(C("#FFF6E4C1"), C("#FFE8C98A"), new Point(0, 0), new Point(1, 1)); fill.Freeze();
            dc.DrawGeometry(fill, new Pen(B("#FF8A6A32"), 2), page);
            dc.PushClip(page);
            for (int i = 0; i < 9; i++)
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(28, 90, 60, 20)), 1), new Point(m + pw * 0.08, m + ph * (0.18 + i * 0.08)), new Point(m + pw * 0.92, m + ph * (0.18 + i * 0.08)));
            var gold = B("#FFB8860B");
            dc.DrawGeometry(gold, null, new CombinedGeometry(GeometryCombineMode.Union,
                new RectangleGeometry(new Rect(w / 2 - 7, m + ph * 0.08, 14, ph * 0.12), 2, 2),
                new RectangleGeometry(new Rect(w / 2 - 22, m + ph * 0.11, 44, 12), 2, 2)));
            var type = new Typeface(new FontFamily("Georgia"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
            var body = new FormattedText(verse.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, type, Math.Max(18, h * 0.038), B("#FF3A2410"), 1.0)
            { MaxTextWidth = pw * 0.78, MaxTextHeight = ph * 0.55, Trimming = TextTrimming.WordEllipsis, TextAlignment = TextAlignment.Center };
            dc.DrawText(body, new Point(m + pw * 0.11, m + ph * 0.28));
            var refType = new Typeface(new FontFamily("Georgia"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var rf = new FormattedText(verse.Ref, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, refType, Math.Max(16, h * 0.032), gold, 1.0)
            { MaxTextWidth = pw * 0.8, TextAlignment = TextAlignment.Center };
            dc.DrawText(rf, new Point(m + pw * 0.1, m + ph * 0.82));
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv); rtb.Freeze();
        return rtb;
    }

    /// <summary>Writes the page PNG the exporter overlays during Bible Page Turn (output pixels).</summary>
    public static string WritePagePng(int w, int h, Verse verse, List<string> temps)
    {
        string file = Path.Combine(Path.GetTempPath(), $"vedit_biblepage_{Guid.NewGuid().ToString("N")[..8]}.png");
        temps.Add(file);
        Exporter.Sta(() =>
        {
            var bmp = DrawPage(w, h, verse);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(file); enc.Save(fs);
        });
        return file;
    }
}
