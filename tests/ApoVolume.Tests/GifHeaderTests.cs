using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class GifHeaderTests : IDisposable
{
    private readonly string _dir;
    private readonly ITestOutputHelper _out;

    public GifHeaderTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "apo-volume-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(300, 100)]
    [InlineData(1920, 1080)]
    public void Read_round_trips_dimensions(int width, int height)
    {
        var path = Path.Combine(_dir, "test.gif");
        TestPngs.WriteGif(path, width, height);
        var result = GifHeader.Read(path);
        _out.WriteLine($"wrote {width}x{height}, parsed: {(result is null ? "<null>" : $"{result.Value.Width}x{result.Value.Height}")}");
        Assert.Equal((width, height), result);
    }

    [Fact]
    public void Read_returns_null_for_png_bytes()
    {
        var path = Path.Combine(_dir, "actually-a-png.gif");
        TestPngs.Write(path, 300, 100);
        var result = GifHeader.Read(path);
        _out.WriteLine($"png bytes parsed as gif: {(result is null ? "<null>" : "non-null!")}");
        Assert.Null(result);
    }

    [Fact]
    public void Read_returns_null_for_truncated_file()
    {
        var path = Path.Combine(_dir, "truncated.gif");
        File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes("GIF89a"));
        Assert.Null(GifHeader.Read(path));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(300, 0)]
    public void Read_returns_null_for_zero_dimensions(int width, int height)
    {
        var path = Path.Combine(_dir, "zero.gif");
        TestPngs.WriteGif(path, width, height);
        Assert.Null(GifHeader.Read(path));
    }

    [Fact]
    public void Read_returns_null_for_missing_file()
    {
        Assert.Null(GifHeader.Read(Path.Combine(_dir, "nope.gif")));
    }
}
