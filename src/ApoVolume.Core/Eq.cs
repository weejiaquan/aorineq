using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace ApoVolume.Core;

/// <summary>Filter shapes in Equalizer APO's parametric-filter vocabulary. Serialized tokens:
/// PK, LSC, HSC, NO, LPQ, HPQ (canonical forms carrying an explicit Q; the Q-less aliases
/// LS/HS/LP/HP/PEQ are accepted on parse). Persisted to settings.json by name (readable, and
/// an unknown name fails the whole load safely instead of aliasing to a random filter type).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EqBandType>))]
public enum EqBandType
{
    Peak,
    LowShelf,
    HighShelf,
    Notch,
    LowPass,
    HighPass,
}

/// <summary>One parametric band. Gain is meaningful for Peak/LowShelf/HighShelf only —
/// EAPO's grammar has no Gain token for NO/LP/HP and the serializer omits it. Pure value
/// object: range clamping happens at the boundaries (<see cref="EqPreset.Parse"/>, editor
/// input), not here.</summary>
public sealed record EqBand(EqBandType Type, double Fc, double GainDb, double Q)
{
    /// <summary>Derived from <see cref="Type"/>, never persisted — a stored copy could drift
    /// from the type it describes (settings.json is rewritten by every version).</summary>
    [JsonIgnore]
    public bool HasGain => Type is EqBandType.Peak or EqBandType.LowShelf or EqBandType.HighShelf;
}

/// <summary>A named EQ chain in the Equalizer APO ParametricEQ text format — the exact format
/// AutoEq/Peace publish and EAPO consumes, so import/export is file copy. PreampDb is the
/// preset's own clipping-prevention preamp (AutoEq ships one), preserved on import and summed
/// with the device volume preamp at render time.</summary>
public sealed record EqPreset(string Name, double PreampDb, IReadOnlyList<EqBand> Bands)
{
    public const double MinFc = 10, MaxFc = 24000;
    public const double MaxGainDb = 30;
    public const double MinQ = 0.1, MaxQ = 50;
    public const double MinPreampDb = -60, MaxPreampDb = 20;

    /// <summary>Q for LS/HS/LP/HP lines that carry none: RBJ S=1 shelf / Butterworth.</summary>
    public const double DefaultQ = 0.707;
    /// <summary>Q for NO lines that carry none — the conventional narrow notch.</summary>
    public const double DefaultNotchQ = 30.0;

    /// <summary>The name a scope carries when its chain doesn't match any saved preset file —
    /// shown in the editor's preset box and never written to disk as a preset name.</summary>
    public const string CustomName = "(custom)";

