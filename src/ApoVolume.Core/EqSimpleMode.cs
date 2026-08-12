namespace ApoVolume.Core;

/// <summary>Which face the EQ editor shows. Strings (not an enum) so the persisted json stays
/// readable, like <see cref="VolumeModes"/>. The empty string means "never chosen" and resolves
/// from what the user already has.</summary>
public static class EqEditorModes
{
    public const string Unset = "";
    public const string Simple = "simple";
    public const string Advanced = "advanced";

    /// <summary>Unknown values normalize to "never chosen" rather than to a fixed face, so the
    /// resolution below still gets to look at the user's own chains.</summary>
    public static string Normalize(string? mode) =>
        mode is Simple or Advanced ? mode : Unset;

    /// <summary>The face to open with. A stored choice always wins. Otherwise: anyone who
    /// already has bands configured was using the full editor before this existed and keeps it;
    /// a first-time EQ user gets the three sliders.</summary>
    public static string Resolve(Settings settings)
    {
        var stored = Normalize(settings.EqEditorMode);
        if (stored != Unset)
            return stored;
        bool hasBands = settings.GlobalEq?.Bands is { Count: > 0 }
            || (settings.DeviceEq?.Values.Any(s => s.Bands is { Count: > 0 }) ?? false);
        return hasBands ? Advanced : Simple;
    }
}

/// <summary>The three macro gains Simple mode exposes, in dB.</summary>
public readonly record struct MacroGains(double BassDb, double MidDb, double TrebleDb);

/// <summary>Simple mode's bass/mid/treble sliders, expressed in the SAME band model the full
/// editor edits — there is no parallel state and nothing to reconcile. The sliders own three
/// reserved bands (low shelf 100 Hz, peak 1 kHz, high shelf 8 kHz, all Q 0.7) that live at the
/// END of a scope's chain; anything in front of them is somebody else's (an AutoEq import, a
/// hand-built chain) and is never touched.
///
/// The trio is identified by its reserved SHAPE and position, not by a marker: apo-volume.txt
/// stays pure Equalizer APO syntax, with no comments this app would have to defend. The cost is
/// that editing a macro band in Advanced mode turns it into an ordinary band — which is exactly
/// the honest outcome, since it no longer is what the slider claims to control.</summary>
public static class EqSimpleMode
{
    /// <summary>Slider travel. Wide enough to be worth a slider, narrow enough that a first-time
    /// user can't wreck their audio with one drag.</summary>
    public const double MaxGainDb = 12;

    /// <summary>The reserved shapes, in slider order (bass, mid, treble), at 0 dB. Every value
    /// is exactly representable in the ParametricEQ text format, so a chain still detects after
    /// a round trip through apo-volume.txt.</summary>
    public static readonly IReadOnlyList<EqBand> Shapes = new[]
    {
        new EqBand(EqBandType.LowShelf, 100, 0, 0.7),
        new EqBand(EqBandType.Peak, 1000, 0, 0.7),
        new EqBand(EqBandType.HighShelf, 8000, 0, 0.7),
    };

    /// <summary>Reads the macro gains off a chain that ends in the reserved trio.</summary>
    public static bool TryRead(IReadOnlyList<EqBand> bands, out MacroGains gains)
    {
        gains = default;
        if (!EndsWithMacroTrio(bands))
            return false;
        int start = bands.Count - Shapes.Count;
        gains = new MacroGains(bands[start].GainDb, bands[start + 1].GainDb, bands[start + 2].GainDb);
        return true;
    }

    /// <summary>The macro gains, or all-flat when this chain has no macro trio yet — what Simple
    /// mode shows on its sliders the first time it opens on a scope.</summary>
    public static MacroGains ReadOrZero(IReadOnlyList<EqBand> bands) =>
        TryRead(bands, out var gains) ? gains : new MacroGains(0, 0, 0);

    /// <summary>Everything in the chain that isn't the macro trio — the bands Simple mode leaves
    /// alone and tells the user about.</summary>
    public static IReadOnlyList<EqBand> ForeignBands(IReadOnlyList<EqBand> bands) =>
        EndsWithMacroTrio(bands)
            ? bands.Take(bands.Count - Shapes.Count).ToArray()
            : bands.ToArray();

    public static bool HasForeignBands(IReadOnlyList<EqBand> bands) => ForeignBands(bands).Count > 0;

    /// <summary>Whether the macro trio fits: it always does once the chain already ends in it,
    /// and otherwise only if three more bands stay inside <see cref="EqPreset.MaxBands"/>.</summary>
    public static bool HasRoom(IReadOnlyList<EqBand> bands) =>
        ForeignBands(bands).Count + Shapes.Count <= EqPreset.MaxBands;

    /// <summary>The chain with the macro trio set to <paramref name="gains"/>: foreign bands
    /// first, exactly as they were, then the three reserved bands. Idempotent, so dragging a
    /// slider replaces gains instead of stacking bands. A chain with no room for the trio is
    /// returned untouched — nothing is ever dropped to make space.</summary>
    public static IReadOnlyList<EqBand> Apply(IReadOnlyList<EqBand> bands, MacroGains gains)
    {
        if (!HasRoom(bands))
            return bands.ToArray();
        var result = new List<EqBand>(ForeignBands(bands));
        double[] values = { gains.BassDb, gains.MidDb, gains.TrebleDb };
        for (int i = 0; i < Shapes.Count; i++)
            result.Add(Shapes[i] with { GainDb = ClampGain(values[i]) });
        return result;
    }

    private static double ClampGain(double db) =>
        double.IsFinite(db) ? Math.Clamp(db, -MaxGainDb, MaxGainDb) : 0;

    private static bool EndsWithMacroTrio(IReadOnlyList<EqBand> bands)
    {
        if (bands.Count < Shapes.Count)
            return false;
        int start = bands.Count - Shapes.Count;
        for (int i = 0; i < Shapes.Count; i++)
        {
            if (!IsShape(bands[start + i], Shapes[i]))
                return false;
        }
        return true;
    }

    /// <summary>Shape match: type, centre frequency and Q, ignoring gain (which is the whole
    /// point of the slider). The tolerance absorbs the text format's rounding without being wide
    /// enough to capture a band the user deliberately moved.</summary>
    private static bool IsShape(EqBand band, EqBand shape) =>
        band.Type == shape.Type
        && Math.Abs(band.Fc - shape.Fc) < 0.005
        && Math.Abs(band.Q - shape.Q) < 0.005;
}
