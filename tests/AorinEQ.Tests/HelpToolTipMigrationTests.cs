using System.Text.RegularExpressions;

namespace AorinEQ.Tests;

/// <summary>No window may carry a hand-written tooltip.
///
/// The EQ editor shipped ten of them before this release - `ToolTip="Remove this band"` and the
/// like. A literal tooltip is a second source of truth for the same control: never translated,
/// never found by search, and free to contradict the help topic beside it. All ten now come from
/// the string table.
///
/// This test is what stops an eleventh being added out of habit, because typing ToolTip="..." is
/// what every WPF example on the internet shows.</summary>
public class HelpToolTipMigrationTests
{
    /// <summary>A ToolTip whose value is neither a markup extension ({loc:T ...}, a binding) nor
    /// empty - i.e. text typed straight into the XAML.</summary>
    private static readonly Regex LiteralToolTip =
        new(@"(?<![\w.])ToolTip\s*=\s*""[^""{]", RegexOptions.Compiled);

    private static readonly Regex XamlComment = new("<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    [Theory]
    [MemberData(nameof(WindowXamlFiles))]
    public void No_window_xaml_carries_a_hand_written_tooltip(string relativePath)
    {
        var xaml = XamlComment.Replace(RepoFiles.ReadText(relativePath), "");

        var offenders = LiteralToolTip.Matches(xaml)
            .Select(m => xaml[m.Index..Math.Min(xaml.Length, m.Index + 60)].ReplaceLineEndings(" "))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{relativePath} has {offenders.Count} literal ToolTip(s). Use {{loc:T <key>}} so the " +
            $"text is translated, or help:Help.Topic so it is translated AND searchable:" +
            $"{Environment.NewLine}  " + string.Join($"{Environment.NewLine}  ", offenders));
    }

    public static TheoryData<string> WindowXamlFiles() => RepoFiles.WindowXamlTheoryData();
}
