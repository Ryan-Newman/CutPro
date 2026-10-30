namespace CutPro;

public enum LibKind { Transition, Graphic, Sound, Look, Preset }

/// <summary>One card in the Library (the Effects panel): a named cinematic transition (often with a sound effect), a graphic
/// template, a look, or a whoosh to drop at the playhead. Many cards share an engine (<see cref="TransKind"/>) the way a
/// Sonduck pack has hundreds of named presets over a smaller set of transitions.</summary>
public sealed class LibItem
{
    public string Id = "", Name = "", Category = "", Help = "";
    public LibKind Kind;
    public string? TransKind;
    public double Dur = 1;
    public SfxKind Sfx;
    public string? Graphic;
    public LookFx? Look;
    public string? Preset;
    public bool Matches(string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return true;
        q = q.Trim();
        return Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || Category.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || (Help?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || (TransKind?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }
}

/// <summary>The built-in Library: cinematic transitions with sound, graphics, looks and SFX. Stock / templates browser of ROADMAP.</summary>
public static class Library
{
    public static readonly LibItem[] All = Build();
    public static IEnumerable<string> Categories => new[] { "All" }.Concat(All.Select(i => i.Category).Distinct());
    public static LibItem? Of(string id) => All.FirstOrDefault(i => i.Id == id);

    static LibItem[] Build()
    {
        var list = new List<LibItem>();
        int n = 0;
        string Id(string prefix) => prefix + "-" + (++n);

        LibItem Tr(string name, string cat, string kind, double dur, SfxKind sfx, string help) =>
            new() { Id = Id("t"), Name = name, Category = cat, Kind = LibKind.Transition, TransKind = kind, Dur = dur, Sfx = sfx, Help = help };

        foreach (var d in Transitions.VideoDefs)
            list.Add(Tr(d.Name, "Classic", d.Name, 1, SfxFor(d), d.Help + (SfxFor(d) == SfxKind.None ? "" : " — includes a sound effect")));

        foreach (var d in Transitions.AudioDefs)
            list.Add(Tr(d.Name, "Audio", d.Name, 1, SfxKind.None, d.Help));

        (string dir, string kind)[] whips = { ("Left", "Push Left"), ("Right", "Push Right"), ("Up", "Push Up"), ("Down", "Push Down") };
        (string tag, double dur)[] speeds = { ("Quick", 0.22), ("", 0.38), ("Long", 0.65) };
        foreach (var (dir, kind) in whips)
            foreach (var (tag, dur) in speeds)
                list.Add(Tr($"Whip Pan {dir}" + (tag.Length == 0 ? "" : " · " + tag), "Whip pans", kind, dur, SfxKind.Whoosh,
                    $"A fast push {dir.ToLowerInvariant()} with a whoosh ({dur:0.##} s)"));

        foreach (var (dir, kind) in new[] { ("Left", "Slide Left"), ("Right", "Slide Right"), ("Up", "Slide Up"), ("Down", "Slide Down") })
            foreach (var (tag, dur) in speeds)
                list.Add(Tr($"Whoosh {dir}" + (tag.Length == 0 ? "" : " · " + tag), "Whoosh", kind, dur, SfxKind.WhooshAir,
                    $"The next shot slides in from the {dir.ToLowerInvariant()} with an airy whoosh"));

        list.Add(Tr("Flash White", "Flash", "Dip to White", 0.28, SfxKind.Flash, "A camera flash through white"));
        list.Add(Tr("Flash White · Long", "Flash", "Dip to White", 0.55, SfxKind.Flash, "A slower white flash"));
        list.Add(Tr("Flash Black", "Flash", "Dip to Black", 0.28, SfxKind.Hit, "A hard cut through black"));
        list.Add(Tr("Flash Black · Long", "Flash", "Dip to Black", 0.7, SfxKind.Boom, "A dramatic dip to black"));
        list.Add(Tr("Camera Click", "Flash", "Dip to White", 0.18, SfxKind.Click, "A still-camera click"));
        list.Add(Tr("Film Burn", "Film", "Film Burn", 0.7, SfxKind.Scratch, "A warm film-burn dip"));
        list.Add(Tr("Film Burn · Quick", "Film", "Film Burn", 0.35, SfxKind.Scratch, "A short film burn"));
        list.Add(Tr("Light Leak Warm", "Light leaks", "Light Leak", 0.8, SfxKind.WhooshAir, "A warm light-leak dip"));
        list.Add(Tr("Light Leak Gold", "Light leaks", "Light Leak", 1.1, SfxKind.WhooshAir, "A longer gold leak"));
        list.Add(Tr("Film Dissolve 12f", "Film", "Film Dissolve", 0.4, SfxKind.Scratch, "A 12-frame dithered dissolve"));
        list.Add(Tr("Film Dissolve 24f", "Film", "Film Dissolve", 0.8, SfxKind.Tape, "A 24-frame dithered dissolve"));

        list.Add(Tr("Zoom Punch", "Zoom", "Zoom Punch", 0.45, SfxKind.Punch, "A smash zoom through the cut"));
        list.Add(Tr("Zoom Punch · Slow", "Zoom", "Zoom Punch", 0.8, SfxKind.Whoosh, "A longer smash zoom"));
        list.Add(Tr("Zoom In · Hit", "Zoom", "Zoom In", 0.5, SfxKind.Hit, "The next shot grows in with a hit"));
        list.Add(Tr("Zoom Out · Whoosh", "Zoom", "Zoom Out", 0.5, SfxKind.Whoosh, "The current shot shrinks away"));
        list.Add(Tr("Cross Zoom · Cinematic", "Zoom", "Cross Zoom", 0.7, SfxKind.Whoosh, "Zoom-match through the cut"));
        list.Add(Tr("Shake Cut", "Camera", "Shake Cut", 0.35, SfxKind.Hit, "The outgoing shot shakes, then the next hits"));
        list.Add(Tr("Shake Cut · Heavy", "Camera", "Shake Cut", 0.55, SfxKind.Boom, "A heavier camera shake into the next shot"));
        list.Add(Tr("Spin Whip", "Spin", "Spin In", 0.4, SfxKind.Whoosh, "The next shot spins in with a whoosh"));
        list.Add(Tr("Spin Away · Hit", "Spin", "Spin Away", 0.45, SfxKind.Punch, "The current shot spins off"));

        list.Add(Tr("Glitch Slice", "Glitch", "Block Glitch", 0.4, SfxKind.Glitch, "Blocks of the next shot slam in"));
        list.Add(Tr("Glitch Slice · Quick", "Glitch", "Block Glitch", 0.22, SfxKind.Glitch, "A short digital glitch"));
        list.Add(Tr("RGB Smash", "Glitch", "Block Glitch", 0.5, SfxKind.Scratch, "A longer block glitch"));
        list.Add(Tr("TV Static", "Glitch", "Film Dissolve", 0.3, SfxKind.Glitch, "A noisy dissolve"));

        list.Add(Tr("Ink Blot", "Paint", "Iris Round", 0.7, SfxKind.Paper, "A soft circle opens like ink"));
        list.Add(Tr("Heart Iris", "Paint", "Heart Iris", 0.8, SfxKind.WhooshAir, "A heart opens onto the next shot"));
        list.Add(Tr("Star Iris", "Paint", "Star Iris", 0.7, SfxKind.Flash, "A star opens onto the next shot"));
        list.Add(Tr("Paint Stroke", "Paint", "Paint Stroke", 0.55, SfxKind.Paper, "A diagonal brush reveals the next shot"));
        list.Add(Tr("Marker Wipe", "Trendy", "Wipe Left", 0.5, SfxKind.Scratch, "A marker-style wipe"));
        list.Add(Tr("Tape Rip Left", "Trendy", "Wipe Left", 0.4, SfxKind.Tape, "A tape-rip wipe"));
        list.Add(Tr("Tape Rip Right", "Trendy", "Wipe Right", 0.4, SfxKind.Tape, "A tape-rip wipe the other way"));
        list.Add(Tr("Paper Peel", "Trendy", "Page Peel", 0.9, SfxKind.Paper, "A page peel with a paper rustle"));
        list.Add(Tr("Collage Inset", "Trendy", "Inset", 0.6, SfxKind.Click, "The next shot grows from the corner"));

        list.Add(Tr("Clock Wipe · Hit", "Wipe", "Clock Wipe", 1, SfxKind.Hit, "A clock wipe with a hit at the cut"));
        list.Add(Tr("Barn Doors · Whoosh", "Wipe", "Barn Doors", 0.7, SfxKind.Whoosh, "Doors open with a whoosh"));
        list.Add(Tr("Venetian · Soft", "Wipe", "Venetian Blinds", 0.8, SfxKind.WhooshAir, "Slats open softly"));
        list.Add(Tr("Cube Spin · Cinematic", "3D", "Cube Spin", 0.9, SfxKind.Whoosh, "A cube turn with a whoosh"));
        list.Add(Tr("Morph Cut · Soft", "Dissolve", "Morph Cut", 1.2, SfxKind.WhooshAir, "A zoom-match dissolve"));
        list.Add(Tr("Bible Page · Soft", "Gospel", "Bible Page Turn", 1.1, SfxKind.Paper, "Scripture page turn with paper"));

        // Sonduck-style pack: every engine at four edit lengths, each with a sound (preview = export — the WAV is a real clip).
        foreach (var d in Transitions.VideoDefs)
            foreach (var (tag, dur) in new[] { ("Snap", 0.16), ("Beat", 0.33), ("Trailer", 0.6), ("Epic", 1.05) })
            {
                var sfx = SfxFor(d);
                if (sfx == SfxKind.None) sfx = d.Fx is Transitions.Fx.Dip ? SfxKind.Flash : SfxKind.Whoosh;
                list.Add(Tr($"{d.Name} · {tag}", "Cinematic · " + Transitions.GroupOf(d), d.Name, dur, sfx,
                    $"{d.Help} — {tag.ToLowerInvariant()} ({dur:0.##} s), with a built-in sound effect"));
            }

        foreach (var (name, kind) in new[] { ("Whoosh", SfxKind.Whoosh), ("Air whoosh", SfxKind.WhooshAir), ("Hit", SfxKind.Hit),
                 ("Punch", SfxKind.Punch), ("Flash", SfxKind.Flash), ("Riser", SfxKind.Riser), ("Glitch", SfxKind.Glitch),
                 ("Boom", SfxKind.Boom), ("Tape rip", SfxKind.Tape), ("Paper", SfxKind.Paper), ("Scratch", SfxKind.Scratch), ("Click", SfxKind.Click) })
            foreach (var dur in new[] { 0.15, 0.25, 0.5, 1.0, 1.5 })
                list.Add(new() { Id = Id("s"), Name = $"{name} {dur:0.##}s", Category = "Sound effects", Kind = LibKind.Sound, Sfx = kind, Dur = dur,
                                 Help = $"Drop a {name.ToLowerInvariant()} at the playhead ({dur:0.##} s)" });

        foreach (var g in GraphicLibrary.BuiltIn)
            list.Add(new() { Id = Id("g"), Name = g.Name, Category = "Graphics · " + g.Category, Kind = LibKind.Graphic, Graphic = g.Name,
                             Help = "Adds the graphic at the playhead (Titles view also has it)" });

        list.Add(Look("Film grain", 0, 0, 45, 0, 0, "Grain over the selected clips"));
        list.Add(Look("Heavy grain", 0, 0, 80, 0, 0, "Heavy film grain"));
        list.Add(Look("Glow", 0, 55, 0, 0, 0, "A soft halo on the highlights"));
        list.Add(Look("Sharpen", 55, 0, 0, 0, 0, "Unsharp mask"));
        list.Add(Look("VHS", 0, 0, 12, 70, 0, "Chroma smear and scanlines"));
        list.Add(Look("Glitch look", 0, 0, 0, 0, 65, "RGB split and a sliding slice"));
        list.Add(Look("Cinematic punch", 35, 30, 18, 0, 0, "Sharpen, glow and a little grain"));
        list.Add(Look("Soft glow", 0, 80, 0, 0, 0, "A strong halo on the highlights"));
        list.Add(Look("Gritty film", 25, 0, 70, 18, 0, "Grain and a hint of VHS"));
        list.Add(Look("RGB split", 0, 0, 0, 0, 80, "A heavy glitch look"));

        LibItem Look(string name, double sh, double gl, double gr, double vhs, double gli, string help) =>
            new() { Id = Id("l"), Name = name, Category = "Looks", Kind = LibKind.Look, Help = help,
                    Look = new LookFx { Sharpen = sh, Glow = gl, Grain = gr, Vhs = vhs, Glitch = gli } };

        return list.ToArray();
    }

    static SfxKind SfxFor(Transitions.Def d) => d.Name switch
    {
        "Push Left" or "Push Right" or "Push Up" or "Push Down" or "Slide Left" or "Slide Right" or "Slide Up" or "Slide Down" => SfxKind.Whoosh,
        "Cross Zoom" or "Zoom In" or "Zoom Out" or "Zoom Punch" => SfxKind.Punch,
        "Dip to White" => SfxKind.Flash,
        "Dip to Black" => SfxKind.Hit,
        "Spin In" or "Spin Away" or "Cube Spin" => SfxKind.Whoosh,
        "Page Peel" or "Bible Page Turn" => SfxKind.Paper,
        "Film Dissolve" or "Film Burn" => SfxKind.Scratch,
        "Light Leak" => SfxKind.WhooshAir,
        "Block Glitch" => SfxKind.Glitch,
        "Shake Cut" => SfxKind.Hit,
        _ => SfxKind.None
    };

    /// <summary>Places the sound on a free audio track covering [start, start+dur]. Call after the picture transition is on.</summary>
    public static Clip? PlaceSfx(Editor ed, SfxKind kind, double start, double dur)
    {
        if (kind == SfxKind.None || dur < 0.05) return null;
        var m = Sfx.Media(kind, dur);
        if (!ed.Bins.Contains("Library")) ed.Bins.Add("Library");
        if (ed.Media.FirstOrDefault(x => x.Path == m.Path) is { } have) m = have;
        else ed.Media.Add(m);
        int row = Tracks.AudioRows.LastOrDefault(r => !ed.Locked(r) && !ed.Clips.Any(c => c.Track == r && c.Start < start + dur - 1e-3 && c.End > start + 1e-3));
        if (row < Tracks.A1) row = Tracks.AudioRows.FirstOrDefault(r => !ed.Locked(r));
        if (row < Tracks.A1) return null;
        var made = ed.DropMedia(m, row, start, false, true, false);
        return made.FirstOrDefault();
    }

    /// <summary>Where a just-placed transition sits, for lining the sound up.</summary>
    public static (double t0, double dur)? WindowOf(Editor ed, Clip owner, bool head)
    {
        var plan = Transitions.Compute(ed);
        var w = plan.Wins.FirstOrDefault(x => x.Owner.Id == owner.Id && x.Head == head);
        return w == null ? null : (w.T0, w.Dur);
    }
}
