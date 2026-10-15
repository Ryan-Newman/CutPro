using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CutPro;

/// <summary>HTTP for the platform APIs: one client, JSON in/out, readable API errors. Tests point every API host at a local
/// mock server with <see cref="MockBase"/> (the path is kept, the host recorded in an X-Original-Host header).</summary>
public static class Http
{
    public static readonly HttpClient Client = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromHours(6) };
    /// <summary>Tests: "http://127.0.0.1:port" — every API URL goes there instead.</summary>
    public static string? MockBase;

    public static string Url(string url)
    {
        if (MockBase == null) return url;
        var u = new Uri(url);
        return MockBase.TrimEnd('/') + "/" + u.Host + u.PathAndQuery;
    }

    public static HttpRequestMessage Req(HttpMethod m, string url, string? bearer = null, object? json = null, string? authScheme = "Bearer")
    {
        var r = new HttpRequestMessage(m, Url(url));
        if (bearer != null) r.Headers.Authorization = new AuthenticationHeaderValue(authScheme ?? "Bearer", bearer);
        if (json != null) r.Content = new StringContent(json is JsonNode n ? n.ToJsonString() : JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
        return r;
    }

    public static async Task<JsonObject> SendJsonAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body)) return new JsonObject();
        try { return JsonNode.Parse(body) as JsonObject ?? new JsonObject { ["value"] = JsonNode.Parse(body) }; }
        catch (JsonException) { return new JsonObject { ["raw"] = body }; }
    }

    /// <summary>Sends; throws an exception with the API's own error message on a non-success status.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var res = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (res.IsSuccessStatusCode) return res;
        var body = await res.Content.ReadAsStringAsync(ct);
        string msg = body;
        try
        {
            var j = JsonNode.Parse(body);
            msg = (string?)j?["error"]?["message"] ?? (string?)j?["error_description"] ?? (string?)j?["error"]?["code"] ?? (string?)j?["detail"]
                  ?? (string?)j?["message"] ?? (string?)j?["title"] ?? (j?["error"] is JsonValue v ? v.ToString() : null) ?? body;
        }
        catch { }
        res.Dispose();
        throw new HttpRequestException($"{(int)res.StatusCode} {res.ReasonPhrase}: {Trim(msg)} ({req.Method} {new Uri(req.RequestUri!.ToString()).AbsolutePath})");
    }

    static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;

    /// <summary>A byte range of a file as content (for chunked uploads), reporting progress.</summary>
    public static ByteArrayContent Chunk(string file, long offset, int count, string contentType)
    {
        var buf = new byte[count];
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) { fs.Position = offset; int got = 0; while (got < count) { int n = fs.Read(buf, got, count - got); if (n <= 0) break; got += n; } }
        var c = new ByteArrayContent(buf);
        c.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return c;
    }


    /// <summary>multipart/form-data the way browsers send it — quoted field names, text fields without a Content-Type, an
    /// unquoted boundary. .NET's defaults (name=key, text/plain on every field, boundary="…") aren't accepted everywhere (S3).</summary>
    public static MultipartFormDataContent Form()
    {
        string boundary = "----CutPro" + Guid.NewGuid().ToString("N");
        var f = new MultipartFormDataContent(boundary);
        f.Headers.ContentType!.Parameters.First(p => p.Name == "boundary").Value = boundary;
        return f;
    }
    public static HttpContent Part(HttpContent c, string name, string? fileName = null)
    {
        c.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = $"\"{name}\"", FileName = fileName != null ? $"\"{fileName}\"" : null };
        return c;
    }
    public static HttpContent Field(string name, string value)
    {
        var c = new StringContent(value); c.Headers.ContentType = null;
        return Part(c, name);
    }
    public static string Mime(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".mov" => "video/quicktime", ".webm" => "video/webm", ".mkv" => "video/x-matroska", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        _ => "video/mp4",
    };
}
