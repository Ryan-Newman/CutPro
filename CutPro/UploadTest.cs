using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace CutPro;

/// <summary>CutPro.exe --uploadtest dir: every platform's sign-in and upload against a local fake API server (nothing leaves the
/// PC; accounts go to dir, not %APPDATA%). Checks the requests each platform's documentation requires — OAuth parameters
/// (PKCE, client id names, token exchange), upload protocol (resumable / chunked / tus / S3 form), byte ranges adding up to
/// the file, field mapping, token refresh — and that API errors reach the user readably.</summary>
public static class UploadTest
{
    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        using var mock = new MockApi();
        Http.MockBase = mock.Base;
        Accounts.StoreOverride = dir; Accounts.Reload();
        try { File.Delete(Path.Combine(dir, "accounts.dat")); } catch { }
        var authUrls = new Dictionary<string, string>();
        string? current = null;
        using var browser = new HttpClient();
        Accounts.OpenBrowser = url =>
        {   // the "user" signs in and the platform redirects back to CutPro's loopback listener
            authUrls[current!] = url;
            var q = HttpUtility.ParseQueryString(new Uri(url).Query);
            _ = Task.Run(async () => { await Task.Delay(30); await browser.GetStringAsync($"{q["redirect_uri"]}?code=code-{current}&state={q["state"]}"); });
        };

