using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The one probe the loader and the designer both read artwork headers through. The
/// loader's error wording is user-facing (the Settings picker shows it), so it is pinned against
/// the real loader rather than restated.</summary>
public class ImageProbeTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aorineq-probe-" + Guid.NewGuid().ToString("N"));

    public ImageProbeTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_bad_sheet_is_worded_exactly_as_the_loader_words_it()
    {
        var folder = Path.Combine(_dir, "sheet");
        Directory.CreateDirectory(folder);
        TestPngs.Write(Path.Combine(folder, "empty.png"), 300, 7);
        TestPngs.Write(Path.Combine(folder, "full.png"), 300, 7);
        File.WriteAllText(Path.Combine(folder, "skin.json"), """{"emptyFrames":2}""");

        var loaded = SkinLoader.Load(folder);
        var probed = new ImageProbe().Layer(Path.Combine(folder, "empty.png"), "empty", 2);

        _out.WriteLine($"loader: {loaded.Error}");
        _out.WriteLine($"probe : {probed.Error} ({probed.Problem})");
        Assert.Equal(loaded.Error, probed.Error);
        Assert.Equal(LayerProblem.FramesDoNotDivide, probed.Problem);
        Assert.False(probed.IsUsable);
    }

    [Fact]
    public void A_sprite_sheet_reports_its_logical_frame_size()
    {
        var path = Path.Combine(_dir, "sheet.png");
        TestPngs.Write(path, 300, 80);

        var probed = new ImageProbe().Layer(path, "empty", 4);

        _out.WriteLine($"{probed}");
        Assert.Equal(new LayerProbe(path, false, 300, 20, LayerProblem.None, null), probed);
    }

    [Fact]
    public void A_gif_ignores_the_declared_frame_count()
    {
        var path = Path.Combine(_dir, "anim.gif");
        TestPngs.WriteGif(path, 120, 30);

        var probed = new ImageProbe().Layer(path, "full", 5);

        _out.WriteLine($"{probed}");
        Assert.Equal(new LayerProbe(path, true, 120, 30, LayerProblem.None, null), probed);
    }

    [Fact]
    public void A_missing_file_is_not_found_and_a_corrupt_one_is_unreadable()
    {
        var missing = new ImageProbe().Layer(Path.Combine(_dir, "nope.png"), "empty", 1);
        var corruptPath = Path.Combine(_dir, "corrupt.png");
        File.WriteAllBytes(corruptPath, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25]);
        var corrupt = new ImageProbe().Layer(corruptPath, "notice", 1);

        _out.WriteLine($"missing: {missing.Problem} '{missing.Error}'");
        _out.WriteLine($"corrupt: {corrupt.Problem} '{corrupt.Error}'");
        Assert.Equal(LayerProblem.NotFound, missing.Problem);
        Assert.Equal("nope.png not found", missing.Error);
        Assert.Equal(LayerProblem.Unreadable, corrupt.Problem);
        Assert.Equal("notice.png is not a valid PNG", corrupt.Error);
    }

    [Fact]
    public void Size_is_cached_until_the_file_changes_on_disk()
    {
        var path = Path.Combine(_dir, "art.png");
        TestPngs.Write(path, 100, 10);
        var stamp = File.GetLastWriteTimeUtc(path);
        var probe = new ImageProbe();
        var first = probe.Size(path);

        // Same length and the SAME timestamp: the cache must answer without re-reading, which is
        // the only way to see that it is a cache at all.
        TestPngs.Write(path, 200, 20);
        File.SetLastWriteTimeUtc(path, stamp);
        var cached = probe.Size(path);

        // A real change on disk (new timestamp) must be seen.
        File.SetLastWriteTimeUtc(path, stamp.AddMinutes(1));
        var refreshed = probe.Size(path);

        _out.WriteLine($"first={first} cached={cached} refreshed={refreshed}");
        Assert.Equal((100, 10), first);
        Assert.Equal((100, 10), cached);
        Assert.Equal((200, 20), refreshed);
    }
}
