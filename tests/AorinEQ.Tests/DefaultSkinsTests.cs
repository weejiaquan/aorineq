using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The skins AorinEQ ships with.
///
/// They are drawn at run time rather than installed, so the thing worth testing is not what they
/// look like but that each one LOADS - a shipped skin that SkinLoader rejects would be the app
/// shipping its own broken example, and it is the example skin authors will copy.
///
/// Since each style now has its OWN layout rather than one shape recoloured, the geometry checks
/// are per style: they compare what the loader read out of the shipped skin.json against the
/// <see cref="SkinArt.Layout"/> the artwork was drawn from, which is the only thing that keeps the
/// two from drifting.</summary>
public class DefaultSkinsTests : IDisposable
{
    private readonly string _root;
    private readonly ITestOutputHelper _out;

    /// <summary>Every shipped style, so a style added to <see cref="SkinArt.All"/> without a row
    /// here fails <see cref="Every_shipped_style_is_covered_by_these_tests"/> rather than going
    /// quietly untested.</summary>
    public static TheoryData<string> Styles()
    {
        var data = new TheoryData<string>();
        foreach (var style in SkinArt.All) data.Add(style);
        return data;
    }

    public DefaultSkinsTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "aorineq-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Every_shipped_skin_is_created()
    {
        var created = DefaultSkins.EnsureInstalled(_root);

        _out.WriteLine("created: " + string.Join(", ", created));
        Assert.Equal(SkinArt.All.OrderBy(s => s), created.OrderBy(s => s));
    }

    /// <summary>The house style ships, and it is the one an unset SkinName resolves to. Without
    /// both halves, picking "custom skin" before choosing a skin shows the built-in dark pill -
    /// the one look that says nothing was applied.</summary>
    [Fact]
    public void The_default_skin_is_shipped_and_is_what_an_unset_name_resolves_to()
    {
        DefaultSkins.EnsureInstalled(_root);

        _out.WriteLine($"SkinArt.Default = {SkinArt.Default}");
        _out.WriteLine($"Resolve(null) = {SkinArt.Resolve(null)}");
        _out.WriteLine($"Resolve(\"\") = {SkinArt.Resolve("")}");
        _out.WriteLine($"Resolve(\"  \") = {SkinArt.Resolve("  ")}");
        _out.WriteLine($"Resolve(\"seia\") = {SkinArt.Resolve("seia")}");

        Assert.Contains(SkinArt.Default, SkinArt.All);
        Assert.True(Directory.Exists(Path.Combine(_root, SkinArt.Default)));
        Assert.Equal(SkinArt.Default, SkinArt.Resolve(null));
        Assert.Equal(SkinArt.Default, SkinArt.Resolve(""));
        Assert.Equal(SkinArt.Default, SkinArt.Resolve("   "));

        // A name the user DID pick is never second-guessed, including one that does not exist -
        // that has to reach SkinLoader and be reported, not be silently swapped for the default.
        Assert.Equal("seia", SkinArt.Resolve("seia"));
        Assert.Equal("no-such-skin", SkinArt.Resolve("no-such-skin"));
    }

    /// <summary>The point of the whole exercise: every shipped skin has to survive the same loader
    /// a user's own skin goes through, including its AirPlay bar.</summary>
    [Theory]
    [MemberData(nameof(Styles))]
    public void Each_shipped_skin_loads_cleanly_and_has_an_airplay_bar(string style)
    {
        DefaultSkins.EnsureInstalled(_root);
        var skin = SkinLoader.Load(Path.Combine(_root, style));
        var l = SkinArt.LayoutFor(style);

        _out.WriteLine($"{style}: valid={skin.IsValid} error={skin.Error ?? "(none)"}");
        _out.WriteLine($"  volume  {skin.Width}x{skin.Height} (drawn {l.Width}x{l.VolumeHeight})");
        _out.WriteLine($"  airplay {skin.AirPlay?.Width}x{skin.AirPlay?.Height} " +
                       $"(drawn {l.Width}x{l.AirPlayHeight}) error={skin.AirPlayError ?? "(none)"}");
        _out.WriteLine($"  muted   {skin.MutedPath ?? "(none)"}");

        Assert.True(skin.IsValid, skin.Error);
        Assert.Null(skin.AirPlayError);
        Assert.NotNull(skin.AirPlay);
        Assert.Equal(l.Width, skin.Width);
        Assert.Equal(l.VolumeHeight, skin.Height);
        Assert.Equal(l.Width, skin.AirPlay!.Width);
        Assert.Equal(l.AirPlayHeight, skin.AirPlay.Height);
    }

