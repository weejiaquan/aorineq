using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class EqConfigRenderTests
{
    private const string GuidA = "{aaaaaaaa-1111-2222-3333-444444444444}";
    private const string GuidB = "{bbbbbbbb-5555-6666-7777-888888888888}";

    private readonly ITestOutputHelper _out;
    public EqConfigRenderTests(ITestOutputHelper output) => _out = output;

    private static EqBand Pk(double fc, double gain, double q) => new(EqBandType.Peak, fc, gain, q);

    [Fact]
    public void Render_emits_global_filters_then_guid_guarded_device_blocks()
    {
        var model = new EqConfigModel(
            GlobalEqEnabled: true, GlobalPresetPreampDb: 0,
            GlobalBands: new[] { Pk(1000, 3.0, 1.0) },
            Devices: new[]
            {
                new DeviceEqSection(GuidA, VolumeDb: -12.5, EqEnabled: true, PresetPreampDb: 0,
                    Bands: new[] { Pk(105, -2.9, 0.7) }),
                new DeviceEqSection(GuidB, VolumeDb: -3.0, EqEnabled: true, PresetPreampDb: 0,
                    Bands: Array.Empty<EqBand>()),
            });
        var text = ApoWriter.RenderConfig(model);
        _out.WriteLine(text);
        var lines = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[]
        {
            "# managed by apo-volume - do not hand-edit",
            "Filter 1: ON PK Fc 1000 Hz Gain 3.0 dB Q 1.00",
            $"Device: {GuidA}",
            "Preamp: -12.5 dB",
            "Filter 1: ON PK Fc 105 Hz Gain -2.9 dB Q 0.70",
            $"Device: {GuidB}",
            "Preamp: -3.0 dB",
            "Device: all",
        }, lines);
    }

    [Fact]
    public void Render_sums_volume_device_preset_and_global_preset_preamps()
    {
        var model = new EqConfigModel(
            GlobalEqEnabled: true, GlobalPresetPreampDb: -2.0,
            GlobalBands: new[] { Pk(500, 4.0, 1.0) },
            Devices: new[]
            {
                new DeviceEqSection(GuidA, VolumeDb: -12.5, EqEnabled: true, PresetPreampDb: -6.1,
                    Bands: new[] { Pk(105, -2.9, 0.7) }),
            });
        var text = ApoWriter.RenderConfig(model);
        _out.WriteLine(text);
        Assert.Contains("Preamp: -20.6 dB", text); // -12.5 + -6.1 + -2.0
    }

    [Fact]
    public void Render_with_scope_disabled_drops_filters_and_their_preamp_compensation()
    {
        var model = new EqConfigModel(
            GlobalEqEnabled: false, GlobalPresetPreampDb: -2.0,
            GlobalBands: new[] { Pk(500, 4.0, 1.0) },
            Devices: new[]
            {
                new DeviceEqSection(GuidA, VolumeDb: -10.0, EqEnabled: false, PresetPreampDb: -6.1,
                    Bands: new[] { Pk(105, -2.9, 0.7) }),
            });
        var text = ApoWriter.RenderConfig(model);
        _out.WriteLine(text);
        // Volume preamp ALWAYS renders (keys must keep working); no filter lines at all,
        // and neither preset preamp applies when its filters are bypassed.
        Assert.Contains("Preamp: -10.0 dB", text);
        Assert.DoesNotContain("Filter", text);
    }

    [Fact]
    public void Render_global_preamp_only_folds_in_when_global_filters_render()
    {
        var noGlobalBands = new EqConfigModel(true, -2.0, Array.Empty<EqBand>(),
            new[] { new DeviceEqSection(GuidA, -10.0, true, 0, Array.Empty<EqBand>()) });
        var text = ApoWriter.RenderConfig(noGlobalBands);
        _out.WriteLine(text);
        Assert.Contains("Preamp: -10.0 dB", text); // no bands -> nothing to compensate
    }

    [Fact]
    public void Render_empty_model_is_header_only()
    {
        var text = ApoWriter.RenderConfig(new EqConfigModel(false, 0, Array.Empty<EqBand>(),
            Array.Empty<DeviceEqSection>()));
        _out.WriteLine(text);
        var lines = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines);
        Assert.StartsWith("#", line);
    }

    [Fact]
    public void Render_skips_devices_without_a_guid()
    {
        var model = new EqConfigModel(false, 0, Array.Empty<EqBand>(), new[]
        {
            new DeviceEqSection("", -10.0, false, 0, Array.Empty<EqBand>()),
            new DeviceEqSection(GuidA, -5.0, false, 0, Array.Empty<EqBand>()),
        });
        var text = ApoWriter.RenderConfig(model);
        _out.WriteLine(text);
        Assert.DoesNotContain("Preamp: -10.0 dB", text);
        Assert.Contains($"Device: {GuidA}", text);
    }

    [Fact]
    public void Render_mute_uses_minus_120()
    {
        var model = new EqConfigModel(false, 0, Array.Empty<EqBand>(), new[]
        {
            new DeviceEqSection(GuidA, VolumeState.MuteDb, true, -6.0, new[] { Pk(100, 6.0, 1.0) }),
        });
        var text = ApoWriter.RenderConfig(model);
        _out.WriteLine(text);
        Assert.Contains("Preamp: -126.0 dB", text); // mute + preset preamp still sums
    }

    [Fact]
    public void ReadDevicePreamp_finds_each_device_block_and_rejects_unknown()
    {
        var model = new EqConfigModel(true, 0, new[] { Pk(1000, 3.0, 1.0) }, new[]
        {
            new DeviceEqSection(GuidA, -12.5, true, 0, new[] { Pk(105, -2.9, 0.7) }),
            new DeviceEqSection(GuidB, -3.0, false, 0, Array.Empty<EqBand>()),
        });
        var text = ApoWriter.RenderConfig(model);
        _out.WriteLine(text);
        Assert.Equal(-12.5, ApoWriter.ReadDevicePreamp(text, GuidA));
        Assert.Equal(-3.0, ApoWriter.ReadDevicePreamp(text, GuidB));
        Assert.Null(ApoWriter.ReadDevicePreamp(text, "{cccccccc-0000-0000-0000-000000000000}"));
    }

    [Fact]
    public void ReadDevicePreamp_returns_null_for_legacy_single_line_file()
    {
        // The pre-v2 file was a bare "Preamp: x dB" with no Device guards — a device query
        // must not claim it (migration rerenders the file in block format on first write).
        Assert.Null(ApoWriter.ReadDevicePreamp("Preamp: -25.3 dB" + Environment.NewLine, GuidA));
    }

    [Fact]
    public void WriteConfig_replaces_a_legacy_single_preamp_file_atomically()
    {
        var dir = Path.Combine(Path.GetTempPath(), "apo-volume-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var w = new ApoWriter(dir);
            File.WriteAllText(w.VolumeFilePath, "Preamp: -25.3 dB" + Environment.NewLine); // v1.x file
            var model = new EqConfigModel(false, 0, Array.Empty<EqBand>(), new[]
            {
                new DeviceEqSection(GuidA, -12.5, false, 0, Array.Empty<EqBand>()),
            });
            w.WriteConfig(model);
            w.Flush();
            var content = File.ReadAllText(w.VolumeFilePath);
            _out.WriteLine(content);
            Assert.Equal(ApoWriter.RenderConfig(model), content);
            Assert.DoesNotContain("-25.3", content);
            // Atomic temp+rename leaves no stray temp files behind.
            var stray = Directory.GetFiles(dir).Where(f =>
                Path.GetFileName(f) is not ("apo-volume.txt" or "config.txt")).ToArray();
            Assert.Empty(stray);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
