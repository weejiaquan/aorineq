using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

public class SkinMathTests
{
    private readonly ITestOutputHelper _out;
    public SkinMathTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(300, 0, 0)]
    [InlineData(300, 50, 150)]
    [InlineData(300, 100, 300)]
    [InlineData(300, -20, 0)]    // clamps below 0
    [InlineData(300, 150, 300)]  // clamps above 100
    [InlineData(301, 50, 151)]   // rounds (150.5 -> 151, away from zero)
    public void FillWidth_clamps_percent_and_rounds(int imageWidth, int percent, int expected)
    {
        var result = SkinMath.FillWidth(imageWidth, percent);
        _out.WriteLine($"FillWidth({imageWidth}, {percent}) = {result} (expected {expected})");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0.0, 300.0, 0)]
    [InlineData(150.0, 300.0, 50)]
    [InlineData(300.0, 300.0, 100)]
    [InlineData(-50.0, 300.0, 0)]    // clamps below 0
    [InlineData(400.0, 300.0, 100)]  // clamps above 100
    public void PercentFromX_clamps_and_rounds(double x, double width, int expected)
    {
        var result = SkinMath.PercentFromX(x, width);
        _out.WriteLine($"PercentFromX({x}, {width}) = {result} (expected {expected})");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, 120, 680, 120)]     // 0% sits at the bar's left edge
    [InlineData(50, 120, 680, 400)]    // midpoint of the range
    [InlineData(100, 120, 680, 680)]   // 100% sits at the bar's right edge
    [InlineData(-5, 120, 680, 120)]    // percent clamps low
    [InlineData(150, 120, 680, 680)]   // percent clamps high
    [InlineData(25, 0, 300, 75)]       // full-width range behaves like the classic overload
    public void FillWidth_with_range_maps_percent_onto_the_bar(int percent, int start, int end, int expected)
    {
        var result = SkinMath.FillWidth(800, percent, start, end);
        _out.WriteLine($"FillWidth(800, {percent}, {start}, {end}) = {result} (expected {expected})");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(120.0, 120, 680, 0)]    // click at the bar's left edge -> 0
    [InlineData(400.0, 120, 680, 50)]   // middle of the bar -> 50
    [InlineData(680.0, 120, 680, 100)]  // right edge -> 100
    [InlineData(10.0, 120, 680, 0)]     // decorative margin left of the bar clamps to 0
    [InlineData(790.0, 120, 680, 100)]  // margin right of the bar clamps to 100
    [InlineData(100.0, 100, 100, 0)]    // degenerate range never divides by zero
    public void PercentFromX_with_range_maps_bar_pixels_to_percent(double x, int start, int end, int expected)
    {
        var result = SkinMath.PercentFromX(x, start, end);
        _out.WriteLine($"PercentFromX({x}, {start}, {end}) = {result} (expected {expected})");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(100.0, 40.0, "left", 100.0)]    // left: x IS the left edge
    [InlineData(100.0, 40.0, "center", 80.0)]   // center: x is the midpoint
    [InlineData(100.0, 40.0, "right", 60.0)]    // right: x is the right edge
    [InlineData(100.0, 0.0, "center", 100.0)]   // zero width: all alignments collapse to x
    [InlineData(100.0, 0.0, "right", 100.0)]
    [InlineData(100.0, 40.0, "banana", 100.0)]  // unknown align falls back to left
    [InlineData(0.0, 30.0, "right", -30.0)]     // anchors may push the left edge negative
    public void AlignedTextX_maps_anchor_to_left_edge(double x, double textWidth, string align, double expected)
    {
        var result = SkinMath.AlignedTextX(x, textWidth, align);
        _out.WriteLine($"AlignedTextX({x}, {textWidth}, '{align}') = {result} (expected {expected})");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(10, 10, false)] // exactly at default threshold -> not opaque
    [InlineData(11, 10, true)]  // one above default threshold -> opaque
    [InlineData(0, 10, false)]
    [InlineData(255, 10, true)]
    [InlineData(5, 5, false)]   // exactly at custom threshold -> not opaque
    [InlineData(6, 5, true)]    // one above custom threshold -> opaque
    public void IsOpaque_boundary_is_strictly_greater_than_threshold(byte alpha, byte threshold, bool expected)
    {
        var result = SkinMath.IsOpaque(alpha, threshold);
        _out.WriteLine($"IsOpaque(alpha={alpha}, threshold={threshold}) = {result} (expected {expected})");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsOpaque_default_threshold_is_10()
    {
        var atDefault = SkinMath.IsOpaque(10);
        var aboveDefault = SkinMath.IsOpaque(11);
        _out.WriteLine($"IsOpaque(10) default = {atDefault}; IsOpaque(11) default = {aboveDefault}");
        Assert.False(atDefault);
        Assert.True(aboveDefault);
    }
}
