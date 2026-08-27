using System.Text.RegularExpressions;
using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>Both directions of the contract between the app and the string table.
///
/// A key the app references but the table lacks renders as the raw key on screen - "settings.step
/// .title" where a label should be. A key the table has but nothing references is a string someone
/// stopped using, which is harmless right up until a translator spends an afternoon on it, in four
/// languages.
///
/// Both directions matter and neither is caught by the compiler, because a string-table key is
/// just a string.</summary>
[Collection("Loc")]
public class LocKeyUsageTests
{
    private static readonly Regex XamlReference =
        new(@"\{loc:T\s+(?<key>[A-Za-z0-9_.\-]+)\s*\}", RegexOptions.Compiled);

    private static readonly Regex CodeReference =
        new(@"Loc\.T\(\s*""(?<key>[A-Za-z0-9_.\-]+)""", RegexOptions.Compiled);

    /// <summary>Prefixes reached by composing a key at runtime (help.{topic}.body), so they never
    /// appear as a literal anywhere. HelpCatalogueTests covers those instead - it walks the
    /// catalogue and asserts every topic resolves a title, summary and body.</summary>
    private static readonly string[] ComposedPrefixes = ["help.", "feature.", "surface."];

    private static IReadOnlyList<string> ReferencedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in RepoFiles.WindowXaml)
            foreach (Match m in XamlReference.Matches(RepoFiles.ReadText(path)))
                keys.Add(m.Groups["key"].Value);

        foreach (var path in RepoFiles.AppSources)
            foreach (Match m in CodeReference.Matches(RepoFiles.ReadText(path)))
                keys.Add(m.Groups["key"].Value);

        return [.. keys.OrderBy(k => k, StringComparer.Ordinal)];
    }

    [Fact]
    public void Every_key_the_app_references_exists_in_the_english_table()
    {
        var table = Loc.Keys(Languages.En).ToHashSet(StringComparer.Ordinal);

        var missing = ReferencedKeys().Where(k => !table.Contains(k)).ToList();

        Assert.True(missing.Count == 0,
            $"Referenced by the app but absent from strings.en.json - these render as the raw " +
            $"key on screen:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", missing));
    }

    [Fact]
    public void Every_key_in_the_english_table_is_used_by_the_app()
    {
        var referenced = ReferencedKeys().ToHashSet(StringComparer.Ordinal);

        var orphans = Loc.Keys(Languages.En)
            .Where(k => !referenced.Contains(k))
            .Where(k => !ComposedPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal)))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(orphans.Count == 0,
            $"In strings.en.json but referenced by nothing - four translations of a string no " +
            $"user will ever see:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", orphans));
    }

    /// <summary>Keys are lower-kebab within dot-separated segments. Not decoration: this is the
    /// only thing keeping 900 hand-written keys from drifting into three competing conventions,
    /// and a key is the one part of a string that is not allowed to change once translators have
    /// worked against it.</summary>
    [Fact]
    public void Every_key_follows_the_naming_convention()
    {
        var malformed = Loc.Keys(Languages.En)
            .Where(k => !Regex.IsMatch(k, "^[a-z0-9]+(-[a-z0-9]+)*(\\.[a-z0-9]+(-[a-z0-9]+)*)+$"))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(malformed.Count == 0,
            $"Keys must be dot-separated lower-kebab segments:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", malformed));
    }
}
