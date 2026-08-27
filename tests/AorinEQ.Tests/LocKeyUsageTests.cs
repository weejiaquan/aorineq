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

    /// <summary>Loc.T("...") and the thin wrappers over it. TrayIcon.Localised registers a menu
    /// item's key so a language change can re-set its Text; it is a reference like any other.</summary>
    private static readonly Regex CodeReference =
        new(@"(?:Loc\.T|Localised)\(\s*""(?<key>[A-Za-z0-9_.\-]+)""", RegexOptions.Compiled);

    /// <summary>Prefixes reached by COMPOSING a key at runtime - Loc.T($"help.{topic}.body") - so
    /// they never appear as a literal anywhere for the regex above to find.
    ///
    /// Excluding a prefix from the orphan check would normally be a hole, so each family has a
    /// test that enumerates its real members instead: the two below are covered by
    /// Every_hud_widget_type_and_tray_action_has_a_name here, and help./feature. by
    /// HelpCatalogueTests and FeatureCatalogueTests walking their catalogues.</summary>
    private static readonly string[] ComposedPrefixes =
        ["help.", "feature.", "surface.", "hud.widget.", "tray.action."];

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

    /// <summary>The two families whose keys are composed from a persisted identifier.
    ///
    /// HudWidgetTypes.DisplayName and TrayActions.DisplayName used to be switch statements with
    /// English literals, and both carried a comment saying they existed so a member added later
    /// could not ship with its RAW PERSISTED NAME showing in the UI. Composing a string-table key
    /// keeps that promise only if the key is actually there - otherwise "eqcurve" is exactly what
    /// the user sees. This is that guarantee, enumerated rather than assumed.</summary>
    [Fact]
    public void Every_hud_widget_type_and_tray_action_has_a_name()
    {
        var table = Loc.Keys(Languages.En).ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var type in HudWidgetTypes.All)
            if (!table.Contains($"hud.widget.{type}.name"))
                missing.Add($"hud.widget.{type}.name");

        foreach (var action in TrayActions.All)
            if (!table.Contains($"tray.action.{action}.name"))
                missing.Add($"tray.action.{action}.name");

        Assert.True(missing.Count == 0,
            $"These would render as their raw persisted name in menus:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", missing));
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