    /// <summary>Every shipped skin draws its own muted state rather than falling back to the
    /// generic dimming. Each of them has a real way to say it - a struck-through speaker, or XP's
    /// Mute checkbox ticked - and a skin that ships one is the example authors should copy.</summary>
    [Theory]
    [MemberData(nameof(Styles))]
    public void Each_shipped_skin_ships_its_own_muted_layer(string style)
    {
        DefaultSkins.EnsureInstalled(_root);
        var skin = SkinLoader.Load(Path.Combine(_root, style));

        _out.WriteLine($"{style}: muted = {skin.MutedPath ?? "(none — would dim instead)"}");

        Assert.NotNull(skin.MutedPath);
        Assert.True(File.Exists(skin.MutedPath));
    }

    /// <summary>The JSON is generated from the same layout as the drawing, so the fill range it
    /// declares must be the one the artwork was painted for. Written twice, they would drift.</summary>
    [Theory]
    [MemberData(nameof(Styles))]
    public void The_declared_fill_range_matches_the_artwork_it_describes(string style)
    {
        DefaultSkins.EnsureInstalled(_root);
        var skin = SkinLoader.Load(Path.Combine(_root, style));
        var l = SkinArt.LayoutFor(style);

        _out.WriteLine($"{style}: volume declared {skin.FillStartX}..{skin.FillEndX}, " +
                       $"drawn {l.Track.X}..{l.Track.Right}");
        _out.WriteLine($"{style}: airplay declared {skin.AirPlay!.FillStartX}..{skin.AirPlay.FillEndX}, " +
                       $"drawn {l.AirTrack.X}..{l.AirTrack.Right}");

        Assert.Equal(l.Track.X, skin.FillStartX);
        Assert.Equal(l.Track.Right, skin.FillEndX);
        Assert.Equal(l.AirTrack.X, skin.AirPlay.FillStartX);
        Assert.Equal(l.AirTrack.Right, skin.AirPlay.FillEndX);
    }

    /// <summary>The chevron is drawn inside the box the JSON declares as the click target, so what
    /// the eye aims at and what the hit test accepts are the same rectangle.</summary>
    [Theory]
    [MemberData(nameof(Styles))]
    public void The_dropdown_hit_region_is_where_the_chevron_is_drawn(string style)
    {
        DefaultSkins.EnsureInstalled(_root);
        var hit = SkinLoader.Load(Path.Combine(_root, style)).AirPlay!.DropdownHit;
        var drawn = SkinArt.LayoutFor(style).DropdownHit;

        _out.WriteLine($"{style}: declared {hit}, drawn [{drawn.X},{drawn.Y} {drawn.W}x{drawn.H}]");

        Assert.Equal(drawn.X, hit.X);
        Assert.Equal(drawn.Y, hit.Y);
        Assert.Equal(drawn.W, hit.Width);
        Assert.Equal(drawn.H, hit.Height);
    }