        // test "video": 9 MB + a bit, so chunked uploads take several requests
        var file = Path.Combine(dir, "upload.mp4");
        var data = new byte[9 * 1024 * 1024 + 12345]; new Random(7).NextBytes(data); File.WriteAllBytes(file, data);
        long size = data.Length;
        var thumb = Path.Combine(dir, "thumb.png"); File.WriteAllBytes(thumb, new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });

        try
        {
            foreach (var p in Platforms.All.Where(p => p.Auth != null))
            {
                current = p.Id;
                var a = Accounts.Get(p.Id); a.ClientId = "client-" + p.Id; a.ClientSecret = "secret-" + p.Id;
                mock.Clear();
                try
                {
                    await Accounts.ConnectAsync(p, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
                    var q = HttpUtility.ParseQueryString(new Uri(authUrls[p.Id]).Query);
                    bool meta = p.Id is "facebook" or "instagram", pkce = !meta && p.Id != "linkedin";
                    var tokReq = mock.Log.First(r => r.Url.Contains(new Uri(p.Auth!.TokenUrl).AbsolutePath.TrimEnd('/')) && !r.Url.Contains("fb_exchange_token"));
                    var form = HttpUtility.ParseQueryString(meta ? tokReq.Query : tokReq.Text);
                    string idParam = p.Auth!.ClientIdParam;
                    bool ok = q[idParam] == a.ClientId && q["response_type"] == "code" && q["redirect_uri"] == Accounts.Redirect(p) && q["scope"] == p.Auth.Scopes
                              && (pkce ? q["code_challenge_method"] == "S256" && q["code_challenge"]?.Length is 43 or 64 && form["code_verifier"]?.Length > 40 : q["code_challenge"] == null)
                              && form["code"] == "code-" + p.Id && form["grant_type"] == "authorization_code" && form["redirect_uri"] == Accounts.Redirect(p)
                              && (p.Auth.BasicAuthToken ? tokReq.Header("Authorization")?.StartsWith("Basic ") == true : form["client_secret"] == a.ClientSecret)
                              && a.Connected && a.UserName != null;
                    if (p.Id == "tiktok") ok &= q["code_challenge"]!.Length == 64 && q["code_challenge"]!.All(Uri.IsHexDigit) && a.Extra.GetValueOrDefault("privacyOptions") == "PUBLIC_TO_EVERYONE,SELF_ONLY";
                    if (p.Id == "youtube") ok &= q["access_type"] == "offline";
                    if (meta) ok &= a.AccessToken == "long-lived" && mock.Log.Any(r => r.Query.Contains("fb_exchange_token"));
                    if (p.Id == "instagram") ok &= a.Extra.GetValueOrDefault("igUserId") == "IG1";
                    if (p.Id == "facebook") ok &= a.Defaults.GetValueOrDefault("page") == "PAGE1" && a.Extra.GetValueOrDefault("pageToken:PAGE1") == "page-token";
                    if (p.Id == "pinterest") ok &= a.Defaults.GetValueOrDefault("board") == "B1";
                    if (p.Id == "linkedin") ok &= a.Extra.GetValueOrDefault("person") == "urn:li:person:abc";
                    Check(ok, $"{p.Name}: sign-in ({(pkce ? "PKCE " + (p.Auth.HexChallenge ? "hex" : "base64url") : meta ? "Meta, long-lived token" : "client secret")}, " +
                              $"{(p.Auth.BasicAuthToken ? "Basic auth" : "secret in form")}) → {a.UserName}" + (ok ? "" : $" [auth {authUrls[p.Id]} | token {tokReq.Method} {tokReq.Url} {tokReq.Text} | at {a.AccessToken}]"));
                }
                catch (Exception ex) { Check(false, $"{p.Name}: sign-in: {ex.Message}"); }
            }
            Accounts.Reload();
            Check(Accounts.All.Count >= 8 && Accounts.Get("youtube").AccessToken == "tok-youtube" && !File.ReadAllText(Path.Combine(dir, "accounts.dat"), Encoding.Latin1).Contains("tok-youtube"),
                  "accounts saved encrypted (DPAPI) and read back");

            // ---- uploads ----
            async Task<(string? url, string? err)> Up(string id, Dictionary<string, string> v)
            {
                mock.Clear();
                try { return (await Uploaders.UploadAsync(Platforms.Get(id), file, v, new Progress<double>(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)), null); }
                catch (Exception ex) { return (null, ex.Message); }
            }
            long Sum(IEnumerable<MockApi.Rec> rs) => rs.Sum(r => (long)r.Body.Length);

            {   // YouTube: resumable session, 8 MB chunks, 308 between, thumbnail, playlist
                var (url, err) = await Up("youtube", new() { ["title"] = "Grace Upon Grace", ["description"] = "Hope #faith", ["tags"] = "faith, holy spirit", ["privacy"] = "unlisted",
                                                          ["madeForKids"] = "no", ["thumbnail"] = thumb, ["playlist"] = "PL123", ["publishAt"] = "" });
                var init = mock.Log.FirstOrDefault(r => r.Method == "POST" && r.Url.Contains("/upload/youtube/v3/videos"));
                var meta = init?.Json;
                var puts = mock.Log.Where(r => r.Method == "PUT").ToList();
                bool ok = err == null && url == "https://youtu.be/vid123" && init?.Query.Contains("uploadType=resumable") == true && init.Header("X-Upload-Content-Length") == size.ToString()
                          && (string?)meta?["snippet"]?["title"] == "Grace Upon Grace" && meta?["snippet"]?["tags"]?.AsArray().Count == 2 && (string?)meta?["status"]?["privacyStatus"] == "unlisted"
                          && meta?["status"]?["selfDeclaredMadeForKids"]?.GetValue<bool>() == false
                          && puts.Count == 2 && Sum(puts) == size && puts[0].Header("Content-Range") == $"bytes 0-{8 * 1024 * 1024 - 1}/{size}"
                          && mock.Log.Any(r => r.Url.Contains("thumbnails/set?videoId=vid123") && r.Body.Length == 7) && mock.Log.Any(r => r.Url.Contains("playlistItems"))
                          && puts.All(r => r.Header("Authorization") == "Bearer tok-youtube");
                Check(ok, $"YouTube upload: {url ?? err}; {puts.Count} chunks, {Sum(puts)} / {size} bytes, thumbnail + playlist");
            }
            {   // TikTok: creator info first, FILE_UPLOAD with chunk rules, PUT with Content-Range, status poll
                var (url, err) = await Up("tiktok", new() { ["caption"] = "Grace #faith", ["privacy"] = "SELF_ONLY", ["duet"] = "false", ["cover"] = "2.5" });
                var init = mock.Log.FirstOrDefault(r => r.Url.Contains("/video/init/"))?.Json;
                int iCreator = mock.Log.FindIndex(r => r.Url.Contains("creator_info")), iInit = mock.Log.FindIndex(r => r.Url.Contains("/video/init/"));
                var puts = mock.Log.Where(r => r.Method == "PUT").ToList();
                bool ok = err == null && url == "https://www.tiktok.com/@gracechurch/video/7123" && iCreator >= 0 && iCreator < iInit
                          && (string?)init?["post_info"]?["title"] == "Grace #faith" && (string?)init?["post_info"]?["privacy_level"] == "SELF_ONLY"
                          && init?["post_info"]?["disable_duet"]?.GetValue<bool>() == true && init?["post_info"]?["video_cover_timestamp_ms"]?.GetValue<int>() == 2500
                          && (string?)init?["source_info"]?["source"] == "FILE_UPLOAD" && init?["source_info"]?["video_size"]?.GetValue<long>() == size
                          && init?["source_info"]?["total_chunk_count"]?.GetValue<long>() == 1 && puts.Count == 1 && Sum(puts) == size && puts[0].Header("Content-Range") == $"bytes 0-{size - 1}/{size}";
                Check(ok, $"TikTok upload: {url ?? err}; creator info checked first, 1 chunk of {size} bytes, status polled {mock.Log.Count(r => r.Url.Contains("status/fetch"))}×");
                var (_, err2) = await Up("tiktok", new() { ["caption"] = "x", ["privacy"] = "FOLLOWER_OF_CREATOR" });
                Check(err2?.Contains("doesn't allow") == true && !mock.Log.Any(r => r.Url.Contains("/video/init/")), $"TikTok: a privacy option the creator doesn't have is refused before uploading ({err2})");
            }
            {   // Instagram: Reels container (resumable) → rupload → status → publish → permalink
                var (url, err) = await Up("instagram", new() { ["caption"] = "Grace upon grace 🙏 #faith", ["collaborators"] = "@friend1, friend2", ["shareToFeed"] = "false", ["cover"] = "3" });
                var c = mock.Log.FirstOrDefault(r => r.Url.Contains("/IG1/media") && !r.Url.Contains("publish"));
                var qs = HttpUtility.ParseQueryString(c?.Query ?? "");
                var up = mock.Log.FirstOrDefault(r => r.Host == "rupload.facebook.com");
                bool ok = err == null && url == "https://www.instagram.com/reel/abc/" && qs["media_type"] == "REELS" && qs["upload_type"] == "resumable" && qs["caption"] == "Grace upon grace 🙏 #faith"
                          && qs["share_to_feed"] == "false" && qs["collaborators"] == "[\"friend1\",\"friend2\"]" && qs["thumb_offset"] == "3000"
                          && up?.Path == "/ig-api-upload/v23.0/C1" && up.Header("Authorization") == "OAuth long-lived" && up.Header("offset") == "0" && up.Header("file_size") == size.ToString() && up.Body.Length == size
                          && mock.Log.Any(r => r.Url.Contains("media_publish") && r.Query.Contains("creation_id=C1"));
                Check(ok, $"Instagram Reel: {url ?? err}; container → rupload {up?.Body.Length} bytes → FINISHED → publish");
            }
            {   // Facebook Reel (page token) and Facebook video (resumable chunks, scheduled)
                var (url, err) = await Up("facebook", new() { ["type"] = "reel", ["page"] = "PAGE1", ["description"] = "Sunday #faith", ["publish"] = "now" });
                var fin = mock.Log.FirstOrDefault(r => r.Query.Contains("upload_phase=finish"));
                var up = mock.Log.FirstOrDefault(r => r.Host == "rupload.facebook.com");
                bool ok = err == null && url == "https://www.facebook.com/reel/V1" && up?.Path == "/video-upload/v23.0/V1" && up.Header("Authorization") == "OAuth page-token" && up.Body.Length == size
                          && fin != null && HttpUtility.ParseQueryString(fin.Query)["video_state"] == "PUBLISHED" && HttpUtility.ParseQueryString(fin.Query)["description"] == "Sunday #faith"
                          && mock.Log.All(r => r.Host != "graph.facebook.com" || r.Query.Contains("access_token=page-token"));
                Check(ok, $"Facebook Reel: {url ?? err}; page token, rupload {up?.Body.Length} bytes, PUBLISHED");
                var (url2, err2) = await Up("facebook", new() { ["type"] = "video", ["page"] = "PAGE1", ["title"] = "Sermon", ["description"] = "Full message", ["publish"] = "schedule", ["scheduleAt"] = "2026-12-24 18:00" });
                var transfers = mock.Log.Where(r => r.Host == "graph-video.facebook.com" && r.Header("Content-Type")?.StartsWith("multipart/form-data") == true).ToList();
                var fin2 = HttpUtility.ParseQueryString(mock.Log.LastOrDefault(r => r.Query.Contains("upload_phase=finish"))?.Query ?? "");
                bool ok2 = err2 == null && url2 == "https://www.facebook.com/PAGE1/videos/V2" && transfers.Count == 3 && fin2["title"] == "Sermon" && fin2["published"] == "false"
                           && fin2["scheduled_publish_time"] == new DateTimeOffset(new DateTime(2026, 12, 24, 18, 0, 0, DateTimeKind.Local)).ToUnixTimeSeconds().ToString();
                Check(ok2, $"Facebook video: {url2 ?? err2}; {transfers.Count} transfer chunks, scheduled");
            }
            {   // X: initialize / append segments / finalize / status / post
                var (url, err) = await Up("x", new() { ["text"] = "Grace upon grace #faith", ["replies"] = "following" });
                var init = mock.Log.FirstOrDefault(r => r.Url.EndsWith("/2/media/upload/initialize"))?.Json;
                var appends = mock.Log.Where(r => r.Url.Contains("/append")).ToList();
                var tweet = mock.Log.FirstOrDefault(r => r.Url.EndsWith("/2/tweets"))?.Json;
                bool ok = err == null && url == "https://x.com/i/web/status/t1" && init?["total_bytes"]?.GetValue<long>() == size && (string?)init?["media_category"] == "tweet_video"
                          && appends.Count == 3 && appends.Select((r, i) => MockApi.Field(Encoding.Latin1.GetString(r.Body), "segment_index") == i.ToString()).All(b => b) && mock.Log.Any(r => r.Url.Contains("/finalize"))
                          && mock.Log.Any(r => r.Query.Contains("command=STATUS")) && (string?)tweet?["text"] == "Grace upon grace #faith"
                          && (string?)tweet?["media"]?["media_ids"]?[0] == "m1" && (string?)tweet?["reply_settings"] == "following";
                Check(ok, $"X post: {url ?? err}; {appends.Count} segments, processing polled, reply settings");
                var (_, err2) = await Up("x", new() { ["text"] = "FORBIDDEN" });
                Check(err2?.Contains("not permitted") == true, $"X: an API error reaches the user readably: {err2}");
            }
            {   // LinkedIn: initializeUpload → PUT parts (ETags) → finalizeUpload → post (little text)
                var (url, err) = await Up("linkedin", new() { ["text"] = "Grace (John 1:16) #faith", ["title"] = "Grace", ["visibility"] = "PUBLIC" });
                var parts = mock.Log.Where(r => r.Method == "PUT").ToList();
                var fin = mock.Log.FirstOrDefault(r => r.Query.Contains("finalizeUpload"))?.Json;
                var post = mock.Log.FirstOrDefault(r => r.Url.EndsWith("/rest/posts"));
                bool ok = err == null && url == "https://www.linkedin.com/feed/update/urn:li:share:1" && parts.Count == 2 && Sum(parts) == size
                          && fin?["finalizeUploadRequest"]?["uploadedPartIds"]?.AsArray().Select(x => (string?)x).SequenceEqual(new[] { "etag-a1", "etag-a2" }) == true
                          && post?.Header("LinkedIn-Version") == "202606" && post.Header("X-Restli-Protocol-Version") == "2.0.0"
                          && (string?)post.Json?["commentary"] == @"Grace \(John 1:16\) {hashtag|\#|faith}" && (string?)post.Json?["author"] == "urn:li:person:abc"
                          && (string?)post.Json?["content"]?["media"]?["id"] == "urn:li:video:1";
                Check(ok, $"LinkedIn post: {url ?? err}; 2 parts, ETags finalized, little-text commentary");
            }
            {   // Pinterest: register media → S3 form (fields before the file) → status → pin
                var (url, err) = await Up("pinterest", new() { ["board"] = "B1", ["title"] = "Grace", ["description"] = "Hope #faith", ["alt"] = "A cross", ["link"] = "https://example.org", ["cover"] = "1.5" });
                var s3 = mock.Log.FirstOrDefault(r => r.Host.Contains("amazonaws"));
                var pin = mock.Log.FirstOrDefault(r => r.Url.EndsWith("/v5/pins"))?.Json;
                string s3text = s3 != null ? Encoding.Latin1.GetString(s3.Body) : "";
                int ik = System.Text.RegularExpressions.Regex.Match(s3text, "name=\"?key\"?").Index, ifl = System.Text.RegularExpressions.Regex.Match(s3text, "name=\"?file\"?").Index;
                Check(s3text.Contains("Content-Disposition: form-data; name=\"key\"\r\n\r\n") && !s3text.Contains("text/plain") && s3text.Contains("name=\"file\"; filename=\"upload.mp4\"")
                      && s3?.Header("Content-Type")?.Contains("boundary=\"") == false,
                      "multipart like a browser (S3 / X / Meta): quoted names, text fields without a type, unquoted boundary: " +
                      string.Join(" | ", s3text.Split("\r\n").Where(x => x.StartsWith("Content-")).Take(3)));
                bool ok = err == null && url == "https://www.pinterest.com/pin/PIN1/" && ik > 0 && ifl > ik && MockApi.Field(s3text, "key") == "uploads/k"
                          && MockApi.Field(s3text, "policy") == "pol" && s3!.Body.Length > size && (string?)pin?["board_id"] == "B1" && (string?)pin?["media_source"]?["media_id"] == "PM1"
                          && (string?)pin?["media_source"]?["source_type"] == "video_id" && pin?["media_source"]?["cover_image_key_frame_time"]?.GetValue<double>() == 1.5 && (string?)pin?["alt_text"] == "A cross";
                Check(ok, $"Pinterest pin: {url ?? err}; S3 form upload, media id on the pin");
            }
            {   // Vimeo: create (tus) → PATCH with offsets → tags
                var (url, err) = await Up("vimeo", new() { ["title"] = "Grace", ["description"] = "Message", ["tags"] = "faith, grace", ["privacy"] = "unlisted", ["downloads"] = "true" });
                var create = mock.Log.FirstOrDefault(r => r.Method == "POST" && r.Url.EndsWith("/me/videos"));
                var patches = mock.Log.Where(r => r.Method == "PATCH").ToList();
                bool ok = err == null && url == "https://vimeo.com/99" && (string?)create?.Json?["upload"]?["approach"] == "tus" && (string?)create?.Json?["upload"]?["size"] == size.ToString()
                          && (string?)create?.Json?["privacy"]?["view"] == "unlisted" && create.Header("Accept")?.Contains("version=3.4") == true
                          && patches.Count == 2 && patches[0].Body.Length == size && patches[1].Body.Length == size - size / 2 && patches.All(r => r.Header("Tus-Resumable") == "1.0.0" && r.Header("Content-Type") == "application/offset+octet-stream")
                          && patches[1].Header("Upload-Offset") == (size / 2).ToString() && mock.Log.Any(r => r.Method == "PUT" && r.Url.EndsWith("/videos/99/tags"));
                Check(ok, $"Vimeo upload: {url ?? err}; tus {patches.Count} PATCHes (server-reported offsets followed), tags");
            }
            {   // expired token → refresh before the upload
                var a = Accounts.Get("x"); a.Expires = DateTime.UtcNow.AddMinutes(-5); a.RefreshToken = "ref-x";
                mock.Clear();
                var t = await Accounts.TokenAsync(Platforms.Get("x"), CancellationToken.None);
                var r = mock.Log.FirstOrDefault(r => r.Url.Contains("/2/oauth2/token"));
                var form = HttpUtility.ParseQueryString(r?.Text ?? "");
                Check(t == "tok-x-refreshed" && form["grant_type"] == "refresh_token" && form["refresh_token"] == "ref-x" && a.Expires > DateTime.UtcNow, "expired token refreshed before uploading");
            }
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        finally { Http.MockBase = null; Accounts.StoreOverride = null; Accounts.Reload(); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(dir, "uploadtest.txt"), log);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>Minimal HTTP/1.1 server on 127.0.0.1 (TcpListener: no admin rights / URL reservation) answering like each API.</summary>
    sealed class MockApi : IDisposable
    {
        public sealed record Rec(string Method, string Host, string Path, string Query, Dictionary<string, string> Headers, byte[] Body)
        {
            public string Url => $"https://{Host}{Path}{(Query.Length > 0 ? "?" + Query : "")}";
            public string Text => Encoding.UTF8.GetString(Body);
            public JsonNode? Json { get { try { return JsonNode.Parse(Text); } catch { return null; } } }
            public string? Header(string k) => Headers.TryGetValue(k.ToLowerInvariant(), out var v) ? v : null;
        }
        readonly TcpListener l = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource cts = new();
        readonly List<Rec> log = new();
        int statusCalls;
        public string Base => $"http://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}";
        public List<Rec> Log { get { lock (log) return log.ToList(); } }
        public void Clear() { lock (log) log.Clear(); statusCalls = 0; }

        public MockApi() { l.Start(); _ = Task.Run(Accept); }
        public void Dispose() { cts.Cancel(); l.Stop(); }

        async Task Accept()
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient c; try { c = await l.AcceptTcpClientAsync(cts.Token); } catch { return; }
                _ = Task.Run(() => Serve(c));
            }
        }

        async Task Serve(TcpClient c)
        {
            using var _ = c;
            var s = c.GetStream();
            try
            {
                while (true)
                {
                    string? line = ReadLine(s); if (string.IsNullOrEmpty(line)) return;
                    var parts = line.Split(' ');
                    var h = new Dictionary<string, string>();
                    for (string? hl; !string.IsNullOrEmpty(hl = ReadLine(s));) { int i = hl.IndexOf(':'); if (i > 0) h[hl[..i].Trim().ToLowerInvariant()] = hl[(i + 1)..].Trim(); }
                    byte[] body;
                    if (h.TryGetValue("content-length", out var cl)) { body = new byte[long.Parse(cl)]; await s.ReadExactlyAsync(body); }
                    else if (h.GetValueOrDefault("transfer-encoding") == "chunked")
                    {
                        using var ms = new MemoryStream();
                        while (true) { int n = Convert.ToInt32(ReadLine(s)!.Split(';')[0], 16); var b = new byte[n]; await s.ReadExactlyAsync(b); ms.Write(b); ReadLine(s); if (n == 0) break; }
                        body = ms.ToArray();
                    }
                    else body = Array.Empty<byte>();
                    // /{host}/{path}?{query} (Http.MockBase layout)
                    string target = parts[1]; int qi = target.IndexOf('?');
                    string pathAll = qi < 0 ? target : target[..qi], query = qi < 0 ? "" : target[(qi + 1)..];
                    int si = pathAll.IndexOf('/', 1);
                    var rec = new Rec(parts[0], si < 0 ? pathAll[1..] : pathAll[1..si], si < 0 ? "/" : pathAll[si..], query, h, body);
                    lock (log) log.Add(rec);
                    var (status, json, extra) = Answer(rec);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    var head = new StringBuilder($"HTTP/1.1 {status} {(status < 300 ? "OK" : status == 308 ? "Resume Incomplete" : "Error")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\n");
                    foreach (var (k, v) in extra) head.Append($"{k}: {v}\r\n");
                    head.Append("\r\n");
                    await s.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()).Concat(bytes).ToArray());
                }
            }
            catch { }
        }

        /// <summary>Value of a multipart/form-data text field (name quoted or not, any part headers).</summary>
        public static string? Field(string body, string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(body, $"name=\"?{System.Text.RegularExpressions.Regex.Escape(name)}\"?(?:;[^\\r\\n]*)?\\r\\n(?:[^\\r\\n]+\\r\\n)*\\r\\n([^\\r\\n]*)");
            return m.Success ? m.Groups[1].Value : null;
        }

        static string? ReadLine(Stream s)
        {
            var sb = new StringBuilder();
            while (true)
            {
                int b = s.ReadByte(); if (b < 0) return sb.Length > 0 ? sb.ToString() : null;
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)b);
            }
        }

        (int status, string json, Dictionary<string, string> headers) Answer(Rec r)
        {
            var none = new Dictionary<string, string>();
            string u = r.Url, M = r.Method;
            long Range(string? cr) => cr != null && cr.StartsWith("bytes ") ? long.Parse(cr[6..].Split('/')[0].Split('-')[1]) + 1 : 0;
            long Total(string? cr) => cr != null && cr.Contains('/') ? long.Parse(cr.Split('/')[1]) : 0;
            var q = HttpUtility.ParseQueryString(r.Query);
            // OAuth token endpoints
            if (u.Contains("fb_exchange_token")) return (200, "{\"access_token\":\"long-lived\",\"token_type\":\"bearer\",\"expires_in\":5183944}", none);
            if (u.Contains("oauth2.googleapis.com/token") || u.Contains("/v2/oauth/token") || u.Contains("/oauth/access_token") || u.Contains("/2/oauth2/token")
                || u.Contains("/oauth/v2/accessToken") || u.Contains("/v5/oauth/token"))
            {
                var form = HttpUtility.ParseQueryString(M == "GET" ? r.Query : r.Text);
                string who = r.Host.Contains("google") ? "youtube" : r.Host.Contains("tiktok") ? "tiktok" : r.Host.Contains("facebook") ? "meta" : r.Host.Contains("x.com") ? "x"
                           : r.Host.Contains("linkedin") ? "linkedin" : r.Host.Contains("pinterest") ? "pinterest" : "vimeo";
                string tok = form["grant_type"] == "refresh_token" ? $"tok-{who}-refreshed" : $"tok-{who}";
                return (200, $"{{\"access_token\":\"{tok}\",\"refresh_token\":\"ref-{who}\",\"expires_in\":3600,\"token_type\":\"Bearer\"}}", none);
            }
            // profiles
            if (u.Contains("youtube/v3/channels")) return (200, "{\"items\":[{\"id\":\"UC1\",\"snippet\":{\"title\":\"Grace Church\"}}]}", none);
            if (u.Contains("/v2/user/info/")) return (200, "{\"data\":{\"user\":{\"display_name\":\"Grace Church\",\"username\":\"gracechurch\"}},\"error\":{\"code\":\"ok\"}}", none);
            if (u.Contains("creator_info/query")) return (200, "{\"data\":{\"privacy_level_options\":[\"PUBLIC_TO_EVERYONE\",\"SELF_ONLY\"],\"max_video_post_duration_sec\":600,\"creator_nickname\":\"Grace Church\",\"duet_disabled\":false},\"error\":{\"code\":\"ok\"}}", none);
            if (u.Contains("graph.facebook.com/v23.0/me/accounts")) return (200, "{\"data\":[{\"id\":\"PAGE1\",\"name\":\"Grace Church\",\"access_token\":\"page-token\",\"instagram_business_account\":{\"id\":\"IG1\",\"username\":\"gracechurch\"}}]}", none);
            if (u.Contains("graph.facebook.com/v23.0/me?")) return (200, "{\"id\":\"1\",\"name\":\"Pat\"}", none);
            if (u.EndsWith("api.x.com/2/users/me")) return (200, "{\"data\":{\"id\":\"9\",\"username\":\"gracechurch\"}}", none);
            if (u.Contains("/v2/userinfo")) return (200, "{\"sub\":\"abc\",\"name\":\"Pat Example\"}", none);
            if (u.Contains("/v5/user_account")) return (200, "{\"username\":\"gracechurch\"}", none);
            if (u.Contains("/v5/boards")) return (200, "{\"items\":[{\"id\":\"B1\",\"name\":\"Sermons\"}]}", none);
            if (u.EndsWith("api.vimeo.com/me")) return (200, "{\"name\":\"Grace Church\"}", none);
            // YouTube
            if (M == "POST" && u.Contains("/upload/youtube/v3/videos")) return (200, "{}", new() { ["Location"] = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&upload_id=xyz" });
            if (M == "PUT" && u.Contains("upload_id=xyz"))
            {
                long end = Range(r.Header("content-range")), total = Total(r.Header("content-range"));
                return end < total ? (308, "", new() { ["Range"] = $"bytes=0-{end - 1}" }) : (200, "{\"id\":\"vid123\",\"kind\":\"youtube#video\"}", none);
            }
            if (u.Contains("thumbnails/set") || u.Contains("playlistItems")) return (200, "{}", none);
            // TikTok
            if (u.Contains("/post/publish/video/init/")) return (200, "{\"data\":{\"publish_id\":\"p1\",\"upload_url\":\"https://open-upload.tiktokapis.com/video/?upload_id=u1\"},\"error\":{\"code\":\"ok\"}}", none);
            if (M == "PUT" && r.Host == "open-upload.tiktokapis.com") return (201, "", none);
            if (u.Contains("/status/fetch/"))
                return ++statusCalls < 2 ? (200, "{\"data\":{\"status\":\"PROCESSING_UPLOAD\"},\"error\":{\"code\":\"ok\"}}", none)
                                         : (200, "{\"data\":{\"status\":\"PUBLISH_COMPLETE\",\"publicaly_available_post_id\":[7123]},\"error\":{\"code\":\"ok\"}}", none);
            // Instagram
            if (M == "POST" && u.Contains("/v23.0/IG1/media_publish")) return (200, "{\"id\":\"M1\"}", none);
            if (M == "POST" && u.Contains("/v23.0/IG1/media")) return (200, "{\"id\":\"C1\",\"uri\":\"https://rupload.facebook.com/ig-api-upload/v23.0/C1\"}", none);
            if (r.Host == "rupload.facebook.com") return (200, "{\"success\":true,\"message\":\"Upload successful.\"}", none);
            if (u.Contains("/v23.0/C1?")) return ++statusCalls < 2 ? (200, "{\"status_code\":\"IN_PROGRESS\"}", none) : (200, "{\"status_code\":\"FINISHED\"}", none);
            if (u.Contains("/v23.0/M1?")) return (200, "{\"permalink\":\"https://www.instagram.com/reel/abc/\"}", none);
            // Facebook
            if (u.Contains("/PAGE1/video_reels") && q["upload_phase"] == "start") return (200, "{\"video_id\":\"V1\",\"upload_url\":\"https://rupload.facebook.com/video-upload/v23.0/V1\"}", none);
            if (u.Contains("/PAGE1/video_reels") && q["upload_phase"] == "finish") return (200, "{\"success\":true}", none);
            if (r.Host == "graph-video.facebook.com")
            {
                const long chunk = 4 * 1024 * 1024;
                if (q["upload_phase"] == "start") return (200, $"{{\"upload_session_id\":\"S1\",\"video_id\":\"V2\",\"start_offset\":\"0\",\"end_offset\":\"{Math.Min(chunk, long.Parse(q["file_size"]!))}\"}}", none);
                if (q["upload_phase"] == "finish") return (200, "{\"success\":true}", none);
                // transfer: multipart with start_offset + chunk
                var txt = Encoding.Latin1.GetString(r.Body);
                long st = long.Parse(Field(txt, "start_offset") ?? "0"), got = st + chunk, size = 9 * 1024 * 1024 + 12345;
                long next = Math.Min(got, size);
                return (200, $"{{\"start_offset\":\"{next}\",\"end_offset\":\"{Math.Min(next + chunk, size)}\"}}", none);
            }
            // X
            if (u.EndsWith("/2/media/upload/initialize")) return (200, "{\"data\":{\"id\":\"m1\",\"expires_after_secs\":86400}}", none);
            if (u.Contains("/2/media/upload/m1/append")) return (204, "", none);
            if (u.Contains("/2/media/upload/m1/finalize")) return (200, "{\"data\":{\"id\":\"m1\",\"processing_info\":{\"state\":\"pending\",\"check_after_secs\":1}}}", none);
            if (u.Contains("command=STATUS")) return (200, "{\"data\":{\"id\":\"m1\",\"processing_info\":{\"state\":\"succeeded\",\"progress_percent\":100}}}", none);
            if (u.EndsWith("/2/tweets"))
                return r.Text.Contains("FORBIDDEN") ? (403, "{\"title\":\"Forbidden\",\"detail\":\"You are not permitted to perform this action.\",\"type\":\"about:blank\",\"status\":403}", none)
                                                    : (201, "{\"data\":{\"id\":\"t1\",\"text\":\"ok\"}}", none);
            // LinkedIn
            if (u.Contains("/rest/videos") && r.Query.Contains("initializeUpload"))
            {
                long size = r.Json?["initializeUploadRequest"]?["fileSizeBytes"]?.GetValue<long>() ?? 0, half = 4 * 1024 * 1024;
                return (200, $"{{\"value\":{{\"video\":\"urn:li:video:1\",\"uploadToken\":\"\",\"uploadUrlsExpireAt\":1,\"uploadInstructions\":[" +
                             $"{{\"uploadUrl\":\"https://www.linkedin.com/dms-uploads/a1\",\"firstByte\":0,\"lastByte\":{half - 1}}}," +
                             $"{{\"uploadUrl\":\"https://www.linkedin.com/dms-uploads/a2\",\"firstByte\":{half},\"lastByte\":{size - 1}}}]}}}}", none);
            }
            if (M == "PUT" && u.Contains("/dms-uploads/")) return (200, "", new() { ["ETag"] = $"\"etag-{r.Path.Split('/').Last()}\"" });
            if (u.Contains("/rest/videos") && r.Query.Contains("finalizeUpload")) return (200, "", none);
            if (u.EndsWith("/rest/posts")) return (201, "", new() { ["x-restli-id"] = "urn:li:share:1" });
            // Pinterest
            if (M == "POST" && u.EndsWith("/v5/media")) return (201, "{\"media_id\":\"PM1\",\"media_type\":\"video\",\"upload_url\":\"https://pinterest-media-upload.s3-accelerate.amazonaws.com/\",\"upload_parameters\":{\"key\":\"uploads/k\",\"policy\":\"pol\",\"x-amz-signature\":\"sig\"}}", none);
            if (r.Host.Contains("amazonaws")) return (204, "", none);
            if (u.Contains("/v5/media/PM1")) return ++statusCalls < 2 ? (200, "{\"status\":\"processing\"}", none) : (200, "{\"status\":\"succeeded\"}", none);
            if (u.EndsWith("/v5/pins")) return (201, "{\"id\":\"PIN1\"}", none);
            // Vimeo
            if (M == "POST" && u.EndsWith("api.vimeo.com/me/videos")) return (200, "{\"uri\":\"/videos/99\",\"link\":\"https://vimeo.com/99\",\"upload\":{\"approach\":\"tus\",\"upload_link\":\"https://files.tus.vimeo.com/files/abc\"}}", none);
            if (M == "PATCH" && r.Host == "files.tus.vimeo.com")
            {   // accept only half of the first PATCH: the client must continue from the offset the server reports
                long off = long.Parse(r.Header("upload-offset") ?? "0"), now = off == 0 ? r.Body.Length / 2 : off + r.Body.Length;
                return (204, "", new() { ["Upload-Offset"] = now.ToString(), ["Tus-Resumable"] = "1.0.0" });
            }
            if (M == "PUT" && u.Contains("/videos/99/tags")) return (200, "[]", none);
            return (404, $"{{\"error\":{{\"message\":\"mock: no route for {r.Method} {u}\"}}}}", none);
        }
    }
}
