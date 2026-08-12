using ApoVolume.Core;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

/// <summary>Simple mode is three macro sliders over the SAME band model the full editor edits —
/// there is no parallel state — so the mapping between "bass/mid/treble" and a band chain is
/// where every honesty guarantee lives: an existing chain (an AutoEq import, say) is never
/// silently discarded, switching modes round-trips losslessly, and the three macro bands are
/// identified by their reserved shape rather than by a marker in the Equalizer APO file.</summary>
public class EqSimpleModeTests
{
    private readonly ITestOutputHelper _out;
    public EqSimpleModeTests(ITestOutputHelper output) => _out = output;

    private static readonly EqBand[] AutoEqImport =
    {
        new(EqBandType.LowShelf, 105, -1.4, 0.7),
        new(EqBandType.Peak, 3200, 2.6, 1.8),
        new(EqBandType.Peak, 9000, -4.1, 3.0),
    };

    [Fact]
    public void Macro_edits_produce_exactly_the_three_reserved_bands()
    {
        var bands = EqSimpleMode.Apply(Array.Empty<EqBand>(), new MacroGains(4, -2, 6));

        foreach (var b in bands)
            _out.WriteLine(EqPreset.FormatFilterLine(1, b));
        Assert.Equal(3, bands.Count);
        Assert.Equal(new EqBand(EqBandType.LowShelf, 100, 4, 0.7), bands[0]);
        Assert.Equal(new EqBand(EqBandType.Peak, 1000, -2, 0.7), bands[1]);
        Assert.Equal(new EqBand(EqBandType.HighShelf, 8000, 6, 0.7), bands[2]);
    }

    [Fact]
    public void Repeated_edits_replace_the_macro_gains_rather_than_stacking_bands()
    {
        var bands = EqSimpleMode.Apply(Array.Empty<EqBand>(), new MacroGains(4, 0, 0));
        bands = EqSimpleMode.Apply(bands, new MacroGains(-3, 1, 2));
        bands = EqSimpleMode.Apply(bands, new MacroGains(0, 0, 0));

        Assert.Equal(3, bands.Count);
        Assert.All(bands, b => Assert.Equal(0, b.GainDb));
    }

    [Fact]
    public void Gains_are_clamped_to_the_macro_range()
    {
        var bands = EqSimpleMode.Apply(Array.Empty<EqBand>(), new MacroGains(99, -99, double.NaN));

        _out.WriteLine(string.Join(" / ", bands.Select(b => b.GainDb)));
        Assert.Equal(EqSimpleMode.MaxGainDb, bands[0].GainDb);
        Assert.Equal(-EqSimpleMode.MaxGainDb, bands[1].GainDb);
        Assert.Equal(0, bands[2].GainDb); // not a number at all -> no boost, never NaN into EAPO
    }

    [Fact]
    public void Foreign_bands_survive_untouched_and_stay_in_front()
    {
        var bands = EqSimpleMode.Apply(AutoEqImport, new MacroGains(5, 0, -3));

        _out.WriteLine(string.Join("\n", bands.Select((b, i) => EqPreset.FormatFilterLine(i + 1, b))));
        Assert.Equal(6, bands.Count);
        Assert.Equal(AutoEqImport, bands.Take(3));
        Assert.Equal(AutoEqImport, EqSimpleMode.ForeignBands(bands));
        Assert.True(EqSimpleMode.HasForeignBands(bands));
    }

    [Fact]
    public void Editing_macro_gains_on_top_of_an_import_never_touches_the_imported_bands()
    {
        var bands = EqSimpleMode.Apply(AutoEqImport, new MacroGains(5, 0, -3));
        for (double g = -12; g <= 12; g += 3)
            bands = EqSimpleMode.Apply(bands, new MacroGains(g, g / 2, -g));

        Assert.Equal(6, bands.Count);
        Assert.Equal(AutoEqImport, bands.Take(3));
    }

    [Fact]
    public void Mode_round_trip_is_lossless()
    {
        // Simple -> Advanced is just "show the bands"; Advanced -> Simple re-detects them. The
        // chain must come back identical, foreign bands and macro gains alike.
        var simple = EqSimpleMode.Apply(AutoEqImport, new MacroGains(-4.5, 1.5, 7));

        Assert.True(EqSimpleMode.TryRead(simple, out var gains));
        var back = EqSimpleMode.Apply(simple, gains);

        _out.WriteLine($"gains read back: {gains}");
        Assert.Equal(new MacroGains(-4.5, 1.5, 7), gains);
        Assert.Equal(simple, back);
    }

    [Fact]
    public void Round_trip_survives_the_equalizer_apo_text_format()
    {
        // The chain lives in apo-volume.txt between sessions, so detection has to survive the
        // serializer's rounding — the reserved shapes were chosen to be exactly representable.
        var simple = EqSimpleMode.Apply(AutoEqImport, new MacroGains(-4.5, 1.5, 7));
        var text = new EqPreset("scope", 0, simple).Serialize();
        _out.WriteLine(text);

        var reparsed = EqPreset.Parse("scope", text);
        Assert.True(EqSimpleMode.TryRead(reparsed.Bands, out var gains));
        Assert.Equal(new MacroGains(-4.5, 1.5, 7), gains);
        Assert.Equal(AutoEqImport, EqSimpleMode.ForeignBands(reparsed.Bands));
    }

