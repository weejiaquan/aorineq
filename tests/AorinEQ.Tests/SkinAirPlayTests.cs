using AorinEQ.Core;
using Xunit.Abstractions;
using System.Text;

namespace AorinEQ.Tests;

/// <summary>The optional AirPlay bar a skin may carry.
///
/// The contract this has to keep is the one <see cref="SkinMeta"/> established in 3.2: the block
/// is entirely optional, absence is exactly the format every existing skin is written in, and a
/// skin that does not use it resaves byte-identically. The second contract is new and matters
/// more: a broken AirPlay layer must never cost somebody their VOLUME OSD.</summary>
public class SkinAirPlayTests : IDisposable
{
    private readonly string _dir;
    private readonly ITestOutputHelper _out;

    public SkinAirPlayTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "aorineq-airplay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A valid volume skin, optionally carrying AirPlay artwork and/or an airplay block.</summary>
    private string MakeSkin(string name, bool airPlayArt = false, string? airPlayJson = null,
        int airPlayWidth = 300, int airPlayHeight = 40)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        TestPngs.Write(Path.Combine(folder, "empty.png"), 300, 100);
        TestPngs.Write(Path.Combine(folder, "full.png"), 300, 100);

        if (airPlayArt)
        {
            TestPngs.Write(Path.Combine(folder, "airplay-empty.png"), airPlayWidth, airPlayHeight);
            TestPngs.Write(Path.Combine(folder, "airplay-full.png"), airPlayWidth, airPlayHeight);
        }

        if (airPlayJson is not null)
            File.WriteAllText(Path.Combine(folder, "skin.json"), airPlayJson);

