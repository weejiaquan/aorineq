using System.IO;
using System.Runtime.InteropServices;
using AorinEQ.UI;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The one filter for what decoding user art can throw. It must catch what a real broken
/// file really throws (or the notice, the HUD widget and the designer crash on it) and nothing
/// that is a bug in our own code (or the bug is silently swallowed).</summary>
[Collection("Wpf")]
public class SkinFramesTests : IDisposable
{
    private readonly StaWpf _sta;
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aorineq-frames-" + Guid.NewGuid().ToString("N"));

    public SkinFramesTests(StaWpf sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void What_a_broken_file_throws_is_an_imaging_failure()
    {
        var headerOnly = Path.Combine(_dir, "header-only.png");
        TestPngs.WriteHeaderOnly(headerOnly, 200, 40);
        var garbage = Path.Combine(_dir, "garbage.png");
        File.WriteAllBytes(garbage, new byte[64]);

        foreach (var path in new[] { headerOnly, garbage, Path.Combine(_dir, "missing.png") })
        {
            var thrown = _sta.Run(() => Record.Exception(() => SkinFrames.Load(path, 1, 10)));
            _out.WriteLine($"{Path.GetFileName(path)}: threw {thrown?.GetType().FullName ?? "(nothing)"}; " +
                           $"expected an imaging failure, IsImagingFailure = {(thrown is null ? "n/a" : SkinFrames.IsImagingFailure(thrown))}");
            Assert.NotNull(thrown);
            Assert.True(SkinFrames.IsImagingFailure(thrown), $"{thrown.GetType().FullName} escaped the filter: {thrown.Message}");
        }
    }

    [Theory]
    [InlineData(typeof(NotSupportedException), true)]
    [InlineData(typeof(FileFormatException), true)]
    [InlineData(typeof(IOException), true)]
    [InlineData(typeof(ArgumentException), true)]
    [InlineData(typeof(UnauthorizedAccessException), true)]
    [InlineData(typeof(COMException), true)]
    [InlineData(typeof(OverflowException), true)]
    [InlineData(typeof(OutOfMemoryException), true)]
    [InlineData(typeof(InvalidOperationException), false)]
    [InlineData(typeof(NullReferenceException), false)]
    public void Only_imaging_failures_are_contained(Type type, bool expected)
    {
        var ex = (Exception)Activator.CreateInstance(type)!;
        bool actual = SkinFrames.IsImagingFailure(ex);
        _out.WriteLine($"{type.Name}: expected {expected}, actual {actual}");
        Assert.Equal(expected, actual);
    }
}
