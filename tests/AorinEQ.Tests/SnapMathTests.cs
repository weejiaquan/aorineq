using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

public class SnapMathTests
{
    private readonly ITestOutputHelper _out;

    public SnapMathTests(ITestOutputHelper output) => _out = output;

    private void Check<T>(string what, T expected, T actual)
    {
        _out.WriteLine($"{what}: expected {expected}, actual {actual}");
        Assert.Equal(expected, actual);
    }

    private SnapResult Run(SnapRect moving, IReadOnlyList<SnapTarget> targets, double threshold)
    {
        var result = SnapMath.Snap(moving, targets, threshold);
        _out.WriteLine($"moving {moving} threshold {threshold} -> dx {result.OffsetX} dy {result.OffsetY} guides [{string.Join(", ", result.Guides)}]");
        return result;
    }

    [Fact]
    public void An_edge_near_the_artboard_edge_snaps_onto_it()
    {
        var result = Run(new SnapRect(2, 50, 10, 10), SnapMath.ArtboardTargets(300, 100), 4);
        Check("dX", -2, result.OffsetX);
        Assert.Contains(new SnapTarget(SnapAxis.X, 0, SnapSource.Artboard), result.Guides);
    }

    [Fact]
    public void A_centre_near_the_artboard_centre_snaps_onto_it()
    {
        var result = Run(new SnapRect(146, 2, 10, 10), SnapMath.ArtboardTargets(300, 100), 4);
        Check("dX", -1, result.OffsetX);   // centre 151 -> 150
        Check("dY", -2, result.OffsetY);   // top 2 -> 0
        Assert.Contains(new SnapTarget(SnapAxis.X, 150, SnapSource.Artboard), result.Guides);
    }

    [Fact]
    public void A_fill_line_is_a_target()
    {
        var result = Run(new SnapRect(118, 300, 0, 10), [new SnapTarget(SnapAxis.X, 120, SnapSource.Artboard)], 4);
        Check("dX", 2, result.OffsetX);
        Check("dY", 0, result.OffsetY);
    }

    [Fact]
    public void The_threshold_is_four_screen_pixels_converted_by_zoom_and_scale()
    {
        Check("ThresholdFor(4, 1)", 1.0, SnapMath.ThresholdFor(4, 1));
        Check("ThresholdFor(0.25, 1)", 16.0, SnapMath.ThresholdFor(0.25, 1));
        Check("ThresholdFor(1, 2)", 2.0, SnapMath.ThresholdFor(1, 2));
        var targets = SnapMath.ArtboardTargets(300, 100);
        var zoomedIn = Run(new SnapRect(2, 50, 10, 10), targets, SnapMath.ThresholdFor(4, 1));
        var zoomedOut = Run(new SnapRect(12, 50, 10, 10), targets, SnapMath.ThresholdFor(0.25, 1));
        Check("dX", 0, zoomedIn.OffsetX);     // 2 skin px is 8 screen px at 400 %: too far
        Check("dX", -12, zoomedOut.OffsetX);  // 12 skin px is 3 screen px at 25 %: snaps
    }

    [Fact]
    public void The_closest_target_wins()
    {
        var result = Run(new SnapRect(102, 0, 10, 10),
            [new SnapTarget(SnapAxis.X, 100, SnapSource.Element), new SnapTarget(SnapAxis.X, 103, SnapSource.Element)], 4);
        Check("dX", 1, result.OffsetX);
        Assert.Equal([new SnapTarget(SnapAxis.X, 103, SnapSource.Element)], result.Guides);
    }

    [Fact]
    public void A_tie_prefers_the_artboard_over_an_element()
    {
        var result = Run(new SnapRect(102, 0, 0, 0),
            [new SnapTarget(SnapAxis.X, 100, SnapSource.Element), new SnapTarget(SnapAxis.X, 104, SnapSource.Artboard)], 4);
        Check("dX", 2, result.OffsetX);
        Assert.Equal([new SnapTarget(SnapAxis.X, 104, SnapSource.Artboard)], result.Guides);
    }

    [Fact]
    public void Nothing_in_range_means_no_snap_and_no_guides()
    {
        var result = Run(new SnapRect(50, 50, 10, 10), SnapMath.ArtboardTargets(300, 200), 4);
        Check("dX", 0, result.OffsetX);
        Check("dY", 0, result.OffsetY);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void An_element_offers_its_edges_and_centres()
    {
        var targets = SnapMath.ElementTargets(new SnapRect(10, 20, 30, 40));
        _out.WriteLine(string.Join(", ", targets));
        Assert.Equal(new[]
        {
            new SnapTarget(SnapAxis.X, 10, SnapSource.Element), new SnapTarget(SnapAxis.X, 25, SnapSource.Element),
            new SnapTarget(SnapAxis.X, 40, SnapSource.Element), new SnapTarget(SnapAxis.Y, 20, SnapSource.Element),
            new SnapTarget(SnapAxis.Y, 40, SnapSource.Element), new SnapTarget(SnapAxis.Y, 60, SnapSource.Element),
        }, targets);
    }
}
