using System.Text;
using System.Text.RegularExpressions;

namespace CutPro;

/// <summary>
/// Writes a suggested title, description / caption, keyword tags and hashtags for each platform from <see cref="ContentInfo"/>.
/// No cloud AI: the summary is extractive (the most representative sentences of what was said, by keyword weight), Bible
/// references are quoted, scene tags and the faith vocabulary become hashtags, and each platform gets its own length,
/// tone and hashtag count. Everything is a suggestion the user edits in the publish window.
/// </summary>
public static class Captions
{
    static readonly HashSet<string> Stop = new(("a about above after again against all also am an and any are aren't as at be because been before being below between both but by can " +
        "could did didn't do does doesn't doing don't down during each even ever every few for from further get gets getting go going gonna got had has have having he he'd " +
        "he'll he's her here here's hers herself him himself his how how's i i'd i'll i'm i've if in into is isn't it it's its itself just know let let's like little lot make " +
        "many may me might more most much must my myself need never no nor not now of off oh okay on once one only or other ought our ours ourselves out over own really " +
        "right said same say says see she she'd she'll she's should so some something still such take than that that's the their theirs them themselves then there there's " +
        "these they they'd they'll they're they've thing things think this those though through to today too under until up us very want wanna was wasn't way we we'd we'll " +
        "we're we've well went were weren't what what's when when's where where's which while who who's whom why why's will with won't would wouldn't yeah yes you you'd " +
        "you'll you're you've your yours yourself yourselves going come comes came back good great new year years day days time times people person man men woman women " +
        "thank thanks amen um uh kind sort mean actually maybe gonna gotta").Split(' '));

    /// <summary>Faith vocabulary: words in the speech or titles → hashtags.</summary>
    static readonly (string word, string tag)[] Faith =
    {
        ("jesus", "jesus"), ("christ", "jesuschrist"), ("god", "god"), ("lord", "lord"), ("holy spirit", "holyspirit"), ("spirit", "holyspirit"),
        ("grace", "grace"), ("faith", "faith"), ("hope", "hope"), ("love", "godslove"), ("prayer", "prayer"), ("pray", "prayer"), ("praying", "prayer"),
        ("worship", "worship"), ("praise", "praise"), ("gospel", "gospel"), ("bible", "bible"), ("scripture", "scripture"), ("church", "church"),
        ("salvation", "salvation"), ("saved", "salvation"), ("forgive", "forgiveness"), ("forgiveness", "forgiveness"), ("mercy", "mercy"),
        ("peace", "peace"), ("joy", "joy"), ("easter", "easter"), ("resurrection", "resurrection"), ("risen", "heisrisen"), ("christmas", "christmas"),
        ("testimony", "testimony"), ("baptism", "baptism"), ("baptized", "baptism"), ("heaven", "heaven"), ("kingdom", "kingdomofgod"),
        ("disciple", "discipleship"), ("ministry", "ministry"), ("mission", "missions"), ("sermon", "sermon"), ("pastor", "pastor"),
        ("revival", "revival"), ("blessed", "blessed"), ("blessing", "blessed"), ("gratitude", "gratitude"), ("thankful", "thankful"),
        ("strength", "strength"), ("healing", "healing"), ("anxiety", "anxiety"), ("marriage", "marriage"), ("family", "family"), ("youth", "youthministry"),
    };
    static readonly string[] BaseTags = { "faith", "jesus", "christian", "god", "church" };

    public sealed record Suggestion(string Title, Dictionary<string, string> Fields, List<string> Hashtags, string Summary);