        return folder;
    }

    /// <summary>Every skin written before this release. The AirPlay bar is absent, and the skin is
    /// perfectly valid - the OSD falls back to its native AirPlay bar for these.</summary>
    [Fact]
    public void A_skin_with_no_airplay_artwork_is_valid_and_simply_has_none()
    {
        var skin = SkinLoader.Load(MakeSkin("legacy"));

        _out.WriteLine($"valid={skin.IsValid} airplay={(skin.AirPlay is null ? "(none)" : "present")}");

        Assert.True(skin.IsValid);
        Assert.Null(skin.AirPlay);
        Assert.Null(skin.AirPlayError);
    }

    [Fact]
    public void Airplay_artwork_alone_is_enough_to_get_a_bar()
    {
        var skin = SkinLoader.Load(MakeSkin("art-only", airPlayArt: true));

        _out.WriteLine($"valid={skin.IsValid} bar={skin.AirPlay?.Width}x{skin.AirPlay?.Height}");

        Assert.True(skin.IsValid);
        Assert.NotNull(skin.AirPlay);
        Assert.Equal(300, skin.AirPlay!.Width);
        Assert.Equal(40, skin.AirPlay.Height);
    }

    /// <summary>The AirPlay bar is its OWN size. It sits under the volume bar rather than on top
    /// of it, so there is no reason to force it to the same height - and forcing it would stop a
    /// skin from drawing a slim device strip under a tall volume graphic.</summary>
    [Fact]
    public void The_airplay_bar_does_not_have_to_match_the_volume_bars_size()
    {
        var skin = SkinLoader.Load(
            MakeSkin("different-size", airPlayArt: true, airPlayWidth: 220, airPlayHeight: 24));

        _out.WriteLine($"volume={skin.Width}x{skin.Height} airplay={skin.AirPlay?.Width}x{skin.AirPlay?.Height}");

        Assert.True(skin.IsValid);
        Assert.Equal(220, skin.AirPlay!.Width);
        Assert.Equal(24, skin.AirPlay.Height);
    }

    /// <summary>THE rule that matters most here.
    ///
    /// A half-finished AirPlay bar - one layer present, the other missing - must not take the
    /// volume OSD down with it. The skin stays valid, loses only its AirPlay bar, and says why so
    /// the author can find out; the OSD then draws the native AirPlay bar instead, exactly as it
    /// does for a skin that never had one.</summary>
    [Fact]
    public void A_broken_airplay_layer_costs_the_airplay_bar_and_nothing_else()
    {
        var folder = MakeSkin("half-done");
        TestPngs.Write(Path.Combine(folder, "airplay-empty.png"), 300, 40);
        // airplay-full deliberately absent.

        var skin = SkinLoader.Load(folder);

        _out.WriteLine($"valid={skin.IsValid} error={skin.Error ?? "(none)"}");
        _out.WriteLine($"airplay={(skin.AirPlay is null ? "(none)" : "present")} airplayError={skin.AirPlayError}");

        Assert.True(skin.IsValid);          // the volume bar still works
        Assert.Null(skin.Error);
        Assert.Null(skin.AirPlay);          // but there is no AirPlay bar
        Assert.NotNull(skin.AirPlayError);  // and the author is told why
    }

    /// <summary>Mismatched layers are the same class of mistake and get the same treatment.</summary>
    [Fact]
    public void Airplay_layers_of_different_sizes_disable_the_bar_without_killing_the_skin()
    {
        var folder = MakeSkin("mismatched");
        TestPngs.Write(Path.Combine(folder, "airplay-empty.png"), 300, 40);
        TestPngs.Write(Path.Combine(folder, "airplay-full.png"), 280, 40);

        var skin = SkinLoader.Load(folder);

        _out.WriteLine($"valid={skin.IsValid} airplayError={skin.AirPlayError}");

        Assert.True(skin.IsValid);
        Assert.Null(skin.AirPlay);
        Assert.NotNull(skin.AirPlayError);
        Assert.Contains("airplay-full", skin.AirPlayError!);
    }

    [Fact]
    public void The_fill_range_and_text_anchors_are_read_from_the_airplay_block()
    {
        var json = """
        {
          "airplay": {
            "fillStartX": 8,
            "fillEndX": 292,
            "nameText":    { "show": true, "x": 30, "y": 6, "align": "left" },
            "percentText": { "show": true, "x": 260, "y": 6, "align": "right" }
          }
        }
        """;
        var skin = SkinLoader.Load(MakeSkin("anchored", airPlayArt: true, airPlayJson: json));

        var bar = skin.AirPlay!;
        _out.WriteLine($"fill {bar.FillStartX}..{bar.FillEndX}");
        _out.WriteLine($"name  x={bar.Name?.X} align={bar.Name?.Align}");
        _out.WriteLine($"pct   x={bar.Percent?.X} align={bar.Percent?.Align}");

        Assert.Equal(8, bar.FillStartX);
        Assert.Equal(292, bar.FillEndX);
        Assert.Equal(30, bar.Name!.X);
        Assert.Equal("right", bar.Percent!.Align);
    }

    /// <summary>No fill range declared means the whole width fills, which is what a skin that just
    /// drops in two PNGs expects - and it is what the volume bar already does.</summary>
    [Fact]
    public void An_absent_fill_range_spans_the_whole_bar()
    {
        var skin = SkinLoader.Load(MakeSkin("nofill", airPlayArt: true, airPlayWidth: 240));

        var bar = skin.AirPlay!;
        _out.WriteLine($"fill {bar.FillStartX}..{bar.FillEndX} of width {bar.Width}");

        Assert.Equal(0, bar.FillStartX);
        Assert.Equal(240, bar.FillEndX);
    }

    /// <summary>The click target for the dropdown. Absent means the whole bar opens it, so a skin
    /// that declares nothing still has a usable control rather than a decorative strip.</summary>
    [Fact]
    public void Without_a_declared_hit_region_the_whole_bar_opens_the_dropdown()
    {
        var skin = SkinLoader.Load(MakeSkin("wholebar", airPlayArt: true, airPlayWidth: 240));

        var bar = skin.AirPlay!;
        _out.WriteLine($"hit = {bar.DropdownHit}");

        Assert.Equal(0, bar.DropdownHit.X);
        Assert.Equal(0, bar.DropdownHit.Y);
        Assert.Equal(240, bar.DropdownHit.Width);
        Assert.Equal(40, bar.DropdownHit.Height);
    }

    [Fact]
    public void A_declared_hit_region_is_read_and_clamped_into_the_bar()
    {
        var json = """
        { "airplay": { "dropdownHit": { "x": 200, "y": 8, "w": 900, "h": 20 } } }
        """;
        var skin = SkinLoader.Load(MakeSkin("hit", airPlayArt: true, airPlayWidth: 240, airPlayJson: json));

        var bar = skin.AirPlay!;
        _out.WriteLine($"hit = {bar.DropdownHit} in a {bar.Width}x{bar.Height} bar");

        Assert.Equal(200, bar.DropdownHit.X);
        Assert.Equal(8, bar.DropdownHit.Y);
        // Clamped: a region running off the artwork would be unclickable at its far end, and a
        // skin author has no way to see that from the JSON.
        Assert.Equal(40, bar.DropdownHit.Width);
        Assert.True(bar.DropdownHit.Right <= bar.Width);
    }

    /// <summary>An airplay block with no artwork is a skin.json that promises a bar the folder
    /// cannot draw. It is not fatal - the volume bar is untouched - but it is reported.</summary>
    [Fact]
    public void An_airplay_block_without_artwork_reports_rather_than_pretending()
    {
        var json = """{ "airplay": { "fillStartX": 4, "fillEndX": 100 } }""";
        var skin = SkinLoader.Load(MakeSkin("promised", airPlayArt: false, airPlayJson: json));

        _out.WriteLine($"valid={skin.IsValid} airplayError={skin.AirPlayError}");

        Assert.True(skin.IsValid);
        Assert.Null(skin.AirPlay);
        Assert.NotNull(skin.AirPlayError);
    }

    /// <summary>A malformed airplay block must not take the skin down - the same reasoning the
    /// metadata fields already carry, where "title": 42 must not stop a skin loading.</summary>
    [Fact]
    public void A_malformed_airplay_block_does_not_stop_the_skin_loading()
    {
        var json = """{ "airplay": 42 }""";
        var skin = SkinLoader.Load(MakeSkin("garbage", airPlayArt: true, airPlayJson: json));

        _out.WriteLine($"valid={skin.IsValid} error={skin.Error ?? "(none)"}");

        Assert.True(skin.IsValid);
        Assert.Equal(300, skin.Width);
    }

    /// <summary>Every field of the strip the writer can author, at a non-default value, read back
    /// by the real loader. The designer edits the strip now, so the writer writes it from the
    /// config rather than copying whatever block happened to be on disk.</summary>
    [Fact]
    public void Saving_writes_every_airplay_field_and_loading_reads_it_back()
    {
        var source = MakeSkin("source", airPlayArt: true, airPlayWidth: 300, airPlayHeight: 80);
        var strip = new SkinAirPlay(
            Path.Combine(source, "airplay-empty.png"), Path.Combine(source, "airplay-full.png"),
            300, 40, false, false, 2, 2, 8, 292,
            new SkinText(true, 30, 6, Color: "#FF102030", FontSize: 12),
            new SkinText(true, 290, 6, Align: "right", OutlineColor: "#FF000000", OutlineWidth: 2,
                ShadowColor: "#80000000"),
            new SkinHitRect(260, 4, 30, 30), new SkinHitRect(226, 4, 30, 30), "#FF4CC2FF");

        var folder = SkinWriter.Save(_dir, "authored",
            Path.Combine(source, "empty.png"), Path.Combine(source, "full.png"),
            new SkinConfig(null, 1.0, AirPlay: strip));
        var loaded = SkinLoader.Load(folder);
        var expected = strip with
        {
            EmptyPath = Path.Combine(folder, "airplay-empty.png"),
            FullPath = Path.Combine(folder, "airplay-full.png"),
        };

        _out.WriteLine("skin.json: " + File.ReadAllText(Path.Combine(folder, "skin.json")));
        _out.WriteLine($"expected {expected}");
        _out.WriteLine($"loaded   {loaded.AirPlay?.ToString() ?? "(none)"} error={loaded.AirPlayError ?? "(none)"}");
        Assert.Equal(expected, loaded.AirPlay);
    }

    /// <summary>A strip at the loader's defaults writes an empty block - present, so the strip is
    /// declared, with every default omitted the way the rest of skin.json is written.</summary>
    [Fact]
    public void A_strip_at_its_defaults_writes_an_empty_block()
    {
        var source = MakeSkin("plain-strip", airPlayArt: true);
        var strip = new SkinAirPlay(
            Path.Combine(source, "airplay-empty.png"), Path.Combine(source, "airplay-full.png"),
            300, 40, false, false, 1, 1, 0, 300, null, null, new SkinHitRect(0, 0, 300, 40));

        var folder = SkinWriter.Save(_dir, "plain-strip",
            Path.Combine(source, "empty.png"), Path.Combine(source, "full.png"),
            new SkinConfig(null, 1.0, AirPlay: strip));
        var json = File.ReadAllText(Path.Combine(folder, "skin.json"));

        _out.WriteLine("skin.json: " + json);
        Assert.Contains("\"airplay\":{}", json);
        Assert.Equal(strip, SkinLoader.Load(folder).AirPlay);
    }

    /// <summary>No strip in the config is no strip on disk: the block AND both layers go, in both
    /// extensions, so a removed strip cannot come back on the next load.</summary>
    [Fact]
    public void A_config_without_a_strip_removes_its_files_and_block()
    {
        var folder = MakeSkin("had-strip", airPlayArt: true, airPlayJson: """{"airplay":{"fillStartX":8}}""");
        TestPngs.WriteGif(Path.Combine(folder, "airplay-full.gif"), 300, 40);

        SkinWriter.Save(_dir, "had-strip",
            Path.Combine(folder, "empty.png"), Path.Combine(folder, "full.png"),
            new SkinConfig(null, 1.0));
        var left = Directory.GetFiles(folder, "airplay-*");
        var loaded = SkinLoader.Load(folder);

        _out.WriteLine($"left behind: [{string.Join(", ", left.Select(Path.GetFileName))}]; skin.json exists: " +
                       File.Exists(Path.Combine(folder, "skin.json")));
        Assert.Empty(left);
        Assert.False(File.Exists(Path.Combine(folder, "skin.json")));
        Assert.Null(loaded.AirPlay);
        Assert.Null(loaded.AirPlayError);
    }

    /// <summary>And a skin that never had one still resaves without growing an empty block - the
    /// byte-identical contract SkinMeta established in 3.2.</summary>
    [Fact]
    public void Resaving_a_skin_without_airplay_does_not_invent_a_block()
    {
        var folder = MakeSkin("plain", airPlayJson: """{ "percentText": { "show": true, "x": 5, "y": 6 } }""");

        SkinWriter.Save(_dir, "plain",
            Path.Combine(folder, "empty.png"), Path.Combine(folder, "full.png"),
            new SkinConfig(new SkinText(true, 5, 6), Scale: 1.0));

        var text = File.ReadAllText(Path.Combine(folder, "skin.json"));
        _out.WriteLine(text);

        Assert.DoesNotContain("airplay", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The loss the carry-through documented and could not fix: a save-as whose volume art
    /// was swapped for loose files first. No folder knew what the strip was then; the config does
    /// now, so the copy keeps every field and both layers.</summary>
    [Fact]
    public void Saving_under_a_new_name_with_loose_volume_art_keeps_the_strip()
    {
        var json = """
        { "airplay": { "fillStartX": 8, "fillEndX": 292,
                       "nameText": { "show": true, "x": 30, "y": 6 },
                       "dropdownHit": { "x": 260, "y": 4, "w": 30, "h": 30 } } }
        """;
        var original = SkinLoader.Load(MakeSkin("original", airPlayArt: true, airPlayJson: json));
        var loose = MakeSkin("loose-art");

        var copyFolder = SkinWriter.Save(_dir, "copied",
            Path.Combine(loose, "empty.png"), Path.Combine(loose, "full.png"),
            new SkinConfig(null, 1.0, AirPlay: original.AirPlay));
        var copy = SkinLoader.Load(copyFolder);
        var expected = original.AirPlay! with
        {
            EmptyPath = Path.Combine(copyFolder, "airplay-empty.png"),
            FullPath = Path.Combine(copyFolder, "airplay-full.png"),
        };

        _out.WriteLine($"original {original.AirPlay}");
        _out.WriteLine($"copy     {copy.AirPlay?.ToString() ?? "(none)"} error={copy.AirPlayError ?? "(none)"}");
        Assert.True(copy.IsValid, copy.Error);
        Assert.Equal(expected, copy.AirPlay);
    }

    /// <summary>Saving a GIF strip over a folder that still holds the PNG one must remove the PNG.
    /// The loader prefers .png, so leaving it would keep the OLD artwork winning and the save would
    /// appear to have done nothing at all.</summary>
    [Fact]
    public void Replacing_a_png_strip_with_a_gif_one_removes_the_stale_png()
    {
        var target = MakeSkin("gif-target", airPlayArt: true);          // has airplay-*.png
        var source = MakeSkin("gif-source");
        TestPngs.WriteGif(Path.Combine(source, "airplay-empty.gif"), 300, 32);
        TestPngs.WriteGif(Path.Combine(source, "airplay-full.gif"), 300, 32);
        var strip = new SkinAirPlay(
            Path.Combine(source, "airplay-empty.gif"), Path.Combine(source, "airplay-full.gif"),
            300, 32, true, true, 1, 1, 0, 300, null, null, new SkinHitRect(0, 0, 300, 32));

        SkinWriter.Save(_dir, "gif-target",
            Path.Combine(source, "empty.png"), Path.Combine(source, "full.png"),
            new SkinConfig(null, 1.0, AirPlay: strip));

        bool png = File.Exists(Path.Combine(target, "airplay-empty.png"));
        bool gif = File.Exists(Path.Combine(target, "airplay-empty.gif"));
        var loaded = SkinLoader.Load(target).AirPlay;
        _out.WriteLine($"after save: airplay-empty.png={png} airplay-empty.gif={gif} loaded={loaded}");

        Assert.False(png, "the stale PNG survived, so the loader would keep showing it");
        Assert.True(gif);
        Assert.True(loaded is { EmptyIsGif: true, FullIsGif: true });
    }

    // ---- the optional power button ----------------------------------------------------

    /// <summary>connectHit is OPT-IN and has no default, which is the opposite of dropdownHit.
    ///
    /// dropdownHit falling back to the whole bar is what makes a two-PNG skin usable. A connect
    /// button doing the same would put an invisible power switch under every pixel of every skin
    /// written before this existed, and connecting to a speaker by accident is not a small
    /// mistake to make on somebody's behalf.</summary>
    [Fact]
    public void A_skin_that_declares_no_connect_region_gets_no_power_button()
    {
        var folder = MakeSkin("no-connect", airPlayArt: true, airPlayJson: """
        { "airplay": { "dropdownHit": { "x": 260, "y": 4, "w": 30, "h": 24 } } }
        """);

        var bar = SkinLoader.Load(folder).AirPlay!;
        _out.WriteLine($"connectHit = {bar.ConnectHit?.ToString() ?? "(none)"}");
        _out.WriteLine($"dropdownHit = {bar.DropdownHit}");

        Assert.Null(bar.ConnectHit);
        Assert.Equal(260, bar.DropdownHit.X); // and the dropdown is unaffected
    }

    /// <summary>A skin with NO airplay block at all - every skin shipped before this - is still
    /// valid, still gets the whole bar as its dropdown, and still has no power button.</summary>
    [Fact]
    public void A_skin_with_no_airplay_block_has_no_power_button_either()
    {
        var bar = SkinLoader.Load(MakeSkin("bare", airPlayArt: true)).AirPlay!;

        _out.WriteLine($"connectHit = {bar.ConnectHit?.ToString() ?? "(none)"}, dropdown = {bar.DropdownHit}");

        Assert.Null(bar.ConnectHit);
        Assert.Equal(new SkinHitRect(0, 0, bar.Width, bar.Height), bar.DropdownHit);
    }

    [Fact]
    public void A_declared_connect_region_is_read_with_its_colour()
    {
        var folder = MakeSkin("with-connect", airPlayArt: true, airPlayJson: """
        {
          "airplay": {
            "connectHit": { "x": 230, "y": 6, "w": 28, "h": 22 },
            "connectColor": "#FF2CC12C",
            "dropdownHit": { "x": 264, "y": 4, "w": 30, "h": 24 }
          }
        }
        """);

        var bar = SkinLoader.Load(folder).AirPlay!;
        _out.WriteLine($"connectHit = {bar.ConnectHit}, colour = {bar.ConnectColor}");

        Assert.Equal(new SkinHitRect(230, 6, 28, 22), bar.ConnectHit);
        Assert.Equal("#FF2CC12C", bar.ConnectColor);
    }

    /// <summary>Clamped into the artwork like every other declared region, so a region running off
    /// the strip cannot put a live control where there are no pixels.</summary>
    [Fact]
    public void A_connect_region_outside_the_artwork_is_pulled_back_inside()
    {
        var folder = MakeSkin("overflow", airPlayArt: true, airPlayWidth: 300, airPlayHeight: 40,
            airPlayJson: """
        { "airplay": { "connectHit": { "x": 290, "y": 30, "w": 400, "h": 400 } } }
        """);

        var hit = SkinLoader.Load(folder).AirPlay!.ConnectHit!;
        _out.WriteLine($"clamped to {hit} inside 300x40");

        Assert.True(hit.Right <= 300, $"right edge {hit.Right} escapes the artwork");
        Assert.True(hit.Bottom <= 40, $"bottom edge {hit.Bottom} escapes the artwork");
    }

    /// <summary>A zero-area region is an authoring slip, not a control. Dropped rather than kept as
    /// a button nobody can hit and nothing can draw.</summary>
    [Theory]
    [InlineData(0, 20)]
    [InlineData(20, 0)]
    [InlineData(0, 0)]
    public void A_zero_area_connect_region_is_dropped(int w, int h)
    {
        var folder = MakeSkin($"zero-{w}-{h}", airPlayArt: true, airPlayJson: $$"""
        { "airplay": { "connectHit": { "x": 200, "y": 4, "w": {{w}}, "h": {{h}} } } }
        """);

        var bar = SkinLoader.Load(folder).AirPlay!;
        _out.WriteLine($"{w}x{h} -> {bar.ConnectHit?.ToString() ?? "(dropped)"}");

        Assert.Null(bar.ConnectHit);
    }

    /// <summary>A colour without a region is not a button. It must not resurrect one, and it must
    /// not fail the skin either.</summary>
    [Fact]
    public void A_connect_colour_with_no_region_is_not_a_button()
    {
        var folder = MakeSkin("colour-only", airPlayArt: true, airPlayJson: """
        { "airplay": { "connectColor": "#FF00FF00" } }
        """);

        var skin = SkinLoader.Load(folder);
        _out.WriteLine($"valid={skin.IsValid} connectHit={skin.AirPlay!.ConnectHit?.ToString() ?? "(none)"}");

        Assert.True(skin.IsValid);
        Assert.Null(skin.AirPlay.ConnectHit);
    }

    /// <summary>A pre-AirPlay skin resaved gains neither a block nor files, and its second save
    /// writes the same bytes as its first.</summary>
    [Fact]
    public void A_legacy_skin_gains_no_strip_files_on_resave()
    {
        var folder = MakeSkin("legacy", airPlayJson: """{"percentText":{"show":true,"x":5,"y":6}}""");
        SkinWriter.Save(_dir, "legacy", Path.Combine(folder, "empty.png"), Path.Combine(folder, "full.png"),
            new SkinConfig(new SkinText(true, 5, 6), 1.0));
        var once = File.ReadAllBytes(Path.Combine(folder, "skin.json"));
        SkinWriter.Save(_dir, "legacy", Path.Combine(folder, "empty.png"), Path.Combine(folder, "full.png"),
            new SkinConfig(new SkinText(true, 5, 6), 1.0));
        var twice = File.ReadAllBytes(Path.Combine(folder, "skin.json"));

        _out.WriteLine($"first save : {Encoding.UTF8.GetString(once)}");
        _out.WriteLine($"second save: {Encoding.UTF8.GetString(twice)}");
        Assert.Empty(Directory.GetFiles(folder, "airplay-*"));
        Assert.Equal(once, twice);
        Assert.DoesNotContain("airplay", Encoding.UTF8.GetString(once), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every shipped skin, loaded and saved in place, is the same skin; and the file that
    /// save wrote resaves to the same bytes. The FIRST save re-serializes the shipped,
    /// hand-indented skin.json compactly - as it always has - so byte identity is pinned from the
    /// writer's own output, and sameness of the skin from the shipped one.</summary>
    [Fact]
    public void Every_shipped_skin_resaves_to_the_same_skin_and_then_byte_identically()
    {
        var root = Path.Combine(_dir, "shipped");
        DefaultSkins.EnsureInstalled(root);

        foreach (var style in SkinArt.All)
        {
            var folder = Path.Combine(root, style);
            var original = SkinLoader.Load(folder);
            Assert.True(original.IsValid, original.Error);
            Assert.NotNull(original.AirPlay);

            SaveInPlace(root, original);
            var resaved = SkinLoader.Load(folder);
            var first = File.ReadAllBytes(Path.Combine(folder, "skin.json"));
            SaveInPlace(root, resaved);
            var second = File.ReadAllBytes(Path.Combine(folder, "skin.json"));

            _out.WriteLine($"{style}: {Encoding.UTF8.GetString(first)}");
            Assert.Equal(original, resaved);
            Assert.Equal(first, second);
        }
    }

    private static void SaveInPlace(string root, SkinInfo info) =>
        SkinWriter.Save(root, info.Name, info.EmptyPath, info.FullPath, ConfigOf(info), info.MutedPath);

    private static SkinConfig ConfigOf(SkinInfo i)
    {
        bool whole = i.FillStartX == 0 && i.FillEndX == i.Width;
        return new SkinConfig(i.Text, i.Scale, i.Fps, i.EmptyFrames, i.FullFrames,
            whole ? null : i.FillStartX, whole ? null : i.FillEndX,
            i.MutedFrames, i.MutedDim, i.Meta, i.Notice, i.AirPlay);
    }
}
