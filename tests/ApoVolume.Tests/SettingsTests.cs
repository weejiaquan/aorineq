using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly ITestOutputHelper _out;

    public SettingsTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "apo-volume-tests-" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "sub", "settings.json"); // nested: Save must create directories
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Save_then_Load_roundtrips()
    {
        new Settings(73, true).Save(_path);
        _out.WriteLine("saved json: " + File.ReadAllText(_path));
        var loaded = Settings.Load(_path);
        Assert.Equal(73, loaded.Percent);
        Assert.True(loaded.Muted);
    }

    [Fact]
    public void Load_missing_file_returns_default()
    {
        var loaded = Settings.Load(_path);
        Assert.Equal(Settings.Default, loaded);
        Assert.Equal(50, loaded.Percent);
        Assert.False(loaded.Muted);
    }

    [Fact]
    public void Load_corrupt_file_returns_default()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{not json!!");
        Assert.Equal(Settings.Default, Settings.Load(_path));
    }

    [Fact]
    public void Load_clamps_out_of_range_percent()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{\"Percent\": 999, \"Muted\": false}");
        Assert.Equal(100, Settings.Load(_path).Percent);
    }
}