    /// <summary>Weighted keywords of the speech + titles (lower case), most important first.</summary>
    public static List<(string word, double weight)> Keywords(ContentInfo info)
    {
        var text = (info.TranscriptText + " " + string.Join(" ", info.Titles.Select(t => t.text + " " + t.text))).ToLowerInvariant();
        var counts = new Dictionary<string, int>();
        foreach (Match m in Regex.Matches(text, @"[\p{L}][\p{L}']{2,}"))
        {
            var w = m.Value.Trim('\'');
            if (w.Length < 4 || Stop.Contains(w) || w.EndsWith("'s")) continue;
            counts[w] = counts.GetValueOrDefault(w) + 1;
        }
        return counts.Select(kv => (kv.Key, kv.Value * Math.Log(1 + kv.Key.Length) * (Faith.Any(f => f.word == kv.Key) ? 1.6 : 1)))
                     .OrderByDescending(x => x.Item2).Take(25).ToList();
    }

    /// <summary>The 1–3 sentences that best represent what was said (in their original order).</summary>
    public static List<string> KeySentences(ContentInfo info, int count)
    {
        var kw = Keywords(info).ToDictionary(k => k.word, k => k.weight);
        var sentences = Regex.Split(info.TranscriptText, @"(?<=[.!?])\s+").Select(s => s.Trim()).Where(s => s.Length is >= 30 and <= 240).ToList();
        if (sentences.Count == 0) return new();
        var scored = sentences.Select((s, i) =>
        {
            var words = Regex.Matches(s.ToLowerInvariant(), @"[\p{L}']+").Select(m => m.Value).ToList();
            double sc = words.Sum(w => kw.GetValueOrDefault(w)) / Math.Sqrt(Math.Max(4, words.Count));
            if (Bible.Find(s).Count > 0) sc *= 1.3;
            if (i < sentences.Count / 5) sc *= 1.1;                                     // openings tend to state the topic
            if (Regex.IsMatch(s, @"\b(subscribe|like and|turn to|good morning|welcome back|let's pray)\b", RegexOptions.IgnoreCase)) sc *= 0.3;
            return (s, i, sc);
        }).OrderByDescending(x => x.sc).Take(count).OrderBy(x => x.i).Select(x => x.s).ToList();
        return scored;
    }

    /// <summary>Ranked hashtag candidates (without #).</summary>
    public static List<string> HashtagPool(ContentInfo info, PlatformDef p)
    {
        var score = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        void Add(string tag, double s) { tag = Hashtags.Clean(tag).ToLowerInvariant(); if (tag.Length >= 3 && tag.Length <= 30) score[tag] = score.GetValueOrDefault(tag) + s; }
        bool isShort = info.Vertical && info.Duration <= 180;
        if (p.Id == "youtube" && isShort) Add("shorts", 100);
        foreach (var r in info.Scripture.Take(2)) Add(Bible.Tag(r), 6);
        foreach (var r in info.Scripture.Take(2)) Add(Regex.Replace(r, @"\s*\d.*$", ""), 3);         // the book: #Romans
        foreach (var (phrase, tags, sc) in info.Scenes) foreach (var t in tags) Add(t, 4 + sc * 20);
        var text = (info.TranscriptText + " " + string.Join(" ", info.Titles.Select(t => t.text))).ToLowerInvariant();
        foreach (var (word, tag) in Faith)
        {
            int n = Regex.Matches(text, $@"\b{Regex.Escape(word)}\b").Count;
            if (n > 0) Add(tag, 2 + Math.Min(6, n));
        }
        foreach (var (w, wt) in Keywords(info).Take(8)) Add(w, Math.Min(3, wt / 3));
        foreach (var n in info.MaskNames.Concat(info.ClipNames).Take(4)) Add(n.Replace(" ", ""), 1.5);
        foreach (var (t, i) in BaseTags.Select((t, i) => (t, i))) Add(t, 2 - i * 0.2);
        if (p.Id == "tiktok") Add("christiantiktok", 5);
        if (p.Id == "instagram" && info.Vertical) Add("christianreels", 3);
        if (p.Id == "facebook") Add("faithfamily", 1);
        return score.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }

