using System.Globalization;

namespace AorinEQ.Core;

/// <summary>A colour a skin (or a HUD widget, or the colour picker) wrote, parsed once for
/// everything that draws it: the WPF renderers, the GDI+ preview generator and the picker.
///
/// Accepts #RGB, #ARGB, #RRGGBB, #AARRGGBB (shorthand digits double, exactly as WPF's converter
/// does: #F0C is #FFFF00CC) and the named colours, minus the Windows SYSTEM colours ("Control"),
/// which WPF's converter does not know either - so a skin cannot render one way in the OSD and
/// another in its gallery preview. Anything else is null and the caller falls back; nothing here
/// throws on an author's string.</summary>
public readonly record struct SkinColor(byte A, byte R, byte G, byte B)
{
    /// <summary>How many recent colours the picker keeps.</summary>
    public const int RecentCapacity = 12;

    public static SkinColor? Parse(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        if (text[0] == '#')
        {
            var hex = text[1..];
            if (hex.Length is not (3 or 4 or 6 or 8)
                || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var raw))
                return null;
            return hex.Length switch
            {
                3 => Doubled(0xF, raw >> 8, raw >> 4, raw),
                4 => Doubled(raw >> 12, raw >> 8, raw >> 4, raw),
                6 => new SkinColor(0xFF, (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw),
                _ => new SkinColor((byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw),
            };
        }

        var named = System.Drawing.Color.FromName(text);
        return named.IsKnownColor && !named.IsSystemColor
            ? new SkinColor(named.A, named.R, named.G, named.B)
            : null;
    }

    /// <summary>#AARRGGBB, upper case - the one spelling the designer writes.</summary>
    public string ToHex() => string.Create(CultureInfo.InvariantCulture, $"#{A:X2}{R:X2}{G:X2}{B:X2}");

    /// <summary>What the hex box shows after a commit, or null when the input is not a colour.</summary>
    public static string? Format(string? value) => Parse(value)?.ToHex();

    /// <summary>The colour with its alpha forced to FF; input that is not a colour is returned as
    /// it is. A new notice border starts from the plate made opaque: the default plate is #F2, and
    /// a border at that alpha is a faint, see-through edge.</summary>
    public static string Opaque(string color) =>
        Parse(color) is { } c ? (c with { A = 0xFF }).ToHex() : color;

    /// <summary>Hue in degrees (any value, wrapped into 0-360), saturation and value 0-1.</summary>
    public static SkinColor FromHsv(double hue, double saturation, double value, byte alpha)
    {
        double h = (hue % 360 + 360) % 360;
        double s = Math.Clamp(saturation, 0, 1);
        double v = Math.Clamp(value, 0, 1);
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        var (r, g, b) = (h / 60) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new SkinColor(alpha, ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    public (double Hue, double Saturation, double Value) ToHsv()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * ((g - b) / d % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    /// <summary>The recents with <paramref name="color"/> moved to the front, de-duplicated
    /// case-insensitively and capped at <see cref="RecentCapacity"/>. A non-colour is ignored.</summary>
    public static IReadOnlyList<string> PushRecent(IReadOnlyList<string>? recents, string color)
    {
        var existing = NormalizeRecents(recents);
        if (Format(color) is not { } hex) return existing;
        return new[] { hex }
            .Concat(existing.Where(c => !string.Equals(c, hex, StringComparison.OrdinalIgnoreCase)))
            .Take(RecentCapacity)
            .ToArray();
    }

    /// <summary>Stored recents made trustworthy: settings.json is a file people edit.</summary>
    public static IReadOnlyList<string> NormalizeRecents(IReadOnlyList<string>? recents) =>
        (recents ?? [])
            .Select(Format)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(RecentCapacity)
            .ToArray();

    private static SkinColor Doubled(uint a, uint r, uint g, uint b) =>
        new((byte)((a & 0xF) * 17), (byte)((r & 0xF) * 17), (byte)((g & 0xF) * 17), (byte)((b & 0xF) * 17));

    private static byte ToByte(double unit) => (byte)Math.Round(unit * 255, MidpointRounding.AwayFromZero);
}
