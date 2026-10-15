using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CutPro;

/// <summary>
/// Scene tags with OpenAI CLIP ViT-B/32 (int8 ONNX from Xenova/clip-vit-base-patch32, MIT licence): zero-shot — each frame's
/// image embedding is compared with text embeddings of scene descriptions (church worship, choir, baptism, a sermon, nature …).
/// Text embeddings are computed once and cached next to the models.
/// </summary>
public sealed class ClipTagger : IDisposable
{
    public static readonly (string file, string url)[] Files =
    {
        ("clip-vit-b32/vision_model_quantized.onnx", "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/vision_model_quantized.onnx"),
        ("clip-vit-b32/text_model_quantized.onnx", "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/text_model_quantized.onnx"),
        ("clip-vit-b32/tokenizer.json", "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/tokenizer.json"),
    };
    public static bool Present => ModelStore.Present(Files);

    /// <summary>Scene vocabulary: what CLIP compares frames with → phrase for the description, hashtags.</summary>
    public static readonly (string prompt, string phrase, string[] tags)[] Scenes =
    {
        ("a worship band playing on a church stage with lights", "worship", new[] { "worship", "praise", "worshipmusic" }),
        ("people raising their hands in worship", "worship", new[] { "worship", "praise" }),
        ("a church choir singing", "the choir", new[] { "choir", "gospelmusic" }),
        ("a pastor preaching a sermon from a pulpit", "a sermon", new[] { "sermon", "preaching", "bibleteaching" }),
        ("a man speaking on a stage with a microphone", "the message", new[] { "sermon", "message" }),
        ("a person talking to the camera", "a personal message", new[] { "testimony" }),
        ("an open Bible", "Scripture", new[] { "bible", "scripture", "godsword" }),
        ("a cross", "the cross", new[] { "jesus", "cross" }),
        ("a baptism in water", "a baptism", new[] { "baptism" }),
        ("people praying with their heads bowed", "prayer", new[] { "prayer", "pray" }),
        ("a wedding ceremony", "a wedding", new[] { "wedding", "christianwedding" }),
        ("a Christmas nativity scene", "Christmas", new[] { "christmas", "nativity" }),
        ("an Easter sunrise service", "Easter", new[] { "easter", "heisrisen" }),
        ("communion bread and a cup of wine", "communion", new[] { "communion" }),
        ("candles glowing in a dark church", "candlelight", new[] { "candlelight" }),
        ("stained glass windows in a church", "the church", new[] { "church" }),
        ("the outside of a church building", "our church", new[] { "church", "churchfamily" }),
        ("a group of teenagers at a youth group", "youth ministry", new[] { "youthgroup", "youthministry" }),
        ("children in a Sunday school classroom", "kids ministry", new[] { "sundayschool", "kidsministry" }),
        ("a small group studying the Bible around a table", "Bible study", new[] { "biblestudy", "smallgroup" }),
        ("volunteers serving food to people in need", "serving others", new[] { "serve", "community", "outreach" }),
        ("a mission trip in a village", "missions", new[] { "missions", "missiontrip" }),
        ("a family together", "family", new[] { "family" }),
        ("people hugging and smiling", "fellowship", new[] { "fellowship", "churchfamily" }),
        ("a sunrise over mountains", "God's creation", new[] { "godscreation", "sunrise" }),
        ("a sunset sky", "God's creation", new[] { "godscreation", "sunset" }),
        ("the ocean and a beach", "God's creation", new[] { "godscreation", "ocean" }),
        ("a forest with sunlight", "God's creation", new[] { "godscreation", "nature" }),
        ("a city skyline", "the city", new[] { "city" }),
        ("a concert crowd", "a crowd", new[] { "concert" }),
        ("a person playing guitar", "music", new[] { "guitar", "music" }),
        ("a person playing piano", "music", new[] { "piano", "music" }),
        ("a podcast studio with microphones", "a podcast", new[] { "podcast", "christianpodcast" }),
        ("a meal on a table", "a shared meal", new[] { "fellowship", "food" }),
        ("a funeral or memorial service", "a memorial", new[] { "celebrationoflife", "memorial" }),
        ("people playing sports", "sports", new[] { "sports" }),
        ("a dog", "a dog", new[] { "dog" }),
        // text on screen (announcements, lyrics, title cards): CLIP reads words ("Sunday Service" looked like Easter), so these
        // soak up frames that are mostly text — they add no tags
        ("a slide with text on a screen", "", Array.Empty<string>()),
        ("a presentation slide with words and bullet points", "", Array.Empty<string>()),
        ("song lyrics on a screen", "", Array.Empty<string>()),
        ("a title card with text", "", Array.Empty<string>()),
        // neutral answers: softmax always picks something, so pictures that are none of the above (graphics, test patterns,
        // black frames) land here instead of on a scene that would add wrong hashtags
        ("abstract colorful shapes", "", Array.Empty<string>()),
        ("a test pattern of color bars", "", Array.Empty<string>()),
        ("a black screen", "", Array.Empty<string>()),
    };
    /// <summary>Prompts that add nothing to the description (text, graphics, blank).</summary>
    public static bool IsNeutral(int scene) => Scenes[scene].tags.Length == 0;

