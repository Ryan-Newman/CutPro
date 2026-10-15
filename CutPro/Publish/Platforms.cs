namespace CutPro;

public enum FieldKind { Text, Multiline, Tags, Choice, Bool, DateTime, Number, Image }

/// <summary>One field of a platform's upload form (named after the platform's own form / API field).</summary>
public sealed record PField(string Id, string Label, FieldKind Kind, int Max = 0, string Default = "", string[]? Options = null,
                            string? Help = null, bool Required = false, string? Api = null);

/// <summary>OAuth 2.0 endpoints of a platform (authorization-code flow with PKCE, loopback redirect to this PC).</summary>
public sealed record AuthDef(string AuthorizeUrl, string TokenUrl, string Scopes, string ClientIdParam = "client_id", bool HexChallenge = false,
                             bool BasicAuthToken = false, string ScopeSeparator = " ");

/// <summary>A social platform: its upload form, limits, hashtag rules and how CutPro uploads to it.</summary>
public sealed record PlatformDef(
    string Id, string Name, string Color, string Glyph,
    PField[] Fields,
    int HashtagLimit, int HashtagSuggest, string HashtagWhere,     // where hashtags go: description | caption | text | tags
    double MaxSeconds, long MaxBytes, string Formats,              // limits shown in the publish window
    string Upload,                                                  // how: "api" (direct upload) or "intent" (opens the app / site)
    AuthDef? Auth, string DevPortal, string Setup, string Notes);

public static class Platforms
{
    // shared field sets
    static readonly string[] YtCategories =
    {
        "Film & Animation|1", "Autos & Vehicles|2", "Music|10", "Pets & Animals|15", "Sports|17", "Travel & Events|19", "Gaming|20",
        "People & Blogs|22", "Comedy|23", "Entertainment|24", "News & Politics|25", "Howto & Style|26", "Education|27",
        "Science & Technology|28", "Nonprofits & Activism|29",
    };
    static readonly string[] Languages = { "English|en", "Spanish|es", "Portuguese|pt", "French|fr", "German|de", "Korean|ko", "Chinese|zh", "Tagalog|tl", "Swahili|sw", "Hindi|hi", "Arabic|ar", "Russian|ru" };

