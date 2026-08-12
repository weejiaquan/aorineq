using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The band-list model behind the editor's Peace-style band strip: appending with a
/// cap, the typed-field policy shared by the strip and the numeric panel, and the renderer's
/// behavior with a large arbitrary band count.</summary>
public class EqBandStripTests
{
    private const string Guid1 = "{aaaaaaaa-1111-2222-3333-444444444444}";

    private readonly ITestOutputHelper _out;
    public EqBandStripTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void NewBand_is_a_neutral_peak_the_user_can_type_over()
    {
        var band = EqPreset.NewBand();
        _out.WriteLine($"{band.Type} Fc={band.Fc} Gain={band.GainDb} Q={band.Q}");
        Assert.Equal(EqBandType.Peak, band.Type);
        Assert.Equal(1000, band.Fc);
        Assert.Equal(0, band.GainDb);
        Assert.Equal(1.41, band.Q, 3);
    }

    [Fact]
    public void TryAppend_grows_without_a_fixed_count_and_stops_at_the_cap()
    {
        var bands = new List<EqBand>();
        for (int i = 0; i < EqPreset.MaxBands; i++)
            Assert.True(EqPreset.TryAppend(bands, EqPreset.NewBand()));
        Assert.Equal(EqPreset.MaxBands, bands.Count);

        // At the cap: refused, and the list is left exactly as it was.
        Assert.False(EqPreset.TryAppend(bands, EqPreset.NewBand()));
        _out.WriteLine($"cap {EqPreset.MaxBands} enforced; count still {bands.Count}");
        Assert.Equal(EqPreset.MaxBands, bands.Count);
    }

    [Fact]
    public void TryAppend_clamps_what_it_stores()
    {
        var bands = new List<EqBand>();
        Assert.True(EqPreset.TryAppend(bands, new EqBand(EqBandType.Peak, 999999, 500, 0)));
        var band = Assert.Single(bands);
        _out.WriteLine($"stored: Fc={band.Fc} Gain={band.GainDb} Q={band.Q}");
        Assert.Equal(EqPreset.MaxFc, band.Fc);
        Assert.Equal(EqPreset.MaxGainDb, band.GainDb);
        Assert.Equal(EqPreset.MinQ, band.Q);
    }

    [Fact]
    public void Field_input_applies_a_valid_typed_value()
    {
        var band = EqPreset.NewBand();
        var updated = EqFieldInput.Apply(band, EqBandField.Fc, "250.5", out var outcome);
        _out.WriteLine($"Fc 250.5 -> {updated.Fc} ({outcome})");
        Assert.Equal(EqFieldOutcome.Applied, outcome);
        Assert.Equal(250.5, updated.Fc, 3);

        updated = EqFieldInput.Apply(band, EqBandField.GainDb, "-6.5", out outcome);
        Assert.Equal(EqFieldOutcome.Applied, outcome);
        Assert.Equal(-6.5, updated.GainDb, 3);

        updated = EqFieldInput.Apply(band, EqBandField.Q, "3.25", out outcome);
        Assert.Equal(EqFieldOutcome.Applied, outcome);
        Assert.Equal(3.25, updated.Q, 3);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1,5")]        // comma decimal: not invariant, must not silently become 15
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Field_input_reverts_unparseable_text_to_the_previous_value(string text)
    {
        var band = new EqBand(EqBandType.Peak, 440, -2.5, 1.2);
        var updated = EqFieldInput.Apply(band, EqBandField.Fc, text, out var outcome);
        _out.WriteLine($"'{text}' -> {outcome}, Fc still {updated.Fc}");
        Assert.Equal(EqFieldOutcome.Reverted, outcome);
        Assert.Equal(band, updated); // every field untouched, not just Fc
    }

    [Theory]
    [InlineData(EqBandField.Fc, "999999")]
    [InlineData(EqBandField.Fc, "0.0001")]
    [InlineData(EqBandField.GainDb, "500")]
    [InlineData(EqBandField.GainDb, "-500")]
    [InlineData(EqBandField.Q, "0")]
    [InlineData(EqBandField.Q, "100000")]
    public void Field_input_reports_out_of_range_values_as_clamped(EqBandField field, string text)
    {
        var band = new EqBand(EqBandType.Peak, 440, -2.5, 1.2);
        var updated = EqFieldInput.Apply(band, field, text, out var outcome);
        _out.WriteLine($"{field}='{text}' -> {outcome} ({updated})");
        Assert.Equal(EqFieldOutcome.Clamped, outcome);
        // Clamped to the model's own documented limits, never to something arbitrary.
        Assert.InRange(updated.Fc, EqPreset.MinFc, EqPreset.MaxFc);
        Assert.InRange(updated.GainDb, -EqPreset.MaxGainDb, EqPreset.MaxGainDb);
        Assert.InRange(updated.Q, EqPreset.MinQ, EqPreset.MaxQ);
    }

    [Fact]
    public void Renderer_emits_every_band_of_a_24_band_scope_in_order()
    {
        var bands = new List<EqBand>();
        for (int i = 0; i < 24; i++)
        {
            // Distinct, recognizable frequencies so order is verifiable line by line.
            Assert.True(EqPreset.TryAppend(bands,
                new EqBand(EqBandType.Peak, 100 + i * 100, i % 2 == 0 ? 1.5 : -1.5, 1.41)));
        }
        var model = new EqConfigModel(false, 0, Array.Empty<EqBand>(), new[]
        {
            new DeviceEqSection(Guid1, -12.0, true, -3.0, bands),
        });
        var text = ApoWriter.RenderConfig(model);
        _out.WriteLine(text);

        var filterLines = text.ReplaceLineEndings("\n").Split('\n')
            .Where(l => l.StartsWith("Filter ")).ToArray();
        Assert.Equal(24, filterLines.Length);
        for (int i = 0; i < 24; i++)
        {
            // Numbering is sequential and each band's Fc lands on its own line, in order.
            Assert.StartsWith($"Filter {i + 1}: ON PK Fc {100 + i * 100} Hz ", filterLines[i]);
        }
        Assert.Contains("Preamp: -15.0 dB", text); // -12.0 volume + -3.0 preset preamp
    }

    [Fact]
    public void Removing_a_band_removes_exactly_that_filter_line()
    {
        var bands = new List<EqBand>
        {
            new(EqBandType.Peak, 100, 1.0, 1.0),
            new(EqBandType.Peak, 200, 2.0, 1.0),
            new(EqBandType.Peak, 300, 3.0, 1.0),
        };
        bands.RemoveAt(1); // the strip's × on the middle column
        var text = ApoWriter.RenderConfig(new EqConfigModel(false, 0, Array.Empty<EqBand>(),
            new[] { new DeviceEqSection(Guid1, 0, true, 0, bands) }));
        _out.WriteLine(text);
        Assert.Contains("Filter 1: ON PK Fc 100 Hz", text);
        // The survivor is renumbered, and the removed frequency is gone entirely.
        Assert.Contains("Filter 2: ON PK Fc 300 Hz", text);
        Assert.DoesNotContain("Fc 200 Hz", text);
    }
}
