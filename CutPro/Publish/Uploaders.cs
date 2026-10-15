using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CutPro;

/// <summary>What the platform told us after sign-in: account name and the ids uploads need (Page, Instagram account,
/// LinkedIn person, Pinterest boards, TikTok privacy options).</summary>
public static class Profile
{
    public static async Task FetchAsync(PlatformDef p, Account a, CancellationToken ct)
    {
        string t = a.AccessToken!;
        switch (p.Id)
        {
            case "youtube":
            {
                var j = await Http.SendJsonAsync(Http.Req(HttpMethod.Get, "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true", t), ct);
                var ch = j["items"]?.AsArray().FirstOrDefault();
                a.UserName = (string?)ch?["snippet"]?["title"] ?? "YouTube channel";
                if ((string?)ch?["id"] is { } id) a.Extra["channelId"] = id;
                break;
            }
            case "tiktok":
            {
                var j = await Http.SendJsonAsync(Http.Req(HttpMethod.Get, "https://open.tiktokapis.com/v2/user/info/?fields=open_id,display_name,username", t), ct);
                a.UserName = (string?)j["data"]?["user"]?["display_name"] ?? "TikTok account";
                if ((string?)j["data"]?["user"]?["username"] is { } un) a.Extra["username"] = un;
                await TikTokCreatorAsync(a, ct);
                break;
            }
            case "facebook":
            case "instagram":
            {
                var me = await Http.SendJsonAsync(Http.Req(HttpMethod.Get, $"https://graph.facebook.com/v23.0/me?fields=id,name&access_token={Uri.EscapeDataString(t)}"), ct);
                var pages = await Http.SendJsonAsync(Http.Req(HttpMethod.Get,
                    $"https://graph.facebook.com/v23.0/me/accounts?fields=id,name,access_token,instagram_business_account{{id,username}}&access_token={Uri.EscapeDataString(t)}"), ct);
                var list = pages["data"]?.AsArray() ?? new JsonArray();
                a.Extra["pages"] = string.Join("\n", list.Select(x => $"{x?["id"]}|{x?["name"]}"));
                foreach (var pg in list)
                    if ((string?)pg?["id"] is { } pid && (string?)pg?["access_token"] is { } ptok) a.Extra["pageToken:" + pid] = ptok;
                if (p.Id == "facebook")
                {
                    var first = list.FirstOrDefault();
                    a.UserName = first != null ? $"{me["name"]} → {first["name"]}" : $"{me["name"]} (no Pages found)";
                    if ((string?)first?["id"] is { } pid0) a.Defaults["page"] = pid0;
                }
                else
                {
                    var ig = list.Select(x => x?["instagram_business_account"]).FirstOrDefault(x => x != null);
                    if (ig == null) throw new Exception("No Instagram professional account is linked to your Facebook Pages. Switch Instagram to a Business or Creator account and link it to a Page.");
                    a.Extra["igUserId"] = (string)ig["id"]!;
                    a.UserName = "@" + ((string?)ig["username"] ?? "instagram");
                }
                break;
            }
            case "x":
            {
                var j = await Http.SendJsonAsync(Http.Req(HttpMethod.Get, "https://api.x.com/2/users/me", t), ct);
                a.UserName = "@" + ((string?)j["data"]?["username"] ?? "x");
                break;
            }
            case "linkedin":
            {
                var j = await Http.SendJsonAsync(Http.Req(HttpMethod.Get, "https://api.linkedin.com/v2/userinfo", t), ct);
                a.Extra["person"] = "urn:li:person:" + (string?)j["sub"];
                a.UserName = (string?)j["name"] ?? "LinkedIn member";
                break;
            }
            case "pinterest":
            {
                var j = await Http.SendJsonAsync(Http.Req(HttpMethod.Get, "https://api.pinterest.com/v5/user_account", t), ct);
                a.UserName = (string?)j["username"] ?? "Pinterest account";
                var boards = await Http.SendJsonAsync(Http.Req(HttpMethod.Get, "https://api.pinterest.com/v5/boards?page_size=100", t), ct);
                var items = boards["items"]?.AsArray() ?? new JsonArray();
                a.Extra["boards"] = string.Join("\n", items.Select(x => $"{x?["id"]}|{x?["name"]}"));
                if ((string?)items.FirstOrDefault()?["id"] is { } bid) a.Defaults["board"] = bid;
                break;
            }
            case "vimeo":
            {
                var j = await Http.SendJsonAsync(Vimeo(Http.Req(HttpMethod.Get, "https://api.vimeo.com/me", t)), ct);
                a.UserName = (string?)j["name"] ?? "Vimeo account";
                break;
            }
        }
    }