    public static readonly PlatformDef[] All =
    {
        new("youtube", "YouTube", "#FFFF0000", "",
            new PField[]
            {
                new("title", "Title", FieldKind.Text, 100, Required: true, Help: "Shown above the video. Up to 100 characters.", Api: "snippet.title"),
                new("description", "Description", FieldKind.Multiline, 5000, Help: "Up to 5,000 characters. The first hashtags (up to 3) show above the title.", Api: "snippet.description"),
                new("tags", "Tags", FieldKind.Tags, 500, Help: "Keywords, comma separated (500 characters in total). Help with misspellings and search.", Api: "snippet.tags"),
                new("category", "Category", FieldKind.Choice, Default: "29", Options: YtCategories, Api: "snippet.categoryId"),
                new("privacy", "Visibility", FieldKind.Choice, Default: "private", Options: new[] { "Private|private", "Unlisted|unlisted", "Public|public" }, Api: "status.privacyStatus"),
                new("publishAt", "Schedule", FieldKind.DateTime, Help: "Publish later (the video stays private until then).", Api: "status.publishAt"),
                new("madeForKids", "Audience: made for kids", FieldKind.Choice, Default: "no", Required: true,
                    Options: new[] { "No, it's not made for kids|no", "Yes, it's made for kids|yes" }, Help: "Required by law (COPPA). Made-for-kids videos have comments and some features turned off.", Api: "status.selfDeclaredMadeForKids"),
                new("synthetic", "Altered or synthetic content", FieldKind.Bool, Default: "false", Help: "Disclose realistic content made or changed with AI.", Api: "status.containsSyntheticMedia"),
                new("playlist", "Playlist ID", FieldKind.Text, Help: "Optional: add the video to a playlist (the ID from the playlist's URL, list=…).", Api: "playlistItems.insert"),
                new("thumbnail", "Thumbnail", FieldKind.Image, Help: "1280×720 JPG/PNG under 2 MB (needs a verified channel). Leave empty to let YouTube pick.", Api: "thumbnails.set"),
                new("license", "License", FieldKind.Choice, Default: "youtube", Options: new[] { "Standard YouTube License|youtube", "Creative Commons – Attribution|creativeCommon" }, Api: "status.license"),
                new("embeddable", "Allow embedding", FieldKind.Bool, Default: "true", Api: "status.embeddable"),
                new("notify", "Publish to subscriptions feed and notify subscribers", FieldKind.Bool, Default: "true", Api: "notifySubscribers"),
                new("language", "Video language", FieldKind.Choice, Default: "en", Options: Languages, Api: "snippet.defaultLanguage"),
            },
            HashtagLimit: 15, HashtagSuggest: 5, HashtagWhere: "description", MaxSeconds: 12 * 3600, MaxBytes: 256L << 30,
            Formats: "16:9 up to 4K · Shorts: vertical 9:16 or square, up to 3 min (#Shorts helps)", Upload: "api",
            Auth: new("https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token",
                      "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube"),
            DevPortal: "https://console.cloud.google.com/apis/credentials",
            Setup: "Google Cloud console → create a project → enable \"YouTube Data API v3\" → OAuth consent screen (add yourself as a test user) → Credentials → Create OAuth client ID → Desktop app. Paste the Client ID and Client secret here.",
            Notes: "Videos uploaded by apps that Google hasn't audited are locked to Private until the app passes an audit. More than 15 hashtags makes YouTube ignore them all."),

        new("tiktok", "TikTok", "#FF25F4EE", "",
            new PField[]
            {
                new("caption", "Caption", FieldKind.Multiline, 2200, Help: "Up to 2,200 characters, with #hashtags and @mentions.", Api: "post_info.title"),
                new("privacy", "Who can watch", FieldKind.Choice, Default: "SELF_ONLY", Required: true,
                    Options: new[] { "Everyone|PUBLIC_TO_EVERYONE", "Friends|MUTUAL_FOLLOW_FRIENDS", "Followers|FOLLOWER_OF_CREATOR", "Only me|SELF_ONLY" },
                    Help: "The options your account allows come from TikTok when you connect.", Api: "post_info.privacy_level"),
                new("comments", "Allow comments", FieldKind.Bool, Default: "true", Api: "post_info.disable_comment"),
                new("duet", "Allow Duet", FieldKind.Bool, Default: "true", Api: "post_info.disable_duet"),
                new("stitch", "Allow Stitch", FieldKind.Bool, Default: "true", Api: "post_info.disable_stitch"),
                new("cover", "Cover frame (seconds)", FieldKind.Number, Default: "1", Help: "Which moment of the video is the cover.", Api: "post_info.video_cover_timestamp_ms"),
                new("brandOrganic", "Disclose: promotes your own brand", FieldKind.Bool, Default: "false", Help: "Labels the video \"Promotional content\".", Api: "post_info.brand_organic_toggle"),
                new("brandContent", "Disclose: branded content (paid partnership)", FieldKind.Bool, Default: "false", Help: "Labels the video \"Paid partnership\"; can't be private.", Api: "post_info.brand_content_toggle"),
                new("aigc", "AI-generated content", FieldKind.Bool, Default: "false", Api: "post_info.is_aigc"),
            },
            HashtagLimit: 30, HashtagSuggest: 5, HashtagWhere: "caption", MaxSeconds: 600, MaxBytes: 4L << 30,
            Formats: "Vertical 9:16, 1080×1920, up to 10 min", Upload: "api",
            Auth: new("https://www.tiktok.com/v2/auth/authorize/", "https://open.tiktokapis.com/v2/oauth/token/", "user.info.basic,video.publish",
                      ClientIdParam: "client_key", HexChallenge: true, ScopeSeparator: ","),
            DevPortal: "https://developers.tiktok.com/apps",
            Setup: "TikTok for Developers → Manage apps → Create an app → add Login Kit and Content Posting API (Direct Post) → redirect URI http://127.0.0.1:53682/callback → request the video.publish scope. Paste the Client key and Client secret here.",
            Notes: "Until TikTok audits the app, posts are visible to you only (SELF_ONLY). By posting you agree to TikTok's Music Usage Confirmation."),

        new("instagram", "Instagram", "#FFE1306C", "",
            new PField[]
            {
                new("caption", "Caption", FieldKind.Multiline, 2200, Help: "Up to 2,200 characters and at most 5 hashtags (Instagram's limit since December 2025).", Api: "caption"),
                new("shareToFeed", "Also share to Feed", FieldKind.Bool, Default: "true", Help: "Show the Reel in your profile grid / Feed as well as the Reels tab.", Api: "share_to_feed"),
                new("collaborators", "Collaborators", FieldKind.Tags, 200, Help: "Up to 3 Instagram usernames, comma separated.", Api: "collaborators"),
                new("people", "Tag people", FieldKind.Tags, 300, Help: "Usernames, comma separated.", Api: "user_tags"),
                new("location", "Location (Facebook Page ID)", FieldKind.Text, Help: "Optional place: the ID of a Facebook Page with a location.", Api: "location_id"),
                new("cover", "Cover frame (seconds)", FieldKind.Number, Default: "1", Api: "thumb_offset"),
                new("audioName", "Audio name", FieldKind.Text, 60, Help: "Name for the Reel's original audio (once, can't be changed).", Api: "audio_name"),
            },
            HashtagLimit: 5, HashtagSuggest: 5, HashtagWhere: "caption", MaxSeconds: 1200, MaxBytes: 1L << 30,
            Formats: "Reels: 9:16, 1080×1920; under 90 s reaches the most people", Upload: "api",
            Auth: new("https://www.facebook.com/v23.0/dialog/oauth", "https://graph.facebook.com/v23.0/oauth/access_token",
                      "instagram_basic,instagram_content_publish,pages_show_list,pages_read_engagement,business_management", ScopeSeparator: ","),
            DevPortal: "https://developers.facebook.com/apps",
            Setup: "Needs an Instagram professional (Business or Creator) account linked to a Facebook Page. Meta for Developers → Create app (type: Business) → add Instagram Graph API / Facebook Login → valid OAuth redirect URI http://127.0.0.1:53682/callback. Paste the App ID and App secret here (the same app serves Facebook).",
            Notes: "Hashtags over 5 make Instagram reject the post."),

        new("facebook", "Facebook", "#FF1877F2", "",
            new PField[]
            {
                new("type", "Post as", FieldKind.Choice, Default: "reel", Options: new[] { "Reel (vertical, up to 90 s)|reel", "Video|video" }),
                new("page", "Page ID", FieldKind.Text, Required: true, Help: "The Facebook Page to post to (filled in when you connect; pick one if you manage several).", Api: "page-id"),
                new("title", "Title", FieldKind.Text, 255, Help: "Video posts only.", Api: "title"),
                new("description", "Description", FieldKind.Multiline, 5000, Help: "Text of the post, with #hashtags.", Api: "description"),
                new("publish", "Publish", FieldKind.Choice, Default: "now", Options: new[] { "Now|now", "Schedule|schedule", "Save as draft|draft" }, Api: "video_state / published"),
                new("scheduleAt", "Schedule time", FieldKind.DateTime, Help: "10 minutes to 30 days ahead.", Api: "scheduled_publish_time"),
            },
            HashtagLimit: 10, HashtagSuggest: 3, HashtagWhere: "description", MaxSeconds: 4 * 3600, MaxBytes: 10L << 30,
            Formats: "Reels: 9:16, up to 90 s · Videos: any shape", Upload: "api",
            Auth: new("https://www.facebook.com/v23.0/dialog/oauth", "https://graph.facebook.com/v23.0/oauth/access_token",
                      "pages_show_list,pages_read_engagement,pages_manage_posts,business_management", ScopeSeparator: ","),
            DevPortal: "https://developers.facebook.com/apps",
            Setup: "Meta for Developers → Create app (type: Business) → add Facebook Login → valid OAuth redirect URI http://127.0.0.1:53682/callback. Paste the App ID and App secret here. CutPro posts to Pages you manage.",
            Notes: "Posting to a personal profile isn't possible through Meta's API — only to Pages."),

        new("x", "X (Twitter)", "#FF000000", "",
            new PField[]
            {
                new("text", "Post", FieldKind.Multiline, 280, Help: "280 characters (links count as 23). Premium accounts can post longer text.", Api: "text"),
                new("premium", "I have Premium (longer posts and videos)", FieldKind.Bool, Default: "false"),
                new("replies", "Who can reply", FieldKind.Choice, Default: "everyone",
                    Options: new[] { "Everyone|everyone", "Accounts you follow|following", "Only accounts you mention|mentionedUsers", "Verified accounts|verified", "Subscribers|subscribers" }, Api: "reply_settings"),
            },
            HashtagLimit: 3, HashtagSuggest: 2, HashtagWhere: "text", MaxSeconds: 140, MaxBytes: 512L << 20,
            Formats: "Up to 1920×1200, 2 min 20 s (Premium: longer)", Upload: "api",
            Auth: new("https://x.com/i/oauth2/authorize", "https://api.x.com/2/oauth2/token", "tweet.read tweet.write users.read media.write offline.access", BasicAuthToken: true),
            DevPortal: "https://developer.x.com/en/portal/dashboard",
            Setup: "X Developer Portal → Project & App → User authentication settings: OAuth 2.0, type \"Native App\" or \"Web App\", callback http://127.0.0.1:53682/callback, permissions Read and write. Paste the OAuth 2.0 Client ID (and secret for Web App) here. Posting needs a paid API tier.",
            Notes: "Media uploads use the v2 /2/media/upload endpoints."),

        new("linkedin", "LinkedIn", "#FF0A66C2", "",
            new PField[]
            {
                new("text", "Post text", FieldKind.Multiline, 3000, Help: "Up to 3,000 characters.", Api: "commentary"),
                new("title", "Video title", FieldKind.Text, 200, Api: "content.media.title"),
                new("visibility", "Who can see it", FieldKind.Choice, Default: "PUBLIC", Options: new[] { "Anyone|PUBLIC", "Connections only|CONNECTIONS" }, Api: "visibility"),
            },
            HashtagLimit: 5, HashtagSuggest: 3, HashtagWhere: "text", MaxSeconds: 900, MaxBytes: 5L << 30,
            Formats: "16:9 or square, 3 s – 15 min", Upload: "api",
            Auth: new("https://www.linkedin.com/oauth/v2/authorization", "https://www.linkedin.com/oauth/v2/accessToken", "openid profile w_member_social"),
            DevPortal: "https://www.linkedin.com/developers/apps",
            Setup: "LinkedIn Developers → Create app → Products: \"Share on LinkedIn\" and \"Sign In with LinkedIn using OpenID Connect\" → Auth: redirect URL http://127.0.0.1:53682/callback. Paste the Client ID and Client secret here.",
            Notes: "Posts to your personal profile (company pages need extra LinkedIn approval)."),

        new("threads", "Threads", "#FF101010", "",
            new PField[]
            {
                new("text", "Post", FieldKind.Multiline, 500, Help: "Up to 500 characters; Threads uses one topic tag per post.", Api: "text"),
                new("replies", "Who can reply", FieldKind.Choice, Default: "everyone",
                    Options: new[] { "Everyone|everyone", "Profiles you follow|accounts_you_follow", "Mentioned only|mentioned_only" }, Api: "reply_control"),
            },
            HashtagLimit: 1, HashtagSuggest: 1, HashtagWhere: "text", MaxSeconds: 300, MaxBytes: 1L << 30,
            Formats: "Up to 5 min", Upload: "intent", Auth: null,
            DevPortal: "https://developers.facebook.com/docs/threads",
            Setup: "Nothing to connect: CutPro copies the text, opens Threads in your browser and shows the video in Explorer to attach.",
            Notes: "Threads' API only takes videos from a public web address, so CutPro hands the post to the Threads site instead of uploading."),

        new("pinterest", "Pinterest", "#FFE60023", "",
            new PField[]
            {
                new("board", "Board ID", FieldKind.Text, Required: true, Help: "The board to pin to (filled in when you connect).", Api: "board_id"),
                new("title", "Title", FieldKind.Text, 100, Api: "title"),
                new("description", "Description", FieldKind.Multiline, 500, Api: "description"),
                new("link", "Destination link", FieldKind.Text, 2048, Help: "Where the pin leads, e.g. your church website or sermon page.", Api: "link"),
                new("alt", "Alt text", FieldKind.Text, 500, Help: "Describes the video for people using screen readers.", Api: "alt_text"),
                new("cover", "Cover frame (seconds)", FieldKind.Number, Default: "1", Api: "media_source.cover_image_key_frame_time"),
            },
            HashtagLimit: 5, HashtagSuggest: 3, HashtagWhere: "description", MaxSeconds: 900, MaxBytes: 2L << 30,
            Formats: "Vertical 2:3 or 9:16 work best, 4 s – 15 min", Upload: "api",
            Auth: new("https://www.pinterest.com/oauth/", "https://api.pinterest.com/v5/oauth/token", "boards:read,pins:read,pins:write", BasicAuthToken: true, ScopeSeparator: ","),
            DevPortal: "https://developers.pinterest.com/apps/",
            Setup: "Pinterest Developers → My apps → Connect app (needs a business account) → redirect URI http://127.0.0.1:53682/callback. Paste the App ID and App secret key here.",
            Notes: "Pinterest uses keywords in the title and description more than hashtags."),

        new("vimeo", "Vimeo", "#FF1AB7EA", "",
            new PField[]
            {
                new("title", "Title", FieldKind.Text, 128, Required: true, Api: "name"),
                new("description", "Description", FieldKind.Multiline, 5000, Api: "description"),
                new("tags", "Tags", FieldKind.Tags, 500, Help: "Comma separated.", Api: "tags"),
                new("privacy", "Who can watch", FieldKind.Choice, Default: "anybody",
                    Options: new[] { "Anyone|anybody", "Only me|nobody", "People with the private link|unlisted", "People with a password|password", "Hide from Vimeo (embed only)|disable" }, Api: "privacy.view"),
                new("password", "Password", FieldKind.Text, Help: "When \"People with a password\" is chosen.", Api: "password"),
                new("downloads", "Allow downloads", FieldKind.Bool, Default: "false", Api: "privacy.download"),
                new("embed", "Embedding", FieldKind.Choice, Default: "public", Options: new[] { "Anywhere|public", "Nowhere|private" }, Api: "privacy.embed"),
                new("license", "License", FieldKind.Choice, Default: "", Options: new[] { "All rights reserved|", "CC BY|by", "CC BY-SA|by-sa", "CC BY-NC|by-nc", "CC0 (public domain)|cc0" }, Api: "license"),
            },
            HashtagLimit: 10, HashtagSuggest: 0, HashtagWhere: "tags", MaxSeconds: 24 * 3600, MaxBytes: 250L << 30,
            Formats: "Any shape, up to 8K", Upload: "api",
            Auth: new("https://api.vimeo.com/oauth/authorize", "https://api.vimeo.com/oauth/access_token", "public private upload edit", BasicAuthToken: true),
            DevPortal: "https://developer.vimeo.com/apps",
            Setup: "Vimeo Developer → Create an app → callback URL http://127.0.0.1:53682/callback → request Upload access. Paste the Client identifier and Client secret here.",
            Notes: "Upload quota depends on your Vimeo plan."),
    };

