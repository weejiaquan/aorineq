using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

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
        // GetSkinsRoot targets the real per-user %APPDATA%\AorinEQ\skins folder (there's no
        // injectable root), so this only asserts the path SHAPE and that the method's own
        // create-on-demand behavior actually ran — it doesn't fabricate a temp directory, since
        // the app creates this same real folder on every normal run anyway.
        var root = ApoPaths.GetSkinsRoot();
        _out.WriteLine("resolved skins root: " + root);

        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AorinEQ", "skins");
        Assert.Equal(expectedRoot, root);
        Assert.EndsWith(Path.Combine("AorinEQ", "skins"), root, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(root), $"GetSkinsRoot should create the folder on demand: {root}");
    }

    [Fact]
    public void Every_per_user_path_hangs_off_the_one_state_root()
    {
        // The folder name is spelled once, in ApoPaths. This is the guard that keeps it that way:
        // if any of these ever re-spells it, the v3.0.0 rename is half-done again.
        var root = ApoPaths.GetStateRoot();
        _out.WriteLine("resolved state root: " + root);

        Assert.Equal(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ApoPaths.StateFolderName), root);
        Assert.True(Directory.Exists(root), $"GetStateRoot should create the folder on demand: {root}");
        Assert.Equal(Path.Combine(root, "skins"), ApoPaths.GetSkinsRoot());
        Assert.Equal(Path.Combine(root, "presets"), ApoPaths.GetPresetsRoot());
        Assert.Equal(Path.Combine(root, "protocol-links.txt"), ProtocolSpool.DefaultPath);
    }
}