    /// <summary>TikTok: the privacy options and limits this creator has (the post form must offer exactly these).</summary>
    public static async Task TikTokCreatorAsync(Account a, CancellationToken ct)
    {
        var j = await Http.SendJsonAsync(Http.Req(HttpMethod.Post, "https://open.tiktokapis.com/v2/post/publish/creator_info/query/", a.AccessToken, new JsonObject()), ct);
        Uploaders.TikTokCheck(j);
        var d = j["data"];
        if (d?["privacy_level_options"] is JsonArray opts) a.Extra["privacyOptions"] = string.Join(",", opts.Select(o => o?.ToString()));
        if (d?["max_video_post_duration_sec"] is JsonNode m) a.Extra["maxSeconds"] = m.ToString();
        foreach (var k in new[] { "comment_disabled", "duet_disabled", "stitch_disabled" }) if (d?[k] is JsonNode v) a.Extra[k] = v.ToString();
        if ((string?)d?["creator_nickname"] is { } nick) a.UserName = nick;
    }

    internal static HttpRequestMessage Vimeo(HttpRequestMessage r)
    {
        r.Headers.Accept.ParseAdd("application/vnd.vimeo.*+json;version=3.4");
        return r;
    }
}

/// <summary>Uploads a finished video to a platform through its API and returns the post's address. Follows each platform's
/// documented flow (2026): YouTube resumable upload, TikTok Direct Post (chunked PUT), Instagram / Facebook Reels resumable
/// upload (rupload.facebook.com), X v2 chunked media upload, LinkedIn Videos + Posts API, Pinterest media + pin, Vimeo tus.</summary>
public static class Uploaders
{
    static string V(IReadOnlyDictionary<string, string> v, string k, string d = "") => v.TryGetValue(k, out var s) && s != null ? s : d;
    static bool B(IReadOnlyDictionary<string, string> v, string k, bool d) => v.TryGetValue(k, out var s) ? s == "true" : d;

    public static async Task<string> UploadAsync(PlatformDef p, string file, IReadOnlyDictionary<string, string> v, IProgress<double> progress, CancellationToken ct)
    {
        return p.Id switch
        {
            "youtube" => await YouTube(p, file, v, progress, ct),
            "tiktok" => await TikTok(p, file, v, progress, ct),
            "instagram" => await Instagram(p, file, v, progress, ct),
            "facebook" => await Facebook(p, file, v, progress, ct),
            "x" => await X(p, file, v, progress, ct),
            "linkedin" => await LinkedIn(p, file, v, progress, ct),
            "pinterest" => await Pinterest(p, file, v, progress, ct),
            "vimeo" => await Vimeo(p, file, v, progress, ct),
            _ => throw new NotSupportedException($"{p.Name} has no upload API — use \"Open {p.Name}\"."),
        };
    }