    public static Suggestion Suggest(ContentInfo info, PlatformDef p)
    {
        var f = new Dictionary<string, string>();
        var sentences = KeySentences(info, 3);
        var pool = HashtagPool(info, p);
        int n = p.HashtagSuggest;
        var tags = pool.Take(n).ToList();
        string hash = string.Join(" ", tags.Select(t => "#" + t));
        string scripture = info.Scripture.Count > 0 ? string.Join("; ", info.Scripture.Take(3)) : "";
        string title = Title(info, sentences);
        string hook = sentences.FirstOrDefault() ?? Hook(info);
        string summary = sentences.Count > 0 ? string.Join(" ", sentences) : Hook(info);

        switch (p.Id)
        {
            case "youtube":
            {
                var d = new StringBuilder();
                d.AppendLine(summary).AppendLine();
                if (scripture.Length > 0) d.AppendLine("📖 Scripture: " + scripture).AppendLine();
                if (info.Chapters.Count >= 3)
                {
                    d.AppendLine("Chapters:");
                    foreach (var (t, l) in info.Chapters) d.AppendLine($"{Stamp(t)} {l}");
                    d.AppendLine();
                }
                d.AppendLine(info.Vertical && info.Duration <= 180 ? "Follow for more encouragement." : "Subscribe for more messages of hope, and share this with someone who needs it today.");
                d.AppendLine().Append(hash);
                f["title"] = Analyzer.Shorten(NoAngle(title), 100);                        // YouTube rejects < and >
                f["description"] = Fit(NoAngle(d.ToString()), 5000);
                f["tags"] = TagList(info, 500);
                break;
            }
            case "tiktok":
                f["caption"] = Fit($"{Analyzer.Shorten(hook, 150)}{(scripture.Length > 0 ? $" ({info.Scripture[0]})" : "")}\n\n{hash}", 2200);
                break;
            case "instagram":
                f["caption"] = Fit($"{Analyzer.Shorten(summary, 600)}{(scripture.Length > 0 ? $"\n\n📖 {scripture}" : "")}\n.\n{hash}", 2200);
                break;
            case "facebook":
                f["title"] = Analyzer.Shorten(title, 255);
                f["description"] = Fit($"{Analyzer.Shorten(summary, 900)}{(scripture.Length > 0 ? $"\n\n📖 {scripture}" : "")}\n\n{hash}", 5000);
                f["type"] = info.Vertical && info.Duration <= 90 ? "reel" : "video";
                break;
            case "x":
            {
                string tail = (scripture.Length > 0 ? $" {info.Scripture[0]}" : "") + (hash.Length > 0 ? " " + hash : "");
                int room = Math.Max(40, 280 - Platforms.Length(p, "text", tail) - 1);
                do f["text"] = Analyzer.Shorten(hook, room) + tail;             // X weighs emoji / CJK double
                while (Platforms.Length(p, "text", f["text"]) > 280 && (room -= 10) > 20);
                break;
            }
            case "linkedin":
                f["text"] = Fit($"{Analyzer.Shorten(summary, 1200)}{(scripture.Length > 0 ? $"\n\n{scripture}" : "")}\n\n{hash}", 3000);
                f["title"] = Analyzer.Shorten(title, 200);
                break;
            case "threads":
                f["text"] = Analyzer.Shorten($"{hook}{(scripture.Length > 0 ? $" ({info.Scripture[0]})" : "")} {hash}".Trim(), 500);
                break;
            case "pinterest":
                f["title"] = Analyzer.Shorten(title, 100);
                f["description"] = Fit($"{Analyzer.Shorten(summary, 420)} {hash}".Trim(), 500);
                f["alt"] = Analyzer.Shorten(AltText(info), 500);
                break;
            case "vimeo":
                f["title"] = Analyzer.Shorten(title, 128);
                f["description"] = Fit(summary + (scripture.Length > 0 ? "\n\nScripture: " + scripture : ""), 5000);
                f["tags"] = TagList(info, 500);
                break;
        }
        foreach (var k in f.Keys.ToList()) f[k] = f[k].Trim();          // nothing to say → no leading blank lines / spaces
        return new Suggestion(title, f, tags, summary);
    }

