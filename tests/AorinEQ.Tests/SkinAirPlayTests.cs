using AorinEQ.Core;
using Xunit.Abstractions;

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

    /// <summary>Saving a skin in the designer must not quietly delete its AirPlay bar.
    ///
    /// SkinWriter rewrites skin.json wholesale from a SkinConfig, and the designer does not know
    /// about the airplay block. The artwork files would survive that - they are never deleted -
    /// so the bar would still LOAD, but stripped of its fill range, its text anchors and its hit
    /// region, and the author would have no idea which save did it. Preserving what the writer
    /// does not understand is the only honest option while the designer cannot edit it.</summary>
    [Fact]
    public void Resaving_a_skin_preserves_an_airplay_block_the_designer_cannot_edit()
    {
        var json = """
        {
          "percentText": { "show": true, "x": 5, "y": 6 },
          "airplay": {
            "fillStartX": 8,
            "fillEndX": 292,
            "nameText": { "show": true, "x": 30, "y": 6 },
            "dropdownHit": { "x": 260, "y": 4, "w": 30, "h": 30 }
          }
        }
        """;
        var folder = MakeSkin("resaved", airPlayArt: true, airPlayJson: json);

        var before = SkinLoader.Load(folder);
        Assert.NotNull(before.AirPlay);

        // What the designer does: same folder, same layers, its own idea of the config.
        SkinWriter.Save(_dir, "resaved",
            Path.Combine(folder, "empty.png"), Path.Combine(folder, "full.png"),
            new SkinConfig(new SkinText(true, 5, 6), Scale: 1.0));

        var after = SkinLoader.Load(folder);

        _out.WriteLine("skin.json after resave:");
        _out.WriteLine(File.ReadAllText(Path.Combine(folder, "skin.json")));

        Assert.NotNull(after.AirPlay);
        Assert.Equal(8, after.AirPlay!.FillStartX);
        Assert.Equal(292, after.AirPlay.FillEndX);
        Assert.Equal(30, after.AirPlay.Name!.X);
        Assert.Equal(260, after.AirPlay.DropdownHit.X);
        Assert.Equal(30, after.AirPlay.DropdownHit.Width);
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

    /// <summary>Saving a skin under a NEW name must carry its AirPlay bar with it.
    ///
    /// The writer preserved the airplay block from the DESTINATION folder, which is right for an
    /// in-place save and wrong for every other kind: a save-as landed in an empty folder, found no
    /// block to keep, and produced a copy with the artwork but none of the geometry - and a plain
    /// skin saved OVER an existing AirPlay skin inherited that skin's block, hybridising the two.
    /// The source is the only folder that can answer "what is this skin".</summary>
    [Fact]
    public void Saving_a_skin_under_a_new_name_carries_its_airplay_bar()
    {
        var json = """
        {
          "airplay": { "fillStartX": 8, "fillEndX": 292,
                       "dropdownHit": { "x": 260, "y": 4, "w": 30, "h": 30 } }
        }
        """;
        var source = MakeSkin("original", airPlayArt: true, airPlayJson: json);

        SkinWriter.Save(_dir, "copied",
            Path.Combine(source, "empty.png"), Path.Combine(source, "full.png"),
            new SkinConfig(null, Scale: 1.0));

        var copy = SkinLoader.Load(Path.Combine(_dir, "copied"));

        _out.WriteLine($"copy valid={copy.IsValid} airplay={(copy.AirPlay is null ? "(none)" : "present")}");
        _out.WriteLine($"airplayError={copy.AirPlayError ?? "(none)"}");

        Assert.True(copy.IsValid, copy.Error);
        Assert.NotNull(copy.AirPlay);
        Assert.Equal(8, copy.AirPlay!.FillStartX);
        Assert.Equal(260, copy.AirPlay.DropdownHit.X);
    }

    /// <summary>Which folder describes the skin being saved.
    ///
    /// The designer lets you swap in loose PNGs from anywhere and then save, so the source folder
    /// is often not a skin at all. When it is not, the DESTINATION is the only folder that knows
    /// what this skin is, and its block is kept - that is an in-place edit with new artwork, which
    /// is the commonest thing the designer does. When the source IS a skin, it wins, because that
    /// is a save-as and the block belongs to the skin being copied.
    ///
    /// Both halves matter: preferring the destination always loses the bar on save-as, and
    /// preferring the source always loses it on an in-place edit with swapped art.</summary>
    [Fact]
    public void An_in_place_save_with_loose_artwork_keeps_the_skins_own_block()
    {
        var json = """{ "airplay": { "fillStartX": 8, "fillEndX": 292 } }""";
        MakeSkin("target", airPlayArt: true, airPlayJson: json);

        // Loose files from somewhere that is not a skin folder - what "Choose empty.png..." gives.
        var loose = MakeSkin("loose-art");

        SkinWriter.Save(_dir, "target",
            Path.Combine(loose, "empty.png"), Path.Combine(loose, "full.png"),
            new SkinConfig(null, Scale: 1.0));

        var reloaded = SkinLoader.Load(Path.Combine(_dir, "target"));
        _out.WriteLine($"reloaded airplay = " +
                       (reloaded.AirPlay is null ? "(none)" : $"{reloaded.AirPlay.FillStartX}..{reloaded.AirPlay.FillEndX}"));

        Assert.True(reloaded.IsValid, reloaded.Error);
        Assert.NotNull(reloaded.AirPlay);
        Assert.Equal(8, reloaded.AirPlay!.FillStartX);
        Assert.Equal(292, reloaded.AirPlay.FillEndX);
    }

    /// <summary>Saving a GIF-backed strip over a folder that still holds the PNG one must remove
    /// the PNG. The loader prefers .png, so leaving it would keep the OLD artwork winning and the
    /// save would appear to have done nothing at all.</summary>
    [Fact]
    public void Replacing_a_png_strip_with_a_gif_one_removes_the_stale_png()
    {
        var target = MakeSkin("gif-target", airPlayArt: true);          // has airplay-*.png
        var source = MakeSkin("gif-source");
        TestPngs.WriteGif(Path.Combine(source, "airplay-empty.gif"), 300, 32);
        TestPngs.WriteGif(Path.Combine(source, "airplay-full.gif"), 300, 32);

        SkinWriter.Save(_dir, "gif-target",
            Path.Combine(source, "empty.png"), Path.Combine(source, "full.png"),
            new SkinConfig(null, Scale: 1.0));

        bool png = File.Exists(Path.Combine(target, "airplay-empty.png"));
        bool gif = File.Exists(Path.Combine(target, "airplay-empty.gif"));
        _out.WriteLine($"after save: airplay-empty.png={png} airplay-empty.gif={gif}");

        Assert.False(png, "the stale PNG survived, so the loader would keep showing it");
        Assert.True(gif);
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
}
