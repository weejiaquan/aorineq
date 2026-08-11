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

    [Fact]
    public void Load_locked_file_returns_default()
    {
        new Settings(73, true).Save(_path);
        using var locker = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);
        var loaded = Settings.Load(_path);
        _out.WriteLine("loaded while file locked (FileShare.None): " + loaded);
        Assert.Equal(Settings.Default, loaded);
    }

    [Fact]
    public void Load_v1_file_without_RunAsAdmin_defaults_false()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{\"Percent\":73,\"Muted\":true}"); // v1 format
        var s = Settings.Load(_path);
        _out.WriteLine($"loaded: {s}");
        Assert.Equal(73, s.Percent);
        Assert.True(s.Muted);
        Assert.False(s.RunAsAdmin);
    }

    [Fact]
    public void RunAsAdmin_roundtrips()
    {
        new Settings(40, false, RunAsAdmin: true).Save(_path);
        Assert.True(Settings.Load(_path).RunAsAdmin);
    }

    [Fact]
    public void Load_v11_file_without_new_fields_defaults_all()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{\"Percent\":73,\"Muted\":true}"); // v1.1 format
        var s = Settings.Load(_path);
        _out.WriteLine($"loaded v1.1: {s}");
        Assert.Equal(73, s.Percent);
        Assert.True(s.Muted);
        Assert.False(s.RunAsAdmin);
        Assert.Equal("dark-pill", s.OsdStyle);
        Assert.Equal("", s.SkinName);
        Assert.Equal("bottom-center", s.OsdAnchor);
        Assert.Equal(0, s.OsdOffsetX);
        Assert.Equal(0, s.OsdOffsetY);
        Assert.Equal(1.5, s.HideDelaySeconds);
        Assert.True(s.AnimationEnabled);
        Assert.Equal(150, s.AnimationMs);
        Assert.Equal(2, s.StepPercent);
    }

    [Fact]
    public void Load_clamps_out_of_range_new_fields()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path,
            "{\"Percent\":50,\"Muted\":false,\"HideDelaySeconds\":99,\"AnimationMs\":5,\"StepPercent\":3,\"OsdAnchor\":\"middle\",\"OsdStyle\":\"neon\"}");
        var s = Settings.Load(_path);
        _out.WriteLine($"loaded with clamping: {s}");
        Assert.Equal(5.0, s.HideDelaySeconds);
        Assert.Equal(50, s.AnimationMs);
        Assert.Equal(2, s.StepPercent);
        Assert.Equal("bottom-center", s.OsdAnchor);
        Assert.Equal("dark-pill", s.OsdStyle);
    }

    [Fact]
    public void Save_with_bare_filename_writes_to_current_directory()
    {
        var name = "apo-volume-test-" + Guid.NewGuid().ToString("N") + ".json";
        try
        {
            new Settings(60, false).Save(name); // no directory component: must not throw
            _out.WriteLine($"saved bare filename to: {Path.GetFullPath(name)}");
            Assert.True(File.Exists(name));
            Assert.Equal(60, Settings.Load(name).Percent);
        }
        finally
        {
            File.Delete(name);
        }
    }

    [Fact]
    public void VolumeMode_missing_defaults_to_eapo()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{\"Percent\":73,\"Muted\":true}"); // pre-1.8 format
        var s = Settings.Load(_path);
        _out.WriteLine($"loaded pre-1.8: {s}");
        Assert.Equal(VolumeModes.Eapo, s.VolumeMode);
    }

    [Fact]
    public void VolumeMode_garbage_normalizes_to_eapo()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{\"Percent\":50,\"Muted\":false,\"VolumeMode\":\"banana\"}");
        var s = Settings.Load(_path);
        _out.WriteLine($"loaded with garbage mode: {s}");
        Assert.Equal(VolumeModes.Eapo, s.VolumeMode);
    }

    [Fact]
    public void VolumeMode_system_roundtrips()
    {
        new Settings(50, false, VolumeMode: VolumeModes.System).Save(_path);
        var s = Settings.Load(_path);
        _out.WriteLine($"roundtripped: {s}");
        Assert.Equal(VolumeModes.System, s.VolumeMode);
    }

    [Fact]
    public void All_fields_roundtrip()
    {
        var orig = new Settings(
            Percent: 73, Muted: true, RunAsAdmin: true,
            OsdStyle: "fluent", SkinName: "custom", OsdAnchor: "top-left",
            OsdOffsetX: 10, OsdOffsetY: 20,
            HideDelaySeconds: 3.0, AnimationEnabled: false, AnimationMs: 300, StepPercent: 5);
        orig.Save(_path);
        var loaded = Settings.Load(_path);
        Assert.Equal(orig, loaded);
    }
}