    /// <summary>The canonical Equalizer APO token for a band type (the Q-carrying forms).</summary>
    public static string TypeToken(EqBandType type) => type switch
    {
        EqBandType.Peak => "PK",
        EqBandType.LowShelf => "LSC",
        EqBandType.HighShelf => "HSC",
        EqBandType.Notch => "NO",
        EqBandType.LowPass => "LPQ",
        EqBandType.HighPass => "HPQ",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The band type a filter token names, or null when it isn't one we support. The
    /// Q-less aliases (LS/HS/LP/HP/PEQ) are accepted; case is ignored.</summary>
    public static EqBandType? ParseTypeToken(string token) => token.ToUpperInvariant() switch
    {
        "PK" or "PEQ" => EqBandType.Peak,
        "LS" or "LSC" => EqBandType.LowShelf,
        "HS" or "HSC" => EqBandType.HighShelf,
        "NO" => EqBandType.Notch,
        "LP" or "LPQ" => EqBandType.LowPass,
        "HP" or "HPQ" => EqBandType.HighPass,
        _ => null,
    };

    /// <summary>EAPO ParametricEQ text: a Preamp line (only when non-zero) followed by
    /// numbered Filter lines. Invariant culture; Gain 1 decimal, Q 2 decimals, Fc up to 2
    /// decimals with trailing zeros trimmed (AutoEq's own shapes round-trip byte-for-byte).</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        if (PreampDb != 0)
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"Preamp: {PreampDb:0.0} dB")).Append('\n');
        for (int i = 0; i < Bands.Count; i++)
            sb.Append(FormatFilterLine(i + 1, Bands[i])).Append('\n');
        return sb.ToString();
    }

    public static string FormatFilterLine(int number, EqBand band)
    {
        var inv = CultureInfo.InvariantCulture;
        string type = TypeToken(band.Type);
        string gain = band.HasGain ? string.Create(inv, $" Gain {band.GainDb:0.0} dB") : "";
        return string.Create(inv, $"Filter {number}: ON {type} Fc {band.Fc:0.##} Hz{gain} Q {band.Q:0.00}");
    }

    /// <summary>Parses ParametricEQ text tolerantly: comments (#), blank lines, and
    /// unrecognized lines are skipped; OFF filters are skipped (they're disabled); unknown
    /// filter types are skipped; missing Q gets the type's conventional default; "BW Oct" is
    /// converted to Q (RBJ 1/Q = 2·sinh(ln2/2·BW)); every value is clamped to sane ranges so a
    /// hostile file can't smuggle absurd parameters. Never throws on content.</summary>
    public static EqPreset Parse(string name, string text)
    {
        double preamp = 0;
        var bands = new List<EqBand>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("Preamp:", StringComparison.OrdinalIgnoreCase))
            {
                if (TryReadNumberAfter(line, "Preamp:", out double p))
                    preamp = Math.Clamp(p, MinPreampDb, MaxPreampDb);
                continue;
            }
            if (ParseFilterLine(line).Band is { } band)
                bands.Add(band);
        }
        return new EqPreset(name, preamp, bands);
    }

    /// <summary>Outcome of one filter line: a band, a skip (disabled filter), or an error
    /// explaining what is wrong — the tolerant <see cref="Parse"/> ignores the error, the
    /// strict <see cref="TryParse"/> surfaces it. One implementation, two policies.</summary>
    private readonly record struct FilterLineResult(EqBand? Band, string? Error);

    private static FilterLineResult ParseFilterLine(string line)
    {
        if (!line.StartsWith("Filter", StringComparison.OrdinalIgnoreCase))
            return new FilterLineResult(null, "expected a 'Filter …' or 'Preamp: …' line.");
        int colon = line.IndexOf(':');
        if (colon < 0)
            return new FilterLineResult(null, "missing ':' after the filter number.");
        var tokens = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return new FilterLineResult(null, "expected 'ON <type> Fc <freq> Hz …' after the colon.");
        // A disabled filter is valid syntax that shapes nothing. Checked BEFORE the parameter
        // count, because a disabled line legitimately carries nothing else ("Filter 3: None").
        if (!tokens[0].Equals("ON", StringComparison.OrdinalIgnoreCase))
        {
            return tokens[0].Equals("OFF", StringComparison.OrdinalIgnoreCase)
                || tokens[0].Equals("None", StringComparison.OrdinalIgnoreCase)
                ? new FilterLineResult(null, null)
                : new FilterLineResult(null, $"expected ON, OFF or None, found '{tokens[0]}'.");
        }
        if (tokens.Length < 2)
            return new FilterLineResult(null, "expected a filter type after ON.");
        EqBandType? type = ParseTypeToken(tokens[1]);
        if (type is null)
            return new FilterLineResult(null,
                $"unsupported filter type '{tokens[1]}' (supported: PK, LSC, HSC, NO, LPQ, HPQ).");

        double? fc = null, gain = null, q = null, bwOct = null;
        for (int i = 2; i < tokens.Length - 1; i++)
        {
            switch (tokens[i].ToUpperInvariant())
            {
                case "FC":
                    fc ??= ReadNumber(tokens[i + 1]);
                    break;
                case "GAIN":
                    gain ??= ReadNumber(tokens[i + 1]);
                    break;
                case "Q":
                    q ??= ReadNumber(tokens[i + 1]);
                    break;
                case "BW":
                    if (i + 2 < tokens.Length && tokens[i + 1].Equals("Oct", StringComparison.OrdinalIgnoreCase))
                        bwOct ??= ReadNumber(tokens[i + 2]);
                    break;
            }
        }
        if (fc is null)
            return new FilterLineResult(null, "missing 'Fc <frequency> Hz'.");
        if (q is null && bwOct is { } bw && bw > 0)
        {
            // Full RBJ bandwidth-to-Q: 1/Q = 2·sinh(ln2/2 · BW · ω0/sin ω0). The ω0/sin ω0
            // term needs Fc, which is why the conversion happens after the token scan.
            double w0 = 2 * Math.PI * Math.Clamp(fc.Value, MinFc, MaxFc) / EqResponse.SampleRate;
            q = 1.0 / (2.0 * Math.Sinh(Math.Log(2) / 2.0 * bw * w0 / Math.Sin(w0)));
        }
        double defaultQ = type == EqBandType.Notch ? DefaultNotchQ : DefaultQ;
        return new FilterLineResult(
            Clamp(new EqBand(type.Value, fc.Value, gain ?? 0, q ?? defaultQ)), null);
    }

    /// <summary>Strict counterpart of <see cref="Parse"/> for hand-typed/pasted text (the
    /// editor's "Edit as text" dialog): the SAME line parser, but the first unusable line
    /// fails the whole parse with a 1-based line number and reason, so nothing is ever
    /// partially applied. Blank lines, '#' comments, CRLF and disabled (OFF/None) filters are
    /// accepted exactly as the tolerant path accepts them.</summary>
    public static bool TryParse(string name, string text, out EqPreset preset, out string? error)
    {
        double preamp = 0;
        var bands = new List<EqBand>();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("Preamp:", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadNumberAfter(line, "Preamp:", out double p))
                {
                    preset = new EqPreset(name, 0, Array.Empty<EqBand>());
                    error = $"Line {i + 1}: expected a number after 'Preamp:'.";
                    return false;
                }
                preamp = Math.Clamp(p, MinPreampDb, MaxPreampDb);
                continue;
            }
            var result = ParseFilterLine(line);
            if (result.Error is { } lineError)
            {
                preset = new EqPreset(name, 0, Array.Empty<EqBand>());
                error = $"Line {i + 1}: {lineError}";
                return false;
            }
            if (result.Band is { } band)
                bands.Add(band);
        }
        // The cap the editor enforces on every other entry point applies here too: a pasted or
        // downloaded block longer than a scope can hold is refused whole rather than truncated.
        if (bands.Count > MaxBands)
        {
            preset = new EqPreset(name, 0, Array.Empty<EqBand>());
            error = $"Too many filters ({bands.Count}); the limit is {MaxBands} per scope.";
            return false;
        }
        preset = new EqPreset(name, preamp, bands);
        error = null;
        return true;
    }

    /// <summary>Every band with its gain zeroed, keeping type/Fc/Q and band count — the
    /// editor's Flatten action (a flat response that keeps the chain's shape for re-editing).</summary>
    public static IReadOnlyList<EqBand> Flatten(IReadOnlyList<EqBand> bands) =>
        bands.Select(b => b with { GainDb = 0 }).ToArray();

    /// <summary>Upper bound on bands in one scope. There is no musical reason for a specific
    /// number — this only stops a runaway paste from building a chain that would bog down the
    /// response redraw (and that Equalizer APO would have to evaluate per sample).</summary>
    public const int MaxBands = 64;

    /// <summary>What a new band starts as when the editor's "+" appends one.</summary>
    public static EqBand NewBand() => new(EqBandType.Peak, 1000, 0, 1.41);

    /// <summary>Appends <paramref name="band"/> unless the scope is already at
    /// <see cref="MaxBands"/>. False (with the list untouched) when the cap is reached.</summary>
    public static bool TryAppend(IList<EqBand> bands, EqBand band)
    {
        if (bands.Count >= MaxBands)
            return false;
        bands.Add(Clamp(band));
        return true;
    }

    /// <summary>Clamps a band's parameters into the supported ranges — the shared boundary
    /// guard for parsed files and persisted settings (both are external input).</summary>
    public static EqBand Clamp(EqBand band) => band with
    {
        Fc = Math.Clamp(double.IsFinite(band.Fc) ? band.Fc : 1000, MinFc, MaxFc),
        GainDb = Math.Clamp(double.IsFinite(band.GainDb) ? band.GainDb : 0, -MaxGainDb, MaxGainDb),
        Q = Math.Clamp(double.IsFinite(band.Q) ? band.Q : DefaultQ, MinQ, MaxQ),
    };

    private static double? ReadNumber(string token) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
        && double.IsFinite(v) ? v : null;

    private static bool TryReadNumberAfter(string line, string prefix, out double value)
    {
        value = 0;
        var rest = line[prefix.Length..].Trim();
        int space = rest.IndexOf(' ');
        var numberToken = space >= 0 ? rest[..space] : rest;
        if (ReadNumber(numberToken) is not { } v)
            return false;
        value = v;
        return true;
    }
}

