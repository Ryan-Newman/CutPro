using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CutPro;

/// <summary>
/// Speech-to-text with OpenAI Whisper (base, multilingual, int8 ONNX from onnx-community/whisper-base, MIT licence) on the
/// CPU through ONNX Runtime: 30-second windows → 80-bin log-mel spectrogram (Whisper's own front end) → encoder → greedy
/// decoding (language detected from the first window) → byte-level BPE text.
/// </summary>
public sealed class Whisper : IDisposable
{
    public static readonly (string file, string url)[] Files =
    {
        ("whisper-base/encoder_model_quantized.onnx", "https://huggingface.co/onnx-community/whisper-base/resolve/main/onnx/encoder_model_quantized.onnx"),
        ("whisper-base/decoder_model_quantized.onnx", "https://huggingface.co/onnx-community/whisper-base/resolve/main/onnx/decoder_model_quantized.onnx"),
        ("whisper-base/tokenizer.json", "https://huggingface.co/onnx-community/whisper-base/resolve/main/tokenizer.json"),
    };
    public static bool Present => ModelStore.Present(Files);

    const int Rate = 16000, NFft = 400, Hop = 160, Mels = 80, Frames = 3000, Chunk = Rate * 30;
    const int Eot = 50257, Sot = 50258, LangFirst = 50259, LangLast = 50357, TranscribeTok = 50359, NoSpeech = 50362, NoTimestamps = 50363;
    static readonly string[] LangCodes = { "en","zh","de","es","ru","ko","fr","ja","pt","tr","pl","ca","nl","ar","sv","it","id","hi","fi","vi","he","uk","el","ms","cs","ro","da","hu","ta","no","th","ur","hr","bg","lt","la","mi","ml","cy","sk","te","fa","lv","bn","sr","az","sl","kn","et","mk","br","eu","is","hy","ne","mn","bs","kk","sq","sw","gl","mr","pa","si","km","sn","yo","so","af","oc","ka","be","tg","sd","gu","am","yi","lo","uz","fo","ht","ps","tk","nn","mt","sa","lb","my","bo","tl","mg","as","tt","haw","ln","ha","ba","jw","su" };

    readonly InferenceSession enc, dec;
    readonly string encIn, encOut, decIds, decStates, decLogits;
    readonly Dictionary<int, string> vocab = new();
    static readonly Dictionary<char, byte> byteOf = ByteDecoder();
    static float[,]? melFilters;
    static double[]? window, cosT, sinT;

