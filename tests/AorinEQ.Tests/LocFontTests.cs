using System.Text.RegularExpressions;

namespace AorinEQ.Tests;

/// <summary>Segoe UI contains no CJK glyphs.
///
/// The app set no FontFamily at all before this release, so everything inherited Segoe UI. Ship
/// the Chinese, Japanese or Korean table against that and the user sees tofu boxes - not an
/// exception, not a failed test, just squares where the words should be. WPF does some font
/// fallback of its own, and leaning on it is precisely why this class of failure shows up as
/// "some labels are squares, on some machines, in some languages".
///
/// So the chain is explicit and every window states it. The four families after Segoe UI are the
/// Windows 10/11 system UI faces for Simplified Chinese, Traditional Chinese, Japanese and Korean;
/// none of them needs installing. WPF walks the list per GLYPH, so an English UI still renders in
/// Segoe UI and only the characters it lacks come from the others.</summary>
public class LocFontTests
{
    private static readonly (string Family, string Script)[] RequiredFallbacks =
    [
        ("Microsoft YaHei UI", "Simplified Chinese"),
        ("Microsoft JhengHei UI", "Traditional Chinese"),
        ("Yu Gothic UI", "Japanese"),
        ("Malgun Gothic", "Korean"),
    ];

    private static string AppXaml => RepoFiles.ReadText("src/AorinEQ/App.xaml");

    [Fact]
    public void The_app_font_chain_exists()
    {
        Assert.Matches("""<FontFamily\s+x:Key="AppFontFamily">""", AppXaml);
    }

    [Fact]
    public void The_app_font_chain_starts_with_segoe_ui()
    {
        // Segoe UI first, so an English UI is unchanged by this release. A CJK face first would
        // restyle the entire app for every user who reads English.
        Assert.StartsWith("Segoe UI", Chain(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Microsoft YaHei UI")]
    [InlineData("Microsoft JhengHei UI")]
    [InlineData("Yu Gothic UI")]
    [InlineData("Malgun Gothic")]
    public void The_app_font_chain_covers_every_script_we_ship(string family)
    {
        var script = RequiredFallbacks.First(f => f.Family == family).Script;
        Assert.True(Chain().Contains(family, StringComparison.Ordinal),
            $"The AppFontFamily chain has no {script} face ({family}). Text in that language " +
            "will render as empty boxes.");
    }

    [Theory]
    [MemberData(nameof(WindowXamlFiles))]
    public void Every_window_states_the_font_chain(string relativePath)
    {
        var xaml = RepoFiles.ReadText(relativePath);

        Assert.True(xaml.Contains("FontFamily=\"{StaticResource AppFontFamily}\"", StringComparison.Ordinal),
            $"{relativePath} does not set FontFamily=\"{{StaticResource AppFontFamily}}\" on its root. " +
            "Its text will fall back to Segoe UI and render as boxes in Chinese, Japanese and Korean.");
    }

    private static string Chain()
    {
        var match = Regex.Match(AppXaml, """<FontFamily\s+x:Key="AppFontFamily">(?<chain>[^<]+)</FontFamily>""");
        Assert.True(match.Success, "App.xaml must define the AppFontFamily resource.");
        return match.Groups["chain"].Value.Trim();
    }

    public static TheoryData<string> WindowXamlFiles() => RepoFiles.WindowXamlTheoryData();
}