    readonly InferenceSession vis, txt;
    readonly string visIn, visOut, txtIn, txtOut;
    readonly Dictionary<string, int> vocab = new();
    readonly Dictionary<(string, string), int> merges = new();
    float[][]? sceneEmb;

    public ClipTagger()
    {
        var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        vis = new InferenceSession(ModelStore.PathOf(Files[0].file), so);
        txt = new InferenceSession(ModelStore.PathOf(Files[1].file), so);
        visIn = vis.InputMetadata.Keys.First(k => k.Contains("pixel"));
        visOut = vis.OutputMetadata.Keys.FirstOrDefault(k => k.Contains("image_embeds")) ?? vis.OutputMetadata.Keys.First();
        txtIn = txt.InputMetadata.Keys.First(k => k.Contains("input_ids"));
        txtOut = txt.OutputMetadata.Keys.FirstOrDefault(k => k.Contains("text_embeds")) ?? txt.OutputMetadata.Keys.First();
        using var doc = JsonDocument.Parse(File.ReadAllText(ModelStore.PathOf(Files[2].file)));
        var model = doc.RootElement.GetProperty("model");
        foreach (var kv in model.GetProperty("vocab").EnumerateObject()) vocab[kv.Name] = kv.Value.GetInt32();
        int rank = 0;
        foreach (var m in model.GetProperty("merges").EnumerateArray())
        {
            string a, b;
            if (m.ValueKind == JsonValueKind.Array) { a = m[0].GetString()!; b = m[1].GetString()!; }
            else { var parts = m.GetString()!.Split(' '); if (parts.Length != 2) continue; a = parts[0]; b = parts[1]; }
            merges.TryAdd((a, b), rank++);
        }
    }

    public void Dispose() { vis.Dispose(); txt.Dispose(); }

    // ---------------- text side ----------------
    static readonly Dictionary<byte, char> unicodeOf = Enumerable.Range(0, 256).ToDictionary(b => (byte)b, b => ByteChar(b));
    static char ByteChar(int b)
    {
        var bs = new List<int>();
        for (int x = '!'; x <= '~'; x++) bs.Add(x);
        for (int x = 0xA1; x <= 0xAC; x++) bs.Add(x);
        for (int x = 0xAE; x <= 0xFF; x++) bs.Add(x);
        if (bs.Contains(b)) return (char)b;
        int n = 0; for (int x = 0; x < 256; x++) if (!bs.Contains(x)) { if (x == b) return (char)(256 + n); n++; }
        return '?';
    }
    static readonly Regex Pat = new(@"'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>CLIP BPE: lower-case words → byte chars → merges (word-final symbol ends with &lt;/w&gt;) → ids, between start/end tokens.</summary>
    public List<long> Tokenize(string text)
    {
        var ids = new List<long> { 49406 };
        foreach (Match w in Pat.Matches(Regex.Replace(text.ToLowerInvariant().Trim(), @"\s+", " ")))
        {
            var chars = Encoding.UTF8.GetBytes(w.Value).Select(b => unicodeOf[b].ToString()).ToList();
            chars[^1] += "</w>";
            while (chars.Count > 1)
            {
                int best = -1, bestRank = int.MaxValue;
                for (int i = 0; i < chars.Count - 1; i++)
                    if (merges.TryGetValue((chars[i], chars[i + 1]), out int r) && r < bestRank) { bestRank = r; best = i; }
                if (best < 0) break;
                chars[best] += chars[best + 1]; chars.RemoveAt(best + 1);
            }
            foreach (var c in chars) if (vocab.TryGetValue(c, out int id)) ids.Add(id);
        }
        ids.Add(49407);
        return ids.Count > 77 ? ids.Take(76).Append(49407L).ToList() : ids;
    }

    float[] TextEmbedding(string prompt)
    {
        var ids = Tokenize(prompt);
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(txtIn, new DenseTensor<long>(ids.ToArray(), new[] { 1, ids.Count })) };
        if (txt.InputMetadata.Keys.FirstOrDefault(k => k.Contains("attention")) is { } am)
            inputs.Add(NamedOnnxValue.CreateFromTensor(am, new DenseTensor<long>(Enumerable.Repeat(1L, ids.Count).ToArray(), new[] { 1, ids.Count })));
        using var o = txt.Run(inputs);
        return Normalize(o.First(x => x.Name == txtOut).AsTensor<float>().ToArray());
    }

