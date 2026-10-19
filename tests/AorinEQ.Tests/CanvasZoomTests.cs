using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

public class CanvasZoomTests
{
    private readonly ITestOutputHelper _out;

    public CanvasZoomTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(0.1, 0.25)]
    [InlineData(9.0, 8.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(double.NaN, 1.0)]
    public void Zoom_is_twenty_five_to_eight_hundred_percent(double input, double expected)
    {
        _out.WriteLine($"Clamp({input}): expected {expected}, actual {CanvasZoom.Clamp(input)}");
        Assert.Equal(expected, CanvasZoom.Clamp(input));
    }

    [Fact]
    public void Steps_go_in_and_out_and_stop_at_the_ends()
    {
        _out.WriteLine($"In(1)={CanvasZoom.In(1)} Out(1)={CanvasZoom.Out(1)} In(0.3)={CanvasZoom.In(0.3)} In(8)={CanvasZoom.In(8)} Out(0.25)={CanvasZoom.Out(0.25)}");
        Assert.Equal(1.5, CanvasZoom.In(1));
        Assert.Equal(0.67, CanvasZoom.Out(1));
        Assert.Equal(0.33, CanvasZoom.In(0.3));
        Assert.Equal(8.0, CanvasZoom.In(8));
        Assert.Equal(0.25, CanvasZoom.Out(0.25));
    }

    [Fact]
    public void Notches_step_one_stop_each_and_stop_at_the_ends()
    {
        var r = (In1: CanvasZoom.Stepped(2, 1), Out1: CanvasZoom.Stepped(2, -1), None: CanvasZoom.Stepped(2, 0),
            In3: CanvasZoom.Stepped(1, 3), Out2: CanvasZoom.Stepped(1, -2), PastMax: CanvasZoom.Stepped(4, 20), PastMin: CanvasZoom.Stepped(0.5, -20));
        _out.WriteLine($"expected (3, 1.5, 2, 3, 0.5, 8, 0.25); actual {r}");
        Assert.Equal((3.0, 1.5, 2.0, 3.0, 0.5, 8.0, 0.25), r);
    }

    /// <summary>Review focus: a 1×1 image and a 4000 px strip both get a usable zoom.</summary>
    [Fact]
    public void Fit_clamps_tiny_and_huge_artboards()
    {
        _out.WriteLine($"1x1 -> {CanvasZoom.Fit(1, 1, 800, 600)}; 4000x30 -> {CanvasZoom.Fit(4000, 30, 800, 600)}; " +
                       $"300x100 -> {CanvasZoom.Fit(300, 100, 600, 600)}; empty -> {CanvasZoom.Fit(0, 0, 800, 600)}; " +
                       $"no viewport -> {CanvasZoom.Fit(300, 100, 0, 0)}");
        Assert.Equal(8.0, CanvasZoom.Fit(1, 1, 800, 600));
        Assert.Equal(0.25, CanvasZoom.Fit(4000, 30, 800, 600));
        Assert.Equal(2.0, CanvasZoom.Fit(300, 100, 600, 600));
        Assert.Equal(1.0, CanvasZoom.Fit(0, 0, 800, 600));
        Assert.Equal(1.0, CanvasZoom.Fit(300, 100, 0, 0));
    }

    [Fact]
    public void Zooming_at_the_cursor_keeps_the_point_under_it()
    {
        double offset = 100, cursor = 50;
        double next = CanvasZoom.OffsetFor(offset, cursor, 1, 2);
        double before = (offset + cursor) / 1, after = (next + cursor) / 2;
        _out.WriteLine($"offset {offset} -> {next}; content under cursor {before} -> {after}");
        Assert.Equal(before, after);
        Assert.Equal(0, CanvasZoom.OffsetFor(0, 10, 2, 1)); // never scrolls before the start
    }
}