    static string Title(ContentInfo info, List<string> sentences)
    {
        // a title card on the timeline is the strongest hint; else the clearest spoken line; else scenes / project name
        var card = info.Titles.Where(t => t.text.Length is >= 4 and <= 90).OrderBy(t => t.t).Select(t => t.text.Split('\n')[0].Trim()).FirstOrDefault();
        string baseTitle = card ?? (sentences.Count > 0 ? Analyzer.Shorten(sentences[0].TrimEnd('.', '!', '?'), 70)
                         : info.Scenes.Count > 0 ? Cap(string.Join(" & ", info.Scenes.Select(s => s.phrase).Distinct().Take(2)))
                         : NameOf(info));
        if (info.Scripture.Count > 0 && !baseTitle.Contains(info.Scripture[0]) && baseTitle.Length + info.Scripture[0].Length < 95) baseTitle += (baseTitle.Length > 0 ? " | " : "") + info.Scripture[0];
        return baseTitle;
    }

    static string Hook(ContentInfo info)
    {
        var phrases = info.Scenes.Select(s => s.phrase).Distinct().Take(3).ToList();
        string what = phrases.Count switch { 0 => "", 1 => phrases[0], 2 => $"{phrases[0]} and {phrases[1]}", _ => $"{phrases[0]}, {phrases[1]} and {phrases[2]}" };
        var card = info.Titles.FirstOrDefault().text;
        if (card is { Length: > 0 } && what.Length > 0) return $"{card.Split('\n')[0]} — {what}.";
        if (card is { Length: > 0 }) return card.Split('\n')[0] + ".";
        return what.Length > 0 ? $"A {(info.Vertical ? "short" : "video")} with {what}." : NameOf(info) is { Length: > 0 } n ? n + "." : "";
    }

    /// <summary>The project name as a title — not when it's a placeholder ("Untitled", "Sequence 01", "New project"): an empty
    /// title gets the user's attention (it's required); a fake one might get published.</summary>
    static string NameOf(ContentInfo info)
    {
        var n = Regex.Replace(info.Name, @"[_\-]+", " ").Trim();
        return Regex.IsMatch(n, @"^(untitled|new project|sequence|project|video|export|test)\s*\d*$", RegexOptions.IgnoreCase) ? "" : Cap(n);
    }

    static string AltText(ContentInfo info)
    {
        var phrases = info.Scenes.Select(s => s.phrase).Distinct().Take(3).ToList();
        string what = phrases.Count > 0 ? string.Join(", ", phrases) : "the video";
        return $"Video showing {what}" + (info.Titles.Count > 0 ? $", with the title \"{info.Titles[0].text.Split('\n')[0]}\"" : "") + ".";
    }

    static string TagList(ContentInfo info, int max)
    {
        var words = new List<string>();
        words.AddRange(info.Titles.Select(t => t.text.Split('\n')[0].Trim()).Where(t => t.Length <= 40));
        words.AddRange(info.Scripture.Take(3));
        words.AddRange(info.Scenes.SelectMany(s => s.tags).Take(6));
        words.AddRange(Keywords(info).Take(10).Select(k => k.word));
        words.AddRange(new[] { "christian", "faith", "church" });
        var list = new List<string>(); int len = 0;
        foreach (var w in words.Select(w => w.Replace(",", "").Trim()).Where(w => w.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int cost = w.Length + (w.Contains(' ') ? 2 : 0) + (list.Count > 0 ? 1 : 0);   // YouTube: quotes around multi-word tags, commas between
            if (len + cost > max) break;
            list.Add(w); len += cost;
        }
        return string.Join(", ", list);
    }

    static string Stamp(double t) => t >= 3600 ? $"{(int)(t / 3600)}:{(int)(t / 60) % 60:00}:{(int)t % 60:00}" : $"{(int)(t / 60)}:{(int)t % 60:00}";
    static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
    static string Fit(string s, int max) => s.Length <= max ? s.Trim() : s[..max].Trim();
    static string NoAngle(string s) => s.Replace("<", "").Replace(">", "");
}