    public static PlatformDef Get(string id) => All.First(p => p.Id == id);

    /// <summary>"Label|value" option → (label, value).</summary>
    public static (string label, string value) Opt(string o) { int i = o.IndexOf('|'); return i < 0 ? (o, o) : (o[..i], o[(i + 1)..]); }

    /// <summary>A field's length the way the platform counts it: X weights text (links = 23, emoji / CJK = 2), YouTube counts
    /// tags with quotes around multi-word tags and a comma between tags; everything else counts UTF-16 units.</summary>
    public static int Length(PlatformDef p, string fieldId, string val)
    {
        if (p.Id == "x" && fieldId == "text")
        {
            int n = 0;
            string rest = System.Text.RegularExpressions.Regex.Replace(val, @"https?://\S+", m => { n += 23; return ""; });
            for (int i = 0; i < rest.Length; i++)
            {
                int cp = rest[i];
                if (char.IsHighSurrogate(rest[i]) && i + 1 < rest.Length) { cp = char.ConvertToUtf32(rest[i], rest[i + 1]); i++; }
                n += cp <= 0x10FF || cp is >= 0x2000 and <= 0x200D or >= 0x2010 and <= 0x201F or >= 0x2032 and <= 0x2037 ? 1 : 2;
            }
            return n;
        }
        if (p.Id == "youtube" && fieldId == "tags")
        {
            var tags = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return tags.Sum(t => t.Length + (t.Contains(' ') ? 2 : 0)) + Math.Max(0, tags.Length - 1);
        }
        return val.Length;
    }

