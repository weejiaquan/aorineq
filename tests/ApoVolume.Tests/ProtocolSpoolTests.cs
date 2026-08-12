using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class ProtocolSpoolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apo-spool-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly ProtocolSpool _spool;
    private readonly ITestOutputHelper _out;

    public ProtocolSpoolTests(ITestOutputHelper output)
    {
        _out = output;
        _path = Path.Combine(_dir, "protocol-links.txt");
        _spool = new ProtocolSpool(_path, "ApoVolumeSpoolTest-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TakeAll_on_missing_file_returns_empty()
    {
        var links = _spool.TakeAll();
        _out.WriteLine($"missing file -> {links.Count} links");
        Assert.Empty(links);
    }

    [Fact]
    public void Post_then_TakeAll_roundtrips_and_consumes()
    {
        _spool.Post("apo-volume://install-skin?url=https://example.com/a.zip");
        _spool.Post("apo-volume://install-skin?url=https://example.com/b.zip");
        var links = _spool.TakeAll();
        _out.WriteLine("took: " + string.Join(" | ", links));
        Assert.Equal(new[]
        {
            "apo-volume://install-skin?url=https://example.com/a.zip",
            "apo-volume://install-skin?url=https://example.com/b.zip",
        }, links);
        Assert.False(File.Exists(_path)); // consumed: the file is gone
        Assert.Empty(_spool.TakeAll());   // second take sees nothing
    }

    [Fact]
    public void Post_creates_the_directory_when_missing()
    {
        Assert.False(Directory.Exists(_dir));
        _spool.Post("apo-volume://install-skin?url=https://example.com/a.zip");
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void TakeAll_skips_blank_lines()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, "apo-volume://x\n\n\napo-volume://y\n");
        var links = _spool.TakeAll();
        Assert.Equal(new[] { "apo-volume://x", "apo-volume://y" }, links);
    }

    [Fact]
    public async Task Concurrent_posts_from_parallel_writers_all_land()
    {
        // Cross-process handoff is the whole point — simulate contention with parallel writers
        // (each Post takes the same named mutex a second process would).
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(
            () => _spool.Post($"apo-volume://install-skin?url=https://example.com/{i}.zip")));
        await Task.WhenAll(tasks);
        var links = _spool.TakeAll();
        _out.WriteLine($"{links.Count} links landed");
        Assert.Equal(20, links.Count);
        Assert.Equal(20, links.Distinct().Count());
    }
}
