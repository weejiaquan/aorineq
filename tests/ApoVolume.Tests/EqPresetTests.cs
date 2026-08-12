using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class EqPresetTests
{
    private readonly ITestOutputHelper _out;
    public EqPresetTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Serialize_writes_exact_parametric_eq_lines()
    {
        var preset = new EqPreset("test", -3.5, new[]
        {
            new EqBand(EqBandType.Peak, 105, -2.9, 0.7),
            new EqBand(EqBandType.LowShelf, 80, 6.0, 0.7),
            new EqBand(EqBandType.HighShelf, 10000, -4.0, 0.7),
        });
        var text = preset.Serialize();
        _out.WriteLine(text);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        Assert.Equal("Preamp: -3.5 dB", lines[0]);
        Assert.Equal("Filter 1: ON PK Fc 105 Hz Gain -2.9 dB Q 0.70", lines[1]);
        Assert.Equal("Filter 2: ON LSC Fc 80 Hz Gain 6.0 dB Q 0.70", lines[2]);
        Assert.Equal("Filter 3: ON HSC Fc 10000 Hz Gain -4.0 dB Q 0.70", lines[3]);
    }

    [Fact]
    public void Serialize_notch_lowpass_highpass_use_eapo_tokens()
    {
        var preset = new EqPreset("t", 0, new[]
        {
            new EqBand(EqBandType.Notch, 50, 0, 30),
            new EqBand(EqBandType.LowPass, 5000, 0, 0.71),
            new EqBand(EqBandType.HighPass, 30, 0, 0.71),
        });
        var text = preset.Serialize();
        _out.WriteLine(text);
        Assert.Contains("Filter 1: ON NO Fc 50 Hz Q 30.00", text);
        Assert.Contains("Filter 2: ON LPQ Fc 5000 Hz Q 0.71", text);
        Assert.Contains("Filter 3: ON HPQ Fc 30 Hz Q 0.71", text);
        // No gain token for gainless filter types — EAPO's grammar has none for NO/LP/HP.
        Assert.DoesNotContain("Gain", text);
    }

    [Fact]
    public void Parse_real_autoeq_file_roundtrips_exactly()
    {
        var preset = EqPreset.Parse("HD 650", AutoEqFixture.Hd650ParametricEq);
        _out.WriteLine($"preamp={preset.PreampDb} bands={preset.Bands.Count}");
        Assert.Equal("HD 650", preset.Name);
        Assert.Equal(-6.1, preset.PreampDb, 3);
        Assert.Equal(10, preset.Bands.Count);
        Assert.Equal(EqBandType.LowShelf, preset.Bands[0].Type);
        Assert.Equal(105, preset.Bands[0].Fc, 3);
        Assert.Equal(6.4, preset.Bands[0].GainDb, 3);
        Assert.Equal(0.7, preset.Bands[0].Q, 3);
        Assert.Equal(EqBandType.HighShelf, preset.Bands[5].Type);
        Assert.Equal(EqBandType.Peak, preset.Bands[9].Type);
        Assert.Equal(5332, preset.Bands[9].Fc, 3);

        // Round trip must reproduce the AutoEq file byte-for-byte (modulo trailing newline).
        var roundTripped = preset.Serialize().TrimEnd('\r', '\n');
        Assert.Equal(AutoEqFixture.Hd650ParametricEq.ReplaceLineEndings("\n"),
            roundTripped.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Parse_tolerates_comments_blanks_off_filters_and_junk()
    {
        var text = """
            # a comment
            Preamp: -2.0 dB

            Filter 1: ON PK Fc 1000 Hz Gain 3.0 dB Q 1.00
            Filter 2: OFF PK Fc 2000 Hz Gain -3.0 dB Q 1.00
            NotAFilterLine
            Filter 3: ON XX Fc 3000 Hz Gain 1.0 dB Q 1.00
            """;
        var preset = EqPreset.Parse("t", text);
        _out.WriteLine($"bands={preset.Bands.Count} preamp={preset.PreampDb}");
        Assert.Equal(-2.0, preset.PreampDb, 3);
        var band = Assert.Single(preset.Bands); // OFF skipped, junk + unknown type ignored
        Assert.Equal(1000, band.Fc, 3);
    }

    [Fact]
    public void Parse_accepts_type_aliases_and_defaults_missing_q()
    {
        var text = """
            Filter 1: ON PEQ Fc 100 Hz Gain 1.0 dB Q 2.00
            Filter 2: ON LS Fc 100 Hz Gain 2.0 dB
            Filter 3: ON HS Fc 8000 Hz Gain -2.0 dB
            Filter 4: ON LP Fc 12000 Hz
            Filter 5: ON HP Fc 20 Hz
            Filter 6: ON NO Fc 60 Hz
            """;
        var preset = EqPreset.Parse("t", text);
        foreach (var b in preset.Bands) _out.WriteLine($"{b.Type} Fc={b.Fc} Gain={b.GainDb} Q={b.Q}");
        Assert.Equal(6, preset.Bands.Count);
        Assert.Equal(EqBandType.Peak, preset.Bands[0].Type);
        Assert.Equal(EqBandType.LowShelf, preset.Bands[1].Type);
        Assert.Equal(0.707, preset.Bands[1].Q, 3);     // RBJ S=1 shelf default
        Assert.Equal(EqBandType.HighShelf, preset.Bands[2].Type);
        Assert.Equal(EqBandType.LowPass, preset.Bands[3].Type);
        Assert.Equal(0.707, preset.Bands[3].Q, 3);     // Butterworth default
        Assert.Equal(EqBandType.HighPass, preset.Bands[4].Type);
        Assert.Equal(EqBandType.Notch, preset.Bands[5].Type);
        Assert.Equal(30.0, preset.Bands[5].Q, 3);      // narrow-notch default
        Assert.Equal(0.0, preset.PreampDb);            // no preamp line -> 0
    }

    [Fact]
    public void Parse_converts_bandwidth_to_q_with_the_full_rbj_formula()
    {
        // RBJ: 1/Q = 2·sinh(ln2/2 · BW · ω0/sin ω0) — the ω0/sin ω0 term matters near Nyquist.
        var preset = EqPreset.Parse("t", """
            Filter 1: ON PK Fc 1000 Hz Gain 3.0 dB BW Oct 1
            Filter 2: ON PK Fc 12000 Hz Gain 3.0 dB BW Oct 1
            """);
        Assert.Equal(2, preset.Bands.Count);
        double ExpectedQ(double fc)
        {
            double w0 = 2 * Math.PI * fc / EqResponse.SampleRate;
            return 1.0 / (2.0 * Math.Sinh(Math.Log(2) / 2.0 * 1.0 * w0 / Math.Sin(w0)));
        }
        _out.WriteLine($"1k: Q {preset.Bands[0].Q} (expected {ExpectedQ(1000)}); "
            + $"12k: Q {preset.Bands[1].Q} (expected {ExpectedQ(12000)})");
        Assert.Equal(ExpectedQ(1000), preset.Bands[0].Q, 4);
        Assert.Equal(ExpectedQ(12000), preset.Bands[1].Q, 4);
        // Near Nyquist the corrected Q is visibly smaller than the naive conversion.
        Assert.True(preset.Bands[1].Q < 1.0 / (2.0 * Math.Sinh(Math.Log(2) / 2.0)) - 0.05);
    }

    [Fact]
    public void TryParse_roundtrips_a_serialized_scope_identically()
    {
        var original = new EqPreset("scope", -4.5, new[]
        {
            new EqBand(EqBandType.LowShelf, 105, 6.4, 0.7),
            new EqBand(EqBandType.Peak, 1000, -3.5, 2.0),
            new EqBand(EqBandType.HighShelf, 10000, -2.1, 0.7),
            new EqBand(EqBandType.Notch, 60, 0, 30),
            new EqBand(EqBandType.LowPass, 16000, 0, 0.71),
        });
        var text = original.Serialize();
        _out.WriteLine(text);
        Assert.True(EqPreset.TryParse("scope", text, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal(original.PreampDb, parsed.PreampDb, 3);
        Assert.Equal(original.Bands, parsed.Bands); // records: full structural equality
    }

    [Fact]
    public void TryParse_accepts_real_world_variations()
    {
        var text = "# my EQ\r\n\r\nPreamp: -6.1 dB\r\n"
            + "Filter 1: ON PK Fc 1000 Hz Gain 3.0 dB Q 1.00\r\n"
            + "Filter 2: OFF PK Fc 2000 Hz Gain -3.0 dB Q 1.00\r\n"
            + "Filter 3: None\r\n";
        Assert.True(EqPreset.TryParse("t", text, out var preset, out var error));
        _out.WriteLine($"error={error ?? "<none>"} bands={preset.Bands.Count}");
        Assert.Null(error);
        Assert.Equal(-6.1, preset.PreampDb, 3);
        var band = Assert.Single(preset.Bands); // OFF and None contribute nothing
        Assert.Equal(1000, band.Fc, 3);
    }

    [Theory]
    [InlineData("Filter 1: ON XX Fc 1000 Hz Gain 3.0 dB Q 1.0", "unsupported filter type")]
    [InlineData("Filter 1: ON PK Gain 3.0 dB Q 1.0", "missing 'Fc")]
    [InlineData("Preamp: abc dB", "expected a number")]
    [InlineData("total nonsense here", "expected a 'Filter")]
    [InlineData("Filter 1: MAYBE PK Fc 100 Hz Gain 1 dB Q 1", "expected ON, OFF or None")]
    [InlineData("Filter 1", "missing ':'")]
    public void TryParse_reports_the_failing_line_and_applies_nothing(string bad, string expectedFragment)
    {
        var text = "Preamp: -2.0 dB\nFilter 1: ON PK Fc 500 Hz Gain 1.0 dB Q 1.00\n" + bad;
        Assert.False(EqPreset.TryParse("t", text, out var preset, out var error));
        _out.WriteLine($"'{bad}' -> {error}");
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error);
        Assert.Contains("Line 3", error); // 1-based line number of the offending line
        // Nothing partially applied: the good lines before the failure are discarded too.
        Assert.Empty(preset.Bands);
        Assert.Equal(0, preset.PreampDb);
    }

    [Fact]
    public void Flatten_zeroes_gains_without_dropping_bands()
    {
        var bands = new[]
        {
            new EqBand(EqBandType.LowShelf, 105, 6.4, 0.7),
            new EqBand(EqBandType.Peak, 1000, -3.5, 2.0),
            new EqBand(EqBandType.Notch, 60, 0, 30),
        };
        var flat = EqPreset.Flatten(bands);
        foreach (var b in flat) _out.WriteLine($"{b.Type} Fc={b.Fc} Gain={b.GainDb} Q={b.Q}");
        Assert.Equal(3, flat.Count);
        Assert.All(flat, b => Assert.Equal(0, b.GainDb));
        // Type, Fc and Q are untouched, so the shape can be re-edited afterwards.
        for (int i = 0; i < bands.Length; i++)
        {
            Assert.Equal(bands[i].Type, flat[i].Type);
            Assert.Equal(bands[i].Fc, flat[i].Fc);
            Assert.Equal(bands[i].Q, flat[i].Q);
        }
        // Flattening the gain-bearing types really does produce a flat response. (Gainless
        // types — NO/LP/HP — shape the chain by their nature, not by gain, so they are
        // deliberately left shaping it; Clear all bands is the action that removes those.)
        var gainBands = flat.Where(b => b.HasGain).ToArray();
        Assert.Equal(2, gainBands.Length);
        var response = EqResponse.ResponseDb(gainBands, EqResponse.LogFrequencies(64));
        Assert.All(response, db => Assert.Equal(0, db, 6));
    }

    [Fact]
    public void Parse_clamps_hostile_values()
    {
        var preset = EqPreset.Parse("t", """
            Preamp: -999 dB
            Filter 1: ON PK Fc 999999 Hz Gain 500 dB Q 10000
            Filter 2: ON PK Fc 0.001 Hz Gain -500 dB Q 0
            """);
        _out.WriteLine(string.Join("\n", preset.Bands));
        Assert.Equal(2, preset.Bands.Count);
        Assert.Equal(24000, preset.Bands[0].Fc);
        Assert.Equal(30, preset.Bands[0].GainDb);
        Assert.Equal(50, preset.Bands[0].Q);
        Assert.Equal(10, preset.Bands[1].Fc);
        Assert.Equal(-30, preset.Bands[1].GainDb);
        Assert.Equal(0.1, preset.Bands[1].Q);
        Assert.Equal(-60, preset.PreampDb); // preamp clamped to [-60, 20]
    }

    [Fact]
    public void Parse_ignores_invariant_culture_traps()
    {
        // Values must parse invariant regardless of host culture (comma-decimal locales).
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var preset = EqPreset.Parse("t", "Filter 1: ON PK Fc 105.5 Hz Gain -2.9 dB Q 0.70");
            var band = Assert.Single(preset.Bands);
            Assert.Equal(105.5, band.Fc, 3);
            Assert.Equal(-2.9, band.GainDb, 3);
            var text = new EqPreset("t", -1.5, preset.Bands).Serialize();
            _out.WriteLine(text);
            Assert.Contains("Fc 105.5 Hz", text);
            Assert.Contains("Preamp: -1.5 dB", text);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }
}
