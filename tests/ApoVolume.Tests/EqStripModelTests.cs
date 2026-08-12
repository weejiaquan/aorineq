using ApoVolume.Core;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

/// <summary>Regression cover for a user-reported bug: importing an AutoEq preset updated the
/// response curve and the rendered filters, but the band strip below kept showing the PREVIOUS
/// chain. The cause was structural — the strip was refreshed only by the paths that changed it
/// one band at a time (+ / × / a node drag), while every BULK replace (AutoEq import, preset
/// switch, pasted text, an apo-volume:// preset link, a scope or mode switch) swapped the band
/// list and refreshed only the plot.
///
/// The fix makes the strip a projection of the band list — <see cref="EqStripModel"/> — that the
/// editor rebuilds from the model on every bulk path. These tests pin the projection: for a
/// replaced chain the strip model must match the NEW bands exactly in count, order and values,
/// and a selection index left over from the old chain must never point past the end.</summary>
public class EqStripModelTests
{
    private readonly ITestOutputHelper _out;
    public EqStripModelTests(ITestOutputHelper output) => _out = output;

    private void Dump(IReadOnlyList<EqBandColumn> columns)
    {
        foreach (var c in columns)
            _out.WriteLine($"#{c.Number} {c.Type} Fc {c.Fc} Gain {c.GainDb} Q {c.Q} "
                + $"gainEnabled={c.GainEnabled} selected={c.Selected}");
    }

    /// <summary>Asserts the strip shows exactly this chain, in order, with the model's values.</summary>
    private void AssertMatches(IReadOnlyList<EqBand> bands, IReadOnlyList<EqBandColumn> columns)
    {
        Assert.Equal(bands.Count, columns.Count);
        for (int i = 0; i < bands.Count; i++)
        {
            Assert.Equal(i + 1, columns[i].Number);
            Assert.Equal(bands[i].Type, columns[i].Type);
            Assert.Equal(EqStripModel.FormatFc(bands[i].Fc), columns[i].Fc);
            Assert.Equal(EqStripModel.FormatGain(bands[i].GainDb), columns[i].GainDb);
            Assert.Equal(EqStripModel.FormatQ(bands[i].Q), columns[i].Q);
            Assert.Equal(bands[i].HasGain, columns[i].GainEnabled);
        }
    }

    [Fact]
    public void An_autoeq_import_replaces_the_strip_with_the_imported_chain()
    {
        // The exact path the user hit: a three-band chain in the editor, then a real AutoEq
        // import (Sennheiser HD 650, 10 bands).
        var before = new[]
        {
            new EqBand(EqBandType.Peak, 1000, 0, 1.41),
            new EqBand(EqBandType.Peak, 2000, 0, 1.41),
            new EqBand(EqBandType.Peak, 4000, 0, 1.41),
        };
        var imported = EqPreset.Parse("Sennheiser HD 650", AutoEqFixture.Hd650ParametricEq);
        Assert.Equal(10, imported.Bands.Count);

        var columns = EqStripModel.Build(imported.Bands,
            EqStripModel.ClampSelection(selected: 0, imported.Bands.Count));
        Dump(columns);

        AssertMatches(imported.Bands, columns);
        Assert.NotEqual(before.Length, columns.Count); // the stale chain is gone, not merged
        Assert.Equal("105", columns[0].Fc);
        Assert.Equal("6.4", columns[0].GainDb);
        Assert.Equal("0.70", columns[0].Q);
        Assert.Equal(EqBandType.HighShelf, columns[5].Type);
    }

