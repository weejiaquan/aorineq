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
}
