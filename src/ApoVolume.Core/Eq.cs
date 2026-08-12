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
        string type = band.Type switch
        {
            EqBandType.Peak => "PK",
            EqBandType.LowShelf => "LSC",
            EqBandType.HighShelf => "HSC",
            EqBandType.Notch => "NO",
            EqBandType.LowPass => "LPQ",
            EqBandType.HighPass => "HPQ",
            _ => throw new ArgumentOutOfRangeException(nameof(band)),
        };
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
            if (TryParseFilterLine(line) is { } band)
                bands.Add(band);
        }
        return new EqPreset(name, preamp, bands);
    }

    private static EqBand? TryParseFilterLine(string line)
    {
        if (!line.StartsWith("Filter", StringComparison.OrdinalIgnoreCase))
            return null;
        int colon = line.IndexOf(':');
        if (colon < 0)
            return null;
        var tokens = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
            return null;
        if (!tokens[0].Equals("ON", StringComparison.OrdinalIgnoreCase))
            return null; // OFF (or malformed) — a disabled filter must not shape the chain
        EqBandType? type = tokens[1].ToUpperInvariant() switch
        {
            "PK" or "PEQ" => EqBandType.Peak,
            "LS" or "LSC" => EqBandType.LowShelf,
            "HS" or "HSC" => EqBandType.HighShelf,
            "NO" => EqBandType.Notch,
            "LP" or "LPQ" => EqBandType.LowPass,
            "HP" or "HPQ" => EqBandType.HighPass,
            _ => null,
        };
        if (type is null)
            return null;

        double? fc = null, gain = null, q = null;
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
                    // "BW Oct <n>": bandwidth in octaves; RBJ conversion to Q.
                    if (i + 2 < tokens.Length && tokens[i + 1].Equals("Oct", StringComparison.OrdinalIgnoreCase)
                        && ReadNumber(tokens[i + 2]) is { } bw && bw > 0)
                        q ??= 1.0 / (2.0 * Math.Sinh(Math.Log(2) / 2.0 * bw));
                    break;
            }
        }
        if (fc is null)
            return null; // Fc is required for every supported type
        double defaultQ = type == EqBandType.Notch ? DefaultNotchQ : DefaultQ;
        return Clamp(new EqBand(type.Value, fc.Value, gain ?? 0, q ?? defaultQ));
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
