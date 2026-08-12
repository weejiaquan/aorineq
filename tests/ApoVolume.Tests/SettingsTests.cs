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
    public void Eq_and_device_volume_state_roundtrips()
    {
        const string dev = "{0.0.0.00000000}.{aaaaaaaa-1111-2222-3333-444444444444}";
        var settings = Settings.Default with
        {
            DeviceVolumes = new Dictionary<string, DeviceVolumeSetting> { [dev] = new(80, true) },
            DeviceEq = new Dictionary<string, EqScopeSetting>
            {
                [dev] = new("HD 650", -6.1, Enabled: true,
                    Bands: new[] { new EqBand(EqBandType.LowShelf, 105, 6.4, 0.7) }),
            },
            GlobalEq = new EqScopeSetting("house", -2.0, Enabled: false,
                Bands: new[] { new EqBand(EqBandType.Peak, 1000, 3.0, 1.0) }),
        };
        settings.Save(_path);
        _out.WriteLine(File.ReadAllText(_path));
        var s = Settings.Load(_path);
        Assert.Equal(80, s.DeviceVolumes![dev].Percent);
        Assert.True(s.DeviceVolumes[dev].Muted);
        var eq = s.DeviceEq![dev];
        Assert.Equal("HD 650", eq.PresetName);
        Assert.Equal(-6.1, eq.PresetPreampDb, 3);
        Assert.True(eq.Enabled);
        var band = Assert.Single(eq.Bands!);
        Assert.Equal(EqBandType.LowShelf, band.Type);
        Assert.Equal(105, band.Fc, 3);
        Assert.False(s.GlobalEq!.Enabled);
        Assert.Equal(EqBandType.Peak, Assert.Single(s.GlobalEq.Bands!).Type);
        // Band types persist as readable names, not bare numbers.
        var json = File.ReadAllText(_path);
        Assert.Contains("LowShelf", json);
        // Derived properties must NOT be persisted (a stored copy could drift from its type).
        Assert.DoesNotContain("HasGain", json);
    }

    [Fact]
    public void Load_v19_file_without_eq_fields_defaults_null()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{\"Percent\":40,\"Muted\":false,\"VolumeMode\":\"eapo\"}");
        var s = Settings.Load(_path);
        _out.WriteLine($"loaded v1.9: {s}");
        Assert.Null(s.DeviceVolumes);
        Assert.Null(s.DeviceEq);
        Assert.Null(s.GlobalEq);
    }

    [Fact]
    public void Load_clamps_hostile_eq_and_device_values()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, """
            {"Percent":50,"Muted":false,
             "DeviceVolumes":{"dev":{"Percent":999,"Muted":false}},
             "GlobalEq":{"PresetName":"x","PresetPreampDb":-500,"Enabled":true,
                         "Bands":[{"Type":"Peak","Fc":999999,"GainDb":500,"Q":0}]}}
            """);
        var s = Settings.Load(_path);
        _out.WriteLine($"loaded hostile: {s.GlobalEq}");
        Assert.Equal(100, s.DeviceVolumes!["dev"].Percent);
        Assert.Equal(-60, s.GlobalEq!.PresetPreampDb);
        var band = Assert.Single(s.GlobalEq.Bands!);
        Assert.Equal(24000, band.Fc);
        Assert.Equal(30, band.GainDb);
        Assert.Equal(0.1, band.Q);
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
    public void ProtocolLinks_and_AutoUpdate_default_on_when_missing_from_older_settings()
    {
        // An existing pre-1.9 settings.json has neither field — both features default ON.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{\"Percent\":50,\"Muted\":false}");
        var s = Settings.Load(_path);
        _out.WriteLine($"loaded: ProtocolLinksEnabled={s.ProtocolLinksEnabled} AutoUpdate={s.AutoUpdate}");
        Assert.True(s.ProtocolLinksEnabled);
        Assert.True(s.AutoUpdate);
    }

    [Fact]
    public void ProtocolLinks_and_AutoUpdate_off_roundtrip()
    {
        new Settings(50, false, ProtocolLinksEnabled: false, AutoUpdate: false).Save(_path);
        _out.WriteLine("saved json: " + File.ReadAllText(_path));
        var s = Settings.Load(_path);
        Assert.False(s.ProtocolLinksEnabled);
        Assert.False(s.AutoUpdate);
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
