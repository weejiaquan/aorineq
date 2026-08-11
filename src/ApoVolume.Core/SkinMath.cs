namespace ApoVolume.Core;

/// <summary>Pixel-fill and hit-test math for skin rendering.</summary>
public static class SkinMath
{
    /// <summary>Width in pixels of the "full" overlay to draw over the "empty" image, for the given percent (0-100, clamped).</summary>
    public static int FillWidth(int imageWidth, int percent) =>
        FillWidth(imageWidth, percent, 0, imageWidth);

    /// <summary>Range-aware fill: 0% clips at <paramref name="fillStartX"/>, 100% at
    /// <paramref name="fillEndX"/> — for skins whose bar occupies only part of a wider image.
    /// full.png should paint only the bar's lit pixels inside that range (static decoration
    /// belongs in empty.png), making the mapping pixel-exact at both ends.</summary>
    public static int FillWidth(int imageWidth, int percent, int fillStartX, int fillEndX)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        double width = fillStartX + (fillEndX - fillStartX) * (clamped / 100.0);
        return (int)Math.Round(width, MidpointRounding.AwayFromZero);
    }

    /// <summary>Percent (0-100, clamped) corresponding to an x-coordinate within a width, e.g. for click-to-set.</summary>
    public static int PercentFromX(double x, double width) => PercentFromX(x, 0.0, width);

    /// <summary>Range-aware inverse of <see cref="FillWidth(int,int,int,int)"/>: maps
    /// [<paramref name="fillStartX"/>..<paramref name="fillEndX"/>] to 0..100; clicks in the
    /// decorative margins clamp to the nearest end. A degenerate range reads as 0.</summary>
    public static int PercentFromX(double x, double fillStartX, double fillEndX)
    {
        double range = fillEndX - fillStartX;
        double raw = range > 0 ? (x - fillStartX) / range * 100.0 : 0.0;
        int rounded = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, 0, 100);
    }

    /// <summary>Whether an alpha value counts as "opaque" (i.e. hit-testable) for skin hit-testing.</summary>
    public static bool IsOpaque(byte alpha, byte threshold = 10) => alpha > threshold;

    /// <summary>Left edge of the percent text when <paramref name="x"/> is its ANCHOR under the
    /// given alignment: left → x, center → x − width/2, right → x − width. Anything but
    /// center/right reads as left, matching <c>SkinLoader</c>'s align normalization.</summary>
    public static double AlignedTextX(double x, double textWidth, string align) => align switch
    {
        "center" => x - textWidth / 2,
        "right" => x - textWidth,
        _ => x,
    };
}