    // ---------------- YouTube: videos.insert resumable upload ----------------
    static async Task<string> YouTube(PlatformDef p, string file, IReadOnlyDictionary<string, string> v, IProgress<double> progress, CancellationToken ct)
    {
        var token = await Accounts.TokenAsync(p, ct);
        long size = new FileInfo(file).Length;
        var tags = new JsonArray(V(v, "tags").Split(',').Select(s => s.Trim().TrimStart('#')).Where(s => s.Length > 0).Select(s => (JsonNode)s).ToArray());
        string lang = V(v, "language", "en");
        var snippet = new JsonObject { ["title"] = V(v, "title"), ["description"] = V(v, "description"), ["tags"] = tags, ["categoryId"] = V(v, "category", "29"),
                                       ["defaultLanguage"] = lang, ["defaultAudioLanguage"] = lang };
        var status = new JsonObject
        {
            ["privacyStatus"] = V(v, "privacy", "private"), ["selfDeclaredMadeForKids"] = V(v, "madeForKids", "no") == "yes",
            ["containsSyntheticMedia"] = B(v, "synthetic", false), ["license"] = V(v, "license", "youtube"), ["embeddable"] = B(v, "embeddable", true),
        };
        if (DateTime.TryParse(V(v, "publishAt"), CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var at))
        { status["privacyStatus"] = "private"; status["publishAt"] = at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"); }
        var init = Http.Req(HttpMethod.Post, $"https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status&notifySubscribers={(B(v, "notify", true) ? "true" : "false")}",
                            token, new JsonObject { ["snippet"] = snippet, ["status"] = status });
        init.Headers.Add("X-Upload-Content-Length", size.ToString());
        init.Headers.Add("X-Upload-Content-Type", Http.Mime(file));
        string session;
        using (var r = await Http.SendAsync(init, ct)) session = r.Headers.Location?.ToString() ?? throw new Exception("YouTube didn't return an upload address.");
        // 8 MB chunks (a multiple of 256 KB); 308 = "resume incomplete", 200/201 = done
        const int chunk = 32 * 256 * 1024;
        JsonObject? video = null;
        for (long off = 0; off < size; off += chunk)
        {
            int n = (int)Math.Min(chunk, size - off);
            var put = new HttpRequestMessage(HttpMethod.Put, session.StartsWith("http") && Http.MockBase == null ? session : Http.Url(session)) { Content = Http.Chunk(file, off, n, Http.Mime(file)) };
            put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            put.Content.Headers.ContentRange = new ContentRangeHeaderValue(off, off + n - 1, size);
            using var res = await Http.Client.SendAsync(put, ct);
            if ((int)res.StatusCode == 308) { progress.Report((off + n) / (double)size); continue; }
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"YouTube upload failed: {(int)res.StatusCode} {body}");
            video = JsonNode.Parse(body) as JsonObject;
            progress.Report(1);
        }
        string id = (string?)video?["id"] ?? throw new Exception("YouTube didn't return the video id.");
        if (V(v, "thumbnail") is { Length: > 0 } thumb && File.Exists(thumb))
        {
            var r = Http.Req(HttpMethod.Post, $"https://www.googleapis.com/upload/youtube/v3/thumbnails/set?videoId={id}", token);
            r.Content = new ByteArrayContent(File.ReadAllBytes(thumb)); r.Content.Headers.ContentType = new MediaTypeHeaderValue(Http.Mime(thumb));
            (await Http.SendAsync(r, ct)).Dispose();
        }
        if (V(v, "playlist").Trim() is { Length: > 0 } pl)
            await Http.SendJsonAsync(Http.Req(HttpMethod.Post, "https://www.googleapis.com/youtube/v3/playlistItems?part=snippet", token,
                new JsonObject { ["snippet"] = new JsonObject { ["playlistId"] = pl, ["resourceId"] = new JsonObject { ["kind"] = "youtube#video", ["videoId"] = id } } }), ct);
        return $"https://youtu.be/{id}";
    }

    // ---------------- TikTok: Content Posting API, Direct Post, FILE_UPLOAD ----------------
    internal static void TikTokCheck(JsonObject j)
    {
        var code = (string?)j["error"]?["code"];
        if (code != null && code != "ok") throw new Exception($"TikTok: {code} — {(string?)j["error"]?["message"]}");
    }