    /// <summary>The power button is declared, sits inside the strip, and does not overlap either
    /// the drag range or the chevron. Overlap is not a cosmetic fault here: the strip tests the
    /// connect region FIRST, so a power button straying over the chevron would silently eat the
    /// only way into the device list.</summary>
    [Theory]
    [MemberData(nameof(Styles))]
    public void The_power_button_is_declared_and_clear_of_everything_else(string style)
    {
        DefaultSkins.EnsureInstalled(_root);
        var bar = SkinLoader.Load(Path.Combine(_root, style)).AirPlay!;
        var l = SkinArt.LayoutFor(style);
        var c = l.ConnectHit;

        _out.WriteLine($"{style}: connect declared {bar.ConnectHit}, drawn [{c.X},{c.Y} {c.W}x{c.H}]");
        _out.WriteLine($"  fill {l.AirTrack.X}..{l.AirTrack.Right}, dropdown {l.DropdownHit.X}..{l.DropdownHit.Right}");
        _out.WriteLine($"  connectColor = {bar.ConnectColor ?? "(none)"}");

        Assert.NotNull(bar.ConnectHit);
        Assert.Equal(c.X, bar.ConnectHit!.X);
        Assert.Equal(c.Y, bar.ConnectHit.Y);
        Assert.Equal(c.W, bar.ConnectHit.Width);
        Assert.Equal(c.H, bar.ConnectHit.Height);

        Assert.True(c.X >= 0 && c.Right <= l.Width, "power button runs off the strip horizontally");
        Assert.True(c.Y >= 0 && c.Bottom <= l.AirPlayHeight, "power button runs off the strip vertically");
        Assert.True(c.X >= l.AirTrack.Right, "power button overlaps the drag range");
        Assert.True(c.Right <= l.DropdownHit.X, "power button overlaps the chevron");

        // A lit colour every shipped skin states for itself, so none of them borrows another
        // desktop's accent by falling through to the default.
        Assert.False(string.IsNullOrWhiteSpace(bar.ConnectColor));
    }

    /// <summary>The dropdown must sit inside the strip and clear of the fill range, or the click
    /// that opens the device list is the same click that sets the receiver's volume.</summary>
    [Theory]
    [MemberData(nameof(Styles))]
    public void The_dropdown_is_inside_the_strip_and_clear_of_the_fill_range(string style)
    {
        var l = SkinArt.LayoutFor(style);
        var hit = l.DropdownHit;

        _out.WriteLine($"{style}: strip {l.Width}x{l.AirPlayHeight}, fill {l.AirTrack.X}..{l.AirTrack.Right}, " +
                       $"hit [{hit.X},{hit.Y} {hit.W}x{hit.H}]");

        Assert.True(hit.X >= 0 && hit.Right <= l.Width, "dropdown runs off the strip horizontally");
        Assert.True(hit.Y >= 0 && hit.Bottom <= l.AirPlayHeight, "dropdown runs off the strip vertically");
        Assert.True(hit.X >= l.AirTrack.Right, "dropdown overlaps the drag range");
    }

    /// <summary>The speaker and the percent number sit OUTSIDE the fill range on purpose. The
    /// renderer swaps empty for full across the full height of the band between fillStartX and the
    /// level, so anything drawn in there is replaced as the level rises - a glyph inside it would
    /// flicker between two layers as the user held a volume key.</summary>
    [Theory]
    [MemberData(nameof(Styles))]
    public void The_glyph_is_clear_of_the_fill_range(string style)
    {
        var l = SkinArt.LayoutFor(style);

        _out.WriteLine($"{style}: glyph {l.Glyph.X}..{l.Glyph.Right}, fill {l.Track.X}..{l.Track.Right}");

        Assert.True(l.Glyph.Right <= l.Track.X,
            $"{style}: the speaker at {l.Glyph.X}..{l.Glyph.Right} reaches into the fill range " +
            $"{l.Track.X}..{l.Track.Right} and would be swapped out as the level rises");
    }

