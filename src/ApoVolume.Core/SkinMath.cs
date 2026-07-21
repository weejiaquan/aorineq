namespace ApoVolume.Core;

/// <summary>Pixel-fill and hit-test math for skin rendering.</summary>
public static class SkinMath
{
    /// <summary>Width in pixels of the "full" overlay to draw over the "empty" image, for the given percent (0-100, clamped).</summary>
    public static int FillWidth(int imageWidth, int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        return (int)Math.Round(imageWidth * (clamped / 100.0), MidpointRounding.AwayFromZero);
    }

    /// <summary>Percent (0-100, clamped) corresponding to an x-coordinate within a width, e.g. for click-to-set.</summary>
    public static int PercentFromX(double x, double width)
    {
        double raw = width > 0 ? x / width * 100.0 : 0.0;
        int rounded = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, 0, 100);
    }

    /// <summary>Whether an alpha value counts as "opaque" (i.e. hit-testable) for skin hit-testing.</summary>
    public static bool IsOpaque(byte alpha, byte threshold = 10) => alpha > threshold;
}
