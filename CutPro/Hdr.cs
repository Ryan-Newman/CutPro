using System.Globalization;
using System.Text.Json;

namespace CutPro;

/// <summary>
/// HDR footage (HLG — phones, action cameras — and PQ / HDR10) in CutPro's SDR (Rec.709) pipeline: tone-mapped to SDR with one ffmpeg chain,
/// run wherever the original file is decoded — the preview's decoder and the export, both right after scaling (so 4K HDR still plays), the
/// scrub cache, thumbnails, frame grabs, proxies and the speed / stabilize renders (their files are SDR then and are never tone-mapped again).
/// Without it HDR shows flat and grey. Project panel ▸ HDR ▸ Show as is keeps the signal untouched (a file mislabelled as HDR).
/// The curve (measured by --hdrtest on footage graded the BT.2408 way): HDR reference white (203 cd/m²) is SDR white, linear light below
/// <see cref="Knee"/> of it passes unchanged (18 % grey lands where SDR puts it), Mobius compresses the rest up to the file's peak into the top
/// of the range — HDR white shows at ~91 %, highlights keep their gradation. (Hable at 100 cd/m², the usual recipe, left white at 74 %: dull.)
/// </summary>
public static class Hdr
{
    /// <summary>HDR reference white, cd/m² (BT.2408): graphics white in HDR = SDR white.</summary>
    public const double White = 203;
    /// <summary>Linear light (× white) up to which the picture passes unchanged.</summary>
    public const double Knee = 0.6;

    /// <summary>"hlg" / "pq" from a stream's colour transfer (ffprobe's color_transfer), null for SDR.</summary>
    public static string? Kind(string? transfer) => transfer switch { "arib-std-b67" => "hlg", "smpte2084" => "pq", _ => null };

    /// <summary>The chain that makes the file's frames SDR ("" for SDR files, or when the file is shown as is).</summary>
    /// <param name="deep">a 10-bit export (<see cref="ExportSettings.Deep"/>): the tone-mapped picture keeps 10 bits (8 otherwise, like SDR footage)</param>
    public static string Of(MediaItem m, bool deep = false) => m.Hdr is { } k && !m.KeepHdr && m.Seq == null ? Chain(k, k == "pq" ? m.HdrPeak : 1000, deep) : "";

    /// <summary>HDR → SDR: to linear light (1 = reference white; every output colour property named — zscale keeps a missing input tag on its
    /// output and then finds no path), BT.2020 → BT.709 primaries, the curve, BT.709 4:2:0 like ordinary footage.</summary>
    /// <param name="peak">the brightest the picture gets, cd/m² (HLG: its nominal 1000; PQ: the file's metadata)</param>
    public static string Chain(string kind, double peak = 1000, bool deep = false) =>
        $"zscale=tin={(kind == "pq" ? "smpte2084" : "arib-std-b67")}:pin=bt2020:min=bt2020nc:rin=tv:t=linear:p=bt2020:npl={White},format=gbrpf32le,zscale=p=bt709," +
        string.Create(CultureInfo.InvariantCulture, $"tonemap=tonemap=mobius:param={Knee}:peak={Math.Max(peak, White * 1.5) / White:0.###}:desat=0,") +
        $"zscale=t=bt709:m=bt709:r=tv,format={(deep ? "yuv420p10le" : "yuv420p")}";

    /// <summary>SDR Rec.709 composite → HLG / PQ for an HDR file (the inverse of a camera's BT.2408 grade: SDR white = 203 cd/m²).
    /// Every colour property is named so zscale finds a path. <paramref name="pix"/> is the encoder's pixel format.</summary>
    public static string Encode(string kind, string pix)
    {
        string t = kind == "pq" ? "smpte2084" : "arib-std-b67";
        return $"setparams=colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv," +
               $"zscale=tin=bt709:min=bt709:pin=bt709:rin=tv:t=linear:npl={White},format=gbrpf32le," +
               $"zscale=pin=bt709:p=bt2020,zscale=t={t}:npl={White}:m=bt2020nc:p=bt2020:r=tv,format={pix}";
    }
    /// <summary>ffmpeg -color_* tags for an HLG / PQ file.</summary>
    public static string[] FileTags(string kind)
    {
        string t = kind == "pq" ? "smpte2084" : "arib-std-b67";
        return new[] { "-color_primaries", "bt2020", "-color_trc", t, "-colorspace", "bt2020nc", "-color_range", "tv" };
    }
    /// <summary>x265 colour / HDR10 mastering (colon-separated, merged into -x265-params).</summary>
    public static string X265(string kind)
    {
        string t = kind == "pq" ? "smpte2084" : "arib-std-b67";
        string p = $"colorprim=bt2020:transfer={t}:colormatrix=bt2020nc";
        return kind == "pq" ? p + ":master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,203" : p;
    }

    /// <summary>A comma and the chain (to put after a filter), or "".</summary>
    public static string Post(MediaItem m, bool deep = false) => Of(m, deep) is { Length: > 0 } c ? "," + c : "";
    /// <summary>Marks a render's signature when its frames were tone-mapped (Remap, Stab, Nest, the scrub cache: "Show as is" or another curve
    /// renders them again).</summary>
    public static string Mark(MediaItem m) => Of(m) is { Length: > 0 } c ? "|tm:" + c : "";

    /// <summary>How the file's dynamic range shows in the Project panel.</summary>
    public static string Label(MediaItem m) => m.Hdr == null ? "" : $" {(m.Hdr == "pq" ? "PQ" : "HLG")} HDR{(m.KeepHdr ? " (as is)" : "")}";

    /// <summary>The brightest a PQ picture says it gets (cd/m²) from ffprobe's side data (a stream's or a frame's): its MaxCLL, else its
    /// mastering display's peak; null when it says nothing.</summary>
    public static double? Peak(JsonElement e)
    {
        if (!e.TryGetProperty("side_data_list", out var l) || l.ValueKind != JsonValueKind.Array) return null;
        double cll = 0, mdl = 0;
        foreach (var d in l.EnumerateArray())
        {
            if (d.TryGetProperty("max_content", out var mc) && double.TryParse(mc.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) cll = v;
            if (d.TryGetProperty("max_luminance", out var ml)) mdl = Ratio(ml.ToString());
        }
        double p = cll > 0 ? cll : mdl;
        return p > 0 ? Math.Clamp(p, 400, 10000) : null;
    }

    static double Ratio(string s)
    {
        var p = s.Split('/');
        return double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? p.Length == 2 && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b != 0 ? a / b : 0 : a : 0;
    }
}
