namespace ApoVolume.Core;

/// <summary>Computes the OSD window's Left/Top within a monitor's work area for one of the 8 anchor positions.</summary>
public static class OsdPosition
{
    /// <summary>
    /// anchor: one of "top-left", "top-center", "top-right", "left-center", "right-center",
    /// "bottom-left", "bottom-center", "bottom-right". Returns Left,Top in the work-area coordinate space.
    /// </summary>
    public static (double Left, double Top) Compute(string anchor,
        double winW, double winH, double waLeft, double waTop, double waW, double waH,
        int offsetX, int offsetY, double margin = 12)
    {
        var (horizontal, vertical) = anchor switch
        {
            "top-left" => ("left", "top"),
            "top-center" => ("center", "top"),
            "top-right" => ("right", "top"),
            "left-center" => ("left", "center"),
            "right-center" => ("right", "center"),
            "bottom-left" => ("left", "bottom"),
            "bottom-center" => ("center", "bottom"),
            "bottom-right" => ("right", "bottom"),
            _ => throw new ArgumentException($"Unknown OSD anchor: '{anchor}'", nameof(anchor))
        };

        double left = horizontal switch
        {
            "left" => waLeft + margin,
            "center" => waLeft + (waW - winW) / 2,
            "right" => waLeft + waW - winW - margin,
            _ => throw new ArgumentException($"Unknown horizontal anchor: '{horizontal}'", nameof(anchor))
        };

        double top = vertical switch
        {
            "top" => waTop + margin,
            "center" => waTop + (waH - winH) / 2,
            "bottom" => waTop + waH - winH - margin,
            _ => throw new ArgumentException($"Unknown vertical anchor: '{vertical}'", nameof(anchor))
        };

        return (left + offsetX, top + offsetY);
    }
}
