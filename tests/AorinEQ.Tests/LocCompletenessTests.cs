using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>Every table says everything the English one says.
///
/// The untranslated-value check is the one that earns its keep. A file copied from English and
/// worked through from the top leaves the tail in English, and absolutely nothing else notices -
/// the app runs, the tests pass, and a Korean user reads half a screen of English.
///
/// It needs an allowlist because a proper noun's correct translation IS the English: AirPlay is
/// AirPlay in Korean, and "20 Hz – 10 kHz" is the same in all five. Without one, this test would
/// fail on exactly the strings it should pass, and the usual response to that is to delete the
/// test.</summary>
[Collection("Loc")]
public class LocCompletenessTests
{
    public static TheoryData<string> Translations()
    {
        var data = new TheoryData<string>();
        foreach (var language in Languages.All.Where(l => l != Languages.En)) data.Add(language);
        return data;
    }

    /// <summary>Keys whose value is deliberately the same as the English.
    ///
    /// Four groups, and every entry belongs to one of them: product names written in Latin script
    /// in all five languages; units and frequencies; glyphs and pure format strings; and each
    /// language's own name, which by definition is not translated.</summary>
    private static readonly HashSet<string> IdenticalByDesign = new(StringComparer.Ordinal)
    {
        // Product and feature names, written the same in all five.
        "feature.airplay.title", "surface.airplay.name", "tray.menu.airplay",
        "settings.style.fluent", "settings.open-configurator.label",
        "eq.advanced-preset-tools.label-5", "dialog.eqtext.ok-button.label",
        "eq.clip-indicator.label",

        // Units, sizes and frequency ranges.
        "hud.menu.1-px", "hud.menu.2-px", "hud.menu.4-px", "hud.menu.8-px",
        "hud.menu.20-hz-10-khz", "hud.menu.40-hz-16-khz",
        "settings.hud-fps.10-fps", "settings.hud-fps.20-fps",
        "settings.hud-fps.30-fps", "settings.hud-fps.60-fps",
        "designer.text.x", "designer.text.y", "settings.text.x", "settings.text.y",

        // Glyphs, dashes and strings that are only formatting.
        "designer.empty-path.text", "designer.full-path.text", "designer.muted-path.text",
        "osd.glyph.text", "osd.fluent-glyph.text",
        "eq.hz-db-q", "settings.search.result", "app.tray.tooltip.level",

        // A PowerShell command line. Translating it would break it.
        "onboarding.noprofile-executionpolicy-bypass-command-restart-service",

        // Every language names itself the same way in every table - that is what an endonym is.
        "language.en.endonym", "language.zh-hans.endonym", "language.zh-hant.endonym",
        "language.ja.endonym", "language.ko.endonym",
    };

    [Theory]
    [MemberData(nameof(Translations))]
    public void Has_every_key_the_english_table_has(string language)
    {
        var missing = Loc.Keys(Languages.En).Except(Loc.Keys(language))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            $"strings.{language}.json is missing {missing.Count} key(s) - each renders in English " +
            $"for that user:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", missing.Take(40)));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Has_no_keys_the_english_table_lacks(string language)
    {
        var extra = Loc.Keys(language).Except(Loc.Keys(Languages.En))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(extra.Count == 0,
            $"strings.{language}.json has keys English does not - dead weight, or a typo in a key " +
            $"that leaves the real one untranslated:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", extra));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Has_no_empty_values(string language)
    {
        var empty = Loc.Raw(language)
            .Where(kv => string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(empty.Count == 0,
            $"strings.{language}.json has empty values - these render as a blank control:" +
            $"{Environment.NewLine}  " + string.Join($"{Environment.NewLine}  ", empty));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Has_nothing_left_untranslated(string language)
    {
        var english = Loc.Raw(Languages.En);

        var untranslated = Loc.Raw(language)
            .Where(kv => !IdenticalByDesign.Contains(kv.Key))
            .Where(kv => english.TryGetValue(kv.Key, out var en) && en == kv.Value)
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(untranslated.Count == 0,
            $"strings.{language}.json still holds {untranslated.Count} English value(s). Translate " +
            $"them, or add the key to IdenticalByDesign if the English IS the translation:" +
            $"{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", untranslated.Take(40)));
    }

    /// <summary>The allowlist must not rot. An entry for a key that no longer exists, or one whose
    /// translation has since diverged, is a hole that would let a real untranslated string through
    /// unnoticed.</summary>
    [Fact]
    public void The_identical_by_design_list_has_no_stale_entries()
    {
        var english = Loc.Raw(Languages.En);
        var stale = new List<string>();

        foreach (var key in IdenticalByDesign)
        {
            if (!english.ContainsKey(key))
            {
                stale.Add($"{key} (no longer in the English table)");
                continue;
            }

            var anyIdentical = Languages.All
                .Where(l => l != Languages.En)
                .Any(l => Loc.Raw(l).TryGetValue(key, out var v) && v == english[key]);

            if (!anyIdentical) stale.Add($"{key} (now translated in every language - remove it)");
        }

        Assert.True(stale.Count == 0,
            $"IdenticalByDesign entries that no longer apply:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", stale));
    }
}
