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
}