/// <summary>The editable numeric fields of a band, for <see cref="EqFieldInput.Apply"/>.</summary>
public enum EqBandField
{
    Fc,
    GainDb,
    Q,
}

/// <summary>What happened to a typed field value.</summary>
public enum EqFieldOutcome
{
    /// <summary>Taken as typed.</summary>
    Applied,
    /// <summary>Parsed, but outside the supported range and pulled to the nearest limit.</summary>
    Clamped,
    /// <summary>Not a number at all — the previous value was kept.</summary>
    Reverted,
}

/// <summary>Applies text typed into a band's numeric field, with one shared policy for every
/// entry point in the editor (the numeric side panel and the per-band strip): unparseable
/// input keeps the previous value, and a parseable but out-of-range value is pulled to the
/// model's own limits rather than silently accepted. Callers show the outcome as an inline
/// cue, so nothing changes behind the user's back.</summary>
public static class EqFieldInput
{
    public static EqBand Apply(EqBand band, EqBandField field, string text, out EqFieldOutcome outcome)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
        {
            outcome = EqFieldOutcome.Reverted;
            return band;
        }
        var updated = field switch
        {
            EqBandField.Fc => band with { Fc = value },
            EqBandField.GainDb => band with { GainDb = value },
            _ => band with { Q = value },
        };
        var clamped = EqPreset.Clamp(updated);
        outcome = clamped == updated ? EqFieldOutcome.Applied : EqFieldOutcome.Clamped;
        return clamped;
    }
}