    static async Task<string> TikTok(PlatformDef p, string file, IReadOnlyDictionary<string, string> v, IProgress<double> progress, CancellationToken ct)
    {
        var token = await Accounts.TokenAsync(p, ct);
        var a = Accounts.Get(p.Id);
        await Profile.TikTokCreatorAsync(a, ct);                       // TikTok requires a fresh creator-info check before each post
        string privacy = V(v, "privacy", "SELF_ONLY");
        if (a.Extra.TryGetValue("privacyOptions", out var opts) && opts.Length > 0 && !opts.Split(',').Contains(privacy))
            throw new Exception($"TikTok doesn't allow \"{privacy}\" for this account (allowed: {opts}).");
        long size = new FileInfo(file).Length;
        long chunk = size <= 64L << 20 ? size : 10L << 20;
        long count = Math.Max(1, size / chunk);                        // the remainder goes into the last chunk
        var post = new JsonObject
        {
            ["title"] = V(v, "caption"), ["privacy_level"] = privacy,
            ["disable_comment"] = !B(v, "comments", true), ["disable_duet"] = !B(v, "duet", true), ["disable_stitch"] = !B(v, "stitch", true),
            ["video_cover_timestamp_ms"] = (int)(double.TryParse(V(v, "cover", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cs) ? cs * 1000 : 1000),
            ["brand_content_toggle"] = B(v, "brandContent", false), ["brand_organic_toggle"] = B(v, "brandOrganic", false), ["is_aigc"] = B(v, "aigc", false),
        };
        var init = await Http.SendJsonAsync(Http.Req(HttpMethod.Post, "https://open.tiktokapis.com/v2/post/publish/video/init/", token,
            new JsonObject { ["post_info"] = post, ["source_info"] = new JsonObject { ["source"] = "FILE_UPLOAD", ["video_size"] = size, ["chunk_size"] = chunk, ["total_chunk_count"] = count } }), ct);
        TikTokCheck(init);
        string publishId = (string)init["data"]!["publish_id"]!, uploadUrl = (string)init["data"]!["upload_url"]!;
        for (long i = 0; i < count; i++)
        {
            long a0 = i * chunk, a1 = i == count - 1 ? size - 1 : a0 + chunk - 1;
            var put = new HttpRequestMessage(HttpMethod.Put, Http.Url(uploadUrl)) { Content = Http.Chunk(file, a0, (int)(a1 - a0 + 1), Http.Mime(file)) };
            put.Content.Headers.ContentRange = new ContentRangeHeaderValue(a0, a1, size);
            (await Http.SendAsync(put, ct)).Dispose();
            progress.Report((a1 + 1) / (double)size * 0.9);
        }
        for (int k = 0; k < 120; k++)
        {
            var st = await Http.SendJsonAsync(Http.Req(HttpMethod.Post, "https://open.tiktokapis.com/v2/post/publish/status/fetch/", token, new JsonObject { ["publish_id"] = publishId }), ct);
            TikTokCheck(st);
            string s = (string?)st["data"]?["status"] ?? "";
            if (s == "FAILED") throw new Exception("TikTok couldn't publish the video: " + (string?)st["data"]?["fail_reason"]);
            if (s == "PUBLISH_COMPLETE" || s == "SEND_TO_USER_INBOX")
            {
                progress.Report(1);
                var ids = st["data"]?["publicaly_available_post_id"]?.AsArray();
                return ids?.Count > 0 && a.Extra.TryGetValue("username", out var un) ? $"https://www.tiktok.com/@{un}/video/{ids[0]}" : "Posted to TikTok (it appears on your profile when processing finishes)";
            }
            await Task.Delay(Http.MockBase != null ? 50 : 3000, ct);
        }
        return "Uploaded to TikTok — still processing";
    }

    // ---------------- Instagram: Reels resumable upload ----------------
    static async Task<string> Instagram(PlatformDef p, string file, IReadOnlyDictionary<string, string> v, IProgress<double> progress, CancellationToken ct)
    {
        var token = await Accounts.TokenAsync(p, ct);
        var a = Accounts.Get(p.Id);
        string ig = a.Extra.GetValueOrDefault("igUserId") ?? throw new Exception("Reconnect Instagram (no professional account id).");
        long size = new FileInfo(file).Length;
        var q = new List<string> { "media_type=REELS", "upload_type=resumable", "caption=" + Uri.EscapeDataString(V(v, "caption")),
                                   "share_to_feed=" + (B(v, "shareToFeed", true) ? "true" : "false") };
        var collabs = V(v, "collaborators").Split(',').Select(s => s.Trim().TrimStart('@')).Where(s => s.Length > 0).Take(3).ToList();
        if (collabs.Count > 0) q.Add("collaborators=" + Uri.EscapeDataString(new JsonArray(collabs.Select(c => (JsonNode)c).ToArray()).ToJsonString()));
        var people = V(v, "people").Split(',').Select(s => s.Trim().TrimStart('@')).Where(s => s.Length > 0).ToList();
        if (people.Count > 0) q.Add("user_tags=" + Uri.EscapeDataString(new JsonArray(people.Select(u => (JsonNode)new JsonObject { ["username"] = u }).ToArray()).ToJsonString()));
        if (V(v, "location").Trim() is { Length: > 0 } loc) q.Add("location_id=" + Uri.EscapeDataString(loc));
        if (V(v, "audioName").Trim() is { Length: > 0 } an) q.Add("audio_name=" + Uri.EscapeDataString(an));
        if (double.TryParse(V(v, "cover", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cs)) q.Add("thumb_offset=" + (int)(cs * 1000));
        q.Add("access_token=" + Uri.EscapeDataString(token));
        var c = await Http.SendJsonAsync(Http.Req(HttpMethod.Post, $"https://graph.facebook.com/v23.0/{ig}/media?" + string.Join("&", q)), ct);
        string container = (string?)c["id"] ?? throw new Exception("Instagram didn't create the upload: " + c.ToJsonString());
        await RUpload($"https://rupload.facebook.com/ig-api-upload/v23.0/{container}", token, file, size, progress, ct);
        await WaitMetaAsync($"https://graph.facebook.com/v23.0/{container}?fields=status_code,status&access_token={Uri.EscapeDataString(token)}", "status_code", "FINISHED", ct);
        var pub = await Http.SendJsonAsync(Http.Req(HttpMethod.Post, $"https://graph.facebook.com/v23.0/{ig}/media_publish?creation_id={container}&access_token={Uri.EscapeDataString(token)}"), ct);
        string mediaId = (string?)pub["id"] ?? throw new Exception("Instagram didn't publish the Reel: " + pub.ToJsonString());
        try
        {
            var link = await Http.SendJsonAsync(Http.Req(HttpMethod.Get, $"https://graph.facebook.com/v23.0/{mediaId}?fields=permalink&access_token={Uri.EscapeDataString(token)}"), ct);
            return (string?)link["permalink"] ?? "Posted to Instagram";
        }
        catch { return "Posted to Instagram"; }
    }

    /// <summary>Meta resumable upload (rupload.facebook.com): the whole file from offset 0, headers offset / file_size.</summary>
    static async Task RUpload(string url, string token, string file, long size, IProgress<double> progress, CancellationToken ct)
    {
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        var req = new HttpRequestMessage(HttpMethod.Post, Http.Url(url)) { Content = new ProgressContent(fs, size, x => progress.Report(x * 0.85)) };
        req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        req.Headers.Add("offset", "0");
        req.Headers.Add("file_size", size.ToString());
        (await Http.SendAsync(req, ct)).Dispose();
    }

    static async Task WaitMetaAsync(string url, string field, string done, CancellationToken ct)
    {
        for (int k = 0; k < 200; k++)
        {
            var j = await Http.SendJsonAsync(Http.Req(HttpMethod.Get, url), ct);
            var s = (string?)j[field] ?? (string?)j["status"]?["video_status"] ?? "";
            if (s == done || s == "ready") return;
            if (s is "ERROR" or "EXPIRED" or "error") throw new Exception($"Processing failed: {j.ToJsonString()}");
            await Task.Delay(Http.MockBase != null ? 50 : 3000, ct);
        }
        throw new TimeoutException("The video is still processing — check the app in a few minutes.");
    }

    // ---------------- Facebook Page: Reels (video_reels) or video (resumable /videos) ----------------
    static async Task<string> Facebook(PlatformDef p, string file, IReadOnlyDictionary<string, string> v, IProgress<double> progress, CancellationToken ct)
    {
        await Accounts.TokenAsync(p, ct);
        var a = Accounts.Get(p.Id);
        string page = V(v, "page", a.Defaults.GetValueOrDefault("page", "")).Trim();
        string ptok = a.Extra.GetValueOrDefault("pageToken:" + page) ?? throw new Exception("Pick one of your Facebook Pages (reconnect if it's new).");
        long size = new FileInfo(file).Length;
        string when = V(v, "publish", "now");
        long? unix = DateTime.TryParse(V(v, "scheduleAt"), CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var at) ? new DateTimeOffset(at).ToUnixTimeSeconds() : null;
        string pt = Uri.EscapeDataString(ptok);
        if (V(v, "type", "reel") == "reel")
        {
            var s = await Http.SendJsonAsync(Http.Req(HttpMethod.Post, $"https://graph.facebook.com/v23.0/{page}/video_reels?upload_phase=start&access_token={pt}"), ct);
            string vid = (string?)s["video_id"] ?? throw new Exception("Facebook didn't start the Reel upload: " + s.ToJsonString());
            await RUpload($"https://rupload.facebook.com/video-upload/v23.0/{vid}", ptok, file, size, progress, ct);
            string state = when == "draft" ? "DRAFT" : when == "schedule" && unix != null ? "SCHEDULED" : "PUBLISHED";
            var fin = await Http.SendJsonAsync(Http.Req(HttpMethod.Post, $"https://graph.facebook.com/v23.0/{page}/video_reels?upload_phase=finish&video_id={vid}&video_state={state}" +
                $"&description={Uri.EscapeDataString(V(v, "description"))}" + (state == "SCHEDULED" ? $"&scheduled_publish_time={unix}" : "") + $"&access_token={pt}"), ct);
            if (fin["success"] is JsonNode ok && ok.ToString() != "true") throw new Exception("Facebook didn't publish the Reel: " + fin.ToJsonString());
            progress.Report(1);
            return $"https://www.facebook.com/reel/{vid}";
        }
        // video: resumable start → transfer chunks → finish
        var st = await Http.SendJsonAsync(Http.Req(HttpMethod.Post, $"https://graph-video.facebook.com/v23.0/{page}/videos?upload_phase=start&file_size={size}&access_token={pt}"), ct);
        string sess = (string)st["upload_session_id"]!, videoId = (string?)st["video_id"] ?? "";
        long start = long.Parse(st["start_offset"]!.ToString()), end = long.Parse(st["end_offset"]!.ToString());
        while (start < end)
        {
            var form = Http.Form();
            form.Add(Http.Field("upload_phase", "transfer")); form.Add(Http.Field("upload_session_id", sess)); form.Add(Http.Field("start_offset", start.ToString()));
            form.Add(Http.Part(Http.Chunk(file, start, (int)(end - start), "application/octet-stream"), "video_file_chunk", "chunk"));
            var tr = await Http.SendJsonAsync(new HttpRequestMessage(HttpMethod.Post, Http.Url($"https://graph-video.facebook.com/v23.0/{page}/videos?access_token={pt}")) { Content = form }, ct);
            start = long.Parse(tr["start_offset"]!.ToString()); end = long.Parse(tr["end_offset"]!.ToString());
            progress.Report(Math.Min(start, size) / (double)size * 0.95);
        }
        string q = $"upload_phase=finish&upload_session_id={sess}&title={Uri.EscapeDataString(V(v, "title"))}&description={Uri.EscapeDataString(V(v, "description"))}";
        if (when == "draft") q += "&published=false";
        if (when == "schedule" && unix != null) q += $"&published=false&scheduled_publish_time={unix}";
        await Http.SendJsonAsync(Http.Req(HttpMethod.Post, $"https://graph-video.facebook.com/v23.0/{page}/videos?{q}&access_token={pt}"), ct);
        progress.Report(1);
        return videoId.Length > 0 ? $"https://www.facebook.com/{page}/videos/{videoId}" : "Posted to Facebook";
    }

    // ---------------- X: v2 chunked media upload + post ----------------
    static async Task<string> X(PlatformDef p, string file, IReadOnlyDictionary<string, string> v, IProgress<double> progress, CancellationToken ct)
    {
        var token = await Accounts.TokenAsync(p, ct);
        long size = new FileInfo(file).Length;
        var init = await Http.SendJsonAsync(Http.Req(HttpMethod.Post, "https://api.x.com/2/media/upload/initialize", token,
            new JsonObject { ["media_type"] = Http.Mime(file), ["total_bytes"] = size, ["media_category"] = "tweet_video" }), ct);
        string id = (string?)init["data"]?["id"] ?? throw new Exception("X didn't start the upload: " + init.ToJsonString());
        const int chunk = 4 << 20;
        int seg = 0;
        for (long off = 0; off < size; off += chunk, seg++)
        {
            int n = (int)Math.Min(chunk, size - off);
            var form = Http.Form();
            form.Add(Http.Field("segment_index", seg.ToString())); form.Add(Http.Part(Http.Chunk(file, off, n, "application/octet-stream"), "media", "blob"));
            var r = Http.Req(HttpMethod.Post, $"https://api.x.com/2/media/upload/{id}/append", token); r.Content = form;
            (await Http.SendAsync(r, ct)).Dispose();
            progress.Report((off + n) / (double)size * 0.85);
        }
        var fin = await Http.SendJsonAsync(Http.Req(HttpMethod.Post, $"https://api.x.com/2/media/upload/{id}/finalize", token), ct);
        var info = fin["data"]?["processing_info"];
        for (int k = 0; k < 200 && info != null; k++)
        {
            string state = (string?)info["state"] ?? "succeeded";
            if (state == "succeeded") break;
            if (state == "failed") throw new Exception("X couldn't process the video: " + info.ToJsonString());
            int wait = int.TryParse(info["check_after_secs"]?.ToString(), out var w) ? w : 2;
            await Task.Delay(Http.MockBase != null ? 50 : wait * 1000, ct);
            var s = await Http.SendJsonAsync(Http.Req(HttpMethod.Get, $"https://api.x.com/2/media/upload?command=STATUS&media_id={id}", token), ct);
            info = s["data"]?["processing_info"];
        }
        var post = new JsonObject { ["text"] = V(v, "text"), ["media"] = new JsonObject { ["media_ids"] = new JsonArray(id) } };
        if (V(v, "replies", "everyone") is var rs && rs != "everyone") post["reply_settings"] = rs;
        var tw = await Http.SendJsonAsync(Http.Req(HttpMethod.Post, "https://api.x.com/2/tweets", token, post), ct);
        progress.Report(1);
        return $"https://x.com/i/web/status/{(string?)tw["data"]?["id"]}";
    }

    // ---------------- LinkedIn: Videos API (multipart upload) + Posts API ----------------
    const string LinkedInVersion = "202606";
    static HttpRequestMessage Li(HttpMethod m, string url, string token, JsonNode? body = null)
    {
        var r = Http.Req(m, url, token, body);
        r.Headers.Add("LinkedIn-Version", LinkedInVersion);
        r.Headers.Add("X-Restli-Protocol-Version", "2.0.0");
        return r;
    }

    /// <summary>LinkedIn "little text": reserved characters escaped; #hashtags become hashtag templates.</summary>
    public static string LittleText(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '#' && i + 1 < s.Length && (char.IsLetterOrDigit(s[i + 1]) || s[i + 1] == '_'))
            {
                int j = i + 1; while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                sb.Append("{hashtag|\\#|").Append(s[(i + 1)..j]).Append('}'); i = j - 1; continue;
            }
            if ("|{}@[]()<>#\\*_~".Contains(ch)) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    static async Task<string> LinkedIn(PlatformDef p, string file, IReadOnlyDictionary<string, string> v, IProgress<double> progress, CancellationToken ct)
    {
        var token = await Accounts.TokenAsync(p, ct);
        string owner = Accounts.Get(p.Id).Extra.GetValueOrDefault("person") ?? throw new Exception("Reconnect LinkedIn (no member id).");
        long size = new FileInfo(file).Length;
        var init = await Http.SendJsonAsync(Li(HttpMethod.Post, "https://api.linkedin.com/rest/videos?action=initializeUpload", token,
            new JsonObject { ["initializeUploadRequest"] = new JsonObject { ["owner"] = owner, ["fileSizeBytes"] = size, ["uploadCaptions"] = false, ["uploadThumbnail"] = false } }), ct);
        var val = init["value"]!;
        string video = (string)val["video"]!, uploadToken = (string?)val["uploadToken"] ?? "";
        var etags = new JsonArray();
        foreach (var ins in val["uploadInstructions"]!.AsArray())
        {
            long a0 = long.Parse(ins!["firstByte"]!.ToString()), a1 = long.Parse(ins["lastByte"]!.ToString());
            var put = new HttpRequestMessage(HttpMethod.Put, Http.Url((string)ins["uploadUrl"]!)) { Content = Http.Chunk(file, a0, (int)(a1 - a0 + 1), "application/octet-stream") };
            put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await Http.SendAsync(put, ct);
            etags.Add(res.Headers.ETag?.Tag.Trim('"') ?? (res.Headers.TryGetValues("etag", out var e) ? e.First().Trim('"') : ""));
            progress.Report((a1 + 1) / (double)size * 0.9);
        }
        await Http.SendJsonAsync(Li(HttpMethod.Post, "https://api.linkedin.com/rest/videos?action=finalizeUpload", token,
            new JsonObject { ["finalizeUploadRequest"] = new JsonObject { ["video"] = video, ["uploadToken"] = uploadToken, ["uploadedPartIds"] = etags } }), ct);
        var post = new JsonObject
        {
            ["author"] = owner, ["commentary"] = LittleText(V(v, "text")), ["visibility"] = V(v, "visibility", "PUBLIC"),
            ["distribution"] = new JsonObject { ["feedDistribution"] = "MAIN_FEED", ["targetEntities"] = new JsonArray(), ["thirdPartyDistributionChannels"] = new JsonArray() },
            ["content"] = new JsonObject { ["media"] = new JsonObject { ["title"] = V(v, "title"), ["id"] = video } },
            ["lifecycleState"] = "PUBLISHED", ["isReshareDisabledByAuthor"] = false,
        };
        using var pr = await Http.SendAsync(Li(HttpMethod.Post, "https://api.linkedin.com/rest/posts", token, post), ct);
        progress.Report(1);
        string urn = pr.Headers.TryGetValues("x-restli-id", out var ids) ? ids.First() : "";
        return urn.Length > 0 ? $"https://www.linkedin.com/feed/update/{urn}" : "Posted to LinkedIn";
    }

    // ---------------- Pinterest: register media → upload (S3 form) → pin ----------------
    static async Task<string> Pinterest(PlatformDef p, string file, IReadOnlyDictionary<string, string> v, IProgress<double> progress, CancellationToken ct)
    {
        var token = await Accounts.TokenAsync(p, ct);
        var reg = await Http.SendJsonAsync(Http.Req(HttpMethod.Post, "https://api.pinterest.com/v5/media", token, new JsonObject { ["media_type"] = "video" }), ct);
        string mediaId = (string)reg["media_id"]!, url = (string)reg["upload_url"]!;
        var form = Http.Form();                                         // S3 POST policy: the signed fields first, the file last
        if (reg["upload_parameters"] is JsonObject pars) foreach (var (k, val) in pars) form.Add(Http.Field(k, val?.ToString() ?? ""));
        await using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true))
        {
            var body = new ProgressContent(fs, fs.Length, x => progress.Report(x * 0.85));
            body.Headers.ContentType = new MediaTypeHeaderValue(Http.Mime(file));
            form.Add(Http.Part(body, "file", Path.GetFileName(file)));
            (await Http.SendAsync(new HttpRequestMessage(HttpMethod.Post, Http.Url(url)) { Content = form }, ct)).Dispose();
        }
        for (int k = 0; k < 200; k++)
        {
            var st = await Http.SendJsonAsync(Http.Req(HttpMethod.Get, $"https://api.pinterest.com/v5/media/{mediaId}", token), ct);
            var s = (string?)st["status"];
            if (s == "succeeded") break;
            if (s == "failed") throw new Exception("Pinterest couldn't process the video.");
            await Task.Delay(Http.MockBase != null ? 50 : 3000, ct);
        }
        double cover = double.TryParse(V(v, "cover", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cs) ? cs : 1;
        var pin = new JsonObject
        {
            ["board_id"] = V(v, "board"), ["title"] = V(v, "title"), ["description"] = V(v, "description"), ["alt_text"] = V(v, "alt"),
            ["media_source"] = new JsonObject { ["source_type"] = "video_id", ["media_id"] = mediaId, ["cover_image_key_frame_time"] = cover },
        };
        if (V(v, "link").Trim() is { Length: > 0 } link) pin["link"] = link;
        var res = await Http.SendJsonAsync(Http.Req(HttpMethod.Post, "https://api.pinterest.com/v5/pins", token, pin), ct);
        progress.Report(1);
        return $"https://www.pinterest.com/pin/{(string?)res["id"]}/";
    }

    // ---------------- Vimeo: tus upload ----------------
    static async Task<string> Vimeo(PlatformDef p, string file, IReadOnlyDictionary<string, string> v, IProgress<double> progress, CancellationToken ct)
    {
        var token = await Accounts.TokenAsync(p, ct);
        long size = new FileInfo(file).Length;
        var body = new JsonObject
        {
            ["upload"] = new JsonObject { ["approach"] = "tus", ["size"] = size.ToString() },
            ["name"] = V(v, "title"), ["description"] = V(v, "description"),
            ["privacy"] = new JsonObject { ["view"] = V(v, "privacy", "anybody"), ["embed"] = V(v, "embed", "public"), ["download"] = B(v, "downloads", false) },
        };
        if (V(v, "privacy") == "password") body["password"] = V(v, "password");
        if (V(v, "license") is { Length: > 0 } lic) body["license"] = lic;
        var created = await Http.SendJsonAsync(Profile.Vimeo(Http.Req(HttpMethod.Post, "https://api.vimeo.com/me/videos", token, body)), ct);
        string upload = (string)created["upload"]!["upload_link"]!, uri = (string?)created["uri"] ?? "", link = (string?)created["link"] ?? "";
        const int chunk = 32 << 20;
        long off = 0;
        while (off < size)
        {
            int n = (int)Math.Min(chunk, size - off);
            var patch = new HttpRequestMessage(HttpMethod.Patch, Http.Url(upload)) { Content = Http.Chunk(file, off, n, "application/offset+octet-stream") };
            patch.Headers.Add("Tus-Resumable", "1.0.0");
            patch.Headers.Add("Upload-Offset", off.ToString());
            using var res = await Http.SendAsync(patch, ct);
            off = res.Headers.TryGetValues("Upload-Offset", out var o) && long.TryParse(o.First(), out var no) ? no : off + n;
            progress.Report(off / (double)size);
        }
        var tags = V(v, "tags").Split(',').Select(s => s.Trim().TrimStart('#')).Where(s => s.Length > 0).ToList();
        if (tags.Count > 0 && uri.Length > 0)
            (await Http.SendAsync(Profile.Vimeo(Http.Req(HttpMethod.Put, $"https://api.vimeo.com{uri}/tags", token,
                new JsonArray(tags.Select(t => (JsonNode)new JsonObject { ["name"] = t }).ToArray()))), ct)).Dispose();
        return link.Length > 0 ? link : "Uploaded to Vimeo";
    }

    /// <summary>Stream content that reports how much has been sent (0..1).</summary>
    sealed class ProgressContent(Stream src, long length, Action<double> report) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var buf = new byte[1 << 20]; long sent = 0; int n;
            while ((n = await src.ReadAsync(buf)) > 0) { await stream.WriteAsync(buf.AsMemory(0, n)); sent += n; report(sent / (double)Math.Max(1, length)); }
        }
        protected override bool TryComputeLength(out long l) { l = length; return true; }
    }
}
