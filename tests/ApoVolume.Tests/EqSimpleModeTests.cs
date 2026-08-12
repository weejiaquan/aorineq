using ApoVolume.Core;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

/// <summary>Simple mode is three macro sliders over the SAME band model the full editor edits —
/// there is no parallel state — so the mapping between "bass/mid/treble" and a band chain is
/// where every honesty guarantee lives: an existing chain (an AutoEq import, say) is never
/// silently discarded, switching modes round-trips losslessly, and the three macro bands are
/// identified by an ownership flag in the app's own store plus their reserved shape, never by a
/// marker inside the Equalizer APO file.
///
/// <c>owned</c> throughout is <see cref="EqScopeSetting.MacroBands"/>: false means the sliders
/// have never created a trio in this scope, whatever the chain happens to look like.</summary>
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

    /// <summary>A chain that ends in exactly the reserved shapes but was NOT created by the
    /// sliders — the collision case.</summary>
    private static readonly EqBand[] LooksLikeMacroBands =
    {
        new(EqBandType.LowShelf, 100, -5, 0.7),
        new(EqBandType.Peak, 1000, 3, 0.7),
        new(EqBandType.HighShelf, 8000, -2, 0.7),
    };

    [Fact]
    public void Macro_edits_produce_exactly_the_three_reserved_bands()
    {
        var bands = EqSimpleMode.Apply(Array.Empty<EqBand>(), owned: false, new MacroGains(4, -2, 6));

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
        var bands = EqSimpleMode.Apply(Array.Empty<EqBand>(), owned: false, new MacroGains(4, 0, 0));
        bands = EqSimpleMode.Apply(bands, owned: true, new MacroGains(-3, 1, 2));
        bands = EqSimpleMode.Apply(bands, owned: true, new MacroGains(0, 0, 0));

        Assert.Equal(3, bands.Count);
        Assert.All(bands, b => Assert.Equal(0, b.GainDb));
    }

    [Fact]
    public void Gains_are_clamped_to_the_macro_range()
    {
        var bands = EqSimpleMode.Apply(
            Array.Empty<EqBand>(), owned: false, new MacroGains(99, -99, double.NaN));

        _out.WriteLine(string.Join(" / ", bands.Select(b => b.GainDb)));
        Assert.Equal(EqSimpleMode.MaxGainDb, bands[0].GainDb);
        Assert.Equal(-EqSimpleMode.MaxGainDb, bands[1].GainDb);
        Assert.Equal(0, bands[2].GainDb); // not a number at all -> no boost, never NaN into EAPO
    }

    [Fact]
    public void Foreign_bands_survive_untouched_and_stay_in_front()
    {
        var bands = EqSimpleMode.Apply(AutoEqImport, owned: false, new MacroGains(5, 0, -3));

        _out.WriteLine(string.Join("\n", bands.Select((b, i) => EqPreset.FormatFilterLine(i + 1, b))));
        Assert.Equal(6, bands.Count);
        Assert.Equal(AutoEqImport, bands.Take(3));
        Assert.Equal(AutoEqImport, EqSimpleMode.ForeignBands(bands, owned: true));
        Assert.True(EqSimpleMode.HasForeignBands(bands, owned: true));
    }

    [Fact]
    public void Editing_macro_gains_on_top_of_an_import_never_touches_the_imported_bands()
    {
        var bands = EqSimpleMode.Apply(AutoEqImport, owned: false, new MacroGains(5, 0, -3));
        for (double g = -12; g <= 12; g += 3)
            bands = EqSimpleMode.Apply(bands, owned: true, new MacroGains(g, g / 2, -g));

        Assert.Equal(6, bands.Count);
        Assert.Equal(AutoEqImport, bands.Take(3));
    }

    /// <summary>The reason ownership is recorded rather than inferred: somebody's real chain can
    /// legitimately end in a 100 Hz low shelf, a 1 kHz peak and an 8 kHz high shelf at Q 0.7.
    /// Shape alone would let the sliders seize and rewrite those bands.</summary>
    [Fact]
    public void A_chain_that_merely_looks_like_the_macro_trio_is_not_seized()
    {
        Assert.False(EqSimpleMode.OwnsMacroBands(LooksLikeMacroBands, owned: false));
        Assert.False(EqSimpleMode.TryRead(LooksLikeMacroBands, owned: false, out _));
        Assert.Equal(LooksLikeMacroBands, EqSimpleMode.ForeignBands(LooksLikeMacroBands, owned: false));

        // Simple mode adds its OWN trio after them; the user's three bands keep their gains.
        var bands = EqSimpleMode.Apply(LooksLikeMacroBands, owned: false, new MacroGains(6, 0, 0));
        _out.WriteLine(string.Join("\n", bands.Select((b, i) => EqPreset.FormatFilterLine(i + 1, b))));
        Assert.Equal(6, bands.Count);
        Assert.Equal(LooksLikeMacroBands, bands.Take(3));
        Assert.True(EqSimpleMode.TryRead(bands, owned: true, out var gains));
        Assert.Equal(new MacroGains(6, 0, 0), gains);
    }

    [Fact]
    public void Mode_round_trip_is_lossless()
    {
        // Simple -> Advanced is just "show the bands"; Advanced -> Simple re-detects them. The
        // chain must come back identical, foreign bands and macro gains alike.
        var simple = EqSimpleMode.Apply(AutoEqImport, owned: false, new MacroGains(-4.5, 1.5, 7));

        Assert.True(EqSimpleMode.TryRead(simple, owned: true, out var gains));
        var back = EqSimpleMode.Apply(simple, owned: true, gains);

        _out.WriteLine($"gains read back: {gains}");
        Assert.Equal(new MacroGains(-4.5, 1.5, 7), gains);
        Assert.Equal(simple, back);
    }

    [Fact]
    public void Round_trip_survives_the_equalizer_apo_text_format()
    {
        // The chain lives in apo-volume.txt between sessions, so detection has to survive the
        // serializer's rounding — the reserved shapes were chosen to be exactly representable.
        var simple = EqSimpleMode.Apply(AutoEqImport, owned: false, new MacroGains(-4.5, 1.5, 7));
        var text = new EqPreset("scope", 0, simple).Serialize();
        _out.WriteLine(text);

        var reparsed = EqPreset.Parse("scope", text);
        Assert.True(EqSimpleMode.TryRead(reparsed.Bands, owned: true, out var gains));
        Assert.Equal(new MacroGains(-4.5, 1.5, 7), gains);
        Assert.Equal(AutoEqImport, EqSimpleMode.ForeignBands(reparsed.Bands, owned: true));
    }

    [Theory]
    [InlineData(EqBandType.Peak, 100, 0.7)]        // wrong type
    [InlineData(EqBandType.LowShelf, 120, 0.7)]    // wrong frequency
    [InlineData(EqBandType.LowShelf, 100, 1.4)]    // wrong Q
    public void A_macro_band_edited_in_advanced_mode_stops_being_a_macro_band(
        EqBandType type, double fc, double q)
    {
        var edited = EqSimpleMode.Apply(
            Array.Empty<EqBand>(), owned: false, new MacroGains(3, 3, 3)).ToList();
        edited[0] = edited[0] with { Type = type, Fc = fc, Q = q };

        // Even with the ownership flag still set, the shape no longer matches — so the sliders
        // no longer control it, which is the honest outcome.
        Assert.False(EqSimpleMode.TryRead(edited, owned: true, out _));
        var reentered = EqSimpleMode.Apply(edited, owned: true, new MacroGains(0, 0, 0));
        _out.WriteLine(string.Join("\n", reentered.Select((b, i) => EqPreset.FormatFilterLine(i + 1, b))));
        Assert.Equal(6, reentered.Count);
        Assert.Equal(edited, reentered.Take(3));
        Assert.True(EqSimpleMode.TryRead(reentered, owned: true, out var zero));
        Assert.Equal(new MacroGains(0, 0, 0), zero);
    }

    [Fact]
    public void An_unrecognized_chain_reads_as_flat_rather_than_failing()
    {
        Assert.False(EqSimpleMode.TryRead(AutoEqImport, owned: true, out _));
        Assert.Equal(new MacroGains(0, 0, 0), EqSimpleMode.ReadOrZero(AutoEqImport, owned: true));
        Assert.Equal(new MacroGains(0, 0, 0),
            EqSimpleMode.ReadOrZero(Array.Empty<EqBand>(), owned: false));
    }

    [Fact]
    public void There_is_no_room_for_macro_bands_in_an_almost_full_scope()
    {
        var full = Enumerable.Range(0, EqPreset.MaxBands - 2)
            .Select(i => new EqBand(EqBandType.Peak, 100 + i, 1, 1)).ToArray();
        _out.WriteLine($"{full.Length} foreign bands, cap {EqPreset.MaxBands}");

        Assert.False(EqSimpleMode.HasRoom(full, owned: false));
        // A chain the sliders already own always has room — it needs no new bands.
        var owned = EqSimpleMode.Apply(
            full.Take(EqPreset.MaxBands - 3).ToArray(), owned: false, new MacroGains(1, 1, 1));
        Assert.True(EqSimpleMode.HasRoom(owned, owned: true));
    }

    [Fact]
    public void Apply_never_exceeds_the_band_cap()
    {
        var full = Enumerable.Range(0, EqPreset.MaxBands)
            .Select(i => new EqBand(EqBandType.Peak, 100 + i, 1, 1)).ToArray();

        var result = EqSimpleMode.Apply(full, owned: false, new MacroGains(6, 6, 6));
        _out.WriteLine($"{full.Length} -> {result.Count}");
        Assert.Equal(full, result); // no room: the chain is returned untouched, nothing dropped
    }

    [Fact]
    public void Ownership_survives_settings_persistence()
    {
        var path = Path.Combine(Path.GetTempPath(), "apo-macro-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var bands = EqSimpleMode.Apply(AutoEqImport, owned: false, new MacroGains(2, 0, -2));
            (Settings.Default with
            {
                GlobalEq = new EqScopeSetting("(custom)", 0, true, bands, MacroBands: true),
            }).Save(path);

            var scope = Settings.Load(path).GlobalEq!;
            _out.WriteLine($"MacroBands={scope.MacroBands} bands={scope.Bands!.Count}");
            Assert.True(scope.MacroBands);
            Assert.True(EqSimpleMode.TryRead(scope.Bands!, scope.MacroBands, out var gains));
            Assert.Equal(new MacroGains(2, 0, -2), gains);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_scope_saved_before_this_release_owns_no_macro_bands()
    {
        // Old settings.json has no MacroBands field, so an existing chain that happens to end in
        // the reserved shapes stays the user's own.
        var path = Path.Combine(Path.GetTempPath(), "apo-macro-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                {"Percent":50,"Muted":false,"GlobalEq":{"PresetName":"mine","PresetPreampDb":0,
                "Enabled":true,"Bands":[{"Type":"LowShelf","Fc":100,"GainDb":-5,"Q":0.7},
                {"Type":"Peak","Fc":1000,"GainDb":3,"Q":0.7},
                {"Type":"HighShelf","Fc":8000,"GainDb":-2,"Q":0.7}]}}
                """);
            var scope = Settings.Load(path).GlobalEq!;

            Assert.False(scope.MacroBands);
            Assert.Equal(LooksLikeMacroBands, scope.Bands);
            Assert.False(EqSimpleMode.OwnsMacroBands(scope.Bands!, scope.MacroBands));
        }
        finally
        {
            File.Delete(path);
        }
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

    /// <summary>The editor pins the resolved face on first open, which is what keeps this
    /// stable: a first-time user starts in Simple, moves a slider, and now HAS bands — without a
    /// stored choice the next open would resolve to Advanced they never asked for.</summary>
    [Fact]
    public void A_pinned_simple_choice_survives_the_user_creating_bands()
    {
        var afterFirstOpen = Settings.Default with { EqEditorMode = EqEditorModes.Simple };
        var afterSliderMove = afterFirstOpen with
        {
            GlobalEq = new EqScopeSetting(Bands:
                EqSimpleMode.Apply(Array.Empty<EqBand>(), owned: false, new MacroGains(4, 0, 0)),
                MacroBands: true),
        };
        Assert.Equal(EqEditorModes.Simple, EqEditorModes.Resolve(afterSliderMove));
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
