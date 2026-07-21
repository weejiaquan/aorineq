using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class ApoPathsTests
{
    private readonly ITestOutputHelper _out;
    public ApoPathsTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void GetConfigDir_finds_real_equalizer_apo_install()
    {
        var dir = ApoPaths.GetConfigDir();
        _out.WriteLine("resolved config dir: " + dir);
        Assert.True(Directory.Exists(dir), $"config dir should exist: {dir}");
        Assert.True(File.Exists(Path.Combine(dir, "config.txt")),
            "config.txt should exist inside the APO config dir");
        Assert.EndsWith("config", dir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSkinsRoot_resolves_under_appdata_and_creates_it()
    {
        // GetSkinsRoot targets the real per-user %APPDATA%\apo-volume\skins folder (there's no
        // injectable root), so this only asserts the path SHAPE and that the method's own
        // create-on-demand behavior actually ran — it doesn't fabricate a temp directory, since
        // the app creates this same real folder on every normal run anyway.
        var root = ApoPaths.GetSkinsRoot();
        _out.WriteLine("resolved skins root: " + root);

        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "apo-volume", "skins");
        Assert.Equal(expectedRoot, root);
        Assert.EndsWith(Path.Combine("apo-volume", "skins"), root, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(root), $"GetSkinsRoot should create the folder on demand: {root}");
    }
}