    /// <summary>Every style is a genuinely different layout, not one shape recoloured. Comparing
    /// the whole record catches the regression this rewrite exists to fix - three skins that
    /// differed only in palette and shared every measurement.</summary>
    [Fact]
    public void No_two_styles_share_a_layout()
    {
        var seen = new Dictionary<SkinArt.Layout, string>();

        foreach (var style in SkinArt.All)
        {
            var l = SkinArt.LayoutFor(style);
            _out.WriteLine($"{style}: {l.Width}x{l.VolumeHeight} + {l.AirPlayHeight}, " +
                           $"track {l.Track.X}..{l.Track.Right} h{l.Track.H}, " +
                           $"percent {l.PercentAlign}@{l.PercentX} size {l.PercentSize}");

            Assert.False(seen.TryGetValue(l, out var twin),
                $"{style} and {twin} have identical layouts");
            seen[l] = style;
        }
    }

    /// <summary>Every shipped style has a test row. A style added to SkinArt.All without one would
    /// otherwise ship untested, which is exactly how the first three got away with sharing a
    /// layout.</summary>
    [Fact]
    public void Every_shipped_style_is_covered_by_these_tests()
    {
        var covered = Styles().Select(row => (string)row[0]!).ToHashSet(StringComparer.Ordinal);

        _out.WriteLine("SkinArt.All: " + string.Join(", ", SkinArt.All));
        _out.WriteLine("covered:     " + string.Join(", ", covered.OrderBy(s => s)));

        Assert.Equal(SkinArt.All.OrderBy(s => s), covered.OrderBy(s => s));
    }

    /// <summary>A typo in a layer name would otherwise ship a blank PNG.</summary>
    [Fact]
    public void An_unknown_layer_throws_rather_than_drawing_nothing()
    {
        var ex = Assert.Throws<ArgumentException>(() => SkinArt.Draw(SkinArt.Default, "airplay-half"));
        _out.WriteLine(ex.Message);
        Assert.Contains("airplay-half", ex.Message);
    }

    /// <summary>Called on every launch, so it must be idempotent - and it must never overwrite a
    /// skin somebody has edited. Restoring the shipped artwork on the next start is exactly the
    /// behaviour that loses an evening's work.</summary>
    [Fact]
    public void An_edited_shipped_skin_is_never_restored()
    {
        DefaultSkins.EnsureInstalled(_root);

        var marker = Path.Combine(_root, "windows-11", "empty.png");
        File.WriteAllText(marker, "the user replaced this");

        var second = DefaultSkins.EnsureInstalled(_root);

        _out.WriteLine("created on the second pass: " + (second.Count == 0 ? "(nothing)" : string.Join(", ", second)));
        Assert.Empty(second);
        Assert.Equal("the user replaced this", File.ReadAllText(marker));
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void Each_shipped_skin_has_a_notice_that_loads(string style)
    {
        DefaultSkins.EnsureInstalled(_root);
        var skin = SkinLoader.Load(Path.Combine(_root, style));
        var l = SkinArt.LayoutFor(style);
        var n = skin.Notice;

        _out.WriteLine($"{style}: notice={(n is null ? "(none)" : $"{n.Width}x{n.Height} art={n.ArtPath}")} " +
                       $"error={skin.NoticeError ?? "(none)"} (drawn {l.Width}x{l.NoticeHeight})");

        Assert.Null(skin.NoticeError);
        Assert.NotNull(n);
        Assert.True(n!.HasArt);
        Assert.Equal(l.Width, n.Width);
        Assert.Equal(l.NoticeHeight, n.Height);
        Assert.Equal(l.NoticeText.X, n.Text.X);
        Assert.Equal(l.NoticeText.Y, n.Text.Y);
        Assert.Equal(l.NoticeText.W, n.TextWidth);
        Assert.Equal(l.NoticeSize, n.Text.FontSize);
        Assert.Equal(l.NoticeGlyph.X, n.Glyph.X);
        Assert.Equal(l.NoticeGlyph.Y, n.Glyph.Y);
        Assert.Equal(l.NoticeGlyph.H, n.Glyph.Size);
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void The_notice_text_box_sits_inside_the_art_and_clear_of_the_glyph(string style)
    {
        var l = SkinArt.LayoutFor(style);
        _out.WriteLine($"{style}: glyph ..{l.NoticeGlyph.Right}, text {l.NoticeText.X}..{l.NoticeText.Right} of {l.Width}; " +
                       $"glyph bottom {l.NoticeGlyph.Bottom}, text bottom {l.NoticeText.Bottom} of {l.NoticeHeight}");

        Assert.True(l.NoticeText.X >= l.NoticeGlyph.Right, "the text starts under the glyph");
        Assert.True(l.NoticeText.Right <= l.Width, "the text box runs off the art");
        Assert.True(l.NoticeGlyph.Bottom <= l.NoticeHeight && l.NoticeText.Bottom <= l.NoticeHeight,
            "glyph or text runs off the bottom of the art");
    }

    [Fact]
    public void An_existing_shipped_skin_without_a_notice_is_topped_up()
    {
        DefaultSkins.EnsureInstalled(_root);
        var folder = Path.Combine(_root, "windows-11");
        var empty = Path.Combine(folder, "empty.png");
        var emptyBefore = File.ReadAllBytes(empty);

        File.Delete(Path.Combine(folder, "notice.png"));
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "skin.json")))!.AsObject();
        json.Remove("notice");
        json["title"] = "mine";
        File.WriteAllText(Path.Combine(folder, "skin.json"), json.ToJsonString());