    [Fact]
    public void Applying_pasted_text_replaces_the_strip_with_the_parsed_chain()
    {
        const string pasted = """
            Preamp: -3.0 dB
            Filter 1: ON PK Fc 63 Hz Gain 4.5 dB Q 1.10
            Filter 2: ON NO Fc 8000 Hz Q 30.00
            Filter 3: ON HPQ Fc 25 Hz Q 0.71
            """;
        Assert.True(EqPreset.TryParse("pasted", pasted, out var preset, out var error), error);

        var columns = EqStripModel.Build(preset.Bands, EqStripModel.ClampSelection(0, preset.Bands.Count));
        Dump(columns);

        AssertMatches(preset.Bands, columns);
        Assert.Equal(3, columns.Count);
        // Gainless filter types have no Gain token in Equalizer APO's grammar at all, so their
        // gain box must be disabled rather than showing an editable 0.
        Assert.True(columns[0].GainEnabled);
        Assert.False(columns[1].GainEnabled);
        Assert.False(columns[2].GainEnabled);
    }

    [Fact]
    public void A_protocol_applied_preset_lands_in_the_strip_too()
    {
        // apo-volume://apply-preset with an inline payload: the same projection, so a shared
        // link cannot leave the strip stale either.
        var shared = EqPreset.Parse("Shared preset", AutoEqFixture.Hd650ParametricEq);
        Assert.True(EqShare.TryBuildShareUrl(shared, out var url, out _));
        var link = ProtocolLink.Parse(url).Link!;

        var columns = EqStripModel.Build(link.Preset!.Bands, 0);
        Dump(columns);
        AssertMatches(shared.Bands, columns);
    }

    [Fact]
    public void Simple_mode_macro_bands_show_up_in_the_strip_as_ordinary_bands()
    {
        // Switching Simple -> Advanced must reveal the three reserved bands in the strip.
        var bands = EqSimpleMode.Apply(
            EqPreset.Parse("x", AutoEqFixture.Hd650ParametricEq).Bands,
            owned: false, new MacroGains(4, -1.5, 6));

        var columns = EqStripModel.Build(bands, 0);
        Dump(columns);

        AssertMatches(bands, columns);
        Assert.Equal(13, columns.Count);
        Assert.Equal("4.0", columns[10].GainDb);
        Assert.Equal("-1.5", columns[11].GainDb);
        Assert.Equal("6.0", columns[12].GainDb);
    }

    [Theory]
    [InlineData(9, 3, 2)]    // selection past the end of a shorter chain -> last band
    [InlineData(0, 0, -1)]   // chain cleared -> nothing selected
    [InlineData(-1, 5, 0)]   // nothing was selected, now something can be
    [InlineData(2, 10, 2)]   // still valid -> kept
    public void A_stale_selection_never_points_past_the_end(int selected, int newCount, int expected)
    {
        _out.WriteLine($"selected {selected} against {newCount} bands -> {expected}");
        Assert.Equal(expected, EqStripModel.ClampSelection(selected, newCount));
    }

    [Fact]
    public void Clamping_keeps_the_strip_and_the_selection_consistent_after_a_shrinking_replace()
    {
        // The crash shape: 10 bands with the last one selected, replaced by a 3-band preset.
        var big = EqPreset.Parse("big", AutoEqFixture.Hd650ParametricEq).Bands;
        int selected = big.Count - 1;
        var small = new[]
        {
            new EqBand(EqBandType.Peak, 100, 1, 1),
            new EqBand(EqBandType.Peak, 200, 2, 1),
            new EqBand(EqBandType.Peak, 300, 3, 1),
        };

        int clamped = EqStripModel.ClampSelection(selected, small.Length);
        var columns = EqStripModel.Build(small, clamped);
        Dump(columns);

        Assert.Equal(2, clamped);
        Assert.Equal(3, columns.Count);
        Assert.Single(columns, c => c.Selected);
        Assert.True(columns[2].Selected);
    }

    [Fact]
    public void An_empty_chain_projects_to_an_empty_strip_with_nothing_selected()
    {
        var columns = EqStripModel.Build(Array.Empty<EqBand>(), EqStripModel.ClampSelection(4, 0));
        Assert.Empty(columns);
    }
}
