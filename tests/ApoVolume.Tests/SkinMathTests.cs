using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

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
