using System.Diagnostics;
using System.Net.Http;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CutPro;

/// <summary>
/// MobileSAM (Segment Anything, mobile) via ONNX Runtime — DirectML on the GPU, CPU fallback.
/// Encoder: RGB image (longest side 1024, 0..255 floats, HxWx3) → 256x64x64 embedding. Decoder: points / box prompt
/// (+ optional previous low-res mask) → mask logits at the image size. Models (MIT, from huggingface.co/Acly/MobileSAM)
/// live in %LOCALAPPDATA%\CutPro\Models and are downloaded on first use.
/// </summary>
public sealed class SamEngine : IDisposable
{
    public const int Side = 1024;
    public static string ModelDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutPro", "Models");
    static readonly (string file, string url)[] Files =
    {
        ("mobile_sam_image_encoder.onnx", "https://huggingface.co/Acly/MobileSAM/resolve/main/mobile_sam_image_encoder.onnx"),
        ("sam_mask_decoder_single.onnx", "https://huggingface.co/Acly/MobileSAM/resolve/main/sam_mask_decoder_single.onnx"),
    };
    public static bool ModelsPresent => Files.All(f => File.Exists(Path.Combine(ModelDir, f.file)));

    /// <summary>Downloads the models (≈43 MB). progress: 0..1.</summary>
    public static async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ModelDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        for (int i = 0; i < Files.Length; i++)
        {
            var dst = Path.Combine(ModelDir, Files[i].file);
            if (File.Exists(dst)) continue;
            using var resp = await http.GetAsync(Files[i].url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0, got = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fs = File.Create(dst + ".part"))
            {
                var buf = new byte[1 << 16]; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct); got += n;
                    if (total > 0) progress?.Report((i + (double)got / total) / Files.Length);
                }
            }
            File.Move(dst + ".part", dst, true);
        }
        progress?.Report(1);
    }

    readonly InferenceSession enc, dec;
    public string Provider { get; }

    public SamEngine(bool preferGpu = true)
    {
        string E(string f) => Path.Combine(ModelDir, f);
        if (preferGpu)
        {
            try
            {
                var o = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
                o.AppendExecutionProvider_DML(0);
                enc = new InferenceSession(E(Files[0].file), o);
                dec = new InferenceSession(E(Files[1].file), new SessionOptions());   // decoder is tiny: CPU avoids GPU round trips
                Provider = "DirectML (GPU)";
                return;
            }
            catch { enc?.Dispose(); }
        }
        enc = new InferenceSession(E(Files[0].file));
        dec = new InferenceSession(E(Files[1].file));
        Provider = "CPU";
    }

    public string Describe()
    {
        static string Io(IReadOnlyDictionary<string, NodeMetadata> d) =>
            string.Join(", ", d.Select(kv => $"{kv.Key}[{string.Join("x", kv.Value.Dimensions)}]:{kv.Value.ElementType.Name}"));
        return $"provider={Provider}\nencoder in:  {Io(enc.InputMetadata)}\nencoder out: {Io(enc.OutputMetadata)}\n" +
               $"decoder in:  {Io(dec.InputMetadata)}\ndecoder out: {Io(dec.OutputMetadata)}";
    }

    /// <summary>Image embedding for a frame. rgb: packed BGR32/BGRA pixels (w*h*4, longest side must be 1024).</summary>
    public DenseTensor<float> Encode(byte[] bgra, int w, int h)
    {
        var t = new DenseTensor<float>(new[] { h, w, 3 });
        var span = t.Buffer.Span;
        for (int i = 0, p = 0; i < w * h; i++, p += 4)
        {
            span[i * 3] = bgra[p + 2]; span[i * 3 + 1] = bgra[p + 1]; span[i * 3 + 2] = bgra[p];
        }
        using var r = enc.Run(new[] { NamedOnnxValue.CreateFromTensor(enc.InputMetadata.Keys.First(), t) });
        var emb = r.First().AsTensor<float>();
        var copy = new DenseTensor<float>(emb.Dimensions.ToArray());
        emb.ToDenseTensor().Buffer.Span.CopyTo(copy.Buffer.Span);
        return copy;
    }

    /// <summary>Prompt → mask. Points in image pixels (label 1 = object, 0 = background, 2/3 = box top-left/bottom-right).
    /// prevLowRes: 256x256 logits of the previous frame's mask (tracking) or null. Returns logits (h*w) and the new low-res mask.</summary>
    public (float[] mask, float[] lowRes, float score) Decode(DenseTensor<float> emb, IList<(float x, float y, float label)> pts, float[]? prevLowRes, int w, int h)
    {
        bool hasBox = pts.Any(p => p.label >= 2);
        int n = pts.Count + (hasBox ? 0 : 1);                                    // SAM ONNX wants a padding point when there's no box
        var coords = new DenseTensor<float>(new[] { 1, n, 2 });
        var labels = new DenseTensor<float>(new[] { 1, n });
        for (int i = 0; i < pts.Count; i++) { coords[0, i, 0] = pts[i].x; coords[0, i, 1] = pts[i].y; labels[0, i] = pts[i].label; }
        if (!hasBox) { coords[0, n - 1, 0] = 0; coords[0, n - 1, 1] = 0; labels[0, n - 1] = -1; }
        var maskIn = new DenseTensor<float>(new[] { 1, 1, 256, 256 });
        if (prevLowRes != null) prevLowRes.AsSpan().CopyTo(maskIn.Buffer.Span);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("image_embeddings", emb),
            NamedOnnxValue.CreateFromTensor("point_coords", coords),
            NamedOnnxValue.CreateFromTensor("point_labels", labels),
            NamedOnnxValue.CreateFromTensor("mask_input", maskIn),
            NamedOnnxValue.CreateFromTensor("has_mask_input", new DenseTensor<float>(new[] { prevLowRes != null ? 1f : 0f }, new[] { 1 })),
            NamedOnnxValue.CreateFromTensor("orig_im_size", new DenseTensor<float>(new[] { (float)h, w }, new[] { 2 })),
        };
        using var r = dec.Run(inputs.Where(i => dec.InputMetadata.ContainsKey(i.Name)).ToList());
        float[] Out(string name) => r.First(x => x.Name == name).AsTensor<float>().ToArray();
        var masks = Out("masks");
        var low = Out("low_res_masks");
        var iou = r.First(x => x.Name == "iou_predictions").AsTensor<float>().ToArray();
        return (masks.Length >= w * h ? masks[..(w * h)] : masks, low.Length >= 65536 ? low[..65536] : low, iou.Length > 0 ? iou[0] : 0);
    }

    public void Dispose() { enc.Dispose(); dec.Dispose(); }
}
