using System.Net.Http;

namespace CutPro;

/// <summary>Downloads model files once into %LOCALAPPDATA%\CutPro\Models (resumable via .part files) and says whether a
/// set is present. Used by Whisper and CLIP (MobileSAM keeps its own copy of the same logic).</summary>
public static class ModelStore
{
    public static string Dir => SamEngine.ModelDir;
    public static string PathOf(string file) => Path.Combine(Dir, file);
    public static bool Present(IEnumerable<(string file, string url)> files) => files.All(f => File.Exists(PathOf(f.file)));

    public static async Task DownloadAsync((string file, string url)[] files, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        for (int i = 0; i < files.Length; i++)
        {
            var dst = PathOf(files[i].file);
            if (File.Exists(dst)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            using var resp = await http.GetAsync(files[i].url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0, got = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fs = File.Create(dst + ".part"))
            {
                var buf = new byte[1 << 16]; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct); got += n;
                    if (total > 0) progress?.Report((i + (double)got / total) / files.Length);
                }
            }
            File.Move(dst + ".part", dst, true);
        }
        progress?.Report(1);
    }
}
