namespace AorinEQ.Core;

/// <summary>The designer canvas's zoom: 25 %-800 %, stepped by the buttons and Ctrl+wheel through
/// the stops image editors use, fitted to the window by Ctrl+0, and anchored at the cursor.</summary>
public static class CanvasZoom
{
    public const double Min = 0.25;
    public const double Max = 8.0;

    public static readonly IReadOnlyList<double> Steps = [0.25, 0.33, 0.5, 0.67, 1, 1.5, 2, 3, 4, 6, 8];

    public static double Clamp(double zoom) => double.IsFinite(zoom) ? Math.Clamp(zoom, Min, Max) : 1.0;

    public static double In(double zoom) => Steps.FirstOrDefault(s => s > zoom + 1e-9, Max);

    public static double Out(double zoom) => Steps.LastOrDefault(s => s < zoom - 1e-9, Min);

    /// <summary>One wheel notch: positive deltas zoom in.</summary>
    public static double WheelZoom(double zoom, int delta) => delta > 0 ? In(zoom) : delta < 0 ? Out(zoom) : zoom;

    /// <summary>The zoom that shows the whole content in the viewport, clamped; 100 % when there is
    /// nothing to fit or nowhere to fit it.</summary>
    public static double Fit(double contentWidth, double contentHeight, double viewportWidth, double viewportHeight)
    {
        if (contentWidth <= 0 || contentHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0) return 1.0;
        return Clamp(Math.Min(viewportWidth / contentWidth, viewportHeight / contentHeight));
    }

    /// <summary>The scroll offset after a zoom change that keeps the content under the cursor
    /// (cursor in viewport pixels) where it was.</summary>
    public static double OffsetFor(double offset, double cursor, double oldZoom, double newZoom) =>
        Math.Max(0, (offset + cursor) * (newZoom / oldZoom) - cursor);
}
