namespace AorinEQ.Core;

/// <summary>A rectangle in skin pixels. Core has no WPF, so the designer's canvas converts.</summary>
public readonly record struct SnapRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

/// <summary>X targets are vertical lines, Y targets horizontal ones.</summary>
public enum SnapAxis { X, Y }

/// <summary>Artboard lines (edges, centre, fill range) win ties against other elements' lines.</summary>
public enum SnapSource { Artboard, Element }

public sealed record SnapTarget(SnapAxis Axis, double Position, SnapSource Source);

public sealed record SnapResult(double OffsetX, double OffsetY, IReadOnlyList<SnapTarget> Guides);

/// <summary>Where a dragged element snaps: per axis, the moving rectangle's left/centre/right (or
/// top/centre/bottom) against every target on that axis; the closest within the threshold wins,
/// and a tie goes to the artboard. The threshold is 4 SCREEN pixels, so it stays the same size
/// under the cursor at any zoom.</summary>
public static class SnapMath
{
    public const double ScreenThreshold = 4;

    /// <summary><see cref="ScreenThreshold"/> in skin pixels at a zoom and a skin scale.</summary>
    public static double ThresholdFor(double zoom, double skinScale) => ScreenThreshold / (zoom * skinScale);

    public static IReadOnlyList<SnapTarget> ArtboardTargets(double width, double height) =>
    [
        new(SnapAxis.X, 0, SnapSource.Artboard), new(SnapAxis.X, width / 2, SnapSource.Artboard),
        new(SnapAxis.X, width, SnapSource.Artboard), new(SnapAxis.Y, 0, SnapSource.Artboard),
        new(SnapAxis.Y, height / 2, SnapSource.Artboard), new(SnapAxis.Y, height, SnapSource.Artboard),
    ];

    public static IReadOnlyList<SnapTarget> ElementTargets(SnapRect r) =>
    [
        new(SnapAxis.X, r.X, SnapSource.Element), new(SnapAxis.X, r.CenterX, SnapSource.Element),
        new(SnapAxis.X, r.Right, SnapSource.Element), new(SnapAxis.Y, r.Y, SnapSource.Element),
        new(SnapAxis.Y, r.CenterY, SnapSource.Element), new(SnapAxis.Y, r.Bottom, SnapSource.Element),
    ];

    public static SnapResult Snap(SnapRect moving, IReadOnlyList<SnapTarget> targets, double thresholdSkinPx)
    {
        var (dx, gx) = Axis([moving.X, moving.CenterX, moving.Right], targets, SnapAxis.X, thresholdSkinPx);
        var (dy, gy) = Axis([moving.Y, moving.CenterY, moving.Bottom], targets, SnapAxis.Y, thresholdSkinPx);
        return new SnapResult(dx, dy, [.. gx, .. gy]);
    }

    private static (double Offset, IReadOnlyList<SnapTarget> Guides) Axis(
        double[] edges, IReadOnlyList<SnapTarget> targets, SnapAxis axis, double threshold)
    {
        const double Epsilon = 1e-9;
        SnapTarget? best = null;
        double bestOffset = 0, bestDistance = double.MaxValue;
        foreach (var target in targets)
        {
            if (target.Axis != axis) continue;
            foreach (var edge in edges)
            {
                double offset = target.Position - edge, distance = Math.Abs(offset);
                if (distance > threshold + Epsilon) continue;
                bool closer = distance < bestDistance - Epsilon;
                bool tieWonByArtboard = Math.Abs(distance - bestDistance) <= Epsilon
                    && target.Source == SnapSource.Artboard && best?.Source == SnapSource.Element;
                if (!closer && !tieWonByArtboard) continue;
                best = target;
                bestOffset = offset;
                bestDistance = distance;
            }
        }
        return best is null ? (0, []) : (bestOffset, [best]);
    }
}
