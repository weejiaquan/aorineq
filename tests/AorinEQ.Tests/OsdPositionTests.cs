using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

public class OsdPositionTests
{
    private readonly ITestOutputHelper _out;
    public OsdPositionTests(ITestOutputHelper output) => _out = output;

    // Known work area, shared by every anchor case below.
    private const double WaLeft = 100, WaTop = 50, WaW = 1920, WaH = 1040;
    private const double WinW = 300, WinH = 80;

    [Theory]
    [InlineData("top-left", 112, 62)]
    [InlineData("top-center", 910, 62)]
    [InlineData("top-right", 1708, 62)]
    [InlineData("left-center", 112, 530)]
    [InlineData("right-center", 1708, 530)]
    [InlineData("bottom-left", 112, 998)]
    [InlineData("bottom-center", 910, 998)]
    [InlineData("bottom-right", 1708, 998)]
    public void Compute_positions_all_8_anchors_with_default_margin_and_no_offset(string anchor, double expectedLeft, double expectedTop)
    {
        var (left, top) = OsdPosition.Compute(anchor, WinW, WinH, WaLeft, WaTop, WaW, WaH, offsetX: 0, offsetY: 0);
        _out.WriteLine($"anchor={anchor}: computed=({left},{top}) expected=({expectedLeft},{expectedTop})");

        Assert.Equal(expectedLeft, left);
        Assert.Equal(expectedTop, top);
    }

    [Theory]
    [InlineData("top-left", 112 + 5, 62 - 3)]
    [InlineData("top-center", 910 + 5, 62 - 3)]
    [InlineData("top-right", 1708 + 5, 62 - 3)]
    [InlineData("left-center", 112 + 5, 530 - 3)]
    [InlineData("right-center", 1708 + 5, 530 - 3)]
    [InlineData("bottom-left", 112 + 5, 998 - 3)]
    [InlineData("bottom-center", 910 + 5, 998 - 3)]
    [InlineData("bottom-right", 1708 + 5, 998 - 3)]
    public void Compute_applies_offsets_after_anchoring_for_all_8_anchors(string anchor, double expectedLeft, double expectedTop)
    {
        var (left, top) = OsdPosition.Compute(anchor, WinW, WinH, WaLeft, WaTop, WaW, WaH, offsetX: 5, offsetY: -3);
        _out.WriteLine($"anchor={anchor} with offset(5,-3): computed=({left},{top}) expected=({expectedLeft},{expectedTop})");

        Assert.Equal(expectedLeft, left);
        Assert.Equal(expectedTop, top);
    }

    [Fact]
    public void Compute_uses_custom_margin_instead_of_default_12()
    {
        var (left, top) = OsdPosition.Compute("top-left", WinW, WinH, WaLeft, WaTop, WaW, WaH, offsetX: 0, offsetY: 0, margin: 30);
        _out.WriteLine($"top-left with margin=30: computed=({left},{top}) expected=({WaLeft + 30},{WaTop + 30})");

        Assert.Equal(WaLeft + 30, left);
        Assert.Equal(WaTop + 30, top);
    }

    [Fact]
    public void Compute_center_anchors_average_correctly_with_odd_leftover_space()
    {
        // Work area / window sizes that don't divide evenly, to confirm center math isn't
        // silently truncating in a way that only happens to look right on round numbers.
        var (left, top) = OsdPosition.Compute("top-center", 301, 81, 0, 0, 1921, 1041, offsetX: 0, offsetY: 0);
        double expectedLeft = (1921 - 301) / 2.0;
        double expectedTop = 12; // top anchor still uses margin
        _out.WriteLine($"uneven center case: computed=({left},{top}) expected=({expectedLeft},{expectedTop})");

        Assert.Equal(expectedLeft, left);
        Assert.Equal(expectedTop, top);
    }

    [Fact]
    public void Compute_throws_for_unknown_anchor()
    {
        var ex = Record.Exception(() => OsdPosition.Compute("nonsense", WinW, WinH, WaLeft, WaTop, WaW, WaH, 0, 0));
        _out.WriteLine($"unknown anchor exception: {ex?.GetType().Name}: {ex?.Message}");
        Assert.IsType<ArgumentException>(ex);
    }
}