    /// <summary>Text embeddings of prompts, cached in clip-vit-b32/scenes.json (computed once per prompt).</summary>
    float[][] Embeddings(IList<string> prompts)
    {
        var cache = ModelStore.PathOf("clip-vit-b32/scenes.json");
        Dictionary<string, float[]> d;
        try { d = File.Exists(cache) ? JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(cache)) ?? new() : new(); } catch { d = new(); }
        bool added = false;
        foreach (var p in prompts) if (!d.ContainsKey(p)) { d[p] = TextEmbedding(p); added = true; }
        if (added) try { File.WriteAllText(cache, JsonSerializer.Serialize(d)); } catch { }
        return prompts.Select(p => d[p]).ToArray();
    }
    float[][] SceneEmbeddings() => sceneEmb ??= Embeddings(Scenes.Select(s => "a photo of " + s.prompt).ToList());

    // "is this frame mostly text?" — asked on its own (slide vs photo), because among scenes CLIP reads the words: a slide
    // saying "Sunday Service" scored 85 % "an Easter sunrise service"
    static readonly string[] TextPrompts = { "a slide full of text", "a screenshot of a document with lots of text", "song lyrics written on a screen", "a page of written words" };
    static readonly string[] PhotoPrompts = { "a photo", "a picture of a scene", "an illustration", "a picture of an object", "a symbol", "a simple shape" };
    float[]? textAxis, photoAxis;
    /// <summary>Frames above this are treated as text (a missed scene costs a tag; a slide read as a scene adds wrong ones).</summary>
    public const double TextCutoff = 0.6;
    /// <summary>Probability (0..1) that a frame is mostly text: prompt-ensembled "text" vs "photo".</summary>
    public double TextLikelihood(float[] img)
    {
        static float[] Mean(float[][] v) => Normalize(Enumerable.Range(0, v[0].Length).Select(i => v.Average(e => e[i])).ToArray());
        textAxis ??= Mean(Embeddings(TextPrompts)); photoAxis ??= Mean(Embeddings(PhotoPrompts));
        double t = 100 * Dot(textAxis, img), p = 100 * Dot(photoAxis, img);
        return 1 / (1 + Math.Exp(p - t));
    }

    static double Dot(float[] a, float[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += (double)a[i] * b[i]; return s; }
    static float[] Normalize(float[] v) { double n = Math.Sqrt(v.Sum(x => (double)x * x)); return v.Select(x => (float)(x / Math.Max(1e-9, n))).ToArray(); }

    // ---------------- image side ----------------
    /// <summary>Image embedding of one frame (BGR32, any size): resize short side 224 (bilinear), centre crop, CLIP
    /// normalisation, vision model, unit length.</summary>
    public float[] Embed(byte[] bgr, int w, int h)
    {
        const int S = 224;
        double f = S / (double)Math.Min(w, h);
        int rw = (int)Math.Round(w * f), rh = (int)Math.Round(h * f), ox = (rw - S) / 2, oy = (rh - S) / 2;
        float[] mean = { 0.48145466f, 0.4578275f, 0.40821073f }, std = { 0.26862954f, 0.26130258f, 0.27577711f };
        var data = new float[3 * S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                double sx = Math.Clamp((x + ox + 0.5) / f - 0.5, 0, w - 1), sy = Math.Clamp((y + oy + 0.5) / f - 0.5, 0, h - 1);
                int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
                double fx = sx - x0, fy = sy - y0;
                for (int ch = 0; ch < 3; ch++)
                {
                    int bc = 2 - ch;                       // BGR → RGB
                    double v = (bgr[(y0 * w + x0) * 4 + bc] * (1 - fx) + bgr[(y0 * w + x1) * 4 + bc] * fx) * (1 - fy)
                             + (bgr[(y1 * w + x0) * 4 + bc] * (1 - fx) + bgr[(y1 * w + x1) * 4 + bc] * fx) * fy;
                    data[ch * S * S + y * S + x] = (float)((v / 255.0 - mean[ch]) / std[ch]);
                }
            }
        using var o = vis.Run(new[] { NamedOnnxValue.CreateFromTensor(visIn, new DenseTensor<float>(data, new[] { 1, 3, S, S })) });
        return Normalize(o.First(x => x.Name == visOut).AsTensor<float>().ToArray());
    }

    /// <summary>Probability of each scene for an image embedding: softmax over cosine similarity × 100.</summary>
    public float[] Scores(float[] img)
    {
        var logits = SceneEmbeddings().Select(t => 100.0 * Dot(t, img)).ToArray();
        double m = logits.Max(), sum = logits.Sum(l => Math.Exp(l - m));
        return logits.Select(l => (float)(Math.Exp(l - m) / sum)).ToArray();
    }
    public float[] Scores(byte[] bgr, int w, int h) => Scores(Embed(bgr, w, h));
}