        var topped = DefaultSkins.EnsureInstalled(_root);
        var skin = SkinLoader.Load(folder);

        _out.WriteLine($"topped up: {string.Join(", ", topped)}; title={skin.Name} notice={(skin.Notice?.HasArt)} error={skin.NoticeError ?? "(none)"}");
        Assert.Equal(["windows-11"], topped);
        Assert.True(File.Exists(Path.Combine(folder, "notice.png")));
        Assert.True(skin.Notice!.HasArt);
        Assert.Null(skin.NoticeError);
        Assert.Contains("\"title\": \"mine\"", File.ReadAllText(Path.Combine(folder, "skin.json")));
        Assert.Equal(emptyBefore, File.ReadAllBytes(empty));
        Assert.Empty(DefaultSkins.EnsureInstalled(_root));
    }

    [Fact]
    public void A_shipped_skin_that_already_has_a_notice_is_left_alone()
    {
        DefaultSkins.EnsureInstalled(_root);
        var art = Path.Combine(_root, "windows-10", "notice.png");
        var jsonPath = Path.Combine(_root, "windows-10", "skin.json");
        byte[] mine = [1, 2, 3, 4];
        File.WriteAllBytes(art, mine);
        var jsonBefore = File.ReadAllBytes(jsonPath);

        var second = DefaultSkins.EnsureInstalled(_root);

        _out.WriteLine("second pass: " + (second.Count == 0 ? "(nothing)" : string.Join(", ", second)));
        Assert.Empty(second);
        Assert.Equal(mine, File.ReadAllBytes(art));
        Assert.Equal(jsonBefore, File.ReadAllBytes(jsonPath));
    }

    [Fact]
    public void A_user_skin_is_never_topped_up()
    {
        DefaultSkins.EnsureInstalled(_root);
        var mine = Path.Combine(_root, "my-skin");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "skin.json"), "{ \"title\": \"mine\" }");

        var second = DefaultSkins.EnsureInstalled(_root);

        _out.WriteLine("files: " + string.Join(", ", Directory.GetFiles(mine).Select(Path.GetFileName)));
        Assert.Empty(second);
        Assert.Equal(["skin.json"], Directory.GetFiles(mine).Select(Path.GetFileName).ToArray());
        Assert.Equal("{ \"title\": \"mine\" }", File.ReadAllText(Path.Combine(mine, "skin.json")));
    }

    /// <summary>The top-up runs at every launch, unguarded, so a shipped folder whose skin.json
    /// the JSON node API refuses (duplicate keys throw ArgumentException, not JsonException) must
    /// be skipped - not crash startup - and must be left exactly as it was.</summary>
    [Fact]
    public void A_shipped_skin_json_with_duplicate_keys_is_skipped_untouched()
    {
        DefaultSkins.EnsureInstalled(_root);
        var folder = Path.Combine(_root, "windows-11");
        var jsonPath = Path.Combine(folder, "skin.json");
        File.Delete(Path.Combine(folder, "notice.png"));
        File.WriteAllText(jsonPath, """{ "title": "a", "title": "b", "scale": 1 }""");
        var before = File.ReadAllBytes(jsonPath);

        var ex = Record.Exception(() => DefaultSkins.EnsureInstalled(_root));

        _out.WriteLine($"exception: {ex?.GetType().FullName ?? "(none)"} {ex?.Message}");
        _out.WriteLine("files: " + string.Join(", ", Directory.GetFiles(folder).Select(Path.GetFileName)));
        Assert.Null(ex);
        Assert.Equal(before, File.ReadAllBytes(jsonPath));
        Assert.Empty(Directory.GetFiles(folder, "notice.*"));
        Assert.Empty(Directory.GetFiles(folder, ".skin.json.*"));
    }

    /// <summary>GDI+ reports a failed save as ExternalException, which is neither IOException nor
    /// UnauthorizedAccessException. A directory squatting on notice.png makes Bitmap.Save fail
    /// that way every time; the top-up must skip the skin, not crash startup.</summary>
    [Fact]
    public void A_notice_png_that_cannot_be_written_is_skipped_without_throwing()
    {
        DefaultSkins.EnsureInstalled(_root);
        var folder = Path.Combine(_root, "windows-10");
        var jsonPath = Path.Combine(folder, "skin.json");
        File.Delete(Path.Combine(folder, "notice.png"));
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(jsonPath))!.AsObject();
        json.Remove("notice");
        File.WriteAllText(jsonPath, json.ToJsonString());
        var before = File.ReadAllBytes(jsonPath);
        Directory.CreateDirectory(Path.Combine(folder, "notice.png"));

        var ex = Record.Exception(() => DefaultSkins.EnsureInstalled(_root));

        _out.WriteLine($"exception: {ex?.GetType().FullName ?? "(none)"} {ex?.Message}");
        Assert.Null(ex);
        Assert.Equal(before, File.ReadAllBytes(jsonPath));
        Assert.True(Directory.Exists(Path.Combine(folder, "notice.png")));
    }

    /// <summary>The top-up rewrites skin.json through a temp file moved into place; the temp file
    /// must never be left beside it.</summary>
    [Fact]
    public void The_top_up_leaves_no_temp_file_behind()
    {
        DefaultSkins.EnsureInstalled(_root);
        var folder = Path.Combine(_root, "windows-11");
        var jsonPath = Path.Combine(folder, "skin.json");
        File.Delete(Path.Combine(folder, "notice.png"));
        File.WriteAllText(jsonPath, """{ "title": "mine" }""");

        var topped = DefaultSkins.EnsureInstalled(_root);

        var files = Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(f => f).ToArray();
        _out.WriteLine("topped: " + string.Join(", ", topped) + "; files: " + string.Join(", ", files));
        Assert.Equal(["windows-11"], topped);
        Assert.DoesNotContain(files, f => f!.StartsWith('.'));
        Assert.Equal("mine", SkinLoader.Load(folder).Meta.Title);
    }

    /// <summary>The loader reads skin.json case-insensitively, so a hand-written "Notice" block is
    /// the skin's notice. The top-up must see it too, or it adds a second "notice" next to it.</summary>
    [Fact]
    public void A_notice_key_in_any_case_stops_the_top_up()
    {
        DefaultSkins.EnsureInstalled(_root);
        var folder = Path.Combine(_root, "windows-xp");
        var jsonPath = Path.Combine(folder, "skin.json");
        File.Delete(Path.Combine(folder, "notice.png"));
        File.WriteAllText(jsonPath, """{ "Notice": { "background": "#FF102030" } }""");
        var before = File.ReadAllBytes(jsonPath);

        var second = DefaultSkins.EnsureInstalled(_root);
        var skin = SkinLoader.Load(folder);

        _out.WriteLine("second pass: " + (second.Count == 0 ? "(nothing)" : string.Join(", ", second)));
        _out.WriteLine($"skin.json now: {File.ReadAllText(jsonPath)}; notice background={skin.Notice?.Background}");
        Assert.Empty(second);
        Assert.Equal(before, File.ReadAllBytes(jsonPath));
        Assert.Empty(Directory.GetFiles(folder, "notice.*"));
        Assert.Equal("#FF102030", skin.Notice?.Background);
    }

    /// <summary>Turning the notice off on a shipped skin in the designer has to survive the next
    /// launch. The designer's save goes through SkinWriter with Notice = null; the top-up then
    /// runs, and must find the "notice": false the writer left rather than restore the art.</summary>
    [Fact]
    public void A_shipped_skin_whose_notice_was_turned_off_stays_off()
    {
        DefaultSkins.EnsureInstalled(_root);
        var folder = Path.Combine(_root, SkinArt.Default);
        var loaded = SkinLoader.Load(folder);
        Assert.NotNull(loaded.Notice);
        Assert.NotNull(loaded.AirPlay);

        SkinWriter.Save(_root, SkinArt.Default, loaded.EmptyPath, loaded.FullPath,
            new SkinConfig(loaded.Text, loaded.Scale, FillStartX: loaded.FillStartX, FillEndX: loaded.FillEndX,
                Meta: loaded.Meta, Notice: null, AirPlay: loaded.AirPlay),
            loaded.MutedPath);
        var topped = DefaultSkins.EnsureInstalled(_root);
        var jsonText = File.ReadAllText(Path.Combine(folder, "skin.json"));
        var reloaded = SkinLoader.Load(folder);

        _out.WriteLine("topped up: " + (topped.Count == 0 ? "(nothing)" : string.Join(", ", topped)));
        _out.WriteLine("skin.json: " + jsonText);
        _out.WriteLine($"notice={reloaded.Notice?.ToString() ?? "(none)"} error={reloaded.NoticeError ?? "(none)"}");
        _out.WriteLine($"strip expected {loaded.AirPlay}");
        _out.WriteLine($"strip actual   {reloaded.AirPlay?.ToString() ?? "(none)"} error={reloaded.AirPlayError ?? "(none)"}");
        Assert.Empty(topped);
        Assert.Empty(Directory.GetFiles(folder, "notice.*"));
        using var doc = System.Text.Json.JsonDocument.Parse(jsonText);
        Assert.Equal(System.Text.Json.JsonValueKind.False, doc.RootElement.GetProperty("notice").ValueKind);
        Assert.True(reloaded.IsValid, reloaded.Error);
        Assert.Null(reloaded.Notice);
        Assert.Equal(loaded.AirPlay, reloaded.AirPlay);
        Assert.Equal(2, Directory.GetFiles(folder, "airplay-*").Length);
    }

    /// <summary>Nothing is left behind mid-write. The staging folder is a dot-folder, which
    /// SkinLoader.Scan skips, but one left lying around would still be litter.</summary>
    [Fact]
    public void No_staging_folders_survive_a_successful_install()
    {
        DefaultSkins.EnsureInstalled(_root);

        var strays = Directory.GetDirectories(_root)
            .Select(d => new DirectoryInfo(d).Name)
            .Where(n => n.StartsWith('.'))
            .ToList();

        _out.WriteLine("dot-folders: " + (strays.Count == 0 ? "(none)" : string.Join(", ", strays)));
        Assert.Empty(strays);
    }
}
