using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The three skins AorinEQ ships with.
///
/// They are drawn at run time rather than installed, so the thing worth testing is not what they
/// look like but that each one LOADS - a shipped skin that SkinLoader rejects would be the app
/// shipping its own broken example, and it is the example skin authors will copy.</summary>
public class DefaultSkinsTests : IDisposable
{
    private readonly string _root;
    private readonly ITestOutputHelper _out;

    public DefaultSkinsTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "aorineq-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void All_three_shipped_skins_are_created()
    {
        var created = DefaultSkins.EnsureInstalled(_root);

        _out.WriteLine("created: " + string.Join(", ", created));
        Assert.Equal(SkinArt.All.OrderBy(s => s), created.OrderBy(s => s));
    }

    /// <summary>The point of the whole exercise: every shipped skin has to survive the same loader
    /// a user's own skin goes through, including its AirPlay bar.</summary>
    [Theory]
    [InlineData("windows-11")]
    [InlineData("windows-10")]
    [InlineData("windows-xp")]
    public void Each_shipped_skin_loads_cleanly_and_has_an_airplay_bar(string style)
    {
        DefaultSkins.EnsureInstalled(_root);
        var skin = SkinLoader.Load(Path.Combine(_root, style));

        _out.WriteLine($"{style}: valid={skin.IsValid} error={skin.Error ?? "(none)"}");
        _out.WriteLine($"  volume  {skin.Width}x{skin.Height} fill {skin.FillStartX}..{skin.FillEndX}");
        _out.WriteLine($"  airplay {skin.AirPlay?.Width}x{skin.AirPlay?.Height} " +
                       $"hit {skin.AirPlay?.DropdownHit} error={skin.AirPlayError ?? "(none)"}");

        Assert.True(skin.IsValid, skin.Error);
        Assert.Null(skin.AirPlayError);
        Assert.NotNull(skin.AirPlay);
        Assert.Equal(SkinArt.Width, skin.Width);
        Assert.Equal(SkinArt.VolumeHeight, skin.Height);
        Assert.Equal(SkinArt.AirPlayHeight, skin.AirPlay!.Height);
    }

    /// <summary>The JSON is generated from the same constants as the drawing, so the fill range it
    /// declares must be the one the artwork was painted for. Written twice, they would drift.</summary>
    [Theory]
    [InlineData("windows-11")]
    [InlineData("windows-10")]
    [InlineData("windows-xp")]
    public void The_declared_fill_range_matches_the_artwork_it_describes(string style)
    {
        DefaultSkins.EnsureInstalled(_root);
        var skin = SkinLoader.Load(Path.Combine(_root, style));

        _out.WriteLine($"{style}: volume {skin.FillStartX}..{skin.FillEndX}, " +
                       $"airplay {skin.AirPlay!.FillStartX}..{skin.AirPlay.FillEndX}");

        Assert.Equal(SkinArt.FillStartX, skin.FillStartX);
        Assert.Equal(SkinArt.FillEndX, skin.FillEndX);
        Assert.Equal(SkinArt.FillStartX, skin.AirPlay.FillStartX);
        Assert.Equal(SkinArt.FillEndX, skin.AirPlay.FillEndX);
    }

    /// <summary>The chevron is drawn inside the box the JSON declares as the click target, so what
    /// the eye aims at and what the hit test accepts are the same rectangle.</summary>
    [Theory]
    [InlineData("windows-11")]
    [InlineData("windows-10")]
    [InlineData("windows-xp")]
    public void The_dropdown_hit_region_is_where_the_chevron_is_drawn(string style)
    {
        DefaultSkins.EnsureInstalled(_root);
        var hit = SkinLoader.Load(Path.Combine(_root, style)).AirPlay!.DropdownHit;
        var (x, y, w, h) = SkinArt.DropdownHit;

        _out.WriteLine($"{style}: declared {hit}, drawn [{x},{y} {w}x{h}]");

        Assert.Equal(x, hit.X);
        Assert.Equal(y, hit.Y);
        Assert.Equal(w, hit.Width);
        Assert.Equal(h, hit.Height);
    }

    /// <summary>Called on every launch, so it must be idempotent - and it must never overwrite a
    /// skin somebody has edited. Restoring the shipped artwork on the next start is exactly the
    /// behaviour that loses an evening's work.</summary>
    [Fact]
    public void An_edited_shipped_skin_is_never_restored()
    {
        DefaultSkins.EnsureInstalled(_root);

        var marker = Path.Combine(_root, "windows-11", "empty.png");
        File.WriteAllText(marker, "the user replaced this");

        var second = DefaultSkins.EnsureInstalled(_root);

        _out.WriteLine("created on the second pass: " + (second.Count == 0 ? "(nothing)" : string.Join(", ", second)));
        Assert.Empty(second);
        Assert.Equal("the user replaced this", File.ReadAllText(marker));
    }

    /// <summary>Nothing is left behind mid-write. The staging folder is a dot-folder, which
    /// SkinLoader.Scan skips, but one left lying around would still be litter.</summary>
    [Fact]
    public void No_staging_folders_survive_a_successful_install()
    {
        DefaultSkins.EnsureInstalled(_root);

        var strays = Directory.GetDirectories(_root)
            .Select(d => new DirectoryInfo(d).Name)
            .Where(n => n.StartsWith('.'))
            .ToList();

        _out.WriteLine("dot-folders: " + (strays.Count == 0 ? "(none)" : string.Join(", ", strays)));
        Assert.Empty(strays);
    }
}
