using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class PresetStoreTests : IDisposable
{
    private readonly string _root;
    private readonly ITestOutputHelper _out;

    public PresetStoreTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "apo-presets-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Save_List_Load_Delete_roundtrip()
    {
        PresetStore.Save(_root, "HD 650", AutoEqFixture.Hd650ParametricEq);
        PresetStore.Save(_root, "flat", "Filter 1: ON PK Fc 1000 Hz Gain 0.0 dB Q 1.00");

        var names = PresetStore.List(_root);
        _out.WriteLine("names: " + string.Join(", ", names));
        Assert.Equal(new[] { "flat", "HD 650" }, names); // sorted, case-insensitive

        var preset = PresetStore.Load(_root, "HD 650");
        Assert.NotNull(preset);
        Assert.Equal(10, preset!.Bands.Count);
        Assert.Equal("HD 650", preset.Name);

        Assert.True(PresetStore.Delete(_root, "flat"));
        Assert.False(PresetStore.Delete(_root, "flat")); // already gone
        Assert.Equal(new[] { "HD 650" }, PresetStore.List(_root));
    }

    [Fact]
    public void Load_missing_returns_null_and_List_empty_dir_is_empty()
    {
        Assert.Null(PresetStore.Load(_root, "nope"));
        Assert.Empty(PresetStore.List(_root));
        Assert.Empty(PresetStore.List(Path.Combine(_root, "does-not-exist")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("NUL")]
    [InlineData("ends.")]
    public void Save_rejects_invalid_names(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => PresetStore.Save(_root, name, "x"));
        _out.WriteLine($"'{name}' -> {ex.Message}");
    }

    [Fact]
    public void SanitizeName_makes_autoeq_model_names_file_safe()
    {
        Assert.Equal("Sennheiser HD 650", PresetStore.SanitizeName("Sennheiser HD 650"));
        Assert.Equal("Apple AirPods Pro 2 (51dB + ANC)",
            PresetStore.SanitizeName("Apple AirPods Pro 2 (51dB + ANC)"));
        Assert.Equal("Focal Bathys (wired-USB)", PresetStore.SanitizeName("Focal Bathys (wired/USB)"));
        Assert.Equal("weird - name", PresetStore.SanitizeName("weird <:> name"));
        Assert.Equal("NUL-", PresetStore.SanitizeName("NUL")); // reserved device name defused
        Assert.Equal("ends", PresetStore.SanitizeName("ends."));
        Assert.Equal("preset", PresetStore.SanitizeName("   ")); // nothing left -> placeholder
        // Every sanitized output must actually pass validation.
        foreach (var hostile in new[] { "a/b\\c", "COM1", "...", "x\"y|z", "" })
        {
            var sanitized = PresetStore.SanitizeName(hostile);
            _out.WriteLine($"'{hostile}' -> '{sanitized}'");
            Assert.Null(PresetStore.ValidateName(sanitized));
        }
    }

    [Fact]
    public void ValidateName_rejects_a_name_too_long_for_a_path()
    {
        // A name arrives from an apo-volume:// link as well as from a text box, and the link cap
        // allows thousands of characters — without this the failure would surface as a
        // PathTooLongException at write time, long after the link was accepted.
        Assert.Null(PresetStore.ValidateName(new string('a', FileNames.MaxLength)));
        var error = PresetStore.ValidateName(new string('a', FileNames.MaxLength + 1));
        _out.WriteLine(error);
        Assert.NotNull(error);
        Assert.Contains("too long", error!);
    }

    [Fact]
    public void A_preset_named_before_the_length_cap_stays_loadable_and_deletable()
    {
        // The cap is a policy for names we ACCEPT. Applying it to names already on disk would
        // make anything saved before it shipped unselectable in the editor and impossible to
        // delete from the app — the file would just sit there, listed but inert.
        var longName = new string('n', FileNames.MaxLength + 20);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, longName + ".txt"),
            "Filter 1: ON PK Fc 1000 Hz Gain 3.0 dB Q 1.00");

        Assert.Contains(longName, PresetStore.List(_root));
        var loaded = PresetStore.Load(_root, longName);
        _out.WriteLine($"{longName.Length}-char name -> loaded {loaded?.Bands.Count} bands");
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Bands);
        Assert.True(PresetStore.Delete(_root, longName));
        Assert.Empty(PresetStore.List(_root));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("NUL")]
    [InlineData("")]
    public void Path_unsafe_names_are_still_refused_by_every_entry_point(string name)
    {
        // Relaxing the LENGTH check on the read paths must not relax traversal safety.
        Assert.False(FileNames.IsPathSafe(name));
        Assert.Null(PresetStore.Load(_root, name));
        Assert.False(PresetStore.Delete(_root, name));
        Assert.Throws<ArgumentException>(() => PresetStore.Save(_root, name, "x"));
    }
}
