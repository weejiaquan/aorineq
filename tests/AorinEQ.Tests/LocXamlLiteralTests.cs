using System.Text.RegularExpressions;

namespace AorinEQ.Tests;

/// <summary>No user-facing text may remain as a literal in window XAML.
///
/// This is the test that makes localisation STAY done. Keying two hundred and sixty-odd literals
/// once is a morning's work; the failure mode is the two hundred and sixty-fifth, added next year
/// by someone typing Text="Enable" because that is what every WPF example on the internet shows.
/// Then exactly one label is English in all five languages, and nobody notices until a user
/// reports it - if they bother, which mostly they do not. They just conclude the translation is
/// sloppy.
///
/// The allowlist is for values that are not language: product names that are written identically
/// in all five, units, and single symbols.</summary>
public class LocXamlLiteralTests
{
    /// <summary>Attributes whose value the user reads. A value starting with '{' is already a
    /// markup extension - a binding or a {loc:T} - and is what this test wants to see.</summary>
    private static readonly Regex Literal = new(
        // The lookbehind matters: without it "Content" matches inside SizeToContent="Manual" and
        // "Text" inside PlaceholderText, and the test reports layout enums as untranslated strings.
        @"(?<![\w.])(?<attr>Text|Content|Header|PlaceholderText|Title|ToolTip)\s*=\s*""(?<value>[^""{][^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex XamlComment = new("<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Values that are not language, so they are the same string in every table.
    ///
    /// Keyed by the literal rather than by file: "dB" is "dB" in Japanese and listing it once is
    /// honest, where listing it per file would be noise that hides the real entries.</summary>
    private static readonly HashSet<string> NotLanguage = new(StringComparer.Ordinal)
    {
        "AorinEQ", "AirPlay", "HomePod", "Apple TV", "Equalizer APO", "AutoEq", "GitHub",
        "dB", "Hz", "ms", "Q", "%", "×", "—", "…", "?", "+", "-",
    };

    [Theory]
    [MemberData(nameof(WindowXamlFiles))]
    public void No_window_xaml_carries_a_user_facing_literal(string relativePath)
    {
        // Comments are stripped first: this repo comments heavily, and several of those comments
        // quote the very attributes being searched for.
        var xaml = XamlComment.Replace(RepoFiles.ReadText(relativePath), "");

        var offenders = Literal.Matches(xaml)
            .Select(m => m.Groups["value"].Value.Trim())
            .Where(v => v.Length > 0)
            .Where(v => !NotLanguage.Contains(v))
            .Where(v => !IsNotProse(v))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{relativePath} still has {offenders.Count} literal user-facing string(s). Replace " +
            $"each with {{loc:T <key>}} and add the key to strings.en.json:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", offenders));
    }

    /// <summary>A value that is plainly not prose: a resource path, a number, a WPF-UI symbol
    /// name, a units suffix on a number.</summary>
    private static bool IsNotProse(string value) =>
        value.StartsWith('/') ||
        double.TryParse(value, out _) ||
        Regex.IsMatch(value, @"^[A-Za-z]+[0-9]{2,3}$") ||       // Warning24, Speaker224
        Regex.IsMatch(value, @"^[0-9.]+\s*(%|dB|Hz|ms|x)?$");   // "1%", "0.7", "48 kHz"

    public static TheoryData<string> WindowXamlFiles() => RepoFiles.WindowXamlTheoryData();
}