    [Theory]
    [InlineData(EqBandType.Peak, 100, 0.7)]        // wrong type
    [InlineData(EqBandType.LowShelf, 120, 0.7)]    // wrong frequency
    [InlineData(EqBandType.LowShelf, 100, 1.4)]    // wrong Q
    public void A_macro_band_edited_in_advanced_mode_stops_being_a_macro_band(
        EqBandType type, double fc, double q)
    {
        var edited = EqSimpleMode.Apply(Array.Empty<EqBand>(), new MacroGains(3, 3, 3)).ToList();
        edited[0] = edited[0] with { Type = type, Fc = fc, Q = q };

        Assert.False(EqSimpleMode.TryRead(edited, out _));
        // ...and it is then treated as an ordinary band: Simple mode adds a fresh macro trio and
        // leaves the edited one alone rather than pretending it is still the bass control.
        var reentered = EqSimpleMode.Apply(edited, new MacroGains(0, 0, 0));
        _out.WriteLine(string.Join("\n", reentered.Select((b, i) => EqPreset.FormatFilterLine(i + 1, b))));
        Assert.Equal(6, reentered.Count);
        Assert.Equal(edited, reentered.Take(3));
        Assert.True(EqSimpleMode.TryRead(reentered, out var zero));
        Assert.Equal(new MacroGains(0, 0, 0), zero);
    }

    [Fact]
    public void An_unrecognized_chain_reads_as_flat_rather_than_failing()
    {
        Assert.False(EqSimpleMode.TryRead(AutoEqImport, out _));
        Assert.Equal(new MacroGains(0, 0, 0), EqSimpleMode.ReadOrZero(AutoEqImport));
        Assert.Equal(new MacroGains(0, 0, 0), EqSimpleMode.ReadOrZero(Array.Empty<EqBand>()));
    }

    [Fact]
    public void There_is_no_room_for_macro_bands_in_an_almost_full_scope()
    {
        var full = Enumerable.Range(0, EqPreset.MaxBands - 2)
            .Select(i => new EqBand(EqBandType.Peak, 100 + i, 1, 1)).ToArray();
        _out.WriteLine($"{full.Length} foreign bands, cap {EqPreset.MaxBands}");

        Assert.False(EqSimpleMode.HasRoom(full));
        // A chain that already ends in the macro trio always has room — it needs no new bands.
        Assert.True(EqSimpleMode.HasRoom(EqSimpleMode.Apply(
            full.Take(EqPreset.MaxBands - 3).ToArray(), new MacroGains(1, 1, 1))));
    }

    [Fact]
    public void Apply_never_exceeds_the_band_cap()
    {
        var full = Enumerable.Range(0, EqPreset.MaxBands)
            .Select(i => new EqBand(EqBandType.Peak, 100 + i, 1, 1)).ToArray();

        var result = EqSimpleMode.Apply(full, new MacroGains(6, 6, 6));
        _out.WriteLine($"{full.Length} -> {result.Count}");
        Assert.Equal(full, result); // no room: the chain is returned untouched, nothing dropped
    }

    // ---- which face the editor opens with ----

    [Fact]
    public void A_first_time_eq_user_gets_simple_mode()
    {
        Assert.Equal(EqEditorModes.Simple, EqEditorModes.Resolve(Settings.Default));
    }

    [Fact]
    public void Someone_who_already_has_bands_keeps_the_full_editor()
    {
        // Everyone upgrading from v2.0 was using the full editor; dropping them into three
        // sliders would hide the chain they built.
        var withGlobal = Settings.Default with { GlobalEq = new EqScopeSetting(Bands: AutoEqImport) };
        var withDevice = Settings.Default with
        {
            DeviceEq = new Dictionary<string, EqScopeSetting>
            {
                ["{device}"] = new EqScopeSetting(Bands: AutoEqImport),
            },
        };
        Assert.Equal(EqEditorModes.Advanced, EqEditorModes.Resolve(withGlobal));
        Assert.Equal(EqEditorModes.Advanced, EqEditorModes.Resolve(withDevice));

        // An empty chain is not "already using it".
        var empty = Settings.Default with { GlobalEq = new EqScopeSetting(Bands: Array.Empty<EqBand>()) };
        Assert.Equal(EqEditorModes.Simple, EqEditorModes.Resolve(empty));
    }

    [Theory]
    [InlineData(EqEditorModes.Simple)]
    [InlineData(EqEditorModes.Advanced)]
    public void An_explicit_choice_always_wins(string mode)
    {
        var settings = Settings.Default with
        {
            EqEditorMode = mode,
            GlobalEq = new EqScopeSetting(Bands: AutoEqImport),
        };
        Assert.Equal(mode, EqEditorModes.Resolve(settings));
    }

    [Fact]
    public void The_chosen_mode_survives_a_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), "apo-mode-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            (Settings.Default with { EqEditorMode = EqEditorModes.Simple }).Save(path);
            _out.WriteLine(File.ReadAllText(path));
            Assert.Equal(EqEditorModes.Simple, Settings.Load(path).EqEditorMode);

            // A value from a newer (or corrupted) file falls back to "never chosen".
            File.WriteAllText(path, """{"Percent":50,"Muted":false,"EqEditorMode":"spatial"}""");
            Assert.Equal(EqEditorModes.Unset, Settings.Load(path).EqEditorMode);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