    public Whisper()
    {
        var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) };
        enc = new InferenceSession(ModelStore.PathOf(Files[0].file), so);
        dec = new InferenceSession(ModelStore.PathOf(Files[1].file), so);
        encIn = enc.InputMetadata.Keys.First(); encOut = enc.OutputMetadata.Keys.First();
        decIds = dec.InputMetadata.Keys.First(k => k.Contains("input_ids"));
        decStates = dec.InputMetadata.Keys.First(k => k.Contains("encoder_hidden_states"));
        decLogits = dec.OutputMetadata.Keys.First(k => k.Contains("logits"));
        using var doc = JsonDocument.Parse(File.ReadAllText(ModelStore.PathOf(Files[2].file)));
        foreach (var kv in doc.RootElement.GetProperty("model").GetProperty("vocab").EnumerateObject()) vocab[kv.Value.GetInt32()] = kv.Name;
        if (doc.RootElement.TryGetProperty("added_tokens", out var added))
            foreach (var t in added.EnumerateArray()) vocab[t.GetProperty("id").GetInt32()] = t.GetProperty("content").GetString() ?? "";
    }

    public void Dispose() { enc.Dispose(); dec.Dispose(); }

    public sealed record Segment(double Start, double End, string Text);
    public sealed record Result(string Language, List<Segment> Segments)
    {
        public string Text => string.Join(" ", Segments.Select(s => s.Text.Trim())).Trim();
    }

    /// <summary>Transcribes 16 kHz mono samples (−1..1). maxSeconds limits long videos (sermons: the first part says enough).</summary>
    public Result Transcribe(float[] pcm, double maxSeconds, IProgress<double>? progress, CancellationToken ct)
    {
        int total = (int)Math.Min(pcm.Length, maxSeconds * Rate);
        var segs = new List<Segment>();
        string lang = "en"; int langTok = LangFirst;
        bool detected = false;
        for (int off = 0; off < total; off += Chunk)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(Chunk, total - off);
            if (n < Rate / 2) break;
            // skip silence quickly
            double rms = 0; for (int i = off; i < off + n; i++) rms += pcm[i] * pcm[i];
            if (Math.Sqrt(rms / n) < 0.003) { progress?.Report((off + n) / (double)total); continue; }
            var mel = LogMel(pcm, off, n);
            var feats = new DenseTensor<float>(mel, new[] { 1, Mels, Frames });
            using var eo = enc.Run(new[] { NamedOnnxValue.CreateFromTensor(encIn, feats) });
            var states = eo.First(x => x.Name == encOut).AsTensor<float>().ToDenseTensor();
            if (!detected)
            {
                var first = Logits(new List<int> { Sot }, states);
                langTok = LangFirst; float best = float.MinValue;
                for (int t = LangFirst; t <= Math.Min(LangLast, LangFirst + LangCodes.Length - 1); t++) if (first[t] > best) { best = first[t]; langTok = t; }
                lang = LangCodes[langTok - LangFirst]; detected = true;
            }
            var tokens = new List<int> { Sot, langTok, TranscribeTok, NoTimestamps };
            int prompt = tokens.Count;
            // no-speech probability at the first step: skip windows of only music / noise
            var l0 = Logits(tokens, states);
            double noSpeech = Softmax(l0, NoSpeech);
            if (noSpeech > 0.6) { progress?.Report((off + n) / (double)total); continue; }
            var logits = l0;
            for (int step = 0; step < 220; step++)
            {
                for (int t = Eot + 1; t < logits.Length; t++) logits[t] = float.NegativeInfinity;     // no special / timestamp tokens
                if (step == 0) { logits[220] = float.NegativeInfinity; logits[Eot] = float.NegativeInfinity; }
                int next = 0; float bv = float.MinValue;
                for (int t = 0; t < logits.Length; t++) if (logits[t] > bv) { bv = logits[t]; next = t; }
                if (next == Eot) break;
                tokens.Add(next);
                if (Repeats(tokens, prompt)) { tokens.RemoveRange(tokens.Count - 8, 8); break; }
                logits = Logits(tokens, states);
            }
            var text = Clean(Decode(tokens.Skip(prompt)));
            if (text.Length > 0 && !(noSpeech > 0.1 && Hallucination(text))) segs.Add(new Segment(off / (double)Rate, (off + n) / (double)Rate, text));
            progress?.Report((off + n) / (double)total);
        }
        return new Result(lang, segs);
    }

    /// <summary>&lt;|0.00|&gt;: the first timestamp token (one step = 0.02 s, up to &lt;|30.00|&gt;).</summary>
    const int TsBegin = 50364;
    public static readonly (string code, string name)[] Languages =
        { ("", "Detect"), ("en", "English"), ("es", "Spanish"), ("pt", "Portuguese"), ("fr", "French"), ("de", "German"), ("it", "Italian"), ("ko", "Korean"),
          ("zh", "Chinese"), ("ja", "Japanese"), ("tl", "Tagalog"), ("ru", "Russian"), ("uk", "Ukrainian"), ("ar", "Arabic"), ("hi", "Hindi"), ("sw", "Swahili") };

    /// <summary>Speech with its times (captions): Whisper's timestamp tokens, window after window — each window starts where the last whole
    /// segment of the one before ended, so no word is cut in two (OpenAI's sequential transcription, greedy). language: "" / null = detect.</summary>
    public Result TranscribeTimed(float[] pcm, string? language, IProgress<double>? progress, CancellationToken ct)
    {
        int total = pcm.Length;
        var segs = new List<Segment>();
        int langTok = LangFirst; string lang = "en";
        bool detected = false;
        if (!string.IsNullOrEmpty(language) && Array.IndexOf(LangCodes, language) is int li and >= 0) { langTok = LangFirst + li; lang = language!; detected = true; }
        int seek = 0;
        while (seek < total - Rate / 4)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(Chunk, total - seek);
            double rms = 0; for (int i = seek; i < seek + n; i++) rms += pcm[i] * pcm[i];
            if (Math.Sqrt(rms / n) < 0.003) { seek += n; progress?.Report(Math.Min(1, seek / (double)total)); continue; }   // silence
            var mel = LogMel(pcm, seek, n);
            var feats = new DenseTensor<float>(mel, new[] { 1, Mels, Frames });
            using var eo = enc.Run(new[] { NamedOnnxValue.CreateFromTensor(encIn, feats) });
            var states = eo.First(x => x.Name == encOut).AsTensor<float>().ToDenseTensor();
            if (!detected)
            {
                var first = Logits(new List<int> { Sot }, states);
                float best = float.MinValue;
                for (int t = LangFirst; t <= Math.Min(LangLast, LangFirst + LangCodes.Length - 1); t++) if (first[t] > best) { best = first[t]; langTok = t; }
                lang = LangCodes[langTok - LangFirst]; detected = true;
            }
            var tokens = new List<int> { Sot, langTok, TranscribeTok };
            int prompt = tokens.Count, maxTs = Math.Min(1500, (int)Math.Round(n / (double)Rate / 0.02));
            var logits = Logits(tokens, states);
            double noSpeech = Softmax(logits, NoSpeech);
            if (noSpeech > 0.6) { seek += n; progress?.Report(Math.Min(1, seek / (double)total)); continue; }   // music / noise only
            for (int step = 0; step < 224; step++)
            {
                TimestampRules(logits, tokens, prompt, maxTs);
                int next = 0; float bv = float.MinValue;
                for (int t = 0; t < logits.Length; t++) if (logits[t] > bv) { bv = logits[t]; next = t; }
                if (next == Eot) break;
                tokens.Add(next);
                if (Repeats(tokens, prompt)) { tokens.RemoveRange(tokens.Count - 8, 8); break; }
                logits = Logits(tokens, states);
            }
            // segments between consecutive timestamps; seek to the end of the last whole one (an unfinished one is decoded again)
            var s = tokens.Skip(prompt).ToList();
            double off = seek / (double)Rate, winEnd = (seek + n) / (double)Rate;
            void Add(List<int> part, double a, double b)
            {
                var text = Clean(Decode(part.Where(t => t < Eot)));
                if (text.Length == 0 || (noSpeech > 0.1 && Hallucination(text))) return;
                segs.Add(new Segment(Math.Min(a, winEnd), Math.Clamp(b, a, winEnd), text));
            }
            bool IsTs(int i) => s[i] >= TsBegin;
            bool singleEnd = s.Count >= 2 && !IsTs(s.Count - 2) && IsTs(s.Count - 1);
            var cuts = new List<int>();
            for (int i = 1; i < s.Count; i++) if (IsTs(i) && IsTs(i - 1)) cuts.Add(i);
            int advance = n;
            if (cuts.Count > 0)
            {
                if (singleEnd) cuts.Add(s.Count);
                int last = 0;
                foreach (int cut in cuts)
                {
                    var part = s.GetRange(last, cut - last);
                    if (part.Count > 0 && part[0] >= TsBegin && part[^1] >= TsBegin) Add(part, off + (part[0] - TsBegin) * 0.02, off + (part[^1] - TsBegin) * 0.02);
                    last = cut;
                }
                if (!singleEnd)
                {
                    int lastTs = s[last - 1] - TsBegin;                    // the end of the last whole segment
                    if (lastTs > 50) advance = Math.Min(n, (int)Math.Round(lastTs * 0.02 * Rate));
                }
            }
            else
            {
                var ts = s.Where(t => t >= TsBegin).ToList();
                double dur = n / (double)Rate;
                if (ts.Count > 0 && ts[^1] != TsBegin) dur = (ts[^1] - TsBegin) * 0.02;
                double st = ts.Count > 0 && s.Count > 0 && s[0] >= TsBegin ? (s[0] - TsBegin) * 0.02 : 0;
                Add(s, off + st, off + Math.Max(st, dur));
            }
            seek += Math.Max(Rate, advance);
            progress?.Report(Math.Min(1, seek / (double)total));
        }
        return new Result(lang, segs);
    }

    /// <summary>OpenAI's timestamp rules: the text starts with a timestamp (within its first second), timestamps come in pairs (end, then the
    /// next start) and never go back, and a timestamp is taken whenever all timestamps together are likelier than any word.</summary>
    static void TimestampRules(float[] logits, List<int> tokens, int prompt, int maxTs)
    {
        const float No = float.NegativeInfinity;
        for (int t = Eot + 1; t < TsBegin && t < logits.Length; t++) logits[t] = No;            // special tokens
        for (int t = TsBegin + maxTs + 1; t < logits.Length; t++) logits[t] = No;              // past the window's sound
        int n = tokens.Count - prompt;
        bool last = n >= 1 && tokens[^1] >= TsBegin, penult = n < 2 || tokens[^2] >= TsBegin;
        if (last)
        {
            if (penult) for (int t = TsBegin; t < logits.Length; t++) logits[t] = No;         // after a pair: words (or the end)
            else for (int t = 0; t < Eot; t++) logits[t] = No;                                 // a segment's end: the next start (or the end)
        }
        int lastTs = -1;
        for (int i = tokens.Count - 1; i >= prompt; i--) if (tokens[i] >= TsBegin) { lastTs = tokens[i]; break; }
        if (lastTs >= 0) { int lo = last && !penult ? lastTs : lastTs + 1; for (int t = TsBegin; t < Math.Min(lo, logits.Length); t++) logits[t] = No; }
        if (n == 0)
        {
            for (int t = 0; t < TsBegin; t++) logits[t] = No;
            for (int t = TsBegin + 51; t < logits.Length; t++) logits[t] = No;
        }
        // log-softmax: all timestamps together vs the best word
        float max = float.MinValue; foreach (var v in logits) if (v > max) max = v;
        if (float.IsNegativeInfinity(max)) return;
        double sum = 0; foreach (var v in logits) if (!float.IsNegativeInfinity(v)) sum += Math.Exp(v - max);
        double lse = max + Math.Log(sum), tsSum = 0, bestText = double.NegativeInfinity;
        for (int t = TsBegin; t < logits.Length; t++) if (!float.IsNegativeInfinity(logits[t])) tsSum += Math.Exp(logits[t] - max);
        for (int t = 0; t < TsBegin; t++) if (logits[t] > bestText) bestText = logits[t];
        if (tsSum > 0 && max + Math.Log(tsSum) - lse > bestText - lse) for (int t = 0; t < TsBegin; t++) logits[t] = No;
    }

    /// <summary>Without sound descriptions — Whisper captions music / noise as "(crickets chirping)", "[Music]", "♪" — which
    /// aren't words anyone said.</summary>
    public static string Clean(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[[^\]]{0,60}\]|\([^)]{0,60}\)|\*[^*]{0,60}\*|[♪♫]+", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s.Any(char.IsLetter) ? s : "";
    }

    /// <summary>What Whisper typically "hears" in silence or music (from its subtitle training data).</summary>
    static readonly HashSet<string> Phantoms = new() { "thank you", "thanks for watching", "thank you for watching", "thank you so much for watching", "please subscribe",
        "subscribe", "you", "bye", "thank you very much", "thanks", "so", "okay", "subtitles by the amaraorg community" };
    static bool Hallucination(string s) => Phantoms.Contains(System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\s]", "").Trim());

    static double Softmax(float[] l, int k)
    {
        float m = l.Max(); double s = 0; foreach (var v in l) s += Math.Exp(v - m);
        return Math.Exp(l[k] - m) / s;
    }

    /// <summary>The same 4-token phrase 4 times in a row = Whisper looping; stop.</summary>
    static bool Repeats(List<int> t, int start)
    {
        if (t.Count - start < 16) return false;
        for (int k = 0; k < 4; k++) if (t[^(1 + k)] != t[^(5 + k)] || t[^(1 + k)] != t[^(9 + k)] || t[^(1 + k)] != t[^(13 + k)]) return false;
        return true;
    }

    float[] Logits(List<int> tokens, DenseTensor<float> states)
    {
        var ids = new DenseTensor<long>(tokens.Select(x => (long)x).ToArray(), new[] { 1, tokens.Count });
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(decIds, ids), NamedOnnxValue.CreateFromTensor(decStates, states) };
        using var o = dec.Run(inputs);
        var l = o.First(x => x.Name == decLogits).AsTensor<float>();
        int vocabN = l.Dimensions[2], last = tokens.Count - 1;
        var r = new float[vocabN];
        for (int i = 0; i < vocabN; i++) r[i] = l[0, last, i];
        return r;
    }

    string Decode(IEnumerable<int> ids)
    {
        var bytes = new List<byte>();
        foreach (var id in ids)
        {
            if (id >= Eot || !vocab.TryGetValue(id, out var s)) continue;
            foreach (var ch in s) if (byteOf.TryGetValue(ch, out var b)) bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>GPT-2 byte ↔ unicode table (printable bytes map to themselves, the rest to 256+).</summary>
    static Dictionary<char, byte> ByteDecoder()
    {
        var bs = new List<int>();
        for (int b = '!'; b <= '~'; b++) bs.Add(b);
        for (int b = 0xA1; b <= 0xAC; b++) bs.Add(b);
        for (int b = 0xAE; b <= 0xFF; b++) bs.Add(b);
        var cs = new List<int>(bs); int n = 0;
        for (int b = 0; b < 256; b++) if (!bs.Contains(b)) { bs.Add(b); cs.Add(256 + n++); }
        var d = new Dictionary<char, byte>();
        for (int i = 0; i < bs.Count; i++) d[(char)cs[i]] = (byte)bs[i];
        return d;
    }

    // ---------------- Whisper's log-mel front end ----------------
    /// <summary>80 × 3000 log-mel features of one 30 s window (zero-padded), exactly as Whisper computes them.</summary>
    public static float[] LogMel(float[] pcm, int offset, int count)
    {
        var x = new double[Chunk];
        for (int i = 0; i < count; i++) x[i] = pcm[offset + i];
        // reflect-pad by NFft/2 (center=True)
        int pad = NFft / 2;
        var xp = new double[Chunk + 2 * pad];
        for (int i = 0; i < Chunk; i++) xp[i + pad] = x[i];
        for (int i = 0; i < pad; i++) { xp[pad - 1 - i] = x[i + 1]; xp[pad + Chunk + i] = x[Chunk - 2 - i]; }
        Tables();
        var filters = melFilters!;
        int bins = NFft / 2 + 1;
        var mel = new float[Mels * Frames];
        var power = new double[bins];
        var frame = new double[NFft];
        double maxV = double.MinValue;
        var logm = new double[Mels * Frames];
        for (int f = 0; f < Frames; f++)
        {
            int s0 = f * Hop;
            for (int i = 0; i < NFft; i++) frame[i] = xp[s0 + i] * window![i];
            for (int k = 0; k < bins; k++)
            {
                double re = 0, im = 0; int idx = 0;
                for (int i = 0; i < NFft; i++) { re += frame[i] * cosT![idx]; im -= frame[i] * sinT![idx]; idx += k; if (idx >= NFft) idx -= NFft; }
                power[k] = re * re + im * im;
            }
            for (int m = 0; m < Mels; m++)
            {
                double s = 0; for (int k = 0; k < bins; k++) s += filters[m, k] * power[k];
                double lv = Math.Log10(Math.Max(s, 1e-10));
                logm[m * Frames + f] = lv; if (lv > maxV) maxV = lv;
            }
        }
        for (int i = 0; i < logm.Length; i++) mel[i] = (float)((Math.Max(logm[i], maxV - 8.0) + 4.0) / 4.0);
        return mel;
    }

    static void Tables()
    {
        if (melFilters != null) return;
        window = Enumerable.Range(0, NFft).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / NFft)).ToArray();   // periodic Hann
        cosT = Enumerable.Range(0, NFft).Select(i => Math.Cos(2 * Math.PI * i / NFft)).ToArray();
        sinT = Enumerable.Range(0, NFft).Select(i => Math.Sin(2 * Math.PI * i / NFft)).ToArray();
        // librosa.filters.mel(sr=16000, n_fft=400, n_mels=80), Slaney mel scale and area normalisation
        static double HzToMel(double f) => f < 1000 ? f / (200.0 / 3) : 15 + Math.Log(f / 1000) / (Math.Log(6.4) / 27);
        static double MelToHz(double m) => m < 15 ? m * (200.0 / 3) : 1000 * Math.Exp((m - 15) * (Math.Log(6.4) / 27));
        int bins = NFft / 2 + 1;
        var fftFreqs = Enumerable.Range(0, bins).Select(k => k * (double)Rate / NFft).ToArray();
        double mMin = HzToMel(0), mMax = HzToMel(Rate / 2.0);
        var pts = Enumerable.Range(0, Mels + 2).Select(i => MelToHz(mMin + (mMax - mMin) * i / (Mels + 1))).ToArray();
        var fb = new float[Mels, bins];
        for (int m = 0; m < Mels; m++)
        {
            double lo = pts[m], c = pts[m + 1], hi = pts[m + 2], norm = 2.0 / (hi - lo);
            for (int k = 0; k < bins; k++)
            {
                double f = fftFreqs[k], up = (f - lo) / (c - lo), down = (hi - f) / (hi - c);
                fb[m, k] = (float)(Math.Max(0, Math.Min(up, down)) * norm);
            }
        }
        melFilters = fb;
    }

    /// <summary>16 kHz mono PCM of a media file or of the timeline's mixed sound (via ffmpeg).</summary>
    public static float[] Pcm16k(string ff, string file, double maxSeconds = 0)
    {
        var psi = new ProcessStartInfo(ff) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string> { "-v", "error", "-i", file, "-vn" };
        if (maxSeconds > 0) args.AddRange(new[] { "-t", maxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-ac", "1", "-ar", "16000", "-f", "f32le", "pipe:1" });
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var ms = new MemoryStream(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        var b = ms.ToArray(); var r = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, r, 0, r.Length * 4);
        return r;
    }
}