    /// <summary>Problems with a post before uploading (empty = fine). Counts like the platforms do (UTF-16 units, hashtags).</summary>
    public static List<string> Validate(PlatformDef p, IReadOnlyDictionary<string, string> v, double seconds, long bytes, double aspect)
    {
        var errs = new List<string>();
        foreach (var f in p.Fields)
        {
            var val = v.GetValueOrDefault(f.Id, f.Default) ?? "";
            if (f.Required && string.IsNullOrWhiteSpace(val)) errs.Add($"{f.Label} is required.");
            if (f.Max > 0 && f.Kind is FieldKind.Text or FieldKind.Multiline or FieldKind.Tags)
            {
                int len = Length(p, f.Id, val);
                if (p.Id == "x" && f.Id == "text" && v.GetValueOrDefault("premium") == "true") continue;
                if (len > f.Max) errs.Add($"{f.Label}: {len} / {f.Max} characters{(p.Id == "x" ? " (links count 23, emoji 2)" : "")}.");
            }
            if (p.Id == "youtube" && f.Id is "title" or "description" && (val.Contains('<') || val.Contains('>')))
                errs.Add($"YouTube doesn't allow < or > in the {f.Label.ToLowerInvariant()}.");
        }
        int tags = Hashtags.Count(string.Join(" ", p.Fields.Where(f => f.Kind == FieldKind.Multiline || f.Id is "title" or "text").Select(f => v.GetValueOrDefault(f.Id, ""))));
        if (p.Id == "instagram" && tags > 5) errs.Add($"Instagram allows at most 5 hashtags ({tags} now).");
        if (p.Id == "youtube" && tags > 15) errs.Add($"YouTube ignores every hashtag when there are more than 15 ({tags} now).");
        if (p.Id == "instagram" && v.GetValueOrDefault("collaborators", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Length > 3) errs.Add("Instagram allows up to 3 collaborators.");
        double max = p.Id == "facebook" && v.GetValueOrDefault("type", "reel") == "reel" ? 90 : p.Id == "x" && v.GetValueOrDefault("premium") == "true" ? 4 * 3600 : p.MaxSeconds;
        if (seconds > max + 0.5) errs.Add($"The video is {TimeUtil.Format(seconds, false)}; {p.Name} {(p.Id == "facebook" ? "Reels" : "")} allows up to {TimeUtil.Format(max, false)}.");
        if (bytes > p.MaxBytes) errs.Add($"The file is {bytes / 1048576.0:0} MB; {p.Name} allows up to {p.MaxBytes / 1048576.0:0} MB.");
        if (p.Id == "youtube" && v.GetValueOrDefault("publishAt") is { Length: > 0 } && v.GetValueOrDefault("privacy", "private") != "private")
            errs.Add("Scheduled YouTube videos must be Private until they publish.");
        if (p.Id == "tiktok" && v.GetValueOrDefault("brandContent") == "true" && v.GetValueOrDefault("privacy") == "SELF_ONLY")
            errs.Add("TikTok: branded content can't be private (\"Only me\").");
        return errs;
    }
}
